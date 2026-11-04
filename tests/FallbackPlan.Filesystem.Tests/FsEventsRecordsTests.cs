using FallbackPlan.Filesystem.Local;

namespace FallbackPlan.Filesystem.Tests;

/// <summary>
/// Which folders a replay of a macOS volume's event history names, and what
/// in it is doubt (ADR-0191, FR-SNP-011). A file or a link names the folder
/// holding it; anything else names itself; a path is named under every root
/// it lies below, as it is spelled on disk, and under none it does not. The
/// mark's folder is never named. A history that lost track, a changed file
/// with another name, and a path that cannot be spelled as on disk, or is
/// spelled outside every root, are doubt. A place's history is the volume's
/// identity, and its first eight bytes are the journal's.
/// </summary>
/// <remarks>
/// The events are built here, so this runs on every platform: what a replay
/// names is a rule, not a platform call. <c>MacChangeJournalTests</c> holds
/// the history itself where macOS runs.
/// </remarks>
[TestClass]
public sealed class FsEventsRecordsTests
{
    private const uint ItemCreated = 0x100;
    private const uint ItemRemoved = 0x200;
    private const uint ItemInodeMetaMod = 0x400;
    private const uint ItemModified = 0x1000;
    private const uint ItemIsFile = 0x10000;
    private const uint ItemIsDir = 0x20000;
    private const uint ItemIsSymlink = 0x40000;
    private const uint ItemIsHardlink = 0x100000;

    private const string Root = "/var/folders/t/set";
    private const string Real = "/private/var/folders/t/set";
    private const string Marks = "/private/var/folders/t/marks";

    [TestMethod]
    public void AFileChanged_NamesTheFolderHoldingIt() =>
        Assert.AreEqual(new ChangedFolder(Root, "docs"), Assert.ContainsSingle(Named((Real + "/docs/a.txt", ItemIsFile | ItemModified))));

    [TestMethod]
    public void ALinkChanged_NamesTheFolderHoldingIt() =>
        Assert.AreEqual(new ChangedFolder(Root, "docs"), Assert.ContainsSingle(Named((Real + "/docs/to-photos", ItemIsSymlink | ItemCreated))));

    [TestMethod]
    public void AFileInTheRoot_NamesTheRoot() =>
        Assert.AreEqual(new ChangedFolder(Root, string.Empty), Assert.ContainsSingle(Named((Real + "/a.txt", ItemIsFile | ItemModified))));

    [TestMethod]
    public void AFolderChanged_NamesItself() =>
        Assert.AreEqual(new ChangedFolder(Root, "docs/quarterly"), Assert.ContainsSingle(Named((Real + "/docs/quarterly", ItemIsDir | ItemCreated))));

    [TestMethod]
    public void TheRootChanged_NamesTheRoot() =>
        Assert.AreEqual(new ChangedFolder(Root, string.Empty), Assert.ContainsSingle(Named((Real, ItemIsDir | ItemInodeMetaMod))));

    [TestMethod]
    public void AnItemOfNoKnownKind_NamesItself() =>
        Assert.AreEqual(new ChangedFolder(Root, "docs/gone"), Assert.ContainsSingle(Named((Real + "/docs/gone", ItemRemoved))));

    [TestMethod]
    public void APathEndingInASlash_IsTheFolderBeforeIt() =>
        Assert.AreEqual(new ChangedFolder(Root, "docs"), Assert.ContainsSingle(Named((Real + "/docs/", ItemIsDir | ItemCreated))));

    [TestMethod]
    public void AChangeNamedTwice_IsNamedOnce() =>
        Assert.AreEqual(
            new ChangedFolder(Root, "docs"),
            Assert.ContainsSingle(Named((Real + "/docs/a.txt", ItemIsFile | ItemModified), (Real + "/docs/b.txt", ItemIsFile | ItemCreated))));

    [TestMethod]
    public void APathOutsideTheRoots_NamesNothing() =>
        Assert.IsEmpty(Named(
            ("/private/var/folders/t/other/a.txt", ItemIsFile | ItemModified),
            (Real + "-old/b.txt", ItemIsFile | ItemModified),
            ("/private/var/folders/t", ItemIsDir | ItemInodeMetaMod)));

    [TestMethod]
    public void APathBelowTwoRoots_IsNamedUnderEach()
    {
        var named = FsEventsRecords.Named(
            [(Real + "/docs/inner/a.txt", ItemIsFile | ItemModified)],
            [(Root, Real), (Root + "/docs", Real + "/docs")],
            Marks,
            static _ => false,
            static path => path,
            out var doubt);

        Assert.IsNull(doubt);
        CollectionAssert.AreEquivalent(
            new[] { new ChangedFolder(Root, "docs/inner"), new ChangedFolder(Root + "/docs", "inner") },
            named!.ToArray());
    }

