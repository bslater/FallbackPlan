using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;

namespace FallbackPlan.Restore;

/// <summary>What the restore target can and cannot represent.</summary>
public sealed record RestoreTargetProfile
{
    /// <summary>Whether the target filesystem distinguishes case.</summary>
    public required bool CaseSensitive { get; init; }

    /// <summary>
    /// Whether the target takes two names that differ only in their Unicode
    /// normalisation, an accent composed into its letter or a mark of its
    /// own after it, for one name, as macOS's volumes do. Windows' and
    /// Linux's keep them apart (ADR-0170 Amendment 2).
    /// </summary>
    public bool NormalisationInsensitive { get; init; }

    /// <summary>Whether the target can apply POSIX mode/ownership.</summary>
    public required bool SupportsPosixMetadata { get; init; }

    /// <summary>Whether the target can create symlinks without elevation.</summary>
    public required bool SupportsSymlinks { get; init; }

    /// <summary>
    /// Whether the executor writes a file's alternate data streams back on
    /// this target: Windows keeps them, and nothing else does
    /// (<see cref="AlternateStreams"/>; ADR-0174). It is what the plan
    /// predicts by. A volume that keeps none, as a FAT one, refuses each
    /// stream, and the receipt says so of the file.
    /// </summary>
    public bool SupportsAlternateStreams { get; init; }

    /// <summary>
    /// Whether this target takes an extended attribute's name only in a
    /// namespace, as Linux does, so a file's alternate stream kept as an
    /// attribute is kept only where its name is in the user namespace
    /// (<see cref="MetadataRules.RefusedStreamAsAttribute"/>; ADR-0179).
    /// </summary>
    public bool AttributesTakeNamespaces { get; init; }

    /// <summary>
    /// The most bytes this target takes in an extended attribute's name, its
    /// namespace with it, or null where none is known: a file's stream kept
    /// as an attribute with a longer name is not kept
    /// (<see cref="MetadataRules.StreamTooLargeForAttribute"/>; ADR-0179
    /// Amendment 2).
    /// </summary>
    public uint? MaxAttributeNameBytes { get; init; }

    /// <summary>
    /// The most bytes this target holds in an extended attribute's value on
    /// any volume, or null where none is known: a file's stream kept as an
    /// attribute that is larger is not kept (ADR-0179 Amendment 2).
    /// </summary>
    public uint? MaxAttributeValueBytes { get; init; }

    /// <summary>
    /// Whether the executor writes a file's creation time back on this
    /// target: Windows and macOS have a call that sets one, Linux does not
    /// (<see cref="FileTimes"/>). It is what the plan predicts by. The
    /// receipt records what each write did, so a volume that keeps no
    /// creation times is still said where it refuses one.
    /// </summary>
    public bool SupportsCreationTimes { get; init; }

    /// <summary>
    /// Whether the executor writes extended attributes back on this target:
    /// Linux and macOS have a call that writes one, Windows does not
    /// (<see cref="ExtendedAttributes"/>). Each is still the platform's to
    /// refuse, and the receipt says which it did (ADR-0087).
    /// </summary>
    public bool SupportsExtendedAttributes { get; init; }

    /// <summary>
    /// Whether the executor writes a file's and a folder's Windows attribute
    /// bits back on this target: Windows keeps them, and nothing else does
    /// (ADR-0175). A bit no restore sets, as encrypted, is named in the plan
    /// and the receipt.
    /// </summary>
    public bool SupportsFileAttributes { get; init; }

    /// <summary>
    /// The access a file made in a folder would take, and the folder it comes
    /// from: the nearest at or above it that stands, through each folder below
    /// that the restore makes. The plan holds it against a file's own where
    /// its descriptor will not be written (<see cref="WindowsAccess"/>;
    /// ADR-0178 and its Amendment 3). Null where it does not say: off
    /// Windows, and for a target no case names.
    /// </summary>
    public Func<string, InheritedAccess?> InheritedAccessAt { get; init; } = _ => null;

