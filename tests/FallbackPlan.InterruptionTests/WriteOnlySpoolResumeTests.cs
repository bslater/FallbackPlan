using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// The sealed (write-only, ADR-0042) spool under real process death
/// (specification 05 §6; NFR-SEC-003, NFR-REL-001): a v2 checkpoint carries
/// the blob's content key, resume re-emits the sealed bytes under that same
/// key, and every restart draws a fresh salt AND a fresh content key — the
/// pair that keeps an ordinal from being reused under keys that already
/// covered it.
/// </summary>
[TestClass]
public sealed class WriteOnlySpoolResumeTests : InterruptionHarness
{
    private const string PassphraseText = "the interruption drill passphrase";

    [TestMethod]
    public async Task WriteOnlySpool_JobKilledMidBlob_LeavesACheckpointCarryingTheContentKey()
    {
        var store = CreateStore();
        var (opened, authority) = await CreateWriteOnlyAsync(store);
        using (opened)
        using (authority)
        {
            await KillMidBlobAsync(store, opened);

            // The sidecar is there, parses, and holds the 32-byte content
            // key — without it a resumed process could not seal the rest of
            // the blob under the key the spooled records already used.
            Assert.IsTrue(SpoolCheckpoint.TryParse(await File.ReadAllBytesAsync(SidecarPath()), out var checkpoint));
            Assert.AreEqual(32, checkpoint!.ContentKey!.Value.Length);
        }
    }

    [TestMethod]
    public async Task WriteOnlySpool_ResumedAfterAKill_ReEmitsItsSealedBytesUnderTheCheckpointedContentKey()
    {
        var store = CreateStore();
        var (opened, authority) = await CreateWriteOnlyAsync(store);
        using (opened)
        using (authority)
        {
            var content = await KillMidBlobAsync(store, opened);
            var spooled = await ReadSpoolAsync();
            Assert.IsTrue(SpoolCheckpoint.TryParse(await File.ReadAllBytesAsync(SidecarPath()), out var checkpoint));
            var checkpointedKey = checkpoint!.ContentKey!.Value.ToArray();

            // A fresh process life over the same durable state.
            using (var source = new MemoryStream(content))
            {
                await CreateWriteOnlyOrchestrator(store, opened)
                    .PublishAsync(Job(source, snapshotSeed: 0x81), CancellationToken.None);
            }

            SequenceAssert.AreEqual(content, await RestoreUnderGrantAsync(store, opened, authority, snapshotSeed: 0x81));

            // Resumed, not restarted — and under the SAME content key: the
            // finished blob starts with the interrupted run's sealed bytes
            // verbatim, and its sealed share opens (under the grant) to the
            // key the checkpoint carried. Re-keying mid-blob would be the
            // v2 shape of the nonce-reuse failure 05 §6.1 forbids.
            var resumed = (await SealedBlobsAsync(store)).Single(blob => StartsWith(blob, spooled));
            SequenceAssert.AreEqual(checkpointedKey, OpenSealedShare(authority, opened.RepositoryId, resumed));
        }
    }

    [TestMethod]
    public async Task WriteOnlySpool_TailIsTorn_RestartsWithAFreshSaltAndAFreshContentKey()
    {
        var store = CreateStore();
        var (opened, authority) = await CreateWriteOnlyAsync(store);
        using (opened)
        using (authority)
        {
            var content = await KillMidBlobAsync(store, opened);
            var abandonedSalt = SaltOf(await ReadSpoolAsync());
            Assert.IsTrue(SpoolCheckpoint.TryParse(await File.ReadAllBytesAsync(SidecarPath()), out var checkpoint));
            var abandonedKey = checkpoint!.ContentKey!.Value.ToArray();

            // A torn write the crash left behind — restart, never truncate.
            await using (var spool = new FileStream(SpoolFiles().Single(), FileMode.Append, FileAccess.Write))
            {
                await spool.WriteAsync(new byte[137], CancellationToken.None);
            }

            using (var source = new MemoryStream(content))
            {
                await CreateWriteOnlyOrchestrator(store, opened)
                    .PublishAsync(Job(source, snapshotSeed: 0x82), CancellationToken.None);
            }

            SequenceAssert.AreEqual(content, await RestoreUnderGrantAsync(store, opened, authority, snapshotSeed: 0x82));

            // The restarted blob shares NEITHER the salt NOR the content key
            // with the abandoned spool: both cover ordinals, so both must be
            // fresh for the ordinal space to restart safely.
            foreach (var blob in await SealedBlobsAsync(store))
            {
                Assert.AreNotEqual(abandonedSalt, SaltOf(blob));
                if (IsSealedDataBlob(blob))
                {
                    SequenceAssert.AreNotEqual(abandonedKey, OpenSealedShare(authority, opened.RepositoryId, blob));
                }
            }
        }
    }

