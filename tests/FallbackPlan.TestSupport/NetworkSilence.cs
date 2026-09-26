using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net;
using System.Text;

namespace FallbackPlan.TestSupport;

/// <summary>One event the runtime's own network instrumentation wrote.</summary>
/// <param name="Source">The event source's name, e.g. <c>System.Net.Sockets</c>.</param>
/// <param name="Event">The event's name, e.g. <c>ConnectStart</c>.</param>
/// <param name="Payload">Its payload, name and value, exactly as written.</param>
public sealed record NetworkEvent(
    string Source, string Event, IReadOnlyList<KeyValuePair<string, string>> Payload)
{
    /// <summary>One line naming this event and everything it carried.</summary>
    public string Describe() =>
        $"{Source}/{Event}"
        + (Payload.Count == 0
            ? string.Empty
            : " " + string.Join(" ", Payload.Select(p => $"{p.Key}={p.Value}")));

    /// <summary>
    /// The endpoint this event names, as the runtime writes it:
    /// <c>{family}:{length}:{b,b,…}</c> — for example
    /// <c>Unix:74:{47,116,109,112,…}</c> for a socket file under <c>/tmp</c>,
    /// or <c>InterNetwork:16:{…}</c> for an IPv4 address. Null for the events
    /// that carry no endpoint, such as a connect's completion.
    /// </summary>
    public string? Address =>
        Payload.FirstOrDefault(p => string.Equals(p.Key, "address", StringComparison.Ordinal)).Value;

    /// <summary>
    /// The address family <see cref="Address"/> names — <c>Unix</c>,
    /// <c>InterNetwork</c>, <c>InterNetworkV6</c> — or null when there is no
    /// address to read it from.
    /// </summary>
    public string? Family
    {
        get
        {
            if (Address is null)
            {
                return null;
            }

            var colon = Address.IndexOf(':', StringComparison.Ordinal);
            return colon < 0 ? Address : Address[..colon];
        }
    }

    /// <summary>
    /// Whether this event names an address on an IP network — the question
    /// NFR-PRIV-001 asks. A Unix domain socket is a path on this machine's own
    /// filesystem and leaves nothing.
    /// </summary>
    public bool IsInternet =>
        string.Equals(Family, "InterNetwork", StringComparison.Ordinal)
        || string.Equals(Family, "InterNetworkV6", StringComparison.Ordinal);

    /// <summary>
    /// The filesystem path a Unix address names, decoded from the bytes the
    /// runtime printed; null for any other family. The trailing NUL the
    /// address is padded with is dropped.
    /// </summary>
    public string? UnixPath =>
        string.Equals(Family, "Unix", StringComparison.Ordinal) && Bytes() is { } bytes
            ? Encoding.UTF8.GetString(bytes).TrimEnd('\0')
            : null;

    /// <summary>
    /// The IP endpoint this event names, decoded from the same bytes; null for
    /// any other family, or when the address is too short to hold one. The
    /// layout is the platform's <c>sockaddr</c> with its two-byte family
    /// header already stripped by the runtime's own printing: port first, in
    /// network order, then the address.
    /// </summary>
    /// <remarks>
    /// An IPv4-mapped IPv6 address, <c>::ffff:a.b.c.d</c>, comes back as the
    /// IPv4 endpoint it carries. It is what a dual-mode socket — the runtime's
    /// default on any host with an IPv6 stack — writes when it dials an IPv4
    /// destination, and the packets go to that IPv4 destination. Only a mapped
    /// address is unwrapped: <see cref="IPAddress.MapToIPv4"/> would turn
    /// <c>::1</c> into <c>0.0.0.1</c> without complaint. <see cref="Family"/>
    /// still reports what the runtime wrote.
    /// </remarks>
    public IPEndPoint? InternetEndpoint
    {
        get
        {
            if (!IsInternet || Bytes() is not { } bytes || bytes.Length < 8)
            {
                return null;
            }

            var port = (bytes[0] << 8) | bytes[1];

            if (string.Equals(Family, "InterNetwork", StringComparison.Ordinal))
            {
                return new IPEndPoint(new IPAddress(bytes[2..6]), port);
            }

            if (bytes.Length < 26)
            {
                return null;
            }

            var address = new IPAddress(bytes[6..22]);
            return new IPEndPoint(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address, port);
        }
    }

    /// <summary>The address's bytes as the runtime printed them, or null when it printed none.</summary>
    private byte[]? Bytes()
    {
        if (Address is null)
        {
            return null;
        }

        var open = Address.IndexOf('{', StringComparison.Ordinal);
        var close = Address.LastIndexOf('}');
        if (open < 0 || close <= open)
        {
            return null;
        }

        var bytes = new List<byte>();
        foreach (var token in Address[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!byte.TryParse(token.Trim(), out var value))
            {
                return null;
            }

            bytes.Add(value);
        }

        return [.. bytes];
    }
}

