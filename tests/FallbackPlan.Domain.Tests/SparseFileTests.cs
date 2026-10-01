using System.Runtime.Versioning;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The file a restore writes a hole into (FR-ARCH-013; specification 09 §4):
/// a range the writer skips is left unallocated wherever the filesystem can
/// hold a hole, and reads back as zeroes everywhere. Skipping alone does that
/// on POSIX. NTFS allocates and zero-fills a skipped range unless the file was
/// marked sparse first, so the mark is what these tests hold on Windows.
/// </summary>
/// <remarks>
/// <see cref="AllocatedSize"/> is the oracle, because only allocation tells a
/// hole from written zeroes. Every layout here keeps its data on 1 MiB
/// boundaries, so no filesystem's allocation unit straddles data and hole.
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
            file.Seek(4 * MiB, SeekOrigin.Begin);
            file.Write(data);
            file.SetLength(32 * MiB);
        }

        var allocated = AllocatedSize.Of(path);
        Assert.IsLessThan(16L * MiB, allocated, $"{allocated} bytes allocated for 1 MiB written into a 32 MiB file");

        var content = File.ReadAllBytes(path);
        Assert.AreEqual(32 * MiB, content.Length);
        Assert.IsLessThan(0, content.AsSpan(0, 4 * MiB).IndexOfAnyExcept((byte)0), "the leading hole did not read as zeroes");
        Assert.IsTrue(content.AsSpan(4 * MiB, MiB).SequenceEqual(data), "the data did not read back where it was written");
        Assert.IsLessThan(0, content.AsSpan(5 * MiB).IndexOfAnyExcept((byte)0), "the trailing hole did not read as zeroes");
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
            file.Seek(20 * MiB, SeekOrigin.Begin);
            file.Write(Data(MiB, 5));
            file.SetLength(32 * MiB);
        }

        var allocated = AllocatedSize.Of(path);
        Assert.IsLessThan(16L * MiB, allocated, $"{allocated} bytes allocated for 1 MiB written into a 32 MiB file");
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
