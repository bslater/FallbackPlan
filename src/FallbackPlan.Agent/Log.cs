using Microsoft.Extensions.Logging;

namespace FallbackPlan.Agent;

/// <summary>
/// Service-host diagnostics (ADR-0043; event ids 3700–3999): the scheduler's
/// lane, the remote binding's connection lifecycle, and the pairing exchange.
/// </summary>
/// <remarks>
/// <para>
/// This file replaces the two untyped delegates the Agent used to carry — an
/// <c>Action&lt;string, Exception?&gt;</c> on <c>ServiceOptions</c> and an
/// <c>Action&lt;string&gt;</c> threaded into the listeners. Both wrote a bare
/// local timestamp to a <c>TextWriter</c> with no level, no category and no
/// structure, which is the shape of logging that cannot be filtered, cannot
/// be read by a client, and cannot be turned down when it is noisy.
/// </para>
/// <para>
/// A peer is named by <b>fingerprint</b> and never by address. The
/// fingerprint is the identity that was authenticated; an address is where a
/// packet came from, and the two answer different questions (ADR-0030).
/// </para>
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 3700, Level = LogLevel.Error,
        Message = "Job {JobId} ({Description}) failed past its own handler")]
    internal static partial void JobFaulted(ILogger logger, string jobId, string description, Exception exception);

    [LoggerMessage(
        EventId = 3710, Level = LogLevel.Information,
        Message = "Remote peer authenticated: {Fingerprint}")]
    internal static partial void PeerAuthenticated(ILogger logger, string fingerprint);

    [LoggerMessage(
        EventId = 3711, Level = LogLevel.Warning,
        Message = "Remote connection refused: {Reason} — {Detail}")]
    internal static partial void RemoteRefused(ILogger logger, string reason, string detail);

    [LoggerMessage(
        EventId = 3712, Level = LogLevel.Debug,
        Message = "Remote connection ended: {Reason}")]
    internal static partial void RemoteEnded(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 3713, Level = LogLevel.Warning,
        Message = "Pairing offered but this listener holds no state directory; closing")]
    internal static partial void PairingWithoutState(ILogger logger);

    [LoggerMessage(
        EventId = 3714, Level = LogLevel.Information,
        Message = "Peer paired by invite: '{Label}' ({Fingerprint})")]
    internal static partial void PeerPaired(ILogger logger, string label, string fingerprint);

    [LoggerMessage(
        EventId = 3715, Level = LogLevel.Warning,
        Message = "Invite pairing refused: {Reason} — {Detail}")]
    internal static partial void PairingRefused(ILogger logger, string reason, string detail);

    [LoggerMessage(
        EventId = 3720, Level = LogLevel.Warning,
        Message = "Replication offered by {Fingerprint} but this service holds no replicas; closing")]
    internal static partial void ReplicationWithoutReplicas(ILogger logger, string fingerprint);

    [LoggerMessage(
        EventId = 3721, Level = LogLevel.Information,
        Message = "Retrieval session served for {Fingerprint}")]
    internal static partial void RetrievalServed(ILogger logger, string fingerprint);

    [LoggerMessage(
        EventId = 3722, Level = LogLevel.Information,
        Message = "Peering terminated by {Fingerprint}")]
    internal static partial void PeeringTerminated(ILogger logger, string fingerprint);

    /// <remarks>
    /// The repository is deliberately not named. At this call site its id is
    /// already a hex string, so redaction by declared type has nothing to
    /// bite on (NFR-SEC-006) — and a repository identifier is correlatable
    /// (NFR-PRIV-002). The peer and the count are what a support question
    /// actually asks for.
    /// </remarks>
    [LoggerMessage(
        EventId = 3723, Level = LogLevel.Information,
        Message = "Replicated {Committed} object(s) for {Fingerprint}")]
    internal static partial void Replicated(ILogger logger, long committed, string fingerprint);

    [LoggerMessage(
        EventId = 3730, Level = LogLevel.Information,
        Message = "Service listening on the local binding")]
    internal static partial void LocalBindingUp(ILogger logger);

    [LoggerMessage(
        EventId = 3731, Level = LogLevel.Information,
        Message = "Remote binding listening on {Endpoint} as {Fingerprint}")]
    internal static partial void RemoteBindingUp(ILogger logger, string endpoint, string fingerprint);
}
