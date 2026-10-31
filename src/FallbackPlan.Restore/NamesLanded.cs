using System.Text;
using Bodu;

namespace FallbackPlan.Restore;

/// <summary>
/// The names a restore run has landed, kept so the run can tell its own file
/// from somebody else's where the volume it writes to takes two of a
/// snapshot's names for one (FR-RST-005, ADR-0170 Amendment 3).
/// </summary>
/// <remarks>
/// <para>
/// A name is kept by what a volume that folds names would take it for: its
/// composed form, compared without case. That only finds a candidate. The
/// volume decides: a destination is taken for a landed name only where its
/// folder lists no entry under the destination's own name, so the file found
/// there is the one listed under the landed name.
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
    /// Whether the destination's folder lists an entry under the destination's
    /// own name. Asked only where a landed name folds onto it.
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

    private static string Fold(string path)
    {
        try
        {
            return path.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Not well-formed Unicode, so it has no normal form; it is
            // compared by case alone.
            return path;
        }
    }
}
