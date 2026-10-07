using System.Globalization;
using FallbackPlan.Api;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Retention;

namespace FallbackPlan.Agent;

/// <summary>
/// The <c>delete_snapshots</c> verb (FR-GC-013, contract 1.51,
/// [ADR-0080](../../docs/adr/0080-a-person-deletes-a-snapshot.md)).
/// </summary>
public sealed partial class ServiceCommandHandler
{
    /// <summary>What the audit record says asked, when no session put a name on the command.</summary>
    private const string UnnamedActor = "cli";

    /// <summary>
    /// Deletes snapshots a person named, from staging and every copy, or says
    /// what it would do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An apply runs in this order, and each step needs the one before. The
    /// request is written, and its audit record is the publication its grace
    /// waits for. Then every destination of the set converges under the grant,
    /// and the ledger records where each began. Then a pass that carries out
    /// requests and nothing else deletes each snapshot every copy has let go,
    /// and condemns what only it held. Last, a pass-closing audit record ends
    /// that garbage's grace, so a second pass removes it in the same command
    /// rather than at a retention run nobody may ever start.
    /// </para>
    /// <para>
    /// Nothing the set's policy would expire is touched: that is a retention
    /// run's to do, when someone asks for one. A copy that cannot be reached
    /// holds the deletion, and asking again finishes it, because the request
    /// stands and a second request for the same snapshot is a resume.
    /// </para>
    /// </remarks>
    private async ValueTask<ServiceResult> DeleteSnapshotsAsync(
        DeleteSnapshotsCommand command, CancellationToken cancellationToken)
    {
        var archives = await runtime.ExistingArchivesAsync(cancellationToken).ConfigureAwait(false);
        var match = archives.FirstOrDefault(pair => string.Equals(pair.Set.Id, command.SetId, StringComparison.OrdinalIgnoreCase));
        if (match.Set is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound,
                $"No backup set '{command.SetId}' has an archive here, so it holds no snapshot to delete.");
        }

