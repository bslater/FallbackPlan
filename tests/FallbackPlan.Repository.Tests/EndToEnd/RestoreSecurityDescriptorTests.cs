using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A restore gives a file and a folder their Windows security descriptors
/// back where the target keeps them (FR-RST-004; ADR-0176): owner, group and
/// DACL as captured, protected as they were, after every other write, where
/// every account the descriptor names is one the target knows. The plan and
/// the receipt no longer list them there. The profile says whether its
/// account holds SeRestorePrivilege, which the writer enables to give an
/// owner the account is not (ADR-0176 Amendment 1), and which owners its
/// account may give without it, by which the plan predicts (ADR-0176
/// Amendment 2).
/// </summary>
[TestClass]
public sealed class RestoreSecurityDescriptorTests : ArchiveTestHarness
{
    private readonly string _sourceRoot =
        Path.Combine(Path.GetTempPath(), "fbp-security-descriptor-restore-tests", Guid.NewGuid().ToString("n"));

    [TestMethod]
    public void Profile_ForThisPlatform_WritesDescriptorsOnlyOnWindows_AndPassesThemAndItsAccountsToTheWriter()
    {
        var target = RestoreTargetProfile.ForLocalPlatform();
        Func<string, bool> knows = account => account == "S-1-5-18";

        Assert.AreEqual(OperatingSystem.IsWindows(), target.SupportsSecurityDescriptors);
        Assert.IsTrue((target with { SupportsSecurityDescriptors = true }).Metadata.SupportsSecurityDescriptors);
        Assert.IsFalse((target with { SupportsSecurityDescriptors = false }).Metadata.SupportsSecurityDescriptors);
        Assert.AreSame(knows, (target with { KnowsAccount = knows }).Metadata.KnowsAccount);
        foreach (var kind in new[] { EntryKind.File, EntryKind.DirectoryPlaceholder })
        {
            Assert.IsTrue(RestoreMetadata.Applied(kind, target with { SupportsSecurityDescriptors = true }).HasFlag(CapturedMetadata.SecurityDescriptor));
            Assert.IsFalse(RestoreMetadata.Applied(kind, target with { SupportsSecurityDescriptors = false }).HasFlag(CapturedMetadata.SecurityDescriptor));
        }
    }

    [TestMethod]
    public void Profile_ForThisPlatform_HoldsTheRestorePrivilege_OnlyWhereItsAccountDoes_AndPassesItToTheWriter()
    {
        var target = RestoreTargetProfile.ForLocalPlatform();

        Assert.AreEqual(WindowsPrivileges.Holds(WindowsPrivileges.Restore), target.HoldsRestorePrivilege);
        Assert.IsTrue((target with { HoldsRestorePrivilege = true }).Metadata.HoldsRestorePrivilege);
        Assert.IsFalse((target with { HoldsRestorePrivilege = false }).Metadata.HoldsRestorePrivilege);
        if (!OperatingSystem.IsWindows())
        {
            Assert.IsFalse(target.HoldsRestorePrivilege);
        }
    }

