using System.Text.Json;
using FallbackPlan.Protocol;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The <c>receipts</c> verb, the reader for deletion receipts (FR-GC-008;
/// [ADR-0063](../../docs/adr/0063-deletion-receipts.md)). File-direct and
/// read-only, so it needs no service and no peer; what it has to get right
/// is refusing to reassure. A path that holds nothing because it was
/// mistyped is not "no deletions on record", and every fact it prints comes
/// from the signed bytes, never from the envelope around them.
/// </summary>
[TestClass]
public sealed class ReceiptsVerbValidationTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "fbp-receipts-verb", Guid.NewGuid().ToString("n"));

    [TestMethod]
    public async Task Receipts_WithoutAStateDirectory_IsUsage()
    {
        var result = await CliHarness.RunRawAsync("receipts");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("--state", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Receipts_OfAStateDirectoryThatDoesNotExist_IsRefusedByPathNotAnsweredEmpty()
    {
        var result = await CliHarness.RunRawAsync("receipts", "--state", _state);

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("no state directory", result.Error, StringComparison.Ordinal);
        Assert.Contains(_state, result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("no receipts", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Receipts_WithAMalformedRepositoryId_SaysWhatShapeItWanted()
    {
        Directory.CreateDirectory(_state);

        var result = await CliHarness.RunRawAsync("receipts", "--state", _state, "--repository", "not-hex");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("32 hex", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Receipts_WithNothingFiled_SaysSoInWords()
    {
        Directory.CreateDirectory(_state);

        var result = await CliHarness.RunRawAsync("receipts", "--state", _state);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.Contains("no receipts", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Receipts_AsJson_CarriesEveryAttestedFactFromTheSignedBytes()
    {
        using var spoke = PeerKeypair.Generate();
        var signed = Receipt().EncodeForSigning();
        var path = DeletionReceiptStore.Open(_state).File(
            DeletionReceiptRole.Commander, signed, spoke.Sign(signed), spoke.Identity, "docs", "friend");

        var result = await CliHarness.RunRawAsync("receipts", "--state", _state, "--json");

        Assert.AreEqual(0, result.ExitCode, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var entry = Assert.ContainsSingle(document.RootElement.EnumerateArray().ToList());
        Assert.IsTrue(entry.GetProperty("verified").GetBoolean());
        Assert.AreEqual("commander", entry.GetProperty("role").GetString());
        Assert.AreEqual("docs", entry.GetProperty("set").GetString());
        Assert.AreEqual("friend", entry.GetProperty("destination").GetString());
        Assert.AreEqual(spoke.Identity.Fingerprint, entry.GetProperty("signer_fingerprint").GetString());
        Assert.AreEqual(path, entry.GetProperty("path").GetString());

        var attested = entry.GetProperty("receipt");
        Assert.AreEqual(1, attested.GetProperty("deleted_count").GetInt32());
        Assert.AreEqual(
            "snapshots/aa/bb/gone",
            Assert.ContainsSingle(attested.GetProperty("deleted").EnumerateArray().ToList()).GetString());
        Assert.AreEqual(2, attested.GetProperty("not_held").GetInt32());
        Assert.AreEqual(3, attested.GetProperty("floor_generations").GetInt32());
        Assert.AreEqual(
            PeerIdentity.FromPublicKey(Receipt().CommanderPublicKey.Span).Fingerprint,
            attested.GetProperty("commander_fingerprint").GetString());
    }

    [TestMethod]
    public async Task Receipts_ListsBothKindsNewestFirst_AndKindNarrowsToOne()
    {
        // ADR-0064: one verb over both stores, interleaved by issue time, and
        // `--kind` narrows to one — the empty answer naming the kind asked for.
        using var peer = PeerKeypair.Generate();
        var deletion = Receipt().EncodeForSigning();
        DeletionReceiptStore.Open(_state).File(
            DeletionReceiptRole.Commander, deletion, peer.Sign(deletion), peer.Identity, "docs", "friend");
        var replication = Replication().EncodeForSigning();
        ReplicationReceiptStore.Open(_state).File(
            DeletionReceiptRole.Commander, replication, peer.Sign(replication), peer.Identity, "docs", "friend");

        var both = await CliHarness.RunRawAsync("receipts", "--state", _state, "--json");
        Assert.AreEqual(0, both.ExitCode, both.Error);
        using (var document = JsonDocument.Parse(both.Output))
        {
            var entries = document.RootElement.EnumerateArray().ToList();
            Assert.HasCount(2, entries);
            Assert.AreEqual("replication", entries[0].GetProperty("kind").GetString(), "the newer receipt comes first");
            Assert.AreEqual("deletion", entries[1].GetProperty("kind").GetString());
            Assert.AreEqual(2, entries[0].GetProperty("receipt").GetProperty("committed_count").GetInt32());
        }

        var one = await CliHarness.RunRawAsync("receipts", "--state", _state, "--kind", "deletion", "--json");
        Assert.AreEqual(0, one.ExitCode, one.Error);
        using (var document = JsonDocument.Parse(one.Output))
        {
            var entry = Assert.ContainsSingle(document.RootElement.EnumerateArray().ToList());
            Assert.AreEqual("deletion", entry.GetProperty("kind").GetString());
        }

        var text = await CliHarness.RunRawAsync("receipts", "--state", _state, "--kind", "replication");
        Assert.AreEqual(0, text.ExitCode, text.Error);
        Assert.Contains("replication", text.Output, StringComparison.Ordinal);
        Assert.Contains("2 object(s) this session", text.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("not held", text.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Receipts_WithALimitOfNone_IsRefusedByName()
    {
        // A receipt first, so the state directory exists: a mistyped path is
        // refused ahead of anything else, deliberately, and this case is
        // about the limit rather than about that rule.
        using var spoke = PeerKeypair.Generate();
        var signed = Receipt().EncodeForSigning();
        DeletionReceiptStore.Open(_state).File(
            DeletionReceiptRole.Commander, signed, spoke.Sign(signed), spoke.Identity, "docs", "friend");

        var result = await CliHarness.RunRawAsync("receipts", "--state", _state, "--limit", "0");

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains("--limit must be at least 1", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Receipts_WithALimit_ReadsOnlyTheNewest()
    {
        using var spoke = PeerKeypair.Generate();
        foreach (var issuedAt in new ulong[] { 1_000, 2_000, 3_000 })
        {
            var signed = Receipt(issuedAt).EncodeForSigning();
            DeletionReceiptStore.Open(_state).File(
                DeletionReceiptRole.Commander, signed, spoke.Sign(signed), spoke.Identity, "docs", "friend");
        }

        var result = await CliHarness.RunRawAsync("receipts", "--state", _state, "--limit", "2", "--json");

        Assert.AreEqual(0, result.ExitCode, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var entries = document.RootElement.EnumerateArray().ToList();
        Assert.HasCount(2, entries);
        Assert.AreEqual(
            3_000L, entries[0].GetProperty("receipt").GetProperty("issued_at").GetInt64(), "the newest first");
        Assert.AreEqual(2_000L, entries[1].GetProperty("receipt").GetProperty("issued_at").GetInt64());
    }

    [TestMethod]
    public async Task Receipts_WithAKindThatDoesNotExist_NamesTheTwoThatDo()
    {
        Directory.CreateDirectory(_state);

        var result = await CliHarness.RunRawAsync("receipts", "--state", _state, "--kind", "refund");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("deletion", result.Error, StringComparison.Ordinal);
        Assert.Contains("replication", result.Error, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }

    private static ReplicationReceipt Replication() => new(
        SessionId: Enumerable.Repeat((byte)0xCD, ReplicationReceipt.SessionIdLength).ToArray(),
        RepositoryId: Enumerable.Repeat((byte)0x01, ReplicationOffer.RepositoryIdLength).ToArray(),
        CommanderPublicKey: Enumerable.Repeat((byte)0xC0, PeerIdentity.KeyLength).ToArray(),
        IssuedAtUnixMilliseconds: 2_000,
        CommittedCount: 2,
        Committed: ["blobs/data/aa/one", "snapshots/aa/two"],
        HeldObjects: 7,
        HeldBytes: 1_234);

    private static DeletionReceipt Receipt(ulong issuedAt = 1_000) => new(
        SessionId: Enumerable.Repeat((byte)(issuedAt % 251), DeletionReceipt.SessionIdLength).ToArray(),
        RepositoryId: Enumerable.Repeat((byte)0x01, ReplicationOffer.RepositoryIdLength).ToArray(),
        CommanderPublicKey: Enumerable.Repeat((byte)0xC0, PeerIdentity.KeyLength).ToArray(),
        IssuedAtUnixMilliseconds: issuedAt,
        FloorGenerations: 3,
        ReclaimPublicKey: ReadOnlyMemory<byte>.Empty,
        PageDigests: [new byte[DeletionReceipt.DigestLength]],
        DeletedCount: 1,
        Deleted: ["snapshots/aa/bb/gone"],
        NotHeld: 2);
}
