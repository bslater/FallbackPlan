using System.Diagnostics;
using System.Text;
using FallbackPlan.Diagnostics;

namespace FallbackPlan.Diagnostics.Tests;

/// <summary>
/// The durable half of the log (ADR-0043 §6): size-capped, retention-bounded,
/// owner-only, and written off the caller's thread.
/// </summary>
[TestClass]
public sealed class RollingFileSinkTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fbp-log-sink-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string[] RolledFiles() =>
        Directory.GetFiles(_directory, "fallbackplan-*.log")
            .Where(path => !path.EndsWith("fallbackplan-current.log", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private string CurrentText() =>
        File.ReadAllText(Path.Combine(_directory, "fallbackplan-current.log"));

    private static async Task SettleAsync(RollingFileSink sink)
    {
        await sink.FlushAsync();

        // The drain flushes after each batch; give the last batch a moment to
        // land before reading the file back.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(10);
            if (sink is not null)
            {
                break;
            }
        }
    }

    [TestMethod]
    public async Task Sink_LinesWritten_ReachTheFile()
    {
        await using var sink = new RollingFileSink(_directory, maximumBytes: 1024 * 1024, retain: 3);
        sink.Write("the first thing that happened");
        sink.Write("the second thing that happened");
        await SettleAsync(sink);

        var text = CurrentText();
        Assert.Contains("the first thing that happened", text, StringComparison.Ordinal);
        Assert.Contains("the second thing that happened", text, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sink_TheSizeCapIsReached_RollsAndKeepsWriting()
    {
        await using var sink = new RollingFileSink(_directory, maximumBytes: 512, retain: 5);

        for (var index = 0; index < 60; index++)
        {
            sink.Write($"line {index} padded out to make the cap arrive quickly {new string('x', 40)}");
        }

        await SettleAsync(sink);

        Assert.IsNotEmpty(RolledFiles(), "the cap must actually have been reached");
        Assert.IsTrue(File.Exists(Path.Combine(_directory, "fallbackplan-current.log")));
    }

    [TestMethod]
    public async Task Sink_ManyRolls_KeepsOnlyTheRetainedCount()
    {
        const int Retain = 3;
        await using var sink = new RollingFileSink(_directory, maximumBytes: 256, retain: Retain);

        for (var index = 0; index < 200; index++)
        {
            sink.Write($"line {index} {new string('y', 60)}");
            if (index % 20 == 0)
            {
                await SettleAsync(sink);
            }
        }

        await SettleAsync(sink);

        // Disk that used to be the user's: a backup product filling one with
        // its own diagnostics has done the thing it exists to prevent.
        Assert.IsTrue(
            RolledFiles().Length <= Retain,
            $"retention keeps at most {Retain} rolled files; found {RolledFiles().Length}");
    }

    [TestMethod]
    public async Task Sink_ALineOfNonAsciiPaths_IsMeasuredInBytesNotCharacters()
    {
        // A path of non-ASCII characters costs more UTF-8 bytes than it has
        // chars. Counting chars would let the file quietly overshoot the cap
        // it was given — and only for the users whose filenames are not
        // English, which is the worst way for a limit to be wrong.
        const int Cap = 2_048;
        await using var sink = new RollingFileSink(_directory, maximumBytes: Cap, retain: 10);

        var wide = new string('你', 200);   // 3 UTF-8 bytes each
        for (var index = 0; index < 40; index++)
        {
            sink.Write(wide);
        }

        await SettleAsync(sink);

        foreach (var path in RolledFiles())
        {
            Assert.IsTrue(
                new FileInfo(path).Length <= Cap + (Encoding.UTF8.GetByteCount(wide) + 2),
                $"{Path.GetFileName(path)} is {new FileInfo(path).Length} bytes against a {Cap}-byte cap");
        }
    }

    [TestMethod]
    public void Sink_Writing_DoesNotBlockTheCaller()
    {
        // The reason this sink is queued at all: writing inline put a lock, a
        // write and a flush syscall on the archive loop for every logged line.
        // A backup must not get slower because somebody raised the log level
        // to find out why it was slow.
        using var sink = new RollingFileSink(_directory, maximumBytes: 1024 * 1024, retain: 3);

        var clock = Stopwatch.StartNew();
        for (var index = 0; index < 10_000; index++)
        {
            sink.Write($"line {index}");
        }

        clock.Stop();

        Assert.IsTrue(
            clock.Elapsed < TimeSpan.FromSeconds(2),
            $"10,000 queued lines took {clock.ElapsedMilliseconds} ms — the caller is waiting on the disk");
    }

    [TestMethod]
    public void Sink_MoreLinesThanTheQueueHolds_DropsThemAndSaysSo()
    {
        // An unbounded queue in front of a slow disk is a memory leak with a
        // delay on it. Dropping is the right answer; silence about it is not.
        using var sink = new RollingFileSink(_directory, maximumBytes: 1024 * 1024, retain: 3);

        for (var index = 0; index < 200_000; index++)
        {
            sink.Write($"line {index} {new string('z', 100)}");
        }

        Assert.IsTrue(
            sink.Dropped > 0,
            "the drill must actually have outrun the writer for this to prove anything");
    }

    [TestMethod]
    public async Task Sink_Disposed_WritesWhatWasStillQueued()
    {
        // A shutdown line is often the most interesting one in the file.
        var sink = new RollingFileSink(_directory, maximumBytes: 1024 * 1024, retain: 3);
        sink.Write("the very last thing before shutdown");
        await sink.DisposeAsync();

        Assert.Contains("the very last thing before shutdown", CurrentText(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sink_OnAPosixHost_IsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file modes do not apply on Windows.");
            return;
        }

        await using var sink = new RollingFileSink(_directory, maximumBytes: 1024 * 1024, retain: 3);
        sink.Write("a line naming somebody's files");
        await SettleAsync(sink);

        var mode = File.GetUnixFileMode(Path.Combine(_directory, "fallbackplan-current.log"));

        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
