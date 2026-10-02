using System.Buffers.Binary;
using DngSharp.Dng.Sdk;
using DngSharp.Dng.Sdk.Color;
using DngSharp.Dng.Sdk.Color.Cct;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Math;
using DngSharp.Dng.Sdk.Metadata;
using DngSharp.Dng.Sdk.Render;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Imaging.Profile;

/// <summary>
/// Reads the embedded camera-profile and shared color metadata from IFD 0.
/// Mirrors the subset of <c>dng_negative::Parse</c> needed by the current
/// render path: calibration illuminants, matrices, as-shot white balance,
/// analog balance, baseline exposure, and optional tone curve.
/// </summary>
public static class CameraProfileReader
{
    public static DngCameraProfile? Read(
        DngStream stream,
        TiffIfd ifd,
        bool bigEndian,
        DngShared shared)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(ifd);
        ArgumentNullException.ThrowIfNull(shared);

        if (ifd.Find(DngTagCode.AnalogBalance) is { } analogBalance)
        {
            var values = ReadURationalDoubles(stream, analogBalance, bigEndian);
            if (values.Length > 0)
                shared.AnalogBalance = ToVector(values, analogBalance.Tag);
        }

        if (ifd.Find(DngTagCode.AsShotNeutral) is { } asShotNeutral)
        {
            var values = ReadURationalDoubles(stream, asShotNeutral, bigEndian);
            if (values.Length > 0)
                shared.SetAsShotNeutral(ToVector(values, asShotNeutral.Tag));
        }

        if (ifd.Find(DngTagCode.AsShotWhiteXY) is { } asShotWhiteXy)
        {
            var values = ReadURationalDoubles(stream, asShotWhiteXy, bigEndian);
            if (values.Length >= 2)
                shared.SetAsShotWhiteXy(new XyCoord(values[0], values[1]));
        }

        if (ifd.Find(DngTagCode.BaselineExposure) is { } baselineExposure)
            shared.BaselineExposure = ReadScalarSRationalDouble(stream, baselineExposure, bigEndian);

        var profile = new DngCameraProfile
        {
            ToneCurve = ifd.Find(DngTagCode.ProfileToneCurve) is { } toneCurve
                ? ReadToneCurve(stream, toneCurve, bigEndian)
                : null,
        };

        // ProfileHueSatMapDims applies to all Data1/2/3 tables in the profile.
        (int Hue, int Sat, int Val)? hsmDims = ifd.Find(DngTagCode.ProfileHueSatMapDims) is { } dimsEntry
            ? ReadHueSatMapDims(stream, dimsEntry, bigEndian)
            : null;

        if (ReadCalibration(stream, ifd, bigEndian, 1) is { } illum1)
        {
            if (hsmDims is { } d1 && ifd.Find(DngTagCode.ProfileHueSatMapData1) is { } hsm1Entry)
                illum1.HueSatMap = ReadHueSatMap(stream, hsm1Entry, bigEndian, d1);
            profile.Illuminants.Add(illum1);
        }

        if (ReadCalibration(stream, ifd, bigEndian, 2) is { } illum2)
        {
            if (hsmDims is { } d2 && ifd.Find(DngTagCode.ProfileHueSatMapData2) is { } hsm2Entry)
                illum2.HueSatMap = ReadHueSatMap(stream, hsm2Entry, bigEndian, d2);
            profile.Illuminants.Add(illum2);
        }

        bool hasProfileData = profile.Illuminants.Count > 0
            || (profile.ToneCurve is { Length: > 0 });

