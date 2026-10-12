using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using Counting = FallbackPlan.TestSupport.CountingObjectStore;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The accounting the product's index loads pass (FR-MAN-016; specification
/// 07 §4; ADR-0022 Decision 7, amended 2026-10). A writer's deltas, journal
/// records and blob counters draw from one sequence space, so most of its
/// numbers are not deltas. A load that judges gaps has to be told what
/// accounts for the rest, or every journal record and every blob between two
/// deltas is a gap.
/// </summary>
/// <remarks>
/// A gap is left unresolved while the generations a reader waits through
/// have not passed, and reported as damage once they have. Nothing the
/// product does advances a generation yet, so these tests read the gaps a
/// load leaves unresolved. Format 4's index generation will advance, and the
/// one aged load below shows that the same numbers then become damage
/// findings.
/// </remarks>
[TestClass]
public sealed class SequenceAccountingTests : ArchiveTestHarness
{
    private const int KiB = 1024;

    private const ulong Now = 1_722_600_000_000;

    private sealed class KilledAfterUploadsException() : Exception("killed after the uploads, before the index");

    private sealed class KillAfterUploads : IPublicationObserver
    {
        public void AfterStep(PublicationStep completedStep)
        {
            if (completedStep == PublicationStep.UploadBlobs)
            {
                throw new KilledAfterUploadsException();
            }
        }
    }

    [TestMethod]
    public async Task EveryNumberABackupSpent_IsAccountedFor()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue("spent");
        var sequence = Sequence("spent");

        // The first backup writes more blobs than its intent names, so an
        // extension names the rest.
        for (var run = 0; run < 3; run++)
        {
            await Orchestrator(store, keys, credential, sequence, catalogue)
                .PublishAsync(
                    Job(Tree(files: run == 0 ? 12 : 3, seed: 100 * (run + 1)), (byte)(0xA1 + run), Now + ((ulong)run * 60_000)),
                    CancellationToken.None);
        }

        Assert.IsNotEmpty(
            (await JournalAsync(store, credential)).Where(record => record.Payload is JournalPayload.IntentExtension).ToList(),
            "the premise: a blob was named by an extension, not only by an intent");

        // And the backups spent numbers on intents, extensions, retirements
        // and blobs between their deltas.
        Assert.IsNotEmpty((await LoadAsync(store, credential, accounting: null)).UnresolvedGaps);

        var accounting = await SequenceAccounting.LoadAsync(store, Repo, credential, maxGeneration: 0, CancellationToken.None);
        var accounted = await LoadAsync(store, credential, accounting);

