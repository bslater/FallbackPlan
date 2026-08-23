using Bodu;
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
/// It also carries the claim credential (ADR-0046): the token this destination
/// minted and the public key the source registered against it. Losing those to
/// a corrupt file costs a re-registration on the next session rather than an
/// unclaimable replica, because the source can always register again — but a
/// destination that loses them while the source is gone forever has lost that
/// household's recovery, which is why the file is written atomically.
/// </para>
/// </remarks>
public sealed class ReplicaOwnerStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Dictionary<string, ReplicaAttribution> _owners;
    private readonly Lock _gate = new();

    private ReplicaOwnerStore(string path, Dictionary<string, ReplicaAttribution> owners)
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
            return new ReplicaOwnerStore(path, Empty());
        }

        try
        {
            return new ReplicaOwnerStore(path, Read(File.ReadAllText(path)));
        }
        catch (JsonException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return new ReplicaOwnerStore(path, Empty());
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
    public bool TryAttribute(string repositoryIdHex, string fingerprint)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(repositoryIdHex);
        ThrowHelper.ThrowIfNullOrWhiteSpace(fingerprint);

        lock (_gate)
        {
            if (_owners.TryGetValue(repositoryIdHex, out var attribution))
            {
                return string.Equals(attribution.Fingerprint, fingerprint, StringComparison.Ordinal);
            }

            _owners[repositoryIdHex] = new ReplicaAttribution(fingerprint);
            Persist();
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
                .Where(pair => string.Equals(pair.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
                .Select(pair => pair.Key)];
        }
    }

    /// <summary>What this destination holds for one replica, or null if it holds none.</summary>
    /// <param name="repositoryIdHex">The repository's identity, lower-hex.</param>
    public ReplicaAttribution? Find(string repositoryIdHex)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(repositoryIdHex);

        lock (_gate)
        {
            return _owners.GetValueOrDefault(repositoryIdHex);
        }
    }

    /// <summary>
    /// Returns the token to offer a source so it can register a claim
    /// credential, minting one on first need. Null once a credential is
    /// registered: the destination asks exactly once, and never re-offers a
    /// token for a replica that can already be claimed.
    /// </summary>
    /// <param name="repositoryIdHex">The repository's identity, lower-hex.</param>
    /// <param name="mintToken">Produces a fresh 16-byte token.</param>
    /// <returns>The token as lower-hex, or null when nothing should be offered.</returns>
    public string? OfferClaimToken(string repositoryIdHex, Func<byte[]> mintToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(repositoryIdHex);
        ThrowHelper.ThrowIfNull(mintToken);

        lock (_gate)
        {
            if (!_owners.TryGetValue(repositoryIdHex, out var attribution)
                || attribution.ClaimPublicKeyHex is not null)
            {
                return null;
            }

            if (attribution.ClaimTokenHex is { } existing)
            {
                return existing;
            }

            var token = Convert.ToHexStringLower(mintToken());
            _owners[repositoryIdHex] = attribution with { ClaimTokenHex = token };
            Persist();
            return token;
        }
    }

    /// <summary>
    /// Records the public half a source derived from its passphrase and this
    /// destination's token.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when no token was offered for this repository,
    /// or one is already registered — a registration is answered once, and a
    /// later one must not silently replace the credential a recovery depends
    /// on.
    /// </returns>
    public bool TryRegisterClaimKey(string repositoryIdHex, string claimPublicKeyHex)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(repositoryIdHex);
        ThrowHelper.ThrowIfNullOrWhiteSpace(claimPublicKeyHex);

        lock (_gate)
        {
            if (!_owners.TryGetValue(repositoryIdHex, out var attribution)
                || attribution.ClaimTokenHex is null
                || attribution.ClaimPublicKeyHex is not null)
            {
                return false;
            }

            _owners[repositoryIdHex] = attribution with { ClaimPublicKeyHex = claimPublicKeyHex };
            Persist();
            return true;
        }
    }

    /// <summary>
    /// The replicas a dialling peer could be challenged to claim: every one
    /// carrying a registered credential that this identity does not already
    /// own (peer-protocol 07 §5.5).
    /// </summary>
    /// <param name="fingerprint">The dialling peer's fingerprint.</param>
    public IReadOnlyList<string> ClaimableBy(string fingerprint)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(fingerprint);

        lock (_gate)
        {
            return [.. _owners
                .Where(pair => pair.Value.ClaimPublicKeyHex is not null
                    && !string.Equals(pair.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Moves a replica's attribution to the peer that proved the passphrase,
    /// and marks the claim as awaiting the operator's acknowledgement.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the repository is unknown or carries no
    /// registered credential; a caller must have verified a proof against that
    /// credential before calling.
    /// </returns>
    public bool TryReattribute(string repositoryIdHex, string fingerprint)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(repositoryIdHex);
        ThrowHelper.ThrowIfNullOrWhiteSpace(fingerprint);

        lock (_gate)
        {
            if (!_owners.TryGetValue(repositoryIdHex, out var attribution)
                || attribution.ClaimPublicKeyHex is null)
            {
                return false;
            }

            // Re-claiming what you already own is idempotent and raises no
            // notice: it moves nothing, so there is nothing for an operator to
            // be told about.
            if (string.Equals(attribution.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return true;
            }

            _owners[repositoryIdHex] = attribution with
            {
                Fingerprint = fingerprint,
                ClaimAwaitingAcknowledgement = true,
            };
            Persist();
            return true;
        }
    }

    /// <summary>
    /// Whether a claim on this replica is still waiting for the destination's
    /// operator — the gate retention instructions are refused behind
    /// (peer-protocol 06 §3).
    /// </summary>
    public bool IsClaimAwaitingAcknowledgement(string repositoryIdHex)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(repositoryIdHex);

        lock (_gate)
        {
            return _owners.TryGetValue(repositoryIdHex, out var attribution)
                && attribution.ClaimAwaitingAcknowledgement;
        }
    }

    /// <summary>
    /// Records that the destination's operator acknowledged a claim, releasing
    /// the retention gate. Idempotent.
    /// </summary>
    public void AcknowledgeClaim(string repositoryIdHex)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(repositoryIdHex);

        lock (_gate)
        {
            if (_owners.TryGetValue(repositoryIdHex, out var attribution)
                && attribution.ClaimAwaitingAcknowledgement)
            {
                _owners[repositoryIdHex] = attribution with { ClaimAwaitingAcknowledgement = false };
                Persist();
            }
        }
    }

    private void Persist() => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_owners, SerializerOptions));
}
