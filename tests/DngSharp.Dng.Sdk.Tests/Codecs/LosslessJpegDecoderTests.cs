using DngSharp.Dng.Sdk.Codecs.LosslessJpeg;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Tests.Codecs;

public class LosslessJpegDecoderTests
{
    /// <summary>
    /// Build a minimal predictor-mode-1, 1-component, 8-bit-precision lossless
    /// JPEG bitstream that encodes a small image. Uses a fixed Huffman table
    /// with the single SSSS=0 code = "0" (1 bit), meaning every sample equals
    /// its predictor: all-zero diffs. The reconstructed image is constant at
    /// the row/col-degenerate seed value (2^(P-1) = 128 for the first pixel,
    /// then propagated by Ra/Rb predictors).
    /// </summary>
    [Fact]
    public void Decodes_all_zero_diff_stream_to_constant_image()
    {
        // 2x2 single-component image, predictor 1, precision 8.
        // Huffman: bits[1]=1 (one code of length 1), huffval=[0]. Code "0" = SSSS=0.
        var bytes = new List<byte>();
        // SOI
        bytes.AddRange([0xFF, 0xD8]);
        // SOF3: marker (2) + length (2=8) + precision (1) + height (2) + width (2) + Nf (1) + 3*Nf
        bytes.AddRange([0xFF, 0xC3]);
        ushort sofLen = (ushort)(8 + 3 * 1);
        bytes.AddRange([(byte)(sofLen >> 8), (byte)(sofLen & 0xFF)]);
        bytes.Add(8);                        // precision
        bytes.AddRange([0, 2]);              // height = 2
        bytes.AddRange([0, 2]);              // width  = 2
        bytes.Add(1);                        // Nf
        bytes.AddRange([1, 0x11, 0]);        // component id=1, H=1 V=1, Qt=0 (ignored)
        // DHT: marker (2) + length (2) + tc (1) + bits[16] + huffval[total]
        bytes.AddRange([0xFF, 0xC4]);
        // We add 1 code of length 1, value 0.
        ushort dhtLen = (ushort)(2 + 1 + 16 + 1);
        bytes.AddRange([(byte)(dhtLen >> 8), (byte)(dhtLen & 0xFF)]);
        bytes.Add(0x00);                     // tc=0 (DC), th=0
        bytes.AddRange([1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);  // bits[1]=1
        bytes.Add(0);                        // huffval[0]=0
        // SOS: marker (2) + length (2=8) + Ns + 2*Ns + Ss + Se + Ah:Al
        bytes.AddRange([0xFF, 0xDA]);
        ushort sosLen = (ushort)(6 + 2 * 1);
        bytes.AddRange([(byte)(sosLen >> 8), (byte)(sosLen & 0xFF)]);
        bytes.Add(1);                        // Ns
        bytes.AddRange([1, 0x00]);           // component selector=1, td=0 ta=0
        bytes.Add(1);                        // Ss = predictor 1
        bytes.Add(0);                        // Se must be 0 for lossless
        bytes.Add(0);                        // Ah=0, Al=0
        // Entropy: 4 samples × 1 bit each ("0" → SSSS=0 → diff=0). 4 bits total,
        // padded to a byte = 0x00. Don't emit stuffing for 0x00.
        bytes.Add(0x00);
        // EOI
        bytes.AddRange([0xFF, 0xD9]);

        // Decode into a 2x2 single-plane UInt8 buffer.
        var dst = new byte[4];
        var buf = PixelBuffer.Interleaved(new DngRect(0, 0, 2, 2), 1, PixelType.UInt8, dst);
        new LosslessJpegDecoder().Decode(bytes.ToArray(), buf, bigEndian: false);

        // Pixel(0,0) = seed = 2^(8-1) = 128.
        // Pixel(0,1) = predictor 1 (left) = 128.
        // Pixel(1,0) = first column of row 1 → Rb (above) = 128.
        // Pixel(1,1) = predictor 1 (left) = 128.
        Assert.Equal(128, dst[0]);
        Assert.Equal(128, dst[1]);
        Assert.Equal(128, dst[2]);
        Assert.Equal(128, dst[3]);
    }

    [Fact]
    public void Decodes_nonzero_diff_stream_into_ramp()
    {
        // Same SOF/DHT framing, but Huffman table has TWO codes:
        // bits[2] = 2 (two codes of length 2), huffval=[0, 1]
        // Code "00" -> SSSS=0 (diff=0), Code "01" -> SSSS=1 (1 extended bit -> -1 or 0)
        // We emit: pixel(0,0) SSSS=1 with extra-bit "1" => diff = +1; predictor=128 -> 129.
        //         pixel(0,1) SSSS=0 -> diff=0; predictor=Ra=129 -> 129.
        //         pixel(1,0) SSSS=1 with extra-bit "1" => diff = +1; predictor=Rb=129 -> 130.
        //         pixel(1,1) SSSS=0 -> diff=0; predictor=Ra=130 -> 130.

        var bytes = new List<byte>();
        bytes.AddRange([0xFF, 0xD8]);
        bytes.AddRange([0xFF, 0xC3]);
        bytes.AddRange([0, 11]);
        bytes.Add(8); bytes.AddRange([0, 2]); bytes.AddRange([0, 2]); bytes.Add(1);
        bytes.AddRange([1, 0x11, 0]);
        bytes.AddRange([0xFF, 0xC4]);
        bytes.AddRange([0, 21]);   // length = 2 (length itself) + 1 (tc) + 16 (bits) + 2 (huffval)
        bytes.Add(0x00);
        bytes.AddRange([0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]); // bits[2]=2
        bytes.AddRange([0, 1]); // huffval
        bytes.AddRange([0xFF, 0xDA]);
        bytes.AddRange([0, 8]);
        bytes.Add(1); bytes.AddRange([1, 0x00]);
        bytes.Add(1); bytes.Add(0); bytes.Add(0);
        // Entropy bits, MSB-first:
        // pixel(0,0): "01" (SSSS=1) + "1" (extra) = 011
        // pixel(0,1): "00" (SSSS=0)               = 00
        // pixel(1,0): "01" + "1"                  = 011
        // pixel(1,1): "00"                        = 00
        // Total bits: 011 00 011 00 = 0110 0011 00 (10 bits) → 0x63, 0x00 (last 2 bits set, then pad zeros)
        bytes.Add(0x63);
        bytes.Add(0x00);
        bytes.AddRange([0xFF, 0xD9]);

        var dst = new byte[4];
        var buf = PixelBuffer.Interleaved(new DngRect(0, 0, 2, 2), 1, PixelType.UInt8, dst);
        new LosslessJpegDecoder().Decode(bytes.ToArray(), buf, bigEndian: false);

        Assert.Equal(129, dst[0]);  // 128 + 1
        Assert.Equal(129, dst[1]);  // Ra = 129
        Assert.Equal(130, dst[2]);  // Rb = 129, diff = +1 → 130
        Assert.Equal(130, dst[3]);  // Ra = 130
    }

    [Fact]
    public void Missing_soi_throws_bad_format()
    {
        var buf = PixelBuffer.Interleaved(new DngRect(0, 0, 2, 2), 1, PixelType.UInt8, new byte[4]);
        Assert.Throws<DngException>(() =>
            new LosslessJpegDecoder().Decode(new byte[] { 0x00, 0x00 }, buf, bigEndian: false));
    }

    [Fact]
    public void Wrong_destination_size_throws_bad_format()
    {
        // Build the minimal stream from the first test, but decode into a
        // 4x4 buffer instead of 2x2 — should reject before producing bad data.
        var bytes = MinimalConstantStream();
        var buf = PixelBuffer.Interleaved(new DngRect(0, 0, 4, 4), 1, PixelType.UInt8, new byte[16]);
        Assert.Throws<DngException>(() =>
            new LosslessJpegDecoder().Decode(bytes, buf, bigEndian: false));
    }

    private static byte[] MinimalConstantStream()
    {
        var bytes = new List<byte>();
        bytes.AddRange([0xFF, 0xD8, 0xFF, 0xC3, 0, 11, 8, 0, 2, 0, 2, 1, 1, 0x11, 0]);
        bytes.AddRange([0xFF, 0xC4, 0, 20, 0x00, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        bytes.AddRange([0xFF, 0xDA, 0, 8, 1, 1, 0x00, 1, 0, 0, 0x00, 0xFF, 0xD9]);
        return bytes.ToArray();
    }
}
