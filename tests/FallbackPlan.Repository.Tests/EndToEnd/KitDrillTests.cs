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
/// The recovery-kit drill (FR-KIT-001, FR-KIT-003, FR-KIT-006): a real
/// repository is created, backed up, and then restored using ONLY the
/// store, the exported kit, and the passphrase — no state directory, no
/// catalogue, no engine; the standalone tool's session does everything.
/// A wrong passphrase opens nothing, and a transcription error in the
/// text kit is caught at the offending line, never guessed around.
/// </summary>
[TestClass]
public sealed class KitDrillTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-kit-drill", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "kit-drill-passphrase-with-length";

    public KitDrillTests() => Directory.CreateDirectory(_root);

    private async Task<(LocalFileSystemObjectStore Store, RecoveryKit Kit, Dictionary<string, byte[]> Files)>
        CreateBackedUpRepositoryAsync()
    {
        var store = new LocalFileSystemObjectStore(Path.Combine(_root, "repo"));

        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.CreateAsync(
            store, passphrase, Domain.Configuration.RepositoryCreationSettings.Default,
            createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);

        var files = new Dictionary<string, byte[]>
        {
            ["docs/report.bin"] = new byte[150_000],
            ["docs/notes.txt"] = new byte[700],
            ["top.bin"] = new byte[65_000],
        };
        var random = new Random(21);
        var source = new FakeFileSystemSource();
        foreach (var (path, content) in files)
        {
            random.NextBytes(content);
            source.AddFile(path, content);
        }

        var spool = Path.Combine(_root, "spool");
        Directory.CreateDirectory(spool);
        var generation = repository.CurrentDataGeneration;
        var orchestrator = new PublicationOrchestrator(
            Domain.Configuration.CapturePolicy.Default with
            {
                SegmentSize = Domain.SegmentSize.Create(64 * 1024),
            },
            repository.RepositoryId,
            Domain.Identifiers.WriterId.FromBytes(Enumerable.Repeat((byte)0xA0, 16).ToArray()),
            generation,
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
                SnapshotId = Enumerable.Repeat((byte)0x55, 16).ToArray(),
                NowUnixMilliseconds = 1_722_600_000_001,
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = 5,
                ClientVersion = "kit-drill/1.0",
            },
            CancellationToken.None);

        // Export the kit — the passphrase provably opens what is exported.
        using var exportPassphrase = Passphrase.Create(PassphraseText);
        var kit = await RecoveryKitFactory.BuildAsync(
            store, exportPassphrase, Enumerable.Repeat((byte)0x22, 16).ToArray(),
            issuedAt: 1_722_600_000_002, destinations: [], CancellationToken.None);

        return (store, kit, files);
    }

    [TestMethod]
    public async Task RecoveryDrill_AKitAndItsPassphraseAlone_RestoreEverythingWithNoLocalState()
    {
        var (store, kit, files) = await CreateBackedUpRepositoryAsync();

        // The kit survives its own serialisation round trip — what the
        // drill uses is what a printed page holds (FR-KIT-002/003).
        var framed = RecoveryKitCodec.Serialize(kit);
        var reparsed = RecoveryKitCodec.Parse(RecoveryKitText.ParseToFramed(RecoveryKitText.Render(framed)));

        // ONLY store + kit + passphrase from here (FR-KIT-006).
        using var passphrase = Passphrase.Create(PassphraseText);
        using var session = RecoverySession.Open(reparsed, passphrase, store);

        var (blobs, notes) = await session.LoadBlobsAsync(CancellationToken.None);
        Assert.IsTrue(blobs > 0);
        Assert.IsEmpty(notes);

        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));
        Assert.IsTrue(snapshot.SignatureVerified, "the recovered snapshot's signature must verify (06 §6.1)");

        var output = Path.Combine(_root, "restored");
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
    public async Task RecoveryDrill_ThePassphraseIsWrong_OpensNothing()
    {
        var (store, kit, _) = await CreateBackedUpRepositoryAsync();

        using var wrong = Passphrase.Create("wrong-passphrase-entirely-here!");
        Assert.ThrowsExactly<KeyUnwrapFailedException>(() => RecoverySession.Open(kit, wrong, store));
    }

    [TestMethod]
    public async Task RecoveryDrill_TheKitTextIsMistranscribed_IsCaughtAtTheOffendingLine()
    {
        var (_, kit, _) = await CreateBackedUpRepositoryAsync();
        var text = RecoveryKitText.Render(RecoveryKitCodec.Serialize(kit));

        // Flip one payload character on a data line — the classic
        // transcription mistake the per-line check exists for (FR-KIT-003).
        var lines = text.Split('\n');
        var lineIndex = Array.FindIndex(lines, line => line.StartsWith("03:", StringComparison.Ordinal));
        Assert.IsTrue(lineIndex >= 0, "the text kit has a line 03");
        var line = lines[lineIndex].ToCharArray();
        var payloadStart = 4;
        line[payloadStart] = line[payloadStart] == 'a' ? 'b' : 'a';
        lines[lineIndex] = new string(line);

        var exception = Assert.Throws<FormatException>(
            () => RecoveryKitText.ParseToFramed(string.Join('\n', lines)));
        Assert.Contains("3", exception.Message, StringComparison.Ordinal);
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
