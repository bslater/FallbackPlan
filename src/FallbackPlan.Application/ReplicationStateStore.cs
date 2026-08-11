using Bodu;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FallbackPlan.Application;

/// <summary>One destination's last successful replication of a scope.</summary>
/// <remarks>
/// Keyed by the destination's peer fingerprint and the scope replicated. Slice 1
/// replicates whole repositories (scope <c>all</c>); per-<c>(snapshot,
/// destination)</c> tracking arrives with snapshot scoping — this record is the
/// home that grows into it.
/// </remarks>
public sealed record ReplicationRecord
{
    /// <summary>The destination peer's fingerprint.</summary>
    [JsonPropertyName("destination")]
    public required string Destination { get; init; }

    /// <summary>The scope replicated (03 §4).</summary>
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    /// <summary>How many objects the destination acknowledged committing, last time.</summary>
    [JsonPropertyName("objects")]
    public required long Objects { get; init; }

    /// <summary>When the last replication completed, Unix milliseconds.</summary>
    [JsonPropertyName("replicated_at")]
    public required ulong ReplicatedAt { get; init; }
}

/// <summary>
/// The per-destination replication ledger: <c>replication.json</c> beside
/// <c>jobs.json</c> in the state directory (FR-REP-001 — replication state is
/// tracked independently of commit). Like the job journal it is durable but
/// sacrificial: a corrupt ledger is set aside and started empty, because it is a
/// record of what was replicated, never the objects themselves, which the
/// destination holds regardless.
/// </summary>
public sealed class ReplicationStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly List<ReplicationRecord> _records;
    private readonly Lock _gate = new();

    private ReplicationStateStore(string path, List<ReplicationRecord> records)
    {
        _path = path;
        _records = records;
    }

    /// <summary>Every recorded replication, a snapshot of the list.</summary>
    public IReadOnlyList<ReplicationRecord> Records
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
    public static ReplicationStateStore Open(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, "replication.json");

        if (!File.Exists(path))
        {
            return new ReplicationStateStore(path, []);
        }

        try
        {
            var records = JsonSerializer.Deserialize<List<ReplicationRecord>>(File.ReadAllText(path), SerializerOptions)
                ?? [];
            return new ReplicationStateStore(path, records);
        }
        catch (JsonException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return new ReplicationStateStore(path, []);
        }
    }

    /// <summary>Records a completed replication of a scope to a destination, and persists.</summary>
    public ReplicationRecord Record(string destination, string scope, long objects, ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(destination);
        ThrowHelper.ThrowIfNullOrWhiteSpace(scope);

        var record = new ReplicationRecord
        {
            Destination = destination,
            Scope = scope,
            Objects = objects,
            ReplicatedAt = nowUnixMilliseconds,
        };

        lock (_gate)
        {
            // One row per (destination, scope): the latest replication replaces
            // the previous, because what matters is the current state, not a log.
            _records.RemoveAll(existing =>
                string.Equals(existing.Destination, destination, StringComparison.Ordinal)
                && string.Equals(existing.Scope, scope, StringComparison.Ordinal));
            _records.Add(record);
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_records, SerializerOptions));
            return record;
        }
    }

    /// <summary>The last replication of a scope to a destination, or null.</summary>
    public ReplicationRecord? Find(string destination, string scope)
    {
        lock (_gate)
        {
            return _records.LastOrDefault(record =>
                string.Equals(record.Destination, destination, StringComparison.Ordinal)
                && string.Equals(record.Scope, scope, StringComparison.Ordinal));
        }
    }
}