    [TestMethod]
    public async Task WriteOnlySpool_SidecarDeleted_RestartsRatherThanResuming()
    {
        var store = CreateStore();
        var (opened, authority) = await CreateWriteOnlyAsync(store);
        using (opened)
        using (authority)
        {
            var content = await KillMidBlobAsync(store, opened);
            var spooled = await ReadSpoolAsync();
            var abandonedSalt = SaltOf(spooled);

            // The checkpoint is the resume warrant; without it the spooled
            // bytes cannot be authenticated to a resume point and the blob
            // restarts (05 §6.2).
            File.Delete(SidecarPath());

            using (var source = new MemoryStream(content))
            {
                await CreateWriteOnlyOrchestrator(store, opened)
                    .PublishAsync(Job(source, snapshotSeed: 0x83), CancellationToken.None);
            }

            SequenceAssert.AreEqual(content, await RestoreUnderGrantAsync(store, opened, authority, snapshotSeed: 0x83));
            foreach (var blob in await SealedBlobsAsync(store))
            {
                Assert.AreNotEqual(abandonedSalt, SaltOf(blob));
                Assert.IsFalse(StartsWith(blob, spooled), "an unwarranted spool must never be resumed");
            }
        }
    }

    [TestMethod]
    public async Task WriteOnlyPublication_ABlobPutTears_RepublishVerifiesWithTornDamageDistinctFromSealedContent()
    {
        var store = CreateStore();
        var (opened, authority) = await CreateWriteOnlyAsync(store);
        using (opened)
        using (authority)
        {
            var content = BuildFile(seed: 29);

            // The first blob put of the run tears mid-upload: partial bytes
            // become durable under the final key.
            var tearing = new TearingObjectStore(store, tearAtBlobPut: 1);
            using (var source = new MemoryStream(content))
            {
                await Assert.ThrowsExactlyAsync<IOException>(async () =>
                    await CreateWriteOnlyOrchestrator(tearing, opened)
                        .PublishAsync(Job(source, snapshotSeed: 0x84), CancellationToken.None));
            }

            Assert.IsNotNull(tearing.TornKey, "the tear never happened — the test proved nothing");

            using (var source = new MemoryStream(content))
            {
                await CreateWriteOnlyOrchestrator(store, opened)
                    .PublishAsync(Job(source, snapshotSeed: 0x85), CancellationToken.None);
            }

            SequenceAssert.AreEqual(content, await RestoreUnderGrantAsync(store, opened, authority, snapshotSeed: 0x85));

            // Verify keeps the two truths apart: the torn orphan is DAMAGE
            // and fails; every whole blob passes with its sealed records
            // counted as a stated incapacity, never as failure (ADR-0042
            // decision 7).
            using var verifier = new VerifyEngine(opened.RepositoryId, opened.Keys, store);
            var sawSealedData = false;
            await foreach (var entry in store.ListAsync(
                ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
            {
                var result = await verifier.VerifyBlobAsync(
                    entry.Key, entry.Length, VerifyLevel.EveryRecord, CancellationToken.None);

                if (entry.Key.ToString() == tearing.TornKey)
                {
                    Assert.IsFalse(result.Ok, "a torn blob is damage and must fail verification");
                    continue;
                }

                Assert.IsTrue(result.Ok, $"{entry.Key.Value}: {result.Detail}");
                if (entry.Key.Value.StartsWith("blobs/data/", StringComparison.Ordinal))
                {
                    sawSealedData = true;
                    Assert.AreEqual(0, result.RecordsVerified);
                    Assert.IsTrue(result.RecordsSealed > 0);
                }
            }

            Assert.IsTrue(sawSealedData, "the drill must have verified at least one sealed data blob");
        }
    }

    private static async Task<(OpenedRepository Opened, RepositoryReadAuthority Authority)> CreateWriteOnlyAsync(
        LocalFileSystemObjectStore store)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        return await RepositoryLifecycle.CreateWriteOnlyAsync(
            store,
            passphrase,
            RepositoryCreationSettings.Default with { CreatedBy = "interruption-tests/1.0" },
            createdAtUnixMilliseconds: 1_722_600_000_000,
            CancellationToken.None);
    }

    /// <summary>
    /// The harness orchestrator, re-pointed at the write-only world: the
    /// created repository's own identity and keys, and the device trust
    /// domain (the repository domain's verify-on-reuse reads content, which
    /// write-only keys cannot — the orchestrator refuses it by name).
    /// </summary>
    private PublicationOrchestrator CreateWriteOnlyOrchestrator(IObjectStore store, OpenedRepository opened) =>
        new(
            SmallBlobPolicy with { DedupTrustDomain = DedupTrustDomain.Device },
            opened.RepositoryId, Writer, KeyGeneration.Zero, opened.Keys, opened.Hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory,
            observer: null,
            catalogue: null);

    /// <summary>Runs a publication that dies with a sealed blob still open.</summary>
    private async Task<byte[]> KillMidBlobAsync(IObjectStore store, OpenedRepository opened)
    {
        var content = BuildFile(seed: 23);

        using var source = new FaultingStream(content, failAfterBytes: 600 * 1024);
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await CreateWriteOnlyOrchestrator(store, opened)
                .PublishAsync(Job(source, snapshotSeed: 0x80), CancellationToken.None));

        return content;
    }

