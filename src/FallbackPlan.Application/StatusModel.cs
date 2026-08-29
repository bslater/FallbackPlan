using Bodu;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Application;

/// <summary>
/// Builds a <see cref="DestinationStatusInput"/> from the three things that
/// describe a destination: what the configuration declares, what the sync
/// ledger recorded, and when the set last completed a backup.
/// </summary>
/// <remarks>
/// <para>
/// This exists because there are two status producers — the service handler
/// and the console's direct path — and they had drifted into deriving the same
/// three facts twice: the in-sync-but-behind demotion, the failure domain, and
/// the row itself. Two copies of a derivation is two places for a rule to
/// change in one of them, and every field added to
/// <see cref="DestinationStatusInput"/> doubled the edit.
/// </para>
/// <para>
/// The device comparison arrives as a delegate rather than a call, because the
/// use-case layer reaches only the domain layer (architecture 11 §2, enforced
/// by <c>ArchitectureTests</c>) and stating a path's volume is the platform's
/// job. The <b>policy</b> — what a shared volume means for protection — lives
/// here, once.
/// </para>
/// </remarks>
public static class DestinationStatus
{
    /// <summary>
    /// How long a local-path destination's proof stands before it is called
    /// overdue (architecture 09 §4).
    /// </summary>
    /// <remarks>
    /// The tighter of the two bounds, because a local path is the cheaper
    /// destination to re-read — the hub owns the disk — and because the deep
    /// sweep that refreshes it runs weekly by default
    /// (<c>ReplicaSweepJob.DefaultIntervalDays</c>). A bound shorter than the
    /// cadence meant to satisfy it would be permanently unmet.
    /// </remarks>
    public const int LocalPathVerificationBoundDays = 7;

    /// <summary>
    /// How long a peer destination's proof stands before it is called overdue
    /// (architecture 09 §4).
    /// </summary>
    /// <remarks>
    /// Looser than a local path's, because refreshing it costs somebody
    /// else's bandwidth and availability rather than this hub's own disk.
    /// </remarks>
    public const int PeerVerificationBoundDays = 30;

    /// <summary>
    /// Describes one declared destination of one set.
    /// </summary>
    /// <param name="reference">The set's reference to the destination, by name.</param>
    /// <param name="declared">The destination's declaration, or null when the reference dangles.</param>
    /// <param name="setRoots">The set's capture roots, for the failure-domain comparison (ADR-0040).</param>
    /// <param name="record">The pair's sync ledger row, or null when never attempted.</param>
    /// <param name="lastCompletedAt">When the set last completed a backup, Unix milliseconds; 0 when never.</param>
    /// <param name="nowUnixMilliseconds">
    /// The clock, for the age of the verification stamp. It enters here rather
    /// than in <see cref="StatusDeriver.Derive"/> because the derivation is
    /// deliberately a pure function of plain values with no clock of its own,
    /// and the per-destination bound this is compared against already lives on
    /// <see cref="DestinationStatusInput"/>.
    /// </param>
    /// <param name="deviceIdOf">
    /// The volume a path sits on, or null when the platform cannot say — which
    /// is answered conservatively, as sharing a volume.
    /// </param>
    public static DestinationStatusInput Describe(
        string reference,
        DestinationConfiguration? declared,
        IReadOnlyList<string> setRoots,
        DestinationSyncRecord? record,
        ulong lastCompletedAt,
        ulong nowUnixMilliseconds,
        Func<string, ulong?> deviceIdOf)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(reference);
        ThrowHelper.ThrowIfNull(setRoots);
        ThrowHelper.ThrowIfNull(deviceIdOf);

        if (declared is null)
        {
            // Validation refuses dangling references, so this is a
            // configuration edited mid-flight — reported, never invented
            // around, and conservative: an undeclarable destination earns
            // nothing.
            return new DestinationStatusInput
            {
                Name = reference,
                Kind = DestinationKind.LocalPath,
                Sync = DestinationSyncState.Failed,
                Domain = FailureDomain.SameVolume,
                Detail = "no longer declared",
                Cause = SyncCause.Reported,
            };
        }

