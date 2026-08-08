using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>How a record read for verification resolved (ADR-0006).</summary>
internal enum ReuseVerification
{
    /// <summary>The record decrypted and its plaintext matched its identifier.</summary>
    Verified,

    /// <summary>
    /// The record could not be reached — no resolvable location, a blob that
    /// would not open, a read that failed. Nothing is known about its
    /// content, so this is not evidence of damage.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The record was read and did not verify. This <em>is</em> evidence: the
    /// bytes another writer stored are not the bytes their identifier claims.
    /// </summary>
    Failed,
}

/// <summary>
/// One publication's targeted read path: given an object identifier, that
/// object's record, without a whole-repository blob enumeration.
/// </summary>
/// <remarks>
/// <para>
/// Two callers need it, for opposite reasons. The rename path reads a prior
/// <em>file-version manifest</em> so a file that only moved is republished
/// under its new name without its content being re-read (architecture
/// 06 §4.2). Verify-on-reuse reads a <em>segment</em> another writer stored
/// and confirms it before referencing it ([ADR-0006](../../docs/adr/0006-object-identifiers-and-dedup-trust-domains.md)).
/// </para>
/// <para>
/// The catalogue resolves the location (07 §3 precedence) and
/// <see cref="BlobReader"/> opens the one blob it names, so the cost is two
/// range reads plus one small envelope read per blob and one range read per
/// record — never a listing. Readers are cached for the run, because the
/// objects a run wants are clustered: a directory renamed wholesale puts
/// every child's prior manifest in the same few blobs, and another writer's
/// segments arrive in that writer's blobs.
/// </para>
/// <para>
/// <see cref="BlobReader.ReadRecordAsync"/> already performs 04 §6 step 7 —
/// it re-hashes the plaintext and compares the identifier the hash implies —
/// so a successful read <em>is</em> the confirmation ADR-0006 asks for. There
/// is no separate verification step to get wrong.
/// </para>
/// </remarks>
internal sealed class TargetedRecordReader(
    IObjectStore store,
    RepositoryId repositoryId,
    RepositoryKeySet keys,
    Catalogue.Catalogue catalogue) : IDisposable
{
    private readonly ObjectIdDeriver _objectIdDeriver = new(keys.ContentIdKey);
    private readonly StoreBlobKeyDeriver _storeKeyDeriver = new(keys.KeyIdKey);
    private readonly Dictionary<ObjectKey, BlobReader?> _blobs = [];

    /// <summary>
    /// The file-version manifest <paramref name="objectId"/> names, or
    /// <see langword="null"/> when it cannot be read for any reason.
    /// </summary>
    /// <remarks>
    /// Every failure yields <see langword="null"/> and nothing else. A
    /// manifest that cannot be fetched, authenticated, or decoded is not a
    /// damage finding here: the caller's fallback is to read the file, which
    /// is what it would have done anyway. Damage in a blob this publication
    /// does not depend on is <c>verify</c>'s business, and reporting it from
    /// the capture path would fail a backup over an object nobody asked for.
    /// </remarks>
    public async ValueTask<FileVersionManifest?> TryReadFileVersionAsync(
        ObjectId objectId, CancellationToken cancellationToken)
    {
        var (outcome, plaintext) = await ReadAsync(
            objectId, ObjectType.FileVersionManifest, cancellationToken).ConfigureAwait(false);

        if (outcome != RecordReadOutcome.Ok)
        {
            return null;
        }

        try
        {
            return FileVersionManifestCodec.Decode(plaintext!);
        }
        catch (Exception exception) when (exception is ManifestValidationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches, decrypts, and confirms the content identifier of
    /// <paramref name="objectId"/> — the verify-on-reuse read.
    /// </summary>
    /// <remarks>
    /// The distinction between <see cref="ReuseVerification.Unavailable"/> and
    /// <see cref="ReuseVerification.Failed"/> is what keeps a transient fetch
    /// failure from being recorded as permanent damage. Both refuse the
    /// reuse; only one is a finding.
    /// </remarks>
    public async ValueTask<ReuseVerification> VerifyAsync(ObjectId objectId, CancellationToken cancellationToken)
    {
        var (outcome, _) = await ReadAsync(
            objectId, ObjectType.SegmentRecord, cancellationToken).ConfigureAwait(false);

        return outcome switch
        {
            RecordReadOutcome.Ok => ReuseVerification.Verified,
            null => ReuseVerification.Unavailable,
            RecordReadOutcome.UnsupportedProfile => ReuseVerification.Unavailable,
            _ => ReuseVerification.Failed,
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var reader in _blobs.Values)
        {
            reader?.Dispose();
        }

        _blobs.Clear();
        _storeKeyDeriver.Dispose();
        _objectIdDeriver.Dispose();
    }

    /// <summary>
    /// Reads one record. A null outcome means the record was never reached —
    /// distinct from a record that was read and refused.
    /// </summary>
    private async ValueTask<(RecordReadOutcome? Outcome, byte[]? Plaintext)> ReadAsync(
        ObjectId objectId, ObjectType expectedType, CancellationToken cancellationToken)
    {
        if (catalogue.ResolveLocation(objectId) is not { } location)
        {
            return (null, null);
        }

        // The blob's address is derived, not looked up. A catalogue rebuilt
        // from index deltas — which is every second device's starting state —
        // has locations but no blob rows, and the store blob key is a keyed
        // function of the blob identifier (02 §4.3) while the class follows
        // from the object type (05 §2). Depending on the blob row instead
        // would make this work only on the device that wrote the data, which
        // is precisely the device that never needs it.
        var storeKey = BlobStoreKeys.ForBlob(
            expectedType == ObjectType.SegmentRecord ? BlobClass.Data : BlobClass.Metadata,
            location.StoreBlobKey ?? _storeKeyDeriver.Derive(location.BlobId));

        var reader = await OpenAsync(storeKey, cancellationToken).ConfigureAwait(false);

        if (reader is null)
        {
            return (null, null);
        }

        // The footer's table is the authority on where the record is, not the
        // catalogue's copy of it: the table is authenticated against the
        // repository and the catalogue is a cache.
        RecordTableEntry located = default;
        var found = false;
        foreach (var entry in reader.RecordTable)
        {
            if (entry.ObjectId == objectId)
            {
                located = entry;
                found = true;
                break;
            }
        }

        if (!found)
        {
            return (null, null);
        }

        // A record of the wrong type under the right identifier is not a read
        // that failed — it is a claim that does not hold, and the caller asked
        // about a specific kind of object.
        if (located.ObjectType != expectedType)
        {
            return (RecordReadOutcome.FormatViolation, null);
        }

        try
        {
            var record = await reader.ReadRecordAsync(located, cancellationToken).ConfigureAwait(false);
            return (record.Outcome, record.Plaintext);
        }
        catch (Exception exception) when (exception is BlobFormatException or IOException)
        {
            return (null, null);
        }
    }

    private async ValueTask<BlobReader?> OpenAsync(ObjectKey storeKey, CancellationToken cancellationToken)
    {
        if (_blobs.TryGetValue(storeKey, out var cached))
        {
            return cached;
        }

        BlobReader? reader;
        try
        {
            // The footer's locator sits at the end of the object, so opening
            // one needs its length. Asked of the store rather than the
            // catalogue for the same reason the key is derived: the blob row
            // may not be there.
            var metadata = await store.GetMetadataAsync(storeKey, cancellationToken).ConfigureAwait(false);

            if (!metadata.Found || metadata.Metadata!.Length <= 0)
            {
                _blobs[storeKey] = null;
                return null;
            }

            var blobLength = metadata.Metadata.Length;

            reader = await BlobReader.OpenAsync(
                store, storeKey, blobLength, repositoryId, keys.DeriveClassKey, _objectIdDeriver, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is BlobFormatException or IOException)
        {
            // Remembered as unreadable, so a directory of moved files does not
            // retry the same broken blob once per child.
            reader = null;
        }

        _blobs[storeKey] = reader;
        return reader;
    }
}
