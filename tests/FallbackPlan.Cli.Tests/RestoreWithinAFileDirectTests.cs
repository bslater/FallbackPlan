using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Cli;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// In direct mode a restore cut short resumes within a file (FR-RST-008,
/// ADR-0135), as the service's does (ADR-0129). A run keeps the part of each
/// file it assembles under the state directory, in a folder named by where
/// it lands, since a run in direct mode has no name of its own. A resume
/// there takes each part up as far as it proves, a run there that finishes
/// lets them go, and a run that is not a resume takes up no part. A resume's
/// plan reads nothing its parts prove (FR-RST-003, ADR-0132), and the room a
/// run needs to assemble its largest file is measured where the parts are
/// kept (ADR-0131), less what they prove, for a resume's plan and run alike
/// (ADR-0136). A run that does not finish says how to resume it, as one
/// through the service does, and how much of the backup its parts keep under
/// the state directory and where (ADR-0137). <c>restore-parts</c> lists each
/// destination a run left unfinished, by the name of its folder, with what it
/// restores and where, and lets one go by that name alone (ADR-0142). A run
/// holds its destination's folder while it runs, so a second run there is
/// refused before it writes anything, and the listing says a run is under
/// way there and lets none of its parts go (ADR-0143).
/// </summary>
[TestClass]
public sealed class RestoreWithinAFileDirectTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    private string Destination => Path.Combine(_cli.WorkPath, "restored");

    private string Parts => DirectRestoreParts.Of(_cli.StatePath, Destination);

    private string Name => Path.GetFileName(Parts);

    [TestMethod]
    public async Task AResume_TakesUpThePartsItsDestinationKeeps_ReadingNoneOfThemAgain()
    {
        var snapshot = await BackUpAsync();
        KeepPart("first");
        KeepPart("second");

        var resumed = await WithContentUnreadableAsync(() => _cli.RunAsync("restore", snapshot, "--output", Destination, "--resume"));

        Assert.AreEqual(0, resumed.ExitCode, resumed.All);
        Assert.AreEqual("first", await File.ReadAllTextAsync(Path.Combine(Destination, "one.txt")));
        Assert.AreEqual("second", await File.ReadAllTextAsync(Path.Combine(Destination, "nested", "two.txt")));
        Assert.Contains("2 file(s) were resumed from what the run had assembled of them", resumed.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(Parts), "a run that finishes lets its parts go");
    }

    [TestMethod]
    public async Task ARunThatIsNotAResume_TakesUpNoPart()
    {
        var snapshot = await BackUpAsync();
        KeepPart("first");
        KeepPart("second");

        var run = await WithContentUnreadableAsync(() => _cli.RunAsync("restore", snapshot, "--output", Destination));

        Assert.AreNotEqual(0, run.ExitCode, run.All);
        Assert.IsFalse(File.Exists(Path.Combine(Destination, "one.txt")), "what it could not read did not land");
    }

    [TestMethod]
    public async Task ARunThatDoesNotFinish_KeepsItsParts_UntilARunThereFinishes()
    {
        var snapshot = await BackUpAsync();

        var failed = await WithContentUnreadableAsync(() => _cli.RunAsync("restore", snapshot, "--output", Destination));

        Assert.AreNotEqual(0, failed.ExitCode, failed.All);
        Assert.IsTrue(Directory.Exists(Parts), "a run that does not finish keeps what it assembled");

        var resumed = await _cli.RunAsync("restore", snapshot, "--output", Destination, "--resume");

        Assert.AreEqual(0, resumed.ExitCode, resumed.All);
        Assert.IsFalse(Directory.Exists(Parts), "and a run there that finishes lets it go");
    }

    [TestMethod]
    public async Task ARunThatDoesNotFinish_SaysHowToResumeIt_AndWhatItsPartsKeep()
    {
        var snapshot = await BackUpAsync();
        KeepPart("assembled before");

        var failed = await WithContentUnreadableAsync(() => _cli.RunAsync("restore", snapshot, "--output", Destination));

        Assert.AreNotEqual(0, failed.ExitCode, failed.All);
        Assert.Contains("to restore what is left, run it again with --resume", failed.All, StringComparison.Ordinal);
        Assert.Contains($"16 bytes of its files assembled so far are kept under '{Parts}'", failed.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ARunThatDoesNotFinish_HavingAssembledNothing_SaysOnlyHowToResumeIt()
    {
        var snapshot = await BackUpAsync();

        var failed = await WithContentUnreadableAsync(() => _cli.RunAsync("restore", snapshot, "--output", Destination));

        Assert.AreNotEqual(0, failed.ExitCode, failed.All);
        Assert.Contains("to restore what is left, run it again with --resume", failed.All, StringComparison.Ordinal);
        Assert.DoesNotContain(Parts, failed.All, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a FIFO is a POSIX entry kind, captured and never materialised")]
    public async Task ARunThatSkipsOnlyWhatTheTargetCannotHold_IsFinished_AndSaysNothingOfAResume()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/one.txt", "first");
        if (!MakeFifo(Path.Combine(_cli.WorkPath, "tree", "pipe")))
        {
            return; // the filesystem refuses FIFOs (some CI mounts): nothing to assert
        }

        var backup = await _cli.RunAsync("backup", Path.Combine(_cli.WorkPath, "tree"));
        Assert.AreEqual(0, backup.ExitCode, backup.All);
        var snapshots = await _cli.RunAsync("snapshots");
        var snapshot = snapshots.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0].Split(' ')[0];
        KeepPart("assembled before");

        var restored = await _cli.RunAsync("restore", snapshot, "--output", Destination);

        Assert.Contains("restore Partial", restored.All, StringComparison.Ordinal);
        Assert.DoesNotContain("--resume", restored.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(Parts), "a run that skipped only what the target cannot hold is finished");
    }

    [TestMethod]
    public async Task ARunThatFinishes_SaysNothingOfAResume()
    {
        var snapshot = await BackUpAsync();

        var restored = await _cli.RunAsync("restore", snapshot, "--output", Destination);

        Assert.AreEqual(0, restored.ExitCode, restored.All);
        Assert.DoesNotContain("--resume", restored.All, StringComparison.Ordinal);
        Assert.DoesNotContain(Parts, restored.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public void EachDestination_KeepsItsOwnParts_ApartFromTheServicesRuns()
    {
        var elsewhere = DirectRestoreParts.Of(_cli.StatePath, Path.Combine(_cli.WorkPath, "elsewhere"));

        Assert.AreNotEqual(Parts, elsewhere);
        Assert.AreEqual(Parts, DirectRestoreParts.Of(_cli.StatePath, Destination + Path.DirectorySeparatorChar), "one destination however it is written");
        Assert.AreEqual(DirectRestoreParts.RootUnder(_cli.StatePath), Path.GetDirectoryName(Parts));
        Assert.AreNotEqual(Path.Combine(_cli.StatePath, "restore-parts"), DirectRestoreParts.RootUnder(_cli.StatePath), "a service's start sweeps the runs it has no record of");
    }

    [TestMethod]
    public async Task APlanOfAResume_ReadsNothingItsPartsProve()
    {
        var snapshot = await BackUpAsync();
        var afresh = Read(await _cli.RunAsync("restore", snapshot, "--output", Destination, "--resume", "--plan"));
        KeepPart("second");

        var held = Read(await _cli.RunAsync("restore", snapshot, "--output", Destination, "--resume", "--plan"));

        Assert.IsLessThan(afresh, held);
    }

    [TestMethod]
    public async Task TheRoomToAssembleAFile_IsMeasuredWhereThePartsAreKept()
    {
        var snapshot = await BackUpAsync();
        // The state directory is a volume of its own, short of room; the
        // folder restored to and the system's temporary folder never are.
        DirectGateway.VolumeOfInFlow = path => path.StartsWith(_cli.StatePath, StringComparison.Ordinal) ? 2UL : 1UL;
        DirectGateway.AvailableBytesInFlow = path => path.StartsWith(_cli.StatePath, StringComparison.Ordinal) ? 1_000 : null;

        var plan = await _cli.RunAsync("restore", snapshot, "--output", Destination, "--plan");

        Assert.AreNotEqual(0, plan.ExitCode, plan.All);
        Assert.Contains(
            $"room: {DirectRestoreParts.RootUnder(_cli.StatePath)} needs 4096 byte(s) to assemble its files one at a time, 1000 free",
            plan.All,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task TheRoomToAssembleAFile_IsCreditedWhatItsPartsHold_OnAResumeAlone()
    {
        var snapshot = await BackUpAsync();
        KeepPart("first");
        KeepPart("second");
        DirectGateway.VolumeOfInFlow = path => path.StartsWith(_cli.StatePath, StringComparison.Ordinal) ? 2UL : 1UL;
        DirectGateway.AvailableBytesInFlow = path => path.StartsWith(_cli.StatePath, StringComparison.Ordinal) ? 1_000 : null;

        var afresh = await _cli.RunAsync("restore", snapshot, "--output", Destination, "--plan");
        var plan = await _cli.RunAsync("restore", snapshot, "--output", Destination, "--resume", "--plan");
        var refused = await _cli.RunAsync("restore", snapshot, "--output", Destination);
        var resumed = await _cli.RunAsync("restore", snapshot, "--output", Destination, "--resume");

        Assert.AreNotEqual(0, afresh.ExitCode, "a run that is not a resume takes up no part, so needs room for a whole file");
        Assert.AreEqual(0, plan.ExitCode, plan.All);
        Assert.DoesNotContain(DirectRestoreParts.RootUnder(_cli.StatePath), plan.All, StringComparison.Ordinal);
        Assert.AreNotEqual(0, refused.ExitCode, "nor is a run that is not a resume");
        Assert.Contains("would not fit", refused.All, StringComparison.Ordinal);
        Assert.AreEqual(0, resumed.ExitCode, resumed.All);
        Assert.Contains("2 file(s) were resumed from what the run had assembled of them", resumed.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ARunThatDoesNotFinish_IsListed_WithWhatItRestoresAndWhereItsPartsHold()
    {
        var snapshot = await BackUpAsync();
        KeepPart("assembled before");
        var failed = await WithContentUnreadableAsync(
            () => _cli.RunAsync("restore", snapshot, "--output", Destination, "--existing", "rename"));
        Assert.AreNotEqual(0, failed.ExitCode, failed.All);

        var listed = await CliHarness.RunRawAsync("restore-parts", "--state", _cli.StatePath);

        Assert.AreEqual(0, listed.ExitCode, listed.All);
        Assert.Contains(
            $"{Name}  snapshot {snapshot} to {Destination}, rename where a file is in the way, begun ",
            listed.Output,
            StringComparison.Ordinal);
        Assert.Contains(" UTC, holding 16 bytes it had assembled", listed.Output, StringComparison.Ordinal);
        Assert.Contains(
            $"1 destination a restore in direct mode left unfinished keeps parts under '{DirectRestoreParts.RootUnder(_cli.StatePath)}'.",
            listed.Output,
            StringComparison.Ordinal);
        Assert.Contains("restore-parts --state", listed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("one.txt", listed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("two.txt", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ARunThatFinishes_IsListedNoMore()
    {
        var snapshot = await BackUpAsync();
        var failed = await WithContentUnreadableAsync(() => _cli.RunAsync("restore", snapshot, "--output", Destination));
        Assert.AreNotEqual(0, failed.ExitCode, failed.All);
        Assert.Contains(Name, (await CliHarness.RunRawAsync("restore-parts", "--state", _cli.StatePath)).Output, StringComparison.Ordinal);

        var resumed = await _cli.RunAsync("restore", snapshot, "--output", Destination, "--resume");
        var listed = await CliHarness.RunRawAsync("restore-parts", "--state", _cli.StatePath);

        Assert.AreEqual(0, resumed.ExitCode, resumed.All);
        Assert.AreEqual(0, listed.ExitCode, listed.All);
        Assert.DoesNotContain(Name, listed.Output, StringComparison.Ordinal);
        Assert.Contains(
            $"No restore in direct mode left parts under '{DirectRestoreParts.RootUnder(_cli.StatePath)}'.",
            listed.Output,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task EachDestinationLeftUnfinished_IsListed_NewestFirst()
    {
        var snapshot = await BackUpAsync();
        var elsewhere = Path.Combine(_cli.WorkPath, "elsewhere");
        var first = await WithContentUnreadableAsync(() => _cli.RunAsync("restore", snapshot, "--output", Destination));
        await Task.Delay(TimeSpan.FromMilliseconds(5));
        var second = await WithContentUnreadableAsync(() => _cli.RunAsync("restore", snapshot, "--output", elsewhere));
        Assert.AreNotEqual(0, first.ExitCode, first.All);
        Assert.AreNotEqual(0, second.ExitCode, second.All);

        var listed = await CliHarness.RunRawAsync("restore-parts", "--state", _cli.StatePath);

        var newer = listed.Output.IndexOf($"to {elsewhere},", StringComparison.Ordinal);
        var older = listed.Output.IndexOf($"to {Destination},", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, newer, listed.All);
        Assert.IsGreaterThan(newer, older, "the newest is listed first");
        Assert.Contains("2 destinations a restore in direct mode left unfinished keep parts under", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task PartsKeptWithNoAccountOfTheirRun_AreListedAsSuch()
    {
        KeepPart("assembled before");

        var listed = await CliHarness.RunRawAsync("restore-parts", "--state", _cli.StatePath);

        Assert.AreEqual(0, listed.ExitCode, listed.All);
        Assert.Contains($"{Name}  holding 16 bytes it had assembled; what its run restored was not kept", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ADestinationsParts_AreLetGoByItsNameAlone_AndEveryOthersStay()
    {
        var snapshot = await BackUpAsync();
        var elsewhere = Path.Combine(_cli.WorkPath, "elsewhere");
        KeepPart("assembled before");
        Assert.AreNotEqual(0, (await WithContentUnreadableAsync(() => _cli.RunAsync("restore", snapshot, "--output", Destination))).ExitCode);
        Assert.AreNotEqual(0, (await WithContentUnreadableAsync(() => _cli.RunAsync("restore", snapshot, "--output", elsewhere))).ExitCode);

        var letGo = await CliHarness.RunRawAsync("restore-parts", "--state", _cli.StatePath, "--forget", Name);
        var listed = await CliHarness.RunRawAsync("restore-parts", "--state", _cli.StatePath);

        Assert.AreEqual(0, letGo.ExitCode, letGo.All);
        Assert.Contains(
            $"let go the parts kept under '{Parts}', 16 bytes of the files a restore in direct mode had assembled",
            letGo.Output,
            StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(Parts), "its folder is gone");
        Assert.IsTrue(Directory.Exists(DirectRestoreParts.Of(_cli.StatePath, elsewhere)), "every other destination's stays");
        Assert.DoesNotContain(Name, listed.Output, StringComparison.Ordinal);
        Assert.Contains($"to {elsewhere},", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ANameNoDestinationKeeps_IsRefused_NamingIt()
    {
        Directory.CreateDirectory(_cli.StatePath);
        var absent = new string('a', 32);

        var refused = await CliHarness.RunRawAsync("restore-parts", "--state", _cli.StatePath, "--forget", absent);

        Assert.AreEqual(1, refused.ExitCode, refused.All);
        Assert.Contains(
            $"No restore in direct mode keeps parts named '{absent}' under '{DirectRestoreParts.RootUnder(_cli.StatePath)}'.",
            refused.Error,
            StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("../receipts")]
    [DataRow("..")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task WhatIsNotAPartsFoldersName_IsRefused_AndLetsNothingGo(string name)
    {
        var receipts = Directory.CreateDirectory(Path.Combine(_cli.StatePath, "receipts")).FullName;
        KeepPart("assembled before");

        var refused = await CliHarness.RunRawAsync("restore-parts", "--state", _cli.StatePath, "--forget", name);

        Assert.AreEqual(1, refused.ExitCode, refused.All);
        Assert.Contains($"'{name}' is not the name of a destination's parts", refused.Error, StringComparison.Ordinal);
        Assert.IsTrue(Directory.Exists(receipts), "nothing beside the parts is let go");
        Assert.IsTrue(Directory.Exists(Parts), "nor any destination's parts");
    }

    [TestMethod]
    public async Task ARunThatDoesNotFinish_SaysHowToLetItsPartsGo()
    {
        var snapshot = await BackUpAsync();
        KeepPart("assembled before");

        var failed = await WithContentUnreadableAsync(() => _cli.RunAsync("restore", snapshot, "--output", Destination));

        Assert.AreNotEqual(0, failed.ExitCode, failed.All);
        Assert.Contains(
            $"kept under '{Parts}' until a run there finishes, or restore-parts --state '{_cli.StatePath}' --forget {Name} lets them go",
            failed.All,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AStateDirectoryThatIsNotThere_IsRefused()
    {
        var absent = Path.Combine(_cli.WorkPath, "no-such-state");

        var refused = await CliHarness.RunRawAsync("restore-parts", "--state", absent);

        Assert.AreEqual(1, refused.ExitCode, refused.All);
        Assert.Contains($"no state directory at '{absent}'", refused.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ASecondRunToADestinationUnderWay_IsRefused_BeforeAnythingIsWritten()
    {
        var snapshot = await BackUpAsync();
        using var underWay = HoldTheDestination();

        var refused = await _cli.RunAsync("restore", snapshot, "--output", Destination);

        Assert.AreEqual(1, refused.ExitCode, refused.All);
        Assert.Contains($"Another restore in direct mode holds this destination's folder '{Parts}'", refused.Error, StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(Path.Combine(Destination, "one.txt")), "nothing is written");
        Assert.IsFalse(File.Exists(Path.Combine(Parts, "run.json")), "nor what the run would restore");
    }

    [TestMethod]
    public async Task ARunThatEnds_LetsTheDestinationGo_ForTheNextRunThere()
    {
        var snapshot = await BackUpAsync();
        var failed = await WithContentUnreadableAsync(() => _cli.RunAsync("restore", snapshot, "--output", Destination));
        Assert.AreNotEqual(0, failed.ExitCode, failed.All);

        using (HoldTheDestination())
        {
            // Taken here, it was let go by the run that ended.
        }

        var resumed = await _cli.RunAsync("restore", snapshot, "--output", Destination, "--resume");

        Assert.AreEqual(0, resumed.ExitCode, resumed.All);
    }

    [TestMethod]
    public async Task ADestinationWithARunUnderWay_IsListedAsSuch()
    {
        var snapshot = await BackUpAsync();
        KeepPart("assembled before");
        Assert.AreNotEqual(0, (await WithContentUnreadableAsync(() => _cli.RunAsync("restore", snapshot, "--output", Destination))).ExitCode);
        using var underWay = HoldTheDestination();

        var listed = await CliHarness.RunRawAsync("restore-parts", "--state", _cli.StatePath);

        Assert.AreEqual(0, listed.ExitCode, listed.All);
        Assert.Contains("holding 16 bytes it had assembled; a run there is under way", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ADestinationLeftUnfinished_IsNotListedAsUnderWay()
    {
        var snapshot = await BackUpAsync();
        Assert.AreNotEqual(0, (await WithContentUnreadableAsync(() => _cli.RunAsync("restore", snapshot, "--output", Destination))).ExitCode);

        var listed = await CliHarness.RunRawAsync("restore-parts", "--state", _cli.StatePath);

        Assert.Contains(Name, listed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("under way", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ThePartsOfADestinationWithARunUnderWay_AreNotLetGo()
    {
        var snapshot = await BackUpAsync();
        Assert.AreNotEqual(0, (await WithContentUnreadableAsync(() => _cli.RunAsync("restore", snapshot, "--output", Destination))).ExitCode);
        using var underWay = HoldTheDestination();

        var refused = await CliHarness.RunRawAsync("restore-parts", "--state", _cli.StatePath, "--forget", Name);

        Assert.AreEqual(1, refused.ExitCode, refused.All);
        Assert.Contains($"A restore in direct mode is under way to the destination whose parts are named '{Name}'", refused.Error, StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(Path.Combine(Parts, "run.json")), "nothing of it is let go");
    }

    /// <summary>Holds the destination's folder as a run there does while it runs.</summary>
    private FileStream HoldTheDestination()
    {
        Directory.CreateDirectory(Parts);
        return new FileStream(Path.Combine(Parts, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>Makes a FIFO at <paramref name="path"/>, answering whether the platform would.</summary>
    private static bool MakeFifo(string path)
    {
        try
        {
            using var mkfifo = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo", [path]));
            mkfifo!.WaitForExit();
            return mkfifo.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>What a printed plan says its run will read.</summary>
    private static long Read(CliHarness.Invocation plan)
    {
        Assert.AreEqual(0, plan.ExitCode, plan.All);
        var read = System.Text.RegularExpressions.Regex.Match(plan.All, @", (\d+) byte\(s\) read");
        Assert.IsTrue(read.Success, plan.All);
        return long.Parse(read.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Keeps a part of the snapshot's file holding <paramref name="content"/>, as a run cut short in it would.</summary>
    private void KeepPart(string content)
    {
        Directory.CreateDirectory(Parts);
        File.WriteAllText(
            Path.Combine(Parts, $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))}.part"), content);
    }

    /// <summary>
    /// Runs <paramref name="invoke"/> while every data blob's bytes past its
    /// envelope are flipped, so no file's content reads and only what a part
    /// holds can land; the blobs are put back after.
    /// </summary>
    private async Task<CliHarness.Invocation> WithContentUnreadableAsync(Func<Task<CliHarness.Invocation>> invoke)
    {
        var files = Directory.GetFiles(Path.Combine(_cli.RepositoryPath, "blobs", "data"), "*", SearchOption.AllDirectories);
        Assert.IsNotEmpty(files, "the archive must hold data blobs for this to test anything");
        var sound = files.Select(path => (Path: path, Bytes: File.ReadAllBytes(path))).ToList();
        foreach (var (path, bytes) in sound)
        {
            var flipped = bytes.ToArray();
            for (var i = 200; i < flipped.Length; i++)
            {
                flipped[i] ^= 0xFF;
            }

            await File.WriteAllBytesAsync(path, flipped);
        }

        try
        {
            return await invoke();
        }
        finally
        {
            foreach (var (path, bytes) in sound)
            {
                await File.WriteAllBytesAsync(path, bytes);
            }
        }
    }

    private async Task<string> BackUpAsync()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/one.txt", "first");
        _cli.WriteFile("tree/nested/two.txt", "second");

        var backup = await _cli.RunAsync("backup", Path.Combine(_cli.WorkPath, "tree"));
        Assert.AreEqual(0, backup.ExitCode, backup.All);

        var snapshots = await _cli.RunAsync("snapshots");
        Assert.AreEqual(0, snapshots.ExitCode, snapshots.All);

        // The identifier is the leading hex token of the listing line.
        var first = snapshots.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        Assert.IsNotNull(first);
        return first.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }
}
