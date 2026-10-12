using Bodu;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository.Index.Journal;

/// <summary>
/// What accounts for a writer's sequence numbers besides its deltas and void
/// deltas ([ADR-0022](../../../docs/adr/0022-standalone-metadata-records-and-index-identifiers.md)
/// Decision 7, amended 2026-10): a journal record at the number, or a blob
/// that embeds the number and that any intent or extension names, whether
/// or not the blob still exists. The index loader takes it as its sequence
/// accounting (specification 07 §4).
/// </summary>
/// <remarks>
/// <para>
/// A named blob counts after it is gone because a collector removes
/// unreachable blobs, among them those of a backup that died after its
/// uploads. The writer marked those numbers accounted when each put was
/// acknowledged, so no void delta follows, and the intent that named them is
/// the only record of them left. A delta's number is never named as a blob,
/// so a dropped delta still shows as a gap.
/// </para>
/// <para>
/// Only records that open and verify count. An object under a journal key
/// that does not would otherwise close the gap a dropped delta leaves, which
/// is the truncation the gap scan exists to see.
/// </para>
/// </remarks>
public sealed class SequenceAccounting
{
    private readonly HashSet<(WriterId Writer, ulong Sequence)> _records;
    private readonly HashSet<BlobId> _named;

    private SequenceAccounting(HashSet<(WriterId Writer, ulong Sequence)> records, HashSet<BlobId> named)
    {
        _records = records;
        _named = named;
    }

    /// <summary>Reads the journal once and holds what its records account for.</summary>
    /// <param name="store">The repository, or the metadata plane of a direct-ship set.</param>
    /// <param name="repositoryId">The repository's identity.</param>
    /// <param name="credential">The credential the journal opens and verifies under.</param>
    /// <param name="maxGeneration">The highest key generation a record may be sealed at.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The accounting.</returns>
    public static async ValueTask<SequenceAccounting> LoadAsync(
        IObjectStore store,
        RepositoryId repositoryId,
        RepositoryWriteCredential credential,
        uint maxGeneration,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(credential);

        using var reader = new JournalReader(store, repositoryId, credential);
        var (records, _, _) = await reader.LoadAsync(maxGeneration, cancellationToken).ConfigureAwait(false);

        var numbers = new HashSet<(WriterId Writer, ulong Sequence)>();
        var named = new HashSet<BlobId>();
        foreach (var record in records)
        {
            numbers.Add((record.WriterId, record.Sequence));
            switch (record.Payload)
            {
                case JournalPayload.WriteIntent intent:
                    named.UnionWith(intent.IntendedBlobIds);
                    break;
                case JournalPayload.IntentExtension extension:
                    named.UnionWith(extension.AdditionalBlobIds);
                    break;
                default:
                    break;
            }
        }

        return new SequenceAccounting(numbers, named);
    }

    /// <summary>
    /// Whether <paramref name="sequence"/> of <paramref name="writer"/> is
    /// accounted for, in the shape <see cref="IndexLoader.LoadAsync"/> takes.
    /// </summary>
    /// <param name="writer">The writer whose number it is.</param>
    /// <param name="sequence">The number.</param>
    /// <returns>True when a journal record or a named blob accounts for it.</returns>
    public ValueTask<bool> IsAccountedAsync(WriterId writer, ulong sequence) =>
        ValueTask.FromResult(
            _records.Contains((writer, sequence)) || _named.Contains(BlobId.FromWriterCounter(writer, sequence)));
}
