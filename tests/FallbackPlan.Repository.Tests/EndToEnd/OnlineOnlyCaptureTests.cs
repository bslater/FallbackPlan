using System.Text;
using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Online-only files, whose content a cloud provider keeps and fetches when
/// they are read (ADR-0185, FR-SNP-009). Once the provider fails to deliver
/// one, the run fetches no other online-only file: each is named as not
/// fetched, with the file that failed and why, and the next backup fetches it
/// again. An unchanged online-only file is never fetched at all
/// (NFR-PERF-003). A file gone or refused, or a file on this device that will
/// not read, is not the provider failing and stops nothing. The provider's
/// bits, which say whether the content is here and whether the person wants
/// it kept here, are not kept with a file or a folder, so going between
/// online-only and on the device is no change (ADR-0185 Amendment 1). A
/// file the run fetched, and read whole as the scan found it, is freed from
/// the device again; one not fetched, or changed while it was read, is not,
/// and a refusal to free it fails nothing (ADR-0185 Amendment 2). A changed
/// online-only file the run did not fetch keeps the version backed up before
/// it, named in the failure with when it was last modified, and the next
/// backup asks for it again even where nothing is said to have changed; a
/// file new to the set has none to keep, a file on the device that will not
/// read keeps none, and nor does one found only under another name
/// (ADR-0185 Amendment 3). A run says how many online-only files it did not
/// fetch, how many of them kept the version before, and which file's fetch
/// failed and why, and says nothing where it fetched every one (ADR-0193,
/// FR-SNP-014). A restore's plan names each file whose version is one backed
/// up before, standing in for one its backup could not fetch, with what the
/// backup said of it, and names no other (ADR-0194, FR-RST-003). A
/// snapshot's failures are read as the paths they name, for the count of
/// what the next backup will try again (ADR-0192 Amendment 1, FR-SNP-013).
/// A restore's receipt names the same files, each with what the backup said
/// of it, and says where the snapshot's failures would not read
/// (ADR-0194 Amendment 1, FR-RST-004). A run whose every failure is an
/// online-only file that kept its version before is behind, not partial; one
/// that failed a file new to the set, or a file on the device, is not
/// (ADR-0193 Amendment 1, FR-SNP-014). A set that leaves its online-only
/// files to their cloud provider fetches none: one new or changed is left out
/// of the snapshot and counted, and fails nothing, while one unchanged keeps
/// the version backed up, and one only moved is still taken without a fetch
/// (ADR-0195, FR-SNP-015). A folder its provider holds, whose listing does
/// not come, keeps its tree from the backup before, in a full walk and in a
/// run told it changed, and the failure says so; one new to the set has no
/// tree to keep, and the next backup lists it again even where nothing is
/// said to have changed. In a set of several folders it is kept at its path
/// under its folder's label, and it counts among the folders the run did
/// not walk. A comparison counts it a failure and names none of its files
/// deleted (ADR-0185 Amendment 6, FR-SNP-009).
/// </summary>
/// <remarks>
/// The fake source is what makes the provider's failure assertable on every
/// platform: a real one needs a cloud provider on the machine, which no CI
/// runner has.
/// </remarks>
[TestClass]
public sealed class OnlineOnlyCaptureTests : ArchiveTestHarness
{
    private const string TimedOut = "The cloud operation was not completed before the time-out period expired.";

    [TestMethod]
    public async Task AnOnlineOnlyFileNotDelivered_StopsTheRunFetchingTheRest()
    {
        var source = new FakeFileSystemSource();
        var first = Online(source, "a.bin", 1);
        first.OpenFailure = new IOException(TimedOut);
        var second = Online(source, "b.bin", 2);
        source.AddFile("local.bin", Deterministic(4_000, 3));
        var below = Online(source, "sub/c.bin", 4);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        // The provider was asked once. Asked for each file, it would have
        // spent its whole time-out on every one of them.
        Assert.AreEqual(1, first.OpenAttempts);
        Assert.AreEqual(0, second.OpenAttempts, "b.bin was not fetched");
        Assert.AreEqual(0, below.OpenAttempts, "sub/c.bin was not fetched");

        // The file on this device is still backed up.
        Assert.AreEqual("local.bin", Assert.ContainsSingle(published.Files).RelativePath);

        Assert.HasCount(3, published.Failures);
        var failed = Detail(published, "a.bin");
        Assert.Contains("online-only", failed, StringComparison.Ordinal);
        Assert.Contains(TimedOut, failed, StringComparison.Ordinal);
        foreach (var path in new[] { "b.bin", "sub/c.bin" })
        {
            var notFetched = Detail(published, path);
            Assert.Contains("not fetched", notFetched, StringComparison.Ordinal);
            Assert.Contains("a.bin", notFetched, StringComparison.Ordinal);
            Assert.Contains(TimedOut, notFetched, StringComparison.Ordinal);
            Assert.AreEqual(CaptureFailureReason.IoError, ReasonFor(published, path));
        }
    }

