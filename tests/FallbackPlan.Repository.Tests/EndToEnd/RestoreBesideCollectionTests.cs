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
/// A restore that is partway through a blob when the blob goes from its
/// store (FR-RST-005, FR-RST-007; the prior-art review's T-4): the collector
/// sweeping what an expired snapshot alone reached, or the staging trim
/// removing what every destination holds, while a restore that began before
/// either still reads. The reader learns where a blob is and how it is
/// framed once, so a record it reads after the blob has gone is one the
/// store no longer holds. It is read from another copy that holds it, or it
/// fails as not held and the run goes on, its receipt saying so (ADR-0172
/// Amendment 2); it is never taken for damage, and it never ends the run. A compaction under the same restore moves the records
/// before their blob goes, and they are read from where they went.
/// </summary>
/// <remarks>
/// Each case reads one record from a blob first, so the reader has located
/// and framed it, and only then removes the blob. Both formats are read: a
/// format-2 blob is framed from its envelope before its first record, and a
/// later one is read at an assumed framing first.
/// </remarks>
[TestClass]
public sealed class RestoreBesideCollectionTests : ArchiveTestHarness
{
    private const string Own = "the staging archive";

    private const string Sibling = "destination 'spare'";

    private const int FileCount = 4;

    /// <summary>
    /// A budget too small to coalesce under, so every record is a read of its
    /// own: what a restore larger than one prefetch wave does to a blob that
    /// two of its waves need.
    /// </summary>
    private static readonly PrefetchPolicy OneReadARecord = PrefetchPolicy.Default with { BudgetBytes = 1 };

    private string SpareRoot => Path.Combine(StoreRoot, "..", "spare");

