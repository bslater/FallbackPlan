using Bodu;
using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Repository.Format.Keys;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Format.RecoveryKit;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Recovery;

/// <summary>One listed snapshot, as the recovery tool sees it.</summary>
public sealed record RecoveredSnapshot(SnapshotManifest Manifest, bool SignatureVerified);

/// <summary>What one tree restore did.</summary>
public sealed record RecoveryRestoreReport(int Restored, int Failed, int Skipped, IReadOnlyList<string> Notes);

/// <summary>
/// The last line of defence (architecture 08 §5; FR-KIT-006): opens a
/// repository from a recovery kit and a passphrase — no state directory,
/// no catalogue, no engine — and restores from recovery footers alone.
/// The kit supplies the KDF parameters and the verbatim wrapped key
/// object; the passphrase is the second factor; everything else is read
/// from the store.
/// </summary>
public sealed class RecoverySession : IDisposable
{
    private readonly IObjectStore _store;
    private readonly KeyHierarchy _hierarchy;
    private readonly ObjectIdDeriver _objectIdDeriver;
    private readonly RepositoryReadAuthority? _authority;
    private readonly Func<BlobEnvelope, byte[]>? _sealedContentKeyOpener;
    private readonly List<BlobReader> _readers = [];
    private readonly Dictionary<ObjectId, (BlobReader Reader, RecordTableEntry Entry)> _records = [];

    private RecoverySession(
        IObjectStore store, RepositoryId repositoryId, KeyHierarchy hierarchy, RepositoryReadAuthority? authority = null)
    {
        _store = store;
        RepositoryId = repositoryId;
        _hierarchy = hierarchy;
        _objectIdDeriver = new ObjectIdDeriver(hierarchy.DeriveContentIdKey());
        _authority = authority;

        if (authority is not null)
        {
            _sealedContentKeyOpener = envelope => SealedContentKey.Open(
                authority.SealingPrivateKey, envelope.SealedContentKey, RepositoryId, envelope.BlobId);
        }
    }

    /// <summary>The repository identity the kit names.</summary>
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
    /// Opens a session, reading the repository's own identity from the
    /// archive when the kit does not carry one (specifications/recovery-kit
    /// §2.2, §6; FR-KIT-006).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <b>installation kit</b> names no repository, because one opens
    /// every archive its passphrase wrote. The repository id is nonetheless
    /// load-bearing — it is associated data for every record and every
    /// sealed blob — so it comes from the descriptor of whichever archive
    /// the operator pointed at. The kit supplies the keys; the archive
    /// supplies its name.
    /// </para>
    /// <para>
    /// The derivation is proved <b>twice</b>, and the two failures are
    /// different questions with different answers. Against the kit's copy:
    /// this is not the passphrase that made this kit. Against the
    /// descriptor's: this kit belongs to a different installation than this
    /// archive. Collapsing them into one message would leave somebody
    /// retyping a passphrase that was right all along.
    /// </para>
    /// <para>
    /// A <b>repository kit</b> is delegated to <see cref="Open"/> unchanged,
    /// so one call site serves both and neither has to ask which it holds.
    /// </para>
    /// </remarks>
    /// <param name="kit">The kit, of either format.</param>
    /// <param name="passphrase">The repository passphrase.</param>
    /// <param name="store">The archive to open.</param>
    /// <param name="cancellationToken">Abandons the descriptor read.</param>
    /// <returns>The opened session.</returns>
    /// <exception cref="KeyUnwrapFailedException">The passphrase does not reproduce the kit's keys.</exception>
    /// <exception cref="RecoveryKitFormatException">The archive has no readable descriptor, or belongs to another installation.</exception>
    public static async ValueTask<RecoverySession> OpenAsync(
        RecoveryKit kit, Passphrase passphrase, IObjectStore store, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(kit);
        ThrowHelper.ThrowIfNull(passphrase);
        ThrowHelper.ThrowIfNull(store);

        if (!kit.IsInstallationKit)
        {
            return Open(kit, passphrase, store);
        }

        var descriptor = await ReadDescriptorAsync(store, cancellationToken).ConfigureAwait(false);

        var authority = WriteOnlyDerivation.Derive(
            passphrase,
            new Argon2Parameters
            {
                MemoryKiB = kit.KdfMemoryKiB,
                Iterations = kit.KdfIterations,
                Parallelism = kit.KdfParallelism,
            },
            kit.KdfSalt.Span,
            KdfValidationMode.OpenRepository);

        try
        {
            if (!authority.Credential.SealingPublicKey.SequenceEqual(kit.SealingPublicKey.Span))
            {
                throw new KeyUnwrapFailedException(Resources.Strings.RecoverySession_PassphraseDoesNotReproduce);
            }

            if (!authority.Credential.SealingPublicKey.SequenceEqual(descriptor.SealingPublicKey.Span))
            {
                throw new RecoveryKitFormatException(
                    Resources.Strings.RecoverySession_KitBelongsToAnotherInstallation);
            }

            return new RecoverySession(
                store, descriptor.RepositoryId, KeyHierarchy.ForWriteOnly(authority.Credential), authority);
        }
        catch
        {
            authority.Dispose();
            throw;
        }
    }

