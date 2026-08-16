namespace FallbackPlan.Application.Tests;

/// <summary>
/// How a destination is named, matched and addressed — the comparisons that
/// decide which destination a reference means (FR-DEST-001, FR-DEST-006).
/// </summary>
/// <remarks>
/// <para>
/// The surveyed changelog records two shipped fixes here — a hostname check
/// made case-insensitive, and a default port no longer rendered with a
/// pointless colon. Both are the same underlying hazard in different clothes: an
/// address or a name is compared one way in one place and another way
/// somewhere else, or is re-rendered into a form that no longer matches what
/// was stored.
/// </para>
/// <para>
/// The property that matters here is not which comparison is chosen — a
/// destination name is an identifier its operator picked, and case-sensitive
/// is a defensible choice — but that <b>uniqueness and lookup agree</b>. If
/// the set that rejects duplicates is stricter than the lookup, two
/// destinations collide and one silently shadows the other; if it is looser, a
/// declared destination cannot be found and a set stops backing up to it.
/// Either way nothing throws and nothing appears in a log.
/// </para>
/// </remarks>
[TestClass]
public sealed class DestinationIdentityTests
{
    private string _directory = null!;

    private string ConfigPath => Path.Combine(_directory, "config.json");

    [TestInitialize]
    public void Initialize() =>
        _directory = Directory.CreateTempSubdirectory("fp-destid-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private static DestinationConfiguration LocalPath(string name, char fill) => new()
    {
        Id = new string(fill, 32),
        Name = name,
        Kind = DestinationKind.LocalPath,
        Path = $"/mnt/{name}",
    };

    private static ClientConfiguration WithDestinations(params DestinationConfiguration[] destinations) => new()
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations = destinations,
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = new string('a', 32),
                Name = "docs",
                Root = "/data/docs",
                Destinations = [.. destinations.Select(d => new SetDestinationReference { Ref = d.Name })],
            },
        ],
    };

    /// <summary>
    /// The invariant: whatever comparison names use, the uniqueness check and
    /// the lookup must use the same one. Two names differing only in case are
    /// either both one destination or both two — never one to the validator
    /// and two to the resolver.
    /// </summary>
    [TestMethod]
    public void TwoNamesDifferingOnlyInCase_AreJudgedTheSameWayByUniquenessAndByLookup()
    {
        var configuration = WithDestinations(LocalPath("vault", '1'), LocalPath("VAULT", '2'));

        bool acceptedAsTwo;
        try
        {
            configuration.Save(ConfigPath);
            acceptedAsTwo = true;
        }
        catch (ClientStateException)
        {
            acceptedAsTwo = false;
        }

        if (!acceptedAsTwo)
        {
            // Uniqueness treats them as one name. Nothing more to check: the
            // configuration never loads, so no lookup can disagree with it.
            return;
        }

        var loaded = ClientConfiguration.Load(ConfigPath);

        // Uniqueness accepted two, so lookup must resolve two — distinctly.
        Assert.AreEqual(2, loaded.Destinations.Count);
        Assert.IsNotNull(loaded.FindDestination("vault"), "a declared destination could not be found");
        Assert.IsNotNull(loaded.FindDestination("VAULT"), "a declared destination could not be found");
        Assert.AreNotEqual(
            loaded.FindDestination("vault")!.Id,
            loaded.FindDestination("VAULT")!.Id,
            "uniqueness accepted two destinations that lookup collapses into one");
    }

    /// <summary>
    /// A reference that matches no declared destination must be refused at
    /// load, not resolved to a near-miss. This is the other half of the same
    /// invariant: the lookup must not be more forgiving than the validator.
    /// </summary>
    [TestMethod]
    public void AReferenceWhoseCaseDoesNotMatchADeclaredName_DoesNotSilentlyResolve()
    {
        var declared = LocalPath("vault", '1');
        var configuration = new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [declared],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = new string('a', 32),
                    Name = "docs",
                    Root = "/data/docs",
                    Destinations = [new SetDestinationReference { Ref = "VAULT" }],
                },
            ],
        };

        // Either the save refuses the dangling reference, or it saves and the
        // lookup must agree that "VAULT" is not "vault". What must not happen
        // is a save that succeeds and a lookup that quietly picks the
        // near-miss, because that silently redirects a backup.
        try
        {
            configuration.Save(ConfigPath);
        }
        catch (ClientStateException)
        {
            return;
        }

        var loaded = ClientConfiguration.Load(ConfigPath);
        var resolved = loaded.FindDestination("VAULT");

        Assert.IsTrue(
            resolved is null || string.Equals(resolved.Name, "VAULT", StringComparison.Ordinal),
            "a reference resolved to a destination with a different name");
    }

    /// <summary>
    /// An endpoint is stored as the operator wrote it and handed back
    /// unchanged. Nothing re-renders it, which is what makes the "colon for
    /// the default port" class of defect unreachable — there is no second
    /// rendering to disagree with the first.
    /// </summary>
    [TestMethod]
    [DataRow("alice.example.com:7040")]
    [DataRow("ALICE.example.com:7040")]
    [DataRow("[2001:db8::1]:7040")]
    [DataRow("192.0.2.10:443")]
    public void AnEndpointSurvivesAConfigurationRoundTripExactly(string endpoint)
    {
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('2', 32),
                    Name = "alice",
                    Kind = DestinationKind.Peer,
                    Fingerprint = "mgr7e7euwdpfkggmp4astkz5ia",
                    Endpoint = endpoint,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = new string('a', 32),
                    Name = "docs",
                    Root = "/data/docs",
                    Destinations = [new SetDestinationReference { Ref = "alice" }],
                },
            ],
        }.Save(ConfigPath);

        var loaded = ClientConfiguration.Load(ConfigPath);

        Assert.AreEqual(endpoint, loaded.FindDestination("alice")!.Endpoint);
    }

    /// <summary>
    /// And the host survives the parse with its case intact. DNS is
    /// case-insensitive, so lowercasing would resolve the same — but it would
    /// also mean the string handed to the dialler is not the string the
    /// operator wrote, and TLS name verification is the place where that stops
    /// being harmless.
    /// </summary>
    [TestMethod]
    public void ParsingAnEndpointPreservesTheHostsCase()
    {
        Assert.IsTrue(
            PeerEndpoint.TryParse("ALICE.Example.COM:7040", out var host, out var port, out var defect), defect);

        Assert.AreEqual("ALICE.Example.COM", host);
        Assert.AreEqual(7040, port);
    }
}
