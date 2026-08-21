using System.Reflection;
using FallbackPlan.Api;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// NFR-SEC-009: key material never crosses the command surface, in either
/// direction, under any setting. As amended by ADR-0042, exactly one shape
/// is carved out: hex-rendered sealed envelopes on the two named write-only
/// ceremonies — asserted here as precisely as the bans.
/// </summary>
/// <remarks>
/// <para>
/// This is asserted structurally rather than by review, because the rule is
/// easy to hold today and easy to lose the first time someone needs "just the
/// passphrase, just for this one command". A client that could ask the service
/// for the key would have made the keystore pointless.
/// </para>
/// <para>
/// The consequence for key export is deliberate: it is <b>not a command</b>.
/// Exporting a recovery kit re-derives the key-encryption key from a
/// user-supplied passphrase per invocation, so it runs locally with the
/// passphrase the user just typed — which is what makes holding a running
/// service insufficient to mint a recovery kit (ADR-0028 §9).
/// </para>
/// </remarks>
[TestClass]
public sealed class KeyMaterialConfinementTests
{
    private static readonly string[] ForbiddenFragments =
    [
        "passphrase", "password", "secret", "privatekey", "keymaterial",
        "kek", "masterkey", "blobkey", "classkey", "seed", "credential", "token",
    ];

    [TestMethod]
    public void ContractSurface_MemberNameSuggestsKeyMaterial_ExposesNone()
    {
        var offenders = new List<string>();

        foreach (var type in ContractTypes())
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = property.Name.ToLowerInvariant();
                if (Array.Exists(ForbiddenFragments, fragment => name.Contains(fragment, StringComparison.Ordinal)))
                {
                    offenders.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        Assert.IsTrue(
            offenders.Count == 0,
            "These contract members name key material, which must never cross the command surface "
            + $"(NFR-SEC-009): {string.Join(", ", offenders)}");
    }

    [TestMethod]
    public void ContractSurface_CommandsAndResults_CarryNoRawByteMembers()
    {
        // A byte array on the wire is how key material arrives without being
        // named as such. Identifiers cross as hex strings for exactly this
        // reason.
        var offenders = new List<string>();

        foreach (var type in ContractTypes())
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType == typeof(byte[])
                    || property.PropertyType == typeof(ReadOnlyMemory<byte>)
                    || property.PropertyType == typeof(Memory<byte>))
                {
                    offenders.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        Assert.IsTrue(offenders.Count == 0, $"These contract members carry raw bytes: {string.Join(", ", offenders)}");
    }

    [TestMethod]
    public void ContractSurface_CommandVocabulary_OffersNoKeyExport()
    {
        // Named exceptions, not a softer pattern. ConfirmRecoveryKitCommand
        // carries a SHA-256 of a kit the CLIENT built and the operator
        // saved; it produces nothing, opens nothing, and the service never
        // holds the kit it names (ADR-0044's amendment, FR-KIT-004). The
        // rule this test protects is that a kit is never *produced* by a
        // command — which remains true, and stays checkable, only because
        // the exception is written down here rather than dissolved into the
        // match.
        var recording = new[] { nameof(ConfirmRecoveryKitCommand) };

        var exporting = ContractTypes()
            .Where(type => typeof(ServiceCommand).IsAssignableFrom(type))
            .Where(type => type.Name.Contains("Kit", StringComparison.Ordinal)
                || type.Name.Contains("Key", StringComparison.Ordinal)
                || type.Name.Contains("Unlock", StringComparison.Ordinal))
            .Select(type => type.Name)
            .Except(recording, StringComparer.Ordinal)
            .ToList();

        Assert.IsTrue(
            exporting.Count == 0,
            "Key export re-derives the KEK from a passphrase supplied per invocation and therefore runs locally, "
            + $"never as a command: {string.Join(", ", exporting)}");
    }

    [TestMethod]
    public void ContractSurface_SealedEnvelopes_AreStringsOnExactlyTheNamedVerbs()
    {
        // NFR-SEC-009 [amended] (ADR-0042 §4): the ONE permitted shape of key
        // material in transit is a sealed envelope — hex-rendered, end-to-end
        // encrypted to the service's recipient key, opaque to every relay —
        // and only on the two ceremonies that need one. This test is the
        // carve-out's fence: the fields must stay string-typed (never raw
        // bytes), and must not quietly spread to other verbs.
        var envelopeMembers = ContractTypes()
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.Name == "Envelope")
                .Select(property => (Type: type, Property: property)))
            .ToList();

        CollectionAssert.AreEquivalent(
            new[]
            {
                nameof(ProvisionWriteOnlySetCommand),
                nameof(ProvisionInstallationCommand),
                nameof(OpenRestoreSourceCommand),
            },
            envelopeMembers.Select(member => member.Type.Name).ToList(),
            "Sealed envelopes are permitted on exactly provision_write_only_set, provision_installation and "
            + "open_restore_source (NFR-SEC-009 as amended by ADR-0042, and NFR-SEC-011 for setup) — nowhere "
            + "else. This list grows only by decision, which is what keeps it a fence; widening it to a "
            + "pattern that admits any verb named plausibly would not be one.");

        foreach (var (type, property) in envelopeMembers)
        {
            Assert.AreEqual(
                typeof(string), property.PropertyType,
                $"{type.Name}.{property.Name} must stay a hex string — a sealed envelope, never raw bytes.");
        }

        // The recipient key clients seal to is public by construction and
        // crosses as hex on describe_service.
        var recipient = typeof(ServiceDescriptionResult).GetProperty("RestoreGrantRecipient");
        Assert.IsNotNull(recipient);
        Assert.AreEqual(typeof(string), recipient.PropertyType);
    }

    [TestMethod]
    public void ContractAssembly_DependencyClosure_ReachesNoCryptography()
    {
        // The platform's SHA-256 is referenced and allowed: the endpoint derives
        // a named-pipe identity from the state directory path, which is naming,
        // not key handling. What must never appear is the project's own key
        // hierarchy or the third-party primitives ADR-0019 confines to
        // Repository.Crypto — those are how key material would arrive here.
        var referenced = typeof(ServiceCommand).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(name => name.StartsWith("FallbackPlan.Repository", StringComparison.Ordinal), referenced);
        Assert.DoesNotContain(name => name.StartsWith("FallbackPlan.Application", StringComparison.Ordinal), referenced);
        Assert.DoesNotContain(name => name.StartsWith("Bodu.Security", StringComparison.Ordinal), referenced);
    }

    private static IEnumerable<Type> ContractTypes() =>
        typeof(ServiceCommand).Assembly
            .GetTypes()
            .Where(type => type.IsPublic
                && (typeof(ServiceCommand).IsAssignableFrom(type)
                    || typeof(ServiceResult).IsAssignableFrom(type)
                    || type.Namespace == "FallbackPlan.Api"));
}
