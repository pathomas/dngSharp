using System.Buffers.Binary;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Codecs.LosslessJpeg;

/// <summary>
/// ITU T.81 lossless JPEG (SOF3) decoder. Mirrors the decode side of
/// <c>dng_lossless_jpeg_shared.cpp</c>. DNG stores raw sensor data this
/// way (Compression = 7 with photometric = CFA / LinearRaw).
///
/// <para>Covers predictor modes 1–7 from Annex H.1, 1- to 4-component scans,
/// precision up to 16 bits, and the standard byte-stuffing rules. The DNG
/// SDK only writes predictor 1 for raw data; modes 2–7 are supported for
/// reading 3rd-party files.</para>
///
/// <para><b>Not covered:</b> the rare point-transform (Pt &gt; 0) case, restart
/// markers (DRI / RST<i>m</i> — DNG SDK doesn't write them, so neither do we
/// need to read them initially), the 16-bit "bug" workaround
/// (<c>bug16</c> flag in C++ which mishandles certain 16-bit predictor wraps
/// for back-compat with broken cameras). These throw <see cref="DngError.NotYetImplemented"/>.</para>
/// </summary>
public sealed class LosslessJpegDecoder : IRawDecoder
{
    public Compression Compression => Compression.Jpeg;

    public void Decode(ReadOnlySpan<byte> compressed, PixelBuffer destination, bool bigEndian)
    {
        // We need random access plus position rollback for marker scanning;
        // copy to an array so we can take ReadOnlyMemory<byte> handles.
        Decode(compressed.ToArray(), destination);
    }

    private static void Decode(ReadOnlyMemory<byte> payload, PixelBuffer destination)
    {
        var p = new Parser(payload);
        p.ExpectMarker(0xFFD8);  // SOI

        SofData? sof = null;
        var dcTables = new HuffmanTable?[4];

        while (true)
        {
            ushort marker = p.ReadMarker();
            switch (marker)
            {
                case 0xFFC3:  // SOF3 — Lossless (sequential)
                    sof = p.ReadSof();
                    break;
                case 0xFFC4:  // DHT
                    p.ReadDht(dcTables);
                    break;
                case 0xFFDA:  // SOS
                    if (sof is null) DngThrow.BadFormat("Lossless JPEG: SOS before SOF3");
                    var sos = p.ReadSos(sof);
                    DecodeScan(ref p, sof, sos, dcTables, destination);
                    return;  // single scan per DNG strip
                case 0xFFDD:  // DRI — restart interval
                    DngThrow.NotYetImplemented("Lossless JPEG with restart markers");
                    break;
                case 0xFFD9:  // EOI before SOS
                    DngThrow.BadFormat("Lossless JPEG: EOI before any scan");
                    break;
                default:
                    if ((marker & 0xFF00) == 0xFF00)
                    {
                        // Unknown segment with length — skip its body.
                        p.SkipSegment();
                    }
                    else
                    {
                        DngThrow.BadFormat($"Lossless JPEG: expected marker, got 0x{marker:X4}");
                    }
                    break;
            }
        }
    }