        return hasProfileData ? profile : null;
    }

    private static CalibrationIlluminant? ReadCalibration(
        DngStream stream,
        TiffIfd ifd,
        bool bigEndian,
        int index)
    {
        var illuminantTag = index == 1 ? DngTagCode.CalibrationIlluminant1 : DngTagCode.CalibrationIlluminant2;
        var colorTag = index == 1 ? DngTagCode.ColorMatrix1 : DngTagCode.ColorMatrix2;
        var forwardTag = index == 1 ? DngTagCode.ForwardMatrix1 : DngTagCode.ForwardMatrix2;
        var calibrationTag = index == 1 ? DngTagCode.CameraCalibration1 : DngTagCode.CameraCalibration2;

        double? kelvin = ifd.Find(illuminantTag) is { } illuminantEntry
            ? ExifLightSourceToKelvin(illuminantEntry.GetScalarUInt(bigEndian))
            : null;

        var colorMatrix = ReadColorLikeMatrix(stream, ifd.Find(colorTag), bigEndian);
        var forwardMatrix = ReadColorLikeMatrix(stream, ifd.Find(forwardTag), bigEndian);
        var cameraCalibration = ReadSquareMatrix(stream, ifd.Find(calibrationTag), bigEndian);

        if (kelvin is null && colorMatrix is null && forwardMatrix is null && cameraCalibration is null)
            return null;

        double resolvedKelvin = kelvin ?? 5000.0;

        return new CalibrationIlluminant
        {
            Kelvin = resolvedKelvin,
            WhitePoint = resolvedKelvin > 0.0
                ? CctRobertson.TemperatureTintToXy(resolvedKelvin, 0.0)
                : XyCoord.D50,
            ColorMatrix = colorMatrix,
            ForwardMatrix = forwardMatrix,
            CameraCalibration = cameraCalibration,
        };
    }

    private static DngMatrix? ReadColorLikeMatrix(DngStream stream, TiffIfdEntry? entry, bool bigEndian)
    {
        if (entry is null) return null;

        var flat = ReadSRationalDoubles(stream, entry, bigEndian);
        if (flat.Length == 0) return null;
        if (flat.Length % 3 != 0)
            DngThrow.BadFormat($"Tag {entry.Tag}: expected 3×N matrix payload, got {flat.Length} values");

        int cols = flat.Length / 3;
        if (cols <= 0 || cols > DngLimits.MaxColorPlanes)
            DngThrow.BadFormat($"Tag {entry.Tag}: unsupported matrix width {cols}");

        return ToMatrix(flat, 3, cols);
    }

    private static DngMatrix? ReadSquareMatrix(DngStream stream, TiffIfdEntry? entry, bool bigEndian)
    {
        if (entry is null) return null;

        var flat = ReadSRationalDoubles(stream, entry, bigEndian);
        if (flat.Length == 0) return null;

        int size = (int)System.Math.Sqrt(flat.Length);
        if (size * size != flat.Length)
            DngThrow.BadFormat($"Tag {entry.Tag}: expected square matrix payload, got {flat.Length} values");
        if (size <= 0 || size > DngLimits.MaxColorPlanes)
            DngThrow.BadFormat($"Tag {entry.Tag}: unsupported matrix size {size}×{size}");

        return ToMatrix(flat, size, size);
    }

    private static DngMatrix ToMatrix(double[] flat, int rows, int cols)
    {
        var matrix = new DngMatrix(rows, cols);
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
                matrix[row, col] = flat[row * cols + col];
        return matrix;
    }

    private static DngVector ToVector(double[] values, DngTagCode tag)
    {
        if (values.Length > DngLimits.MaxColorPlanes)
            DngThrow.BadFormat($"Tag {tag}: expected <= {DngLimits.MaxColorPlanes} values, got {values.Length}");

        var vector = new DngVector(values.Length);
        for (int i = 0; i < values.Length; i++)
            vector[i] = values[i];
        return vector;
    }

    private static (double Input, double Output)[]? ReadToneCurve(
        DngStream stream,
        TiffIfdEntry entry,
        bool bigEndian)
    {
        if (entry.Type != TiffDataType.Float) return null;

        var bytes = ReadAllBytes(stream, entry);
        if ((bytes.Length % 4) != 0)
            return null;

        int valueCount = bytes.Length / 4;
        if (valueCount < 4 || (valueCount % 2) != 0)
            return null;

        var points = new (double Input, double Output)[valueCount / 2];
        var span = bytes.Span;

        for (int i = 0; i < points.Length; i++)
        {
            int offset = i * 8;
            float input = bigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(span.Slice(offset, 4))
                : BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset, 4));
            float output = bigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(span.Slice(offset + 4, 4))
                : BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 4, 4));
            points[i] = (input, output);
        }

        return points;
    }

    private static double[] ReadURationalDoubles(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        if (entry.Type != TiffDataType.Rational) return [];

        var bytes = ReadAllBytes(stream, entry);
        int count = bytes.Length / 8;
        var values = new double[count];
        var span = bytes.Span;

        for (int i = 0; i < count; i++)
        {
            uint numerator = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(span[(i * 8)..])
                : BinaryPrimitives.ReadUInt32LittleEndian(span[(i * 8)..]);
            uint denominator = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(span[(i * 8 + 4)..])
                : BinaryPrimitives.ReadUInt32LittleEndian(span[(i * 8 + 4)..]);
            values[i] = denominator != 0 ? (double)numerator / denominator : 0.0;
        }

        return values;
    }

    private static double[] ReadSRationalDoubles(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        if (entry.Type != TiffDataType.SRational) return [];

        var bytes = ReadAllBytes(stream, entry);
        int count = bytes.Length / 8;
        var values = new double[count];
        var span = bytes.Span;

        for (int i = 0; i < count; i++)
        {
            int numerator = bigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(span[(i * 8)..])
                : BinaryPrimitives.ReadInt32LittleEndian(span[(i * 8)..]);
            int denominator = bigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(span[(i * 8 + 4)..])
                : BinaryPrimitives.ReadInt32LittleEndian(span[(i * 8 + 4)..]);
            values[i] = denominator != 0 ? (double)numerator / denominator : 0.0;
        }

        return values;
    }

    private static double[] ReadFloatDoubles(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        if (entry.Type != TiffDataType.Float) return [];

        var bytes = ReadAllBytes(stream, entry);
        int count = bytes.Length / 4;
        var values = new double[count];
        var span = bytes.Span;

        for (int i = 0; i < count; i++)
            values[i] = bigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(span[(i * 4)..])
                : BinaryPrimitives.ReadSingleLittleEndian(span[(i * 4)..]);

        return values;
    }

    private static (int Hue, int Sat, int Val)? ReadHueSatMapDims(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        // ProfileHueSatMapDims is 3 Longs: HueDivisions, SatDivisions, ValDivisions.
        if (entry.Type != TiffDataType.Long) return null;

        var bytes = ReadAllBytes(stream, entry);
        if (bytes.Length < 12) return null;

        var span = bytes.Span;
        int hue = (int)(bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span[0..4]) : BinaryPrimitives.ReadUInt32LittleEndian(span[0..4]));
        int sat = (int)(bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span[4..8]) : BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]));
        int val = (int)(bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span[8..12]) : BinaryPrimitives.ReadUInt32LittleEndian(span[8..12]));

        if (hue <= 0 || sat <= 0 || val < 0) return null;
        return (hue, sat, val);
    }

    private static HueSatMap? ReadHueSatMap(
        DngStream stream,
        TiffIfdEntry entry,
        bool bigEndian,
        (int Hue, int Sat, int Val) dims)
    {
        var doubles = ReadFloatDoubles(stream, entry, bigEndian);
        if (doubles.Length == 0) return null;

        var floats = new float[doubles.Length];
        for (int i = 0; i < doubles.Length; i++)
            floats[i] = (float)doubles[i];

        try
        {
            return new HueSatMap(dims.Hue, dims.Sat, dims.Val, floats);
        }
        catch (ArgumentException)
        {
            return null; // Malformed/truncated table — ignore rather than fail the whole parse.
        }
    }

    private static double ReadScalarSRationalDouble(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        var values = ReadSRationalDoubles(stream, entry, bigEndian);
        return values.Length > 0 ? values[0] : 0.0;
    }

    private static ReadOnlyMemory<byte> ReadAllBytes(DngStream stream, TiffIfdEntry entry)
    {
        if (entry.IsInline)
        {
            int length = (int)System.Math.Min(entry.PayloadSize, (ulong)entry.InlineValue.Length);
            return entry.InlineValue[..length];
        }

        if (entry.PayloadSize > int.MaxValue)
            return ReadOnlyMemory<byte>.Empty;

        var buffer = new byte[(int)entry.PayloadSize];
        long saved = stream.Position;
        try
        {
            stream.Position = entry.ValueOffset;
            stream.ReadExactly(buffer);
        }
        finally
        {
            stream.Position = saved;
        }

        return buffer;
    }

    private static double ExifLightSourceToKelvin(uint lightSource) => lightSource switch
    {
        0 => 5000.0,
        1 => 5500.0,
        2 => 4000.0,
        3 => 2850.0,
        10 => 5500.0,
        17 => 2856.0,
        18 => 5503.0,
        19 => 6774.0,
        20 => 5503.0,
        21 => 6504.0,
        22 => 7504.0,
        23 => 5003.0,
        24 => 3200.0,
        255 => 5000.0,
        _ => 5000.0,
    };
}
