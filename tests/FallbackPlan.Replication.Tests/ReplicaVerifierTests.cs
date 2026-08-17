using FallbackPlan.Replication;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Replication.Tests;

/// <summary>
/// The verifier decides whether a destination is believed, so the distinction
/// it draws between <i>proved nothing</i> and <i>proved everything</i> is
/// load-bearing: a run that could not read its own ground truth must not be
/// spelled like a clean sweep (FR-VER-003). These are the unit-level proofs of
/// that distinction, and of what a replica's silence costs it.
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