    /// <summary>The restore ceremony's oracle, granted: manifest walk plus content, byte for byte.</summary>
    private static async Task<byte[]> RestoreUnderGrantAsync(
        LocalFileSystemObjectStore store, OpenedRepository opened, RepositoryReadAuthority authority, byte snapshotSeed)
    {
        var wantedId = Enumerable.Repeat(snapshotSeed, 16).ToArray();

        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("snapshots/"), ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory);

            var record = StandaloneRecordFraming.Parse(memory.ToArray());
            var metadataKey = opened.Keys.DeriveClassKey(BlobClass.Metadata, record.KeyGeneration);
            Assert.IsTrue(StandaloneRecordCipher.TryOpen(record, opened.RepositoryId, metadataKey, out var plaintext));

            var decoded = SnapshotManifestCodec.Decode(plaintext);
            if (!decoded.Manifest.SnapshotId.Span.SequenceEqual(wantedId))
            {
                continue;
            }

            using var reader = new RepositoryReader(opened.RepositoryId, opened.Keys, store, authority);
            await reader.LoadBlobsAsync(CancellationToken.None);

            var treeRead = await reader.ReadSegmentAsync(decoded.Manifest.RootTree, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, treeRead.Outcome);
            var tree = TreeManifestCodec.Decode(treeRead.Plaintext!);

            var manifestRead = await reader.ReadSegmentAsync(tree.Entries[0].ObjectId, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, manifestRead.Outcome);
            var manifest = FileVersionManifestCodec.Decode(manifestRead.Plaintext!);

            using var restored = new MemoryStream();
            var restore = await new RestoreEngine(reader).RestoreFileAsync(manifest, restored, CancellationToken.None);
            Assert.IsTrue(restore.Success, restore.FailureDetail);

            return restored.ToArray();
        }

        throw new InvalidOperationException($"Snapshot {snapshotSeed:x2} was not found.");
    }

    private IEnumerable<string> SpoolFiles() =>
        Directory.Exists(SpoolDirectory)
            ? Directory.EnumerateFiles(SpoolDirectory, "blob-*.spool.checkpoint")
                .Select(path => path[..^".checkpoint".Length])
            : [];

    private string SidecarPath() =>
        Directory.EnumerateFiles(SpoolDirectory, "blob-*.spool.checkpoint").Single();

    private async Task<byte[]> ReadSpoolAsync() =>
        await File.ReadAllBytesAsync(SpoolFiles().Single(), CancellationToken.None);

    private static async Task<List<byte[]>> SealedBlobsAsync(LocalFileSystemObjectStore store)
    {
        var blobs = new List<byte[]>();

        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory);
            blobs.Add(memory.ToArray());
        }

        return blobs;
    }

    private static bool StartsWith(byte[] blob, byte[] prefix) =>
        blob.Length >= prefix.Length && blob.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static byte[] SaltOf(byte[] blobOrSpool) =>
        BlobEnvelope.Parse(blobOrSpool.AsSpan(0, Math.Min(BlobEnvelope.MaxLength, blobOrSpool.Length)))
            .BlobSalt.ToArray();

    private static bool IsSealedDataBlob(byte[] blob) =>
        BlobEnvelope.Parse(blob.AsSpan(0, Math.Min(BlobEnvelope.MaxLength, blob.Length)))
            .SealedContentKey.Length == ContentSealing.SealedLength;

    /// <summary>Opens a sealed data blob's content-key share under the grant.</summary>
    private static byte[] OpenSealedShare(RepositoryReadAuthority authority, Domain.Identifiers.RepositoryId repo, byte[] blob)
    {
        var envelope = BlobEnvelope.Parse(blob.AsSpan(0, BlobEnvelope.MaxLength));
        return SealedContentKey.Open(authority.SealingPrivateKey, envelope.SealedContentKey, repo, envelope.BlobId);
    }
}
