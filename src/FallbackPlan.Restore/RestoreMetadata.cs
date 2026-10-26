using Bodu;
using System.Buffers.Binary;
using System.Globalization;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Restore;

/// <summary>
/// The attributes of an entry's captured metadata (specification 06 §4.1),
/// one flag each.
/// </summary>
[Flags]
public enum CapturedMetadata
{
    /// <summary>Nothing captured.</summary>
    None = 0,

    /// <summary>The modification time (key 1).</summary>
    ModifiedAt = 1 << 0,

    /// <summary>The creation time (key 2).</summary>
    CreatedAt = 1 << 1,

    /// <summary>The access time (key 3).</summary>
    AccessedAt = 1 << 2,

    /// <summary>The POSIX mode bits (key 4).</summary>
    PosixMode = 1 << 3,

    /// <summary>The owner's name (key 5).</summary>
    Owner = 1 << 4,

    /// <summary>The group's name (key 6).</summary>
    Group = 1 << 5,

    /// <summary>The Windows security descriptor (key 7).</summary>
    SecurityDescriptor = 1 << 6,

    /// <summary>Extended attributes (key 8).</summary>
    ExtendedAttributes = 1 << 7,

    /// <summary>Alternate data streams (key 9).</summary>
    AlternateStreams = 1 << 8,

    /// <summary>The platform's attribute bits (key 10).</summary>
    FileAttributes = 1 << 9,
}

/// <summary>
/// The kinds of extended attribute an item carries that a restore does not
/// write back everywhere (ADR-0087), one flag each.
/// </summary>
[Flags]
public enum ExtendedAttributeKinds
{
    /// <summary>None of them.</summary>
    None = 0,

    /// <summary>One in Linux's security or trusted namespace, which only root writes.</summary>
    Privileged = 1 << 0,

    /// <summary>A Linux POSIX ACL that names an account or group by number, or that will not parse.</summary>
    NumberedAcl = 1 << 1,

    /// <summary>A Linux POSIX ACL that names no account or group: only the owner, the group, the mask and everyone else.</summary>
    AnonymousAcl = 1 << 2,

    /// <summary>One in Linux's user namespace, which Linux keeps on no symlink.</summary>
    User = 1 << 3,
}

/// <summary>
/// What a restore knows of one item from its manifest: the bytes restoring
/// it writes, and what was captured with it.
/// </summary>
/// <param name="Kind">The entry's kind.</param>
/// <param name="WrittenBytes">Its segments' bytes, its holes skipped (<see cref="RestoreSpace.WrittenBytes"/>); none for a folder.</param>
/// <param name="Captured">The metadata captured with it.</param>
/// <param name="Owner">The name of the owner it was captured under, which the plan resolves on the target.</param>
/// <param name="Group">The name of the group it was captured under, which the plan resolves on the target.</param>
/// <param name="Mode">Its captured POSIX mode, whose set-id bits depend on the ownership given back.</param>
/// <param name="Attributes">The kinds of extended attribute it carries that are not written back everywhere.</param>
public sealed record RestoreItemFacts(
    EntryKind Kind,
    ulong WrittenBytes,
    CapturedMetadata Captured,
    string? Owner = null,
    string? Group = null,
    uint? Mode = null,
    ExtendedAttributeKinds Attributes = ExtendedAttributeKinds.None);

/// <summary>
/// Which captured metadata a restore writes back, and saying so where it
/// does not (FR-RST-003, FR-RST-004; ADR-0083, ADR-0084, ADR-0085,
/// ADR-0086, ADR-0087, ADR-0101; architecture 06 §3).
/// </summary>
/// <remarks>
/// <para>
/// The rule is the executor's, and a folder it makes is held to it as a file
/// is: modification and access times everywhere, a creation time where the
/// target can set one, and permissions where the target applies POSIX
/// metadata. Owner and group come back where the target applies POSIX
/// metadata, each where its name resolves there and the restoring account
/// may give it (<see cref="RestoreAccount"/>), so that part of the rule is
/// decided item by item. A set-user-id or set-group-id bit is kept only
/// where the owner or group it goes with was given back. Extended
/// attributes come back where the target writes them, each alone, with the
/// exceptions <see cref="Withheld"/> names. Nothing else is written back
/// yet. A symlink the run makes is held to the same rule, onto the link
/// itself, where the platform has a call that does not follow it: its
/// permissions only where the platform keeps a link's own, and on Linux,
/// which gives every link the same ones, a link's captured permissions land
/// only where they are those (<see cref="LinkPermissionsLand"/>).
/// The plan predicts by that rule. The receipt records what each write did
/// (ADR-0084), which the rule cannot know in advance: a volume may refuse a
/// write the platform supports, a captured time may lie beyond what a file
/// can carry, and a folder already at the destination keeps its own.
/// </para>
/// <para>
/// Alternate streams are never applied, whatever a profile claims, because
/// the executor writes none. The profile's flag decides whether the file
/// counts as degraded; this records what is true.
/// </para>
/// </remarks>
public static class RestoreMetadata
{
    /// <summary>The set-user-id bit of a POSIX mode.</summary>
    private const uint SetUserId = 0x800;

