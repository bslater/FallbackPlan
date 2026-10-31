using FallbackPlan.Restore;

namespace FallbackPlan.Repository.Tests;

/// <summary>
/// What a run has landed, asked of each destination it finds occupied, so a
/// run never displaces, replaces or fails vaguely over a file it restored
/// itself (FR-RST-005; ADR-0170 Amendment 3). A destination is taken for a
/// name the run landed only where that name folds onto it, by case and by
/// Unicode normalisation, and the volume lists no entry under the
/// destination's own name: the volume took the two names for one.
/// </summary>
[TestClass]
public sealed class NamesLandedTests
{
    private const string Composed = "names/café.txt";
    private const string Decomposed = "names/café.txt";

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
    public void ANameThatIsNotWellFormedUnicode_IsFoldedByCaseAlone()
    {
        // A lone surrogate has no normal form; the name is still compared.
        var landed = new NamesLanded();
        landed.Add("names/A\uD800.txt");

        Assert.AreEqual("names/A\uD800.txt", landed.TakenForOne("names/a\uD800.txt", occupied: true, listedAsWritten: () => false));
    }
}
