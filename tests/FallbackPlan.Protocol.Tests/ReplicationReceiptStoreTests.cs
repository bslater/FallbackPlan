using FallbackPlan.Protocol;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// The replication receipt's home (FR-DEST-004;
/// [ADR-0064](../../docs/adr/0064-replication-receipts.md)): one immutable
/// document per push under the state directory, beside the deletion receipts
/// and read through the same core, whose every displayed fact is read from
/// the bytes the destination signed and never from the envelope around them.
/// </summary>
[TestClass]
public sealed class ReplicationReceiptStoreTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "fbp-replication-receipt-tests", Guid.NewGuid().ToString("n"));

    private static ReplicationReceipt Receipt(ulong issuedAt, byte session = 0xAB) => new(
        SessionId: Enumerable.Repeat(session, ReplicationReceipt.SessionIdLength).ToArray(),
        RepositoryId: Enumerable.Repeat((byte)0x01, ReplicationOffer.RepositoryIdLength).ToArray(),
        CommanderPublicKey: Enumerable.Repeat((byte)0xC0, PeerIdentity.KeyLength).ToArray(),
        IssuedAtUnixMilliseconds: issuedAt,
        CommittedCount: 1,
        Committed: ["blobs/data/aa/bb/arrived"],
        HeldObjects: 7,
        HeldBytes: 4_096);

    private ReplicationReceiptStore Open() => ReplicationReceiptStore.Open(_stateDirectory);

    [TestMethod]
    public void File_ThenList_AnswersNewestFirstAndVerifies()
    {
        using var signer = PeerKeypair.Generate();
        var store = Open();
        foreach (var issuedAt in new ulong[] { 1_000, 3_000, 2_000 })
        {
            var signed = Receipt(issuedAt, session: (byte)issuedAt).EncodeForSigning();
            store.File(DeletionReceiptRole.Destination, signed, signer.Sign(signed), signer.Identity, null, null);
        }

        var listed = Open().List();

        Assert.HasCount(3, listed);
        CollectionAssert.AreEqual(
            new ulong[] { 3_000, 2_000, 1_000 },
            listed.Select(filed => filed.Receipt!.IssuedAtUnixMilliseconds).ToList());
        foreach (var filed in listed)
        {
            Assert.IsTrue(filed.Verified, filed.Problem);
            Assert.AreEqual(DeletionReceiptRole.Destination, filed.Role);
            Assert.AreEqual(signer.Identity.Fingerprint, filed.SignerFingerprint);
            Assert.AreEqual(7UL, filed.Receipt!.HeldObjects);
        }

        Assert.IsTrue(
            listed[0].Path.StartsWith(Path.Combine(_stateDirectory, "receipts", "replications"), StringComparison.Ordinal),
            "replication receipts live beside the deletions, not among them");
    }

    [TestMethod]
    public void List_ForOneRepository_LeavesTheOthersOut()
    {
        using var signer = PeerKeypair.Generate();
        var store = Open();
        var mine = Receipt(1_000).EncodeForSigning();
        store.File(DeletionReceiptRole.Commander, mine, signer.Sign(mine), signer.Identity, "docs", "friend");
        var other = (Receipt(2_000) with { RepositoryId = Enumerable.Repeat((byte)0x02, ReplicationOffer.RepositoryIdLength).ToArray() }).EncodeForSigning();
        store.File(DeletionReceiptRole.Commander, other, signer.Sign(other), signer.Identity, "photos", "friend");

        var listed = Open().List(repositoryIdHex: string.Concat(Enumerable.Repeat("01", ReplicationOffer.RepositoryIdLength)));

        var filed = Assert.ContainsSingle(listed);
        Assert.AreEqual("docs", filed.Set);
        Assert.AreEqual("friend", filed.Destination);
    }

    [TestMethod]
    public void Verify_ASignatureFromAnotherIdentity_Fails()
    {
        using var signer = PeerKeypair.Generate();
        using var impostor = PeerKeypair.Generate();
        var signed = Receipt(1_000).EncodeForSigning();
        Open().File(DeletionReceiptRole.Destination, signed, impostor.Sign(signed), signer.Identity, null, null);

        var filed = Assert.ContainsSingle(Open().List());

        Assert.IsFalse(filed.Verified);
        Assert.IsNotNull(filed.Receipt, "the statement still parses; it is the attribution that fails");
    }

    [TestMethod]
    public void Verify_AFlippedByteInTheSignedBytes_Fails()
    {
        using var signer = PeerKeypair.Generate();
        var signed = Receipt(1_000).EncodeForSigning();
        var path = Open().File(DeletionReceiptRole.Destination, signed, signer.Sign(signed), signer.Identity, null, null);

        // The last eight bytes are the held bytes: raising them keeps the
        // receipt parsing and makes the signature a signature of something else.
        var text = File.ReadAllText(path);
        var hex = Convert.ToHexStringLower(signed);
        File.WriteAllText(path, text.Replace(hex, hex[..^1] + "1", StringComparison.Ordinal));

        var filed = Assert.ContainsSingle(Open().List());

        Assert.IsFalse(filed.Verified);
        Assert.AreEqual(4_097UL, filed.Receipt!.HeldBytes, "what is shown is what is on disk, marked unverified");
    }

    [TestMethod]
    public void List_ADeletionReceiptFiledAmongReplications_IsReportedNotShownAsOne()
    {
        // The two kinds share a core and a label each; a file of the other
        // kind in this root is reported as the wrong kind, never parsed as
        // this one and never skipped.
        using var signer = PeerKeypair.Generate();
        var deletion = new DeletionReceipt(
            SessionId: Enumerable.Repeat((byte)0xAB, DeletionReceipt.SessionIdLength).ToArray(),
            RepositoryId: Enumerable.Repeat((byte)0x01, ReplicationOffer.RepositoryIdLength).ToArray(),
            CommanderPublicKey: Enumerable.Repeat((byte)0xC0, PeerIdentity.KeyLength).ToArray(),
            IssuedAtUnixMilliseconds: 1_000,
            FloorGenerations: 0,
            ReclaimPublicKey: ReadOnlyMemory<byte>.Empty,
            PageDigests: [new byte[DeletionReceipt.DigestLength]],
            DeletedCount: 0,
            Deleted: [],
            NotHeld: 0);
        var signed = deletion.EncodeForSigning();
        var path = DeletionReceiptStore.Open(_stateDirectory).File(
            DeletionReceiptRole.Destination, signed, signer.Sign(signed), signer.Identity, null, null);
        var misfiled = path.Replace(
            Path.Combine("receipts", "deletions"), Path.Combine("receipts", "replications"), StringComparison.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(misfiled)!);
        File.Copy(path, misfiled);

        var filed = Assert.ContainsSingle(Open().List());

        Assert.IsFalse(filed.Verified);
        Assert.IsNull(filed.Receipt);
        Assert.IsNotNull(filed.Problem);
        Assert.Contains("deletion", filed.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void List_AFileThatIsNotAReceipt_IsReportedNotSkipped()
    {
        var directory = Path.Combine(_stateDirectory, "receipts", "replications", "00");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stray.json"), "{ not json");

        var filed = Assert.ContainsSingle(Open().List());

        Assert.IsFalse(filed.Verified);
        Assert.IsNull(filed.Receipt);
        Assert.IsNotNull(filed.Problem);
    }

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }
}
