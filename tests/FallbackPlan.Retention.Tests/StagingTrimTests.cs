using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// The staging trim (ADR-0034 §6): historic data blobs leave staging once
/// every destination entitled to them verifiably holds them — and only then.
/// The newest snapshot's closure never trims (it is the dedup cache), an
/// unverifiable destination blocks everything it is entitled to, a narrow
/// destination blocks nothing it dropped, and the archive keeps publishing
/// and converging cleanly afterwards.
/// </summary>
[TestClass]
public sealed class StagingTrimTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-staging-trim", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "staging-trim-passphrase!!";
    private static readonly string SetId = new('a', 32);

    private string ArchivesRoot => Path.Combine(_root, "archives");
    private string RepoPath => Path.Combine(ArchivesRoot, SetId);
    private string StateDirectory => Path.Combine(_root, "state");
    private string SourceRoot => Path.Combine(_root, "source");
    private string VaultPath => Path.Combine(_root, "vault");

    private static readonly DateTimeOffset Day1 = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

    public StagingTrimTests()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(SourceRoot);
        Directory.CreateDirectory(VaultPath);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day one content");

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('1', 32), Name = "vault",
                    Kind = DestinationKind.LocalPath, Path = VaultPath,
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
                    Destinations =
                    [
                        // Rules in force, but wide: the vault keeps everything,
                        // so fan-out converges (the S1 path) and every blob is
                        // entitled at the vault.
                        new SetDestinationReference
                        {
                            Ref = "vault",
                            Retention = new RetentionConfiguration { MinGenerations = 10 },
                        },
                    ],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    [TestMethod]
    public async Task RetentionApply_EveryHistoricBlobVerifiedAtTheVault_TrimsThemAndTheSetKeepsWorking()
    {
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);
        Assert.HasCount(3, await ListAsync(store, "blobs/data/"));

        // Apply: the two historic data blobs go — the newest snapshot's
        // closure stays, because it is what the next backup dedups against.
        var report = await RunAsync(store, VaultVerification, apply: true, now: Day1.AddDays(2).AddHours(1));
        Assert.Contains(
            line => line.StartsWith("trimmed: 2 historic data blob(s)", StringComparison.Ordinal), report.Lines);
        Assert.HasCount(1, await ListAsync(store, "blobs/data/"));

        // Metadata is untouched: snapshots, meta blobs, journal all stay, so
        // every derivation the hub runs still sees the full history.
        Assert.HasCount(3, await ListAsync(store, "snapshots/"));
        Assert.IsNotEmpty(await ListAsync(store, "blobs/meta/"));

        // The set keeps publishing: sequence continuity is untouched because
        // trim never touches the journal or the snapshots.
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day four content");
        await BackUpAsync(Day1.AddDays(3));
        Assert.HasCount(4, await ListAsync(store, "snapshots/"));

        // That backup's fan-out CONVERGED the vault while staging was a
        // subset — and deleted nothing there (the S1 rule): the replica still
        // holds all four days' data blobs and walks clean end to end.
        var replica = new LocalFileSystemObjectStore(Directory.GetDirectories(VaultPath).Single());
        Assert.HasCount(4, await ListAsync(replica, "blobs/data/"));
        await AssertWalksCleanAsync(replica, expectedSnapshots: 4);

        // The next pass trims the blob day four's publication superseded —
        // trim converges pass over pass instead of oscillating.
        var second = await RunAsync(store, VaultVerification, apply: true, now: Day1.AddDays(3).AddHours(1));
        Assert.Contains(
            line => line.StartsWith("trimmed: 1 historic data blob(s)", StringComparison.Ordinal), second.Lines);
        Assert.HasCount(1, await ListAsync(store, "blobs/data/"));
    }

    [TestMethod]
    public async Task RetentionApply_ADestinationNothingCanVouchFor_HoldsEveryBlobItIsEntitledTo()
    {
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        // The vault's drive is unplugged: no store to probe, no ledger claim
        // to trust — nothing may leave staging (FR-GC-009's discipline), and
        // the report names WHO could not vouch, not just how much stayed.
        var report = await RunAsync(store, _ => TrimVerification.None, apply: true, now: Day1.AddDays(2).AddHours(1));

        Assert.Contains(
            line => line.StartsWith("trim held back: 2 data blob(s)", StringComparison.Ordinal), report.Lines);
        Assert.Contains(
            line => line.Contains("destination 'vault' cannot vouch", StringComparison.Ordinal), report.Lines);
        Assert.IsFalse(report.Lines.Any(line => line.StartsWith("trimmed:", StringComparison.Ordinal)));
        Assert.HasCount(3, await ListAsync(store, "blobs/data/"));
    }

    [TestMethod]
    public async Task RetentionApply_ABlobNoSurveyedSnapshotReaches_IsTheCollectorsBusinessNotTheTrims()
    {
        // Deleting a snapshot object from staging (what the sweep does)
        // leaves its unique data blob unreachable — debris. A destination
        // without rules is entitled to everything, and the replica really
        // holds the blob, but the trim's direct delete must never stand in
        // for the tombstone cycle's grace and revalidation (FR-GC-006).
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        using (var passphrase = Passphrase.Create(PassphraseText))
        using (var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None))
        {
            var surveyed = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
            var oldest = surveyed.Snapshots.MinBy(snapshot => snapshot.Fact.PublicationSequence)!;
            await store.DeleteAsync(oldest.StoreKey, DeleteConditions.None, CancellationToken.None);
        }

        // Day two's blob is history and trims; day one's is debris and
        // stays; day three's is the current generation and stays.
        var report = await RunAsyncWithoutRules(store, now: Day1.AddDays(2).AddHours(1));

        Assert.Contains(
            line => line.StartsWith("trimmed: 1 historic data blob(s)", StringComparison.Ordinal), report.Lines);
        Assert.HasCount(2, await ListAsync(store, "blobs/data/"));
    }

    [TestMethod]
    public async Task Trim_ALivePublicationIntentCoversABlob_HoldsItWhateverTheReplicaSays()
    {
        // The single highest-consequence exclusion: an in-flight
        // publication's blob may already sit at a whole-copy replica, so the
        // store probe answers yes — and deleting the staging copy would tear
        // the publication the intent journal exists to protect (ADR-0009).
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);

        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        // Fabricate the collector's view: one live intent covering every
        // data blob in the archive — the shape of a capture mid-flight.
        var covered = reader.Blobs
            .Where(blob => blob.StoreKey.Value.StartsWith("blobs/data/", StringComparison.Ordinal))
            .Select(blob => blob.BlobId)
            .ToList();
        var intents = new FallbackPlan.Repository.Index.Journal.IntentSurvey(
            [
                new FallbackPlan.Repository.Index.Journal.LiveIntent(
                    WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId),
                    Sequence: 1,
                    new FallbackPlan.Repository.Index.Journal.JournalPayload.WriteIntent(
                        Convert.FromHexString(SetId), covered, 3_600_000, 999,
                        FallbackPlan.Repository.Index.Journal.IntentPurpose.Backup),
                    IssuedAt: 0,
                    covered),
            ],
            HasUnparseableIntent: false);

        var sync = DestinationSyncStore.Open(StateDirectory);
        var plan = await StagingTrim.PlanAsync(
            store, reader, survey,
            setPolicy: null,
            [new SetDestinationReference { Ref = "vault", Retention = new RetentionConfiguration { MinGenerations = 10 } }],
            VaultVerification,
            name => sync.Find(SetId, name),
            intents, Day1.AddDays(2).AddHours(1), CancellationToken.None);

        Assert.IsEmpty(plan.Eligible);
    }

    [TestMethod]
    public async Task RetentionApply_AReplicaStoreThatFaults_HoldsTheBlobsInsteadOfAbortingThePass()
    {
        // A store fault is not evidence of anything except that nothing can
        // be vouched for — whatever exception a provider surfaces it as. It
        // must degrade to held-back, never abort the whole retention pass.
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        var report = await RunAsync(
            store,
            _ => TrimVerification.AgainstStore(new ThrowingStore()),
            apply: true,
            now: Day1.AddDays(2).AddHours(1));

        Assert.Contains(
            line => line.StartsWith("trim held back: 2 data blob(s)", StringComparison.Ordinal), report.Lines);
        Assert.HasCount(3, await ListAsync(store, "blobs/data/"));
    }

    /// <summary>A provider whose every call surfaces a non-IO fault.</summary>
    private sealed class ThrowingStore : IObjectStore
    {
        public StoreCapabilities Capabilities => new();

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the provider's session expired");

        public ValueTask<PutResult> PutAsync(
            ObjectKey key, Func<CancellationToken, ValueTask<Stream>> openContent,
            PutConditions conditions, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the provider's session expired");

        public IAsyncEnumerable<ObjectEntry> ListAsync(
            ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the provider's session expired");

        public ValueTask<OpenReadResult> OpenReadAsync(
            ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the provider's session expired");

        public ValueTask<DeleteResult> DeleteAsync(
            ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the provider's session expired");
    }

    [TestMethod]
    public async Task RetentionApply_APeerThatOnlyCLAIMSToHoldIt_DoesNotLicenseDeletingTheLastCopy()
    {
        // The data-loss path this closes (FR-VER-006). A sync ledger records
        // what we SENT, not what the destination still has; trimming staging
        // on that basis deletes our last copy of a historic blob because a
        // peer said so. A claim is not a proof, however current it is.
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        var nowMs = (ulong)Day1.AddDays(2).AddHours(1).ToUnixTimeMilliseconds();
        DestinationSyncStore.Open(StateDirectory).RecordSuccess(SetId, "friend", 0, nowMs, syncedSequence: 1_000_000);

        var report = await RunAsync(
            store,
            name => name == "vault" ? VaultVerification(name) : TrimVerification.Ledger,
            apply: true,
            now: Day1.AddDays(2).AddHours(1),
            extraDestination: new SetDestinationReference { Ref = "friend" });

        Assert.IsFalse(
            report.Lines.Any(line => line.StartsWith("trimmed:", StringComparison.Ordinal)),
            "an unproven destination must not license a delete");
        Assert.HasCount(3, await ListAsync(store, "blobs/data/"));
    }

    [TestMethod]
    public async Task RetentionApply_APeerThatHasPROVENIt_VerifiesWithoutTouchingItsStore()
    {
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        // The same peer, now having answered a challenge covering the current
        // sequence: the hub asks its store nothing, because bytes were read
        // back off it during the sync and the ledger records that they were.
        var nowMs = (ulong)Day1.AddDays(2).AddHours(1).ToUnixTimeMilliseconds();
        var sync = DestinationSyncStore.Open(StateDirectory);
        sync.RecordSuccess(SetId, "friend", 0, nowMs, syncedSequence: 1_000_000);
        sync.RecordVerification(
            SetId, "friend", objects: 4, population: 12, verifiedSequence: 1_000_000, sampleCursor: null, nowMs);

        var report = await RunAsync(
            store,
            name => name == "vault" ? VaultVerification(name) : TrimVerification.Ledger,
            apply: true,
            now: Day1.AddDays(2).AddHours(1),
            extraDestination: new SetDestinationReference { Ref = "friend" });

        Assert.Contains(
            line => line.StartsWith("trimmed: 2 historic data blob(s)", StringComparison.Ordinal), report.Lines);
        Assert.HasCount(1, await ListAsync(store, "blobs/data/"));
    }

    [TestMethod]
    public async Task RetentionApply_APeerWhoseLatestSyncWentUnproven_DoesNotLicenseDeletingTheLastCopy()
    {
        // Freshness without a new knob: the peer proved itself once, then
        // synced again and that later sync proved nothing. The stamp is now
        // older than the state it would vouch for, so it stops counting.
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        var provenAt = (ulong)Day1.AddDays(2).ToUnixTimeMilliseconds();
        var syncedAt = (ulong)Day1.AddDays(2).AddHours(1).ToUnixTimeMilliseconds();
        var sync = DestinationSyncStore.Open(StateDirectory);
        sync.RecordSuccess(SetId, "friend", 0, provenAt, syncedSequence: 1_000_000);
        sync.RecordVerification(
            SetId, "friend", objects: 4, population: 12, verifiedSequence: 1_000_000, sampleCursor: null, provenAt);
        sync.RecordSuccess(SetId, "friend", 0, syncedAt, syncedSequence: 1_000_000);

        var report = await RunAsync(
            store,
            name => name == "vault" ? VaultVerification(name) : TrimVerification.Ledger,
            apply: true,
            now: Day1.AddDays(2).AddHours(1),
            extraDestination: new SetDestinationReference { Ref = "friend" });

        Assert.IsFalse(
            report.Lines.Any(line => line.StartsWith("trimmed:", StringComparison.Ordinal)),
            "a proof older than the sync it would vouch for is stale, not evidence");
        Assert.HasCount(3, await ListAsync(store, "blobs/data/"));
    }

    [TestMethod]
    public async Task RetentionApply_APeerWhoseLedgerClaimIsStale_BlocksTheTrim()
    {
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        // The peer synced once, long ago — its claim covers sequence 1 and
        // the archive is past it. The vault's verified copy is not enough:
        // EVERY entitled destination must vouch (ADR-0034 §6).
        var nowMs = (ulong)Day1.AddDays(2).AddHours(1).ToUnixTimeMilliseconds();
        DestinationSyncStore.Open(StateDirectory).RecordSuccess(SetId, "friend", 0, nowMs, syncedSequence: 1);

        var report = await RunAsync(
            store,
            name => name == "vault" ? VaultVerification(name) : TrimVerification.Ledger,
            apply: true,
            now: Day1.AddDays(2).AddHours(1),
            extraDestination: new SetDestinationReference { Ref = "friend" });

        Assert.Contains(
            line => line.StartsWith("trim held back: 2 data blob(s)", StringComparison.Ordinal), report.Lines);

        // And the count alone is not the report. Retention stalling is an
        // operator problem, so the destination holding it up is named with the
        // reason — a bare number leaves nothing to act on (FR-GC-005).
        Assert.Contains(
            line => line.Contains(
                "destination 'friend' has not proven what it was last sent", StringComparison.Ordinal),
            report.Lines);
        Assert.HasCount(3, await ListAsync(store, "blobs/data/"));
    }

    [TestMethod]
    public async Task RetentionApply_AVaultMissingTheBlob_NamesWhatItDoesNotHold()
    {
        // The probe basis stalling had no named line at all before: only a
        // destination the caller mapped to Unverifiable was ever named, so a
        // reachable replica that had silently lost an object vanished into the
        // count — which is precisely the case an operator most needs to see.
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);
        var replicaRoot = Directory.GetDirectories(VaultPath).Single();

        // Every data blob, not an arbitrary one. Which blob `EnumerateFiles`
        // returns first is filesystem order, and one of the three belongs to
        // the newest snapshot's closure — which never trims, so deleting that
        // one leaves every candidate still held and the line legitimately
        // absent. Taking them all makes the assertion depend on the behaviour
        // under test rather than on directory ordering.
        foreach (var path in Directory.EnumerateFiles(
            Path.Combine(replicaRoot, "blobs", "data"), "*", SearchOption.AllDirectories))
        {
            File.Delete(path);
        }

        var report = await RunAsync(store, VaultVerification, apply: true, now: Day1.AddDays(2).AddHours(1));

        Assert.Contains(
            line => line.Contains(
                "destination 'vault' does not hold every historic blob staging would drop",
                StringComparison.Ordinal),
            report.Lines);
    }

    [TestMethod]
    public async Task RetentionApply_ANarrowDestinationThatDroppedTheBlobs_DoesNotBlockTheirTrim()
    {
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        // The narrow destination keeps only the newest snapshot, so it was
        // never entitled to the historic blobs — its being unverifiable is
        // irrelevant to them. Only the vault must vouch, and it does.
        var report = await RunAsync(
            store,
            name => name == "vault" ? VaultVerification(name) : TrimVerification.None,
            apply: true,
            now: Day1.AddDays(2).AddHours(1),
            extraDestination: new SetDestinationReference
            {
                Ref = "friend",
                Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
            });

        Assert.Contains(
            line => line.StartsWith("trimmed: 2 historic data blob(s)", StringComparison.Ordinal), report.Lines);
        Assert.HasCount(1, await ListAsync(store, "blobs/data/"));
    }

    [TestMethod]
    public async Task Trim_AReplicaCopyVanishesBetweenPlanAndExecute_LeavesTheStagingCopyAlone()
    {
        // The trim's plan is evidence gathered at one moment; a convergence
        // computed against a narrower keep-set can delete the verified
        // copies at the replica before the plan executes. Execution must
        // re-check the evidence at the moment of deletion — otherwise the
        // blob is lost everywhere (ADR-0029 Amendment 2's belt-and-braces).
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);

        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var sealing = Math.Max(
            repository.CurrentDataGeneration.Value, repository.CurrentMetadataGeneration.Value);
        IReadOnlyList<FallbackPlan.Repository.Index.Journal.JournalRecord> records;
        int unparseable;
        using (var journal = new FallbackPlan.Repository.Index.Journal.JournalReader(
            store, repository.RepositoryId, repository.Hierarchy))
        {
            (records, unparseable, _) = await journal.LoadAsync(sealing, CancellationToken.None);
        }

        var now = Day1.AddDays(2).AddHours(1);
        var intents = FallbackPlan.Repository.Index.Journal.IntentSurveyor.Survey(
            records, unparseable, sealing, (ulong)now.ToUnixTimeMilliseconds(), skewMarginMs: 300_000);

        var sync = DestinationSyncStore.Open(StateDirectory);
        var plan = await StagingTrim.PlanAsync(
            store, reader, survey,
            setPolicy: null,
            [new SetDestinationReference { Ref = "vault", Retention = new RetentionConfiguration { MinGenerations = 10 } }],
            VaultVerification,
            name => sync.Find(SetId, name),
            intents, now, CancellationToken.None);
        Assert.HasCount(2, plan.Eligible);

        // The race, made deterministic: between plan and execute, a
        // convergence pass under a narrower filter removes the data blobs
        // from the replica (the control assertion proves it really did).
        var replica = new LocalFileSystemObjectStore(Directory.GetDirectories(VaultPath).Single());
        var converged = await FallbackPlan.Replication.StoreToStoreCopier.ConvergeAsync(
            store, replica,
            key => !key.StartsWith("blobs/data/", StringComparison.Ordinal),
            CancellationToken.None);
        Assert.IsGreaterThanOrEqualTo(2, converged.Deleted);

        // Execution re-verifies and refuses: the staging copies are now the
        // only copies, and every one of them stays.
        var (deleted, _, _) = await StagingTrim.ExecuteAsync(
            store, plan, VaultVerification, name => sync.Find(SetId, name), CancellationToken.None);
        Assert.AreEqual(0, deleted);
        Assert.HasCount(3, await ListAsync(store, "blobs/data/"));
    }

    [TestMethod]
    public async Task RetentionApply_APassClockBehindTheLastSync_RefusesTheLedgerClaim()
    {
        // A pass whose clock sits BEHIND the last sync computes entitlement
        // the destination may already have converged away from — its ledger
        // claim cannot be trusted, and the blobs stay.
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        var syncedAt = (ulong)Day1.AddDays(3).ToUnixTimeMilliseconds();
        DestinationSyncStore.Open(StateDirectory).RecordSuccess(SetId, "friend", 0, syncedAt, syncedSequence: 1_000_000);

        var report = await RunAsync(
            store,
            name => name == "vault" ? VaultVerification(name) : TrimVerification.Ledger,
            apply: true,
            now: Day1.AddDays(2).AddHours(1),
            extraDestination: new SetDestinationReference { Ref = "friend" });

        Assert.Contains(
            line => line.StartsWith("trim held back: 2 data blob(s)", StringComparison.Ordinal), report.Lines);
        Assert.HasCount(3, await ListAsync(store, "blobs/data/"));
    }

    private TrimVerification VaultVerification(string name) =>
        TrimVerification.AgainstStore(
            new LocalFileSystemObjectStore(Directory.GetDirectories(VaultPath).Single()));

    /// <summary>A pass where the vault has NO rules — entitled to everything.</summary>
    private async Task<RetentionReport> RunAsyncWithoutRules(LocalFileSystemObjectStore store, DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);

        var sync = DestinationSyncStore.Open(StateDirectory);
        return await RetentionRunner.RunAsync(
            store, repository,
            policy: null,
            [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name),
            VaultVerification,
            WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId),
            apply: true,
            (ulong)now.ToUnixTimeMilliseconds(),
            CancellationToken.None);
    }

    private async Task<RetentionReport> RunAsync(
        LocalFileSystemObjectStore store,
        Func<string, TrimVerification> verificationFor,
        bool apply,
        DateTimeOffset now,
        SetDestinationReference? extraDestination = null)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);

        var destinations = new List<SetDestinationReference>
        {
            new() { Ref = "vault", Retention = new RetentionConfiguration { MinGenerations = 10 } },
        };
        if (extraDestination is not null)
        {
            destinations.Add(extraDestination);
        }

        var sync = DestinationSyncStore.Open(StateDirectory);
        return await RetentionRunner.RunAsync(
            store, repository,
            policy: null,
            destinations,
            name => sync.Find(SetId, name),
            verificationFor,
            WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId),
            apply,
            (ulong)now.ToUnixTimeMilliseconds(),
            CancellationToken.None);
    }

    private async Task BackUpThreeDaysAsync()
    {
        await BackUpAsync(Day1);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
        await BackUpAsync(Day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
        await BackUpAsync(Day1.AddDays(2));
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        var result = await AgentPass.RunAsync(ArchivesRoot, passphrase, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private static async Task AssertWalksCleanAsync(LocalFileSystemObjectStore replica, int expectedSnapshots)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(replica, passphrase, CancellationToken.None);

        var survey = await StagingMark.SurveyAsync(replica, repository, CancellationToken.None);
        Assert.HasCount(expectedSnapshots, survey.Snapshots);
        Assert.IsEmpty(survey.Undecodable);

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, replica);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var (_, unwalkable) = await StagingMark.MarkAsync(reader, survey.Snapshots, CancellationToken.None);
        Assert.IsEmpty(unwalkable);
    }

    private static async Task<List<string>> ListAsync(LocalFileSystemObjectStore store, string prefix)
    {
        var keys = new List<string>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse(prefix), ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key.Value);
        }

        return keys;
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
