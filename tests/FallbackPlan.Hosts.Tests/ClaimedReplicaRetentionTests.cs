using System.Net;
using System.Security.Cryptography;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Protocol;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A claimed replica is readable at once and deletable only once the
/// destination's operator has acknowledged the claim (FR-DR-005;
/// peer-protocol 06 §3 and 07 §5.9; threat model T-21).
/// </summary>
/// <remarks>
/// <para>
/// The claim ceremony (03 §6) hands a replica to whoever proves the owner's
/// passphrase, and a passphrase can be stolen. Reading is safe to hand over
/// unattended: the passphrase already decrypts the repository wherever its
/// bytes turn up, and a recovery that waits for a sleeping friend is a
/// recovery that fails. Deleting is not: a stolen passphrase must not be able
/// to quietly destroy the copy that outlived the machine it was taken from.
/// </para>
/// <para>
/// So this runs the whole of it for real: a machine backs up to a friend's
/// service, a rebuilt machine claims the replica with the passphrase alone,
/// and then asks the friend to delete. The friend's service is wired as the
/// host wires it — the listener serving from the runtime's attribution ledger
/// and raising into the runtime's notices — because the acknowledgement that
/// releases the hold is a command to that runtime.
/// </para>
/// </remarks>
[TestClass]
public sealed class ClaimedReplicaRetentionTests : IDisposable
{
    private readonly HostHarness _source = new();
    private readonly HostHarness _destination = new();

    private readonly string _rebuiltState =
        Path.Combine(Path.GetTempPath(), "fbp-claimed-retention", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private const ulong PairedAt = 1_722_600_000_000;

    /// <summary>A key the replica holds that no snapshot reaches, so the floor cannot be what refuses.</summary>
    private static readonly ObjectKey Condemned = ObjectKey.Parse("blobs/data/zz/condemned");

    private CancellationToken Timeout => _timeout.Token;

    private ServiceRuntime? _runtime;
    private RemoteServiceListener? _listener;
    private ServiceCommandHandler? _local;
    private PeerKeypair? _listenerKeypair;
    private PeerGrantStore? _destinationGrants;
    private Protocol.PeerIdentity? _destinationIdentity;
    private IPEndPoint? _endpoint;
    private readonly List<PeerTlsConnection> _connections = [];
    private readonly List<PeerKeypair> _keypairs = [];

    [TestMethod]
    public async Task AClaim_HoldsTheClaimantsDeletions_UntilTheOperatorAcknowledgesIt()
    {
        var repositoryId = await SeedAsync();
        var repositoryIdHex = Convert.ToHexStringLower(repositoryId);
        var rebuilt = PairRebuiltMachine();

        await ClaimAsync(rebuilt);

        // The claim is on the record as held, and a notice at the friend's
        // machine asks its operator for exactly the decision that releases it.
        Assert.IsTrue(_runtime!.ReplicaOwners.Find(repositoryIdHex)!.ClaimAwaitingAcknowledgement);
        var notice = _runtime.Notices.Unacknowledged.Single(notice => notice.Key == $"replica-claimed:{repositoryIdHex}");
        Assert.Contains("rebuilt-source", notice.Message, StringComparison.Ordinal);
        Assert.Contains("acknowledge-claim", notice.Message, StringComparison.Ordinal);

        // The claimant's retention instruction is refused whole: the object it
        // names survives, and the refusal says why.
        var replica = new LocalFileSystemObjectStore(ReplicaPath(repositoryIdHex));
        await PlantAsync(replica);
        var refused = await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => InstructAsync(rebuilt, repositoryId));
        Assert.AreEqual(PeerRefusalReason.TermsRefused, refused.Reason);
        Assert.Contains("acknowledged", refused.Message, StringComparison.Ordinal);
        Assert.IsTrue(
            (await replica.GetMetadataAsync(Condemned, Timeout)).Found,
            "a claim nobody acknowledged deleted from the replica");

        // The operator acknowledges, and the same instruction is then served.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await _local!.ExecuteAsync(new AcknowledgeReplicaClaimCommand(repositoryIdHex), Timeout));
        var served = await InstructAsync(rebuilt, repositoryId);
        Assert.AreEqual(1UL, served.Deleted);
        Assert.IsFalse((await replica.GetMetadataAsync(Condemned, Timeout)).Found);
        Assert.IsFalse(_runtime.Notices.Unacknowledged.Any(notice => notice.Key == $"replica-claimed:{repositoryIdHex}"));
    }

