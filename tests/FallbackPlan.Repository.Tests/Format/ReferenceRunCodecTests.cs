using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Cbor;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// Format 8's reference runs (ADR-0202; FR-ARCH-002, FR-ARCH-010): a run holds
/// one contiguous stretch of a file's segment references and holes, covers it
/// exactly, and holds at most 262,144 pieces, and a file version manifest that
/// lists runs in key 15 holds no references or holes of its own. A file's runs
/// start at 0, each where the one before ended, end at its length, and each
/// but the last is full, so the same file is the same objects on every device.
/// </summary>
[TestClass]
public sealed class ReferenceRunCodecTests
{
    private const int Full = ReferenceRunCodec.MaxPieces;

    private static ObjectId Id(int seed)
    {
        var bytes = new byte[32];
        BitConverter.TryWriteBytes(bytes, seed);
        bytes[31] = 0xAB;
        return ObjectId.FromBytes(bytes);
    }

    /// <summary>A run of <paramref name="pieces"/> one-byte segments from <paramref name="start"/>.</summary>
    private static ReferenceRun Run(ulong start, int pieces) => new()
    {
        Start = start,
        Length = (ulong)pieces,
        SegmentReferences = [.. Enumerable.Range(0, pieces).Select(index => new SegmentReference((long)start + index, 1, Id(index)))],
    };

    private static FileVersionManifest Head(ulong length, params ObjectId[] runs) => new()
    {
        EntryKind = EntryKind.File,
        Name = "huge.bin"u8.ToArray(),
        NameNormalisation = NameNormalisation.Nfc,
        LogicalLength = length,
        SegmentReferences = [],
        ReferenceRuns = runs,
        WholeFileHash = SHA256.HashData("huge"u8),
        SegmentationProfile = 0x0001,
        Metadata = new EntryMetadata { ModifiedAt = 1_722_600_000_000 },
    };

    [TestMethod]
    public void ARun_EncodedAndDecoded_RoundTripsByteIdentically_HolesAndAll()
    {
        var run = new ReferenceRun
        {
            Start = 1_000,
            Length = 300,
            SegmentReferences = [new SegmentReference(1_000, 100, Id(1)), new SegmentReference(1_150, 150, Id(2))],
            SparseExtents = [new SparseExtent(1_100, 50)],
        };

        var encoded = ReferenceRunCodec.Encode(run);
        var decoded = ReferenceRunCodec.Decode(encoded);

        Assert.AreEqual(run.Start, decoded.Start);
        Assert.AreEqual(run.Length, decoded.Length);
        CollectionAssert.AreEqual(run.SegmentReferences.ToArray(), decoded.SegmentReferences.ToArray());
        CollectionAssert.AreEqual(run.SparseExtents.ToArray(), decoded.SparseExtents.ToArray());
        SequenceAssert.AreEqual(encoded, ReferenceRunCodec.Encode(decoded));
    }

    [TestMethod]
    [DataRow(1_000UL, 299UL, DisplayName = "falls short of its stretch")]
    [DataRow(1_000UL, 301UL, DisplayName = "claims more than its pieces cover")]
    [DataRow(999UL, 301UL, DisplayName = "starts before its first piece")]
    public void ARunWhosePiecesDoNotCoverItsStretchExactly_IsRefused(ulong start, ulong length)
    {
        var run = new ReferenceRun
        {
            Start = start,
            Length = length,
            SegmentReferences = [new SegmentReference(1_000, 100, Id(1)), new SegmentReference(1_100, 200, Id(2))],
        };

        Assert.ThrowsExactly<ManifestValidationException>(() => ReferenceRunCodec.Encode(run));
    }

    [TestMethod]
    public void ARunWithAGapOrAnOverlap_IsRefused()
    {
        var gap = new ReferenceRun
        {
            Start = 0,
            Length = 300,
            SegmentReferences = [new SegmentReference(0, 100, Id(1)), new SegmentReference(150, 150, Id(2))],
        };
        var overlap = gap with { SegmentReferences = [new SegmentReference(0, 200, Id(1)), new SegmentReference(150, 150, Id(2))] };

        Assert.ThrowsExactly<ManifestValidationException>(() => ReferenceRunCodec.Encode(gap));
        Assert.ThrowsExactly<ManifestValidationException>(() => ReferenceRunCodec.Encode(overlap));
    }

    [TestMethod]
    public void ARunWhoseReferencesAreOutOfOrder_IsRefused_ThoughTheyCoverItsStretch()
    {
        var run = new ReferenceRun
        {
            Start = 0,
            Length = 300,
            SegmentReferences = [new SegmentReference(100, 200, Id(2)), new SegmentReference(0, 100, Id(1))],
        };

        Assert.ThrowsExactly<ManifestValidationException>(() => ReferenceRunCodec.Encode(run));
    }

