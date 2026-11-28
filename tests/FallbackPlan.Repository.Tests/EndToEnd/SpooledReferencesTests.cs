using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A file's references and content identifiers as its session archives it
/// (ADR-0201 Amendment 3; FR-ARCH-002, NFR-PERF-001): past
/// <see cref="ArchiveSession.ReferencesHeldPerFile"/> they wait in the
/// session's spool a chunk at a time, and read back the same, in order and
/// by index, across every chunk's edge. Once the session ends its spool is
/// gone, and reading what waited there says so, while the chunk still held
/// in memory reads as before.
/// </summary>
/// <remarks>Does not establish FR-MAN-006: nothing here is reused.</remarks>
[TestClass]
public sealed class SpooledReferencesTests : ArchiveTestHarness
{
    private const int Chunk = ArchiveSession.ReferencesHeldPerFile;

    /// <summary>Two chunks and three more: the last chunk short, as a file's is.</summary>
    private const int Segments = (2 * Chunk) + 3;

    /// <summary>A file of one-byte stretches of data with a hole after each; stretch <c>n</c> is at <c>2n</c>.</summary>
    private static (byte[] Content, SparseExtent[] Holes) Stretches(int count)
    {
        var content = new byte[count * 2];
        for (var stretch = 0; stretch < count; stretch++)
        {
            content[stretch * 2] = (byte)(1 + (stretch % 251));
        }

        return (content, [.. Enumerable.Range(0, count).Select(hole => new SparseExtent((ulong)((hole * 2) + 1), 1))]);
    }

    private FileArchiver CreateDiscardingArchiver(RepositoryKeySet keys) => new(
        SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, new DiscardingObjectStore(),
        new MonotonicBlobCounterAllocator(1), SpoolDirectory, FormatVersions.SealedDataPlane);

    [TestMethod]
    public async Task AFilePastAChunk_ReadsBackItsReferencesAndIdentifiers_InOrderAndByIndex_AcrossEveryChunksEdge()
    {
        var (content, holes) = Stretches(Segments);
        using var keys = CreateKeys();
        var session = CreateDiscardingArchiver(keys).OpenSession();
        await using (session)
        {
            using var stream = new MemoryStream(content, writable: false);
            var archived = await session.ArchiveSparseFileAsync(stream, holes, content.Length, CancellationToken.None);

            Assert.HasCount(Segments, archived.SegmentReferences);
            Assert.HasCount(Segments, archived.SegmentContentIds);

            // In order: every stretch where it is, one byte long.
            var expected = 0L;
            foreach (var reference in archived.SegmentReferences)
            {
                Assert.AreEqual(expected, reference.LogicalOffset);
                Assert.AreEqual(1L, reference.LogicalLength);
                expected += 2;
            }

            Assert.AreEqual(2L * Segments, expected);

            // In order, an identifier per stretch: the same for stretches of
            // the same byte, 251 apart, and different for the next.
            List<ContentId> identifiers = [.. archived.SegmentContentIds];
            Assert.HasCount(Segments, identifiers);
            Assert.AreEqual(identifiers[0], identifiers[251]);
            Assert.AreNotEqual(identifiers[0], identifiers[1]);

            // By index, on each side of each chunk's edge and at the ends, as
            // read in order.
            foreach (var index in new[] { 0, Chunk - 1, Chunk, (2 * Chunk) - 1, 2 * Chunk, Segments - 1 })
            {
                Assert.AreEqual(2L * index, archived.SegmentReferences[index].LogicalOffset, $"reference {index}");
                Assert.AreEqual(identifiers[index], archived.SegmentContentIds[index], $"identifier {index}");
                Assert.AreEqual(identifiers[index % 251], archived.SegmentContentIds[index], $"identifier {index} is its byte's");
            }
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => archived.SegmentReferences[Segments]);
        }
    }

    [TestMethod]
    public async Task AFilesSpooledReferences_OnceItsSessionHasEnded_SayWhereElseToLook()
    {
        var (content, holes) = Stretches(Chunk + 1);
        using var keys = CreateKeys();
        ArchiveResult archived;
        var session = CreateDiscardingArchiver(keys).OpenSession();
        await using (session)
        {
            using var stream = new MemoryStream(content, writable: false);
            archived = await session.ArchiveSparseFileAsync(stream, holes, content.Length, CancellationToken.None);
        }

        var references = Assert.ThrowsExactly<InvalidOperationException>(() => archived.SegmentReferences[0]);
        Assert.Contains("manifest", references.Message, StringComparison.Ordinal);
        var identifiers = Assert.ThrowsExactly<InvalidOperationException>(() => archived.SegmentContentIds.ToList());
        Assert.Contains("never durable", identifiers.Message, StringComparison.Ordinal);

        // The one held in memory, the last, is still there.
        Assert.AreEqual(2L * Chunk, archived.SegmentReferences[Chunk].LogicalOffset);
    }
}
