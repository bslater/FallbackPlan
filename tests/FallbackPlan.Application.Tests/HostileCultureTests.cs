using FallbackPlan.TestSupport;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// Durable state and the parses that read it, under cultures chosen to break
/// them (NFR-PORT-003).
/// </summary>
/// <remarks>
/// <para>
/// The surveyed changelog carries locale fixes repeatedly and in every
/// direction: locale-sensitive parsing for one region, invariant formatting
/// crashing a backup, date handling under a non-Gregorian calendar,
/// culture-dependent sorting. None of those is exotic — they are what happens
/// when a program writes a number one way and reads it another, and the two
/// ways are chosen by a setting the program never looks at.
/// </para>
/// <para>
/// FallbackPlan is invariant by construction and by analyser: CA1304 and
/// CA1311 are build errors here, so a culture-sensitive case change cannot
/// compile. That covers casing and nothing else. Number and date formatting
/// through ordinary interpolation is still culture-sensitive and still
/// compiles, and running the whole suite under one hostile culture cannot
/// find the defect that matters most — the <em>asymmetric</em> one, where a
/// file is written under a culture and read under a different one. Both
/// halves agree with each other in a single-culture run and disagree in real
/// life, on the machine of somebody who changed their locale or moved a state
/// directory between two devices.
/// </para>
/// <para>
/// So these tests deliberately cross cultures rather than merely set one.
/// </para>
/// </remarks>
[TestClass]
public sealed class HostileCultureTests
{
    private string _directory = null!;

    private string ConfigPath => Path.Combine(_directory, "config.json");