        // Every demotion states its cause (ADR-0027 §4: "the per-destination
        // reason carried, not summarised away"). The ledger's own words win
        // where it wrote any; the demotions below are decided here, so only
        // here can they be explained.
        var sync = record?.State ?? DestinationSyncState.Behind;
        var cause = SyncCause.None;
        var detail = record?.LastError;
        if (record is null)
        {
            cause = SyncCause.NeverSynced;
            detail = "never synced — no sync has been attempted for this destination yet";
        }

        if (sync == DestinationSyncState.InSync && record is { NeedsFull: true })
        {
            // A pair owed its seeding full holds nothing restorable yet: its
            // fresh ledger row defaults to InSync as an artefact of the blank
            // row (ADR-0047 §5). Checked BEFORE the catch-up comparison —
            // a NeedsFull row has no success stamp, so under a set with a
            // completed backup the comparison below also matches it, and an
            // owed seed labelled "catching up" would read as held protection
            // under the derivation's amendment when the pair holds nothing.
            sync = DestinationSyncState.Behind;
            cause = SyncCause.AwaitingSeed;
            detail ??= "owed its full backup — incrementals skip this destination until the seed lands";
        }

        if (sync == DestinationSyncState.InSync && (record!.LastSuccessAt ?? 0) < lastCompletedAt)
        {
            // In sync as of an older snapshot: the staging archive moved on.
            // The one origin of `behind` that used to carry no reason at all
            // — LastError is nulled by every success — so minutes after a
            // successful backup the console read "behind." full stop, for a
            // state that heals unaided on the next sync pass.
            sync = DestinationSyncState.Behind;
            cause = SyncCause.CatchingUp;
            detail = "a backup completed after this destination's last sync — it catches up on the next sync pass";
        }

        if (cause == SyncCause.None && detail is not null)
        {
            cause = SyncCause.Reported;
        }

        return new DestinationStatusInput
        {
            Name = declared.Name,
            Kind = declared.Kind,
            Sync = sync,
            Domain = DomainOf(declared, setRoots, deviceIdOf),
            RequiresVerification = declared.RequiresVerification,
            LastSuccessAt = record?.LastSuccessAt,
            Detail = detail,
            Cause = cause,
            SyncedSequence = record?.SyncedSequence ?? 0,
            VerifiedAt = record?.VerifiedAt,
            VerifiedSequence = record?.VerifiedSequence ?? 0,
            VerifiedObjects = record?.VerifiedObjects ?? 0,
            VerifiedPopulation = record?.VerifiedPopulation ?? 0,
            VerificationAgeDays = AgeInDays(record?.VerifiedAt, nowUnixMilliseconds),
            VerificationBoundDays = BoundFor(declared.Kind),
            AddressDefect = declared.AddressDefect,
        };
    }

    /// <summary>
    /// Whole days between a stamp and now, or null when there is no stamp or
    /// the stamp is in the future.
    /// </summary>
    /// <remarks>
    /// A stamp ahead of the clock is a skewed clock, not a fresh proof, and it
    /// must not be reported as an age of zero — that would read as "verified
    /// today". Withholding the age instead leaves the destination out of the
    /// overdue check entirely, which is the conservative direction: it can
    /// only fail to warn, never warn wrongly.
    /// </remarks>
    private static int? AgeInDays(ulong? verifiedAt, ulong nowUnixMilliseconds) =>
        verifiedAt is { } stamp && stamp <= nowUnixMilliseconds
            ? (int)((nowUnixMilliseconds - stamp) / 86_400_000)
            : null;

    /// <summary>How long a destination of this kind may go unproven (architecture 09 §4).</summary>
    private static int BoundFor(DestinationKind kind) => kind switch
    {
        DestinationKind.Peer => PeerVerificationBoundDays,
        _ => LocalPathVerificationBoundDays,
    };

    /// <summary>
    /// The declaration wins; otherwise the domain is derived by kind, always
    /// conservatively (FR-SNP-007, ADR-0018 Amendment 2).
    /// </summary>
    public static FailureDomain DomainOf(
        DestinationConfiguration destination, IReadOnlyList<string> setRoots, Func<string, ulong?> deviceIdOf)
    {
        ThrowHelper.ThrowIfNull(destination);
        ThrowHelper.ThrowIfNull(setRoots);
        ThrowHelper.ThrowIfNull(deviceIdOf);

        if (destination.FailureDomain is { } declared)
        {
            return declared;
        }

        return destination.Kind switch
        {
            // A different volume on this machine survives losing the disk and
            // nothing more. With several roots the claim must hold for every
            // one (ADR-0040): a destination sharing ANY root's volume — or a
            // platform that will not say for any of them — survives nothing.
            DestinationKind.LocalPath =>
                setRoots.Count > 0
                && destination.Path is { Length: > 0 } path
                && deviceIdOf(path) is { } destinationDevice
                && setRoots.All(root =>
                    root.Length > 0
                    && deviceIdOf(root) is { } rootDevice
                    && rootDevice != destinationDevice)
                    ? FailureDomain.SameMachine
                    : FailureDomain.SameVolume,
            DestinationKind.Peer => FailureDomain.SameSite,
            _ => FailureDomain.Independent,
        };
    }
}

