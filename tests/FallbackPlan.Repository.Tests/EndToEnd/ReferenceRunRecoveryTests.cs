using FallbackPlan.Domain.Configuration;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The recovery tool reads a large file through its reference runs as the
/// product does (ADR-0202 decision 4; FR-ARCH-010): from the passphrase and the
/// store alone it puts down a file of two runs byte for byte, holes and all,
/// and a run it cannot read, or one that does not follow the one before it,
/// fails that file alone with a note naming why, and puts nothing down where
/// it goes (FR-RST-005).
/// </summary>
/// <remarks>
/// The snapshot is <see cref="ReferenceRunHarness"/>'s, built by hand into a
/// repository made from a passphrase, which is what the tool opens.
/// </remarks>
[TestClass]
public sealed class ReferenceRunRecoveryTests : ReferenceRunHarness
{
    private const string PassphraseText = "a file of two runs, recovered by hand";

    private async Task<(Built Built, RecoveryRestoreReport Report, string Output)> RecoverAsync(
        bool writeSecondRun = true, bool gapBeforeSecondRun = false)
    {
        var store = CreateStore();
        Built built;
        using (var passphrase = Passphrase.Create(PassphraseText))
        {
            var (repository, authority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
                store, passphrase, RepositoryCreationSettings.Default,
                createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);
            using var _repository = repository;
            using var _authority = authority;
            built = await BuildAsync(store, repository.Keys, writeSecondRun, gapBeforeSecondRun, repository.RepositoryId);
        }

        var output = Path.Combine(SpoolDirectory, "recovered");
        using var open = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(open, store, CancellationToken.None);
        await session.LoadBlobsAsync(CancellationToken.None);
        var report = await session.RestoreTreeAsync(built.Snapshot.RootTree, output, CancellationToken.None);
        return (built, report, output);
    }

    [TestMethod]
    public async Task TheRecoveryTool_PutsDownAFileOfTwoRuns_ByteForByte()
    {
        var (built, report, output) = await RecoverAsync();

        Assert.AreEqual(1, report.Restored, string.Join("; ", report.Notes));
        Assert.AreEqual(0, report.Failed);
        SequenceAssert.AreEqual(built.Content, File.ReadAllBytes(Path.Combine(output, "huge.bin")));
    }

    [TestMethod]
    public async Task TheRecoveryTool_OverARunItCannotRead_FailsTheFile_NamingTheRun()
    {
        var (built, report, output) = await RecoverAsync(writeSecondRun: false);

        Assert.AreEqual(0, report.Restored);
        Assert.AreEqual(1, report.Failed);
        var note = Assert.ContainsSingle(report.Notes);
        Assert.AreEqual(RecoveryNoteKind.RecordMissing, note.Kind);
        Assert.AreEqual(built.SecondRun, note.Record);
        Assert.AreEqual("huge.bin", note.Path);
        Assert.IsFalse(File.Exists(Path.Combine(output, "huge.bin")), "a file whose runs did not all read is never a shorter file");
    }

    [TestMethod]
    public async Task TheRecoveryTool_OverARunThatDoesNotFollow_FailsTheFile_SayingWhy()
    {
        var (_, report, output) = await RecoverAsync(gapBeforeSecondRun: true);

        Assert.AreEqual(0, report.Restored);
        Assert.AreEqual(1, report.Failed);
        var note = Assert.ContainsSingle(report.Notes);
        Assert.AreEqual(RecoveryNoteKind.RecordUnreadable, note.Kind);
        Assert.AreEqual("huge.bin", note.Path);
        Assert.Contains("run 1", note.Detail!, StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(Path.Combine(output, "huge.bin")));
    }
}
