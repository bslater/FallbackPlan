using FallbackPlan.TestSupport;
using FallbackPlan.Repository.Format.Cbor;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Repository.Format.Keys;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Repository.FuzzTests;

/// <summary>
/// Parser robustness (F3; NFR-REL-005, specification 00 §8): every parser fed
/// arbitrary and structure-mutated bytes either succeeds or throws its own
/// typed <see cref="FormatException"/> subclass — never a null reference, an
/// index error, an overflow, or an unbounded allocation. Try-style parsers
/// never throw at all, and the descriptor parser always returns a result.
/// </summary>
[TestClass]
public sealed class ParserFuzzTests
{
    /// <summary>
    /// Every throwing parser, fed one byte buffer. A typed format exception
    /// is the contract; anything else propagates and fails the property with
    /// the parser named in the failure.
    /// </summary>
    private static readonly IReadOnlyList<(string Name, Action<byte[]> Parse)> ThrowingParsers =
    [
        ("canonical-cbor", bytes => CanonicalCbor.Validate(bytes)),
        ("record-header", bytes => RecordHeader.Parse(bytes)),
        ("blob-envelope", bytes => BlobEnvelope.Parse(bytes)),
        ("footer-locator", bytes => FooterLocator.Parse(
            bytes.Length >= FooterLocator.Length ? bytes.AsSpan(0, FooterLocator.Length) : bytes,
            blobLength: 1024)),
        ("blob-footer-header", bytes => BlobFooter.ParseHeader(bytes)),
        ("record-table", bytes => BlobFooter.DecodeRecordTable(bytes, declaredRecordCount: 1, blobLength: 1024)),
        ("key-object", bytes => KeyObjectFraming.Parse(bytes)),
        ("key-bundle", bytes => KeyBundleCodec.Decode(bytes).Dispose()),
        ("standalone-record", bytes => StandaloneRecordFraming.Parse(bytes)),
        .. FuzzCorpus.Seeds.Select(seed => (seed.Name, seed.Parse)),
    ];

    private static void AssertOnlyTypedFailure(string name, byte[] bytes, Action<byte[]> parse)
    {
        try
        {
            parse(bytes);
        }
        catch (FormatException)
        {
            // CborFormatException, ManifestValidationException,
            // RecordFormatException, KeyObjectFormatException,
            // BlobFormatException, IndexFormatException — the typed refusals
            // the callers are written to catch. Anything else escapes and
            // fails the test with the parser's name in the stack.
        }
#pragma warning disable CA1031 // deliberate: re-wrap so the failing parser is named
        catch (Exception exception)
        {
            Assert.Fail($"Parser '{name}' escaped with {exception.GetType().Name}: {exception.Message}");
        }
#pragma warning restore CA1031
    }

    [TestMethod]
    public void Parsers_GivenRandomBytes_ThrowOnlyTheirDeclaredExceptions() =>
        PropertyCheck.Holds(this, maxTest: 200);

    public static void Parsers_GivenRandomBytes_ThrowOnlyTheirDeclaredExceptionsProperty(byte[]? data)
    {
        data ??= [];
        foreach (var (name, parse) in ThrowingParsers)
        {
            AssertOnlyTypedFailure(name, data, parse);
        }
    }

    [TestMethod]
    public void Parsers_GivenRandomBytesBehindAValidMagic_ThrowOnlyTheirDeclaredExceptions() =>
        PropertyCheck.Holds(this, maxTest: 200);

    public static void Parsers_GivenRandomBytesBehindAValidMagic_ThrowOnlyTheirDeclaredExceptionsProperty(byte[]? data, int magicIndex)
    {
        // Stamp each framing magic over the head so the fuzz input gets past
        // the cheap gate and into the field validation it exists to test.
        data ??= [];
        ReadOnlySpan<byte> magic = ((uint)magicIndex % 4) switch
        {
            0 => "FBPKSREC"u8,
            1 => "FBPKBLOB"u8,
            2 => "FBPKKEYS"u8,
            _ => "FBPKSPCK"u8,
        };

        var stamped = new byte[Math.Max(data.Length, 8)];
        data.CopyTo(stamped, 0);
        magic.CopyTo(stamped);

        foreach (var (name, parse) in ThrowingParsers)
        {
            AssertOnlyTypedFailure(name, stamped, parse);
        }
    }

    [TestMethod]
    public void Parsers_GivenAMutatedValidEncoding_ThrowOnlyTheirDeclaredExceptions() =>
        PropertyCheck.Holds(this, maxTest: 300);

