using System.Runtime.Versioning;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A restore gives each folder it makes its own captured metadata back, by
/// the rule it gives a file's (FR-RST-004; ADR-0086): its modification and
/// access times everywhere, its creation time where the target can set one,
/// and on a POSIX target its owner and group where the restoring account may
/// give them, then its permissions. The plan says beforehand what a folder
/// will not get back, counted apart from the files, and names a folder whose
/// own record the store does not hold (FR-RST-003). The receipt says it per
/// folder afterwards.
/// </summary>
/// <remarks>
/// <para>
/// A folder's metadata goes on last, deepest folder first, once nothing more
/// will land in it. Every item written into a folder moves its modification
/// time, and a folder whose captured permissions forbid writing would refuse
/// whatever was still to land in it.
/// </para>
/// <para>
/// Only a folder the restore made is given anything. One already at the
/// destination keeps its own: no existing-file policy reaches a folder, and a
/// live folder's permissions and owner are its owner's current choice, not
/// the snapshot's. Nor is a folder ever given its metadata through a link,
/// whether the link stood there before the run or was put there while it ran.
/// </para>
/// </remarks>
[TestClass]
public sealed class RestoreFolderMetadataTests : ArchiveTestHarness
{
    /// <summary>When a folder was modified: 2020-09-13, long before anything the restore writes.</summary>
    private const ulong FolderModified = 1_600_000_000_000;

    /// <summary>When a folder was last read: 2022-04-15.</summary>
    private const ulong FolderAccessed = 1_650_000_000_000;

    /// <summary>When the folder inside it was modified: 2021-01-07.</summary>
    private const ulong InnerModified = 1_610_000_000_000;

    /// <summary>When a folder was made: 2020-05-20.</summary>
    private const ulong FolderCreated = 1_590_000_000_000;

    /// <summary>A time a folder outside the restore carries: 2001-09-09.</summary>
    private const ulong Before = 1_000_000_000_000;

    /// <summary><c>rwxrwxrwx</c>: a folder anyone may write to, which must never be what a link's target becomes.</summary>
    private const int Everyone = 0x1FF;

    /// <summary><c>rwx------</c>: a folder its owner has closed to everyone else.</summary>
    private const int OwnerOnly = 0x1C0;

    /// <summary>A group no machine the suite runs on has.</summary>
    private const string NoSuchGroup = "fallbackplan-no-such-group";

    /// <summary>An id no account or group is given on any machine the suite runs on.</summary>
    private const uint Unclaimed = 54_321;

    /// <summary>An object no store in these tests holds.</summary>
    private static readonly ObjectId Unheld = ObjectId.FromBytes(SHA256.HashData("a folder record no store holds"u8));

    private static readonly RestoreTargetProfile Posix = new()
    {
        CaseSensitive = true,
        SupportsPosixMetadata = true,
        SupportsSymlinks = true,
    };

