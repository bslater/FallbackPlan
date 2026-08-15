using FallbackPlan.Application;

namespace FallbackPlan.Agent;

/// <summary>
/// Asks whether a declared destination could actually take a backup, without
/// sending one (FR-DEST-001, FR-DEST-003).
/// </summary>
/// <remarks>
/// <para>
/// Everything the hub knew about a destination's fitness, it learned by trying
/// to use it. Pairing ends at a grant, which proves a key exchange happened
/// once and says nothing about whether that peer is reachable now; a
/// local-path destination was never checked at all until a copy was already
/// under way. So the first sync was the first news — and for a destination no
/// set had synced yet, there was no news at all, because the sync ledger has
/// no row for a pair that was never attempted.
/// </para>
/// <para>
/// This is deliberately the <b>shallowest</b> setting of the same verb whose
/// deeper settings re-read stored bytes. One surface with a depth flag, not
/// two verbs: a separate `destination check` would drift from
/// `verify-destination` in output shape and exit codes within an arc or two,
/// and an operator would have to know which one to reach for.
/// </para>
/// <para>
/// A probe never writes a success to the ledger. Reaching a destination is not
/// syncing to it, and a row saying otherwise would move the trim gate and the
/// scheduler's due-ness on the strength of a handshake. Failures <i>are</i>
/// recorded, because they are the same failures a sync would have found and
/// the operator should see them in status without running one.
/// </para>
/// </remarks>
internal static class DestinationProbe
{
    /// <summary>What a probe found.</summary>
    /// <param name="Viable">Whether the destination could take a backup right now.</param>
    /// <param name="Detail">One sentence: what was confirmed, or what is wrong.</param>
    internal sealed record ProbeOutcome(bool Viable, string Detail);

    /// <summary>Probes one declared destination.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set the destination is being probed for — the ledger row's other half.</param>
    /// <param name="declared">The destination's declaration.</param>
    /// <param name="nowMs">The clock, for any ledger row this writes.</param>
    /// <param name="cancellationToken">Cancels the dial.</param>
    internal static async ValueTask<ProbeOutcome> ProbeAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        DestinationConfiguration declared,
        ulong nowMs,
        CancellationToken cancellationToken)
    {
        // The declaration first: no point dialling a host that was never
        // spelled as one. This is the same syntax check status reports, so the
        // two say the same sentence.
        if (declared.AddressDefect is { } defect)
        {
            return Refuse(runtime, set, declared, DestinationSyncState.Failed, defect, nowMs);
        }

        return declared.Kind switch
        {
            DestinationKind.LocalPath => ProbeLocalPath(runtime, set, declared, nowMs),
            DestinationKind.Peer =>
                await ProbePeerAsync(runtime, set, declared, nowMs, cancellationToken).ConfigureAwait(false),

            // A reserved kind is a stated incapacity, never a failure
            // (FR-DEST-005), and the probe must not spell it like one.
            _ => new ProbeOutcome(false, $"destination kind '{declared.Kind}' is not yet supported"),
        };
    }

    /// <summary>
    /// Confirms the directory exists, is a directory, and accepts a write.
    /// </summary>
    /// <remarks>
    /// The configured directory is never created here, for the same reason the
    /// fan-out never creates it: a removable drive's mount point that is
    /// missing means the drive is out, and creating the directory would write
    /// the backup onto whatever disk holds the mount point instead
    /// (FR-DEST-003).
    /// </remarks>
    private static ProbeOutcome ProbeLocalPath(
        ServiceRuntime runtime, BackupSetConfiguration set, DestinationConfiguration declared, ulong nowMs)
    {
        var path = declared.Path!;
        if (File.Exists(path))
        {
            // A file where a directory was declared is a typo that no amount
            // of waiting fixes, so it is a failure rather than an outage.
            return Refuse(
                runtime, set, declared, DestinationSyncState.Failed,
                $"'{path}' is a file, not a directory", nowMs);
        }

        if (!Directory.Exists(path))
        {
            return Refuse(
                runtime, set, declared, DestinationSyncState.Unavailable,
                $"'{path}' does not exist — if this is a removable drive, it is not attached", nowMs);
        }

        // Existence is not permission, and a directory that will not take a
        // write is a destination that will fail every sync while looking
        // perfectly present. One temp file, removed immediately.
        var probeFile = Path.Combine(path, $".fallbackplan-probe-{Guid.NewGuid():n}");
        try
        {
            File.WriteAllBytes(probeFile, []);
            File.Delete(probeFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refuse(
                runtime, set, declared, DestinationSyncState.Failed,
                $"'{path}' exists but will not accept a write: {exception.Message}", nowMs);
        }

        return new ProbeOutcome(true, $"'{path}' exists and accepts writes");
    }

    /// <summary>
    /// Opens a session with the peer and closes it again — the handshake a
    /// sync would do, without the push that follows it.
    /// </summary>
    /// <remarks>
    /// Negotiation is where the answers that matter live: whether the peer is
    /// reachable, whether it still honours the grant, and whether it will
    /// prove possession at all (FR-VER-006). A destination that declines
    /// verification is refused here exactly as it would be refused at a sync,
    /// so the probe cannot report a destination as viable that the fan-out
    /// would then turn away.
    /// </remarks>
    private static async ValueTask<ProbeOutcome> ProbePeerAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        DestinationConfiguration declared,
        ulong nowMs,
        CancellationToken cancellationToken)
    {
        if (!PeerAddress.TryResolve(runtime, declared, out var address, out var unreachable))
        {
            return Refuse(runtime, set, declared, DestinationSyncState.Failed, unreachable!, nowMs);
        }

        var (grants, grant, host, port) = address!;
        try
        {
            using var keypair = Protocol.PeerKeypairStore.Open(runtime.Options.StateDirectory);
            await using var connection = await Protocol.PeerTlsConnection.DialAsync(
                host, port, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            var session = await Protocol.PeerSessionDriver.DialAsync(
                connection, keypair, grants, grant.Identity, "fallbackplan-agent", terms: null,
                requiredFeatures: declared.RequiresVerification ? FanOut.VerificationRequirement : null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var quota = session.TheirTerms?.QuotaBytes ?? 0;
            return new ProbeOutcome(
                true,
                $"reached {host}:{port}, session established"
                + (quota > 0 ? $", lending {quota} byte(s)" : ", lending unbounded space"));
        }
        catch (Protocol.PeerProtocolException refusal)
        {
            return Refuse(
                runtime, set, declared, DestinationSyncState.Failed,
                $"the peer refused: {refusal.Reason} — {refusal.Message}", nowMs);
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException
            or System.Security.Authentication.AuthenticationException)
        {
            // Not reached, so a gap that closes itself when it returns
            // (FR-DEST-003) — never a fault to go and fix.
            return Refuse(
                runtime, set, declared, DestinationSyncState.Unavailable,
                $"could not reach {host}:{port}: {exception.Message}", nowMs);
        }
    }

    /// <summary>
    /// Records the finding against the pair and returns it — the failure half
    /// of a probe belongs in status, not only in the console the probe was run
    /// from.
    /// </summary>
    private static ProbeOutcome Refuse(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        DestinationConfiguration declared,
        DestinationSyncState state,
        string detail,
        ulong nowMs)
    {
        runtime.DestinationSync.RecordFailure(set.Id, declared.Name, state, detail, nowMs);
        return new ProbeOutcome(false, detail);
    }
}
