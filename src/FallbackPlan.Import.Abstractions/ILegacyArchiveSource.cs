using System.Globalization;

namespace FallbackPlan.Import.Abstractions;

/// <summary>
/// Where an imported version came from (FR-CP-002). Provenance is carried as
/// <c>capture_diagnostics</c> strings on the file-version manifest (06 §4
/// key 13) — the one place import history may live, because everywhere else
/// an imported version MUST be format-indistinguishable from a native one.
/// </summary>
/// <param name="SourceSystem">The legacy system's own short name, chosen by the importer that read it.</param>
/// <param name="SourceIdentifier">The legacy archive's own identifier for this version.</param>
/// <param name="OriginalModifiedAtUnixMs">The file's modification time as the legacy archive recorded it.</param>
/// <param name="OriginalCapturedAtUnixMs">When the legacy archive captured the version.</param>
public sealed record ProvenanceRecord(
    string SourceSystem,
    string SourceIdentifier,
    ulong? OriginalModifiedAtUnixMs,
    ulong? OriginalCapturedAtUnixMs)
{
    /// <summary>
    /// Renders the provenance as the <c>capture_diagnostics</c> strings the
    /// manifest carries. Stable prefixes, one fact per string, so later
    /// tooling can parse without a schema change.
    /// </summary>
    public IReadOnlyList<string> ToCaptureDiagnostics()
    {
        var diagnostics = new List<string>
        {
            $"imported-from: {SourceSystem}",
            $"legacy-id: {SourceIdentifier}",
        };

        if (OriginalModifiedAtUnixMs is { } modified)
        {
            diagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"legacy-modified-at-ms: {modified}"));
        }

        if (OriginalCapturedAtUnixMs is { } captured)
        {
            diagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"legacy-captured-at-ms: {captured}"));
        }

        return diagnostics;
    }
}

/// <summary>
/// One file version a legacy archive can produce: its name, its provenance,
/// and a way to open its content as a stream. The content is a plain byte
/// stream — whatever decryption or decompression the legacy format needs
/// happens inside the source, behind this seam, outside the trust boundary
/// of the engine (T-15).
/// </summary>
public sealed record LegacyFileVersion(
    ReadOnlyMemory<byte> Name,
    ProvenanceRecord Provenance,
    Func<CancellationToken, ValueTask<Stream>> OpenContentAsync);

/// <summary>
/// A legacy archive that can be imported (FR-CP-002; exit criterion 11).
/// Implementations — one per legacy format, plus a synthetic test source —
/// live in their own packages and depend only on this assembly and Domain;
/// the engine never learns which one fed it. That ignorance is the point: an
/// imported version must be format-indistinguishable from a native one, and
/// the surest way to keep it so is for the core to have no vocabulary for the
/// difference.
/// </summary>
public interface ILegacyArchiveSource
{
    /// <summary>The source system name recorded in provenance; the importer names its own format.</summary>
    string SourceName { get; }

    /// <summary>Enumerates every file version the legacy archive holds, oldest first.</summary>
    IAsyncEnumerable<LegacyFileVersion> EnumerateVersionsAsync(CancellationToken cancellationToken);
}
