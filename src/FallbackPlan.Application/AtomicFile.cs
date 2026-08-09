using Bodu;

namespace FallbackPlan.Application;

/// <summary>
/// Whole-file replacement that a crash cannot half-apply.
/// </summary>
/// <remarks>
/// <para>
/// The client-domain state files were written with <c>File.WriteAllText</c>,
/// which truncates first and writes second: a crash between the two leaves a
/// zero-length <c>state.json</c>, and losing that file loses the device's
/// writer identity. <c>FileSequenceStateStore</c> already used temp-plus-rename
/// for exactly this reason; this generalises it so the three client-domain
/// files share one durability story instead of three.
/// </para>
/// <para>
/// This is not the cross-process fix. Two processes writing one state
/// directory is prevented by <see cref="StateDirectoryLock"/> and the single
/// writer role (ADR-0028 §2) — atomic replacement only closes the case that
/// survives single ownership, which is a crash mid-write.
/// </para>
/// </remarks>
public static class AtomicFile
{
    /// <summary>Writes <paramref name="contents"/> to <paramref name="path"/>, replacing it atomically.</summary>
    /// <param name="path">The destination path.</param>
    /// <param name="contents">The text to write.</param>
    public static void WriteAllText(string path, string contents)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // A per-write temp name, not a fixed one: a fixed name is safe under
        // single ownership and silently wrong the moment it is not, which is
        // the kind of latent assumption this codebase has already paid for.
        var temporary = $"{path}.{Guid.NewGuid():n}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            Replace(temporary, path);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// The number of replace attempts before a contended destination is
    /// reported as a failure rather than retried again.
    /// </summary>
    private const int ReplaceAttempts = 10;

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="temporary"/>,
    /// absorbing the contention Windows reports and POSIX does not.
    /// </summary>
    /// <remarks>
    /// <see cref="File.Move(string, string, bool)"/> is
    /// <c>MoveFileEx(MOVEFILE_REPLACE_EXISTING)</c> on Windows, which fails with
    /// <c>ERROR_ACCESS_DENIED</c> while another thread is replacing the same
    /// destination; <c>rename(2)</c> is atomic and simply wins or loses the
    /// race. Each attempt here is still atomic, so the guarantee is unchanged —
    /// the retry absorbs contention, it does not weaken replacement.
    /// <para>
    /// Two writers to one state file are a bug the writer role prevents
    /// (ADR-0028 §2), which is exactly why this is worth handling: an
    /// assumption that holds until it does not is the kind this file already
    /// exists to remove.
    /// </para>
    /// </remarks>
    private static void Replace(string temporary, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                attempt < ReplaceAttempts &&
                exception is UnauthorizedAccessException
                    or (IOException and not (FileNotFoundException or DirectoryNotFoundException)))
            {
                // A missing source or directory is not contention and is not
                // retried — it is a defect, and delaying it only hides it.
                Thread.Sleep(attempt);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file is untidy, never incorrect.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
