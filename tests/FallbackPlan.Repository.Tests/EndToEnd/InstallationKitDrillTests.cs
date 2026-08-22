using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Filesystem;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.RecoveryKit;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The installation-kit drill (FR-KIT-004, FR-KIT-006; recovery-kit §2.2,
/// §6): one kit, generated from the passphrase alone before any archive
/// existed, restores from an archive it never saw — and from a
/// <em>second</em> archive it never saw either, which is the whole claim of
/// one kit per installation.
/// </summary>
[TestClass]
public sealed class InstallationKitDrillTests : IDisposable
{
    private const string PassphraseText = "the one long passphrase of this installation";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-installation-kit", Guid.NewGuid().ToString("n"));

    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);

    public InstallationKitDrillTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// The kit setup would produce: derived from the passphrase and the
    /// installation's salt, with no archive in existence.
    /// </summary>
    private RecoveryKit InstallationKit()
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, RepositoryCreationSettings.Default.KdfParameters, _salt,
            KdfValidationMode.CreateRepository);

        return RecoveryKitFactory.BuildForInstallation(
            authority.Credential, _salt, RepositoryCreationSettings.Default.KdfParameters,
            Enumerable.Repeat((byte)0x22, 16).ToArray(), issuedAt: 1_722_600_000_000);
    }

    /// <summary>Creates one archive from the installation's root and backs a tree up into it.</summary>
    private async Task<(LocalFileSystemObjectStore Store, Dictionary<string, byte[]> Files)>
        BackUpArchiveAsync(string name, int seed)
    {
        var store = new LocalFileSystemObjectStore(Path.Combine(_root, name));

        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, RepositoryCreationSettings.Default.KdfParameters, _salt,
            KdfValidationMode.CreateRepository);

        using var repository = await RepositoryLifecycle.CreateWriteOnlyFromCredentialAsync(
            store, authority.Credential, _salt, RepositoryCreationSettings.Default.KdfParameters,
            createdBy: "installation-kit-drill", 1_722_600_000_000, CancellationToken.None);

        var files = new Dictionary<string, byte[]>
        {
            [$"{name}/report.bin"] = new byte[120_000],
            [$"{name}/notes.txt"] = new byte[640],
        };

        var random = new Random(seed);
        var source = new FakeFileSystemSource();
        foreach (var (path, content) in files)
        {
            random.NextBytes(content);
            source.AddFile(path, content);
        }

        var spool = Path.Combine(_root, "spool", name);
        Directory.CreateDirectory(spool);

        var orchestrator = new PublicationOrchestrator(
            CapturePolicy.Default with
            {
                SegmentSize = SegmentSize.Create(64 * 1024),

                // A write-only repository cannot read another writer's
                // segments to verify reuse, so the device domain is its
                // default and its only unacknowledged choice (ADR-0042).
                DedupTrustDomain = DedupTrustDomain.Device,
            },
            repository.RepositoryId,
            Domain.Identifiers.WriterId.FromBytes(Enumerable.Repeat((byte)0xA0, 16).ToArray()),
            repository.CurrentDataGeneration,
            repository.Keys,
            repository.Hierarchy,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool);

        await orchestrator.PublishAsync(
            new SnapshotJob
            {
                Source = source,
                Roots = [new ScanRoot("/")],
                DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                BackupSetId = Enumerable.Repeat((byte)seed, 16).ToArray(),
                SnapshotId = Enumerable.Repeat((byte)(seed + 1), 16).ToArray(),
                NowUnixMilliseconds = 1_722_600_000_001,
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = 5,
                ClientVersion = "installation-kit-drill/1.0",
            },
            CancellationToken.None);

        return (store, files);
    }

    private static async Task RestoreAndCompareAsync(
        RecoverySession session, string output, Dictionary<string, byte[]> files)
    {
        // The blob index has to be loaded before a tree restore can resolve
        // a record to the blob holding it — the recovery tool has no
        // catalogue to consult.
        var (blobs, notes) = await session.LoadBlobsAsync(CancellationToken.None);
        Assert.IsTrue(blobs > 0);
        Assert.IsEmpty(notes);

        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));
        Assert.IsTrue(snapshot.SignatureVerified);

        var report = await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, CancellationToken.None);

        Assert.AreEqual(0, report.Failed);
        Assert.AreEqual(files.Count, report.Restored);
        foreach (var (path, content) in files)
        {
            SequenceAssert.AreEqual(content, File.ReadAllBytes(
                Path.Combine(output, path.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    [TestMethod]
    public async Task InstallationKit_AndItsPassphraseAlone_RestoreAnArchiveTheKitNeverSaw()
    {
        // The kit is minted before the archive exists and never told about
        // it afterwards. Everything the restore needs beyond the passphrase
        // comes from the archive's own descriptor.
        var kit = InstallationKit();
        var (store, files) = await BackUpArchiveAsync("documents", seed: 21);

        // Through the printed form, as a person would hold it.
        var reparsed = RecoveryKitCodec.Parse(
            RecoveryKitText.ParseToFramed(RecoveryKitText.Render(RecoveryKitCodec.Serialize(kit))));

        using var passphrase = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(
            reparsed, passphrase, store, CancellationToken.None);

        await RestoreAndCompareAsync(session, Path.Combine(_root, "restored-documents"), files);
    }

    [TestMethod]
    public async Task InstallationKit_TheSameKit_OpensASecondArchiveToo()
    {
        // The claim that makes one kit per installation worth having. Two
        // archives, two repository identities, one passphrase, one kit.
        var kit = InstallationKit();
        var (documents, documentFiles) = await BackUpArchiveAsync("documents", seed: 21);
        var (photos, photoFiles) = await BackUpArchiveAsync("photos", seed: 77);

        using var passphrase = Passphrase.Create(PassphraseText);

        using (var first = await RecoverySession.OpenAsync(kit, passphrase, documents, CancellationToken.None))
        using (var second = await RecoverySession.OpenAsync(kit, passphrase, photos, CancellationToken.None))
        {
            Assert.AreNotEqual(
                Convert.ToHexString(first.RepositoryId.ToArray()),
                Convert.ToHexString(second.RepositoryId.ToArray()),
                "two archives are two repositories; the kit is what they share");

            await RestoreAndCompareAsync(first, Path.Combine(_root, "r1"), documentFiles);
            await RestoreAndCompareAsync(second, Path.Combine(_root, "r2"), photoFiles);
        }
    }

    [TestMethod]
    public async Task InstallationKit_TheWrongPassphrase_IsRefusedAsAWrongPassphrase()
    {
        var kit = InstallationKit();
        var (store, _) = await BackUpArchiveAsync("documents", seed: 21);

        using var wrong = Passphrase.Create("an entirely different installation's passphrase");

        await Assert.ThrowsExactlyAsync<KeyUnwrapFailedException>(
            async () => await RecoverySession.OpenAsync(kit, wrong, store, CancellationToken.None));
    }

    [TestMethod]
    public async Task InstallationKit_AnArchiveFromAnotherInstallation_FailsDifferentlyFromAWrongPassphrase()
    {
        // The two failures are different questions. Collapsing them would
        // leave somebody retyping a passphrase that was right all along.
        var kit = InstallationKit();

        var strangerSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var strangerStore = new LocalFileSystemObjectStore(Path.Combine(_root, "stranger"));
        using (var strangerPassphrase = Passphrase.Create("some other machine's long passphrase"))
        using (var strangerAuthority = WriteOnlyDerivation.Derive(
            strangerPassphrase, RepositoryCreationSettings.Default.KdfParameters, strangerSalt,
            KdfValidationMode.CreateRepository))
        {
            (await RepositoryLifecycle.CreateWriteOnlyFromCredentialAsync(
                strangerStore, strangerAuthority.Credential, strangerSalt,
                RepositoryCreationSettings.Default.KdfParameters, "stranger",
                1_722_600_000_000, CancellationToken.None)).Dispose();
        }

        using var passphrase = Passphrase.Create(PassphraseText);
        var failure = await Assert.ThrowsExactlyAsync<RecoveryKitFormatException>(
            async () => await RecoverySession.OpenAsync(
                kit, passphrase, strangerStore, CancellationToken.None));

        Assert.Contains("different FallbackPlan installation", failure.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task InstallationKit_AFolderThatIsNotAnArchive_SaysSoRatherThanFailingOnKeys()
    {
        var kit = InstallationKit();
        var empty = Path.Combine(_root, "not-an-archive");
        Directory.CreateDirectory(empty);

        using var passphrase = Passphrase.Create(PassphraseText);
        var failure = await Assert.ThrowsExactlyAsync<RecoveryKitFormatException>(
            async () => await RecoverySession.OpenAsync(
                kit, passphrase, new LocalFileSystemObjectStore(empty), CancellationToken.None));

        Assert.Contains("not a FallbackPlan archive", failure.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task InstallationKit_TheSynchronousOpen_RefusesItByName()
    {
        // Open cannot read a descriptor, and the repository id is AAD for
        // every record — so it refuses rather than inventing one.
        var kit = InstallationKit();
        var (store, _) = await BackUpArchiveAsync("documents", seed: 21);

        using var passphrase = Passphrase.Create(PassphraseText);
        var failure = Assert.ThrowsExactly<RecoveryKitFormatException>(
            () => RecoverySession.Open(kit, passphrase, store));

        Assert.Contains("asynchronous overload", failure.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RepositoryKit_ThroughOpenAsync_StillWorksUnchanged()
    {
        // OpenAsync serves both formats so one call site can hold both.
        var store = new LocalFileSystemObjectStore(Path.Combine(_root, "v1"));
        using var passphrase = Passphrase.Create(PassphraseText);
        using (var repository = await RepositoryLifecycle.CreateAsync(
            store, passphrase, RepositoryCreationSettings.Default, 1_722_600_000_000, CancellationToken.None))
        {
            Assert.IsNotNull(repository);
        }

        var kit = await RecoveryKitFactory.BuildAsync(
            store, passphrase, Enumerable.Repeat((byte)0x22, 16).ToArray(),
            issuedAt: 1_722_600_000_002, destinations: [], CancellationToken.None);

        using var session = await RecoverySession.OpenAsync(kit, passphrase, store, CancellationToken.None);

        SequenceAssert.AreEqual(kit.RepositoryId!.Value.ToArray(), session.RepositoryId.ToArray());
    }
}
