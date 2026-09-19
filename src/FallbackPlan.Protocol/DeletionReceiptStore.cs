using Bodu;

namespace FallbackPlan.Protocol;

/// <summary>Which side of a receipt exchange filed it — for every kind of peer receipt.</summary>
public enum DeletionReceiptRole
{
    /// <summary>The destination that acted and signed.</summary>
    Destination,

    /// <summary>The commander that instructed or pushed, received and verified.</summary>
    Commander,
}

/// <summary>
/// A deletion receipt as read back from disk: the envelope, the parsed
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
public sealed record FiledDeletionReceipt(
    string Path,
    DeletionReceiptRole Role,
    ulong FiledAtUnixMilliseconds,
    string SignerPublicKey,
    string SignerFingerprint,
    string? Set,
    string? Destination,
    DeletionReceipt? Receipt,
    bool Verified,
    string? Problem);

/// <summary>
/// Where deletion receipts live: one immutable document per instruction
/// under <c>receipts/deletions/&lt;repository&gt;/</c> in a state directory
/// ([ADR-0063](../../docs/adr/0063-deletion-receipts.md)). The destination
/// files what it signed; the commander files what it verified. Neither
/// depends on the other's copy. The filing itself is
/// <see cref="PeerReceiptFiles"/>, shared with the replication receipts
/// beside it.
/// </summary>
/// <remarks>
/// Every fact <see cref="List"/> reports about a deletion is parsed from the
/// signed bytes after the signature is checked, never taken from the
/// envelope, so a file edited after filing reads as unverified rather than
/// as a different attestation.
/// </remarks>
public sealed class DeletionReceiptStore
{
    /// <summary>The kind an envelope names for a deletion receipt.</summary>
    public const string Kind = "deletion";

    /// <summary>
    /// How long a deletion receipt is kept (NFR-OPS-008). Longer than a
    /// replication receipt's, and deliberately: each one attests a distinct
    /// irreversible act and is FR-GC-008's audit record, where a replication
    /// receipt attests what a peer holds now and is superseded by the next
    /// push's. At a daily retention run the ceiling is eleven years.
    /// </summary>
    public static ReceiptRetentionPolicy Policy { get; } = new(
        MinimumRetained: 8, MaximumRetained: 4096, RetainedDays: 365);

    private readonly string _root;

    private DeletionReceiptStore(string root) => _root = root;

    /// <summary>Opens the store under <paramref name="stateDirectory"/>; the directory is created on first filing.</summary>
    public static DeletionReceiptStore Open(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        return new DeletionReceiptStore(System.IO.Path.Combine(stateDirectory, "receipts", "deletions"));
    }

    /// <summary>
    /// Files a receipt. The name carries the repository, the issue time and
    /// the session, so the destination's and the commander's copies of one
    /// exchange share a name across the two machines.
    /// </summary>
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
        var receipt = DeletionReceipt.Parse(signedBytes);
        return PeerReceiptFiles.File(
            _root, Kind, role, signedBytes, signature, signer, set, destination,
            receipt.RepositoryId.Span, receipt.IssuedAtUnixMilliseconds, receipt.SessionId.Span, Policy);
    }

    /// <summary>
    /// Every filed receipt, newest first by the time the destination issued
    /// it; optionally only one repository's. A file that is not a receipt
    /// is reported with its problem rather than skipped.
    /// </summary>
    /// <param name="repositoryIdHex">The repository, lower-hex, or null for all.</param>
    /// <param name="limit">
    /// At most this many of the newest by the issue time their names carry,
    /// or null for every one. The limit bounds the reading and not only the
    /// answer, so a listing costs what was asked for rather than what has
    /// accumulated.
    /// </param>
    public IReadOnlyList<FiledDeletionReceipt> List(string? repositoryIdHex = null, int? limit = null) =>
    [
        .. PeerReceiptFiles.Read(_root, repositoryIdHex, limit)
            .Select(Interpret)
            .OrderByDescending(entry => entry.Receipt?.IssuedAtUnixMilliseconds ?? 0)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal),
    ];

    /// <summary>
    /// How many receipts are on file, counted from names alone — so it costs
    /// a listing and no reads, and a receipt that no longer parses is still
    /// counted as being on file.
    /// </summary>
    /// <param name="repositoryIdHex">The repository, lower-hex, or null for all.</param>
    public int Count(string? repositoryIdHex = null) => PeerReceiptFiles.Count(_root, repositoryIdHex);

    /// <summary>
    /// Applies a retention policy across every repository filed here, or one,
    /// and answers how many files went. Filing applies <see cref="Policy"/> to
    /// the repository it writes into, so this is what reaches a pair that has
    /// stopped filing — a set deleted, a pairing ended, a peer gone — and a
    /// pile left by a build that had no bound at all.
    /// </summary>
    /// <param name="policy">How many, and how long.</param>
    /// <param name="now">The clock.</param>
    /// <param name="repositoryIdHex">One repository, lower-hex, or null for all.</param>
    /// <returns>How many files were deleted.</returns>
    public int Sweep(ReceiptRetentionPolicy policy, DateTimeOffset now, string? repositoryIdHex = null) =>
        PeerReceiptFiles.Sweep(_root, policy, now, repositoryIdHex);

    /// <summary>Applies <see cref="Policy"/> across every repository filed here.</summary>
    /// <param name="now">The clock.</param>
    /// <returns>How many files were deleted.</returns>
    public int Sweep(DateTimeOffset now) => Sweep(Policy, now);

    private static FiledDeletionReceipt Interpret(PeerReceiptFiles.Reading reading)
    {
        DeletionReceipt? receipt = null;
        var problem = reading.Problem;
        if (problem is null)
        {
            // An envelope written before kinds existed names none and is a
            // deletion by where it sits.
            if (reading.Kind is not null && reading.Kind != Kind)
            {
                problem = $"a {reading.Kind} receipt filed among the deletion receipts";
            }
            else
            {
                try
                {
                    receipt = DeletionReceipt.Parse(reading.SignedBytes);
                }
                catch (PeerProtocolException exception)
                {
                    problem = exception.Message;
                }
            }
        }

        return new FiledDeletionReceipt(
            reading.Path, reading.Role, reading.FiledAtUnixMilliseconds, reading.SignerPublicKey,
            reading.SignerFingerprint, reading.Set, reading.Destination, receipt,
            reading.SignatureValid && receipt is not null, problem);
    }
}
