namespace FallbackPlan.Domain.Tests;

/// <summary>
/// What a run has landed, asked of each destination it finds occupied, so a
/// run never displaces, replaces or fails vaguely over a file it restored
/// itself (FR-RST-005; ADR-0170 Amendment 3). A destination is taken for a
/// name the run landed only where that name folds onto it, by case and by
/// Unicode normalisation, and the volume lists no entry under the
/// destination's own name: the volume took the two names for one. The rule
/// is one, in Domain, for the executor and the recovery tool alike, and asks
/// the disk itself where a destination is given (ADR-0170 Amendment 4).
/// </summary>
[TestClass]
public sealed class NamesLandedTests
{
    private const string Composed = "names/caf\u00E9.txt";
    private const string Decomposed = "names/cafe\u0301.txt";

    [TestMethod]
    public void AnOccupiedDestination_ThatANameLandedFoldsOnto_AndNoEntryListsAsWritten_IsTakenForThatName()
    {
        var landed = new NamesLanded();
        landed.Add("names/Notes.txt");
        landed.Add(Decomposed);

        Assert.AreEqual("names/Notes.txt", landed.TakenForOne("names/notes.txt", occupied: true, listedAsWritten: () => false));
        Assert.AreEqual(Decomposed, landed.TakenForOne(Composed, occupied: true, listedAsWritten: () => false));
    }

    [TestMethod]
    public void ADestinationListedUnderItsOwnName_IsAnEntryOfItsOwn()
    {
        var landed = new NamesLanded();
        landed.Add("names/Notes.txt");

        Assert.IsNull(landed.TakenForOne("names/notes.txt", occupied: true, listedAsWritten: () => true));
    }

    [TestMethod]
    public void AnUnoccupiedDestination_IsTakenForNothing_AndNothingIsListed()
    {
        var landed = new NamesLanded();
        landed.Add("names/Notes.txt");

        Assert.IsNull(landed.TakenForOne(
            "names/notes.txt", occupied: false, listedAsWritten: () => throw new AssertFailedException("listed")));
    }

    [TestMethod]
    public void ADestinationNoLandedNameFoldsOnto_IsTakenForNothing_AndNothingIsListed()
    {
        var landed = new NamesLanded();
        landed.Add("names/Other.txt");
        landed.Add("elsewhere/notes.txt");

        Assert.IsNull(landed.TakenForOne(
            "names/notes.txt", occupied: true, listedAsWritten: () => throw new AssertFailedException("listed")));
    }

    [TestMethod]
    public void ANameLanded_IsNotTakenForItself()
    {
        var landed = new NamesLanded();
        landed.Add("names/Notes.txt");

        Assert.IsNull(landed.TakenForOne("names/Notes.txt", occupied: true, listedAsWritten: () => false));
    }

    [TestMethod]
    public void TheFirstNameLanded_IsTheOneADestinationIsTakenFor()
    {
        // Both land where the volume keeps them apart; a third that folds
        // onto them is named by the first.
        var landed = new NamesLanded();
        landed.Add("names/Notes.txt");
        landed.Add("names/notes.txt");

        Assert.AreEqual("names/Notes.txt", landed.TakenForOne("names/NOTES.txt", occupied: true, listedAsWritten: () => false));
    }

    [TestMethod]
    public void OnDisk_TheVolumeDecidesWhetherADestinationIsTakenForALandedName()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"fbp-names-landed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "Notes.txt"), [1]);
            var landed = new NamesLanded();
            landed.Add("Notes.txt");
            var second = Path.Combine(folder, "notes.txt");

            // Windows' and macOS's volumes find the landed file under the
            // other name, and list it under its own alone.
            if (File.Exists(second))
            {
                Assert.AreEqual("Notes.txt", landed.TakenForOneAt("notes.txt", second));
                return;
            }

            Assert.IsNull(landed.TakenForOneAt("notes.txt", second), "nothing stands there");

            File.WriteAllBytes(second, [2]);
            Assert.IsNull(landed.TakenForOneAt("notes.txt", second), "somebody else's file, listed under its own name");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void ANameThatIsNotWellFormedUnicode_IsFoldedByCaseAlone()
    {
        // A lone surrogate has no normal form; the name is still compared.
        var landed = new NamesLanded();
        landed.Add("names/A\uD800.txt");

        Assert.AreEqual("names/A\uD800.txt", landed.TakenForOne("names/a\uD800.txt", occupied: true, listedAsWritten: () => false));
    }
}
