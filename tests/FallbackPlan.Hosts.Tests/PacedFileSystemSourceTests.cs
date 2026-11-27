using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The source a paced backup reads through asks the source it paces to free
/// an online-only file the run fetched, as an unpaced backup's does, and
/// gives back that source's answer (ADR-0185 Amendment 2, FR-SNP-009). Under
/// the CPU limit its walk is looked at too, every sixty-four events, and every
/// event passes through unchanged; a byte limit alone leaves the walk alone,
/// because a stat moves no bytes (NFR-PERF-013, ADR-0200 Amendment 2).
/// </summary>
[TestClass]
public sealed class PacedFileSystemSourceTests
{
    [TestMethod]
    public void FreeAgain_AsksTheSourceItPaces_AndGivesItsAnswer()
    {
        Assert.IsTrue(ByteRate.TryParse("64 KiB/s", out var rate, out var defect), defect);
        var inner = new FreeingSource();
        var paced = PacedFileSystemSource.Over(inner, new BackgroundPace(new ByteRateLimiter(rate!, PacingClock.System), null));
        Assert.AreNotSame(inner, paced, "the premise: a rate gives a pacing source");

        var entry = new ScanEntry
        {
            RelativePath = "fetched.bin",
            NameBytes = "fetched.bin"u8.ToArray(),
            NameNormalisation = NameNormalisation.Nfc,
            Kind = ScanEntryKind.File,
            Length = 1,
            Metadata = EntryMetadata.Empty,
            FullPath = "/source/fetched.bin",
            OnlineOnly = true,
        };

        Assert.AreEqual("the provider said no", paced.FreeAgain(entry));
        Assert.AreEqual("/source/fetched.bin", Assert.ContainsSingle(inner.Freed));
    }

    [TestMethod]
    public async Task Scan_UnderTheCpuLimit_LooksEverySixtyFourEvents_AndPassesEachThrough()
    {
        var meter = new CountingMeter();
        var cpu = new CpuLimiter(Share("50%"), meter.Meter, Instant());
        var readingsBefore = meter.Readings;
        var paced = PacedFileSystemSource.Over(new ListingSource(200), new BackgroundPace(null, cpu));

        var seen = new List<string>();
        await foreach (var scanEvent in paced.ScanAsync("/source", new ScanOptions(), CancellationToken.None))
        {
            Assert.IsInstanceOfType<ScanEvent.Leaf>(scanEvent, out var leaf);
            seen.Add(leaf.Entry.RelativePath);
        }

        CollectionAssert.AreEqual(ListingSource.Names(200), seen, "pacing must not drop, add or reorder an event");
        Assert.AreEqual(200 / 64, meter.Readings - readingsBefore, "a look every sixty-four events, and only then");
    }

    [TestMethod]
    public async Task Scan_UnderAByteLimitAlone_IsNotLookedAt()
    {
        Assert.IsTrue(ByteRate.TryParse("64 KiB/s", out var rate, out var defect), defect);
        var bytes = new ByteRateLimiter(rate!, Instant());
        var paced = PacedFileSystemSource.Over(new ListingSource(200), new BackgroundPace(bytes, null));

        var count = 0;
        await foreach (var _ in paced.ScanAsync("/source", new ScanOptions(), CancellationToken.None))
        {
            count++;
        }

        Assert.AreEqual(200, count);
        Assert.AreEqual(0L, bytes.BytesPaced, "a stat moves no bytes, and the walk is charged none");
    }

    private static CpuShare Share(string text)
    {
        Assert.IsTrue(CpuShare.TryParse(text, out var share, out var defect), defect);
        return share!;
    }

    private static PacingClock Instant() => new(static () => 0, static (_, _) => Task.CompletedTask);

    private sealed class CountingMeter
    {
        private int _readings;

        public CountingMeter() =>
            Meter = new CpuMeter(
                () =>
                {
                    Interlocked.Increment(ref _readings);
                    return TimeSpan.Zero;
                },
                processorCount: 1);

        public CpuMeter Meter { get; }

        public int Readings => Volatile.Read(ref _readings);
    }

    private sealed class ListingSource(int count) : IFileSystemSource
    {
        public static List<string> Names(int count) => [.. Enumerable.Range(0, count).Select(index => $"file-{index:D4}.txt")];

        public SourceFilesystemInfo Probe(string rootPath) => throw new NotSupportedException();

        public RevalidationProbe? Revalidate(ScanEntry entry) => throw new NotSupportedException();

        public async IAsyncEnumerable<ScanEvent> ScanAsync(
            string rootPath, ScanOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var name in Names(count))
            {
                await Task.Yield();
                yield return new ScanEvent.Leaf(new ScanEntry
                {
                    RelativePath = name,
                    NameBytes = System.Text.Encoding.UTF8.GetBytes(name),
                    NameNormalisation = NameNormalisation.Nfc,
                    Kind = ScanEntryKind.File,
                    Length = 1,
                    Metadata = EntryMetadata.Empty,
                    FullPath = $"{rootPath}/{name}",
                });
            }
        }

        public Stream OpenRead(ScanEntry entry) => throw new NotSupportedException();

        public Stream OpenAlternateStream(ScanEntry entry, string streamName) => throw new NotSupportedException();

        public ulong? DeviceOf(string path) => null;

        public string? FreeAgain(ScanEntry entry) => null;
    }

    private sealed class FreeingSource : IFileSystemSource
    {
        public List<string> Freed { get; } = [];

        public SourceFilesystemInfo Probe(string rootPath) => throw new NotSupportedException();

        public RevalidationProbe? Revalidate(ScanEntry entry) => throw new NotSupportedException();

        public IAsyncEnumerable<ScanEvent> ScanAsync(string rootPath, ScanOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Stream OpenRead(ScanEntry entry) => throw new NotSupportedException();

        public Stream OpenAlternateStream(ScanEntry entry, string streamName) => throw new NotSupportedException();

        public ulong? DeviceOf(string path) => null;

        public string? FreeAgain(ScanEntry entry)
        {
            Freed.Add(entry.FullPath);
            return "the provider said no";
        }
    }
}
