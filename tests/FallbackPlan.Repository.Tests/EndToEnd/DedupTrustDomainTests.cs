using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using CatalogueRebuilder = FallbackPlan.Repository.Catalogue.CatalogueRebuilder;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Verify-on-reuse and trust-domain gating (FR-DED-001, FR-DED-002,
/// FR-DED-003, NFR-SEC-007; specification 09 §5;
/// [ADR-0006](../../../../docs/adr/0006-object-identifiers-and-dedup-trust-domains.md)).
///
/// Every other end-to-end suite has one writer, and with one writer all three
/// domains behave identically — which is the requirement, not a gap. These
/// tests are the ones that can tell them apart: a second writer publishes into
/// a repository the first already filled, and what the second does with the
/// first's segments is the whole of the decision.
/// </summary>
[TestClass]
public sealed class DedupTrustDomainTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private static readonly WriterId SecondWriter =
        WriterId.FromBytes(Convert.FromHexString("b0b1b2b3b4b5b6b7b8b9babbbcbdbebf"));

    [TestMethod]
    public async Task DedupReuse_ASingleWritersOwnSegments_AreReusedWithNoVerificationRead()
    {
        var store = new CountingObjectStore(CreateStore());
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("first");

        var source = OneFileSource();

        await Publish(store, keys, hierarchy, catalogue, Writer, "first", DedupTrustDomain.Repository)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        var readsBefore = store.Reads;

        // The same writer, the same content, a second snapshot. Its segments
        // are already in the index and they are its own, so they are reused —
        // and the fetch-and-decrypt the default domain performs for another
        // writer's bytes is never issued, because FR-DED-002's acceptance
        // criterion says a fresh single-device repository performs none.
        var second = await Publish(store, keys, hierarchy, catalogue, Writer, "first", DedupTrustDomain.Repository)
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
    public async Task RepositoryDomain_AnotherWritersSegments_AreConfirmedThenReused()
    {
        using var second = await SecondWriterPublishes(DedupTrustDomain.Repository);

        // Confirmed, then referenced: no content record is written for bytes
        // the repository already holds, and nothing was found wrong with them.
        Assert.AreEqual(0, second.Published.ContentBlobs.Sum(blob => blob.RecordCount));
        Assert.IsEmpty(second.Catalogue.Findings());
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
        using var hierarchy = new KeyHierarchy(MasterKey);
        var spool = Path.Combine(SpoolDirectory, "unacknowledged");
        Directory.CreateDirectory(spool);

        var refusal = Assert.ThrowsExactly<ArgumentException>(() => new PublicationOrchestrator(
            unacknowledged, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))), spool));
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
        // what opting out of verification buys and costs. It is here as a
        // control: it is the same corruption the default domain catches below,
        // so the two tests together show the read is what makes the difference
        // rather than something else about the fixture.
        Assert.AreEqual(0, second.Published.ContentBlobs.Sum(blob => blob.RecordCount));
        Assert.IsEmpty(second.Catalogue.Findings());
    }

    [TestMethod]
    public async Task RepositoryDomain_ASegmentFailsVerification_IsWrittenAgainAndReported()
    {
        using var second = await SecondWriterPublishes(
            DedupTrustDomain.Repository, corruptFirstWritersData: true);

        // T-10, caught at write time: the other writer's record does not
        // decrypt to what its identifier claims, so it is not referenced. The
        // backup still succeeds — this writer has the real bytes in front of it
        // and stores them.
        Assert.IsTrue(second.Published.ContentBlobs.Sum(blob => blob.RecordCount) > 0);

        var findings = second.Catalogue.Findings();
        Assert.Contains(finding => finding.Kind == DamageKind.CorruptRecord, findings);
        Assert.Contains(finding => finding.Detail.Contains("offered for reuse", StringComparison.Ordinal), findings);
    }

    [TestMethod]
    public async Task RepositoryDomain_ASegmentAlreadyConfirmed_IsNotReadAgain()
    {
        using var second = await SecondWriterPublishes(DedupTrustDomain.Repository);
        var readsToVerify = second.Store.Reads;

        // Corrupting the blob *after* it was confirmed is a probe, not a
        // scenario: nothing that re-read it could still call it good. If the
        // next publication reuses the segment anyway, the only explanation is
        // that it did not read it — which is the claim under test, that the
        // confirmation is paid once per object and not once per backup.
        CorruptOneDataBlob();

        var source = OneFileSource();
        var third = await Publish(
                second.Store, second.Keys, second.Hierarchy, second.Catalogue, SecondWriter, "second",
                DedupTrustDomain.Repository)
            .PublishAsync(
                Job(source, 0xB3, now: 1_722_600_000_003) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xB2, 16).ToArray(),
                },
                CancellationToken.None);

        Assert.AreEqual(0, third.ContentBlobs.Sum(blob => blob.RecordCount));
        Assert.IsEmpty(second.Catalogue.Findings());

        // The measured shape of the cost ADR-0006 asked to see: the reads that
        // confirmed the other writer's objects happened once, and the backup
        // that followed issued fewer than that to publish the same tree.
        Assert.IsTrue(
            second.Store.Reads - readsToVerify < readsToVerify,
            $"the confirming publication read {readsToVerify} times; the next read "
            + $"{second.Store.Reads - readsToVerify}");
    }

    [TestMethod]
    public async Task RepositoryDomain_ManySegmentsVerifiedConcurrently_ReusesThemAllWithoutRacing()
    {
        var store = new CountingObjectStore(CreateStore());
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        var source = ManyFileSource();

        using (var first = OpenCatalogue("first"))
        {
            await Publish(store, keys, hierarchy, first, Writer, "first", DedupTrustDomain.Repository)
                .PublishAsync(Job(source, 0xC1), CancellationToken.None);
        }

        using var second = OpenCatalogue("second");
        using (var loader = new IndexLoader(store, Repo, hierarchy))
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
                store, keys, hierarchy, second, SecondWriter, "second", DedupTrustDomain.Repository, concurrency: 8)
            .PublishAsync(
                Job(source, 0xC2, now: 1_722_600_000_002) with
                {
                    DeviceId = Enumerable.Repeat((byte)0x44, 16).ToArray(),
                },
                CancellationToken.None);

        // Every segment verified and was referenced: nothing re-written, and
        // nothing reported wrong. A race that lost a cache entry would show up
        // here as content records the second writer should never have needed.
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
        var hierarchy = new KeyHierarchy(MasterKey);
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
                await Publish(store, keys, hierarchy, first, Writer, "first", DedupTrustDomain.Repository)
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
            using (var loader = new IndexLoader(store, Repo, hierarchy))
            {
                await new CatalogueRebuilder(loader).RebuildAsync(
                    secondCatalogue, currentGeneration: 0, gapPatienceGenerations: 2,
                    isSequenceAccountedAsync: null, CancellationToken.None);
            }

            var published = await Publish(store, keys, hierarchy, secondCatalogue, SecondWriter, "second", domain)
                .PublishAsync(
                    Job(source, 0xB2, now: 1_722_600_000_002) with
                    {
                        DeviceId = Enumerable.Repeat((byte)0x44, 16).ToArray(),
                    },
                    CancellationToken.None);

            return new SecondWriterRun(store, keys, hierarchy, secondCatalogue, published);
        }
        catch
        {
            secondCatalogue?.Dispose();
            hierarchy.Dispose();
            keys.Dispose();
            throw;
        }
    }

    /// <summary>One two-writer fixture and what the second writer published.</summary>
    private sealed record SecondWriterRun(
        CountingObjectStore Store,
        RepositoryKeySet Keys,
        KeyHierarchy Hierarchy,
        CatalogueDb Catalogue,
        PublishedTreeSnapshot Published) : IDisposable
    {
        public void Dispose()
        {
            Catalogue.Dispose();
            Hierarchy.Dispose();
            Keys.Dispose();
        }
    }

    /// <summary>
    /// Flips a byte inside the sole data blob's first record ciphertext —
    /// past the envelope and record header, well short of the footer — so the
    /// blob still opens and the record no longer authenticates.
    /// </summary>
    private void CorruptOneDataBlob()
    {
        var blob = Directory.EnumerateFiles(Path.Combine(StoreRoot, "blobs", "data"), "*", SearchOption.AllDirectories)
            .Single();

        using var handle = File.Open(blob, FileMode.Open, FileAccess.ReadWrite);
        handle.Seek(200, SeekOrigin.Begin);
        var original = handle.ReadByte();
        handle.Seek(200, SeekOrigin.Begin);
        handle.WriteByte((byte)(original ^ 0xFF));
    }

    private CatalogueDb OpenCatalogue(string name) =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, $"catalogue-{name}.db"), Repo);

    private PublicationOrchestrator Publish(
        IObjectStore store,
        RepositoryKeySet keys,
        KeyHierarchy hierarchy,
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
            hierarchy,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool,
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
        RootPath = "/",
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
