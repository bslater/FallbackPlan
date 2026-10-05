using Bodu;
using System.Globalization;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Restore;

/// <summary>One part of a run: a plan, and the folder it lands in.</summary>
/// <remarks>
/// An original-location restore of a set with several roots runs one slice a
/// root (ADR-0041). Every other restore is one slice.
/// </remarks>
/// <param name="Plan">What the slice restores.</param>
/// <param name="OutputDirectory">Where it lands, as the operator named it.</param>
public sealed record RestoreSlice(RestorePlan Plan, string OutputDirectory);

/// <summary>How a restore asks the platform about room (FR-RST-003; ADR-0083).</summary>
public sealed record RestoreSpaceProbe
{
    /// <summary>
    /// The bytes free on the volume holding a directory, or null when the
    /// platform will not say. Asked of a directory that exists: the nearest
    /// existing ancestor of where the restore will write.
    /// </summary>
    public required Func<string, long?> AvailableBytes { get; init; }

    /// <summary>
    /// The volume a directory sits on, or null when the platform will not
    /// say, asked as <see cref="AvailableBytes"/> is. Directories on one
    /// volume share its free space, so their needs add up. One whose volume
    /// is unknown is measured on its own.
    /// </summary>
    public Func<string, ulong?> VolumeOf { get; init; } = static _ => null;

    /// <summary>
    /// Where the engine holds a file while its hash verifies: the system's
    /// temporary directory, which is where <see cref="RestoreEngine"/> spools
    /// unless told otherwise.
    /// </summary>
    public string WorkingDirectory { get; init; } = Path.GetTempPath();

    /// <summary>
    /// A probe that asks the platform what is free, naming volumes with
    /// <paramref name="volumeOf"/> when given.
    /// </summary>
    /// <param name="volumeOf">The host's volume identity, or null to measure each directory on its own.</param>
    public static RestoreSpaceProbe Platform(Func<string, ulong?>? volumeOf = null) => new()
    {
        AvailableBytes = PlatformAvailableBytes,
        VolumeOf = volumeOf ?? (static _ => null),
    };

