using Bodu;
using System.Globalization;
using System.Net;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Keystore;
using FallbackPlan.Protocol;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

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
                  fallbackplan-agent run    --archives <root> --state <dir> [--passphrase-env <VAR>]
                                            [--once] [--poll-seconds <n>]   (default 60)
                                            [--remote-interface <ip> --remote-port <n>]
                  fallbackplan-agent unlock --archives <root> --state <dir> --passphrase-env <VAR>
                  fallbackplan-agent lock   --state <dir>
                  fallbackplan-agent pair   --state <dir> --remote-interface <ip> --remote-port <n>
                                            [--label <name>] [--role stores-here|stores-for-us|both]
                  fallbackplan-agent pairings --state <dir>
                  fallbackplan-agent unpair --state <dir> --fingerprint <fp>
                  fallbackplan-agent install --archives <root> --state <dir> [--user <account>]
                                            [--name <svc>] [--target systemd|launchd|windows]
                                            [--remote-interface <ip> --remote-port <n>]
                  fallbackplan-agent replicate --repo <path> --state <dir> --to <host:port> --fingerprint <fp>

                Backup sets, their destinations and their schedules come from
                <state>/config.json. Each set's staging archive lives under
                --archives as <root>/<set id>, created on the set's first backup
                (ADR-0034). Missed runs coalesce to one catch-up run per set
                (ADR-0027 §1).

                `unlock` stores the passphrase in this account's platform keystore so
                scheduled backups run with nobody present; `run` then needs no
                --passphrase-env. `lock` removes it. Key export always takes a
                passphrase per invocation and never reads the keystore
                (ADR-0028 section 9).

                While it runs the service holds the writer role for <dir> exclusively,
                and listens on a local socket or named pipe there. It listens on no
                network port: the remote binding is off until explicitly enabled
                (ADR-0028 §5).

                `install` prints the definition that registers this agent with the
                operating system's service manager — a systemd unit, a launchd job,
                or the Windows `sc.exe` commands (default: this platform). It only
                prints it; nothing is changed. Store the passphrase with `unlock`
                first, as the account the service will run as (ADR-0033).

                `replicate` pushes one archive's objects to a destination this
                console paired with (peer-protocol 03), naming the archive by
                path (a set's staging archive is <root>/<set id>) and the
                destination by fingerprint. It forwards encrypted objects and
                takes no passphrase; the destination stores what it cannot read.
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

        var archivesRoot = Get("--archives");
        var repoPath = Get("--repo");
        var stateDirectory = Get("--state");
        var passphraseVariable = Get("--passphrase-env");

        if (args[0] is not ("run" or "unlock" or "lock" or "pair" or "pairings" or "unpair" or "install" or "replicate"))
        {
            error.WriteLine(
                "error: usage is `run`, `unlock`, `lock`, `pair`, `pairings`, `unpair`, `install`, or `replicate` — no other verb exists.");
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
                    Get("--role"), output, error, cancellationToken).ConfigureAwait(false),
            };
        }

        // `replicate` pushes one archive's objects to a paired destination
        // (peer-protocol 03). It reads raw objects and needs no passphrase —
        // replication forwards ciphertext. It is the one verb that still takes
        // an archive by path: any repository replicates, staging or otherwise.
        if (args[0] == "replicate")
        {
            if (stateDirectory is null || repoPath is null)
            {
                error.WriteLine(
                    "error: usage is `replicate --repo <path> --state <dir> --to <host:port> --fingerprint <fp>`.");
                return 1;
            }

            return await ReplicateAsync(
                repoPath, stateDirectory, Get("--to"), Get("--fingerprint"), output, error, cancellationToken)
                .ConfigureAwait(false);
        }

        if (repoPath is not null && archivesRoot is null)
        {
            // The old single-repository flag, refused with directions rather
            // than reinterpreted: --archives names a root that holds one
            // staging archive per set (ADR-0034), which is not what a --repo
            // caller was pointing at.
            error.WriteLine(
                "error: `--repo` became `--archives <root>` — the service holds one staging archive per "
                + "backup set under that root (ADR-0034). An existing single archive can be adopted by "
                + "moving it to <root>/<set id>.");
            return 1;
        }

        if (stateDirectory is null || (args[0] is not "lock" && archivesRoot is null))
        {
            error.WriteLine("error: usage is `run --archives <root> --state <dir>`.");
            return 1;
        }

        // `install` opens neither the repository nor the keystore: it only prints
        // the definition that would register this agent as a service (ADR-0033).
        if (args[0] == "install")
        {
            return Install(
                archivesRoot!, stateDirectory, Get("--user"), Get("--name"), Get("--target"),
                Get("--remote-interface"), Get("--remote-port"), output, error);
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
            ArchivesRoot = archivesRoot!,
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
                        log: line => output.WriteLine($"{DateTimeOffset.Now:u}  {line}"),
                        replicationStateDirectory: stateDirectory);
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
        string? roleText,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (remoteInterface is null || remotePort is null
            || !int.TryParse(remotePort, CultureInfo.InvariantCulture, out var port)
            || !IPAddress.TryParse(remoteInterface, out var address))
        {
            error.WriteLine(
                "error: usage is `pair --state <dir> --remote-interface <ip> --remote-port <n>"
                + " [--label <name>] [--role stores-here|stores-for-us|both]`.");
            return 1;
        }

        // The role this device records for the dialler — what the dialler may
        // do here (01 §3). stores-for-us is the console pairing; a spoke
        // accepting a hub that will store here says stores-here. The declared
        // role rides the ceremony's transcript, so both humans approve it
        // (ADR-0030 Amendment 2).
        if (!PeerRoles.TryParse(roleText, out var role))
        {
            error.WriteLine($"error: --role '{roleText}' is not stores-here, stores-for-us, or both.");
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
            connection.Stream, keypair, grants, label ?? Environment.MachineName, role, PeerTerms.None,
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
        output.WriteLine($"they will record this device as: {DescribeRole(prospect.TheirRoleForUs)}");
        output.WriteLine($"compare this string on both devices: {prospect.ShortAuthenticationString}");
        output.Write("do the strings match, and do you approve? [y/N] ");
        output.Flush();

        var answer = Console.In.ReadLine();
        return answer is not null && answer.Trim().StartsWith('y');
    }

    private static string DescribeRole(PeerRole role) => role switch
    {
        PeerRole.StoresHere => "stores-here (this device may store objects there)",
        PeerRole.StoresForUs => "stores-for-us (this device is a client or a source they store for)",
        _ => "both",
    };

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

    /// <summary>
    /// Prints the service-manager definition that would register this agent
    /// (ADR-0033) — the systemd unit, launchd job, or Windows <c>sc.exe</c>
    /// commands. The definition goes to standard output so it can be redirected
    /// to a file; the guidance for applying it, and the reminder to pre-seed the
    /// passphrase, go to standard error. Nothing on the system is changed.
    /// </summary>
    private static int Install(
        string archivesRoot,
        string stateDirectory,
        string? account,
        string? name,
        string? target,
        string? remoteInterface,
        string? remotePort,
        TextWriter output,
        TextWriter error)
    {
        var resolved = target ?? DefaultTarget();
        if (resolved is not ("systemd" or "launchd" or "windows"))
        {
            error.WriteLine($"error: unknown --target '{target}'; use systemd, launchd, or windows.");
            return 1;
        }

        var executablePath = Environment.ProcessPath;
        if (executablePath is null)
        {
            error.WriteLine("error: could not determine this executable's path to write into the service definition.");
            return 1;
        }

        // Absolutise the paths only when generating for this same platform — a
        // service needs absolute paths and the operator may have given relative
        // ones. Generating a foreign target's definition (a Windows unit from a
        // Linux box, say) must pass the paths through untouched, since this
        // platform's path rules would mangle the other's; there the operator
        // supplies absolute target paths.
        var forThisPlatform = resolved == DefaultTarget();
        var archivesPath = forThisPlatform ? Path.GetFullPath(archivesRoot) : archivesRoot;
        var statePath = forThisPlatform ? Path.GetFullPath(stateDirectory) : stateDirectory;

        var options = new ServiceUnitOptions(
            executablePath,
            archivesPath,
            statePath,
            account,
            name ?? "FallbackPlan",
            name ?? "com.fallbackplan.agent",
            remoteInterface,
            remotePort);

        var (artifact, apply) = resolved switch
        {
            "systemd" => (ServiceUnit.Systemd(options),
                $"write it to /etc/systemd/system/{options.ServiceName}.service, then "
                + $"`sudo systemctl daemon-reload && sudo systemctl enable --now {options.ServiceName}`."),
            "launchd" => (ServiceUnit.Launchd(options),
                $"write it to /Library/LaunchDaemons/{options.LaunchdLabel}.plist (owned by root), then "
                + $"`sudo launchctl load /Library/LaunchDaemons/{options.LaunchdLabel}.plist`."),
            _ => (ServiceUnit.Windows(options),
                "run the commands above from an elevated prompt."),
        };

        error.WriteLine(
            $"# The {resolved} definition to register FallbackPlan as a service. This only prints it; "
            + "nothing on this machine is changed. Review it, then apply it.");
        output.Write(artifact);
        if (!artifact.EndsWith('\n'))
        {
            output.WriteLine();
        }

        error.WriteLine($"# To apply: {apply}");
        error.WriteLine(
            "# First, store the passphrase once as the SAME account the service runs as, so it self-unlocks "
            + "at boot with nobody present (ADR-0028 §9):");
        error.WriteLine(
            $"#   \"{executablePath}\" unlock --archives \"{options.ArchivesRoot}\" "
            + $"--state \"{options.StateDirectory}\" --passphrase-env <VAR>");
        return 0;
    }

    private static string DefaultTarget() =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "launchd"
        : "systemd";

    /// <summary>
    /// Pushes the repository's objects to a paired destination over the peer
    /// protocol (peer-protocol 03). The source reads raw objects and never
    /// decrypts one, so no passphrase is taken; the destination stores what it
    /// cannot read.
    /// </summary>
    private static async Task<int> ReplicateAsync(
        string repoPath,
        string stateDirectory,
        string? to,
        string? fingerprint,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(to) || !TryParseEndpoint(to, out var host, out var port))
        {
            error.WriteLine(
                "error: usage is `replicate --repo <path> --state <dir> --to <host:port> --fingerprint <fp>`.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            error.WriteLine("error: --fingerprint names the destination this console paired with.");
            return 1;
        }

        var grants = PeerGrantStore.Open(stateDirectory);
        var grant = grants.Grants.FirstOrDefault(
            candidate => string.Equals(candidate.Identity.Fingerprint, fingerprint, StringComparison.Ordinal));
        if (grant is null)
        {
            error.WriteLine($"error: no pairing matches fingerprint '{fingerprint}'.");
            return 1;
        }

        var store = new LocalFileSystemObjectStore(repoPath);
        RepositoryId repositoryId;
        try
        {
            repositoryId = await ReadRepositoryIdAsync(store, cancellationToken).ConfigureAwait(false);
        }
        catch (ClientStateException exception)
        {
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }

        using var keypair = PeerKeypairStore.Open(stateDirectory);
        output.WriteLine($"replicating {repositoryId} to {host}:{port} ({grant.Label}) …");

        try
        {
            await using var connection = await PeerTlsConnection.DialAsync(
                host, port, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            var session = await PeerSessionDriver.DialAsync(
                connection, keypair, grants, grant.Identity, "fallbackplan-agent", terms: null, cancellationToken)
                .ConfigureAwait(false);

            var committed = await ReplicationInitiator.PushAllAsync(
                store, repositoryId.ToArray(), session.Stream, cancellationToken).ConfigureAwait(false);

            ReplicationStateStore.Open(stateDirectory).Record(
                fingerprint, "all", committed, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            output.WriteLine($"replicated: the destination committed {committed} newly sent object(s).");
            return 0;
        }
        catch (PeerProtocolException refusal)
        {
            error.WriteLine($"error: the destination refused replication: {refusal.Reason} — {refusal.Message}");
            return 1;
        }
    }

    private static async Task<RepositoryId> ReadRepositoryIdAsync(
        LocalFileSystemObjectStore store, CancellationToken cancellationToken)
    {
        using var read = await store.OpenReadAsync(
            RepositoryLifecycle.DescriptorKey, range: null, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != OpenReadOutcome.Found || read.Content is null)
        {
            throw new ClientStateException("no repository descriptor at --repo; is it a repository?");
        }

        using var buffer = new MemoryStream();
        await read.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return RepositoryDescriptorCodec.Parse(buffer.ToArray()) is DescriptorParseResult.Ok ok
            ? ok.Descriptor.RepositoryId
            : throw new ClientStateException("the repository descriptor at --repo did not parse.");
    }

    private static bool TryParseEndpoint(string target, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var colon = target.LastIndexOf(':');
        if (colon <= 0 || colon == target.Length - 1)
        {
            return false;
        }

        host = target[..colon];
        return int.TryParse(target[(colon + 1)..], CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
    }
}
