using Microsoft.Extensions.Logging;

namespace FallbackPlan.Web;

/// <summary>
/// Web console diagnostics (ADR-0043; event ids 4100–4199): the binding, the
/// host check, and whether the service answered.
/// </summary>
/// <remarks>
/// Deliberately three messages. The console is a relay — the work it reports
/// on happens in the service, which logs it there, and duplicating a command's
/// life on both sides of a socket produces two accounts of one event that
/// disagree the moment either changes. What is logged here is what only this
/// process knows.
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

    // What only this process knows about a session it relays: that it chose
    // not to forward a command because the service had already refused the
    // session it rode in on. The service logs its own refusal (3755); this is
    // the console's half — the command that was consequently never sent. The
    // token itself has no parameter to ride in, deliberately.
    [LoggerMessage(
        EventId = 4103, Level = LogLevel.Debug,
        Message = "A relayed session was refused by the service; {Command} was not sent")]
    internal static partial void RelayedSessionRefused(ILogger logger, string command);

    [LoggerMessage(
        EventId = 4104, Level = LogLevel.Trace,
        Message = "Event stream opened (session presented: {SessionPresented})")]
    internal static partial void EventStreamOpened(ILogger logger, bool sessionPresented);

    [LoggerMessage(
        EventId = 4105, Level = LogLevel.Trace,
        Message = "Event stream ended after {Events} event(s)")]
    internal static partial void EventStreamEnded(ILogger logger, long events);
}
