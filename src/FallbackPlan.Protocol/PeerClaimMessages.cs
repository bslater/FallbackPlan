using Bodu;
using Bodu.Security.Cryptography;
using System.Formats.Cbor;

namespace FallbackPlan.Protocol;

/// <summary>
/// The bytes a claim proof signs, and the Ed25519 either side applies to them
/// (specification peer-protocol 07 §5.6–§5.7).
/// </summary>
/// <remarks>
/// <para>
/// Deriving the claim keypair needs the passphrase and lives in
/// <c>Repository.Crypto</c>. <b>Verifying</b> one needs nothing but a stored
/// public key, which is why it lives here: a destination holds no repository
/// key at all, and must be able to decide a claim without one. That division
/// is the whole reason the ceremony works — see ADR-0046.
/// </para>
/// <para>
/// Every field in the message binds the proof to one moment. The nonce makes
/// it fresh, the session transcript hash makes it inseparable from this
/// connection and these two identities, the claimant's fingerprint names the
/// identity the attribution would move <em>to</em>, and the destination's own
/// token makes it worthless at any other destination.
/// </para>
/// </remarks>
public static class ReplicaClaimProof
{
    /// <summary>An Ed25519 signature is 64 bytes.</summary>
    public const int SignatureLength = 64;

    /// <summary>A claim public key is 32 bytes.</summary>
    public const int PublicKeyLength = 32;

    /// <summary>A destination's claim token is 16 bytes.</summary>
    public const int TokenLength = 16;

    /// <summary>A challenge nonce is 32 bytes.</summary>
    public const int NonceLength = 32;

    /// <summary>A repository identity is 16 bytes.</summary>
    public const int RepositoryIdLength = 16;

    private static ReadOnlySpan<byte> Context => "fbp-peer-v1:replica-claim"u8;

    /// <summary>
    /// Builds the byte string of 07 §5.6. Both sides construct it the same
    /// way — the destination from its own copy of every field, never from
    /// anything the claimant sent — so a claimant cannot choose what it signs.
    /// </summary>
    /// <exception cref="ArgumentException">A component is the wrong length.</exception>
    public static byte[] Message(
        ReadOnlySpan<byte> repositoryId,
        ReadOnlySpan<byte> claimToken,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> transcriptHash,
        ReadOnlySpan<byte> claimantFingerprint)
    {
        Require(repositoryId, RepositoryIdLength, nameof(repositoryId));
        Require(claimToken, TokenLength, nameof(claimToken));
        Require(nonce, NonceLength, nameof(nonce));
        Require(transcriptHash, 32, nameof(transcriptHash));
        Require(claimantFingerprint, 32, nameof(claimantFingerprint));

        var message = new byte[
            Context.Length + RepositoryIdLength + TokenLength + NonceLength + 32 + 32];
        var offset = 0;
        Append(message, ref offset, Context);
        Append(message, ref offset, repositoryId);
        Append(message, ref offset, claimToken);
        Append(message, ref offset, nonce);
        Append(message, ref offset, transcriptHash);
        Append(message, ref offset, claimantFingerprint);
        return message;
    }

    /// <summary>
    /// The public half of a claim seed — what a source registers and a
    /// destination later compares against.
    /// </summary>
    /// <remarks>
    /// <c>Repository.Crypto</c>'s <c>ClaimKeyDeriver</c> computes the same
    /// value, and the repetition is deliberate: deriving a seed needs the
    /// passphrase and belongs there, while a destination verifying a claim
    /// holds no repository key at all and must not have to reference that
    /// assembly to do it (ADR-0019's blast-radius tiering).
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="seed"/> is not 32 bytes.</exception>
    public static byte[] PublicKeyOf(ReadOnlySpan<byte> seed)
    {
        Require(seed, PublicKeyLength, nameof(seed));

        using var key = Ed25519.Create();
        key.ImportPrivateKey(seed);
        return key.ExportPublicKey();
    }

    /// <summary>Signs a claim message with the seed derived from the passphrase.</summary>
    /// <exception cref="ArgumentException"><paramref name="seed"/> is not 32 bytes.</exception>
    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        Require(seed, PublicKeyLength, nameof(seed));