    /// <summary>
    /// The access a folder the restore makes at a path would take as its own,
    /// and the folder it comes from, which the plan holds against the
    /// folder's own where its descriptor will not be written
    /// (<see cref="WindowsAccess"/>; ADR-0178 Amendment 4). Null where it
    /// does not say: where a folder stands at the path, off Windows, and for
    /// a target no case names.
    /// </summary>
    public Func<string, InheritedAccess?> InheritedFolderAccessAt { get; init; } = _ => null;

    /// <summary>
    /// Whether the executor gives a file and a folder their NTFS compression
    /// back on this target: Windows keeps it, and nothing else does
    /// (<see cref="FileCompression"/>; ADR-0175 Amendment 2). Where it does,
    /// the plan does not name the compressed bit among those no restore sets.
    /// A volume that keeps none refuses it, and the receipt says so.
    /// </summary>
    public bool SupportsCompression { get; init; }

    /// <summary>
    /// The flags the executor gives a file and a folder back on this target
    /// (<see cref="FileFlags.Settable"/>; ADR-0180 Amendment 4): no-dump on
    /// Linux, with immutable and append-only for an account holding
    /// <c>CAP_LINUX_IMMUTABLE</c>; the owner's immutable, append-only,
    /// no-dump and hidden flags on macOS; none on Windows, and none for a
    /// target no case names. The plan names the rest.
    /// </summary>
    public FileFlagBits SettableFlags { get; init; }

    /// <summary>The flags the executor gives a link's own back, as <see cref="SettableFlags"/> are a file's: macOS's, and none on Linux.</summary>
    public FileFlagBits SettableLinkFlags { get; init; }

    /// <summary>
    /// The flags this target would set for an account with the privilege
    /// this one lacks: Linux's immutable and append-only without
    /// <c>CAP_LINUX_IMMUTABLE</c>. The plan says so where it names them.
    /// </summary>
    public FileFlagBits FlagsNeedingPrivilege { get; init; }

    /// <summary>
    /// The calls flags are set through: the platform's unless a caller says
    /// otherwise, as a case judged on a platform that keeps none does.
    /// </summary>
    public IFileFlags Flags { get; init; } = FileFlags.OfThisPlatform;

    /// <summary>
    /// Whether the executor writes a file's and a folder's Windows security
    /// descriptor back on this target: Windows keeps one, and nothing else
    /// does (ADR-0176). It is what the plan predicts by, with
    /// <see cref="KnowsAccount"/>.
    /// </summary>
    public bool SupportsSecurityDescriptors { get; init; }

    /// <summary>
    /// Whether this machine knows an account a captured descriptor names, by
    /// its SID. A descriptor is written back only where every account it
    /// names is known, and the plan predicts by the same answer
    /// (<see cref="WindowsAccounts.Knows"/>; ADR-0176).
    /// </summary>
    public Func<string, bool> KnowsAccount { get; init; } = WindowsAccounts.Knows;

    /// <summary>
    /// Whether the account the restore runs as holds SeRestorePrivilege,
    /// which the writer enables, for one write at a time, to give a file an
    /// owner the account is not (<see cref="WindowsPrivileges"/>; ADR-0176
    /// Amendment 1).
    /// </summary>
    public bool HoldsRestorePrivilege { get; init; }

    /// <summary>
    /// Whether the account the restore runs as holds SeSecurityPrivilege,
    /// which the writer enables, for one write at a time, to write a file's
    /// audit list (ADR-0176 Amendment 3). Where it does not, the plan names
    /// the descriptors whose audit lists will not land.
    /// </summary>
    public bool HoldsSecurityPrivilege { get; init; }

    /// <summary>
    /// Whether the account the restore runs as may make a SID a file's owner
    /// without SeRestorePrivilege: its own account, or a group its token
    /// marks as one it may own by. Where it holds no such privilege, the plan
    /// predicts by this which descriptors' owners will be refused
    /// (<see cref="WindowsAccounts.MayOwn"/>; ADR-0176 Amendment 2).
    /// </summary>
    public Func<string, bool> MayOwnBy { get; init; } = WindowsAccounts.MayOwn;

