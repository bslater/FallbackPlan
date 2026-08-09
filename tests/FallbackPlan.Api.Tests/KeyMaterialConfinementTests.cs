using System.Reflection;
using FallbackPlan.Api;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// NFR-SEC-009: key material never crosses the command surface, in either
/// direction, under any setting.
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
        var exporting = ContractTypes()
            .Where(type => typeof(ServiceCommand).IsAssignableFrom(type))
            .Where(type => type.Name.Contains("Kit", StringComparison.Ordinal)
                || type.Name.Contains("Key", StringComparison.Ordinal)
                || type.Name.Contains("Unlock", StringComparison.Ordinal))
            .Select(type => type.Name)
            .ToList();

        Assert.IsTrue(
            exporting.Count == 0,
            "Key export re-derives the KEK from a passphrase supplied per invocation and therefore runs locally, "
            + $"never as a command: {string.Join(", ", exporting)}");
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
