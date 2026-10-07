using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.AzureBlob;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// An Azure Blob destination, served end to end against a store that speaks
/// the Blob API on this machine (FR-DEST-005, FR-REP-002, ADR-0093): every
/// case of <see cref="ObjectStoreDestinationTests"/> — sync, read-back,
/// direct-ship, restore, drill, sweep and repair, and the three ways a store
/// goes wrong (FR-VER-001, FR-VER-002, FR-VER-004, FR-VER-007, FR-VER-008,
/// FR-DRL-002, FR-DEST-003) — against a container, signed with the account
/// key; and what only a container has: a second credential, a shared access
/// signature carried in each request as issued, whose expiry the service
/// reads, reports and holds to before a request is sent; and an address the
/// configuration and the provider must agree on.
/// </summary>
/// <remarks>
/// Either credential reaches the service only as an envelope sealed to its
/// recipient key (NFR-SEC-009), lives owner-only in its state directory
/// (NFR-SEC-012), and is in nothing the service says back (NFR-OPS-003,
/// NFR-SEC-006).
/// </remarks>
[TestClass]
public sealed class AzureBlobDestinationTests() : ObjectStoreDestinationTests(StartStore())
{
    private AzureBlobTestServer Azure => (AzureBlobTestServer)Store;

    /// <inheritdoc />
    protected override string Secret => Azure.AccountKey;

    /// <inheritdoc />
    protected override string RefusedSecret => "d3Jvbmcta2V5LXdyb25nLWtleS13cm9uZy1rZXktd3Jvbmcta2V5LXdyb25nLWtleQ==";

    /// <inheritdoc />
    protected override string RefusalCode => "AuthenticationFailed";

    /// <inheritdoc />
    protected override string MissingCredentialWords => "no account key or shared access signature";

    /// <inheritdoc />
    protected override string CredentialNoun => "account key";

    /// <inheritdoc />
    protected override string AlterationWords => "holds the account key or a shared access signature for the container";

    /// <inheritdoc />
    protected override ObjectStoreTestServer StartAnotherStore() => StartStore();

    /// <inheritdoc />
    protected override string EndpointAt(Uri origin) =>
        $"{origin.GetLeftPart(UriPartial.Authority)}/{AzureBlobTestServer.DefaultAccount}";

    /// <inheritdoc />
    protected override DestinationConfiguration Declare(string endpoint, int? drillIntervalDays, int? deepVerifyIntervalDays) => new()
    {
        Id = CloudId,
        Name = "cloud",
        Kind = DestinationKind.AzureBlob,
        Endpoint = endpoint,
        Account = AzureBlobTestServer.DefaultAccount,
        Container = Namespace,
        Prefix = Prefix,
        DrillIntervalDays = drillIntervalDays,
        DeepVerifyIntervalDays = deepVerifyIntervalDays,
    };

