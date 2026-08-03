using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// Runs the full provider contract suite against
/// <see cref="LocalFileSystemObjectStore"/> over a per-test temporary
/// directory.
/// </summary>
public sealed class LocalFileSystemObjectStoreContractTests : ObjectStoreContractTests, IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-store-tests", Guid.NewGuid().ToString("n"));

    /// <inheritdoc />
    protected override IObjectStore CreateStore() => new LocalFileSystemObjectStore(_root);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
