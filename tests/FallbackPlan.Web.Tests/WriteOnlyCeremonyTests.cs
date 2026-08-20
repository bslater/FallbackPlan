using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FallbackPlan.Api;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's write-only ceremonies (ADR-0042 §4, §10; NFR-SEC-009 as
/// amended): the setup endpoint derives in the console process and sends the
/// service a sealed provisioning envelope — never the passphrase; the wizard
/// gate verifies a v2 archive by derive-and-compare and mints the sealed
/// restore grant the source open carries; and both refuse honestly — wrong
/// passphrase, missing acknowledgement, foreign recipient.
/// </summary>
[TestClass]
public sealed class WriteOnlyCeremonyTests : IDisposable
{
    private const string PassphraseText = "the console ceremony passphrase";

    private readonly string _archives =
        Path.Combine(Path.GetTempPath(), "fbp-wo-web", Guid.NewGuid().ToString("n")[..12]);

    private readonly string _setId = new('a', 32);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_archives, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or DirectoryNotFoundException)
        {
        }
    }

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

    [TestMethod]
    public async Task ProvisionEndpoint_CreatesASealedEnvelopeTheServiceCanOpen_AndThePassphraseNeverCrosses()
    {
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var recipientHex = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(recipientScalar));

        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => new ServiceDescriptionResult(
                "1.12", "test", "vm", "/state", false, 0,
                ArchivesRoot: _archives, RestoreGrantRecipient: recipientHex),
            ListBackupSetsCommand => new BackupSetsResult(
                [new BackupSetDescriptor(_setId, "docs", "/src", null, [], [], ["vault"])]),
            ProvisionWriteOnlySetCommand => new ConfigurationChangeResult(["provisioned (fake)"]),
            _ => new AcknowledgedResult(),
        };

        // Without the loss acknowledgement nothing derives and nothing is sent.
        using (var refused = await harness.Http.SendAsync(Post(
            harness, "/api/provision-write-only",
            $$"""{"setName":"docs","passphrase":"{{PassphraseText}}","acknowledged":false}""")))
        {
            Assert.AreEqual(HttpStatusCode.OK, refused.StatusCode);
            using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.AreEqual("refused", body.RootElement.GetProperty("outcome").GetString());
            Assert.IsEmpty(
                harness.Clients.Client.Received.OfType<ProvisionWriteOnlySetCommand>(),
                "a refused acknowledgement must send the service nothing");
        }

        using (var response = await harness.Http.SendAsync(Post(
            harness, "/api/provision-write-only",
            $$"""{"setName":"docs","passphrase":"{{PassphraseText}}","acknowledged":true}""")))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("provisioned", body.RootElement.GetProperty("outcome").GetString());
        }

        // The envelope the service received opens with the recipient scalar
        // and carries the write bundle — and the passphrase is in no command.
        var sent = harness.Clients.Client.Received.OfType<ProvisionWriteOnlySetCommand>().Single();
        var (credential, salt, parameters) = WriteOnlyProvisioning.OpenProvision(
            recipientScalar, Convert.FromHexString(sent.Envelope));
        using (credential)
        {
            Assert.HasCount(KekDerivation.SaltLength, salt);

            using var passphrase = Passphrase.Create(PassphraseText);
            using var expected = WriteOnlyDerivation.Derive(
                passphrase, parameters, salt, KdfValidationMode.OpenRepository);
            Assert.IsTrue(
                expected.Credential.SealingPublicKey.SequenceEqual(credential.SealingPublicKey),
                "the sealed bundle is the passphrase's own derivation");
        }

        Assert.IsTrue(
            harness.Clients.Client.Received.All(command =>
                command is DescribeServiceCommand or ListBackupSetsCommand or ProvisionWriteOnlySetCommand),
            "the ceremony speaks exactly three verbs");
    }

    [TestMethod]
    public async Task Gate_AgainstAWriteOnlyArchive_VerifiesByDerivationAndMintsTheSealedGrant()
    {
        var archive = Path.Combine(_archives, _setId);
        Directory.CreateDirectory(archive);
        var store = new LocalFileSystemObjectStore(archive);
        using (var passphrase = Passphrase.Create(PassphraseText))
        {
            var (repository, authority) = await RepositoryLifecycle.CreateWriteOnlyAsync(
                store, passphrase, RepositoryCreationSettings.Default, 1_722_700_000_000UL, CancellationToken.None);
            repository.Dispose();
            authority.Dispose();
        }

        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var recipientHex = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(recipientScalar));

        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new ServiceDescriptionResult(
            "1.12", "test", "vm", "/state", false, 0,
            ArchivesRoot: _archives, RestoreGrantRecipient: recipientHex);

        using (var response = await harness.Http.SendAsync(Post(
            harness, "/api/restore-gate", $$"""{"passphrase":"{{PassphraseText}}"}""")))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("verified", body.RootElement.GetProperty("outcome").GetString());

            // The grant is real: it opens with the recipient scalar and
            // reproduces the archive's sealing public key.
            var envelope = body.RootElement.GetProperty("envelope").GetString();
            Assert.IsNotNull(envelope);
            var granted = WriteOnlyProvisioning.OpenGrant(recipientScalar, Convert.FromHexString(envelope));
            var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, CancellationToken.None);
            Assert.IsTrue(
                ContentSealing.PublicKeyOf(granted).AsSpan().SequenceEqual(descriptor.SealingPublicKey.Span),
                "the sealed grant carries this repository's derived scalar");
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(granted);
        }

        using (var response = await harness.Http.SendAsync(Post(
            harness, "/api/restore-gate", """{"passphrase":"not this repository's"}""")))
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("wrong", body.RootElement.GetProperty("outcome").GetString());
            Assert.IsTrue(
                body.RootElement.GetProperty("envelope").ValueKind is JsonValueKind.Null,
                "a wrong passphrase mints nothing");
        }

        Assert.IsTrue(
            harness.Clients.Client.Received.All(command => command is DescribeServiceCommand),
            "the gate may ask the service only where the archives live and what to seal to");
    }
}
