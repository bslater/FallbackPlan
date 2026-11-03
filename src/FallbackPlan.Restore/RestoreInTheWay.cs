using System.Globalization;
using Bodu;
using FallbackPlan.Domain;

namespace FallbackPlan.Restore;

/// <summary>A file already where a restore would write, which a flag keeps where it stands.</summary>
/// <param name="Destination">Where the file stands.</param>
/// <param name="Flags">The flags that keep it there: immutable, append-only, or both.</param>
public sealed record HeldInPlace(string Destination, FileFlagBits Flags);

/// <summary>A folder already in a restore's way, which a flag keeps from taking what the run would put there.</summary>
/// <param name="Folder">Where the folder stands.</param>
/// <param name="Flags">The flags that keep it: immutable, append-only, or both.</param>
/// <param name="Items">How many of the run's items it fails.</param>
public sealed record FolderHeld(string Folder, FileFlagBits Flags, int Items);

/// <summary>
/// What a restore's plan says of the files already in its way that a flag
/// keeps where they stand (FR-RST-003; ADR-0180 Amendment 6), and of the
/// folders a flag keeps from taking what the run would put there (ADR-0180
/// Amendment 8). An immutable or append-only file can be neither moved aside
/// nor replaced, and such a folder refuses a name made in it or a rename, so
/// the run fails each item it meets with the platform's refusal. The plan says
/// so before anything is written. No flag is cleared: that is the person's to
/// do.
/// </summary>
public static class RestoreInTheWay
{
    /// <summary>The flags that keep a file from being moved or replaced.</summary>
    private const FileFlagBits Holding = FileFlagBits.Immutable | FileFlagBits.AppendOnly;

    /// <summary>
    /// The files of <paramref name="slices"/> that a run under
    /// <paramref name="existing"/> would move aside or replace, and that a
    /// flag keeps where they stand.
    /// </summary>
    /// <remarks>
    /// A run meets what is already there only in place, or where a resume
    /// lands where its run did, and leaves a file that run landed, at its
    /// length, as it stands: the space check's rule. A run that keeps both
    /// writes beside the file, and one that fails what is there fails it
    /// whatever its flags, so neither is held. A file and a link each move
    /// aside or replace what stands in their place; a folder is made where it
    /// is, which no policy reaches (ADR-0086), so it is not asked.
    /// </remarks>
    /// <param name="slices">The run's slices.</param>
    /// <param name="mode">Where the run lands.</param>
    /// <param name="existing">What it does with a file in its way.</param>
    /// <param name="flags">Reads what stands.</param>
    /// <returns>Each file held, in the slices' order.</returns>
    public static IReadOnlyList<HeldInPlace> HeldByFlags(
        IReadOnlyList<RestoreSlice> slices,
        RestoreDestinationMode mode,
        ExistingDestinationPolicy existing,
        IFileFlags flags)
    {
        ThrowHelper.ThrowIfNull(slices);
        ThrowHelper.ThrowIfNull(flags);

        if (existing is not (ExistingDestinationPolicy.Preserve or ExistingDestinationPolicy.Replace))
        {
            return [];
        }

        var held = new List<HeldInPlace>();
        foreach (var slice in slices)
        {
            ThrowHelper.ThrowIfNull(slice);
            var root = new DirectoryInfo(slice.OutputDirectory).FullName;
            foreach (var item in slice.Plan.Items.Where(item => item.Kind is EntryKind.File or EntryKind.Symlink))
            {
                var destination = RestoreSpace.DestinationOf(slice, mode, root, item);
                if (destination is null || !File.Exists(destination) || RestoreSpace.IsLanded(slice, destination, item))
                {
                    continue;
                }

                if ((flags.Read(destination) & Holding) is var holding && holding != FileFlagBits.None)
                {
                    held.Add(new HeldInPlace(destination, holding));
                }
            }
        }

        return held;
    }

