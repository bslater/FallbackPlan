using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Cli;

/// <summary>
/// The CLI's half of the passphrase gate (FR-WOR-007, ADR-0089; ADR-0042 §5):
/// a restore grant per set, derived here from the passphrase
/// <c>--passphrase-env</c> names under the facts the service publishes, proved
/// against each set's sealing key before anything is sent, and a restore
/// source opened under the grant of the set asked about. That source is what
/// the service asks of any verb that names a backup's files — the console's
/// ceremony, at the shell.
/// </summary>
internal static class GrantedSources
{
    /// <summary>
    /// Opens a source of the first set <paramref name="wanted"/> accepts whose
    /// grant the passphrase derives.
    /// </summary>
    /// <param name="client">The service.</param>
    /// <param name="passphraseEnvironmentVariable">The variable naming the passphrase; null when none was named.</param>
    /// <param name="what">What was asked, as a sentence's subject, for the refusals.</param>
    /// <param name="wanted">Which sets may answer, in the service's order.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The opened source.</returns>
    /// <exception cref="CliFailureException">No passphrase named, none published to derive under, or it opens no wanted set.</exception>
    public static async ValueTask<RestoreSourceOpenedResult> OpenAsync(
        IFallbackPlanClient client,
        string? passphraseEnvironmentVariable,
        string what,
        Func<BackupSetDescriptor, bool> wanted,
        CancellationToken cancellationToken)
    {
        var opened = await OpenFirstAsync(
            client, passphraseEnvironmentVariable, what, wanted, _ => true, cancellationToken).ConfigureAwait(false);
        return opened ?? throw new CliFailureException($"{what}: no backup set here answers to that name.");
    }

    /// <summary>
    /// Opens a source of whichever set's archive holds <paramref name="snapshotId"/>:
    /// a snapshot names no set a client can rely on — one a direct-mode backup
    /// wrote carries the archive's own identity — so each set the passphrase
    /// opens is tried in turn, and every source opened on the way is closed.
    /// </summary>
    /// <param name="client">The service.</param>
    /// <param name="passphraseEnvironmentVariable">The variable naming the passphrase; null when none was named.</param>
    /// <param name="what">What was asked, as a sentence's subject, for the refusals.</param>
    /// <param name="snapshotId">The snapshot, hex.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The opened source.</returns>
    /// <exception cref="CliFailureException">No passphrase named, it opens no set, or no set holds the snapshot.</exception>
    public static async ValueTask<RestoreSourceOpenedResult> OpenHoldingAsync(
        IFallbackPlanClient client,
        string? passphraseEnvironmentVariable,
        string what,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        var opened = await OpenFirstAsync(
            client, passphraseEnvironmentVariable, what, _ => true,
            source => source.Snapshots.Any(
                candidate => string.Equals(candidate.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase)),
            cancellationToken).ConfigureAwait(false);
        return opened ?? throw new CliFailureException($"no configured set's archive holds snapshot '{snapshotId}'.");
    }

