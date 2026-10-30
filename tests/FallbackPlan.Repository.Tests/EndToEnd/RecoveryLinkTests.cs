using System.Runtime.Versioning;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The recovery tool makes a symlink a link, pointing where it pointed, and
/// gives it its own metadata back onto the link and never through it, as
/// the executor does (FR-RST-004; ADR-0155). A link standing in the output
/// still carries nothing out of it, since every later path is placed by the
/// executor's rule (ADR-0152).
/// </summary>
/// <remarks>
/// Whatever the output holds at a file's or a link's name and will not give
/// up, a folder or a write the platform refuses, fails that entry alone and
/// is noted, and the rest of the recovery continues (FR-RST-005).
/// </remarks>
[TestClass]
public sealed class RecoveryLinkTests : IDisposable
{
    /// <summary>When a link was modified: 2021-03-04.</summary>
    private const ulong LinkModified = 1_614_834_367_890;

    /// <summary>When a link was last read: 2022-04-15.</summary>
    private const ulong LinkAccessed = 1_650_000_000_000;

    /// <summary>A time a file outside the restore carries: 2001-09-09.</summary>
    private const ulong Before = 1_000_000_000_000;

    /// <summary><c>rw-------</c>: a file its owner alone may read.</summary>
    private const int OwnerReadWrite = 0x180;

    /// <summary><c>rwxrwxrwx</c>: what a link may carry, and what its target must never become.</summary>
    private const int Everyone = 0x1FF;

    private const string PassphraseText = "recovery-links-passphrase!!";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-recovery-links", Guid.NewGuid().ToString("n"));

    public RecoveryLinkTests() => Directory.CreateDirectory(_root);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task RecoveryRestore_ASymlink_IsMadeALink_PointingWhereItPointed()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("data/target.bin", [1, 2, 3]);
        source.AddNode(Link("link", "data/target.bin"));
        var output = Path.Combine(_root, "restored");

        var report = await RestoreIntoAsync(source, output);

        Assert.AreEqual("data/target.bin", new FileInfo(Path.Combine(output, "link")).LinkTarget);
        Assert.AreEqual((2, 0, 0), (report.Restored, report.Failed, report.Skipped));
        Assert.IsEmpty(report.Notes);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task RecoveryRestore_ALinkLeadingOutOfTheOutput_GetsItsOwnTimesBack_AndNothingGoesThroughIt()
    {
        // A link's target is the repository's text, and may lead anywhere.
        // The link is made, and its own metadata goes onto it, never onto
        // what it points at.
        var outside = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName, "kept.txt");
        File.WriteAllBytes(outside, [9, 9, 9]);
        File.SetUnixFileMode(outside, (UnixFileMode)OwnerReadWrite);
        File.SetLastWriteTimeUtc(outside, Utc(Before));
        var source = new FakeFileSystemSource();
        source.AddNode(Link("escape", outside) with
        {
            Metadata = new EntryMetadata { ModifiedAt = LinkModified, AccessedAt = LinkAccessed, PosixMode = Everyone },
        });
        var output = Path.Combine(_root, "restored");

        var report = await RestoreIntoAsync(source, output);

