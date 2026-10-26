using Bodu;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FallbackPlan.Domain;

/// <summary>
/// The call a restore gives a file it landed a second name with: a hard
/// link, the same file under another name (specification 06 §4 key 12;
/// FR-RST-004; ADR-0102).
/// </summary>
/// <remarks>
/// <para>
/// .NET has no call that makes one. POSIX's <c>link</c> and Windows's
/// <c>CreateHardLinkW</c> each refuse a name already taken, a file that is
/// not there, a folder, and a volume that keeps no links or is not the
/// file's own, and a refusal is answered as not made, so the restore can
/// restore that name as a file of its own and say why.
/// </para>
/// <para>
/// Sits beside <see cref="FileTimes"/> for the reason it gives: the
/// engine's restore and the standalone recovery tool both write restored
/// files, and Domain is the one assembly both may reach.
/// </para>
/// </remarks>
public static partial class HardLinks
{
    /// <summary>Gives the file at <paramref name="existing"/> the further name <paramref name="link"/>.</summary>
    /// <param name="existing">A file.</param>
    /// <param name="link">The new name, which must not be taken.</param>
    /// <returns>Whether <paramref name="link"/> now names that file: false wherever the platform refuses.</returns>
    public static bool TryCreate(string existing, string link)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(existing);
        ThrowHelper.ThrowIfNullOrWhiteSpace(link);

        return OperatingSystem.IsWindows()
            ? Windows.CreateHardLink(link, existing, 0)
            : Posix.Link(existing, link) == 0;
    }

    private static partial class Posix
    {
        [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        public static partial int Link(string existing, string link);
    }

    [SupportedOSPlatform("windows")]
    private static partial class Windows
    {
        [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CreateHardLink(string link, string existing, nint security);
    }
}
