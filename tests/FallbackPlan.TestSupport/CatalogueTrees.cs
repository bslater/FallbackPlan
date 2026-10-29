using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Records a snapshot's tree in a catalogue from the paths a test names, as
/// the catalogue keeps it: each folder's entries under the folder's tree
/// (ADR-0151).
/// </summary>
/// <remarks>
/// A folder a test names as an entry keeps the object it is given as its
/// tree. One a path only passes through is given a tree derived from what
/// it holds, as a repository's tree is, so two snapshots that hold the same
/// folder share its entries as they would from real trees.
/// </remarks>
public static class CatalogueTrees
{
    /// <summary>
    /// Records <paramref name="entries"/> under the root tree
    /// <paramref name="rootTree"/>, or under one derived from what the root
    /// holds when it is null, and answers the root.
    /// </summary>
    public static ObjectId Record(
        Catalogue catalogue,
        IEnumerable<(string Path, EntryKind Kind, ObjectId ObjectId)> entries,
        ObjectId? rootTree = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(entries);

        var folders = new Dictionary<string, Folder>(StringComparer.Ordinal) { [string.Empty] = new(rootTree) };
        foreach (var (path, kind, objectId) in entries)
        {
            var slash = path.LastIndexOf('/');
            var parent = slash < 0 ? string.Empty : path[..slash];
            FolderAt(parent).Children[path[(slash + 1)..]] = (kind, objectId, kind == EntryKind.DirectoryPlaceholder ? path : null);
            if (kind == EntryKind.DirectoryPlaceholder)
            {
                FolderAt(path).Tree = objectId;
            }
        }

        return TreeOf(string.Empty);

        Folder FolderAt(string path)
        {
            if (!folders.TryGetValue(path, out var folder))
            {
                folders[path] = folder = new Folder(null);
                var slash = path.LastIndexOf('/');
                var parent = slash < 0 ? string.Empty : path[..slash];
                var name = path[(slash + 1)..];
                if (!FolderAt(parent).Children.ContainsKey(name))
                {
                    FolderAt(parent).Children[name] = (EntryKind.DirectoryPlaceholder, default, path);
                }
            }

            return folder;
        }

        ObjectId TreeOf(string path)
        {
            var folder = folders[path];
            var children = folder.Children
                .OrderBy(child => child.Key, StringComparer.Ordinal)
                .Select(child => new CatalogueFolderEntry(
                    child.Key, child.Value.Kind, child.Value.Folder is { } inner ? TreeOf(inner) : child.Value.ObjectId))
                .ToList();

            var tree = folder.Tree ??= Derived(children);
            catalogue.RecordFolder(tree, children);
            return tree;
        }
    }

    /// <summary>
    /// Records snapshot <paramref name="snapshotId"/> as holding
    /// <paramref name="entries"/>, its row included, and answers its root.
    /// </summary>
    public static ObjectId RecordSnapshot(
        Catalogue catalogue,
        ReadOnlySpan<byte> snapshotId,
        params (string Path, EntryKind Kind, ObjectId ObjectId)[] entries)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var root = Record(catalogue, entries);
        catalogue.RecordSnapshot(
            snapshotId, new byte[16], new byte[16], ObjectId.FromBytes(SHA256.HashData(snapshotId)), root,
            publicationGeneration: 0, captureStatus: 1, signatureState: 1);
        return root;
    }

    /// <summary>A tree for a folder no test named, from what it holds.</summary>
    internal static ObjectId Derived(IReadOnlyList<CatalogueFolderEntry> children)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("fbp-test-tree"u8);
        foreach (var child in children)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(child.Name));
            hash.AppendData([0, (byte)child.Kind]);
            hash.AppendData(child.ObjectId.ToArray());
        }

        return ObjectId.FromBytes(hash.GetHashAndReset());
    }

    private sealed class Folder(ObjectId? tree)
    {
        public ObjectId? Tree { get; set; } = tree;

        public Dictionary<string, (EntryKind Kind, ObjectId ObjectId, string? Folder)> Children { get; } = new(StringComparer.Ordinal);
    }
}
