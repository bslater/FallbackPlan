using Bodu;
using FallbackPlan.Domain;
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
    /// The staging archive's journal head when the last sync that converged
    /// this pair while a deletion was pending <b>began</b> (FR-GC-013): every
    /// request made before it reached the destination. The replication gate
    /// holds a requested snapshot while this is below the request's
    /// generation. Monotonic, like <see cref="SyncedSequence"/>, and zero
    /// until a converge has run with a request pending.
    /// </summary>
    [JsonPropertyName("converged_sequence")]
    public ulong ConvergedSequence { get; init; }

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
    /// How many of <see cref="VerifiedObjects"/> the last passed verification
    /// proved by opening a record's AEAD tag at the destination (schema 3).
    /// Zero on a ledger written before the tiers were recorded, which is
    /// honest: nobody counted them.
    /// </summary>
    [JsonPropertyName("verified_sealed")]
    public int VerifiedSealed { get; init; }

    /// <summary>
    /// How many of <see cref="VerifiedObjects"/> the last passed verification
    /// proved by hashing the whole sealed blob at the destination against the
    /// digest the writer signed (schema 3) — a write-only set's data plane's
    /// proof.
    /// </summary>
    [JsonPropertyName("verified_digest")]
    public int VerifiedDigest { get; init; }

    /// <summary>
    /// How many of <see cref="VerifiedObjects"/> the last passed
    /// verification proved by asking the destination for one leaf of the
    /// blob's Merkle commitment and its authentication path, checked against
    /// the root the writer signed (schema 4). A <b>sampled</b> proof of the
    /// blob rather than a whole-blob one, counted apart from
    /// <see cref="VerifiedDigest"/> so that the cheaper tier cannot be read
    /// as the stronger one.
    /// </summary>
    [JsonPropertyName("verified_chunk")]
    public int VerifiedChunk { get; init; }

    /// <summary>
    /// Where the sync-time challenge rotation resumes — the highest key the
    /// last passed verification asked about, or null to start at the
    /// beginning of the key space.
    /// </summary>
    /// <remarks>
    /// This is what makes sampling coverage <i>accumulate</i> (FR-VER-002). A
    /// stateless sample re-asks about the same objects for as long as the
    /// destination lives: sixteen of ten thousand objects, drawn afresh every
    /// pass, will in expectation never reach most of them. It advances only on
    /// a pass that actually proved something — a failed or empty pass leaves
    /// it where it was, so the next pass re-asks the questions that were not
    /// answered.
    /// </remarks>
    [JsonPropertyName("sample_cursor")]
    public string? SampleCursor { get; init; }

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

    /// <summary>
    /// The store keys a deep sweep found damaged at this destination and could
    /// not repair; null when none are outstanding (schema 6,
    /// [ADR-0035](../../docs/adr/0035-destination-fitness.md) Amendment 1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// While any are listed the pair is <see cref="DestinationSyncState.Failed"/>
    /// whatever else is recorded of it: <see cref="DestinationSyncStore.RecordSuccess"/>
    /// keeps it failed and says why. A capture ships its new blobs to a
    /// destination knowing nothing of the old ones, so a success is not
    /// evidence the damage has gone, and a pair read as in sync over objects
    /// that cannot be restored would count as protection it does not give.
    /// </para>
    /// <para>
    /// Keys leave the list when the sync re-reads them and finds them sound,
    /// repaired, or no longer held.
    /// </para>
    /// </remarks>
    [JsonPropertyName("damaged_keys")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DamagedKeys { get; init; }

    /// <summary>
    /// Whether the failure standing on this row is damage the ledger names in
    /// <see cref="DamagedKeys"/> and nothing else: the destination was reached
    /// and read, and what is wrong with it is those objects (schema 7,
    /// FR-VER-005).
    /// </summary>
    /// <remarks>
    /// What lets a pair failed for its damage degrade only the snapshots that
    /// need the damaged objects. A failure of any other kind — a refusal, a
    /// failed proof, a copy that broke off — makes the whole copy suspect, and
    /// a damage finding recorded over one does not narrow it: only a success
    /// says the rest of the copy answers. False on a row written before the
    /// field existed, which reads every failure as the whole copy's, the
    /// reading that cannot understate damage.
    /// </remarks>
    [JsonPropertyName("damage_only")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DamageOnly { get; init; }

    /// <summary>
    /// Deep-sweep segments in a row that stopped short at a blob they could
    /// not read, or could not read the replica at all; zero once a segment
    /// finishes (schema 6, [ADR-0035](../../docs/adr/0035-destination-fitness.md)
    /// Amendment 1).
    /// </summary>
    /// <remarks>
    /// Sets how long the next attempt waits — the sync's back-off, not the
    /// next pass — because a circuit under way is otherwise due on every
    /// pass, and a blob that will not read would be read up to once a minute
    /// for as long as it would not. A stalled segment that still read past
    /// where the last one stopped counts from one again.
    /// </remarks>
    [JsonPropertyName("sweep_stalls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SweepStalls { get; init; }

    /// <summary>When the last stalled segment stopped, Unix milliseconds; null when none is stalled.</summary>
    [JsonPropertyName("sweep_stalled_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? SweepStalledAt { get; init; }

    /// <summary>
    /// The blob the last stalled segment could not read; null when the
    /// replica itself could not be read, or when none is stalled.
    /// </summary>
    [JsonPropertyName("sweep_stalled_on")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SweepStalledOn { get; init; }

    /// <summary>
    /// The snapshot whose complete closure first made this destination a full
    /// replica; null while it holds none. Declared ahead of its writer
    /// (ADR-0047 §6): nothing fills it yet — the schema carries the field so
    /// the build that starts writing it needs no migration, and every reader
    /// already treats null as "not recorded".
    /// </summary>
    [JsonPropertyName("baseline_snapshot_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaselineSnapshotId { get; init; }

    /// <summary>
    /// When this destination first held a full copy, Unix milliseconds; null
    /// while it never has. This is the field rule "a destination without a
    /// full backup is skipped by incrementals" reads (ADR-0047), and the
    /// baseline never moves on later syncs — it records the first full.
    /// </summary>
    [JsonPropertyName("baseline_completed_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? BaselineCompletedAt { get; init; }

    /// <summary>
    /// Whether this pair owes the destination a full backup — set when a set
    /// gains the destination, cleared by the success that establishes the
    /// baseline.
    /// </summary>
    [JsonPropertyName("needs_full")]
    public bool NeedsFull { get; init; }

    /// <summary>
    /// When this row was last rebuilt from the destination's own inventory,
    /// Unix milliseconds; null when never. The ledger is metadata about the
    /// destination, and the destination stays the ground truth. Declared ahead
    /// of its writer by [ADR-0047](../../docs/adr/0047-backup-pool-and-priorities.md)
    /// and written since [ADR-0056](../../docs/adr/0056-incremental-reconciliation.md),
    /// which is also what reads it: a pass may only skip on the strength of a
    /// recent reading-through, so this stamp is what gives the skip a shelf
    /// life.
    /// </summary>
    [JsonPropertyName("last_reconciled_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? LastReconciledAt { get; init; }

    /// <summary>
    /// A stable rendering of the keep-set this destination's policy selected
    /// when the last pass ran; null when its policy keeps everything.
    /// </summary>
    /// <remarks>
    /// The one input to a pass that moves with the clock rather than with
    /// publication (ADR-0056): a snapshot ages out of a retention window while
    /// nothing at all is published, and the destination is then owed a
    /// deletion no publication sequence would ever reveal. Comparing the
    /// rendering is how a pass tells "nothing has changed" from "nothing has
    /// been published".
    /// </remarks>
    [JsonPropertyName("keep_fingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KeepFingerprint { get; init; }

    /// <summary>
    /// Bytes this destination holds of what it is owed, as the last pass
    /// counted them; zero when nothing has counted.
    /// </summary>
    /// <remarks>
    /// The numerator of a completion figure, recorded by the pass rather than
    /// measured on demand: converging a destination lists both sides anyway,
    /// so the bytes are in hand. Asking a status poll for them instead would
    /// mean listing a whole replica every few seconds.
    /// </remarks>
    [JsonPropertyName("held_bytes")]
    public long HeldBytes { get; init; }

    /// <summary>
    /// Bytes this destination is owed in total, as the last pass counted
    /// them; zero when nothing has counted.
    /// </summary>
    /// <remarks>
    /// Owed by <em>this</em> destination's own policy, not by the set: a
    /// narrow per-destination retention override is complete when it holds
    /// its own keep-set (FR-GC-010), and measuring it against a wider
    /// sibling's would leave it permanently short for doing as it was told.
    /// </remarks>
    [JsonPropertyName("owed_bytes")]
    public long OwedBytes { get; init; }

    /// <summary>
    /// When <see cref="HeldBytes"/> and <see cref="OwedBytes"/> were counted,
    /// Unix milliseconds; null when they never have been.
    /// </summary>
    /// <remarks>
    /// Null is not zero, and the difference is the whole point: a destination
    /// no pass has reached holds an unknown amount, and drawing that as an
    /// empty gauge would claim it holds nothing when nobody has looked.
    /// </remarks>
    [JsonPropertyName("measured_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? MeasuredAt { get; init; }

    /// <summary>
    /// When a restore drill last ran against this destination's replica,
    /// Unix milliseconds; null when none ever has ([ADR-0054](../../docs/adr/0054-scheduled-restore-drills.md)).
    /// </summary>
    /// <remarks>
    /// Null and a failure are different answers and both are kept. "Nobody
    /// has tried" is not "we tried and it did not work", and a surface that
    /// cannot tell them apart turns an unexercised destination into a
    /// reassuring one.
    /// </remarks>
    [JsonPropertyName("drilled_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? DrilledAt { get; init; }

    /// <summary>Files the last drill brought back whole; zero when it brought back none.</summary>
    [JsonPropertyName("drill_files")]
    public int DrillFiles { get; init; }

    /// <summary>Bytes those files amounted to.</summary>
    [JsonPropertyName("drill_bytes")]
    public long DrillBytes { get; init; }

    /// <summary>
    /// Why the last drill did not come back with a file, in the drill's own
    /// words; null when it did.
    /// </summary>
    /// <remarks>
    /// Recorded beside <see cref="DrilledAt"/> rather than through
    /// <see cref="DestinationSyncState.Failed"/>, because a drill answers a
    /// different question from a copy. A destination can hold every byte it
    /// was sent, prove possession of them, and still not be restorable — and
    /// calling that a sync failure would put the fault on the copy that
    /// worked.
    /// </remarks>
    [JsonPropertyName("drill_failure")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DrillFailure { get; init; }

    /// <summary>
    /// What the last drill could not prove, in its own words, when it passed
    /// with a stated limit; null when it proved everything it set out to, when
    /// it failed, and when none has run.
    /// </summary>
    /// <remarks>
    /// A write-only set's replica seals its content to a key the service does
    /// not hold (ADR-0042 §7), so a drill run by the service proves the road
    /// back as far as the sealed content and no further — the replica opens,
    /// its index and catalogue rebuild, the sampled files' manifests and
    /// segment records are found — and says so here rather than reporting
    /// the passphrase's absence as damage (ADR-0054 Amendment 2). A pass with
    /// a limit is still a pass: <see cref="DrillFailure"/> stays null.
    /// </remarks>
    [JsonPropertyName("drill_limit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DrillLimit { get; init; }

    /// <summary>
    /// How many drills in a row have ended without completing, cut short by a
    /// fault in the service rather than by an answer about the replica; zero
    /// once a drill completes, passing or failing (schema 5,
    /// [ADR-0054](../../docs/adr/0054-scheduled-restore-drills.md) Amendment 4).
    /// </summary>
    /// <remarks>
    /// It is what brings the pair's next drill forward. A drill that did not
    /// complete is due again on a back-off from an hour rather than at the
    /// pair's interval, so a fault that has passed does not stand as a failed
    /// drill for a month, and one that lasts is not retried every pass.
    /// </remarks>
    [JsonPropertyName("consecutive_incomplete_drills")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ConsecutiveIncompleteDrills { get; init; }

    /// <summary>
    /// The peer's clock minus this one's, as the latest verified receipt from
    /// this destination read it (schema 8, NFR-TIME-002,
    /// [ADR-0077](../../docs/adr/0077-observed-clock-skew.md)); null where no
    /// exchange has read one, which is every kind but a peer.
    /// </summary>
    [JsonPropertyName("clock_skew_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ClockSkewMs { get; init; }

    /// <summary>When that reading was complete, by this machine's clock.</summary>
    [JsonPropertyName("clock_observed_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? ClockObservedAt { get; init; }

    /// <summary>How long the bracketed exchange took; half of it bounds the reading's error.</summary>
    [JsonPropertyName("clock_round_trip_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? ClockRoundTripMs { get; init; }

    /// <summary>The three fields above as one reading, or null when the row holds none.</summary>
    [JsonIgnore]
    public ClockObservation? Clock =>
        ClockSkewMs is { } skew && ClockObservedAt is { } at && ClockRoundTripMs is { } roundTrip
            ? new ClockObservation(skew, at, roundTrip)
            : null;
}

/// <summary>
/// What a passed verification proved, as the ledger stamps it on a pair's
/// row (FR-VER-003).
/// </summary>
/// <param name="Objects">Ranges the pass proved.</param>
/// <param name="Population">Objects eligible when the sample was drawn — the coverage denominator.</param>
/// <param name="VerifiedSequence">The highest publication sequence the pass's sample covered.</param>
/// <param name="SampleCursor">
/// Where the next pass's rotation resumes; null when this pass reached the
/// end of the key space and the rotation starts over.
/// </param>
/// <param name="Sealed">How many of <paramref name="Objects"/> were proved by a record's AEAD tag.</param>
/// <param name="Digest">How many of <paramref name="Objects"/> were proved by the signed whole-blob digest.</param>
/// <param name="Chunk">How many of <paramref name="Objects"/> were proved by one leaf of the signed Merkle commitment.</param>
public sealed record VerificationStamp(
    int Objects, int Population, ulong VerifiedSequence, string? SampleCursor,
    int Sealed = 0, int Digest = 0, int Chunk = 0);

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
    private const int CurrentSchemaVersion = 9;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly List<DestinationSyncRecord> _records;
    private readonly Lock _gate = new();
    private Action<DestinationSyncRecord>? _written;

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
    /// <param name="stateDirectory">The directory holding <c>destinations.json</c>.</param>
    /// <param name="written">
    /// Called with each row as it is written, under the ledger's lock, so rows
    /// arrive in the order they were written and each is a state a reader
    /// could have read. It must not write to the ledger, and one that throws
    /// fails a write that has already happened. Null, the production value,
    /// observes nothing.
    /// </param>
    public static DestinationSyncStore Open(string stateDirectory, Action<DestinationSyncRecord>? written = null)
    {
        var store = Load(stateDirectory);
        store._written = written;
        return store;
    }

    private static DestinationSyncStore Load(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, "destinations.json");

        if (!File.Exists(path))
        {
            return new DestinationSyncStore(path, []);
        }

        // Read sharing deletion because another process may open the ledger
        // while the service replaces it: `status` given a repository reads it
        // without the writer role.
        var text = AtomicFile.ReadAllText(path);
        try
        {
            var file = JsonSerializer.Deserialize<LedgerFile>(text, SerializerOptions);
            if (file is not null && file.SchemaVersion <= CurrentSchemaVersion)
            {
                return new DestinationSyncStore(path, Migrate(file));
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
            // them would silently restart every destination's history. It
            // rides the same migration as schema 1: the bare array predates
            // it, so its rows are owed every rule schema 1's are, baseline
            // seeding included.
            try
            {
                var legacy = JsonSerializer.Deserialize<List<DestinationSyncRecord>>(text, SerializerOptions) ?? [];
                return new DestinationSyncStore(
                    path, Migrate(new LedgerFile { SchemaVersion = 1, Destinations = legacy }));
            }
            catch (JsonException)
            {
                return Quarantine(path);
            }
        }
    }

    private static DestinationSyncStore Quarantine(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A .corrupt target something is holding must not turn a
            // sacrificial ledger into a service that will not start: the
            // empty store below is the same recovery either way, and the
            // unreadable bytes stay where they are until the next write
            // replaces them — strictly worse than the rename, still better
            // than being down.
        }

        return new DestinationSyncStore(path, []);
    }

    /// <summary>
    /// Rows written before schema 2 seed their baselines from their
    /// successes: on the staging architecture every successful sync copied
    /// the whole archive, so a pair with a success already IS a full replica
    /// — this is "replicas seed the ledgers" (ADR-0047), landing at open so
    /// no install re-ships terabytes to learn what it already holds.
    /// </summary>
    private static List<DestinationSyncRecord> Migrate(LedgerFile file)
    {
        // Schema 3 added the verification tiers as plain additive columns: a
        // schema-2 row reads them as zero, which says exactly what is true of
        // it — the tiers were not counted — so 2 → 3 needs no rewrite. Schemas
        // 4, 5, 6 and 7 did the same with the chunk tier, the count of drills
        // that did not complete, the keys found damaged and not repaired, and
        // whether a failure is that damage alone — false, the whole copy's,
        // being the reading of an older row that cannot understate damage.
        // Schema 8 added a reading of the peer's clock, absent from an older
        // row because nothing had read one. Schema 9 added the sequence the
        // last converge with a deletion pending began at, zero on an older row
        // because none had run: a pending deletion then waits for one, which is
        // the cautious reading. Only 1 → 2 changes a row, below.
        var rows = file.Destinations ?? [];
        if (file.SchemaVersion >= 2)
        {
            return rows;
        }

        return [.. rows.Select(row => row is { LastSuccessAt: not null, BaselineCompletedAt: null }
            ? row with { BaselineCompletedAt = row.LastSuccessAt }
            : row)];
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
    /// <param name="keepFingerprint">
    /// This destination's keep-set as the pass computed it, or null when the
    /// caller computed none — a destination that keeps everything has a
    /// rendering of its own, because "keeps everything" is a keep-set and
    /// "nobody looked" is not. Compared by the next pass to tell a keep-set
    /// that moved with the clock from one that did not (ADR-0056).
    /// </param>
    /// <param name="reconciled">
    /// Whether this pass read both inventories through. Only a pass that did
    /// may stamp <see cref="DestinationSyncRecord.LastReconciledAt"/>, because
    /// that stamp is what a later pass skips on.
    /// </param>
    /// <param name="baselineSnapshotId">
    /// The newest snapshot this destination held when its baseline completed.
    /// Recorded once, with the baseline, and never moved after.
    /// </param>
    /// <param name="verified">
    /// What the same pass proved of the copy, or null when it proved nothing.
    /// Stamped in this write rather than a second one: between two, the row
    /// would call the pair in sync with nothing saying the copy was proved,
    /// and a reader there would report a snapshot this pass did prove as
    /// merely durable (FR-SNP-003).
    /// </param>
    /// <param name="convergedSequence">
    /// The staging journal head when this pass began, for a pass that
    /// converged the pair while a deletion was pending (FR-GC-013); null for
    /// any other pass, which leaves the last one's standing.
    /// </param>
    public DestinationSyncRecord RecordSuccess(
        string setId,
        string destination,
        long objects,
        ulong nowUnixMilliseconds,
        ulong syncedSequence = 0,
        string? keepFingerprint = null,
        bool reconciled = false,
        string? baselineSnapshotId = null,
        VerificationStamp? verified = null,
        ulong? convergedSequence = null)
    {
        // Everything not named here is carried forward by `with` — including,
        // on a pass that proved nothing, the verification stamps, which
        // outlive the sync that earned them: they say when bytes were last
        // proven, which a newer copy does not undo.
        return Mutate(setId, destination, previous =>
        {
            // Damage nobody could repair outlives any copy that succeeds
            // around it: the pair stays failed, says why, and is not counted
            // as recovered, so its back-off is not reset either.
            var damaged = previous?.DamagedKeys is { Count: > 0 } keys ? keys : null;
            var synced = Seed(previous, setId, destination, nowUnixMilliseconds, DestinationSyncState.InSync) with
            {
                State = damaged is null ? DestinationSyncState.InSync : DestinationSyncState.Failed,
                // The copy answered, so whatever failure stood before, all
                // that is left standing is the damage.
                DamageOnly = damaged is not null,
                LastAttemptAt = nowUnixMilliseconds,
                LastSuccessAt = nowUnixMilliseconds,
                Objects = objects,
                ConsecutiveFailures = damaged is null ? 0 : previous!.ConsecutiveFailures,
                // Success clears the last failure's message. Stated, because `with`
                // would otherwise carry it forward and `status` would repeat a
                // resolved error verbatim forever.
                LastError = damaged is null ? null : DamageStatement(damaged),
                // A later sync never un-holds what an earlier one delivered.
                SyncedSequence = Math.Max(syncedSequence, previous?.SyncedSequence ?? 0),
                // Nor un-converges what an earlier one removed: a pass that
                // converged nothing, or began earlier, leaves it standing.
                ConvergedSequence = Math.Max(convergedSequence ?? 0, previous?.ConvergedSequence ?? 0),
                // The first success is the full copy that establishes the
                // baseline (a staging-model sync converges the whole archive);
                // later successes never move it — it records the first full.
                BaselineCompletedAt = previous?.BaselineCompletedAt ?? nowUnixMilliseconds,
                BaselineSnapshotId = previous?.BaselineSnapshotId ?? baselineSnapshotId,
                NeedsFull = false,
                // Carried forward when the caller computed none: a run recorded by
                // the ship sink knows nothing about retention, and clearing the
                // fingerprint there would make the next pass see a keep-set that
                // had moved when it had not.
                KeepFingerprint = keepFingerprint ?? previous?.KeepFingerprint,
                // Carried forward, never cleared: an incremental pass leaves the
                // last reading-through standing, which is exactly what its own
                // expiry is measured from.
                LastReconciledAt = reconciled ? nowUnixMilliseconds : previous?.LastReconciledAt,
            };

            return verified is null ? synced : Stamp(synced, verified, nowUnixMilliseconds);
        });
    }

    /// <summary>
    /// Records how much of what a destination is owed it holds, and when that
    /// was counted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="RecordSuccess"/> on purpose, because the two
    /// answer different questions and a failed pass answers only this one. A
    /// drive pulled halfway through leaves a destination genuinely part-full;
    /// folding the count into the success would mean the only destinations
    /// that could report being behind are the ones that are not.
    /// </para>
    /// <para>
    /// Both halves are written together. Read separately they could be paired
    /// out of step — a new numerator against an old denominator — which is a
    /// percentage above a hundred or below zero, and a reader has no way to
    /// tell that from a real one.
    /// </para>
    /// </remarks>
    /// <param name="setId">The backup set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="heldBytes">Bytes it holds of what it is owed.</param>
    /// <param name="owedBytes">Bytes it is owed in total.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    public DestinationSyncRecord RecordCompleteness(
        string setId, string destination, long heldBytes, long owedBytes, ulong nowUnixMilliseconds)
    {
        return Mutate(setId, destination, previous => Seed(previous, setId, destination, nowUnixMilliseconds, DestinationSyncState.Behind) with
        {
            HeldBytes = heldBytes,
            OwedBytes = owedBytes,
            MeasuredAt = nowUnixMilliseconds,
        });
    }

    /// <summary>
    /// Records a reading of the destination's clock against this one's
    /// (NFR-TIME-002, [ADR-0077](../../docs/adr/0077-observed-clock-skew.md)),
    /// replacing the pair's previous reading and nothing else on the row: a
    /// reading is not a sync, and says nothing of one.
    /// </summary>
    /// <param name="setId">The backup set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="observation">The reading.</param>
    public DestinationSyncRecord RecordClockObservation(
        string setId, string destination, ClockObservation observation)
    {
        ThrowHelper.ThrowIfNull(observation);

        return Mutate(setId, destination, previous => Seed(previous, setId, destination, observation.ObservedAt, DestinationSyncState.Behind) with
        {
            ClockSkewMs = observation.SkewMilliseconds,
            ClockObservedAt = observation.ObservedAt,
            ClockRoundTripMs = observation.RoundTripMilliseconds,
        });
    }

    /// <summary>
    /// Records what a restore drill found: the files it brought back whole
    /// from this destination's own replica, or why it could not
    /// ([ADR-0054](../../docs/adr/0054-scheduled-restore-drills.md)).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stamp moves on a failed drill as well as a passed one, which is
    /// the opposite of how the verification stamps behave, and deliberately.
    /// A verification stamp answers "when were bytes last proven", so a
    /// failure must leave the last true answer standing. A drill stamp
    /// answers "when did we last try to recover", and a failed attempt IS a
    /// try — leaving yesterday's success on the row would report a
    /// destination as recently drilled when the most recent drill said it
    /// cannot be restored.
    /// </para>
    /// <para>
    /// The sync half of the row is untouched. A destination can hold every
    /// byte it was sent and still fail to restore; that is not a failure of
    /// the copy, and recording it as one would back off the transfers as
    /// though they were at fault.
    /// </para>
    /// </remarks>
    /// <param name="setId">The backup set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="files">Files restored whole; zero on a failure.</param>
    /// <param name="bytes">What those files amounted to.</param>
    /// <param name="failure">Why it did not work, or null when it did.</param>
    /// <param name="limit">What a passing drill could not prove, or null when it proved everything.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    public DestinationSyncRecord RecordDrill(
        string setId, string destination, int files, long bytes, string? failure, string? limit, ulong nowUnixMilliseconds)
    {
        return Mutate(setId, destination, previous => Seed(previous, setId, destination, nowUnixMilliseconds, DestinationSyncState.Behind) with
        {
            DrilledAt = nowUnixMilliseconds,
            DrillFiles = files,
            DrillBytes = bytes,
            DrillFailure = failure,
            DrillLimit = limit,
            ConsecutiveIncompleteDrills = 0,
        });
    }

    /// <summary>
    /// Records a drill that did not complete: ended by a fault in the service
    /// while it ran, not by an answer about the replica (ADR-0054 Amendment 4).
    /// </summary>
    /// <remarks>
    /// On the row it is a failed drill like any other: the stamp moves,
    /// because it was a try, and the reason stands in the drill's own words.
    /// It is also counted, which is what lets the next drill come sooner than
    /// the pair's interval and back off while the fault lasts. A drill that
    /// completes, through <see cref="RecordDrill"/>, ends the count.
    /// </remarks>
    /// <param name="setId">The backup set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="failure">What stopped it, in the drill's own words.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    public DestinationSyncRecord RecordIncompleteDrill(
        string setId, string destination, string failure, ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(failure);

        return Mutate(setId, destination, previous => Seed(previous, setId, destination, nowUnixMilliseconds, DestinationSyncState.Behind) with
        {
            DrilledAt = nowUnixMilliseconds,
            DrillFiles = 0,
            DrillBytes = 0,
            DrillFailure = failure,
            DrillLimit = null,
            ConsecutiveIncompleteDrills = (previous?.ConsecutiveIncompleteDrills ?? 0) + 1,
        });
    }

    /// <summary>
    /// Marks a pair as owing the destination a full backup — a set just
    /// gained this destination (ADR-0047). Cleared by the success that
    /// establishes the baseline; a no-op on a pair that already holds one.
    /// </summary>
    /// <param name="setId">The backup set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    public DestinationSyncRecord RecordNeedsFull(string setId, string destination, ulong nowUnixMilliseconds)
    {
        return Mutate(setId, destination, previous => Seed(previous, setId, destination, nowUnixMilliseconds, DestinationSyncState.Behind) with
        {
            NeedsFull = previous?.BaselineCompletedAt is null,
        });
    }

    /// <summary>
    /// Marks a pair as behind without counting it a failure: a run held the
    /// destination out because it missed a prior run (ADR-0046 §3), which is
    /// a fact about its history, not a fault of its own — the failure
    /// counter stays put so the healing catch-up runs immediately instead of
    /// backing off from a "failure" nothing failed.
    /// </summary>
    /// <param name="setId">The backup set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="reason">Why the pair was held out, for status.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    public DestinationSyncRecord RecordBehind(
        string setId, string destination, string reason, ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(reason);

        return Mutate(setId, destination, previous => Seed(previous, setId, destination, nowUnixMilliseconds, DestinationSyncState.Behind) with
        {
            State = DestinationSyncState.Behind,
            LastAttemptAt = nowUnixMilliseconds,
            LastError = reason,
            DamageOnly = false,
        });
    }

    /// <summary>
    /// Records a passed verification: sampled bytes were proven present at
    /// the destination just now (FR-VER-005's happy half). The failure half
    /// goes through <see cref="RecordFailure"/> — a failed proof is a sync
    /// failure, and the stamps here keep saying when bytes were LAST proven.
    /// A pass that also copied hands its proof to <see cref="RecordSuccess"/>
    /// instead, so that the copy and the proof are one write.
    /// </summary>
    /// <param name="setId">The backup set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="objects">Ranges the pass proved.</param>
    /// <param name="population">Objects eligible when the sample was drawn — the coverage denominator.</param>
    /// <param name="verifiedSequence">The highest publication sequence the pass's sample covered.</param>
    /// <param name="sampleCursor">
    /// Where the next pass's rotation resumes; null when this pass reached the
    /// end of the key space and the rotation starts over.
    /// </param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    /// <param name="sealed">How many of <paramref name="objects"/> were proved by a record's AEAD tag.</param>
    /// <param name="digest">How many of <paramref name="objects"/> were proved by the signed whole-blob digest.</param>
    /// <param name="chunk">How many of <paramref name="objects"/> were proved by one leaf of the signed Merkle commitment.</param>
    public DestinationSyncRecord RecordVerification(
        string setId, string destination, int objects, int population, ulong verifiedSequence,
        string? sampleCursor, ulong nowUnixMilliseconds, int @sealed = 0, int digest = 0, int chunk = 0)
    {
        // A verification touches only the stamps: the sync half of the row —
        // state, attempt, success, the synced sequence — is carried forward
        // untouched, because proving bytes says nothing about when they
        // arrived.
        return Mutate(setId, destination, previous => Stamp(
            Seed(previous, setId, destination, nowUnixMilliseconds, DestinationSyncState.Behind),
            new VerificationStamp(objects, population, verifiedSequence, sampleCursor, @sealed, digest, chunk),
            nowUnixMilliseconds));
    }

    /// <summary>The row with a passed verification's stamps on it.</summary>
    /// <remarks>
    /// The cursor rides with the stamps rather than with the sync, and only
    /// on a pass that passed: advancing it after a failure would walk the
    /// rotation past objects nobody proved anything about.
    /// </remarks>
    private static DestinationSyncRecord Stamp(
        DestinationSyncRecord row, VerificationStamp proof, ulong nowUnixMilliseconds) =>
        row with
        {
            VerifiedAt = nowUnixMilliseconds,
            VerifiedSequence = Math.Max(proof.VerifiedSequence, row.VerifiedSequence),
            VerifiedObjects = proof.Objects,
            VerifiedPopulation = proof.Population,
            VerifiedSealed = proof.Sealed,
            VerifiedDigest = proof.Digest,
            VerifiedChunk = proof.Chunk,
            SampleCursor = proof.SampleCursor,
        };

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
        return Mutate(setId, destination, previous => Seed(previous, setId, destination, nowUnixMilliseconds, DestinationSyncState.Behind) with
        {
            SweepCursor = cursor,
            SweptAt = nowUnixMilliseconds,
            // The tally resets with the circuit, so it always answers "how far
            // through the CURRENT pass", never an ever-growing lifetime count
            // that would imply coverage it does not have.
            SweptThisCircuit = completedCircuit ? 0 : (previous?.SweptThisCircuit ?? 0) + examined,
            SweepCompletedAt = completedCircuit ? nowUnixMilliseconds : previous?.SweepCompletedAt,
            SweepStalls = 0,
            SweepStalledAt = null,
            SweepStalledOn = null,
        });
    }

    /// <summary>
    /// Records a deep-sweep segment that stopped short — at a blob it could
    /// not read, or before reading anything because the replica itself could
    /// not be read — and counts the stall ([ADR-0035](../../docs/adr/0035-destination-fitness.md)
    /// Amendment 1).
    /// </summary>
    /// <remarks>
    /// What the segment read before it stopped stands: the cursor moves to
    /// the last blob read, so the next attempt begins with the one that would
    /// not read instead of re-reading the run up to it. A segment that read
    /// nothing moves nothing, and does not move "when the sweep last read
    /// anything" either.
    /// </remarks>
    /// <param name="setId">The backup set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="cursor">The last blob read before the stall; ignored when <paramref name="examined"/> is zero.</param>
    /// <param name="examined">Blobs read before the stall.</param>
    /// <param name="stalledOn">The blob that would not read; null when the replica itself could not be read.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    /// <returns>The row as written, carrying the stall count the caller decides on.</returns>
    public DestinationSyncRecord RecordSweepStall(
        string setId, string destination, string? cursor, int examined, string? stalledOn,
        ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfLessThan(examined, 0);

        return Mutate(setId, destination, previous =>
        {
            var row = Seed(previous, setId, destination, nowUnixMilliseconds, DestinationSyncState.Behind);
            if (examined > 0)
            {
                row = row with
                {
                    SweepCursor = cursor,
                    SweptAt = nowUnixMilliseconds,
                    SweptThisCircuit = (previous?.SweptThisCircuit ?? 0) + examined,
                };
            }

            return row with
            {
                // Read past where the last one stopped: a new stall, not the
                // old one again.
                SweepStalls = examined > 0 ? 1 : (previous?.SweepStalls ?? 0) + 1,
                SweepStalledAt = nowUnixMilliseconds,
                SweepStalledOn = stalledOn,
            };
        });
    }

    /// <summary>
    /// Records what a deep sweep or a sync found about damaged objects at a
    /// destination: the keys still damaged with no sound copy to repair them
    /// from, and the keys now resolved — repaired, found sound, or no longer
    /// held ([ADR-0035](../../docs/adr/0035-destination-fitness.md) Amendment 1).
    /// </summary>
    /// <remarks>
    /// Applied as a set difference under the ledger's lock rather than written
    /// as a whole list, because the sweep and the sync each know only the keys
    /// they looked at, and each must leave the other's standing.
    /// </remarks>
    /// <param name="setId">The backup set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="unrepaired">Keys found damaged and not repaired.</param>
    /// <param name="resolved">Keys no longer outstanding.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    public DestinationSyncRecord RecordDamage(
        string setId,
        string destination,
        IReadOnlyCollection<string> unrepaired,
        IReadOnlyCollection<string> resolved,
        ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNull(unrepaired);
        ThrowHelper.ThrowIfNull(resolved);

        return Mutate(setId, destination, previous =>
        {
            var keys = new SortedSet<string>(previous?.DamagedKeys ?? [], StringComparer.Ordinal);
            keys.UnionWith(unrepaired);
            keys.ExceptWith(resolved);
            return Seed(previous, setId, destination, nowUnixMilliseconds, DestinationSyncState.Behind) with
            {
                DamagedKeys = keys.Count == 0 ? null : [.. keys],
            };
        });
    }

    /// <summary>
    /// What a pair carrying unrepaired damage says about it, in the ledger's
    /// words: which objects, and that nothing could replace them.
    /// </summary>
    /// <param name="damagedKeys">The keys still outstanding; at least one.</param>
    public static string DamageStatement(IReadOnlyList<string> damagedKeys)
    {
        ThrowHelper.ThrowIfNull(damagedKeys);
        ThrowHelper.ThrowIfLessThan(damagedKeys.Count, 1);

        // Worded for both kinds: a local path's damage stands because no
        // sound copy was found, a peer's because nothing here can put one
        // there, and either way the same thing is true of the destination.
        var others = damagedKeys.Count - 1;
        return $"{damagedKeys.Count} object(s) found damaged here have had no sound copy put in their place: "
            + $"blob {damagedKeys[0]}{(others > 0 ? $" and {others} more" : string.Empty)} — "
            + "what needs them cannot be restored from this destination until they are replaced";
    }

    /// <summary>
    /// Records a deep sweep that reached a destination and could not read it —
    /// a peer that will not serve the retrieval session
    /// ([ADR-0035](../../docs/adr/0035-destination-fitness.md) Amendment 2).
    /// </summary>
    /// <remarks>
    /// Stamped so the next attempt waits the destination's interval rather
    /// than following on the next pass, as an open circuit's segments do: a
    /// refusal is not going to change in a minute, and each attempt holds the
    /// one transfer worker while it fails. The circuit starts over when
    /// reading next succeeds, because a replica nobody could read for an
    /// interval may not be the one the old cursor was walking.
    /// </remarks>
    /// <param name="setId">The backup set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    public DestinationSyncRecord RecordSweepUnreadable(string setId, string destination, ulong nowUnixMilliseconds)
    {
        return Mutate(setId, destination, previous => Seed(previous, setId, destination, nowUnixMilliseconds, DestinationSyncState.Behind) with
        {
            SweepCursor = null,
            SweptAt = nowUnixMilliseconds,
            SweptThisCircuit = 0,
            // The interval governs from here, not a stall's back-off.
            SweepStalls = 0,
            SweepStalledAt = null,
            SweepStalledOn = null,
        });
    }

    /// <summary>Records a failed or refused attempt, keeping the last success and counting toward back-off.</summary>
    /// <param name="setId">The backup set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="state">Where the attempt leaves the pair.</param>
    /// <param name="error">What the failure said, for status to repeat.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    /// <param name="damageOnly">
    /// Whether this failure is the damage the row's <see cref="DestinationSyncRecord.DamagedKeys"/>
    /// names and nothing else — a deep sweep, a sync's re-check or a restore
    /// finding objects altered (FR-VER-005). It holds only while no failure
    /// of another kind stands: one recorded over such a failure leaves the
    /// whole copy suspect until a success.
    /// </param>
    public DestinationSyncRecord RecordFailure(
        string setId, string destination, DestinationSyncState state, string error, ulong nowUnixMilliseconds,
        bool damageOnly = false)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(error);

        // The last success and every verification stamp survive a failure:
        // they record what WAS true, and a failed attempt does not un-prove
        // bytes that were proven. Only the failure counters move.
        return Mutate(setId, destination, previous => Seed(previous, setId, destination, nowUnixMilliseconds, DestinationSyncState.Behind) with
        {
            State = state,
            LastAttemptAt = nowUnixMilliseconds,
            ConsecutiveFailures = (previous?.ConsecutiveFailures ?? 0) + 1,
            LastError = error,
            DamageOnly = damageOnly && previous is not { State: DestinationSyncState.Failed, DamageOnly: false },
        });
    }

    /// <summary>
    /// Records a sync that reached the destination, copied what it was owed
    /// and re-checked the objects its row names as damaged, and is held failed
    /// only because some of them could not be replaced (FR-VER-007, FR-VER-005).
    /// </summary>
    /// <remarks>
    /// Unlike a damage finding through <see cref="RecordFailure"/>, this
    /// narrows a failure of another kind that stood before it: the attempt
    /// has just shown the rest of the copy answers. It is still a failure for
    /// the back-off, so the next attempt does not re-read the same blobs on
    /// the very next pass.
    /// </remarks>
    /// <param name="setId">The backup set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="error">What holds the pair, for status to repeat.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    public DestinationSyncRecord RecordHeldForDamage(
        string setId, string destination, string error, ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(error);

        return Mutate(setId, destination, previous => Seed(previous, setId, destination, nowUnixMilliseconds, DestinationSyncState.Behind) with
        {
            State = DestinationSyncState.Failed,
            LastAttemptAt = nowUnixMilliseconds,
            ConsecutiveFailures = (previous?.ConsecutiveFailures ?? 0) + 1,
            LastError = error,
            DamageOnly = true,
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
    /// <remarks>
    /// <paramref name="state"/> is stated by every caller rather than defaulted
    /// here, and that is the whole point of it. The blank used to be born
    /// <see cref="DestinationSyncState.InSync"/>, which was invisible only
    /// while every mutator immediately overwrote the state — and three of them
    /// do not. <see cref="RecordCompleteness"/> writes from the copy's
    /// <c>finally</c>, so on a pair's first copy it reaches the ledger before
    /// any success does, and the row it created announced that a destination
    /// holding nothing yet was in sync. A caller that is not recording an
    /// outcome passes <see cref="DestinationSyncState.Behind"/>: a pair with
    /// no success behind it is behind by definition, which is what the row
    /// meant before any of these writers existed.
    /// </remarks>
    private static DestinationSyncRecord Seed(
        DestinationSyncRecord? previous, string setId, string destination, ulong nowUnixMilliseconds,
        DestinationSyncState state) =>
        previous ?? new DestinationSyncRecord
        {
            SetId = setId,
            Destination = destination,
            State = state,
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
            _written?.Invoke(record);
            return record;
        }
    }

    private DestinationSyncRecord? FindLocked(string setId, string destination) =>
        _records.LastOrDefault(record =>
            string.Equals(record.SetId, setId, StringComparison.Ordinal)
            && string.Equals(record.Destination, destination, StringComparison.Ordinal));
}
