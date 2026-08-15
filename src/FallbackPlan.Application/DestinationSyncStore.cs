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

    /// <summary>
    /// When a verification pass last proved sampled bytes at this destination,
    /// Unix milliseconds; null when never verified. Status reports coverage
    /// and age, never a bare boolean (FR-VER-003).
    /// </summary>
    [JsonPropertyName("verified_at")]
    public ulong? VerifiedAt { get; init; }

    /// <summary>
    /// The highest publication sequence a passed verification covered — the
    /// pass sampled a listing that included the snapshot carrying this
    /// sequence. Monotonic, like <see cref="SyncedSequence"/>.
    /// </summary>
    [JsonPropertyName("verified_sequence")]
    public ulong VerifiedSequence { get; init; }

    /// <summary>Ranges the last passed verification proved.</summary>
    [JsonPropertyName("verified_objects")]
    public int VerifiedObjects { get; init; }

    /// <summary>
    /// How many objects were eligible when that sample was drawn — the
    /// denominator that turns <see cref="VerifiedObjects"/> into the coverage
    /// fraction status reports (FR-VER-003).
    /// </summary>
    [JsonPropertyName("verified_population")]
    public int VerifiedPopulation { get; init; }

    /// <summary>
    /// Where the deep sweep resumes — the last blob key it read, or null to
    /// start from the beginning of the key space.
    /// </summary>
    /// <remarks>
    /// A key, deliberately, never an index: a blob trimmed between segments
    /// would shift every index after it, silently skipping some objects and
    /// re-reading others with nothing to say so. A key is only ever compared
    /// against, so a cursor naming a since-deleted blob still resumes exactly
    /// where it should.
    /// </remarks>
    [JsonPropertyName("sweep_cursor")]
    public string? SweepCursor { get; init; }

    /// <summary>When the deep sweep last read anything, Unix milliseconds; null when never.</summary>
    [JsonPropertyName("swept_at")]
    public ulong? SweptAt { get; init; }

    /// <summary>
    /// When the sweep last finished a full circuit of the replica's blobs —
    /// the only fact that supports "every stored object was confirmed as of
    /// this moment". A count of segments cannot say it, and neither can
    /// <see cref="SweptAt"/>, which moves on every partial segment.
    /// </summary>
    [JsonPropertyName("sweep_completed_at")]
    public ulong? SweepCompletedAt { get; init; }

    /// <summary>Blobs the sweep has read since the current circuit began.</summary>
    [JsonPropertyName("swept_this_circuit")]
    public int SweptThisCircuit { get; init; }
}

/// <summary>
/// The on-disk shape of <c>destinations.json</c>: a version and the rows.
/// </summary>
/// <remarks>
/// The version exists so a downgrade can recognise a file it does not fully
/// understand and set it aside instead of silently reading it as defaults. That
/// matters more as the row grows: losing <c>synced_sequence</c> fails safe (the
/// trim gate simply refuses), but losing a field that records how far a
/// long-running sweep has got would fail <i>silently</i> — the sweep would
/// restart from the beginning and nothing would say so.
/// </remarks>
internal sealed record LedgerFile
{
    /// <summary>The shape this file was written under.</summary>
    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    /// <summary>One row per <c>(set, destination)</c>.</summary>
    [JsonPropertyName("destinations")]
    public required List<DestinationSyncRecord>? Destinations { get; init; }
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
    /// <summary>The shape this build writes.</summary>
    private const int CurrentSchemaVersion = 1;

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

