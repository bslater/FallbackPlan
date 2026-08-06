using System.Globalization;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Keystore;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Agent;

/// <summary>
/// The service host's command line, as a callable unit.
/// </summary>
/// <remarks>
/// What this file no longer does is the interesting part. It used to re-derive
/// Argon2id, re-read the configuration and open and close SQLite on every poll,
/// because nothing was permitted to live between passes — two peer processes
/// sharing a state directory could not safely hold anything open. Holding the
/// writer role exclusively (ADR-0028 §2) is what makes holding the repository
/// open correct, and the loop is now a scheduler over a long-lived service
/// rather than a process that rebuilds itself once a minute.
/// </remarks>
public static class AgentHost
{
    /// <summary>Runs the service with the given command line.</summary>
    /// <param name="args">The command line, as the process received it.</param>
    /// <param name="output">Where run lines and help are written.</param>
    /// <param name="error">Where operator-facing failures are written.</param>
    /// <param name="cancellationToken">Stops the service; a clean shutdown, not a failure.</param>
    /// <returns>0 on success, 1 for a usage or open failure, 2 when a pass reported a failed set.</returns>
    public static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine("""
                FallbackPlan service — scheduled backups, and the command surface clients talk to

                usage:
                  fallbackplan-agent run    --repo <path> --state <dir> [--passphrase-env <VAR>]
                                            [--once] [--poll-seconds <n>]   (default 60)
                  fallbackplan-agent unlock --repo <path> --state <dir> --passphrase-env <VAR>
                  fallbackplan-agent lock   --state <dir>

                Backup sets and their schedules come from <state>/config.json.
                Missed runs coalesce to one catch-up run per set (ADR-0027 §1).

                `unlock` stores the passphrase in this account's platform keystore so
                scheduled backups run with nobody present; `run` then needs no
                --passphrase-env. `lock` removes it. Key export always takes a
                passphrase per invocation and never reads the keystore
                (ADR-0028 section 9).

                While it runs the service holds the writer role for <dir> exclusively,
                and listens on a local socket or named pipe there. It listens on no
                network port: the remote binding is off until explicitly enabled
                (ADR-0028 §5).
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

        if (args[0] is not ("run" or "unlock" or "lock"))
        {
            error.WriteLine("error: usage is `run`, `unlock`, or `lock` — no other verb exists.");
            return 1;
        }

        if (stateDirectory is null || (args[0] != "lock" && repoPath is null))
        {
            error.WriteLine("error: usage is `run --repo <path> --state <dir>`.");
            return 1;
        }

        string? FromEnvironment()
        {
            if (passphraseVariable is null)
            {
                return null;
            }

            var value = Environment.GetEnvironmentVariable(passphraseVariable);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        if (args[0] == "lock")
        {
            try
            {
                var store = PlatformKeystore.For(stateDirectory);
                store.Delete(stateDirectory);
                output.WriteLine($"removed the stored passphrase from {store.Description}.");
                return 0;
            }
            catch (KeystoreException exception)
            {
                error.WriteLine($"error: {exception.Message}");
                return 1;
            }
        }

        if (args[0] == "unlock")
        {
            var supplied = FromEnvironment();
            if (supplied is null)
            {
                error.WriteLine(
                    "error: `unlock` needs --passphrase-env <VAR> naming a set environment variable — the "
                    + "passphrase is passed by name, never on the command line.");
                return 1;
            }

            try
            {
                var store = PlatformKeystore.For(stateDirectory);
                store.Write(stateDirectory, supplied);
                output.WriteLine($"stored the passphrase in {store.Description}.");
                output.WriteLine(
                    "an attacker who obtains this service account obtains the backups — see T-19 in the threat model.");
                return 0;
            }
            catch (KeystoreException exception)
            {
                error.WriteLine($"error: {exception.Message}");
                return 1;
            }
        }

        if (passphraseVariable is not null && FromEnvironment() is null)
        {
            // An explicitly named variable that is unset is a mistake, not an
            // invitation to use the keystore instead: falling back would run
            // the backup under a different passphrase than the operator asked
            // for, and say nothing.
            error.WriteLine(
                $"error: environment variable '{passphraseVariable}' is unset — the passphrase is passed by name, never on the command line.");
            return 1;
        }

        var passphraseValue = FromEnvironment();
        if (passphraseValue is null)
        {
            // The keystore is what makes unattended scheduled backup possible
            // at all (ADR-0028 section 9). An environment variable held for the
            // life of the process, and inherited by every child, is the thing
            // it replaces.
            try
            {
                if (!PlatformKeystore.For(stateDirectory).TryRead(stateDirectory, out passphraseValue)
                    || passphraseValue is null)
                {
                    error.WriteLine(
                        "error: no passphrase. Either run `unlock --passphrase-env <VAR>` once to store it in this "
                        + "account's keystore, or pass --passphrase-env on every run.");
                    return 1;
                }
            }
            catch (KeystoreException exception)
            {
                error.WriteLine($"error: {exception.Message}");
                return 1;
            }
        }

        var once = args.Contains("--once");
        int pollSeconds;
        if (Get("--poll-seconds") is { } poll)
        {
            if (!int.TryParse(poll, CultureInfo.InvariantCulture, out pollSeconds) || pollSeconds <= 0)
            {
                error.WriteLine($"error: --poll-seconds '{poll}' is not a positive number of seconds.");
                return 1;
            }
        }
        else
        {
            pollSeconds = 60;
        }

        var options = new ServiceOptions
        {
            RepositoryPath = repoPath!,
            StateDirectory = stateDirectory,
            PollSeconds = pollSeconds,
        };

        try
        {
            using var passphrase = Passphrase.Create(passphraseValue);
            await using var runtime = await ServiceRuntime.StartAsync(options, passphrase, cancellationToken)
                .ConfigureAwait(false);

            // The command surface comes up before the first pass, so a client
            // that starts alongside the service is not told "nothing is
            // listening" while a ten-hour backup runs.
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            await using var listener = LocalServiceListener.Start(handler, stateDirectory);
            if (!once)
            {
                output.WriteLine($"{DateTimeOffset.Now:u}  listening on {listener.Address}");
            }

            var failed = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var set in result.Sets)
                {
                    output.WriteLine(
                        $"{DateTimeOffset.Now:u}  {set.SetName,-20} {set.Outcome}{(set.Detail is null ? "" : "  " + set.Detail)}");
                }

                failed = result.Failed;
                if (once)
                {
                    return failed == 0 ? 0 : 2;
                }

                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), cancellationToken).ConfigureAwait(false);
            }

            return failed == 0 ? 0 : 2;
        }
        catch (OperationCanceledException)
        {
            // A clean shutdown: in-flight publication either completed or will be
            // resumed by the engine's own checkpoints — the service owns neither.
            return 0;
        }
        catch (ClientStateException exception)
        {
            // This is where a second service, or a CLI holding the writer role,
            // is refused by name rather than proceeding into a shared sequence
            // space (FR-SVC-002).
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
    }
}
