using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.RecoveryKit;
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
/// re-derived authority — and recovered on a clean machine from a kit that
/// carries no key material at all.
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

    private static RepositoryCreationSettings Settings => RepositoryCreationSettings.Default with
    {
        CreatedBy = "write-only-tests/1.0",
    };

    private static Passphrase Right() => Passphrase.Create(PassphraseText);

    /// <summary>64 KiB segments so a modest file spans several sealed records.</summary>
    private static CapturePolicy SmallPolicy => CapturePolicy.Default with
    {
        SegmentSize = SegmentSize.Create(64 * 1024),
        BlobWriteProfile = BlobWriteProfile.LocalDefault with
        {
            TargetSizeBytes = 256 * 1024,
            MaximumSizeBytes = 512 * 1024,
        },
    };

    private async Task<(OpenedRepository Opened, RepositoryReadAuthority Authority, CatalogueDb Catalogue, Dictionary<string, byte[]> Files)>
        CreateAndBackUpAsync(LocalFileSystemObjectStore store)
    {
        using var passphrase = Right();
        var (opened, authority) = await RepositoryLifecycle.CreateWriteOnlyAsync(
            store, passphrase, Settings, createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);

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
            SmallPolicy, opened.RepositoryId, Writer, KeyGeneration.Zero, opened.Keys, opened.Hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool, observer: null, catalogue);

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
        var (readOpened, readAuthority) = await RepositoryLifecycle.OpenWriteOnlyForReadAsync(
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
                await RepositoryLifecycle.OpenWriteOnlyForReadAsync(store, wrong, CancellationToken.None));
        }

        // A credential from another repository (a wrong passphrase's shape)
        // is refused against the descriptor before anything is read.
        using (var other = Passphrase.Create("a different repository's secret"))
        {
            var salt = Enumerable.Repeat((byte)0x11, KekDerivation.SaltLength).ToArray();
            using var foreign = WriteOnlyDerivation.Derive(
                other, Settings.KdfParameters, salt, KdfValidationMode.OpenRepository);
            await Assert.ThrowsExactlyAsync<RepositoryOpenException>(async () =>
                await RepositoryLifecycle.OpenWriteOnlyAsync(store, foreign.Credential, CancellationToken.None));
        }

        // The v1 open paths name what this is instead of failing confusingly.
        using (var passphrase = Right())
        {
            var openRefusal = await Assert.ThrowsExactlyAsync<RepositoryOpenException>(async () =>
                await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None));
            Assert.Contains("write-only", openRefusal.Message, StringComparison.Ordinal);

            var exportRefusal = await Assert.ThrowsExactlyAsync<RepositoryOpenException>(async () =>
                await RepositoryLifecycle.ExportVerifiedKeyObjectAsync(store, passphrase, CancellationToken.None));
            Assert.Contains("write-only", exportRefusal.Message, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task RecoveryKit_AWriteOnlyRepository_CarriesNoKeyMaterialAndStillRestoresEverything()
    {
        var store = CreateStore();
        var (opened, authority, catalogue, files) = await CreateAndBackUpAsync(store);
        using var _ = opened;
        using var __ = authority;
        using var db = catalogue;

        using var passphrase = Right();
        var kit = await RecoveryKitFactory.BuildAsync(
            store, passphrase, Enumerable.Repeat((byte)0x22, 16).ToArray(),
            issuedAt: 1_722_600_000_002, destinations: [], CancellationToken.None);

        // The kit is pure "where and how to derive": no key object, the
        // public key as the verifier — and it survives its own text form,
        // which is what a printed page holds (ADR-0042 §8).
        Assert.IsTrue(kit.KeyObject.IsEmpty);
        Assert.AreEqual(32, kit.SealingPublicKey.Length);
        var reparsed = RecoveryKitCodec.Parse(
            RecoveryKitText.ParseToFramed(RecoveryKitText.Render(RecoveryKitCodec.Serialize(kit))));

        using (var wrong = Passphrase.Create("not the passphrase at all!!"))
        {
            var wrongPassphrase = wrong;
            Assert.ThrowsExactly<KeyUnwrapFailedException>(() => RecoverySession.Open(reparsed, wrongPassphrase, store));
        }

        using var session = RecoverySession.Open(reparsed, passphrase, store);
        var (blobs, notes) = await session.LoadBlobsAsync(CancellationToken.None);
        Assert.IsTrue(blobs > 0);
        Assert.IsEmpty(notes);

        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));
        Assert.IsTrue(snapshot.SignatureVerified, "the v2 signing seed derives from the bundle and must verify");

        var output = Path.Combine(_root, "kit-out");
        var report = await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, CancellationToken.None);
        Assert.AreEqual(0, report.Failed);
        Assert.AreEqual(files.Count, report.Restored);
        foreach (var (path, content) in files)
        {
            SequenceAssert.AreEqual(
                content, File.ReadAllBytes(Path.Combine(output, path.Replace('/', Path.DirectorySeparatorChar))));
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
