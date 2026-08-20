using System.Globalization;
using System.Text;
using Bodu;

namespace FallbackPlan.Diagnostics;

/// <summary>
/// The durable half of the log: a size-capped file in the state directory, with
/// a bounded number of rolled predecessors (ADR-0043 §6).
/// </summary>
/// <remarks>
/// <para>
/// This file records <b>plaintext paths</b>. It sits inside the trust boundary,
/// in a directory only the service account may read, on the machine that
/// already holds the files themselves — and a support log that cannot name the
/// file that failed answers almost none of the questions it exists to answer
/// (architecture 10 §4, as amended by ADR-0043).
/// </para>
/// <para>
/// Retention is bounded because this is disk that used to be the user's. A
/// backup product that fills a disk with its own diagnostics has done the one
/// thing it exists to prevent.
/// </para>
/// </remarks>
public sealed class RollingFileSink : IDisposable
{
    private const string Prefix = "fallbackplan-";
    private const string Suffix = ".log";

    private readonly string _directory;
    private readonly long _maximumBytes;
    private readonly int _retain;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private long _written;

    /// <summary>Opens (or creates) the current log file.</summary>
    /// <param name="directory">Where the files live.</param>
    /// <param name="maximumBytes">How large one file may grow before it rolls.</param>
    /// <param name="retain">How many files to keep, newest first.</param>
    public RollingFileSink(string directory, long maximumBytes, int retain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retain);

        _directory = directory;
        _maximumBytes = maximumBytes;
        _retain = retain;

        Directory.CreateDirectory(directory);
        RestrictToOwner(directory);
    }

    /// <summary>The file currently being written.</summary>
    public string CurrentPath { get; private set; } = string.Empty;

    /// <summary>Appends one rendered line, rolling first if this would overflow.</summary>
    /// <param name="line">The line to append.</param>
    public void Write(string line)
    {
        ThrowHelper.ThrowIfNull(line);

        lock (_gate)
        {
            try
            {
                Open();

                if (_written + line.Length + Environment.NewLine.Length > _maximumBytes && _written > 0)
                {
                    Roll();
                    Open();
                }

                _writer!.WriteLine(line);
                _writer.Flush();
                _written += line.Length + Environment.NewLine.Length;
            }
#pragma warning disable CA1031 // A sink that throws turns a diagnosable failure into an undiagnosable one.
            catch (Exception)
            {
                // Losing a log line is regrettable. Taking down a backup
                // because a log line could not be written is not a trade this
                // product makes — the ring buffer still holds the record, and
                // the operation continues.
                Close();
            }
#pragma warning restore CA1031
        }
    }

    private void Open()
    {
        if (_writer is not null)
        {
            return;
        }

        CurrentPath = Path.Combine(_directory, $"{Prefix}current{Suffix}");
        var existing = new FileInfo(CurrentPath);
        _written = existing.Exists ? existing.Length : 0;

        _writer = new StreamWriter(
            new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        RestrictToOwner(CurrentPath);
    }

    private void Roll()
    {
        Close();

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var rolled = Path.Combine(_directory, $"{Prefix}{stamp}{Suffix}");

        // A second roll inside the same second must not throw over the first.
        for (var attempt = 1; File.Exists(rolled); attempt++)
        {
            rolled = Path.Combine(_directory, $"{Prefix}{stamp}-{attempt}{Suffix}");
        }

        File.Move(CurrentPath, rolled);
        _written = 0;
        Prune();
    }

    private void Prune()
    {
        var rolled = Directory.GetFiles(_directory, $"{Prefix}*{Suffix}")
            .Where(path => !path.EndsWith($"{Prefix}current{Suffix}", StringComparison.Ordinal))
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .Skip(_retain)
            .ToArray();

        foreach (var path in rolled)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Somebody is reading it. It will be pruned on the next roll.
            }
            catch (UnauthorizedAccessException)
            {
                // Likewise — never fail a backup over log housekeeping.
            }
        }
    }

    private void Close()
    {
        _writer?.Dispose();
        _writer = null;
    }

    /// <summary>
    /// Owner-only, where the platform has the concept. A log naming a person's
    /// files should not be world-readable on a shared machine.
    /// </summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var mode = Directory.Exists(path)
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);
        }
        catch (IOException)
        {
            // A filesystem without modes. The directory's own permissions stand.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            Close();
        }
    }
}
