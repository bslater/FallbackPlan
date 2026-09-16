using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using Microsoft.Data.Sqlite;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Shared fixture for the end-to-end suites: a temp-rooted local store, a
/// deterministic key set, a small-blob policy that forces multiple blobs from
/// a few-mebibyte file, and a seeded test file mixing compressible and
/// incompressible regions.
/// </summary>
/// <remarks>
/// The keys are a write-only bundle over <see cref="TestAuthority"/>'s pinned
/// root, so every suite here archives sealed data blobs — which is the only
/// shape the product writes. Reading content back therefore needs the
/// derived scalar: readers take <see cref="Authority"/>, and a blob opened
/// by hand takes <see cref="OpenContentKey"/>. The dedup trust domain is
/// the device domain, because the repository domain verifies another
/// writer's segments by reading their content and a write-only holder
/// cannot (ADR-0042 §7).
/// </remarks>
public abstract class ArchiveTestHarness : IDisposable
{
    protected static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    protected static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-e2e-tests", Guid.NewGuid().ToString("n"));

    /// <summary>64 KiB segments, tiny blob targets: a ~3 MiB file spans many blobs.</summary>
    protected static CapturePolicy SmallBlobPolicy { get; } = CapturePolicy.Default with
    {
        DedupTrustDomain = DedupTrustDomain.Device,
        SegmentSize = SegmentSize.Create(64 * 1024),
        BlobWriteProfile = BlobWriteProfile.LocalDefault with
        {
            TargetSizeBytes = 256 * 1024,
            MaximumSizeBytes = 512 * 1024,
        },
    };

    /// <summary>
    /// cdc-v1 at the conformance-vector parameters (target 64 KiB, min 8 KiB,
    /// max 512 KiB), with blob targets sized so the maximum segment still
    /// fits one blob.
    /// </summary>
    protected static CapturePolicy CdcPolicy { get; } = CapturePolicy.Default with
    {
        DedupTrustDomain = DedupTrustDomain.Device,
        SegmentationProfile = Domain.Profiles.SegmentationProfile.CdcV1,
        CdcParameters = CdcParameters.Create(64 * 1024, 8 * 1024, 512 * 1024),
        BlobWriteProfile = BlobWriteProfile.LocalDefault with
        {
            TargetSizeBytes = 1024 * 1024,
            MaximumSizeBytes = 2 * 1024 * 1024,
        },
    };

    /// <summary>The store root on disk — corruption probes reach beneath the interface here.</summary>
    protected string StoreRoot => Path.Combine(_root, "store");

    /// <summary>The archiver spool directory.</summary>
    protected string SpoolDirectory => Path.Combine(_root, "spool");

    protected LocalFileSystemObjectStore CreateStore() => new(StoreRoot);

    private static readonly byte[] SealingPrivateKey = TestAuthority.Shared.SealingPrivateKey.ToArray();

    protected static RepositoryKeySet CreateKeys() =>
        RepositoryKeySet.FromWriteCredential(TestAuthority.Shared.Credential);

    protected static RepositoryWriteCredential CreateCredential() => TestAuthority.Shared.Credential.Clone();

    /// <summary>The read authority a reader needs to open sealed content; shared, never disposed.</summary>
    protected static RepositoryReadAuthority Authority => TestAuthority.Shared;

    /// <summary>Opens a sealed data blob's content key — for a <see cref="BlobReader"/> opened by hand.</summary>
    protected static byte[] OpenContentKey(BlobEnvelope envelope) =>
        SealedContentKey.Open(SealingPrivateKey, envelope.SealedContentKey, Repo, envelope.BlobId);

    protected FileArchiver CreateArchiver(LocalFileSystemObjectStore store, RepositoryKeySet keys) =>
        CreateArchiver(store, keys, SmallBlobPolicy, firstCounter: 1);

    protected FileArchiver CreateArchiver(
        LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        CapturePolicy policy,
        ulong firstCounter) => new(
        policy,
        Repo,
        Writer,
        KeyGeneration.Zero,
        keys,
        store,
        new MonotonicBlobCounterAllocator(firstCounter),
        SpoolDirectory);

    /// <summary>
    /// A deterministic ~3 MiB file: alternating 128 KiB regions of repeated
    /// ASCII (highly compressible) and seeded pseudo-random bytes
    /// (incompressible), so the per-record threshold decision goes both ways
    /// in one archive run.
    /// </summary>
    protected static byte[] BuildTestFile(int seed = 20260803, int regions = 24)
    {
        var random = new Random(seed);
        var file = new MemoryStream();
        var pattern = "FallbackPlan end-to-end segment content. "u8.ToArray();

        for (var region = 0; region < regions; region++)
        {
            var buffer = new byte[128 * 1024];

            if (region % 2 == 0)
            {
                for (var i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = pattern[i % pattern.Length];
                }
            }
            else
            {
                random.NextBytes(buffer);
            }

            file.Write(buffer);
        }

        return file.ToArray();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Deletes the temp root.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || !Directory.Exists(_root))
        {
            return;
        }

        // Connection pooling can keep a catalogue file open past its owning
        // connection's dispose, and on Windows an open handle fails the
        // recursive delete — a cleanup failure the test then wears as its
        // own. Drain the pools first, and treat a residual refusal as best
        // effort: the temp root is per-test-class and leaks nothing that
        // matters more than the assertion that already ran.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
