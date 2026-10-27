using System.Text.Json;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A restore commanded through the contract tells the truth about the disk it
/// writes to, what it will read, and the metadata it will not apply
/// (FR-RST-003, FR-RST-004; ADR-0083). Given the run's shape, the plan measures the volume the run
/// would write to against what is free there, and says when it will not fit.
/// The run refuses before it writes anything, unless it is told to restore
/// anyway. Both say which captured metadata will not be applied. The plan
/// gives counts; the result summarises the receipt, which names it per item.
/// The account running these tests restores its own files, so on a POSIX
/// host it gives them back their owner and group (ADR-0085), and a symlink
/// its own metadata, onto the link itself (ADR-0101). What is left to say
/// there is a creation time Linux cannot set.
/// </summary>
[TestClass]
public sealed class RestoreHonestyServiceTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    [TestMethod]
    public async Task Plan_NamingAFolderThatWillNotHoldTheRestore_SaysSoBeforeAnythingMoves()
    {
        await BackUpTwoFilesAsync();
        await using var runtime = await StartAsync(availableBytes: 1_000);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var snapshotId = await SnapshotIdAsync(handler);
        var destination = Path.Combine(_harness.WorkPath, "restored");

        Assert.IsInstanceOfType<RestorePlanResult>(
            await handler.ExecuteAsync(
                new PlanRestoreCommand(snapshotId, null, OutputDirectory: destination, Source: (await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token)).SourceId),
                _timeout.Token),
            out var plan);

        // One volume holds the folder and the engine's working directory, so
        // the need is one figure.
        Assert.IsNotNull(plan.Space);
        var space = Assert.ContainsSingle(plan.Space);
        Assert.AreEqual(destination, space.Directory);
        Assert.AreEqual(1_000L, space.AvailableBytes);
        Assert.IsGreaterThan(1_000L, space.NeededBytes);
        Assert.IsFalse(space.Working);

        // Said where a client that predates the figure already looks, too.
        Assert.IsGreaterThanOrEqualTo(1L, plan.Conflicts);
        Assert.IsNotNull(plan.ConflictSample);
        Assert.Contains(
            line => line.Contains(destination, StringComparison.Ordinal) && line.Contains("1000", StringComparison.Ordinal),
            plan.ConflictSample);

        Assert.IsFalse(Directory.Exists(destination), "a plan writes nothing");
    }

    [TestMethod]
    public async Task Run_ThatWillNotFit_IsRefused_AndWritesNothingAtAll()
    {
        await BackUpTwoFilesAsync();
        await using var runtime = await StartAsync(availableBytes: 1_000);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var snapshotId = await SnapshotIdAsync(handler);
        var destination = Path.Combine(_harness.WorkPath, "restored");
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(snapshotId, null, destination, Source: source.SourceId), _timeout.Token),
            out var refused);

        // A rule, not a failure: nothing ran.
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains(destination, refused.Message, StringComparison.Ordinal);
        Assert.Contains("1000", refused.Message, StringComparison.Ordinal);

        Assert.IsFalse(Directory.Exists(destination), "a refused restore must not even create its folder");
        Assert.IsFalse(
            Directory.Exists(runtime.ReceiptsRoot) && Directory.EnumerateFileSystemEntries(runtime.ReceiptsRoot).Any(),
            "a restore that never ran has no receipt");
    }

    [TestMethod]
    public async Task Run_ToldToIgnoreFreeSpace_RestoresAnyway()
    {
        // The estimate counts what a restore writes, and a volume that
        // compresses what it stores holds more than that. A person who knows
        // theirs does must be able to proceed.
        await BackUpTwoFilesAsync();
        await using var runtime = await StartAsync(availableBytes: 1_000);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var snapshotId = await SnapshotIdAsync(handler);
        var destination = Path.Combine(_harness.WorkPath, "restored");
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(snapshotId, null, destination, Source: source.SourceId, IgnoreFreeSpace: true),
                _timeout.Token),
            out var restored);

        Assert.AreEqual("complete", restored.Outcome);
        Assert.AreEqual(2, restored.Restored);
        Assert.AreEqual(
            "hello",
            await File.ReadAllTextAsync(Path.Combine(restored.OutputDirectory, "notes.txt"), _timeout.Token));
    }

    [TestMethod]
    public async Task Run_WithRoom_SummarisesWhatItDidNotApply_AndTheReceiptNamesItPerItem()
    {
        await BackUpTwoFilesAsync(withLink: !OperatingSystem.IsWindows());
        await using var runtime = await StartAsync(availableBytes: null);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var snapshotId = await SnapshotIdAsync(handler);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshotId, null, Path.Combine(_harness.WorkPath, "restored"), Source: source.SourceId),
                _timeout.Token),
            out var restored);

        Assert.AreEqual("complete", restored.Outcome, "metadata alone does not change what a restore achieved");

        // What no target here writes back: on Windows the attribute bits of
        // every file and of the folder they share. On a POSIX host every item
        // is the account's own and gets its owner back, the link included,
        // and every item its access time, the link onto itself (ADR-0101).
        // Linux sets no creation time, so where the volume recorded one,
        // that is what each item lists.
        var summary = restored.NotApplied ?? [];
        Assert.DoesNotContain(line => line.StartsWith("accessed_at", StringComparison.Ordinal), summary);

        var receipt = NotAppliedByPath(await File.ReadAllTextAsync(restored.ReceiptPath!, _timeout.Token));
        var notes = receipt.GetValueOrDefault(ItemEndingIn(receipt, "notes.txt")) ?? [];
        Assert.DoesNotContain("accessed_at", notes);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(line => line.StartsWith("file_attributes not applied to 3 item(s)", StringComparison.Ordinal), summary);
            Assert.Contains("file_attributes", notes);
        }
        else
        {
            var link = receipt.GetValueOrDefault(ItemEndingIn(receipt, "link")) ?? [];
            CollectionAssert.IsSubsetOf(link, new[] { "created_at" }, "the link lists more than a creation time Linux cannot set");
            Assert.DoesNotContain("owner", notes);
            Assert.DoesNotContain("group", notes);
            if (OperatingSystem.IsLinux() && LinkStatus.Of(Path.Combine(_harness.SourceRoot, "link")).CreatedUtc is not null)
            {
                Assert.Contains("created_at", link);
                Assert.Contains(line => line.StartsWith("created_at not applied to", StringComparison.Ordinal), summary);
            }
        }
    }

    [TestMethod]
    public async Task Run_GivesARestoredFolderItsOwnModificationTimeBack()
    {
        // Captured from a real folder and written back onto the one the
        // restore made, once the file inside it had landed (ADR-0086).
        var modified = new DateTime(2020, 9, 13, 12, 26, 40, DateTimeKind.Utc);
        await BackUpTwoFilesAsync(nestedModified: modified);
        await using var runtime = await StartAsync(availableBytes: null);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var snapshotId = await SnapshotIdAsync(handler);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshotId, null, Path.Combine(_harness.WorkPath, "restored"), Source: source.SourceId),
                _timeout.Token),
            out var restored);

        Assert.AreEqual("complete", restored.Outcome);
        Assert.AreEqual(modified, Directory.GetLastWriteTimeUtc(Path.Combine(restored.OutputDirectory, "nested")));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS write a file's extended attributes")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Run_GivesARealFileItsExtendedAttributesBack_AndItsAclWhereThisInstallationCapturedIt()
    {
        // Captured from a real file and written back through the service
        // (ADR-0087). On Linux the file also carries an ACL naming an account
        // by number, which comes back because this installation captured it.
        await BackUpTwoFilesAsync(tagged: true, acl: OperatingSystem.IsLinux() ? NumberedAcl : null);
        await using var runtime = await StartAsync(availableBytes: null);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var snapshotId = await SnapshotIdAsync(handler);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshotId, null, Path.Combine(_harness.WorkPath, "restored"), Source: source.SourceId),
                _timeout.Token),
            out var restored);

        Assert.AreEqual("complete", restored.Outcome);
        var notes = Path.Combine(restored.OutputDirectory, "notes.txt");
        CollectionAssert.AreEqual("kept"u8.ToArray(), Xattr.Get(notes, "user.tag"));
        if (OperatingSystem.IsLinux())
        {
            CollectionAssert.AreEqual(NumberedAcl, Xattr.Get(notes, "system.posix_acl_access"));
        }
    }

    [TestMethod]
    public async Task Plan_OverRealFiles_DeclaresTheCapturedMetadataItWillNotApply_WithCounts()
    {
        await BackUpTwoFilesAsync(withLink: !OperatingSystem.IsWindows());
        await using var runtime = await StartAsync(availableBytes: null);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var snapshotId = await SnapshotIdAsync(handler);

        Assert.IsInstanceOfType<RestorePlanResult>(
            await handler.ExecuteAsync(
                new PlanRestoreCommand(snapshotId, null, Source: (await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token)).SourceId), _timeout.Token),
            out var plan);

        // On macOS every file, folder and link here gets all it carries back,
        // so the plan may declare nothing at all.
        var declared = plan.Degradations ?? [];
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(
                line => line.Contains("File attributes", StringComparison.Ordinal) && line.Contains("2 file(s)", StringComparison.Ordinal),
                declared);
        }
        else
        {
            // A link gets its own metadata back, so no line speaks for links
            // as such. Linux sets no creation time, so where the volume
            // recorded the link's, it is counted with the files'.
            Assert.DoesNotContain(line => line.Contains("Metadata captured on", StringComparison.Ordinal), declared);
            if (OperatingSystem.IsLinux() && LinkStatus.Of(Path.Combine(_harness.SourceRoot, "link")).CreatedUtc is not null)
            {
                Assert.Contains(
                    line => line.Contains("Creation times", StringComparison.Ordinal) && line.Contains("1 symlink(s)", StringComparison.Ordinal),
                    declared);
            }
            else
            {
                Assert.DoesNotContain(line => line.Contains("symlink(s)", StringComparison.Ordinal), declared);
            }
        }

        Assert.DoesNotContain(line => line.Contains("Access times", StringComparison.Ordinal), declared);

        // The files are the restoring account's own, so their ownership is
        // given back and not declared.
        Assert.DoesNotContain(line => line.Contains("Ownership", StringComparison.Ordinal), declared);

        // With no folder named there is nothing to measure against, so the
        // plan answers as it always did, plus what its files will write.
        Assert.IsNull(plan.Space);
        Assert.AreEqual(plan.Bytes, plan.WriteBytes);
    }

    [TestMethod]
    public async Task Plan_SaysWhatTheRunWillRead_WhichForContentThatCompressesIsLessThanItWrites()
    {
        // On a store that bills for what is read, this is the figure a
        // person is paying for, and it is not the one the files write.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", string.Concat(Enumerable.Repeat("the same line, over and over\n", 40_000)));
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartAsync(availableBytes: null);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var snapshotId = await SnapshotIdAsync(handler);

        Assert.IsInstanceOfType<RestorePlanResult>(
            await handler.ExecuteAsync(
                new PlanRestoreCommand(snapshotId, null, Source: (await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token)).SourceId), _timeout.Token),
            out var plan);

        Assert.IsNotNull(plan.ReadBytes);
        Assert.IsGreaterThan(0L, plan.ReadBytes.Value);
        Assert.IsLessThan(plan.Bytes / 4, plan.ReadBytes.Value);
    }

    /// <summary>user::rw-, user:54321:r--, group::r--, mask::r--, other::---: an ACL naming an account by number.</summary>
    private static byte[] NumberedAcl => Xattr.Acl(
        (Xattr.AclUserObject, 6, Xattr.AclUndefinedId),
        (Xattr.AclUser, 4, 54_321),
        (Xattr.AclGroupObject, 4, Xattr.AclUndefinedId),
        (Xattr.AclMask, 4, Xattr.AclUndefinedId),
        (Xattr.AclOther, 0, Xattr.AclUndefinedId));

    /// <summary>What no target here writes back, and how many items it is left off: two files and their folder on Windows.</summary>
    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    /// <summary>Each item's not_applied in a receipt, by its path; an item that lists nothing is absent.</summary>
    private static Dictionary<string, string[]> NotAppliedByPath(string receipt)
    {
        using var document = JsonDocument.Parse(receipt);
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Where(item => item.TryGetProperty("not_applied", out _))
            .ToDictionary(
                item => item.GetProperty("path").GetString()!,
                item => item.GetProperty("not_applied").EnumerateArray().Select(name => name.GetString()!).ToArray(),
                StringComparer.Ordinal);
    }

    private static string ItemEndingIn(Dictionary<string, string[]> receipt, string name) =>
        receipt.Keys.SingleOrDefault(path => path.EndsWith(name, StringComparison.Ordinal)) ?? name;

    private async Task BackUpTwoFilesAsync(
        bool withLink = false, DateTime? nestedModified = null, bool tagged = false, byte[]? acl = null)
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteSourceFile("nested/deeper.txt", "deeper");
        if (withLink)
        {
            File.CreateSymbolicLink(Path.Combine(_harness.SourceRoot, "link"), "notes.txt");
        }

        if (tagged)
        {
            Xattr.Set(Path.Combine(_harness.SourceRoot, "notes.txt"), "user.tag", "kept"u8.ToArray());
        }

        if (acl is not null)
        {
            Xattr.Set(Path.Combine(_harness.SourceRoot, "notes.txt"), "system.posix_acl_access", acl);
        }

        if (nestedModified is { } modified)
        {
            Directory.SetLastWriteTimeUtc(Path.Combine(_harness.SourceRoot, "nested"), modified);
        }

        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");
    }

    private async Task<string> SnapshotIdAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        return Assert.ContainsSingle(snapshots.Snapshots).SnapshotId;
    }

    private async Task<ServiceRuntime> StartAsync(long? availableBytes)
    {
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                // The fixture's paths share one real volume; the vaults are
                // told apart by name, the compliant install's shape. The
                // restore folder and the engine's working directory share
                // the other.
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
                AvailableBytesProbe = _ => availableBytes,
            },
            _timeout.Token);
    }
}
