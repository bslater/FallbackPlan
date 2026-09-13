using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// How much of what a destination is owed it actually holds (contract 1.24,
/// FR-DEST-004): counted by the sync pass, carried on the ledger, and put on
/// the status matrix so a console can draw it without measuring anything
/// itself.
/// </summary>
/// <remarks>
/// <para>
/// The figures are a by-product of work the pass already does — converging a
/// destination lists both sides — so they cost nothing extra. Measuring them
/// on a status poll instead would mean listing a whole replica every few
/// seconds to answer a question about a gauge.
/// </para>
/// <para>
/// The distinction that matters most here is <c>measured_at</c>: a
/// destination no pass has reached holds an unknown amount, not zero, and a
/// client that cannot tell the two apart draws an empty gauge over a
/// destination nobody has looked at.
/// </para>
/// <para>
/// This does not establish FR-VER-001 — nothing here verifies anything.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class DestinationCompletenessTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string Spare => Path.Combine(_harness.WorkPath, "spare");

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Sync_AConvergedDestination_ReportsHoldingEverythingItIsOwed()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);
        _harness.WriteSourceFile("docs/content.txt", new string('c', 90_000) + "bytes to hold");

        await using var runtime = await StartAsync();

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        Assert.AreEqual(1, pass.Ran);
        await pass.Transfers.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "vault");
        Assert.IsNotNull(record);
        Assert.IsNotNull(record.MeasuredAt, "a pass that converged a destination must have counted it");
        Assert.IsGreaterThan(0, record.OwedBytes, "a set with a captured file owes its destination bytes");
        Assert.AreEqual(
            record.OwedBytes, record.HeldBytes,
            "a converged destination holds everything it is owed");

        // And the figures reach a client without it measuring anything.
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<StatusResult>(
            await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);

        var row = Assert.ContainsSingle(Assert.ContainsSingle(status.Sets).Destinations);
        Assert.AreEqual(record.HeldBytes, row.HeldBytes);
        Assert.AreEqual(record.OwedBytes, row.OwedBytes);
        Assert.AreEqual(record.MeasuredAt, row.MeasuredAt);
    }

    [TestMethod]
    public async Task Status_ADestinationNoPassHasReached_SaysUncountedRatherThanEmpty()
    {
        // An unreachable destination declared beside a healthy one. Its row
        // must be distinguishable from a destination that genuinely holds
        // nothing — the difference between "we looked and it is empty" and
        // "nobody has looked" is the whole reason measured_at exists.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false, withSpare: true);
        _harness.WriteSourceFile("docs/content.txt", new string('c', 90_000) + "bytes to hold");

        await using var runtime = await StartAsync();

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<StatusResult>(
            await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);

        var rows = Assert.ContainsSingle(status.Sets).Destinations;
        var spare = Assert.ContainsSingle(rows.Where(r => r.Name == "spare"));

        Assert.IsNull(spare.MeasuredAt, "a destination the pass could not reach must not claim a count");
        Assert.AreEqual(0, spare.HeldBytes);
        Assert.AreEqual(0, spare.OwedBytes);
    }

    private void WriteConfiguration(bool directShip, bool withSpare = false)
    {
        List<DestinationConfiguration> destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('d', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
            },
        ];

        List<SetDestinationReference> references = [new SetDestinationReference { Ref = "vault" }];

        if (withSpare)
        {
            // Declared but never created on disk, so the pass refuses it
            // before it can count anything.
            destinations.Add(new DestinationConfiguration
            {
                Id = new string('e', 32), Name = "spare", Kind = DestinationKind.LocalPath, Path = Spare,
            });
            references.Add(new SetDestinationReference { Ref = "spare" });
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
                    DirectShip = directShip,
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
