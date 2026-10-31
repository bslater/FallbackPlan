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
    /// The compressed bit, which a target that keeps compression gives an
    /// entry by a call of its own rather than with the bits it writes
    /// (ADR-0175 Amendment 2).
    /// </summary>
    public const uint CompressedFileAttribute = 0x800;

    /// <summary>
    /// The bits that say what an entry is rather than how it is kept: a
    /// directory, a file with nothing else set, a sparse file and a reparse
    /// point. A restore gives an entry these by what it makes, or not at all.
    /// </summary>
    public const uint EntryFileAttributes = 0x10 | 0x80 | 0x200 | 0x400;

    /// <summary>
    /// Each bit of <paramref name="captured"/> a restore neither writes back
    /// nor gives an entry by making it, as encrypted, and compressed where the
    /// target keeps no compression, named in the code's words, in the bits'
    /// order. Any such bit leaves the attributes not applied.
    /// </summary>
    /// <param name="captured">An entry's captured attribute bits.</param>
    /// <param name="compresses">Whether the target gives an entry its compression (ADR-0175 Amendment 2).</param>
    public static IReadOnlyList<string> UnsetFileAttributes(uint captured, bool compresses = false)
    {
        List<string> unset = [];
        var given = WrittenFileAttributes | EntryFileAttributes | (compresses ? CompressedFileAttribute : 0);
        for (var left = captured & ~given; left != 0; left &= left - 1)
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

    /// <summary>
    /// The SIDs the Windows security descriptor captured with
    /// <paramref name="metadata"/> names (<see cref="WindowsSecurityDescriptor.Accounts"/>),
    /// or null where it carries none or one that does not read.
    /// </summary>
    public static IReadOnlyList<string>? AccountsOf(EntryMetadata metadata)
    {
        ThrowHelper.ThrowIfNull(metadata);

        return metadata.WindowsSecurityDescriptor is { IsEmpty: false } descriptor
            ? WindowsSecurityDescriptor.Read(descriptor.Span)?.Accounts
            : null;
    }

    /// <summary>
    /// The SIDs the audit list of the Windows security descriptor captured
    /// with <paramref name="metadata"/> audits
    /// (<see cref="WindowsSecurityDescriptor.AuditedAccounts"/>; ADR-0176
    /// Amendment 3), or null where it carries no audit list with an entry in
    /// it, or a descriptor that does not read.
    /// </summary>
    public static IReadOnlyList<string>? AuditedAccountsOf(EntryMetadata metadata)
    {
        ThrowHelper.ThrowIfNull(metadata);

        return metadata.WindowsSecurityDescriptor is { IsEmpty: false } descriptor
            && WindowsSecurityDescriptor.Read(descriptor.Span) is { SaclPresent: true } read
            ? read.AuditedAccounts
            : null;
    }

    /// <summary>
    /// The SID the Windows security descriptor captured with
    /// <paramref name="metadata"/> names as its owner, or null where it names
    /// none, carries none, or carries one that does not read.
    /// </summary>
    public static string? DescriptorOwnerOf(EntryMetadata metadata)
    {
        ThrowHelper.ThrowIfNull(metadata);

        return metadata.WindowsSecurityDescriptor is { IsEmpty: false } descriptor
            ? WindowsSecurityDescriptor.Read(descriptor.Span)?.Owner
            : null;
    }

    /// <summary>
    /// The access the DACL of the Windows security descriptor
    /// <paramref name="metadata"/> carries gives each account
    /// (<see cref="WindowsAccess"/>; ADR-0178), or null where it carries none,
    /// one that does not read, or one with no DACL.
    /// </summary>
    public static IReadOnlyDictionary<string, uint>? AccessOf(EntryMetadata metadata)
    {
        ThrowHelper.ThrowIfNull(metadata);

        return metadata.WindowsSecurityDescriptor is { IsEmpty: false } descriptor
            && WindowsSecurityDescriptor.Read(descriptor.Span) is { } read
            ? WindowsAccess.Of(read)
            : null;
    }

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

    /// <summary>
    /// Whether a target keeps an entry's extended attributes as alternate
    /// streams of the same names: a file's (ADR-0177) or a folder's (ADR-0177
    /// Amendment 2), where the target keeps streams and writes no attributes
    /// of its own.
    /// </summary>
    /// <param name="kind">
    /// The entry's kind. A symlink keeps none: a stream joined to its path
    /// would be written through it.
    /// </param>
    /// <param name="supportsAlternateStreams">Whether the target keeps alternate streams.</param>
    /// <param name="writesAttributes">Whether it writes extended attributes of its own.</param>
    public static bool KeepsAttributesAsStreams(EntryKind kind, bool supportsAlternateStreams, bool writesAttributes) =>
        kind is EntryKind.File or EntryKind.DirectoryPlaceholder && supportsAlternateStreams && !writesAttributes;

    /// <summary>
    /// Why <paramref name="attribute"/> is not kept as a stream, or null where
    /// it is: one in Linux's security or trusted namespace, or a POSIX ACL,
    /// means something to Linux's kernel alone (ADR-0177).
    /// </summary>
    internal static string? WithheldAsStream(ExtendedAttributeEntry attribute) =>
        IsPrivileged(attribute.Name.Span) || IsPosixAcl(attribute.Name.Span)
            ? "only Linux's kernel reads it, so it is not kept as a stream"
            : null;

    /// <summary>
    /// The stream each of a file's captured extended attributes is kept as
    /// where a target keeps them as streams, in the order they were captured,
    /// or why one is not (ADR-0177): only Linux's kernel reads it, its name is
    /// one no stream may have (<see cref="AlternateStreams.TryName"/>), the
    /// file was captured with a stream of that name, or an attribute before it
    /// has that name. Windows matches a stream's name without regard to case,
    /// and so do the last two.
    /// </summary>
    /// <param name="metadata">The file's captured metadata, its own streams among it.</param>
    public static IReadOnlyList<AttributeKeptAs> AsStreams(EntryMetadata metadata)
    {
        if (metadata.ExtendedAttributes.Count == 0)
        {
            return [];
        }

        var own = new HashSet<string>(
            metadata.AlternateStreams.Select(stream => System.Text.Encoding.UTF8.GetString(stream.Name.Span)), StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var judged = new AttributeKeptAs[metadata.ExtendedAttributes.Count];
        for (var at = 0; at < judged.Length; at++)
        {
            var attribute = metadata.ExtendedAttributes[at];
            judged[at] = WithheldAsStream(attribute) is { } withheld ? new AttributeKeptAs(null, withheld)
                : !AlternateStreams.TryName(attribute.Name.Span, out var name, out var why) ? new AttributeKeptAs(null, why)
                : own.Contains(name) ? new AttributeKeptAs(null, "the file was captured with a stream of that name")
                : taken.Add(name) ? new AttributeKeptAs(name, null)
                : new AttributeKeptAs(null, "another of the file's attributes has that name, as Windows compares names");
        }

        return judged;
    }

    /// <summary>
    /// How many of a file's captured extended attributes are not kept as
    /// streams for their names, those only Linux's kernel reads apart, which
    /// the plan declares (ADR-0177 Amendment 1).
    /// </summary>
    /// <param name="metadata">The file's captured metadata, its own streams among it.</param>
    public static int RefusedAttributeNames(EntryMetadata metadata) =>
        metadata.ExtendedAttributes.Zip(AsStreams(metadata))
            .Count(judged => judged.Second.Why is not null && WithheldAsStream(judged.First) is null);

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

/// <summary>
/// The stream a file's extended attribute is kept as where a target keeps
/// attributes as streams, or why it is not (ADR-0177).
/// </summary>
/// <param name="Stream">The stream's name, or null where it is not kept.</param>
/// <param name="Why">Why it is not kept, or null where it is.</param>
public readonly record struct AttributeKeptAs(string? Stream, string? Why);
