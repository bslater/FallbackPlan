using Bodu;
using FallbackPlan.Domain;
using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Api;

namespace FallbackPlan.Cli;

/// <summary>
/// Where the CLI keeps the session token a <c>login</c> minted
/// (FR-USR-003, FR-USR-006; ADR-0045 §5).
/// </summary>
/// <remarks>
/// <para>
/// The service holds sessions in memory and never writes one down. A
/// <em>client</em> has to, or every command would be a login — and a command
/// line is not a place to type a password once per verb. So the token is
/// cached here, owner-only, in the same state directory the CLI already treats
/// as private.
/// </para>
/// <para>
/// This is not the token file ADR-0028 §5 rejected, and the difference is
/// worth being precise about. That objection was to a credential needed to
/// <em>reach</em> the service: a stale one made the service unreachable while
/// it was running perfectly well, with no way for a user to tell. This token
/// reaches nothing — the socket's permissions are still the whole of the
/// connection check — and when it is stale the answer is a sentence naming
/// <c>fallbackplan login</c>, not a mystery.
/// </para>
/// </remarks>
public sealed class SessionCache
{
    private sealed record Model
    {
        [JsonPropertyName("token")]
        public required string Token { get; init; }

        [JsonPropertyName("user")]
        public required string User { get; init; }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _path;

    /// <summary>Opens the cache for a state directory.</summary>
    /// <param name="stateDirectory">The CLI's state directory.</param>
    public SessionCache(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        _path = Path.Combine(stateDirectory, "session.json");
    }

    /// <summary>The cached token, or null when there is none or it does not parse.</summary>
    /// <remarks>
    /// A cache that does not parse answers "no session" rather than throwing.
    /// The worst it can cost is one login, and refusing to run at all because a
    /// convenience file is malformed would be a poor trade.
    /// </remarks>
    public string? Token
    {
        get
        {
            try
            {
                return File.Exists(_path)
                    ? JsonSerializer.Deserialize<Model>(File.ReadAllText(_path), SerializerOptions)?.Token
                    : null;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>The account the cached session belongs to, for telling the operator who they are.</summary>
    public string? User
    {
        get
        {
            try
            {
                return File.Exists(_path)
                    ? JsonSerializer.Deserialize<Model>(File.ReadAllText(_path), SerializerOptions)?.User
                    : null;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Stores a minted session.</summary>
    /// <param name="session">What the service answered.</param>
    public void Save(SessionResult session)
    {
        ThrowHelper.ThrowIfNull(session);

        AtomicFile.WriteAllText(
            _path,
            JsonSerializer.Serialize(new Model { Token = session.Token, User = session.User }, SerializerOptions));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>Forgets the cached session.</summary>
    public void Clear()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing to do about it, and nothing worth failing a logout over:
            // the service has already been told to revoke, which is the half
            // that matters.
        }
    }

    /// <summary>
    /// Presents the cached session on a freshly opened connection, if there is
    /// one to present.
    /// </summary>
    /// <param name="client">The connection.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>Whether a session is now attached to this connection.</returns>
    /// <remarks>
    /// Silent when there is no session, and silent when the service does not
    /// want one: a service that predates contract 1.16 answers
    /// <c>InvalidArgument</c> through the existing catch-all, and an
    /// installation with no accounts has nothing to attach. Neither is a
    /// failure — the command that follows will either work or be refused by
    /// name, and that refusal is the better place to explain.
    /// </remarks>
    public async ValueTask<bool> PresentAsync(IFallbackPlanClient client, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(client);

        if (Token is not { Length: > 0 } token)
        {
            return false;
        }

        var presented = await client
            .ExecuteAsync(new ResumeSessionCommand(token), cancellationToken)
            .ConfigureAwait(false);

        return presented is SessionResult;
    }
}
