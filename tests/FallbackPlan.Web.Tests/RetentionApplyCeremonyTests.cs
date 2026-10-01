using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// Applying retention from the console (FR-GC-008, ADR-0055): the service
/// holds the key that publishes and not the key that authorises a deletion,
/// so the console derives the reclaim grant from the passphrase in its own
/// process, seals it to the service's recipient key and sends only the
/// sealed envelope (NFR-SEC-009 as amended). One grant per set, keyed by its
/// id, derived once per distinct salt, because a set adopted from a
/// destination keeps the salt it was born under. A passphrase that opens no
/// set is refused here and the service is sent nothing.
/// </summary>
[TestClass]
public sealed class RetentionApplyCeremonyTests
{
    private const string PassphraseText = "the console retention passphrase";

    private static readonly string DocsId = new('a', 32);
    private static readonly string PhotosId = new('b', 32);

    private static Argon2Parameters Parameters => RepositoryCreationSettings.Default.KdfParameters;

    [TestMethod]
    public async Task RetentionApply_DerivesAGrantPerSetInTheConsole_AndThePassphraseNeverCrosses()
    {
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var installationSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var photosSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        using var installation = Derive(PassphraseText, installationSalt);
        using var photos = Derive(PassphraseText, photosSalt);

        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = Service(recipientScalar, installationSalt, installation, photosSalt, photos);

        using (var response = await harness.Http.SendAsync(
            Post(harness, PassphraseText, session: "the browser's session")))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("applied", body.RootElement.GetProperty("outcome").GetString());
            Assert.AreEqual("docs: applied (fake)", body.RootElement.GetProperty("lines")[0].GetString());
        }

        var received = harness.Clients.Client.Received;
        var sent = Assert.ContainsSingle(received.OfType<RetentionCommand>());
        Assert.IsTrue(sent.Apply);
        Assert.IsNull(sent.ReclaimGrant, "grants travel per set, never as one for all");
        CollectionAssert.AreEquivalent(new[] { DocsId, PhotosId }, sent.ReclaimGrants!.Keys.ToList());

        // Each envelope opens with the recipient's scalar to the reclaim key
        // its own set's salt derives: docs under the installation's, photos
        // under the salt its archive was born under.
        CollectionAssert.AreEqual(
            installation.ReclaimKeySeed.ToArray(),
            WriteOnlyProvisioning.OpenGrant(recipientScalar, Convert.FromHexString(sent.ReclaimGrants[DocsId])));
        CollectionAssert.AreEqual(
            photos.ReclaimKeySeed.ToArray(),
            WriteOnlyProvisioning.OpenGrant(recipientScalar, Convert.FromHexString(sent.ReclaimGrants[PhotosId])));

        // The person's session is resumed first, so the deletion is theirs.
        Assert.IsInstanceOfType<ResumeSessionCommand>(received[0]);
        Assert.IsFalse(
            received.Any(command => JsonSerializer.Serialize(command, FrameCodec.SerializerOptions)
                .Contains(PassphraseText, StringComparison.Ordinal)),
            "the passphrase reaches no command the service is sent");
    }

    [TestMethod]
    public async Task RetentionApply_APassphraseThatOpensNoSet_IsRefusedHere_AndTheServiceIsSentNothing()
    {
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var installationSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var photosSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        using var installation = Derive(PassphraseText, installationSalt);
        using var photos = Derive(PassphraseText, photosSalt);

        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = Service(recipientScalar, installationSalt, installation, photosSalt, photos);

        using var response = await harness.Http.SendAsync(Post(harness, "not the passphrase of anything here"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("wrong", body.RootElement.GetProperty("outcome").GetString());
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<RetentionCommand>());
    }

    [TestMethod]
    public async Task RetentionApply_ASetAnotherPassphraseMade_IsLeftOutOfTheGrants_AndTheRestAreSent()
    {
        // An archive adopted under another passphrase is not this one's to
        // collect. The service says so for that set and collects the rest.
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var installationSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var photosSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        using var installation = Derive(PassphraseText, installationSalt);
        using var someoneElse = Derive("a passphrase from another installation", photosSalt);

        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = Service(recipientScalar, installationSalt, installation, photosSalt, someoneElse);

        using var response = await harness.Http.SendAsync(Post(harness, PassphraseText));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("applied", body.RootElement.GetProperty("outcome").GetString());

        var sent = Assert.ContainsSingle(harness.Clients.Client.Received.OfType<RetentionCommand>());
        Assert.AreEqual(DocsId, Assert.ContainsSingle(sent.ReclaimGrants!.Keys));
    }

    private static RepositoryReadAuthority Derive(string passphraseText, byte[] salt)
    {
        using var passphrase = Passphrase.Create(passphraseText);
        return WriteOnlyDerivation.Derive(passphrase, Parameters, salt, KdfValidationMode.OpenRepository);
    }

    /// <summary>
    /// A service whose installation and whose 'photos' set are described by
    /// the given derivations; 'docs' carries no facts of its own, as a set
    /// born on the installation does not.
    /// </summary>
    private static Func<ServiceCommand, ServiceResult> Service(
        byte[] recipientScalar, byte[] installationSalt, RepositoryReadAuthority installation,
        byte[] photosSalt, RepositoryReadAuthority photos)
    {
        var recipientHex = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(recipientScalar));
        var installationSealing = Convert.ToHexStringLower(installation.Credential.SealingPublicKey);
        var photosSealing = Convert.ToHexStringLower(photos.Credential.SealingPublicKey);
        return command => command switch
        {
            DescribeServiceCommand => new ServiceDescriptionResult(
                "1.50", "test", "vm", "/state", false, 0,
                RestoreGrantRecipient: recipientHex,
                KdfSalt: Convert.ToHexStringLower(installationSalt),
                KdfMemoryKib: Parameters.MemoryKiB,
                KdfIterations: Parameters.Iterations,
                KdfParallelism: Parameters.Parallelism,
                SealingPublicKey: installationSealing),
            ListBackupSetsCommand => new BackupSetsResult(
            [
                new BackupSetDescriptor(DocsId, "docs", "/src", null, [], [], ["vault"]),
                new BackupSetDescriptor(
                    PhotosId, "photos", "/photos", null, [], [], ["vault"],
                    KdfSalt: Convert.ToHexStringLower(photosSalt),
                    KdfMemoryKib: Parameters.MemoryKiB,
                    KdfIterations: Parameters.Iterations,
                    KdfParallelism: Parameters.Parallelism,
                    SealingPublicKey: photosSealing),
            ]),
            ResumeSessionCommand => new AcknowledgedResult(),
            RetentionCommand => new RetentionResult(["docs: applied (fake)"]),
            _ => new AcknowledgedResult(),
        };
    }

    private static HttpRequestMessage Post(ConsoleHarness harness, string passphrase, string? session = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/retention-apply", UriKind.Relative))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { passphrase }), System.Text.Encoding.UTF8, "application/json"),
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
