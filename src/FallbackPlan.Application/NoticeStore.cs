using Bodu;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    public static NoticeStore Open(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, "notices.json");

        if (!File.Exists(path))
        {
            return new NoticeStore(path, []);
        }

        try
        {
            var notices = JsonSerializer.Deserialize<List<Notice>>(File.ReadAllText(path), SerializerOptions) ?? [];
            return new NoticeStore(path, notices);
        }
        catch (JsonException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return new NoticeStore(path, []);
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
            var existing = _notices.FirstOrDefault(notice =>
                notice.AcknowledgedAt is null && string.Equals(notice.Key, key, StringComparison.Ordinal));
            if (existing is not null)
            {
                return existing;
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
            return notice;
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
