using FallbackPlan.Domain;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// What format a repository is born at (FR-ARCH-012, NFR-COMP-004;
/// [ADR-0052](../../docs/adr/0052-relocatable-records-format-v3.md),
/// [ADR-0096](../../docs/adr/0096-format-4-an-index-generation-per-delta.md)).
/// Format 3 was built over two slices and nothing live wrote it: the only
/// opt-in was the CLI's <c>--format-version</c>, and the service created at
/// format 2, so no installation published a Merkle commitment and no peer
/// could be challenged by chunk. A product that can read a format creates at
/// it, and that is format 4 now.
/// </summary>
/// <remarks>
/// These assert the <b>literal</b> version rather than
/// <see cref="FormatLimits.FormatVersion"/>, which is the constant under test;
/// a test written against the constant would follow it wherever it went and
/// prove nothing.
/// </remarks>
[TestClass]
public sealed class CreatedFormatTests : IDisposable
{
    private readonly HostHarness _harness = new();

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    [TestMethod]
    public async Task ARepositoryTheServiceCreates_IsAtTheLatestFormat()
    {
        await _harness.SetupAsync();
        _harness.WriteSourceFile("docs/report.txt", "the words worth keeping");
        await _harness.CreateRepositoryAsync();

        var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(
            new LocalFileSystemObjectStore(_harness.RepositoryPath), _timeout.Token);

        Assert.AreEqual(
            FormatVersions.IndexGenerations, descriptor.FormatVersion,
            "a set created today is a format-4 repository, or its compactions under a second writer still decline");
        Assert.Contains(
            RepositoryDescriptorCodec.FeatureRelocatableRecords,
            descriptor.RequiredFeatures,
            "the required feature is what makes a reader that cannot relocate refuse by name (ADR-0014)");
        Assert.Contains(
            RepositoryDescriptorCodec.FeatureIndexGenerations,
            descriptor.RequiredFeatures,
            "and this one makes a reader that cannot rank by index generation refuse by name (ADR-0096)");
    }

    [TestMethod]
    public void TheCreationDefault_IsTheLatestThisBuildCanRead() =>
        // The two constants have said different things since format 3 landed:
        // one what a reader accepts, the other what a writer creates. They
        // agree now, and the day they diverge again is a decision somebody
        // must take deliberately rather than inherit.
        Assert.AreEqual(FormatLimits.LatestFormatVersion, FormatLimits.FormatVersion);

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }
}
