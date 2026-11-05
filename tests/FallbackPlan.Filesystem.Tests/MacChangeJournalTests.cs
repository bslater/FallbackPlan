using System.Globalization;
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
/// before the question is in its answer, and so is one made long enough
/// before it that the system has numbered it and the replay gives it as
/// history rather than as it happens. The history is read only once a mark
/// written after the question has been heard, and holds that mark and the
/// change made before it (ADR-0191 Amendment 1); a history that does not yet
/// hold the mark is read again, and one that never does cannot be answered.
/// A changed file with another name cannot be answered, nor a place in
/// another history or ahead of this one, and a folder not there has no
/// place.
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

    // A folder of marks for a journal whose reads a test changes, so what it
    // takes out of a read is only that journal's marks.
    private readonly string _marks = Path.Combine(Path.GetTempPath(), "fbp-fsevents-marks", Guid.NewGuid().ToString("n"));

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
    public void ChangedSince_AFileWrittenLongBeforeTheQuestion_NamesTheFolderHoldingIt()
    {
        // Long enough for the system to have numbered the change: the replay
        // gives it as history, not as it happens.
        Make("docs");
        Make("photos");
        var journal = new MacChangeJournal();
        var since = Quiet(journal);

        File.WriteAllText(Path.Combine(_root, "docs", "a.txt"), "new");
        Thread.Sleep(3_000);

        var changed = journal.ChangedSince(since, [_root], out var doubt);
        Assert.IsNotNull(changed, $"the history could not say: {doubt}");
        var names = changed.Select(folder => folder.Path).ToArray();
        Assert.Contains("docs", names, $"{Listed(names)}; {Heard(journal, since)}");
        Assert.IsFalse(names.Any(path => Within(path, "photos")), Listed(names));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public void ChangedSince_ReadsAHistoryHoldingAMarkHeardAfterTheChange_AndTheChangeWithIt()
    {
        // The change made a moment before is numbered before the mark, so a
        // history holding the mark holds the change: it is not left between
        // a history that does not hold it yet and events heard after it.
        Make("docs");
        var journal = new MacChangeJournal();
        var since = Quiet(journal);

        File.WriteAllText(Path.Combine(_root, "docs", "a.txt"), "new");

        var heard = journal.HeardSince(since, [_root], out var doubt);
        Assert.IsNotNull(heard, $"the history could not say: {doubt}");
        var (replayed, mark) = heard.Value;
        Assert.IsTrue(
            replayed.Any(e => string.Equals(e.Path.TrimEnd('/'), mark, StringComparison.OrdinalIgnoreCase)),
            $"the mark '{mark}', in {Described(replayed)}");
        Assert.IsTrue(
            replayed.Any(e => e.Path.EndsWith("/docs/a.txt", StringComparison.OrdinalIgnoreCase)),
            $"the change, in {Described(replayed)}");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public void ChangedSince_AHistoryNotYetHoldingItsMark_IsReadAgain()
    {
        Make("docs");
        Make("photos");
        var since = Quiet(new MacChangeJournal());
        var reads = 0;
        var journal = new MacChangeJournal(_marks)
        {
            // The first read is given as a system that has not yet put the
            // mark in its history would give it.
            AsRead = replayed => ++reads == 1 ? WithoutMarks(replayed) : replayed,
        };

        File.WriteAllText(Path.Combine(_root, "docs", "a.txt"), "new");

        var changed = Changed(journal, since);
        Assert.Contains("docs", changed, Listed(changed));
        Assert.IsFalse(changed.Any(path => Within(path, "photos")), Listed(changed));
        Assert.AreEqual(2, reads, "read again once, and answered from the read that held it");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public void ChangedSince_AHistoryThatNeverHoldsItsMark_CannotBeAnswered()
    {
        Make("docs");
        var since = Quiet(new MacChangeJournal());
        var journal = new MacChangeJournal(_marks) { AsRead = WithoutMarks };

        File.WriteAllText(Path.Combine(_root, "docs", "a.txt"), "new");

        Assert.IsNull(journal.ChangedSince(since, [_root], out var doubt));
        Assert.Contains("did not hold the mark", doubt!, StringComparison.Ordinal);
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
        foreach (var folder in new[] { _root, _marks })
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private static bool Within(string path, string folder) =>
        string.Equals(path, folder, StringComparison.Ordinal) || path.StartsWith(folder + "/", StringComparison.Ordinal);

    private static string Listed(string[] changed) => $"named: {string.Join(", ", changed.Select(path => $"'{path}'"))}";

    // Every event the history replayed, as the system gave it, so a wrong
    // answer says what the history held.
    [SupportedOSPlatform("macos")]
    private string Heard(MacChangeJournal journal, JournalPosition since)
    {
        var heard = journal.HeardSince(since, [_root], out var doubt);
        return heard is { } read
            ? $"since {since.Usn}, with the mark '{read.Mark}', {Described(read.Replayed)}"
            : $"the history could not be read: {doubt}";
    }

    private static string Described(IReadOnlyList<(string Path, uint Flags, ulong Id)> replayed) =>
        $"the history replayed {replayed.Count}: "
            + string.Join("; ", replayed.Select(e => string.Create(CultureInfo.InvariantCulture, $"'{e.Path}' 0x{e.Flags:X} #{e.Id}")));

    // A read without this test's marks in it.
    private IReadOnlyList<(string Path, uint Flags, ulong Id)> WithoutMarks(IReadOnlyList<(string Path, uint Flags, ulong Id)> replayed) =>
        [.. replayed.Where(e => !e.Path.Contains(Path.GetFileName(_marks), StringComparison.OrdinalIgnoreCase))];

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
