using System.Text;
using System.Text.Json;
using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Agent;

/// <summary>
/// What a set's last backup kept for the next (ADR-0188): which backup it
/// was, when the set was last walked in full, and where the change journal
/// stood for each of the set's folders before that backup walked.
/// </summary>
internal sealed record KeptJournalPlaces(
    string SnapshotId, ulong LastFullWalk, IReadOnlyList<(string Root, JournalPosition Place)> Places);

/// <summary>
/// What one backup is told, and what it will keep (ADR-0188): the folders
/// that changed since the set's last backup, or null to walk in full; where
/// the journal stood for each folder before this walk, or null where it
/// keeps none for one; and when the set was last walked in full.
/// </summary>
internal sealed record ChangeHintPlan(
    IReadOnlyCollection<string>? ChangedFolders,
    IReadOnlyList<(string Root, JournalPosition Place)>? Places,
    ulong? LastFullWalk);

/// <summary>
/// How the service tells a backup what changed since the set's last one,
/// from the change journal (ADR-0188). Every doubt walks in full.
/// </summary>
internal static class ChangeHints
{
    /// <summary>How long a set may go without a full walk, whatever the journal says.</summary>
    public static readonly TimeSpan FullWalkEvery = TimeSpan.FromDays(1);

    /// <summary>
    /// What a backup of <paramref name="set"/> is told, and where the journal
    /// stands now for each of its folders. The places are taken first, before
    /// the walk, so what changes while it walks is the next backup's to hear.
    /// </summary>
    public static ChangeHintPlan Plan(
        IChangeJournal? journal, KeptJournalPlaces? kept, BackupSetConfiguration set,
        ReadOnlyMemory<byte>? prior, ulong nowMs, bool full)
    {
        ThrowHelper.ThrowIfNull(set);
        if (journal is null)
        {
            return new ChangeHintPlan(null, null, null);
        }

        var roots = SetChangeScan.ScanRootsOf(set);
        List<(string Root, JournalPosition Place)>? places = [];
        foreach (var root in roots)
        {
            if (journal.PositionOf(root.Path) is not { } place)
            {
                places = null;
                break;
            }

            places.Add((root.Path, place));
        }

        var changed = full || prior is not { } priorId || kept is null
            ? null
            : Changed(journal, kept, roots, priorId, nowMs);
        return new ChangeHintPlan(changed, places, kept?.LastFullWalk);
    }

    /// <summary>
    /// What a backup that published <paramref name="snapshotId"/> keeps for
    /// the next, or null where it found no place for one of its folders.
    /// </summary>
    public static KeptJournalPlaces? Kept(ChangeHintPlan plan, ReadOnlySpan<byte> snapshotId, ulong nowMs)
    {
        ThrowHelper.ThrowIfNull(plan);
        return plan.Places is { } places
            ? new KeptJournalPlaces(
                Convert.ToHexStringLower(snapshotId),
                plan.ChangedFolders is null || plan.LastFullWalk is not { } last ? nowMs : last,
                places)
            : null;
    }

    /// <summary>
    /// A folder the journal named, as the snapshot spells it: its path below
    /// its root, after the root's label in a set of several folders.
    /// </summary>
    internal static string SnapshotPath(IReadOnlyList<ScanRoot> roots, ChangedFolder folder)
    {
        ThrowHelper.ThrowIfNull(roots);
        ThrowHelper.ThrowIfNull(folder);
        if (roots.Count == 1)
        {
            return folder.Path;
        }

        var label = roots.First(root => string.Equals(root.Path, folder.Root, StringComparison.Ordinal)).Label
            ?? throw new ArgumentException("A set of several folders names each by its label.", nameof(roots));
        return folder.Path.Length == 0 ? label : label + "/" + folder.Path;
    }

    /// <summary>
    /// The folders that changed since the set's last backup, or null where
    /// the run walks in full: the places kept are another backup's, a full
    /// walk is due, the set's folders are not the ones kept, or the journal
    /// cannot say for one.
    /// </summary>
    private static HashSet<string>? Changed(
        IChangeJournal journal, KeptJournalPlaces kept, IReadOnlyList<ScanRoot> roots, ReadOnlyMemory<byte> prior, ulong nowMs)
    {
        // A clock that went back is a doubt too: the walk is due when it cannot be told.
        if (!string.Equals(kept.SnapshotId, Convert.ToHexStringLower(prior.Span), StringComparison.Ordinal)
            || nowMs < kept.LastFullWalk
            || nowMs - kept.LastFullWalk >= (ulong)FullWalkEvery.TotalMilliseconds
            || !kept.Places.Select(place => place.Root).SequenceEqual(roots.Select(root => root.Path), StringComparer.Ordinal))
        {
            return null;
        }

        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (root, place) in kept.Places)
        {
            if (journal.ChangedSince(place, [root]) is not { } named)
            {
                return null;
            }

            foreach (var folder in named)
            {
                changed.Add(SnapshotPath(roots, folder));
            }
        }

        return changed;
    }
}

/// <summary>
/// Where each set's last backup kept its journal places (ADR-0188): one file
/// per set under <c>&lt;state&gt;/change-journal/</c>, by the set's id,
/// replaced atomically. A file that does not read is no place, and the next
/// backup walks in full, which is all a lost place costs.
/// </summary>
internal sealed class JournalPlaceStore(string stateDirectory)
{
    private string Root => Path.Combine(stateDirectory, "change-journal");

    /// <summary>What the set's last backup kept, or null where it kept nothing that reads.</summary>
    public KeptJournalPlaces? Read(string setId)
    {
        var path = PathFor(setId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(AtomicFile.ReadAllText(path));
            var root = document.RootElement;
            return new KeptJournalPlaces(
                root.GetProperty("snapshot").GetString() ?? throw new FormatException("no snapshot"),
                root.GetProperty("lastFullWalk").GetUInt64(),
                [.. root.GetProperty("places").EnumerateArray().Select(static place => (
                    place.GetProperty("root").GetString() ?? throw new FormatException("no root"),
                    new JournalPosition(
                        place.GetProperty("volume").GetString() ?? throw new FormatException("no volume"),
                        place.GetProperty("journal").GetUInt64(),
                        place.GetProperty("usn").GetInt64())))]);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keeps what a backup leaves the next, or lets go of what an earlier one kept where it leaves nothing.</summary>
    public void Keep(string setId, KeptJournalPlaces? kept)
    {
        var path = PathFor(setId);
        if (kept is null)
        {
            File.Delete(path);
            return;
        }

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("snapshot", kept.SnapshotId);
            writer.WriteNumber("lastFullWalk", kept.LastFullWalk);
            writer.WriteStartArray("places");
            foreach (var (root, place) in kept.Places)
            {
                writer.WriteStartObject();
                writer.WriteString("root", root);
                writer.WriteString("volume", place.Volume);
                writer.WriteNumber("journal", place.JournalId);
                writer.WriteNumber("usn", place.Usn);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        AtomicFile.WriteAllText(path, Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }

    private string PathFor(string setId)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(setId);
        if (setId.Length != 32 || !setId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException($"A set id is 32 hex characters; '{setId}' is not one.", nameof(setId));
        }

        return Path.Combine(Root, $"{setId.ToLowerInvariant()}.json");
    }
}