    private static void DecodeScan(
        ref Parser parser, SofData sof, SosData sos,
        HuffmanTable?[] dcTables, PixelBuffer destination)
    {
        // Each pixel: for each component, decode an SSSS magnitude category,
        // then read SSSS more bits and sign-extend to get the difference d.
        // The reconstructed sample = predict(neighbors) + d, masked to precision.

        var entropy = parser.RemainingMemory;
        var reader = new JpegBitReader(entropy);

        int components = sof.Components.Count;
        int width = sof.Width;
        int height = sof.Height;
        int precision = sof.Precision;
        int initial = 1 << (precision - 1);  // start-of-row/start-of-image seed

        // SOS predictor (Ss). Use the predictor for ALL pixels except where the
        // spec degenerates: first pixel of image uses 'initial', first column
        // of subsequent rows uses Rb (above), first row uses Ra (left).
        int predictor = sos.PredictorSelector;
        if (predictor is < 1 or > 7)
            throw new DngException(DngError.BadFormat, $"Lossless JPEG: predictor {predictor} out of range");

        // Sanity-check destination shape.
        long expectedSamples = (long)width * height * components;
        long destSamples = (long)destination.Area.W * destination.Area.H * destination.Planes;
        if (expectedSamples != destSamples)
            throw new DngException(DngError.BadFormat,
                $"Lossless JPEG: scan size {width}×{height}×{components} doesn't match destination "
                + $"{destination.Area.W}×{destination.Area.H}×{destination.Planes}");
        if (destination.PixelSize != (precision <= 8 ? 1 : 2))
            throw new DngException(DngError.BadFormat,
                $"Lossless JPEG: precision {precision} doesn't match destination pixel size {destination.PixelSize}");

        var dst = destination.Memory.Span;
        Span<int> prevRow = new int[width * components];
        Span<int> curRow = new int[width * components];

        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                for (int c = 0; c < components; c++)
                {
                    int tableIdx = sos.DcTableSelectors[c];
                    var table = dcTables[tableIdx]
                        ?? throw new DngException(DngError.BadFormat, $"Lossless JPEG: missing DC table {tableIdx}");

                    int ssss = table.Decode(ref reader);
                    int diff;
                    if (ssss == 0) diff = 0;
                    else if (ssss == 16) diff = -32768;     // T.81 H.1.2.2 special case
                    else
                    {
                        int bits = reader.ReadBits(ssss);
                        diff = Extend(bits, ssss);
                    }

                    int prediction = PredictSample(
                        predictor, row, col, c, components, initial, prevRow, curRow);

                    int sample = (prediction + diff) & ((1 << precision) - 1);
                    curRow[col * components + c] = sample;

                    // Store.
                    long dstOff = destination.OffsetBytes(
                        destination.Area.T + row, destination.Area.L + col, (uint)c);
                    if (destination.PixelSize == 1)
                        dst[(int)dstOff] = (byte)sample;
                    else
                        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice((int)dstOff, 2), (ushort)sample);
                }
            }
            // Swap row buffers for next iteration. ValueTuple deconstruction
            // doesn't allow ref-struct generic args, so swap by hand.
            var tmp = prevRow;
            prevRow = curRow;
            curRow = tmp;
        }
    }

    /// <summary>
    /// Apply predictor mode <paramref name="mode"/> (1..7) at
    /// (<paramref name="row"/>, <paramref name="col"/>) for component
    /// <paramref name="c"/>. Mirrors T.81 Annex H.1.
    /// </summary>
    private static int PredictSample(
        int mode, int row, int col, int c, int comps, int initial,
        ReadOnlySpan<int> prevRow, ReadOnlySpan<int> curRow)
    {
        // Ra = same-row, previous-col; Rb = prev-row, same-col; Rc = prev-row, prev-col.
        // Degenerate cases override the predictor.
        if (row == 0 && col == 0) return initial;
        if (row == 0) return curRow[(col - 1) * comps + c]; // first row: predict by left
        if (col == 0) return prevRow[c];                    // first col: predict by above

        int ra = curRow[(col - 1) * comps + c];
        int rb = prevRow[col * comps + c];
        int rc = prevRow[(col - 1) * comps + c];

        return mode switch
        {
            1 => ra,
            2 => rb,
            3 => rc,
            4 => ra + rb - rc,
            5 => ra + ((rb - rc) >> 1),
            6 => rb + ((ra - rc) >> 1),
            7 => (ra + rb) >> 1,
            _ => 0,
        };
    }

    /// <summary>
    /// JPEG sign-extension: convert an <paramref name="ssss"/>-bit value
    /// <paramref name="v"/> into a signed difference in
    /// [-2^ssss + 1, 2^ssss - 1]. Mirrors T.81 F.2.1.3.1.
    /// </summary>
    private static int Extend(int v, int ssss)
    {
        int vt = 1 << (ssss - 1);
        if (v < vt) v += (-1 << ssss) + 1;
        return v;
    }

    // ---- Marker parsing -----------------------------------------------------

    private struct Parser
    {
        private readonly ReadOnlyMemory<byte> _data;
        private int _pos;

        public Parser(ReadOnlyMemory<byte> data) { _data = data; _pos = 0; }

        public ReadOnlyMemory<byte> RemainingMemory => _data[_pos..];

        public ushort ReadMarker()
        {
            // Skip junk until we hit 0xFF.
            while (_pos < _data.Length && _data.Span[_pos] != 0xFF) _pos++;
            // Skip "fill" 0xFF bytes between markers (JPEG spec allows runs of 0xFF).
            while (_pos < _data.Length && _data.Span[_pos] == 0xFF) _pos++;
            // _pos now points at the marker code (the byte AFTER all the 0xFFs).
            if (_pos >= _data.Length) DngThrow.BadFormat("Lossless JPEG: expected marker");
            byte b = _data.Span[_pos++];
            return (ushort)(0xFF00 | b);
        }

        public void ExpectMarker(ushort expected)
        {
            ushort m = ReadMarker();
            if (m != expected)
                throw new DngException(DngError.BadFormat, $"Lossless JPEG: expected 0x{expected:X4}, got 0x{m:X4}");
        }

        public ushort ReadSegmentLength()
        {
            if (_pos + 2 > _data.Length) DngThrow.BadFormat("Lossless JPEG: truncated segment length");
            ushort len = BinaryPrimitives.ReadUInt16BigEndian(_data.Span[_pos..]);
            _pos += 2;
            return len;  // length INCLUDES the 2 bytes we just read
        }

        public void SkipSegment()
        {
            ushort len = ReadSegmentLength();
            _pos += len - 2;
        }

        public SofData ReadSof()
        {
            ushort len = ReadSegmentLength();
            int end = _pos + (len - 2);
            byte precision = _data.Span[_pos++];
            ushort h = BinaryPrimitives.ReadUInt16BigEndian(_data.Span[_pos..]); _pos += 2;
            ushort w = BinaryPrimitives.ReadUInt16BigEndian(_data.Span[_pos..]); _pos += 2;
            byte nf = _data.Span[_pos++];
            var comps = new List<SofComponent>(nf);
            for (int i = 0; i < nf; i++)
            {
                byte id = _data.Span[_pos++];
                byte hv = _data.Span[_pos++];
                byte qt = _data.Span[_pos++]; // ignored for lossless
                comps.Add(new SofComponent(id, hv >> 4, hv & 0x0F, qt));
            }
            if (_pos != end) DngThrow.BadFormat("Lossless JPEG: SOF length mismatch");
            return new SofData(precision, h, w, comps);
        }

        public void ReadDht(HuffmanTable?[] tables)
        {
            ushort len = ReadSegmentLength();
            int end = _pos + (len - 2);
            while (_pos < end)
            {
                byte tc = _data.Span[_pos++];
                int klass = tc >> 4;        // 0 = DC, 1 = AC; lossless has only DC
                int idx = tc & 0x0F;
                if (klass != 0)
                    DngThrow.BadFormat($"Lossless JPEG: AC Huffman table in lossless scan (class {klass})");
                if (idx >= tables.Length)
                    DngThrow.BadFormat($"Lossless JPEG: Huffman table index {idx} out of range");

                if (_pos + 16 > end) DngThrow.BadFormat("Lossless JPEG: truncated DHT bits[]");
                var bits = _data.Slice(_pos, 16);
                _pos += 16;
                int total = 0;
                for (int i = 0; i < 16; i++) total += bits.Span[i];
                if (_pos + total > end) DngThrow.BadFormat("Lossless JPEG: truncated DHT values");
                var vals = _data.Slice(_pos, total);
                _pos += total;
                tables[idx] = new HuffmanTable(bits.Span, vals.Span);
            }
        }

        public SosData ReadSos(SofData sof)
        {
            ushort len = ReadSegmentLength();
            int end = _pos + (len - 2);
            byte ns = _data.Span[_pos++];
            if (ns != sof.Components.Count)
                DngThrow.BadFormat($"Lossless JPEG: SOS Ns={ns} != SOF Nf={sof.Components.Count}");
            var dcSelectors = new int[ns];
            for (int i = 0; i < ns; i++)
            {
                _pos++;  // component selector (ignored — match by order)
                byte tdta = _data.Span[_pos++];
                dcSelectors[i] = tdta >> 4;
            }
            byte ss = _data.Span[_pos++];        // predictor selector (Ss)
            byte se = _data.Span[_pos++];        // must be 0 for lossless
            byte ahal = _data.Span[_pos++];      // Ah=0, Al = point transform
            if (se != 0) DngThrow.BadFormat($"Lossless JPEG: SOS Se={se} (must be 0)");
            int pt = ahal & 0x0F;
            if (pt != 0) DngThrow.NotYetImplemented($"Lossless JPEG with point transform Al={pt}");
            if (_pos != end) DngThrow.BadFormat("Lossless JPEG: SOS length mismatch");
            return new SosData(ss, dcSelectors);
        }
    }

    private sealed record SofData(int Precision, int Height, int Width, List<SofComponent> Components);
    private sealed record SofComponent(int Id, int HSampling, int VSampling, int QuantTable);
    private sealed record SosData(int PredictorSelector, int[] DcTableSelectors);
}