/// <summary>
/// Why a destination row is not in sync (ADR-0050; contract 1.22's
/// <c>reason</c>): the machine cause beside the prose, decided in
/// <see cref="DestinationStatus.Describe"/> — the one place the demotions
/// happen, so the one place they can be explained.
/// </summary>
public enum SyncCause
{
    /// <summary>Nothing to explain — the row is in sync, or no cause is known.</summary>
    None = 0,

    /// <summary>
    /// A backup completed after this destination's last sync; the next sync
    /// pass heals it unaided. The self-healing window, not a fault.
    /// </summary>
    CatchingUp = 1,

    /// <summary>The destination is owed its seeding full backup (ADR-0047 §5).</summary>
    AwaitingSeed = 2,

    /// <summary>No sync has ever been attempted for this pair.</summary>
    NeverSynced = 3,

    /// <summary>The ledger recorded its own reason; <see cref="DestinationStatusInput.Detail"/> carries those words.</summary>
    Reported = 4,
}

/// <summary>
/// One destination's observed facts, as the derivation consumes them
/// (ADR-0027 amendment): plain values gathered by the host — the sync ledger
/// supplies the state, the platform supplies the failure-domain comparison,
/// the configuration supplies the kind. Built by
/// <see cref="DestinationStatus.Describe"/>, which is the only place those
/// three sources are combined.
/// </summary>
public sealed record DestinationStatusInput
{
    /// <summary>The destination's declared name.</summary>
    public required string Name { get; init; }

    /// <summary>The destination's declared kind.</summary>
    public required DestinationKind Kind { get; init; }

    /// <summary>Where the (set, destination) pair stands, per the sync ledger.</summary>
    public required DestinationSyncState Sync { get; init; }

    /// <summary>
    /// Where the destination sits relative to the source (FR-SNP-007):
    /// declared in configuration or derived by kind (ADR-0018 Amendment 2).
    /// <see cref="FailureDomain.SameVolume"/> and <see cref="FailureDomain.SameMachine"/>
    /// die with the machine, so they cap what the destination can earn at
    /// <see cref="ProtectionState.Captured"/> (PT-8) — and the staging
    /// archive never appears here at all: it is a cache, not a destination
    /// (ADR-0018 Amendment 1).
    /// </summary>
    public required FailureDomain Domain { get; init; }

    /// <summary>When this pair last synced, Unix milliseconds; null when never.</summary>
    public ulong? LastSuccessAt { get; init; }

    /// <summary>
    /// Why the row is not simply in sync, for the warning to repeat verbatim
    /// (ADR-0027 §4): the ledger's own failure words, or the demotion's
    /// stated cause. Null on a healthy row — a state that needs no excuse.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The machine form of <see cref="Detail"/> (contract 1.22): which kind
    /// of not-in-sync this is, so a client can distinguish the self-healing
    /// catch-up window from a reported fault without parsing prose.
    /// </summary>
    public SyncCause Cause { get; init; }

