using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Retention.Tests;

using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

/// <summary>
/// The destructive half of compaction
/// ([ADR-0067](../../docs/adr/0067-the-keyless-compactor.md); ADR-0025 exit
/// criteria 7 and 12): a blob a compaction pass drained is condemned by the
/// collector on the collector's own terms, tombstoned, swept after its grace,
/// and nothing a surviving snapshot needs is lost by any of it. Establishes
/// FR-GC-001, FR-GC-003 and FR-MAN-019.
/// </summary>
/// <remarks>
/// <para>
/// The pass itself deletes nothing, and that is the design rather than an
/// omission. What compaction changes is <em>where</em> a record lives; the
/// collector already knows how to condemn a blob holding nothing live, and
/// once the index sends readers elsewhere a drained blob holds nothing live.
/// So the tombstone's grace, the sweep's revalidation and the veto rules all
/// apply to it unchanged — a compactor that deleted its own sources would be
/// asserting a conclusion the collector is built to reach.
/// </para>
/// <para>
/// The condemnation is also the sharpest place criterion 12 can be got wrong
/// in reverse. A supersession is only a reason to delete the old bytes while
/// the new ones are really there; an index entry naming a blob nobody holds
/// must condemn nothing at all, or the last copy of a record goes on the
/// strength of a pointer into nothing.
/// </para>
/// <para>
/// The two-writer tests pin what happens when the compaction is published under
/// a second writer, as in an archive adopted by a machine that kept its own
/// identity (ADR-0061). At format 3 every entry ranks at one generation, so
/// between two writers the writer id's bytes decide (ADR-0017 Amendment 1).
/// Under an id that sorts higher the compaction stands. Under one that sorts
/// lower its supersessions would lose to the insertions they relocate, so the
/// pass declines the blob and writes nothing, and the dry run says why
/// (ADR-0017 Amendment 2). At format 4 the pass publishes at a higher index
/// generation than any entry it relocates, so it stands under either id
/// (ADR-0096). Every compaction that is published therefore stands whichever
/// writer published it, which is FR-MAN-015's acceptance.
/// </para>
/// </remarks>
[TestClass]
public sealed class CompactionCollectionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-compaction-collection-tests", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "compaction-collection-passphrase";

    private static readonly string SetId = new('a', 32);

    private static readonly DateTimeOffset Day1 = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

    private string ArchivesRoot => Path.Combine(_root, "archives");

    private string RepoPath => Path.Combine(ArchivesRoot, SetId);

    private string StateDirectory => Path.Combine(_root, "state");

    private string SourceRoot => Path.Combine(_root, "source");

    private string SpoolDirectory => Path.Combine(_root, "spool");

    public CompactionCollectionTests()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(SpoolDirectory);
        WriteOnlyInstallation.Provision(StateDirectory, PassphraseText);
        Directory.CreateDirectory(SourceRoot);
        File.WriteAllText(Path.Combine(SourceRoot, "steady.txt"), new string('s', 200_000));
        File.WriteAllText(Path.Combine(SourceRoot, "churn.txt"), new string('1', 200_000));

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('d', 32),
                    Name = "vault",
                    Kind = DestinationKind.LocalPath,
                    Path = Directory.CreateDirectory(Path.Combine(_root, "vault")).FullName,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = SetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = SourceRoot }],
                    Schedule = "every 4h",
                    Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                    DirectShip = false,
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    /// <summary>The collector condemns a drained blob, and the sweep takes it.</summary>
    [TestMethod]
    public async Task ABlobCompactionDrained_IsCondemnedAndSwept_AndEverySurvivingSnapshotStillReads()
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        await ChurnAsync(store);

        var (drained, produced) = await CompactAsync(store);

        // Criterion 5's conclusion, reached by the collector rather than
        // asserted by the compactor: the blob's records now resolve into the
        // blob that replaced them, so it holds nothing live.
        using (var catalogue = OpenCatalogue(await RepositoryIdAsync(store)))
        {
            var plan = await PlanAsync(store, catalogue);
            Assert.Contains(
                drained,
                plan.DeletableBlobs.Select(blob => blob.BlobId).ToList(),
                "the drained blob was not condemned, so compaction reclaims nothing");
            Assert.DoesNotContain(
                produced,
                plan.DeletableBlobs.Select(blob => blob.BlobId).ToList(),
                "the blob the pass produced was condemned, which would undo the compaction");
        }

        // Tombstone, publish past the decision so the grace runs, sweep.
        await RetainAsync(store, Day1.AddDays(3).AddHours(1));
        File.WriteAllText(Path.Combine(SourceRoot, "churn.txt"), new string('4', 200_000));
        await BackUpAsync(Day1.AddDays(4));
        var swept = await RetainAsync(store, Day1.AddDays(4).AddHours(1));

        Assert.IsGreaterThan(0, swept.Swept!.Deleted, "nothing was swept");
        Assert.IsEmpty(swept.Swept.Findings);
        Assert.DoesNotContain(drained, await StoredBlobIdsAsync(store), "the drained blob is still there");

        // Criterion 7: not one live record was lost on the way.
        await AssertEveryReachableObjectReadsAsync(store);
    }

    /// <summary>
    /// Criterion 12, inverted: a supersession into a blob the store does not
    /// hold condemns nothing. Deleting the produced blob is the same shape as
    /// a destination that never received it or a pass cut before its upload.
    /// </summary>
    [TestMethod]
    public async Task ASupersessionIntoABlobTheStoreDoesNotHold_CondemnsNothing()
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        await ChurnAsync(store);

        var (drained, produced) = await CompactAsync(store);

        using var catalogue = OpenCatalogue(await RepositoryIdAsync(store));
        Assert.Contains(
            drained,
            (await PlanAsync(store, catalogue)).DeletableBlobs.Select(blob => blob.BlobId).ToList(),
            "the drained blob was not condemned even with its replacement present");

        await DeleteBlobAsync(store, produced);

        Assert.DoesNotContain(
            drained,
            (await PlanAsync(store, catalogue)).DeletableBlobs.Select(blob => blob.BlobId).ToList(),
            "the drained blob was condemned on the strength of an entry naming a blob nobody holds");
    }

    /// <summary>
    /// The tombstone says <em>why</em>, and the reason is inside the bytes the
    /// reclaim key signs (specification 11 §3, keys 1–7) — so it is an
    /// attested claim about why data was destroyed rather than a comment.
    /// Until this test the field was a constant at both of the sweep's call
    /// sites: reasons 2, 3 and 4 were declared, encoded, decoded and
    /// round-tripped, and written by nothing.
    /// </summary>
    /// <remarks>
    /// [ADR-0067](../../docs/adr/0067-the-keyless-compactor.md) named the
    /// wrong reason as a limit it could not reach — "the collector condemns by
    /// plan and does not know provenance". It does know: a record here is dead
    /// either because nothing reaches it, or because it is still reached and
    /// the index resolves it into another blob that is present. The second is
    /// a relocation, which is what <em>compacted</em> names, and the planner
    /// was already computing the distinction to decide condemnation at all.
    /// </remarks>
    [TestMethod]
    public async Task ADrainedBlobIsTombstonedCompacted_AndOneNothingReachesIsTombstonedUnreferenced()
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        await ChurnAsync(store);

        // Before any compaction: every condemnation is an ordinary expiry, so
        // every reason is reason 1. This half also pins that the new reason is
        // not simply always produced.
        using (var untouched = OpenCatalogue(await RepositoryIdAsync(store)))
        {
            var expiredOnly = (await PlanAsync(store, untouched)).DeletableBlobs;
            Assert.IsNotEmpty(expiredOnly, "nothing was condemned by expiry, so reason 1 is untested here");
            foreach (var blob in expiredOnly)
            {
                Assert.AreEqual(
                    TombstoneReason.Unreferenced, blob.Reason,
                    $"blob {blob.BlobId} was condemned by expiry and named something else");
            }
        }

        var (drained, _) = await CompactAsync(store);

        using (var catalogue = OpenCatalogue(await RepositoryIdAsync(store)))
        {
            var plan = await PlanAsync(store, catalogue);
            var condemned = plan.DeletableBlobs.Single(blob => blob.BlobId.Equals(drained));
            Assert.AreEqual(
                TombstoneReason.Compacted, condemned.Reason,
                "the drained blob's records live in another blob now, which is reason 3");

            foreach (var other in plan.DeletableBlobs.Where(blob => !blob.BlobId.Equals(drained)))
            {
                Assert.AreEqual(
                    TombstoneReason.Unreferenced, other.Reason,
                    $"blob {other.BlobId} was not drained by the rewrite and named reason 3 anyway");
            }
        }

        // And it survives the two passes that separate a rewrite from its
        // reclaim: what is asserted here is the signed record on disk, not the
        // plan that produced it.
        await RetainAsync(store, Day1.AddDays(3).AddHours(1));

        var tombstones = await TombstonesAsync(store);
        var forDrained = tombstones.Single(tombstone =>
            tombstone.ObjectTypeCode == Tombstone.BlobTypeCode
            && BlobId.FromBytes(tombstone.ObjectId.Span).Equals(drained));
        Assert.AreEqual(TombstoneReason.Compacted, forDrained.Reason);

        foreach (var snapshot in tombstones.Where(
            tombstone => tombstone.ObjectTypeCode != Tombstone.BlobTypeCode))
        {
            Assert.AreEqual(
                TombstoneReason.Unreferenced, snapshot.Reason,
                "a snapshot manifest expires because retention no longer keeps it, which is reason 1");
        }
    }

    /// <summary>
    /// The dry run says what the pass would rewrite and rewrites nothing
    /// (FR-GC-005). The selection is the runner's rather than the caller's
    /// so that what is described and what is done cannot be computed twice
    /// and disagree.
    /// </summary>
    [TestMethod]
    public async Task ADryRun_NamesWhatItWouldRewrite_AndRewritesNothing()
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        await ChurnAsync(store);

        var before = await StoredBlobIdsAsync(store);

        // Bounds this fixture's few dozen bytes of dead data can clear: its
        // files compress to almost nothing, and only a data blob is ever a
        // candidate. What the thresholds themselves are worth is
        // CompactionPolicyTests', and what is asserted here is that a dry run
        // reports the selection and touches nothing.
        var report = await PlanOnlyAsync(
            store, Day1.AddDays(3).AddHours(1), CompactionPolicy.Default with { MinimumReclaim = 1, DeadFraction = 0.01 });

        Assert.IsNotEmpty(report.CompactionCandidates);
        Assert.Contains(
            "compaction would rewrite",
            string.Join("\n", report.Lines),
            StringComparison.Ordinal);

        CollectionAssert.AreEquivalent(
            before.Select(id => id.ToString()).ToList(),
            (await StoredBlobIdsAsync(store)).Select(id => id.ToString()).ToList(),
            "a dry run changed the store");
    }

    /// <summary>
    /// A compaction published under a second writer whose id sorts above the
    /// first stands, as a single writer's does: its supersessions outrank the
    /// insertions they relocate, so the drained blob is condemned and the one
    /// the pass produced is kept.
    /// </summary>
    [TestMethod]
    public async Task AtFormatThree_TwoWriters_ACompactionUnderAnIdThatSortsHigher_Stands()
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        await ChurnAsync(store, FormatVersions.RelocatableRecords);

        using var catalogue = OpenCatalogue(await RepositoryIdAsync(store), live: true);
        var (drained, produced) = await CompactAsync(store, catalogue, SecondWriter(sortsAboveTheFirst: true));

        var condemned = (await PlanAsync(store, catalogue)).DeletableBlobs.Select(blob => blob.BlobId).ToList();
        Assert.Contains(drained, condemned, "the drained blob was not condemned under the higher id");
        Assert.DoesNotContain(produced, condemned, "the produced blob was condemned under the higher id");
    }

    /// <summary>
    /// The same compaction under an id that sorts below the first writer's is
    /// not started. Its supersessions would lose to the insertions they
    /// relocate, so the pass declines the blob and writes nothing at all, not
    /// even its intent, and the next plan still has the blob part dead, for the
    /// collector to take once its records die.
    /// </summary>
    [TestMethod]
    public async Task AtFormatThree_TwoWriters_ACompactionUnderAnIdThatSortsLower_IsDeclined_AndWritesNothing()
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        await ChurnAsync(store, FormatVersions.RelocatableRecords);
        var repositoryId = await RepositoryIdAsync(store);
        var keysBefore = await StoredKeysAsync(store);

        using var catalogue = OpenCatalogue(repositoryId, live: true);
        var (candidate, outcome) = await RunCompactionAsync(store, catalogue, SecondWriter(sortsAboveTheFirst: false));

        Assert.AreEqual(candidate.BlobId, Assert.ContainsSingle(outcome.Declined));
        Assert.IsEmpty(outcome.Published);
        Assert.IsEmpty(outcome.Drained);
        Assert.AreEqual(0, outcome.RecordsMoved);
        CollectionAssert.AreEqual(keysBefore, await StoredKeysAsync(store), "a declined pass wrote to the store");

        var plan = await PlanAsync(store, catalogue);
        Assert.DoesNotContain(candidate.BlobId, plan.DeletableBlobs.Select(blob => blob.BlobId).ToList());
        Assert.Contains(candidate.BlobId, plan.PartlyLiveBlobs.Select(blob => blob.BlobId).ToList());
        await AssertEveryReachableObjectReadsAsync(store);
    }

    /// <summary>
    /// The dry run reads the same rule the pass does, so under the lower id it
    /// offers the blob to no pass and says why, rather than promising a
    /// compaction the pass would decline (FR-GC-005).
    /// </summary>
    [TestMethod]
    public async Task AtFormatThree_TwoWriters_UnderAnIdThatSortsLower_TheDryRunLeavesTheBlobWhole_AndSaysWhy()
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        await ChurnAsync(store, FormatVersions.RelocatableRecords);

        var lower = SecondWriter(sortsAboveTheFirst: false);
        var report = await RetainAsync(
            store, Day1.AddDays(3).AddHours(1), live: true, apply: false, writer: lower,
            compaction: new CompactionPolicy(0.0, 0, long.MaxValue));

        Assert.IsEmpty(report.CompactionCandidates);
        Assert.Contains(
            line => line.Contains("another writer's entries would outrank the rewrite", StringComparison.Ordinal),
            report.Lines);

        // The control: under the backups' own writer the same blob is offered.
        var own = await RetainAsync(
            store, Day1.AddDays(3).AddHours(1), live: true, apply: false,
            compaction: new CompactionPolicy(0.0, 0, long.MaxValue));
        Assert.IsNotEmpty(own.CompactionCandidates);
    }

    /// <summary>
    /// At format 4 the same compaction under the lower id stands (ADR-0096).
    /// The pass publishes at one more than the highest index generation its
    /// catalogue holds, so its supersessions outrank the insertions they
    /// relocate whichever writer published either. The drained blob is
    /// condemned, the produced one is not, and every reachable object reads.
    /// </summary>
    [TestMethod]
    public async Task AtFormatFour_TwoWriters_ACompactionUnderAnIdThatSortsLower_Stands()
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        await ChurnAsync(store, FormatVersions.IndexGenerations);

        using var catalogue = OpenCatalogue(await RepositoryIdAsync(store), live: true);
        var (drained, produced) = await CompactAsync(store, catalogue, SecondWriter(sortsAboveTheFirst: false));

        var condemned = (await PlanAsync(store, catalogue)).DeletableBlobs.Select(blob => blob.BlobId).ToList();
        Assert.Contains(drained, condemned, "the drained blob was not condemned under the lower id");
        Assert.DoesNotContain(produced, condemned, "the produced blob was condemned under the lower id");
        await AssertEveryReachableObjectReadsAsync(store);
    }

    /// <summary>
    /// And the dry run under the lower id offers the blob at format 4, with
    /// no line saying another writer's entries would outrank it.
    /// </summary>
    [TestMethod]
    public async Task AtFormatFour_TwoWriters_UnderAnIdThatSortsLower_TheDryRunOffersTheBlob()
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        await ChurnAsync(store, FormatVersions.IndexGenerations);

        var report = await RetainAsync(
            store, Day1.AddDays(3).AddHours(1), live: true, apply: false, writer: SecondWriter(sortsAboveTheFirst: false),
            compaction: new CompactionPolicy(0.0, 0, long.MaxValue));

        Assert.IsNotEmpty(report.CompactionCandidates);
        Assert.DoesNotContain(
            line => line.Contains("another writer's entries would outrank the rewrite", StringComparison.Ordinal),
            report.Lines);
    }

    /// <summary>
    /// Four backups with churn, so some records in the early blobs die, into
    /// an archive created at <paramref name="formatVersion"/> when one is
    /// named and at the format new sets are born at when not.
    /// </summary>
    private async Task ChurnAsync(LocalFileSystemObjectStore store, ushort? formatVersion = null)
    {
        if (formatVersion is { } version)
        {
            await WriteOnlyInstallation.CreateArchiveAsync(StateDirectory, RepoPath, version);
        }

        await BackUpAsync(Day1);
        File.WriteAllText(Path.Combine(SourceRoot, "churn.txt"), new string('2', 200_000));
        await BackUpAsync(Day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "churn.txt"), new string('3', 200_000));
        await BackUpAsync(Day1.AddDays(2));

        // A first ordinary pass, so the expired snapshots are gone and the
        // early blobs are genuinely part dead rather than merely old.
        await RetainAsync(store, Day1.AddDays(2).AddHours(1));
        await BackUpAsync(Day1.AddDays(3));
        await RetainAsync(store, Day1.AddDays(3).AddMinutes(30));
    }

    /// <summary>
    /// Compacts the first blob the collector reports as part dead, and
    /// requires that the pass drained it into one new blob.
    /// </summary>
    private async Task<(BlobId Drained, BlobId Produced)> CompactAsync(
        LocalFileSystemObjectStore store, CatalogueDb? into = null, WriterId? writer = null)
    {
        var (candidate, outcome) = await RunCompactionAsync(store, into, writer);

        Assert.AreEqual(candidate.BlobId, Assert.ContainsSingle(outcome.Drained));
        return (candidate.BlobId, Assert.ContainsSingle(outcome.Published));
    }

    /// <summary>
    /// Runs one compaction pass over the first blob the collector reports as
    /// part dead, publishing into <paramref name="into"/> (the test's own
    /// catalogue when null) under <paramref name="writer"/> (the backups'
    /// writer when null), and returns what the pass said it did.
    /// </summary>
    private async Task<(CompactableBlob Candidate, CompactionOutcome Outcome)> RunCompactionAsync(
        LocalFileSystemObjectStore store, CatalogueDb? into, WriterId? writer)
    {
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        using var owned = into is null ? OpenCatalogue(opened.Repository.RepositoryId) : null;
        var catalogue = into ?? owned!;

        var backlog = (await PlanAsync(store, catalogue)).PartlyLiveBlobs;
        Assert.IsNotEmpty(backlog, "no blob is part dead, so there is nothing to compact and nothing to prove");

        // A second writer has its own sequence space, outside the state
        // directory the backups' writer allocates from.
        var candidate = backlog[0];
        var sequence = new WriterSequence(
            new FileSequenceStateStore(writer is null
                ? Path.Combine(
                    StateDirectory,
                    $"sequence-{Convert.ToHexStringLower(opened.Repository.RepositoryId.ToArray())}.txt")
                : Path.Combine(_root, "second-writer-sequence.txt")));

        var outcome = await CompactionPass.RunAsync(
            [new CompactionSource(candidate.StoreKey, candidate.BlobId, candidate.Live)],
            opened.Repository, store, store,
            writer ?? WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId),
            CapturePolicy.Default, sequence, catalogue, SpoolDirectory,
            (ulong)Day1.AddDays(3).AddHours(1).ToUnixTimeMilliseconds(),
            declaredMaxDurationMs: 3_600_000, expiryGeneration: 16, CancellationToken.None);

        return (candidate, outcome);
    }

    /// <summary>
    /// The catalogue the compaction pass publishes into and the planner
    /// resolves against — one per test, at the archive's real repository id
    /// rather than the set's, which are different things and only look alike
    /// when a set was provisioned by hand.
    /// </summary>
    private CatalogueDb OpenCatalogue(RepositoryId repositoryId, bool live = false)
    {
        if (!live)
        {
            return CatalogueDb.Open(Path.Combine(StateDirectory, "compaction-catalogue.db"), repositoryId);
        }

        // The service's own catalogue, which every backup applied its delta
        // to: the one place both writers' rows meet, as they do in service.
        var path = Path.Combine(StateDirectory, $"catalogue-{repositoryId}.db");
        Assert.IsTrue(File.Exists(path), $"the backups wrote no catalogue at {path}");
        return CatalogueDb.Open(path, repositoryId);
    }

    /// <summary>
    /// A writer other than the backups', whose id sorts above or below theirs
    /// in the byte order the catalogue's tie-break compares.
    /// </summary>
    private WriterId SecondWriter(bool sortsAboveTheFirst)
    {
        var first = LocalState.LoadOrCreate(StateDirectory).WriterId;
        var second = new byte[first.Length];
        if (sortsAboveTheFirst)
        {
            Array.Fill(second, (byte)0xFF);
        }
        else
        {
            second[^1] = 0x01;
        }

        Assert.AreEqual(
            sortsAboveTheFirst ? -1 : 1, Math.Sign(first.AsSpan().SequenceCompareTo(second)),
            "the backups' writer id does not sort where this test needs it");
        return WriterId.FromBytes(second);
    }

    private static async Task<RepositoryId> RepositoryIdAsync(LocalFileSystemObjectStore store) =>
        (await RepositoryLifecycle.ReadDescriptorAsync(store, CancellationToken.None)).RepositoryId;

    private static async Task<CollectionPlan> PlanAsync(LocalFileSystemObjectStore store, CatalogueDb catalogue)
    {
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;

        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        var selection = RetentionPlanner.Select(
            [.. survey.Snapshots.Select(snapshot => snapshot.Fact)],
            new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
            Day1.AddDays(4));

        var gate = ReplicationGate.Apply(
            selection.Expire, [], _ => null, keptBy: (_, _) => true, deferralDays: null,
            (ulong)Day1.AddDays(4).ToUnixTimeMilliseconds());

        var protectedIds = selection.Keep.Select(keep => keep.Snapshot.SnapshotId).ToHashSet(StringComparer.Ordinal);
        var protectedSnapshots = survey.Snapshots
            .Where(snapshot => protectedIds.Contains(snapshot.Fact.SnapshotId))
            .ToList();

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store, opened.Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var (reachable, unwalkable) = await StagingMark.MarkAsync(reader, protectedSnapshots, CancellationToken.None);

        IReadOnlyList<JournalRecord> records;
        int unparseable;
        using (var journal = new JournalReader(store, repository.RepositoryId, repository.Credential))
        {
            (records, unparseable, _) = await journal.LoadAsync(0, CancellationToken.None);
        }

        var intents = IntentSurveyor.Survey(
            records, unparseable, currentGeneration: 0,
            (ulong)Day1.AddDays(4).ToUnixTimeMilliseconds(), skewMarginMs: 300_000);

        return CollectionPlanner.Plan(
            survey, selection, gate, reader, reachable, unwalkable, intents, ListingConsistency.Strong,
            objectId => catalogue.ResolveLocation(objectId)?.BlobId);
    }

    private static async Task AssertEveryReachableObjectReadsAsync(LocalFileSystemObjectStore store)
    {
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;
        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        Assert.IsNotEmpty(survey.Snapshots);

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store, opened.Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        Assert.IsEmpty(reader.SkippedBlobs);

        var (reachable, unwalkable) = await StagingMark.MarkAsync(reader, survey.Snapshots, CancellationToken.None);
        Assert.IsEmpty(unwalkable);

        foreach (var objectId in reachable)
        {
            var result = await reader.ReadSegmentAsync(objectId, CancellationToken.None);
            Assert.AreEqual(
                Repository.Packing.RecordReadOutcome.Ok,
                result.Outcome,
                $"the reachable object {objectId} would not read: {result.Detail}");
        }
    }

    private static async Task DeleteBlobAsync(LocalFileSystemObjectStore store, BlobId blobId)
    {
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            var envelope = new byte[Math.Min(Repository.Packing.BlobEnvelope.MaxLength, entry.Length)];
            using (var read = await store.OpenReadAsync(
                entry.Key, new ObjectRange(0, envelope.Length), CancellationToken.None))
            {
                await read.Content!.ReadExactlyAsync(envelope);
            }

            if (Repository.Packing.BlobEnvelope.Parse(envelope).BlobId.Equals(blobId))
            {
                await store.DeleteAsync(entry.Key, DeleteConditions.None, CancellationToken.None);
                return;
            }
        }

        Assert.Fail($"blob {blobId} was not in the store");
    }

    private static async Task<List<BlobId>> StoredBlobIdsAsync(LocalFileSystemObjectStore store)
    {
        var ids = new List<BlobId>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            var envelope = new byte[Math.Min(Repository.Packing.BlobEnvelope.MaxLength, entry.Length)];
            using var read = await store.OpenReadAsync(
                entry.Key, new ObjectRange(0, envelope.Length), CancellationToken.None);
            await read.Content!.ReadExactlyAsync(envelope);
            ids.Add(Repository.Packing.BlobEnvelope.Parse(envelope).BlobId);
        }

        return ids;
    }

    /// <summary>
    /// Every tombstone on the store, opened and decoded the way the sweep's
    /// own delete gate opens them: the standalone record's framing, the
    /// metadata key for the generation it was sealed under, then the codec.
    /// Reading the signed bytes rather than the plan is the point — a reason
    /// the planner computed and the sweep dropped would pass an assertion on
    /// the plan and fail the audit record.
    /// </summary>
    private static async Task<List<Tombstone>> TombstonesAsync(LocalFileSystemObjectStore store)
    {
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;
        var found = new List<Tombstone>();

        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("tombstones/"), ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory, CancellationToken.None);

            var record = StandaloneRecordFraming.Parse(memory.ToArray());
            var metadataKey = repository.Credential.DeriveMetadataKey(record.KeyGeneration);
            Assert.IsTrue(
                StandaloneRecordCipher.TryOpen(record, repository.RepositoryId, metadataKey, out var plaintext),
                $"tombstone {entry.Key} did not open");
            found.Add(TombstoneCodec.Decode(plaintext).Value);
        }

        Assert.IsNotEmpty(found, "no tombstone was written, so nothing was asserted about any reason");
        return found;
    }

    // Each backup is preceded by what a filesystem that moves access times on
    // every read would have done since the last: a temp directory on a tmpfs
    // without relatime does, one on a relatime disk does at most daily. What
    // this class asserts must not depend on how a machine mounted it.
    private async Task BackUpAsync(DateTimeOffset now)
    {
        // Windows opens a folder for its times only through the folder's own
        // call, so files and folders are touched apart.
        var read = DateTime.UtcNow;
        foreach (var file in Directory.EnumerateFiles(SourceRoot, "*", SearchOption.AllDirectories))
        {
            File.SetLastAccessTimeUtc(file, read);
        }

        foreach (var folder in Directory.EnumerateDirectories(SourceRoot, "*", SearchOption.AllDirectories)
            .Append(SourceRoot))
        {
            Directory.SetLastAccessTimeUtc(folder, read);
        }

        var result = await AgentPass.RunAsync(ArchivesRoot, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private async Task<RetentionReport> RetainAsync(
        LocalFileSystemObjectStore store,
        DateTimeOffset now,
        bool live = false,
        bool apply = true,
        WriterId? writer = null,
        CompactionPolicy? compaction = null)
    {
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        using var catalogue = OpenCatalogue(opened.Repository.RepositoryId, live);

        // The pass is run by the writer that would compact, as the service's
        // is, and judges each candidate by the rule the compaction pass uses.
        var running = writer ?? WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId);
        var sync = DestinationSyncStore.Open(StateDirectory);
        return await RetentionRunner.RunAsync(
            store, opened.Repository, new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
            [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name), _ => TrimVerification.None,
            running, apply,
            (ulong)now.ToUnixTimeMilliseconds(), CancellationToken.None, reclaim: opened.Reclaim,
            resolveLocation: objectId => catalogue.ResolveLocation(objectId)?.BlobId,
            compactionPolicy: compaction,
            relocationStands: CompactionPass.RelocationStands(opened.Repository, running, catalogue));
    }

    private static async Task<List<string>> StoredKeysAsync(LocalFileSystemObjectStore store)
    {
        var keys = new List<string>();
        await foreach (var entry in store.ListAsync(ObjectPrefix.All, ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key.Value);
        }

        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    private async Task<RetentionReport> PlanOnlyAsync(
        LocalFileSystemObjectStore store, DateTimeOffset now, CompactionPolicy policy)
    {
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        using var catalogue = OpenCatalogue(opened.Repository.RepositoryId);

        var sync = DestinationSyncStore.Open(StateDirectory);
        return await RetentionRunner.RunAsync(
            store, opened.Repository, new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
            [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name), _ => TrimVerification.None,
            WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId), apply: false,
            (ulong)now.ToUnixTimeMilliseconds(), CancellationToken.None,
            resolveLocation: objectId => catalogue.ResolveLocation(objectId)?.BlobId,
            compactionPolicy: policy);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
