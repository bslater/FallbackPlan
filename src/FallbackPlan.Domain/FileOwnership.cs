using Bodu;
using System.Globalization;
using System.Runtime.InteropServices;

namespace FallbackPlan.Domain;

/// <summary>
/// The owner and group a restore gives back to a file it landed
/// (specification 06 §4.1 keys 5 and 6; FR-RST-004; ADR-0085).
/// </summary>
/// <remarks>
/// <para>
/// A captured owner is a name, because an id means nothing on another
/// machine. A restore looks the name up on the machine it writes to and gives
/// the file to whatever account the name has there. A name no account has
/// resolves to nothing, and is not applied.
/// </para>
/// <para>
/// Owner and group are written apart, each with <c>lchown</c>, which never
/// follows a link, so a refusal of one leaves the other. Root, or a Linux
/// process holding CAP_CHOWN, may give a file to any account and group. Any
/// other process may keep a file as its own and give it a group it is in. A
/// refusal is answered as not set. Windows keeps a file's owner in its
/// security descriptor, so nothing here applies there.
/// </para>
/// <para>
/// Sits beside <see cref="FileTimes"/> for the reason <see cref="SparseFile"/>
/// gives: the engine's restore and the standalone recovery tool both write
/// restored files, and Domain is the one assembly both may reach.
/// </para>
/// </remarks>
public static unsafe partial class FileOwnership
{
    /// <summary>The id <c>lchown</c> takes as "leave this half as it is".</summary>
    private const uint Unchanged = uint.MaxValue;

    /// <summary>What a lookup answers when its buffer is too small for the entry, on Linux and macOS alike.</summary>
    private const int RangeError = 34;

    /// <summary>The bit of the effective capability set that is CAP_CHOWN.</summary>
    private const ulong ChownCapability = 1;

    /// <summary>Room for a <c>struct passwd</c> or <c>struct group</c> on every platform here; the largest, macOS's passwd, takes 72 bytes.</summary>
    private const int EntryWords = 16;

    /// <summary>The largest buffer a lookup grows to before giving the name up as unresolvable.</summary>
    private const int MaxBufferBytes = 1 << 20;

    /// <summary>Whether a file's owner and group are accounts the platform names: every POSIX platform, and not Windows.</summary>
    public static bool CanSetOwnership { get; } = !OperatingSystem.IsWindows();

    /// <summary>The effective user id of this process; 0 on Windows, where it means nothing here.</summary>
    public static uint EffectiveUserId => CanSetOwnership ? GetEffectiveUserId() : 0;

    /// <summary>Every group this process is in, its effective group among them; none on Windows.</summary>
    public static IReadOnlySet<uint> GroupIds
    {
        get
        {
            if (!CanSetOwnership)
            {
                return new HashSet<uint>();
            }

            var groups = new HashSet<uint> { GetEffectiveGroupId() };
            var count = GetGroups(0, null);
            if (count > 0)
            {
                var listed = new uint[count];
                fixed (uint* list = listed)
                {
                    count = GetGroups(listed.Length, list);
                }

                groups.UnionWith(listed.Take(Math.Max(count, 0)));
            }

            return groups;
        }
    }

    /// <summary>
    /// Whether this process may give a file to any account and group: root,
    /// or a Linux process holding CAP_CHOWN. Never on Windows.
    /// </summary>
    public static bool MayGiveFilesAway =>
        CanSetOwnership && (Environment.IsPrivilegedProcess || (OperatingSystem.IsLinux() && HoldsChownCapability()));

    /// <summary>The id of the account named <paramref name="name"/> here, or null when there is none.</summary>
    /// <param name="name">A captured owner's name.</param>
    public static uint? UserId(string name)
    {
        ThrowHelper.ThrowIfNull(name);
        return CanSetOwnership ? Lookup(name, group: false) : null;
    }

    /// <summary>The id of the group named <paramref name="name"/> here, or null when there is none.</summary>
    /// <param name="name">A captured group's name.</param>
    public static uint? GroupId(string name)
    {
        ThrowHelper.ThrowIfNull(name);
        return CanSetOwnership ? Lookup(name, group: true) : null;
    }

    /// <summary>Gives the file at <paramref name="path"/> to the account <paramref name="userId"/>, leaving its group.</summary>
    /// <param name="path">The file; a link is changed itself, never followed.</param>
    /// <param name="userId">The account.</param>
    /// <returns>Whether the file now belongs to it: false on Windows, and wherever the platform refuses.</returns>
    public static bool TrySetOwner(string path, uint userId)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);
        return CanSetOwnership && ChangeOwner(path, userId, Unchanged) == 0;
    }

    /// <summary>Gives the file at <paramref name="path"/> to the group <paramref name="groupId"/>, leaving its owner.</summary>
    /// <param name="path">The file; a link is changed itself, never followed.</param>
    /// <param name="groupId">The group.</param>
    /// <returns>Whether the file is now the group's: false on Windows, and wherever the platform refuses.</returns>
    public static bool TrySetGroup(string path, uint groupId)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);
        return CanSetOwnership && ChangeOwner(path, Unchanged, groupId) == 0;
    }

    // getpwnam_r and getgrnam_r fill a caller's struct and a caller's buffer
    // for its strings. The id follows the name and password pointers in both
    // structs, on Linux and macOS and at either pointer width, so it is read
    // two pointers in rather than through two layouts.
    private static uint? Lookup(string name, bool group)
    {
        var entry = stackalloc nint[EntryWords];
        for (var size = 1024; size <= MaxBufferBytes; size *= 2)
        {
            var buffer = new byte[size];
            int status;
            nint found;
            fixed (byte* strings = buffer)
            {
                status = group
                    ? GetGroupByName(name, entry, strings, (nuint)size, out found)
                    : GetUserByName(name, entry, strings, (nuint)size, out found);
            }

            if (status != RangeError)
            {
                return status == 0 && found != 0 ? *(uint*)(entry + 2) : null;
            }
        }

        return null;
    }

    private static bool HoldsChownCapability()
    {
        try
        {
            var effective = File.ReadLines("/proc/self/status")
                .FirstOrDefault(line => line.StartsWith("CapEff:", StringComparison.Ordinal));
            return effective is not null
                && ulong.TryParse(
                    effective.AsSpan("CapEff:".Length).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out var capabilities)
                && (capabilities & ChownCapability) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "getpwnam_r", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int GetUserByName(string name, nint* entry, byte* buffer, nuint size, out nint result);

    [LibraryImport("libc", EntryPoint = "getgrnam_r", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int GetGroupByName(string name, nint* entry, byte* buffer, nuint size, out nint result);

    [LibraryImport("libc", EntryPoint = "lchown", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int ChangeOwner(string path, uint owner, uint group);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "getegid")]
    private static partial uint GetEffectiveGroupId();

    [LibraryImport("libc", EntryPoint = "getgroups", SetLastError = true)]
    private static partial int GetGroups(int size, uint* list);
}
