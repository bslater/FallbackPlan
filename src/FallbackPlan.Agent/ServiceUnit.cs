using System.Security;

namespace FallbackPlan.Agent;

/// <summary>What a generated service definition needs to name (ADR-0033).</summary>
/// <param name="ExecutablePath">The absolute path of the agent executable to run.</param>
/// <param name="RepositoryPath">The repository the service backs up to (<c>--repo</c>).</param>
/// <param name="StateDirectory">The state directory it holds the writer role for (<c>--state</c>).</param>
/// <param name="Account">
/// The account the service runs as, or null for the platform default. The keystore
/// entry the service self-unlocks from is scoped to this account, so it is the
/// account the operator must run <c>unlock</c> as (ADR-0028 §9).
/// </param>
/// <param name="ServiceName">The service's name for systemd and the Windows SCM.</param>
/// <param name="LaunchdLabel">The reverse-DNS label for the launchd job.</param>
/// <param name="RemoteInterface">The interface to bind the remote binding on, or null for off.</param>
/// <param name="RemotePort">The remote-binding port, or null for off.</param>
public sealed record ServiceUnitOptions(
    string ExecutablePath,
    string RepositoryPath,
    string StateDirectory,
    string? Account,
    string ServiceName,
    string LaunchdLabel,
    string? RemoteInterface,
    string? RemotePort);

/// <summary>
/// Generates the artifact that registers the agent with an operating system's
/// service manager (ADR-0033): a systemd unit, a launchd job, or the Windows
/// <c>sc.exe</c> commands. These are printed for an operator or an installer to
/// apply — the agent never registers itself, so what would run is inspectable
/// first. The generation is pure text and carries no platform calls, so it is the
/// same on every host and testable on any of them.
/// </summary>
public static class ServiceUnit
{
    private const string Description = "FallbackPlan backup service";

    /// <summary>The run arguments common to every target, in order.</summary>
    private static IEnumerable<string> RunArguments(ServiceUnitOptions options)
    {
        yield return "run";
        yield return "--repo";
        yield return options.RepositoryPath;
        yield return "--state";
        yield return options.StateDirectory;

        if (options.RemoteInterface is not null && options.RemotePort is not null)
        {
            yield return "--remote-interface";
            yield return options.RemoteInterface;
            yield return "--remote-port";
            yield return options.RemotePort;
        }
    }

    /// <summary>A systemd service unit (Linux).</summary>
    public static string Systemd(ServiceUnitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // systemd honours double quotes around each word, so a path with a space
        // survives; embedded quotes are escaped.
        var command = string.Join(' ',
            new[] { options.ExecutablePath }.Concat(RunArguments(options)).Select(SystemdQuote));

        var lines = new List<string>
        {
            "[Unit]",
            $"Description={Description}",
            "After=network-online.target",
            "Wants=network-online.target",
            string.Empty,
            "[Service]",
            "Type=simple",
            $"ExecStart={command}",
        };

        if (options.Account is not null)
        {
            lines.Add($"User={options.Account}");
        }

        lines.Add("Restart=on-failure");
        lines.Add("RestartSec=5");
        lines.Add(string.Empty);
        lines.Add("[Install]");
        lines.Add("WantedBy=multi-user.target");
        return string.Join('\n', lines) + '\n';
    }

    /// <summary>A launchd LaunchDaemon property list (macOS).</summary>
    public static string Launchd(ServiceUnitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var lines = new List<string>
        {
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" "
                + "\"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">",
            "<plist version=\"1.0\">",
            "<dict>",
            "    <key>Label</key>",
            $"    <string>{Xml(options.LaunchdLabel)}</string>",
            "    <key>ProgramArguments</key>",
            "    <array>",
        };

        foreach (var argument in new[] { options.ExecutablePath }.Concat(RunArguments(options)))
        {
            lines.Add($"        <string>{Xml(argument)}</string>");
        }

        lines.Add("    </array>");
        if (options.Account is not null)
        {
            lines.Add("    <key>UserName</key>");
            lines.Add($"    <string>{Xml(options.Account)}</string>");
        }

        lines.Add("    <key>RunAtLoad</key>");
        lines.Add("    <true/>");
        lines.Add("    <key>KeepAlive</key>");
        lines.Add("    <true/>");
        lines.Add("</dict>");
        lines.Add("</plist>");
        return string.Join('\n', lines) + '\n';
    }

    /// <summary>The <c>sc.exe</c> commands that register the Windows service.</summary>
    public static string Windows(ServiceUnitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // sc.exe wants the whole command as one quoted binPath value, with the
        // inner quotes that protect the exe path and each argument escaped.
        var inner = string.Join(' ',
            new[] { options.ExecutablePath }.Concat(RunArguments(options)).Select(WindowsInnerQuote));
        var binPath = inner.Replace("\"", "\\\"");

        var create = $"sc.exe create {options.ServiceName} binPath= \"{binPath}\" start= auto";
        if (options.Account is not null)
        {
            create += $" obj= \"{options.Account}\"";
        }

        var lines = new[]
        {
            create,
            $"sc.exe description {options.ServiceName} \"{Description}\"",
            $"sc.exe start {options.ServiceName}",
        };
        return string.Join('\n', lines) + '\n';
    }

    private static string SystemdQuote(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static string WindowsInnerQuote(string value) => $"\"{value}\"";

    private static string Xml(string value) => SecurityElement.Escape(value) ?? value;
}
