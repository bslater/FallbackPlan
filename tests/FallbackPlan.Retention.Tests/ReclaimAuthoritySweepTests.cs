using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// The authority that deletes, separated from the authority that publishes
/// (ADR-0055; FR-GC-008). A tombstone's signature <em>is</em> the
/// authorisation to remove an object (specification 11 §3), so it is the one
/// signature that must not be producible by a service holding only the right
/// to publish.
/// </summary>
/// <remarks>
/// <para>
/// Two branches, and both are load-bearing. A repository declaring
/// <c>reclaim-authority</c> signs tombstones under the reclaim key; one
/// written before the decision keeps the signing key, and must keep verifying,
/// or the change would strand every tombstone already on disk.
/// </para>
/// <para>
/// The descriptor decides, never the tombstone. That is why these assert
/// against the repository's required features rather than anything inside the
/// object: a per-object discriminator would let whoever writes the object pick
/// the weaker key, which is precisely the downgrade being closed.
/// </para>
/// <para>
/// This does not establish FR-WOR-003 — nothing here provisions a write-only
/// service, and the reclaim grant that lets one collect is its own work.
/// </para>
/// </remarks>
[TestClass]
public sealed class ReclaimAuthoritySweepTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-reclaim-authority-tests", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "reclaim-authority-tests-passphrase!!";
    private static readonly string SetId = new('a', 32);
    private static readonly DateTimeOffset Day1 = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    private static RetentionConfiguration Policy => new() { KeepDaily = 1, MinGenerations = 1 };

    private WriterId Writer => WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId);

    private string ArchivesRoot => Path.Combine(_root, "archives");

    private string RepoPath => Path.Combine(ArchivesRoot, SetId);

    private string StateDirectory => Path.Combine(_root, "state");

    private string SourceRoot => Path.Combine(_root, "source");

    public ReclaimAuthoritySweepTests()
    {
        Directory.CreateDirectory(StateDirectory);
        WriteOnlyInstallation.Provision(StateDirectory, PassphraseText);
        Directory.CreateDirectory(SourceRoot);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "tombstone fodder");

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('d', 32),
                    Name = "vault",
                    Kind = DestinationKind.LocalPath,
                    Path = Directory.CreateDirectory(Path.Combine(_root, "vault")).FullName,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = SetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    [TestMethod]
    public async Task Repository_CreatedToday_RequiresReclaimAuthority()
    {
        await BackUpAsync(Day1);

        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;

        // Required, not optional: an optional feature lets an older collector
        // proceed and accept signing-key tombstones, which is the downgrade
        // the feature exists to stop.
        Assert.Contains(
            RepositoryDescriptorCodec.FeatureReclaimAuthority, repository.Descriptor.RequiredFeatures);
    }

    [TestMethod]
    public async Task Tombstone_UnderTheFeature_IsSignedByTheReclaimKeyAndNotTheSigningKey()
    {
        var tombstone = await WriteOneTombstoneAsync();
        var generation = tombstone.Generation;

        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;

        var signed = tombstone.Decoded.SignedBytes.Span;

        using (var reclaim = RepositorySigner.FromSeed(
            opened.Reclaim.SeedFor(generation), generation))
        {
            Assert.IsTrue(
                reclaim.Verify(signed, tombstone.Signature.Span),
                "a tombstone in a reclaim-authority repository must verify under the reclaim key");
        }

        using var signing = RepositorySigner.Create(repository.Credential, generation);
        Assert.IsFalse(
            signing.Verify(signed, tombstone.Signature.Span),
            "and must NOT verify under the publication key — that is the whole separation");
    }

    [TestMethod]
    public async Task Sweep_ATombstoneSignedUnderThePublicationKey_IsNotActedOn()
    {
        // The attack this closes: something holding only the right to publish
        // forges an authorisation to delete. The sweep must treat it as a
        // security finding rather than as licence.
        await BackUpAsync(Day1);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
        await BackUpAsync(Day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
        await BackUpAsync(Day1.AddDays(2));

        {
            var store = new LocalFileSystemObjectStore(RepoPath);
            var marked = await RunRetentionAsync(store, apply: true, Day1.AddDays(2).AddHours(1));
            Assert.IsGreaterThanOrEqualTo(1, marked.TombstonesWritten);
        }

        // Re-sign every tombstone under the publication key, in place.
        var forged = await ReSignEveryTombstoneUnderTheSigningKeyAsync();
        Assert.IsGreaterThanOrEqualTo(1, forged, "nothing was re-signed, so this proves nothing");

        // The publication the grace waits for, so eligibility is not what
        // stops the delete — the signature is.
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day four content");
        await BackUpAsync(Day1.AddDays(3));

        var after = new LocalFileSystemObjectStore(RepoPath);
        var swept = await RunRetentionAsync(after, apply: true, Day1.AddDays(4));

        Assert.AreEqual(
            0, swept.Swept!.Deleted,
            "a tombstone signed by the publication key is not an authorisation to delete");
    }

    [TestMethod]
    public async Task Tombstone_InARepositoryWithoutTheFeature_StillVerifiesUnderTheSigningKey()
    {
        // The migration rule. Every repository written before this decision
        // has tombstones signed under the publication key, and they must keep
        // being honoured — a security improvement that strands existing
        // deletions would be a data-retention bug.
        await BackUpAsync(Day1);

        var descriptorPath = Path.Combine(RepoPath, RepositoryLifecycle.DescriptorKey.Value);
        var before = await File.ReadAllBytesAsync(descriptorPath);
        var parsed = RepositoryDescriptorCodec.Parse(before);
        Assert.IsInstanceOfType<DescriptorParseResult.Ok>(parsed, out var ok);

        await File.WriteAllBytesAsync(
            descriptorPath,
            RepositoryDescriptorCodec.Serialize(ok.Descriptor with { RequiredFeatures = [] }));

        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;

        Assert.IsEmpty(repository.Descriptor.RequiredFeatures);

        // A repository that makes no claim gets the old key, so a tombstone
        // written by an older build reads exactly as it always did.
        var generation = new KeyGeneration(0);
        using var signing = RepositorySigner.Create(repository.Credential, generation);
        using var reclaim = RepositorySigner.FromSeed(
            opened.Reclaim.SeedFor(generation), generation);

        var payload = "an older build's tombstone"u8.ToArray();
        var signature = signing.Sign(payload);

        Assert.IsTrue(signing.Verify(payload, signature));
        Assert.IsFalse(
            reclaim.Verify(payload, signature),
            "the two keys must stay distinguishable, or the compatibility branch means nothing");
    }

    private static async Task<(DecodedTombstone Decoded, ReadOnlyMemory<byte> Signature, KeyGeneration Generation)>
        ReadOneTombstoneAsync(LocalFileSystemObjectStore store, OpenedRepository repository)
    {
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("tombstones/"), ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory, CancellationToken.None);

            var record = StandaloneRecordFraming.Parse(memory.ToArray());
            var metadataKey = repository.Credential.DeriveMetadataKey(record.KeyGeneration);
            try
            {
                Assert.IsTrue(StandaloneRecordCipher.TryOpen(
                    record, repository.RepositoryId, metadataKey, out var plaintext));
                var decoded = TombstoneCodec.Decode(plaintext!);
                return (decoded, decoded.Signature, record.KeyGeneration);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(metadataKey);
            }
        }

        Assert.Fail("the repository holds no tombstone");
        throw new InvalidOperationException();
    }

    private async Task<(DecodedTombstone Decoded, ReadOnlyMemory<byte> Signature, KeyGeneration Generation)>
        WriteOneTombstoneAsync()
    {
        await BackUpAsync(Day1);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
        await BackUpAsync(Day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
        await BackUpAsync(Day1.AddDays(2));

        var store = new LocalFileSystemObjectStore(RepoPath);
        var report = await RunRetentionAsync(store, apply: true, Day1.AddDays(2).AddHours(1));
        Assert.IsGreaterThanOrEqualTo(1, report.TombstonesWritten, "nothing was tombstoned, so this proves nothing");

        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;
        return await ReadOneTombstoneAsync(store, repository);
    }

    private async Task<int> ReSignEveryTombstoneUnderTheSigningKeyAsync()
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;

        var keys = new List<ObjectKey>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("tombstones/"), ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key);
        }

        var rewritten = 0;
        foreach (var key in keys)
        {
            byte[] sealedBytes;
            using (var read = await store.OpenReadAsync(key, range: null, CancellationToken.None))
            using (var memory = new MemoryStream())
            {
                await read.Content!.CopyToAsync(memory, CancellationToken.None);
                sealedBytes = memory.ToArray();
            }

            var record = StandaloneRecordFraming.Parse(sealedBytes);
            var metadataKey = repository.Credential.DeriveMetadataKey(record.KeyGeneration);
            byte[] resealed;
            try
            {
                Assert.IsTrue(StandaloneRecordCipher.TryOpen(
                    record, repository.RepositoryId, metadataKey, out var plaintext));
                var decoded = TombstoneCodec.Decode(plaintext!);

                byte[] encoded;
                using (var signer = RepositorySigner.Create(repository.Credential, record.KeyGeneration))
                {
                    encoded = TombstoneCodec.Encode(
                        decoded.Value, signer.Sign(decoded.SignedBytes.Span));
                }

                // Re-derived, because the identifier is content-derived and
                // the content just changed: an attacker re-signing would have
                // to do exactly this too.
                var contentIdKey = repository.Credential.ContentIdKey.ToArray();
                Domain.Identifiers.ObjectId objectId;
                try
                {
                    objectId = new ObjectIdDeriver(contentIdKey).Derive(
                        ObjectType.Tombstone, ContentHasher.Hash(encoded));
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(contentIdKey);
                }

                resealed = StandaloneRecordCipher.Seal(
                    repository.RepositoryId, metadataKey, record.KeyGeneration, record.WriterId,
                    counter: record.Counter, ObjectType.Tombstone, objectId, encoded);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(metadataKey);
            }

            await store.DeleteAsync(key, DeleteConditions.None, CancellationToken.None);
            await store.PutAsync(
                key,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(resealed, writable: false)),
                PutConditions.None,
                CancellationToken.None);
            rewritten++;
        }

        return rewritten;
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        var result = await AgentPass.RunAsync(ArchivesRoot, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private async Task<RetentionReport> RunRetentionAsync(
        IObjectStore store, bool apply, DateTimeOffset now)
    {
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;

        var sync = DestinationSyncStore.Open(StateDirectory);
        return await RetentionRunner.RunAsync(
            store, repository, Policy, [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name), _ => TrimVerification.None, Writer, apply,
            (ulong)now.ToUnixTimeMilliseconds(), CancellationToken.None, reclaim: opened.Reclaim);
    }
}
