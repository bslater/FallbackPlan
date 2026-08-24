using System.Net;
using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's trace tier (event ids 4110–4114) exists so a stranded setup
/// ceremony can be diagnosed from the console's own stderr. Per ADR-0043's
/// "a call site is not a logger", each record is asserted arriving through
/// real requests — and, because three of these endpoints carry a passphrase,
/// every test also asserts the passphrase reached no record in any form.
/// </summary>
[TestClass]
public sealed class SetupCeremonyLoggingTests
{
    private const string StrongPassphrase = "Vault-Door-19-Kestrel-Harbour";

    private const string DeviceIdHex = "00112233445566778899aabbccddeeff";

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

        using var response = await harness.Http.SendAsync(Post(harness, "/api/setup",
            $$"""{"passphrase":"{{StrongPassphrase}}","confirmation":"{{StrongPassphrase}}","acknowledged":true}"""));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var outcome = log.Records.SingleOrDefault(record => record.EventId == 4111);
        Assert.IsNotNull(outcome, "the setup outcome record (4111) is the point of the trace tier");
        Assert.AreEqual("provisioned", outcome.Value("Outcome"));
        Assert.IsTrue((bool)outcome.Value("KitIncluded")!);

        Assert.IsTrue(
            log.Records.Any(record => record.EventId == 4110
                && Equals(record.Value("Endpoint"), "/api/setup")),
            "the request line (4110) must bracket the ceremony");
        AssertNoPassphraseAnywhere(log);
    }

    [TestMethod]
    public async Task RecoveryKit_WhenTheServiceCannotDescribeItself_StillLeavesTheOutcomeRecord()
    {
        // The rebuild path's failure is exactly the situation the trace tier
        // exists for: the page shows a warning toast and nothing else says
        // why. The fake answers the describe with the wrong result shape, so
        // the endpoint classifies "unavailable" deterministically.
        var log = new RecordingLogger();
        await using var harness = await ConsoleHarness.StartAsync(log);
        harness.Clients.Client.Respond = _ => new AcknowledgedResult();

        using var response = await harness.Http.SendAsync(Post(harness, "/api/recovery-kit",
            $$"""{"passphrase":"{{StrongPassphrase}}"}"""));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var outcome = log.Records.SingleOrDefault(record => record.EventId == 4112);
        Assert.IsNotNull(outcome, "the rebuild outcome record (4112) must say how the request was classified");
        Assert.AreEqual("unavailable", outcome.Value("Outcome"));
        Assert.IsFalse((bool)outcome.Value("KitIncluded")!);
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
        Assert.AreEqual(nameof(DescribeServiceCommand), relayed.Value("Command"));
        Assert.AreEqual(nameof(AcknowledgedResult), relayed.Value("Result"));
        Assert.IsTrue(
            log.Records.Any(record => record.EventId == 4110
                && Equals(record.Value("Endpoint"), "/api/command")));
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
        Assert.AreEqual("/app.js", served.Value("Path"));
        Assert.IsGreaterThan(0, (int)served.Value("ByteCount")!);
    }
}
