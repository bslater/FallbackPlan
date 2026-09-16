using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.RecoveryKit;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The morning the machine is gone
/// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md),
/// FR-REP-001, FR-KIT-006). Does not establish FR-DEST-006.
/// </summary>
/// <remarks>
/// <para>
/// A replica is attributed to a <b>pinned peer identity</b> (peer-protocol
/// 05 §2), a device keypair is per-installation, and a rebuilt machine has a
/// new one. So the peer will not hand a rebuilt machine its own replica, and
/// re-pairing does not transfer the attribution — deliberately, because that
/// rule is what stops a stranger asking a peer for a repository by name.
/// </para>
/// <para>
/// Every existing peer drill misses this, because they all preserve the state
/// directory. Preserving the state directory is precisely what a dead machine
/// does not do, so this suite destroys it: the archive, the configuration, the
/// catalogue and the device keypair all go, and what is left is what a person
/// actually keeps — the recovery kit and the passphrase.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class PeerClaimTests : IDisposable
{
    private readonly HostHarness _harness = new();

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-claim", Guid.NewGuid().ToString("n"));

    private readonly string _rebuiltState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-claim-rebuilt", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private const ulong PairedAt = 1_722_600_000_000;

    private CancellationToken Timeout => _timeout.Token;

    private IPEndPoint? _endpoint;
    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;
    private PeerGrantStore? _destinationGrants;
    private Protocol.PeerIdentity? _destinationIdentity;
    private bool _withoutClaimFeature;

    [TestMethod]
    public async Task ARebuiltMachine_WithItsKitAndPassphrase_ClaimsItsReplicaBack()
    {
        var repositoryId = await SeedAsync();

        // The machine is gone: archive, state, keypair, configuration. What
        // survives is the kit and the passphrase, in a drawer somewhere.
        DestroyTheSourceMachine();

        // A fresh install, paired with the friend as any new peer would be.
        // Pairing alone is not enough and never was — the replica is
        // attributed to a device identity that no longer exists anywhere.
        var rebuilt = PairRebuiltMachineAsync();
        var refusedFirst = await Assert.ThrowsExactlyAsync<PeerProtocolException>(
            () => OpenReplicaAsync(rebuilt, repositoryId));
        Assert.AreNotEqual(
            PeerRefusalReason.NotPaired,
            refusedFirst.Reason,
            "the rebuilt machine IS paired — this must fail on attribution, or it proves nothing");

        // The claim. Nothing but the printed kit and the passphrase produced
        // the key it signs under, which is the whole proposition: the two
        // things that survive a dead machine are enough.
        var accepted = await ClaimAsync(rebuilt);

        Assert.ContainsSingle(accepted.RepositoryIds);
        Assert.IsTrue(accepted.RepositoryIds[0].Span.SequenceEqual(repositoryId));

        // And now the replica opens, on the same request that was refused
        // three lines ago. The attribution followed the owner rather than the
        // hardware.
        await OpenReplicaAsync(rebuilt, repositoryId);

        var owner = ReplicaOwnerStore.Open(_destinationState).Find(Convert.ToHexStringLower(repositoryId));
        Assert.AreEqual(rebuilt.Identity.Fingerprint, owner!.Fingerprint);
        Assert.IsNotNull(owner.ClaimPublicKey, "the recorded key must survive the claim, or a second one is refused");
    }

    [TestMethod]
    public async Task TheClaimVerb_IsHowAPersonActuallyDoesThis()
    {
        // The ceremony is worth nothing if reaching it needs a test harness.
        // This is the whole recovery as somebody would type it: pair with the
        // friend, then point the verb at the kit and the passphrase. No
        // --fingerprint, because exactly one peer is pinned to store for this
        // machine and asking for it would be friction at the worst moment.
        var repositoryId = await SeedAsync();
        DestroyTheSourceMachine();
        var rebuilt = PairRebuiltMachineAsync();
        rebuilt.Dispose();

        var output = new StringWriter();
        var exit = await Cli.CliApplication.RunAsync(
            [
                "claim", $"{_endpoint!.Address}:{_endpoint.Port}",
                "--state", _rebuiltState,
                "--kit", Path.Combine(_harness.WorkPath, "kit.bin"),
                "--passphrase-env", _harness.PassphraseVariable,
            ],
            new System.CommandLine.InvocationConfiguration { Output = output, Error = output });

        Assert.AreEqual(0, exit, output.ToString());
        Assert.Contains(Convert.ToHexStringLower(repositoryId), output.ToString(), StringComparison.Ordinal);

        var owner = ReplicaOwnerStore.Open(_destinationState).Find(Convert.ToHexStringLower(repositoryId));
        Assert.AreEqual(
            PeerKeypairStore.Open(_rebuiltState).Identity.Fingerprint,
            owner!.Fingerprint,
            "the verb must move the attribution, not merely report that it could");
    }

    [TestMethod]
    public async Task AClaimForNothing_AndAClaimSignedByAStranger_RefuseIdentically()
    {
        // The no-reconnaissance rule (07 §4), applied where it matters most:
        // if "no such key here" and "that signature is wrong" were
        // distinguishable, this would be a way to ask a stranger's peer
        // whether it holds a given installation's replicas.
        _ = await SeedAsync();
        DestroyTheSourceMachine();
        var rebuilt = PairRebuiltMachineAsync();

        var stranger = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(stranger);
        var unknownKey = await Assert.ThrowsExactlyAsync<PeerProtocolException>(
            () => ClaimAsync(rebuilt, stranger));

        // The right key, a signature that is not over this session's bytes.
        var wrongSignature = await Assert.ThrowsExactlyAsync<PeerProtocolException>(
            () => ClaimAsync(rebuilt, ClaimSeed(), forge: true));

        Assert.AreEqual(PeerRefusalReason.TermsRefused, unknownKey.Reason);
        Assert.AreEqual(unknownKey.Reason, wrongSignature.Reason);
        Assert.AreEqual(unknownKey.Message, wrongSignature.Message);
    }

    [TestMethod]
    public async Task ADestinationTooOldToClaim_RefusesRatherThanIgnoring()
    {
        // The compatibility rule, pinned in the direction that matters. A
        // household where one machine updates first must get a refusal it can
        // read, not a claim that appears to work and moves nothing — the
        // owner would otherwise believe the replica was theirs again.
        _withoutClaimFeature = true;
        _ = await SeedAsync();
        DestroyTheSourceMachine();
        var rebuilt = PairRebuiltMachineAsync();

        var refusal = await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => ClaimAsync(rebuilt));

        Assert.AreNotEqual(
            PeerRefusalReason.MessageUnknown,
            refusal.Reason,
            "the type is defined; it is the feature that is not offered");
        Assert.Contains("replica-claim", refusal.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task TheFirstOffer_PublishesTheClaimKey_SoTheDestinationHasSomethingToCheck()
    {
        // The carrier, on its own. A destination holds no repository keys, so
        // the only thing it can ever check a claim against is a key the owner
        // published while the owner still existed — which means it has to
        // ride the ordinary offer, long before anyone needs it.
        //
        // The key recorded here is the installation's (ADR-0053 §1), so it is
        // the same 32 bytes for every set this installation writes; the
        // attribution is still per repository, because the quota and the
        // retrieval gate are.
        var repositoryId = await SeedAsync();

        var owners = ReplicaOwnerStore.Open(_destinationState);
        var owner = owners.Find(Convert.ToHexStringLower(repositoryId));

        Assert.IsNotNull(owner);
        Assert.IsNotNull(owner.ClaimPublicKey, "without this the owner can never prove the replica is theirs");
        Assert.HasCount(64, owner.ClaimPublicKey);
        Assert.AreNotEqual(
            owner.ReclaimPublicKey,
            owner.ClaimPublicKey,
            "deleting and re-pointing an attribution are different powers and must be different keys");
    }

    /// <summary>A backup to the peer, and the repository id it landed under.</summary>
    private async Task<byte[]> SeedAsync()
    {
        await StartDestinationAsync();
        WriteConfiguration();
        ProvisionInstallation();
        _harness.WriteSourceFile("docs/report.txt", "the only copy, once the machine is gone");

        var run = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "run", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--once");
        Assert.AreEqual(0, run.ExitCode, run.Error);

        var replica = await ReplicaPathAsync();

        return Convert.FromHexString(Path.GetFileName(replica));
    }

    /// <summary>First-run setup, as the console or the headless verb performs it.</summary>
    private void ProvisionInstallation()
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var parameters = RepositoryCreationSettings.Default.KdfParameters;

        using var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, parameters, salt, KdfValidationMode.CreateRepository);
        using var provisioning = new InstallationProvisioning(
            RepositoryWriteCredential.FromBytes(authority.Credential.ToBytes()), salt, parameters);

        Assert.IsTrue(new InstallationCredentialStore(_harness.StateDirectory).TrySave(provisioning));

        // The kit is written while the machine still exists, which is the
        // only time anybody can write one. It goes outside the state
        // directory because the state directory is about to be destroyed —
        // which is the point: a kit lives in a drawer, not on the machine.
        Directory.CreateDirectory(_harness.WorkPath);
        File.WriteAllBytes(
            Path.Combine(_harness.WorkPath, "kit.bin"),
            RecoveryKitCodec.Serialize(Repository.RecoveryKitFactory.BuildForInstallation(
                authority.Credential, salt, parameters, new byte[16],
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
    }

    /// <summary>
    /// The claim seed, from the kit and the passphrase and nothing else —
    /// no archive, no state directory, no repository id (ADR-0053 §1).
    /// </summary>
    private byte[] ClaimSeed()
    {
        var kit = RecoveryKitCodec.Parse(File.ReadAllBytes(Path.Combine(_harness.WorkPath, "kit.bin")));
        Assert.IsTrue(kit.IsInstallationKit, "a per-repository kit is a different claimant's case");

        using var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase,
            new Argon2Parameters
            {
                MemoryKiB = kit.KdfMemoryKiB,
                Iterations = kit.KdfIterations,
                Parallelism = kit.KdfParallelism,
            },
            kit.KdfSalt.Span,
            KdfValidationMode.OpenRepository);

        return authority.ClaimKeySeed.ToArray();
    }

    /// <summary>Makes the claim (peer-protocol 03 §6), or throws the peer's refusal.</summary>
    private async Task<ReplicationClaimAccepted> ClaimAsync(
        PeerKeypair keypair, byte[]? seed = null, bool forge = false)
    {
        using var signer = RepositorySigner.FromSeed(seed ?? ClaimSeed(), Domain.KeyGeneration.Zero);

        var grants = PeerGrantStore.Open(_rebuiltState);
        await using var connection = await PeerTlsConnection.DialAsync(
            _endpoint!.Address.ToString(), _endpoint.Port, DateTimeOffset.UtcNow, Timeout);
        var session = await PeerSessionDriver.DialAsync(
            connection, keypair, grants, _destinationIdentity!, "fallbackplan-agent",
            terms: null, requiredFeatures: null, cancellationToken: Timeout);

        // Forging signs the right bytes for the WRONG session, which is what
        // a captured claim replayed into a later connection would carry.
        var signed = forge
            ? ReplicationClaim.EncodeForSigning(new byte[SessionBinding.SessionIdLength], keypair.Identity.Fingerprint)
            : ReplicationClaim.EncodeForSigning(session.Binding.Span, keypair.Identity.Fingerprint);

        await PeerFrame.WriteAsync(
            session.Stream,
            new ReplicationClaim(signer.PublicKey.ToArray(), signer.Sign(signed)),
            Timeout);

        return await ReplicationWire.ReadAsync(
            session.Stream, PeerMessageType.ReplicationClaimAccepted, ReplicationClaimAccepted.Read, Timeout);
    }

    private void DestroyTheSourceMachine()
    {
        Directory.Delete(_harness.StateDirectory, recursive: true);
        if (Directory.Exists(_harness.ArchivesRoot))
        {
            Directory.Delete(_harness.ArchivesRoot, recursive: true);
        }
    }

    /// <summary>A fresh installation, pinned both ways with the friend.</summary>
    private PeerKeypair PairRebuiltMachineAsync()
    {
        Directory.CreateDirectory(_rebuiltState);
        var rebuilt = PeerKeypairStore.Open(_rebuiltState);

        _destinationGrants!.Pin(new PeerGrant(
            rebuilt.Identity, "rebuilt-source", PeerRole.StoresHere, PeerTerms.None, PairedAt));
        PeerGrantStore.Open(_rebuiltState).Pin(new PeerGrant(
            _destinationIdentity!, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        return rebuilt;
    }

    /// <summary>Asks the peer to open the replica for reading (peer-protocol 07 §3).</summary>
    private async Task OpenReplicaAsync(PeerKeypair keypair, byte[] repositoryId)
    {
        var grants = PeerGrantStore.Open(_rebuiltState);
        await using var connection = await PeerTlsConnection.DialAsync(
            _endpoint!.Address.ToString(), _endpoint.Port, DateTimeOffset.UtcNow, Timeout);
        var session = await PeerSessionDriver.DialAsync(
            connection, keypair, grants, _destinationIdentity!, "fallbackplan-agent",
            terms: null, requiredFeatures: null, cancellationToken: Timeout);

        await PeerFrame.WriteAsync(
            session.Stream, new RetrieveOpen(repositoryId, ReplicationInitiator.FormatCapability), Timeout);
        _ = await ReplicationWire.ReadAsync(
            session.Stream, PeerMessageType.RetrieveReady, RetrieveReady.Read, Timeout);
    }

    private async Task<string> ReplicaPathAsync()
    {
        var replicas = Path.Combine(_destinationState, "replicas");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var directories = Directory.Exists(replicas) ? Directory.GetDirectories(replicas) : [];
            if (directories.Length == 1 && Directory.Exists(Path.Combine(directories[0], "snapshots")))
            {
                return directories[0];
            }

            Assert.IsTrue(DateTimeOffset.UtcNow < deadline, "the destination never took a replica");
            await Task.Delay(100, Timeout);
        }
    }

    private void WriteConfiguration() => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32),
                Name = "friend",
                Kind = DestinationKind.Peer,
                Fingerprint = _destinationIdentity!.Fingerprint,
                Endpoint = $"{_endpoint!.Address}:{_endpoint.Port}",
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
                Destinations = [new SetDestinationReference { Ref = "friend" }],
                DirectShip = true,
            },
        ],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private async Task StartDestinationAsync()
    {
        using var sourceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);
        _destinationIdentity = destinationKeypair.Identity;

        _destinationGrants = PeerGrantStore.Open(_destinationState);
        _destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));

        PeerGrantStore.Open(_harness.StateDirectory).Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listenerKeypair = PeerKeypairStore.Open(_destinationState);
        _listener = RemoteServiceListener.Start(
            _listenerKeypair, _destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destinationState,
            offeredFeatures: _withoutClaimFeature
                ? [.. PeerSessionNegotiation.SupportedFeatures.Where(feature => !string.Equals(
                    feature, PeerSessionNegotiation.ReplicaClaimFeature, StringComparison.Ordinal))]
                : null);
        _listener.Bind(new UnusedService());
        _endpoint = _listener.Endpoint;

        await Task.CompletedTask;
    }

    /// <summary>The Bind contract needs a service; the replication path never calls it.</summary>
    private sealed class UnusedService : IFallbackPlanService
    {
        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");
    }

    public void Dispose()
    {
        _listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _listenerKeypair?.Dispose();
        _timeout.Dispose();
        _harness.Dispose();

        foreach (var directory in new[] { _destinationState, _rebuiltState })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A test directory that will not delete is not a test failure.
            }
        }
    }
}
