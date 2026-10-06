using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository;

/// <summary>
/// How much of a run's counted plan is backed up (FR-SVC-006, ADR-0088):
/// content the store has acknowledged, and content the run did not write
/// because the store already holds it.
/// </summary>
/// <remarks>
/// <para>
/// Content counts in the order it was archived, and only once the blob holding
/// it and every blob opened before that one have been acknowledged. Blobs
/// upload several at a time and land in any order, so the count is the plan
/// bytes archived before the oldest blob still unacknowledged — a watermark,
/// which never moves back.
/// </para>
/// <para>
/// A file contributes exactly its planned length. Its segments count up to
/// that length and no further, so a file read again because it changed under
/// the reader, or archived beside its alternate streams, never counts twice;
/// whatever its segments did not cover — a sparse file's holes, a file that
/// failed partway or shrank — counts where the file ended.
/// </para>
/// <para>
/// A segment reused from another this run claimed but has not yet appended —
/// the same content twice in flight, the later copy winning the claim —
/// counts where that claimant is appended, never ahead of the blob that will
/// hold it.
/// </para>
/// </remarks>
internal sealed class BackedUpTally
{
    private readonly Lock _gate = new();
    private readonly Dictionary<BlobId, long> _uploading = [];
    private readonly Dictionary<ObjectId, long> _awaitingClaimant = [];
    private long _archived;
    private long _alreadyStored;
    private long? _openStart;
    private long _fileBudget;
    private long _fileCounted;

    /// <summary>
    /// Called after an acknowledgement moves the count forward, outside the
    /// tally's lock and on the uploading thread.
    /// </summary>
    public Action? Advanced { get; set; }

    /// <summary>The plan's bytes backed up so far.</summary>
    public long BackedUp
    {
        get
        {
            lock (_gate)
            {
                var floor = _archived;
                if (_openStart is { } open && open < floor)
                {
                    floor = open;
                }

                foreach (var start in _uploading.Values)
                {
                    if (start < floor)
                    {
                        floor = start;
                    }
                }

                return floor + _alreadyStored;
            }
        }
    }

    /// <summary>A planned file begins: its segments may count up to <paramref name="plannedBytes"/>.</summary>
    /// <param name="plannedBytes">The file's length as the walk saw it.</param>
    public void BeginFile(long plannedBytes)
    {
        lock (_gate)
        {
            _fileBudget = Math.Max(0, plannedBytes);
            _fileCounted = 0;
        }
    }

    /// <summary>
    /// The rest of the current file is content the store already holds — an
    /// unchanged or renamed file — so it counts now.
    /// </summary>
    public void RestAlreadyStored()
    {
        lock (_gate)
        {
            _alreadyStored += _fileBudget - _fileCounted;
            _fileCounted = _fileBudget;
        }
    }

    /// <summary>The current file ends: whatever its segments did not cover counts here.</summary>
    public void EndFile()
    {
        lock (_gate)
        {
            _archived += _fileBudget - _fileCounted;

            // A claimant still unappended when its file ends never will be:
            // the file failed between claiming and appending.
            foreach (var waiting in _awaitingClaimant.Values)
            {
                _archived += waiting;
            }

            _awaitingClaimant.Clear();
            _fileBudget = 0;
            _fileCounted = 0;
        }
    }

    /// <summary>A blob was opened to append to; what is archived from here waits on it.</summary>
    public void BlobOpened()
    {
        lock (_gate)
        {
            _openStart = _archived;
        }
    }

    /// <summary>The open blob was sealed and handed to the uploaders.</summary>
    /// <param name="blobId">The sealed blob.</param>
    public void BlobSealed(BlobId blobId)
    {
        lock (_gate)
        {
            if (_openStart is { } start)
            {
                _uploading[blobId] = start;
            }

            _openStart = null;
        }
    }

    /// <summary>The store acknowledged a blob.</summary>
    /// <param name="blobId">The acknowledged blob.</param>
    public void BlobAcknowledged(BlobId blobId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _uploading.Remove(blobId);
        }

        if (removed)
        {
            Advanced?.Invoke();
        }
    }

    /// <summary>A segment's record was appended to the open blob.</summary>
    /// <param name="objectId">The record's object identifier.</param>
    /// <param name="length">Its logical length, or zero for content the plan did not count.</param>
    public void RecordAppended(ObjectId objectId, long length)
    {
        lock (_gate)
        {
            _archived += Counted(length);
            if (_awaitingClaimant.Remove(objectId, out var waiting))
            {
                _archived += waiting;
            }
        }
    }

    /// <summary>A segment needed no new record of its own.</summary>
    /// <param name="objectId">The record it refers to.</param>
    /// <param name="length">Its logical length, or zero for content the plan did not count.</param>
    /// <param name="claimantPending">
    /// Whether that record was claimed by another segment of this run and is
    /// not yet appended, so it counts when that one is.
    /// </param>
    public void SegmentReused(ObjectId objectId, long length, bool claimantPending)
    {
        lock (_gate)
        {
            var counted = Counted(length);
            if (claimantPending)
            {
                _awaitingClaimant[objectId] = _awaitingClaimant.GetValueOrDefault(objectId) + counted;
            }
            else
            {
                _archived += counted;
            }
        }
    }

    private long Counted(long length)
    {
        var counted = Math.Min(length, _fileBudget - _fileCounted);
        _fileCounted += counted;
        return counted;
    }
}
