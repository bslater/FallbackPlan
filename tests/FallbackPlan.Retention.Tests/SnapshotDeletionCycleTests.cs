using System.Security.Cryptography;
using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Retention;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// A person's request that a snapshot be deleted, against a real staging
/// archive (FR-GC-013, ADR-0080): the request is the snapshot manifest's
/// tombstone with reason <c>requested</c>, signed under the reclaim key, and
/// the audit record published after it, naming who asked and for what, is the
/// publication that makes it eligible. Every later survey reads it, so the
/// planner expires the snapshot whatever the policy keeps, and the sweep
/// deletes it once every copy has converged since the request.
/// </summary>
[TestClass]
public sealed class SnapshotDeletionCycleTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-deletion-tests", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "deletion-cycle-passphrase!!";
    private static readonly string SetId = new('b', 32);

    /// <summary>A policy that keeps every snapshot these tests make: only a request can expire one.</summary>
    private static readonly RetentionConfiguration KeepsEverything = new() { KeepDaily = 30, MinGenerations = 3 };

    private static readonly DateTimeOffset Day1 = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    private string ArchivesRoot => Path.Combine(_root, "archives");
    private string RepoPath => Path.Combine(ArchivesRoot, SetId);
    private string StateDirectory => Path.Combine(_root, "state");
    private string SourceRoot => Path.Combine(_root, "source");

    public SnapshotDeletionCycleTests()
    {
        Directory.CreateDirectory(StateDirectory);
        WriteOnlyInstallation.Provision(StateDirectory, PassphraseText);
        Directory.CreateDirectory(SourceRoot);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day one content");

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
                    Retention = KeepsEverything,
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    [TestMethod]
    public async Task Request_IsReadByEverySurveyAfterIt()
    {
        await BackUpThreeAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);

        var before = await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None);
        Assert.IsTrue(before.Snapshots.All(snapshot => snapshot.Fact.DeletionRequest is null));
        var target = before.Snapshots[1];

        var request = await RequestAsync(store, opened, [target], actor: "ben");

        var after = await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None);
        Assert.HasCount(3, after.Snapshots);
        Assert.AreEqual(
            request.Generation,
            after.Snapshots.Single(snapshot => snapshot.Fact.SnapshotId == target.Fact.SnapshotId).Fact.DeletionRequest);
        Assert.IsTrue(after.Snapshots
            .Where(snapshot => snapshot.Fact.SnapshotId != target.Fact.SnapshotId)
            .All(snapshot => snapshot.Fact.DeletionRequest is null));
        CollectionAssert.AreEqual(new[] { target.Fact.SnapshotId }, request.SnapshotIds.ToArray());
    }

    [TestMethod]
    public async Task Request_PublishesTheAuditRecordThatMakesItEligible()
    {
        await BackUpThreeAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None);
        var head = (await LoadJournalAsync(store, opened.Repository)).Max(record => record.Sequence);

        var request = await RequestAsync(store, opened, [survey.Snapshots[0], survey.Snapshots[2]], actor: "ben");

        // The request counts from the publication after the one it was made
        // at, which is the audit record itself: the grace a tombstone waits
        // for (11 §3.1) is the record that says who asked.
        Assert.AreEqual(head + 1, request.Generation);
        Assert.IsGreaterThanOrEqualTo(request.Generation, request.AuditSequence);

        var records = await LoadJournalAsync(store, opened.Repository);
        var audit = records.Single(record => record.Kind == JournalRecordKind.Audit);
        Assert.AreEqual(request.AuditSequence, audit.Sequence);
        Assert.IsInstanceOfType<JournalPayload.Audit>(audit.Payload, out var payload);
        Assert.AreEqual(AuditAction.BulkSnapshotDeletion, payload.Action);
        Assert.AreEqual("ben", payload.Actor);
        Assert.AreEqual(2UL, payload.ObjectsAffected);
        CollectionAssert.AreEquivalent(
            new[] { survey.Snapshots[0].Fact.SnapshotId, survey.Snapshots[2].Fact.SnapshotId },
            payload.Snapshots.Select(snapshot => Convert.ToHexStringLower(snapshot.Span)).ToArray());
    }

    [TestMethod]
    public async Task Request_ReplacesTheUnreferencedTombstoneAnExpirySnapshotAlreadyHad()
    {
        // Expired by a narrower policy and tombstoned as unreferenced, awaiting
        // its grace. A later pass under a wider policy would find it protected
        // again and keep it; the request is what makes the decision durable.
        await BackUpThreeAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);

        var expiry = await RunAsync(
            store, opened, apply: true, Day1.AddDays(2).AddHours(1),
            policy: new RetentionConfiguration { MinGenerations = 2 }, convergedSince: ulong.MaxValue);
        Assert.IsGreaterThanOrEqualTo(1, expiry.TombstonesWritten);

        var survey = await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None);
        var oldest = survey.Snapshots[^1];
        Assert.IsNull(oldest.Fact.DeletionRequest, "an expiry's tombstone is not a person's request");

        var request = await RequestAsync(store, opened, [oldest], actor: "ben");

        var requests = await SnapshotDeletion.ReadRequestsAsync(store, opened.Repository, CancellationToken.None);
        Assert.AreEqual(request.Generation, requests[oldest.ManifestObjectId]);
        Assert.ContainsSingle(await ListAsync(store, "tombstones/04/"));
    }

    [TestMethod]
    public async Task Survey_ARequestNotSignedByTheReclaimKey_IsNotHonoured_AndTheSweepSaysSo()
    {
        // Whoever can write the store can seal a tombstone: the write
        // credential derives the metadata key. Only the reclaim key makes it a
        // request, and a service holds that key only for the length of a
        // granted run (ADR-0055 §6).
        await BackUpThreeAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var target = (await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None)).Snapshots[0];

        await ForgeRequestAsync(store, opened.Repository, target);

        var survey = await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None);
        Assert.IsTrue(survey.Snapshots.All(snapshot => snapshot.Fact.DeletionRequest is null));
        Assert.IsEmpty(await SnapshotDeletion.ReadRequestsAsync(store, opened.Repository, CancellationToken.None));

        var pass = await RunAsync(store, opened, apply: true, Day1.AddDays(2).AddHours(1), convergedSince: ulong.MaxValue);
        Assert.HasCount(3, await ListAsync(store, "snapshots/"));
        Assert.Contains(finding => finding.StartsWith("security:", StringComparison.Ordinal), pass.Swept!.Findings);
    }

    [TestMethod]
    public async Task Run_ARequestedSnapshotEveryCopyHasConvergedSince_IsDeletedThoughThePolicyKeepsIt()
    {
        await BackUpThreeAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None);
        var target = survey.Snapshots[1];

        var request = await RequestAsync(store, opened, [target], actor: "cli");

        var pass = await RunAsync(store, opened, apply: true, Day1.AddDays(2).AddHours(1), convergedSince: request.Generation);

        Assert.IsGreaterThanOrEqualTo(1, pass.Swept!.Deleted);
        var remaining = await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None);
        Assert.HasCount(2, remaining.Snapshots);
        Assert.IsFalse(remaining.Snapshots.Any(snapshot => snapshot.Fact.SnapshotId == target.Fact.SnapshotId));
        Assert.IsFalse(pass.Swept.Findings.Any(finding => finding.StartsWith("damage:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Run_ARequestedSnapshotTheVaultHasNotConvergedSince_IsHeldNamingTheVault()
    {
        // The vault was synced by every backup, all of them before the
        // request, so it may hold the snapshot and has not been told to drop
        // it. Staging keeps the snapshot until it has: once staging lets go,
        // the vault's copy is one nothing would ever remove (ADR-0034 §6).
        await BackUpThreeAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var target = (await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None)).Snapshots[1];

        await RequestAsync(store, opened, [target], actor: "cli");

        var pass = await RunAsync(store, opened, apply: true, Day1.AddDays(2).AddHours(1), convergedSince: null);

        var held = Assert.ContainsSingle(pass.Held);
        Assert.AreEqual(target.Fact.SnapshotId, held.Snapshot.SnapshotId);
        Assert.IsTrue(held.DeletionPending);
        Assert.AreEqual("vault", Assert.ContainsSingle(held.AwaitingDestinations));
        Assert.Contains(
            line => line.Contains("deletion requested", StringComparison.Ordinal) && line.Contains("vault", StringComparison.Ordinal),
            pass.Lines);
        Assert.HasCount(3, await ListAsync(store, "snapshots/"));

        // Held is not damage: the sweep found the request and left it standing.
        Assert.IsFalse(pass.Swept!.Findings.Any(finding => finding.StartsWith("damage:", StringComparison.Ordinal)));
        Assert.ContainsSingle(await SnapshotDeletion.ReadRequestsAsync(store, opened.Repository, CancellationToken.None));
    }

    [TestMethod]
    public async Task Run_WhatOnlyTheRequestedSnapshotHeld_IsCollectedAfterTheNextPublication()
    {
        // The manifest goes in the pass that releases it. The blobs only it
        // reached are condemned in that pass, as unreferenced, and wait out
        // their own grace like any other garbage.
        await BackUpAsync(Day1);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "what should never have been backed up");
        await BackUpAsync(Day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
        await BackUpAsync(Day1.AddDays(2));

        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var target = (await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None)).Snapshots[1];
        var blobsBefore = (await ListAsync(store, "blobs/")).Count;

        var request = await RequestAsync(store, opened, [target], actor: "cli");

        var first = await RunAsync(store, opened, apply: true, Day1.AddDays(2).AddHours(1), convergedSince: request.Generation);
        Assert.HasCount(2, await ListAsync(store, "snapshots/"));
        Assert.IsGreaterThanOrEqualTo(1, first.Swept!.Deleted);
        Assert.IsNotEmpty(
            await ListAsync(store, $"tombstones/{Tombstone.BlobTypeCode:x2}/"),
            "nothing the snapshot alone held was condemned");
        Assert.AreEqual(blobsBefore, (await ListAsync(store, "blobs/")).Count, "a blob went before its own grace");

        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day four content");
        await BackUpAsync(Day1.AddDays(3));

        var second = await RunAsync(store, opened, apply: true, Day1.AddDays(3).AddHours(1), convergedSince: request.Generation);
        Assert.IsGreaterThanOrEqualTo(1, second.Swept!.Deleted);
        Assert.IsFalse(second.Swept.Findings.Any(finding => finding.StartsWith("damage:", StringComparison.Ordinal)));
        Assert.IsEmpty(await SnapshotDeletion.ReadRequestsAsync(store, opened.Repository, CancellationToken.None));

        // What remains is a working archive: a dry run walks clean.
        var after = await RunAsync(store, opened, apply: false, Day1.AddDays(3).AddHours(2), convergedSince: request.Generation);
        Assert.IsFalse(after.Lines.Any(line => line.StartsWith("NO DELETION", StringComparison.Ordinal)));
    }

    private async Task BackUpThreeAsync()
    {
        await BackUpAsync(Day1);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
        await BackUpAsync(Day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
        await BackUpAsync(Day1.AddDays(2));
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        var result = await AgentPass.RunAsync(ArchivesRoot, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private WriterId Writer => WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId);

    private async Task<SnapshotDeletionRequest> RequestAsync(
        LocalFileSystemObjectStore store, OpenedArchive opened, IReadOnlyList<SurveyedSnapshot> snapshots, string actor)
    {
        // The sequence file the service allocates this archive's numbers from,
        // so the next backup carries on past the audit record.
        var sequence = new WriterSequence(new FileSequenceStateStore(Path.Combine(
            StateDirectory, $"sequence-{Convert.ToHexStringLower(opened.Repository.RepositoryId.ToArray())}.txt")));

        return await SnapshotDeletion.RequestAsync(
            store, opened.Repository, Writer, sequence, snapshots, actor,
            (ulong)Day1.AddDays(2).AddHours(1).ToUnixTimeMilliseconds(), CancellationToken.None, opened.Reclaim);
    }

    /// <summary>
    /// One pass. <paramref name="convergedSince"/> stands in for the fan-out:
    /// the vault's ledger row as written by the backups, with its converged
    /// sequence set to the value given, or left as the backups wrote it.
    /// </summary>
    private async Task<RetentionReport> RunAsync(
        LocalFileSystemObjectStore store, OpenedArchive opened, bool apply, DateTimeOffset now,
        ulong? convergedSince, RetentionConfiguration? policy = null)
    {
        var sync = DestinationSyncStore.Open(StateDirectory);
        return await RetentionRunner.RunAsync(
            store, opened.Repository,
            policy ?? KeepsEverything,
            [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name) is { } row && convergedSince is { } converged
                ? row with { ConvergedSequence = converged }
                : sync.Find(SetId, name),
            _ => TrimVerification.None,
            Writer,
            apply,
            (ulong)now.ToUnixTimeMilliseconds(),
            CancellationToken.None,
            "docs",
            reclaim: opened.Reclaim);
    }

    private static async Task<IReadOnlyList<JournalRecord>> LoadJournalAsync(
        LocalFileSystemObjectStore store, OpenedRepository repository)
    {
        using var journal = new JournalReader(store, repository.RepositoryId, repository.Credential);
        var (records, _, _) = await journal.LoadAsync(
            Math.Max(repository.CurrentDataGeneration.Value, repository.CurrentMetadataGeneration.Value),
            CancellationToken.None);
        return records;
    }

    /// <summary>
    /// A requested tombstone sealed as the store expects, under the metadata
    /// key the write credential derives, and signed by a key that is not the
    /// reclaim key.
    /// </summary>
    private async Task ForgeRequestAsync(LocalFileSystemObjectStore store, OpenedRepository repository, SurveyedSnapshot snapshot)
    {
        var tombstone = new Tombstone(
            (byte)ObjectType.SnapshotManifest, snapshot.ManifestObjectId.ToArray(), TombstoneReason.Requested,
            Writer.ToArray(), 0, EligibleGeneration: 1);

        byte[] encoded;
        using (var forger = RepositorySigner.FromSeed(RandomNumberGenerator.GetBytes(32), KeyGeneration.Zero))
        {
            encoded = TombstoneCodec.Encode(tombstone, forger.Sign(TombstoneCodec.EncodeForSigning(tombstone)));
        }

        using var deriver = new ObjectIdDeriver(repository.Credential.ContentIdKey.ToArray());
        var objectId = deriver.Derive(ObjectType.Tombstone, ContentHasher.Hash(encoded));
        var metadataKey = repository.Credential.DeriveMetadataKey(KeyGeneration.Zero);
        var sealedObject = StandaloneRecordCipher.Seal(
            repository.RepositoryId, metadataKey, KeyGeneration.Zero, Writer, counter: 0,
            ObjectType.Tombstone, objectId, encoded);

        var put = await store.PutAsync(
            ObjectKey.Parse($"tombstones/04/{Base32.Encode(snapshot.ManifestObjectId.ToArray())}"),
            _ => ValueTask.FromResult<Stream>(new MemoryStream(sealedObject, writable: false)),
            PutConditions.IfNotExists,
            CancellationToken.None);
        Assert.AreEqual(PutOutcome.Created, put.Outcome);
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