    [TestMethod]
    [DataRow(FormatVersions.SealedDataPlane, DisplayName = "format 2")]
    [DataRow(FormatVersions.DeltaCheckpoints, DisplayName = "format 5")]
    public async Task ABlobThatGoesAfterTheRestoreReadFromIt_IsReadFromACopyThatHoldsIt_AndIsNoFinding(ushort format)
    {
        var world = await PublishAsync(format);
        using var held = world.Keys;
        using var _ = world.Catalogue;
        var spare = await MirrorAsync(world.Store, SpareRoot);
        var (first, second, blob) = await TwoSegmentsInOneBlobAsync(world);

        using var reader = new RepositoryReader(Repo, world.Keys, world.Store, Authority);
        reader.UseLocationSource(world.Catalogue.ResolveLocation);
        reader.UseOtherCopies(Own, [Serve(Sibling, spare)]);
        Assert.AreEqual(RecordReadOutcome.Ok, (await reader.ReadSegmentAsync(first, CancellationToken.None)).Outcome);

        await world.Store.DeleteAsync(blob, DeleteConditions.None, CancellationToken.None);
        var read = await reader.ReadSegmentAsync(second, CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        var passed = Assert.ContainsSingle(Assert.ContainsSingle(reader.ReadAround).PassedOver);
        Assert.AreEqual(CopyFault.NotHeld, passed.Fault, passed.Detail);
        Assert.IsEmpty(reader.Refusals.Where(refusal => refusal.Fault == CopyFault.Damaged).ToList());
    }

    [TestMethod]
    [DataRow(FormatVersions.SealedDataPlane, DisplayName = "format 2")]
    [DataRow(FormatVersions.DeltaCheckpoints, DisplayName = "format 5")]
    public async Task ABlobThatGoesAfterTheRestoreReadFromIt_WithNoOtherCopy_FailsTheRecord_AndThrowsNothing(ushort format)
    {
        var world = await PublishAsync(format);
        using var held = world.Keys;
        using var _ = world.Catalogue;
        var (first, second, blob) = await TwoSegmentsInOneBlobAsync(world);

        using var reader = new RepositoryReader(Repo, world.Keys, world.Store, Authority);
        reader.UseLocationSource(world.Catalogue.ResolveLocation);
        Assert.AreEqual(RecordReadOutcome.Ok, (await reader.ReadSegmentAsync(first, CancellationToken.None)).Outcome);

        await world.Store.DeleteAsync(blob, DeleteConditions.None, CancellationToken.None);
        var read = await reader.ReadSegmentAsync(second, CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.NotHeld, read.Outcome, read.Detail);
        Assert.Contains("does not hold", read.Detail!, StringComparison.Ordinal);
        Assert.IsEmpty(reader.SkippedBlobs, "a blob the store no longer holds is not a damaged one");
    }

    [TestMethod]
    [DataRow(FormatVersions.SealedDataPlane, DisplayName = "format 2")]
    [DataRow(FormatVersions.DeltaCheckpoints, DisplayName = "format 5")]
    public async Task AReaderThatLoadedEveryFooter_ABlobThatGoesAfterIt_FailsTheRecord_AndThrowsNothing(ushort format)
    {
        // The recovery tool's reader: every blob opened through its footer
        // before the first read, and no location source to ask again.
        var world = await PublishAsync(format);
        using var held = world.Keys;
        using var _ = world.Catalogue;
        var (first, second, blob) = await TwoSegmentsInOneBlobAsync(world);

        using var reader = new RepositoryReader(Repo, world.Keys, world.Store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, (await reader.ReadSegmentAsync(first, CancellationToken.None)).Outcome);

        await world.Store.DeleteAsync(blob, DeleteConditions.None, CancellationToken.None);
        var read = await reader.ReadSegmentAsync(second, CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.NotHeld, read.Outcome, read.Detail);
        Assert.IsEmpty(reader.SkippedBlobs);
    }

    [TestMethod]
    [DataRow(FormatVersions.SealedDataPlane, DisplayName = "format 2")]
    [DataRow(FormatVersions.DeltaCheckpoints, DisplayName = "format 5")]
    public async Task AReaderThatLoadedEveryFooter_ABlobThatGoesAfterIt_IsReadFromACopy_AndIsNotHeldAtTheOwnStore(ushort format)
    {
        // Such a reader keeps the blob's record table after the blob goes,
        // so the record still has a place at the own store: only the read's
        // outcome says the store no longer holds it.
        var world = await PublishAsync(format);
        using var held = world.Keys;
        using var _ = world.Catalogue;
        var spare = await MirrorAsync(world.Store, SpareRoot);
        var (first, second, blob) = await TwoSegmentsInOneBlobAsync(world);

        using var reader = new RepositoryReader(Repo, world.Keys, world.Store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        reader.UseOtherCopies(Own, [Serve(Sibling, spare)]);
        Assert.AreEqual(RecordReadOutcome.Ok, (await reader.ReadSegmentAsync(first, CancellationToken.None)).Outcome);

        await world.Store.DeleteAsync(blob, DeleteConditions.None, CancellationToken.None);
        var read = await reader.ReadSegmentAsync(second, CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        var passed = Assert.ContainsSingle(Assert.ContainsSingle(reader.ReadAround).PassedOver);
        Assert.AreEqual(CopyFault.NotHeld, passed.Fault, passed.Detail);
        Assert.IsEmpty(reader.Refusals.Where(refusal => refusal.Fault == CopyFault.Damaged).ToList());
    }

    [TestMethod]
    [DataRow(FormatVersions.SealedDataPlane, DisplayName = "format 2")]
    [DataRow(FormatVersions.DeltaCheckpoints, DisplayName = "format 5")]
    public async Task ABlobGoneBeforeTheRestoreBegan_WithNoOtherCopy_FailsItsRecordsAsNotHeld(ushort format)
    {
        // The other way a location can name a blob the store does not hold:
        // it went before this reader asked about it at all.
        var world = await PublishAsync(format);
        using var held = world.Keys;
        using var _ = world.Catalogue;
        var (first, _, blob) = await TwoSegmentsInOneBlobAsync(world);
        await world.Store.DeleteAsync(blob, DeleteConditions.None, CancellationToken.None);

        using var reader = new RepositoryReader(Repo, world.Keys, world.Store, Authority);
        reader.UseLocationSource(world.Catalogue.ResolveLocation);
        var read = await reader.ReadSegmentAsync(first, CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.NotHeld, read.Outcome, read.Detail);
        Assert.IsEmpty(reader.SkippedBlobs);
    }

    [TestMethod]
    [DataRow(FormatVersions.SealedDataPlane, DisplayName = "format 2")]
    [DataRow(FormatVersions.DeltaCheckpoints, DisplayName = "format 5")]
    public async Task ABlobTheStoreStillHoldsButThatWillNotFrame_IsDamage_NotNotHeld(ushort format)
    {
        // The control: only a blob the store no longer holds is not held. One
        // it holds, cut short inside the record read so that neither the
        // record nor the footer frames, is damage, as it always was.
        var world = await PublishAsync(format);
        using var held = world.Keys;
        using var _ = world.Catalogue;
        var (first, _, blob) = await TwoSegmentsInOneBlobAsync(world);
        var path = Path.Combine(StoreRoot, blob.Value.Replace('/', Path.DirectorySeparatorChar));
        using (var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite))
        {
            file.SetLength((long)world.Catalogue.ResolveLocation(first)!.PhysicalOffset + 1);
        }

        using var reader = new RepositoryReader(Repo, world.Keys, world.Store, Authority);
        reader.UseLocationSource(world.Catalogue.ResolveLocation);
        var read = await reader.ReadSegmentAsync(first, CancellationToken.None);

        Assert.AreNotEqual(RecordReadOutcome.Ok, read.Outcome);
        Assert.AreNotEqual(RecordReadOutcome.NotHeld, read.Outcome, read.Detail);
        Assert.IsNotEmpty(reader.SkippedBlobs, "a held blob that will not frame is skipped as damage");
    }

    [TestMethod]
    [DataRow(FormatVersions.SealedDataPlane, 1, DisplayName = "format 2, after one read")]
    [DataRow(FormatVersions.SealedDataPlane, 2, DisplayName = "format 2, after two reads")]
    [DataRow(FormatVersions.SealedDataPlane, 3, DisplayName = "format 2, after three reads")]
    [DataRow(FormatVersions.DeltaCheckpoints, 1, DisplayName = "format 5, after one read")]
    [DataRow(FormatVersions.DeltaCheckpoints, 2, DisplayName = "format 5, after two reads")]
    [DataRow(FormatVersions.DeltaCheckpoints, 3, DisplayName = "format 5, after three reads")]
    public async Task ARestoreWhoseBlobGoesPartway_ReadsTheRestFromACopy_AndCompletes(ushort format, int servedReads)
    {
        var world = await PublishAsync(format);
        using var held = world.Keys;
        using var _ = world.Catalogue;
        var spare = await MirrorAsync(world.Store, SpareRoot);
        var (_, _, blob) = await TwoSegmentsInOneBlobAsync(world);
        var collected = new SweptAfterReadObjectStore(world.Store, key => key == blob.Value, servedReads);

        using var reader = new RepositoryReader(Repo, world.Keys, collected, Authority);
        reader.UseLocationSource(world.Catalogue.ResolveLocation);
        reader.UseOtherCopies(Own, [Serve(Sibling, spare)]);
        reader.UsePrefetchPolicy(OneReadARecord);
        var output = Path.Combine(SpoolDirectory, "swept-with-copy");
        var receipt = await RestoreAsync(reader, world.Plan, output);

        Assert.IsTrue(collected.Swept, $"the blob went partway through the run ({collected.Reads} reads)");
        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome, string.Join("; ", receipt.Items.Select(item => item.Detail)));
        AssertRestoredByteForByte(world, output, receipt.Items.Select(item => item.Path));
    }

    [TestMethod]
    [DataRow(FormatVersions.SealedDataPlane, 1, DisplayName = "format 2, after one read")]
    [DataRow(FormatVersions.SealedDataPlane, 2, DisplayName = "format 2, after two reads")]
    [DataRow(FormatVersions.SealedDataPlane, 3, DisplayName = "format 2, after three reads")]
    [DataRow(FormatVersions.DeltaCheckpoints, 1, DisplayName = "format 5, after one read")]
    [DataRow(FormatVersions.DeltaCheckpoints, 2, DisplayName = "format 5, after two reads")]
    [DataRow(FormatVersions.DeltaCheckpoints, 3, DisplayName = "format 5, after three reads")]
    public async Task ARestoreWhoseBlobGoesPartway_WithNoOtherCopy_KeepsItsReceipt_AndFailsOnlyWhatItCouldNotRead(ushort format, int servedReads)
    {
        var world = await PublishAsync(format);
        using var held = world.Keys;
        using var _ = world.Catalogue;
        var (_, _, blob) = await TwoSegmentsInOneBlobAsync(world);
        var collected = new SweptAfterReadObjectStore(world.Store, key => key == blob.Value, servedReads);

        using var reader = new RepositoryReader(Repo, world.Keys, collected, Authority);
        reader.UseLocationSource(world.Catalogue.ResolveLocation);
        reader.UsePrefetchPolicy(OneReadARecord);
        var output = Path.Combine(SpoolDirectory, "swept-alone");
        var receipt = await RestoreAsync(reader, world.Plan, output);

        Assert.IsTrue(collected.Swept, $"the blob went partway through the run ({collected.Reads} reads)");
        Assert.AreEqual(RestoreOutcome.Failed, receipt.Outcome, "a file the run could no longer read fails the run");
        var failed = receipt.Items.Where(item => item.Outcome == "failed").ToList();
        Assert.IsNotEmpty(failed, "and is named in it");
        Assert.IsTrue(
            failed.All(item => item.Detail!.Contains(nameof(RecordReadOutcome.NotHeld), StringComparison.Ordinal)),
            "as one whose blob is not held, not as damage: " + string.Join("; ", failed.Select(item => item.Detail)));
        Assert.IsNotEmpty(receipt.Items.Where(item => item.Outcome == "restored" && item.Bytes > 0).ToList(), "and the rest restored");
        AssertRestoredByteForByte(
            world, output, receipt.Items.Where(item => item.Outcome == "restored").Select(item => item.Path));
    }

    [TestMethod]
    [DataRow(FormatVersions.RelocatableRecords, DisplayName = "format 3")]
    [DataRow(FormatVersions.DeltaCheckpoints, DisplayName = "format 5")]
    public async Task Compaction_RunsWhileARestoreIsReading_NeverRemovesAnObjectTheRestoreNeeds(ushort format)
    {
        var world = await PublishAsync(format);
        using var held = world.Keys;
        using var _ = world.Catalogue;
        var (first, second, blob) = await TwoSegmentsInOneBlobAsync(world);

        using var reader = new RepositoryReader(Repo, world.Keys, world.Store, Authority);
        reader.UseLocationSource(world.Catalogue.ResolveLocation);
        Assert.AreEqual(RecordReadOutcome.Ok, (await reader.ReadSegmentAsync(first, CancellationToken.None)).Outcome);

        // The compaction lands under the restore: its replacement published,
        // and the blob it drained gone, before the restore's next read of it.
        var (drained, moved) = await CompactAsync(world, blob);
        Assert.AreNotEqual(drained, moved);
        Assert.AreEqual(moved, world.Catalogue.ResolveLocation(second)!.BlobId, "the record is found where it went");
        await world.Store.DeleteAsync(blob, DeleteConditions.None, CancellationToken.None);
        var read = await reader.ReadSegmentAsync(second, CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        Assert.IsEmpty(reader.SkippedBlobs);
    }

    [TestMethod]
    [DataRow(FormatVersions.SealedDataPlane, DisplayName = "format 2")]
    [DataRow(FormatVersions.DeltaCheckpoints, DisplayName = "format 5")]
    public async Task ABlobThatGoesFromTheCopyARestoreReadsAroundTo_IsNotHeldThere_AndIsNoFinding(ushort format)
    {
        // Staging trimmed the blob, so a destination serves it, and then the
        // destination's own retention drops it while the restore still reads.
        var world = await PublishAsync(format);
        using var held = world.Keys;
        using var _ = world.Catalogue;
        var spare = await MirrorAsync(world.Store, SpareRoot);
        var (first, second, blob) = await TwoSegmentsInOneBlobAsync(world);
        await world.Store.DeleteAsync(blob, DeleteConditions.None, CancellationToken.None);

        using var reader = new RepositoryReader(Repo, world.Keys, world.Store, Authority);
        reader.UseLocationSource(world.Catalogue.ResolveLocation);
        reader.UseOtherCopies(Own, [Serve(Sibling, spare)]);
        Assert.AreEqual(RecordReadOutcome.Ok, (await reader.ReadSegmentAsync(first, CancellationToken.None)).Outcome);

        await spare.DeleteAsync(blob, DeleteConditions.None, CancellationToken.None);
        var read = await reader.ReadSegmentAsync(second, CancellationToken.None);

        Assert.AreNotEqual(RecordReadOutcome.Ok, read.Outcome);
        var atSpare = reader.Refusals.Where(refusal => refusal.Source == Sibling).ToList();
        Assert.AreEqual(CopyFault.NotHeld, Assert.ContainsSingle(atSpare).Fault, Assert.ContainsSingle(atSpare).Detail);
        Assert.IsEmpty(reader.Refusals.Where(refusal => refusal.Fault == CopyFault.Damaged).ToList());
    }

    [TestMethod]
    [DataRow(FormatVersions.SealedDataPlane, DisplayName = "format 2")]
    [DataRow(FormatVersions.DeltaCheckpoints, DisplayName = "format 5")]
    public async Task ABlobThatGoesFromACopyWhoseFooterWasRead_IsNotHeldThere_AndThrowsNothing(ushort format)
    {
        // The copy's footer is opened when a record at a location fails its
        // checks there; it is kept, and the blob under it can still go.
        var world = await PublishAsync(format);
        using var held = world.Keys;
        using var _ = world.Catalogue;
        var spare = await MirrorAsync(world.Store, SpareRoot);
        var (first, second, blob) = await TwoSegmentsInOneBlobAsync(world);
        await world.Store.DeleteAsync(blob, DeleteConditions.None, CancellationToken.None);
        RotRecord(SpareRoot, blob, world.Catalogue.ResolveLocation(first)!);

        using var reader = new RepositoryReader(Repo, world.Keys, world.Store, Authority);
        reader.UseLocationSource(world.Catalogue.ResolveLocation);
        reader.UseOtherCopies(Own, [Serve(Sibling, spare)]);
        Assert.AreNotEqual(RecordReadOutcome.Ok, (await reader.ReadSegmentAsync(first, CancellationToken.None)).Outcome);
        Assert.AreEqual(
            CopyFault.Damaged,
            Assert.ContainsSingle(reader.Refusals.Where(refusal => refusal.Source == Sibling)).Fault,
            "the control: the copy's footer was read for a record that is damaged there");

        await spare.DeleteAsync(blob, DeleteConditions.None, CancellationToken.None);
        var before = reader.Refusals.Count;
        var read = await reader.ReadSegmentAsync(second, CancellationToken.None);

        Assert.AreNotEqual(RecordReadOutcome.Ok, read.Outcome);
        var atSpare = reader.Refusals.Skip(before).Where(refusal => refusal.Source == Sibling).ToList();
        Assert.AreEqual(CopyFault.NotHeld, Assert.ContainsSingle(atSpare).Fault, Assert.ContainsSingle(atSpare).Detail);
    }

    [TestMethod]
    [DataRow(FormatVersions.SealedDataPlane, 1, DisplayName = "format 2, after one read")]
    [DataRow(FormatVersions.SealedDataPlane, 2, DisplayName = "format 2, after two reads")]
    [DataRow(FormatVersions.SealedDataPlane, 3, DisplayName = "format 2, after three reads")]
    [DataRow(FormatVersions.DeltaCheckpoints, 1, DisplayName = "format 5, after one read")]
    [DataRow(FormatVersions.DeltaCheckpoints, 2, DisplayName = "format 5, after two reads")]
    [DataRow(FormatVersions.DeltaCheckpoints, 3, DisplayName = "format 5, after three reads")]
    public async Task ARestoreReadingAroundToACopyWhoseBlobGoesPartway_KeepsItsReceipt_AndCallsNothingDamaged(
        ushort format, int servedReads)
    {
        var world = await PublishAsync(format);
        using var held = world.Keys;
        using var _ = world.Catalogue;
        var spare = await MirrorAsync(world.Store, SpareRoot);
        var (_, _, blob) = await TwoSegmentsInOneBlobAsync(world);
        await world.Store.DeleteAsync(blob, DeleteConditions.None, CancellationToken.None);
        var collected = new SweptAfterReadObjectStore(spare, key => key == blob.Value, servedReads);

        using var reader = new RepositoryReader(Repo, world.Keys, world.Store, Authority);
        reader.UseLocationSource(world.Catalogue.ResolveLocation);
        reader.UseOtherCopies(Own, [Serve(Sibling, collected)]);
        reader.UsePrefetchPolicy(OneReadARecord);
        var output = Path.Combine(SpoolDirectory, "swept-at-copy");
        var receipt = await RestoreAsync(reader, world.Plan, output);

        Assert.IsTrue(collected.Swept, $"the blob went from the copy partway through the run ({collected.Reads} reads)");
        Assert.AreEqual(RestoreOutcome.Failed, receipt.Outcome, "a file no copy could still read fails the run");
        Assert.IsNotEmpty(receipt.Items.Where(item => item.Outcome == "failed").ToList(), "and is named in it");
        Assert.IsEmpty(
            reader.Refusals.Where(refusal => refusal.Fault == CopyFault.Damaged).ToList(),
            "a copy whose blob went is no damage to hold against it");
        AssertRestoredByteForByte(
            world, output, receipt.Items.Where(item => item.Outcome == "restored").Select(item => item.Path));
    }

    /// <summary>What the restores read: four incompressible files, their catalogue, and how to write more.</summary>
    private sealed record World(
        RestorePlan Plan,
        CatalogueDb Catalogue,
        LocalFileSystemObjectStore Store,
        RepositoryKeySet Keys,
        IReadOnlyDictionary<string, byte[]> Files,
        string SequencePath,
        ushort Format);

    private static CopySource Serve(string name, IObjectStore store) =>
        new(name, _ => ValueTask.FromResult<IObjectStore?>(store));

    private static async Task<RestoreReceipt> RestoreAsync(RepositoryReader reader, RestorePlan plan, string output) =>
        await new RestoreExecutor(reader, RestoreTargetProfile.ForLocalPlatform()).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "beside-collection-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

    private async Task<World> PublishAsync(ushort format)
    {
        var source = new FakeFileSystemSource();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var index = 0; index < FileCount; index++)
        {
            var content = new byte[200_000];
            new Random(70 + index).NextBytes(content);
            source.AddFile($"data/file-{index}.bin", content, fileId: (ulong)(9_700 + index));
            files[$"data/file-{index}.bin"] = content;
        }

        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue-beside-collection.db"), Repo);
        var spool = Path.Combine(SpoolDirectory, "beside-collection");
        Directory.CreateDirectory(spool);
        var sequencePath = Path.Combine(spool, "sequence.txt");
        await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(sequencePath)),
                spool, format, observer: null, catalogue)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = source,
                    Roots = [new ScanRoot("/")],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat((byte)0xEB, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "beside-collection-tests/1.0",
                },
                CancellationToken.None);

        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat((byte)0xEB, 16).ToArray(), string.Empty,
            RestoreTargetProfile.ForLocalPlatform());
        return new World(plan, catalogue, store, keys, files, sequencePath, format);
    }

    /// <summary>Two segments whose records share one data blob, and that blob's store key.</summary>
    private static async Task<(ObjectId First, ObjectId Second, ObjectKey Blob)> TwoSegmentsInOneBlobAsync(World world)
    {
        using var reader = new RepositoryReader(Repo, world.Keys, world.Store, Authority);
        reader.UseLocationSource(world.Catalogue.ResolveLocation);
        var byBlob = new Dictionary<ObjectKey, List<ObjectId>>();
        foreach (var item in world.Plan.Items.Where(entry => entry.Kind == EntryKind.File))
        {
            var read = await reader.ReadSegmentAsync(item.ObjectId, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
            foreach (var reference in FileVersionManifestCodec.Decode(read.Plaintext!).SegmentReferences)
            {
                var key = BlobKeyOf(world, world.Catalogue.ResolveLocation(reference.ObjectId)!);
                if (!byBlob.TryGetValue(key, out var segments))
                {
                    byBlob[key] = segments = [];
                }

                segments.Add(reference.ObjectId);
            }
        }

        var shared = byBlob.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
            .First(pair => pair.Value.Count >= 2);
        return (shared.Value[0], shared.Value[1], shared.Key);
    }

    private static ObjectKey BlobKeyOf(World world, Repository.Catalogue.ResolvedLocation location)
    {
        using var deriver = new StoreBlobKeyDeriver(world.Keys.KeyIdKey);
        return BlobStoreKeys.ForBlob(BlobClass.Data, location.StoreBlobKey ?? deriver.Derive(location.BlobId));
    }

    /// <summary>Compacts one data blob whole and publishes the move into the catalogue; answers the blob and where its records went.</summary>
    private async Task<(BlobId Drained, BlobId Moved)> CompactAsync(World world, ObjectKey blob)
    {
        using var loaded = new RepositoryReader(Repo, world.Keys, world.Store, Authority);
        await loaded.LoadBlobsAsync(CancellationToken.None);
        var chosen = loaded.Blobs.Single(candidate => candidate.StoreKey == blob);

        var sequence = new WriterSequence(new FileSequenceStateStore(world.SequencePath));
        using var credential = CreateCredential();
        using var compactor = new BlobCompactor(
            Repo, Writer, KeyGeneration.Zero, world.Keys, SmallBlobPolicy, world.Store, sequence,
            SpoolDirectory, world.Format);
        var produced = await compactor.CompactAsync(
            [new CompactionSource(chosen.StoreKey, chosen.BlobId, chosen.Records)], CancellationToken.None);

        using (var storeKeys = new StoreBlobKeyDeriver(world.Keys.KeyIdKey))
        using (var publisher = new IndexPublisher(
            world.Store, Repo, Writer, credential, sequence, logger: null,
            FormatVersions.HasIndexGenerations(world.Format) ? world.Catalogue.MaxIndexGeneration() : null))
        {
            await CompactionPublication.PublishAsync(
                produced, world.Store, storeKeys, publisher, world.Catalogue, generation: 0, CancellationToken.None);
        }

        return (chosen.BlobId, Assert.ContainsSingle(produced).Sealed.BlobId);
    }

    private static void AssertRestoredByteForByte(World world, string output, IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(world.Files.ContainsKey))
        {
            var written = File.ReadAllBytes(Path.Combine(output, path.Replace('/', Path.DirectorySeparatorChar)));
            Assert.IsTrue(world.Files[path].AsSpan().SequenceEqual(written), $"{path} restored other bytes");
        }
    }

    /// <summary>
    /// Flips one byte inside a record's ciphertext in the store at
    /// <paramref name="root"/> alone, where its tag will catch it.
    /// </summary>
    private static void RotRecord(string root, ObjectKey blob, Repository.Catalogue.ResolvedLocation location)
    {
        var path = Path.Combine(root, blob.Value.Replace('/', Path.DirectorySeparatorChar));
        using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        file.Seek((long)location.PhysicalOffset + RecordHeader.Length + 4, SeekOrigin.Begin);
        var original = file.ReadByte();
        file.Seek(-1, SeekOrigin.Current);
        file.WriteByte((byte)(original ^ 0xFF));
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
