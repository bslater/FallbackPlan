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
        Assert.DoesNotContain("no deletion receipts", result.Output, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("no deletion receipts", result.Output, StringComparison.OrdinalIgnoreCase);
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

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
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
}
