using System.Diagnostics;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The pairing ceremony performed by two real operating-system processes
/// (ADR-0030 §2): the shipped Agent and CLI apphosts, each shown the short
/// authentication string and each fed a human's approval on its own standard
/// input. This is the thing every in-process test could not be — the ceremony
/// carried out by two people at two machines, the string compared out of band.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PairingCeremonyProcessTests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fbp-pair-process", Guid.NewGuid().ToString("n"));

    private static string ApphostPath(string name)
    {
        var fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        Assert.IsTrue(File.Exists(path), $"the {name} apphost is not in the test output directory.");
        return path;
    }

    private static Process Start(string apphost, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = apphost,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info)!;
    }

    [TestMethod]
    public async Task Pairing_TwoRealProcessesApprove_PinEachOther()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var serviceState = Path.Combine(_scratch, "service-state");
        var consoleState = Path.Combine(_scratch, "console-state");

        // The service waits for a pairing connection on an OS-assigned port and
        // prints which one; the console then dials it.
        var service = Start(
            ApphostPath("FallbackPlan.Agent"),
            "pair", "--state", serviceState, "--remote-interface", "127.0.0.1", "--remote-port", "0", "--label", "service");

        var serviceOutput = new List<string>();
        var port = await ReadUntilAsync(
            service.StandardOutput, serviceOutput, line => TryParsePort(line, out _), timeout.Token);
        Assert.IsTrue(TryParsePort(port, out var boundPort), $"the service never announced a port: {port}");

        var console = Start(
            ApphostPath("FallbackPlan.Cli"),
            "pair", $"127.0.0.1:{boundPort}", "--state", consoleState, "--label", "console");

        // Both humans approve. The approval is buffered into each process's
        // standard input; each consumes it when its ceremony reaches the prompt.
        await service.StandardInput.WriteLineAsync("y");
        await service.StandardInput.FlushAsync(timeout.Token);
        await console.StandardInput.WriteLineAsync("y");
        await console.StandardInput.FlushAsync(timeout.Token);

        var consoleOutput = console.StandardOutput.ReadToEndAsync(timeout.Token);
        var serviceRest = service.StandardOutput.ReadToEndAsync(timeout.Token);

        await service.WaitForExitAsync(timeout.Token);
        await console.WaitForExitAsync(timeout.Token);

        Assert.AreEqual(0, service.ExitCode, await service.StandardError.ReadToEndAsync(timeout.Token));
        Assert.AreEqual(0, console.ExitCode, await console.StandardError.ReadToEndAsync(timeout.Token));

        // Both report a completed pairing, and both wrote a grant file — the
        // ceremony pinned each side to the other.
        Assert.Contains("paired with", await serviceRest, StringComparison.Ordinal);
        Assert.Contains("paired with", await consoleOutput, StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(Path.Combine(serviceState, "peers.json")));
        Assert.IsTrue(File.Exists(Path.Combine(consoleState, "peers.json")));
    }

    private static bool TryParsePort(string? line, out int port)
    {
        port = 0;
        if (line is null)
        {
            return false;
        }

        var colon = line.LastIndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        // The tail after the last colon, trimming the trailing " …".
        var tail = new string(line[(colon + 1)..].Where(char.IsDigit).ToArray());
        return int.TryParse(tail, out port) && port is > 0 and <= 65535;
    }

    private static async Task<string> ReadUntilAsync(
        StreamReader reader, List<string> collected, Func<string, bool> predicate, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            collected.Add(line);
            if (predicate(line))
            {
                return line;
            }
        }

        throw new InvalidOperationException(
            $"the process ended before the expected line. Saw:\n{string.Join('\n', collected)}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            try
            {
                Directory.Delete(_scratch, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
