using Bodu;

namespace FallbackPlan.Application;

/// <summary>
/// The one place a peer destination's <c>host:port</c> is taken apart
/// (FR-DEST-006, FR-DEST-009).
/// </summary>
/// <remarks>
/// <para>
/// It lives here, and is public, so the admission check and the dialling path
/// cannot disagree about what a valid endpoint is. They did: both split on the
/// last colon and both accepted <c>fe80::1</c> as host <c>fe80:</c> on port
/// <c>1</c> — an address a person plainly meant as portless, taken as a
/// well-formed one, reported as fine by status, and then dialled at a host
/// that cannot exist. The whole point of checking an address before the first
/// sync is to catch exactly that.
/// </para>
/// <para>
/// A bracketed IPv6 literal is the reason splitting on the last colon cannot
/// work on its own: <c>[::1]:9443</c> is the only spelling that carries both
/// an IPv6 host and a port, and it is the spelling this accepts. An
/// unbracketed address with two or more colons is refused rather than guessed
/// at, because guessing is what produced the defect.
/// </para>
/// </remarks>
public static class PeerEndpoint
{
    /// <summary>Splits <c>host:port</c>, or says why it is not one.</summary>
    /// <param name="endpoint">The declared endpoint text.</param>
    /// <param name="host">The host to dial, brackets stripped from an IPv6 literal.</param>
    /// <param name="port">The port to dial.</param>
    /// <param name="defect">Why not, when it is not an endpoint.</param>
    public static bool TryParse(string endpoint, out string host, out int port, out string? defect)
    {
        ThrowHelper.ThrowIfNull(endpoint);

        host = string.Empty;
        port = 0;

        // A bracketed IPv6 literal: the port, if any, follows the bracket.
        if (endpoint.StartsWith('['))
        {
            var close = endpoint.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                defect = $"endpoint '{endpoint}' opens an IPv6 literal and never closes it.";
                return false;
            }

            host = endpoint[1..close];
            var remainder = endpoint[(close + 1)..];
            return host.Length > 0 && remainder.StartsWith(':')
                ? TryPort(endpoint, remainder[1..], ref port, out defect)
                : Refuse(endpoint, out defect);
        }

        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0)
        {
            return Refuse(endpoint, out defect);
        }

        host = endpoint[..separator];
        if (host.Contains(':', StringComparison.Ordinal))
        {
            // Two or more colons and no brackets: an IPv6 address written
            // without a port. Splitting on the last one would hand back a
            // host ending in a colon and a port made of the final group.
            defect =
                $"endpoint '{endpoint}' looks like an IPv6 address with no port; write it as [{endpoint}]:<port>.";
            return false;
        }

        return TryPort(endpoint, endpoint[(separator + 1)..], ref port, out defect);
    }

    private static bool TryPort(string endpoint, string text, ref int port, out string? defect)
    {
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, Culture, out port))
        {
            return Refuse(endpoint, out defect);
        }

        if (port is <= 0 or > 65535)
        {
            defect = $"endpoint '{endpoint}' names port {port}, which is not a port.";
            return false;
        }

        defect = null;
        return true;
    }

    private static bool Refuse(string endpoint, out string? defect)
    {
        defect = $"endpoint '{endpoint}' is not host:port.";
        return false;
    }

    /// <summary>
    /// A port is digits, in every language. Parsed invariantly and with no
    /// styles allowed, so a culture's group separator or sign cannot smuggle
    /// something past that the dialler would then read differently.
    /// </summary>
    private static System.Globalization.CultureInfo Culture =>
        System.Globalization.CultureInfo.InvariantCulture;
}