    [TestMethod]
    public void ARunOfMoreThanItsPieces_OrOfNone_IsRefused()
    {
        Assert.AreEqual(262_144, ReferenceRunCodec.MaxPieces);
        _ = ReferenceRunCodec.Encode(Run(0, Full));
        Assert.ThrowsExactly<ManifestValidationException>(() => ReferenceRunCodec.Encode(Run(0, Full + 1)));
        Assert.ThrowsExactly<ManifestValidationException>(() => ReferenceRunCodec.Encode(new ReferenceRun
        {
            Start = 0,
            Length = 0,
            SegmentReferences = [],
        }));
    }

    [TestMethod]
    public void ARunCarryingAnUnknownKey_IsRefused()
    {
        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(1);
        writer.WriteKey(5);
        writer.WriteUnsignedInteger(1);
        writer.WriteEndMap();

        Assert.ThrowsExactly<ManifestValidationException>(() => ReferenceRunCodec.Decode(writer.Encode()));
    }

    [TestMethod]
    public void AManifestListingRuns_RoundTrips_AndCarriesThemInKeyFifteen()
    {
        var head = Head(2 * (ulong)Full + 10, Id(-1), Id(-2), Id(-3));

        var encoded = FileVersionManifestCodec.Encode(head);
        var decoded = FileVersionManifestCodec.Decode(encoded);

        CollectionAssert.AreEqual(head.ReferenceRuns.ToArray(), decoded.ReferenceRuns.ToArray());
        Assert.IsEmpty(decoded.SegmentReferences);
        SequenceAssert.AreEqual(encoded, FileVersionManifestCodec.Encode(decoded));
    }

    [TestMethod]
    public void AManifestWithoutRuns_ListsNone_AndEncodesNoKeyFifteen()
    {
        var plain = Head(3) with
        {
            ReferenceRuns = [],
            SegmentReferences = [new SegmentReference(0, 3, Id(7))],
        };

        var decoded = FileVersionManifestCodec.Decode(FileVersionManifestCodec.Encode(plain));

        Assert.IsEmpty(decoded.ReferenceRuns);
        Assert.HasCount(1, decoded.SegmentReferences);
    }

    [TestMethod]
    public void AManifestListingRuns_AndHoldingReferencesOrHolesOfItsOwn_IsRefused()
    {
        var withReferences = Head(2 * (ulong)Full, Id(-1), Id(-2)) with
        {
            SegmentReferences = [new SegmentReference(0, 1, Id(1))],
        };
        var withHoles = Head(2 * (ulong)Full, Id(-1), Id(-2)) with { SparseExtents = [new SparseExtent(0, 1)] };

        Assert.ThrowsExactly<ManifestValidationException>(() => FileVersionManifestCodec.Encode(withReferences));
        Assert.ThrowsExactly<ManifestValidationException>(() => FileVersionManifestCodec.Encode(withHoles));
    }

    [TestMethod]
    public void AManifestListingOneRun_OrRunsForALink_IsRefused()
    {
        // One run holds at most what one manifest may, so a file of one run
        // is described by one manifest.
        var one = Assert.ThrowsExactly<ManifestValidationException>(() => FileVersionManifestCodec.Encode(Head(10, Id(-1))));
        Assert.Contains("two or more", one.Message, StringComparison.Ordinal);

        var link = Head(0, Id(-1), Id(-2)) with { EntryKind = EntryKind.Symlink, LinkTarget = "elsewhere"u8.ToArray() };
        Assert.ThrowsExactly<ManifestValidationException>(() => FileVersionManifestCodec.Encode(link));
    }

    [TestMethod]
    public void AFilesRuns_InOrder_FromZeroToItsLength_EachButTheLastFull_AreItsReferences()
    {
        var first = Run(0, Full);
        var second = Run(Full, Full);
        var last = Run(2 * (ulong)Full, 7);
        var head = Head(2 * (ulong)Full + 7, Id(-1), Id(-2), Id(-3));

        ReferenceRunCodec.ValidateSequence(head, [first, second, last]);
    }

    [TestMethod]
    public void AFilesRuns_ThatDoNotJoin_OrStopShort_OrArentFullBeforeTheLast_AreRefused()
    {
        var head = Head(2 * (ulong)Full + 7, Id(-1), Id(-2), Id(-3));

        // A stretch missing between the second run and the last.
        Assert.ThrowsExactly<ManifestValidationException>(() =>
            ReferenceRunCodec.ValidateSequence(head, [Run(0, Full), Run(Full, Full), Run((2 * (ulong)Full) + 1, 6)]));

        // The runs end before the file does.
        Assert.ThrowsExactly<ManifestValidationException>(() =>
            ReferenceRunCodec.ValidateSequence(head with { LogicalLength = 2 * (ulong)Full + 8 }, [Run(0, Full), Run(Full, Full), Run(2 * (ulong)Full, 7)]));

        // A run before the last holds fewer than a run may, so the same file
        // would be different objects on another device.
        Assert.ThrowsExactly<ManifestValidationException>(() =>
            ReferenceRunCodec.ValidateSequence(head, [Run(0, Full - 1), Run(Full - 1, Full), Run((2 * (ulong)Full) - 1, 8)]));

        // A run in the middle that is not full is refused as the first is.
        Assert.ThrowsExactly<ManifestValidationException>(() =>
            ReferenceRunCodec.ValidateSequence(head, [Run(0, Full), Run(Full, Full - 1), Run((2 * (ulong)Full) - 1, 8)]));

        // As many runs as the manifest lists, no more and no fewer, even when
        // those there are cover the file.
        Assert.ThrowsExactly<ManifestValidationException>(() =>
            ReferenceRunCodec.ValidateSequence(head with { LogicalLength = 2 * (ulong)Full }, [Run(0, Full), Run(Full, Full)]));
    }

