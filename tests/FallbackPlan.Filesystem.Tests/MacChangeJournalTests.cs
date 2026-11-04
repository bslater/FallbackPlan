using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Filesystem.Tests;

/// <summary>
/// What a macOS volume's event history says changed in a set's folders since
/// a place in it (ADR-0191, FR-SNP-011): the folder a file was written in, a
/// folder made, moved or deleted, a folder whose permissions changed. A
/// folder outside the roots is never named, and a change made a moment
/// before the question is in its answer. A changed file with another name
/// cannot be answered, nor a place in another history or ahead of this one,
/// and a folder not there has no place.
/// </summary>
/// <remarks>
/// These read the real history of the volume the temporary folder is on, so
/// they run where macOS does. A run walks every folder above a path it is
/// told of, so where a change may be told by the path itself or by the
/// folder holding it, either is enough, and the assertions say so.
/// </remarks>
[TestClass]
public sealed partial class MacChangeJournalTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-fsevents", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public void ChangedSince_NothingDone_NamesNothing()
    {
        Make("docs");
        var journal = new MacChangeJournal();
        var since = Quiet(journal);

        Assert.IsEmpty(Changed(journal, since));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public void ChangedSince_AFileWrittenAMomentBefore_NamesTheFolderHoldingIt_AndNoOther()
    {
        Make("docs");
        Make("photos");
        var journal = new MacChangeJournal();
        var since = Quiet(journal);

        File.WriteAllText(Path.Combine(_root, "docs", "a.txt"), "new");

        var changed = Changed(journal, since);
        Assert.Contains("docs", changed, Listed(changed));
        Assert.IsFalse(changed.Any(path => Within(path, "photos")), Listed(changed));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public void ChangedSince_AFolderMade_NamesIt()
    {
        Make("docs");
        var journal = new MacChangeJournal();
        var since = Quiet(journal);

        Make("docs/quarterly");

        var changed = Changed(journal, since);
        Assert.Contains("docs/quarterly", changed, Listed(changed));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public void ChangedSince_AFolderMoved_NamesWhereItIs_AndAPathWhereItWas()
    {
        Make("docs/old");
        Make("photos");
        var journal = new MacChangeJournal();
        var since = Quiet(journal);

        Directory.Move(Path.Combine(_root, "docs", "old"), Path.Combine(_root, "photos", "old"));

        var changed = Changed(journal, since);
        Assert.Contains("photos/old", changed, Listed(changed));
        Assert.IsTrue(changed.Any(path => Within(path, "docs")), $"where it was, so the folder that held it is walked: {Listed(changed)}");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public void ChangedSince_AFolderDeletedWithWhatItHeld_NamesAPathInTheFolderThatHeldIt()
    {
        Make("docs/gone/deeper");
        Make("photos");
        File.WriteAllText(Path.Combine(_root, "docs", "gone", "deeper", "a.txt"), "soon gone");
        var journal = new MacChangeJournal();
        var since = Quiet(journal);

        Directory.Delete(Path.Combine(_root, "docs", "gone"), recursive: true);

        var changed = Changed(journal, since);
        Assert.IsTrue(changed.Any(path => Within(path, "docs")), Listed(changed));
        Assert.IsFalse(changed.Any(path => Within(path, "photos")), Listed(changed));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public void ChangedSince_AFoldersPermissionsChanged_NamesIt()
    {
        Make("docs/private");
        Make("photos");
        var journal = new MacChangeJournal();
        var since = Quiet(journal);

        File.SetUnixFileMode(
            Path.Combine(_root, "docs", "private"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var changed = Changed(journal, since);
        Assert.Contains("docs/private", changed, Listed(changed));
        Assert.IsFalse(changed.Any(path => Within(path, "photos")), Listed(changed));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public void ChangedSince_AFolderOutsideTheRoots_IsNotNamed()
    {
        Make("docs");
        Make("photos");
        var journal = new MacChangeJournal();
        var since = Quiet(journal);

        File.WriteAllText(Path.Combine(_root, "docs", "a.txt"), "inside");
        File.WriteAllText(Path.Combine(_root, "photos", "b.txt"), "outside");

        var docs = Path.Combine(_root, "docs");
        var changed = journal.ChangedSince(since, [docs], out var doubt);
        Assert.IsNotNull(changed, $"the history could not say: {doubt}");
        Assert.AreEqual(new ChangedFolder(docs, string.Empty), Assert.ContainsSingle(changed), "the root itself, and nothing beside it");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public void ChangedSince_AFileWithAnotherName_CannotBeAnswered()
    {
        Make("docs");
        Make("photos");
        File.WriteAllText(Path.Combine(_root, "docs", "a.txt"), "one file, two names");
        Assert.AreEqual(0, Link(Path.Combine(_root, "docs", "a.txt"), Path.Combine(_root, "photos", "b.txt")), "link(2)");
        var journal = new MacChangeJournal();
        var since = Quiet(journal);

        File.AppendAllText(Path.Combine(_root, "docs", "a.txt"), ", changed");

        Assert.IsNull(journal.ChangedSince(since, [_root], out var doubt));
        Assert.Contains("another name", doubt!, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public void ChangedSince_APlaceInAnotherHistory_OrAheadOfThisOne_CannotBeAnswered()
    {
        Make("docs");
        var journal = new MacChangeJournal();
        var since = Quiet(journal);

        Assert.IsNull(journal.ChangedSince(since with { Volume = "fsevents:another" }, [_root], out _), "another volume's");
        Assert.IsNull(journal.ChangedSince(since with { JournalId = since.JournalId + 1 }, [_root], out _), "a history reset since");
        Assert.IsNull(journal.ChangedSince(since with { Usn = long.MaxValue }, [_root], out _), "a place it never reached");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public void PositionOf_AFolderNotThere_IsNoPlace_AndSaysWhy()
    {
        var missing = Path.Combine(_root, "not-there");

        Assert.IsNull(MacChangeJournal.PositionOf(missing, out var why));
        Assert.Contains("not a folder", why!, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static bool Within(string path, string folder) =>
        string.Equals(path, folder, StringComparison.Ordinal) || path.StartsWith(folder + "/", StringComparison.Ordinal);

    private static string Listed(string[] changed) => $"named: {string.Join(", ", changed.Select(path => $"'{path}'"))}";

    // A place from which the folder's own making is behind it: what is asked
    // after names only what the test does next.
    [SupportedOSPlatform("macos")]
    private JournalPosition Quiet(MacChangeJournal journal)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var since = journal.PositionOf(_root) ?? throw new AssertFailedException($"no place for {_root}");
            Thread.Sleep(100);
            if (journal.ChangedSince(since, [_root], out _) is { Count: 0 })
            {
                return since;
            }
        }

        throw new AssertFailedException($"the folder {_root} never went quiet");
    }

    [SupportedOSPlatform("macos")]
    private string[] Changed(MacChangeJournal journal, JournalPosition since)
    {
        var changed = journal.ChangedSince(since, [_root], out var doubt);
        Assert.IsNotNull(changed, $"the history could not say: {doubt}");
        Assert.IsTrue(changed.All(folder => folder.Root == _root), "every folder under the root it was asked of");
        return [.. changed.Select(folder => folder.Path)];
    }

    private void Make(string path) => Directory.CreateDirectory(Path.Combine(_root, path));

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string existing, string created);
}
