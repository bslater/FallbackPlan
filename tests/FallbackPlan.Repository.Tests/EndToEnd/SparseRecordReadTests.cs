using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A data record read by the location the catalogue holds, with no run in
/// hand that reaches its blob's envelope (NFR-PERF-009, ADR-0166). At format
/// 3 a data record carries its own nonce and its own sealed key, so it is
/// read in one ranged read and the envelope is never fetched, the first
/// record of a blob or any after it. A blob that turns out to be of an older
/// format is framed from its envelope and the record re-read from the bytes
/// already in hand, at no more reads than before. A record that does not
/// verify is still a damage finding, a location that points at another
/// record is still refused rather than served, and a reader with no grant to
/// open a record's share is answered that the content is sealed, as before.
/// A data blob the store answers for and will not serve is a finding from the
/// footer path, as it was, never a throw.
/// </summary>
/// <remarks>
/// <para>
/// A format-3 metadata record is read the same way, in one ranged read, under
/// the key generation the catalogue recorded for its blob (NFR-PERF-009,
/// ADR-0167). A blob the catalogue holds no row of, or one whose recorded
/// generation does not open the record, is framed from its envelope from the
/// bytes already in hand; so is a metadata blob of an older format, at no
/// more reads than before. A write-only holder reads one too, since the
/// structure plane needs no grant, and a metadata record that does not
/// verify is still a damage finding.
/// </para>
/// <para>
/// A prefetch reaches down to a blob's envelope only where the read may want
/// it (NFR-PERF-009, ADR-0168). Once an envelope the reader framed has shown
/// format 3, a run over records it can read before their envelope starts at
/// the first of them, while one over a metadata blob whose generation the
/// catalogue did not record still takes the envelope in the same read. In a
/// repository whose framed envelopes show an older format, every prefetch
/// takes its blob's envelope, at one read a blob as before.
/// </para>
/// </remarks>
[TestClass]
public sealed class SparseRecordReadTests : ArchiveTestHarness
{
    [TestMethod]
    public async Task AFormat3DataRecord_ReadByItsLocation_CostsOneRead_AndOpensNoEnvelope()
    {
        var (segments, catalogue, inner, keys) = await PublishAsync(FormatVersions.RelocatableRecords, 0xA1);
        using var _ = keys;
        using var _c = catalogue;

        var counting = new CountingObjectStore(inner);
        using var reader = new RepositoryReader(Repo, keys, counting, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);

        var read = await reader.ReadSegmentAsync(segments[^1], CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        Assert.AreEqual(1L, counting.Reads, "one ranged read of the record, and none of its blob's envelope");
        Assert.AreEqual(0, reader.FramedBlobs, "no envelope was opened");
        Assert.AreEqual(0, reader.LocationFallbacks);
    }

    [TestMethod]
    public async Task EachFurtherFormat3RecordOfThatBlob_CostsOneReadMore()
    {
        var (segments, catalogue, inner, keys) = await PublishAsync(FormatVersions.RelocatableRecords, 0xA2);
        using var _ = keys;
        using var _c = catalogue;

        var counting = new CountingObjectStore(inner);
        using var reader = new RepositoryReader(Repo, keys, counting, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);

        foreach (var segment in segments)
        {
            var read = await reader.ReadSegmentAsync(segment, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        }

        Assert.AreEqual(segments.Count, counting.Reads, "one read a record, the blob's envelope never among them");
        Assert.AreEqual(0, reader.FramedBlobs);
        Assert.AreEqual(0, reader.LocationFallbacks);
    }

    [TestMethod]
    public async Task AnOlderFormatsDataRecord_IsFramedFromItsEnvelope_AtNoMoreReadsThanBefore()
    {
        // Format 2 seals a blob's content key in its envelope, so a record
        // read as if it were format 3 cannot open, and the envelope is read
        // after all. The record is framed again from the bytes already
        // fetched rather than fetched a second time: two reads for the first
        // record, as before, and one for each after it.
        var (segments, catalogue, inner, keys) = await PublishAsync(FormatVersions.SealedDataPlane, 0xA3);
        using var _ = keys;
        using var _c = catalogue;

        var counting = new CountingObjectStore(inner);
        using var reader = new RepositoryReader(Repo, keys, counting, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);

        var first = await reader.ReadSegmentAsync(segments[0], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, first.Outcome, first.Detail);
        Assert.AreEqual(2L, counting.Reads, "the record and its blob's envelope, and the record not fetched again");

        var second = await reader.ReadSegmentAsync(segments[1], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, second.Outcome, second.Detail);
        Assert.AreEqual(3L, counting.Reads, "the blob's framing is held for the records after it");
        Assert.AreEqual(1, reader.FramedBlobs);
        Assert.AreEqual(0, reader.LocationFallbacks, "a guess the envelope corrects is not a fallback to the footer");
    }

    [TestMethod]
    public async Task AFormat3RecordThatDoesNotVerify_IsStillADamageFinding()
    {
        var (segments, catalogue, inner, keys) = await PublishAsync(FormatVersions.RelocatableRecords, 0xA4);
        using var _ = keys;
        using var _c = catalogue;
        RotOneByte(catalogue.ResolveLocation(segments[^1])!, "data");

        using var reader = new RepositoryReader(Repo, keys, inner, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);

        var read = await reader.ReadSegmentAsync(segments[^1], CancellationToken.None);

        Assert.AreNotEqual(RecordReadOutcome.Ok, read.Outcome);
        Assert.AreEqual(1, reader.LocationFallbacks, "the footer path is the one that says what is wrong");
    }

    [TestMethod]
    public async Task AFormat3LocationPointingAtAnotherRecord_IsRefused_AndTheRecordAskedForIsServedFromTheFooter()
    {
        // The neighbour is a valid record of the same blob, so it would
        // decrypt and verify as itself: only the object-id cross-check stops
        // it being served under the name asked for.
        var (segments, catalogue, inner, keys) = await PublishAsync(FormatVersions.RelocatableRecords, 0xA5);
        using var _ = keys;
        using var _c = catalogue;
        var neighbour = catalogue.ResolveLocation(segments[1])!;

        using var reader = new RepositoryReader(Repo, keys, inner, Authority);
        reader.UseLocationSource(objectId => objectId == segments[0] ? neighbour : catalogue.ResolveLocation(objectId));

        var read = await reader.ReadSegmentAsync(segments[0], CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        Assert.AreEqual(1, reader.LocationFallbacks, "the neighbour was refused, and the footer served the record asked for");
    }

    [TestMethod]
    public async Task AReaderWithNoGrantToOpenAShare_IsAnsweredThatTheContentIsSealed()
    {
        // A write-only holder opens no record's share, so there is nothing
        // to read a record as format 3 with, and it is answered as before.
        var (segments, catalogue, inner, keys) = await PublishAsync(FormatVersions.RelocatableRecords, 0xA6);
        using var _ = keys;
        using var _c = catalogue;

        using var reader = new RepositoryReader(Repo, keys, inner);
        reader.UseLocationSource(catalogue.ResolveLocation);

        var read = await reader.ReadSegmentAsync(segments[0], CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.ContentSealed, read.Outcome, read.Detail);
    }

    [TestMethod]
    public async Task ADataBlobTheStoreWillNotServe_IsAFindingFromTheFooterPath_NotAThrow()
    {
        // Listed and then gone, or held where a read is refused: the guess
        // is not served, and the read goes on as it did before the envelope
        // was skipped — the envelope is asked for, the footer after it, and
        // what the footer path cannot open is a finding.
        var (segments, catalogue, inner, keys) = await PublishAsync(FormatVersions.RelocatableRecords, 0xA7);
        using var _ = keys;
        using var _c = catalogue;

        using var reader = new RepositoryReader(Repo, keys, new UnservedDataObjectStore(inner), Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);

        var read = await reader.ReadSegmentAsync(segments[0], CancellationToken.None);

        Assert.AreNotEqual(RecordReadOutcome.Ok, read.Outcome);
        Assert.AreEqual(1, reader.LocationFallbacks);
    }

    [TestMethod]
    public async Task AFormat3MetadataRecord_ReadByItsLocation_CostsOneRead_AndOpensNoEnvelope()
    {
        var (_, catalogue, inner, keys) = await PublishAsync(FormatVersions.RelocatableRecords, 0xB1);
        using var _ = keys;
        using var _c = catalogue;
        var manifests = ManifestsSharingABlob(catalogue, 0xB1);

        var counting = new CountingObjectStore(inner);
        using var reader = new RepositoryReader(Repo, keys, counting, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);

        var read = await reader.ReadSegmentAsync(manifests[^1], CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        Assert.AreEqual(1L, counting.Reads, "one ranged read of the record, and none of its blob's envelope");
        Assert.AreEqual(0, reader.FramedBlobs, "no envelope was opened");
        Assert.AreEqual(0, reader.LocationFallbacks);
    }

    [TestMethod]
    public async Task EachFurtherFormat3MetadataRecordOfThatBlob_CostsOneReadMore()
    {
        var (_, catalogue, inner, keys) = await PublishAsync(FormatVersions.RelocatableRecords, 0xB2);
        using var _ = keys;
        using var _c = catalogue;
        var manifests = ManifestsSharingABlob(catalogue, 0xB2);

        var counting = new CountingObjectStore(inner);
        using var reader = new RepositoryReader(Repo, keys, counting, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);

        foreach (var manifest in manifests)
        {
            var read = await reader.ReadSegmentAsync(manifest, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        }

        Assert.AreEqual(manifests.Count, counting.Reads, "one read a record, the blob's envelope never among them");
        Assert.AreEqual(0, reader.FramedBlobs);
    }

    [TestMethod]
    public async Task AMetadataRecordWhoseBlobTheCatalogueHoldsNoRowOf_IsFramedFromItsEnvelope()
    {
        // The ordinary state of a catalogue rebuilt from the index plane: the
        // location is known and the blob's key generation is not.
        var (_, catalogue, inner, keys) = await PublishAsync(FormatVersions.RelocatableRecords, 0xB3);
        using var _ = keys;
        using var _c = catalogue;
        var manifests = ManifestsSharingABlob(catalogue, 0xB3);

        var counting = new CountingObjectStore(inner);
        using var reader = new RepositoryReader(Repo, keys, counting, Authority);
        reader.UseLocationSource(objectId => catalogue.ResolveLocation(objectId) is { } location
            ? location with { BlobKeyGeneration = null }
            : null);

        var read = await reader.ReadSegmentAsync(manifests[0], CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        Assert.AreEqual(2L, counting.Reads, "the record and its blob's envelope, as before");
        Assert.AreEqual(1, reader.FramedBlobs);
        Assert.AreEqual(0, reader.LocationFallbacks);
    }

    [TestMethod]
    public async Task AMetadataRecordUnderAGenerationItsBlobWasNotWrittenAt_IsFramedFromItsEnvelope_FromTheBytesInHand()
    {
        // A recorded generation that does not open the record is a reason to
        // read the envelope, never an answer and never a second fetch of the
        // record.
        var (_, catalogue, inner, keys) = await PublishAsync(FormatVersions.RelocatableRecords, 0xB4);
        using var _ = keys;
        using var _c = catalogue;
        var manifests = ManifestsSharingABlob(catalogue, 0xB4);

        var counting = new CountingObjectStore(inner);
        using var reader = new RepositoryReader(Repo, keys, counting, Authority);
        reader.UseLocationSource(objectId => catalogue.ResolveLocation(objectId) is { } location
            ? location with { BlobKeyGeneration = new KeyGeneration(7) }
            : null);

        var read = await reader.ReadSegmentAsync(manifests[0], CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        Assert.AreEqual(2L, counting.Reads, "the record and its blob's envelope, and the record not fetched again");
        Assert.AreEqual(1, reader.FramedBlobs);
        Assert.AreEqual(0, reader.LocationFallbacks, "a guess the envelope corrects is not a fallback to the footer");
    }

    [TestMethod]
    public async Task AnOlderFormatsMetadataRecord_IsFramedFromItsEnvelope_AtNoMoreReadsThanBefore()
    {
        // Format 2 keys a metadata record from its blob's salt, which only
        // the envelope carries, so a record read as format 3 cannot open.
        var (_, catalogue, inner, keys) = await PublishAsync(FormatVersions.SealedDataPlane, 0xB5);
        using var _ = keys;
        using var _c = catalogue;
        var manifests = ManifestsSharingABlob(catalogue, 0xB5);

        var counting = new CountingObjectStore(inner);
        using var reader = new RepositoryReader(Repo, keys, counting, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);

        var first = await reader.ReadSegmentAsync(manifests[0], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, first.Outcome, first.Detail);
        Assert.AreEqual(2L, counting.Reads, "the record and its blob's envelope, and the record not fetched again");

        var second = await reader.ReadSegmentAsync(manifests[1], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, second.Outcome, second.Detail);
        Assert.AreEqual(3L, counting.Reads, "the blob's framing is held for the records after it");
        Assert.AreEqual(1, reader.FramedBlobs);
        Assert.AreEqual(0, reader.LocationFallbacks);
    }

    [TestMethod]
    public async Task AWriteOnlyHolder_ReadsAFormat3MetadataRecord_InOneRead()
    {
        // The structure plane is keyed by the class key a writer holds, so
        // no grant is needed for a metadata record, read either way.
        var (_, catalogue, inner, keys) = await PublishAsync(FormatVersions.RelocatableRecords, 0xB6);
        using var _ = keys;
        using var _c = catalogue;
        var manifests = ManifestsSharingABlob(catalogue, 0xB6);

        var counting = new CountingObjectStore(inner);
        using var reader = new RepositoryReader(Repo, keys, counting);
        reader.UseLocationSource(catalogue.ResolveLocation);

        var read = await reader.ReadSegmentAsync(manifests[0], CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        Assert.AreEqual(1L, counting.Reads);
        Assert.AreEqual(0, reader.FramedBlobs);
    }

    [TestMethod]
    public async Task AFormat3MetadataRecordThatDoesNotVerify_IsStillADamageFinding()
    {
        var (_, catalogue, inner, keys) = await PublishAsync(FormatVersions.RelocatableRecords, 0xB7);
        using var _ = keys;
        using var _c = catalogue;
        var manifests = ManifestsSharingABlob(catalogue, 0xB7);
        RotOneByte(catalogue.ResolveLocation(manifests[0])!, "meta");

        using var reader = new RepositoryReader(Repo, keys, inner, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);

        var read = await reader.ReadSegmentAsync(manifests[0], CancellationToken.None);

        Assert.AreNotEqual(RecordReadOutcome.Ok, read.Outcome);
        Assert.AreEqual(1, reader.LocationFallbacks, "the footer path is the one that says what is wrong");
    }

    [TestMethod]
    public async Task OnceAFramedEnvelopeShowsFormat3_APrefetchOfDataRecords_DoesNotReachDownToTheirBlobsEnvelope()
    {
        var (catalogue, inner, keys, first, second) = await PublishInTwoDataBlobsAsync(FormatVersions.RelocatableRecords, 0xC1);
        using var _ = keys;
        using var _c = catalogue;
        var wanted = catalogue.ResolveLocation(second)!;

        var counting = new CountingObjectStore(inner);
        using var reader = new RepositoryReader(Repo, keys, counting, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);

        // Nothing has shown this repository's format yet, so the first
        // blob's run takes its envelope, and framing it shows format 3.
        await reader.PrefetchAsync([first], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, (await reader.ReadSegmentAsync(first, CancellationToken.None)).Outcome);
        var before = reader.PrefetchedBytes;

        await reader.PrefetchAsync([second], CancellationToken.None);
        var read = await reader.ReadSegmentAsync(second, CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        Assert.AreEqual(
            RecordFraming.MaxRecordLength(wanted.StoredLength),
            reader.PrefetchedBytes - before,
            $"the run starts at the record, {wanted.PhysicalOffset} bytes in, not at the blob's start");
        Assert.AreEqual(2L, counting.Reads, "one read a blob");
        Assert.AreEqual(1, reader.FramedBlobs, "only the first blob's envelope was opened");
    }

    [TestMethod]
    public async Task OnceAFramedEnvelopeShowsFormat3_APrefetchOfMetadataRecords_DoesNotReachDownToTheirBlobsEnvelope()
    {
        var (catalogue, inner, keys, first, _) = await PublishInTwoDataBlobsAsync(FormatVersions.RelocatableRecords, 0xC2);
        using var _k = keys;
        using var _c = catalogue;
        var manifests = ManifestsSharingABlob(catalogue, 0xC2);
        var wanted = catalogue.ResolveLocation(manifests[^1])!;

        var counting = new CountingObjectStore(inner);
        using var reader = new RepositoryReader(Repo, keys, counting, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);

        await reader.PrefetchAsync([first], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, (await reader.ReadSegmentAsync(first, CancellationToken.None)).Outcome);
        var before = reader.PrefetchedBytes;

        await reader.PrefetchAsync([manifests[^1]], CancellationToken.None);
        var read = await reader.ReadSegmentAsync(manifests[^1], CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        Assert.AreEqual(
            RecordFraming.MaxRecordLength(wanted.StoredLength),
            reader.PrefetchedBytes - before,
            $"the run starts at the record, {wanted.PhysicalOffset} bytes in, not at the blob's start");
        Assert.AreEqual(2L, counting.Reads);
        Assert.AreEqual(1, reader.FramedBlobs);
    }

    [TestMethod]
    public async Task APrefetchOfAMetadataBlobWhoseGenerationIsNotRecorded_StillTakesItsEnvelopeInTheSameRead()
    {
        // The read will need this blob's envelope whatever the repository's
        // format, so the run that is happening anyway collects it.
        var (catalogue, inner, keys, first, _) = await PublishInTwoDataBlobsAsync(FormatVersions.RelocatableRecords, 0xC3);
        using var _k = keys;
        using var _c = catalogue;
        var manifests = ManifestsSharingABlob(catalogue, 0xC3);
        var wanted = catalogue.ResolveLocation(manifests[^1])!;

        var counting = new CountingObjectStore(inner);
        using var reader = new RepositoryReader(Repo, keys, counting, Authority);
        reader.UseLocationSource(objectId => catalogue.ResolveLocation(objectId) is { } location
            ? location with { BlobKeyGeneration = null }
            : null);

        await reader.PrefetchAsync([first], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, (await reader.ReadSegmentAsync(first, CancellationToken.None)).Outcome);
        var before = reader.PrefetchedBytes;

        await reader.PrefetchAsync([manifests[^1]], CancellationToken.None);
        var read = await reader.ReadSegmentAsync(manifests[^1], CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        Assert.AreEqual(
            (long)wanted.PhysicalOffset + RecordFraming.MaxRecordLength(wanted.StoredLength),
            reader.PrefetchedBytes - before,
            "the run reaches down to offset 0");
        Assert.AreEqual(2L, counting.Reads, "the envelope rode the record's read");
        Assert.AreEqual(2, reader.FramedBlobs);
    }

    [TestMethod]
    public async Task InARepositoryOfAnOlderFormat_EveryPrefetchStillTakesItsBlobsEnvelope_AtOneReadABlob()
    {
        // The first envelope framed shows format 2, which is the evidence
        // the reader goes by: every prefetch after it folds the envelope in,
        // so such a repository costs what it did.
        var (catalogue, inner, keys, first, second) = await PublishInTwoDataBlobsAsync(FormatVersions.SealedDataPlane, 0xC4);
        using var _ = keys;
        using var _c = catalogue;
        var wanted = catalogue.ResolveLocation(second)!;

        var counting = new CountingObjectStore(inner);
        using var reader = new RepositoryReader(Repo, keys, counting, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);

        await reader.PrefetchAsync([first], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, (await reader.ReadSegmentAsync(first, CancellationToken.None)).Outcome);
        var before = reader.PrefetchedBytes;

        await reader.PrefetchAsync([second], CancellationToken.None);
        var read = await reader.ReadSegmentAsync(second, CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        Assert.AreEqual(
            (long)wanted.PhysicalOffset + RecordFraming.MaxRecordLength(wanted.StoredLength),
            reader.PrefetchedBytes - before,
            "the second blob's run reaches down to offset 0");
        Assert.AreEqual(2L, counting.Reads, "one read a blob, as before");
        Assert.AreEqual(2, reader.FramedBlobs);
        Assert.AreEqual(0, reader.LocationFallbacks);
    }

    [TestMethod]
    public async Task InARepositoryHoldingBothFormats_TheFirstOlderBlobCostsItsEnvelopeARead_AndEveryPrefetchAfterItTakesItsBlobsEnvelope()
    {
        // A repository upgraded from format 2 holds blobs of both. Its
        // format-3 blob is framed first, which says nothing of the format-2
        // blobs beside it: the first of those pays its envelope a read of its
        // own, and from then on the reader folds every envelope in again.
        // Older first: a writer publishes at the format the repository has
        // reached, so the format-2 blobs are the ones written before it was
        // upgraded.
        var (older, store, olderKeys, first, second) = await PublishInTwoDataBlobsAsync(FormatVersions.SealedDataPlane, 0xD0);
        using var _k = olderKeys;
        using var _o = older;
        var (newer, _, keys, relocatable, _) = await PublishInTwoDataBlobsAsync(
            FormatVersions.RelocatableRecords, 0xD8, WriterId.FromBytes(Enumerable.Repeat((byte)0x5E, 16).ToArray()));
        using var _ = keys;
        using var _n = newer;

        var counting = new CountingObjectStore(store);
        using var reader = new RepositoryReader(Repo, keys, counting, Authority);
        reader.UseLocationSource(objectId => newer.ResolveLocation(objectId) ?? older.ResolveLocation(objectId));

        foreach (var (objectId, readsAfter) in new[] { (relocatable, 1L), (first, 3L), (second, 4L) })
        {
            await reader.PrefetchAsync([objectId], CancellationToken.None);
            var read = await reader.ReadSegmentAsync(objectId, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
            Assert.AreEqual(readsAfter, counting.Reads);
        }

        Assert.AreEqual(3, reader.FramedBlobs);
        Assert.AreEqual(0, reader.LocationFallbacks);
    }

    /// <summary>
    /// Publishes as <see cref="PublishAsync"/> does and answers, beside it, a
    /// data record past the first of two different data blobs, so each sits
    /// within the bridge of its blob's start without being its first record.
    /// </summary>
    private async Task<(CatalogueDb Catalogue, Storage.Local.LocalFileSystemObjectStore Store, RepositoryKeySet Keys, ObjectId First, ObjectId Second)>
        PublishInTwoDataBlobsAsync(ushort formatVersion, byte seed, WriterId? writer = null)
    {
        var (_, catalogue, store, keys) = await PublishAsync(formatVersion, seed, writer);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);

        var byBlob = new Dictionary<BlobId, List<ObjectId>>();
        foreach (var manifestId in RestorePlanner.Plan(
                catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, RestoreTargetProfile.ForLocalPlatform())
            .Items.Where(entry => entry.Kind == EntryKind.File)
            .Select(entry => entry.ObjectId))
        {
            var manifest = await reader.ReadSegmentAsync(manifestId, CancellationToken.None);
            foreach (var reference in FileVersionManifestCodec.Decode(manifest.Plaintext!).SegmentReferences)
            {
                var blob = catalogue.ResolveLocation(reference.ObjectId)!.BlobId;
                if (!byBlob.TryGetValue(blob, out var held))
                {
                    held = [];
                    byBlob[blob] = held;
                }

                held.Add(reference.ObjectId);
            }
        }

        var pairs = byBlob.Values.Where(held => held.Count >= 2).Take(2).ToList();
        Assert.HasCount(2, pairs, "the fixture must spread its segments over two data blobs");
        return (catalogue, store, keys, pairs[0][1], pairs[1][1]);
    }

    /// <summary>
    /// The file-version manifests of the snapshot <paramref name="seed"/>
    /// names that share a metadata blob with the first of them, in order.
    /// </summary>
    private static List<ObjectId> ManifestsSharingABlob(CatalogueDb catalogue, byte seed)
    {
        var manifests = RestorePlanner.Plan(
                catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, RestoreTargetProfile.ForLocalPlatform())
            .Items.Where(entry => entry.Kind == EntryKind.File)
            .Select(entry => entry.ObjectId)
            .ToList();
        var blob = catalogue.ResolveLocation(manifests[0])!.BlobId;
        var together = manifests.Where(id => catalogue.ResolveLocation(id)!.BlobId.Equals(blob)).ToList();
        Assert.IsGreaterThanOrEqualTo(2, together.Count, "the fixture must put two manifests in one metadata blob");
        return together;
    }

    /// <summary>
    /// Publishes incompressible files at <paramref name="formatVersion"/> and
    /// answers the segments of one of them that share a data blob, in order.
    /// </summary>
    private async Task<(IReadOnlyList<ObjectId> Segments, CatalogueDb Catalogue, Storage.Local.LocalFileSystemObjectStore Store, RepositoryKeySet Keys)>
        PublishAsync(ushort formatVersion, byte seed, WriterId? writer = null)
    {
        var source = new FakeFileSystemSource();
        for (var index = 0; index < 3; index++)
        {
            var content = new byte[200_000];
            new Random(seed + index).NextBytes(content);
            source.AddFile($"data/file-{index}.bin", content, fileId: (ulong)(9_500 + index));
        }

        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"catalogue-{seed:x2}.db"), Repo);
        var spool = Path.Combine(SpoolDirectory, $"spool-{seed:x2}");
        Directory.CreateDirectory(spool);
        await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, writer ?? Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                spool, formatVersion, observer: null, catalogue)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = source,
                    Roots = [new ScanRoot("/")],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "sparse-record-read-tests/1.0",
                },
                CancellationToken.None);

        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, RestoreTargetProfile.ForLocalPlatform());
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);
        foreach (var item in plan.Items.Where(entry => entry.Kind == EntryKind.File))
        {
            var manifest = await reader.ReadSegmentAsync(item.ObjectId, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, manifest.Outcome, manifest.Detail);
            var ids = FileVersionManifestCodec.Decode(manifest.Plaintext!).SegmentReferences
                .Select(reference => reference.ObjectId)
                .ToList();
            var blob = catalogue.ResolveLocation(ids[0])!.BlobId;
            var together = ids.TakeWhile(id => catalogue.ResolveLocation(id)!.BlobId.Equals(blob)).ToList();
            if (together.Count >= 2)
            {
                return (together, catalogue, store, keys);
            }
        }

        Assert.Fail("the fixture must produce a file with two segments in one data blob");
        return default;
    }

    /// <summary>A store that answers for every data blob's metadata and serves none of its bytes.</summary>
    private sealed class UnservedDataObjectStore(IObjectStore inner) : IObjectStore
    {
        public StoreCapabilities Capabilities => inner.Capabilities;

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
            key.Value.StartsWith("blobs/data/", StringComparison.Ordinal)
                ? ValueTask.FromResult(OpenReadResult.NotFound)
                : inner.OpenReadAsync(key, range, cancellationToken);

        public ValueTask<PutResult> PutAsync(
            ObjectKey key, Func<CancellationToken, ValueTask<Stream>> openContent, PutConditions conditions, CancellationToken cancellationToken) =>
            inner.PutAsync(key, openContent, conditions, cancellationToken);

        public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }

    /// <summary>Flips one byte inside a record's sealed bytes, where its tag or its key will catch it.</summary>
    private void RotOneByte(Repository.Catalogue.ResolvedLocation location, string blobClass)
    {
        foreach (var path in Directory
            .EnumerateFiles(Path.Combine(StoreRoot, "blobs", blobClass), "*", SearchOption.AllDirectories)
            .Where(path => new FileInfo(path).Length > (long)location.PhysicalOffset + 64))
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
            file.Seek((long)location.PhysicalOffset + RecordHeader.Length + 4, SeekOrigin.Begin);
            var original = file.ReadByte();
            file.Seek(-1, SeekOrigin.Current);
            file.WriteByte((byte)(original ^ 0xFF));
        }
    }
}