        using var key = Ed25519.Create();
        key.ImportPrivateKey(seed);
        return key.SignData(message);
    }

    /// <summary>
    /// Verifies a claim signature against the public key the destination
    /// stored. Returns <see langword="false"/> rather than throwing on every
    /// shape of failure: which check caught it is not a claimant's business
    /// (07 §5.7).
    /// </summary>
    public static bool Verify(
        ReadOnlySpan<byte> claimPublicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (claimPublicKey.Length != PublicKeyLength || signature.Length != SignatureLength)
        {
            return false;
        }

        using var key = Ed25519.Create();
        key.ImportPublicKey(claimPublicKey);
        return key.VerifyData(message, signature);
    }

    private static void Append(byte[] destination, ref int offset, ReadOnlySpan<byte> value)
    {
        value.CopyTo(destination.AsSpan(offset));
        offset += value.Length;
    }

    private static void Require(ReadOnlySpan<byte> value, int length, string name)
    {
        if (value.Length != length)
        {
            throw new ArgumentException($"'{name}' is exactly {length} bytes.", name);
        }
    }
}

/// <summary>
/// A peer asking what it could claim here (specification peer-protocol
/// §5.4). An empty map: the destination answers from its own ledger, and a
/// claimant that named repositories would be probing rather than claiming.
/// </summary>
public sealed record ClaimRequest : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ClaimRequest;

    /// <inheritdoc/>
    public int BodyEntryCount => 0;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer) => ThrowHelper.ThrowIfNull(writer);

    /// <summary>Reads a request from a body positioned after the message type.</summary>
    public static ClaimRequest Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);
        PeerCbor.ReadEntries(reader, _ => reader.SkipValue());
        return new ClaimRequest();
    }
}

/// <summary>One replica a claimant may try to prove, and the challenge for it.</summary>
/// <param name="RepositoryId">The replica's repository identity (16 bytes).</param>
/// <param name="ClaimToken">This destination's token for it (16 bytes) — not a secret.</param>
/// <param name="Nonce">Fresh bytes for this frame (32 bytes), never reused across sessions.</param>
public sealed record ClaimCandidate(
    ReadOnlyMemory<byte> RepositoryId, ReadOnlyMemory<byte> ClaimToken, ReadOnlyMemory<byte> Nonce);

/// <summary>
/// The destination's challenge (specification peer-protocol 07 §5.5): one
/// entry per replica carrying a registered credential that the dialling
/// identity does not already own.
/// </summary>
/// <remarks>
/// An <b>empty</b> array is the ordinary answer to a claimant with nothing to
/// claim, and is sent rather than a refusal. It discloses only that this
/// identity has nothing unclaimed waiting — which the owner inventory already
/// tells an attributed peer about itself.
/// </remarks>
/// <param name="Candidates">The replicas that may be claimed.</param>
public sealed record ClaimChallenge(IReadOnlyList<ClaimCandidate> Candidates) : IPeerMessage
{
    /// <summary>The most candidates one challenge may carry (00 §2.3).</summary>
    public const int MaximumCandidates = 256;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ClaimChallenge;

    /// <inheritdoc/>
    public int BodyEntryCount => 1;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        if (Candidates.Count > MaximumCandidates)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A claim challenge of {Candidates.Count} candidates violates 07 §5.5.");
        }

        writer.WriteInt32(1);
        writer.WriteStartArray(Candidates.Count);
        foreach (var candidate in Candidates)
        {
            writer.WriteStartMap(3);
            writer.WriteInt32(1);
            writer.WriteByteString(candidate.RepositoryId.Span);
            writer.WriteInt32(2);
            writer.WriteByteString(candidate.ClaimToken.Span);
            writer.WriteInt32(3);
            writer.WriteByteString(candidate.Nonce.Span);
            writer.WriteEndMap();
        }

        writer.WriteEndArray();
    }

    /// <summary>Reads a challenge from a body positioned after the message type.</summary>
    /// <exception cref="PeerProtocolException">The body violates 07 §5.5 or a 00 §2.3 limit.</exception>
    public static ClaimChallenge Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        List<ClaimCandidate> candidates = [];

        PeerCbor.ReadEntries(reader, key =>
        {
            if (key != 1)
            {
                reader.SkipValue();
                return;
            }

            reader.ReadStartArray();
            while (reader.PeekState() != CborReaderState.EndArray)
            {
                byte[]? repositoryId = null, token = null, nonce = null;
                PeerCbor.ReadEntries(reader, inner =>
                {
                    switch (inner)
                    {
                        case 1:
                            repositoryId = reader.ReadByteString();
                            break;
                        case 2:
                            token = reader.ReadByteString();
                            break;
                        case 3:
                            nonce = reader.ReadByteString();
                            break;
                        default:
                            reader.SkipValue();
                            break;
                    }
                });

                if (repositoryId?.Length != ReplicaClaimProof.RepositoryIdLength
                    || token?.Length != ReplicaClaimProof.TokenLength
                    || nonce?.Length != ReplicaClaimProof.NonceLength)
                {
                    throw new PeerProtocolException(
                        PeerRefusalReason.Malformed, "A claim candidate is not the shape 07 §5.5 defines.");
                }

                if (candidates.Count == MaximumCandidates)
                {
                    throw new PeerProtocolException(
                        PeerRefusalReason.Malformed, "A claim challenge exceeds the 07 §5.5 candidate limit.");
                }

                candidates.Add(new ClaimCandidate(repositoryId, token, nonce));
            }

            reader.ReadEndArray();
        });

        return new ClaimChallenge(candidates);
    }
}

