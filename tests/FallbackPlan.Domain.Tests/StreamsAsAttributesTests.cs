using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// On a target that writes extended attributes and keeps no alternate
/// streams, as Linux and macOS do, a restore keeps a file's captured streams
/// as attributes of the same names (FR-RST-004; ADR-0179; architecture 06 §3).
/// Linux takes a file's own attribute only in its user namespace, so a stream
/// named outside it is not kept there, and is named; macOS takes any name.
/// The plan counts the streams Linux would refuse, by the same rule. A name
/// or a value larger than the platform takes in any volume's attribute, as
/// Linux's 255 bytes and 64 KiB and macOS's 127-byte names, is refused by
/// the rule too, and measured for the plan (ADR-0179 Amendment 2).
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

    [TestMethod]
    [DataRow(256, 1UL, 255U, 65_536U, "255")]
    [DataRow(1, 65_537UL, 255U, 65_536U, "65536")]
    [DataRow(128, 1UL, 127U, null, "127")]
    public void AStreamLargerThanThePlatformTakesInAnAttribute_IsRefused_SayingTheLimit(
        int nameBytes, ulong length, uint? maxNameBytes, uint? maxValueBytes, string limit)
    {
        var why = MetadataRules.StreamTooLargeForAttribute(nameBytes, length, maxNameBytes, maxValueBytes);

        Assert.IsNotNull(why);
        Assert.Contains(limit, why, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(255, 65_536UL, 255U, 65_536U)]
    [DataRow(127, 1UL << 40, 127U, null)]
    [DataRow(4_000, 1UL << 40, null, null)]
    public void AStreamWithinWhatThePlatformTakes_IsKept(int nameBytes, ulong length, uint? maxNameBytes, uint? maxValueBytes) =>
        Assert.IsNull(MetadataRules.StreamTooLargeForAttribute(nameBytes, length, maxNameBytes, maxValueBytes));

    [TestMethod]
    public void ThisPlatformsLimits_AreLinuxsAndMacOss()
    {
        Assert.AreEqual(OperatingSystem.IsLinux() ? 255U : OperatingSystem.IsMacOS() ? 127U : (uint?)null, ExtendedAttributes.MaxNameBytes);
        Assert.AreEqual(OperatingSystem.IsLinux() ? 65_536U : (uint?)null, ExtendedAttributes.MaxValueBytes);
        Assert.AreEqual(
            new AttributeLimits(ExtendedAttributes.TakesNamespaces, ExtendedAttributes.MaxNameBytes, ExtendedAttributes.MaxValueBytes),
            AttributeLimits.OfThisPlatform);
    }

    [TestMethod]
    public void TheLongestStreamNameAndTheLargestStream_AreMeasured()
    {
        var metadata = new EntryMetadata
        {
            AlternateStreams =
            [
                new AlternateStreamEntry("user.a"u8.ToArray(), ObjectId.FromBytes(Enumerable.Repeat((byte)0xAA, 32).ToArray()), 70_000),
                new AlternateStreamEntry("user.longer"u8.ToArray(), ObjectId.FromBytes(Enumerable.Repeat((byte)0xBB, 32).ToArray()), 4),
            ],
        };

        Assert.AreEqual(11, MetadataRules.LongestStreamName(metadata));
        Assert.AreEqual(70_000UL, MetadataRules.LargestStream(metadata));
        Assert.AreEqual(0, MetadataRules.LongestStreamName(new EntryMetadata()));
        Assert.AreEqual(0UL, MetadataRules.LargestStream(new EntryMetadata()));
    }

    private static AlternateStreamEntry Stream(string name) =>
        new(System.Text.Encoding.UTF8.GetBytes(name), ObjectId.FromBytes(Enumerable.Repeat((byte)0xAA, 32).ToArray()), 4);
}
