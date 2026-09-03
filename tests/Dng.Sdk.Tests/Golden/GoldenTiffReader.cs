using System.Buffers.Binary;

namespace Dng.Sdk.Tests.Golden;

/// <summary>
/// Minimal single-strip TIFF pixel reader shared by the golden-diff tests.
/// Supports the two layouts native <c>dng_validate -1/-2/-3</c> dumps
/// produce: normalized <c>UInt16</c> (default TIFF sample format) and IEEE
/// <c>Float32</c>.
/// </summary>
internal static class GoldenTiffReader
{
    public static float[] ReadFloat32(string path, out uint width, out uint height, out uint planes)
    {
        var bytes = File.ReadAllBytes(path);
        bool be = bytes[0] == 'M';
        width = 0; height = 0; planes = 1;
        ushort bitsPerSample = 8;
        ushort sampleFormat = 1; // 1 = unsigned integer (TIFF default)

        int ifdOffset = (int)(be
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4))
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));

        int numEntries = be
            ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(ifdOffset))
            : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(ifdOffset));

        long offset = 0, count = 0;
        for (int i = 0; i < numEntries; i++)
        {
            int pos = ifdOffset + 2 + i * 12;
            ushort tag = be
                ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(pos))
                : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos));
            switch (tag)
            {
                case 0x0100: width = Read4(bytes, pos + 8, be); break;
                case 0x0101: height = Read4(bytes, pos + 8, be); break;
                case 0x0115: planes = Read4(bytes, pos + 8, be); break;
                case 0x0111: offset = Read4(bytes, pos + 8, be); break;
                case 0x0117: count = Read4(bytes, pos + 8, be); break;
                case 0x0102: bitsPerSample = ReadFirstUInt16(bytes, pos, be); break;
                case 0x0153: sampleFormat = ReadFirstUInt16(bytes, pos, be); break;
            }
        }

        if (offset == 0 || count == 0) return [];

        // SampleFormat 3 (IEEE float) always implies 32-bit in this codebase's writer.
        bool isFloat32 = sampleFormat == 3 && bitsPerSample == 32;

        if (isFloat32)
        {
            int n = (int)(count / 4);
            var result = new float[n];
            for (int i = 0; i < n; i++)
                result[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan((int)offset + i * 4));
            return result;
        }

        if (bitsPerSample == 16 && sampleFormat == 1)
        {
            int n = (int)(count / 2);
            var result = new float[n];
            for (int i = 0; i < n; i++)
            {
                ushort v = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)offset + i * 2));
                result[i] = v / 65535.0f;
            }
            return result;
        }

        // Unsupported sample layout for this diagnostic reader.
        return [];
    }

    /// <summary>
    /// Reads raw (unnormalized) sample values as <c>double</c>, for
    /// comparing against Stage-1 (pre-linearization) managed output, which
    /// is stored in whatever native sample representation the file used
    /// (e.g. 16-bit unsigned sensor codes, not normalized to [0,1]).
    /// Supports UInt8/UInt16/UInt32/Float32 single-strip layouts.
    /// </summary>
    public static double[] ReadRawDouble(string path, out uint width, out uint height, out uint planes)
    {
        var bytes = File.ReadAllBytes(path);
        bool be = bytes[0] == 'M';
        width = 0; height = 0; planes = 1;
        ushort bitsPerSample = 8;
        ushort sampleFormat = 1; // 1 = unsigned integer (TIFF default)

        int ifdOffset = (int)(be
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4))
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));

        int numEntries = be
            ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(ifdOffset))
            : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(ifdOffset));

        long offset = 0, count = 0;
        for (int i = 0; i < numEntries; i++)
        {
            int pos = ifdOffset + 2 + i * 12;
            ushort tag = be
                ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(pos))
                : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos));
            switch (tag)
            {
                case 0x0100: width = Read4(bytes, pos + 8, be); break;
                case 0x0101: height = Read4(bytes, pos + 8, be); break;
                case 0x0115: planes = Read4(bytes, pos + 8, be); break;
                case 0x0111: offset = Read4(bytes, pos + 8, be); break;
                case 0x0117: count = Read4(bytes, pos + 8, be); break;
                case 0x0102: bitsPerSample = ReadFirstUInt16(bytes, pos, be); break;
                case 0x0153: sampleFormat = ReadFirstUInt16(bytes, pos, be); break;
            }
        }

        if (offset == 0 || count == 0) return [];

        if (sampleFormat == 3 && bitsPerSample == 32)
        {
            int n = (int)(count / 4);
            var result = new double[n];
            for (int i = 0; i < n; i++)
                result[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan((int)offset + i * 4));
            return result;
        }

        if (bitsPerSample == 16 && sampleFormat == 1)
        {
            int n = (int)(count / 2);
            var result = new double[n];
            for (int i = 0; i < n; i++)
                result[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)offset + i * 2));
            return result;
        }

        if (bitsPerSample == 8 && sampleFormat == 1)
        {
            int n = (int)count;
            var result = new double[n];
            for (int i = 0; i < n; i++)
                result[i] = bytes[(int)offset + i];
            return result;
        }

        // Unsupported sample layout for this diagnostic reader.
        return [];
    }

    private static ushort ReadFirstUInt16(byte[] bytes, int entryPos, bool be)
    {
        // A SHORT-typed entry stores its value(s) inline in the 4-byte value
        // slot only when count <= 2 (2 bytes each); for count >= 3 the slot
        // holds an offset to an out-of-line array instead (e.g. a 3-plane
        // BitsPerSample = [16, 16, 16]). Detect and dereference accordingly.
        uint entryCount = be
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entryPos + 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryPos + 4));

        int valuePos = entryPos + 8;
        if (entryCount > 2)
        {
            valuePos = (int)(be
                ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entryPos + 8))
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryPos + 8)));
        }

        return be
            ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(valuePos))
            : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(valuePos));
    }

    private static uint Read4(byte[] bytes, int pos, bool be) =>
        be ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos))
           : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos));
}
