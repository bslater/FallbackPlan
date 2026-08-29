using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.TestSupport;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The per-connection gate (FR-USR-001, FR-USR-003, FR-USR-004, FR-USR-006;
/// ADR-0045 §5): who may speak which verb, and what a session is.
/// </summary>
/// <remarks>
/// Driven against the decorator over a stub rather than through a live socket.
/// What is under test is the gate's decisions — which verbs pass unauthenticated,
/// what a session outlives, what a refusal says — and a real transport would add
/// minutes without changing any of them. The transport's own half is covered by
/// the listener suites.
/// </remarks>
[TestClass]
public sealed class AuthenticationGateTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-auth-gate-tests", Guid.NewGuid().ToString("n"));

    private static readonly Domain.Configuration.Argon2Parameters Fast = new()
    {
        MemoryKiB = 64,
        Iterations = 1,
        Parallelism = 1,
    };

    private sealed class StubService : IFallbackPlanService
    {
        public int Executed { get; private set; }

        /// <summary>What the inner handler reports, so the decorator's override can be tested against it.</summary>
        public string? SetupState { get; set; }

        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
        {
            Executed++;
            return ValueTask.FromResult<ServiceResult>(command is DescribeServiceCommand
                ? new ServiceDescriptionResult("1.16", "test", "machine", "/state", false, 0, SetupState: SetupState)
                : new AcknowledgedResult());
        }

        public async IAsyncEnumerable<JobProgressEvent> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Executed++;
            await ValueTask.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    private StubService _inner = null!;
    private UserStore _users = null!;
    private SessionRegistry _sessions = null!;
    private RecordingLogger _log = null!;

    [TestInitialize]
    public void SetUp()
    {
        Directory.CreateDirectory(_root);
        _inner = new StubService();
        _users = UserStore.Open(_root, throttle: new UserStore.ThrottlePolicy(
            TimeSpan.FromMilliseconds(0.25), TimeSpan.FromMilliseconds(4)));
        _sessions = new SessionRegistry();
        _log = new RecordingLogger();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    /// <summary>A fresh connection, as a listener builds one per accepted socket.</summary>
    private AuthenticatingService Connect() => new(_inner, _users, _sessions, _log);

    private void GiveTheInstallationAnOwner() =>
        _users.Create("ben", "A-good-passw0rd9", parameters: Fast);

    [TestMethod]
    public async Task AnInstallationWithNoAccounts_IsNotLockedOut()
    {
        // The migration decision, asserted rather than assumed: an installation
        // that predates this feature keeps working. Refusing everything would
        // brick it on upgrade and leave no door for the first account.
        var connection = Connect();

        var answered = await connection.ExecuteAsync(new GetStatusCommand(), CancellationToken.None);

        Assert.IsInstanceOfType<AcknowledgedResult>(answered);
    }

    [TestMethod]
    public async Task AnInstallationWithNoAccounts_SaysSoOnDescribeService()
    {
        // The other half of that decision: it is surfaced, not silent, so a
        // console prompts rather than leaving the installation open forever.
        var connection = Connect();

        var described = (ServiceDescriptionResult)await connection.ExecuteAsync(
            new DescribeServiceCommand(), CancellationToken.None);

        Assert.AreEqual(AuthenticatingService.UsersRequiredState, described.SetupState);
        Assert.IsNull(described.SignedInUser);
    }

    [TestMethod]
    public async Task AnUnfinishedSetup_KeepsItsOwnStateRatherThanBeingOverwritten()
    {
        // An installation still owing a passphrase has a more urgent state to
        // report; stacking users_required on top would send the operator to
        // the wrong screen. The inner handler owns the setup state and the
        // decorator only fills in where there is nothing more pressing.
        _inner.SetupState = "setup_required";
        var connection = Connect();

        var described = (ServiceDescriptionResult)await connection.ExecuteAsync(
            new DescribeServiceCommand(), CancellationToken.None);

        Assert.AreEqual("setup_required", described.SetupState);
        Assert.IsFalse(_users.HasAccounts, "and it has no accounts either, so the override had its chance");
    }

    [TestMethod]
    public async Task AKitStillOwed_AlsoKeepsItsOwnState()
    {
        _inner.SetupState = "kit_required";
        var connection = Connect();

        var described = (ServiceDescriptionResult)await connection.ExecuteAsync(
            new DescribeServiceCommand(), CancellationToken.None);

        Assert.AreEqual("kit_required", described.SetupState);
    }

    [TestMethod]
    public async Task AReadyInstallationWithNoAccounts_IsMovedToUsersRequired()
    {
        // The one case the override exists for: everything else is finished,
        // so the missing account is the next thing to do.
        _inner.SetupState = "ready";
        var connection = Connect();

        var described = (ServiceDescriptionResult)await connection.ExecuteAsync(
            new DescribeServiceCommand(), CancellationToken.None);

        Assert.AreEqual(AuthenticatingService.UsersRequiredState, described.SetupState);
    }

    [TestMethod]
    public async Task AReadyInstallationWithAccounts_StaysReady()
    {
        _inner.SetupState = "ready";
        GiveTheInstallationAnOwner();
        var connection = Connect();

        var described = (ServiceDescriptionResult)await connection.ExecuteAsync(
            new DescribeServiceCommand(), CancellationToken.None);

        Assert.AreEqual("ready", described.SetupState);
    }

    [TestMethod]
    public async Task OnceAccountsExist_AnUnauthenticatedConnection_IsRefusedByName()
    {
        GiveTheInstallationAnOwner();
        var connection = Connect();

        var refused = (ServiceError)await connection.ExecuteAsync(
            new GetStatusCommand(), CancellationToken.None);

        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("log in", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task OnceAccountsExist_DescribeServiceAndLogin_AreStillReachable()
    {
        // A login screen has to render, and to do that it has to know whether
        // the installation is even set up.
        GiveTheInstallationAnOwner();
        var connection = Connect();

        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await connection.ExecuteAsync(new DescribeServiceCommand(), CancellationToken.None));

        Assert.IsInstanceOfType<SessionResult>(
            await connection.ExecuteAsync(new LoginCommand("ben", "A-good-passw0rd9"), CancellationToken.None));
    }

    [TestMethod]
    public async Task Login_ThenACommand_Passes()
    {
        GiveTheInstallationAnOwner();
        var connection = Connect();

        await connection.ExecuteAsync(new LoginCommand("ben", "A-good-passw0rd9"), CancellationToken.None);
        var answered = await connection.ExecuteAsync(new GetStatusCommand(), CancellationToken.None);

        Assert.IsInstanceOfType<AcknowledgedResult>(answered);
    }

    [TestMethod]
    public async Task Login_WithAWrongPassword_RefusesWithoutSayingWhichHalfWasWrong()
    {
        // A refusal that distinguishes "no such account" from "wrong password"
        // is a name oracle. Both answers are the same sentence.
        GiveTheInstallationAnOwner();
        var connection = Connect();

        var wrongPassword = (ServiceError)await connection.ExecuteAsync(
            new LoginCommand("ben", "not-the-password"), CancellationToken.None);
        var wrongName = (ServiceError)await connection.ExecuteAsync(
            new LoginCommand("nobody", "A-good-passw0rd9"), CancellationToken.None);

        Assert.AreEqual(wrongPassword.Message, wrongName.Message);
        Assert.DoesNotContain("exist", wrongName.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task ASession_IsPresentableOnASecondConnection()
    {
        // The reason a session is not the connection. The web console opens a
        // fresh connection for every request it relays, so a session welded to
        // one would sign the operator out between clicking two buttons.
        GiveTheInstallationAnOwner();
        var first = Connect();
        var minted = (SessionResult)await first.ExecuteAsync(
            new LoginCommand("ben", "A-good-passw0rd9"), CancellationToken.None);

        var second = Connect();
        Assert.IsInstanceOfType<ServiceError>(
            await second.ExecuteAsync(new GetStatusCommand(), CancellationToken.None));

        var resumed = await second.ExecuteAsync(new ResumeSessionCommand(minted.Token), CancellationToken.None);

        Assert.IsInstanceOfType<SessionResult>(resumed);
        Assert.IsInstanceOfType<AcknowledgedResult>(
            await second.ExecuteAsync(new GetStatusCommand(), CancellationToken.None));
    }

    [TestMethod]
    public async Task AnInventedToken_IsRefused()
    {
        GiveTheInstallationAnOwner();
        var connection = Connect();

        var refused = (ServiceError)await connection.ExecuteAsync(
            new ResumeSessionCommand(new string('a', 64)), CancellationToken.None);

        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
    }

    [TestMethod]
    public async Task Logout_EndsTheSessionEverywhere_NotJustOnThisConnection()
    {
        // Pressing "sign out" means the person, not the socket. A session the
        // console still held would otherwise keep working from its next request.
        GiveTheInstallationAnOwner();
        var first = Connect();
        var minted = (SessionResult)await first.ExecuteAsync(
            new LoginCommand("ben", "A-good-passw0rd9"), CancellationToken.None);

        var second = Connect();
        await second.ExecuteAsync(new ResumeSessionCommand(minted.Token), CancellationToken.None);

        await first.ExecuteAsync(new LogoutCommand(), CancellationToken.None);

        Assert.IsInstanceOfType<ServiceError>(
            await second.ExecuteAsync(new GetStatusCommand(), CancellationToken.None));
        Assert.IsEmpty(_sessions.Live());
    }

    [TestMethod]
    public async Task ARestart_SignsEveryoneOut()
    {
        // The blunt revocation story ADR-0045 §5 accepts on purpose. A new
        // registry is what a restarted process has.
        GiveTheInstallationAnOwner();
        var before = Connect();
        var minted = (SessionResult)await before.ExecuteAsync(
            new LoginCommand("ben", "A-good-passw0rd9"), CancellationToken.None);

        _sessions = new SessionRegistry();
        var after = Connect();

        Assert.IsInstanceOfType<ServiceError>(
            await after.ExecuteAsync(new ResumeSessionCommand(minted.Token), CancellationToken.None));
    }

    [TestMethod]
    public async Task ALapsedSession_StopsWorking()
    {
        GiveTheInstallationAnOwner();
        _sessions = new SessionRegistry(idleTimeout: TimeSpan.FromMilliseconds(1));
        var connection = Connect();
        var minted = (SessionResult)await connection.ExecuteAsync(
            new LoginCommand("ben", "A-good-passw0rd9"), CancellationToken.None);

        await Task.Delay(20, CancellationToken.None);

        Assert.IsInstanceOfType<ServiceError>(
            await Connect().ExecuteAsync(new ResumeSessionCommand(minted.Token), CancellationToken.None));
    }

    [TestMethod]
    public async Task DescribeService_OnceSignedIn_NamesWhoIsActing()
    {
        GiveTheInstallationAnOwner();
        var connection = Connect();
        await connection.ExecuteAsync(new LoginCommand("ben", "A-good-passw0rd9"), CancellationToken.None);

        var described = (ServiceDescriptionResult)await connection.ExecuteAsync(
            new DescribeServiceCommand(), CancellationToken.None);

        Assert.AreEqual("ben", described.SignedInUser);
        Assert.AreEqual(nameof(UserRole.Owner), described.SignedInRole);
    }

    [TestMethod]
    public async Task OnlyTheOwner_MayCreateAndRemoveAccounts()
    {
        GiveTheInstallationAnOwner();
        var owner = Connect();
        await owner.ExecuteAsync(new LoginCommand("ben", "A-good-passw0rd9"), CancellationToken.None);
        await owner.ExecuteAsync(new CreateUserCommand("sam", "Another-passw0rd9"), CancellationToken.None);

        var operatorConnection = Connect();
        await operatorConnection.ExecuteAsync(
            new LoginCommand("sam", "Another-passw0rd9"), CancellationToken.None);

        var refusedCreate = (ServiceError)await operatorConnection.ExecuteAsync(
            new CreateUserCommand("mallory", "third-password"), CancellationToken.None);
        var refusedDelete = (ServiceError)await operatorConnection.ExecuteAsync(
            new DeleteUserCommand("ben"), CancellationToken.None);

        Assert.AreEqual(ServiceErrorReason.Refused, refusedCreate.Reason);
        Assert.Contains("owner", refusedCreate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(ServiceErrorReason.Refused, refusedDelete.Reason);
    }

    [TestMethod]
    public async Task RestartService_IsTheOwnersAlone()
    {
        // The second Owner-only privilege after account management
        // (ADR-0049): a restart interrupts everyone's runs and signs
        // everybody out, so an operator may not command it.
        GiveTheInstallationAnOwner();
        var owner = Connect();
        await owner.ExecuteAsync(new LoginCommand("ben", "A-good-passw0rd9"), CancellationToken.None);
        await owner.ExecuteAsync(new CreateUserCommand("sam", "Another-passw0rd9"), CancellationToken.None);

        var operatorConnection = Connect();
        await operatorConnection.ExecuteAsync(
            new LoginCommand("sam", "Another-passw0rd9"), CancellationToken.None);

        var refused = (ServiceError)await operatorConnection.ExecuteAsync(
            new RestartServiceCommand(), CancellationToken.None);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("owner", refused.Message, StringComparison.OrdinalIgnoreCase);

        // The owner's restart passes the gate to the inner service.
        var before = _inner.Executed;
        Assert.IsInstanceOfType<AcknowledgedResult>(
            await owner.ExecuteAsync(new RestartServiceCommand(), CancellationToken.None));
        Assert.AreEqual(before + 1, _inner.Executed, "the owner's restart must reach the inner handler");
    }

    [TestMethod]
    public async Task RestartService_BeforeAnyAccountExists_IsRefusedNotBootstrapped()
    {
        // The bootstrap window admits exactly the verbs that create the
        // first account — an unset-up installation is not restartable by
        // whoever can reach the socket.
        var refused = (ServiceError)await Connect().ExecuteAsync(
            new RestartServiceCommand(), CancellationToken.None);

        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.AreEqual(0, _inner.Executed, "the inner service was never reached");
    }

    [TestMethod]
    public async Task DeletingTheOwner_IsRefusedThroughTheContractToo()
    {
        GiveTheInstallationAnOwner();
        var owner = Connect();
        await owner.ExecuteAsync(new LoginCommand("ben", "A-good-passw0rd9"), CancellationToken.None);

        var refused = (ServiceError)await owner.ExecuteAsync(
            new DeleteUserCommand("ben"), CancellationToken.None);

        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("owner", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task DeletingAnAccount_EndsItsLiveSessions()
    {
        // Otherwise removing somebody takes effect only once they happen to
        // close a console.
        GiveTheInstallationAnOwner();
        var owner = Connect();
        await owner.ExecuteAsync(new LoginCommand("ben", "A-good-passw0rd9"), CancellationToken.None);
        await owner.ExecuteAsync(new CreateUserCommand("sam", "Another-passw0rd9"), CancellationToken.None);

        var theirs = Connect();
        await theirs.ExecuteAsync(new LoginCommand("sam", "Another-passw0rd9"), CancellationToken.None);
        Assert.IsInstanceOfType<AcknowledgedResult>(
            await theirs.ExecuteAsync(new GetStatusCommand(), CancellationToken.None));

        await owner.ExecuteAsync(new DeleteUserCommand("sam"), CancellationToken.None);

        Assert.IsInstanceOfType<ServiceError>(
            await theirs.ExecuteAsync(new GetStatusCommand(), CancellationToken.None));
    }

    [TestMethod]
    public async Task ChangePassword_IsAlwaysTheCallersOwnAccount()
    {
        // An owner who could set somebody else's password could become them,
        // which would make every attributable action attributable to the wrong
        // person. The verb names no account for exactly that reason.
        GiveTheInstallationAnOwner();
        var owner = Connect();
        await owner.ExecuteAsync(new LoginCommand("ben", "A-good-passw0rd9"), CancellationToken.None);

        var changed = await owner.ExecuteAsync(
            new ChangePasswordCommand("A-good-passw0rd9", "A-newer-passw0rd9"), CancellationToken.None);

        Assert.IsInstanceOfType<AcknowledgedResult>(changed);
        Assert.IsInstanceOfType<SessionResult>(
            await Connect().ExecuteAsync(new LoginCommand("ben", "A-newer-passw0rd9"), CancellationToken.None));
    }

    [TestMethod]
    public async Task ListUsers_CarriesNoCredential()
    {
        GiveTheInstallationAnOwner();
        var owner = Connect();
        await owner.ExecuteAsync(new LoginCommand("ben", "A-good-passw0rd9"), CancellationToken.None);

        var listed = (UserListResult)await owner.ExecuteAsync(new ListUsersCommand(), CancellationToken.None);

        var only = listed.Users.Single();
        Assert.AreEqual("ben", only.Name);
        Assert.IsTrue(only.IsOwner);
        Assert.DoesNotContain(
            "fbp-argon2id", System.Text.Json.JsonSerializer.Serialize(listed), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AnUnauthenticatedWatch_EndsRatherThanStreaming()
    {
        // A progress stream carries set names and job outcomes — the same class
        // of thing every gated verb answers with — so it is gated the same way.
        GiveTheInstallationAnOwner();
        var connection = Connect();

        var received = 0;
        await foreach (var _ in connection.WatchAsync(CancellationToken.None))
        {
            received++;
        }

        Assert.AreEqual(0, received);
        Assert.AreEqual(0, _inner.Executed, "the inner service was never reached");
    }

    [TestMethod]
    public async Task NoPasswordAndNoToken_ReachesTheLog()
    {
        // FR-USR-002 over the channel this arc adds. Asserted against every
        // record the whole flow produced, rendered, rather than against the
        // call sites one at a time.
        const string Password = "Sup3r-Secr3tPhrase";
        _users.Create("ben", Password, parameters: Fast);

        var connection = Connect();
        await connection.ExecuteAsync(new LoginCommand("ben", "wrong-one-first"), CancellationToken.None);
        var minted = (SessionResult)await connection.ExecuteAsync(
            new LoginCommand("ben", Password), CancellationToken.None);
        await connection.ExecuteAsync(new CreateUserCommand("sam", "Another-passw0rd9"), CancellationToken.None);
        await connection.ExecuteAsync(new LogoutCommand(), CancellationToken.None);

        Assert.IsNotEmpty(_log.Records, "the flow logged nothing, so this would prove nothing");

        foreach (var record in _log.Records)
        {
            var rendered = string.Join(
                " ", record.Values.Select(value => $"{value.Key}={value.Value}"));

            Assert.DoesNotContain(Password, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("Another-passw0rd9", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(minted.Token, rendered, StringComparison.Ordinal);
        }

        // The anti-vacuity half: the account names ARE logged, which is what
        // makes an action attributable and is the whole point of the arc.
        Assert.IsNotEmpty(
            _log.Records.Where(record => record.Values.Any(value =>
                value.Value is string text && text.Contains("ben", StringComparison.Ordinal))),
            "no record named the account, so the assertions above prove only that nothing was logged");
    }

    [TestMethod]
    public async Task ARefusedResume_LeavesARecordButNeverTheToken()
    {
        // Twenty-one hours of trace-level service log showed a client failing
        // every command for sixteen minutes without one line saying why: the
        // gate refused resumes silently, and the listener's generic "answered
        // ServiceError" was all there was. A refusal that common must name
        // itself — and still must not name the token, which is a credential
        // whether or not it has lapsed (NFR-SEC-006).
        GiveTheInstallationAnOwner();
        var connection = Connect();

        var stale = new string('b', 64);
        var answered = await connection.ExecuteAsync(
            new ResumeSessionCommand(stale), CancellationToken.None);

        Assert.IsInstanceOfType<ServiceError>(answered);
        var record = Assert.ContainsSingle(_log.Records.Where(record => record.EventId == 3755));
        Assert.AreEqual(LogLevel.Debug, record.Level);
        foreach (var logged in _log.Records)
        {
            var rendered = string.Join(" ", logged.Values.Select(value => $"{value.Key}={value.Value}"));
            Assert.DoesNotContain(stale, rendered, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task ASuccessfulResume_LeavesATraceNamingTheUser()
    {
        GiveTheInstallationAnOwner();
        var first = Connect();
        var minted = (SessionResult)await first.ExecuteAsync(
            new LoginCommand("ben", "A-good-passw0rd9"), CancellationToken.None);

        var second = Connect();
        await second.ExecuteAsync(new ResumeSessionCommand(minted.Token), CancellationToken.None);

        var record = Assert.ContainsSingle(_log.Records.Where(record => record.EventId == 3756));
        Assert.AreEqual(LogLevel.Trace, record.Level);
        Assert.AreEqual("ben", record.Value("User"));
    }

    [TestMethod]
    public async Task AGatedRefusal_LeavesATraceNamingTheCommand()
    {
        // The other half of the silent sixteen minutes: each relayed command
        // was refused for want of a session, and the log never said which
        // verb or why. At trace, the wedge describes itself.
        GiveTheInstallationAnOwner();
        var connection = Connect();

        var answered = await connection.ExecuteAsync(new GetStatusCommand(), CancellationToken.None);

        Assert.IsInstanceOfType<ServiceError>(answered);
        var record = Assert.ContainsSingle(_log.Records.Where(record => record.EventId == 3757));
        Assert.AreEqual(LogLevel.Trace, record.Level);
        Assert.AreEqual(nameof(GetStatusCommand), record.Value("Command"));
    }
}
