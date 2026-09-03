using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Dng.Sdk.Jxl.Native;

/// <summary>
/// LibraryImport-based P/Invoke declarations for <c>libjxl 0.11.2</c>.
///
/// <para>Covers the minimal decode surface needed by <see cref="JxlDecoder"/>:
/// signature probe, basic-info query, single-frame pixel decode to a managed
/// <c>byte[]</c> / <c>Span&lt;byte&gt;</c>.</para>
///
/// <para><b>Native dependency:</b> requires <c>jxl.dll</c> (Windows) /
/// <c>libjxl.so</c> (Linux) / <c>libjxl.dylib</c> (macOS) on the loader
/// path. CI builds and places this under
/// <c>src/Dng.Sdk.Jxl/runtimes/&lt;rid&gt;/native/</c> where the .csproj
/// glob copies it to the output directory. For local development run
/// <c>tools/build-libjxl.ps1</c>.</para>
/// </summary>
internal static partial class LibJxl
{
    // "jxl" resolves to: jxl.dll (Windows), libjxl.so (Linux), libjxl.dylib (macOS).
    private const string Library = "jxl";

    // ── Enums ──────────────────────────────────────────────────────────────

    /// <summary>JxlSignature — result of <see cref="JxlSignatureCheck"/>.</summary>
    public enum JxlSignature : int
    {
        NotEnoughBytes = 0,
        Invalid        = 1,
        Codestream     = 2,
        Container      = 3,
    }

    /// <summary>JxlDecoderStatus — return value of <see cref="JxlDecoderProcessInput"/>.</summary>
    public enum JxlDecoderStatus : int
    {
        Success              = 0,
        Error                = 1,
        NeedMoreInput        = 2,
        NeedPreviewOutBuffer = 3,
        NeedImageOutBuffer   = 5,
        BasicInfo            = 0x40,
        ColorEncoding        = 0x100,
        PreviewImage         = 0x200,
        Frame                = 0x400,
        FullImage            = 0x1000,
    }

    /// <summary>JxlDataType — sample format for pixel output buffers.</summary>
    public enum JxlDataType : int
    {
        Float   = 0,
        UInt8   = 2,
        UInt16  = 3,
        Float16 = 5,
    }

    /// <summary>JxlEndianness — multi-byte sample byte order.</summary>
    public enum JxlEndianness : int
    {
        Native = 0,
        Little = 1,
        Big    = 2,
    }

    // ── Structs ─────────────────────────────────────────────────────────────

    /// <summary>
    /// JxlPixelFormat — describes the layout of the pixel output buffer
    /// passed to <see cref="JxlDecoderSetImageOutBuffer"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct JxlPixelFormat
    {
        /// <summary>Number of interleaved channels (1 = gray, 3 = RGB, 4 = RGBA).</summary>
        public uint NumChannels;
        /// <summary>Sample data type per channel.</summary>
        public JxlDataType DataType;
        /// <summary>Byte order of multi-byte samples.</summary>
        public JxlEndianness Endianness;
        /// <summary>Row alignment in bytes (0 or 1 = no alignment).</summary>
        public nuint Align;
    }

