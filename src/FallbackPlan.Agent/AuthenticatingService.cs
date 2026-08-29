using Bodu;
using FallbackPlan.Api;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Agent;

/// <summary>
/// One connection's view of the service, carrying who is acting on it
/// (FR-USR-001, FR-USR-003; ADR-0045 §5).
/// </summary>
/// <remarks>
/// <para>
/// A decorator rather than a field on <see cref="ServiceCommand"/>: no existing
/// verb changes, no client that does not care has to learn anything, and the
/// one piece of per-connection state lives in the one object that is per
/// connection. Both listeners build one of these per accepted connection.
/// </para>
/// <para>
/// <b>The session is not the connection.</b> It is minted once, lives in
/// <see cref="SessionRegistry"/>, and is <em>presented</em> by a connection
/// through <see cref="ResumeSessionCommand"/>. That separation is not
/// decoration: the web console opens a fresh transport connection for every
/// request it relays, so a session welded to one connection would sign the
/// operator out between clicking two buttons.
/// </para>
/// <para>
/// <b>Enforcement engages only once the installation has accounts.</b> An
/// installation with none is one that predates this feature or has not finished
/// setup, and refusing every verb there would brick a working installation on
/// upgrade and leave no way to create the first account. Instead
/// <c>describe_service</c> reports <c>users_required</c>, so a client says so
/// rather than an installation silently staying open forever.
/// </para>
/// </remarks>
public sealed class AuthenticatingService : IFallbackPlanService
{
    /// <summary>The setup state a client is shown when the installation has no accounts yet.</summary>
    public const string UsersRequiredState = "users_required";

    private readonly IFallbackPlanService _inner;
    private readonly UserStore _users;
    private readonly SessionRegistry _sessions;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private string? _token;

