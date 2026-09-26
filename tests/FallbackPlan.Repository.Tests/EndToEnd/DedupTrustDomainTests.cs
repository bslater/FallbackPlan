using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using CatalogueRebuilder = FallbackPlan.Repository.Catalogue.CatalogueRebuilder;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Trust-domain gating (FR-DED-001, FR-DED-002, FR-DED-003, NFR-SEC-007;
/// specification 09 §5;
/// [ADR-0006](../../../../docs/adr/0006-object-identifiers-and-dedup-trust-domains.md)).
///
/// Every other end-to-end suite has one writer, and with one writer every
/// domain behaves identically — which is the requirement, not a gap. These
/// tests are the ones that can tell them apart: a second writer publishes into
/// a repository the first already filled, and what the second does with the
/// first's segments is the whole of the decision.
///
/// The <b>repository</b> domain — confirm another writer's segment by reading
/// its content back — is refused by name rather than exercised: a writer holds
/// no content key (ADR-0042 §7), so the read it needs cannot happen, and the
/// device domain is the default (ADR-0006, amended). What these establish
/// about it is exactly that refusal.
/// </summary>
[TestClass]
public sealed class DedupTrustDomainTests : ArchiveTestHarness
{
    private static readonly WriterId SecondWriter =
        WriterId.FromBytes(Convert.FromHexString("b0b1b2b3b4b5b6b7b8b9babbbcbdbebf"));

    [TestMethod]
    public async Task DedupReuse_ASingleWritersOwnSegments_AreReusedWithNoVerificationRead()
    {
        var store = new CountingObjectStore(CreateStore());
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue("first");

        var source = OneFileSource();

        await Publish(store, keys, credential, catalogue, Writer, "first", DedupTrustDomain.Device)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        var readsBefore = store.Reads;

        // The same writer, the same content, a second snapshot. Its segments
        // are already in the index and they are its own, so they are reused —
        // and no fetch is issued for them, because FR-DED-002's acceptance
        // criterion says a fresh single-device repository performs none.
        var second = await Publish(store, keys, credential, catalogue, Writer, "first", DedupTrustDomain.Device)
            .PublishAsync(
                Job(source, 0xA2, now: 1_722_600_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                },
                CancellationToken.None);

        Assert.AreEqual(0, second.ContentBlobs.Sum(blob => blob.RecordCount));
        Assert.IsEmpty(catalogue.Findings());

        // Not "few reads" — none. A writer's own segments are recognised by
        // attribution, and attribution costs a catalogue lookup, so the second
        // backup of an unchanged tree reads nothing from the store at all.
        Assert.AreEqual(readsBefore, store.Reads);
    }

    [TestMethod]
    public void RepositoryDomain_IsRefusedByNameWithTheRemedy()
    {
        // ADR-0042 §7: confirming another writer's segment means reading its
        // content, and a writer holds no content key. The pipeline refuses to
        // start rather than degrade every confirmation into "unavailable" and
        // quietly re-archive; the message names the device domain.
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var spool = Path.Combine(SpoolDirectory, "repository-domain");
        Directory.CreateDirectory(spool);

        var refusal = Assert.ThrowsExactly<ArgumentException>(() => new PublicationOrchestrator(
            SmallBlobPolicy with { DedupTrustDomain = DedupTrustDomain.Repository },
            Repo, Writer, KeyGeneration.Zero, keys, credential, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))), spool,
            FormatVersions.SealedDataPlane));
        Assert.Contains("device", refusal.Message, StringComparison.Ordinal);
        Assert.AreEqual(DedupTrustDomain.Device, CapturePolicy.Default.DedupTrustDomain);
    }

    [TestMethod]
    public async Task DeviceDomain_AnotherWritersSegments_AreRefusedAndStoredAgain()
    {
        using var second = await SecondWriterPublishes(DedupTrustDomain.Device);

        // The hardened opt-in, doing the thing it exists to do: another
        // device's bytes are not referenced at all, at the cost of storing the
        // same content twice. That cost is the point — this is the domain for
        // a user who does not want their backup to depend on another member's
        // record being honest.
        Assert.IsTrue(second.Published.ContentBlobs.Sum(blob => blob.RecordCount) > 0);
        Assert.IsEmpty(second.Catalogue.Findings());
    }

    [TestMethod]
    public void UnverifiedDomain_WithoutTheAcknowledgement_CannotBeEnabled()
    {
        // FR-DED-004: opting out of reuse verification is opting into
        // trusting every other writer's honesty and health. The pipeline
        // refuses to start until that is acknowledged explicitly, and the
        // policy's own validation names the same defect.
        var unacknowledged = SmallBlobPolicy with { DedupTrustDomain = DedupTrustDomain.RepositoryUnverified };

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var spool = Path.Combine(SpoolDirectory, "unacknowledged");
        Directory.CreateDirectory(spool);

        var refusal = Assert.ThrowsExactly<ArgumentException>(() => new PublicationOrchestrator(
            unacknowledged, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))), spool,
            FormatVersions.SealedDataPlane));
        Assert.Contains("acknowledge", refusal.Message, StringComparison.OrdinalIgnoreCase);

        var validation = unacknowledged.Validate();
        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Has("unverified_dedup_unacknowledged"));
    }

    [TestMethod]
    public async Task UnverifiedDomain_AnotherWritersSegments_AreReusedWithoutBeingRead()
    {
        using var second = await SecondWriterPublishes(
            DedupTrustDomain.RepositoryUnverified, corruptFirstWritersData: true);

        // The segments are corrupt and referenced anyway, which is exactly
        // what opting out of verification buys and costs: nothing read them,
        // so nothing could have noticed.
        Assert.AreEqual(0, second.Published.ContentBlobs.Sum(blob => blob.RecordCount));
        Assert.IsEmpty(second.Catalogue.Findings());
    }

    [TestMethod]
    public async Task UnverifiedDomain_ManySegmentsReusedConcurrently_ReusesThemAllWithoutRacing()
    {
        var store = new CountingObjectStore(CreateStore());
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var source = ManyFileSource();

        using (var first = OpenCatalogue("first"))
        {
            await Publish(store, keys, credential, first, Writer, "first", DedupTrustDomain.Device)
                .PublishAsync(Job(source, 0xC1), CancellationToken.None);
        }

        using var second = OpenCatalogue("second");
        using (var loader = new IndexLoader(store, Repo, credential))
        {
            await new CatalogueRebuilder(loader).RebuildAsync(
                second, currentGeneration: 0, gapPatienceGenerations: 2,
                isSequenceAccountedAsync: null, CancellationToken.None);
        }

        // The reuse decision is asked from the archive pipeline, which is
        // concurrent, and answering it reads the catalogue and caches an open
        // blob. Both of those were built for the sequential tree walk, and one
        // file's two segments never made the overlap wide enough to notice:
        // the first Windows run of the suite died on a corrupted Dictionary
        // inside the blob cache. Twenty-four files across many blobs at eight
        // workers is wide enough.
        var published = await Publish(
                store, keys, credential, second, SecondWriter, "second", DedupTrustDomain.RepositoryUnverified,
                concurrency: 8)
            .PublishAsync(
                Job(source, 0xC2, now: 1_722_600_000_002) with
                {
                    DeviceId = Enumerable.Repeat((byte)0x44, 16).ToArray(),
                },
                CancellationToken.None);

        // Every segment was referenced: nothing re-written, and nothing
        // reported wrong. A race that lost a cache entry would show up here
        // as content records the second writer should never have needed.
        Assert.AreEqual(0, published.ContentBlobs.Sum(blob => blob.RecordCount));
        Assert.IsEmpty(second.Findings());
    }

    /// <summary>
    /// Writer one fills the repository; writer two arrives with a catalogue
    /// rebuilt from the index alone — which is how a second device really
    /// learns where anything is — and publishes the same content.
    /// </summary>
    private async Task<SecondWriterRun> SecondWriterPublishes(
        DedupTrustDomain domain, bool corruptFirstWritersData = false)
    {
        var store = new CountingObjectStore(CreateStore());
        var keys = CreateKeys();
        var credential = CreateCredential();
        CatalogueDb? secondCatalogue = null;

        // Nothing here is owned by the caller until the record is returned, so
        // a publication that throws has to close its own files. Otherwise the
        // real exception is lost behind a cleanup failure — "the process
        // cannot access 'catalogue-second.db'" — which says nothing about
        // what actually went wrong.
        try
        {
            var source = OneFileSource();

            using (var first = OpenCatalogue("first"))
            {
                await Publish(store, keys, credential, first, Writer, "first", DedupTrustDomain.Device)
                    .PublishAsync(Job(source, 0xB1), CancellationToken.None);
            }

            if (corruptFirstWritersData)
            {
                CorruptOneDataBlob();
            }

            // The second device knows nothing locally. It learns the first
            // writer's locations the only way a second device can: from the
            // repository's own index objects.
            secondCatalogue = OpenCatalogue("second");
            using (var loader = new IndexLoader(store, Repo, credential))
            {
                await new CatalogueRebuilder(loader).RebuildAsync(
                    secondCatalogue, currentGeneration: 0, gapPatienceGenerations: 2,
                    isSequenceAccountedAsync: null, CancellationToken.None);
            }

            var published = await Publish(store, keys, credential, secondCatalogue, SecondWriter, "second", domain)
                .PublishAsync(
                    Job(source, 0xB2, now: 1_722_600_000_002) with
                    {
                        DeviceId = Enumerable.Repeat((byte)0x44, 16).ToArray(),
                    },
                    CancellationToken.None);

            return new SecondWriterRun(store, keys, credential, secondCatalogue, published);
        }
        catch
        {
            secondCatalogue?.Dispose();
            credential.Dispose();
            keys.Dispose();
            throw;
        }
    }

    /// <summary>One two-writer fixture and what the second writer published.</summary>
    private sealed record SecondWriterRun(
        CountingObjectStore Store,
        RepositoryKeySet Keys,
        RepositoryWriteCredential Credential,
        CatalogueDb Catalogue,
        PublishedTreeSnapshot Published) : IDisposable
    {
        public void Dispose()
        {
            Catalogue.Dispose();
            Credential.Dispose();
            Keys.Dispose();
        }
    }

    /// <summary>
    /// Flips a byte inside the sole data blob's first record ciphertext —
    /// past the sealed envelope and record header, well short of the footer —
    /// so the blob still opens and the record no longer authenticates.
    /// </summary>
    private void CorruptOneDataBlob()
    {
        var blob = Directory.EnumerateFiles(Path.Combine(StoreRoot, "blobs", "data"), "*", SearchOption.AllDirectories)
            .Single();

        var offset = BlobEnvelope.MaxLength + RecordHeader.Length + 8;
        using var handle = File.Open(blob, FileMode.Open, FileAccess.ReadWrite);
        handle.Seek(offset, SeekOrigin.Begin);
        var original = handle.ReadByte();
        handle.Seek(offset, SeekOrigin.Begin);
        handle.WriteByte((byte)(original ^ 0xFF));
    }

    private CatalogueDb OpenCatalogue(string name) =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, $"catalogue-{name}.db"), Repo);

    private PublicationOrchestrator Publish(
        IObjectStore store,
        RepositoryKeySet keys,
        RepositoryWriteCredential credential,
        CatalogueDb catalogue,
        WriterId writer,
        string spoolName,
        DedupTrustDomain domain,
        int concurrency = CapturePolicy.DefaultConcurrency)
    {
        var spool = Path.Combine(SpoolDirectory, spoolName);
        Directory.CreateDirectory(spool);

        return new PublicationOrchestrator(
            SmallBlobPolicy with
            {
                DedupTrustDomain = domain,
                Concurrency = concurrency,
                // FR-DED-004: the unverified domain cannot be enabled without
                // the explicit acknowledgement; these tests opt in knowingly.
                AcknowledgesUnverifiedDedupRisk = domain == DedupTrustDomain.RepositoryUnverified,
            },
            Repo,
            writer,
            KeyGeneration.Zero,
            keys,
            credential,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool,
            FormatVersions.SealedDataPlane,
            observer: null,
            catalogue);
    }

    private static FakeFileSystemSource OneFileSource()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("shared/payload.bin", Deterministic(120_000, 17), fileId: 9_001);
        return source;
    }

    /// <summary>
    /// Enough distinct content to fill many blobs, so a second writer's reuse
    /// decisions land on many different blobs at once rather than queueing on
    /// one.
    /// </summary>
    private static FakeFileSystemSource ManyFileSource()
    {
        var source = new FakeFileSystemSource();

        for (var i = 0; i < 24; i++)
        {
            source.AddFile(
                $"shared/payload-{i:D2}.bin", Deterministic(192_000, (byte)(20 + i)), fileId: (ulong)(9_100 + i));
        }

        return source;
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte snapshotSeed, ulong now = 1_722_600_000_000) => new()
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

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }
}
