using Bodu;
using System.Globalization;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;

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
/// <param name="RefusedStreams">
/// How many of its captured alternate streams have a name no stream may be
/// written under (<see cref="AlternateStreams.TryName"/>), which the plan
/// declares (ADR-0174 Amendment 3).
/// </param>
/// <param name="DescriptorOwner">
/// The SID its captured Windows security descriptor names as owner, which
/// the plan asks the target whether its account may give without
/// SeRestorePrivilege (ADR-0176 Amendment 2), or null where it names none.
/// </param>
/// <param name="Access">
/// The access its captured Windows security descriptor's DACL gives each
/// account (<see cref="WindowsAccess"/>), which the plan holds against what
/// its folder would give it where the descriptor is not written (ADR-0178),
/// or null where it carries none that says.
/// </param>
/// <param name="AuditedAccounts">
/// The SIDs the audit list of its captured Windows security descriptor
/// audits, which the writer writes only with SeSecurityPrivilege and where
/// the target knows each (ADR-0176 Amendment 3), or null where it carries
/// none.
/// </param>
/// <param name="LongestStreamName">
/// The bytes in the longest name of its captured alternate streams
/// (<see cref="MetadataRules.LongestStreamName"/>), which a target that keeps
/// streams as attributes takes only within its limit (ADR-0179 Amendment 2).
/// </param>
/// <param name="LargestStream">
/// The length of its largest captured alternate stream
/// (<see cref="MetadataRules.LargestStream"/>), judged likewise.
/// </param>
/// <param name="StreamsOutsideUserNamespace">
/// How many of its captured alternate streams have no name in the user
/// namespace (<see cref="MetadataRules.StreamsOutsideUserNamespace"/>), which
/// a target that keeps streams as attributes and takes names only in a
/// namespace does not keep (ADR-0179).
/// </param>
/// <param name="RefusedAttributeNames">
/// How many of its captured extended attributes are not kept as streams for
/// their names (<see cref="MetadataRules.RefusedAttributeNames"/>), which the
/// plan declares where a target keeps attributes as streams (ADR-0177
/// Amendment 1).
/// </param>
/// <param name="Flags">
/// The flags its manifest keeps in metadata key 11 or names in its capture
/// diagnostics
/// (<see cref="MetadataRules.FileFlagsOf(EntryMetadata, IEnumerable{string})"/>),
/// a folder's from its tree's key 11 (ADR-0180 Amendments 1 and 3), which
/// the plan declares where the target will not set them (Amendment 4).
/// </param>
/// <param name="Junction">
/// Whether it is a link captured as a junction
/// (<see cref="MountPoints.IsJunction"/>), which the plan declares where the
/// target makes none (ADR-0183 Amendment 2).
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
    uint? FileAttributes = null,
    int RefusedStreams = 0,
    string? DescriptorOwner = null,
    int RefusedAttributeNames = 0,
    IReadOnlyDictionary<string, uint>? Access = null,
    IReadOnlyList<string>? AuditedAccounts = null,
    int StreamsOutsideUserNamespace = 0,
    int LongestStreamName = 0,
    ulong LargestStream = 0,
    FileFlagBits Flags = FileFlagBits.None,
    bool Junction = false);

