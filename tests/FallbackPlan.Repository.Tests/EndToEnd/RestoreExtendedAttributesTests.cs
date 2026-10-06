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
/// A restore writes back the extended attributes captured with a file or a
/// folder it makes, each on its own, where the account and the volume allow
/// it (FR-RST-004; ADR-0087; architecture 06 §3). The plan says beforehand
/// which will not be written and why (FR-RST-003). The receipt lists an
/// item's extended attributes as not applied when any of them did not land,
/// and says which.
/// </summary>
/// <remarks>
/// <para>
/// They go on after ownership and before the permissions. Changing a file's
/// owner strips a file capability, and a captured mode that takes away the
/// owner's write permission would refuse the owner's own attributes.
/// </para>
/// <para>
/// A Linux ACL names accounts by number. A number names an account only on
/// the machine that captured it, so an ACL that names one is written back
/// only where this installation captured the snapshot; elsewhere it is
/// listed. macOS keeps no POSIX ACLs, so there none is written. Only root
/// writes Linux's security and trusted namespaces.
/// </para>
/// <para>
/// While a file has an ACL, the group bits of its mode are the ACL's mask,
/// and so is the captured mode. Wherever the ACL does not come back, those
/// bits would give the file's group what the mask allowed the accounts the
/// ACL named. The group is given only what the ACL gave it.
/// </para>
/// </remarks>
[TestClass]
public sealed class RestoreExtendedAttributesTests : ArchiveTestHarness
{
    private const ulong Modified = 1_722_000_000_000;

    /// <summary>An id no account or group is given on any machine the suite runs on.</summary>
    private const uint Unclaimed = 54_321;

    /// <summary><c>rw-r-----</c>: what a file whose ACL masks its group to reading shows.</summary>
    private const int RwRNone = 0x1A0;

    /// <summary><c>rw-rw----</c>: what a file whose ACL masks its group to reading and writing shows.</summary>
    private const int RwRwNone = 0x1B0;

    private static readonly RestoreTargetProfile Posix = new()
    {
        CaseSensitive = true,
        SupportsPosixMetadata = true,
        SupportsSymlinks = true,
        SupportsExtendedAttributes = true,
    };

    /// <summary>user::rw-, group::r--, mask::r--, other::---: an ACL that names no account.</summary>
    private static byte[] AnonymousAcl => Xattr.Acl(
        (Xattr.AclUserObject, 6, Xattr.AclUndefinedId),
        (Xattr.AclGroupObject, 4, Xattr.AclUndefinedId),
        (Xattr.AclMask, 4, Xattr.AclUndefinedId),
        (Xattr.AclOther, 0, Xattr.AclUndefinedId));

    /// <summary>The same, giving account 54321 read access by number.</summary>
    private static byte[] NumberedAcl => Xattr.Acl(
        (Xattr.AclUserObject, 6, Xattr.AclUndefinedId),
        (Xattr.AclUser, 4, Unclaimed),
        (Xattr.AclGroupObject, 4, Xattr.AclUndefinedId),
        (Xattr.AclMask, 4, Xattr.AclUndefinedId),
        (Xattr.AclOther, 0, Xattr.AclUndefinedId));

    /// <summary>
    /// user::rw-, user:54321:rw-, group::r--, mask::rw-, other::---: account
    /// 54321 may write, the group only read, and the mask is what the mode's
    /// group bits show.
    /// </summary>
    private static byte[] SharedAcl => Xattr.Acl(
        (Xattr.AclUserObject, 6, Xattr.AclUndefinedId),
        (Xattr.AclUser, 6, Unclaimed),
        (Xattr.AclGroupObject, 4, Xattr.AclUndefinedId),
        (Xattr.AclMask, 6, Xattr.AclUndefinedId),
        (Xattr.AclOther, 0, Xattr.AclUndefinedId));

    /// <summary>A file capability, version 2: bind a privileged port, effective.</summary>
    private static byte[] BindServiceCapability =>
        [0x01, 0x00, 0x00, 0x02, 0x00, 0x04, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS write a file's extended attributes")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Receipt_AFilesExtendedAttributes_ComeBack_AndAreNotListed()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/tagged.bin", Deterministic(700, 1)).Metadata = new EntryMetadata
        {
            ModifiedAt = Modified,
            ExtendedAttributes =
            [
                Attribute("user.origin", "download"u8),
                Attribute("user.tag", "kept"u8),
            ],
        };

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xA1, RestoreTargetProfile.ForLocalPlatform());

