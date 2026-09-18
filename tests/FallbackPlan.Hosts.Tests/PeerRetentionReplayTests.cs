using System.Net;
using System.Security.Cryptography;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A deletion instruction is only as good as the session it was authorised
/// for, and nobody may excuse themselves from proving it
/// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §5,
/// [ADR-0059](../../docs/adr/0059-session-bound-deletion-authority.md)).
/// Establishes the peer half of FR-GC-008, with FR-GC-007's floor deliberately
/// set to nothing so that it cannot be what refuses.
/// </summary>
/// <remarks>
/// <para>
/// A signed <c>RetentionOffer</c> page cannot be forged and cannot be edited.
/// It can be <b>replayed</b>: the signature covers the page's own bytes and
/// says nothing about when they were authorised, so a page recorded from one
/// session verifies perfectly in the next.
/// </para>
/// <para>
/// That is not a theoretical hole, it is the scenario the reclaim key exists
/// for. A compromised write-only service holds the peer device key, so it can
/// open an authenticated session whenever it likes; it cannot derive the
/// reclaim key, so it cannot author a deletion — and it does not need to,
/// because it can send one it kept. The service that "cannot author a
/// deletion" deletes.
/// </para>
/// <para>
/// This suite signs one page and sends those exact bytes in two successive
/// sessions, which is indistinguishable from capturing and replaying and needs
/// no wire tap. The object is put back between them, so the second session's
/// deletion is of something that exists now and should not be destroyed by a
/// stale instruction — rather than the harmless idempotent no-op a replay
/// usually lands on.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class PeerRetentionReplayTests : IDisposable
{
    private readonly HostHarness _source = new();

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-retention-replay", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private const ulong PairedAt = 1_722_600_000_000;

    /// <summary>A key the replica holds that no snapshot reaches, so the floor cannot be what refuses.</summary>
    private static readonly ObjectKey Condemned = ObjectKey.Parse("blobs/data/zz/condemned");

    private CancellationToken Timeout => _timeout.Token;

    private IPEndPoint? _endpoint;
    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;
    private Protocol.PeerIdentity? _destinationIdentity;

    [TestMethod]
    public async Task RetentionOffer_APageFromAnEarlierSession_IsAcceptedAgainInALaterOne()
    {
        await SeedAsync();
        var replica = new LocalFileSystemObjectStore(await ReplicaPathAsync());

        // One page, signed once with the repository's real reclaim key, inside
        // a real session — and kept, which is the whole of the attack.
        RetentionOffer? recorded = null;

        await PlantAsync(replica);
        var honest = await InstructAsync(async binding =>
        {
            recorded = await SignedDropAsync(binding, Condemned.Value);
            return [recorded];
        });

        Assert.AreEqual(1UL, honest.Deleted, "the genuine instruction did not delete what it named");
        Assert.IsFalse((await replica.GetMetadataAsync(Condemned, Timeout)).Found);

        // The object comes back — the source re-shipped it, a later capture
        // referenced it again, any ordinary reason. The instruction that
        // condemned it belonged to a session that is over, and the recording
        // goes out unchanged into a new one.
        await PlantAsync(replica);
        var replayed = await Assert.ThrowsExactlyAsync<PeerProtocolException>(
            () => InstructAsync(_ => Task.FromResult<RetentionOffer[]>([recorded!])));

        Assert.AreEqual(PeerRefusalReason.TermsRefused, replayed.Reason);
        Assert.Contains("session", replayed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(
            (await replica.GetMetadataAsync(Condemned, Timeout)).Found,
            "the replayed instruction destroyed an object that was put back after it was authorised");
    }

    [TestMethod]
    public async Task RetentionOffer_FromASourceThatSimplyDoesNotOfferSignedRetention_DeletesUnsigned()
    {
        // Worse than replay, and found while planning the fix for it. The
        // requirement to sign is a NEGOTIATED feature, and negotiation is an
        // intersection of what the two sides offer — so the party the feature
        // defends against is the party that decides whether it applies. A
        // source that omits `signed-retention` from its hello is not refused;
        // the spoke stops asking for a signature and deletes whatever it is
        // told, bounded only by the floor.
        //
        // That is forgery, not replay: the drop-list is arbitrary rather than
        // one the reclaim authority once approved. It needs no captured page
        // and no reclaim key — only the device key, which is exactly what a
        // compromised service holds.
        await SeedAsync();
        var replica = new LocalFileSystemObjectStore(await ReplicaPathAsync());
        await PlantAsync(replica);

        var forged = new RetentionOffer(await RepositoryIdAsync(), [Condemned.Value], More: false);

        // The spoke recorded this repository's reclaim public key when it
        // first took the replica, and that is not a fact a later session can
        // withdraw. The refusal is total, exactly as a floor breach is.
        var refusal = await Assert.ThrowsExactlyAsync<PeerProtocolException>(
            () => InstructAsync(
                offerSignedRetention: false, pages: _ => Task.FromResult<RetentionOffer[]>([forged])));
        Assert.AreEqual(PeerRefusalReason.TermsRefused, refusal.Reason);
        Assert.Contains("reclaim", refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.IsTrue(
            (await replica.GetMetadataAsync(Condemned, Timeout)).Found,
            "the spoke destroyed an object on the authority of a session, which is the authority ADR-0055 "
                + "took away from it");
    }

    [TestMethod]
    public async Task RetentionOffer_ToASpokeTooOldToVerifyABoundSignature_IsSignedTheWayThatSpokeCanCheck()
    {
        // The rollout case, and the only direction that gets an
        // accommodation. A household updates one machine before the other, so
        // a current commander will meet a spoke that verifies the older
        // unbound encoding. It signs what that spoke can check, because the
        // feature is a statement about what the other side understands — and
        // the deletion goes through rather than every page being refused.
        //
        // The reverse has no accommodation on purpose: a spoke that accepted
        // both encodings would be accepting the replayable one.
        await SeedAsync(spokeUnderstandsSessionBinding: false);
        var replica = new LocalFileSystemObjectStore(await ReplicaPathAsync());
        await PlantAsync(replica);

        var deleted = await InstructAsync(async binding =>
        {
            Assert.IsTrue(binding.IsEmpty, "a commander must not bind a signature a spoke cannot check");
            return [await SignedDropAsync(binding, Condemned.Value)];
        });

        Assert.AreEqual(1UL, deleted.Deleted);
        Assert.IsFalse((await replica.GetMetadataAsync(Condemned, Timeout)).Found);
    }

    [TestMethod]
    public async Task RetentionAck_CarriesAReceiptTheDestinationSigned_OverTheSessionThePageAndTheKeys()
    {
        // The audit half of FR-GC-008 on the peer plane: the destination
        // holds no repository key, so what it can attest is signed under its
        // own device key — and what it attests is exactly what it was told,
        // in which session, and what it did about it (ADR-0063).
        await SeedAsync();
        var replica = new LocalFileSystemObjectStore(await ReplicaPathAsync());
        await PlantAsync(replica);

        ReadOnlyMemory<byte> session = default;
        RetentionOffer? sent = null;
        var ack = await InstructAsync(async binding =>
        {
            session = binding;
            sent = await SignedDropAsync(binding, Condemned.Value);
            return [sent];
        });

        Assert.AreEqual(1UL, ack.Deleted);
        Assert.IsFalse(ack.Receipt.IsEmpty, "the spoke deleted and sent no receipt");
        Assert.IsTrue(
            _destinationIdentity!.Verify(ack.Receipt.Span, ack.Signature.Span),
            "the receipt is not the destination's own statement");

        var receipt = DeletionReceipt.Parse(ack.Receipt.Span);
        Assert.IsFalse(session.IsEmpty, "the spoke under test binds signatures to the session");
        CollectionAssert.AreEqual(session.ToArray(), receipt.SessionId.ToArray(), "the receipt names another session");
        CollectionAssert.AreEqual(await RepositoryIdAsync(), receipt.RepositoryId.ToArray());

        using var commander = PeerKeypairStore.Open(_source.StateDirectory);
        CollectionAssert.AreEqual(commander.Identity.PublicKey.ToArray(), receipt.CommanderPublicKey.ToArray());

        Assert.AreEqual(0u, receipt.FloorGenerations, "this pairing's terms carry no floor");
        var owner = ReplicaOwnerStore.Open(_destinationState).Find(await RepositoryIdHexAsync());
        CollectionAssert.AreEqual(
            Convert.FromHexString(owner!.ReclaimPublicKey!), receipt.ReclaimPublicKey.ToArray(),
            "the receipt must name the key the instruction was verified against");

        var digest = Assert.ContainsSingle(receipt.PageDigests);
        CollectionAssert.AreEqual(
            SHA256.HashData(sent!.EncodeForSigning(session.Span)), digest.ToArray(),
            "the receipt does not commit to the page that was sent");

        Assert.AreEqual(1UL, receipt.DeletedCount);
        Assert.AreEqual(Condemned.Value, Assert.ContainsSingle(receipt.Deleted));
        Assert.AreEqual(0u, receipt.NotHeld);
    }

    [TestMethod]
    public async Task RetentionAck_AKeyTheDestinationNeverHeld_IsCountedInTheReceiptAndNotListedAsDeleted()
    {
        // An instruction can name what the destination lost, never took, or
        // already deleted under an earlier one. The receipt says how many
        // such keys there were and lists only what was actually removed —
        // listing them as deleted would attest to a deletion that did not
        // happen.
        await SeedAsync();
        var replica = new LocalFileSystemObjectStore(await ReplicaPathAsync());
        await PlantAsync(replica);

        var ack = await InstructAsync(async binding =>
            [await SignedDropAsync(binding, Condemned.Value, "blobs/data/zz/never-held")]);

        Assert.AreEqual(1UL, ack.Deleted);
        var receipt = DeletionReceipt.Parse(ack.Receipt.Span);
        Assert.AreEqual(Condemned.Value, Assert.ContainsSingle(receipt.Deleted));
        Assert.AreEqual(1UL, receipt.DeletedCount);
        Assert.AreEqual(1u, receipt.NotHeld);
    }

    [TestMethod]
    public async Task RetentionAck_TheDestinationFilesItsOwnCopyBeforeAnswering()
    {
        // Both parties keep the receipt and neither depends on the other's
        // copy: the destination's is the record of what it did on whose
        // instruction, kept where its operator can read it back.
        await SeedAsync();
        var replica = new LocalFileSystemObjectStore(await ReplicaPathAsync());
        await PlantAsync(replica);

        var ack = await InstructAsync(async binding => [await SignedDropAsync(binding, Condemned.Value)]);

        var filed = Assert.ContainsSingle(
            DeletionReceiptStore.Open(_destinationState).List(await RepositoryIdHexAsync()));
        Assert.AreEqual(DeletionReceiptRole.Destination, filed.Role);
        Assert.IsTrue(filed.Verified, filed.Problem);
        Assert.IsNull(filed.Problem);
        Assert.IsNull(filed.Set, "a destination files under no set name of the commander's");
        Assert.AreEqual(_destinationIdentity!.Fingerprint, filed.SignerFingerprint);
        Assert.AreEqual(DeletionReceipt.Parse(ack.Receipt.Span), filed.Receipt);
    }

    /// <summary>A backup, so the destination holds a replica and has recorded the reclaim key.</summary>
    private async Task SeedAsync(bool spokeUnderstandsSessionBinding = true)
    {
        await StartDestinationAsync(spokeUnderstandsSessionBinding);
        await _source.SetupAsync();
        _source.WriteSourceFile("notes.txt", "something worth keeping");
        WriteConfiguration();

        var run = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "run", "--archives", _source.ArchivesRoot, "--state", _source.StateDirectory,
            "--once");
        Assert.AreEqual(0, run.ExitCode, run.Error);

        var owner = ReplicaOwnerStore.Open(_destinationState).Find(await RepositoryIdHexAsync());
        Assert.IsNotNull(owner);
        Assert.IsNotNull(owner.ReclaimPublicKey, "the spoke must hold a key to check instructions against");
    }

    /// <summary>Puts the condemned object back, so a deletion of it is observable.</summary>
    private async Task PlantAsync(LocalFileSystemObjectStore replica)
    {
        _ = await replica.DeleteAsync(Condemned, DeleteConditions.None, Timeout);
        var put = await replica.PutAsync(
            Condemned,
            _ => ValueTask.FromResult<Stream>(new MemoryStream("an object a stale instruction must not reach"u8.ToArray())),
            PutConditions.None,
            Timeout);
        Assert.AreEqual(PutOutcome.Created, put.Outcome);
    }

    /// <summary>
    /// One page condemning the named keys, signed under the repository's
    /// reclaim key over the given session's identifier.
    /// </summary>
    private async Task<RetentionOffer> SignedDropAsync(ReadOnlyMemory<byte> sessionBinding, params string[] keys)
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_source.PassphraseVariable)!);
        var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(
            new LocalFileSystemObjectStore(_source.RepositoryPath), passphrase, Timeout);
        using var _repository = repository;
        using var _authority = authority;

        // The reclaim key is the passphrase's to derive and never the write
        // credential's (ADR-0055 §2) — this test is the authority a console
        // would be.
        var page = new RetentionOffer(repository.RepositoryId.ToArray(), keys, More: false);
        var generation = repository.CurrentMetadataGeneration;
        using var reclaim = new ReclaimAuthority(authority.ReclaimKeySeed);
        var seed = reclaim.SeedFor(generation);
        try
        {
            using var signer = RepositorySigner.FromSeed(seed, generation);
            return page with { Signature = signer.Sign(page.EncodeForSigning(sessionBinding.Span)) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>
    /// Opens a session, offers the repository with nothing to push, and sends
    /// the given retention pages — the shape a commander's pass takes, with
    /// the push half emptied out.
    /// </summary>
    /// <param name="pages">Builds the retention pages, given the session's identifier.</param>
    /// <returns>The spoke's acknowledgement — the count, and the receipt when it sends one.</returns>
    private Task<RetentionAck> InstructAsync(Func<ReadOnlyMemory<byte>, Task<RetentionOffer[]>> pages) =>
        InstructAsync(offerSignedRetention: true, pages);

    /// <summary>
    /// The same exchange, with this side's offered feature set under the
    /// caller's control — which is the point: what a source offers is a source's
    /// own choice, and a feature the source declines is a feature the pair does
    /// not have.
    /// </summary>
    /// <param name="offerSignedRetention">Whether to offer <c>signed-retention</c> at the hello.</param>
    /// <param name="pages">
    /// Builds the retention pages once the session is open, so a page may be
    /// signed over this session's identifier — or, for a replay, so a page
    /// built for an earlier one may be returned unchanged.
    /// </param>
    /// <returns>The spoke's acknowledgement — the count, and the receipt when it sends one.</returns>
    private async Task<RetentionAck> InstructAsync(
        bool offerSignedRetention, Func<ReadOnlyMemory<byte>, Task<RetentionOffer[]>> pages)
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_source.PassphraseVariable)!);
        var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(
            new LocalFileSystemObjectStore(_source.RepositoryPath), passphrase, Timeout);
        using var _repository = repository;
        using var _authority = authority;
        var reclaimPublicKey = repository.Credential.ReclaimPublicKey.ToArray();

        using var keypair = PeerKeypairStore.Open(_source.StateDirectory);
        var grants = PeerGrantStore.Open(_source.StateDirectory);

        await using var connection = await PeerTlsConnection.DialAsync(
            _endpoint!.Address.ToString(), _endpoint.Port, DateTimeOffset.UtcNow, Timeout);
        var session = await PeerSessionDriver.DialAsync(
            connection, keypair, grants, _destinationIdentity!, "fallbackplan-agent",
            terms: null, requiredFeatures: null, logger: null,
            offeredFeatures: offerSignedRetention
                ? null
                : [.. PeerSessionNegotiation.SupportedFeatures.Where(feature => !string.Equals(
                    feature, PeerSessionNegotiation.SignedRetentionFeature, StringComparison.Ordinal))],
            cancellationToken: Timeout);

        Assert.AreEqual(
            offerSignedRetention,
            session.Supports(PeerSessionNegotiation.SignedRetentionFeature),
            "the session did not negotiate what this test set out to exercise");

        await PeerFrame.WriteAsync(
            session.Stream,
            new ReplicationOffer(
                repository.RepositoryId.ToArray(), ReplicationInitiator.FormatCapability, "all", reclaimPublicKey),
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

        // The same rule the fan-out applies: bind only to a spoke that says it
        // verifies over one (02 §6).
        var binding = session.Supports(PeerSessionNegotiation.SessionBoundRetentionFeature)
            ? session.Binding
            : default;

        foreach (var page in await pages(binding))
        {
            await PeerFrame.WriteAsync(session.Stream, page, Timeout);
        }

        return await ReplicationWire.ReadAsync(
            session.Stream, PeerMessageType.RetentionAck, RetentionAck.Read, Timeout);
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

    private async Task<string> RepositoryIdHexAsync() => Path.GetFileName(await ReplicaPathAsync());

    private async Task<byte[]> RepositoryIdAsync() => Convert.FromHexString(await RepositoryIdHexAsync());

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
                Id = _source.DocsSetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = _source.SourceRoot }],
                Schedule = "every 4h",
                Destinations = [new SetDestinationReference { Ref = "friend" }],
            },
        ],
    }.Save(Path.Combine(_source.StateDirectory, "config.json"));

    private async Task StartDestinationAsync(bool understandsSessionBinding = true)
    {
        using var sourceKeypair = PeerKeypairStore.Open(_source.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);
        _destinationIdentity = destinationKeypair.Identity;

        var destinationGrants = PeerGrantStore.Open(_destinationState);

        // No floor: the floor is the safeguard that holds when everything else
        // fails, and leaving it in would let it be what refuses a replay and
        // hide whether the signature ever noticed (FR-GC-007).
        destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));

        PeerGrantStore.Open(_source.StateDirectory).Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listenerKeypair = PeerKeypairStore.Open(_destinationState);
        _listener = RemoteServiceListener.Start(
            _listenerKeypair, destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destinationState,
            offeredFeatures: understandsSessionBinding
                ? null
                : [.. PeerSessionNegotiation.SupportedFeatures.Where(feature => !string.Equals(
                    feature, PeerSessionNegotiation.SessionBoundRetentionFeature, StringComparison.Ordinal))]);
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
        _source.Dispose();

        try
        {
            if (Directory.Exists(_destinationState))
            {
                Directory.Delete(_destinationState, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }
}
