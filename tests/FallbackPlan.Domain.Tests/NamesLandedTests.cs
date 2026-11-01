namespace FallbackPlan.Domain.Tests;

/// <summary>
/// What a run has landed, asked of each destination it finds occupied, so a
/// run never displaces, replaces or fails vaguely over a file it restored
/// itself (FR-RST-005; ADR-0170 Amendment 3). A destination is taken for a
/// name the run landed only where that name folds onto it, by case and by
/// Unicode normalisation, and the volume lists no entry under the
/// destination's own name: the volume took the two names for one. The rule
/// is one, in Domain, for the executor and the recovery tool alike, and asks
/// the disk itself where a destination is given (ADR-0170 Amendment 4). A
/// name folds onto one that differs from it only by a component's trailing
/// periods, which a FAT or exFAT drive strips, so a run to one never takes
/// the file it landed as <c>notes.</c> for somebody else's <c>notes</c>
/// (FR-RST-005; ADR-0170 Amendment 7). The listing is asked of every name the
/// path gives on the way to its destination, each folder's as well as the
/// file's own, so a file landing in a folder the volume takes for one the
/// run landed into is taken for the run's own too (ADR-0170 Amendment 8).
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
    public void AnOccupiedDestination_ThatALandedNameDiffersFromOnlyByTrailingPeriods_AndNoEntryListsAsWritten_IsTakenForThatName()
    {
        var landed = new NamesLanded();
        landed.Add("names/notes");
        landed.Add("folder/report.");

        Assert.AreEqual("names/notes", landed.TakenForOne("names/notes.", occupied: true, listedAsWritten: () => false));
        Assert.AreEqual("names/notes", landed.TakenForOne("names/notes..", occupied: true, listedAsWritten: () => false));
        Assert.AreEqual("folder/report.", landed.TakenForOne("folder/report", occupied: true, listedAsWritten: () => false));

        // A folder's trailing periods go as a file's do.
        Assert.AreEqual("names/notes", landed.TakenForOne("names./notes", occupied: true, listedAsWritten: () => false));

        // Where the volume keeps the two apart, the second is listed under
        // its own name, and is an entry of its own.
        Assert.IsNull(landed.TakenForOne("names/notes.", occupied: true, listedAsWritten: () => true));
    }

    [TestMethod]
    public void APeriodWithinOrBeforeAName_OrATrailingSpace_IsNotFoldedAway()
    {
        var landed = new NamesLanded();
        landed.Add("names/notes.txt");
        landed.Add("names/draft");
        landed.Add("names/profile");

        Assert.IsNull(landed.TakenForOne(
            "names/.profile", occupied: true, listedAsWritten: () => throw new AssertFailedException("listed")));

        Assert.IsNull(landed.TakenForOne(
            "names/notestxt", occupied: true, listedAsWritten: () => throw new AssertFailedException("listed")));
        Assert.IsNull(landed.TakenForOne(
            "names/draft ", occupied: true, listedAsWritten: () => throw new AssertFailedException("listed")));
    }

    [TestMethod]
    public void ListedAsWritten_AsksEveryNameThePathGivesOnTheWay_AndNoFolderAboveIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "fbp-listed", "out");
        var docs = Path.Combine(root, "docs");

        // A volume that took "docs" for the "Docs" a run made lists the
        // folder under the name it was made with, and the file under its own.
        var folded = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [docs] = ["a.txt"],
            [root] = ["Docs"],
        };
        IEnumerable<string>? Lists(Dictionary<string, string[]> listing, string folder) =>
            listing.TryGetValue(folder, out var names) ? names : throw new AssertFailedException($"asked {folder}");

        Assert.IsFalse(NamesLanded.ListedAsWritten("docs/a.txt", Path.Combine(docs, "a.txt"), folder => Lists(folded, folder)));

        // Each name listed as written: the destination is an entry of its own.
        var apart = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [docs] = ["a.txt"],
            [root] = ["Docs", "docs"],
        };
        Assert.IsTrue(NamesLanded.ListedAsWritten("docs/a.txt", Path.Combine(docs, "a.txt"), folder => Lists(apart, folder)));

        // The file's own name, not listed, is not listed as written.
        var renamed = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [docs] = ["A.txt"],
            [root] = ["docs"],
        };
        Assert.IsFalse(NamesLanded.ListedAsWritten("docs/a.txt", Path.Combine(docs, "a.txt"), folder => Lists(renamed, folder)));

        // A folder that cannot be listed is taken to list it.
        Assert.IsTrue(NamesLanded.ListedAsWritten(
            "docs/a.txt", Path.Combine(docs, "a.txt"), folder => folder == docs ? null : Lists(apart, folder)));
    }

    [TestMethod]
    public void OnDisk_AFolderTheVolumeTakesForOneThatALandedNameLiesIn_IsJudgedToo()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"fbp-names-landed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "Docs"));
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "Docs", "a.txt"), [1]);
            var landed = new NamesLanded();
            landed.Add("Docs/a.txt");
            var second = Path.Combine(folder, "docs", "a.txt");

            // Windows' and macOS's volumes find the landed file in the other
            // folder, whose name they list as the folder was made.
            if (File.Exists(second))
            {
                Assert.AreEqual("Docs/a.txt", landed.TakenForOneAt("docs/a.txt", second));
                return;
            }

            Assert.IsNull(landed.TakenForOneAt("docs/a.txt", second), "nothing stands there");

            Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            File.WriteAllBytes(second, [2]);
            Assert.IsNull(landed.TakenForOneAt("docs/a.txt", second), "somebody else's file, in a folder listed under its own name");
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
