using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The recovery tool never replaces a file it restored itself (FR-RST-005;
/// ADR-0170 Amendment 4). Where the output's volume takes two of a
/// snapshot's names for one, by case or by Unicode normalisation, the second
/// is refused and noted, naming the first, which keeps what the tool wrote
/// there, as the executor fails it (ADR-0170 Amendment 3). Where the volume
/// keeps them apart, each is restored with its own content.
/// </summary>
[TestClass]
public sealed class RecoveryNamesTests : IDisposable
{
    private const string PassphraseText = "recovery-names-passphrase!!";

    private const string Composed = "names/caf\u00E9.txt";
    private const string Decomposed = "names/cafe\u0301.txt";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-recovery-names", Guid.NewGuid().ToString("n"));

    public RecoveryNamesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A scratch directory that will not delete is not a test failure.
        }
    }

    [TestMethod]
    public async Task RecoveryRestore_TwoNamesTheVolumeTakesForOne_TheSecondIsRefusedNamingTheFirst_WhichKeepsItsContent()
    {
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["names/Notes.txt"] = [1, 1, 1],
            ["names/notes.txt"] = [2, 2, 2, 2],
            [Composed] = [3, 3, 3, 3, 3],
            [Decomposed] = [4, 4, 4, 4, 4, 4],
        };
        var source = new FakeFileSystemSource();
        foreach (var (path, content) in contents)
        {
            source.AddFile(path, content);
        }

        var output = Path.Combine(_root, "restored");

        var report = await RestoreIntoAsync(source, output);

        // Judged by the volume the tool wrote to, which is the test's own.
        var foldsCase = VolumeFolds("Probe.txt", "probe.txt");
        var foldsForm = VolumeFolds("caf\u00E9.txt", "cafe\u0301.txt");
        var refusedPairs = (foldsCase ? 1 : 0) + (foldsForm ? 1 : 0);
        AssertOneRecoveredPerName(report, output, contents, foldsCase, "names/Notes.txt", "names/notes.txt");
        AssertOneRecoveredPerName(report, output, contents, foldsForm, Composed, Decomposed);
        Assert.AreEqual((4 - refusedPairs, refusedPairs), (report.Restored, report.Failed));
    }

    /// <summary>
    /// Where the volume keeps <paramref name="one"/> and <paramref name="other"/>
    /// apart, each was restored with its own content and neither is noted.
    /// Where it takes them for one, one holds its own content and the other
    /// is refused, naming it.
    /// </summary>
    private static void AssertOneRecoveredPerName(
        RecoveryRestoreReport report, string output, Dictionary<string, byte[]> contents, bool folds, string one, string other)
    {
        var noted = report.Notes.Where(note => note.Path == one || note.Path == other).ToList();
        if (!folds)
        {
            Assert.IsEmpty(noted);
            SequenceAssert.AreEqual(contents[one], File.ReadAllBytes(Path.Combine(output, one)));
            SequenceAssert.AreEqual(contents[other], File.ReadAllBytes(Path.Combine(output, other)));
            return;
        }

        var refused = Assert.ContainsSingle(noted);
        Assert.AreEqual(RecoveryNoteKind.Refused, refused.Kind);
        var landed = refused.Path == one ? other : one;
        Assert.Contains($"the tool restored {landed} there", refused.Reason!, StringComparison.Ordinal);
        SequenceAssert.AreEqual(contents[landed], File.ReadAllBytes(Path.Combine(output, landed)));
    }

    /// <summary>Whether this test's volume finds a file written as <paramref name="written"/> when asked for <paramref name="asked"/>.</summary>
    private bool VolumeFolds(string written, string asked)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, $"folds-{Guid.NewGuid():N}")).FullName;
        File.WriteAllBytes(Path.Combine(folder, written), []);
        return File.Exists(Path.Combine(folder, asked));
    }

    /// <summary>
    /// Backs <paramref name="source"/> up into a repository made from the
    /// passphrase, then restores its one snapshot into
    /// <paramref name="output"/> with the recovery session alone.
    /// </summary>
    private async Task<RecoveryRestoreReport> RestoreIntoAsync(FakeFileSystemSource source, string output)
    {
        var store = new LocalFileSystemObjectStore(Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("n")));
        using (var passphrase = Passphrase.Create(PassphraseText))
        {
            var (repository, authority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
                store, passphrase, RepositoryCreationSettings.Default,
                createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);
            using var _repository = repository;
            using var _authority = authority;

            var spool = Directory.CreateDirectory(Path.Combine(_root, "spool-" + Guid.NewGuid().ToString("n"))).FullName;
            await new PublicationOrchestrator(
                    CapturePolicy.Default with { SegmentSize = SegmentSize.Create(64 * 1024) },
                    repository.RepositoryId,
                    WriterId.FromBytes(Enumerable.Repeat((byte)0xA1, 16).ToArray()),
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
                        DeviceId = Enumerable.Repeat((byte)0x23, 16).ToArray(),
                        BackupSetId = Enumerable.Repeat((byte)0x34, 16).ToArray(),
                        SnapshotId = Enumerable.Repeat((byte)0x69, 16).ToArray(),
                        NowUnixMilliseconds = 1_722_600_000_001,
                        DeclaredMaxDurationMs = 3_600_000,
                        ExpiryGeneration = 5,
                        ClientVersion = "recovery-names/1.0",
                    },
                    CancellationToken.None);
        }

        using var open = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(open, store, CancellationToken.None);
        await session.LoadBlobsAsync(CancellationToken.None);
        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));
        return await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, CancellationToken.None);
    }
}
