using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The notices verbs (FR-DEST-008, ADR-0039): the ledger listed structured —
/// identity, key, age — and acknowledgement as a command, so a console can
/// retire a notice without shelling out to the agent. Acknowledged notices
/// stay on record; they merely stop demanding attention.
/// </summary>
[TestClass]
public sealed class NoticeCommandTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Notices_ListedAcknowledgedAndListedAgain_TheLedgerTellsTheWholeStory()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        runtime.Notices.Raise("first:thing", "the older finding", 1_000);
        runtime.Notices.Raise("second:thing", "the newer finding", 2_000);

        // Oldest first: the notice that has waited longest reads first.
        Assert.IsInstanceOfType<NoticesResult>(
            await handler.ExecuteAsync(new ListNoticesCommand(), _timeout.Token), out var listed);
        Assert.HasCount(2, listed.Notices);
        Assert.AreEqual("first:thing", listed.Notices[0].Key);
        Assert.AreEqual(1_000UL, listed.Notices[0].RaisedAt);
        Assert.IsNull(listed.Notices[0].AcknowledgedAt);
        Assert.AreEqual("the older finding", listed.Notices[0].Message);

        // Acknowledged: gone from the default listing and from status…
        var id = listed.Notices[0].Id;
        Assert.IsInstanceOfType<AcknowledgedResult>(
            await handler.ExecuteAsync(new AcknowledgeNoticeCommand(id), _timeout.Token));

        Assert.IsInstanceOfType<NoticesResult>(
            await handler.ExecuteAsync(new ListNoticesCommand(), _timeout.Token), out var remaining);
        Assert.AreEqual("second:thing", Assert.ContainsSingle(remaining.Notices).Key);

        Assert.IsInstanceOfType<StatusResult>(
            await handler.ExecuteAsync(new GetStatusCommand(), _timeout.Token), out var status);
        Assert.ContainsSingle(status.Notices);
        Assert.Contains("the newer finding", status.Notices[0]);

        // …but never erased: the history listing still holds it, stamped.
        Assert.IsInstanceOfType<NoticesResult>(
            await handler.ExecuteAsync(new ListNoticesCommand(IncludeAcknowledged: true), _timeout.Token),
            out var history);
        Assert.HasCount(2, history.Notices);
        var acknowledged = history.Notices.Single(notice => notice.Id == id);
        Assert.IsNotNull(acknowledged.AcknowledgedAt);

        // A second acknowledgement of the same id is a stated miss, not a no-op.
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new AcknowledgeNoticeCommand(id), _timeout.Token), out var refused);
        Assert.AreEqual(ServiceErrorReason.NotFound, refused.Reason);
    }

    [TestMethod]
    public async Task AgentNoticesVerb_AServiceIsListening_ActsThroughItsLiveStore()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = FallbackPlan.Api.Transport.LocalServiceListener.Start(
            handler, _harness.StateDirectory);

        runtime.Notices.Raise("reroute:proof", "seen through the binding", 1_000);

        // The listing reaches the service's own store…
        var listed = await HostHarness.RunAsync(
            AgentHost.RunAsync, "notices", "--state", _harness.StateDirectory);
        Assert.AreEqual(0, listed.ExitCode, listed.Error);
        Assert.Contains("seen through the binding", listed.Output);

        // …and so does the acknowledgement: the LIVE store retires it, which
        // a second writer on notices.json could never make it do. Before
        // ADR-0039 this verb wrote the file beside the running service.
        var id = runtime.Notices.Unacknowledged.Single().Id;
        var acked = await HostHarness.RunAsync(
            AgentHost.RunAsync, "notices", "--state", _harness.StateDirectory, "--ack", id);
        Assert.AreEqual(0, acked.ExitCode, acked.Error);
        Assert.IsEmpty(runtime.Notices.Unacknowledged);
    }

    [TestMethod]
    public async Task AgentNoticesVerb_NoServiceListening_StillWorksAgainstTheFile()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        var store = FallbackPlan.Application.NoticeStore.Open(_harness.StateDirectory);
        var notice = store.Raise("offline:proof", "read at breakfast", 1_000);

        var acked = await HostHarness.RunAsync(
            AgentHost.RunAsync, "notices", "--state", _harness.StateDirectory, "--ack", notice.Id);
        Assert.AreEqual(0, acked.ExitCode, acked.Error);
        Assert.IsEmpty(FallbackPlan.Application.NoticeStore.Open(_harness.StateDirectory).Unacknowledged);
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
            _timeout.Token);
    }
}
