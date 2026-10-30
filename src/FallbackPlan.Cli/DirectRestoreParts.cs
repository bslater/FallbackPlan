using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Domain;

namespace FallbackPlan.Cli;

/// <summary>
/// Where a restore in direct mode keeps the parts of the files it assembles
/// (FR-RST-008, ADR-0135): a folder under the state directory named by where
/// the run lands, since a run in direct mode has no name a resume could ask
/// it by. A resume to the same place finds them.
/// </summary>
/// <remarks>
/// The folders sit apart from the service's <c>restore-parts</c>, which a
/// service sweeps at start of every run it has no record of. A part holds
/// the plaintext of a backup's file, as the service's do, so it stays under
/// the state directory and never where the run writes.
/// </remarks>
internal static class DirectRestoreParts
{
    /// <summary>The file in a folder that says what its run restores and where (ADR-0142).</summary>
    private const string RecordName = "run.json";

    /// <summary>The file a run holds while it runs, so no second run there starts (ADR-0143).</summary>
    private const string HoldName = "run.lock";

    /// <summary>
    /// The file a run holds while it takes its destination's folder, beside
    /// every destination's, so two runs never judge where they land against
    /// each other at once (ADR-0157).
    /// </summary>
    private const string ClaimName = "claim.lock";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>The folder every destination's parts are kept under, in a state directory.</summary>
    /// <param name="stateDirectory">The command line's state directory.</param>
    public static string RootUnder(string stateDirectory) => Path.Combine(stateDirectory, "direct-restore-parts");

    /// <summary>The folder a run landing at <paramref name="outputDirectory"/> keeps its parts in.</summary>
    /// <param name="stateDirectory">The command line's state directory.</param>
    /// <param name="outputDirectory">Where the run lands.</param>
    public static string Of(string stateDirectory, string outputDirectory) => Of(stateDirectory, [outputDirectory]);

