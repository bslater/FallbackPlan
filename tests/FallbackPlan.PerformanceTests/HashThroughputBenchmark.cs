using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Bodu.Security.Cryptography;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Compression;

namespace FallbackPlan.PerformanceTests;

/// <summary>
/// Q6's measurement (ADR-0004): what the content-hash function costs per
/// byte, against the one faster alternative a managed implementation now
/// makes portable and against the pipeline stages that share a core with it.
/// Numbers land in docs/segment-hash-benchmark.md.
/// </summary>
/// <remarks>
/// <para>
/// BLAKE3 is measured here and used nowhere. It reaches this project only
/// through Repository.Crypto's package, whose use ADR-0019 confines to the
/// primitives its amendments admit. A non-cryptographic hash is not measured
/// at all: the second-preimage requirement excludes one whatever it costs.
/// </para>
/// <para>
/// SHA-256 is the platform's, which on Linux is OpenSSL, and OpenSSL uses
/// the SHA extensions where the processor has them. The reference machine
/// requires AES-NI and says nothing of those, so the page reports a second
/// run with <c>OPENSSL_ia32cap=:~0x20000000</c>, which hides them.
/// </para>
/// </remarks>
public static class HashThroughputBenchmark
{
    private const int Trials = 5;

    public static int Run()
    {
        var random = new Random(42);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} logical processors"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"OPENSSL_ia32cap={Environment.GetEnvironmentVariable("OPENSSL_ia32cap") ?? "(unset)"}"));

        foreach (var size in new[] { 64 * 1024, 1024 * 1024 })
        {
            Console.WriteLine();
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{size / 1024} KiB inputs, MiB/s on one thread, median of {Trials}"));

            var incompressible = new byte[size];
            random.NextBytes(incompressible);
            var textLike = TextLike(random, size);
            var digest = new byte[64];

            Row("SHA-256, the content id", size, () => ContentHasher.Hash(incompressible));
            Row("SHA-512, SHA-512/256's function", size, () => SHA512.HashData(incompressible, digest));
            using (var blake3 = new Blake3())
            {
                Row("BLAKE3, managed", size, () => blake3.TryComputeHash(incompressible, digest, out _));
            }

            var key = new byte[32];
            random.NextBytes(key);
            using (var gcm = new AesGcm(key, 16))
            {
                var nonce = new byte[12];
                var tag = new byte[16];
                var sealedBytes = new byte[size];
                Row("AES-256-GCM seal", size, () => gcm.Encrypt(nonce, incompressible, sealedBytes, tag));
            }

            using var codec = new ZstdSegmentCodec(CompressionSettings.Default.ZstdLevel, size);
            var frame = new byte[size];
            var threshold = CompressionSettings.Default.ThresholdPermille;
            Row("zstd-v1 level 3, text-like", size, () => codec.TryCompressForStorage(textLike, threshold, frame, out _));
            Row("zstd-v1 level 3, incompressible", size, () => codec.TryCompressForStorage(incompressible, threshold, frame, out _));
        }

        // A whole large file, where BLAKE3's tree can use every processor and
        // SHA-256's chain cannot: the shape of the whole-file hash.
        Console.WriteLine();
        Console.WriteLine("16 MiB input, MiB/s");
        var large = new byte[16 * 1024 * 1024];
        random.NextBytes(large);
        var output = new byte[32];
        Row("SHA-256", large.Length, () => SHA256.HashData(large, output));
        using (var serial = new Blake3())
        {
            Row("BLAKE3, one thread", large.Length, () => serial.TryComputeHash(large, output, out _));
        }

        using (var parallel = new Blake3(-1))
        {
            Row("BLAKE3, every processor", large.Length, () => parallel.TryComputeHash(large, output, out _));
        }

        return 0;
    }

    private static void Row(string name, int bytesPerCall, Action call) =>
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {name,-36} {Measure(call, bytesPerCall),8:F0}"));

    /// <summary>A warm-up, then the median of five 0.3 s trials.</summary>
    private static double Measure(Action call, int bytesPerCall)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 0.4)
        {
            call();
        }

        var rates = new double[Trials];
        for (var trial = 0; trial < Trials; trial++)
        {
            long bytes = 0;
            clock.Restart();
            while (clock.Elapsed.TotalSeconds < 0.3)
            {
                call();
                bytes += bytesPerCall;
            }

            rates[trial] = bytes / clock.Elapsed.TotalSeconds / (1024 * 1024);
        }

        Array.Sort(rates);
        return rates[Trials / 2];
    }

    /// <summary>
    /// Words and numbers, which zstd level 3 packs about three to one: a
    /// stand-in for documents and source trees, not a corpus.
    /// </summary>
    private static byte[] TextLike(Random random, int size)
    {
        string[] words = ["backup ", "snapshot ", "segment ", "restore ", "manifest ", "journal ", "the ", "of ", "and ", "a "];
        var text = new byte[size];
        var at = 0;
        while (at < size)
        {
            var word = System.Text.Encoding.ASCII.GetBytes(
                words[random.Next(words.Length)] + random.Next(1000).ToString(CultureInfo.InvariantCulture));
            var length = Math.Min(word.Length, size - at);
            word.AsSpan(0, length).CopyTo(text.AsSpan(at));
            at += length;
        }

        return text;
    }
}
