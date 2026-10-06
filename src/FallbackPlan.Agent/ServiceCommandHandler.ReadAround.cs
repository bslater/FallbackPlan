using FallbackPlan.Application;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;

namespace FallbackPlan.Agent;

public sealed partial class ServiceCommandHandler
{
    /// <summary>
    /// Puts what a restore found while reading around damage where the rest
    /// of the service acts on it (FR-RST-007). A destination's damaged
    /// objects go on its ledger row as the deep sweep's do, so the next sync
    /// replaces a local path's from a sound copy (FR-VER-007) and a peer's are
    /// held until its owner removes them (ADR-0035 Amendment 2); either way
    /// the pair is failed (FR-VER-005) and a notice says what was found. The
    /// staging archive has no ledger row and nothing repairs it in place, so
    /// its damage is a notice alone.
    /// </summary>
    /// <remarks>
    /// Damage only. A copy that would not read, or did not hold a blob, is
    /// not shown to be altered, and a restore that said otherwise would
    /// condemn a device for going away (ADR-0035 Amendment 1).
    /// </remarks>
    private void RecordReadAroundFindings(
        BackupSetConfiguration set, ArchiveHandle archive, SetCopies copies, RepositoryReader reader, ulong nowMs)
    {
        var repositoryId = archive.Repository.RepositoryId;
        var passedOver = reader.ReadAround.SelectMany(around => around.PassedOver).ToHashSet();
        foreach (var found in reader.Refusals
            .Where(refusal => refusal is { Fault: CopyFault.Damaged, BlobKey: not null })
            .GroupBy(refusal => refusal.Source, StringComparer.Ordinal))
        {
            List<string> keys = [.. found.Select(refusal => refusal.BlobKey!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

            // Said by what needs them, not only by their keys (FR-VER-005):
            // everything that copy could not give back on its own, counted,
            // with the files kept beside the notice for whoever unlocks the
            // set (ADR-0089 Amendment 1).
            var reach = archive.TraceDamage(keys);
            var names = DamageReachText.Names(set.Id, reach);
            var what = $"{keys.Count} object(s) that no longer match what was sealed: {found.First().Detail.TrimEnd('.')}. "
                + DamageReachText.Sentences(reach);
            var after = found.All(passedOver.Contains)
                ? "The files that needed them were read from another copy of the set instead, and verified. "
                : "Files that no other copy of the set held sound did not restore. ";

            if (string.Equals(found.Key, SetCopies.StagingName, StringComparison.Ordinal))
            {
                runtime.Notices.Raise(
                    $"restore-found-damage:{set.Id}:staging",
                    $"A restore found the staging archive of set '{set.Name}' holding {what} {after}Nothing here "
                    + "repairs a staging archive in place, so those objects stay as they are: check the disk that "
                    + "holds it, and the filesystem, before counting on it.",
                    nowMs,
                    names);
                continue;
            }

            if (copies.DestinationNamed(found.Key) is not { } destination)
            {
                continue;
            }

            // On the ledger before anything is said, as the sweep does, so
            // the sync that re-checks the pair has the keys to re-check.
            var ledger = runtime.DestinationSync;
            var outstanding = ledger.RecordDamage(set.Id, destination.Name, keys, resolved: [], nowMs).DamagedKeys ?? keys;
            ledger.RecordFailure(
                set.Id, destination.Name, DestinationSyncState.Failed,
                $"a restore found {keys.Count} damaged object(s) here: {found.First().Detail}; "
                + DestinationSyncStore.DamageStatement(outstanding),
                nowMs,
                damageOnly: true);

            var said = $"A restore found destination '{destination.Name}' of set '{set.Name}' holding {what} {after}";
            runtime.Notices.Raise(
                $"restore-found-damage:{set.Id}:{destination.Name}",
                destination.Kind == DestinationKind.Peer
                    ? said + "A peer's replica cannot be repaired from here — this installation can read what it "
                        + "holds but not replace it — so those bytes cannot be restored from there until they are "
                        + "gone. Ask the owner of that machine to remove them from its replica of repository "
                        + $"{repositoryId}: {string.Join(", ", keys)}; the next sync sends them again whole. Its "
                        + "storage altered a backup once and may again."
                    : said + "The next sync replaces them from a sound copy and re-verifies them where they land. "
                        + "Its storage altered a backup once and may again: check the device, the filesystem, and "
                        + "anything else that writes there before counting on it.",
                nowMs,
                names);
        }
    }
}
