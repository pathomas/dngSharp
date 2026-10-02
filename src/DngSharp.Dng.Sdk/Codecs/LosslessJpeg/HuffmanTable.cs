using DngSharp.Dng.Sdk.Errors;

namespace DngSharp.Dng.Sdk.Codecs.LosslessJpeg;

/// <summary>
/// Canonical Huffman decode table built from a JPEG DHT segment's
/// (bits[], huffval[]) pair. Mirrors ITU T.81 Annex C.
/// </summary>
internal sealed class HuffmanTable
{
    /// <summary>
    /// For each code length L in 1..16: the smallest L-bit code value present
    /// at that length (or 0 when no codes of length L exist).
    /// </summary>
    private readonly int[] _minCode = new int[17];
    /// <summary>The largest L-bit code value present at length L (or -1).</summary>
    private readonly int[] _maxCode = new int[17];
    /// <summary>Offset into <see cref="_huffVal"/> for codes of length L.</summary>
    private readonly int[] _valPtr = new int[17];
    private readonly byte[] _huffVal;

    public HuffmanTable(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> huffVal)
    {
        // bits[0..15] = number of codes of length (1..16). Total codes <= 256.
        if (bits.Length < 16) DngThrow.BadFormat("DHT bits[] must have 16 entries");
        _huffVal = huffVal.ToArray();

        // Build huffcode[] / huffsize[] per T.81 Annex C.
        Span<int> huffSize = stackalloc int[257];
        Span<int> huffCode = stackalloc int[257];
        int p = 0;
        for (int l = 1; l <= 16; l++)
        {
            int cnt = bits[l - 1];
            for (int i = 0; i < cnt; i++)
            {
                if (p >= 256) DngThrow.BadFormat("DHT: too many codes");
                huffSize[p++] = l;
            }
        }
        huffSize[p] = 0;
        if (huffVal.Length < p) DngThrow.BadFormat($"DHT: huffval too small ({huffVal.Length} < {p})");

        int code = 0, si = huffSize[0], k = 0;
        while (huffSize[k] != 0)
        {
            while (huffSize[k] == si)
            {
                huffCode[k] = code;
                code++;
                k++;
            }
            if (huffSize[k] == 0) break;
            do { code <<= 1; si++; } while (huffSize[k] != si);
        }

        // Build min/max/valptr per Annex F.
        int j = 0;
        for (int l = 1; l <= 16; l++)
        {
            int cnt = bits[l - 1];
            if (cnt == 0)
            {
                _maxCode[l] = -1;
                continue;
            }
            _valPtr[l] = j;
            _minCode[l] = huffCode[j];
            j += cnt;
            _maxCode[l] = huffCode[j - 1];
        }
    }

    /// <summary>
    /// Decode one symbol from <paramref name="reader"/>. Returns the symbol
    /// value (0..255). Throws <see cref="DngError.BadFormat"/> on an invalid
    /// code (longer than 16 bits or unmapped).
    /// </summary>
    public int Decode(ref JpegBitReader reader)
    {
        // Slow-path bit-by-bit decode — simple, correct, sufficient for the
        // test stream. A fast 8-bit lookup table is a Phase 10 optimization.
        int code = reader.ReadBit();
        int l = 1;
        while (l <= 16 && code > _maxCode[l])
        {
            code = (code << 1) | reader.ReadBit();
            l++;
        }
        if (l > 16) throw new DngException(DngError.BadFormat, "Huffman: code longer than 16 bits");
        int j = _valPtr[l] + code - _minCode[l];
        if (j < 0 || j >= _huffVal.Length)
            throw new DngException(DngError.BadFormat, $"Huffman: index {j} out of range");
        return _huffVal[j];
    }
}
