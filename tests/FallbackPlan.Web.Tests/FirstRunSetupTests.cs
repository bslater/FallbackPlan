using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FallbackPlan.Api;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's first-run setup ceremony (ADR-0044; FR-SVC-011,
/// NFR-SEC-011): the acknowledgement is collected before anything derives,
/// the two entries must match, a weak passphrase is refused with reasons,
/// and what reaches the service is a sealed envelope rather than the
/// passphrase.
/// </summary>
/// <remarks>
/// The ceremony ends at the passphrase (ADR-0060): nothing is handed back
/// to save, and there is no second step to resume at.
/// </remarks>
[TestClass]
public sealed class FirstRunSetupTests
{
    private const string StrongPassphrase = "Vault-Door-19-Kestrel-Harbour";

    /// <summary>The device identity the service describes itself with.</summary>
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

    private static void Describes(ConsoleHarness harness, string recipientHex, string setupState) =>
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => new ServiceDescriptionResult(
                "1.14", "test", "vm", "/state", false, 0,
                ArchivesRoot: "/archives", RestoreGrantRecipient: recipientHex, SetupState: setupState,
                DeviceId: DeviceIdHex),
            ProvisionInstallationCommand => new ConfigurationChangeResult(["set up (fake)"]),
            _ => new AcknowledgedResult(),
        };

    private static async Task<JsonDocument> SetupAsync(ConsoleHarness harness, string json)
    {
        using var response = await harness.Http.SendAsync(Post(harness, "/api/setup", json));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Setup_WithoutTheAcknowledgement_DerivesNothingAndSendsNothing()
    {
        // Consent before the work, exactly as the write-only ceremony does:
        // there is no recovery path to offer afterwards, so the order is the
        // decision and not a detail of the handler.
        var recipient = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(RandomNumberGenerator.GetBytes(32)));
        await using var harness = await ConsoleHarness.StartAsync();
        Describes(harness, recipient, "setup_required");

        using var body = await SetupAsync(harness,
            $$"""{"passphrase":"{{StrongPassphrase}}","confirmation":"{{StrongPassphrase}}","acknowledged":false}""");

        Assert.AreEqual("refused", body.RootElement.GetProperty("outcome").GetString());
        Assert.Contains(
            "master key", body.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.IsEmpty(
            harness.Clients.Client.Received.OfType<ProvisionInstallationCommand>(),
            "a refused acknowledgement must send the service nothing");
    }

    [TestMethod]
    public async Task Setup_EntriesThatDoNotMatch_AreRefusedBeforeAnythingDerives()
    {
        // The one thing that cannot be fixed later is a typo in a passphrase
        // entered exactly once.
        var recipient = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(RandomNumberGenerator.GetBytes(32)));
        await using var harness = await ConsoleHarness.StartAsync();
        Describes(harness, recipient, "setup_required");

        using var body = await SetupAsync(harness,
            $$"""{"passphrase":"{{StrongPassphrase}}","confirmation":"Vault-Door-19-Kestrel-Harbor","acknowledged":true}""");

        Assert.AreEqual("refused", body.RootElement.GetProperty("outcome").GetString());
        Assert.Contains("do not match", body.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<ProvisionInstallationCommand>());
    }

    [TestMethod]
    public async Task Setup_AWeakPassphrase_IsRefusedWithReasonsAndNothingIsSent()
    {
        var recipient = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(RandomNumberGenerator.GetBytes(32)));
        await using var harness = await ConsoleHarness.StartAsync();
        Describes(harness, recipient, "setup_required");

        using var body = await SetupAsync(harness,
            """{"passphrase":"abcabcabcabc","confirmation":"abcabcabcabc","acknowledged":true}""");

        Assert.AreEqual("weak", body.RootElement.GetProperty("outcome").GetString());
        Assert.IsNotEmpty(
            body.RootElement.GetProperty("findings").EnumerateArray().ToList(),
            "a refusal that does not say why leaves the operator guessing at a screen they cannot skip");
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<ProvisionInstallationCommand>());
    }

    [TestMethod]
    public async Task Setup_ALongPassphraseMissingTheComposition_IsWeakAndNamesTheRules()
    {
        // Length alone stopped being enough (ADR-0044 §6 as amended): the
        // refusal's findings name the missing uppercase, digits and special
        // character so the operator fixes the checklist in one pass.
        var recipient = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(RandomNumberGenerator.GetBytes(32)));
        await using var harness = await ConsoleHarness.StartAsync();
        Describes(harness, recipient, "setup_required");

        using var body = await SetupAsync(harness,
            """{"passphrase":"twentylowercasechars","confirmation":"twentylowercasechars","acknowledged":true}""");

        Assert.AreEqual("weak", body.RootElement.GetProperty("outcome").GetString());
        var findings = string.Join(" | ", body.RootElement.GetProperty("findings")
            .EnumerateArray().Select(finding => finding.GetString()));
        Assert.Contains("uppercase", findings, StringComparison.Ordinal);
        Assert.Contains("two digits", findings, StringComparison.Ordinal);
        Assert.Contains("special character", findings, StringComparison.Ordinal);
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<ProvisionInstallationCommand>());
    }

    [TestMethod]
    public async Task PasswordCheck_AnswersEachRuleAndAsksTheServiceNothing()
    {
        // The account form's checklist speaks the same policy the service
        // enforces (FR-USR-001 as amended) — one implementation, one verdict,
        // and both polarities proven on the bytes.
        await using var harness = await ConsoleHarness.StartAsync();

        foreach (var (candidate, acceptable, named) in new (string, bool, string?)[]
                 {
                     ("Owner-Pass-19!", true, null),
                     ("Aa12!Aa12", false, "10 characters"),          // 9 — below the floor
                     ("long-enough-42!", false, "uppercase"),
                     ("Long-Enough-Here!", false, "two digits"),
                     ("LongEnough42x9", false, "special character"),
                 })
        {
            using var response = await harness.Http.SendAsync(Post(
                harness, "/api/password-check", $$"""{"candidate":"{{candidate}}"}"""));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.AreEqual(
                acceptable, body.RootElement.GetProperty("acceptable").GetBoolean(), $"'{candidate}'");
            if (named is not null)
            {
                Assert.Contains(named, string.Join(" | ", body.RootElement.GetProperty("findings")
                    .EnumerateArray().Select(finding => finding.GetString())), StringComparison.Ordinal);
            }
        }

        Assert.IsEmpty(harness.Clients.Client.Received, "the checklist asks the service nothing");
    }

    [TestMethod]
    public async Task PasswordCheck_WithoutTheConsoleToken_IsRefused()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/password-check", UriKind.Relative))
        {
            Content = new StringContent(
                """{"candidate":"Owner-Pass-19!"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Setup_AcceptedPassphrase_SendsASealedEnvelopeAndNeverThePassphrase()
    {
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var recipient = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(recipientScalar));
        await using var harness = await ConsoleHarness.StartAsync();
        Describes(harness, recipient, "setup_required");

        using (var body = await SetupAsync(harness,
            $$"""{"passphrase":"{{StrongPassphrase}}","confirmation":"{{StrongPassphrase}}","acknowledged":true}"""))
        {
            Assert.AreEqual("provisioned", body.RootElement.GetProperty("outcome").GetString());
        }

        // The envelope opens with the recipient scalar and carries the
        // passphrase's own derivation — and the passphrase is in no command.
        var sent = harness.Clients.Client.Received.OfType<ProvisionInstallationCommand>().Single();
        var (credential, salt, parameters) = WriteOnlyProvisioning.OpenProvision(
            recipientScalar, Convert.FromHexString(sent.Envelope));
        using (credential)
        {
            Assert.HasCount(KekDerivation.SaltLength, salt);

            using var passphrase = Passphrase.Create(StrongPassphrase);
            using var expected = WriteOnlyDerivation.Derive(
                passphrase, parameters, salt, KdfValidationMode.OpenRepository);
            Assert.IsTrue(
                expected.Credential.SealingPublicKey.SequenceEqual(credential.SealingPublicKey),
                "the sealed bundle is the passphrase's own derivation");
        }

        Assert.IsTrue(
            harness.Clients.Client.Received.All(command =>
                command is DescribeServiceCommand or ProvisionInstallationCommand),
            "the ceremony speaks exactly two verbs");

        // And hands nothing back to keep: the passphrase is the whole
        // recovery credential (ADR-0060), so the answer carries no kit.
        Assert.IsFalse(
            harness.Clients.Client.Received.Any(command => command.GetType().Name.Contains("Kit", StringComparison.Ordinal)),
            "no verb about a kit exists to be spoken");
    }

    [TestMethod]
    public async Task Setup_AServiceRefusal_IsRelayedRatherThanReshaped()
    {
        var recipient = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(RandomNumberGenerator.GetBytes(32)));
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => new ServiceDescriptionResult(
                "1.14", "test", "vm", "/state", false, 0,
                RestoreGrantRecipient: recipient, SetupState: "ready", DeviceId: DeviceIdHex),
            ProvisionInstallationCommand => new ServiceError(
                ServiceErrorReason.Refused, "This installation is already set up."),
            _ => new AcknowledgedResult(),
        };

        using var body = await SetupAsync(harness,
            $$"""{"passphrase":"{{StrongPassphrase}}","confirmation":"{{StrongPassphrase}}","acknowledged":true}""");

        Assert.AreEqual("refused", body.RootElement.GetProperty("outcome").GetString());
        Assert.Contains(
            "already set up", body.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Strength_TheMeter_ScoresWithoutTellingTheService()
    {
        // One implementation of the policy, so the page can never say
        // "strong" about something the submit will refuse.
        await using var harness = await ConsoleHarness.StartAsync();
        Describes(harness, Convert.ToHexStringLower(ContentSealing.PublicKeyOf(RandomNumberGenerator.GetBytes(32))),
            "setup_required");

        using (var response = await harness.Http.SendAsync(Post(
            harness, "/api/passphrase-strength", """{"candidate":"short"}""")))
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("too_short", body.RootElement.GetProperty("band").GetString());
            Assert.IsFalse(body.RootElement.GetProperty("acceptable").GetBoolean());
        }

        using (var response = await harness.Http.SendAsync(Post(
            harness, "/api/passphrase-strength", $$"""{"candidate":"{{StrongPassphrase}}"}""")))
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("strong", body.RootElement.GetProperty("band").GetString());
            Assert.IsTrue(body.RootElement.GetProperty("acceptable").GetBoolean());
        }

        Assert.IsEmpty(harness.Clients.Client.Received, "the meter asks the service nothing");
    }

    [TestMethod]
    public async Task SetupAndStrength_WithoutTheConsoleToken_AreRefused()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        foreach (var path in new[] { "/api/setup", "/api/passphrase-strength" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
            using var response = await harness.Http.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, path);
        }
    }
}
