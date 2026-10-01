using System.Runtime.Versioning;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The file a restore writes a hole into (FR-ARCH-013; specification 09 §4):
/// a range the writer skips is left unallocated wherever the filesystem can
/// hold a hole, and reads back as zeroes everywhere. Skipping alone does that
/// on Linux, and on APFS for a hole of 16 MiB or more. APFS fills a shorter
/// hole with zeroes once it writes the file back, and a test here pins that.
/// NTFS allocates and zero-fills a skipped range unless the file was marked
/// sparse first, so the mark is what these tests hold on Windows.
/// </summary>
/// <remarks>
/// <see cref="AllocatedSize"/> is the oracle, because only allocation tells a
/// hole from written zeroes, and it writes a file back before it measures.
/// Every layout here keeps its data on 1 MiB boundaries, so no filesystem's
/// allocation unit straddles data and hole. A hole meant to stay one is
/// 32 MiB, twice the shortest APFS keeps.
/// </remarks>
[TestClass]
public sealed class SparseFileTests : IDisposable
{
    private const int MiB = 1024 * 1024;

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-sparse-file-tests", Guid.NewGuid().ToString("n"))).FullName;

    private string PathOf(string name) => Path.Combine(_root, name);

    [TestMethod]
    public void Create_ForAFileWithHoles_LeavesTheSkippedRangesUnallocatedAndReadingAsZeroes()
    {
        var path = PathOf("holes.bin");
        var data = Data(MiB, 3);

        using (var file = SparseFile.Create(path, FileMode.CreateNew, holes: true))
        {
            file.Seek(32 * MiB, SeekOrigin.Begin);
            file.Write(data);
            file.SetLength(65 * MiB);
        }

        var allocated = AllocatedSize.Of(path);
        Assert.IsLessThan(16L * MiB, allocated, $"{allocated} bytes allocated for 1 MiB written into a 65 MiB file");

        // Read a mebibyte at a time: each is all hole or all data.
        using var content = File.OpenRead(path);
        Assert.AreEqual(65 * MiB, content.Length);
        var chunk = new byte[MiB];
        for (var offset = 0; offset < 65 * MiB; offset += MiB)
        {
            content.ReadExactly(chunk);
            if (offset == 32 * MiB)
            {
                Assert.IsTrue(chunk.AsSpan().SequenceEqual(data), "the data did not read back where it was written");
            }
            else
            {
                Assert.IsLessThan(0, chunk.AsSpan().IndexOfAnyExcept((byte)0), $"the hole did not read as zeroes at {offset}");
            }
        }
    }

    [TestMethod]
    public void AllowHoles_OnAFileTheCallerCreated_LeavesItsSkippedRangesUnallocated()
    {
        // The shape a restore destination arrives in: created by its caller,
        // with File.Create, and handed over as a stream.
        var path = PathOf("caller.bin");

        using (var file = File.Create(path))
        {
            Assert.IsTrue(SparseFile.AllowHoles(file.SafeFileHandle), "a file on this filesystem refused to hold holes");
            file.Seek(32 * MiB, SeekOrigin.Begin);
            file.Write(Data(MiB, 5));
            file.SetLength(65 * MiB);
        }

        var allocated = AllocatedSize.Of(path);
        Assert.IsLessThan(16L * MiB, allocated, $"{allocated} bytes allocated for 1 MiB written into a 65 MiB file");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "only APFS fills a hole it judges too short")]
    [PlatformTrait(TestPlatforms.MacOs)]
    public void OnApfs_AHoleShorterThan16MiB_IsAllocated_WhicheverOrderTheFileWasWrittenIn()
    {
        // Why a hole meant to stay one is 32 MiB in these suites. Each file is
        // 33 MiB, with 1 MiB of data at its start and 1 MiB at 17 MiB: a 16 MiB
        // hole between them and a 15 MiB hole after. APFS leaves the first and
        // fills the second with zeroes once the file is written back, whether
        // the length is set after the data, as a restore sets it, or before.
        // It goes by a hole's length, not where the hole lies, so the 16 MiB
        // one sits off the 16 MiB boundaries. Found when the first macOS run
        // of the sparse restore had every hole filled: they were 4, 15 and
        // 11 MiB.
        foreach (var lengthFirst in new[] { false, true })
        {
            var path = PathOf(lengthFirst ? "length-first.bin" : "length-last.bin");
            using (var file = SparseFile.Create(path, FileMode.CreateNew, holes: true))
            {
                if (lengthFirst)
                {
                    file.SetLength(33 * MiB);
                }

                file.Write(Data(MiB, 9));
                file.Seek(17 * MiB, SeekOrigin.Begin);
                file.Write(Data(MiB, 10));

                if (!lengthFirst)
                {
                    file.SetLength(33 * MiB);
                }
            }

            // The data and the 15 MiB hole: 17 MiB. With neither hole filled
            // it would be 2 MiB, and with both, 33.
            var allocated = AllocatedSize.Of(path);
            var order = lengthFirst ? "given its length first" : "given its length last";
            Assert.IsGreaterThanOrEqualTo(
                10L * MiB, allocated,
                $"{allocated} bytes allocated for a file {order}: APFS now keeps a 15 MiB hole, so the 16 MiB "
                + "the records give for the shortest hole it keeps is stale");
            Assert.IsLessThan(
                25L * MiB, allocated,
                $"{allocated} bytes allocated for a file {order}: APFS now fills a 16 MiB hole, so the records are "
                + "stale, and the 32 MiB holes these suites leave may be too short");
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only NTFS needs a file marked before it can hold a hole")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void Create_MarksOnlyAFileThatWillHaveHoles()
    {
        // A dense file restored with the sparse attribute would not be the
        // file that was captured, so the mark goes only where a hole will.
        using (SparseFile.Create(PathOf("sparse.bin"), FileMode.CreateNew, holes: true))
        using (SparseFile.Create(PathOf("dense.bin"), FileMode.CreateNew, holes: false))
        {
        }

        Assert.IsTrue(File.GetAttributes(PathOf("sparse.bin")).HasFlag(FileAttributes.SparseFile));
        Assert.IsFalse(File.GetAttributes(PathOf("dense.bin")).HasFlag(FileAttributes.SparseFile));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only NTFS needs the mark, and only there is it an ioctl an overlapped handle cannot take")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void AllowHoles_OnAnOverlappedHandle_Declines()
    {
        // The mark is a synchronous ioctl. On an overlapped handle its
        // completion would be posted to the thread pool's port, which owns no
        // such request, so it is not attempted: the file stays dense, and its
        // skipped ranges read as zeroes all the same.
        var path = PathOf("overlapped.bin");
        using var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous);

        Assert.IsFalse(SparseFile.AllowHoles(handle));
        Assert.IsFalse(File.GetAttributes(path).HasFlag(FileAttributes.SparseFile));
    }

    private static byte[] Data(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
