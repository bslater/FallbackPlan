using Bodu;

namespace FallbackPlan.Protocol;

/// <summary>
/// A replication receipt as read back from disk: the envelope, the parsed
/// statement, and whether the signature holds.
/// </summary>
/// <param name="Path">Where it was read from.</param>
/// <param name="Role">Which side filed it.</param>
/// <param name="FiledAtUnixMilliseconds">When it was filed, by the filer's clock.</param>
/// <param name="SignerPublicKey">The device key the file names as the signer, lower-hex.</param>
/// <param name="SignerFingerprint">That key's fingerprint, for display.</param>
/// <param name="Set">The commander's set name, when the commander filed it.</param>
/// <param name="Destination">The commander's destination name, when the commander filed it.</param>
/// <param name="Receipt">The statement, parsed from the signed bytes; null when they do not parse.</param>
/// <param name="Verified">Whether the signature verifies under the named signer over the signed bytes.</param>
/// <param name="Problem">Why the file could not be read as a receipt, or null.</param>
public sealed record FiledReplicationReceipt(
    string Path,
    DeletionReceiptRole Role,
    ulong FiledAtUnixMilliseconds,
    string SignerPublicKey,
    string SignerFingerprint,
    string? Set,
    string? Destination,
    ReplicationReceipt? Receipt,
    bool Verified,
    string? Problem);

/// <summary>
/// Where replication receipts live: one immutable document per push under
/// <c>receipts/replications/&lt;repository&gt;/</c> in a state directory
/// ([ADR-0064](../../docs/adr/0064-replication-receipts.md)), beside the
/// deletion receipts and filed through the same core. The destination files
/// what it signed; the commander files what it verified.
/// </summary>
public sealed class ReplicationReceiptStore
{
    /// <summary>The kind an envelope names for a replication receipt.</summary>
    public const string Kind = "replication";

    private readonly string _root;

    private ReplicationReceiptStore(string root) => _root = root;

    /// <summary>Opens the store under <paramref name="stateDirectory"/>; the directory is created on first filing.</summary>
    public static ReplicationReceiptStore Open(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        return new ReplicationReceiptStore(System.IO.Path.Combine(stateDirectory, "receipts", "replications"));
    }

    /// <summary>Files a receipt; see <see cref="DeletionReceiptStore.File"/> for the naming.</summary>
    /// <param name="role">Which side is filing.</param>
    /// <param name="signedBytes">The receipt's signed bytes.</param>
    /// <param name="signature">Its signature under <paramref name="signer"/>.</param>
    /// <param name="signer">The device that signed it.</param>
    /// <param name="set">The commander's set name, or null.</param>
    /// <param name="destination">The commander's destination name, or null.</param>
    /// <returns>The path filed.</returns>
    /// <exception cref="PeerProtocolException">The bytes are not a receipt.</exception>
    public string File(
        DeletionReceiptRole role,
        ReadOnlySpan<byte> signedBytes,
        ReadOnlySpan<byte> signature,
        PeerIdentity signer,
        string? set,
        string? destination)
    {
        ThrowHelper.ThrowIfNull(signer);
        var receipt = ReplicationReceipt.Parse(signedBytes);
        return PeerReceiptFiles.File(
            _root, Kind, role, signedBytes, signature, signer, set, destination,
            receipt.RepositoryId.Span, receipt.IssuedAtUnixMilliseconds, receipt.SessionId.Span);
    }

    /// <summary>
    /// Every filed receipt, newest first by the time the destination issued
    /// it; optionally only one repository's. A file that is not a receipt
    /// is reported with its problem rather than skipped.
    /// </summary>
    /// <param name="repositoryIdHex">The repository, lower-hex, or null for all.</param>
    public IReadOnlyList<FiledReplicationReceipt> List(string? repositoryIdHex = null) =>
    [
        .. PeerReceiptFiles.Read(_root, repositoryIdHex)
            .Select(Interpret)
            .OrderByDescending(entry => entry.Receipt?.IssuedAtUnixMilliseconds ?? 0)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal),
    ];

    private static FiledReplicationReceipt Interpret(PeerReceiptFiles.Reading reading)
    {
        ReplicationReceipt? receipt = null;
        var problem = reading.Problem;
        if (problem is null)
        {
            // Unlike a deletion, a replication receipt has never been filed
            // without a kind: an envelope naming none is somebody else's.
            if (reading.Kind != Kind)
            {
                problem = $"a {reading.Kind ?? DeletionReceiptStore.Kind} receipt filed among the replication receipts";
            }
            else
            {
                try
                {
                    receipt = ReplicationReceipt.Parse(reading.SignedBytes);
                }
                catch (PeerProtocolException exception)
                {
                    problem = exception.Message;
                }
            }
        }

        return new FiledReplicationReceipt(
            reading.Path, reading.Role, reading.FiledAtUnixMilliseconds, reading.SignerPublicKey,
            reading.SignerFingerprint, reading.Set, reading.Destination, receipt,
            reading.SignatureValid && receipt is not null, problem);
    }
}
