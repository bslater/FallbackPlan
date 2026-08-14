using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The staging trim through the REAL operator surface: the agent `retention
/// --apply` verb, with the handler's own destination-kind mapping deciding
/// how each destination is verified — a reachable local path probed key by
/// key, a peer trusted through its sync-ledger claim (ADR-0034 §6). Every
/// earlier trim test supplied its own verification callback; this one proves
/// the mapping itself.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RetentionTrimVerbTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-trim-verb", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "trim-verb-passphrase!!";
    private static readonly string SetId = new('a', 32);
    private static readonly DateTimeOffset Day1 = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

    private string ArchivesRoot => Path.Combine(_root, "archives");
    private string StateDirectory => Path.Combine(_root, "state");
    private string SourceRoot => Path.Combine(_root, "source");
    private string VaultPath => Path.Combine(_root, "vault");

    public RetentionTrimVerbTests()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(SourceRoot);
        Directory.CreateDirectory(VaultPath);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day one content");

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('1', 32), Name = "vault",
                    Kind = DestinationKind.LocalPath, Path = VaultPath,
                },
                new DestinationConfiguration
                {
                    Id = new string('2', 32), Name = "friend",
                    Kind = DestinationKind.Peer,
                    Fingerprint = new string('b', 64),
                    Endpoint = "127.0.0.1:9",
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = SetId,
                    Name = "docs",
                    Root = SourceRoot,
                    Schedule = "every 4h",
                    Destinations =
                    [
                        new SetDestinationReference
                        {
                            Ref = "vault",
                            Retention = new RetentionConfiguration { MinGenerations = 10 },
                        },
                        new SetDestinationReference { Ref = "friend" },
                    ],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    [TestMethod]
    public async Task RetentionApplyVerb_TrimsThroughTheHandlersOwnVerificationMapping()
    {
        var variable = "FBP_TRIM_VERB_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, PassphraseText);
        try
        {
            // Three days of backups; each pass fans out — the vault converges
            // (it has rules), the unreachable peer is recorded and does not
            // fail the pass.
            await BackUpAsync(Day1);
            File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
            await BackUpAsync(Day1.AddDays(1));
            File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
            await BackUpAsync(Day1.AddDays(2));

            // The peer's ledger row, as a completed and PROVEN sync would
            // leave it: a synced sequence past everything published, and a
            // challenge answered covering it. Both halves are needed — a bare
            // claim no longer licenses a delete (FR-VER-006) — and this peer
            // is deliberately unreachable, so the row is forged rather than
            // earned. What is under test here is the handler's own mapping of
            // destination kind to verification basis, not the proof rule.
            var syncedAt = (ulong)DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
            var ledger = DestinationSyncStore.Open(StateDirectory);
            ledger.RecordSuccess(SetId, "friend", 0, syncedAt, syncedSequence: 1_000_000);
            ledger.RecordVerification(
                SetId, "friend", objects: 4, population: 12, verifiedSequence: 1_000_000, syncedAt);

            // The verb, end to end: the handler maps vault → store probe and
            // friend → ledger claim itself; both must vouch for both historic
            // blobs, and both do.
            var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            var error = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            var exit = await AgentHost.RunAsync(
                ["retention", "--archives", ArchivesRoot, "--state", StateDirectory,
                    "--passphrase-env", variable, "--apply"],
                output, error, CancellationToken.None);

            Assert.AreEqual(0, exit, error.ToString());
            Assert.Contains("trimmed: 2 historic data blob(s)", output.ToString(), StringComparison.Ordinal);

            var staging = new LocalFileSystemObjectStore(Path.Combine(ArchivesRoot, SetId));
            Assert.HasCount(1, await ListAsync(staging, "blobs/data/"));

            // The vault still holds every day's data — the trim moved
            // nothing there, it only stopped paying for history twice.
            var replica = new LocalFileSystemObjectStore(Directory.GetDirectories(VaultPath).Single());
            Assert.HasCount(3, await ListAsync(replica, "blobs/data/"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestMethod]
    public async Task RetentionApplyVerb_APeerExcusedFromProving_IsNamedRatherThanCountedSilently()
    {
        var variable = "FBP_TRIM_VERB_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, PassphraseText);
        try
        {
            await BackUpAsync(Day1);
            File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
            await BackUpAsync(Day1.AddDays(1));
            File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
            await BackUpAsync(Day1.AddDays(2));

            // The peer declares itself unprovable — and is then given the
            // strongest ledger row a proven peer could have. It must still not
            // license the delete: a destination that will not be challenged
            // cannot authorise us forgetting bytes on its say-so (FR-VER-006).
            // Mapping it by KIND would hand it the ledger basis, fail the
            // proof test, and leave the operator a bare count with no
            // destination named.
            DeclineVerificationAtThePeer();
            var syncedAt = (ulong)DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
            var ledger = DestinationSyncStore.Open(StateDirectory);
            ledger.RecordSuccess(SetId, "friend", 0, syncedAt, syncedSequence: 1_000_000);
            ledger.RecordVerification(
                SetId, "friend", objects: 4, population: 12, verifiedSequence: 1_000_000, syncedAt);

            var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            var error = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            var exit = await AgentHost.RunAsync(
                ["retention", "--archives", ArchivesRoot, "--state", StateDirectory,
                    "--passphrase-env", variable, "--apply"],
                output, error, CancellationToken.None);

            Assert.AreEqual(0, exit, error.ToString());
            var text = output.ToString();
            Assert.DoesNotContain("trimmed:", text, StringComparison.Ordinal);

            // The line names the destination and says outright that waiting is
            // not one of the ways out.
            Assert.Contains("destination 'friend' is declared unprovable", text, StringComparison.Ordinal);
            Assert.Contains("give it retention rules", text, StringComparison.Ordinal);

            var staging = new LocalFileSystemObjectStore(Path.Combine(ArchivesRoot, SetId));
            Assert.HasCount(3, await ListAsync(staging, "blobs/data/"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>Rewrites the configuration with the peer excused from verification.</summary>
    private void DeclineVerificationAtThePeer()
    {
        var path = Path.Combine(StateDirectory, "config.json");
        var configuration = ClientConfiguration.Load(path);
        new ClientConfiguration
        {
            SchemaVersion = configuration.SchemaVersion,
            Destinations =
            [
                .. configuration.Destinations.Select(destination => destination.Kind == DestinationKind.Peer
                    ? destination with { Verification = VerificationPolicy.AcknowledgedNone }
                    : destination),
            ],
            BackupSets = configuration.BackupSets,
        }.Save(path);
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        var result = await AgentPass.RunAsync(ArchivesRoot, passphrase, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private static async Task<List<string>> ListAsync(LocalFileSystemObjectStore store, string prefix)
    {
        var keys = new List<string>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse(prefix), ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key.Value);
        }

        return keys;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
