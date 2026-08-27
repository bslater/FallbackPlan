using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Diagnostics;
using FallbackPlan.Repository.Crypto;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Direct-to-destination publication (ADR-0046): a set flagged
/// <c>direct_ship</c> captures straight into its destinations — every blob
/// ships to every in-scope destination, metadata lands both locally (the
/// planning copy) and at the destinations, and the agent's state holds no
/// file content at all. Each destination is a whole, independently
/// restorable repository; a destination that missed a run is caught up from
/// a sibling, because every committed snapshot's closure exists at at least
/// one destination by construction. Establishes FR-DEST-013 and
/// FR-DEST-015, and the direct-ship shape of FR-DEST-002, FR-DEST-003,
/// FR-DEST-004 and FR-DEST-014.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DirectShipTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string VaultA => Path.Combine(_harness.WorkPath, "vault-a");

    private string VaultB => Path.Combine(_harness.WorkPath, "vault-b");

    private string MetadataRoot => Path.Combine(_harness.StateDirectory, "sets", _harness.DocsSetId);

    private LoggingComposition? _logging;

    public void Dispose()
    {
        _logging?.Dispose();
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task DirectShipSet_ABackup_PopulatesEveryDestinationAndKeepsNoLocalBlobs()
    {
        Directory.CreateDirectory(VaultA);
        Directory.CreateDirectory(VaultB);
        WriteDirectShipConfiguration();
        _harness.WriteSourceFile("docs/report.txt", "the words worth keeping");
        _harness.WriteSourceFile("docs/big.bin", new string('b', 300_000));

        await using (var runtime = await StartAsync())
        {
            var set = runtime.Configuration.BackupSets.Single();
            var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)
                .WaitAsync(Timeout);
            Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
        }

        // Each destination holds a whole repository under its repository id.
        var replicaA = Assert.ContainsSingle(Directory.GetDirectories(VaultA));
        var replicaB = Assert.ContainsSingle(Directory.GetDirectories(VaultB));
        foreach (var replica in new[] { replicaA, replicaB })
        {
            Assert.IsTrue(File.Exists(Path.Combine(replica, "repository-format")), $"{replica} has no descriptor");
            Assert.IsTrue(
                Directory.Exists(Path.Combine(replica, "blobs")) && Directory
                    .GetFiles(Path.Combine(replica, "blobs"), "*", SearchOption.AllDirectories).Length > 0,
                $"{replica} holds no blobs — the content never shipped");
            Assert.IsTrue(
                Directory.GetFiles(Path.Combine(replica, "snapshots"), "*", SearchOption.AllDirectories).Length == 1,
                $"{replica} does not hold the snapshot");
        }

        // The agent keeps metadata only: descriptor, journal, index, snapshot
        // — and NO file content.
        Assert.IsTrue(File.Exists(Path.Combine(MetadataRoot, "repository-format")));
        Assert.IsTrue(Directory.Exists(Path.Combine(MetadataRoot, "snapshots")));
        var localBlobs = Path.Combine(MetadataRoot, "blobs");
        Assert.IsTrue(
            !Directory.Exists(localBlobs)
                || Directory.GetFiles(localBlobs, "*", SearchOption.AllDirectories).Length == 0,
            "the agent's state must hold no blob content");
        Assert.IsFalse(
            Directory.Exists(Path.Combine(_harness.ArchivesRoot, _harness.DocsSetId)),
            "a direct-ship set must not grow a staging archive");

        // Independently openable: each replica unlocks with the passphrase
        // alone — no agent state, no sibling, no kit.
        foreach (var replica in new[] { replicaA, replicaB })
        {
            await AssertOpensAloneAsync(replica);
        }
    }

    [TestMethod]
    public async Task DirectShipSet_ASecondBackup_ReusesInsteadOfReshipping()
    {
        Directory.CreateDirectory(VaultA);
        WriteDirectShipConfiguration(vaultBToo: false);
        _harness.WriteSourceFile("docs/report.txt", "unchanged content between the two runs");

        await using var runtime = await StartAsync();
        var set = runtime.Configuration.BackupSets.Single();

        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout)).Outcome);
        var replica = Assert.ContainsSingle(Directory.GetDirectories(VaultA));
        var dataBlobsAfterFirst = Directory
            .GetFiles(Path.Combine(replica, "blobs", "data"), "*", SearchOption.AllDirectories).Length;
        Assert.IsTrue(dataBlobsAfterFirst > 0);

        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now.AddMinutes(5), userInitiated: true)
                .WaitAsync(Timeout)).Outcome);

        // The reuse decision consults the destination (the presence probe has
        // nowhere else to look), so an unchanged source ships no new data.
        Assert.AreEqual(
            dataBlobsAfterFirst,
            Directory.GetFiles(Path.Combine(replica, "blobs", "data"), "*", SearchOption.AllDirectories).Length,
            "an unchanged source must reuse, not re-ship");
        Assert.AreEqual(
            2,
            Directory.GetFiles(Path.Combine(replica, "snapshots"), "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task DirectShipSet_ADestinationOfflineDuringCapture_IsCaughtUpFromItsSibling()
    {
        Directory.CreateDirectory(VaultA);
        // Vault B does not exist yet — a drive that is not plugged in.
        WriteDirectShipConfiguration();
        _harness.WriteSourceFile("docs/report.txt", "captured while B was away");

        await using var runtime = await StartAsync();
        var set = runtime.Configuration.BackupSets.Single();

        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)
            .WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, "one reachable destination is enough to capture");

        var missedRow = runtime.DestinationSync.Find(_harness.DocsSetId, "vault-b");
        Assert.IsNotNull(missedRow);
        Assert.IsNull(missedRow.BaselineCompletedAt, "a destination that received nothing holds no baseline");

        // The drive returns; the scheduler pass catches the pair up from its
        // sibling — the sink reads blobs from whoever holds them.
        Directory.CreateDirectory(VaultB);
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddMinutes(2), Timeout);
        await pass.Transfers.WaitAsync(Timeout);

        var caughtUp = runtime.DestinationSync.Find(_harness.DocsSetId, "vault-b");
        Assert.IsNotNull(caughtUp?.LastSuccessAt, "the pass is the retry pump for direct-ship pairs too");

        var replicaB = Assert.ContainsSingle(Directory.GetDirectories(VaultB));
        Assert.IsTrue(
            Directory.GetFiles(Path.Combine(replicaB, "snapshots"), "*", SearchOption.AllDirectories).Length == 1,
            "the caught-up replica must hold the snapshot it missed");
        await AssertOpensAloneAsync(replicaB);
    }

    [TestMethod]
    public async Task DirectShipSet_ARestoreOverTheService_ComesBackByteIdentical()
    {
        // The read path with no local content: the restore reads blobs
        // through the sink, which answers from whichever destination holds
        // them — the same routing the dedupe probe uses.
        Directory.CreateDirectory(VaultA);
        Directory.CreateDirectory(VaultB);
        WriteDirectShipConfiguration();
        var payload = new string('r', 120_000) + "…and the tail that proves the bytes";
        _harness.WriteSourceFile("docs/precious.txt", payload);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var set = runtime.Configuration.BackupSets.Single();
        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout)).Outcome);

        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var snapshotId = Assert.ContainsSingle(listed.Snapshots).SnapshotId;

        var output = Path.Combine(_harness.WorkPath, "restored");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(new RunRestoreCommand(snapshotId, null, output), Timeout),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        Assert.AreEqual(0, restored.Failed);

        var recovered = Assert.ContainsSingle(
            Directory.GetFiles(output, "precious.txt", SearchOption.AllDirectories));
        Assert.AreEqual(payload, await File.ReadAllTextAsync(recovered, Timeout));
    }

    [TestMethod]
    public async Task DirectShipSet_VerifyDestination_ReadsEachReplicaAgainstItsSealsCleanly()
    {
        // The deep sweep for a direct-ship pair: replica bytes re-read
        // against their seals, with the "source" length comparison answered
        // through the sink. Zero damage on an honest replica; verification
        // must not silently narrow just because staging holds nothing.
        Directory.CreateDirectory(VaultA);
        Directory.CreateDirectory(VaultB);
        WriteDirectShipConfiguration();
        _harness.WriteSourceFile("docs/report.txt", new string('v', 50_000));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var set = runtime.Configuration.BackupSets.Single();
        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout)).Outcome);

        Assert.IsInstanceOfType<VerifyDestinationResult>(
            await handler.ExecuteAsync(
                new VerifyDestinationCommand("docs", null, Full: true), Timeout),
            out var verified);
        Assert.AreEqual(0, verified.Damaged, string.Join(" | ", verified.Lines));
    }

    [TestMethod]
    public async Task DirectShipSet_ARetentionReport_TraversesTheSinkWithoutAStagingCopy()
    {
        // Retention's closure walk (mark, keeps) reads manifests out of
        // metadata blobs — which live at the destinations. The report must
        // traverse cleanly through the sink; the destructive half's real
        // deletes are per-destination convergence, and the sink ignoring
        // blob deletes is what keeps the staging trim from meaning anything
        // false here.
        Directory.CreateDirectory(VaultA);
        WriteDirectShipConfiguration(vaultBToo: false);
        _harness.WriteSourceFile("docs/report.txt", new string('k', 30_000));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var set = runtime.Configuration.BackupSets.Single();
        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout)).Outcome);

        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: false), Timeout), out var report);
        Assert.IsFalse(
            report.Lines.Any(line => line.Contains("could not", StringComparison.OrdinalIgnoreCase)),
            string.Join(" | ", report.Lines));
    }

    [TestMethod]
    public async Task DirectShipSet_NoReachableDestination_RefusesTheCaptureAsAStatedFailure()
    {
        // The stated consequence of holding no local copy (ADR-0046): with
        // every destination away there is nowhere to write, and the capture
        // says so instead of pretending.
        WriteDirectShipConfiguration();
        _harness.WriteSourceFile("docs/report.txt", "nowhere to go");

        await using var runtime = await StartAsync();
        var set = runtime.Configuration.BackupSets.Single();

        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)
            .WaitAsync(Timeout);
        Assert.AreEqual("failed", outcome.Outcome);
        Assert.Contains("destination", outcome.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task DirectShipSet_AFailingDestination_IsDroppedNamedAndHealed()
    {
        // ADR-0046 §3: a destination that fails during a run is dropped and
        // named — in the ledger and in the log (event 3758) — while the run
        // completes to the surviving sibling; the next catch-up heals it.
        Directory.CreateDirectory(VaultA);
        Directory.CreateDirectory(VaultB);
        WriteDirectShipConfiguration();
        _harness.WriteSourceFile("docs/report.txt", "before the failure");

        await using var runtime = await StartAsync(withLogging: true);
        var set = runtime.Configuration.BackupSets.Single();
        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout)).Outcome);

        // Break vault-b: a file where its replica directory was, so every
        // write into it refuses while the destination itself still "exists".
        var replicaB = Assert.ContainsSingle(Directory.GetDirectories(VaultB));
        Directory.Delete(replicaB, recursive: true);
        await File.WriteAllTextAsync(replicaB, "not a directory", Timeout);

        _harness.WriteSourceFile("docs/second.txt", "written while vault-b is broken");
        var outcome = await Scheduler
            .Enqueue(runtime, set, DateTimeOffset.Now.AddMinutes(5), userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);

        // Vault-a carried the run; vault-b is named in the ledger and the log.
        var replicaA = Assert.ContainsSingle(Directory.GetDirectories(VaultA));
        Assert.HasCount(2, Directory.GetFiles(Path.Combine(replicaA, "snapshots"), "*", SearchOption.AllDirectories));
        var row = runtime.DestinationSync.Find(set.Id, "vault-b");
        Assert.IsNotNull(row);
        Assert.AreEqual(DestinationSyncState.Failed, row.State);
        Assert.IsNotNull(row.LastError);
        Assert.IsNotNull(row.BaselineCompletedAt, "a drop must not erase the baseline");

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<LogRecordsResult>(
            await handler.ExecuteAsync(new ReadLogCommand(0, int.MaxValue), Timeout), out var log);
        Assert.IsTrue(
            log.Records.Any(record => record.EventId == 3758 && record.Message.Contains(
                "vault-b", StringComparison.Ordinal)),
            "the drop must be one log line (event 3758) naming the destination");

        // Heal: unblock, and the pass's catch-up carries the missed run out.
        File.Delete(replicaB);
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddMinutes(10), Timeout);
        await pass.Transfers.WaitAsync(Timeout);

        var healed = Assert.ContainsSingle(Directory.GetDirectories(VaultB));
        Assert.HasCount(2, Directory.GetFiles(Path.Combine(healed, "snapshots"), "*", SearchOption.AllDirectories));
        await AssertOpensAloneAsync(healed);
    }

    [TestMethod]
    public async Task DirectShipSet_TheLastDestinationFailing_FailsRecoverablyAndStillNamesTheDrop()
    {
        // When the LAST destination fails the run fails through ordinary
        // interruption safety — and the ledger still carries the drop, so
        // status can say why and the retry arms (ADR-0046 §3).
        Directory.CreateDirectory(VaultA);
        WriteDirectShipConfiguration(vaultBToo: false);
        _harness.WriteSourceFile("docs/report.txt", "the only copy's content");

        await using var runtime = await StartAsync();
        var set = runtime.Configuration.BackupSets.Single();
        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout)).Outcome);

        var replica = Assert.ContainsSingle(Directory.GetDirectories(VaultA));
        Directory.Delete(replica, recursive: true);
        await File.WriteAllTextAsync(replica, "not a directory", Timeout);

        _harness.WriteSourceFile("docs/second.txt", "will not land anywhere");
        var outcome = await Scheduler
            .Enqueue(runtime, set, DateTimeOffset.Now.AddMinutes(5), userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("failed", outcome.Outcome);

        var row = runtime.DestinationSync.Find(set.Id, "vault-a");
        Assert.IsNotNull(row);
        Assert.IsTrue(
            row.State is DestinationSyncState.Failed or DestinationSyncState.Unavailable,
            $"the failed run must still name the drop in the ledger (state was {row.State})");
        Assert.IsTrue((row.LastAttemptAt) >= (ulong)DateTimeOffset.Now.AddMinutes(4).ToUnixTimeMilliseconds(),
            "the drop must be recorded by THIS run, not left at the previous success");

        // Unblocked, the next run completes clean.
        File.Delete(replica);
        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now.AddMinutes(10), userInitiated: true)
                .WaitAsync(Timeout)).Outcome);
    }

    [TestMethod]
    public async Task DirectShipSet_ABehindDestination_IsSkippedUntilCaughtUp()
    {
        // The scope rule that keeps every replica independently restorable:
        // a destination that missed a run holds an incomplete history, and an
        // incremental would hand it a snapshot without its closure — because
        // the dedupe probe is satisfied by ANY holder, its blobs would never
        // ship. It is skipped and healed by catch-up instead (ADR-0046 §3).
        Directory.CreateDirectory(VaultA);
        Directory.CreateDirectory(VaultB);
        WriteDirectShipConfiguration();
        _harness.WriteSourceFile("docs/report.txt", "era one");

        await using var runtime = await StartAsync();
        var set = runtime.Configuration.BackupSets.Single();
        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout)).Outcome);

        // Vault-b misses run 2 entirely.
        var away = VaultB + ".away";
        Directory.Move(VaultB, away);
        _harness.WriteSourceFile("docs/era-two.txt", "content vault-b never saw");
        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now.AddMinutes(5), userInitiated: true)
                .WaitAsync(Timeout)).Outcome);

        // Vault-b returns — behind. Run 3 must NOT include it: no run-3
        // snapshot may appear there while run 2's closure is missing.
        Directory.Move(away, VaultB);
        _harness.WriteSourceFile("docs/era-three.txt", "content shipped while vault-b is behind");
        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now.AddMinutes(10), userInitiated: true)
                .WaitAsync(Timeout)).Outcome);

        var replicaB = Assert.ContainsSingle(Directory.GetDirectories(VaultB));
        Assert.HasCount(
            1,
            Directory.GetFiles(Path.Combine(replicaB, "snapshots"), "*", SearchOption.AllDirectories));

        // Catch-up heals the gap, and only then is vault-b a run target again.
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddMinutes(15), Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        Assert.HasCount(
            3,
            Directory.GetFiles(Path.Combine(replicaB, "snapshots"), "*", SearchOption.AllDirectories));
        await AssertOpensAloneAsync(replicaB);

        _harness.WriteSourceFile("docs/era-four.txt", "vault-b is current again");
        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now.AddMinutes(20), userInitiated: true)
                .WaitAsync(Timeout)).Outcome);
        Assert.HasCount(
            4,
            Directory.GetFiles(Path.Combine(replicaB, "snapshots"), "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task DirectShipSet_ADestinationPluggedBackIn_AnswersReadsWithoutARestart()
    {
        // Reads outside a run resolve fresh from the configuration — a run's
        // scope must not outlive the run, or a destination plugged back in
        // stays invisible until a service restart (ADR-0046 §2).
        Directory.CreateDirectory(VaultA);
        Directory.CreateDirectory(VaultB);
        WriteDirectShipConfiguration();
        _harness.WriteSourceFile("docs/history.txt", "the first era's bytes, worth restoring");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var set = runtime.Configuration.BackupSets.Single();
        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout)).Outcome);
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var firstSnapshot = Assert.ContainsSingle(listed.Snapshots).SnapshotId;

        // Run 2 happens while vault-a is away: the run's scope is vault-b.
        var awayA = VaultA + ".away";
        Directory.Move(VaultA, awayA);
        _harness.WriteSourceFile("docs/era-two.txt", "second era");
        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now.AddMinutes(5), userInitiated: true)
                .WaitAsync(Timeout)).Outcome);

        // Vault-a comes back; vault-b goes away entirely. A restore of the
        // first snapshot must be answered by vault-a — the only holder left.
        Directory.Move(awayA, VaultA);
        Directory.Move(VaultB, VaultB + ".away");

        var output = Path.Combine(_harness.WorkPath, "restored");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(new RunRestoreCommand(firstSnapshot, null, output), Timeout),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        var recovered = Assert.ContainsSingle(
            Directory.GetFiles(output, "history.txt", SearchOption.AllDirectories));
        Assert.Contains(
            "the first era's bytes", await File.ReadAllTextAsync(recovered, Timeout), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task DirectShipSet_AGainedDestination_KeepsTheFlagAndIsSeededThenIncluded()
    {
        // The whole gained-destination story over the command surface: the
        // edit that adds a destination must not erase the direct_ship flag or
        // the references' priorities (an upsert preserves what the command
        // does not carry), the new pair is owed its seed, catch-up delivers
        // it, and the next run ships there directly.
        Directory.CreateDirectory(VaultA);
        Directory.CreateDirectory(VaultB);
        WriteDirectShipConfiguration(vaultBToo: false, vaultAReferencePriority: 3);
        _harness.WriteSourceFile("docs/report.txt", "the seedable history");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var set = runtime.Configuration.BackupSets.Single();
        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout)).Outcome);

        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                new string('2', 32), "vault-b", "local-path", VaultB, null, null)),
            Timeout));
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                set.Id, set.Name, _harness.SourceRoot, set.Schedule, [], [], ["vault-a", "vault-b"])),
            Timeout));

        // The edit preserved what it did not carry.
        var saved = ClientConfiguration.Load(Path.Combine(_harness.StateDirectory, "config.json"))
            .FindSet("docs");
        Assert.IsNotNull(saved);
        Assert.IsTrue(saved.DirectShip, "an upsert must never silently un-flag a direct-ship set");
        Assert.AreEqual(
            3,
            saved.Destinations.Single(reference => reference.Ref == "vault-a").Priority,
            "an upsert must never silently erase a reference's priority");

        // The gained pair is owed its seed, and the queued seed delivers it.
        Assert.IsTrue(runtime.DestinationSync.Find(set.Id, "vault-b")?.NeedsFull ?? false);
        await WaitForAsync(() => !runtime.Queue.IsActive(FanOut.JobIdFor(set.Id, "vault-b")), Timeout);
        var replicaB = Assert.ContainsSingle(Directory.GetDirectories(VaultB));
        await AssertOpensAloneAsync(replicaB);
        Assert.IsNotNull(runtime.DestinationSync.Find(set.Id, "vault-b")?.BaselineCompletedAt);

        // And the next capture ships to vault-b directly.
        _harness.WriteSourceFile("docs/second.txt", "lands at both directly");
        var second = runtime.Configuration.BackupSets.Single();
        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, second, DateTimeOffset.Now.AddMinutes(5), userInitiated: true)
                .WaitAsync(Timeout)).Outcome);
        Assert.HasCount(
            2,
            Directory.GetFiles(Path.Combine(replicaB, "snapshots"), "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task DirectShipSink_AShortPrefix_StillAnswersMetadataBesideTheBlobUnion()
    {
        // The router must not let a prefix that merely SHARES letters with
        // "blobs/" bypass the metadata store: a listing for "b" is a listing
        // of everything under "b", wherever it lives.
        Directory.CreateDirectory(VaultA);
        WriteDirectShipConfiguration(vaultBToo: false);
        _harness.WriteSourceFile("docs/report.txt", "router fodder");

        await using var runtime = await StartAsync();
        var set = runtime.Configuration.BackupSets.Single();
        Assert.AreEqual(
            "ran",
            (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout)).Outcome);

        var archive = await runtime.ArchiveForAsync(set, Timeout);
        var marker = Storage.Abstractions.ObjectKey.Parse("banner/router-proof");
        _ = await archive.Store.PutAsync(
            marker,
            _ => ValueTask.FromResult<Stream>(new MemoryStream("metadata beside the blobs"u8.ToArray())),
            Storage.Abstractions.PutConditions.None,
            Timeout);

        var keys = new List<string>();
        await foreach (var entry in archive.Store.ListAsync(
            Storage.Abstractions.ObjectPrefix.Parse("b"), Storage.Abstractions.ListOptions.Default, Timeout))
        {
            keys.Add(entry.Key.Value);
        }

        Assert.IsTrue(keys.Any(key => key.StartsWith("blobs/", StringComparison.Ordinal)),
            "the blob union answers under its prefix");
        Assert.Contains("banner/router-proof", keys,
            "a metadata key under the same first letter must not vanish from the listing");

        // The metadata store is the planning copy — it answers even with the
        // destination away, which is exactly when routing "b" to the blob
        // union alone would lose it.
        Directory.Move(VaultA, VaultA + ".away");
        var withoutDestination = new List<string>();
        await foreach (var entry in archive.Store.ListAsync(
            Storage.Abstractions.ObjectPrefix.Parse("b"), Storage.Abstractions.ListOptions.Default, Timeout))
        {
            withoutDestination.Add(entry.Key.Value);
        }

        Assert.Contains("banner/router-proof", withoutDestination,
            "the planning copy must answer a short prefix with every destination away");
    }

    /// <summary>Proves a replica is a whole repository: it unlocks alone.</summary>
    private async Task AssertOpensAloneAsync(string replica)
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);
        using var opened = await Repository.RepositoryLifecycle.OpenAsync(
            new Storage.Local.LocalFileSystemObjectStore(replica), passphrase, Timeout);
        Assert.IsNotNull(opened.Descriptor);
    }

    /// <summary>Two local destinations and one direct-ship set over them.</summary>
    private void WriteDirectShipConfiguration(bool vaultBToo = true, int? vaultAReferencePriority = null)
    {
        List<DestinationConfiguration> destinations =
        [
            new()
            {
                Id = new string('1', 32), Name = "vault-a", Kind = DestinationKind.LocalPath,
                Path = VaultA, Priority = 5,
            },
        ];
        List<SetDestinationReference> references = [new() { Ref = "vault-a", Priority = vaultAReferencePriority }];
        if (vaultBToo)
        {
            destinations.Add(new()
            {
                Id = new string('2', 32), Name = "vault-b", Kind = DestinationKind.LocalPath, Path = VaultB,
            });
            references.Add(new() { Ref = "vault-b" });
        }

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = destinations,
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = references,
                    DirectShip = true,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
    }

    private async Task<ServiceRuntime> StartAsync(bool withLogging = false)
    {
        if (withLogging && _logging is null)
        {
            _logging = LoggingComposition.Create(new LoggingOptions
            {
                Default = LogLevel.Debug,
                RingCapacity = 256,
            });
        }

        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                Logging = withLogging ? _logging : null,
            },
            passphrase,
            Timeout);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(25, cancellationToken);
        }
    }
}
