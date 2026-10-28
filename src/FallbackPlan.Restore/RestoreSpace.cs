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
/// <param name="ResumedIn">
/// Where the run this one resumes landed it (ADR-0109): the output itself in
/// place, or that run's quarantine folder. Null for a run that resumes none.
/// </param>
/// <param name="LabelPrefix">
/// The root's label and a slash, which the slice's paths shed from the
/// plan's (ADR-0041), or empty where there is one root.
/// </param>
public sealed record RestoreSlice(RestorePlan Plan, string OutputDirectory, string? ResumedIn = null, string LabelPrefix = "");

/// <summary>What restoring one file writes, and how much of that its run already holds.</summary>
/// <param name="Written">Its segments' bytes, its holes skipped.</param>
/// <param name="Held">
/// What of <paramref name="Written"/> a resume takes up from the part its run
/// kept, which the working space already holds (ADR-0136). None for a run
/// afresh.
/// </param>
public readonly record struct RestoreFileBytes(ulong Written, ulong Held = 0);

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
/// <param name="Working">Whether this is the engine's working directory alone, which needs room for the largest file less what a part holds of it.</param>
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
/// <param name="Landed">
/// The files a resumed run already landed where it looks, at their length,
/// which need no room (FR-RST-008, ADR-0120). None for a run that resumes
/// nothing.
/// </param>
public sealed record RestoreSpaceReport(IReadOnlyList<RestoreSpaceNeed> Needs, ulong WriteBytes, bool Exact, int Landed = 0)
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
/// A resume takes up what each part its run kept proves, which the working
/// directory already holds, so there it needs only the rest of each file,
/// the most of any one file. Measured from manifests alone, which name the
/// parts: a measure by logical length credits nothing. The folder the file
/// lands in still needs all of it (ADR-0136).
/// </para>
/// <para>
/// A resumed run leaves a file an earlier one landed as it stands, so a file
/// already where that run landed it, at its length, needs nothing. The run
/// checks its hash when it reaches it; the measure goes by length, which
/// costs nothing to ask. A file of that length whose content differs is
/// landed under the policy, and the estimate erred low for it.
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
    /// The bytes the first <paramref name="segments"/> of a file's segments
    /// write: what a part holds of it that holds them. The codec keeps a
    /// manifest's segments in the file's order.
    /// </summary>
    /// <param name="manifest">The file's manifest.</param>
    /// <param name="segments">How many of its segments, from its start.</param>
    public static ulong HeldBytes(FileVersionManifest manifest, int segments)
    {
        ThrowHelper.ThrowIfNull(manifest);
        return manifest.SegmentReferences.Take(segments)
            .Aggregate(0ul, (sum, reference) => sum + (ulong)reference.LogicalLength);
    }

    /// <summary>
    /// What each file writes, read from its manifest through
    /// <paramref name="reader"/>. A manifest that will not read answers null,
    /// and the measure takes that file at its logical length. A later name of
    /// a hard-link group the run makes one file again writes nothing
    /// (ADR-0102), so a tree of links is measured once, not once a name.
    /// </summary>
    public static Func<RestorePlanItem, CancellationToken, ValueTask<RestoreFileBytes?>> WrittenBytesThrough(RepositoryReader reader) =>
        WrittenBytesThrough(reader, partsDirectory: null);

    /// <summary>
    /// What each file writes, as the overload without parts says, and what a
    /// resume holds of it in the part its run kept in
    /// <paramref name="partsDirectory"/>: as far as the part proves, read as
    /// the resume would take it up, writing nothing (ADR-0136).
    /// </summary>
    /// <param name="reader">Reads each manifest, and proves each part's segments.</param>
    /// <param name="partsDirectory">Where a resume's run kept its parts, or null for a run afresh.</param>
    public static Func<RestorePlanItem, CancellationToken, ValueTask<RestoreFileBytes?>> WrittenBytesThrough(
        RepositoryReader reader, string? partsDirectory)
    {
        ThrowHelper.ThrowIfNull(reader);

        var linked = new HashSet<string>(StringComparer.Ordinal);
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

            FileVersionManifest manifest;
            try
            {
                manifest = FileVersionManifestCodec.Decode(read.Plaintext);
            }
            catch (FormatException)
            {
                return null;
            }

            if (RestoreHardLinks.KeyOf(manifest) is { } key && !linked.Add(key))
            {
                return new RestoreFileBytes(0);
            }

            var held = partsDirectory is null
                ? 0
                : await RestorePart.HeldSegmentsAsync(
                    Path.Combine(partsDirectory, RestorePart.NameOf(manifest)),
                    manifest,
                    (reference, plaintext) => reader.Proves(reference, plaintext),
                    cancellationToken).ConfigureAwait(false);
            return new RestoreFileBytes(WrittenBytes(manifest), HeldBytes(manifest, held));
        };
    }

    /// <summary>
    /// What each file writes, and what a resume holds of it, as the plan
    /// probe found them (<see cref="RestoreBlobSetResult.Facts"/>), a later
    /// name of a hard-link group nothing, as
    /// <see cref="WrittenBytesThrough(RepositoryReader)"/> says.
    /// </summary>
    public static Func<RestorePlanItem, CancellationToken, ValueTask<RestoreFileBytes?>> WrittenBytesFrom(
        IReadOnlyDictionary<ObjectId, RestoreItemFacts> facts)
    {
        ThrowHelper.ThrowIfNull(facts);

        var linked = new HashSet<string>(StringComparer.Ordinal);
        return (item, _) => ValueTask.FromResult(
            !facts.TryGetValue(item.ObjectId, out var known)
                ? (RestoreFileBytes?)null
                : known.HardLink is { } key && !linked.Add(key)
                    ? new RestoreFileBytes(0)
                    : new RestoreFileBytes(known.WrittenBytes, known.HeldBytes));
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
    /// What each file writes, and what a resume holds of it; null takes every
    /// file at its logical length, which the catalogue knows without a read,
    /// and holds none of it.
    /// </param>
    /// <param name="cancellationToken">Cancels the measure.</param>
    public static async ValueTask<RestoreSpaceReport> MeasureAsync(
        IReadOnlyList<RestoreSlice> slices,
        RestoreDestinationMode mode,
        ExistingDestinationPolicy existing,
        RestoreSpaceProbe probe,
        Func<RestorePlanItem, CancellationToken, ValueTask<RestoreFileBytes?>>? writtenBytes,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(slices);
        ThrowHelper.ThrowIfNull(probe);

        var volumes = new List<Volume>();
        ulong writes = 0;
        ulong largest = 0;
        var landedFiles = 0;

        foreach (var slice in slices)
        {
            ThrowHelper.ThrowIfNull(slice);
            var root = new DirectoryInfo(slice.OutputDirectory).FullName;
            ulong need = 0;
            ulong inFlight = 0;

            foreach (var item in slice.Plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = DestinationOf(slice, mode, root, item);

                if (item.Kind == EntryKind.DirectoryPlaceholder)
                {
                    need += destination is not null && Directory.Exists(destination) ? 0 : ClusterBytes;
                    continue;
                }

                if (item.Kind != EntryKind.File)
                {
                    continue;
                }

                if (IsLanded(slice, destination, item))
                {
                    landedFiles++;
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

                var measured = writtenBytes is null ? null : await writtenBytes(item, cancellationToken).ConfigureAwait(false);
                var data = measured?.Written ?? item.Length;
                var allocated = Allocated(data);

                writes += data;
                need += allocated > replaced ? allocated - replaced : 0;
                inFlight = Math.Max(inFlight, Math.Min(allocated, replaced));
                largest = Math.Max(largest, Allocated(data - Math.Min(measured?.Held ?? 0, data)));
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
            writtenBytes is not null,
            landedFiles);
    }

    /// <summary>
    /// The files a resume will find its run landed, by the rule its measure
    /// counts them by (ADR-0120): at their length where the resume lands
    /// them. Named as the plan names them, each behind its slice's label
    /// prefix, so a plan's probe can leave their content unread (ADR-0132).
    /// </summary>
    /// <param name="slices">The run's slices, each with where the run it resumes landed it.</param>
    /// <param name="mode">How the run lands.</param>
    public static IReadOnlySet<string> LandedPaths(IReadOnlyList<RestoreSlice> slices, RestoreDestinationMode mode)
    {
        ThrowHelper.ThrowIfNull(slices);

        var landed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slice in slices)
        {
            ThrowHelper.ThrowIfNull(slice);
            var root = new DirectoryInfo(slice.OutputDirectory).FullName;
            foreach (var item in slice.Plan.Items.Where(item => item.Kind == EntryKind.File))
            {
                if (IsLanded(slice, DestinationOf(slice, mode, root, item), item))
                {
                    landed.Add(slice.LabelPrefix + item.Path);
                }
            }
        }

        return landed;
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
    public static ValueTask<RestoreSpaceReport> MeasureRunAsync(
        IReadOnlyList<RestoreSlice> slices,
        RestoreDestinationMode mode,
        ExistingDestinationPolicy existing,
        RestoreSpaceProbe probe,
        RepositoryReader reader,
        CancellationToken cancellationToken) =>
        MeasureRunAsync(slices, mode, existing, probe, reader, partsDirectory: null, cancellationToken);

    /// <summary>
    /// A resume's space as the overload without parts measures it, crediting
    /// its working space, once it reads the manifests, what each part its run
    /// kept in <paramref name="partsDirectory"/> proves (ADR-0136).
    /// </summary>
    /// <param name="slices">The run's slices, in the order it writes them.</param>
    /// <param name="mode">Where restored content lands.</param>
    /// <param name="existing">What the run does about a file already at a destination.</param>
    /// <param name="probe">How the platform is asked.</param>
    /// <param name="reader">Reads each manifest, should the run be short by logical length, and proves each part.</param>
    /// <param name="partsDirectory">Where the run it resumes kept its parts, or null for a run afresh.</param>
    /// <param name="cancellationToken">Cancels the measure.</param>
    public static async ValueTask<RestoreSpaceReport> MeasureRunAsync(
        IReadOnlyList<RestoreSlice> slices,
        RestoreDestinationMode mode,
        ExistingDestinationPolicy existing,
        RestoreSpaceProbe probe,
        RepositoryReader reader,
        string? partsDirectory,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(reader);

        var byLength = await MeasureAsync(slices, mode, existing, probe, writtenBytes: null, cancellationToken)
            .ConfigureAwait(false);

        return byLength.IsShort
            ? await MeasureAsync(slices, mode, existing, probe, WrittenBytesThrough(reader, partsDirectory), cancellationToken)
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

    /// <summary>
    /// Where <paramref name="item"/> would meet something already there: only
    /// a run in place, or one landing where an earlier run landed, can.
    /// </summary>
    private static string? DestinationOf(RestoreSlice slice, RestoreDestinationMode mode, string root, RestorePlanItem item)
    {
        var landsIn = mode == RestoreDestinationMode.InPlace
            ? root
            : slice.ResumedIn is { } resumed ? new DirectoryInfo(resumed).FullName : null;
        return landsIn is not null && RestoreExecutor.TryResolve(landsIn, item.Path, out var resolved, out _)
            ? resolved
            : null;
    }

    /// <summary>Whether a resume finds <paramref name="item"/> landed, at its length, at <paramref name="destination"/>.</summary>
    private static bool IsLanded(RestoreSlice slice, string? destination, RestorePlanItem item) =>
        slice.ResumedIn is not null && destination is not null
        && new FileInfo(destination) is { Exists: true } landed
        && (ulong)landed.Length == item.Length;

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
