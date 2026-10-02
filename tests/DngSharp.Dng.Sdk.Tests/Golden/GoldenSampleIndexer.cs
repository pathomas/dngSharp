// Copyright (c) DngSharp contributors.

using System;
using System.Buffers.Binary;
using DngSharp.Dng.Sdk.Pixels;

namespace DngSharp.Dng.Sdk.Tests.Golden;

/// <summary>
/// Maps a golden-TIFF sample index onto the managed <see cref="PixelBuffer"/>
/// that holds the same image.
/// <para>Golden stage dumps written by the native <c>dng_validate</c> are
/// always interleaved and row-major (R,G,B for pixel 0, then pixel 1, ...).
/// Managed buffers are not: <see cref="PixelBuffer.Planar"/> images store each
/// plane as one contiguous region. Walking the managed bytes with the golden's
/// flat index therefore compares the wrong samples for any multi-plane planar
/// image. Go through the buffer's own <see cref="PixelBuffer.RowStep"/> /
/// <see cref="PixelBuffer.ColStep"/> / <see cref="PixelBuffer.PlaneStep"/>
/// instead, which is correct for either layout.</para>
/// </summary>
internal static class GoldenSampleIndexer
{
    /// <summary>
    /// Translate <paramref name="goldenIndex"/> (interleaved, row-major) into a
    /// sample offset within <paramref name="buffer"/>, honouring its layout.
    /// </summary>
    public static int SampleIndex(in PixelBuffer buffer, int goldenIndex)
    {
        int planes = (int)buffer.Planes;
        int width = (int)buffer.Area.W;

        int pixel = goldenIndex / planes;
        int plane = goldenIndex - (pixel * planes);
        int row = pixel / width;
        int col = pixel - (row * width);

        return (int)((row * buffer.RowStep) + (col * buffer.ColStep) + (plane * buffer.PlaneStep));
    }

    /// <summary>
    /// Read the Float32 sample that corresponds to the golden's
    /// <paramref name="goldenIndex"/>.
    /// </summary>
    public static float ReadFloat32(ReadOnlySpan<byte> bytes, in PixelBuffer buffer, int goldenIndex)
        => BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(SampleIndex(buffer, goldenIndex) * 4, 4));
}
