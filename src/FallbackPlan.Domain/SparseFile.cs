using Bodu;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace FallbackPlan.Domain;

/// <summary>
/// A file a restore can leave holes in (specification 09 §4; FR-ARCH-013): a
/// range its writer skips rather than writes stays unallocated wherever the
/// filesystem can hold a hole, and reads as zeroes everywhere.
/// </summary>
/// <remarks>
/// <para>
/// Skipping is what leaves the hole, and it is correct on every platform:
/// POSIX reads zeroes from a range an extending write or an <c>ftruncate</c>
/// skipped, and Windows zero-fills one. What differs is whether the range is
/// allocated. Linux leaves it unallocated wherever the filesystem supports
/// holes, with nothing asked. APFS leaves a range of 16 MiB or more, and fills
/// a shorter one with zeroes when it writes the file back, whichever order
/// the length and the data were written in. NTFS allocates and zero-fills it
/// unless the file was marked sparse first, so on Windows a file that will
/// have holes is marked before anything is written.
/// </para>
/// <para>
/// Sits beside <see cref="AtomicFile"/> for the same reason: the engine's
/// restore and the standalone recovery tool both write restored files, and
/// Domain is the one assembly both may reach.
/// </para>
/// </remarks>
public static partial class SparseFile
{
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Creates <paramref name="path"/> for reading and writing, ready for its
    /// holes to be skipped rather than written when <paramref name="holes"/>
    /// says it will have any.
    /// </summary>
    /// <param name="path">The file to create.</param>
    /// <param name="mode">How to create it: <see cref="FileMode.CreateNew"/> or <see cref="FileMode.Create"/>.</param>
    /// <param name="holes">
    /// Whether the file will have holes. Only then is it marked: a dense file
    /// restored with the sparse attribute would not be the file that was
    /// captured.
    /// </param>
    public static FileStream Create(string path, FileMode mode, bool holes)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);

        // A synchronous handle, because the mark is a synchronous ioctl (see
        // AllowHoles); the stream offloads its asynchronous calls itself.
        var handle = File.OpenHandle(path, mode, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (holes)
            {
                _ = AllowHoles(handle);
            }

            return new FileStream(handle, FileAccess.ReadWrite, BufferSize);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Lets the ranges a writer skips in the file behind
    /// <paramref name="handle"/> stay unallocated.
    /// </summary>
    /// <param name="handle">An open handle with write access.</param>
    /// <returns>
    /// Whether the file can now hold holes. True on POSIX, where nothing is
    /// needed. On Windows, false where the mark was refused — a filesystem
    /// without sparse files, such as FAT — or not attempted, on an overlapped
    /// handle. Either way a skipped range still reads as zeroes, and is
    /// allocated.
    /// </returns>
    public static bool AllowHoles(SafeFileHandle handle)
    {
        ThrowHelper.ThrowIfNull(handle);

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        // An overlapped handle's completion is posted to the thread pool's
        // port, which owns no request for this ioctl. Not attempted rather
        // than risked.
        return !handle.IsAsync && WindowsSparse.Mark(handle);
    }

    [SupportedOSPlatform("windows")]
    private static partial class WindowsSparse
    {
        private const uint FsctlSetSparse = 0x000900C4;

        [LibraryImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeviceIoControl(
            SafeFileHandle device, uint controlCode, nint inBuffer, uint inBufferSize,
            nint outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

        // No input buffer marks the file sparse, as FILE_SET_SPARSE_BUFFER's
        // SetSparse = TRUE would.
        public static bool Mark(SafeFileHandle handle) =>
            DeviceIoControl(handle, FsctlSetSparse, 0, 0, 0, 0, out _, 0);
    }
}