    /// <summary>
    /// Whether this row is behind only because a newer backup awaits its
    /// next sync pass while the previously delivered backup is still held —
    /// the self-healing window a successful run itself opens (ADR-0050
    /// amendment). The recorded success is the load-bearing guard: a row
    /// that never converged holds nothing, whatever its cause claims.
    /// </summary>
    public bool HoldsPreviousBackup =>
        Sync == DestinationSyncState.Behind && Cause == SyncCause.CatchingUp && LastSuccessAt is not null;

    /// <summary>
    /// The highest publication sequence the last successful sync delivered —
    /// what the destination claims to hold (FR-GC-009's currency).
    /// </summary>
    public ulong SyncedSequence { get; init; }

    /// <summary>
    /// When a verification pass last proved sampled bytes at this destination
    /// (peer-protocol 04); null when never. The age half of FR-VER-003.
    /// </summary>
    public ulong? VerifiedAt { get; init; }

    /// <summary>
    /// The highest publication sequence a passed verification covered. The
    /// destination's copy is <b>verified current</b> only while this is at
    /// least <see cref="SyncedSequence"/> — a later unverified sync makes the
    /// stamp stale, never wrong.
    /// </summary>
    public ulong VerifiedSequence { get; init; }

    /// <summary>Ranges the last passed verification proved — the coverage numerator.</summary>
    public int VerifiedObjects { get; init; }

    /// <summary>Objects eligible when that sample was drawn — the coverage denominator.</summary>
    public int VerifiedPopulation { get; init; }

    /// <summary>
    /// Whether this destination is required to prove possession (FR-VER-006),
    /// or was knowingly excused. It distinguishes the two ways a destination
    /// can be unproven — "cannot be, and you said so" from "should be, and
    /// isn't" — which look identical in the stamps alone and call for
    /// completely different responses from a human.
    /// </summary>
    public bool RequiresVerification { get; init; } = true;

    /// <summary>
    /// What is wrong with this destination's declared address, or null when
    /// nothing is (FR-DEST-001). Syntax only, gathered at the same moment as
    /// everything else here — it is a report, never a refusal, because
    /// refusing a whole configuration over one destination's typo would stop
    /// every set backing up.
    /// </summary>
    public string? AddressDefect { get; init; }

    /// <summary>
    /// Whole days since this destination was last proven; null when it never
    /// has been, or when the stamp is ahead of the clock.
    /// </summary>
    public int? VerificationAgeDays { get; init; }

    /// <summary>
    /// How long this destination's proof stands before it is called overdue,
    /// by kind (architecture 09 §4).
    /// </summary>
    public int VerificationBoundDays { get; init; } = DestinationStatus.LocalPathVerificationBoundDays;

    /// <summary>Whether a usable proof covers what this destination was last sent.</summary>
    public bool IsProvenCurrent =>
        VerifiedAt is not null && VerifiedObjects > 0 && VerifiedSequence >= SyncedSequence;

    /// <summary>
    /// Whether the proof still covers what was last sent but has gone
    /// unrefreshed past this destination's bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately narrowed to the case where nothing else is already
    /// complaining. A destination that is unproven, sequence-stale, or
    /// knowingly unprovable earns its own warning naming that situation; the
    /// age would be a second line about the same problem. What this catches is
    /// the case with no other symptom at all — a set that has stopped changing,
    /// so nothing syncs, so nothing re-verifies, and the row reads
    /// <c>proven</c> from a stamp that could be a year old. Verification only
    /// ever ran inside a sync, and a sync only ever ran when the archive moved
    /// on, so an idle set's proof froze and no existing signal said so.
    /// </para>
    /// <para>
    /// Warning only: <see cref="ProtectionState"/> is untouched
    /// (ADR-0027 amendment). An old proof is a proof, and demoting the whole
    /// set over its age would say data is at risk when what is actually true
    /// is that nobody has looked lately.
    /// </para>
    /// </remarks>
    public bool VerificationOverdue =>
        RequiresVerification && IsProvenCurrent && VerificationAgeDays > VerificationBoundDays;
}

