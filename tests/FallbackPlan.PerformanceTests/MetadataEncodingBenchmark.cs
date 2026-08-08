using System.Formats.Cbor;
using System.Globalization;
using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.PerformanceTests;

/// <summary>
/// The encoding-size half of [Q4]: what canonical CBOR costs on realistic
/// manifests, against the two alternatives ADR-0003 rejected on size — a
/// bespoke binary format (the floor) and canonical JSON (the ceiling).
/// Run with <c>dotnet run -c Release -- metadata-size</c>.
/// </summary>
/// <remarks>
/// <para>
/// The corpus is generated rather than captured, and its shape is pinned to
/// the reference scales in the requirements rather than chosen: scale L is
/// 100 M file versions against 500 M segment references, so a file version
/// averages five references, and the size distribution below is built to
/// land on that mean rather than on a guess about what files look like.
/// </para>
/// <para>
/// Both comparisons are derived from the encoded CBOR itself rather than
/// from a parallel hand-written model of each manifest, so neither can
/// drift from the codecs as fields are added.
/// </para>
/// </remarks>
public static class MetadataEncodingBenchmark
{
    public static int Run()
    {
        var random = new Random(20260808);

        var fileVersions = new List<byte[]>();
        var singleSegment = new List<byte[]>();
        var totalReferences = 0L;
        for (var i = 0; i < 20_000; i++)
        {
            var manifest = SampleFileVersion(random, out var references);
            totalReferences += references;

            var encoded = FileVersionManifestCodec.Encode(manifest);
            fileVersions.Add(encoded);

            // The commonest object in a real repository, and the one where
            // self-description is worst amortised: everything but the single
            // segment reference is fixed cost.
            if (references == 1)
            {
                singleSegment.Add(encoded);
            }
        }

        var trees = new List<byte[]>();
        var totalEntries = 0L;
        for (var i = 0; i < 2_000; i++)
        {
            var manifest = SampleTree(random, out var entries);
            totalEntries += entries;
            trees.Add(TreeManifestCodec.Encode(manifest));
        }

        Console.WriteLine("Metadata encoding size — canonical CBOR against the alternatives ADR-0003 rejected");
        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"corpus: {fileVersions.Count} file versions ({totalReferences / (double)fileVersions.Count:f2} segment " +
            $"references each), {trees.Count} directories ({totalEntries / (double)trees.Count:f1} entries each)"));
        Console.WriteLine();

        Console.WriteLine("| Object | CBOR mean | Payload floor | CBOR overhead | Canonical JSON | JSON over CBOR |");
        Console.WriteLine("|--------|-----------|---------------|---------------|----------------|----------------|");

        var fileVersion = Report("File-version manifest", fileVersions);
        Report(
            string.Create(CultureInfo.InvariantCulture, $"…of which single-segment ({singleSegment.Count})"),
            singleSegment);
        var tree = Report("Tree manifest", trees);
        Report("Tree manifest, per entry", trees, divisor: totalEntries);

        Console.WriteLine();

        // Scale L: 100 M file versions, 10 M directories (the requirements'
        // 10 M files at the ~10:1 file-to-directory ratio this corpus uses).
        const double VersionsAtScaleL = 100_000_000;
        const double DirectoriesAtScaleL = 10_000_000;

        var cborTotal = (fileVersion.Cbor * VersionsAtScaleL) + (tree.Cbor * DirectoriesAtScaleL);
        var floorTotal = (fileVersion.Floor * VersionsAtScaleL) + (tree.Floor * DirectoriesAtScaleL);
        var jsonTotal = (fileVersion.Json * VersionsAtScaleL) + (tree.Json * DirectoriesAtScaleL);

        Console.WriteLine("At scale L (100 M file versions, 10 M directories), metadata plane only:");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  canonical CBOR  {Gigabytes(cborTotal)}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  payload floor   {Gigabytes(floorTotal)}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  canonical JSON  {Gigabytes(jsonTotal)}"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  CBOR costs {cborTotal / floorTotal:f3}x the floor; JSON costs {jsonTotal / cborTotal:f2}x CBOR " +
            $"({Gigabytes(jsonTotal - cborTotal)} more)"));

        return 0;
    }

    private readonly record struct Sizes(double Cbor, double Floor, double Json);

    private static Sizes Report(string label, List<byte[]> encoded, long divisor = 0)
    {
        var units = divisor > 0 ? divisor : encoded.Count;

        double cbor = 0;
        double floor = 0;
        double json = 0;

        foreach (var bytes in encoded)
        {
            cbor += bytes.Length;
            floor += PayloadFloor(bytes);
            json += JsonSize(bytes);
        }

        cbor /= units;
        floor /= units;
        json /= units;

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"| {label} | {cbor:f1} B | {floor:f1} B | {(cbor - floor) / floor:p1} | {json:f1} B | {json / cbor:f2}x |"));

        return new Sizes(cbor, floor, json);
    }

    private static string Gigabytes(double bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):f1} GiB");

    /// <summary>
    /// The bytes a self-describing-free format could not avoid: byte-string
    /// and text-string contents, the minimal big-endian width of each
    /// integer, and a minimal length prefix for each variable-length item.
    /// </summary>
    /// <remarks>
    /// Deliberately generous to the alternative. Map keys cost nothing —
    /// a bespoke format knows its own field order — and booleans cost
    /// nothing, on the assumption that flags pack into a header. The number
    /// this produces is therefore a lower bound on a bespoke encoding, which
    /// is the conservative direction: it makes CBOR's overhead look larger
    /// than any real competitor would achieve.
    /// </remarks>
    private static long PayloadFloor(byte[] encoded)
    {
        var reader = new CborReader(encoded, CborConformanceMode.Canonical);
        return Walk(reader, countKeys: false);

        static long Walk(CborReader reader, bool countKeys)
        {
            switch (reader.PeekState())
            {
                case CborReaderState.StartMap:
                {
                    var count = reader.ReadStartMap();
                    long total = 0;
                    for (var i = 0; i < count; i++)
                    {
                        reader.SkipValue(); // the key: a bespoke format carries no key
                        total += Walk(reader, countKeys);
                    }

                    reader.ReadEndMap();
                    return total;
                }

                case CborReaderState.StartArray:
                {
                    var count = reader.ReadStartArray();
                    long total = LengthPrefix(count ?? 0);
                    for (var i = 0; i < count; i++)
                    {
                        total += Walk(reader, countKeys);
                    }

                    reader.ReadEndArray();
                    return total;
                }

                case CborReaderState.ByteString:
                {
                    var value = reader.ReadByteString();
                    return value.Length + LengthPrefix(value.Length);
                }

                case CborReaderState.TextString:
                {
                    var value = reader.ReadTextString();
                    var length = Encoding.UTF8.GetByteCount(value);
                    return length + LengthPrefix(length);
                }

                case CborReaderState.UnsignedInteger:
                {
                    var value = reader.ReadUInt64();
                    return MinimalWidth(value);
                }

                case CborReaderState.NegativeInteger:
                {
                    reader.ReadInt64();
                    return 8;
                }

                case CborReaderState.Boolean:
                    reader.ReadBoolean();
                    return 0;

                default:
                    reader.SkipValue();
                    return 0;
            }
        }

        static long LengthPrefix(int count) => count switch
        {
            < 256 => 1,
            < 65_536 => 2,
            _ => 4,
        };

        static long MinimalWidth(ulong value) => value switch
        {
            <= byte.MaxValue => 1,
            <= ushort.MaxValue => 2,
            <= uint.MaxValue => 4,
            _ => 8,
        };
    }

    /// <summary>
    /// The same logical content rendered as canonical JSON: integer map keys
    /// become quoted strings, byte strings become base64. This is what
    /// JSON/JCS costs, which is what ADR-0003 rejected it on.
    /// </summary>
    private static long JsonSize(byte[] encoded)
    {
        var reader = new CborReader(encoded, CborConformanceMode.Canonical);
        var builder = new StringBuilder();
        Write(reader, builder);
        return Encoding.UTF8.GetByteCount(builder.ToString());

        static void Write(CborReader reader, StringBuilder builder)
        {
            switch (reader.PeekState())
            {
                case CborReaderState.StartMap:
                {
                    var count = reader.ReadStartMap();
                    builder.Append('{');
                    for (var i = 0; i < count; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(',');
                        }

                        builder.Append('"').Append(reader.ReadUInt32()).Append("\":");
                        Write(reader, builder);
                    }

                    reader.ReadEndMap();
                    builder.Append('}');
                    return;
                }

                case CborReaderState.StartArray:
                {
                    var count = reader.ReadStartArray();
                    builder.Append('[');
                    for (var i = 0; i < count; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(',');
                        }

                        Write(reader, builder);
                    }

                    reader.ReadEndArray();
                    builder.Append(']');
                    return;
                }

                case CborReaderState.ByteString:
                    builder.Append('"').Append(Convert.ToBase64String(reader.ReadByteString())).Append('"');
                    return;

                case CborReaderState.TextString:
                    builder.Append('"').Append(reader.ReadTextString()).Append('"');
                    return;

                case CborReaderState.UnsignedInteger:
                    builder.Append(reader.ReadUInt64().ToString(CultureInfo.InvariantCulture));
                    return;

                case CborReaderState.NegativeInteger:
                    builder.Append(reader.ReadInt64().ToString(CultureInfo.InvariantCulture));
                    return;

                case CborReaderState.Boolean:
                    builder.Append(reader.ReadBoolean() ? "true" : "false");
                    return;

                default:
                    reader.SkipValue();
                    builder.Append("null");
                    return;
            }
        }
    }

    /// <summary>
    /// One file version, from a distribution built to average the five
    /// segment references per version the reference scales imply.
    /// </summary>
    private static FileVersionManifest SampleFileVersion(Random random, out int references)
    {
        var roll = random.NextDouble();
        references = roll switch
        {
            < 0.68 => 1,                       // documents, source, configuration
            < 0.90 => random.Next(2, 7),       // spreadsheets, small archives
            < 0.985 => random.Next(8, 29),     // photographs, audio
            _ => random.Next(90, 171),         // video, VM images, large archives
        };

        var segments = new List<SegmentReference>(references);
        long offset = 0;
        for (var i = 0; i < references; i++)
        {
            long length = random.Next(48 * 1024, 80 * 1024);
            segments.Add(new SegmentReference(offset, length, RandomObjectId(random)));
            offset += length;
        }

        var name = RandomName(random);

        return new FileVersionManifest
        {
            EntryKind = EntryKind.File,
            Name = Encoding.UTF8.GetBytes(name),
            NameNormalisation = NameNormalisation.Nfc,
            LogicalLength = (ulong)offset,
            SegmentReferences = segments,
            WholeFileHash = RandomBytes(random, 32),
            SegmentationProfile = 0x0002,

            // Most versions in a live repository replace one: a repository
            // whose manifests mostly had no parent would be one nobody had
            // backed up twice.
            ParentVersion = random.NextDouble() < 0.85 ? RandomObjectId(random) : null,
            Metadata = new EntryMetadata
            {
                ModifiedAt = 1_722_600_000_000 + (ulong)random.Next(0, 10_000_000),
                CreatedAt = 1_700_000_000_000 + (ulong)random.Next(0, 10_000_000),
                AccessedAt = 1_722_600_000_000 + (ulong)random.Next(0, 10_000_000),
                PosixMode = 0x1A4,
                OwnerName = "alex",
                GroupName = "staff",

                // A minority of files carry extended attributes on a real
                // POSIX tree — quarantine flags, Finder metadata, SELinux
                // labels — and they are the largest optional field here.
                ExtendedAttributes = random.NextDouble() < 0.15
                    ?
                    [
                        new ExtendedAttributeEntry(
                            "user.com.apple.quarantine"u8.ToArray(), RandomBytes(random, 42)),
                    ]
                    : [],
            },
        };
    }

    /// <summary>One directory, from a width distribution with a long tail.</summary>
    private static TreeManifest SampleTree(Random random, out int entries)
    {
        var roll = random.NextDouble();
        entries = roll switch
        {
            < 0.60 => random.Next(1, 9),
            < 0.90 => random.Next(9, 41),
            < 0.99 => random.Next(41, 201),
            _ => random.Next(201, 1_001),
        };

        var children = new List<TreeEntry>(entries);
        for (var i = 0; i < entries; i++)
        {
            children.Add(new TreeEntry(
                Encoding.UTF8.GetBytes(RandomName(random)),
                RandomObjectId(random),
                random.NextDouble() < 0.1 ? EntryKind.DirectoryPlaceholder : EntryKind.File));
        }

        children.Sort((left, right) => left.Name.Span.SequenceCompareTo(right.Name.Span));

        return new TreeManifest
        {
            Entries = children,
            Name = Encoding.UTF8.GetBytes(RandomName(random)),
            NameNormalisation = NameNormalisation.Nfc,
            Metadata = new EntryMetadata
            {
                ModifiedAt = 1_722_600_000_000 + (ulong)random.Next(0, 10_000_000),
                PosixMode = 0x1ED,
                OwnerName = "alex",
                GroupName = "staff",
            },
        };
    }

    private static string RandomName(Random random)
    {
        const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789-_";

        // 8-40 bytes, which is where real filenames sit once extensions and
        // date stamps are included.
        var length = random.Next(8, 41);
        return string.Create(length, random, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[source.Next(Alphabet.Length)];
            }
        });
    }

    private static ObjectId RandomObjectId(Random random) => ObjectId.FromBytes(RandomBytes(random, 32));

    private static byte[] RandomBytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
