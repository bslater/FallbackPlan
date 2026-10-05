using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The times a restore writes back onto a file it landed (FR-RST-004;
/// architecture 06 §3). A creation time is set where the platform has a call
/// that sets one, and nowhere else. .NET's own setter writes the modification
/// time instead on Linux, and on macOS whenever a volume keeps no creation
/// times, so using it would silently replace the time a restore had just put
/// back.
/// </summary>
/// <remarks>
/// A captured time arrives from a manifest, which a restore treats as
/// untrusted input. One beyond the last instant a file can carry is answered
/// as nothing to apply, never as an exception that would end the run.
/// </remarks>
[TestClass]
public sealed class FileTimesTests : IDisposable
{
    private static readonly DateTime Created = new(2024, 7, 14, 9, 30, 15, 123, DateTimeKind.Utc);

    private static readonly DateTime Modified = new(2024, 8, 1, 17, 5, 42, 456, DateTimeKind.Utc);

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-file-times-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public void CanSetCreationTime_IsTrueOnWindowsAndMacOsAlone() =>
        Assert.AreEqual(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), FileTimes.CanSetCreationTime);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows | TestPlatforms.MacOs, "only Windows and macOS can set a file's creation time")]
    [PlatformTrait(TestPlatforms.Windows | TestPlatforms.MacOs)]
    public void TrySetCreationTime_WhereThePlatformCan_SetsIt_AndLeavesTheModificationTimeAlone()
    {
        var path = Landed("created.bin");
        File.SetLastWriteTimeUtc(path, Modified);

        Assert.IsTrue(FileTimes.TrySetCreationTime(path, Created));

        Assert.AreEqual(Created, File.GetCreationTimeUtc(path));
        Assert.AreEqual(Modified, File.GetLastWriteTimeUtc(path));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux has no call that sets a file's creation time")]
    [PlatformTrait(TestPlatforms.Linux)]
    public void TrySetCreationTime_OnLinux_SetsNothing_AndLeavesTheModificationTimeAlone()
    {
        var path = Landed("created.bin");
        File.SetLastWriteTimeUtc(path, Modified);

        Assert.IsFalse(FileTimes.TrySetCreationTime(path, Created));

        Assert.AreEqual(Modified, File.GetLastWriteTimeUtc(path));
    }

    [TestMethod]
    public void FromUnixMilliseconds_AnInstantAFileCanCarry_IsThatInstant()
    {
        Assert.AreEqual(Modified, FileTimes.FromUnixMilliseconds((ulong)new DateTimeOffset(Modified).ToUnixTimeMilliseconds()));
        Assert.AreEqual(
            new DateTime(9999, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc),
            FileTimes.FromUnixMilliseconds(253_402_300_799_999));
    }

    [TestMethod]
    public void FromUnixMilliseconds_BeyondTheLastInstantAFileCanCarry_IsNothing()
    {
        Assert.IsNull(FileTimes.FromUnixMilliseconds(253_402_300_800_000));
        Assert.IsNull(FileTimes.FromUnixMilliseconds(ulong.MaxValue));
        Assert.IsNull(FileTimes.FromUnixMilliseconds(null));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A straggling handle on a temp directory is not a test failure.
        }
    }

    private string Landed(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }
}
