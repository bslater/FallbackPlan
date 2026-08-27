using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The status matrix says where each destination stands on holding a full
/// backup (ADR-0047 §6, ADR-0046 §3): a destination that has completed its
/// baseline says when; one still owed its seed says so — the console's
/// per-destination panel renders exactly these two facts.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class StatusBaselineTests : IDisposable
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
    public async Task TheDestinationRow_CarriesItsBaselineAndItsOwedSeed()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/a.txt", "the baseline's bytes");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // Before anything ran: no baseline, no owed seed — just "never".
        Assert.IsInstanceOfType<StatusResult>(
            await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var before);
        var fresh = Assert.ContainsSingle(Assert.ContainsSingle(before.Sets).Destinations);
        Assert.IsNull(fresh.BaselineCompletedAt);
        Assert.IsFalse(fresh.NeedsFull);

        // A pass captures and ships; the first success IS the baseline
        // (ADR-0047 §6), and the row now says when it completed.
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        Assert.AreEqual(1, pass.Ran);
        await pass.Transfers.WaitAsync(Timeout);

        Assert.IsInstanceOfType<StatusResult>(
            await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var after);
        var seeded = Assert.ContainsSingle(Assert.ContainsSingle(after.Sets).Destinations);
        Assert.IsNotNull(seeded.BaselineCompletedAt);
        Assert.IsFalse(seeded.NeedsFull);

        // The set gains a second destination that has never held anything.
        // Its pair is marked as owed a full backup — exactly what the upsert
        // does for a gained destination (ADR-0047 §5) — and the row says so,
        // so the console can render "awaiting full backup" instead of a bare
        // "behind". The seeded pair's no-op is the same rule from the other
        // side: a destination with a baseline is never owed one.
        var offsite = Path.Combine(_harness.WorkPath, "offsite");
        Directory.CreateDirectory(offsite);
        WriteConfiguration(offsitePath: offsite);
        runtime.DestinationSync.RecordNeedsFull(
            _harness.DocsSetId, "offsite", (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.IsInstanceOfType<StatusResult>(
            await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var owed);
        var rows = Assert.ContainsSingle(owed.Sets).Destinations;
        Assert.HasCount(2, rows);
        Assert.IsFalse(rows.Single(row => row.Name == "vault").NeedsFull);
        var owing = rows.Single(row => row.Name == "offsite");
        Assert.IsTrue(owing.NeedsFull);
        Assert.IsNull(owing.BaselineCompletedAt);
    }

    private void WriteConfiguration(string? offsitePath = null) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('d', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
            },
            .. offsitePath is null
                ? Array.Empty<DestinationConfiguration>()
                :
                [
                    new DestinationConfiguration
                    {
                        Id = new string('e', 32), Name = "offsite", Kind = DestinationKind.LocalPath,
                        Path = offsitePath,
                    },
                ],
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = _harness.DocsSetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                Schedule = "every 1h",
                Destinations =
                [
                    new SetDestinationReference { Ref = "vault" },
                    .. offsitePath is null
                        ? Array.Empty<SetDestinationReference>()
                        : [new SetDestinationReference { Ref = "offsite" }],
                ],
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