    /// <summary>
    /// The folders already standing that the run would write into, and that a
    /// flag keeps from taking what it would put there (ADR-0180 Amendment 8).
    /// </summary>
    /// <remarks>
    /// Each item makes its first new name in the nearest folder that stands
    /// where it lands: the item itself, or the first folder of a path below
    /// that the run would make. A folder item that stands makes nothing, and a
    /// file a resume landed is left. A quarantined run afresh makes only its
    /// own folder in the output. An immutable folder takes no new name, so it
    /// fails every item whose first new name is made in it. An append-only
    /// folder takes a new name but lets none be renamed or removed, so it
    /// fails a file landing in it, which lands by renaming its spool, and
    /// whatever the run would move aside or replace in it; a link or a folder
    /// made new there it takes. No policy helps: a copy kept beside lands by a
    /// rename too, and only failing what stands spares the folder that item.
    /// </remarks>
    /// <param name="slices">The run's slices.</param>
    /// <param name="mode">Where the run lands.</param>
    /// <param name="existing">What it does with a file in its way.</param>
    /// <param name="flags">Reads what stands.</param>
    /// <returns>Each folder held, once, in the order the run first reaches it.</returns>
    public static IReadOnlyList<FolderHeld> FoldersHeld(
        IReadOnlyList<RestoreSlice> slices,
        RestoreDestinationMode mode,
        ExistingDestinationPolicy existing,
        IFileFlags flags)
    {
        ThrowHelper.ThrowIfNull(slices);
        ThrowHelper.ThrowIfNull(flags);

        var made = new Dictionary<string, int>(StringComparer.Ordinal);
        var renamed = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();

        void Count(Dictionary<string, int> counts, string folder)
        {
            if (!made.ContainsKey(folder) && !renamed.ContainsKey(folder))
            {
                order.Add(folder);
            }

            counts[folder] = counts.GetValueOrDefault(folder) + 1;
        }

        foreach (var slice in slices)
        {
            ThrowHelper.ThrowIfNull(slice);
            var root = new DirectoryInfo(slice.OutputDirectory).FullName;
            if (mode == RestoreDestinationMode.Quarantine && slice.ResumedIn is null)
            {
                // Afresh, the run's first new name is its own folder.
                if (StandingFolder(Path.Combine(root, RestoreExecutionOptions.DefaultQuarantineDirectory, "run")) is { } output)
                {
                    foreach (var _ in slice.Plan.Items)
                    {
                        Count(made, output);
                    }
                }

                continue;
            }

            foreach (var item in slice.Plan.Items.Where(item => item.Kind is EntryKind.File or EntryKind.Symlink or EntryKind.DirectoryPlaceholder))
            {
                var destination = RestoreSpace.DestinationOf(slice, mode, root, item);
                if (destination is null
                    || (item.Kind == EntryKind.DirectoryPlaceholder && Directory.Exists(destination))
                    || RestoreSpace.IsLanded(slice, destination, item))
                {
                    continue;
                }

                var parent = Path.GetDirectoryName(destination)!;
                if (!Directory.Exists(parent))
                {
                    if (StandingFolder(parent) is { } above)
                    {
                        Count(made, above);
                    }

                    continue;
                }

                var stands = File.Exists(destination) || new FileInfo(destination).LinkTarget is not null;
                if (stands && existing == ExistingDestinationPolicy.Fail)
                {
                    continue;
                }

                Count(made, parent);
                if (item.Kind == EntryKind.File
                    || (item.Kind == EntryKind.Symlink && stands && existing != ExistingDestinationPolicy.WriteBeside))
                {
                    Count(renamed, parent);
                }
            }
        }

        var held = new List<FolderHeld>();
        foreach (var folder in order)
        {
            var standing = flags.Read(folder);
            if (standing.HasFlag(FileFlagBits.Immutable))
            {
                held.Add(new FolderHeld(folder, standing & Holding, made[folder]));
            }
            else if (standing.HasFlag(FileFlagBits.AppendOnly) && renamed.GetValueOrDefault(folder) is > 0 and var count)
            {
                held.Add(new FolderHeld(folder, FileFlagBits.AppendOnly, count));
            }
        }

        return held;
    }

    /// <summary>The plan's line for <paramref name="held"/>.</summary>
    /// <param name="held">The folder held.</param>
    public static string Describe(FolderHeld held)
    {
        ThrowHelper.ThrowIfNull(held);

        var flags = string.Join(" and ", MetadataRules.FileFlagNamesOf(held.Flags));
        var refuses = held.Flags.HasFlag(FileFlagBits.Immutable)
            ? "the restore can add nothing to it, nor move or replace what is in it"
            : "a restored file lands by a rename, which it refuses, as it does moving aside or replacing what is in it";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{held.Folder} — {flags}: {refuses}, so the {held.Items} item(s) the run would land there fail. Clear the flag first.");
    }

    /// <summary>The nearest folder that stands at or above <paramref name="path"/>, or null where a file stands in its way.</summary>
    private static string? StandingFolder(string path)
    {
        var current = path;
        while (!Directory.Exists(current))
        {
            if (File.Exists(current) || Path.GetDirectoryName(current) is not { } parent)
            {
                return null;
            }

            current = parent;
        }

        return current;
    }

    /// <summary>The plan's line for <paramref name="held"/>, as a run under <paramref name="existing"/> would meet it.</summary>
    /// <param name="held">The file held.</param>
    /// <param name="existing">What the run would do with it.</param>
    public static string Describe(HeldInPlace held, ExistingDestinationPolicy existing)
    {
        ThrowHelper.ThrowIfNull(held);

        var flags = string.Join(" and ", MetadataRules.FileFlagNamesOf(held.Flags));
        var cannot = existing == ExistingDestinationPolicy.Replace ? "replace it" : "move it aside";
        return $"{held.Destination} — {flags}: the restore cannot {cannot}, so it fails there. "
            + "Clear the flag first, or keep both, writing the restored copy beside it.";
    }
}
