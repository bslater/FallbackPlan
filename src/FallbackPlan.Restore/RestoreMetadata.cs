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
public sealed record RestoreItemFacts(EntryKind Kind, ulong WrittenBytes, CapturedMetadata Captured);

/// <summary>
/// Which captured metadata a restore writes back, and saying so where it
/// does not (FR-RST-003, FR-RST-004; ADR-0083, ADR-0084; architecture 06 §3).
/// </summary>
/// <remarks>
/// <para>
/// The rule is the executor's: a file's modification and access times
/// everywhere, its creation time where the target can set one, and its
/// permissions where the target applies POSIX metadata. Nothing else is
/// written back yet. A symlink is created with none of its own metadata.
/// The plan predicts by that rule. The receipt records what each write did
/// (ADR-0084), which the rule cannot know in advance: a volume may refuse a
/// write the platform supports, and a captured time may lie beyond what a
/// file can carry.
/// </para>
/// <para>
/// Alternate streams are never applied, whatever a profile claims, because
/// the executor writes none. The profile's flag decides whether the file
/// counts as degraded; this records what is true.
/// </para>
/// </remarks>
public static class RestoreMetadata
{
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
    /// What the planner already says is not said twice. On a target that
    /// applies no POSIX metadata, its line covers permissions and ownership.
    /// Its alternate-streams line covers streams. Symlinks on a target that
    /// cannot create them are reported skipped by its symlink line, so
    /// their metadata goes unmentioned here.
    /// </remarks>
    public static IReadOnlyList<MetadataDegradation> Declare(
        RestorePlan plan, IReadOnlyDictionary<ObjectId, RestoreItemFacts> facts, RestoreTargetProfile target)
    {
        ThrowHelper.ThrowIfNull(plan);
        ThrowHelper.ThrowIfNull(facts);
        ThrowHelper.ThrowIfNull(target);

        var files = new Dictionary<CapturedMetadata, int>();
        var ownership = 0;
        var links = 0;

        foreach (var item in plan.Items)
        {
            if (item.Kind is not (EntryKind.File or EntryKind.Symlink)
                || !facts.TryGetValue(item.ObjectId, out var known))
            {
                continue;
            }

            var missing = known.Captured & ~Applied(item.Kind, target);
            if (missing == CapturedMetadata.None)
            {
                continue;
            }

            if (item.Kind == EntryKind.Symlink)
            {
                links += target.SupportsSymlinks ? 1 : 0;
                continue;
            }

            if ((missing & (CapturedMetadata.Owner | CapturedMetadata.Group)) != 0)
            {
                ownership++;
            }

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
    /// <param name="target">The target, for the privilege ownership would need.</param>
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
                    + (entry.Attribute is CapturedMetadata.Owner or CapturedMetadata.Group && target.SupportsPosixMetadata
                        ? "; applying ownership needs root or CAP_CHOWN"
                        : string.Empty)),
        ];
    }
}
