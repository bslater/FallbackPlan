using Bodu;
using System.Globalization;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Restore;

/// <summary>
/// What a restore knows of one item from its manifest: the bytes restoring
/// it writes, what was captured with it, and how much of it a resume
/// already holds.
/// </summary>
/// <param name="Kind">The entry's kind.</param>
/// <param name="WrittenBytes">Its segments' bytes, its holes skipped (<see cref="RestoreSpace.WrittenBytes"/>); none for a folder.</param>
/// <param name="Captured">The metadata captured with it.</param>
/// <param name="Owner">The name of the owner it was captured under, which the plan resolves on the target.</param>
/// <param name="Group">The name of the group it was captured under, which the plan resolves on the target.</param>
/// <param name="Mode">Its captured POSIX mode, whose set-id bits depend on the ownership given back.</param>
/// <param name="Attributes">The kinds of extended attribute it carries that are not written back everywhere.</param>
/// <param name="HardLink">What it must agree on with its hard-link group's first name to share that name's file, or null for a file of no group (ADR-0102).</param>
/// <param name="HeldBytes">
/// What the run a plan resumes already holds of its written bytes: all of
/// them for a file it landed, and for one it was cut short in what the part
/// it kept proves (<see cref="RestoreSpace.HeldBytes"/>; ADR-0132,
/// ADR-0136). None for a plan of a run afresh.
/// </param>
/// <param name="Accounts">
/// The SIDs its captured Windows security descriptor names, which the plan
/// asks the target whether it knows (ADR-0176), or null where it carries none
/// or one that does not read.
/// </param>
/// <param name="FileAttributes">
/// Its captured Windows attribute bits, whose bits no restore sets the plan
/// names (ADR-0175 Amendment 1), or null where none were captured.
/// </param>
public sealed record RestoreItemFacts(
    EntryKind Kind,
    ulong WrittenBytes,
    CapturedMetadata Captured,
    string? Owner = null,
    string? Group = null,
    uint? Mode = null,
    ExtendedAttributeKinds Attributes = ExtendedAttributeKinds.None,
    string? HardLink = null,
    ulong HeldBytes = 0,
    IReadOnlyList<string>? Accounts = null,
    uint? FileAttributes = null);

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
/// exceptions <c>MetadataRules.Withheld</c> names. On Windows a file's and a
/// folder's attribute bits come back (ADR-0175), and their security
/// descriptor where the target knows every account it names (ADR-0176),
/// which is decided item by item too. Nothing else is written back yet. A
/// symlink the run makes is held to the same rule, onto the link
/// itself, where the platform has a call that does not follow it: its
/// permissions only where the platform keeps a link's own, and on Linux,
/// which gives every link the same ones, a link's captured permissions land
/// only where they are those (<see cref="MetadataRules.LinkPermissionsLand"/>).
/// The plan predicts by that rule. The receipt records what each write did
/// (ADR-0084), which the rule cannot know in advance: a volume may refuse a
/// write the platform supports, a captured time may lie beyond what a file
/// can carry, and a folder already at the destination keeps its own.
/// </para>
/// <para>
/// A file's alternate streams are written where the target keeps them
/// (ADR-0174), each read and proved like a segment. A folder is captured with
/// none.
/// </para>
/// </remarks>
public static class RestoreMetadata
{
    /// <summary>
    /// The attributes the executor writes back for an entry of
    /// <paramref name="kind"/> on <paramref name="target"/>: the same for a
    /// folder it makes as for a file it lands, save the alternate streams only
    /// a file carries, what the platform's calls that
    /// never follow a link can write for a symlink it makes, and nothing for
    /// anything else. Extended attributes are counted written back where the
    /// target writes them, though <c>MetadataRules.Withheld</c> keeps some of them
    /// back item by item, as ownership is decided item by item, and as a
    /// link's permissions are on Linux (<see cref="MetadataRules.LinkPermissionsLand"/>).
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

        if (kind == EntryKind.File && target.SupportsAlternateStreams)
        {
            applied |= CapturedMetadata.AlternateStreams;
        }

        if (target.SupportsFileAttributes)
        {
            applied |= CapturedMetadata.FileAttributes;
        }

        if (target.SupportsSecurityDescriptors)
        {
            applied |= CapturedMetadata.SecurityDescriptor;
        }