/// <summary>One claimant's answer for one candidate.</summary>
/// <param name="RepositoryId">Which candidate this answers (16 bytes).</param>
/// <param name="ClaimPublicKey">The public half derived from the passphrase (32 bytes).</param>
/// <param name="Signature">Ed25519 over the message of 07 §5.6 (64 bytes).</param>
public sealed record ClaimAnswer(
    ReadOnlyMemory<byte> RepositoryId, ReadOnlyMemory<byte> ClaimPublicKey, ReadOnlyMemory<byte> Signature);

/// <summary>
/// The claimant's proofs (specification peer-protocol 07 §5.6). Omitting a
/// candidate is not an error: a claimant holding one repository's passphrase
/// and not another's sends one answer.
/// </summary>
/// <param name="Answers">One entry per candidate the claimant can prove.</param>
public sealed record ClaimProof(IReadOnlyList<ClaimAnswer> Answers) : IPeerMessage
{
    /// <summary>The most answers one proof may carry (00 §2.3).</summary>
    public const int MaximumAnswers = ClaimChallenge.MaximumCandidates;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ClaimProof;

    /// <inheritdoc/>
    public int BodyEntryCount => 1;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        if (Answers.Count > MaximumAnswers)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"A claim proof of {Answers.Count} answers violates 07 §5.6.");
        }

        writer.WriteInt32(1);
        writer.WriteStartArray(Answers.Count);
        foreach (var answer in Answers)
        {
            writer.WriteStartMap(3);
            writer.WriteInt32(1);
            writer.WriteByteString(answer.RepositoryId.Span);
            writer.WriteInt32(2);
            writer.WriteByteString(answer.ClaimPublicKey.Span);
            writer.WriteInt32(3);
            writer.WriteByteString(answer.Signature.Span);
            writer.WriteEndMap();
        }

        writer.WriteEndArray();
    }

    /// <summary>Reads a proof from a body positioned after the message type.</summary>
    /// <exception cref="PeerProtocolException">The body violates 07 §5.6 or a 00 §2.3 limit.</exception>
    public static ClaimProof Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        List<ClaimAnswer> answers = [];

        PeerCbor.ReadEntries(reader, key =>
        {
            if (key != 1)
            {
                reader.SkipValue();
                return;
            }

            reader.ReadStartArray();
            while (reader.PeekState() != CborReaderState.EndArray)
            {
                byte[]? repositoryId = null, publicKey = null, signature = null;
                PeerCbor.ReadEntries(reader, inner =>
                {
                    switch (inner)
                    {
                        case 1:
                            repositoryId = reader.ReadByteString();
                            break;
                        case 2:
                            publicKey = reader.ReadByteString();
                            break;
                        case 3:
                            signature = reader.ReadByteString();
                            break;
                        default:
                            reader.SkipValue();
                            break;
                    }
                });

                if (repositoryId?.Length != ReplicaClaimProof.RepositoryIdLength
                    || publicKey?.Length != ReplicaClaimProof.PublicKeyLength
                    || signature?.Length != ReplicaClaimProof.SignatureLength)
                {
                    throw new PeerProtocolException(
                        PeerRefusalReason.Malformed, "A claim answer is not the shape 07 §5.6 defines.");
                }

                if (answers.Count == MaximumAnswers)
                {
                    throw new PeerProtocolException(
                        PeerRefusalReason.Malformed, "A claim proof exceeds the 07 §5.6 answer limit.");
                }

                answers.Add(new ClaimAnswer(repositoryId, publicKey, signature));
            }

            reader.ReadEndArray();
        });

        return new ClaimProof(answers);
    }
}

/// <summary>One replica a claim moved, and what it holds.</summary>
/// <param name="RepositoryId">The claimed repository (16 bytes).</param>
/// <param name="BackupSetIds">
/// The set identifiers its snapshots carry (16 bytes each) — the one piece of
/// a claimant's lost configuration nothing else can supply (07 §5.8).
/// </param>
public sealed record ClaimedReplica(
    ReadOnlyMemory<byte> RepositoryId, IReadOnlyList<ReadOnlyMemory<byte>> BackupSetIds);

