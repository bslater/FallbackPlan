using System.Net;
using System.Text.Json;
using FallbackPlan.Api;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// Contract 1.19's full-backup facts reaching the console's status matrix
/// (ADR-0047 §§5–6): the destination rows <c>app.js</c> renders as "awaiting
/// full backup" instead of a bare "behind".
/// </summary>
/// <remarks>
/// Same rationale as <c>DiagnosticsRelayTests</c>: the console serialises
/// with web defaults, so the camelCase names the view reads are derived from
/// C# property names rather than declared anywhere — a rename compiles
/// cleanly and quietly renders <c>undefined</c>. Each name the status view
/// reads off a destination row is therefore asserted on the HTTP bytes.
/// </remarks>
[TestClass]
public sealed class StatusRelayNamesTests
{
    [TestMethod]
    public async Task GetStatus_OverTheRelay_CarriesTheDestinationRowNamesTheViewReads()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new StatusResult(
            MachineName: "hub",
            Sets:
            [
                new BackupSetStatusDescriptor(
                    "docs", new BackupSetStatus(ProtectionState.Degraded, Verification: null, Warnings: []), NextRun: null,
                    [
                        new DestinationStatusDescriptor(
                            "vault-b", "local-path", "behind", LastSuccessAt: null,
                            Detail: "awaiting full backup", "same-machine", "unproven",
                            BaselineCompletedAt: null, NeedsFull: true),
                        new DestinationStatusDescriptor(
                            "vault-a", "local-path", "in-sync", LastSuccessAt: 9_000, Detail: null,
                            "same-machine", "proven", BaselineCompletedAt: 5_000, NeedsFull: false),
                    ]),
            ],
            ObservedAt: 10_000,
            Notices: []);

        using var request = harness.Command("""{"command":"get_status"}""");
        using var response = await harness.Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var destinations = body.RootElement
            .GetProperty("sets")[0]
            .GetProperty("destinations");

        var owed = destinations[0];
        Assert.IsTrue(owed.GetProperty("needsFull").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, owed.GetProperty("baselineCompletedAt").ValueKind);

