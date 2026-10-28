using Bodu;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Domain.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Restore;

/// <summary>
/// What a restore as a whole achieved (FR-RST-004; architecture 08 §3).
/// </summary>
/// <remarks>
/// A boolean cannot carry this. It was one, computed as "nothing failed",
/// which made a restore that skipped every symlink and special file report
/// itself complete — while architecture 08 §3 says a restore that recovered
/// 9 999 of 10 000 files is a failed restore that recovered 9 999 files.
/// </remarks>
public enum RestoreOutcome
{
    /// <summary>Every required item was restored and verified.</summary>
    Complete = 0,

    /// <summary>Nothing failed, but a required item was not materialised.</summary>
    Partial = 1,

    /// <summary>At least one required item could not be restored.</summary>
    Failed = 2,

    /// <summary>The operator stopped it.</summary>
    Cancelled = 3,
}

/// <summary>
/// Where restored historical content lands (FR-RST-006; architecture 08 §3.1).
/// </summary>
/// <remarks>
/// A snapshot may contain malware that was present when it was captured, and
/// restoring it is what the user asked for. Restoring it *into the live tree
/// by default* is not — that is a decision a person should make deliberately,
/// not one that happens because they pressed Enter.
/// </remarks>
public enum RestoreDestinationMode
{
    /// <summary>
    /// Under a quarantine directory inside the chosen output, so nothing
    /// unscanned lands where the system will run it. The default.
    /// </summary>
    Quarantine = 0,

    /// <summary>Directly into the chosen output — the operator asked for this explicitly.</summary>
    InPlace = 1,
}

/// <summary>What to do about a file already sitting at a restore destination.</summary>
/// <remarks>
/// <b>This is not architecture 08 §3.1's quarantine control</b>, and the two
/// were conflated. §3.1 is about where *restored historical content* lands —
/// unscanned content from an old snapshot should not be dropped into a live
/// tree by default. This is about what happens to a file that is already
/// there, which is a different question with a different answer.
/// </remarks>
public enum ExistingDestinationPolicy
{
    /// <summary>Move it aside into the displaced store, then restore. The default.</summary>
    Preserve = 0,

    /// <summary>Leave it, and record the item as failed.</summary>
    Fail = 1,

    /// <summary>Overwrite it. Destructive, and never the default.</summary>
    Replace = 2,

    /// <summary>
    /// Leave it, and write the restored copy beside it as
    /// <c>name (restored 2026-08-18).ext</c> (ADR-0041). The live file is
    /// untouched and nothing is displaced; the receipt item records where
    /// the copy landed.
    /// </summary>
    WriteBeside = 3,
}

