using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FallbackPlan.Api;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.RecoveryKit;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's first-run setup ceremony (ADR-0044; FR-SVC-011,
/// NFR-SEC-011): the acknowledgement is collected before anything derives,
/// the two entries must match, a weak passphrase is refused with reasons,
/// and what reaches the service is a sealed envelope rather than the
/// passphrase.
/// </summary>
[TestClass]
public sealed class FirstRunSetupTests
{
    private const string StrongPassphrase = "Vault-Door-19-Kestrel-Harbour";

    /// <summary>The device identity a kit records as its issuer (FR-KIT-001).</summary>
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
    }

    [TestMethod]
    public async Task Setup_Provisioned_HandsBackTheKitInBothFormsWithIdenticalContent()
    {
        // FR-KIT-003's machine/printable equivalence, asserted where the
        // operator actually receives them: the text is a rendering of the
        // very bytes offered as a file, not a second encoding of the same
        // idea.
        var recipient = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(RandomNumberGenerator.GetBytes(32)));
        await using var harness = await ConsoleHarness.StartAsync();
        Describes(harness, recipient, "setup_required");

        using var body = await SetupAsync(harness,
            $$"""{"passphrase":"{{StrongPassphrase}}","confirmation":"{{StrongPassphrase}}","acknowledged":true}""");

        Assert.AreEqual("provisioned", body.RootElement.GetProperty("outcome").GetString());
        var kit = body.RootElement.GetProperty("kit");

        var framed = Convert.FromBase64String(kit.GetProperty("machine").GetString()!);
        var text = kit.GetProperty("text").GetString()!;

        SequenceAssert.AreEqual(framed, RecoveryKitText.ParseToFramed(text));
        Assert.AreEqual(
            Convert.ToHexStringLower(SHA256.HashData(framed.AsSpan(0, framed.Length - 32))),
            kit.GetProperty("checksum").GetString(),
            "the checksum the page will confirm is the kit's own");

        var parsed = RecoveryKitCodec.Parse(framed);
        Assert.IsTrue(parsed.IsInstallationKit);
        Assert.IsNull(parsed.RepositoryId);
    }

    [TestMethod]
    public async Task Setup_TheKitItHandsBack_CarriesNoPassphraseAndNoKeyMaterial()
    {
        // The kit is what a person prints and leaves in a drawer. FR-KIT-002
        // at the bytes the console actually served.
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var recipient = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(recipientScalar));
        await using var harness = await ConsoleHarness.StartAsync();
        Describes(harness, recipient, "setup_required");

        using var body = await SetupAsync(harness,
            $$"""{"passphrase":"{{StrongPassphrase}}","confirmation":"{{StrongPassphrase}}","acknowledged":true}""");

        var framed = Convert.FromBase64String(
            body.RootElement.GetProperty("kit").GetProperty("machine").GetString()!);
        var text = body.RootElement.GetProperty("kit").GetProperty("text").GetString()!;

        // Recover the derivation the service was sent, and check the kit
        // against every private thing it produced.
        var sent = harness.Clients.Client.Received.OfType<ProvisionInstallationCommand>().Single();
        var (credential, salt, parameters) = WriteOnlyProvisioning.OpenProvision(
            recipientScalar, Convert.FromHexString(sent.Envelope));
        using (credential)
        {
            using var passphrase = Passphrase.Create(StrongPassphrase);
            using var authority = WriteOnlyDerivation.Derive(
                passphrase, parameters, salt, KdfValidationMode.OpenRepository);

            Assert.IsFalse(
                framed.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(StrongPassphrase)) >= 0,
                "the passphrase must not be in the kit");
            Assert.IsFalse(
                framed.AsSpan().IndexOf(authority.SealingPrivateKey) >= 0,
                "the sealing private key must not be in the kit");
            Assert.DoesNotContain(StrongPassphrase, text, StringComparison.Ordinal);

            Assert.IsTrue(
                framed.AsSpan().IndexOf(authority.Credential.SealingPublicKey) >= 0,
                "the public verifier belongs here — and its presence proves the scan is not vacuous");
        }
    }

    [TestMethod]
    public async Task Setup_TheKitAndTheEnvelope_ComeFromOneDerivation()
    {
        // Argon2id is deliberately expensive. Producing the two artefacts
        // from one root is the reason they are built together.
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var recipient = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(recipientScalar));
        await using var harness = await ConsoleHarness.StartAsync();
        Describes(harness, recipient, "setup_required");

        using var body = await SetupAsync(harness,
            $$"""{"passphrase":"{{StrongPassphrase}}","confirmation":"{{StrongPassphrase}}","acknowledged":true}""");

        var kit = RecoveryKitCodec.Parse(Convert.FromBase64String(
            body.RootElement.GetProperty("kit").GetProperty("machine").GetString()!));

        var sent = harness.Clients.Client.Received.OfType<ProvisionInstallationCommand>().Single();
        var (credential, salt, _) = WriteOnlyProvisioning.OpenProvision(
            recipientScalar, Convert.FromHexString(sent.Envelope));
        using (credential)
        {
            SequenceAssert.AreEqual(salt, kit.KdfSalt.ToArray());
            SequenceAssert.AreEqual(
                credential.SealingPublicKey.ToArray(), kit.SealingPublicKey.ToArray());
        }
    }

    [TestMethod]
    public async Task Setup_ARefusedPassphrase_HandsBackNoKitAtAll()
    {
        var recipient = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(RandomNumberGenerator.GetBytes(32)));
        await using var harness = await ConsoleHarness.StartAsync();
        Describes(harness, recipient, "setup_required");

        using var body = await SetupAsync(harness,
            """{"passphrase":"abcabcabcabc","confirmation":"abcabcabcabc","acknowledged":true}""");

        Assert.AreEqual("weak", body.RootElement.GetProperty("outcome").GetString());
        Assert.IsFalse(
            body.RootElement.TryGetProperty("kit", out var kit) && kit.ValueKind != JsonValueKind.Null,
            "a refused ceremony produces nothing to save");
    }

    [TestMethod]
    public async Task RecoveryKit_WithoutAnArchiveToRecoverTheSaltFrom_SaysSoRatherThanMintingASecondRoot()
    {
        // Minting a fresh salt here would produce a kit that opens nothing —
        // the worst possible outcome for an artefact whose entire job is to
        // still work in ten years.
        var recipient = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(RandomNumberGenerator.GetBytes(32)));
        await using var harness = await ConsoleHarness.StartAsync();
        Describes(harness, recipient, "kit_required");

        using var response = await harness.Http.SendAsync(Post(
            harness, "/api/recovery-kit", $$"""{"passphrase":"{{StrongPassphrase}}"}"""));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual("unavailable", body.RootElement.GetProperty("outcome").GetString());
        Assert.Contains(
            "cannot be recovered", body.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);
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

        foreach (var path in new[] { "/api/setup", "/api/passphrase-strength", "/api/recovery-kit" })
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
