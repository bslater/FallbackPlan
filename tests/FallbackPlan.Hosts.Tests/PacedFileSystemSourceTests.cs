using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The source a paced backup reads through asks the source it paces to free
/// an online-only file the run fetched, as an unpaced backup's does, and
/// gives back that source's answer (ADR-0185 Amendment 2, FR-SNP-009).
/// </summary>
[TestClass]
public sealed class PacedFileSystemSourceTests
{
    [TestMethod]
    public void FreeAgain_AsksTheSourceItPaces_AndGivesItsAnswer()
    {
        Assert.IsTrue(ByteRate.TryParse("64 KiB/s", out var rate, out var defect), defect);
        var inner = new FreeingSource();
        var paced = PacedFileSystemSource.Over(inner, new ByteRateLimiter(rate!, PacingClock.System));
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
