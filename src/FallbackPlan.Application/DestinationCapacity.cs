using Bodu;

namespace FallbackPlan.Application;

/// <summary>
/// How much room a destination must leave for the machine that owns it
/// (FR-DEST-003).
/// </summary>
/// <remarks>
/// The policy lives here and the measurement does not, for the same reason the
/// failure-domain comparison does: asking a volume how much space it has is
/// the platform's job, and the use-case layer reaches only the domain layer
/// (architecture 11 §2). What a number of free bytes <i>means</i> is a
/// decision, and decisions belong where they can be tested without a disk.
/// </remarks>
public static class DestinationCapacity
{
    /// <summary>
    /// The free space a destination volume must keep, whatever the backup
    /// would like.
    /// </summary>
    /// <remarks>
    /// An absolute floor rather than a fraction of the volume: what a full
    /// disk breaks — the journal, temp files, the next capture's staging —
    /// needs roughly the same room whether the disk is 500 GB or 5 TB.
    /// </remarks>
    public const long FreeSpaceFloorBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Says why a copy to <paramref name="replicaRoot"/> must not start, or
    /// null when there is room.
    /// </summary>
    /// <param name="replicaRoot">The directory the copy would write into, for the message.</param>
    /// <param name="availableBytes">
    /// What the platform says is free on that volume, or null when it will not
    /// say — which answers "there is room". This guard exists to stop a disk
    /// being filled; it must never be the reason a healthy destination stops
    /// receiving backups.
    /// </param>
    public static string? FloorShortfall(string replicaRoot, long? availableBytes)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(replicaRoot);

        return availableBytes is { } free && free < FreeSpaceFloorBytes
            ? $"the volume holding '{replicaRoot}' has {free} byte(s) free, under the "
                + $"{FreeSpaceFloorBytes}-byte floor this hub leaves for the machine that owns it"
            : null;
    }

    /// <summary>
    /// The path to hand the platform's free-space query for the volume
    /// holding <paramref name="replicaRoot"/>.
    /// </summary>
    /// <remarks>
    /// On Unix the answer is the replica root itself: <c>statvfs</c> reports
    /// for whatever volume holds the path, and <c>Path.GetPathRoot</c> would
    /// answer <c>/</c> for every absolute path — measuring the OS volume for
    /// a destination whose whole job is to be a different one, which is the
    /// 2026-08 defect this decision pins. Windows drive-letter volumes want
    /// the drive root (NTFS mounted folders remain measured at the drive — a
    /// documented approximation).
    /// </remarks>
    /// <param name="replicaRoot">The directory the copy would write into.</param>
    public static string ProbeRootFor(string replicaRoot)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(replicaRoot);

        return OperatingSystem.IsWindows() ? Path.GetPathRoot(replicaRoot)! : replicaRoot;
    }
}
