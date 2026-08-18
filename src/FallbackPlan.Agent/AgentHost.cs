using Bodu;
using System.Globalization;
using System.Net;
using FallbackPlan.Api;
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
                                            [--label <name>] [--role stores-here|stores-for-us|both] [--quota <bytes>]
                  fallbackplan-agent pairings --state <dir>
                  fallbackplan-agent unpair --state <dir> --fingerprint <fp> [--to <host:port>] [--no-notify]
                  fallbackplan-agent install --archives <root> --state <dir> [--user <account>]
                                            [--name <svc>] [--target systemd|launchd|windows]
                                            [--remote-interface <ip> --remote-port <n>]
                  fallbackplan-agent sync   --archives <root> --state <dir> [--passphrase-env <VAR>]
                                            [--set <name>] [--destination <name>]
                  fallbackplan-agent verify-destination --archives <root> --state <dir>
                                            [--passphrase-env <VAR>] [--set <name>]
                                            [--destination <name>] [--probe | --full]
                  fallbackplan-agent retention --archives <root> --state <dir> [--passphrase-env <VAR>] [--apply]
                  fallbackplan-agent notices --state <dir> [--ack <id>]

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

                `sync` converges declared destinations now, outside the schedule
                (ADR-0034 §3): one pass per matching (set, destination) pair,
                reported from the sync ledger. `retention` runs one pass per set
                — the report either way, tombstones, sweep and staging trim only
                with --apply (FR-GC-005).
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

        // The verb `replicate` used to push one archive by path. Removed, not
        // shimmed (pre-1.0): destinations are declared in the configuration
        // and the service syncs them itself (ADR-0034 §3).
        if (args[0] == "replicate")
        {
            error.WriteLine(
                "error: `replicate` was removed — declare the destination in config.json and the service "
                + "syncs it on every backup (ADR-0034 §3). Use `sync [--set <name>] [--destination <name>]` "
                + "to converge one now.");
            return 1;
        }

        if (args[0] is not ("run" or "unlock" or "lock" or "pair" or "pairings" or "unpair" or "install" or "sync" or "notices" or "retention" or "verify-destination"))
        {
            error.WriteLine(
                "error: usage is `run`, `unlock`, `lock`, `pair`, `pairings`, `unpair`, `install`, `sync`, `verify-destination`, `notices`, or `retention` — no other verb exists.");
            return 1;
        }

        // `notices` lists what awaits a human, or acknowledges one entry —
        // the durable third channel (architecture 10 §3.1): a peering that
        // ended at 3 a.m. is still known at breakfast.
        if (args[0] == "notices")
        {
            if (stateDirectory is null)
            {
                error.WriteLine("error: usage is `notices --state <dir> [--ack <id>]`.");
                return 1;
            }

            return await NoticesAsync(stateDirectory, Get("--ack"), output, error, cancellationToken)
                .ConfigureAwait(false);
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
                "unpair" => await UnpairAsync(
                    stateDirectory, Get("--fingerprint"), Get("--to"), args.Contains("--no-notify"),
                    output, error, cancellationToken).ConfigureAwait(false),
                _ => await PairAsync(stateDirectory, Get("--remote-interface"), Get("--remote-port"), Get("--label"),
                    Get("--role"), Get("--quota"), output, error, cancellationToken).ConfigureAwait(false),
            };
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

        // A one-shot verb that speaks the service surface: its own runtime
        // (taking the writer role for its duration), one command, the lines
        // printed. The expected start-up refusals — a running service holds
        // the role, an archive refuses to open, a wrong passphrase — are
        // rendered as errors here, exactly as the `run` verb renders them; an
        // unhandled stack trace is never the answer to a held lock.
        async Task<int> ServiceVerbAsync(
            Api.ServiceCommand command, Func<Api.ServiceResult, IReadOnlyList<string>?> reportLines)
        {
            try
            {
                using var verbPassphrase = Passphrase.Create(passphraseValue);
                await using var verbRuntime = await ServiceRuntime.StartAsync(
                    new ServiceOptions { ArchivesRoot = archivesRoot!, StateDirectory = stateDirectory },
                    verbPassphrase, cancellationToken).ConfigureAwait(false);

                var handler = new ServiceCommandHandler(verbRuntime, RemoteBindingState.Off);
                var result = await handler.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);

                if (reportLines(result) is { } lines)
                {
                    foreach (var line in lines)
                    {
                        output.WriteLine(line);
                    }

                    return 0;
                }

                if (result is Api.ServiceError failure)
                {
                    error.WriteLine($"error: {failure.Message}");
                    return 2;
                }

                error.WriteLine($"error: unexpected result '{result.GetType().Name}'.");
                return 2;
            }
            catch (ClientStateException exception)
            {
                // A running service, or a CLI holding the writer role, is
                // refused by name (FR-SVC-002) — the command surface of that
                // service is the way in while it runs.
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

        // `retention [--apply]` runs one pass per configured set
        // (architecture 07): the mandatory dry-run report either way,
        // tombstones, sweep and trim only with --apply — through the same
        // service surface a console uses, so the writer lane serialises it
        // against anything else that writes (FR-GC-005/008).
        if (args[0] == "retention")
        {
            return await ServiceVerbAsync(
                new Api.RetentionCommand(args.Contains("--apply")),
                result => (result as Api.RetentionResult)?.Lines).ConfigureAwait(false);
        }

        // `verify-destination [--set] [--destination] [--probe|--full]` asks
        // what a destination can still be trusted for (FR-DEST-001,
        // FR-VER-002, FR-VER-004): whether it could take a backup at all, or
        // whether the bytes it already holds still match what was sealed.
        // `verify` sweeps this hub's own staging archives instead.
        if (args[0] == "verify-destination")
        {
            return await ServiceVerbAsync(
                new Api.VerifyDestinationCommand(
                    Get("--set"), Get("--destination"), args.Contains("--full"), args.Contains("--probe")),
                result => (result as Api.VerifyDestinationResult)?.Lines).ConfigureAwait(false);
        }

        // `sync [--set] [--destination]` converges declared destinations now,
        // through the same command surface a console uses (FR-DEST-002,
        // ADR-0034 §3) — the fan-out runs on the transfer lane and the answer
        // is read from the refreshed sync ledger.
        if (args[0] == "sync")
        {
            return await ServiceVerbAsync(
                new Api.SyncCommand(Get("--set"), Get("--destination")),
                result => (result as Api.SyncResult)?.Lines).ConfigureAwait(false);
        }

        // Everything below is the `run` verb. Say so, rather than arriving
        // here by falling off the end of the branches above: a verb added to
        // the allow-list without its own branch would otherwise start the
        // service loop and never return, which reads as a hang rather than a
        // mistake. That happened once while this file was being extended.
        if (args[0] != "run")
        {
            error.WriteLine(
                $"error: `{args[0]}` is accepted but not implemented in this build — this is a defect, not a usage "
                + "error. Please report it.");
            return 70;
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
            Log = (message, exception) => output.WriteLine(
                $"{DateTimeOffset.Now:u}  {message}{(exception is null ? string.Empty : $": {exception.Message}")}"),
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
        string? quotaText,
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
                + " [--label <name>] [--role stores-here|stores-for-us|both] [--quota <bytes>]`.");
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

        // Terms belong to the side that owns the disk (01 §4). A quota of 0 —
        // the default — declares no byte ceiling (peer-protocol 05 §1).
        var quota = 0UL;
        if (quotaText is not null && !ulong.TryParse(quotaText, CultureInfo.InvariantCulture, out quota))
        {
            error.WriteLine($"error: --quota '{quotaText}' is not a number of bytes.");
            return 1;
        }

        if (quota > 0 && role == PeerRole.StoresForUs)
        {
            error.WriteLine(
                "error: --quota states what this device will store for the peer;"
                + " it applies with --role stores-here or both.");
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
            connection.Stream, keypair, grants, label ?? Environment.MachineName, role,
            new PeerTerms(quota, string.Empty, 0),
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

    private static async Task<int> NoticesAsync(
        string stateDirectory, string? acknowledgeId, TextWriter output, TextWriter error,
        CancellationToken cancellationToken)
    {
        // Through the running service when one is listening (ADR-0028 §3:
        // liveness decides): its NoticeStore is the live writer, and a second
        // process writing notices.json beside it would race the file. Direct
        // access remains the no-service path — the notices must be readable
        // at breakfast even when the agent is not running.
        try
        {
            await using var client = await LocalServiceClient.ConnectAsync(
                stateDirectory, "fallbackplan-agent", cancellationToken).ConfigureAwait(false);

            if (acknowledgeId is not null)
            {
                var result = await client.ExecuteAsync(
                    new AcknowledgeNoticeCommand(acknowledgeId), cancellationToken).ConfigureAwait(false);
                if (result is ServiceError refusal)
                {
                    error.WriteLine($"error: {refusal.Message}");
                    return 1;
                }

                output.WriteLine($"acknowledged {acknowledgeId}.");
                return 0;
            }

            if (await client.ExecuteAsync(new ListNoticesCommand(), cancellationToken).ConfigureAwait(false)
                is NoticesResult listed)
            {
                WriteNotices(
                    output,
                    [.. listed.Notices.Select(notice => (notice.Id, notice.RaisedAt, notice.Message))]);
                return 0;
            }

            error.WriteLine("error: the service answered a notice listing with something else.");
            return 1;
        }
        catch (ServiceConnectionException)
        {
            // No service holds the state directory; the file is ours to touch.
        }

        var notices = FallbackPlan.Application.NoticeStore.Open(stateDirectory);

        if (acknowledgeId is not null)
        {
            if (!notices.Acknowledge(acknowledgeId, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                error.WriteLine($"error: no unacknowledged notice '{acknowledgeId}' exists.");
                return 1;
            }

            output.WriteLine($"acknowledged {acknowledgeId}.");
            return 0;
        }

        WriteNotices(
            output,
            [.. notices.Unacknowledged.Select(notice => (notice.Id, notice.RaisedAt, notice.Message))]);
        return 0;
    }

    private static void WriteNotices(TextWriter output, IReadOnlyList<(string Id, ulong RaisedAt, string Message)> pending)
    {
        if (pending.Count == 0)
        {
            output.WriteLine("no notices.");
            return;
        }

        foreach (var notice in pending)
        {
            var raised = DateTimeOffset.FromUnixTimeMilliseconds((long)notice.RaisedAt);
            output.WriteLine($"[{notice.Id}] {raised:u}  {notice.Message}");
        }
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

    private static async Task<int> UnpairAsync(
        string stateDirectory,
        string? fingerprint,
        string? to,
        bool noNotify,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            error.WriteLine("error: usage is `unpair --state <dir> --fingerprint <fp> [--to <host:port>] [--no-notify]`.");
            return 1;
        }

        var grants = PeerGrantStore.Open(stateDirectory);

        // Resolution refuses ambiguity rather than guessing (ADR-0030 §1);
        // the mechanics are shared with the contract's unpair command.
        var (grant, matchCount) = PeerUnpairing.Resolve(grants, fingerprint);
        if (matchCount == 0)
        {
            error.WriteLine($"error: no pairing matches '{fingerprint}'.");
            return 1;
        }

        if (grant is null)
        {
            error.WriteLine($"error: '{fingerprint}' matches {matchCount} pairings; give more of the fingerprint.");
            return 1;
        }

        // Best-effort notice first, while the grant still authenticates the
        // session (ADR-0030 Amendment 2): the ending should be a stated fact
        // at the other house, not unexplained refusals. Revocation never
        // waits on it — an unreachable peer learns from the Revoked refusal
        // at its next dial instead.
        if (!noNotify)
        {
            var endpoint = to ?? PeerUnpairing.EndpointFor(stateDirectory, grant.Identity.Fingerprint);
            if (endpoint is null)
            {
                output.WriteLine("no endpoint known for the peer — it will learn of the ending at its next dial.");
            }
            else
            {
                output.WriteLine(await PeerUnpairing.TryNotifyTerminationAsync(
                    stateDirectory, grants, grant, endpoint, cancellationToken).ConfigureAwait(false));
            }
        }

        grants.Revoke(grant.Identity);
        output.WriteLine($"revoked the pairing with {grant.Label} ({grant.Identity.Fingerprint}).");
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

}