        var text = File.ReadAllText(path);
        try
        {
            var file = JsonSerializer.Deserialize<LedgerFile>(text, SerializerOptions);
            if (file is not null && file.SchemaVersion <= CurrentSchemaVersion)
            {
                return new DestinationSyncStore(path, file.Destinations ?? []);
            }

            // A file from a newer build. Setting it aside preserves its bytes
            // rather than overwriting them on the next write, which is the
            // safest outcome available to a downgrade.
            return Quarantine(path);
        }
        catch (JsonException)
        {
            // Pre-versioning shape: a bare array. Migrate rather than
            // quarantine — the rows are perfectly readable, and discarding
            // them would silently restart every destination's history.
            try
            {
                var legacy = JsonSerializer.Deserialize<List<DestinationSyncRecord>>(text, SerializerOptions) ?? [];
                return new DestinationSyncStore(path, legacy);
            }
            catch (JsonException)
            {
                return Quarantine(path);
            }
        }
    }

    private static DestinationSyncStore Quarantine(string path)
    {
        File.Move(path, path + ".corrupt", overwrite: true);
        return new DestinationSyncStore(path, []);
    }

    /// <summary>The pair's state, or null when it has never been attempted.</summary>
    /// <remarks>
    /// A caller that intends to write back what it reads must use
    /// <see cref="Mutate"/> instead: this releases the lock before returning,
    /// so anything built on the answer is already racing.
    /// </remarks>
    public DestinationSyncRecord? Find(string setId, string destination)
    {
        lock (_gate)
        {
            return FindLocked(setId, destination);
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
        // Everything not named here is carried forward by `with` — including
        // the verification stamps, which outlive the sync that earned them:
        // they say when bytes were last proven, which a newer copy does not
        // undo.
        return Mutate(setId, destination, previous => Seed(previous, setId, destination, nowUnixMilliseconds) with
        {
            State = DestinationSyncState.InSync,
            LastAttemptAt = nowUnixMilliseconds,
            LastSuccessAt = nowUnixMilliseconds,
            Objects = objects,
            ConsecutiveFailures = 0,
            // Success clears the last failure's message. Stated, because `with`
            // would otherwise carry it forward and `status` would repeat a
            // resolved error verbatim forever.
            LastError = null,
            // A later sync never un-holds what an earlier one delivered.
            SyncedSequence = Math.Max(syncedSequence, previous?.SyncedSequence ?? 0),
        });
    }

    /// <summary>
    /// Records a passed verification: sampled bytes were proven present at
    /// the destination just now (FR-VER-005's happy half). The failure half
    /// goes through <see cref="RecordFailure"/> — a failed proof is a sync
    /// failure, and the stamps here keep saying when bytes were LAST proven.
    /// </summary>
    /// <param name="setId">The backup set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="objects">Ranges the pass proved.</param>
    /// <param name="population">Objects eligible when the sample was drawn — the coverage denominator.</param>
    /// <param name="verifiedSequence">The highest publication sequence the pass's sample covered.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    public DestinationSyncRecord RecordVerification(
        string setId, string destination, int objects, int population, ulong verifiedSequence,
        ulong nowUnixMilliseconds)
    {
        // A verification touches only the stamps: the sync half of the row —
        // state, attempt, success, the synced sequence — is carried forward
        // untouched, because proving bytes says nothing about when they
        // arrived.
        return Mutate(setId, destination, previous => Seed(previous, setId, destination, nowUnixMilliseconds) with
        {
            VerifiedAt = nowUnixMilliseconds,
            VerifiedSequence = Math.Max(verifiedSequence, previous?.VerifiedSequence ?? 0),
            VerifiedObjects = objects,
            VerifiedPopulation = population,
        });
    }

    /// <summary>
    /// Records one segment of a deep sweep: how far it got, and whether that
    /// closed a full circuit of the replica's stored objects.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="RecordVerification"/>. A sweep
    /// segment that read forty blobs and found nothing wrong is not "forty of
    /// forty verified" — writing it into the coverage fields would print 100%
    /// while the sweep was a thousandth of the way round. The two answer
    /// different questions: the challenge stamps say <i>how much of what we
    /// last sent has this destination proven</i>, and these say <i>when was
    /// every stored object last re-read</i>. Only <see cref="DestinationSyncRecord.SweepCompletedAt"/>
    /// can honestly support the second.
    /// </remarks>
    /// <param name="setId">The backup set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="cursor">Where the next segment resumes; null when the circuit closed.</param>
    /// <param name="examined">Blobs this segment read.</param>
    /// <param name="completedCircuit">Whether this segment reached the end of the key space.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    public DestinationSyncRecord RecordSweep(
        string setId, string destination, string? cursor, int examined, bool completedCircuit,
        ulong nowUnixMilliseconds)
    {
        return Mutate(setId, destination, previous => Seed(previous, setId, destination, nowUnixMilliseconds) with
        {
            SweepCursor = cursor,
            SweptAt = nowUnixMilliseconds,
            // The tally resets with the circuit, so it always answers "how far
            // through the CURRENT pass", never an ever-growing lifetime count
            // that would imply coverage it does not have.
            SweptThisCircuit = completedCircuit ? 0 : (previous?.SweptThisCircuit ?? 0) + examined,
            SweepCompletedAt = completedCircuit ? nowUnixMilliseconds : previous?.SweepCompletedAt,
        });
    }

    /// <summary>Records a failed or refused attempt, keeping the last success and counting toward back-off.</summary>
    public DestinationSyncRecord RecordFailure(
        string setId, string destination, DestinationSyncState state, string error, ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(error);

        // The last success and every verification stamp survive a failure:
        // they record what WAS true, and a failed attempt does not un-prove
        // bytes that were proven. Only the failure counters move.
        return Mutate(setId, destination, previous => Seed(previous, setId, destination, nowUnixMilliseconds) with
        {
            State = state,
            LastAttemptAt = nowUnixMilliseconds,
            ConsecutiveFailures = (previous?.ConsecutiveFailures ?? 0) + 1,
            LastError = error,
        });
    }

    /// <summary>
    /// The row a transform starts from: the existing one, or a blank for a pair
    /// never attempted. Every transform then names only the fields it changes
    /// and `with` carries the rest — which is the point. The three mutators
    /// used to re-copy all thirteen fields by hand, so a field added here had
    /// three carry-forward sites and silently reset to its default if any one
    /// was missed.
    /// </summary>
    private static DestinationSyncRecord Seed(
        DestinationSyncRecord? previous, string setId, string destination, ulong nowUnixMilliseconds) =>
        previous ?? new DestinationSyncRecord
        {
            SetId = setId,
            Destination = destination,
            State = DestinationSyncState.InSync,
            LastAttemptAt = nowUnixMilliseconds,
        };

    /// <summary>
    /// Reads the pair's current row, applies <paramref name="transform"/>, and
    /// writes the result — all under one lock.
    /// </summary>
    /// <remarks>
    /// The read must be inside the lock, not before it. The mutators used to
    /// call <see cref="Find"/> first and take the lock only to write, which is
    /// a read-modify-write race: two writers to one row each carry forward the
    /// fields they read, and the loser's changes vanish. That was survivable
    /// only while every writer ran on the transfer lane's single worker
    /// (ADR-0029 §4). A verification pass on any other lane makes it real, and
    /// the field most likely to be lost is <c>SyncedSequence</c> — the trim
    /// gate's currency (FR-GC-009).
    /// </remarks>
    private DestinationSyncRecord Mutate(
        string setId, string destination, Func<DestinationSyncRecord?, DestinationSyncRecord> transform)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(setId);
        ThrowHelper.ThrowIfNullOrWhiteSpace(destination);
        ThrowHelper.ThrowIfNull(transform);

        lock (_gate)
        {
            var record = transform(FindLocked(setId, destination));

            // One row per (set, destination): current state, not a log.
            _records.RemoveAll(existing =>
                string.Equals(existing.SetId, setId, StringComparison.Ordinal)
                && string.Equals(existing.Destination, destination, StringComparison.Ordinal));
            _records.Add(record);
            AtomicFile.WriteAllText(
                _path,
                JsonSerializer.Serialize(
                    new LedgerFile { SchemaVersion = CurrentSchemaVersion, Destinations = _records },
                    SerializerOptions));
            return record;
        }
    }

    private DestinationSyncRecord? FindLocked(string setId, string destination) =>
        _records.LastOrDefault(record =>
            string.Equals(record.SetId, setId, StringComparison.Ordinal)
            && string.Equals(record.Destination, destination, StringComparison.Ordinal));
}
