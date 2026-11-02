using FallbackPlan.TestSupport;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// Runs the full provider contract suite against
/// <see cref="Storage.Local.LocalFileSystemObjectStore"/> rooted on an exFAT
/// volume, the format a removable drive too large for FAT32 is most often
/// given (FR-DEST-001; ADR-0012 Amendment 6). exFAT holds no links either,
/// and refuses the call that asks a folder for one as FAT32 does.
/// </summary>
[TestClass]
[PrivilegedPlatformCondition(TestPlatforms.Windows, "only an elevated process may make and mount a volume of its own")]
public sealed class ExFatLocalFileSystemObjectStoreContractTests : MountedVolumeContractTests
{
    private static FormattedVolume? s_volume;

    /// <summary>Mounts the one volume every test in the class writes to, each under a folder of its own.</summary>
    [ClassInitialize]
    public static void MountVolume(TestContext _)
    {
        if (OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
        {
            s_volume = FormattedVolume.Mount("exfat");
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
        s_volume?.MountedAt ?? throw new InvalidOperationException("the exFAT volume is not mounted");
}