    /// <summary>Wraps the shared handler for one connection.</summary>
    /// <param name="inner">The handler every connection shares.</param>
    /// <param name="users">The installation's accounts.</param>
    /// <param name="sessions">The installation's live sessions.</param>
    /// <param name="logger">Where authentication outcomes are recorded.</param>
    public AuthenticatingService(
        IFallbackPlanService inner,
        UserStore users,
        SessionRegistry sessions,
        ILogger? logger = null)
    {
        ThrowHelper.ThrowIfNull(inner);
        ThrowHelper.ThrowIfNull(users);
        ThrowHelper.ThrowIfNull(sessions);

        _inner = inner;
        _users = users;
        _sessions = sessions;
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>Whose session this connection has presented, or null.</summary>
    public Session? Current
    {
        get
        {
            string? token;
            lock (_gate)
            {
                token = _token;
            }

            return token is null ? null : _sessions.Resolve(token);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ServiceResult> ExecuteAsync(
        ServiceCommand command, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(command);

        switch (command)
        {
            case LoginCommand login:
                return await LoginAsync(login, cancellationToken).ConfigureAwait(false);

            case ResumeSessionCommand resume:
                return Resume(resume);

            case LogoutCommand:
                return Logout();

            case DescribeServiceCommand:
                return Describe(
                    await _inner.ExecuteAsync(command, cancellationToken).ConfigureAwait(false));
        }

        // Everything past here needs a person, once there is one to need.
        if (_users.HasAccounts)
        {
            if (Current is not { } session)
            {
                Log.CommandRefusedUnauthenticated(_log, command.GetType().Name);
                return new ServiceError(
                    ServiceErrorReason.Refused,
                    "This connection has not signed in. Log in first — `fallbackplan login`, or the "
                    + "console's sign-in screen — then retry (FR-USR-001).");
            }

            if (command is ListUsersCommand or CreateUserCommand or DeleteUserCommand or ChangePasswordCommand)
            {
                return await ManageAsync(command, session, cancellationToken).ConfigureAwait(false);
            }

            // The second Owner-only privilege after account management
            // (ADR-0049): a restart interrupts everyone's runs and signs
            // everybody out, which is not an operator's call to make.
            if (command is RestartServiceCommand && !_users.MayManageAccounts(session.User))
            {
                return NotTheOwner("restart the service");
            }
        }
        else if (command is ListUsersCommand or CreateUserCommand or DeleteUserCommand or ChangePasswordCommand)
        {
            // The bootstrap window: no accounts yet, so the first create_user
            // is what produces the owner. Reaching this connection at all was
            // already authorised by the operating system or by a pinned
            // pairing (ADR-0045 §1) — this is not an unguarded door, it is the
            // only door through which the first account can arrive.
            return await ManageAsync(command, session: null, cancellationToken).ConfigureAwait(false);
        }
        else if (command is RestartServiceCommand)
        {
            // Deliberately NOT in the bootstrap window: it admits exactly the
            // verbs that create the first account, and an unset-up
            // installation is not restartable by whoever can reach the socket.
            return new ServiceError(
                ServiceErrorReason.Refused,
                "The installation has no accounts yet, so nobody owns a restart. Finish setup — the "
                + "first account is the owner — and restart as that account.");
        }

        return await _inner.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken)
    {
        // A progress stream carries set names and job outcomes, which is the
        // same class of thing every gated verb answers with. It is gated the
        // same way, and an unauthenticated watch simply ends rather than
        // hanging open on nothing.
        return _users.HasAccounts && Current is null
            ? Empty()
            : _inner.WatchAsync(cancellationToken);

        static async IAsyncEnumerable<JobProgressEvent> Empty()
        {
            await ValueTask.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    private async ValueTask<ServiceResult> LoginAsync(LoginCommand login, CancellationToken cancellationToken)
    {
        var verified = await _users
            .VerifyAsync(login.User ?? string.Empty, login.Password ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (!verified.IsOk)
        {
            // Named at Warning with the account, never with the password and
            // never with what was wrong about it: "no such account" and "wrong
            // password" answer alike, so the log does not become the name
            // oracle the verb refuses to be.
            Log.AuthenticationFailed(_log, login.User ?? "(none)");

            return new ServiceError(
                ServiceErrorReason.Refused,
                "That account name and password were not accepted.");
        }

        var session = _sessions.Mint(verified.User!.Name, verified.User.Role);
        lock (_gate)
        {
            _token = session.Token;
        }

        var role = session.Role.ToString();
        Log.SignedIn(_log, session.User, role);
        return Describe(session);
    }

    private ServiceResult Resume(ResumeSessionCommand resume)
    {
        if (_sessions.Resolve(resume.Token) is not { } session)
        {
            Log.SessionResumeRefused(_log);
            return new ServiceError(
                ServiceErrorReason.Refused,
                "That session is not current — it expired, was signed out, or did not survive a "
                + "service restart. Log in again (ADR-0045 §5).");
        }

        lock (_gate)
        {
            _token = session.Token;
        }

        Log.SessionResumed(_log, session.User);
        return Describe(session);
    }

    private AcknowledgedResult Logout()
    {
        string? token;
        lock (_gate)
        {
            token = _token;
            _token = null;
        }

        // Revoked, not merely forgotten by this connection: pressing "sign out"
        // means the person, not the socket, and a session the console still
        // held would otherwise keep working from its next request.
        if (token is not null && _sessions.Revoke(token))
        {
            Log.SignedOut(_log);
        }

        return new AcknowledgedResult();
    }

    private async ValueTask<ServiceResult> ManageAsync(
        ServiceCommand command, Session? session, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case ListUsersCommand:
                return new UserListResult([.. _users.List().Select(Describe)]);

            case ChangePasswordCommand change:
            {
                if (session is null)
                {
                    return new ServiceError(
                        ServiceErrorReason.Refused, "No account is signed in to change the password of.");
                }

                var changed = await _users
                    .ChangePasswordAsync(
                        session.User, change.CurrentPassword ?? string.Empty,
                        change.NewPassword ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false);

                return changed.IsOk
                    ? new AcknowledgedResult()
                    : Refuse(changed.Outcome);
            }

            case CreateUserCommand create:
            {
                if (session is not null && !_users.MayManageAccounts(session.User))
                {
                    return NotTheOwner("create accounts");
                }

                var role = Enum.TryParse<UserRole>(create.Role, ignoreCase: true, out var asked)
                    ? asked
                    : UserRole.Operator;

                var created = _users.Create(create.Name ?? string.Empty, create.Password ?? string.Empty, role);
                if (!created.IsOk)
                {
                    return Refuse(created.Outcome);
                }

                var createdRole = created.User!.Role.ToString();
                Log.AccountCreated(_log, created.User.Name, createdRole);
                return new UserListResult([Describe(created.User)]);
            }

            case DeleteUserCommand delete:
            {
                if (session is null || !_users.MayManageAccounts(session.User))
                {
                    return NotTheOwner("remove accounts");
                }

                var removed = _users.Delete(delete.Name ?? string.Empty);
                if (!removed.IsOk)
                {
                    return Refuse(removed.Outcome);
                }

                // A deleted account's sessions end with it. Leaving them live
                // would mean removing somebody took effect only once they
                // happened to close a console.
                var ended = _sessions.RevokeAllFor(removed.User!.Name);
                Log.AccountDeleted(_log, removed.User.Name, ended);
                return new AcknowledgedResult();
            }

            default:
                return new ServiceError(ServiceErrorReason.InvalidArgument, "Not an account command.");
        }
    }

    private static ServiceError NotTheOwner(string what) => new(
        ServiceErrorReason.Refused,
        $"Only the owner may {what} (FR-USR-004). The owner is the installation's first account.");

    private static ServiceError Refuse(UserStoreOutcome outcome) => new(
        outcome switch
        {
            UserStoreOutcome.NoSuchUser => ServiceErrorReason.NotFound,
            UserStoreOutcome.NameRefused or UserStoreOutcome.PasswordRefused => ServiceErrorReason.InvalidArgument,
            _ => ServiceErrorReason.Refused,
        },
        outcome switch
        {
            UserStoreOutcome.NoSuchUser => "No account by that name.",
            UserStoreOutcome.NameTaken => "An account by that name already exists.",
            UserStoreOutcome.NameRefused =>
                $"An account name must be 1 to {UserStore.MaximumNameLength} characters with no spaces "
                + "and no control characters — one that could carry a line break could forge a log line.",
            UserStoreOutcome.PasswordRefused =>
                $"A password must be at least {UserStore.MinimumPasswordLength} characters, with an "
                + "uppercase letter, two digits and a special character.",
            UserStoreOutcome.WrongPassword or UserStoreOutcome.Throttled =>
                "That password was not accepted.",
            UserStoreOutcome.OwnerIsPermanent =>
                "The owner cannot be removed (FR-USR-004) — an installation with no permanent account "
                + "would have nobody who could not be locked out of it.",
            UserStoreOutcome.NotPermitted => "Only the owner may manage accounts (FR-USR-004).",
            _ => "The account operation was refused.",
        });

    private static Api.UserDescriptor Describe(Agent.UserDescriptor user) => new(
        user.Name, user.Role.ToString(), user.CreatedAtUnixMilliseconds, user.Role == UserRole.Owner);

    private static SessionResult Describe(Session session) => new(
        session.Token,
        session.User,
        session.Role.ToString(),
        session.IdleExpiresAtUnixMilliseconds,
        session.AbsoluteExpiresAtUnixMilliseconds);

    private ServiceResult Describe(ServiceResult described)
    {
        if (described is not ServiceDescriptionResult description)
        {
            return described;
        }

        var session = Current;

        return description with
        {
            SignedInUser = session?.User,
            SignedInRole = session?.Role.ToString(),

            // Only when the installation is otherwise finished: an installation
            // still owing a passphrase or a recovery kit has a more urgent
            // state to report, and stacking this on top would send the operator
            // to the wrong screen.
            SetupState = !_users.HasAccounts && description.SetupState is null or "ready"
                ? UsersRequiredState
                : description.SetupState,
        };
    }
}
