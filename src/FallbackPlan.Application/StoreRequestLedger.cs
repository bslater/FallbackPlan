using Bodu;
using FallbackPlan.Domain;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FallbackPlan.Application;

/// <summary>
/// What one destination's store was asked over the window a bill is read in
/// (FR-DEST-019, ADR-0111): the requests sent, by operation, and the bytes
/// they carried both ways.
/// </summary>
/// <param name="Puts">Puts sent.</param>
/// <param name="Gets">Gets sent: whole objects and ranges alike.</param>
/// <param name="Heads">Heads sent.</param>
/// <param name="Lists">Listing pages asked for.</param>
/// <param name="Deletes">Deletes sent.</param>
/// <param name="BytesSent">The bodies the requests carried.</param>
/// <param name="BytesReceived">The bodies the store said its answers carried.</param>
/// <param name="Since">
/// When counting began, Unix milliseconds, where that falls inside the
/// window; null when the count covers the whole of it.
/// </param>
public sealed record StoreRequestWindow(
    long Puts, long Gets, long Heads, long Lists, long Deletes, long BytesSent, long BytesReceived, ulong? Since);

/// <summary>One UTC day's requests to one destination's store.</summary>
internal sealed class StoreRequestDay
{
    [JsonPropertyName("day")]
    public required DateOnly Day { get; init; }

    [JsonPropertyName("put")]
    public long Puts { get; set; }

    [JsonPropertyName("get")]
    public long Gets { get; set; }

    [JsonPropertyName("head")]
    public long Heads { get; set; }

    [JsonPropertyName("list")]
    public long Lists { get; set; }

    [JsonPropertyName("delete")]
    public long Deletes { get; set; }

    [JsonPropertyName("sent")]
    public long BytesSent { get; set; }

    [JsonPropertyName("received")]
    public long BytesReceived { get; set; }
}

/// <summary>One destination's days.</summary>
internal sealed record StoreRequestDestination
{
    [JsonPropertyName("destination")]
    public required string DestinationId { get; init; }

    [JsonPropertyName("days")]
    public required List<StoreRequestDay> Days { get; init; }
}

/// <summary>The on-disk shape of <c>store-requests.json</c>.</summary>
internal sealed record StoreRequestFile
{
    /// <summary>The shape this file was written under.</summary>
    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    /// <summary>When counting began, Unix milliseconds.</summary>
    [JsonPropertyName("began")]
    public required ulong Began { get; init; }

    /// <summary>One record per destination asked anything inside the window.</summary>
    [JsonPropertyName("destinations")]
    public required List<StoreRequestDestination>? Destinations { get; init; }
}

/// <summary>
/// Every request each cloud destination's store was sent, by UTC day:
/// <c>store-requests.json</c> beside the destination ledger (FR-DEST-019,
/// ADR-0111). Sacrificial, like everything beside it: losing it begins the
/// count again, and the window then says when it began.
/// </summary>
/// <remarks>
/// <para>
/// Counted by destination, not by set, because a store bills its owner for
/// every request whichever set sent it. Only the days the window can still
/// read are kept.
/// </para>
/// <para>
/// A store is sent thousands of requests an hour, so the file is written at
/// most once a <c>writeInterval</c>, and closing the ledger writes what the
/// interval held back. A service that dies unstopped loses at most that
/// interval's count. A file that cannot be written loses nothing until the
/// ledger closes: the count is held, and the next write carries it.
/// </para>
/// </remarks>
public sealed class StoreRequestLedger : IDisposable
{
    /// <summary>The days a window spans, today among them.</summary>
    public const int WindowDays = 30;

    /// <summary>The shape this build writes.</summary>
    private const int CurrentSchemaVersion = 1;

    private static readonly TimeSpan DefaultWriteInterval = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _writeInterval;
    private readonly ulong _began;
    private readonly Dictionary<string, List<StoreRequestDay>> _destinations;
    private readonly Lock _gate = new();
    private DateTimeOffset? _writtenAt;
    private bool _unwritten;

    private StoreRequestLedger(
        string path, TimeProvider clock, TimeSpan writeInterval, ulong began, Dictionary<string, List<StoreRequestDay>> destinations)
    {
        _path = path;
        _clock = clock;
        _writeInterval = writeInterval;
        _began = began;
        _destinations = destinations;
    }

