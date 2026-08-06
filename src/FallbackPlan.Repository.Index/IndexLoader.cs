using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

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
}

/// <summary>
/// Loads the index (specification 07 §5–§8): every checkpoint at every
/// generation is applied (07 §6), then every delta whose sequence exceeds
/// its writer's watermark — whether or not a listing revealed it in time.
/// Signatures verify against the derived signing key per generation; a
/// failure is a <b>security finding</b> and the object is excluded. Sequence
/// gaps follow ADR-0022 §Decision 7: a number is accounted for by a delta, a
/// void delta, a checkpoint's cleartext counter, or the caller's accounting
/// callback (journal records and intent-covered blobs); a gap older than the
/// bounded patience is a damage finding, never an indefinite block (07 §4).
/// </summary>
public sealed class IndexLoader : IDisposable
{
    private readonly IObjectStore _store;
    private readonly RepositoryId _repositoryId;
    private readonly KeyHierarchy _hierarchy;
    private readonly ObjectIdDeriver _objectIdDeriver;

    /// <summary>Creates a loader.</summary>
    public IndexLoader(IObjectStore store, RepositoryId repositoryId, KeyHierarchy hierarchy)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(hierarchy);

        _store = store;
        _repositoryId = repositoryId;
        _hierarchy = hierarchy;
        _objectIdDeriver = new ObjectIdDeriver(hierarchy.DeriveContentIdKey());
    }

    /// <summary>
    /// Loads and applies the whole index plane.
    /// </summary>
    /// <param name="currentGeneration">The repository's current generation (ADR-0022 §Decision 5).</param>
    /// <param name="gapPatienceGenerations">How many generations a gap may stay unresolved before it is damage (07 §4).</param>
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

        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("index/checkpoint/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            var opened = await OpenObjectAsync(entry.Key, ObjectType.IndexCheckpoint, findings, cancellationToken)
                .ConfigureAwait(false);
            if (opened is not { } checkpointRecord)
            {
                continue;
            }

            checkpointCounters.Add((checkpointRecord.Record.WriterId, checkpointRecord.Record.Counter));

            DecodedCheckpoint decoded;
            try
            {
                decoded = CheckpointCodec.Decode(checkpointRecord.Plaintext);
            }
            catch (IndexFormatException exception)
            {
                findings.Add(new DamageFinding(DamageKind.MissingIndexObject, $"Checkpoint '{entry.Key}': {exception.Message}"));
                continue;
            }

            if (!VerifySignature(decoded.Checkpoint.Generation, decoded.SignedBytes.Span, decoded.Signature.Span, entry.Key, findings))
            {
                continue;
            }

            checkpoints.Add(decoded);
        }

        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("index/delta/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            var opened = await OpenObjectAsync(entry.Key, ObjectType.IndexDelta, findings, cancellationToken)
                .ConfigureAwait(false);
            if (opened is not { } deltaRecord)
            {
                continue;
            }

            DecodedDelta decoded;
            try
            {
                decoded = IndexDeltaCodec.Decode(deltaRecord.Plaintext);
            }
            catch (IndexFormatException exception)
            {
                findings.Add(new DamageFinding(DamageKind.MissingIndexObject, $"Delta '{entry.Key}': {exception.Message}"));
                continue;
            }

            if (!VerifySignature(decoded.Delta.Generation, decoded.SignedBytes.Span, decoded.Signature.Span, entry.Key, findings))
            {
                continue;
            }

            deltas.Add(decoded);
        }

        // Watermarks: the highest any retained checkpoint states per writer.
        var watermarks = new Dictionary<WriterId, ulong>();
        foreach (var checkpoint in checkpoints)
        {
            foreach (var watermark in checkpoint.Checkpoint.WriterWatermarks)
            {
                watermarks[watermark.WriterId] = Math.Max(
                    watermarks.GetValueOrDefault(watermark.WriterId), watermark.HighestSequence);
            }
        }

        // Apply: every checkpoint, then every delta above its writer's
        // watermark (07 §5) — safe under any arrival order because of §3.
        var allEntries = new List<ProvenancedEntry>();

        foreach (var checkpoint in checkpoints)
        {
            var watermark = checkpoint.Checkpoint.WriterWatermarks
                .FirstOrDefault(mark => mark.WriterId == checkpoint.Checkpoint.WriterId)?.HighestSequence ?? 0;

            foreach (var entry in checkpoint.Checkpoint.Entries)
            {
                allEntries.Add(new ProvenancedEntry(
                    entry, checkpoint.Checkpoint.Generation, checkpoint.Checkpoint.WriterId, watermark));
            }
        }

        var appliedSequences = new Dictionary<WriterId, HashSet<ulong>>();
        var newestGenerationPerWriter = new Dictionary<WriterId, ulong>();

        foreach (var delta in deltas)
        {
            var writer = delta.Delta.WriterId;
            appliedSequences.TryAdd(writer, []);
            appliedSequences[writer].Add(delta.Delta.Sequence);
            newestGenerationPerWriter[writer] = Math.Max(
                newestGenerationPerWriter.GetValueOrDefault(writer), delta.Delta.Generation);

            if (delta.Delta.Sequence <= watermarks.GetValueOrDefault(writer))
            {
                continue; // subsumed by a checkpoint
            }

            foreach (var entry in delta.Delta.Entries)
            {
                allEntries.Add(new ProvenancedEntry(entry, delta.Delta.Generation, writer, delta.Delta.Sequence));
            }
        }

        // Gap accounting (07 §4; ADR-0022 §Decision 7): every number from the
        // watermark to the writer's highest seen must be accounted for.
        var unresolved = new List<(WriterId, ulong)>();

        foreach (var (writer, sequences) in appliedSequences)
        {
            var floor = watermarks.GetValueOrDefault(writer);
            var ceiling = sequences.Max();

            for (var sequence = floor + 1; sequence <= ceiling; sequence++)
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

                var age = currentGeneration - Math.Min(currentGeneration, newestGenerationPerWriter.GetValueOrDefault(writer));
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

        return new IndexState(resolved, allEntries, deltas, checkpoints, unresolved, findings);
    }

    private bool VerifySignature(
        ulong generation,
        ReadOnlySpan<byte> signedBytes,
        ReadOnlySpan<byte> signature,
        ObjectKey key,
        List<DamageFinding> findings)
    {
        if (generation > uint.MaxValue)
        {
            findings.Add(new DamageFinding(DamageKind.MissingIndexObject, $"Index object '{key}' declares generation {generation}, beyond the u32 key space."));
            return false;
        }

        using var signer = RepositorySigner.Create(_hierarchy, new KeyGeneration((uint)generation));

        if (!signer.Verify(signedBytes, signature))
        {
            // A bad signature means substitution or forgery, not a bad disk
            // (06 §6.1) — reported as security, and the object is excluded.
            findings.Add(new DamageFinding(
                DamageKind.SecurityFinding,
                $"Index object '{key}' failed signature verification for generation {generation}."));
            return false;
        }

        return true;
    }

    private async ValueTask<(StandaloneRecord Record, byte[] Plaintext)?> OpenObjectAsync(
        ObjectKey key,
        ObjectType expectedType,
        List<DamageFinding> findings,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        using (var read = await _store.OpenReadAsync(key, range: null, cancellationToken).ConfigureAwait(false))
        {
            if (read.Outcome != OpenReadOutcome.Found)
            {
                findings.Add(new DamageFinding(DamageKind.MissingIndexObject, $"Index object '{key}' vanished between listing and read."));
                return null;
            }

            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            bytes = memory.ToArray();
        }

        StandaloneRecord record;
        try
        {
            record = StandaloneRecordFraming.Parse(bytes);
        }
        catch (RecordFormatException exception)
        {
            findings.Add(new DamageFinding(DamageKind.MissingIndexObject, $"Index object '{key}': {exception.Message}"));
            return null;
        }

        if (record.Header.ObjectType != expectedType)
        {
            findings.Add(new DamageFinding(
                DamageKind.MissingIndexObject,
                $"Index object '{key}' carries object type {record.Header.ObjectType}, not {expectedType}."));
            return null;
        }

        var metadataKey = _hierarchy.DeriveMetadataKey(record.KeyGeneration);
        try
        {
            if (!StandaloneRecordCipher.TryOpen(record, _repositoryId, metadataKey, out var plaintext))
            {
                findings.Add(new DamageFinding(
                    DamageKind.CorruptRecord,
                    $"Index object '{key}' failed authentication (specification 04 §7)."));
                return null;
            }

            // 04 §6 step 7: the plaintext must hash to the identifier the
            // header claims — skipping this restores corrupt data as success.
            var implied = _objectIdDeriver.Derive(record.Header.ObjectType, ContentHasher.Hash(plaintext));
            if (implied != record.Header.ObjectId)
            {
                findings.Add(new DamageFinding(
                    DamageKind.CorruptRecord,
                    $"Index object '{key}' decrypted but its content does not match its identifier (04 §6 step 7)."));
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
