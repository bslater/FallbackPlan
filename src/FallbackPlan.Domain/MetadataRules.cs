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
        for (var left = UnsetFileAttributeBits(captured, compresses); left != 0; left &= left - 1)
        {
            var bit = left & (~left + 1);
            unset.Add(Enum.IsDefined(typeof(FileAttributes), (int)bit)
                ? ((FileAttributes)bit).ToString()
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"0x{bit:X8}"));
        }

        return unset;
    }

    /// <summary>
    /// The bits of <paramref name="captured"/> that <see cref="UnsetFileAttributes"/>
    /// names, so the names for several entries can be given from one mask.
    /// </summary>
    /// <param name="captured">An entry's captured attribute bits.</param>
    /// <param name="compresses">Whether the target gives this entry its compression.</param>
    public static uint UnsetFileAttributeBits(uint captured, bool compresses = false) =>
        captured & ~(WrittenFileAttributes | EntryFileAttributes | (compresses ? CompressedFileAttribute : 0));

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

    /// <summary>The prefix of the capture diagnostic that names a file's flags (ADR-0180).</summary>
    private const string FileFlagsPrefix = "file-flags: ";

    /// <summary>Each flag with the name a diagnostic gives it, in the order it gives them.</summary>
    private static readonly (FileFlagBits Flag, string Name)[] FileFlagNames =
    [
        (FileFlagBits.Immutable, "immutable"),
        (FileFlagBits.AppendOnly, "append-only"),
        (FileFlagBits.NoDump, "no-dump"),
        (FileFlagBits.Compressed, "compressed"),
        (FileFlagBits.Encrypted, "encrypted"),
        (FileFlagBits.Hidden, "hidden"),
    ];

    /// <summary>
    /// The flags a Linux file carries, from what <c>statx</c> reports in
    /// <paramref name="attributes"/>, read only where
    /// <paramref name="attributesMask"/> says the volume reports each
    /// (ADR-0180).
    /// </summary>
    /// <param name="attributes"><c>stx_attributes</c>.</param>
    /// <param name="attributesMask"><c>stx_attributes_mask</c>.</param>
    public static FileFlagBits FileFlagsFromStatx(ulong attributes, ulong attributesMask)
    {
        var reported = attributes & attributesMask;
        var flags = FileFlagBits.None;
        flags |= (reported & 0x10) != 0 ? FileFlagBits.Immutable : FileFlagBits.None;
        flags |= (reported & 0x20) != 0 ? FileFlagBits.AppendOnly : FileFlagBits.None;
        flags |= (reported & 0x40) != 0 ? FileFlagBits.NoDump : FileFlagBits.None;
        flags |= (reported & 0x04) != 0 ? FileFlagBits.Compressed : FileFlagBits.None;
        flags |= (reported & 0x800) != 0 ? FileFlagBits.Encrypted : FileFlagBits.None;
        return flags;
    }

    /// <summary>
    /// The flags a macOS file carries, from its <c>st_flags</c>
    /// <paramref name="flags"/>: the user's and the system's immutable and
    /// append-only flags alike, no-dump, compressed and hidden (ADR-0180).
    /// </summary>
    /// <param name="flags"><c>st_flags</c>.</param>
    public static FileFlagBits FileFlagsFromDarwin(uint flags)
    {
        var named = FileFlagBits.None;
        named |= (flags & (0x00000002 | 0x00020000)) != 0 ? FileFlagBits.Immutable : FileFlagBits.None;
        named |= (flags & (0x00000004 | 0x00040000)) != 0 ? FileFlagBits.AppendOnly : FileFlagBits.None;
        named |= (flags & 0x00000001) != 0 ? FileFlagBits.NoDump : FileFlagBits.None;
        named |= (flags & 0x00000020) != 0 ? FileFlagBits.Compressed : FileFlagBits.None;
        named |= (flags & 0x00008000) != 0 ? FileFlagBits.Hidden : FileFlagBits.None;
        return named;
    }

    /// <summary>
    /// The capture diagnostic that names <paramref name="flags"/>, each in
    /// one order (<c>file-flags: immutable, no-dump</c>), or null where there
    /// are none (ADR-0180; specification 06 §4.1 key 13).
    /// </summary>
    public static string? FileFlagsDiagnostic(FileFlagBits flags) =>
        flags == FileFlagBits.None ? null : FileFlagsPrefix + string.Join(", ", FileFlagNamesOf(flags));

    /// <summary>
    /// The name of each of <paramref name="flags"/>, as the diagnostic gives
    /// them and in its order (ADR-0180).
    /// </summary>
    public static IReadOnlyList<string> FileFlagNamesOf(FileFlagBits flags) =>
        [.. FileFlagNames.Where(entry => flags.HasFlag(entry.Flag)).Select(entry => entry.Name)];

    /// <summary>
    /// What a restore says of <paramref name="flags"/> a file carried, none
    /// of which it sets, or null where there are none (ADR-0180 Amendment 1).
    /// </summary>
    public static string? FileFlagsNotApplied(FileFlagBits flags) =>
        flags == FileFlagBits.None
            ? null
            : $"file flag(s) not applied: {string.Join(", ", FileFlagNamesOf(flags))} (not set by a restore)";

    /// <summary>
    /// The flags the capture <paramref name="diagnostics"/> name
    /// (<see cref="FileFlagsDiagnostic"/>); a name no build knows is passed
    /// over.
    /// </summary>
    public static FileFlagBits FileFlagsOf(IEnumerable<string> diagnostics)
    {
        ThrowHelper.ThrowIfNull(diagnostics);

        var flags = FileFlagBits.None;
        foreach (var diagnostic in diagnostics.Where(diagnostic => diagnostic.StartsWith(FileFlagsPrefix, StringComparison.Ordinal)))
        {
            foreach (var name in diagnostic[FileFlagsPrefix.Length..].Split(", "))
            {
                flags |= FileFlagNames.FirstOrDefault(entry => entry.Name == name).Flag;
            }
        }

        return flags;
    }

    /// <summary>
    /// <paramref name="diagnostics"/> with the line that names a file's flags
    /// made to name <paramref name="flags"/>: a manifest restated for a file
    /// whose bytes did not change keeps what it said of its read, and says
    /// the flags the file carries now (ADR-0180).
    /// </summary>
    public static IReadOnlyList<string> WithFileFlags(IReadOnlyList<string> diagnostics, FileFlagBits flags)
    {
        ThrowHelper.ThrowIfNull(diagnostics);

        IEnumerable<string> kept = diagnostics.Where(diagnostic => !diagnostic.StartsWith(FileFlagsPrefix, StringComparison.Ordinal));
        return FileFlagsDiagnostic(flags) is { } named ? [.. kept, named] : [.. kept];
    }

    /// <summary>
    /// Whether a target keeps a file's alternate streams as extended
    /// attributes of the same names: where it writes attributes of its own
    /// and keeps no streams, as Linux and macOS do (ADR-0179).
    /// </summary>
    /// <param name="kind">The entry's kind. A folder is captured with no streams, and a link has none.</param>
    /// <param name="supportsAlternateStreams">Whether the target keeps alternate streams.</param>
    /// <param name="writesAttributes">Whether it writes extended attributes of its own.</param>
    public static bool KeepsStreamsAsAttributes(EntryKind kind, bool supportsAlternateStreams, bool writesAttributes) =>
        kind == EntryKind.File && !supportsAlternateStreams && writesAttributes;

    /// <summary>
    /// Why a stream named <paramref name="name"/> is not kept as an attribute,
    /// or null where it is: where a target takes an attribute's name only in
    /// a namespace, as Linux does, one with no name in its user namespace,
    /// the only one a file's own attributes are given (ADR-0179).
    /// </summary>
    /// <param name="name">The stream's name, as captured.</param>
    /// <param name="namespaced">Whether the target takes names only in a namespace.</param>
    public static string? RefusedStreamAsAttribute(ReadOnlySpan<byte> name, bool namespaced) =>
        namespaced && !(name.Length > "user."u8.Length && IsUser(name))
            ? "it has no name in the user namespace, the only one Linux gives a file's own attributes"
            : null;

    /// <summary>
    /// Why a stream whose name has <paramref name="nameBytes"/> bytes and
    /// whose content <paramref name="length"/> is not kept as an attribute
    /// where the platform takes names of at most
    /// <paramref name="maxNameBytes"/> and values of at most
    /// <paramref name="maxValueBytes"/>, or null where it fits
    /// (ADR-0179 Amendment 2).
    /// </summary>
    /// <param name="nameBytes">The stream's name, in bytes.</param>
    /// <param name="length">Its content's length.</param>
    /// <param name="maxNameBytes">The most bytes a name may have; null where none is known.</param>
    /// <param name="maxValueBytes">The most bytes a value may hold; null where none is known.</param>
    public static string? StreamTooLargeForAttribute(int nameBytes, ulong length, uint? maxNameBytes, uint? maxValueBytes) =>
        maxNameBytes is { } maxName && nameBytes > maxName
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"its name is longer than the {maxName} bytes an attribute's name may have here")
            : maxValueBytes is { } maxValue && length > maxValue
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"it is larger than the {maxValue} bytes an attribute may hold here")
                : null;

    /// <summary>The bytes in the longest name of the streams captured with <paramref name="metadata"/>; 0 where it has none.</summary>
    public static int LongestStreamName(EntryMetadata metadata)
    {
        ThrowHelper.ThrowIfNull(metadata);

        return metadata.AlternateStreams.Count == 0 ? 0 : metadata.AlternateStreams.Max(stream => stream.Name.Length);
    }

    /// <summary>The length of the largest stream captured with <paramref name="metadata"/>; 0 where it has none.</summary>
    public static ulong LargestStream(EntryMetadata metadata)
    {
        ThrowHelper.ThrowIfNull(metadata);

        return metadata.AlternateStreams.Count == 0 ? 0 : metadata.AlternateStreams.Max(stream => stream.Length);
    }

    /// <summary>
    /// How many of the streams captured with <paramref name="metadata"/> have
    /// no name in the user namespace (<see cref="RefusedStreamAsAttribute"/>),
    /// which a target that takes names only in a namespace does not keep.
    /// </summary>
    public static int StreamsOutsideUserNamespace(EntryMetadata metadata)
    {
        ThrowHelper.ThrowIfNull(metadata);

        return metadata.AlternateStreams.Count(stream => RefusedStreamAsAttribute(stream.Name.Span, namespaced: true) is not null);
    }

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
