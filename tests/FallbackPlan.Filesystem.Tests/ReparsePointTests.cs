using FallbackPlan.Filesystem.Local;

namespace FallbackPlan.Filesystem.Tests;

/// <summary>
/// Which Windows reparse points capture reads as links (FR-MAN-003;
/// ADR-0183): a symbolic link and a mount point, a junction among them,
/// which name another path, and one whose tag could not be read, which is
/// never followed. Any other reparse point is a file or a folder whose
/// content a filter keeps, as a deduplicated or a compressed file is, and
/// is read as one.
/// </summary>
[TestClass]
public sealed class ReparsePointTests
{
    private const uint Link = 0xA000;
    private const uint Folder = 0x4000;
    private const uint File = 0x8000;

    private const uint WofTag = 0x8000_0017;
    private const uint DedupTag = 0x8000_0013;
    private const uint CloudTag = 0x9000_601A;

    [TestMethod]
    [DataRow(FileAttributes.ReparsePoint | FileAttributes.Archive, ReparsePoints.SymbolicLinkTag, Link)]
    [DataRow(FileAttributes.ReparsePoint | FileAttributes.Directory, ReparsePoints.SymbolicLinkTag, Link)]
    [DataRow(FileAttributes.ReparsePoint | FileAttributes.Directory, ReparsePoints.MountPointTag, Link)]
    public void ModeOf_ASymbolicLinkOrAMountPoint_IsALink(FileAttributes attributes, uint tag, uint mode) =>
        Assert.AreEqual(mode, ReparsePoints.ModeOf(attributes, tag));

    [TestMethod]
    [DataRow(FileAttributes.ReparsePoint | FileAttributes.Archive, WofTag, File)]
    [DataRow(FileAttributes.ReparsePoint | FileAttributes.SparseFile | FileAttributes.Archive, DedupTag, File)]
    [DataRow(FileAttributes.ReparsePoint | FileAttributes.Directory, CloudTag, Folder)]
    public void ModeOf_AReparsePointAFilterKeeps_IsTheFileOrTheFolderItIs(FileAttributes attributes, uint tag, uint mode) =>
        Assert.AreEqual(mode, ReparsePoints.ModeOf(attributes, tag));

    [TestMethod]
    [DataRow(FileAttributes.ReparsePoint | FileAttributes.Archive)]
    [DataRow(FileAttributes.ReparsePoint | FileAttributes.Directory)]
    public void ModeOf_AReparsePointWhoseTagWasNotRead_IsALink_SoItIsNeverFollowed(FileAttributes attributes) =>
        Assert.AreEqual(Link, ReparsePoints.ModeOf(attributes, tag: null));

    [TestMethod]
    [DataRow(FileAttributes.Archive, File)]
    [DataRow(FileAttributes.Normal, File)]
    [DataRow(FileAttributes.Directory, Folder)]
    public void ModeOf_AnEntryThatIsNoReparsePoint_IsWhatItsDirectoryBitSays(FileAttributes attributes, uint mode)
    {
        Assert.AreEqual(mode, ReparsePoints.ModeOf(attributes, tag: null));
        Assert.AreEqual(mode, ReparsePoints.ModeOf(attributes, ReparsePoints.SymbolicLinkTag), "a tag means nothing without the bit");
    }
}
