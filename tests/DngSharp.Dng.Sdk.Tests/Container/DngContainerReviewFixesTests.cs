using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Tests.Container;

/// <summary>
/// Regression tests for issues surfaced by the Phase 2 code review.
/// </summary>
public class DngContainerReviewFixesTests
{
    /// <summary>
    /// Big-endian (MM) DNGs must classify NewSubFileType correctly.
    /// Pre-fix, Classify() always decoded inline bytes little-endian.
    /// </summary>
    [Fact]
    public void Big_endian_dng_classifies_enhanced_image_correctly()
    {
        // Synth: MM TIFF, one IFD with NewSubFileType=16 (EnhancedImage).
        // Inline value for a Long-typed scalar: 4 bytes big-endian = 00 00 00 10.
        byte[] bytes =
        [
            (byte)'M', (byte)'M',
            0, 42,
            0, 0, 0, 8,                          // first IFD at offset 8
            0, 1,                                 // 1 entry
            0, 254,                               // tag = NewSubFileType
            0, 4,                                 // type = Long
            0, 0, 0, 1,                           // count = 1
            0, 0, 0, 16,                          // inline LONG = 16  (BE)
            0, 0, 0, 0,                           // next IFD = 0
        ];
        using var s = DngMemoryStream.WrapNoCopy(bytes);
        var c = DngContainer.Parse(s);
        Assert.True(c.Header.BigEndian);
        Assert.Equal(NewSubFileType.EnhancedImage, c.TopLevelIfds[0].Classify(c.Header.BigEndian));
        Assert.Equal(0, c.EnhancedIndex);
    }

    [Fact]
    public void SubIfd_count_DoS_is_rejected_before_allocating()
    {
        // Synth: II TIFF whose only IFD has SubIFDs tag claiming 1 billion entries.
        // Pre-fix: tried to `new List<long>(1_000_000_000)` ≈ 8 GB before the cap check.
        byte[] bytes =
        [
            (byte)'I', (byte)'I',
            42, 0,
            8, 0, 0, 0,
            1, 0,                                  // 1 entry
            // SubIFDs tag (0x014A) = 330
            0x4A, 0x01,
            // type = Long (4)
            0x04, 0x00,
            // count = 1_000_000_000  (LE)
            0x00, 0xCA, 0x9A, 0x3B,
            // value/offset (ignored — claim is rejected first)
            0x00, 0x00, 0x00, 0x00,
            0, 0, 0, 0,                            // next IFD = 0
        ];
        using var s = DngMemoryStream.WrapNoCopy(bytes);
        var ex = Assert.Throws<DngException>(() => DngContainer.Parse(s));
        Assert.Equal(DngError.BadFormat, ex.ErrorCode);
        Assert.Contains("SubIFDs", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
