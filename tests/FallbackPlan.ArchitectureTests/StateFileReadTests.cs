using System.Reflection;
using System.Text;
using FallbackPlan.Domain;

namespace FallbackPlan.ArchitectureTests;

/// <summary>
/// A state file is read whole while a save replaces it, on Windows as
/// elsewhere (NFR-PORT-001, NFR-REL-007,
/// [ADR-0010](../../docs/adr/0010-local-store-separation.md) Amendment 8).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AtomicFile.WriteAllText"/> renames its new file into place,
/// and the rename holds that file open for deletion until it returns.
/// Windows refuses any open that does not share deletion with such a handle.
/// <c>File.ReadAllText</c>, <c>ReadAllBytes</c> and <c>ReadAllLines</c> share
/// reading only, so a reader that arrives in that moment throws where it
/// should read one version or the other. <see cref="AtomicFile.ReadAllText"/>
/// shares deletion. The grant store read <c>peers.json</c> the other way,
/// and every peer session saves that file, so a dial landing on a save
/// failed.
/// </para>
/// <para>
/// The rule is held here, in the compiled code, rather than by racing a
/// save. The moment is one rename long, and POSIX has no such refusal, so
/// such a test passes everywhere but the one platform it is about. The
/// Windows-only cases in <c>Application.Tests/AtomicFileTests</c> and
/// <c>Protocol.Tests/PeerGrantStoreTests</c> hold the read itself.
/// </para>
/// </remarks>
[TestClass]
public sealed class StateFileReadTests
{
    /// <summary>The whole-file reads that share reading only.</summary>
    private static readonly MethodInfo[] ReadsSharingReadingOnly =
    [
        typeof(File).GetMethod(nameof(File.ReadAllText), [typeof(string)])!,
        typeof(File).GetMethod(nameof(File.ReadAllText), [typeof(string), typeof(Encoding)])!,
        typeof(File).GetMethod(nameof(File.ReadAllBytes), [typeof(string)])!,
        typeof(File).GetMethod(nameof(File.ReadAllLines), [typeof(string)])!,
        typeof(File).GetMethod(nameof(File.ReadAllLines), [typeof(string), typeof(Encoding)])!,
    ];

    [TestMethod]
    public void ATypeThatSavesThroughAtomicFile_ReadsThroughItToo()
    {
        var save = typeof(AtomicFile).GetMethod(nameof(AtomicFile.WriteAllText))!;
        var savers = new SortedSet<string>(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (var assembly in ProductAssemblies())
        {
            var savingTypes = CallSites.Of(save, assembly).Select(TypeOf).ToHashSet(StringComparer.Ordinal);
            savers.UnionWith(savingTypes);

            foreach (var read in ReadsSharingReadingOnly)
            {
                offenders.AddRange(CallSites.Of(read, assembly)
                    .Where(caller => savingTypes.Contains(TypeOf(caller)))
                    .Select(caller => $"{caller} ({read.Name})"));
            }
        }

        // The scan has to find the saver that failed, or finding no reader
        // beside a saver proves nothing.
        Assert.Contains("FallbackPlan.Protocol.PeerGrantStore", savers);
        Assert.IsEmpty(
            offenders,
            "each reads a file its type saves through AtomicFile with a read that does not share deletion, "
            + "which Windows refuses while a save is renaming the file into place; read it with "
            + "AtomicFile.ReadAllText: " + string.Join(", ", offenders));
    }

    /// <summary>The outermost type a call site was written in.</summary>
    private static string TypeOf(string caller)
    {
        var type = caller[..caller.IndexOf("::", StringComparison.Ordinal)];
        var nested = type.IndexOf('+', StringComparison.Ordinal);
        return nested < 0 ? type : type[..nested];
    }

    /// <summary>
    /// Every product assembly in the test output directory, where each
    /// project reference has copied one.
    /// </summary>
    private static IEnumerable<Assembly> ProductAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "FallbackPlan.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.EndsWith("Tests", StringComparison.Ordinal)
                && !name.EndsWith(".TestSupport", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name => Assembly.Load(name!));
}
