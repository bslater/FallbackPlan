using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bodu;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
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
                  fallbackplan-web --state <dir> [--port <n>]

                The console talks to the service holding the writer role for <dir>
                over its local binding, exactly as the CLI does. It binds
                http://127.0.0.1 only — remote access to a service is what device
                pairing is for — and prints a URL carrying a fresh access token on
                every start. It holds no repository, no keys and no writer role: if
                no service is listening it says so, keeps trying, and the page shows
                the service as unreachable until one answers (ADR-0036).
                """).ConfigureAwait(false);
            return args.Length == 0 ? 1 : 0;
        }

        if (!WebConsoleOptions.TryParse(args, out var options, out var failure))
        {
            await error.WriteLineAsync(failure).ConfigureAwait(false);
            return 1;
        }

        var auth = ConsoleAuth.CreateWithRandomToken();
        IServiceClientFactory clients = new LocalServiceClientFactory(options!.StateDirectory);

        await using var console = await StartAsync(options, clients, auth, cancellationToken).ConfigureAwait(false);

        await output.WriteLineAsync($"state    {options.StateDirectory}").ConfigureAwait(false);
        await output.WriteLineAsync($"console  {console.TokenisedUrl}").ConfigureAwait(false);
        await output.WriteLineAsync(await ProbeServiceAsync(clients, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        await console.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Starts the console listening on loopback. The test seam: everything
    /// <see cref="RunAsync"/> does beyond this is printing.
    /// </summary>
    /// <param name="options">What to serve.</param>
    /// <param name="clients">Where connected service clients come from.</param>
    /// <param name="auth">The run's authenticator.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running console; dispose to stop listening.</returns>
    public static async Task<RunningConsole> StartAsync(
        WebConsoleOptions options,
        IServiceClientFactory clients,
        ConsoleAuth auth,
        CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(options);
        ThrowHelper.ThrowIfNull(clients);
        ThrowHelper.ThrowIfNull(auth);

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
        IServiceClientFactory clients, CancellationToken cancellationToken)
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
