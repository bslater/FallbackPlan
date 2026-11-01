using System.Text.Json;
using FallbackPlan.Cli;
using FallbackPlan.Domain;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// A restore is planned from the command line on its own (FR-RST-003,
/// ADR-0107): what the run would write and read, whether it fits, and what it
/// could not bring back, with nothing written. The exit code says whether the
/// run would restore every file, so a script can plan before it restores.
/// A plan exported to a file is kept as JSON, the plan's own figures beside
/// the run it was made for (ADR-0108). Two names the volume of the folder
/// it would write to takes for one are named a collision, whatever its
/// platform's volumes do (ADR-0170 Amendment 5), and a name that volume's
/// rules refuse or alter is named where it names files by Windows' rules
/// (ADR-0170 Amendment 6).
/// </summary>
/// <remarks>
/// In direct mode the CLI makes the plan itself, as it runs the restore
/// itself. The disk is the flow's own: <c>DirectGateway.AvailableBytesInFlow</c>
/// answers for every directory this test's plan asks about, and no other
/// test's.
/// </remarks>
[TestClass]
public sealed class RestorePlanCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    [TestMethod]
    public async Task Plan_SaysWhatTheRunWouldDo_AndWritesNothing()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");

        var plan = await _cli.RunAsync("restore", snapshot, "--output", destination, "--plan");

        Assert.AreEqual(0, plan.ExitCode, plan.All);
        Assert.Contains("2 file(s)", plan.All, StringComparison.Ordinal);
        Assert.Contains(destination, plan.All, StringComparison.Ordinal);
        Assert.Contains("nothing was restored", plan.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(destination), "a plan writes nothing, not even the folder");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "only Linux's own volume holds two names that differ in case as the source")]
    public async Task Plan_ToAFolderOnAVolumeThatFoldsCase_NamesTheCollision_ThoughItsPlatformsVolumesDoNot()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/Notes.txt", "upper");
        _cli.WriteFile("tree/notes.txt", "lower");
        var backup = await _cli.RunAsync("backup", Path.Combine(_cli.WorkPath, "tree"));
        Assert.AreEqual(0, backup.ExitCode, backup.All);
        var snapshot = await FirstSnapshotAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");

        var apart = await _cli.RunAsync("restore", snapshot, "--output", destination, "--plan");
        Assert.DoesNotContain("Collides on this target", apart.All, StringComparison.Ordinal);

        // A FAT drive mounted where the run would write (ADR-0170 Amendment 5).
        VolumeNaming.OfInFlow = _ => new VolumeNaming(CaseSensitive: false, NormalisationInsensitive: false);
        var folded = await _cli.RunAsync("restore", snapshot, "--output", destination, "--plan");

        Assert.Contains("Collides on this target", folded.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(destination), "a plan writes nothing, not even the folder");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Windows' own volumes cannot hold the name to back up")]
    public async Task Plan_ToAFolderOnAVolumeThatNamesByWindowsRules_NamesWhatTheyRefuse_ThoughItsPlatformDoesNot()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/what?.txt", "asked");
        _cli.WriteFile("tree/fine.txt", "fine");
        var backup = await _cli.RunAsync("backup", Path.Combine(_cli.WorkPath, "tree"));
        Assert.AreEqual(0, backup.ExitCode, backup.All);
        var snapshot = await FirstSnapshotAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");

        var own = await _cli.RunAsync("restore", snapshot, "--output", destination, "--plan");
        Assert.DoesNotContain("by Windows' rules", own.All, StringComparison.Ordinal);

        // A FAT drive mounted where the run would write (ADR-0170 Amendment 6).
        VolumeNaming.OfInFlow = _ => new VolumeNaming(CaseSensitive: false, NormalisationInsensitive: false, WindowsNames: true);
        var fat = await _cli.RunAsync("restore", snapshot, "--output", destination, "--plan");

        Assert.Contains("by Windows' rules", fat.All, StringComparison.Ordinal);
        Assert.Contains("what?.txt", fat.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(destination), "a plan writes nothing, not even the folder");
    }

    [TestMethod]
    public async Task Plan_ThatWillNotFit_SaysSo_AndExitsNonZero()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");

        DirectGateway.AvailableBytesInFlow = _ => 1_000;
        var plan = await _cli.RunAsync("restore", snapshot, "--output", destination, "--plan");

        Assert.AreNotEqual(0, plan.ExitCode, plan.All);
        Assert.Contains("would not fit", plan.All, StringComparison.Ordinal);
        Assert.Contains("1000", plan.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    public async Task Plan_OfFilesWhoseContentIsGone_NamesThem_AndExitsNonZero()
    {
        var snapshot = await BackUpAsync();
        Directory.Delete(Path.Combine(_cli.RepositoryPath, "blobs", "data"), recursive: true);

        var plan = await _cli.RunAsync("restore", snapshot, "--output", Path.Combine(_cli.WorkPath, "restored"), "--plan");

        Assert.AreNotEqual(0, plan.ExitCode, plan.All);
        Assert.Contains("missing", plan.All, StringComparison.Ordinal);
        Assert.Contains("one.txt", plan.All, StringComparison.Ordinal);
        Assert.Contains("two.txt", plan.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Plan_Exported_IsKeptAsJson_BesidePrintingIt()
    {
        var snapshot = await BackUpAsync();
        Directory.Delete(Path.Combine(_cli.RepositoryPath, "blobs", "data"), recursive: true);
        var destination = Path.Combine(_cli.WorkPath, "restored");
        var file = Path.Combine(_cli.WorkPath, "plan.json");

        var plan = await _cli.RunAsync("restore", snapshot, "--output", destination, "--plan", "--export", file);

        Assert.AreNotEqual(0, plan.ExitCode, "the plan names files a run would fail, exported or not");
        Assert.Contains(file, plan.All, StringComparison.Ordinal);
        Assert.Contains("missing", plan.All, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        var root = document.RootElement;
        Assert.AreEqual("fallbackplan-restore-plan", root.GetProperty("kind").GetString());
        Assert.AreEqual(2, root.GetProperty("version").GetInt32(), "one shape whichever client keeps it (ADR-0113)");
        Assert.AreEqual(snapshot, root.GetProperty("snapshot_id").GetString());
        Assert.AreEqual(destination, root.GetProperty("output_directory").GetString());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("path").ValueKind);
        Assert.AreEqual(2, root.GetProperty("plan").GetProperty("files").GetInt64());
        Assert.AreEqual(2, root.GetProperty("plan").GetProperty("missing_objects").GetArrayLength());
        Assert.IsFalse(Directory.Exists(destination), "an exported plan restores nothing either");
    }

    [TestMethod]
    public async Task Export_ImpliesAPlan_AndRestoresNothing()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");
        var file = Path.Combine(_cli.WorkPath, "plan.json");

        var plan = await _cli.RunAsync("restore", snapshot, "--output", destination, "--export", file);

        Assert.AreEqual(0, plan.ExitCode, plan.All);
        Assert.IsTrue(File.Exists(file));
        Assert.Contains("nothing was restored", plan.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    public async Task Plan_ExportedOverAFileAlreadyThere_IsRefused_AndLeavesItAlone()
    {
        var snapshot = await BackUpAsync();
        var file = Path.Combine(_cli.WorkPath, "plan.json");
        await File.WriteAllTextAsync(file, "somebody's");

        var plan = await _cli.RunAsync(
            "restore", snapshot, "--output", Path.Combine(_cli.WorkPath, "restored"), "--plan", "--export", file);

        Assert.AreNotEqual(0, plan.ExitCode, plan.All);
        Assert.Contains("already exists", plan.All, StringComparison.Ordinal);
        Assert.AreEqual("somebody's", await File.ReadAllTextAsync(file));
    }

    [TestMethod]
    public async Task Plan_ExportedOverAFileAlreadyThere_IsRefusedBeforeAnythingIsAsked()
    {
        // A snapshot nobody holds: had the plan been asked for, that would be
        // the answer.
        await _cli.InitAsync();
        var file = _cli.WriteFile("plan.json", "somebody's");

        var plan = await _cli.RunAsync(
            "restore", new string('0', 32), "--output", Path.Combine(_cli.WorkPath, "restored"), "--export", file);

        Assert.AreNotEqual(0, plan.ExitCode, plan.All);
        Assert.Contains("already exists", plan.All, StringComparison.Ordinal);
        Assert.DoesNotContain("knows nothing", plan.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Export_OverAFileThatAppearedSinceTheCheck_IsRefused_AndLeavesItAlone()
    {
        // The check before anything is asked can be outrun: the file is made
        // new or not at all.
        var file = _cli.WriteFile("late.json", "somebody's");

        var refused = Assert.ThrowsExactly<CliFailureException>(() => RestorePlanExport.Write(
            file, new RestoreRequest(new string('e', 32), null, "/out"), new Api.RestorePlanResult(1, 1, [])));

        Assert.Contains("already exists", refused.Message, StringComparison.Ordinal);
        Assert.AreEqual("somebody's", File.ReadAllText(file));
    }

    public void Dispose() => _cli.Dispose();

    private async Task<string> BackUpAsync()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/one.txt", "first");
        _cli.WriteFile("tree/nested/two.txt", "second");

        var backup = await _cli.RunAsync("backup", Path.Combine(_cli.WorkPath, "tree"));
        Assert.IsTrue(backup.ExitCode == 0, backup.All);
        return await FirstSnapshotAsync();
    }

    private async Task<string> FirstSnapshotAsync()
    {
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
