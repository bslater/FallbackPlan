using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Format.Cbor;
using FallbackPlan.Repository.Format.Resources;

namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>
/// Encodes and decodes reference runs (ADR-0202), and checks a file's runs
/// against the manifest that lists them. A run's references and holes, in
/// ascending order, cover its stretch exactly, as a manifest's cover its file
/// (06 §3.2), and an unknown key is refused as a manifest's is (06 §3.1).
/// </summary>
public static class ReferenceRunCodec
{
    /// <summary>
    /// The most references and holes a run holds, together: a fixed count, so
    /// that a file is the same objects on every device, and at most about
    /// 14 MB encoded, inside the 16 MiB an object may be.
    /// </summary>
    public const int MaxPieces = 262_144;

    /// <summary>Encodes a run canonically, validating it first.</summary>
    /// <exception cref="ManifestValidationException">The run breaks ADR-0202's rules.</exception>
    public static byte[] Encode(ReferenceRun run)
    {
        ThrowHelper.ThrowIfNull(run);
        Validate(run);

        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(run.SparseExtents.Count > 0 ? 4 : 3);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(run.Start);
        writer.WriteKey(2);
        writer.WriteUnsignedInteger(run.Length);
        writer.WriteKey(3);
        writer.WriteStartArray(run.SegmentReferences.Count);
        foreach (var reference in run.SegmentReferences)
        {
            FileVersionManifestCodec.WriteSegmentReference(writer, reference);
        }

        writer.WriteEndArray();
        if (run.SparseExtents.Count > 0)
        {
            writer.WriteKey(4);
            writer.WriteStartArray(run.SparseExtents.Count);
            foreach (var extent in run.SparseExtents)
            {
                writer.WriteStartArray(2);
                writer.WriteUnsignedInteger(extent.Offset);
                writer.WriteUnsignedInteger(extent.Length);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndMap();
        return writer.Encode();
    }

    /// <summary>Decodes and validates a run.</summary>
    /// <exception cref="ManifestValidationException">The bytes are not a run ADR-0202 allows — a damage finding.</exception>
    public static ReferenceRun Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new ManifestValidationException(Strings.FormatReferenceRunCodec_NotCanonicalCbor(exception.Message), exception);
        }
    }

    /// <summary>
    /// Checks that <paramref name="runs"/> are the ones <paramref name="manifest"/>
    /// lists, as a file's: as many as it lists, the first from 0, each where
    /// the one before ended, the last at the file's length, and each but the
    /// last full. A file's runs that fail any of it are a damage finding.
    /// </summary>
    /// <exception cref="ManifestValidationException">The runs do not describe the file.</exception>
    public static void ValidateSequence(FileVersionManifest manifest, IReadOnlyList<ReferenceRun> runs)
    {
        ThrowHelper.ThrowIfNull(manifest);
        ThrowHelper.ThrowIfNull(runs);

        if (runs.Count != manifest.ReferenceRuns.Count)
        {
            throw new ManifestValidationException(Strings.FormatReferenceRunCodec_SequenceCount(manifest.ReferenceRuns.Count, runs.Count));
        }

        var sequence = new Sequence(manifest);
        foreach (var run in runs)
        {
            sequence.Next(run);
        }

        sequence.End();
    }

