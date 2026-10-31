using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A restore gives a hard-link group back as one file under each of its
/// names, as it was captured (FR-RST-004; ADR-0102; architecture 06 §3's
/// matrix, which preserves hard links on every platform). The first name
/// of a group the run reaches lands as any file does, and each later name
/// is a link to it, not a copy. The plan measures a group's bytes once
/// (FR-RST-003).
/// </summary>
/// <remarks>
/// <para>
/// A group is named by manifest key 12, which keys on the device's identity
/// and the file's, not on the filesystem's. Two files on two filesystems
/// may share a group, so a name is linked only where it agrees with the
/// file it would share: its content, its length, and every captured
/// attribute but its access time, which reading the first name moves. A
/// name that does not agree is restored as a file of its own, and its
/// receipt item says why.
/// </para>
/// <para>
/// Each name meets the existing-file policy at its own destination, as a
/// file would, and its receipt item names the one it shares a file with.
/// The shared file's metadata is the file's: written once, with the first
/// name, and listed the same for every name. So are the flags it carried,
/// which no restore sets (ADR-0180 Amendment 1).
/// </para>
/// </remarks>
[TestClass]
public sealed class RestoreHardLinkTests : ArchiveTestHarness
{
    private const int MiB = 1024 * 1024;

    /// <summary>The identity the test's groups are captured under.</summary>
    private const ulong Shared = 900;

    private static readonly RestoreTargetProfile Posix = new()
    {
        CaseSensitive = true,
        SupportsPosixMetadata = true,
        SupportsSymlinks = true,
    };