    /// <summary>
    /// What <see cref="DriveInfo"/> says is free on the volume holding
    /// <paramref name="directory"/>, or null when it will not say.
    /// </summary>
    /// <param name="directory">An existing directory.</param>
    public static long? PlatformAvailableBytes(string directory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(directory);

        try
        {
            // The directory itself on Unix, where statvfs answers for whatever
            // volume holds the path, and the drive root on Windows: the probe
            // root the destination floor settled on, NTFS mounted folders
            // measured at their drive included.
            var root = OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(directory))! : directory;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>What a run needs on one volume it writes to, against what is free there.</summary>
/// <param name="Directory">
/// The folder the run writes to on that volume, as the operator named it, or
/// the engine's working directory when that volume holds no folder of the run.
/// </param>
/// <param name="NeededBytes">The bytes the run needs there (<see cref="RestoreSpace"/> says how they are counted).</param>
/// <param name="AvailableBytes">What the platform says is free there, or null when it will not say.</param>
/// <param name="Working">Whether this is the engine's working directory alone, which needs room for the largest file.</param>
public sealed record RestoreSpaceNeed(string Directory, ulong NeededBytes, long? AvailableBytes, bool Working = false)
{
    /// <summary>Whether the platform says less is free than the run needs. Never, when it will not say.</summary>
    public bool IsShort => AvailableBytes is { } free && (free < 0 || (ulong)free < NeededBytes);

    /// <summary>The need in words: how much, where, and what is free.</summary>
    public string Describe()
    {
        var free = AvailableBytes is { } available
            ? $"which has {RestoreSpace.Bytes((ulong)Math.Max(available, 0))} free"
            : "where the free space is unknown";

        return Working
            ? $"needs {RestoreSpace.Bytes(NeededBytes)} of working space in '{Directory}' for its largest file, {free}"
            : $"needs {RestoreSpace.Bytes(NeededBytes)} on the volume holding '{Directory}', {free}";
    }
}

/// <summary>A run's space, volume by volume.</summary>
/// <param name="Needs">Each volume the run writes to, folders first in the order the run writes them.</param>
/// <param name="WriteBytes">What the files write, unrounded: logical lengths, or less their holes when <paramref name="Exact"/>.</param>
/// <param name="Exact">Whether every file was measured from its manifest rather than its logical length.</param>
public sealed record RestoreSpaceReport(IReadOnlyList<RestoreSpaceNeed> Needs, ulong WriteBytes, bool Exact)
{
    /// <summary>The volumes with less free than the run needs.</summary>
    public IReadOnlyList<RestoreSpaceNeed> Shortfalls => [.. Needs.Where(need => need.IsShort)];

    /// <summary>Whether any volume has less free than the run needs.</summary>
    public bool IsShort => Needs.Any(need => need.IsShort);

    /// <summary>Why the run would not fit, naming the space needed, the space free and where, for each short volume.</summary>
    public string Refusal() => IsShort
        ? $"The restore would not fit: it {string.Join(", and ", Shortfalls.Select(need => need.Describe()))}."
        : "The restore fits.";
}

/// <summary>
/// Whether a restore fits where it will write, before it writes
/// (FR-RST-003; ADR-0083).
/// </summary>
/// <remarks>
/// <para>
/// A file needs the bytes restoring it writes: its segments, because a hole
/// is never written (FR-ARCH-013), rounded up to whole clusters. A directory
/// the run creates needs a cluster. A symlink, and anything else not
/// materialised as a file, needs nothing measurable. The engine holds each
/// file in its working directory until its hash verifies, so that volume
/// needs room for the largest file too.
/// </para>
/// <para>
/// A run in place is credited only what its existing-file policy frees.
/// Overwriting frees each file once its replacement lands, so a run that
/// overwrites needs its growth plus the largest replacement in flight.
/// Moving aside stays on the volume and keeping both frees nothing. A file
/// the policy fails is not written. A quarantined run lands in a directory
/// of its own, so nothing there is in the way.
/// </para>
/// <para>
/// It is an estimate that errs high. A volume that compresses what it stores
/// can hold more than this says, so a run may be told to ignore it. One the
/// platform will not describe is never short: the check exists to stop a disk
/// filling, not to stop a restore because a platform would not answer.
/// </para>
/// </remarks>
public static class RestoreSpace
{
    /// <summary>
    /// The allocation unit a file's bytes are rounded up to: the default
    /// cluster of NTFS, APFS and ext4 alike.
    /// </summary>
    public const ulong ClusterBytes = 4096;

    /// <summary>The bytes <paramref name="dataBytes"/> occupy once written, in whole clusters.</summary>
    public static ulong Allocated(ulong dataBytes) =>
        dataBytes / ClusterBytes * ClusterBytes + (dataBytes % ClusterBytes == 0 ? 0 : ClusterBytes);

    /// <summary>The bytes restoring <paramref name="manifest"/> writes: its segments, its holes skipped.</summary>
    public static ulong WrittenBytes(FileVersionManifest manifest)
    {
        ThrowHelper.ThrowIfNull(manifest);
        return manifest.SegmentReferences.Aggregate(0ul, (sum, reference) => sum + (ulong)reference.LogicalLength);
    }

    /// <summary>
    /// What each file writes, read from its manifest through
    /// <paramref name="reader"/>. A manifest that will not read answers null,
    /// and the measure takes that file at its logical length.
    /// </summary>
    public static Func<RestorePlanItem, CancellationToken, ValueTask<ulong?>> WrittenBytesThrough(RepositoryReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        return async (item, cancellationToken) =>
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
                return WrittenBytes(FileVersionManifestCodec.Decode(read.Plaintext));
            }
            catch (FormatException)
            {
                return null;
            }
        };
    }

    /// <summary>What each file writes, as the plan probe found it (<see cref="RestoreBlobSetResult.Facts"/>).</summary>
    public static Func<RestorePlanItem, CancellationToken, ValueTask<ulong?>> WrittenBytesFrom(
        IReadOnlyDictionary<ObjectId, RestoreItemFacts> facts)
    {
        ThrowHelper.ThrowIfNull(facts);
        return (item, _) => ValueTask.FromResult(
            facts.TryGetValue(item.ObjectId, out var known) ? known.WrittenBytes : (ulong?)null);
    }

