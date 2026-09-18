using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Derives a format-3 record's key from the record itself rather than from
/// its container (specification 03 §5.4;
/// [ADR-0052](../../docs/adr/0052-relocatable-records-format-v3.md)
/// Amendment 1): <c>record_key = HKDF-Expand(class_key[generation],
/// "fbp/record/v3" ‖ u8(object_type) ‖ object_id, 32)</c>. Nothing about the
/// blob enters the derivation, which is what lets the sealed bytes be copied
/// into another blob and still open there.
/// </summary>
/// <remarks>
/// The second derivation here is the writer's own: a format-3 data record's
/// content key is random to every reader, but the writer derives it from a
/// per-blob seed and the object identifier so that an interrupted spool can
/// be authenticated on resume without a key per record on disk. The seed
/// lives only in the spool checkpoint and dies at seal (05 §6.2).
/// </remarks>
public static class RecordKeyDeriver
{
    /// <summary>A record key is 32 bytes.</summary>
    public const int RecordKeyLength = 32;

    /// <summary>Derives a structure-plane record's key from the class key and the record's own identity.</summary>
    /// <param name="classKey">The 32-byte metadata (or, without a sealed plane, data) key of the blob's generation.</param>
    /// <param name="objectType">The record's object type, from its header.</param>
    /// <param name="objectId">The record's object identifier, from its header.</param>
    /// <param name="destination">Receives exactly 32 key bytes; the caller zeroes it after use.</param>
    /// <exception cref="ArgumentException">A key or destination length is wrong.</exception>
    public static void Derive(
        ReadOnlySpan<byte> classKey, ObjectType objectType, ObjectId objectId, Span<byte> destination)
    {
        if (classKey.Length != RecordKeyLength)
        {
            throw new ArgumentException("A class key is exactly 32 bytes.", nameof(classKey));
        }

        if (destination.Length != RecordKeyLength)
        {
            throw new ArgumentException("A record key is exactly 32 bytes.", nameof(destination));
        }

        var label = "fbp/record/v3"u8;
        Span<byte> info = stackalloc byte[label.Length + 1 + ObjectId.Size];
        label.CopyTo(info);
        info[label.Length] = (byte)objectType;
        objectId.CopyTo(info[(label.Length + 1)..]);

        HKDF.Expand(HashAlgorithmName.SHA256, classKey, destination, info);
    }

    /// <summary>
    /// Derives a data record's content key from the writer's per-blob seed
    /// and the record's object identifier (03 §5.4). Only the writer ever
    /// holds the seed; a reader opens the sealed share the writer made from
    /// this key instead.
    /// </summary>
    /// <param name="seed">The blob's 32-byte record-key seed, from the spool checkpoint.</param>
    /// <param name="objectId">The record's object identifier.</param>
    /// <param name="destination">Receives exactly 32 key bytes; the caller zeroes it after use.</param>
    /// <exception cref="ArgumentException">The seed or destination length is wrong.</exception>
    public static void DeriveFromSeed(ReadOnlySpan<byte> seed, ObjectId objectId, Span<byte> destination)
    {
        if (seed.Length != RecordKeyLength)
        {
            throw new ArgumentException("A record-key seed is exactly 32 bytes.", nameof(seed));
        }

        if (destination.Length != RecordKeyLength)
        {
            throw new ArgumentException("A record key is exactly 32 bytes.", nameof(destination));
        }

        var label = "fbp/record-seed/v3"u8;
        Span<byte> info = stackalloc byte[label.Length + ObjectId.Size];
        label.CopyTo(info);
        objectId.CopyTo(info[label.Length..]);

        HKDF.Expand(HashAlgorithmName.SHA256, seed, destination, info);
    }
}
