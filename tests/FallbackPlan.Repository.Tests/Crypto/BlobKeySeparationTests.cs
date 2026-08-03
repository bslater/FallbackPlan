using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FsCheck.Xunit;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// The A4 separation property (specification 03 §5; NFR-SEC-008): the blob-key
/// derivation binds writer identity and counter, so writers whose CSPRNGs
/// replay the same stream — a cloned virtual machine — still derive distinct
/// keys, and the nonce-reuse catastrophe the construction exists to prevent
/// cannot arise from RNG state alone.
/// </summary>
public sealed class BlobKeySeparationTests
{
    private static byte[] Derive(byte[] classKey, byte[] salt, WriterId writer, ulong counter)
    {
        var key = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(classKey, salt, writer, counter, key);
        return key;
    }

    [Property]
    public bool Distinct_writers_with_an_identical_csprng_stream_derive_distinct_keys(byte[]? saltSeed, byte writerDifference)
    {
        // The identical CSPRNG stream is modelled by giving both writers the
        // same blob salt — the only random derivation input. Writer identity
        // must still separate the keys.
        var salt = PadTo32(saltSeed);
        var classKey = new byte[32];

        var writerA = WriterId.FromBytes(new byte[WriterId.Size]);
        var writerBBytes = new byte[WriterId.Size];
        writerBBytes[0] = (byte)(writerDifference | 0x01); // guaranteed different in byte 0
        var writerB = WriterId.FromBytes(writerBBytes);

        return !Derive(classKey, salt, writerA, 7).AsSpan().SequenceEqual(Derive(classKey, salt, writerB, 7));
    }

    [Property]
    public bool Adjacent_counters_derive_distinct_keys(byte[]? saltSeed, ulong counter)
    {
        var salt = PadTo32(saltSeed);
        var classKey = new byte[32];
        var writer = WriterId.FromBytes(new byte[WriterId.Size]);

        var next = counter == ulong.MaxValue ? 0 : counter + 1;

        return !Derive(classKey, salt, writer, counter).AsSpan().SequenceEqual(Derive(classKey, salt, writer, next));
    }

    [Property]
    public bool Different_class_keys_derive_distinct_keys(byte classKeyDifference)
    {
        var salt = new byte[32];
        var writer = WriterId.FromBytes(new byte[WriterId.Size]);

        var classKeyA = new byte[32];
        var classKeyB = new byte[32];
        classKeyB[0] = (byte)(classKeyDifference | 0x01);

        return !Derive(classKeyA, salt, writer, 0).AsSpan().SequenceEqual(Derive(classKeyB, salt, writer, 0));
    }

    private static byte[] PadTo32(byte[]? seed)
    {
        var salt = new byte[BlobKeyDeriver.BlobSaltLength];
        seed?.AsSpan(0, Math.Min(seed.Length, salt.Length)).CopyTo(salt);
        return salt;
    }
}
