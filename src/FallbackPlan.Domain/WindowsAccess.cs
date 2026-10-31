using System.Runtime.InteropServices;

namespace FallbackPlan.Domain;

/// <summary>One entry of a DACL that allows or denies one account access.</summary>
/// <param name="Account">The account's SID.</param>
/// <param name="Allows">Whether it allows, rather than denies.</param>
/// <param name="Mask">The rights it allows or denies, as captured.</param>
/// <param name="Flags">Its inheritance flags, as captured.</param>
public readonly record struct AccessEntry(string Account, bool Allows, uint Mask, byte Flags);

/// <summary>
/// The access a file made somewhere takes, and the folder it comes from: the
/// nearest that stands at or above where the file is made (ADR-0178
/// Amendment 3).
/// </summary>
/// <param name="Folder">The folder whose DACL was read.</param>
/// <param name="Access">What the file takes, per account.</param>
public sealed record InheritedAccess(string Folder, IReadOnlyDictionary<string, uint> Access);

/// <summary>
/// The access a Windows DACL gives each account to a file, and the access a
/// file made in a folder takes from it, so the plan can say where a restore
/// that does not write a file's own descriptor leaves it open to more than
/// it was (ADR-0178; architecture 06 §3).
/// </summary>
/// <remarks>
/// <para>
/// The reading is each account's own entries, not who is in which group: a
/// right an account holds through a group it is in is that group's. A
/// denying entry takes from the same account's allowing ones wherever it
/// stands, as a DACL in Windows' own order reads. Generic rights are read as
/// a file's, which is what a file made in a folder is given for them.
/// </para>
/// <para>
/// A file made in a folder takes each entry the folder marks to pass to
/// files, wherever the folder got it. The creator's entries become the
/// restoring account's, which already holds what it restores, so they widen
/// nothing.
/// </para>
/// <para>
/// A folder made in a folder passes entries on as Windows does: one that
/// passes to folders becomes its own, and passes further; one that passes to
/// files alone is kept for the files below it; one marked not to propagate
/// stops there (ADR-0178 Amendment 3).
/// </para>
/// </remarks>
public static partial class WindowsAccess
{
    private const byte ObjectInherit = 0x01;
    private const byte ContainerInherit = 0x02;
    private const byte NoPropagate = 0x04;
    private const byte InheritOnly = 0x08;
    private const uint GenericRead = 0x8000_0000;
    private const uint GenericWrite = 0x4000_0000;
    private const uint GenericExecute = 0x2000_0000;
    private const uint GenericAll = 0x1000_0000;
    private const uint FileGenericRead = 0x0012_0089;
    private const uint FileGenericWrite = 0x0012_0116;
    private const uint FileGenericExecute = 0x0012_00A0;
    private const uint FileAllAccess = 0x001F_01FF;
    private const uint Synchronize = 0x0010_0000;
    private const uint DaclInformation = 0x4;
    private const string Everyone = "S-1-1-0";
    private const string CreatorOwner = "S-1-3-0";
    private const string CreatorGroup = "S-1-3-1";

    /// <summary>
    /// The access <paramref name="descriptor"/>'s DACL gives each account: a
    /// null DACL gives everyone everything, and a descriptor captured with no
    /// DACL says nothing, so null.
    /// </summary>
    /// <param name="descriptor">A descriptor as captured.</param>
    public static IReadOnlyDictionary<string, uint>? Of(WindowsSecurityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return !descriptor.DaclPresent ? null
            : descriptor.NullDacl ? new Dictionary<string, uint>(StringComparer.Ordinal) { [Everyone] = FileAllAccess }
            : Of(descriptor.Entries);
    }

    /// <summary>
    /// The access <paramref name="entries"/> give each account, in the order
    /// each is first allowed: what its allowing entries grant less what its
    /// denying ones take, inherit-only entries apart, and an account left
    /// with nothing not named.
    /// </summary>
    /// <param name="entries">A DACL's entries, in its order.</param>
    public static IReadOnlyDictionary<string, uint> Of(IReadOnlyList<AccessEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var allowed = new Dictionary<string, uint>(StringComparer.Ordinal);
        var denied = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(entry => (entry.Flags & InheritOnly) == 0))
        {
            var into = entry.Allows ? allowed : denied;
            into[entry.Account] = into.GetValueOrDefault(entry.Account) | AsAFiles(entry.Mask);
        }

