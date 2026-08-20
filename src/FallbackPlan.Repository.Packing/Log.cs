using FallbackPlan.Domain.Identifiers;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// The packing layer's diagnostics (ADR-0043; event ids 1600–1699).
/// </summary>
/// <remarks>
/// The resume path is the reason this file exists. A spool that restarts
/// instead of resuming costs work and is invisible from the outside: the job
/// still completes, the snapshot is still correct, and nobody learns that the
/// checkpoint was torn until somebody asks why a nightly backup got slower.
/// Every discard reason is logged with the reason string the resume contract
/// already defines, so the answer is in the file before the question is asked.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1600, Level = LogLevel.Debug,
        Message = "Resumed spool for blob {BlobId} at record {Records}, {Bytes} bytes already sealed")]
    internal static partial void SpoolResumed(ILogger logger, BlobId blobId, int records, long bytes);

    [LoggerMessage(
        EventId = 1601, Level = LogLevel.Warning,
        Message = "Restarting the blob rather than resuming its spool: {Reason}. "
            + "Spooled work is lost; correctness is not — the restarted blob draws a fresh salt")]
    internal static partial void SpoolDiscarded(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 1602, Level = LogLevel.Trace,
        Message = "Checkpointed spool for blob {BlobId} at record {Records}")]
    internal static partial void SpoolCheckpointed(ILogger logger, BlobId blobId, int records);

    [LoggerMessage(
        EventId = 1603, Level = LogLevel.Debug,
        Message = "Swept an unresumable spool with no checkpoint beside it: {Count} file(s)")]
    internal static partial void UnresumableSwept(ILogger logger, int count);

    [LoggerMessage(
        EventId = 1610, Level = LogLevel.Debug,
        Message = "Sealed blob {BlobId}: {Records} records, {Bytes} bytes, format version {FormatVersion}")]
    internal static partial void BlobSealed(
        ILogger logger, BlobId blobId, int records, long bytes, int formatVersion);

    [LoggerMessage(
        EventId = 1611, Level = LogLevel.Trace,
        Message = "Opened blob {BlobId} for reading: {Records} records in its table")]
    internal static partial void BlobOpened(ILogger logger, BlobId blobId, int records);

    [LoggerMessage(
        EventId = 1612, Level = LogLevel.Warning,
        Message = "A sealed share did not open for blob {BlobId} — a tampered or transplanted share, "
            + "contained to this blob (ADR-0042)")]
    internal static partial void SealedShareRefused(ILogger logger, BlobId blobId);
}
