using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A direct-ship set's writer writes through <c>Agent/DestinationShipSink</c>,
/// so what the sink says it can do is what the engine plans against. It used
/// to say what the LOCAL METADATA STORE could do — for a store whose blob
/// reads and writes the destinations answer
/// ([ADR-0012](../../docs/adr/0012-storage-provider-contract.md) Amendment 4).
///
/// That was harmless only by accident: every destination is a local
/// filesystem, which promises exactly what the metadata store promises, so
/// the forwarded answer happened to be right. It is a property of there being
/// one provider rather than of the design, which is why the falsifier needs a
/// destination that promises less — and `TestSupport/DegradedObjectStore`,
/// built to drive slice 25's admission gate, is one.
///
/// Establishes NFR-PORT-005 and the capability half of FR-DEST-013. Does not
/// establish NFR-COMP-005.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ShipSinkCapabilityTests : IDisposable
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
    public async Task ADestinationThatAcceptsSmallerObjects_IsWhatTheCaptureIsPlannedAgainst()
    {
        // The ceiling is far below any blob the policy would seal, so a
        // capture planned against the metadata store's long.MaxValue would
        // spool an object this destination could never take and only find out
        // at the put — one blob's work after the decision.
        var outcome = await RunAsync(new StoreCapabilities { MaximumObjectSize = 4096 });

        Assert.AreNotEqual("ran", outcome.Outcome, "the run was planned against a promise the destination never made");
        Assert.Contains(
            "capture policy", outcome.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase,
            $"the refusal did not name what was wrong: {outcome.Detail}");
    }

    [TestMethod]
    public async Task ADestinationThatPromisesWhatTheLocalStoreDoes_ChangesNothing()
    {
        // The control, and the reason the assertion above is about the
        // intersection rather than about decoration: the same run through the
        // same decorator, with the destination promising what a local path
        // really promises, is an ordinary backup.
        var outcome = await RunAsync(new StoreCapabilities
        {
            ConditionalCreate = true,
            RangedReads = true,
            ListingConsistency = ListingConsistency.Strong,
            MaximumObjectSize = long.MaxValue,
        });

        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
        Assert.IsNotEmpty(Directory.GetDirectories(Vault), "nothing reached the destination");
    }

    private async Task<(string Outcome, string? Detail)> RunAsync(StoreCapabilities declared)
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/report.txt", new string('r', 200_000));

        await _harness.SetupAsync();
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                ReplicaStoreDecorator = (_, store) => new DegradedObjectStore(store, declared),
            },
            Timeout);

        var set = runtime.Configuration.BackupSets.Single();
        var result = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)
            .WaitAsync(Timeout);
        return (result.Outcome, result.Detail);
    }

    private void WriteConfiguration() => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('d', 32),
                Name = "vault",
                Kind = DestinationKind.LocalPath,
                Path = Vault,
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
                DirectShip = true,
            },
        ],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
}
