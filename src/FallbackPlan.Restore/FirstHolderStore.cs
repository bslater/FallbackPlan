using FallbackPlan.Repository;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Restore;

/// <summary>
/// A read-only view over a store and the other copies of its blobs that
/// answers each key from the first of them holding it: the store first, then
/// each copy in order, a copy opened only when a key reaches it. What a plan
/// probes through so that it counts a file as missing only when no copy holds
/// it (FR-RST-003, FR-RST-007).
/// </summary>
/// <remarks>
/// <para>
/// Presence only: a plan asks whether a blob is there, and reads the
/// manifests it needs from wherever the metadata blob is found. Nothing here
/// judges whether a copy is sound — a run reads records through the verifying
/// path whichever copy they come from. Once a key is found somewhere, its
/// reads go there, so a manifest is read from the copy that was probed.
/// </para>
/// <para>
/// A copy that holds a key only in an archival tier is passed over for one
/// that holds it readable, as a run reads around it (ADR-0103). Only when
/// every copy that holds it holds it archived is the archived answer given.
/// </para>
/// </remarks>
internal sealed class FirstHolderStore(IObjectStore own, IReadOnlyList<CopySource> others) : IObjectStore
{
    private readonly Dictionary<int, Task<IObjectStore?>> _opened = [];
    private readonly Dictionary<ObjectKey, IObjectStore> _holders = [];

    /// <inheritdoc />
    public StoreCapabilities Capabilities => own.Capabilities;

    /// <inheritdoc />
    public async ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        var held = await own.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        if (held is { Found: true, Metadata.Archived: false })
        {
            _holders[key] = own;
            return held;
        }

        var archived = held.Found ? (Answer: held, At: own) : default((GetMetadataResult Answer, IObjectStore At)?);

        for (var index = 0; index < others.Count; index++)
        {
            if (await OpenAsync(index, cancellationToken).ConfigureAwait(false) is not { } copy)
            {
                continue;
            }

            GetMetadataResult atCopy;
            try
            {
                atCopy = await copy.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A copy that cannot answer is one that did not hold it; the
                // next may.
                continue;
            }

            if (atCopy is { Found: true, Metadata.Archived: false })
            {
                _holders[key] = copy;
                return atCopy;
            }

            if (atCopy.Found)
            {
                archived ??= (atCopy, copy);
            }
        }

        if (archived is { } first)
        {
            _holders[key] = first.At;
            return first.Answer;
        }

        return held;
    }

    /// <inheritdoc />
    public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
        (_holders.TryGetValue(key, out var holder) ? holder : own).OpenReadAsync(key, range, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
        own.ListAsync(prefix, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("A plan's view over the copies only reads.");

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(
        ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        throw new NotSupportedException("A plan's view over the copies only reads.");

    /// <summary>A copy's store, opened on first use and remembered; null when it cannot be reached.</summary>
    private async ValueTask<IObjectStore?> OpenAsync(int index, CancellationToken cancellationToken)
    {
        if (!_opened.TryGetValue(index, out var opening))
        {
            opening = others[index].OpenAsync(cancellationToken).AsTask();
            _opened[index] = opening;
        }

        try
        {
            return await opening.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
