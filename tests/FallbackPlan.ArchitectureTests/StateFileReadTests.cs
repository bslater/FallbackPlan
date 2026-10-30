using System.Reflection;
using System.Text;
using FallbackPlan.Domain;

namespace FallbackPlan.ArchitectureTests;

/// <summary>
/// A state file is read whole while a save replaces it, on Windows as
/// elsewhere (NFR-PORT-001, NFR-REL-007,
/// [ADR-0010](../../docs/adr/0010-local-store-separation.md) Amendments 8
/// and 9), whether the save goes through <see cref="AtomicFile"/> or renames
/// a temporary of its own into place.
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
/// Windows-only cases in <c>Application.Tests/AtomicFileTests</c>,
/// <c>Protocol.Tests/PeerGrantStoreTests</c>,
/// <c>Repository.Tests/Index/FileSequenceStateStoreTests</c> and
/// <c>Cli.Tests/RestoreReceiptsVerbTests</c> hold the read itself.
/// </para>
/// <para>
/// The scan pairs a read with a write in the same type. A type that reads a
/// file another type saves is beyond it; the command line's receipt report
/// is one, and its Windows case holds it instead.
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

    /// <summary>The calls that rename a file into place, over what was there or beside it.</summary>
    private static readonly MethodInfo[] RenamesIntoPlace =
    [
        typeof(File).GetMethod(nameof(File.Move), [typeof(string), typeof(string), typeof(bool)])!,
        typeof(File).GetMethod(nameof(File.Replace), [typeof(string), typeof(string), typeof(string)])!,
        typeof(File).GetMethod(nameof(File.Replace), [typeof(string), typeof(string), typeof(string), typeof(bool)])!,
    ];

    [TestMethod]
    public void ATypeThatSavesThroughAtomicFile_ReadsThroughItToo()
    {
        var (savers, offenders) = ReadsBeside([typeof(AtomicFile).GetMethod(nameof(AtomicFile.WriteAllText))!]);

        // The scan has to find the saver that failed, or finding no reader
        // beside a saver proves nothing.
        Assert.Contains("FallbackPlan.Protocol.PeerGrantStore", savers);
        Assert.IsEmpty(
            offenders,
            "each reads a file its type saves through AtomicFile with a read that does not share deletion, "
            + "which Windows refuses while a save is renaming the file into place; read it with "
            + "AtomicFile.ReadAllText: " + string.Join(", ", offenders));
    }

    [TestMethod]
    public void ATypeThatRenamesAFileIntoPlace_ReadsThroughAtomicFile()
    {
        // A store that writes its own temporary and renames it into place
        // holds the file open for deletion exactly as AtomicFile's rename
        // does, so its reads are refused at the same moment.
        var (renamers, offenders) = ReadsBeside(RenamesIntoPlace);

        Assert.Contains("FallbackPlan.Repository.Index.FileSequenceStateStore", renamers);
        Assert.IsEmpty(
            offenders,
            "each reads a file in a type that renames files into place, with a read that does not share "
            + "deletion, which Windows refuses while a rename is under way; read it through AtomicFile: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The types that call any of <paramref name="writes"/>, and every read
    /// sharing reading only that one of those types makes.
    /// </summary>
    private static (SortedSet<string> Writers, List<string> Offenders) ReadsBeside(MethodInfo[] writes)
    {
        var writers = new SortedSet<string>(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (var assembly in ProductAssemblies())
        {
            var writingTypes = writes
                .SelectMany(write => CallSites.Of(write, assembly))
                .Select(TypeOf)
                .ToHashSet(StringComparer.Ordinal);
            writers.UnionWith(writingTypes);

            foreach (var read in ReadsSharingReadingOnly)
            {
                offenders.AddRange(CallSites.Of(read, assembly)
                    .Where(caller => writingTypes.Contains(TypeOf(caller)))
                    .Select(caller => $"{caller} ({read.Name})"));
            }
        }

        return (writers, offenders);
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
