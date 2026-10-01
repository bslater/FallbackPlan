using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The placement condition (FR-DEST-017, ADR-0051) judged by the platform's
/// own volume probe, as it is judged on a person's machine. Every other
/// service suite overrides that probe, so that its fixture paths can pretend
/// to sit on two drives. That is how a probe that answered wrongly on Windows
/// went unseen. It called every path volume 0, so a set whose source was on
/// C: was refused a destination on an external drive, as it was refused every
/// local destination at all.
/// </summary>
/// <remarks>
/// Two volumes are needed. On the CI runner the temporary directory sits on
/// a disk of its own, apart from the system volume, and the test uses that.
/// On a machine where both are one volume, it reports inconclusive rather than
/// passing, because a refusal there would be right.
/// </remarks>
[TestClass]
public sealed partial class LocalPlacementRealVolumeTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private readonly string _systemVolumeDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "fbp-placement-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
        if (Directory.Exists(_systemVolumeDirectory))
        {
            try
            {
                Directory.Delete(_systemVolumeDirectory, recursive: true);
            }
            catch (IOException)
            {
                // The accepted set's first backup writes here; a handle that
                // outlives the test is noise, as the harness treats its own.
            }
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "the probe that misread every path as volume 0 is the Windows one")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task ADestinationOnAnotherVolume_IsAccepted_AndOneOnTheRootsVolume_IsRefused()
    {
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var elsewhere = Directory.CreateDirectory(Path.Combine(_systemVolumeDirectory, "vault")).FullName;
        var beside = Directory.CreateDirectory(Path.Combine(_harness.WorkPath, "beside")).FullName;
        if (VolumeSerialNumberOf(elsewhere) == VolumeSerialNumberOf(_harness.SourceRoot))
        {
            Assert.Inconclusive(
                $"'{elsewhere}' and '{_harness.SourceRoot}' are one volume here; the test needs the temporary "
                + "directory on a volume of its own, as the CI runner has it.");
        }

        // Declaring a destination is not what is judged: placement is a
        // condition of choosing one for a set.
        foreach (var (name, path) in new[] { ("external", elsewhere), ("beside", beside) })
        {
            var declared = await handler.ExecuteAsync(
                new UpsertDestinationCommand(new DestinationDescriptor(null, name, "local-path", path, null, null)),
                _timeout.Token);
            Assert.IsNotInstanceOfType<ServiceError>(
                declared, (declared as ServiceError)?.Message ?? $"'{name}' was not declared");
        }

        // The user's case: the source on one drive, the destination on
        // another. Accepted.
        var accepted = await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 1h", [], [], ["external"])),
            _timeout.Token);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            accepted, (accepted as ServiceError)?.Message ?? "the set was not accepted");

        // And the probe still tells volumes apart rather than accepting
        // everything: a destination on the root's own volume is refused.
        var refused = await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 1h", [], [], ["external", "beside"])),
            _timeout.Token);
        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.Contains("'beside' shares a volume", error.Message, StringComparison.Ordinal);
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();

        // No volume override: the platform's own probe answers.
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            _timeout.Token);
    }

    [SupportedOSPlatform("windows")]
    private static unsafe uint VolumeSerialNumberOf(string path)
    {
        var volume = stackalloc char[512];
        Assert.IsTrue(GetVolumePathName(path, volume, 512), $"GetVolumePathNameW failed with error {Marshal.GetLastPInvokeError()}");

        var root = new string(volume);
        Assert.IsTrue(
            GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0),
            $"GetVolumeInformationW({root}) failed with error {Marshal.GetLastPInvokeError()}");
        return serial;
    }

    [LibraryImport("kernel32", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static unsafe partial bool GetVolumePathName(string fileName, char* volumePathName, uint bufferLength);

    [LibraryImport("kernel32", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static unsafe partial bool GetVolumeInformation(
        string rootPathName, char* volumeName, uint volumeNameSize, out uint volumeSerialNumber,
        out uint maximumComponentLength, out uint fileSystemFlags, char* fileSystemName, uint fileSystemNameSize);
}
