namespace FallbackPlan.Filesystem;

/// <summary>
/// A place in a volume's change journal (ADR-0187): the volume, the
/// journal's identity, which is new whenever the journal is made anew, and
/// the number the journal's next record will carry.
/// </summary>
/// <param name="Volume">The volume, by the name that stays its own whatever drive letter it has.</param>
/// <param name="JournalId">The journal's identity.</param>
/// <param name="Usn">The number of the next record the journal will write.</param>
public sealed record JournalPosition(string Volume, ulong JournalId, long Usn);

/// <summary>
/// A folder a change journal names (ADR-0187): the root it is under, as the
/// caller spelled it, and its path below that root, names joined by
/// <c>/</c> and spelled as the volume holds them. The root itself is the
/// empty string.
/// </summary>
public sealed record ChangedFolder(string Root, string Path);

/// <summary>
/// What a volume's change journal says changed since a place in it
/// (ADR-0187). An answer the journal cannot be sure of is no answer, and
/// the caller walks in full.
/// </summary>
public interface IChangeJournal
{
    /// <summary>
    /// Where the journal of the volume holding <paramref name="folder"/>
    /// stands now, or null where the folder is not there or its volume keeps
    /// no journal this can read.
    /// </summary>
    JournalPosition? PositionOf(string folder);

    /// <summary>
    /// Every folder at or below one of <paramref name="roots"/> in which
    /// anything changed since <paramref name="since"/>: an entry made,
    /// removed, renamed or changed, or the folder's own metadata, a file's
    /// every name counted. Null where the journal cannot say: it is another
    /// journal, it no longer holds every record since then, a root is on
    /// another volume, or a folder it names cannot be found.
    /// </summary>
    IReadOnlyList<ChangedFolder>? ChangedSince(JournalPosition since, IReadOnlyCollection<string> roots);
}