/// <summary>
/// What the claim moved (specification peer-protocol 07 §5.8). A proof that
/// failed any check is simply absent — never distinguished from a replica the
/// destination does not hold (07 §4's rule, unchanged).
/// </summary>
/// <param name="Claimed">One entry per proof that validated.</param>
public sealed record ClaimResult(IReadOnlyList<ClaimedReplica> Claimed) : IPeerMessage
{
    /// <summary>The most set ids one entry may carry (00 §2.3).</summary>
    public const int MaximumSetIds = 256;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ClaimResult;

    /// <inheritdoc/>
    public int BodyEntryCount => 1;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteStartArray(Claimed.Count);
        foreach (var replica in Claimed)
        {
            if (replica.BackupSetIds.Count > MaximumSetIds)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, "A claim result names more set ids than 07 §5.8 permits.");
            }

            writer.WriteStartMap(2);
            writer.WriteInt32(1);
            writer.WriteByteString(replica.RepositoryId.Span);
            writer.WriteInt32(2);
            writer.WriteStartArray(replica.BackupSetIds.Count);
            foreach (var setId in replica.BackupSetIds)
            {
                writer.WriteByteString(setId.Span);
            }

            writer.WriteEndArray();
            writer.WriteEndMap();
        }

        writer.WriteEndArray();
    }

    /// <summary>Reads a result from a body positioned after the message type.</summary>
    /// <exception cref="PeerProtocolException">The body violates 07 §5.8 or a 00 §2.3 limit.</exception>
    public static ClaimResult Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        List<ClaimedReplica> claimed = [];

        PeerCbor.ReadEntries(reader, key =>
        {
            if (key != 1)
            {
                reader.SkipValue();
                return;
            }

            reader.ReadStartArray();
            while (reader.PeekState() != CborReaderState.EndArray)
            {
                byte[]? repositoryId = null;
                List<ReadOnlyMemory<byte>> setIds = [];

                PeerCbor.ReadEntries(reader, inner =>
                {
                    switch (inner)
                    {
                        case 1:
                            repositoryId = reader.ReadByteString();
                            break;
                        case 2:
                            reader.ReadStartArray();
                            while (reader.PeekState() != CborReaderState.EndArray)
                            {
                                if (setIds.Count == MaximumSetIds)
                                {
                                    throw new PeerProtocolException(
                                        PeerRefusalReason.Malformed,
                                        "A claim result names more set ids than 07 §5.8 permits.");
                                }

                                setIds.Add(reader.ReadByteString());
                            }

                            reader.ReadEndArray();
                            break;
                        default:
                            reader.SkipValue();
                            break;
                    }
                });

                if (repositoryId?.Length != ReplicaClaimProof.RepositoryIdLength)
                {
                    throw new PeerProtocolException(
                        PeerRefusalReason.Malformed, "A claim result entry is not the shape 07 §5.8 defines.");
                }

                claimed.Add(new ClaimedReplica(repositoryId, setIds));
            }

            reader.ReadEndArray();
        });

        return new ClaimResult(claimed);
    }
}

/// <summary>
/// A source registering the public half of its claim credential
/// (specification peer-protocol 03 §3.2.1), answering a destination that
/// offered a token in its inventory.
/// </summary>
/// <remarks>
/// This is how disaster recovery is armed, one session ahead of the disaster:
/// a machine that has already been lost cannot register anything, so the
/// credential has to be recorded while the pairing is still alive.
/// </remarks>
/// <param name="RepositoryId">The repository the credential is for (16 bytes).</param>
/// <param name="ClaimPublicKey">The public half (32 bytes); the private half never leaves the source.</param>
public sealed record ClaimRegister(
    ReadOnlyMemory<byte> RepositoryId, ReadOnlyMemory<byte> ClaimPublicKey) : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ClaimRegister;

    /// <inheritdoc/>
    public int BodyEntryCount => 2;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteByteString(RepositoryId.Span);
        writer.WriteInt32(2);
        writer.WriteByteString(ClaimPublicKey.Span);
    }

    /// <summary>Reads a registration from a body positioned after the message type.</summary>
    /// <exception cref="PeerProtocolException">The body violates 03 §3.2.1.</exception>
    public static ClaimRegister Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        byte[]? repositoryId = null, publicKey = null;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    repositoryId = reader.ReadByteString();
                    break;
                case 2:
                    publicKey = reader.ReadByteString();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (repositoryId?.Length != ReplicaClaimProof.RepositoryIdLength
            || publicKey?.Length != ReplicaClaimProof.PublicKeyLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A claim registration is not the shape 03 §3.2.1 defines.");
        }

        return new ClaimRegister(repositoryId, publicKey);
    }
}
