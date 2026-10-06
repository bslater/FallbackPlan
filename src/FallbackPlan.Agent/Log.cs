using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Storage.Abstractions;
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
        EventId = 3764, Level = LogLevel.Warning,
        Message = "Set {SetId}: writer sequence adopted the repository's observed head, {From} -> {To} — local allocation state was behind its own published history")]
    internal static partial void ObservedHeadAdopted(ILogger logger, LogId setId, ulong from, ulong to);

    [LoggerMessage(
        EventId = 3765, Level = LogLevel.Warning,
        Message = "Set {SetId}: the repository's observed head could not be read ({Reason}); the sequence keeps local state and the colliding-put refusal stands behind it")]
    internal static partial void ObservedHeadUnavailable(ILogger logger, LogId setId, string reason);

    [LoggerMessage(
        EventId = 3771, Level = LogLevel.Information,
        Message = "Set {SetId} adopted repository {RepositoryId} from destination {Destination}: {SnapshotCount} snapshot(s), writer identity resumed: {WriterIdentityResumed}")]
    internal static partial void ArchiveAdopted(
        ILogger logger, LogId setId, LogId repositoryId, LogLabel destination, int snapshotCount, bool writerIdentityResumed);

    [LoggerMessage(
        EventId = 3772, Level = LogLevel.Warning,
        Message = "This installation now writes under the adopted archive's writer identity {WriterId}; its own never published")]
    internal static partial void WriterIdentityResumed(ILogger logger, LogId writerId);

    [LoggerMessage(
        EventId = 3700, Level = LogLevel.Error,
        Message = "Job {JobId} ({Description}) failed past its own handler")]
    internal static partial void JobFaulted(ILogger logger, LogId jobId, LogLabel description, Exception exception);

    [LoggerMessage(
        EventId = 3710, Level = LogLevel.Information,
        Message = "Remote peer authenticated: {Fingerprint}")]
    internal static partial void PeerAuthenticated(ILogger logger, LogId fingerprint);

    [LoggerMessage(
        EventId = 3711, Level = LogLevel.Warning,
        Message = "Remote connection refused: {Reason} — {Detail}")]
    internal static partial void RemoteRefused(ILogger logger, LogLabel reason, string detail);

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
    internal static partial void PeerPaired(ILogger logger, LogLabel label, LogId fingerprint);

    [LoggerMessage(
        EventId = 3715, Level = LogLevel.Warning,
        Message = "Invite pairing refused: {Reason} — {Detail}")]
    internal static partial void PairingRefused(ILogger logger, LogLabel reason, string detail);

    [LoggerMessage(
        EventId = 3720, Level = LogLevel.Warning,
        Message = "Replication offered by {Fingerprint} but this service holds no replicas; closing")]
    internal static partial void ReplicationWithoutReplicas(ILogger logger, LogId fingerprint);

    [LoggerMessage(
        EventId = 3721, Level = LogLevel.Information,
        Message = "Retrieval session served for {Fingerprint}")]
    internal static partial void RetrievalServed(ILogger logger, LogId fingerprint);




    [LoggerMessage(
        EventId = 3722, Level = LogLevel.Information,
        Message = "Peering terminated by {Fingerprint}")]
    internal static partial void PeeringTerminated(ILogger logger, LogId fingerprint);

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
    internal static partial void Replicated(ILogger logger, long committed, LogId fingerprint);

    [LoggerMessage(
        EventId = 3725, Level = LogLevel.Warning,
        Message = "A receipt for {Fingerprint} was sent but could not be filed here: {Detail}")]
    internal static partial void DeletionReceiptNotFiled(ILogger logger, LogId fingerprint, string detail);

    [LoggerMessage(
        EventId = 3726, Level = LogLevel.Warning,
        Message = "Deletion receipt from peer {Destination} for set {Set} rejected: {Detail}")]
    internal static partial void DeletionReceiptRejected(ILogger logger, LogLabel destination, LogLabel set, string detail);

    [LoggerMessage(
        EventId = 3727, Level = LogLevel.Warning,
        Message = "Deletion receipt from peer {Destination} for set {Set} verified but could not be filed: {Detail}")]
    internal static partial void DeletionReceiptNotFiledByCommander(
        ILogger logger, LogLabel destination, LogLabel set, string detail);

    [LoggerMessage(
        EventId = 3778, Level = LogLevel.Warning,
        Message = "Replication receipt from peer {Destination} for set {Set} rejected: {Detail}")]
    internal static partial void ReplicationReceiptRejected(
        ILogger logger, LogLabel destination, LogLabel set, string detail);

    [LoggerMessage(
        EventId = 3779, Level = LogLevel.Warning,
        Message = "Replication receipt from peer {Destination} for set {Set} verified but could not be filed: {Detail}")]
    internal static partial void ReplicationReceiptNotFiledByCommander(
        ILogger logger, LogLabel destination, LogLabel set, string detail);

    [LoggerMessage(
        EventId = 3780, Level = LogLevel.Information,
        Message = "Swept {Deletions} deletion receipt(s) and {Replications} replication receipt(s) past their retention")]
    internal static partial void ReceiptsSwept(ILogger logger, int deletions, int replications);

    [LoggerMessage(
        EventId = 3781, Level = LogLevel.Warning,
        Message = "Compaction did not run for set {Set}; the retention this pass did stands and the next pass retries")]
    internal static partial void CompactionFailed(ILogger logger, LogLabel set, Exception exception);

    [LoggerMessage(
        EventId = 3724, Level = LogLevel.Information,
        Message = "Log level for {Category} changed to {Level} for the life of this service")]
    internal static partial void LogLevelChanged(ILogger logger, LogLabel category, LogLabel level);

    [LoggerMessage(
        EventId = 3740, Level = LogLevel.Debug,
        Message = "Set {SetName} is due: last completed {LastCompleted}, next run {NextRun}")]
    internal static partial void SetDue(
        ILogger logger, LogLabel setName, LogLabel lastCompleted, LogLabel nextRun);

    [LoggerMessage(
        EventId = 3741, Level = LogLevel.Debug,
        Message = "Set {SetName} is not due yet; next run {NextRun}")]
    internal static partial void SetNotDue(ILogger logger, LogLabel setName, LogLabel nextRun);

    // Information rather than Debug: a window is a setting an operator chose,
    // and "nothing ran last night" is the question it creates. A pass that
    // held everything back says so at the tier an operator actually reads.
    [LoggerMessage(
        EventId = 3782, Level = LogLevel.Information,
        Message = "Background window {Window} is shut; nothing scheduled runs until {NextOpen}")]
    internal static partial void BackgroundWindowShut(ILogger logger, LogLabel window, LogLabel nextOpen);

    // The other half of the same question: not "nothing started" but
    // "something that was already running stopped". A capture parking at ten
    // in the evening is the most visible thing a window does, and the least
    // guessable without a line saying so.
    [LoggerMessage(
        EventId = 3783, Level = LogLevel.Information,
        Message = "Background window {Window} shut over {Runs} running capture(s); each parks at its next file boundary")]
    internal static partial void BackgroundWindowParked(ILogger logger, LogLabel window, int runs);

    // Trace: one line per command at the seam every verb crosses, so a
    // service log read end to end is a conversation. Type names only, never
    // the command's content — several commands carry paths.
    [LoggerMessage(
        EventId = 3784, Level = LogLevel.Trace,
        Message = "{Command} answered {Result} in {ElapsedMilliseconds} ms")]
    internal static partial void CommandExecuted(
        ILogger logger, LogLabel command, LogLabel result, long elapsedMilliseconds);

    // Debug: how the setup verb classified a ceremony. A console stuck on its
    // setup screen shows a toast and nothing else says why; this does, and it
    // carries the classification only — the envelope is sealed and stays so.
    [LoggerMessage(
        EventId = 3785, Level = LogLevel.Debug,
        Message = "Setup provisioning answered '{Outcome}'")]
    internal static partial void ProvisionOutcome(ILogger logger, LogLabel outcome);

    [LoggerMessage(
        EventId = 3786, Level = LogLevel.Warning,
        Message = "A scheduled pass could not run; the service keeps serving and tries again in {PollSeconds} s")]
    internal static partial void PassLost(ILogger logger, int pollSeconds, Exception exception);

    [LoggerMessage(
        EventId = 3787, Level = LogLevel.Information,
        Message = "Scheduled passes run again, after {LostPasses} that could not in this process")]
    internal static partial void PassesResumed(ILogger logger, int lostPasses);

    // FR-MAN-002: a catalogue created at open — new, or in place of one
    // another schema wrote — is filled from the repository before it is read.
    [LoggerMessage(
        EventId = 3788, Level = LogLevel.Information,
        Message = "Set {SetId}: catalogue rebuilt from the repository at open — {Snapshots} snapshot(s), "
            + "{FileVersions} file version(s), {Missing} record(s) not seen; a rebuild that did not see every record runs again at the next open")]
    internal static partial void CatalogueRebuiltAtOpen(
        ILogger logger, LogId setId, int snapshots, int fileVersions, int missing);

    [LoggerMessage(
        EventId = 3789, Level = LogLevel.Warning,
        Message = "Set {SetId}: the catalogue could not be rebuilt from the repository at open ({Reason}); the set opens, and the next open tries again")]
    internal static partial void CatalogueRebuildAtOpenFailed(ILogger logger, LogId setId, string reason);

    [LoggerMessage(
        EventId = 3790, Level = LogLevel.Information,
        Message = "Set {SetId}: catalogue rebuild at open reported {Finding}")]
    internal static partial void CatalogueRebuildAtOpenFinding(ILogger logger, LogId setId, string finding);

    [LoggerMessage(
        EventId = 3791, Level = LogLevel.Information,
        Message = "Diagnostic bundle built for a {Scope} caller: {Records} log record(s), {LeftOut} left out, paths included {IncludesPaths}")]
    internal static partial void DiagnosticBundleBuilt(
        ILogger logger, CallerScope scope, int records, int leftOut, bool includesPaths);

    // A sync's live count of a destination's files is a display (ADR-0088
    // Amendment 1): a catalogue that cannot answer for it costs the circle its
    // live count, never the sync.
    [LoggerMessage(
        EventId = 3792, Level = LogLevel.Warning,
        Message = "The files {Destination} holds of set {Set}'s newest backup could not be counted for this sync: "
            + "{Reason}. The sync goes on, and the status shows what the ledger supports")]
    internal static partial void SyncCountUnavailable(
        ILogger logger, LogLabel set, LogLabel destination, string reason);

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

    // The startup configuration record (FR-SVC-010; ADR-0049): what the
    // service RESOLVED to operate against, not what was typed — provenance
    // included — so a diagnostic log alone can reconstruct its posture.
    // Paths are honest here and redacted where records leave the machine
    // (ADR-0043's rendering boundary), and no line carries a secret: the
    // passphrase appears only as a posture word.
    [LoggerMessage(
        EventId = 3760, Level = LogLevel.Information,
        Message = "Operating against state {StateDirectory} ({StateProvenance}) and archives {ArchivesRoot} ({ArchivesProvenance})")]
    internal static partial void StartupLocations(
        ILogger logger, LogPath stateDirectory, LogLabel stateProvenance, LogPath archivesRoot, LogLabel archivesProvenance);

    [LoggerMessage(
        EventId = 3761, Level = LogLevel.Information,
        Message = "Posture: poll {PollSeconds}s, backup pool {PoolWidth}, remote binding {RemoteBinding}")]
    internal static partial void StartupPosture(
        ILogger logger, int pollSeconds, int poolWidth, string remoteBinding);

    [LoggerMessage(
        EventId = 3762, Level = LogLevel.Information,
        Message = "Set '{Set}': {Roots} root(s), schedule {Schedule}, {Destinations} destination(s), direct-ship {DirectShip}, priority {Priority}")]
    internal static partial void StartupSet(
        ILogger logger, LogLabel set, int roots, LogLabel schedule, int destinations, bool directShip, LogLabel priority);

    [LoggerMessage(
        EventId = 3763, Level = LogLevel.Information,
        Message = "Destination '{Destination}': kind {Kind}, failure domain {Domain}")]
    internal static partial void StartupDestination(
        ILogger logger, LogLabel destination, LogLabel kind, LogLabel domain);

    [LoggerMessage(
        EventId = 3731, Level = LogLevel.Information,
        Message = "Remote binding listening on {Endpoint} as {Fingerprint}")]
    internal static partial void RemoteBindingUp(ILogger logger, string endpoint, LogId fingerprint);
    // Authentication (ADR-0045). The account is named and the password never
    // is — not redacted, not hashed, not truncated: there is no parameter one
    // could be passed through, which is the surest way to honour "no secrets
    // in logs". A failure does not say whether the account exists, because a
    // log that distinguishes the two is the name oracle the verb refuses to be.
    [LoggerMessage(
        EventId = 3750, Level = LogLevel.Information,
        Message = "{User} signed in as {Role}")]
    internal static partial void SignedIn(ILogger logger, LogLabel user, LogLabel role);

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
    internal static partial void AccountCreated(ILogger logger, LogLabel user, LogLabel role);

    [LoggerMessage(
        EventId = 3754, Level = LogLevel.Information,
        Message = "Account {User} removed; {Sessions} live session(s) ended with it")]
    internal static partial void AccountDeleted(ILogger logger, LogLabel user, int sessions);

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
    internal static partial void SessionResumed(ILogger logger, LogLabel user);

    [LoggerMessage(
        EventId = 3757, Level = LogLevel.Trace,
        Message = "{Command} refused: this connection has not signed in")]
    internal static partial void CommandRefusedUnauthenticated(ILogger logger, LogLabel command);

    // Warning: a destination leaving mid-run means this backup commits with
    // one copy fewer than the configuration promises, and the catch-up that
    // heals it is silent when it works.
    [LoggerMessage(
        EventId = 3758, Level = LogLevel.Warning,
        Message = "Destination {Destination} dropped from this backup run: {Reason}. Its replica lags until the next catch-up")]
    internal static partial void ShipDestinationDropped(ILogger logger, LogLabel destination, string reason);

    // Warning: a queued job with no tracked cancellation cannot be run and is
    // discarded — unreachable while identities stay fresh, and the silent
    // version of this is an awaiter that hangs forever.
    [LoggerMessage(
        EventId = 3759, Level = LogLevel.Warning,
        Message = "Job {JobId} ({Description}) was dequeued with no tracked cancellation and was discarded")]
    internal static partial void QueuedJobUntracked(ILogger logger, LogId jobId, LogLabel description);

    // Debug rather than Information: on a busy install this is the commonest
    // thing a pass does, and it is the absence of work. It is here at all
    // because "the sync did nothing and said nothing" and "the sync did not
    // run" look identical from outside, and only one of them is a bug
    // (ADR-0056).
    [LoggerMessage(
        EventId = 3766, Level = LogLevel.Debug,
        Message = "Set {Set} is level with {Destination} at publication sequence {Sequence}; "
            + "nothing published since it was last read through, so this pass carried nothing")]
    internal static partial void SyncSkipped(
        ILogger logger, LogLabel set, LogLabel destination, ulong sequence);

    // Information, not Debug: this is the saving the work exists for, and an
    // operator watching a slow uplink finish a transfer it started yesterday
    // should be able to see that it did (ADR-0057).
    [LoggerMessage(
        EventId = 3767, Level = LogLevel.Information,
        Message = "Resuming {Key} at {Offset} bytes: the destination's staged prefix matches this copy")]
    internal static partial void ObjectResumed(ILogger logger, ObjectKey key, ulong offset);

    // Information, not Warning: a replica that cannot be read back proves
    // nothing and is accused of nothing, and the pair's row already says it is
    // unproven. A warning here would train an operator to ignore one.
    [LoggerMessage(
        EventId = 3769, Level = LogLevel.Information,
        Message = "Destination {Destination} could not be read back for proof: {Reason}. "
            + "This pass records the sync without a verification")]
    internal static partial void ReadBackUnavailable(ILogger logger, LogLabel destination, string reason);

    // Information and not Warning, though it is a change of ownership: the
    // claim is the recovery working, and an operator who sees this expected to
    // see it. The peer's own ledger is the durable record of what moved.
    [LoggerMessage(
        EventId = 3770, Level = LogLevel.Information,
        Message = "Peer {Fingerprint} claimed {Count} replica(s) here under its installation's claim key; "
            + "the attribution now points at that device")]
    internal static partial void ReplicaClaimed(ILogger logger, LogId fingerprint, int count);

    // Warning: the bytes were staged by this pair and no longer match, so
    // something between the two sessions damaged them. Re-sending is the right
    // answer and a silent one would hide a destination whose disk is rotting.
    [LoggerMessage(
        EventId = 3768, Level = LogLevel.Warning,
        Message = "The destination staged {Offset} bytes of {Key} that do not match this copy; "
            + "the object is being sent whole instead")]
    internal static partial void ObjectResumeRefused(ILogger logger, ObjectKey key, ulong offset);

    [LoggerMessage(
        EventId = 3773, Level = LogLevel.Warning,
        Message = "Set {SetName}: destination {Destination} attests writer sequence {Attested} but local state said {Local} — "
            + "the state directory was rolled back; the writer moved past the destination's head and this pass deletes nothing there")]
    internal static partial void DestinationAhead(
        ILogger logger, LogLabel setName, LogLabel destination, ulong attested, ulong local);

    [LoggerMessage(
        EventId = 3774, Level = LogLevel.Warning,
        Message = "Set {SetName}: metadata copied back from destination {Destination} and the catalogue rebuilt in place — "
            + "the set's local state had fallen behind what it published")]
    internal static partial void MetadataHealedFromDestination(ILogger logger, LogLabel setName, LogLabel destination);

    [LoggerMessage(
        EventId = 3775, Level = LogLevel.Warning,
        Message = "Set {SetName}: the heal from a destination could not copy the metadata back ({Reason}); the next pass retries")]
    internal static partial void MetadataHealFailed(ILogger logger, LogLabel setName, string reason);

    [LoggerMessage(
        EventId = 3776, Level = LogLevel.Information,
        Message = "Set {SetId}: catalogue rebuild during the heal reported {Finding}")]
    internal static partial void HealRebuildFinding(ILogger logger, LogId setId, string finding);

    [LoggerMessage(
        EventId = 3777, Level = LogLevel.Warning,
        Message = "Set {SetName}: {Objects} object(s), {Bytes} bytes of destination {Destination}'s newer history copied back "
            + "into the staging archive and the catalogue rebuilt — the archive had fallen behind what it published")]
    internal static partial void ContentHealedFromDestination(
        ILogger logger, LogLabel setName, LogLabel destination, long objects, long bytes);
}
