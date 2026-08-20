using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FallbackPlan.TestSupport;

namespace FallbackPlan.ArchitectureTests;

/// <summary>
/// The shape ADR-0043 promises of every logged message: a stable event id, in
/// the range its project was allocated, unique across the solution
/// (NFR-OPS-007).
/// </summary>
/// <remarks>
/// <para>
/// The point of an event id is that somebody quoting "2021" from a bug report
/// can find the one place that emits it. Two projects both using 2021 breaks
/// that quietly and permanently — a log written today is read next year, so a
/// collision is not something a later renumbering can fix.
/// </para>
/// <para>
/// This reads the declarations rather than the compiled assemblies because
/// <c>[LoggerMessage]</c> is consumed by a source generator: the attribute is
/// not preserved in a form that survives to reflection on the generated
/// partial, and parsing the source is what actually sees what a maintainer
/// wrote.
/// </para>
/// </remarks>
[TestClass]
public sealed class LoggingShapeTests
{
    /// <summary>The ranges ADR-0043 §3 allocates, as (first, last, project prefix).</summary>
    private static readonly (int First, int Last, string Project)[] Ranges =
    [
        (1000, 1099, "FallbackPlan.Domain"),
        (1100, 1299, "FallbackPlan.Repository.Format"),
        (1300, 1399, "FallbackPlan.Repository.Crypto"),
        (1400, 1499, "FallbackPlan.Repository.Segmentation"),
        (1600, 1699, "FallbackPlan.Repository.Packing"),
        (1700, 1799, "FallbackPlan.Repository.Index"),
        (1800, 1899, "FallbackPlan.Repository.Catalogue"),
        (2000, 2499, "FallbackPlan.Repository"),
        (2500, 2599, "FallbackPlan.Storage.Local"),
        (2600, 2699, "FallbackPlan.Filesystem.Local"),
        (2800, 2899, "FallbackPlan.Restore"),
        (2900, 2999, "FallbackPlan.Retention"),
        (3000, 3099, "FallbackPlan.Replication"),
        (3100, 3199, "FallbackPlan.Recovery"),
        (3200, 3299, "FallbackPlan.Protocol"),
        (3300, 3399, "FallbackPlan.Keystore"),
        (3400, 3599, "FallbackPlan.Application"),
        (3600, 3699, "FallbackPlan.Api"),
        (3700, 3999, "FallbackPlan.Agent"),
        (4000, 4099, "FallbackPlan.Cli"),
        (4100, 4199, "FallbackPlan.Web"),
    ];

    [TestMethod]
    public void LogMessages_EveryEventId_IsUniqueAcrossTheSolution()
    {
        var byId = new Dictionary<int, List<string>>();

        foreach (var (id, project, member) in DeclaredEvents())
        {
            if (!byId.TryGetValue(id, out var owners))
            {
                owners = [];
                byId[id] = owners;
            }

            owners.Add($"{project}/{member}");
        }

        var collisions = byId
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => $"{pair.Key}: {string.Join(" and ", pair.Value)}")
            .ToArray();

        Assert.IsEmpty(
            collisions,
            "An event id names one message for the life of the product; a log written today is read "
            + $"next year. Colliding: {string.Join("; ", collisions)}");
    }

    [TestMethod]
    public void LogMessages_EveryEventId_SitsInItsProjectsAllocatedRange()
    {
        var strays = new List<string>();

        foreach (var (id, project, member) in DeclaredEvents())
        {
            var range = Ranges.FirstOrDefault(
                candidate => string.Equals(candidate.Project, project, StringComparison.Ordinal));

            if (range.Project is null)
            {
                strays.Add($"{project}/{member} ({id}): the project has no allocated range in ADR-0043 §3");
                continue;
            }

            if (id < range.First || id > range.Last)
            {
                strays.Add(
                    $"{project}/{member} ({id}): outside {range.First}–{range.Last}");
            }
        }

        Assert.IsEmpty(
            strays,
            "Every event id belongs to the range its project was allocated, so a range says where to "
            + $"look. Out of place: {string.Join("; ", strays)}");
    }

    [TestMethod]
    public void LogMessages_EveryDeclaration_NamesItsHolesInPascalCase()
    {
        // CA1727 enforces this at build time for the compiler's benefit; this
        // asserts it for the reader's, since the holes are what a structured
        // consumer filters on and a stray casing is a field nobody can query.
        var offenders = new List<string>();

        foreach (var file in LogFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match hole in Regex.Matches(text, @"\{([A-Za-z_][A-Za-z0-9_]*)(?::[^}]*)?\}"))
            {
                var name = hole.Groups[1].Value;
                if (!char.IsUpper(name[0]))
                {
                    offenders.Add($"{Path.GetFileName(Path.GetDirectoryName(file))}: {{{name}}}");
                }
            }
        }

        Assert.IsEmpty(offenders, string.Join("; ", offenders));
    }

    /// <summary>Every <c>[LoggerMessage]</c> declaration, as (event id, project, member).</summary>
    private static IEnumerable<(int Id, string Project, string Member)> DeclaredEvents()
    {
        foreach (var file in LogFiles())
        {
            var project = Path.GetFileName(Path.GetDirectoryName(file))!;
            var text = File.ReadAllText(file);

            foreach (Match declaration in Regex.Matches(
                text,
                @"EventId\s*=\s*(\d+)[^]]*\][^;{]*?\b(\w+)\s*\(",
                RegexOptions.Singleline))
            {
                yield return (
                    int.Parse(declaration.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    project,
                    declaration.Groups[2].Value);
            }
        }
    }

    private static string[] LogFiles() =>
        Directory.GetFiles(Path.Combine(RepositoryRoot(), "src"), "Log.cs", SearchOption.AllDirectories);

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        var root = Locate(AppContext.BaseDirectory) ?? Locate(Path.GetDirectoryName(sourceFile));
        Assert.IsNotNull(root);
        return root;
    }

    private static string? Locate(string? start)
    {
        for (var directory = start; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "FallbackPlan.slnx")))
            {
                return directory;
            }
        }

        return null;
    }
}