    /// <summary>
    /// JxlBasicInfo — image geometry and bit-depth, filled by
    /// <see cref="JxlDecoderGetBasicInfo"/> when
    /// <see cref="JxlDecoderStatus.BasicInfo"/> is returned.
    ///
    /// Only the fields we actually read are named; the rest are marshalled as
    /// padding to keep the struct layout correct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct JxlBasicInfo
    {
        /// <summary>True iff the codestream is wrapped in a BMFF container.</summary>
        public int HaveContainer;           // JXL_BOOL (int)
        /// <summary>Image width in pixels (before orientation).</summary>
        public uint XSize;
        /// <summary>Image height in pixels (before orientation).</summary>
        public uint YSize;
        /// <summary>Bits per color channel in the encoded codestream.</summary>
        public uint BitsPerSample;
        /// <summary>Exponent bits (0 for integer, 5 for float16, 8 for float32).</summary>
        public uint ExponentBitsPerSample;
        /// <summary>Peak luminance in nits (0 = let libjxl choose).</summary>
        public float IntensityTarget;
        /// <summary>Minimum luminance in nits.</summary>
        public float MinNits;
        public int RelativeToMaxDisplay;    // JXL_BOOL
        public float LinearBelow;
        public int UsesOriginalProfile;     // JXL_BOOL
        public int HavePreview;             // JXL_BOOL
        public int HaveAnimation;           // JXL_BOOL
        public int Orientation;             // JxlOrientation enum (int)
        /// <summary>Number of color channels (1 = gray, 3 = color).</summary>
        public uint NumColorChannels;
        /// <summary>Number of extra channels (alpha, depth, …).</summary>
        public uint NumExtraChannels;
        /// <summary>Alpha channel bits (0 = no alpha).</summary>
        public uint AlphaBits;
        public uint AlphaExponentBits;
        public int AlphaPremultiplied;      // JXL_BOOL
        // JxlPreviewHeader (2 × uint32)
        public uint PreviewXSize;
        public uint PreviewYSize;
        // JxlAnimationHeader (4 fields)
        public uint TpsNumerator;
        public uint TpsDenominator;
        public uint NumLoops;
        public int HaveTimecodes;           // JXL_BOOL
    }

    // ── Functions ────────────────────────────────────────────────────────────

    /// <summary><c>JxlSignature JxlSignatureCheck(const uint8_t* buf, size_t len)</c></summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial JxlSignature JxlSignatureCheck(ref byte buf, nuint len);

    /// <summary><c>JxlDecoder* JxlDecoderCreate(NULL)</c> — use default allocator.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial nint JxlDecoderCreate(nint memoryManager); // pass 0

    /// <summary><c>void JxlDecoderDestroy(JxlDecoder*)</c></summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial void JxlDecoderDestroy(nint dec);

    /// <summary><c>JxlDecoderStatus JxlDecoderSubscribeEvents(JxlDecoder*, int events_wanted)</c></summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial JxlDecoderStatus JxlDecoderSubscribeEvents(nint dec, int eventsWanted);

    /// <summary><c>JxlDecoderStatus JxlDecoderSetInput(JxlDecoder*, const uint8_t* data, size_t size)</c></summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial JxlDecoderStatus JxlDecoderSetInput(nint dec, ref byte data, nuint size);

    /// <summary><c>void JxlDecoderCloseInput(JxlDecoder*)</c></summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial void JxlDecoderCloseInput(nint dec);

    /// <summary><c>JxlDecoderStatus JxlDecoderProcessInput(JxlDecoder*)</c></summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial JxlDecoderStatus JxlDecoderProcessInput(nint dec);

    /// <summary><c>JxlDecoderStatus JxlDecoderGetBasicInfo(JxlDecoder*, JxlBasicInfo*)</c></summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial JxlDecoderStatus JxlDecoderGetBasicInfo(nint dec, out JxlBasicInfo info);

    /// <summary><c>JxlDecoderStatus JxlDecoderImageOutBufferSize(JxlDecoder*, JxlPixelFormat*, size_t* size)</c></summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial JxlDecoderStatus JxlDecoderImageOutBufferSize(
        nint dec, ref JxlPixelFormat format, out nuint size);

    /// <summary><c>JxlDecoderStatus JxlDecoderSetImageOutBuffer(JxlDecoder*, JxlPixelFormat*, void* buffer, size_t size)</c></summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial JxlDecoderStatus JxlDecoderSetImageOutBuffer(
        nint dec, ref JxlPixelFormat format, ref byte buffer, nuint size);

    /// <summary><c>size_t JxlDecoderReleaseInput(JxlDecoder*)</c></summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial nuint JxlDecoderReleaseInput(nint dec);
}
