namespace DngSharp.Dng.Sdk.Codecs.LosslessJpeg;

/// <summary>
/// Bit-by-bit reader over a JPEG entropy-coded segment. Implements
/// 0xFF/0x00 byte-stuffing removal — JPEG escapes any 0xFF byte in the
/// entropy stream by following it with 0x00, which must be silently
/// dropped during decode.
/// </summary>
internal struct JpegBitReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _bytePos;
    private uint _buffer;
    private int _bitCount;

    public JpegBitReader(ReadOnlyMemory<byte> data)
    {
        _data = data;
        _bytePos = 0;
        _buffer = 0;
        _bitCount = 0;
    }

    public bool IsExhausted => _bytePos >= _data.Length && _bitCount == 0;

    public int ReadBits(int n)
    {
        FillBuffer(n);
        int result = (int)(_buffer >> (32 - n));
        _buffer <<= n;
        _bitCount -= n;
        return result;
    }

    public int ReadBit()
    {
        FillBuffer(1);
        int bit = (int)(_buffer >> 31);
        _buffer <<= 1;
        _bitCount -= 1;
        return bit;
    }

    public int PeekBits(int n)
    {
        FillBuffer(n);
        return (int)(_buffer >> (32 - n));
    }

    public void ConsumeBits(int n)
    {
        _buffer <<= n;
        _bitCount -= n;
    }

    private void FillBuffer(int needed)
    {
        while (_bitCount < needed)
        {
            if (_bytePos >= _data.Length)
            {
                // Pad with zeros — matches libjpeg behavior at EOF inside an entropy segment.
                _bitCount += 8;
                continue;
            }
            byte b = _data.Span[_bytePos++];
            if (b == 0xFF)
            {
                // JPEG stuffing: 0xFF 0x00 → emit 0xFF. 0xFF (anything-else) = real marker;
                // unread the 0xFF and pad with zeros so the outer layer can resync.
                if (_bytePos < _data.Length && _data.Span[_bytePos] == 0x00)
                {
                    _bytePos++;
                }
                else
                {
                    _bytePos--;
                    _bitCount += 8;
                    continue;
                }
            }
            _buffer |= (uint)b << (24 - _bitCount);
            _bitCount += 8;
        }
    }
}
