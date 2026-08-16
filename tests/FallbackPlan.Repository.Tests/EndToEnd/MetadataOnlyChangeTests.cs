using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using System.Text;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A file whose bytes did not change but whose metadata did (FR-MAN-002,
/// NFR-PERF-003).
/// </summary>
/// <remarks>
/// <para>
/// The surveyed changelog records this exact defect as a shipped fix —
/// updated timestamps discarded when no data changed — and it is a fidelity
/// bug of the worst
/// shape, because the backup reports success and the loss appears only at
/// restore. The reuse short-circuit is keyed on identity, size and
/// modification time, and none of those moves when a file is chmodded: POSIX
/// <c>chmod</c> touches ctime, not mtime. So the cheapest possible reading of
/// "unchanged" quietly means "the bytes are unchanged", while the thing being
/// re-emitted is a whole manifest that also states mode, owner, group,
/// extended attributes and the security descriptor.
/// </para>
/// <para>
/// The stake is not cosmetic. Somebody who notices a private key is
/// world-readable, fixes it, and takes a backup expects the backup to hold
/// the file as it now is. Restoring the earlier mode re-opens the hole, from
/// the tool whose job was to preserve the state of things.
/// </para>
/// </remarks>
[TestClass]
public sealed class MetadataOnlyChangeTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private CatalogueDb OpenCatalogue() =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy, CatalogueDb catalogue) =>
        new(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory, observer: null, catalogue);

    private static SnapshotJob Job(FakeFileSystemSource source, byte snapshotSeed, ulong now = 1_722_600_000_000) => new()
    {
        Source = source,
        RootPath = "/",
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(snapshotSeed, 16).ToArray(),
        NowUnixMilliseconds = now,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "fallbackplan-tests/1.0",
    };

    private static byte[] Content() => [.. Enumerable.Range(0, 4_000).Select(value => (byte)(value * 7))];

    private static async Task<FileVersionManifest> ReadFileVersionAsync(RepositoryReader reader, ObjectId id)
    {
        var read = await reader.ReadSegmentAsync(id, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        return FileVersionManifestCodec.Decode(read.Plaintext!);
    }

    /// <summary>
    /// Runs two publications with only <paramref name="mutate"/> between them,
    /// and returns the second publication's version of <c>secret.key</c>.
    /// </summary>
    private async Task<(PublishedFileVersion File, FileVersionManifest Manifest)> AfterMetadataChangeAsync(
        Func<EntryMetadata, EntryMetadata> mutate)
    {
        var source = new FakeFileSystemSource();
        var node = source.AddFile("secret.key", Content());
        node.Metadata = node.Metadata with { ModifiedAt = 1_722_000_000_000, PosixMode = 0x1A4 };

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        // The only thing that changes. The content is untouched, so identity,
        // size and mtime are all exactly what the first publication recorded —
        // which is the whole difficulty: every signal the short-circuit looks
        // at says "nothing happened".
        node.Metadata = mutate(node.Metadata);

        var second = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                    ParentSnapshots = [Enumerable.Repeat((byte)0xA1, 16).ToArray()],
                },
                CancellationToken.None);

        var file = Assert.ContainsSingle(second.Files);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        return (file, await ReadFileVersionAsync(reader, file.ObjectId));
    }

    [TestMethod]
    public async Task ThePermissionsTightened_TheSnapshotHoldsTheNewMode()
    {
        // 0o644 → 0o600: the world-readable private key, fixed.
        var (file, manifest) = await AfterMetadataChangeAsync(
            metadata => metadata with { PosixMode = 0x180 });

        Assert.AreEqual(0x180u, manifest.Metadata.PosixMode, "the snapshot still holds the old mode");
        Assert.IsFalse(file.Reused, "a metadata change is a change; the prior version cannot stand in for it");
    }

    [TestMethod]
    public async Task TheOwnerChanged_TheSnapshotHoldsTheNewOwner()
    {
        var (_, manifest) = await AfterMetadataChangeAsync(
            metadata => metadata with { OwnerName = "archivist", GroupName = "staff" });

        Assert.AreEqual("archivist", manifest.Metadata.OwnerName);
        Assert.AreEqual("staff", manifest.Metadata.GroupName);
    }

    [TestMethod]
    public async Task AnExtendedAttributeChanged_TheSnapshotHoldsIt()
    {
        var (_, manifest) = await AfterMetadataChangeAsync(
            metadata => metadata with
            {
                ExtendedAttributes = [new ExtendedAttributeEntry(Encoding.UTF8.GetBytes("user.label"), new byte[] { 1, 2, 3 })],
            });

        var attribute = Assert.ContainsSingle(manifest.Metadata.ExtendedAttributes);
        Assert.AreEqual("user.label", Encoding.UTF8.GetString(attribute.Name.Span));
    }

    /// <summary>
    /// The saving grace that must survive the fix: recording the new metadata
    /// must not mean re-reading and re-storing the bytes. A metadata-only
    /// change writes a new manifest and no new content.
    /// </summary>
    [TestMethod]
    public async Task OnlyTheMetadataChanged_TheContentIsNeitherReadNorStoredAgain()
    {
        var source = new FakeFileSystemSource();
        var node = source.AddFile("secret.key", Content());

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        node.Metadata = node.Metadata with { PosixMode = 0x180 };
        source.OpenedPaths.Clear();

        var second = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                    ParentSnapshots = [Enumerable.Repeat((byte)0xA1, 16).ToArray()],
                },
                CancellationToken.None);

        Assert.IsEmpty(source.OpenedPaths, "the bytes did not change; re-reading them is the cost NFR-PERF-003 forbids");
        Assert.AreEqual(0, second.ContentBlobs.Sum(blob => blob.RecordCount), "no content record should be written");

        // A new manifest, and it descends from the old one rather than
        // claiming to be the file's first version.
        var file = Assert.ContainsSingle(second.Files);
        Assert.AreNotEqual(Assert.ContainsSingle(first.Files).ObjectId, file.ObjectId);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var manifest = await ReadFileVersionAsync(reader, file.ObjectId);

        Assert.AreEqual(Assert.ContainsSingle(first.Files).ObjectId, manifest.ParentVersion);
        Assert.AreEqual(0x180u, manifest.Metadata.PosixMode);
    }

    /// <summary>
    /// Access time is the one metadata field that must NOT count as a change.
    /// Reading a file moves its atime and the backup's own read is a read, so
    /// treating it as evidence would make every file differ from itself on the
    /// next pass — a whole-tree re-capture every run, which is precisely the
    /// work NFR-PERF-003 exists to avoid. This is not hypothetical: the first
    /// version of the fix included atime and turned an agent's second pass
    /// from "2 unchanged" into "0 unchanged".
    /// </summary>
    [TestMethod]
    public async Task OnlyTheAccessTimeMoved_TheShortCircuitStillFires()
    {
        var source = new FakeFileSystemSource();
        var node = source.AddFile("secret.key", Content());
        node.Metadata = node.Metadata with { AccessedAt = 1_722_000_000_000 };

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        // What the previous backup's own read did to the file.
        node.Metadata = node.Metadata with { AccessedAt = 1_722_600_000_000 };

        var second = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                    ParentSnapshots = [Enumerable.Repeat((byte)0xA1, 16).ToArray()],
                },
                CancellationToken.None);

        var file = Assert.ContainsSingle(second.Files);
        Assert.IsTrue(file.Reused, "a backup's own read must not make the next backup think the file changed");
        Assert.AreEqual(Assert.ContainsSingle(first.Files).ObjectId, file.ObjectId);
    }

    /// <summary>
    /// And the short-circuit still fires when genuinely nothing changed — the
    /// property the fix must not trade away.
    /// </summary>
    [TestMethod]
    public async Task NothingChangedAtAll_TheShortCircuitStillFires()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("secret.key", Content());

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        source.OpenedPaths.Clear();
        var second = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                    ParentSnapshots = [Enumerable.Repeat((byte)0xA1, 16).ToArray()],
                },
                CancellationToken.None);

        var file = Assert.ContainsSingle(second.Files);
        Assert.IsTrue(file.Reused);
        Assert.AreEqual(Assert.ContainsSingle(first.Files).ObjectId, file.ObjectId);
        Assert.IsEmpty(source.OpenedPaths);
    }
}