    public static void Parsers_GivenAMutatedValidEncoding_ThrowOnlyTheirDeclaredExceptionsProperty(
        int seedIndex, (int Offset, byte Mask)[]? mutations, int resize)
    {
        var seed = FuzzCorpus.Seeds[(int)((uint)seedIndex % FuzzCorpus.Seeds.Count)];

        // Resize first (truncation or random-length extension), then flip:
        // exactly the damage classes 04 §7 demands stay contained.
        var length = Math.Max(0, seed.Bytes.Length + (int)((uint)resize % 64) - 32);
        var mutated = new byte[length];
        Array.Copy(seed.Bytes, mutated, Math.Min(seed.Bytes.Length, length));

        foreach (var (offset, mask) in mutations ?? [])
        {
            if (mutated.Length > 0)
            {
                mutated[(int)((uint)offset % mutated.Length)] ^= (byte)(mask | 1);
            }
        }

        AssertOnlyTypedFailure(seed.Name, mutated, seed.Parse);
    }

    [TestMethod]
    public void RepositoryDescriptorParser_GivenAnyInput_ReturnsAResultAndNeverThrows() =>
        PropertyCheck.Holds(this, maxTest: 200);

    public static void RepositoryDescriptorParser_GivenAnyInput_ReturnsAResultAndNeverThrowsProperty(
        byte[]? data, bool stampMagic, (int Offset, byte Mask)[]? mutations)
    {
        // 01 §3.1: "not a repository", "damaged", and "unsupported" are
        // RESULTS, not exceptions — for every input, including a mutated
        // once-valid descriptor.
        byte[] candidate;
        if (stampMagic)
        {
            candidate = (byte[])FuzzCorpus.DescriptorSeed.Clone();
            foreach (var (offset, mask) in mutations ?? [])
            {
                candidate[(int)((uint)offset % candidate.Length)] ^= (byte)(mask | 1);
            }
        }
        else
        {
            candidate = data ?? [];
        }

        var result = RepositoryDescriptorCodec.Parse(candidate);

        Assert.IsNotNull(result);
    }

    [TestMethod]
    public void SpoolCheckpointTryParse_GivenAnyInput_NeverThrows() =>
        PropertyCheck.Holds(this, maxTest: 200);

    public static void SpoolCheckpointTryParse_GivenAnyInput_NeverThrowsProperty(byte[]? data, bool stampMagic)
    {
        var candidate = data ?? [];
        if (stampMagic && candidate.Length >= 8)
        {
            "FBPKSPCK"u8.CopyTo(candidate);
        }

        // The try-contract: false for garbage, never an exception — a torn
        // sidecar means "restart the blob", not "crash the resume".
        if (!SpoolCheckpoint.TryParse(candidate, out var checkpoint))
        {
            Assert.IsNull(checkpoint);
        }
    }

    [TestMethod]
    public void Base32TryDecode_GivenAnyInput_NeverThrowsAndNeverReportsFalseSuccess() =>
        PropertyCheck.Holds(this, maxTest: 200);

    public static void Base32TryDecode_GivenAnyInput_NeverThrowsAndNeverReportsFalseSuccessProperty(string? text)
    {
        var input = (text ?? string.Empty).AsSpan();
        var destination = new byte[Domain.Base32.GetEncodedLength(input.Length) + 8];

        if (Domain.Base32.TryDecode(input, destination, out var written))
        {
            // Bijectivity: exactly one rendering names the bytes, so a
            // successful decode re-encodes to the identical text.
            Assert.AreEqual(new string(input), Domain.Base32.Encode(destination.AsSpan(0, written)));
        }
    }

    [TestMethod]
    public void StandaloneRecord_TruncatedButClaimingAHugeLength_IsRefusedBeforeAllocation()
    {
        // The 00 §8 pre-allocation bound with teeth: a 126-byte buffer whose
        // header declares a 16 MiB payload must be refused from the declared
        // lengths alone. The allocation budget below fails if the parser
        // allocates anywhere near the claimed size first.
        var seed = FuzzCorpus.Seeds.First(candidate => candidate.Name == "standalone-record");
        var truncated = seed.Bytes.AsSpan(0, StandaloneRecordFraming.PrefixLength + RecordHeader.Length).ToArray();

        // Rewrite the header's stored-length field (offset 18 within the
        // 54-byte header) to claim a 16 MiB payload the buffer does not hold.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            truncated.AsSpan(StandaloneRecordFraming.PrefixLength + 18), 16 * 1024 * 1024);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsExactly<RecordFormatException>(() => StandaloneRecordFraming.Parse(truncated));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(allocated < 64 * 1024, $"refusal allocated {allocated} bytes");
    }
}
