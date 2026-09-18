using System.Text.Json;
using System.Text.Json.Serialization;
using Bodu;

namespace FallbackPlan.Protocol;

/// <summary>
/// The reader's view of filed deletion receipts
/// ([ADR-0063](../../docs/adr/0063-deletion-receipts.md)): one rendering
/// shared by every host that offers a <c>receipts</c> verb, so the agent and
/// the CLI print the same thing from the same bytes. Every attested fact
/// comes from the signed statement; the envelope contributes only where the
/// file sits, who this side says signed it, and the names this side filed it
/// under — and the status says whether the signature holds over the bytes
/// on disk now, not whether it held when they were filed.
/// </summary>
public static class DeletionReceiptReport
{
    /// <summary>How many removed keys a text rendering lists before summarising the rest.</summary>
    public const int ListedKeysShown = 8;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Reads a repository id as a person typed it: 32 hex digits, either
    /// case, answered lower-case as the store keys its directories.
    /// </summary>
    /// <param name="text">What was typed.</param>
    /// <param name="repositoryIdHex">The store's key for it.</param>
    /// <returns><see langword="true"/> when it is one.</returns>
    public static bool TryParseRepositoryId(string? text, out string repositoryIdHex)
    {
        repositoryIdHex = string.Empty;
        if (text is null || text.Length != 2 * ReplicationOffer.RepositoryIdLength)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        repositoryIdHex = text.ToLowerInvariant();
        return true;
    }

    /// <summary>Writes the receipts for a person, newest first as listed.</summary>
    /// <param name="output">Where to write.</param>
    /// <param name="receipts">What the store listed.</param>
    public static void Write(TextWriter output, IReadOnlyList<FiledDeletionReceipt> receipts)
    {
        ThrowHelper.ThrowIfNull(output);
        ThrowHelper.ThrowIfNull(receipts);

        if (receipts.Count == 0)
        {
            output.WriteLine("no deletion receipts.");
            return;
        }

        var first = true;
        foreach (var filed in receipts)
        {
            if (!first)
            {
                output.WriteLine();
            }

            first = false;
            WriteOne(output, filed);
        }
    }

    /// <summary>The same receipts as a JSON array, for a script.</summary>
    /// <param name="receipts">What the store listed.</param>
    /// <returns>Indented JSON.</returns>
    public static string ToJson(IReadOnlyList<FiledDeletionReceipt> receipts)
    {
        ThrowHelper.ThrowIfNull(receipts);
        return JsonSerializer.Serialize(receipts.Select(Project).ToList(), SerializerOptions);
    }

