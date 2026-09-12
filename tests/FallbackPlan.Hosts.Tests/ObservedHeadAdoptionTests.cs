using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The rollback witness through the service (NFR-SEC-005): opening a set's
/// archive asks the repository how far this writer had got, and moves the
/// local sequence past it when local allocation state turns out to be behind
/// its own published history.
/// </summary>
/// <remarks>
/// <para>
/// The sequence file records what this machine handed out and is exactly as
/// durable as the state directory holding it. The repository records the same
/// fact in signed checkpoints, signed deltas and journal keys, all of which
/// live at every destination. Until this, nothing compared the two: a lost or
/// rolled-back state directory was discovered by a colliding put partway
/// through the next backup, reported to the operator as a store failure.
/// </para>
/// <para>
/// This does not establish FR-KIT-006 — it never restores anything; the
/// machine keeps its repository throughout and only its allocation state is
/// disturbed.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ObservedHeadAdoptionTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task ArchiveOpen_TheSequenceFileIsGone_AdoptsTheRepositorysHeadAndSaysSo()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/history.txt", new string('h', 40_000) + "the first era");

        await using (var runtime = await StartAsync())
        {
            var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
            Assert.AreEqual(1, pass.Ran);
            await pass.Transfers.WaitAsync(Timeout);
        }

        // The state a rebuilt machine has, and the state a state directory
        // restored from an older copy has: the repository holds a history the
        // allocation state knows nothing about.
        var sequenceFiles = Directory.GetFiles(_harness.StateDirectory, "sequence-*.txt");
        var sequencePath = Assert.ContainsSingle(sequenceFiles);
        var beforeLoss = await File.ReadAllTextAsync(sequencePath, Timeout);
        File.Delete(sequencePath);

        await using (var runtime = await StartAsync())
        {
            // Opening the archive is what consults the repository.
            _ = await runtime.ExistingArchiveAsync(_harness.DocsSetId, Timeout);

            Assert.IsTrue(File.Exists(sequencePath), "adoption must persist the head it adopted");
            var afterAdoption = await File.ReadAllTextAsync(sequencePath, Timeout);
            Assert.AreNotEqual(
                beforeLoss, afterAdoption, "an adopted sequence must not read as a freshly initialised one");

            var notice = Assert.ContainsSingle(
                runtime.Notices.Unacknowledged.Where(n => n.Key == $"sequence-adopted:{_harness.DocsSetId}"));
            Assert.Contains("re-used writer sequence numbers", notice.Message, StringComparison.Ordinal);

            // And the proof that the adoption was the right number: the next
            // backup publishes rather than colliding with its own history.
            _harness.WriteSourceFile("docs/second-era.txt", new string('s', 30_000) + "after the loss");
            var outcome = await Scheduler.Enqueue(
                runtime, runtime.Configuration.BackupSets.Single(), DateTimeOffset.Now, userInitiated: true)
                .WaitAsync(Timeout);
            Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);

            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            Assert.IsInstanceOfType<SnapshotsResult>(
                await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
            Assert.HasCount(2, listed.Snapshots);
        }
    }

    [TestMethod]
    public async Task ArchiveOpen_TheSequenceFileIsIntact_AdoptsNothingAndSaysNothing()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/history.txt", new string('h', 40_000) + "the first era");

        await using (var runtime = await StartAsync())
        {
            var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
            Assert.AreEqual(1, pass.Ran);
            await pass.Transfers.WaitAsync(Timeout);
        }

        // The ordinary second start. A writer's own state is normally AHEAD of
        // the published head — numbers are allocated before the objects
        // accounting for them exist — so a notice here would cry wolf on every
        // restart, which is the failure mode that makes warnings worthless.
        await using (var runtime = await StartAsync())
        {
            _ = await runtime.ExistingArchiveAsync(_harness.DocsSetId, Timeout);

            Assert.IsEmpty(
                runtime.Notices.Unacknowledged.Where(n => n.Key.StartsWith("sequence-adopted:", StringComparison.Ordinal)),
                "an intact sequence must adopt nothing");
        }
    }

    private void WriteConfiguration() => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('d', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = _harness.DocsSetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                Schedule = "every 1h",
                Destinations = [new SetDestinationReference { Ref = "vault" }],
            },
        ],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

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