/// <summary>
/// The facts the derivation consumes — plain values, so the derivation is
/// a pure function and this layer needs no engine, provider, or clock.
/// The host gathers them: the catalogue supplies the snapshot facts, the
/// sync ledger supplies each destination's state, and the platform supplies
/// the failure-domain comparisons.
/// </summary>
public sealed record StatusInputs
{
    /// <summary>The set's latest committed snapshot capture time; null when none exists.</summary>
    public required ulong? LatestSnapshotAt { get; init; }

    /// <summary>The latest snapshot's capture status (1 complete, 2 partial); null when none.</summary>
    public byte? LatestCaptureStatus { get; init; }

    /// <summary>The set's destinations, in declaration order (FR-DEST-004: never summarised into one flag).</summary>
    public required IReadOnlyList<DestinationStatusInput> Destinations { get; init; }

    /// <summary>Open damage findings against the set's staging archive.</summary>
    public required int DamageFindings { get; init; }

    /// <summary>Whether a damage report names required objects with no readable copy.</summary>
    public required bool RequiredObjectsMissing { get; init; }
}

/// <summary>
/// Derives the user-level status (10 §1). The never-merge rules hold by
/// construction: <c>Degraded</c> and <c>Unrecoverable</c> are distinct
/// outcomes with distinct warnings, and <c>Captured</c> can never become
/// <c>Protected</c> without an in-sync destination outside the source's
/// failure domain (PT-8, ADR-0034).
/// </summary>
/// <remarks>
/// This is the single place the vocabulary is decided. A client receives the
/// result over the command surface and never re-derives it (10 §3.1), which is
/// what keeps the never-merge rules enforceable at one site rather than at
/// every front end.
/// </remarks>
public static class StatusDeriver
{
    /// <summary>Derives one set's status from observed facts.</summary>
    /// <param name="inputs">The gathered facts.</param>
    /// <returns>The derived status with its warnings.</returns>
    public static BackupSetStatus Derive(StatusInputs inputs)
    {
        ThrowHelper.ThrowIfNull(inputs);

        var warnings = new List<string>();

        if (inputs.LatestSnapshotAt is null)
        {
            return new BackupSetStatus(ProtectionState.NeverBackedUp, null, ["No snapshot has ever committed for this set."]);
        }

        // The most recent proof of bytes at any destination, whatever state
        // that destination is in NOW: a claim is a date and a coverage, and a
        // later failure dates it rather than erases it (FR-VER-003).
        var latestClaim = inputs.Destinations
            .Where(destination => destination.VerifiedAt is not null && destination.VerifiedObjects > 0)
            .OrderByDescending(destination => destination.VerifiedAt!.Value)
            .Select(DetailOf)
            .FirstOrDefault();

        // Unrecoverable means data is already gone — it outranks
        // everything and is never softened into "a problem" (10 §1.1).
        if (inputs.RequiredObjectsMissing)
        {
            return new BackupSetStatus(
                ProtectionState.Unrecoverable,
                latestClaim,
                ["Required objects are missing or damaged with no replica able to heal them."]);
        }

        if (inputs.LatestCaptureStatus == 2)
        {
            warnings.Add("The latest snapshot is PARTIAL — its error manifest names what was not captured.");
        }

        if (inputs.DamageFindings > 0)
        {
            warnings.Add($"{inputs.DamageFindings} damage finding(s) are open — run `check`.");
            return new BackupSetStatus(ProtectionState.Degraded, latestClaim, warnings);
        }

        // The matrix, one row per destination — the truth every roll-up is
        // computed from, never invented beside (ADR-0028 §8).
        var protectedByAny = false;

        // "In sync" here includes a held previous backup during the catch-up
        // window: the independent destination IS in sync with the backup it
        // holds, which is precisely the claim the roll-up makes.
        var independentInSync = false;
        var capturedOnlyByAny = false;
        var supportedButNotInSync = false;
        DestinationStatusInput? verifiedBy = null;

        foreach (var destination in inputs.Destinations)
        {
            // Checked for every destination, not only the ones the protection
            // question reaches. A local-path replica sits inside the source's
            // failure domain and so can never earn Protected — but its proof
            // is exactly what licenses the staging trim to reclaim space
            // (FR-GC-009, ADR-0009 Amendment 4), so an overdue proof there
            // quietly stops space coming back. Skipping it because the row is
            // below same-site would hide the one consequence it has.
            if (destination.AddressDefect is { } defect)
            {
                // Named before anything has counted on it, rather than at the
                // first sync — which for a destination no set has synced yet
                // is never. A typo'd endpoint used to be discovered by the
                // fan-out, and only for a (set, destination) pair that had
                // been attempted at least once.
                warnings.Add($"'{destination.Name}' cannot be reached as declared: {defect}");
            }

            if (destination.VerificationOverdue)
            {
                warnings.Add(
                    $"'{destination.Name}' was last proven {destination.VerificationAgeDays} days ago, past the "
                    + $"{destination.VerificationBoundDays}-day bound for a "
                    + $"{DestinationLabel(destination.Kind)} destination — the proof still covers what was sent, "
                    + "but nothing has re-read those bytes since.");
            }

            switch (destination.Sync)
            {
                // Protected asks one question: if this machine is destroyed,
                // does a copy survive (FR-SNP-007, ADR-0018)? Same-site and
                // independent answer yes; same-volume and same-machine die
                // with it, however healthy their sync is.
                case DestinationSyncState.InSync when destination.Domain >= FailureDomain.SameSite:
                    protectedByAny = true;
                    independentInSync |= destination.Domain == FailureDomain.Independent;

                    // Verified current: proven bytes at least as new as the
                    // sync's own claim (peer-protocol 04). A destination whose
                    // last sync proved nothing — one excused from proving, or
                    // one whose samples could not be read — stays Protected:
                    // the stamp goes stale, never wrong.
                    // A stamp that proved no ranges is not a verification, and
                    // the word must not be reachable from one: the engine
                    // withholds such a stamp, and this refuses to honour one
                    // that reached the ledger by any other route.
                    if (destination.IsProvenCurrent
                        && (verifiedBy is null || destination.VerifiedAt > verifiedBy.VerifiedAt))
                    {
                        verifiedBy = destination;
                    }
                    else if (!destination.RequiresVerification)
                    {
                        // Named every time, not once at configuration: this is
                        // a copy nobody can audit, and the reason it is
                        // tolerated should stay in front of the person who
                        // chose to tolerate it (FR-VER-006).
                        warnings.Add(
                            $"'{destination.Name}' is in sync but cannot be proven — it was knowingly accepted "
                            + "unverifiable, so it never counts as verified and never licenses reclaiming local space.");
                    }
                    else
                    {
                        warnings.Add(
                            $"'{destination.Name}' is in sync but its copy is not proven"
                            + $"{(destination.VerifiedAt is null ? " — no challenge has ever succeeded there" : " for what it was last sent")}.");
                    }

                    break;

                case DestinationSyncState.InSync:
                    capturedOnlyByAny = true;
                    warnings.Add(
                        $"'{destination.Name}' shares the source's failure domain ({DomainLabel(destination.Domain)}) — a safeguard against mistakes, none against losing the machine.");
                    break;

                case DestinationSyncState.NotSupported:
                    // A stated incapacity, never a failure (FR-DEST-005) —
                    // but no protection comes from it either.
                    warnings.Add($"'{destination.Name}' is not served yet: {destination.Detail ?? "kind not supported"}.");
                    break;

                // The catch-up window keeps the badge (ADR-0050 amendment):
                // a destination behind only because a newer backup awaits its
                // next sync pass still holds the previous backup — present
                // and restorable — so the set keeps exactly the tier that
                // held copy earned yesterday. Never a `verifiedBy` candidate:
                // the proof may not cover the run now replicating; Verified
                // returns with in-sync. No time bound, deliberately — a sync
                // attempt that fails reports itself (RecordFailure writes the
                // ledger, the next poll reads a fault and degrades), the
                // catch-up predicate never backs off, this derivation takes
                // no clock, and LastAttemptAt moves only at attempt
                // completion, so any bound would false-alarm midway through
                // a long legitimate replication. Every other cause — a
                // reported fault, a never-synced pair, an owed seed — and
                // every harder state still degrades below.
                case DestinationSyncState.Behind when destination.HoldsPreviousBackup
                    && destination.Domain >= FailureDomain.SameSite:
                    protectedByAny = true;
                    independentInSync |= destination.Domain == FailureDomain.Independent;
                    warnings.Add(
                        $"'{destination.Name}' holds the previous backup; the newest is still replicating — it catches up on the next sync pass.");
                    break;

                case DestinationSyncState.Behind when destination.HoldsPreviousBackup:
                    capturedOnlyByAny = true;
                    warnings.Add(
                        $"'{destination.Name}' holds the previous backup; the newest is still replicating — it catches up on the next sync pass.");
                    break;

                default:
                    supportedButNotInSync = true;
                    warnings.Add(
                        $"'{destination.Name}' is {Describe(destination.Sync)}{(destination.Detail is null ? "" : $": {destination.Detail}")}.");
                    break;
            }
        }

        if (protectedByAny)
        {
            if (!independentInSync)
            {
                // Honest about the residue (ADR-0018): a same-site copy
                // answers the machine question, not the site one.
                warnings.Add(
                    "Protection rests on same-site destination(s) — a copy at the same site survives losing this machine, not losing the site.");
            }

            // Verified only ever appears WITH coverage and age (10 §1.2), and
            // only from a destination that is in sync, off-domain, and whose
            // proof covers what the sync delivered — bytes read back off the
            // destination's own disk, never its word (FR-VER-001).
            return verifiedBy is not null
                ? new BackupSetStatus(ProtectionState.Verified, DetailOf(verifiedBy), warnings)
                : new BackupSetStatus(ProtectionState.Protected, null, warnings);
        }

        if (supportedButNotInSync)
        {
            // A destination that should hold the data does not — recoverable
            // today, and the reason each is not is already in the warnings.
            return new BackupSetStatus(ProtectionState.Degraded, latestClaim, warnings);
        }

        // What remains: in sync only within the source's failure domain, or
        // nothing but stated incapacities. Captured is the honest word —
        // commit to staging succeeded, and nothing off-domain holds a copy.
        if (!capturedOnlyByAny)
        {
            warnings.Add("No destination holds this set's data yet.");
        }

        return new BackupSetStatus(ProtectionState.Captured, latestClaim, warnings);
    }

