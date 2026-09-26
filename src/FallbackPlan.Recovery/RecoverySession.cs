using Bodu;
using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Repository.Format.Lifecycle;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Recovery;

/// <summary>One listed snapshot, as the recovery tool sees it.</summary>
public sealed record RecoveredSnapshot(SnapshotManifest Manifest, bool SignatureVerified);

/// <summary>What one tree restore did.</summary>
public sealed record RecoveryRestoreReport(int Restored, int Failed, int Skipped, IReadOnlyList<string> Notes);

/// <summary>
/// The last line of defence (architecture 08 §5; FR-DRL-001; ADR-0060):
/// opens a repository from the passphrase and the archive alone — no state
/// directory, no catalogue, no engine, no kit — and restores from recovery
/// footers alone. The archive's descriptor supplies the KDF salt and
/// parameters and the sealing public key that proves the passphrase;
/// everything else is read from the store.
/// </summary>
public sealed class RecoverySession : IDisposable
{
    private readonly IObjectStore _store;
    private readonly RepositoryWriteCredential _credential;
    private readonly ObjectIdDeriver _objectIdDeriver;
    private readonly RepositoryReadAuthority? _authority;
    private readonly SealedContentKeyOpener? _sealedContentKeyOpener;
    private readonly List<BlobReader> _readers = [];
    private readonly Dictionary<ObjectId, (BlobReader Reader, RecordTableEntry Entry)> _records = [];

    private RecoverySession(
        IObjectStore store, RepositoryId repositoryId, RepositoryWriteCredential credential, RepositoryReadAuthority? authority = null)
    {
        _store = store;
        RepositoryId = repositoryId;
        _credential = credential;
        _objectIdDeriver = new ObjectIdDeriver(credential.ContentIdKey.ToArray());
        _authority = authority;

        if (authority is not null)
        {
            _sealedContentKeyOpener = new SealedContentKeyOpener(authority.SealingPrivateKey, RepositoryId);
        }
    }

    /// <summary>The repository identity the archive's descriptor names.</summary>
    public RepositoryId RepositoryId { get; }

    /// <summary>
    /// The descriptor object every repository publishes at its root
    /// (repository-format 01 §6).
    /// </summary>
    /// <remarks>
    /// Named here rather than taken from <c>RepositoryLifecycle</c>: the
    /// recovery tool's dependency closure is deliberately
    /// format/crypto/packing/storage only, so it reads the descriptor with
    /// the format codec and does not reach the engine to learn a filename.
    /// </remarks>
    private static readonly ObjectKey DescriptorKey = ObjectKey.Parse("repository-format");

    /// <summary>
    /// Opens a session from the passphrase and the archive
    /// ([ADR-0060](../../docs/adr/0060-the-passphrase-is-the-recovery-credential.md);
    /// FR-DRL-001).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything the derivation needs is in the archive's own descriptor,
    /// which is unencrypted by design: the KDF salt and parameters, the
    /// sealing public key that proves the passphrase reproduced the root,
    /// and the repository id — load-bearing, because it is associated data
    /// for every record and every sealed blob. So one passphrase opens every
    /// archive it wrote, from wherever that archive is now.
    /// </para>
    /// <para>
    /// There are exactly two refusals. The passphrase does not reproduce
    /// this archive's sealing public key — a wrong passphrase, or another
    /// installation's archive, deliberately indistinguishable
    /// (<see cref="KeyUnwrapFailedException"/>). Or the path does not hold an
    /// archive at all, which is a different thing to tell a person than
    /// "wrong passphrase" (<see cref="RecoveryFailureException"/>).
    /// </para>
    /// </remarks>
    /// <param name="passphrase">The installation's passphrase.</param>
    /// <param name="store">The archive to open.</param>
    /// <param name="cancellationToken">Abandons the descriptor read.</param>
    /// <returns>The opened session.</returns>
    /// <exception cref="KeyUnwrapFailedException">The passphrase does not reproduce this archive's keys.</exception>
    /// <exception cref="RecoveryFailureException">The store has no readable descriptor.</exception>
    public static async ValueTask<RecoverySession> OpenAsync(
        Passphrase passphrase, IObjectStore store, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(passphrase);
        ThrowHelper.ThrowIfNull(store);

        var descriptor = await ReadDescriptorAsync(store, cancellationToken).ConfigureAwait(false);

        if (!WriteOnlyDerivation.TryDeriveVerified(
            passphrase, descriptor.KdfParameters, descriptor.KdfSalt.Span, descriptor.SealingPublicKey.Span,
            out var authority))
        {
            throw new KeyUnwrapFailedException(Resources.Strings.RecoverySession_PassphraseDoesNotReproduce);
        }

        try
        {
            return new RecoverySession(store, descriptor.RepositoryId, authority!.Credential.Clone(), authority)
            {
                FormatVersion = descriptor.FormatVersion,
                EffectiveFormatVersion = await ReadEffectiveFormatAsync(
                    store, descriptor, authority.Credential, cancellationToken).ConfigureAwait(false),
            };
        }
        catch
        {
            authority!.Dispose();
            throw;
        }
    }

