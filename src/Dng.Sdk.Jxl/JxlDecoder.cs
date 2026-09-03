using System.Runtime.InteropServices;
using Dng.Sdk.Codecs;
using Dng.Sdk.Errors;
using Dng.Sdk.Jxl.Native;
using Dng.Sdk.Pixels;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Jxl;

/// <summary>
/// <see cref="IRawDecoder"/> backed by P/Invoke against <c>libjxl</c>.
///
/// <para>The native library must be on the OS loader path. Use
/// <see cref="IsAvailable"/> to probe at runtime before registration; if
/// libjxl isn't present, hosts that don't process JXL files (e.g. legacy
/// DNG-only workflows) can skip the registration entirely.</para>
///
/// <para><b>Thread safety:</b> a single <see cref="JxlDecoder"/> instance
/// creates a new native decoder handle per call to <see cref="Decode"/> and
/// destroys it before returning, so the same instance is safe to share across
/// threads (each call is independent).</para>
/// </summary>
public sealed class JxlDecoder : IRawDecoder
{
    public Compression Compression => Compression.Jxl;

    /// <summary>
    /// True iff <c>libjxl</c> can be loaded and answers a signature probe.
    /// Calls into native code; the result is cached after first call.
    /// </summary>
    public static bool IsAvailable => _availableCache.Value;

    private static readonly Lazy<bool> _availableCache = new(ProbeAvailable);