    [TestInitialize]
    public void Initialize() =>
        _directory = Directory.CreateTempSubdirectory("fp-culture-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private static ClientConfiguration Configuration() => new()
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32),
                Name = "usb-vault",
                Kind = DestinationKind.LocalPath,
                Path = "/mnt/vault",
            },
            new DestinationConfiguration
            {
                Id = new string('2', 32),
                Name = "alice",
                Kind = DestinationKind.Peer,
                Fingerprint = "mgr7e7euwdpfkggmp4astkz5ia",
                Endpoint = "alice.example.com:7040",
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = new string('a', 32),
                Name = "docs",
                Root = "/data/docs",
                Schedule = "every 30m",
                Destinations =
                [
                    new SetDestinationReference { Ref = "usb-vault" },
                    new SetDestinationReference
                    {
                        Ref = "alice",
                        Retention = new RetentionConfiguration { KeepMonthly = 4 },
                    },
                ],
                Retention = new RetentionConfiguration
                {
                    KeepDaily = 14,
                    KeepWeekly = 8,
                    MinGenerations = 3,
                },
            },
        ],
    };

    /// <summary>
    /// The whole point of the class: written under one culture, read under
    /// another. A single-culture sweep cannot fail this way, because a writer
    /// and a reader that are wrong in the same direction agree.
    /// </summary>
    [TestMethod]
    [DataRow("tr-TR", "en-US")]
    [DataRow("en-US", "tr-TR")]
    [DataRow("de-DE", "en-US")]
    [DataRow("ar-SA", "fr-CA")]
    [DataRow("fr-CA", "de-DE")]
    public void AConfigurationWrittenUnderOneCulture_ReadsBackUnchangedUnderAnother(string writing, string reading)
    {
        string written;
        using (new CultureScope(writing))
        {
            Configuration().Save(ConfigPath);
            written = File.ReadAllText(ConfigPath);
        }

        using (new CultureScope(reading))
        {
            var loaded = ClientConfiguration.Load(ConfigPath);
            var set = Assert.ContainsSingle(loaded.BackupSets);

            Assert.AreEqual(2, loaded.Destinations.Count);
            Assert.AreEqual("alice.example.com:7040", loaded.FindDestination("alice")!.Endpoint);
            Assert.AreEqual("every 30m", set.Schedule);
            Assert.AreEqual(14, set.Retention!.KeepDaily);
            Assert.AreEqual(8, set.Retention.KeepWeekly);
            Assert.AreEqual(3, set.Retention.MinGenerations);
            Assert.AreEqual(4, set.Destinations[1].Retention!.KeepMonthly);

            // Re-saving must not rewrite the file either: a round trip that
            // changes the bytes would churn every config on every locale
            // change, and would mean one of the two cultures is wrong.
            loaded.Save(ConfigPath);
            Assert.AreEqual(written, File.ReadAllText(ConfigPath), "the file changed by being read and written elsewhere");
        }
    }

    /// <summary>
    /// The sync ledger carries the numbers a status display and a retention
    /// decision both read — counts, sequences and Unix-millisecond stamps.
    /// </summary>
    [TestMethod]
    [DataRow("tr-TR", "en-US")]
    [DataRow("ar-SA", "de-DE")]
    [DataRow("de-DE", "fr-CA")]
    public void ASyncLedgerWrittenUnderOneCulture_ReadsBackUnchangedUnderAnother(string writing, string reading)
    {
        const ulong SyncedAt = 1_755_000_000_000;
        const ulong VerifiedAt = 1_755_000_600_000;

        using (new CultureScope(writing))
        {
            var store = DestinationSyncStore.Open(_directory);
            store.RecordSuccess("set-1", "usb-vault", objects: 1234567, SyncedAt, syncedSequence: 987654321);
            store.RecordVerification(
                "set-1", "usb-vault", objects: 16, population: 40960, verifiedSequence: 987654321,
                sampleCursor: "blobs/data/aaaa/bbbb", VerifiedAt);
        }

        using (new CultureScope(reading))
        {
            var record = DestinationSyncStore.Open(_directory).Find("set-1", "usb-vault");

            Assert.IsNotNull(record);
            Assert.AreEqual(DestinationSyncState.InSync, record.State);
            Assert.AreEqual(1234567L, record.Objects);
            Assert.AreEqual(SyncedAt, record.LastSuccessAt);
            Assert.AreEqual(987654321UL, record.SyncedSequence);
            Assert.AreEqual(VerifiedAt, record.VerifiedAt);
            Assert.AreEqual(16, record.VerifiedObjects);
            Assert.AreEqual(40960, record.VerifiedPopulation);
            Assert.AreEqual("blobs/data/aaaa/bbbb", record.SampleCursor);
        }
    }

    /// <summary>
    /// A schedule is text the operator wrote, stored verbatim and parsed on
    /// every scheduling decision. <c>daily at 07:05</c> is the interesting
    /// one: in a custom format string <c>:</c> means "the culture's time
    /// separator", which is not <c>:</c> everywhere.
    /// </summary>
    [TestMethod]
    [DataRow("every 30m")]
    [DataRow("every 12h")]
    [DataRow("every 7d")]
    [DataRow("daily at 07:05")]
    [DataRow("daily at 23:59")]
    [DataRow("daily at 00:00")]
    public void AScheduleParsesAndDerivesTheSameInEveryCulture(string text)
    {
        var anchor = new DateTimeOffset(2026, 3, 1, 4, 30, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 3, 1, 9, 15, 0, TimeSpan.Zero);

        DateTimeOffset? expected = null;
        foreach (var culture in CultureScope.Hostile)
        {
            using var scope = new CultureScope(culture);

            Assert.IsTrue(Schedule.TryParse(text, out var schedule, out var defect), $"{culture}: {defect}");
            Assert.AreEqual(text, schedule!.Text);

            var next = schedule.NextRun(anchor, now);
            expected ??= next;
            Assert.AreEqual(expected, next, $"'{text}' means a different time in {culture}");
        }
    }

    /// <summary>
    /// A malformed schedule must be refused everywhere. This is the direction
    /// that costs a backup rather than a message: a culture where a rejected
    /// number parses is one where a typo becomes a silently wrong interval.
    /// </summary>
    [TestMethod]
    [DataRow("every 1,5h")]
    [DataRow("every 1.5h")]
    [DataRow("every ٣h")]
    [DataRow("daily at 7.05")]
    [DataRow("daily at ٠٧:٠٥")]
    public void AMalformedScheduleIsRefusedInEveryCulture(string text)
    {
        foreach (var culture in CultureScope.Hostile)
        {
            using var scope = new CultureScope(culture);
            Assert.IsFalse(Schedule.TryParse(text, out _, out _), $"'{text}' was accepted under {culture}");
        }
    }

    /// <summary>
    /// A port is digits in every language — the same property
    /// <c>PeerEndpointTests</c> pins against culture flourishes, checked here
    /// from the other side by actually being in those cultures.
    /// </summary>
    [TestMethod]
    public void AnEndpointParsesTheSameInEveryCulture()
    {
        foreach (var culture in CultureScope.Hostile)
        {
            using var scope = new CultureScope(culture);

            Assert.IsTrue(PeerEndpoint.TryParse("alice.example.com:7040", out var host, out var port, out var defect), defect);
            Assert.AreEqual("alice.example.com", host);
            Assert.AreEqual(7040, port);

            Assert.IsFalse(PeerEndpoint.TryParse("alice.example.com:7.040", out _, out _, out _), culture);
            Assert.IsFalse(PeerEndpoint.TryParse("alice.example.com:7,040", out _, out _, out _), culture);
            Assert.IsFalse(PeerEndpoint.TryParse("alice.example.com:٧٠٤٠", out _, out _, out _), culture);
        }
    }

    /// <summary>
    /// The declaration checks that decide whether a destination is admitted
    /// must not change their mind with the locale — an address accepted in one
    /// culture and refused in another is a set that backs up on one machine
    /// and not its neighbour.
    /// </summary>
    [TestMethod]
    public void ADestinationIsJudgedTheSameInEveryCulture()
    {
        var peer = new DestinationConfiguration
        {
            Id = new string('2', 32),
            Name = "alice",
            Kind = DestinationKind.Peer,
            Fingerprint = "mgr7e7euwdpfkggmp4astkz5ia",
            Endpoint = "alice.example.com:7040",
        };
        var malformed = peer with { Endpoint = "alice.example.com:70000" };

        foreach (var culture in CultureScope.Hostile)
        {
            using var scope = new CultureScope(culture);
            Assert.IsNull(peer.AddressDefect, culture);
            Assert.IsNotNull(malformed.AddressDefect, culture);
        }
    }
}
