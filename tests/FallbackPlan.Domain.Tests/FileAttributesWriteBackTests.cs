using System.Runtime.Versioning;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// A restore writes back the Windows attribute bits captured with a file or
/// a folder, where the platform keeps them (FR-RST-004; ADR-0175): read-only,
/// hidden, system, archive and not-content-indexed, set as captured and
/// cleared where not, after every other write, since a read-only file refuses
/// its own streams. A bit no restore sets, as compressed or encrypted, leaves
/// the attributes not applied and is named. A file a restore replaces whole
/// is replaced though it is read-only.
/// </summary>
[TestClass]
public sealed class FileAttributesWriteBackTests : IDisposable
{
    private const uint ReadOnly = 0x1;
    private const uint Hidden = 0x2;
    private const uint Archive = 0x20;
    private const uint Compressed = 0x800;
    private const uint Encrypted = 0x4000;
    private const uint Directory = 0x10;

    private static readonly DateTime Modified = new(2024, 8, 1, 17, 5, 42, 456, DateTimeKind.Utc);

    private readonly string _root =
        System.IO.Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-file-attributes-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public void ThisPlatform_WritesAttributeBits_OnlyOnWindows() =>
        Assert.AreEqual(OperatingSystem.IsWindows(), MetadataTarget.ForLocalPlatform().SupportsFileAttributes);

    [TestMethod]
    [DataRow(Archive)]
    [DataRow(ReadOnly | Hidden | 0x4 | Archive | 0x2000)]
    [DataRow(Directory | Hidden)]
    [DataRow(0x80u)]
    [DataRow(Archive | 0x200 | 0x400)]
    public void UnsetFileAttributes_OfBitsARestoreSetsOrAnEntryIs_IsNone(uint captured) =>
        Assert.IsEmpty(MetadataRules.UnsetFileAttributes(captured));

    [TestMethod]
    public void UnsetFileAttributes_NamesEachBitNoRestoreSets()
    {
        CollectionAssert.AreEqual(
            new[] { "Compressed", "Encrypted" },
            MetadataRules.UnsetFileAttributes(Archive | Compressed | Encrypted).ToArray());
        CollectionAssert.AreEqual(new[] { "Temporary", "Offline" }, MetadataRules.UnsetFileAttributes(0x100 | 0x1000).ToArray());
        Assert.AreEqual("0x00800000", Assert.ContainsSingle(MetadataRules.UnsetFileAttributes(0x800000)));
    }

    [TestMethod]
    [DataRow(Archive | 0x4, ReadOnly | Hidden, ReadOnly | Hidden)]
    [DataRow(Directory, Directory | Hidden, Directory | Hidden)]
    [DataRow(Directory | Archive, Directory, Directory)]
    [DataRow(Archive | Compressed, Archive | Hidden, Archive | Hidden | Compressed)]
    [DataRow(0x80u, ReadOnly, ReadOnly)]
    [DataRow(Archive, 0x80u, 0x80u)]
    [DataRow(ReadOnly | Hidden | 0x4 | Archive | 0x2000, 0x80u, 0x80u)]
    public void FileAttributesToSet_KeepsTheStandingBitsNoRestoreSets_AndIsNormalWhereNoneIsLeft(uint standing, uint captured, uint expected) =>
        Assert.AreEqual(expected, MetadataRules.FileAttributesToSet(standing, captured));

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's attribute bits")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Write_OnWindows_SetsTheBitsCaptured_ClearsTheRest_AndLeavesTheTimeCaptured()
    {
        var file = Landed("flags.bin");
        File.SetAttributes(file, FileAttributes.Archive | FileAttributes.System);

        var written = Writer().Write(file, new EntryMetadata
        {
            ModifiedAt = (ulong)new DateTimeOffset(Modified).ToUnixTimeMilliseconds(),
            FileAttributes = ReadOnly | Hidden,
        });

        Assert.AreEqual(FileAttributes.ReadOnly | FileAttributes.Hidden, File.GetAttributes(file));
        Assert.IsTrue(written.Applied.HasFlag(CapturedMetadata.FileAttributes));
        Assert.AreEqual(Modified, File.GetLastWriteTimeUtc(file));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's attribute bits")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Write_OnWindows_AFileCapturedWithNoBitsSet_ComesBackWithNone()
    {
        var file = Landed("plain.bin");
        File.SetAttributes(file, FileAttributes.Archive);

        var written = Writer().Write(file, new EntryMetadata { FileAttributes = 0x80 });

        Assert.AreEqual(FileAttributes.Normal, File.GetAttributes(file));
        Assert.IsTrue(written.Applied.HasFlag(CapturedMetadata.FileAttributes));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a folder's attribute bits")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Write_OnWindows_GivesAFolderItsBits()
    {
        var folder = System.IO.Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;

        var written = Writer().Write(folder, new EntryMetadata { FileAttributes = Directory | Hidden }, EntryKind.DirectoryPlaceholder);

        Assert.AreEqual(FileAttributes.Directory | FileAttributes.Hidden, File.GetAttributes(folder));
        Assert.IsTrue(written.Applied.HasFlag(CapturedMetadata.FileAttributes));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's attribute bits")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Write_OnWindows_ABitNoRestoreSets_LeavesTheAttributesNotApplied_AndIsNamed()
    {
        var file = Landed("compressed.bin");

        var written = Writer().Write(file, new EntryMetadata { FileAttributes = Hidden | Compressed });

        Assert.IsTrue(File.GetAttributes(file).HasFlag(FileAttributes.Hidden), "the bits a restore sets still land");
        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.FileAttributes));
        Assert.IsNotNull(written.Detail);
        Assert.Contains("Compressed", written.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "POSIX keeps no attribute bits, and .NET maps read-only onto the permissions there")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public void Write_OnPosix_ATargetSaidToKeepBits_WritesNone()
    {
        var file = Landed("posix.bin");
        var before = File.GetUnixFileMode(file);

        var written = Writer().Write(file, new EntryMetadata { FileAttributes = ReadOnly | Hidden });

        Assert.AreEqual(before, File.GetUnixFileMode(file));
        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.FileAttributes));
    }

    [TestMethod]
    public void MoveOver_ReplacesAFileThatIsThere_WithTheSpool()
    {
        var destination = Landed("replaced.bin");
        var spool = Path.Combine(_root, "spool.tmp");
        File.WriteAllText(spool, "the restored content");

        RestorePaths.MoveOver(spool, destination);

        Assert.AreEqual("the restored content", File.ReadAllText(destination));
        Assert.IsFalse(File.Exists(spool));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "Windows refuses to replace a read-only file")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void MoveOver_OnWindows_ReplacesAReadOnlyFileToo()
    {
        var destination = Landed("read-only.bin");
        File.SetAttributes(destination, FileAttributes.ReadOnly);
        var spool = Path.Combine(_root, "spool.tmp");
        File.WriteAllText(spool, "the restored content");

        RestorePaths.MoveOver(spool, destination);

        Assert.AreEqual("the restored content", File.ReadAllText(destination));
    }

    public void Dispose()
    {
        try
        {
            // Windows refuses to delete a read-only file.
            foreach (var path in OperatingSystem.IsWindows()
                ? System.IO.Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
                : [])
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
            }

            System.IO.Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    private static MetadataWriter Writer() => new(MetadataTarget.ForLocalPlatform() with { SupportsFileAttributes = true });

    private string Landed(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "landed");
        return path;
    }
}
