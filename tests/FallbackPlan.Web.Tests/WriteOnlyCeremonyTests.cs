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

    [TestMethod]
    public async Task Gate_AnUnusableRecipientKey_IsNamedNotBlamedOnTheArchive()
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

        // A service publishing a non-hex or wrong-length recipient key is
        // ITS OWN finding — pre-fix, the non-hex case was swallowed as "no
        // archive could answer" and the short case escaped untyped.
        foreach (var unusable in new[] { "this is not hex", "abcd" })
        {
            var answer = await ConsoleRestoreGate.VerifyAsync(
                _archives, stateDirectory: null, PassphraseText, unusable, CancellationToken.None);
            Assert.AreEqual(ConsoleRestoreGate.GateOutcome.Unavailable, answer.Outcome, unusable);
            Assert.Contains("grant-recipient", answer.Detail!, StringComparison.Ordinal);
        }

        // No recipient at all still verifies — it just mints no grant.
        var verified = await ConsoleRestoreGate.VerifyAsync(
            _archives, stateDirectory: null, PassphraseText, grantRecipientHex: null, CancellationToken.None);
        Assert.AreEqual(ConsoleRestoreGate.GateOutcome.Verified, verified.Outcome);
        Assert.IsNull(verified.GrantEnvelope);
    }

    [TestMethod]
    public async Task BuildProvisionEnvelope_BadRecipientOrDamagedDescriptor_AnswersUnavailableByName()
    {
        var recipientHex = Convert.ToHexStringLower(
            ContentSealing.PublicKeyOf(Enumerable.Repeat((byte)0x71, 32).ToArray()));

        // A bad recipient never starts an Argon2 derivation — pre-fix this
        // was an unguarded FormatException out of the ceremony endpoint.
        var badRecipient = await ConsoleRestoreGate.BuildProvisionEnvelopeAsync(
            _archives, _setId, PassphraseText, "definitely-not-hex", CancellationToken.None);
        Assert.AreEqual(ConsoleRestoreGate.GateOutcome.Unavailable, badRecipient.Outcome);
        Assert.Contains("grant-recipient", badRecipient.Detail!, StringComparison.Ordinal);

        // A staging archive whose descriptor no longer reads is named, not
        // crashed over — adoption cannot proceed against damage.
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

        var descriptorPath = Path.Combine(archive, "repository-format");
        var bytes = await File.ReadAllBytesAsync(descriptorPath);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(descriptorPath, bytes);

        var damaged = await ConsoleRestoreGate.BuildProvisionEnvelopeAsync(
            _archives, _setId, PassphraseText, recipientHex, CancellationToken.None);
        Assert.AreEqual(ConsoleRestoreGate.GateOutcome.Unavailable, damaged.Outcome);
        Assert.Contains("does not read", damaged.Detail!, StringComparison.Ordinal);
    }
}
