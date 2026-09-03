using Dng.Sdk.Imaging;
using Dng.Sdk.Imaging.Opcodes;
using Dng.Sdk.Metadata;
using Dng.Sdk.Pipeline;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Tests.Pipeline;

/// <summary>
/// <see cref="OpcodeList1Applier"/> and <see cref="OpcodeList2Applier"/> apply
/// a growing subset of opcodes (see their doc comments for the current list);
/// these tests assert the pass-through contract for opcodes that remain
/// unimplemented (null/empty lists return the same image untouched;
/// unsupported entries don't throw and don't corrupt pixel data). Dedicated
/// tests for the implemented opcodes live alongside their opcode classes
/// under <c>tests/Dng.Sdk.Tests/Imaging/Opcodes/</c>.
/// </summary>
public class OpcodeList1And2ApplierTests
{
    private static SimpleImage MakeImage(float fill = 0.25f)
    {
        var bounds = new DngRect(0, 0, 4, 4);
        var image = new SimpleImage(bounds, planes: 1, PixelType.Float32);
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(image.Buffer.AsByteSpan());
        floats.Fill(fill);
        return image;
    }

    [Fact]
    public void List1_null_returns_same_instance()
    {
        var img = MakeImage();
        var result = OpcodeList1Applier.Apply(img, null);
        Assert.Same(img, result);
    }

    [Fact]
    public void List1_empty_returns_same_instance()
    {
        var img = MakeImage();
        var empty = new DngOpcodeList(stage: 1);
        var result = OpcodeList1Applier.Apply(img, empty);
        Assert.Same(img, result);
    }

    [Fact]
    public void List1_unsupported_opcode_is_skipped_without_throwing_or_mutating()
    {
        var img = MakeImage(0.5f);
        var list = new DngOpcodeList(stage: 1);
        list.Entries.Add(new DngOpcode
        {
            Id = OpcodeId.GainMap,
            MinVersion = new DngVersion(1, 3, 0, 0),
            Flags = OpcodeFlags.None,
            BodyBytes = new byte[] { 1, 2, 3, 4 },
        });

        var result = OpcodeList1Applier.Apply(img, list);

        Assert.Same(img, result);
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(result.Buffer.AsByteSpan());
        Assert.All(floats.ToArray(), v => Assert.Equal(0.5f, v));
    }

    [Fact]
    public void List2_null_returns_same_instance()
    {
        var img = MakeImage();
        var result = OpcodeList2Applier.Apply(img, null);
        Assert.Same(img, result);
    }

    [Fact]
    public void List2_empty_returns_same_instance()
    {
        var img = MakeImage();
        var empty = new DngOpcodeList(stage: 2);
        var result = OpcodeList2Applier.Apply(img, empty);
        Assert.Same(img, result);
    }

    [Fact]
    public void List2_unsupported_opcode_is_skipped_without_throwing_or_mutating()
    {
        var img = MakeImage(0.75f);
        var list = new DngOpcodeList(stage: 2);
        list.Entries.Add(new DngOpcode
        {
            Id = OpcodeId.MapTable,
            MinVersion = new DngVersion(1, 3, 0, 0),
            Flags = OpcodeFlags.None,
            BodyBytes = new byte[] { 5, 6, 7, 8 },
        });

        var result = OpcodeList2Applier.Apply(img, list);

        Assert.Same(img, result);
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(result.Buffer.AsByteSpan());
        Assert.All(floats.ToArray(), v => Assert.Equal(0.75f, v));
    }
}
