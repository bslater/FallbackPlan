using Bodu;
using System.Globalization;
using System.Net;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Diagnostics;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Protocol;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using Microsoft.Extensions.Logging;

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

        if (args.Length > 0 && args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine("""
                FallbackPlan service — scheduled backups, and the command surface clients talk to

                usage:
                  fallbackplan-agent [run]  [--archives <root>] [--state <dir>]
                                            [--once] [--poll-seconds <n>]   (default 60)
                                            [--remote-interface <ip> --remote-port <n>]
                  fallbackplan-agent setup  --archives <root> --state <dir> --passphrase-env <VAR>
                                            --acknowledge-loss --user <name> --password-env <VAR>
                  fallbackplan-agent pair   --state <dir> --remote-interface <ip> --remote-port <n>
                                            [--label <name>] [--role stores-here|stores-for-us|both] [--quota <bytes>]
                  fallbackplan-agent pairings --state <dir>
                  fallbackplan-agent unpair --state <dir> --fingerprint <fp> [--to <host:port>] [--no-notify]
                  fallbackplan-agent reattribute --state <dir> --repository <hex> --to <fingerprint>
                  fallbackplan-agent install --archives <root> --state <dir> [--user <account>]
                                            [--name <svc>] [--target systemd|launchd|windows]
                                            [--remote-interface <ip> --remote-port <n>]
                  fallbackplan-agent sync   --archives <root> --state <dir>
                                            [--set <name>] [--destination <name>]
                  fallbackplan-agent verify-destination --archives <root> --state <dir>
                                            [--set <name>] [--destination <name>] [--probe | --full]
                  fallbackplan-agent retention --archives <root> --state <dir> [--passphrase-env <VAR>] [--apply]
                  fallbackplan-agent notices --state <dir> [--ack <id>]
                  fallbackplan-agent receipts --state <dir> [--kind deletion|replication] [--set <name>]
                                            [--repository <hex>] [--limit <n>] [--json]
                  fallbackplan-agent upgrade-format --state <dir> --set <name>

                Every verb accepts --log-level <trace|debug|information|warning|
                error|critical|none>, which also reads from FALLBACKPLAN_LOG_LEVEL
                when the flag is absent (ADR-0043 §6). Logs go to <state>/logs;
                `run` echoes them to the console as well.

                With no arguments the service simply starts: --archives and
                --state default to the machine's data directory —
                %ProgramData%\FallbackPlan on Windows, /var/lib/fallbackplan
                on Linux, /Library/Application Support/FallbackPlan on macOS
                (falling back to the user profile only where that cannot be
                created) — overridable by FALLBACKPLAN_ARCHIVES and
                FALLBACKPLAN_STATE. Every process of the installation shares
                the same default, so the web console and the CLI find this
                service with no path arguments either. Name the paths only
                to aim at a specific installation.

                Backup sets, their destinations and their schedules come from
                <state>/config.json. A staging set's archive lives under
                --archives as <root>/<set id>, created on the set's first backup
                (ADR-0034); a direct-ship set keeps only metadata, under
                <state>/sets/<set id> (ADR-0046). Missed runs coalesce to
                one catch-up run per set (ADR-0027 §1).

                `setup` gives a fresh installation the passphrase everything derives
                from (ADR-0044). It is for headless installs with no browser; the
                console runs the same ceremony with the warnings spelled out.
                Derivation happens here and only the sealed bundle reaches the
                service, so the passphrase is named by environment variable and
                never appears on the command line. It runs exactly once: a v2
                passphrase can never be changed, so a second attempt is refused
                rather than obeyed. --acknowledge-loss is required, because losing
                the passphrase makes every backup unrecoverable and there is no
                reset, no export and no support path. The passphrase is the whole
                recovery credential (ADR-0060): there is no kit to write, and a
                recovery needs only the passphrase and reach to an archive.

                The service never holds the passphrase (ADR-0042 §5): after
                `setup` it opens every archive with the stored write credential,
                which publishes and cannot read content back. Scheduled backups
                therefore run with nobody present and nothing to unlock.

                While it runs the service holds the writer role for <dir> exclusively,
                and listens on a local socket or named pipe there. It listens on no
                network port: the remote binding is off until explicitly enabled
                (ADR-0028 §5).

                `install` prints the definition that registers this agent with the
                operating system's service manager — a systemd unit, a launchd job,
                or the Windows `sc.exe` commands (default: this platform). It only
                prints it; nothing is changed. Run `setup` first, as the account
                the service will run as, so the credential it leaves behind is
                readable at boot (ADR-0033).

                `sync` converges declared destinations now, outside the schedule
                (ADR-0034 §3): one pass per matching (set, destination) pair,
                reported from the sync ledger. `retention` runs one pass per set
                — the report either way, tombstones, sweep and staging trim only
                with --apply (FR-GC-005). On a set-up installation --apply needs
                --passphrase-env: the service holds the key that publishes, not
                the key that authorises a deletion, and the passphrase derives
                that authority for the one run (ADR-0055).

                `reattribute` is this machine's operator re-pointing a replica a
                peer stores here at a different paired device (ADR-0053 §3) —
                for a replica attributed before its owner's claim key was
                published, which the passphrase alone cannot claim back. A
                replica that carries a claim key is refused: its owner claims it
                with the passphrase. Through the running service when one is
                listening; directly on the ledger otherwise.
                """);
            return 0;
        }

        // A bare invocation — or one that leads with options — is `run`: the
        // service starts on the installation the defaults name (FR-SVC-016).
        // A verb is for doing something specific.
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            args = ["run", .. args];
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

        // The flags override; the environment overrides the platform default;
        // a command line naming neither still names an installation
        // (FR-SVC-016). Only a DEFAULT location is created on first touch — a
        // path somebody typed is left to the verb, which reports what is
        // missing rather than inventing it.
        var archivesWereExplicit = archivesRoot is not null;
        var stateWasExplicit = stateDirectory is not null;
        try
        {
            if (archivesRoot is null)
            {
                archivesRoot = Api.InstallationDefaults.ArchivesRoot;
                Directory.CreateDirectory(archivesRoot);
            }

            if (stateDirectory is null)
            {
                stateDirectory = Api.InstallationDefaults.StateDirectory;
                Directory.CreateDirectory(stateDirectory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }

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

        if (args[0] is "unlock" or "lock")
        {
            // Retired with the passphrase-holding service (ADR-0042 §5,
            // ADR-0033 amended): a service opens archives with the credential
            // setup leaves behind, so there is nothing to store or forget.
            error.WriteLine(
                $"error: `{args[0]}` was removed — the service never holds the passphrase. Run `setup` once; "
                + "the stored write credential opens every archive afterwards (ADR-0042 §5).");
            return 1;
        }

        if (args[0] is not ("run" or "setup" or "pair" or "pairings" or "unpair" or "reattribute" or "install" or "sync" or "notices" or "receipts" or "retention" or "upgrade-format" or "verify-destination"))
        {
            error.WriteLine(
                "error: usage is `run`, `setup`, `pair`, `pairings`, `unpair`, `reattribute`, `install`, `sync`, `verify-destination`, `notices`, `receipts`, `retention`, or `upgrade-format` — no other verb exists.");
            return 1;
        }

        // What config.json asks for, if it can be read at all. A file the
        // service will go on to refuse must not also stop it logging: the
        // refusal is one of the first things worth having in the log, so a
        // configuration that does not load leaves the level to the flag, the
        // environment and the fallback, and the defect is reported through the
        // ordinary path a moment later.
        var configured = LoggingFromConfiguration(stateDirectory);

        // The level in force, resolved before any verb runs: the flag, then
        // the environment, then config.json, then Information (ADR-0043 §6). A
        // name nobody recognises is refused here rather than quietly ignored.
        if (!LoggingOptions.TryResolveLevel(
                Get("--log-level"),
                Environment.GetEnvironmentVariable(LoggingOptions.LevelVariable),
                configured?.DefaultLevel(),
                out var logLevel,
                out var levelRefusal))
        {
            error.WriteLine($"error: {levelRefusal}");
            return 1;
        }

        // One composition for the process. The sinks own a file handle and a
        // rotation policy, so a verb that built a second would be rolling the
        // same file from two places. `run` also echoes to the console, which
        // is where the service's own lines have always gone; the one-shot
        // verbs print their result and leave the file to hold the detail.
        var defaults = new LoggingOptions();
        using var logging = LoggingComposition.Create(
            new LoggingOptions
            {
                Default = logLevel,
                Categories = configured?.CategoryLevels() ?? defaults.Categories,
                Directory = LogDirectoryFor(args[0], stateDirectory),
                MaximumFileBytes = configured?.MaxFileBytes ?? defaults.MaximumFileBytes,
                RetainFiles = configured?.RetainFiles ?? defaults.RetainFiles,
                RingCapacity = configured?.RingCapacity ?? defaults.RingCapacity,
                Console = args[0] == "run",
            },
            output);

        // Said once, on standard error, and never on standard output: `install`
        // prints a service definition somebody redirects into a file, and a
        // warning in the middle of a unit file is a corrupt unit file. A log
        // that cannot be written is worth knowing about and is not worth
        // stopping for — the verb below runs either way.
        if (logging.DurableSinkRefusal is { } sinkRefusal)
        {
            error.WriteLine($"warning: {sinkRefusal}");
        }

        // `notices` lists what awaits a human, or acknowledges one entry —
        // the durable third channel (architecture 10 §3.1): a peering that
        // ended at 3 a.m. is still known at breakfast.
        if (args[0] == "notices")
        {
            return await NoticesAsync(stateDirectory, Get("--ack"), output, error, cancellationToken)
                .ConfigureAwait(false);
        }

        // `receipts` reads back the receipts filed here — deletion
        // (ADR-0063) and replication (ADR-0064): the ones this device signed
        // as a destination and the ones it verified as a commander.
        // File-direct always — the stores are append-only and this verb only
        // reads, so there is no writer to race and no reason to need the
        // service up at breakfast.
        if (args[0] == "receipts")
        {
            int? receiptLimit = null;
            if (Get("--limit") is { } limitText)
            {
                if (!int.TryParse(limitText, System.Globalization.CultureInfo.InvariantCulture, out var parsedLimit))
                {
                    error.WriteLine($"error: --limit takes a whole number, not '{limitText}'.");
                    return 1;
                }

                receiptLimit = parsedLimit;
            }

            return Receipts(
                stateDirectory, Get("--kind"), Get("--set"), Get("--repository"), args.Contains("--json"),
                receiptLimit, output, error);
        }

        // `reattribute` re-points a replica stored here (ADR-0053 §3), routed
        // like `notices`: the live service's ledger when one is listening,
        // the file when none is — never both, which is the one thing that
        // would make the override silently undone.
        if (args[0] == "reattribute")
        {
            return await ReattributeAsync(stateDirectory, Get("--repository"), Get("--to"), output, error, cancellationToken)
                .ConfigureAwait(false);
        }

        // `upgrade-format` moves one set to the latest repository format
        // (ADR-0066). Through the running service only, and deliberately so:
        // the effective format version is fixed when an archive opens, so a
        // service listening elsewhere would go on sealing the older format
        // against its cached handle while this verb reported success.
        if (args[0] == "upgrade-format")
        {
            return await UpgradeFormatAsync(stateDirectory, Get("--set"), output, error, cancellationToken)
                .ConfigureAwait(false);
        }

        // The pairing verbs need the state directory and no repository — a
        // device's peer identity and its grants live beside the state, not
        // inside the repository (ADR-0030 §1).
        if (args[0] is "pair" or "pairings" or "unpair")
        {
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

        if (repoPath is not null && !archivesWereExplicit)
        {
            // The old single-repository flag, refused with directions rather
            // than reinterpreted: --archives names a root that holds staging
            // sets' archives (ADR-0034), which is not what a --repo caller
            // was pointing at.
            error.WriteLine(
                "error: `--repo` became `--archives <root>` — the service holds a staging set's archive "
                + "under that root, at <root>/<set id> (ADR-0034). An existing single archive can be "
                + "adopted by moving it there.");
            return 1;
        }

        // `install` opens nothing: it only prints the definition that would
        // register this agent as a service (ADR-0033).
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

        if (passphraseVariable is not null && args[0] is "run" or "sync" or "verify-destination")
        {
            // Not ignored: a flag that used to mean "hold this passphrase for
            // the run" and now means nothing would let an operator believe
            // the service holds something it does not (ADR-0042 §5).
            error.WriteLine(
                $"error: `{args[0]}` takes no --passphrase-env — the service never holds the passphrase; it opens "
                + "every archive with the credential `setup` stored. The flag belongs to `setup` and to "
                + "`retention --apply`, which derive an authority from it for one run.");
            return 1;
        }

        if (passphraseVariable is not null && FromEnvironment() is null)
        {
            // An explicitly named variable that is unset is a mistake, and
            // running on without it would silently do something other than
            // what the operator asked.
            error.WriteLine(
                $"error: environment variable '{passphraseVariable}' is unset — the passphrase is passed by name, never on the command line.");
            return 1;
        }

        // Only `setup` and `retention --apply` read this: the first derives
        // the installation's credential where the person typed, the second
        // the reclaim grant for one run. A running service holds no
        // passphrase at all (ADR-0042 §5).
        var passphraseValue = FromEnvironment();

        // A one-shot verb that speaks the service surface: its own runtime
        // (taking the writer role for its duration), one command, the lines
        // printed. The expected start-up refusals — a running service holds
        // the role, an archive refuses to open, a wrong passphrase — are
        // rendered as errors here, exactly as the `run` verb renders them; an
        // unhandled stack trace is never the answer to a held lock.
        async Task<int> ServiceVerbAsync(
            Func<ServiceRuntime, Api.ServiceCommand?> commandFor,
            Func<Api.ServiceResult, IReadOnlyList<string>?> reportLines)
        {
            try
            {
                await using var verbRuntime = await ServiceRuntime.StartAsync(
                    new ServiceOptions
                    {
                        ArchivesRoot = archivesRoot!,
                        StateDirectory = stateDirectory,
                        Logging = logging,
                    },
                    cancellationToken).ConfigureAwait(false);

                // A null command is a refusal the factory already printed.
                if (commandFor(verbRuntime) is not { } command)
                {
                    return 1;
                }

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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A state directory or archives root that cannot be used — a
                // parent that is a file, a permission wall — is a stated
                // refusal, never a stack trace. Surfaced here since the paths
                // gained defaults: an unusable EXPLICIT path used to die on
                // the usage check instead of reaching the verb.
                error.WriteLine($"error: {exception.Message}");
                return 1;
            }
        }

        // On a set-up installation the service holds the key that publishes
        // and not the key that authorises a deletion (ADR-0055 §6), so
        // `retention --apply` needs a grant the way a console sends one: the
        // reclaim sub-root, re-derived from the passphrase under the
        // installation's own salt and sealed to this service's recipient key.
        // The passphrase is proved against the stored credential BEFORE the
        // grant is built — a wrong one would otherwise author tombstones
        // nothing can verify on an archive that has none yet to disagree
        // with. A dry run authors nothing and needs nothing; an installation
        // without a stored credential derives the key it already holds.
        (string? Grant, bool Refused) ReclaimGrantFor(ServiceRuntime verbRuntime, bool apply)
        {
            if (!apply)
            {
                return (null, false);
            }

            using var provisioning = new InstallationCredentialStore(stateDirectory).TryLoad();
            if (provisioning is null)
            {
                return (null, false);
            }

            if (passphraseValue is null)
            {
                error.WriteLine(
                    "error: `retention --apply` on a set-up installation needs --passphrase-env <VAR>: applying "
                    + "retention authors deletions, and this service holds the key that publishes, not the key "
                    + "that authorises a deletion. The passphrase derives that authority for this run only "
                    + "(ADR-0055).");
                return (null, true);
            }

            using var passphrase = Passphrase.Create(passphraseValue);
            using var authority = WriteOnlyDerivation.Derive(
                passphrase, provisioning.KdfParameters, provisioning.KdfSalt,
                Domain.Configuration.KdfValidationMode.OpenRepository);

            if (!authority.Credential.SealingPublicKey.SequenceEqual(provisioning.Credential.SealingPublicKey))
            {
                error.WriteLine(
                    "error: the passphrase does not reproduce this installation's credential, so it cannot "
                    + "authorise a deletion. Nothing was tombstoned.");
                return (null, true);
            }

            return (Convert.ToHexStringLower(
                WriteOnlyProvisioning.SealReclaimGrant(
                    [.. verbRuntime.GrantRecipient.PublicKey], authority.ReclaimKeySeed)), false);
        }

        // Setup speaks the same one-shot shape as the other verbs here: its
        // own runtime for the duration, then the ceremony. It therefore
        // inherits the state-directory refusal — a running service holds the
        // writer role and is named rather than fought.
        //
        // Two commands rather than one, so it cannot use ServiceVerbAsync:
        // the recipient key has to be read before there is anything to seal
        // to. That is exactly what the console does over HTTP, and doing it
        // the same way here keeps one ceremony rather than two.
        // Written directly to the store rather than through a command: this
        // process IS the service for the duration of the ceremony, so there is
        // no connection to gate and no session to hold. The gate exists to ask
        // "which person is acting" of a client, and setup has no client.
        int CreateFirstAccount(string name, string password)
        {
            var store = UserStore.Open(stateDirectory);
            if (store.HasAccounts)
            {
                error.WriteLine(
                    "error: this installation already has accounts, so setup will not add another owner. "
                    + "Use the console or the CLI to add an account (FR-USR-004).");
                return 2;
            }

            var created = store.Create(name, password, UserRole.Owner);
            if (!created.IsOk)
            {
                error.WriteLine(
                    $"error: the first account was refused ({created.Outcome}). The installation is set up; "
                    + "add the account from the console or the CLI.");
                return 2;
            }

            output.WriteLine($"owner          {created.User!.Name}");
            return 0;
        }

        async Task<int> SetupVerbAsync(string firstUser, string firstPassword)
        {
            try
            {
                await using var setupRuntime = await ServiceRuntime.StartAsync(
                    new ServiceOptions
                    {
                        ArchivesRoot = archivesRoot!,
                        StateDirectory = stateDirectory,
                        Logging = logging,
                    },
                    cancellationToken).ConfigureAwait(false);

                var handler = new ServiceCommandHandler(setupRuntime, RemoteBindingState.Off, CallerScope.Local);

                if (await handler.ExecuteAsync(new Api.DescribeServiceCommand(), cancellationToken)
                        .ConfigureAwait(false) is not Api.ServiceDescriptionResult
                        { RestoreGrantRecipient.Length: > 0 } description)
                {
                    error.WriteLine("error: this service does not publish a grant-recipient key.");
                    return 2;
                }

                string envelope;
                using (var passphrase = Passphrase.Create(passphraseValue!))
                {
                    var parameters = Domain.Configuration.RepositoryCreationSettings.Default.KdfParameters;
                    var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(
                        Repository.Crypto.KekDerivation.SaltLength);

                    // Argon2id runs here, in the process the operator started.
                    // What crosses is the sealed bundle (NFR-SEC-011).
                    using var authority = Repository.Crypto.WriteOnlyDerivation.Derive(
                        passphrase, parameters, salt, Domain.Configuration.KdfValidationMode.CreateRepository);
                    envelope = Convert.ToHexStringLower(
                        Repository.Crypto.WriteOnlyProvisioning.SealProvision(
                            Convert.FromHexString(description.RestoreGrantRecipient), authority, salt, parameters));
                }

                var result = await handler.ExecuteAsync(
                    new Api.ProvisionInstallationCommand(envelope), cancellationToken).ConfigureAwait(false);

                switch (result)
                {
                    case Api.ConfigurationChangeResult change:
                        foreach (var line in change.Lines)
                        {
                            output.WriteLine(line);
                        }

                        return CreateFirstAccount(firstUser, firstPassword);

                    case Api.ServiceError refusal:
                        error.WriteLine($"error: {refusal.Message}");
                        return 2;

                    default:
                        error.WriteLine($"error: unexpected result '{result.GetType().Name}'.");
                        return 2;
                }
            }
            catch (ClientStateException exception)
            {
                error.WriteLine($"error: {exception.Message}");
                return 1;
            }
        }

        // `setup` gives a fresh installation its passphrase (ADR-0044) for
        // headless hosts with no browser. Same ceremony as the console's:
        // derive here, seal to this service's own recipient key, send hex.
        // The passphrase is read from the named environment variable — it
        // never goes on the command line, where a process list would hold
        // the one secret that can never be changed.
        if (args[0] == "setup")
        {
            if (passphraseValue is null)
            {
                error.WriteLine(
                    "error: `setup` needs --passphrase-env <VAR> naming a set environment variable — the "
                    + "passphrase is passed by name, never on the command line.");
                return 1;
            }

            if (!args.Contains("--acknowledge-loss"))
            {
                // The acknowledgement is the ceremony, not a speed bump
                // (ADR-0042 §11, ADR-0044 §3): there is no recovery path to
                // offer later, so consent is collected before the derivation.
                error.WriteLine(
                    "error: this passphrase becomes the master key for the whole installation. It is never "
                    + "stored, it can never be changed, and if it is lost every backup is unrecoverable — "
                    + "there is no reset and no export. Re-run with --acknowledge-loss to accept this "
                    + "(ADR-0044).");
                return 1;
            }

            var assessment = Domain.Configuration.PassphraseStrength.Assess(passphraseValue);
            if (!assessment.IsAcceptable)
            {
                error.WriteLine(
                    $"error: that passphrase is too weak to be an installation's master key — it needs at "
                    + $"least {Domain.Configuration.PassphraseStrength.MinimumLength} characters including "
                    + "an uppercase letter, two digits and a special character, and more than one repeated "
                    + "unit (ADR-0044 §6).");
                return 1;
            }

            if (args.Contains("--kit-output"))
            {
                // Refused by name rather than ignored: a flag that used to
                // name where the recovery kit went would otherwise leave an
                // operator believing a kit had been written somewhere.
                error.WriteLine(
                    "error: `setup` no longer takes --kit-output. The recovery kit is withdrawn (ADR-0060): "
                    + "the passphrase is the whole recovery credential, and a recovery needs only it and "
                    + "reach to an archive. Drop the flag and run again.");
                return 1;
            }

            var firstUser = Get("--user");
            var passwordVariable = Get("--password-env");

            if (firstUser is null || passwordVariable is null)
            {
                // Setup captures the first account (FR-USR-001). A headless
                // operator has nowhere to type one later, and an installation
                // finished without an owner is one whose next caller becomes
                // its owner.
                error.WriteLine(
                    "error: `setup` needs --user <name> and --password-env <VAR>. The installation's first "
                    + "account is its owner, and it is captured here rather than left for whoever connects "
                    + "next (ADR-0045 §6, FR-USR-001).");
                return 1;
            }

            var firstPassword = Environment.GetEnvironmentVariable(passwordVariable);
            if (string.IsNullOrEmpty(firstPassword))
            {
                error.WriteLine(
                    $"error: the environment variable '{passwordVariable}' is not set. The password is "
                    + "passed by name, never on the command line (FR-USR-006).");
                return 1;
            }

            if (!Domain.Configuration.PasswordPolicy.Assess(firstPassword).IsAcceptable)
            {
                error.WriteLine(
                    $"error: that password does not meet the account policy — at least "
                    + $"{UserStore.MinimumPasswordLength} characters, with an uppercase letter, two digits "
                    + "and a special character (ADR-0045).");
                return 1;
            }

            return await SetupVerbAsync(firstUser, firstPassword).ConfigureAwait(false);
        }

        // `retention [--apply]` runs one pass per configured set
        // (architecture 07): the mandatory dry-run report either way,
        // tombstones, sweep and trim only with --apply — through the same
        // service surface a console uses, so the writer lane serialises it
        // against anything else that writes (FR-GC-005/008).
        if (args[0] == "retention")
        {
            var apply = args.Contains("--apply");
            return await ServiceVerbAsync(
                verbRuntime => ReclaimGrantFor(verbRuntime, apply) is var (grant, refused) && !refused
                    ? new Api.RetentionCommand(apply, grant)
                    : null,
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
                _ => new Api.VerifyDestinationCommand(
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
                _ => new Api.SyncCommand(Get("--set"), Get("--destination")),
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
            Logging = logging,
        };

        try
        {
        // The recycle loop (ADR-0049): restart_service asks the host to tear
        // the runtime down and start it again in the same process — the same
        // outcome on every platform, whatever the service manager's restart
        // policy would make of an exit. Each iteration owns its own lifetime
        // token; only the operator's restart re-enters the loop, and the
        // caller's cancellation still means stop.
        while (true)
        {
            var restartRequested = false;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            void RequestRestart()
            {
                restartRequested = true;

                // The acknowledgement must reach the wire before the
                // listener dies under it; the grace period is what lets the
                // pump flush the reply it is writing right now. Deliberately
                // no token: this delay must run even as the lifetime it is
                // about to cancel winds down.
                _ = Task.Run(
                    async () =>
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None)
                            .ConfigureAwait(false);
                        try
                        {
                            await lifetime.CancelAsync().ConfigureAwait(false);
                        }
                        catch (ObjectDisposedException)
                        {
                            // The iteration already ended for another reason.
                        }
                    },
                    CancellationToken.None);
            }

            try
            {
            await using var runtime = await ServiceRuntime.StartAsync(options, lifetime.Token)
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
                        log: logging.Factory.CreateLogger<RemoteServiceListener>(),
                        replicationStateDirectory: stateDirectory,
                        owners: runtime.ReplicaOwners);
                    bindingState = RemoteBindingState.On(remoteListener.Endpoint.ToString());
                }

                // The command surface comes up before the first pass, so a client
                // that starts alongside the service is not told "nothing is
                // listening" while a ten-hour backup runs. The binding state was
                // seeded from the remote listener's bound endpoint above.
                // One handler per listener over the same runtime, so each
                // knows where its caller came from. RemoteBindingState says
                // only whether the remote binding is on; a verb that must be
                // refused to a remote console needs to know that THIS caller
                // is remote (ADR-0044 §5).
                // The local handler carries the recycle signal; the remote one
                // deliberately does not — restart is refused to remote scope
                // by name, and a null callback is defence in depth behind
                // that refusal. --once has no host loop to re-enter.
                var localHandler = new ServiceCommandHandler(
                    runtime, bindingState, CallerScope.Local, once ? null : RequestRestart);
                var remoteHandler = new ServiceCommandHandler(runtime, bindingState, CallerScope.Remote);

                // One account store and one session registry for the whole
                // installation; one decorator per accepted connection, because
                // "which person is acting" is the one piece of state that is
                // genuinely per connection (ADR-0045 §5). Sessions live here
                // and nowhere else, which is why stopping this process is what
                // signs everybody out.
                var users = UserStore.Open(stateDirectory);
                var sessions = new SessionRegistry();
                var authLog = logging.Factory.CreateLogger<AuthenticatingService>();

                // The remote socket bound before the handler existed so its
                // endpoint could seed the binding state; it begins serving now
                // that the handler exists.
                remoteListener?.Bind(() => new AuthenticatingService(remoteHandler, users, sessions, authLog));

                await using var localListener = LocalServiceListener.Start(
                    () => new AuthenticatingService(localHandler, users, sessions, authLog),
                    stateDirectory,
                    logging.Factory.CreateLogger<LocalServiceListener>());

                // Logged as well as printed, and the duplication is deliberate.
                // Installed as a service there is no console to print to, and
                // "what came up, and when" is the first thing anybody reading a
                // support log needs. The printed lines stay because a
                // foreground run is somebody waiting to see it start.
                var hostLog = logging.Factory.CreateLogger(typeof(AgentHost).FullName!);
                Log.LocalBindingUp(hostLog);

                // The startup configuration record (FR-SVC-010; ADR-0049):
                // what was RESOLVED, provenance included — the first thing a
                // diagnostics read needs is what this service was actually
                // operating against. Formatted into locals inside the guard:
                // CA1873 is right that argument expressions are evaluated
                // whether or not anybody is listening.
                if (hostLog.IsEnabled(LogLevel.Information))
                {
                    var stateProvenance = Provenance(
                        stateWasExplicit, Api.InstallationDefaults.StateVariable, stateDirectory);
                    var archivesProvenance = Provenance(
                        archivesWereExplicit, Api.InstallationDefaults.ArchivesVariable, archivesRoot!);
                    var poolWidth = ServiceRuntime.ConfiguredBackupPoolWidth(options);
                    var remoteBound = remoteListener is null ? "off" : remoteListener.Endpoint.ToString();
                    Log.StartupLocations(hostLog, stateDirectory, stateProvenance, archivesRoot!, archivesProvenance);
                    Log.StartupPosture(hostLog, pollSeconds, poolWidth, remoteBound);
                    foreach (var set in runtime.Configuration.BackupSets)
                    {
                        var schedule = set.Schedule ?? "manual-only";
                        var priority = set.Priority?.ToString(CultureInfo.InvariantCulture) ?? "none";
                        Log.StartupSet(
                            hostLog, set.Name, set.Roots.Count, schedule,
                            set.Destinations.Count, set.DirectShip, priority);
                    }

                    foreach (var declared in runtime.Configuration.Destinations)
                    {
                        var kind = declared.Kind.ToString();
                        var domain = declared.FailureDomain?.ToString() ?? "unstated";
                        Log.StartupDestination(hostLog, declared.Name, kind, domain);
                    }
                }
                if (remoteListener is not null)
                {
                    var boundTo = remoteListener.Endpoint.ToString();
                    Log.RemoteBindingUp(hostLog, boundTo, peerKeypair!.Identity.Fingerprint);
                }

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
            while (!lifetime.IsCancellationRequested)
            {
                var result = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, lifetime.Token)
                    .ConfigureAwait(false);

                foreach (var set in result.Sets)
                {
                    output.WriteLine(
                        $"{DateTimeOffset.Now:u}  {set.SetName,-20} {set.Outcome}{(set.Detail is null ? "" : "  " + set.Detail)}");
                }

                failed = result.Failed;
                if (once)
                {
                    // --once means once, whole: the transfer phases the
                    // service would leave running are awaited, because the
                    // runtime — and every queued job — is torn down on return.
                    await result.Transfers.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    return failed == 0 ? 0 : 2;
                }

                // Deliberately NOT awaiting result.Transfers (ADR-0047): the
                // loop ticks on its interval whatever the transfer lane is
                // doing, so due-ness keeps being evaluated during an
                // hours-long copy. The stable per-pair job identities keep
                // un-awaited passes from piling transfers up.
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), lifetime.Token).ConfigureAwait(false);
            }

            // A cancelled lifetime never returns here quietly: the throw is
            // what routes a restart back into the recycle loop and a real
            // stop out to the clean-shutdown catch below.
            lifetime.Token.ThrowIfCancellationRequested();
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
            catch (OperationCanceledException) when (restartRequested && !cancellationToken.IsCancellationRequested)
            {
                // The teardown above ran whole — listeners, runtime, writer
                // role — so the next iteration reacquires cleanly.
                output.WriteLine($"{DateTimeOffset.Now:u}  restarting at an operator's request");
            }
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unusable state directory or archives root is a stated
            // refusal, never a stack trace — same mapping as the one-shot
            // verbs above.
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// How a directory was chosen (FR-SVC-016's precedence), for the startup
    /// configuration record: the flag, the environment variable, or which
    /// default the resolution landed on.
    /// </summary>
    private static string Provenance(bool explicitFlag, string environmentVariable, string resolved)
    {
        if (explicitFlag)
        {
            return "named by flag";
        }

        if (Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 })
        {
            return $"from {environmentVariable}";
        }

        return resolved.StartsWith(Api.InstallationDefaults.MachineRoot, StringComparison.Ordinal)
            ? "machine-wide default"
            : "profile fallback";
    }

    /// <summary>
    /// Where this verb's records belong on disk, or null for the one verb that
    /// has no business creating anything.
    /// </summary>
    /// <param name="verb">The verb about to run.</param>
    /// <param name="stateDirectory">The state directory named on the command line, if one was.</param>
    /// <remarks>
    /// <c>install</c> prints a service definition and touches nothing, and the
    /// state directory it names is the one the service will use <em>once the
    /// account exists</em>. Creating a log directory there would be the wrong
    /// act by whoever is running it: an operator with enough privilege to
    /// register a service leaves behind a <c>logs</c> directory owned by
    /// themselves, in the place the service account is about to be told to
    /// write. Records still reach the ring, and this verb produces its answer
    /// on the two streams either way.
    /// </remarks>
    private static string? LogDirectoryFor(string verb, string? stateDirectory) =>
        stateDirectory is null || verb == "install" ? null : Path.Combine(stateDirectory, "logs");

    /// <summary>
    /// The <c>logging</c> block from <c>&lt;state&gt;/config.json</c>, or null
    /// when there is no readable configuration to ask.
    /// </summary>
    /// <param name="stateDirectory">The state directory holding the configuration.</param>
    /// <remarks>
    /// Deliberately silent about failure. This runs before there is anywhere to
    /// report to, and every way the file can be wrong — missing, malformed,
    /// a version this build does not read — is reported properly by the verb
    /// that follows. Swallowing it here only decides what to log at; it never
    /// decides whether the configuration is acceptable.
    /// </remarks>
    private static LoggingConfiguration? LoggingFromConfiguration(string stateDirectory)
    {
        try
        {
            return ClientConfiguration.Load(Path.Combine(stateDirectory, "config.json")).Logging;
        }
        catch (ClientStateException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
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

    private static async Task<int> UpgradeFormatAsync(
        string stateDirectory,
        string? setName,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(setName))
        {
            error.WriteLine("error: usage is `upgrade-format --state <dir> --set <name>`.");
            return 1;
        }

        ServiceResult result;
        try
        {
            await using var client = await LocalServiceClient.ConnectAsync(
                stateDirectory, "fallbackplan-agent", cancellationToken).ConfigureAwait(false);
            result = await client.ExecuteAsync(new UpgradeSetFormatCommand(setName), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ServiceConnectionException)
        {
            // No fallback to the files, unlike `reattribute`. Writing the
            // record here would be safe only if nothing held the archive
            // open, and this verb cannot tell "no service" from "a service
            // this socket did not reach" — the second leaves a set sealing
            // the old format with a record saying otherwise.
            error.WriteLine(
                "error: no service is listening on this state directory, and the format upgrade takes effect "
                + "through the running service — it drops the set's open archive so the next backup seals the "
                + "newer format. Start the service and run this verb again.");
            return 1;
        }

        switch (result)
        {
            case ConfigurationChangeResult changed:
                foreach (var line in changed.Lines)
                {
                    output.WriteLine(line);
                }

                return 0;

            case ServiceError refusal:
                error.WriteLine($"error: {refusal.Message}");
                return 1;

            default:
                error.WriteLine($"error: the service answered a format upgrade with {result.GetType().Name}.");
                return 1;
        }
    }

    private static async Task<int> ReattributeAsync(
        string stateDirectory,
        string? repositoryId,
        string? to,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryId) || string.IsNullOrWhiteSpace(to))
        {
            error.WriteLine("error: usage is `reattribute --state <dir> --repository <hex> --to <fingerprint>`.");
            return 1;
        }

        ServiceResult result;
        try
        {
            // The running service's ledger is the one its listener serves
            // from (ADR-0053 §3): a second writer on replica-owners.json
            // beside it would move the file while the live gate went on
            // refusing from what it read at start — and the next offer the
            // service recorded would write the override away again.
            await using var client = await LocalServiceClient.ConnectAsync(
                stateDirectory, "fallbackplan-agent", cancellationToken).ConfigureAwait(false);
            result = await client.ExecuteAsync(new ReattributeReplicaCommand(repositoryId, to), cancellationToken)
                .ConfigureAwait(false);

            if (result is ServiceError { Reason: ServiceErrorReason.Refused } gate
                && gate.Message.Contains("signed in", StringComparison.Ordinal))
            {
                // The service's socket wants a signed-in owner and this verb
                // carries no session. Said plainly, with the two honest ways
                // on — and never the file, which is the race above.
                error.WriteLine($"error: {gate.Message}");
                error.WriteLine(
                    "The running service answers this verb only to a signed-in owner. Re-point the replica from "
                    + "the console (Pairings → Replicas stored here), or stop the service and run this verb again: "
                    + "with no service listening it edits the ledger directly.");
                return 1;
            }
        }
        catch (ServiceConnectionException)
        {
            // No service holds the state directory; the ledger is ours to touch.
            result = ReplicaReattribution.Apply(
                FallbackPlan.Application.ReplicaOwnerStore.Open(stateDirectory),
                PeerGrantStore.Open(stateDirectory),
                FallbackPlan.Application.NoticeStore.Open(stateDirectory),
                repositoryId,
                to,
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        switch (result)
        {
            case ConfigurationChangeResult changed:
                foreach (var line in changed.Lines)
                {
                    output.WriteLine(line);
                }

                return 0;

            case ServiceError refusal:
                error.WriteLine($"error: {refusal.Message}");
                return 1;

            default:
                error.WriteLine($"error: the service answered a re-attribution with {result.GetType().Name}.");
                return 1;
        }
    }

    private static int Receipts(
        string stateDirectory, string? kind, string? set, string? repository, bool json, int? limit,
        TextWriter output, TextWriter error)
    {
        // A mistyped path holds nothing, and "no receipts" for it would be
        // the one answer this verb must never give by accident.
        if (!Directory.Exists(stateDirectory))
        {
            error.WriteLine($"error: no state directory at '{stateDirectory}' — nothing has been filed there.");
            return 1;
        }

        if (kind is not null && kind is not (DeletionReceiptStore.Kind or ReplicationReceiptStore.Kind))
        {
            error.WriteLine(
                $"error: --kind takes '{DeletionReceiptStore.Kind}' or '{ReplicationReceiptStore.Kind}', not '{kind}'.");
            return 1;
        }

        string? repositoryIdHex = null;
        if (repository is not null)
        {
            if (!DeletionReceiptReport.TryParseRepositoryId(repository, out var parsed))
            {
                error.WriteLine("error: --repository takes the repository id as 32 hex digits.");
                return 1;
            }

            repositoryIdHex = parsed;
        }

        if (limit is <= 0)
        {
            error.WriteLine("error: --limit must be at least 1.");
            return 1;
        }

        // No limit reads everything, which is what an operator reading their
        // own audit trail asked for; a limit bounds the reading and not only
        // the printing.
        IReadOnlyList<FiledDeletionReceipt> deletions = kind == ReplicationReceiptStore.Kind
            ? []
            : DeletionReceiptStore.Open(stateDirectory).List(repositoryIdHex, limit);
        IReadOnlyList<FiledReplicationReceipt> replications = kind == DeletionReceiptStore.Kind
            ? []
            : ReplicationReceiptStore.Open(stateDirectory).List(repositoryIdHex, limit);
        if (set is not null)
        {
            deletions = [.. deletions.Where(filed => string.Equals(filed.Set, set, StringComparison.Ordinal))];
            replications = [.. replications.Where(filed => string.Equals(filed.Set, set, StringComparison.Ordinal))];
        }

        if (json)
        {
            output.WriteLine(ReceiptReport.ToJson(deletions, replications));
        }
        else
        {
            ReceiptReport.Write(output, deletions, replications, kind);
        }

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
            "# First, run `setup` once as the SAME account the service runs as, so the write credential it "
            + "stores is readable at boot with nobody present (ADR-0042 §5, ADR-0044):");
        error.WriteLine(
            $"#   \"{executablePath}\" setup --archives \"{options.ArchivesRoot}\" "
            + $"--state \"{options.StateDirectory}\" --passphrase-env <VAR> --acknowledge-loss "
            + "--user <name> --password-env <VAR>");
        return 0;
    }

    private static string DefaultTarget() =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "launchd"
        : "systemd";

}