/// <summary>One item's outcome in the receipt.</summary>
public sealed record ReceiptItem
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("bytes")]
    public required ulong Bytes { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>
    /// Where the content actually landed when it is not <see cref="Path"/> —
    /// the beside-suffixed name under
    /// <see cref="ExistingDestinationPolicy.WriteBeside"/>. Null when the
    /// item landed at its own path.
    /// </summary>
    [JsonPropertyName("written_as")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WrittenAs { get; init; }

    /// <summary>
    /// The copies other than the restore's own store that this item's
    /// content or manifest was read from (FR-RST-007), each because the
    /// copies tried before it would not serve, and each record verified as
    /// the own store's would have been. Null when the own store served all of
    /// it.
    /// </summary>
    [JsonPropertyName("read_from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ReadFrom { get; init; }

    /// <summary>
    /// The copies this item was read around because they held it damaged or
    /// would not read it, each with what was wrong. Null when every copy
    /// passed over simply did not hold it — a staging archive's trimmed
    /// history, a destination not yet caught up — which is nothing to warn
    /// about.
    /// </summary>
    [JsonPropertyName("read_around")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ReadAround { get; init; }

    /// <summary>
    /// The metadata captured with this item that the target did not get
    /// back, in the format's order: <c>modified_at</c>, <c>created_at</c>,
    /// <c>accessed_at</c>, <c>posix_mode</c>, <c>owner</c>, <c>group</c>,
    /// <c>security_descriptor</c>, <c>extended_attributes</c>,
    /// <c>alternate_streams</c>, <c>file_attributes</c> (FR-RST-004;
    /// ADR-0083). Null when nothing captured was left out. Recorded for each
    /// item the run landed. The outcome does not change for metadata alone,
    /// because the content landed and verified; the plan declared this
    /// beforehand, and this says where.
    /// </summary>
    [JsonPropertyName("not_applied")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NotApplied { get; init; }

    /// <summary>
    /// The item this one shares a file with: the first name of its hard-link
    /// group the run landed, which this name is now a further name for
    /// (FR-RST-004; ADR-0102). Null for a file that is one of no group, for a
    /// group's first name, and for a name restored as a file of its own,
    /// whose detail says why.
    /// </summary>
    [JsonPropertyName("linked_to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LinkedTo { get; init; }

    /// <summary>
    /// Whether the file was already at its destination holding the
    /// snapshot's content, its length and its whole-file hash both, when a
    /// resumed run reached it, and was left as it stood: not moved aside, not
    /// written again, and nothing of its content read (FR-RST-008;
    /// ADR-0109). Its captured metadata was applied again. Written only when
    /// true.
    /// </summary>
    [JsonPropertyName("found_in_place")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FoundInPlace { get; init; }

    /// <summary>
    /// Where a resumed run resumed the file: how many of its bytes it took up
    /// from the part an earlier run had assembled, each segment proved, and
    /// did not read again (FR-RST-008; ADR-0129, ADR-0130). Written only when
    /// some were.
    /// </summary>
    [JsonPropertyName("resumed_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ulong ResumedAt { get; init; }
}

/// <summary>
/// The machine-readable restore receipt (FR-RST-004/005; ADR-0026
/// §Decision 10): a versioned client-domain JSON document accounting for
/// <b>every planned item</b> — restored, skipped, or failed, with the
/// quarantine ledger — so "the restore succeeded" is a checkable claim,
/// never an impression.
/// </summary>
public sealed record RestoreReceipt
{
    /// <summary>The current receipt schema version.</summary>
    /// <remarks>
    /// Version 2 replaced the <c>complete</c> boolean with <c>outcome</c>.
    /// The boolean is not carried alongside it: a reader that understood the
    /// old field would have read <c>true</c> for a partial restore, which is
    /// the defect, so leaving it present would preserve the lie for exactly
    /// the readers most likely to trust it. Version 3 added the
    /// <c>degraded</c> item outcome (RR-6): a file whose alternate data
    /// streams were captured and not written back is not <c>restored</c>,
    /// and a run containing one is not <c>Complete</c>. Version 4 added the
    /// optional per-item <c>written_as</c> (ADR-0041): under the write-beside
    /// policy the content lands at a suffixed name, and a receipt that could
    /// not say where would fail the only question it exists to answer.
    /// Version 5 added the optional per-item <c>read_from</c> and
    /// <c>read_around</c> (FR-RST-007): a file read from another copy,
    /// because the copy it was first read from would not serve, says where its
    /// bytes came from and what was wrong with the copies passed over.
    /// Version 6 added the optional per-item <c>not_applied</c> (FR-RST-004,
    /// ADR-0083): the captured metadata the target did not get back, which
    /// architecture 06 §3 always said the receipt records and which it had
    /// never once written. Version 7 added the optional per-item
    /// <c>linked_to</c> (ADR-0102): a hard-link group's later names are links
    /// to the file its first name landed as, and say which. Version 8 added
    /// the optional per-item <c>found_in_place</c> (ADR-0109): a resumed run
    /// leaves a file an earlier run landed as it stands, and a receipt that
    /// said it restored the file would not say this run never wrote it.
    /// Version 9 added the optional <c>set</c> (ADR-0126): a receipt read
    /// back long after its run said which snapshot it restored, and only a
    /// hex identifier said whose. Version 10 added the optional
    /// <c>set_id</c> (ADR-0127): a set's name can pass to another set, and
    /// its files are shown only to the passphrase of the set they are.
    /// Version 11 added the optional per-item <c>resumed_at</c> (ADR-0130):
    /// a file a resume took up from its part was written by this run, but not
    /// all of it was read again, and a receipt that said nothing of it would
    /// not say where the run picked it up.
    /// </remarks>
    public const int CurrentSchemaVersion = 11;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("snapshot_id")]
    public required string SnapshotId { get; init; }

    [JsonPropertyName("started_at")]
    public required ulong StartedAt { get; init; }

    [JsonPropertyName("completed_at")]
    public required ulong CompletedAt { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<ReceiptItem> Items { get; init; }

    /// <summary>Paths whose existing file was moved aside to make room.</summary>
    [JsonPropertyName("displaced")]
    public required IReadOnlyList<string> Displaced { get; init; }

    /// <summary>
    /// Where content was actually written — the quarantine directory, or the
    /// operator's chosen output when they asked for in place.
    /// </summary>
    /// <remarks>
    /// Recorded because "restored to /home/ana/recovered" and "restored to
    /// /home/ana/recovered/.fbp-quarantine/&lt;run&gt;" are different claims,
    /// and a receipt that cannot tell them apart cannot answer the only
    /// question that matters after a restore: where are my files.
    /// </remarks>
    [JsonPropertyName("written_to")]
    public required string WrittenTo { get; init; }

    /// <summary>What the restore as a whole achieved.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<RestoreOutcome>))]
    [JsonPropertyName("outcome")]
    public required RestoreOutcome Outcome { get; init; }

    /// <summary>
    /// The name of the set whose snapshot the run restored, as the run knew
    /// it (ADR-0126). Null where it knew none, and in a receipt kept before
    /// schema 9.
    /// </summary>
    [JsonPropertyName("set")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Set { get; init; }

    /// <summary>
    /// The identity of the set <see cref="Set"/> names, as 32 hex digits
    /// (ADR-0127), which a rename does not change. Null where the run knew no
    /// set, and in a receipt kept before schema 10.
    /// </summary>
    [JsonPropertyName("set_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SetId { get; init; }

    /// <summary>The receipt as indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Keeps the receipt as its run's own file, <c>{runId}.json</c>, under
    /// <paramref name="receiptsDirectory"/> (FR-RST-004, ADR-0122): written
    /// whole beside its place and moved there, so a reader never meets half
    /// of one, and a receipt kept again under its run replaces the last.
    /// </summary>
    /// <param name="receiptsDirectory">Where a state directory keeps restore receipts.</param>
    /// <param name="runId">The run's sixteen lowercase hex digits.</param>
    /// <returns>Where the receipt was kept.</returns>
    public string Keep(string receiptsDirectory, string runId)
    {
        Directory.CreateDirectory(receiptsDirectory);
        var path = Path.Combine(receiptsDirectory, $"{runId}.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, ToJson());
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Whether <paramref name="name"/> is a run's: sixteen lowercase hex
    /// digits, as every run is named and its receipt kept.
    /// </summary>
    /// <param name="name">What names the run.</param>
    /// <returns>Whether it does.</returns>
    public static bool IsRunName(string name)
    {
        ThrowHelper.ThrowIfNull(name);
        return name.Length == 16
            && name.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
    }

    /// <summary>
    /// The receipts kept under <paramref name="receiptsDirectory"/>, newest
    /// first by when each run ended (FR-RST-004, ADR-0124), each read as it
    /// was kept.
    /// </summary>
    /// <param name="receiptsDirectory">Where a state directory keeps restore receipts.</param>
    /// <returns>Each run's kept receipt, or why its file does not read as one.</returns>
    /// <remarks>
    /// Only a run's own file is read, named by the run and <c>.json</c>: a
    /// receipt half-written beside its place, the peers' receipts in the
    /// folders beside, and anything else left there are passed over. A run's
    /// file that does not read as a receipt is answered with why, placed by
    /// when it was written, so that it neither hides the rest nor is lost
    /// among them.
    /// </remarks>
    public static IReadOnlyList<KeptRestoreReceipt> ReadKept(string receiptsDirectory)
    {
        ThrowHelper.ThrowIfNull(receiptsDirectory);
        if (!Directory.Exists(receiptsDirectory))
        {
            return [];
        }

        var kept = new List<(ulong EndedAt, KeptRestoreReceipt Kept)>();
        foreach (var path in Directory.EnumerateFiles(receiptsDirectory))
        {
            var run = Path.GetFileNameWithoutExtension(path);
            if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.Ordinal) || !IsRunName(run))
            {
                continue;
            }

            var entry = Read(path, run);
            var endedAt = entry.Receipt?.CompletedAt
                ?? (ulong)Math.Max(0L, new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds());
            kept.Add((endedAt, entry));
        }

        return
        [
            .. kept
                .OrderByDescending(entry => entry.EndedAt)
                .ThenBy(entry => entry.Kept.Run, StringComparer.Ordinal)
                .Select(entry => entry.Kept),
        ];
    }

    /// <summary>
    /// The receipt <paramref name="run"/> kept under
    /// <paramref name="receiptsDirectory"/>, read alone (ADR-0127), or null
    /// where it kept none there.
    /// </summary>
    /// <param name="receiptsDirectory">Where a state directory keeps restore receipts.</param>
    /// <param name="run">The run.</param>
    /// <returns>
    /// The run's kept receipt, or why its file does not read as one; null where
    /// there is no file, and where <paramref name="run"/> is not a run's name,
    /// which is never joined to a path.
    /// </returns>
    public static KeptRestoreReceipt? ReadKept(string receiptsDirectory, string run)
    {
        ThrowHelper.ThrowIfNull(receiptsDirectory);
        if (!IsRunName(run))
        {
            return null;
        }

        var path = Path.Combine(receiptsDirectory, $"{run}.json");
        return File.Exists(path) ? Read(path, run) : null;
    }

    /// <summary>
    /// Lets the receipt <paramref name="run"/> kept under
    /// <paramref name="receiptsDirectory"/> go (ADR-0139): its file is
    /// removed, and every other run's stays.
    /// </summary>
    /// <param name="receiptsDirectory">Where a state directory keeps restore receipts.</param>
    /// <param name="run">The run.</param>
    /// <returns>
    /// Whether it kept one there to let go; never where <paramref name="run"/>
    /// is not a run's name, which is never joined to a path.
    /// </returns>
    public static bool LetGo(string receiptsDirectory, string run)
    {
        ThrowHelper.ThrowIfNull(receiptsDirectory);
        if (!IsRunName(run))
        {
            return false;
        }

        var path = Path.Combine(receiptsDirectory, $"{run}.json");
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private static KeptRestoreReceipt Read(string path, string run)
    {
        RestoreReceipt? receipt = null;
        string? problem;
        try
        {
            receipt = JsonSerializer.Deserialize<RestoreReceipt>(File.ReadAllText(path));
            problem = receipt is null ? "the file holds no receipt" : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = exception.Message;
        }

        return new KeptRestoreReceipt(run, path, receipt, problem);
    }
}

/// <summary>
/// A restore receipt as a state directory keeps it (FR-RST-004, ADR-0124):
/// under its run's name, read back as it was kept.
/// </summary>
/// <param name="Run">The run it is kept under.</param>
/// <param name="Path">The file it is kept in.</param>
/// <param name="Receipt">The receipt, or null where the file does not read as one.</param>
/// <param name="Problem">Why the file does not read as a receipt, or null where it does.</param>
public sealed record KeptRestoreReceipt(string Run, string Path, RestoreReceipt? Receipt, string? Problem);

/// <summary>Executor switches.</summary>
public sealed record RestoreExecutionOptions
{
    /// <summary>Where restored content lands (FR-RST-006).</summary>
    /// <remarks>
    /// Defaults to <see cref="RestoreDestinationMode.Quarantine"/>. This is a
    /// different control from <see cref="ExistingDestination"/> and the two
    /// were once conflated: this one is about where the restored file goes,
    /// that one is about what happens to a file already there.
    /// </remarks>
    public RestoreDestinationMode DestinationMode { get; init; } = RestoreDestinationMode.Quarantine;

    /// <summary>The quarantine directory every run lands under unless told otherwise.</summary>
    public const string DefaultQuarantineDirectory = ".fbp-quarantine";

    /// <summary>The quarantine directory, relative to the chosen output.</summary>
    public string QuarantineDirectory { get; init; } = DefaultQuarantineDirectory;

    /// <summary>What to do about a file already at a destination.</summary>
    /// <remarks>
    /// Defaults to <see cref="ExistingDestinationPolicy.Preserve"/>: restore
    /// never destroys what it found.
    /// </remarks>
    public ExistingDestinationPolicy ExistingDestination { get; init; } = ExistingDestinationPolicy.Preserve;

    /// <summary>
    /// The directory displaced files are moved into, relative to the restore
    /// root. Each run gets its own subdirectory, so two restores of the same
    /// path cannot overwrite one another's displaced copies.
    /// </summary>
    public string DisplacedDirectory { get; init; } = ".fbp-displaced";

    /// <summary>
    /// Distinguishes this run's displaced store from every other run's.
    /// </summary>
    /// <remarks>
    /// A single shared refuge with <c>overwrite: true</c> was worse than no
    /// refuge: restoring the same path twice destroyed the first displaced
    /// copy silently, which is precisely the data the policy exists to keep.
    /// </remarks>
    public required string RunId { get; init; }

    /// <summary>
    /// Whether this run resumes one cut short (FR-RST-008; ADR-0109): a file
    /// already at its destination holding the snapshot's content, its length
    /// and its whole-file hash both, is left as it stands, and its metadata
    /// applied again. Anything else lands as any run lands it.
    /// </summary>
    /// <remarks>
    /// Off by default: without being told, a run cannot know that a file
    /// like the snapshot's is its own rather than somebody's.
    /// </remarks>
    public bool Resume { get; init; }

    /// <summary>
    /// The earlier run whose quarantine folder this one lands in, when it
    /// resumes a quarantined run; null lands under its own. The displaced
    /// store stays this run's own, so nothing either run moved aside is in
    /// the other's way.
    /// </summary>
    public string? ResumedRunId { get; init; }

    /// <summary>
    /// Where the run assembles each file it writes, as a part named by the
    /// file's whole-file hash, and keeps one it was stopped in, or could not
    /// read all of, for a resume to take up (ADR-0129); null keeps none, and
    /// a file cut short is restored whole again.
    /// </summary>
    /// <remarks>
    /// A part is taken up only by a resume, as a file in place is: without
    /// being told, a run cannot know the part is its own.
    /// </remarks>
    public string? PartsDirectory { get; init; }

    /// <summary>The wall clock for receipt timestamps.</summary>
    public required ulong NowUnixMilliseconds { get; init; }
}

/// <summary>
/// Executes a restore plan: content first through the verifying engine
/// (per-segment content identifiers plus the whole-file hash before any
/// file reaches its destination), metadata strictly after content, and a
/// receipt that accounts for everything (FR-RST-004/005).
/// </summary>
public sealed class RestoreExecutor(
    RepositoryReader reader, RestoreTargetProfile target, ILogger? logger = null, Func<string, string, bool>? makeLink = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// How a further name is made for a file the run landed: the platform's
    /// call (<see cref="HardLinks.TryCreate"/>) unless the caller says how,
    /// as a test of a target that refuses one does.
    /// </summary>
    private readonly Func<string, string, bool> _makeLink = makeLink ?? HardLinks.TryCreate;

    /// <summary>The owners' names this run has resolved, so a tree of one owner asks the system once.</summary>
    private readonly Dictionary<string, uint?> _owners = new(StringComparer.Ordinal);

    /// <summary>The groups' names this run has resolved.</summary>
    private readonly Dictionary<string, uint?> _groups = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads ahead of the restore loop so a blob costs one GET rather than
    /// one a file (NFR-PERF-009). A wave prefetches the manifests of a
    /// bounded run of upcoming items, reads them, and prefetches every
    /// segment they name — so the reader is asked for everything one blob
    /// owes this stretch of the restore before it is asked for any of it,
    /// and can coalesce accordingly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounded twice, because a plan is not: by items, so a wave never holds
    /// more manifests than a machine should; and by the bytes the reader
    /// reports fetching, at half its own budget, so the wave's own runs are
    /// never evicted by its own later ones before the loop consumes them.
    /// </para>
    /// <para>
    /// Each manifest is read once. The wave keeps the record it read and the
    /// loop takes it, so looking ahead costs a decode rather than a second
    /// decrypt — and a read the wave could not make is simply not cached,
    /// leaving the loop to make it and to report what it finds, which is
    /// where the receipt's account of a failure belongs. A folder's manifest
    /// is its tree, read for the folder's own metadata (ADR-0086); it sits in
    /// the metadata blobs beside the files' and names no segments.
    /// </para>
    /// <para>
    /// A file a resumed run will leave as it stands needs none of its
    /// content, so the wave asks <c>landed</c> where it stands and prefetches
    /// none of it when it stands somewhere.
    /// </para>
    /// </remarks>
    private sealed class PrefetchWave(
        RepositoryReader reader,
        RestorePlan plan,
        Func<int, FileVersionManifest, CancellationToken, ValueTask<string?>> landed,
        Func<FileVersionManifest, bool> takesUpAPart)
    {
        private const int MaximumItems = 1024;
        private const int MaximumSegments = 512;

        private readonly Dictionary<ObjectId, RecordReadResult> _read = [];
        private int _through;

        /// <summary>
        /// The manifest record for <paramref name="objectId"/>, from the wave
        /// when it has it and from the reader when it does not.
        /// </summary>
        public async ValueTask<RecordReadResult> ReadManifestAsync(
            int index, ObjectId objectId, CancellationToken cancellationToken)
        {
            // Nothing to coalesce without a location source, so nothing to
            // read ahead for: the footer path reads each manifest at its own
            // turn, exactly as it did.
            if (reader.ReadsFromLocations && index >= _through)
            {
                await FillAsync(index, cancellationToken).ConfigureAwait(false);
            }

            if (_read.Remove(objectId, out var cached))
            {
                return cached;
            }

            return await reader.ReadSegmentAsync(objectId, cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask FillAsync(int from, CancellationToken cancellationToken)
        {
            _read.Clear();

            var last = Math.Min(plan.Items.Count, from + MaximumItems);
            var manifests = plan.Items
                .Take(last)
                .Skip(from)
                .Select(item => item.ObjectId)
                .ToList();

            _through = from + 1;
            if (manifests.Count == 0)
            {
                _through = last;
                return;
            }

            try
            {
                await reader.PrefetchAsync(manifests, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A prefetch is never the thing that fails a restore: the
                // loop reads what it needs and reports what it finds.
                return;
            }

            // Asked for together, not file by file: a blob holds the segments
            // of several consecutive files, and one prefetch a file would
            // fetch its own stretch of that blob each time.
            var ceiling = reader.Prefetch.BudgetBytes / 2;
            var pending = new List<ObjectId>();
            var held = 0L;

            for (var index = from; index < last && held < ceiling; index++)
            {
                var item = plan.Items[index];
                RecordReadResult read;
                try
                {
                    read = await reader.ReadSegmentAsync(item.ObjectId, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    break;
                }

                _read[item.ObjectId] = read;
                _through = index + 1;

                if (item.Kind == EntryKind.DirectoryPlaceholder || read.Outcome != RecordReadOutcome.Ok)
                {
                    continue;
                }

                FileVersionManifest manifest;
                try
                {
                    manifest = FileVersionManifestCodec.Decode(read.Plaintext!);
                }
                catch (Exception decode) when (decode is FormatException or ManifestValidationException)
                {
                    // The loop decodes the same bytes and answers for it.
                    continue;
                }

                // A file a part holds some of is read for what it lacks once
                // the part is taken up, and not ahead of it.
                if (await landed(index, manifest, cancellationToken).ConfigureAwait(false) is not null
                    || takesUpAPart(manifest))
                {
                    continue;
                }

                pending.AddRange(manifest.SegmentReferences.Select(reference => reference.ObjectId));

                // Flushed by count, because the bytes are only known once
                // they are fetched and a wave must not ask for more than the
                // reader can hold. At the segment size the product targets
                // this is about half its budget.
                if (pending.Count >= MaximumSegments)
                {
                    held += await FlushAsync(pending, cancellationToken).ConfigureAwait(false);
                }
            }

            await FlushAsync(pending, cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask<long> FlushAsync(List<ObjectId> pending, CancellationToken cancellationToken)
        {
            if (pending.Count == 0)
            {
                return 0;
            }

            try
            {
                return await reader.PrefetchAsync(pending, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A prefetch is never the thing that fails a restore.
                return 0;
            }
            finally
            {
                pending.Clear();
            }
        }
    }

    /// <summary>Runs <paramref name="plan"/> into <paramref name="outputDirectory"/>.</summary>
    public async ValueTask<RestoreReceipt> ExecuteAsync(
        RestorePlan plan,
        string outputDirectory,
        RestoreExecutionOptions options,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(plan);
        ThrowHelper.ThrowIfNullOrWhiteSpace(outputDirectory);
        ThrowHelper.ThrowIfNull(options);

        using var activity = FallbackPlan.Domain.Diagnostics.EngineDiagnostics.Activities.StartActivity("restore");
        Directory.CreateDirectory(outputDirectory);

        // Resolved once, and every destination is checked against it. A
        // repository is not a trusted source of path text: the threat model
        // treats other participants and historical data as adversarial, so a
        // manifest naming "../../.ssh/authorized_keys" must be refused by
        // construction rather than by whoever wrote the store.
        var chosen = new DirectoryInfo(outputDirectory).FullName;

        // Quarantine by default (08 §3.1): restored historical content lands
        // under a directory of its own rather than directly where the operator
        // pointed, unless they said in place. Displaced files stay beside the
        // chosen output, not inside the quarantine — they are the user's
        // current files and were never in question. A resumed quarantined
        // run lands in the folder of the run it continues (ADR-0109).
        var root = options.DestinationMode == RestoreDestinationMode.Quarantine
            ? Path.Combine(chosen, options.QuarantineDirectory, options.ResumedRunId ?? options.RunId)
            : chosen;

        Directory.CreateDirectory(root);
        var displacedRoot = Path.Combine(chosen, options.DisplacedDirectory, options.RunId);
        var engine = new RestoreEngine(reader);
        var items = new List<ReceiptItem>();
        var folders = new List<Folder>();
        var displaced = new List<string>();

        // The first name of each hard-link group the run lands, by group, so
        // a later name can be made that file again (ADR-0102).
        var groups = new Dictionary<string, LandedName>(StringComparer.Ordinal);

        var existingPolicy = options.ExistingDestination.ToString();
        Log.RestoreStarting(_logger, plan.Items.Count, new LogPath(root), new LogLabel(existingPolicy));

        // Reads ahead of the loop so that a blob is fetched once rather than
        // once a file (NFR-PERF-009). Prefetching only the file being
        // restored is not enough: consecutive files share the blob they were
        // written into, so each would fetch the stretch after the last one's
        // — one read per (file, blob) pair, which is one read a file and not
        // one a blob. The wave prefetches a bounded run of upcoming items'
        // manifests and their segments together, so everything one blob owes
        // this restore is asked for at once.
        var landedAt = new Dictionary<int, string?>();
        var listed = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (options.PartsDirectory is { } parts)
        {
            Directory.CreateDirectory(parts);
        }

        var wave = new PrefetchWave(reader, plan, LandedAsync, TakesUpAPart);

        bool TakesUpAPart(FileVersionManifest manifest) =>
            options.Resume && PartOf(manifest) is { } part && File.Exists(part);

        string? PartOf(FileVersionManifest manifest) => options.PartsDirectory is { } directory
            ? Path.Combine(directory, RestorePart.NameOf(manifest))
            : null;

        // Where a resumed run finds each file already landed, decided once:
        // ahead of the loop, so none of its content is read, and in it. That
        // is where this run would land it: its destination, or, under rename
        // with a file in the way, beside it (ADR-0109 Amendment 1).
        async ValueTask<string?> LandedAsync(int at, FileVersionManifest manifest, CancellationToken token)
        {
            if (!options.Resume || manifest.EntryKind != EntryKind.File)
            {
                return null;
            }

            if (landedAt.TryGetValue(at, out var known))
            {
                return known;
            }

            string? found = null;
            if (TryResolve(root, plan.Items[at].Path, out var where, out _))
            {
                if (await HoldsAsync(where, manifest, token).ConfigureAwait(false))
                {
                    found = where;
                }
                else if (options.ExistingDestination == ExistingDestinationPolicy.WriteBeside && File.Exists(where))
                {
                    foreach (var copy in BesideCopies(where, listed))
                    {
                        if (await HoldsAsync(copy, manifest, token).ConfigureAwait(false))
                        {
                            found = copy;
                            break;
                        }
                    }
                }
            }

            landedAt[at] = found;
            return found;
        }

        for (var index = 0; index < plan.Items.Count; index++)
        {
            var item = plan.Items[index];
            // Cooperative stop, not an exception: break so the receipt is still
            // produced and Aggregate can see fewer items than planned and
            // report Cancelled (architecture 08 §3's outcome, previously
            // unreachable because this threw before the receipt was built).
            // Stopping between items means every item already in the receipt is
            // whole — a cancelled restore leaves the same class of state a
            // completed prefix would.
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!TryResolve(root, item.Path, out var destination, out var refusal))
            {
                items.Add(new ReceiptItem
                {
                    Path = item.Path, Outcome = "failed", Bytes = 0, Detail = refusal,
                });
                continue;
            }

            if (item.Kind == EntryKind.DirectoryPlaceholder)
            {
                // Read before the folder is made, so a run stopped mid-read
                // leaves nothing the receipt does not account for.
                EntryMetadata? captured;
                string? unread;
                try
                {
                    (captured, unread) = OwnMetadata(
                        await wave.ReadManifestAsync(index, item.ObjectId, cancellationToken).ConfigureAwait(false));
                }
                catch (IOException exception)
                {
                    (captured, unread) = (null, exception.Message);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Whatever already stands here keeps its own metadata: no
                // existing-file policy reaches a folder (ADR-0086).
                var found = Directory.Exists(destination);
                Directory.CreateDirectory(destination);
                items.Add(new ReceiptItem { Path = item.Path, Outcome = "restored", Bytes = 0 });
                folders.Add(new Folder(
                    items.Count - 1, item, destination, found,
                    captured is null ? CapturedMetadata.None : RestoreMetadata.Captured(captured),
                    captured is null ? null : captured with { WindowsSecurityDescriptor = null, AlternateStreams = [] },
                    unread));
                continue;
            }

            RecordReadResult read;
            try
            {
                read = await wave.ReadManifestAsync(index, item.ObjectId, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                // A store fault is contained to the item it hit (architecture
                // 08 §3): the receipt accounts for it as failed and the run
                // continues, exactly as an unreadable record does. Letting it
                // propagate aborted the whole run with no receipt at all.
                items.Add(new ReceiptItem
                {
                    Path = item.Path, Outcome = "failed", Bytes = 0, Detail = exception.Message,
                });
                continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The token fired inside this item's read rather than between
                // items. The posture is the same as the between-items check
                // above: stop, and still produce the receipt — a cancelled
                // restore that throws instead loses the account of what DID
                // land, which is the receipt's whole reason to exist.
                break;
            }

            if (read.Outcome != RecordReadOutcome.Ok)
            {
                items.Add(new ReceiptItem
                {
                    Path = item.Path, Outcome = "failed", Bytes = 0,
                    Detail = $"manifest read {read.Outcome}",
                });
                continue;
            }

            var manifest = FileVersionManifestCodec.Decode(read.Plaintext!);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            switch (manifest.EntryKind)
            {
                case EntryKind.File:
                {
                    // A later name of a group the run has landed is a link to
                    // that file, where it agrees with it and the target makes
                    // one; otherwise it is restored as a file of its own, and
                    // says why.
                    var group = RestoreHardLinks.GroupOf(manifest);
                    string? notLinked = null;
                    if (group is not null && groups.TryGetValue(group, out var first))
                    {
                        if (!string.Equals(first.Key, RestoreHardLinks.KeyOf(manifest), StringComparison.Ordinal))
                        {
                            notLinked = $"not linked to {first.Path}: its content or attributes differ from that name's, so it was restored as a file of its own";
                        }
                        else if (Link(item, destination, first, displacedRoot, displaced, options) is { } linked)
                        {
                            items.Add(linked);
                            continue;
                        }
                        else
                        {
                            notLinked = $"not linked to {first.Path}: the target would not make a hard link here, so it was restored as a file of its own";
                        }
                    }

                    // A resumed run leaves what an earlier one landed as it
                    // stands, whatever the policy says: the policy is about
                    // somebody else's file, and this one holds exactly what
                    // the run would write (ADR-0109).
                    string? inPlace;
                    try
                    {
                        inPlace = await LandedAsync(index, manifest, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (inPlace is { } standing)
                    {
                        var (kept, keptNotApplied, keptDegraded) = Settle(
                            item, manifest, standing, manifest.LogicalLength,
                            writtenAs: string.Equals(standing, destination, StringComparison.Ordinal) ? null : BesidePath(item.Path, standing),
                            notLinked, foundInPlace: true);
                        if (!keptDegraded && group is not null && notLinked is null)
                        {
                            groups.TryAdd(group, new LandedName(RestoreHardLinks.KeyOf(manifest)!, item.Path, standing, keptNotApplied));
                        }

                        items.Add(kept);
                        break;
                    }

                    var spool = destination + ".fbp-restore-tmp";
                    RestoreResult result;
                    try
                    {
                        var output = File.Create(spool);
                        await using (output.ConfigureAwait(false))
                        {
                            result = PartOf(manifest) is { } part
                                ? await engine.RestoreFileAsync(manifest, output, part, options.Resume, cancellationToken).ConfigureAwait(false)
                                : await engine.RestoreFileAsync(manifest, output, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (IOException exception)
                    {
                        // Same containment as the manifest read above: the
                        // fault fails this item, the spool never becomes the
                        // destination, and the run carries on.
                        if (File.Exists(spool))
                        {
                            File.Delete(spool);
                        }

                        items.Add(new ReceiptItem
                        {
                            Path = item.Path, Outcome = "failed", Bytes = 0, Detail = exception.Message,
                        });
                        continue;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Mid-item cancellation, same posture as the manifest
                        // read above: the spool never becomes the destination
                        // and the receipt still accounts for what landed. The
                        // break leaves the switch; the check at the top of the
                        // loop ends the run.
                        if (File.Exists(spool))
                        {
                            File.Delete(spool);
                        }

                        break;
                    }

                    if (!result.Success)
                    {
                        File.Delete(spool);
                        items.Add(new ReceiptItem
                        {
                            Path = item.Path, Outcome = "failed", Bytes = 0, Detail = result.FailureDetail,
                        });
                        continue;
                    }

                    // A directory at the destination is no policy's to
                    // resolve: Preserve would displace a subtree it never
                    // planned, Replace would delete one. File.Exists is false
                    // for a directory, so without this check the policies are
                    // silently skipped and the final move throws — aborting
                    // the run instead of failing the item.
                    if (Directory.Exists(destination))
                    {
                        File.Delete(spool);
                        items.Add(new ReceiptItem
                        {
                            Path = item.Path, Outcome = "failed", Bytes = 0,
                            Detail = "a directory occupies this destination; no existing-file policy applies to a directory",
                        });
                        continue;
                    }

                    var landing = destination;
                    string? writtenAs = null;
                    if (File.Exists(destination))
                    {
                        if (options.ExistingDestination == ExistingDestinationPolicy.Fail)
                        {
                            File.Delete(spool);
                            items.Add(new ReceiptItem
                            {
                                Path = item.Path, Outcome = "failed", Bytes = 0,
                                Detail = "a file is already at this destination and the policy is to fail",
                            });
                            continue;
                        }

                        if (options.ExistingDestination == ExistingDestinationPolicy.Preserve)
                        {
                            // Moved aside before the verified version lands, into
                            // this run's own store. overwrite is false on purpose:
                            // if something is already there the displaced copy is
                            // not ours to destroy.
                            if (!TryResolve(displacedRoot, item.Path, out var refuge, out var displacedRefusal))
                            {
                                File.Delete(spool);
                                items.Add(new ReceiptItem
                                {
                                    Path = item.Path, Outcome = "failed", Bytes = 0, Detail = displacedRefusal,
                                });
                                continue;
                            }

                            Directory.CreateDirectory(Path.GetDirectoryName(refuge)!);
                            File.Move(destination, refuge);
                            displaced.Add(item.Path);
                            Log.DisplacedExisting(_logger, new LogPath(destination), new LogPath(refuge));
                        }

                        if (options.ExistingDestination == ExistingDestinationPolicy.WriteBeside)
                        {
                            // The live file is kept and the restored copy lands
                            // beside it under a dated name (ADR-0041). Built as
                            // the resolved destination's directory plus a new
                            // final component, so containment holds by
                            // construction.
                            if (!TryBesideName(destination, options.NowUnixMilliseconds, out landing, out var besideRefusal))
                            {
                                File.Delete(spool);
                                items.Add(new ReceiptItem
                                {
                                    Path = item.Path, Outcome = "failed", Bytes = 0, Detail = besideRefusal,
                                });
                                continue;
                            }

                            writtenAs = BesidePath(item.Path, landing);
                            Log.WroteBeside(_logger, new LogPath(destination), new LogPath(landing));
                        }
                    }

                    try
                    {
                        File.Move(spool, landing, overwrite: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // The landing itself can fault — something raced into
                        // the checked path, or the platform refused the write.
                        // Contained like every other per-item fault.
                        if (File.Exists(spool))
                        {
                            File.Delete(spool);
                        }

                        items.Add(new ReceiptItem
                        {
                            Path = item.Path, Outcome = "failed", Bytes = 0, Detail = exception.Message,
                        });
                        continue;
                    }

                    var (settled, notApplied, degraded) = Settle(
                        item, manifest, landing, (ulong)result.Length, writtenAs, notLinked, foundInPlace: false,
                        resumedAt: (ulong)result.Resumed);
                    if (!degraded && group is not null && notLinked is null)
                    {
                        groups.TryAdd(group, new LandedName(RestoreHardLinks.KeyOf(manifest)!, item.Path, landing, notApplied));
                    }

                    items.Add(settled);
                    break;
                }

                case EntryKind.Symlink when target.SupportsSymlinks && manifest.LinkTarget is { } linkTarget:
                {
                    // The existing-destination policy applies here exactly as
                    // it does to files — this case once deleted whatever stood
                    // at the destination unconditionally, which made Preserve
                    // a policy the run only mostly followed.
                    var targetText = System.Text.Encoding.UTF8.GetString(linkTarget.Span);
                    var linkLanding = destination;
                    string? linkWrittenAs = null;
                    try
                    {
                        if (File.Exists(destination) || Directory.Exists(destination))
                        {
                            switch (options.ExistingDestination)
                            {
                                case ExistingDestinationPolicy.Fail:
                                    items.Add(new ReceiptItem
                                    {
                                        Path = item.Path, Outcome = "failed", Bytes = 0,
                                        Detail = "something is already at this destination and the policy is to fail",
                                    });
                                    continue;

                                case ExistingDestinationPolicy.Preserve:
                                    if (!TryResolve(displacedRoot, item.Path, out var linkRefuge, out var linkRefusal))
                                    {
                                        items.Add(new ReceiptItem
                                        {
                                            Path = item.Path, Outcome = "failed", Bytes = 0, Detail = linkRefusal,
                                        });
                                        continue;
                                    }

                                    Directory.CreateDirectory(Path.GetDirectoryName(linkRefuge)!);
                                    File.Move(destination, linkRefuge);
                                    displaced.Add(item.Path);
                                    break;

                                case ExistingDestinationPolicy.WriteBeside:
                                    if (!TryBesideName(destination, options.NowUnixMilliseconds, out linkLanding, out var linkBesideRefusal))
                                    {
                                        items.Add(new ReceiptItem
                                        {
                                            Path = item.Path, Outcome = "failed", Bytes = 0, Detail = linkBesideRefusal,
                                        });
                                        continue;
                                    }

                                    linkWrittenAs = BesidePath(item.Path, linkLanding);
                                    break;

                                default: // Replace
                                    File.Delete(destination);
                                    break;
                            }
                        }

                        File.CreateSymbolicLink(linkLanding, targetText);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // A directory (or an undeletable entry) at a link's
                        // destination is contained to this item, like every
                        // other write fault.
                        items.Add(new ReceiptItem
                        {
                            Path = item.Path, Outcome = "failed", Bytes = 0, Detail = exception.Message,
                        });
                        continue;
                    }

                    // Onto the link the run just made, never through it.
                    var linkWritten = ApplyMetadata(linkLanding, manifest.Metadata, EntryKind.Symlink);
                    items.Add(ReadAroundOf(item.ObjectId, manifest, new ReceiptItem
                    {
                        Path = item.Path, Outcome = "restored", Bytes = 0, WrittenAs = linkWrittenAs,
                        Detail = linkWritten.Detail,
                        NotApplied = RestoreMetadata.NotApplied(RestoreMetadata.Captured(manifest.Metadata), linkWritten.Applied),
                    }));
                    break;
                }

                case EntryKind.Symlink:
                    items.Add(new ReceiptItem
                    {
                        Path = item.Path, Outcome = "skipped", Bytes = 0,
                        Detail = "symlinks are not supported on this target (declared in the plan)",
                    });
                    break;

                default:
                    items.Add(new ReceiptItem
                    {
                        Path = item.Path, Outcome = "skipped", Bytes = 0,
                        Detail = $"{manifest.EntryKind} is recorded but not materialised (declared in the plan)",
                    });
                    break;
            }
        }

        // A folder's own metadata goes on last, deepest folder first, once
        // nothing more will land in it (ADR-0086). Every item written into a
        // folder moves its modification time, and permissions that forbid
        // writing would refuse what was still to come, so it cannot go on as
        // the folder is made. A stopped run settles the folders it made too:
        // each is in the receipt, and its item says what it was given.
        foreach (var folder in folders.OrderByDescending(folder => Depth(folder.Item.Path)))
        {
            items[folder.Receipt] = ReadAroundOf([folder.Item.ObjectId], Settle(root, folder, items[folder.Receipt]));
        }

        var receipt = new RestoreReceipt
        {
            SchemaVersion = RestoreReceipt.CurrentSchemaVersion,
            SnapshotId = Convert.ToHexString(plan.SnapshotId.Span).ToLowerInvariant(),
            StartedAt = options.NowUnixMilliseconds,
            CompletedAt = options.NowUnixMilliseconds,
            Items = items,
            Displaced = displaced,
            WrittenTo = root,
            Outcome = Aggregate(items, plan.Items.Count),
        };

        // Per item, from the receipt rather than from twenty scattered call
        // sites: the receipt already records every item's outcome, and these
        // records exist to explain the reasoning behind it. Logging the two
        // from one place is what keeps them from ever disagreeing.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var item in items)
            {
                switch (item.Outcome)
                {
                    case "restored":
                        Log.ItemRestored(_logger, new LogPath(item.Path), item.Bytes);
                        break;
                    case "failed":
                        Log.ItemFailed(_logger, new LogPath(item.Path), item.Detail ?? "no reason recorded");
                        break;
                    default:
                        Log.ItemSkipped(_logger, new LogPath(item.Path), item.Detail ?? item.Outcome);
                        break;
                }
            }
        }

        // Guarded, not hoisted: counting three ways over every restored item is
        // real work, and CA1873 is right that an argument is evaluated whether
        // or not anybody is listening.
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var restored = items.Count(item => item.Outcome == "restored");
            var failed = items.Count(item => item.Outcome == "failed");
            var skipped = items.Count(item => item.Outcome is "skipped" or "degraded");
            Log.RestoreComplete(_logger, receipt.Outcome, restored, failed, skipped);
        }

        return receipt;
    }

    /// <summary>
    /// The receipt's item for a file whose verified content stands at
    /// <paramref name="landing"/>, its captured metadata written back first,
    /// and whether it is degraded, in which case it is no hard-link group's
    /// first name.
    /// </summary>
    private (ReceiptItem Item, IReadOnlyList<string>? NotApplied, bool Degraded) Settle(
        RestorePlanItem item,
        FileVersionManifest manifest,
        string landing,
        ulong length,
        string? writtenAs,
        string? notLinked,
        bool foundInPlace,
        ulong resumedAt = 0)
    {
        // Metadata strictly AFTER content (architecture 08 §3): a crash
        // between the two leaves verified content with default metadata,
        // which is recoverable, never the reverse. And outside the landing's
        // catch, because a write the platform refuses leaves that attribute
        // not applied; it does not undo content that landed and verified
        // (ADR-0084).
        var written = ApplyMetadata(landing, manifest.Metadata);
        var notApplied = RestoreMetadata.NotApplied(RestoreMetadata.Captured(manifest.Metadata), written.Applied);

        // The main stream restored and verified; alternate data streams the
        // manifest carries did not (RR-6's honesty half — write-back is not
        // implemented on any target). "degraded", not "restored": the file on
        // disk is not the file that was captured, and the receipt says so per
        // item, exactly where the shortfall is.
        if (manifest.Metadata.AlternateStreams.Count > 0 && !target.SupportsAlternateStreams)
        {
            return (ReadAroundOf(item.ObjectId, manifest, new ReceiptItem
            {
                Path = item.Path, Outcome = "degraded", Bytes = length, WrittenAs = writtenAs,
                Detail = Joined(
                    notLinked,
                    $"{manifest.Metadata.AlternateStreams.Count} alternate data stream(s) were captured "
                        + "and not written back on this target (declared in the plan)"
                        + (written.Detail is { } left ? $"; {left}" : string.Empty)),
                NotApplied = notApplied,
                FoundInPlace = foundInPlace,
                ResumedAt = resumedAt,
            }), notApplied, true);
        }

        return (ReadAroundOf(item.ObjectId, manifest, new ReceiptItem
        {
            Path = item.Path, Outcome = "restored", Bytes = length, WrittenAs = writtenAs,
            Detail = Joined(notLinked, written.Detail), NotApplied = notApplied,
            FoundInPlace = foundInPlace, ResumedAt = resumedAt,
        }), notApplied, false);
    }

    /// <summary>
    /// Whether <paramref name="destination"/> already holds the content
    /// <paramref name="manifest"/> names: a file of its own, not a link, of
    /// its length and hashing to its whole-file hash. Anything the platform
    /// will not let be read is not.
    /// </summary>
    private static async ValueTask<bool> HoldsAsync(
        string destination, FileVersionManifest manifest, CancellationToken cancellationToken)
    {
        var found = new FileInfo(destination);
        if (!found.Exists || found.LinkTarget is not null || (ulong)found.Length != manifest.LogicalLength)
        {
            return false;
        }

        try
        {
            var stream = new FileStream(
                destination, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                return hash.AsSpan().SequenceEqual(manifest.WholeFileHash.Span);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// <paramref name="landed"/>, saying which copies its manifest and
    /// content came from when some came from another than the restore's own
    /// store, and what was wrong with those it was read around (FR-RST-007).
    /// </summary>
    private ReceiptItem ReadAroundOf(ObjectId manifestId, FileVersionManifest manifest, ReceiptItem landed) =>
        ReadAroundOf(manifest.SegmentReferences.Select(reference => reference.ObjectId).Prepend(manifestId), landed);

    /// <summary>
    /// <paramref name="landed"/>, saying which copies the records
    /// <paramref name="objectIds"/> came from when some came from another
    /// than the restore's own store, and what was wrong with those passed
    /// over (FR-RST-007).
    /// </summary>
    private ReceiptItem ReadAroundOf(IEnumerable<ObjectId> objectIds, ReceiptItem landed)
    {
        var around = new List<RecordReadAround>();
        foreach (var objectId in objectIds)
        {
            if (reader.TryGetReadAround(objectId, out var readAround))
            {
                around.Add(readAround);
            }
        }

        if (around.Count == 0)
        {
            return landed;
        }

        // Damage and unreadable copies are what a person needs to hear of. A
        // copy that simply did not hold the blob — staging's trimmed history,
        // a destination not yet caught up — is where the bytes were never
        // going to come from.
        List<string> faults =
        [
            .. around
                .SelectMany(readAround => readAround.PassedOver)
                .Where(refusal => refusal.Fault is CopyFault.Damaged or CopyFault.Unreadable)
                .Select(refusal => $"{refusal.Source}: {refusal.Detail}")
                .Distinct(StringComparer.Ordinal),
        ];

        return landed with
        {
            ReadFrom = [.. around.Select(readAround => readAround.ReadFrom).Distinct(StringComparer.Ordinal)],
            ReadAround = faults.Count == 0 ? null : faults,
        };
    }

    /// <summary>
    /// A folder the run made or found, and what it is given once nothing more
    /// will land in it. Everything captured with it is kept as flags, for the
    /// receipt; of the values, only those the rule can apply, so a tree of
    /// many folders holds no descriptors or streams it will not write.
    /// </summary>
    /// <param name="Receipt">Where its item is in the receipt.</param>
    /// <param name="Item">The plan's item.</param>
    /// <param name="Destination">Where it was made.</param>
    /// <param name="Found">Whether a folder already stood there.</param>
    /// <param name="Captured">What was captured with it.</param>
    /// <param name="Values">The captured values the rule can apply, or null when its record would not read.</param>
    /// <param name="Unread">Why its record would not read.</param>
    private sealed record Folder(
        int Receipt,
        RestorePlanItem Item,
        string Destination,
        bool Found,
        CapturedMetadata Captured,
        EntryMetadata? Values,
        string? Unread);

    /// <summary>
    /// A folder's own captured metadata, from the head of its tree, or why
    /// it could not be had.
    /// </summary>
    private static (EntryMetadata? Captured, string? Unread) OwnMetadata(RecordReadResult read)
    {
        if (read.Outcome != RecordReadOutcome.Ok)
        {
            return (null, $"manifest read {read.Outcome}");
        }

        try
        {
            return (TreeManifestCodec.Decode(read.Plaintext!).Metadata ?? EntryMetadata.Empty, null);
        }
        catch (FormatException exception)
        {
            return (null, exception.Message);
        }
    }

    /// <summary>How many folders deep <paramref name="path"/> lies.</summary>
    private static int Depth(string path) => path.Count(character => character == '/');

    /// <summary>
    /// The receipt's item for <paramref name="folder"/>, once it has been
    /// given what it may be given (ADR-0086).
    /// </summary>
    /// <remarks>
    /// A folder whose record would not read is still made, because it holds
    /// what restored under it; what it carried is unknown, so the item says
    /// why rather than listing anything. A folder that was already there
    /// keeps its own metadata, and so does anything that has taken the place
    /// of the one the run made. Either way nothing captured was applied, and
    /// the item lists all of it.
    /// </remarks>
    private ReceiptItem Settle(string root, Folder folder, ReceiptItem made)
    {
        if (folder.Values is not { } values)
        {
            return made with
            {
                Detail = $"its own captured metadata could not be read ({folder.Unread}), so none of it was applied",
            };
        }

        var none = RestoreMetadata.NotApplied(folder.Captured, CapturedMetadata.None);
        if (folder.Found)
        {
            return made with { Detail = "a folder was already here, and keeps its own metadata", NotApplied = none };
        }

        if (!IsStillTheFolderMade(root, folder.Item.Path, folder.Destination))
        {
            return made with
            {
                Detail = "the folder was moved or replaced before its own metadata went on, so none of it did",
                NotApplied = none,
            };
        }

        var written = ApplyMetadata(folder.Destination, values, EntryKind.DirectoryPlaceholder);
        return made with
        {
            Detail = written.Detail, NotApplied = RestoreMetadata.NotApplied(folder.Captured, written.Applied),
        };
    }

    /// <summary>
    /// Whether what stands at <paramref name="destination"/> is still the
    /// folder this run made there for <paramref name="path"/>: reached
    /// without leaving the root through a link, a folder, and not itself a
    /// link.
    /// </summary>
    /// <remarks>
    /// A folder's metadata goes on at the end of the run, so an account that
    /// can write beside it has had the whole run to put something else in
    /// its place. Written through a link, the folder's permissions and times
    /// would land on whatever the link points at.
    /// </remarks>
    private static bool IsStillTheFolderMade(string root, string path, string destination) =>
        TryResolve(root, path, out var resolved, out _)
        && string.Equals(resolved, destination, StringComparison.Ordinal)
        && new DirectoryInfo(destination) is { Exists: true, LinkTarget: null };

    /// <summary>Reduces the item outcomes to what the restore as a whole achieved.</summary>
    /// <param name="items">Every item's outcome.</param>
    /// <param name="planned">How many items the plan held.</param>
    /// <returns>The aggregate.</returns>
    /// <remarks>
    /// A skipped or degraded item makes the restore
    /// <see cref="RestoreOutcome.Partial"/>. The plan declaring in advance
    /// that a symlink cannot be materialised on this target — or that a
    /// file's alternate streams cannot be written back — is a reason the
    /// shortfall is expected; it is not a reason to report that nothing is
    /// missing. What the operator needs to know is that the tree they
    /// restored is not the tree that was captured.
    /// </remarks>
    private static RestoreOutcome Aggregate(List<ReceiptItem> items, int planned)
    {
        if (items.Any(item => item.Outcome == "failed"))
        {
            return RestoreOutcome.Failed;
        }

        if (items.Count != planned)
        {
            return RestoreOutcome.Cancelled;
        }

        return items.Any(item => item.Outcome is "skipped" or "degraded")
            ? RestoreOutcome.Partial
            : RestoreOutcome.Complete;
    }

    /// <summary>
    /// The beside-name for a restored copy the write-beside policy keeps next
    /// to an existing file: <c>name (restored 2026-08-18).ext</c>, deduped
    /// with <c>-2</c>, <c>-3</c>… and capped rather than unbounded. Built
    /// from the already-resolved destination's directory plus one new final
    /// component — no repository text enters it, so containment holds by
    /// construction.
    /// </summary>
    private static bool TryBesideName(
        string destination, ulong nowUnixMilliseconds, out string landing, out string refusal)
    {
        landing = string.Empty;
        refusal = string.Empty;

        var directory = Path.GetDirectoryName(destination)!;
        var stem = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);
        var date = DateTimeOffset.FromUnixTimeMilliseconds((long)nowUnixMilliseconds)
            .UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        for (var attempt = 1; attempt <= 100; attempt++)
        {
            var suffix = attempt == 1 ? $" (restored {date})" : $" (restored {date}-{attempt})";
            var candidate = Path.Combine(directory, stem + suffix + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                landing = candidate;
                return true;
            }
        }

        refusal = "refused: a hundred beside-named copies already sit at this destination";
        return false;
    }

    /// <summary>
    /// The copies written beside <paramref name="destination"/> under the
    /// names <see cref="TryBesideName"/> gives, whatever day they were
    /// written, in name order. Its folder is listed once a run, in
    /// <paramref name="listed"/>; one that will not list holds none.
    /// </summary>
    private static IEnumerable<string> BesideCopies(string destination, Dictionary<string, string[]> listed)
    {
        var directory = Path.GetDirectoryName(destination)!;
        if (!listed.TryGetValue(directory, out var names))
        {
            try
            {
                names = [.. Directory.EnumerateFiles(directory).Select(path => Path.GetFileName(path))];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                names = [];
            }

            listed[directory] = names;
        }

        var prefix = Path.GetFileNameWithoutExtension(destination) + " (restored ";
        var suffix = ")" + Path.GetExtension(destination);
        return names
            .Where(name => name.Length > prefix.Length + suffix.Length
                && name.StartsWith(prefix, StringComparison.Ordinal)
                && name.EndsWith(suffix, StringComparison.Ordinal)
                && IsBesideMark(name[prefix.Length..^suffix.Length]))
            .Order(StringComparer.Ordinal)
            .Select(name => Path.Combine(directory, name));
    }

    /// <summary>
    /// Whether <paramref name="mark"/> is what <see cref="TryBesideName"/>
    /// writes after "restored ": a date, alone or with a counter from 2 to
    /// 100 written as it writes one.
    /// </summary>
    private static bool IsBesideMark(string mark)
    {
        if (mark.Length < 10 || !DateTime.TryParseExact(
            mark[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _))
        {
            return false;
        }

        if (mark.Length == 10)
        {
            return true;
        }

        return mark[10] == '-'
            && int.TryParse(mark[11..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var counter)
            && counter is >= 2 and <= 100
            && string.Equals(mark[11..], counter.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>The receipt's repository-relative rendering of a beside landing.</summary>
    private static string BesidePath(string itemPath, string landing)
    {
        var leaf = Path.GetFileName(landing);
        var separator = itemPath.LastIndexOf('/');
        return separator < 0 ? leaf : itemPath[..(separator + 1)] + leaf;
    }

    /// <summary>
    /// Resolves one repository path under <paramref name="root"/>, refusing
    /// anything that is not a sequence of plain name components.
    /// </summary>
    /// <param name="root">The already-resolved containment root.</param>
    /// <param name="path">The repository-supplied path. Untrusted.</param>
    /// <param name="destination">The resolved destination when this returns true.</param>
    /// <param name="refusal">Why it was refused, for the receipt.</param>
    /// <returns><see langword="true"/> when the path is safe to materialise.</returns>
    /// <remarks>
    /// Component validation is the boundary; the containment check underneath
    /// it is defence in depth, not the mechanism. A check that only compares
    /// the combined string against a prefix is defeated by anything the
    /// platform normalises differently from the checker — which is most of the
    /// interesting cases.
    /// </remarks>
    internal static bool TryResolve(
        string root, string path, out string destination, out string refusal)
    {
        destination = string.Empty;
        refusal = string.Empty;

        if (path.Length == 0)
        {
            refusal = "refused: the manifest names an empty path";
            return false;
        }

        var built = root;
        foreach (var component in path.Split('/'))
        {
            if (!IsPlainComponent(component, out var why))
            {
                refusal = $"refused: {why}";
                return false;
            }

            built = Path.Combine(built, component);
        }

        var resolved = Path.GetFullPath(built);
        var fence = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        if (!resolved.StartsWith(fence, StringComparison.Ordinal))
        {
            refusal = "refused: the path resolves outside the restore root";
            return false;
        }

        // GetFullPath is lexical — it normalises `..` and `.` but does not
        // follow symlinks. A link inside the root pointing out of it (seeded by
        // an attacker, or restored by an earlier item) would let a later path
        // traverse it and escape though every component reads as plain. Resolve
        // the deepest existing ancestor through its links and re-check: a real
        // path that leaves the root is refused before anything is written.
        if (EscapesThroughALink(root, resolved))
        {
            refusal = "refused: the path traverses a symlink that leaves the restore root";
            return false;
        }

        destination = resolved;
        return true;
    }

    private static bool EscapesThroughALink(string root, string resolved)
    {
        // Lexical containment already proved `resolved` is under `root`. What is
        // left is a component *between* the root and the destination that exists
        // on disk as a symlink leaving the root — the only way a link can be
        // traversed by this write. The root itself is the executor's own
        // directory and is not suspect; anything at or above it is out of scope.
        var realRootFence = RealPath(root) + Path.DirectorySeparatorChar;
        var rootFence = (root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar);

        var component = Path.GetDirectoryName(resolved);
        while (!string.IsNullOrEmpty(component)
            && component.Length >= rootFence.Length
            && (component + Path.DirectorySeparatorChar).StartsWith(rootFence, StringComparison.Ordinal))
        {
            if (Directory.Exists(component) || File.Exists(component))
            {
                var real = RealPath(component) + Path.DirectorySeparatorChar;
                if (!real.StartsWith(realRootFence, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            component = Path.GetDirectoryName(component);
        }

        return false;
    }

    private static string RealPath(string path)
    {
        // ResolveLinkTarget with returnFinalTarget follows a chain of links to
        // the real object; a non-link returns null and the path stands.
        try
        {
            var resolved = Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
                ?? File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
            return resolved is null ? Path.GetFullPath(path) : Path.GetFullPath(resolved);
        }
        catch (IOException)
        {
            return Path.GetFullPath(path);
        }
    }

    private static bool IsPlainComponent(string component, out string why)
    {
        why = string.Empty;

        if (component.Length == 0)
        {
            why = "the manifest path has an empty component";
            return false;
        }

        if (component is "." or "..")
        {
            why = $"the manifest path contains a '{component}' component";
            return false;
        }

        if (component.Contains('\0', StringComparison.Ordinal))
        {
            why = "the manifest path contains a NUL";
            return false;
        }

        // Separators, drive-relative and UNC forms cannot appear inside one
        // component. A backslash is a separator on Windows and a legal
        // filename byte on POSIX, so it is refused on both: a name that means
        // two different shapes on two platforms is one this cannot restore
        // predictably on either.
        if (component.Contains('/', StringComparison.Ordinal)
            || component.Contains('\\', StringComparison.Ordinal)
            || component.Contains(':', StringComparison.Ordinal))
        {
            why = "the manifest path component contains a separator or drive marker";
            return false;
        }

        if (Path.IsPathRooted(component))
        {
            why = "the manifest path component is rooted";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Makes the later name <paramref name="item"/> of a hard-link group a
    /// further name for the file <paramref name="first"/> landed as
    /// (ADR-0102), meeting the existing-file policy at its destination as a
    /// file would. Answers the item's receipt entry: restored as a link, or
    /// failed by the policy. Answers null where the target would not make the
    /// link, so the caller restores the name as a file of its own.
    /// </summary>
    /// <remarks>
    /// The file's metadata is the file's, written once with its first name,
    /// so a later name lists what that file did not get. Nothing is read or
    /// verified for it: it is the file that was.
    /// </remarks>
    private ReceiptItem? Link(
        RestorePlanItem item, string destination, LandedName first, string displacedRoot, List<string> displaced,
        RestoreExecutionOptions options)
    {
        if (Directory.Exists(destination))
        {
            return new ReceiptItem
            {
                Path = item.Path, Outcome = "failed", Bytes = 0,
                Detail = "a directory occupies this destination; no existing-file policy applies to a directory",
            };
        }

        var landing = destination;
        string? writtenAs = null;
        try
        {
            if (File.Exists(destination))
            {
                switch (options.ExistingDestination)
                {
                    case ExistingDestinationPolicy.Fail:
                        return new ReceiptItem
                        {
                            Path = item.Path, Outcome = "failed", Bytes = 0,
                            Detail = "a file is already at this destination and the policy is to fail",
                        };

                    case ExistingDestinationPolicy.Preserve:
                        if (!TryResolve(displacedRoot, item.Path, out var refuge, out var refusal))
                        {
                            return new ReceiptItem { Path = item.Path, Outcome = "failed", Bytes = 0, Detail = refusal };
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(refuge)!);
                        File.Move(destination, refuge);
                        displaced.Add(item.Path);
                        Log.DisplacedExisting(_logger, new LogPath(destination), new LogPath(refuge));
                        break;

                    case ExistingDestinationPolicy.WriteBeside:
                        if (!TryBesideName(destination, options.NowUnixMilliseconds, out landing, out var besideRefusal))
                        {
                            return new ReceiptItem { Path = item.Path, Outcome = "failed", Bytes = 0, Detail = besideRefusal };
                        }

                        writtenAs = BesidePath(item.Path, landing);
                        Log.WroteBeside(_logger, new LogPath(destination), new LogPath(landing));
                        break;

                    default: // Replace
                        File.Delete(destination);
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ReceiptItem { Path = item.Path, Outcome = "failed", Bytes = 0, Detail = exception.Message };
        }

        if (!_makeLink(first.Landing, landing))
        {
            return null;
        }

        return new ReceiptItem
        {
            Path = item.Path, Outcome = "restored", Bytes = (ulong)new FileInfo(landing).Length, WrittenAs = writtenAs,
            NotApplied = first.NotApplied, LinkedTo = first.Path,
        };
    }

    /// <summary>The parts of an item's detail there are, joined, or null when there are none.</summary>
    private static string? Joined(params string?[] parts) =>
        parts.Where(part => part is not null).ToList() is { Count: > 0 } present ? string.Join("; ", present) : null;

    /// <summary>
    /// Writes back what the target takes of <paramref name="metadata"/> onto
    /// the file landed, the folder made or the symlink made at
    /// <paramref name="destination"/>, as <paramref name="kind"/> says, one
    /// attribute at a time, and answers which writes landed, and which
    /// extended attributes were left and why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is fixed by what each write disturbs. Ownership goes first:
    /// giving a file to another owner or group clears its set-user-id and
    /// set-group-id bits and strips its file capabilities, so the permissions
    /// and the extended attributes have to follow it, and the permissions
    /// keep a set-id bit only where its owner or group landed. Extended
    /// attributes go on before the permissions, because a captured mode that
    /// takes away the owner's write permission would refuse the owner's own
    /// attributes, and an ACL sets the mode's group bits that the
    /// permissions then set as captured. Permissions change no time.
    /// Modification and access times follow. The creation time goes last. On
    /// macOS a modification time set earlier than the creation time makes
    /// the volume move the creation time back to it, and .NET then restores
    /// the one before; written last, the captured creation time is the last
    /// word whichever of those holds. A folder is written through the
    /// folder's own calls, which Windows needs to open one for its times.
    /// </para>
    /// <para>
    /// An owner or group is attempted wherever its name resolves, whatever
    /// the plan predicted, and the platform decides: the receipt records
    /// that. So is each extended attribute the rule does not withhold
    /// (<see cref="RestoreMetadata.Withheld"/>); any that is withheld or
    /// refused leaves extended attributes not applied, and the detail names
    /// it. A write that fails, a name that resolves to nothing, or a time no
    /// file can carry is that attribute not applied, never the item failed.
    /// </para>
    /// <para>
    /// Where the file's captured ACL did not come back, its group is given
    /// no more than the ACL gave it (<see cref="RestoreMetadata.WithoutAcl"/>),
    /// and its permissions are then not those captured.
    /// </para>
    /// <para>
    /// A symlink takes the same writes in the same order, each by a call that
    /// changes the link itself (ADR-0101). .NET's permission setter follows a
    /// link and would write onto whatever it points at, and its time setters
    /// promise nothing either way, so a link's permissions and times go
    /// through <see cref="SymlinkMetadata"/>. Its owner, group
    /// and extended attributes are written by calls that never follow one,
    /// and its creation time only on macOS, whose call does not. Linux gives
    /// every link the same permissions and has no call to change them, so
    /// there a link's captured ones are written by nothing and land only where
    /// they are those (<see cref="RestoreMetadata.LinkPermissionsLand"/>).
    /// </para>
    /// </remarks>
    private Written ApplyMetadata(string destination, EntryMetadata metadata, EntryKind kind = EntryKind.File)
    {
        var applied = CapturedMetadata.None;
        var link = kind == EntryKind.Symlink;
        FileSystemInfo landed = kind == EntryKind.DirectoryPlaceholder ? new DirectoryInfo(destination) : new FileInfo(destination);

        if (target.SupportsPosixMetadata && !OperatingSystem.IsWindows() && target.Account is { } account)
        {
            if (metadata.OwnerName is { } owner
                && Resolved(_owners, owner, account.ResolveUser) is { } userId
                && FileOwnership.TrySetOwner(destination, userId))
            {
                applied |= CapturedMetadata.Owner;
            }

            if (metadata.GroupName is { } group
                && Resolved(_groups, group, account.ResolveGroup) is { } groupId
                && FileOwnership.TrySetGroup(destination, groupId))
            {
                applied |= CapturedMetadata.Group;
            }
        }

        // Each alone, so a refusal leaves the rest. The access ACL is
        // remembered, because the permissions depend on whether it landed.
        var writes = target.SupportsExtendedAttributes && ExtendedAttributes.CanSet;
        var left = new List<(string Name, string Why)>();
        ExtendedAttributeEntry? acl = null;
        var aclLanded = false;
        foreach (var attribute in metadata.ExtendedAttributes)
        {
            var isAcl = RestoreMetadata.IsAccessAcl(attribute.Name.Span);
            var why = !writes ? null : RestoreMetadata.Withheld(attribute, target, link);
            var landedHere = writes
                && why is null
                && ExtendedAttributes.TrySet(destination, attribute.Name.Span, attribute.Value.Span);
            if (writes && !landedHere)
            {
                left.Add((System.Text.Encoding.UTF8.GetString(attribute.Name.Span), why ?? "refused by the target"));
            }

            if (isAcl)
            {
                (acl, aclLanded) = (attribute, landedHere);
            }
        }

        if (writes && metadata.ExtendedAttributes.Count > 0 && left.Count == 0)
        {
            applied |= CapturedMetadata.ExtendedAttributes;
        }

        if (target.SupportsPosixMetadata && metadata.PosixMode is { } mode && !OperatingSystem.IsWindows())
        {
            // A set-id bit whose owner or group did not land is dropped, and
            // so is any group permission only an ACL that did not land gave,
            // so the mode that lands is not the one captured.
            var permitted = RestoreMetadata.PermittedMode(mode, applied);
            if (acl is not null && !aclLanded)
            {
                permitted = RestoreMetadata.WithoutAcl(permitted, acl);
            }

            var lands = !link
                ? Wrote(() => landed.UnixFileMode = (UnixFileMode)permitted)
                : SymlinkMetadata.CanSetPermissions
                    ? SymlinkMetadata.TrySetPermissions(destination, permitted)
                    : RestoreMetadata.LinkPermissionsLand(permitted);
            if (lands && permitted == (mode & 0xFFF))
            {
                applied |= CapturedMetadata.PosixMode;
            }
        }

        if (link)
        {
            var modifiedAt = FileTimes.FromUnixMilliseconds(metadata.ModifiedAt);
            var accessedAt = FileTimes.FromUnixMilliseconds(metadata.AccessedAt);
            if ((modifiedAt ?? accessedAt) is not null && SymlinkMetadata.TrySetTimes(destination, modifiedAt, accessedAt))
            {
                applied |= (modifiedAt is null ? CapturedMetadata.None : CapturedMetadata.ModifiedAt)
                    | (accessedAt is null ? CapturedMetadata.None : CapturedMetadata.AccessedAt);
            }
        }
        else
        {
            if (FileTimes.FromUnixMilliseconds(metadata.ModifiedAt) is { } modified
                && Wrote(() => landed.LastWriteTimeUtc = modified))
            {
                applied |= CapturedMetadata.ModifiedAt;
            }

            if (FileTimes.FromUnixMilliseconds(metadata.AccessedAt) is { } accessed
                && Wrote(() => landed.LastAccessTimeUtc = accessed))
            {
                applied |= CapturedMetadata.AccessedAt;
            }
        }

        if (target.SupportsCreationTimes
            && (!link || OperatingSystem.IsMacOS())
            && FileTimes.FromUnixMilliseconds(metadata.CreatedAt) is { } created
            && FileTimes.TrySetCreationTime(destination, created))
        {
            applied |= CapturedMetadata.CreatedAt;
        }

        return new Written(
            applied,
            left.Count == 0
                ? null
                : "extended attribute(s) not applied: " + string.Join("; ", left
                    .GroupBy(entry => entry.Why, StringComparer.Ordinal)
                    .Select(reason => $"{string.Join(", ", reason.Select(entry => entry.Name))} ({reason.Key})")));

        static bool Wrote(Action write)
        {
            try
            {
                write();
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        static uint? Resolved(Dictionary<string, uint?> known, string name, Func<string, uint?> resolve)
        {
            if (!known.TryGetValue(name, out var id))
            {
                known[name] = id = resolve(name);
            }

            return id;
        }
    }

    /// <summary>The first name of a hard-link group the run landed, which its later names are made again.</summary>
    /// <param name="Key">What a later name must agree on with it (<see cref="RestoreHardLinks.KeyOf"/>).</param>
    /// <param name="Path">Its path in the snapshot.</param>
    /// <param name="Landing">Where it landed.</param>
    /// <param name="NotApplied">What its file did not get back, which every name of it shares.</param>
    private sealed record LandedName(string Key, string Path, string Landing, IReadOnlyList<string>? NotApplied);

    /// <summary>What the metadata writes for one file or folder did.</summary>
    /// <param name="Applied">The attributes that landed as captured.</param>
    /// <param name="Detail">Which extended attributes were left and why, or null when none was.</param>
    private readonly record struct Written(CapturedMetadata Applied, string? Detail);
}
