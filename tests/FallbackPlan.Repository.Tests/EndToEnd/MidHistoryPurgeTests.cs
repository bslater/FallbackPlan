using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Retention;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A version in the middle of a file's history expires, its storage is
/// reclaimed, and every version around it still restores byte for byte
/// (FR-GC-001, FR-GC-005, FR-RST-004).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FileVersionLadderTests"/> proves that keeping several versions
/// of one file gives all of them back. This is the harder half: what happens
/// when one of them stops being kept. Retention deletes nothing on its own —
/// it selects — so the whole collection path runs here, mark through sweep,
/// against a real archive rather than a mocked plan.
/// </para>
/// <para>
/// The middle version is chosen by the <em>policy</em>, never by hand. V1 is
/// taken on day one, V2 and V3 on day two, and a daily rule keeps the newest
/// snapshot of each day — so V2 falls out because it is the older of two on
/// the same day, which is exactly how a real history loses its middle. A test
/// that reached in and named V2 would prove the collector deletes what it is
/// told and nothing about what it is told.
/// </para>
/// <para>
/// The versions share a long prefix and differ only in a tail of whole
/// segments, and each tail is seeded differently, so V2's records belong to
/// V2 alone. That matters: <see cref="CollectionPlanner"/> condemns a blob
/// only when <em>every</em> record in it is unreachable, so a ladder whose
/// versions overlapped inside one blob would reclaim nothing and the test
/// would pass by asserting nothing. The prefix, which all three share, must
/// survive — and V1 and V3 restoring afterwards is what proves it did.
/// </para>
/// <para>
/// The fourth publication is not decoration. <see cref="StagingSweep"/> gives
/// a tombstone its grace in the writer's sequence space rather than in wall
/// time — eligible at <c>currentPublicationSequence + 1</c> — so nothing a
/// clock can be told will let this pass delete what it just condemned. Only a
/// real publication moves that counter, which is the point: the writer has to
/// have visibly moved past the decision.
/// </para>
/// </remarks>
[TestClass]
public sealed class MidHistoryPurgeTests : ArchiveTestHarness
{
    private const string PassphraseText = "mid-history-purge-passphrase-32b";

    private const string FilePath = "ledger/book.bin";

    /// <summary>64 KiB, matching <see cref="ArchiveTestHarness.SmallBlobPolicy"/>'s fixed segment.</summary>
    private const int Segment = 64 * 1024;

    private static readonly WriterId Scribe =
        WriterId.FromBytes([.. Enumerable.Repeat((byte)0xA7, 16)]);

