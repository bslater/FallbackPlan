using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Application.Resources;

namespace FallbackPlan.Application;

/// <summary>
/// The restores under way that no service runs, said where a service looks
/// (FR-GC-003, ADR-0172 Amendment 6). A restore in direct mode holds a marker
/// for its run under a state directory while it runs, and a service working
/// in that state directory asks whether any is held for a set's repository
/// before it collects, converges a destination or instructs a peer.
/// </summary>
/// <remarks>
/// <para>
/// A marker is an open handle, as the writer role is (ADR-0028 §4): the
/// operating system lets it go when its holder dies, so a run killed before it
/// could end holds nothing a moment later, and no clock decides it. The file
/// is only where the handle is taken. One nobody holds says nothing, and the
/// first look that finds it so clears it.
/// </para>
/// <para>
/// A handle deletes its file as it closes, before it lets the file go, so a
/// look never clears a file a run still holds. A run that opened the file a
/// look was clearing finds it gone once it holds it, and takes it again.
/// </para>
/// </remarks>
public static class RestoresUnderWay
{
    private const string FolderName = "restores-under-way";

    private const string Suffix = ".lock";

    private const int Attempts = 20;

    /// <summary>
    /// Holds a marker for <paramref name="run"/>, a restore of
    /// <paramref name="repositoryId"/>, under <paramref name="stateDirectory"/>
    /// until the hold is disposed.
    /// </summary>
    /// <param name="stateDirectory">The state directory a service working there looks in.</param>
    /// <param name="repositoryId">The repository the run restores from.</param>
    /// <param name="run">The run's name: lowercase hex digits.</param>
    /// <returns>The hold; disposing it lets the marker go.</returns>
    /// <exception cref="ArgumentException"><paramref name="run"/> is not lowercase hex digits.</exception>
    /// <exception cref="IOException">The marker could not be held.</exception>
    /// <exception cref="UnauthorizedAccessException">The state directory may not be written.</exception>
    public static IDisposable Hold(string stateDirectory, RepositoryId repositoryId, string run)
    {
        ArgumentException.ThrowIfNullOrEmpty(stateDirectory);
        if (!IsRunName(run))
        {
            throw new ArgumentException(Strings.FormatRestoresUnderWay_RunNotHex(run), nameof(run));
        }

        var folder = FolderOf(stateDirectory, repositoryId);
        var marker = Path.Combine(folder, run + Suffix);
        for (var attempt = 1; ; attempt++)
        {
            Directory.CreateDirectory(folder);
            try
            {
                var hold = new FileStream(
                    marker, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
                if (File.Exists(marker))
                {
                    return hold;
                }

                // A look cleared the file as this took it, so what this holds
                // is no longer where a service looks.
                hold.Dispose();
            }
            catch (IOException) when (attempt < Attempts)
            {
                // A look holds the file for the moment it takes to clear it.
            }

            if (attempt >= Attempts)
            {
                throw new IOException(Strings.FormatRestoresUnderWay_MarkerNotHeld(marker));
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Whether a run holds a marker for <paramref name="repositoryId"/> under
    /// <paramref name="stateDirectory"/>. A marker nobody holds is cleared.
    /// </summary>
    /// <param name="stateDirectory">The state directory.</param>
    /// <param name="repositoryId">The repository.</param>
    /// <returns>Whether one does.</returns>
    public static bool AnyFor(string stateDirectory, RepositoryId repositoryId)
    {
        ArgumentException.ThrowIfNullOrEmpty(stateDirectory);
        List<string> markers;
        try
        {
            markers = [.. Directory.EnumerateFiles(FolderOf(stateDirectory, repositoryId), "*" + Suffix)];
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        foreach (var marker in markers)
        {
            try
            {
                // Taken at once, so nobody held it: a run that has gone left
                // it, and it goes as this lets it go.
                using var look = new FileStream(
                    marker, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Let go as this looked.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Held; or kept from this process, which cannot then say the
                // restore has ended.
                return true;
            }
        }

        return false;
    }

    private static string FolderOf(string stateDirectory, RepositoryId repositoryId) =>
        Path.Combine(stateDirectory, FolderName, repositoryId.ToString());

    private static bool IsRunName(string? run) =>
        run is { Length: > 0 and <= 64 } && run.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
}
