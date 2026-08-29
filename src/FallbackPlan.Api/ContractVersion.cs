using System.Globalization;

namespace FallbackPlan.Api;

/// <summary>
/// The client↔service contract version (ADR-0028 §7).
/// </summary>
/// <remarks>
/// <para>
/// Versioned independently of the repository format and of the peer protocol.
/// ADR-0003 anticipates exactly this: repository encoding is canonical CBOR,
/// while "wire protocols are versioned independently and may use a different
/// encoding". Nothing here is durable — a contract change never touches a byte
/// already written.
/// </para>
/// <para>
/// Compatibility is by <b>major</b>. A client and service that disagree on the
/// major version must refuse to proceed with both versions named (FR-SVC-007),
/// because the failure users of a legacy backup service met was an
/// unexplained blank window. A
/// console in topology 3 routinely meets services at several versions at once,
/// so the refusal is per service and never stops the console starting.
/// </para>
/// </remarks>
/// <param name="Major">Incompatible changes.</param>
/// <param name="Minor">Additive changes an older peer can ignore.</param>
public readonly record struct ContractVersion(int Major, int Minor)
{
    /// <summary>The version this build speaks.</summary>
    /// <remarks>
    /// 1.7 added the configuration surface: set and destination CRUD, the
    /// folder browser, draft validation, and the pairing-invite verbs
    /// (ADR-0037). 1.8 added preview_set_changes / set_change_preview, made
    /// upsert_backup_set answer a material root-or-rules edit with
    /// configuration_change, and honours run_backup's full flag over the
    /// service (ADR-0038). 1.9 added the operator-loop verbs —
    /// list_notices / acknowledge_notice over the notices ledger and unpair
    /// for ending a pairing from a console — and enriched list_directory
    /// with modification times, change markers against the set's previous
    /// snapshot, and the names deleted since it (ADR-0039). 1.10 added
    /// multi-root sets (ADR-0040): roots on the set descriptor and on
    /// preview_set_changes — upsert accepts roots or root, roots winning —
    /// and the preview answers a draft with no saved set against an empty
    /// baseline. 1.11 added the guided restore (ADR-0041):
    /// open_restore_source / close_restore_source over per-set staging,
    /// replica and peer sources; source, several paths, target, existing
    /// and in-place options on the restore verbs; plan conflicts and the
    /// persisted-receipt summary on their results; and the archives root on
    /// describe_service. 1.12 added write-only repositories (ADR-0042):
    /// provision_write_only_set carrying the sealed write bundle for both the
    /// create and adopt ceremonies, the optional sealed restore-grant
    /// envelope on open_restore_source, the grant-recipient public key on
    /// describe_service, and the sealed-record count on verification results
    /// so a records-level sweep of a write-only set reads as neither damage
    /// nor a clean content check.
    /// 1.13 added first-run setup (ADR-0044): provision_installation
    /// carrying the sealed write bundle for the installation rather than for
    /// a named set — a service on its first run has no sets — and the setup
    /// state on describe_service, so a client learns it must capture the
    /// passphrase before anything else. Local callers only; a paired remote
    /// console is refused.
    /// 1.14 finished that ceremony (ADR-0044's amendment, ADR-0013's):
    /// confirm_recovery_kit records that the installation's kit was saved,
    /// describe_service gains a kit_required state between setup_required
    /// and ready plus this device's public identity for the kit to record,
    /// and validate_set_draft answers a draft's roots and destinations with
    /// a failure-domain warning (FR-SNP-007).
    /// 1.15 opened the diagnostics the engine had been writing to nobody
    /// (ADR-0043 §6, FR-SVC-010): get_diagnostics reports the levels in
    /// force and whether a durable sink exists — never where it is (T-16);
    /// read_log serves the in-memory ring by cursor, paginated because
    /// FrameCodec caps a frame at 8 MiB and "send me everything" is not a
    /// thing a log reader may ask; and set_log_level changes a level
    /// without a restart, which matters because the level a machine needs
    /// is only known once it has already misbehaved. Records cross
    /// rendered rather than as their name/value state, because rendering
    /// is where redaction happens: a local caller is served in full, a
    /// paired remote console redacted, and set_log_level is refused to a
    /// remote caller outright. describe_service carries the effective
    /// level so a console can show it without a second round trip.
    /// 1.15 also carries the recovery kit's status on describe_service
    /// (FR-KIT-005): never_saved or saved, with when it was confirmed. Two
    /// values rather than the three the requirement's wording implies —
    /// an installation kit carries no destinations, so the stated staleness
    /// trigger cannot fire, and its salt, parameters and sealing key are
    /// fixed for the installation's life, so nothing else can make it
    /// stale either (ADR-0013 as amended). Surfaced continuously rather
    /// than only during the ceremony, which is what "continuously" means.
    /// 1.16 gave the product a way to say who is acting (ADR-0045,
    /// FR-USR-001..006): login mints a session, resume_session presents an
    /// existing one on a new connection — which the web console needs,
    /// because it opens a fresh connection for every request it relays —
    /// logout revokes it, and list_users / create_user / delete_user /
    /// change_password manage the accounts. describe_service carries who is
    /// signed in and their role, and reports users_required when an
    /// installation has finished setup but has no accounts yet. Sessions are
    /// held in the service's memory alone, so a restart signs everyone out;
    /// there is no session file, which is the stale-credential failure
    /// ADR-0028 §5 rejected. A session token crosses on the two verbs that
    /// mint and present it and nowhere else, and no password or hash reaches
    /// any result or any log record.
    /// 1.17 carries the backup pool's ordering (ADR-0047): backup sets and
    /// destinations gain an optional priority on their descriptors — higher
    /// runs or ships first among waiting work of the same initiation, and a
    /// person still outranks any priority. Null on an upsert preserves, so a
    /// pre-1.17 client edits nothing it cannot see. Alongside (no wire
    /// change): saving a new set queues its first backup at once, and a set
    /// gaining a destination queues that destination's seed.
    /// 1.18 carries retire_staging (ADR-0046): a migrated direct-ship set's
    /// staging archive is deleted only by this explicit verb, refused while
    /// anything staging holds has not reached a destination.
    /// 1.19 puts the full-backup facts on the status matrix (ADR-0047 §§5–6):
    /// each destination row says when its baseline completed and whether the
    /// pair is still owed its seed, so a console can render "awaiting full
    /// backup" instead of a bare "behind". Additive with defaults — a
    /// pre-1.19 client simply does not see the fields.
    /// 1.20 puts the counted plan on the progress stream (FR-SVC-006's
    /// determinate half): a backup first counts what it will process, and
    /// every progress report then carries total_files and total_bytes — the
    /// denominator a client divides by for a percentage and a time estimate.
    /// Null until the count completes and from producers that never count
    /// (the single-stream path, verification sweeps, pre-1.20 services), so
    /// additive with defaults: a client seeing null falls back to the
    /// indeterminate meter it always had. The watch frame also gains the
    /// client's session token: a watch takes its own connection with its own
    /// authentication gate, and without the session every watch on an
    /// installation with accounts was anonymous — answered with an empty
    /// stream, so no progress ever reached a signed-in console. Additive the
    /// same way: a pre-1.20 service ignores the field, a pre-1.20 client
    /// keeps its anonymous watch.
    /// 1.21 adds restart_service (ADR-0049): an in-process recycle of the
    /// running service — Owner-only, local callers only, refused before
    /// setup and under --once. The acknowledgement is flushed before the
    /// teardown, and the restart signs every session out (the FR-USR-003
    /// contract, unchanged).
    /// </remarks>
    public static ContractVersion Current { get; } = new(1, 21);

    /// <summary>Whether a peer at <paramref name="other"/> can be spoken to.</summary>
    /// <param name="other">The peer's version.</param>
    /// <returns><see langword="true"/> when the major versions match.</returns>
    public bool IsCompatibleWith(ContractVersion other) => Major == other.Major;

    /// <summary>Renders as <c>major.minor</c>.</summary>
    /// <returns>The rendered version.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");

    /// <summary>Parses a <c>major.minor</c> rendering.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="version">The parsed version.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> was well formed.</returns>
    public static bool TryParse(string? text, out ContractVersion version)
    {
        version = default;
        if (text is null)
        {
            return false;
        }

        var separator = text.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        if (!int.TryParse(text[..separator], CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(text[(separator + 1)..], CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        version = new ContractVersion(major, minor);
        return true;
    }

    /// <summary>
    /// The refusal message for an incompatible peer — it names both versions,
    /// because "cannot connect" is what makes this failure infamous.
    /// </summary>
    /// <param name="clientVersion">The client's version.</param>
    /// <param name="serviceVersion">The service's version.</param>
    /// <returns>The message to show.</returns>
    public static string DescribeMismatch(ContractVersion clientVersion, ContractVersion serviceVersion) =>
        $"The client speaks contract {clientVersion} and the service speaks {serviceVersion}. "
        + "These are incompatible; upgrade whichever is older rather than retrying.";
}
