using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Filesystem.Tests;

/// <summary>
/// What the service's own watch of a set's folders says changed since a
/// place in it, on Linux (ADR-0190, FR-SNP-011): the folder a file was
/// written in, a folder made, moved or deleted and the folder around it, a
/// folder whose permissions changed, and a folder made since, watched in its
/// turn. A folder moved within the roots goes on being named where it now
/// is. A change to a file with another name cannot be answered, since its
/// other folders cannot be found, and neither can a place from another watch,
/// a watch whose queue overflowed, or a root no longer there.
/// </summary>
/// <remarks>
/// These watch real folders with inotify, so they run where Linux does. Each
/// works in a folder of its own, so the rest of the machine's traffic is only
/// ever outside the roots.
/// </remarks>
[TestClass]
public sealed partial class LinuxChangeJournalTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-inotify", Guid.NewGuid().ToString("n"))).FullName;

    private readonly List<IDisposable> _journals = [];

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "inotify is Linux's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void ChangedSince_NothingDone_NamesNothing()
    {
        Make("docs");
        var journal = Journal();
        var since = PositionOf(journal);

        Assert.IsEmpty(Changed(journal, since));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "inotify is Linux's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void ChangedSince_AFileWritten_NamesTheFolderHoldingIt_AndNoOther()
    {
        Make("docs");
        Make("photos");
        var outside = Directory.CreateDirectory(_root + "-outside").FullName;
        var journal = Journal();
        var since = PositionOf(journal);

        File.WriteAllText(Path.Combine(_root, "docs", "a.txt"), "new");
        File.WriteAllText(Path.Combine(outside, "b.txt"), "beside the root, not in it");

        AssertNamed(Changed(journal, since), named: ["docs"], notNamed: ["photos", string.Empty]);
        Directory.Delete(outside, recursive: true);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "inotify is Linux's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void ChangedSince_AFileOverwrittenInTheRoot_NamesTheRoot()
    {
        File.WriteAllText(Path.Combine(_root, "top.txt"), "first");
        var journal = Journal();
        var since = PositionOf(journal);

        File.WriteAllText(Path.Combine(_root, "top.txt"), "second");

        AssertNamed(Changed(journal, since), named: [string.Empty], notNamed: []);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "inotify is Linux's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void ChangedSince_AFolderMade_NamesItAndTheFolderHoldingIt_AndWatchesIt()
    {
        Make("docs");
        var journal = Journal();
        var since = PositionOf(journal);

        // Made in one call, so a folder below is made before anything could
        // have been watching it: it is named for having been made.
        Make("docs/quarterly/june");
        AssertNamed(Changed(journal, since), named: ["docs", "docs/quarterly", "docs/quarterly/june"], notNamed: []);

        var later = PositionOf(journal);
        File.WriteAllText(Path.Combine(_root, "docs", "quarterly", "june", "a.txt"), "written once it was watched");
        AssertNamed(Changed(journal, later), named: ["docs/quarterly/june"], notNamed: ["docs/quarterly"]);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "inotify is Linux's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void ChangedSince_AFolderMoved_NamesWhereItWasAndWhereItIs_AndGoesOnNamingItThere()
    {
        Make("docs/old/inner");
        Make("photos");
        var journal = Journal();
        var since = PositionOf(journal);

        Directory.Move(Path.Combine(_root, "docs", "old"), Path.Combine(_root, "photos", "old"));
        AssertNamed(Changed(journal, since), named: ["docs", "photos", "photos/old"], notNamed: ["docs/old"]);

        var later = PositionOf(journal);
        File.WriteAllText(Path.Combine(_root, "photos", "old", "inner", "a.txt"), "where it is now");
        AssertNamed(Changed(journal, later), named: ["photos/old/inner"], notNamed: ["docs/old/inner", "docs"]);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "inotify is Linux's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void ChangedSince_AFolderMovedOutOfTheRoots_IsNoLongerWatched()
    {
        Make("docs/leaving");
        var outside = Directory.CreateDirectory(_root + "-outside").FullName;
        var journal = Journal();
        var since = PositionOf(journal);

        Directory.Move(Path.Combine(_root, "docs", "leaving"), Path.Combine(outside, "leaving"));
        AssertNamed(Changed(journal, since), named: ["docs"], notNamed: []);

        var later = PositionOf(journal);
        File.WriteAllText(Path.Combine(outside, "leaving", "a.txt"), "outside the roots now");
        Assert.IsEmpty(Changed(journal, later), "nothing outside the roots is named");
        Directory.Delete(outside, recursive: true);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "inotify is Linux's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void ChangedSince_AFolderDeletedWithWhatItHeld_NamesTheFolderThatHeldIt()
    {
        Make("docs/gone/deeper");
        Make("photos");
        File.WriteAllText(Path.Combine(_root, "docs", "gone", "deeper", "a.txt"), "soon gone");
        var journal = Journal();
        var since = PositionOf(journal);

        Directory.Delete(Path.Combine(_root, "docs", "gone"), recursive: true);

        AssertNamed(Changed(journal, since), named: ["docs"], notNamed: ["photos"]);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "inotify is Linux's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void ChangedSince_AFoldersPermissionsChanged_NamesIt()
    {
        Make("docs/private");
        Make("photos");
        var journal = Journal();
        var since = PositionOf(journal);

        File.SetUnixFileMode(
            Path.Combine(_root, "docs", "private"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        AssertNamed(Changed(journal, since), named: ["docs/private"], notNamed: ["photos"]);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "inotify is Linux's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void ChangedSince_AFileWithAnotherName_CannotBeAnswered()
    {
        // Linux keeps no list of a file's names: its other folder cannot be
        // found, and would be taken as it was.
        Make("docs");
        Make("photos");
        File.WriteAllText(Path.Combine(_root, "docs", "a.txt"), "one file, two names");
        Assert.AreEqual(0, Link(Path.Combine(_root, "docs", "a.txt"), Path.Combine(_root, "photos", "b.txt")), "link(2)");
        var journal = Journal();
        var since = PositionOf(journal);

        File.AppendAllText(Path.Combine(_root, "docs", "a.txt"), ", changed");

        Assert.IsNull(journal.ChangedSince(since, [_root], out var doubt));
        Assert.Contains("another name", doubt!, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "inotify is Linux's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void ChangedSince_APlaceFromAnotherWatch_CannotBeAnswered()
    {
        Make("docs");
        var first = Journal();
        var since = PositionOf(first);
        var second = Journal();
        PositionOf(second);

        Assert.IsNull(second.ChangedSince(since, [_root], out _), "a place from another watch");
        Assert.IsNull(first.ChangedSince(since with { JournalId = since.JournalId + 1 }, [_root], out _), "another session");
        Assert.IsNull(first.ChangedSince(since with { Usn = since.Usn + 1_000_000 }, [_root], out _), "a place it never reached");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "inotify is Linux's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void ChangedSince_AfterTheQueueOverflowed_CannotBeAnswered_AndANewPlaceCanBe()
    {
        Make("docs");
        var journal = new LinuxChangeJournal(readsInBackground: false);
        _journals.Add(journal);
        var since = PositionOf(journal);

        // Alternating names, so the kernel merges none of them.
        var limit = int.Parse(File.ReadAllText("/proc/sys/fs/inotify/max_queued_events").Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var a = Path.Combine(_root, "docs", "a.txt");
        var b = Path.Combine(_root, "docs", "b.txt");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        for (var i = 0; i <= limit; i++)
        {
            File.SetLastWriteTimeUtc(i % 2 == 0 ? a : b, DateTime.UtcNow.AddSeconds(-i));
        }

        Assert.IsNull(journal.ChangedSince(since, [_root], out var doubt));
        Assert.Contains("overflowed", doubt!, StringComparison.Ordinal);

        var after = PositionOf(journal);
        Assert.IsEmpty(Changed(journal, after), "a place taken after the overflow can be answered");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "inotify is Linux's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void ChangedSince_APlaceOlderThanTheWatchKeeps_CannotBeAnswered_AndALaterOneCan()
    {
        // A service runs for months: the watch lets go of the older half of
        // what it keeps, and a place from before that is doubt, not silence.
        var journal = new LinuxChangeJournal(readsInBackground: false, mostFoldersKept: 8);
        _journals.Add(journal);
        var since = PositionOf(journal);
        for (var i = 0; i < 12; i++)
        {
            Make($"folder-{i}");
        }

        Assert.IsNull(journal.ChangedSince(since, [_root], out var doubt));
        Assert.Contains("older than", doubt!, StringComparison.Ordinal);

        var later = PositionOf(journal);
        File.WriteAllText(Path.Combine(_root, "folder-11", "a.txt"), "after");
        AssertNamed(Changed(journal, later), named: ["folder-11"], notNamed: ["folder-0"]);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "inotify is Linux's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void ChangedSince_ARootDeleted_CannotBeAnswered()
    {
        Make("docs");
        var journal = Journal();
        var since = PositionOf(journal);

        Directory.Delete(_root, recursive: true);

        Assert.IsNull(journal.ChangedSince(since, [_root], out _));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "inotify is Linux's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void PositionOf_AFolderNotThere_IsNoPlace()
    {
        var journal = Journal();

        Assert.IsNull(journal.PositionOf(Path.Combine(_root, "not-there")));
        Assert.IsNotNull(journal.WhyNoPosition(Path.Combine(_root, "not-there")));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "inotify is Linux's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void PositionOf_AFolderAsked_IsWatchedFromThen_AndAskingAgainKeepsTheSameWatch()
    {
        Make("docs");
        var journal = Journal();

        var first = PositionOf(journal);
        File.WriteAllText(Path.Combine(_root, "docs", "a.txt"), "after the first place");
        var second = PositionOf(journal);

        Assert.AreEqual(first.JournalId, second.JournalId, "one watch, asked twice");
        Assert.IsGreaterThan(first.Usn, second.Usn);
        AssertNamed(Changed(journal, first), named: ["docs"], notNamed: []);
        Assert.IsEmpty(Changed(journal, second));
    }

    public void Dispose()
    {
        foreach (var journal in _journals)
        {
            journal.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [SupportedOSPlatform("linux")]
    private LinuxChangeJournal Journal()
    {
        var journal = new LinuxChangeJournal();
        _journals.Add(journal);
        return journal;
    }

    [SupportedOSPlatform("linux")]
    private JournalPosition PositionOf(LinuxChangeJournal journal) =>
        journal.PositionOf(_root) ?? throw new AssertFailedException($"no place for {_root}: {journal.WhyNoPosition(_root)}");

    [SupportedOSPlatform("linux")]
    private string[] Changed(LinuxChangeJournal journal, JournalPosition since)
    {
        var changed = journal.ChangedSince(since, [_root], out var doubt);
        Assert.IsNotNull(changed, $"the watch could not say: {doubt}");
        Assert.IsTrue(changed.All(folder => folder.Root == _root), "every folder under the root it was asked of");
        return [.. changed.Select(folder => folder.Path)];
    }

    private static void AssertNamed(string[] changed, string[] named, string[] notNamed)
    {
        foreach (var folder in named)
        {
            Assert.Contains(folder, changed, $"'{folder}' is named among {string.Join(", ", changed.Select(c => $"'{c}'"))}");
        }

        // A folder above one named may be named too, which costs nothing:
        // a run walks every folder above one it was told of anyway.
        foreach (var folder in notNamed)
        {
            Assert.DoesNotContain(folder, changed, $"'{folder}' is not named among {string.Join(", ", changed.Select(c => $"'{c}'"))}");
        }
    }

    private void Make(string path) => Directory.CreateDirectory(Path.Combine(_root, path));

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string existing, string created);
}
