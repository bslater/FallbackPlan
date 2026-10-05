using System.Runtime.InteropServices;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Who this process is on a POSIX host: its effective user and group, and
/// every group it belongs to. The oracle a restore's sense of its own
/// account is checked against, and how a test finds a group the account may
/// give a file to.
/// </summary>
public static unsafe partial class PosixAccount
{
    /// <summary>The effective user id.</summary>
    public static uint UserId => GetEffectiveUserId();

    /// <summary>The effective group id, which a new file takes on Linux.</summary>
    public static uint GroupId => GetEffectiveGroupId();

    /// <summary>Every group the process is in: its supplementary groups and its effective group.</summary>
    public static IReadOnlySet<uint> Groups
    {
        get
        {
            var count = GetGroups(0, null);
            if (count < 0)
            {
                throw new IOException($"getgroups failed with errno {Marshal.GetLastPInvokeError()}.");
            }

            var groups = new uint[count];
            fixed (uint* list = groups)
            {
                count = GetGroups(groups.Length, list);
            }

            if (count < 0)
            {
                throw new IOException($"getgroups failed with errno {Marshal.GetLastPInvokeError()}.");
            }

            return new HashSet<uint>(groups.Take(count)) { GroupId };
        }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "getegid")]
    private static partial uint GetEffectiveGroupId();

    [LibraryImport("libc", EntryPoint = "getgroups", SetLastError = true)]
    private static partial int GetGroups(int size, uint* list);
}
