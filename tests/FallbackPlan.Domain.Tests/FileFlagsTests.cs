namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The file flags capture names and does not keep (FR-MAN-003; ADR-0180;
/// architecture 06 §3): Linux reports a file's immutable, append-only,
/// no-dump, compressed and encrypted flags through <c>statx</c>, only where
/// the volume says it reports each; macOS keeps its own in <c>st_flags</c>,
/// with hidden besides. No format key holds them, so capture names them in a
/// diagnostic, in one order, which reads back as the flags it named. A
/// restore sets none of them, and says so in the same names (ADR-0180
/// Amendment 1).
/// </summary>
[TestClass]
public sealed class FileFlagsTests
{
    private const ulong StatxCompressed = 0x04;
    private const ulong StatxImmutable = 0x10;
    private const ulong StatxAppend = 0x20;
    private const ulong StatxNoDump = 0x40;
    private const ulong StatxEncrypted = 0x800;
    private const ulong StatxMountRoot = 0x2000;
    private const ulong StatxVerity = 0x100000;

    [TestMethod]
    public void FromStatx_TakesEachFlagTheVolumeReports_AndNoOther()
    {
        const ulong all = StatxCompressed | StatxImmutable | StatxAppend | StatxNoDump | StatxEncrypted;

        Assert.AreEqual(
            FileFlagBits.Immutable | FileFlagBits.AppendOnly | FileFlagBits.NoDump | FileFlagBits.Compressed | FileFlagBits.Encrypted,
            MetadataRules.FileFlagsFromStatx(all | StatxMountRoot | StatxVerity, all | StatxMountRoot | StatxVerity));
        Assert.AreEqual(
            FileFlagBits.AppendOnly,
            MetadataRules.FileFlagsFromStatx(StatxImmutable | StatxAppend, StatxAppend),
            "a bit the volume does not say it reports is not read");
        Assert.AreEqual(FileFlagBits.None, MetadataRules.FileFlagsFromStatx(0, all));
    }

    [TestMethod]
    [DataRow(0x00000001U, FileFlagBits.NoDump)]
    [DataRow(0x00000002U, FileFlagBits.Immutable)]
    [DataRow(0x00000004U, FileFlagBits.AppendOnly)]
    [DataRow(0x00000020U, FileFlagBits.Compressed)]
    [DataRow(0x00008000U, FileFlagBits.Hidden)]
    [DataRow(0x00020000U, FileFlagBits.Immutable)]
    [DataRow(0x00040000U, FileFlagBits.AppendOnly)]
    [DataRow(0x00010040U, FileFlagBits.None)]
    public void FromDarwin_TakesTheUserAndSystemFlags(uint flags, FileFlagBits expected) =>
        Assert.AreEqual(expected, MetadataRules.FileFlagsFromDarwin(flags));

    [TestMethod]
    public void TheDiagnostic_NamesEachFlagInOneOrder_AndNoFlagsNameNothing()
    {
        Assert.IsNull(MetadataRules.FileFlagsDiagnostic(FileFlagBits.None));
        Assert.AreEqual(
            "file-flags: immutable, no-dump, hidden",
            MetadataRules.FileFlagsDiagnostic(FileFlagBits.Hidden | FileFlagBits.Immutable | FileFlagBits.NoDump));
        Assert.AreEqual(
            "file-flags: immutable, append-only, no-dump, compressed, encrypted, hidden",
            MetadataRules.FileFlagsDiagnostic((FileFlagBits)0x3F));
    }

    [TestMethod]
    public void TheNames_AreTheDiagnosticsOwn_InItsOrder()
    {
        Assert.IsEmpty(MetadataRules.FileFlagNamesOf(FileFlagBits.None));
        CollectionAssert.AreEqual(
            new[] { "append-only", "compressed", "hidden" },
            MetadataRules.FileFlagNamesOf(FileFlagBits.Hidden | FileFlagBits.Compressed | FileFlagBits.AppendOnly).ToArray());
    }

    [TestMethod]
    public void WhatARestoreDoesNotApply_NamesEachFlag_AndNothingWhereThereAreNone()
    {
        Assert.IsNull(MetadataRules.FileFlagsNotApplied(FileFlagBits.None));
        Assert.AreEqual(
            "file flag(s) not applied: immutable, no-dump (not set by a restore)",
            MetadataRules.FileFlagsNotApplied(FileFlagBits.NoDump | FileFlagBits.Immutable));
    }

    [TestMethod]
    public void TheDiagnostic_ReadsBackAsTheFlagsItNamed_AmongOthers()
    {
        for (var flags = FileFlagBits.None; flags <= (FileFlagBits)0x3F; flags++)
        {
            var diagnostics = MetadataRules.FileFlagsDiagnostic(flags) is { } named
                ? new[] { "captured-inconsistent: 2", named }
                : ["captured-inconsistent: 2"];

            Assert.AreEqual(flags, MetadataRules.FileFlagsOf(diagnostics));
        }

        Assert.AreEqual(FileFlagBits.None, MetadataRules.FileFlagsOf([]));
        Assert.AreEqual(FileFlagBits.NoDump, MetadataRules.FileFlagsOf(["file-flags: no-dump, sideways"]), "a name no build knows is passed over");
    }
}
