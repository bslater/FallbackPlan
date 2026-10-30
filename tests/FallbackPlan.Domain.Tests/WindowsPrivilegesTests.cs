using System.Runtime.Versioning;
using System.Security.Principal;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// A restore enables a privilege its account holds only for the one call that
/// needs it, on the thread that makes it (FR-RST-004; ADR-0176 Amendment 1):
/// under a copy of the process's token, reverted once the call returns, so
/// the process's own token and every other thread are as they were. A
/// privilege the account does not hold is not enabled, and the call is not
/// made, nor is it on a thread already impersonating, which reverting would
/// end.
/// </summary>
[TestClass]
public sealed class WindowsPrivilegesTests
{
    private const string CreateToken = "SeCreateTokenPrivilege";

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "only Windows grants an account privileges by name")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void OffWindows_NoPrivilegeIsHeld_AndNoCallIsMade()
    {
        var made = false;

        Assert.IsFalse(WindowsPrivileges.Holds(WindowsPrivileges.Restore));
        Assert.IsFalse(WindowsPrivileges.IsEnabled(WindowsPrivileges.Restore));
        Assert.IsFalse(WindowsPrivileges.RunWith(WindowsPrivileges.Restore, () => made = true));
        Assert.IsFalse(made);
    }

    [TestMethod]
    [PrivilegedPlatformCondition(TestPlatforms.Windows, "only an elevated process holds the privilege a restore gives an owner with")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void OnWindows_AnElevatedProcess_HoldsTheRestorePrivilege_AndEnablesItForTheCallAlone()
    {
        var before = WindowsPrivileges.IsEnabled(WindowsPrivileges.Restore);
        var enabledInside = false;
        var impersonatingInside = false;

        var answer = WindowsPrivileges.RunWith(WindowsPrivileges.Restore, () =>
        {
            enabledInside = WindowsPrivileges.IsEnabled(WindowsPrivileges.Restore);
            impersonatingInside = WindowsIdentity.GetCurrent(ifImpersonating: true) is not null;
            return true;
        });

        Assert.IsTrue(WindowsPrivileges.Holds(WindowsPrivileges.Restore));
        Assert.IsTrue(answer);
        Assert.IsTrue(enabledInside);
        Assert.IsTrue(impersonatingInside);
        Assert.IsNull(WindowsIdentity.GetCurrent(ifImpersonating: true));
        Assert.AreEqual(before, WindowsPrivileges.IsEnabled(WindowsPrivileges.Restore));
    }

    [TestMethod]
    [PrivilegedPlatformCondition(TestPlatforms.Windows, "only an elevated process holds the privilege a restore gives an owner with")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void OnWindows_TheCallsOwnAnswer_IsWhatComesBack()
    {
        Assert.IsFalse(WindowsPrivileges.RunWith(WindowsPrivileges.Restore, () => false));
        Assert.IsNull(WindowsIdentity.GetCurrent(ifImpersonating: true));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows grants an account privileges by name")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void OnWindows_APrivilegeTheAccountDoesNotHold_IsNotEnabled_AndTheCallIsNotMade()
    {
        // Only the system's own logon process holds the right to make tokens.
        var made = false;

        Assert.IsFalse(WindowsPrivileges.Holds(CreateToken));
        Assert.IsFalse(WindowsPrivileges.RunWith(CreateToken, () => made = true));
        Assert.IsFalse(made);
        Assert.IsFalse(WindowsPrivileges.IsEnabled(CreateToken));
        Assert.IsNull(WindowsIdentity.GetCurrent(ifImpersonating: true));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows grants an account privileges by name")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void OnWindows_ANameNoPrivilegeHas_IsNeitherHeldNorEnabled()
    {
        var made = false;

        Assert.IsFalse(WindowsPrivileges.Holds("SeNoSuchPrivilege"));
        Assert.IsFalse(WindowsPrivileges.IsEnabled("SeNoSuchPrivilege"));
        Assert.IsFalse(WindowsPrivileges.RunWith("SeNoSuchPrivilege", () => made = true));
        Assert.IsFalse(made);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows grants an account privileges by name")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void OnWindows_AThreadAlreadyImpersonating_IsLeftImpersonating_AndTheCallIsNotMade()
    {
        var made = false;
        var answer = true;
        var impersonatingAfter = false;

        using (var self = WindowsIdentity.GetCurrent())
        {
            WindowsIdentity.RunImpersonated(self.AccessToken, () =>
            {
                answer = WindowsPrivileges.RunWith(WindowsPrivileges.Restore, () => made = true);
                impersonatingAfter = WindowsIdentity.GetCurrent(ifImpersonating: true) is not null;
            });
        }

        Assert.IsFalse(answer);
        Assert.IsFalse(made);
        Assert.IsTrue(impersonatingAfter);
    }
}
