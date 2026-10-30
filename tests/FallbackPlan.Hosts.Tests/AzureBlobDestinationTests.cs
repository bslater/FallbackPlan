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
/// reads, reports and holds to before a request is sent, and names in a
/// notice from two weeks before it lapses until a new one is stored (ADR-0093
/// Amendment 1); an address the configuration and the provider must agree
/// on; and an account under soft delete, whose deletes the sync counts and
/// raises no notice for, since the account releases what it keeps by itself
/// (FR-GC-010, ADR-0171 Amendment 4).
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
    protected override string BusyCode => "ServerBusy";

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
    protected override DestinationConfiguration DeclareStore(string endpoint, int? drillIntervalDays, int? deepVerifyIntervalDays) => new()
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
            new SetDestinationCredentialsCommand("cloud", null, Convert.ToHexStringLower(envelope), Kind: "shared-key"),
            Timeout);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(stored, (stored as ServiceError)?.Message);
    }

    /// <inheritdoc />
    protected override void AssertAddressListed(DestinationDescriptor listed)
    {
        Assert.AreEqual(AzureBlobTestServer.DefaultAccount, listed.Account);
        Assert.AreEqual(Namespace, listed.Container);
        Assert.AreEqual("shared-key", listed.AuthorisedBy);
        Assert.IsNull(listed.SignatureExpires, "an account key does not lapse");
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
        Assert.AreEqual("sas", described.AuthorisedBy);
        Assert.AreEqual(expires.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture), described.SignatureExpires);
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
    public async Task StoringASignatureThatLapsesWithinTwoWeeks_RaisesANotice_NamingTheDestinationAndWhen()
    {
        // The answer to the store says when it lapses, once, to whoever
        // stored it. The notice is for whoever looks after the installation
        // in the days that are left.
        var expires = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(10), TimeSpan.Zero);
        WriteConfiguration(directShip: false);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await StoreSignatureAsync(handler, Azure.IssueSas(Namespace, expires: expires)));

        var notice = LapseNotice(runtime);
        Assert.IsNotNull(notice, "a signature with ten days left is named before it lapses");
        Assert.Contains("'cloud'", notice.Message, StringComparison.Ordinal);
        Assert.Contains(
            expires.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC",
            notice.Message,
            StringComparison.Ordinal);
        Assert.Contains("destination-credentials cloud --sas-env", notice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("expired", notice.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task APass_NamesASignature_OnlyOnceItIsWithinTwoWeeksOfItsLapse()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await StoreSignatureAsync(handler, Azure.IssueSas(Namespace, expires: DateTimeOffset.UtcNow.AddDays(15))));
        await BackUpAsync(runtime);
        Assert.IsNull(LapseNotice(runtime), "fifteen days ahead is more than two weeks");

        await PassAtAsync(runtime, DateTimeOffset.Now.AddDays(2));

        Assert.IsNotNull(LapseNotice(runtime), "thirteen days ahead is within them");
    }

    [TestMethod]
    public async Task AShutBackgroundWindow_StillNamesASignatureAboutToLapse()
    {
        // The window holds back work that loads the machine or the link. A
        // signature's expiry is read from the token the service holds, and
        // asks nothing of either.
        WriteConfiguration(directShip: false);
        var opens = DateTimeOffset.Now.AddHours(3);
        var configuration = Path.Combine(Harness.StateDirectory, "config.json");
        (ClientConfiguration.Load(configuration) with
        {
            BackgroundWindow = string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"{opens:HH\\:mm}-{opens.AddHours(1):HH\\:mm}"),
        }).Save(configuration);

        await using var runtime = await StartAsync();
        runtime.DestinationCredentials.Save(
            CloudId, AzureBlobCredentials.SharedAccessSignature(Azure.IssueSas(Namespace, expires: DateTimeOffset.UtcNow.AddDays(10))));
        Assert.IsNull(LapseNotice(runtime));

        await PassAtAsync(runtime, DateTimeOffset.Now);

        Assert.IsNotNull(LapseNotice(runtime));
        Assert.IsEmpty(Store.Requests, "the window still held the sync");
    }

    [TestMethod]
    public async Task StoringANewSignature_ResolvesTheNotice_AndKeepsItOnRecord()
    {
        WriteConfiguration(directShip: false);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await StoreSignatureAsync(handler, Azure.IssueSas(Namespace, expires: DateTimeOffset.UtcNow.AddDays(10))));
        Assert.IsNotNull(LapseNotice(runtime));

        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await StoreSignatureAsync(handler, Azure.IssueSas(Namespace, expires: DateTimeOffset.UtcNow.AddDays(90))));

        Assert.IsNull(LapseNotice(runtime), "a signature with ninety days left is not about to lapse");
        Assert.IsNotNull(
            Assert.ContainsSingle(runtime.Notices.Notices.Where(notice => notice.Key == LapseNoticeKey)).AcknowledgedAt,
            "resolved, and still on record");
    }

    [TestMethod]
    public async Task StoringTheAccountKeyInstead_ResolvesTheNotice()
    {
        WriteConfiguration(directShip: false);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await StoreSignatureAsync(handler, Azure.IssueSas(Namespace, expires: DateTimeOffset.UtcNow.AddDays(10))));
        Assert.IsNotNull(LapseNotice(runtime));

        await StoreCredentialAsync(runtime);

        Assert.IsNull(LapseNotice(runtime), "an account key does not lapse");
    }

    [TestMethod]
    public async Task AnAcknowledgedNotice_IsNotAskedAgain_UntilTheSignatureLapses_AndThenSaysItHas()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await StoreSignatureAsync(handler, Azure.IssueSas(Namespace, expires: DateTimeOffset.UtcNow.AddDays(10))));
        Assert.IsTrue(runtime.Notices.Acknowledge(
            LapseNotice(runtime)!.Id, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        await PassAtAsync(runtime, DateTimeOffset.Now);
        Assert.IsNull(LapseNotice(runtime), "the same warning is not asked again on every pass");

        await PassAtAsync(runtime, DateTimeOffset.Now.AddDays(11));

        var lapsed = LapseNotice(runtime);
        Assert.IsNotNull(lapsed, "the lapse itself is news");
        Assert.Contains("expired", lapsed.Message, StringComparison.Ordinal);
        Assert.Contains("destination-credentials cloud --sas-env", lapsed.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ASignatureThatHasLapsedSinceItWasStored_IsNamedAsLapsed_AtTheNextPass()
    {
        // A destination nothing is synced to for weeks, or one kept only to
        // restore from, fails nothing when its signature lapses. The notice
        // says so all the same.
        WriteConfiguration(directShip: false, referenced: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        runtime.DestinationCredentials.Save(
            CloudId, AzureBlobCredentials.SharedAccessSignature(Azure.IssueSas(Namespace, expires: DateTimeOffset.UtcNow.AddMinutes(-1))));

        await PassAtAsync(runtime, DateTimeOffset.Now);

        var notice = LapseNotice(runtime);
        Assert.IsNotNull(notice);
        Assert.Contains("expired", notice.Message, StringComparison.Ordinal);
        Assert.Contains("nothing is sent under it", notice.Message, StringComparison.Ordinal);
        Assert.IsEmpty(Store.Requests);
    }

    [TestMethod]
    public async Task ASignatureThatStatesNoExpiry_IsNeverNamed()
    {
        WriteConfiguration(directShip: false, referenced: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        runtime.DestinationCredentials.Save(
            CloudId, AzureBlobCredentials.SharedAccessSignature("sv=2024-11-04&sr=c&sp=racwdl&sig=c2lnbmF0dXJl"));

        await PassAtAsync(runtime, DateTimeOffset.Now.AddYears(5));

        Assert.IsNull(LapseNotice(runtime));
    }

    [TestMethod]
    public async Task RemovingTheDestination_ResolvesItsNotice()
    {
        WriteConfiguration(directShip: false);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await StoreSignatureAsync(handler, Azure.IssueSas(Namespace, expires: DateTimeOffset.UtcNow.AddDays(10))));
        Assert.IsNotNull(LapseNotice(runtime));

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [],
            BackupSets = [],
        }.Save(Path.Combine(Harness.StateDirectory, "config.json"));
        await PassAtAsync(runtime, DateTimeOffset.Now);

        Assert.IsNull(LapseNotice(runtime), "nothing is sent under a credential no destination uses");
    }

    [TestMethod]
    public async Task ASignatureHeldForADestinationThatIsNoLongerAContainer_IsNotNamed()
    {
        // The destination's kind was changed after its signature was
        // stored. Nothing is sent under a credential its kind cannot use,
        // so that credential's expiry is nobody's concern.
        WriteConfiguration(directShip: false);

        await using var runtime = await StartAsync();
        runtime.DestinationCredentials.Save(
            CloudId, AzureBlobCredentials.SharedAccessSignature(Azure.IssueSas(Namespace, expires: DateTimeOffset.UtcNow.AddDays(10))));
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = CloudId, Name = "cloud", Kind = DestinationKind.LocalPath, Path = Path.Combine(Harness.WorkPath, "vault"),
                },
            ],
            BackupSets = [],
        }.Save(Path.Combine(Harness.StateDirectory, "config.json"));

        await PassAtAsync(runtime, DateTimeOffset.Now);

        Assert.IsNull(LapseNotice(runtime));
    }

    [TestMethod]
    public async Task APass_ResolvesOnlyItsOwnNotices()
    {
        WriteConfiguration(directShip: false, referenced: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        runtime.Notices.Raise(
            "quota-exceeded:cloud", "Destination 'cloud' refused set 'docs'.", (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        await PassAtAsync(runtime, DateTimeOffset.Now);

        Assert.ContainsSingle(runtime.Notices.Unacknowledged.Where(notice => notice.Key == "quota-exceeded:cloud"));
    }

    [TestMethod]
    public async Task ACredentialThatWillNotRead_LosesNoPass_AndLeavesTheNoticeStanding()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await StoreSignatureAsync(handler, Azure.IssueSas(Namespace, expires: DateTimeOffset.UtcNow.AddDays(10))));
        File.WriteAllText(HeldCredential, "{ \"kind\": \"sas\" }");

        await PassAtAsync(runtime, DateTimeOffset.Now);

        Assert.IsNotNull(LapseNotice(runtime), "damage is said where the credential is used, and is not a renewal");
        Assert.Contains("damaged", Pair(runtime, "cloud").LastError!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ACredentialOfAnotherApi_IsNotHeldForAContainer_AndNothingIsSentUnderIt()
    {
        // A destination whose kind was changed after its credential was
        // stored holds an access key a container cannot use. It is told it
        // holds none, the way it would be if nothing had been stored, rather
        // than listed as ready and failing at the store.
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        runtime.DestinationCredentials.Save(CloudId, new Storage.S3.S3Credentials("FBPKEYID0001", "not/a/key/for/this/api"));
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<DestinationsResult>(
            await handler.ExecuteAsync(new ListDestinationsCommand(), Timeout), out var listed);
        var described = listed.Destinations.Single(destination => destination.Name == "cloud");
        Assert.IsFalse(described.AccessKeyStored);
        Assert.IsNull(described.AuthorisedBy);

        await BackUpAsync(runtime);
        var cloud = await RowAsync(runtime, "cloud");
        Assert.AreEqual("failed", cloud.State);
        Assert.Contains(MissingCredentialWords, cloud.Detail!, StringComparison.Ordinal);
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

    private const string LapseNoticeKey = $"signature-lapsing:{CloudId}";

    private static Notice? LapseNotice(ServiceRuntime runtime) =>
        runtime.Notices.Unacknowledged.SingleOrDefault(notice => notice.Key == LapseNoticeKey);

    private async Task PassAtAsync(ServiceRuntime runtime, DateTimeOffset now)
    {
        var pass = await Scheduler.RunPassAsync(runtime, now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
    }

    private async Task<ServiceResult> StoreSignatureAsync(ServiceCommandHandler handler, string token)
    {
        var envelope = WriteOnlyProvisioning.SealSharedAccessSignature(await RecipientAsync(handler), "cloud", token);
        return await handler.ExecuteAsync(
            new SetDestinationCredentialsCommand("cloud", null, Convert.ToHexStringLower(envelope), Kind: "sas"),
            Timeout);
    }

    [TestMethod]
    public async Task Sync_ToAContainerUnderSoftDelete_StaysInSync_AndRaisesNoNotice()
    {
        // Soft delete is on by default for a new account. Each delete the
        // store's retention asks for is answered, and the account keeps the
        // bytes until its retention period ends, then releases them itself.
        // The replica is whole. A notice would stand on every default
        // account for space that comes back on its own, so none is raised;
        // the sync's log says what was kept.
        WriteConfiguration(directShip: false, retention: new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 });
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var now = DateTimeOffset.Now;
        await BackUpAsync(runtime, now);

        Azure.SoftDelete(Namespace);
        Harness.WriteSourceFile("docs/c.txt", "written after the first backup, so held by the second snapshot alone");
        await BackUpAsync(runtime, now.AddHours(1));

        Assert.IsGreaterThan(
            0L, Azure.SoftDeletedBytesIn(Namespace),
            "the premise: the store's retention deleted something, and the account kept it");
        var synced = Pair(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.InSync, synced.State, synced.LastError);
        Assert.DoesNotContain("damaged", await VerifyAsync(runtime), StringComparison.Ordinal);
        Assert.IsFalse(
            runtime.Notices.Unacknowledged.Any(notice => notice.Key.StartsWith("destination-deletes-", StringComparison.Ordinal)),
            "space the account releases by itself raises no notice: "
                + string.Join("; ", runtime.Notices.Unacknowledged.Select(notice => notice.Key)));
    }

    private static AzureBlobTestServer StartStore()
    {
        var store = new AzureBlobTestServer();
        store.CreateContainer(Namespace);
        return store;
    }
}
