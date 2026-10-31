using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// On a target that writes extended attributes and keeps no alternate
/// streams, as Linux and macOS do, a restore keeps a file's captured streams
/// as attributes of the same names (FR-RST-004; ADR-0179; architecture 06 §3).
/// Linux takes a file's own attribute only in its user namespace, so a stream
/// named outside it is not kept there, and is named; macOS takes any name.
/// The plan counts the streams Linux would refuse, by the same rule.
/// </summary>
[TestClass]
public sealed class StreamsAsAttributesTests
{
    [TestMethod]
    public void AFilesStreams_AreKeptAsAttributes_OnlyWhereTheTargetWritesAttributesAndKeepsNoStreams()
    {
        Assert.IsTrue(MetadataRules.KeepsStreamsAsAttributes(EntryKind.File, supportsAlternateStreams: false, writesAttributes: true));
        Assert.IsFalse(MetadataRules.KeepsStreamsAsAttributes(EntryKind.File, supportsAlternateStreams: true, writesAttributes: true));
        Assert.IsFalse(MetadataRules.KeepsStreamsAsAttributes(EntryKind.File, supportsAlternateStreams: false, writesAttributes: false));
        Assert.IsFalse(
            MetadataRules.KeepsStreamsAsAttributes(EntryKind.DirectoryPlaceholder, supportsAlternateStreams: false, writesAttributes: true),
            "a folder is captured with no streams");
        Assert.IsFalse(
            MetadataRules.KeepsStreamsAsAttributes(EntryKind.Symlink, supportsAlternateStreams: false, writesAttributes: true),
            "a link has none");
    }

    [TestMethod]
    [DataRow("user.origin")]
    [DataRow("user.com.apple.FinderInfo")]
    public void AStreamInTheUserNamespace_IsKeptAsAnAttribute_EvenWhereNamesTakeNamespaces(string name)
    {
        Assert.IsNull(MetadataRules.RefusedStreamAsAttribute(System.Text.Encoding.UTF8.GetBytes(name), namespaced: true));
        Assert.IsNull(MetadataRules.RefusedStreamAsAttribute(System.Text.Encoding.UTF8.GetBytes(name), namespaced: false));
    }

    [TestMethod]
    [DataRow("Zone.Identifier")]
    [DataRow("security.selinux")]
    [DataRow("trusted.overlay")]
    [DataRow("system.posix_acl_access")]
    [DataRow("users.not-quite")]
    [DataRow("user.")]
    public void AStreamOutsideTheUserNamespace_IsRefusedWhereNamesTakeNamespaces_AndKeptWhereTheyDoNot(string name)
    {
        var why = MetadataRules.RefusedStreamAsAttribute(System.Text.Encoding.UTF8.GetBytes(name), namespaced: true);

        Assert.IsNotNull(why);
        Assert.Contains("user namespace", why, StringComparison.Ordinal);
        Assert.IsNull(MetadataRules.RefusedStreamAsAttribute(System.Text.Encoding.UTF8.GetBytes(name), namespaced: false));
    }

    [TestMethod]
    public void TheStreamsOutsideTheUserNamespace_AreCounted()
    {
        var metadata = new EntryMetadata
        {
            AlternateStreams =
            [
                Stream("user.origin"),
                Stream("Zone.Identifier"),
                Stream("security.selinux"),
            ],
        };

        Assert.AreEqual(2, MetadataRules.StreamsOutsideUserNamespace(metadata));
        Assert.AreEqual(0, MetadataRules.StreamsOutsideUserNamespace(new EntryMetadata()));
    }

    private static AlternateStreamEntry Stream(string name) =>
        new(System.Text.Encoding.UTF8.GetBytes(name), ObjectId.FromBytes(Enumerable.Repeat((byte)0xAA, 32).ToArray()), 4);
}
