using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The service's pairing management verbs (ADR-0030 §3): listing the grants a
/// device holds and revoking one — the revocation being unilateral at the
/// service, the party at risk — and re-pointing a replica stored here at a
/// different paired device with no service listening (ADR-0053 §3,
/// FR-REP-001), the file-direct arm of the verb — and reading back the
/// deletion receipts filed here, every printed fact taken from the signed
/// bytes and never from the envelope around them
/// ([ADR-0063](../../docs/adr/0063-deletion-receipts.md), FR-GC-008).
/// </summary>
[TestClass]
public sealed class AgentPairingVerbsTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "fbp-agent-verbs", Guid.NewGuid().ToString("n"));

    private PeerIdentity Pin(string label)
    {
        var grants = PeerGrantStore.Open(_state);
        using var peer = PeerKeypair.Generate();
        grants.Pin(new PeerGrant(peer.Identity, label, PeerRole.StoresHere, PeerTerms.None, 1_722_600_000_000));
        return peer.Identity;
    }

    [TestMethod]
    public async Task Pairings_WithGrantsHeld_ListsEachByFingerprintAndLabel()
    {
        var one = Pin("laptop");
        var two = Pin("phone");

        var result = await HostHarness.RunAsync(AgentHost.RunAsync, "pairings", "--state", _state);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.Contains(one.Fingerprint, result.Output, StringComparison.Ordinal);
        Assert.Contains(two.Fingerprint, result.Output, StringComparison.Ordinal);
        Assert.Contains("laptop", result.Output, StringComparison.Ordinal);
        Assert.Contains("phone", result.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Unpair_ByFingerprint_RemovesThatGrantAlone()
    {
        var keep = Pin("laptop");
        var drop = Pin("phone");

        var result = await HostHarness.RunAsync(AgentHost.RunAsync, "unpair", "--state", _state, "--fingerprint", drop.Fingerprint);

        Assert.AreEqual(0, result.ExitCode, result.Error);

        var grants = PeerGrantStore.Open(_state);
        Assert.IsNull(grants.Find(drop));
        Assert.IsNotNull(grants.Find(keep));
    }

    [TestMethod]
    public async Task Unpair_AFingerprintThatMatchesNothing_IsRefusedAndChangesNothing()
    {
        Pin("laptop");

        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync, "unpair", "--state", _state, "--fingerprint", "zzzzzzzz");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("no pairing matches", result.Error, StringComparison.Ordinal);
        Assert.AreEqual(1, PeerGrantStore.Open(_state).Grants.Count);
    }

    private const string RepositoryIdHex = "0123456789abcdef0123456789abcdef";

    [TestMethod]
    public async Task Reattribute_NoServiceListening_RePointsTheLedgerAndSaysSo()
    {
        var gone = Pin("gone");
        var rebuilt = Pin("rebuilt");
        Assert.IsTrue(ReplicaOwnerStore.Open(_state).TryAttribute(RepositoryIdHex, gone.Fingerprint));

        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync, "reattribute", "--state", _state,
            "--repository", RepositoryIdHex, "--to", rebuilt.Fingerprint[..8]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.Contains("rebuilt", result.Output, StringComparison.Ordinal);
        Assert.Contains(gone.Fingerprint, result.Output, StringComparison.Ordinal);
        Assert.AreEqual(rebuilt.Fingerprint, ReplicaOwnerStore.Open(_state).Find(RepositoryIdHex)!.Fingerprint);

        // On the record for whoever reads this machine next.
        Assert.IsTrue(NoticeStore.Open(_state).Unacknowledged
            .Any(notice => notice.Key == $"replica-reattributed:{RepositoryIdHex}"));
    }

    [TestMethod]
    public async Task Receipts_WithAFiledReceipt_PrintsWhatWasSignedAndThatItVerifies()
    {
        using var spoke = PeerKeypair.Generate();
        var signed = Receipt().EncodeForSigning();
        DeletionReceiptStore.Open(_state).File(
            DeletionReceiptRole.Commander, signed, spoke.Sign(signed), spoke.Identity, "docs", "friend");

        var result = await HostHarness.RunAsync(AgentHost.RunAsync, "receipts", "--state", _state);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.Contains("verified", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("SIGNATURE INVALID", result.Output, StringComparison.Ordinal);
        Assert.Contains("commander", result.Output, StringComparison.Ordinal);
        Assert.Contains("docs", result.Output, StringComparison.Ordinal);
        Assert.Contains("friend", result.Output, StringComparison.Ordinal);
        Assert.Contains("snapshots/aa/bb/gone", result.Output, StringComparison.Ordinal);
        Assert.Contains(spoke.Identity.Fingerprint, result.Output, StringComparison.Ordinal);
        Assert.Contains("2 not held", result.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Receipts_AFileEditedAfterFiling_PrintsSignatureInvalidFromTheSignedBytes()
    {
        using var spoke = PeerKeypair.Generate();
        var signed = Receipt().EncodeForSigning();
        var path = DeletionReceiptStore.Open(_state).File(
            DeletionReceiptRole.Destination, signed, spoke.Sign(signed), spoke.Identity, null, null);

        // The statement's last four bytes are the not-held count. Changing
        // its last digit keeps the receipt parsing and makes the signature a
        // signature of something else — the edit an operator's disk could
        // suffer, or an operator could make.
        var text = File.ReadAllText(path);
        var hex = Convert.ToHexStringLower(signed);
        Assert.Contains(hex, text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(hex, hex[..^1] + "1", StringComparison.Ordinal));

        var result = await HostHarness.RunAsync(AgentHost.RunAsync, "receipts", "--state", _state);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.Contains("SIGNATURE INVALID", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("verified", result.Output, StringComparison.Ordinal);
        Assert.Contains("1 not held", result.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Receipts_WithAMalformedRepositoryId_IsUsage()
    {
        Directory.CreateDirectory(_state);

        var result = await HostHarness.RunAsync(AgentHost.RunAsync, "receipts", "--state", _state, "--repository", "xyz");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("32 hex", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task UpgradeFormat_WithoutItsArguments_IsUsage()
    {
        var result = await HostHarness.RunAsync(AgentHost.RunAsync, "upgrade-format", "--state", _state);

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("--set", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task UpgradeFormat_WithNoServiceListening_IsRefusedRatherThanWrittenBehindItsBack()
    {
        // Unlike `reattribute` there is no file-direct arm, and the refusal
        // says why: the upgrade takes effect by dropping the set's open
        // archive, which only the running service can do (ADR-0066).
        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync, "upgrade-format", "--state", _state, "--set", "docs");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("no service is listening", result.Error, StringComparison.Ordinal);
        Assert.Contains("Start the service", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Reattribute_WithoutItsArguments_IsUsage()
    {
        var result = await HostHarness.RunAsync(AgentHost.RunAsync, "reattribute", "--state", _state, "--to", "ABCDEF");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("--repository", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Reattribute_AReplicaItsOwnerCanClaim_IsRefusedByNameEvenOffline()
    {
        // The file-direct arm refuses exactly what the service refuses: the
        // override is for the replica the passphrase cannot reach, and a
        // recorded claim key means the passphrase can.
        var gone = Pin("gone");
        var rebuilt = Pin("rebuilt");
        Assert.IsTrue(ReplicaOwnerStore.Open(_state).TryAttribute(
            RepositoryIdHex, gone.Fingerprint, claimPublicKey: new string('e', 64)));

        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync, "reattribute", "--state", _state,
            "--repository", RepositoryIdHex, "--to", rebuilt.Fingerprint);

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("claim", result.Error, StringComparison.Ordinal);
        Assert.AreEqual(gone.Fingerprint, ReplicaOwnerStore.Open(_state).Find(RepositoryIdHex)!.Fingerprint);
    }

    [TestMethod]
    public async Task Reattribute_AnAmbiguousPrefix_IsRefusedAndChangesNothing()
    {
        var gone = Pin("gone");
        Assert.IsTrue(ReplicaOwnerStore.Open(_state).TryAttribute(RepositoryIdHex, gone.Fingerprint));

        // A second pairing whose fingerprint begins as the first does —
        // minted until one does, which a 32-symbol alphabet makes cheap —
        // so that one symbol names two devices.
        var grants = PeerGrantStore.Open(_state);
        for (var attempt = 0; ; attempt++)
        {
            Assert.IsTrue(attempt < 500, "no keypair shared the first fingerprint symbol in 500 tries");
            using var twin = PeerKeypair.Generate();
            if (twin.Identity.Fingerprint[0] == gone.Fingerprint[0])
            {
                grants.Pin(new PeerGrant(twin.Identity, "twin", PeerRole.StoresHere, PeerTerms.None, 1_722_600_000_000));
                break;
            }
        }

        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync, "reattribute", "--state", _state,
            "--repository", RepositoryIdHex, "--to", gone.Fingerprint[..1]);

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("more of the fingerprint", result.Error, StringComparison.Ordinal);
        Assert.AreEqual(gone.Fingerprint, ReplicaOwnerStore.Open(_state).Find(RepositoryIdHex)!.Fingerprint);
    }

    private static DeletionReceipt Receipt() => new(
        SessionId: Enumerable.Repeat((byte)0xAB, DeletionReceipt.SessionIdLength).ToArray(),
        RepositoryId: Enumerable.Repeat((byte)0x01, ReplicationOffer.RepositoryIdLength).ToArray(),
        CommanderPublicKey: Enumerable.Repeat((byte)0xC0, PeerIdentity.KeyLength).ToArray(),
        IssuedAtUnixMilliseconds: 1_000,
        FloorGenerations: 3,
        ReclaimPublicKey: ReadOnlyMemory<byte>.Empty,
        PageDigests: [new byte[DeletionReceipt.DigestLength]],
        DeletedCount: 1,
        Deleted: ["snapshots/aa/bb/gone"],
        NotHeld: 2);

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }
}
