using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Filesystem;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The passphrase drill (FR-DRL-001; ADR-0060): one passphrase, and
/// nothing else kept anywhere, restores from an archive it created — and
/// from a <em>second</em> archive of the same installation — because every
/// archive's own descriptor carries the salt, the parameters and the
/// verifier the derivation needs.
/// </summary>
[TestClass]
public sealed class PassphraseDrillTests : IDisposable
{
    private const string PassphraseText = "the one long passphrase of this installation";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-passphrase-drill", Guid.NewGuid().ToString("n"));

    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);

    public PassphraseDrillTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
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

        using var repository = await RepositoryLifecycle.CreateAsync(
            store, authority.Credential, _salt, RepositoryCreationSettings.Default.KdfParameters,
            createdBy: "passphrase-drill", 1_722_600_000_000, CancellationToken.None);

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
            CapturePolicy.Default with { SegmentSize = SegmentSize.Create(64 * 1024) },
            repository.RepositoryId,
            Domain.Identifiers.WriterId.FromBytes(Enumerable.Repeat((byte)0xA0, 16).ToArray()),
            repository.CurrentDataGeneration,
            repository.Keys,
            repository.Credential,
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
                ClientVersion = "passphrase-drill/1.0",
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
    public async Task ThePassphraseAlone_RestoresAnArchive_WithNothingKeptAnywhereElse()
    {
        // Nothing was written down but the passphrase. Everything the
        // restore needs beyond it comes from the archive's own descriptor.
        var (store, files) = await BackUpArchiveAsync("documents", seed: 21);

        using var passphrase = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(passphrase, store, CancellationToken.None);

        Assert.AreEqual(FormatLimits.FormatVersion, session.FormatVersion);
        await RestoreAndCompareAsync(session, Path.Combine(_root, "restored-documents"), files);
    }

    [TestMethod]
    public async Task ThePassphrase_OpensASecondArchiveOfTheSameInstallationToo()
    {
        // The claim that makes one passphrase per installation worth
        // having. Two archives, two repository identities, one passphrase.
        var (documents, documentFiles) = await BackUpArchiveAsync("documents", seed: 21);
        var (photos, photoFiles) = await BackUpArchiveAsync("photos", seed: 77);

        using var passphrase = Passphrase.Create(PassphraseText);

        using (var first = await RecoverySession.OpenAsync(passphrase, documents, CancellationToken.None))
        using (var second = await RecoverySession.OpenAsync(passphrase, photos, CancellationToken.None))
        {
            Assert.AreNotEqual(
                Convert.ToHexString(first.RepositoryId.ToArray()),
                Convert.ToHexString(second.RepositoryId.ToArray()),
                "two archives are two repositories; the passphrase is what they share");

            await RestoreAndCompareAsync(first, Path.Combine(_root, "r1"), documentFiles);
            await RestoreAndCompareAsync(second, Path.Combine(_root, "r2"), photoFiles);
        }
    }

    [TestMethod]
    public async Task TheWrongPassphrase_IsRefusedAsAWrongPassphrase()
    {
        var (store, _) = await BackUpArchiveAsync("documents", seed: 21);

        using var wrong = Passphrase.Create("an entirely different installation's passphrase");

        await Assert.ThrowsExactlyAsync<KeyUnwrapFailedException>(
            async () => await RecoverySession.OpenAsync(wrong, store, CancellationToken.None));
    }

    [TestMethod]
    public async Task AnotherInstallationsArchive_IsRefusedExactlyAsAWrongPassphraseIs()
    {
        // With no kit there is no second copy of the verifier to check the
        // passphrase against first, so "another installation's archive" and
        // "wrong passphrase" are the same refusal — deliberately
        // indistinguishable, since the descriptor's sealing public key is the
        // only verifier and equality is the whole check (specification 03 §4).
        var strangerSalt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var strangerStore = new LocalFileSystemObjectStore(Path.Combine(_root, "stranger"));
        using (var strangerPassphrase = Passphrase.Create("some other machine's long passphrase"))
        using (var strangerAuthority = WriteOnlyDerivation.Derive(
            strangerPassphrase, RepositoryCreationSettings.Default.KdfParameters, strangerSalt,
            KdfValidationMode.CreateRepository))
        {
            (await RepositoryLifecycle.CreateAsync(
                strangerStore, strangerAuthority.Credential, strangerSalt,
                RepositoryCreationSettings.Default.KdfParameters, "stranger",
                1_722_600_000_000, CancellationToken.None)).Dispose();
        }

        using var passphrase = Passphrase.Create(PassphraseText);
        var (own, _) = await BackUpArchiveAsync("documents", seed: 21);
        using var wrong = Passphrase.Create("not this installation's passphrase either");

        var stranger = await Assert.ThrowsExactlyAsync<KeyUnwrapFailedException>(
            async () => await RecoverySession.OpenAsync(passphrase, strangerStore, CancellationToken.None));
        var mistyped = await Assert.ThrowsExactlyAsync<KeyUnwrapFailedException>(
            async () => await RecoverySession.OpenAsync(wrong, own, CancellationToken.None));

        Assert.AreEqual(mistyped.Message, stranger.Message);
    }

    [TestMethod]
    public async Task AFolderThatIsNotAnArchive_SaysSoRatherThanFailingOnKeys()
    {
        var empty = Path.Combine(_root, "not-an-archive");
        Directory.CreateDirectory(empty);

        using var passphrase = Passphrase.Create(PassphraseText);
        var failure = await Assert.ThrowsExactlyAsync<RecoveryFailureException>(
            async () => await RecoverySession.OpenAsync(
                passphrase, new LocalFileSystemObjectStore(empty), CancellationToken.None));

        Assert.Contains("not a FallbackPlan archive", failure.Message, StringComparison.Ordinal);
    }
}
