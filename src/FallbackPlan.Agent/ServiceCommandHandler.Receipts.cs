using FallbackPlan.Api;
using FallbackPlan.Protocol;

namespace FallbackPlan.Agent;

/// <summary>
/// The receipts listing (contract 1.33): the deletion receipts
/// ([ADR-0063](../../docs/adr/0063-deletion-receipts.md)) and replication
/// receipts ([ADR-0064](../../docs/adr/0064-replication-receipts.md)) filed
/// under the state directory, read through the same two stores the
/// <c>receipts</c> verbs read and answered as facts. The signature is
/// re-checked over the bytes on disk as each file is read, so the status a
/// row carries is the service's verdict now, not what was true at filing.
/// </summary>
public sealed partial class ServiceCommandHandler
{
    /// <summary>How many bytes of the session identifier a row shows, as hex.</summary>
    private const int SessionPrefixBytes = 8;

    private ServiceResult ListReceipts(ListReceiptsCommand command)
    {
        var deletions = true;
        var replications = true;
        switch (command.Kind)
        {
            case null:
                break;
            case DeletionReceiptStore.Kind:
                replications = false;
                break;
            case ReplicationReceiptStore.Kind:
                deletions = false;
                break;
            default:
                return new ServiceError(
                    ServiceErrorReason.InvalidArgument,
                    $"'{command.Kind}' is not a receipt kind ({DeletionReceiptStore.Kind} | {ReplicationReceiptStore.Kind}).");
        }

        string? repositoryIdHex = null;
        if (command.Repository is not null
            && !DeletionReceiptReport.TryParseRepositoryId(command.Repository, out repositoryIdHex))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument, "repository takes the repository id as 32 hex digits.");
        }

        if (command.Limit is <= 0)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, "limit must be at least 1 when given.");
        }

        var state = runtime.Options.StateDirectory;
        var deletionStore = DeletionReceiptStore.Open(state);
        var replicationStore = ReplicationReceiptStore.Open(state);

        // The limit reaches each store, which applies it to file names before
        // it reads anything (contract 1.35). Asking each for the limit and
        // then taking the limit from the merge is right: the newest `limit`
        // of a union is a subset of the union of each side's newest `limit`.
        var total = 0;
        var rows = new List<(ulong IssuedAt, string Path, ReceiptDescriptor Row)>();
        if (deletions)
        {
            total += deletionStore.Count(repositoryIdHex);
            foreach (var filed in deletionStore.List(repositoryIdHex, command.Limit))
            {
                rows.Add((filed.Receipt?.IssuedAtUnixMilliseconds ?? 0, filed.Path, Describe(filed)));
            }
        }

        if (replications)
        {
            total += replicationStore.Count(repositoryIdHex);
            foreach (var filed in replicationStore.List(repositoryIdHex, command.Limit))
            {
                rows.Add((filed.Receipt?.IssuedAtUnixMilliseconds ?? 0, filed.Path, Describe(filed)));
            }
        }

        IEnumerable<(ulong IssuedAt, string Path, ReceiptDescriptor Row)> ordered = rows
            .OrderByDescending(entry => entry.IssuedAt)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal);

        // The set is inside the signed bytes, so this narrows what was read
        // and never what was counted: a listing by set can come back shorter
        // than its limit while the total stands above both, which the
        // contract states rather than leaving a client to draw a ratio that
        // does not close.
        if (command.Set is { } set)
        {
            ordered = ordered.Where(entry => string.Equals(entry.Row.Set, set, StringComparison.Ordinal));
        }

        if (command.Limit is { } limit)
        {
            ordered = ordered.Take(limit);
        }

        return new ReceiptsResult([.. ordered.Select(entry => entry.Row)], total);
    }

    private static ReceiptDescriptor Describe(FiledDeletionReceipt filed) => new(
        Kind: DeletionReceiptStore.Kind,
        Role: RoleName(filed.Role),
        FiledAt: filed.FiledAtUnixMilliseconds,
        Status: StatusName(filed.Receipt is not null, filed.Verified),
        Verified: filed.Verified,
        Problem: filed.Problem,
        SignerFingerprint: filed.SignerFingerprint,
        Set: filed.Set,
        Destination: filed.Destination,
        RepositoryId: filed.Receipt is { } receipt ? Convert.ToHexStringLower(receipt.RepositoryId.Span) : null,
        IssuedAt: filed.Receipt?.IssuedAtUnixMilliseconds,
        SessionPrefix: filed.Receipt is { } session ? SessionPrefix(session.SessionId) : null,
        DeletedCount: filed.Receipt?.DeletedCount,
        NotHeld: filed.Receipt?.NotHeld,
        CommittedCount: null,
        HeldObjects: null,
        HeldBytes: null);

    private static ReceiptDescriptor Describe(FiledReplicationReceipt filed) => new(
        Kind: ReplicationReceiptStore.Kind,
        Role: RoleName(filed.Role),
        FiledAt: filed.FiledAtUnixMilliseconds,
        Status: StatusName(filed.Receipt is not null, filed.Verified),
        Verified: filed.Verified,
        Problem: filed.Problem,
        SignerFingerprint: filed.SignerFingerprint,
        Set: filed.Set,
        Destination: filed.Destination,
        RepositoryId: filed.Receipt is { } receipt ? Convert.ToHexStringLower(receipt.RepositoryId.Span) : null,
        IssuedAt: filed.Receipt?.IssuedAtUnixMilliseconds,
        SessionPrefix: filed.Receipt is { } session ? SessionPrefix(session.SessionId) : null,
        DeletedCount: null,
        NotHeld: null,
        CommittedCount: filed.Receipt?.CommittedCount,
        HeldObjects: filed.Receipt?.HeldObjects,
        HeldBytes: filed.Receipt?.HeldBytes);

    private static string RoleName(DeletionReceiptRole role) =>
        role == DeletionReceiptRole.Destination ? "destination" : "commander";

    private static string StatusName(bool readable, bool verified) =>
        !readable ? "unreadable" : verified ? "verified" : "signature-invalid";

    private static string SessionPrefix(ReadOnlyMemory<byte> sessionId) =>
        Convert.ToHexStringLower(sessionId.Span[..Math.Min(SessionPrefixBytes, sessionId.Length)]);
}
