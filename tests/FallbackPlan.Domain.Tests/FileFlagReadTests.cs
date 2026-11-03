using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// A restore reads the flags of a file already where it would write, so its
/// plan can name one a flag keeps where it stands (FR-RST-003; ADR-0180
/// Amendment 6). Linux's are its inode flags, which a link cannot be opened
/// for, and macOS's its <c>st_flags</c>, a link's its own. Where nothing
/// stands, or the platform keeps no flags, there are none.
/// </summary>
[TestClass]
public sealed class FileFlagReadTests
{
    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux keeps a file's and a folder's inode flags")]
    [PlatformTrait(TestPlatforms.Linux)]
    public void OnLinux_AFilesFlags_AreReadAsTheyStand_AndNoneThroughALink()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-flag-reads", Guid.NewGuid().ToString("n"))).FullName;
        try
        {
            var file = Path.Combine(root, "a.bin");
            File.WriteAllBytes(file, [1, 2, 3]);
            var link = Path.Combine(root, "l");
            File.CreateSymbolicLink(link, file);
            Assert.AreEqual(FileFlagBits.None, FileFlags.OfThisPlatform.Read(file));

            // The owner may set no-dump; immutable and append-only only a
            // process holding CAP_LINUX_IMMUTABLE.
            DiskFlags.AddOnLinux(file, DiskFlags.LinuxNoDump);
            var expected = FileFlagBits.NoDump;
            if (FileFlags.HoldsImmutableCapability)
            {
                DiskFlags.AddOnLinux(file, DiskFlags.LinuxImmutable | DiskFlags.LinuxAppendOnly);
                expected |= FileFlagBits.Immutable | FileFlagBits.AppendOnly;
            }

            Assert.AreEqual(expected, FileFlags.OfThisPlatform.Read(file));
            Assert.AreEqual(FileFlagBits.None, FileFlags.OfThisPlatform.Read(link), "a link cannot be opened without following it");
        }
        finally
        {
            DiskFlags.ClearUnder(root);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "macOS keeps a file's flags in st_flags")]
    [PlatformTrait(TestPlatforms.MacOs)]
    public void OnMacOs_AFilesFlagsAndALinksOwn_AreReadAsTheyStand()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-flag-reads", Guid.NewGuid().ToString("n"))).FullName;
        try
        {
            var file = Path.Combine(root, "a.bin");
            File.WriteAllBytes(file, [1, 2, 3]);
            var link = Path.Combine(root, "l");
            File.CreateSymbolicLink(link, file);

            DiskFlags.SetOnMacOs(file, DiskFlags.DarwinImmutable | DiskFlags.DarwinNoDump);
            Assert.AreEqual(FileFlagBits.Hidden, FileFlags.OfThisPlatform.Apply(link, FileFlagBits.Hidden, EntryKind.Symlink));

            Assert.AreEqual(FileFlagBits.Immutable | FileFlagBits.NoDump, FileFlags.OfThisPlatform.Read(file));
            Assert.AreEqual(FileFlagBits.Hidden, FileFlags.OfThisPlatform.Read(link), "the link's own, not what it points at");
        }
        finally
        {
            DiskFlags.ClearUnder(root);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void WhereNothingStands_ThereAreNone()
    {
        var missing = Path.Combine(Path.GetTempPath(), "fbp-flag-reads", Guid.NewGuid().ToString("n"));

        Assert.AreEqual(FileFlagBits.None, FileFlags.OfThisPlatform.Read(missing));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "Windows keeps none of these flags")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void OnWindows_AFileCarriesNone()
    {
        var file = Path.Combine(Path.GetTempPath(), "fbp-flag-reads-" + Guid.NewGuid().ToString("n"));
        File.WriteAllBytes(file, [1]);
        try
        {
            File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden);

            Assert.AreEqual(FileFlagBits.None, FileFlags.OfThisPlatform.Read(file));
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
    }
}
