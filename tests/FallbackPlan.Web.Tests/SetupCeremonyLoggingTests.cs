using System.Net;
using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's trace tier (event ids 4110, 4111, 4113 and 4114; FR-SVC-010)
/// exists so a stranded setup ceremony can be diagnosed from the console's own
/// stderr. Per ADR-0043's "a call site is not a logger", each record is
/// asserted arriving through real requests — and, because the ceremony's
/// request carries the passphrase, every ceremony case also asserts it
/// reached no record in any form.
/// </summary>
/// <remarks>
/// Ported from the line merged in at 9fb5ab6, and removed at 38c4df3 with the
/// declarations it covered once those had lost their call sites. That line's
/// recovery-kit case is re-aimed at the setup endpoint's own unavailable
/// outcome: the kit's endpoint went with the kit (ADR-0060), but the situation
/// the case was written for, a ceremony that stops with nothing but a toast to
/// say why, is the setup's too.
/// </remarks>
[TestClass]
public sealed class SetupCeremonyLoggingTests
{
    private const string StrongPassphrase = "Vault-Door-19-Kestrel-Harbour";

    private const string DeviceIdHex = "00112233445566778899aabbccddeeff";

    private const string SetupBody =
        $$"""{"passphrase":"{{StrongPassphrase}}","confirmation":"{{StrongPassphrase}}","acknowledged":true}""";

    private static HttpRequestMessage Post(ConsoleHarness harness, string path, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", harness.Auth.Token);
        return request;
    }

    private static void AssertNoPassphraseAnywhere(RecordingLogger log)
    {
        foreach (var record in log.Records)
        {
            Assert.DoesNotContain(StrongPassphrase, record.Message, StringComparison.OrdinalIgnoreCase);
            foreach (var value in record.Values)
            {
                Assert.DoesNotContain(
                    StrongPassphrase, value.Value?.ToString() ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [TestMethod]
    public async Task Setup_Provisioned_LeavesTheOutcomeRecordAndNeverThePassphrase()
    {
        var log = new RecordingLogger();
        var recipient = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(RandomNumberGenerator.GetBytes(32)));
        await using var harness = await ConsoleHarness.StartAsync(log);
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => new ServiceDescriptionResult(
                "1.14", "test", "vm", "/state", false, 0,
                ArchivesRoot: "/archives", RestoreGrantRecipient: recipient, SetupState: "setup_required",
                DeviceId: DeviceIdHex),
            ProvisionInstallationCommand => new ConfigurationChangeResult(["set up (fake)"]),
            _ => new AcknowledgedResult(),
        };

        using var response = await harness.Http.SendAsync(Post(harness, "/api/setup", SetupBody));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var outcome = log.Records.SingleOrDefault(record => record.EventId == 4111);
        Assert.IsNotNull(outcome, "the setup outcome record (4111) is the point of the trace tier");
        Assert.AreEqual(LogLabel.Of("provisioned"), outcome.Value("Outcome"));

        Assert.IsTrue(
            log.Records.Any(record => record.EventId == 4110
                && Equals(record.Value("Endpoint"), LogLabel.Of("/api/setup"))
                && Equals(record.Value("StatusCode"), 200)),
            "the request line (4110) must bracket the ceremony");
        AssertNoPassphraseAnywhere(log);
    }

    [TestMethod]
    public async Task Setup_WhenTheServiceCannotDescribeItself_StillLeavesTheOutcomeRecord()
    {
        // The failure the trace tier exists for: the page shows a toast and
        // nothing else says why. The fake answers the describe with the wrong
        // result shape, so the endpoint classifies "unavailable"
        // deterministically, after every check that precedes a derivation.
        var log = new RecordingLogger();
        await using var harness = await ConsoleHarness.StartAsync(log);
        harness.Clients.Client.Respond = _ => new AcknowledgedResult();

        using var response = await harness.Http.SendAsync(Post(harness, "/api/setup", SetupBody));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var outcome = log.Records.SingleOrDefault(record => record.EventId == 4111);
        Assert.IsNotNull(outcome, "the setup outcome record (4111) must say how the request was classified");
        Assert.AreEqual(LogLabel.Of("unavailable"), outcome.Value("Outcome"));
        AssertNoPassphraseAnywhere(log);
    }

    [TestMethod]
    public async Task CommandRelay_NamesTheCommandAndTheResultItRelayed()
    {
        var log = new RecordingLogger();
        await using var harness = await ConsoleHarness.StartAsync(log);
        harness.Clients.Client.Respond = _ => new AcknowledgedResult();

        using var response = await harness.Http.SendAsync(
            harness.Command("""{"command":"describe_service"}"""));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var relayed = log.Records.SingleOrDefault(record => record.EventId == 4113);
        Assert.IsNotNull(relayed, "the relay record (4113) is what pairs a page action with a service answer");
        Assert.AreEqual(LogLabel.Of(nameof(DescribeServiceCommand)), relayed.Value("Command"));
        Assert.AreEqual(LogLabel.Of(nameof(AcknowledgedResult)), relayed.Value("Result"));
        Assert.IsTrue(
            log.Records.Any(record => record.EventId == 4110
                && Equals(record.Value("Endpoint"), LogLabel.Of("/api/command"))));
    }

    [TestMethod]
    public async Task StaticAssets_ReportWhatWasServed_SoAStaleBuildIsDiagnosable()
    {
        // app.js is embedded at build time and the page traces its own asset
        // version; this record is the server half of that staleness bracket.
        var log = new RecordingLogger();
        await using var harness = await ConsoleHarness.StartAsync(log);

        using var response = await harness.Http.GetAsync(new Uri("/app.js", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var served = log.Records.SingleOrDefault(record => record.EventId == 4114);
        Assert.IsNotNull(served);
        Assert.AreEqual(LogLabel.Of("/app.js"), served.Value("Path"));
        Assert.IsGreaterThan(0, (int)served.Value("ByteCount")!);
    }
}
