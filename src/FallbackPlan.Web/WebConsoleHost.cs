using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bodu;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Web.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Web;

/// <summary>
/// The local web console, as a callable unit (ADR-0036).
/// </summary>
/// <remarks>
/// The console is a client of the running service and nothing more: every data
/// request opens a client over the local binding, sends exactly the command the
/// browser asked for, and returns exactly the result the service answered — it
/// relays, it never derives (ADR-0028 §8). The page it serves is embedded in
/// this assembly, the listener binds loopback only, and every data request must
/// present the per-run token printed at start (ADR-0036 §§2–3).
/// </remarks>
public static class WebConsoleHost
{
    /// <summary>
    /// The header a browser presents its service session on (ADR-0045 §5).
    /// </summary>
    /// <remarks>
    /// Separate from the console's own bearer token, and deliberately so: the
    /// bearer token says this browser may talk to this console, and the session
    /// says which person is acting on the service behind it. Conflating them
    /// would mean one console had one identity.
    /// </remarks>
    public const string SessionHeader = "X-FallbackPlan-Session";

    /// <summary>
    /// The wire shape for the browser: camel-cased, enums as their names, and
    /// the contract's own polymorphism discriminators accepted anywhere in the
    /// object rather than first-property-only, because the page's JSON is
    /// hand-built rather than round-tripped.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>How long the start-up reachability probe waits before printing "not yet".</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Runs the console with the given command line.</summary>
    /// <param name="args">The command line, as the process received it.</param>
    /// <param name="output">Where the URL and run lines are written.</param>
    /// <param name="error">Where operator-facing failures are written.</param>
    /// <param name="cancellationToken">Stops the console; a clean shutdown, not a failure.</param>
    /// <returns>0 on a clean run, 1 for a usage or start-up failure.</returns>
    public static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(args);
        ThrowHelper.ThrowIfNull(output);
        ThrowHelper.ThrowIfNull(error);

        if (args.Length > 0 && args[0] is "-h" or "--help" or "help")
        {
            await output.WriteLineAsync("""
                FallbackPlan web console — a browser front end for a running service

                usage:
                  fallbackplan-web [--state <dir>] [--port <n>] [--log-level <level>]

                The console talks to the service holding the writer role for the
                state directory over its local binding, exactly as the CLI does.
                With no --state it uses the machine's default installation — the
                same directory a bare `fallbackplan-agent` serves (FR-SVC-016),
                overridable by FALLBACKPLAN_STATE — so a default install of the
                whole solution connects with no arguments at all. It binds
                http://127.0.0.1 only — remote access to a service is what device
                pairing is for — and prints a URL carrying a fresh access token on
                every start. It holds no repository, no keys and no writer role: if
                no service is listening it says so, keeps trying, and the page shows
                the service as unreachable until one answers (ADR-0036).

                --log-level takes trace, debug, information, warning, error, critical
                or none, and reads FALLBACKPLAN_LOG_LEVEL when absent. Logs go to
                standard error; the service keeps its own (ADR-0043 §6).
                """).ConfigureAwait(false);
            return 0;
        }

        if (!WebConsoleOptions.TryParse(args, out var options, out var failure))
        {
            await error.WriteLineAsync(failure).ConfigureAwait(false);
            return 1;
        }

        if (!ConsoleLogging.TryResolveLevel(LogLevelArgument(args), out var logLevel, out var levelRefusal))
        {
            await error.WriteLineAsync($"error: {levelRefusal}").ConfigureAwait(false);
            return 1;
        }

        var log = ConsoleLogging.For(error, logLevel, typeof(WebConsoleHost).FullName!);
        var auth = ConsoleAuth.CreateWithRandomToken();
        IServiceClientFactory clients = new LocalServiceClientFactory(options!.StateDirectory);

        await using var console = await StartAsync(options, clients, auth, log, cancellationToken)
            .ConfigureAwait(false);

        Log.ConsoleBound(log, options.Port, new LogPath(options.StateDirectory));
        await output.WriteLineAsync($"state    {options.StateDirectory}").ConfigureAwait(false);
        await output.WriteLineAsync($"console  {console.TokenisedUrl}").ConfigureAwait(false);
        await output.WriteLineAsync(await ProbeServiceAsync(clients, log, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        await console.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>What <c>--log-level</c> carried, if the command line named it.</summary>
    /// <param name="args">The command line, as the process received it.</param>
    private static string? LogLevelArgument(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--log-level")
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Starts the console listening on loopback. The test seam: everything
    /// <see cref="RunAsync"/> does beyond this is printing.
    /// </summary>
    /// <param name="options">What to serve.</param>
    /// <param name="clients">Where connected service clients come from.</param>
    /// <param name="auth">The run's authenticator.</param>
    /// <param name="logger">Where the host check's refusals are recorded.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running console; dispose to stop listening.</returns>
    public static async Task<RunningConsole> StartAsync(
        WebConsoleOptions options,
        IServiceClientFactory clients,
        ConsoleAuth auth,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(options);
        ThrowHelper.ThrowIfNull(clients);
        ThrowHelper.ThrowIfNull(auth);

        var log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            // The host check runs on every request, static page included: a
            // rebound hostname gets nothing at all (ADR-0036 §3).
            if (!ConsoleAuth.IsLoopbackHost(context.Request.Host))
            {
                Log.HostNotLoopback(log, context.Request.Host.Value ?? string.Empty);
                await RefuseAsync(context, StatusCodes.Status403Forbidden, "host_not_loopback",
                    Strings.WebConsoleHost_HostNotLoopback).ConfigureAwait(false);
                return;
            }

            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy =
                "default-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XFrameOptions = "DENY";

            await next(context).ConfigureAwait(false);
        });

        MapStaticAsset(app, "/", "wwwroot/index.html", "text/html; charset=utf-8", log);
        MapStaticAsset(app, "/app.css", "wwwroot/app.css", "text/css; charset=utf-8", log);
        MapStaticAsset(app, "/app.js", "wwwroot/app.js", "text/javascript; charset=utf-8", log);

        app.MapPost("/api/command", (HttpContext context) =>
            TimedAsync(context, log, "/api/command", () => ExchangeAsync(context, clients, auth, log)));
        app.MapGet("/api/events", (HttpContext context) => StreamEventsAsync(context, clients, auth, log));
        var gateThrottle = options.RestoreGateThrottle ?? new RestoreGateThrottle();
        app.MapPost("/api/restore-gate", (HttpContext context) =>
            TimedAsync(context, log, "/api/restore-gate", () => RestoreGateAsync(context, clients, auth, gateThrottle)));
        app.MapPost("/api/provision-write-only", (HttpContext context) =>
            TimedAsync(context, log, "/api/provision-write-only", () => ProvisionWriteOnlyAsync(context, clients, auth)));
        app.MapPost("/api/adopt-archive", (HttpContext context) =>
            TimedAsync(context, log, "/api/adopt-archive", () => AdoptArchiveAsync(context, clients, auth)));
        app.MapPost("/api/retention-apply", (HttpContext context) =>
            TimedAsync(context, log, "/api/retention-apply", () => RetentionApplyAsync(context, clients, auth)));
        app.MapPost("/api/destination-credentials", (HttpContext context) =>
            TimedAsync(context, log, "/api/destination-credentials", () => DestinationCredentialsAsync(context, clients, auth)));
        app.MapPost("/api/delete-snapshots", (HttpContext context) =>
            TimedAsync(context, log, "/api/delete-snapshots", () => DeleteSnapshotsAsync(context, clients, auth)));
        app.MapPost("/api/setup", (HttpContext context) =>
            TimedAsync(context, log, "/api/setup", () => SetupAsync(context, clients, auth, log)));
        app.MapPost("/api/passphrase-strength", (HttpContext context) =>
            TimedAsync(context, log, "/api/passphrase-strength", () => AssessPassphraseAsync(context, auth)));
        app.MapPost("/api/password-check", (HttpContext context) =>
            TimedAsync(context, log, "/api/password-check", () => CheckPasswordAsync(context, auth)));

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        return new RunningConsole(app, auth);
    }

    /// <summary>
    /// Runs one endpoint and leaves a trace line saying it happened. The
    /// events stream is deliberately not wrapped: a connection that lives
    /// for hours would report its lifetime, not a request.
    /// </summary>
    private static async Task TimedAsync(HttpContext context, ILogger log, string endpoint, Func<Task> handler)
    {
        var started = Stopwatch.GetTimestamp();
        await handler().ConfigureAwait(false);
        if (log.IsEnabled(LogLevel.Trace))
        {
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Log.RequestHandled(log, new LogLabel(endpoint), context.Response.StatusCode, elapsed);
        }
    }

    /// <summary>One command in, one result out — the whole data surface.</summary>
    private static async Task ExchangeAsync(
        HttpContext context, IServiceClientFactory clients, ConsoleAuth auth, ILogger log)
    {
        if (!auth.Authorizes(context.Request))
        {
            await RefuseAsync(context, StatusCodes.Status401Unauthorized, "token_missing_or_wrong",
                Strings.WebConsoleHost_TokenMissingOrWrong).ConfigureAwait(false);
            return;
        }

        ServiceCommand? command;
        try
        {
            command = await JsonSerializer.DeserializeAsync<ServiceCommand>(
                context.Request.Body, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand(exception.Message)).ConfigureAwait(false);
            return;
        }

        if (command is null)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand("null")).ConfigureAwait(false);
            return;
        }

