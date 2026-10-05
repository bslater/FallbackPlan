using System.Runtime.Versioning;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A restore says which captured metadata it will not write back, before it
/// starts and after it ends (FR-RST-003, FR-RST-004; ADR-0083; architecture
/// 06 §3's "no silent drop"). The plan declares each attribute the tree
/// carries and the target will not get back, with how many files carry it.
/// For ownership it names the privilege applying it needs. The receipt names
/// what was not applied to each item it landed.
/// </summary>
/// <remarks>
/// <para>
/// The plan predicts by the target's rule; the receipt records what each
/// attribute's write actually did (ADR-0084), so a write the platform
/// refuses is listed even where the rule expected it to land. A file gets
/// its modification and access times everywhere, its creation time where
/// the target can set one, and its permissions where the target applies
/// POSIX metadata. It gets its owner and group where the names resolve on
/// the target and the account running the restore may give them (ADR-0085):
/// root, or CAP_CHOWN, may give a file to anyone, and any other account
/// keeps a file as its own and gives it a group it is in. The plan
/// predicts that per file. Security descriptors, extended attributes and
/// file attributes are captured and recorded, not yet written back, and a
/// symlink is created with none of its own.
/// </para>
/// <para>
/// An item's outcome does not change for metadata alone. A file whose
/// content landed and verified is restored. What it lacks is said beside it,
/// as the plan said it would be. Alternate streams stay the exception they
/// were: a file missing one of its streams is not the captured file, so it
/// is degraded.
/// </para>
/// </remarks>
[TestClass]
public sealed class RestoreMetadataHonestyTests : ArchiveTestHarness
{
    private const ulong Modified = 1_722_000_000_000;

    /// <summary>An account no machine the suite runs on has.</summary>
    private const string NoSuchAccount = "fallbackplan-no-such-account";

    /// <summary>A group no machine the suite runs on has.</summary>
    private const string NoSuchGroup = "fallbackplan-no-such-group";

    /// <summary>An id no account or group is given on any machine the suite runs on.</summary>
    private const uint Unclaimed = 54_321;

    private static readonly RestoreTargetProfile Posix = new()
    {
        CaseSensitive = true,
        SupportsPosixMetadata = true,
        SupportsSymlinks = true,
    };

