using System.Text.Json;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The service's receipts listing (contract 1.33; FR-GC-008, FR-DEST-004;
/// [ADR-0063](../../docs/adr/0063-deletion-receipts.md),
/// [ADR-0064](../../docs/adr/0064-replication-receipts.md)): both kinds
/// from the two stores under the state directory, interleaved newest first,
/// narrowed by kind, set, repository and count, each row the facts the
/// signed bytes attest plus the service's verdict on whether the signature
/// still holds over the bytes on disk — and nothing an operator's disk
/// reveals that the peer did not already say: no path, no signed bytes, no
/// key bytes. It is an audit listing, so any signed-in role and any caller
/// scope may read it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ReceiptsCommandTests : IDisposable
{
    private static readonly byte[] RepositoryId =
        Enumerable.Repeat((byte)0x01, ReplicationOffer.RepositoryIdLength).ToArray();

    private static readonly byte[] OtherRepositoryId =
        Enumerable.Repeat((byte)0x02, ReplicationOffer.RepositoryIdLength).ToArray();

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));
    private readonly PeerKeypair _peer = PeerKeypair.Generate();

    private CancellationToken Timeout => _timeout.Token;

    [TestMethod]
    public async Task ListReceipts_AnswersBothKindsNewestFirst_AsFactsOnly()
    {
        var deletion = FileDeletion(issuedAt: 1_000, role: DeletionReceiptRole.Destination, set: null, destination: null);
        var replication = FileReplication(issuedAt: 2_000, set: "docs", destination: "friend");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ReceiptsResult>(
            await handler.ExecuteAsync(new ListReceiptsCommand(), Timeout), out var listed);
        Assert.HasCount(2, listed.Receipts);

        var newest = listed.Receipts[0];
        Assert.AreEqual("replication", newest.Kind);
        Assert.AreEqual("commander", newest.Role);
        Assert.AreEqual("verified", newest.Status);
        Assert.IsTrue(newest.Verified);
        Assert.IsNull(newest.Problem);
        Assert.AreEqual(_peer.Identity.Fingerprint, newest.SignerFingerprint);
        Assert.AreEqual("docs", newest.Set);
        Assert.AreEqual("friend", newest.Destination);
        Assert.AreEqual(Convert.ToHexStringLower(RepositoryId), newest.RepositoryId);
        Assert.AreEqual(2_000UL, newest.IssuedAt);
        Assert.AreEqual(Convert.ToHexStringLower(Replication().SessionId.Span[..8]), newest.SessionPrefix);
        Assert.AreEqual(2UL, newest.CommittedCount);
        Assert.AreEqual(7UL, newest.HeldObjects);
        Assert.AreEqual(1_234UL, newest.HeldBytes);
        Assert.IsFalse(newest.DeletedCount.HasValue);
        Assert.IsFalse(newest.NotHeld.HasValue);

        var older = listed.Receipts[1];
        Assert.AreEqual("deletion", older.Kind);
        Assert.AreEqual("destination", older.Role);
        Assert.IsNull(older.Set);
        Assert.AreEqual(1_000UL, older.IssuedAt);
        Assert.AreEqual(1UL, older.DeletedCount);
        Assert.AreEqual(2UL, older.NotHeld);
        Assert.IsFalse(older.CommittedCount.HasValue);

        // Facts only: the wire carries neither where the files sit nor the
        // bytes that were signed.
        var wire = JsonSerializer.Serialize<ServiceResult>(listed, FrameCodec.SerializerOptions);
        Assert.DoesNotContain(deletion, wire, StringComparison.Ordinal);
        Assert.DoesNotContain(replication, wire, StringComparison.Ordinal);
        Assert.DoesNotContain(_harness.StateDirectory, wire, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexStringLower(Replication().EncodeForSigning()), wire, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexStringLower(_peer.Identity.PublicKey), wire, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ListReceipts_NarrowsByKindSetRepositoryAndLimit()
    {
        FileDeletion(issuedAt: 1_000, role: DeletionReceiptRole.Commander, set: "docs", destination: "friend");
        FileDeletion(issuedAt: 3_000, role: DeletionReceiptRole.Commander, set: "photos", destination: "friend",
            repositoryId: OtherRepositoryId);
        FileReplication(issuedAt: 2_000, set: "docs", destination: "friend");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var byKind = await ListAsync(handler, new ListReceiptsCommand(Kind: "deletion"));
        Assert.HasCount(2, byKind);
        Assert.IsTrue(byKind.All(row => row.Kind == "deletion"));

        var bySet = await ListAsync(handler, new ListReceiptsCommand(Set: "docs"));
        Assert.HasCount(2, bySet);
        Assert.IsTrue(bySet.All(row => row.Set == "docs"));

        var byRepository = await ListAsync(
            handler, new ListReceiptsCommand(Repository: Convert.ToHexStringLower(OtherRepositoryId)));
        var only = Assert.ContainsSingle(byRepository);
        Assert.AreEqual("photos", only.Set);

        var byLimit = await ListAsync(handler, new ListReceiptsCommand(Limit: 1));
        var first = Assert.ContainsSingle(byLimit);
        Assert.AreEqual(3_000UL, first.IssuedAt, "the bound keeps the newest");
    }

    [TestMethod]
    public async Task ListReceipts_AFileEditedAfterFiling_ReadsSignatureInvalidFromTheSignedBytes()
    {
        var path = FileDeletion(issuedAt: 1_000, role: DeletionReceiptRole.Destination, set: null, destination: null);
        var signed = Deletion(1_000, RepositoryId).EncodeForSigning();
        var text = File.ReadAllText(path);
        var hex = Convert.ToHexStringLower(signed);
        Assert.Contains(hex, text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(hex, hex[..^1] + "1", StringComparison.Ordinal));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var row = Assert.ContainsSingle(await ListAsync(handler, new ListReceiptsCommand()));
        Assert.AreEqual("signature-invalid", row.Status);
        Assert.IsFalse(row.Verified);
        Assert.AreEqual(1UL, row.NotHeld, "the facts shown are the edited file's, marked as unverified");
    }

    [TestMethod]
    public async Task ListReceipts_APairedRemoteConsole_IsAdmitted()
    {
        // An audit listing, not a decision: nothing here is the operator's
        // alone, so the line restart_service and reattribute_replica draw
        // is not drawn.
        FileReplication(issuedAt: 2_000, set: "docs", destination: "friend");

        await using var runtime = await StartAsync();
        var remote = new ServiceCommandHandler(runtime, RemoteBindingState.On("127.0.0.1:1"), CallerScope.Remote);

        Assert.ContainsSingle(await ListAsync(remote, new ListReceiptsCommand()));
    }

    [TestMethod]
    public async Task ListReceipts_AKindOrRepositoryOfTheWrongShape_IsRefusedByName()
    {
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ListReceiptsCommand(Kind: "refund"), Timeout), out var kind);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, kind.Reason);
        Assert.Contains("deletion", kind.Message, StringComparison.Ordinal);
        Assert.Contains("replication", kind.Message, StringComparison.Ordinal);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ListReceiptsCommand(Repository: "not-hex"), Timeout), out var repository);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, repository.Reason);
        Assert.Contains("32 hex", repository.Message, StringComparison.Ordinal);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ListReceiptsCommand(Limit: 0), Timeout), out var limit);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, limit.Reason);
    }

    public void Dispose()
    {
        _peer.Dispose();
        _timeout.Dispose();
        _harness.Dispose();
    }

    private async Task<IReadOnlyList<ReceiptDescriptor>> ListAsync(ServiceCommandHandler handler, ListReceiptsCommand command)
    {
        Assert.IsInstanceOfType<ReceiptsResult>(await handler.ExecuteAsync(command, Timeout), out var listed);
        return listed.Receipts;
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();
        return await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            Timeout);
    }

    private string FileDeletion(
        ulong issuedAt, DeletionReceiptRole role, string? set, string? destination, byte[]? repositoryId = null)
    {
        var signed = Deletion(issuedAt, repositoryId ?? RepositoryId).EncodeForSigning();
        return DeletionReceiptStore.Open(_harness.StateDirectory).File(
            role, signed, _peer.Sign(signed), _peer.Identity, set, destination);
    }

    private string FileReplication(ulong issuedAt, string set, string destination)
    {
        var signed = (Replication() with { IssuedAtUnixMilliseconds = issuedAt }).EncodeForSigning();
        return ReplicationReceiptStore.Open(_harness.StateDirectory).File(
            DeletionReceiptRole.Commander, signed, _peer.Sign(signed), _peer.Identity, set, destination);
    }

    private static DeletionReceipt Deletion(ulong issuedAt, byte[] repositoryId) => new(
        SessionId: Enumerable.Repeat((byte)0xAB, DeletionReceipt.SessionIdLength).ToArray(),
        RepositoryId: repositoryId,
        CommanderPublicKey: Enumerable.Repeat((byte)0xC0, Protocol.PeerIdentity.KeyLength).ToArray(),
        IssuedAtUnixMilliseconds: issuedAt,
        FloorGenerations: 3,
        ReclaimPublicKey: ReadOnlyMemory<byte>.Empty,
        PageDigests: [new byte[DeletionReceipt.DigestLength]],
        DeletedCount: 1,
        Deleted: ["snapshots/aa/bb/gone"],
        NotHeld: 2);

    private static ReplicationReceipt Replication() => new(
        SessionId: Enumerable.Repeat((byte)0xCD, ReplicationReceipt.SessionIdLength).ToArray(),
        RepositoryId: RepositoryId,
        CommanderPublicKey: Enumerable.Repeat((byte)0xC0, Protocol.PeerIdentity.KeyLength).ToArray(),
        IssuedAtUnixMilliseconds: 2_000,
        CommittedCount: 2,
        Committed: ["blobs/data/aa/one", "snapshots/aa/two"],
        HeldObjects: 7,
        HeldBytes: 1_234);
}
