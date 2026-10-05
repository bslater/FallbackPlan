using System.Globalization;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Giving a restored file back to the account and group it was captured
/// under (FR-RST-004; architecture 06 §3, "preserve where the principal
/// resolves"). A captured owner is a name, because an id means nothing on
/// another machine, so a restore looks the name up here and gives the file
/// to whatever it names. Owner and group are written apart, so a refusal of
/// one leaves the other.
/// </summary>
/// <remarks>
/// Root, or a process holding CAP_CHOWN, may give a file to any account and
/// group. Any other process may keep a file as its own and give it a group
/// it is in, and nothing more. A refusal is an answer, never an exception:
/// a restore lists what it could not write back and goes on. The privileged
/// and unprivileged halves each run where the suite runs as that, so a
/// container proves the one and a CI runner the other.
/// </remarks>
[TestClass]
public sealed class FileOwnershipTests : IDisposable
{
    private const string NoSuchName = "fallbackplan-no-such-account";

    /// <summary>An id no account or group is given on any machine the suite runs on.</summary>
    private const uint Unclaimed = 54_321;

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-file-ownership-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public void CanSetOwnership_IsTrueOnPosixAlone() =>
        Assert.AreEqual(!OperatingSystem.IsWindows(), FileOwnership.CanSetOwnership);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX file's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void UserId_OfTheAccountRunningThisProcess_IsItsId() =>
        Assert.AreEqual(PosixAccount.UserId, FileOwnership.UserId(Environment.UserName));

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX file's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void UserId_AndGroupId_OfTheSuperuser_AreZero()
    {
        Assert.AreEqual(0u, FileOwnership.UserId("root"));

        // Group 0 is root on Linux and wheel on macOS.
        Assert.AreEqual(0u, FileOwnership.GroupId(OperatingSystem.IsMacOS() ? "wheel" : "root"));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX file's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void UserId_AndGroupId_OfANameNoAccountHas_AreNothing()
    {
        Assert.IsNull(FileOwnership.UserId(NoSuchName));
        Assert.IsNull(FileOwnership.GroupId(NoSuchName));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX file's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void TrySetOwner_ToTheAccountItself_Lands()
    {
        var path = Landed("own.bin");

        Assert.IsTrue(FileOwnership.TrySetOwner(path, PosixAccount.UserId));

        Assert.AreEqual(PosixAccount.UserId, FileOwner.Of(path).UserId);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX file's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void TrySetGroup_ToAGroupTheAccountMayGive_Lands_AndLeavesTheOwner()
    {
        var path = Landed("grouped.bin");
        var (owner, made) = FileOwner.Of(path);

        // A group other than the one the file was made with, where there is
        // one to give: any at all for a privileged process, else one the
        // account is in.
        var given = Environment.IsPrivilegedProcess
            ? Unclaimed
            : PosixAccount.Groups.FirstOrDefault(group => group != made, made);

        Assert.IsTrue(FileOwnership.TrySetGroup(path, given));

        Assert.AreEqual((owner, given), FileOwner.Of(path));
    }

    [TestMethod]
    [UnprivilegedPlatformCondition(TestPlatforms.Posix, "only an unprivileged process is refused another account")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void TrySetOwner_ToAnotherAccount_IsRefused_AndTheFileStaysItsOwn()
    {
        var path = Landed("refused.bin");

        Assert.IsFalse(FileOwnership.TrySetOwner(path, 0));

        Assert.AreEqual(PosixAccount.UserId, FileOwner.Of(path).UserId);
    }

    [TestMethod]
    [PrivilegedPlatformCondition(TestPlatforms.Posix, "only a privileged process may give a file to another account")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void TrySetOwner_ToAnotherAccount_LandsWithPrivilege()
    {
        var path = Landed("given.bin");

        Assert.IsTrue(FileOwnership.TrySetOwner(path, Unclaimed));

        Assert.AreEqual(Unclaimed, FileOwner.Of(path).UserId);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX file's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void TrySetOwnerAndGroup_OnAPathThatIsNotThere_AreFalse()
    {
        var path = Path.Combine(_root, "absent.bin");

        Assert.IsFalse(FileOwnership.TrySetOwner(path, PosixAccount.UserId));
        Assert.IsFalse(FileOwnership.TrySetGroup(path, PosixAccount.GroupId));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX file's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void TheProcessAccount_IsTheOneTheSystemReports()
    {
        Assert.AreEqual(PosixAccount.UserId, FileOwnership.EffectiveUserId);
        Assert.IsTrue(PosixAccount.Groups.SetEquals(FileOwnership.GroupIds));

        // Root may give a file away, and so may a Linux process holding
        // CAP_CHOWN, the lowest bit of its effective capabilities.
        Assert.AreEqual(Environment.IsPrivilegedProcess || HoldsChownCapability(), FileOwnership.MayGiveFilesAway);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "a Windows file's owner lives in its security descriptor")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void OnWindows_NoNameResolves_AndNoFileIsGivenAway()
    {
        var path = Landed("windows.bin");

        Assert.IsNull(FileOwnership.UserId(Environment.UserName));
        Assert.IsNull(FileOwnership.GroupId("Users"));
        Assert.IsFalse(FileOwnership.TrySetOwner(path, 0));
        Assert.IsFalse(FileOwnership.TrySetGroup(path, 0));
        Assert.IsFalse(FileOwnership.MayGiveFilesAway);
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

    private static bool HoldsChownCapability()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var effective = File.ReadLines("/proc/self/status")
            .Single(line => line.StartsWith("CapEff:", StringComparison.Ordinal))["CapEff:".Length..]
            .Trim();
        return (ulong.Parse(effective, NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 1) != 0;
    }

    private string Landed(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }
}
