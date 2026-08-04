using System.Runtime.InteropServices;
using FallbackPlan.Domain;
using Microsoft.Win32.SafeHandles;

namespace FallbackPlan.Filesystem.Local;

/// <summary>
/// Hole enumeration (architecture 06 §1: record sparse extents as logical
/// zero extents, never read zeroes). POSIX via <c>lseek</c> with
/// <c>SEEK_DATA</c>/<c>SEEK_HOLE</c> — whose values are swapped between
/// Linux and Darwin, pinned per platform here. Degrades to "no holes" on
/// any failure: sparse detection is an optimisation, never a correctness
/// input, because restore materialises whatever the manifest says.
/// </summary>
internal static partial class SparseProbe
{
    [LibraryImport("libc", EntryPoint = "lseek", SetLastError = true)]
    private static partial long NativeSeek(SafeFileHandle fd, long offset, int whence);

    private static (int Data, int Hole) Whence =>
        OperatingSystem.IsMacOS() ? (4, 3) : (3, 4);

    /// <summary>
    /// Enumerates the file's holes as sparse extents. Empty when the file
    /// is dense, the filesystem does not support hole reporting, or this
    /// platform's probe is unavailable.
    /// </summary>
    public static IReadOnlyList<SparseExtent> FindHoles(string path, long length)
    {
        if (length == 0 || OperatingSystem.IsWindows())
        {
            return []; // NTFS sparse ranges are a later refinement; dense capture stays correct.
        }

        try
        {
            using var stream = File.Open(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
            });

            var (data, hole) = Whence;
            var holes = new List<SparseExtent>();
            long position = 0;

            while (position < length)
            {
                var holeStart = NativeSeek(stream.SafeFileHandle, position, hole);
                if (holeStart < 0 || holeStart >= length)
                {
                    break; // no further holes (or unsupported: lseek fails with ENXIO/EINVAL)
                }

                var dataStart = NativeSeek(stream.SafeFileHandle, holeStart, data);
                var holeEnd = dataStart < 0 ? length : Math.Min(dataStart, length);
                if (holeEnd > holeStart)
                {
                    holes.Add(new SparseExtent((ulong)holeStart, (ulong)(holeEnd - holeStart)));
                }

                position = Math.Max(holeEnd, holeStart + 1);
            }

            // Every filesystem reports one implicit "hole" at EOF; a real
            // hole list never includes it, and SEEK_HOLE at EOF was already
            // excluded by the length guard above.
            return holes;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