    private static ReferenceRun DecodeCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);
        var count = reader.ReadStartMap();

        ulong? start = null;
        ulong? length = null;
        List<SegmentReference>? references = null;
        List<SparseExtent> holes = [];

        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    start = reader.ReadUnsignedInteger();
                    break;
                case 2:
                    length = reader.ReadUnsignedInteger();
                    break;
                case 3:
                    references = FileVersionManifestCodec.ReadSegmentReferences(reader);
                    break;
                case 4:
                    holes = FileVersionManifestCodec.ReadSparseExtents(reader);
                    break;
                default:
                    throw new ManifestValidationException(Strings.ReferenceRunCodec_UnknownKey);
            }
        }

        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        if (start is null || length is null || references is null)
        {
            throw new ManifestValidationException(Strings.ReferenceRunCodec_OmitsMandatoryKey);
        }

        var run = new ReferenceRun
        {
            Start = start.Value,
            Length = length.Value,
            SegmentReferences = references,
            SparseExtents = holes,
        };

        Validate(run);
        return run;
    }

    private static void Validate(ReferenceRun run)
    {
        var count = run.SegmentReferences.Count + run.SparseExtents.Count;
        if (count == 0)
        {
            throw new ManifestValidationException(Strings.ReferenceRunCodec_Empty);
        }

        if (count > MaxPieces)
        {
            throw new ManifestValidationException(Strings.FormatReferenceRunCodec_TooManyPieces(count));
        }

        var pieces = new List<(ulong Offset, ulong Length)>(count);
        foreach (var reference in run.SegmentReferences)
        {
            if (reference.LogicalOffset < 0 || reference.LogicalLength <= 0)
            {
                throw new ManifestValidationException(Strings.FileVersionManifestCodec_SegmentReferenceSOffsetMust);
            }

            pieces.Add(((ulong)reference.LogicalOffset, (ulong)reference.LogicalLength));
        }

        foreach (var extent in run.SparseExtents)
        {
            if (extent.Length == 0)
            {
                throw new ManifestValidationException(Strings.FileVersionManifestCodec_SparseExtentSLengthMust);
            }

            pieces.Add((extent.Offset, extent.Length));
        }

        pieces.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));

        var expected = run.Start;
        foreach (var (offset, length) in pieces)
        {
            if (offset != expected)
            {
                throw new ManifestValidationException(Strings.FormatReferenceRunCodec_CoverageBreaks(expected, offset));
            }

            expected = checked(offset + length);
        }

        if (expected != checked(run.Start + run.Length))
        {
            throw new ManifestValidationException(Strings.FormatReferenceRunCodec_CoverageShort(expected, run.Start + run.Length));
        }

        for (var i = 1; i < run.SegmentReferences.Count; i++)
        {
            if (run.SegmentReferences[i].LogicalOffset <= run.SegmentReferences[i - 1].LogicalOffset)
            {
                throw new ManifestValidationException(Strings.FileVersionManifestCodec_SegmentReferencesMUSTOrderedAscending);
            }
        }
    }

    /// <summary>
    /// A file's runs held to its manifest one at a time, as a reader meets
    /// them: each starts where the one before it ended and is full unless it
    /// is the last, and the last ends at the file's length.
    /// </summary>
    internal sealed class Sequence(FileVersionManifest manifest)
    {
        private ulong _end;
        private int _index;

        /// <summary>Holds the next run to the manifest.</summary>
        /// <exception cref="ManifestValidationException">The run does not follow the one before it, or is not full though another follows it.</exception>
        public void Next(ReferenceRun run)
        {
            if (run.Start != _end)
            {
                throw new ManifestValidationException(Strings.FormatReferenceRunCodec_SequenceBreaks(_index, run.Start, _end));
            }

            var pieces = run.SegmentReferences.Count + run.SparseExtents.Count;
            if (_index < manifest.ReferenceRuns.Count - 1 && pieces != MaxPieces)
            {
                throw new ManifestValidationException(Strings.FormatReferenceRunCodec_SequenceNotFull(_index, pieces));
            }

            _end = checked(run.Start + run.Length);
            _index++;
        }

        /// <summary>Holds the runs, all read, to the file's length.</summary>
        /// <exception cref="ManifestValidationException">The runs end short of the file's length, or past it.</exception>
        public void End()
        {
            if (_end != manifest.LogicalLength)
            {
                throw new ManifestValidationException(Strings.FormatReferenceRunCodec_SequenceShort(_end, manifest.LogicalLength));
            }
        }
    }
}
