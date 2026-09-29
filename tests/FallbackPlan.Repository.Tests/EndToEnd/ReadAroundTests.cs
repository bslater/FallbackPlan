using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A restore that meets a record its own store will not serve reads that
/// record from another copy of the same blob (FR-RST-007): a record whose
/// bytes fail their checks, a blob the store holds but will not read, or one
/// it no longer holds. The other copy passes the same checks as the first,
/// is opened only once every earlier copy has failed, and each copy passed
/// over is named with what was wrong. With every copy failed, the record still
/// fails (FR-RST-005), naming each one tried; and a reader given no other
/// copies reads exactly as it did.
/// </summary>
/// <remarks>
/// A copy is found by the record's location, and the location does not
/// depend on the store: blobs are immutable and a replica holds them key for
/// key, byte for byte, so the same record sits at the same offset of the same
/// blob wherever that blob is held.
/// </remarks>
[TestClass]
public sealed class ReadAroundTests : ArchiveTestHarness
{
    private const string Own = "the staging archive";

    private const string Sibling = "destination 'spare'";

    private string SpareRoot => Path.Combine(StoreRoot, "..", "spare");

    [TestMethod]
    public async Task ARecordItsOwnStoreHoldsDamaged_IsReadFromTheNextCopy_AndVerifiedThere()
    {
        var (plan, catalogue, store, keys) = await PublishAsync();
        using var held = keys;
        using var _ = catalogue;
        var spare = await MirrorAsync(store, SpareRoot);
        var (segment, location) = await FirstSegmentAsync(plan, catalogue, store, keys);
        var victim = RotRecord(StoreRoot, keys, location);

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);
        reader.UseOtherCopies(Own, [Serve(Sibling, spare)]);

        var read = await reader.ReadSegmentAsync(segment, CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        using (var direct = new RepositoryReader(Repo, keys, spare, Authority))
        {
            direct.UseLocationSource(catalogue.ResolveLocation);
            var sound = await direct.ReadSegmentAsync(segment, CancellationToken.None);
            CollectionAssert.AreEqual(sound.Plaintext, read.Plaintext, "the sibling's verified record must be what was served");
        }

        var around = Assert.ContainsSingle(reader.ReadAround);
        Assert.AreEqual(segment, around.ObjectId);
        Assert.AreEqual(Sibling, around.ReadFrom);
        var passed = Assert.ContainsSingle(around.PassedOver);
        Assert.AreEqual(Own, passed.Source);
        Assert.AreEqual(CopyFault.Damaged, passed.Fault);
        Assert.AreEqual(victim, passed.BlobKey, "the damaged copy is named by the blob it is in, so it can be put right");
    }

    [TestMethod]
    public async Task EveryCopyDamaged_TheRecordStillFails_NamingEachCopyTried()
    {
        var (plan, catalogue, store, keys) = await PublishAsync();
        using var held = keys;
        using var _ = catalogue;
        var spare = await MirrorAsync(store, SpareRoot);
        var (segment, location) = await FirstSegmentAsync(plan, catalogue, store, keys);
        RotRecord(StoreRoot, keys, location);
        RotRecord(SpareRoot, keys, location);

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);
        reader.UseOtherCopies(Own, [Serve(Sibling, spare)]);

