using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Segmentation;
using Xunit;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives every <c>segmentation.json</c> case through the real fixed-v1
/// boundary arithmetic and, for the materializable cases, through the
/// streaming reader over actual bytes — Wave B1's acceptance criterion
/// (specification 09 §2; FR-ARCH-001).
/// </summary>
public sealed class SegmentationConformanceTests
{
    private static JsonDocument Vectors { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "segmentation.json")));

    [Fact]
    public void Boundary_arithmetic_matches_every_committed_case()
    {
        foreach (var vectorCase in Vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            var fileLength = vectorCase.GetProperty("file_length").GetInt64();
            var segmentSize = SegmentSize.Create(vectorCase.GetProperty("segment_size").GetInt32());
            var expectedSegments = vectorCase.GetProperty("segments").EnumerateArray().ToArray();

            Assert.Equal(
                vectorCase.GetProperty("segment_count").GetInt64(),
                FixedSegmentation.SegmentCount(fileLength, segmentSize));

            for (var index = 0; index < expectedSegments.Length; index++)
            {
                var (offset, length) = FixedSegmentation.GetSegment(index, fileLength, segmentSize);

                Assert.Equal(expectedSegments[index].GetProperty("offset").GetInt64(), offset);
                Assert.Equal(expectedSegments[index].GetProperty("length").GetInt64(), length);
            }
        }
    }

    [Fact]
    public async Task The_streaming_reader_reproduces_every_materializable_case()
    {
        foreach (var vectorCase in Vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            var fileLength = vectorCase.GetProperty("file_length").GetInt64();

            if (fileLength > 8 * 1024 * 1024)
            {
                continue; // arithmetic-only above 8 MiB; every committed case is under it anyway
            }

            var segmentSize = SegmentSize.Create(vectorCase.GetProperty("segment_size").GetInt32());
            var expectedSegments = vectorCase.GetProperty("segments").EnumerateArray().ToArray();

            using var source = new MemoryStream(new byte[fileLength]);
            var reader = new FixedSegmentReader(source, segmentSize);
            var buffer = new byte[segmentSize.Bytes];

            var index = 0;
            while (await reader.ReadNextAsync(buffer, CancellationToken.None) is { } segment)
            {
                Assert.Equal(expectedSegments[index].GetProperty("offset").GetInt64(), segment.Offset);
                Assert.Equal(expectedSegments[index].GetProperty("length").GetInt64(), segment.Length);
                Assert.Equal(index, segment.Index);
                index++;
            }

            Assert.Equal(expectedSegments.Length, index);
        }
    }

    [Fact]
    public void Cdc_tables_derived_from_the_polynomial_match_the_committed_tables()
    {
        // The vectors commit the polynomial, the derivation rule, and the
        // computed tables; the engine derives its tables from the polynomial
        // at type initialisation. Agreement here proves both routes to the
        // tables — regenerate or embed — describe one function (ADR-0023).
        var cdc = Vectors.RootElement.GetProperty("cdc_v1");

        Assert.Equal("0x000000000000001b", cdc.GetProperty("polynomial").GetString());
        Assert.Equal(RabinFingerprint.WindowSize, cdc.GetProperty("window_size").GetInt32());

        var push = cdc.GetProperty("push_table").EnumerateArray().Select(e => e.GetString()).ToArray();
        var pop = cdc.GetProperty("pop_table").EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(256, push.Length);
        Assert.Equal(256, pop.Length);

        for (var b = 0; b < 256; b++)
        {
            Assert.Equal(push[b], RabinFingerprint.PushTable[b].ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(pop[b], RabinFingerprint.PopTable[b].ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task The_cdc_reader_reproduces_every_committed_boundary()
    {
        var cdc = Vectors.RootElement.GetProperty("cdc_v1");

        foreach (var vectorCase in cdc.GetProperty("cases").EnumerateArray())
        {
            var name = vectorCase.GetProperty("name").GetString();
            var parameters = CdcParameters.Create(
                vectorCase.GetProperty("target_size").GetInt32(),
                vectorCase.GetProperty("min_size").GetInt32(),
                vectorCase.GetProperty("max_size").GetInt32());
            var expectedSegments = vectorCase.GetProperty("segments").EnumerateArray().ToArray();

            using var source = new MemoryStream(MaterializeInput(vectorCase.GetProperty("input")));
            var reader = new CdcSegmentReader(source, parameters);
            var buffer = new byte[parameters.MaxSize];

            var index = 0;
            while (await reader.ReadNextAsync(buffer, CancellationToken.None) is { } segment)
            {
                Assert.True(index < expectedSegments.Length, $"{name}: more segments than committed");
                Assert.Equal(expectedSegments[index].GetProperty("offset").GetInt64(), segment.Offset);
                Assert.Equal(expectedSegments[index].GetProperty("length").GetInt64(), segment.Length);
                index++;
            }

            Assert.Equal(expectedSegments.Length, index);
        }
    }

    /// <summary>
    /// Rebuilds a case's input bytes from its committed generator rule — the
    /// vectors describe inputs, they never embed them.
    /// </summary>
    private static byte[] MaterializeInput(JsonElement input)
    {
        switch (input.GetProperty("kind").GetString())
        {
            case "empty":
                return [];

            case "zeros":
                return new byte[input.GetProperty("length").GetInt32()];

            case "sha256_stream":
                return Sha256Stream(input.GetProperty("blocks").GetInt32());

            case "prefixed_sha256_stream":
                var prefix = Convert.FromHexString(input.GetProperty("prefix").GetString()!);
                return [.. prefix, .. Sha256Stream(input.GetProperty("blocks").GetInt32())];

            case "repeat":
                var pattern = Convert.FromHexString(input.GetProperty("pattern").GetString()!);
                var repetitions = input.GetProperty("repetitions").GetInt32();
                var repeated = new byte[pattern.Length * repetitions];
                for (var i = 0; i < repetitions; i++)
                {
                    pattern.CopyTo(repeated, i * pattern.Length);
                }

                return repeated;

            default:
                throw new InvalidOperationException($"Unknown input kind '{input.GetProperty("kind").GetString()}'.");
        }
    }

    private static byte[] Sha256Stream(int blocks)
    {
        var output = new byte[blocks * 32];
        Span<byte> counter = stackalloc byte[8];

        for (var i = 0; i < blocks; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(counter, (ulong)i);
            System.Security.Cryptography.SHA256.HashData(counter, output.AsSpan(i * 32, 32));
        }

        return output;
    }
}