    /// <summary>The stamp as the vocabulary carries it: coverage and age, never a tick (10 §1.2).</summary>
    private static VerificationDetail DetailOf(DestinationStatusInput destination) => new(
        destination.VerifiedPopulation > 0
            ? (double)destination.VerifiedObjects / destination.VerifiedPopulation
            : 0,
        destination.VerifiedAt!.Value);

    private static string Describe(DestinationSyncState state) => state switch
    {
        DestinationSyncState.Behind => "behind",
        DestinationSyncState.Unavailable => "unreachable",
        DestinationSyncState.Failed => "failing",
        _ => state.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Where one destination stands on proving itself, for the status matrix
    /// (FR-VER-003, FR-VER-006). Four answers, because "not verified" hides
    /// three different situations calling for three different responses.
    /// </summary>
    /// <param name="destination">The destination's observed facts.</param>
    public static string VerificationLabel(DestinationStatusInput destination)
    {
        ThrowHelper.ThrowIfNull(destination);

        if (destination.IsProvenCurrent)
        {
            return "proven";
        }

        return destination switch
        {
            { RequiresVerification: false } => "unprovable (accepted)",
            { VerifiedAt: null } => "unproven",
            _ => "stale",
        };
    }

    /// <summary>The configuration's spelling of a destination kind.</summary>
    private static string DestinationLabel(DestinationKind kind) => kind switch
    {
        DestinationKind.LocalPath => "local-path",
        DestinationKind.Peer => "peer",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>The configuration's spelling of a domain (FR-SNP-007).</summary>
    public static string DomainLabel(FailureDomain domain) => domain switch
    {
        FailureDomain.SameVolume => "same-volume",
        FailureDomain.SameMachine => "same-machine",
        FailureDomain.SameSite => "same-site",
        _ => "independent",
    };
}
