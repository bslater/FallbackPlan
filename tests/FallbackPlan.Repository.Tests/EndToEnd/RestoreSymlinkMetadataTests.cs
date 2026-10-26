using System.Runtime.Versioning;
using FallbackPlan.Domain;
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
/// A restore gives each symlink it makes its own captured metadata back, by
/// the rule and in the order it gives a file's (FR-RST-004; ADR-0101): its
/// owner and group where the restoring account may give them, its extended
/// attributes, its permissions where the platform keeps a link's own, its
/// modification and access times, and its creation time where the target can
/// set one. Every write lands on the link itself, never on what it points
/// at. The plan counts what a link will not get back on the lines files and
/// folders use (FR-RST-003).
/// </summary>
/// <remarks>
/// <para>
/// A link may point anywhere: out of the restore, at a file the restore
/// lands beside it, or at nothing. A write that followed it would stamp the
/// link's captured times, owner or permissions onto whatever it points at,
/// which a restore as root could be any file on the machine. So the target a
/// link points at keeps its own in every test here.
/// </para>
/// <para>
/// Linux gives every link <c>rwxrwxrwx</c> and has no call that changes it,
/// and keeps no user-namespace attribute and no ACL on one. A link captured
/// there carries those permissions and none of those attributes, so it
/// lists nothing. One captured elsewhere with others is listed, and the plan
/// says so first.
/// </para>
/// </remarks>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class RestoreSymlinkMetadataTests : ArchiveTestHarness
{
    /// <summary>When a link was made: 2020-09-13.</summary>
    private const ulong LinkModified = 1_600_000_000_000;

    /// <summary>When a link was last read: 2022-04-15.</summary>
    private const ulong LinkAccessed = 1_650_000_000_000;

    /// <summary>When a link was made, as a creation time: 2020-05-20.</summary>
    private const ulong LinkCreated = 1_590_000_000_000;

    /// <summary>When the file a link points at was modified: 2021-01-07.</summary>
    private const ulong TargetModified = 1_610_000_000_000;

    /// <summary>When the file a link points at was last read: 2021-06-01.</summary>
    private const ulong TargetAccessed = 1_622_500_000_000;

    /// <summary>A time a file outside the restore carries: 2001-09-09.</summary>
    private const ulong Before = 1_000_000_000_000;

    /// <summary><c>rwxrwxrwx</c>: the permissions Linux gives every link.</summary>
    private const uint Everyone = 0x1FF;

    /// <summary><c>rwxr-xr-x</c>: what macOS gives a link it makes.</summary>
    private const uint MacOsDefault = 0x1ED;

    /// <summary><c>rw-------</c>: a file its owner has closed to everyone else.</summary>
    private const int OwnerOnly = 0x180;

    /// <summary>An account no machine the suite runs on has.</summary>
    private const string NoSuchAccount = "fallbackplan-no-such-account";

    /// <summary>An id no account or group is given on any machine the suite runs on.</summary>
    private const uint Unclaimed = 54_321;

    private static readonly RestoreTargetProfile Posix = new()
    {
        CaseSensitive = true,
        SupportsPosixMetadata = true,
        SupportsSymlinks = true,
    };

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Receipt_ALink_GetsItsOwnTimesBack_AndWhatItPointsAtKeepsItsOwn()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("data/target.bin", Deterministic(500, 1)).Metadata =
            new EntryMetadata { ModifiedAt = TargetModified, AccessedAt = TargetAccessed };
        AddLink(source, "link", "data/target.bin", new EntryMetadata { ModifiedAt = LinkModified, AccessedAt = LinkAccessed });

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xA1, RestoreTargetProfile.ForLocalPlatform());

        var link = LinkStatus.Of(Path.Combine(output, "link"));
        Assert.AreEqual(Utc(LinkModified), link.ModifiedUtc);
        Assert.AreEqual(Utc(LinkAccessed), link.AccessedUtc);
        var target = Path.Combine(output, "data", "target.bin");
        Assert.AreEqual(Utc(TargetModified), File.GetLastWriteTimeUtc(target), "a link's times went through it");
        Assert.AreEqual(Utc(TargetAccessed), File.GetLastAccessTimeUtc(target), "a link's times went through it");

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.IsNull(receipt.Items.Single(item => item.Path == "link").NotApplied);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Receipt_ALinkThatPointsOutOfTheRestore_IsGivenItsOwn_AndWhatItPointsAtIsUntouched()
    {
        // The restore as root's case: a historical link to a system file.
        // Followed, the link's captured owner, permissions and times would be
        // that file's.
        var outside = Path.Combine(Directory.CreateDirectory(Path.Combine(SpoolDirectory, "outside-a2")).FullName, "guarded.bin");
        await File.WriteAllBytesAsync(outside, [1, 2, 3]);
        File.SetUnixFileMode(outside, (UnixFileMode)OwnerOnly);
        File.SetLastWriteTimeUtc(outside, Utc(Before));
        var ownerBefore = FileOwner.Of(outside);

        var source = new FakeFileSystemSource();
        AddLink(source, "escape", outside, new EntryMetadata
        {
            ModifiedAt = LinkModified,
            AccessedAt = LinkAccessed,
            PosixMode = OperatingSystem.IsMacOS() ? MacOsDefault : Everyone,
            OwnerName = "ana",
            GroupName = "staff",
        });

        var group = GivableGroup();
        var (receipt, output) = await PublishAndRestoreAsync(
            source, 0xA2, Resolving(user => user == "ana" ? PosixAccount.UserId : null, name => name == "staff" ? group : null));

        Assert.AreEqual((UnixFileMode)OwnerOnly, File.GetUnixFileMode(outside), "a link's permissions went through it");
        Assert.AreEqual(Utc(Before), File.GetLastWriteTimeUtc(outside), "a link's times went through it");
        Assert.AreEqual(ownerBefore, FileOwner.Of(outside), "a link's owner went through it");

        var link = LinkStatus.Of(Path.Combine(output, "escape"));
        Assert.AreEqual(Utc(LinkModified), link.ModifiedUtc);
        Assert.AreEqual((PosixAccount.UserId, group), (link.UserId, link.GroupId));
        Assert.IsNull(receipt.Items.Single(item => item.Path == "escape").NotApplied);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Receipt_ALinkThatPointsAtNothing_StillGetsItsTimesBack()
    {
        var source = new FakeFileSystemSource();
        AddLink(source, "dangling", "never-captured.bin", new EntryMetadata { ModifiedAt = LinkModified, AccessedAt = LinkAccessed });

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xA3, RestoreTargetProfile.ForLocalPlatform());

        Assert.AreEqual(Utc(LinkModified), LinkStatus.Of(Path.Combine(output, "dangling")).ModifiedUtc);
        Assert.IsNull(receipt.Items.Single(item => item.Path == "dangling").NotApplied);
        Assert.IsFalse(File.Exists(Path.Combine(output, "never-captured.bin")));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX link's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Receipt_ALinksOwnerAndGroup_AreGivenBackWhereTheAccountMayGiveThem_AndWhatItPointsAtKeepsItsOwn()
    {
        var source = new FakeFileSystemSource();
        // The target is given the account's own group, and comes before the
        // link, so a group written through the link would be the last word.
        source.AddFile("data/target.bin", Deterministic(500, 4)).Metadata =
            new EntryMetadata { ModifiedAt = TargetModified, GroupName = "own" };
        AddLink(source, "shared", "data/target.bin", new EntryMetadata
        {
            ModifiedAt = LinkModified, OwnerName = "ana", GroupName = "staff",
        });

        var group = GivableGroup();
        var (receipt, output) = await PublishAndRestoreAsync(
            source, 0xA4, Resolving(
                user => user == "ana" ? PosixAccount.UserId : null,
                name => name switch { "staff" => group, "own" => PosixAccount.GroupId, _ => null }));

        var link = LinkStatus.Of(Path.Combine(output, "shared"));
        Assert.AreEqual((PosixAccount.UserId, group), (link.UserId, link.GroupId));
        Assert.AreEqual(
            (PosixAccount.UserId, PosixAccount.GroupId), FileOwner.Of(Path.Combine(output, "data", "target.bin")),
            "a link's group went through it");
        Assert.IsNull(receipt.Items.Single(item => item.Path == "shared").NotApplied);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX link's owner is an account the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Receipt_ALinksOwnerThatResolvesToNoAccount_IsListed_AndItsTimesStillLand()
    {
        var source = new FakeFileSystemSource();
        AddLink(source, "orphan", "elsewhere", new EntryMetadata { ModifiedAt = LinkModified, OwnerName = NoSuchAccount });

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xA5, RestoreTargetProfile.ForLocalPlatform());

        CollectionAssert.AreEqual(new[] { "owner" }, receipt.Items.Single(item => item.Path == "orphan").NotApplied!.ToArray());
        Assert.AreEqual(Utc(LinkModified), LinkStatus.Of(Path.Combine(output, "orphan")).ModifiedUtc);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux gives every link the same permissions")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task Receipt_OnLinux_ALinkCapturedWithThePermissionsEveryLinkHasThere_ListsNothing_AndOneCapturedWithOthersListsThem()
    {
        var source = new FakeFileSystemSource();
        AddLink(source, "captured-here", "elsewhere", new EntryMetadata { ModifiedAt = LinkModified, PosixMode = Everyone });
        AddLink(source, "captured-on-macos", "elsewhere", new EntryMetadata { ModifiedAt = LinkModified, PosixMode = MacOsDefault });

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xA6, RestoreTargetProfile.ForLocalPlatform());

        Assert.IsNull(receipt.Items.Single(item => item.Path == "captured-here").NotApplied);
        CollectionAssert.AreEqual(
            new[] { "posix_mode" }, receipt.Items.Single(item => item.Path == "captured-on-macos").NotApplied!.ToArray());
        Assert.AreEqual(Everyone, LinkStatus.Of(Path.Combine(output, "captured-on-macos")).Permissions);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "macOS keeps a link's own permissions")]
    [PlatformTrait(TestPlatforms.MacOs)]
    public async Task Receipt_OnMacOs_ALinksPermissions_AreWrittenOntoTheLinkItself_AndWhatItPointsAtKeepsItsOwn()
    {
        const uint Closed = 0x140;
        var source = new FakeFileSystemSource();
        source.AddFile("data/target.bin", Deterministic(500, 7)).Metadata = new EntryMetadata { PosixMode = (uint)OwnerOnly };
        AddLink(source, "closed", "data/target.bin", new EntryMetadata { PosixMode = Closed });

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xA7, RestoreTargetProfile.ForLocalPlatform());

        Assert.AreEqual(Closed, LinkStatus.Of(Path.Combine(output, "closed")).Permissions);
        Assert.AreEqual(
            (UnixFileMode)OwnerOnly, File.GetUnixFileMode(Path.Combine(output, "data", "target.bin")), "a link's permissions went through it");
        Assert.IsNull(receipt.Items.Single(item => item.Path == "closed").NotApplied);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "only macOS sets a link's creation time")]
    [PlatformTrait(TestPlatforms.MacOs)]
    public async Task Receipt_OnMacOs_ALinksCreationTime_IsWrittenOntoTheLinkItself()
    {
        var source = new FakeFileSystemSource();
        AddLink(source, "made", "elsewhere", new EntryMetadata { ModifiedAt = LinkModified, CreatedAt = LinkCreated });

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xA8, RestoreTargetProfile.ForLocalPlatform());

        var link = LinkStatus.Of(Path.Combine(output, "made"));
        Assert.AreEqual(Utc(LinkCreated), link.CreatedUtc);
        Assert.AreEqual(Utc(LinkModified), link.ModifiedUtc);
        Assert.IsNull(receipt.Items.Single(item => item.Path == "made").NotApplied);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux keeps no user attribute on a link")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task Receipt_OnLinux_AUserAttributeOrAnAclOnALink_IsListed_WithWhy()
    {
        var source = new FakeFileSystemSource();
        AddLink(source, "tagged", "elsewhere", new EntryMetadata
        {
            ModifiedAt = LinkModified,
            ExtendedAttributes =
            [
                new ExtendedAttributeEntry("user.tag"u8.ToArray(), "kept"u8.ToArray()),
                new ExtendedAttributeEntry("system.posix_acl_access"u8.ToArray(), Xattr.Acl((0x01, 7, 0), (0x04, 5, 0), (0x20, 5, 0))),
            ],
        });

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xA9, RestoreTargetProfile.ForLocalPlatform());

        var tagged = receipt.Items.Single(item => item.Path == "tagged");
        CollectionAssert.AreEqual(new[] { "extended_attributes" }, tagged.NotApplied!.ToArray());
        Assert.Contains("user.tag", tagged.Detail!, StringComparison.Ordinal);
        Assert.Contains("system.posix_acl_access", tagged.Detail!, StringComparison.Ordinal);
        Assert.Contains("on a symlink", tagged.Detail!, StringComparison.Ordinal);
        Assert.AreEqual(Utc(LinkModified), LinkStatus.Of(Path.Combine(output, "tagged")).ModifiedUtc);
    }

    [TestMethod]
    [PrivilegedPlatformCondition(TestPlatforms.Linux, "only root writes Linux's trusted namespace")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task Receipt_AsRoot_ALinksTrustedAttribute_IsWrittenOntoTheLinkItself_AndNotOntoWhatItPointsAt()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("data/target.bin", Deterministic(500, 10));
        AddLink(source, "labelled", "data/target.bin", new EntryMetadata
        {
            ExtendedAttributes = [new ExtendedAttributeEntry("trusted.tag"u8.ToArray(), "link's"u8.ToArray())],
        });

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xAA, RestoreTargetProfile.ForLocalPlatform());

        CollectionAssert.AreEqual("link's"u8.ToArray(), Xattr.Get(Path.Combine(output, "labelled"), "trusted.tag"));
        Assert.IsNull(Xattr.Get(Path.Combine(output, "data", "target.bin"), "trusted.tag"), "a link's attribute went through it");
        Assert.IsNull(receipt.Items.Single(item => item.Path == "labelled").NotApplied);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX target makes links and resolves their owners by name")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Plan_WhatALinkWillNotGetBack_IsCountedOnTheLinesFilesAndFoldersUse_AndNoLineSpeaksForEveryLink()
    {
        var source = new FakeFileSystemSource();
        foreach (var name in new[] { "a.bin", "b.bin" })
        {
            source.AddFile($"docs/{name}", Deterministic(700, 11)).Metadata =
                new EntryMetadata { ModifiedAt = TargetModified, CreatedAt = LinkCreated };
        }

        source.Folders["docs"] = new EntryMetadata { ModifiedAt = TargetModified, CreatedAt = LinkCreated };
        AddLink(source, "docs/link", "a.bin", new EntryMetadata
        {
            ModifiedAt = LinkModified, CreatedAt = LinkCreated, OwnerName = NoSuchAccount,
        });

        var (plan, facts) = await PublishAndProbeAsync(source, 0xB1);

        var target = RestoreTargetProfile.ForLocalPlatform() with
        {
            SupportsCreationTimes = false,
            Account = RestoreTargetProfile.ForLocalPlatform().Account! with { ResolveUser = _ => null, ResolveGroup = _ => null },
        };
        var declared = RestoreMetadata.Declare(plan, facts, target);

        var created = Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "creation-times"));
        Assert.Contains("2 file(s), 1 folder(s) and 1 symlink(s)", created.Detail, StringComparison.Ordinal);
        var unknown = Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "unknown-accounts"));
        Assert.Contains("1 symlink(s)", unknown.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(degradation => degradation.Capability == "symlink-metadata", declared);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux gives every link the same permissions and keeps no user attribute on one")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task Plan_OnLinux_ALinksPermissionsAndAttributesThatCannotLandThere_AreDeclared_AndALinkCapturedThereIsNot()
    {
        var source = new FakeFileSystemSource();
        AddLink(source, "native", "elsewhere", new EntryMetadata { ModifiedAt = LinkModified, PosixMode = Everyone });
        AddLink(source, "moded", "elsewhere", new EntryMetadata { ModifiedAt = LinkModified, PosixMode = MacOsDefault });
        AddLink(source, "tagged", "elsewhere", new EntryMetadata
        {
            ModifiedAt = LinkModified,
            PosixMode = Everyone,
            ExtendedAttributes = [new ExtendedAttributeEntry("user.tag"u8.ToArray(), "kept"u8.ToArray())],
        });

        var (plan, facts) = await PublishAndProbeAsync(source, 0xB2);

        var declared = RestoreMetadata.Declare(plan, facts, RestoreTargetProfile.ForLocalPlatform());

        Assert.Contains(
            "1 symlink(s)",
            Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "symlink-permissions")).Detail,
            StringComparison.Ordinal);
        Assert.Contains(
            "1 symlink(s)",
            Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "symlink-attributes")).Detail,
            StringComparison.Ordinal);
        Assert.HasCount(2, declared);
    }

    [TestMethod]
    public async Task Plan_ALinkOnATargetThatCannotMakeOne_IsLeftToThePlannersLine_AndNothingElseCountsIt()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 12));
        AddLink(source, "docs/link", "a.bin", new EntryMetadata
        {
            ModifiedAt = LinkModified, CreatedAt = LinkCreated, OwnerName = NoSuchAccount,
        });

        var (plan, facts) = await PublishAndProbeAsync(source, 0xB3);

        var declared = RestoreMetadata.Declare(plan, facts, Posix with { SupportsSymlinks = false });

        Assert.DoesNotContain(degradation => degradation.Detail.Contains("symlink", StringComparison.Ordinal), declared);
    }

    private static void AddLink(FakeFileSystemSource source, string path, string target, EntryMetadata metadata) =>
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = path,
            Kind = ScanEntryKind.Symlink,
            LinkTarget = System.Text.Encoding.UTF8.GetBytes(target),
            Metadata = metadata,
        });

    /// <summary>
    /// A group the restoring account may give a link: any at all with
    /// privilege, else one it is in other than the one a link is made with,
    /// where it has one.
    /// </summary>
    private static uint GivableGroup() => Environment.IsPrivilegedProcess
        ? Unclaimed
        : PosixAccount.Groups.FirstOrDefault(candidate => candidate != PosixAccount.GroupId, PosixAccount.GroupId);

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
        ClientVersion = "restore-symlink-tests/1.0",
    };

    private async Task<(LocalFileSystemObjectStore Store, RepositoryKeySet Keys, CatalogueDb Catalogue)> PublishAsync(
        FakeFileSystemSource source, byte seed)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"links-{seed:x2}.db"), Repo);

        await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, $"sequence-{seed:x2}.txt"))),
                SpoolDirectory, FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(Job(source, seed), CancellationToken.None);

        return (store, keys, catalogue);
    }

    private async Task<(RestoreReceipt Receipt, string Output)> PublishAndRestoreAsync(
        FakeFileSystemSource source, byte seed, RestoreTargetProfile target)
    {
        var output = Path.Combine(SpoolDirectory, $"restored-{seed:x2}");
        var (store, keys, catalogue) = await PublishAsync(source, seed);
        using (keys)
        using (catalogue)
        {
            var plan = RestorePlanner.Plan(catalogue, SnapshotOf(seed), string.Empty, target);
            using var reader = new RepositoryReader(Repo, keys, store, Authority);
            await reader.LoadBlobsAsync(CancellationToken.None);
            var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
                plan, output,
                new RestoreExecutionOptions
                {
                    DestinationMode = RestoreDestinationMode.InPlace,
                    RunId = "links",
                    NowUnixMilliseconds = 1_722_700_000_000,
                },
                CancellationToken.None);
            return (receipt, output);
        }
    }

    private async Task<(RestorePlan Plan, IReadOnlyDictionary<Domain.Identifiers.ObjectId, RestoreItemFacts> Facts)> PublishAndProbeAsync(
        FakeFileSystemSource source, byte seed)
    {
        var (store, keys, catalogue) = await PublishAsync(source, seed);
        using (keys)
        using (catalogue)
        {
            var plan = RestorePlanner.Plan(catalogue, SnapshotOf(seed), string.Empty, Posix);
            var probed = await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None);
            return (plan, probed.Facts);
        }
    }
}
