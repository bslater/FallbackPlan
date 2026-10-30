using System.Globalization;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.S3;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// An S3-compatible destination, served end to end against a store that
/// speaks the API on this machine (FR-DEST-005, FR-REP-002, ADR-0091): every
/// case of <see cref="ObjectStoreDestinationTests"/> — sync, read-back,
/// direct-ship, restore, drill, sweep and repair, and the three ways a store
/// goes wrong (FR-VER-001, FR-VER-002, FR-VER-004, FR-VER-007, FR-VER-008,
/// FR-DRL-002, FR-DEST-003) — against a bucket, signed with an access key,
/// and what only a bucket has: a key id the key's envelope is bound to, an
/// address the configuration and the provider must agree on, and a store
/// that can be full or hold a quota, each told apart from a throttle and
/// from the other (FR-QUOTA-001, FR-QUOTA-002). A bucket that keeps versions
/// answers the deletes its retention asks for and keeps their bytes, and the
/// sync says how many (FR-GC-010, ADR-0171 Amendment 2).
/// </summary>
/// <remarks>
/// The access key reaches the service only as an envelope sealed to its
/// recipient key (NFR-SEC-009), lives owner-only in its state directory
/// (NFR-SEC-012), and is in nothing the service says back (NFR-OPS-003,
/// NFR-SEC-006).
/// </remarks>
[TestClass]
public sealed class S3DestinationTests() : ObjectStoreDestinationTests(StartStore())
{
    private S3CompatibleTestServer S3 => (S3CompatibleTestServer)Store;

    /// <inheritdoc />
    protected override string Secret => S3.SecretAccessKey;

    /// <inheritdoc />
    protected override string RefusedSecret => "not/the+secret=this/store/knows/0000000";

    /// <inheritdoc />
    protected override string RefusalCode => "SignatureDoesNotMatch";

    /// <inheritdoc />
    protected override string BusyCode => "SlowDown";

    /// <inheritdoc />
    protected override string MissingCredentialWords => "no access key";

    /// <inheritdoc />
    protected override string CredentialNoun => "access key";

    /// <inheritdoc />
    protected override string AlterationWords => "holds a key to the bucket";

    /// <inheritdoc />
    protected override ObjectStoreTestServer StartAnotherStore() => StartStore();

    /// <inheritdoc />
    protected override string EndpointAt(Uri origin) => origin.ToString();

    /// <inheritdoc />
    protected override DestinationConfiguration DeclareStore(string endpoint, int? drillIntervalDays, int? deepVerifyIntervalDays) => new()
    {
        Id = CloudId,
        Name = "cloud",
        Kind = DestinationKind.S3,
        Endpoint = endpoint,
        Bucket = Namespace,
        Region = S3CompatibleTestServer.DefaultRegion,
        Prefix = Prefix,
        DrillIntervalDays = drillIntervalDays,
        DeepVerifyIntervalDays = deepVerifyIntervalDays,
    };

