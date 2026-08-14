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
/// because the failure CrashPlan users met was an unexplained blank window. A
/// console in topology 3 routinely meets services at several versions at once,
/// so the refusal is per service and never stops the console starting.
/// </para>
/// </remarks>
/// <param name="Major">Incompatible changes.</param>
/// <param name="Minor">Additive changes an older peer can ignore.</param>
public readonly record struct ContractVersion(int Major, int Minor)
{
    /// <summary>The version this build speaks.</summary>
    public static ContractVersion Current { get; } = new(1, 4);

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
