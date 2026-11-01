using Bodu;
using System.Globalization;
using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using RestoreResult = FallbackPlan.Api.RestoreResult;
using FallbackPlan.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Cli;

/// <summary>What a backup was asked to capture.</summary>
/// <remarks>
/// A configured set and an ad-hoc root are not interchangeable. A service knows
/// only what its configuration names, so <see cref="Root"/> is a direct-mode
/// capability — see <see cref="IOperationGateway.RunBackupAsync"/>.
/// </remarks>
public sealed record BackupRequest
{
    /// <summary>The configured set to run, or null for an ad-hoc root.</summary>
    public string? SetName { get; init; }

    /// <summary>The directory to capture when no set was named.</summary>
    public string? Root { get; init; }

    /// <summary>rules-v1 include rules, for an ad-hoc root.</summary>
    public IReadOnlyList<string> IncludeRules { get; init; } = [];

    /// <summary>rules-v1 exclude rules, for an ad-hoc root.</summary>
    public IReadOnlyList<string> ExcludeRules { get; init; } = [];

    /// <summary>Whether to ignore the prior snapshot and re-read everything.</summary>
    public bool Full { get; init; }
}

/// <summary>What an operation did, and what to tell the operator about it.</summary>
/// <param name="Ok">Whether it succeeded outright; false sets a non-zero exit code.</param>
/// <param name="Lines">
/// The lines to print. The two gateways genuinely know different things — direct
/// mode holds the published snapshot or the failing blob in hand, a client holds
/// what the contract carries back — so each renders what it observed rather than
/// padding a shared shape with values it would have to invent.
/// </param>
public sealed record OperationReport(bool Ok, IReadOnlyList<string> Lines);

/// <summary>
/// Where a write command's work happens (ADR-0028 §3): a running service, or
/// this process in direct mode.
/// </summary>
/// <remarks>
/// The choice is resolved once, before any work, by
/// <see cref="OperationGateway.OpenForWriteAsync"/> — and it is never silent.
/// "Did my backup run against the same state the service uses" is a question an
/// operator must not have to guess at, so both modes announce themselves.
/// </remarks>
public interface IOperationGateway : IAsyncDisposable
{
    /// <summary>The mode line for the operator.</summary>
    string Mode { get; }

    /// <summary>Runs a backup wherever this gateway's work happens, and waits for it.</summary>
    /// <param name="request">What to capture.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="CliFailureException">The request is not one this gateway can serve.</exception>
    ValueTask<OperationReport> RunBackupAsync(BackupRequest request, CancellationToken cancellationToken);

    /// <summary>Verifies stored blobs at the given level.</summary>
    /// <param name="level">One of <c>locator</c>, <c>digest</c>, <c>records</c>.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>What it found.</returns>
    ValueTask<OperationReport> VerifyAsync(string level, CancellationToken cancellationToken);

    /// <summary>Reports repository health at the given verification level.</summary>
    /// <param name="level">One of <c>locator</c>, <c>digest</c>, <c>records</c>.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>What it found.</returns>
    ValueTask<OperationReport> CheckAsync(string level, CancellationToken cancellationToken);

