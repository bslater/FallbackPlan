using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Bodu;

namespace FallbackPlan.Filesystem.Local;

/// <summary>
/// What changed in a set's folders on Linux (ADR-0190): the service's own
/// inotify watch of them, kept while it runs. Linux keeps no journal of its
/// own, so the first place asked for a folder starts watching it, and a place
/// is good only to the watch that gave it: a service started again, a queue
/// that overflowed, or anything else that loses track gives a new session,
/// and every place from before it is answered with doubt.
/// </summary>
/// <remarks>
/// <para>
/// One watch per folder, as inotify gives one per inode, held in one tree
/// whatever set asked, so a set's folders inside another set's are watched
/// once. A folder on another filesystem than the one above it is not watched,
/// as capture does not walk into it.
/// </para>
/// <para>
/// A change names the folder it happened in, and a folder made or moved in is
/// named whole, every folder below it too, since nothing was watching it as
/// it was filled. A change to a file with another name cannot be answered:
/// Linux keeps no list of a file's names, so its other folders cannot be
/// found.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxChangeJournal : IChangeJournal, IDisposable
{
    /// <summary>
    /// How many changed folders the watch keeps before letting go of the
    /// older half, after which a place older than what it kept is doubt.
    /// </summary>
    public const int MostFoldersKept = 100_000;

    private const uint InModify = 0x2;
    private const uint InAttrib = 0x4;
    private const uint InCloseWrite = 0x8;
    private const uint InMovedFrom = 0x40;
    private const uint InMovedTo = 0x80;
    private const uint InCreate = 0x100;
    private const uint InDelete = 0x200;
    private const uint InDeleteSelf = 0x400;
    private const uint InMoveSelf = 0x800;
    private const uint InUnmount = 0x2000;
    private const uint InQueueOverflow = 0x4000;
    private const uint InIgnored = 0x8000;
    private const uint InOnlyDir = 0x0100_0000;
    private const uint InDontFollow = 0x0200_0000;
    private const uint InExcludeUnlinked = 0x0400_0000;
    private const uint InIsDirectory = 0x4000_0000;

    private const uint WatchMask = InModify | InAttrib | InCloseWrite | InMovedFrom | InMovedTo | InCreate | InDelete
        | InDeleteSelf | InMoveSelf | InOnlyDir | InDontFollow | InExcludeUnlinked;

    private const uint FileChanges = InModify | InAttrib | InCloseWrite | InCreate | InMovedTo;

    private const int NonBlocking = 0x800;
    private const int CloseOnExec = 0x80000;
    private const int TryAgain = 11;
    private const int Interrupted = 4;
    private const int NoSpace = 28;
    private const int PollIn = 0x1;

    private static readonly Lazy<LinuxChangeJournal> SharedJournal = new(() => new LinuxChangeJournal());

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Filesystems whose changes are made elsewhere, where no watch here hears them.
    private static readonly HashSet<long> RemoteFilesystems =
    [
        0x6969, 0x517B, 0xFF534D42, 0xFE534D42, 0x65735546, 0x01021997, 0x00C36400, 0x5346414F, 0x73757245,
        0x564C, 0x01161970, 0x0BD00BD0, 0x7461636F, 0x47504653,
    ];

    private readonly Lock _gate = new();
    private readonly bool _readsInBackground;
    private readonly int _mostFoldersKept;
    private readonly Dictionary<int, Node> _watches = [];
    private readonly List<Node> _tops = [];
    private readonly Dictionary<string, Root> _roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _changed = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, (Node Node, string Was)> _movedFrom = [];
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _fd = -1;
    private string? _broken;
    private string? _limit;
    private long _keptFrom;
    private long _sequence;
    private Thread? _reader;
    private volatile bool _disposed;

    /// <summary>A watch whose reader keeps its queue drained as changes come.</summary>
    public LinuxChangeJournal()
        : this(readsInBackground: true)
    {
    }

    /// <summary>
    /// A watch that reads its queue only when asked, so a test can fill it
    /// past what the kernel keeps, and that keeps as few changed folders as
    /// a test can reach.
    /// </summary>
    internal LinuxChangeJournal(bool readsInBackground, int mostFoldersKept = MostFoldersKept)
    {
        _readsInBackground = readsInBackground;
        _mostFoldersKept = mostFoldersKept;
    }

    /// <summary>The one watch a service process keeps for all its sets.</summary>
    public static LinuxChangeJournal Shared => SharedJournal.Value;

    /// <inheritdoc />
    public JournalPosition? PositionOf(string folder)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(folder);
        var path = Normal(folder);
        lock (_gate)
        {
            if (!Open() || _failed.ContainsKey(path))
            {
                return null;
            }

            Drain();
            if (!_roots.TryGetValue(path, out var root))
            {
                if (Watch(path) is not { } watched)
                {
                    return null;
                }

                Drain();
                root = watched;
            }

            return new JournalPosition(root.Path, root.Session, _sequence);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ChangedFolder>? ChangedSince(JournalPosition since, IReadOnlyCollection<string> roots) =>
        ChangedSince(since, roots, out _);

    /// <summary>
    /// The folders that changed under <paramref name="roots"/> since
    /// <paramref name="since"/>, or null with <paramref name="doubt"/> saying
    /// why the watch cannot say.
    /// </summary>
    public IReadOnlyList<ChangedFolder>? ChangedSince(
        JournalPosition since, IReadOnlyCollection<string> roots, out string? doubt)
    {
        ThrowHelper.ThrowIfNull(since);
        ThrowHelper.ThrowIfNull(roots);
        lock (_gate)
        {
            if (_fd < 0)
            {
                doubt = _broken ?? "nothing is being watched";
                return null;
            }

            Drain();
            if (_broken is not null)
            {
                doubt = _broken;
                return null;
            }

            var named = new List<ChangedFolder>();
            foreach (var asked in roots)
            {
                var path = Normal(asked);
                if (!_roots.TryGetValue(path, out var root))
                {
                    doubt = $"'{path}' is no longer watched";
                    return null;
                }

                if (!string.Equals(since.Volume, root.Path, StringComparison.Ordinal) || since.JournalId != root.Session)
                {
                    doubt = root.Doubt is { } lost
                        ? $"the watch of '{path}' lost track since: {lost}"
                        : $"the place is not this watch's of '{path}'";
                    return null;
                }

                if (since.Usn > _sequence)
                {
                    doubt = "the place is ahead of the watch";
                    return null;
                }

                if (since.Usn < _keptFrom)
                {
                    doubt = "the place is older than the changes the watch still keeps";
                    return null;
                }

                foreach (var (folder, at) in _changed)
                {
                    if (at > since.Usn && Below(root.Path, folder) is { } relative)
                    {
                        named.Add(new ChangedFolder(asked, relative));
                    }
                }
            }

            doubt = null;
            return named;
        }
    }

    /// <summary>Why <paramref name="folder"/> has no place, or null where it has one or could.</summary>
    public string? WhyNoPosition(string folder)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(folder);
        var path = Normal(folder);
        lock (_gate)
        {
            if (_broken is not null)
            {
                return _broken;
            }

            if (_failed.TryGetValue(path, out var failure))
            {
                return failure;
            }

            return _roots.ContainsKey(path) ? null : Unwatchable(path);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _reader?.Join();
        lock (_gate)
        {
            if (_fd >= 0)
            {
                _ = NativeClose(_fd);
                _fd = -1;
            }
        }
    }

    private static string Normal(string folder)
    {
        var full = Path.GetFullPath(folder);
        return full.Length > 1 ? full.TrimEnd('/') : full;
    }

    private static string? Below(string root, string folder)
    {
        if (string.Equals(root, folder, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var prefix = root == "/" ? root : root + "/";
        return folder.StartsWith(prefix, StringComparison.Ordinal) ? folder[prefix.Length..] : null;
    }

    private static string? Unwatchable(string path)
    {
        if (!LinuxInterop.TryStat(path, out var stat) || !stat.IsDirectory)
        {
            return $"'{path}' is not a folder";
        }

        var buffer = new byte[256];
        if (NativeStatfs(path, buffer) == 0 && RemoteFilesystems.Contains(BinaryPrimitives.ReadInt64LittleEndian(buffer)))
        {
            return $"'{path}' is on a filesystem changed from elsewhere, which no watch here hears";
        }

        return null;
    }

    private bool Open()
    {
        // A watch that broke gives no place again: what it missed is unknown.
        if (_broken is not null || _disposed)
        {
            return false;
        }

        if (_fd >= 0)
        {
            return true;
        }

        var fd = NativeInotifyInit(NonBlocking | CloseOnExec);
        if (fd < 0)
        {
            _broken = $"inotify could not be opened (errno {Marshal.GetLastPInvokeError()})";
            return false;
        }

        _fd = fd;
        if (_readsInBackground)
        {
            _reader = new Thread(Read) { IsBackground = true, Name = "change watch" };
            _reader.Start();
        }

        return true;
    }

    private void Read()
    {
        var poll = new PollFd { Fd = _fd, Events = PollIn };
        while (!_disposed && _broken is null)
        {
            if (NativePoll(ref poll, 1, 250) > 0)
            {
                lock (_gate)
                {
                    if (_fd >= 0)
                    {
                        Drain();
                    }
                }
            }
        }
    }

    private Root? Watch(string path)
    {
        if (Unwatchable(path) is not null || !LinuxInterop.TryStat(path, out var stat))
        {
            return null;
        }

        var added = new List<int>();
        if (Find(path) is null)
        {
            var top = Add(parent: null, path, path, stat.Device, added, name: false);
            if (top is null && added.Count == 0)
            {
                Fail(path, added, $"'{path}' could not be watched");
                return null;
            }
        }

        if (_broken is not null)
        {
            return null;
        }

        if (_limit is { } limit)
        {
            _limit = null;
            Fail(path, added, limit);
            return null;
        }

        var root = new Root(path) { Session = NewSession() };
        _roots[path] = root;
        return root;
    }

    private void Fail(string path, List<int> added, string reason)
    {
        foreach (var watch in added)
        {
            if (_watches.Remove(watch, out var node))
            {
                Detach(node);
                _ = NativeRemoveWatch(_fd, watch);
            }
        }

        _failed[path] = reason;
    }

    /// <summary>
    /// Watches the folder at <paramref name="path"/> and every folder below it
    /// on its filesystem, naming each where <paramref name="name"/> says it is
    /// new to the watch. Null where the folder could not be watched.
    /// </summary>
    private Node? Add(Node? parent, string childName, string path, ulong device, List<int> added, bool name)
    {
        var watch = NativeAddWatch(_fd, path, WatchMask);
        if (watch < 0)
        {
            if (Marshal.GetLastPInvokeError() == NoSpace)
            {
                _limit = "the system's limit on inotify watches (fs.inotify.max_user_watches) is reached";
            }

            return null;
        }

        if (_watches.TryGetValue(watch, out var existing))
        {
            // A folder another set's watch already holds: it becomes a child
            // here, already watched below.
            if (existing.Parent is null && parent is not null)
            {
                _tops.Remove(existing);
                existing.Parent = parent;
                existing.Name = childName;
                parent.Children[childName] = existing;
            }

            if (name)
            {
                NameTree(existing);
            }

            return existing;
        }

        var node = new Node(watch, parent, childName, device);
        _watches[watch] = node;
        added.Add(watch);
        if (parent is null)
        {
            _tops.Add(node);
        }
        else
        {
            parent.Children[childName] = node;
        }

        if (name)
        {
            Name(PathOf(node));
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };
        try
        {
            foreach (var child in Directory.EnumerateDirectories(path, "*", options))
            {
                if (_limit is not null)
                {
                    break;
                }

                if (LinuxInterop.TryStat(child, out var stat) && stat.IsDirectory && stat.Device == device)
                {
                    _ = Add(node, Path.GetFileName(child), child, device, added, name);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Gone or unreadable as it was listed: capture finds it so too.
        }

        return node;
    }

    private void Drain()
    {
        if (_fd < 0 || _broken is not null)
        {
            return;
        }

        var stated = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var read = NativeRead(_fd, _buffer, (nuint)_buffer.Length);
            if (read < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == Interrupted)
                {
                    continue;
                }

                if (error != TryAgain)
                {
                    _broken = $"inotify could not be read (errno {error})";
                    LoseTrack(null, _broken);
                }

                break;
            }

            if (read == 0)
            {
                break;
            }

            var offset = 0;
            while (offset + 16 <= read)
            {
                var span = _buffer.AsSpan(offset);
                var watch = BinaryPrimitives.ReadInt32LittleEndian(span);
                var mask = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                var cookie = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
                var nameBytes = span.Slice(16, length);
                var end = nameBytes.IndexOf((byte)0);
                offset += 16 + length;
                Event(watch, mask, cookie, end < 0 ? nameBytes : nameBytes[..end], stated);
            }
        }

        // A folder moved away with nowhere here to land: out of the watch.
        foreach (var (_, (gone, was)) in _movedFrom)
        {
            Forget(gone, was);
        }

        _movedFrom.Clear();
        if (_changed.Count > _mostFoldersKept)
        {
            // The older half goes, and with it any place from before them.
            var cut = _changed.Values.Order().ElementAt(_changed.Count / 2);
            foreach (var (folder, at) in _changed.ToList())
            {
                if (at <= cut)
                {
                    _changed.Remove(folder);
                }
            }

            _keptFrom = Math.Max(_keptFrom, cut);
        }
    }

    private void Event(int watch, uint mask, uint cookie, ReadOnlySpan<byte> nameBytes, HashSet<string> stated)
    {
        _sequence++;
        if ((mask & InQueueOverflow) != 0)
        {
            LoseTrack(null, "the watch's queue overflowed, so changes were lost");
            return;
        }

        if (!_watches.TryGetValue(watch, out var node))
        {
            return;
        }

        if ((mask & InIgnored) != 0)
        {
            _watches.Remove(watch);
            Detach(node);
            return;
        }

        var folder = PathOf(node);
        // A folder moved here and not yet landed is settled once the queue is
        // read; only a watched top moving is a root gone.
        if ((mask & (InDeleteSelf | InUnmount)) != 0 || ((mask & InMoveSelf) != 0 && _tops.Contains(node)))
        {
            RootsGone(folder);
            if ((mask & InMoveSelf) != 0)
            {
                Forget(node, folder);
            }

            return;
        }

        if ((mask & InMoveSelf) != 0)
        {
            return;
        }

        Name(folder);
        if (nameBytes.IsEmpty)
        {
            return;
        }

        string name;
        try
        {
            name = StrictUtf8.GetString(nameBytes);
        }
        catch (DecoderFallbackException)
        {
            LoseTrack(folder, $"a name in '{folder}' is not UTF-8");
            return;
        }

        var path = folder == "/" ? "/" + name : folder + "/" + name;
        if ((mask & InIsDirectory) == 0)
        {
            if ((mask & FileChanges) != 0 && stated.Add(path)
                && LinuxInterop.TryStat(path, out var file) && file.IsRegularFile && file.LinkCount > 1)
            {
                LoseTrack(path, $"a file with another name changed: '{path}'");
            }

            return;
        }

        // A folder gone from here is named by the folder that held it, not
        // where it no longer is.
        if ((mask & InMovedFrom) != 0)
        {
            if (node.Children.Remove(name, out var moving))
            {
                moving.Parent = null;
                _movedFrom[cookie] = (moving, path);
            }

            return;
        }

        if ((mask & InDelete) != 0)
        {
            node.Children.Remove(name);
            return;
        }

        Name(path);
        if ((mask & InMovedTo) != 0 && _movedFrom.Remove(cookie, out var moved))
        {
            moved.Node.Parent = node;
            moved.Node.Name = name;
            node.Children[name] = moved.Node;
            NameTree(moved.Node);

            // A set's root moved with it is no longer where its set says.
            foreach (var root in _roots.Values.ToList())
            {
                if (Below(moved.Was, root.Path) is not null)
                {
                    _roots.Remove(root.Path);
                }
            }
        }
        else if ((mask & (InMovedTo | InCreate)) != 0)
        {
            _ = Add(node, name, path, node.Device, [], name: true);
            if (_limit is { } limit)
            {
                // Part of what is below can no longer be watched, so no place
                // there can be answered again until the service starts anew.
                _limit = null;
                foreach (var root in _roots.Values.ToList())
                {
                    if (Below(root.Path, path) is not null)
                    {
                        _roots.Remove(root.Path);
                        _failed[root.Path] = limit;
                    }
                }
            }
        }
    }

    private void Name(string folder) => _changed[folder] = _sequence;

    private void NameTree(Node node)
    {
        Name(PathOf(node));
        foreach (var child in node.Children.Values)
        {
            NameTree(child);
        }
    }

    private void RootsGone(string folder)
    {
        foreach (var root in _roots.Values.ToList())
        {
            if (Below(folder, root.Path) is not null)
            {
                _roots.Remove(root.Path);
            }
        }
    }

    private void LoseTrack(string? at, string reason)
    {
        foreach (var root in _roots.Values)
        {
            if (at is null || Below(root.Path, at) is not null)
            {
                root.Session = NewSession();
                root.Doubt = reason;
            }
        }
    }

    private void Forget(Node node, string path)
    {
        foreach (var (name, child) in node.Children.ToList())
        {
            Forget(child, path + "/" + name);
        }

        if (_watches.Remove(node.Watch))
        {
            _ = NativeRemoveWatch(_fd, node.Watch);
        }

        Detach(node);
        RootsGone(path);
    }

    private void Detach(Node node)
    {
        if (node.Parent is { } parent)
        {
            if (parent.Children.TryGetValue(node.Name, out var held) && ReferenceEquals(held, node))
            {
                parent.Children.Remove(node.Name);
            }
        }
        else
        {
            _tops.Remove(node);
        }
    }

    private Node? Find(string path)
    {
        foreach (var top in _tops)
        {
            if (Below(top.Name, path) is not { } relative)
            {
                continue;
            }

            var node = top;
            foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!node.Children.TryGetValue(part, out var child))
                {
                    return null;
                }

                node = child;
            }

            return node;
        }

        return null;
    }

    private static string PathOf(Node node)
    {
        if (node.Parent is null)
        {
            return node.Name;
        }

        var above = PathOf(node.Parent);
        return above == "/" ? "/" + node.Name : above + "/" + node.Name;
    }

    private static ulong NewSession() => BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));

    private sealed class Root(string path)
    {
        public string Path { get; } = path;

        public ulong Session { get; set; }

        public string? Doubt { get; set; }
    }

    private sealed class Node(int watch, Node? parent, string name, ulong device)
    {
        public int Watch { get; } = watch;

        public Node? Parent { get; set; } = parent;

        // A folder's name in its parent, or the whole path of a folder no
        // watched folder holds.
        public string Name { get; set; } = name;

        public ulong Device { get; } = device;

        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Returned;
    }

    [LibraryImport("libc", EntryPoint = "inotify_init1", SetLastError = true)]
    private static partial int NativeInotifyInit(int flags);

    [LibraryImport("libc", EntryPoint = "inotify_add_watch", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeAddWatch(int fd, string path, uint mask);

    [LibraryImport("libc", EntryPoint = "inotify_rm_watch", SetLastError = true)]
    private static partial int NativeRemoveWatch(int fd, int watch);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint NativeRead(int fd, byte[] buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int NativePoll(ref PollFd fds, nuint count, int timeout);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int NativeClose(int fd);

    [LibraryImport("libc", EntryPoint = "statfs", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeStatfs(string path, byte[] buffer);
}