    /// <summary>Closes a source, best effort: the service's idle sweep reclaims one this never reaches.</summary>
    /// <param name="client">The service.</param>
    /// <param name="sourceId">The source.</param>
    public static async ValueTask CloseAsync(IFallbackPlanClient client, string sourceId)
    {
        try
        {
            await client.ExecuteAsync(new CloseRestoreSourceCommand(sourceId), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ServiceConnectionException)
        {
        }
    }

    private static async ValueTask<RestoreSourceOpenedResult?> OpenFirstAsync(
        IFallbackPlanClient client,
        string? passphraseEnvironmentVariable,
        string what,
        Func<BackupSetDescriptor, bool> wanted,
        Func<RestoreSourceOpenedResult, bool> fits,
        CancellationToken cancellationToken)
    {
        if (passphraseEnvironmentVariable is null)
        {
            throw new CliFailureException(
                $"{what} names the files a backup holds, so it needs the passphrase: name --passphrase-env <VAR>, "
                + "the environment variable holding it, so a restore grant can be derived here (FR-WOR-007).");
        }

        var description = await AskAsync<ServiceDescriptionResult>(
            client, new DescribeServiceCommand(), cancellationToken).ConfigureAwait(false);
        if (description.RestoreGrantRecipient is not { Length: > 0 } recipientHex)
        {
            throw new CliFailureException(
                "the service publishes no grant-recipient key to seal a restore grant to — finish its setup first "
                + "(ADR-0044).");
        }

        var sets = await AskAsync<BackupSetsResult>(client, new ListBackupSetsCommand(), cancellationToken)
            .ConfigureAwait(false);

        // One derivation per distinct salt and cost, so the ordinary
        // installation runs Argon2id once; a set adopted from a destination
        // keeps the salt it was born under (ADR-0061) and is derived under it.
        var recipient = Convert.FromHexString(recipientHex);
        var bySalt = new Dictionary<string, (byte[] SealingPublicKey, string Grant)>(StringComparer.OrdinalIgnoreCase);
        using var passphrase = CliSession.ReadPassphrase(passphraseEnvironmentVariable);
        var derivedAny = false;
        var openedAny = false;
        foreach (var set in sets.Sets.Where(wanted))
        {
            if (FactsOf(set, description) is not { } facts)
            {
                continue;
            }

            derivedAny = true;
            var key = $"{facts.Salt}/{facts.Parameters.MemoryKiB}/{facts.Parameters.Iterations}/{facts.Parameters.Parallelism}";
            if (!bySalt.TryGetValue(key, out var derived))
            {
                using var authority = WriteOnlyDerivation.Derive(
                    passphrase, facts.Parameters, Convert.FromHexString(facts.Salt), KdfValidationMode.OpenRepository);
                derived = (
                    authority.Credential.SealingPublicKey.ToArray(),
                    Convert.ToHexStringLower(WriteOnlyProvisioning.SealGrant(recipient, authority.SealingPrivateKey)));
                bySalt[key] = derived;
            }

            if (!derived.SealingPublicKey.AsSpan().SequenceEqual(Convert.FromHexString(facts.SealingPublicKey)))
            {
                // Not this set's passphrase; the next may be adopted from
                // elsewhere and answer to it.
                continue;
            }

            openedAny = true;
            if (await client.ExecuteAsync(
                    new OpenRestoreSourceCommand(set.Name, Envelope: derived.Grant), cancellationToken).ConfigureAwait(false)
                is not RestoreSourceOpenedResult opened)
            {
                continue;
            }

            if (fits(opened))
            {
                return opened;
            }

            await CloseAsync(client, opened.SourceId).ConfigureAwait(false);
        }

        if (derivedAny && !openedAny)
        {
            throw new CliFailureException(
                "the passphrase does not reproduce this installation's credential — nothing was sent.");
        }

        return null;
    }

    /// <summary>The facts a set derives under: its own when it publishes them, the installation's otherwise.</summary>
    private static (string Salt, Argon2Parameters Parameters, string SealingPublicKey)? FactsOf(
        BackupSetDescriptor set, ServiceDescriptionResult description) =>
        set is { KdfSalt.Length: > 0, KdfMemoryKib: { } memory, KdfIterations: { } iterations, KdfParallelism: { } lanes, SealingPublicKey.Length: > 0 }
            ? (set.KdfSalt, new Argon2Parameters { MemoryKiB = memory, Iterations = iterations, Parallelism = lanes }, set.SealingPublicKey)
            : description is { KdfSalt.Length: > 0, KdfMemoryKib: { } installationMemory, KdfIterations: { } installationIterations, KdfParallelism: { } installationLanes, SealingPublicKey.Length: > 0 }
                ? (description.KdfSalt,
                    new Argon2Parameters { MemoryKiB = installationMemory, Iterations = installationIterations, Parallelism = installationLanes },
                    description.SealingPublicKey)
                : null;

    private static async ValueTask<TResult> AskAsync<TResult>(
        IFallbackPlanClient client, ServiceCommand command, CancellationToken cancellationToken)
        where TResult : ServiceResult
    {
        var result = await client.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            ServiceError error => throw new CliFailureException(error.Message),
            TResult expected => expected,
            _ => throw new CliFailureException($"the service answered with {result.GetType().Name}."),
        };
    }
}
