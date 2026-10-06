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

    /// <summary>Whether the target can apply POSIX mode/ownership.</summary>
    public required bool SupportsPosixMetadata { get; init; }

    /// <summary>Whether the target can create symlinks without elevation.</summary>
    public required bool SupportsSymlinks { get; init; }

    /// <summary>
    /// Whether the executor can write NTFS alternate data streams back on
    /// this target. <see langword="false"/> everywhere — including Windows —
    /// until the RR-6 write-back half lands: the polarity is "what the
    /// executor can do", not "what the filesystem could hold", and an
    /// executor that drops streams must say so on every target.
    /// </summary>
    public bool SupportsAlternateStreams { get; init; }

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

    /// <summary>The local defaults for this process's platform, and the account it runs as.</summary>
    public static RestoreTargetProfile ForLocalPlatform() => new()
    {
        CaseSensitive = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS(),
        SupportsPosixMetadata = !OperatingSystem.IsWindows(),
        SupportsSymlinks = !OperatingSystem.IsWindows(),
        SupportsCreationTimes = FileTimes.CanSetCreationTime,
        SupportsExtendedAttributes = ExtendedAttributes.CanSet,
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
/// overruns), the space estimate, and declared metadata degradations —
/// all computed before any transfer starts, so the operator decides with
/// facts instead of discovering them mid-restore.
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

        // Case collisions: two captured paths that fold to one target path
        // (architecture 08 §2 — a plan-time conflict, never a capture
        // error and never a silent overwrite).
        if (!target.CaseSensitive)
        {
            foreach (var group in items
                .GroupBy(item => Catalogue.Casefold(item.Path), StringComparer.Ordinal)
                .Where(group => group.Count() > 1))
            {
                foreach (var item in group)
                {
                    conflicts.Add(new RestoreConflict(
                        item.Path,
                        $"Collides on a case-insensitive target with: {string.Join(", ", group.Select(other => other.Path).Where(path => path != item.Path))}."));
                }
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
        if (!target.SupportsAlternateStreams && items.Any(item => item.HasAlternateStreams))
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
