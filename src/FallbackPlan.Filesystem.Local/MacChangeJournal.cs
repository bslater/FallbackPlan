using System.Globalization;
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
/// they are made, so an answer is not given until a mark this process
/// writes, after the history has been replayed, comes back through the same
/// stream: every change made before it has been numbered and heard by then.
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

    private const uint MustScanSubDirs = 0x01;
    private const uint UserDropped = 0x02;
    private const uint KernelDropped = 0x04;
    private const uint EventIdsWrapped = 0x08;
    private const uint HistoryDone = 0x10;
    private const uint RootChanged = 0x20;
    private const uint Mount = 0x40;
    private const uint Unmount = 0x80;
    private const uint ItemIsFile = 0x10000;
    private const uint ItemIsHardlink = 0x100000;

    private const uint LostTrack = MustScanSubDirs | UserDropped | KernelDropped | EventIdsWrapped | RootChanged | Mount | Unmount;

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

        if (since.Usn < 0 || (ulong)since.Usn > FSEventsGetCurrentEventId())
        {
            doubt = "the place is ahead of the history";
            return null;
        }

        // Each root as the history spells it: no link above it, as the
        // system resolves a path before it records it.
        var real = new List<(string Root, string Real)>();
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
        if (RealPath(_marks) is not { } marks)
        {
            doubt = $"the folder for marks, '{_marks}', could not be resolved";
            return null;
        }

        if (Replay([.. real.Select(pair => pair.Real), marks], (ulong)since.Usn, marks, out doubt) is not { } events)
        {
            return null;
        }

        var named = new HashSet<ChangedFolder>();
        foreach (var (path, flags) in events)
        {
            if ((flags & LostTrack) != 0)
            {
                doubt = string.Create(CultureInfo.InvariantCulture, $"the history lost track at '{path}' (flags 0x{flags:X})");
                return null;
            }

            if (Below(marks, path) is not null)
            {
                continue;
            }

            // The system marks a file it knows has another name; one it does
            // not mark is asked as it is now.
            if ((flags & ItemIsFile) != 0
                && ((flags & ItemIsHardlink) != 0 || (DarwinInterop.TryStat(path, out var file) && file.IsRegularFile && file.LinkCount > 1)))
            {
                doubt = $"a file with another name changed: '{path}'";
                return null;
            }

            // A file names the folder holding it; anything else names itself,
            // and a run walks every folder above a path it is told of.
            var at = path.TrimEnd('/');
            if ((flags & ItemIsFile) != 0)
            {
                at = Path.GetDirectoryName(at) ?? at;
            }

            foreach (var (root, resolved) in real)
            {
                if (Below(resolved, at) is { } relative)
                {
                    named.Add(new ChangedFolder(root, relative));
                }
            }
        }

        doubt = null;
        return [.. named];
    }

    // Folders on the volumes macOS makes are named without regard to case,
    // as the history spells them; a sibling differing only in case on a
    // volume that does regard it is over-named, which costs a walk.
    private static string? Below(string root, string path)
    {
        var trimmed = path.TrimEnd('/');
        if (string.Equals(root, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var prefix = root.EndsWith('/') ? root : root + "/";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? trimmed[prefix.Length..] : null;
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

                var hex = spelled.Replace("-", string.Empty, StringComparison.Ordinal);
                return ($"fsevents:{spelled}", ulong.Parse(hex.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
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
    /// Every event in the history under <paramref name="paths"/> since
    /// <paramref name="since"/>, and every one heard after it until a mark
    /// written in <paramref name="marks"/> comes back; or null with why.
    /// </summary>
    private unsafe List<(string Path, uint Flags)>? Replay(
        IReadOnlyList<string> paths, ulong since, string marks, out string? doubt)
    {
        var collector = new Collector();
        var handle = GCHandle.Alloc(collector);
        var strings = new List<nint>();
        nint array = 0;
        nint stream = 0;
        nint queue = 0;
        string? mark = null;
        try
        {
            foreach (var path in paths)
            {
                var text = CFStringCreateWithCString(0, path, Utf8Encoding);
                if (text == 0)
                {
                    doubt = $"'{path}' could not be given to the history";
                    return null;
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
                doubt = "the history could not be opened";
                return null;
            }

            queue = NativeQueueCreate("fallbackplan.change-history", 0);
            FSEventStreamSetDispatchQueue(stream, queue);
            if (!FSEventStreamStart(stream))
            {
                doubt = "the history could not be read";
                return null;
            }

            if (!collector.Replayed.Wait(ReplayTimeout))
            {
                doubt = "the history was not replayed in time";
                return null;
            }

            // Every change made before the mark has been numbered and heard
            // once the mark is heard.
            mark = Path.Combine(marks, Guid.NewGuid().ToString("n"));
            lock (collector)
            {
                collector.Mark = mark;
            }

            File.WriteAllBytes(mark, []);
            if (!collector.Marked.Wait(MarkTimeout))
            {
                doubt = "the history did not hear its own mark in time";
                return null;
            }

            doubt = null;
            lock (collector)
            {
                return [.. collector.Events];
            }
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
            if (mark is not null)
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
    }

    [UnmanagedCallersOnly]
    private static unsafe void Heard(nint stream, nint info, nuint count, nint paths, uint* flags, ulong* ids)
    {
        try
        {
            var collector = (Collector)GCHandle.FromIntPtr(info).Target!;
            var names = (byte**)paths;
            for (nuint i = 0; i < count; i++)
            {
                if ((flags[i] & HistoryDone) != 0)
                {
                    collector.Replayed.Set();
                    continue;
                }

                var path = Marshal.PtrToStringUTF8((nint)names[i]) ?? string.Empty;
                lock (collector)
                {
                    if (collector.Marked.IsSet)
                    {
                        return;
                    }

                    collector.Events.Add((path, flags[i]));
                    if (collector.Mark is { } mark && string.Equals(path.TrimEnd('/'), mark, StringComparison.OrdinalIgnoreCase))
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
        public List<(string Path, uint Flags)> Events { get; } = [];

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
