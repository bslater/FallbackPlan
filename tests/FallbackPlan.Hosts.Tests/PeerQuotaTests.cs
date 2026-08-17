using System.Net;
using System.Text;
using FallbackPlan.Agent;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Quota enforcement end to end (peer-protocol 05): the destination enforces
/// the terms its grant carries at the object boundary, disk trouble is told
/// apart from policy, and a narrowing reaches the source as a durable notice
/// before the first refusal would (FR-QUOTA-001/002).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PeerQuotaTests : IDisposable
{
    private readonly HostHarness _source = new();
    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-quota-dest", Guid.NewGuid().ToString("n"));
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private const ulong PairedAt = 1_722_600_000_000;

    [TestMethod]
    public async Task FanOut_TheQuotaWouldBeCrossed_StopsAtTheBoundaryAndResumesWhenRaised()
    {
        _source.WriteSourceFile("notes.txt", "too big for a one-byte quota");

        // One byte: the first object's declared length already crosses it, so
        // nothing commits — the cleanest form of "stops at the boundary".
        // The source records the same terms so the hello narrows nothing.
        var destinationFingerprint = await StartDestinationAsync(
            destinationTermsForSource: new PeerTerms(1, string.Empty, 0),
            sourceRecordsForDestination: new PeerTerms(1, string.Empty, 0));
        WritePeerConfiguration(destinationFingerprint);

        var run = await RunAgentAsync(
            "run", "--archives", _source.ArchivesRoot, "--state", _source.StateDirectory,
            "--passphrase-env", _source.PassphraseVariable, "--once", "--poll-seconds", "1");
        Assert.AreEqual(0, run.ExitCode, run.Error);

        // The lender's policy said no: failed with the quota named, and a
        // durable notice for the human (05 §5). Local protection continued —
        // the run exited 0 and the staging archive holds the snapshot.
        var record = FallbackPlan.Application.DestinationSyncStore.Open(_source.StateDirectory)
            .Find(_source.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreEqual(FallbackPlan.Application.DestinationSyncState.Failed, record.State);

        // The code, not the prose. Peer-protocol 02 §8 makes the reason code
        // normative and says the human text is not for parsing — so a test
        // that asserts only the message pins the half that may change freely
        // and leaves the half that may not unguarded. Both, then: the code
        // because it is the contract, the words because an operator reads
        // them.
        Assert.Contains(
            nameof(PeerRefusalReason.TermsRefused), record.LastError!, StringComparison.Ordinal);
        Assert.Contains("quota", record.LastError!, StringComparison.OrdinalIgnoreCase);

        var notice = Assert.ContainsSingle(
            FallbackPlan.Application.NoticeStore.Open(_source.StateDirectory).Unacknowledged);
        Assert.Contains("quota", notice.Message, StringComparison.OrdinalIgnoreCase);

        // Nothing partial became visible: the replica directory holds no
        // committed object (FR-QUOTA-002).
        var replicasRoot = Path.Combine(_destinationState, "replicas");
        if (Directory.Exists(replicasRoot))
        {
            Assert.IsEmpty(Directory.EnumerateFiles(replicasRoot, "*", SearchOption.AllDirectories));
        }

        // The lender relents: the same grant, the ceiling lifted. The next
        // pass is the ordinary resumption — no operator command, no state
        // beyond what the objects record (05 §3).
        using (var sourceKeypair = PeerKeypairStore.Open(_source.StateDirectory))
        {
            _destinationGrants!.Pin(new PeerGrant(
                sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));
        }

        await Task.Delay(2_500, _timeout.Token); // one failure's back-off at --poll-seconds 1

        var second = await RunAgentAsync(
            "run", "--archives", _source.ArchivesRoot, "--state", _source.StateDirectory,
            "--passphrase-env", _source.PassphraseVariable, "--once", "--poll-seconds", "1");
        Assert.AreEqual(0, second.ExitCode, second.Error);

        var recovered = FallbackPlan.Application.DestinationSyncStore.Open(_source.StateDirectory)
            .Find(_source.DocsSetId, "friend");
        Assert.AreEqual(FallbackPlan.Application.DestinationSyncState.InSync, recovered!.State);
        Assert.IsNotEmpty(Directory.EnumerateFiles(replicasRoot, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task FanOut_TheDestinationNarrowedItsTerms_AdoptsThemAndLeavesADurableNotice()
    {
        _source.WriteSourceFile("notes.txt", "small enough to converge");

        // The source recorded a megabyte at pairing; the destination's grant
        // now says half that. The hello carries the current terms (05 §6),
        // the source adopts them, and the human is told before any refusal.
        var destinationFingerprint = await StartDestinationAsync(
            destinationTermsForSource: new PeerTerms(500_000, string.Empty, 0),
            sourceRecordsForDestination: new PeerTerms(1_000_000, string.Empty, 0));
        WritePeerConfiguration(destinationFingerprint);

        var run = await RunAgentAsync(
            "run", "--archives", _source.ArchivesRoot, "--state", _source.StateDirectory,
            "--passphrase-env", _source.PassphraseVariable, "--once");
        Assert.AreEqual(0, run.ExitCode, run.Error);

        // Replication itself converged — the new ceiling still fits this set.
        var record = FallbackPlan.Application.DestinationSyncStore.Open(_source.StateDirectory)
            .Find(_source.DocsSetId, "friend");
        Assert.AreEqual(FallbackPlan.Application.DestinationSyncState.InSync, record!.State);

        var notice = Assert.ContainsSingle(
            FallbackPlan.Application.NoticeStore.Open(_source.StateDirectory).Unacknowledged);
        Assert.Contains("narrowed", notice.Message, StringComparison.Ordinal);

        // The adopted terms are persisted with the grant — in force, not
        // merely displayed.
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);
        var grant = PeerGrantStore.Open(_source.StateDirectory).Find(destinationKeypair.Identity);
        Assert.AreEqual(500_000UL, grant!.Terms.QuotaBytes);
    }

    [TestMethod]
    public async Task FanOut_TheDestinationCannotStore_IsRecordedUnavailableForRetryNotAsPolicy()
    {
        _source.WriteSourceFile("notes.txt", "nowhere to put this");

        var destinationFingerprint = await StartDestinationAsync(
            destinationTermsForSource: PeerTerms.None,
            sourceRecordsForDestination: PeerTerms.None);
        WritePeerConfiguration(destinationFingerprint);

        // A file squatting where the replicas directory must be created: the
        // destination's storage fails, its quota never said no. Disk trouble
        // is a fault the lender fixes — storage_exhausted, not terms_refused
        // (05 §4) — so the source records it retryable, not failed.
        File.WriteAllText(Path.Combine(_destinationState, "replicas"), "not a directory");

        var run = await RunAgentAsync(
            "run", "--archives", _source.ArchivesRoot, "--state", _source.StateDirectory,
            "--passphrase-env", _source.PassphraseVariable, "--once");
        Assert.AreEqual(0, run.ExitCode, run.Error);

        var record = FallbackPlan.Application.DestinationSyncStore.Open(_source.StateDirectory)
            .Find(_source.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreEqual(FallbackPlan.Application.DestinationSyncState.Unavailable, record.State);
        Assert.Contains("cannot store", record.LastError!, StringComparison.Ordinal);

        // The reason code is what tells this apart from a policy refusal, and
        // the two lead to different people fixing different things (05 §5).
        // The recorded line does not carry the code on this path — the state
        // does — so the distinction is asserted where it lives: Unavailable
        // above, and no notice below, are together the shape of
        // storage_exhausted rather than terms_refused.
        Assert.DoesNotContain(
            nameof(PeerRefusalReason.TermsRefused), record.LastError!, StringComparison.Ordinal);

        // No notice: this is the destination's fault to fix, and back-off
        // retries close the gap when it does — the human on this side has no
        // action to take (05 §5).
        Assert.IsEmpty(FallbackPlan.Application.NoticeStore.Open(_source.StateDirectory).Unacknowledged);
    }

    [TestMethod]
    public async Task FanOut_ThePeersLoanIsNearlySpent_WarnsBeforeAPushRunsIntoTheCeiling()
    {
        // Until now the only news about capacity was the boundary stop
        // itself: exact, but delivered by the failure it was meant to
        // anticipate. The destination already had `quota − usage` in a local
        // one line before it sends the inventory, so it costs nothing to say.
        _source.WriteSourceFile("notes.txt", "converge me, then leave almost no room");

        var destinationFingerprint = await StartDestinationAsync(
            destinationTermsForSource: PeerTerms.None, sourceRecordsForDestination: PeerTerms.None);
        WritePeerConfiguration(destinationFingerprint);

        var first = await RunAgentAsync(
            "run", "--archives", _source.ArchivesRoot, "--state", _source.StateDirectory,
            "--passphrase-env", _source.PassphraseVariable, "--once");
        Assert.AreEqual(0, first.ExitCode, first.Error);

        // No quota, so nothing to be near the end of.
        Assert.IsEmpty(FallbackPlan.Application.NoticeStore.Open(_source.StateDirectory).Unacknowledged);

        // The lender now bounds the loan at a hair over what is already
        // stored. Nothing new needs to cross on the next pass, so the sync
        // still succeeds — which is exactly the situation worth warning
        // about, because the next new object will not fit.
        var used = (ulong)Directory
            .EnumerateFiles(Path.Combine(_destinationState, "replicas"), "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
        Assert.IsGreaterThan(0UL, used, "the first pass must have stored something to be near the ceiling of");

        using (var sourceKeypair = PeerKeypairStore.Open(_source.StateDirectory))
        {
            _destinationGrants!.Pin(new PeerGrant(
                sourceKeypair.Identity, "source", PeerRole.StoresHere,
                new PeerTerms(used + 8, string.Empty, 0), PairedAt));
        }

        var second = await RunAgentAsync(
            "sync", "--archives", _source.ArchivesRoot, "--state", _source.StateDirectory,
            "--passphrase-env", _source.PassphraseVariable);
        Assert.AreEqual(0, second.ExitCode, second.Error);

        var record = FallbackPlan.Application.DestinationSyncStore.Open(_source.StateDirectory)
            .Find(_source.DocsSetId, "friend");
        Assert.AreEqual(
            FallbackPlan.Application.DestinationSyncState.InSync, record!.State,
            "a warning, not a refusal: the sync still succeeded");

        var headroom = FallbackPlan.Application.NoticeStore.Open(_source.StateDirectory).Unacknowledged
            .SingleOrDefault(notice => notice.Key.StartsWith("destination-headroom:", StringComparison.Ordinal));
        Assert.IsNotNull(headroom, "the operator is told the loan is nearly spent");
        Assert.Contains("8 byte(s) left", headroom.Message, StringComparison.Ordinal);
    }

    private IPEndPoint? _endpoint;
    private string DestinationAddress => $"{_endpoint!.Address}:{_endpoint.Port}";
    private Stopper? _stop;
    private PeerGrantStore? _destinationGrants;

    /// <summary>
    /// Stands up the destination with the given terms for the source, records
    /// the destination into the source with the terms the source believes it
    /// was offered, and returns the destination's fingerprint.
    /// </summary>
    private async Task<string> StartDestinationAsync(
        PeerTerms destinationTermsForSource, PeerTerms sourceRecordsForDestination)
    {
        using var sourceKeypair = PeerKeypairStore.Open(_source.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);

        _destinationGrants = PeerGrantStore.Open(_destinationState);
        _destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, destinationTermsForSource, PairedAt));

        var sourceGrants = PeerGrantStore.Open(_source.StateDirectory);
        sourceGrants.Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, sourceRecordsForDestination, PairedAt));

        var listenerKeypair = PeerKeypairStore.Open(_destinationState);
        var listener = RemoteServiceListener.Start(
            listenerKeypair, _destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destinationState);
        listener.Bind(new UnusedService());
        _endpoint = listener.Endpoint;
        _stop = new Stopper(listener, listenerKeypair);

        await Task.CompletedTask;
        return destinationKeypair.Identity.Fingerprint;
    }

    private void WritePeerConfiguration(string destinationFingerprint) =>
        new FallbackPlan.Application.ClientConfiguration
        {
            SchemaVersion = FallbackPlan.Application.ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new FallbackPlan.Application.DestinationConfiguration
                {
                    Id = new string('1', 32),
                    Name = "friend",
                    Kind = FallbackPlan.Application.DestinationKind.Peer,
                    Fingerprint = destinationFingerprint,
                    Endpoint = DestinationAddress,
                },
            ],
            BackupSets =
            [
                new FallbackPlan.Application.BackupSetConfiguration
                {
                    Id = _source.DocsSetId,
                    Name = "docs",
                    Root = _source.SourceRoot,
                    Schedule = "every 4h",
                    Destinations = [new FallbackPlan.Application.SetDestinationReference { Ref = "friend" }],
                },
            ],
        }.Save(Path.Combine(_source.StateDirectory, "config.json"));

    private static async Task<(int ExitCode, string Output, string Error)> RunAgentAsync(params string[] args)
    {
        var output = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        var error = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        var exit = await AgentHost.RunAsync(args, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>The Bind contract needs a service; the replication path never calls it.</summary>
    private sealed class UnusedService : Api.IFallbackPlanService
    {
        public ValueTask<Api.ServiceResult> ExecuteAsync(Api.ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");

        public IAsyncEnumerable<Api.JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");
    }

    private sealed class Stopper(RemoteServiceListener listener, PeerKeypair keypair) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await listener.DisposeAsync();
            keypair.Dispose();
        }
    }

    public void Dispose()
    {
        if (_stop is not null)
        {
            _stop.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _timeout.Dispose();
        _source.Dispose();
        if (Directory.Exists(_destinationState))
        {
            try
            {
                Directory.Delete(_destinationState, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