        return applied;
    }

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

    /// <summary>
    /// The receipt's names for what was captured and is not applied by the
    /// rule, in the format's order, or null when nothing captured is left out.
    /// </summary>
    public static IReadOnlyList<string>? NotApplied(CapturedMetadata captured, EntryKind kind, RestoreTargetProfile target) =>
        MetadataRules.NotApplied(captured, Applied(kind, target));

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
    /// (<see cref="MetadataRules.PermittedMode"/>), and declared on its own line, because
    /// the file lands with permissions other than those captured.
    /// </para>
    /// <para>
    /// On a target that writes extended attributes, those the rule withholds
    /// (<c>MetadataRules.Withheld</c>) are declared on lines of their own, one a
    /// reason: ACLs that name accounts by number where this installation did
    /// not capture the snapshot, every ACL where the target keeps none, and on
    /// Linux the namespaces only root writes, to an account that is not root.
    /// An ACL's line says the group keeps no more than the ACL gave it
    /// (<c>MetadataRules.WithoutAcl</c>).
    /// </para>
    /// <para>
    /// On a target that writes security descriptors, one is declared where it
    /// names an account the target does not know, on a line of its own,
    /// because no privilege mends that, and with the rest where it does not
    /// read as a descriptor at all (ADR-0176).
    /// </para>
    /// <para>
    /// On a target that writes Windows attribute bits, the items captured with
    /// a bit no restore sets, as compressed or encrypted, are declared on a
    /// line of their own that names those bits, because the rest of their
    /// bits still land (ADR-0175 Amendment 1).
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
    /// every link the same ones, cannot carry (<see cref="MetadataRules.LinkPermissionsLand"/>),
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
        var strangers = new Tally();
        var unsetBits = new Tally();
        var unsetMask = 0u;
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
                && !MetadataRules.LinkPermissionsLand(linkMode))
            {
                applied &= ~CapturedMetadata.PosixMode;
                linkPermissions.Add(item.Kind);
            }

            if ((applied & CapturedMetadata.PosixMode) != 0
                && known.Mode is { } mode
                && MetadataRules.PermittedMode(mode, given) != (mode & MetadataRules.ModeBits))
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

            var apart = CapturedMetadata.None;
            if ((applied & known.Captured & CapturedMetadata.SecurityDescriptor) != 0)
            {
                if (known.Accounts is not { } accounts)
                {
                    applied &= ~CapturedMetadata.SecurityDescriptor;
                }
                else if (accounts.Any(account => !names.Knows(account, target.KnowsAccount)))
                {
                    applied &= ~CapturedMetadata.SecurityDescriptor;
                    apart |= CapturedMetadata.SecurityDescriptor;
                    strangers.Add(item.Kind);
                }
            }

            if ((applied & known.Captured & CapturedMetadata.FileAttributes) != 0
                && known.FileAttributes is { } bits
                && MetadataRules.UnsetFileAttributes(bits).Count > 0)
            {
                applied &= ~CapturedMetadata.FileAttributes;
                apart |= CapturedMetadata.FileAttributes;
                unsetBits.Add(item.Kind);
                unsetMask |= bits;
            }

            var missing = known.Captured & ~(applied | given | apart);
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

            foreach (var (attribute, _) in MetadataRules.Vocabulary.Where(entry => missing.HasFlag(entry.Attribute)))
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
        Add("security-descriptor-accounts", strangers, string.Create(CultureInfo.InvariantCulture,
            $"Security descriptors captured on {strangers} name accounts this machine does not know, so they will "
            + $"not be applied; they will take the permissions of the folder they land in."));

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
        Add("unset-file-attributes", unsetBits, string.Create(CultureInfo.InvariantCulture,
            $"File attributes captured on {unsetBits} include bits no restore sets: "
            + $"{string.Join(", ", MetadataRules.UnsetFileAttributes(unsetMask))}. Those will not be applied; "
            + $"the read-only, hidden, system, archive and not-content-indexed bits will."));

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
            .. MetadataRules.Vocabulary
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
        private readonly Dictionary<string, bool> _accounts = new(StringComparer.Ordinal);

        public uint? User(string name, RestoreAccount account) => Resolved(_users, name, account.ResolveUser);

        public uint? Group(string name, RestoreAccount account) => Resolved(_groups, name, account.ResolveGroup);

        public bool Knows(string account, Func<string, bool> knows)
        {
            if (!_accounts.TryGetValue(account, out var known))
            {
                _accounts[account] = known = knows(account);
            }

            return known;
        }

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
