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
        EventId = 3724, Level = LogLevel.Information,
        Message = "Log level for {Category} changed to {Level} for the life of this service")]
    internal static partial void LogLevelChanged(ILogger logger, string category, string level);

    [LoggerMessage(
        EventId = 3740, Level = LogLevel.Debug,
        Message = "Set {SetName} is due: last completed {LastCompleted}, next run {NextRun}")]
    internal static partial void SetDue(
        ILogger logger, string setName, string lastCompleted, string nextRun);

    [LoggerMessage(
        EventId = 3741, Level = LogLevel.Debug,
        Message = "Set {SetName} is not due yet; next run {NextRun}")]
    internal static partial void SetNotDue(ILogger logger, string setName, string nextRun);

    // The Information-tier half of configuration loading: the load itself is
    // Debug (Application's 3400, once per scheduler pass); this fires on the
    // first load and then only when the content differs from the last one, so
    // the operator's tier records edits rather than re-reads.
    [LoggerMessage(
        EventId = 3742, Level = LogLevel.Information,
        Message = "Configuration in force: schema {Schema}, {Sets} sets, {Destinations} destinations")]
    internal static partial void ConfigurationChanged(
        ILogger logger, int schema, int sets, int destinations);

    [LoggerMessage(
        EventId = 3730, Level = LogLevel.Information,
        Message = "Service listening on the local binding")]
    internal static partial void LocalBindingUp(ILogger logger);

    [LoggerMessage(
        EventId = 3731, Level = LogLevel.Information,
        Message = "Remote binding listening on {Endpoint} as {Fingerprint}")]
    internal static partial void RemoteBindingUp(ILogger logger, string endpoint, string fingerprint);
    // Authentication (ADR-0045). The account is named and the password never
    // is — not redacted, not hashed, not truncated: there is no parameter one
    // could be passed through, which is the surest way to honour "no secrets
    // in logs". A failure does not say whether the account exists, because a
    // log that distinguishes the two is the name oracle the verb refuses to be.
    [LoggerMessage(
        EventId = 3750, Level = LogLevel.Information,
        Message = "{User} signed in as {Role}")]
    internal static partial void SignedIn(ILogger logger, string user, string role);

    [LoggerMessage(
        EventId = 3751, Level = LogLevel.Information,
        Message = "A session was signed out")]
    internal static partial void SignedOut(ILogger logger);

    [LoggerMessage(
        EventId = 3752, Level = LogLevel.Warning,
        Message = "Authentication failed for {User}")]
    internal static partial void AuthenticationFailed(ILogger logger, string user);

    [LoggerMessage(
        EventId = 3753, Level = LogLevel.Information,
        Message = "Account {User} created as {Role}")]
    internal static partial void AccountCreated(ILogger logger, string user, string role);

    [LoggerMessage(
        EventId = 3754, Level = LogLevel.Information,
        Message = "Account {User} removed; {Sessions} live session(s) ended with it")]
    internal static partial void AccountDeleted(ILogger logger, string user, int sessions);

    // The refusals the gate answers were, for a long stretch of one real
    // incident, invisible: a client with a lapsed session failed every
    // command for sixteen minutes and a trace-level log never said why —
    // the listener's generic "answered ServiceError" was all there was.
    // These name the why. The token is a credential whether or not it has
    // lapsed, so like the password above it has no parameter to ride in.
    [LoggerMessage(
        EventId = 3755, Level = LogLevel.Debug,
        Message = "A session resume was refused: the presented token is expired, revoked, or unknown")]
    internal static partial void SessionResumeRefused(ILogger logger);

    [LoggerMessage(
        EventId = 3756, Level = LogLevel.Trace,
        Message = "Session resumed for {User}")]
    internal static partial void SessionResumed(ILogger logger, string user);

    [LoggerMessage(
        EventId = 3757, Level = LogLevel.Trace,
        Message = "{Command} refused: this connection has not signed in")]
    internal static partial void CommandRefusedUnauthenticated(ILogger logger, string command);
}
