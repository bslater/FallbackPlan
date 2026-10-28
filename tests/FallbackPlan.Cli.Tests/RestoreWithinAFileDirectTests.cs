using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Cli;

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
/// kept (ADR-0131).
/// </summary>
[TestClass]
public sealed class RestoreWithinAFileDirectTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    private string Destination => Path.Combine(_cli.WorkPath, "restored");

    private string Parts => DirectRestoreParts.Of(_cli.StatePath, Destination);

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
        Assert.Contains(DirectRestoreParts.RootUnder(_cli.StatePath), plan.All, StringComparison.Ordinal);
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
