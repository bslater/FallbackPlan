using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using System.Text;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using Microsoft.Data.Sqlite;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Shared fixture for the end-to-end suites: a temp-rooted local store, a
/// deterministic key set, a small-blob policy that forces multiple blobs from
/// a few-mebibyte file, and a seeded test file mixing compressible and
/// incompressible regions.
/// </summary>
/// <remarks>
/// The keys are a write-only bundle over <see cref="TestAuthority"/>'s pinned
/// root, so every suite here archives sealed data blobs — which is the only
/// shape the product writes. Reading content back therefore needs the
/// derived scalar: readers take <see cref="Authority"/>, and a blob opened
/// by hand takes <see cref="ContentKeyOpener"/>. The dedup trust domain is
/// the device domain, because the repository domain verifies another
/// writer's segments by reading their content and a write-only holder
/// cannot (ADR-0042 §7).
/// </remarks>
public abstract class ArchiveTestHarness : IDisposable
{
    protected static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    protected static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-e2e-tests", Guid.NewGuid().ToString("n"));

    /// <summary>64 KiB segments, tiny blob targets: a ~3 MiB file spans many blobs.</summary>
    protected static CapturePolicy SmallBlobPolicy { get; } = CapturePolicy.Default with
    {
        DedupTrustDomain = DedupTrustDomain.Device,
        SegmentSize = SegmentSize.Create(64 * 1024),
        BlobWriteProfile = BlobWriteProfile.LocalDefault with
        {
            TargetSizeBytes = 256 * 1024,
            MaximumSizeBytes = 512 * 1024,
        },
    };

    /// <summary>
    /// cdc-v1 at the conformance-vector parameters (target 64 KiB, min 8 KiB,
    /// max 512 KiB), with blob targets sized so the maximum segment still
    /// fits one blob.
    /// </summary>
    protected static CapturePolicy CdcPolicy { get; } = CapturePolicy.Default with
    {
        DedupTrustDomain = DedupTrustDomain.Device,
        SegmentationProfile = Domain.Profiles.SegmentationProfile.CdcV1,
        CdcParameters = CdcParameters.Create(64 * 1024, 8 * 1024, 512 * 1024),
        BlobWriteProfile = BlobWriteProfile.LocalDefault with
        {
            TargetSizeBytes = 1024 * 1024,
            MaximumSizeBytes = 2 * 1024 * 1024,
        },
    };

    /// <summary>The store root on disk — corruption probes reach beneath the interface here.</summary>
    protected string StoreRoot => Path.Combine(_root, "store");

    /// <summary>The archiver spool directory.</summary>
    protected string SpoolDirectory => Path.Combine(_root, "spool");

    protected LocalFileSystemObjectStore CreateStore() => new(StoreRoot);

    private static readonly byte[] SealingPrivateKey = TestAuthority.Shared.SealingPrivateKey.ToArray();

    protected static RepositoryKeySet CreateKeys() =>
        RepositoryKeySet.FromWriteCredential(TestAuthority.Shared.Credential);

    protected static RepositoryWriteCredential CreateCredential() => TestAuthority.Shared.Credential.Clone();

    /// <summary>The read authority a reader needs to open sealed content; shared, never disposed.</summary>
    protected static RepositoryReadAuthority Authority => TestAuthority.Shared;

    /// <summary>The grant a <see cref="BlobReader"/> opened by hand needs for sealed content; shared, never disposed.</summary>
    protected static SealedContentKeyOpener ContentKeyOpener { get; } = new(SealingPrivateKey, Repo);

    protected FileArchiver CreateArchiver(LocalFileSystemObjectStore store, RepositoryKeySet keys) =>
        CreateArchiver(store, keys, SmallBlobPolicy, firstCounter: 1);

    protected FileArchiver CreateArchiver(
        LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        CapturePolicy policy,
        ulong firstCounter) => new(
        policy,
        Repo,
        Writer,
        KeyGeneration.Zero,
        keys,
        store,
        new MonotonicBlobCounterAllocator(firstCounter),
        SpoolDirectory,
        FormatVersions.SealedDataPlane);

    /// <summary>
    /// The index entries an archive's records amount to, projected from the
    /// blobs' own footers: the 07 §10 relationship, "every field in an index
    /// entry is present in the footer".
    /// </summary>
    protected static async Task<List<IndexEntry>> IndexEntriesOfAsync(
        Storage.Local.LocalFileSystemObjectStore store, RepositoryKeySet keys, ArchiveResult archived)
    {
        var entries = new List<IndexEntry>();
        using var probe = new RepositoryReader(Repo, keys, store, Authority);
        await probe.LoadBlobsAsync(CancellationToken.None);

        foreach (var reference in archived.SegmentReferences.DistinctBy(reference => reference.ObjectId))
        {
            Assert.IsTrue(probe.TryLocateRecord(reference.ObjectId, out var locatedKey, out var entry));

            // A segment an earlier archive stored is that archive's to index.
            if (archived.Blobs.SingleOrDefault(archivedBlob => archivedBlob.StoreKey.Equals(locatedKey)) is not { } blob)
            {
                continue;
            }

            entries.Add(new IndexEntry(
                entry.ObjectId, blob.BlobId, entry.PhysicalOffset, entry.StoredLength,
                entry.CompressionProfileValue, entry.EncryptionProfileValue, IndexEntryType.Insertion));
        }

        return entries;
    }

