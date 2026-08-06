using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Application;

/// <summary>One job's durable record.</summary>
public sealed record JobRecord
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("backup_set_id")]
    public required string BackupSetId { get; init; }

    [JsonPropertyName("state")]
    public required JobState State { get; init; }

    [JsonPropertyName("started_at")]
    public required ulong StartedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public required ulong UpdatedAt { get; init; }

    [JsonPropertyName("snapshot_id")]
    public string? SnapshotId { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

/// <summary>
/// The job-state journal (ADR-0027 §2): <c>jobs.json</c> beside
/// <c>state.json</c> — not rebuildable from the repository, but
/// deliberately <b>sacrificial</b>: resumability belongs to spool
/// checkpoints and the intent journal, never to this file. A corrupt
/// journal is set aside and restarted empty; the cost is history and one
/// coalesced catch-up run, never identity and never correctness — which
/// is exactly why it is a separate file from <c>state.json</c>, whose
/// corruption is refused loudly.
/// </summary>
public sealed class JobStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private List<JobRecord> _jobs;

    private JobStateStore(string path, List<JobRecord> jobs)
    {
        _path = path;
        _jobs = jobs;
    }

    /// <summary>Every recorded job, oldest first.</summary>
    public IReadOnlyList<JobRecord> Jobs => _jobs;

    /// <summary>Opens (or creates) the journal in <paramref name="stateDirectory"/>.</summary>
    public static JobStateStore Open(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, "jobs.json");

        if (!File.Exists(path))
        {
            return new JobStateStore(path, []);
        }

        try
        {
            var jobs = JsonSerializer.Deserialize<List<JobRecord>>(File.ReadAllText(path), SerializerOptions) ?? [];
            return new JobStateStore(path, jobs);
        }
        catch (JsonException)
        {
            // Sacrificial by design (ADR-0027 §2): set the wreck aside for
            // diagnosis and start empty.
            File.Move(path, path + ".corrupt", overwrite: true);
            return new JobStateStore(path, []);
        }
    }

    /// <summary>Begins a job in <see cref="JobState.Pending"/> and persists.</summary>
    public JobRecord Begin(string backupSetId, ulong nowUnixMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupSetId);

        var job = new JobRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            BackupSetId = backupSetId,
            State = JobState.Pending,
            StartedAt = nowUnixMilliseconds,
            UpdatedAt = nowUnixMilliseconds,
        };
        _jobs.Add(job);
        Save();
        return job;
    }

    /// <summary>Transitions a job and persists — every transition durable and idempotent (10 §3).</summary>
    public JobRecord Transition(
        string jobId, JobState state, ulong nowUnixMilliseconds, string? detail = null, string? snapshotId = null)
    {
        var index = _jobs.FindIndex(job => job.Id == jobId);
        if (index < 0)
        {
            throw new ClientStateException($"No job '{jobId}' exists in the journal.");
        }

        var updated = _jobs[index] with
        {
            State = state,
            UpdatedAt = nowUnixMilliseconds,
            Detail = detail ?? _jobs[index].Detail,
            SnapshotId = snapshotId ?? _jobs[index].SnapshotId,
        };
        _jobs[index] = updated;
        Save();
        return updated;
    }

    /// <summary>The last COMPLETED run of a set — the schedule anchor (ADR-0027 §1).</summary>
    public JobRecord? LastCompleted(string backupSetId) =>
        _jobs.LastOrDefault(job => job.BackupSetId == backupSetId && job.State == JobState.Complete);

    /// <summary>Jobs the Agent retries on its next pass — recoverable failures only (10 §3).</summary>
    public IReadOnlyList<JobRecord> RecoverableFailures(string backupSetId) =>
        [.. _jobs.Where(job => job.BackupSetId == backupSetId && job.State == JobState.FailedRecoverable)];

    private void Save() =>
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_jobs, SerializerOptions));
}