        return allowed
            .Select(grant => (grant.Key, Mask: grant.Value & ~denied.GetValueOrDefault(grant.Key)))
            .Where(grant => grant.Mask != 0)
            .ToDictionary(grant => grant.Key, grant => grant.Mask, StringComparer.Ordinal);
    }

    /// <summary>
    /// The access a file takes where it is made <paramref name="made"/>
    /// folders below a folder whose DACL holds <paramref name="folder"/>,
    /// each of them made too: the entries that pass down to it, as the file's
    /// own, the creator's apart.
    /// </summary>
    /// <param name="folder">The DACL entries of the folder that stands, in its order.</param>
    /// <param name="made">How many folders are made between it and the file: none where the file is made in it.</param>
    public static IReadOnlyDictionary<string, uint> InheritedByAFile(IReadOnlyList<AccessEntry> folder, int made = 0)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentOutOfRangeException.ThrowIfNegative(made);

        var passing = folder;
        for (var level = 0; level < made; level++)
        {
            passing = PassedToAFolder(passing);
        }

        return Of([.. passing
            .Where(entry => (entry.Flags & ObjectInherit) != 0 && entry.Account is not (CreatorOwner or CreatorGroup))
            .Select(entry => entry with { Flags = 0 })]);
    }

    /// <summary>
    /// Each account <paramref name="inherited"/> gives a right
    /// <paramref name="own"/> did not, in <paramref name="inherited"/>'s order.
    /// Synchronising alone opens nothing.
    /// </summary>
    /// <param name="own">The access an entry's own DACL gave.</param>
    /// <param name="inherited">The access it would take from its folder instead.</param>
    public static IReadOnlyList<string> Wider(IReadOnlyDictionary<string, uint> own, IReadOnlyDictionary<string, uint> inherited)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(inherited);
        return [.. inherited
            .Where(grant => (grant.Value & ~Synchronize & ~own.GetValueOrDefault(grant.Key)) != 0)
            .Select(grant => grant.Key)];
    }

    /// <summary>
    /// The access a file made at <paramref name="folder"/> would take, read
    /// from the nearest folder at or above it that stands, through each
    /// folder below that the restore makes. Null off Windows, or where the
    /// folder's DACL will not read.
    /// </summary>
    /// <param name="folder">Where the restore would make a file.</param>
    public static InheritedAccess? InheritedByAFileAt(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var made = 0;
        for (var at = Path.GetFullPath(folder); at is not null; at = Path.GetDirectoryName(at), made++)
        {
            if (Directory.Exists(at))
            {
                return DaclOf(at) is { } descriptor && WindowsSecurityDescriptor.Read(descriptor) is { DaclPresent: true } read
                    ? new InheritedAccess(at, InheritedByAFile(read.Entries, made))
                    : null;
            }
        }

        return null;
    }

    /// <summary>
    /// The entries a folder made in a folder holding <paramref name="folder"/>
    /// takes, with the flags that say what it passes on. The creator's own
    /// copy is the restoring account's, so only what it passes on is kept.
    /// </summary>
    private static List<AccessEntry> PassedToAFolder(IReadOnlyList<AccessEntry> folder)
    {
        var passed = new List<AccessEntry>();
        foreach (var entry in folder)
        {
            var stops = (entry.Flags & NoPropagate) != 0;
            if ((entry.Flags & ContainerInherit) != 0)
            {
                var onward = stops ? (byte)0 : (byte)(entry.Flags & (ObjectInherit | ContainerInherit));
                if (entry.Account is not (CreatorOwner or CreatorGroup))
                {
                    passed.Add(entry with { Flags = onward });
                }
                else if (!stops)
                {
                    passed.Add(entry with { Flags = (byte)(onward | InheritOnly) });
                }
            }
            else if ((entry.Flags & ObjectInherit) != 0 && !stops)
            {
                passed.Add(entry with { Flags = ObjectInherit | InheritOnly });
            }
        }

        return passed;
    }

    private static uint AsAFiles(uint mask) =>
        ((mask & GenericRead) != 0 ? FileGenericRead : 0)
        | ((mask & GenericWrite) != 0 ? FileGenericWrite : 0)
        | ((mask & GenericExecute) != 0 ? FileGenericExecute : 0)
        | ((mask & GenericAll) != 0 ? FileAllAccess : 0)
        | (mask & FileAllAccess);

    private static byte[]? DaclOf(string path)
    {
        _ = GetFileSecurity(path, DaclInformation, null, 0, out var needed);
        if (needed == 0)
        {
            return null;
        }

        var descriptor = new byte[needed];
        return GetFileSecurity(path, DaclInformation, descriptor, needed, out _) ? descriptor : null;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "GetFileSecurityW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileSecurity(string path, uint information, byte[]? descriptor, uint length, out uint needed);
}
