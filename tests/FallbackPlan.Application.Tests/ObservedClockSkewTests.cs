namespace FallbackPlan.Application.Tests;

/// <summary>
/// How far this machine's clock stood from a peer's, read from the one
/// exchange that carries the peer's clock — a replication receipt's signed
/// <c>issued_at</c> (peer-protocol 03 §3.5) — and which reading a capture
/// records (NFR-TIME-002, ADR-0077).
/// </summary>
/// <remarks>
/// <para>
/// The sign is the reference's clock minus this one's, so a positive value is
/// a clock behind its peer. The reading is bracketed: this side's clock just
/// before it sends <c>ReplicationComplete</c> and just after the
/// acknowledgement arrives, and the peer stamps its receipt between the two.
/// The midpoint is the estimate and half the round trip its uncertainty.
/// </para>
/// <para>
/// A capture cannot record the reading its own run makes, because the
/// manifest is signed before the peer answers. It records the freshest one
/// the ledger holds from its set's destinations, and none older than a day:
/// a clock can be set right, or wrong, between a reading and a capture, and a
/// stale reading recorded as if current would misdirect the diagnosis it
/// exists for.
/// </para>
/// </remarks>
[TestClass]
public sealed class ObservedClockSkewTests
{
    private const ulong Now = 1_785_000_000_000;
    private static readonly string SetId = new('a', 32);

    private string _state = null!;

    [TestInitialize]
    public void Initialize()
    {
        _state = Directory.CreateTempSubdirectory("fbp-clock-skew-").FullName;
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_state, recursive: true);
    }

    [TestMethod]
    public void AReading_IsThePeersStampMinusTheMidpointOfTheExchange()
    {
        var reading = ClockObservation.FromExchange(sentAt: Now, receivedAt: Now + 200, issuedAt: Now + 100 + 10_800_000);

        Assert.IsNotNull(reading);
        Assert.AreEqual(10_800_000L, reading.SkewMilliseconds, "a peer three hours ahead reads as this clock three hours behind");
        Assert.AreEqual(200UL, reading.RoundTripMilliseconds);
        Assert.AreEqual(Now + 200, reading.ObservedAt, "a reading is dated by when it was complete");
    }

    [TestMethod]
    public void APeerBehind_ReadsNegative()
    {
        var reading = ClockObservation.FromExchange(sentAt: Now, receivedAt: Now + 40, issuedAt: Now + 20 - 90_000);

        Assert.AreEqual(-90_000L, reading!.SkewMilliseconds);
    }

    [TestMethod]
    public void AnExchangeTheLocalClockRanBackwardsAcross_SaysNothing()
    {
        // A bracket that ends before it began cannot hold a stamp: the local
        // clock was stepped mid-exchange, and the reading would be the step.
        Assert.IsNull(ClockObservation.FromExchange(sentAt: Now, receivedAt: Now - 1, issuedAt: Now));
    }

    [TestMethod]
    public void ACapture_RecordsTheFreshestReadingOfItsSet()
    {
        var older = new ClockObservation(5_000, Now - 3_600_000, 50);
        var fresher = new ClockObservation(7_000, Now - 60_000, 900);

        Assert.AreEqual(fresher, ClockObservation.ForCapture([older, null, fresher], Now));
    }

    [TestMethod]
    public void ACapture_BetweenReadingsOfOneMoment_PrefersTheTighterBracket()
    {
        var loose = new ClockObservation(5_000, Now - 60_000, 900);
        var tight = new ClockObservation(4_800, Now - 60_000, 30);

        Assert.AreEqual(tight, ClockObservation.ForCapture([loose, tight], Now));
    }

    [TestMethod]
    public void ACapture_RecordsNoReadingOlderThanADay()
    {
        var stale = new ClockObservation(5_000, Now - (ulong)TimeSpan.FromDays(1).TotalMilliseconds - 1, 50);
        var edge = new ClockObservation(6_000, Now - (ulong)TimeSpan.FromDays(1).TotalMilliseconds, 50);

        Assert.IsNull(ClockObservation.ForCapture([stale], Now));
        Assert.AreEqual(edge, ClockObservation.ForCapture([stale, edge], Now), "a day old is still a day");
        Assert.AreEqual(TimeSpan.FromDays(1), ClockObservation.MaximumAge);
    }

    [TestMethod]
    public void ACapture_RecordsNoReadingDatedInItsFuture()
    {
        // The local clock went back since the reading was taken, so the
        // reading describes a clock that is no longer this one.
        Assert.IsNull(ClockObservation.ForCapture([new ClockObservation(5_000, Now + 1, 50)], Now));
    }

    [TestMethod]
    public void ACaptureWithNoReading_RecordsNone() =>
        Assert.IsNull(ClockObservation.ForCapture([null, null], Now));

    [TestMethod]
    public void TheLedger_KeepsAPairsLatestReading_AtSchemaEight()
    {
        var store = DestinationSyncStore.Open(_state);
        store.RecordClockObservation(SetId, "friend", new ClockObservation(10_800_000, Now, 12));
        store.RecordClockObservation(SetId, "friend", new ClockObservation(10_799_000, Now + 5_000, 8));

        // Written at a schema that carries the reading: eight, or a later one.
        var text = File.ReadAllText(Path.Combine(_state, "destinations.json"));
        using (var written = System.Text.Json.JsonDocument.Parse(text))
        {
            Assert.IsGreaterThanOrEqualTo(8, written.RootElement.GetProperty("schema_version").GetInt32());
        }

        Assert.Contains("\"clock_skew_ms\": 10799000", text, StringComparison.Ordinal);

        var row = DestinationSyncStore.Open(_state).Find(SetId, "friend")!;
        Assert.AreEqual(new ClockObservation(10_799_000, Now + 5_000, 8), row.Clock);
        Assert.AreEqual(DestinationSyncState.Behind, row.State, "a reading is not a sync, and says nothing of one");
    }

    [TestMethod]
    public void AReading_KeepsTheRestOfTheRow()
    {
        var store = DestinationSyncStore.Open(_state);
        store.RecordSuccess(SetId, "friend", objects: 7, nowUnixMilliseconds: Now, syncedSequence: 42);

        store.RecordClockObservation(SetId, "friend", new ClockObservation(-2_000, Now + 1, 3));

        var row = store.Find(SetId, "friend")!;
        Assert.AreEqual(DestinationSyncState.InSync, row.State);
        Assert.AreEqual(42UL, row.SyncedSequence);
        Assert.AreEqual(-2_000L, row.Clock!.SkewMilliseconds);
    }

    [TestMethod]
    public void ASchemaSevenLedger_LoadsWithNoReading()
    {
        File.WriteAllText(Path.Combine(_state, "destinations.json"), """
            { "schema_version": 7, "destinations": [
                { "set": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "destination": "friend", "state": "InSync",
                  "last_attempt_at": 1000, "synced_sequence": 42 } ] }
            """);

        var row = DestinationSyncStore.Open(_state).Find(SetId, "friend")!;

        Assert.AreEqual(42UL, row.SyncedSequence);
        Assert.IsNull(row.Clock, "a pair nothing has read a peer's clock through has no reading, not a zero one");
    }
}
