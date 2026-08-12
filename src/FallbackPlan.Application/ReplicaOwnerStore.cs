using Bodu;
using System.Text.Json;

namespace FallbackPlan.Application;

/// <summary>
/// Which peer each replica repository belongs to: <c>replica-owners.json</c>
/// in the destination's state directory (peer-protocol 05 §2). The attribution
/// is written the first time an offer for a repository is accepted, and is what
/// makes "the total this peer stores here" — the quota's denominator — a
/// computable number across sessions and restarts.
/// </summary>
/// <remarks>
/// Unlike the sync ledger this is <b>not</b> sacrificial: an attribution lost
/// is a quota that can no longer be enforced and, later, a retention command
/// that cannot be validated against its owner. It is still recoverable — the
/// owner is whoever next offers the repository over an authenticated session —
/// so a corrupt file is set aside rather than fatal, and the store refills as
/// peers return.
/// </remarks>
public sealed class ReplicaOwnerStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Dictionary<string, string> _owners;
    private readonly Lock _gate = new();

    private ReplicaOwnerStore(string path, Dictionary<string, string> owners)
    {
        _path = path;
        _owners = owners;
    }

    /// <summary>Opens (or creates) the attribution store in <paramref name="stateDirectory"/>.</summary>
    /// <param name="stateDirectory">The destination's durable local state directory.</param>
    /// <returns>The store.</returns>
    public static ReplicaOwnerStore Open(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, "replica-owners.json");

        if (!File.Exists(path))
        {
            return new ReplicaOwnerStore(path, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        try
        {
            var owners = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(path), SerializerOptions) ?? [];
            return new ReplicaOwnerStore(path, new Dictionary<string, string>(owners, StringComparer.Ordinal));
        }
        catch (JsonException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return new ReplicaOwnerStore(path, new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Attributes a repository to a peer, or confirms an existing attribution.
    /// </summary>
    /// <param name="repositoryIdHex">The repository's identity, lower-hex.</param>
    /// <param name="fingerprint">The offering peer's fingerprint.</param>
    /// <returns>
    /// <see langword="false"/> when the repository is already attributed to a
    /// <b>different</b> peer — the offer is refused rather than one household's
    /// archive counting against another's quota (05 §2).
    /// </returns>
    public bool TryAttribute(string repositoryIdHex, string fingerprint)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(repositoryIdHex);
        ThrowHelper.ThrowIfNullOrWhiteSpace(fingerprint);

        lock (_gate)
        {
            if (_owners.TryGetValue(repositoryIdHex, out var owner))
            {
                return string.Equals(owner, fingerprint, StringComparison.Ordinal);
            }

            _owners[repositoryIdHex] = fingerprint;
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_owners, SerializerOptions));
            return true;
        }
    }

    /// <summary>Every repository attributed to a peer — the quota's scope (05 §1).</summary>
    /// <param name="fingerprint">The peer's fingerprint.</param>
    /// <returns>The repository ids, lower-hex, in no particular order.</returns>
    public IReadOnlyList<string> OwnedBy(string fingerprint)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(fingerprint);

        lock (_gate)
        {
            return [.. _owners
                .Where(pair => string.Equals(pair.Value, fingerprint, StringComparison.Ordinal))
                .Select(pair => pair.Key)];
        }
    }
}
