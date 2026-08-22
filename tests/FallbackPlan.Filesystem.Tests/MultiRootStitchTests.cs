using System.Runtime.CompilerServices;
using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Filesystem.Tests;

/// <summary>
/// How several roots become one coordinate system (ADR-0040), and in
/// particular what happens to a <see cref="ScanEvent.Failure"/> on the way
/// through.
/// </summary>
/// <remarks>
/// <para>
/// Everything a multi-root scan emits is re-labelled so that rules, capture,
/// catalogue and prior-version lookup speak one set of paths. A failure is the
/// event where getting that wrong costs the most: it is the one an operator
/// reads when something did not get backed up, and an unprefixed
/// <c>Documents/report.pdf</c> in a set with three roots does not say
/// <em>which</em> Documents.
/// </para>
/// <para>
/// The source here is a stub rather than a real tree, because the behaviour
/// under test is the stitching, and a failure raised by a real filesystem is
/// exactly the thing that cannot be arranged reliably on every platform.
/// </para>
/// </remarks>
[TestClass]
public sealed class MultiRootStitchTests
{
    /// <summary>A source that replays a scripted stream per root path.</summary>
    private sealed class ScriptedSource(Dictionary<string, ScanEvent[]> byRoot) : IFileSystemSource
    {
        public SourceFilesystemInfo Probe(string rootPath) => throw new NotSupportedException();

        public RevalidationProbe? Revalidate(ScanEntry entry) => throw new NotSupportedException();

        public Stream OpenRead(ScanEntry entry) => throw new NotSupportedException();

        public Stream OpenAlternateStream(ScanEntry entry, string streamName) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ScanEvent> ScanAsync(
            string rootPath,
            ScanOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var scanEvent in byRoot[rootPath])
            {
                yield return scanEvent;
                await Task.Yield();
            }
        }
    }

    private static ScanEntry Directory(string relativePath, string name) => new()
    {
        RelativePath = relativePath,
        NameBytes = Encoding.UTF8.GetBytes(name),
        NameNormalisation = NameNormalisation.Unknown,
        Kind = ScanEntryKind.Directory,
        Length = 0,
        Metadata = EntryMetadata.Empty,
        FullPath = string.Empty,
    };

    private static async Task<List<ScanEvent>> StitchAsync(Dictionary<string, ScanEvent[]> byRoot)
    {
        var roots = byRoot.Keys
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new ScanRoot(path, Path.GetFileName(path)))
            .ToList();

        var collected = new List<ScanEvent>();
        await foreach (var scanEvent in MultiRootScan.ScanAsync(
            new ScriptedSource(byRoot), roots, new ScanOptions(), CancellationToken.None))
        {
            collected.Add(scanEvent);
        }

        return collected;
    }

    [TestMethod]
    public async Task MultiRootScan_AFailureBelowARoot_IsReportedUnderThatRootsLabel()
    {
        var events = await StitchAsync(new Dictionary<string, ScanEvent[]>(StringComparer.Ordinal)
        {
            ["/src/docs"] =
            [
                new ScanEvent.EnterDirectory(Directory(string.Empty, "docs")),
                new ScanEvent.Failure(new ScanFailure(
                    "quarterly/report.pdf", CaptureFailureReason.Permission, "denied")),
                new ScanEvent.LeaveDirectory(Directory(string.Empty, "docs")),
            ],
            ["/src/photos"] =
            [
                new ScanEvent.EnterDirectory(Directory(string.Empty, "photos")),
                new ScanEvent.Failure(new ScanFailure(
                    "quarterly/report.pdf", CaptureFailureReason.Permission, "denied")),
                new ScanEvent.LeaveDirectory(Directory(string.Empty, "photos")),
            ],
        });

        var failures = events.OfType<ScanEvent.Failure>().Select(failure => failure.Detail.RelativePath).ToArray();

        // The same relative path under two roots must not collapse into one
        // report — which is the whole reason the arm exists.
        Assert.HasCount(2, failures);
        Assert.Contains("docs/quarterly/report.pdf", failures);
        Assert.Contains("photos/quarterly/report.pdf", failures);
    }

    [TestMethod]
    public async Task MultiRootScan_AFailureAtARootItself_IsReportedAsThatLabelAlone()
    {
        var events = await StitchAsync(new Dictionary<string, ScanEvent[]>(StringComparer.Ordinal)
        {
            ["/src/docs"] =
            [
                new ScanEvent.Failure(new ScanFailure(
                    string.Empty, CaptureFailureReason.Permission, "the root itself could not be read")),
            ],
            ["/src/photos"] =
            [
                new ScanEvent.EnterDirectory(Directory(string.Empty, "photos")),
                new ScanEvent.LeaveDirectory(Directory(string.Empty, "photos")),
            ],
        });

        var failure = events.OfType<ScanEvent.Failure>().Single();

        // Not "docs/" with a trailing separator, and not the empty string the
        // single-root shape would have carried: the root's own label.
        Assert.AreEqual("docs", failure.Detail.RelativePath);
    }

    [TestMethod]
    public async Task MultiRootScan_AFailure_KeepsItsReasonAndDetail()
    {
        // Only the path is re-anchored. A relabelling that also rewrote why
        // would be turning a diagnosis into a guess.
        var events = await StitchAsync(new Dictionary<string, ScanEvent[]>(StringComparer.Ordinal)
        {
            ["/src/docs"] =
            [
                new ScanEvent.Failure(new ScanFailure(
                    "locked.db", CaptureFailureReason.IoError, "held by another process")),
            ],
            ["/src/photos"] = [],
        });

        var failure = events.OfType<ScanEvent.Failure>().Single();

        Assert.AreEqual(CaptureFailureReason.IoError, failure.Detail.Reason);
        Assert.AreEqual("held by another process", failure.Detail.Detail);
    }
}
