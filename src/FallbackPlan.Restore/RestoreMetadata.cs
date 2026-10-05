using Bodu;
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
/// What a restore knows of one item from its manifest: the bytes restoring
/// it writes, and what was captured with it.
/// </summary>
/// <param name="Kind">The entry's kind.</param>
/// <param name="WrittenBytes">Its segments' bytes, its holes skipped (<see cref="RestoreSpace.WrittenBytes"/>).</param>
/// <param name="Captured">The metadata captured with it.</param>
/// <param name="Owner">The name of the owner it was captured under, which the plan resolves on the target.</param>
/// <param name="Group">The name of the group it was captured under, which the plan resolves on the target.</param>
/// <param name="Mode">Its captured POSIX mode, whose set-id bits depend on the ownership given back.</param>
public sealed record RestoreItemFacts(
    EntryKind Kind,
    ulong WrittenBytes,
    CapturedMetadata Captured,
    string? Owner = null,
    string? Group = null,
    uint? Mode = null);

/// <summary>
/// Which captured metadata a restore writes back, and saying so where it
/// does not (FR-RST-003, FR-RST-004; ADR-0083, ADR-0084, ADR-0085;
/// architecture 06 §3).
/// </summary>
/// <remarks>
/// <para>
/// The rule is the executor's: a file's modification and access times
/// everywhere, its creation time where the target can set one, and its
/// permissions where the target applies POSIX metadata. Its owner and group
/// come back where the target applies POSIX metadata, each where its name
/// resolves there and the restoring account may give it
/// (<see cref="RestoreAccount"/>), so that part of the rule is decided file
/// by file. A set-user-id or set-group-id bit is kept only where the owner
/// or group it runs as was given back. Nothing else is written back yet. A
/// symlink is created with none of its own metadata. The plan predicts by
/// that rule. The receipt records what each write did (ADR-0084), which the
/// rule cannot know in advance: a volume may refuse a write the platform
/// supports, and a captured time may lie beyond what a file can carry.
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

        if (metadata.WindowsSecurityDescriptor is not null)
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

    /// <summary>The attributes the executor writes back for an entry of <paramref name="kind"/> on <paramref name="target"/>.</summary>
    public static CapturedMetadata Applied(EntryKind kind, RestoreTargetProfile target)
    {
        ThrowHelper.ThrowIfNull(target);

        if (kind != EntryKind.File)
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

        return applied;
    }

    /// <summary>
    /// The permissions a file captured with <paramref name="mode"/> may be
    /// given once the halves of its ownership in <paramref name="given"/>
    /// have landed (ADR-0085). A set-user-id file runs as its owner, so its
    /// bit is kept only with the owner it was captured under; on any other
    /// owner it would run as whoever restored it. A set-group-id bit is kept
    /// only with its group, likewise.
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
    /// not get back, with how many files carry it, and only when some do.
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
    /// What the planner already says is not said twice. On a target that
    /// applies no POSIX metadata, its line covers permissions and ownership.
    /// Its alternate-streams line covers streams. Symlinks on a target that
    /// cannot create them are reported skipped by its symlink line, so
    /// their metadata goes unmentioned here.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<MetadataDegradation> Declare(
        RestorePlan plan, IReadOnlyDictionary<ObjectId, RestoreItemFacts> facts, RestoreTargetProfile target)
    {
        ThrowHelper.ThrowIfNull(plan);
        ThrowHelper.ThrowIfNull(facts);
        ThrowHelper.ThrowIfNull(target);

        var files = new Dictionary<CapturedMetadata, int>();
        var ownership = 0;
        var unknown = 0;
        var setIds = 0;
        var links = 0;
        var names = new Names();

        foreach (var item in plan.Items)
        {
            if (item.Kind is not (EntryKind.File or EntryKind.Symlink)
                || !facts.TryGetValue(item.ObjectId, out var known))
            {
                continue;
            }

            var (given, unresolved) = item.Kind == EntryKind.File
                ? Ownership(known.Owner, known.Group, target, names)
                : (CapturedMetadata.None, CapturedMetadata.None);
            if (item.Kind == EntryKind.File
                && (Applied(item.Kind, target) & CapturedMetadata.PosixMode) != 0
                && known.Mode is { } mode
                && PermittedMode(mode, given) != (mode & ModeBits))
            {
                setIds++;
            }

            var missing = known.Captured & ~(Applied(item.Kind, target) | given);
            if (missing == CapturedMetadata.None)
            {
                continue;
            }

            if (item.Kind == EntryKind.Symlink)
            {
                links += target.SupportsSymlinks ? 1 : 0;
                continue;
            }

            var owned = missing & (CapturedMetadata.Owner | CapturedMetadata.Group);
            ownership += (owned & ~unresolved) != 0 ? 1 : 0;
            unknown += (owned & unresolved) != 0 ? 1 : 0;

            foreach (var (attribute, _) in Vocabulary.Where(entry => missing.HasFlag(entry.Attribute)))
            {
                files[attribute] = files.GetValueOrDefault(attribute) + 1;
            }
        }

        var declared = new List<MetadataDegradation>();
        void Add(string capability, int count, string detail)
        {
            if (count > 0)
            {
                declared.Add(new MetadataDegradation(capability, detail));
            }
        }

        if (target.SupportsPosixMetadata)
        {
            Add("ownership", ownership, string.Create(CultureInfo.InvariantCulture,
                $"Ownership captured on {ownership} file(s) will not be applied, so they will belong to the account "
                + $"running the restore; applying it needs root or CAP_CHOWN."));
            Add("unknown-accounts", unknown, string.Create(CultureInfo.InvariantCulture,
                $"Ownership captured on {unknown} file(s) names an account or group this machine does not have; "
                + $"that part of it will not be applied."));
            Add("set-id-bits", setIds, string.Create(CultureInfo.InvariantCulture,
                $"Permissions captured on {setIds} file(s) will lose a set-user-id or set-group-id bit, because "
                + $"the owner or group it runs as will not be given back."));
        }

        var descriptors = files.GetValueOrDefault(CapturedMetadata.SecurityDescriptor);
        Add("security-descriptors", descriptors, string.Create(CultureInfo.InvariantCulture,
            $"Security descriptors captured on {descriptors} file(s) will not be applied; they will take the "
            + $"permissions of the folder they land in."));

        var attributes = files.GetValueOrDefault(CapturedMetadata.ExtendedAttributes);
        Add("extended-attributes", attributes, string.Create(CultureInfo.InvariantCulture,
            $"Extended attributes captured on {attributes} file(s) will not be written back."));

        var created = files.GetValueOrDefault(CapturedMetadata.CreatedAt);
        Add("creation-times", created, string.Create(CultureInfo.InvariantCulture,
            $"Creation times captured on {created} file(s) will not be applied; this target cannot set them."));

        var flags = files.GetValueOrDefault(CapturedMetadata.FileAttributes);
        Add("file-attributes", flags, string.Create(CultureInfo.InvariantCulture,
            $"File attributes (read-only, hidden, system, archive) captured on {flags} file(s) will not be applied."));

        Add("symlink-metadata", links, string.Create(CultureInfo.InvariantCulture,
            $"Metadata captured on {links} symlink(s) will not be applied; each link is created with the platform's defaults."));

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
    /// Which halves of a file's captured ownership the target gives back,
    /// and which name nothing on the target: given where a name resolves
    /// and the restoring account may give what it resolves to.
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
