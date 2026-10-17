using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Repository.Index;

/// <summary>The loaded index: every applied entry, the resolved winner per object, and every finding.</summary>
public sealed class IndexState
{
    internal IndexState(
        IReadOnlyDictionary<ObjectId, ProvenancedEntry> resolved,
        IReadOnlyList<ProvenancedEntry> allEntries,
        IReadOnlyList<DecodedDelta> deltas,
        IReadOnlyList<DecodedCheckpoint> checkpoints,
        IReadOnlyList<(WriterId Writer, ulong Sequence)> unresolvedGaps,
        IReadOnlyList<DamageFinding> findings)
    {
        Resolved = resolved;
        AllEntries = allEntries;
        Deltas = deltas;
        Checkpoints = checkpoints;
        UnresolvedGaps = unresolvedGaps;
        Findings = findings;
    }

    /// <summary>The winning location per object identifier, after precedence (07 §3).</summary>
    public IReadOnlyDictionary<ObjectId, ProvenancedEntry> Resolved { get; }

    /// <summary>Every applied entry with provenance.</summary>
    public IReadOnlyList<ProvenancedEntry> AllEntries { get; }

    /// <summary>The applied deltas.</summary>
    public IReadOnlyList<DecodedDelta> Deltas { get; }

    /// <summary>The applied checkpoints — all of them, at every generation (07 §6).</summary>
    public IReadOnlyList<DecodedCheckpoint> Checkpoints { get; }

    /// <summary>
    /// Sequence gaps not yet resolvable — neither delta, void, nor accounting
    /// object seen, but still inside the bounded patience (07 §4).
    /// </summary>
    public IReadOnlyList<(WriterId Writer, ulong Sequence)> UnresolvedGaps { get; }

    /// <summary>Every damage and security finding.</summary>
    public IReadOnlyList<DamageFinding> Findings { get; }

    /// <summary>
    /// The highest sequence number the repository itself attests for
    /// <paramref name="writer"/> — the observed head (NFR-SEC-005).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The writer's own <c>sequence</c> file is allocation state: it says what
    /// this machine has handed out, and it is exactly as durable as the state
    /// directory holding it. This is the other half, and it is the half that
    /// travels: every applied delta carries its signed sequence, whether it
    /// was read from its key or from a checkpoint carrying it (07 §§2, 5), so
    /// any replica of the repository can say how far its writers had got. A
    /// machine whose local state was lost or rolled back can therefore learn
    /// the truth from the repository rather than from a collision partway
    /// through its next backup.
    /// </para>
    /// <para>
    /// Zero for a writer this index has never seen, which is the honest
    /// answer for a genuinely new writer and for one whose objects are all
    /// missing — the second being a damage finding this method is not the
    /// place to raise.
    /// </para>
    /// </remarks>
    /// <param name="writer">The writer to ask about.</param>
    public ulong ObservedHeadFor(WriterId writer)
    {
        var head = 0UL;

        foreach (var delta in Deltas)
        {
            if (delta.Delta.WriterId == writer)
            {
                head = Math.Max(head, delta.Delta.Sequence);
            }
        }

        // A gap inside the bounded patience is a number this writer allocated
        // and has not accounted for: unpublished, but assuredly used.
        foreach (var (gapWriter, sequence) in UnresolvedGaps)
        {
            if (gapWriter == writer)
            {
                head = Math.Max(head, sequence);
            }
        }

        return head;
    }
}

/// <summary>
/// Loads the index (specification 07 §5–§8): every checkpoint at every
/// generation is read (07 §6), every delta it carries is opened from its copy
/// and applied, and every listed delta no verified copy carries is fetched
/// from its key and applied
/// ([ADR-0097](../../../docs/adr/0097-format-5-a-checkpoint-carries-its-deltas.md)).
/// A copy that does not open is reported and the delta is read from its key
/// instead. A delta's key carries a random identifier, so one the listing
/// omits and no checkpoint carries is never fetched; it is seen only as a gap
/// once a later number of its writer is. A checkpoint's own entries and
/// watermarks, the earlier form, are not acted on (ADR-0008 Amendment 1).
/// Signatures verify against the derived signing key per generation; a
/// failure is a <b>security finding</b> and the object is excluded. Sequence
/// gaps follow ADR-0022 §Decision 7: a number is accounted for by a delta, a
/// void delta, a checkpoint's cleartext counter, or the caller's accounting
/// callback (journal records and intent-covered blobs); a gap older than the
/// bounded patience is a damage finding, never an indefinite block (07 §4).
/// </summary>
public sealed class IndexLoader : IDisposable
{
    /// <summary>
    /// How many generations a production reader lets a gap stay unresolved
    /// before it reports it as damage (07 §4;
    /// [ADR-0096](../../../docs/adr/0096-format-4-an-index-generation-per-delta.md)).
    /// </summary>
    /// <remarks>
    /// Counted per gap, in index generations, from the first later delta of
    /// the gap's writer. A number its writer left pending is discharged by
    /// that writer's next publication (07 §4), so eight is room for the
    /// writer to miss several runs while the repository's other writers
    /// publish, after which the gap is reported as the damage it then most
    /// likely is.
    /// </remarks>
    public const int GapPatienceGenerations = 8;