    [TestMethod]
    public async Task AClaimedReplica_IsReadableBeforeItsClaimIsAcknowledged()
    {
        // The other half of the requirement: reading never waits for anyone.
        var repositoryId = await SeedAsync();
        var rebuilt = PairRebuiltMachine();

        await ClaimAsync(rebuilt);

        Assert.IsTrue(_runtime!.ReplicaOwners.Find(Convert.ToHexStringLower(repositoryId))!.ClaimAwaitingAcknowledgement);
        await OpenReplicaAsync(rebuilt, repositoryId);
    }

    [TestMethod]
    public async Task ARestoreFromAClaimedReplica_CompletesBeforeAnyoneAcknowledgesTheClaim()
    {
        // The acceptance itself, the way a person gets there: the machine is
        // lost, a rebuilt one claims its replica with the verb, adopts it,
        // and restores the file — while, as far as this recovery is
        // concerned, the friend is asleep. Nobody at that end does anything.
        var repositoryIdHex = Convert.ToHexStringLower(await SeedAsync());
        await RebuildSourceAsync();

        var claim = await HostHarness.RunAsync(
            (arguments, output, error, _) => Cli.CliApplication.RunAsync(
                arguments,
                new System.CommandLine.InvocationConfiguration
                {
                    Output = output, Error = error, EnableDefaultExceptionHandler = false,
                }),
            "claim", $"{_endpoint!.Address}:{_endpoint.Port}",
            "--state", _source.StateDirectory, "--passphrase-env", _source.PassphraseVariable);
        Assert.AreEqual(0, claim.ExitCode, claim.All);

        await using var rebuilt = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _source.ArchivesRoot, StateDirectory = _source.StateDirectory },
            Timeout);
        var handler = new ServiceCommandHandler(rebuilt, RemoteBindingState.Off);
        await AdoptAsync(handler, repositoryIdHex);

        var source = await _source.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", "friend", Timeout);
        var recovered = Path.Combine(_source.WorkPath, "recovered");
        Assert.IsInstanceOfType<Api.RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    source.Snapshots[0].SnapshotId, null, recovered, Source: source.SourceId, InPlace: true),
                Timeout),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        Assert.AreEqual(
            "the only copy, once the machine is gone",
            File.ReadAllText(Path.Combine(recovered, "docs", "report.txt")));

        // All of it with the claim still waiting on the friend's operator.
        Assert.IsTrue(_runtime!.ReplicaOwners.Find(repositoryIdHex)!.ClaimAwaitingAcknowledgement);
        Assert.IsTrue(_runtime.Notices.Unacknowledged.Any(notice => notice.Key == $"replica-claimed:{repositoryIdHex}"));
    }

    /// <summary>
    /// Starts the friend's service, provisions the source, and backs the
    /// source up to the friend, direct to the peer.
    /// </summary>
    /// <returns>The repository id the replica landed under.</returns>
    private async Task<byte[]> SeedAsync()
    {
        await StartDestinationAsync();
        WriteSourceConfiguration();
        ProvisionSource();
        _source.WriteSourceFile("docs/report.txt", "the only copy, once the machine is gone");

        var run = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "run", "--archives", _source.ArchivesRoot, "--state", _source.StateDirectory,
            "--once");
        Assert.AreEqual(0, run.ExitCode, run.Error);

        var replicas = Path.Combine(_destination.StateDirectory, "replicas");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var directories = Directory.Exists(replicas) ? Directory.GetDirectories(replicas) : [];
            if (directories.Length == 1 && Directory.Exists(Path.Combine(directories[0], "snapshots")))
            {
                var owner = _runtime!.ReplicaOwners.Find(Path.GetFileName(directories[0]));
                Assert.IsNotNull(owner?.ClaimPublicKey, "the first offer must publish the claim key");
                Assert.IsNotNull(owner.ReclaimPublicKey, "the first offer must publish the reclaim key");
                return Convert.FromHexString(Path.GetFileName(directories[0]));
            }

            Assert.IsTrue(DateTimeOffset.UtcNow < deadline, "the destination never took a replica");
            await Task.Delay(100, Timeout);
        }
    }

    /// <summary>
    /// The friend's machine: a set-up installation with its service running,
    /// and the remote binding over it as the host wires it.
    /// </summary>
    private async Task StartDestinationAsync()
    {
        await _destination.SetupAsync();
        _runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _destination.ArchivesRoot, StateDirectory = _destination.StateDirectory },
            Timeout);

        using var sourceKeypair = PeerKeypairStore.Open(_source.StateDirectory);
        _listenerKeypair = PeerKeypairStore.Open(_destination.StateDirectory);
        _destinationIdentity = _listenerKeypair.Identity;

        // No floor: the floor is the safeguard that holds when everything else
        // fails, and leaving it in could let it be what refuses.
        _destinationGrants = PeerGrantStore.Open(_destination.StateDirectory);
        _destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));
        PeerGrantStore.Open(_source.StateDirectory).Pin(new PeerGrant(
            _destinationIdentity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listener = RemoteServiceListener.Start(
            _listenerKeypair, _destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destination.StateDirectory,
            owners: _runtime.ReplicaOwners, notices: _runtime.Notices);
        var binding = RemoteBindingState.On(_listener.Endpoint.ToString());
        _listener.Bind(new ServiceCommandHandler(_runtime, binding, CallerScope.Remote));
        _local = new ServiceCommandHandler(_runtime, binding, CallerScope.Local);
        _endpoint = _listener.Endpoint;
    }

    /// <summary>
    /// First-run setup of the source, as the console or the headless verb
    /// performs it: the installation credential its claim key derives from.
    /// </summary>
    private void ProvisionSource()
    {
        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var parameters = RepositoryCreationSettings.Default.KdfParameters;

        using var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(_source.PassphraseVariable)!);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, parameters, salt, KdfValidationMode.CreateRepository);
        using var provisioning = new InstallationProvisioning(
            RepositoryWriteCredential.FromBytes(authority.Credential.ToBytes()), salt, parameters);

        Assert.IsTrue(new InstallationCredentialStore(_source.StateDirectory).TrySave(provisioning));
    }

    private void WriteSourceConfiguration(bool withDocsSet = true) => new ClientConfiguration
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
        BackupSets = withDocsSet
            ?
            [
                new BackupSetConfiguration
                {
                    Id = _source.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _source.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = [new SetDestinationReference { Ref = "friend" }],
                    DirectShip = true,
                },
            ]
            : [],
    }.Save(Path.Combine(_source.StateDirectory, "config.json"));

    /// <summary>
    /// The source machine is lost and rebuilt: a fresh installation under the
    /// same passphrase and a new salt, paired with the friend both ways, its
    /// configuration naming the friend and no set — where a person is before
    /// they have claimed anything.
    /// </summary>
    private async Task RebuildSourceAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_source.StateDirectory, recursive: true);
        Directory.CreateDirectory(_source.StateDirectory);
        var setup = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "setup", "--archives", _source.ArchivesRoot, "--state", _source.StateDirectory,
            "--passphrase-env", _source.PassphraseVariable, "--acknowledge-loss",
            "--user", HostHarness.OwnerUser, "--password-env", _source.PasswordVariable);
        Assert.AreEqual(0, setup.ExitCode, setup.All);

        using var rebuilt = PeerKeypairStore.Open(_source.StateDirectory);
        _destinationGrants!.Pin(new PeerGrant(
            rebuilt.Identity, "rebuilt-source", PeerRole.StoresHere, PeerTerms.None, PairedAt));
        PeerGrantStore.Open(_source.StateDirectory).Pin(new PeerGrant(
            _destinationIdentity!, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));
        WriteSourceConfiguration(withDocsSet: false);
    }

    /// <summary>
    /// Discovers the claimed replica at the friend and adopts it, sealing the
    /// passphrase's derivation under the replica's own salt to the rebuilt
    /// service, as the console's recovery does.
    /// </summary>
    private async Task AdoptAsync(ServiceCommandHandler handler, string repositoryIdHex)
    {
        Assert.IsInstanceOfType<ArchivesDiscoveredResult>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand("friend"), Timeout), out var discovered);
        var row = Assert.ContainsSingle(discovered.Archives);
        Assert.AreEqual(repositoryIdHex, row.RepositoryId);

        var parameters = new Argon2Parameters
        {
            MemoryKiB = row.KdfMemoryKib, Iterations = row.KdfIterations, Parallelism = row.KdfParallelism,
        };
        var salt = Convert.FromHexString(row.KdfSalt);
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), Timeout), out var description);

        string envelope;
        using (var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(_source.PassphraseVariable)!))
        using (var authority = WriteOnlyDerivation.Derive(passphrase, parameters, salt, KdfValidationMode.OpenRepository))
        {
            envelope = Convert.ToHexStringLower(WriteOnlyProvisioning.SealProvision(
                Convert.FromHexString(description.RestoreGrantRecipient!), authority, salt, parameters));
        }

        var (_, result) = await HostHarness.PreviewThenAdoptAsync(
            handler.ExecuteAsync, new AdoptArchiveCommand("friend", repositoryIdHex, envelope), Timeout);
        Assert.IsInstanceOfType<ArchiveAdoptedResult>(
            result, out var adopted, (result as ServiceError)?.Message ?? "adoption of the claimed replica refused");
        Assert.AreEqual("docs", adopted.SetName);
    }

    /// <summary>A fresh installation after the source is lost, paired with the friend as any new peer is.</summary>
    private PeerKeypair PairRebuiltMachine()
    {
        Directory.CreateDirectory(_rebuiltState);
        var rebuilt = PeerKeypairStore.Open(_rebuiltState);
        _keypairs.Add(rebuilt);

        _destinationGrants!.Pin(new PeerGrant(
            rebuilt.Identity, "rebuilt-source", PeerRole.StoresHere, PeerTerms.None, PairedAt));
        PeerGrantStore.Open(_rebuiltState).Pin(new PeerGrant(
            _destinationIdentity!, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));
        return rebuilt;
    }

    private async Task<PeerSession> DialAsync(PeerKeypair keypair)
    {
        var connection = await PeerTlsConnection.DialAsync(
            _endpoint!.Address.ToString(), _endpoint.Port, DateTimeOffset.UtcNow, Timeout);
        _connections.Add(connection);
        return await PeerSessionDriver.DialAsync(
            connection, keypair, PeerGrantStore.Open(_rebuiltState), _destinationIdentity!, "fallbackplan-agent",
            terms: null, requiredFeatures: null, cancellationToken: Timeout);
    }

    /// <summary>The claim ceremony (03 §6) with the passphrase and nothing else.</summary>
    private async Task ClaimAsync(PeerKeypair keypair)
    {
        var session = await DialAsync(keypair);
        await PeerFrame.WriteAsync(session.Stream, new ReplicationClaimOpen(), Timeout);
        var parameters = await ReplicationWire.ReadAsync(
            session.Stream, PeerMessageType.ReplicationClaimParameters, ReplicationClaimParameters.Read, Timeout);

        var signed = ReplicationClaim.EncodeForSigning(session.Binding.Span, keypair.Identity.Fingerprint);
        using var secret = Passphrase.Create(Environment.GetEnvironmentVariable(_source.PassphraseVariable)!);
        var keys = new List<ReadOnlyMemory<byte>>();
        var signatures = new List<ReadOnlyMemory<byte>>();
        for (var index = 0; index < parameters.Salts.Count; index++)
        {
            using var authority = WriteOnlyDerivation.Derive(
                secret,
                new Argon2Parameters
                {
                    MemoryKiB = parameters.MemoryKiB[index],
                    Iterations = parameters.Iterations[index],
                    Parallelism = parameters.Parallelism[index],
                },
                parameters.Salts[index].Span,
                KdfValidationMode.OpenRepository);
            using var signer = RepositorySigner.FromSeed(authority.ClaimKeySeed.ToArray(), KeyGeneration.Zero);
            keys.Add(signer.PublicKey.ToArray());
            signatures.Add(signer.Sign(signed));
        }

        await PeerFrame.WriteAsync(session.Stream, new ReplicationClaim(keys, signatures), Timeout);
        var accepted = await ReplicationWire.ReadAsync(
            session.Stream, PeerMessageType.ReplicationClaimAccepted, ReplicationClaimAccepted.Read, Timeout);
        Assert.ContainsSingle(accepted.RepositoryIds);
    }

    /// <summary>Asks the friend to open the replica for reading (peer-protocol 07 §3).</summary>
    private async Task OpenReplicaAsync(PeerKeypair keypair, byte[] repositoryId)
    {
        var session = await DialAsync(keypair);
        await PeerFrame.WriteAsync(
            session.Stream, new RetrieveOpen(repositoryId, ReplicationInitiator.FormatCapability), Timeout);
        _ = await ReplicationWire.ReadAsync(
            session.Stream, PeerMessageType.RetrieveReady, RetrieveReady.Read, Timeout);
    }

    /// <summary>
    /// The rebuilt machine as a commander: it offers the repository with
    /// nothing to push, then instructs the deletion of <see cref="Condemned"/>
    /// under the repository's reclaim key, which its passphrase derives, signed
    /// over this session.
    /// </summary>
    /// <returns>The friend's acknowledgement, or its refusal thrown.</returns>
    private async Task<RetentionAck> InstructAsync(PeerKeypair keypair, byte[] repositoryId)
    {
        using var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(_source.PassphraseVariable)!);
        var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(
            new LocalFileSystemObjectStore(ReplicaPath(Convert.ToHexStringLower(repositoryId))), passphrase, Timeout);
        using var _repository = repository;
        using var _authority = authority;

        var session = await DialAsync(keypair);
        await PeerFrame.WriteAsync(
            session.Stream,
            new ReplicationOffer(
                repositoryId, ReplicationInitiator.FormatCapability, "all",
                repository.Credential.ReclaimPublicKey.ToArray()),
            Timeout);

        while (true)
        {
            var inventory = await ReplicationWire.ReadAsync(
                session.Stream, PeerMessageType.ReplicationInventory, ReplicationInventory.Read, Timeout);
            if (!inventory.More)
            {
                break;
            }
        }

        if (session.Supports(PeerSessionNegotiation.PartialObjectResumeFeature))
        {
            _ = await ReplicationWire.ReadAsync(
                session.Stream, PeerMessageType.ReplicationPartial, ReplicationPartial.Read, Timeout);
        }

        await PeerFrame.WriteAsync(session.Stream, new ReplicationComplete(0), Timeout);
        _ = await ReplicationWire.ReadAsync(
            session.Stream, PeerMessageType.ReplicationAck, ReplicationAck.Read, Timeout);

        var page = new RetentionOffer(repositoryId, [Condemned.Value], More: false);
        var generation = repository.CurrentMetadataGeneration;
        using var reclaim = new ReclaimAuthority(authority.ReclaimKeySeed);
        var seed = reclaim.SeedFor(generation);
        try
        {
            using var signer = RepositorySigner.FromSeed(seed, generation);
            page = page with { Signature = signer.Sign(page.EncodeForSigning(session.Binding.Span)) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }

        await PeerFrame.WriteAsync(session.Stream, page, Timeout);
        return await ReplicationWire.ReadAsync(
            session.Stream, PeerMessageType.RetentionAck, RetentionAck.Read, Timeout);
    }

    /// <summary>Puts the condemned object in the replica, so a deletion of it is observable.</summary>
    private async Task PlantAsync(LocalFileSystemObjectStore replica)
    {
        var put = await replica.PutAsync(
            Condemned,
            _ => ValueTask.FromResult<Stream>(new MemoryStream("an object only an acknowledged claim may delete"u8.ToArray())),
            PutConditions.None,
            Timeout);
        Assert.AreEqual(PutOutcome.Created, put.Outcome);
    }

    private string ReplicaPath(string repositoryIdHex) =>
        Path.Combine(_destination.StateDirectory, "replicas", repositoryIdHex);

    public void Dispose()
    {
        foreach (var connection in _connections)
        {
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _listenerKeypair?.Dispose();
        foreach (var keypair in _keypairs)
        {
            keypair.Dispose();
        }

        _timeout.Dispose();
        _source.Dispose();
        _destination.Dispose();

        try
        {
            if (Directory.Exists(_rebuiltState))
            {
                Directory.Delete(_rebuiltState, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }
}
