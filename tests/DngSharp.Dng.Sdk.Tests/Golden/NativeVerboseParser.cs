using System.Text.RegularExpressions;

namespace DngSharp.Dng.Sdk.Tests.Golden;

/// <summary>
/// Structural parser for <c>dng_validate -v</c> text output. Extracts just
/// enough shape (byte order, TIFF magic, per-IFD offset/entry count/tag
/// names) to diff against the managed <see cref="DngSharp.Dng.Sdk.Container.DngContainer"/>
/// tree without matching the native tag-value formatting byte-for-byte
/// (that lives in <c>dng_parse_utils.cpp</c> and is out of scope for the
/// tier-1 golden diff).
/// </summary>
internal static class NativeVerboseParser
{
    private static readonly Regex IfdHeader = new(
        @"^(?<kind>IFD\s+\d+|SubIFD\s+\d+|Exif\s+IFD|GPS\s+IFD|Interoperability\s+IFD|Camera\s+Profile\s+IFD)\s*:\s+Offset\s*=\s*(?<offset>\d+),\s+Entries\s*=\s*(?<count>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Native dng_validate expands ExtraCameraProfiles inline after IFD 0's
    // ExtraCameraProfiles tag: "ExtraCameraProfile [0]:" starts a sub-block
    // whose tag lines belong to that profile's own TIFF IFD, not to IFD 0.
    // Recognise these markers so their contents don't leak into IFD 0's tag
    // set. Same treatment for the MakerNote and IPTC blocks the printer emits.
    private static readonly Regex SubSectionMarker = new(
        @"^(ExtraCameraProfile\s*\[\d+\]|MakerNote|IPTC-NAA)\s*:\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TagLine = new(
        @"^(?<name>[A-Za-z][A-Za-z0-9_]*):\s",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ByteOrder = new(
        @"^Uses\s+(?<order>little|big)-endian\s+byte\s+order\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Magic = new(
        @"^Magic\s+number\s*=\s*(?<n>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static NativeDump Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        bool bigEndian = false;
        int magic = 0;
        var ifds = new List<NativeIfd>();
        NativeIfd? current = null;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimEnd();

            var boMatch = ByteOrder.Match(line);
            if (boMatch.Success)
            {
                bigEndian = boMatch.Groups["order"].Value == "big";
                continue;
            }

            var magMatch = Magic.Match(line);
            if (magMatch.Success)
            {
                magic = int.Parse(magMatch.Groups["n"].Value);
                continue;
            }

            var hdr = IfdHeader.Match(line);
            if (hdr.Success)
            {
                current = new NativeIfd
                {
                    Kind = Regex.Replace(hdr.Groups["kind"].Value, @"\s+", " "),
                    Offset = long.Parse(hdr.Groups["offset"].Value),
                    EntryCount = int.Parse(hdr.Groups["count"].Value),
                };
                ifds.Add(current);
                continue;
            }

            if (SubSectionMarker.IsMatch(line))
            {
                // Enter a "swallow" mode: subsequent tag lines don't belong
                // to any IFD until the next real IFD header (or EOF).
                current = null;
                continue;
            }

            if (current is null) continue;

            var tag = TagLine.Match(line);
            if (tag.Success)
            {
                // XMP printer emits many "XMP: ..." continuation lines; only
                // count each tag name once per IFD.
                current.TagNames.Add(tag.Groups["name"].Value);
            }
        }

        return new NativeDump
        {
            BigEndian = bigEndian,
            Magic = magic,
            Ifds = ifds,
        };
    }
}

internal sealed class NativeDump
{
    public required bool BigEndian { get; init; }
    public required int Magic { get; init; }
    public required List<NativeIfd> Ifds { get; init; }

    public NativeIfd? FindByKindPrefix(string prefix) =>
        Ifds.Find(i => i.Kind.StartsWith(prefix, StringComparison.Ordinal));

    public bool BigTiff => Magic == 43;
}

internal sealed class NativeIfd
{
    public required string Kind { get; init; }
    public required long Offset { get; init; }
    public required int EntryCount { get; init; }
    public HashSet<string> TagNames { get; } = new(StringComparer.Ordinal);
}
