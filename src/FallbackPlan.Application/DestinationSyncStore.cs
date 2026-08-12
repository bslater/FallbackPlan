using Bodu;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FallbackPlan.Application;

/// <summary>Where one destination stands for one backup set.</summary>
public enum DestinationSyncState
{
    /// <summary>Held everything the staging archive held, as of the last attempt.</summary>
    InSync = 0,

    /// <summary>The staging archive has moved on since the last success.</summary>
    Behind = 1,

    /// <summary>The destination could not be reached — a gap that closes itself when it returns (FR-DEST-003).</summary>
    Unavailable = 2,

    /// <summary>The destination was reached and the attempt failed anyway.</summary>
    Failed = 3,

    /// <summary>The destination's kind is accepted by configuration and not yet served — a stated incapacity, never a failure (FR-DEST-005).</summary>
    NotSupported = 4,
}

/// <summary>One <c>(set, destination)</c> pair's sync state (FR-DEST-004).</summary>
public sealed record DestinationSyncRecord
{
    /// <summary>The backup set's 32-hex identity.</summary>
    [JsonPropertyName("set")]
    public required string SetId { get; init; }

    /// <summary>The destination's declared name.</summary>
    [JsonPropertyName("destination")]
    public required string Destination { get; init; }

    /// <summary>Where the pair stands.</summary>
    [JsonPropertyName("state")]
    public required DestinationSyncState State { get; init; }

    /// <summary>When a sync last ran, Unix milliseconds.</summary>
    [JsonPropertyName("last_attempt_at")]
    public required ulong LastAttemptAt { get; init; }

    /// <summary>When a sync last succeeded, Unix milliseconds; null when never.</summary>
    [JsonPropertyName("last_success_at")]
    public ulong? LastSuccessAt { get; init; }

    /// <summary>Objects copied by the last successful sync.</summary>
    [JsonPropertyName("objects")]
    public long Objects { get; init; }

    /// <summary>Failed attempts since the last success — the back-off input.</summary>
    [JsonPropertyName("consecutive_failures")]
    public int ConsecutiveFailures { get; init; }

    /// <summary>What the last failure said, for `status` to repeat verbatim.</summary>
    [JsonPropertyName("last_error")]
    public string? LastError { get; init; }

    /// <summary>
    /// The staging archive's highest publication sequence when the last
    /// successful sync <b>began</b> — everything published at or before it
    /// is at the destination. The replication gate compares snapshot
    /// publication sequences to this, never a clock (FR-GC-009, ADR-0009
    /// Amendment 4).
    /// </summary>
    [JsonPropertyName("synced_sequence")]
    public ulong SyncedSequence { get; init; }
}

/// <summary>
/// The per-<c>(set, destination)</c> sync ledger: <c>destinations.json</c>
/// beside <c>jobs.json</c> (FR-DEST-004, ADR-0010 Amendment 1). Durable but
/// sacrificial, like everything beside it: losing this file loses when each
/// destination was last reached, and the next convergence pass re-derives
/// what each destination holds from the destination's own inventory — the
/// copy diff is idempotent, so an empty ledger is a slow first pass, never a
/// wrong one.
/// </summary>
public sealed class DestinationSyncStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly List<DestinationSyncRecord> _records;
    private readonly Lock _gate = new();

    private DestinationSyncStore(string path, List<DestinationSyncRecord> records)
    {
        _path = path;
        _records = records;
    }

    /// <summary>Every pair's state, a snapshot of the list.</summary>
    public IReadOnlyList<DestinationSyncRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return [.. _records];
            }
        }
    }

    /// <summary>Opens (or creates) the ledger in <paramref name="stateDirectory"/>.</summary>
    public static DestinationSyncStore Open(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, "destinations.json");

        if (!File.Exists(path))
        {
            return new DestinationSyncStore(path, []);
        }

        try
        {
            var records = JsonSerializer.Deserialize<List<DestinationSyncRecord>>(
                File.ReadAllText(path), SerializerOptions) ?? [];
            return new DestinationSyncStore(path, records);
        }
        catch (JsonException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return new DestinationSyncStore(path, []);
        }
    }

    /// <summary>The pair's state, or null when it has never been attempted.</summary>
    public DestinationSyncRecord? Find(string setId, string destination)
    {
        lock (_gate)
        {
            return _records.LastOrDefault(record =>
                string.Equals(record.SetId, setId, StringComparison.Ordinal)
                && string.Equals(record.Destination, destination, StringComparison.Ordinal));
        }
    }

    /// <summary>Records a successful sync: the pair is in sync as of now.</summary>
    /// <param name="setId">The backup set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="objects">Objects copied by this sync.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    /// <param name="syncedSequence">
    /// The staging archive's highest publication sequence when the sync began
    /// — the replication gate's input (FR-GC-009). A snapshot published after
    /// the sync started may or may not have crossed, so the claim stops here.
    /// </param>
    public DestinationSyncRecord RecordSuccess(
        string setId, string destination, long objects, ulong nowUnixMilliseconds, ulong syncedSequence = 0)
    {
        var previous = Find(setId, destination);
        return Upsert(new DestinationSyncRecord
        {
            SetId = setId,
            Destination = destination,
            State = DestinationSyncState.InSync,
            LastAttemptAt = nowUnixMilliseconds,
            LastSuccessAt = nowUnixMilliseconds,
            Objects = objects,
            ConsecutiveFailures = 0,
            // A later sync never un-holds what an earlier one delivered.
            SyncedSequence = Math.Max(syncedSequence, previous?.SyncedSequence ?? 0),
        });
    }

    /// <summary>Records a failed or refused attempt, keeping the last success and counting toward back-off.</summary>
    public DestinationSyncRecord RecordFailure(
        string setId, string destination, DestinationSyncState state, string error, ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(error);

        var previous = Find(setId, destination);
        return Upsert(new DestinationSyncRecord
        {
            SetId = setId,
            Destination = destination,
            State = state,
            LastAttemptAt = nowUnixMilliseconds,
            LastSuccessAt = previous?.LastSuccessAt,
            Objects = previous?.Objects ?? 0,
            ConsecutiveFailures = (previous?.ConsecutiveFailures ?? 0) + 1,
            LastError = error,
            SyncedSequence = previous?.SyncedSequence ?? 0,
        });
    }

    private DestinationSyncRecord Upsert(DestinationSyncRecord record)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(record.SetId);
        ThrowHelper.ThrowIfNullOrWhiteSpace(record.Destination);

        lock (_gate)
        {
            // One row per (set, destination): current state, not a log.
            _records.RemoveAll(existing =>
                string.Equals(existing.SetId, record.SetId, StringComparison.Ordinal)
                && string.Equals(existing.Destination, record.Destination, StringComparison.Ordinal));
            _records.Add(record);
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_records, SerializerOptions));
            return record;
        }
    }
}
