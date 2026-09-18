using System.Text.Json;
using System.Text.Json.Serialization;
using Bodu;

namespace FallbackPlan.Protocol;

/// <summary>
/// The reader's view of every kind of filed peer receipt — deletion
/// ([ADR-0063](../../docs/adr/0063-deletion-receipts.md)) and replication
/// ([ADR-0064](../../docs/adr/0064-replication-receipts.md)) — interleaved
/// newest first, rendered by one routine so the agent and the CLI print the
/// same thing from the same bytes. Every attested fact comes from the signed
/// statement; the envelope contributes only where the file sits, who this
/// side says signed it, and the names this side filed it under — and the
/// status says whether the signature holds over the bytes on disk now.
/// </summary>
public static class ReceiptReport
{
    /// <summary>How many keys a text rendering lists before summarising the rest.</summary>
    public const int ListedKeysShown = DeletionReceiptReport.ListedKeysShown;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes both kinds for a person, newest first by issue time.</summary>
    /// <param name="output">Where to write.</param>
    /// <param name="deletions">What the deletion store listed.</param>
    /// <param name="replications">What the replication store listed.</param>
    public static void Write(
        TextWriter output,
        IReadOnlyList<FiledDeletionReceipt> deletions,
        IReadOnlyList<FiledReplicationReceipt> replications)
    {
        ThrowHelper.ThrowIfNull(output);
        ThrowHelper.ThrowIfNull(deletions);
        ThrowHelper.ThrowIfNull(replications);

        var entries = Interleave(deletions, replications);
        if (entries.Count == 0)
        {
            output.WriteLine("no receipts.");
            return;
        }

        var first = true;
        foreach (var entry in entries)
        {
            if (!first)
            {
                output.WriteLine();
            }

            first = false;
            if (entry.Deletion is { } deletion)
            {
                DeletionReceiptReport.WriteOne(output, deletion);
            }
            else
            {
                WriteOne(output, entry.Replication!);
            }
        }
    }

    /// <summary>Both kinds as one JSON array, each entry naming its kind, for a script.</summary>
    /// <param name="deletions">What the deletion store listed.</param>
    /// <param name="replications">What the replication store listed.</param>
    /// <returns>Indented JSON.</returns>
    public static string ToJson(
        IReadOnlyList<FiledDeletionReceipt> deletions, IReadOnlyList<FiledReplicationReceipt> replications)
    {
        ThrowHelper.ThrowIfNull(deletions);
        ThrowHelper.ThrowIfNull(replications);

        var projected = Interleave(deletions, replications)
            .Select(entry => entry.Deletion is { } deletion
                ? (object)DeletionReceiptReport.Project(deletion)
                : Project(entry.Replication!))
            .ToList();
        return JsonSerializer.Serialize(projected, SerializerOptions);
    }

    /// <summary>Writes one replication receipt.</summary>
    internal static void WriteOne(TextWriter output, FiledReplicationReceipt filed)
    {
        var filedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)filed.FiledAtUnixMilliseconds);
        output.WriteLine($"{filedAt:u}  {Status(filed)}  {DeletionReceiptReport.Role(filed.Role)}  replication");
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
        output.WriteLine($"  issued:      {issued:u}");
        output.WriteLine(
            $"  committed:   {receipt.CommittedCount} object(s) this session; holds {receipt.HeldObjects} object(s), "
            + $"{receipt.HeldBytes} bytes");
        foreach (var key in receipt.Committed.Take(ListedKeysShown))
        {
            output.WriteLine($"    {key}");
        }

        if (receipt.Committed.Count > ListedKeysShown)
        {
            output.WriteLine($"    ... and {receipt.Committed.Count - ListedKeysShown} more listed");
        }

        if (receipt.CommittedCount > (ulong)receipt.Committed.Count)
        {
            output.WriteLine($"    ... and {receipt.CommittedCount - (ulong)receipt.Committed.Count} more counted but not listed");
        }

        if (filed.Problem is { } problem)
        {
            output.WriteLine($"  problem:     {problem}");
        }
    }

    private static string Status(FiledReplicationReceipt filed) =>
        filed.Receipt is null ? "unreadable" : filed.Verified ? "verified" : "SIGNATURE INVALID";

    private static List<Interleaved> Interleave(
        IReadOnlyList<FiledDeletionReceipt> deletions, IReadOnlyList<FiledReplicationReceipt> replications) =>
    [
        .. deletions.Select(filed => new Interleaved(filed.Receipt?.IssuedAtUnixMilliseconds ?? 0, filed.Path, filed, null))
            .Concat(replications.Select(filed => new Interleaved(filed.Receipt?.IssuedAtUnixMilliseconds ?? 0, filed.Path, null, filed)))
            .OrderByDescending(entry => entry.IssuedAt)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal),
    ];

    private sealed record Interleaved(
        ulong IssuedAt, string Path, FiledDeletionReceipt? Deletion, FiledReplicationReceipt? Replication);

    private static Entry Project(FiledReplicationReceipt filed) => new()
    {
        Kind = ReplicationReceiptStore.Kind,
        Path = filed.Path,
        Role = DeletionReceiptReport.Role(filed.Role),
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
                CommittedCount = receipt.CommittedCount,
                Committed = receipt.Committed,
                HeldObjects = receipt.HeldObjects,
                HeldBytes = receipt.HeldBytes,
            }
            : null,
    };

    private sealed class Entry
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

    private sealed class Attested
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

        [JsonPropertyName("committed_count")]
        public ulong CommittedCount { get; init; }

        [JsonPropertyName("committed")]
        public IReadOnlyList<string>? Committed { get; init; }

        [JsonPropertyName("held_objects")]
        public ulong HeldObjects { get; init; }

        [JsonPropertyName("held_bytes")]
        public ulong HeldBytes { get; init; }
    }
}