    /// <inheritdoc />
    protected override async Task StoreCredentialAsync(ServiceRuntime runtime, string? secret = null)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var envelope = WriteOnlyProvisioning.SealAccessKeySecret(
            await RecipientAsync(handler), "cloud", S3.AccessKeyId, secret ?? S3.SecretAccessKey);
        var stored = await handler.ExecuteAsync(
            new SetDestinationCredentialsCommand("cloud", S3.AccessKeyId, Convert.ToHexStringLower(envelope)),
            Timeout);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(stored, (stored as ServiceError)?.Message);
    }

    /// <inheritdoc />
    protected override void AssertAddressListed(DestinationDescriptor listed)
    {
        Assert.AreEqual(Namespace, listed.Bucket);
        Assert.AreEqual(S3.Region, listed.Region);
        Assert.AreEqual("access-key", listed.AuthorisedBy);
        Assert.IsNull(listed.Account);
        Assert.IsNull(listed.Container);
    }

    [TestMethod]
    public async Task SetDestinationCredentials_WhatCannotBeStored_IsRefusedByName()
    {
        Directory.CreateDirectory(Path.Combine(Harness.WorkPath, "vault"));
        WriteConfiguration(directShip: false, withVault: true);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var recipient = await RecipientAsync(handler);
        var sealedForCloud = Convert.ToHexStringLower(WriteOnlyProvisioning.SealAccessKeySecret(
            recipient, "cloud", S3.AccessKeyId, S3.SecretAccessKey));

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new SetDestinationCredentialsCommand("nowhere", S3.AccessKeyId, sealedForCloud), Timeout),
            out var unknown);
        Assert.AreEqual(ServiceErrorReason.NotFound, unknown.Reason);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new SetDestinationCredentialsCommand(
                    "vault", S3.AccessKeyId,
                    Convert.ToHexStringLower(WriteOnlyProvisioning.SealAccessKeySecret(
                        recipient, "vault", S3.AccessKeyId, S3.SecretAccessKey))),
                Timeout),
            out var local);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, local.Reason);
        Assert.Contains("vault", local.Message, StringComparison.Ordinal);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new SetDestinationCredentialsCommand("cloud", S3.AccessKeyId, "not hex"), Timeout),
            out var notHex);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, notHex.Reason);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new SetDestinationCredentialsCommand("cloud", "ANOTHERKEYID", sealedForCloud), Timeout),
            out var misbound);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, misbound.Reason);
        Assert.Contains("does not open", misbound.Message, StringComparison.Ordinal);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new SetDestinationCredentialsCommand("cloud", " ", sealedForCloud), Timeout),
            out var blank);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, blank.Reason);

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
        string[] endpoints =
        [
            "https://objects.example.net", "https://objects.example.net:8443", "http://objects.example.net",
            "http://127.0.0.1:9000", "http://localhost:9000", "http://[::1]:9000", "objects.example.net",
            "https://objects.example.net/backups", "https://objects.example.net?x=1", "ftp://objects.example.net",
            "https://user:pass@objects.example.net", "https://objects.example.net/",
        ];
        string[] buckets = ["family-backups", "ab", "No_Capitals", "a.b-c.d", "-leading", "trailing-", "x".PadRight(64, 'x')];
        string?[] regions = [null, "eu-test-1", "Eu West", "us-east-1", ""];
        string?[] prefixes = [null, "site-a", "site-a/host_1", "/site-a", "site-a/", "site-a//b", "site-a/.hidden", "a b"];

        foreach (var endpoint in endpoints)
        {
            foreach (var bucket in buckets)
            {
                foreach (var region in regions)
                {
                    foreach (var prefix in prefixes)
                    {
                        var declared = new DestinationConfiguration
                        {
                            Id = CloudId, Name = "cloud", Kind = DestinationKind.S3,
                            Endpoint = endpoint, Bucket = bucket, Region = region, Prefix = prefix,
                        };
                        var provider = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                            ? S3Location.DefectOf(uri, bucket, declared.EffectiveRegion, prefix)
                            : "not an absolute URI";

                        Assert.AreEqual(
                            provider is null, declared.AddressDefect is null,
                            $"{endpoint} {bucket} {region ?? "(none)"} {prefix ?? "(none)"}: configuration says "
                            + $"'{declared.AddressDefect ?? "fine"}', provider says '{provider ?? "fine"}'");
                    }
                }
            }
        }
    }

    private static S3CompatibleTestServer StartStore()
    {
        var store = new S3CompatibleTestServer();
        store.CreateBucket(Namespace);
        return store;
    }

    [TestMethod]
    public async Task Sync_ToAStoreOutOfRoom_IsUnavailable_SaysSo_AndTheReplicaItHadStillProves()
    {
        // A store with no room for the next object (507): the copy stops at
        // the object it could not place, leaving every one before it whole,
        // and it is a gap that closes itself once room is made, as a full
        // disk at a local path is.
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var now = DateTimeOffset.Now;
        await BackUpAsync(runtime, now);

        Harness.WriteSourceFile("docs/c.txt", "the object there is no room for");
        Store.FailFrom(servedFirst: 0, 507, "XMinioStorageFull", method: "PUT");
        await BackUpAsync(runtime, now.AddHours(1));

        var full = Pair(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Unavailable, full.State, full.LastError);
        Assert.Contains("no room", full.LastError!, StringComparison.Ordinal);
        Assert.Contains("XMinioStorageFull", full.LastError!, StringComparison.Ordinal);

        Store.Recover();
        var line = await VerifyAsync(runtime);
        Assert.Contains("object(s) confirmed", line, StringComparison.Ordinal);
        Assert.DoesNotContain("damaged", line, StringComparison.Ordinal);

        await SyncAsync(runtime);
        var synced = Pair(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.InSync, synced.State, synced.LastError);
    }

    [TestMethod]
    public async Task Sync_OverTheBucketsQuota_FailsNamingTheQuota_WithANoticeForAPerson()
    {
        // A quota is a limit the store's owner set, and it holds until a
        // person raises it or keeps less there: a failure with a notice, told
        // apart from a full store and from a throttle; and the replica the
        // store had is untouched.
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var now = DateTimeOffset.Now;
        await BackUpAsync(runtime, now);

        Harness.WriteSourceFile("docs/c.txt", "the object the quota will not take");
        Store.FailFrom(servedFirst: 0, 403, "QuotaExceeded", method: "PUT");
        await BackUpAsync(runtime, now.AddHours(1));

        var refused = Pair(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Failed, refused.State, refused.LastError);
        Assert.Contains("quota", refused.LastError!, StringComparison.Ordinal);
        Assert.Contains("QuotaExceeded", refused.LastError!, StringComparison.Ordinal);
        var notice = runtime.Notices.Unacknowledged.Single(notice =>
            notice.Key.StartsWith("quota-exceeded:", StringComparison.Ordinal)).Message;
        Assert.Contains("cloud", notice, StringComparison.Ordinal);

        Store.Recover();
        await SyncAsync(runtime);
        var synced = Pair(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.InSync, synced.State, synced.LastError);
    }

    [TestMethod]
    public async Task Sync_ToABucketThatKeepsVersions_StaysInSync_AndSaysWhatItsDeletesKept()
    {
        // A bucket under object lock keeps versions, and so does one with
        // versioning on: each delete the store's retention asks for is
        // answered, and the bytes stay behind a delete marker. The replica
        // is whole, and only a notice can say the space did not come back.
        WriteConfiguration(directShip: false, retention: new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 });
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var now = DateTimeOffset.Now;
        await BackUpAsync(runtime, now);

        S3.KeepVersions(Namespace);
        Harness.WriteSourceFile("docs/c.txt", "written after the first backup, so held by the second snapshot alone");
        await BackUpAsync(runtime, now.AddHours(1));

        var kept = S3.EarlierVersionBytesIn(Namespace);
        Assert.IsGreaterThan(0L, kept, "the premise: the store's retention deleted something, and the bucket kept it");
        var synced = Pair(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.InSync, synced.State, synced.LastError);
        var notice = RetainedDeletesNotice(runtime);
        Assert.IsNotNull(notice, "space a delete did not release is said, not just logged");
        Assert.Contains("earlier version", notice.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"{kept.ToString("N0", CultureInfo.InvariantCulture)} byte(s)", notice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("damaged", await VerifyAsync(runtime), StringComparison.Ordinal);

        // A pass that deletes nothing has learned nothing about the bucket.
        // One two days on reads the store through, and its policy drops
        // nothing more: the newest snapshot is the one generation it keeps.
        var reading = FanOut.Enqueue(
            runtime, runtime.Configuration.BackupSets.Single(), "cloud", now.AddDays(2), userInitiated: true);
        Assert.IsNotNull(reading, "nothing else was syncing");
        await reading.WaitAsync(Timeout);
        Assert.AreEqual(DestinationSyncState.InSync, Pair(runtime, "cloud").State, Pair(runtime, "cloud").LastError);
        Assert.AreEqual(kept, S3.EarlierVersionBytesIn(Namespace), "the premise: that pass deleted nothing");
        Assert.IsNotNull(RetainedDeletesNotice(runtime), "a pass that deleted nothing does not resolve it");

        // Once the bucket keeps no versions, the next sync's deletes release
        // their space and the notice goes.
        S3.KeepNoVersions(Namespace);
        Harness.WriteSourceFile("docs/d.txt", "written once the bucket kept no versions");
        await BackUpAsync(runtime, now.AddDays(2).AddHours(1));
        Assert.AreEqual(kept, S3.EarlierVersionBytesIn(Namespace), "the premise: nothing more was kept");
        Assert.IsNull(RetainedDeletesNotice(runtime));
    }

    /// <summary>The notice that the bucket kept what its deletes hid, or null.</summary>
    /// <param name="runtime">The service.</param>
    private Notice? RetainedDeletesNotice(ServiceRuntime runtime) =>
        runtime.Notices.Unacknowledged.SingleOrDefault(notice =>
            notice.Key == $"destination-deletes-retained:{Harness.DocsSetId}:cloud");
}
