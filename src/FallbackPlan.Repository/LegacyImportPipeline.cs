using System.Security.Cryptography;
using FallbackPlan.Import.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>One imported version: its snapshot identity and what publication produced.</summary>
public sealed record ImportedVersion(
    ReadOnlyMemory<byte> SnapshotId,
    ProvenanceRecord Provenance,
    PublishedSnapshot Published);

/// <summary>
/// Imports legacy archives through the SAME pipeline a native backup takes
/// (FR-CP-002; exit criterion 11): every version becomes an ordinary
/// publication — intent, segmentation, sealing, deltas, signed snapshot,
/// retirement — with provenance carried only in the file-version manifest's
/// <c>capture_diagnostics</c>. There is no second write path: if the import
/// pipeline diverged from the backup pipeline, imported data would age
/// differently from native data, and FR-CP-002 exists to forbid exactly that.
/// </summary>
public sealed class LegacyImportPipeline
{
    private readonly PublicationOrchestrator _orchestrator;

    /// <summary>Creates the pipeline over the one and only publication path.</summary>
    public LegacyImportPipeline(PublicationOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        _orchestrator = orchestrator;
    }

    /// <summary>
    /// Imports every version <paramref name="source"/> yields, one
    /// publication per version, oldest first so parent lineage could later
    /// be threaded. Failure stops the run at the failing version — completed
    /// snapshots stay published and durable; the rest import on retry.
    /// </summary>
    public async ValueTask<IReadOnlyList<ImportedVersion>> ImportAsync(
        ILegacyArchiveSource source,
        ReadOnlyMemory<byte> deviceId,
        ReadOnlyMemory<byte> backupSetId,
        ulong nowUnixMilliseconds,
        ulong expiryGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var imported = new List<ImportedVersion>();

        await foreach (var version in source.EnumerateVersionsAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshotId = RandomNumberGenerator.GetBytes(16);

            var content = await version.OpenContentAsync(cancellationToken).ConfigureAwait(false);
            PublishedSnapshot published;
            await using (content.ConfigureAwait(false))
            {
                published = await _orchestrator.PublishAsync(
                    new BackupJob(
                        content,
                        version.Name,
                        deviceId,
                        backupSetId,
                        snapshotId,
                        nowUnixMilliseconds,
                        DeclaredMaxDurationMs: 3_600_000,
                        expiryGeneration,
                        ClientVersion: $"import/{source.SourceName}",
                        version.Provenance.ToCaptureDiagnostics()),
                    cancellationToken).ConfigureAwait(false);
            }

            imported.Add(new ImportedVersion(snapshotId, version.Provenance, published));
        }

        return imported;
    }
}
