namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The form a link is captured with (FR-MAN-003; ADR-0183 Amendment 1;
/// ADR-0184 §2): capture names a junction a mount point in a diagnostic, as
/// it names a file's flags, and from format 6 a manifest keeps the form in
/// key 14 and names it no more. Below format 6 no key keeps it, and a reader
/// of that format refuses key 14, so it stays named.
/// </summary>
[TestClass]
public sealed class LinkFormTests
{
    [TestMethod]
    public void TheDiagnostic_NamesAMountPoint_AndASymbolicLinkNothing()
    {
        Assert.AreEqual("link-form: mount-point", MetadataRules.LinkFormDiagnostic(LinkForm.MountPoint));
        Assert.IsNull(MetadataRules.LinkFormDiagnostic(LinkForm.SymbolicLink));
    }

    [TestMethod]
    public void AtFormatSix_AMountPointNamed_MovesIntoKeyFourteen_AndTheOtherDiagnosticsStay()
    {
        IReadOnlyList<string> diagnostics = ["captured-inconsistent: 2", "link-form: mount-point"];

        var (form, rest) = MetadataRules.KeepLinkForm(FormatVersions.FileFlagsAndLinkForms, diagnostics);

        Assert.AreEqual(LinkForm.MountPoint, form);
        CollectionAssert.AreEqual(new[] { "captured-inconsistent: 2" }, rest.ToArray());
    }

    [TestMethod]
    public void BelowFormatSix_AMountPointStaysNamed_AndNoKeyKeepsIt()
    {
        IReadOnlyList<string> diagnostics = ["link-form: mount-point"];

        foreach (var version in new[] { FormatVersions.RelocatableRecords, FormatVersions.DeltaCheckpoints })
        {
            var (form, rest) = MetadataRules.KeepLinkForm(version, diagnostics);

            Assert.AreEqual(LinkForm.SymbolicLink, form, $"format {version}");
            CollectionAssert.AreEqual(diagnostics.ToArray(), rest.ToArray(), $"format {version}");
        }
    }

    [TestMethod]
    public void ALinkNamedNoForm_IsASymbolicLink_AndKeptAsItWas()
    {
        IReadOnlyList<string> diagnostics = ["captured-inconsistent: 2"];

        var (form, rest) = MetadataRules.KeepLinkForm(FormatVersions.FileFlagsAndLinkForms, diagnostics);

        Assert.AreEqual(LinkForm.SymbolicLink, form);
        Assert.AreSame(diagnostics, rest);
    }

    [TestMethod]
    public void AFormNoBuildKnows_IsNoMountPoint_AndStaysNamed()
    {
        // Key 14 has one value; a name that is not it is said as captured
        // rather than kept as a form it is not.
        IReadOnlyList<string> diagnostics = ["link-form: sideways"];

        var (form, rest) = MetadataRules.KeepLinkForm(FormatVersions.FileFlagsAndLinkForms, diagnostics);

        Assert.AreEqual(LinkForm.SymbolicLink, form);
        CollectionAssert.AreEqual(diagnostics.ToArray(), rest.ToArray());
    }
}
