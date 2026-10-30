using Bodu;
using System.Buffers.Binary;

namespace FallbackPlan.Domain;

/// <summary>
/// What a restore writes back of an entry's captured metadata, attribute by
/// attribute, and the names it gives what it did not write back (FR-RST-004;
/// ADR-0083 to ADR-0087, ADR-0101). One rule for every way of restoring
/// (ADR-0149): the executor's plan predicts by it, and
/// <see cref="MetadataWriter"/> writes by it.
/// </summary>
public static class MetadataRules
{
    /// <summary>The set-user-id bit of a POSIX mode.</summary>
    private const uint SetUserId = 0x800;

    /// <summary>The set-group-id bit of a POSIX mode.</summary>
    private const uint SetGroupId = 0x400;

    /// <summary>The bits of a POSIX mode a restore writes: permissions, set-id and sticky.</summary>
    public const uint ModeBits = 0xFFF;

    /// <summary>The group's permission bits of a POSIX mode.</summary>
    private const uint GroupBits = 0x38;

    /// <summary>The version a Linux ACL's value starts with.</summary>
    private const uint AclVersion = 2;

    /// <summary>A Linux ACL entry's tag for the file's owner.</summary>
    private const ushort AclUserObject = 0x01;

    /// <summary>A Linux ACL entry's tag for an account named by number.</summary>
    private const ushort AclUser = 0x02;

    /// <summary>A Linux ACL entry's tag for the file's group.</summary>
    private const ushort AclGroupObject = 0x04;

    /// <summary>A Linux ACL entry's tag for a group named by number.</summary>
    private const ushort AclGroup = 0x08;

    /// <summary>A Linux ACL entry's tag for the mask.</summary>
    private const ushort AclMask = 0x10;

    /// <summary>A Linux ACL entry's tag for everyone else.</summary>
    private const ushort AclOther = 0x20;

    /// <summary>The receipt's vocabulary, in the order the format numbers the attributes.</summary>
    public static readonly IReadOnlyList<(CapturedMetadata Attribute, string Name)> Vocabulary =
    [
        (CapturedMetadata.ModifiedAt, "modified_at"),
        (CapturedMetadata.CreatedAt, "created_at"),
        (CapturedMetadata.AccessedAt, "accessed_at"),
        (CapturedMetadata.PosixMode, "posix_mode"),
        (CapturedMetadata.Owner, "owner"),
        (CapturedMetadata.Group, "group"),
        (CapturedMetadata.SecurityDescriptor, "security_descriptor"),
        (CapturedMetadata.ExtendedAttributes, "extended_attributes"),
        (CapturedMetadata.AlternateStreams, "alternate_streams"),
        (CapturedMetadata.FileAttributes, "file_attributes"),
    ];

    /// <summary>
    /// The Windows attribute bits a restore writes back, set as captured and
    /// cleared where not: read-only, hidden, system, archive and
    /// not-content-indexed (ADR-0175).
    /// </summary>
    public const uint WrittenFileAttributes = 0x1 | 0x2 | 0x4 | 0x20 | 0x2000;

    /// <summary>
    /// The bits that say what an entry is rather than how it is kept: a
    /// directory, a file with nothing else set, a sparse file and a reparse
    /// point. A restore gives an entry these by what it makes, or not at all.
    /// </summary>
    public const uint EntryFileAttributes = 0x10 | 0x80 | 0x200 | 0x400;

    /// <summary>
    /// Each bit of <paramref name="captured"/> a restore neither writes back
    /// nor gives an entry by making it, as compressed and encrypted, named in
    /// the code's words, in the bits' order. Any such bit leaves the
    /// attributes not applied.
    /// </summary>
    /// <param name="captured">An entry's captured attribute bits.</param>
    public static IReadOnlyList<string> UnsetFileAttributes(uint captured)
    {
        List<string> unset = [];
        for (var left = captured & ~(WrittenFileAttributes | EntryFileAttributes); left != 0; left &= left - 1)
        {
            var bit = left & (~left + 1);
            unset.Add(Enum.IsDefined(typeof(FileAttributes), (int)bit)
                ? ((FileAttributes)bit).ToString()
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"0x{bit:X8}"));
        }

