using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository;

/// <summary>
/// How much of a run's counted plan is backed up (FR-SVC-006, ADR-0088 and
/// its Amendment 1), in bytes and in files: content the store has
/// acknowledged, and content the run did not write because the store already
/// holds it.
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
/// <para>
/// A file counts once everything archived up to its end has been
/// acknowledged, measured in records rather than bytes: a record the plan
/// does not count, such as an alternate stream's, still waits for its blob.
/// An unchanged or renamed file counts at once, and a file that failed never
/// does, since none of it reached the store.
/// </para>
/// </remarks>
internal sealed class BackedUpTally
{
    private readonly Lock _gate = new();
    private readonly Dictionary<BlobId, Position> _uploading = [];
    private readonly Dictionary<ObjectId, long> _awaitingClaimant = [];
    private readonly Queue<long> _fileEnds = new();
    private long _archived;
    private long _records;
    private long _alreadyStored;
    private Position? _openStart;
    private long _fileBudget;
    private long _fileCounted;
    private bool _fileAlreadyStored;
    private long _filesBackedUp;

    /// <summary>
    /// Called after an acknowledgement moves the count forward, outside the
    /// tally's lock and on the uploading thread.
    /// </summary>
    public Action? Advanced { get; set; }

    /// <summary>The plan's bytes backed up so far.</summary>
    public long BackedUp => Read().Bytes;

    /// <summary>The plan's files backed up so far.</summary>
    public long FilesBackedUp => Read().Files;

    /// <summary>Both measures, taken together so a report never pairs two moments.</summary>
    public (long Bytes, long Files) Read()
    {
        lock (_gate)
        {
            var bytes = _archived;
            var records = _records;
            if (_openStart is { } open)
            {
                bytes = Math.Min(bytes, open.Bytes);
                records = Math.Min(records, open.Records);
            }

            foreach (var start in _uploading.Values)
            {
                bytes = Math.Min(bytes, start.Bytes);
                records = Math.Min(records, start.Records);
            }

            while (_fileEnds.TryPeek(out var end) && end <= records)
            {
                _fileEnds.Dequeue();
                _filesBackedUp++;
            }

            return (bytes + _alreadyStored, _filesBackedUp);
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
            _fileAlreadyStored = false;
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
            _fileAlreadyStored = true;
        }
    }

    /// <summary>The current file ends: whatever its segments did not cover counts here.</summary>
    /// <param name="published">Whether the file made it into the snapshot, rather than failing.</param>
    public void EndFile(bool published)
    {
        lock (_gate)
        {
            _archived += _fileBudget - _fileCounted;
            if (published && _fileAlreadyStored)
            {
                _filesBackedUp++;
            }
            else if (published)
            {
                _fileEnds.Enqueue(_records);
            }

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
            _openStart = new Position(_archived, _records);
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
            _records++;
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

    /// <summary>A point in the archive order: the plan's bytes archived, and records appended, before it.</summary>
    private readonly record struct Position(long Bytes, long Records);
}
