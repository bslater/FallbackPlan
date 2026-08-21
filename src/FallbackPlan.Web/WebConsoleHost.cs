using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bodu;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Domain.Configuration;
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

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            await output.WriteLineAsync("""
                FallbackPlan web console — a browser front end for a running service

                usage:
                  fallbackplan-web --state <dir> [--port <n>] [--log-level <level>]

                The console talks to the service holding the writer role for <dir>
                over its local binding, exactly as the CLI does. It binds
                http://127.0.0.1 only — remote access to a service is what device
                pairing is for — and prints a URL carrying a fresh access token on
                every start. It holds no repository, no keys and no writer role: if
                no service is listening it says so, keeps trying, and the page shows
                the service as unreachable until one answers (ADR-0036).

                --log-level takes trace, debug, information, warning, error, critical
                or none, and reads FALLBACKPLAN_LOG_LEVEL when absent. Logs go to
                standard error; the service keeps its own (ADR-0043 §6).
                """).ConfigureAwait(false);
            return args.Length == 0 ? 1 : 0;
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

        Log.ConsoleBound(log, options.Port, options.StateDirectory);
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

        MapStaticAsset(app, "/", "wwwroot/index.html", "text/html; charset=utf-8");
        MapStaticAsset(app, "/app.css", "wwwroot/app.css", "text/css; charset=utf-8");
        MapStaticAsset(app, "/app.js", "wwwroot/app.js", "text/javascript; charset=utf-8");

        app.MapPost("/api/command", (HttpContext context) => ExchangeAsync(context, clients, auth));
        app.MapGet("/api/events", (HttpContext context) => StreamEventsAsync(context, clients, auth));
        app.MapPost("/api/restore-gate", (HttpContext context) => RestoreGateAsync(context, clients, auth));
        app.MapPost("/api/provision-write-only", (HttpContext context) => ProvisionWriteOnlyAsync(context, clients, auth));
        app.MapPost("/api/setup", (HttpContext context) => SetupAsync(context, clients, auth));
        app.MapPost("/api/recovery-kit", (HttpContext context) => RecoveryKitAsync(context, clients, auth));
        app.MapPost("/api/passphrase-strength", (HttpContext context) => AssessPassphraseAsync(context, auth));

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        return new RunningConsole(app, auth);
    }

    /// <summary>One command in, one result out — the whole data surface.</summary>
    private static async Task ExchangeAsync(HttpContext context, IServiceClientFactory clients, ConsoleAuth auth)
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
            result = await client.ExecuteAsync(command, context.RequestAborted).ConfigureAwait(false);
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
    /// <param name="Passphrase">The typed passphrase; verified locally, sent nowhere (ADR-0041).</param>
    private sealed record GateRequest(string? Passphrase);

    /// <summary>The gate's answer to the page.</summary>
    /// <param name="Outcome"><c>verified</c>, <c>wrong</c>, or <c>unavailable</c>.</param>
    /// <param name="Detail">What an unavailable outcome met.</param>
    /// <param name="Envelope">
    /// A sealed restore grant for a write-only archive (ADR-0042 §5), hex —
    /// opaque to the page, opened only by the service; the wizard passes it
    /// into <c>open_restore_source</c>. Null on v1 archives.
    /// </param>
    private sealed record GateResponse(string Outcome, string? Detail, string? Envelope = null);

    /// <summary>
    /// The restore wizard's passphrase gate (ADR-0041): the one endpoint that
    /// handles a secret, and the secret goes no further than this process —
    /// verification is <see cref="ConsoleRestoreGate"/> deriving against the
    /// archive's key files on local disk. The service is consulted only for
    /// WHERE the archives live (a path, not a secret), never given the
    /// passphrase it already holds its own copy of.
    /// </summary>
    private static async Task RestoreGateAsync(HttpContext context, IServiceClientFactory clients, ConsoleAuth auth)
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

        string? archivesRoot = null;
        string? grantRecipient = null;
        try
        {
            await using var client = await clients.ConnectAsync(context.RequestAborted).ConfigureAwait(false);
            if (await client.ExecuteAsync(new DescribeServiceCommand(), context.RequestAborted).ConfigureAwait(false)
                is ServiceDescriptionResult description)
            {
                archivesRoot = description.ArchivesRoot;
                grantRecipient = description.RestoreGrantRecipient;
            }
        }
        catch (ServiceConnectionException)
        {
            // No service answering means no restore either; the gate's
            // unavailable answer lets the page say so in one place.
        }

        var answer = await ConsoleRestoreGate.VerifyAsync(
            archivesRoot, passphrase, grantRecipient, context.RequestAborted).ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new GateResponse(answer.Outcome.ToString().ToLowerInvariant(), answer.Detail, answer.GrantEnvelope),
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
                description.ArchivesRoot, target.Id, request.Passphrase, description.RestoreGrantRecipient,
                context.RequestAborted).ConfigureAwait(false);
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
    /// <param name="Kit">The recovery kit, when provisioned — see <see cref="SetupKit"/>.</param>
    private sealed record SetupResponse(
        string Outcome, string? Detail = null,
        IReadOnlyList<string>? Lines = null, IReadOnlyList<string>? Findings = null,
        SetupKit? Kit = null);

    /// <summary>
    /// The recovery kit, handed to the page rather than kept here.
    /// </summary>
    /// <remarks>
    /// Returned inline so this host holds no kit between two requests. The
    /// page turns these into downloads; nothing about the kit is secret
    /// (FR-KIT-002), and a console that stored one would be a console
    /// keeping a copy of the thing the operator is being asked to take
    /// somewhere else.
    /// </remarks>
    /// <param name="Machine">The framed binary form, base64.</param>
    /// <param name="Text">The printable transcribable form.</param>
    /// <param name="Checksum">The kit's SHA-256, lowercase hex.</param>
    private sealed record SetupKit(string Machine, string Text, string Checksum);

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
    private static async Task SetupAsync(HttpContext context, IServiceClientFactory clients, ConsoleAuth auth)
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
                + $"{PassphraseStrength.MinimumLength} characters, and more than one repeated unit).",
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

            // One Argon2id pass produces both the sealed envelope and the
            // recovery kit — they come from the same derivation, so deriving
            // twice would be paying that cost to learn nothing.
            var minted = ConsoleRestoreGate.BuildInstallationSetup(
                request.Passphrase, description.RestoreGrantRecipient, description.DeviceId ?? string.Empty);
            if (minted.Outcome != ConsoleRestoreGate.GateOutcome.Verified)
            {
                await AnswerAsync(new SetupResponse("unavailable", minted.Detail)).ConfigureAwait(false);
                return;
            }

            var result = await client.ExecuteAsync(
                new ProvisionInstallationCommand(minted.Envelope!), context.RequestAborted).ConfigureAwait(false);

            await AnswerAsync(result switch
            {
                ConfigurationChangeResult change => new SetupResponse(
                    "provisioned",
                    Lines: change.Lines,
                    Kit: new SetupKit(
                        Convert.ToBase64String(minted.Kit!.Framed), minted.Kit.Text, minted.Kit.Checksum)),
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

    /// <summary>What the recovery-kit endpoint reads from the page.</summary>
    /// <param name="Passphrase">The typed passphrase; derived from here, sent nowhere.</param>
    private sealed record KitRequest(string? Passphrase);

    /// <summary>The recovery-kit endpoint's answer.</summary>
    /// <param name="Outcome"><c>built</c>, <c>wrong</c>, or <c>unavailable</c>.</param>
    /// <param name="Detail">Why not, when it was not built.</param>
    /// <param name="Kit">The kit in both forms.</param>
    private sealed record KitResponse(string Outcome, string? Detail = null, SetupKit? Kit = null);

    /// <summary>
    /// Rebuilds this installation's recovery kit for a ceremony that was
    /// interrupted before the kit was confirmed saved (FR-KIT-004).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one place the ceremony asks for the passphrase a second
    /// time, and only because the first attempt did not finish. Nothing is
    /// stored between the two visits: the service keeps no kit, and this
    /// host keeps no passphrase — so the only way back to a kit is the way
    /// it was made.
    /// </para>
    /// <para>
    /// The salt comes from the archive the installation already wrote, via
    /// the same derive-and-compare the restore gate uses, so a wrong
    /// passphrase produces no kit rather than a kit that opens nothing. An
    /// installation with no archive yet has no salt to recover from — its
    /// operator has to finish the original ceremony, and the endpoint says
    /// so rather than minting a second root.
    /// </para>
    /// </remarks>
    private static async Task RecoveryKitAsync(HttpContext context, IServiceClientFactory clients, ConsoleAuth auth)
    {
        if (!auth.Authorizes(context.Request))
        {
            await RefuseAsync(context, StatusCodes.Status401Unauthorized, "token_missing_or_wrong",
                Strings.WebConsoleHost_TokenMissingOrWrong).ConfigureAwait(false);
            return;
        }

        KitRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<KitRequest>(
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

        async Task AnswerAsync(KitResponse response)
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

            if (await client.ExecuteAsync(new DescribeServiceCommand(), context.RequestAborted).ConfigureAwait(false)
                is not ServiceDescriptionResult description)
            {
                await AnswerAsync(new KitResponse("unavailable", "The service did not describe itself."))
                    .ConfigureAwait(false);
                return;
            }

            var sets = await client.ExecuteAsync(new ListBackupSetsCommand(), context.RequestAborted)
                .ConfigureAwait(false) as BackupSetsResult;

            var rebuilt = await ConsoleRestoreGate.RebuildInstallationKitAsync(
                description.ArchivesRoot, sets?.Sets.Select(set => set.Id) ?? [],
                request.Passphrase, description.DeviceId ?? string.Empty, context.RequestAborted)
                .ConfigureAwait(false);

            await AnswerAsync(rebuilt.Outcome switch
            {
                ConsoleRestoreGate.GateOutcome.Verified => new KitResponse(
                    "built",
                    Kit: new SetupKit(
                        Convert.ToBase64String(rebuilt.Kit!.Framed), rebuilt.Kit.Text, rebuilt.Kit.Checksum)),
                ConsoleRestoreGate.GateOutcome.Wrong => new KitResponse("wrong", rebuilt.Detail),
                _ => new KitResponse("unavailable", rebuilt.Detail),
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
            "Long enough that ordinary words are fine — no digits or symbols needed.",
        _ => "Several kinds of character, which is what you want.",
    };

    /// <summary>
    /// Bridges the contract's progress stream onto server-sent events. One
    /// service watch per subscribed page; the browser's <c>EventSource</c>
    /// reconnects on its own when either end goes away.
    /// </summary>
    private static async Task StreamEventsAsync(HttpContext context, IServiceClientFactory clients, ConsoleAuth auth)
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

            try
            {
                // Ask EventSource to wait a beat before redialling, so a
                // stopped service is polite retries rather than a busy loop.
                await context.Response.WriteAsync("retry: 2000\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

                await foreach (var progress in client.WatchAsync(context.RequestAborted).ConfigureAwait(false))
                {
                    var json = JsonSerializer.Serialize(progress, SerializerOptions);
                    await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The browser went away; nothing to tell anyone.
            }
        }
    }

    private static void MapStaticAsset(WebApplication app, string path, string resource, string contentType)
    {
        var bytes = LoadEmbedded(resource);
        app.MapGet(path, async (HttpContext context) =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = contentType;
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
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
