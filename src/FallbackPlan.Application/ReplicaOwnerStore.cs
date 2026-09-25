using Bodu;
using FallbackPlan.Domain;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FallbackPlan.Application;

/// <summary>
/// What a destination knows about one replica it holds: who it belongs to, and
/// the credential by which someone could prove it is theirs again.
/// </summary>
/// <param name="Fingerprint">The owning peer's fingerprint.</param>
/// <param name="ClaimTokenHex">
/// The token this destination minted for the replica (peer-protocol 07 §5.3),
/// lower-hex. Not a secret — its job is to be <em>unique to this
/// destination</em>, so a proof produced here is inert anywhere else. Null
/// until the replica is first accepted under the claim feature.
/// </param>
/// <param name="ClaimPublicKeyHex">
/// The public half the source registered against that token, lower-hex. Null
/// for a replica stored before the ceremony, or by a source that does not
/// implement it — which is why an unclaimable replica says so by name rather
/// than failing as a wrong passphrase.
/// </param>
/// <param name="ClaimAwaitingAcknowledgement">
/// Whether a claim moved this attribution and the destination's operator has
/// not yet acknowledged it. While true, retention instructions from the
/// claiming identity are refused, deleting nothing (peer-protocol 06 §3).
/// </param>
public sealed record ReplicaAttribution(
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("claim_token")] string? ClaimTokenHex = null,
    [property: JsonPropertyName("claim_public_key")] string? ClaimPublicKeyHex = null,
    [property: JsonPropertyName("claim_awaiting_acknowledgement")] bool ClaimAwaitingAcknowledgement = false);

/// <summary>
/// Which peer each replica repository belongs to: <c>replica-owners.json</c>
/// in the destination's state directory (peer-protocol 05 §2). The attribution
/// is written the first time an offer for a repository is accepted, and is what
/// makes "the total this peer stores here" — the quota's denominator — a
/// computable number across sessions and restarts.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the sync ledger this is <b>not</b> sacrificial: an attribution lost
/// is a quota that can no longer be enforced and, later, a retention command
/// that cannot be validated against its owner. It is still recoverable — the
/// owner is whoever next offers the repository over an authenticated session —
/// so a corrupt file is set aside rather than fatal, and the store refills as
/// peers return.
/// </para>
/// <para>
/// It also carries the claim credential (ADR-0070): the token this destination
/// minted and the public key the source registered against it. Losing those to
/// a corrupt file costs a re-registration on the next session rather than an
/// unclaimable replica, because the source can always register again — but a
/// destination that loses them while the source is gone forever has lost that
/// household's recovery, which is why the file is written atomically.
/// </para>
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
    /// Reads either shape this file has had. Before the claim ceremony each
    /// value was the owning fingerprint as a bare string; it is now an object.
    /// </summary>
    /// <remarks>
    /// The older shape is <b>migrated, never discarded</b>. Deserialising it as
    /// the newer one would throw, and the catch above would move a perfectly
    /// good ledger aside as corrupt — silently unattributing every replica the
    /// destination holds, which is the quota gone and every retention command
    /// unvalidatable until each peer happened to return. A format change is not
    /// damage and must not be mistaken for it.
    /// </remarks>
    private static Dictionary<string, ReplicaAttribution> Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The attribution ledger is not a JSON object.");
        }

        var owners = Empty();
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            owners[entry.Name] = entry.Value.ValueKind switch
            {
                JsonValueKind.String => new ReplicaAttribution(entry.Value.GetString()!),
                JsonValueKind.Object => entry.Value.Deserialize<ReplicaAttribution>(SerializerOptions)
                    ?? throw new JsonException("An attribution entry is null."),
                _ => throw new JsonException("An attribution entry is neither a fingerprint nor a record."),
            };
        }

        return owners;
    }

    private static Dictionary<string, ReplicaAttribution> Empty() => new(StringComparer.Ordinal);

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

    /// <summary>
    /// Every repository recorded against a claim public key
    /// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md) §1).
    /// </summary>
    /// <remarks>
    /// The claim key is the <em>installation's</em>, so one key selects every
    /// repository that installation stores here — which is what lets a
    /// claimant that holds no repository id ask for its replicas by proving a
    /// key instead of naming a repository.
    /// </remarks>
    /// <param name="claimPublicKey">The claim public key, lower-hex.</param>
    /// <returns>The repository ids, lower-hex.</returns>
    public IReadOnlyList<string> ClaimedBy(string claimPublicKey)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(claimPublicKey);

        lock (_gate)
        {
            return [.. _owners
                .Where(pair => string.Equals(pair.Value.ClaimPublicKey, claimPublicKey, StringComparison.Ordinal))
                .Select(pair => pair.Key)];
        }
    }

    /// <summary>
    /// Every repository whose attribution carries a claim public key, with
    /// that key — what the claim ceremony's first answer is built from
    /// (peer-protocol 03 §6): the destination reads each such replica's
    /// descriptor for the salt and parameters a claimant must derive under.
    /// </summary>
    /// <returns>Repository ids, lower-hex, each with its recorded claim public key, in no particular order.</returns>
    public IReadOnlyList<(string RepositoryIdHex, string ClaimPublicKey)> WithClaimKey()
    {
        lock (_gate)
        {
            return [.. _owners
                .Where(pair => pair.Value.ClaimPublicKey is { Length: > 0 })
                .Select(pair => (pair.Key, pair.Value.ClaimPublicKey!))];
        }
    }

    /// <summary>
    /// Every attribution this destination holds, ids ascending — the
    /// operator's view ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md)
    /// §3): whose each replica is, and whether a claim key is on record,
    /// which decides whether its owner's passphrase can move it or only the
    /// operator can.
    /// </summary>
    /// <returns>Repository ids, lower-hex, each with its attribution as recorded.</returns>
    public IReadOnlyList<(string RepositoryIdHex, ReplicaOwner Owner)> All()
    {
        lock (_gate)
        {
            return [.. _owners
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (pair.Key, pair.Value))];
        }
    }

    /// <summary>
    /// Points a replica at a new device identity, the claim ceremony having
    /// proved the claimant is the same owner (ADR-0053 §2).
    /// </summary>
    /// <remarks>
    /// The only writer here that changes a fingerprint, and the one place
    /// <see cref="TryAttribute"/>'s "already stored here for another peer"
    /// rule is deliberately set aside. It is set aside on <em>proof</em>, and
    /// the proof is not this store's to check — the store holds no
    /// cryptography and knows no keys, so a caller that skipped the signature
    /// would be a caller that skipped the ceremony. The two recorded public
    /// keys are kept exactly as they were: the same passphrase re-derives
    /// them, so a claimant that could replace them could only replace them
    /// with themselves, and anyone else must not.
    /// </remarks>
    /// <param name="repositoryIdHex">The repository's identity, lower-hex.</param>
    /// <param name="fingerprint">The claimant's fingerprint.</param>
    /// <returns><see langword="false"/> when no such repository is attributed here.</returns>
    public bool Reattribute(string repositoryIdHex, string fingerprint)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(repositoryIdHex);
        ThrowHelper.ThrowIfNullOrWhiteSpace(fingerprint);

        lock (_gate)
        {
            if (!_owners.TryGetValue(repositoryIdHex, out var owner))
            {
                return false;
            }

            if (string.Equals(owner.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return true;
            }

            _owners[repositoryIdHex] = owner with { Fingerprint = fingerprint };
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_owners, SerializerOptions));
            return true;
        }
    }
}
