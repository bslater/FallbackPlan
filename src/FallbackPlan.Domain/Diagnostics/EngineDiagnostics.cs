using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FallbackPlan.Domain.Diagnostics;

/// <summary>
/// The engine's OpenTelemetry instrumentation surface (ADR-0027 §3;
/// architecture 10 §2; NFR-OPS-001), built on the in-box
/// <see cref="Meter"/>/<see cref="ActivitySource"/> API — exporters are a
/// host concern and no exporter package enters the tree.
///
/// <para><b>Privacy bound (NFR-PRIV-002), enforced by test:</b> the only
/// attributes any instrument may attach are <c>outcome</c>, <c>reused</c>,
/// <c>class</c>, and <c>operation</c>, with closed value sets. No path,
/// filename, repository identifier, snapshot identifier, destination
/// endpoint, or anything derived from file content ever rides telemetry.
/// A new attribute fails the automated allowlist assertion until it is
/// added to ADR-0027 §3 <em>and</em> judged against NFR-PRIV-002.</para>
/// </summary>
public static class EngineDiagnostics
{
    /// <summary>The meter and activity-source name hosts subscribe to.</summary>
    public const string Name = "FallbackPlan.Engine";

    /// <summary>The attribute names any instrument may use — the exhaustive allowlist.</summary>
    public static readonly IReadOnlySet<string> AllowedAttributes =
        new HashSet<string>(StringComparer.Ordinal) { "outcome", "reused", "class", "operation" };

    private static readonly Meter Meter = new(Name);

    /// <summary>Tracing spans: <c>backup</c>, <c>scan</c>, <c>publish</c>, <c>restore</c>.</summary>
    public static readonly ActivitySource Activities = new(Name);

    /// <summary>Files scanned; attribute <c>outcome</c>: captured | reused | failed.</summary>
    public static readonly Counter<long> ScanFiles =
        Meter.CreateCounter<long>("fallbackplan.scan.files", "{file}", "Files scanned by outcome");

    /// <summary>Logical bytes scanned.</summary>
    public static readonly Counter<long> ScanBytes =
        Meter.CreateCounter<long>("fallbackplan.scan.bytes", "By", "Logical bytes scanned");

    /// <summary>Bytes presented to segmentation.</summary>
    public static readonly Counter<long> ArchiveBytesLogical =
        Meter.CreateCounter<long>("fallbackplan.archive.bytes_logical", "By", "Bytes presented to segmentation");

    /// <summary>Bytes written after reuse and compression — dedup and compression ratios derive from the pair.</summary>
    public static readonly Counter<long> ArchiveBytesStored =
        Meter.CreateCounter<long>("fallbackplan.archive.bytes_stored", "By", "Bytes stored after reuse and compression");

    /// <summary>Segments produced; attribute <c>reused</c>: true | false.</summary>
    public static readonly Counter<long> ArchiveSegments =
        Meter.CreateCounter<long>("fallbackplan.archive.segments", "{segment}", "Segments by reuse");

    /// <summary>Blobs sealed; attribute <c>class</c>: data | meta.</summary>
    public static readonly Counter<long> BlobsSealed =
        Meter.CreateCounter<long>("fallbackplan.blob.sealed", "{blob}", "Blobs sealed by class");

    /// <summary>Fill fraction at seal — the request-amplification canary (10 §2).</summary>
    public static readonly Histogram<double> BlobFillFraction =
        Meter.CreateHistogram<double>("fallbackplan.blob.fill_fraction", "1", "Blob fill fraction at seal");

    /// <summary>Provider requests; attribute <c>operation</c>: put | get | list | delete.</summary>
    public static readonly Counter<long> StoreRequests =
        Meter.CreateCounter<long>("fallbackplan.store.requests", "{request}", "Provider requests by operation");

    /// <summary>Path/dedup lookup latency (NFR-PERF-004/010).</summary>
    public static readonly Histogram<double> CatalogueLookupDuration =
        Meter.CreateHistogram<double>("fallbackplan.catalogue.lookup_duration", "us", "Catalogue lookup latency");

    /// <summary>Publication wall time, steps 1–9, per snapshot.</summary>
    public static readonly Histogram<double> PublicationDuration =
        Meter.CreateHistogram<double>("fallbackplan.publication.duration", "s", "Snapshot publication wall time");
}
