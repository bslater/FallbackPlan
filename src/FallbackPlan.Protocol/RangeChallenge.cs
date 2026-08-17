using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Bodu;

namespace FallbackPlan.Protocol;

/// <summary>
/// The proof both sides of a verification challenge compute (specification
/// peer-protocol 04 §2): <c>HMAC-SHA-256(challenge_key, "fbp/verify/v1" ‖
/// nonce ‖ store_key ‖ u64_be(offset) ‖ u32_be(length) ‖ bytes_at_range)</c>.
/// The label sits inside the MAC's message, like every keyed derivation in
/// this codebase, so the proof can never collide with another HMAC use of
/// the same key material.
/// </summary>
public static class RangeChallenge
{
    private static readonly byte[] Label = Encoding.ASCII.GetBytes("fbp/verify/v1");

    /// <summary>Computes the proof over the given range bytes.</summary>
    /// <param name="challengeKey">The challenge's fresh key (32 bytes).</param>
    /// <param name="nonce">The challenge's fresh nonce (16 bytes).</param>
    /// <param name="storeKey">The store key the range belongs to.</param>
    /// <param name="offset">The range's byte offset.</param>
    /// <param name="length">The range's length; must equal <paramref name="bytes"/>.Length.</param>
    /// <param name="bytes">The exact stored bytes at the range.</param>
    /// <returns>The 32-byte proof.</returns>
    public static byte[] Compute(
        ReadOnlySpan<byte> challengeKey,
        ReadOnlySpan<byte> nonce,
        string storeKey,
        ulong offset,
        uint length,
        ReadOnlySpan<byte> bytes)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(storeKey);

        if (bytes.Length != length)
        {
            throw new ArgumentException("The proof is over exactly the challenged range.", nameof(bytes));
        }

        using var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, challengeKey);
        mac.AppendData(Label);
        mac.AppendData(nonce);
        mac.AppendData(Encoding.UTF8.GetBytes(storeKey));

        Span<byte> numbers = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(numbers, offset);
        BinaryPrimitives.WriteUInt32BigEndian(numbers[8..], length);
        mac.AppendData(numbers);

        mac.AppendData(bytes);
        return mac.GetHashAndReset();
    }
}
