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
    public void Cdc_v1_remains_unpinned_in_the_vectors()
    {
        // The committed status stub is the B6 blocker made visible; when
        // parameters are pinned this assertion will fail and force the
        // implementation decision it guards.
        Assert.Equal(
            "parameters not yet pinned",
            Vectors.RootElement.GetProperty("cdc_v1").GetProperty("status").GetString());
    }
}
