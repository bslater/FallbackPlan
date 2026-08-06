using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Format.Compression;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Repository.Tests.Packing;

/// <summary>
/// The C1 acceptance criteria (specification 05 §6; FR-ARCH-011,
/// NFR-REL-001, NFR-REL-005): resume re-emits the checkpointed sealed bytes
/// verbatim — byte-identical to an uninterrupted run — and <b>any</b> pinned
/// field mismatch, the codec version above all, forces a restart under a
/// fresh salt rather than a resume.
/// </summary>
public sealed class SpoolCheckpointTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private static readonly byte[] Salt = [.. Enumerable.Repeat((byte)0x5A, 32)];

    private static readonly byte[] ClassKey = [.. Enumerable.Range(100, 32).Select(value => (byte)value)];

    private static readonly SpoolPinnedConfiguration Pinned = new(
        SegmentationProfile.FixedV1.Value,
        64 * 1024,
        0,
        0,
        CompressionProfile.ZstdV1.Value,
        ZstdSegmentCodec.CodecVersion,
        EncryptionProfile.Aes256GcmV1.Value);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-spool-tests", Guid.NewGuid().ToString("n"));

    private string SpoolDirectory(string name) => Path.Combine(_root, name);

    private static BlobWriter CreateWriter(string directory, SpoolPinnedConfiguration? pinned) =>
        BlobWriter.Create(
            Repo,
            Writer,
            KeyGeneration.Zero,
            BlobClass.Data,
            ClassKey,
            blobCounter: 7,
            EncryptionProfile.Aes256GcmV1,
            BlobWriteProfile.LocalDefault,
            directory,
            Salt,
            pinned);

    private static ResumeResult Resume(string directory, SpoolPinnedConfiguration current) =>
        BlobWriter.TryResume(
            directory,
            Repo,
            Writer,
            KeyGeneration.Zero,
            BlobClass.Data,
            ClassKey,
            EncryptionProfile.Aes256GcmV1,
            BlobWriteProfile.LocalDefault,
            current);

    private static (ObjectId Id, byte[] Payload) Record(int seed)
    {
        var payload = new byte[10_000 + seed];
        new Random(seed).NextBytes(payload);
        var id = ObjectId.FromBytes(SHA256.HashData(payload));
        return (id, payload);
    }

    private static async Task AppendAsync(BlobWriter writer, int seed)
    {
        var (id, payload) = Record(seed);
        await writer.AppendRecordAsync(
            ObjectType.SegmentRecord, id, CompressionProfile.None, (ulong)payload.Length, payload, CancellationToken.None);
    }

    [Fact]
    public async Task Resume_produces_a_sealed_blob_byte_identical_to_an_uninterrupted_run()
    {
        // Uninterrupted reference run: five records, sealed.
        var referenceDirectory = SpoolDirectory("reference");
        string referenceBytesPath;
        var reference = CreateWriter(referenceDirectory, Pinned);
        await using (reference.ConfigureAwait(false))
        {
            for (var seed = 0; seed < 5; seed++)
            {
                await AppendAsync(reference, seed);
            }

            var sealedReference = await reference.SealAsync(CancellationToken.None);
            await using (sealedReference.ConfigureAwait(false))
            {
                referenceBytesPath = Path.Combine(_root, "reference.sealed");
                var content = await sealedReference.OpenContentAsync(CancellationToken.None);
                await using (content.ConfigureAwait(false))
                {
                    using var output = File.Create(referenceBytesPath);
                    await content.CopyToAsync(output);
                }
            }
        }

        // Interrupted run: three records, then a crash — handles released,
        // spool and checkpoint left on disk.
        var interruptedDirectory = SpoolDirectory("interrupted");
        var interrupted = CreateWriter(interruptedDirectory, Pinned);
        for (var seed = 0; seed < 3; seed++)
        {
            await AppendAsync(interrupted, seed);
        }

        await interrupted.AbandonAsync();

        // Resume, append the remaining two records, seal.
        var resume = Resume(interruptedDirectory, Pinned);
        var resumed = Assert.IsType<ResumeResult.Resumed>(resume);

        Assert.Equal(3, resumed.Writer.RecordCount);

        await AppendAsync(resumed.Writer, 3);
        await AppendAsync(resumed.Writer, 4);

        var sealedResumed = await resumed.Writer.SealAsync(CancellationToken.None);
        byte[] resumedBytes;
        await using (sealedResumed.ConfigureAwait(false))
        {
            using var memory = new MemoryStream();
            var content = await sealedResumed.OpenContentAsync(CancellationToken.None);
            await using (content.ConfigureAwait(false))
            {
                await content.CopyToAsync(memory);
            }

            resumedBytes = memory.ToArray();
        }

        await resumed.Writer.DisposeAsync();

        // 05 §6.3: byte-identical to the uninterrupted blob — same salt, same
        // records, same ordinals, and the checkpointed bytes re-emitted
        // verbatim rather than recomputed.
        Assert.Equal(await File.ReadAllBytesAsync(referenceBytesPath), resumedBytes);
    }

    [Fact]
    public async Task A_changed_codec_version_forces_restart_and_discards_the_spool()
    {
        var directory = SpoolDirectory("codec");
        var writer = CreateWriter(directory, Pinned);
        await AppendAsync(writer, 1);
        await writer.AbandonAsync();

        var result = Resume(directory, Pinned with { CodecVersion = "ZstdSharp.Port 9.9.9" });

        var restart = Assert.IsType<ResumeResult.MustRestart>(result);
        Assert.Equal("codec_version_changed", restart.Reason);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("segmentation_profile_changed")]
    [InlineData("segmentation_parameters_changed")]
    [InlineData("compression_profile_changed")]
    public async Task Any_changed_pinned_field_forces_restart(string expectedReason)
    {
        var directory = SpoolDirectory(expectedReason);
        var writer = CreateWriter(directory, Pinned);
        await AppendAsync(writer, 2);
        await writer.AbandonAsync();

        var mutated = expectedReason switch
        {
            "segmentation_profile_changed" => Pinned with { SegmentationProfile = SegmentationProfile.CdcV1.Value },
            "segmentation_parameters_changed" => Pinned with { SegmentationParameter1 = 128 * 1024 },
            "compression_profile_changed" => Pinned with { CompressionProfile = CompressionProfile.None.Value },
            _ => throw new InvalidOperationException(),
        };

        var result = Resume(directory, mutated);

        var restart = Assert.IsType<ResumeResult.MustRestart>(result);
        Assert.Equal(expectedReason, restart.Reason);
    }

    [Fact]
    public async Task A_changed_key_generation_forces_restart()
    {
        var directory = SpoolDirectory("generation");
        var writer = CreateWriter(directory, Pinned);
        await AppendAsync(writer, 3);
        await writer.AbandonAsync();

        var result = BlobWriter.TryResume(
            directory, Repo, Writer, new KeyGeneration(1), BlobClass.Data, ClassKey,
            EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, Pinned);

        var restart = Assert.IsType<ResumeResult.MustRestart>(result);
        Assert.Equal("key_generation_changed", restart.Reason);
    }

    [Fact]
    public async Task A_torn_checkpoint_forces_restart()
    {
        var directory = SpoolDirectory("torn");
        var writer = CreateWriter(directory, Pinned);
        await AppendAsync(writer, 4);
        await writer.AbandonAsync();

        var checkpointPath = Directory.GetFiles(directory, "*.checkpoint").Single();
        var bytes = await File.ReadAllBytesAsync(checkpointPath);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(checkpointPath, bytes);

        var result = Resume(directory, Pinned);

        var restart = Assert.IsType<ResumeResult.MustRestart>(result);
        Assert.Equal("checkpoint_unreadable", restart.Reason);
    }

    [Fact]
    public async Task A_torn_spool_tail_beyond_the_watermark_is_truncated_on_resume()
    {
        var directory = SpoolDirectory("tail");
        var writer = CreateWriter(directory, Pinned);
        await AppendAsync(writer, 5);
        await writer.AbandonAsync();

        // Simulate a torn write after the last checkpoint: garbage beyond
        // the watermark that a crash left behind.
        var spoolPath = Directory.GetFiles(directory, "*.spool").Single();
        using (var spool = new FileStream(spoolPath, FileMode.Append, FileAccess.Write))
        {
            spool.Write(new byte[137]);
        }

        var result = Resume(directory, Pinned);
        var resumed = Assert.IsType<ResumeResult.Resumed>(result);

        Assert.Equal(1, resumed.Writer.RecordCount);
        await resumed.Writer.DisposeAsync();
    }

    [Fact]
    public void An_empty_spool_directory_reports_no_spool()
    {
        var directory = SpoolDirectory("empty");
        Directory.CreateDirectory(directory);

        Assert.IsType<ResumeResult.NoSpool>(Resume(directory, Pinned));
    }

    [Fact]
    public async Task A_restarted_blob_draws_a_fresh_salt_and_therefore_a_different_key()
    {
        // Restart is the safe failure precisely because a fresh CSPRNG salt
        // makes the new blob a different key (05 §6.2-§6.3) — ordinals
        // beginning again at zero reuse nothing. Two Create calls without a
        // caller-pinned salt must never share one.
        var firstSalt = await AbandonAndReadSaltAsync("salt-a");
        var secondSalt = await AbandonAndReadSaltAsync("salt-b");

        Assert.NotEqual(firstSalt, secondSalt);
    }

    private async Task<byte[]> AbandonAndReadSaltAsync(string name)
    {
        var writer = BlobWriter.Create(
            Repo, Writer, KeyGeneration.Zero, BlobClass.Data, ClassKey, blobCounter: 7,
            EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, SpoolDirectory(name),
            blobSalt: default, pinned: Pinned);
        await writer.AbandonAsync();

        var spoolPath = Directory.GetFiles(SpoolDirectory(name), "*.spool").Single();
        var envelope = new byte[BlobEnvelope.Length];
        using var stream = File.OpenRead(spoolPath);
        await stream.ReadExactlyAsync(envelope);
        return BlobEnvelope.Parse(envelope).BlobSalt.ToArray();
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
