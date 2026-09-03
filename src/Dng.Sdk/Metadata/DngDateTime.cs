using System.Globalization;

namespace Dng.Sdk.Metadata;

/// <summary>
/// Wall-clock date/time. Mirrors <c>dng_date_time</c>. Distinct from
/// <see cref="DateTime"/> because EXIF dates carry no time-zone information,
/// and DNG/EXIF accepts partial validity (Year 0 → "invalid").
/// </summary>
public struct DngDateTime : IEquatable<DngDateTime>
{
    public uint Year;
    public uint Month;
    public uint Day;
    public uint Hour;
    public uint Minute;
    public uint Second;

    public DngDateTime(uint year, uint month, uint day, uint hour, uint minute, uint second)
    {
        Year = year; Month = month; Day = day;
        Hour = hour; Minute = minute; Second = second;
    }

    /// <summary>
    /// True iff all fields are within valid ranges. Mirrors C++
    /// <c>dng_date_time::IsValid</c>. Year 0 is invalid; months are 1-12;
    /// days are 1-31 (does not validate per-month day counts — matches C++).
    /// </summary>
    public readonly bool IsValid =>
        Year >= 1 && Year <= 9999 &&
        Month >= 1 && Month <= 12 &&
        Day >= 1 && Day <= 31 &&
        Hour <= 23 && Minute <= 59 && Second <= 59;

    public void Clear() => this = default;

    /// <summary>
    /// Parse the EXIF date string format <c>"YYYY:MM:DD HH:MM:SS"</c>.
    /// Returns true on success. Whitespace, NUL terminator, and a trailing
    /// fractional-seconds suffix are tolerated.
    /// </summary>
    public bool TryParseExif(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;

        // Strip trailing NULs and whitespace.
        s = s.Trim('\0', ' ', '\t');

        // Strict EXIF spec: 19 chars "YYYY:MM:DD HH:MM:SS".
        if (s.Length < 19) return false;

        if (!uint.TryParse(s.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) return false;
        if (s[4] != ':' && s[4] != '-') return false;
        if (!uint.TryParse(s.AsSpan(5, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mo)) return false;
        if (s[7] != ':' && s[7] != '-') return false;
        if (!uint.TryParse(s.AsSpan(8, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) return false;
        if (s[10] != ' ' && s[10] != 'T') return false;
        if (!uint.TryParse(s.AsSpan(11, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return false;
        if (s[13] != ':') return false;
        if (!uint.TryParse(s.AsSpan(14, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mi)) return false;
        if (s[16] != ':') return false;
        if (!uint.TryParse(s.AsSpan(17, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var se)) return false;

        var dt = new DngDateTime(y, mo, d, h, mi, se);
        if (!dt.IsValid) return false;
        this = dt;
        return true;
    }

    /// <summary>EXIF-format string <c>"YYYY:MM:DD HH:MM:SS"</c> or empty for invalid.</summary>
    public readonly string ToExifString() =>
        IsValid
            ? $"{Year:D4}:{Month:D2}:{Day:D2} {Hour:D2}:{Minute:D2}:{Second:D2}"
            : string.Empty;

    /// <summary>ISO 8601 <c>"YYYY-MM-DDTHH:MM:SS"</c> for XMP/RDF.</summary>
    public readonly string ToIso8601() =>
        IsValid
            ? $"{Year:D4}-{Month:D2}-{Day:D2}T{Hour:D2}:{Minute:D2}:{Second:D2}"
            : string.Empty;

    public readonly bool Equals(DngDateTime other) =>
        Year == other.Year && Month == other.Month && Day == other.Day
        && Hour == other.Hour && Minute == other.Minute && Second == other.Second;

    public override readonly bool Equals(object? obj) => obj is DngDateTime d && Equals(d);
    public override readonly int GetHashCode() => HashCode.Combine(Year, Month, Day, Hour, Minute, Second);
    public static bool operator ==(DngDateTime a, DngDateTime b) => a.Equals(b);
    public static bool operator !=(DngDateTime a, DngDateTime b) => !a.Equals(b);
    public override readonly string ToString() => ToExifString();
}
