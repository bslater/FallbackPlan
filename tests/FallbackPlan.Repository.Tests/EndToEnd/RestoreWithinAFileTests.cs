using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A restore cut short in the middle of a file resumes within it (FR-RST-008,
/// ADR-0129). A run told where to keep its parts assembles each file in a part
/// named by the file's whole-file hash, and keeps it unless the file lands or
/// its assembly is refused: a run stopped, or a segment that would not read,
/// leaves what was assembled. A resume takes the part up only so far as each
/// of its segments proves against its object identifier, and each hole reads
/// as zeroes, and reads from the store only what the part lacks. A run that
/// is not a resume takes up no part. The receipt, at schema 11, says where
/// each file was resumed: how much of it was taken up and not read again
/// (FR-RST-004, ADR-0130). A plan of a resume proves a part as the resume
/// would, writing nothing, and counts nothing it proves among what the run
/// reads, nor anything of a file the run landed but its manifest (FR-RST-003,
/// ADR-0132). What a part proves is room the run's working space already
/// holds, so a resume's plan, and a run measured from its manifests, need
/// there only the rest of each file; the folder the file lands in still
/// needs all of it (ADR-0136).
/// </summary>
[TestClass]
public sealed class RestoreWithinAFileTests : ArchiveTestHarness
{
    private const int Segment = 64 * 1024;
    private const int Segments = 48;
    private const string BigPath = "data/big.bin";

    private static readonly byte[] Big = RandomBytes(Segments * Segment, 41);

    [TestMethod]
    public async Task AResume_TakesUpThePartOfAFileCutShort_AndReadsOnlyWhatItLacks()
    {
        using var published = await PublishAsync("takes", 0xE1);
        var parts = Path.Combine(SpoolDirectory, "takes-parts");
        var fresh = await RestoreAsync(published, "takes-fresh", Fresh("takes-fresh"));
        WritePart(parts, Big.AsSpan(0, (24 * Segment) + 1_000));

        var resumed = await RestoreAsync(published, "takes-resumed", Resumed("takes-resumed", parts));

        Assert.AreEqual(RestoreOutcome.Complete, resumed.Receipt.Outcome);
        SequenceAssert.AreEqual(Big, File.ReadAllBytes(resumed.Landed));
        Assert.AreEqual(
            24L, fresh.SegmentsRead - resumed.SegmentsRead,
            "the whole segments the part held are not read again, and the piece of the next one is not taken");
        Assert.IsLessThan(fresh.ContentReads, resumed.ContentReads, "nor are the blobs only they were in");
        Assert.IsFalse(File.Exists(PartOf(parts)), "a part goes once its file lands");
        Assert.AreEqual((ulong)(24 * Segment), Item(resumed.Receipt).ResumedAt, "the receipt says where the file was resumed");
        Assert.AreEqual(0UL, Item(fresh.Receipt).ResumedAt);
    }

    [TestMethod]
    public async Task TheReceipt_SaysWhereAFileWasResumed_AtSchemaEleven_AndNothingOfOneWrittenWhole()
    {
        using var published = await PublishAsync("receipt", 0xE8);
        var parts = Path.Combine(SpoolDirectory, "receipt-parts");
        WritePart(parts, Big.AsSpan(0, 3 * Segment));

        var resumed = await RestoreAsync(published, "receipt-resumed", Resumed("receipt-resumed", parts));
        var fresh = await RestoreAsync(published, "receipt-fresh", Fresh("receipt-fresh"));

        Assert.AreEqual(11, RestoreReceipt.CurrentSchemaVersion);
        Assert.AreEqual(11, resumed.Receipt.SchemaVersion);
        Assert.Contains($"\"resumed_at\": {3 * Segment}", resumed.Receipt.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"resumed_at\"", fresh.Receipt.ToJson(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AFileResumedWithin_ThatLandsDegraded_StillSaysWhereItWasResumed()
    {
        // On a target that keeps a file's streams in no form, neither as
        // streams nor as attributes, the file lands degraded; it was still
        // written from its part.
        using var published = await PublishAsync("degraded", 0xE9, withStream: true);
        var parts = Path.Combine(SpoolDirectory, "degraded-parts");
        WritePart(parts, Big.AsSpan(0, 2 * Segment));

        var resumed = await RestoreAsync(published, "degraded-resumed", Resumed("degraded-resumed", parts));

        Assert.AreEqual("degraded", Item(resumed.Receipt).Outcome);
        Assert.AreEqual((ulong)(2 * Segment), Item(resumed.Receipt).ResumedAt);
    }

    [TestMethod]
    public async Task APlanOfAResume_ReadsNothingOfWhatAPartProves_NorOfAFileItsRunLanded()
    {
        using var published = await PublishAsync("plans", 0xEA);
        var parts = Path.Combine(SpoolDirectory, "plans-parts");
        Task<RestoreBlobSetResult> ProbeAsync(RestoreHeld? held) =>
            RestoreBlobSet.ResolveAsync(
                published.Catalogue, published.Plan, published.Store, Repo, published.Keys, held, CancellationToken.None)
            .AsTask();
        async Task<ulong> ReadsAsync(RestoreHeld? held) => (await ProbeAsync(held)).ReadBytes;

        var afresh = await ReadsAsync(null);
        Assert.AreEqual(afresh, await ReadsAsync(new RestoreHeld { PartsDirectory = parts }), "no part holds anything");

        WritePart(parts, Big.AsSpan(0, (24 * Segment) + 1_000));
        var half = await ReadsAsync(new RestoreHeld { PartsDirectory = parts });
        var differs = Big.ToArray();
        differs[(10 * Segment) + 5] ^= 0xFF;
        WritePart(parts, differs);
        var ten = await ReadsAsync(new RestoreHeld { PartsDirectory = parts });
        WritePart(parts, Big);
        var whole = await ReadsAsync(new RestoreHeld { PartsDirectory = parts });
        var landed = await ReadsAsync(new RestoreHeld { Landed = new HashSet<string>(StringComparer.Ordinal) { BigPath } });

        Assert.IsLessThan(afresh, ten, "what the part proves is not read again");
        Assert.IsLessThan(ten, half, "and only as far as it proves");
        Assert.IsLessThan(half, whole);

        // Each segment is a record of at least its own length, the content
        // being random, so what is not read is at least that many bytes.
        Assert.IsGreaterThanOrEqualTo(10UL * Segment, afresh - ten);
        Assert.IsGreaterThanOrEqualTo(14UL * Segment, ten - half);
        Assert.IsGreaterThanOrEqualTo(24UL * Segment, half - whole);
        Assert.AreEqual(landed, whole, "a part that proves all of a file leaves only its manifest to read, as a file landed does");
        Assert.DoesNotContain(
            key => key.Value.StartsWith("blobs/data/", StringComparison.Ordinal),
            (await ProbeAsync(new RestoreHeld { PartsDirectory = parts })).Blobs,
            "nor is a blob opened only for what is held");

        // A plan leaves a part as it found it.
        SequenceAssert.AreEqual(Big, File.ReadAllBytes(PartOf(parts)));
    }

    [TestMethod]
    public async Task APlanOfAResume_ReadsNothingOfTheStreamsOfAFileItsRunLanded()
    {
        // A file the run writes is counted with the streams it carries, as
        // the plan counts any file it writes; a file landed reads only its
        // manifest.
        using var published = await PublishAsync("plan-streams", 0xEB, withStream: true);
        var parts = Path.Combine(SpoolDirectory, "plan-streams-parts");
        WritePart(parts, Big);
        async Task<ulong> ReadsAsync(RestoreHeld held) =>
            (await RestoreBlobSet.ResolveAsync(
                published.Catalogue, published.Plan, published.Store, Repo, published.Keys, held, CancellationToken.None))
            .ReadBytes;

        var whole = await ReadsAsync(new RestoreHeld { PartsDirectory = parts });
        var landed = await ReadsAsync(new RestoreHeld { Landed = new HashSet<string>(StringComparer.Ordinal) { BigPath } });

        Assert.IsLessThan(whole, landed);
    }

    [TestMethod]
    public async Task APlanOfAResume_CountsAHoleItsPartHolds_AsNoSegment()
    {
        var holed = Big.ToArray();
        Array.Clear(holed, Segment, Segment);
        using var published = await PublishAsync("plan-holes", 0xEC, content: holed, hole: new SparseExtent(Segment, Segment));
        var parts = Path.Combine(SpoolDirectory, "plan-holes-parts");
        var part = Path.Combine(parts, $"{Convert.ToHexStringLower(SHA256.HashData(holed))}.part");
        Directory.CreateDirectory(parts);
        async Task<ulong> ReadsAsync(int held)
        {
            File.WriteAllBytes(part, holed.AsSpan(0, held).ToArray());
            return (await RestoreBlobSet.ResolveAsync(
                    published.Catalogue, published.Plan, published.Store, Repo, published.Keys,
                    new RestoreHeld { PartsDirectory = parts }, CancellationToken.None))
                .ReadBytes;
        }

        var first = await ReadsAsync(Segment);
        var andTheHole = await ReadsAsync(2 * Segment);
        var andTheNext = await ReadsAsync(3 * Segment);

        Assert.AreEqual(first, andTheHole, "a hole the part holds leaves no segment unread");
        Assert.IsLessThan(andTheHole, andTheNext, "the segment after it does");
    }

    [TestMethod]
    public async Task APlanOfAResume_CreditsItsWorkingSpaceWhatAPartProves_AndNotItsDestination()
    {
        using var published = await PublishAsync("room", 0xED);
        var parts = Path.Combine(SpoolDirectory, "room-parts");
        var output = Path.Combine(SpoolDirectory, "room-out");
        var item = published.Plan.Items.Single(candidate => candidate.Path == BigPath);
        async Task<(ulong Held, ulong Working, ulong Destination)> MeasureAsync(bool resume)
        {
            var probed = await RestoreBlobSet.ResolveAsync(
                published.Catalogue, published.Plan, published.Store, Repo, published.Keys,
                resume ? new RestoreHeld { PartsDirectory = parts } : null, CancellationToken.None);
            var report = await RestoreSpace.MeasureAsync(
                [new RestoreSlice(published.Plan, output)], RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Preserve,
                ApartProbe(parts, available: null), RestoreSpace.WrittenBytesFrom(probed.Facts), CancellationToken.None);
            return (probed.Facts[item.ObjectId].HeldBytes, WorkingOf(report), report.Needs.Single(need => !need.Working).NeededBytes);
        }

        WritePart(parts, Big.AsSpan(0, (24 * Segment) + 1_000));
        var afresh = await MeasureAsync(resume: false);
        var half = await MeasureAsync(resume: true);
        var differs = Big.ToArray();
        differs[(10 * Segment) + 5] ^= 0xFF;
        WritePart(parts, differs);
        var ten = await MeasureAsync(resume: true);
        WritePart(parts, Big);
        var whole = await MeasureAsync(resume: true);

        Assert.AreEqual((0UL, (ulong)Big.Length), (afresh.Held, afresh.Working), "a run that is not a resume takes up no part");
        Assert.AreEqual((24UL * Segment, 24UL * Segment), (half.Held, half.Working), "what the part proves is room already taken");
        Assert.AreEqual((10UL * Segment, 38UL * Segment), (ten.Held, ten.Working), "and only as far as it proves");
        Assert.AreEqual(((ulong)Big.Length, 0UL), (whole.Held, whole.Working), "a part that proves all of a file leaves nothing to assemble");
        Assert.AreEqual(afresh.Destination, half.Destination, "the file still lands whole where the run writes");
        Assert.AreEqual(afresh.Destination, whole.Destination);
    }

    [TestMethod]
    public async Task ARunShortOfWorkingSpaceForAWholeFile_FitsWhereItsPartHoldsTheRest()
    {
        using var published = await PublishAsync("room-run", 0xEE);
        var parts = Path.Combine(SpoolDirectory, "room-run-parts");
        var output = Path.Combine(SpoolDirectory, "room-run-out");
        WritePart(parts, Big.AsSpan(0, 24 * Segment));
        using var reader = Reader(published);
        Task<RestoreSpaceReport> MeasureAsync(string? held) => RestoreSpace.MeasureRunAsync(
            [new RestoreSlice(published.Plan, output)], RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Preserve,
            ApartProbe(parts, available: 30L * Segment), reader, held, CancellationToken.None).AsTask();

        Assert.IsTrue((await MeasureAsync(null)).IsShort, "the whole file will not fit beside what is free");

        var credited = await MeasureAsync(parts);

        Assert.IsFalse(credited.IsShort, "the rest of it does");
        Assert.AreEqual(24UL * Segment, WorkingOf(credited));
        Assert.IsTrue(credited.Exact, "measured from the manifests, which name the part");

        // And the part is left as it was.
        SequenceAssert.AreEqual(Big.AsSpan(0, 24 * Segment).ToArray(), File.ReadAllBytes(PartOf(parts)));
    }

    [TestMethod]
    public async Task APart_IsTakenUpOnlyAsFarAsItsSegmentsProve()
    {
        using var published = await PublishAsync("proves", 0xE2);
        var parts = Path.Combine(SpoolDirectory, "proves-parts");
        var fresh = await RestoreAsync(published, "proves-fresh", Fresh("proves-fresh"));
        var held = Big.AsSpan(0, 24 * Segment).ToArray();
        held[(10 * Segment) + 5] ^= 0xFF;
        WritePart(parts, held);

        var resumed = await RestoreAsync(published, "proves-resumed", Resumed("proves-resumed", parts));

        Assert.AreEqual(RestoreOutcome.Complete, resumed.Receipt.Outcome);
        SequenceAssert.AreEqual(Big, File.ReadAllBytes(resumed.Landed));
        Assert.AreEqual(10L, fresh.SegmentsRead - resumed.SegmentsRead, "the ten before the one that differs");
        Assert.AreEqual((ulong)(10 * Segment), Item(resumed.Receipt).ResumedAt);
    }

    [TestMethod]
    public async Task ARunThatIsNotAResume_TakesUpNoPart()
    {
        using var published = await PublishAsync("fresh", 0xE3);
        var parts = Path.Combine(SpoolDirectory, "fresh-parts");
        var fresh = await RestoreAsync(published, "fresh-plain", Fresh("fresh-plain"));
        WritePart(parts, Big);

        var again = await RestoreAsync(published, "fresh-again", Fresh("fresh-again", parts));

        Assert.AreEqual(RestoreOutcome.Complete, again.Receipt.Outcome);
        SequenceAssert.AreEqual(Big, File.ReadAllBytes(again.Landed));
        Assert.AreEqual(fresh.SegmentsRead, again.SegmentsRead, "a run not told it resumes cannot know the part is its own");
        Assert.IsFalse(File.Exists(PartOf(parts)));
        Assert.AreEqual(0UL, Item(again.Receipt).ResumedAt);
    }

    [TestMethod]
    public async Task ARunStoppedInAFile_KeepsWhatItHadAssembled_ForAResumeToTakeUp()
    {
        using var published = await PublishAsync("stopped", 0xE4);
        var parts = Path.Combine(SpoolDirectory, "stopped-parts");
        var fresh = await RestoreAsync(published, "stopped-fresh", Fresh("stopped-fresh"));
        using var stop = new CancellationTokenSource();

        // Read a segment at a time, so the run is in the file when it stops.
        var cut = await RestoreAsync(
            published, "stopped", Fresh("stopped", parts),
            new PrefetchPolicy { BudgetBytes = Segment, CoalesceWindowBytes = Segment },
            new StoppingStore(published.Store, afterContentReads: 6, stop), stop.Token);

        Assert.AreEqual(RestoreOutcome.Cancelled, cut.Receipt.Outcome);
        Assert.IsFalse(File.Exists(cut.Landed));
        var held = File.ReadAllBytes(PartOf(parts));
        Assert.IsNotEmpty(held, "the run had assembled some of the file");
        SequenceAssert.AreEqual(Big.AsSpan(0, held.Length).ToArray(), held);

        var resumed = await RestoreAsync(published, "stopped", Resumed("stopped-again", parts));

        Assert.AreEqual(RestoreOutcome.Complete, resumed.Receipt.Outcome);
        SequenceAssert.AreEqual(Big, File.ReadAllBytes(resumed.Landed));
        Assert.AreEqual((long)(held.Length / Segment), fresh.SegmentsRead - resumed.SegmentsRead);
        Assert.IsFalse(File.Exists(PartOf(parts)));
        Assert.AreEqual((ulong)(held.Length / Segment * Segment), Item(resumed.Receipt).ResumedAt);
    }

    [TestMethod]
    public async Task ASegmentThatWillNotRead_LeavesThePartAsFarAsItWasAssembled()
    {
        using var published = await PublishAsync("unread", 0xE5);
        var part = Path.Combine(SpoolDirectory, "unread.part");
        using var reader = Reader(published);
        var manifest = await ManifestAsync(reader, published);
        Assert.HasCount(Segments, manifest.SegmentReferences);
        var last = manifest.SegmentReferences[^1];
        var unreadable = manifest with
        {
            SegmentReferences = [.. manifest.SegmentReferences.SkipLast(1), last with { ObjectId = ObjectId.FromBytes(RandomBytes(32, 7)) }],
        };

        var result = await new RestoreEngine(reader).RestoreFileAsync(unreadable, Stream.Null, part, takeUp: true, CancellationToken.None);

        Assert.IsFalse(result.Success);
        SequenceAssert.AreEqual(Big.AsSpan(0, (Segments - 1) * Segment).ToArray(), File.ReadAllBytes(part));
    }

    [TestMethod]
    public async Task AnAssemblyThatDoesNotHashToTheFile_LetsItsPartGo()
    {
        using var published = await PublishAsync("refused", 0xE6);
        var part = Path.Combine(SpoolDirectory, "refused.part");
        using var reader = Reader(published);
        var manifest = await ManifestAsync(reader, published);

        var result = await new RestoreEngine(reader).RestoreFileAsync(
            manifest with { WholeFileHash = new byte[32] }, Stream.Null, part, takeUp: true, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.Contains("whole_file_hash", result.FailureDetail!, StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(part), "a part that assembles to the wrong file is nothing to resume from");
    }

    [TestMethod]
    public async Task AHoleInAPart_IsTakenUpOnlyWhereItReadsAsZeroes()
    {
        using var published = await PublishAsync("holes", 0xE7);
        var part = Path.Combine(SpoolDirectory, "holes.part");
        using var reader = Reader(published);
        var manifest = await ManifestAsync(reader, published);
        var first = Big.AsSpan(0, Segment).ToArray();
        var second = Big.AsSpan(Segment, Segment).ToArray();
        byte[] file = [.. first, .. new byte[Segment], .. second];
        var holed = manifest with
        {
            LogicalLength = (ulong)file.Length,
            SegmentReferences = [manifest.SegmentReferences[0], manifest.SegmentReferences[1] with { LogicalOffset = 2 * Segment }],
            SparseExtents = [new SparseExtent(Segment, Segment)],
            WholeFileHash = SHA256.HashData(file),
        };
        var engine = new RestoreEngine(reader);
        byte[] notZero = [.. first, .. RandomBytes(Segment, 9), .. second];

        // Emitted where a hole cannot be kept, so what the part holds there
        // is what lands.
        await File.WriteAllBytesAsync(part, notZero);
        var before = reader.RecordsRead;
        var overHole = new Unseekable();
        var written = await engine.RestoreFileAsync(holed, overHole, part, takeUp: true, CancellationToken.None);

        Assert.IsTrue(written.Success, written.FailureDetail);
        SequenceAssert.AreEqual(file, overHole.ToArray());
        Assert.AreEqual(1L, reader.RecordsRead - before, "what lay where the hole is was not taken, nor anything after it");
        Assert.AreEqual((long)Segment, written.Resumed, "the file was resumed where the hole begins");

        await File.WriteAllBytesAsync(part, notZero);
        var afresh = new Unseekable();
        written = await engine.RestoreFileAsync(holed, afresh, part, takeUp: false, CancellationToken.None);

        Assert.IsTrue(written.Success, written.FailureDetail);
        SequenceAssert.AreEqual(file, afresh.ToArray());
        Assert.AreEqual(0L, written.Resumed);

        await File.WriteAllBytesAsync(part, file);
        before = reader.RecordsRead;
        var whole = new MemoryStream();
        written = await engine.RestoreFileAsync(holed, whole, part, takeUp: true, CancellationToken.None);

        Assert.IsTrue(written.Success, written.FailureDetail);
        SequenceAssert.AreEqual(file, whole.ToArray());
        Assert.AreEqual(0L, reader.RecordsRead - before, "a hole that reads as zeroes is taken with the rest");
        Assert.AreEqual((long)file.Length, written.Resumed, "all of it, the hole included");
    }

    private static ReceiptItem Item(RestoreReceipt receipt) => receipt.Items.Single(item => item.Path == BigPath);

    /// <summary>
    /// A probe whose working space is <paramref name="parts"/>, a volume of
    /// its own with <paramref name="available"/> free, beside a volume for
    /// everything else that never says.
    /// </summary>
    private static RestoreSpaceProbe ApartProbe(string parts, long? available) => new()
    {
        AvailableBytes = path => path.StartsWith(parts, StringComparison.Ordinal) ? available : null,
        VolumeOf = path => path.StartsWith(parts, StringComparison.Ordinal) ? 2UL : 1UL,
        WorkingDirectory = parts,
    };

    /// <summary>What a run needs of its working space, which names no need where it needs none.</summary>
    private static ulong WorkingOf(RestoreSpaceReport report) =>
        report.Needs.SingleOrDefault(need => need.Working)?.NeededBytes ?? 0;

    private static string PartOf(string parts) =>
        Path.Combine(parts, $"{Convert.ToHexStringLower(SHA256.HashData(Big))}.part");

    private static void WritePart(string parts, ReadOnlySpan<byte> held)
    {
        Directory.CreateDirectory(parts);
        File.WriteAllBytes(PartOf(parts), held.ToArray());
    }

    private static RestoreExecutionOptions Fresh(string runId, string? parts = null) => new()
    {
        DestinationMode = RestoreDestinationMode.InPlace,
        RunId = runId,
        NowUnixMilliseconds = 1_722_700_000_000,
        PartsDirectory = parts,
    };

    private static RestoreExecutionOptions Resumed(string runId, string parts) => Fresh(runId, parts) with { Resume = true };

    private static RepositoryReader Reader(Published published, IObjectStore? store = null)
    {
        var reader = new RepositoryReader(Repo, published.Keys, store ?? published.Store, Authority);
        reader.UseLocationSource(published.Catalogue.ResolveLocation);
        return reader;
    }

    private static async Task<FileVersionManifest> ManifestAsync(RepositoryReader reader, Published published)
    {
        var item = published.Plan.Items.Single(candidate => candidate.Path == BigPath);
        var read = await reader.ReadSegmentAsync(item.ObjectId, CancellationToken.None);
        return FileVersionManifestCodec.Decode(read.Plaintext!);
    }

    private async Task<(RestoreReceipt Receipt, string Landed, long SegmentsRead, int ContentReads)> RestoreAsync(
        Published published,
        string output,
        RestoreExecutionOptions options,
        PrefetchPolicy? prefetch = null,
        IObjectStore? store = null,
        CancellationToken cancellationToken = default)
    {
        var counting = new TestSupport.CountingObjectStore(store ?? published.Store);
        using var reader = Reader(published, counting);
        if (prefetch is not null)
        {
            reader.UsePrefetchPolicy(prefetch);
        }

        var root = Path.Combine(SpoolDirectory, output);
        var receipt = await new RestoreExecutor(reader, published.Target)
            .ExecuteAsync(published.Plan, root, options, cancellationToken);
        return (receipt, Path.Combine(root, "data", "big.bin"), reader.RecordsRead, counting.ReadKeys.Count(IsContent));
    }

    private async Task<Published> PublishAsync(
        string name, byte seed, bool withStream = false, byte[]? content = null, SparseExtent? hole = null)
    {
        var source = new FakeFileSystemSource();
        var big = source.AddFile(BigPath, content ?? Big, fileId: 9_101);
        big.Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000 };
        if (withStream)
        {
            big.AlternateStreams["Zone.Identifier"] = "[ZoneTransfer]\nZoneId=3"u8.ToArray();
        }

        if (hole is { } extent)
        {
            source.AddNode(big with { SparseExtents = [extent] });
        }

        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"catalogue-{name}.db"), Repo);
        var spool = Path.Combine(SpoolDirectory, name);
        Directory.CreateDirectory(spool);
        await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                spool,
                FormatVersions.SealedDataPlane, observer: null, catalogue)
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
                    ClientVersion = "restore-within-a-file-tests/1.0",
                },
                CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform() with { SupportsAlternateStreams = false, SupportsExtendedAttributes = false };
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, target);
        return new Published(plan, target, store, keys, catalogue);
    }

    private static bool IsContent(string key) => key.StartsWith("blobs/data/", StringComparison.Ordinal);

    /// <summary>A destination that cannot seek, so a hole is written out as what the part holds there.</summary>
    private sealed class Unseekable : MemoryStream
    {
        public override bool CanSeek => false;
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private sealed record Published(
        RestorePlan Plan, RestoreTargetProfile Target, IObjectStore Store, RepositoryKeySet Keys, CatalogueDb Catalogue) : IDisposable
    {
        public void Dispose()
        {
            Catalogue.Dispose();
            Keys.Dispose();
        }
    }

    /// <summary>Stops the run as the store serves its <c>afterContentReads</c>th read of content.</summary>
    private sealed class StoppingStore(IObjectStore inner, int afterContentReads, CancellationTokenSource stop) : IObjectStore
    {
        private int _contentReads;

        public StoreCapabilities Capabilities => inner.Capabilities;

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
        {
            if (IsContent(key.Value) && Interlocked.Increment(ref _contentReads) == afterContentReads)
            {
                stop.Cancel();
            }

            return inner.OpenReadAsync(key, range, cancellationToken);
        }

        public ValueTask<PutResult> PutAsync(
            ObjectKey key,
            Func<CancellationToken, ValueTask<Stream>> openContent,
            PutConditions conditions,
            CancellationToken cancellationToken) =>
            inner.PutAsync(key, openContent, conditions, cancellationToken);

        public async IAsyncEnumerable<ObjectEntry> ListAsync(
            ObjectPrefix prefix, ListOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var entry in inner.ListAsync(prefix, options, cancellationToken))
            {
                yield return entry;
            }
        }

        public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }
}
