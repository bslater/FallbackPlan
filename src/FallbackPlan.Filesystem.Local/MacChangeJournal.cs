using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bodu;

namespace FallbackPlan.Filesystem.Local;

/// <summary>
/// What changed in a set's folders on macOS (ADR-0191): the volume's own
/// FSEvents history, which the system keeps whether or not anything listens,
/// as an NTFS volume keeps its change journal. A place in it is the volume's
/// history identity and an event number; asked what changed since one, it
/// replays the history from there and names the folder of each change.
/// </summary>
/// <remarks>
/// <para>
/// The system takes changes from the kernel and numbers them a moment after
/// they are made, and nothing promises that one it has numbered is already
/// in the history it replays. So a mark this process writes is heard first,
/// as it happens; then the history is replayed from the place, and taken
/// only where it holds that mark. Every change made before the question is
/// numbered before the mark, so a history holding the mark holds it too.
/// </para>
/// <para>
/// A history the system reset, dropped events from, or tells to be scanned
/// again is doubt; so is a changed file with another name, whose other
/// folders the history does not name.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed partial class MacChangeJournal : IChangeJournal
{
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.dylib";

    private const uint Utf8Encoding = 0x08000100;

    private const uint CreateNoDefer = 0x02;
    private const uint CreateWatchRoot = 0x04;
    private const uint CreateFileEvents = 0x10;

    private static readonly Lazy<nint> ArrayCallbacks = new(() =>
        NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFTypeArrayCallBacks"));

    private readonly string _marks;

    /// <summary>A journal whose marks are written in a folder of the process's own.</summary>
    public MacChangeJournal()
        : this(Path.Combine(Path.GetTempPath(), "fallbackplan-change-marks"))
    {
    }

    /// <summary>A journal whose marks are written in <paramref name="marks"/>.</summary>
    internal MacChangeJournal(string marks) => _marks = marks;

    /// <summary>How long a replay of the history may take before the answer is doubt.</summary>
    internal static TimeSpan ReplayTimeout { get; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a mark may take to come back before the answer is doubt.</summary>
    internal static TimeSpan MarkTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the history may take to hold a mark already heard, read again
    /// and again, before the answer is doubt.
    /// </summary>
    internal static TimeSpan HoldTimeout { get; } = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan HoldPause = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// For a test: each read of the history as it is to be taken, as a system
    /// that has not yet put a mark in its history would give it.
    /// </summary>
    internal Func<IReadOnlyList<(string Path, uint Flags, ulong Id)>, IReadOnlyList<(string Path, uint Flags, ulong Id)>>? AsRead { get; init; }

    /// <inheritdoc />
    public JournalPosition? PositionOf(string folder) => PositionOf(folder, out _);

    /// <summary>Where the history stands for <paramref name="folder"/>, or null with why it has no place.</summary>
    public static JournalPosition? PositionOf(string folder, out string? why)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(folder);
        if (!DarwinInterop.TryStat(folder, out var stat) || !stat.IsDirectory)
        {
            why = $"'{folder}' is not a folder";
            return null;
        }

        if (HistoryOf(stat.Device) is not { } history)
        {
            why = $"the volume holding '{folder}' keeps no history of its changes";
            return null;
        }

        why = null;
        return new JournalPosition(history.Volume, history.Id, unchecked((long)FSEventsGetCurrentEventId()));
    }

    /// <inheritdoc />
    public IReadOnlyList<ChangedFolder>? ChangedSince(JournalPosition since, IReadOnlyCollection<string> roots) =>
        ChangedSince(since, roots, out _);

    /// <summary>
    /// The folders that changed under <paramref name="roots"/> since
    /// <paramref name="since"/>, or null with <paramref name="doubt"/> saying
    /// why the history cannot say.
    /// </summary>
    public IReadOnlyList<ChangedFolder>? ChangedSince(
        JournalPosition since, IReadOnlyCollection<string> roots, out string? doubt)
    {
        ThrowHelper.ThrowIfNull(since);
        ThrowHelper.ThrowIfNull(roots);
        if (roots.Count == 0)
        {
            doubt = null;
            return [];
        }

        if (Heard(since, roots, out var real, out var marks, out doubt) is not { } heard)
        {
            return null;
        }

        return FsEventsRecords.Named(
            heard.Replayed.Select(replayed => (replayed.Path, replayed.Flags)),
            real,
            marks!,
            static path => DarwinInterop.TryStat(path, out var file) && file.IsRegularFile && file.LinkCount > 1,
            SpelledOnDisk,
            out doubt);
    }

    /// <summary>
    /// Every event the history an answer since <paramref name="since"/> is
    /// read from replayed under <paramref name="roots"/>, the marks' among
    /// them, with its flags and number, exactly as the system gave it, and
    /// the mark heard before it was read; or null with
    /// <paramref name="doubt"/> saying why the history cannot be read. For a
    /// test to show what the history said where an answer is wrong.
    /// </summary>
    internal (IReadOnlyList<(string Path, uint Flags, ulong Id)> Replayed, string Mark)? HeardSince(
        JournalPosition since, IReadOnlyCollection<string> roots, out string? doubt)
    {
        ThrowHelper.ThrowIfNull(since);
        ThrowHelper.ThrowIfNull(roots);
        return Heard(since, roots, out _, out _, out doubt);
    }

    private (IReadOnlyList<(string Path, uint Flags, ulong Id)> Replayed, string Mark)? Heard(
        JournalPosition since,
        IReadOnlyCollection<string> roots,
        out List<(string Root, string Real)> real,
        out string? marks,
        out string? doubt)
    {
        real = [];
        marks = null;

        if (since.Usn < 0 || (ulong)since.Usn > FSEventsGetCurrentEventId())
        {
            doubt = "the place is ahead of the history";
            return null;
        }

        // Each root as the history spells it: no link above it, as the
        // system resolves a path before it records it.
        foreach (var root in roots)
        {
            if (!DarwinInterop.TryStat(root, out var stat) || !stat.IsDirectory)
            {
                doubt = $"'{root}' is not a folder";
                return null;
            }

            if (HistoryOf(stat.Device) is not { } history || history.Volume != since.Volume || history.Id != since.JournalId)
            {
                doubt = $"'{root}' is not on the history the place is in";
                return null;
            }

            if (RealPath(root) is not { } resolved)
            {
                doubt = $"'{root}' could not be resolved";
                return null;
            }

            real.Add((root, resolved));
        }

        Directory.CreateDirectory(_marks);
        marks = RealPath(_marks);
        if (marks is null)
        {
            doubt = $"the folder for marks, '{_marks}', could not be resolved";
            return null;
        }

        var mark = Path.Combine(marks, Guid.NewGuid().ToString("n"));
        try
        {
            // Heard as it happens, before the history is read: by then the
            // system has numbered every change made before the question.
            if (Hear(marks, mark) is { } unheard)
            {
                doubt = unheard;
                return null;
            }

            // A change numbered before the mark is in a history holding it.
            var waited = Stopwatch.StartNew();
            while (true)
            {
                if (Replay([.. real.Select(pair => pair.Real), marks], (ulong)since.Usn, out doubt) is not { } replayed)
                {
                    return null;
                }

                var read = AsRead is { } asRead ? asRead(replayed) : replayed;
                if (FsEventsRecords.Holds(read.Select(heard => heard.Path), mark))
                {
                    return (read, mark);
                }

                if (waited.Elapsed >= HoldTimeout)
                {
                    doubt = $"the history did not hold the mark heard before it was read, '{mark}'";
                    return null;
                }

                Thread.Sleep(HoldPause);
            }
        }
        finally
        {
            try
            {
                File.Delete(mark);
            }
            catch (IOException)
            {
            }
        }
    }

    // As the folders are spelled on disk: the nearest that is still there,
    // resolved, and the rest as heard, which name nothing a walk enters.
    private static string? SpelledOnDisk(string path)
    {
        var rest = new Stack<string>();
        var at = path;
        while (true)
        {
            if (RealPath(at) is { } resolved)
            {
                return rest.Count == 0 ? resolved : resolved.TrimEnd('/') + "/" + string.Join('/', rest);
            }

            var slash = at.LastIndexOf('/');
            if (slash <= 0)
            {
                return null;
            }

            rest.Push(at[(slash + 1)..]);
            at = at[..slash];
        }
    }

    private static (string Volume, ulong Id)? HistoryOf(ulong device)
    {
        var uuid = FSEventsCopyUUIDForDevice(unchecked((int)(uint)device));
        if (uuid == 0)
        {
            return null;
        }

        try
        {
            var text = CFUUIDCreateString(0, uuid);
            if (text == 0)
            {
                return null;
            }

            try
            {
                if (StringOf(text) is not { } spelled)
                {
                    return null;
                }

                return FsEventsRecords.History(spelled);
            }
            finally
            {
                CFRelease(text);
            }
        }
        finally
        {
            CFRelease(uuid);
        }
    }

    private static unsafe string? StringOf(nint cfString)
    {
        var buffer = stackalloc byte[256];
        return CFStringGetCString(cfString, buffer, 256, Utf8Encoding)
            ? Marshal.PtrToStringUTF8((nint)buffer)
            : null;
    }

    // Into a buffer of PATH_MAX, which every version of realpath takes.
    private static unsafe string? RealPath(string path)
    {
        var buffer = stackalloc byte[1024];
        return NativeRealPath(path, buffer) == 0 ? null : Marshal.PtrToStringUTF8((nint)buffer);
    }

    /// <summary>
    /// Every event the history holds under <paramref name="paths"/> since
    /// <paramref name="since"/>, as far as the system says its replay is
    /// done; or null with why.
    /// </summary>
    private static List<(string Path, uint Flags, ulong Id)>? Replay(IReadOnlyList<string> paths, ulong since, out string? doubt)
    {
        var collector = new Collector();
        doubt = Streamed(
            paths,
            since,
            collector,
            () => collector.Replayed.Wait(ReplayTimeout) ? null : "the history was not replayed in time");
        if (doubt is not null)
        {
            return null;
        }

        lock (collector)
        {
            return [.. collector.Events];
        }
    }

    /// <summary>
    /// Null once <paramref name="mark"/>, written in <paramref name="marks"/>
    /// when a stream over that folder is open, comes back through it; or why
    /// it did not.
    /// </summary>
    private static string? Hear(string marks, string mark)
    {
        var collector = new Collector();
        return Streamed(
            [marks],
            FSEventsGetCurrentEventId(),
            collector,
            () =>
            {
                // The replay done says the stream is open, so the mark is
                // heard as it happens.
                if (!collector.Replayed.Wait(ReplayTimeout))
                {
                    return "the history was not replayed in time";
                }

                lock (collector)
                {
                    collector.Mark = mark;
                }

                File.WriteAllBytes(mark, []);
                return collector.Marked.Wait(MarkTimeout) ? null : "the history did not hear its own mark in time";
            });
    }

    /// <summary>
    /// Runs <paramref name="whileOpen"/> while a stream over
    /// <paramref name="paths"/> from <paramref name="since"/> gives
    /// <paramref name="collector"/> what it hears, and returns what it
    /// returns, or why the stream could not be opened.
    /// </summary>
    private static unsafe string? Streamed(IReadOnlyList<string> paths, ulong since, Collector collector, Func<string?> whileOpen)
    {
        var handle = GCHandle.Alloc(collector);
        var strings = new List<nint>();
        nint array = 0;
        nint stream = 0;
        nint queue = 0;
        try
        {
            foreach (var path in paths)
            {
                var text = CFStringCreateWithCString(0, path, Utf8Encoding);
                if (text == 0)
                {
                    return $"'{path}' could not be given to the history";
                }

                strings.Add(text);
            }

            fixed (nint* values = strings.ToArray())
            {
                array = CFArrayCreate(0, values, strings.Count, ArrayCallbacks.Value);
            }

            var context = new StreamContext { Info = GCHandle.ToIntPtr(handle) };
            stream = FSEventStreamCreate(
                0, &Heard, ref context, array, since, 0, CreateNoDefer | CreateWatchRoot | CreateFileEvents);
            if (stream == 0)
            {
                return "the history could not be opened";
            }

            queue = NativeQueueCreate("fallbackplan.change-history", 0);
            FSEventStreamSetDispatchQueue(stream, queue);
            if (!FSEventStreamStart(stream))
            {
                return "the history could not be read";
            }

            return whileOpen();
        }
        finally
        {
            if (stream != 0)
            {
                FSEventStreamStop(stream);
                FSEventStreamInvalidate(stream);
            }

            // Nothing the queue still holds runs once this returns.
            if (queue != 0)
            {
                NativeQueueSync(queue, 0, &Settled);
            }

            if (stream != 0)
            {
                FSEventStreamRelease(stream);
            }

            if (queue != 0)
            {
                NativeRelease(queue);
            }

            if (array != 0)
            {
                CFRelease(array);
            }

            foreach (var text in strings)
            {
                CFRelease(text);
            }

            handle.Free();
        }
    }

    // What a stream gives is kept until its replay is done, and after that
    // only while a mark is awaited, up to the mark.
    [UnmanagedCallersOnly]
    private static unsafe void Heard(nint stream, nint info, nuint count, nint paths, uint* flags, ulong* ids)
    {
        try
        {
            var collector = (Collector)GCHandle.FromIntPtr(info).Target!;
            var names = (byte**)paths;
            for (nuint i = 0; i < count; i++)
            {
                if ((flags[i] & FsEventsRecords.HistoryDone) != 0)
                {
                    collector.Replayed.Set();
                    continue;
                }

                var path = Marshal.PtrToStringUTF8((nint)names[i]) ?? string.Empty;
                lock (collector)
                {
                    if (collector.Marked.IsSet || (collector.Replayed.IsSet && collector.Mark is null))
                    {
                        continue;
                    }

                    collector.Events.Add((path, flags[i], ids[i]));
                    if (collector.Mark is { } mark && FsEventsRecords.IsMark(path, mark))
                    {
                        collector.Marked.Set();
                    }
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // A callback must not throw into the system's thread.
        }
    }

    [UnmanagedCallersOnly]
    private static void Settled(nint context)
    {
    }

    private sealed class Collector
    {
        public List<(string Path, uint Flags, ulong Id)> Events { get; } = [];

        public ManualResetEventSlim Replayed { get; } = new();

        public ManualResetEventSlim Marked { get; } = new();

        public string? Mark { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamContext
    {
        public nint Version;
        public nint Info;
        public nint Retain;
        public nint Release;
        public nint CopyDescription;
    }

    [LibraryImport(CoreServices)]
    private static partial ulong FSEventsGetCurrentEventId();

    [LibraryImport(CoreServices)]
    private static partial nint FSEventsCopyUUIDForDevice(int device);

    [LibraryImport(CoreServices)]
    private static unsafe partial nint FSEventStreamCreate(
        nint allocator,
        delegate* unmanaged<nint, nint, nuint, nint, uint*, ulong*, void> callback,
        ref StreamContext context,
        nint pathsToWatch,
        ulong sinceWhen,
        double latency,
        uint flags);

    [LibraryImport(CoreServices)]
    private static partial void FSEventStreamSetDispatchQueue(nint stream, nint queue);

    [LibraryImport(CoreServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool FSEventStreamStart(nint stream);

    [LibraryImport(CoreServices)]
    private static partial void FSEventStreamStop(nint stream);

    [LibraryImport(CoreServices)]
    private static partial void FSEventStreamInvalidate(nint stream);

    [LibraryImport(CoreServices)]
    private static partial void FSEventStreamRelease(nint stream);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFUUIDCreateString(nint allocator, nint uuid);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static unsafe partial bool CFStringGetCString(nint text, byte* buffer, nint size, uint encoding);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static unsafe partial nint CFArrayCreate(nint allocator, nint* values, nint count, nint callbacks);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);

    [LibraryImport(LibSystem, EntryPoint = "dispatch_queue_create", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint NativeQueueCreate(string label, nint attributes);

    [LibraryImport(LibSystem, EntryPoint = "dispatch_sync_f")]
    private static unsafe partial void NativeQueueSync(nint queue, nint context, delegate* unmanaged<nint, void> work);

    [LibraryImport(LibSystem, EntryPoint = "dispatch_release")]
    private static partial void NativeRelease(nint value);

    [LibraryImport(LibSystem, EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial nint NativeRealPath(string path, byte* resolved);
}