    [TestMethod]
    public async Task Receipt_AFileCarryingMetadataTheTargetDoesNotApply_NamesEachAttributeNotApplied()
    {
        var source = new FakeFileSystemSource();
        var file = source.AddFile("data/owned.bin", Deterministic(3_000, 1));
        file.Metadata = new EntryMetadata
        {
            ModifiedAt = Modified,
            CreatedAt = 1_721_000_000_000,
            AccessedAt = 1_722_500_000_000,
            OwnerName = NoSuchAccount,
            GroupName = NoSuchGroup,
            ExtendedAttributes = [new ExtendedAttributeEntry("user.tag"u8.ToArray(), "kept"u8.ToArray())],
            FileAttributes = 0x20,
        };

        // A target that sets no creation times, and an owner and group no
        // machine has, so the list is one list on every platform and under
        // any account; the tests below hold the others.
        var (receipt, output) = await PublishAndRestoreAsync(
            source, 0xC1, RestoreTargetProfile.ForLocalPlatform() with { SupportsCreationTimes = false });

        var item = receipt.Items.Single(candidate => candidate.Path == "data/owned.bin");
        Assert.AreEqual("restored", item.Outcome, "metadata alone does not change what the content achieved");
        Assert.IsNotNull(item.NotApplied);
        CollectionAssert.AreEqual(
            new[] { "created_at", "owner", "group", "extended_attributes", "file_attributes" },
            item.NotApplied.ToArray());

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.AreEqual(6, receipt.SchemaVersion);
        Assert.AreEqual(6, RestoreReceipt.CurrentSchemaVersion);
        Assert.Contains("\"not_applied\"", receipt.ToJson(), StringComparison.Ordinal);

        // What it did apply, it applied. Nothing has read the file since, so
        // its access time is still the one the restore wrote.
        var landed = Path.Combine(output, "data", "owned.bin");
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds((long)Modified).UtcDateTime, File.GetLastWriteTimeUtc(landed));
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1_722_500_000_000).UtcDateTime, File.GetLastAccessTimeUtc(landed));

        // A directory's own captured metadata is not read back yet (ADR-0084
        // says so), so its item says nothing.
        Assert.IsNull(receipt.Items.Single(candidate => candidate.Path == "data").NotApplied);
    }

    [TestMethod]
    public async Task Receipt_AFileCarryingOnlyWhatEveryTargetApplies_SaysNothingMore()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("plain.bin", Deterministic(1_000, 2)).Metadata = new EntryMetadata { ModifiedAt = Modified };

        var (receipt, _) = await PublishAndRestoreAsync(source, 0xC2, RestoreTargetProfile.ForLocalPlatform());

        Assert.IsNull(Assert.ContainsSingle(receipt.Items).NotApplied);
        Assert.DoesNotContain("not_applied", receipt.ToJson(), StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows | TestPlatforms.MacOs, "only Windows and macOS can set a file's creation time")]
    [PlatformTrait(TestPlatforms.Windows | TestPlatforms.MacOs)]
    public async Task Receipt_ACreationTimeTheTargetCanSet_IsWrittenBack_AndNotListed()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("created.bin", Deterministic(1_000, 10)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, CreatedAt = 1_721_000_000_000 };

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xC7, RestoreTargetProfile.ForLocalPlatform());

        Assert.IsNull(Assert.ContainsSingle(receipt.Items).NotApplied);
        var landed = Path.Combine(output, "created.bin");
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1_721_000_000_000).UtcDateTime, File.GetCreationTimeUtc(landed));
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds((long)Modified).UtcDateTime, File.GetLastWriteTimeUtc(landed));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux has no call that sets a file's creation time")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task Receipt_ACreationTimeThePlatformCannotSet_IsListed_WhateverTheProfileClaims()
    {
        // The receipt records what the write did, not what the rule expected:
        // a profile claiming creation times on Linux still gets none.
        var source = new FakeFileSystemSource();
        source.AddFile("created.bin", Deterministic(1_000, 11)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, CreatedAt = 1_721_000_000_000 };

        var (receipt, output) = await PublishAndRestoreAsync(
            source, 0xC8, RestoreTargetProfile.ForLocalPlatform() with { SupportsCreationTimes = true });

        CollectionAssert.AreEqual(new[] { "created_at" }, Assert.ContainsSingle(receipt.Items).NotApplied!.ToArray());
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds((long)Modified).UtcDateTime,
            File.GetLastWriteTimeUtc(Path.Combine(output, "created.bin")),
            "an attempt at the creation time must not land on the modification time");
    }

    [TestMethod]
    public async Task Receipt_ATimeBeyondWhatAFileCanCarry_IsNotApplied_AndTheRunCompletes()
    {
        // A manifest is untrusted input. A modification time in the year 10000
        // used to end the whole run with no receipt at all.
        var source = new FakeFileSystemSource();
        source.AddFile("far.bin", Deterministic(1_000, 12)).Metadata =
            new EntryMetadata { ModifiedAt = 253_402_300_800_000, AccessedAt = 1_722_500_000_000 };

        var (receipt, _) = await PublishAndRestoreAsync(source, 0xC9, RestoreTargetProfile.ForLocalPlatform());

        var item = Assert.ContainsSingle(receipt.Items);
        Assert.AreEqual("restored", item.Outcome);
        CollectionAssert.AreEqual(new[] { "modified_at" }, item.NotApplied!.ToArray());
        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX file's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Receipt_AnOwnerAndGroupTheAccountMayGive_AreWrittenBack_AndNotListed()
    {
        // The account itself, and a group it may give: any at all with
        // privilege, else one it is in, other than the one a file is made
        // with where it has one.
        var group = Environment.IsPrivilegedProcess
            ? Unclaimed
            : PosixAccount.Groups.FirstOrDefault(candidate => candidate != PosixAccount.GroupId, PosixAccount.GroupId);
        var source = new FakeFileSystemSource();
        source.AddFile("owned.bin", Deterministic(1_000, 14)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, OwnerName = "ana", GroupName = "staff" };

        var (receipt, output) = await PublishAndRestoreAsync(
            source, 0xCB, Resolving(user => user == "ana" ? PosixAccount.UserId : null, name => name == "staff" ? group : null));

        Assert.IsNull(Assert.ContainsSingle(receipt.Items).NotApplied);
        Assert.AreEqual((PosixAccount.UserId, group), FileOwner.Of(Path.Combine(output, "owned.bin")));
    }

    [TestMethod]
    [UnprivilegedPlatformCondition(TestPlatforms.Posix, "only an unprivileged restore is refused another account")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Receipt_AnOwnerTheAccountMayNotGive_IsListed_AndTheFileStaysItsOwn()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("theirs.bin", Deterministic(1_000, 15)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, OwnerName = "ben" };

        var (receipt, output) = await PublishAndRestoreAsync(
            source, 0xCC, Resolving(user => user == "ben" ? 0u : null, _ => null));

        var item = Assert.ContainsSingle(receipt.Items);
        Assert.AreEqual("restored", item.Outcome, "metadata alone does not change what the content achieved");
        CollectionAssert.AreEqual(new[] { "owner" }, item.NotApplied!.ToArray());
        Assert.AreEqual(PosixAccount.UserId, FileOwner.Of(Path.Combine(output, "theirs.bin")).UserId);
    }

    [TestMethod]
    [PrivilegedPlatformCondition(TestPlatforms.Posix, "only a privileged restore may give a file to another account")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Receipt_AnOwnerAPrivilegedRestoreMayGive_IsWrittenBack()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("theirs.bin", Deterministic(1_000, 16)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, OwnerName = "ben" };

        var (receipt, output) = await PublishAndRestoreAsync(
            source, 0xCD, Resolving(user => user == "ben" ? Unclaimed : null, _ => null));

        Assert.IsNull(Assert.ContainsSingle(receipt.Items).NotApplied);
        Assert.AreEqual(Unclaimed, FileOwner.Of(Path.Combine(output, "theirs.bin")).UserId);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX file's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task Receipt_ASetUserIdBit_SurvivesTheOwnershipWrite()
    {
        // Changing a file's owner or group clears its set-user-id bit, so the
        // permissions are written after the ownership, never before.
        const int SetUserIdAndRwxrXrX = 0x9ED;
        var source = new FakeFileSystemSource();
        source.AddFile("tool", Deterministic(1_000, 17)).Metadata = new EntryMetadata
        {
            ModifiedAt = Modified, PosixMode = SetUserIdAndRwxrXrX, OwnerName = "ana", GroupName = "staff",
        };

        var (receipt, output) = await PublishAndRestoreAsync(
            source, 0xCE,
            Resolving(user => user == "ana" ? PosixAccount.UserId : null, name => name == "staff" ? PosixAccount.GroupId : null));

        Assert.IsNull(Assert.ContainsSingle(receipt.Items).NotApplied);
        Assert.AreEqual((UnixFileMode)SetUserIdAndRwxrXrX, File.GetUnixFileMode(Path.Combine(output, "tool")));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX file's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task Receipt_ASetIdBitWhoseOwnerOrGroupIsNotGivenBack_IsDropped_AndSaid()
    {
        // A set-user-id file runs as its owner. Kept on a file whose captured
        // owner did not land, it would run as whoever restored it, root
        // included: a file wrong in a way nobody would see. So the bit goes,
        // and the permissions are said not applied; set-group-id likewise.
        const int SetUserIdAndRwxrXrX = 0x9ED;
        const int SetGroupIdAndRwxrXrX = 0x5ED;
        const int RwxrXrX = 0x1ED;
        var source = new FakeFileSystemSource();
        source.AddFile("tool", Deterministic(1_000, 18)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, PosixMode = SetUserIdAndRwxrXrX, OwnerName = NoSuchAccount };
        source.AddFile("shared", Deterministic(1_000, 19)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, PosixMode = SetGroupIdAndRwxrXrX, GroupName = NoSuchGroup };

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xD1, RestoreTargetProfile.ForLocalPlatform());

        CollectionAssert.AreEqual(
            new[] { "posix_mode", "owner" }, receipt.Items.Single(item => item.Path == "tool").NotApplied!.ToArray());
        CollectionAssert.AreEqual(
            new[] { "posix_mode", "group" }, receipt.Items.Single(item => item.Path == "shared").NotApplied!.ToArray());
        Assert.AreEqual((UnixFileMode)RwxrXrX, File.GetUnixFileMode(Path.Combine(output, "tool")));
        Assert.AreEqual((UnixFileMode)RwxrXrX, File.GetUnixFileMode(Path.Combine(output, "shared")));
    }

    [TestMethod]
    public async Task Receipt_PermissionsOnATargetThatDoesNotApplyThem_AreNotApplied()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("mode.bin", Deterministic(1_000, 3)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, PosixMode = 0x1A4 };

        var (receipt, _) = await PublishAndRestoreAsync(
            source, 0xC3, RestoreTargetProfile.ForLocalPlatform() with { SupportsPosixMetadata = false });

        CollectionAssert.AreEqual(new[] { "posix_mode" }, Assert.ContainsSingle(receipt.Items).NotApplied!.ToArray());
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a symlink needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Receipt_ASymlink_IsCreatedWithoutItsCapturedMetadata_AndSaysSo()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("data/target.bin", Deterministic(500, 4)).Metadata = new EntryMetadata { ModifiedAt = Modified };
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "link",
            Kind = ScanEntryKind.Symlink,
            LinkTarget = "data/target.bin"u8.ToArray(),
            Metadata = new EntryMetadata { ModifiedAt = Modified, OwnerName = "ana" },
        });

        var (receipt, _) = await PublishAndRestoreAsync(source, 0xC4, Posix);

        var link = receipt.Items.Single(candidate => candidate.Path == "link");
        Assert.AreEqual("restored", link.Outcome);
        CollectionAssert.AreEqual(new[] { "modified_at", "owner" }, link.NotApplied!.ToArray());
    }

    [TestMethod]
    public async Task Plan_CapturedMetadataTheTargetWillNotApply_IsDeclaredWithCounts_AndOwnershipNamesItsPrivilege()
    {
        var source = new FakeFileSystemSource();
        foreach (var name in new[] { "a.txt", "b.txt" })
        {
            source.AddFile($"docs/{name}", Deterministic(700, 5)).Metadata = new EntryMetadata
            {
                ModifiedAt = Modified, PosixMode = 0x1A4, OwnerName = "ana", GroupName = "staff",
            };
        }

        source.AddFile("docs/tagged.txt", Deterministic(700, 6)).Metadata = new EntryMetadata
        {
            ModifiedAt = Modified,
            PosixMode = 0x1A4,
            OwnerName = "ana",
            ExtendedAttributes = [new ExtendedAttributeEntry("user.tag"u8.ToArray(), "kept"u8.ToArray())],
        };
        source.AddFile("docs/plain.txt", Deterministic(700, 7));

        var (plan, facts) = await PublishAndProbeAsync(source, 0xC5);

        var declared = RestoreMetadata.Declare(plan, facts, Posix);

        var ownership = Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "ownership"));
        Assert.Contains("3 file(s)", ownership.Detail, StringComparison.Ordinal);
        Assert.Contains("CAP_CHOWN", ownership.Detail, StringComparison.Ordinal);

        var attributes = Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "extended-attributes"));
        Assert.Contains("1 file(s)", attributes.Detail, StringComparison.Ordinal);

        // Declared only when the tree carries it, like the symlink and stream
        // declarations before it.
        Assert.DoesNotContain(degradation => degradation.Capability == "security-descriptors", declared);
        Assert.DoesNotContain(degradation => degradation.Capability == "creation-times", declared);

        // Where a target applies no POSIX metadata, the planner's own line
        // already says ownership will not be applied. It is not said twice.
        Assert.DoesNotContain(
            degradation => degradation.Capability == "ownership",
            RestoreMetadata.Declare(plan, facts, Posix with { SupportsPosixMetadata = false }));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX file's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Plan_Ownership_IsDeclaredPerFile_ByWhetherItsNamesResolve_AndTheAccountMayGiveThem()
    {
        var source = new FakeFileSystemSource();
        void Add(string name, string owner, string group, byte seed) =>
            source.AddFile($"docs/{name}", Deterministic(700, seed)).Metadata =
                new EntryMetadata { ModifiedAt = Modified, OwnerName = owner, GroupName = group };
        Add("mine.txt", "ana", "staff", 20);
        Add("theirs.txt", "ben", "staff", 21);
        Add("wheel.txt", "ana", "wheel", 22);
        Add("ghost.txt", "ghost", "staff", 23);

        var (plan, facts) = await PublishAndProbeAsync(source, 0xCF);

        // The account is ana, in staff and not in wheel; ben is another
        // account, and ghost is no account at all here.
        var account = new RestoreAccount
        {
            UserId = 1_000,
            GroupIds = new HashSet<uint> { 1_000 },
            MayGiveFilesAway = false,
            ResolveUser = name => name switch { "ana" => 1_000u, "ben" => 1_001u, _ => null },
            ResolveGroup = name => name switch { "staff" => 1_000u, "wheel" => 0u, _ => null },
        };

        var declared = RestoreMetadata.Declare(plan, facts, Posix with { Account = account });

        var privilege = Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "ownership"));
        Assert.Contains("2 file(s)", privilege.Detail, StringComparison.Ordinal);
        Assert.Contains("CAP_CHOWN", privilege.Detail, StringComparison.Ordinal);
        var unknown = Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "unknown-accounts"));
        Assert.Contains("1 file(s)", unknown.Detail, StringComparison.Ordinal);

        // An account that may give files away is told only of the name that
        // is no account here.
        var privileged = RestoreMetadata.Declare(plan, facts, Posix with { Account = account with { MayGiveFilesAway = true } });
        Assert.DoesNotContain(degradation => degradation.Capability == "ownership", privileged);
        Assert.Contains(
            "1 file(s)",
            Assert.ContainsSingle(privileged.Where(degradation => degradation.Capability == "unknown-accounts")).Detail,
            StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX file's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Plan_ASetIdBitThatWillBeDropped_IsDeclared()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("bin/theirs", Deterministic(700, 25)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, PosixMode = 0x9ED, OwnerName = "ben", GroupName = "staff" };
        source.AddFile("bin/mine", Deterministic(700, 26)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, PosixMode = 0x9ED, OwnerName = "ana", GroupName = "staff" };

        var (plan, facts) = await PublishAndProbeAsync(source, 0xD2);

        var account = new RestoreAccount
        {
            UserId = 1_000,
            GroupIds = new HashSet<uint> { 1_000 },
            MayGiveFilesAway = false,
            ResolveUser = name => name switch { "ana" => 1_000u, "ben" => 1_001u, _ => null },
            ResolveGroup = name => name == "staff" ? 1_000u : null,
        };

        // ben's file cannot be given back, so its set-user-id bit would run
        // as the restorer: it is dropped, and the plan says so first.
        var dropped = Assert.ContainsSingle(
            RestoreMetadata.Declare(plan, facts, Posix with { Account = account })
                .Where(degradation => degradation.Capability == "set-id-bits"));
        Assert.Contains("1 file(s)", dropped.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain(
            degradation => degradation.Capability == "set-id-bits",
            RestoreMetadata.Declare(plan, facts, Posix with { Account = account with { MayGiveFilesAway = true } }));
    }

    [TestMethod]
    public void Summary_TellsOnlyAnAccountThatCannotGiveFilesAwayWhatOwnershipNeeds()
    {
        ReceiptItem[] items = [new ReceiptItem { Path = "a.txt", Outcome = "restored", Bytes = 0, NotApplied = ["owner"] }];
        var account = new RestoreAccount { UserId = 1_000, GroupIds = new HashSet<uint> { 1_000 }, MayGiveFilesAway = false };

        Assert.Contains(
            "CAP_CHOWN",
            Assert.ContainsSingle(RestoreMetadata.Summarise(items, Posix with { Account = account })),
            StringComparison.Ordinal);

        // Root that could not write an owner back was not short of privilege:
        // the name resolved to nothing, or the volume refused.
        Assert.DoesNotContain(
            "CAP_CHOWN",
            Assert.ContainsSingle(RestoreMetadata.Summarise(items, Posix with { Account = account with { MayGiveFilesAway = true } })),
            StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX file's owner and group are accounts the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void Profile_ForThisMachine_KnowsTheAccountTheRestoreRunsAs()
    {
        var account = RestoreTargetProfile.ForLocalPlatform().Account;

        Assert.IsNotNull(account);
        Assert.AreEqual(PosixAccount.UserId, account.UserId);
        Assert.IsTrue(PosixAccount.Groups.SetEquals(account.GroupIds));
        Assert.AreEqual(FileOwnership.MayGiveFilesAway, account.MayGiveFilesAway);
        Assert.AreEqual(PosixAccount.UserId, account.ResolveUser(Environment.UserName));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "a Windows file's owner lives in its security descriptor")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Profile_ForWindows_HasNoAccountToGiveFilesTo() =>
        Assert.IsNull(RestoreTargetProfile.ForLocalPlatform().Account);

    [TestMethod]
    public async Task Plan_TimesAreDeclaredOnlyWhereTheTargetCannotSetThem()
    {
        var source = new FakeFileSystemSource();
        foreach (var name in new[] { "a.txt", "b.txt" })
        {
            source.AddFile($"docs/{name}", Deterministic(700, 13)).Metadata = new EntryMetadata
            {
                ModifiedAt = Modified, CreatedAt = 1_721_000_000_000, AccessedAt = 1_722_500_000_000,
            };
        }

        var (plan, facts) = await PublishAndProbeAsync(source, 0xCA);

        // Access times are written back on every target, so they are never
        // declared. Creation times are, where the target cannot set them.
        var withoutCreation = RestoreMetadata.Declare(plan, facts, Posix);
        Assert.DoesNotContain(degradation => degradation.Capability == "access-times", withoutCreation);
        var created = Assert.ContainsSingle(withoutCreation.Where(degradation => degradation.Capability == "creation-times"));
        Assert.Contains("2 file(s)", created.Detail, StringComparison.Ordinal);

        Assert.IsEmpty(RestoreMetadata.Declare(plan, facts, Posix with { SupportsCreationTimes = true }));
    }

    [TestMethod]
    public async Task Plan_ATreeCarryingOnlyWhatTheTargetApplies_DeclaresNothingMore()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.txt", Deterministic(700, 8));
        source.AddFile("docs/b.txt", Deterministic(700, 9));

        var (plan, facts) = await PublishAndProbeAsync(source, 0xC6);

        Assert.IsEmpty(RestoreMetadata.Declare(plan, facts, Posix));
    }

    [TestMethod]
    public async Task Probe_EachFile_SaysWhatItWritesAndWhatWasCaptured()
    {
        // The plan already decodes every manifest to find what the store is
        // missing, so what each file writes and carries costs it nothing more.
        const int MiB = 1024 * 1024;
        var content = new byte[4 * MiB];
        new Random(9).NextBytes(content.AsSpan(MiB, MiB));
        var source = new FakeFileSystemSource();
        var disk = source.AddFile("disk.img", content);
        source.AddNode(disk with { SparseExtents = [new SparseExtent(0, MiB), new SparseExtent(2 * MiB, 2 * MiB)] });

        var (plan, facts) = await PublishAndProbeAsync(source, 0xC7);

        var item = Assert.ContainsSingle(plan.Items);
        var known = facts[item.ObjectId];
        Assert.AreEqual(EntryKind.File, known.Kind);
        Assert.AreEqual((ulong)MiB, known.WrittenBytes);
        Assert.AreEqual(CapturedMetadata.ModifiedAt | CapturedMetadata.PosixMode, known.Captured);
    }

    [TestMethod]
    public async Task Probe_EachFile_CarriesTheNamesItsOwnershipWasCapturedUnder()
    {
        // The plan predicts ownership per file, by the names, so the probe
        // that already decodes each manifest keeps them.
        var source = new FakeFileSystemSource();
        source.AddFile("owned.txt", Deterministic(700, 24)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, OwnerName = "ana", GroupName = "staff" };

        var (plan, facts) = await PublishAndProbeAsync(source, 0xD0);

        var known = facts[Assert.ContainsSingle(plan.Items).ObjectId];
        Assert.AreEqual("ana", known.Owner);
        Assert.AreEqual("staff", known.Group);
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
    /// This machine's target, with the restoring account's names resolved
    /// as the test says, so an owner can be any account the test needs.
    /// </summary>
    private static RestoreTargetProfile Resolving(Func<string, uint?> users, Func<string, uint?> groups)
    {
        var target = RestoreTargetProfile.ForLocalPlatform();
        Assert.IsNotNull(target.Account);
        return target with { Account = target.Account with { ResolveUser = users, ResolveGroup = groups } };
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte seed) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "restore-metadata-tests/1.0",
    };

    private async Task<(LocalFileSystemObjectStore Store, RepositoryKeySet Keys, CatalogueDb Catalogue)> PublishAsync(
        FakeFileSystemSource source, byte seed)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"metadata-{seed:x2}.db"), Repo);

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
        var (store, keys, catalogue) = await PublishAsync(source, seed);
        using (keys)
        using (catalogue)
        {
            var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, target);
            using var reader = new RepositoryReader(Repo, keys, store, Authority);
            await reader.LoadBlobsAsync(CancellationToken.None);

            var output = Path.Combine(SpoolDirectory, $"restored-{seed:x2}");
            var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
                plan, output,
                new RestoreExecutionOptions
                {
                    DestinationMode = RestoreDestinationMode.InPlace,
                    RunId = "metadata",
                    NowUnixMilliseconds = 1_722_700_000_000,
                },
                CancellationToken.None);

            return (receipt, output);
        }
    }

    private async Task<(RestorePlan Plan, IReadOnlyDictionary<ObjectId, RestoreItemFacts> Facts)> PublishAndProbeAsync(
        FakeFileSystemSource source, byte seed)
    {
        var (store, keys, catalogue) = await PublishAsync(source, seed);
        using (keys)
        using (catalogue)
        {
            var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, Posix);
            var probed = await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None);
            Assert.IsEmpty(probed.Missing);
            return (plan, probed.Facts);
        }
    }
}
