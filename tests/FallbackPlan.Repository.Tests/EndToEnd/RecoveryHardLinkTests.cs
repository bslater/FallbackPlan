using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The recovery tool gives a hard-link group back as one file under each of
/// its names, by the rule the executor holds to (FR-RST-004; ADR-0156,
/// ADR-0102): a later name is a link to the first name landed, where it
/// agrees with that name in content, length and every attribute but the
/// access time, and the output makes one.
/// </summary>
/// <remarks>
/// A name that does not agree, or that the output will not link, is restored
/// as a file of its own, with a note that says why. A linked name is the
/// first name's file, so it is counted as that name was in what did not
/// land, and its flags are noted as that name's are (ADR-0180 Amendment 2).
/// </remarks>
[TestClass]
public sealed class RecoveryHardLinkTests : IDisposable
{
    /// <summary>The file identity the names of one group share.</summary>
    private const ulong Shared = 900;

    /// <summary>An account no machine the suite runs on has.</summary>
    private const string NoSuchUser = "fallbackplan-no-such-user";

    private const string PassphraseText = "recovery-hardlinks-passphrase!!";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-recovery-hardlinks", Guid.NewGuid().ToString("n"));

    public RecoveryHardLinkTests() => Directory.CreateDirectory(_root);

    [TestMethod]
    public async Task RecoveryRestore_AHardLinkGroup_ComesBackAsOneFile_UnderEachOfItsNames()
    {
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 1);
        source.AddFile("docs/a.bin", content, linkCount: 2, fileId: Shared);
        source.AddFile("docs/b.bin", content, linkCount: 2, fileId: Shared);
        var output = Path.Combine(_root, "restored");

        var report = await RestoreIntoAsync(source, output);