    [TestMethod]
    public async Task Receipt_AFolder_GetsItsCapturedTimesBack_OnceEverythingInItHasLanded()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 1));
        source.AddFile("docs/inner/b.bin", Deterministic(700, 2));
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = FolderModified, AccessedAt = FolderAccessed };
        source.Folders["docs/inner"] = new EntryMetadata { ModifiedAt = InnerModified };

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xF1, RestoreTargetProfile.ForLocalPlatform());

        // Read before anything lists the folders, which would move their
        // access times.
        var docs = Path.Combine(output, "docs");
        Assert.AreEqual(Utc(FolderModified), Directory.GetLastWriteTimeUtc(docs));
        Assert.AreEqual(Utc(FolderAccessed), Directory.GetLastAccessTimeUtc(docs));
        Assert.AreEqual(Utc(InnerModified), Directory.GetLastWriteTimeUtc(Path.Combine(docs, "inner")));

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.IsNull(receipt.Items.Single(item => item.Path == "docs").NotApplied);
        Assert.IsNull(receipt.Items.Single(item => item.Path == "docs/inner").NotApplied);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows | TestPlatforms.MacOs, "only Windows and macOS can set a creation time")]
    [PlatformTrait(TestPlatforms.Windows | TestPlatforms.MacOs)]
    public async Task Receipt_AFoldersCreationTime_IsWrittenBackWhereTheTargetCanSetOne()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 3));
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = FolderModified, CreatedAt = FolderCreated };

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xF2, RestoreTargetProfile.ForLocalPlatform());

        var docs = Path.Combine(output, "docs");
        Assert.AreEqual(Utc(FolderCreated), Directory.GetCreationTimeUtc(docs));
        Assert.AreEqual(Utc(FolderModified), Directory.GetLastWriteTimeUtc(docs));
        Assert.IsNull(receipt.Items.Single(item => item.Path == "docs").NotApplied);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX folder's permissions are its mode bits")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task Receipt_AFoldersPermissions_LandAfterItsContents_EvenWhereTheyForbidWriting()
    {
        // r-x for everyone: nothing could be written into the folder once
        // these were on it, so they can only go on last.
        const int ReadAndSearchOnly = 0x16D;
        var source = new FakeFileSystemSource();
        source.AddFile("locked/a.bin", Deterministic(700, 4));
        source.AddFile("locked/b.bin", Deterministic(700, 5));
        source.Folders["locked"] = new EntryMetadata { ModifiedAt = FolderModified, PosixMode = ReadAndSearchOnly };

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xF3, Posix);

        var locked = Path.Combine(output, "locked");
        try
        {
            Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
            Assert.AreEqual((UnixFileMode)ReadAndSearchOnly, File.GetUnixFileMode(locked));
            Assert.IsNull(receipt.Items.Single(item => item.Path == "locked").NotApplied);
            Assert.HasCount(2, Directory.GetFiles(locked));
        }
        finally
        {
            // So the harness can remove what it made.
            File.SetUnixFileMode(locked, (UnixFileMode)0x1ED);
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX folder's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Receipt_AFoldersOwnerAndGroup_AreGivenBackWhereTheAccountMayGiveThem()
    {
        // The account itself, and a group it may give: any at all with
        // privilege, else one it is in, other than the one a folder is made
        // with where it has one.
        var group = Environment.IsPrivilegedProcess
            ? Unclaimed
            : PosixAccount.Groups.FirstOrDefault(candidate => candidate != PosixAccount.GroupId, PosixAccount.GroupId);
        var source = new FakeFileSystemSource();
        source.AddFile("shared/a.bin", Deterministic(700, 6));
        source.Folders["shared"] = new EntryMetadata { ModifiedAt = FolderModified, OwnerName = "ana", GroupName = "staff" };

        var (receipt, output) = await PublishAndRestoreAsync(
            source, 0xF4, Resolving(user => user == "ana" ? PosixAccount.UserId : null, name => name == "staff" ? group : null));

        Assert.IsNull(receipt.Items.Single(item => item.Path == "shared").NotApplied);
        Assert.AreEqual((PosixAccount.UserId, group), FileOwner.Of(Path.Combine(output, "shared")));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX folder's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task Receipt_AFoldersSetGroupIdBit_IsKeptOnlyWithTheGroupItGivesWhatIsMadeInIt()
    {
        // A set-group-id folder gives what is made in it the folder's group.
        // Kept on a folder whose captured group did not land, it would give
        // them the restoring account's group instead, so it goes, as a file's
        // set-id bits do (ADR-0085).
        const int SetGroupIdAndRwxrwxrX = 0x5FD;
        const int RwxrwxrX = 0x1FD;
        var source = new FakeFileSystemSource();
        source.AddFile("team/a.bin", Deterministic(700, 7));
        source.AddFile("foreign/b.bin", Deterministic(700, 8));
        source.Folders["team"] = new EntryMetadata
        {
            ModifiedAt = FolderModified, PosixMode = SetGroupIdAndRwxrwxrX, GroupName = "staff",
        };
        source.Folders["foreign"] = new EntryMetadata
        {
            ModifiedAt = FolderModified, PosixMode = SetGroupIdAndRwxrwxrX, GroupName = NoSuchGroup,
        };

        var (receipt, output) = await PublishAndRestoreAsync(
            source, 0xF5, Resolving(_ => null, name => name == "staff" ? PosixAccount.GroupId : null));

        Assert.IsNull(receipt.Items.Single(item => item.Path == "team").NotApplied);
        Assert.AreEqual((UnixFileMode)SetGroupIdAndRwxrwxrX, File.GetUnixFileMode(Path.Combine(output, "team")));

        var foreign = receipt.Items.Single(item => item.Path == "foreign");
        Assert.IsNotNull(foreign.NotApplied);
        CollectionAssert.AreEqual(new[] { "posix_mode", "group" }, foreign.NotApplied.ToArray());
        Assert.AreEqual((UnixFileMode)RwxrwxrX, File.GetUnixFileMode(Path.Combine(output, "foreign")));
    }

    [TestMethod]
    public async Task Receipt_AFolderAlreadyAtTheDestination_KeepsItsOwnMetadata_AndSaysSo()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 9));
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = FolderModified, PosixMode = Everyone };

        var output = Path.Combine(SpoolDirectory, "found-out");
        var live = Directory.CreateDirectory(Path.Combine(output, "docs")).FullName;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(live, (UnixFileMode)OwnerOnly);
        }

        var receipt = await PublishAndRestoreIntoAsync(source, 0xF6, RestoreTargetProfile.ForLocalPlatform(), output);

        // A live folder's permissions are its owner's current choice. An old
        // snapshot restored in place would otherwise reopen a folder its owner
        // has since closed.
        var docs = receipt.Items.Single(item => item.Path == "docs");
        Assert.AreEqual("restored", docs.Outcome);
        Assert.IsNotNull(docs.NotApplied);
        CollectionAssert.AreEqual(new[] { "modified_at", "posix_mode" }, docs.NotApplied.ToArray());
        Assert.IsNotNull(docs.Detail);
        Assert.Contains("already", docs.Detail, StringComparison.Ordinal);
        Assert.AreNotEqual(Utc(FolderModified), Directory.GetLastWriteTimeUtc(live));
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual((UnixFileMode)OwnerOnly, File.GetUnixFileMode(live));
        }

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task Receipt_ALinkStandingWhereAFolderIsRestored_IsNeverGivenTheFoldersMetadata()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 10));
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = FolderModified, PosixMode = Everyone };

        var outside = Outside("seeded-outside");
        var output = Path.Combine(SpoolDirectory, "seeded-out");
        Directory.CreateDirectory(output);
        Directory.CreateSymbolicLink(Path.Combine(output, "docs"), outside);

        var receipt = await PublishAndRestoreIntoAsync(source, 0xF7, Posix, output);

        AssertUntouched(outside);
        var docs = receipt.Items.Single(item => item.Path == "docs");
        Assert.IsNotNull(docs.NotApplied);
        CollectionAssert.AreEqual(new[] { "modified_at", "posix_mode" }, docs.NotApplied.ToArray());
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task Receipt_AFolderSwappedForALinkWhileTheRestoreRuns_IsNotGivenItsMetadataThroughTheLink()
    {
        // A folder's metadata goes on at the end of the run, so an account
        // that can write beside the folder has the whole run to put a link in
        // its place. Written through the link, the folder's permissions would
        // land on whatever it points at.
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 11));
        source.AddFile("docs/b.bin", Deterministic(700, 12));
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = FolderModified, PosixMode = Everyone };

        var outside = Outside("swapped-outside");
        var output = Path.Combine(SpoolDirectory, "swapped-out");
        var docs = Path.Combine(output, "docs");
        var (store, keys, catalogue) = await PublishAsync(source, 0xF8);
        using (keys)
        using (catalogue)
        {
            var plan = RestorePlanner.Plan(catalogue, SnapshotOf(0xF8), string.Empty, Posix);

            // The folder is made before the first file, and the first file's
            // content is read from a data blob: that is when the folder is
            // moved aside and a link put where it was.
            var swapping = new SwappingObjectStore(store, () =>
            {
                Directory.Move(docs, docs + "-moved");
                Directory.CreateSymbolicLink(docs, outside);
            });
            var receipt = await RestoreAsync(swapping, keys, plan, Posix, output, loaded: swapping.Arm);

            Assert.IsTrue(swapping.Swapped, "the folder was never swapped, so this proves nothing");
            AssertUntouched(outside);
            var item = receipt.Items.Single(candidate => candidate.Path == "docs");
            Assert.IsNotNull(item.NotApplied);
            CollectionAssert.AreEqual(new[] { "modified_at", "posix_mode" }, item.NotApplied.ToArray());
        }
    }

    [TestMethod]
    public async Task Receipt_AFolderWhoseOwnRecordWillNotRead_IsStillMade_AndSaysWhy()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 13));
        var (store, keys, catalogue) = await PublishAsync(source, 0xF9);
        using (keys)
        using (catalogue)
        {
            var plan = Unreadable(RestorePlanner.Plan(catalogue, SnapshotOf(0xF9), string.Empty, Posix));
            var output = Path.Combine(SpoolDirectory, "unheld-out");

            var receipt = await RestoreAsync(store, keys, plan, Posix, output);

            // The folder holds what restored under it, so it is made. What it
            // carried is unknown, so nothing can be listed as not applied: the
            // receipt says why instead. Metadata alone does not change what
            // the content achieved.
            var docs = receipt.Items.Single(item => item.Path == "docs");
            Assert.AreEqual("restored", docs.Outcome);
            Assert.IsNull(docs.NotApplied);
            Assert.IsNotNull(docs.Detail);
            Assert.Contains("could not be read", docs.Detail, StringComparison.Ordinal);
            Assert.IsTrue(File.Exists(Path.Combine(output, "docs", "a.bin")));
            Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        }
    }

    [TestMethod]
    public async Task Probe_EachFolder_SaysWhatWasCapturedWithIt()
    {
        // The run reads a folder's own record now, so the plan does too.
        var source = new FakeFileSystemSource();
        source.AddFile("shared/a.bin", Deterministic(700, 14));
        source.Folders["shared"] = new EntryMetadata
        {
            ModifiedAt = FolderModified, PosixMode = 0x5FD, OwnerName = "ana", GroupName = "staff",
        };

        var (plan, probed) = await PublishAndProbeAsync(source, 0xFA);

        var folder = plan.Items.Single(item => item.Kind == EntryKind.DirectoryPlaceholder);
        Assert.IsTrue(probed.Facts.TryGetValue(folder.ObjectId, out var known), "the plan has no facts for the folder");
        Assert.AreEqual(EntryKind.DirectoryPlaceholder, known.Kind);
        Assert.AreEqual(0UL, known.WrittenBytes);
        Assert.AreEqual(
            CapturedMetadata.ModifiedAt | CapturedMetadata.PosixMode | CapturedMetadata.Owner | CapturedMetadata.Group,
            known.Captured);
        Assert.AreEqual("ana", known.Owner);
        Assert.AreEqual("staff", known.Group);
        Assert.AreEqual(0x5FDu, known.Mode);
        Assert.IsEmpty(probed.Missing);
    }

    [TestMethod]
    public async Task Probe_AFolderWhoseOwnRecordTheStoreDoesNotHold_IsNamedMissing()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 15));
        var (store, keys, catalogue) = await PublishAsync(source, 0xFB);
        using (keys)
        using (catalogue)
        {
            var plan = Unreadable(RestorePlanner.Plan(catalogue, SnapshotOf(0xFB), string.Empty, Posix));

            var probed = await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None);

            Assert.AreEqual("docs", Assert.ContainsSingle(probed.Missing));
        }
    }

    [TestMethod]
    public async Task Plan_WhatAFolderWillNotGetBack_IsCountedApartFromTheFiles()
    {
        var source = new FakeFileSystemSource();
        foreach (var name in new[] { "a.bin", "b.bin" })
        {
            source.AddFile($"docs/{name}", Deterministic(700, 16)).Metadata =
                new EntryMetadata { ModifiedAt = FolderModified, CreatedAt = FolderCreated };
        }

        source.Folders["docs"] = new EntryMetadata
        {
            ModifiedAt = FolderModified,
            CreatedAt = FolderCreated,
            ExtendedAttributes = [new ExtendedAttributeEntry("user.tag"u8.ToArray(), "kept"u8.ToArray())],
        };

        var (plan, probed) = await PublishAndProbeAsync(source, 0xFC);

        var declared = RestoreMetadata.Declare(plan, probed.Facts, Posix);

        var created = Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "creation-times"));
        Assert.Contains("2 file(s) and 1 folder(s)", created.Detail, StringComparison.Ordinal);
        var attributes = Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "extended-attributes"));
        Assert.Contains("1 folder(s)", attributes.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("file(s)", attributes.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX folder's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Plan_AFoldersOwnershipAndSetIdBits_AreDeclaredByTheRuleAFilesAre()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("theirs/a.bin", Deterministic(700, 17));
        source.AddFile("ghost/b.bin", Deterministic(700, 18));
        source.Folders["theirs"] = new EntryMetadata
        {
            ModifiedAt = FolderModified, PosixMode = 0x5FD, OwnerName = "ana", GroupName = "wheel",
        };
        source.Folders["ghost"] = new EntryMetadata { ModifiedAt = FolderModified, OwnerName = "ghost" };

        var (plan, probed) = await PublishAndProbeAsync(source, 0xFD);

        // The account is ana, in staff and not in wheel; ghost is no account
        // at all here. So theirs keeps its owner, cannot be given wheel, and
        // so loses its set-group-id bit; ghost's owner is no account here.
        var account = new RestoreAccount
        {
            UserId = 1_000,
            GroupIds = new HashSet<uint> { 1_000 },
            MayGiveFilesAway = false,
            ResolveUser = name => name == "ana" ? 1_000u : null,
            ResolveGroup = name => name switch { "staff" => 1_000u, "wheel" => 0u, _ => null },
        };

        var declared = RestoreMetadata.Declare(plan, probed.Facts, Posix with { Account = account });

        Assert.Contains(
            "1 folder(s)",
            Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "ownership")).Detail,
            StringComparison.Ordinal);
        Assert.Contains(
            "1 folder(s)",
            Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "unknown-accounts")).Detail,
            StringComparison.Ordinal);
        Assert.Contains(
            "1 folder(s)",
            Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "set-id-bits")).Detail,
            StringComparison.Ordinal);
    }

    private static DateTime Utc(ulong milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds).UtcDateTime;

    private static byte[] SnapshotOf(byte seed) => Enumerable.Repeat(seed, 16).ToArray();

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }

    /// <summary><paramref name="plan"/> with each folder naming a record no store holds.</summary>
    private static RestorePlan Unreadable(RestorePlan plan) => plan with
    {
        Items =
        [
            .. plan.Items.Select(item =>
                item.Kind == EntryKind.DirectoryPlaceholder ? item with { ObjectId = Unheld } : item),
        ],
    };

    /// <summary>
    /// This machine's target, with the restoring account's names resolved
    /// as the test says, so an owner can be any account the test needs.
    /// </summary>
    private static RestoreTargetProfile Resolving(Func<string, uint?> users, Func<string, uint?> groups)
    {
        var target = RestoreTargetProfile.ForLocalPlatform();
        Assert.IsNotNull(target.Account);
        return target with { Account = target.Account with { ResolveUser = users, ResolveGroup = groups } };
    }

    /// <summary>A folder outside every restore's root, closed to all but its owner and last modified long ago.</summary>
    [UnsupportedOSPlatform("windows")]
    private string Outside(string name)
    {
        var outside = Directory.CreateDirectory(Path.Combine(SpoolDirectory, name)).FullName;
        File.SetUnixFileMode(outside, (UnixFileMode)OwnerOnly);
        Directory.SetLastWriteTimeUtc(outside, Utc(Before));
        return outside;
    }

    [UnsupportedOSPlatform("windows")]
    private static void AssertUntouched(string outside)
    {
        Assert.AreEqual((UnixFileMode)OwnerOnly, File.GetUnixFileMode(outside), "a folder's permissions went through a link");
        Assert.AreEqual(Utc(Before), Directory.GetLastWriteTimeUtc(outside), "a folder's times went through a link");
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte seed) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = SnapshotOf(seed),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "restore-folder-tests/1.0",
    };

    private async Task<(LocalFileSystemObjectStore Store, RepositoryKeySet Keys, CatalogueDb Catalogue)> PublishAsync(
        FakeFileSystemSource source, byte seed)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"folders-{seed:x2}.db"), Repo);

        await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, $"sequence-{seed:x2}.txt"))),
                SpoolDirectory, FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(Job(source, seed), CancellationToken.None);

        return (store, keys, catalogue);
    }

    private static async Task<RestoreReceipt> RestoreAsync(
        IObjectStore store, RepositoryKeySet keys, RestorePlan plan, RestoreTargetProfile target, string output,
        Action? loaded = null)
    {
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        loaded?.Invoke();

        return await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "folders",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);
    }

    private async Task<(RestoreReceipt Receipt, string Output)> PublishAndRestoreAsync(
        FakeFileSystemSource source, byte seed, RestoreTargetProfile target)
    {
        var output = Path.Combine(SpoolDirectory, $"restored-{seed:x2}");
        return (await PublishAndRestoreIntoAsync(source, seed, target, output), output);
    }

    private async Task<RestoreReceipt> PublishAndRestoreIntoAsync(
        FakeFileSystemSource source, byte seed, RestoreTargetProfile target, string output)
    {
        var (store, keys, catalogue) = await PublishAsync(source, seed);
        using (keys)
        using (catalogue)
        {
            var plan = RestorePlanner.Plan(catalogue, SnapshotOf(seed), string.Empty, target);
            return await RestoreAsync(store, keys, plan, target, output);
        }
    }

    private async Task<(RestorePlan Plan, RestoreBlobSetResult Probed)> PublishAndProbeAsync(
        FakeFileSystemSource source, byte seed)
    {
        var (store, keys, catalogue) = await PublishAsync(source, seed);
        using (keys)
        using (catalogue)
        {
            var plan = RestorePlanner.Plan(catalogue, SnapshotOf(seed), string.Empty, Posix);
            return (plan, await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None));
        }
    }

    /// <summary>
    /// A store that, once armed, runs an action the first time a data blob is
    /// read: by then the restore has made the folders ahead of the first file
    /// and is reading that file's content.
    /// </summary>
    private sealed class SwappingObjectStore(IObjectStore inner, Action swap) : IObjectStore
    {
        private int _armed;

        /// <summary>Whether the action ran.</summary>
        public bool Swapped { get; private set; }

        /// <inheritdoc />
        public StoreCapabilities Capabilities => inner.Capabilities;

        /// <summary>Runs the action at the next data blob read, and only then.</summary>
        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        /// <inheritdoc />
        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        /// <inheritdoc />
        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
        {
            if (key.Value.StartsWith("blobs/data/", StringComparison.Ordinal) && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                swap();
                Swapped = true;
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
