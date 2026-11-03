using Bodu;
using FallbackPlan.Domain;

namespace FallbackPlan.Restore;

/// <summary>A file already where a restore would write, which a flag keeps where it stands.</summary>
/// <param name="Destination">Where the file stands.</param>
/// <param name="Flags">The flags that keep it there: immutable, append-only, or both.</param>
public sealed record HeldInPlace(string Destination, FileFlagBits Flags);

/// <summary>
/// What a restore's plan says of the files already in its way that a flag
/// keeps where they stand (FR-RST-003; ADR-0180 Amendment 6). An immutable or
/// append-only file can be neither moved aside nor replaced, so a run that
/// would do either fails it with the platform's refusal. The plan says so
/// before anything is written. No flag is cleared: that is the person's to do.
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
