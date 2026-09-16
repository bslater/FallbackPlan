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
/// <param name="Fingerprint">The owning peer's fingerprint.</param>
/// <param name="ReclaimPublicKey">
/// The repository's reclaim public key, lower-hex, as the source published it
/// at first attribution ([ADR-0055](../../docs/adr/0055-reclaim-authority.md)
/// §5); null for an attribution recorded before the decision, or by a source
/// that published none.
/// <para>
/// A destination holds no repository keys by design, so this is the only
/// thing it can check a deletion instruction's signature against — the
/// exception ADR-0020's amendment carves out of "nothing stores a public
/// key", which is true inside the key boundary and not beyond it.
/// </para>
/// </param>
/// <param name="ClaimPublicKey">
/// The owning installation's claim public key, lower-hex, as the source
/// published it at first attribution
/// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md)
/// §1); null for an attribution recorded before the decision, or by a source
/// that published none.
/// <para>
/// What a machine rebuilt after total loss proves its ownership with. It is
/// the <em>installation's</em> key rather than the repository's, because a
/// claimant that has lost the repository cannot reach a repository-derived
/// key — so the same 32 bytes appear against every repository one
/// installation stores here, while the attribution stays per repository,
/// because the quota and the retrieval gate are.
/// </para>
/// </param>
public sealed record ReplicaOwner(
    string Fingerprint, string? ReclaimPublicKey = null, string? ClaimPublicKey = null);

/// <inheritdoc cref="ReplicaOwnerStore"/>
public sealed class ReplicaOwnerStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Dictionary<string, ReplicaOwner> _owners;
    private readonly Lock _gate = new();

    private ReplicaOwnerStore(string path, Dictionary<string, ReplicaOwner> owners)
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
            return new ReplicaOwnerStore(path, new Dictionary<string, ReplicaOwner>(StringComparer.Ordinal));
        }

        var text = File.ReadAllText(path);
        try
        {
            var owners = JsonSerializer.Deserialize<Dictionary<string, ReplicaOwner>>(text, SerializerOptions) ?? [];
            return new ReplicaOwnerStore(path, new Dictionary<string, ReplicaOwner>(owners, StringComparer.Ordinal));
        }
        catch (JsonException)
        {
            // The pre-reclaim shape was a flat id-to-fingerprint map. Lifted
            // rather than set aside, because an attribution discarded is a
            // quota that stops being enforceable and a peer that has to
            // re-offer to be recognised — too high a price for a field that
            // was simply not there yet. The file rewrites in the new shape on
            // the next attribution.
            try
            {
                var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(text, SerializerOptions) ?? [];
                return new ReplicaOwnerStore(
                    path,
                    legacy.ToDictionary(
                        pair => pair.Key, pair => new ReplicaOwner(pair.Value), StringComparer.Ordinal));
            }
            catch (JsonException)
            {
                File.Move(path, path + ".corrupt", overwrite: true);
                return new ReplicaOwnerStore(path, new Dictionary<string, ReplicaOwner>(StringComparer.Ordinal));
            }
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
    /// <param name="reclaimPublicKey">
    /// The reclaim public key the source published with its offer (ADR-0055
    /// §5), or null when it published none. Recorded at first attribution and
    /// <b>never replaced</b> afterwards: the key a destination checks deletion
    /// instructions against must not be replaceable by whoever is sending the
    /// instructions, or the check would be one the attacker controls.
    /// </param>
    /// <param name="claimPublicKey">
    /// The claim public key the source published with its offer
    /// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md) §1),
    /// or null when it published none. Recorded and never replaced on the
    /// same rule, which matters more here than for the reclaim key: this is
    /// what decides which device the replica will be handed back to, so a key
    /// a later offer could overwrite would let whoever can reach this peer
    /// nominate themselves the owner.
    /// </param>
    public bool TryAttribute(
        string repositoryIdHex,
        string fingerprint,
        string? reclaimPublicKey = null,
        string? claimPublicKey = null)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(repositoryIdHex);
        ThrowHelper.ThrowIfNullOrWhiteSpace(fingerprint);

        lock (_gate)
        {
            if (_owners.TryGetValue(repositoryIdHex, out var owner))
            {
                if (!string.Equals(owner.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return false;
                }

                // A first attribution that recorded no key — an older source,
                // or one provisioned before the decision — may still learn it
                // once. Filling an absence is not replacing an answer, and the
                // alternative is a peering that can never be secured without
                // being torn down and rebuilt. Each key fills independently:
                // a source may publish one and not the other.
                var filled = owner;
                if (filled.ReclaimPublicKey is null && reclaimPublicKey is { Length: > 0 })
                {
                    filled = filled with { ReclaimPublicKey = reclaimPublicKey };
                }

                if (filled.ClaimPublicKey is null && claimPublicKey is { Length: > 0 })
                {
                    filled = filled with { ClaimPublicKey = claimPublicKey };
                }

                if (filled != owner)
                {
                    _owners[repositoryIdHex] = filled;
                    AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_owners, SerializerOptions));
                }

                return true;
            }

            _owners[repositoryIdHex] = new ReplicaOwner(
                fingerprint,
                reclaimPublicKey is { Length: > 0 } ? reclaimPublicKey : null,
                claimPublicKey is { Length: > 0 } ? claimPublicKey : null);
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_owners, SerializerOptions));
            return true;
        }
    }

    /// <summary>The attribution for one repository, or null when there is none.</summary>
    /// <param name="repositoryIdHex">The repository's identity, lower-hex.</param>
    public ReplicaOwner? Find(string repositoryIdHex)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(repositoryIdHex);

        lock (_gate)
        {
            return _owners.GetValueOrDefault(repositoryIdHex);
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
                .Where(pair => string.Equals(pair.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
                .Select(pair => pair.Key)];
        }
    }
}
