using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Repository.Index.Resources;

namespace FallbackPlan.Repository.Index.Journal;

/// <summary>
/// Publishes journal records (specification 08 §1–§2): signed keys 1–5,
/// sealed under FBPKSREC with the record's sequence as the counter, stored
/// at <c>/journal/&lt;writer-id&gt;/&lt;sequence&gt;</c>. The put is awaited
/// and its outcome checked before the caller proceeds — durability first is
/// the whole mechanism (08 §3.1).
/// </summary>
public sealed class JournalPublisher : IDisposable
{
    private readonly IObjectStore _store;
    private readonly RepositoryId _repositoryId;
    private readonly WriterId _writerId;
    private readonly KeyHierarchy _hierarchy;
    private readonly WriterSequence _sequence;
    private readonly ObjectIdDeriver _objectIdDeriver;

    /// <summary>Creates a publisher for one writer.</summary>
    public JournalPublisher(
        IObjectStore store,
        RepositoryId repositoryId,
        WriterId writerId,
        KeyHierarchy hierarchy,
        WriterSequence sequence)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(hierarchy);
        ThrowHelper.ThrowIfNull(sequence);

        _store = store;
        _repositoryId = repositoryId;
        _writerId = writerId;
        _hierarchy = hierarchy;
        _sequence = sequence;
        _objectIdDeriver = new ObjectIdDeriver(hierarchy.DeriveContentIdKey());
    }

    /// <summary>
    /// Publishes one record, allocating the next sequence from the shared
    /// space. Returns the sequence once the object is durable.
    /// </summary>
    public async ValueTask<ulong> PublishAsync(
        JournalRecordKind kind,
        JournalPayload payload,
        ulong issuedAtMs,
        uint signingGeneration,
        CancellationToken cancellationToken)
    {
        var sequence = _sequence.AllocateNext();
        var record = new JournalRecord(kind, _writerId, sequence, issuedAtMs, payload);

        byte[] stored;
        using (var signer = RepositorySigner.Create(_hierarchy, new KeyGeneration(signingGeneration)))
        {
            stored = JournalRecordCodec.Encode(record, signer.Sign(JournalRecordCodec.EncodeForSigning(record)));
        }

        var keyGeneration = new KeyGeneration(signingGeneration);
        var objectId = _objectIdDeriver.Derive(ObjectType.JournalRecord, ContentHasher.Hash(stored));
        var metadataKey = _hierarchy.DeriveMetadataKey(keyGeneration);

        byte[] sealedObject;
        try
        {
            sealedObject = StandaloneRecordCipher.Seal(
                _repositoryId, metadataKey, keyGeneration, _writerId, sequence,
                ObjectType.JournalRecord, objectId, stored);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(metadataKey);
        }

        var put = await _store.PutAsync(
            MetadataStoreKeys.Journal(_writerId, sequence),
            _ => ValueTask.FromResult<Stream>(new MemoryStream(sealedObject, writable: false)),
            PutConditions.IfNotExists,
            cancellationToken).ConfigureAwait(false);

        if (put.Outcome == PutOutcome.PreconditionFailed)
        {
            throw new IOException(Strings.FormatJournalPublisher_StoreRefusedJournalRecordWith(sequence));
        }

        _sequence.MarkAccounted(sequence);
        return sequence;
    }

    /// <inheritdoc />
    public void Dispose() => _objectIdDeriver.Dispose();
}

/// <summary>
/// Reads the journal (specification 08 §8): every record under
/// <c>/journal/</c>, decrypted, signature-verified by generation descent,
/// content-verified per 04 §6 step 7 — and every record that cannot be
/// parsed counted, because a collector MUST treat an unparseable intent as
/// live.
/// </summary>
public sealed class JournalReader : IDisposable
{
    private readonly IObjectStore _store;
    private readonly RepositoryId _repositoryId;
    private readonly KeyHierarchy _hierarchy;
    private readonly ObjectIdDeriver _objectIdDeriver;

    /// <summary>Creates a reader.</summary>
    public JournalReader(IObjectStore store, RepositoryId repositoryId, KeyHierarchy hierarchy)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(hierarchy);

        _store = store;
        _repositoryId = repositoryId;
        _hierarchy = hierarchy;
        _objectIdDeriver = new ObjectIdDeriver(hierarchy.DeriveContentIdKey());
    }

    /// <summary>Loads every journal record, counting the unparseable separately.</summary>
    public async ValueTask<(IReadOnlyList<JournalRecord> Records, int Unparseable, IReadOnlyList<DamageFinding> Findings)> LoadAsync(
        uint maxGeneration,
        CancellationToken cancellationToken)
    {
        var records = new List<JournalRecord>();
        var findings = new List<DamageFinding>();
        var unparseable = 0;

        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("journal/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            byte[] bytes;
            using (var read = await _store.OpenReadAsync(entry.Key, range: null, cancellationToken).ConfigureAwait(false))
            {
                if (read.Outcome != OpenReadOutcome.Found)
                {
                    unparseable++;
                    continue;
                }

                using var memory = new MemoryStream();
                await read.Content!.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                bytes = memory.ToArray();
            }

            try
            {
                var record = StandaloneRecordFraming.Parse(bytes);

                if (record.Header.ObjectType != ObjectType.JournalRecord)
                {
                    unparseable++;
                    continue;
                }

                var metadataKey = _hierarchy.DeriveMetadataKey(record.KeyGeneration);
                byte[] plaintext;
                try
                {
                    if (!StandaloneRecordCipher.TryOpen(record, _repositoryId, metadataKey, out plaintext))
                    {
                        unparseable++;
                        findings.Add(new DamageFinding(
                            DamageKind.CorruptRecord,
                            $"Journal record '{entry.Key}' failed authentication — treated as a live intent by collectors (08 §8)."));
                        continue;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(metadataKey);
                }

                var implied = _objectIdDeriver.Derive(ObjectType.JournalRecord, ContentHasher.Hash(plaintext));
                if (implied != record.Header.ObjectId)
                {
                    unparseable++;
                    findings.Add(new DamageFinding(
                        DamageKind.CorruptRecord,
                        $"Journal record '{entry.Key}' fails content verification (04 §6 step 7)."));
                    continue;
                }

                var decoded = JournalRecordCodec.Decode(plaintext);

                if (JournalRecordCodec.VerifyByDescent(
                    decoded.SignedBytes.Span, decoded.Signature.Span, _hierarchy, maxGeneration) is null)
                {
                    unparseable++;
                    findings.Add(new DamageFinding(
                        DamageKind.SecurityFinding,
                        $"Journal record '{entry.Key}' failed signature verification at every generation ≤ {maxGeneration}."));
                    continue;
                }

                records.Add(decoded.Record);
            }
            catch (FormatException exception)
            {
                // 08 §8: an unparseable intent means the collector is older
                // than the writer, or the record is damaged — both call for
                // the conservative reading.
                unparseable++;
                findings.Add(new DamageFinding(
                    DamageKind.CorruptRecord,
                    $"Journal record '{entry.Key}' could not be parsed and is treated as live: {exception.Message}"));
            }
        }

        return (records, unparseable, findings);
    }

    /// <inheritdoc />
    public void Dispose() => _objectIdDeriver.Dispose();
}
