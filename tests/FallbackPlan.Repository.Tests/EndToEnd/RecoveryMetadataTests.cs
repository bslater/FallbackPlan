using System.Runtime.Versioning;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The recovery tool gives what it restores the metadata captured with it,
/// by the rule the executor's restore is held to (FR-RST-004; ADR-0150): a
/// file's once its content has verified and landed, and each folder's once
/// nothing more will land in it, deepest folder first.
/// </summary>
/// <remarks>
/// <para>
/// The tool writes through the same writer as the executor, for this
/// platform, as the account it runs as, and with no claim to have captured
/// what it restores. It keeps no receipt, so its report counts, per
/// attribute and in the receipt's words, how many of the files and folders it
/// restored did not get that attribute back.
/// </para>
/// <para>
/// Only a folder the tool made is given anything (ADR-0086). One already in
/// the output keeps its own, and so does the output folder itself. Nor is a
/// folder given its metadata through a link that has taken its place.
/// </para>
/// </remarks>
[TestClass]
public sealed class RecoveryMetadataTests : IDisposable
{
    /// <summary>When a file was modified: 2021-03-04.</summary>
    private const ulong FileModified = 1_614_834_367_890;

    /// <summary>When a file was last read: 2022-04-15.</summary>
    private const ulong FileAccessed = 1_650_000_000_000;

    /// <summary>When a folder was modified: 2020-09-13, long before anything the tool writes.</summary>
    private const ulong FolderModified = 1_600_000_000_000;

    /// <summary>When the folder inside it was modified: 2021-01-07.</summary>
    private const ulong InnerModified = 1_610_000_000_000;

    /// <summary>A time a folder outside the restore carries: 2001-09-09.</summary>
    private const ulong Before = 1_000_000_000_000;

    /// <summary><c>rw-------</c>: a file its owner alone may read.</summary>
    private const int OwnerReadWrite = 0x180;

    /// <summary><c>rwxrwxrwx</c>: a folder anyone may write to, which must never be what a link's target becomes.</summary>
    private const int Everyone = 0x1FF;

    /// <summary><c>rwx------</c>: a folder its owner has closed to everyone else.</summary>
    private const int OwnerOnly = 0x1C0;

    /// <summary>An account no machine the suite runs on has.</summary>
    private const string NoSuchUser = "fallbackplan-no-such-user";

    /// <summary>A group no machine the suite runs on has.</summary>
    private const string NoSuchGroup = "fallbackplan-no-such-group";

    private const string PassphraseText = "recovery-metadata-passphrase!!";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-recovery-metadata", Guid.NewGuid().ToString("n"));

    public RecoveryMetadataTests() => Directory.CreateDirectory(_root);

    [TestMethod]
    public async Task RecoveryRestore_AFile_GetsItsCapturedTimesBack_AndItsPermissionsWhereTheTargetKeepsThem()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("notes.txt", Deterministic(700, 1)).Metadata = new EntryMetadata
        {
            ModifiedAt = FileModified, AccessedAt = FileAccessed, PosixMode = OwnerReadWrite,
        };

        var (report, output) = await RestoreAsync(source);