    /// <summary>
    /// <paramref name="data"/> one-byte references with a one-byte hole after
    /// each, the last's left off when <paramref name="holes"/> is one fewer,
    /// starting with a hole of its own when <paramref name="leadingHole"/>.
    /// </summary>
    private static (List<SegmentReference> References, List<SparseExtent> Holes, ulong Length) Alternating(
        int data, int holes, bool leadingHole = false)
    {
        List<SegmentReference> references = [];
        List<SparseExtent> extents = [];
        ulong at = 0;
        if (leadingHole)
        {
            extents.Add(new SparseExtent(at++, 1));
        }

        for (var stretch = 0; stretch < data; stretch++)
        {
            references.Add(new SegmentReference((long)at++, 1, Id(stretch % 7)));
            if (stretch < holes)
            {
                extents.Add(new SparseExtent(at++, 1));
            }
        }

        return (references, extents, at);
    }

    [TestMethod]
    public void AFileOfNoMorePiecesThanARunHolds_IsCutIntoNoRuns()
    {
        var (references, holes, length) = Alternating(Full / 2, Full / 2);

        Assert.IsEmpty(ReferenceRunCodec.Cut(references, holes, length));
    }

    [TestMethod]
    public void AFileOfOnePieceMore_IsCutIntoAFullRunAndARunOfThatPiece()
    {
        var (references, holes, length) = Alternating((Full / 2) + 1, Full / 2);

        var runs = ReferenceRunCodec.Cut(references, holes, length).ToList();

        Assert.HasCount(2, runs);
        Assert.AreEqual(0ul, runs[0].Start);
        Assert.AreEqual((ulong)Full, runs[0].Length);
        Assert.HasCount(Full / 2, runs[0].SegmentReferences);
        Assert.HasCount(Full / 2, runs[0].SparseExtents);
        Assert.AreEqual((ulong)Full, runs[1].Start);
        Assert.AreEqual(1ul, runs[1].Length);
        Assert.AreEqual(references[^1], Assert.ContainsSingle(runs[1].SegmentReferences));
        Assert.IsEmpty(runs[1].SparseExtents);
    }

    [TestMethod]
    public void AFilesRuns_AsCut_TakeItsPiecesInOrder_AHoleFirstAmongThem_AndAreItsRunsByEveryRule()
    {
        // A hole at 0 is the first run's first piece; two runs and a third
        // ending with a hole, every rule a reader holds a file's runs to.
        var (references, holes, length) = Alternating(Full + 10, Full + 10, leadingHole: true);

        var runs = ReferenceRunCodec.Cut(references, holes, length).ToList();

        Assert.HasCount(3, runs);
        Assert.AreEqual(new SparseExtent(0, 1), runs[0].SparseExtents[0]);
        Assert.AreEqual(1L, runs[0].SegmentReferences[0].LogicalOffset);
        CollectionAssert.AreEqual(references, runs.SelectMany(run => run.SegmentReferences).ToList());
        CollectionAssert.AreEqual(holes, runs.SelectMany(run => run.SparseExtents).ToList());
        Assert.AreEqual(length, runs[^1].Start + runs[^1].Length);
        foreach (var run in runs)
        {
            _ = ReferenceRunCodec.Encode(run);
        }

        var ids = Enumerable.Range(0, runs.Count).Select(index => Id(1_000 + index)).ToArray();
        ReferenceRunCodec.ValidateSequence(Head(length, ids), runs);
    }

    [TestMethod]
    public void PiecesThatStopShortOfTheFile_AreRefusedAsTheyAreCut_BeforeTheLastRunIsGiven()
    {
        // A manifest that lists runs holds no pieces of its own, so the cut is
        // where a writer's pieces are last held to the file's length.
        var (references, holes, length) = Alternating((Full / 2) + 1, Full / 2);

        using var runs = ReferenceRunCodec.Cut(references, holes, length + 5).GetEnumerator();
        Assert.IsTrue(runs.MoveNext(), "the full run is given");
        var refused = Assert.ThrowsExactly<ManifestValidationException>(() => runs.MoveNext());
        Assert.Contains(length.ToString(System.Globalization.CultureInfo.InvariantCulture), refused.Message, StringComparison.Ordinal);
    }
}
