using FallbackPlan.Web.Resources;

namespace FallbackPlan.Web;

/// <summary>What the console was asked to serve, parsed from its command line.</summary>
/// <remarks>
/// <para>
/// Deliberately small: a state directory, and optionally a port. There is no
/// flag for the interface, because there is no choice — the console binds
/// loopback and nothing else (ADR-0036 §2). Offering the weaker path would make
/// it the used path.
/// </para>
/// <para>
/// <c>--log-level</c> is accepted and discarded here. It belongs to the host
/// rather than to what is served, but a parser that refused it would refuse the
/// documented command line, which is worse than carrying one idle case.
/// </para>
/// </remarks>
public sealed record WebConsoleOptions
{
    /// <summary>The state directory whose service this console talks to.</summary>
    public required string StateDirectory { get; init; }

    /// <summary>The port to listen on; 0 asks the operating system for one.</summary>
    public int Port { get; init; }

    /// <summary>Parses the command line, or says why it could not.</summary>
    /// <param name="args">The command line, as the process received it.</param>
    /// <param name="options">The parsed options, when parsing succeeded.</param>
    /// <param name="failure">What to tell the operator, when it did not.</param>
    /// <returns>Whether parsing succeeded.</returns>
    public static bool TryParse(
        IReadOnlyList<string> args,
        out WebConsoleOptions? options,
        out string? failure)
    {
        Bodu.ThrowHelper.ThrowIfNull(args);

        options = null;
        failure = null;

        string? state = null;
        var port = 0;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--state":
                    if (!TryTakeValue(args, ref i, out var stateValue, ref failure))
                    {
                        return false;
                    }

                    state = stateValue;
                    break;

                case "--port":
                    if (!TryTakeValue(args, ref i, out var portValue, ref failure))
                    {
                        return false;
                    }

                    if (!int.TryParse(portValue, out port) || port is < 0 or > 65535)
                    {
                        failure = Strings.FormatWebConsoleOptions_PortInvalid(portValue);
                        return false;
                    }

                    break;

                case "--log-level":
                    // Consumed here so it is not an unknown argument, and
                    // resolved nowhere but WebConsoleHost, through
                    // ConsoleLogging.TryResolveLevel. Two places deciding what
                    // a level means would be two places to disagree; this one
                    // only has to know the flag takes a value.
                    if (!TryTakeValue(args, ref i, out _, ref failure))
                    {
                        return false;
                    }

                    break;

                default:
                    failure = Strings.FormatWebConsoleOptions_UnknownArgument(args[i]);
                    return false;
            }
        }

        if (state is null)
        {
            failure = Strings.WebConsoleOptions_StateDirectoryRequired;
            return false;
        }

        // A state directory that is not there yet is NOT a parse failure. The
        // service creates it, the console only reads through it, and the two
        // are routinely started together — so refusing here lost a race the
        // console is otherwise built to win. It is the same fact as a service
        // that is not listening, and it is reported the same way: bind, print
        // the URL, say nothing is listening yet, keep trying (ADR-0036). A
        // mistyped path then shows as a service that never answers, which is
        // what it is. Path.GetFullPath is textual, so the absolute path below
        // holds either way.
        options = new WebConsoleOptions { StateDirectory = Path.GetFullPath(state), Port = port };
        return true;
    }

    private static bool TryTakeValue(
        IReadOnlyList<string> args, ref int index, out string value, ref string? failure)
    {
        if (index + 1 >= args.Count)
        {
            failure = Strings.FormatWebConsoleOptions_FlagNeedsValue(args[index]);
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }
}
