using System.Runtime.Versioning;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// The 1.7 additions (ADR-0037) ride the same wire as everything else: each
/// new command crosses the local binding typed, discriminators and optional
/// fields intact, and each new result comes back as itself.
/// </summary>
[TestClass]
public sealed class ConfigurationContractTests : IDisposable
{
    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "fbp-api", Guid.NewGuid().ToString("n")[..12]);

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public ConfigurationContractTests() => Directory.CreateDirectory(_state);

    public void Dispose()
    {
        _timeout.Dispose();
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public void ContractVersion_TheChangePreviewSurface_IsRecordedAtOneEight()
    {
        // Deliberately exact: bumping Current without landing here is how a
        // minor stops meaning anything (the convention since 1.2).
        Assert.AreEqual("1.8", ContractVersion.Current.ToString());
    }

    [TestMethod]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("windows")]
    public async Task ConfigurationCommands_SentOverTheEndpoint_ArriveTypedWithTheirOptionalFields()
    {
        var service = new FakeService
        {
            Respond = command => command switch
            {
                UpsertBackupSetCommand => new AcknowledgedResult(),
                DeleteDestinationCommand => new ConfigurationChangeResult(["nothing was deleted"]),
                BrowseFoldersCommand => new FolderListingResult(
                    "/srv", "/", [new FolderDescriptor("data", "/srv/data", false, false)]),
                CreatePairingInviteCommand => new PairingInviteResult(
                    "code", "invite-1", 5UL, "127.0.0.1:9", null),
                PreviewSetChangesCommand => new SetChangePreviewResult(
                    "docs", "ab12", 7UL, 40,
                    New: new ChangeBucketDescriptor(2, ["a.txt", "b.txt"]),
                    Updated: new ChangeBucketDescriptor(1, ["c.txt"]),
                    MetadataOnly: new ChangeBucketDescriptor(0, []),
                    Moved: new ChangeBucketDescriptor(0, []),
                    Deleted: new ChangeBucketDescriptor(3, ["d.txt"]),
                    NoLongerIncluded: new ChangeBucketDescriptor(5, ["e.txt"]),
                    Failures: 1,
                    SampleLimit: 20),
                _ => new AcknowledgedResult(),
            },
        };

        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", _timeout.Token);

        Assert.IsInstanceOfType<AcknowledgedResult>(await client.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('a', 32), "docs", "/data", "every 4h", ["photos/**"], ["*.iso"], ["vault"],
                new RetentionPolicyDescriptor(KeepDaily: 7, MinGenerations: 2),
                new Dictionary<string, RetentionPolicyDescriptor> { ["vault"] = new(KeepWeekly: 4) })),
            _timeout.Token));

        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await client.ExecuteAsync(new DeleteDestinationCommand("vault"), _timeout.Token), out var change);
        Assert.ContainsSingle(change.Lines);

        Assert.IsInstanceOfType<FolderListingResult>(
            await client.ExecuteAsync(new BrowseFoldersCommand("/srv"), _timeout.Token), out var listing);
        Assert.AreEqual("/srv/data", Assert.ContainsSingle(listing.Folders).Path);

        Assert.IsInstanceOfType<PairingInviteResult>(
            await client.ExecuteAsync(
                new CreatePairingInviteCommand("laptop", "stores-here", 1024, 60), _timeout.Token),
            out var invite);
        Assert.AreEqual("invite-1", invite.InviteId);

        Assert.IsInstanceOfType<SetChangePreviewResult>(
            await client.ExecuteAsync(
                new PreviewSetChangesCommand("docs", ExcludeRules: ["photos"], SampleLimit: 5), _timeout.Token),
            out var preview);
        Assert.AreEqual(2, preview.New.Count);
        Assert.AreEqual("d.txt", Assert.ContainsSingle(preview.Deleted.Sample));
        Assert.AreEqual(5, preview.NoLongerIncluded.Count);
        Assert.AreEqual(1, preview.Failures);

        // What the service received is what was sent — optional fields intact.
        Assert.IsInstanceOfType<UpsertBackupSetCommand>(service.Received[0], out var upsert);
        Assert.AreEqual(7, upsert.Set.Retention?.KeepDaily);
        Assert.AreEqual(4, upsert.Set.DestinationRetention?["vault"].KeepWeekly);
        Assert.IsInstanceOfType<CreatePairingInviteCommand>(service.Received[3], out var create);
        Assert.AreEqual(60, create.TimeToLiveMinutes);
    }
}
