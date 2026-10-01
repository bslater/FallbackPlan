using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Filesystem.Tests;

/// <summary>
/// What a stat reports for a path on Windows (FR-DEST-017; architecture 06
/// §3). One field is the volume the path sits on, which decides whether a
/// local destination shares a root's drive (ADR-0051). Another is how many
/// names a file has, which decides whether capture groups it as a hard link.
/// Both come from <c>GetFileInformationByHandle</c>, read through a managed
/// copy of <c>BY_HANDLE_FILE_INFORMATION</c>.
/// </summary>
/// <remarks>
/// That copy once declared its three <c>FILETIME</c>s as longs. C# aligns a
/// long to eight bytes, and Windows aligns a <c>FILETIME</c> to four, so every
/// field after the first was read four bytes late. The volume serial number
/// was the high half of the file's size, which is zero for every directory.
/// So every local destination on Windows shared a volume with every root, and
/// every file had as many links as the high half of its file index. The
/// layout test needs no Windows, and it pins the cause. The others check the
/// values against what Windows reports through other calls.
/// </remarks>
[TestClass]
public sealed partial class WindowsFileIdentityTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-windows-identity", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public void ByHandleFileInformation_HasTheNativeLayout()
    {
        // BY_HANDLE_FILE_INFORMATION (fileapi.h): a DWORD, three FILETIMEs
        // of two DWORDs each, then six DWORDs. Nothing in it is wider than
        // four bytes, so nothing in it is aligned to more.
        Assert.AreEqual(52, Marshal.SizeOf<ByHandleFileInformation>());
        Assert.AreEqual(28, OffsetOf(nameof(ByHandleFileInformation.VolumeSerialNumber)));
        Assert.AreEqual(40, OffsetOf(nameof(ByHandleFileInformation.NumberOfLinks)));
        Assert.AreEqual(44, OffsetOf(nameof(ByHandleFileInformation.FileIndexHigh)));
        Assert.AreEqual(48, OffsetOf(nameof(ByHandleFileInformation.FileIndexLow)));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "the volume serial number is what a Windows stat reports as the device")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void TryStat_ADirectory_ReportsTheSerialNumberOfTheVolumeItIsOn()
    {
        // A directory has no size, so the misread layout reported zero here
        // for every one, on every volume.
        Assert.IsTrue(LocalFileSystemSource.TryStat(_root, out var stat));

        Assert.AreEqual((ulong)VolumeSerialNumberOf(_root), stat.Device);
        Assert.AreNotEqual(0ul, stat.Device, "a volume serial number of zero is the misread layout, not a volume");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "link counts here come from GetFileInformationByHandle")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void TryStat_AFileWithOneName_HasOneLink_AndAHardLinkMakesTwo()
    {
        var file = Path.Combine(_root, "one.txt");
        File.WriteAllText(file, "one file, then two names");

        Assert.IsTrue(LocalFileSystemSource.TryStat(file, out var single));
        Assert.AreEqual(1u, single.LinkCount);

        var second = Path.Combine(_root, "two.txt");
        Assert.IsTrue(CreateHardLink(second, file, 0), $"CreateHardLinkW failed with error {Marshal.GetLastPInvokeError()}");

        Assert.IsTrue(LocalFileSystemSource.TryStat(file, out var first));
        Assert.IsTrue(LocalFileSystemSource.TryStat(second, out var other));
        Assert.AreEqual(2u, first.LinkCount);
        Assert.AreEqual(2u, other.LinkCount);
        Assert.AreEqual(first.FileId, other.FileId, "two names of one file must report one identity");
        Assert.AreEqual(first.Device, other.Device);
    }

    private static int OffsetOf(string field) =>
        Marshal.OffsetOf<ByHandleFileInformation>(field).ToInt32();

    [SupportedOSPlatform("windows")]
    private static unsafe uint VolumeSerialNumberOf(string path)
    {
        var volume = stackalloc char[512];
        Assert.IsTrue(GetVolumePathName(path, volume, 512), $"GetVolumePathNameW failed with error {Marshal.GetLastPInvokeError()}");

        var root = new string(volume);
        Assert.IsTrue(
            GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0),
            $"GetVolumeInformationW({root}) failed with error {Marshal.GetLastPInvokeError()}");
        return serial;
    }

    [LibraryImport("kernel32", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static unsafe partial bool GetVolumePathName(string fileName, char* volumePathName, uint bufferLength);

    [LibraryImport("kernel32", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static unsafe partial bool GetVolumeInformation(
        string rootPathName, char* volumeName, uint volumeNameSize, out uint volumeSerialNumber,
        out uint maximumComponentLength, out uint fileSystemFlags, char* fileSystemName, uint fileSystemNameSize);

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
