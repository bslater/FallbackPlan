using FallbackPlan.Api;
using FallbackPlan.Cli;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// <c>drill</c> asks the service to run a destination's restore drill now
/// (FR-DRL-003): it prints the service's line per pair and exits non-zero
/// unless every pair it asked about was drilled and restored. A pair that was
/// not drilled has proved nothing, so it is not a success either, though the
/// service counts it apart from a failure.
/// </summary>
[TestClass]
public sealed class DrillVerbTests
{
    [TestMethod]
    public async Task Drill_EveryPairRestored_IsOk_AndPrintsTheServicesLines()
    {
        await using var client = new ScriptedClient(
            new DrillResult(["docs -> vault: restored 3 file(s) from its replica"], Failed: 0, NotDrilled: 0));

        var report = await DrillAsync(client, "docs", "vault");

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        Assert.AreEqual("docs -> vault: restored 3 file(s) from its replica", Assert.ContainsSingle(report.Lines));

        var asked = Assert.ContainsSingle(client.Received.OfType<RunDrillCommand>());
        Assert.AreEqual("docs", asked.BackupSetName);
        Assert.AreEqual("vault", asked.DestinationName);
    }

    [TestMethod]
    public async Task Drill_ADrillThatCouldNotRestore_IsNotOk()
    {
        await using var client = new ScriptedClient(new DrillResult(
            ["docs -> vault: restored 3 file(s) from its replica", "docs -> spare: could not restore — the replica would not open"],
            Failed: 1, NotDrilled: 0));

        var report = await DrillAsync(client, "docs", null);

        Assert.IsFalse(report.Ok, "a failed drill sets a non-zero exit code, read from the count and never the prose");
        Assert.HasCount(2, report.Lines);
    }

    [TestMethod]
    public async Task Drill_APairThatWasNotDrilled_IsNotOk()
    {
        await using var client = new ScriptedClient(new DrillResult(
            ["docs -> vault: not drilled — the set has no backup yet, so there is nothing there to restore"],
            Failed: 0, NotDrilled: 1));

        var report = await DrillAsync(client, null, null);

        Assert.IsFalse(report.Ok, "a pair nothing was drilled at has proved nothing");
        var asked = Assert.ContainsSingle(client.Received.OfType<RunDrillCommand>());
        Assert.IsNull(asked.BackupSetName, "no --set asks about every set");
        Assert.IsNull(asked.DestinationName, "no --destination asks about each set's every destination");
    }

    [TestMethod]
    public async Task Drill_ARefusal_IsSaidAsTheServiceSaidIt()
    {
        await using var client = new ScriptedClient(
            new ServiceError(ServiceErrorReason.NotFound, "No backup set named 'nonesuch' is configured."));

        var refused = await Assert.ThrowsExactlyAsync<CliFailureException>(() => DrillAsync(client, "nonesuch", null));

        Assert.Contains("nonesuch", refused.Message, StringComparison.Ordinal);
    }

    private static async Task<OperationReport> DrillAsync(ScriptedClient client, string? set, string? destination)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gateway = new ServiceGateway(client, "scripted", client);
        return await gateway.DrillAsync(set, destination, timeout.Token);
    }

    /// <summary>Answers a drill with <paramref name="drilled"/> and keeps what it was asked.</summary>
    private sealed class ScriptedClient(ServiceResult drilled) : IFallbackPlanClient
    {
        public List<ServiceCommand> Received { get; } = [];

        public ContractVersion ServiceContractVersion => ContractVersion.Current;

        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
        {
            Received.Add(command);
            return ValueTask.FromResult(command is RunDrillCommand ? drilled : new AcknowledgedResult());
        }

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<JobProgressEvent>();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
