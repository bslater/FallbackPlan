using System.Net;
using System.Text.Json;
using FallbackPlan.Api;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// Contract 1.15's diagnostics verbs reaching the console's Diagnostics view
/// (ADR-0043 §6).
/// </summary>
/// <remarks>
/// <para>
/// The relay itself needs no server change — the console never enumerates
/// commands — so what these tests are really for is the <b>wire names</b>. The
/// console serialises with <c>JsonSerializerDefaults.Web</c>, which means the
/// property names <c>app.js</c> reads are camelCase forms <em>derived</em> from
/// C# property names rather than declared anywhere. Rename
/// <c>DiagnosticsResult.RingCapacity</c> and nothing fails to compile, no
/// analyzer complains, and a panel in the browser quietly renders
/// <c>undefined</c>.
/// </para>
/// <para>
/// So each name the view reads is asserted here, on the actual HTTP response.
/// </para>
/// </remarks>
[TestClass]
public sealed class DiagnosticsRelayTests
{
    [TestMethod]
    public async Task GetDiagnostics_OverTheRelay_CarriesEveryNameTheViewReads()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new DiagnosticsResult(
            DefaultLevel: "debug",
            CategoryLevels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["FallbackPlan.Repository"] = "trace",
            },
            DurableSink: true,
            RetainFiles: 5,
            MaximumFileBytes: 8 * 1024 * 1024,
            RingCapacity: 2048,
            OldestSequence: 12,
            NextSequence: 4096);

        using var request = harness.Command("""{"command":"get_diagnostics"}""");
        using var response = await harness.Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.IsInstanceOfType<GetDiagnosticsCommand>(
            Assert.ContainsSingle(harness.Clients.Client.Received), out _);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.AreEqual("diagnostics", root.GetProperty("result").GetString());
        Assert.AreEqual("debug", root.GetProperty("defaultLevel").GetString());
        Assert.AreEqual("trace", root.GetProperty("categoryLevels").GetProperty("FallbackPlan.Repository").GetString());
        Assert.IsTrue(root.GetProperty("durableSink").GetBoolean());
        Assert.AreEqual(5, root.GetProperty("retainFiles").GetInt32());
        Assert.AreEqual(8 * 1024 * 1024, root.GetProperty("maximumFileBytes").GetInt64());
        Assert.AreEqual(2048, root.GetProperty("ringCapacity").GetInt32());
        Assert.AreEqual(12, root.GetProperty("oldestSequence").GetInt64());
        Assert.AreEqual(4096, root.GetProperty("nextSequence").GetInt64());
    }

    [TestMethod]
    public async Task GetDiagnostics_OverTheRelay_CarriesNoPath()
    {
        // The result has no field for one, and this asserts that stays true of
        // the bytes rather than only of the type: a service exposing no
        // filesystem access to clients (T-16) must not leak a path through the
        // one verb whose subject is a file.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new DiagnosticsResult(
            "information", new Dictionary<string, string>(StringComparer.Ordinal),
            true, 5, 1024, 64, 0, 0);

        using var request = harness.Command("""{"command":"get_diagnostics"}""");
        using var response = await harness.Http.SendAsync(request);

        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\", text, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ReadLog_OverTheRelay_CarriesEveryNameTheTableReads()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new LogRecordsResult(
            [
                new LogRecordDescriptor(
                    Sequence: 41,
                    TimestampUnixMilliseconds: 1_755_000_000_000,
                    Level: "warning",
                    EventId: 2101,
                    Category: "FallbackPlan.Repository",
                    Message: "a blob was skipped",
                    ExceptionType: "System.IO.IOException",
                    ExceptionMessage: "the device is full"),
            ],
            NextSequence: 42,
            Dropped: true);

        using var request = harness.Command(
            """{"command":"read_log","sinceSequence":7,"maxRecords":200,"minimumLevel":"warning"}""");
        using var response = await harness.Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        // The command's own names have to survive the trip too, or the view
        // pages from the wrong cursor.
        Assert.IsInstanceOfType<ReadLogCommand>(
            Assert.ContainsSingle(harness.Clients.Client.Received), out var command);
        Assert.AreEqual(7, command.SinceSequence);
        Assert.AreEqual(200, command.MaxRecords);
        Assert.AreEqual("warning", command.MinimumLevel);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.AreEqual("log_records", root.GetProperty("result").GetString());
        Assert.AreEqual(42, root.GetProperty("nextSequence").GetInt64());
        Assert.IsTrue(root.GetProperty("dropped").GetBoolean());

        var record = Assert.ContainsSingle(root.GetProperty("records").EnumerateArray().ToArray());
        Assert.AreEqual(41, record.GetProperty("sequence").GetInt64());
        Assert.AreEqual(1_755_000_000_000, record.GetProperty("timestampUnixMilliseconds").GetInt64());
        Assert.AreEqual("warning", record.GetProperty("level").GetString());
        Assert.AreEqual(2101, record.GetProperty("eventId").GetInt32());
        Assert.AreEqual("FallbackPlan.Repository", record.GetProperty("category").GetString());
        Assert.AreEqual("a blob was skipped", record.GetProperty("message").GetString());
        Assert.AreEqual("System.IO.IOException", record.GetProperty("exceptionType").GetString());
        Assert.AreEqual("the device is full", record.GetProperty("exceptionMessage").GetString());
    }

    [TestMethod]
    public async Task SetLogLevel_OverTheRelay_ReachesTheServiceWithBothFields()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new ConfigurationChangeResult(["done"]);

        using var request = harness.Command(
            """{"command":"set_log_level","category":"FallbackPlan.Repository","level":"trace"}""");
        using var response = await harness.Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.IsInstanceOfType<SetLogLevelCommand>(
            Assert.ContainsSingle(harness.Clients.Client.Received), out var command);
        Assert.AreEqual("FallbackPlan.Repository", command.Category);
        Assert.AreEqual("trace", command.Level);
    }

    [TestMethod]
    public async Task SetLogLevel_WithNoCategory_ReachesTheServiceAsTheDefault()
    {
        // The view sends null when the category box is empty, and null is what
        // the handler reads as "the level that applies where nothing matches".
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new ConfigurationChangeResult(["done"]);

        using var request = harness.Command(
            """{"command":"set_log_level","category":null,"level":"debug"}""");
        using var response = await harness.Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.IsInstanceOfType<SetLogLevelCommand>(
            Assert.ContainsSingle(harness.Clients.Client.Received), out var command);
        Assert.IsNull(command.Category);
        Assert.AreEqual("debug", command.Level);
    }
}
