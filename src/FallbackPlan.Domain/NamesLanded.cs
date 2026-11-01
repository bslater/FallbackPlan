using System.Text;
using Bodu;

namespace FallbackPlan.Domain;

/// <summary>
/// The names a restore run has landed, kept so the run can tell its own file
/// from somebody else's where the volume it writes to takes two of a
/// snapshot's names for one (FR-RST-005, ADR-0170 Amendment 3). One rule for
/// the executor and the recovery tool, which may not reference the restore
/// project (ADR-0170 Amendment 4; ADR-0152).
/// </summary>
/// <remarks>
/// <para>
/// A name is kept by what a volume that folds names would take it for: its
/// composed form with each component's trailing periods taken off, as a FAT
/// or exFAT drive takes them off (ADR-0170 Amendment 7), compared without
/// case. That only finds a candidate. The
/// volume decides: a destination is taken for a landed name only where some
/// name the path gives on the way to it, the destination's own or a
/// folder's, is not listed in its folder as written. The file found there is
/// then the one the run landed, reached through a name the volume took for
/// another (ADR-0170 Amendment 8).
/// </para>
/// <para>
/// A volume that stores a name in another form than it was given, as HFS+
/// stores one decomposed, lists the landed name in the form asked for next,
/// and is not judged here.
/// </para>
/// </remarks>
public sealed class NamesLanded
{
    private readonly Dictionary<string, string> _byFold = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keeps <paramref name="path"/> as landed; the first name landed for a fold is the one kept.</summary>
    /// <param name="path">The repository path the run landed.</param>
    public void Add(string path)
    {
        ThrowHelper.ThrowIfNull(path);
        _byFold.TryAdd(Fold(path), path);
    }

    /// <summary>
    /// The name this run landed that the volume took <paramref name="path"/>
    /// for, or null where the destination is somebody else's or empty.
    /// </summary>
    /// <param name="path">The repository path about to land.</param>
    /// <param name="occupied">Whether something stands at its destination.</param>
    /// <param name="listedAsWritten">
    /// Whether every name the path gives on the way to the destination is
    /// listed in its folder as written (<see cref="ListedAsWritten"/>). Asked
    /// only where a landed name folds onto it.
    /// </param>
    /// <returns>The landed name, or null.</returns>
    public string? TakenForOne(string path, bool occupied, Func<bool> listedAsWritten)
    {
        ThrowHelper.ThrowIfNull(path);
        ThrowHelper.ThrowIfNull(listedAsWritten);
        if (!occupied
            || !_byFold.TryGetValue(Fold(path), out var landed)
            || string.Equals(landed, path, StringComparison.Ordinal))
        {
            return null;
        }

        return listedAsWritten() ? null : landed;
    }

    /// <summary>
    /// The name this run landed that the volume took <paramref name="path"/>
    /// for, asked of the disk at <paramref name="destination"/>: whether
    /// anything stands there, and whether every name the path gives on the
    /// way to it is listed in its folder as written.
    /// </summary>
    /// <param name="path">The repository path about to land.</param>
    /// <param name="destination">Where it lands.</param>
    /// <returns>The landed name, or null.</returns>
    public string? TakenForOneAt(string path, string destination)
    {
        ThrowHelper.ThrowIfNull(destination);
        return TakenForOne(path, Occupied(destination), () => ListedAsWritten(path, destination, NamesIn));
    }

    /// <summary>
    /// Whether every name <paramref name="path"/> gives on the way to
    /// <paramref name="destination"/>, the destination's own and each
    /// folder's, is listed in its folder as written. No folder above those
    /// the path names is asked: the place a restore writes to is the
    /// operator's to name.
    /// </summary>
    /// <param name="path">The repository path, whose names end the destination.</param>
    /// <param name="destination">Where it lands.</param>
    /// <param name="namesIn">
    /// The names a folder lists, or null where it cannot be listed, which is
    /// taken to list the name, so what stands there is met as it was.
    /// </param>
    /// <returns>Whether each name is listed as written.</returns>
    public static bool ListedAsWritten(string path, string destination, Func<string, IEnumerable<string>?> namesIn)
    {
        ThrowHelper.ThrowIfNull(path);
        ThrowHelper.ThrowIfNull(destination);
        ThrowHelper.ThrowIfNull(namesIn);
        var at = destination;
        for (var names = path.Split('/').Length; names > 0; names--)
        {
            var name = Path.GetFileName(at);
            if (Path.GetDirectoryName(at) is not { } folder)
            {
                return true;
            }

            if (namesIn(folder) is { } listed && !listed.Contains(name, StringComparer.Ordinal))
            {
                return false;
            }

            at = folder;
        }

        return true;
    }

    /// <summary>Whether anything stands at <paramref name="path"/>: a file, a folder, or a link wherever it leads.</summary>
    private static bool Occupied(string path) =>
        File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    /// <summary>The names <paramref name="folder"/> lists, or null where it cannot be listed.</summary>
    private static string[]? NamesIn(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFileSystemEntries(folder).Select(entry => Path.GetFileName(entry))];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Fold(string path)
    {
        var stripped = string.Join('/', path.Split('/').Select(component => component.TrimEnd('.')));
        try
        {
            return stripped.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Not well-formed Unicode, so it has no normal form; it is
            // compared by case alone.
            return stripped;
        }
    }
}
