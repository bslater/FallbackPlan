using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Repository;

/// <summary>
/// The repository engine's diagnostics (ADR-0043; event ids 2000–2499).
/// </summary>
/// <remarks>
/// <para>
/// Source-generated partials rather than <c>logger.LogInformation(…)</c>: the
/// analyzers make that mandatory here (CA1848, CA2254), and the ceremony buys
/// a stable <c>EventId</c> per message plus no allocation when the level is
/// off — which matters in a loop that runs once per file.
/// </para>
/// <para>
/// Anything path-shaped is declared <see cref="LogPath"/> and anything
/// identifier-shaped keeps its own type, so the redaction boundary can do its
/// work by declared type when the record leaves the machine (ADR-0043 §4).
/// Passing a bare <see cref="string"/> path here would defeat that silently,
/// which is why the parameter types are not <c>string</c>.
/// </para>
/// </remarks>
internal static partial class Log
{
    // 2020 (blob sealed), 2040 (record read) and 2041 (record sealed) were
    // declared here and are deliberately gone rather than wired.
    //
    // 2020 duplicated Repository.Packing's 1610: blobs are sealed in Packing,
    // and two messages for one event is how a log begins to disagree with
    // itself. 2040 and 2041 sat on the per-record path — the hottest in the
    // product — and 2041 was at Debug, so a million-file backup would have
    // emitted a million Debug lines. That is a volume defect wearing a
    // diagnostic's clothes; what a person actually needs at that layer is the
    // per-file record above, which carries the counts.

    // ---------------------------------------------------------- publication

    [LoggerMessage(
        EventId = 2000, Level = LogLevel.Information,
        Message = "Publication starting for snapshot {SnapshotId} in {Repository}, writer {Writer}")]
    internal static partial void PublicationStarting(
        ILogger logger, LogId snapshotId, RepositoryId repository, WriterId writer);

    [LoggerMessage(
        EventId = 2001, Level = LogLevel.Debug,
        Message = "Publication step {Step} complete for snapshot {SnapshotId}")]
    internal static partial void PublicationStep(ILogger logger, string step, LogId snapshotId);

    [LoggerMessage(
        EventId = 2002, Level = LogLevel.Information,
        Message = "Publication complete for snapshot {SnapshotId}: {LogicalBytes} logical bytes in "
            + "{Records} records across {Blobs} blobs")]
    internal static partial void PublicationComplete(
        ILogger logger, LogId snapshotId, long logicalBytes, int records, int blobs);

    [LoggerMessage(
        EventId = 2004, Level = LogLevel.Information,
        Message = "Snapshot {SnapshotId} published: {Files} files, {Failures} failed, "
            + "{LogicalBytes} logical bytes")]
    internal static partial void SnapshotPublished(
        ILogger logger, LogId snapshotId, int files, int failures, long logicalBytes);

    [LoggerMessage(
        EventId = 2003, Level = LogLevel.Error,
        Message = "Publication failed for snapshot {SnapshotId} at step {Step}")]
    internal static partial void PublicationFailed(
        ILogger logger, LogId snapshotId, string step, Exception exception);

    // ---------------------------------------------------------------- files

    [LoggerMessage(
        EventId = 2010, Level = LogLevel.Debug,
        Message = "Captured {Path}: {Bytes} bytes, {Segments} segments, {Written} records written")]
    internal static partial void FileCaptured(
        ILogger logger, LogPath path, long bytes, int segments, int written);

    [LoggerMessage(
        EventId = 2011, Level = LogLevel.Debug,
        Message = "Unchanged since the last snapshot, reusing its manifest: {Path}")]
    internal static partial void FileUnchanged(ILogger logger, LogPath path);

    [LoggerMessage(
        EventId = 2012, Level = LogLevel.Warning,
        Message = "Could not capture {Path}: {Reason} — the job continues and the snapshot records the gap")]
    internal static partial void FileFailed(ILogger logger, LogPath path, string reason);

    // ---------------------------------------------------------------- blobs

    [LoggerMessage(
        EventId = 2021, Level = LogLevel.Warning,
        Message = "Skipping blob {Key} while loading: {Reason} — contained to this blob, "
            + "every other blob still loads")]
    internal static partial void BlobSkipped(ILogger logger, ObjectKey key, string reason);

    [LoggerMessage(
        EventId = 2022, Level = LogLevel.Debug,
        Message = "Loaded {Blobs} blobs from {Repository}, {Skipped} skipped")]
    internal static partial void BlobsLoaded(ILogger logger, int blobs, RepositoryId repository, int skipped);

    // ------------------------------------------------------------ lifecycle

    [LoggerMessage(
        EventId = 2030, Level = LogLevel.Information,
        Message = "Repository {Repository} created at format version {FormatVersion}, write-only {WriteOnly}")]
    internal static partial void RepositoryCreated(
        ILogger logger, RepositoryId repository, int formatVersion, bool writeOnly);

    [LoggerMessage(
        EventId = 2031, Level = LogLevel.Information,
        Message = "Repository {Repository} opened at format version {FormatVersion}, write-only {WriteOnly}")]
    internal static partial void RepositoryOpened(
        ILogger logger, RepositoryId repository, int formatVersion, bool writeOnly);

    [LoggerMessage(
        EventId = 2032, Level = LogLevel.Warning,
        Message = "Repository {Repository} refused to open: {Reason}")]
    internal static partial void RepositoryOpenRefused(ILogger logger, RepositoryId repository, string reason);

    // ----------------------------------------------------------------- read
}