        var link = LinkStatus.Of(Path.Combine(output, "escape"));
        Assert.AreEqual(Utc(LinkModified), link.ModifiedUtc);
        Assert.AreEqual(Utc(LinkAccessed), link.AccessedUtc);
        Assert.AreEqual(Utc(Before), File.GetLastWriteTimeUtc(outside), "a link's times went through it");
        Assert.AreEqual((UnixFileMode)OwnerReadWrite, File.GetUnixFileMode(outside), "a link's permissions went through it");
        CollectionAssert.AreEqual(new byte[] { 9, 9, 9 }, File.ReadAllBytes(outside));
        Assert.AreEqual(1, report.Restored);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task RecoveryRestore_AFileStandingAtALinksName_IsReplacedByTheLink()
    {
        var source = new FakeFileSystemSource();
        source.AddNode(Link("link", "elsewhere"));
        var output = Directory.CreateDirectory(Path.Combine(_root, "restored")).FullName;
        File.WriteAllBytes(Path.Combine(output, "link"), [7]);

        var report = await RestoreIntoAsync(source, output);

        Assert.AreEqual("elsewhere", new FileInfo(Path.Combine(output, "link")).LinkTarget);
        Assert.AreEqual((1, 0), (report.Restored, report.Failed));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task RecoveryRestore_AFolderStandingAtALinksName_FailsThatEntryAlone_AndIsLeftAsItWas()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("good.txt", [4, 5, 6]);
        source.AddNode(Link("link", "good.txt"));
        var output = Directory.CreateDirectory(Path.Combine(_root, "restored")).FullName;
        var kept = Path.Combine(Directory.CreateDirectory(Path.Combine(output, "link")).FullName, "kept.txt");
        File.WriteAllBytes(kept, [8]);

        var report = await RestoreIntoAsync(source, output);

        Assert.AreEqual((1, 1), (report.Restored, report.Failed));
        Assert.Contains(note => note.Kind == RecoveryNoteKind.WriteFailed && note.Path == "link", report.Notes);
        CollectionAssert.AreEqual(new byte[] { 8 }, File.ReadAllBytes(kept));
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Path.Combine(output, "good.txt")));
    }

    [TestMethod]
    public async Task RecoveryRestore_AFolderStandingAtAFilesName_FailsThatEntryAlone_AndTheRestRestores()
    {
        // A folder at a file's name will not be replaced by a rename. The
        // tool once let that end the whole recovery; it is one entry the
        // output would not take.
        var source = new FakeFileSystemSource();
        source.AddFile("a.txt", [1]);
        source.AddFile("b.txt", [2]);
        var output = Directory.CreateDirectory(Path.Combine(_root, "restored")).FullName;
        Directory.CreateDirectory(Path.Combine(output, "a.txt"));

        var report = await RestoreIntoAsync(source, output);

        Assert.AreEqual((1, 1), (report.Restored, report.Failed));
        var note = Assert.ContainsSingle(report.Notes);
        Assert.AreEqual((RecoveryNoteKind.WriteFailed, "a.txt"), (note.Kind, note.Path));
        Assert.StartsWith("FAILED a.txt: the output would not take it", note.ToString(), StringComparison.Ordinal);
        CollectionAssert.AreEqual(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(output, "b.txt")));
        Assert.IsFalse(File.Exists(RestorePaths.SpoolBeside(Path.Combine(output, "a.txt"), "recover")), "the spool outlived the entry");
    }

    [TestMethod]
    public async Task RecoveryRestore_AFileStandingAtAFoldersName_FailsThatFolderAlone_WithWhatItWouldHaveHeld()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.txt", [1]);
        source.AddFile("good.txt", [2]);
        var output = Directory.CreateDirectory(Path.Combine(_root, "restored")).FullName;
        File.WriteAllBytes(Path.Combine(output, "docs"), [7]);

        var report = await RestoreIntoAsync(source, output);

        Assert.AreEqual((1, 1), (report.Restored, report.Failed));
        var note = Assert.ContainsSingle(report.Notes);
        Assert.AreEqual((RecoveryNoteKind.WriteFailed, "docs"), (note.Kind, note.Path));
        CollectionAssert.AreEqual(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(output, "docs")));
        CollectionAssert.AreEqual(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(output, "good.txt")));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "Windows makes a link only with a privilege a restore does not ask for")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task RecoveryRestore_ASymlink_WhereTheTargetMakesNoLinks_IsSkippedWithANote()
    {
        var source = new FakeFileSystemSource();
        source.AddNode(Link("link", "elsewhere"));
        var output = Path.Combine(_root, "restored");

        var report = await RestoreIntoAsync(source, output);

        Assert.AreEqual((0, 0, 1), (report.Restored, report.Failed, report.Skipped));
        var note = Assert.ContainsSingle(report.Notes);
        Assert.AreEqual((RecoveryNoteKind.SkippedSpecial, "link"), (note.Kind, note.Path));
        Assert.IsFalse(Path.Exists(Path.Combine(output, "link")));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static FakeFileSystemSource.Node Link(string path, string target) => new()
    {
        RelativePath = path,
        Kind = ScanEntryKind.Symlink,
        LinkTarget = System.Text.Encoding.UTF8.GetBytes(target),
    };

    private static DateTime Utc(ulong milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds).UtcDateTime;

    /// <summary>
    /// Backs <paramref name="source"/> up into a repository made from the
    /// passphrase, then restores its one snapshot into
    /// <paramref name="output"/> with the recovery session alone.
    /// </summary>
    private async Task<RecoveryRestoreReport> RestoreIntoAsync(FakeFileSystemSource source, string output)
    {
        var store = new LocalFileSystemObjectStore(Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("n")));
        using (var passphrase = Passphrase.Create(PassphraseText))
        {
            var (repository, authority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
                store, passphrase, RepositoryCreationSettings.Default,
                createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);
            using var _repository = repository;
            using var _authority = authority;

            var spool = Directory.CreateDirectory(Path.Combine(_root, "spool-" + Guid.NewGuid().ToString("n"))).FullName;
            await new PublicationOrchestrator(
                    CapturePolicy.Default with { SegmentSize = SegmentSize.Create(64 * 1024) },
                    repository.RepositoryId,
                    WriterId.FromBytes(Enumerable.Repeat((byte)0xA0, 16).ToArray()),
                    repository.CurrentDataGeneration,
                    repository.Keys,
                    repository.Credential,
                    store,
                    new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                    spool,
                    FormatVersions.SealedDataPlane)
                .PublishAsync(
                    new SnapshotJob
                    {
                        Source = source,
                        Roots = [new ScanRoot("/")],
                        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                        SnapshotId = Enumerable.Repeat((byte)0x68, 16).ToArray(),
                        NowUnixMilliseconds = 1_722_600_000_001,
                        DeclaredMaxDurationMs = 3_600_000,
                        ExpiryGeneration = 5,
                        ClientVersion = "recovery-links/1.0",
                    },
                    CancellationToken.None);
        }

        using var open = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(open, store, CancellationToken.None);
        await session.LoadBlobsAsync(CancellationToken.None);
        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));
        return await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, CancellationToken.None);
    }
}