    /// <summary>Opens (or begins) the ledger in <paramref name="stateDirectory"/>.</summary>
    /// <param name="stateDirectory">The directory holding <c>store-requests.json</c>.</param>
    /// <param name="clock">The clock days are told by; the system's when null.</param>
    /// <param name="writeInterval">The least time between writes of the file; thirty seconds when null.</param>
    public static StoreRequestLedger Open(string stateDirectory, TimeProvider? clock = null, TimeSpan? writeInterval = null)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, "store-requests.json");
        clock ??= TimeProvider.System;
        var interval = writeInterval ?? DefaultWriteInterval;

        if (File.Exists(path))
        {
            try
            {
                var file = JsonSerializer.Deserialize<StoreRequestFile>(AtomicFile.ReadAllText(path), SerializerOptions);
                if (file is not null && file.SchemaVersion <= CurrentSchemaVersion)
                {
                    return new StoreRequestLedger(
                        path, clock, interval, file.Began,
                        (file.Destinations ?? []).ToDictionary(
                            destination => destination.DestinationId, destination => destination.Days, StringComparer.Ordinal));
                }
            }
            catch (JsonException)
            {
                // Unreadable: set aside below, as a newer build's file is.
            }

            Quarantine(path);
        }

        return new StoreRequestLedger(
            path, clock, interval, (ulong)clock.GetUtcNow().ToUnixTimeMilliseconds(), new(StringComparer.Ordinal));
    }

    /// <summary>
    /// Counts one request sent to a destination's store, on today's UTC day.
    /// An operation no store bills by is not counted, nor are its bytes.
    /// </summary>
    /// <param name="destinationId">The destination whose store was sent it.</param>
    /// <param name="operation"><c>put</c>, <c>get</c>, <c>head</c>, <c>list</c> or <c>delete</c>.</param>
    /// <param name="bytesSent">The request body's length.</param>
    /// <param name="bytesReceived">The length the store said its answer's body was.</param>
    public void Record(string destinationId, string operation, long bytesSent, long bytesReceived)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(destinationId);

        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (!_destinations.TryGetValue(destinationId, out var days))
            {
                days = [];
                _destinations[destinationId] = days;
            }

            var day = days.Find(held => held.Day == today);
            if (day is null)
            {
                day = new StoreRequestDay { Day = today };
                days.Add(day);
            }

            switch (operation)
            {
                case "put":
                    day.Puts++;
                    break;
                case "get":
                    day.Gets++;
                    break;
                case "head":
                    day.Heads++;
                    break;
                case "list":
                    day.Lists++;
                    break;
                case "delete":
                    day.Deletes++;
                    break;
                default:
                    return;
            }

            day.BytesSent += bytesSent;
            day.BytesReceived += bytesReceived;
            _unwritten = true;
            if (_writtenAt is not { } written || now - written >= _writeInterval)
            {
                WriteLocked(now);
            }
        }
    }

    /// <summary>What the destination's store was asked over the thirty UTC days ending today.</summary>
    /// <param name="destinationId">The destination.</param>
    public StoreRequestWindow Window(string destinationId)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(destinationId);

        lock (_gate)
        {
            var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
            var first = today.AddDays(1 - WindowDays);
            var opens = (ulong)new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds();
            var inWindow = _destinations.TryGetValue(destinationId, out var days)
                ? days.Where(day => day.Day >= first && day.Day <= today).ToList()
                : [];

            return new StoreRequestWindow(
                inWindow.Sum(day => day.Puts),
                inWindow.Sum(day => day.Gets),
                inWindow.Sum(day => day.Heads),
                inWindow.Sum(day => day.Lists),
                inWindow.Sum(day => day.Deletes),
                inWindow.Sum(day => day.BytesSent),
                inWindow.Sum(day => day.BytesReceived),
                _began > opens ? _began : null);
        }
    }

    /// <summary>Writes what the interval has held back.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_unwritten)
            {
                WriteLocked(_clock.GetUtcNow());
            }
        }
    }

    /// <summary>Writes what the interval has held back.</summary>
    public void Dispose() => Flush();

    private static void Quarantine(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // As the ledger's: an empty count is the same recovery either
            // way, and a service that will not start is worse than either.
        }
    }

    private void WriteLocked(DateTimeOffset now)
    {
        var first = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1 - WindowDays);
        foreach (var (destinationId, days) in _destinations.ToList())
        {
            days.RemoveAll(day => day.Day < first);
            if (days.Count == 0)
            {
                _destinations.Remove(destinationId);
            }
        }

        // Stamped whether or not the write lands: a disk that refused it is
        // not asked again on every request, and the count waits in memory.
        _writtenAt = now;
        try
        {
            AtomicFile.WriteAllText(
                _path,
                JsonSerializer.Serialize(
                    new StoreRequestFile
                    {
                        SchemaVersion = CurrentSchemaVersion,
                        Began = _began,
                        Destinations =
                        [
                            .. _destinations
                                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                .Select(pair => new StoreRequestDestination { DestinationId = pair.Key, Days = pair.Value }),
                        ],
                    },
                    SerializerOptions));
            _unwritten = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Told on the sending path: a count that cannot be written must
            // never fail the request it counts.
        }
    }
}