    /// <summary>
    /// Whether this installation captured the snapshot being restored: its
    /// device is the one the snapshot names. A Linux ACL names accounts by
    /// number, and a number names the account it meant only on the machine
    /// that captured it, so an ACL that names one is written back only where
    /// this holds (ADR-0087). False unless a caller that knows who captured
    /// the snapshot says so.
    /// </summary>
    /// <remarks>
    /// The device a snapshot names is attribution by claim (ADR-0020): any
    /// member of the repository could have written it. This keeps an honest
    /// restore on another machine from giving a file to whoever holds a
    /// number there. It is no defence against a member who lies about it:
    /// such a member can already name any owner, which a restore as root
    /// gives back (ADR-0085).
    /// </remarks>
    public bool CapturedHere { get; init; }

    /// <summary>
    /// The account the restore runs as, which decides whose files it may
    /// give away and to which groups (<see cref="RestoreAccount"/>), or null
    /// where the target gives no file an owner by name. The plan predicts
    /// each file's ownership by it, and the executor resolves names through
    /// it.
    /// </summary>
    public RestoreAccount? Account { get; init; }

    /// <summary>The target's maximum path bytes, when known.</summary>
    public uint? MaxPathBytes { get; init; }

    /// <summary>
    /// Whether the target names files by Windows' rules
    /// (<see cref="RestorePaths.IsWindowsName"/>), so a name another platform
    /// wrote may be one it refuses or alters (ADR-0170): Windows does, and so
    /// does a FAT or exFAT drive wherever it is mounted (ADR-0170 Amendment
    /// 6). Stated rather than taken from the running platform, so a plan for
    /// a Windows target can be made, and tested, anywhere.
    /// </summary>
    public bool WindowsNamingRules { get; init; }

    /// <summary>
    /// The most bytes the target holds in one name, counted in UTF-8, or null
    /// where that is not known (ADR-0170 Amendment 1). Windows counts a name
    /// in UTF-16 units instead, which <see cref="WindowsNamingRules"/> judges,
    /// and a name is never fewer bytes than units, so one Windows holds may
    /// be one a target that counts bytes does not.
    /// </summary>
    public uint? MaxNameBytes { get; init; }

    /// <summary>
    /// The most UTF-16 units the target holds in one name, or null where that
    /// is not known or <see cref="WindowsNamingRules"/> judges it: an exFAT or
    /// NTFS volume on Linux holds 255 whatever the bytes (ADR-0170 Amendment
    /// 9).
    /// </summary>
    public uint? MaxNameUnits { get; init; }

    /// <summary>What this target lets a <see cref="MetadataWriter"/> write back.</summary>
    public MetadataTarget Metadata => new()
    {
        SupportsPosixMetadata = SupportsPosixMetadata,
        SupportsCreationTimes = SupportsCreationTimes,
        SupportsExtendedAttributes = SupportsExtendedAttributes,
        SupportsFileAttributes = SupportsFileAttributes,
        SupportsSecurityDescriptors = SupportsSecurityDescriptors,
        SupportsAlternateStreams = SupportsAlternateStreams,
        SupportsCompression = SupportsCompression,
        SettableFlags = SettableFlags,
        SettableLinkFlags = SettableLinkFlags,
        Flags = Flags,
        KnowsAccount = KnowsAccount,
        HoldsRestorePrivilege = HoldsRestorePrivilege,
        HoldsSecurityPrivilege = HoldsSecurityPrivilege,
        CapturedHere = CapturedHere,
        Account = Account,
    };