        return unset;
    }

    /// <summary>
    /// The bits to set on an entry standing with <paramref name="standing"/>
    /// to give it those of <paramref name="captured"/> a restore writes back,
    /// keeping every other bit it has. Windows takes the plain-file bit only
    /// alone, and alone it is how every written bit is cleared.
    /// </summary>
    /// <param name="standing">The entry's bits as it stands.</param>
    /// <param name="captured">The entry's captured bits.</param>
    public static uint FileAttributesToSet(uint standing, uint captured)
    {
        const uint plain = 0x80;
        var bits = (standing & ~(WrittenFileAttributes | plain)) | (captured & WrittenFileAttributes);
        return bits == 0 ? plain : bits;
    }

    /// <summary>The attributes <paramref name="metadata"/> carries.</summary>
    public static CapturedMetadata Captured(EntryMetadata metadata)
    {
        ThrowHelper.ThrowIfNull(metadata);

        var captured = CapturedMetadata.None;
        if (metadata.ModifiedAt is not null)
        {
            captured |= CapturedMetadata.ModifiedAt;
        }

        if (metadata.CreatedAt is not null)
        {
            captured |= CapturedMetadata.CreatedAt;
        }

        if (metadata.AccessedAt is not null)
        {
            captured |= CapturedMetadata.AccessedAt;
        }

        if (metadata.PosixMode is not null)
        {
            captured |= CapturedMetadata.PosixMode;
        }

        if (metadata.OwnerName is not null)
        {
            captured |= CapturedMetadata.Owner;
        }

        if (metadata.GroupName is not null)
        {
            captured |= CapturedMetadata.Group;
        }

        // An empty one is no descriptor (06 §4.1 key 7), and is what earlier
        // builds wrote for every entry captured where none was read.
        if (metadata.WindowsSecurityDescriptor is { IsEmpty: false })
        {
            captured |= CapturedMetadata.SecurityDescriptor;
        }

        if (metadata.ExtendedAttributes.Count > 0)
        {
            captured |= CapturedMetadata.ExtendedAttributes;
        }

        if (metadata.AlternateStreams.Count > 0)
        {
            captured |= CapturedMetadata.AlternateStreams;
        }

        if (metadata.FileAttributes is not null)
        {
            captured |= CapturedMetadata.FileAttributes;
        }

        return captured;
    }

    /// <summary>
    /// Whether a symlink captured with <paramref name="mode"/> can carry it on
    /// this platform: anywhere that sets a link's own permissions, and on
    /// Linux, which gives every link <see cref="SymlinkMetadata.LinuxPermissions"/>
    /// and has no call to change them, only where the captured ones are those.
    /// </summary>
    /// <param name="mode">The link's captured mode.</param>
    public static bool LinkPermissionsLand(uint mode) =>
        SymlinkMetadata.CanSetPermissions
        || (OperatingSystem.IsLinux() && (mode & ModeBits) == SymlinkMetadata.LinuxPermissions);

    /// <summary>The kinds of extended attribute <paramref name="metadata"/> carries that are not written back everywhere.</summary>
    public static ExtendedAttributeKinds KindsOf(EntryMetadata metadata)
    {
        ThrowHelper.ThrowIfNull(metadata);

        var kinds = ExtendedAttributeKinds.None;
        foreach (var attribute in metadata.ExtendedAttributes)
        {
            var name = attribute.Name.Span;
            if (IsPrivileged(name))
            {
                kinds |= ExtendedAttributeKinds.Privileged;
            }
            else if (IsPosixAcl(name))
            {
                kinds |= NamesAccountsByNumber(attribute) ? ExtendedAttributeKinds.NumberedAcl : ExtendedAttributeKinds.AnonymousAcl;
            }
            else if (IsUser(name))
            {
                kinds |= ExtendedAttributeKinds.User;
            }
        }

        return kinds;
    }

    /// <summary>
    /// Whether <paramref name="attribute"/> is a Linux POSIX ACL that names
    /// an account or a group by number. One whose value will not parse is
    /// counted as naming one, because nothing vouches for what it grants.
    /// </summary>
    /// <param name="attribute">A captured extended attribute.</param>
    public static bool NamesAccountsByNumber(ExtendedAttributeEntry attribute)
    {
        ThrowHelper.ThrowIfNull(attribute);
        return IsPosixAcl(attribute.Name.Span) && (!TryReadAcl(attribute.Value.Span, out var acl) || acl.NamesAccounts);
    }

    /// <summary>
    /// Why a restore to <paramref name="target"/> does not attempt to write
    /// <paramref name="attribute"/> back onto a file, a folder or, where
    /// <paramref name="link"/>, a symlink, or null when it does (ADR-0087,
    /// ADR-0101).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Linux keeps no user-namespace attribute and no ACL on a symlink, and
    /// refuses either, so neither is attempted on one there.
    /// </para>
    /// <para>
    /// A POSIX ACL is written back only on Linux, which keeps one as an
    /// extended attribute. macOS keeps no POSIX ACLs, and would take it as an
    /// attribute that grants nothing. An ACL that names an account or group by
    /// number is written back only where this installation captured the
    /// snapshot (<see cref="MetadataTarget.CapturedHere"/>): anywhere
    /// else the number may be someone else.
    /// </para>
    /// <para>
    /// On Linux only root writes the security and trusted namespaces, so a
    /// restore that is not root leaves them, as its plan said. A security
    /// policy may let an account relabel its own file; a restore by that
    /// account still leaves the label the policy gave the file it made.
    /// </para>
    /// </remarks>
    internal static string? Withheld(ExtendedAttributeEntry attribute, MetadataTarget target, bool link = false)
    {
        var name = attribute.Name.Span;
        if (link && OperatingSystem.IsLinux())
        {
            if (IsPosixAcl(name))
            {
                return "Linux keeps no access control list on a symlink";
            }

            if (IsUser(name))
            {
                return "Linux keeps no user attributes on a symlink";
            }
        }

        if (IsPosixAcl(name))
        {
            if (!OperatingSystem.IsLinux())
            {
                return "no POSIX access control lists on this target";
            }

            return !target.CapturedHere && NamesAccountsByNumber(attribute)
                ? "names accounts by number, and this installation did not capture the snapshot"
                : null;
        }

        return OperatingSystem.IsLinux() && IsPrivileged(name) && target.Account is not { IsSuperuser: true }
            ? "writable only by root"
            : null;
    }

    /// <summary>Whether <paramref name="name"/> is a Linux POSIX ACL that governs who may reach the file itself.</summary>
    internal static bool IsAccessAcl(ReadOnlySpan<byte> name) => name.SequenceEqual("system.posix_acl_access"u8);

    /// <summary>
    /// The permissions a file or folder captured with <paramref name="mode"/>
    /// may be given where its captured access ACL <paramref name="acl"/> did
    /// not come back: its group no more than the ACL gave the group.
    /// </summary>
    /// <remarks>
    /// While a file has an ACL, the group bits of its mode are the ACL's mask,
    /// which bounds what every account and group the ACL names may do. Written
    /// back without the ACL, they would give the file's group all of that.
    /// What the ACL gave the group is its group entry as far as the mask lets
    /// it, and nothing where the ACL will not parse.
    /// </remarks>
    internal static uint WithoutAcl(uint mode, ExtendedAttributeEntry acl)
    {
        var group = TryReadAcl(acl.Value.Span, out var read) && read.GroupObject is { } granted
            ? granted & (read.Mask ?? 7)
            : 0;
        return (mode & ~GroupBits) | (mode & (group << 3));
    }

    /// <summary>
    /// The permissions a file or folder captured with <paramref name="mode"/>
    /// may be given once the halves of its ownership in
    /// <paramref name="given"/> have landed (ADR-0085). A set-user-id file
    /// runs as its owner, so its bit is kept only with the owner it was
    /// captured under; on any other owner it would run as whoever restored
    /// it. A set-group-id bit is kept only with its group, likewise: on a
    /// folder it hands that group to everything made in it.
    /// </summary>
    /// <param name="mode">The captured mode.</param>
    /// <param name="given">Which of the owner and group were given back.</param>
    public static uint PermittedMode(uint mode, CapturedMetadata given)
    {
        var permitted = mode & ModeBits;
        if ((given & CapturedMetadata.Owner) == 0)
        {
            permitted &= ~SetUserId;
        }

        if ((given & CapturedMetadata.Group) == 0)
        {
            permitted &= ~SetGroupId;
        }

        return permitted;
    }

    /// <summary>
    /// The receipt's names for what was captured and not <paramref name="applied"/>,
    /// in the format's order, or null when nothing captured is left out.
    /// </summary>
    /// <param name="captured">What the entry carries.</param>
    /// <param name="applied">What the writes for it actually did.</param>
    public static IReadOnlyList<string>? NotApplied(CapturedMetadata captured, CapturedMetadata applied)
    {
        var missing = captured & ~applied;
        return missing == CapturedMetadata.None
            ? null
            : [.. Vocabulary.Where(entry => missing.HasFlag(entry.Attribute)).Select(entry => entry.Name)];
    }

    /// <summary>Whether <paramref name="name"/> is in Linux's user namespace.</summary>
    private static bool IsUser(ReadOnlySpan<byte> name) => name.StartsWith("user."u8);

    /// <summary>Whether <paramref name="name"/> is in a Linux namespace only root writes: security or trusted.</summary>
    private static bool IsPrivileged(ReadOnlySpan<byte> name) =>
        name.StartsWith("security."u8) || name.StartsWith("trusted."u8);

    /// <summary>Whether <paramref name="name"/> is a Linux POSIX ACL: the file's own, or a folder's default for what is made in it.</summary>
    private static bool IsPosixAcl(ReadOnlySpan<byte> name) =>
        IsAccessAcl(name) || name.SequenceEqual("system.posix_acl_default"u8);

    /// <summary>
    /// What a Linux ACL's value says, as far as a restore needs it: version 2,
    /// then each entry's tag, permissions and id, eight bytes little-endian.
    /// False where it will not parse, a tag it does not know among it.
    /// </summary>
    private static bool TryReadAcl(ReadOnlySpan<byte> value, out AclRead read)
    {
        read = default;
        if (value.Length < 4 || (value.Length - 4) % 8 != 0 || BinaryPrimitives.ReadUInt32LittleEndian(value) != AclVersion)
        {
            return false;
        }

        var names = false;
        uint? group = null;
        uint? mask = null;
        for (var at = 4; at < value.Length; at += 8)
        {
            var permissions = BinaryPrimitives.ReadUInt16LittleEndian(value[(at + 2)..]) & 7u;
            switch (BinaryPrimitives.ReadUInt16LittleEndian(value[at..]))
            {
                case AclUser or AclGroup:
                    names = true;
                    break;
                case AclGroupObject:
                    group = permissions;
                    break;
                case AclMask:
                    mask = permissions;
                    break;
                case AclUserObject or AclOther:
                    break;
                default:
                    return false;
            }
        }

        read = new AclRead(names, group, mask);
        return true;
    }

    /// <summary>What a Linux ACL says, as far as a restore needs it.</summary>
    /// <param name="NamesAccounts">Whether an entry names an account or a group by number.</param>
    /// <param name="GroupObject">The permissions its group entry gives the file's group.</param>
    /// <param name="Mask">Its mask, which bounds every entry but the owner's and everyone else's.</param>
    private readonly record struct AclRead(bool NamesAccounts, uint? GroupObject, uint? Mask);
}
