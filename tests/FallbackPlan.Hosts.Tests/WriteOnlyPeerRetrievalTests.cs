using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;
using RestoreResult = FallbackPlan.Api.RestoreResult;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The write-only total-staging-loss drill over the wire (ADR-0042 §5,
/// peer-protocol 07): a v2 set provisioned by sealed envelope backs up
/// passphrase-free and fans out to a paired peer; then site A's staging
/// archives are DESTROYED. The peer's replica — which the peer can read no
/// byte of — serves structure browsing with the write bundle alone, refuses
/// content honestly without a grant, and restores byte-identically once the
/// passphrase re-derives the scalar and it arrives sealed on the source
/// open. The passphrase is the recovery factor; the service never held it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class WriteOnlyPeerRetrievalTests : IDisposable
{
    private const string PassphraseText = "the over-the-wire write-only passphrase";

    private readonly HostHarness _siteOne = new();
    private readonly HostHarness _siteTwo = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(5));

    public void Dispose()
    {
        _timeout.Dispose();
        _siteOne.Dispose();
        _siteTwo.Dispose();
    }

    [TestMethod]
    public async Task WriteOnlyPeerRetrieval_AfterTotalStagingLoss_GrantRestoresOverTheWire()
    {
        var notes = "sealed notes that must come back over the wire";
        _siteOne.WriteSourceFile("notes.txt", notes);

        // Site B: a live service with its listener, storing for site A.
        await _siteTwo.CreateRepositoryAsync();
        await using var runtimeTwo = await StartV1Async(_siteTwo);
        using var keypairTwo = PeerKeypairStore.Open(_siteTwo.StateDirectory);
        var grantsTwo = PeerGrantStore.Open(_siteTwo.StateDirectory);
        await using var listener = RemoteServiceListener.Start(
            keypairTwo, grantsTwo, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            replicationStateDirectory: _siteTwo.StateDirectory);
        var handlerTwo = new ServiceCommandHandler(
            runtimeTwo, RemoteBindingState.On(listener.Endpoint.ToString()));
        listener.Bind(handlerTwo);

        Assert.IsInstanceOfType<PairingInviteResult>(
            await handlerTwo.ExecuteAsync(
                new CreatePairingInviteCommand("site-a", "stores-here", QuotaBytes: null, TimeToLiveMinutes: 60),
                _timeout.Token),
            out var invite);

        string snapshotId;
        string recipient;

        // What the recovery kit records for a v2 repository (ADR-0042 §8):
        // where the archive is, and how to re-derive — the KDF salt and
        // parameters. Captured here before the loss, exactly as a kit is.
        byte[] kdfSalt;
        Domain.Configuration.Argon2Parameters kdfParameters;

        await using (var runtimeOne = await StartWriteOnlyAsync(_siteOne))
        {
            var handlerOne = new ServiceCommandHandler(runtimeOne, RemoteBindingState.Off);
            Assert.IsInstanceOfType<PairingCompletedResult>(
                await handlerOne.ExecuteAsync(
                    new PairWithInviteCommand(invite.Code, "127.0.0.1", listener.Endpoint.Port, "site-b"),
                    _timeout.Token),
                out var paired);

            Assert.IsInstanceOfType<AcknowledgedResult>(await handlerOne.ExecuteAsync(
                new UpsertDestinationCommand(new DestinationDescriptor(
                    Id: null, Name: "site-b", Kind: "peer", Path: null,
                    Fingerprint: paired.Fingerprint, Endpoint: $"127.0.0.1:{listener.Endpoint.Port}")),
                _timeout.Token));
            Assert.IsInstanceOfType<AcknowledgedResult>(await handlerOne.ExecuteAsync(
                new UpsertBackupSetCommand(new BackupSetDescriptor(
                    _siteOne.DocsSetId, "docs", _siteOne.SourceRoot, Schedule: null, [], [], ["site-b"])),
                _timeout.Token));

            // The provisioning ceremony: the client derives, the service gets
            // a sealed envelope, and this service never sees the passphrase.
            Assert.IsInstanceOfType<ServiceDescriptionResult>(
                await handlerOne.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);
            recipient = description.RestoreGrantRecipient!;
            var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
            Assert.IsInstanceOfType<ConfigurationChangeResult>(
                await handlerOne.ExecuteAsync(
                    new ProvisionWriteOnlySetCommand("docs", SealProvision(recipient, salt)),
                    _timeout.Token));

            await RunBackupAndWaitAsync(runtimeOne, handlerOne);
            await WaitForAsync(() =>
                runtimeOne.DestinationSync.Find(_siteOne.DocsSetId, "site-b") is
                { State: DestinationSyncState.InSync });

            Assert.IsInstanceOfType<SnapshotsResult>(
                await handlerOne.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var listed);
            snapshotId = Assert.ContainsSingle(listed.Snapshots).SnapshotId;

            var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(
                new LocalFileSystemObjectStore(_siteOne.RepositoryPath), _timeout.Token);
            kdfSalt = descriptor.KdfSalt.ToArray();
            kdfParameters = descriptor.KdfParameters;
        }

        // ---- The drill: site A's archives are gone. What remains is the
        // pairing, the stored write bundle, and — with the person — the
        // passphrase. The service still starts without one.
        Directory.Delete(_siteOne.ArchivesRoot, recursive: true);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        await using var recovered = await StartWriteOnlyAsync(_siteOne);
        var handler = new ServiceCommandHandler(recovered, RemoteBindingState.Off);

        // Structure over the wire, write bundle alone: the peer's replica
        // opens, snapshots list, the tree browses — and content refuses
        // honestly without a grant.
        var opened = await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs", "site-b"), _timeout.Token);
        if (opened is ServiceError refusal)
        {
            Assert.Fail($"peer source refused: {refusal.Reason}: {refusal.Message}");
        }

        Assert.IsInstanceOfType<RestoreSourceOpenedResult>(opened, out var structural);
        Assert.AreEqual(snapshotId, Assert.ContainsSingle(structural.Snapshots).SnapshotId);
        Assert.IsInstanceOfType<DirectoryResult>(
            await handler.ExecuteAsync(
                new ListDirectoryCommand(snapshotId, null, Source: structural.SourceId), _timeout.Token),
            out var top);
        Assert.AreEqual("notes.txt", Assert.ContainsSingle(top.Entries).Name);

        var sealedOut = Path.Combine(_siteOne.WorkPath, "sealed-over-the-wire");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(snapshotId, null, sealedOut, Source: structural.SourceId, InPlace: true),
                _timeout.Token),
            out var withoutGrant);
        Assert.AreEqual(0, withoutGrant.Restored);
        Assert.IsTrue(withoutGrant.Failed > 0, "peer-held sealed content must not read without a grant");
        Assert.IsInstanceOfType<AcknowledgedResult>(
            await handler.ExecuteAsync(new CloseRestoreSourceCommand(structural.SourceId), _timeout.Token));

        // The passphrase re-derives against the kit-recorded salt and
        // parameters; the grant arrives sealed; the bytes come back.
        Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
            await handler.ExecuteAsync(
                new OpenRestoreSourceCommand("docs", "site-b", Envelope: SealGrant(recipient, kdfSalt, kdfParameters)),
                _timeout.Token),
            out var granted);

        var output = Path.Combine(_siteOne.WorkPath, "granted-over-the-wire");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(snapshotId, null, output, Source: granted.SourceId, InPlace: true),
                _timeout.Token),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        Assert.AreEqual(notes, File.ReadAllText(Path.Combine(output, "notes.txt")));

        Assert.IsInstanceOfType<AcknowledgedResult>(
            await handler.ExecuteAsync(new CloseRestoreSourceCommand(granted.SourceId), _timeout.Token));
    }

    private static string SealProvision(string recipientHex, byte[] salt)
    {
        var parameters = Domain.Configuration.RepositoryCreationSettings.Default.KdfParameters;
        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, parameters, salt, Domain.Configuration.KdfValidationMode.OpenRepository);
        return Convert.ToHexStringLower(
            WriteOnlyProvisioning.SealProvision(Convert.FromHexString(recipientHex), authority, salt, parameters));
    }

    private static string SealGrant(string recipientHex, byte[] salt, Domain.Configuration.Argon2Parameters parameters)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, parameters, salt, Domain.Configuration.KdfValidationMode.OpenRepository);
        return Convert.ToHexStringLower(
            WriteOnlyProvisioning.SealGrant(Convert.FromHexString(recipientHex), authority.SealingPrivateKey));
    }

    private async Task RunBackupAndWaitAsync(ServiceRuntime runtime, ServiceCommandHandler handler)
    {
        var progress = runtime.Progress.WatchAsync(_timeout.Token);
        var watching = Task.Run(
            async () =>
            {
                await foreach (var observation in progress)
                {
                    if (observation.Progress.State is JobState.Complete or JobState.CompletedWithFailures)
                    {
                        return observation.Progress.State;
                    }
                }

                return JobState.Pending;
            },
            _timeout.Token);

        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand("docs", Full: false), _timeout.Token));
        Assert.AreEqual(JobState.Complete, await watching, "the write-only backup completes");
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        while (!condition())
        {
            _timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, _timeout.Token);
        }
    }

    private async Task<ServiceRuntime> StartV1Async(HostHarness site)
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(site.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = site.ArchivesRoot, StateDirectory = site.StateDirectory },
            passphrase,
            _timeout.Token);
    }

    private async Task<ServiceRuntime> StartWriteOnlyAsync(HostHarness site) =>
        await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = site.ArchivesRoot, StateDirectory = site.StateDirectory },
            passphrase: null,
            _timeout.Token);
}
