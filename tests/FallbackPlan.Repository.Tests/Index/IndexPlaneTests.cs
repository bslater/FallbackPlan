using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// The index plane end to end (specification 07 §2–§8; FR-MAN-008,
/// FR-MAN-016, FR-MAN-017): codecs round-trip through the two-pass
/// signature, the publisher seals real objects into a real store, the
/// loader verifies and applies them, void deltas fill crash-skipped
/// numbers, and gaps surface as damage only after the bounded patience.
/// </summary>
public sealed class IndexPlaneTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-index-tests", Guid.NewGuid().ToString("n"));

    private LocalFileSystemObjectStore CreateStore() => new(Path.Combine(_root, "store"));

    private WriterSequence CreateSequence() =>
        new(new FileSequenceStateStore(Path.Combine(_root, "state", "sequence.txt")));

    private static IndexEntry Entry(byte objectSeed, byte blobSeed)
    {
        var objectBytes = new byte[32];
        Array.Fill(objectBytes, objectSeed);
        var blobBytes = new byte[16];
        Array.Fill(blobBytes, blobSeed);

        return new IndexEntry(
            ObjectId.FromBytes(objectBytes),
            BlobId.FromBytes(blobBytes),
            PhysicalOffset: 88,
            StoredLength: 4096,
            CompressionProfileValue: 0x0001,
            EncryptionProfileValue: 0x0001,
            IndexEntryType.Insertion);
    }

    [Fact]
    public void A_covered_blob_digest_round_trips_and_is_covered_by_the_signature()
    {
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var signer = RepositorySigner.Create(hierarchy, KeyGeneration.Zero);

        var digest = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        var delta = new IndexDelta
        {
            WriterId = Writer,
            Sequence = 11,
            Generation = 0,
            CoveredBlobIds = [BlobId.FromBytes(Enumerable.Repeat((byte)6, 16).ToArray())],
            CoveredBlobDigests = [digest],
            Entries = [Entry(1, 6)],
        };

        var signedBytes = IndexDeltaCodec.EncodeForSigning(delta);
        var decoded = IndexDeltaCodec.Decode(IndexDeltaCodec.Encode(delta, signer.Sign(signedBytes)));

        Assert.Equal(digest, Assert.Single(decoded.Delta.CoveredBlobDigests).ToArray());
        Assert.True(signer.Verify(decoded.SignedBytes.Span, decoded.Signature.Span));

        // Key 10 sorts after the signature and is signed anyway — the
        // signature covers every key except itself (07 §2), which is the rule
        // that let a field be added without renumbering an established one.
        // A digest that changes must change the signed bytes.
        var altered = delta with { CoveredBlobDigests = [Enumerable.Repeat((byte)0x5B, 32).ToArray()] };
        Assert.NotEqual(signedBytes, IndexDeltaCodec.EncodeForSigning(altered));
    }

    [Fact]
    public void Covered_blob_digests_are_parallel_to_the_covered_blobs_or_absent()
    {
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var signer = RepositorySigner.Create(hierarchy, KeyGeneration.Zero);

        var delta = new IndexDelta
        {
            WriterId = Writer,
            Sequence = 12,
            Generation = 0,
            CoveredBlobIds =
            [
                BlobId.FromBytes(Enumerable.Repeat((byte)6, 16).ToArray()),
                BlobId.FromBytes(Enumerable.Repeat((byte)7, 16).ToArray()),
            ],

            // One digest, two blobs. Pairing what can be paired would attach
            // the first blob's digest to whichever blob a reader happened to
            // pair it with, which is worse than carrying no digest at all.
            CoveredBlobDigests = [Enumerable.Repeat((byte)0x5A, 32).ToArray()],
            Entries = [Entry(1, 6)],
        };

        // The same validation guards both directions — a writer cannot
        // produce this object and a reader would refuse it if one appeared.
        Assert.Throws<IndexFormatException>(() => IndexDeltaCodec.EncodeForSigning(delta));

        var wrongWidth = delta with
        {
            CoveredBlobDigests = [new byte[31], new byte[31]],
        };

        Assert.Throws<IndexFormatException>(() => IndexDeltaCodec.EncodeForSigning(wrongWidth));
    }

    [Fact]
    public void A_delta_round_trips_through_the_two_pass_signature()
    {
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var signer = RepositorySigner.Create(hierarchy, KeyGeneration.Zero);

        var delta = new IndexDelta
        {
            WriterId = Writer,
            Sequence = 7,
            PredecessorDeltaId = DeltaId.FromBytes(Enumerable.Repeat((byte)5, 16).ToArray()),
            Generation = 0,
            CoveredBlobIds = [BlobId.FromBytes(Enumerable.Repeat((byte)6, 16).ToArray())],
            Entries = [Entry(1, 6), Entry(2, 6)],
        };

        var signedBytes = IndexDeltaCodec.EncodeForSigning(delta);
        var stored = IndexDeltaCodec.Encode(delta, signer.Sign(signedBytes));

        var decoded = IndexDeltaCodec.Decode(stored);

        Assert.Equal(signedBytes, decoded.SignedBytes.ToArray());
        Assert.True(signer.Verify(decoded.SignedBytes.Span, decoded.Signature.Span));
        Assert.Equal(delta.Sequence, decoded.Delta.Sequence);
        Assert.Equal(2, decoded.Delta.Entries.Count);
        Assert.Equal(delta.PredecessorDeltaId, decoded.Delta.PredecessorDeltaId);

        // The packed-profiles wire form unpacks losslessly.
        Assert.Equal((ushort)0x0001, decoded.Delta.Entries[0].CompressionProfileValue);
        Assert.Equal((ushort)0x0001, decoded.Delta.Entries[0].EncryptionProfileValue);
    }

    [Fact]
    public void A_multi_shard_delta_omits_key_5_and_round_trips()
    {
        // Object ids are HMAC outputs, so real deltas span shards — key 5 is
        // absent (ADR-0022 §Decision 4). Distinct fill bytes land in
        // distinct shards.
        var delta = new IndexDelta
        {
            WriterId = Writer,
            Sequence = 1,
            Generation = 0,
            Entries = [Entry(0x11, 1), Entry(0x22, 1)],
        };

        Assert.NotEqual(delta.Entries[0].Shard, delta.Entries[1].Shard);

        var decoded = IndexDeltaCodec.Decode(IndexDeltaCodec.Encode(delta, new byte[64]));
        Assert.Null(decoded.Delta.Shard);
    }

    [Fact]
    public void A_declared_shard_that_mismatches_an_entry_is_refused()
    {
        var delta = new IndexDelta
        {
            WriterId = Writer,
            Sequence = 1,
            Generation = 0,
            Shard = 0x1111,
            Entries = [Entry(0x22, 1)],
        };

        Assert.Throws<IndexFormatException>(() => IndexDeltaCodec.Encode(delta, new byte[64]));
    }

    [Fact]
    public void A_checkpoint_round_trips_with_computed_shard_hashes()
    {
        var entries = new List<IndexEntry> { Entry(0x11, 1), Entry(0x22, 2), Entry(0x11, 3) };
        var (shardSet, hashes) = ShardHashes.Compute(entries);

        var checkpoint = new Checkpoint
        {
            Generation = 1,
            SubsumedDeltaIds = [DeltaId.FromBytes(Enumerable.Repeat((byte)1, 16).ToArray())],
            WriterWatermarks = [new WriterWatermark(Writer, 9)],
            ShardSet = shardSet,
            ShardHashesList = [.. hashes.Select(hash => (ReadOnlyMemory<byte>)hash)],
            Entries = entries,
            WriterId = Writer,
        };

        var decoded = CheckpointCodec.Decode(CheckpointCodec.Encode(checkpoint, new byte[64]));

        Assert.Equal(shardSet, decoded.Checkpoint.ShardSet);
        Assert.Equal(3, decoded.Checkpoint.Entries.Count);
        Assert.Equal((ulong)9, decoded.Checkpoint.WriterWatermarks[0].HighestSequence);

        // The hashes are reader-recomputable from the entries themselves.
        var (recomputedSet, recomputedHashes) = ShardHashes.Compute(decoded.Checkpoint.Entries);
        Assert.Equal(decoded.Checkpoint.ShardSet, recomputedSet);
        Assert.Equal(
            decoded.Checkpoint.ShardHashesList.Select(hash => Convert.ToHexString(hash.Span)),
            recomputedHashes.Select(Convert.ToHexString));
    }

    [Fact]
    public void A_checkpoint_with_an_unenumerated_shard_is_refused()
    {
        var checkpoint = new Checkpoint
        {
            Generation = 1,
            ShardSet = [Entry(0x11, 1).Shard],
            ShardHashesList = [new byte[32]],
            Entries = [Entry(0x11, 1), Entry(0x22, 2)],
            WriterId = Writer,
        };

        Assert.Throws<IndexFormatException>(() => CheckpointCodec.Encode(checkpoint, new byte[64]));
    }

    [Fact]
    public async Task Publish_and_load_round_trips_with_verified_signatures()
    {
        var store = CreateStore();
        using var hierarchy = new KeyHierarchy(MasterKey);
        var sequence = CreateSequence();

        using (var publisher = new IndexPublisher(store, Repo, Writer, hierarchy, sequence))
        {
            await publisher.PublishDeltaAsync(
                0, [Entry(1, 1).BlobId], [Entry(1, 1), Entry(2, 1)], CancellationToken.None);
            await publisher.PublishDeltaAsync(
                0, [Entry(3, 2).BlobId], [Entry(3, 2)], CancellationToken.None);
        }

        using var loader = new IndexLoader(store, Repo, hierarchy);
        var state = await loader.LoadAsync(
            currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null, blobState: null,
            CancellationToken.None);

        Assert.Empty(state.Findings);
        Assert.Empty(state.UnresolvedGaps);
        Assert.Equal(2, state.Deltas.Count);
        Assert.Equal(3, state.Resolved.Count);
        Assert.True(state.Resolved.ContainsKey(Entry(1, 1).ObjectId));
    }

    [Fact]
    public async Task A_crash_skipped_sequence_gets_a_void_delta_and_the_gap_closes()
    {
        var store = CreateStore();
        using var hierarchy = new KeyHierarchy(MasterKey);

        // Run 1: allocate a number (a blob counter, say) and crash before
        // anything is published for it.
        var sequence = CreateSequence();
        var skipped = sequence.AllocateNext();
        Assert.Equal(1UL, skipped);

        // Run 2: recover — the pending number is a void-delta obligation.
        var recovered = CreateSequence();
        Assert.Equal([skipped], recovered.RecoveredObligations);

        using (var publisher = new IndexPublisher(store, Repo, Writer, hierarchy, recovered))
        {
            foreach (var obligation in recovered.RecoveredObligations)
            {
                await publisher.PublishVoidDeltaAsync(0, obligation, CancellationToken.None);
            }

            await publisher.PublishDeltaAsync(0, [], [Entry(1, 1)], CancellationToken.None);
        }

        using var loader = new IndexLoader(store, Repo, hierarchy);
        var state = await loader.LoadAsync(0, 2, null, null, CancellationToken.None);

        // The void fills sequence 1; sequence 2 carries the entries. No gap,
        // no damage, and the void contributed nothing.
        Assert.Empty(state.Findings);
        Assert.Empty(state.UnresolvedGaps);
        Assert.Single(state.Resolved);
    }

    [Fact]
    public async Task An_unaccounted_gap_is_patient_then_damage()
    {
        var store = CreateStore();
        using var hierarchy = new KeyHierarchy(MasterKey);
        var sequence = CreateSequence();

        using (var publisher = new IndexPublisher(store, Repo, Writer, hierarchy, sequence))
        {
            await publisher.PublishDeltaAsync(0, [], [Entry(1, 1)], CancellationToken.None);

            // Sequence 2 vanishes — allocated, never accounted.
            sequence.AllocateNext();

            await publisher.PublishDeltaAsync(0, [], [Entry(2, 1)], CancellationToken.None);
        }

        using var loader = new IndexLoader(store, Repo, hierarchy);

        // Inside patience: unresolved, not damage — a reader MUST NOT block
        // and MUST NOT interpret silence as an empty delta (07 §4).
        var patient = await loader.LoadAsync(0, gapPatienceGenerations: 2, null, null, CancellationToken.None);
        Assert.Empty(patient.Findings);
        Assert.Equal([(Writer, 2UL)], patient.UnresolvedGaps);

        // Past patience: a damage finding, surfaced rather than blocking.
        var exhausted = await loader.LoadAsync(
            currentGeneration: 5, gapPatienceGenerations: 2, null, null, CancellationToken.None);
        var finding = Assert.Single(exhausted.Findings);
        Assert.Equal(DamageKind.MissingIndexObject, finding.Kind);

        // An accounting callback (a journal record, an intent-covered blob)
        // resolves the same gap without damage (ADR-0022 §Decision 7).
        var accounted = await loader.LoadAsync(
            5, 2, (_, gap) => ValueTask.FromResult(gap == 2), null, CancellationToken.None);
        Assert.Empty(accounted.Findings);
        Assert.Empty(accounted.UnresolvedGaps);
    }

    [Fact]
    public async Task A_tampered_index_object_is_a_security_finding_and_excluded()
    {
        var store = CreateStore();
        using var hierarchy = new KeyHierarchy(MasterKey);
        var sequence = CreateSequence();

        using (var publisher = new IndexPublisher(store, Repo, Writer, hierarchy, sequence))
        {
            await publisher.PublishDeltaAsync(0, [], [Entry(1, 1)], CancellationToken.None);
        }

        // Reach beneath the store and re-seal the delta with a mutated body
        // — the encryption authenticates, but the SIGNATURE must not.
        var deltaPath = Directory
            .EnumerateFiles(Path.Combine(_root, "store", "index"), "*", SearchOption.AllDirectories)
            .Single();

        var record = FallbackPlan.Repository.Format.Records.StandaloneRecordFraming.Parse(
            await File.ReadAllBytesAsync(deltaPath));
        var metadataKey = hierarchy.DeriveMetadataKey(KeyGeneration.Zero);
        Assert.True(FallbackPlan.Repository.Packing.StandaloneRecordCipher.TryOpen(
            record, Repo, metadataKey, out var plaintext));

        var forged = IndexDeltaCodec.Decode(plaintext);
        var forgedDelta = forged.Delta with { Entries = [Entry(9, 9)] };
        var forgedBytes = IndexDeltaCodec.Encode(forgedDelta, forged.Signature.Span);

        using var deriver = new ObjectIdDeriver(hierarchy.DeriveContentIdKey());
        var forgedObjectId = deriver.Derive(Domain.ObjectType.IndexDelta, ContentHasher.Hash(forgedBytes));
        var resealed = FallbackPlan.Repository.Packing.StandaloneRecordCipher.Seal(
            Repo, metadataKey, KeyGeneration.Zero, Writer, record.Counter,
            Domain.ObjectType.IndexDelta, forgedObjectId, forgedBytes);
        await File.WriteAllBytesAsync(deltaPath, resealed);

        using var loader = new IndexLoader(store, Repo, hierarchy);
        var state = await loader.LoadAsync(0, 2, (_, _) => ValueTask.FromResult(true), null, CancellationToken.None);

        // A bad signature is substitution or forgery, not a bad disk
        // (06 §6.1) — the object is excluded and the finding says security.
        Assert.Contains(state.Findings, finding => finding.Kind == DamageKind.SecurityFinding);
        Assert.Empty(state.Resolved);
    }

    [Fact]
    public async Task A_checkpoint_subsumes_deltas_and_newer_deltas_still_apply()
    {
        var store = CreateStore();
        using var hierarchy = new KeyHierarchy(MasterKey);
        var sequence = CreateSequence();

        using (var publisher = new IndexPublisher(store, Repo, Writer, hierarchy, sequence))
        {
            var first = await publisher.PublishDeltaAsync(0, [], [Entry(1, 1)], CancellationToken.None);
            var second = await publisher.PublishDeltaAsync(0, [], [Entry(2, 1)], CancellationToken.None);

            await publisher.PublishCheckpointAsync(
                generation: 1,
                subsumedDeltaIds: [first, second],
                writerWatermarks: [new WriterWatermark(Writer, 2)],
                entries: [Entry(1, 1), Entry(2, 1)],
                predecessor: null,
                CancellationToken.None);

            // A delta beyond the watermark — applied even though a listing
            // might not have revealed it yet (07 §5).
            await publisher.PublishDeltaAsync(1, [], [Entry(3, 2)], CancellationToken.None);
        }

        using var loader = new IndexLoader(store, Repo, hierarchy);
        var state = await loader.LoadAsync(1, 2, null, null, CancellationToken.None);

        Assert.Empty(state.Findings);
        Assert.Single(state.Checkpoints);
        Assert.Equal(3, state.Resolved.Count);
        Assert.True(state.Resolved.ContainsKey(Entry(3, 2).ObjectId));
    }

    [Fact]
    public void The_sequence_store_survives_process_restarts()
    {
        var sequence = CreateSequence();
        var first = sequence.AllocateNext();
        var second = sequence.AllocateNext();
        sequence.MarkAccounted(first);

        var reloaded = CreateSequence();

        Assert.Equal([second], reloaded.RecoveredObligations);
        Assert.Equal(3UL, reloaded.AllocateNext());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