    [TestMethod]
    public void APathSpelledInAnotherCase_IsNamedAsItIsOnDisk()
    {
        var named = FsEventsRecords.Named(
            [("/PRIVATE/var/folders/t/SET/Docs/a.txt", ItemIsFile | ItemModified)],
            [(Root, Real)],
            Marks,
            static _ => false,
            static path => path == "/PRIVATE/var/folders/t/SET/Docs" ? Real + "/docs" : null,
            out var doubt);

        Assert.IsNull(doubt);
        Assert.AreEqual(new ChangedFolder(Root, "docs"), Assert.ContainsSingle(named!));
    }

    [TestMethod]
    public void APathThatCannotBeSpelledAsOnDisk_IsDoubt()
    {
        Assert.IsNull(FsEventsRecords.Named(
            [(Real + "/docs/a.txt", ItemIsFile | ItemModified)], [(Root, Real)], Marks, static _ => false, static _ => null, out var doubt));
        Assert.Contains("could not be spelled", doubt!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void APathSpelledOutsideEveryRoot_IsDoubt()
    {
        Assert.IsNull(FsEventsRecords.Named(
            [(Real + "/docs/a.txt", ItemIsFile | ItemModified)],
            [(Root, Real)],
            Marks,
            static _ => false,
            static _ => "/private/var/folders/t/elsewhere",
            out var doubt));
        Assert.Contains("outside", doubt!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheMarksFolder_IsNeverNamedNorSpelled()
    {
        var marks = Real + "/.marks";
        var named = FsEventsRecords.Named(
            [(marks + "/0123", ItemIsFile | ItemCreated), (marks, ItemIsDir | ItemCreated)],
            [(Root, Real)],
            marks,
            static _ => throw new AssertFailedException("a mark is not asked for its names"),
            static _ => throw new AssertFailedException("a mark is not spelled"),
            out var doubt);

        Assert.IsNull(doubt);
        Assert.IsEmpty(named!);
    }

    [TestMethod]
    [DataRow(0x01u, DisplayName = "the folders must be scanned again")]
    [DataRow(0x02u, DisplayName = "this process dropped events")]
    [DataRow(0x04u, DisplayName = "the kernel dropped events")]
    [DataRow(0x08u, DisplayName = "the numbers wrapped")]
    [DataRow(0x20u, DisplayName = "a root moved")]
    [DataRow(0x40u, DisplayName = "a volume was mounted")]
    [DataRow(0x80u, DisplayName = "a volume was unmounted")]
    public void AHistoryThatLostTrack_IsDoubt(uint flag)
    {
        Assert.IsNull(FsEventsRecords.Named(
            [(Real + "/docs/a.txt", ItemIsFile | ItemModified), (Real + "/docs", flag)],
            [(Root, Real)],
            Marks,
            static _ => false,
            static path => path,
            out var doubt));
        Assert.Contains("lost track", doubt!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void AFileTheSystemMarksAsHavingAnotherName_IsDoubt()
    {
        Assert.IsNull(FsEventsRecords.Named(
            [(Real + "/docs/a.txt", ItemIsFile | ItemIsHardlink | ItemModified)],
            [(Root, Real)],
            Marks,
            static _ => false,
            static path => path,
            out var doubt));
        Assert.Contains("another name", doubt!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void AFileWithAnotherNameWhenAsked_IsDoubt()
    {
        Assert.IsNull(FsEventsRecords.Named(
            [(Real + "/docs/a.txt", ItemIsFile | ItemModified)],
            [(Root, Real)],
            Marks,
            static path => path == Real + "/docs/a.txt",
            static path => path,
            out var doubt));
        Assert.Contains("another name", doubt!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void AFolder_IsNotAskedForOtherNames()
    {
        var named = FsEventsRecords.Named(
            [(Real + "/docs", ItemIsDir | ItemInodeMetaMod)],
            [(Root, Real)],
            Marks,
            static _ => throw new AssertFailedException("a folder is not asked for its names"),
            static path => path,
            out var doubt);

        Assert.IsNull(doubt);
        Assert.AreEqual(new ChangedFolder(Root, "docs"), Assert.ContainsSingle(named!));
    }

    [TestMethod]
    public void AVolumesHistory_IsItsIdentity_AndItsFirstEightBytesTheJournals()
    {
        var (volume, id) = FsEventsRecords.History("0A1B2C3D-4E5F-6071-8293-A4B5C6D7E8F9");

        Assert.AreEqual("fsevents:0A1B2C3D-4E5F-6071-8293-A4B5C6D7E8F9", volume);
        Assert.AreEqual(0x0A1B2C3D4E5F6071UL, id);
    }

    private static IReadOnlyList<ChangedFolder> Named(params (string Path, uint Flags)[] events)
    {
        var named = FsEventsRecords.Named(events, [(Root, Real)], Marks, static _ => false, static path => path, out var doubt);
        Assert.IsNotNull(named, $"the replay could not say: {doubt}");
        return named;
    }
}
