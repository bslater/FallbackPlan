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
}
