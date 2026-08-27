using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The 04 §5.1 kill matrix, run through the store that carries every
/// direct-ship byte (ADR-0046 §3, FR-DEST-003): a destination can die at
/// EVERY put of a capture, and at all of them the same claims must hold —
/// the run completes to the surviving sibling, the dying destination is a
/// recorded drop (never a silent gap, never the run's failure), and the
/// next catch-up converges its replica to the sibling's content with no
/// repair and no operator action (NFR-REL-001's posture, one store out).
/// </summary>
/// <remarks>
/// The sweep drives a real service runtime per budget — the sink is
/// composed inside it — with the store fault injected through
/// <see cref="ServiceOptions.ReplicaStoreDecorator"/>. Budgets climb until
/// a run completes unfaulted, so the sweep covers exactly as many puts as a
/// capture actually sends to one destination and cannot silently shrink if
/// that number grows.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class DirectShipFaultSweepTests
{
    /// <summary>A generous ceiling so a runaway put count fails loudly instead of looping.</summary>
    private const int MaximumBudget = 60;

    [TestMethod]
    public async Task ADestinationDyingAfterAnyPut_IsADropTheNextCatchUpHeals()
    {
        var sweptBudgets = 0;
        for (var budget = 1; budget <= MaximumBudget; budget++)
        {
            using var harness = new HostHarness();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var vaultA = Path.Combine(harness.WorkPath, "vault-a");
            var vaultB = Path.Combine(harness.WorkPath, "vault-b");
            Directory.CreateDirectory(vaultA);
            Directory.CreateDirectory(vaultB);
            WriteConfiguration(harness, vaultA, vaultB);
            harness.WriteSourceFile("docs/report.txt", new string('r', 200_000) + "the swept bytes");

            var faulting = new PutBudgetStore(budget);
            using var passphrase = Passphrase.Create(
                Environment.GetEnvironmentVariable(harness.PassphraseVariable)!);
            await using var runtime = await ServiceRuntime.StartAsync(
                new ServiceOptions
                {
                    ArchivesRoot = harness.ArchivesRoot,
                    StateDirectory = harness.StateDirectory,
                    ReplicaStoreDecorator = (name, store) =>
                        string.Equals(name, "vault-b", StringComparison.Ordinal) ? faulting.Wrap(store) : store,
                },
                passphrase,
                timeout.Token);
            var set = runtime.Configuration.BackupSets.Single();

            // Whichever put dies, the run itself completes: vault-a is
            // healthy, and one failing destination is that destination's
            // drop, never the backup's failure (ADR-0046 §3).
            var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)
                .WaitAsync(timeout.Token);
            Assert.AreEqual("ran", outcome.Outcome, $"budget {budget}: {outcome.Detail}");

            if (!faulting.Faulted)
            {
                // This budget outlasted the capture — the sweep has covered
                // every put a run sends to one destination.
                sweptBudgets = budget - 1;
                break;
            }

            var row = runtime.DestinationSync.Find(set.Id, "vault-b");
            Assert.IsNotNull(row, $"budget {budget}: the drop must reach the ledger");
            Assert.AreEqual(DestinationSyncState.Failed, row.State, $"budget {budget}");
            Assert.IsNotNull(row.LastError, $"budget {budget}");

            // The heal: a later pass catches vault-b up from its sibling,
            // with the injected fault lifted — the drive came back.
            faulting.Lift();
            var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddHours(6), timeout.Token);
            await pass.Transfers.WaitAsync(timeout.Token);

            AssertReplicaCoversSibling(vaultB, vaultA, budget);
        }

        Assert.IsTrue(sweptBudgets > 5, $"the sweep ended after {sweptBudgets} faulted budgets — too few puts were covered for the matrix to mean anything");
    }

    [TestMethod]
    public async Task ADestinationUnderItsFloor_IsSkippedAsUnavailableNotFilled()
    {
        // FR-DEST-010 through the sink: a volume at its floor is left for
        // the machine that owns it. The pair reads unavailable — space
        // freeing up is the gap closing itself — and the sibling carries
        // the run.
        using var harness = new HostHarness();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var vaultA = Path.Combine(harness.WorkPath, "vault-a");
        var vaultB = Path.Combine(harness.WorkPath, "vault-b");
        Directory.CreateDirectory(vaultA);
        Directory.CreateDirectory(vaultB);
        WriteConfiguration(harness, vaultA, vaultB);
        harness.WriteSourceFile("docs/report.txt", "small enough for any floor");

        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(harness.PassphraseVariable)!);
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = harness.ArchivesRoot,
                StateDirectory = harness.StateDirectory,
                AvailableBytesProbe = root =>
                    root.StartsWith(vaultB, StringComparison.Ordinal) ? 1L : null,
            },
            passphrase,
            timeout.Token);
        var set = runtime.Configuration.BackupSets.Single();

        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)
            .WaitAsync(timeout.Token);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);

        var row = runtime.DestinationSync.Find(set.Id, "vault-b");
        Assert.IsNotNull(row);
        Assert.AreEqual(DestinationSyncState.Unavailable, row.State);
        Assert.Contains("floor", row.LastError!, StringComparison.Ordinal);

        Assert.ContainsSingle(Directory.GetDirectories(vaultA));
        Assert.IsEmpty(Directory.GetDirectories(vaultB), "a floor skip must write nothing at all");
    }

    /// <summary>
    /// The healed replica holds every object its sibling holds. Leases are
    /// excluded: they are transient coordination state, not content.
    /// </summary>
    private static void AssertReplicaCoversSibling(string healedVault, string sourceVault, int budget)
    {
        var healedRoot = Assert.ContainsSingle(Directory.GetDirectories(healedVault));
        var sourceRoot = Assert.ContainsSingle(Directory.GetDirectories(sourceVault));

        var healed = RelativeFiles(healedRoot);
        foreach (var file in RelativeFiles(sourceRoot))
        {
            Assert.IsTrue(
                healed.Contains(file),
                $"budget {budget}: after catch-up the healed replica is missing '{file}'");
        }
    }

    private static HashSet<string> RelativeFiles(string root) =>
        [.. Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file))
            .Where(file => !file.StartsWith("leases", StringComparison.Ordinal))];

    private static void WriteConfiguration(HostHarness harness, string vaultA, string vaultB) =>
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new()
                {
                    Id = new string('1', 32), Name = "vault-a", Kind = DestinationKind.LocalPath, Path = vaultA,
                },
                new()
                {
                    Id = new string('2', 32), Name = "vault-b", Kind = DestinationKind.LocalPath, Path = vaultB,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations =
                    [
                        new SetDestinationReference { Ref = "vault-a" },
                        new SetDestinationReference { Ref = "vault-b" },
                    ],
                    DirectShip = true,
                },
            ],
        }.Save(Path.Combine(harness.StateDirectory, "config.json"));

    /// <summary>
    /// Fails every put past a budget, on every store it wraps, until lifted —
    /// the out-of-space and yanked-drive shapes, scoped to one destination.
    /// </summary>
    private sealed class PutBudgetStore(int budget)
    {
        private readonly int _budget = budget;
        private int _puts;
        private volatile bool _lifted;

        public bool Faulted { get; private set; }

        public void Lift() => _lifted = true;

        public IObjectStore Wrap(IObjectStore inner) => new Decorated(this, inner);

        private sealed class Decorated(PutBudgetStore owner, IObjectStore inner) : IObjectStore
        {
            public StoreCapabilities Capabilities => inner.Capabilities;

            public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
                inner.GetMetadataAsync(key, cancellationToken);

            public ValueTask<OpenReadResult> OpenReadAsync(
                ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
                inner.OpenReadAsync(key, range, cancellationToken);

            public ValueTask<PutResult> PutAsync(
                ObjectKey key,
                Func<CancellationToken, ValueTask<Stream>> openContent,
                PutConditions conditions,
                CancellationToken cancellationToken)
            {
                if (!owner._lifted && Interlocked.Increment(ref owner._puts) > owner._budget)
                {
                    owner.Faulted = true;
                    throw new IOException(
                        $"Injected fault: the destination failed after {owner._budget} put(s).");
                }

                return inner.PutAsync(key, openContent, conditions, cancellationToken);
            }

            public IAsyncEnumerable<ObjectEntry> ListAsync(
                ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
                inner.ListAsync(prefix, options, cancellationToken);

            public ValueTask<DeleteResult> DeleteAsync(
                ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
                inner.DeleteAsync(key, conditions, cancellationToken);
        }
    }
}