        var notes = Path.Combine(output, "notes.txt");
        Assert.AreEqual(Utc(FileModified), File.GetLastWriteTimeUtc(notes));
        Assert.AreEqual(Utc(FileAccessed), File.GetLastAccessTimeUtc(notes));
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual((UnixFileMode)OwnerReadWrite, File.GetUnixFileMode(notes));
            Assert.IsEmpty(report.NotApplied);
        }

        Assert.AreEqual(1, report.Restored);
    }

    [TestMethod]
    public async Task RecoveryRestore_AFolder_GetsItsOwnTimesBack_OnceEverythingInItHasLanded()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 2));
        source.AddFile("docs/inner/b.bin", Deterministic(700, 3));
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = FolderModified, AccessedAt = FileAccessed };
        source.Folders["docs/inner"] = new EntryMetadata { ModifiedAt = InnerModified };

        var (report, output) = await RestoreAsync(source);

        // Read before anything lists the folders, which would move their
        // access times.
        var docs = Path.Combine(output, "docs");
        Assert.AreEqual(Utc(FolderModified), Directory.GetLastWriteTimeUtc(docs));
        Assert.AreEqual(Utc(FileAccessed), Directory.GetLastAccessTimeUtc(docs));
        Assert.AreEqual(Utc(InnerModified), Directory.GetLastWriteTimeUtc(Path.Combine(docs, "inner")));
        Assert.AreEqual(2, report.Restored);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX folder's permissions are its mode bits")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task RecoveryRestore_AFoldersPermissions_LandAfterItsContents_EvenWhereTheyForbidWriting()
    {
        // r-x for everyone: nothing could be written into the folder once
        // these were on it, so they can only go on last.
        const int ReadAndSearchOnly = 0x16D;
        var source = new FakeFileSystemSource();
        source.AddFile("locked/a.bin", Deterministic(700, 4));
        source.AddFile("locked/b.bin", Deterministic(700, 5));
        source.Folders["locked"] = new EntryMetadata { ModifiedAt = FolderModified, PosixMode = ReadAndSearchOnly };

        var (report, output) = await RestoreAsync(source);

        var locked = Path.Combine(output, "locked");
        try
        {
            Assert.AreEqual(0, report.Failed);
            Assert.AreEqual((UnixFileMode)ReadAndSearchOnly, File.GetUnixFileMode(locked));
            Assert.HasCount(2, Directory.GetFiles(locked));
            Assert.IsEmpty(report.NotApplied);
        }
        finally
        {
            // So the test can remove what it made.
            File.SetUnixFileMode(locked, (UnixFileMode)0x1ED);
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX folder's permissions are its mode bits")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task RecoveryRestore_AFolderCapturedWithoutSearchPermission_StillLetsTheFolderInItGetItsOwn()
    {
        // rw------- : no one may reach what is inside, so a folder inside it
        // is given its own first. An account with privilege reaches it either
        // way; any other would find the folder inside shut away.
        const int NoSearch = 0x180;
        var source = new FakeFileSystemSource();
        source.AddFile("sealed/inner/a.bin", Deterministic(700, 17));
        source.Folders["sealed"] = new EntryMetadata { ModifiedAt = FolderModified, PosixMode = NoSearch };
        source.Folders["sealed/inner"] = new EntryMetadata { ModifiedAt = InnerModified };

        var (report, output) = await RestoreAsync(source);

        var @sealed = Path.Combine(output, "sealed");
        try
        {
            Assert.AreEqual((UnixFileMode)NoSearch, File.GetUnixFileMode(@sealed));
        }
        finally
        {
            // So the folder inside can be read, and the test can remove what it made.
            File.SetUnixFileMode(@sealed, (UnixFileMode)0x1ED);
        }

        Assert.AreEqual(Utc(InnerModified), Directory.GetLastWriteTimeUtc(Path.Combine(@sealed, "inner")));
        Assert.IsEmpty(report.NotApplied);
    }

    [TestMethod]
    public async Task RecoveryRestore_AFolderAlreadyInTheOutput_KeepsItsOwnMetadata_AndTheReportCountsIt()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 6));
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = FolderModified, PosixMode = Everyone };

        var output = Path.Combine(_root, "found-out");
        var live = Directory.CreateDirectory(Path.Combine(output, "docs")).FullName;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(live, (UnixFileMode)OwnerOnly);
        }

        var report = await RestoreIntoAsync(source, output);

        // A live folder's permissions are its owner's current choice, not the
        // snapshot's.
        Assert.AreNotEqual(Utc(FolderModified), Directory.GetLastWriteTimeUtc(live));
        Assert.AreEqual(1, Items(report, "modified_at"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual((UnixFileMode)OwnerOnly, File.GetUnixFileMode(live));
            CollectionAssert.AreEqual(
                new[] { new RecoveryNotApplied("modified_at", 1), new RecoveryNotApplied("posix_mode", 1) },
                report.NotApplied.ToArray());
        }

        Assert.AreEqual(0, report.Failed);
    }

    [TestMethod]
    public async Task RecoveryRestore_TheOutputFolder_KeepsItsOwnMetadata_AndIsNotCounted()
    {
        // The snapshot's root is restored as its contents, into the folder
        // the person chose. Its own metadata would change who may reach that
        // folder (ADR-0086 §3).
        var source = new FakeFileSystemSource();
        source.AddFile("a.bin", Deterministic(700, 7));
        source.Folders[string.Empty] = new EntryMetadata { ModifiedAt = FolderModified, PosixMode = Everyone };

        var output = Directory.CreateDirectory(Path.Combine(_root, "chosen")).FullName;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(output, (UnixFileMode)OwnerOnly);
        }

        var report = await RestoreIntoAsync(source, output);

        Assert.AreNotEqual(Utc(FolderModified), Directory.GetLastWriteTimeUtc(output));
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual((UnixFileMode)OwnerOnly, File.GetUnixFileMode(output));
            Assert.IsEmpty(report.NotApplied);
        }
    }

    [TestMethod]
    public async Task RecoveryRestore_AnOwnerOrGroupNoAccountHereHas_IsCountedNotApplied_InTheReceiptsWords()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 8)).Metadata = new EntryMetadata
        {
            ModifiedAt = FileModified, OwnerName = NoSuchUser,
        };
        source.AddFile("docs/b.bin", Deterministic(700, 9)).Metadata = new EntryMetadata
        {
            ModifiedAt = FileModified, OwnerName = NoSuchUser,
        };
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = FolderModified, GroupName = NoSuchGroup };

        var (report, output) = await RestoreAsync(source);

        Assert.AreEqual(2, Items(report, "owner"));
        Assert.AreEqual(1, Items(report, "group"));
        Assert.AreEqual(0, Items(report, "modified_at"));
        Assert.AreEqual(Utc(FileModified), File.GetLastWriteTimeUtc(Path.Combine(output, "docs", "a.bin")));
        Assert.AreEqual(Utc(FolderModified), Directory.GetLastWriteTimeUtc(Path.Combine(output, "docs")));
        Assert.AreEqual(
            "metadata not applied: owner to 2 item(s), group to 1 item(s)",
            report.DescribeNotApplied());
    }

    [TestMethod]
    public async Task RecoveryRestore_EverythingApplied_DescribesNothingNotApplied()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("a.bin", Deterministic(700, 10)).Metadata = new EntryMetadata { ModifiedAt = FileModified };

        var (report, _) = await RestoreAsync(source);

        Assert.IsEmpty(report.NotApplied);
        Assert.IsNull(report.DescribeNotApplied());
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task RecoveryRestore_AFolderSwappedForALinkWhileTheToolRuns_IsNotGivenItsMetadataThroughTheLink()
    {
        // A folder's metadata goes on at the end of the run, so whoever can
        // write beside the folder has the rest of the run to put a link in
        // its place. Written through the link, the folder's permissions
        // would land on whatever it points at.
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 11));
        source.AddFile("zzz.bin", Deterministic(700, 12));
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = FolderModified, PosixMode = Everyone };

        var outside = Outside("swapped-outside");
        var output = Path.Combine(_root, "swapped-out");
        var docs = Path.Combine(output, "docs");

        // Once the folder's own file has landed, the next file's content is
        // read: that is when the folder is moved aside and a link put where
        // it was.
        var report = await RestoreIntoAsync(source, output, store => new InterruptingObjectStore(
            store,
            when: () => File.Exists(Path.Combine(docs, "a.bin")),
            act: () =>
            {
                Directory.Move(docs, docs + "-moved");
                Directory.CreateSymbolicLink(docs, outside);
            }));

        Assert.AreEqual((UnixFileMode)OwnerOnly, File.GetUnixFileMode(outside), "a folder's permissions went through a link");
        Assert.AreEqual(Utc(Before), Directory.GetLastWriteTimeUtc(outside), "a folder's times went through a link");
        Assert.AreEqual(1, Items(report, "modified_at"));
        Assert.AreEqual(1, Items(report, "posix_mode"));
        Assert.AreEqual(2, report.Restored);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task RecoveryRestore_AFolderWhoseParentIsSwappedForALink_IsNotGivenItsMetadataThroughTheLink()
    {
        // The folder itself is still a folder, but the way to it now leaves
        // the output through a link, so its writes would land on whatever
        // stands under the same name at the link's target.
        var source = new FakeFileSystemSource();
        source.AddFile("docs/inner/a.bin", Deterministic(700, 15));
        source.AddFile("zzz.bin", Deterministic(700, 16));
        source.Folders["docs/inner"] = new EntryMetadata { ModifiedAt = FolderModified, PosixMode = Everyone };

        var outside = Path.Combine(_root, "parent-outside");
        var inside = Directory.CreateDirectory(Path.Combine(outside, "inner")).FullName;
        File.SetUnixFileMode(inside, (UnixFileMode)OwnerOnly);
        Directory.SetLastWriteTimeUtc(inside, Utc(Before));
        var output = Path.Combine(_root, "parent-out");
        var docs = Path.Combine(output, "docs");

        var report = await RestoreIntoAsync(source, output, store => new InterruptingObjectStore(
            store,
            when: () => File.Exists(Path.Combine(docs, "inner", "a.bin")),
            act: () =>
            {
                Directory.Move(docs, docs + "-moved");
                Directory.CreateSymbolicLink(docs, outside);
            }));

        Assert.AreEqual((UnixFileMode)OwnerOnly, File.GetUnixFileMode(inside), "a folder's permissions went through a link");
        Assert.AreEqual(Utc(Before), Directory.GetLastWriteTimeUtc(inside), "a folder's times went through a link");
        Assert.AreEqual(2, Items(report, "modified_at"));
        Assert.AreEqual(2, Items(report, "posix_mode"));
    }

    [TestMethod]
    public async Task RecoveryRestore_StoppedPartWay_StillGivesTheFoldersItMadeTheirOwnMetadata()
    {
        // A folder the tool made holds what restored under it, whether or not
        // the run went on (ADR-0086 §2).
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 13));
        source.AddFile("zzz.bin", Deterministic(700, 14));
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = FolderModified };

        var output = Path.Combine(_root, "stopped-out");
        var docs = Path.Combine(output, "docs");
        using var stop = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() => RestoreIntoAsync(
            source,
            output,
            store => new InterruptingObjectStore(store, when: () => File.Exists(Path.Combine(docs, "a.bin")), act: stop.Cancel),
            stop.Token));

        Assert.IsFalse(File.Exists(Path.Combine(output, "zzz.bin")), "the run was never stopped, so this proves nothing");
        Assert.AreEqual(Utc(FolderModified), Directory.GetLastWriteTimeUtc(docs));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static int Items(RecoveryRestoreReport report, string attribute) =>
        report.NotApplied.SingleOrDefault(entry => entry.Attribute == attribute)?.Items ?? 0;

    private static DateTime Utc(ulong milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds).UtcDateTime;

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }

    /// <summary>A folder outside every restore's output, closed to all but its owner and last modified long ago.</summary>
    [UnsupportedOSPlatform("windows")]
    private string Outside(string name)
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        File.SetUnixFileMode(outside, (UnixFileMode)OwnerOnly);
        Directory.SetLastWriteTimeUtc(outside, Utc(Before));
        return outside;
    }

    private async Task<(RecoveryRestoreReport Report, string Output)> RestoreAsync(FakeFileSystemSource source)
    {
        var output = Path.Combine(_root, "restored");
        return (await RestoreIntoAsync(source, output), output);
    }

    /// <summary>
    /// Backs <paramref name="source"/> up into a repository made from the
    /// passphrase, then restores its one snapshot with the recovery session
    /// alone, through <paramref name="wrap"/>'s store when one is given.
    /// </summary>
    private async Task<RecoveryRestoreReport> RestoreIntoAsync(
        FakeFileSystemSource source,
        string output,
        Func<IObjectStore, InterruptingObjectStore>? wrap = null,
        CancellationToken cancellationToken = default)
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
                        SnapshotId = Enumerable.Repeat((byte)0x66, 16).ToArray(),
                        NowUnixMilliseconds = 1_722_600_000_001,
                        DeclaredMaxDurationMs = 3_600_000,
                        ExpiryGeneration = 5,
                        ClientVersion = "recovery-metadata/1.0",
                    },
                    CancellationToken.None);
        }

        var interrupting = wrap?.Invoke(store);
        using var open = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(open, (IObjectStore?)interrupting ?? store, CancellationToken.None);
        await session.LoadBlobsAsync(CancellationToken.None);
        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));

        interrupting?.Arm();
        var report = await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, cancellationToken);
        Assert.IsTrue(interrupting is null || interrupting.Acted, "the run was never interrupted, so this proves nothing");
        return report;
    }

    /// <summary>
    /// A store that, once armed, runs an action at the first read of a data
    /// blob made while a condition holds, and then answers that read as a
    /// stopped run would if the action stopped it.
    /// </summary>
    private sealed class InterruptingObjectStore(IObjectStore inner, Func<bool> when, Action act) : IObjectStore
    {
        private int _armed;

        /// <summary>Whether the action ran.</summary>
        public bool Acted { get; private set; }

        /// <inheritdoc />
        public StoreCapabilities Capabilities => inner.Capabilities;

        /// <summary>Runs the action at the next data blob read the condition allows, and only then.</summary>
        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        /// <inheritdoc />
        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        /// <inheritdoc />
        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
        {
            if (key.Value.StartsWith("blobs/data/", StringComparison.Ordinal)
                && Volatile.Read(ref _armed) == 1
                && when()
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                act();
                Acted = true;
                cancellationToken.ThrowIfCancellationRequested();
            }

            return inner.OpenReadAsync(key, range, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<PutResult> PutAsync(
            ObjectKey key,
            Func<CancellationToken, ValueTask<Stream>> openContent,
            PutConditions conditions,
            CancellationToken cancellationToken) =>
            inner.PutAsync(key, openContent, conditions, cancellationToken);

        /// <inheritdoc />
        public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        /// <inheritdoc />
        public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }
}