    [TestMethod]
    public async Task AFetchThatFailsPartWay_StopsTheRunToo()
    {
        // A provider that starts to deliver and stops is failing as surely as
        // one that never starts.
        var source = new FakeFileSystemSource();
        var first = Online(source, "a.bin", 1, length: 200_000);
        first.FailReadAfterBytes = 8 * 1024;
        first.ReadFailure = new IOException(TimedOut);
        var second = Online(source, "b.bin", 2);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        Assert.AreEqual(0, second.OpenAttempts, "b.bin was not fetched");
        Assert.IsEmpty(published.Files);
        Assert.Contains("a.bin", Detail(published, "b.bin"), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AFileGoneOrRefusedOrOnThisDevice_StopsNoFetching()
    {
        // None of these is the provider failing: a file deleted since the
        // scan, a file this account may not read, and a file on this device
        // that will not read. Each fails alone, as it always has.
        var source = new FakeFileSystemSource();
        Online(source, "a-gone.bin", 1).OpenFailure = new FileNotFoundException("no longer there");
        Online(source, "b-denied.bin", 2).OpenFailure = new UnauthorizedAccessException("denied");
        source.AddFile("c-local.bin", Deterministic(4_000, 3)).OpenFailure = new IOException("a bad sector");
        var fetched = Online(source, "d.bin", 4);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        Assert.AreEqual(1, fetched.Opens);
        Assert.AreEqual("d.bin", Assert.ContainsSingle(published.Files).RelativePath);
        Assert.HasCount(3, published.Failures);
        Assert.AreEqual("a bad sector", Detail(published, "c-local.bin"));
        foreach (var failure in published.Failures)
        {
            Assert.DoesNotContain("not fetched", failure.Detail, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task AFileNotFetched_IsFetchedByTheNextBackup()
    {
        var source = new FakeFileSystemSource();
        var first = Online(source, "a.bin", 1);
        first.OpenFailure = new IOException(TimedOut);
        var second = Online(source, "b.bin", 2);
        source.AddFile("local.bin", Deterministic(4_000, 3));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        // The provider answers again.
        first.OpenFailure = null;
        var published = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                    ParentSnapshots = [Enumerable.Repeat((byte)0xA1, 16).ToArray()],
                },
                CancellationToken.None);

        Assert.IsEmpty(published.Failures);
        var byPath = published.Files.ToDictionary(file => file.RelativePath);
        Assert.IsFalse(byPath["a.bin"].Reused);
        Assert.IsFalse(byPath["b.bin"].Reused);
        Assert.IsTrue(byPath["local.bin"].Reused);
        Assert.AreEqual(1, first.Opens);
        Assert.AreEqual(1, second.Opens);
    }

    [TestMethod]
    public async Task AnUnchangedOnlineOnlyFile_IsNeverFetchedAgain()
    {
        // What makes fetching on demand affordable: a file is fetched when it
        // is new or changed, and once (NFR-PERF-003).
        var source = new FakeFileSystemSource();
        var kept = Online(source, "kept.bin", 1);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        Assert.AreEqual(1, kept.OpenAttempts);

        var published = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                    ParentSnapshots = [Enumerable.Repeat((byte)0xA1, 16).ToArray()],
                },
                CancellationToken.None);

        Assert.AreEqual(1, kept.OpenAttempts, "an unchanged online-only file is not fetched again");
        Assert.IsTrue(Assert.ContainsSingle(published.Files).Reused);
    }

    [TestMethod]
    public async Task AFileGoingBetweenOnlineOnlyAndOnTheDevice_IsNoChange()
    {
        var source = new FakeFileSystemSource();
        var doc = Online(source, "doc.bin", 1);
        doc.Metadata = doc.Metadata with
        {
            FileAttributes = Archive | ReparsePoint | Offline | RecallOnDataAccess | Unpinned,
        };

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        // The person keeps it on the device now.
        doc.OnlineOnly = false;
        doc.Metadata = doc.Metadata with { FileAttributes = Archive | ReparsePoint | Pinned };
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                    ParentSnapshots = [Enumerable.Repeat((byte)0xA1, 16).ToArray()],
                },
                CancellationToken.None);

        var kept = Assert.ContainsSingle(second.Files);
        Assert.IsTrue(kept.Reused, "where the provider keeps the content is not a change to the file");
        Assert.AreEqual(Assert.ContainsSingle(first.Files).ObjectId, kept.ObjectId);
        Assert.AreEqual(1, doc.OpenAttempts);

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var read = await reader.ReadSegmentAsync(kept.ObjectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        Assert.AreEqual(
            Archive | ReparsePoint,
            FileVersionManifestCodec.Decode(read.Plaintext!).Metadata.FileAttributes,
            "the provider's bits are not kept");
    }

