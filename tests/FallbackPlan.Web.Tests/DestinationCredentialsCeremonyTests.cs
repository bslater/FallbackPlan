using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// Storing an S3-compatible destination's access key, or an Azure Blob
/// destination's account key or shared access signature, from the console
/// (FR-DEST-005, ADR-0091, ADR-0093): the secret is typed into the page and
/// sealed in the console's own process to the service's recipient key, for
/// the one destination (and key id) it was typed for, and only the envelope
/// reaches the service (NFR-SEC-009). The person's session is resumed first, so the change
/// is theirs (ADR-0045). A request missing a part is refused here and the
/// service is sent nothing.
/// </summary>
[TestClass]
public sealed class DestinationCredentialsCeremonyTests
{
    private const string Secret = "fbp/console+secret=key/0123456789abcdef";

    [TestMethod]
    public async Task DestinationCredentials_SealsTheSecretInTheConsole_AndOnlyTheEnvelopeCrosses()
    {
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = Service(recipientScalar);

        using (var response = await harness.Http.SendAsync(
            Post(harness, new { destinationName = "cloud", accessKeyId = "FBPKEYID0001", secretAccessKey = Secret },
                session: "the browser's session")))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("stored", body.RootElement.GetProperty("outcome").GetString());
            Assert.AreEqual("stored (fake)", body.RootElement.GetProperty("lines")[0].GetString());
        }

