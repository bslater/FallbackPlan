using Bodu;
using FallbackPlan.Domain;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FallbackPlan.Application;

/// <summary>
/// Where the sweep of one set's staging archive stands: the circuit of its
/// stored blobs, a stall, and the damage found there and not yet replaced
/// (FR-VER-009, ADR-0095).
/// </summary>
/// <remarks>
/// The circuit and stall fields mean what a destination's do on its ledger
/// row (<see cref="DestinationSyncRecord"/>), so the scheduler and the status
/// report read both alike.
/// </remarks>
public sealed record StagingSweepRecord
{
    /// <summary>The backup set's identifier.</summary>
    [JsonPropertyName("set")]
    public required string SetId { get; init; }

    /// <summary>The key after which the next segment resumes; null when the circuit closed or none has begun.</summary>
    [JsonPropertyName("sweep_cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SweepCursor { get; init; }

    /// <summary>When a segment last read anything, Unix milliseconds; null when none has.</summary>
    [JsonPropertyName("swept_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? SweptAt { get; init; }

    /// <summary>When a circuit last closed — every stored blob read — Unix milliseconds; null when none has.</summary>
    [JsonPropertyName("sweep_completed_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? SweepCompletedAt { get; init; }

    /// <summary>Blobs read since the circuit under way began.</summary>
    [JsonPropertyName("swept_this_circuit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SweptThisCircuit { get; init; }

    /// <summary>
    /// The store keys found damaged in the staging archive and not yet
    /// replaced from a sound copy; null when none are outstanding.
    /// </summary>
    /// <remarks>
    /// Written by whatever finds them — the staging sweep, a destination's
    /// sweep whose length comparison it was, a restore that read around
    /// them — and cleared only by the staging sweep, when it reads a key
    /// sound, repairs it, or finds it no longer held.
    /// </remarks>
    [JsonPropertyName("damaged_keys")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DamagedKeys { get; init; }

    /// <summary>
    /// When the outstanding damage was last tried, Unix milliseconds; null
    /// when some of it has not been tried yet, which makes all of it due.
    /// </summary>
    [JsonPropertyName("damage_checked_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? DamageCheckedAt { get; init; }

    /// <summary>
    /// Tries in a row that left damage outstanding; what the next try waits
    /// on, as a sync's consecutive failures are, because a copy that could
    /// not be found an hour ago is rarely there a minute later.
    /// </summary>
    [JsonPropertyName("damage_checks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DamageChecks { get; init; }

    /// <summary>
    /// The keys repairs began replacing and have not returned from, in key
    /// order; null when none is under way.
    /// </summary>
    /// <remarks>
    /// A repair deletes the damaged copy before it puts the sound one,
    /// because nothing at a store is ever overwritten, and a service stopped
    /// between the two, or a put the disk refused, leaves the key empty while
    /// a backup may already reference it. Found empty, these keys are
    /// refilled. Any other key the staging archive no longer holds was let go
    /// by the trim or the collector, and is not owed. Every such key is kept,
    /// not only the last: a try carries on past a repair the staging archive
    /// refused, and the repairs after it begin and return in turn.
    /// </remarks>
    [JsonPropertyName("repairing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RepairingKeys { get; init; }

    /// <summary>Segments in a row that stopped at a blob that would not read; zero once one gets through.</summary>
    [JsonPropertyName("sweep_stalls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SweepStalls { get; init; }

    /// <summary>When the last stalled segment stopped, Unix milliseconds; null when none is stalled.</summary>
    [JsonPropertyName("sweep_stalled_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? SweepStalledAt { get; init; }

    /// <summary>The blob the last stalled segment could not read; null when none is stalled.</summary>
    [JsonPropertyName("sweep_stalled_on")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SweepStalledOn { get; init; }
}

/// <summary>The on-disk shape of <c>staging-sweeps.json</c>.</summary>
internal sealed record StagingSweepFile
{
    /// <summary>The shape this file was written under.</summary>
    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    /// <summary>One record per set.</summary>
    [JsonPropertyName("sets")]
    public required List<StagingSweepRecord>? Sets { get; init; }
}

/// <summary>
/// Each staging set's sweep of its own staging archive:
/// <c>staging-sweeps.json</c> beside the destination ledger (FR-VER-009,
/// ADR-0095). Sacrificial, like everything beside it: losing it starts each
/// circuit again, and damage it forgot is found again by the next circuit,
/// which reads every blob.
/// </summary>
/// <remarks>
/// Its own file rather than a row on the destination ledger, because the
/// staging archive is not a destination: nothing syncs to it, and every
/// reader of the ledger takes each row for a place the set is copied to.
/// </remarks>
public sealed class StagingSweepStore
{
    /// <summary>The shape this build writes.</summary>
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly List<StagingSweepRecord> _records;
    private readonly Lock _gate = new();

    private StagingSweepStore(string path, List<StagingSweepRecord> records)
    {
        _path = path;
        _records = records;
    }

    /// <summary>Every set's record, a snapshot of the list.</summary>
    public IReadOnlyList<StagingSweepRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return [.. _records];
            }
        }
    }

    /// <summary>Opens (or creates) the store in <paramref name="stateDirectory"/>.</summary>
    /// <param name="stateDirectory">The directory holding <c>staging-sweeps.json</c>.</param>
    public static StagingSweepStore Open(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, "staging-sweeps.json");

        if (!File.Exists(path))
        {
            return new StagingSweepStore(path, []);
        }

        // Read sharing deletion, as the ledger is: `status` may read while
        // the service replaces the file.
        try
        {
            var file = JsonSerializer.Deserialize<StagingSweepFile>(AtomicFile.ReadAllText(path), SerializerOptions);
            if (file is not null && file.SchemaVersion <= CurrentSchemaVersion)
            {
                return new StagingSweepStore(path, file.Sets ?? []);
            }
        }
        catch (JsonException)
        {
            // Unreadable: set aside below, as a newer build's file is.
        }

        return Quarantine(path);
    }

    /// <summary>The set's record, or null when its staging archive has never been swept or found damaged.</summary>
    /// <param name="setId">The backup set.</param>
    public StagingSweepRecord? Find(string setId)
    {
        lock (_gate)
        {
            return FindLocked(setId);
        }
    }

    /// <summary>Records a segment that read what it set out to, and closes the circuit when it reached the end.</summary>
    /// <param name="setId">The backup set.</param>
    /// <param name="cursor">Where the next segment resumes; null when the circuit closed.</param>
    /// <param name="examined">Blobs this segment read.</param>
    /// <param name="completedCircuit">Whether this segment reached the end of the key space.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    public StagingSweepRecord RecordSweep(
        string setId, string? cursor, int examined, bool completedCircuit, ulong nowUnixMilliseconds) =>
        Mutate(setId, previous => previous with
        {
            SweepCursor = cursor,
            SweptAt = nowUnixMilliseconds,
            SweptThisCircuit = completedCircuit ? 0 : previous.SweptThisCircuit + examined,
            SweepCompletedAt = completedCircuit ? nowUnixMilliseconds : previous.SweepCompletedAt,
            SweepStalls = 0,
            SweepStalledAt = null,
            SweepStalledOn = null,
        });

    /// <summary>
    /// Records a segment that stopped at a blob it could not read, keeping
    /// what it read before it, and counts the stall.
    /// </summary>
    /// <param name="setId">The backup set.</param>
    /// <param name="cursor">The last blob read before the stall; ignored when <paramref name="examined"/> is zero.</param>
    /// <param name="examined">Blobs read before the stall.</param>
    /// <param name="stalledOn">The blob that would not read.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    /// <returns>The record as written, carrying the stall count the caller decides on.</returns>
    public StagingSweepRecord RecordSweepStall(
        string setId, string? cursor, int examined, string? stalledOn, ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfLessThan(examined, 0);

        return Mutate(setId, previous => (examined > 0
            ? previous with
            {
                SweepCursor = cursor,
                SweptAt = nowUnixMilliseconds,
                SweptThisCircuit = previous.SweptThisCircuit + examined,
            }
            : previous) with
        {
            // Read past where the last one stopped: a new stall, not the old
            // one again.
            SweepStalls = examined > 0 ? 1 : previous.SweepStalls + 1,
            SweepStalledAt = nowUnixMilliseconds,
            SweepStalledOn = stalledOn,
        });
    }

    /// <summary>
    /// Records damage found in the staging archive and damage no longer
    /// outstanding, as a set union and difference, so each finder leaves the
    /// others' keys standing.
    /// </summary>
    /// <remarks>
    /// A key not already outstanding makes all of it due at once, because
    /// nothing has tried to repair it yet; one already outstanding keeps the
    /// back-off its last try earned. A key resolved is no longer owed back
    /// either, whatever repair of it began and did not return.
    /// </remarks>
    /// <param name="setId">The backup set.</param>
    /// <param name="unrepaired">Keys found damaged and not replaced.</param>
    /// <param name="resolved">Keys read sound, repaired, or no longer held.</param>
    public StagingSweepRecord RecordDamage(
        string setId, IReadOnlyCollection<string> unrepaired, IReadOnlyCollection<string> resolved)
    {
        ThrowHelper.ThrowIfNull(unrepaired);
        ThrowHelper.ThrowIfNull(resolved);

        return Mutate(setId, previous =>
        {
            var before = new SortedSet<string>(previous.DamagedKeys ?? [], StringComparer.Ordinal);
            var keys = new SortedSet<string>(before, StringComparer.Ordinal);
            keys.UnionWith(unrepaired);
            keys.ExceptWith(resolved);
            var repairing = new SortedSet<string>(previous.RepairingKeys ?? [], StringComparer.Ordinal);
            repairing.ExceptWith(resolved);
            if (keys.Count == 0)
            {
                return previous with
                {
                    DamagedKeys = null,
                    DamageCheckedAt = null,
                    DamageChecks = 0,
                    RepairingKeys = repairing.Count == 0 ? null : [.. repairing],
                };
            }

            var untried = unrepaired.Any(key => !before.Contains(key));
            return previous with
            {
                DamagedKeys = [.. keys],
                DamageCheckedAt = untried ? null : previous.DamageCheckedAt,
                DamageChecks = untried ? 0 : previous.DamageChecks,
                RepairingKeys = repairing.Count == 0 ? null : [.. repairing],
            };
        });
    }

    /// <summary>
    /// Records a try at the outstanding damage that left some of it standing:
    /// stamped, and counted toward how long the next try waits. Nothing is
    /// stamped when nothing is outstanding.
    /// </summary>
    /// <param name="setId">The backup set.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    public StagingSweepRecord RecordDamageChecked(string setId, ulong nowUnixMilliseconds) =>
        Mutate(setId, previous => previous.DamagedKeys is not { Count: > 0 }
            ? previous
            : previous with { DamageCheckedAt = nowUnixMilliseconds, DamageChecks = previous.DamageChecks + 1 });

    /// <summary>
    /// Records a key a repair is about to replace, before anything at the
    /// staging archive is touched.
    /// </summary>
    /// <param name="setId">The backup set.</param>
    /// <param name="key">The key under repair.</param>
    public StagingSweepRecord RecordRepairing(string setId, string key)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(key);

        return Mutate(setId, previous => previous with
        {
            RepairingKeys = [.. new SortedSet<string>(previous.RepairingKeys ?? [], StringComparer.Ordinal) { key }],
        });
    }

    /// <summary>
    /// Records that the repair of a key returned, whatever became of it: the
    /// key is held again, by the copy put there or by the damage nothing
    /// proven replaced.
    /// </summary>
    /// <param name="setId">The backup set.</param>
    /// <param name="key">The key whose repair returned.</param>
    public StagingSweepRecord RecordRepairReturned(string setId, string key)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(key);

        return Mutate(setId, previous =>
        {
            var repairing = new SortedSet<string>(previous.RepairingKeys ?? [], StringComparer.Ordinal);
            repairing.Remove(key);
            return previous with { RepairingKeys = repairing.Count == 0 ? null : [.. repairing] };
        });
    }

    private static StagingSweepStore Quarantine(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // As the ledger's: an empty record is the same recovery either
            // way, and a service that will not start is worse than either.
        }

        return new StagingSweepStore(path, []);
    }

    /// <summary>Reads the set's record, applies <paramref name="transform"/>, and writes the result, all under one lock.</summary>
    private StagingSweepRecord Mutate(string setId, Func<StagingSweepRecord, StagingSweepRecord> transform)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(setId);

        lock (_gate)
        {
            var record = transform(FindLocked(setId) ?? new StagingSweepRecord { SetId = setId });
            _records.RemoveAll(existing => string.Equals(existing.SetId, setId, StringComparison.Ordinal));
            _records.Add(record);
            AtomicFile.WriteAllText(
                _path,
                JsonSerializer.Serialize(
                    new StagingSweepFile { SchemaVersion = CurrentSchemaVersion, Sets = _records },
                    SerializerOptions));
            return record;
        }
    }

    private StagingSweepRecord? FindLocked(string setId) =>
        _records.LastOrDefault(record => string.Equals(record.SetId, setId, StringComparison.Ordinal));
}
