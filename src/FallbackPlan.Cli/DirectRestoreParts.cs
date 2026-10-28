using System.Security.Cryptography;
using System.Text;

namespace FallbackPlan.Cli;

/// <summary>
/// Where a restore in direct mode keeps the parts of the files it assembles
/// (FR-RST-008, ADR-0135): a folder under the state directory named by where
/// the run lands, since a run in direct mode has no name a resume could ask
/// it by. A resume to the same place finds them.
/// </summary>
/// <remarks>
/// The folders sit apart from the service's <c>restore-parts</c>, which a
/// service sweeps at start of every run it has no record of. A part holds
/// the plaintext of a backup's file, as the service's do, so it stays under
/// the state directory and never where the run writes.
/// </remarks>
internal static class DirectRestoreParts
{
    /// <summary>The folder every destination's parts are kept under, in a state directory.</summary>
    /// <param name="stateDirectory">The command line's state directory.</param>
    public static string RootUnder(string stateDirectory) => Path.Combine(stateDirectory, "direct-restore-parts");

    /// <summary>The folder a run landing at <paramref name="outputDirectory"/> keeps its parts in.</summary>
    /// <param name="stateDirectory">The command line's state directory.</param>
    /// <param name="outputDirectory">Where the run lands.</param>
    public static string Of(string stateDirectory, string outputDirectory) => Of(stateDirectory, [outputDirectory]);

    /// <summary>
    /// The folder a run landing at every one of <paramref name="outputDirectories"/>
    /// keeps its parts in: a run to the original location of a set with
    /// several roots keeps one folder for all of them.
    /// </summary>
    /// <param name="stateDirectory">The command line's state directory.</param>
    /// <param name="outputDirectories">Where the run lands.</param>
    public static string Of(string stateDirectory, IEnumerable<string> outputDirectories)
    {
        var where = string.Join(
            '\n',
            outputDirectories
                .Select(directory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)))
                .Order(StringComparer.Ordinal));
        return Path.Combine(RootUnder(stateDirectory), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(where)))[..32]);
    }

    /// <summary>
    /// The bytes a folder's parts hold of the backup's files (ADR-0137), or
    /// none where it keeps none or they will not be read.
    /// </summary>
    /// <param name="parts">The folder.</param>
    public static long Held(string parts)
    {
        try
        {
            var folder = new DirectoryInfo(parts);
            return folder.Exists ? folder.EnumerateFiles().Sum(part => part.Length) : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Removes a folder's parts: a run there finished, and nothing will take
    /// them up. Best effort: a part not removed is taken up or written again
    /// by the next run there, and let go when one finishes.
    /// </summary>
    /// <param name="parts">The folder.</param>
    public static void LetGo(string parts)
    {
        try
        {
            if (Directory.Exists(parts))
            {
                Directory.Delete(parts, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
