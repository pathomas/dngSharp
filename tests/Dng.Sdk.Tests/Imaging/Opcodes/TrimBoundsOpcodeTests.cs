using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Dng.Sdk.Errors;
using Dng.Sdk.Imaging;
using Dng.Sdk.Imaging.Opcodes;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Tests.Imaging.Opcodes;

public class TrimBoundsOpcodeTests
{
    /// <summary>
    /// Builds a raw <c>TrimBounds</c> opcode body: big-endian t/l/b/r (no
    /// leading dataSize field — see class-under-test doc comment).
    /// </summary>
    private static byte[] BuildBody(DngRect bounds)
    {
        using var ms = new MemoryStream();

        void WriteI32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }

        WriteI32(bounds.T); WriteI32(bounds.L); WriteI32(bounds.B); WriteI32(bounds.R);

        return ms.ToArray();
    }

    private static SimpleImage MakeImage(int width, int height, uint planes, PixelType pixelType = PixelType.Float32, float fillFrom = 0f)
    {
        var bounds = new DngRect(0, 0, height, width);
        var image = new SimpleImage(bounds, planes, pixelType);
        if (pixelType == PixelType.Float32)
        {
            var floats = MemoryMarshal.Cast<byte, float>(image.Buffer.AsByteSpan());
            for (int i = 0; i < floats.Length; i++) floats[i] = fillFrom + i;
        }
        return image;
    }

    [Fact]
    public void Decode_reads_bounds()
    {
        var bounds = new DngRect(1, 2, 5, 6);
        var body = BuildBody(bounds);

        var decoded = TrimBoundsOpcode.Decode(body);

        Assert.Equal(bounds, decoded);
    }

    [Fact]
    public void Decode_throws_on_body_too_short()
    {
        var body = new byte[12]; // needs 16 bytes for t/l/b/r
        Assert.Throws<DngException>(() => TrimBoundsOpcode.Decode(body));
    }

    [Fact]
    public void Decode_throws_on_empty_bounds()
    {
        var body = BuildBody(DngRect.Empty);
        Assert.Throws<DngException>(() => TrimBoundsOpcode.Decode(body));
    }

    [Fact]
    public void Apply_crops_to_bounds_and_shifts_to_origin()
    {
        var image = MakeImage(width: 4, height: 4, planes: 1);
        var bounds = new DngRect(1, 1, 3, 3); // 2x2 sub-rect

        var result = TrimBoundsOpcode.Apply(image, bounds);

        Assert.Equal(new DngRect(0, 0, 2, 2), result.Bounds);

        var floats = MemoryMarshal.Cast<byte, float>(result.Buffer.AsByteSpan());
        // Original pixel at (1,1) was index 1*4+1=5, should now be at (0,0).
        long idx = result.Buffer.OffsetBytes(0, 0, 0) / sizeof(float);
        Assert.Equal(5f, floats[(int)idx]);
    }

    [Fact]
    public void Apply_throws_when_bounds_not_contained_in_image()
    {
        var image = MakeImage(width: 2, height: 2, planes: 1);
        var bounds = new DngRect(0, 0, 5, 5); // exceeds image bounds

        Assert.Throws<DngException>(() => TrimBoundsOpcode.Apply(image, bounds));
    }

    [Fact]
    public void Apply_throws_on_empty_bounds()
    {
        var image = MakeImage(width: 2, height: 2, planes: 1);
        Assert.Throws<DngException>(() => TrimBoundsOpcode.Apply(image, DngRect.Empty));
    }
}
