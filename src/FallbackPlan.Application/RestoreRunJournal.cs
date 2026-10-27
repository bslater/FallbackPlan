using Bodu;
using FallbackPlan.Domain;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FallbackPlan.Application;

/// <summary>
/// One restore run the service began and has not seen finish (FR-RST-008,
/// ADR-0115): what it was asked, so a resume can ask it again by its name
/// alone.
/// </summary>
/// <remarks>
/// It holds the paths the run was asked for, which are the backup's file
/// names, as the receipts beside it do. Nothing that lists runs gives them
/// out (ADR-0089).
/// </remarks>
public sealed record RestoreRunRecord
{
    /// <summary>The run's name: the sixteen lowercase hex digits its result gives (ADR-0110).</summary>
    [JsonPropertyName("run")]
    public required string Run { get; init; }

    /// <summary>The name of the set whose snapshot it restores, where the service knew it.</summary>
    [JsonPropertyName("set")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SetName { get; init; }

    /// <summary>The snapshot, hex-encoded.</summary>
    [JsonPropertyName("snapshot")]
    public required string SnapshotId { get; init; }

    /// <summary>The one subtree it was asked for; null for the whole snapshot or for several.</summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    /// <summary>The subtrees it was asked for, where it was asked several; null otherwise.</summary>
    [JsonPropertyName("paths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Paths { get; init; }

    /// <summary>Where it was asked to write, on the service's machine; null or empty where it lands where the files came from.</summary>
    [JsonPropertyName("output")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputDirectory { get; init; }

    /// <summary>Its target, as the contract takes it; null where none was asked.</summary>
    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; init; }

    /// <summary>Its existing-file policy, as the contract takes it; null for the default.</summary>
    [JsonPropertyName("existing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Existing { get; init; }

    /// <summary>Whether it lands in place rather than under quarantine.</summary>
    [JsonPropertyName("in_place")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool InPlace { get; init; }

    /// <summary>When the run under this name last began, Unix milliseconds.</summary>
    [JsonPropertyName("began_at")]
    public required ulong BeganAt { get; init; }
}

/// <summary>The on-disk shape of <c>restore-runs.json</c>.</summary>
internal sealed record RestoreRunFile
{
    /// <summary>The shape this file was written under.</summary>
    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    /// <summary>One record per run, oldest first.</summary>
    [JsonPropertyName("runs")]
    public required List<RestoreRunRecord>? Runs { get; init; }
}

/// <summary>
/// The restore runs the service began and has not seen finish:
/// <c>restore-runs.json</c> beside the job journal (FR-RST-008, ADR-0115).
/// A run is recorded before it writes anything and dropped once a run of it
/// finishes, so one cut short by the machine stopping is on record when the
/// service starts again, though nobody heard it end.
/// </summary>
/// <remarks>
/// Sacrificial, like everything beside it: a run it forgot is still resumed
/// by its folder's name (ADR-0109). Which runs are under way is the running
/// process's own knowledge and is never written: every run on record when
/// the journal opens was left by a process that has gone.
/// </remarks>
public sealed class RestoreRunJournal
{
    /// <summary>The shape this build writes.</summary>
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly List<RestoreRunRecord> _runs;
    private readonly HashSet<string> _live = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private RestoreRunJournal(string path, List<RestoreRunRecord> runs)
    {
        _path = path;
        _runs = runs;
    }

    /// <summary>Every run on record, under way or not, oldest first.</summary>
    public IReadOnlyList<RestoreRunRecord> Runs
    {
        get
        {
            lock (_gate)
            {
                return [.. _runs];
            }
        }
    }

    /// <summary>The runs on record that are not under way: each ended without finishing, oldest first.</summary>
    public IReadOnlyList<RestoreRunRecord> Unfinished
    {
        get
        {
            lock (_gate)
            {
                return [.. _runs.Where(run => !_live.Contains(run.Run))];
            }
        }
    }

    /// <summary>Opens (or creates) the journal in <paramref name="stateDirectory"/>.</summary>
    /// <param name="stateDirectory">The directory holding <c>restore-runs.json</c>.</param>
    public static RestoreRunJournal Open(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var path = System.IO.Path.Combine(stateDirectory, "restore-runs.json");

        if (!File.Exists(path))
        {
            return new RestoreRunJournal(path, []);
        }

        try
        {
            var file = JsonSerializer.Deserialize<RestoreRunFile>(AtomicFile.ReadAllText(path), SerializerOptions);
            if (file is not null && file.SchemaVersion <= CurrentSchemaVersion)
            {
                return new RestoreRunJournal(path, file.Runs ?? []);
            }
        }
        catch (JsonException)
        {
            // Unreadable: set aside below, as a newer build's file is.
        }

        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An empty journal is the same recovery either way, and a service
            // that will not start is worse than either.
        }

        return new RestoreRunJournal(path, []);
    }

    /// <summary>The run on record under <paramref name="run"/>, or null when none is.</summary>
    /// <param name="run">The run's name.</param>
    public RestoreRunRecord? Find(string run)
    {
        lock (_gate)
        {
            return FindLocked(run);
        }
    }

    /// <summary>Whether the run is under way in this process.</summary>
    /// <param name="run">The run's name.</param>
    public bool IsLive(string run)
    {
        lock (_gate)
        {
            return _live.Contains(run);
        }
    }

    /// <summary>
    /// Records a run about to write, under way, replacing any run on record
    /// under its name.
    /// </summary>
    /// <param name="record">The run, as it was asked.</param>
    /// <param name="continues">
    /// The run it continues under a name of its own, as a resumed run in
    /// place does (ADR-0110). That run is dropped: what it left is this
    /// one's to finish. Null when it continues none, or continues its own.
    /// </param>
    public void Begin(RestoreRunRecord record, string? continues = null)
    {
        ThrowHelper.ThrowIfNull(record);
        ThrowHelper.ThrowIfNullOrWhiteSpace(record.Run);

        lock (_gate)
        {
            _runs.RemoveAll(existing =>
                string.Equals(existing.Run, record.Run, StringComparison.Ordinal)
                || string.Equals(existing.Run, continues, StringComparison.Ordinal));
            _runs.Add(record);
            _live.Add(record.Run);
            WriteLocked();
        }
    }

    /// <summary>Drops a run a run of it finished.</summary>
    /// <param name="run">The run's name.</param>
    public void Finish(string run)
    {
        lock (_gate)
        {
            _live.Remove(run);
            if (_runs.RemoveAll(existing => string.Equals(existing.Run, run, StringComparison.Ordinal)) > 0)
            {
                WriteLocked();
            }
        }
    }

    /// <summary>Records that a run under way ended without finishing; it stays on record.</summary>
    /// <param name="run">The run's name.</param>
    public void Leave(string run)
    {
        lock (_gate)
        {
            _live.Remove(run);
        }
    }

    /// <summary>Drops a run a person let go, unless it is under way.</summary>
    /// <param name="run">The run's name.</param>
    /// <returns>Whether a run was dropped: false for one under way or not on record.</returns>
    public bool Forget(string run)
    {
        lock (_gate)
        {
            if (_live.Contains(run)
                || _runs.RemoveAll(existing => string.Equals(existing.Run, run, StringComparison.Ordinal)) == 0)
            {
                return false;
            }

            WriteLocked();
            return true;
        }
    }

    private RestoreRunRecord? FindLocked(string run) =>
        _runs.LastOrDefault(existing => string.Equals(existing.Run, run, StringComparison.Ordinal));

    private void WriteLocked() =>
        AtomicFile.WriteAllText(
            _path,
            JsonSerializer.Serialize(new RestoreRunFile { SchemaVersion = CurrentSchemaVersion, Runs = _runs }, SerializerOptions));
}
