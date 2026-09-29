using System.Net;
using System.Text.Json;
using FallbackPlan.Api;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// Contract 1.44's settings reaching the console both ways: the names the
/// settings card and the destination form read off the relay, and the
/// camelCase the console sends arriving at the service as the command it
/// means, the clearing spellings included.
/// </summary>
/// <remarks>
/// Same rationale as <see cref="StatusRelayNamesTests"/>: the relay's names
/// are derived from C# property names, so a rename compiles and quietly
/// renders <c>undefined</c> — or, inbound, quietly sends null, which on this
/// surface means "keep what the file says" and so fails silently twice.
/// </remarks>
[TestClass]
public sealed class SettingsRelayNamesTests
{
    [TestMethod]
    public async Task GetServiceSettings_OverTheRelay_CarriesTheNamesTheCardReads()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ =>
            new ServiceSettingsResult("22:00-06:00", "40 MiB/s", 3, EffectiveMaxConcurrentBackups: 2);

        using var request = harness.Command("""{"command":"get_service_settings"}""");
        using var response = await harness.Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.AreEqual("22:00-06:00", root.GetProperty("backgroundWindow").GetString());
        Assert.AreEqual("40 MiB/s", root.GetProperty("backgroundReadLimit").GetString());
        Assert.AreEqual(3, root.GetProperty("maxConcurrentBackups").GetInt32());
        Assert.AreEqual(2, root.GetProperty("effectiveMaxConcurrentBackups").GetInt32());
    }

    [TestMethod]
    public async Task UpdateServiceSettings_FromTheCard_ReachesTheServiceWithItsClearingSpellings()
    {
        // The card sends every field it shows: text, an empty text that
        // clears, and a zero width that clears. Each must arrive as sent —
        // arriving as null would keep what the operator just removed.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new ConfigurationChangeResult(["done"]);

        using var request = harness.Command(
            """{"command":"update_service_settings","backgroundWindow":"22:00-06:00","backgroundReadLimit":"","maxConcurrentBackups":0}""");
        using var response = await harness.Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.IsInstanceOfType<UpdateServiceSettingsCommand>(
            Assert.ContainsSingle(harness.Clients.Client.Received), out var command);
        Assert.AreEqual("22:00-06:00", command.BackgroundWindow);
        Assert.AreEqual("", command.BackgroundReadLimit);
        Assert.AreEqual(0, command.MaxConcurrentBackups);
    }

    [TestMethod]
    public async Task ListDestinations_OverTheRelay_CarriesTheLimitAndTheCadence()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new DestinationsResult(
        [
            new DestinationDescriptor(
                "dest-1", "vault", "local-path", "/backups", null, null,
                TransferLimit: "2 MiB/s", DrillIntervalDays: 7),
        ]);

        using var request = harness.Command("""{"command":"list_destinations"}""");
        using var response = await harness.Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var vault = body.RootElement.GetProperty("destinations")[0];
        Assert.AreEqual("2 MiB/s", vault.GetProperty("transferLimit").GetString());
        Assert.AreEqual(7, vault.GetProperty("drillIntervalDays").GetInt32());
    }
}
