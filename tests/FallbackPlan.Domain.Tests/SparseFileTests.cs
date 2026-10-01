using System.Runtime.Versioning;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The file a restore writes a hole into (FR-ARCH-013; specification 09 §4):
/// given its whole length while still empty, and its data written inside
/// that length, a file leaves every range nothing was written to unallocated
/// wherever the filesystem can hold a hole, and reads it back as zeroes
/// everywhere. NTFS allocates and zero-fills such a range unless the file was
/// marked sparse first, so the mark is what these tests hold on Windows.
/// APFS allocates the range a write skips past the end of a file, so the
/// length comes first, and a test here pins that on macOS.
/// </summary>
/// <remarks>
/// <see cref="AllocatedSize"/> is the oracle, because only allocation tells a
/// hole from written zeroes, and it writes the file back before it measures.
/// Every layout here keeps its data on 1 MiB boundaries, so no filesystem's
/// allocation unit straddles data and hole.
/// </remarks>
[TestClass]
public sealed class SparseFileTests : IDisposable
{
    private const int MiB = 1024 * 1024;

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-sparse-file-tests", Guid.NewGuid().ToString("n"))).FullName;

    private string PathOf(string name) => Path.Combine(_root, name);

    [TestMethod]
    public void Create_ForAFileWithHoles_LeavesTheUnwrittenRangesUnallocatedAndReadingAsZeroes()
    {
        var path = PathOf("holes.bin");
        var data = Data(MiB, 3);

        using (var file = SparseFile.Create(path, FileMode.CreateNew, holes: true, length: 32 * MiB))
        {
            Assert.AreEqual(32 * MiB, file.Length, "the length is set before anything is written");
            file.Seek(4 * MiB, SeekOrigin.Begin);
            file.Write(data);
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
    public void AllowHoles_OnAFileTheCallerCreated_LeavesItsUnwrittenRangesUnallocated()
    {
        // The shape a restore destination arrives in: created by its caller,
        // with File.Create, and handed over as a stream, which the restore
        // gives its length before it writes the data.
        var path = PathOf("caller.bin");

        using (var file = File.Create(path))
        {
            Assert.IsTrue(SparseFile.AllowHoles(file.SafeFileHandle), "a file on this filesystem refused to hold holes");
            file.SetLength(32 * MiB);
            file.Seek(20 * MiB, SeekOrigin.Begin);
            file.Write(Data(MiB, 5));
        }

        var allocated = AllocatedSize.Of(path);
        Assert.IsLessThan(16L * MiB, allocated, $"{allocated} bytes allocated for 1 MiB written into a 32 MiB file");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "only APFS allocates the range a write skips past the end of a file")]
    [PlatformTrait(TestPlatforms.MacOs)]
    public void OnApfs_AWriteThatExtendsTheFile_HasTheRangeItSkippedAllocated()
    {
        // Why the length comes first. The range this write skips reads as
        // zeroes, and it is zero-filled and allocated once the file is written
        // back, so it is not a hole. Found when the macOS suite first ran a
        // sparse restore: every restored file was fully allocated.
        var path = PathOf("extended.bin");

        using (var file = File.Create(path))
        {
            file.Seek(20 * MiB, SeekOrigin.Begin);
            file.Write(Data(MiB, 9));
            file.SetLength(32 * MiB);
        }

        var allocated = AllocatedSize.Of(path);
        Assert.IsGreaterThanOrEqualTo(
            16L * MiB, allocated,
            $"{allocated} bytes allocated for 1 MiB written past the end of a 32 MiB file: APFS now leaves that "
            + "range a hole, and the length need no longer come first");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "a diagnostic of how APFS keeps holes, removed once read")]
    [PlatformTrait(TestPlatforms.MacOs)]
    public void Diagnostic_WhichHolesApfsKeeps_ReportedAsAFailure()
    {
        // Temporary, and the second of two. The first found that APFS keeps
        // a 16 MiB hole and fills a 15 MiB one, but every hole it kept that
        // was shorter than 32 MiB also held a 16 MiB-aligned block. These
        // layouts tell a rule by length from a rule by aligned block. Each
        // run is 1 MiB of data at the given offset, written in one call
        // unless a chunk size is given.
        var layouts = new (string Name, int SizeMiB, int[] DataAtMiB, bool LengthFirst, int Chunk)[]
        {
            ("K", 64, [0, 18], true, MiB),
            ("L", 64, [8, 30], true, MiB),
            ("M", 32, [16], false, MiB),
            ("N", 64, [0, 17], false, MiB),
            ("O", 64, [0, 17], true, MiB),
            ("P", 48, [31], true, MiB),
            ("Q", 40, [0, 23], true, MiB),
            ("S", 64, [8], true, 64 * 1024),
            ("T", 34, [0, 17], true, MiB),
        };

        var lines = new List<string>();
        var data = Data(MiB, 13);
        foreach (var (name, sizeMiB, dataAt, lengthFirst, chunk) in layouts)
        {
            var path = PathOf($"diag-{name}.bin");
            using (var file = File.Create(path))
            {
                if (lengthFirst)
                {
                    file.SetLength((long)sizeMiB * MiB);
                }

                foreach (var offset in dataAt)
                {
                    file.Seek((long)offset * MiB, SeekOrigin.Begin);
                    for (var written = 0; written < MiB; written += chunk)
                    {
                        file.Write(data.AsSpan(written, chunk));
                    }
                }

                if (!lengthFirst)
                {
                    file.SetLength((long)sizeMiB * MiB);
                }
            }

            var allocated = AllocatedSize.Of(path) / (double)MiB;
            lines.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{name}: {sizeMiB} MiB, data at [{string.Join(",", dataAt)}] MiB, {(lengthFirst ? "length first" : "length last")}, {chunk / 1024} KiB writes: {allocated:f1} MiB allocated"));
            File.Delete(path);
        }

        Assert.Fail(string.Join(" | ", lines));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only NTFS needs a file marked before it can hold a hole")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void Create_MarksOnlyAFileThatWillHaveHoles()
    {
        // A dense file restored with the sparse attribute would not be the
        // file that was captured, so the mark goes only where a hole will.
        using (SparseFile.Create(PathOf("sparse.bin"), FileMode.CreateNew, holes: true, length: MiB))
        using (SparseFile.Create(PathOf("dense.bin"), FileMode.CreateNew, holes: false, length: 0))
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
