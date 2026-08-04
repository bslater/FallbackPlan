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

    /// <summary>The target's maximum path bytes, when known.</summary>
    public uint? MaxPathBytes { get; init; }

    /// <summary>The local defaults for this process's platform.</summary>
    public static RestoreTargetProfile ForLocalPlatform() => new()
    {
        CaseSensitive = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS(),
        SupportsPosixMetadata = !OperatingSystem.IsWindows(),
        SupportsSymlinks = !OperatingSystem.IsWindows(),
    };
}

/// <summary>One planned restore item.</summary>
public sealed record RestorePlanItem(
    string Path,
    EntryKind Kind,
    ObjectId ObjectId,
    ulong Length);

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
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(pathPrefix);
        ArgumentNullException.ThrowIfNull(target);

        var snapshotId = snapshotIdSpan.ToArray();
        var items = new List<RestorePlanItem>();
        var conflicts = new List<RestoreConflict>();
        var degradations = new List<MetadataDegradation>();

        void Walk(string parent)
        {
            foreach (var entry in catalogue.ListDirectory(snapshotId, parent))
            {
                items.Add(new RestorePlanItem(
                    entry.Path, entry.EntryKind, entry.ObjectId, entry.LogicalLength ?? 0));

                if (entry.EntryKind == EntryKind.DirectoryPlaceholder)
                {
                    Walk(entry.Path);
                }
            }
        }

        if (pathPrefix.Length == 0)
        {
            Walk(string.Empty);
        }
        else
        {
            var entry = catalogue.LookupPath(snapshotId, pathPrefix);
            if (entry is null)
            {
                conflicts.Add(new RestoreConflict(pathPrefix, "The path does not exist in this snapshot."));
            }
            else
            {
                items.Add(new RestorePlanItem(entry.Path, entry.EntryKind, entry.ObjectId, entry.LogicalLength ?? 0));
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

        return new RestorePlan
        {
            SnapshotId = snapshotId,
            Items = items,
            Conflicts = conflicts,
            Degradations = degradations,
        };
    }
}
