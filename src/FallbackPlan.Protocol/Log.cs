using Microsoft.Extensions.Logging;

namespace FallbackPlan.Protocol;

/// <summary>
/// Peer protocol diagnostics (ADR-0043; event ids 3200–3299).
/// </summary>
/// <remarks>
/// Pairing and authentication failures are the ones people report, and they
/// are also the ones where saying too much is a hazard: a refusal names what
/// was refused and why, never the key material that failed to match.
/// Fingerprints are short public identifiers and are safe to print; nothing
/// here takes a private key or a session secret.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 3200, Level = LogLevel.Information,
        Message = "Pairing accepted with {Fingerprint} in role {Role}")]
    internal static partial void PairingAccepted(ILogger logger, string fingerprint, string role);

    [LoggerMessage(
        EventId = 3201, Level = LogLevel.Warning,
        Message = "Pairing refused for {Fingerprint}: {Reason}")]
    internal static partial void PairingRefused(ILogger logger, string fingerprint, string reason);

    [LoggerMessage(
        EventId = 3202, Level = LogLevel.Warning,
        Message = "A peer presented an identity that is not the pinned one for {Fingerprint} — "
            + "refused, and it stays refused until somebody approves the new identity")]
    internal static partial void IdentityChanged(ILogger logger, string fingerprint);

    [LoggerMessage(
        EventId = 3203, Level = LogLevel.Debug,
        Message = "Session established with {Fingerprint}, features {Features}")]
    internal static partial void SessionEstablished(ILogger logger, string fingerprint, string features);

    [LoggerMessage(
        EventId = 3204, Level = LogLevel.Warning,
        Message = "Session with {Fingerprint} failed: {Reason}")]
    internal static partial void SessionFailed(ILogger logger, string fingerprint, string reason);
}
