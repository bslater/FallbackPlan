using FallbackPlan.Api;
using FallbackPlan.Cli;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// What <c>restore</c> prints when the service read some files around damage
/// (FR-RST-007, contract 1.45): the count and the service's own lines saying
/// which files came from which copy, and around what — so a person restoring
/// learns that a copy of their backup is damaged from the restore that met
/// it, not only from a notice they may never open.
/// </summary>
[TestClass]
public sealed class GatewayRestoreReportTests
{
    [TestMethod]
    public async Task ARestoreThatReadAroundDamage_SaysSo_AndNamesTheCopies()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        const string line = "docs/report.txt — read from destination 'spare', around destination 'vault': "
            + "Record 1234 in blob 5678 failed authentication (specification 04 §7).";
        await using var client = new ScriptedClient(new RestoreResult(
            2, 0, "/out/.fbp-quarantine/run", "complete", ReadAround: 1, ReadAroundSample: [line]));
        var gateway = new ServiceGateway(client, "scripted", client);

        var report = await gateway.RestoreAsync(new RestoreRequest(new string('e', 32), null, "/out"), timeout.Token);

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        Assert.IsTrue(
            report.Lines.Any(printed => printed.Contains("1 file(s)", StringComparison.Ordinal)
                && printed.Contains("another copy", StringComparison.Ordinal)),
            string.Join(" | ", report.Lines));
        Assert.IsTrue(report.Lines.Any(printed => printed.Contains(line, StringComparison.Ordinal)), string.Join(" | ", report.Lines));
    }

    [TestMethod]
    public async Task ARestoreThatReadAroundNothing_SaysNothingAboutIt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(new RestoreResult(2, 0, "/out/.fbp-quarantine/run", "complete"));
        var gateway = new ServiceGateway(client, "scripted", client);

        var report = await gateway.RestoreAsync(new RestoreRequest(new string('e', 32), null, "/out"), timeout.Token);

        Assert.IsTrue(report.Ok);
        Assert.IsFalse(report.Lines.Any(printed => printed.Contains("another copy", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Answers the description as an installation that holds its own keys, so
    /// no restore grant is derived, and the restore with <paramref name="restored"/>.
    /// </summary>
    private sealed class ScriptedClient(RestoreResult restored) : IFallbackPlanClient
    {
        public ContractVersion ServiceContractVersion => ContractVersion.Current;

        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ServiceResult>(command switch
            {
                DescribeServiceCommand => new ServiceDescriptionResult(
                    ContractVersion.Current.ToString(), "test", "machine", "/state", false, 0),
                RunRestoreCommand => restored,
                _ => new AcknowledgedResult(),
            });

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<JobProgressEvent>();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
