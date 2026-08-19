using FallbackPlan.Domain;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.RecoveryKit;
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
/// exactly as adversarial for the kit drill as for the daily restore; a
/// tree naming <c>..</c> must fail that entry with a stated refusal, not
/// write beside the chosen output.
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
        using var repository = await RepositoryLifecycle.CreateAsync(
            store, passphrase, Domain.Configuration.RepositoryCreationSettings.Default,
            createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);

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
                BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                SnapshotId = Enumerable.Repeat((byte)0x66, 16).ToArray(),
                NowUnixMilliseconds = 1_722_600_000_001,
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = 5,
                ClientVersion = "containment-drill/1.0",
            },
            CancellationToken.None);

        using var exportPassphrase = Passphrase.Create(PassphraseText);
        var kit = await RecoveryKitFactory.BuildAsync(
            store, exportPassphrase, Enumerable.Repeat((byte)0x22, 16).ToArray(),
            issuedAt: 1_722_600_000_002, destinations: [], CancellationToken.None);

        using var openPassphrase = Passphrase.Create(PassphraseText);
        using var session = RecoverySession.Open(kit, openPassphrase, store);
        await session.LoadBlobsAsync(CancellationToken.None);
        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));

        // Restore into a NESTED output whose parent this test owns, so an
        // escape through '..' would land at a path we can prove empty.
        var parent = Path.Combine(_root, "nested");
        var output = Path.Combine(parent, "restored");
        var report = await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, CancellationToken.None);

        // The hostile entry fails by name; the honest file still restores.
        Assert.IsTrue(report.Failed >= 1, "the '..' entry must be counted as failed");
        Assert.Contains(note => note.Contains("refused", StringComparison.Ordinal)
            && note.Contains("..", StringComparison.Ordinal), report.Notes);
        SequenceAssert.AreEqual(good, File.ReadAllBytes(Path.Combine(output, "good.txt")));

        // Nothing landed outside the chosen output — not the file, and not
        // a spool for it either.
        Assert.IsFalse(File.Exists(Path.Combine(parent, "escaped.bin")), "the restore escaped the output directory");
        SequenceAssert.AreEqual(
            new[] { Path.GetFileName(output) },
            [.. Directory.EnumerateFileSystemEntries(parent).Select(Path.GetFileName).Order()]);
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
