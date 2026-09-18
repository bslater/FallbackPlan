using FallbackPlan.Protocol;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// The deletion receipt's home (FR-GC-008;
/// [ADR-0063](../../docs/adr/0063-deletion-receipts.md)): one immutable
/// document per instruction under the state directory, on both sides of the
/// wire, whose every displayed fact is read from the bytes the destination
/// signed and never from the envelope around them.
/// </summary>
[TestClass]
public sealed class DeletionReceiptStoreTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "fbp-receipt-tests", Guid.NewGuid().ToString("n"));

    private static DeletionReceipt Receipt(ulong issuedAt, byte session = 0xAB) => new(
        SessionId: Enumerable.Repeat(session, DeletionReceipt.SessionIdLength).ToArray(),
        RepositoryId: Enumerable.Repeat((byte)0x01, ReplicationOffer.RepositoryIdLength).ToArray(),
        CommanderPublicKey: Enumerable.Repeat((byte)0xC0, PeerIdentity.KeyLength).ToArray(),
        IssuedAtUnixMilliseconds: issuedAt,
        FloorGenerations: 0,
        ReclaimPublicKey: ReadOnlyMemory<byte>.Empty,
        PageDigests: [new byte[DeletionReceipt.DigestLength]],
        DeletedCount: 1,
        Deleted: ["snapshots/aa/bb/gone"],
        NotHeld: 0);

    private DeletionReceiptStore Open() => DeletionReceiptStore.Open(_stateDirectory);

    [TestMethod]
    public void File_ThenList_AnswersNewestFirstAndVerifies()
    {
        using var signer = PeerKeypair.Generate();
        var store = Open();
        foreach (var issued in new ulong[] { 1_000, 3_000, 2_000 })
        {
            var signed = Receipt(issued, session: (byte)issued).EncodeForSigning();
            store.File(DeletionReceiptRole.Destination, signed, signer.Sign(signed), signer.Identity, set: null, destination: null);
        }

        var listed = Open().List();

        Assert.HasCount(3, listed);
        CollectionAssert.AreEqual(
            new ulong[] { 3_000, 2_000, 1_000 },
            listed.Select(filed => filed.Receipt!.IssuedAtUnixMilliseconds).ToArray());
        Assert.IsTrue(listed.All(filed => filed.Verified), "every receipt was signed by the identity it names");
        Assert.IsTrue(listed.All(filed => filed.Problem is null));
        Assert.AreEqual(signer.Identity.Fingerprint, listed[0].SignerFingerprint);
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
        Assert.AreEqual(DeletionReceiptRole.Commander, filed.Role);
    }

    [TestMethod]
    public void Verify_ASignatureFromAnotherIdentity_Fails()
    {
        using var signer = PeerKeypair.Generate();
        using var impostor = PeerKeypair.Generate();
        var signed = Receipt(1_000).EncodeForSigning();

        // Filed as if the impostor's signature were the signer's: the file
        // names one identity and carries a signature made by another.
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

        // Tamper with the envelope's copy of the signed bytes: the deleted
        // key's last character. A reader that displayed the envelope would
        // show the altered key as attested; one that reads what was signed
        // sees the signature refuse.
        var text = File.ReadAllText(path);
        var hex = Convert.ToHexStringLower(signed);
        var tampered = Convert.ToHexStringLower(signed.Select((b, i) => i == signed.Length - 5 ? (byte)(b ^ 1) : b).ToArray());
        Assert.AreNotEqual(hex, tampered);
        File.WriteAllText(path, text.Replace(hex, tampered, StringComparison.Ordinal));

        var filed = Assert.ContainsSingle(Open().List());
        Assert.IsFalse(filed.Verified, "a byte changed under the signature must not read as verified");
    }

    [TestMethod]
    public void List_AFileThatIsNotAReceipt_IsReportedNotSkipped()
    {
        var store = Open();
        using var signer = PeerKeypair.Generate();
        var signed = Receipt(1_000).EncodeForSigning();
        var path = store.File(DeletionReceiptRole.Destination, signed, signer.Sign(signed), signer.Identity, null, null);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "9999-garbage.json"), "{ not json");

        var listed = Open().List();

        Assert.HasCount(2, listed);
        var broken = Assert.ContainsSingle(listed.Where(filed => filed.Receipt is null));
        Assert.IsFalse(broken.Verified);
        Assert.IsNotNull(broken.Problem);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_stateDirectory))
            {
                Directory.Delete(_stateDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }
}