        var read = await reader.ReadSegmentAsync(segment, CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.AuthenticationFailed, read.Outcome, "a second bad copy is no reason to call it something else");
        Assert.Contains(Own, read.Detail!, StringComparison.Ordinal);
        Assert.Contains(Sibling, read.Detail!, StringComparison.Ordinal);
        Assert.IsEmpty(reader.ReadAround);
        Assert.HasCount(2, reader.Refusals.Where(refusal => refusal.Fault == CopyFault.Damaged).ToList());
    }

    [TestMethod]
    public async Task AnotherCopy_IsOpenedOnlyWhenTheOwnCopyFails_AndOnlyAsFarAsNeeded()
    {
        var (plan, catalogue, store, keys) = await PublishAsync();
        using var held = keys;
        using var _ = catalogue;
        var spare = await MirrorAsync(store, SpareRoot);
        var (segment, location) = await FirstSegmentAsync(plan, catalogue, store, keys);

        var spareOpens = 0;
        var farOpens = 0;
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);
        reader.UseOtherCopies(Own,
        [
            new CopySource(Sibling, _ =>
            {
                spareOpens++;
                return ValueTask.FromResult<IObjectStore?>(spare);
            }),
            new CopySource("destination 'far'", _ =>
            {
                // A peer, in the service: opening it means dialling it.
                farOpens++;
                return ValueTask.FromResult<IObjectStore?>(spare);
            }),
        ]);

        Assert.AreEqual(RecordReadOutcome.Ok, (await reader.ReadSegmentAsync(segment, CancellationToken.None)).Outcome);
        Assert.AreEqual(0, spareOpens, "a sound own copy opens no other");

        RotRecord(StoreRoot, keys, location);
        Assert.AreEqual(RecordReadOutcome.Ok, (await reader.ReadSegmentAsync(segment, CancellationToken.None)).Outcome);
        Assert.AreEqual(RecordReadOutcome.Ok, (await reader.ReadSegmentAsync(segment, CancellationToken.None)).Outcome);

        Assert.AreEqual(1, spareOpens, "a copy is opened once however many records it serves");
        Assert.AreEqual(0, farOpens, "a copy past the one that served is never opened");
    }

    [TestMethod]
    public async Task AnOwnCopyThatWillNotRead_IsReadAround_AndIsNotCalledDamage()
    {
        // One read cannot tell a bad sector from a device going away, which
        // is why the sweep will not call an unreadable blob damaged
        // (ADR-0035 Amendment 1). A restore reads around it all the same.
        var (plan, catalogue, store, keys) = await PublishAsync();
        using var held = keys;
        using var _ = catalogue;
        var spare = await MirrorAsync(store, SpareRoot);
        var (segment, location) = await FirstSegmentAsync(plan, catalogue, store, keys);
        var victim = BlobKey(keys, location, BlobClass.Data);

        using var reader = new RepositoryReader(
            Repo, keys, new UnreadableObjectStore(store, key => key == victim), Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);
        reader.UseOtherCopies(Own, [Serve(Sibling, spare)]);

        var read = await reader.ReadSegmentAsync(segment, CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        var passed = Assert.ContainsSingle(Assert.ContainsSingle(reader.ReadAround).PassedOver);
        Assert.AreEqual(CopyFault.Unreadable, passed.Fault);
        Assert.Contains(UnreadableObjectStore.Refusal, passed.Detail, StringComparison.Ordinal);
        Assert.IsEmpty(reader.Refusals.Where(refusal => refusal.Fault == CopyFault.Damaged).ToList());
    }

    [TestMethod]
    public async Task ABlobItsOwnStoreNoLongerHolds_IsReadFromACopyThatDoes_AndIsNoFinding()
    {
        // What a staging trim leaves (ADR-0034 §6): the history's data blobs
        // are gone from staging on purpose, because every destination
        // provably holds them.
        var (plan, catalogue, store, keys) = await PublishAsync();
        using var held = keys;
        using var _ = catalogue;
        var spare = await MirrorAsync(store, SpareRoot);
        var (segment, location) = await FirstSegmentAsync(plan, catalogue, store, keys);
        File.Delete(PathOf(StoreRoot, BlobKey(keys, location, BlobClass.Data)));

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);
        reader.UseOtherCopies(Own, [Serve(Sibling, spare)]);

        var read = await reader.ReadSegmentAsync(segment, CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        var passed = Assert.ContainsSingle(Assert.ContainsSingle(reader.ReadAround).PassedOver);
        Assert.AreEqual(CopyFault.NotHeld, passed.Fault);
        Assert.IsEmpty(reader.Refusals.Where(refusal => refusal.Fault == CopyFault.Damaged).ToList());
    }

    [TestMethod]
    public async Task AReadPathOverTheCopies_IsNotItselfNamedAmongThoseReadAround()
    {
        // A direct-ship set's own store answers from the first of its
        // destinations holding a blob. It is not one copy but a way of
        // reading them, and every copy it reads is among the others, so the
        // damaged one is named when it is tried in its own right.
        var (plan, catalogue, store, keys) = await PublishAsync();
        using var held = keys;
        using var _ = catalogue;
        var spare = await MirrorAsync(store, SpareRoot);
        var (segment, location) = await FirstSegmentAsync(plan, catalogue, store, keys);
        RotRecord(StoreRoot, keys, location);

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);
        reader.UseOtherCopies(ownName: null, [Serve("destination 'vault'", store), Serve(Sibling, spare)]);

        var read = await reader.ReadSegmentAsync(segment, CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        var around = Assert.ContainsSingle(reader.ReadAround);
        Assert.AreEqual(Sibling, around.ReadFrom);
        Assert.AreEqual("destination 'vault'", Assert.ContainsSingle(around.PassedOver).Source);
    }

    [TestMethod]
    public async Task ALocationPointingAtAnotherBlob_IsNotTakenForDamage_AtAnyCopy()
    {
        // The catalogue is a cache, never authoritative. A location naming
        // the wrong blob fails at every copy alike, and each copy's own
        // authenticated footer says why: that blob does not hold the record.
        // Calling that damage would hold a sound destination failed and tell
        // its owner to check a device that is fine.
        var (plan, catalogue, store, keys) = await PublishAsync();
        using var held = keys;
        using var _ = catalogue;
        var spare = await MirrorAsync(store, SpareRoot);
        var (segment, location) = await FirstSegmentAsync(plan, catalogue, store, keys);
        var elsewhere = await SegmentInAnotherBlobAsync(plan, catalogue, store, keys, location.BlobId);

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(objectId => objectId == segment
            ? location with { BlobId = elsewhere.BlobId, StoreBlobKey = elsewhere.StoreBlobKey }
            : catalogue.ResolveLocation(objectId));
        reader.UseOtherCopies(Own, [Serve(Sibling, spare)]);

        var read = await reader.ReadSegmentAsync(segment, CancellationToken.None);

        Assert.AreNotEqual(RecordReadOutcome.Ok, read.Outcome, "nothing says where the record really is");
        Assert.IsEmpty(
            reader.Refusals.Where(refusal => refusal.Fault == CopyFault.Damaged).ToList(),
            string.Join(" | ", reader.Refusals.Select(refusal => $"{refusal.Source}: {refusal.Fault} {refusal.Detail}")));
    }

    [TestMethod]
    public async Task WithNoOtherCopies_ADamagedRecordFailsExactlyAsItDid()
    {
        // A restore from a named source reads that source alone: a drill of a
        // destination exists to prove that copy restores (ADR-0054), and one
        // that read around its damage would pass it.
        var (plan, catalogue, store, keys) = await PublishAsync();
        using var held = keys;
        using var _ = catalogue;
        var (segment, location) = await FirstSegmentAsync(plan, catalogue, store, keys);
        RotRecord(StoreRoot, keys, location);

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);

        var read = await reader.ReadSegmentAsync(segment, CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.AuthenticationFailed, read.Outcome);
        Assert.DoesNotContain("copy", read.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.IsEmpty(reader.ReadAround);
        Assert.IsEmpty(reader.Refusals);
    }

    [TestMethod]
    public async Task ARestoredFileReadAroundDamage_SaysWhereItCameFrom_AndWhatItWasReadAround()
    {
        var (plan, catalogue, store, keys) = await PublishAsync();
        using var held = keys;
        using var _ = catalogue;
        var spare = await MirrorAsync(store, SpareRoot);
        var (_, location, path) = await FirstSegmentWithPathAsync(plan, catalogue, store, keys);
        RotRecord(StoreRoot, keys, location);

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);
        reader.UseOtherCopies(Own, [Serve(Sibling, spare)]);

        var output = Path.Combine(SpoolDirectory, "read-around-out");
        var receipt = await RestoreAsync(reader, plan, output);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        var item = receipt.Items.Single(candidate => candidate.Path == path);
        Assert.AreEqual("restored", item.Outcome);
        Assert.AreEqual(Sibling, Assert.ContainsSingle(item.ReadFrom!));
        Assert.Contains(Own, Assert.ContainsSingle(item.ReadAround!), StringComparison.Ordinal);

        // Every other file came from its own store, and says nothing.
        Assert.IsTrue(receipt.Items.Where(other => other.Path != path).All(other => other.ReadFrom is null && other.ReadAround is null));

        var json = receipt.ToJson();
        Assert.Contains("\"read_from\": [", json, StringComparison.Ordinal);
        Assert.Contains("\"read_around\": [", json, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AFileWhoseBlobsItsOwnStoreNoLongerHolds_NamesTheCopy_AndNothingItWasReadAround()
    {
        // Routine after a staging trim, so nothing to warn about: the receipt
        // says where the bytes came from, and that is all.
        var (plan, catalogue, store, keys) = await PublishAsync();
        using var held = keys;
        using var _ = catalogue;
        var spare = await MirrorAsync(store, SpareRoot);
        foreach (var blob in Directory.GetFiles(Path.Combine(StoreRoot, "blobs", "data"), "*", SearchOption.AllDirectories))
        {
            File.Delete(blob);
        }

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);
        reader.UseOtherCopies(Own, [Serve(Sibling, spare)]);

        var receipt = await RestoreAsync(reader, plan, Path.Combine(SpoolDirectory, "trimmed-out"));

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        var files = receipt.Items.Where(item => item.Bytes > 0).ToList();
        Assert.IsNotEmpty(files);
        Assert.IsTrue(files.All(item => item.ReadFrom is [Sibling]), "every file's content came from the sibling");
        Assert.IsTrue(files.All(item => item.ReadAround is null), "a blob that was never there to be damaged is nothing to warn about");
    }

    [TestMethod]
    public async Task APlanWhoseBlobsItsOwnStoreNoLongerHolds_CallsNothingMissing_WhenAnotherCopyHoldsThem()
    {
        // The plan answers for the run it plans (FR-RST-003): a run that will
        // read what staging trimmed from a destination must not be announced
        // as one whose files will fail.
        var (plan, catalogue, store, keys) = await PublishAsync();
        using var held = keys;
        using var _ = catalogue;
        var spare = await MirrorAsync(store, SpareRoot);
        foreach (var blob in Directory.GetFiles(Path.Combine(StoreRoot, "blobs", "data"), "*", SearchOption.AllDirectories))
        {
            File.Delete(blob);
        }

        var alone = await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None);
        Assert.IsNotEmpty(alone.Missing, "the control: its own store cannot serve the plan");

        var withCopies = await RestoreBlobSet.ResolveAsync(
            catalogue, plan, store, [Serve(Sibling, spare)], Repo, keys, CancellationToken.None);
        Assert.IsEmpty(withCopies.Missing, string.Join(", ", withCopies.Missing));
    }

    private static CopySource Serve(string name, IObjectStore store) =>
        new(name, _ => ValueTask.FromResult<IObjectStore?>(store));

    private static async Task<RestoreReceipt> RestoreAsync(RepositoryReader reader, RestorePlan plan, string output) =>
        await new RestoreExecutor(reader, RestoreTargetProfile.ForLocalPlatform()).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "read-around-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

    /// <summary>
    /// Four incompressible files with their catalogue, so the records land
    /// across several data blobs and every file has segments of its own.
    /// </summary>
    private async Task<(RestorePlan Plan, CatalogueDb Catalogue, LocalFileSystemObjectStore Store, RepositoryKeySet Keys)>
        PublishAsync()
    {
        var source = new FakeFileSystemSource();
        for (var index = 0; index < 4; index++)
        {
            var content = new byte[200_000];
            new Random(90 + index).NextBytes(content);
            source.AddFile($"data/file-{index}.bin", content, fileId: (ulong)(9_400 + index));
        }

        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue-read-around.db"), Repo);
        var spool = Path.Combine(SpoolDirectory, "read-around");
        Directory.CreateDirectory(spool);
        await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                spool, FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = source,
                    Roots = [new ScanRoot("/")],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat((byte)0xEA, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "read-around-tests/1.0",
                },
                CancellationToken.None);

        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat((byte)0xEA, 16).ToArray(), string.Empty,
            RestoreTargetProfile.ForLocalPlatform());
        return (plan, catalogue, store, keys);
    }

    private static async Task<(ObjectId Segment, Repository.Catalogue.ResolvedLocation Location)> FirstSegmentAsync(
        RestorePlan plan, CatalogueDb catalogue, IObjectStore store, RepositoryKeySet keys)
    {
        var (segment, location, _) = await FirstSegmentWithPathAsync(plan, catalogue, store, keys);
        return (segment, location);
    }

    /// <summary>The first segment of the plan's first file, where it sits, and the file's path.</summary>
    private static async Task<(ObjectId Segment, Repository.Catalogue.ResolvedLocation Location, string Path)>
        FirstSegmentWithPathAsync(RestorePlan plan, CatalogueDb catalogue, IObjectStore store, RepositoryKeySet keys)
    {
        var item = plan.Items.First(entry => entry.Kind == EntryKind.File);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);
        var read = await reader.ReadSegmentAsync(item.ObjectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);

        var segment = FileVersionManifestCodec.Decode(read.Plaintext!).SegmentReferences[0].ObjectId;
        return (segment, catalogue.ResolveLocation(segment)!, item.Path);
    }

    /// <summary>Where a segment of some file sits, in a blob other than <paramref name="not"/>.</summary>
    private static async Task<Repository.Catalogue.ResolvedLocation> SegmentInAnotherBlobAsync(
        RestorePlan plan, CatalogueDb catalogue, IObjectStore store, RepositoryKeySet keys, BlobId not)
    {
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);
        foreach (var item in plan.Items.Where(entry => entry.Kind == EntryKind.File))
        {
            var read = await reader.ReadSegmentAsync(item.ObjectId, CancellationToken.None);
            foreach (var reference in FileVersionManifestCodec.Decode(read.Plaintext!).SegmentReferences)
            {
                if (catalogue.ResolveLocation(reference.ObjectId) is { } located && !located.BlobId.Equals(not))
                {
                    return located;
                }
            }
        }

        Assert.Fail("the fixture must spread its segments across more than one data blob");
        return null!;
    }

    private static string BlobKey(RepositoryKeySet keys, Repository.Catalogue.ResolvedLocation location, BlobClass blobClass)
    {
        using var deriver = new StoreBlobKeyDeriver(keys.KeyIdKey);
        return BlobStoreKeys.ForBlob(blobClass, location.StoreBlobKey ?? deriver.Derive(location.BlobId)).Value;
    }

    private static string PathOf(string root, string key) =>
        Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Flips one byte inside the record's ciphertext in the store at
    /// <paramref name="root"/> alone, where its tag will catch it.
    /// </summary>
    /// <returns>The damaged blob's store key.</returns>
    private static string RotRecord(string root, RepositoryKeySet keys, Repository.Catalogue.ResolvedLocation location)
    {
        var key = BlobKey(keys, location, BlobClass.Data);
        using var file = File.Open(PathOf(root, key), FileMode.Open, FileAccess.ReadWrite);
        file.Seek((long)location.PhysicalOffset + RecordHeader.Length + 4, SeekOrigin.Begin);
        var original = file.ReadByte();
        file.Seek(-1, SeekOrigin.Current);
        file.WriteByte((byte)(original ^ 0xFF));
        return key;
    }

    private static async Task<LocalFileSystemObjectStore> MirrorAsync(LocalFileSystemObjectStore from, string root)
    {
        Directory.CreateDirectory(root);
        var mirror = new LocalFileSystemObjectStore(root);
        await foreach (var entry in from.ListAsync(ObjectPrefix.All, ListOptions.Default, CancellationToken.None))
        {
            using var read = await from.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            var bytes = new byte[entry.Length];
            await read.Content!.ReadExactlyAsync(bytes, CancellationToken.None);
            await mirror.PutAsync(
                entry.Key,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)),
                PutConditions.None,
                CancellationToken.None);
        }

        return mirror;
    }
}
