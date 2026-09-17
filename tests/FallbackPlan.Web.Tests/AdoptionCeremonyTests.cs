using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FallbackPlan.Api;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's adoption ceremony (ADR-0061 §5; FR-WOR-006, NFR-SEC-009 as
/// amended): the page discovers a destination's archives through the
/// service and adopts one through the console process, which derives
/// against the <em>discovered</em> archive's salt, proves the derivation
/// against the discovered sealing key before sending anything, and sends
/// the service a sealed provisioning envelope — never the passphrase. The
/// script's structure is held to the same shape: a discover button on
/// local-path destination rows only, the three actions, one endpoint.
/// </summary>
[TestClass]
public sealed class AdoptionCeremonyTests
{
    private const string PassphraseText = "the archive's own passphrase, typed again";
    private const string Destination = "vault";

    private static readonly string RepositoryId = new('c', 32);

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

    /// <summary>
    /// A discovered row whose sealing key really is <see cref="PassphraseText"/>'s
    /// derivation under a salt of its own — the facts the service would have
    /// read from the archive's descriptor.
    /// </summary>
    private static DiscoveredArchiveDescriptor DiscoveredRow(byte[] salt, Argon2Parameters parameters)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(passphrase, parameters, salt, KdfValidationMode.OpenRepository);
        return new DiscoveredArchiveDescriptor(
            RepositoryId, 2, 1_700_000_000_000, "fallbackplan-agent/0.1",
            Convert.ToHexStringLower(salt), parameters.MemoryKiB, parameters.Iterations, parameters.Parallelism,
            Convert.ToHexStringLower(authority.Credential.SealingPublicKey),
            SnapshotObjects: 3, HighestPublicationSequence: 12, OwnedBySet: null, SameInstallation: false);
    }

    [TestMethod]
    public async Task AdoptEndpoint_DerivesAgainstTheDiscoveredArchive_AndSendsASealedEnvelopeOnly()
    {
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var recipientHex = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(recipientScalar));
        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var parameters = RepositoryCreationSettings.Default.KdfParameters;
        var row = DiscoveredRow(salt, parameters);

        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => new ServiceDescriptionResult(
                "1.30", "test", "vm", "/state", false, 0, RestoreGrantRecipient: recipientHex),
            DiscoverArchivesCommand discover when discover.DestinationName == Destination =>
                new ArchivesDiscoveredResult(Destination, [row], []),
            AdoptArchiveCommand => new ArchiveAdoptedResult(
                new string('a', 32), "docs", RepositoryId, [new BackupRootDescriptor("/src")], [],
                "every 1h", [], [], 3, null, null, WriterIdentityResumed: true, AlreadyAdopted: false,
                Lines: ["adopted (fake)"]),
            _ => new AcknowledgedResult(),
        };

        // Without the loss acknowledgement nothing derives and nothing is sent.
        using (var refused = await harness.Http.SendAsync(Post(
            harness, "/api/adopt-archive",
            $$"""{"destinationName":"{{Destination}}","repositoryId":"{{RepositoryId}}","passphrase":"{{PassphraseText}}","acknowledged":false}""")))
        {
            Assert.AreEqual(HttpStatusCode.OK, refused.StatusCode);
            using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.AreEqual("refused", body.RootElement.GetProperty("outcome").GetString());
            Assert.IsEmpty(harness.Clients.Client.Received.OfType<AdoptArchiveCommand>());
        }

        using (var response = await harness.Http.SendAsync(Post(
            harness, "/api/adopt-archive",
            $$"""{"destinationName":"{{Destination}}","repositoryId":"{{RepositoryId}}","passphrase":"{{PassphraseText}}","acknowledged":true,"setName":"docs-again"}""")))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("adopted", body.RootElement.GetProperty("outcome").GetString());
            Assert.AreEqual("docs", body.RootElement.GetProperty("set").GetProperty("setName").GetString());
            Assert.IsTrue(body.RootElement.GetProperty("lines").GetArrayLength() > 0);
        }

        // The envelope was derived under the DISCOVERED salt, opens with the
        // recipient scalar, and carries that archive's own credential; the
        // override reached the command; the passphrase is in no command.
        var sent = harness.Clients.Client.Received.OfType<AdoptArchiveCommand>().Single();
        Assert.AreEqual(Destination, sent.DestinationName);
        Assert.AreEqual(RepositoryId, sent.RepositoryId);
        Assert.AreEqual("docs-again", sent.SetName);
        var (credential, sentSalt, sentParameters) = WriteOnlyProvisioning.OpenProvision(
            recipientScalar, Convert.FromHexString(sent.Envelope));
        using (credential)
        {
            Assert.IsTrue(sentSalt.AsSpan().SequenceEqual(salt), "the envelope must carry the archive's salt, not a fresh one");
            Assert.AreEqual(parameters.MemoryKiB, sentParameters.MemoryKiB);
            Assert.AreEqual(row.SealingPublicKey, Convert.ToHexStringLower(credential.SealingPublicKey));
        }

        Assert.IsTrue(
            harness.Clients.Client.Received.All(command =>
                command is DescribeServiceCommand or DiscoverArchivesCommand or AdoptArchiveCommand),
            "the ceremony speaks exactly three verbs");
    }

    [TestMethod]
    public async Task AdoptEndpoint_TheWrongPassphrase_IsCaughtWhereItWasTyped()
    {
        var recipientHex = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(RandomNumberGenerator.GetBytes(32)));
        var row = DiscoveredRow(RandomNumberGenerator.GetBytes(KekDerivation.SaltLength), RepositoryCreationSettings.Default.KdfParameters);

        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => new ServiceDescriptionResult(
                "1.30", "test", "vm", "/state", false, 0, RestoreGrantRecipient: recipientHex),
            DiscoverArchivesCommand => new ArchivesDiscoveredResult(Destination, [row], []),
            _ => new AcknowledgedResult(),
        };

        using var response = await harness.Http.SendAsync(Post(
            harness, "/api/adopt-archive",
            $$"""{"destinationName":"{{Destination}}","repositoryId":"{{RepositoryId}}","passphrase":"not that passphrase","acknowledged":true}"""));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("wrong", body.RootElement.GetProperty("outcome").GetString());
        Assert.IsEmpty(
            harness.Clients.Client.Received.OfType<AdoptArchiveCommand>(),
            "a derivation that does not reproduce the discovered sealing key sends the service nothing");

        // And an archive discovery does not list is refused before any derivation.
        using var unknown = await harness.Http.SendAsync(Post(
            harness, "/api/adopt-archive",
            $$"""{"destinationName":"{{Destination}}","repositoryId":"{{new string('e', 32)}}","passphrase":"{{PassphraseText}}","acknowledged":true}"""));
        using var unknownBody = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        Assert.AreEqual("unavailable", unknownBody.RootElement.GetProperty("outcome").GetString());
    }

    [TestMethod]
    public void Script_OffersDiscoveryOnLocalPathDestinationsOnly_AndDeclaresTheCeremony()
    {
        var script = SetupWizardScriptTests.AppJs();

        Assert.Contains("\"dest-discover\"(", script, StringComparison.Ordinal);
        Assert.Contains("\"dest-adopt\"(", script, StringComparison.Ordinal);
        Assert.Contains("async \"dest-adopt-go\"(", script, StringComparison.Ordinal);
        Assert.Contains("/api/adopt-archive", script, StringComparison.Ordinal);
        Assert.Contains("discover_archives", script, StringComparison.Ordinal);

        // The button rides on the destination row, and only a local path
        // gets one: a peer is refused by the service, and offering it would
        // be a button that only ever says no.
        var row = script.IndexOf("data-action=\"dest-discover\"", StringComparison.Ordinal);
        Assert.IsTrue(row >= 0, "the destinations table offers no discover button");
        var guard = script.LastIndexOf("destination.kind === \"local-path\"", row, StringComparison.Ordinal);
        Assert.IsTrue(guard >= 0 && row - guard < 200, "the discover button is not guarded by the destination's kind");
    }
}
