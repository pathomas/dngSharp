using DngSharp.Dng.Sdk.Errors;

namespace DngSharp.Dng.Sdk.Metadata.Iptc;

/// <summary>
/// IPTC IIM (Information Interchange Model) metadata. Mirrors a subset of
/// <c>dng_iptc</c>. IPTC blocks live in the legacy <c>Photoshop:8649</c> tag
/// and (historically) the <c>RichTIFFIPTC</c> / <c>IPTCNAA</c> tag — XMP has
/// superseded IPTC in DNG since spec 1.1, but legacy readers still expect
/// these fields when present.
/// </summary>
public sealed class DngIptc
{
    public string Title { get; set; } = string.Empty;
    public int Urgency { get; set; }
    public string Category { get; set; } = string.Empty;
    public List<string> SupplementalCategories { get; } = [];
    public List<string> Keywords { get; } = [];
    public string Instructions { get; set; } = string.Empty;
    public DngDateTime DateCreated { get; set; }
    public DngDateTime DigitalCreationDateTime { get; set; }
    public List<string> Authors { get; } = [];
    public string AuthorsPosition { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string TransmissionReference { get; set; } = string.Empty;
    public string Headline { get; set; } = string.Empty;
    public string Credit { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string CopyrightNotice { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DescriptionWriter { get; set; } = string.Empty;

    public bool IsEmpty =>
        Title.Length == 0 && Urgency == 0 && Category.Length == 0
        && SupplementalCategories.Count == 0 && Keywords.Count == 0
        && Instructions.Length == 0 && !DateCreated.IsValid
        && !DigitalCreationDateTime.IsValid && Authors.Count == 0
        && AuthorsPosition.Length == 0 && City.Length == 0
        && State.Length == 0 && Country.Length == 0 && CountryCode.Length == 0
        && Location.Length == 0 && TransmissionReference.Length == 0
        && Headline.Length == 0 && Credit.Length == 0 && Source.Length == 0
        && CopyrightNotice.Length == 0 && Description.Length == 0
        && DescriptionWriter.Length == 0;
}

/// <summary>
/// IPTC IIM block parser. Mirrors <c>dng_iptc::Parse</c>. Each DataSet record
/// is framed as: <c>0x1C, recordNumber, datasetNumber, length(uint16 BE),
/// data...</c>. Strings are UTF-8 if the file declares CharSet=UTF8 in
/// record 1:90; we conservatively treat all bytes as UTF-8 with replacement
/// fallback.
/// </summary>
public static class IptcReader
{
    private const byte TagMarker = 0x1C;
    private const byte CharSetUtf8 = 1;

    /// <summary>
    /// Read an IPTC block from <paramref name="block"/>. Unknown DataSet
    /// numbers are skipped silently; framing errors throw
    /// <see cref="DngException"/>(<see cref="DngError.BadFormat"/>).
    /// </summary>
    public static DngIptc Read(ReadOnlySpan<byte> block)
    {
        var iptc = new DngIptc();
        bool isUtf8 = false;

        int i = 0;
        while (i < block.Length)
        {
            if (block[i] != TagMarker)
            {
                // Some IPTC writers prefix the block with junk or pad — skip to next marker.
                i++;
                continue;
            }
            if (i + 5 > block.Length)
                throw new DngException(DngError.BadFormat, "Truncated IPTC record header");

            byte record = block[i + 1];
            byte dataset = block[i + 2];
            ushort length = (ushort)((block[i + 3] << 8) | block[i + 4]);

            int payloadStart = i + 5;
            if (payloadStart + length > block.Length)
                throw new DngException(DngError.BadFormat,
                    $"Truncated IPTC payload (need {length} from offset {payloadStart})");

            var payload = block.Slice(payloadStart, length);
            i = payloadStart + length;

            // Charset declaration (record 1, dataset 90).
            if (record == 1 && dataset == 90 && payload.Length >= 1)
            {
                // ESC % G = UTF-8 (RFC 2279). We accept any single-byte ESC sequence
                // that includes 'G' as UTF-8.
                if (payload.IndexOf((byte)'G') >= 0) isUtf8 = true;
                continue;
            }

            // Only record 2 carries content.
            if (record != 2) continue;

            string s = Decode(payload, isUtf8);

            switch (dataset)
            {
                case 5: iptc.Title = s; break;
                case 10:
                    if (int.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var u)) iptc.Urgency = u;
                    break;
                case 15: iptc.Category = s; break;
                case 20: iptc.SupplementalCategories.Add(s); break;
                case 25: iptc.Keywords.Add(s); break;
                case 40: iptc.Instructions = s; break;
                case 55: iptc.DateCreated = ParseDate(s, iptc.DateCreated); break;
                case 60: iptc.DateCreated = ParseTime(s, iptc.DateCreated); break;
                case 62: iptc.DigitalCreationDateTime = ParseDate(s, iptc.DigitalCreationDateTime); break;
                case 63: iptc.DigitalCreationDateTime = ParseTime(s, iptc.DigitalCreationDateTime); break;
                case 80: iptc.Authors.Add(s); break;
                case 85: iptc.AuthorsPosition = s; break;
                case 90: iptc.City = s; break;
                case 92: iptc.Location = s; break;
                case 95: iptc.State = s; break;
                case 100: iptc.CountryCode = s; break;
                case 101: iptc.Country = s; break;
                case 103: iptc.TransmissionReference = s; break;
                case 105: iptc.Headline = s; break;
                case 110: iptc.Credit = s; break;
                case 115: iptc.Source = s; break;
                case 116: iptc.CopyrightNotice = s; break;
                case 120: iptc.Description = s; break;
                case 122: iptc.DescriptionWriter = s; break;

                // Other datasets (RecordVersion etc.) are silently ignored.
                default: break;
            }
        }

        return iptc;
    }

    private static string Decode(ReadOnlySpan<byte> payload, bool isUtf8)
    {
        // Either UTF-8 (explicit) or Latin-1 (default per IPTC IIM). We use
        // UTF-8 with fallback to Latin-1 for safety.
        var enc = isUtf8
            ? System.Text.Encoding.UTF8
            : System.Text.Encoding.Latin1;
        return enc.GetString(payload).TrimEnd('\0');
    }

    private static DngDateTime ParseDate(string s, DngDateTime existing)
    {
        // IPTC date is "CCYYMMDD".
        if (s.Length < 8) return existing;
        if (!uint.TryParse(s.AsSpan(0, 4), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var y)) return existing;
        if (!uint.TryParse(s.AsSpan(4, 2), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var mo)) return existing;
        if (!uint.TryParse(s.AsSpan(6, 2), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var d)) return existing;
        var dt = existing;
        dt.Year = y; dt.Month = mo; dt.Day = d;
        return dt;
    }

    private static DngDateTime ParseTime(string s, DngDateTime existing)
    {
        // IPTC time is "HHMMSS±HHMM" (we drop the offset).
        if (s.Length < 6) return existing;
        if (!uint.TryParse(s.AsSpan(0, 2), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var h)) return existing;
        if (!uint.TryParse(s.AsSpan(2, 2), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var mi)) return existing;
        if (!uint.TryParse(s.AsSpan(4, 2), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var se)) return existing;
        var dt = existing;
        dt.Hour = h; dt.Minute = mi; dt.Second = se;
        return dt;
    }
}