        ServiceResult result;
        try
        {
            await using var client = await clients.ConnectAsync(context.RequestAborted).ConfigureAwait(false);

            // The browser holds the service session, not this process. Each
            // viewer therefore acts as themselves even though one console
            // relays for all of them — a console that cached one token would
            // make every action attributable to whoever signed in first, which
            // is the problem ADR-0045 exists to fix, moved one hop.
            //
            // A refused resume ends the exchange: the refusal names the fix
            // ("log in again") where the command's own refusal, sent blind
            // afterwards, would not — and a browser that slept through its
            // session's idle timeout retries every few seconds, so the doomed
            // command would double the traffic of an already-failing loop.
            // Refused specifically: a service that predates contract 1.16
            // answers resume_session itself with InvalidArgument, and that
            // must stay the shrug it always was, not become a blockade.
            var presented = context.Request.Headers[SessionHeader].ToString() is { Length: > 0 } session
                ? await client.ExecuteAsync(new ResumeSessionCommand(session), context.RequestAborted)
                    .ConfigureAwait(false)
                : null;
            var refused = presented is ServiceError { Reason: ServiceErrorReason.Refused } dead ? dead : null;

            if (refused is not null)
            {
                Log.RelayedSessionRefused(log, new LogLabel(command.GetType().Name));
                result = refused;
            }
            else
            {
                var relayed = Stopwatch.GetTimestamp();
                result = await client.ExecuteAsync(command, context.RequestAborted).ConfigureAwait(false);
                if (log.IsEnabled(LogLevel.Trace))
                {
                    var elapsed = (long)Stopwatch.GetElapsedTime(relayed).TotalMilliseconds;
                    Log.CommandRelayed(log, new LogLabel(command.GetType().Name), new LogLabel(result.GetType().Name), elapsed);
                }
            }
        }
        catch (ServiceConnectionException exception)
        {
            // Unreachable is a transport fact, not a command outcome, so it is
            // an HTTP status rather than a ServiceResult — the page turns it
            // into staleness with the age of last contact (NFR-OPS-006).
            await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable, "service_unreachable",
                exception.Message).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync<ServiceResult>(
            context.Response.Body, result, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>What the restore-gate endpoint reads from the page.</summary>
    /// <param name="Passphrase">The typed passphrase; derived from here, sent nowhere (ADR-0042 §5).</param>
    private sealed record GateRequest(string? Passphrase);

    /// <summary>The gate's answer to the page.</summary>
    /// <param name="Outcome"><c>verified</c>, <c>wrong</c>, or <c>unavailable</c>.</param>
    /// <param name="Detail">Why not, when it was not verified.</param>
    /// <param name="Grants">
    /// One sealed restore grant per set the passphrase opens, hex, keyed by
    /// set id (ADR-0042 §5) — opaque to the page, opened only by the service;
    /// the page passes the set's grant into <c>open_restore_source</c>.
    /// </param>
    private sealed record GateResponse(
        string Outcome, string? Detail, IReadOnlyDictionary<string, string>? Grants = null);

    /// <summary>
    /// The passphrase gate (FR-WOR-007, ADR-0089): the one endpoint every
    /// look at a backup's files passes through first. The passphrase goes no
    /// further than this process: it is derived here under the facts the
    /// service publishes for each set, proved against each set's sealing key,
    /// and what the page gets back is a grant per set it opens, sealed to the
    /// service's recipient key. Nothing local is read, so a console on any
    /// machine checks alike, and a passphrase that cannot be checked opens
    /// nothing. A run of wrong ones is slowed per account, never locked.
    /// </summary>
    private static async Task RestoreGateAsync(
        HttpContext context, IServiceClientFactory clients, ConsoleAuth auth, RestoreGateThrottle throttle)
    {
        if (!auth.Authorizes(context.Request))
        {
            await RefuseAsync(context, StatusCodes.Status401Unauthorized, "token_missing_or_wrong",
                Strings.WebConsoleHost_TokenMissingOrWrong).ConfigureAwait(false);
            return;
        }

        GateRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<GateRequest>(
                context.Request.Body, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand(exception.Message)).ConfigureAwait(false);
            return;
        }

        if (request?.Passphrase is not { Length: > 0 } passphrase)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand("a passphrase is required")).ConfigureAwait(false);
            return;
        }

        ServiceDescriptionResult? description = null;
        BackupSetsResult? sets = null;
        try
        {
            await using var client = await clients.ConnectAsync(context.RequestAborted).ConfigureAwait(false);
            var session = context.Request.Headers[SessionHeader].ToString();
            if (session.Length > 0)
            {
                await client.ExecuteAsync(new ResumeSessionCommand(session), context.RequestAborted).ConfigureAwait(false);
            }

            description = await client.ExecuteAsync(new DescribeServiceCommand(), context.RequestAborted)
                .ConfigureAwait(false) as ServiceDescriptionResult;
            sets = await client.ExecuteAsync(new ListBackupSetsCommand(), context.RequestAborted)
                .ConfigureAwait(false) as BackupSetsResult;
        }
        catch (ServiceConnectionException)
        {
            // No service answering means nothing to derive under; the
            // unavailable answer below says so in one place.
        }

        ConsoleRestoreGate.GrantsAnswer answer;
        if (description is null || sets is null)
        {
            answer = new ConsoleRestoreGate.GrantsAnswer(
                ConsoleRestoreGate.GateOutcome.Unavailable,
                "The service did not say what to check the passphrase against.");
        }
        else
        {
            // Counted per account, as the service names who is signed in, so
            // signing in again does not start the count over and one person's
            // slips never slow another's; an installation with no accounts yet
            // has one count. The account's tries take turns.
            var account = description.SignedInUser ?? string.Empty;
            using var turn = await throttle.TakeTurnAsync(account, context.RequestAborted).ConfigureAwait(false);
            answer = ConsoleRestoreGate.BuildRestoreGrants(description, sets.Sets, passphrase);
            switch (answer.Outcome)
            {
                case ConsoleRestoreGate.GateOutcome.Wrong:
                    throttle.Wrong(account);
                    break;
                case ConsoleRestoreGate.GateOutcome.Verified:
                    throttle.Opened(account);
                    break;
            }
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new GateResponse(answer.Outcome.ToString().ToLowerInvariant(), answer.Detail, answer.Grants),
            SerializerOptions,
            context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>What the provisioning endpoint reads from the page.</summary>
    /// <param name="SetName">The backup set to provision.</param>
    /// <param name="Passphrase">The typed passphrase; derived from here, sent nowhere (ADR-0042 §4).</param>
    /// <param name="Acknowledged">
    /// The typed loss acknowledgement: the operator accepts that the
    /// passphrase can never change and that losing it loses the backup.
    /// </param>
    private sealed record ProvisionRequest(string? SetName, string? Passphrase, bool Acknowledged);

    /// <summary>The provisioning endpoint's answer to the page.</summary>
    /// <param name="Outcome"><c>provisioned</c>, <c>wrong</c>, <c>refused</c>, or <c>unavailable</c>.</param>
    /// <param name="Detail">Why, when not provisioned.</param>
    /// <param name="Lines">The service's ceremony statements, when provisioned.</param>
    private sealed record ProvisionResponse(string Outcome, string? Detail = null, IReadOnlyList<string>? Lines = null);

    /// <summary>The page's adoption request (ADR-0061 §5, FR-DR-009).</summary>
    /// <param name="DestinationName">The declared destination the archive was discovered at.</param>
    /// <param name="RepositoryId">The discovered archive's repository id.</param>
    /// <param name="Passphrase">The typed passphrase; it stops here.</param>
    /// <param name="Acknowledged">The loss acknowledgement, collected before anything derives.</param>
    /// <param name="SetName">An optional name to adopt the set under; blank takes the archive's recorded one.</param>
    /// <param name="Confirmation">
    /// The preview's confirmation. Absent, the endpoint previews; present, it
    /// adopts what that preview showed.
    /// </param>
    /// <param name="Roots">The roots as the person confirmed them, recorded labels kept; absent takes the recorded ones.</param>
    private sealed record AdoptRequest(
        string? DestinationName,
        string? RepositoryId,
        string? Passphrase,
        bool Acknowledged,
        string? SetName = null,
        string? Confirmation = null,
        IReadOnlyList<BackupRootDescriptor>? Roots = null);

    /// <summary>The adoption endpoint's answer to the page.</summary>
    /// <param name="Outcome"><c>preview</c>, <c>adopted</c>, <c>wrong</c>, <c>refused</c>, or <c>unavailable</c>.</param>
    /// <param name="Detail">Why, when neither previewed nor adopted.</param>
    /// <param name="Lines">The service's statements, when adopted.</param>
    /// <param name="Set">The set as adopted, when adopted.</param>
    /// <param name="Preview">What the archive recorded, when previewed.</param>
    private sealed record AdoptResponse(
        string Outcome,
        string? Detail = null,
        IReadOnlyList<string>? Lines = null,
        ArchiveAdoptedResult? Set = null,
        AdoptionPreviewResult? Preview = null);

    /// <summary>
    /// The adoption ceremony (ADR-0061 §5): the third endpoint permitted a
    /// secret, holding the same line as the other two — Argon2id runs in
    /// this process, against the <em>discovered</em> archive's salt, and
    /// what goes to the service is the write bundle sealed to its published
    /// recipient key. The derivation is proved against the discovered
    /// sealing key before anything is sent, so a wrong passphrase is caught
    /// where it was typed.
    /// </summary>
    /// <remarks>
    /// Two phases on the one endpoint (FR-DR-009), so no second endpoint is
    /// permitted a secret: without a confirmation the service is asked for
    /// the preview, which the page shows; with one, it is asked to adopt what
    /// that preview showed, with any root the person re-pointed. The page
    /// sends the passphrase each time and the derivation runs each time; the
    /// console holds nothing between the two.
    /// </remarks>
    private static async Task AdoptArchiveAsync(HttpContext context, IServiceClientFactory clients, ConsoleAuth auth)
    {
        if (!auth.Authorizes(context.Request))
        {
            await RefuseAsync(context, StatusCodes.Status401Unauthorized, "token_missing_or_wrong",
                Strings.WebConsoleHost_TokenMissingOrWrong).ConfigureAwait(false);
            return;
        }

        AdoptRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<AdoptRequest>(
                context.Request.Body, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand(exception.Message)).ConfigureAwait(false);
            return;
        }

        if (request is not { DestinationName.Length: > 0, RepositoryId.Length: > 0, Passphrase.Length: > 0 })
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand("a destination name, a repository id and a passphrase are required"))
                .ConfigureAwait(false);
            return;
        }

        async Task AnswerAsync(AdoptResponse response)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await JsonSerializer.SerializeAsync(
                context.Response.Body, response, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }

        if (!request.Acknowledged)
        {
            await AnswerAsync(new AdoptResponse(
                "refused",
                "Adoption needs the loss acknowledgement: the passphrase can never change, and if it is "
                + "lost the backup is unrecoverable.")).ConfigureAwait(false);
            return;
        }

        try
        {
            await using var client = await clients.ConnectAsync(context.RequestAborted).ConfigureAwait(false);

            if (await client.ExecuteAsync(new DescribeServiceCommand(), context.RequestAborted).ConfigureAwait(false)
                is not ServiceDescriptionResult { RestoreGrantRecipient.Length: > 0 } description)
            {
                await AnswerAsync(new AdoptResponse(
                    "unavailable", "The service does not publish a grant-recipient key.")).ConfigureAwait(false);
                return;
            }

            // The facts to derive against come from the service's own
            // discovery, never from the page: the page names an id, the
            // service says what that id's descriptor holds.
            var discovered = await client.ExecuteAsync(
                new DiscoverArchivesCommand(request.DestinationName), context.RequestAborted).ConfigureAwait(false);
            if (discovered is ServiceError discoveryRefusal)
            {
                await AnswerAsync(new AdoptResponse("refused", discoveryRefusal.Message)).ConfigureAwait(false);
                return;
            }

            if (discovered is not ArchivesDiscoveredResult listing
                || listing.Archives.FirstOrDefault(archive =>
                    string.Equals(archive.RepositoryId, request.RepositoryId, StringComparison.OrdinalIgnoreCase)) is not { } target)
            {
                await AnswerAsync(new AdoptResponse(
                    "unavailable",
                    $"Destination '{request.DestinationName}' does not list an archive '{request.RepositoryId}' — "
                    + "discover again and pick one it shows.")).ConfigureAwait(false);
                return;
            }

            var minted = ConsoleRestoreGate.BuildAdoptEnvelope(target, request.Passphrase, description.RestoreGrantRecipient);
            if (minted.Outcome != ConsoleRestoreGate.GateOutcome.Verified)
            {
                await AnswerAsync(new AdoptResponse(
                    minted.Outcome == ConsoleRestoreGate.GateOutcome.Wrong ? "wrong" : "unavailable",
                    minted.Detail)).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(request.Confirmation))
            {
                var previewed = await client.ExecuteAsync(
                    new PreviewAdoptionCommand(request.DestinationName, target.RepositoryId, minted.Envelope!),
                    context.RequestAborted).ConfigureAwait(false);
                await AnswerAsync(previewed switch
                {
                    AdoptionPreviewResult preview => new AdoptResponse("preview", Preview: preview),
                    ServiceError refusal => new AdoptResponse("refused", refusal.Message),
                    _ => new AdoptResponse("refused", $"Unexpected result '{previewed.GetType().Name}'."),
                }).ConfigureAwait(false);
                return;
            }

            var result = await client.ExecuteAsync(
                new AdoptArchiveCommand(
                    request.DestinationName, target.RepositoryId, minted.Envelope!,
                    SetName: string.IsNullOrWhiteSpace(request.SetName) ? null : request.SetName.Trim(),
                    Roots: request.Roots is { Count: > 0 } roots ? roots : null,
                    Confirmation: request.Confirmation),
                context.RequestAborted).ConfigureAwait(false);
            await AnswerAsync(result switch
            {
                ArchiveAdoptedResult adopted => new AdoptResponse("adopted", Lines: adopted.Lines, Set: adopted),
                ServiceError refusal => new AdoptResponse("refused", refusal.Message),
                _ => new AdoptResponse("refused", $"Unexpected result '{result.GetType().Name}'."),
            }).ConfigureAwait(false);
        }
        catch (ServiceConnectionException exception)
        {
            await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable, "service_unreachable",
                exception.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The write-only setup ceremony (ADR-0042 §4, §10): the second endpoint
    /// permitted a secret, and it holds the same line as the restore gate —
    /// Argon2id runs in this process, and what goes to the service is the
    /// write bundle sealed to its published recipient key. Serves creation
    /// and adoption alike; the loss acknowledgement is collected here, before
    /// anything derives.
    /// </summary>
    private static async Task ProvisionWriteOnlyAsync(HttpContext context, IServiceClientFactory clients, ConsoleAuth auth)
    {
        if (!auth.Authorizes(context.Request))
        {
            await RefuseAsync(context, StatusCodes.Status401Unauthorized, "token_missing_or_wrong",
                Strings.WebConsoleHost_TokenMissingOrWrong).ConfigureAwait(false);
            return;
        }

        ProvisionRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ProvisionRequest>(
                context.Request.Body, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand(exception.Message)).ConfigureAwait(false);
            return;
        }

        if (request is not { SetName.Length: > 0, Passphrase.Length: > 0 })
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand("a set name and a passphrase are required"))
                .ConfigureAwait(false);
            return;
        }

        async Task AnswerAsync(ProvisionResponse response)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await JsonSerializer.SerializeAsync(
                context.Response.Body, response, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }

        if (!request.Acknowledged)
        {
            // The acknowledgement is the ceremony (ADR-0042 §11): there is no
            // recovery path to offer later, so consent comes before the
            // derivation, enforced where the derivation runs.
            await AnswerAsync(new ProvisionResponse(
                "refused",
                "Provisioning needs the loss acknowledgement: the passphrase can never change, and if it is "
                + "lost the backup is unrecoverable.")).ConfigureAwait(false);
            return;
        }

        try
        {
            await using var client = await clients.ConnectAsync(context.RequestAborted).ConfigureAwait(false);

            if (await client.ExecuteAsync(new DescribeServiceCommand(), context.RequestAborted).ConfigureAwait(false)
                is not ServiceDescriptionResult { RestoreGrantRecipient.Length: > 0 } description)
            {
                await AnswerAsync(new ProvisionResponse(
                    "unavailable", "The service does not publish a grant-recipient key.")).ConfigureAwait(false);
                return;
            }

            if (await client.ExecuteAsync(new ListBackupSetsCommand(), context.RequestAborted).ConfigureAwait(false)
                    is not BackupSetsResult sets
                || sets.Sets.FirstOrDefault(set =>
                    string.Equals(set.Name, request.SetName, StringComparison.Ordinal)) is not { } target)
            {
                await AnswerAsync(new ProvisionResponse(
                    "unavailable", $"No backup set named '{request.SetName}' is configured.")).ConfigureAwait(false);
                return;
            }

            var minted = await ConsoleRestoreGate.BuildProvisionEnvelopeAsync(
                description.ArchivesRoot, description.StateDirectory, target.Id, request.Passphrase,
                description.RestoreGrantRecipient, context.RequestAborted).ConfigureAwait(false);
            if (minted.Outcome != ConsoleRestoreGate.GateOutcome.Verified)
            {
                await AnswerAsync(new ProvisionResponse(
                    minted.Outcome == ConsoleRestoreGate.GateOutcome.Wrong ? "wrong" : "unavailable",
                    minted.Detail)).ConfigureAwait(false);
                return;
            }

            var result = await client.ExecuteAsync(
                new ProvisionWriteOnlySetCommand(request.SetName, minted.Envelope!), context.RequestAborted)
                .ConfigureAwait(false);
            await AnswerAsync(result switch
            {
                ConfigurationChangeResult change => new ProvisionResponse("provisioned", Lines: change.Lines),
                ServiceError refusal => new ProvisionResponse("refused", refusal.Message),
                _ => new ProvisionResponse("refused", $"Unexpected result '{result.GetType().Name}'."),
            }).ConfigureAwait(false);
        }
        catch (ServiceConnectionException exception)
        {
            await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable, "service_unreachable",
                exception.Message).ConfigureAwait(false);
        }
    }

    /// <summary>What the retention-apply endpoint reads from the page.</summary>
    /// <param name="Passphrase">The typed passphrase; derived from here, sent nowhere (ADR-0055 §6).</param>
    private sealed record RetentionApplyRequest(string? Passphrase);

    /// <summary>The retention-apply endpoint's answer to the page.</summary>
    /// <param name="Outcome"><c>applied</c>, <c>wrong</c>, <c>refused</c>, or <c>unavailable</c>.</param>
    /// <param name="Detail">Why, when not applied.</param>
    /// <param name="Lines">The pass's report, when applied.</param>
    private sealed record RetentionApplyResponse(
        string Outcome, string? Detail = null, IReadOnlyList<string>? Lines = null);

    /// <summary>
    /// Applies retention on a set-up installation
    /// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §6, Amendment 3):
    /// the service holds the key that publishes and not the key that
    /// authorises a deletion, so the grant is derived here, where the person
    /// typed, and only sealed envelopes reach the service. A passphrase that
    /// opens no set is answered here and the service is sent nothing.
    /// </summary>
    /// <remarks>
    /// The browser's session is resumed first, as the command relay resumes
    /// it, so the deletion is attributed to the person who confirmed it and
    /// not to whoever the console last relayed for (ADR-0045).
    /// </remarks>
    private static async Task RetentionApplyAsync(HttpContext context, IServiceClientFactory clients, ConsoleAuth auth)
    {
        if (!auth.Authorizes(context.Request))
        {
            await RefuseAsync(context, StatusCodes.Status401Unauthorized, "token_missing_or_wrong",
                Strings.WebConsoleHost_TokenMissingOrWrong).ConfigureAwait(false);
            return;
        }

        RetentionApplyRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<RetentionApplyRequest>(
                context.Request.Body, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand(exception.Message)).ConfigureAwait(false);
            return;
        }

        if (request is not { Passphrase.Length: > 0 })
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand("a passphrase is required"))
                .ConfigureAwait(false);
            return;
        }

        async Task AnswerAsync(RetentionApplyResponse response)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await JsonSerializer.SerializeAsync(
                context.Response.Body, response, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }

        try
        {
            await using var client = await clients.ConnectAsync(context.RequestAborted).ConfigureAwait(false);

            if (context.Request.Headers[SessionHeader].ToString() is { Length: > 0 } session
                && await client.ExecuteAsync(new ResumeSessionCommand(session), context.RequestAborted)
                    .ConfigureAwait(false) is ServiceError { Reason: ServiceErrorReason.Refused } dead)
            {
                await AnswerAsync(new RetentionApplyResponse("refused", dead.Message)).ConfigureAwait(false);
                return;
            }

            if (await client.ExecuteAsync(new DescribeServiceCommand(), context.RequestAborted).ConfigureAwait(false)
                is not ServiceDescriptionResult description)
            {
                await AnswerAsync(new RetentionApplyResponse(
                    "unavailable", "The service did not describe itself.")).ConfigureAwait(false);
                return;
            }

            if (await client.ExecuteAsync(new ListBackupSetsCommand(), context.RequestAborted).ConfigureAwait(false)
                is not BackupSetsResult sets)
            {
                await AnswerAsync(new RetentionApplyResponse(
                    "unavailable", "The service did not list its backup sets.")).ConfigureAwait(false);
                return;
            }

            var minted = ConsoleRestoreGate.BuildReclaimGrants(description, sets.Sets, request.Passphrase);
            if (minted.Outcome != ConsoleRestoreGate.GateOutcome.Verified)
            {
                await AnswerAsync(new RetentionApplyResponse(
                    minted.Outcome == ConsoleRestoreGate.GateOutcome.Wrong ? "wrong" : "unavailable",
                    minted.Detail)).ConfigureAwait(false);
                return;
            }

            var result = await client.ExecuteAsync(
                new RetentionCommand(Apply: true, ReclaimGrants: minted.Grants), context.RequestAborted)
                .ConfigureAwait(false);
            await AnswerAsync(result switch
            {
                RetentionResult report => new RetentionApplyResponse("applied", Lines: report.Lines),
                ServiceError refusal => new RetentionApplyResponse("refused", refusal.Message),
                _ => new RetentionApplyResponse("refused", $"Unexpected result '{result.GetType().Name}'."),
            }).ConfigureAwait(false);
        }
        catch (ServiceConnectionException exception)
        {
            await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable, "service_unreachable",
                exception.Message).ConfigureAwait(false);
        }
    }

    /// <summary>What the destination-credentials endpoint reads from the page.</summary>
    /// <param name="DestinationName">The s3 destination the key is for.</param>
    /// <param name="AccessKeyId">The access key id.</param>
    /// <param name="SecretAccessKey">The typed secret; sealed here, sent nowhere (ADR-0091).</param>
    private sealed record DestinationCredentialsRequest(
        string? DestinationName, string? AccessKeyId, string? SecretAccessKey);

    /// <summary>The destination-credentials endpoint's answer to the page.</summary>
    /// <param name="Outcome"><c>stored</c>, <c>refused</c>, or <c>unavailable</c>.</param>
    /// <param name="Detail">Why, when not stored.</param>
    /// <param name="Lines">The service's report, when stored.</param>
    private sealed record DestinationCredentialsResponse(
        string Outcome, string? Detail = null, IReadOnlyList<string>? Lines = null);

    /// <summary>
    /// Stores an S3-compatible destination's access key
    /// ([ADR-0091](../../docs/adr/0091-an-s3-compatible-destination.md)): the
    /// third endpoint permitted a secret, and it holds the restore gate's
    /// line. The secret is sealed here, in the console's process, to the
    /// service's published recipient key for the one destination and key id
    /// it was typed for, and only the envelope reaches the service
    /// (NFR-SEC-009).
    /// </summary>
    /// <remarks>
    /// The browser's session is resumed first, so the change is the person's
    /// who made it and not whoever the console last relayed for (ADR-0045).
    /// </remarks>
    private static async Task DestinationCredentialsAsync(
        HttpContext context, IServiceClientFactory clients, ConsoleAuth auth)
    {
        if (!auth.Authorizes(context.Request))
        {
            await RefuseAsync(context, StatusCodes.Status401Unauthorized, "token_missing_or_wrong",
                Strings.WebConsoleHost_TokenMissingOrWrong).ConfigureAwait(false);
            return;
        }

        DestinationCredentialsRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<DestinationCredentialsRequest>(
                context.Request.Body, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand(exception.Message)).ConfigureAwait(false);
            return;
        }

        if (request is not { DestinationName.Length: > 0, AccessKeyId.Length: > 0, SecretAccessKey.Length: > 0 })
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand(
                    "a destination, an access key id and a secret access key are required"))
                .ConfigureAwait(false);
            return;
        }

        async Task AnswerAsync(DestinationCredentialsResponse response)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await JsonSerializer.SerializeAsync(
                context.Response.Body, response, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }

        try
        {
            await using var client = await clients.ConnectAsync(context.RequestAborted).ConfigureAwait(false);

            if (context.Request.Headers[SessionHeader].ToString() is { Length: > 0 } session
                && await client.ExecuteAsync(new ResumeSessionCommand(session), context.RequestAborted)
                    .ConfigureAwait(false) is ServiceError { Reason: ServiceErrorReason.Refused } dead)
            {
                await AnswerAsync(new DestinationCredentialsResponse("refused", dead.Message)).ConfigureAwait(false);
                return;
            }

            if (await client.ExecuteAsync(new DescribeServiceCommand(), context.RequestAborted).ConfigureAwait(false)
                is not ServiceDescriptionResult { RestoreGrantRecipient.Length: > 0 } description)
            {
                await AnswerAsync(new DestinationCredentialsResponse(
                    "unavailable", "The service does not publish a recipient key to seal to.")).ConfigureAwait(false);
                return;
            }

            var sealedKey = ConsoleRestoreGate.SealAccessKey(
                request.DestinationName, request.AccessKeyId, request.SecretAccessKey,
                description.RestoreGrantRecipient);
            if (sealedKey.Outcome != ConsoleRestoreGate.GateOutcome.Verified)
            {
                await AnswerAsync(new DestinationCredentialsResponse(
                    sealedKey.Outcome == ConsoleRestoreGate.GateOutcome.Wrong ? "refused" : "unavailable",
                    sealedKey.Detail)).ConfigureAwait(false);
                return;
            }

            var result = await client.ExecuteAsync(
                new SetDestinationCredentialsCommand(request.DestinationName, request.AccessKeyId, sealedKey.Envelope!),
                context.RequestAborted).ConfigureAwait(false);
            await AnswerAsync(result switch
            {
                ConfigurationChangeResult change => new DestinationCredentialsResponse("stored", Lines: change.Lines),
                ServiceError refusal => new DestinationCredentialsResponse("refused", refusal.Message),
                _ => new DestinationCredentialsResponse("refused", $"Unexpected result '{result.GetType().Name}'."),
            }).ConfigureAwait(false);
        }
        catch (ServiceConnectionException exception)
        {
            await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable, "service_unreachable",
                exception.Message).ConfigureAwait(false);
        }
    }

    /// <summary>What the snapshot-deletion endpoint reads from the page.</summary>
    /// <param name="SetId">The set the snapshots belong to.</param>
    /// <param name="SnapshotIds">The snapshots to delete, as the listing names them.</param>
    /// <param name="Passphrase">The typed passphrase; derived from here, sent nowhere (ADR-0055 §6).</param>
    private sealed record DeleteSnapshotsRequest(string? SetId, IReadOnlyList<string>? SnapshotIds, string? Passphrase);

    /// <summary>The snapshot-deletion endpoint's answer to the page.</summary>
    /// <param name="Outcome"><c>applied</c>, <c>wrong</c>, <c>refused</c>, or <c>unavailable</c>.</param>
    /// <param name="Detail">Why, when not applied.</param>
    /// <param name="Snapshots">Where each snapshot's deletion stands, when applied.</param>
    /// <param name="Lines">The service's report, when applied.</param>
    private sealed record DeleteSnapshotsResponse(
        string Outcome,
        string? Detail = null,
        IReadOnlyList<SnapshotDeletionOutcome>? Snapshots = null,
        IReadOnlyList<string>? Lines = null);

    /// <summary>
    /// Deletes snapshots a person confirmed
    /// ([ADR-0080](../../docs/adr/0080-a-person-deletes-a-snapshot.md)):
    /// as with an applied retention pass, the grant is derived here, where the
    /// passphrase was typed, and only the sealed envelope for the one set
    /// reaches the service. A passphrase that does not open that set is
    /// answered here and the service is sent nothing.
    /// </summary>
    /// <remarks>
    /// The browser's session is resumed first, so the journal records the
    /// deletion as the person's who confirmed it (ADR-0045). The dry run the
    /// dialog shows first needs no passphrase and goes through the command
    /// relay, so this endpoint only ever applies.
    /// </remarks>
    private static async Task DeleteSnapshotsAsync(HttpContext context, IServiceClientFactory clients, ConsoleAuth auth)
    {
        if (!auth.Authorizes(context.Request))
        {
            await RefuseAsync(context, StatusCodes.Status401Unauthorized, "token_missing_or_wrong",
                Strings.WebConsoleHost_TokenMissingOrWrong).ConfigureAwait(false);
            return;
        }

        DeleteSnapshotsRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<DeleteSnapshotsRequest>(
                context.Request.Body, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand(exception.Message)).ConfigureAwait(false);
            return;
        }

        if (request is not { SetId.Length: > 0, SnapshotIds.Count: > 0, Passphrase.Length: > 0 })
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand("a set, at least one snapshot and the passphrase are required"))
                .ConfigureAwait(false);
            return;
        }

        async Task AnswerAsync(DeleteSnapshotsResponse response)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await JsonSerializer.SerializeAsync(
                context.Response.Body, response, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }

        try
        {
            await using var client = await clients.ConnectAsync(context.RequestAborted).ConfigureAwait(false);

            if (context.Request.Headers[SessionHeader].ToString() is { Length: > 0 } session
                && await client.ExecuteAsync(new ResumeSessionCommand(session), context.RequestAborted)
                    .ConfigureAwait(false) is ServiceError { Reason: ServiceErrorReason.Refused } dead)
            {
                await AnswerAsync(new DeleteSnapshotsResponse("refused", dead.Message)).ConfigureAwait(false);
                return;
            }

            if (await client.ExecuteAsync(new DescribeServiceCommand(), context.RequestAborted).ConfigureAwait(false)
                is not ServiceDescriptionResult description)
            {
                await AnswerAsync(new DeleteSnapshotsResponse(
                    "unavailable", "The service did not describe itself.")).ConfigureAwait(false);
                return;
            }

            if (await client.ExecuteAsync(new ListBackupSetsCommand(), context.RequestAborted).ConfigureAwait(false)
                is not BackupSetsResult sets)
            {
                await AnswerAsync(new DeleteSnapshotsResponse(
                    "unavailable", "The service did not list its backup sets.")).ConfigureAwait(false);
                return;
            }

            if (sets.Sets.FirstOrDefault(set =>
                    string.Equals(set.Id, request.SetId, StringComparison.OrdinalIgnoreCase)) is not { } target)
            {
                await AnswerAsync(new DeleteSnapshotsResponse(
                    "unavailable", $"No backup set '{request.SetId}' is configured.")).ConfigureAwait(false);
                return;
            }

            // Only the one set's derivation runs: a deletion needs that set's
            // grant and no other, and each further salt is another Argon2id run.
            var minted = ConsoleRestoreGate.BuildReclaimGrants(description, [target], request.Passphrase);
            if (minted.Outcome != ConsoleRestoreGate.GateOutcome.Verified
                || minted.Grants?.GetValueOrDefault(target.Id) is not { } grant)
            {
                await AnswerAsync(new DeleteSnapshotsResponse(
                    minted.Outcome == ConsoleRestoreGate.GateOutcome.Unavailable ? "unavailable" : "wrong",
                    minted.Detail)).ConfigureAwait(false);
                return;
            }

            var result = await client.ExecuteAsync(
                new DeleteSnapshotsCommand(target.Id, request.SnapshotIds, Apply: true, ReclaimGrant: grant),
                context.RequestAborted).ConfigureAwait(false);
            await AnswerAsync(result switch
            {
                DeleteSnapshotsResult report => new DeleteSnapshotsResponse(
                    "applied", Snapshots: report.Snapshots, Lines: report.Lines),
                ServiceError refusal => new DeleteSnapshotsResponse("refused", refusal.Message),
                _ => new DeleteSnapshotsResponse("refused", $"Unexpected result '{result.GetType().Name}'."),
            }).ConfigureAwait(false);
        }
        catch (ServiceConnectionException exception)
        {
            await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable, "service_unreachable",
                exception.Message).ConfigureAwait(false);
        }
    }

    /// <summary>What the passphrase-strength endpoint reads from the page.</summary>
    /// <param name="Candidate">The passphrase as typed so far.</param>
    private sealed record StrengthRequest(string? Candidate);

    /// <summary>The strength endpoint's answer, for the live meter.</summary>
    /// <param name="Band"><c>too_short</c>, <c>weak</c>, <c>fair</c> or <c>strong</c>.</param>
    /// <param name="Score">0–100, for the meter's width.</param>
    /// <param name="Acceptable">Whether setup would accept this candidate.</param>
    /// <param name="Findings">Plain sentences to show beneath the field.</param>
    private sealed record StrengthResponse(
        string Band, int Score, bool Acceptable, IReadOnlyList<string> Findings);

    /// <summary>
    /// Scores a half-typed passphrase for the setup meter (ADR-0044 §6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a round trip rather than a script in the page.</b> A
    /// meter computed in JavaScript would be a second copy of the policy, in
    /// a second language, free to disagree with the one that actually
    /// decides — and the way that disagreement shows up is the page saying
    /// "strong" and the submit being refused, at the one moment a person is
    /// least able to work out why. One implementation, one verdict.
    /// </para>
    /// <para>
    /// The exposure it costs is small and worth naming: the candidate reaches
    /// the same local process that is about to derive from the finished
    /// passphrase seconds later, over loopback, behind the same token. It is
    /// never sent to the service and never leaves this machine.
    /// </para>
    /// </remarks>
    private static async Task AssessPassphraseAsync(HttpContext context, ConsoleAuth auth)
    {
        if (!auth.Authorizes(context.Request))
        {
            await RefuseAsync(context, StatusCodes.Status401Unauthorized, "token_missing_or_wrong",
                Strings.WebConsoleHost_TokenMissingOrWrong).ConfigureAwait(false);
            return;
        }

        StrengthRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<StrengthRequest>(
                context.Request.Body, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand(exception.Message)).ConfigureAwait(false);
            return;
        }

        var assessment = PassphraseStrength.Assess(request?.Candidate ?? string.Empty);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new StrengthResponse(
                BandName(assessment.Band), assessment.Score, assessment.IsAcceptable,
                [.. assessment.Findings.Select(Describe)]),
            SerializerOptions,
            context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>The password-check endpoint's answer, for the account form's live checklist.</summary>
    /// <param name="Acceptable">Whether the account policy would accept this candidate.</param>
    /// <param name="Findings">Plain sentences naming each unmet rule.</param>
    private sealed record PasswordCheckResponse(bool Acceptable, IReadOnlyList<string> Findings);

    /// <summary>
    /// Checks a half-typed account password against the policy the service
    /// will enforce (FR-USR-001 as amended) — same posture as the passphrase
    /// meter above: one implementation, one verdict, computed in this local
    /// process and never sent to the service.
    /// </summary>
    private static async Task CheckPasswordAsync(HttpContext context, ConsoleAuth auth)
    {
        if (!auth.Authorizes(context.Request))
        {
            await RefuseAsync(context, StatusCodes.Status401Unauthorized, "token_missing_or_wrong",
                Strings.WebConsoleHost_TokenMissingOrWrong).ConfigureAwait(false);
            return;
        }

        StrengthRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<StrengthRequest>(
                context.Request.Body, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand(exception.Message)).ConfigureAwait(false);
            return;
        }

        var assessment = PasswordPolicy.Assess(request?.Candidate ?? string.Empty);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new PasswordCheckResponse(
                assessment.IsAcceptable, [.. assessment.Findings.Select(Describe)]),
            SerializerOptions,
            context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>The account-policy sentences the checklist renders.</summary>
    private static string Describe(PasswordFinding finding) => finding switch
    {
        PasswordFinding.TooShort =>
            $"At least {PasswordPolicy.MinimumLength} characters.",
        PasswordFinding.NoUppercase => "At least one uppercase letter.",
        PasswordFinding.FewerThanTwoDigits => "At least two digits.",
        PasswordFinding.NoSpecialCharacter =>
            "At least one special character — anything that is not a letter or digit.",
        _ => finding.ToString(),
    };

    /// <summary>What the setup endpoint reads from the page.</summary>
    /// <param name="Passphrase">The typed passphrase; derived from here, sent nowhere (ADR-0044 §4).</param>
    /// <param name="Confirmation">The second entry, which must match.</param>
    /// <param name="Acknowledged">
    /// The typed loss acknowledgement: the operator accepts that this is the
    /// master key, that it can never change, and that losing it makes every
    /// backup unrecoverable.
    /// </param>
    private sealed record SetupRequest(string? Passphrase, string? Confirmation, bool Acknowledged);

    /// <summary>The setup endpoint's answer.</summary>
    /// <param name="Outcome"><c>provisioned</c>, <c>refused</c>, <c>weak</c>, or <c>unavailable</c>.</param>
    /// <param name="Detail">Why, when not provisioned.</param>
    /// <param name="Lines">The service's ceremony statements, when provisioned.</param>
    /// <param name="Findings">What was wrong with the passphrase, on a weak outcome.</param>
    private sealed record SetupResponse(
        string Outcome, string? Detail = null,
        IReadOnlyList<string>? Lines = null, IReadOnlyList<string>? Findings = null);

    /// <summary>
    /// First-run setup (ADR-0044): the third endpoint permitted a secret, and
    /// it holds the same line as the restore gate and the write-only
    /// ceremony — Argon2id runs in this process, and what reaches the service
    /// is the write bundle sealed to its published recipient key.
    /// </summary>
    /// <remarks>
    /// The order below is fixed and tested: acknowledgement, then the two
    /// entries matching, then strength, and only then a derivation. Consent
    /// comes before the work for the same reason it does in the write-only
    /// ceremony — there is no recovery path to offer afterwards.
    /// </remarks>
    private static async Task SetupAsync(
        HttpContext context, IServiceClientFactory clients, ConsoleAuth auth, ILogger log)
    {
        if (!auth.Authorizes(context.Request))
        {
            await RefuseAsync(context, StatusCodes.Status401Unauthorized, "token_missing_or_wrong",
                Strings.WebConsoleHost_TokenMissingOrWrong).ConfigureAwait(false);
            return;
        }

        SetupRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<SetupRequest>(
                context.Request.Body, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand(exception.Message)).ConfigureAwait(false);
            return;
        }

        if (request is not { Passphrase.Length: > 0 })
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "malformed_command",
                Strings.FormatWebConsoleHost_MalformedCommand("a passphrase is required")).ConfigureAwait(false);
            return;
        }

        async Task AnswerAsync(SetupResponse response)
        {
            // The outcome only, never the body. This is the server half of the
            // pair the page's own trace line forms: the two disagreeing is
            // what localises a stale page.
            Log.SetupOutcome(log, new LogLabel(response.Outcome));
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await JsonSerializer.SerializeAsync(
                context.Response.Body, response, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
        }

        if (!request.Acknowledged)
        {
            await AnswerAsync(new SetupResponse(
                "refused",
                "Setup needs the acknowledgement: this passphrase is the master key for the installation, it "
                + "can never be changed, and if it is lost every backup is permanently unrecoverable."))
                .ConfigureAwait(false);
            return;
        }

        if (!string.Equals(request.Passphrase, request.Confirmation, StringComparison.Ordinal))
        {
            // Compared ordinally and before normalisation, because the two
            // entries exist to catch a typing mistake, and two strings that
            // differ only after NFC folding were still typed differently.
            await AnswerAsync(new SetupResponse(
                "refused", "The two passphrases do not match.")).ConfigureAwait(false);
            return;
        }

        var assessment = PassphraseStrength.Assess(request.Passphrase);
        if (!assessment.IsAcceptable)
        {
            await AnswerAsync(new SetupResponse(
                "weak",
                $"This passphrase is too weak to be an installation's master key (it needs at least "
                + $"{PassphraseStrength.MinimumLength} characters including an uppercase letter, two "
                + "digits and a special character, and more than one repeated unit).",
                Findings: [.. assessment.Findings.Select(Describe)])).ConfigureAwait(false);
            return;
        }

        try
        {
            await using var client = await clients.ConnectAsync(context.RequestAborted).ConfigureAwait(false);

            if (await client.ExecuteAsync(new DescribeServiceCommand(), context.RequestAborted).ConfigureAwait(false)
                is not ServiceDescriptionResult { RestoreGrantRecipient.Length: > 0 } description)
            {
                await AnswerAsync(new SetupResponse(
                    "unavailable", "The service does not publish a grant-recipient key.")).ConfigureAwait(false);
                return;
            }

            var minted = ConsoleRestoreGate.BuildInstallationSetup(
                request.Passphrase, description.RestoreGrantRecipient);
            if (minted.Outcome != ConsoleRestoreGate.GateOutcome.Verified)
            {
                await AnswerAsync(new SetupResponse("unavailable", minted.Detail)).ConfigureAwait(false);
                return;
            }

            var result = await client.ExecuteAsync(
                new ProvisionInstallationCommand(minted.Envelope!), context.RequestAborted).ConfigureAwait(false);

            await AnswerAsync(result switch
            {
                ConfigurationChangeResult change => new SetupResponse("provisioned", Lines: change.Lines),
                ServiceError refusal => new SetupResponse("refused", refusal.Message),
                _ => new SetupResponse("refused", $"Unexpected result '{result.GetType().Name}'."),
            }).ConfigureAwait(false);
        }
        catch (ServiceConnectionException exception)
        {
            await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable, "service_unreachable",
                exception.Message).ConfigureAwait(false);
        }
    }

    /// <summary>The wire name for a strength band.</summary>
    private static string BandName(PassphraseStrengthBand band) => band switch
    {
        PassphraseStrengthBand.TooShort => "too_short",
        PassphraseStrengthBand.Weak => "weak",
        PassphraseStrengthBand.Fair => "fair",
        _ => "strong",
    };

    /// <summary>
    /// A finding as a sentence. The assessment returns values rather than
    /// prose so the policy stays testable; the wording lives here, where it
    /// is shown.
    /// </summary>
    private static string Describe(PassphraseFinding finding) => finding switch
    {
        PassphraseFinding.TooShort =>
            $"Too short — use at least {PassphraseStrength.MinimumLength} characters.",
        PassphraseFinding.SingleRepeatedCharacter =>
            "This is one character repeated, which is one character's worth of secret however long it is.",
        PassphraseFinding.ShortRepeatedCycle =>
            "This repeats a short run over and over, so its length is not doing the work it looks like it is.",
        PassphraseFinding.OneCharacterClassOnly =>
            "Only one kind of character. Either make it longer, or mix in capitals, digits or punctuation.",
        PassphraseFinding.FewDistinctCharacters =>
            "Long, but built from very few different characters.",
        PassphraseFinding.LengthCarriesIt =>
            "Good length — ordinary words carry it, once the required characters are in.",
        PassphraseFinding.NoUppercase => "Add at least one uppercase letter.",
        PassphraseFinding.FewerThanTwoDigits => "Add at least two digits.",
        PassphraseFinding.NoSpecialCharacter =>
            "Add at least one special character — anything that is not a letter or digit.",
        _ => "Several kinds of character, which is what you want.",
    };

    /// <summary>
    /// Bridges the contract's progress stream onto server-sent events. One
    /// service watch per subscribed page; the browser's <c>EventSource</c>
    /// reconnects on its own when either end goes away.
    /// </summary>
    /// <remarks>
    /// The session rides the query the way the console's own token does,
    /// because <c>EventSource</c> cannot set a header (ADR-0036 §4) — and it
    /// must ride somewhere: once an installation has accounts the gate
    /// answers an anonymous watch with an empty stream, which ends at once,
    /// which the browser answers by redialling on the streaming retry hint.
    /// That loop ran for sixteen minutes at a watch every two seconds in the
    /// 2026-08-25 service log without a single progress event arriving. A
    /// refused session therefore ends the stream honestly: the page is told
    /// on a named event so it can show sign-in, and the retry hint is raised
    /// to thirty seconds so even a page that ignores it polls politely.
    /// </remarks>
    private static async Task StreamEventsAsync(
        HttpContext context, IServiceClientFactory clients, ConsoleAuth auth, ILogger log)
    {
        if (!auth.Authorizes(context.Request))
        {
            await RefuseAsync(context, StatusCodes.Status401Unauthorized, "token_missing_or_wrong",
                Strings.WebConsoleHost_TokenMissingOrWrong).ConfigureAwait(false);
            return;
        }

        IFallbackPlanClient client;
        try
        {
            client = await clients.ConnectAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (ServiceConnectionException exception)
        {
            await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable, "service_unreachable",
                exception.Message).ConfigureAwait(false);
            return;
        }

        await using (client.ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";

            string? session = context.Request.Query["session"];
            var sessionPresented = !string.IsNullOrEmpty(session);
            Log.EventStreamOpened(log, sessionPresented);

            var events = 0L;
            try
            {
                if (!string.IsNullOrEmpty(session)
                    && await client.ExecuteAsync(new ResumeSessionCommand(session), context.RequestAborted)
                        .ConfigureAwait(false) is ServiceError { Reason: ServiceErrorReason.Refused } refused)
                {
                    var refusal = JsonSerializer.Serialize<ServiceResult>(refused, SerializerOptions);
                    await context.Response.WriteAsync(
                        $"retry: 30000\nevent: session\ndata: {refusal}\n\n", context.RequestAborted)
                        .ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                    return;
                }

                // Ask EventSource to wait a beat before redialling, so a
                // stopped service is polite retries rather than a busy loop.
                await context.Response.WriteAsync("retry: 2000\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

                await foreach (var progress in client.WatchAsync(context.RequestAborted).ConfigureAwait(false))
                {
                    events++;
                    var json = JsonSerializer.Serialize(progress, SerializerOptions);
                    await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The browser went away; nothing to tell anyone.
            }
            finally
            {
                Log.EventStreamEnded(log, events);
            }
        }
    }

    private static void MapStaticAsset(
        WebApplication app, string path, string resource, string contentType, ILogger log)
    {
        var bytes = LoadEmbedded(resource);
        app.MapGet(path, async (HttpContext context) =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = contentType;
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
            // The assets are embedded at build time, so the byte count names
            // the build: a page tracing one asset version while this line
            // reports another settles a staleness question from both sides.
            Log.StaticAssetServed(log, new LogLabel(path), bytes.Length);
        });
    }

    private static byte[] LoadEmbedded(string resource)
    {
        using var stream = typeof(WebConsoleHost).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(Strings.FormatWebConsoleHost_EmbeddedAssetMissing(resource));
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>A refusal the page can branch on without parsing prose.</summary>
    /// <param name="Error">The closed-set code.</param>
    /// <param name="Message">What to tell the operator.</param>
    private sealed record TransportRefusal(string Error, string Message);

    private static async Task RefuseAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, new TransportRefusal(code, message), SerializerOptions, context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>The start-up reachability line: an answer either way, never a hang.</summary>
    private static async Task<string> ProbeServiceAsync(
        IServiceClientFactory clients, ILogger log, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(ProbeTimeout);

        try
        {
            await using var client = await clients.ConnectAsync(bounded.Token).ConfigureAwait(false);
            return await client.ExecuteAsync(new DescribeServiceCommand(), bounded.Token).ConfigureAwait(false)
                is ServiceDescriptionResult description
                ? Strings.FormatWebConsoleHost_ServiceReachable(description.MachineName, description.ContractVersion)
                : Strings.WebConsoleHost_ServiceAnsweredUnexpectedly;
        }
        catch (Exception exception) when (exception is ServiceConnectionException or OperationCanceledException)
        {
            Log.ServiceUnreachable(log);
            return Strings.FormatWebConsoleHost_NoServiceListening(clients.Address);
        }
    }
}

/// <summary>A started console, for whoever needs its address and its end.</summary>
public sealed class RunningConsole : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConsoleAuth _auth;

    internal RunningConsole(WebApplication app, ConsoleAuth auth)
    {
        _app = app;
        _auth = auth;
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var address = addresses?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException(Strings.RunningConsole_NoBoundAddress);
        BaseAddress = new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal), UriKind.Absolute);
    }

    /// <summary>Where the console is listening.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The URL to hand the operator — address and token in one line.</summary>
    public string TokenisedUrl => $"{BaseAddress}?token={_auth.Token}";

    /// <summary>Runs until the host is asked to stop.</summary>
    /// <param name="cancellationToken">Stops the console cleanly.</param>
    /// <returns>Completion of the shutdown.</returns>
    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) =>
        _app.WaitForShutdownAsync(cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await _app.DisposeAsync().ConfigureAwait(false);
}
