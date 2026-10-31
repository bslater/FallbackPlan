using FallbackPlan.Filesystem;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Filesystem.Tests;

/// <summary>
/// The local scanner names the flags a file carries that no format key keeps,
/// in a capture diagnostic (FR-MAN-003; ADR-0180; architecture 06 §3): Linux's
/// as <c>statx</c> reports them, and macOS's from <c>st_flags</c>.
/// </summary>
public sealed partial class LocalScanTests
{
    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux keeps a file's inode flags")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task Scan_OnLinux_AFileCarryingTheNoDumpFlag_NamesIt_AndAPlainFileNamesNone()
    {
        DiskFlags.AddOnLinux(Write("flagged.txt"), DiskFlags.LinuxNoDump);
        Write("plain.txt");

        var leaves = (await ScanAsync()).OfType<ScanEvent.Leaf>().ToDictionary(leaf => leaf.Entry.RelativePath, leaf => leaf.Entry);

        Assert.Contains("file-flags: no-dump", leaves["flagged.txt"].Diagnostics);
        Assert.DoesNotContain(diagnostic => diagnostic.StartsWith("file-flags:", StringComparison.Ordinal), leaves["plain.txt"].Diagnostics);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux keeps a folder's inode flags")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task Scan_OnLinux_AFolderCarryingTheNoDumpFlag_NamesNone_SinceATreeCarriesNoDiagnostics()
    {
        Write("flagged/inside.txt");
        DiskFlags.AddOnLinux(Path.Combine(_root, "flagged"), DiskFlags.LinuxNoDump);

        var events = await ScanAsync();

        Assert.DoesNotContain(
            diagnostic => diagnostic.StartsWith("file-flags:", StringComparison.Ordinal),
            events.OfType<ScanEvent.EnterDirectory>().Single(enter => enter.Entry.RelativePath == "flagged").Entry.Diagnostics);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "macOS keeps a file's flags in st_flags")]
    [PlatformTrait(TestPlatforms.MacOs)]
    public async Task Scan_OnMacOs_AFileCarryingTheNoDumpAndHiddenFlags_NamesThem_AndAPlainFileNamesNone()
    {
        DiskFlags.SetOnMacOs(Write("flagged.txt"), DiskFlags.DarwinNoDump | DiskFlags.DarwinHidden);
        Write("plain.txt");

        var leaves = (await ScanAsync()).OfType<ScanEvent.Leaf>().ToDictionary(leaf => leaf.Entry.RelativePath, leaf => leaf.Entry);

        Assert.Contains("file-flags: no-dump, hidden", leaves["flagged.txt"].Diagnostics);
        Assert.DoesNotContain(diagnostic => diagnostic.StartsWith("file-flags:", StringComparison.Ordinal), leaves["plain.txt"].Diagnostics);
    }
}
