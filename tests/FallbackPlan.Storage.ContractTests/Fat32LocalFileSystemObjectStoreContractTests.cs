using FallbackPlan.TestSupport;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// Runs the full provider contract suite against
/// <see cref="Storage.Local.LocalFileSystemObjectStore"/> rooted on a FAT32 volume, as a
/// removable drive often is (FR-DEST-001; ADR-0012 Amendment 6).
/// FAT32 holds no links, and refuses the call that asks a folder for one; the
/// store's guard against a linked folder asked every folder on a key's path,
/// so every write into a folder an earlier write had made failed.
/// </summary>
[TestClass]
[PrivilegedPlatformCondition(TestPlatforms.Windows, "only an elevated process may make and mount a volume of its own")]
public sealed class Fat32LocalFileSystemObjectStoreContractTests : MountedVolumeContractTests
{
    private static FormattedVolume? s_volume;

    /// <summary>Mounts the one volume every test in the class writes to, each under a folder of its own.</summary>
    [ClassInitialize]
    public static void MountVolume(TestContext _)
    {
        if (OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
        {
            s_volume = FormattedVolume.Mount("fat32");
        }
    }

    /// <summary>Takes the volume away again.</summary>
    [ClassCleanup]
    public static void DismountVolume()
    {
        s_volume?.Dispose();
        s_volume = null;
    }

    /// <inheritdoc />
    protected override string MountedAt =>
        s_volume?.MountedAt ?? throw new InvalidOperationException("the FAT32 volume is not mounted");
}
