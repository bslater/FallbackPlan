using Microsoft.Extensions.Logging;

namespace FallbackPlan.Web;

/// <summary>
/// Web console diagnostics (ADR-0043; event ids 4100–4199): the binding, the
/// host check, and whether the service answered.
/// </summary>
/// <remarks>
/// Two tiers. The operational messages (4100–4102) are deliberately few: the
/// console is a relay — the work it reports on happens in the service, which
/// logs it there, and duplicating a command's life on both sides of a socket
/// produces two accounts of one event that disagree the moment either
/// changes. The trace tier (4110–4114) does not break that rule, because it
/// reports what only this process knows: that a request arrived and what
/// status answered it, how a setup ceremony was classified, and which asset
/// bytes were served. Outcomes and names, never bodies — three of these
/// endpoints carry a passphrase, and no message here takes one as a
/// parameter in any form (ADR-0043).
/// <para>
/// The 401 path is not among them. A request without the run's token is the
/// ordinary state of a browser that has not been handed the URL yet, it happens
/// on every page load before the token is presented, and the token it would be
/// guessing at is 256 bits from the system generator. Logging it would be
/// volume without signal. A request from a rebound hostname is the opposite:
/// rare, deliberate, and worth a line.
/// </para>
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 4100, Level = LogLevel.Information,
        Message = "Console listening on loopback port {Port} for state directory {StateDirectory}")]
    internal static partial void ConsoleBound(ILogger logger, int port, string stateDirectory);

    [LoggerMessage(
        EventId = 4101, Level = LogLevel.Warning,
        Message = "Request refused: Host header {Host} is not loopback")]
    internal static partial void HostNotLoopback(ILogger logger, string host);

    [LoggerMessage(
        EventId = 4102, Level = LogLevel.Warning,
        Message = "The service holding the writer role did not answer the start-up probe")]
    internal static partial void ServiceUnreachable(ILogger logger);

    [LoggerMessage(
        EventId = 4110, Level = LogLevel.Trace,
        Message = "{Endpoint} answered {StatusCode} in {ElapsedMilliseconds} ms")]
    internal static partial void RequestHandled(
        ILogger logger, string endpoint, int statusCode, long elapsedMilliseconds);

    [LoggerMessage(
        EventId = 4111, Level = LogLevel.Debug,
        Message = "Setup answered '{Outcome}' (kit included: {KitIncluded})")]
    internal static partial void SetupOutcome(ILogger logger, string outcome, bool kitIncluded);

    [LoggerMessage(
        EventId = 4112, Level = LogLevel.Debug,
        Message = "Recovery-kit rebuild answered '{Outcome}' (kit included: {KitIncluded})")]
    internal static partial void RecoveryKitOutcome(ILogger logger, string outcome, bool kitIncluded);

    [LoggerMessage(
        EventId = 4113, Level = LogLevel.Trace,
        Message = "Relayed {Command}; the service answered {Result} in {ElapsedMilliseconds} ms")]
    internal static partial void CommandRelayed(
        ILogger logger, string command, string result, long elapsedMilliseconds);

    [LoggerMessage(
        EventId = 4114, Level = LogLevel.Trace,
        Message = "Served {Path} ({ByteCount} bytes, embedded at build time)")]
    internal static partial void StaticAssetServed(ILogger logger, string path, int byteCount);
}
