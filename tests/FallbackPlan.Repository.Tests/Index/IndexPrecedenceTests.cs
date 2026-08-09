using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// The 07 §3 precedence rules, verbatim (FR-MAN-013, FR-MAN-015; ADR-0017;
/// the conformance README's demonstration 7): highest generation wins,
/// <c>(writer_id, sequence)</c> breaks ties, a deleted-blob winner is
/// superseded and reported, and any application order converges (07 §6 —
/// exit criterion 9's index half).
/// </summary>
[TestClass]
public sealed class IndexPrecedenceTests
{
    private static ObjectId Object(byte seed)
    {
        var bytes = new byte[32];
        Array.Fill(bytes, seed);
        return ObjectId.FromBytes(bytes);
    }

    private static BlobId Blob(byte seed)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, seed);
        return BlobId.FromBytes(bytes);
    }

    private static WriterId Writer(byte seed)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, seed);
        return WriterId.FromBytes(bytes);
    }

    private static ProvenancedEntry Entry(
        byte blobSeed, ulong generation, byte writerSeed, ulong sequence,
        IndexEntryType type = IndexEntryType.Insertion) =>
        new(
            new IndexEntry(Object(1), Blob(blobSeed), 100, 200, 0, 1, type),
            generation,
            Writer(writerSeed),
            sequence);

    [TestMethod]
    public void IndexPrecedence_EntriesAtDifferentGenerations_ChoosesTheHighest()
    {
        var older = Entry(blobSeed: 1, generation: 1, writerSeed: 9, sequence: 500);
        var newer = Entry(blobSeed: 2, generation: 2, writerSeed: 1, sequence: 1, IndexEntryType.Supersession);

        var findings = new List<DamageFinding>();
        var winner = IndexPrecedence.Resolve([older, newer], _ => BlobState.Live, findings);

        // Compaction republished the object at generation 2: that entry wins
        // even against a higher (writer, sequence) at generation 1.
        Assert.AreEqual(Blob(2), winner!.Entry.BlobId);
        Assert.IsEmpty(findings);
    }

    [TestMethod]
    public void IndexPrecedence_EntriesAtEqualGeneration_ChoosesTheHigherWriterThenSequence()
    {
        var lowWriter = Entry(blobSeed: 1, generation: 3, writerSeed: 0x10, sequence: 99);
        var highWriter = Entry(blobSeed: 2, generation: 3, writerSeed: 0x20, sequence: 1);

        var winner = IndexPrecedence.Resolve([lowWriter, highWriter], _ => BlobState.Live, []);
        Assert.AreEqual(Blob(2), winner!.Entry.BlobId);

        var lowSequence = Entry(blobSeed: 3, generation: 3, writerSeed: 0x20, sequence: 5);
        var highSequence = Entry(blobSeed: 4, generation: 3, writerSeed: 0x20, sequence: 6);

        winner = IndexPrecedence.Resolve([highSequence, lowSequence], _ => BlobState.Live, []);
        Assert.AreEqual(Blob(4), winner!.Entry.BlobId);
    }

    [TestMethod]
    public void IndexPrecedence_TheWinnerNamesADeletedBlob_IsSupersededAndReported()
    {
        var deletedWinner = Entry(blobSeed: 1, generation: 5, writerSeed: 1, sequence: 10, IndexEntryType.Supersession);
        var liveLoser = Entry(blobSeed: 2, generation: 4, writerSeed: 1, sequence: 5);

        var findings = new List<DamageFinding>();
        var winner = IndexPrecedence.Resolve(
            [deletedWinner, liveLoser],
            blob => blob == Blob(1) ? BlobState.Deleted : BlobState.Live,
            findings);

        // Even though it wins on generation, the deleted-blob entry is
        // treated as superseded — and the anomaly is a damage finding.
        Assert.AreEqual(Blob(2), winner!.Entry.BlobId);
        var finding = Assert.ContainsSingle(findings);
        Assert.AreEqual(DamageKind.MissingBlob, finding.Kind);
    }

    [TestMethod]
    public void IndexPrecedence_EveryCandidateNamesADeletedBlob_ResolvesToNothingWithFindings()
    {
        var findings = new List<DamageFinding>();
        var winner = IndexPrecedence.Resolve(
            [Entry(1, 1, 1, 1), Entry(2, 2, 1, 2)],
            _ => BlobState.Deleted,
            findings);

        Assert.IsNull(winner);
        Assert.IsNotEmpty(findings);
    }

    [TestMethod]
    public void IndexPrecedence_AnyApplicationOrder_ConvergesOnTheSameState()
    {
        // 07 §6's claim, checked: the winner is a property of the entries,
        // not of arrival sequence. Build a messy multiset — duplicate
        // insertions, supersessions, several writers and generations — and
        // resolve it under many shuffles.
        var random = new Random(20260803);
        var entries = new List<ProvenancedEntry>();

        for (var i = 0; i < 60; i++)
        {
            entries.Add(Entry(
                blobSeed: (byte)random.Next(1, 8),
                generation: (ulong)random.Next(1, 5),
                writerSeed: (byte)random.Next(1, 4),
                sequence: (ulong)random.Next(1, 100),
                random.Next(2) == 0 ? IndexEntryType.Insertion : IndexEntryType.Supersession));
        }

        var reference = IndexPrecedence.Resolve(entries, _ => BlobState.Live, [])!;

        for (var shuffle = 0; shuffle < 25; shuffle++)
        {
            var shuffled = entries.OrderBy(_ => random.Next()).ToList();
            var winner = IndexPrecedence.Resolve(shuffled, _ => BlobState.Live, [])!;

            Assert.AreEqual(reference.Entry, winner.Entry);
            Assert.AreEqual(reference.Generation, winner.Generation);
            Assert.AreEqual(reference.WriterId, winner.WriterId);
            Assert.AreEqual(reference.Sequence, winner.Sequence);
        }
    }

    [TestMethod]
    public void IndexPrecedence_TwoLocationsForOneObject_AreBenignAndEitherServes()
    {
        // 07 §3: two writers independently stored the same content in
        // different blobs. Either serves — the resolver just picks the
        // deterministic winner, with no findings.
        var first = Entry(blobSeed: 1, generation: 1, writerSeed: 1, sequence: 1);
        var second = Entry(blobSeed: 2, generation: 1, writerSeed: 2, sequence: 1);

        var findings = new List<DamageFinding>();
        var winner = IndexPrecedence.Resolve([first, second], _ => BlobState.Live, findings);

        Assert.IsNotNull(winner);
        Assert.IsEmpty(findings);
    }
}
