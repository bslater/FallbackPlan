using FallbackPlan.Domain;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The recovery session's containment (FR-RST-005's trust posture applied
/// to the last-line tool): the full client's executor refuses manifest
/// paths that are not plain components, but <see cref="RecoverySession"/>
/// builds destinations from tree-entry names directly — and the coverage
/// survey found no test had ever handed it a hostile one. A repository is
/// exactly as adversarial for the passphrase drill as for the daily restore; a
/// tree naming <c>..</c> must fail that entry with a stated refusal, not
/// write beside the chosen output. Nor may a link the output already holds
/// carry a write out of it, at a folder or at a file's spool, by the rule
/// the executor holds to (ADR-0152). An output that is itself a link is
/// where the person chose, and takes everything below it (ADR-0159). A
/// file's spool is named by that rule too, so a name as long as a target
/// holds is restored (ADR-0173).
/// </summary>
[TestClass]
public sealed class RecoveryContainmentTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-recovery-containment", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "containment-drill-passphrase!!";

    public RecoveryContainmentTests() => Directory.CreateDirectory(_root);

    [TestMethod]
    public async Task RecoveryRestore_ATreeNamingDotDot_RefusesTheEntryAndNothingLandsOutside()
    {
        var store = new LocalFileSystemObjectStore(Path.Combine(_root, "repo"));

        using var passphrase = Passphrase.Create(PassphraseText);
        var (repository, authority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
            store, passphrase, Domain.Configuration.RepositoryCreationSettings.Default,
            createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);
        using var _repository = repository;
        using var _authority = authority;

        // The fake source will happily scan a tree whose first component is
        // '..' — the shape a tampered repository would publish on purpose.
        var good = new byte[4_096];
        var evil = new byte[4_096];
        new Random(31).NextBytes(good);
        new Random(37).NextBytes(evil);
        var source = new FakeFileSystemSource();
        source.AddFile("good.txt", good);
        source.AddFile("../escaped.bin", evil);

        var spool = Path.Combine(_root, "spool");
        Directory.CreateDirectory(spool);
        var orchestrator = new PublicationOrchestrator(
            Domain.Configuration.CapturePolicy.Default with { SegmentSize = SegmentSize.Create(64 * 1024) },
            repository.RepositoryId,
            Domain.Identifiers.WriterId.FromBytes(Enumerable.Repeat((byte)0xA0, 16).ToArray()),
            repository.CurrentDataGeneration,
            repository.Keys,
            repository.Credential,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool,
            FormatVersions.SealedDataPlane);

        await orchestrator.PublishAsync(
            new SnapshotJob
            {
                Source = source,
                Roots = [new ScanRoot("/")],
                DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                SnapshotId = Enumerable.Repeat((byte)0x66, 16).ToArray(),
                NowUnixMilliseconds = 1_722_600_000_001,
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = 5,
                ClientVersion = "containment-drill/1.0",
            },
            CancellationToken.None);

        using var openPassphrase = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(openPassphrase, store, CancellationToken.None);
        await session.LoadBlobsAsync(CancellationToken.None);
        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));

        // Restore into a NESTED output whose parent this test owns, so an
        // escape through '..' would land at a path we can prove empty.
        var parent = Path.Combine(_root, "nested");
        var output = Path.Combine(parent, "restored");
        var report = await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, CancellationToken.None);

        // The hostile entry fails by name; the honest file still restores.
        Assert.IsTrue(report.Failed >= 1, "the '..' entry must be counted as failed");
        Assert.Contains(note => note.Kind == RecoveryNoteKind.Refused
            && note.Path is { } path && path.Contains("..", StringComparison.Ordinal)
            && note.ToString().Contains("refused", StringComparison.Ordinal), report.Notes);
        SequenceAssert.AreEqual(good, File.ReadAllBytes(Path.Combine(output, "good.txt")));

        // Nothing landed outside the chosen output — not the file, and not
        // a spool for it either.
        Assert.IsFalse(File.Exists(Path.Combine(parent, "escaped.bin")), "the restore escaped the output directory");
        SequenceAssert.AreEqual(
            new[] { Path.GetFileName(output) },
            [.. Directory.EnumerateFileSystemEntries(parent).Select(Path.GetFileName).Order()]);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task RecoveryRestore_AFolderInTheOutputThatIsALinkLeavingIt_IsWrittenThroughByNothing()
    {
        // A tree's names can all be plain and still lead out: a folder the
        // output already holds can be a link to somewhere else. The executor
        // refuses a path that leaves its root through a link, and so must the
        // tool, by the same rule (ADR-0152).
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", [1, 2, 3]);
        source.AddFile("good.txt", [4, 5, 6]);
        var store = await PublishAsync(source);

        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(_root, "linked-out")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(output, "docs"), outside);

        using var passphrase = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(passphrase, store, CancellationToken.None);
        await session.LoadBlobsAsync(CancellationToken.None);
        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));
        var report = await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, CancellationToken.None);

        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(outside), "the restore wrote through a link out of the output");
        Assert.Contains(note => note.Kind == RecoveryNoteKind.Refused && note.Path == "docs/a.bin"
            && note.Reason is { } reason && reason.Contains("symlink", StringComparison.Ordinal), report.Notes);
        Assert.AreEqual(1, report.Failed);
        SequenceAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Path.Combine(output, "good.txt")));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task RecoveryRestore_ALinkStandingAtASpoolsName_IsNotWrittenThrough()
    {
        // The tool spools each file beside its destination, under a name of
        // its own. A link already standing at that name must be replaced,
        // never opened.
        var source = new FakeFileSystemSource();
        source.AddFile("good.txt", [4, 5, 6]);
        var store = await PublishAsync(source);

        var outside = Directory.CreateDirectory(Path.Combine(_root, "spool-outside")).FullName;
        var target = Path.Combine(outside, "target.txt");
        File.WriteAllBytes(target, [9, 9, 9]);
        var output = Directory.CreateDirectory(Path.Combine(_root, "spool-out")).FullName;
        File.CreateSymbolicLink(RestorePaths.SpoolBeside(Path.Combine(output, "good.txt"), "recover"), target);

        using var passphrase = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(passphrase, store, CancellationToken.None);
        await session.LoadBlobsAsync(CancellationToken.None);
        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));
        var report = await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, CancellationToken.None);

        CollectionAssert.AreEqual(new byte[] { 9, 9, 9 }, File.ReadAllBytes(target), "the spool was written through a link");
        SequenceAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Path.Combine(output, "good.txt")));
        Assert.AreEqual(0, report.Failed);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task RecoveryRestore_IntoAnOutputThatIsItselfALink_RestoresWhatLiesBelowItsTopLevel()
    {
        // The output is the person's choice, wherever its link leads; only a
        // link inside it that leads out of where it really is is refused
        // (ADR-0159).
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", [1, 2, 3]);
        source.AddFile("good.txt", [4, 5, 6]);
        var store = await PublishAsync(source);

        var real = Directory.CreateDirectory(Path.Combine(_root, "other-disk")).FullName;
        var output = Path.Combine(_root, "linked-output");
        Directory.CreateSymbolicLink(output, real);

        using var passphrase = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(passphrase, store, CancellationToken.None);
        await session.LoadBlobsAsync(CancellationToken.None);
        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));
        var report = await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, CancellationToken.None);

        Assert.AreEqual(0, report.Failed, string.Join("; ", report.Notes.Select(note => note.ToString())));
        SequenceAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(real, "docs", "a.bin")));
    }

    [TestMethod]
    public async Task RecoveryRestore_AFileWhoseNameIsAsLongAsATargetHolds_IsRestored()
    {
        // 255 ASCII characters: the longest name the common Linux
        // filesystems and NTFS both hold. Its spool needs no longer a name
        // (ADR-0173).
        var name = new string('n', 251) + ".bin";
        var source = new FakeFileSystemSource();
        source.AddFile(name, [7, 8, 9]);
        var store = await PublishAsync(source);
        var output = Directory.CreateDirectory(Path.Combine(_root, "long-out")).FullName;

        using var passphrase = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(passphrase, store, CancellationToken.None);
        await session.LoadBlobsAsync(CancellationToken.None);
        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));
        var report = await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, CancellationToken.None);

        Assert.AreEqual(0, report.Failed, string.Join("; ", report.Notes.Select(note => note.ToString())));
        SequenceAssert.AreEqual(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Path.Combine(output, name)));
        Assert.ContainsSingle(Directory.GetFiles(output), "no spool is left beside it");
    }

    /// <summary>Publishes <paramref name="source"/> into a repository made from the passphrase.</summary>
    private async Task<LocalFileSystemObjectStore> PublishAsync(FakeFileSystemSource source)
    {
        var store = new LocalFileSystemObjectStore(Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("n")));
        using var passphrase = Passphrase.Create(PassphraseText);
        var (repository, authority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
            store, passphrase, Domain.Configuration.RepositoryCreationSettings.Default,
            createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);
        using var _repository = repository;
        using var _authority = authority;

        var spool = Directory.CreateDirectory(Path.Combine(_root, "spool-" + Guid.NewGuid().ToString("n"))).FullName;
        await new PublicationOrchestrator(
                Domain.Configuration.CapturePolicy.Default with { SegmentSize = SegmentSize.Create(64 * 1024) },
                repository.RepositoryId,
                Domain.Identifiers.WriterId.FromBytes(Enumerable.Repeat((byte)0xA0, 16).ToArray()),
                repository.CurrentDataGeneration,
                repository.Keys,
                repository.Credential,
                store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                spool,
                FormatVersions.SealedDataPlane)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = source,
                    Roots = [new ScanRoot("/")],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat((byte)0x67, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_001,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "containment-drill/1.0",
                },
                CancellationToken.None);
        return store;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