    /// <inheritdoc />
    protected override async Task StoreCredentialAsync(ServiceRuntime runtime, string? secret = null)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var envelope = WriteOnlyProvisioning.SealAccountKey(await RecipientAsync(handler), "cloud", secret ?? Azure.AccountKey);
        var stored = await handler.ExecuteAsync(
            new SetDestinationCredentialsCommand("cloud", null, Convert.ToHexStringLower(envelope), CredentialKind: "shared-key"),
            Timeout);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(stored, (stored as ServiceError)?.Message);
    }

    /// <inheritdoc />
    protected override void AssertAddressListed(DestinationDescriptor listed)
    {
        Assert.AreEqual(AzureBlobTestServer.DefaultAccount, listed.Account);
        Assert.AreEqual(Namespace, listed.Container);
        Assert.AreEqual("shared-key", listed.CredentialKind);
        Assert.IsNull(listed.CredentialExpires, "an account key does not lapse");
        Assert.IsNull(listed.Bucket);
        Assert.IsNull(listed.Region);
    }

    [TestMethod]
    public async Task SetDestinationCredentials_ASharedAccessSignature_CarriesEveryRequest_AndTheListingSaysWhenItLapses()
    {
        var expires = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(30), TimeSpan.Zero);
        var token = Azure.IssueSas(Namespace, expires: expires);
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var stored = await StoreSignatureAsync(handler, token);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(stored, out var change, (stored as ServiceError)?.Message);
        Assert.Contains(expires.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), Assert.ContainsSingle(change.Lines), StringComparison.Ordinal);

        await BackUpAsync(runtime);

        var cloud = await RowAsync(runtime, "cloud");
        Assert.AreEqual("in-sync", cloud.State, cloud.Detail);
        Assert.IsTrue(
            Store.Requests.All(request => request.Target.Contains("sig=", StringComparison.Ordinal)
                && !request.Headers.ContainsKey("authorization")),
            "every request carries the signature as issued, and none is signed with a key the service does not hold");

        Assert.IsInstanceOfType<DestinationsResult>(
            await handler.ExecuteAsync(new ListDestinationsCommand(), Timeout), out var listed);
        var described = listed.Destinations.Single(destination => destination.Name == "cloud");
        Assert.IsTrue(described.AccessKeyStored);
        Assert.AreEqual("sas", described.CredentialKind);
        Assert.AreEqual(expires.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture), described.CredentialExpires);
        var signature = token[(token.IndexOf("sig=", StringComparison.Ordinal) + 4)..];
        Assert.DoesNotContain(signature, System.Text.Json.JsonSerializer.Serialize<ServiceResult>(listed, Api.Transport.FrameCodec.SerializerOptions), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SetDestinationCredentials_ASignatureThatHasAlreadyLapsed_IsRefusedByName_AndNothingIsStored()
    {
        WriteConfiguration(directShip: false);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await StoreSignatureAsync(handler, Azure.IssueSas(Namespace, expires: DateTimeOffset.UtcNow.AddHours(-1))),
            out var refused);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, refused.Reason);
        Assert.Contains("expired", refused.Message, StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(HeldCredential), "nothing refused was stored");
    }

    [TestMethod]
    public async Task Sync_UnderASignatureThatHasLapsedSinceItWasStored_IsFailed_SayingWhen_AndSendsNothing()
    {
        // A signature says in clear when it stops being honoured. Past that,
        // every request is one the store will refuse, so none is sent, and
        // the person is told what to replace rather than shown a refusal.
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        runtime.DestinationCredentials.Save(
            CloudId, AzureBlobCredentials.SharedAccessSignature(Azure.IssueSas(Namespace, expires: DateTimeOffset.UtcNow.AddMinutes(-1))));
        await BackUpAsync(runtime);

        var cloud = await RowAsync(runtime, "cloud");
        Assert.AreEqual("failed", cloud.State);
        Assert.Contains("expired", cloud.Detail!, StringComparison.Ordinal);
        Assert.Contains("destination-credentials", cloud.Detail!, StringComparison.Ordinal);
        Assert.IsEmpty(Store.Requests);
    }

    [TestMethod]
    public async Task SetDestinationCredentials_WhatCannotBeStored_IsRefusedByName()
    {
        Directory.CreateDirectory(Path.Combine(Harness.WorkPath, "vault"));
        WriteConfiguration(directShip: false, withVault: true);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var recipient = await RecipientAsync(handler);
        string Sealed(string secret) => Convert.ToHexStringLower(WriteOnlyProvisioning.SealAccountKey(recipient, "cloud", secret));
        var key = Sealed(Azure.AccountKey);

        ServiceError Refused(ServiceResult result, ServiceErrorReason reason)
        {
            Assert.IsInstanceOfType<ServiceError>(result, out var error);
            Assert.AreEqual(reason, error.Reason, error.Message);
            return error;
        }

        Refused(
            await handler.ExecuteAsync(new SetDestinationCredentialsCommand("nowhere", null, key, "shared-key"), Timeout),
            ServiceErrorReason.NotFound);

        var local = Refused(
            await handler.ExecuteAsync(
                new SetDestinationCredentialsCommand(
                    "vault", null,
                    Convert.ToHexStringLower(WriteOnlyProvisioning.SealAccountKey(recipient, "vault", Azure.AccountKey)),
                    "shared-key"),
                Timeout),
            ServiceErrorReason.InvalidArgument);
        Assert.Contains("vault", local.Message, StringComparison.Ordinal);

        // An S3-compatible store's access key is a credential of another API.
        var accessKey = Refused(
            await handler.ExecuteAsync(
                new SetDestinationCredentialsCommand(
                    "cloud", "FBPKEYID0001",
                    Convert.ToHexStringLower(WriteOnlyProvisioning.SealAccessKeySecret(recipient, "cloud", "FBPKEYID0001", "secret"))),
                Timeout),
            ServiceErrorReason.InvalidArgument);
        Assert.Contains("account key", accessKey.Message, StringComparison.Ordinal);
        Assert.Contains("shared access signature", accessKey.Message, StringComparison.Ordinal);

        Refused(
            await handler.ExecuteAsync(new SetDestinationCredentialsCommand("cloud", null, key, "password"), Timeout),
            ServiceErrorReason.InvalidArgument);
        Refused(
            await handler.ExecuteAsync(new SetDestinationCredentialsCommand("cloud", "FBPKEYID0001", key, "shared-key"), Timeout),
            ServiceErrorReason.InvalidArgument);
        Refused(
            await handler.ExecuteAsync(new SetDestinationCredentialsCommand("cloud", null, "not hex", "shared-key"), Timeout),
            ServiceErrorReason.InvalidArgument);

        // Sealed for another destination, or as the other kind: either way it
        // does not open, and the refusal does not say which.
        var elsewhere = Convert.ToHexStringLower(WriteOnlyProvisioning.SealAccountKey(recipient, "offsite", Azure.AccountKey));
        Assert.Contains(
            "does not open",
            Refused(await handler.ExecuteAsync(new SetDestinationCredentialsCommand("cloud", null, elsewhere, "shared-key"), Timeout), ServiceErrorReason.InvalidArgument).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not open",
            Refused(await handler.ExecuteAsync(new SetDestinationCredentialsCommand("cloud", null, key, "sas"), Timeout), ServiceErrorReason.InvalidArgument).Message,
            StringComparison.Ordinal);

        // Opened, and still not a credential the store could take.
        Refused(
            await handler.ExecuteAsync(new SetDestinationCredentialsCommand("cloud", null, Sealed("not base64 at all"), "shared-key"), Timeout),
            ServiceErrorReason.InvalidArgument);
        var unsigned = Refused(
            await handler.ExecuteAsync(
                new SetDestinationCredentialsCommand(
                    "cloud", null,
                    Convert.ToHexStringLower(WriteOnlyProvisioning.SealSharedAccessSignature(recipient, "cloud", "sv=2024-11-04&sr=c&sp=racwdl")),
                    "sas"),
                Timeout),
            ServiceErrorReason.InvalidArgument);
        Assert.Contains("sig", unsigned.Message, StringComparison.Ordinal);

        Assert.IsFalse(
            Directory.Exists(Path.Combine(Harness.StateDirectory, "destination-credentials"))
            && Directory.EnumerateFiles(Path.Combine(Harness.StateDirectory, "destination-credentials")).Any(),
            "nothing refused was stored");
    }

    [TestMethod]
    public void AddressDefect_TheConfigurationAndTheProvider_AgreeOnEveryAddress()
    {
        // The configuration judges an address without the provider, which it
        // cannot reference; the provider judges it again before it dials.
        // Two rules that disagreed would let a declaration pass the one and
        // fail the other only when a backup ran.
        string?[] endpoints =
        [
            null, "https://objects.example.net", "https://objects.example.net:8443", "http://objects.example.net",
            "http://127.0.0.1:10000/fbptestaccount", "http://localhost:10000/fbptestaccount/", "http://[::1]:10000",
            "objects.example.net", "https://objects.example.net/another", "https://objects.example.net/fbptestaccount/x",
            "https://objects.example.net?x=1", "ftp://objects.example.net", "https://user:pass@objects.example.net",
            "https://objects.example.net/", "https://objects.example.net#frag",
        ];
        string?[] accounts = [null, "fbptestaccount", "ab", "Has_Capitals", "has-hyphen", new string('a', 24), new string('a', 25)];
        string?[] containers = [null, "family-backups", "ab", "two--hyphens", "-leading", "trailing-", "No_Capitals", new string('c', 63), new string('c', 64)];
        string?[] prefixes = [null, "site-a", "site-a/host_1", "/site-a", "site-a/", "site-a//b", "site-a/.hidden", "a b"];

        foreach (var endpoint in endpoints)
        {
            foreach (var account in accounts)
            {
                foreach (var container in containers)
                {
                    foreach (var prefix in prefixes)
                    {
                        var declared = new DestinationConfiguration
                        {
                            Id = CloudId, Name = "cloud", Kind = DestinationKind.AzureBlob,
                            Endpoint = endpoint, Account = account, Container = container, Prefix = prefix,
                        };
                        var provider = endpoint is null || Uri.TryCreate(endpoint, UriKind.Absolute, out _)
                            ? AzureBlobLocation.DefectOf(account, container, prefix, endpoint is null ? null : new Uri(endpoint))
                            : "not an absolute URI";

                        Assert.AreEqual(
                            provider is null, declared.AddressDefect is null,
                            $"{endpoint ?? "(none)"} {account ?? "(none)"} {container ?? "(none)"} {prefix ?? "(none)"}: "
                            + $"configuration says '{declared.AddressDefect ?? "fine"}', provider says '{provider ?? "fine"}'");
                    }
                }
            }
        }
    }

    private string HeldCredential => Path.Combine(Harness.StateDirectory, "destination-credentials", $"{CloudId}.json");

    private async Task<ServiceResult> StoreSignatureAsync(ServiceCommandHandler handler, string token)
    {
        var envelope = WriteOnlyProvisioning.SealSharedAccessSignature(await RecipientAsync(handler), "cloud", token);
        return await handler.ExecuteAsync(
            new SetDestinationCredentialsCommand("cloud", null, Convert.ToHexStringLower(envelope), CredentialKind: "sas"),
            Timeout);
    }

    private static AzureBlobTestServer StartStore()
    {
        var store = new AzureBlobTestServer();
        store.CreateContainer(Namespace);
        return store;
    }
}
