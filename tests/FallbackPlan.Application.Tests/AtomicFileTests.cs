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

        Assert.ThrowsExactly<IOException>(() => AtomicFile.WriteAllText(path, "{}"));

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