    [TestMethod]
    public void Profile_ForThisPlatform_AsksItsOwnAccountWhichOwnersItMayGive()
    {
        var target = RestoreTargetProfile.ForLocalPlatform();
        Func<string, bool> mayOwn = account => account == "S-1-5-18";

        Assert.IsFalse(target.MayOwnBy("S-1-5-21-1-2-3-4242"));
        Assert.IsFalse(target.MayOwnBy("S-1-5-19"));
        Assert.AreSame(mayOwn, (target with { MayOwnBy = mayOwn }).MayOwnBy);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows names an account by SID")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void Profile_OnWindows_MayGiveItsOwnAccountAsOwner()
    {
        using var self = WindowsIdentity.GetCurrent();

        Assert.IsTrue(RestoreTargetProfile.ForLocalPlatform().MayOwnBy(self.User!.Value));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's security descriptor")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task OnWindows_AFileAndItsFolder_GetTheirDaclsBack_ThroughTheExecutor_AndNothingIsListed()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_sourceRoot, "data"));
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var shared = folder.GetAccessControl();
        shared.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        folder.SetAccessControl(shared);

        var file = new FileInfo(Path.Combine(folder.FullName, "kept.bin"));
        File.WriteAllText(file.FullName, "kept with its own access");
        var modified = new DateTime(2024, 7, 27, 8, 45, 0, DateTimeKind.Utc);
        var own = file.GetAccessControl();
        own.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        own.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        own.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(own);
        File.SetLastWriteTimeUtc(file.FullName, modified);

        var (store, keys, catalogue) = await PublishAsync(0xD6);
        using var _ = keys;
        using var db = catalogue;
        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(db, Enumerable.Repeat((byte)0xD6, 16).ToArray(), string.Empty, target);
        var probed = await RestoreBlobSet.ResolveAsync(db, plan, store, Repo, keys, CancellationToken.None);
        Assert.DoesNotContain(
            degradation => degradation.Capability.StartsWith("security-descriptor", StringComparison.Ordinal),
            RestoreMetadata.Declare(plan, probed.Facts, target));

        // The account captured these, so it may own by every owner they name
        // without the privilege.
        Assert.DoesNotContain(
            degradation => degradation.Capability.StartsWith("security-descriptor", StringComparison.Ordinal),
            RestoreMetadata.Declare(plan, probed.Facts, target with { HoldsRestorePrivilege = false }));

        var output = Path.Combine(SpoolDirectory, "descriptors-out");
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "descriptors",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        var landed = new FileInfo(Path.Combine(output, "data", "kept.bin"));
        Assert.AreEqual(Dacl(file), Dacl(landed));
        Assert.IsTrue(landed.GetAccessControl().AreAccessRulesProtected);
        Assert.AreEqual(Dacl(folder), Dacl(new DirectoryInfo(Path.Combine(output, "data"))));
        Assert.AreEqual(modified, File.GetLastWriteTimeUtc(landed.FullName));
        Assert.AreEqual("kept with its own access", File.ReadAllText(landed.FullName));
        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.IsTrue(receipt.Items.All(item => !(item.NotApplied?.Contains("security_descriptor") ?? false)));
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(_sourceRoot))
        {
            Directory.Delete(_sourceRoot, recursive: true);
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// An entry's DACL: its protection and its entries, in order, each with
    /// whether its folder gave it. The control bit that marks inherited
    /// entries as given by automatic inheritance is not kept by the call a
    /// restore writes a descriptor with (ADR-0176), so it is not compared.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string Dacl(FileSystemInfo entry)
    {
        var security = entry is DirectoryInfo folder
            ? (FileSystemSecurity)folder.GetAccessControl(AccessControlSections.Access)
            : ((FileInfo)entry).GetAccessControl(AccessControlSections.Access);
        return (security.AreAccessRulesProtected ? "protected: " : "inheriting: ")
            + string.Join("; ", security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Select(rule => $"{rule.AccessControlType} {rule.IdentityReference} {rule.FileSystemRights} "
                    + $"{rule.InheritanceFlags} {rule.PropagationFlags}{(rule.IsInherited ? " inherited" : string.Empty)}"));
    }

    private async Task<(LocalFileSystemObjectStore Store, RepositoryKeySet Keys, CatalogueDb Catalogue)> PublishAsync(byte seed)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"descriptors-{seed:x2}.db"), Repo);
        var spool = Path.Combine(SpoolDirectory, $"spool-{seed:x2}");
        Directory.CreateDirectory(spool);

        var result = await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                spool, FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = new LocalFileSystemSource(),
                    Roots = [new ScanRoot(_sourceRoot)],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "restore-security-descriptor-tests/1.0",
                },
                CancellationToken.None);
        Assert.IsEmpty(result.Failures);
        return (store, keys, catalogue);
    }
}
