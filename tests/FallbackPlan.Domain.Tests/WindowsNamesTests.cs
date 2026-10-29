using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Which names Windows keeps as they are written (FR-RST-003, FR-RST-005,
/// ADR-0170). A repository's names are captured as their source wrote them,
/// and a name another platform wrote can be one Windows refuses or alters:
/// a name it reserves for a device, a character it refuses, a trailing
/// space or period it strips, or a component longer than it allows.
/// The rule is judged on any platform, so a plan made anywhere can say what
/// a Windows target would not keep; a restore that runs on Windows refuses
/// such a name rather than letting Windows write it somewhere else.
/// </summary>
[TestClass]
public sealed class WindowsNamesTests : IDisposable
{
    private readonly string _work = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "fbp-windows-names", Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    [DataRow("Readme.TXT")]
    [DataRow("My Docs")]
    [DataRow("a b.txt")]
    [DataRow(".hidden")]
    [DataRow("CONSOLE")]
    [DataRow("nul1.txt")]
    [DataRow("COM10")]
    [DataRow("über straße.txt")]
    public void IsWindowsName_ANameWindowsKeepsAsWritten_IsOne(string name)
    {
        Assert.IsTrue(RestorePaths.IsWindowsName(name, out var why), why);
        Assert.AreEqual(string.Empty, why);
    }

    [TestMethod]
    [DataRow("CON", "device")]
    [DataRow("con.txt", "device")]
    [DataRow("Nul.tar.gz", "device")]
    [DataRow("aux", "device")]
    [DataRow("PRN.log", "device")]
    [DataRow("COM1", "device")]
    [DataRow("lpt9.doc", "device")]
    [DataRow("what?.txt", "character")]
    [DataRow("a*b", "character")]
    [DataRow("<angle>", "character")]
    [DataRow("pipe|end", "character")]
    [DataRow("say \"hi\"", "character")]
    [DataRow("tab\tname", "character")]
    [DataRow("drive:name", "character")]
    [DataRow("notes.", "strips")]
    [DataRow("notes ", "strips")]
    [DataRow("a.b.", "strips")]
    public void IsWindowsName_ANameWindowsRefusesOrAlters_IsNotOne_AndSaysWhy(string name, string why)
    {
        Assert.IsFalse(RestorePaths.IsWindowsName(name, out var reason));
        Assert.Contains(why, reason, StringComparison.Ordinal);
    }

    [TestMethod]
    public void IsWindowsName_AComponentOfMoreThan255Units_IsNotOne()
    {
        // Counted in UTF-16 units, which is what Windows counts: 255 of them
        // is the most a component may hold, however few characters they are.
        Assert.IsTrue(RestorePaths.IsWindowsName(new string('a', 255), out _));
        Assert.IsFalse(RestorePaths.IsWindowsName(new string('a', 256), out var why));
        Assert.Contains("255", why, StringComparison.Ordinal);
        Assert.IsFalse(RestorePaths.IsWindowsName(string.Concat(Enumerable.Repeat("😀", 128)), out _));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows alters these names")]
    [DataRow("data/CON")]
    [DataRow("data/notes.")]
    [DataRow("data/notes ")]
    [DataRow("data/what?.txt")]
    public void TryResolve_OnWindows_ANameWindowsWouldNotKeep_IsRefused(string path)
    {
        Assert.IsFalse(RestorePaths.TryResolve(_work, path, out _, out var reason));
        Assert.Contains("Windows", reason, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "these names are only Windows' to refuse")]
    [DataRow("data/CON")]
    [DataRow("data/notes.")]
    [DataRow("data/notes ")]
    [DataRow("data/what?.txt")]
    public void TryResolve_OnPosix_ANameOnlyWindowsRefuses_IsTaken(string path)
    {
        Assert.IsTrue(RestorePaths.TryResolve(_work, path, out var destination, out var reason), reason);
        Assert.AreEqual(Path.Combine(_work, "data", path["data/".Length..]), destination);
    }
}