    [TestMethod]
    public async Task Receipt_AHardLinkGroup_LandsAsOneFile_UnderEachOfItsNames()
    {
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 1);
        source.AddFile("docs/a.bin", content, linkCount: 2, fileId: Shared);
        source.AddFile("docs/b.bin", content, linkCount: 2, fileId: Shared);

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xE1, RestoreTargetProfile.ForLocalPlatform());

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        AssertOneFile(Path.Combine(output, "docs", "a.bin"), Path.Combine(output, "docs", "b.bin"));
        Assert.IsNull(Item(receipt, "docs/a.bin").LinkedTo);
        var linked = Item(receipt, "docs/b.bin");
        Assert.AreEqual("restored", linked.Outcome);
        Assert.AreEqual("docs/a.bin", linked.LinkedTo);
    }

    [TestMethod]
    public async Task Receipt_ALinkedName_ListsWhatTheSharedFileDidNotGet_AsTheFirstNameDoes()
    {
        // The metadata is the file's, so every name of it says the same.
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 2);
        var shared = new EntryMetadata { ModifiedAt = 1_722_000_000_000, PosixMode = 0x1A4, OwnerName = "fallbackplan-no-such-account" };
        source.AddFile("docs/a.bin", content, linkCount: 2, fileId: Shared).Metadata = shared;
        source.AddFile("docs/b.bin", content, linkCount: 2, fileId: Shared).Metadata = shared;

        var (receipt, _) = await PublishAndRestoreAsync(source, 0xE2, Posix);

        var first = Item(receipt, "docs/a.bin");
        Assert.IsNotNull(first.NotApplied);
        CollectionAssert.AreEqual(first.NotApplied.ToArray(), Item(receipt, "docs/b.bin").NotApplied!.ToArray());
    }

    [TestMethod]
    public async Task Receipt_ALinkedName_NamesTheFlagsTheSharedFileDidNotGet_AsTheFirstNameDoes()
    {
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 12);
        var shared = new EntryMetadata { ModifiedAt = 1_722_000_000_000 };
        foreach (var name in new[] { "docs/a.bin", "docs/b.bin" })
        {
            var node = source.AddFile(name, content, linkCount: 2, fileId: Shared);
            node.Metadata = shared;
            node.Diagnostics = ["file-flags: no-dump"];
        }

        var (receipt, _) = await PublishAndRestoreAsync(source, 0xEC, RestoreTargetProfile.ForLocalPlatform());

        var linked = Item(receipt, "docs/b.bin");
        Assert.AreEqual("docs/a.bin", linked.LinkedTo, "the premise: the second name is a link to the first");
        Assert.AreEqual("file flag(s) not applied: no-dump (not set by a restore)", Item(receipt, "docs/a.bin").Detail);
        Assert.AreEqual(Item(receipt, "docs/a.bin").Detail, linked.Detail);
    }

    [TestMethod]
    public async Task Receipt_ANameWhoseContentDiffersFromItsGroup_IsRestoredAsAFileOfItsOwn_AndSaysWhy()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(5_000, 3), linkCount: 2, fileId: Shared);
        source.AddFile("docs/b.bin", Deterministic(5_000, 4), linkCount: 2, fileId: Shared);

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xE3, RestoreTargetProfile.ForLocalPlatform());

        CollectionAssert.AreEqual(Deterministic(5_000, 4), await File.ReadAllBytesAsync(Path.Combine(output, "docs", "b.bin")));
        AssertTwoFiles(Path.Combine(output, "docs", "a.bin"), Path.Combine(output, "docs", "b.bin"));
        var own = Item(receipt, "docs/b.bin");
        Assert.AreEqual("restored", own.Outcome);
        Assert.IsNull(own.LinkedTo);
        Assert.Contains("not linked", own.Detail!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Receipt_ANameWhoseMetadataDiffersFromItsGroup_IsRestoredAsAFileOfItsOwn()
    {
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 5);
        source.AddFile("docs/a.bin", content, linkCount: 2, fileId: Shared).Metadata =
            new EntryMetadata { ModifiedAt = 1_722_000_000_000, PosixMode = 0x1A4 };
        source.AddFile("docs/b.bin", content, linkCount: 2, fileId: Shared).Metadata =
            new EntryMetadata { ModifiedAt = 1_722_000_000_000, PosixMode = 0x1ED };

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xE4, RestoreTargetProfile.ForLocalPlatform());

        AssertTwoFiles(Path.Combine(output, "docs", "a.bin"), Path.Combine(output, "docs", "b.bin"));
        Assert.IsNull(Item(receipt, "docs/b.bin").LinkedTo);
        Assert.Contains("not linked", Item(receipt, "docs/b.bin").Detail!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Receipt_ANameWhoseAccessTimeAloneDiffers_IsStillLinked()
    {
        // Reading the first name moves the access time the second is captured
        // with, so every real group differs in it.
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 6);
        source.AddFile("docs/a.bin", content, linkCount: 2, fileId: Shared).Metadata =
            new EntryMetadata { ModifiedAt = 1_722_000_000_000, AccessedAt = 1_722_000_100_000, PosixMode = 0x1A4 };
        source.AddFile("docs/b.bin", content, linkCount: 2, fileId: Shared).Metadata =
            new EntryMetadata { ModifiedAt = 1_722_000_000_000, AccessedAt = 1_722_000_200_000, PosixMode = 0x1A4 };

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xE5, RestoreTargetProfile.ForLocalPlatform());

        AssertOneFile(Path.Combine(output, "docs", "a.bin"), Path.Combine(output, "docs", "b.bin"));
        Assert.AreEqual("docs/a.bin", Item(receipt, "docs/b.bin").LinkedTo);
    }

    [TestMethod]
    public async Task Receipt_ANameTheTargetWillNotLink_IsRestoredAsAFileOfItsOwn_AndSaysWhy()
    {
        // A volume that keeps no links, or a first name that landed on
        // another volume: the content is right, and a copy is less than was
        // captured, not wrong.
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 12);
        source.AddFile("docs/a.bin", content, linkCount: 2, fileId: Shared);
        source.AddFile("docs/b.bin", content, linkCount: 2, fileId: Shared);
        var output = Path.Combine(SpoolDirectory, "restored-eb");

        var receipt = await PublishAndRestoreIntoAsync(
            source, 0xEB, RestoreTargetProfile.ForLocalPlatform(), output, ExistingDestinationPolicy.Preserve,
            makeLink: (_, _) => false);

        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(Path.Combine(output, "docs", "b.bin")));
        AssertTwoFiles(Path.Combine(output, "docs", "a.bin"), Path.Combine(output, "docs", "b.bin"));
        var own = Item(receipt, "docs/b.bin");
        Assert.AreEqual("restored", own.Outcome);
        Assert.IsNull(own.LinkedTo);
        Assert.Contains("would not make a hard link", own.Detail!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Receipt_ALinkedNameWhereAFileStands_IsMovedAsideFirst_AsAFileWouldBe()
    {
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 7);
        source.AddFile("docs/a.bin", content, linkCount: 2, fileId: Shared);
        source.AddFile("docs/b.bin", content, linkCount: 2, fileId: Shared);
        var output = Path.Combine(SpoolDirectory, "restored-e6");
        Directory.CreateDirectory(Path.Combine(output, "docs"));
        await File.WriteAllTextAsync(Path.Combine(output, "docs", "b.bin"), "already here");

        var receipt = await PublishAndRestoreIntoAsync(source, 0xE6, RestoreTargetProfile.ForLocalPlatform(), output, ExistingDestinationPolicy.Preserve);

        Assert.Contains("docs/b.bin", receipt.Displaced);
        AssertOneFile(Path.Combine(output, "docs", "a.bin"), Path.Combine(output, "docs", "b.bin"));
        Assert.AreEqual("docs/a.bin", Item(receipt, "docs/b.bin").LinkedTo);
    }

    [TestMethod]
    public async Task Receipt_ALinkedNameWhereAFileStands_UnderFail_IsFailed_AndWhatStandsThereIsKept()
    {
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 8);
        source.AddFile("docs/a.bin", content, linkCount: 2, fileId: Shared);
        source.AddFile("docs/b.bin", content, linkCount: 2, fileId: Shared);
        var output = Path.Combine(SpoolDirectory, "restored-e7");
        Directory.CreateDirectory(Path.Combine(output, "docs"));
        await File.WriteAllTextAsync(Path.Combine(output, "docs", "b.bin"), "already here");

        var receipt = await PublishAndRestoreIntoAsync(source, 0xE7, RestoreTargetProfile.ForLocalPlatform(), output, ExistingDestinationPolicy.Fail);

        Assert.AreEqual("failed", Item(receipt, "docs/b.bin").Outcome);
        Assert.AreEqual("already here", await File.ReadAllTextAsync(Path.Combine(output, "docs", "b.bin")));
        Assert.AreEqual("restored", Item(receipt, "docs/a.bin").Outcome);
    }

    [TestMethod]
    public async Task Receipt_ALinkedNameUnderWriteBeside_IsLinkedBeside_AndNamesWhereItLanded()
    {
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 9);
        source.AddFile("docs/a.bin", content, linkCount: 2, fileId: Shared);
        source.AddFile("docs/b.bin", content, linkCount: 2, fileId: Shared);
        var output = Path.Combine(SpoolDirectory, "restored-e8");
        Directory.CreateDirectory(Path.Combine(output, "docs"));
        await File.WriteAllTextAsync(Path.Combine(output, "docs", "b.bin"), "already here");

        var receipt = await PublishAndRestoreIntoAsync(source, 0xE8, RestoreTargetProfile.ForLocalPlatform(), output, ExistingDestinationPolicy.WriteBeside);

        var linked = Item(receipt, "docs/b.bin");
        Assert.IsNotNull(linked.WrittenAs);
        Assert.AreEqual("already here", await File.ReadAllTextAsync(Path.Combine(output, "docs", "b.bin")));
        AssertOneFile(Path.Combine(output, "docs", "a.bin"), Path.Combine(output, linked.WrittenAs.Replace('/', Path.DirectorySeparatorChar)));
        Assert.AreEqual("docs/a.bin", linked.LinkedTo);
    }

    [TestMethod]
    public async Task Receipt_OneNameOfAGroupRestoredAlone_IsAFileOfItsOwn()
    {
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 10);
        source.AddFile("here/a.bin", content, linkCount: 2, fileId: Shared);
        source.AddFile("there/b.bin", content, linkCount: 2, fileId: Shared);

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xE9, RestoreTargetProfile.ForLocalPlatform(), prefix: "here");

        var only = Item(receipt, "here/a.bin");
        Assert.AreEqual("restored", only.Outcome);
        Assert.IsNull(only.LinkedTo);
        Assert.IsNull(only.Detail);
        Assert.IsFalse(Directory.Exists(Path.Combine(output, "there")));
    }

    [TestMethod]
    public async Task Plan_AHardLinkGroup_IsMeasuredOnce_WhetherFromTheProbeOrFromTheManifests()
    {
        // A tree of hard links, such as a package store or a series of
        // snapshot folders, writes each file once; counted per name, its
        // restore would be refused for room it does not need.
        var source = new FakeFileSystemSource();
        var content = Deterministic(MiB, 11);
        foreach (var name in new[] { "a.bin", "b.bin", "c.bin" })
        {
            source.AddFile($"docs/{name}", content, linkCount: 3, fileId: Shared);
        }

        var (store, keys, catalogue) = await PublishAsync(source, 0xEA);
        using (keys)
        using (catalogue)
        {
            var plan = RestorePlanner.Plan(catalogue, SnapshotOf(0xEA), string.Empty, Posix);
            RestoreSlice[] slices = [new(plan, Path.Combine(SpoolDirectory, "measured-ea"))];
            var probe = new RestoreSpaceProbe { AvailableBytes = _ => 1L << 40, VolumeOf = _ => 1 };

            using var reader = new RepositoryReader(Repo, keys, store, Authority);
            await reader.LoadBlobsAsync(CancellationToken.None);
            var throughManifests = await RestoreSpace.MeasureAsync(
                slices, RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Preserve, probe,
                RestoreSpace.WrittenBytesThrough(reader), CancellationToken.None);
            Assert.AreEqual((ulong)MiB, throughManifests.WriteBytes);

            var probed = await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None);
            var fromProbe = await RestoreSpace.MeasureAsync(
                slices, RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Preserve, probe,
                RestoreSpace.WrittenBytesFrom(probed.Facts), CancellationToken.None);
            Assert.AreEqual((ulong)MiB, fromProbe.WriteBytes);
        }
    }

    /// <summary>Whether two names are one file: what is written through one is read through the other.</summary>
    private static void AssertOneFile(string first, string second)
    {
        var before = File.ReadAllBytes(first);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(second));
        File.AppendAllText(second, "+");
        Assert.HasCount(before.Length + 1, File.ReadAllBytes(first), $"{second} is a copy of {first}, not a second name for it");
    }

    /// <summary>Whether two names are two files: what is written through one is not read through the other.</summary>
    private static void AssertTwoFiles(string first, string second)
    {
        var before = File.ReadAllBytes(first);
        File.AppendAllText(second, "+");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(first), $"{second} and {first} are one file");
    }

    private static ReceiptItem Item(RestoreReceipt receipt, string path) =>
        receipt.Items.Single(item => item.Path == path);

    private static byte[] SnapshotOf(byte seed) => Enumerable.Repeat(seed, 16).ToArray();

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte seed) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = SnapshotOf(seed),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "restore-hard-link-tests/1.0",
    };

    private async Task<(LocalFileSystemObjectStore Store, RepositoryKeySet Keys, CatalogueDb Catalogue)> PublishAsync(
        FakeFileSystemSource source, byte seed)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"links-{seed:x2}.db"), Repo);

        await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, $"sequence-{seed:x2}.txt"))),
                SpoolDirectory, FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(Job(source, seed), CancellationToken.None);

        return (store, keys, catalogue);
    }

    private async Task<(RestoreReceipt Receipt, string Output)> PublishAndRestoreAsync(
        FakeFileSystemSource source, byte seed, RestoreTargetProfile target, string prefix = "")
    {
        var output = Path.Combine(SpoolDirectory, $"restored-{seed:x2}");
        return (await PublishAndRestoreIntoAsync(source, seed, target, output, ExistingDestinationPolicy.Preserve, prefix), output);
    }

    private async Task<RestoreReceipt> PublishAndRestoreIntoAsync(
        FakeFileSystemSource source, byte seed, RestoreTargetProfile target, string output,
        ExistingDestinationPolicy existing, string prefix = "", Func<string, string, bool>? makeLink = null)
    {
        var (store, keys, catalogue) = await PublishAsync(source, seed);
        using (keys)
        using (catalogue)
        {
            var plan = RestorePlanner.Plan(catalogue, SnapshotOf(seed), prefix, target);
            using var reader = new RepositoryReader(Repo, keys, store, Authority);
            await reader.LoadBlobsAsync(CancellationToken.None);
            return await new RestoreExecutor(reader, target, makeLink: makeLink).ExecuteAsync(
                plan, output,
                new RestoreExecutionOptions
                {
                    DestinationMode = RestoreDestinationMode.InPlace,
                    ExistingDestination = existing,
                    RunId = "links",
                    NowUnixMilliseconds = 1_722_700_000_000,
                },
                CancellationToken.None);
        }
    }
}
