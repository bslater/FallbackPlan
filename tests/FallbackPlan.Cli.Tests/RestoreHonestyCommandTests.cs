using System.Runtime.Versioning;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The restore verb tells the truth about the disk it writes to and the
/// metadata it will not apply (FR-RST-003, FR-RST-004; ADR-0083). In direct
/// mode the CLI runs the restore itself, so it is the CLI that refuses a run
/// that will not fit before it writes anything, and the CLI that says what
/// was not applied once it ends. On Windows it is the CLI, too, that refuses
/// a run whose folder would open a file wider than it was, unless told to go
/// on (ADR-0178 Amendment 1).
/// </summary>
/// <remarks>
/// The disk is the flow's own: <c>DirectGateway.AvailableBytesInFlow</c>
/// answers for every directory this test's restore asks about, and no other
/// test's.
/// </remarks>
[TestClass]
public sealed class RestoreHonestyCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    [TestMethod]
    public async Task Restore_ThatWillNotFit_IsRefused_AndWritesNothing()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");

        DirectGateway.AvailableBytesInFlow = _ => 1_000;
        var restore = await _cli.RunAsync("restore", snapshot, "--output", destination);

        Assert.AreNotEqual(0, restore.ExitCode, restore.All);
        Assert.Contains(destination, restore.All, StringComparison.Ordinal);
        Assert.Contains("1000", restore.All, StringComparison.Ordinal);
        Assert.Contains("--ignore-free-space", restore.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(destination), "a refused restore must not even create its folder");
    }

    [TestMethod]
    public async Task Restore_ToldToIgnoreFreeSpace_RestoresAnyway()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");

        DirectGateway.AvailableBytesInFlow = _ => 1_000;
        var restore = await _cli.RunAsync("restore", snapshot, "--output", destination, "--ignore-free-space");

        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        Assert.AreEqual("first", await File.ReadAllTextAsync(Path.Combine(destination, "one.txt")));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's DACL")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Plan_OnWindows_NamesAFileItsFolderWouldOpenWider_WhereItsDescriptorNamesAnAccountTheMachineDoesNotKnow()
    {
        const string Stranger = "S-1-5-21-9-9-9-4242";
        const string Users = "S-1-5-32-545";
        await _cli.InitAsync();
        _cli.WriteFile("tree/private.txt", "kept to two accounts");
        WindowsAcls.KeepTo(Path.Combine(_cli.WorkPath, "tree", "private.txt"), Stranger);
        var snapshot = await BackUpTreeAsync();
        var destination = WindowsAcls.Sharing(Path.Combine(_cli.WorkPath, "shared"), Users);

        var plan = await _cli.RunAsync("restore", snapshot, "--output", destination, "--plan");

        // The run it plans would be refused (ADR-0178 Amendment 1), so the
        // plan is not a success, and says how to go on.
        Assert.AreNotEqual(0, plan.ExitCode, plan.All);
        Assert.Contains("access their own did not", plan.Output, StringComparison.Ordinal);
        Assert.Contains(Users, plan.Output, StringComparison.Ordinal);
        Assert.Contains("--acknowledge-wider-access", plan.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's DACL")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Restore_OnWindows_IntoAFolderThatWouldOpenAFileWider_IsRefused_AndWritesNothing_UnlessAcknowledged()
    {
        const string Stranger = "S-1-5-21-9-9-9-4242";
        const string Users = "S-1-5-32-545";
        await _cli.InitAsync();
        _cli.WriteFile("tree/private.txt", "kept to two accounts");
        WindowsAcls.KeepTo(Path.Combine(_cli.WorkPath, "tree", "private.txt"), Stranger);
        var snapshot = await BackUpTreeAsync();
        var destination = WindowsAcls.Sharing(Path.Combine(_cli.WorkPath, "shared"), Users);

        var refused = await _cli.RunAsync("restore", snapshot, "--output", destination);

        Assert.AreNotEqual(0, refused.ExitCode, refused.All);
        Assert.Contains(Users, refused.All, StringComparison.Ordinal);
        Assert.Contains("--acknowledge-wider-access", refused.All, StringComparison.Ordinal);
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(destination), "a refused restore writes nothing");

        var restored = await _cli.RunAsync("restore", snapshot, "--output", destination, "--acknowledge-wider-access");

        Assert.IsTrue(restored.ExitCode == 0, restored.All);
        Assert.AreEqual("kept to two accounts", await File.ReadAllTextAsync(Path.Combine(destination, "private.txt")));
    }

    [TestMethod]
    public async Task Restore_SaysWhichCapturedMetadataItDidNotApply()
    {
        var snapshot = await BackUpAsync(withLink: !OperatingSystem.IsWindows());

        var restore = await _cli.RunAsync("restore", snapshot, "--output", Path.Combine(_cli.WorkPath, "restored"));

        // On Windows the attribute bits and the security descriptors of both
        // files and of the folder one of them is in come back (ADR-0175,
        // ADR-0176), so neither is listed. On a POSIX host the
        // files, their folder and the link are the account's own and get
        // their owner back (ADR-0085, ADR-0086, ADR-0101), and the link its
        // own times, so neither is listed. Metadata alone does not fail the
        // restore.
        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("file_attributes not applied", restore.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("security_descriptor not applied", restore.Output, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("owner not applied", restore.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("modified_at not applied", restore.Output, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("accessed_at not applied", restore.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS write a file's extended attributes")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Restore_GivesARealFileItsExtendedAttributesBack_AndItsAclWhereThisInstallationCapturedIt()
    {
        // In direct mode the CLI runs the restore itself, so it is the CLI
        // that knows this installation captured the snapshot and an ACL's
        // account numbers mean here what they meant then (ADR-0087).
        var snapshot = await BackUpAsync(tagged: true, acl: OperatingSystem.IsLinux() ? NumberedAcl : null);
        var destination = Path.Combine(_cli.WorkPath, "restored");

        var restore = await _cli.RunAsync("restore", snapshot, "--output", destination);

        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        var one = Path.Combine(destination, "one.txt");
        CollectionAssert.AreEqual("kept"u8.ToArray(), Xattr.Get(one, "user.tag"));
        if (OperatingSystem.IsLinux())
        {
            CollectionAssert.AreEqual(NumberedAcl, Xattr.Get(one, "system.posix_acl_access"));
        }
    }

    public void Dispose() => _cli.Dispose();

    /// <summary>user::rw-, user:54321:r--, group::r--, mask::r--, other::---: an ACL naming an account by number.</summary>
    private static byte[] NumberedAcl => Xattr.Acl(
        (Xattr.AclUserObject, 6, Xattr.AclUndefinedId),
        (Xattr.AclUser, 4, 54_321),
        (Xattr.AclGroupObject, 4, Xattr.AclUndefinedId),
        (Xattr.AclMask, 4, Xattr.AclUndefinedId),
        (Xattr.AclOther, 0, Xattr.AclUndefinedId));

    private async Task<string> BackUpAsync(bool withLink = false, bool tagged = false, byte[]? acl = null)
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/one.txt", "first");
        _cli.WriteFile("tree/nested/two.txt", "second");
        if (withLink)
        {
            File.CreateSymbolicLink(Path.Combine(_cli.WorkPath, "tree", "link"), "one.txt");
        }

        if (tagged)
        {
            Xattr.Set(Path.Combine(_cli.WorkPath, "tree", "one.txt"), "user.tag", "kept"u8.ToArray());
        }

        if (acl is not null)
        {
            Xattr.Set(Path.Combine(_cli.WorkPath, "tree", "one.txt"), "system.posix_acl_access", acl);
        }

        return await BackUpTreeAsync();
    }

    private async Task<string> BackUpTreeAsync()
    {
        var backup = await _cli.RunAsync("backup", Path.Combine(_cli.WorkPath, "tree"));
        Assert.IsTrue(backup.ExitCode == 0, backup.All);

        var snapshots = await _cli.RunAsync("snapshots");
        Assert.IsTrue(snapshots.ExitCode == 0, snapshots.All);

        // The identifier is the leading hex token of the listing line.
        var first = snapshots.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        Assert.IsNotNull(first);
        return first.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }
}