    /// <summary>The archive's format version, from its descriptor — what it was <em>created</em> at.</summary>
    public int FormatVersion { get; private init; }

    /// <summary>
    /// The format version the archive's owner writes now (specification
    /// 11 §5.1): the descriptor's, or higher when a signed upgrade record
    /// says so. It changes nothing about how this tool reads — every blob
    /// declares its own container in its envelope — but it is what a person
    /// staring at a damaged archive needs told, and a line that said 2 over
    /// format-3 blobs would be read at the worst possible moment.
    /// </summary>
    public int EffectiveFormatVersion { get; private init; }

    /// <summary>
    /// Reads the upgrade records beside the descriptor and answers the
    /// effective version. The listing is here and the decision is in
    /// <see cref="FormatUpgradeRecordCodec.EffectiveVersion"/>, because this
    /// tool's dependency closure deliberately stops short of the engine and
    /// what must not differ between the two is which records count.
    /// </summary>
    private static async ValueTask<ushort> ReadEffectiveFormatAsync(
        IObjectStore store,
        RepositoryDescriptor descriptor,
        RepositoryWriteCredential credential,
        CancellationToken cancellationToken)
    {
        var records = new List<ReadOnlyMemory<byte>>();

        await foreach (var entry in store
            .ListAsync(ObjectPrefix.Parse(FormatUpgradeRecordCodec.KeyPrefix), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            using var read = await store.OpenReadAsync(entry.Key, range: null, cancellationToken)
                .ConfigureAwait(false);
            if (read.Outcome != OpenReadOutcome.Found)
            {
                continue;
            }

            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            records.Add(memory.ToArray());
        }

        if (records.Count == 0)
        {
            return descriptor.FormatVersion;
        }

        using var signer = RepositorySigner.Create(credential, KeyGeneration.Zero);
        return FormatUpgradeRecordCodec.EffectiveVersion(
            descriptor.FormatVersion,
            records,
            decoded => signer.Verify(decoded.SignedBytes.Span, decoded.Signature.Span));
    }

    /// <summary>Reads and parses the archive's descriptor.</summary>
    private static async ValueTask<RepositoryDescriptor> ReadDescriptorAsync(
        IObjectStore store, CancellationToken cancellationToken)
    {
        using var read = await store.OpenReadAsync(DescriptorKey, range: null, cancellationToken)
            .ConfigureAwait(false);

        if (read.Outcome != OpenReadOutcome.Found)
        {
            throw new RecoveryFailureException(Resources.Strings.RecoverySession_ArchiveHasNoDescriptor);
        }

        using var memory = new MemoryStream();
        await read.Content!.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);

        // Every non-Ok outcome is the same answer here — this is not an
        // archive this passphrase can open — but the tool still says which,
        // because "not a repository" and "digest does not verify" send an
        // operator to completely different places.
        return RepositoryDescriptorCodec.Parse(memory.ToArray()) switch
        {
            DescriptorParseResult.Ok ok => ok.Descriptor,
            DescriptorParseResult.NotARepository =>
                throw new RecoveryFailureException(Resources.Strings.RecoverySession_ArchiveHasNoDescriptor),
            DescriptorParseResult.IntegrityFailure =>
                throw new RecoveryFailureException(Resources.Strings.RecoverySession_ArchiveDescriptorDoesNotRead),
            DescriptorParseResult.FormatViolation violation =>
                throw new RecoveryFailureException(violation.Message),
            // Named, because this is the one place a person reads the answer
            // under pressure: a newer format's feature is a reason to update
            // the tool, and "does not read" would send them to check the disk.
            DescriptorParseResult.UnsupportedRequiredFeatures unsupported =>
                throw new RecoveryFailureException(
                    "The archive requires features this recovery tool does not implement: "
                    + string.Join(", ", unsupported.Features.Select(feature => $"0x{feature:x4}"))
                    + " — update the tool; the archive is not damaged (specification 01 §3.2)."),
            var other =>
                throw new RecoveryFailureException(
                    Resources.Strings.RecoverySession_ArchiveDescriptorDoesNotRead + " (" + other.GetType().Name + ")"),
        };
    }