    /// <summary>The local defaults for this process's platform, and the account it runs as.</summary>
    public static RestoreTargetProfile ForLocalPlatform() => new()
    {
        CaseSensitive = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS(),
        NormalisationInsensitive = OperatingSystem.IsMacOS(),
        SupportsPosixMetadata = !OperatingSystem.IsWindows(),
        SupportsSymlinks = SymbolicLinks.CanCreate,
        WindowsNamingRules = OperatingSystem.IsWindows(),

        // The filesystems Linux commonly creates hold 255 bytes in a name.
        // macOS's limit is not stated, so a name there is not judged by one.
        MaxNameBytes = OperatingSystem.IsLinux() ? 255u : null,
        SupportsCreationTimes = FileTimes.CanSetCreationTime,
        SupportsExtendedAttributes = ExtendedAttributes.CanSet,
        SupportsFileAttributes = OperatingSystem.IsWindows(),
        SupportsSecurityDescriptors = OperatingSystem.IsWindows(),
        HoldsRestorePrivilege = WindowsPrivileges.Holds(WindowsPrivileges.Restore),
        HoldsSecurityPrivilege = WindowsPrivileges.Holds(WindowsPrivileges.Security),
        SupportsAlternateStreams = AlternateStreams.CanWrite,
        AttributesTakeNamespaces = ExtendedAttributes.TakesNamespaces,
        MaxAttributeNameBytes = ExtendedAttributes.MaxNameBytes,
        MaxAttributeValueBytes = ExtendedAttributes.MaxValueBytes,
        SupportsCompression = FileCompression.CanSet,
        SettableFlags = FileFlags.Settable(EntryKind.File),
        SettableLinkFlags = FileFlags.Settable(EntryKind.Symlink),
        FlagsNeedingPrivilege = FileFlags.NeedingPrivilege,
        InheritedAccessAt = WindowsAccess.InheritedByAFileAt,
        InheritedFolderAccessAt = WindowsAccess.InheritedByAFolderAt,
        Account = RestoreAccount.OfThisProcess(),
    };

    /// <summary>
    /// The local defaults for a restore of <paramref name="snapshotId"/> by the
    /// installation whose device is <paramref name="deviceId"/>, which
    /// captured it where the catalogue says the snapshot names that device
    /// (<see cref="CapturedHere"/>).
    /// </summary>
    /// <param name="catalogue">The catalogue the restore is planned from.</param>
    /// <param name="snapshotId">The snapshot, 16 bytes.</param>
    /// <param name="deviceId">This installation's device.</param>
    public static RestoreTargetProfile ForLocalPlatform(
        Catalogue catalogue, ReadOnlySpan<byte> snapshotId, ReadOnlySpan<byte> deviceId)
    {
        ThrowHelper.ThrowIfNull(catalogue);

        var capturedHere = false;
        foreach (var snapshot in catalogue.EnumerateSnapshots())
        {
            if (snapshot.SnapshotId.Span.SequenceEqual(snapshotId))
            {
                capturedHere = snapshot.DeviceId.Span.SequenceEqual(deviceId);
                break;
            }
        }

        return ForLocalPlatform() with { CapturedHere = capturedHere };
    }

    /// <summary>
    /// This target as the volumes of <paramref name="directories"/> take
    /// names, where they say (ADR-0170 Amendment 5): case-sensitive only where
    /// every one is, and taking a name's two forms for one where any does. It
    /// names files by Windows' rules where any volume does, or where this
    /// target already did, since a platform that names by them does so on
    /// every volume (ADR-0170 Amendment 6). It holds a name to this target's
    /// byte limit only where some volume counts a name in bytes, and to 255
    /// UTF-16 units where some volume counts it in units and Windows' rules,
    /// which hold a name to as many, are not in force, so a name must fit
    /// every volume it may be written to (ADR-0170 Amendment 9). A volume
    /// that says nothing is taken to name as this target states, and no
    /// folder leaves it as it is.
    /// </summary>
    /// <param name="directories">Each folder a restore writes to.</param>
    /// <returns>The target as written there.</returns>
    public RestoreTargetProfile AsWrittenTo(IEnumerable<string> directories)
    {
        ThrowHelper.ThrowIfNull(directories);
        var caseSensitive = true;
        var normalisationInsensitive = false;
        var windowsNames = WindowsNamingRules;
        var countedInBytes = false;
        var countedInUnits = false;
        var any = false;
        foreach (var directory in directories)
        {
            any = true;
            if (VolumeNaming.Of(directory) is not { } naming)
            {
                // Taken to name as this target states.
                caseSensitive &= CaseSensitive;
                normalisationInsensitive |= NormalisationInsensitive;
                countedInBytes |= MaxNameBytes is not null;
                countedInUnits |= MaxNameUnits is not null;
                continue;
            }

            caseSensitive &= naming.CaseSensitive;
            normalisationInsensitive |= naming.NormalisationInsensitive;
            windowsNames |= naming.WindowsNames;
            countedInBytes |= !naming.NamesInUnits;
            countedInUnits |= naming.NamesInUnits;
        }

        return any
            ? this with
            {
                CaseSensitive = caseSensitive,
                NormalisationInsensitive = normalisationInsensitive,
                WindowsNamingRules = windowsNames,
                MaxNameBytes = countedInBytes ? MaxNameBytes : null,
                MaxNameUnits = countedInUnits && !windowsNames ? MaxNameUnits ?? MostUnitsInAName : MaxNameUnits,
            }
            : this;
    }

