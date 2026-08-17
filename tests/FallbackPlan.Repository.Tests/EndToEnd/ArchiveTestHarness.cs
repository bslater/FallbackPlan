using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Storage.Local;
using Microsoft.Data.Sqlite;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Shared fixture for the end-to-end suites: a temp-rooted local store, a
/// deterministic key set, a small-blob policy that forces multiple blobs from
/// a few-mebibyte file, and a seeded test file mixing compressible and
/// incompressible regions.
/// </summary>
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

    protected static RepositoryKeySet CreateKeys() =>
        RepositoryKeySet.FromMasterKey(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

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
