using FallbackPlan.Domain;
using System.Runtime.Versioning;
using System.Text.Json;
using FallbackPlan.Application;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// Whole-file replacement that a crash cannot half-apply. The failure this
/// closes is not exotic: <c>File.WriteAllText</c> truncates first, so a crash
/// between truncate and write leaves a zero-length <c>state.json</c>, and that
/// file carries the device's writer identity.
/// </summary>
/// <remarks>
/// One residue is left uncovered on purpose and is named here rather than
/// left to be rediscovered: the two catch arms of the private
/// <c>TryDelete</c>. Reaching them means making the deletion of the temporary
/// fail, and the temporary's name is a fresh GUID chosen inside the call — so
/// there is no way to arrange an undeletable file at that path from outside.
/// They swallow and continue by construction ("untidy, never incorrect"), so
/// what they could hide is a leftover file, which the tests below already
/// assert the absence of on the paths that can be reached.
/// </remarks>
[TestClass]
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fbp-atomic-tests", Guid.NewGuid().ToString("n"));

    public AtomicFileTests() => Directory.CreateDirectory(_root);

    [TestMethod]
    public void WriteAllText_WhenTheDirectoryDoesNotExist_ShouldCreateItAndWriteTheFile()
    {
        var path = Path.Combine(_root, "nested", "state.json");

        AtomicFile.WriteAllText(path, "{}");

        Assert.AreEqual("{}", File.ReadAllText(path));
    }

    [TestMethod]
    public void WriteAllText_WhenReplacingAnExistingFile_ShouldLeaveNoTemporaryFileBehind()
    {
        var path = Path.Combine(_root, "state.json");

        AtomicFile.WriteAllText(path, "first");
        AtomicFile.WriteAllText(path, "second");

        Assert.AreEqual("second", File.ReadAllText(path));
        Assert.IsEmpty(Directory.GetFiles(_root, "*.tmp"));
    }

    [TestMethod]
    public void WriteAllText_WhenReplacingRepeatedly_ShouldNeverExposeATruncatedFile()
    {
        // The property that matters: at no instant does the destination hold a
        // truncated file. Observing the instant directly needs a crash, so the
        // proxy is that a reader opening the path between writes always sees
        // one complete document, never an empty one.
        var path = Path.Combine(_root, "state.json");
        AtomicFile.WriteAllText(path, """{"schema_version":1}""");

        for (var i = 0; i < 50; i++)
        {
            AtomicFile.WriteAllText(path, $$"""{"schema_version":1,"n":{{i}}}""");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.AreEqual(1, document.RootElement.GetProperty("schema_version").GetInt32());
        }
    }

    [TestMethod]
    public void WriteAllText_WhenCalledConcurrently_ShouldNotCollideOnATemporaryName()
    {
        // Under single ownership two writers here would be a bug, but a fixed
        // temp name is the kind of assumption that is safe until it is not —
        // FileSequenceStateStore.Save used one, and that is a hazard ADR-0028
        // names explicitly.
        var path = Path.Combine(_root, "state.json");

        Parallel.For(0, 32, i => AtomicFile.WriteAllText(path, $$"""{"n":{{i}}}"""));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.IsTrue(document.RootElement.TryGetProperty("n", out _));
        Assert.IsEmpty(Directory.GetFiles(_root, "*.tmp"));
    }

    [TestMethod]
    public void WriteAllText_WhenTheReplaceCannotSucceed_ThrowsAndTakesItsTemporaryFileWithIt()
    {
        // The failure half of the primitive every durable state file writes
        // through. A directory standing where the state file belongs is the
        // cheapest way to make the replace fail permanently, and it exercises
        // the whole path: the attempts run out, the outer catch removes the
        // temporary it created, and the original exception is rethrown rather
        // than swallowed into a silent no-write.
        //
        // Leaving the temporary behind would be the quiet failure — the write
        // reports an error, somebody retries, and the state directory
        // accumulates orphans nothing ever collects.
        var path = Path.Combine(_root, "state.json");
        Directory.CreateDirectory(path);

        // POSIX reports the directory-in-the-way as IOException; Windows
        // reports it as UnauthorizedAccessException. The contract under test
        // is that the failure escapes and cleans up, not which of the two
        // shapes the platform gives it.
        var thrown = Assert.Throws<Exception>(() => AtomicFile.WriteAllText(path, "{}"));
        Assert.IsTrue(
            thrown is IOException or UnauthorizedAccessException,
            $"an unexpected exception type escaped: {thrown.GetType().Name}");

        Assert.IsEmpty(
            Directory.GetFiles(_root, "*.tmp"),
            "a write that failed must not leave its temporary file in the state directory");
    }

    [TestMethod]
    [UnprivilegedPlatformCondition(TestPlatforms.Posix, "The subject is a directory the process may not write to.")]
    [UnsupportedOSPlatform("windows")]
    public void WriteAllText_WhenTheTemporaryCannotBeCreated_FailsWithoutTouchingWhatIsAlreadyThere()
    {
        // The other side of the same catch: the write fails before a temporary
        // exists at all. What matters is that the destination is untouched —
        // the old contents are still readable, which is the entire reason this
        // type exists instead of File.WriteAllText.
        var directory = Path.Combine(_root, "sealed");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        AtomicFile.WriteAllText(path, "original");

        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => AtomicFile.WriteAllText(path, "replacement"));
            Assert.AreEqual("original", File.ReadAllText(path));
        }
        finally
        {
            File.SetUnixFileMode(
                directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows,
        "a rename holds the file it renamed open for deletion until it returns, and only Windows refuses "
        + "a reader that does not share deletion with that handle")]
    public void ReadAllText_WhileAReplaceHoldsTheFileForDeletion_StillReadsIt()
    {
        // A replace renames the new file into place, and the rename holds that
        // file open for deletion until it returns. A reader that opens it then
        // without sharing deletion is refused, so a state file read while it
        // is being saved would fail instead of reading one version or the other.
        //
        // The handle below is that moment held still: opened for deletion,
        // sharing everything, as the rename's own is. Delete-on-close is how
        // .NET asks for deletion access, so the file goes when it closes,
        // after the read this test is about.
        var path = Path.Combine(_root, "state.json");
        AtomicFile.WriteAllText(path, "whole");

        using (File.OpenHandle(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.DeleteOnClose))
        {
            Assert.AreEqual("whole", AtomicFile.ReadAllText(path));
        }
    }

    [TestMethod]
    public void ReadAllBytes_ReadsTheFileWhole()
    {
        var path = Path.Combine(_root, "credential.bin");
        File.WriteAllBytes(path, [0x00, 0x01, 0xFE, 0xFF]);

        CollectionAssert.AreEqual(new byte[] { 0x00, 0x01, 0xFE, 0xFF }, AtomicFile.ReadAllBytes(path));
    }

    [TestMethod]
    public void ReadAllLines_ReadsEveryLine_WithoutItsEnding()
    {
        var path = Path.Combine(_root, "sequence.txt");
        File.WriteAllText(path, "next=4\npending=\r\nlast-index-generation=2\n");

        CollectionAssert.AreEqual(
            new[] { "next=4", "pending=", "last-index-generation=2" }, AtomicFile.ReadAllLines(path));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows,
        "a rename holds the file it renamed open for deletion until it returns, and only Windows refuses "
        + "a reader that does not share deletion with that handle")]
    public void ReadAllBytes_WhileAReplaceHoldsTheFileForDeletion_StillReadsIt()
    {
        // As the text read above: the handle is a rename's moment held still.
        var path = Path.Combine(_root, "credential.bin");
        File.WriteAllBytes(path, [0x2A]);

        using (File.OpenHandle(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.DeleteOnClose))
        {
            CollectionAssert.AreEqual(new byte[] { 0x2A }, AtomicFile.ReadAllBytes(path));
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows,
        "a rename holds the file it renamed open for deletion until it returns, and only Windows refuses "
        + "a reader that does not share deletion with that handle")]
    public void ReadAllLines_WhileAReplaceHoldsTheFileForDeletion_StillReadsIt()
    {
        var path = Path.Combine(_root, "sequence.txt");
        File.WriteAllText(path, "next=4\n");

        using (File.OpenHandle(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.DeleteOnClose))
        {
            CollectionAssert.AreEqual(new[] { "next=4" }, AtomicFile.ReadAllLines(path));
        }
    }

    [TestMethod]
    public void AContendedReplace_WaitsAtLeast383AndAtMost766MillisecondsInAll()
    {
        // Each wait is drawn from the upper half of a ceiling that doubles
        // from 2 ms to 128 ms, so writers contending for one destination spread
        // apart rather than retry in step (ADR-0010 Amendment 10). The least it
        // can wait in all is what a writer holding the destination briefly is
        // given, and well past the 100 ms the Windows case below holds it; the
        // most is how late a destination that never frees is reported.
        Assert.AreEqual(TimeSpan.FromMilliseconds(383), AtomicFile.LeastReplaceWait);
        Assert.AreEqual(TimeSpan.FromMilliseconds(766), AtomicFile.MostReplaceWait);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(1), AtomicFile.MostReplaceWait);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows,
        "only Windows refuses to rename a file over one another handle holds open without sharing deletion")]
    [DoNotParallelize] // The handle's release is timed against the replace's real waits.
    public void WriteAllText_WhileAReaderHoldsTheDestinationBriefly_WaitsForIt_AndReplacesIt()
    {
        // A reader that does not share deletion, as one written before
        // Amendment 8 did, holds the replace off until it closes. Released on
        // a thread of its own, so a busy pool cannot hold it past the waits.
        var path = Path.Combine(_root, "state.json");
        AtomicFile.WriteAllText(path, "before");
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = new Thread(() =>
        {
            Thread.Sleep(100);
            held.Dispose();
        });
        release.Start();

        try
        {
            AtomicFile.WriteAllText(path, "after");
        }
        finally
        {
            release.Join();
        }

        Assert.AreEqual("after", AtomicFile.ReadAllText(path));
        Assert.IsEmpty(Directory.GetFiles(_root, "*.tmp"));
    }

    [TestMethod]
    public void IsReplaceContention_ContentionAndDefects_AreToldApart()
    {
        // The classification is the part of the retry a test can reach.
        // `rename(2)` is atomic and simply wins or loses, so no POSIX test can
        // manufacture the Windows ERROR_ACCESS_DENIED the loop absorbs — but
        // getting this predicate wrong is what would actually break, in either
        // direction: retry a defect and the report is ten sleeps late, refuse
        // to retry contention and a healthy write fails under a race the
        // writer role is supposed to make impossible anyway.
        Assert.IsTrue(AtomicFile.IsReplaceContention(new UnauthorizedAccessException()));
        Assert.IsTrue(AtomicFile.IsReplaceContention(new IOException()));

        // Both of these ARE IOExceptions, which is exactly why they are named:
        // a missing source or directory is a defect, and delaying it hides it.
        Assert.IsFalse(AtomicFile.IsReplaceContention(new FileNotFoundException()));
        Assert.IsFalse(AtomicFile.IsReplaceContention(new DirectoryNotFoundException()));

        // Anything else is not the filesystem talking and is not retried.
        Assert.IsFalse(AtomicFile.IsReplaceContention(new InvalidOperationException()));
        Assert.IsGreaterThan(1, AtomicFile.ReplaceAttempts, "a single attempt is not a retry");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