        var received = harness.Clients.Client.Received;
        Assert.IsInstanceOfType<ResumeSessionCommand>(received[0], "the person's session first, so the change is theirs");
        var sent = Assert.ContainsSingle(received.OfType<SetDestinationCredentialsCommand>());
        Assert.AreEqual("cloud", sent.DestinationName);
        Assert.AreEqual("FBPKEYID0001", sent.AccessKeyId);
        Assert.AreEqual(
            Secret,
            WriteOnlyProvisioning.OpenAccessKeySecret(
                recipientScalar, Convert.FromHexString(sent.Envelope), "cloud", "FBPKEYID0001"));
        Assert.IsFalse(
            received.Any(command => JsonSerializer.Serialize(command, FrameCodec.SerializerOptions)
                .Contains("console+secret", StringComparison.Ordinal)),
            "the secret reaches no command the service is sent");
    }

    [TestMethod]
    public async Task DestinationCredentials_AnAccountKey_IsSealedInTheConsoleForItsKind_AndCrossesWithNoKeyId()
    {
        // ADR-0093: an Azure Blob container's account key is sealed the same
        // way, under its own purpose, and names no key id: the account is in
        // the destination's declaration already.
        const string accountKey = "BwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyAhIiMkJSYnKCkqKywtLi8wMTIzNDU2Nzg5Ojs8PT4/QEFCQ0RFRg==";
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = Service(recipientScalar);

        using (var response = await harness.Http.SendAsync(
            Post(harness, new { destinationName = "cloud", credentialKind = "shared-key", secret = accountKey })))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("stored", body.RootElement.GetProperty("outcome").GetString());
        }

        var sent = Assert.ContainsSingle(harness.Clients.Client.Received.OfType<SetDestinationCredentialsCommand>());
        Assert.AreEqual("shared-key", sent.CredentialKind);
        Assert.IsNull(sent.AccessKeyId);
        Assert.AreEqual(accountKey, WriteOnlyProvisioning.OpenAccountKey(recipientScalar, Convert.FromHexString(sent.Envelope), "cloud"));
        Assert.IsFalse(
            harness.Clients.Client.Received.Any(command => JsonSerializer.Serialize(command, FrameCodec.SerializerOptions)
                .Contains("BwgJCgsM", StringComparison.Ordinal)),
            "the account key reaches no command the service is sent");
    }

    [TestMethod]
    public async Task DestinationCredentials_ASharedAccessSignature_IsSealedInTheConsoleForItsKind()
    {
        const string token = "sv=2024-11-04&sr=c&sp=racwdl&se=2099-12-31T00%3A00%3A00Z&sig=AbC%2Bd%2Fe%3D";
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = Service(recipientScalar);

        using (var response = await harness.Http.SendAsync(
            Post(harness, new { destinationName = "cloud", credentialKind = "sas", secret = token })))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var sent = Assert.ContainsSingle(harness.Clients.Client.Received.OfType<SetDestinationCredentialsCommand>());
        Assert.AreEqual("sas", sent.CredentialKind);
        Assert.AreEqual(token, WriteOnlyProvisioning.OpenSharedAccessSignature(recipientScalar, Convert.FromHexString(sent.Envelope), "cloud"));
    }

    [TestMethod]
    public async Task DestinationCredentials_AKindThatCannotBe_OrAKeyIdItCannotTake_IsRefusedHere()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = Service(RandomNumberGenerator.GetBytes(32));

        foreach (var wrong in new object[]
        {
            new { destinationName = "cloud", credentialKind = "password", secret = Secret },
            new { destinationName = "cloud", credentialKind = "shared-key", accessKeyId = "FBPKEYID0001", secret = Secret },
            new { destinationName = "cloud", credentialKind = "sas" },
        })
        {
            using var response = await harness.Http.SendAsync(Post(harness, wrong));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.IsEmpty(harness.Clients.Client.Received);
    }

    [TestMethod]
    public async Task DestinationCredentials_ARequestMissingAPart_IsRefusedHere_AndTheServiceIsSentNothing()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = Service(RandomNumberGenerator.GetBytes(32));

        foreach (var partial in new object[]
        {
            new { destinationName = "cloud", accessKeyId = "FBPKEYID0001" },
            new { destinationName = "cloud", secretAccessKey = Secret },
            new { accessKeyId = "FBPKEYID0001", secretAccessKey = Secret },
        })
        {
            using var response = await harness.Http.SendAsync(Post(harness, partial));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.IsEmpty(harness.Clients.Client.Received);
    }

    [TestMethod]
    public async Task DestinationCredentials_TheServicesRefusal_IsSaidBackInItsOwnWords()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        var recipientHex = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(RandomNumberGenerator.GetBytes(32)));
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => new ServiceDescriptionResult(
                "1.60", "test", "vm", "/state", false, 0, RestoreGrantRecipient: recipientHex),
            SetDestinationCredentialsCommand => new ServiceError(
                ServiceErrorReason.InvalidArgument,
                "Destination 'vault' is a local-path destination, which signs no requests."),
            _ => new AcknowledgedResult(),
        };

        using var response = await harness.Http.SendAsync(
            Post(harness, new { destinationName = "vault", accessKeyId = "FBPKEYID0001", secretAccessKey = Secret }));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("refused", body.RootElement.GetProperty("outcome").GetString());
        Assert.Contains("signs no requests", body.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task DestinationCredentials_WithoutTheConsoleToken_IsRefused()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = Service(RandomNumberGenerator.GetBytes(32));

        using var request = Post(
            harness, new { destinationName = "cloud", accessKeyId = "FBPKEYID0001", secretAccessKey = Secret });
        request.Headers.Authorization = null;
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsEmpty(harness.Clients.Client.Received);
    }

    private static Func<ServiceCommand, ServiceResult> Service(byte[] recipientScalar)
    {
        var recipientHex = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(recipientScalar));
        return command => command switch
        {
            DescribeServiceCommand => new ServiceDescriptionResult(
                "1.60", "test", "vm", "/state", false, 0, RestoreGrantRecipient: recipientHex),
            ResumeSessionCommand => new AcknowledgedResult(),
            SetDestinationCredentialsCommand => new ConfigurationChangeResult(["stored (fake)"]),
            _ => new AcknowledgedResult(),
        };
    }

    private static HttpRequestMessage Post(ConsoleHarness harness, object payload, string? session = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/destination-credentials", UriKind.Relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", harness.Auth.Token);
        if (session is not null)
        {
            request.Headers.Add(WebConsoleHost.SessionHeader, session);
        }

        return request;
    }
}