    /// <summary>
    /// Whether <paramref name="other"/> takes names as this target does: by
    /// case, by normalisation, by Windows' rules or not, and with the same
    /// limits on a name's length.
    /// </summary>
    /// <param name="other">The other target.</param>
    /// <returns>Whether it does.</returns>
    public bool NamesAs(RestoreTargetProfile other)
    {
        ThrowHelper.ThrowIfNull(other);
        return CaseSensitive == other.CaseSensitive
            && NormalisationInsensitive == other.NormalisationInsensitive
            && WindowsNamingRules == other.WindowsNamingRules
            && MaxNameBytes == other.MaxNameBytes
            && MaxNameUnits == other.MaxNameUnits;
    }

    /// <summary>The most UTF-16 units a volume that counts a name in units holds in one.</summary>
    private const uint MostUnitsInAName = 255;
}

/// <summary>One planned restore item.</summary>
public sealed record RestorePlanItem(
    string Path,
    EntryKind Kind,
    ObjectId ObjectId,
    ulong Length,
    bool HasAlternateStreams = false);

/// <summary>A conflict the plan surfaces BEFORE any byte moves (architecture 08 §2).</summary>
public sealed record RestoreConflict(string Path, string Reason);

/// <summary>A metadata capability the target cannot honour — degradation is declared, never silent.</summary>
public sealed record MetadataDegradation(string Capability, string Detail);

/// <summary>
/// The plan (FR-RST-004; architecture 08 §2): the complete file set,
/// conflicts (case collisions on a case-folding target, path-length
/// overruns, names the target cannot make), the space estimate, and
/// declared metadata degradations — all computed before any transfer
/// starts, so the operator decides with facts instead of discovering them
/// mid-restore.
/// </summary>
public sealed record RestorePlan
{
    /// <summary>The snapshot being restored, 16 bytes.</summary>
    public required ReadOnlyMemory<byte> SnapshotId { get; init; }

    /// <summary>Every item to restore, in path order.</summary>
    public required IReadOnlyList<RestorePlanItem> Items { get; init; }

    /// <summary>Conflicts that need an operator decision.</summary>
    public required IReadOnlyList<RestoreConflict> Conflicts { get; init; }

    /// <summary>Declared degradations for this target.</summary>
    public required IReadOnlyList<MetadataDegradation> Degradations { get; init; }

    /// <summary>The sum of logical lengths — the space the restore needs.</summary>
    public ulong SpaceEstimateBytes => Items.Aggregate(0ul, (sum, item) => sum + item.Length);
}

/// <summary>
/// Builds restore plans from the catalogue's path tables. The planner
/// reads; it never writes — execution is <see cref="RestoreExecutor"/>'s
/// job, and only after the operator has seen the plan.
/// </summary>
public static class RestorePlanner
{
    /// <summary>Plans the restore of <paramref name="pathPrefix"/> (empty = everything) from one snapshot.</summary>
    public static RestorePlan Plan(
        Catalogue catalogue,
        ReadOnlySpan<byte> snapshotIdSpan,
        string pathPrefix,
        RestoreTargetProfile target)
    {
        ThrowHelper.ThrowIfNull(pathPrefix);
        return Plan(catalogue, snapshotIdSpan, pathPrefix.Length == 0 ? [] : [pathPrefix], target);
    }

