using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A snapshot built by hand whose one file lists two reference runs
/// (ADR-0202), for the tests that readers follow them: no writer in this build
/// writes runs. The first run is the file's first segment and 262,143
/// one-byte holes, which is full, and the second is its second segment. The
/// segments are archived as any are, each in a data blob of its own, and the
/// runs are written to a metadata blob of their own, so that damage to either
/// is told apart from damage to the tree and the manifest.
/// </summary>
public abstract class ReferenceRunHarness : ArchiveTestHarness
{
    protected const int Holes = ReferenceRunCodec.MaxPieces - 1;

    protected static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0x5A, 16)];

    private static byte[] Random(int length, byte seed)
    {
        var bytes = new byte[length];
        new System.Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>What the hand-built snapshot is made of, and the file its version restores to.</summary>
    protected sealed record Built(
        SnapshotManifest Snapshot,
        FileVersionManifest Manifest,
        byte[] Content,
        ObjectId Version,
        ObjectId FirstRun,
        ObjectId SecondRun,
        ObjectId FirstSegment,
        ObjectId SecondSegment,
        ArchivedBlob FirstData,
        ArchivedBlob SecondData,
        ArchivedBlob Runs,
        IReadOnlyList<ArchivedBlob> Blobs);

    /// <summary>
    /// Writes the snapshot. <paramref name="writeSecondRun"/> false lists the
    /// second run in the manifest and never writes it;
    /// <paramref name="gapBeforeSecondRun"/> starts the second run a byte
    /// after the first ends, a run sound on its own that does not follow.
    /// </summary>
    protected async Task<Built> BuildAsync(
        LocalFileSystemObjectStore store, RepositoryKeySet keys, bool writeSecondRun = true, bool gapBeforeSecondRun = false)
    {
        var first = Random(40_000, 1);
        var second = Random(30_000, 2);
        ArchiveResult firstArchive;
        using (var source = new MemoryStream(first))
        {
            firstArchive = await CreateArchiver(store, keys, SmallBlobPolicy, firstCounter: 1).ArchiveAsync(source, CancellationToken.None);
        }

        ArchiveResult secondArchive;
        using (var source = new MemoryStream(second))
        {
            secondArchive = await CreateArchiver(store, keys, SmallBlobPolicy, firstCounter: 100).ArchiveAsync(source, CancellationToken.None);
        }

        var firstSegment = Assert.ContainsSingle(firstArchive.SegmentReferences);
        var secondSegment = Assert.ContainsSingle(secondArchive.SegmentReferences);
        var holesStart = (ulong)first.Length;
        var secondStart = holesStart + Holes;
        var length = secondStart + (ulong)second.Length;

        var firstRun = new ReferenceRun
        {
            Start = 0,
            Length = secondStart,
            SegmentReferences = [firstSegment],
            SparseExtents = [.. Enumerable.Range(0, Holes).Select(hole => new SparseExtent(holesStart + (ulong)hole, 1))],
        };
        var secondRunStart = gapBeforeSecondRun ? secondStart + 1 : secondStart;
        var secondRun = new ReferenceRun
        {
            Start = secondRunStart,
            Length = (ulong)second.Length,
            SegmentReferences = [new SegmentReference((long)secondRunStart, second.Length, secondSegment.ObjectId)],
        };

        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        whole.AppendData(first);
        whole.AppendData(new byte[Holes]);
        whole.AppendData(second);

        var builder = new ManifestBuilder(
            Repo, Writer, KeyGeneration.Zero, keys, store,
            new MonotonicBlobCounterAllocator(500), SpoolDirectory, BlobWriteProfile.LocalDefault,
            FormatVersions.SealedDataPlane);
        await using (builder.ConfigureAwait(false))
        {
            var firstRunId = await builder.AppendManifestAsync(ObjectType.ReferenceRun, ReferenceRunCodec.Encode(firstRun), CancellationToken.None);
            var secondRunBytes = ReferenceRunCodec.Encode(secondRun);
            var secondRunId = writeSecondRun
                ? await builder.AppendManifestAsync(ObjectType.ReferenceRun, secondRunBytes, CancellationToken.None)
                : DeriveRunId(keys, secondRunBytes);
            await builder.FlushAsync(CancellationToken.None);

            var manifest = new FileVersionManifest
            {
                EntryKind = EntryKind.File,
                Name = "huge.bin"u8.ToArray(),
                NameNormalisation = NameNormalisation.Nfc,
                LogicalLength = length,
                SegmentReferences = [],
                ReferenceRuns = [firstRunId, secondRunId],
                WholeFileHash = whole.GetHashAndReset(),
                SegmentationProfile = SegmentationProfile.FixedV1.Value,
                Metadata = new EntryMetadata { ModifiedAt = 1_722_600_000_000 },
            };
            var version = await builder.AppendManifestAsync(
                ObjectType.FileVersionManifest, FileVersionManifestCodec.Encode(manifest), CancellationToken.None);
            var tree = await builder.AppendManifestAsync(
                ObjectType.TreeManifest,
                TreeManifestCodec.Encode(new TreeManifest { Entries = [new TreeEntry("huge.bin"u8.ToArray(), version, EntryKind.File)] }),
                CancellationToken.None);
            await builder.FlushAsync(CancellationToken.None);

            var snapshot = new SnapshotManifest
            {
                SnapshotId = SnapshotId,
                DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                CaptureStartedAt = 1_722_600_000_000,
                CaptureCompletedAt = 1_722_600_001_000,
                RootTree = tree,
                PolicyManifest = ObjectId.FromBytes(Enumerable.Repeat((byte)9, 32).ToArray()),
                ConsistencyMethod = 1,
                CaptureStatus = 1,
                SourceFilesystem = new SourceFilesystem(true, true, "ext4"),
                PublicationGeneration = 0,
                ClientVersion = "reference-run-harness/1.0",
            };
            await builder.WriteStandaloneSnapshotAsync(
                snapshot, SnapshotManifestCodec.Encode(snapshot, new byte[64]), intentSequence: 601, CancellationToken.None);

            var firstData = Assert.ContainsSingle(firstArchive.Blobs);
            var secondData = Assert.ContainsSingle(secondArchive.Blobs);
            return new Built(
                snapshot, manifest, [.. first, .. new byte[Holes], .. second], version, firstRunId, secondRunId, firstSegment.ObjectId, secondSegment.ObjectId,
                firstData, secondData, builder.Blobs[0], [firstData, secondData, .. builder.Blobs]);
        }
    }

    private static ObjectId DeriveRunId(RepositoryKeySet keys, byte[] encoded)
    {
        using var deriver = new FallbackPlan.Repository.Crypto.ObjectIdDeriver(keys.ContentIdKey);
        return deriver.Derive(ObjectType.ReferenceRun, FallbackPlan.Repository.Crypto.ContentHasher.Hash(encoded));
    }
}