    [TestMethod]
    public async Task AFolderGoingBetweenOnlineOnlyAndOnTheDevice_IsNoChange()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(4_000, 1));
        source.Folders["docs"] = new EntryMetadata
        {
            ModifiedAt = 1_722_000_000_000,
            FileAttributes = Directory | RecallOnOpen | Unpinned,
        };

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        source.Folders["docs"] = source.Folders["docs"] with { FileAttributes = Directory | Pinned };
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                    ParentSnapshots = [Enumerable.Repeat((byte)0xA1, 16).ToArray()],
                },
                CancellationToken.None);

        Assert.AreEqual(first.RootTreeObjectId, second.RootTreeObjectId, "the folder's tree is the one it had");
    }

    [TestMethod]
    public async Task AFileTheRunFetched_IsFreedAgain_AndNoOtherIs()
    {
        var source = new FakeFileSystemSource();
        Online(source, "fetched.bin", 1);
        source.AddFile("local.bin", Deterministic(4_000, 2));
        Online(source, "unfetched.bin", 3).OpenFailure = new IOException(TimedOut);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        // Only what the run itself fetched. A file on the device was never
        // the provider's to free, and one not fetched is as it was.
        Assert.AreEqual("fetched.bin", Assert.ContainsSingle(source.FreedPaths));
    }

    [TestMethod]
    public async Task AnUnchangedOnlineOnlyFile_IsNotFreedAgainByALaterBackup()
    {
        var source = new FakeFileSystemSource();
        Online(source, "kept.bin", 1);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                    ParentSnapshots = [Enumerable.Repeat((byte)0xA1, 16).ToArray()],
                },
                CancellationToken.None);

        // The later backup fetched nothing, so it has nothing to give back:
        // whatever the person did with the file since is theirs.
        Assert.AreEqual("kept.bin", Assert.ContainsSingle(source.FreedPaths));
    }

    [TestMethod]
    public async Task AFileThatChangedOrWasReplacedWhileFetched_IsNotFreedAgain()
    {
        // Freeing one the person was writing, or another object at its name,
        // would act on something the run did not read whole.
        var source = new FakeFileSystemSource();
        Online(source, "edited.bin", 1).RevalidationChangesRemaining = 5;
        Online(source, "replaced.bin", 2).SubstitutedIdentity = new ScanIdentity(Device: 7, FileId: 9_999, LinkCount: 1);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        Assert.HasCount(2, published.Files);
        Assert.IsEmpty(source.FreedPaths);
    }

    [TestMethod]
    public async Task AFreeingRefused_FailsNothing()
    {
        var source = new FakeFileSystemSource();
        Online(source, "kept-here.bin", 1).FreeRefusal = "The cloud file provider is not running.";

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        Assert.AreEqual("kept-here.bin", Assert.ContainsSingle(published.Files).RelativePath);
        Assert.IsEmpty(published.Failures);
        Assert.AreEqual("kept-here.bin", Assert.ContainsSingle(source.FreedPaths));
    }

    [TestMethod]
    public async Task AChangedFileNotFetched_KeepsTheVersionBackedUpBefore()
    {
        var source = new FakeFileSystemSource();
        var doc = Online(source, "docs/doc.bin", 1);
        var other = Online(source, "docs/other.bin", 2);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        var before = first.Files.ToDictionary(file => file.RelativePath);

        // Both change where the provider keeps them, and then it stops
        // delivering.
        Change(doc, 5);
        Change(other, 6);
        doc.OpenFailure = new IOException(TimedOut);
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001), CancellationToken.None);

        var after = second.Files.ToDictionary(file => file.RelativePath);
        Assert.AreEqual(before["docs/doc.bin"].ObjectId, after["docs/doc.bin"].ObjectId, "the file whose fetch failed keeps the version before");
        Assert.AreEqual(before["docs/other.bin"].ObjectId, after["docs/other.bin"].ObjectId, "so does one the run did not ask for after it");
        Assert.AreEqual(1, other.OpenAttempts, "other.bin was not fetched again");

        // The failure still says what was not backed up, and now what stands
        // in its place.
        Assert.HasCount(2, second.Failures);
        Assert.Contains(TimedOut, Detail(second, "docs/doc.bin"), StringComparison.Ordinal);
        Assert.Contains("not fetched", Detail(second, "docs/other.bin"), StringComparison.Ordinal);
        foreach (var path in new[] { "docs/doc.bin", "docs/other.bin" })
        {
            Assert.Contains(
                "the version backed up earlier, last modified 2024-07-26 13:20 UTC, stands in its place",
                Detail(second, path),
                StringComparison.Ordinal);
        }

        // As it was when it was backed up, so the next backup sees it changed,
        // and with its own length, which no later record overwrites.
        var kept = catalogue.LookupPath(Snapshot(0xB2), "docs/doc.bin")!;
        Assert.AreEqual(1_722_000_000_000UL, kept.ModifiedAt);
        Assert.AreEqual(4_000UL, kept.LogicalLength);
    }

    [TestMethod]
    public async Task AFileKeptFromBefore_IsAskedForAgain_WhileTheProviderStaysAway()
    {
        var source = new FakeFileSystemSource();
        var doc = Online(source, "doc.bin", 1);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        Change(doc, 5);
        doc.OpenFailure = new IOException(TimedOut);
        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001), CancellationToken.None);

        var third = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xC3, 0xB2, now: 1_722_800_000_001), CancellationToken.None);

        Assert.AreEqual(3, doc.OpenAttempts, "each backup asks the provider again");
        Assert.AreEqual(Assert.ContainsSingle(first.Files).ObjectId, Assert.ContainsSingle(third.Files).ObjectId);
        Assert.Contains("stands in its place", Detail(third, "doc.bin"), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AFileKeptFromBefore_IsFetchedOnceTheProviderAnswers_ThoughNothingIsSaidToHaveChanged()
    {
        var source = new FakeFileSystemSource();
        var doc = Online(source, "docs/doc.bin", 1);
        source.AddFile("photos/p.bin", Deterministic(4_000, 2));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        Change(doc, 5);
        doc.OpenFailure = new IOException(TimedOut);
        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001), CancellationToken.None);

        // The provider answers again, and the change journal names nothing:
        // the change it heard was the last backup's to take.
        doc.OpenFailure = null;
        var third = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xC3, 0xB2, now: 1_722_800_000_001) with { ChangedFolders = [] }, CancellationToken.None);

        Assert.IsEmpty(third.Failures);
        var fetched = third.Files.Single(file => file.RelativePath == "docs/doc.bin");
        Assert.IsFalse(fetched.Reused);
        Assert.AreNotEqual(first.Files.Single(file => file.RelativePath == "docs/doc.bin").ObjectId, fetched.ObjectId);
        Assert.AreEqual(2, doc.Opens, "fetched by the first backup, and again by the third");
    }

    [TestMethod]
    public async Task AFileOnTheDeviceThatWillNotRead_KeepsNoVersionBefore()
    {
        // The owner kept this to what a provider holds: a file here that will
        // not read is named and left out, as it always has been.
        var source = new FakeFileSystemSource();
        var local = source.AddFile("local.bin", Deterministic(4_000, 1));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        Change(local, 5);
        local.OpenFailure = new IOException("a bad sector");
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001), CancellationToken.None);

        Assert.IsEmpty(second.Files);
        Assert.AreEqual("a bad sector", Detail(second, "local.bin"));
    }

    [TestMethod]
    public async Task AFileFoundOnlyUnderAnotherName_KeepsNoVersionBefore()
    {
        // A file moved as well as changed: the version before is under its old
        // name, and a manifest states its own name, so it is not put here.
        var source = new FakeFileSystemSource();
        source.AddFile("old/doc.bin", Deterministic(4_000, 1), fileId: 500).OnlineOnly = true;

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        source.Remove("old/doc.bin");
        var moved = source.AddFile("new/doc.bin", Deterministic(4_100, 5), fileId: 500);
        moved.OnlineOnly = true;
        moved.Metadata = moved.Metadata with { ModifiedAt = 1_722_000_060_000 };
        moved.OpenFailure = new IOException(TimedOut);
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001), CancellationToken.None);

        Assert.IsEmpty(second.Files);
        Assert.DoesNotContain("stands in its place", Detail(second, "new/doc.bin"), StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "a full walk")]
    [DataRow(true, DisplayName = "a run told the folder changed")]
    public async Task AFolderItsProviderHolds_WhoseListingDoesNotCome_KeepsItsTreeBefore(bool told)
    {
        // A provider can keep a folder's listing as it keeps a file's
        // content, and listing the folder asks it for the names. Where it
        // does not answer, the folder keeps its tree from the backup before,
        // as a file not fetched keeps its version, rather than going empty.
        var source = new FakeFileSystemSource();
        source.AddFile("cloud/a.bin", Deterministic(4_000, 1));
        source.AddFile("cloud/deep/b.bin", Deterministic(4_000, 2));
        source.AddFile("local.bin", Deterministic(4_000, 3));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        source.UnlistedFolders["cloud"] = TimedOut;
        var job = After(source, 0xB2, 0xA1, now: 1_722_700_000_001);
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(told ? job with { ChangedFolders = ["cloud"] } : job, CancellationToken.None);

        Assert.AreEqual(
            catalogue.LookupPath(Snapshot(0xA1), "cloud")!.ObjectId,
            catalogue.LookupPath(Snapshot(0xB2), "cloud")?.ObjectId,
            "the folder's tree is the one backed up before");
        foreach (var path in new[] { "cloud/a.bin", "cloud/deep/b.bin" })
        {
            Assert.AreEqual(
                catalogue.LookupPath(Snapshot(0xA1), path)!.ObjectId,
                catalogue.LookupPath(Snapshot(0xB2), path)?.ObjectId,
                $"{path} is restored from the newest snapshot as it was backed up before");
        }

        // The failure still says the folder was not listed, and what stands
        // in its place.
        Assert.AreEqual(
            $"online-only, and listing it failed: {TimedOut}; the folder as backed up earlier stands in its place",
            Detail(second, "cloud"));
        Assert.AreEqual(CaptureFailureReason.IoError, ReasonFor(second, "cloud"));
        Assert.HasCount(1, second.Failures);
        Assert.AreEqual(1, second.FoldersReused, "the folder kept is one the run did not walk");
    }

    [TestMethod]
    public async Task AFolderItsProviderHolds_InASetOfSeveralFolders_KeepsItsTreeBefore_AtItsPathUnderItsLabel()
    {
        // A set of several folders names each under its label (ADR-0040),
        // and so does the folder kept and its failure.
        var source = new FakeFileSystemSource();
        source.AddFile("alpha/cloud/a.bin", Deterministic(4_000, 1));
        source.AddFile("alpha/local.bin", Deterministic(4_000, 3));
        source.AddFile("bravo/p.bin", Deterministic(4_000, 4));
        IReadOnlyList<ScanRoot> roots = [new ScanRoot("alpha", "Docs"), new ScanRoot("bravo", "Photos")];

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1) with { Roots = roots }, CancellationToken.None);

        source.UnlistedFolders["alpha/cloud"] = TimedOut;
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001) with { Roots = roots }, CancellationToken.None);

        Assert.AreEqual(
            catalogue.LookupPath(Snapshot(0xA1), "Docs/cloud/a.bin")!.ObjectId,
            catalogue.LookupPath(Snapshot(0xB2), "Docs/cloud/a.bin")?.ObjectId);
        Assert.Contains("stands in its place", Detail(second, "Docs/cloud"), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AFolderItsProviderHolds_NewToTheSet_WhoseListingDoesNotCome_HasNoTreeToKeep()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("local.bin", Deterministic(4_000, 3));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        source.AddFile("cloud/a.bin", Deterministic(4_000, 1));
        source.UnlistedFolders["cloud"] = TimedOut;
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001), CancellationToken.None);

        Assert.AreEqual(
            EntryKind.DirectoryPlaceholder,
            catalogue.LookupPath(Snapshot(0xB2), "cloud")?.EntryKind,
            "the folder is still in the snapshot, as a folder not listed was before");
        Assert.IsNull(catalogue.LookupPath(Snapshot(0xB2), "cloud/a.bin"));
        Assert.AreEqual($"online-only, and listing it failed: {TimedOut}", Detail(second, "cloud"));
    }

    [TestMethod]
    public async Task AFolderKeptFromBefore_IsListedByTheNextBackup_ThoughNothingIsSaidToHaveChanged()
    {
        var source = new FakeFileSystemSource();
        var a = source.AddFile("cloud/a.bin", Deterministic(4_000, 1));
        source.AddFile("local.bin", Deterministic(4_000, 3));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        source.UnlistedFolders["cloud"] = TimedOut;
        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001), CancellationToken.None);

        // The provider answers again, and a file in the folder changed while
        // it was away; the change journal names nothing.
        source.UnlistedFolders.Remove("cloud");
        Change(a, 5);
        source.ListedFolders.Clear();
        var third = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xC3, 0xB2, now: 1_722_800_000_001) with { ChangedFolders = [] }, CancellationToken.None);

        Assert.Contains("cloud", source.ListedFolders, "the folder was listed again");
        Assert.IsEmpty(third.Failures);
        Assert.AreNotEqual(
            catalogue.LookupPath(Snapshot(0xA1), "cloud/a.bin")!.ObjectId,
            catalogue.LookupPath(Snapshot(0xC3), "cloud/a.bin")?.ObjectId,
            "the file changed while the folder was not listed is taken");
    }

    [TestMethod]
    public async Task AComparison_CountsAFolderWhoseListingDoesNotCome_AsAFailure_AndNamesNoneOfItsFilesDeleted()
    {
        // What the backup would keep is not what the disk lost.
        var source = new FakeFileSystemSource();
        source.AddFile("cloud/a.bin", Deterministic(4_000, 1));
        source.AddFile("local.bin", Deterministic(4_000, 3));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        source.UnlistedFolders["cloud"] = TimedOut;

        var comparison = await SourceComparer.CompareAsync(
            source, [new ScanRoot("/")], [], [], catalogue, Snapshot(0xA1), sampleLimit: 20, CancellationToken.None);

        Assert.AreEqual(1, comparison.Failures);
        Assert.AreEqual(0, comparison.Deleted.Count, string.Join(", ", comparison.Deleted.Sample));
        Assert.AreEqual(1, comparison.Unchanged);
    }

    [TestMethod]
    public async Task ARun_SaysHowManyItDidNotFetch_AndHowManyKeptTheirVersionBefore()
    {
        var source = new FakeFileSystemSource();
        var doc = Online(source, "docs/doc.bin", 1);
        var other = Online(source, "docs/other.bin", 2);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        Assert.IsNull(first.NotFetched, "a run that fetched every one says nothing");

        Change(doc, 5);
        Change(other, 6);
        Online(source, "docs/new.bin", 3);
        doc.OpenFailure = new IOException(TimedOut);
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001), CancellationToken.None);

        Assert.AreEqual(new NotFetchedFiles(Files: 3, KeptEarlier: 2, Path: "docs/doc.bin", Reason: TimedOut), second.NotFetched);
    }

    [TestMethod]
    public async Task ARunThatKeptTheEarlierVersionOfEachFileItDidNotFetch_IsBehind_AndOneThatFailedAnyOtherIsNot()
    {
        var source = new FakeFileSystemSource();
        var doc = Online(source, "docs/doc.bin", 1);
        var local = source.AddFile("docs/local.bin", Deterministic(4_000, 7));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var clean = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        Assert.IsFalse(clean.Behind, "a run that failed nothing is complete, not behind");

        // Every failure is a file whose version before stands in its place.
        Change(doc, 5);
        doc.OpenFailure = new IOException(TimedOut);
        var behind = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001), CancellationToken.None);
        Assert.IsTrue(behind.Behind);

        // A file on the device that will not read is no provider's.
        local.OpenFailure = new IOException("a bad sector");
        Change(local, 8);
        var unreadable = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xC3, 0xB2, now: 1_722_700_000_002), CancellationToken.None);
        Assert.IsFalse(unreadable.Behind, "a file on the device that would not read");

        // A file new to the set has no version before to keep.
        local.OpenFailure = null;
        Online(source, "docs/new.bin", 3);
        var unkept = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xD4, 0xC3, now: 1_722_700_000_003), CancellationToken.None);
        Assert.IsFalse(unkept.Behind, "a file new to the set is in no backup");
    }

    [TestMethod]
    public async Task ASetThatLeavesThem_FetchesNone_LeavesOutTheNewAndTheChanged_KeepsTheUnchanged_AndFailsNothing()
    {
        var source = new FakeFileSystemSource();
        var doc = Online(source, "docs/doc.bin", 1);
        Online(source, "docs/kept.bin", 2);
        var local = source.AddFile("docs/local.bin", Deterministic(4_000, 7));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        Assert.AreEqual(0, first.LeftOnlineOnly, "a set that fetches them leaves none");
        var before = first.Files.ToDictionary(file => file.RelativePath);

        Change(doc, 5);
        var added = Online(source, "docs/new.bin", 3);
        Change(local, 8);
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001) with { LeaveOnlineOnly = true }, CancellationToken.None);

        Assert.AreEqual(1, doc.OpenAttempts, "the changed one was not fetched again");
        Assert.AreEqual(0, added.OpenAttempts, "nor was the new one");
        Assert.AreEqual(2, second.LeftOnlineOnly);
        Assert.IsEmpty(second.Failures, "a file left is no failure");
        Assert.IsNull(second.ErrorManifestObjectId);
        Assert.IsNull(second.NotFetched);
        Assert.IsFalse(second.Behind);

        var after = second.Files.ToDictionary(file => file.RelativePath);
        Assert.IsFalse(after.ContainsKey("docs/doc.bin"), "a changed one is left out, its version before in the snapshot before");
        Assert.IsFalse(after.ContainsKey("docs/new.bin"));
        Assert.IsNull(catalogue.LookupPath(Snapshot(0xB2), "docs/doc.bin"));
        Assert.AreEqual(before["docs/kept.bin"].ObjectId, after["docs/kept.bin"].ObjectId, "an unchanged one keeps the version backed up");
        Assert.IsFalse(after["docs/local.bin"].Reused, "a file on the device is taken as ever");
    }

    [TestMethod]
    public async Task ASetThatLeavesThem_StillTakesOneOnlyMoved_WithoutAFetch()
    {
        var source = new FakeFileSystemSource();
        var original = source.AddFile("old/doc.bin", Deterministic(4_000, 1), fileId: 500);
        original.OnlineOnly = true;

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        source.Remove("old/doc.bin");
        var moved = source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "new/doc.bin", Content = original.Content, Identity = original.Identity,
            Metadata = original.Metadata, OnlineOnly = true,
        });
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001) with { LeaveOnlineOnly = true }, CancellationToken.None);

        Assert.AreEqual(0, moved.OpenAttempts, "its content is the version before's");
        Assert.AreEqual(0, second.LeftOnlineOnly);
        Assert.AreEqual("new/doc.bin", Assert.ContainsSingle(second.Files).RelativePath);
    }

    [TestMethod]
    public async Task ARestoresPlan_NamesEachFileAnEarlierVersionStandsIn_AndWhatTheBackupSaid()
    {
        var source = new FakeFileSystemSource();
        var doc = Online(source, "docs/doc.bin", 1);
        Online(source, "docs/still.bin", 2);
        var local = source.AddFile("docs/local.bin", Deterministic(4_000, 7));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        var clean = await ProbeAsync(catalogue, store, keys, 0xA1);
        Assert.IsEmpty(clean.EarlierVersions, "a backup that fetched every one");
        Assert.IsFalse(clean.FailuresUnread, "a snapshot that failed nothing has no failures to read");

        Change(doc, 5);
        Change(local, 8);
        local.OpenFailure = new IOException("a bad sector");
        doc.OpenFailure = new IOException(TimedOut);
        Online(source, "docs/new.bin", 3);
        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001), CancellationToken.None);

        // The file a version before stands in for is named; one new to the set
        // and one on the device that would not read are in no tree to plan,
        // and one unchanged holds its own.
        var probed = await ProbeAsync(catalogue, store, keys, 0xB2);
        var earlier = Assert.ContainsSingle(probed.EarlierVersions);
        Assert.AreEqual("docs/doc.bin", earlier.Path);
        Assert.Contains(TimedOut, earlier.Detail, StringComparison.Ordinal);
        Assert.Contains("stands in its place", earlier.Detail, StringComparison.Ordinal);
        Assert.IsFalse(probed.FailuresUnread);

        // Named only where the plan restores it.
        Assert.IsEmpty((await ProbeAsync(catalogue, store, keys, 0xB2, "docs/still.bin")).EarlierVersions);
    }

    [TestMethod]
    public async Task ARestoresPlan_WhoseSnapshotOrItsFailuresWillNotRead_SaysItCannotName_AndNamesNone()
    {
        var source = new FakeFileSystemSource();
        var doc = Online(source, "docs/doc.bin", 1);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        Change(doc, 5);
        doc.OpenFailure = new IOException(TimedOut);
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001), CancellationToken.None);
        var failures = second.ErrorManifestObjectId ?? throw new AssertFailedException("the run recorded its failure");

        // The failures will not read: nothing can be named, and the plan says so.
        await FlipAsync(store, keys, failures);
        var unread = await ProbeAsync(catalogue, store, keys, 0xB2);
        Assert.IsTrue(unread.FailuresUnread, "the snapshot's failures would not read");
        Assert.IsEmpty(unread.EarlierVersions);
        await FlipAsync(store, keys, failures);

        // Nor where the snapshot's own manifest will not read, which says
        // where its failures are.
        await FlipAsync(store, keys, second.SnapshotObjectId);
        Assert.IsTrue((await ProbeAsync(catalogue, store, keys, 0xB2)).FailuresUnread, "the snapshot's manifest would not read");
    }

    [TestMethod]
    public async Task ARestoresReceipt_NamesEachFileItRestoredAsAnEarlierVersion_AndSaysWhereItCannot()
    {
        var source = new FakeFileSystemSource();
        var doc = Online(source, "docs/doc.bin", 1);
        Online(source, "docs/still.bin", 2);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        var clean = await EarlierAsync(catalogue, store, keys, 0xA1);
        Assert.IsTrue(clean.Stamp(Receipt("docs/doc.bin", "docs/still.bin")).Items.All(item => item.EarlierVersion is null), "a backup that fetched every one");

        Change(doc, 5);
        doc.OpenFailure = new IOException(TimedOut);
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001), CancellationToken.None);

        // Each item the snapshot's failures name carries what the backup said
        // of it; the rest carry nothing, and the receipt says it could name them.
        var stamped = (await EarlierAsync(catalogue, store, keys, 0xB2)).Stamp(Receipt("docs/doc.bin", "docs/still.bin"));
        Assert.Contains(TimedOut, stamped.Items[0].EarlierVersion!, StringComparison.Ordinal);
        Assert.Contains("stands in its place", stamped.Items[0].EarlierVersion!, StringComparison.Ordinal);
        Assert.IsNull(stamped.Items[1].EarlierVersion);
        Assert.IsFalse(stamped.EarlierVersionsUnread);
        using (var kept = JsonDocument.Parse(stamped.ToJson()))
        {
            Assert.AreEqual(12, kept.RootElement.GetProperty("schema_version").GetInt32());
            Assert.IsFalse(kept.RootElement.TryGetProperty("earlier_versions_unread", out _), "written only where they would not read");
            var items = kept.RootElement.GetProperty("items");
            Assert.AreEqual(stamped.Items[0].EarlierVersion, items[0].GetProperty("earlier_version").GetString());
            Assert.IsFalse(items[1].TryGetProperty("earlier_version", out _), "written only for a file an earlier version stands in for");
        }

        // Where the failures will not read, nothing is named, and the receipt says it could not.
        await FlipAsync(store, keys, second.ErrorManifestObjectId!.Value);
        var unread = (await EarlierAsync(catalogue, store, keys, 0xB2)).Stamp(Receipt("docs/doc.bin", "docs/still.bin"));
        Assert.IsTrue(unread.EarlierVersionsUnread);
        Assert.IsTrue(unread.Items.All(item => item.EarlierVersion is null));
        using (var kept = JsonDocument.Parse(unread.ToJson()))
        {
            Assert.IsTrue(kept.RootElement.GetProperty("earlier_versions_unread").GetBoolean());
        }
    }

    [TestMethod]
    public async Task ARunsReadOfThem_FromAStoreThatWillNotServeTheirRecords_OrStoppedBeforeIt_NamesNone_AndSaysItCouldNot()
    {
        var source = new FakeFileSystemSource();
        var doc = Online(source, "docs/doc.bin", 1);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        Change(doc, 5);
        doc.OpenFailure = new IOException(TimedOut);
        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001), CancellationToken.None);

        // A store that opens a blob and then will not serve what is in it is
        // the run's to meet, file by file: here it only means none is named.
        var plan = RestorePlanner.Plan(catalogue, Snapshot(0xB2), string.Empty, RestoreTargetProfile.ForLocalPlatform());
        var earlier = await RestoreBlobSet.EarlierVersionsAsync(
            catalogue, plan, new RecordRefusingStore(store), [], Repo, keys, CancellationToken.None);

        Assert.IsTrue(earlier.Unread);
        Assert.IsEmpty(earlier.Files);

        // Nor does a run stopped before it reads them stop at them: it goes on
        // to its receipt, which says it could not name them.
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();
        var unread = await RestoreBlobSet.EarlierVersionsAsync(catalogue, plan, store, [], Repo, keys, stopped.Token);
        Assert.IsTrue(unread.Unread);
        Assert.IsEmpty(unread.Files);

        // Nor one stopped while it reads them, as a caller hanging up mid-read stops it.
        using var hungUp = new CancellationTokenSource();
        var midRead = await RestoreBlobSet.EarlierVersionsAsync(
            catalogue, plan, new HangingUpStore(store, hungUp), [], Repo, keys, hungUp.Token);
        Assert.IsTrue(midRead.Unread);
        Assert.IsEmpty(midRead.Files);
    }

    [TestMethod]
    public void TheFilesRestoredAsAnEarlierVersion_AreThoseWritten_NotThoseThatFailedOrWereSkipped()
    {
        const string Said = "timed out; the version backed up earlier stands in its place";
        var receipt = Receipt("restored", "degraded", "failed", "skipped", "own");
        receipt = receipt with
        {
            Items =
            [
                .. receipt.Items.Select(item => item.Path == "own"
                    ? item
                    : item with { Outcome = item.Path, EarlierVersion = Said }),
            ],
        };

        CollectionAssert.AreEqual(
            new[] { "restored", "degraded" },
            RestoreEarlierVersions.RestoredIn(receipt.Items).Select(item => item.Path).ToArray());
    }

    [TestMethod]
    public async Task ASnapshotsFailures_AreReadAsItsPaths_NoneWhereItFailedNothing_AndNullWhereTheyWillNotRead()
    {
        var source = new FakeFileSystemSource();
        var doc = Online(source, "docs/doc.bin", 1);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        Assert.IsEmpty(
            await SnapshotFailures.PathsAsync(store, Repo, keys, catalogue, first.SnapshotObjectId, CancellationToken.None) ?? throw new AssertFailedException("it reads"));

        Change(doc, 5);
        doc.OpenFailure = new IOException(TimedOut);
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source, 0xB2, 0xA1, now: 1_722_700_000_001), CancellationToken.None);
        CollectionAssert.AreEqual(
            new[] { "docs/doc.bin" },
            (await SnapshotFailures.PathsAsync(store, Repo, keys, catalogue, second.SnapshotObjectId, CancellationToken.None))?.ToArray());

        await FlipAsync(store, keys, second.ErrorManifestObjectId!.Value);
        Assert.IsNull(await SnapshotFailures.PathsAsync(store, Repo, keys, catalogue, second.SnapshotObjectId, CancellationToken.None));
    }

    /// <summary>Flips one ciphertext byte of a record beneath the store, so it no longer authenticates; flipped again, it does.</summary>
    private async Task FlipAsync(IObjectStore store, RepositoryKeySet keys, ObjectId record)
    {
        RecordTableEntry located;
        string file;
        using (var reader = new RepositoryReader(Repo, keys, store, Authority))
        {
            await reader.LoadBlobsAsync(CancellationToken.None);
            Assert.IsTrue(reader.TryLocateRecord(record, out var blobStoreKey, out located));
            file = Path.Combine(StoreRoot, blobStoreKey.Value.Replace('/', Path.DirectorySeparatorChar));
        }

        var stored = await File.ReadAllBytesAsync(file, CancellationToken.None);
        stored[(long)located.PhysicalOffset + 54 + 3] ^= 0x01;
        await File.WriteAllBytesAsync(file, stored, CancellationToken.None);
    }

    private static async Task<RestoreEarlierVersions> EarlierAsync(
        CatalogueDb catalogue, IObjectStore store, RepositoryKeySet keys, byte snapshotSeed)
    {
        var plan = RestorePlanner.Plan(catalogue, Snapshot(snapshotSeed), string.Empty, RestoreTargetProfile.ForLocalPlatform());
        return await RestoreBlobSet.EarlierVersionsAsync(catalogue, plan, store, [], Repo, keys, CancellationToken.None);
    }

    /// <summary>A receipt of a run that restored <paramref name="paths"/>, as the executor keeps one.</summary>
    private static RestoreReceipt Receipt(params string[] paths) => new()
    {
        SchemaVersion = RestoreReceipt.CurrentSchemaVersion,
        SnapshotId = new string('b', 32),
        StartedAt = 1,
        CompletedAt = 2,
        Items = [.. paths.Select(path => new ReceiptItem { Path = path, Outcome = "restored", Bytes = 4_000 })],
        Displaced = [],
        WrittenTo = "/restored",
        Outcome = RestoreOutcome.Complete,
    };

    private static async Task<RestoreBlobSetResult> ProbeAsync(
        CatalogueDb catalogue, IObjectStore store, RepositoryKeySet keys, byte snapshotSeed, string prefix = "")
    {
        var plan = RestorePlanner.Plan(catalogue, Snapshot(snapshotSeed), prefix, RestoreTargetProfile.ForLocalPlatform());
        return await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None);
    }

    private const uint Archive = 0x20;
    private const uint Directory = 0x10;
    private const uint ReparsePoint = 0x400;
    private const uint Offline = 0x1000;
    private const uint RecallOnOpen = 0x4_0000;
    private const uint Pinned = 0x8_0000;
    private const uint Unpinned = 0x10_0000;
    private const uint RecallOnDataAccess = 0x40_0000;

    private static FakeFileSystemSource.Node Online(
        FakeFileSystemSource source, string path, byte seed, int length = 4_000)
    {
        var node = source.AddFile(path, Deterministic(length, seed));
        node.OnlineOnly = true;
        return node;
    }

    // Changed where the provider keeps it: other content, modified a minute later.
    private static void Change(FakeFileSystemSource.Node node, byte seed)
    {
        node.Content = Deterministic(node.Content.Length + 100, seed);
        node.Metadata = node.Metadata with { ModifiedAt = node.Metadata.ModifiedAt + 60_000 };
    }

    private static byte[] Snapshot(byte seed) => Enumerable.Repeat(seed, 16).ToArray();

    private static SnapshotJob After(FakeFileSystemSource source, byte snapshotSeed, byte priorSeed, ulong now) =>
        Job(source, snapshotSeed, now) with
        {
            PriorSnapshotId = Snapshot(priorSeed),
            ParentSnapshots = [Snapshot(priorSeed)],
        };

    private static string Detail(PublishedTreeSnapshot published, string path) =>
        published.Failures.Single(failure => PathOf(failure) == path).Detail;

    private static CaptureFailureReason ReasonFor(PublishedTreeSnapshot published, string path) =>
        published.Failures.Single(failure => PathOf(failure) == path).Reason;

    private static string PathOf(CaptureFailure failure) =>
        string.Join('/', failure.PathComponents.Select(component => Encoding.UTF8.GetString(component.Span)));

    private CatalogueDb OpenCatalogue() =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, RepositoryWriteCredential credential, CatalogueDb catalogue) =>
        new(
            SmallBlobPolicy,
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            credential,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory,
            FormatVersions.SealedDataPlane,
            observer: null,
            catalogue);

    private static SnapshotJob Job(FakeFileSystemSource source, byte snapshotSeed, ulong now = 1_722_600_000_000) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(snapshotSeed, 16).ToArray(),
        NowUnixMilliseconds = now,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "fallbackplan-tests/1.0",
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

    /// <summary>
    /// Stops the caller's token at the first read it is asked for, and answers
    /// that read as a store does a cancelled one.
    /// </summary>
    private sealed class HangingUpStore(IObjectStore inner, CancellationTokenSource caller) : IObjectStore
    {
        public StoreCapabilities Capabilities => inner.Capabilities;

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public async ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
        {
            await caller.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return await inner.OpenReadAsync(key, range, cancellationToken);
        }

        public ValueTask<PutResult> PutAsync(
            ObjectKey key, Func<CancellationToken, ValueTask<Stream>> openContent, PutConditions conditions, CancellationToken cancellationToken) =>
            inner.PutAsync(key, openContent, conditions, cancellationToken);

        public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }

    /// <summary>
    /// Serves each blob's opening, its locator, footer and envelope, and fails
    /// every read of it after with an <see cref="IOException"/>, as a store
    /// that goes away between a blob's footer and its records does.
    /// </summary>
    private sealed class RecordRefusingStore(IObjectStore inner) : IObjectStore
    {
        private const int OpeningReads = 3;

        private readonly Dictionary<ObjectKey, int> _reads = [];

        public StoreCapabilities Capabilities => inner.Capabilities;

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
        {
            var read = _reads[key] = _reads.GetValueOrDefault(key) + 1;
            return read > OpeningReads
                ? throw new IOException($"Injected fault: the read of '{key}' failed.")
                : inner.OpenReadAsync(key, range, cancellationToken);
        }

        public ValueTask<PutResult> PutAsync(
            ObjectKey key, Func<CancellationToken, ValueTask<Stream>> openContent, PutConditions conditions, CancellationToken cancellationToken) =>
            inner.PutAsync(key, openContent, conditions, cancellationToken);

        public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }
}