/// <summary>
/// The files and the folders a restore would open wider than they were
/// (ADR-0178): their descriptors will not be written, and what they take where
/// they are made gives some account access their own did not. A run is
/// refused for them unless a person acknowledges it (ADR-0178 Amendment 1;
/// architecture 06 §3).
/// </summary>
/// <param name="Files">How many files.</param>
/// <param name="Folders">
/// The folders their access comes from, each once: for each the nearest that
/// stands above where it is made (ADR-0178 Amendment 3).
/// </param>
/// <param name="Accounts">Every account that gains, by SID, each once.</param>
/// <param name="OpenedFolders">How many folders the restore makes (ADR-0178 Amendment 4).</param>
public sealed record WiderAccess(int Files, IReadOnlyList<string> Folders, IReadOnlyList<string> Accounts, int OpenedFolders = 0)
{
    /// <summary>The plan's <c>wider-access</c> line, which the run's refusal says too.</summary>
    public string Describe()
    {
        var files = string.Create(CultureInfo.InvariantCulture, $"{Files} file(s)");
        var folders = string.Create(CultureInfo.InvariantCulture, $"{OpenedFolders} folder(s)");
        var items = OpenedFolders == 0 ? files : Files == 0 ? folders : $"{files} and {folders}";
        return $"Security descriptors captured on {items} will not be applied, and the folder they take their "
            + $"access from ({string.Join(", ", Folders)}) gives them access their own did not, to "
            + $"{string.Join(", ", Accounts)}.";
    }
}

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
    /// <summary>How many manifests a run asks for together: as many as the executor's own reading ahead.</summary>
    private const int ManifestWave = 1024;

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

        var writesAttributes = target.SupportsExtendedAttributes && ExtendedAttributes.CanSet;
        if (writesAttributes || MetadataRules.KeepsAttributesAsStreams(kind, target.SupportsAlternateStreams, writesAttributes))
        {
            applied |= CapturedMetadata.ExtendedAttributes;
        }

        if (kind == EntryKind.File
            && (target.SupportsAlternateStreams
                || MetadataRules.KeepsStreamsAsAttributes(kind, target.SupportsAlternateStreams, writesAttributes)))
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
    /// times where the platform sets a link's own; and a creation time where
    /// the target keeps one and the platform sets a link's own, on macOS and
    /// Windows; and its attribute bits and its security descriptor where the
    /// target writes them and the platform sets a link's own, on Windows.
    /// </summary>
    private static CapturedMetadata AppliedToALink(RestoreTargetProfile target)
    {
        if (!target.SupportsSymlinks)
        {
            return CapturedMetadata.None;
        }

        var applied = CapturedMetadata.None;
        if (SymlinkMetadata.CanSetTimes)
        {
            applied |= CapturedMetadata.ModifiedAt | CapturedMetadata.AccessedAt;
        }

        if (target.SupportsCreationTimes && SymlinkMetadata.CanSetCreationTime)
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

        if (target.SupportsFileAttributes && SymlinkMetadata.CanSetAttributes)
        {
            applied |= CapturedMetadata.FileAttributes;
        }

        if (target.SupportsSecurityDescriptors && SymlinkMetadata.CanSetSecurityDescriptor)
        {
            applied |= CapturedMetadata.SecurityDescriptor;
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
    /// <param name="destinationOf">
    /// The folder each item is restored into, where the caller knows it, so
    /// a file whose descriptor is not written can be held against what that
    /// folder gives (ADR-0178); null where it does not.
    /// </param>
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
    /// On a target that writes alternate streams, the files captured with a
    /// stream whose name no stream may be written under are declared on a
    /// line of their own, because the executor will not write that stream and
    /// will restore the file degraded (ADR-0174 Amendment 3).
    /// </para>
    /// <para>
    /// The files, folders and links captured with flags the target will not
    /// set are declared on a line of their own that names those flags
    /// (ADR-0180 Amendments 1 and 4), and says where setting immutable and
    /// append-only needs a privilege the account running the restore lacks.
    /// A target that sets every flag an item carries says nothing of it.
    /// </para>
    /// <para>
    /// The links captured as Windows junctions that a target making none
    /// will make as symbolic links to the same path are declared on a line of
    /// their own (ADR-0183 Amendment 2).
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
        RestorePlan plan,
        IReadOnlyDictionary<ObjectId, RestoreItemFacts> facts,
        RestoreTargetProfile target,
        Func<RestorePlanItem, string?>? destinationOf = null)
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
        var refusedOwners = new Tally();
        var auditLists = new Tally();
        var auditStrangers = new Tally();
        var kernelAttributes = new Tally();
        var attributeNames = new Tally();
        var unsetBits = new Tally();
        var unsetMask = 0u;
        var refusedStreams = new Tally();
        var streamsOutside = new Tally();
        var streamsTooLarge = new Tally();
        var fileFlags = new Tally();
        var flagMask = FileFlagBits.None;
        var flagsNeedPrivilege = false;
        var junctions = new Tally();
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

            if (link && known.Junction && !target.MakesJunctions)
            {
                junctions.Add(item.Kind);
            }

            var flagsUnset = known.Flags & ~(link ? target.SettableLinkFlags : target.SettableFlags);
            if (flagsUnset != FileFlagBits.None)
            {
                fileFlags.Add(item.Kind);
                flagMask |= flagsUnset;
                flagsNeedPrivilege |= (flagsUnset & target.FlagsNeedingPrivilege) != FileFlagBits.None;
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
                if (MetadataRules.KeepsAttributesAsStreams(
                    item.Kind, target.SupportsAlternateStreams, target.SupportsExtendedAttributes && ExtendedAttributes.CanSet))
                {
                    if ((known.Attributes & (Acls | ExtendedAttributeKinds.Privileged)) != 0)
                    {
                        kernelAttributes.Add(item.Kind);
                    }

                    if (known.RefusedAttributeNames > 0)
                    {
                        attributeNames.Add(item.Kind);
                    }
                }
                else if (!OperatingSystem.IsLinux())
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
                else
                {
                    // The writer still lands the DACL in each case, as its
                    // receipt says, but the descriptor is not applied.
                    if (!target.HoldsRestorePrivilege
                        && known.DescriptorOwner is { } owner
                        && !names.MayOwn(owner, target.MayOwnBy))
                    {
                        applied &= ~CapturedMetadata.SecurityDescriptor;
                        apart |= CapturedMetadata.SecurityDescriptor;
                        refusedOwners.Add(item.Kind);
                    }

                    if (known.AuditedAccounts is { Count: > 0 } audited)
                    {
                        // No privilege mends an account the machine does not
                        // know, so that is said whatever the account holds.
                        var stranger = audited.Any(account => !names.Knows(account, target.KnowsAccount));
                        if (stranger || !target.HoldsSecurityPrivilege)
                        {
                            applied &= ~CapturedMetadata.SecurityDescriptor;
                            apart |= CapturedMetadata.SecurityDescriptor;
                            (stranger ? auditStrangers : auditLists).Add(item.Kind);
                        }
                    }
                }
            }

            // Nothing compresses a link, so its compressed bit is unset.
            if ((applied & known.Captured & CapturedMetadata.FileAttributes) != 0
                && known.FileAttributes is { } bits
                && MetadataRules.UnsetFileAttributeBits(bits, target.SupportsCompression && item.Kind != EntryKind.Symlink) is var unset and not 0)
            {
                applied &= ~CapturedMetadata.FileAttributes;
                apart |= CapturedMetadata.FileAttributes;
                unsetBits.Add(item.Kind);
                unsetMask |= unset;
            }

            if ((applied & known.Captured & CapturedMetadata.AlternateStreams) != 0
                && target.SupportsAlternateStreams
                && known.RefusedStreams > 0)
            {
                applied &= ~CapturedMetadata.AlternateStreams;
                apart |= CapturedMetadata.AlternateStreams;
                refusedStreams.Add(item.Kind);
            }

            // Kept as attributes, a stream's name is judged as Linux judges
            // an attribute's, not as a stream's path.
            if ((applied & known.Captured & CapturedMetadata.AlternateStreams) != 0
                && !target.SupportsAlternateStreams
                && target.AttributesTakeNamespaces
                && known.StreamsOutsideUserNamespace > 0)
            {
                applied &= ~CapturedMetadata.AlternateStreams;
                apart |= CapturedMetadata.AlternateStreams;
                streamsOutside.Add(item.Kind);
            }

            if ((applied & known.Captured & CapturedMetadata.AlternateStreams) != 0
                && !target.SupportsAlternateStreams
                && MetadataRules.StreamTooLargeForAttribute(
                    known.LongestStreamName, known.LargestStream, target.MaxAttributeNameBytes, target.MaxAttributeValueBytes) is not null)
            {
                applied &= ~CapturedMetadata.AlternateStreams;
                apart |= CapturedMetadata.AlternateStreams;
                streamsTooLarge.Add(item.Kind);
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
        if (Widened(plan, facts, target, destinationOf, names) is { } wider)
        {
            declared.Add(new MetadataDegradation("wider-access", wider.Describe()));
        }

        Add("security-descriptor-owners", refusedOwners, string.Create(CultureInfo.InvariantCulture,
            $"Security descriptors captured on {refusedOwners} name an owner the account running the restore may "
            + $"not give: giving it needs SeRestorePrivilege, which the account does not hold. Their access control "
            + $"lists will be applied; their owner and group will not, so they will belong to that account."));
        Add("security-descriptor-audit-lists", auditLists, string.Create(CultureInfo.InvariantCulture,
            $"Security descriptors captured on {auditLists} carry an audit list the account running the restore may "
            + $"not write: writing it needs SeSecurityPrivilege, which the account does not hold. Their audit lists "
            + $"will not be applied; the rest of each will be, unless another line says otherwise."));
        Add("security-descriptor-audit-accounts", auditStrangers, string.Create(CultureInfo.InvariantCulture,
            $"Security descriptors captured on {auditStrangers} carry an audit list naming accounts this machine does "
            + $"not know, so their audit lists will not be applied; the rest of each will be, unless another line says "
            + $"otherwise."));

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
        Add("kernel-attributes", kernelAttributes, string.Create(CultureInfo.InvariantCulture,
            $"Extended attributes in Linux's security and trusted namespaces, and POSIX access control lists, captured "
            + $"on {kernelAttributes}, will not be kept as streams: only Linux's kernel reads them. Their files' other "
            + $"attributes will be."));
        Add("attribute-stream-names", attributeNames, string.Create(CultureInfo.InvariantCulture,
            $"Extended attributes captured on {attributeNames} include one whose name no stream may have, or whose "
            + $"name the file's own stream or another of its attributes already has as Windows compares names; it will "
            + $"not be kept as a stream. Their files' other attributes will be."));
        Add("symlink-attributes", linkAttributes, string.Create(CultureInfo.InvariantCulture,
            $"Extended attributes captured on {linkAttributes} will not all be applied; Linux keeps no "
            + $"user-namespace attribute and no access control list on a symlink."));

        var created = attributes.GetValueOrDefault(CapturedMetadata.CreatedAt);
        Add("creation-times", created, string.Create(CultureInfo.InvariantCulture,
            $"Creation times captured on {created} will not be applied; this target cannot set them."));

        var flags = attributes.GetValueOrDefault(CapturedMetadata.FileAttributes);
        Add("file-attributes", flags, string.Create(CultureInfo.InvariantCulture,
            $"File attributes (read-only, hidden, system, archive) captured on {flags} will not be applied."));
        Add("alternate-stream-names", refusedStreams, string.Create(CultureInfo.InvariantCulture,
            $"Alternate streams captured on {refusedStreams} include one whose name no stream may be written "
            + $"under: joined to the file's path, it would name another file or the file itself. That stream will "
            + $"not be written, and the file will be restored degraded."));
        Add("alternate-stream-attribute-names", streamsOutside, string.Create(CultureInfo.InvariantCulture,
            $"Alternate streams captured on {streamsOutside} include one with no name in the user namespace, the "
            + $"only one Linux gives a file's own attributes, so it will not be kept as an attribute, and the file "
            + $"will be restored degraded."));
        Add("alternate-stream-attribute-sizes", streamsTooLarge, string.Create(CultureInfo.InvariantCulture,
            $"Alternate streams captured on {streamsTooLarge} include one larger than this target takes in an attribute "
            + $"({string.Join(", ", AttributeLimitsOf(target))}), so it will not be kept as an attribute, and the file "
            + $"will be restored degraded."));
        Add("unset-file-attributes", unsetBits, string.Create(CultureInfo.InvariantCulture,
            $"File attributes captured on {unsetBits} include bits no restore sets: "
            + $"{string.Join(", ", MetadataRules.UnsetFileAttributes(unsetMask))}. Those will not be "
            + $"applied; the read-only, hidden, system, archive and not-content-indexed bits will"
            + $"{(target.SupportsCompression ? ", and so will compression" : string.Empty)}."));
        var flagsWhy = flagsNeedPrivilege
            ? "; setting immutable and append-only needs root or CAP_LINUX_IMMUTABLE, which the account running the restore does not hold"
            : string.Empty;
        Add("file-flags", fileFlags, string.Create(CultureInfo.InvariantCulture,
            $"File flags captured on {fileFlags} will not be applied: "
            + $"{string.Join(", ", MetadataRules.FileFlagNamesOf(flagMask))}. This target does not set them{flagsWhy}."));
        Add("junctions", junctions, string.Create(CultureInfo.InvariantCulture,
            $"{junctions} captured as Windows junctions will be made as symbolic links to the same path: this target makes no junction."));

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
    /// The folder each item of <paramref name="slices"/> lands in: its path
    /// resolved under its slice's folder, as the executor resolves it
    /// (ADR-0178 Amendment 3). Null for an item none of them holds, and for
    /// one whose path the executor would refuse.
    /// </summary>
    /// <param name="slices">The restore's slices, each with its plan and where it writes.</param>
    public static Func<RestorePlanItem, string?> DestinationsOf(IReadOnlyList<RestoreSlice> slices)
    {
        ThrowHelper.ThrowIfNull(slices);

        var into = new Dictionary<ObjectId, string>();
        foreach (var slice in slices)
        {
            foreach (var item in slice.Plan.Items)
            {
                if (RestorePaths.TryResolve(Path.GetFullPath(slice.OutputDirectory), item.Path, out var landing, out _)
                    && Path.GetDirectoryName(landing) is { } folder)
                {
                    into.TryAdd(item.ObjectId, folder);
                }
            }
        }

        return item => into.GetValueOrDefault(item.ObjectId);
    }

    /// <summary>
    /// The files of <paramref name="plan"/> whose descriptors will not be
    /// written, for an account the target does not know, and whose folder
    /// would give some account access their own DACL did not (ADR-0178): the
    /// line the plan declares them on, counted.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="facts">What each item's manifest says, keyed by the item's object.</param>
    /// <param name="target">The target, which says which accounts it knows and what a folder passes to a file.</param>
    /// <param name="destinationOf">The folder each item lands in, or null where the plan is told none.</param>
    /// <returns>The files, their folders and the accounts that gain; null where there are none.</returns>
    public static WiderAccess? OpenedWider(
        RestorePlan plan,
        IReadOnlyDictionary<ObjectId, RestoreItemFacts> facts,
        RestoreTargetProfile target,
        Func<RestorePlanItem, string?>? destinationOf)
    {
        ThrowHelper.ThrowIfNull(plan);
        ThrowHelper.ThrowIfNull(facts);
        ThrowHelper.ThrowIfNull(target);

        return Widened(plan, facts, target, destinationOf, new Names());
    }

    /// <summary>
    /// What <see cref="OpenedWider"/> says of <paramref name="plan"/>, for a
    /// run, which is asked without its plan's facts (ADR-0178 Amendment 1):
    /// each file's manifest is read through <paramref name="reader"/> before
    /// anything is written, where the folder the file lands in says what a
    /// file made there takes. Where none does, as off Windows, nothing is
    /// read.
    /// </summary>
    /// <remarks>
    /// The manifests are asked for a wave at a time, so a blob holding many
    /// is read once (NFR-PERF-009). One that will not read is passed over:
    /// the run fails that file, and opens nothing wider.
    /// </remarks>
    /// <param name="plan">The run's plan.</param>
    /// <param name="target">The target, which says which accounts it knows and what a folder passes to a file.</param>
    /// <param name="destinationOf">The folder each item lands in.</param>
    /// <param name="reader">Reads each manifest.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The files, their folders and the accounts that gain; null where there are none.</returns>
    public static async ValueTask<WiderAccess?> OpenedWiderAsync(
        RestorePlan plan,
        RestoreTargetProfile target,
        Func<RestorePlanItem, string?> destinationOf,
        RepositoryReader reader,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(plan);
        ThrowHelper.ThrowIfNull(target);
        ThrowHelper.ThrowIfNull(destinationOf);
        ThrowHelper.ThrowIfNull(reader);

        // Each place is read once, to choose what to read and to judge it.
        var inheritedAt = new Dictionary<string, InheritedAccess?>(StringComparer.Ordinal);
        InheritedAccess? Inherited(string folder)
        {
            if (!inheritedAt.TryGetValue(folder, out var access))
            {
                inheritedAt[folder] = access = target.InheritedAccessAt(folder);
            }

            return access;
        }

        var madeAt = new Dictionary<string, InheritedAccess?>(StringComparer.Ordinal);
        InheritedAccess? Made(string path)
        {
            if (!madeAt.TryGetValue(path, out var access))
            {
                madeAt[path] = access = target.InheritedFolderAccessAt(path);
            }

            return access;
        }

        var judged = plan.Items
            .Where(item => destinationOf(item) is { } folder
                && (item.Kind == EntryKind.File ? Inherited(folder)
                    : item.Kind == EntryKind.DirectoryPlaceholder ? Made(Path.Combine(folder, NameOf(item)))
                    : null) is not null)
            .ToList();
        var facts = new Dictionary<ObjectId, RestoreItemFacts>();
        foreach (var wave in judged.Chunk(ManifestWave))
        {
            try
            {
                await reader.PrefetchAsync([.. wave.Select(item => item.ObjectId)], cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A prefetch is never what fails: each manifest is still read alone.
            }

            foreach (var item in wave)
            {
                if (await ManifestMetadataAsync(reader, item, cancellationToken).ConfigureAwait(false) is { } metadata)
                {
                    facts[item.ObjectId] = new RestoreItemFacts(
                        item.Kind, 0, MetadataRules.Captured(metadata),
                        Accounts: MetadataRules.AccountsOf(metadata), Access: MetadataRules.AccessOf(metadata));
                }
            }
        }

        return Widened(
            plan, facts, target with { InheritedAccessAt = Inherited, InheritedFolderAccessAt = Made }, destinationOf, new Names());
    }

    /// <summary>
    /// What a file's manifest, or a folder's own record, says was captured
    /// with it, or null where it will not read.
    /// </summary>
    private static async ValueTask<EntryMetadata?> ManifestMetadataAsync(
        RepositoryReader reader, RestorePlanItem item, CancellationToken cancellationToken)
    {
        RecordReadResult read;
        try
        {
            read = await reader.ReadSegmentAsync(item.ObjectId, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }

        if (read.Outcome != RecordReadOutcome.Ok || read.Plaintext is null)
        {
            return null;
        }

        try
        {
            return item.Kind == EntryKind.DirectoryPlaceholder
                ? TreeManifestCodec.Decode(read.Plaintext).Metadata
                : FileVersionManifestCodec.Decode(read.Plaintext).Metadata;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The last component of an item's path: the name it is made under.</summary>
    /// <summary>The limits <paramref name="target"/> states for an attribute, as the plan's line says them.</summary>
    private static IEnumerable<string> AttributeLimitsOf(RestoreTargetProfile target)
    {
        if (target.MaxAttributeNameBytes is { } name)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"a name of at most {name} bytes");
        }

        if (target.MaxAttributeValueBytes is { } value)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"a value of at most {value} bytes");
        }
    }

    private static string NameOf(RestorePlanItem item) => item.Path[(item.Path.LastIndexOf('/') + 1)..];

    /// <summary>
    /// A file or a folder the restore makes, whose descriptor names an
    /// account the target does not know, takes what passes to it instead of
    /// its own access, which architecture 06 §3 has a restore say, and
    /// refuse, where that is more.
    /// </summary>
    private static WiderAccess? Widened(
        RestorePlan plan,
        IReadOnlyDictionary<ObjectId, RestoreItemFacts> facts,
        RestoreTargetProfile target,
        Func<RestorePlanItem, string?>? destinationOf,
        Names names)
    {
        if (destinationOf is null)
        {
            return null;
        }

        var files = 0;
        var made = 0;
        var folders = new List<string>();
        var accounts = new List<string>();

        // Each place is read once, however many files land in it. A folder
        // is judged at its own path, where one that stands says nothing.
        var inheritedAt = new Dictionary<string, InheritedAccess?>(StringComparer.Ordinal);
        var madeAt = new Dictionary<string, InheritedAccess?>(StringComparer.Ordinal);
        foreach (var item in plan.Items)
        {
            if (item.Kind is not (EntryKind.File or EntryKind.DirectoryPlaceholder)
                || !facts.TryGetValue(item.ObjectId, out var known)
                || (Applied(item.Kind, target) & known.Captured & CapturedMetadata.SecurityDescriptor) == 0
                || known.Accounts is not { } named
                || named.All(account => names.Knows(account, target.KnowsAccount))
                || known.Access is not { } own
                || destinationOf(item) is not { } folder)
            {
                continue;
            }

            var file = item.Kind == EntryKind.File;
            var at = file ? folder : Path.Combine(folder, NameOf(item));
            var read = file ? inheritedAt : madeAt;
            if (!read.TryGetValue(at, out var inherited))
            {
                read[at] = inherited = file ? target.InheritedAccessAt(at) : target.InheritedFolderAccessAt(at);
            }

            if (inherited is null || WindowsAccess.Wider(own, inherited.Access) is not { Count: > 0 } gaining)
            {
                continue;
            }

            if (file)
            {
                files++;
            }
            else
            {
                made++;
            }

            accounts.AddRange(gaining.Where(account => !accounts.Contains(account, StringComparer.Ordinal)));
            if (!folders.Contains(inherited.Folder, StringComparer.Ordinal))
            {
                folders.Add(inherited.Folder);
            }
        }

        return files + made == 0 ? null : new WiderAccess(files, folders, accounts, made);
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
        private readonly Dictionary<string, bool> _owners = new(StringComparer.Ordinal);

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

        public bool MayOwn(string owner, Func<string, bool> mayOwn)
        {
            if (!_owners.TryGetValue(owner, out var may))
            {
                _owners[owner] = may = mayOwn(owner);
            }

            return may;
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
