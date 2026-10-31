using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// A restore on Windows makes a symlink a link where the account may make
/// one (FR-RST-004; ADR-0181; architecture 06 §3, "preserve, privilege
/// permitting"). Whether it may is asked by making one. A link Windows
/// captured is a folder's link where its captured attribute bits say a
/// folder, and one captured elsewhere where what it names is a folder. A
/// target captured with another platform's separators is written with
/// Windows' own, which is the only separator Windows resolves in a link.
/// </summary>
[TestClass]
public sealed class SymbolicLinksTests
{
    private const uint FolderBit = 0x10;
    private const uint ReparsePointBit = 0x400;

    [TestMethod]
    public void ATarget_IsWrittenWithWindowsSeparators()
    {
        Assert.AreEqual(@"data\target.bin", SymbolicLinks.TargetOnWindows("data/target.bin"));
        Assert.AreEqual(@"..\shared\notes", SymbolicLinks.TargetOnWindows("../shared/notes"));
        Assert.AreEqual(@"C:\Users\ana\file.txt", SymbolicLinks.TargetOnWindows(@"C:\Users\ana\file.txt"));
    }

    [TestMethod]
    public void ALinkWindowsCaptured_IsAFoldersLink_ByItsBits_WhateverItNamesHere()
    {
        Assert.IsTrue(SymbolicLinks.PointsAtAFolder(FolderBit | ReparsePointBit, () => false));
        Assert.IsFalse(SymbolicLinks.PointsAtAFolder(ReparsePointBit, () => true));
    }

    [TestMethod]
    public void ALinkCapturedElsewhere_IsAFoldersLink_WhereWhatItNamesIsAFolder()
    {
        Assert.IsTrue(SymbolicLinks.PointsAtAFolder(null, () => true));
        Assert.IsFalse(SymbolicLinks.PointsAtAFolder(null, () => false));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "only Windows asks whether the account may make a link")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void OffWindows_ALinkCanAlwaysBeMade() => Assert.IsTrue(SymbolicLinks.CanCreate);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows asks whether the account may make a link")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void OnWindows_WhetherALinkCanBeMade_IsWhatMakingOneAnswers()
    {
        var root = Directory.CreateTempSubdirectory("fbp-links-");
        try
        {
            bool made;
            try
            {
                File.CreateSymbolicLink(Path.Combine(root.FullName, "probe"), "nowhere");
                made = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                made = false;
            }

            Assert.AreEqual(made, SymbolicLinks.CanCreate);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
