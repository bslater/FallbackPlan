namespace FallbackPlan.Domain.Tests;

/// <summary>
/// How the volume a folder lies on takes names, read without writing to it,
/// so a restore's plan judges collisions as the volume it writes to will,
/// not as its platform's volumes commonly do (FR-RST-003; ADR-0170
/// Amendment 5). macOS says whether a volume distinguishes case, Windows
/// whether a folder does, and Linux what kind of filesystem a folder is on
/// and whether the folder folds case itself. A folder not yet made is judged
/// by the nearest one that is. A volume made by Windows' rules, Windows' own
/// or a FAT or exFAT drive on Linux, says it names by them, so a plan judges
/// the names written there by them (FR-RST-003; ADR-0170 Amendment 6). A
/// volume that holds a name by its length in UTF-16 units, Windows' own or
/// an exFAT or NTFS volume on Linux, says so, so a plan judges a name's
/// length there in units rather than bytes (ADR-0170 Amendment 9).
/// </summary>
[TestClass]
public sealed class VolumeNamingTests
{
    private string _folder = null!;

    [TestInitialize]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"fbp-volume-naming-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    [TestMethod]
    public void TheVolumeTheTestsWriteOn_IsJudgedAsItBehaves()
    {
        var judged = VolumeNaming.Of(_folder);

        Assert.IsNotNull(judged, "every platform the suite runs on says how its volumes take names");
        File.WriteAllBytes(Path.Combine(_folder, "Probe.txt"), []);
        File.WriteAllBytes(Path.Combine(_folder, "caf\u00E9.txt"), []);
        Assert.AreEqual(!File.Exists(Path.Combine(_folder, "probe.txt")), judged.Value.CaseSensitive);
        Assert.AreEqual(File.Exists(Path.Combine(_folder, "cafe\u0301.txt")), judged.Value.NormalisationInsensitive);

        // Windows strips a name's trailing period, and no other platform's
        // own volume does.
        File.WriteAllBytes(Path.Combine(_folder, "Trailing."), []);
        Assert.AreEqual(File.Exists(Path.Combine(_folder, "Trailing")), judged.Value.WindowsNames);

        // A name of 200 three-byte characters is 200 units and 600 bytes:
        // Windows' volume holds it, and Linux's own does not. macOS's
        // limit is not judged.
        if (!OperatingSystem.IsMacOS())
        {
            bool held;
            try
            {
                File.WriteAllBytes(Path.Combine(_folder, new string('\u8A9E', 200)), []);
                held = true;
            }
            catch (IOException)
            {
                held = false;
            }

            Assert.AreEqual(held, judged.Value.NamesInUnits);
        }
    }

    [TestMethod]
    public void AFolderNotYetMade_IsJudgedByTheNearestOneThatIs_AndNothingIsMade()
    {
        var unmade = Path.Combine(_folder, "not", "yet", "made");

        Assert.AreEqual(VolumeNaming.Of(_folder), VolumeNaming.Of(unmade));
        Assert.IsFalse(Directory.Exists(Path.Combine(_folder, "not")), "judging a volume writes nothing");
    }

    [TestMethod]
    public void AFolderBelowAFile_IsNotJudged()
    {
        var file = Path.Combine(_folder, "a-file");
        File.WriteAllBytes(file, []);

        Assert.IsNull(VolumeNaming.Of(Path.Combine(file, "below")));
    }

    [TestMethod]
    public void OnLinux_AFilesystemThatFoldsCase_TakesNamesThatDifferInCaseForOne()
    {
        // FAT and exFAT, made by Windows' rules and named by them wherever
        // they are mounted.
        // FAT's driver holds a name to 255 bytes as well; exFAT's holds one
        // of 255 units whatever its bytes.
        Assert.AreEqual(
            new VolumeNaming(CaseSensitive: false, NormalisationInsensitive: false, WindowsNames: true),
            VolumeNaming.OfLinuxFilesystem(0x4d44, casefolded: false));
        Assert.AreEqual(
            new VolumeNaming(CaseSensitive: false, NormalisationInsensitive: false, WindowsNames: true, NamesInUnits: true),
            VolumeNaming.OfLinuxFilesystem(0x2011BAB0, casefolded: false));

        // A share served over SMB or CIFS, whose client maps the characters
        // Windows refuses to ones it takes, so a name lands as written.
        foreach (var share in new long[] { 0xFF534D42, 0xFE534D42, 0x517B })
        {
            Assert.AreEqual(
                new VolumeNaming(CaseSensitive: false, NormalisationInsensitive: false),
                VolumeNaming.OfLinuxFilesystem(share, casefolded: false),
                share.ToString("x", System.Globalization.CultureInfo.InvariantCulture));
        }

        // ext4, as Linux commonly makes a volume.
        Assert.AreEqual(
            new VolumeNaming(CaseSensitive: true, NormalisationInsensitive: false),
            VolumeNaming.OfLinuxFilesystem(0xEF53, casefolded: false));
    }

    [TestMethod]
    public void OnLinux_AnNtfsVolume_KeepsCaseApart_AndHoldsANameByItsUnits()
    {
        // Linux's in-kernel NTFS drivers, the current and the older, which
        // keep case apart and write names Windows refuses.
        foreach (var ntfs in new long[] { 0x7366746e, 0x5346544e })
        {
            Assert.AreEqual(
                new VolumeNaming(CaseSensitive: true, NormalisationInsensitive: false, NamesInUnits: true),
                VolumeNaming.OfLinuxFilesystem(ntfs, casefolded: false),
                ntfs.ToString("x", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [TestMethod]
    public void OnLinux_AFolderThatFoldsCaseItself_TakesNamesThatDifferInCaseOrNormalisationForOne()
    {
        // A casefolded folder compares names by their Unicode case fold,
        // which normalises them too.
        Assert.AreEqual(
            new VolumeNaming(CaseSensitive: false, NormalisationInsensitive: true),
            VolumeNaming.OfLinuxFilesystem(0xEF53, casefolded: true));
    }

    [TestMethod]
    public void AFlowThatStatesHowVolumesTakeNames_IsAnsweredByIt()
    {
        VolumeNaming.OfInFlow = folder => string.Equals(folder, _folder, StringComparison.Ordinal)
            ? new VolumeNaming(CaseSensitive: false, NormalisationInsensitive: true)
            : null;

        Assert.AreEqual(new VolumeNaming(CaseSensitive: false, NormalisationInsensitive: true), VolumeNaming.Of(_folder));
        Assert.IsNull(VolumeNaming.Of(Path.Combine(_folder, "elsewhere")));
    }
}
