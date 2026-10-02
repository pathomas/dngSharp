using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Metadata.Exif;

/// <summary>
/// EXIF metadata. Mirrors a subset of <c>dng_exif</c>. Pure data — parsing
/// from an IFD entry stream lives in <see cref="ExifReader"/>.
///
/// <para>This intentionally covers the EXIF tags most consumers rely on
/// (camera/lens identification, exposure, GPS, date/time, color space). The
/// long tail of legacy/manufacturer-specific tags is intentionally
/// uncovered — adding them is a mechanical exercise once a test exposes a
/// missing field.</para>
/// </summary>
public sealed class DngExif
{
    // --- Image identification ----------------------------------------------
    public string Make { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Software { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Copyright { get; set; } = string.Empty;
    public string ImageDescription { get; set; } = string.Empty;
    public string CameraSerialNumber { get; set; } = string.Empty;

    // --- Lens --------------------------------------------------------------
    public string LensMake { get; set; } = string.Empty;
    public string LensModel { get; set; } = string.Empty;
    public string LensSerialNumber { get; set; } = string.Empty;

    public DngURational LensInfoMinFocal { get; set; }
    public DngURational LensInfoMaxFocal { get; set; }
    public DngURational LensInfoMinApertureMinFocal { get; set; }
    public DngURational LensInfoMinApertureMaxFocal { get; set; }

    // --- Exposure ----------------------------------------------------------
    public DngURational ExposureTime { get; set; }
    public DngURational FNumber { get; set; }
    public DngSRational ExposureBias { get; set; }
    public DngURational FocalLength { get; set; }
    public uint FocalLengthIn35mmFilm { get; set; }
    public uint IsoSpeedRating { get; set; }
    public uint Flash { get; set; }
    public uint MeteringMode { get; set; }
    public uint ExposureProgram { get; set; }
    public uint LightSource { get; set; }
    public uint WhiteBalance { get; set; }

    // --- Time --------------------------------------------------------------
    /// <summary>
    /// Image-captured date/time (tag 36867 <c>DateTimeOriginal</c>).
    /// </summary>
    public DngDateTime DateTimeOriginal { get; set; }
    /// <summary>
    /// File-modified date/time (tag 306 <c>DateTime</c>).
    /// </summary>
    public DngDateTime DateTime { get; set; }
    /// <summary>
    /// Digitization date/time (tag 36868 <c>DateTimeDigitized</c>).
    /// </summary>
    public DngDateTime DateTimeDigitized { get; set; }

    public string OffsetTime { get; set; } = string.Empty;           // tag 36880 (DNG 1.4+)
    public string OffsetTimeOriginal { get; set; } = string.Empty;   // tag 36881
    public string OffsetTimeDigitized { get; set; } = string.Empty;  // tag 36882

    // --- GPS ---------------------------------------------------------------
    public string GpsVersionId { get; set; } = string.Empty;
    public string GpsLatitudeRef { get; set; } = string.Empty;   // "N" / "S"
    public string GpsLongitudeRef { get; set; } = string.Empty;  // "E" / "W"
    public DngURational[] GpsLatitude { get; set; } = [];        // deg, min, sec
    public DngURational[] GpsLongitude { get; set; } = [];
    public byte GpsAltitudeRef { get; set; }                     // 0 above, 1 below sea level
    public DngURational GpsAltitude { get; set; }
    public string GpsDateStamp { get; set; } = string.Empty;     // "YYYY:MM:DD"

    // --- Color space -------------------------------------------------------
    public uint ColorSpace { get; set; }       // 1 = sRGB, 0xFFFF = Uncalibrated
    public uint ExifVersion { get; set; }      // packed 4-byte ASCII (e.g. 0x30323330 = "0230")

    /// <summary>Free-form host extension stash (uninterpreted EXIF tags).</summary>
    public Dictionary<uint, ReadOnlyMemory<byte>> UnknownTags { get; } = [];
}