        var landed = Path.Combine(output, "docs", "tagged.bin");
        CollectionAssert.AreEqual("download"u8.ToArray(), Xattr.Get(landed, "user.origin"));
        CollectionAssert.AreEqual("kept"u8.ToArray(), Xattr.Get(landed, "user.tag"));
        var item = receipt.Items.Single(candidate => candidate.Path == "docs/tagged.bin");
        Assert.IsNull(item.NotApplied);
        Assert.IsNull(item.Detail);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS write a folder's extended attributes")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Receipt_AFoldersExtendedAttributes_ComeBack()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 2));
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = Modified, ExtendedAttributes = [Attribute("user.tag", "kept"u8)] };

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xA2, RestoreTargetProfile.ForLocalPlatform());

        CollectionAssert.AreEqual("kept"u8.ToArray(), Xattr.Get(Path.Combine(output, "docs"), "user.tag"));
        Assert.IsNull(receipt.Items.Single(item => item.Path == "docs").NotApplied);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS write a file's extended attributes")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task Receipt_ExtendedAttributesGoOnBeforePermissionsThatForbidWriting()
    {
        // r--r--r--: once these were on, the owner could no longer write its
        // own file's attributes, so the attributes go first.
        const int ReadOnly = 0x124;
        var source = new FakeFileSystemSource();
        source.AddFile("frozen.bin", Deterministic(700, 3)).Metadata = new EntryMetadata
        {
            ModifiedAt = Modified, PosixMode = ReadOnly, ExtendedAttributes = [Attribute("user.tag", "kept"u8)],
        };

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xA3, RestoreTargetProfile.ForLocalPlatform());

        var landed = Path.Combine(output, "frozen.bin");
        CollectionAssert.AreEqual("kept"u8.ToArray(), Xattr.Get(landed, "user.tag"));
        Assert.AreEqual((UnixFileMode)ReadOnly, File.GetUnixFileMode(landed));
        Assert.IsNull(Assert.ContainsSingle(receipt.Items).NotApplied);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "a POSIX ACL is an extended attribute on Linux")]
    [PlatformTrait(TestPlatforms.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Receipt_AnAclThatNamesNoAccount_ComesBack_AndThePermissionsAgreeWithIt()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("shared.bin", Deterministic(700, 4)).Metadata = new EntryMetadata
        {
            ModifiedAt = Modified, PosixMode = RwRNone, ExtendedAttributes = [Attribute("system.posix_acl_access", AnonymousAcl)],
        };

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xA4, RestoreTargetProfile.ForLocalPlatform());

        var landed = Path.Combine(output, "shared.bin");
        CollectionAssert.AreEqual(AnonymousAcl, Xattr.Get(landed, "system.posix_acl_access"));
        Assert.AreEqual((UnixFileMode)RwRNone, File.GetUnixFileMode(landed));
        Assert.IsNull(Assert.ContainsSingle(receipt.Items).NotApplied);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "a POSIX ACL is an extended attribute on Linux")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task Receipt_AnAclNamingAnAccountByNumber_ComesBackOnlyWhereThisInstallationCapturedIt()
    {
        // 54321 is whoever holds that number on the machine restoring. Only
        // on the machine that captured the snapshot is that the account the
        // ACL meant.
        var source = new FakeFileSystemSource();
        source.AddFile("shared.bin", Deterministic(700, 5)).Metadata = new EntryMetadata
        {
            ModifiedAt = Modified,
            PosixMode = RwRNone,
            ExtendedAttributes = [Attribute("user.tag", "kept"u8), Attribute("system.posix_acl_access", NumberedAcl)],
        };
        var (store, keys, catalogue) = await PublishAsync(source, 0xA5);
        using (keys)
        using (catalogue)
        {
            var elsewhere = RestoreTargetProfile.ForLocalPlatform() with { CapturedHere = false };
            var plan = RestorePlanner.Plan(catalogue, SnapshotOf(0xA5), string.Empty, elsewhere);

            var away = Path.Combine(SpoolDirectory, "acl-elsewhere");
            var item = Assert.ContainsSingle((await RestoreAsync(store, keys, plan, elsewhere, away)).Items);
            Assert.IsNull(Xattr.Get(Path.Combine(away, "shared.bin"), "system.posix_acl_access"));
            CollectionAssert.AreEqual("kept"u8.ToArray(), Xattr.Get(Path.Combine(away, "shared.bin"), "user.tag"));
            Assert.IsNotNull(item.NotApplied);
            CollectionAssert.AreEqual(new[] { "extended_attributes" }, item.NotApplied.ToArray());
            Assert.IsNotNull(item.Detail);
            Assert.Contains("system.posix_acl_access", item.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("user.tag", item.Detail, StringComparison.Ordinal);

            var here = elsewhere with { CapturedHere = true };
            var home = Path.Combine(SpoolDirectory, "acl-here");
            Assert.IsNull(Assert.ContainsSingle((await RestoreAsync(store, keys, plan, here, home)).Items).NotApplied);
            CollectionAssert.AreEqual(NumberedAcl, Xattr.Get(Path.Combine(home, "shared.bin"), "system.posix_acl_access"));
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX mode's group bits are an ACL's mask")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task Receipt_AnAclThatDoesNotComeBack_LeavesTheGroupOnlyWhatTheAclGaveIt()
    {
        // Captured rw-rw----, because the mask lets account 54321 write. The
        // group itself was given only reading. Without the ACL, rw-rw----
        // would let the whole group write.
        var source = new FakeFileSystemSource();
        source.AddFile("shared.bin", Deterministic(700, 14)).Metadata = new EntryMetadata
        {
            ModifiedAt = Modified, PosixMode = RwRwNone, ExtendedAttributes = [Attribute("system.posix_acl_access", SharedAcl)],
        };
        var (store, keys, catalogue) = await PublishAsync(source, 0xAC);
        using (keys)
        using (catalogue)
        {
            // Not this installation's capture, and a target that writes no
            // extended attributes at all; on macOS, which keeps no POSIX ACLs,
            // either.
            foreach (var (label, target) in new[]
            {
                ("elsewhere", RestoreTargetProfile.ForLocalPlatform() with { CapturedHere = false }),
                ("unwritten", RestoreTargetProfile.ForLocalPlatform() with { SupportsExtendedAttributes = false }),
            })
            {
                var plan = RestorePlanner.Plan(catalogue, SnapshotOf(0xAC), string.Empty, target);
                var output = Path.Combine(SpoolDirectory, $"narrowed-{label}");
                var item = Assert.ContainsSingle((await RestoreAsync(store, keys, plan, target, output)).Items);

                var landed = Path.Combine(output, "shared.bin");
                Assert.IsNull(Xattr.Get(landed, "system.posix_acl_access"), label);
                Assert.AreEqual((UnixFileMode)RwRNone, File.GetUnixFileMode(landed), label);
                Assert.IsNotNull(item.NotApplied, label);
                CollectionAssert.AreEqual(new[] { "posix_mode", "extended_attributes" }, item.NotApplied.ToArray(), label);
            }
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "macOS keeps no POSIX ACLs")]
    [PlatformTrait(TestPlatforms.MacOs)]
    public async Task Receipt_OnMacOs_AnAclIsListed_AndTheRestLands()
    {
        // Written by name, a Linux ACL would land on macOS as an attribute
        // that grants nothing, and the receipt would call it applied.
        var source = new FakeFileSystemSource();
        source.AddFile("shared.bin", Deterministic(700, 15)).Metadata = new EntryMetadata
        {
            ModifiedAt = Modified,
            PosixMode = RwRNone,
            ExtendedAttributes = [Attribute("system.posix_acl_access", AnonymousAcl), Attribute("user.tag", "kept"u8)],
        };

        var (receipt, output) = await PublishAndRestoreAsync(
            source, 0xAD, RestoreTargetProfile.ForLocalPlatform() with { CapturedHere = true });

        var landed = Path.Combine(output, "shared.bin");
        Assert.IsNull(Xattr.Get(landed, "system.posix_acl_access"));
        CollectionAssert.AreEqual("kept"u8.ToArray(), Xattr.Get(landed, "user.tag"));
        var item = Assert.ContainsSingle(receipt.Items);
        CollectionAssert.AreEqual(new[] { "extended_attributes" }, item.NotApplied!.ToArray());
        Assert.IsNotNull(item.Detail);
        Assert.Contains("system.posix_acl_access", item.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    [UnprivilegedPlatformCondition(TestPlatforms.Linux, "only root writes Linux's trusted namespace")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task Receipt_ANamespaceOnlyRootMayWrite_IsListed_AndTheRestStillLands()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("mixed.bin", Deterministic(700, 6)).Metadata = new EntryMetadata
        {
            ModifiedAt = Modified,
            ExtendedAttributes = [Attribute("trusted.probe", "t"u8), Attribute("user.tag", "kept"u8)],
        };

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xA6, RestoreTargetProfile.ForLocalPlatform());

        var landed = Path.Combine(output, "mixed.bin");
        CollectionAssert.AreEqual("kept"u8.ToArray(), Xattr.Get(landed, "user.tag"));
        var item = Assert.ContainsSingle(receipt.Items);
        Assert.AreEqual("restored", item.Outcome, "metadata alone does not change what the content achieved");
        CollectionAssert.AreEqual(new[] { "extended_attributes" }, item.NotApplied!.ToArray());
        Assert.IsNotNull(item.Detail);
        Assert.Contains("trusted.probe", item.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    [PrivilegedPlatformCondition(TestPlatforms.Linux, "only root could write what the rule keeps back")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task Receipt_ANamespaceOnlyRootMayWrite_IsLeftByARestoreThatIsNotRoot_AsItsPlanSaid()
    {
        // Run as root, so the rule alone keeps it back. A security policy
        // can let an account that is not root write a label its plan said
        // it would not; the receipt keeps to the plan.
        var source = new FakeFileSystemSource();
        source.AddFile("guarded.bin", Deterministic(700, 17)).Metadata = new EntryMetadata
        {
            ModifiedAt = Modified,
            ExtendedAttributes = [Attribute("trusted.probe", "t"u8), Attribute("user.tag", "kept"u8)],
        };
        var target = RestoreTargetProfile.ForLocalPlatform();
        Assert.IsNotNull(target.Account);

        var (receipt, output) = await PublishAndRestoreAsync(
            source, 0xAF, target with { Account = target.Account with { IsSuperuser = false } });

        var landed = Path.Combine(output, "guarded.bin");
        Assert.IsNull(Xattr.Get(landed, "trusted.probe"));
        CollectionAssert.AreEqual("kept"u8.ToArray(), Xattr.Get(landed, "user.tag"));
        var item = Assert.ContainsSingle(receipt.Items);
        CollectionAssert.AreEqual(new[] { "extended_attributes" }, item.NotApplied!.ToArray());
        Assert.IsNotNull(item.Detail);
        Assert.Contains("trusted.probe", item.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    [PrivilegedPlatformCondition(TestPlatforms.Linux, "only root gives a file away and writes its capabilities")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task Receipt_AFileCapability_SurvivesTheOwnershipWrite()
    {
        // Giving a file to another owner strips its capabilities, as it
        // strips a set-user-id bit, so the attributes go on after the owner.
        var source = new FakeFileSystemSource();
        source.AddFile("tool", Deterministic(700, 7)).Metadata = new EntryMetadata
        {
            ModifiedAt = Modified,
            OwnerName = "ben",
            ExtendedAttributes = [Attribute("security.capability", BindServiceCapability)],
        };

        var (receipt, output) = await PublishAndRestoreAsync(
            source, 0xA7, Resolving(user => user == "ben" ? Unclaimed : null, _ => null));

        var landed = Path.Combine(output, "tool");
        Assert.AreEqual(Unclaimed, FileOwner.Of(landed).UserId);
        CollectionAssert.AreEqual(BindServiceCapability, Xattr.Get(landed, "security.capability"));
        Assert.IsNull(Assert.ContainsSingle(receipt.Items).NotApplied);
    }

    [TestMethod]
    public async Task Receipt_OnATargetThatWritesNoExtendedAttributes_TheyAreListed()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("tagged.bin", Deterministic(700, 8)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, ExtendedAttributes = [Attribute("user.tag", "kept"u8)] };

        var (receipt, _) = await PublishAndRestoreAsync(
            source, 0xA8, RestoreTargetProfile.ForLocalPlatform() with { SupportsExtendedAttributes = false });

        CollectionAssert.AreEqual(new[] { "extended_attributes" }, Assert.ContainsSingle(receipt.Items).NotApplied!.ToArray());
    }

    [TestMethod]
    public void AnAcl_NamesAccountsByNumber_OnlyWhenAnEntryIsAnAccountOrGroup_OrItWillNotParse()
    {
        Assert.IsTrue(RestoreMetadata.NamesAccountsByNumber(Attribute("system.posix_acl_access", NumberedAcl)));
        Assert.IsTrue(RestoreMetadata.NamesAccountsByNumber(Attribute(
            "system.posix_acl_default",
            Xattr.Acl((Xattr.AclUserObject, 7, Xattr.AclUndefinedId), (Xattr.AclGroup, 5, Unclaimed), (Xattr.AclOther, 0, Xattr.AclUndefinedId)))));
        Assert.IsFalse(RestoreMetadata.NamesAccountsByNumber(Attribute("system.posix_acl_access", AnonymousAcl)));

        // Not an ACL at all, however it reads.
        Assert.IsFalse(RestoreMetadata.NamesAccountsByNumber(Attribute("user.posix_acl_access", NumberedAcl)));

        // A value that will not parse is treated as naming someone: nothing
        // vouches for what it would grant.
        Assert.IsTrue(RestoreMetadata.NamesAccountsByNumber(Attribute("system.posix_acl_access", [2, 0, 0, 0, 1, 0])));
    }

    [TestMethod]
    public async Task Probe_EachItem_SaysWhichKindsOfExtendedAttributeItCarries()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("plain.bin", Deterministic(700, 9)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, ExtendedAttributes = [Attribute("user.tag", "kept"u8)] };
        source.AddFile("guarded.bin", Deterministic(700, 10)).Metadata = new EntryMetadata
        {
            ModifiedAt = Modified,
            ExtendedAttributes = [Attribute("security.selinux", "label"u8), Attribute("system.posix_acl_access", NumberedAcl)],
        };

        var (plan, probed) = await PublishAndProbeAsync(source, 0xA9);

        Assert.AreEqual(ExtendedAttributeKinds.None, probed.Facts[ObjectIdOf(plan, "plain.bin")].Attributes);
        Assert.AreEqual(
            ExtendedAttributeKinds.Privileged | ExtendedAttributeKinds.NumberedAcl,
            probed.Facts[ObjectIdOf(plan, "guarded.bin")].Attributes);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "a POSIX ACL is an extended attribute on Linux")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task Plan_AnAclNamingAccountsByNumber_IsDeclared_WhereThisInstallationDidNotCaptureIt()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/shared.bin", Deterministic(700, 11)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, ExtendedAttributes = [Attribute("system.posix_acl_access", NumberedAcl)] };
        source.Folders["docs"] = new EntryMetadata
        {
            ModifiedAt = Modified, ExtendedAttributes = [Attribute("system.posix_acl_default", NumberedAcl)],
        };
        source.AddFile("docs/plain.bin", Deterministic(700, 12)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, ExtendedAttributes = [Attribute("user.tag", "kept"u8)] };

        var (plan, probed) = await PublishAndProbeAsync(source, 0xAA);

        var elsewhere = RestoreMetadata.Declare(plan, probed.Facts, Posix);
        var numbered = Assert.ContainsSingle(elsewhere.Where(degradation => degradation.Capability == "numbered-acls"));
        Assert.Contains("1 file(s) and 1 folder(s)", numbered.Detail, StringComparison.Ordinal);

        // A target that writes extended attributes is not told it writes none.
        Assert.DoesNotContain(degradation => degradation.Capability == "extended-attributes", elsewhere);

        Assert.DoesNotContain(
            degradation => degradation.Capability == "numbered-acls",
            RestoreMetadata.Declare(plan, probed.Facts, Posix with { CapturedHere = true }));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "macOS keeps no POSIX ACLs")]
    [PlatformTrait(TestPlatforms.MacOs)]
    public async Task Plan_OnMacOs_EveryAcl_IsDeclared_WhoeverCapturedIt()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/shared.bin", Deterministic(700, 16)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, ExtendedAttributes = [Attribute("system.posix_acl_access", AnonymousAcl)] };
        source.Folders["docs"] = new EntryMetadata
        {
            ModifiedAt = Modified, ExtendedAttributes = [Attribute("system.posix_acl_default", NumberedAcl)],
        };

        var (plan, probed) = await PublishAndProbeAsync(source, 0xAE);

        var declared = RestoreMetadata.Declare(plan, probed.Facts, Posix with { CapturedHere = true });
        var acls = Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "posix-acls"));
        Assert.Contains("1 file(s) and 1 folder(s)", acls.Detail, StringComparison.Ordinal);

        // Said once: an ACL that names an account is among them.
        Assert.DoesNotContain(
            degradation => degradation.Capability == "numbered-acls",
            RestoreMetadata.Declare(plan, probed.Facts, Posix));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "only Linux keeps namespaces root alone may write")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task Plan_ANamespaceOnlyRootMayWrite_IsDeclaredToAnAccountThatIsNotRoot()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("guarded.bin", Deterministic(700, 13)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, ExtendedAttributes = [Attribute("trusted.probe", "t"u8)] };

        var (plan, probed) = await PublishAndProbeAsync(source, 0xAB);

        var account = new RestoreAccount { UserId = 1_000, GroupIds = new HashSet<uint> { 1_000 }, MayGiveFilesAway = false };
        var declared = RestoreMetadata.Declare(plan, probed.Facts, Posix with { Account = account });
        var privileged = Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "privileged-attributes"));
        Assert.Contains("1 file(s)", privileged.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain(
            degradation => degradation.Capability == "privileged-attributes",
            RestoreMetadata.Declare(plan, probed.Facts, Posix with { Account = account with { IsSuperuser = true } }));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a POSIX account is one the machine names")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void Profile_ForThisMachine_WritesExtendedAttributes_AndKnowsWhetherTheAccountIsRoot()
    {
        var target = RestoreTargetProfile.ForLocalPlatform();

        Assert.IsTrue(target.SupportsExtendedAttributes);
        Assert.IsFalse(target.CapturedHere, "only a caller that knows who captured the snapshot may say this installation did");
        Assert.IsNotNull(target.Account);
        Assert.AreEqual(Environment.IsPrivilegedProcess, target.Account.IsSuperuser);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "Windows keeps no extended attributes a restore writes")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Profile_ForWindows_WritesNoExtendedAttributes() =>
        Assert.IsFalse(RestoreTargetProfile.ForLocalPlatform().SupportsExtendedAttributes);

    private static ExtendedAttributeEntry Attribute(string name, ReadOnlySpan<byte> value) =>
        new(System.Text.Encoding.UTF8.GetBytes(name), value.ToArray());

    private static ObjectId ObjectIdOf(RestorePlan plan, string path) => plan.Items.Single(item => item.Path == path).ObjectId;

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

    /// <summary>This machine's target, with the restoring account's names resolved as the test says.</summary>
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
        SnapshotId = SnapshotOf(seed),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "restore-attribute-tests/1.0",
    };

    private async Task<(LocalFileSystemObjectStore Store, RepositoryKeySet Keys, CatalogueDb Catalogue)> PublishAsync(
        FakeFileSystemSource source, byte seed)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"attributes-{seed:x2}.db"), Repo);

        await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, $"sequence-{seed:x2}.txt"))),
                SpoolDirectory, FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(Job(source, seed), CancellationToken.None);

        return (store, keys, catalogue);
    }

    private static async Task<RestoreReceipt> RestoreAsync(
        LocalFileSystemObjectStore store, RepositoryKeySet keys, RestorePlan plan, RestoreTargetProfile target, string output)
    {
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);

        return await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "attributes",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);
    }

    private async Task<(RestoreReceipt Receipt, string Output)> PublishAndRestoreAsync(
        FakeFileSystemSource source, byte seed, RestoreTargetProfile target)
    {
        var (store, keys, catalogue) = await PublishAsync(source, seed);
        using (keys)
        using (catalogue)
        {
            var output = Path.Combine(SpoolDirectory, $"restored-{seed:x2}");
            var plan = RestorePlanner.Plan(catalogue, SnapshotOf(seed), string.Empty, target);
            return (await RestoreAsync(store, keys, plan, target, output), output);
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
}
