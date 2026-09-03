// Generates small, analytically-known synthetic DNG and TIFF fixtures for
// manual inspection / external tooling, reusing the same builders exercised
// by tests/Dng.Sdk.Tests/TestImages/*.cs (SyntheticDngBuilder /
// SyntheticTiffBuilder / SyntheticPixelPatterns) so output here and the test
// fixtures never drift apart.
//
// Output goes to a local synthetic/ folder (see .gitignore) since these are
// regenerable scratch fixtures, not source assets meant to be committed.
//
// Usage:
//   dotnet run --project tools/SyntheticImageGenerator
//   dotnet run --project tools/SyntheticImageGenerator -- --outdir synthetic --size 64 128
//   dotnet run --project tools/SyntheticImageGenerator -- --big-endian

using Dng.Sdk.Tests.TestImages;
using Dng.Sdk.Tiff;
using Dng.Sdk.Writer;

string outDir = "synthetic";
var sizes = new List<int>();
bool bigEndian = false;
bool goldenOpcodes = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--outdir":
            outDir = args[++i];
            break;
        case "--big-endian":
            bigEndian = true;
            break;
        case "--golden-opcodes":
            goldenOpcodes = true;
            break;
        case "--size":
            while (i + 1 < args.Length && int.TryParse(args[i + 1], out int size))
            {
                sizes.Add(size);
                i++;
            }
            break;
        case "-h":
        case "--help":
            Console.WriteLine("Usage: SyntheticImageGenerator [--outdir <dir>] [--size <n...>] [--big-endian] [--golden-opcodes]");
            return 0;
        default:
            Console.Error.WriteLine($"Unrecognized argument: {args[i]}");
            return 1;
    }
}

if (goldenOpcodes)
{
    WriteGoldenOpcodeFixtures();
    return 0;
}

if (sizes.Count == 0)
{
    sizes.AddRange([64, 256]);
}

Directory.CreateDirectory(outDir);
string suffix = bigEndian ? "_be" : "";

foreach (int size in sizes)
{
    WritePair("gradient", size, () => SyntheticPixelPatterns_GradientLeftToRight(size));
    WritePair("circle", size, () => SyntheticPixelPatterns_CenteredCircle(size));
    WritePair("checkerboard", size, () => Checkerboard(size, tile: 8));
}

return 0;

void WritePair(string name, int size, Func<ushort[]> makePixels)
{
    ushort[] pixels = makePixels();

    byte[] dng = SyntheticDngBuilder.BuildLinearRawDng(pixels, size, size, planes: 3, bigEndian);
    byte[] tiff = SyntheticTiffBuilder.BuildRgbTiff(pixels, size, size, bigEndian);

    string dngPath = Path.Combine(outDir, $"{name}_{size}{suffix}.dng");
    string tiffPath = Path.Combine(outDir, $"{name}_{size}{suffix}.tiff");
    File.WriteAllBytes(dngPath, dng);
    File.WriteAllBytes(tiffPath, tiff);
    Console.WriteLine($"wrote {dngPath} ({dng.Length} bytes)");
    Console.WriteLine($"wrote {tiffPath} ({tiff.Length} bytes)");
}

// SyntheticPixelPatterns' gradient/circle generators are `internal`, but this
// project links its source file directly (see the .csproj), so they're
// visible here as ordinary same-assembly members. Thin wrappers just give
// this top-level file stable local names to call through the lambdas above.
static ushort[] SyntheticPixelPatterns_GradientLeftToRight(int size) =>
    SyntheticPixelPatterns.GradientLeftToRight(size, size, maxValue: 65535);

static ushort[] SyntheticPixelPatterns_CenteredCircle(int size) =>
    SyntheticPixelPatterns.CenteredCircle(size, diameterFraction: 0.5, maxValue: 65535);

// Checkerboard is tool-only (not part of the current xUnit fixture set): fine
// alternating black/white tiles for tile-boundary / duplicate-row-or-column
// regression testing. R=G=B at every pixel, matching the other patterns.
static ushort[] Checkerboard(int size, int tile, ushort maxValue = 65535)
{
    var pixels = new ushort[(long)size * size * 3];
    for (int row = 0; row < size; row++)
    {
        for (int col = 0; col < size; col++)
        {
            bool on = ((col / tile) + (row / tile)) % 2 == 0;
            ushort v = on ? maxValue : (ushort)0;
            long baseIdx = ((long)row * size + col) * 3;
            pixels[baseIdx + 0] = v;
            pixels[baseIdx + 1] = v;
            pixels[baseIdx + 2] = v;
        }
    }
    return pixels;
}

// ── Golden opcode fixture generation (--golden-opcodes) ─────────────────
//
// Generates a small synthetic DNG with a FixVignetteRadial opcode embedded
// in OpcodeList2, saved under tests/golden/15_synthetic_fixvignetteradial/
// sample.dng. None of the bundled Adobe sample_files exercise lens-shading
// or per-pixel gain-map opcodes (FixVignetteRadial/WarpFisheye/
// WarpRectilinear2/GainMap), so golden coverage for those opcodes has to
// come from purpose-built synthetic fixtures instead. After regenerating
// this file, capture the native reference with (from repo root, Release
// build of dng_validate):
//
//   dng_sdk_1_7_1\dng_sdk\targets\win\release64_x64\dng_validate.exe `
//     -2 tests\golden\15_synthetic_fixvignetteradial\stage2.tif `
//     tests\golden\15_synthetic_fixvignetteradial\sample.dng
//
// See GoldenSyntheticOpcodeDiffTests for the corresponding managed-vs-native
// pixel comparison.
static void WriteGoldenOpcodeFixtures()
{
    string repoRoot = FindRepoRoot();
    string dir = Path.Combine(repoRoot, "tests", "golden", "15_synthetic_fixvignetteradial");
    Directory.CreateDirectory(dir);

    const int size = 32;
    var pixels = SyntheticPixelPatterns.GradientLeftToRight(size, size, maxValue: 65535);

    var opcodeListBytes = OpcodeListTestBuilder.Build(
        new OpcodeListTestBuilder.Entry(
            Id: 3, // FixVignetteRadial (see dng_sdk_1_7_1/.../dng_opcodes.h dng_opcode_id)
            Major: 1, Minor: 3, Patch: 0, Build: 0,
            Flags: 0,
            Body: OpcodeListTestBuilder.BuildFixVignetteRadialBody([0.3, 0.0, 0.0, 0.0, 0.0], 0.5, 0.5)));

    byte[] dng = SyntheticDngBuilder.BuildLinearRawDng(
        pixels, size, size, planes: 3, bigEndian: false,
        configureIfd: (ifd, be) =>
        {
            ifd.Entries.Add(new TiffEntryToWrite
            {
                Tag = DngTagCode.OpcodeList2,
                Type = TiffDataType.Undefined,
                Count = (uint)opcodeListBytes.Length,
                Payload = opcodeListBytes,
            });
        });

    string dngPath = Path.Combine(dir, "sample.dng");
    File.WriteAllBytes(dngPath, dng);
    Console.WriteLine($"wrote {dngPath} ({dng.Length} bytes)");
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dng.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
