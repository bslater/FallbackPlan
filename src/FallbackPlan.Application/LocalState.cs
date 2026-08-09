using Bodu;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Application.Resources;

namespace FallbackPlan.Application;

/// <summary>One completed (or failed) backup job, for the operator's history.</summary>
public sealed record JobHistoryEntry
{
    [JsonPropertyName("snapshot_id")]
    public required string SnapshotId { get; init; }

    [JsonPropertyName("backup_set_id")]
    public required string BackupSetId { get; init; }

    [JsonPropertyName("started_at")]
    public required ulong StartedAt { get; init; }

    [JsonPropertyName("capture_status")]
    public required byte CaptureStatus { get; init; }

    [JsonPropertyName("files")]
    public required int Files { get; init; }

    [JsonPropertyName("failures")]
    public required int Failures { get; init; }
}

/// <summary>
/// Durable local state (architecture 11 §3): device identity, writer
/// identity, default backup set, job history. Loss of this file loses the
/// device's identity — it is NOT rebuildable, which is exactly why it lives
/// apart from the disposable catalogue and the user-editable configuration:
/// three files, three lifecycles.
/// </summary>
public sealed class LocalState
{
    private sealed record Model
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("device_id")]
        public required string DeviceId { get; init; }

        [JsonPropertyName("writer_id")]
        public required string WriterId { get; init; }

        [JsonPropertyName("default_backup_set_id")]
        public required string DefaultBackupSetId { get; init; }

        [JsonPropertyName("job_history")]
        public IReadOnlyList<JobHistoryEntry> JobHistory { get; init; } = [];
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _path;
    private Model _model;

    private LocalState(string path, Model model)
    {
        _path = path;
        _model = model;
    }

    /// <summary>This device's stable 16-byte identity (01 §2).</summary>
    public byte[] DeviceId => Convert.FromHexString(_model.DeviceId);

    /// <summary>This client's stable 16-byte writer identity (02 §4).</summary>
    public byte[] WriterId => Convert.FromHexString(_model.WriterId);

    /// <summary>The default backup-set identity when no set is configured.</summary>
    public byte[] DefaultBackupSetId => Convert.FromHexString(_model.DefaultBackupSetId);

    /// <summary>The recorded job history, oldest first.</summary>
    public IReadOnlyList<JobHistoryEntry> JobHistory => _model.JobHistory;

    /// <summary>
    /// Loads or creates the state file. Legacy phase-0 identity files
    /// (<c>writer-id</c>, <c>device-id</c>, <c>backup-set-id</c>) in the
    /// same directory are absorbed so an existing writer keeps its
    /// identity — losing it would orphan the writer's sequence space.
    /// </summary>
    public static LocalState LoadOrCreate(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);

        var path = Path.Combine(stateDirectory, "state.json");
        if (File.Exists(path))
        {
            try
            {
                var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(path), SerializerOptions)
                    ?? throw new ClientStateException(Strings.FormatModel_HoldsNoStateObject(path));
                return new LocalState(path, model);
            }
            catch (JsonException exception)
            {
                throw new ClientStateException(Strings.FormatModel_NotValidStateFile(path, exception.Message),
                    exception);
            }
        }

        var state = new LocalState(path, new Model
        {
            DeviceId = LegacyOrNew(stateDirectory, "device-id"),
            WriterId = LegacyOrNew(stateDirectory, "writer-id"),
            DefaultBackupSetId = LegacyOrNew(stateDirectory, "backup-set-id"),
        });
        state.Save();
        return state;
    }

    /// <summary>Appends one job to the history and persists.</summary>
    public void RecordJob(JobHistoryEntry entry)
    {
        ThrowHelper.ThrowIfNull(entry);
        _model = _model with { JobHistory = [.. _model.JobHistory, entry] };
        Save();
    }

    // Atomic replacement, not truncate-then-write: a crash between the two
    // would leave a zero-length state.json, and losing this file loses the
    // writer identity that the whole journal sequence hangs off.
    private void Save() => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_model, SerializerOptions));

    private static string LegacyOrNew(string stateDirectory, string legacyName)
    {
        var legacy = Path.Combine(stateDirectory, legacyName);
        return File.Exists(legacy)
            ? Convert.FromHexString(File.ReadAllText(legacy).Trim()) is { Length: 16 } bytes
                ? Convert.ToHexString(bytes).ToLowerInvariant()
                : throw new ClientStateException(Strings.FormatModel_LegacyIdentityFileNotBytes(legacy))
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }
}
