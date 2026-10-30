using Bodu;

namespace FallbackPlan.Domain;

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
/// directory is prevented by <c>StateDirectoryLock</c> (in Application — this type
/// deliberately sits below it) and the single
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

    /// <summary>Reads a file this type writes, whole, even while a replace of it is in flight.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The file's text, from the version its name held when the read began.</returns>
    /// <remarks>
    /// A replace renames the new file into place, and the rename holds that
    /// file open for deletion until it returns. Windows refuses any open that
    /// does not share deletion with such a handle, and <c>File.ReadAllText</c>
    /// shares reading only, so a reader arriving in that moment would fail
    /// where it should read one version or the other. Sharing deletion changes
    /// nothing else about the read: it sees one whole version, never a mixture.
    /// </remarks>
    public static string ReadAllText(string path)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Reads a file whole, as bytes, even while a rename of it into place is in flight.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The file's bytes, from the version its name held when the read began.</returns>
    /// <remarks>
    /// For a file a store replaces by renaming a temporary of its own into
    /// place, which holds the file open for deletion exactly as
    /// <see cref="WriteAllText"/>'s rename does. It shares deletion for the
    /// reason <see cref="ReadAllText"/> gives.
    /// </remarks>
    public static byte[] ReadAllBytes(string path)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Reads a file's lines, without their endings, even while a rename of it into place is in flight.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The file's lines, from the version its name held when the read began.</returns>
    /// <remarks>Shares deletion for the reason <see cref="ReadAllText"/> gives.</remarks>
    public static string[] ReadAllLines(string path)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    /// <summary>
    /// The number of replace attempts before a contended destination is
    /// reported as a failure rather than retried again.
    /// </summary>
    public const int ReplaceAttempts = 12;

    private const int LongestReplaceWaitMs = 128;

    /// <summary>
    /// The least a replace waits in all, across its attempts, for a contended
    /// destination to free: what a writer or reader holding it briefly is
    /// given before the replace fails (ADR-0010 Amendment 10).
    /// </summary>
    public static TimeSpan LeastReplaceWait =>
        TimeSpan.FromMilliseconds(Enumerable.Range(1, ReplaceAttempts - 1).Sum(ReplaceWaitFloor));

    /// <summary>
    /// The most a replace waits in all before it reports a destination that
    /// never frees.
    /// </summary>
    public static TimeSpan MostReplaceWait =>
        TimeSpan.FromMilliseconds(Enumerable.Range(1, ReplaceAttempts - 1).Sum(ReplaceWaitCeiling));

    /// <summary>
    /// Whether a failed replace attempt is contention worth retrying, or a
    /// defect to report at once.
    /// </summary>
    /// <param name="exception">What the replace attempt threw.</param>
    /// <remarks>
    /// <para>
    /// Public because it is the part of the retry that can be proven on every
    /// platform. The contention itself is Windows' alone: <c>rename(2)</c> is
    /// atomic and simply wins or loses, so no POSIX test can manufacture the
    /// <c>ERROR_ACCESS_DENIED</c> this absorbs. On Windows a reader holding the
    /// destination without sharing deletion manufactures it, and that is where
    /// the waits are held (ADR-0010 Amendment 10).
    /// </para>
    /// <para>
    /// The subtlety worth pinning: <see cref="FileNotFoundException"/> and
    /// <see cref="DirectoryNotFoundException"/> <i>are</i>
    /// <see cref="IOException"/>s, and they are the two that must not be
    /// retried. A missing source or directory is not contention — it is a
    /// defect, and the waits only delay the report of it.
    /// </para>
    /// </remarks>
    public static bool IsReplaceContention(Exception exception) =>
        exception is UnauthorizedAccessException
            or (IOException and not (FileNotFoundException or DirectoryNotFoundException));

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
            catch (Exception exception) when (attempt < ReplaceAttempts && IsReplaceContention(exception))
            {
                // A missing source or directory is not contention and is not
                // retried — it is a defect, and delaying it only hides it.
                // Each wait is a random point in the upper half of its
                // ceiling, so writers that collided do not collide again in step.
                Thread.Sleep(Random.Shared.Next(ReplaceWaitFloor(attempt), ReplaceWaitCeiling(attempt) + 1));
            }
        }
    }

    /// <summary>The longest wait after replace attempt <paramref name="attempt"/>: doubling from 2 ms, up to 128 ms.</summary>
    private static int ReplaceWaitCeiling(int attempt) => Math.Min(LongestReplaceWaitMs, 2 << Math.Min(attempt - 1, 6));

    /// <summary>The shortest wait after replace attempt <paramref name="attempt"/>: half its ceiling.</summary>
    private static int ReplaceWaitFloor(int attempt) => ReplaceWaitCeiling(attempt) / 2;

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