    /// <summary>Restores a snapshot, or a path within it.</summary>
    /// <param name="request">What to restore and where.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>What it wrote.</returns>
    ValueTask<OperationReport> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Plans the restore <see cref="RestoreAsync"/> would run, and restores
    /// nothing (FR-RST-003, ADR-0107).
    /// </summary>
    /// <param name="request">What the run would restore and where.</param>
    /// <param name="cancellationToken">Cancels the plan.</param>
    /// <returns>The plan, a success only where the run would restore every file and fit.</returns>
    ValueTask<OperationReport> PlanRestoreAsync(RestoreRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Converges destinations now, outside the schedule (FR-DEST-002). Only a
    /// service can serve this — the fan-out needs its scheduler, ledger and
    /// per-set archives — so direct mode refuses with directions.
    /// </summary>
    /// <param name="setName">The set to sync, or null for every configured set.</param>
    /// <param name="destinationName">The destination to sync, or null for each set's every destination.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Where each pair stands, from the refreshed ledger.</returns>
    ValueTask<OperationReport> SyncAsync(string? setName, string? destinationName, CancellationToken cancellationToken);

    /// <summary>
    /// Reports what changed under a set's source since its last backup —
    /// new, updated, moved, deleted, and files the rules no longer include
    /// (ADR-0038, FR-SVC-009). Only a service can serve this — the rescan
    /// reads the set's catalogue under the service's runtime — so direct
    /// mode refuses with directions.
    /// </summary>
    /// <param name="setName">The set to compare, or null for the default set.</param>
    /// <param name="sampleLimit">The most paths listed per bucket; null takes the service default.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The comparison, one line per finding.</returns>
    ValueTask<OperationReport> PreviewSetChangesAsync(
        string? setName, int? sampleLimit, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a retention pass per configured set (architecture 07). Only a
    /// service can serve this — retention needs the whole configuration, the
    /// sync ledger and the writer role — so direct mode refuses with
    /// directions.
    /// </summary>
    /// <param name="apply">False reports only; true tombstones, sweeps and trims (FR-GC-005).</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The report, per set in configuration order.</returns>
    ValueTask<OperationReport> RetentionAsync(bool apply, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes snapshots of one set from staging and every copy (FR-GC-013),
    /// or says what it would delete and where each is held. Only a service can
    /// serve this: a deletion has to reach every copy the hub replicates to,
    /// and only the hub knows them.
    /// </summary>
    /// <param name="setName">The set, by name or id.</param>
    /// <param name="snapshotIds">The snapshots, as hex ids.</param>
    /// <param name="apply">False reports only; true requests and carries out the deletion.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Each snapshot's state, then the pass's report.</returns>
    ValueTask<OperationReport> DeleteSnapshotsAsync(
        string setName, IReadOnlyList<string> snapshotIds, bool apply, CancellationToken cancellationToken);

    /// <summary>
    /// Re-reads a destination's stored objects and confirms they still match
    /// what was sealed (FR-VER-002, FR-VER-004).
    /// </summary>
    /// <param name="setName">The set to verify; null takes every configured set.</param>
    /// <param name="destinationName">The destination to verify; null takes each set's every destination.</param>
    /// <param name="full">Whether to keep going until every object has been read.</param>
    /// <param name="probe">
    /// Whether to read nothing and confirm only that the destination could
    /// take a backup — the depth that answers before the first sync.
    /// </param>
    /// <param name="cancellationToken">Cancels the verification.</param>
    ValueTask<OperationReport> VerifyDestinationAsync(
        string? setName, string? destinationName, bool full, bool probe, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the restore drill of each matching (set, destination) pair now
    /// (FR-DRL-003). Only a service can serve this: the drill's answer is
    /// recorded on its ledger and raises or clears its notice, so direct mode
    /// refuses with directions.
    /// </summary>
    /// <param name="setName">The set to drill; null takes every configured set.</param>
    /// <param name="destinationName">The destination to drill; null takes each set's every destination.</param>
    /// <param name="cancellationToken">Ends the wait; the drill itself is the service's and finishes.</param>
    /// <returns>A line per pair; not ok unless every pair asked about was drilled and restored.</returns>
    ValueTask<OperationReport> DrillAsync(string? setName, string? destinationName, CancellationToken cancellationToken);

    /// <summary>
    /// Names the backup's files a notice left out (FR-WOR-007, ADR-0089
    /// Amendment 1), through a source of the notice's set the passphrase
    /// unlocked. Only a service can serve this: the notices are its ledger.
    /// </summary>
    /// <param name="noticeId">The notice, by the identifier the listing gives it.</param>
    /// <param name="cancellationToken">Cancels the look.</param>
    /// <returns>The names, one a line, or that the notice names no files.</returns>
    ValueTask<OperationReport> NoticeNamesAsync(string noticeId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the restore runs the service began and has not seen finish
    /// (FR-RST-008, ADR-0115), without the backup's file names. Only a
    /// service names a run.
    /// </summary>
    /// <param name="cancellationToken">Cancels the look.</param>
    /// <returns>A line per run, or that none is left.</returns>
    ValueTask<OperationReport> RestoreRunsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resumes a restore run left unfinished by its name alone (FR-RST-008,
    /// ADR-0115): the service runs it again as it was asked, through a source
    /// of its snapshot the passphrase unlocked.
    /// </summary>
    /// <param name="run">The run, as the list names it.</param>
    /// <param name="ignoreFreeSpace">Whether to restore although the volume looks too small.</param>
    /// <param name="acknowledgeWiderAccess">
    /// Whether to restore although a folder would open a file wider than it
    /// was (ADR-0178 Amendment 1): the run's own acknowledgement is not kept.
    /// </param>
    /// <param name="cancellationToken">Cancels the resume.</param>
    /// <returns>What the restore says; not ok when a file failed.</returns>
    ValueTask<OperationReport> ResumeRestoreAsync(
        string run, bool ignoreFreeSpace, bool acknowledgeWiderAccess, CancellationToken cancellationToken);

    /// <summary>
    /// Plans the resume of a restore run left unfinished, by its name alone
    /// (FR-RST-008, ADR-0120): the service plans the run as it was asked, as
    /// the resume will meet it, and restores nothing.
    /// </summary>
    /// <param name="run">The run, as the list names it.</param>
    /// <param name="cancellationToken">Cancels the plan.</param>
    /// <returns>The plan's lines; not ok unless the resume would bring back every file and fit.</returns>
    ValueTask<OperationReport> PlanResumeRestoreAsync(string run, CancellationToken cancellationToken);

    /// <summary>Lets a restore run left unfinished go (ADR-0115): the service names it no more.</summary>
    /// <param name="run">The run, as the list names it.</param>
    /// <param name="cancellationToken">Cancels the ask.</param>
    /// <returns>A line saying so.</returns>
    ValueTask<OperationReport> ForgetRestoreRunAsync(string run, CancellationToken cancellationToken);

    /// <summary>
    /// Lets every restore run left unfinished go at once (ADR-0162), or only
    /// those that began more than <paramref name="olderThanDays"/> days ago
    /// (ADR-0165): each the service lists is let go by its name, as one is.
    /// </summary>
    /// <param name="olderThanDays">The days a run must have begun longer ago than to go, or null for every one.</param>
    /// <param name="cancellationToken">Cancels the ask.</param>
    /// <returns>A line per run and how many; not ok where the service let one not go.</returns>
    ValueTask<OperationReport> ForgetRestoreRunsAsync(int? olderThanDays, CancellationToken cancellationToken);
}

/// <summary>What a restore was asked to write, and where.</summary>
/// <param name="SnapshotId">The snapshot, hex-encoded.</param>
/// <param name="Path">The subtree to restore, or null for the whole snapshot.</param>
/// <param name="OutputDirectory">
/// Where to write. In service mode this is a path on the machine running the
/// service (ADR-0028 §6) — the same machine, on the local binding.
/// </param>
/// <param name="IgnoreFreeSpace">
/// Whether to restore although the volume looks too small (FR-RST-003). A
/// restore that will not fit is otherwise refused before anything is written.
/// </param>
/// <param name="PlanExport">
/// Where a plan of the restore is kept as JSON, on the machine running the
/// command line (ADR-0108); null keeps none. A file already there is refused.
/// </param>
/// <param name="Resume">
/// Whether this restore resumes one cut short (FR-RST-008, ADR-0109): each
/// file already holding the snapshot's content where the run lands it is left
/// as it stands, and the rest are restored.
/// </param>
/// <param name="ResumedRun">
/// The run resumed, as the folder it landed in under the quarantine is named.
/// Through the service each run lands in a folder of its own, so a resume
/// names one; in direct mode a run lands in place, and names none.
/// </param>
/// <param name="AsPlanned">
/// The kept plan this restore is held to (FR-RST-003, ADR-0114): a plan of it
/// is compared with the kept one, and the restore runs only where nothing
/// changed. Null holds it to none.
/// </param>
/// <param name="Paths">
/// Several subtrees in one run (ADR-0117), which win over <paramref name="Path"/>;
/// null where one or none is asked.
/// </param>
/// <param name="Target">
/// <c>original</c> lands each file where it was captured, which only the
/// service knows; null or <c>folder</c> lands it under <paramref name="OutputDirectory"/>.
/// </param>
/// <param name="Existing">
/// What to do about a file in the way: null moves it aside, <c>rename</c>
/// writes the restored copy beside it, <c>overwrite</c> replaces it.
/// </param>
/// <param name="InPlace">
/// Whether a run through the service lands directly where it points rather
/// than in a quarantine folder of its own. A run in direct mode lands in place
/// whether asked or not.
/// </param>
/// <param name="AcknowledgeWiderAccess">
/// Whether to restore although a folder it writes to would open a file wider
/// than it was (FR-RST-003, ADR-0178 Amendment 1). Such a restore is
/// otherwise refused before anything is written.
/// </param>
public sealed record RestoreRequest(
    string SnapshotId,
    string? Path,
    string OutputDirectory,
    bool IgnoreFreeSpace = false,
    string? PlanExport = null,
    bool Resume = false,
    string? ResumedRun = null,
    KeptPlan? AsPlanned = null,
    IReadOnlyList<string>? Paths = null,
    string? Target = null,
    string? Existing = null,
    bool InPlace = false,
    bool AcknowledgeWiderAccess = false)
{
    /// <summary>Whether the run lands each file where it was captured.</summary>
    public bool ToOriginal => string.Equals(Target, "original", StringComparison.Ordinal);
}

/// <summary>A kept plan a restore is held to (ADR-0114), and the file it was read from.</summary>
/// <param name="File">The file, as the person named it.</param>
/// <param name="Plan">What the file holds.</param>
public sealed record KeptPlan(string File, RestorePlanFile.Kept Plan);

/// <summary>Resolves which gateway a command gets.</summary>
public static class OperationGateway
{
    /// <summary>
    /// Opens the gateway a write command needs: the service when one is
    /// listening, this process otherwise.
    /// </summary>
    /// <param name="repoPath">The repository.</param>
    /// <param name="passphraseEnvironmentVariable">The variable naming the passphrase.</param>
    /// <param name="stateDirectory">The state directory, or null for the default.</param>
    /// <param name="forceDirect">
    /// Whether <c>--direct</c> was given. Forcing direct does not force the
    /// writer role free: if a service holds it this is refused naming the
    /// holder, because the alternative is two processes writing as one writer.
    /// </param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <param name="logger">Where what the open noticed is recorded.</param>
    /// <returns>The gateway; dispose to release whatever it holds.</returns>
    public static async ValueTask<IOperationGateway> OpenForWriteAsync(
        string repoPath,
        string passphraseEnvironmentVariable,
        string? stateDirectory,
        bool forceDirect,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        // The session opens without the role first, because the default state
        // directory is derived from the repository id — so the address a client
        // would connect to is not knowable until the repository is open.
        var session = await CliSession.OpenAsync(
            repoPath, passphraseEnvironmentVariable, stateDirectory, cancellationToken, logger).ConfigureAwait(false);

        try
        {
            if (!forceDirect)
            {
                // A socket that accepts is the only proof of life that matters;
                // there is no separate liveness protocol to consult.
                LocalServiceClient? client = null;
                try
                {
                    client = await LocalServiceClient.ConnectAsync(
                        session.StateDirectory, "fallbackplan-cli", cancellationToken).ConfigureAwait(false);

                    // Presented as the first thing on a fresh connection: the
                    // service holds the session, this end holds only the token,
                    // and a connection that never presents one is refused by
                    // name a moment later (ADR-0045 §5).
                    await new SessionCache(session.StateDirectory)
                        .PresentAsync(client, cancellationToken).ConfigureAwait(false);
                }
                catch (ServiceConnectionException)
                {
                    // Nothing is listening. Direct mode below, which is the
                    // ordinary case on a machine with no service installed.
                }

                if (client is not null)
                {
                    var address = session.StateDirectory;
                    session.Dispose();
                    return new ServiceGateway(client, LocalMode(address), client, passphraseEnvironmentVariable);
                }
            }

            session.TakeWriterRole();
            return new DirectGateway(session, logger, stateNamed: stateDirectory is not null);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the gateway a read command needs: the service when one is
    /// listening, this process otherwise — and without the writer role either
    /// way.
    /// </summary>
    /// <param name="repoPath">The repository.</param>
    /// <param name="passphraseEnvironmentVariable">The variable naming the passphrase.</param>
    /// <param name="stateDirectory">The state directory, or null for the default.</param>
    /// <param name="forceDirect">Whether <c>--direct</c> was given.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <param name="logger">Where what the open noticed is recorded.</param>
    /// <returns>The gateway; dispose to release whatever it holds.</returns>
    /// <remarks>
    /// A read path never takes the writer role, so unlike the write gateway
    /// this one is never refused for holding it. Routing to a running service
    /// is still worth doing — one process then owns the reads, against one
    /// catalogue connection, bounded by the queue rather than by luck — but
    /// falling back to reading directly is correct rather than a concession.
    /// </remarks>
    public static async ValueTask<IOperationGateway> OpenForReadAsync(
        string repoPath,
        string passphraseEnvironmentVariable,
        string? stateDirectory,
        bool forceDirect,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var session = await CliSession.OpenAsync(
            repoPath, passphraseEnvironmentVariable, stateDirectory, cancellationToken, logger).ConfigureAwait(false);

        try
        {
            if (!forceDirect)
            {
                LocalServiceClient? client = null;
                try
                {
                    client = await LocalServiceClient.ConnectAsync(
                        session.StateDirectory, "fallbackplan-cli", cancellationToken).ConfigureAwait(false);

                    // Presented as the first thing on a fresh connection: the
                    // service holds the session, this end holds only the token,
                    // and a connection that never presents one is refused by
                    // name a moment later (ADR-0045 §5).
                    await new SessionCache(session.StateDirectory)
                        .PresentAsync(client, cancellationToken).ConfigureAwait(false);
                }
                catch (ServiceConnectionException)
                {
                    // Nothing listening; read it here.
                }

                if (client is not null)
                {
                    var address = session.StateDirectory;
                    session.Dispose();
                    return new ServiceGateway(client, LocalMode(address), client, passphraseEnvironmentVariable);
                }
            }

            return new DirectGateway(session, logger, stateNamed: stateDirectory is not null);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the gateway for a command that named no repository: the running
    /// service holding <paramref name="stateDirectory"/> — by default the
    /// machine's shared installation state, the same one a bare service start
    /// serves (FR-SVC-016) — exactly as the web console connects. There is no
    /// direct fallback here, deliberately: direct mode IS the repository, so
    /// without <c>--repo</c> the only honest answer when nothing listens is a
    /// stated refusal with directions, never a guess at which archive was
    /// meant.
    /// </summary>
    /// <param name="stateDirectory">The state directory, or null for the shared default.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <param name="passphraseEnvironmentVariable">
    /// The variable naming the passphrase, when one was given: what a restore
    /// on a set-up installation derives its grant from (ADR-0042 §5), and an
    /// applied retention pass its reclaim grants (ADR-0055 §6).
    /// </param>
    /// <returns>The gateway; dispose to close the connection.</returns>
    public static async ValueTask<IOperationGateway> OpenServiceOnlyAsync(
        string? stateDirectory, CancellationToken cancellationToken, string? passphraseEnvironmentVariable = null)
    {
        var state = stateDirectory is { Length: > 0 } ? stateDirectory : InstallationDefaults.StateDirectory;

        LocalServiceClient client;
        try
        {
            client = await LocalServiceClient.ConnectAsync(state, "fallbackplan-cli", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ServiceConnectionException)
        {
            throw new CliFailureException(
                $"no service is listening for '{state}'. Start one (`fallbackplan-agent`), or name --repo to "
                + "work on a repository directly.");
        }

        try
        {
            await new SessionCache(state).PresentAsync(client, cancellationToken).ConfigureAwait(false);
            return new ServiceGateway(client, LocalMode(state), client, passphraseEnvironmentVariable);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Opens the gateway a command needs against a <em>remote</em> paired service
    /// (ADR-0028 §5): dial the endpoint, authenticate as the pinned peer, and
    /// carry the command contract over the opened session.
    /// </summary>
    /// <param name="host">The service's host.</param>
    /// <param name="port">The service's remote-binding port.</param>
    /// <param name="stateDirectory">The console's state directory (its peer identity and pairings).</param>
    /// <param name="fingerprint">The fingerprint of the pinned service to expect.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <param name="passphraseEnvironmentVariable">
    /// The variable naming the passphrase, when one was given: what a restore
    /// on a set-up installation derives its grant from (ADR-0042 §5), and an
    /// applied retention pass its reclaim grants (ADR-0055 §6).
    /// </param>
    /// <returns>The gateway; dispose to close the session and release the device key.</returns>
    /// <remarks>
    /// There is no direct-mode fallback here, deliberately: a remote console does
    /// not hold the repository, so if the paired service cannot be reached the
    /// only honest answer is to say so — see <see cref="RemotePeer.ConnectAsync"/>,
    /// which turns a refusal into a stated failure.
    /// </remarks>
    public static async ValueTask<IOperationGateway> OpenForRemoteAsync(
        string host,
        int port,
        string stateDirectory,
        string fingerprint,
        CancellationToken cancellationToken,
        string? passphraseEnvironmentVariable = null)
    {
        var connection = await RemotePeer.ConnectAsync(
            host, port, stateDirectory, fingerprint, "fallbackplan-cli", cancellationToken).ConfigureAwait(false);

        return new ServiceGateway(connection.Client, RemoteMode(host, port), connection, passphraseEnvironmentVariable);
    }

    private static string LocalMode(string stateDirectory) =>
        $"mode: service — the service holding the writer role for '{stateDirectory}' will run this.";

    private static string RemoteMode(string host, int port) =>
        $"mode: service (remote) — the paired service at {host}:{port} will run this.";

    /// <summary>What a resumed restore found already landed, said alike by either gateway (ADR-0109).</summary>
    /// <param name="count">The files left as they stood.</param>
    internal static string FoundInPlace(long count) => string.Create(
        CultureInfo.InvariantCulture,
        $"{count} file(s) already in place, holding the snapshot's content, were left as they stood");

    /// <summary>The line saying how many files a resume took up from the parts its run had assembled (ADR-0130).</summary>
    internal static string ResumedWithin(long count) => string.Create(
        CultureInfo.InvariantCulture,
        $"{count} file(s) were resumed from what the run had assembled of them");
}

/// <summary>The gateway that sends work to a running service.</summary>
/// <remarks>
/// The same gateway serves both bindings. Over the local binding
/// <paramref name="client"/> is a <see cref="LocalServiceClient"/>; over the
/// remote binding it is a <see cref="RemoteServiceClient"/> — the body only ever
/// speaks the <see cref="IFallbackPlanClient"/> contract, so nothing here knows
/// or cares which. <paramref name="owned"/> is what closing the gateway
/// disposes: on the local binding that is the client itself; on the remote
/// binding it is the connection holder that also releases the device key.
/// <paramref name="passphraseEnvironmentVariable"/> names the passphrase when
/// the verb was given one: a restore on a set-up installation derives its
/// grant from it here, where the person typed (ADR-0042 §5), and an applied
/// retention pass its reclaim grants (ADR-0055 §6).
/// </remarks>
internal sealed class ServiceGateway(
    IFallbackPlanClient client, string mode, IAsyncDisposable owned, string? passphraseEnvironmentVariable = null)
    : IOperationGateway
{
    /// <summary>How often to ask the service whether the job has finished.</summary>
    /// <remarks>
    /// Polling rather than watching: the progress stream is a separate
    /// connection carrying observations that may be dropped when a watcher falls
    /// behind (ADR-0029 §5), so it is the wrong thing to derive "has it
    /// finished" from. The job list is the authoritative answer.
    /// </remarks>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc/>
    public string Mode => mode;

    /// <inheritdoc/>
    public async ValueTask<OperationReport> RunBackupAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(request);

        // A service runs what its configuration names. An ad-hoc root is not
        // something it can be asked for, and quietly running it here instead
        // would be direct mode by the back door — against state the service
        // owns, which is the whole hazard the writer role exists to stop.
        if (request.SetName is null)
        {
            throw new CliFailureException(Strings.ServiceGateway_ServiceRunningHoldsWriterRole);
        }

        var accepted = await client.ExecuteAsync(
            new RunBackupCommand(request.SetName, request.Full), cancellationToken).ConfigureAwait(false);

        var jobId = accepted switch
        {
            JobAcceptedResult job => job.JobId,
            ServiceError error => throw new CliFailureException(Strings.FormatServiceGateway_ServiceRefusedBackup(error.Message)),
            _ => throw new CliFailureException(Strings.FormatServiceGateway_ServiceAnsweredBackupWith(accepted.GetType().Name)),
        };

        var finished = await AwaitJobAsync(jobId, cancellationToken).ConfigureAwait(false);

        List<string> report =
        [
            $"job            {finished.Id}",
            $"snapshot id    {finished.SnapshotId ?? "(none committed)"}",
            $"status         {Describe(finished.State)}",
        ];

        if (!string.IsNullOrWhiteSpace(finished.Detail))
        {
            report.Add($"detail         {finished.Detail}");
        }

        // Only a clean run is a success, and that follows from the comparison
        // rather than needing a clause: `CompletedWithFailures` is not
        // `Complete`, so a backup that could not read every file exits
        // non-zero. Deliberate — a backup that did not back everything up is
        // not something a script should read as fine, which is the whole
        // reason the state exists. Do not widen this to `IsCommitted`.
        return new OperationReport(finished.State == JobState.Complete, report);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> VerifyAsync(string level, CancellationToken cancellationToken)
    {
        var result = await SendAsync<VerificationResult>(
            new VerifyCommand(level), "a verification", cancellationToken).ConfigureAwait(false);

        List<string> lines =
        [
            string.Create(CultureInfo.InvariantCulture,
                $"verified {result.ObjectsChecked} blob(s) at level {result.Level}; {result.Failures} failure(s)"),
        ];
        if (result.SealedRecords > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"sealed   {result.SealedRecords} record(s) — structure verified; content verification needs a restore grant (ADR-0042). Not damage."));
        }

        return new OperationReport(result.Failures == 0, lines);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> CheckAsync(string level, CancellationToken cancellationToken)
    {
        var result = await SendAsync<CheckResult>(
            new CheckCommand(level), "a check", cancellationToken).ConfigureAwait(false);

        List<string> lines = [.. result.Findings.Select(finding => $"finding: {finding}")];
        lines.Add(result.Findings.Count == 0
            ? "check: OK"
            : string.Create(CultureInfo.InvariantCulture, $"check: {result.Findings.Count} problem(s)"));

        return new OperationReport(result.Findings.Count == 0, lines);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(request);

        // Each run through the service lands in a quarantine folder of its
        // own, so a resume that names none has nowhere to continue.
        if (request.Resume && request.ResumedRun is null)
        {
            throw new CliFailureException(Strings.FormatServiceGateway_ResumeNamesTheRun(request.OutputDirectory));
        }

        // A set-up installation holds no content key (ADR-0042 §7), and a
        // restore names the backup's files (FR-WOR-007): it reads through a
        // source opened under a grant derived here, where the passphrase is.
        var source = (await GrantedSources.OpenHoldingAsync(
            client, passphraseEnvironmentVariable, "A restore", request.SnapshotId, cancellationToken)
            .ConfigureAwait(false)).SourceId;
        try
        {
            var result = await SendAsync<RestoreResult>(
                new RunRestoreCommand(
                    request.SnapshotId, request.Path, request.OutputDirectory, Source: source, Paths: request.Paths,
                    Target: request.Target, Existing: request.Existing, InPlace: request.InPlace,
                    IgnoreFreeSpace: request.IgnoreFreeSpace, Resume: request.Resume ? request.ResumedRun : null,
                    AcknowledgeWiderAccess: request.AcknowledgeWiderAccess),
                "a restore",
                cancellationToken).ConfigureAwait(false);

            return Restored(result, run => $"to restore what is left, run it again with --resume {run}");
        }
        finally
        {
            await GrantedSources.CloseAsync(client, source).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> RestoreRunsAsync(CancellationToken cancellationToken)
    {
        var listed = await SendAsync<RestoreRunsResult>(
            new ListRestoreRunsCommand(), "the restore runs left unfinished", cancellationToken).ConfigureAwait(false);
        if (listed.Runs.Count == 0)
        {
            return new OperationReport(true, [Strings.ServiceGateway_NoRestoreRunUnfinished]);
        }

        return new OperationReport(true, [.. listed.Runs.Select(Describe), Strings.ServiceGateway_RestoreRunsHint]);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> ResumeRestoreAsync(
        string run, bool ignoreFreeSpace, bool acknowledgeWiderAccess, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(run);

        // Which snapshot the run restores is the service's to say, and a run
        // it does not name is refused before the passphrase is asked.
        var listed = await SendAsync<RestoreRunsResult>(
            new ListRestoreRunsCommand(), "the restore runs left unfinished", cancellationToken).ConfigureAwait(false);
        var named = listed.Runs.FirstOrDefault(candidate => string.Equals(candidate.Run, run, StringComparison.Ordinal))
            ?? throw new CliFailureException(Strings.FormatServiceGateway_NoSuchRestoreRun(run));

        var source = (await GrantedSources.OpenHoldingAsync(
            client, passphraseEnvironmentVariable, "A restore", named.SnapshotId, cancellationToken)
            .ConfigureAwait(false)).SourceId;
        try
        {
            var result = await SendAsync<RestoreResult>(
                new ResumeRestoreCommand(run, source, ignoreFreeSpace, acknowledgeWiderAccess), "a restore", cancellationToken)
                .ConfigureAwait(false);
            return Restored(result, again => $"to restore what is left, run restore-resume {again}");
        }
        finally
        {
            await GrantedSources.CloseAsync(client, source).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> PlanResumeRestoreAsync(string run, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(run);

        // As the resume asks it: the snapshot is the service's to say, and a
        // run it does not name is refused before the passphrase is asked.
        var listed = await SendAsync<RestoreRunsResult>(
            new ListRestoreRunsCommand(), "the restore runs left unfinished", cancellationToken).ConfigureAwait(false);
        var named = listed.Runs.FirstOrDefault(candidate => string.Equals(candidate.Run, run, StringComparison.Ordinal))
            ?? throw new CliFailureException(Strings.FormatServiceGateway_NoSuchRestoreRun(run));

        var source = (await GrantedSources.OpenHoldingAsync(
            client, passphraseEnvironmentVariable, "A restore's plan", named.SnapshotId, cancellationToken)
            .ConfigureAwait(false)).SourceId;
        try
        {
            var plan = await SendAsync<RestorePlanResult>(
                new PlanResumeRestoreCommand(run, source), "a restore's plan", cancellationToken).ConfigureAwait(false);
            return RestorePlanReport.Describe(
                plan,
                new RestoreRequest(
                    named.SnapshotId, null, named.OutputDirectory ?? string.Empty, Resume: true, ResumedRun: run,
                    Target: named.Target, Existing: named.Existing, InPlace: named.InPlace));
        }
        finally
        {
            await GrantedSources.CloseAsync(client, source).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> ForgetRestoreRunAsync(string run, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(run);

        await SendAsync<AcknowledgedResult>(
            new ForgetRestoreRunCommand(run), "letting a restore run go", cancellationToken).ConfigureAwait(false);
        return new OperationReport(true, [Strings.FormatServiceGateway_RestoreRunLetGo(run)]);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> ForgetRestoreRunsAsync(int? olderThanDays, CancellationToken cancellationToken)
    {
        var listed = await SendAsync<RestoreRunsResult>(
            new ListRestoreRunsCommand(), "the restore runs left unfinished", cancellationToken).ConfigureAwait(false);
        if (listed.Runs.Count == 0)
        {
            return new OperationReport(true, [Strings.ServiceGateway_NoRestoreRunUnfinished]);
        }

        // One the service will not let go, say because it is under way again,
        // is said in its words and every other is still asked.
        var before = olderThanDays is { } days ? OlderThanDays.Cutoff(days) : (ulong?)null;
        var asked = listed.Runs.Where(run => before is not { } cutoff || run.BeganAt < cutoff).ToList();
        var lines = new List<string>();
        var letGo = 0;
        foreach (var run in asked)
        {
            try
            {
                await SendAsync<AcknowledgedResult>(
                    new ForgetRestoreRunCommand(run.Run), "letting a restore run go", cancellationToken).ConfigureAwait(false);
                lines.Add(Strings.FormatServiceGateway_RestoreRunLetGo(run.Run));
                letGo++;
            }
            catch (CliFailureException refused)
            {
                lines.Add($"{run.Run}  not let go: {refused.Message}");
            }
        }

        lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"let go {letGo} restore {(letGo == 1 ? "run" : "runs")} the service left unfinished"));
        var younger = listed.Runs.Count - asked.Count;
        if (olderThanDays is { } within && younger > 0)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{younger} restore {(younger == 1 ? "run" : "runs")} begun within {OlderThanDays.Days(within)} {(younger == 1 ? "stays" : "stay")}"));
        }

        return new OperationReport(letGo == asked.Count, lines);
    }

    /// <summary>A run left unfinished, as a person chooses whether to resume it.</summary>
    private static string Describe(RestoreRunDescriptor run)
    {
        var began = DateTimeOffset.FromUnixTimeMilliseconds((long)run.BeganAt).UtcDateTime;
        var of = run.Set is { } set ? $" of set '{set}'" : string.Empty;
        var to = string.IsNullOrEmpty(run.OutputDirectory) ? "where its files came from" : run.OutputDirectory;
        var how = (run.InPlace ? ", in place" : string.Empty) + (run.Existing is { } existing ? $", {existing} where a file is in the way" : string.Empty);
        var holding = run.Held > 0 ? $", holding {RestoreSpace.Bytes((ulong)run.Held)} it had assembled" : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture, $"{run.Run}  snapshot {run.SnapshotId}{of} to {to}{how}, begun {began:yyyy-MM-dd HH:mm} UTC{holding}");
    }

    /// <summary>What a restore through the service says, with how to resume it where it did not finish.</summary>
    private static OperationReport Restored(RestoreResult result, Func<string, string> resumeLine)
    {
        List<string> lines =
        [
            string.Create(CultureInfo.InvariantCulture,
                $"restored {result.Restored} file(s) to {result.OutputDirectory}; {result.Failed} failure(s)"),
        ];

        if (result.FoundInPlace > 0)
        {
            lines.Add(OperationGateway.FoundInPlace(result.FoundInPlace));
        }

        if (result.ResumedWithin > 0)
        {
            lines.Add(OperationGateway.ResumedWithin(result.ResumedWithin));
        }

        // How to resume a run that did not finish (ADR-0110). A partial
        // run skipped what this target cannot hold, which a resume would
        // skip again.
        if (result.Run is { } run && result.Outcome is "failed" or "cancelled")
        {
            lines.Add(resumeLine(run));
        }

        // A person restoring hears that a copy of their backup is damaged
        // from the restore that met it (FR-RST-007), and which copy.
        if (result.ReadAround > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{result.ReadAround} file(s) came from another copy, because a copy they were first read from was damaged or would not read; each was verified like any other:"));
            lines.AddRange(result.ReadAroundSample ?? []);
        }

        // What the files came back without, which the receipt names file
        // by file (FR-RST-004).
        lines.AddRange(result.NotApplied ?? []);

        return new OperationReport(result.Failed == 0, lines);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> PlanRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(request);

        // A plan of a resume is measured where the run it continues landed,
        // and through the service each run lands in a folder of its own.
        if (request.Resume && request.ResumedRun is null)
        {
            throw new CliFailureException(Strings.FormatServiceGateway_ResumeNamesTheRun(request.OutputDirectory));
        }

        // The plan names the backup's files as the restore would, so it reads
        // through a source the passphrase unlocked (FR-WOR-007), and asks for
        // the shape the restore above runs: where it writes, under quarantine.
        var source = (await GrantedSources.OpenHoldingAsync(
            client, passphraseEnvironmentVariable, "A restore's plan", request.SnapshotId, cancellationToken)
            .ConfigureAwait(false)).SourceId;
        try
        {
            var plan = await SendAsync<RestorePlanResult>(
                new PlanRestoreCommand(
                    request.SnapshotId, request.Path, Source: source, Paths: request.Paths,
                    OutputDirectory: request.OutputDirectory, Target: request.Target, Existing: request.Existing,
                    InPlace: request.InPlace, Resume: request.Resume ? request.ResumedRun : null),
                "a restore's plan",
                cancellationToken).ConfigureAwait(false);
            return RestorePlanReport.Describe(plan, request);
        }
        finally
        {
            await GrantedSources.CloseAsync(client, source).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> VerifyDestinationAsync(
        string? setName, string? destinationName, bool full, bool probe, CancellationToken cancellationToken)
    {
        var result = await SendAsync<VerifyDestinationResult>(
            new VerifyDestinationCommand(setName, destinationName, full, probe), "a destination verification",
            cancellationToken).ConfigureAwait(false);

        // Unlike a sync, this one CAN fail: damaged objects are a finding, and
        // the exit code must carry that without anyone parsing the prose.
        return new OperationReport(result.Damaged == 0, result.Lines);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> DrillAsync(
        string? setName, string? destinationName, CancellationToken cancellationToken)
    {
        var result = await SendAsync<DrillResult>(
            new RunDrillCommand(setName, destinationName), "a restore drill", cancellationToken).ConfigureAwait(false);

        // A pair nothing was drilled at has proved nothing, so it is not a
        // success either; the counts say so without anyone parsing the prose.
        return new OperationReport(result.Failed == 0 && result.NotDrilled == 0, result.Lines);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> NoticeNamesAsync(string noticeId, CancellationToken cancellationToken)
    {
        var listed = await SendAsync<NoticesResult>(
            new ListNoticesCommand(IncludeAcknowledged: true), "the notices", cancellationToken).ConfigureAwait(false);
        var notice = listed.Notices.FirstOrDefault(candidate => string.Equals(candidate.Id, noticeId, StringComparison.Ordinal))
            ?? throw new CliFailureException($"No notice '{noticeId}' is on record.");

        // A notice that left nothing out needs no passphrase to say so.
        if (notice is not { NamesWithheld: > 0, SetId: { } setId })
        {
            return new OperationReport(true, ["This notice names no files."]);
        }

        var source = await GrantedSources.OpenAsync(
            client, passphraseEnvironmentVariable, "A notice's files",
            set => string.Equals(set.Id, setId, StringComparison.OrdinalIgnoreCase), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var named = await SendAsync<NoticeNamesResult>(
                new NoticeNamesCommand(noticeId, source.SourceId), "a notice's files", cancellationToken)
                .ConfigureAwait(false);
            return new OperationReport(true, named.Names);
        }
        finally
        {
            await GrantedSources.CloseAsync(client, source.SourceId).ConfigureAwait(false);
        }
    }

    public async ValueTask<OperationReport> SyncAsync(
        string? setName, string? destinationName, CancellationToken cancellationToken)
    {
        var result = await SendAsync<SyncResult>(
            new SyncCommand(setName, destinationName), "a sync", cancellationToken).ConfigureAwait(false);

        return new OperationReport(true, result.Lines);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> PreviewSetChangesAsync(
        string? setName, int? sampleLimit, CancellationToken cancellationToken)
    {
        // A deleted file, and one the rules stop capturing, is named by the
        // backup alone, so the comparison names those only through a source
        // the passphrase unlocked (FR-WOR-007); without one it counts them.
        string? source = null;
        if (passphraseEnvironmentVariable is not null)
        {
            var name = setName;
            if (name is null)
            {
                var sets = await SendAsync<BackupSetsResult>(
                    new ListBackupSetsCommand(), "listing the backup sets", cancellationToken).ConfigureAwait(false);
                name = sets.Sets.Count > 0 ? sets.Sets[0].Name : null;
            }

            source = (await GrantedSources.OpenAsync(
                client, passphraseEnvironmentVariable, "A comparison", set => set.Name == name, cancellationToken)
                .ConfigureAwait(false)).SourceId;
        }

        SetChangePreviewResult result;
        try
        {
            result = await SendAsync<SetChangePreviewResult>(
                new PreviewSetChangesCommand(setName, SampleLimit: sampleLimit, Source: source), "a change preview",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (source is not null)
            {
                await GrantedSources.CloseAsync(client, source).ConfigureAwait(false);
            }
        }

        var lines = new List<string>
        {
            result.BaselineSnapshotId is { } baseline
                ? string.Create(CultureInfo.InvariantCulture,
                    $"set '{result.SetName}' vs snapshot {baseline} captured {DateTimeOffset.FromUnixTimeMilliseconds((long)(result.BaselineCapturedAt ?? 0)):u}")
                : $"set '{result.SetName}' has never backed up — everything below is new",
            string.Create(CultureInfo.InvariantCulture, $"{result.Unchanged} unchanged"),
        };

        AppendBucket(lines, "new", result.New);
        AppendBucket(lines, "updated", result.Updated);
        AppendBucket(lines, "metadata-only", result.MetadataOnly);
        AppendBucket(lines, "moved", result.Moved);
        AppendBucket(lines, "deleted", result.Deleted, result.NamesWithheld);
        AppendBucket(lines, "no longer included by the rules", result.NoLongerIncluded, result.NamesWithheld);
        if (result.Failures > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{result.Failures} unreadable"));
        }

        return new OperationReport(true, lines);

        static void AppendBucket(List<string> lines, string label, ChangeBucketDescriptor bucket, bool withheld = false)
        {
            if (bucket.Count == 0)
            {
                return;
            }

            if (withheld)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{bucket.Count} {label} — named only with --passphrase-env"));
                return;
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{bucket.Count} {label}"));
            foreach (var path in bucket.Sample)
            {
                lines.Add($"  {path}");
            }

            if (bucket.Count > bucket.Sample.Count)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"  … and {bucket.Count - bucket.Sample.Count} more"));
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> RetentionAsync(bool apply, CancellationToken cancellationToken)
    {
        var grants = apply ? await ReclaimGrantsAsync(cancellationToken).ConfigureAwait(false) : null;
        var result = await SendAsync<RetentionResult>(
            new RetentionCommand(apply, ReclaimGrants: grants), "a retention pass", cancellationToken)
            .ConfigureAwait(false);

        return new OperationReport(true, result.Lines);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> DeleteSnapshotsAsync(
        string setName, IReadOnlyList<string> snapshotIds, bool apply, CancellationToken cancellationToken)
    {
        var sets = await SendAsync<BackupSetsResult>(
            new ListBackupSetsCommand(), "listing the backup sets", cancellationToken).ConfigureAwait(false);
        var set = sets.Sets.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, setName, StringComparison.Ordinal)
                || string.Equals(candidate.Id, setName, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliFailureException($"no backup set '{setName}' is configured here.");

        // The grant is derived here, where the passphrase is, as an applied
        // retention pass derives its own (ADR-0055 §6).
        string? grant = null;
        if (apply && await ReclaimGrantsAsync(cancellationToken).ConfigureAwait(false) is { } grants)
        {
            grant = grants.GetValueOrDefault(set.Id)
                ?? throw new CliFailureException(
                    $"the passphrase does not open set '{set.Name}' — it was made under another one; nothing was sent.");
        }

        var result = await SendAsync<DeleteSnapshotsResult>(
            new DeleteSnapshotsCommand(set.Id, snapshotIds, apply, grant), "a snapshot deletion", cancellationToken)
            .ConfigureAwait(false);

        var lines = new List<string>();
        foreach (var outcome in result.Snapshots)
        {
            var held = outcome.Awaiting.Count > 0 ? string.Join(", ", outcome.Awaiting) : null;
            lines.Add(outcome.State switch
            {
                "deleted" => $"{outcome.SnapshotId}: deleted",
                "would-delete" => held is null
                    ? $"{outcome.SnapshotId}: would delete from staging"
                    : $"{outcome.SnapshotId}: would delete from staging and from {held}",
                _ => held is null
                    ? $"{outcome.SnapshotId}: deletion pending"
                    : $"{outcome.SnapshotId}: deletion pending, waiting on {held}",
            });
        }

        lines.AddRange(result.Lines);
        return new OperationReport(true, lines);
    }

    /// <summary>
    /// The reclaim grants an applied pass carries (ADR-0055 §6, Amendment 3):
    /// the service holds the key that publishes and not the key that
    /// authorises a deletion, so each set's grant is derived here, where the
    /// person typed, under the facts the service publishes for that set. Null
    /// when no set publishes any, which leaves nothing for a grant to
    /// authorise.
    /// </summary>
    /// <remarks>
    /// One derivation per distinct salt and parameters, each proved against
    /// the sealing key published beside it before anything is sealed, as a
    /// routed restore proves its grant. A set the passphrase does not open is
    /// left out, and the service reports it as not applied.
    /// </remarks>
    private async ValueTask<IReadOnlyDictionary<string, string>?> ReclaimGrantsAsync(
        CancellationToken cancellationToken)
    {
        var description = await SendAsync<ServiceDescriptionResult>(
            new DescribeServiceCommand(), "a description of the service", cancellationToken).ConfigureAwait(false);
        var sets = await SendAsync<BackupSetsResult>(
            new ListBackupSetsCommand(), "listing the backup sets", cancellationToken).ConfigureAwait(false);

        (string Salt, Argon2Parameters Parameters, string SealingPublicKey)? installation = description is
            {
                KdfSalt.Length: > 0,
                KdfMemoryKib: { } memoryKib,
                KdfIterations: { } iterations,
                KdfParallelism: { } parallelism,
                SealingPublicKey.Length: > 0,
            }
            ? (description.KdfSalt,
                new Argon2Parameters { MemoryKiB = memoryKib, Iterations = iterations, Parallelism = parallelism },
                description.SealingPublicKey)
            : null;
        var judged = new List<(string SetId, string Salt, Argon2Parameters Parameters, string SealingPublicKey)>();
        foreach (var set in sets.Sets)
        {
            var facts = set is
                {
                    KdfSalt.Length: > 0,
                    KdfMemoryKib: { } setMemory,
                    KdfIterations: { } setIterations,
                    KdfParallelism: { } setLanes,
                    SealingPublicKey.Length: > 0,
                }
                ? (set.KdfSalt,
                    new Argon2Parameters { MemoryKiB = setMemory, Iterations = setIterations, Parallelism = setLanes },
                    set.SealingPublicKey)
                : installation;
            if (facts is { } chosen)
            {
                judged.Add((set.Id, chosen.Salt, chosen.Parameters, chosen.SealingPublicKey));
            }
        }

        if (judged.Count == 0 || description.RestoreGrantRecipient is not { Length: > 0 } recipientHex)
        {
            return null;
        }

        if (passphraseEnvironmentVariable is null)
        {
            throw new CliFailureException(
                "applying retention authorises a deletion, and this service holds the key that publishes and "
                + "not the key that deletes: name --passphrase-env <VAR> so the reclaim grant can be derived "
                + "here (ADR-0055).");
        }

        var recipient = Convert.FromHexString(recipientHex);
        using var passphrase = CliSession.ReadPassphrase(passphraseEnvironmentVariable);
        var derived = new Dictionary<string, (byte[] SealingPublicKey, string Grant)>(StringComparer.Ordinal);
        var grants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (setId, saltHex, parameters, sealingHex) in judged)
        {
            var key = $"{saltHex.ToUpperInvariant()}/{parameters.MemoryKiB}/{parameters.Iterations}/{parameters.Parallelism}";
            if (!derived.TryGetValue(key, out var known))
            {
                using var authority = WriteOnlyDerivation.Derive(
                    passphrase, parameters, Convert.FromHexString(saltHex), KdfValidationMode.OpenRepository);
                known = (
                    authority.Credential.SealingPublicKey.ToArray(),
                    Convert.ToHexStringLower(WriteOnlyProvisioning.SealReclaimGrant(recipient, authority.ReclaimKeySeed)));
                derived[key] = known;
            }

            if (known.SealingPublicKey.AsSpan().SequenceEqual(Convert.FromHexString(sealingHex)))
            {
                grants[setId] = known.Grant;
            }
        }

        return grants.Count > 0
            ? grants
            : throw new CliFailureException(
                "the passphrase does not reproduce this installation's credential — nothing was sent.");
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => owned.DisposeAsync();

    /// <summary>Sends one command and insists on the result type it should answer with.</summary>
    private async ValueTask<T> SendAsync<T>(ServiceCommand command, string what, CancellationToken cancellationToken)
        where T : ServiceResult
    {
        var result = await client.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);

        return result switch
        {
            T expected => expected,
            ServiceError error => throw new CliFailureException(Strings.FormatServiceGateway_ServiceRefused(what, error.Message)),
            _ => throw new CliFailureException(Strings.FormatServiceGateway_ServiceAnsweredWith(what, result.GetType().Name)),
        };
    }

    private static string Describe(JobState state) => state switch
    {
        JobState.Complete => "complete",
        JobState.CompletedWithFailures =>
            "PARTIAL — the snapshot is committed, but not everything could be read",
        JobState.Cancelled => "cancelled",
        // The cause belongs on the detail line beside this one, which carries
        // the run's own park reason: since ADR-0069 the pool has two askers —
        // a higher-priority arrival and a background window that has shut —
        // and naming one of them here would be wrong half the time.
        JobState.Paused => "PAUSED — suspended with its state held; it resumes unattended",
        JobState.FailedRecoverable => "FAILED (recoverable) — the service retries on its next pass",
        JobState.FailedPermanent => "FAILED — needs intervention; it will not be retried",
        _ => state.ToString().ToLowerInvariant(),
    };

    /// <summary>Whether a job has stopped moving on its own.</summary>
    /// <remarks>
    /// <see cref="JobState.CompletedWithFailures"/> belongs here for the same
    /// reason <see cref="JobState.Complete"/> does — it is terminal. Omitting
    /// it would leave <c>AwaitJobAsync</c> polling a job that will never
    /// transition again. <see cref="JobState.Paused"/> is deliberately NOT
    /// here (ADR-0047 Amendment 1): a suspended run resumes unattended when a pool
    /// slot frees, so a caller waiting on it keeps waiting.
    /// </remarks>
    private static bool HasSettled(JobState state) => state is
        JobState.Complete or JobState.CompletedWithFailures or JobState.Cancelled
        or JobState.FailedRecoverable or JobState.FailedPermanent;

    private async ValueTask<JobDescriptor> AwaitJobAsync(string jobId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var listed = await client.ExecuteAsync(new ListJobsCommand(false), cancellationToken).ConfigureAwait(false);
            if (listed is ServiceError error)
            {
                throw new CliFailureException(Strings.FormatServiceGateway_ServiceStoppedReportingJob(error.Message));
            }

            if (listed is not JobsResult jobs)
            {
                throw new CliFailureException(Strings.FormatServiceGateway_ServiceAnsweredJobListWith(listed.GetType().Name));
            }

            var job = jobs.Jobs.FirstOrDefault(candidate => string.Equals(candidate.Id, jobId, StringComparison.Ordinal))
                ?? throw new CliFailureException(Strings.FormatServiceGateway_ServiceForgotJobBeforeFinished(jobId));

            if (HasSettled(job.State))
            {
                return job;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>The gateway that does the work in this process, holding the writer role.</summary>
/// <param name="session">The session it works through.</param>
/// <param name="logger">Where what it noticed is recorded.</param>
/// <param name="stateNamed">
/// Whether the command named its state directory. One that named none also
/// says a restore is under way where the installation's service looks
/// (ADR-0172 Amendment 6).
/// </param>
internal sealed class DirectGateway(CliSession session, ILogger? logger = null, bool stateNamed = true) : IOperationGateway
{
    private static readonly AsyncLocal<Func<string, long?>?> AvailableBytesFlow = new();

    private static readonly AsyncLocal<Func<string, ulong?>?> VolumeFlow = new();

    private static readonly AsyncLocal<Action<Domain.Identifiers.RepositoryId>?> RestoreUnderWayFlow = new();

    /// <summary>
    /// A test harness's view of the disk for this flow's restores
    /// (FR-RST-003): directory → available bytes, null meaning the platform
    /// will not say. Null, the production value, asks the platform. Scoped to
    /// the flow that sets it, like <c>FanOut.ReadBackBudget</c>, so it
    /// belongs to the test that sets it and to no other.
    /// </summary>
    internal static Func<string, long?>? AvailableBytesInFlow
    {
        get => AvailableBytesFlow.Value;
        set => AvailableBytesFlow.Value = value;
    }

    /// <summary>
    /// A test harness's view of which volume holds each directory, for this
    /// flow's restores (FR-RST-003), as <see cref="AvailableBytesInFlow"/> is
    /// of what is free there: null, the production value, asks the platform.
    /// </summary>
    internal static Func<string, ulong?>? VolumeOfInFlow
    {
        get => VolumeFlow.Value;
        set => VolumeFlow.Value = value;
    }

    /// <summary>
    /// Called with the repository a restore in this flow reads, once it has
    /// said it is under way and before it writes (ADR-0172 Amendment 6), so a
    /// test can look where a service would. Null, the production value, does
    /// nothing.
    /// </summary>
    internal static Action<Domain.Identifiers.RepositoryId>? RestoreUnderWayInFlow
    {
        get => RestoreUnderWayFlow.Value;
        set => RestoreUnderWayFlow.Value = value;
    }

    /// <summary>
    /// Every state directory a restore in direct mode says it is under way in
    /// (ADR-0172 Amendment 6): the one the command works in and, where it
    /// named none, the one the installation's service keeps (FR-SVC-016),
    /// where there is one. That is found as every path-less process finds it,
    /// but never made: a restore makes no installation.
    /// </summary>
    /// <param name="stateDirectory">The state directory the command works in.</param>
    /// <param name="stateNamed">Whether the command named it.</param>
    /// <returns>Each directory, once.</returns>
    internal static IReadOnlyList<string> RestoreSeenFrom(string stateDirectory, bool stateNamed)
    {
        List<string> seen = [stateDirectory];
        if (stateNamed)
        {
            return seen;
        }

        var installation = InstallationDefaults.StateDirectoryWithoutMaking;
        // One directory twice would be held twice by one run, and the second
        // hold refused by the first. Compared without case, as containment is.
        static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Directory.Exists(installation)
            && !string.Equals(Full(installation), Full(stateDirectory), StringComparison.OrdinalIgnoreCase))
        {
            seen.Add(installation);
        }

        return seen;
    }

    /// <summary>The session this gateway works through — the verbs a service cannot serve still need it.</summary>
    public CliSession Session => session;

    /// <inheritdoc/>
    public string Mode => $"mode: direct — this command holds the writer role for '{session.StateDirectory}'.";

    /// <inheritdoc/>
    public async ValueTask<OperationReport> RunBackupAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(request);

        IReadOnlyList<ScanRoot> roots;
        IReadOnlyList<string> include, exclude;
        byte[] backupSetId;

        if (request.SetName is { } setName)
        {
            var configuration = ClientConfiguration.Load(session.ConfigurationPath);
            var set = configuration.FindSet(setName)
                ?? throw new CliFailureException(Strings.FormatDirectGateway_NoBackupSetNamedExists(setName, session.ConfigurationPath));
            roots = [.. set.Roots.Select(root => new ScanRoot(root.Path, root.Label))];
            include = set.IncludeRules;
            exclude = set.ExcludeRules;
            backupSetId = Convert.FromHexString(set.Id);
        }
        else
        {
            var rootPath = request.Root ?? throw new CliFailureException(Strings.DirectGateway_PassRootDirectorySetName);
            roots = [new ScanRoot(rootPath)];
            include = request.IncludeRules;
            exclude = request.ExcludeRules;
            backupSetId = session.BackupSetId;
        }

        foreach (var root in roots.Where(root => !Directory.Exists(root.Path)))
        {
            throw new CliFailureException(Strings.FormatDirectGateway_NotDirectory(root.Path));
        }

        using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId, logger);

        // Incremental against the newest snapshot of the same set the catalogue
        // knows — unless --full asks for a re-read.
        var prior = request.Full
            ? null
            : catalogue.EnumerateSnapshots()
                .FirstOrDefault(row => row.BackupSetId.Span.SequenceEqual(backupSetId));

        var orchestrator = new PublicationOrchestrator(
            CapturePolicy.Default,
            session.Repository.RepositoryId,
            session.Writer,
            session.CurrentGeneration,
            session.Repository.Keys,
            session.Repository.Credential,
            session.Store,
            session.CreateSequence(),
            session.SpoolDirectory,
            session.Repository.EffectiveFormatVersion,
            observer: null,
            catalogue);

        var snapshotId = RandomNumberGenerator.GetBytes(16);
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var published = await orchestrator.PublishAsync(
            new SnapshotJob
            {
                Source = new FallbackPlan.Filesystem.Local.LocalFileSystemSource(),
                Roots = roots,
                IncludeRules = include,
                ExcludeRules = exclude,
                DeviceId = session.DeviceId,
                BackupSetId = backupSetId,
                SnapshotId = snapshotId,
                ParentSnapshots = prior is null ? [] : [prior.SnapshotId],
                PriorSnapshotId = prior?.SnapshotId,
                NowUnixMilliseconds = now,
                Clock = static () => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = session.CurrentGeneration.Value + 2,
                ClientVersion = "fallbackplan-cli/0.1",
            },
            cancellationToken).ConfigureAwait(false);

        session.State.RecordJob(new JobHistoryEntry
        {
            SnapshotId = Hex(snapshotId),
            BackupSetId = Hex(backupSetId),
            StartedAt = now,
            CaptureStatus = (byte)(published.ErrorManifestObjectId is null ? 1 : 2),
            Files = published.Files.Count,
            Failures = published.Failures.Count,
        });

        var reused = published.Files.Count(file => file.Reused);
        List<string> report =
        [
            $"snapshot id    {Hex(snapshotId)}",
            string.Create(CultureInfo.InvariantCulture,
                $"files          {published.Files.Count} ({reused} unchanged, {published.Failures.Count} failed)"),
            string.Create(CultureInfo.InvariantCulture, $"data blobs     {published.ContentBlobs.Count} new"),
        ];

        if (prior is not null)
        {
            report.Add($"incremental    against {Hex(prior.SnapshotId)}");
        }

        report.Add(published.ErrorManifestObjectId is null
            ? "status         complete"
            : "status         PARTIAL — see the error manifest");

        return new OperationReport(published.ErrorManifestObjectId is null, report);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> VerifyAsync(string level, CancellationToken cancellationToken)
    {
        var parsed = ParseLevel(level);

        using var verifier = new VerifyEngine(session.Repository.RepositoryId, session.Repository.Keys, session.Store);
        var blobs = 0;
        var failures = 0;
        var sealedRecords = 0L;
        List<string> lines = [];

        await foreach (var entry in session.Store
            .ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            blobs++;
            var result = await verifier.VerifyBlobAsync(entry.Key, entry.Length, parsed, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Ok)
            {
                failures++;
                lines.Add($"FAILED {entry.Key.Value}: {result.Detail}");
            }

            sealedRecords += result.RecordsSealed;
        }

        // Direct mode names the blob that failed. The contract carries only a
        // count, so this is one of the places the two paths legitimately differ
        // in what they can say.
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"verified {blobs} blob(s) at level {parsed}; {failures} failure(s)"));
        if (sealedRecords > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"sealed   {sealedRecords} record(s) — structure verified; content verification needs a restore grant (ADR-0042). Not damage."));
        }

        return new OperationReport(failures == 0, lines);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> CheckAsync(string level, CancellationToken cancellationToken)
    {
        var parsed = ParseLevel(level);
        var problems = 0;
        List<string> lines = [];

        using (var verifier = new VerifyEngine(session.Repository.RepositoryId, session.Repository.Keys, session.Store))
        {
            var blobs = 0;
            var sealedRecords = 0L;
            await foreach (var entry in session.Store
                .ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
            {
                blobs++;
                var result = await verifier.VerifyBlobAsync(entry.Key, entry.Length, parsed, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Ok)
                {
                    problems++;
                    lines.Add($"blob FAILED  {entry.Key.Value}: {result.Detail}");
                }

                sealedRecords += result.RecordsSealed;
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture, $"blobs      {blobs} verified at {parsed}"));
            if (sealedRecords > 0)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"sealed     {sealedRecords} record(s) — content verification needs a restore grant (ADR-0042). Not damage."));
            }
        }

        using (var journalReader = new JournalReader(
            session.Store, session.Repository.RepositoryId, session.Repository.Credential))
        {
            var (records, unparseable, journalFindings) = await journalReader
                .LoadAsync(session.CurrentGeneration.Value, cancellationToken).ConfigureAwait(false);
            problems += unparseable + journalFindings.Count;

            // The collector's margin, so the count is the collector's: a
            // check calling an intent expired while the collector still
            // honours it would disagree with the thing it checks.
            var margin = ClientConfiguration.Load(session.ConfigurationPath, logger).EffectiveClockSkewMargin;
            var survey = IntentSurveyor.Survey(
                records, unparseable, session.CurrentGeneration.Value,
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), (ulong)margin.TotalMilliseconds);

            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"journal    {records.Count} record(s), {unparseable} unparseable, {survey.LiveIntents.Count} live intent(s); skew margin {margin.TotalHours} h"));
            lines.AddRange(journalFindings.Select(finding => $"journal    {finding.Kind}: {finding.Detail}"));
        }

        if (File.Exists(session.CataloguePath))
        {
            using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId, logger);
            var findings = catalogue.Findings();
            problems += findings.Count;
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"catalogue  {findings.Count} damage finding(s)"));
            lines.AddRange(findings.Select(finding => $"catalogue  {finding.Kind}: {finding.Detail}"));
        }
        else
        {
            lines.Add("catalogue  absent (rebuildable cache — run `rebuild-index` to materialise it)");
        }

        lines.Add(problems == 0
            ? "check: OK"
            : string.Create(CultureInfo.InvariantCulture, $"check: {problems} problem(s)"));

        return new OperationReport(problems == 0, lines);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(request);

        // In direct mode a run lands in place, so there is no run to name.
        if (request.ResumedRun is not null)
        {
            throw new CliFailureException(Strings.DirectGateway_ResumeLandsInPlace);
        }

        var existing = PolicyOf(request.Existing);

        // Direct mode restores through the same planner and executor the
        // service uses (ADR-0028 §3: "the same operation performs identically
        // through either path"). The earlier hand-rolled walk here combined
        // repository-supplied path text with Path.Combine and wrote in place —
        // no containment, no quarantine, no receipt, no metadata — so a
        // manifest naming `../` escaped the output directory. Containment is a
        // property of the executor (architecture 08 §3), and this is now that
        // executor.
        var snapshotId = Convert.FromHexString(request.SnapshotId);

        using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId, logger);
        var target = RestoreTargetProfile.ForLocalPlatform(catalogue, snapshotId, session.DeviceId);
        var plan = RestorePlanner.Plan(catalogue, snapshotId, PrefixesOf(request), target);
        if (plan.Items.Count == 0)
        {
            throw new CliFailureException(Strings.FormatDirectGateway_CatalogueKnowsNothingUnderSnapshot(request.SnapshotId));
        }

        var slices = SlicesOf(catalogue, snapshotId, plan, request);
        var set = SetForReceipt(catalogue, snapshotId);
        using var reader = new RepositoryReader(session.Repository.RepositoryId, session.Repository.Keys, session.Store, session.ReadAuthority);

        // No load at all: the catalogue says where every record is, so the
        // read goes straight there and a blob is opened through its footer
        // only when that fails (NFR-PERF-009). Opening even the blobs a plan
        // needs cost three ranged reads each — locator, footer, envelope —
        // before a byte of payload.
        reader.UseLocationSource(catalogue.ResolveLocation);

        // Kept where a resume to the same place finds them (ADR-0135):
        // direct mode names no run a resume could ask by.
        var parts = DirectRestoreParts.Of(session.StateDirectory, slices.Select(slice => slice.OutputDirectory));

        // Whether a folder it writes to would open a file wider than it was
        // (ADR-0178 Amendment 1; architecture 06 §3), judged as its plan
        // judges it, before anything is written.
        if (!request.AcknowledgeWiderAccess
            && await RestoreMetadata.OpenedWiderAsync(
                plan,
                target,
                RestoreMetadata.DestinationsOf([.. slices.Select(slice => new RestoreSlice(slice.Plan, slice.OutputDirectory))]),
                reader,
                cancellationToken).ConfigureAwait(false) is { } wider)
        {
            throw new CliFailureException(Strings.FormatDirectGateway_RestoreWouldOpenWider(wider.Describe()));
        }

        // Whether it fits, before anything is written (FR-RST-003). The run
        // below lands in place under the policy asked, so that is the run
        // measured, at every root it writes to together, less what a resume
        // leaves where it stands and what its parts hold (ADR-0136).
        var space = await RestoreSpace.MeasureRunAsync(
            [.. slices.Select(slice => new RestoreSlice(
                slice.Plan, slice.OutputDirectory, request.Resume ? slice.OutputDirectory : null))],
            RestoreDestinationMode.InPlace,
            existing,
            SpaceProbe(),
            reader,
            request.Resume ? parts : null,
            cancellationToken).ConfigureAwait(false);
        if (space.IsShort && !request.IgnoreFreeSpace)
        {
            throw new CliFailureException(Strings.FormatDirectGateway_RestoreWouldNotFit(space.Refusal()));
        }

        var began = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var executor = new RestoreExecutor(reader, target);
        var options = new RestoreExecutionOptions
        {
            // The user named the output directory, or asked for the original
            // location, which the service restores in place too, so content
            // lands there rather than in quarantine — quarantine is the
            // default when a service restores onto a live machine it did not
            // choose (architecture 08 §3.1), which is a distinct control from
            // where a person's explicit `--output` points. Containment, the
            // receipt and metadata come from the executor regardless.
            DestinationMode = RestoreDestinationMode.InPlace,
            ExistingDestination = existing,
            // A fresh run identifier per invocation, so two restores of one
            // snapshot displace into distinct stores (architecture 08 §3.1).
            RunId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            NowUnixMilliseconds = began,
            Resume = request.Resume,
            PartsDirectory = parts,
        };

        // One run at a time to a destination (ADR-0143), and none into one
        // that holds or lies inside a destination under way (ADR-0157):
        // either would write the same files, and the first a second would
        // assemble into the same parts and let them go under this one.
        // Beside its parts, what the run restores and where, so
        // restore-parts can say whose they are while it is left unfinished
        // (ADR-0142).
        var hold = DirectRestoreParts.Take(
            session.StateDirectory,
            parts,
            new DirectRestoreRun(
                Convert.ToHexStringLower(snapshotId),
                set?.Name,
                request.ToOriginal ? null : Path.GetFullPath(request.OutputDirectory),
                request.Existing,
                began,
                [.. slices.Select(slice => Path.GetFullPath(slice.OutputDirectory))]),
            out var problem,
            out var overlap);
        if (hold is null)
        {
            throw new CliFailureException(overlap is { } nested
                ? Strings.FormatDirectGateway_DestinationOverlaps(nested.UnderWay, nested.Asked)
                : Strings.FormatDirectGateway_DestinationHeld(parts, problem));
        }

        // Said where a service looks before a byte is written, as the
        // service names its own runs (ADR-0172 Amendment 6): its retention
        // pass, a sync's convergence and its peers collect nothing this run
        // may read while it runs.
        var underWay = SayUnderWay(options.RunId);
        var receipts = new List<(RestoreReceipt Receipt, string LabelPrefix)>();
        try
        {
            RestoreUnderWayInFlow?.Invoke(session.Repository.RepositoryId);
            foreach (var slice in slices)
            {
                receipts.Add((
                    await executor.ExecuteAsync(slice.Plan, slice.OutputDirectory, options, cancellationToken).ConfigureAwait(false),
                    slice.LabelPrefix));
            }
        }
        finally
        {
            foreach (var marker in underWay)
            {
                marker.Dispose();
            }

            // Let go however the run ends, and before its folder is, which a
            // platform that deletes no file held open needs.
            hold.Dispose();
        }

        var receipt = RestoreOriginalLocation.Merge(receipts) with { Set = set?.Name, SetId = set?.Id };

        // A run that completed, or skipped only what the target cannot hold,
        // is finished, as the service has it (ADR-0115): a resume would add
        // nothing, so its parts go.
        if (receipt.Outcome is RestoreOutcome.Complete or RestoreOutcome.Partial)
        {
            DirectRestoreParts.LetGo(options.PartsDirectory);
        }

        var directories = plan.Items
            .Where(item => item.Kind == EntryKind.DirectoryPlaceholder)
            .Select(item => item.Path)
            .ToHashSet(StringComparer.Ordinal);
        // A degraded file's content was written and verified — it counts as
        // written — but the shortfall is named on its own line and in the
        // summary, because the file on disk is not the file that was captured.
        var restored = receipt.Items.Count(
            item => item.Outcome is "restored" or "degraded" && !directories.Contains(item.Path));
        var failed = receipt.Items.Count(item => item.Outcome == "failed");
        var skipped = receipt.Items.Count(item => item.Outcome == "skipped");
        var degraded = receipt.Items.Count(item => item.Outcome == "degraded");

        List<string> lines = [];
        foreach (var item in receipt.Items.Where(item => item.Outcome is "failed" or "skipped" or "degraded"))
        {
            lines.Add($"{item.Outcome} {item.Path}: {item.Detail}");
        }

        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"restore {receipt.Outcome}: {restored} file(s) to {receipt.WrittenTo}; {failed} failure(s), {skipped} skipped, {degraded} degraded"));

        // Kept as the service keeps one, so the claim outlives the terminal
        // it was printed in (FR-RST-004).
        lines.Add($"receipt: {receipt.Keep(session.ReceiptsDirectory, options.RunId)}");

        var foundInPlace = receipt.Items.Count(item => item.FoundInPlace);
        if (foundInPlace > 0)
        {
            lines.Add(OperationGateway.FoundInPlace(foundInPlace));
        }

        var resumedWithin = receipt.Items.Count(item => item.ResumedAt > 0);
        if (resumedWithin > 0)
        {
            lines.Add(OperationGateway.ResumedWithin(resumedWithin));
        }

        // How to resume a run that did not finish, as one through the
        // service says it (ADR-0110), what its parts keep of the backup's
        // plaintext and where (ADR-0137), and how to let them go (ADR-0142).
        if (receipt.Outcome is not (RestoreOutcome.Complete or RestoreOutcome.Partial))
        {
            var held = DirectRestoreParts.Held(parts);
            lines.Add(held > 0
                ? $"to restore what is left, run it again with --resume; {RestoreSpace.Bytes((ulong)held)} of its files assembled so far are kept under '{parts}' until a run there finishes, or restore-parts --state '{session.StateDirectory}' --forget {Path.GetFileName(parts)} lets them go"
                : "to restore what is left, run it again with --resume");
        }

        // What the files came back without, which the receipt names file by
        // file (FR-RST-004).
        lines.AddRange(RestoreMetadata.Summarise(receipt.Items, target));

        return new OperationReport(receipt.Outcome is RestoreOutcome.Complete, lines);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> PlanRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(request);

        // The plan the service makes, made here for the run above: its
        // manifests read and every blob it names asked for, measured where
        // that run writes, in place under the policy asked, and for a resume
        // as it will meet what the run it continues landed (ADR-0120).
        if (request.ResumedRun is not null)
        {
            throw new CliFailureException(Strings.DirectGateway_ResumeLandsInPlace);
        }

        var existing = PolicyOf(request.Existing);
        var snapshotId = Convert.FromHexString(request.SnapshotId);
        using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId, logger);
        var target = RestoreTargetProfile.ForLocalPlatform(catalogue, snapshotId, session.DeviceId);
        var plan = RestorePlanner.Plan(catalogue, snapshotId, PrefixesOf(request), target);
        if (plan.Items.Count == 0)
        {
            throw new CliFailureException(Strings.FormatDirectGateway_CatalogueKnowsNothingUnderSnapshot(request.SnapshotId));
        }

        // Judged as the volumes the run would write to take names, where one
        // takes them otherwise than this platform's (ADR-0170 Amendments 5
        // and 6).
        var rootSlices = SlicesOf(catalogue, snapshotId, plan, request);
        if (target.AsWrittenTo(rootSlices.Select(slice => slice.OutputDirectory)) is var written && !written.NamesAs(target))
        {
            target = written;
            plan = RestorePlanner.Plan(catalogue, snapshotId, PrefixesOf(request), target);
            rootSlices = SlicesOf(catalogue, snapshotId, plan, request);
        }

        // A resume reads nothing of a file its run landed but the manifest
        // (ADR-0132), nor what the parts it kept prove (ADR-0135).
        IReadOnlyList<RestoreSlice> slices =
        [
            .. rootSlices.Select(slice => new RestoreSlice(
                slice.Plan, slice.OutputDirectory, request.Resume ? slice.OutputDirectory : null, slice.LabelPrefix)),
        ];
        var held = request.Resume
            ? new RestoreHeld
            {
                Landed = RestoreSpace.LandedPaths(slices, RestoreDestinationMode.InPlace),
                PartsDirectory = DirectRestoreParts.Of(session.StateDirectory, slices.Select(slice => slice.OutputDirectory)),
            }
            : null;
        var probed = await RestoreBlobSet.ResolveAsync(
            catalogue, plan, session.Store, session.Repository.RepositoryId, session.Repository.Keys, held, cancellationToken)
            .ConfigureAwait(false);
        var space = await RestoreSpace.MeasureAsync(
            slices,
            RestoreDestinationMode.InPlace,
            existing,
            SpaceProbe(),
            RestoreSpace.WrittenBytesFrom(probed.Facts),
            cancellationToken).ConfigureAwait(false);

        var conflicts = RestorePlanFigures.Conflicts(plan);
        var destinationOf = RestoreMetadata.DestinationsOf(slices);
        var degradations = RestorePlanFigures.Degradations(plan, probed, target, destinationOf);
        var wider = RestoreMetadata.OpenedWider(plan, probed.Facts, target, destinationOf);
        return RestorePlanReport.Describe(
            new RestorePlanResult(
                plan.Items.Count(item => item.Kind != EntryKind.DirectoryPlaceholder),
                (long)plan.SpaceEstimateBytes,
                probed.Missing,
                conflicts.Count,
                conflicts.Count == 0 ? null : [.. conflicts.Take(20)],
                degradations.Count == 0 ? null : degradations,
                (long)RestorePlanFigures.WriteBytes(plan, probed),
                [
                    .. space.Needs.Select(need => new RestoreSpaceDescriptor(
                        need.Directory, (long)need.NeededBytes, need.AvailableBytes, need.Working)),
                ],
                (long)probed.ReadBytes,
                probed.Archived.Count == 0 ? null : probed.Archived,
                request.Resume ? space.Landed : null,
                wider is { Files: > 0 } ? wider.Files : null,
                wider is { OpenedFolders: > 0 } ? wider.OpenedFolders : null),
            request);
    }

    /// <summary>The subtrees a request asks, as the planner takes them: several, one, or none for all of it.</summary>
    private static IReadOnlyList<string> PrefixesOf(RestoreRequest request) =>
        request.Paths is { Count: > 0 } paths ? paths : request.Path is { Length: > 0 } path ? [path] : [];

    /// <summary>
    /// Where the run lands: the folder asked, or each root of the snapshot's
    /// set as this state's configuration has them, which is the file the
    /// service reads them from (ADR-0119). Refused before anything is read or
    /// written where nothing says where a file was captured.
    /// </summary>
    private IReadOnlyList<RestoreRootSlice> SlicesOf(
        Catalogue catalogue, byte[] snapshotId, RestorePlan plan, RestoreRequest request)
    {
        if (!request.ToOriginal)
        {
            return [new RestoreRootSlice(plan, request.OutputDirectory, string.Empty)];
        }

        var set = SetOf(catalogue, snapshotId)
            ?? throw new CliFailureException(Strings.FormatDirectGateway_OriginalSetNotConfigured(session.ConfigurationPath));

        return RestoreOriginalLocation.Slice(
                plan, [.. set.Roots.Select(root => new RestoreRoot(root.Path, root.Label))], out var unmapped)
            ?? throw new CliFailureException(Strings.FormatDirectGateway_OriginalLabelNotARoot(unmapped));
    }

    /// <summary>The set this state's configuration has the snapshot as a snapshot of, or null where it has none.</summary>
    private BackupSetConfiguration? SetOf(Catalogue catalogue, byte[] snapshotId)
    {
        var row = catalogue.EnumerateSnapshots()
            .FirstOrDefault(candidate => candidate.SnapshotId.Span.SequenceEqual(snapshotId));
        var setId = row is null ? null : Convert.ToHexStringLower(row.BackupSetId.Span);
        return setId is null
            ? null
            : ClientConfiguration.Load(session.ConfigurationPath, logger).BackupSets.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, setId, StringComparison.Ordinal));
    }

    /// <summary>
    /// The snapshot's set, for its receipt to name (ADR-0126, ADR-0127); null
    /// where this state's configuration has none or does not read, since naming
    /// a set never stops a restore.
    /// </summary>
    private BackupSetConfiguration? SetForReceipt(Catalogue catalogue, byte[] snapshotId)
    {
        try
        {
            return SetOf(catalogue, snapshotId);
        }
        catch (Exception exception) when (exception is ClientStateException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The existing-file policy asked, in the words the service takes it in, as the executor takes it.</summary>
    private static ExistingDestinationPolicy PolicyOf(string? existing) => existing switch
    {
        null => ExistingDestinationPolicy.Preserve,
        "rename" => ExistingDestinationPolicy.WriteBeside,
        "overwrite" => ExistingDestinationPolicy.Replace,
        _ => throw new CliFailureException(Strings.FormatDirectGateway_NotAnExistingFilePolicy(existing)),
    };

    /// <summary>How this machine says how much room a directory's volume has.</summary>
    /// <summary>
    /// Holds a marker for <paramref name="run"/> in every state directory a
    /// service would look in (ADR-0172 Amendment 6). One that cannot be held
    /// is passed over and said: the restore goes ahead, and a service working
    /// there may collect what it reads.
    /// </summary>
    private List<IDisposable> SayUnderWay(string run)
    {
        List<IDisposable> held = [];
        foreach (var directory in RestoreSeenFrom(session.StateDirectory, stateNamed))
        {
            try
            {
                held.Add(RestoresUnderWay.Hold(directory, session.Repository.RepositoryId, run));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (logger is not null)
                {
                    Log.RestoreNotSaidUnderWay(logger, new Domain.Diagnostics.LogPath(directory), exception.Message);
                }
            }
        }

        return held;
    }

    private RestoreSpaceProbe SpaceProbe() => new()
    {
        AvailableBytes = AvailableBytesInFlow ?? RestoreSpaceProbe.PlatformAvailableBytes,
        VolumeOf = VolumeOfInFlow ?? (path => FallbackPlan.Filesystem.Local.LocalFileSystemSource.TryStat(path, out var stat)
            ? stat.Device
            : null),

        // Every file a run here restores is assembled in a part (ADR-0135),
        // so that is where the room for the largest one is needed (ADR-0131).
        WorkingDirectory = DirectRestoreParts.RootUnder(session.StateDirectory),
    };

    /// <inheritdoc/>
    public ValueTask<OperationReport> SyncAsync(
        string? setName, string? destinationName, CancellationToken cancellationToken) =>
        // Fan-out belongs to the service: its scheduler owns the transfer
        // lane, its ledger records the outcome, and its runtime holds every
        // set's archive. A direct-mode copy would race all three.
        throw new CliFailureException(Strings.DirectGateway_SyncNeedsTheService);

    /// <inheritdoc/>
    public ValueTask<OperationReport> VerifyDestinationAsync(
        string? setName, string? destinationName, bool full, bool probe, CancellationToken cancellationToken) =>
        // Same reason, plus one of its own: the sweep's cursor and circuit
        // stamp live in the service's ledger, so a direct-mode pass would read
        // the same objects forever and never record that it had.
        throw new CliFailureException(Strings.DirectGateway_VerifyDestinationNeedsTheService);

    /// <inheritdoc/>
    public ValueTask<OperationReport> NoticeNamesAsync(string noticeId, CancellationToken cancellationToken) =>
        // The notices are the service's ledger: nothing here was raised.
        throw new CliFailureException(Strings.DirectGateway_NoticeNamesNeedsTheService);

    /// <inheritdoc/>
    public ValueTask<OperationReport> RestoreRunsAsync(CancellationToken cancellationToken) =>
        // A run here lands in place and names none (ADR-0109): only the
        // service keeps its runs.
        throw new CliFailureException(Strings.DirectGateway_RestoreRunsNeedTheService);

    /// <inheritdoc/>
    public ValueTask<OperationReport> ResumeRestoreAsync(
        string run, bool ignoreFreeSpace, bool acknowledgeWiderAccess, CancellationToken cancellationToken) =>
        throw new CliFailureException(Strings.DirectGateway_RestoreRunsNeedTheService);

    /// <inheritdoc/>
    public ValueTask<OperationReport> PlanResumeRestoreAsync(string run, CancellationToken cancellationToken) =>
        throw new CliFailureException(Strings.DirectGateway_RestoreRunsNeedTheService);

    /// <inheritdoc/>
    public ValueTask<OperationReport> ForgetRestoreRunAsync(string run, CancellationToken cancellationToken) =>
        throw new CliFailureException(Strings.DirectGateway_RestoreRunsNeedTheService);

    /// <inheritdoc/>
    public ValueTask<OperationReport> ForgetRestoreRunsAsync(int? olderThanDays, CancellationToken cancellationToken) =>
        throw new CliFailureException(Strings.DirectGateway_RestoreRunsNeedTheService);

    /// <inheritdoc/>
    public ValueTask<OperationReport> DrillAsync(
        string? setName, string? destinationName, CancellationToken cancellationToken) =>
        // A drill's answer belongs on the service's ledger, beside the
        // schedule's, and raises or clears the service's notice; a drill run
        // here would prove something nobody else ever learns.
        throw new CliFailureException(Strings.DirectGateway_DrillNeedsTheService);

    /// <inheritdoc/>
    public ValueTask<OperationReport> PreviewSetChangesAsync(
        string? setName, int? sampleLimit, CancellationToken cancellationToken) =>
        // The rescan reads a set's catalogue, which the running service
        // holds open — a second direct open would race its writer.
        throw new CliFailureException(Strings.DirectGateway_ChangesNeedsTheService);

    /// <inheritdoc/>
    public ValueTask<OperationReport> RetentionAsync(bool apply, CancellationToken cancellationToken) =>
        // Retention belongs to the hub for the same reason: the gate reads
        // the sync ledger, the plan reads every set's configuration, and the
        // pass runs on the writer lane a console cannot hold.
        throw new CliFailureException(Strings.DirectGateway_RetentionNeedsTheService);

    /// <inheritdoc/>
    public ValueTask<OperationReport> DeleteSnapshotsAsync(
        string setName, IReadOnlyList<string> snapshotIds, bool apply, CancellationToken cancellationToken) =>
        // A deletion that stopped at the archive on this disk would leave
        // every copy holding the snapshot, and only the hub knows the copies.
        throw new CliFailureException(Strings.DirectGateway_DeleteSnapshotsNeedsTheService);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        session.Dispose();
        return ValueTask.CompletedTask;
    }

    private static VerifyLevel ParseLevel(string level) => level switch
    {
        "locator" => VerifyLevel.LocatorAndFooter,
        "digest" => VerifyLevel.FooterAndDigest,
        "records" => VerifyLevel.EveryRecord,
        _ => throw new CliFailureException(Strings.FormatDirectGateway_NotVerifyLevelLocatorDigest(level)),
    };

    private static string Hex(ReadOnlyMemory<byte> bytes) => Convert.ToHexString(bytes.Span).ToLowerInvariant();
}
