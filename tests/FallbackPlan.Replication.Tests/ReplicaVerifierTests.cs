using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Replication;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Replication.Tests;

/// <summary>
/// The verifier decides whether a destination is believed, so the distinction
/// it draws between <i>proved nothing</i> and <i>proved everything</i> is
/// load-bearing: a run that could not read its own ground truth must not be
/// spelled like a clean sweep (FR-VER-003). These are the unit-level proofs of
/// that distinction, and of what a replica's silence costs it. The digest
/// tier (FR-VER-001, FR-WOR-003): a sealed blob the service cannot open is
/// proved by hashing its bytes at the replica against the digest the writer
/// signed into the index, and only within a byte budget.
/// </summary>
[TestClass]
public sealed class ReplicaVerifierTests
{
    private string _root = null!;
    private string _sourcePath = null!;
    private string _replicaPath = null!;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fbp-verify-{Guid.NewGuid():N}");
        _sourcePath = Path.Combine(_root, "source");
        _replicaPath = Path.Combine(_root, "replica");
        Directory.CreateDirectory(_sourcePath);
        Directory.CreateDirectory(_replicaPath);
    }

    [TestCleanup]
    public void TearDown()
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
    public async Task Verify_BothSidesHoldTheSameBytes_PassesEverySample()
    {
        var source = new LocalFileSystemObjectStore(_sourcePath);
        var replica = new LocalFileSystemObjectStore(_replicaPath);
        var samples = await SeedAsync(source, replica, count: 3);

        var outcome = await ReplicaVerifier.VerifyAsync(source, replica, samples, CancellationToken.None);

        Assert.AreEqual(samples.Count, outcome.Passed);
        Assert.IsEmpty(outcome.Failed);
        Assert.IsTrue(outcome.ProvedSomething);
    }

    [TestMethod]
    public async Task Verify_TheSourceCannotReadItsOwnBytes_ProvesNothingRatherThanPassing()
    {
        // The guard this exists for: every sample skips, so both counters end
        // at zero — byte-identical to a clean sweep unless the outcome carries
        // the count. A caller that stamped the ledger here would print the
        // verified word over nothing at all.
        var source = new ReadFaultingObjectStore(new LocalFileSystemObjectStore(_sourcePath));
        var replica = new LocalFileSystemObjectStore(_replicaPath);
        var samples = await SeedAsync(new LocalFileSystemObjectStore(_sourcePath), replica, count: 3);
        source.Arm();

        var outcome = await ReplicaVerifier.VerifyAsync(source, replica, samples, CancellationToken.None);

        Assert.AreEqual(0, outcome.Passed);
        Assert.IsEmpty(outcome.Failed);
        Assert.IsFalse(outcome.ProvedSomething, "a run that read no ground truth has proven nothing");
    }

    [TestMethod]
    public async Task Verify_TheReplicaCannotBeRead_FailsEverySample()
    {
        // The replica's inability is the finding — the asymmetry with the case
        // above is the whole point: only the source's silence is excused.
        var source = new LocalFileSystemObjectStore(_sourcePath);
        var replica = new ReadFaultingObjectStore(new LocalFileSystemObjectStore(_replicaPath));
        var samples = await SeedAsync(source, new LocalFileSystemObjectStore(_replicaPath), count: 3);
        replica.Arm();

        var outcome = await ReplicaVerifier.VerifyAsync(source, replica, samples, CancellationToken.None);

        Assert.AreEqual(0, outcome.Passed);
        CollectionAssert.AreEquivalent(samples.Select(sample => sample.Key).ToList(), outcome.Failed.ToList());
    }

    [TestMethod]
    public async Task Verify_TheReplicaHoldsDifferentBytes_NamesTheKey()
    {
        var source = new LocalFileSystemObjectStore(_sourcePath);
        var replica = new LocalFileSystemObjectStore(_replicaPath);
        var samples = await SeedAsync(source, replica, count: 2);

        // One object silently rots at the destination.
        var corrupted = samples[1].Key;
        File.WriteAllBytes(PathFor(_replicaPath, corrupted), new byte[512]);

        var outcome = await ReplicaVerifier.VerifyAsync(source, replica, samples, CancellationToken.None);

        Assert.AreEqual(1, outcome.Passed);
        Assert.AreEqual(corrupted, outcome.Failed.Single());
    }

    [TestMethod]
    public async Task Verify_TheReplicaObjectIsTruncated_IsAFailureNotASkip()
    {
        // A short copy cannot answer the range. Treating that as "no ground
        // truth" would let a destination escape verification by holding less.
        var source = new LocalFileSystemObjectStore(_sourcePath);
        var replica = new LocalFileSystemObjectStore(_replicaPath);
        var samples = await SeedAsync(source, replica, count: 1);

        File.WriteAllBytes(PathFor(_replicaPath, samples[0].Key), new byte[16]);

        var outcome = await ReplicaVerifier.VerifyAsync(source, replica, samples, CancellationToken.None);

        Assert.AreEqual(0, outcome.Passed);
        Assert.AreEqual(samples[0].Key, outcome.Failed.Single());
        Assert.IsFalse(outcome.ProvedSomething);
    }

    [TestMethod]
    public async Task Verify_TheReplicaLacksTheObjectEntirely_IsAFailure()
    {
        var source = new LocalFileSystemObjectStore(_sourcePath);
        var replica = new LocalFileSystemObjectStore(_replicaPath);
        var samples = await SeedAsync(source, replica, count: 1);

        File.Delete(PathFor(_replicaPath, samples[0].Key));

        var outcome = await ReplicaVerifier.VerifyAsync(source, replica, samples, CancellationToken.None);

        Assert.AreEqual(samples[0].Key, outcome.Failed.Single());
    }

    [TestMethod]
    public async Task ProveSealed_ASealedBlobWithASignedDigest_IsProvedByDigest()
    {
        // A write-only set's data blob: the footer opens under the structure
        // key, the records are sealed to a key this side does not hold, and
        // the tag proof stops at the container. The signed whole-blob digest
        // is the independent thing left, and it proves the payload bytes are
        // the ones the writer sealed.
        var replica = new LocalFileSystemObjectStore(_replicaPath);
        var (repository, key, blobId, digest) = await SeedSealedBlobAsync(replica);
        using (repository)
        {
            var outcome = await ReplicaVerifier.ProveSealedAsync(
                replica, [key.Value], repository, CancellationToken.None,
                signedDigestOf: id => id.Equals(blobId) ? digest : null);

            Assert.AreEqual(1, outcome.Passed);
            Assert.AreEqual(1, outcome.Digest, "proved by the digest, and said so");
            Assert.AreEqual(0, outcome.Sealed, "no tag was opened, so none is claimed");
            Assert.IsEmpty(outcome.Failed);
        }
    }

    [TestMethod]
    public async Task ProveSealed_ARottedByteUnderTheSealedContent_FailsByDigest()
    {
        // Rot inside a sealed record: the footer still authenticates, the
        // record's tag would refuse but nobody here can try it, and without
        // the digest tier the blob is neither proved nor failed — a replica
        // quietly holding damaged bytes for ever. The digest catches it.
        var replica = new LocalFileSystemObjectStore(_replicaPath);
        var (repository, key, blobId, digest) = await SeedSealedBlobAsync(replica);
        using (repository)
        {
            var path = PathFor(_replicaPath, key.Value);
            var bytes = File.ReadAllBytes(path);
            bytes[200] ^= 0xFF;
            File.WriteAllBytes(path, bytes);

            var outcome = await ReplicaVerifier.ProveSealedAsync(
                replica, [key.Value], repository, CancellationToken.None,
                signedDigestOf: id => id.Equals(blobId) ? digest : null);

            Assert.AreEqual(0, outcome.Passed);
            Assert.AreEqual(key.Value, outcome.Failed.Single());
        }
    }

    [TestMethod]
    public async Task ProveSealed_NoDigestIsKnown_LeavesTheBlobNeitherProvedNorFailed()
    {
        // Today's posture, kept: a sealed blob with nothing independent to
        // check it against is not damage and is not proof. Proving nothing
        // must still not be spelled like proving everything.
        var replica = new LocalFileSystemObjectStore(_replicaPath);
        var (repository, key, _, _) = await SeedSealedBlobAsync(replica);
        using (repository)
        {
            var outcome = await ReplicaVerifier.ProveSealedAsync(
                replica, [key.Value], repository, CancellationToken.None, signedDigestOf: _ => null);

            Assert.AreEqual(0, outcome.Passed);
            Assert.AreEqual(0, outcome.Digest);
            Assert.IsEmpty(outcome.Failed);
            Assert.IsFalse(outcome.ProvedSomething);
        }
    }

    [TestMethod]
    public async Task ProveSealed_TheByteBudgetIsSpent_LeavesTheBlobUnproved()
    {
        // The tier reads whole blobs, so it is bounded in bytes per run and
        // never loops: a blob the budget cannot cover is left unproved and
        // unblamed, for the next run's cursor to reach.
        var replica = new LocalFileSystemObjectStore(_replicaPath);
        var (repository, key, blobId, digest) = await SeedSealedBlobAsync(replica);
        using (repository)
        {
            var outcome = await ReplicaVerifier.ProveSealedAsync(
                replica, [key.Value], repository, CancellationToken.None,
                signedDigestOf: id => id.Equals(blobId) ? digest : null,
                digestByteBudget: 16);

            Assert.AreEqual(0, outcome.Passed);
            Assert.AreEqual(0, outcome.Digest);
            Assert.IsEmpty(outcome.Failed);
        }
    }

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    /// <summary>
    /// A write-only repository at the replica with one sealed data blob in
    /// it, and the digest the writer would sign into the delta for it.
    /// </summary>
    private async Task<(OpenedRepository Repository, ObjectKey Key, BlobId BlobId, byte[] Digest)> SeedSealedBlobAsync(
        LocalFileSystemObjectStore replica)
    {
        var repository = await RepositoryLifecycle.CreateAsync(
            replica, TestAuthority.Shared.Credential.Clone(), new byte[16], Argon2Parameters.CreationMinimums,
            createdBy: "verifier-tests", 1_722_600_000_000, CancellationToken.None);

        var spool = Path.Combine(_root, "spool");
        Directory.CreateDirectory(spool);
        var structureKey = repository.Keys.DeriveClassKey(BlobClass.Metadata, KeyGeneration.Zero);
        await using var writer = BlobWriter.CreateSealed(
            repository.RepositoryId, Writer, KeyGeneration.Zero, structureKey, repository.Keys.SealingPublicKey,
            blobCounter: 1, EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, spool);

        using var ids = new ObjectIdDeriver(repository.Keys.ContentIdKey);
        for (var i = 0; i < 3; i++)
        {
            var payload = new byte[2048 + i];
            Random.Shared.NextBytes(payload);
            await writer.AppendRecordAsync(
                ObjectType.SegmentRecord, ids.Derive(ObjectType.SegmentRecord, ContentHasher.Hash(payload)),
                CompressionProfile.None, (ulong)payload.Length, payload, CancellationToken.None);
        }

        await using var sealedBlob = await writer.SealAsync(CancellationToken.None);
        using var storeKeys = new StoreBlobKeyDeriver(repository.Keys.KeyIdKey);
        var key = BlobStoreKeys.ForBlob(sealedBlob.BlobClass, storeKeys.Derive(sealedBlob.BlobId));
        var put = await replica.PutAsync(key, sealedBlob.OpenContentAsync, PutConditions.IfNotExists, CancellationToken.None);
        Assert.AreEqual(PutOutcome.Created, put.Outcome);

        // The digest the delta carries is SHA-256 over everything but the
        // sixteen-byte locator (07 §2.2). Computed here from the bytes on
        // disk rather than taken from the writer, so the test agrees with the
        // specification and not merely with the code under test.
        var bytes = File.ReadAllBytes(PathFor(_replicaPath, key.Value));
        var digest = SHA256.HashData(bytes.AsSpan(0, bytes.Length - 16));
        Assert.IsTrue(digest.AsSpan().SequenceEqual([.. sealedBlob.Digest]), "the writer's digest is the locator's preimage");

        return (repository, key, sealedBlob.BlobId, digest);
    }

    /// <summary>Writes matching objects to both stores and returns a sample per object.</summary>
    private static async Task<IReadOnlyList<VerificationSample>> SeedAsync(
        LocalFileSystemObjectStore source, LocalFileSystemObjectStore replica, int count)
    {
        var samples = new List<VerificationSample>();
        for (var index = 0; index < count; index++)
        {
            var key = $"blobs/data/{index:x2}{index:x2}/object-{index}";
            var payload = new byte[512];
            Random.Shared.NextBytes(payload);

            await source.PutAsync(
                ObjectKey.Parse(key), Content(payload), PutConditions.None, CancellationToken.None);
            await replica.PutAsync(
                ObjectKey.Parse(key), Content(payload), PutConditions.None, CancellationToken.None);

            samples.Add(new VerificationSample(key, Offset: 64, Length: 128));
        }

        return samples;
    }

    private static string PathFor(string root, string key) =>
        Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));

    private static Func<CancellationToken, ValueTask<Stream>> Content(byte[] bytes) =>
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
}
