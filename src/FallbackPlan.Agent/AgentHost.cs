using System.Globalization;
using FallbackPlan.Application;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Agent;

/// <summary>
/// The Agent host's command line, as a callable unit. The loop, argument
/// parsing and error mapping used to live in <c>Main</c>, where only
/// launching a process could reach them; the pass itself was tested, but
/// everything around it — the usage errors, the exit codes, the
/// <c>--once</c> contract — was not.
/// </summary>
public static class AgentHost
{
    /// <summary>Runs the agent with the given command line.</summary>
    /// <param name="args">The command line, as the process received it.</param>
    /// <param name="output">Where run lines and help are written.</param>
    /// <param name="error">Where operator-facing failures are written.</param>
    /// <param name="cancellationToken">Stops the poll loop; a clean shutdown, not a failure.</param>
    /// <returns>0 on success, 1 for a usage or open failure, 2 when a pass reported a failed set.</returns>
    public static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        // The Agent host (ADR-0027): a thin loop over AgentPass. Every behaviour —
        // schedule arithmetic, job transitions, status derivation — lives in
        // Application or AgentPass where tests drive it; this file only parses
        // arguments, reads the passphrase by name, and sleeps between passes.

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine("""
                FallbackPlan Agent — scheduled backups over the configured sets

                usage:
                  fallbackplan-agent run   --repo <path> --state <dir> --passphrase-env <VAR> [--once]
                                           [--poll-seconds <n>]   (default 60)

                Backup sets and their schedules come from <state>/config.json.
                Missed runs coalesce to one catch-up run per set (ADR-0027 §1).
                """);
            return 0;
        }

        string? Get(string name)
        {
            for (var i = 1; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        var repoPath = Get("--repo");
        var stateDirectory = Get("--state");
        var passphraseVariable = Get("--passphrase-env");

        if (args[0] != "run" || repoPath is null || stateDirectory is null || passphraseVariable is null)
        {
            error.WriteLine("error: usage is `run --repo <path> --state <dir> --passphrase-env <VAR>`.");
            return 1;
        }

        var passphraseValue = Environment.GetEnvironmentVariable(passphraseVariable);
        if (string.IsNullOrEmpty(passphraseValue))
        {
            error.WriteLine(
                $"error: environment variable '{passphraseVariable}' is unset — the passphrase is passed by name, never on the command line.");
            return 1;
        }

        var once = args.Contains("--once");
        var pollSeconds = Get("--poll-seconds") is { } poll
            ? int.Parse(poll, CultureInfo.InvariantCulture)
            : 60;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var passphrase = Passphrase.Create(passphraseValue);
                var result = await AgentPass.RunAsync(
                    repoPath, passphrase, stateDirectory, DateTimeOffset.Now, cancellationToken).ConfigureAwait(false);

                foreach (var set in result.Sets)
                {
                    output.WriteLine($"{DateTimeOffset.Now:u}  {set.SetName,-20} {set.Outcome}{(set.Detail is null ? "" : "  " + set.Detail)}");
                }

                if (once)
                {
                    return result.Failed == 0 ? 0 : 2;
                }

                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A clean shutdown: in-flight publication either completed or will be
            // resumed by the engine's own checkpoints — the Agent owns neither.
        }
        catch (ClientStateException exception)
        {
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (RepositoryOpenException exception)
        {
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (KeyUnwrapFailedException)
        {
            error.WriteLine("error: the passphrase does not open this repository.");
            return 1;
        }

        return 0;

    }
}
