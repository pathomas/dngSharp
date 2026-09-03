using Dng.Sdk.Container;
using Dng.Sdk.IO;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Tests.Golden;

/// <summary>
/// Tier-1 golden diff (Phase 10): parses the native <c>dng_validate -v</c>
/// text output for each shipped sample DNG and asserts that the managed
/// <see cref="DngContainer"/> reports the same byte order, BigTIFF-ness,
/// IFD 0 offset, IFD 0 entry count, and IFD 0 tag set.
///
/// <para>Tag-value formatting (numeric ranges, enum decoding, matrix
/// pretty-printing) is intentionally NOT diffed here — that would require
/// porting <c>dng_parse_utils.cpp</c> verbatim. This suite protects the
/// structural invariants (parser doesn't lose or invent IFDs / entries).
/// </para>
///
/// <para>Tests silently skip when the goldens haven't been captured
/// (<c>tests/golden/&lt;sample&gt;/verbose.txt</c> missing) so the suite
/// remains green on machines without the native <c>dng_validate</c>
/// binary. Regenerate with <c>pwsh tests/golden/capture.ps1 -VerboseOnly</c>.
/// </para>
/// </summary>
public class GoldenVerboseDiffTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string SamplesDir => Path.Combine(RepoRoot, "dng_sdk_1_7_1", "sample_files");
    private static string GoldensDir => Path.Combine(RepoRoot, "tests", "golden");

    public static IEnumerable<object[]> AllSamples()
    {
        if (!Directory.Exists(SamplesDir))
        {
            yield return new object[] { "__no_samples__" };
            yield break;
        }

        foreach (var dng in Directory.EnumerateFiles(SamplesDir, "*.dng").OrderBy(p => p, StringComparer.Ordinal))
            yield return new object[] { Path.GetFileNameWithoutExtension(dng) };
    }

    [Theory]
    [MemberData(nameof(AllSamples))]
    public void Container_shape_matches_native_verbose_dump(string sampleStem)
    {
        if (sampleStem == "__no_samples__") return;

        string dngPath = Path.Combine(SamplesDir, sampleStem + ".dng");
        string goldenPath = Path.Combine(GoldensDir, sampleStem, "verbose.txt");
        if (!File.Exists(goldenPath)) return;

        var native = NativeVerboseParser.Parse(goldenPath);

        using var stream = DngFileStream.OpenRead(dngPath);
        var container = DngContainer.Parse(stream);

        Assert.Equal(native.BigEndian, container.Header.BigEndian);
        Assert.Equal(native.BigTiff, container.Header.BigTiff);

        var nativeIfd0 = native.FindByKindPrefix("IFD 0");
        Assert.NotNull(nativeIfd0);
        Assert.True(container.TopLevelIfds.Count >= 1, "container has no IFD 0");

        var managedIfd0 = container.TopLevelIfds[0];
        Assert.Equal(nativeIfd0!.Offset, managedIfd0.Offset);
        Assert.Equal(nativeIfd0.EntryCount, managedIfd0.Entries.Count);

        // Every native-reported IFD 0 tag name that we know as a defined
        // enum value must be present on the managed side. Unknown tags on
        // either side are tolerated: native may print vendor names we
        // haven't cataloged; we may parse tag codes native doesn't dump.
        var managedNames = managedIfd0.Entries
            .Where(e => Enum.IsDefined(e.Tag))
            .Select(e => e.Tag.ToString())
            .ToHashSet(StringComparer.Ordinal);

        var aliasMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["XMP"] = nameof(DngTagCode.XMP),
        };

        var missing = new List<string>();
        foreach (var nativeName in nativeIfd0.TagNames)
        {
            var expected = aliasMap.TryGetValue(nativeName, out var mapped) ? mapped : nativeName;
            if (!Enum.TryParse<DngTagCode>(expected, out _)) continue;
            if (!managedNames.Contains(expected))
                missing.Add(expected);
        }

        Assert.True(missing.Count == 0,
            $"IFD 0 tags reported by native dng_validate but missing from managed container: "
            + string.Join(", ", missing));
    }
}