    /// <summary>
    /// The segment references a tree snapshot's file version declares, read
    /// from its manifest. Once the snapshot's session has ended that is where
    /// they are: the published result's copy waited in the session's spool
    /// and went with it (ADR-0201).
    /// </summary>
    protected static async Task<IReadOnlyList<SegmentReference>> ReferencesOfAsync(RepositoryReader reader, ObjectId versionId)
    {
        var read = await reader.ReadSegmentAsync(versionId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        return FileVersionManifestCodec.Decode(read.Plaintext!).SegmentReferences;
    }

    /// <summary>
    /// Every file a published tree snapshot holds, read from its trees and
    /// its files' manifests: where each is, the version it names, and that
    /// version. Folders come after the files beside them, each folder's
    /// entries in the order its tree holds them.
    /// </summary>
    /// <remarks>
    /// A published result counts its files and does not list them, so what a
    /// snapshot captured is read from what it published. A folder the run
    /// took as the snapshot before held it is read too: it is this snapshot's
    /// as much as a walked one.
    /// </remarks>
    protected static async Task<IReadOnlyList<CapturedFile>> FilesOfAsync(
        IObjectStore store, RepositoryKeySet keys, PublishedTreeSnapshot published)
    {
        ArgumentNullException.ThrowIfNull(published);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        return await FilesOfAsync(reader, published.RootTreeObjectId);
    }

    /// <inheritdoc cref="FilesOfAsync(IObjectStore, RepositoryKeySet, PublishedTreeSnapshot)"/>
    protected static async Task<IReadOnlyList<CapturedFile>> FilesOfAsync(RepositoryReader reader, ObjectId rootTree)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var files = new List<CapturedFile>();
        await AddFilesOfAsync(reader, rootTree, string.Empty, files);
        return files;
    }

    private static async Task AddFilesOfAsync(
        RepositoryReader reader, ObjectId treeId, string prefix, List<CapturedFile> files)
    {
        List<TreeManifest> chain = [];
        for (ObjectId? next = treeId; next is { } id;)
        {
            var tree = TreeManifestCodec.Decode(await ReadRecordAsync(reader, id));
            chain.Add(tree);
            next = tree.Continuation;
        }

        List<(string Path, ObjectId TreeId)> folders = [];
        foreach (var entry in TreeChain.ValidateAndFlatten(chain))
        {
            var path = prefix + Encoding.UTF8.GetString(entry.Name.Span);
            if (entry.EntryKind == EntryKind.DirectoryPlaceholder)
            {
                folders.Add((path, entry.ObjectId));
                continue;
            }

            files.Add(new CapturedFile(
                path, entry.ObjectId, FileVersionManifestCodec.Decode(await ReadRecordAsync(reader, entry.ObjectId))));
        }

        foreach (var (path, folderId) in folders)
        {
            await AddFilesOfAsync(reader, folderId, path + "/", files);
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadRecordAsync(RepositoryReader reader, ObjectId objectId)
    {
        var read = await reader.ReadSegmentAsync(objectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        return read.Plaintext!;
    }

    /// <summary>
    /// A deterministic ~3 MiB file: alternating 128 KiB regions of repeated
    /// ASCII (highly compressible) and seeded pseudo-random bytes
    /// (incompressible), so the per-record threshold decision goes both ways
    /// in one archive run.
    /// </summary>
    protected static byte[] BuildTestFile(int seed = 20260803, int regions = 24)
    {
        var random = new Random(seed);
        var file = new MemoryStream();
        var pattern = "FallbackPlan end-to-end segment content. "u8.ToArray();

        for (var region = 0; region < regions; region++)
        {
            var buffer = new byte[128 * 1024];

            if (region % 2 == 0)
            {
                for (var i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = pattern[i % pattern.Length];
                }
            }
            else
            {
                random.NextBytes(buffer);
            }

            file.Write(buffer);
        }

        return file.ToArray();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Deletes the temp root.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || !Directory.Exists(_root))
        {
            return;
        }

        // Connection pooling can keep a catalogue file open past its owning
        // connection's dispose, and on Windows an open handle fails the
        // recursive delete — a cleanup failure the test then wears as its
        // own. Drain the pools first, and treat a residual refusal as best
        // effort: the temp root is per-test-class and leaks nothing that
        // matters more than the assertion that already ran.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}

/// <summary>
/// One file a published tree snapshot holds, as its trees and its manifest
/// state it: its path from the snapshot's root, the version it names, and that
/// version's manifest.
/// </summary>
public sealed record CapturedFile(string RelativePath, ObjectId ObjectId, FileVersionManifest Manifest)
{
    /// <summary>What kind of entry the version is.</summary>
    public EntryKind EntryKind => Manifest.EntryKind;
}
