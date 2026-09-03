# Copilot instructions for this repository

This repository contains **two parallel codebases**:

1. **`src/Dng.Sdk*` + `src/Dng.Validate` + `tests/Dng.Sdk.Tests`** — the active
   .NET 10 (C#) port of the Adobe DNG SDK. This is where new feature work
   happens. See `PORTING_PLAN.md` for the full phased plan.
2. **`dng_sdk_1_7_1/`** — the vendored Adobe DNG SDK 1.7.1 (C++17). This is
   the source of truth for spec semantics and the reference implementation we
   diff against. Don't change this tree unless you're cherry-picking an Adobe
   upstream fix; treat it as read-only.

The remainder of this document is split: the **.NET port** section first
(what most contributions touch), then the **native SDK** reference section
(useful when validating behavior against ground truth).

---

## .NET 10 port

### Solution layout

```
Dng.slnx                              .NET 10 SDK-style solution
Directory.Build.props                 net10.0, nullable on, warnings-as-errors,
                                      AllowUnsafeBlocks=true, AOT-friendly defaults
Directory.Build.targets               test-project overrides (no AOT/trim,
                                      relaxed analyzer rules)
Directory.Packages.props              Central Package Management
src/Dng.Sdk/                          core managed port (Phase 1+)
  DngLimits.cs, DngSdkInfo.cs
  Errors/                             DngError enum, DngException, DngThrow helpers
  Math/                               SafeArith, DngMath, DngMatrix, DngVector
  Primitives/                         DngPoint(F), DngRect(F), DngRational,
                                      DngOrientation
  Color/                              XyCoord, DngTemperature (stub)
  Hashing/                            DngFingerprint (MD5)
  Memory/                             IMemoryAllocator + PooledMemoryAllocator
  IO/                                 DngStream (endian-aware), file/memory
                                      adapters                                 (Phase 2)
  Tiff/                               TiffDataType + sizes, TiffEnums
                                      (NewSubFileType / Compression / etc.),
                                      DngTagCode (auto-generated)              (Phase 2)
  Container/                          TiffHeader, TiffIfd, TiffIfdEntry        (Phase 2)
src/Dng.Sdk.Jpeg/                     JPEG codec adapter (Phase 7 stub)
src/Dng.Sdk.Jxl/                      JPEG XL codec adapter (Phase 7 stub,
                                      will P/Invoke libjxl)
src/Dng.Sdk.Xmp/                      XMP adapter (Phase 4 stub, will P/Invoke
                                      libxmp)
src/Dng.Validate/                     CLI mirroring dng_validate.cpp
                                      (Phase 9 — currently a banner stub)
tests/Dng.Sdk.Tests/                  xUnit tests, snake_case names allowed
tests/golden/capture.ps1              regenerates reference outputs from the
                                      native dng_validate
tools/extract-tag-codes.ps1           regenerates Tiff/DngTagCode.cs from
                                      dng_sdk_1_7_1/.../dng_tag_codes.h
```

### Build, test, run

```powershell
dotnet build Dng.slnx -c Release        # 0 warnings, 0 errors expected
dotnet test Dng.slnx -c Release         # xUnit suite
dotnet run --project src/Dng.Validate   # CLI smoke (Phase 9 stub today)
```

AOT publish smoke test (requires Developer Command Prompt locally for the
native link step; CI runs it on `windows-latest` / `ubuntu-latest`):

```powershell
dotnet publish src/Dng.Validate -c Release -r win-x64 --self-contained -p:PublishAot=true
```

### .NET port conventions

- **Target framework:** `net10.0` only. No multi-targeting.
- **Nullable + warnings-as-errors are on** in `Directory.Build.props`.
- **Unsafe code is enabled globally** (`AllowUnsafeBlocks=true`). Reserve
  pointer-based code for measured wins in pixel-buffer hot paths
  (linearization, demosaic, color transform); for stream/IFD I/O, prefer
  `Span<T>` + `BinaryPrimitives`.
- **AOT-compatible by default** (`IsAotCompatible=true`, `IsTrimmable=true`,
  `InvariantGlobalization=true`). Test projects opt out via
  `Directory.Build.targets`.
- **Central Package Management:** add NuGet packages to
  `Directory.Packages.props`, never inline `<PackageReference Version=…>`.
- **POD-style field surface is intentional** for types that mirror C++ layout
  (`DngPoint.V/H`, `DngRect.T/L/B/R`, `DngSRational.N/D`, …). `CA1051` is
  suppressed globally with a justification comment in `Directory.Build.props`.
- **MD5 is spec-mandated** (`OriginalRawFileDigest`, `RawDataUniqueID`, etc.);
  `CA5351` is suppressed for the same reason.
- **Endianness:** always go through `DngStream` or `BinaryPrimitives` — never
  reinterpret-cast. The `DngStream` honors per-instance byte order and is
  flipped to big-endian when parsing **opcode list bodies**, regardless of
  the host TIFF order (per spec).
- **Two-phase parse mirrors the C++ SDK.** When the port catches up, expect
  `Parse(...)` followed by `PostParse(...)` on container/negative types.
  Treating `Parse` as complete is a bug there too.
- **Tag-code enum is generated.** `src/Dng.Sdk/Tiff/DngTagCode.cs` carries a
  `// <auto-generated />` header. To regenerate after the vendored SDK is
  upgraded, run `pwsh tools/extract-tag-codes.ps1`. Don't hand-edit the
  generated file; add or fix tags in the source header (or the script).
- **Golden-file diffs are the validation strategy.** Regenerate goldens from
  the native `dng_validate Release` binary via `tests/golden/capture.ps1`,
  then write xUnit tests that diff parsed output (e.g., `-v` text dumps,
  stage-1/2/3 TIFFs) against `tests/golden/<sample>/`.
- **Test naming:** `snake_case_with_underscores` is allowed and conventional
  in `*.Tests` projects (CA1707 suppressed there only).

### Port phasing

`PORTING_PLAN.md` enumerates 10 phases. Phase status lives in the
session todo DB (`todos` table). Current state (as of Phase 2 in progress):

| # | Phase | Status |
|---|---|---|
| 0 | Scaffolding + CI + golden harness | ✅ done |
| 1 | Foundation types (errors, math, primitives, fingerprint, memory) | ✅ done |
| 2 | I/O + tags + TIFF/IFD parsing | 🟡 in progress |
| 3–10 | Pixel buffers → codecs → render → writer → CLI → validation | ⬜ pending |

---

## Native Adobe DNG SDK 1.7.1 (reference)

The vendored C++ tree under `dng_sdk_1_7_1/` is the source of truth for spec
semantics. Build it when you need golden outputs to diff the .NET port
against.

### Build and validation commands

The primary first-party build target in this SDK bundle is `dng_validate`, the reference command-line tool in `dng_sdk_1_7_1/dng_sdk/source/dng_validate.cpp`.

### Build `dng_validate`

**macOS**

```sh
xcodebuild -project dng_sdk_1_7_1/dng_sdk/projects/mac/dng_validate.xcodeproj -target "dng_validate debug"
xcodebuild -project dng_sdk_1_7_1/dng_sdk/projects/mac/dng_validate.xcodeproj -target "dng_validate release"
```

Outputs land under:

- `dng_sdk_1_7_1/dng_sdk/targets/mac/debug64/dng_validate`
- `dng_sdk_1_7_1/dng_sdk/targets/mac/release64/dng_validate`

**Windows**

Use MSBuild from the Visual Studio 2022 installation. The solution projects use `ClangCL` and `v100` toolsets that require an override to the installed MSVC toolset (`v143`):

```bat
set MSBUILD="D:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
%MSBUILD% dng_sdk_1_7_1\dng_sdk\projects\win\dng_validate.sln /p:Configuration="Validate Debug" /p:Platform=x64 /p:PlatformToolset=v143 /p:LanguageStandard=stdcpp17 /m
%MSBUILD% dng_sdk_1_7_1\dng_sdk\projects\win\dng_validate.sln /p:Configuration="Validate Release" /p:Platform=x64 /p:PlatformToolset=v143 /p:LanguageStandard=stdcpp17 /m
```

Or in PowerShell:

```powershell
$msbuild = "D:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild dng_sdk_1_7_1\dng_sdk\projects\win\dng_validate.sln /p:Configuration="Validate Debug" /p:Platform=x64 /p:PlatformToolset=v143 /p:LanguageStandard=stdcpp17 /m
& $msbuild dng_sdk_1_7_1\dng_sdk\projects\win\dng_validate.sln /p:Configuration="Validate Release" /p:Platform=x64 /p:PlatformToolset=v143 /p:LanguageStandard=stdcpp17 /m
```

> **Why the overrides?** The `.vcxproj` files specify `ClangCL` (libjxl/dng_validate) and `v100` (XMP) toolsets. ClangCL is not installed (only clang-format/tidy ship with the VS 2022 instance). `/p:PlatformToolset=v143` fixes both. `/p:LanguageStandard=stdcpp17` restores C++17, which the ClangCL-specific `CCppSupport` property would have set automatically.

Representative outputs:

- `dng_sdk_1_7_1\dng_sdk\targets\win\debug64_x64\dng_validate.exe`
- `dng_sdk_1_7_1\dng_sdk\targets\win\release64_x64\dng_validate.exe`

> **JXL performance note:** JXL decoding in a Debug build is extremely slow. Use the Release binary when validating JXL-compressed DNG files (`01_jxl_*.dng`, `03_jxl_*.dng`).

### Run a single validation case

There is no repo-wide first-party unit test or lint target in the DNG SDK tree. The normal validation path is to run `dng_validate` against one of the sample DNGs:

```sh
dng_sdk_1_7_1/dng_sdk/targets/mac/debug64/dng_validate dng_sdk_1_7_1/sample_files/01_jxl_linear_raw_integer.dng
```

or on Windows:

```bat
dng_sdk_1_7_1\dng_sdk\targets\win\debug64_x64\dng_validate.exe dng_sdk_1_7_1\sample_files\01_jxl_linear_raw_integer.dng
```

Useful single-file diagnostics from the built-in CLI:

```sh
dng_validate -v <file.dng>                                            # verbose tag dump
dng_validate -1 stage1.tif -2 stage2.tif -3 stage3.tif <file.dng>   # dump each pipeline stage
dng_validate -tif rendered.tif <file.dng>                             # render to TIFF
dng_validate -dng rewritten.dng <file.dng>                            # round-trip to DNG
dng_validate -lossyMosaicJXL -dng out.dng <file.dng>                 # re-encode mosaic with lossy JXL
dng_validate -losslessJXL -dng out.dng <file.dng>                    # re-encode with lossless JXL
dng_validate -proxy 2000 -dng proxy.dng <file.dng>                   # create proxy DNG (max long edge 2000 px)
```

Sample files in `dng_sdk_1_7_1/sample_files/` cover JXL linear/Bayer raw, ProGamut tone maps, image sequence info, image stats, and HDR/SDR profiles.

### Vendored XMP build scripts

If work is confined to `dng_sdk_1_7_1/xmp/`, that subtree has its own build system and scripts, separate from `dng_validate`:

```sh
cmake -S dng_sdk_1_7_1/xmp/toolkit/build -B <build-dir>
./dng_sdk_1_7_1/xmp/toolkit/build/build_xmp_linux.sh 64 Release
```

## High-level architecture

This repository is a bundled SDK distribution, not a single application. The code you will most often change lives in `dng_sdk_1_7_1/dng_sdk/source`; `libjpeg/`, `libjxl/`, and `xmp/` are vendored upstream dependencies that are wired into the SDK projects.

`dng_validate.cpp` is the best end-to-end integration map for the SDK. Its flow mirrors the spec exactly:

1. Create a `dng_host` and configure it with preview/save/JXL options.
2. Parse container structure with `dng_info::Parse` / `PostParse` — reads IFDs, discovers main/mask/depth/enhanced/semantic-mask indices.
3. Materialize image + metadata into `dng_negative::Parse` / `PostParse` — reads camera profile tags, opcode lists, linearization/mosaic info.
4. Read pixels into **stage 1** (`ReadStage1Image` / `ReadEnhancedImage`).
5. Build **stage 2** via `BuildStage2Image` — applies OpcodeList1 (on raw values), linearization (LinearizationTable → black subtraction → rescaling to [0,1] → clipping), then OpcodeList2 (on linear reference values).
6. Build **stage 3** via `BuildStage3Image` — demosaics CFA data and applies OpcodeList3 (on demosaiced [0,1] values).
7. Optionally convert to proxy DNG, apply JXL compression, flatten transparency, render previews with `dng_render`, write output with `dng_image_writer`.

Important class boundaries:

- `dng_host` — application-facing control point for memory, abort/progress, preview sizing, save behavior, and JXL encode settings.
- `dng_info` — TIFF/IFD-level parsing, discovers which IFD index is main/mask/depth/enhanced/semantic.
- `dng_negative` — owns stage images, opcodes, camera profiles, metadata synchronization, proxy conversion, and all compression decisions.
- `dng_render` — color-space pipeline: camera → XYZ (D50) → ProPhoto RGB → HSV table → tone curve → output space.
- `dng_image_writer` — writes TIFF, DNG (with preview list), and JPEG-encoded previews.

### DNG file structure (spec summary)

A DNG file is a TIFF 6.0 extension. IFD types are identified by `NewSubFileType`:

| Value | Meaning |
|---|---|
| 0 | Full-resolution raw image (main IFD) |
| 1 | Primary rendered preview / reduced-resolution |
| 0x10001 | Alternate rendered preview |
| 4 / 5 | Transparency mask (full / reduced resolution) |
| 8 / 9 | Depth map (full / reduced resolution) |
| 16 | Enhanced image (demosaiced, LinearRaw color space) |
| 0x10004 | Semantic mask |

Compression codes used in DNG:

| Value | Compression |
|---|---|
| 1 | Uncompressed |
| 7 | Lossless Huffman JPEG (or baseline DCT for 8-bit YCbCr/grayscale) |
| 8 | Deflate/ZIP |
| 34892 | Lossy JPEG (requires 8-bit LinearRaw or PhotometricMask) |
| 52546 | JPEG XL (≥ DNG 1.7.0; 8–16-bit integer or 16-bit float; 1 or 3 planes) |

64-bit DNG (BigTIFF) uses magic byte `43` instead of `42` at file offset 2. Writers should only use 64-bit format when the file would exceed 4 GB.

Opcode lists are always stored in **big-endian byte order** regardless of the file's byte order, so they can be copied between files without byte-swapping.

### Camera color pipeline (spec Ch. 5 & 6)

**Stage 1 → Stage 2 (linearization):**
1. LUT via `LinearizationTable` (optional)
2. Subtract per-pixel black level (`BlackLevel` + `BlackLevelDeltaH` + `BlackLevelDeltaV`)
3. Rescale to [0, 1] using `WhiteLevel`
4. Clip (values above 1.0 clipped; sub-zero values should be preserved through early pipeline stages)

**Stage 2 → rendered output (color transform):**
1. Build `XYZtoCamera = AB × CC × CM` (AnalogBalance × CameraCalibration × ColorMatrix)
2. Resolve white balance: `AsShotNeutral` (camera-space neutral) **or** `AsShotWhiteXY` (xy chromaticity) — these tags are mutually exclusive.
3. If `ForwardMatrix` tags are present (preferred): `CameraToXYZ_D50 = FM × D × Inverse(AB × CC)` where D is diagonal white-balance scale.
4. If not: invert `XYZtoCamera`, apply Bradford chromatic adaptation to D50.
5. Two or three illuminant calibrations are interpolated using **linear interpolation of inverse correlated color temperature** (CCT). Use the third illuminant for the "as shot" condition when it is present (DNG 1.6+).
6. Apply `ProfileGainTableMap2` (if present; supersedes `ProfileGainTableMap`) in RIMM/ProPhoto space, after `BaselineExposure` and post-warp.
7. Apply HSV table (`ProfileHueSatMapData`), tone curve (`ProfileToneCurve`), look table (`ProfileLookTableData`), and `RGBTables` in ProPhoto RGB space.
8. For **HDR profiles** (`ProfileDynamicRange = 1`): apply encoding function `f(x) = x(256+x) / (256(1+x))` before table lookups and the inverse afterwards; do not clip intermediate values to [0, 1].

### Camera profiles

- The primary camera profile lives in IFD 0. Additional profiles are referenced via `ExtraCameraProfiles`.
- `CameraCalibrationSignature` (per-file) and `ProfileCalibrationSignature` (per-profile) must match to use the `CameraCalibration` matrices; otherwise use identity matrices.
- `ProfileGainTableMap2` (DNG 1.7) can be placed in either IFD 0 or a Camera Profile IFD. Precedence: Camera Profile IFD `ProfileGainTableMap2` > IFD 0 `ProfileGainTableMap2` > Raw IFD `ProfileGainTableMap`.
- `ProfileDynamicRange` (DNG 1.7) and `ProfileGroupName` (DNG 1.7) enable SDR/HDR profile pairs selectable based on output display.

### DNGVersion compatibility

Writers must set `DNGBackwardVersion` to the minimum spec version required by the features they use:

| Min version | Feature |
|---|---|
| 1.3.0.0 | Opcode lists present and required |
| 1.4.0.0 | Floating-point data, deflate, lossy JPEG, proxy DNG |
| 1.5.0.0 | Depth maps, enhanced IFD |
| 1.6.0.0 | Semantic masks, third illuminant |
| 1.7.0.0 | JPEG XL compression |
| 1.7.1.0 | `ColumnInterleaveFactor` |

Readers must reject files where `DNGBackwardVersion` exceeds their supported version.

### DNG tags added in 1.7.0 and 1.7.1

| Tag | Code | Added | Notes |
|---|---|---|---|
| `ProfileGainTableMap2` | 52544 | 1.7.0 | Extended gain table (integer/float, gamma param); supersedes `ProfileGainTableMap` when both present |
| `ImageSequenceInfo` | 52548 | 1.7.0 | Sequence ID, type, frame index/count/final flag; for focus stacks, brackets, bursts, etc. |
| `ImageStats` | 52550 | 1.7.0 | Per-IFD pixel statistics (weighted avg, per-plane avg, ordered samples); big-endian child-tag table |
| `ProfileDynamicRange` | 52551 | 1.7.0 | SDR (0) or HDR (1) + `HintMaxOutputValue`; triggers HDR encode/decode around table lookups |
| `ProfileGroupName` | 52552 | 1.7.0 | UTF-8 group name to associate SDR+HDR paired profiles |
| `ColumnInterleaveFactor` | 52547 | 1.7.1 | Interleaved column storage (combine with `RowInterleaveFactor` to split Bayer into 4 monochrome sub-images) |
| `JXLDistance` | 52553 | 1.7.1 | JXL psychovisual distance (0.0 = lossless) |
| `JXLEffort` | 52554 | 1.7.1 | JXL encode effort (1–9) |
| `JXLDecodeSpeed` | 52555 | 1.7.1 | JXL decode speed hint (1–4) |

### Project wiring

- Windows project settings define platform macros like `qWinOS=1`, include paths for `dng_sdk/source`, `libjxl`, `xmp`, and zlib, and compile `libjpeg` sources directly into `dng_validate`.
- The Windows solution also pulls in `libjxl`, `brotli`, and `highway` as sibling projects.
- macOS Xcode settings do the same through `xcconfig` files and link the XMP/JXL static libraries from the bundled dependency trees.

## Key conventions (native SDK)

These apply when editing C++ under `dng_sdk_1_7_1/` (rare) or when porting
behavior into the .NET tree:

- Follow the SDK’s two-phase parse pattern. Many core types require `Parse(...)` immediately followed by `PostParse(...)`; treating `Parse` as complete is usually wrong.
- Treat stage images as a real pipeline with spec-defined semantics: stage 1 is raw/enhanced sensor data, stage 2 is linearized [0,1] values, stage 3 is demosaiced [0,1] values. Opcode lists run at specific points (List1 on raw, List2 on linear, List3 on demosaiced). When debugging, use `dng_validate -1/-2/-3` to inspect each stage.
- Keep host behavior in `dng_host` configuration. In `dng_validate.cpp`, the globals mostly exist to translate CLI flags into `dng_host` settings; new processing behavior should usually be expressed through host/negative APIs, not more CLI-local state.
- Metadata synchronization is explicit. The reference flow calls `negative->SynchronizeMetadata()` before later rendering/writing work; preserve that sequencing when changing metadata-related code paths.
- XMP lifetime is explicit. Entry points that use XMP should mirror `dng_validate` and bracket work with `dng_xmp_sdk::InitializeSDK()` / `TerminateSDK()`.
- Feature selection is macro-driven in `dng_flags.h` plus project settings. `qDNGUseLibJPEG` is enabled by the validate target; XMP (`qDNGUseXMP`) defaults to 1; platform detection relies on build-defined macros such as `qWinOS` / `qMacOS`.
- `AsShotNeutral` and `AsShotWhiteXY` are mutually exclusive per spec — never write both into the same IFD.
- `ProfileGainTableMap2` supersedes `ProfileGainTableMap` when both are present. When adding gain map support, always write the newer tag and keep the old one only for backwards compatibility.
- Opcode list data is big-endian regardless of file byte order — this is why opcode lists can be copied between files without byte-swapping.
- Prefer changing `dng_sdk/source` or the project settings under `dng_sdk/projects` before patching vendored `libjpeg`, `libjxl`, or `xmp`. Those directories are bundled dependencies, not the main customization surface.
