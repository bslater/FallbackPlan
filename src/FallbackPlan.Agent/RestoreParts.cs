namespace FallbackPlan.Agent;

/// <summary>
/// Where each restore run the service names keeps the parts of the files it
/// was cut short in (FR-RST-008, ADR-0129): <c>restore-parts/{run}</c> beside
/// the run journal, under the run's own name, so a resume of it finds them.
/// </summary>
/// <remarks>
/// <para>
/// A part holds the plaintext of a backup's file, as the spool a restore
/// assembles in always has, so it stays under the service's own state and
/// never in the folder a run writes to.
/// </para>
/// <para>
/// Each step is best effort. A part not carried to a resume's name is
/// assembled again, and one not removed is removed when the service next
/// starts and finds no run on record to take it up.
/// </para>
/// </remarks>
internal static class RestoreParts
{
    /// <summary>The folder every run's parts are kept under, in a state directory.</summary>
    /// <param name="stateDirectory">The service's state directory.</param>
    public static string RootUnder(string stateDirectory) => Path.Combine(stateDirectory, "restore-parts");

    /// <summary>The folder a run keeps its parts in.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="run">The run's name.</param>
    public static string Of(ServiceRuntime runtime, string run) => Path.Combine(runtime.RestorePartsRoot, run);

    /// <summary>
    /// Moves the parts of <paramref name="continued"/> to
    /// <paramref name="run"/>, the name the resume of it runs under.
    /// </summary>
    public static void Carry(ServiceRuntime runtime, string continued, string run)
    {
        var from = Of(runtime, continued);
        var to = Of(runtime, run);
        Attempt(() =>
        {
            if (Directory.Exists(from) && !Directory.Exists(to))
            {
                Directory.Move(from, to);
            }
        });
    }

    /// <summary>
    /// The bytes a run's parts hold (ADR-0133), or none where it kept none
    /// or they will not be read.
    /// </summary>
    public static long Held(ServiceRuntime runtime, string run)
    {
        var held = 0L;
        Attempt(() =>
        {
            var parts = new DirectoryInfo(Of(runtime, run));
            if (parts.Exists)
            {
                held = parts.EnumerateFiles().Sum(part => part.Length);
            }
        });
        return held;
    }

    /// <summary>Removes a run's parts: it finished, or was let go, and nothing will take them up.</summary>
    public static void LetGo(ServiceRuntime runtime, string run) => Attempt(() =>
    {
        var parts = Of(runtime, run);
        if (Directory.Exists(parts))
        {
            Directory.Delete(parts, recursive: true);
        }
    });

    /// <summary>
    /// Removes the parts of every run the journal no longer names, as the
    /// service starts: a run finished or let go while its parts could not be
    /// removed, or the journal itself was lost.
    /// </summary>
    /// <param name="root">The folder every run's parts are kept under.</param>
    /// <param name="named">The runs on record.</param>
    public static void Sweep(string root, IEnumerable<string> named)
    {
        var onRecord = named.ToHashSet(StringComparer.Ordinal);
        Attempt(() =>
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var parts in Directory.EnumerateDirectories(root))
            {
                if (!onRecord.Contains(Path.GetFileName(parts)))
                {
                    Attempt(() => Directory.Delete(parts, recursive: true));
                }
            }
        });
    }

    private static void Attempt(Action step)
    {
        try
        {
            step();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