    /// <summary>
    /// Plans the restore of several subtrees from one snapshot in one plan —
    /// one run, one quarantine, one receipt (ADR-0041). An empty list plans
    /// everything. Prefixes subsumed by an ancestor prefix are dropped
    /// before walking, so the walks are disjoint and the conflict passes run
    /// over the union exactly once.
    /// </summary>
    public static RestorePlan Plan(
        Catalogue catalogue,
        ReadOnlySpan<byte> snapshotIdSpan,
        IReadOnlyList<string> pathPrefixes,
        RestoreTargetProfile target)
    {
        ThrowHelper.ThrowIfNull(catalogue);
        ThrowHelper.ThrowIfNull(pathPrefixes);
        ThrowHelper.ThrowIfNull(target);

        var snapshotId = snapshotIdSpan.ToArray();
        var items = new List<RestorePlanItem>();
        var conflicts = new List<RestoreConflict>();
        var degradations = new List<MetadataDegradation>();

        void Walk(string parent)
        {
            foreach (var entry in catalogue.ListDirectory(snapshotId, parent))
            {
                items.Add(new RestorePlanItem(
                    entry.Path, entry.EntryKind, entry.ObjectId, entry.LogicalLength ?? 0,
                    entry.HasAlternateStreams));

                if (entry.EntryKind == EntryKind.DirectoryPlaceholder)
                {
                    Walk(entry.Path);
                }
            }
        }

        var prefixes = Normalise(pathPrefixes);
        if (prefixes.Count == 0)
        {
            Walk(string.Empty);
        }
        else
        {
            foreach (var prefix in prefixes)
            {
                var entry = catalogue.LookupPath(snapshotId, prefix);
                if (entry is null)
                {
                    conflicts.Add(new RestoreConflict(prefix, "The path does not exist in this snapshot."));
                    continue;
                }

                items.Add(new RestorePlanItem(
                    entry.Path, entry.EntryKind, entry.ObjectId, entry.LogicalLength ?? 0,
                    entry.HasAlternateStreams));
                if (entry.EntryKind == EntryKind.DirectoryPlaceholder)
                {
                    Walk(entry.Path);
                }
            }
        }

        // Collisions: two captured paths the target takes for one
        // (architecture 08 §2 — a plan-time conflict, never a capture
        // error and never a silent overwrite). A path is folded only as the
        // target folds it: case as the executor compares it, and
        // normalisation only where the target ignores it, since Windows
        // keeps a name's composed and decomposed forms apart (ADR-0170
        // Amendment 2).
        if (!target.CaseSensitive || target.NormalisationInsensitive)
        {
            foreach (var group in items
                .GroupBy(
                    item => target.NormalisationInsensitive ? item.Path.Normalize(System.Text.NormalizationForm.FormC) : item.Path,
                    target.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                foreach (var item in group)
                {
                    conflicts.Add(new RestoreConflict(
                        item.Path,
                        $"Collides on this target with: {string.Join(", ", group.Select(other => other.Path).Where(path => path != item.Path))}, which it takes for the same name."));
                }
            }
        }

        // Names the executor would refuse, or that the target would refuse or
        // alter, said before any byte moves rather than met item by item at
        // execution (ADR-0170). Each item is judged by its own last
        // component: a folder's name is judged as the folder's item.
        foreach (var item in items)
        {
            var name = item.Path[(item.Path.LastIndexOf('/') + 1)..];
            if (!RestorePaths.IsPlainComponent(name, out var why))
            {
                conflicts.Add(new RestoreConflict(item.Path, $"No target can restore this name: {why}."));
            }
            else if (target.WindowsNamingRules && !RestorePaths.IsWindowsName(name, out why))
            {
                conflicts.Add(new RestoreConflict(item.Path, $"The target names files by Windows' rules, which this name breaks: {why}."));
            }
            else if (target.MaxNameBytes is { } maxName
                && System.Text.Encoding.UTF8.GetByteCount(name) is var nameBytes && nameBytes > maxName)
            {
                conflicts.Add(new RestoreConflict(
                    item.Path, $"The target holds at most {maxName} bytes in one name, and this name is {nameBytes}."));
            }
            else if (target.MaxNameUnits is { } maxUnits && name.Length > maxUnits)
            {
                conflicts.Add(new RestoreConflict(
                    item.Path, $"The target holds at most {maxUnits} UTF-16 units in one name, and this name is {name.Length}."));
            }
        }

        // The name each file is first written under, beside it (ADR-0173),
        // is the run's own: an entry the snapshot names there, or below it,
        // would be removed when that file spools, after the receipt had
        // counted it restored (ADR-0152 Amendment 4).
        var spools = SpoolsOf(items);
        foreach (var item in items)
        {
            if (SpooledThere(item.Path, spools) is { } spooled)
            {
                conflicts.Add(new RestoreConflict(
                    item.Path, $"The restore writes {spooled} under this name first, so it cannot be restored here."));
            }
        }

        if (target.MaxPathBytes is { } maxPath)
        {
            foreach (var item in items.Where(item =>
                System.Text.Encoding.UTF8.GetByteCount(item.Path) > maxPath))
            {
                conflicts.Add(new RestoreConflict(
                    item.Path, $"The path exceeds the target's {maxPath}-byte limit."));
            }
        }

        if (!target.SupportsPosixMetadata)
        {
            degradations.Add(new MetadataDegradation(
                "posix-metadata", "POSIX mode and ownership will not be applied on this target."));
        }

        if (!target.SupportsSymlinks && items.Any(item => item.Kind == EntryKind.Symlink))
        {
            degradations.Add(new MetadataDegradation(
                "symlinks", "Symlinks cannot be created on this target; they will be reported as skipped."));
        }

        if (items.Any(item => item.Kind == EntryKind.Special))
        {
            degradations.Add(new MetadataDegradation(
                "special-files", "Special files (FIFOs, sockets, devices) are recorded but not materialised."));
        }

        // Presence-gated like the symlink one: declared only when this tree
        // actually carries streams (RR-6 — the captured streams exist in the
        // repository; what cannot happen yet is writing them back).
        if (!target.SupportsAlternateStreams
            && !MetadataRules.KeepsStreamsAsAttributes(
                EntryKind.File, target.SupportsAlternateStreams, target.SupportsExtendedAttributes && ExtendedAttributes.CanSet)
            && items.Any(item => item.HasAlternateStreams))
        {
            degradations.Add(new MetadataDegradation(
                "alternate-streams",
                "Files carrying alternate data streams will restore their main stream only on this target; "
                + "the streams are captured and recorded, not written back."));
        }

        return new RestorePlan
        {
            SnapshotId = snapshotId,
            Items = items,
            Conflicts = conflicts,
            Degradations = degradations,
        };
    }

    /// <summary>
    /// Sorted, deduplicated prefixes with descendants of another prefix
    /// removed; a whole-snapshot request (an empty entry) collapses the list
    /// to "everything".
    /// </summary>
    /// <summary>
    /// The paths the executor first writes <paramref name="items"/>' files
    /// under, each beside its file, mapped to the file's own path. Held
    /// without regard to case, since a target that folds case takes every
    /// spelling of a name for one.
    /// </summary>
    internal static Dictionary<string, string> SpoolsOf(IEnumerable<RestorePlanItem> items)
    {
        var spools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(item => item.Kind == EntryKind.File))
        {
            var folder = item.Path.LastIndexOf('/') + 1;
            spools.TryAdd(item.Path[..folder] + RestorePaths.SpoolName(item.Path[folder..], "restore"), item.Path);
        }

        return spools;
    }

    /// <summary>
    /// The file whose spool <paramref name="path"/> stands at, or below, among
    /// <paramref name="spools"/>; null where it stands at none.
    /// </summary>
    internal static string? SpooledThere(string path, Dictionary<string, string> spools)
    {
        for (var end = path.Length; end > 0; end = path.LastIndexOf('/', end - 1))
        {
            if (spools.TryGetValue(path[..end], out var file))
            {
                return file;
            }
        }

        return null;
    }

    private static List<string> Normalise(IReadOnlyList<string> pathPrefixes)
    {
        if (pathPrefixes.Any(prefix => prefix.Length == 0))
        {
            return [];
        }

        var sorted = pathPrefixes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var kept = new List<string>(sorted.Count);
        foreach (var prefix in sorted)
        {
            if (!kept.Any(ancestor => prefix.StartsWith(ancestor + "/", StringComparison.Ordinal)))
            {
                kept.Add(prefix);
            }
        }

        return kept;
    }
}