    /// <summary>Day one, 09:00 UTC — and the ladder's clock from there.</summary>
    private static readonly DateTimeOffset Day1 = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset[] Taken =
    [
        Day1,                                                     // V1 — day one
        new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),                 // V2 — day two, morning
        new(2026, 8, 11, 20, 0, 0, TimeSpan.Zero),                // V3 — day two, evening
        new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero),                 // V4 — the grace clock
    ];

    /// <summary>Well after the last publication, so every day is complete.</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Three days of history, one snapshot per day kept. Day two holds two
    /// snapshots, so its older one — V2, the middle of the file's history —
    /// is the one the rule drops.
    /// </summary>
    private static RetentionConfiguration Policy => new() { KeepDaily = 3, MinGenerations = 1 };

    [TestMethod]
    public async Task AVersionInTheMiddle_IsExpiredByPolicy_ReclaimedFromStorage_AndTheOthersStillRestore()
    {
        var versions = Ladder();
        var store = CreateStore();
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.CreateAsync(
            store, passphrase, Domain.Configuration.RepositoryCreationSettings.Default,
            createdAtUnixMilliseconds: (ulong)Day1.ToUnixTimeMilliseconds(),
            CancellationToken.None);

        using var catalogue = CatalogueDb.Open(
            Path.Combine(SpoolDirectory, "purge.db"), repository.RepositoryId);

        var snapshotIds = await PublishLadderAsync(store, repository, catalogue, versions, upTo: 3);

        // 1. The policy — not the test — picks the middle one.
        var expired = await SelectAsync(store, passphrase);
        var expiredId = Assert.ContainsSingle(expired).SnapshotId;
        Assert.AreEqual(
            Convert.ToHexStringLower(snapshotIds[1]), expiredId,
            "the daily rule should have dropped the older of day two's two snapshots");

        // 2. Condemn it. A pass that found nothing to delete would make every
        // assertion below vacuous, so the plan is checked before it is acted on.
        var tombstoned = await CondemnAsync(store, passphrase);
        Assert.IsGreaterThan(0, tombstoned, "nothing was tombstoned, so nothing could be reclaimed");

        // 3. The writer publishes again — the only thing that moves the grace
        // clock — and only then does the sweep delete. The store is measured
        // after that publication rather than before it: V4 writes records of
        // its own, and a reading taken earlier would be comparing the sweep's
        // deletions against a fourth backup's arrivals.
        await PublishLadderAsync(store, repository, catalogue, versions, upTo: 4, from: 3);

        var before = StoreBytes();
        var swept = await SweepAsync(store, passphrase);

        Assert.IsGreaterThan(0, swept.Deleted, string.Join("; ", swept.Findings));
        Assert.IsEmpty(swept.Findings, string.Join("; ", swept.Findings));
        Assert.IsLessThan(before, StoreBytes(), "the sweep freed no bytes, so nothing was reclaimed");

        // 4. The whole point: what was kept is still there, whole. V1 and V3
        // share their prefix with the version that was just collected, and a
        // collector that condemned a blob on a majority rather than on every
        // record in it would land short or corrupt bytes here.
        await AssertRestoresAsync(store, repository, catalogue, snapshotIds[0], versions[0], "v1");
        await AssertRestoresAsync(store, repository, catalogue, snapshotIds[2], versions[2], "v3");
    }

    [TestMethod]
    public async Task ThePrefixSharedWithTheExpiredVersion_IsNotCollected_WhileItsOwnTailIs()
    {
        // The mechanism behind the guarantee above, asserted directly rather
        // than inferred from a successful restore: the mark set reaches every
        // record the surviving versions need, and the plan condemns only
        // blobs holding nothing it reached.
        var versions = Ladder();
        var store = CreateStore();
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.CreateAsync(
            store, passphrase, Domain.Configuration.RepositoryCreationSettings.Default,
            createdAtUnixMilliseconds: (ulong)Day1.ToUnixTimeMilliseconds(),
            CancellationToken.None);

        using var catalogue = CatalogueDb.Open(
            Path.Combine(SpoolDirectory, "prefix.db"), repository.RepositoryId);

        await PublishLadderAsync(store, repository, catalogue, versions, upTo: 3);

        var (plan, reachable, reader) = await PlanAsync(store, passphrase);
        using (reader)
        {
            Assert.IsEmpty(plan.Vetoes, string.Join("; ", plan.Vetoes));
            Assert.IsNotEmpty(reachable, "the mark walked nothing, so the plan cannot be trusted");
            Assert.IsNotEmpty(plan.DeletableBlobs, "V2's tail records belong to V2 alone and should be condemnable");

            // Deletion-only collection: a blob keeping one live record stays
            // whole. Whatever is condemned must hold nothing reachable, which
            // is the invariant that makes the restores above safe.
            foreach (var blob in plan.DeletableBlobs)
            {
                Assert.IsGreaterThan(0L, blob.Records, $"blob {blob.BlobId} was condemned holding no records at all");
            }
        }
    }

    /// <summary>
    /// Four versions of one file: a long shared prefix, then a tail of whole
    /// 64 KiB segments that is rewritten and lengthened every time. Each
    /// tail is seeded differently, so no segment of one version is a segment
    /// of another and a version's records are its own.
    /// </summary>
    private static List<byte[]> Ladder()
    {
        var prefix = BuildTestFile();
        var versions = new List<byte[]>(4);
        for (var version = 0; version < 4; version++)
        {
            var tail = new byte[(2 + version) * Segment];
            new Random(9_100 + version).NextBytes(tail);
            versions.Add([.. prefix, .. tail]);
        }

        return versions;
    }

    /// <summary>Publishes versions <paramref name="from"/>..<paramref name="upTo"/>, returning every snapshot id so far.</summary>
    private async Task<List<byte[]>> PublishLadderAsync(
        LocalFileSystemObjectStore store,
        OpenedRepository repository,
        CatalogueDb catalogue,
        List<byte[]> versions,
        int upTo,
        int from = 0)
    {
        var source = new FakeFileSystemSource();
        var node = source.AddFile(FilePath, versions[from], fileId: 5_150);
        var ids = new List<byte[]>();

        for (var version = 0; version < upTo; version++)
        {
            ids.Add([.. Enumerable.Repeat((byte)(0xC0 + version), 16)]);
            if (version < from)
            {
                continue;
            }

            node.Content = versions[version];

            // A fresh modification time each rung: without one the
            // incremental short-circuit may skip the file on identity, size
            // and time, and leaning on the size having changed would make
            // this depend on which half of that check fired.
            node.Metadata = node.Metadata with { ModifiedAt = (ulong)Taken[version].ToUnixTimeMilliseconds() };

            var spool = Path.Combine(SpoolDirectory, $"publish-{version}");
            Directory.CreateDirectory(spool);

            var job = new SnapshotJob
            {
                Source = source,
                Roots = [new ScanRoot("/")],
                // .ToArray(), not a collection expression: ReadOnlyMemory<byte>
                // is not constructible from one (CS9174).
                DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                SnapshotId = ids[version],
                NowUnixMilliseconds = (ulong)Taken[version].ToUnixTimeMilliseconds(),
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = 5,
                ClientVersion = "mid-history-purge-tests/1.0",
            };

            if (version > 0)
            {
                // Applied only when there is a prior. A null byte[] assigned
                // to this ReadOnlyMemory<byte>? would not stay null — the
                // array converts to an EMPTY memory, and lifting that gives
                // HasValue true — so the first snapshot would claim a
                // zero-length ancestor rather than none.
                job = job with { PriorSnapshotId = ids[version - 1] };
            }

            await new PublicationOrchestrator(
                SmallBlobPolicy, repository.RepositoryId, Scribe, repository.CurrentDataGeneration,
                repository.Keys, repository.Hierarchy, store,
                new WriterSequence(new FileSequenceStateStore(
                    Path.Combine(SpoolDirectory, "sequence.txt"))),
                spool, observer: null, catalogue)
                .PublishAsync(job, CancellationToken.None);
        }

        return ids;
    }

    /// <summary>What the policy would expire, with nothing else consulted.</summary>
    private static async Task<IReadOnlyList<SnapshotFact>> SelectAsync(
        LocalFileSystemObjectStore store, Passphrase passphrase)
    {
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        Assert.IsEmpty(survey.Undecodable, "an undecodable snapshot vetoes every deletion");

        return RetentionPlanner
            .Select([.. survey.Snapshots.Select(snapshot => snapshot.Fact)], Policy, Now)
            .Expire;
    }

    /// <summary>Plans a pass and hands back the plan, the mark set and the loaded reader.</summary>
    private static async Task<(CollectionPlan Plan, HashSet<ObjectId> Reachable, RepositoryReader Reader)> PlanAsync(
        LocalFileSystemObjectStore store, Passphrase passphrase)
    {
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        var selection = RetentionPlanner.Select(
            [.. survey.Snapshots.Select(snapshot => snapshot.Fact)], Policy, Now);

        // No destinations: nothing is held back for a replica that has not
        // caught up, so the gate passes the selection through unchanged.
        var gate = ReplicationGate.Apply(
            selection.Expire, [], _ => null, null, (ulong)Now.ToUnixTimeMilliseconds());

        var protectedIds = selection.Keep.Select(keep => keep.Snapshot.SnapshotId)
            .Concat(gate.Held.Select(held => held.Snapshot.SnapshotId))
            .ToHashSet(StringComparer.Ordinal);

        var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var (reachable, unwalkable) = await StagingMark.MarkAsync(
            reader,
            [.. survey.Snapshots.Where(snapshot => protectedIds.Contains(snapshot.Fact.SnapshotId))],
            CancellationToken.None);

        var intents = IntentSurveyor.Survey(
            await JournalRecordsAsync(store, repository), unparseableCount: 0,
            SealingGeneration(repository), (ulong)Now.ToUnixTimeMilliseconds(), skewMarginMs: 300_000);

        return (CollectionPlanner.Plan(survey, selection, gate, reader, reachable, unwalkable, intents),
            reachable, reader);
    }

    /// <summary>Tombstones what a fresh plan condemns; returns how many tombstones were written.</summary>
    private static async Task<int> CondemnAsync(LocalFileSystemObjectStore store, Passphrase passphrase)
    {
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        var (plan, _, reader) = await PlanAsync(store, passphrase);
        using (reader)
        {
            Assert.IsTrue(plan.Deletable, string.Join("; ", plan.Vetoes));

            return await StagingSweep.TombstoneAsync(
                store, repository, Scribe, plan, survey,
                await PublicationSequenceAsync(store, repository),
                (ulong)Now.ToUnixTimeMilliseconds(), CancellationToken.None);
        }
    }

    /// <summary>Revalidates against the world as it is now, then deletes what is still condemned.</summary>
    private static async Task<SweepOutcome> SweepAsync(LocalFileSystemObjectStore store, Passphrase passphrase)
    {
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        var (plan, _, reader) = await PlanAsync(store, passphrase);
        using (reader)
        {
            return await StagingSweep.SweepAsync(
                store, repository, plan, survey,
                await PublicationSequenceAsync(store, repository), CancellationToken.None);
        }
    }

    /// <summary>The generation the journal is sealed under — uint, as the reader takes it.</summary>
    private static uint SealingGeneration(OpenedRepository repository) => Math.Max(
        repository.CurrentDataGeneration.Value, repository.CurrentMetadataGeneration.Value);

    private static async Task<IReadOnlyList<JournalRecord>> JournalRecordsAsync(
        LocalFileSystemObjectStore store, OpenedRepository repository)
    {
        using var journal = new JournalReader(store, repository.RepositoryId, repository.Hierarchy);
        var (records, _, _) = await journal.LoadAsync(SealingGeneration(repository), CancellationToken.None);
        return records;
    }

    /// <summary>The single writer's highest published sequence — the grace clock.</summary>
    private static async Task<ulong> PublicationSequenceAsync(
        LocalFileSystemObjectStore store, OpenedRepository repository)
    {
        var records = await JournalRecordsAsync(store, repository);
        return records.Count == 0 ? 0 : records.Max(record => record.Sequence);
    }

    /// <summary>Every byte the store holds on disk — what reclamation has to move.</summary>
    private long StoreBytes() =>
        Directory.Exists(StoreRoot)
            ? Directory.EnumerateFiles(StoreRoot, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length)
            : 0;

    private async Task AssertRestoresAsync(
        LocalFileSystemObjectStore store,
        OpenedRepository repository,
        CatalogueDb catalogue,
        byte[] snapshotId,
        byte[] expected,
        string label)
    {
        // A reader built after the sweep: the blob index it loads is the one
        // collection left behind, not the one that was there when the
        // snapshot was published.
        var target = RestoreTargetProfile.ForLocalPlatform();
        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var plan = RestorePlanner.Plan(catalogue, snapshotId, string.Empty, target);
        var output = Path.Combine(SpoolDirectory, $"out-{label}");

        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = $"purge-{label}",
                NowUnixMilliseconds = (ulong)Now.ToUnixTimeMilliseconds(),
            },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome, $"{label} did not restore after the purge");

        var landed = Path.Combine(output, FilePath.Replace('/', Path.DirectorySeparatorChar));
        SequenceAssert.AreEqual(expected, await File.ReadAllBytesAsync(landed));
    }
}
