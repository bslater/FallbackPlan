using FallbackPlan.Domain;
using FallbackPlan.Filesystem.Local;

namespace FallbackPlan.Filesystem.Tests;

/// <summary>
/// Which Windows reparse points capture reads as links (FR-MAN-003;
/// ADR-0183): a symbolic link and a mount point, a junction among them,
/// which name another path, and one whose tag could not be read, which is
/// never followed. Any other reparse point is a file or a folder whose
/// content a filter keeps, as a deduplicated file is, and
/// is read as one. A mount point that names a folder by its drive's path,
/// as a junction does, is captured with that form; a symbolic link, and a
/// mount point that names a volume or nothing, with none (ADR-0183
/// Amendment 1).
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

    [TestMethod]
    [DataRow(@"C:\Users\Shared\Projects")]
    [DataRow(@"d:\")]
    public void FormOf_AMountPointNamingAFolderByItsDrivesPath_IsAMountPoint(string target) =>
        Assert.AreEqual(LinkForm.MountPoint, ReparsePoints.FormOf(ReparsePoints.MountPointTag, target));

    [TestMethod]
    [DataRow(@"Volume{0b3c7a52-1f6e-4c1d-9a2b-6c8e2f4d7a10}\")]
    [DataRow("")]
    [DataRow(@"Projects\Shared")]
    [DataRow(@"\\server\share\Projects")]
    [DataRow(@"C:Projects")]
    [DataRow(@"1:\Projects")]
    public void FormOf_AMountPointThatNamesNoFolderByADrivesPath_HasNoForm(string target) =>
        Assert.AreEqual(LinkForm.SymbolicLink, ReparsePoints.FormOf(ReparsePoints.MountPointTag, target));

    [TestMethod]
    public void FormOf_ASymbolicLink_AnUnreadTag_OrNoTarget_HasNoForm()
    {
        Assert.AreEqual(LinkForm.SymbolicLink, ReparsePoints.FormOf(ReparsePoints.SymbolicLinkTag, @"C:\Users\Shared"));
        Assert.AreEqual(LinkForm.SymbolicLink, ReparsePoints.FormOf(tag: null, @"C:\Users\Shared"));
        Assert.AreEqual(LinkForm.SymbolicLink, ReparsePoints.FormOf(DedupTag, @"C:\Users\Shared"));
        Assert.AreEqual(LinkForm.SymbolicLink, ReparsePoints.FormOf(ReparsePoints.MountPointTag, target: null));
    }
}
