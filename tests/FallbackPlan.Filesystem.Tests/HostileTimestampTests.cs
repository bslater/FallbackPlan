using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Filesystem.Tests;

/// <summary>
/// Files whose timestamps are outside the range anybody designed for
/// (architecture 06 §3).
/// </summary>
/// <remarks>
/// <para>
/// The surveyed changelog records invalid timestamps preventing files from
/// being backed up at all and, separately, timestamps drifting far enough to
/// fail validation. A timestamp is
/// attacker-influenced and accident-influenced input: `touch -t` sets any
/// value a user likes, archive extraction restores whatever the archive said,
/// a dead CMOS battery writes 1970, and a Windows FILETIME of zero reads as
/// 1601.
/// </para>
/// <para>
/// The requirement is narrow and absolute: <b>a strange timestamp may cost
/// fidelity in that one field and must never cost the file</b>. Refusing to
/// back up a file because its date looks wrong is the failure the surveyed
/// product shipped a fix for, and it is worse than recording no date at all.
/// </para>
/// </remarks>
[TestClass]
public sealed class HostileTimestampTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-timestamp-tests", Guid.NewGuid().ToString("n"));

    private readonly LocalFileSystemSource _source = new();

    public HostileTimestampTests() => Directory.CreateDirectory(_root);

    private string Write(string name, DateTime modifiedUtc)
    {
        var full = Path.Combine(_root, name);
        File.WriteAllText(full, "content");
        File.SetLastWriteTimeUtc(full, modifiedUtc);
        return full;
    }

    private async Task<List<ScanEntry>> ScanAsync()
    {
        var entries = new List<ScanEntry>();
        await foreach (var scanEvent in _source.ScanAsync(_root, new ScanOptions(), CancellationToken.None))
        {
            if (scanEvent is ScanEvent.Leaf leaf)
            {
                entries.Add(leaf.Entry);
            }

            Assert.IsNotInstanceOfType<ScanEvent.Failure>(
                scanEvent, "a timestamp must never turn into a capture failure");
        }

        return entries;
    }

    /// <summary>
    /// The whole point, stated once over the full range of awkward values: the
    /// file is captured, whatever its clock says.
    /// </summary>
    [TestMethod]
    public async Task AFileWithAStrangeModificationTime_IsStillCaptured()
    {
        // Each is a real thing a filesystem hands back.
        Write("epoch.txt", DateTime.UnixEpoch);                                  // 1970-01-01, a dead clock
        Write("pre-epoch.txt", new DateTime(1960, 6, 15, 0, 0, 0, DateTimeKind.Utc));   // older than Unix time
        Write("filetime-zero.txt", new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // Windows FILETIME 0
        Write("far-future.txt", new DateTime(2999, 12, 31, 23, 59, 59, DateTimeKind.Utc));
        Write("ordinary.txt", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));

        var entries = await ScanAsync();

        SequenceAssert.AreEqual(
            ["epoch.txt", "far-future.txt", "filetime-zero.txt", "ordinary.txt", "pre-epoch.txt"],
            entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A timestamp the epoch cannot express is reported as absent, never as a
    /// number. <see cref="EntryMetadata.ModifiedAt"/> is unsigned milliseconds
    /// since 1970, so a 1960 date has no representation — and the one thing it
    /// must not become is a plausible-looking far-future value, which is what
    /// an unchecked cast of a negative produces.
    /// </summary>
    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "the POSIX stat path is what this asserts; the Windows path is the case below")]
    public async Task APreEpochModificationTime_IsReportedAsAbsentRatherThanAsAHugeNumber()
    {
        Write("pre-epoch.txt", new DateTime(1960, 6, 15, 0, 0, 0, DateTimeKind.Utc));

        var entry = Assert.ContainsSingle(await ScanAsync());
        var modified = entry.Metadata.ModifiedAt;

        // Absent is the right answer. A wrapped negative would land around
        // 1.8e19, which is the specific wrong answer being excluded.
        Assert.IsNull(modified, $"a 1960 date was recorded as {modified}");
    }

    /// <summary>
    /// The same property on Windows, where the conversion goes through
    /// <c>FileInfo</c> rather than <c>stat</c>.
    /// </summary>
    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "exercises the Windows stat path specifically")]
    public async Task OnWindows_APreEpochModificationTime_IsAlsoReportedAsAbsent()
    {
        Write("pre-epoch.txt", new DateTime(1960, 6, 15, 0, 0, 0, DateTimeKind.Utc));

        var entry = Assert.ContainsSingle(await ScanAsync());

        Assert.IsNull(entry.Metadata.ModifiedAt, $"a 1960 date was recorded as {entry.Metadata.ModifiedAt}");
    }

    /// <summary>
    /// A representable timestamp must be recorded accurately, so the test
    /// above is read as "absent because unrepresentable" rather than as
    /// "timestamps are not captured".
    /// </summary>
    [TestMethod]
    public async Task AnOrdinaryModificationTime_IsRecordedToTheMillisecond()
    {
        var when = new DateTime(2026, 6, 1, 12, 34, 56, DateTimeKind.Utc);
        Write("ordinary.txt", when);

        var entry = Assert.ContainsSingle(await ScanAsync());

        Assert.IsNotNull(entry.Metadata.ModifiedAt);
        Assert.AreEqual(
            (ulong)new DateTimeOffset(when).ToUnixTimeMilliseconds(),
            entry.Metadata.ModifiedAt);
    }

    /// <summary>
    /// A far-future date is recorded as the filesystem reports it, not as the
    /// caller asked for it and not clamped to something the capture thinks
    /// more reasonable.
    /// </summary>
    /// <remarks>
    /// The oracle is deliberately <see cref="File.GetLastWriteTimeUtc"/>
    /// rather than the value written. Filesystems clamp: ext4 stores 34 bits
    /// of seconds and silently truncates the year 2999 to 2446, which this
    /// test observed on the container it was written on. That clamp is the
    /// filesystem's, and a backup that faithfully records what `stat` says is
    /// correct — asserting against the requested value would have made this
    /// test a report on the temp directory's format rather than on the
    /// capture.
    /// </remarks>
    [TestMethod]
    public async Task AFarFutureModificationTime_IsRecordedAsTheFilesystemReportsIt()
    {
        var full = Write("far-future.txt", new DateTime(2999, 12, 31, 23, 59, 59, DateTimeKind.Utc));
        var asStored = new DateTimeOffset(File.GetLastWriteTimeUtc(full)).ToUnixTimeMilliseconds();

        var entry = Assert.ContainsSingle(await ScanAsync());

        Assert.AreEqual((ulong)asStored, entry.Metadata.ModifiedAt);
        Assert.IsGreaterThan(
            (ulong)new DateTimeOffset(new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            entry.Metadata.ModifiedAt!.Value,
            "a future date was flattened to something near the present rather than carried through");
    }

    /// <summary>
    /// And the revalidation stat — the check that catches a file changing
    /// underneath the reader — must survive the same values, since it compares
    /// the very field that has no representation.
    /// </summary>
    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "drives the POSIX stat path directly")]
    public void RevalidatingAFileWithAnUnrepresentableTime_DoesNotThrow()
    {
        var full = Write("pre-epoch.txt", new DateTime(1960, 6, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.IsTrue(LocalFileSystemSource.TryStat(full, out var stat));
        Assert.IsNull(stat.ModifiedAtMs);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
