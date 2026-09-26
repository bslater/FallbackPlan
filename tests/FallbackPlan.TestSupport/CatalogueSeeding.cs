using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Index;

namespace FallbackPlan.TestSupport;

/// <summary>Which of the catalogue's planes a seeding fills.</summary>
[Flags]
public enum CataloguePlanes
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary><c>file_versions</c> and its two indexes.</summary>
    FileVersions = 1,

    /// <summary><c>tree_entries</c> and its two indexes.</summary>
    TreeEntries = 2,

    /// <summary><c>object_locations</c>, <c>ix_locations_blob</c> and the applied-delta ledger.</summary>
    Locations = 4,

    /// <summary><c>segment_dedup</c>.</summary>
    Dedup = 8,

    /// <summary>Every plane a captured file version puts rows into.</summary>
    All = FileVersions | TreeEntries | Locations | Dedup,
}

/// <summary>
/// Seeds a catalogue at the shape reference scale <b>M</b> states —
/// 10 M file versions against 50 M segment references, so five references
/// per version, each version named by one path in one snapshot
/// ([non-functional requirements, reference
/// scales](../../docs/requirements/non-functional.md)).
/// </summary>
/// <remarks>
/// <para>
/// Shared by <c>PerformanceTests/CatalogueSizeBenchmark</c>, which reports
/// the figure, and <c>Repository.Tests/Catalogue/CatalogueSizeTests</c>,
/// which asserts the ceiling: one seeding rather than two models that
/// could drift, so the published number and the pinned ceiling describe
/// the same catalogue.
/// </para>
/// <para>
/// It writes through the same <see cref="Catalogue.RecordFileVersion"/>,
/// <see cref="Catalogue.RecordTreeEntry"/>, <see cref="Catalogue.ApplyDelta"/>
/// and <see cref="Catalogue.RecordSegmentDedup"/> calls publication uses, so
/// what is measured is the schema's cost and not a model's guess at it.
/// </para>
/// <para>
/// Every segment reference is a <b>distinct</b> object, which is the
/// no-dedup upper bound on <c>object_locations</c>: a corpus with
/// cross-version dedup holds fewer rows there for the same reference
/// count. Seeding with <c>segmentsPerVersion: 0</c> gives the floor no
/// corpus can get under — a version, the path that reaches it, and its own
/// manifest's location.
/// </para>
/// </remarks>
public static class CatalogueSeeding
{
    /// <summary>Segment references per file version at scale <b>M</b>: 50 M over 10 M.</summary>
    public const int SegmentsPerVersionAtScaleM = 5;

    /// <summary>The writer every seeded delta is published under.</summary>
    public static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    /// <summary>The repository every seeded catalogue is opened for.</summary>
    public static readonly RepositoryId Repository =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    /// <summary>Fills <paramref name="catalogue"/> with <paramref name="versions"/> file versions.</summary>
    public static void Fill(
        Catalogue catalogue,
        int versions,
        int segmentsPerVersion = SegmentsPerVersionAtScaleM,
        CataloguePlanes planes = CataloguePlanes.All)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentOutOfRangeException.ThrowIfNegative(versions);
        ArgumentOutOfRangeException.ThrowIfNegative(segmentsPerVersion);

        var snapshotId = new byte[16];
        Array.Fill(snapshotId, (byte)0x11);

        var entries = new List<IndexEntry>();
        var sequence = 0UL;

        void Flush()
        {
            if (entries.Count == 0)
            {
                return;
            }

            sequence++;
            catalogue.ApplyDelta(
                DeltaId.FromBytes(SHA256.HashData(BitConverter.GetBytes(sequence)).AsSpan(0, 16).ToArray()),
                new IndexDelta { WriterId = Writer, Sequence = sequence, Generation = 0, Entries = entries });
            entries = [];
        }