    /// <summary>
    /// Opens every blob through its recovery footer and indexes records by
    /// object identifier — the index-free read path (specification 05 §4).
    /// A damaged blob is skipped with a note; corruption is local (04 §7).
    /// </summary>
    public async ValueTask<(int Blobs, IReadOnlyList<string> Notes)> LoadBlobsAsync(CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var blobs = 0;

        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            BlobReader reader;
            try
            {
                reader = await BlobReader.OpenAsync(
                    _store, entry.Key, entry.Length, RepositoryId, DeriveClassKey, _objectIdDeriver, cancellationToken,
                    _sealedContentKeyOpener)
                    .ConfigureAwait(false);
            }
            catch (BlobFormatException exception)
            {
                notes.Add($"blob '{entry.Key.Value}' skipped: {exception.Message}");
                continue;
            }

            blobs++;
            _readers.Add(reader);
            foreach (var record in reader.RecordTable)
            {
                _records.TryAdd(record.ObjectId, (reader, record));
            }
        }

        return (blobs, notes);
    }

    /// <summary>
    /// Lists every discoverable snapshot with its signature verdict, newest
    /// first by its own capture time and then by identifier.
    /// </summary>
    /// <remarks>
    /// Sorted here rather than taken from the store: a store lists keys in
    /// ordinal order, and a snapshot's key is its device id and snapshot id,
    /// both random. The first line of this listing is what a person reads
    /// under pressure and what the operator drill restores by machine, so it
    /// has to be the newest by contract and not by the toss of two
    /// identifiers.
    /// </remarks>
    public async ValueTask<IReadOnlyList<RecoveredSnapshot>> ListSnapshotsAsync(CancellationToken cancellationToken)
    {
        var snapshots = new List<RecoveredSnapshot>();

        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("snapshots/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            byte[] bytes;
            using (var read = await _store.OpenReadAsync(entry.Key, range: null, cancellationToken).ConfigureAwait(false))
            {
                if (read.Outcome != OpenReadOutcome.Found)
                {
                    continue;
                }

                using var memory = new MemoryStream();
                await read.Content!.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                bytes = memory.ToArray();
            }

            var record = StandaloneRecordFraming.Parse(bytes);
            var metadataKey = _credential.DeriveMetadataKey(record.KeyGeneration);
            try
            {
                if (!StandaloneRecordCipher.TryOpen(record, RepositoryId, metadataKey, out var plaintext))
                {
                    continue;
                }

                var decoded = SnapshotManifestCodec.Decode(plaintext);
                bool verified;
                using (var signer = RepositorySigner.Create(
                    _credential, new KeyGeneration((uint)decoded.Manifest.PublicationGeneration)))
                {
                    verified = signer.Verify(decoded.SignedBytes.Span, decoded.Signature.Span);
                }

                snapshots.Add(new RecoveredSnapshot(decoded.Manifest, verified));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(metadataKey);
            }
        }

        snapshots.Sort(static (left, right) =>
        {
            var byTime = right.Manifest.CaptureCompletedAt.CompareTo(left.Manifest.CaptureCompletedAt);
            return byTime != 0
                ? byTime
                : left.Manifest.SnapshotId.Span.SequenceCompareTo(right.Manifest.SnapshotId.Span);
        });
        return snapshots;
    }

    /// <summary>
    /// Restores a snapshot's tree under <paramref name="outputDirectory"/>.
    /// Every file spools privately, verifies each segment's implied content
    /// identifier and the whole-file hash — sparse zeroes included — and
    /// reaches its destination only when everything verified (FR-RST-005).
    /// Symlinks and specials are counted as skipped with a note, never
    /// silently dropped.
    /// </summary>
    public async ValueTask<RecoveryRestoreReport> RestoreTreeAsync(
        ObjectId rootTree, string outputDirectory, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var notes = new List<string>();
        var restored = 0;
        var failed = 0;
        var skipped = 0;

        await WalkAsync(rootTree, string.Empty).ConfigureAwait(false);
        return new RecoveryRestoreReport(restored, failed, skipped, notes);

        async ValueTask WalkAsync(ObjectId treeId, string prefix)
        {
            ObjectId? next = treeId;
            while (next is { } id)
            {
                var treeBytes = await ReadRecordAsync(
                    id, prefix.Length == 0 ? "<root tree>" : prefix, notes, cancellationToken).ConfigureAwait(false);
                if (treeBytes is null)
                {
                    failed++;
                    return;
                }

                var tree = TreeManifestCodec.Decode(treeBytes);
                foreach (var entry in tree.Entries)
                {
                    var name = Encoding.UTF8.GetString(entry.Name.Span);
                    var path = prefix.Length == 0 ? name : prefix + "/" + name;

                    // A tree entry's name is repository text, and this session
                    // exists precisely for repositories in the worst state to
                    // trust: a name that is not a plain component — '..', a
                    // separator, a rooted form — would let Path.Combine below
                    // write outside the chosen output. Refused per entry, with
                    // the subtree it would have carried; the drill continues.
                    if (!IsPlainName(name, out var why))
                    {
                        failed++;
                        notes.Add($"FAILED {path}: refused — {why}");
                        continue;
                    }

                    if (entry.EntryKind == EntryKind.DirectoryPlaceholder)
                    {
                        Directory.CreateDirectory(
                            Path.Combine(outputDirectory, path.Replace('/', Path.DirectorySeparatorChar)));
                        await WalkAsync(entry.ObjectId, path).ConfigureAwait(false);
                        continue;
                    }

                    var manifestBytes = await ReadRecordAsync(entry.ObjectId, path, notes, cancellationToken)
                        .ConfigureAwait(false);
                    if (manifestBytes is null)
                    {
                        failed++;
                        continue;
                    }

                    var manifest = FileVersionManifestCodec.Decode(manifestBytes);

                    if (manifest.EntryKind != EntryKind.File)
                    {
                        skipped++;
                        notes.Add($"skipped {path}: {manifest.EntryKind} materialisation is the full client's job");
                        continue;
                    }

                    if (await RestoreFileAsync(manifest, path).ConfigureAwait(false))
                    {
                        restored++;
                    }
                    else
                    {
                        failed++;
                    }
                }

                next = tree.Continuation;
            }
        }

        async ValueTask<bool> RestoreFileAsync(FileVersionManifest manifest, string path)
        {
            var destination = Path.Combine(outputDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var spool = destination + ".fbp-recover-tmp";

            try
            {
                using var wholeFile = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                var pieces = manifest.SegmentReferences
                    .Select(reference => (Offset: (ulong)reference.LogicalOffset,
                        Reference: (SegmentReference?)reference, Extent: (SparseExtent?)null))
                    .Concat(manifest.SparseExtents.Select(extent =>
                        (extent.Offset, (SegmentReference?)null, (SparseExtent?)extent)))
                    .OrderBy(piece => piece.Item1);

                var output = File.Create(spool);
                await using (output.ConfigureAwait(false))
                {
                    foreach (var (_, reference, extent) in pieces)
                    {
                        if (reference is { } segment)
                        {
                            if (!_records.TryGetValue(segment.ObjectId, out var located))
                            {
                                notes.Add($"FAILED {path}: segment {segment.ObjectId} is in no readable blob");
                                return false;
                            }

                            var read = await located.Reader.ReadRecordAsync(located.Entry, cancellationToken)
                                .ConfigureAwait(false);
                            if (read.Outcome != RecordReadOutcome.Ok)
                            {
                                notes.Add($"FAILED {path}: segment read {read.Outcome} — {read.Detail}");
                                return false;
                            }

                            await output.WriteAsync(read.Plaintext!, cancellationToken).ConfigureAwait(false);
                            wholeFile.AppendData(read.Plaintext!);
                        }
                        else if (extent is { } hole)
                        {
                            // A hole materialises as zeroes; the hash covers
                            // the materialised form (06 §4.2).
                            var zeroes = new byte[Math.Min(hole.Length, 64 * 1024)];
                            var remaining = (long)hole.Length;
                            while (remaining > 0)
                            {
                                var block = (int)Math.Min(remaining, zeroes.Length);
                                await output.WriteAsync(zeroes.AsMemory(0, block), cancellationToken).ConfigureAwait(false);
                                wholeFile.AppendData(zeroes.AsSpan(0, block));
                                remaining -= block;
                            }
                        }
                    }
                }

                Span<byte> hash = stackalloc byte[32];
                wholeFile.GetHashAndReset(hash);
                if (!hash.SequenceEqual(manifest.WholeFileHash.Span))
                {
                    notes.Add($"FAILED {path}: the whole-file hash does not verify (FR-RST-002)");
                    return false;
                }

                File.Move(spool, destination, overwrite: true);
                return true;
            }
            finally
            {
                if (File.Exists(spool))
                {
                    File.Delete(spool);
                }
            }
        }
    }

    /// <summary>
    /// Whether a tree-entry name is a single plain path component — the same
    /// rule the full client's executor enforces, because both write
    /// repository-supplied names under a chosen root.
    /// </summary>
    private static bool IsPlainName(string name, out string why)
    {
        why = string.Empty;

        if (name.Length == 0)
        {
            why = "the tree names an empty component";
            return false;
        }

        if (name is "." or "..")
        {
            why = $"the tree names a '{name}' component";
            return false;
        }

        if (name.Contains('\0', StringComparison.Ordinal))
        {
            why = "the tree name contains a NUL";
            return false;
        }

        if (name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains(':', StringComparison.Ordinal))
        {
            why = "the tree name contains a separator or drive marker";
            return false;
        }

        if (Path.IsPathRooted(name))
        {
            why = "the tree name is rooted";
            return false;
        }

        return true;
    }

    private async ValueTask<byte[]?> ReadRecordAsync(
        ObjectId objectId, string path, List<string> notes, CancellationToken cancellationToken)
    {
        if (!_records.TryGetValue(objectId, out var located))
        {
            notes.Add($"FAILED {path}: metadata record {objectId} is in no readable blob");
            return null;
        }

        var read = await located.Reader.ReadRecordAsync(located.Entry, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != RecordReadOutcome.Ok)
        {
            notes.Add($"FAILED {path}: record read {read.Outcome} — {read.Detail}");
            return null;
        }

        return read.Plaintext;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var reader in _readers)
        {
            reader.Dispose();
        }

        _objectIdDeriver.Dispose();
        _credential.Dispose();
        _sealedContentKeyOpener?.Dispose();
        _authority?.Dispose();
    }

    // The reader asks for a blob's STRUCTURE class, which is the metadata
    // plane for every blob — a sealed data blob's footer derives from the
    // metadata key too (ADR-0042 §2). There is no data key to hand out.
    private byte[] DeriveClassKey(BlobClass blobClass, KeyGeneration generation) =>
        blobClass == BlobClass.Metadata
            ? _credential.DeriveMetadataKey(generation)
            : throw new InvalidOperationException("A repository holds no data class key (specification 03 §9.2).");
}
