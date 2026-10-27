using System.Text.Json;
using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// What each cloud destination's store was asked (FR-DEST-019, ADR-0111):
/// the requests sent to it, by operation, with the bytes they carried, over
/// the thirty days ending today — the window a store's bill is read in. These
/// pin the ledger those counts are kept in: by destination, by UTC day, kept
/// across a restart, and honest about when counting began.
/// </summary>
[TestClass]
public sealed class StoreRequestLedgerTests
{
    private const string CloudId = "c10dc10dc10dc10dc10dc10dc10dc10d";
    private const string OtherId = "0bbe0bbe0bbe0bbe0bbe0bbe0bbe0bbe";

    private static readonly DateTimeOffset Noon = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly Clock _clock = new(Noon);
    private string _state = null!;

    private string FilePath => Path.Combine(_state, "store-requests.json");

    [TestInitialize]
    public void SetUp()
    {
        _state = Path.Combine(Path.GetTempPath(), $"fbp-store-requests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_state);
    }

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    [TestMethod]
    public void Window_CountsEachOperation_AndTheBytesBothWays_ForItsOwnDestination()
    {
        using var ledger = Open();

        ledger.Record(CloudId, "put", bytesSent: 1_000, bytesReceived: 0);
        ledger.Record(CloudId, "put", bytesSent: 500, bytesReceived: 0);
        ledger.Record(CloudId, "get", bytesSent: 0, bytesReceived: 2_000);
        ledger.Record(CloudId, "head", bytesSent: 0, bytesReceived: 0);
        ledger.Record(CloudId, "list", bytesSent: 0, bytesReceived: 300);
        ledger.Record(CloudId, "delete", bytesSent: 0, bytesReceived: 0);
        ledger.Record(OtherId, "put", bytesSent: 7, bytesReceived: 0);

        var window = ledger.Window(CloudId);
        Assert.AreEqual(2, window.Puts);
        Assert.AreEqual(1, window.Gets);
        Assert.AreEqual(1, window.Heads);
        Assert.AreEqual(1, window.Lists);
        Assert.AreEqual(1, window.Deletes);
        Assert.AreEqual(1_500, window.BytesSent);
        Assert.AreEqual(2_300, window.BytesReceived);
        Assert.AreEqual(1, ledger.Window(OtherId).Puts, "each destination is counted apart, as each is billed apart");
    }

    [TestMethod]
    public void Window_OfADestinationNeverAsked_IsNothing()
    {
        using var ledger = Open();

        Assert.AreEqual(new StoreRequestWindow(0, 0, 0, 0, 0, 0, 0, ledger.Window(CloudId).Since), ledger.Window(CloudId));
    }

    [TestMethod]
    public void Record_AnOperationNoStoreBillsBy_CountsNothing()
    {
        using var ledger = Open();

        ledger.Record(CloudId, "copy", bytesSent: 10, bytesReceived: 10);

        var window = ledger.Window(CloudId);
        Assert.AreEqual(0, window.Puts + window.Gets + window.Heads + window.Lists + window.Deletes);
        Assert.AreEqual(0, window.BytesSent + window.BytesReceived);
    }

    [TestMethod]
    public void Window_IsTheThirtyUtcDaysEndingToday()
    {
        using var ledger = Open();
        _clock.Now = new DateTimeOffset(2026, 10, 1, 23, 59, 0, TimeSpan.Zero);
        ledger.Record(CloudId, "put", 1, 0);
        _clock.Now = new DateTimeOffset(2026, 10, 2, 0, 1, 0, TimeSpan.Zero);
        ledger.Record(CloudId, "get", 0, 1);

        // 1 October is the first day of the thirty that end on 30 October.
        _clock.Now = new DateTimeOffset(2026, 10, 30, 23, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(1, ledger.Window(CloudId).Puts);
        Assert.AreEqual(1, ledger.Window(CloudId).Gets);

        _clock.Now = new DateTimeOffset(2026, 10, 31, 0, 30, 0, TimeSpan.Zero);
        Assert.AreEqual(0, ledger.Window(CloudId).Puts, "a day thirty days gone has left the window");
        Assert.AreEqual(1, ledger.Window(CloudId).Gets, "the day after it has not");
    }

    [TestMethod]
    public void WhatWasCounted_OutlivesTheLedger_AndNoDayOlderThanTheWindowIsKept()
    {
        using (var ledger = Open())
        {
            _clock.Now = Noon.AddDays(-40);
            ledger.Record(CloudId, "put", 9, 0);
            _clock.Now = Noon;
            ledger.Record(CloudId, "put", 1_000, 0);
            ledger.Record(CloudId, "list", 0, 50);
        }

        using var reopened = Open();
        var window = reopened.Window(CloudId);
        Assert.AreEqual(1, window.Puts);
        Assert.AreEqual(1, window.Lists);
        Assert.AreEqual(1_000, window.BytesSent);

        using var file = JsonDocument.Parse(File.ReadAllText(FilePath));
        var days = file.RootElement.GetProperty("destinations")[0].GetProperty("days");
        Assert.AreEqual(1, days.GetArrayLength(), "a day the window can no longer read is not kept");
    }

    [TestMethod]
    public void Record_WritesAtMostOnceAnInterval_AndClosingWritesTheRest()
    {
        // Every request is counted, and a store is sent thousands an hour:
        // the file is written as the interval allows, not once a request.
        var ledger = StoreRequestLedger.Open(_state, _clock, writeInterval: TimeSpan.FromMinutes(1));
        ledger.Record(CloudId, "put", 1, 0);
        ledger.Record(CloudId, "put", 1, 0);

        using (var meanwhile = Open())
        {
            Assert.AreEqual(1, meanwhile.Window(CloudId).Puts, "the first request is written at once, the second waits");
        }

        _clock.Now = Noon.AddMinutes(1);
        ledger.Record(CloudId, "put", 1, 0);
        using (var later = Open())
        {
            Assert.AreEqual(3, later.Window(CloudId).Puts, "once the interval has passed, the next request writes all of them");
        }

        ledger.Record(CloudId, "get", 0, 1);
        ledger.Dispose();
        using var closed = Open();
        Assert.AreEqual(1, closed.Window(CloudId).Gets, "closing writes what the interval held back");
    }

    [TestMethod]
    public void Since_SaysWhenCountingBegan_UntilTheWindowHoldsNothingBeforeIt()
    {
        // A destination counted for a week has not been asked nothing in the
        // three weeks before: those requests were never counted.
        using (var ledger = Open())
        {
            Assert.AreEqual((ulong)Noon.ToUnixTimeMilliseconds(), ledger.Window(CloudId).Since);
            ledger.Record(CloudId, "put", 1, 0);
        }

        _clock.Now = Noon.AddDays(7);
        using (var reopened = Open())
        {
            Assert.AreEqual((ulong)Noon.ToUnixTimeMilliseconds(), reopened.Window(CloudId).Since, "a restart does not begin the count again");
        }

        _clock.Now = Noon.AddDays(30);
        using var month = Open();
        Assert.IsNull(month.Window(CloudId).Since, "a window that begins after counting began says nothing of it");
    }

    [TestMethod]
    public void Open_UnreadableGarbage_SetsTheFileAside_AndCountsAgainFromNow()
    {
        File.WriteAllText(FilePath, "{ not json");
        _clock.Now = Noon.AddDays(3);

        using var ledger = Open();

        Assert.IsTrue(File.Exists(FilePath + ".corrupt"), "the unreadable bytes must be preserved for inspection");
        Assert.AreEqual(0, ledger.Window(CloudId).Puts);
        Assert.AreEqual((ulong)Noon.AddDays(3).ToUnixTimeMilliseconds(), ledger.Window(CloudId).Since);
    }

    [TestMethod]
    public void Open_ANewerBuildsFile_SetsItAside_RatherThanMisreadIt()
    {
        File.WriteAllText(FilePath, "{\"schema_version\":99,\"began\":1,\"destinations\":[]}");

        using var ledger = Open();

        Assert.IsTrue(File.Exists(FilePath + ".corrupt"), "the newer file's bytes must be preserved");
        Assert.AreEqual((ulong)Noon.ToUnixTimeMilliseconds(), ledger.Window(CloudId).Since);
    }

    private StoreRequestLedger Open() => StoreRequestLedger.Open(_state, _clock, writeInterval: TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
