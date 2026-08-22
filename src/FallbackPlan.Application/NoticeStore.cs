using Bodu;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Application;

/// <summary>One durable event a human should see (architecture 10 §3.1's third channel).</summary>
public sealed record Notice
{
    /// <summary>The notice's identity, for acknowledgement.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>A stable machine key — one notice per (kind, subject), re-raised rather than duplicated.</summary>
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    /// <summary>What happened and what it asks of the operator.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>When it was raised, Unix milliseconds.</summary>
    [JsonPropertyName("raised_at")]
    public required ulong RaisedAt { get; init; }

    /// <summary>When a human acknowledged it; null while it still surfaces.</summary>
    [JsonPropertyName("acknowledged_at")]
    public ulong? AcknowledgedAt { get; init; }
}

/// <summary>
/// The durable notices ledger: <c>notices.json</c> beside <c>jobs.json</c>
/// (ADR-0010 Amendment 1). A notice is neither a state nor a moment — a
/// peering ended at 3 a.m. must still be known at breakfast, after a reboot,
/// without the condition re-occurring — so it is raised once, surfaces until
/// a human acknowledges it, and survives restarts. Sacrificial like its
/// neighbours: a lost notice is re-raised by the condition still holding.
/// </summary>
public sealed class NoticeStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly List<Notice> _notices;
    private readonly Lock _gate = new();
    private ILogger _log = NullLogger.Instance;

    private NoticeStore(string path, List<Notice> notices)
    {
        _path = path;
        _notices = notices;
    }

    /// <summary>Every notice, a snapshot of the list.</summary>
    public IReadOnlyList<Notice> Notices
    {
        get
        {
            lock (_gate)
            {
                return [.. _notices];
            }
        }
    }

    /// <summary>The notices still awaiting a human, oldest first.</summary>
    public IReadOnlyList<Notice> Unacknowledged
    {
        get
        {
            lock (_gate)
            {
                return [.. _notices.Where(notice => notice.AcknowledgedAt is null).OrderBy(notice => notice.RaisedAt)];
            }
        }
    }

    /// <summary>Opens (or creates) the ledger in <paramref name="stateDirectory"/>.</summary>
    /// <param name="stateDirectory">The state directory holding the ledger.</param>
    /// <param name="logger">Where a raised or acknowledged notice is recorded.</param>
    public static NoticeStore Open(string stateDirectory, ILogger? logger = null)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, "notices.json");

        if (!File.Exists(path))
        {
            return new NoticeStore(path, []) { _log = logger ?? NullLogger.Instance };
        }

        try
        {
            var notices = JsonSerializer.Deserialize<List<Notice>>(File.ReadAllText(path), SerializerOptions) ?? [];
            return new NoticeStore(path, notices) { _log = logger ?? NullLogger.Instance };
        }
        catch (JsonException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return new NoticeStore(path, []) { _log = logger ?? NullLogger.Instance };
        }
    }

    /// <summary>
    /// Raises a notice, or refreshes the unacknowledged one already keyed the
    /// same — a failing condition observed hourly is one notice, not a pile.
    /// </summary>
    /// <param name="key">The stable machine key: one per (kind, subject).</param>
    /// <param name="message">What happened, for the human.</param>
    /// <param name="nowUnixMilliseconds">When it was observed.</param>
    /// <returns>The notice on record.</returns>
    public Notice Raise(string key, string message, ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(key);
        ThrowHelper.ThrowIfNullOrWhiteSpace(message);

        lock (_gate)
        {
            var index = _notices.FindIndex(notice =>
                notice.AcknowledgedAt is null && string.Equals(notice.Key, key, StringComparison.Ordinal));
            if (index >= 0)
            {
                // Refresh the message, keeping the identity and the time it was
                // FIRST seen. This used to return the existing notice
                // untouched, which meant a notice carrying numbers — bytes
                // short, objects unproven — showed the first observation
                // forever while the real figures moved underneath it. The
                // original timestamp is the useful one ("since when"), so it
                // stays; the text is the one that must be current.
                var refreshed = _notices[index] with { Message = message };
                if (refreshed != _notices[index])
                {
                    _notices[index] = refreshed;
                    Save();
                }

                return refreshed;
            }

            var notice = new Notice
            {
                Id = Guid.NewGuid().ToString("n")[..8],
                Key = key,
                Message = message,
                RaisedAt = nowUnixMilliseconds,
            };
            _notices.Add(notice);
            Save();

            // Only a genuinely new notice is logged. A refresh above keeps the
            // same identity and the same "since when", so recording it again
            // would turn one durable condition into a stream of events and
            // make the log look like something was repeatedly going wrong.
            Log.NoticeRaised(_log, notice.Key, notice.Message);
            return notice;
        }
    }

    /// <summary>
    /// Withdraws the unacknowledged notice under <paramref name="key"/>,
    /// because the condition it reported has gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, every notice is permanent until a human dismisses it —
    /// which is right for a finding that must survive being ignored (a failed
    /// verification, a peering ended) and wrong for a condition that clears on
    /// its own (a drive that was unplugged and is back, a peer that was full
    /// and now is not). A backup product that cries wolf gets its warnings
    /// ignored, which costs more than the warning was worth.
    /// </para>
    /// <para>
    /// The rule this establishes: a <b>transient</b> condition belongs in the
    /// status derivation, which recomputes from current facts and therefore
    /// clears itself. A notice is for what must outlive the moment — and when
    /// even that is genuinely over, it is resolved here rather than left for
    /// someone to tidy.
    /// </para>
    /// <para>
    /// Resolving marks the notice acknowledged rather than deleting it: it
    /// stops surfacing but stays on record, the same shape a human
    /// acknowledgement leaves, so the history of what was once wrong survives.
    /// </para>
    /// </remarks>
    /// <param name="key">The stable machine key the notice was raised under.</param>
    /// <param name="nowUnixMilliseconds">When the condition was observed to have cleared.</param>
    /// <returns><see langword="false"/> when nothing was outstanding under that key.</returns>
    public bool Resolve(string key, ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            var index = _notices.FindIndex(notice =>
                notice.AcknowledgedAt is null && string.Equals(notice.Key, key, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            _notices[index] = _notices[index] with { AcknowledgedAt = nowUnixMilliseconds };
            Save();
            Log.NoticeAcknowledged(_log, _notices[index].Key);
            return true;
        }
    }

    /// <summary>Acknowledges a notice; it stops surfacing and stays on record.</summary>
    /// <param name="id">The notice's identity.</param>
    /// <param name="nowUnixMilliseconds">When the human acknowledged it.</param>
    /// <returns><see langword="false"/> when no unacknowledged notice has that identity.</returns>
    public bool Acknowledge(string id, ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(id);

        lock (_gate)
        {
            var index = _notices.FindIndex(notice =>
                notice.AcknowledgedAt is null && string.Equals(notice.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            _notices[index] = _notices[index] with { AcknowledgedAt = nowUnixMilliseconds };
            Save();
            return true;
        }
    }

    private void Save() =>
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_notices, SerializerOptions));
}
