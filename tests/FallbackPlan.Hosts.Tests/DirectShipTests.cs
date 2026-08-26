using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Direct-to-destination publication (ADR-0046): a set flagged
/// <c>direct_ship</c> captures straight into its destinations — every blob
/// ships to every in-scope destination, metadata lands both locally (the
/// planning copy) and at the destinations, and the agent's state holds no
/// file content at all. Each destination is a whole, independently
/// restorable repository; a destination that missed a run is caught up from
/// a sibling, because every committed snapshot's closure exists at at least
/// one destination by construction.
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

    public void Dispose()
    {
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
    private void WriteDirectShipConfiguration(bool vaultBToo = true)
    {
        List<DestinationConfiguration> destinations =
        [
            new()
            {
                Id = new string('1', 32), Name = "vault-a", Kind = DestinationKind.LocalPath,
                Path = VaultA, Priority = 5,
            },
        ];
        List<SetDestinationReference> references = [new() { Ref = "vault-a" }];
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

    private async Task<ServiceRuntime> StartAsync()
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase,
            Timeout);
    }
}
