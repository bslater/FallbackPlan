using Bodu;
using FallbackPlan.Domain;
using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Application.Resources;

namespace FallbackPlan.Application;

/// <summary>
/// Reads <see cref="JobState"/> by name, and reads a name it does not know as
/// <see cref="JobState.FailedRecoverable"/> rather than throwing.
/// </summary>
/// <remarks>
/// <para>
/// The journal stores states as strings, which is what makes it diagnosable by
/// eye — and it is also what would make a downgrade destructive. A build that
/// predates a newly added state would throw on the unknown name, and
/// <see cref="JobStateStore.Open"/> answers a <see cref="JsonException"/> by
/// setting the whole file aside as corrupt and starting empty. That loses
/// every set's schedule anchor at once, so every set becomes due at once: a
/// version rollback would trigger a simultaneous backup of everything.
/// </para>
/// <para>
/// <see cref="JobState.FailedRecoverable"/> is the landing place because it is
/// the state the service retries on its next pass. An unrecognised terminal
/// state read as "retry" costs one redundant run; read as "complete" it could
/// silently anchor a schedule against something that never happened.
/// </para>
/// </remarks>
internal sealed class TolerantJobStateConverter : JsonConverter<JobState>
{
    public override JobState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numeric))
        {
            return Enum.IsDefined(typeof(JobState), numeric) ? (JobState)numeric : JobState.FailedRecoverable;
        }

        var name = reader.GetString();
        return Enum.TryParse<JobState>(name, ignoreCase: true, out var state)
            ? state
            : JobState.FailedRecoverable;
    }

    public override void Write(Utf8JsonWriter writer, JobState value, JsonSerializerOptions options)
    {
        ThrowHelper.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}

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
        Converters = { new TolerantJobStateConverter(), new JsonStringEnumConverter() },
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
                throw new ClientStateException(Strings.FormatJobStateStore_NoJobExistsJournal(jobId));
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
    /// <remarks>
    /// <see cref="JobState.CompletedWithFailures"/> counts, and the reason is
    /// worth stating because the opposite reading is a live-lock. That run
    /// committed a snapshot and is genuinely the set's most recent backup;
    /// excluding it would make a set whose backup was partial look as though
    /// it had never run, so it would back up again on every scheduler pass —
    /// for ever, on a set with one permanently unreadable file.
    /// </remarks>
    public JobRecord? LastCompleted(string backupSetId)
    {
        lock (_gate)
        {
            return _jobs.LastOrDefault(job =>
                job.BackupSetId == backupSetId && IsCommitted(job.State));
        }
    }

    /// <summary>Whether a state means "a snapshot was committed by this run".</summary>
    public static bool IsCommitted(JobState state) =>
        state is JobState.Complete or JobState.CompletedWithFailures;

    /// <summary>
    /// The schedule anchor for a set, in the operator's wall-clock frame —
    /// what <see cref="Schedule.IsDue"/> and <see cref="Schedule.NextRun"/>
    /// want, or <see langword="null"/> when the set has never completed a run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists to be the only place this conversion happens. The same five
    /// lines used to appear in the scheduler, the CLI and the command handler,
    /// and they were wrong in all three the same way: they built the anchor
    /// with <see cref="DateTimeOffset.FromUnixTimeMilliseconds(long)"/>, which
    /// carries offset <c>+00:00</c>, and handed it to a comparison whose other
    /// side came from <see cref="DateTimeOffset.Now"/>. Three copies of a
    /// conversion is how the same defect gets fixed twice and missed once.
    /// </para>
    /// <para>
    /// <c>ToLocalTime</c> is what makes the frame right, and it is right per
    /// instant rather than per process: it resolves the offset that was in
    /// effect when the run completed, which is exactly what a daily schedule
    /// needs on the night an offset changes.
    /// </para>
    /// </remarks>
    public DateTimeOffset? ScheduleAnchor(string backupSetId) =>
        LastCompleted(backupSetId) is { } completed
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)completed.UpdatedAt).ToLocalTime()
            : null;

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
