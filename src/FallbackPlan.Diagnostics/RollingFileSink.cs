using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Bodu;

namespace FallbackPlan.Diagnostics;

/// <summary>
/// The durable half of the log: a size-capped file in the state directory, with
/// a bounded number of rolled predecessors, written by a background drain
/// (ADR-0043 §6).
/// </summary>
/// <remarks>
/// <para>
/// <b>The caller never touches the disk.</b> <see cref="Write"/> hands the
/// rendered line to a bounded channel and returns; one background task owns the
/// file and drains it. The alternative — writing and flushing inline under a
/// lock — put a lock acquisition, a write and a flush syscall on the archive
/// loop for every logged line, in a loop that runs once per file. A backup
/// product must not get measurably slower because somebody raised the log
/// level to find out why it was slow.
/// </para>
/// <para>
/// <b>The queue is bounded and drops the oldest under pressure.</b> An
/// unbounded queue in front of a slow disk is a memory leak with a delay on it.
/// The record is already durable in <see cref="LogRing"/>, so a dropped file
/// line costs history in one sink, never the record itself — and
/// <see cref="Dropped"/> counts what went, because a log that quietly omits
/// lines is worse than one that admits to a gap.
/// </para>
/// <para>
/// This file records <b>plaintext paths</b>. It sits inside the trust boundary,
/// in a directory only the service account may read, on the machine that
/// already holds the files themselves — and a support log that cannot name the
/// file that failed answers almost none of the questions it exists to answer
/// (architecture 10 §4, as amended by ADR-0043).
/// </para>
/// </remarks>
public sealed class RollingFileSink : IAsyncDisposable, IDisposable
{
    private const string Prefix = "fallbackplan-";
    private const string Suffix = ".log";

    /// <summary>How many rendered lines may await the writer before the oldest are dropped.</summary>
    private const int QueueCapacity = 4_096;

    private readonly string _directory;
    private readonly long _maximumBytes;
    private readonly int _retain;
    private readonly Channel<string> _pending;
    private readonly Task _draining;
    private readonly CancellationTokenSource _stopping = new();

    private StreamWriter? _writer;
    private long _written;
    private long _dropped;

    /// <summary>Opens the sink and starts its background writer.</summary>
    /// <param name="directory">Where the files live.</param>
    /// <param name="maximumBytes">How large one file may grow before it rolls.</param>
    /// <param name="retain">How many rolled files to keep, newest first.</param>
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

        _pending = Channel.CreateBounded<string>(
            new BoundedChannelOptions(QueueCapacity)
            {
                // Oldest first, matching the ring: what just happened is what
                // somebody is trying to read.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref _dropped));

        _draining = Task.Run(DrainAsync);
    }

    /// <summary>The file currently being written.</summary>
    public string CurrentPath { get; private set; } = string.Empty;

    /// <summary>How many lines were dropped because the writer fell behind.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Queues one rendered line. Returns immediately; the background writer
    /// puts it on disk.
    /// </summary>
    /// <param name="line">The line to append.</param>
    public void Write(string line)
    {
        ThrowHelper.ThrowIfNull(line);

        // DropOldest means this only fails once the channel is completed, which
        // is shutdown — and a line lost on the way down is not worth a throw
        // from inside a logging call.
        _ = _pending.Writer.TryWrite(line);
    }

    /// <summary>Waits for the queue to reach disk — for a test, or an orderly shutdown.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        // The drain loop flushes after each batch, so an empty queue plus a
        // flushed writer is the whole condition.
        while (_pending.Reader.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            while (await _pending.Reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false))
            {
                while (_pending.Reader.TryRead(out var line))
                {
                    Append(line);
                }

                // One flush per batch rather than per line: the syscall is the
                // expensive part, and a batch is exactly the work that arrived
                // while the last one was being written.
                try
                {
                    _writer?.Flush();
                }
#pragma warning disable CA1031 // A sink that throws turns a diagnosable failure into an undiagnosable one.
                catch (Exception)
                {
                    Close();
                }
#pragma warning restore CA1031
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Whatever is still queued is written by DisposeAsync.
        }
    }

    private void Append(string line)
    {
        try
        {
            Open();

            // Measured in BYTES, not characters. A backup product logs paths,
            // and a path full of non-ASCII characters costs more UTF-8 bytes
            // than it has chars — counting chars would let the file overshoot
            // the cap it was given, quietly and only for some users.
            var bytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;

            if (_written > 0 && _written + bytes > _maximumBytes)
            {
                Roll();
                Open();
            }

            _writer!.WriteLine(line);
            _written += bytes;
        }
#pragma warning disable CA1031 // deliberate: see the class remarks
        catch (Exception)
        {
            // Losing a log line is regrettable. Failing a backup because a log
            // line could not be written is not a trade this product makes —
            // the ring buffer still holds the record and the operation
            // continues. Closing here means the next line retries the open.
            Close();
        }
#pragma warning restore CA1031
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

    /// <summary>Stops the writer after putting whatever is queued on disk.</summary>
    /// <remarks>
    /// The order here is the whole of the method. The background drain owns
    /// <see cref="_writer"/> and is the only thread allowed to touch it, so
    /// shutdown must <b>stop and await that task before</b> draining the
    /// remainder itself. Draining first — which is the obvious way to write
    /// this — puts two threads in <c>Append</c> at once, and the loser finds
    /// the <see cref="StreamWriter"/> disposed underneath it.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // No more producers.
        _pending.Writer.TryComplete();

        // Stop the drain and wait for it to actually be gone; only then is
        // this thread the single owner of the writer.
        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await _draining.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The drain's normal way of ending.
        }

        // Whatever it did not reach. A shutdown line is often the most
        // interesting one in the file.
        while (_pending.Reader.TryRead(out var line))
        {
            Append(line);
        }

        try
        {
            _writer?.Flush();
        }
#pragma warning disable CA1031 // deliberate: nothing useful remains to report to
        catch (Exception)
        {
        }
#pragma warning restore CA1031

        Close();
        _stopping.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
