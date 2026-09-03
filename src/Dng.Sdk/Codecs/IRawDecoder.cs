using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Codecs;

/// <summary>
/// Decode a single strip or tile of compressed raw data into a pixel buffer.
/// One implementation per DNG <see cref="Compression"/> value.
///
/// <para>Implementations are stateless and thread-safe — the same decoder
/// instance may be invoked concurrently from <see cref="Tasks.IAreaTask"/>
/// dispatchers.</para>
/// </summary>
public interface IRawDecoder
{
    /// <summary>The compression code this decoder handles.</summary>
    Compression Compression { get; }

    /// <summary>
    /// Decode <paramref name="compressed"/> into <paramref name="destination"/>.
    /// The destination's <see cref="PixelBuffer.Area"/> describes the
    /// strip/tile size; <see cref="PixelBuffer.PixelType"/> and
    /// <see cref="PixelBuffer.Planes"/> describe the expected sample layout.
    /// </summary>
    /// <param name="compressed">On-disk payload bytes for this strip/tile.</param>
    /// <param name="destination">Pre-allocated buffer to populate.</param>
    /// <param name="bigEndian">Byte order of multi-byte samples in <paramref name="compressed"/>.</param>
    void Decode(ReadOnlySpan<byte> compressed, PixelBuffer destination, bool bigEndian);
}

/// <summary>
/// Encode a tightly-packed pixel buffer into compressed bytes. Mirrors the
/// write side. Implementations are added in Phase 8 (image writer).
/// </summary>
public interface IRawEncoder
{
    Compression Compression { get; }

    /// <summary>
    /// Encode <paramref name="source"/> into a freshly allocated byte array.
    /// </summary>
    byte[] Encode(PixelBuffer source, bool bigEndian);
}