        Assert.IsEmpty(accounted.UnresolvedGaps, Describe(accounted.UnresolvedGaps));
        Assert.IsEmpty(accounted.Findings.Where(IsGapFinding).ToList());
    }

    /// <summary>
    /// What changes once generations move: the numbers left unresolved
    /// become damage findings, and the accounting is what keeps a writer's
    /// own journal records and blobs from being reported as missing deltas.
    /// </summary>
    [TestMethod]
    public async Task OnceGenerationsMove_AnUnaccountedNumberIsDamage_AndAnAccountedOneIsNot()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue("aged");
        var sequence = Sequence("aged");

        for (var run = 0; run < 2; run++)
        {
            await Orchestrator(store, keys, credential, sequence, catalogue)
                .PublishAsync(Job(Tree(files: 3, seed: 400 + (100 * run)), (byte)(0xB1 + run), Now + ((ulong)run * 60_000)), CancellationToken.None);
        }

        Assert.IsNotEmpty(
            (await LoadAsync(store, credential, accounting: null, aged: true)).Findings.Where(IsGapFinding).ToList(),
            "without accounting, the journal records and blobs between the deltas are reported as damage");

        var accounting = await SequenceAccounting.LoadAsync(store, Repo, credential, maxGeneration: 0, CancellationToken.None);
        var aged = await LoadAsync(store, credential, accounting, aged: true);
        Assert.IsEmpty(aged.Findings.Where(IsGapFinding).ToList(), string.Join(" | ", aged.Findings.Select(finding => finding.Detail)));
    }

    /// <summary>
    /// A backup that dies after its uploads leaves blobs that an intent names
    /// and no snapshot reaches. The writer marked their numbers accounted when
    /// each put was acknowledged, so no void delta follows, and the intent is
    /// never retired. A collector then sweeps the blobs as garbage. The intent
    /// that named them still accounts for their numbers.
    /// </summary>
    [TestMethod]
    public async Task ABlobAnIntentNamed_StaysAccountedFor_AfterASweepRemovesIt()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue("swept");
        var sequence = Sequence("swept");

        await Assert.ThrowsExactlyAsync<KilledAfterUploadsException>(async () =>
            await Orchestrator(store, keys, credential, sequence, catalogue, new KillAfterUploads())
                .PublishAsync(Job(Tree(files: 3, seed: 700), 0xC1, Now), CancellationToken.None));

        var swept = StoredKeys(store, "blobs/");
        Assert.IsNotEmpty(swept, "the premise: the dead backup uploaded blobs");
        foreach (var key in swept)
        {
            await store.DeleteAsync(ObjectKey.Parse(key), DeleteConditions.None, CancellationToken.None);
        }

        await Orchestrator(store, keys, credential, sequence, catalogue)
            .PublishAsync(Job(Tree(files: 3, seed: 800), 0xC2, Now + 60_000), CancellationToken.None);

        var accounting = await SequenceAccounting.LoadAsync(store, Repo, credential, maxGeneration: 0, CancellationToken.None);
        var accounted = await LoadAsync(store, credential, accounting);
        Assert.IsEmpty(accounted.UnresolvedGaps, Describe(accounted.UnresolvedGaps));

        // The control: case 4 as it read before the amendment, which needed
        // the blob to exist, and case 5, which needs a Completed retirement.
        // Under them, exactly the swept blobs' numbers are gaps.
        var records = await JournalAsync(store, credential);
        var asWritten = AccountingAsWrittenBefore(records, StoredKeys(store, "blobs/"), keys);
        var before = await LoadWithAsync(store, credential, asWritten);
        Assert.HasCount(swept.Count, before.UnresolvedGaps, Describe(before.UnresolvedGaps));
    }

    /// <summary>
    /// The accounting closes no gap a missing delta leaves. The first
    /// backup's deltas are removed before the second runs, so exactly their
    /// numbers stay unresolved: the journal records and blobs around them are
    /// accounted for, and they are not.
    /// </summary>
    [TestMethod]
    public async Task AMissingDelta_StaysAGap()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue("missing");
        var sequence = Sequence("missing");

        await Orchestrator(store, keys, credential, sequence, catalogue)
            .PublishAsync(Job(Tree(files: 3, seed: 900), 0xD1, Now), CancellationToken.None);
        var lost = StoredKeys(store, "index/delta/");
        Assert.IsNotEmpty(lost);
        foreach (var key in lost)
        {
            await store.DeleteAsync(ObjectKey.Parse(key), DeleteConditions.None, CancellationToken.None);
        }

        await Orchestrator(store, keys, credential, sequence, catalogue)
            .PublishAsync(Job(Tree(files: 3, seed: 1_000), 0xD2, Now + 60_000), CancellationToken.None);

        var accounting = await SequenceAccounting.LoadAsync(store, Repo, credential, maxGeneration: 0, CancellationToken.None);
        var state = await LoadAsync(store, credential, accounting);
        Assert.HasCount(lost.Count, state.UnresolvedGaps, Describe(state.UnresolvedGaps));
    }

    /// <summary>
    /// Only a record that opens and verifies accounts for its number. An
    /// object planted under a journal key at a missing delta's number would
    /// otherwise close the very gap that shows the delta was dropped.
    /// </summary>
    [TestMethod]
    public async Task AnObjectPlantedUnderAJournalKey_AccountsForNothing()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue("planted");
        var sequence = Sequence("planted");

        await Orchestrator(store, keys, credential, sequence, catalogue)
            .PublishAsync(Job(Tree(files: 3, seed: 1_100), 0xE1, Now), CancellationToken.None);
        foreach (var key in StoredKeys(store, "index/delta/"))
        {
            await store.DeleteAsync(ObjectKey.Parse(key), DeleteConditions.None, CancellationToken.None);
        }

        await Orchestrator(store, keys, credential, sequence, catalogue)
            .PublishAsync(Job(Tree(files: 3, seed: 1_200), 0xE2, Now + 60_000), CancellationToken.None);

        var accounting = await SequenceAccounting.LoadAsync(store, Repo, credential, maxGeneration: 0, CancellationToken.None);
        var gaps = (await LoadAsync(store, credential, accounting)).UnresolvedGaps;
        Assert.IsNotEmpty(gaps);

        var (writer, number) = gaps[0];
        await store.PutAsync(
            MetadataStoreKeys.Journal(writer, number),
            _ => ValueTask.FromResult<Stream>(new MemoryStream("not a journal record"u8.ToArray())),
            PutConditions.None,
            CancellationToken.None);

        var replanted = await SequenceAccounting.LoadAsync(store, Repo, credential, maxGeneration: 0, CancellationToken.None);
        Assert.Contains((writer, number), (await LoadAsync(store, credential, replanted)).UnresolvedGaps);
    }

    /// <summary>
    /// What the accounting costs a rebuild: one listing of the journal and
    /// one read of each record in it. It reads no delta, blob or snapshot, so
    /// its cost grows with the journal alone.
    /// </summary>
    [TestMethod]
    public async Task TheAccounting_ReadsEachJournalRecordOnce_AndNothingElse()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue("cost");
        var sequence = Sequence("cost");

        for (var run = 0; run < 2; run++)
        {
            await Orchestrator(store, keys, credential, sequence, catalogue)
                .PublishAsync(Job(Tree(files: 3, seed: 1_300 + (100 * run)), (byte)(0xF1 + run), Now + ((ulong)run * 60_000)), CancellationToken.None);
        }

        var counting = new Counting(store);
        await SequenceAccounting.LoadAsync(counting, Repo, credential, maxGeneration: 0, CancellationToken.None);

        SequenceAssert.AreEqual(["journal/"], counting.Listings);
        SequenceAssert.AreEqual(
            StoredKeys(store, "journal/").Order(StringComparer.Ordinal),
            counting.ReadKeys.Order(StringComparer.Ordinal));
    }

    private static bool IsGapFinding(DamageFinding finding) =>
        finding.Kind == DamageKind.MissingIndexObject
        && finding.Detail.Contains("is unaccounted for", StringComparison.Ordinal);

    private static string Describe(IReadOnlyList<(WriterId Writer, ulong Sequence)> gaps) =>
        string.Join(", ", gaps.Select(gap => gap.Sequence));

    /// <summary>
    /// Loads the index. Aged, the current generation is two past every
    /// delta's, the patience the product's rebuild waits, so an unaccounted
    /// number is damage rather than unresolved.
    /// </summary>
    private static async Task<IndexState> LoadAsync(
        IObjectStore store, RepositoryWriteCredential credential, SequenceAccounting? accounting, bool aged = false)
    {
        using var loader = new IndexLoader(store, Repo, credential);
        return await loader.LoadAsync(
            currentGeneration: aged ? 2UL : 0UL,
            gapPatienceGenerations: 2,
            isSequenceAccountedAsync: accounting is null ? null : accounting.IsAccountedAsync,
            blobState: null,
            CancellationToken.None);
    }

    /// <summary>
    /// ADR-0022 Decision 7 before its 2026-10 amendment: a named blob
    /// accounted for its number only while it existed, unless its intent's
    /// retirement was Completed.
    /// </summary>
    private static Func<WriterId, ulong, ValueTask<bool>> AccountingAsWrittenBefore(
        IReadOnlyList<JournalRecord> records, IReadOnlyCollection<string> storedBlobKeys, RepositoryKeySet keys)
    {
        var journalNumbers = records.Select(record => (record.WriterId, record.Sequence)).ToHashSet();
        var completed = records
            .Where(record => record.Payload is JournalPayload.IntentRetirement { Outcome: IntentOutcome.Completed })
            .Select(record => ((JournalPayload.IntentRetirement)record.Payload).RetiresSequence)
            .ToHashSet();

        var namedByAny = new HashSet<BlobId>();
        var namedByCompleted = new HashSet<BlobId>();
        foreach (var record in records)
        {
            var (intent, named) = record.Payload switch
            {
                JournalPayload.WriteIntent written => (record.Sequence, written.IntendedBlobIds),
                JournalPayload.IntentExtension extension => (extension.ExtendsSequence, extension.AdditionalBlobIds),
                _ => (0UL, (IReadOnlyList<BlobId>)[]),
            };
            namedByAny.UnionWith(named);
            if (completed.Contains(intent))
            {
                namedByCompleted.UnionWith(named);
            }
        }

        var stored = storedBlobKeys.ToHashSet(StringComparer.Ordinal);
        using var storeKeys = new StoreBlobKeyDeriver(keys.KeyIdKey);
        var present = namedByAny
            .Where(blob => new[] { BlobClass.Data, BlobClass.Metadata }
                .Any(blobClass => stored.Contains(BlobStoreKeys.ForBlob(blobClass, storeKeys.Derive(blob)).ToString())))
            .ToHashSet();

        return (writer, number) =>
        {
            var blob = BlobId.FromWriterCounter(writer, number);
            return ValueTask.FromResult(
                journalNumbers.Contains((writer, number)) || present.Contains(blob) || namedByCompleted.Contains(blob));
        };
    }

    private static async Task<IndexState> LoadWithAsync(
        IObjectStore store, RepositoryWriteCredential credential, Func<WriterId, ulong, ValueTask<bool>> accounting)
    {
        using var loader = new IndexLoader(store, Repo, credential);
        return await loader.LoadAsync(
            currentGeneration: 0, gapPatienceGenerations: 2, accounting, blobState: null, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<JournalRecord>> JournalAsync(
        LocalFileSystemObjectStore store, RepositoryWriteCredential credential)
    {
        using var reader = new JournalReader(store, Repo, credential);
        var (records, unparseable, _) = await reader.LoadAsync(maxGeneration: 0, CancellationToken.None);
        Assert.AreEqual(0, unparseable);
        return records;
    }

    private static List<string> StoredKeys(LocalFileSystemObjectStore store, string prefix) =>
        [.. store.ListAsync(ObjectPrefix.Parse(prefix), ListOptions.Default, CancellationToken.None)
            .ToBlockingEnumerable()
            .Select(entry => entry.Key.Value)];

    private static byte[] Incompressible(int seed, int length)
    {
        var content = new byte[length];
        new Random(seed).NextBytes(content);
        return content;
    }

    /// <summary>
    /// <paramref name="files"/> incompressible files of 256 KiB: at the
    /// harness's 256 KiB blob target, about a data blob a file.
    /// </summary>
    private static FakeFileSystemSource Tree(int files, int seed)
    {
        var source = new FakeFileSystemSource();
        for (var index = 0; index < files; index++)
        {
            source.AddFile($"data/{index:d3}.bin", Incompressible(seed + index, 256 * KiB), fileId: (ulong)(30_000 + index));
        }

        return source;
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte snapshotSeed, ulong now) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(snapshotSeed, 16).ToArray(),
        NowUnixMilliseconds = now,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "fallbackplan-tests/1.0",
    };

    private CatalogueDb OpenCatalogue(string name) =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, $"{name}.db"), Repo);

    private WriterSequence Sequence(string name) =>
        new(new FileSequenceStateStore(Path.Combine(SpoolDirectory, $"{name}-sequence.txt")));

    private PublicationOrchestrator Orchestrator(
        IObjectStore store,
        RepositoryKeySet keys,
        RepositoryWriteCredential credential,
        WriterSequence sequence,
        CatalogueDb catalogue,
        IPublicationObserver? observer = null) =>
        new(
            SmallBlobPolicy,
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            credential,
            store,
            sequence,
            SpoolDirectory,
            FormatVersions.SealedDataPlane,
            observer,
            catalogue);
}
