using Bodu;
using System.Globalization;
using System.Net;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Keystore;
using FallbackPlan.Protocol;
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
        ThrowHelper.ThrowIfNull(args);
        ThrowHelper.ThrowIfNull(output);
        ThrowHelper.ThrowIfNull(error);

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine("""
                FallbackPlan service — scheduled backups, and the command surface clients talk to

                usage:
                  fallbackplan-agent run    --repo <path> --state <dir> [--passphrase-env <VAR>]
                                            [--once] [--poll-seconds <n>]   (default 60)
                                            [--remote-interface <ip> --remote-port <n>]
                  fallbackplan-agent unlock --repo <path> --state <dir> --passphrase-env <VAR>
                  fallbackplan-agent lock   --state <dir>
                  fallbackplan-agent pair   --state <dir> --remote-interface <ip> --remote-port <n> [--label <name>]
                  fallbackplan-agent pairings --state <dir>
                  fallbackplan-agent unpair --state <dir> --fingerprint <fp>

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

        if (args[0] is not ("run" or "unlock" or "lock" or "pair" or "pairings" or "unpair"))
        {
            error.WriteLine("error: usage is `run`, `unlock`, `lock`, `pair`, `pairings`, or `unpair` — no other verb exists.");
            return 1;
        }

        // The pairing verbs need the state directory and no repository — a
        // device's peer identity and its grants live beside the state, not
        // inside the repository (ADR-0030 §1).
        if (args[0] is "pair" or "pairings" or "unpair")
        {
            if (stateDirectory is null)
            {
                error.WriteLine($"error: usage is `{args[0]} --state <dir>`.");
                return 1;
            }

            return args[0] switch
            {
                "pairings" => ListPairings(stateDirectory, output),
                "unpair" => Unpair(stateDirectory, Get("--fingerprint"), output, error),
                _ => await PairAsync(stateDirectory, Get("--remote-interface"), Get("--remote-port"), Get("--label"),
                    output, error, cancellationToken).ConfigureAwait(false),
            };
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

        // The remote binding is off unless the operator names an interface to
        // bind (FR-SVC-003) — an explicit administrative act, never inferred.
        RemoteBindingOptions remoteBinding;
        if (Get("--remote-interface") is { } remoteInterface)
        {
            if (Get("--remote-port") is not { } portText
                || !int.TryParse(portText, CultureInfo.InvariantCulture, out var remotePort))
            {
                error.WriteLine("error: --remote-interface requires --remote-port <n>.");
                return 1;
            }

            remoteBinding = new RemoteBindingOptions { Enabled = true, Interface = remoteInterface, Port = remotePort };
        }
        else
        {
            remoteBinding = RemoteBindingOptions.Disabled;
        }

        if (!remoteBinding.TryValidate(out var bindingReason))
        {
            error.WriteLine($"error: {bindingReason}");
            return 1;
        }

        if (remoteBinding is { Enabled: true } && !IPAddress.TryParse(remoteBinding.Interface, out _))
        {
            error.WriteLine($"error: --remote-interface '{remoteBinding.Interface}' is not an IP address to bind.");
            return 1;
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

            // The remote binding, when enabled, is opened before the command
            // surface so its state can be reported through DescribeService. Its
            // device keypair lives beside the grants it authenticates; both
            // come up here and nowhere earlier, so a default install touches
            // neither.
            RemoteServiceListener? remoteListener = null;
            PeerKeypair? peerKeypair = null;
            var bindingState = RemoteBindingState.Off;

            try
            {
                if (remoteBinding.Enabled)
                {
                    peerKeypair = PeerKeypairStore.Open(stateDirectory);
                    var grants = PeerGrantStore.Open(stateDirectory);
                    var endpoint = new IPEndPoint(IPAddress.Parse(remoteBinding.Interface!), remoteBinding.Port);
                    remoteListener = RemoteServiceListener.Start(
                        peerKeypair, grants, endpoint, "fallbackplan-agent/0.1",
                        log: line => output.WriteLine($"{DateTimeOffset.Now:u}  {line}"));
                    bindingState = RemoteBindingState.On(remoteListener.Endpoint.ToString());
                }

                // The command surface comes up before the first pass, so a client
                // that starts alongside the service is not told "nothing is
                // listening" while a ten-hour backup runs. The binding state was
                // seeded from the remote listener's bound endpoint above.
                var handler = new ServiceCommandHandler(runtime, bindingState);

                // The remote socket bound before the handler existed so its
                // endpoint could seed the binding state; it begins serving now
                // that the handler exists.
                remoteListener?.Bind(handler);

                await using var localListener = LocalServiceListener.Start(handler, stateDirectory);
                if (!once)
                {
                    output.WriteLine($"{DateTimeOffset.Now:u}  listening on {localListener.Address}");
                    if (remoteListener is not null)
                    {
                        output.WriteLine(
                            $"{DateTimeOffset.Now:u}  remote binding on {remoteListener.Endpoint}"
                            + $" (peer {peerKeypair!.Identity.Fingerprint})");
                    }
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
            finally
            {
                if (remoteListener is not null)
                {
                    await remoteListener.DisposeAsync().ConfigureAwait(false);
                }

                peerKeypair?.Dispose();
            }
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

    /// <summary>
    /// Runs one pairing ceremony as the responding side (ADR-0030 §2): accept a
    /// single connection, show the operator the string and the peer, and pin on
    /// approval. Reads y/n from <see cref="Console.In"/>.
    /// </summary>
    private static async Task<int> PairAsync(
        string stateDirectory,
        string? remoteInterface,
        string? remotePort,
        string? label,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (remoteInterface is null || remotePort is null
            || !int.TryParse(remotePort, CultureInfo.InvariantCulture, out var port)
            || !IPAddress.TryParse(remoteInterface, out var address))
        {
            error.WriteLine("error: usage is `pair --state <dir> --remote-interface <ip> --remote-port <n> [--label <name>]`.");
            return 1;
        }

        using var keypair = PeerKeypairStore.Open(stateDirectory);
        var grants = PeerGrantStore.Open(stateDirectory);

        using var socket = new System.Net.Sockets.Socket(
            address.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(address, port));
        socket.Listen(backlog: 1);

        output.WriteLine($"this device is peer {keypair.Identity.Fingerprint}");

        // The bound endpoint, not the requested one: a port of 0 asks the
        // operating system to assign one, and the operator (or a test) needs
        // to be told which.
        output.WriteLine($"waiting for a pairing connection on {socket.LocalEndPoint} …");
        output.Flush();

        var accepted = await socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await PeerTlsConnection.AcceptAsync(
            accepted, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        var result = await PairingCeremony.AcceptAsync(
            connection.Stream, keypair, grants, label ?? Environment.MachineName, PeerRole.StoresForUs, PeerTerms.None,
            (prospect, _) => ValueTask.FromResult(Approve(prospect, output)),
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);

        if (result.Approved)
        {
            output.WriteLine($"paired with {result.Grant!.Label} ({result.Grant.Identity.Fingerprint}).");
            return 0;
        }

        output.WriteLine($"pairing did not complete: {result.Refusal?.Text ?? "the peer went away"}.");
        return 1;
    }

    private static bool Approve(PairingProspect prospect, TextWriter output)
    {
        output.WriteLine($"pairing with {prospect.PeerLabel} (peer {prospect.PeerIdentity.Fingerprint})");
        output.WriteLine($"compare this string on both devices: {prospect.ShortAuthenticationString}");
        output.Write("do the strings match, and do you approve? [y/N] ");
        output.Flush();

        var answer = Console.In.ReadLine();
        return answer is not null && answer.Trim().StartsWith('y');
    }

    private static int ListPairings(string stateDirectory, TextWriter output)
    {
        var grants = PeerGrantStore.Open(stateDirectory);
        if (grants.Grants.Count == 0)
        {
            output.WriteLine("no pairings.");
            return 0;
        }

        foreach (var grant in grants.Grants.OrderBy(grant => grant.Label, StringComparer.Ordinal))
        {
            output.WriteLine($"{grant.Identity.Fingerprint}  {grant.Role,-11}  {grant.Label}");
        }

        return 0;
    }

    private static int Unpair(string stateDirectory, string? fingerprint, TextWriter output, TextWriter error)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            error.WriteLine("error: usage is `unpair --state <dir> --fingerprint <fp>`.");
            return 1;
        }

        var grants = PeerGrantStore.Open(stateDirectory);

        // Resolve the fingerprint to exactly one grant. A fingerprint is a
        // display handle, never the identity — so an ambiguous prefix is
        // refused rather than guessed, and revocation always acts on the full
        // key (ADR-0030 §1).
        var matches = grants.Grants
            .Where(grant => grant.Identity.Fingerprint.StartsWith(fingerprint, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            error.WriteLine($"error: no pairing matches '{fingerprint}'.");
            return 1;
        }

        if (matches.Count > 1)
        {
            error.WriteLine($"error: '{fingerprint}' matches {matches.Count} pairings; give more of the fingerprint.");
            return 1;
        }

        grants.Revoke(matches[0].Identity);
        output.WriteLine($"revoked the pairing with {matches[0].Label} ({matches[0].Identity.Fingerprint}).");
        return 0;
    }
}
