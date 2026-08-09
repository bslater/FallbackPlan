using Bodu;
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
    private readonly List<JobRecord> _jobs;

    // One journal, several writers. The scheduler transitions a job on its own
    // thread while the queue's workers transition theirs, and a connection
    // thread reads the list to answer ListJobs. Without this every one of them
    // raced: serialising the list while another thread mutated it, and — the
    // one that actually bit — two Saves whose writes landed in the opposite
    // order to the mutations they were meant to persist, so a transition that
    // had happened in memory was overwritten on disk by an older snapshot.
    private readonly Lock _gate = new();

    private JobStateStore(string path, List<JobRecord> jobs)
    {
        _path = path;
        _jobs = jobs;
    }

    /// <summary>Every recorded job, oldest first.</summary>
    /// <remarks>
    /// A snapshot, not a view: handing out the live list would let a caller
    /// enumerate it while a job transitions underneath them.
    /// </remarks>
    public IReadOnlyList<JobRecord> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs];
            }
        }
    }

    /// <summary>Opens (or creates) the journal in <paramref name="stateDirectory"/>.</summary>
    public static JobStateStore Open(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
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
        ThrowHelper.ThrowIfNullOrWhiteSpace(backupSetId);

        var job = new JobRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            BackupSetId = backupSetId,
            State = JobState.Pending,
            StartedAt = nowUnixMilliseconds,
            UpdatedAt = nowUnixMilliseconds,
        };

        lock (_gate)
        {
            _jobs.Add(job);
            Save();
        }

        return job;
    }

    /// <summary>Transitions a job and persists — every transition durable and idempotent (10 §3).</summary>
    public JobRecord Transition(
        string jobId, JobState state, ulong nowUnixMilliseconds, string? detail = null, string? snapshotId = null)
    {
        // The lookup, the replacement and the write are one operation. Split
        // them and two transitions interleave: both read, both write, and the
        // journal keeps whichever reached the disk last rather than both.
        lock (_gate)
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
    }

    /// <summary>The last COMPLETED run of a set — the schedule anchor (ADR-0027 §1).</summary>
    public JobRecord? LastCompleted(string backupSetId)
    {
        lock (_gate)
        {
            return _jobs.LastOrDefault(job => job.BackupSetId == backupSetId && job.State == JobState.Complete);
        }
    }

    /// <summary>Jobs the Agent retries on its next pass — recoverable failures only (10 §3).</summary>
    public IReadOnlyList<JobRecord> RecoverableFailures(string backupSetId)
    {
        lock (_gate)
        {
            return [.. _jobs.Where(job => job.BackupSetId == backupSetId && job.State == JobState.FailedRecoverable)];
        }
    }

    /// <summary>Serialises and writes the journal. Call under <c>_gate</c>.</summary>
    private void Save() =>
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_jobs, SerializerOptions));
}
