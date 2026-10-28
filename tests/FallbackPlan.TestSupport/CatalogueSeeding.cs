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

    /// <summary><c>file_versions</c> and its identity index.</summary>
    FileVersions = 1,

    /// <summary><c>tree_entries</c>, keyed by snapshot, folder and name.</summary>
    TreeEntries = 2,

    /// <summary><c>object_locations</c>, <c>ix_locations_blob</c> and the applied-delta ledger.</summary>
    Locations = 4,

    /// <summary>Every plane a captured file version puts rows into.</summary>
    All = FileVersions | TreeEntries | Locations,
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
/// <see cref="Catalogue.RecordTreeEntry"/> and <see cref="Catalogue.ApplyDelta"/>
/// calls publication uses, so
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
/// <para>
/// One path in one snapshot per version is the set whose every file
/// changes in every snapshot. <see cref="FillSnapshots"/> seeds the shape a
/// set has when fewer change: every snapshot lists every file, so a file a
/// snapshot left alone still costs it a tree entry.
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
        for (var version = 0; version < versions; version++)
        {
            RecordVersion(catalogue, version, identityFileId: (ulong)version + 10_000, segmentsPerVersion, planes, entries);

            if (planes.HasFlag(CataloguePlanes.TreeEntries))
            {
                catalogue.RecordTreeEntry(snapshotId, PathOf(version), EntryKind.File, ObjectFrom(version));
            }

            if (entries.Count >= 1000)
            {
                Flush(catalogue, entries, ref sequence);
            }
        }

        Flush(catalogue, entries, ref sequence);
    }

    /// <summary>
    /// Fills <paramref name="catalogue"/> with <paramref name="files"/> files
    /// over <paramref name="snapshots"/> snapshots, each of which lists every
    /// file, as a set's snapshots do. The first gives each file a version.
    /// Each after it gives <paramref name="changedPerSnapshot"/> files a new
    /// one, the files taken in turn, and names the rest by the version they
    /// had.
    /// </summary>
    /// <returns>How many file versions the snapshots hold between them.</returns>
    public static int FillSnapshots(
        Catalogue catalogue,
        int files,
        int snapshots,
        int changedPerSnapshot,
        int segmentsPerVersion = SegmentsPerVersionAtScaleM)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(files);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(snapshots);
        ArgumentOutOfRangeException.ThrowIfNegative(changedPerSnapshot);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(changedPerSnapshot, files);
        ArgumentOutOfRangeException.ThrowIfNegative(segmentsPerVersion);

        var current = new int[files];
        var entries = new List<IndexEntry>();
        var sequence = 0UL;
        var versions = 0;
        var turn = 0;
        for (var snapshot = 0; snapshot < snapshots; snapshot++)
        {
            for (var renewed = 0; renewed < (snapshot == 0 ? files : changedPerSnapshot); renewed++)
            {
                var file = snapshot == 0 ? renewed : turn++ % files;
                current[file] = versions;
                RecordVersion(catalogue, versions++, identityFileId: (ulong)file + 10_000, segmentsPerVersion, CataloguePlanes.All, entries);
                if (entries.Count >= 1000)
                {
                    Flush(catalogue, entries, ref sequence);
                }
            }

            var snapshotId = SHA256.HashData(BitConverter.GetBytes(snapshot + 3_000_000)).AsSpan(0, 16).ToArray();
            for (var file = 0; file < files; file++)
            {
                catalogue.RecordTreeEntry(snapshotId, PathOf(file), EntryKind.File, ObjectFrom(current[file]));
            }
        }

        Flush(catalogue, entries, ref sequence);
        return versions;
    }

    /// <summary>
    /// Seeds a throwaway catalogue and answers what it occupies on disk —
    /// every file under its directory, so a journal or a write-ahead log is
    /// counted rather than hidden.
    /// </summary>
    public static long MeasureBytes(
        int versions,
        int segmentsPerVersion = SegmentsPerVersionAtScaleM,
        CataloguePlanes planes = CataloguePlanes.All) =>
        Measure(catalogue => Fill(catalogue, versions, segmentsPerVersion, planes));

    /// <summary>
    /// Seeds a throwaway catalogue as <see cref="FillSnapshots"/> does, and
    /// answers what it occupies on disk and how many versions it holds.
    /// </summary>
    public static (long Bytes, int Versions) MeasureSnapshotBytes(int files, int snapshots, int changedPerSnapshot)
    {
        var versions = 0;
        var bytes = Measure(catalogue => versions = FillSnapshots(catalogue, files, snapshots, changedPerSnapshot));
        return (bytes, versions);
    }

    private static long Measure(Action<Catalogue> fill)
    {
        var root = Path.Combine(Path.GetTempPath(), "fbp-catalogue-size", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            using (var catalogue = Catalogue.Open(Path.Combine(root, "catalogue.db"), Repository))
            {
                fill(catalogue);
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

    /// <summary>
    /// Records version <paramref name="version"/>'s row and queues the
    /// locations of its manifest and its segments, each a distinct object.
    /// </summary>
    private static void RecordVersion(
        Catalogue catalogue,
        int version,
        ulong identityFileId,
        int segmentsPerVersion,
        CataloguePlanes planes,
        List<IndexEntry> entries)
    {
        if (planes.HasFlag(CataloguePlanes.FileVersions))
        {
            catalogue.RecordFileVersion(
                ObjectFrom(version),
                logicalLength: 5_000_000,
                modifiedAt: 1_700_000_000_000UL + (ulong)version,
                identityDevice: 2049,
                identityFileId: identityFileId,
                metadataDigest: SHA256.HashData(BitConverter.GetBytes((long)version + 2_000_000)));
        }

        if (!planes.HasFlag(CataloguePlanes.Locations))
        {
            return;
        }

        entries.Add(new IndexEntry(
            ObjectFrom(version),
            BlobId.FromBytes(SHA256.HashData(BitConverter.GetBytes(version / 64)).AsSpan(0, 16)),
            PhysicalOffset: 88,
            StoredLength: 4096,
            CompressionProfileValue: 0x0001,
            EncryptionProfileValue: 0x0001,
            IndexEntryType.Insertion));

        for (var segment = 0; segment < segmentsPerVersion; segment++)
        {
            var seed = 1_000_000_000L + ((long)version * segmentsPerVersion) + segment;
            entries.Add(new IndexEntry(
                ObjectFrom(seed),
                BlobId.FromBytes(SHA256.HashData(BitConverter.GetBytes(seed / 64)).AsSpan(0, 16)),
                PhysicalOffset: 88 + ((ulong)segment * 1_048_576),
                StoredLength: 1_048_576,
                CompressionProfileValue: 0x0001,
                EncryptionProfileValue: 0x0001,
                IndexEntryType.Insertion));
        }
    }

    /// <summary>Publishes the queued locations as one delta of <see cref="Writer"/>'s.</summary>
    private static void Flush(Catalogue catalogue, List<IndexEntry> entries, ref ulong sequence)
    {
        if (entries.Count == 0)
        {
            return;
        }

        sequence++;
        catalogue.ApplyDelta(
            DeltaId.FromBytes(SHA256.HashData(BitConverter.GetBytes(sequence)).AsSpan(0, 16).ToArray()),
            new IndexDelta { WriterId = Writer, Sequence = sequence, Generation = 0, Entries = [.. entries] });
        entries.Clear();
    }

    private static string PathOf(int file) =>
        $"home/user/Documents/Projects/folder-{file / 100:D4}/report-{file:D7}.docx";

    private static ObjectId ObjectFrom(long seed) =>
        ObjectId.FromBytes(SHA256.HashData(BitConverter.GetBytes(seed)));
}