    /// <summary>The set-group-id bit of a POSIX mode.</summary>
    private const uint SetGroupId = 0x400;

    /// <summary>The bits of a POSIX mode a restore writes: permissions, set-id and sticky.</summary>
    private const uint ModeBits = 0xFFF;

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
    private static readonly (CapturedMetadata Attribute, string Name)[] Vocabulary =
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
    /// The attributes the executor writes back for an entry of
    /// <paramref name="kind"/> on <paramref name="target"/>: the same for a
    /// folder it makes as for a file it lands, what the platform's calls that
    /// never follow a link can write for a symlink it makes, and nothing for
    /// anything else. Extended attributes are counted written back where the
    /// target writes them, though <see cref="Withheld"/> keeps some of them
    /// back item by item, as ownership is decided item by item, and as a
    /// link's permissions are on Linux (<see cref="LinkPermissionsLand"/>).
    /// </summary>
    public static CapturedMetadata Applied(EntryKind kind, RestoreTargetProfile target)
    {
        ThrowHelper.ThrowIfNull(target);

        if (kind == EntryKind.Symlink)
        {
            return AppliedToALink(target);
        }

        if (kind is not (EntryKind.File or EntryKind.DirectoryPlaceholder))
        {
            return CapturedMetadata.None;
        }

        var applied = CapturedMetadata.ModifiedAt | CapturedMetadata.AccessedAt;
        if (target.SupportsCreationTimes)
        {
            applied |= CapturedMetadata.CreatedAt;
        }

        if (target.SupportsPosixMetadata && !OperatingSystem.IsWindows())
        {
            applied |= CapturedMetadata.PosixMode;
        }

        if (target.SupportsExtendedAttributes && ExtendedAttributes.CanSet)
        {
            applied |= CapturedMetadata.ExtendedAttributes;
        }

        return applied;
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
    /// What the executor writes onto a symlink it makes, by calls that never
    /// follow it: owner and group as for a file, item by item; extended
    /// attributes where the target writes them; permissions where the target
    /// applies POSIX metadata, item by item on Linux; modification and access
    /// times where the platform sets a link's own; and a creation time only on
    /// macOS, the one platform whose call for it does not follow a link.
    /// </summary>
    private static CapturedMetadata AppliedToALink(RestoreTargetProfile target)
    {
        if (!target.SupportsSymlinks || OperatingSystem.IsWindows())
        {
            return CapturedMetadata.None;
        }

        var applied = CapturedMetadata.None;
        if (SymlinkMetadata.CanSetTimes)
        {
            applied |= CapturedMetadata.ModifiedAt | CapturedMetadata.AccessedAt;
        }

        if (target.SupportsCreationTimes && OperatingSystem.IsMacOS())
        {
            applied |= CapturedMetadata.CreatedAt;
        }

        if (target.SupportsPosixMetadata)
        {
            applied |= CapturedMetadata.PosixMode;
        }

        if (target.SupportsExtendedAttributes && ExtendedAttributes.CanSet)
        {
            applied |= CapturedMetadata.ExtendedAttributes;
        }

        return applied;
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
    /// snapshot (<see cref="RestoreTargetProfile.CapturedHere"/>): anywhere
    /// else the number may be someone else.
    /// </para>
    /// <para>
    /// On Linux only root writes the security and trusted namespaces, so a
    /// restore that is not root leaves them, as its plan said. A security
    /// policy may let an account relabel its own file; a restore by that
    /// account still leaves the label the policy gave the file it made.
    /// </para>
    /// </remarks>
    internal static string? Withheld(ExtendedAttributeEntry attribute, RestoreTargetProfile target, bool link = false)
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
    /// The receipt's names for what was captured and is not applied by the
    /// rule, in the format's order, or null when nothing captured is left out.
    /// </summary>
    public static IReadOnlyList<string>? NotApplied(CapturedMetadata captured, EntryKind kind, RestoreTargetProfile target) =>
        NotApplied(captured, Applied(kind, target));

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

    /// <summary>
    /// What the plan declares about metadata beyond the planner's own lines:
    /// each attribute the tree carries and <paramref name="target"/> will
    /// not get back, with how many files and how many folders carry it, and
    /// only when some do.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="facts">What the plan probe read of each item's manifest.</param>
    /// <param name="target">The target.</param>
    /// <remarks>
    /// <para>
    /// Ownership is declared for two reasons, apart, because they have
    /// different remedies. A name that resolves to an account or group the
    /// restoring account may not give needs root or CAP_CHOWN. A name that
    /// resolves to nothing on the target needs an account no privilege
    /// creates. A target without an account gives no ownership, and says
    /// what giving it would need.
    /// </para>
    /// <para>
    /// A set-id bit whose owner or group will not be given back is dropped
    /// (<see cref="PermittedMode"/>), and declared on its own line, because
    /// the file lands with permissions other than those captured.
    /// </para>
    /// <para>
    /// On a target that writes extended attributes, those the rule withholds
    /// (<see cref="Withheld"/>) are declared on lines of their own, one a
    /// reason: ACLs that name accounts by number where this installation did
    /// not capture the snapshot, every ACL where the target keeps none, and on
    /// Linux the namespaces only root writes, to an account that is not root.
    /// An ACL's line says the group keeps no more than the ACL gave it
    /// (<see cref="WithoutAcl"/>).
    /// </para>
    /// <para>
    /// What the planner already says is not said twice. On a target that
    /// applies no POSIX metadata, its line covers permissions and ownership.
    /// Its alternate-streams line covers streams. Symlinks on a target that
    /// cannot create them are reported skipped by its symlink line, so
    /// their metadata goes unmentioned here.
    /// </para>
    /// <para>
    /// A symlink the target makes is counted on the lines a file and a folder
    /// are, by the rule <see cref="Applied"/> gives it. Two lines are a
    /// link's alone: permissions captured on a link that Linux, which gives
    /// every link the same ones, cannot carry (<see cref="LinkPermissionsLand"/>),
    /// and the user-namespace attributes and ACLs Linux keeps on no link.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<MetadataDegradation> Declare(
        RestorePlan plan, IReadOnlyDictionary<ObjectId, RestoreItemFacts> facts, RestoreTargetProfile target)
    {
        ThrowHelper.ThrowIfNull(plan);
        ThrowHelper.ThrowIfNull(facts);
        ThrowHelper.ThrowIfNull(target);

        var attributes = new Dictionary<CapturedMetadata, Tally>();
        var ownership = new Tally();
        var unknown = new Tally();
        var setIds = new Tally();
        var numbered = new Tally();
        var unkept = new Tally();
        var privileged = new Tally();
        var linkPermissions = new Tally();
        var linkAttributes = new Tally();
        var names = new Names();

        foreach (var item in plan.Items)
        {
            if (item.Kind is not (EntryKind.File or EntryKind.DirectoryPlaceholder or EntryKind.Symlink)
                || !facts.TryGetValue(item.ObjectId, out var known))
            {
                continue;
            }

            // A link the target cannot make is skipped, which the planner's
            // own line says. One it makes is held to the rule a file is.
            var link = item.Kind == EntryKind.Symlink;
            if (link && !target.SupportsSymlinks)
            {
                continue;
            }

            var (given, unresolved) = Ownership(known.Owner, known.Group, target, names);
            var applied = Applied(item.Kind, target);
            if (link
                && (applied & CapturedMetadata.PosixMode) != 0
                && known.Mode is { } linkMode
                && !LinkPermissionsLand(linkMode))
            {
                applied &= ~CapturedMetadata.PosixMode;
                linkPermissions.Add(item.Kind);
            }

            if ((applied & CapturedMetadata.PosixMode) != 0
                && known.Mode is { } mode
                && PermittedMode(mode, given) != (mode & ModeBits))
            {
                setIds.Add(item.Kind);
            }

            if ((applied & CapturedMetadata.ExtendedAttributes) != 0)
            {
                const ExtendedAttributeKinds Acls = ExtendedAttributeKinds.NumberedAcl | ExtendedAttributeKinds.AnonymousAcl;
                if (!OperatingSystem.IsLinux())
                {
                    if ((known.Attributes & Acls) != 0)
                    {
                        unkept.Add(item.Kind);
                    }
                }
                else
                {
                    if (link && (known.Attributes & (Acls | ExtendedAttributeKinds.User)) != 0)
                    {
                        linkAttributes.Add(item.Kind);
                    }

                    if (!link && !target.CapturedHere && known.Attributes.HasFlag(ExtendedAttributeKinds.NumberedAcl))
                    {
                        numbered.Add(item.Kind);
                    }

                    if (target.Account is not { IsSuperuser: true } && known.Attributes.HasFlag(ExtendedAttributeKinds.Privileged))
                    {
                        privileged.Add(item.Kind);
                    }
                }
            }

            var missing = known.Captured & ~(applied | given);
            if (missing == CapturedMetadata.None)
            {
                continue;
            }

            var owned = missing & (CapturedMetadata.Owner | CapturedMetadata.Group);
            if ((owned & ~unresolved) != 0)
            {
                ownership.Add(item.Kind);
            }

            if ((owned & unresolved) != 0)
            {
                unknown.Add(item.Kind);
            }

            foreach (var (attribute, _) in Vocabulary.Where(entry => missing.HasFlag(entry.Attribute)))
            {
                if (!attributes.TryGetValue(attribute, out var carried))
                {
                    attributes[attribute] = carried = new Tally();
                }

                carried.Add(item.Kind);
            }
        }

        var declared = new List<MetadataDegradation>();
        void Add(string capability, Tally? count, string detail)
        {
            if (count is { Any: true })
            {
                declared.Add(new MetadataDegradation(capability, detail));
            }
        }

        if (target.SupportsPosixMetadata)
        {
            Add("ownership", ownership, string.Create(CultureInfo.InvariantCulture,
                $"Ownership captured on {ownership} will not be applied, so they will belong to the account "
                + $"running the restore; applying it needs root or CAP_CHOWN."));
            Add("unknown-accounts", unknown, string.Create(CultureInfo.InvariantCulture,
                $"Ownership captured on {unknown} names an account or group this machine does not have; "
                + $"that part of it will not be applied."));
            Add("set-id-bits", setIds, string.Create(CultureInfo.InvariantCulture,
                $"Permissions captured on {setIds} will lose a set-user-id or set-group-id bit, because "
                + $"the owner or group the bit goes with will not be given back."));
            Add("symlink-permissions", linkPermissions, string.Create(CultureInfo.InvariantCulture,
                $"Permissions captured on {linkPermissions} will not be applied; this target gives every symlink "
                + $"the same permissions and has no call that changes them."));
        }

        var descriptors = attributes.GetValueOrDefault(CapturedMetadata.SecurityDescriptor);
        Add("security-descriptors", descriptors, string.Create(CultureInfo.InvariantCulture,
            $"Security descriptors captured on {descriptors} will not be applied; they will take the "
            + $"permissions of the folder they land in."));

        var extended = attributes.GetValueOrDefault(CapturedMetadata.ExtendedAttributes);
        Add("extended-attributes", extended, string.Create(CultureInfo.InvariantCulture,
            $"Extended attributes captured on {extended} will not be written back."));
        Add("numbered-acls", numbered, string.Create(CultureInfo.InvariantCulture,
            $"Access control lists captured on {numbered} name accounts by number, and this installation did not "
            + $"capture them; a number may belong to another account here, so they will not be applied, and the "
            + $"group keeps no more access than the lists gave it."));
        Add("posix-acls", unkept, string.Create(CultureInfo.InvariantCulture,
            $"Access control lists captured on {unkept} will not be applied, because this target keeps no POSIX "
            + $"access control lists; the group keeps no more access than the lists gave it."));
        Add("privileged-attributes", privileged, string.Create(CultureInfo.InvariantCulture,
            $"Extended attributes in the security and trusted namespaces, captured on {privileged}, will not be "
            + $"applied; writing them needs root."));
        Add("symlink-attributes", linkAttributes, string.Create(CultureInfo.InvariantCulture,
            $"Extended attributes captured on {linkAttributes} will not all be applied; Linux keeps no "
            + $"user-namespace attribute and no access control list on a symlink."));

        var created = attributes.GetValueOrDefault(CapturedMetadata.CreatedAt);
        Add("creation-times", created, string.Create(CultureInfo.InvariantCulture,
            $"Creation times captured on {created} will not be applied; this target cannot set them."));

        var flags = attributes.GetValueOrDefault(CapturedMetadata.FileAttributes);
        Add("file-attributes", flags, string.Create(CultureInfo.InvariantCulture,
            $"File attributes (read-only, hidden, system, archive) captured on {flags} will not be applied."));

        return declared;
    }

    /// <summary>
    /// A receipt's record of what was not applied, one line an attribute
    /// with how many items it was left off, in the format's order.
    /// </summary>
    /// <param name="items">The receipt's items.</param>
    /// <param name="target">
    /// The target, for the privilege ownership would need, said only to an
    /// account without it: one that has it was refused for another reason.
    /// </param>
    public static IReadOnlyList<string> Summarise(IEnumerable<ReceiptItem> items, RestoreTargetProfile target)
    {
        ThrowHelper.ThrowIfNull(items);
        ThrowHelper.ThrowIfNull(target);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in items.SelectMany(item => item.NotApplied ?? []))
        {
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        return
        [
            .. Vocabulary
                .Where(entry => counts.ContainsKey(entry.Name))
                .Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Name} not applied to {counts[entry.Name]} item(s)")
                    + (entry.Attribute is CapturedMetadata.Owner or CapturedMetadata.Group
                        && target.SupportsPosixMetadata
                        && target.Account is not { MayGiveFilesAway: true }
                        ? "; applying ownership needs root or CAP_CHOWN"
                        : string.Empty)),
        ];
    }

    /// <summary>
    /// Which halves of a file's or folder's captured ownership the target
    /// gives back, and which name nothing on the target: given where a name
    /// resolves and the restoring account may give what it resolves to.
    /// </summary>
    private static (CapturedMetadata Given, CapturedMetadata Unresolved) Ownership(
        string? owner, string? group, RestoreTargetProfile target, Names names)
    {
        if (!target.SupportsPosixMetadata || OperatingSystem.IsWindows() || target.Account is not { } account)
        {
            return (CapturedMetadata.None, CapturedMetadata.None);
        }

        var given = CapturedMetadata.None;
        var unresolved = CapturedMetadata.None;
        if (owner is not null)
        {
            if (names.User(owner, account) is not { } userId)
            {
                unresolved |= CapturedMetadata.Owner;
            }
            else if (account.MayGiveTo(userId))
            {
                given |= CapturedMetadata.Owner;
            }
        }

        if (group is not null)
        {
            if (names.Group(group, account) is not { } groupId)
            {
                unresolved |= CapturedMetadata.Group;
            }
            else if (account.MayGiveToGroup(groupId))
            {
                given |= CapturedMetadata.Group;
            }
        }

        return (given, unresolved);
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

    /// <summary>
    /// How many files, folders and symlinks a declaration covers, said the way
    /// a person reads it: "2 file(s)", "1 symlink(s)", or those it covers
    /// joined, as in "2 file(s), 1 folder(s) and 1 symlink(s)".
    /// </summary>
    private sealed class Tally
    {
        private int _files;
        private int _folders;
        private int _links;

        /// <summary>Whether it covers anything at all.</summary>
        public bool Any => _files + _folders + _links > 0;

        /// <summary>Counts one item of <paramref name="kind"/>: a folder, a symlink, or else a file.</summary>
        public void Add(EntryKind kind)
        {
            switch (kind)
            {
                case EntryKind.DirectoryPlaceholder:
                    _folders++;
                    break;
                case EntryKind.Symlink:
                    _links++;
                    break;
                default:
                    _files++;
                    break;
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            List<string> parts = [];
            if (_files > 0 || _folders + _links == 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{_files} file(s)"));
            }

            if (_folders > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{_folders} folder(s)"));
            }

            if (_links > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{_links} symlink(s)"));
            }

            return parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}";
        }
    }

    /// <summary>The names one plan has resolved, so a tree of one owner asks the system once.</summary>
    private sealed class Names
    {
        private readonly Dictionary<string, uint?> _users = new(StringComparer.Ordinal);
        private readonly Dictionary<string, uint?> _groups = new(StringComparer.Ordinal);

        public uint? User(string name, RestoreAccount account) => Resolved(_users, name, account.ResolveUser);

        public uint? Group(string name, RestoreAccount account) => Resolved(_groups, name, account.ResolveGroup);

        private static uint? Resolved(Dictionary<string, uint?> known, string name, Func<string, uint?> resolve)
        {
            if (!known.TryGetValue(name, out var id))
            {
                known[name] = id = resolve(name);
            }

            return id;
        }
    }
}