        for (var version = 0; version < versions; version++)
        {
            var versionObject = ObjectFrom(version);

            if (planes.HasFlag(CataloguePlanes.FileVersions))
            {
                catalogue.RecordFileVersion(
                    versionObject,
                    System.Text.Encoding.UTF8.GetBytes($"report-{version:D7}.docx"),
                    EntryKind.File,
                    logicalLength: 5_000_000,
                    wholeFileHash: SHA256.HashData(BitConverter.GetBytes((long)version + 1_000_000)),
                    // Scale M's ten versions per file: one capture in ten is a
                    // first, and the rest supersede the version before them.
                    parentVersion: version >= 10 ? ObjectFrom(version - 10) : null,
                    segmentCount: segmentsPerVersion,
                    modifiedAt: 1_700_000_000_000UL + (ulong)version,
                    identityDevice: 2049,
                    identityFileId: (ulong)version + 10_000,
                    metadataDigest: SHA256.HashData(BitConverter.GetBytes((long)version + 2_000_000)));
            }

            if (planes.HasFlag(CataloguePlanes.TreeEntries))
            {
                catalogue.RecordTreeEntry(
                    snapshotId,
                    $"home/user/Documents/Projects/folder-{version / 100:D4}/report-{version:D7}.docx",
                    EntryKind.File,
                    versionObject);
            }

            if (planes.HasFlag(CataloguePlanes.Locations))
            {
                entries.Add(new IndexEntry(
                    versionObject,
                    BlobId.FromBytes(SHA256.HashData(BitConverter.GetBytes(version / 64)).AsSpan(0, 16)),
                    PhysicalOffset: 88,
                    StoredLength: 4096,
                    CompressionProfileValue: 0x0001,
                    EncryptionProfileValue: 0x0001,
                    IndexEntryType.Insertion));
            }

            for (var segment = 0; segment < segmentsPerVersion; segment++)
            {
                var seed = 1_000_000_000L + ((long)version * segmentsPerVersion) + segment;
                var segmentObject = ObjectFrom(seed);

                if (planes.HasFlag(CataloguePlanes.Locations))
                {
                    entries.Add(new IndexEntry(
                        segmentObject,
                        BlobId.FromBytes(SHA256.HashData(BitConverter.GetBytes(seed / 64)).AsSpan(0, 16)),
                        PhysicalOffset: 88 + ((ulong)segment * 1_048_576),
                        StoredLength: 1_048_576,
                        CompressionProfileValue: 0x0001,
                        EncryptionProfileValue: 0x0001,
                        IndexEntryType.Insertion));
                }

                if (planes.HasFlag(CataloguePlanes.Dedup))
                {
                    catalogue.RecordSegmentDedup(ContentFrom(seed), segmentObject);
                }
            }

            if (entries.Count >= 1000)
            {
                Flush();
            }
        }

        Flush();
    }

    /// <summary>
    /// Seeds a throwaway catalogue and answers what it occupies on disk —
    /// every file under its directory, so a journal or a write-ahead log is
    /// counted rather than hidden.
    /// </summary>
    public static long MeasureBytes(
        int versions,
        int segmentsPerVersion = SegmentsPerVersionAtScaleM,
        CataloguePlanes planes = CataloguePlanes.All)
    {
        var root = Path.Combine(Path.GetTempPath(), "fbp-catalogue-size", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            using (var catalogue = Catalogue.Open(Path.Combine(root, "catalogue.db"), Repository))
            {
                Fill(catalogue, versions, segmentsPerVersion, planes);
            }

            // The connection pool keeps the file handle, and on Windows an
            // open handle is what stops the directory being deleted.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            return new DirectoryInfo(root).EnumerateFiles().Sum(file => file.Length);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static ObjectId ObjectFrom(long seed) =>
        ObjectId.FromBytes(SHA256.HashData(BitConverter.GetBytes(seed)));

    private static ContentId ContentFrom(long seed) =>
        ContentId.FromBytes(SHA256.HashData(BitConverter.GetBytes(~seed)));
}
