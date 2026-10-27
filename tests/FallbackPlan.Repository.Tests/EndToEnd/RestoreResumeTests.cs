using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A restore cut short is resumed by running it again (FR-RST-008,
/// ADR-0109). A file already holding the snapshot's content where the run
/// lands it, its length and its whole-file hash both, is left as it stands:
/// not moved aside, not written again, and nothing of its content read again
/// from the store. Its captured metadata is applied again, because a run cut
/// short may have stopped between the two. Every other item lands as any run
/// lands it. A quarantined run is resumed in the folder the earlier run
/// landed in, and the receipt says which files were found in place.
/// </summary>
[TestClass]
public sealed class RestoreResumeTests : ArchiveTestHarness
{
    private const ulong Captured = 1_722_000_000_000;

    private static readonly byte[] Alpha = Deterministic(9_000, 3);
    private static readonly byte[] Beta = Deterministic(9_000, 7);

    [TestMethod]
    public async Task Resume_LeavesAFileAlreadyLanded_AndRestoresTheRest()
    {
        var (plan, target, store, keys, catalogue) = await PublishAsync("left", 0xD1);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, "left-out");
        var landed = Land(output, "data/alpha.bin", Alpha);
        var before = FileIdOf(landed);

        var receipt = await ExecuteAsync(store, keys, catalogue, target, plan, output, Resumed("left-run"));

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.IsEmpty(receipt.Displaced, "what already landed is not moved aside");
        Assert.IsFalse(Directory.Exists(Path.Combine(output, ".fbp-displaced")));
        Assert.AreEqual(before, FileIdOf(landed), "the file found in place is the same file, not one written over it");

        var kept = receipt.Items.Single(item => item.Path == "data/alpha.bin");
        Assert.AreEqual("restored", kept.Outcome);
        Assert.IsTrue(kept.FoundInPlace);
        Assert.AreEqual((ulong)Alpha.Length, kept.Bytes);

        var written = receipt.Items.Single(item => item.Path == "data/beta.bin");
        Assert.AreEqual("restored", written.Outcome);
        Assert.IsFalse(written.FoundInPlace);
        SequenceAssert.AreEqual(Alpha, File.ReadAllBytes(landed));
        SequenceAssert.AreEqual(Beta, File.ReadAllBytes(Path.Combine(output, "data", "beta.bin")));
    }

    [TestMethod]
    public async Task WithoutResume_AFileAlreadyLanded_IsMovedAsideAndWrittenAgain()
    {
        // What a run does without being told it resumes: what it finds is
        // somebody's, however like the snapshot's it is.
        var (plan, target, store, keys, catalogue) = await PublishAsync("fresh", 0xD2);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, "fresh-out");
        var landed = Land(output, "data/alpha.bin", Alpha);
        var before = FileIdOf(landed);

        var receipt = await ExecuteAsync(store, keys, catalogue, target, plan, output, Fresh("fresh-run"));

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.Contains("data/alpha.bin", receipt.Displaced);
        Assert.AreNotEqual(before, FileIdOf(landed));
        Assert.IsFalse(receipt.Items.Single(item => item.Path == "data/alpha.bin").FoundInPlace);
    }

    [TestMethod]
    public async Task Resume_ReadsNothingOfWhatAlreadyLanded()
    {
        var (plan, target, store, keys, catalogue) = await PublishAsync("reads", 0xD3);
        using var _ = keys;
        using var __ = catalogue;
        var counting = new TestSupport.CountingObjectStore(store);

        // A run that lands everything reads the files' content…
        var first = Path.Combine(SpoolDirectory, "reads-first");
        Assert.AreEqual(
            RestoreOutcome.Complete,
            (await ExecuteAsync(counting, keys, catalogue, target, plan, first, Fresh("reads-first"))).Outcome);
        Assert.IsTrue(counting.ReadKeys.Any(IsContent), "the instrument must see a content read for its silence to mean anything");

        // …and one resumed over all of it reads none, ahead of the loop or in it.
        counting.Reset();
        var receipt = await ExecuteAsync(counting, keys, catalogue, target, plan, first, Resumed("reads-again"));

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.IsTrue(receipt.Items.Where(item => item.Path.EndsWith(".bin", StringComparison.Ordinal)).All(item => item.FoundInPlace));
        Assert.IsFalse(counting.ReadKeys.Any(IsContent), string.Join(", ", counting.ReadKeys));
    }

    [TestMethod]
    [DataRow(9_000, DisplayName = "the same length")]
    [DataRow(4_000, DisplayName = "another length")]
    public async Task Resume_AFileWhoseContentDiffers_IsLandedUnderThePolicy(int length)
    {
        var seed = (byte)(0xD4 + (length == 9_000 ? 0 : 1));
        var (plan, target, store, keys, catalogue) = await PublishAsync($"differs-{length}", seed);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, $"differs-{length}-out");
        var somebodys = Deterministic(length, 99);
        var landed = Land(output, "data/alpha.bin", somebodys);

        var receipt = await ExecuteAsync(store, keys, catalogue, target, plan, output, Resumed($"differs-{length}-run"));

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.Contains("data/alpha.bin", receipt.Displaced);
        Assert.IsFalse(receipt.Items.Single(item => item.Path == "data/alpha.bin").FoundInPlace);
        SequenceAssert.AreEqual(Alpha, File.ReadAllBytes(landed));
        SequenceAssert.AreEqual(
            somebodys,
            File.ReadAllBytes(Path.Combine(output, ".fbp-displaced", $"differs-{length}-run", "data", "alpha.bin")));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating symlinks needs no privilege on POSIX; on Windows it does")]
    public async Task Resume_ALinkWhereAFileLands_IsNotTheFile_WhateverItPointsAt()
    {
        // A link reports the length of what it holds, so a file as long as
        // the link's target path is one only the link check tells apart,
        // and the link points at the file's own content.
        var elsewhere = Path.Combine(SpoolDirectory, "elsewhere", "alpha.bin");
        var content = Deterministic(System.Text.Encoding.UTF8.GetByteCount(elsewhere), 21);
        var source = new FakeFileSystemSource();
        source.AddFile("data/alpha.bin", content, fileId: 9_001).Metadata = new EntryMetadata { ModifiedAt = Captured };
        var (plan, target, store, keys, catalogue) = await PublishAsync("link", 0xDB, source);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, "link-out");
        Land(SpoolDirectory, "elsewhere/alpha.bin", content);
        var link = Path.Combine(output, "data", "alpha.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, elsewhere);
        Assert.AreEqual(content.Length, new FileInfo(link).Length, "the link must be as long as the file, or its length alone refuses it");

        var receipt = await ExecuteAsync(store, keys, catalogue, target, plan, output, Resumed("link-run"));

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.IsFalse(receipt.Items.Single(item => item.Path == "data/alpha.bin").FoundInPlace);
        Assert.Contains("data/alpha.bin", receipt.Displaced);
        Assert.IsNull(new FileInfo(link).LinkTarget, "the file lands where the link stood");
        SequenceAssert.AreEqual(content, File.ReadAllBytes(link));
    }

    [TestMethod]
    public async Task Resume_UnderFail_AFileAlreadyLanded_IsTheRunsOwnResult()
    {
        // The policy is about somebody else's file. One that already holds
        // exactly what the run would write is what the run is for.
        var (plan, target, store, keys, catalogue) = await PublishAsync("fail", 0xD6);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, "fail-out");
        Land(output, "data/alpha.bin", Alpha);

        var receipt = await ExecuteAsync(
            store, keys, catalogue, target, plan, output,
            Resumed("fail-run") with { ExistingDestination = ExistingDestinationPolicy.Fail });

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.IsTrue(receipt.Items.Single(item => item.Path == "data/alpha.bin").FoundInPlace);
    }

    [TestMethod]
    public async Task Resume_AppliesTheCapturedMetadataAgain_ToAFileLeftInPlace()
    {
        // A run cut short between a file's content and its metadata leaves
        // the content with whatever times it landed with.
        var (plan, target, store, keys, catalogue) = await PublishAsync("metadata", 0xD7);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, "metadata-out");
        var landed = Land(output, "data/alpha.bin", Alpha);
        File.SetLastWriteTimeUtc(landed, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var receipt = await ExecuteAsync(store, keys, catalogue, target, plan, output, Resumed("metadata-run"));

        Assert.IsTrue(receipt.Items.Single(item => item.Path == "data/alpha.bin").FoundInPlace);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds((long)Captured).UtcDateTime,
            File.GetLastWriteTimeUtc(landed));
    }

    [TestMethod]
    public async Task Resume_OfAQuarantinedRun_LandsInTheFolderThatRunLandedIn()
    {
        var (plan, target, store, keys, catalogue) = await PublishAsync("quarantine", 0xD8);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, "quarantine-out");
        var earlier = Path.Combine(output, ".fbp-quarantine", "0123456789abcdef");
        var landed = Land(earlier, "data/alpha.bin", Alpha);
        var before = FileIdOf(landed);

        var receipt = await ExecuteAsync(
            store, keys, catalogue, target, plan, output,
            Resumed("fedcba9876543210") with
            {
                DestinationMode = RestoreDestinationMode.Quarantine,
                ResumedRunId = "0123456789abcdef",
            });

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.AreEqual(new DirectoryInfo(earlier).FullName, receipt.WrittenTo);
        Assert.AreEqual(before, FileIdOf(landed));
        Assert.IsTrue(receipt.Items.Single(item => item.Path == "data/alpha.bin").FoundInPlace);
        SequenceAssert.AreEqual(Beta, File.ReadAllBytes(Path.Combine(earlier, "data", "beta.bin")));
        Assert.IsFalse(
            Directory.Exists(Path.Combine(output, ".fbp-quarantine", "fedcba9876543210")),
            "the resumed run lands in the earlier run's folder, not one of its own");
    }

    [TestMethod]
    public async Task Resume_AHardLinkGroupsFirstNameFoundInPlace_IsTheFileItsLaterNamesLinkTo()
    {
        // ADR-0102: a group's later names are made the file its first name
        // landed as. Found in place, the first name is that file still.
        var content = Deterministic(9_000, 13);
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", content, linkCount: 2, fileId: 9_100).Metadata = new EntryMetadata { ModifiedAt = Captured };
        source.AddFile("docs/b.bin", content, linkCount: 2, fileId: 9_100).Metadata = new EntryMetadata { ModifiedAt = Captured };
        var (plan, target, store, keys, catalogue) = await PublishAsync("links", 0xDA, source);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, "links-out");
        var first = Land(output, "docs/a.bin", content);

        var receipt = await ExecuteAsync(store, keys, catalogue, target, plan, output, Resumed("links-run"));

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.IsTrue(receipt.Items.Single(item => item.Path == "docs/a.bin").FoundInPlace);
        Assert.AreEqual("docs/a.bin", receipt.Items.Single(item => item.Path == "docs/b.bin").LinkedTo);
        Assert.AreEqual(FileIdOf(first), FileIdOf(Path.Combine(output, "docs", "b.bin")), "the later name is the same file");
    }

    [TestMethod]
    public async Task Receipt_SaysWhichFilesWereFoundInPlace()
    {
        var (plan, target, store, keys, catalogue) = await PublishAsync("receipt", 0xD9);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, "receipt-out");
        Land(output, "data/alpha.bin", Alpha);

        var receipt = await ExecuteAsync(store, keys, catalogue, target, plan, output, Resumed("receipt-run"));

        // Schema 8: a file this run did not write says so, and one it did
        // carries nothing new.
        Assert.AreEqual(8, RestoreReceipt.CurrentSchemaVersion);
        Assert.AreEqual(8, receipt.SchemaVersion);
        var json = receipt.ToJson();
        Assert.AreEqual(1, json.Split("\"found_in_place\"").Length - 1, json);
        Assert.Contains("\"found_in_place\": true", json, StringComparison.Ordinal);
    }

    private static bool IsContent(string key) => key.StartsWith("blobs/data/", StringComparison.Ordinal);

    private static RestoreExecutionOptions Fresh(string runId) => new()
    {
        DestinationMode = RestoreDestinationMode.InPlace,
        RunId = runId,
        NowUnixMilliseconds = 1_722_700_000_000,
    };

    private static RestoreExecutionOptions Resumed(string runId) => Fresh(runId) with { Resume = true };

    private static string Land(string root, string path, byte[] content)
    {
        var landed = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(landed)!);
        File.WriteAllBytes(landed, content);
        return landed;
    }

    private static ulong FileIdOf(string path)
    {
        Assert.IsTrue(LocalFileSystemSource.TryStat(path, out var stat), path);
        return stat.FileId;
    }

    private Task<(RestorePlan Plan, RestoreTargetProfile Target, Storage.Local.LocalFileSystemObjectStore Store, RepositoryKeySet Keys, CatalogueDb Catalogue)>
        PublishAsync(string name, byte seed)
    {
        var source = new FakeFileSystemSource();
        source.AddFile("data/alpha.bin", Alpha, fileId: 9_001).Metadata = new EntryMetadata { ModifiedAt = Captured };
        source.AddFile("data/beta.bin", Beta, fileId: 9_002).Metadata = new EntryMetadata { ModifiedAt = Captured };
        return PublishAsync(name, seed, source);
    }

    private async Task<(RestorePlan Plan, RestoreTargetProfile Target, Storage.Local.LocalFileSystemObjectStore Store, RepositoryKeySet Keys, CatalogueDb Catalogue)>
        PublishAsync(string name, byte seed, FakeFileSystemSource source)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"catalogue-{name}.db"), Repo);
        await CreateOrchestrator(store, keys, credential, catalogue, name)
            .PublishAsync(Job(source, seed), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, target);
        return (plan, target, store, keys, catalogue);
    }

    private static async Task<RestoreReceipt> ExecuteAsync(
        IObjectStore store,
        RepositoryKeySet keys,
        CatalogueDb catalogue,
        RestoreTargetProfile target,
        RestorePlan plan,
        string output,
        RestoreExecutionOptions options)
    {
        // Read as a run reads: straight to where the catalogue says each
        // record is, so the executor reads ahead of its loop.
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);
        return await new RestoreExecutor(reader, target).ExecuteAsync(plan, output, options, CancellationToken.None);
    }

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, RepositoryWriteCredential credential, CatalogueDb catalogue, string spoolName)
    {
        var spool = Path.Combine(SpoolDirectory, spoolName);
        Directory.CreateDirectory(spool);

        return new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool,
            FormatVersions.SealedDataPlane, observer: null, catalogue);
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte seed) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "restore-resume-tests/1.0",
    };

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }
}