    /// <summary>
    /// What a run of <paramref name="slices"/> needs on each volume it writes
    /// to, against what is free there.
    /// </summary>
    /// <param name="slices">The run's slices, in the order it writes them.</param>
    /// <param name="mode">Where restored content lands.</param>
    /// <param name="existing">What the run does about a file already at a destination.</param>
    /// <param name="probe">How the platform is asked.</param>
    /// <param name="writtenBytes">
    /// What each file writes; null takes every file at its logical length,
    /// which the catalogue knows without a read.
    /// </param>
    /// <param name="cancellationToken">Cancels the measure.</param>
    public static async ValueTask<RestoreSpaceReport> MeasureAsync(
        IReadOnlyList<RestoreSlice> slices,
        RestoreDestinationMode mode,
        ExistingDestinationPolicy existing,
        RestoreSpaceProbe probe,
        Func<RestorePlanItem, CancellationToken, ValueTask<ulong?>>? writtenBytes,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(slices);
        ThrowHelper.ThrowIfNull(probe);

        var volumes = new List<Volume>();
        ulong writes = 0;
        ulong largest = 0;

        foreach (var slice in slices)
        {
            ThrowHelper.ThrowIfNull(slice);
            var root = new DirectoryInfo(slice.OutputDirectory).FullName;
            ulong need = 0;
            ulong inFlight = 0;

            foreach (var item in slice.Plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Only a run in place can find anything in its way.
                string? destination = null;
                if (mode == RestoreDestinationMode.InPlace
                    && RestoreExecutor.TryResolve(root, item.Path, out var resolved, out _))
                {
                    destination = resolved;
                }

                if (item.Kind == EntryKind.DirectoryPlaceholder)
                {
                    need += destination is not null && Directory.Exists(destination) ? 0 : ClusterBytes;
                    continue;
                }

                if (item.Kind != EntryKind.File)
                {
                    continue;
                }

                var replaced = 0ul;
                if (destination is not null && File.Exists(destination))
                {
                    if (existing == ExistingDestinationPolicy.Fail)
                    {
                        continue;
                    }

                    if (existing == ExistingDestinationPolicy.Replace)
                    {
                        replaced = Allocated((ulong)new FileInfo(destination).Length);
                    }
                }

                var data = writtenBytes is null
                    ? item.Length
                    : await writtenBytes(item, cancellationToken).ConfigureAwait(false) ?? item.Length;
                var allocated = Allocated(data);

                writes += data;
                need += allocated > replaced ? allocated - replaced : 0;
                inFlight = Math.Max(inFlight, Math.Min(allocated, replaced));
                largest = Math.Max(largest, allocated);
            }

            Add(volumes, Identify(slice.OutputDirectory, probe), slice.OutputDirectory, need, inFlight);
        }

        if (largest > 0)
        {
            var working = Identify(probe.WorkingDirectory, probe);
            var shared = volumes.FindIndex(volume => volume.Key == working);
            if (shared >= 0)
            {
                volumes[shared] = volumes[shared] with { Bytes = volumes[shared].Bytes + largest };
            }
            else
            {
                volumes.Add(new Volume(working, probe.WorkingDirectory, largest, 0, Working: true));
            }
        }

        return new RestoreSpaceReport(
            [
                .. volumes.Select(volume => new RestoreSpaceNeed(
                    volume.Directory,
                    volume.Bytes + volume.InFlight,
                    probe.AvailableBytes(NearestExisting(volume.Directory)),
                    volume.Working)),
            ],
            writes,
            writtenBytes is not null);
    }

    /// <summary>
    /// A run's space as a run measures it: from logical lengths, which cost
    /// nothing, and again from every manifest only when they say it is short.
    /// </summary>
    /// <remarks>
    /// A run that fits by logical length has its answer without a read, so
    /// the check costs a restore nothing on its read budget (NFR-PERF-009)
    /// unless the disk is nearly full.
    /// </remarks>
    public static async ValueTask<RestoreSpaceReport> MeasureRunAsync(
        IReadOnlyList<RestoreSlice> slices,
        RestoreDestinationMode mode,
        ExistingDestinationPolicy existing,
        RestoreSpaceProbe probe,
        RepositoryReader reader,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(reader);

        var byLength = await MeasureAsync(slices, mode, existing, probe, writtenBytes: null, cancellationToken)
            .ConfigureAwait(false);

        return byLength.IsShort
            ? await MeasureAsync(slices, mode, existing, probe, WrittenBytesThrough(reader), cancellationToken)
                .ConfigureAwait(false)
            : byLength;
    }

    /// <summary>A byte count for a person: exact, and in binary units once it is large enough to need them.</summary>
    public static string Bytes(ulong bytes)
    {
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes");
        }

        string[] units = ["KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        var value = bytes / 1024.0;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes ({value:0.0} {units[unit]})");
    }

    /// <summary>The volume holding <paramref name="directory"/>, or the directory itself when the platform will not say.</summary>
    private static (ulong? Volume, string? Path) Identify(string directory, RestoreSpaceProbe probe)
    {
        var existing = NearestExisting(directory);
        return probe.VolumeOf(existing) is { } volume ? (volume, null) : (null, existing);
    }

    /// <summary>
    /// <paramref name="path"/>, or its nearest ancestor that exists: where a
    /// directory the run will create would land.
    /// </summary>
    private static string NearestExisting(string path)
    {
        var current = Path.GetFullPath(path);
        while (!Directory.Exists(current) && !File.Exists(current) && Path.GetDirectoryName(current) is { } parent)
        {
            current = parent;
        }

        return current;
    }

    private static void Add(List<Volume> volumes, (ulong? Volume, string? Path) key, string directory, ulong bytes, ulong inFlight)
    {
        var index = volumes.FindIndex(volume => volume.Key == key);
        if (index < 0)
        {
            volumes.Add(new Volume(key, directory, bytes, inFlight, Working: false));
            return;
        }

        // A second slice on a volume already measured: its bytes add, and
        // the run overwrites one file at a time, so the largest replacement
        // in flight is the larger of the two rather than their sum.
        volumes[index] = volumes[index] with
        {
            Bytes = volumes[index].Bytes + bytes,
            InFlight = Math.Max(volumes[index].InFlight, inFlight),
        };
    }

    private sealed record Volume((ulong? Volume, string? Path) Key, string Directory, ulong Bytes, ulong InFlight, bool Working);
}