        Assert.AreEqual((2, 0), (report.Restored, report.Failed));
        Assert.IsEmpty(report.Notes);
        AssertOneFile(Path.Combine(output, "docs", "a.bin"), Path.Combine(output, "docs", "b.bin"));
    }

    [TestMethod]
    public async Task RecoveryRestore_ALinkedName_NotesTheFlagsItsFileDidNotGet_AsTheFirstNameDoes_WhereThisPlatformSetsNone()
    {
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 7);
        foreach (var name in new[] { "docs/a.bin", "docs/b.bin" })
        {
            source.AddFile(name, content, linkCount: 2, fileId: Shared).Diagnostics = ["file-flags: no-dump"];
        }

        var output = Path.Combine(_root, "restored");

        var report = await RestoreIntoAsync(source, output);

        AssertOneFile(Path.Combine(output, "docs", "a.bin"), Path.Combine(output, "docs", "b.bin"));
        CollectionAssert.AreEqual(
            FileFlags.Settable(EntryKind.File).HasFlag(FileFlagBits.NoDump)
                ? Array.Empty<string>()
                : new[] { "docs/a.bin: file flag(s) not applied: no-dump", "docs/b.bin: file flag(s) not applied: no-dump" },
            report.Notes.Select(note => note.ToString()).ToArray());
    }

    [TestMethod]
    public async Task RecoveryRestore_ANameWhoseContentDiffersFromItsGroup_IsRestoredAsAFileOfItsOwn_AndSaysWhy()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(5_000, 3), linkCount: 2, fileId: Shared);
        source.AddFile("docs/b.bin", Deterministic(5_000, 4), linkCount: 2, fileId: Shared);
        var output = Path.Combine(_root, "restored");

        var report = await RestoreIntoAsync(source, output);

        Assert.AreEqual((2, 0), (report.Restored, report.Failed));
        CollectionAssert.AreEqual(Deterministic(5_000, 4), File.ReadAllBytes(Path.Combine(output, "docs", "b.bin")));
        AssertTwoFiles(Path.Combine(output, "docs", "a.bin"), Path.Combine(output, "docs", "b.bin"));
        var note = Assert.ContainsSingle(report.Notes);
        Assert.AreEqual((RecoveryNoteKind.NotLinked, "docs/b.bin"), (note.Kind, note.Path));
        Assert.Contains("content or attributes differ", note.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RecoveryRestore_ANameWhoseMetadataDiffersFromItsGroup_IsRestoredAsAFileOfItsOwn()
    {
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 5);
        source.AddFile("docs/a.bin", content, linkCount: 2, fileId: Shared).Metadata =
            new EntryMetadata { ModifiedAt = 1_722_000_000_000 };
        source.AddFile("docs/b.bin", content, linkCount: 2, fileId: Shared).Metadata =
            new EntryMetadata { ModifiedAt = 1_722_000_009_000 };
        var output = Path.Combine(_root, "restored");

        var report = await RestoreIntoAsync(source, output);

        AssertTwoFiles(Path.Combine(output, "docs", "a.bin"), Path.Combine(output, "docs", "b.bin"));
        Assert.AreEqual((RecoveryNoteKind.NotLinked, "docs/b.bin"), (Assert.ContainsSingle(report.Notes).Kind, report.Notes[0].Path));
    }

    [TestMethod]
    public async Task RecoveryRestore_ANameTheOutputWillNotLink_IsRestoredAsAFileOfItsOwn_AndSaysWhy()
    {
        // A volume that keeps no links: the content is right, and a copy is
        // less than was captured, not wrong.
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 6);
        source.AddFile("docs/a.bin", content, linkCount: 2, fileId: Shared);
        source.AddFile("docs/b.bin", content, linkCount: 2, fileId: Shared);
        var output = Path.Combine(_root, "restored");

        var report = await RestoreIntoAsync(source, output, makeLink: (_, _) => false);

        Assert.AreEqual((2, 0), (report.Restored, report.Failed));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(output, "docs", "b.bin")));
        AssertTwoFiles(Path.Combine(output, "docs", "a.bin"), Path.Combine(output, "docs", "b.bin"));
        var note = Assert.ContainsSingle(report.Notes);
        Assert.AreEqual((RecoveryNoteKind.NotLinked, "docs/b.bin"), (note.Kind, note.Path));
        Assert.Contains("would not make a hard link", note.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RecoveryRestore_ALinkedNameWhereAFileStands_IsReplacedByTheLink()
    {
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 7);
        source.AddFile("docs/a.bin", content, linkCount: 2, fileId: Shared);
        source.AddFile("docs/b.bin", content, linkCount: 2, fileId: Shared);
        var output = Path.Combine(_root, "restored");
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(output, "docs")).FullName, "b.bin"), [9]);

        var report = await RestoreIntoAsync(source, output);

        Assert.AreEqual((2, 0), (report.Restored, report.Failed));
        AssertOneFile(Path.Combine(output, "docs", "a.bin"), Path.Combine(output, "docs", "b.bin"));
    }

    [TestMethod]
    public async Task RecoveryRestore_ALinkedName_IsCountedAsItsFirstNameIs_InWhatDidNotLand()
    {
        // The name is the first name's file, so what that file did not get,
        // this name did not get either.
        var source = new FakeFileSystemSource();
        var content = Deterministic(5_000, 8);
        var metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000, OwnerName = NoSuchUser };
        source.AddFile("docs/a.bin", content, linkCount: 2, fileId: Shared).Metadata = metadata;
        source.AddFile("docs/b.bin", content, linkCount: 2, fileId: Shared).Metadata = metadata;
        var output = Path.Combine(_root, "restored");

        var report = await RestoreIntoAsync(source, output);

        AssertOneFile(Path.Combine(output, "docs", "a.bin"), Path.Combine(output, "docs", "b.bin"));
        Assert.AreEqual(2, report.NotApplied.Single(entry => entry.Attribute == "owner").Items);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Whether two names are one file: what is written through one is read through the other.</summary>
    private static void AssertOneFile(string first, string second)
    {
        var before = File.ReadAllBytes(first);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(second));
        File.AppendAllText(second, "+");
        Assert.HasCount(before.Length + 1, File.ReadAllBytes(first), $"{second} is a copy of {first}, not a second name for it");
    }

    /// <summary>Whether two names are two files: what is written through one is not read through the other.</summary>
    private static void AssertTwoFiles(string first, string second)
    {
        var before = File.ReadAllBytes(first);
        File.AppendAllText(second, "+");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(first), $"{second} and {first} are one file");
    }

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }

    /// <summary>
    /// Backs <paramref name="source"/> up into a repository made from the
    /// passphrase, then restores its one snapshot into
    /// <paramref name="output"/> with the recovery session alone.
    /// </summary>
    private async Task<RecoveryRestoreReport> RestoreIntoAsync(
        FakeFileSystemSource source, string output, Func<string, string, bool>? makeLink = null)
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
                        SnapshotId = Enumerable.Repeat((byte)0x69, 16).ToArray(),
                        NowUnixMilliseconds = 1_722_600_000_001,
                        DeclaredMaxDurationMs = 3_600_000,
                        ExpiryGeneration = 5,
                        ClientVersion = "recovery-hardlinks/1.0",
                    },
                    CancellationToken.None);
        }

        using var open = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(open, store, CancellationToken.None);
        await session.LoadBlobsAsync(CancellationToken.None);
        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));
        return await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, makeLink, CancellationToken.None);
    }
}