        var (set, archive) = match;
        if (command.SnapshotIds is not { Count: > 0 })
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, "Name at least one snapshot to delete.");
        }

        var survey = await StagingMark.SurveyAsync(archive.Store, archive.Repository, cancellationToken)
            .ConfigureAwait(false);
        var named = new List<SurveyedSnapshot>();
        var unknown = new List<string>();
        foreach (var id in command.SnapshotIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = survey.Snapshots.FirstOrDefault(snapshot =>
                string.Equals(snapshot.Fact.SnapshotId, id, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                unknown.Add(id);
            }
            else
            {
                named.Add(found);
            }
        }

        if (unknown.Count > 0)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound,
                $"Set '{set.Name}' holds no snapshot {string.Join(", ", unknown)}, so nothing was requested.");
        }

        // The one rule a person cannot override: a set keeps something to
        // restore from. A request already standing counts as gone.
        var facts = survey.Snapshots.Select(snapshot => snapshot.Fact).ToList();
        if (SnapshotDeletion.WouldLeaveNothing(facts, [.. named.Select(snapshot => snapshot.Fact.SnapshotId)]))
        {
            var kind = facts.Any(fact => fact.IsComplete && fact.DeletionRequest is null) ? "complete snapshot" : "snapshot";
            return new ServiceError(
                ServiceErrorReason.Refused,
                $"Set '{set.Name}': that would leave the set no {kind} to restore from, so nothing was "
                + "requested. A set keeps at least one; delete the others, or take a new backup first.");
        }

        if (!command.Apply)
        {
            return new DeleteSnapshotsResult(
                set.Id,
                Applied: false,
                [.. named.Select(snapshot => new SnapshotDeletionOutcome(
                    snapshot.Fact.SnapshotId,
                    snapshot.Fact.DeletionRequest is null ? "would-delete" : "pending",
                    HoldersOf(set, snapshot.Fact)))],
                [.. named.Select(snapshot => HoldersOf(set, snapshot.Fact) is { Count: > 0 } holders
                    ? $"{set.Name}: would delete {Short(snapshot)} from staging and from {string.Join(", ", holders)}"
                    : $"{set.Name}: would delete {Short(snapshot)} from staging")]);
        }

        var (grant, _, refusal) = await OpenReclaimGrantAsync(
            set, archive, apply: true, command.ReclaimGrant, reclaimGrants: null, "deleting snapshots", cancellationToken)
            .ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        using (grant)
        {
            // A request every pass must honour has to be one every pass can
            // verify, and a pass without a grant verifies it against the public
            // half the credential carries. One that carries none could record a
            // request no scheduled sync would ever act on.
            if (archive.Repository.Descriptor.RequiredFeatures.Contains(RepositoryDescriptorCodec.FeatureReclaimAuthority)
                && archive.Repository.Credential.ReclaimPublicKey.IsEmpty)
            {
                return new ServiceError(
                    ServiceErrorReason.Refused,
                    $"Set '{set.Name}': this installation's credential predates the reclaim key's public half, which "
                    + "a deletion needs so that every pass can honour it (ADR-0055 §5). Re-provision the installation "
                    + "and ask again; nothing was requested.");
            }

            // The set gate, as retention takes it: a sync mid-flight and a
            // deletion must not interleave, and the writer lane must not stall
            // behind a sync that may run for hours.
            var gate = runtime.SetGate(set.Id);
            if (!gate.Wait(0, CancellationToken.None))
            {
                return new ServiceError(
                    ServiceErrorReason.Refused,
                    $"Set '{set.Name}' has a sync in flight; ask again when it finishes. Nothing was requested.");
            }

            try
            {
                return await ApplyDeletionAsync(command, set, archive, named, grant, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async ValueTask<ServiceResult> ApplyDeletionAsync(
        DeleteSnapshotsCommand command,
        Application.BackupSetConfiguration set,
        ArchiveHandle archive,
        IReadOnlyList<SurveyedSnapshot> named,
        Repository.Crypto.ReclaimAuthority? grant,
        CancellationToken cancellationToken)
    {
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var actor = command.Actor ?? UnnamedActor;
        var lines = new List<string>();

        // The set's catalogue on a connection of the command's own: a deletion
        // runs on the writer pool, whose other worker can be running this
        // set's backup through the archive's (ADR-0010 Amendment 5).
        using var catalogue = archive.OpenWritableCatalogue();

        var request = await SnapshotDeletion.RequestAsync(
            archive.Store, archive.Repository, runtime.Writer, archive.Sequence, named, actor, now,
            cancellationToken, grant).ConfigureAwait(false);
        lines.Add(request.SnapshotIds.Count > 0
            ? string.Create(CultureInfo.InvariantCulture,
                $"{set.Name}: deletion requested for {request.SnapshotIds.Count} snapshot(s) by {actor}")
            : $"{set.Name}: deletion already requested; carrying it on");

        if (grant is not null)
        {
            lines.AddRange(
                (await FanOut.ConvergeForDeletionAsync(runtime, set, archive, grant, now, cancellationToken)
                    .ConfigureAwait(false))
                .Select(line => $"{set.Name}: {line}"));
        }

        var report = await DeletionPassAsync(set, archive, catalogue, grant, now, cancellationToken).ConfigureAwait(false);
        lines.AddRange(report.Lines.Select(line => $"{set.Name}: {line}"));

        // What only the deleted snapshots held was condemned by that pass and
        // waits for a publication past it. A pass-closing audit record is
        // that publication, and the second pass removes it.
        if (report.Swept is { Deleted: > 0 } swept)
        {
            using (var journal = new JournalPublisher(
                archive.Store, archive.Repository.RepositoryId, runtime.Writer, archive.Repository.Credential,
                archive.Sequence))
            {
                await journal.PublishAsync(
                    JournalRecordKind.Audit,
                    new JournalPayload.Audit(AuditAction.GcPass, actor, (ulong)swept.Deleted),
                    now,
                    Math.Max(archive.Repository.CurrentDataGeneration.Value, archive.Repository.CurrentMetadataGeneration.Value),
                    cancellationToken).ConfigureAwait(false);
            }

            report = await DeletionPassAsync(set, archive, catalogue, grant, now, cancellationToken).ConfigureAwait(false);
            lines.AddRange(report.Lines.Select(line => $"{set.Name}: {line}"));
        }

        // The catalogue is a cache of the store: a snapshot deleted here is
        // one nothing should list, offer to restore, or trace damage to.
        var after = await StagingMark.SurveyAsync(archive.Store, archive.Repository, cancellationToken)
            .ConfigureAwait(false);
        var outcomes = new List<SnapshotDeletionOutcome>();
        foreach (var snapshot in named)
        {
            if (after.Snapshots.All(remaining => remaining.ManifestObjectId != snapshot.ManifestObjectId))
            {
                catalogue.ForgetSnapshot(snapshot.Manifest.SnapshotId.Span);
                outcomes.Add(new SnapshotDeletionOutcome(snapshot.Fact.SnapshotId, "deleted", []));
                continue;
            }

            var held = report.Held.FirstOrDefault(holding => holding.Snapshot.SnapshotId == snapshot.Fact.SnapshotId);
            outcomes.Add(new SnapshotDeletionOutcome(snapshot.Fact.SnapshotId, "pending", held?.AwaitingDestinations ?? []));
        }

        return new DeleteSnapshotsResult(set.Id, Applied: true, outcomes, lines);
    }

    /// <summary>A pass that carries out the set's requests and nothing else (ADR-0080).</summary>
    private async ValueTask<RetentionReport> DeletionPassAsync(
        Application.BackupSetConfiguration set,
        ArchiveHandle archive,
        Repository.Catalogue.Catalogue catalogue,
        Repository.Crypto.ReclaimAuthority? grant,
        ulong now,
        CancellationToken cancellationToken) =>
        await RetentionRunner.RunAsync(
            archive.Store,
            archive.Repository,
            set.Retention,
            set.Destinations,
            name => runtime.DestinationSync.Find(set.Id, name),
            name => TrimVerificationFor(name, archive),
            runtime.Writer,
            apply: true,
            now,
            cancellationToken,
            set.Name,
            runtime.LoggerFor(typeof(RetentionRunner)),
            grant,
            objectId => catalogue.ResolveLocation(objectId)?.BlobId,
            clockSkewMargin: runtime.Configuration.EffectiveClockSkewMargin,
            requestsOnly: true).ConfigureAwait(false);

    /// <summary>
    /// The destinations a deletion of <paramref name="snapshot"/> waits on:
    /// every declared one that has not converged since the request, or, with
    /// no request yet, every declared one, as the replication gate holds it.
    /// </summary>
    private List<string> HoldersOf(Application.BackupSetConfiguration set, SnapshotFact snapshot) =>
        [.. set.Destinations
            .Select(reference => reference.Ref)
            .Where(name => snapshot.DeletionRequest is not { } requested
                || (runtime.DestinationSync.Find(set.Id, name)?.ConvergedSequence ?? 0) < requested)];

    private static string Short(SurveyedSnapshot snapshot) => $"{snapshot.Fact.SnapshotId[..12]}…";
}