    private readonly IObjectStore _store;
    private readonly RepositoryId _repositoryId;
    private readonly RepositoryWriteCredential _credential;
    private readonly ObjectIdDeriver _objectIdDeriver;
    private readonly ILogger _log;

    /// <summary>Creates a loader.</summary>
    /// <param name="store">Where the index plane lives.</param>
    /// <param name="repositoryId">The repository being loaded.</param>
    /// <param name="credential">The write credential the objects open under.</param>
    /// <param name="logger">Where the load's shape is recorded.</param>
    public IndexLoader(
        IObjectStore store, RepositoryId repositoryId, RepositoryWriteCredential credential, ILogger? logger = null)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(credential);

        _log = logger ?? NullLogger.Instance;
        _store = store;
        _repositoryId = repositoryId;
        _credential = credential;
        _objectIdDeriver = new ObjectIdDeriver(credential.ContentIdKey.ToArray());
    }

    /// <summary>
    /// Loads and applies the whole index plane.
    /// </summary>
    /// <param name="currentGeneration">The repository's current generation (ADR-0022 §Decision 5).</param>
    /// <param name="gapPatienceGenerations">
    /// How many generations a gap may stay unresolved before it is damage
    /// (07 §4): key generations past its writer's newest, or index
    /// generations past the first later delta of its writer, whichever is
    /// more. A production reader passes <see cref="GapPatienceGenerations"/>.
    /// </param>
    /// <param name="isSequenceAccountedAsync">Accounts sequences the index cannot see — journal records and intent-covered blobs; null means none.</param>
    /// <param name="blobState">The reader's blob-lifecycle knowledge for precedence rule 3.</param>
    /// <param name="cancellationToken">Cancels the loads.</param>
    public async ValueTask<IndexState> LoadAsync(
        ulong currentGeneration,
        int gapPatienceGenerations,
        Func<WriterId, ulong, ValueTask<bool>>? isSequenceAccountedAsync,
        Func<BlobId, BlobState>? blobState,
        CancellationToken cancellationToken)
    {
        var findings = new List<DamageFinding>();
        var checkpoints = new List<DecodedCheckpoint>();
        var deltas = new List<DecodedDelta>();
        var checkpointCounters = new HashSet<(WriterId, ulong)>();
        var carried = new Dictionary<DeltaId, DecodedDelta>();

        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("index/checkpoint/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            var bytes = await FetchAsync(entry.Key, findings, cancellationToken).ConfigureAwait(false);
            if (bytes is null || OpenCheckpoint(entry.Key, bytes, findings) is not { } opened)
            {
                continue;
            }

            checkpointCounters.Add(opened.Counter);
            checkpoints.Add(opened.Checkpoint);

            // A carried copy is opened exactly as the object at the delta's
            // key would be (07 §5). One that does not open is reported and
            // left out, and the delta is read from its key instead.
            var checkpoint = opened.Checkpoint.Checkpoint;
            for (var index = 0; index < checkpoint.SubsumedDeltas.Count; index++)
            {
                var deltaId = checkpoint.SubsumedDeltaIds[index];
                if (!carried.ContainsKey(deltaId) &&
                    OpenDelta($"{entry.Key}, delta {deltaId}", checkpoint.SubsumedDeltas[index].ToArray(), findings) is { } delta)
                {
                    carried[deltaId] = delta with { DeltaId = deltaId };
                }
            }
        }

        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("index/delta/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            var named = MetadataStoreKeys.TryParseIndexDelta(entry.Key, out var deltaId);
            if (named && carried.ContainsKey(deltaId))
            {
                continue;
            }

            var bytes = await FetchAsync(entry.Key, findings, cancellationToken).ConfigureAwait(false);
            if (bytes is not null && OpenDelta(entry.Key.Value, bytes, findings) is { } delta)
            {
                deltas.Add(named ? delta with { DeltaId = deltaId } : delta);
            }
        }

        // A carried delta is applied whether or not the listing still shows
        // it: the copy is the delta.
        deltas.AddRange(carried.Values);

        // Apply every delta (07 §5) — safe under any arrival order because of
        // §3. A checkpoint contributes only the deltas it carries: its own
        // entries would rank at the checkpoint rather than where each delta
        // ranks, and its watermarks would hide whatever its builder's listing
        // missed (ADR-0008 Amendment 1, constraints 1 and 3). Neither is acted
        // on.
        var allEntries = new List<ProvenancedEntry>();

        var appliedSequences = new Dictionary<WriterId, HashSet<ulong>>();
        var newestGenerationPerWriter = new Dictionary<WriterId, ulong>();

        foreach (var delta in deltas)
        {
            var writer = delta.Delta.WriterId;
            appliedSequences.TryAdd(writer, []);
            appliedSequences[writer].Add(delta.Delta.Sequence);
            newestGenerationPerWriter[writer] = Math.Max(
                newestGenerationPerWriter.GetValueOrDefault(writer), delta.Delta.Generation);

            foreach (var entry in delta.Delta.Entries)
            {
                allEntries.Add(new ProvenancedEntry(entry, delta.Delta.RankGeneration, writer, delta.Delta.Sequence));
            }
        }

        // Gap accounting (07 §4; ADR-0022 §Decision 7): every number up to the
        // writer's highest seen must be accounted for.
        var unresolved = new List<(WriterId, ulong)>();
        var currentIndexGeneration = deltas.Count == 0 ? 0 : deltas.Max(delta => delta.Delta.RankGeneration);
        var indexGenerationsBySequence = deltas
            .GroupBy(delta => delta.Delta.WriterId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(delta => delta.Delta.Sequence)
                    .Select(delta => (delta.Delta.Sequence, delta.Delta.IndexGeneration))
                    .ToArray());

        foreach (var (writer, sequences) in appliedSequences)
        {
            var ceiling = sequences.Max();
            var published = indexGenerationsBySequence[writer];
            var above = 0;

            for (var sequence = 1UL; sequence <= ceiling; sequence++)
            {
                if (sequences.Contains(sequence) || checkpointCounters.Contains((writer, sequence)))
                {
                    continue;
                }

                if (isSequenceAccountedAsync is not null &&
                    await isSequenceAccountedAsync(writer, sequence).ConfigureAwait(false))
                {
                    continue;
                }

                // The first delta of this writer above the gap was published
                // after the number was allocated, so the gap is at least as
                // old as it. One that predates format 4 carries no index
                // generation to count from, and the gap is aged as before.
                while (published[above].Sequence < sequence)
                {
                    above++;
                }

                var keyGenerationAge =
                    currentGeneration - Math.Min(currentGeneration, newestGenerationPerWriter.GetValueOrDefault(writer));
                var indexGenerationAge = published[above].IndexGeneration is { } after
                    ? currentIndexGeneration - after
                    : 0;
                var age = Math.Max(keyGenerationAge, indexGenerationAge);
                if (age >= (ulong)gapPatienceGenerations)
                {
                    findings.Add(new DamageFinding(
                        DamageKind.MissingIndexObject,
                        $"Writer {writer} sequence {sequence} is unaccounted for after {age} generations — neither delta, void, nor accounting object (specification 07 §4)."));
                }
                else
                {
                    unresolved.Add((writer, sequence));
                }
            }
        }

        // Resolve precedence per object identifier.
        var state = blobState ?? (_ => BlobState.Live);
        var resolved = new Dictionary<ObjectId, ProvenancedEntry>();

        foreach (var group in allEntries.GroupBy(entry => entry.Entry.ObjectId))
        {
            if (IndexPrecedence.Resolve([.. group], state, findings) is { } winner)
            {
                resolved[group.Key] = winner;
            }
        }

        Log.IndexLoaded(
            _log, checked((uint)currentGeneration), deltas.Count, checkpoints.Count, findings.Count);

        return new IndexState(resolved, allEntries, deltas, checkpoints, unresolved, findings);
    }

    /// <summary>
    /// Opens a checkpoint read from <paramref name="key"/>: framing, type,
    /// authentication, identifier, decoding and signature, each failure a
    /// finding. Returns the checkpoint and the writer and counter its
    /// cleartext prefix accounts for, or null.
    /// </summary>
    internal (DecodedCheckpoint Checkpoint, (WriterId Writer, ulong Counter) Counter)? OpenCheckpoint(
        ObjectKey key, byte[] bytes, List<DamageFinding> findings)
    {
        if (Open(key.Value, bytes, ObjectType.IndexCheckpoint, findings) is not { } opened)
        {
            return null;
        }

        DecodedCheckpoint decoded;
        try
        {
            decoded = CheckpointCodec.Decode(opened.Plaintext);
        }
        catch (IndexFormatException exception)
        {
            findings.Add(new DamageFinding(DamageKind.MissingIndexObject, $"Checkpoint '{key}': {exception.Message}"));
            return null;
        }

        if (!VerifySignature(decoded.Checkpoint.Generation, decoded.SignedBytes.Span, decoded.Signature.Span, key.Value, findings))
        {
            return null;
        }

        return (decoded, (opened.Record.WriterId, opened.Record.Counter));
    }

    /// <summary>
    /// Opens a delta's stored object, read from its key or carried in a
    /// checkpoint, by one path: framing, type, authentication, identifier,
    /// decoding and signature, each failure a finding. Null when it does not
    /// open.
    /// </summary>
    /// <param name="label">Where the bytes came from, for the finding.</param>
    /// <param name="bytes">The stored object.</param>
    /// <param name="findings">Where a failure is reported.</param>
    internal DecodedDelta? OpenDelta(string label, byte[] bytes, List<DamageFinding> findings)
    {
        if (Open(label, bytes, ObjectType.IndexDelta, findings) is not { } opened)
        {
            return null;
        }

        DecodedDelta decoded;
        try
        {
            decoded = IndexDeltaCodec.Decode(opened.Plaintext);
        }
        catch (IndexFormatException exception)
        {
            findings.Add(new DamageFinding(DamageKind.MissingIndexObject, $"Delta '{label}': {exception.Message}"));
            return null;
        }

        return VerifySignature(decoded.Delta.Generation, decoded.SignedBytes.Span, decoded.Signature.Span, label, findings)
            ? decoded
            : null;
    }

    /// <summary>Reads the whole object at <paramref name="key"/>, or reports it vanished.</summary>
    internal async ValueTask<byte[]?> FetchAsync(ObjectKey key, List<DamageFinding> findings, CancellationToken cancellationToken)
    {
        using var read = await _store.OpenReadAsync(key, range: null, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != OpenReadOutcome.Found)
        {
            findings.Add(new DamageFinding(DamageKind.MissingIndexObject, $"Index object '{key}' vanished between listing and read."));
            return null;
        }

        using var memory = new MemoryStream();
        await read.Content!.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }

    private bool VerifySignature(
        ulong generation,
        ReadOnlySpan<byte> signedBytes,
        ReadOnlySpan<byte> signature,
        string label,
        List<DamageFinding> findings)
    {
        if (generation > uint.MaxValue)
        {
            findings.Add(new DamageFinding(DamageKind.MissingIndexObject, $"Index object '{label}' declares generation {generation}, beyond the u32 key space."));
            return false;
        }

        using var signer = RepositorySigner.Create(_credential, new KeyGeneration((uint)generation));

        if (!signer.Verify(signedBytes, signature))
        {
            // A bad signature means substitution or forgery, not a bad disk
            // (06 §6.1) — reported as security, and the object is excluded.
            findings.Add(new DamageFinding(
                DamageKind.SecurityFinding,
                $"Index object '{label}' failed signature verification for generation {generation}."));
            return false;
        }

        return true;
    }

    private (StandaloneRecord Record, byte[] Plaintext)? Open(
        string label,
        byte[] bytes,
        ObjectType expectedType,
        List<DamageFinding> findings)
    {
        StandaloneRecord record;
        try
        {
            record = StandaloneRecordFraming.Parse(bytes);
        }
        catch (RecordFormatException exception)
        {
            findings.Add(new DamageFinding(DamageKind.MissingIndexObject, $"Index object '{label}': {exception.Message}"));
            return null;
        }

        if (record.Header.ObjectType != expectedType)
        {
            findings.Add(new DamageFinding(
                DamageKind.MissingIndexObject,
                $"Index object '{label}' carries object type {record.Header.ObjectType}, not {expectedType}."));
            return null;
        }

        var metadataKey = _credential.DeriveMetadataKey(record.KeyGeneration);
        try
        {
            if (!StandaloneRecordCipher.TryOpen(record, _repositoryId, metadataKey, out var plaintext))
            {
                findings.Add(new DamageFinding(
                    DamageKind.CorruptRecord,
                    $"Index object '{label}' failed authentication (specification 04 §7)."));
                return null;
            }

            // 04 §6 step 7: the plaintext must hash to the identifier the
            // header claims — skipping this restores corrupt data as success.
            var implied = _objectIdDeriver.Derive(record.Header.ObjectType, ContentHasher.Hash(plaintext));
            if (implied != record.Header.ObjectId)
            {
                findings.Add(new DamageFinding(
                    DamageKind.CorruptRecord,
                    $"Index object '{label}' decrypted but its content does not match its identifier (04 §6 step 7)."));
                return null;
            }

            return (record, plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(metadataKey);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _objectIdDeriver.Dispose();
}
