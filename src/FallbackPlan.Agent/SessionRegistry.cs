using Bodu;
using System.Security.Cryptography;

namespace FallbackPlan.Agent;

/// <summary>One live session.</summary>
/// <param name="Token">The opaque token a client presents.</param>
/// <param name="User">Whose session it is.</param>
/// <param name="Role">That account's role.</param>
/// <param name="IdleExpiresAtUnixMilliseconds">When it lapses if nothing uses it.</param>
/// <param name="AbsoluteExpiresAtUnixMilliseconds">When it ends regardless of use.</param>
public sealed record Session(
    string Token,
    string User,
    UserRole Role,
    long IdleExpiresAtUnixMilliseconds,
    long AbsoluteExpiresAtUnixMilliseconds);

/// <summary>
/// The installation's live sessions (FR-USR-003; ADR-0045 §5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Memory only.</b> Nothing here is written down, and three things follow,
/// all of them the point: there is no session file to steal, there is no expiry
/// sweep over durable state to get wrong, and a restart logs everyone out. The
/// last is a blunt revocation story, and a comprehensible one — it is what an
/// operator already expects from a service they just restarted — where a
/// credential file that outlives the process is the stale-token failure
/// ADR-0028 §5 rejected on prior art.
/// </para>
/// <para>
/// The token is a credential, so it is drawn from the CSPRNG at 256 bits and
/// compared in constant time. It is not a name, an index, or anything derived
/// from the account: a token a client could predict from a username would make
/// the password decorative.
/// </para>
/// </remarks>
public sealed class SessionRegistry
{
    /// <summary>How long a session survives with nothing using it.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromHours(8);

    /// <summary>How long a session lasts however busy it is.</summary>
    /// <remarks>
    /// An absolute expiry as well as an idle one, because a console left open
    /// on a screen somewhere keeps its session alive forever on idle timeout
    /// alone, which is the case an idle timeout is least able to see.
    /// </remarks>
    public static readonly TimeSpan DefaultAbsoluteLifetime = TimeSpan.FromDays(7);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Func<long> _clock;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _absoluteLifetime;

    /// <summary>Creates a registry.</summary>
    /// <param name="clock">Unix milliseconds, for a test that needs a fixed one.</param>
    /// <param name="idleTimeout">How long a session survives unused.</param>
    /// <param name="absoluteLifetime">How long it lasts regardless.</param>
    public SessionRegistry(
        Func<long>? clock = null,
        TimeSpan? idleTimeout = null,
        TimeSpan? absoluteLifetime = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        _absoluteLifetime = absoluteLifetime ?? DefaultAbsoluteLifetime;
    }

    /// <summary>Mints a session for a verified account.</summary>
    /// <param name="user">The account name.</param>
    /// <param name="role">Its role.</param>
    /// <returns>The new session.</returns>
    public Session Mint(string user, UserRole role)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(user);

        var now = _clock();
        var session = new Session(
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            user,
            role,
            now + (long)_idleTimeout.TotalMilliseconds,
            now + (long)_absoluteLifetime.TotalMilliseconds);

        lock (_gate)
        {
            _sessions[session.Token] = session;
        }

        return session;
    }

    /// <summary>
    /// Resolves a token, extending its idle expiry, or null when it is unknown
    /// or has lapsed.
    /// </summary>
    /// <param name="token">The token presented.</param>
    /// <remarks>
    /// A lapsed session is removed as it is found. That is the whole of the
    /// expiry mechanism: with nothing durable to sweep, a session that is never
    /// presented again simply dies with the process.
    /// </remarks>
    public Session? Resolve(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        lock (_gate)
        {
            // Constant-time over the candidates rather than a dictionary hit:
            // a token is a credential, and a lookup that returns faster for a
            // near-miss is a way to learn one character at a time.
            var found = _sessions.Values.FirstOrDefault(session =>
                CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(session.Token),
                    System.Text.Encoding.ASCII.GetBytes(token)));

            if (found is null)
            {
                return null;
            }

            var now = _clock();
            if (now >= found.IdleExpiresAtUnixMilliseconds || now >= found.AbsoluteExpiresAtUnixMilliseconds)
            {
                _sessions.Remove(found.Token);
                return null;
            }

            var extended = found with
            {
                IdleExpiresAtUnixMilliseconds = Math.Min(
                    now + (long)_idleTimeout.TotalMilliseconds,
                    found.AbsoluteExpiresAtUnixMilliseconds),
            };

            _sessions[extended.Token] = extended;
            return extended;
        }
    }

    /// <summary>Ends one session.</summary>
    /// <param name="token">The token to revoke.</param>
    /// <returns>Whether there was one to end.</returns>
    public bool Revoke(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        lock (_gate)
        {
            return _sessions.Remove(token);
        }
    }

    /// <summary>Ends every session an account holds — what removing that account has to do.</summary>
    /// <param name="user">The account name.</param>
    /// <returns>How many sessions ended.</returns>
    public int RevokeAllFor(string user)
    {
        lock (_gate)
        {
            var doomed = _sessions.Values
                .Where(session => string.Equals(session.User, user, StringComparison.OrdinalIgnoreCase))
                .Select(session => session.Token)
                .ToArray();

            foreach (var token in doomed)
            {
                _sessions.Remove(token);
            }

            return doomed.Length;
        }
    }

    /// <summary>The live sessions, for a diagnostic or a test. Never the tokens.</summary>
    public IReadOnlyList<(string User, long IdleExpiresAt)> Live()
    {
        lock (_gate)
        {
            var now = _clock();
            return
            [
                .. _sessions.Values
                    .Where(session =>
                        now < session.IdleExpiresAtUnixMilliseconds
                        && now < session.AbsoluteExpiresAtUnixMilliseconds)
                    .Select(session => (session.User, session.IdleExpiresAtUnixMilliseconds)),
            ];
        }
    }
}
