using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// Deleting snapshots from the console (FR-GC-013, ADR-0080): the console
/// derives the one set's reclaim grant from the passphrase in its own
/// process, under the salt that set's archive was born under, seals it to the
/// service's recipient key and sends only the sealed envelope with the
/// <c>delete_snapshots</c> command. The browser's session is resumed first,
/// so the deletion is recorded as the person's. A passphrase that does not
/// open the set is refused here, and the service is sent nothing.
/// </summary>
[TestClass]
public sealed class SnapshotDeletionCeremonyTests
{
    private const string PassphraseText = "the console deletion passphrase";

    private static readonly string DocsId = new('a', 32);
    private static readonly string PhotosId = new('b', 32);
    private static readonly string Snapshot = new('5', 64);

    private static Argon2Parameters Parameters => RepositoryCreationSettings.Default.KdfParameters;

    [TestMethod]
    public async Task DeleteSnapshots_DerivesTheSetsGrantInTheConsole_AndThePassphraseNeverCrosses()
    {
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var installationSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var photosSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        using var installation = Derive(PassphraseText, installationSalt);
        using var photos = Derive(PassphraseText, photosSalt);

        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = Service(recipientScalar, installationSalt, installation, photosSalt, photos);

        using (var response = await harness.Http.SendAsync(
            Post(harness, PhotosId, PassphraseText, session: "the browser's session")))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("applied", body.RootElement.GetProperty("outcome").GetString());
            var outcome = body.RootElement.GetProperty("snapshots")[0];
            Assert.AreEqual(Snapshot, outcome.GetProperty("snapshotId").GetString());
            Assert.AreEqual("pending", outcome.GetProperty("state").GetString());
            Assert.AreEqual("usb", outcome.GetProperty("awaiting")[0].GetString());
        }

        var received = harness.Clients.Client.Received;
        var sent = Assert.ContainsSingle(received.OfType<DeleteSnapshotsCommand>());
        Assert.AreEqual(PhotosId, sent.SetId);
        Assert.AreEqual(Snapshot, Assert.ContainsSingle(sent.SnapshotIds));
        Assert.IsTrue(sent.Apply);

        // The envelope opens to the reclaim key the photos archive's own salt
        // derives, not the installation's.
        CollectionAssert.AreEqual(
            photos.ReclaimKeySeed.ToArray(),
            WriteOnlyProvisioning.OpenGrant(recipientScalar, Convert.FromHexString(sent.ReclaimGrant!)));

        // The person's session is resumed first, so the deletion is theirs.
        Assert.IsInstanceOfType<ResumeSessionCommand>(received[0]);
        Assert.IsFalse(
            received.Any(command => JsonSerializer.Serialize(command, FrameCodec.SerializerOptions)
                .Contains(PassphraseText, StringComparison.Ordinal)),
            "the passphrase reaches no command the service is sent");
    }

    [TestMethod]
    public async Task DeleteSnapshots_APassphraseThatDoesNotOpenTheSet_IsRefusedHere_AndTheServiceIsSentNothing()
    {
        // The installation's passphrase does not open an archive adopted under
        // another one, and the console says so before anything is sent.
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var installationSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var photosSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        using var installation = Derive(PassphraseText, installationSalt);
        using var someoneElse = Derive("a passphrase from another installation", photosSalt);

        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = Service(recipientScalar, installationSalt, installation, photosSalt, someoneElse);

        using var response = await harness.Http.SendAsync(Post(harness, PhotosId, PassphraseText));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("wrong", body.RootElement.GetProperty("outcome").GetString());
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<DeleteSnapshotsCommand>());
    }

    [TestMethod]
    public async Task DeleteSnapshots_TheServicesRefusal_IsSaidInItsOwnWords()
    {
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var installationSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var photosSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        using var installation = Derive(PassphraseText, installationSalt);
        using var photos = Derive(PassphraseText, photosSalt);

        await using var harness = await ConsoleHarness.StartAsync();
        var service = Service(recipientScalar, installationSalt, installation, photosSalt, photos);
        harness.Clients.Client.Respond = command => command is DeleteSnapshotsCommand
            ? new ServiceError(ServiceErrorReason.Refused, "it would leave the set no complete snapshot")
            : service(command);

        using var response = await harness.Http.SendAsync(Post(harness, DocsId, PassphraseText));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("refused", body.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual(
            "it would leave the set no complete snapshot", body.RootElement.GetProperty("detail").GetString());
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
                "1.51", "test", "vm", "/state", false, 0,
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
                    PhotosId, "photos", "/photos", null, [], [], ["usb"],
                    KdfSalt: Convert.ToHexStringLower(photosSalt),
                    KdfMemoryKib: Parameters.MemoryKiB,
                    KdfIterations: Parameters.Iterations,
                    KdfParallelism: Parameters.Parallelism,
                    SealingPublicKey: photosSealing),
            ]),
            ResumeSessionCommand => new AcknowledgedResult(),
            DeleteSnapshotsCommand delete => new DeleteSnapshotsResult(
                delete.SetId, Applied: true,
                [.. delete.SnapshotIds.Select(id => new SnapshotDeletionOutcome(id, "pending", ["usb"]))],
                ["photos: deletion requested (fake)"]),
            _ => new AcknowledgedResult(),
        };
    }

    private static HttpRequestMessage Post(
        ConsoleHarness harness, string setId, string passphrase, string? session = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/delete-snapshots", UriKind.Relative))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["setId"] = setId,
                    ["snapshotIds"] = new[] { Snapshot },
                    ["passphrase"] = passphrase,
                }),
                System.Text.Encoding.UTF8,
                "application/json"),
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