    /// <summary>Writes one deletion receipt.</summary>
    internal static void WriteOne(TextWriter output, FiledDeletionReceipt filed)
    {
        var filedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)filed.FiledAtUnixMilliseconds);
        output.WriteLine($"{filedAt:u}  {Status(filed)}  {Role(filed.Role)}  deletion");
        output.WriteLine($"  file:        {filed.Path}");
        if (filed.Set is not null || filed.Destination is not null)
        {
            output.WriteLine($"  set:         {filed.Set ?? "?"} -> {filed.Destination ?? "?"}");
        }

        output.WriteLine($"  signer:      {filed.SignerFingerprint}");
        if (filed.Receipt is not { } receipt)
        {
            output.WriteLine($"  problem:     {filed.Problem}");
            return;
        }

        var issued = DateTimeOffset.FromUnixTimeMilliseconds((long)receipt.IssuedAtUnixMilliseconds);
        output.WriteLine($"  repository:  {Convert.ToHexStringLower(receipt.RepositoryId.Span)}");
        output.WriteLine($"  session:     {Convert.ToHexStringLower(receipt.SessionId.Span[..8])}...");
        output.WriteLine($"  commander:   {PeerIdentity.FromPublicKey(receipt.CommanderPublicKey.Span).Fingerprint}");
        output.WriteLine(
            $"  issued:      {issued:u}   floor: {receipt.FloorGenerations} generation(s)   "
            + $"reclaim key: {(receipt.ReclaimPublicKey.IsEmpty ? "none recorded" : "in force")}   "
            + $"pages: {receipt.PageDigests.Count}");
        output.WriteLine($"  deleted:     {receipt.DeletedCount} object(s), {receipt.NotHeld} not held");
        foreach (var key in receipt.Deleted.Take(ListedKeysShown))
        {
            output.WriteLine($"    {key}");
        }

        if (receipt.Deleted.Count > ListedKeysShown)
        {
            output.WriteLine($"    ... and {receipt.Deleted.Count - ListedKeysShown} more listed");
        }

        if (receipt.DeletedCount > (ulong)receipt.Deleted.Count)
        {
            output.WriteLine($"    ... and {receipt.DeletedCount - (ulong)receipt.Deleted.Count} more counted but not listed");
        }

        if (filed.Problem is { } problem)
        {
            output.WriteLine($"  problem:     {problem}");
        }
    }

    private static string Status(FiledDeletionReceipt filed) =>
        filed.Receipt is null ? "unreadable" : filed.Verified ? "verified" : "SIGNATURE INVALID";

    internal static string Role(DeletionReceiptRole role) =>
        role == DeletionReceiptRole.Destination ? "destination" : "commander";

    internal static Entry Project(FiledDeletionReceipt filed) => new()
    {
        Kind = DeletionReceiptStore.Kind,
        Path = filed.Path,
        Role = Role(filed.Role),
        FiledAt = filed.FiledAtUnixMilliseconds,
        Verified = filed.Verified,
        Status = Status(filed),
        Problem = filed.Problem,
        SignerPublicKey = filed.SignerPublicKey,
        SignerFingerprint = filed.SignerFingerprint,
        Set = filed.Set,
        Destination = filed.Destination,
        Receipt = filed.Receipt is { } receipt
            ? new Attested
            {
                SessionId = Convert.ToHexStringLower(receipt.SessionId.Span),
                RepositoryId = Convert.ToHexStringLower(receipt.RepositoryId.Span),
                CommanderPublicKey = Convert.ToHexStringLower(receipt.CommanderPublicKey.Span),
                CommanderFingerprint = PeerIdentity.FromPublicKey(receipt.CommanderPublicKey.Span).Fingerprint,
                IssuedAt = receipt.IssuedAtUnixMilliseconds,
                FloorGenerations = receipt.FloorGenerations,
                ReclaimPublicKey = receipt.ReclaimPublicKey.IsEmpty
                    ? null
                    : Convert.ToHexStringLower(receipt.ReclaimPublicKey.Span),
                PageDigests = [.. receipt.PageDigests.Select(digest => Convert.ToHexStringLower(digest.Span))],
                DeletedCount = receipt.DeletedCount,
                Deleted = receipt.Deleted,
                NotHeld = receipt.NotHeld,
            }
            : null,
    };

    internal sealed class Entry
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("role")]
        public string? Role { get; init; }

        [JsonPropertyName("filed_at")]
        public ulong FiledAt { get; init; }

        [JsonPropertyName("verified")]
        public bool Verified { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("problem")]
        public string? Problem { get; init; }

        [JsonPropertyName("signer_public_key")]
        public string? SignerPublicKey { get; init; }

        [JsonPropertyName("signer_fingerprint")]
        public string? SignerFingerprint { get; init; }

        [JsonPropertyName("set")]
        public string? Set { get; init; }

        [JsonPropertyName("destination")]
        public string? Destination { get; init; }

        [JsonPropertyName("receipt")]
        public Attested? Receipt { get; init; }
    }

    internal sealed class Attested
    {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; init; }

        [JsonPropertyName("repository_id")]
        public string? RepositoryId { get; init; }

        [JsonPropertyName("commander_public_key")]
        public string? CommanderPublicKey { get; init; }

        [JsonPropertyName("commander_fingerprint")]
        public string? CommanderFingerprint { get; init; }

        [JsonPropertyName("issued_at")]
        public ulong IssuedAt { get; init; }

        [JsonPropertyName("floor_generations")]
        public uint FloorGenerations { get; init; }

        [JsonPropertyName("reclaim_public_key")]
        public string? ReclaimPublicKey { get; init; }

        [JsonPropertyName("page_digests")]
        public IReadOnlyList<string>? PageDigests { get; init; }

        [JsonPropertyName("deleted_count")]
        public ulong DeletedCount { get; init; }

        [JsonPropertyName("deleted")]
        public IReadOnlyList<string>? Deleted { get; init; }

        [JsonPropertyName("not_held")]
        public uint NotHeld { get; init; }
    }
}
