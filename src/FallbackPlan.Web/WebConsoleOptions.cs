using FallbackPlan.Web.Resources;

namespace FallbackPlan.Web;

/// <summary>What the console was asked to serve, parsed from its command line.</summary>
/// <remarks>
/// Deliberately small: a state directory, and optionally a port. There is no
/// flag for the interface, because there is no choice — the console binds
/// loopback and nothing else (ADR-0036 §2). Offering the weaker path would make
/// it the used path.
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

        if (!Directory.Exists(state))
        {
            failure = Strings.FormatWebConsoleOptions_StateDirectoryMissing(state);
            return false;
        }

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
