using Bodu;
using System.Globalization;
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
/// <param name="Restored">The files restored.</param>
/// <param name="Failed">The entries that failed.</param>
/// <param name="Skipped">The entries of a kind only the full client makes.</param>
/// <param name="Notes">What the run could not do, entry by entry.</param>
/// <param name="NotApplied">
/// Each captured attribute that some restored files and folders did not get
/// back, with how many, in the receipt's words and its order (ADR-0150).
/// </param>
public sealed record RecoveryRestoreReport(
    int Restored, int Failed, int Skipped, IReadOnlyList<RecoveryNote> Notes, IReadOnlyList<RecoveryNotApplied> NotApplied)
{
    /// <summary>
    /// What <see cref="NotApplied"/> says, as the operator's terminal shows
    /// it, or null when every restored file and folder got back all that was
    /// captured with it.
    /// </summary>
    public string? DescribeNotApplied() => NotApplied.Count == 0
        ? null
        : "metadata not applied: " + string.Join(", ", NotApplied.Select(entry =>
            string.Create(CultureInfo.InvariantCulture, $"{entry.Attribute} to {entry.Items} item(s)")));
}

/// <summary>One captured attribute some restored items did not get back.</summary>
/// <param name="Attribute">The attribute, by the receipt's name for it.</param>
/// <param name="Items">How many restored files and folders did not get it back.</param>
public sealed record RecoveryNotApplied(string Attribute, int Items);

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
    public static ValueTask<RecoverySession> OpenAsync(
        Passphrase passphrase, IObjectStore store, CancellationToken cancellationToken) =>
        OpenAsync(passphrase, store, run: null, cancellationToken);

    /// <summary>
    /// Opens a session, recording into <paramref name="run"/> what the
    /// descriptor said and whether the passphrase reproduced the keys — before
    /// either refusal is thrown, because a run that stops there is the one its
    /// diagnostic bundle most needs to describe (ADR-0082).
    /// </summary>
    internal static async ValueTask<RecoverySession> OpenAsync(
        Passphrase passphrase, IObjectStore store, RecoveryRun? run, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(passphrase);
        ThrowHelper.ThrowIfNull(store);

        if (run is not null)
        {
            run.Stage = RecoveryStage.Descriptor;
        }

        var descriptor = await ReadDescriptorAsync(store, run, cancellationToken).ConfigureAwait(false);

        if (run is not null)
        {
            run.Stage = RecoveryStage.Passphrase;
        }

        if (!WriteOnlyDerivation.TryDeriveVerified(
            passphrase, descriptor.KdfParameters, descriptor.KdfSalt.Span, descriptor.SealingPublicKey.Span,
            out var authority))
        {
            if (run is not null)
            {
                run.Passphrase = PassphraseOutcome.Refused;
            }

            throw new KeyUnwrapFailedException(Resources.Strings.RecoverySession_PassphraseDoesNotReproduce);
        }

        if (run is not null)
        {
            run.Passphrase = PassphraseOutcome.Reproduced;
        }

        try
        {
            var session = new RecoverySession(store, descriptor.RepositoryId, authority!.Credential.Clone(), authority)
            {
                FormatVersion = descriptor.FormatVersion,
                EffectiveFormatVersion = await ReadEffectiveFormatAsync(
                    store, descriptor, authority.Credential, cancellationToken).ConfigureAwait(false),
            };
            if (run is not null)
            {
                run.EffectiveFormatVersion = session.EffectiveFormatVersion;
            }

            return session;
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

    /// <summary>Reads and parses the archive's descriptor, recording what it said into <paramref name="run"/>.</summary>
    private static async ValueTask<RepositoryDescriptor> ReadDescriptorAsync(
        IObjectStore store, RecoveryRun? run, CancellationToken cancellationToken)
    {
        using var read = await store.OpenReadAsync(DescriptorKey, range: null, cancellationToken)
            .ConfigureAwait(false);

        if (read.Outcome != OpenReadOutcome.Found)
        {
            if (run is not null)
            {
                run.Descriptor = DescriptorOutcome.Missing;
            }

            throw new RecoveryFailureException(Resources.Strings.RecoverySession_ArchiveHasNoDescriptor);
        }

        using var memory = new MemoryStream();
        await read.Content!.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);

        // Every non-Ok outcome is the same answer here — this is not an
        // archive this passphrase can open — but the tool still says which,
        // because "not a repository" and "digest does not verify" send an
        // operator to completely different places.
        var parsed = RepositoryDescriptorCodec.Parse(memory.ToArray());
        if (run is not null)
        {
            switch (parsed)
            {
                case DescriptorParseResult.Ok ok:
                    run.Read(ok.Descriptor);
                    break;
                case DescriptorParseResult.UnsupportedRequiredFeatures unsupported:
                    run.Descriptor = DescriptorOutcome.UnsupportedFeatures;
                    run.UnsupportedFeatures = unsupported.Features;
                    break;
                default:
                    run.Descriptor = parsed switch
                    {
                        DescriptorParseResult.NotARepository => DescriptorOutcome.NotARepository,
                        DescriptorParseResult.IntegrityFailure => DescriptorOutcome.IntegrityFailure,
                        _ => DescriptorOutcome.FormatViolation,
                    };
                    break;
            }
        }

        return parsed switch
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
    public async ValueTask<(int Blobs, IReadOnlyList<RecoveryNote> Notes)> LoadBlobsAsync(CancellationToken cancellationToken)
    {
        var notes = new List<RecoveryNote>();
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
                notes.Add(new RecoveryNote(RecoveryNoteKind.BlobSkipped, Blob: entry.Key, Detail: exception.Message));
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
    /// A symlink is made a link where links can be made without a privilege
    /// (ADR-0155); elsewhere it, and every special, is counted as skipped
    /// with a note, never silently dropped. A write the output refuses at
    /// an entry's name fails that entry alone, with a note.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is restored is given back the metadata captured with it, through
    /// the executor's writer and by its rule (ADR-0150): for this platform,
    /// as the account the tool runs as, and with no claim to have captured
    /// it, since the tool has no installation to compare the snapshot's
    /// device with. A file's goes on once its content has landed. A folder's
    /// goes on once nothing more will land in it, deepest folder first, and
    /// only onto a folder the tool made that still stands where it made it
    /// (ADR-0086). A run that stops part way settles the folders it made.
    /// </para>
    /// <para>
    /// The output folder is the person's choice and keeps its own, as a
    /// folder already in the output does. A write the target refuses leaves
    /// that attribute not applied and never fails the entry; the report
    /// counts each attribute not applied.
    /// </para>
    /// </remarks>
    public ValueTask<RecoveryRestoreReport> RestoreTreeAsync(
        ObjectId rootTree, string outputDirectory, CancellationToken cancellationToken) =>
        RestoreTreeAsync(rootTree, outputDirectory, makeLink: null, cancellationToken);

    /// <summary>
    /// Restores a snapshot's tree under <paramref name="outputDirectory"/>,
    /// as <see cref="RestoreTreeAsync(ObjectId, string, CancellationToken)"/>
    /// does, giving a hard-link group's later names through
    /// <paramref name="makeLink"/>.
    /// </summary>
    /// <param name="rootTree">The snapshot's root tree.</param>
    /// <param name="outputDirectory">Where the tree is restored.</param>
    /// <param name="makeLink">
    /// Gives an existing file a further name, and says whether it did:
    /// <see cref="HardLinks.TryCreate"/> where null. A test stands in for an
    /// output that keeps no links.
    /// </param>
    /// <param name="cancellationToken">Stops the run.</param>
    public async ValueTask<RecoveryRestoreReport> RestoreTreeAsync(
        ObjectId rootTree, string outputDirectory, Func<string, string, bool>? makeLink, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(outputDirectory);
        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);

        var notes = new List<RecoveryNote>();
        var restored = 0;
        var failed = 0;
        var skipped = 0;
        var writer = new MetadataWriter(MetadataTarget.ForLocalPlatform());
        var folders = new List<Folder>();
        var notApplied = new Dictionary<string, int>(StringComparer.Ordinal);
        var link = makeLink ?? HardLinks.TryCreate;

        // Each hard-link group's first name landed: what a later name must
        // agree on, where it landed, and what it got (ADR-0156).
        var groups = new Dictionary<string, (string Key, string Landing, CapturedMetadata Applied)>(StringComparer.Ordinal);

        try
        {
            await WalkAsync(rootTree, string.Empty, folder: null).ConfigureAwait(false);
        }
        finally
        {
            foreach (var folder in folders.OrderByDescending(folder => Depth(folder.Path)))
            {
                Count(
                    folder.Captured,
                    !folder.Found && IsStillTheFolderMade(root, folder.Path)
                        ? writer.Write(folder.Destination, folder.Values, EntryKind.DirectoryPlaceholder).Applied
                        : CapturedMetadata.None);
            }
        }

        return new RecoveryRestoreReport(
            restored,
            failed,
            skipped,
            notes,
            [
                .. MetadataRules.Vocabulary
                    .Where(entry => notApplied.ContainsKey(entry.Name))
                    .Select(entry => new RecoveryNotApplied(entry.Name, notApplied[entry.Name])),
            ]);

        void Count(CapturedMetadata captured, CapturedMetadata applied)
        {
            foreach (var name in MetadataRules.NotApplied(captured, applied) ?? [])
            {
                notApplied[name] = notApplied.GetValueOrDefault(name) + 1;
            }
        }

        // A write the output refuses at an entry's name fails that entry
        // alone (ADR-0155). Only the output's own calls go through here: a
        // store's fault is an IOException too, and still ends the run.
        bool Placed(string path, Action write)
        {
            try
            {
                write();
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                notes.Add(new RecoveryNote(RecoveryNoteKind.WriteFailed, path, Detail: exception.Message));
                return false;
            }
        }

        // folder: where the tool made the folder this tree is, and whether
        // one already stood there; null for the snapshot's root.
        async ValueTask WalkAsync(ObjectId treeId, string prefix, (string Destination, bool Found)? folder)
        {
            ObjectId? next = treeId;
            while (next is { } id)
            {
                var treeBytes = await ReadRecordAsync(
                    id, prefix.Length == 0 ? null : prefix, notes, cancellationToken).ConfigureAwait(false);
                if (treeBytes is null)
                {
                    failed++;
                    return;
                }

                var tree = TreeManifestCodec.Decode(treeBytes);

                // A folder's own metadata rides on the first manifest of its
                // chain. Only what the writer can apply is kept, so a tree of
                // many folders holds no descriptors or streams.
                if (id == treeId && folder is { } made)
                {
                    var own = tree.Metadata ?? EntryMetadata.Empty;
                    folders.Add(new Folder(
                        prefix,
                        made.Destination,
                        made.Found,
                        MetadataRules.Captured(own),
                        own with { WindowsSecurityDescriptor = null, AlternateStreams = [] }));
                }

                foreach (var entry in tree.Entries)
                {
                    var name = Encoding.UTF8.GetString(entry.Name.Span);
                    var path = prefix.Length == 0 ? name : prefix + "/" + name;

                    // A tree entry's name is repository text, and this session
                    // exists precisely for repositories in the worst state to
                    // trust. Refused per entry, with the subtree it would have
                    // carried; the drill continues.
                    if (!TryPlace(root, path, name, out var destination, out var why))
                    {
                        failed++;
                        notes.Add(new RecoveryNote(RecoveryNoteKind.Refused, path, Reason: why));
                        continue;
                    }

                    if (entry.EntryKind == EntryKind.DirectoryPlaceholder)
                    {
                        var directory = destination;
                        var found = Directory.Exists(directory);
                        if (!Placed(path, () => Directory.CreateDirectory(directory)))
                        {
                            failed++;
                            continue;
                        }

                        await WalkAsync(entry.ObjectId, path, (directory, found)).ConfigureAwait(false);
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

                    // A link is made where links can be made without a
                    // privilege, as the executor makes one (ADR-0155).
                    if (manifest.EntryKind == EntryKind.Symlink && !OperatingSystem.IsWindows()
                        && manifest.LinkTarget is { } linkTarget)
                    {
                        if (MakeLink(manifest, path, destination, Encoding.UTF8.GetString(linkTarget.Span)))
                        {
                            restored++;
                        }
                        else
                        {
                            failed++;
                        }

                        continue;
                    }

                    if (manifest.EntryKind != EntryKind.File)
                    {
                        skipped++;
                        notes.Add(new RecoveryNote(RecoveryNoteKind.SkippedSpecial, path, Outcome: manifest.EntryKind));
                        continue;
                    }

                    // A later name of a group whose first name landed is that
                    // file again, where it agrees with it and the output makes
                    // a link; otherwise it is a file of its own, and says why
                    // (ADR-0156).
                    var (group, key) = GroupOf(manifest);
                    if (group is not null && groups.TryGetValue(group, out var first))
                    {
                        if (!string.Equals(first.Key, key, StringComparison.Ordinal))
                        {
                            notes.Add(new RecoveryNote(
                                RecoveryNoteKind.NotLinked, path, Reason: "its content or attributes differ from its group's first name"));
                        }
                        else if (!Placed(path, () => Clear(destination)))
                        {
                            failed++;
                            continue;
                        }
                        else if (link(first.Landing, destination))
                        {
                            restored++;
                            Count(MetadataRules.Captured(manifest.Metadata), first.Applied);
                            continue;
                        }
                        else
                        {
                            notes.Add(new RecoveryNote(
                                RecoveryNoteKind.NotLinked, path, Reason: "the output would not make a hard link here"));
                        }
                    }

                    if (await RestoreFileAsync(manifest, path, destination).ConfigureAwait(false) is { } applied)
                    {
                        restored++;
                        if (group is not null)
                        {
                            groups.TryAdd(group, (key!, destination, applied));
                        }
                    }
                    else
                    {
                        failed++;
                    }
                }

                next = tree.Continuation;
            }
        }

        // Whatever stands at a name a link takes, a link or a file, is
        // replaced as a file's rename replaces it; a folder there is not.
        static void Clear(string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (new FileInfo(destination).LinkTarget is not null || File.Exists(destination))
            {
                File.Delete(destination);
            }
        }

        // The link's metadata goes onto the link, never through it.
        bool MakeLink(FileVersionManifest manifest, string path, string destination, string target)
        {
            if (!Placed(path, () =>
                {
                    Clear(destination);
                    File.CreateSymbolicLink(destination, target);
                }))
            {
                return false;
            }

            Count(
                MetadataRules.Captured(manifest.Metadata),
                writer.Write(destination, manifest.Metadata, EntryKind.Symlink).Applied);
            return true;
        }

        // What the landed file got of its captured metadata, or null where
        // it did not land.
        async ValueTask<CapturedMetadata?> RestoreFileAsync(FileVersionManifest manifest, string path, string destination)
        {
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

                // Whatever stands at the spool's name, a link included, is
                // removed rather than opened, and the spool is then made new,
                // which refuses a name that is there again (ADR-0152).
                FileStream? made = null;
                if (!Placed(path, () =>
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Delete(spool);
                        made = SparseFile.Create(spool, FileMode.CreateNew, holes: manifest.SparseExtents.Count > 0);
                    }))
                {
                    return null;
                }

                var output = made!;
                await using (output.ConfigureAwait(false))
                {
                    foreach (var (_, reference, extent) in pieces)
                    {
                        if (reference is { } segment)
                        {
                            if (!_records.TryGetValue(segment.ObjectId, out var located))
                            {
                                notes.Add(new RecoveryNote(RecoveryNoteKind.SegmentMissing, path, Record: segment.ObjectId));
                                return null;
                            }

                            var read = await located.Reader.ReadRecordAsync(located.Entry, cancellationToken)
                                .ConfigureAwait(false);
                            if (read.Outcome != RecordReadOutcome.Ok)
                            {
                                notes.Add(new RecoveryNote(
                                    RecoveryNoteKind.SegmentUnreadable, path, Outcome: read.Outcome, Detail: read.Detail));
                                return null;
                            }

                            await output.WriteAsync(read.Plaintext!, cancellationToken).ConfigureAwait(false);
                            wholeFile.AppendData(read.Plaintext!);
                        }
                        else if (extent is { } hole)
                        {
                            // The hash covers the zeroes a hole reads as (06
                            // §4.2); the spool skips it, so it stays a hole
                            // wherever the output's filesystem can hold one
                            // (09 §4).
                            var zeroes = new byte[Math.Min(hole.Length, 64 * 1024)];
                            var remaining = (long)hole.Length;
                            while (remaining > 0)
                            {
                                var block = (int)Math.Min(remaining, zeroes.Length);
                                wholeFile.AppendData(zeroes.AsSpan(0, block));
                                remaining -= block;
                            }

                            output.Seek((long)hole.Length, SeekOrigin.Current);
                        }
                    }

                    // A file ending in a hole has nothing written past its
                    // last data, so its length is set rather than reached: to
                    // where the last piece ends, which is where the hash
                    // stopped.
                    output.SetLength(output.Position);
                }

                Span<byte> hash = stackalloc byte[32];
                wholeFile.GetHashAndReset(hash);
                if (!hash.SequenceEqual(manifest.WholeFileHash.Span))
                {
                    notes.Add(new RecoveryNote(RecoveryNoteKind.HashMismatch, path));
                    return null;
                }

                if (!Placed(path, () => File.Move(spool, destination, overwrite: true)))
                {
                    return null;
                }

                var applied = writer.Write(destination, manifest.Metadata, EntryKind.File).Applied;
                Count(MetadataRules.Captured(manifest.Metadata), applied);
                return applied;
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
    /// The hard-link group a file's name is one of, and what it must agree
    /// on with that group's first name to be the same file again, by the
    /// rule the executor holds to (ADR-0156); nulls for a file of no group.
    /// </summary>
    private static (string? Group, string? Key) GroupOf(FileVersionManifest manifest) =>
        (HardLinkGroups.GroupOf(manifest), HardLinkGroups.KeyOf(manifest));

    /// <summary>
    /// Where a tree entry named <paramref name="name"/> at
    /// <paramref name="path"/> is written under <paramref name="root"/>, by
    /// the executor's rule (ADR-0152), or why it is not.
    /// </summary>
    /// <remarks>
    /// A name that is not a plain component — '..', a separator, a rooted
    /// form — or a path that leaves the output through a link already there
    /// would write outside it. The name is held to the rule alone first,
    /// since one holding a separator reads as two once joined.
    /// </remarks>
    private static bool TryPlace(string root, string path, string name, out string destination, out string why)
    {
        if (!RestorePaths.IsPlainComponent(name, out why))
        {
            destination = string.Empty;
            return false;
        }

        return RestorePaths.TryResolve(root, path, out destination, out why);
    }

    /// <summary>
    /// A folder the tool made or found, and what it is given once nothing
    /// more will land in it.
    /// </summary>
    /// <param name="Path">Its path inside the snapshot, slash-separated.</param>
    /// <param name="Destination">Where it was made.</param>
    /// <param name="Found">Whether a folder already stood there.</param>
    /// <param name="Captured">What was captured with it.</param>
    /// <param name="Values">The captured values the writer can apply.</param>
    private sealed record Folder(
        string Path, string Destination, bool Found, CapturedMetadata Captured, EntryMetadata Values);

    /// <summary>How many folders deep <paramref name="path"/> lies.</summary>
    private static int Depth(string path) => path.Count(character => character == '/');

    /// <summary>
    /// Whether every folder from <paramref name="outputDirectory"/> down to
    /// <paramref name="path"/> is still a folder and none is a link.
    /// </summary>
    /// <remarks>
    /// A folder's metadata goes on at the end of the run, so whoever can
    /// write beside it has had the rest of the run to put a link in its
    /// place or in an ancestor's. Written through a link, the folder's
    /// permissions and times would land on whatever the link points at.
    /// </remarks>
    private static bool IsStillTheFolderMade(string outputDirectory, string path)
    {
        var built = outputDirectory;
        foreach (var component in path.Split('/'))
        {
            built = Path.Combine(built, component);
            if (new DirectoryInfo(built) is not { Exists: true, LinkTarget: null })
            {
                return false;
            }
        }

        return true;
    }

    /// <param name="objectId">The record to read.</param>
    /// <param name="path">The entry it serves, or null for the root tree.</param>
    /// <param name="notes">Where a failure is noted.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    private async ValueTask<byte[]?> ReadRecordAsync(
        ObjectId objectId, string? path, List<RecoveryNote> notes, CancellationToken cancellationToken)
    {
        if (!_records.TryGetValue(objectId, out var located))
        {
            notes.Add(new RecoveryNote(RecoveryNoteKind.RecordMissing, path, Record: objectId));
            return null;
        }

        var read = await located.Reader.ReadRecordAsync(located.Entry, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != RecordReadOutcome.Ok)
        {
            notes.Add(new RecoveryNote(RecoveryNoteKind.RecordUnreadable, path, Outcome: read.Outcome, Detail: read.Detail));
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
