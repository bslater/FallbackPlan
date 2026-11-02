namespace FallbackPlan.TestSupport;

/// <summary>
/// Makes a Windows directory junction, the mount point an account needs no
/// privilege to make.
/// </summary>
public static class Junctions
{
    /// <summary>
    /// Makes a junction at <paramref name="path"/> naming the folder
    /// <paramref name="target"/>, and answers whether it was made.
    /// </summary>
    /// <remarks>
    /// <c>mklink</c> is a <c>cmd</c> built-in, so it is run through the
    /// shell. No managed call makes a mount point: .NET's
    /// <see cref="Directory.CreateSymbolicLink"/> makes a folder's symbolic
    /// link, which needs a privilege a junction does not.
    /// </remarks>
    /// <param name="path">Where the junction goes; nothing may stand there.</param>
    /// <param name="target">The absolute path of the folder it names.</param>
    public static bool Make(string path, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "mklink", "/J", path, target },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        if (process is null)
        {
            return false;
        }

        process.WaitForExit(TimeSpan.FromSeconds(30));
        return process.ExitCode == 0 && Directory.Exists(path);
    }
}
