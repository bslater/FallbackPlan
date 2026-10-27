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
/// landed in, and the receipt says which files were found in place. Under
/// rename a resume looks beside a file in the way too, where the restore
/// writes its copy, and finds there a copy of the snapshot's content under a
/// name the restore would have written (ADR-0109 Amendment 1).
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
    public async Task Resume_UnderRename_TheCopyAnEarlierRunWroteBeside_IsFoundThere_AndNoSecondIsWritten()
    {
        // ADR-0109 Amendment 1: under rename a file in the way is kept and
        // the restored copy written beside it, named for its day. A resume
        // looks there too, whatever day the earlier run wrote it.
        var (plan, target, store, keys, catalogue) = await PublishAsync("beside", 0xDC);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, "beside-out");
        var live = Land(output, "data/alpha.bin", Deterministic(9_000, 99));
        var earlier = Land(output, "data/alpha (restored 2024-08-02).bin", Alpha);
        var before = FileIdOf(earlier);

        var receipt = await ExecuteAsync(
            store, keys, catalogue, target, plan, output, Resumed("beside-run") with { ExistingDestination = ExistingDestinationPolicy.WriteBeside });

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        var found = receipt.Items.Single(item => item.Path == "data/alpha.bin");
        Assert.IsTrue(found.FoundInPlace);
        Assert.AreEqual("data/alpha (restored 2024-08-02).bin", found.WrittenAs, "the receipt says where the copy found stands");
        Assert.AreEqual(before, FileIdOf(earlier));
        Assert.IsFalse(File.Exists(Path.Combine(output, "data", "alpha (restored 2024-08-03).bin")), "no second copy is written beside");
        SequenceAssert.AreEqual(Deterministic(9_000, 99), File.ReadAllBytes(live));
    }

    [TestMethod]
    public async Task Resume_UnderRename_ACopyBesideWhoseContentDiffers_IsLeft_AndTheRestoredCopyIsWrittenBesideToo()
    {
        var (plan, target, store, keys, catalogue) = await PublishAsync("beside-differs", 0xDD);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, "beside-differs-out");
        Land(output, "data/alpha.bin", Deterministic(9_000, 99));
        var somebodys = Land(output, "data/alpha (restored 2024-08-02).bin", Deterministic(9_000, 98));

        var receipt = await ExecuteAsync(
            store, keys, catalogue, target, plan, output, Resumed("beside-differs-run") with { ExistingDestination = ExistingDestinationPolicy.WriteBeside });

        var written = receipt.Items.Single(item => item.Path == "data/alpha.bin");
        Assert.IsFalse(written.FoundInPlace);
        Assert.AreEqual("data/alpha (restored 2024-08-03).bin", written.WrittenAs);
        SequenceAssert.AreEqual(Alpha, File.ReadAllBytes(Path.Combine(output, "data", "alpha (restored 2024-08-03).bin")));
        SequenceAssert.AreEqual(Deterministic(9_000, 98), File.ReadAllBytes(somebodys));
    }

    [TestMethod]
    [DataRow("alpha (restored 2024-08-02) copy.bin", DisplayName = "more after the date")]
    [DataRow("alpha (restored not-a-date).bin", DisplayName = "no date")]
    [DataRow("alpha (restored 2024-08-02-101).bin", DisplayName = "past the hundredth")]
    [DataRow("alpha (restored 2024-08-02-02).bin", DisplayName = "a counter written otherwise")]
    [DataRow("alpha (restored 2024-08-02_2).bin", DisplayName = "a counter after something else")]
    [DataRow("alphx (restored 2024-08-02).bin", DisplayName = "another file's")]
    [DataRow("alpha (restored 2024-08-02).txt", DisplayName = "another extension")]
    public async Task Resume_UnderRename_ANameTheRestoreWouldNotHaveWritten_IsNotItsCopy(string name)
    {
        // Only a name the restore writes beside is the restore's: the stem,
        // " (restored ", a date, a counter of at most a hundred, ")", the
        // extension.
        var label = $"beside-name-{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..8]}";
        var (plan, target, store, keys, catalogue) = await PublishAsync(label, 0xDE);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, label + "-out");
        Land(output, "data/alpha.bin", Deterministic(9_000, 99));
        Land(output, "data/" + name, Alpha);

        var receipt = await ExecuteAsync(
            store, keys, catalogue, target, plan, output, Resumed("beside-name-run") with { ExistingDestination = ExistingDestinationPolicy.WriteBeside });

        var written = receipt.Items.Single(item => item.Path == "data/alpha.bin");
        Assert.IsFalse(written.FoundInPlace, name);
        Assert.AreEqual("data/alpha (restored 2024-08-03).bin", written.WrittenAs);
    }

    [TestMethod]
    public async Task Resume_UnderRename_ACountedCopyBeside_IsFoundToo()
    {
        var (plan, target, store, keys, catalogue) = await PublishAsync("beside-counted", 0xDF);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, "beside-counted-out");
        Land(output, "data/alpha.bin", Deterministic(9_000, 99));
        Land(output, "data/alpha (restored 2024-08-02).bin", Deterministic(9_000, 98));
        Land(output, "data/alpha (restored 2024-08-02-2).bin", Alpha);

        var receipt = await ExecuteAsync(
            store, keys, catalogue, target, plan, output, Resumed("beside-counted-run") with { ExistingDestination = ExistingDestinationPolicy.WriteBeside });

        var found = receipt.Items.Single(item => item.Path == "data/alpha.bin");
        Assert.IsTrue(found.FoundInPlace);
        Assert.AreEqual("data/alpha (restored 2024-08-02-2).bin", found.WrittenAs);
    }

    [TestMethod]
    public async Task Resume_UnderRename_WithNothingInTheWay_LandsTheFileWhereItGoes()
    {
        // Rename writes beside only a file in the way. With none there now,
        // this run would land the file at its destination, so that is where
        // a resume looks, and where the file lands.
        var (plan, target, store, keys, catalogue) = await PublishAsync("beside-clear", 0xE2);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, "beside-clear-out");
        Land(output, "data/alpha (restored 2024-08-02).bin", Alpha);

        var receipt = await ExecuteAsync(
            store, keys, catalogue, target, plan, output, Resumed("beside-clear-run") with { ExistingDestination = ExistingDestinationPolicy.WriteBeside });

        var written = receipt.Items.Single(item => item.Path == "data/alpha.bin");
        Assert.IsFalse(written.FoundInPlace);
        Assert.IsNull(written.WrittenAs);
        SequenceAssert.AreEqual(Alpha, File.ReadAllBytes(Path.Combine(output, "data", "alpha.bin")));
    }

    [TestMethod]
    public async Task Resume_UnderRename_OfTwoCopiesBesideBothHoldingIt_TheFirstByNameIsTaken()
    {
        var (plan, target, store, keys, catalogue) = await PublishAsync("beside-two", 0xE3);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, "beside-two-out");
        Land(output, "data/alpha.bin", Deterministic(9_000, 99));
        Land(output, "data/alpha (restored 2024-08-02).bin", Alpha);
        Land(output, "data/alpha (restored 2024-08-01).bin", Alpha);

        var receipt = await ExecuteAsync(
            store, keys, catalogue, target, plan, output, Resumed("beside-two-run") with { ExistingDestination = ExistingDestinationPolicy.WriteBeside });

        Assert.AreEqual("data/alpha (restored 2024-08-01).bin", receipt.Items.Single(item => item.Path == "data/alpha.bin").WrittenAs);
    }

    [TestMethod]
    public async Task WithoutResume_UnderRename_ACopyBesideHoldingTheSnapshot_IsNotThisRunsOwn()
    {
        var (plan, target, store, keys, catalogue) = await PublishAsync("beside-fresh", 0xE0);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, "beside-fresh-out");
        Land(output, "data/alpha.bin", Deterministic(9_000, 99));
        Land(output, "data/alpha (restored 2024-08-02).bin", Alpha);

        var receipt = await ExecuteAsync(
            store, keys, catalogue, target, plan, output, Fresh("beside-fresh-run") with { ExistingDestination = ExistingDestinationPolicy.WriteBeside });

        var written = receipt.Items.Single(item => item.Path == "data/alpha.bin");
        Assert.IsFalse(written.FoundInPlace);
        Assert.AreEqual("data/alpha (restored 2024-08-03).bin", written.WrittenAs);
    }

    [TestMethod]
    public async Task Resume_UnderAnotherPolicy_NoCopyBesideIsLookedFor()
    {
        // Only rename writes beside, so only a resume under it looks there.
        var (plan, target, store, keys, catalogue) = await PublishAsync("beside-preserve", 0xE1);
        using var _ = keys;
        using var __ = catalogue;
        var output = Path.Combine(SpoolDirectory, "beside-preserve-out");
        Land(output, "data/alpha.bin", Deterministic(9_000, 99));
        Land(output, "data/alpha (restored 2024-08-02).bin", Alpha);

        var receipt = await ExecuteAsync(store, keys, catalogue, target, plan, output, Resumed("beside-preserve-run"));

        var written = receipt.Items.Single(item => item.Path == "data/alpha.bin");
        Assert.IsFalse(written.FoundInPlace);
        Assert.IsNull(written.WrittenAs);
        Assert.Contains("data/alpha.bin", receipt.Displaced);
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

        // Since schema 8, a file this run did not write says so, and one it did
        // carries nothing new.
        Assert.AreEqual(9, RestoreReceipt.CurrentSchemaVersion);
        Assert.AreEqual(9, receipt.SchemaVersion);
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
