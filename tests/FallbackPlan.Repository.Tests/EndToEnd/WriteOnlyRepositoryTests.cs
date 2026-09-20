using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The write-only repository end to end (ADR-0042; FR-WOR-001, FR-WOR-003,
/// FR-WOR-004, NFR-SEC-010): created from one passphrase with no key object
/// anywhere in the store, backed up through the real publication pipeline
/// with the write bundle alone, browsed and planned write-only, honest about
/// sealed content without a grant, restored byte-identically with the
/// re-derived authority — and recovered on a clean machine from the
/// passphrase and the archive alone.
/// </summary>
[TestClass]
public sealed class WriteOnlyRepositoryTests : IDisposable
{
    private const string PassphraseText = "one long passphrase to rule them all";

    private static readonly Domain.Identifiers.WriterId Writer =
        Domain.Identifiers.WriterId.FromBytes(Convert.FromHexString("c0c1c2c3c4c5c6c7c8c9cacbcccdcecf"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-write-only-tests", Guid.NewGuid().ToString("n"));

    public WriteOnlyRepositoryTests() => Directory.CreateDirectory(_root);

    private LocalFileSystemObjectStore CreateStore() => new(Path.Combine(_root, "repo"));

    private static RepositoryCreationSettings SettingsFor(ushort formatVersion) =>
        RepositoryCreationSettings.Default with
        {
            CreatedBy = "write-only-tests/1.0",
            FormatVersion = formatVersion,
        };

    private static Passphrase Right() => Passphrase.Create(PassphraseText);

    /// <summary>
    /// 64 KiB segments so a modest file spans several sealed records — and
    /// the device trust domain, which is the write-only default: the
    /// repository domain's verify-on-reuse reads content, so the
    /// orchestrator refuses that combination by name (ADR-0042 §7).
    /// </summary>
    private static CapturePolicy SmallPolicy => CapturePolicy.Default with
    {
        SegmentSize = SegmentSize.Create(64 * 1024),
        DedupTrustDomain = DedupTrustDomain.Device,
        BlobWriteProfile = BlobWriteProfile.LocalDefault with
        {
            TargetSizeBytes = 256 * 1024,
            MaximumSizeBytes = 512 * 1024,
        },
    };

    private async Task<(OpenedRepository Opened, RepositoryReadAuthority Authority, CatalogueDb Catalogue, Dictionary<string, byte[]> Files)>
        CreateAndBackUpAsync(
            LocalFileSystemObjectStore store, ushort formatVersion = FormatVersions.SealedDataPlane)
    {
        using var passphrase = Right();
        var (opened, authority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
            store, passphrase, SettingsFor(formatVersion), createdAtUnixMilliseconds: 1_722_600_000_000,
            CancellationToken.None);

        var random = new Random(51);
        var files = new Dictionary<string, byte[]>
        {
            ["docs/report.bin"] = new byte[200_000],
            ["docs/notes.txt"] = new byte[900],
            ["top.bin"] = new byte[65_000],
        };
        var source = new FakeFileSystemSource();
        foreach (var (path, content) in files)
        {
            random.NextBytes(content);
            source.AddFile(path, content);
        }

        var spool = Path.Combine(_root, "spool");
        Directory.CreateDirectory(spool);
        var catalogue = CatalogueDb.Open(Path.Combine(_root, "catalogue.db"), opened.RepositoryId);

        var orchestrator = new PublicationOrchestrator(
            SmallPolicy, opened.RepositoryId, Writer, KeyGeneration.Zero, opened.Keys, opened.Credential, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool,
            opened.Descriptor.FormatVersion, observer: null, catalogue);

        var published = await orchestrator.PublishAsync(
            new SnapshotJob
            {
                Source = source,
                Roots = [new ScanRoot("/")],
                DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                SnapshotId = Enumerable.Repeat((byte)0x77, 16).ToArray(),
                NowUnixMilliseconds = 1_722_600_000_001,
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = 5,
                ClientVersion = "write-only-tests/1.0",
            },
            CancellationToken.None);
        Assert.IsEmpty(published.Failures);

        return (opened, authority, catalogue, files);
    }

    [TestMethod]
    public async Task WriteOnlyRepository_BackedUpWithTheBundleAlone_RestoresOnlyUnderTheDerivedAuthority()
    {
        var store = CreateStore();
        var (opened, authority, catalogue, files) = await CreateAndBackUpAsync(store);
        using var _ = opened;
        using var __ = authority;
        using var db = catalogue;

        // The store holds NO key object anywhere: the passphrase is the key
        // material's sole source (spec 03 §9.2).
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("keys/"), ListOptions.Default, CancellationToken.None))
        {
            Assert.Fail($"a write-only repository must have an empty /keys/ prefix; found '{entry.Key}'");
        }

        var target = RestoreTargetProfile.ForLocalPlatform();
        var snapshotId = Enumerable.Repeat((byte)0x77, 16).ToArray();
        var plan = RestorePlanner.Plan(db, snapshotId, string.Empty, target);
        Assert.IsEmpty(plan.Conflicts);

        // The WRITE BUNDLE alone: the structure loads whole — every blob's
        // record table opens on the metadata plane — and a restore run is
        // refused per item with the sealed-content reason, never a damage
        // claim (FR-WOR-003).
        using (var writeOnlyReader = new RepositoryReader(opened.RepositoryId, opened.Keys, store))
        {
            await writeOnlyReader.LoadBlobsAsync(CancellationToken.None);
            Assert.IsEmpty(writeOnlyReader.SkippedBlobs);

            var refused = await new RestoreExecutor(writeOnlyReader, target).ExecuteAsync(
                plan, Path.Combine(_root, "refused-out"),
                new RestoreExecutionOptions { RunId = "refused", NowUnixMilliseconds = 1_722_700_000_000 },
                CancellationToken.None);
            Assert.AreEqual(RestoreOutcome.Failed, refused.Outcome);
            Assert.IsTrue(
                refused.Items.Where(item => item.Path.EndsWith(".bin", StringComparison.Ordinal) || item.Path.EndsWith(".txt", StringComparison.Ordinal))
                    .All(item => item.Outcome == "failed" && item.Detail!.Contains("restore grant", StringComparison.Ordinal)),
                "every sealed file names the grant it needs");
        }

        // The passphrase re-derives the authority — a fresh open, exactly
        // the restore ceremony — and the same plan restores byte-identically
        // (FR-WOR-004).
        using var again = Right();
        var (readOpened, readAuthority) = await RepositoryLifecycle.OpenForReadAsync(
            store, again, CancellationToken.None);
        using (readOpened)
        using (readAuthority)
        using (var grantedReader = new RepositoryReader(readOpened.RepositoryId, readOpened.Keys, store, readAuthority))
        {
            await grantedReader.LoadBlobsAsync(CancellationToken.None);

            var output = Path.Combine(_root, "granted-out");
            var receipt = await new RestoreExecutor(grantedReader, target).ExecuteAsync(
                plan, output,
                new RestoreExecutionOptions
                {
                    DestinationMode = RestoreDestinationMode.InPlace,
                    RunId = "granted",
                    NowUnixMilliseconds = 1_722_700_000_000,
                },
                CancellationToken.None);

            Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
            foreach (var (path, content) in files)
            {
                SequenceAssert.AreEqual(
                    content,
                    await File.ReadAllBytesAsync(Path.Combine(output, path.Replace('/', Path.DirectorySeparatorChar))));
            }
        }
    }

    [TestMethod]
    public async Task WriteOnlyRepository_ATamperedSealedShare_SkipsOneBlobAndLoadsTheRest()
    {
        var store = CreateStore();
        var (opened, authority, catalogue, _) = await CreateAndBackUpAsync(store);
        using var _1 = opened;
        using var _2 = authority;
        using var _3 = catalogue;

        var dataDirectory = Path.Combine(_root, "repo", "blobs", "data");
        var blobFiles = Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        Assert.IsNotEmpty(blobFiles);

        // One byte flipped inside a data blob's sealed content-key share.
        var bytes = await File.ReadAllBytesAsync(blobFiles[0]);
        bytes[88 + 32 + 5] ^= 0x01;
        await File.WriteAllBytesAsync(blobFiles[0], bytes);

        // A granted load must contain the damage to that one blob — a single
        // hostile replica object cannot take reading everything else down
        // with it (ADR-0042 §7).
        using var passphrase = Right();
        var (readOpened, readAuthority) = await RepositoryLifecycle.OpenForReadAsync(
            store, passphrase, CancellationToken.None);
        using (readOpened)
        using (readAuthority)
        using (var reader = new RepositoryReader(readOpened.RepositoryId, readOpened.Keys, store, readAuthority))
        {
            await reader.LoadBlobsAsync(CancellationToken.None);

            var skipped = Assert.ContainsSingle(reader.SkippedBlobs);
            Assert.Contains("does not open", skipped.Reason, StringComparison.Ordinal);

            // The metadata blob loaded past the skip and its records still
            // read — the load finished, degraded by exactly one blob.
            Assert.IsNotEmpty(reader.AllRecords);
            var survivor = reader.AllRecords.First(
                record => record.ObjectType == Domain.ObjectType.FileVersionManifest);
            var read = await reader.ReadSegmentAsync(survivor.ObjectId, CancellationToken.None);
            Assert.AreEqual(FallbackPlan.Repository.Packing.RecordReadOutcome.Ok, read.Outcome);
        }
    }

    [TestMethod]
    public async Task WriteOnlyRepository_VerifyIsHonestAboutSealedContent_AndRefusesRepositoryDedup()
    {
        var store = CreateStore();
        var (opened, authority, catalogue, _) = await CreateAndBackUpAsync(store);
        using var _1 = opened;
        using var _2 = authority;
        using var _3 = catalogue;

        // The repository trust domain is refused at construction, with the
        // remedy named — never left to degrade silently (ADR-0042 §7).
        var refused = Assert.ThrowsExactly<ArgumentException>(() => new PublicationOrchestrator(
            SmallPolicy with { DedupTrustDomain = DedupTrustDomain.Repository },
            opened.RepositoryId, Writer, KeyGeneration.Zero, opened.Keys, opened.Credential, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(_root, "refused-sequence.txt"))),
            Path.Combine(_root, "refused-spool"),
            FormatVersions.SealedDataPlane));
        Assert.Contains("device", refused.Message, StringComparison.Ordinal);

        // The exact sealed population, from the structure plane: every
        // segment record lives in a sealed data blob, so the verify sweep's
        // sealed count must EQUAL it — not merely exceed zero, or an
        // off-by-N in the accounting would pass unseen.
        long expectedSealed;
        using (var census = new RepositoryReader(opened.RepositoryId, opened.Keys, store))
        {
            await census.LoadBlobsAsync(CancellationToken.None);
            expectedSealed = census.AllRecords.Count(
                record => record.ObjectType == Domain.ObjectType.SegmentRecord);
        }

        Assert.IsTrue(expectedSealed > 0);

        // Verification with the write bundle alone: levels 1–2 are the
        // structure plane and pass whole; level 3 counts the sealed records
        // as a stated incapacity — Ok, never a failure, never silent.
        using var verifier = new VerifyEngine(opened.RepositoryId, opened.Keys, store);
        var sawSealedData = false;
        var totalSealed = 0L;
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            foreach (var level in new[] { VerifyLevel.LocatorAndFooter, VerifyLevel.FooterAndDigest })
            {
                var structural = await verifier.VerifyBlobAsync(entry.Key, entry.Length, level, CancellationToken.None);
                Assert.IsTrue(structural.Ok, $"{entry.Key.Value} at {level}: {structural.Detail}");
            }

            var records = await verifier.VerifyBlobAsync(
                entry.Key, entry.Length, VerifyLevel.EveryRecord, CancellationToken.None);
            Assert.IsTrue(records.Ok, $"{entry.Key.Value}: sealed content must not read as damage: {records.Detail}");
            totalSealed += records.RecordsSealed;

            if (entry.Key.Value.StartsWith("blobs/data/", StringComparison.Ordinal))
            {
                sawSealedData = true;
                Assert.AreEqual(0, records.RecordsVerified, "no sealed record may claim content verification");
                Assert.IsTrue(records.RecordsSealed > 0, "a sealed data blob's records cannot content-verify");
                Assert.Contains("restore grant", records.Detail!, StringComparison.Ordinal);
            }
            else
            {
                Assert.AreEqual(0, records.RecordsSealed, "metadata blobs are the structure plane and verify whole");
                Assert.IsNull(records.Detail);
            }
        }

        Assert.IsTrue(sawSealedData, "the drill must have verified at least one sealed data blob");
        Assert.AreEqual(expectedSealed, totalSealed, "the sealed count is exact, blob by blob");

        // File verification without a grant is the same statement, distinct
        // from damage, and with the derived authority it verifies end to end.
        using (var structural = new RepositoryReader(opened.RepositoryId, opened.Keys, store))
        {
            await structural.LoadBlobsAsync(CancellationToken.None);
            var manifestEntry = structural.AllRecords.First(
                record => record.ObjectType == Domain.ObjectType.FileVersionManifest);
            var manifestRead = await structural.ReadSegmentAsync(manifestEntry.ObjectId, CancellationToken.None);
            Assert.AreEqual(FallbackPlan.Repository.Packing.RecordReadOutcome.Ok, manifestRead.Outcome);
            var manifest = FallbackPlan.Repository.Format.Manifests.FileVersionManifestCodec.Decode(manifestRead.Plaintext!);

            var unchecked_ = await verifier.VerifyFileAsync(manifest, structural, CancellationToken.None);
            Assert.IsFalse(unchecked_.Ok);
            Assert.IsTrue(unchecked_.NeedsRestoreGrant, "a sealed file is not checkable, and not damaged");
            Assert.Contains("no damage", unchecked_.Detail!, StringComparison.Ordinal);

            using var passphrase = Right();
            var (readOpened, readAuthority) = await RepositoryLifecycle.OpenForReadAsync(
                store, passphrase, CancellationToken.None);
            using (readOpened)
            using (readAuthority)
            using (var granted = new RepositoryReader(readOpened.RepositoryId, readOpened.Keys, store, readAuthority))
            {
                await granted.LoadBlobsAsync(CancellationToken.None);
                var checkedResult = await verifier.VerifyFileAsync(manifest, granted, CancellationToken.None);
                Assert.IsTrue(checkedResult.Ok, checkedResult.Detail);
            }
        }
    }

    [TestMethod]
    public async Task WriteOnlyRepository_EveryWrongOpen_IsRefusedByName()
    {
        var store = CreateStore();
        var (opened, authority, catalogue, _) = await CreateAndBackUpAsync(store);
        using var _ = opened;
        using var db = catalogue;
        authority.Dispose();

        // The wrong passphrase fails derive-and-compare — no decryption, no
        // oracle beyond equality (FR-WOR-002's verifier).
        using (var wrong = Passphrase.Create("not the passphrase at all!!"))
        {
            await Assert.ThrowsExactlyAsync<KeyUnwrapFailedException>(async () =>
                await RepositoryLifecycle.OpenForReadAsync(store, wrong, CancellationToken.None));
        }

        // A credential from another repository (a wrong passphrase's shape)
        // is refused against the descriptor before anything is read.
        using (var other = Passphrase.Create("a different repository's secret"))
        {
            var salt = Enumerable.Repeat((byte)0x11, KekDerivation.SaltLength).ToArray();
            using var foreign = WriteOnlyDerivation.Derive(
                other, SettingsFor(FormatVersions.SealedDataPlane).KdfParameters, salt, KdfValidationMode.OpenRepository);
            await Assert.ThrowsExactlyAsync<RepositoryOpenException>(async () =>
                await RepositoryLifecycle.OpenAsync(store, foreign.Credential, CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task RecoverySession_AWriteOnlyRepository_RestoresEverythingFromThePassphraseAlone()
    {
        var store = CreateStore();
        var (opened, authority, catalogue, files) = await CreateAndBackUpAsync(store);
        using var _ = opened;
        using var __ = authority;
        using var db = catalogue;

        using var passphrase = Right();

        // The archive's descriptor is the whole of "where and how to
        // derive": the salt, the parameters, and the public key as the
        // verifier. Nothing else has to be kept (ADR-0042 §8; ADR-0060).
        using (var wrong = Passphrase.Create("not the passphrase at all!!"))
        {
            var wrongPassphrase = wrong;
            await Assert.ThrowsExactlyAsync<KeyUnwrapFailedException>(
                async () => await RecoverySession.OpenAsync(wrongPassphrase, store, CancellationToken.None));
        }

        using var session = await RecoverySession.OpenAsync(passphrase, store, CancellationToken.None);
        var (blobs, notes) = await session.LoadBlobsAsync(CancellationToken.None);
        Assert.IsTrue(blobs > 0);
        Assert.IsEmpty(notes);

        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));
        Assert.IsTrue(snapshot.SignatureVerified, "the v2 signing seed derives from the bundle and must verify");

        var output = Path.Combine(_root, "recovered");
        var report = await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, CancellationToken.None);
        Assert.AreEqual(0, report.Failed);
        Assert.AreEqual(files.Count, report.Restored);
        foreach (var (path, content) in files)
        {
            SequenceAssert.AreEqual(
                content, File.ReadAllBytes(Path.Combine(output, path.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    [TestMethod]
    public async Task WriteOnlyRepository_AtFormatThree_BacksUpAndRestoresThroughTheRealPipeline()
    {
        // The whole pipeline over format 3, with nothing about the test
        // reaching below the engine: the descriptor decides, the sessions
        // stamp what it decides, and the bytes come back.
        var store = CreateStore();
        var (opened, authority, catalogue, files) =
            await CreateAndBackUpAsync(store, FormatVersions.RelocatableRecords);
        using var _ = opened;
        using var __ = authority;
        using var db = catalogue;

        Assert.AreEqual(FormatVersions.RelocatableRecords, opened.Descriptor.FormatVersion);

        // Metadata blobs are stamped 3 here, where a format-2 repository
        // stamps them 1 — the mapping FormatVersions.ContainerVersion owns,
        // read back off the disk rather than asserted on the constant.
        using (var structural = new RepositoryReader(opened.RepositoryId, opened.Keys, store))
        {
            await structural.LoadBlobsAsync(CancellationToken.None);
            Assert.IsEmpty(structural.SkippedBlobs);
            Assert.IsNotEmpty(structural.AllRecords);
        }

        var target = RestoreTargetProfile.ForLocalPlatform();
        var snapshotId = Enumerable.Repeat((byte)0x77, 16).ToArray();
        var plan = RestorePlanner.Plan(db, snapshotId, string.Empty, target);
        Assert.IsEmpty(plan.Conflicts);

        using var reader = new RepositoryReader(opened.RepositoryId, opened.Keys, store, authority);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var outputRoot = Path.Combine(_root, "v3-out");
        var restored = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, outputRoot,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "v3",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, restored.Outcome);
        foreach (var (path, content) in files)
        {
            SequenceAssert.AreEqual(
                content,
                await File.ReadAllBytesAsync(
                    Path.Combine(outputRoot, path.Replace('/', Path.DirectorySeparatorChar)), CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task WriteOnlyRepository_UpgradedBetweenPublications_SealsTheNewerFormatAndLeavesTheOlderAlone()
    {
        // The upgrade record earning its keep (11 §4.1). The descriptor still
        // says 2 and always will — no destination would accept a replacement
        // — so what the writer must consult is the EFFECTIVE version, and the
        // proof is that blobs of both stamps end up in one repository and
        // every file still comes back.
        var store = CreateStore();
        var (opened, authority, catalogue, files) = await CreateAndBackUpAsync(store);
        using var db = catalogue;

        Assert.AreEqual(FormatVersions.SealedDataPlane, opened.EffectiveFormatVersion);
        var beforeUpgrade = await DataBlobVersionsAsync(store);
        Assert.IsNotEmpty(beforeUpgrade);
        Assert.IsTrue(
            beforeUpgrade.All(version => version == FormatVersions.SealedDataPlane),
            "the first publication wrote something other than format 2");

        await RepositoryLifecycle.WriteFormatUpgradeAsync(
            store, opened.Descriptor, opened.Credential, FormatVersions.RelocatableRecords,
            Writer.ToArray(), upgradedAtUnixMilliseconds: 1_722_650_000_000, CancellationToken.None);
        opened.Dispose();
        authority.Dispose();

        // Re-opened, because that is when the record is read: the descriptor
        // is untouched and the effective version has moved.
        using var passphrase = Right();
        var (reopened, reauthority) = await RepositoryLifecycle.OpenForReadAsync(
            store, passphrase, CancellationToken.None);
        using var _1 = reopened;
        using var _2 = reauthority;

        Assert.AreEqual(FormatVersions.SealedDataPlane, reopened.Descriptor.FormatVersion);
        Assert.AreEqual(FormatVersions.RelocatableRecords, reopened.EffectiveFormatVersion);

        var second = new byte[70_000];
        new Random(52).NextBytes(second);
        files["after-the-upgrade.bin"] = second;
        var source = new FakeFileSystemSource();
        foreach (var (path, content) in files)
        {
            source.AddFile(path, content);
        }

        var spool = Path.Combine(_root, "spool");
        var snapshotId = Enumerable.Repeat((byte)0x78, 16).ToArray();
        var orchestrator = new PublicationOrchestrator(
            SmallPolicy, reopened.RepositoryId, Writer, KeyGeneration.Zero, reopened.Keys, reopened.Credential,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool,
            reopened.EffectiveFormatVersion, observer: null, db);

        var published = await orchestrator.PublishAsync(
            new SnapshotJob
            {
                Source = source,
                Roots = [new ScanRoot("/")],
                DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                SnapshotId = snapshotId,
                NowUnixMilliseconds = 1_722_700_000_001,
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = 5,
                ClientVersion = "write-only-tests/1.0",
            },
            CancellationToken.None);
        Assert.IsEmpty(published.Failures);

        // Both stamps, in one repository. The format-2 blobs are exactly the
        // ones that were there before: an upgrade rewrites nothing.
        var afterUpgrade = await DataBlobVersionsAsync(store);
        Assert.HasCount(
            beforeUpgrade.Count,
            afterUpgrade.Where(version => version == FormatVersions.SealedDataPlane).ToList());
        Assert.IsNotEmpty(afterUpgrade.Where(version => version == FormatVersions.RelocatableRecords).ToList());

        // The delta the upgraded publication wrote carries the Merkle
        // commitment; the one from before it does not. Nothing had to reason
        // about which blobs are old — a delta covers only what its own
        // publication wrote, so the parallel-or-absent rule holds for free.
        using var loader = new FallbackPlan.Repository.Index.IndexLoader(
            store, reopened.RepositoryId, reopened.Credential);
        var state = await loader.LoadAsync(
            currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null,
            blobState: null, CancellationToken.None);
        Assert.IsEmpty(state.Findings);

        var deltas = state.Deltas.Select(entry => entry.Delta).OrderBy(delta => delta.Sequence).ToList();
        Assert.HasCount(2, deltas);
        Assert.IsEmpty(deltas[0].CoveredBlobMerkleRoots);
        Assert.IsNotEmpty(deltas[1].CoveredBlobIds);
        Assert.HasCount(deltas[1].CoveredBlobIds.Count, deltas[1].CoveredBlobMerkleRoots);

        // And the whole of it restores: the newest snapshot spans records
        // sealed under two different constructions, and the reader dispatches
        // on the envelope it finds rather than on anything it was told.
        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(db, snapshotId, string.Empty, target);
        Assert.IsEmpty(plan.Conflicts);

        using var reader = new RepositoryReader(reopened.RepositoryId, reopened.Keys, store, reauthority);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var outputRoot = Path.Combine(_root, "mixed-out");
        var restored = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, outputRoot,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "mixed",
                NowUnixMilliseconds = 1_722_700_000_002,
            },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, restored.Outcome);
        foreach (var (path, content) in files)
        {
            SequenceAssert.AreEqual(
                content,
                await File.ReadAllBytesAsync(
                    Path.Combine(outputRoot, path.Replace('/', Path.DirectorySeparatorChar)), CancellationToken.None));
        }
    }

    /// <summary>The stamped container version of every data blob in the store, read off the disk.</summary>
    private static async Task<List<ushort>> DataBlobVersionsAsync(LocalFileSystemObjectStore store)
    {
        var versions = new List<ushort>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("blobs/data/"), ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(
                entry.Key, new ObjectRange(0, BlobEnvelope.MaxLength), CancellationToken.None);
            Assert.AreEqual(OpenReadOutcome.Found, read.Outcome);

            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory, CancellationToken.None);
            versions.Add(BlobEnvelope.Parse(memory.ToArray()).FormatVersion);
        }

        return versions;
    }

    [TestMethod]
    public async Task WriteOnlyRepository_TheIndexDelta_CarriesMerkleRootsOnlyAtFormatThree()
    {
        // The publication gate, at the engine rather than at a frozen
        // fixture (07 §2.3). A reader that predates key 11 refuses a delta
        // carrying it outright, so the repository's declared format version
        // — not a policy, not a flag — is what decides, and a format-2
        // archive an older build may read never contains one.
        foreach (var version in new[] { FormatVersions.SealedDataPlane, FormatVersions.RelocatableRecords })
        {
            var store = new LocalFileSystemObjectStore(
                Path.Combine(_root, $"roots-{version}"));
            var (opened, authority, catalogue, _) = await CreateAndBackUpAsync(store, version);
            using var _1 = opened;
            using var _2 = authority;
            using var _3 = catalogue;

            using var loader = new FallbackPlan.Repository.Index.IndexLoader(store, opened.RepositoryId, opened.Credential);
            var state = await loader.LoadAsync(
                currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null,
                blobState: null, CancellationToken.None);
            Assert.IsEmpty(state.Findings);

            var delta = Assert.ContainsSingle(state.Deltas).Delta;
            Assert.IsNotEmpty(delta.CoveredBlobIds);
            Assert.HasCount(delta.CoveredBlobIds.Count, delta.CoveredBlobDigests);

            if (version == FormatVersions.SealedDataPlane)
            {
                Assert.IsEmpty(delta.CoveredBlobMerkleRoots);
                continue;
            }

            Assert.HasCount(delta.CoveredBlobIds.Count, delta.CoveredBlobMerkleRoots);

            // And each root is the tree over that blob's own bytes, read
            // back from the store rather than taken from the publisher.
            using var storeKeys = new StoreBlobKeyDeriver(opened.Credential.KeyIdKey.ToArray());
            for (var i = 0; i < delta.CoveredBlobIds.Count; i++)
            {
                var derived = storeKeys.Derive(delta.CoveredBlobIds[i]);
                var key = BlobStoreKeys.ForBlob(BlobClass.Data, derived);
                var metadata = await store.GetMetadataAsync(key, CancellationToken.None);
                if (metadata.Metadata is null)
                {
                    key = BlobStoreKeys.ForBlob(BlobClass.Metadata, derived);
                    metadata = await store.GetMetadataAsync(key, CancellationToken.None);
                }

                Assert.IsNotNull(metadata.Metadata);
                var bytes = new byte[metadata.Metadata.Length];
                using (var content = await store.OpenReadAsync(key, null, CancellationToken.None))
                {
                    Assert.AreEqual(OpenReadOutcome.Found, content.Outcome);
                    await content.Content!.ReadExactlyAsync(bytes, CancellationToken.None);
                }

                SequenceAssert.AreEqual(
                    BlobMerkle.Root(bytes.AsSpan(0, bytes.Length - FooterLocator.Length)),
                    delta.CoveredBlobMerkleRoots[i].ToArray());
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