    /// <summary>Reads and parses the archive's descriptor.</summary>
    private static async ValueTask<RepositoryDescriptor> ReadDescriptorAsync(
        IObjectStore store, CancellationToken cancellationToken)
    {
        using var read = await store.OpenReadAsync(DescriptorKey, range: null, cancellationToken)
            .ConfigureAwait(false);

        if (read.Outcome != OpenReadOutcome.Found)
        {
            throw new RecoveryKitFormatException(Resources.Strings.RecoverySession_ArchiveHasNoDescriptor);
        }

        using var memory = new MemoryStream();
        await read.Content!.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);

        // Every non-Ok outcome is the same answer here — this is not an
        // archive this kit can open — but the tool still says which, because
        // "not a repository" and "digest does not verify" send an operator
        // to completely different places.
        return RepositoryDescriptorCodec.Parse(memory.ToArray()) switch
        {
            DescriptorParseResult.Ok ok => ok.Descriptor,
            DescriptorParseResult.NotARepository =>
                throw new RecoveryKitFormatException(Resources.Strings.RecoverySession_ArchiveHasNoDescriptor),
            DescriptorParseResult.IntegrityFailure =>
                throw new RecoveryKitFormatException(Resources.Strings.RecoverySession_ArchiveDescriptorDoesNotRead),
            DescriptorParseResult.FormatViolation violation =>
                throw new RecoveryKitFormatException(violation.Message),
            var other =>
                throw new RecoveryKitFormatException(
                    Resources.Strings.RecoverySession_ArchiveDescriptorDoesNotRead + " (" + other.GetType().Name + ")"),
        };
    }

    /// <summary>
    /// Opens a session: KEK from the kit's KDF parameters and the
    /// passphrase, master key from the kit's verbatim key object.
    /// </summary>
    /// <exception cref="KeyUnwrapFailedException">The passphrase does not open this kit's key object.</exception>
    public static RecoverySession Open(RecoveryKit kit, Passphrase passphrase, IObjectStore store)
    {
        ThrowHelper.ThrowIfNull(kit);
        ThrowHelper.ThrowIfNull(passphrase);
        ThrowHelper.ThrowIfNull(store);

        if (kit.IsInstallationKit)
        {
            // An installation kit names no repository, and the repository id
            // is AAD for every record and every sealed blob — so it has to
            // come from the archive's own descriptor, which this synchronous
            // overload cannot read. OpenAsync is that path.
            throw new RecoveryKitFormatException(Resources.Strings.RecoverySession_InstallationKitNeedsTheArchive);
        }

        var parameters = new Argon2Parameters
        {
            MemoryKiB = kit.KdfMemoryKiB,
            Iterations = kit.KdfIterations,
            Parallelism = kit.KdfParallelism,
        };

        // A write-only (format v2) kit carries no key object at all — the
        // passphrase and the kit's public salt and parameters re-derive
        // everything, proven by comparing the derived public key against the
        // kit's copy (ADR-0042 §8). The session keeps the authority: its
        // scalar is what opens each sealed blob's content key.
        if (kit.RepositoryFormatVersion >= 2)
        {
            var authority = WriteOnlyDerivation.Derive(
                passphrase, parameters, kit.KdfSalt.Span, KdfValidationMode.OpenRepository);

            if (!authority.Credential.SealingPublicKey.SequenceEqual(kit.SealingPublicKey.Span))
            {
                authority.Dispose();
                throw new KeyUnwrapFailedException(Resources.Strings.RecoverySession_PassphraseDoesNotReproduce);
            }

            try
            {
                return new RecoverySession(
                    store, kit.RepositoryId!.Value, KeyHierarchy.ForWriteOnly(authority.Credential), authority);
            }
            catch
            {
                authority.Dispose();
                throw;
            }
        }

        using var derivation = KekDerivation.Derive(
            passphrase, parameters, kit.KdfSalt.Span, KdfValidationMode.OpenRepository);

        var keyObject = KeyObjectFraming.Parse(kit.KeyObject.Span);
        var aad = KeyObjectFraming.BuildAad(keyObject.FormatVersion, keyObject.KekProfile, keyObject.KeyId);
        var bundleCbor = KeyWrapping.Unwrap(
            derivation.Kek, keyObject.WrapNonce, aad, keyObject.Wrapped, keyObject.Tag);

        try
        {
            using var bundle = KeyBundleCodec.Decode(bundleCbor);
            return new RecoverySession(store, kit.RepositoryId!.Value, new KeyHierarchy(bundle.MasterKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bundleCbor);
        }
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

    /// <summary>Lists every discoverable snapshot with its signature verdict.</summary>
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
            var metadataKey = _hierarchy.DeriveMetadataKey(record.KeyGeneration);
            try
            {
                if (!StandaloneRecordCipher.TryOpen(record, RepositoryId, metadataKey, out var plaintext))
                {
                    continue;
                }

                var decoded = SnapshotManifestCodec.Decode(plaintext);
                bool verified;
                using (var signer = RepositorySigner.Create(
                    _hierarchy, new KeyGeneration((uint)decoded.Manifest.PublicationGeneration)))
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
        _hierarchy.Dispose();
        _authority?.Dispose();
    }

    private byte[] DeriveClassKey(BlobClass blobClass, KeyGeneration generation) =>
        blobClass == BlobClass.Data ? _hierarchy.DeriveDataKey(generation) : _hierarchy.DeriveMetadataKey(generation);
}