        var seeded = destinations[1];
        Assert.IsFalse(seeded.GetProperty("needsFull").GetBoolean());
        Assert.AreEqual(5_000, seeded.GetProperty("baselineCompletedAt").GetInt64());
    }

    [TestMethod]
    public async Task GetStatus_OverTheRelay_CarriesTheDeepSweepNamesTheViewReads()
    {
        // Contract 1.46 (ADR-0035 Amendment 3). sweepLabel() reads six
        // camelCase names off the row's deepSweep, and tells a row with no
        // sweep from one whose sweep has not run by the object being null.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new StatusResult(
            MachineName: "hub",
            Sets:
            [
                new BackupSetStatusDescriptor(
                    "docs", new BackupSetStatus(ProtectionState.Protected, Verification: null, Warnings: []), NextRun: null,
                    [
                        new DestinationStatusDescriptor(
                            "vault", "local-path", "in-sync", LastSuccessAt: 9_000, Detail: null,
                            "other-drive", "proven",
                            DeepSweep: new DeepSweepDescriptor(
                                IntervalDays: 7, CircuitClosedAt: 5_000, ReadThisCircuit: 3, LastReadAt: 8_000,
                                Stalls: 1, StalledOn: "blobs/data/ab/abcdef")),
                        new DestinationStatusDescriptor(
                            "cloud", "s3", "not-supported", LastSuccessAt: null, Detail: null,
                            "other-site", "unproven"),
                    ]),
            ],
            ObservedAt: 10_000,
            Notices: []);

        using var request = harness.Command("""{"command":"get_status"}""");
        using var response = await harness.Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var destinations = body.RootElement.GetProperty("sets")[0].GetProperty("destinations");

        var sweep = destinations[0].GetProperty("deepSweep");
        Assert.AreEqual(7, sweep.GetProperty("intervalDays").GetInt32());
        Assert.AreEqual(5_000, sweep.GetProperty("circuitClosedAt").GetInt64());
        Assert.AreEqual(3, sweep.GetProperty("readThisCircuit").GetInt32());
        Assert.AreEqual(8_000, sweep.GetProperty("lastReadAt").GetInt64());
        Assert.AreEqual(1, sweep.GetProperty("stalls").GetInt32());
        Assert.AreEqual("blobs/data/ab/abcdef", sweep.GetProperty("stalledOn").GetString());

        Assert.AreEqual(JsonValueKind.Null, destinations[1].GetProperty("deepSweep").ValueKind);
    }

    [TestMethod]
    public async Task GetStatus_OverTheRelay_CarriesTheBackgroundWindowNamesTheViewReads()
    {
        // Contract 1.39 (ADR-0069). windowNote() reads three camelCase names
        // off this object; a rename compiles cleanly and quietly renders the
        // clause as "undefined".
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new StatusResult(
            MachineName: "hub",
            Sets: [],
            ObservedAt: 10_000,
            Notices: [],
            BackgroundWindow: new BackgroundWindowDescriptor("22:00-06:00", Open: false, ChangesAt: 20_000));

        using var request = harness.Command("""{"command":"get_status"}""");
        using var response = await harness.Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var window = body.RootElement.GetProperty("backgroundWindow");
        Assert.AreEqual("22:00-06:00", window.GetProperty("text").GetString());
        Assert.IsFalse(window.GetProperty("open").GetBoolean());
        Assert.AreEqual(20_000, window.GetProperty("changesAt").GetInt64());
    }

    [TestMethod]
    public async Task GetStatus_WithNoWindow_RelaysNullRatherThanOmittingIt()
    {
        // The console tests one thing — is there a window — so the absence
        // has to arrive as a value it can test, not as a missing property
        // that reads the same as a typo.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new StatusResult("hub", [], 10_000, []);

        using var request = harness.Command("""{"command":"get_status"}""");
        using var response = await harness.Http.SendAsync(request);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(
            JsonValueKind.Null,
            body.RootElement.GetProperty("backgroundWindow").ValueKind);
    }

    [TestMethod]
    public async Task GetStatus_OverTheRelay_CarriesTheBackgroundLimitNamesTheViewReads()
    {
        // Contract 1.43 (NFR-PERF-013). limitsNote() reads these camelCase
        // names off the object; a rename compiles cleanly and quietly renders
        // the clause as "undefined".
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new StatusResult(
            MachineName: "hub",
            Sets: [],
            ObservedAt: 10_000,
            Notices: [],
            BackgroundLimits: new BackgroundLimitsDescriptor(
                new ByteRateDescriptor("40 MiB/s", 41_943_040),
                [new DestinationTransferLimitDescriptor("friend", "2 MiB/s", 2_097_152)]));

        using var request = harness.Command("""{"command":"get_status"}""");
        using var response = await harness.Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var limits = body.RootElement.GetProperty("backgroundLimits");
        Assert.AreEqual("40 MiB/s", limits.GetProperty("readLimit").GetProperty("text").GetString());
        Assert.AreEqual(41_943_040, limits.GetProperty("readLimit").GetProperty("bytesPerSecond").GetInt64());
        var friend = limits.GetProperty("transferLimits")[0];
        Assert.AreEqual("friend", friend.GetProperty("destinationName").GetString());
        Assert.AreEqual("2 MiB/s", friend.GetProperty("text").GetString());
        Assert.AreEqual(2_097_152, friend.GetProperty("bytesPerSecond").GetInt64());
    }

    [TestMethod]
    public async Task GetStatus_WithNoLimits_RelaysNullRatherThanOmittingIt()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new StatusResult("hub", [], 10_000, []);

        using var request = harness.Command("""{"command":"get_status"}""");
        using var response = await harness.Http.SendAsync(request);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(
            JsonValueKind.Null,
            body.RootElement.GetProperty("backgroundLimits").ValueKind);
    }
}
