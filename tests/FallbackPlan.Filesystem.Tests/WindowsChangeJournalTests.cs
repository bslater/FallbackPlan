using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Filesystem.Tests;

/// <summary>
/// What a Windows volume's change journal says changed in a set's folders
/// since a place in it (ADR-0187, FR-SNP-011): the folder a file was written
/// in, a folder made, moved or deleted and the folders around it, a folder
/// whose permissions changed, and every folder holding a name of a file
/// changed through another. A folder outside the roots is never named, and a
/// root spelled in another case or by its short name is still the root. A
/// place in another journal, or one older than the journal still holds,
/// cannot be answered, and neither can a place on a path that keeps none.
/// </summary>
/// <remarks>
/// These read the real journal of the volume the temporary folder is on, so
/// they run where Windows does. Each works in a folder of its own, so the
/// rest of the volume's traffic is only ever outside the roots.
/// </remarks>
[TestClass]
public sealed partial class WindowsChangeJournalTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-journal", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "the change journal is NTFS's")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void ChangedSince_NothingDone_NamesNothing()
    {
        Make("docs");
        var journal = new WindowsChangeJournal();
        var since = PositionOf(journal);

        var changed = journal.ChangedSince(since, [_root]);

        Assert.IsNotNull(changed);
        Assert.IsEmpty(changed);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "the change journal is NTFS's")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void ChangedSince_AFileWritten_NamesTheFolderHoldingIt_AndNoOther()
    {
        Make("docs");
        Make("photos");
        var journal = new WindowsChangeJournal();
        var since = PositionOf(journal);

        File.WriteAllText(Path.Combine(_root, "docs", "a.txt"), "new");

        AssertNamed(Changed(journal, since), named: ["docs"], notNamed: ["photos"]);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "the change journal is NTFS's")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void ChangedSince_AFolderMade_NamesItAndTheFolderHoldingIt()
    {
        Make("docs");
        var journal = new WindowsChangeJournal();
        var since = PositionOf(journal);

        Make("docs/quarterly");

        AssertNamed(Changed(journal, since), named: ["docs", "docs/quarterly"], notNamed: []);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "the change journal is NTFS's")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void ChangedSince_AFolderMoved_NamesWhereItWasAndWhereItIs()
    {
        Make("docs/old");
        Make("photos");
        var journal = new WindowsChangeJournal();
        var since = PositionOf(journal);

        Directory.Move(Path.Combine(_root, "docs", "old"), Path.Combine(_root, "photos", "old"));

        AssertNamed(Changed(journal, since), named: ["docs", "photos", "photos/old"], notNamed: ["docs/old"]);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "the change journal is NTFS's")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void ChangedSince_AFolderDeletedWithWhatItHeld_NamesTheFolderThatHeldIt()
    {
        Make("docs/gone/deeper");
        Make("photos");
        File.WriteAllText(Path.Combine(_root, "docs", "gone", "deeper", "a.txt"), "soon gone");
        var journal = new WindowsChangeJournal();
        var since = PositionOf(journal);

        Directory.Delete(Path.Combine(_root, "docs", "gone"), recursive: true);

        // The records from inside it name folders no longer there, which is
        // no doubt: the journal says they went.
        AssertNamed(Changed(journal, since), named: ["docs"], notNamed: ["photos", "docs/gone", "docs/gone/deeper"]);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "the change journal is NTFS's")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void ChangedSince_AFoldersPermissionsChanged_NamesIt()
    {
        Make("photos");
        Make("docs");
        var journal = new WindowsChangeJournal();
        var since = PositionOf(journal);

        var photos = new DirectoryInfo(Path.Combine(_root, "photos"));
        var security = photos.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadData, AccessControlType.Allow));
        photos.SetAccessControl(security);

        AssertNamed(Changed(journal, since), named: ["photos"], notNamed: ["docs"]);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "the change journal is NTFS's")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void ChangedSince_AFileChangedThroughOneOfItsNames_NamesEveryFolderHoldingOne()
    {
        Make("docs");
        Make("photos");
        Make("music");
        var original = Path.Combine(_root, "docs", "a.txt");
        File.WriteAllText(original, "one file");
        Assert.IsTrue(
            CreateHardLink(Path.Combine(_root, "photos", "a-too.txt"), original, 0),
            $"CreateHardLinkW failed with error {Marshal.GetLastPInvokeError()}");
        var journal = new WindowsChangeJournal();
        var since = PositionOf(journal);

        File.AppendAllText(original, ", two names");

        AssertNamed(Changed(journal, since), named: ["docs", "photos"], notNamed: ["music"]);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "the change journal is NTFS's")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void ChangedSince_AFolderOutsideTheRoots_IsNotNamed()
    {
        Make("docs");
        Make("photos");
        var journal = new WindowsChangeJournal();
        var since = PositionOf(journal);

        File.WriteAllText(Path.Combine(_root, "docs", "a.txt"), "inside");
        File.WriteAllText(Path.Combine(_root, "photos", "b.txt"), "outside");

        var docs = Path.Combine(_root, "docs");
        var changed = journal.ChangedSince(since, [docs]);
        Assert.IsNotNull(changed);
        Assert.AreEqual(new ChangedFolder(docs, string.Empty), Assert.ContainsSingle(changed), "the root itself, and nothing beside it");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "the change journal is NTFS's")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void ChangedSince_ARootSpelledInAnotherCase_IsStillTheRoot()
    {
        Make("Docs");
        var journal = new WindowsChangeJournal();
        var since = PositionOf(journal);

        File.WriteAllText(Path.Combine(_root, "Docs", "a.txt"), "new");

        // Named as the folder is on the volume, which is how a walk names it.
        var root = _root.ToUpperInvariant();
        var changed = journal.ChangedSince(since, [root]);
        Assert.IsNotNull(changed);
        Assert.Contains(new ChangedFolder(root, "Docs"), changed);
        Assert.IsTrue(changed.All(folder => folder.Root == root), "every folder under the root as it was asked");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "the change journal is NTFS's")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void ChangedSince_APlaceInAnotherJournal_CannotBeAnswered()
    {
        var journal = new WindowsChangeJournal();
        var since = PositionOf(journal);

        Assert.IsNull(journal.ChangedSince(since with { JournalId = since.JournalId ^ 1 }, [_root]));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "volume paths are Windows'")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void PositionOf_APathThatIsNoFolder_IsNone()
    {
        Assert.IsNull(new WindowsChangeJournal().PositionOf(Path.Combine(_root, "not-there")));
    }

    [TestMethod]
    public void Covers_APlaceTheJournalStillHolds()
    {
        var since = new JournalPosition(@"\\?\Volume{0}\", JournalId: 7, Usn: 500);

        Assert.IsTrue(JournalCoverage.Covers(since, journalId: 7, lowestValid: 100, next: 900));
        Assert.IsTrue(JournalCoverage.Covers(since, journalId: 7, lowestValid: 500, next: 500), "the oldest record and the next");
    }

    [TestMethod]
    public void Covers_NotAPlaceInAnotherJournal_OrOneTheJournalNoLongerHolds()
    {
        var since = new JournalPosition(@"\\?\Volume{0}\", JournalId: 7, Usn: 500);

        Assert.IsFalse(JournalCoverage.Covers(since, journalId: 8, lowestValid: 100, next: 900), "another journal");
        Assert.IsFalse(JournalCoverage.Covers(since, journalId: 7, lowestValid: 501, next: 900), "its records since then are gone");
        Assert.IsFalse(JournalCoverage.Covers(since, journalId: 7, lowestValid: 100, next: 499), "a place the journal has not reached");
    }

    /// <summary>
    /// A place after which nothing in the folder has changed for a moment.
    /// A handle another program still holds on what a test made, as a
    /// scanner's, closes later, and the journal records that close then.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private JournalPosition PositionOf(WindowsChangeJournal journal)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var since = journal.PositionOf(_root)
                ?? throw new AssertInconclusiveException($"no change journal on the volume holding {_root}");
            Thread.Sleep(100);
            if (journal.ChangedSince(since, [_root]) is { Count: 0 })
            {
                return since;
            }
        }

        throw new AssertFailedException($"the folder {_root} never went quiet");
    }

    private static void AssertNamed(string[] changed, string[] named, string[] notNamed)
    {
        foreach (var folder in named)
        {
            Assert.Contains(folder, changed, $"{folder} is named among {string.Join(", ", changed)}");
        }

        // A folder above one named may be named too, which costs nothing:
        // a run walks every folder above one it was told of anyway.
        foreach (var folder in notNamed)
        {
            Assert.DoesNotContain(folder, changed, $"{folder} is not named among {string.Join(", ", changed)}");
        }
    }

    [SupportedOSPlatform("windows")]
    private string[] Changed(WindowsChangeJournal journal, JournalPosition since)
    {
        var changed = journal.ChangedSince(since, [_root]);
        Assert.IsNotNull(changed, "the journal could say");
        Assert.IsTrue(changed.All(folder => folder.Root == _root), "every folder under the root it was asked of");
        return [.. changed.Select(folder => folder.Path)];
    }

    private void Make(string path) => Directory.CreateDirectory(Path.Combine(_root, path));

    [LibraryImport("kernel32", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