    private static bool ProbeAvailable()
    {
        try
        {
            byte stub = 0;
            var sig = LibJxl.JxlSignatureCheck(ref stub, 1);
            return sig is LibJxl.JxlSignature.NotEnoughBytes
                       or LibJxl.JxlSignature.Invalid
                       or LibJxl.JxlSignature.Codestream
                       or LibJxl.JxlSignature.Container;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>
    /// Decode a complete JXL strip or tile payload from <paramref name="compressed"/>
    /// into <paramref name="destination"/>. Drives the full libjxl state machine
    /// (BasicInfo → NeedImageOutBuffer → FullImage → Success).
    ///
    /// <para>Supported destination pixel types: <see cref="PixelType.UInt8"/>,
    /// <see cref="PixelType.UInt16"/>, <see cref="PixelType.Float32"/>,
    /// <see cref="PixelType.Float16"/>.</para>
    ///
    /// <para>The output pixel data is always requested in little-endian byte order
    /// so it matches the rest of the managed SDK's conventions.</para>
    /// </summary>
    public void Decode(ReadOnlySpan<byte> compressed, PixelBuffer destination, bool bigEndian)
    {
        if (!IsAvailable)
            throw new DngException(DngError.JxlDecoder,
                "libjxl not available on this system. Build dng_sdk_1_7_1/libjxl and place "
                + "libjxl.dll (Windows) / libjxl.so (Linux) / libjxl.dylib (macOS) on the "
                + "loader path, or run  tools/build-libjxl.ps1  to build it locally.");

        nint dec = LibJxl.JxlDecoderCreate(0);
        if (dec == 0)
            throw new DngException(DngError.Memory, "JxlDecoderCreate returned null");

        try
        {
            DecodeCore(dec, compressed, destination, bigEndian);
        }
        finally
        {
            LibJxl.JxlDecoderDestroy(dec);
        }
    }

    private static void DecodeCore(
        nint dec,
        ReadOnlySpan<byte> compressed,
        PixelBuffer destination,
        bool bigEndian)
    {
        // Subscribe to just the two events we need: basic info (to get image
        // geometry) and full image (to fill the output buffer).
        const int subscribe = (int)(LibJxl.JxlDecoderStatus.BasicInfo | LibJxl.JxlDecoderStatus.FullImage);
        var st = LibJxl.JxlDecoderSubscribeEvents(dec, subscribe);
        if (st != LibJxl.JxlDecoderStatus.Success)
            throw new DngException(DngError.JxlDecoder, $"JxlDecoderSubscribeEvents failed: {st}");

        // Feed the entire payload at once and signal end-of-input.
        st = LibJxl.JxlDecoderSetInput(dec, ref MemoryMarshal.GetReference(compressed), (nuint)compressed.Length);
        if (st != LibJxl.JxlDecoderStatus.Success)
            throw new DngException(DngError.JxlDecoder, $"JxlDecoderSetInput failed: {st}");
        LibJxl.JxlDecoderCloseInput(dec);

        // Determine JxlDataType + bytes-per-sample from destination PixelType.
        var (jxlType, bytesPerSample) = destination.PixelType switch
        {
            PixelType.UInt8   => (LibJxl.JxlDataType.UInt8,   1u),
            PixelType.UInt16  => (LibJxl.JxlDataType.UInt16,  2u),
            PixelType.Float16 => (LibJxl.JxlDataType.Float16, 2u),
            PixelType.Float32 => (LibJxl.JxlDataType.Float,   4u),
            _ => throw new DngException(DngError.JxlDecoder,
                     $"JxlDecoder: unsupported destination pixel type {destination.PixelType}"),
        };

        var fmt = new LibJxl.JxlPixelFormat
        {
            NumChannels = destination.Planes,
            DataType    = jxlType,
            Endianness  = bigEndian ? LibJxl.JxlEndianness.Big : LibJxl.JxlEndianness.Little,
            Align       = 0,
        };

        // Drive the state machine.
        bool basicInfoSeen = false;
        while (true)
        {
            st = LibJxl.JxlDecoderProcessInput(dec);

            switch (st)
            {
                case LibJxl.JxlDecoderStatus.BasicInfo:
                    st = LibJxl.JxlDecoderGetBasicInfo(dec, out var info);
                    if (st != LibJxl.JxlDecoderStatus.Success)
                        throw new DngException(DngError.JxlDecoder, $"JxlDecoderGetBasicInfo failed: {st}");

                    // Validate image geometry against the destination buffer.
                    if (info.XSize != (uint)destination.Area.W || info.YSize != (uint)destination.Area.H)
                        throw new DngException(DngError.JxlDecoder,
                            $"JXL geometry mismatch: codestream is {info.XSize}×{info.YSize}, "
                            + $"destination is {destination.Area.W}×{destination.Area.H}");

                    basicInfoSeen = true;
                    break;

                case LibJxl.JxlDecoderStatus.NeedImageOutBuffer:
                    if (!basicInfoSeen)
                        throw new DngException(DngError.JxlDecoder, "NeedImageOutBuffer before BasicInfo");

                    // Query required buffer size and validate it fits the destination.
                    st = LibJxl.JxlDecoderImageOutBufferSize(dec, ref fmt, out var requiredBytes);
                    if (st != LibJxl.JxlDecoderStatus.Success)
                        throw new DngException(DngError.JxlDecoder, $"JxlDecoderImageOutBufferSize failed: {st}");

                    ulong expectedBytes = (ulong)destination.Area.W * destination.Area.H
                                        * destination.Planes * bytesPerSample;
                    if (requiredBytes != (nuint)expectedBytes)
                        throw new DngException(DngError.JxlDecoder,
                            $"JXL buffer size mismatch: libjxl wants {requiredBytes} bytes, "
                            + $"destination has {expectedBytes}");

                    // Wire the destination memory directly — no extra copy.
                    st = LibJxl.JxlDecoderSetImageOutBuffer(
                        dec, ref fmt, ref MemoryMarshal.GetReference(destination.Memory.Span), requiredBytes);
                    if (st != LibJxl.JxlDecoderStatus.Success)
                        throw new DngException(DngError.JxlDecoder, $"JxlDecoderSetImageOutBuffer failed: {st}");
                    break;

                case LibJxl.JxlDecoderStatus.FullImage:
                    // Pixel data has been written to destination; continue to Success.
                    break;

                case LibJxl.JxlDecoderStatus.Success:
                    return;

                case LibJxl.JxlDecoderStatus.NeedMoreInput:
                    throw new DngException(DngError.JxlDecoder,
                        "JXL decode failed: decoder requested more input after CloseInput "
                        + "(truncated or corrupt JXL payload)");

                default:
                    throw new DngException(DngError.JxlDecoder,
                        $"Unexpected JxlDecoderStatus {st} ({(int)st}) during ProcessInput");
            }
        }
    }
}

/// <summary>
/// JXL signature classification used by hosts that want to sniff before
/// committing to a full decode (e.g. preview pipelines that fall back to a
/// JPEG preview when JXL isn't available).
/// </summary>
public enum JxlSignatureKind
{
    NotEnoughBytes = 1,
    Invalid = 0,
    RawCodestream = 2,
    BmffContainer = 3,
}

/// <summary>Thin wrapper around <c>JxlSignatureCheck</c> for host code.</summary>
public static class JxlProbe
{
    public static JxlSignatureKind Sniff(ReadOnlySpan<byte> bytes)
    {
        if (!JxlDecoder.IsAvailable) return JxlSignatureKind.Invalid;
        if (bytes.IsEmpty) return JxlSignatureKind.NotEnoughBytes;
        var sig = LibJxl.JxlSignatureCheck(ref MemoryMarshal.GetReference(bytes), (nuint)bytes.Length);
        return (JxlSignatureKind)sig;
    }
}
