using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace FallbackPlan.Web;

/// <summary>
/// What stands in for the local socket's operating-system peer credentials once
/// a browser is in the loop (ADR-0036 §3).
/// </summary>
/// <remarks>
/// A loopback port is reachable by every process on the machine and — through a
/// hostile page the operator happens to have open — by script in the browser
/// itself. Two checks close the two known attack classes: requiring a per-run
/// bearer token on every data request closes cross-site requests, because
/// another origin can neither read nor send it; refusing any request whose
/// <c>Host</c> is not loopback closes DNS rebinding, where an attacker's
/// hostname resolves to 127.0.0.1 so their page's requests arrive same-origin.
/// </remarks>
public sealed class ConsoleAuth
{
    private const string BearerPrefix = "Bearer ";
    private const string TokenParameter = "token";

    private readonly byte[] _tokenUtf8;

    private ConsoleAuth(string token)
    {
        Token = token;
        _tokenUtf8 = Encoding.UTF8.GetBytes(token);
    }

    /// <summary>The token a caller must present. Shown once, in the printed URL.</summary>
    public string Token { get; }

    /// <summary>Draws a fresh 256-bit token for this run.</summary>
    /// <returns>The authenticator.</returns>
    public static ConsoleAuth CreateWithRandomToken() =>
        new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));

    /// <summary>
    /// Whether the request names a loopback host. Checked on every request,
    /// static page included — a rebound hostname gets nothing at all.
    /// </summary>
    /// <param name="host">The request's <c>Host</c> header.</param>
    /// <returns>Whether it is loopback.</returns>
    public static bool IsLoopbackHost(HostString host)
    {
        var name = host.Host.Trim('[', ']');
        return name.Equals("127.0.0.1", StringComparison.Ordinal)
            || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("::1", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the request presents this run's token — as a bearer header, or
    /// as a <c>token</c> query parameter for the one caller that cannot set
    /// headers, the browser's <c>EventSource</c>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>Whether the token matched.</returns>
    public bool Authorizes(HttpRequest request)
    {
        Bodu.ThrowHelper.ThrowIfNull(request);

        string? presented = null;

        string? header = request.Headers.Authorization;
        if (header is not null && header.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            presented = header[BearerPrefix.Length..];
        }
        else if (request.Query.TryGetValue(TokenParameter, out var query))
        {
            presented = query;
        }

        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        // Fixed-time over UTF-8 bytes; the length check leaks only the length
        // of the caller's own guess, which the caller already knows.
        var presentedUtf8 = Encoding.UTF8.GetBytes(presented);
        return presentedUtf8.Length == _tokenUtf8.Length
            && CryptographicOperations.FixedTimeEquals(presentedUtf8, _tokenUtf8);
    }
}