    /// <summary>
    /// The folder a run landing at every one of <paramref name="outputDirectories"/>
    /// keeps its parts in: a run to the original location of a set with
    /// several roots keeps one folder for all of them.
    /// </summary>
    /// <param name="stateDirectory">The command line's state directory.</param>
    /// <param name="outputDirectories">Where the run lands.</param>
    public static string Of(string stateDirectory, IEnumerable<string> outputDirectories)
    {
        var where = string.Join(
            '\n',
            outputDirectories
                .Select(directory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)))
                .Order(StringComparer.Ordinal));
        return Path.Combine(RootUnder(stateDirectory), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(where)))[..32]);
    }

    /// <summary>
    /// The bytes a folder's parts hold of the backup's files (ADR-0137), or
    /// none where it keeps none or they will not be read.
    /// </summary>
    /// <param name="parts">The folder.</param>
    public static long Held(string parts)
    {
        try
        {
            var folder = new DirectoryInfo(parts);
            return folder.Exists ? folder.EnumerateFiles("*.part").Sum(part => part.Length) : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Removes a folder's parts: a run there finished, and nothing will take
    /// them up. Best effort: a part not removed is taken up or written again
    /// by the next run there, and let go when one finishes.
    /// </summary>
    /// <param name="parts">The folder.</param>
    public static void LetGo(string parts)
    {
        try
        {
            if (Directory.Exists(parts))
            {
                Directory.Delete(parts, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Says, beside a folder's parts, what the run assembling them restores
    /// and where it lands (ADR-0142), so a listing of what direct mode keeps
    /// can say whose they are. Each run there says it again. Best effort: a
    /// folder whose record is not kept is listed without it.
    /// </summary>
    /// <param name="parts">The folder.</param>
    /// <param name="run">What the run restores and where.</param>
    public static void Record(string parts, DirectRestoreRun run)
    {
        try
        {
            AtomicFile.WriteAllText(Path.Combine(parts, RecordName), JsonSerializer.Serialize(run, SerializerOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Takes a destination's folder for one run (ADR-0143) and says beside it
    /// what the run restores and where (ADR-0142). While the run holds what
    /// this answers, a second run there is refused, and the folder is listed
    /// as under way and let go by nothing. The hold is the operating
    /// system's, so it goes with the process however that ends.
    /// </summary>
    /// <remarks>
    /// A run is refused too where a run under way lands in a folder that
    /// holds or lies inside one this run lands in, since the two would write
    /// the same files though each holds a folder of its own (ADR-0157). The
    /// judgement is <see cref="PathContainment"/>'s, which folds case, made as
    /// each folder is written and again where its links lead (ADR-0160). It is
    /// made, and the record written, under one claim of every destination's
    /// folder, so two runs starting at once each see the other. A run refused
    /// so leaves no folder it made, and keeps one a run before it left.
    /// </remarks>
    /// <param name="stateDirectory">The command line's state directory.</param>
    /// <param name="parts">The folder.</param>
    /// <param name="run">What the run restores and every folder it lands in.</param>
    /// <param name="problem">Why the folder could not be taken, where it could not.</param>
    /// <param name="overlap">The run under way whose destination nests with this one's, where one does.</param>
    /// <returns>The hold, or null where the run is refused.</returns>
    public static FileStream? Take(
        string stateDirectory, string parts, DirectRestoreRun run, out string? problem, out DirectRestoreOverlap? overlap)
    {
        overlap = null;
        var root = RootUnder(stateDirectory);
        Directory.CreateDirectory(root);
        using var claim = Claim(root, out problem);
        if (claim is null)
        {
            return null;
        }

        var made = !Directory.Exists(parts);
        var hold = Hold(parts, out problem);
        if (hold is null)
        {
            return null;
        }

        var own = Path.GetFileName(parts);
        foreach (var folder in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (!IsName(folder.Name) || string.Equals(folder.Name, own, StringComparison.Ordinal)
                || !IsUnderWay(folder.FullName) || ReadRecord(folder.FullName) is not { } other)
            {
                continue;
            }

            foreach (var theirs in LandingOf(other))
            {
                foreach (var ours in run.LandsIn ?? [])
                {
                    if (Nest(theirs, ours))
                    {
                        // A folder a run before this one left keeps what
                        // it holds; one made only to be judged is not listed.
                        hold.Dispose();
                        if (made)
                        {
                            LetGo(parts);
                        }

                        overlap = new DirectRestoreOverlap(theirs, ours);
                        return null;
                    }
                }
            }
        }

        Record(parts, run);
        return hold;
    }

    /// <summary>
    /// Whether two folders hold or lie inside one another, as written or
    /// where their links lead (ADR-0157, ADR-0160): each spelling names a
    /// folder of parts of its own, so only this finds two runs to one place
    /// by two spellings.
    /// </summary>
    private static bool Nest(string one, string other)
    {
        if (PathContainment.IsAtOrUnder(one, other) || PathContainment.IsAtOrUnder(other, one))
        {
            return true;
        }

        var realOne = RestorePaths.ThroughLinks(one);
        var realOther = RestorePaths.ThroughLinks(other);
        return PathContainment.IsAtOrUnder(realOne, realOther) || PathContainment.IsAtOrUnder(realOther, realOne);
    }

    /// <summary>Every folder a run's record says it lands in, its one destination where it said no more.</summary>
    private static IReadOnlyList<string> LandingOf(DirectRestoreRun run) =>
        run.LandsIn ?? (run.OutputDirectory is { } destination ? [destination] : []);

    /// <summary>
    /// Claims every destination's folder while a run judges where it lands.
    /// A judgement takes moments, so a run that finds the claim taken waits
    /// for it rather than being refused.
    /// </summary>
    private static FileStream? Claim(string root, out string? problem)
    {
        var path = Path.Combine(root, ClaimName);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                problem = null;
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 100)
            {
                Thread.Sleep(50);
            }
            catch (IOException exception)
            {
                problem = exception.Message;
                return null;
            }
        }
    }

    private static FileStream? Hold(string parts, out string? problem)
    {
        Directory.CreateDirectory(parts);
        try
        {
            problem = null;
            return new FileStream(Path.Combine(parts, HoldName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            problem = exception.Message;
            return null;
        }
    }

    /// <summary>Whether a run holds the folder <paramref name="parts"/> (ADR-0143).</summary>
    /// <param name="parts">The folder.</param>
    /// <returns>Whether one does.</returns>
    public static bool IsUnderWay(string parts)
    {
        var hold = Path.Combine(parts, HoldName);
        if (!File.Exists(hold))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(hold, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Whether <paramref name="name"/> is a folder's: thirty-two lowercase
    /// hex digits, as every destination's folder is named.
    /// </summary>
    /// <param name="name">What names the folder.</param>
    /// <returns>Whether it does.</returns>
    public static bool IsName(string name) =>
        name.Length == 32 && name.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    /// <summary>
    /// Each destination a run in direct mode left unfinished under
    /// <paramref name="stateDirectory"/> (ADR-0142): every folder that keeps
    /// parts, newest first by when its run began, with what its run said of
    /// itself and what its parts hold.
    /// </summary>
    /// <param name="stateDirectory">The command line's state directory.</param>
    /// <returns>Each folder kept.</returns>
    public static IReadOnlyList<KeptDirectRestoreParts> ReadKept(string stateDirectory)
    {
        var root = new DirectoryInfo(RootUnder(stateDirectory));
        if (!root.Exists)
        {
            return [];
        }

        return
        [
            .. root.EnumerateDirectories()
                .Where(folder => IsName(folder.Name))
                .Select(folder => new KeptDirectRestoreParts(
                    folder.Name, ReadRecord(folder.FullName), Held(folder.FullName), folder.LastWriteTimeUtc, IsUnderWay(folder.FullName)))
                .OrderByDescending(kept => kept.Run?.BeganAt is { } began
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)began).UtcDateTime
                    : kept.LastWritten),
        ];
    }

    /// <summary>
    /// Lets the parts the folder <paramref name="name"/> keeps go (ADR-0142),
    /// and every other destination's stay. A folder a run holds is let go by
    /// nothing (ADR-0143).
    /// </summary>
    /// <remarks>
    /// Judged under the claim a run takes its folder under (ADR-0157), so no
    /// run takes the folder between the judgement and its removal (ADR-0158).
    /// </remarks>
    /// <param name="stateDirectory">The command line's state directory.</param>
    /// <param name="name">The folder's name, as the listing gives it.</param>
    /// <param name="underWay">Whether a run holds it, so nothing of it was let go.</param>
    /// <returns>What its parts held, or null where no folder of that name is kept or a run holds it.</returns>
    /// <exception cref="ArgumentException">When the name is not a folder's.</exception>
    /// <exception cref="IOException">When the claim is not had, or the folder is not all removed.</exception>
    public static long? Forget(string stateDirectory, string name, out bool underWay)
    {
        // Checked before it is joined to a path: what is not a folder's name
        // could name anything the state directory holds, or outside it.
        if (!IsName(name))
        {
            throw new ArgumentException(null, nameof(name));
        }

        underWay = false;
        var root = RootUnder(stateDirectory);
        var parts = Path.Combine(root, name);
        if (!Directory.Exists(parts))
        {
            return null;
        }

        using var claim = Claim(root, out var problem) ?? throw new IOException(problem);
        if (!Directory.Exists(parts))
        {
            return null;
        }

        if (IsUnderWay(parts))
        {
            underWay = true;
            return null;
        }

        var held = Held(parts);
        Directory.Delete(parts, recursive: true);
        return held;
    }

    /// <summary>
    /// Lets the parts of every destination left unfinished under
    /// <paramref name="stateDirectory"/> go at once (ADR-0158), but a folder a
    /// run holds, or only those whose run began before
    /// <paramref name="begunBefore"/> (ADR-0165). Nothing else kept beside
    /// them is let go.
    /// </summary>
    /// <remarks>
    /// Judged under one claim, as <see cref="Forget"/> is, so no run takes a
    /// folder while the rest are let go. A folder not all removed is said and
    /// the rest are let go regardless.
    /// </remarks>
    /// <param name="stateDirectory">The command line's state directory.</param>
    /// <param name="begunBefore">
    /// The Unix milliseconds a run must have begun before for its parts to go,
    /// or null for every one's. A folder whose run said nothing of when it
    /// began then stays, since when it was written is not that.
    /// </param>
    /// <returns>Each destination's folder, newest first, and what became of it.</returns>
    /// <exception cref="IOException">When the claim is not had.</exception>
    public static IReadOnlyList<DirectRestoreLetGo> ForgetAll(string stateDirectory, ulong? begunBefore)
    {
        var root = RootUnder(stateDirectory);
        if (!Directory.Exists(root))
        {
            return [];
        }

        using var claim = Claim(root, out var problem) ?? throw new IOException(problem);
        var outcomes = new List<DirectRestoreLetGo>();
        foreach (var kept in ReadKept(stateDirectory))
        {
            if (kept.UnderWay)
            {
                outcomes.Add(new DirectRestoreLetGo(kept.Name, kept.Held, UnderWay: true, Problem: null));
                continue;
            }

            if (begunBefore is { } cutoff && (kept.Run is not { } run || run.BeganAt >= cutoff))
            {
                outcomes.Add(new DirectRestoreLetGo(
                    kept.Name, kept.Held, UnderWay: false, Problem: null, Undated: kept.Run is null, Younger: kept.Run is not null));
                continue;
            }

            try
            {
                Directory.Delete(Path.Combine(root, kept.Name), recursive: true);
                outcomes.Add(new DirectRestoreLetGo(kept.Name, kept.Held, UnderWay: false, Problem: null));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                outcomes.Add(new DirectRestoreLetGo(kept.Name, kept.Held, UnderWay: false, exception.Message));
            }
        }

        return outcomes;
    }

    /// <summary>What a folder's run said of itself, or null where it said nothing that reads.</summary>
    private static DirectRestoreRun? ReadRecord(string parts)
    {
        try
        {
            var path = Path.Combine(parts, RecordName);
            return File.Exists(path) ? JsonSerializer.Deserialize<DirectRestoreRun>(AtomicFile.ReadAllText(path), SerializerOptions) : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// What a run in direct mode restores and where (ADR-0142): kept beside its
/// parts, as the service keeps a run's record, but naming none of the
/// backup's files.
/// </summary>
/// <param name="SnapshotId">The snapshot it restores, in hex.</param>
/// <param name="Set">The set the snapshot is of, where one is configured here.</param>
/// <param name="OutputDirectory">Where it lands, or null where it lands each file where it was captured.</param>
/// <param name="Existing">The policy asked for a file in the way, or null for the default.</param>
/// <param name="BeganAt">When it began, in Unix milliseconds.</param>
/// <param name="LandsIn">
/// Every folder it lands in, each set's root where it restores to where the
/// files were captured, so a run whose destination nests with one under way
/// is refused (ADR-0157); null in a record written before.
/// </param>
internal sealed record DirectRestoreRun(
    [property: JsonPropertyName("snapshot_id")] string SnapshotId,
    [property: JsonPropertyName("set")] string? Set,
    [property: JsonPropertyName("output_directory")] string? OutputDirectory,
    [property: JsonPropertyName("existing")] string? Existing,
    [property: JsonPropertyName("began_at")] ulong BeganAt,
    [property: JsonPropertyName("lands_in")] IReadOnlyList<string>? LandsIn = null);

/// <summary>
/// A run under way whose destination nests with the one a run asked for
/// (ADR-0157).
/// </summary>
/// <param name="UnderWay">The folder the run under way lands in.</param>
/// <param name="Asked">The folder the refused run would land in, which holds or lies inside it.</param>
internal sealed record DirectRestoreOverlap(string UnderWay, string Asked);

/// <summary>
/// What became of one destination's folder of parts when every one was let
/// go (ADR-0158).
/// </summary>
/// <param name="Name">The folder's name.</param>
/// <param name="Held">The bytes its parts held.</param>
/// <param name="UnderWay">Whether a run holds it, so nothing of it was let go.</param>
/// <param name="Problem">Why it was not all removed, where it was not.</param>
/// <param name="Undated">Whether it stayed, let go by age, because its run said nothing of when it began (ADR-0165).</param>
/// <param name="Younger">Whether it stayed, let go by age, because its run began within the days (ADR-0165).</param>
internal sealed record DirectRestoreLetGo(string Name, long Held, bool UnderWay, string? Problem, bool Undated = false, bool Younger = false);

/// <summary>A destination's folder of parts, as <c>restore-parts</c> lists it (ADR-0142).</summary>
/// <param name="Name">The folder's name.</param>
/// <param name="Run">What its run said of itself, or null where that was not kept.</param>
/// <param name="Held">The bytes its parts hold.</param>
/// <param name="LastWritten">When the folder was last written, for ordering one whose run said nothing.</param>
/// <param name="UnderWay">Whether a run holds the folder now (ADR-0143).</param>
internal sealed record KeptDirectRestoreParts(string Name, DirectRestoreRun? Run, long Held, DateTime LastWritten, bool UnderWay);
