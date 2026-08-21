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
    /// </remarks>
    public static ContractVersion Current { get; } = new(1, 13);

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