/// <summary>
/// Records everything the runtime's own network instrumentation writes while
/// this listener is alive — every socket connect, every name resolution,
/// every HTTP request, every TLS handshake — so a test can assert what a run
/// did on the wire instead of arguing about what the code could do.
///
/// This is the in-process form of NFR-PRIV-001's acceptance criterion,
/// "verified by network capture". It is not a packet capture: it observes
/// THIS process, and it cannot see a child process or another program on the
/// machine. That limit is why the requirement is also defended structurally,
/// by ArchitectureTests/TelemetrySilenceTests — a build whose package list
/// cannot grow in silence and whose libraries cannot reach the network is
/// what makes one observed run worth generalising from.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is filtered on the way in. A listener that decides what is
/// interesting while it records can only ever confirm what its author already
/// expected, and the payload these sources carry is a runtime internal that
/// has changed shape across versions — so every event is kept whole, with its
/// payload names and values as written, and the questions are asked
/// afterwards. <see cref="Report"/> prints the lot, which is what makes a
/// failure name what was dialled rather than only that something was.
/// </para>
/// <para>
/// The queue is created lazily and not by a field initialiser. The
/// <see cref="EventListener"/> base constructor calls
/// <see cref="OnEventSourceCreated"/> for every source that already exists —
/// which happens BEFORE this class's own field initialisers run, so a field
/// initialiser would still be null in that callback.
/// </para>
/// </remarks>
public sealed class NetworkSilence : EventListener
{
    /// <summary>
    /// The runtime's network event sources. Sockets carries connect and
    /// accept, NameResolution carries DNS, Http carries requests, and
    /// Security carries TLS handshakes — between them, every way a managed
    /// process reaches another machine.
    /// </summary>
    public static readonly string[] Sources =
    [
        "System.Net.Sockets",
        "System.Net.NameResolution",
        "System.Net.Http",
        "System.Net.Security",
    ];

    private ConcurrentQueue<NetworkEvent>? _events;

    private ConcurrentQueue<NetworkEvent> Events
    {
        get
        {
            var existing = _events;
            if (existing is not null)
            {
                return existing;
            }

            Interlocked.CompareExchange(ref _events, new ConcurrentQueue<NetworkEvent>(), null);
            return _events;
        }
    }

    /// <summary>Everything recorded so far, oldest first.</summary>
    public IReadOnlyList<NetworkEvent> Recorded => [.. Events];

    /// <summary>Every event whose name says a connection was attempted.</summary>
    public IReadOnlyList<NetworkEvent> Connects =>
        [.. Recorded.Where(e => e.Event.StartsWith("ConnectStart", StringComparison.Ordinal))];

    /// <summary>Every name resolution attempted.</summary>
    public IReadOnlyList<NetworkEvent> Resolutions =>
        [.. Recorded.Where(e =>
            string.Equals(e.Source, "System.Net.NameResolution", StringComparison.Ordinal)
            && e.Event.StartsWith("ResolutionStart", StringComparison.Ordinal))];

    /// <summary>Every HTTP request started.</summary>
    public IReadOnlyList<NetworkEvent> HttpRequests =>
        [.. Recorded.Where(e => string.Equals(e.Source, "System.Net.Http", StringComparison.Ordinal))];

    /// <summary>Every recorded event, one per line, for a failure message.</summary>
    public string Report() =>
        Recorded.Count == 0
            ? "  (nothing was recorded)"
            : "  " + string.Join("\n  ", Recorded.Select(e => e.Describe()));

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        ArgumentNullException.ThrowIfNull(eventSource);

        if (!Sources.Contains(eventSource.Name, StringComparer.Ordinal))
        {
            return;
        }

        EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        var values = eventData.Payload;
        var names = eventData.PayloadNames;
        var payload = new List<KeyValuePair<string, string>>(values?.Count ?? 0);

        for (var i = 0; i < (values?.Count ?? 0); i++)
        {
            payload.Add(new KeyValuePair<string, string>(
                names is not null && i < names.Count ? names[i] : $"[{i}]",
                values![i]?.ToString() ?? string.Empty));
        }

        Events.Enqueue(new NetworkEvent(
            eventData.EventSource?.Name ?? string.Empty, eventData.EventName ?? string.Empty, payload));
    }
}
