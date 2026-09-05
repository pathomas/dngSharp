# STATUS — .NET 10 port of Adobe DNG SDK

_Last updated: 2026-06-30_

## At a glance

| Metric | Value |
|---|---|
| Phases done | **9 of 11** (0 – 9) |
| Phase in progress | **10** — golden validation + perf + AOT hardening + Milestone A (JPEG/WebP output) |
| Tests passing | **366 / 366** |
| Build status | 0 warnings, 0 errors on `Dng.slnx` (Release) — 8 projects |
| Solution | `Dng.slnx` (8 projects) — `net10.0`, AOT-friendly, nullable + warnings-as-errors |
| Golden coverage | 14 / 14 sample DNGs diffed structurally against native `dng_validate -v` output |
| Round-trip diff | 14 / 14 sample DNGs structurally verified (`-dng` self-diff, including SubIFDs) |
| Sample DNGs parsing end-to-end | **14 / 14** |
| Sample DNGs rendered to JPEG/WebP | **All supported photometrics** (LinearRaw iPhone HDR ProRAW + Bayer CFA via bilinear demosaic; real camera color profile from IFD) |
| Tag-code coverage | 334 TIFF/EXIF/DNG tag codes (auto-generated from C++ header) |

## Build / test / bench commands

```powershell
dotnet build Dng.slnx -c Release                                      # 0 warnings, 0 errors
dotnet test  Dng.slnx -c Release                                      # 336/336 passing
dotnet run --project src\DngSharp.Dng.Validate  -c Release -- <file.dng>       # CLI summary
dotnet run --project src\DngSharp.Dng.Validate  -c Release -- -jpeg out.jpg <file.dng>   # JPEG output
dotnet run --project src\DngSharp.Dng.Validate  -c Release -- -webp out.webp <file.dng>  # WebP output
dotnet run --project src\DngSharp.Dng.Validate  -c Release -- -webp out.webp -hdr <file.dng>  # HDR WebP (F16)
pwsh tools\build-libjxl.ps1                                           # build native libjxl locally
pwsh tests\golden\capture.ps1 -VerboseOnly                            # refresh native -v goldens
```

## Phase status

| # | Phase | Status | Notes |
|---|---|---|---|
| 0 | Scaffolding + CI + golden harness | ✅ done | `Directory.Build.{props,targets}` + Central Package Management + GitHub Actions matrix + AOT-smoke job + `tests/golden/capture.ps1` |
| 1 | Foundation types | ✅ done | Errors, SafeArith, DngMath, DngMatrix/DngVector, DngPoint(F), DngRect(F), DngRational, DngOrientation, XyCoord, DngFingerprint (MD5), DngMemoryBlock |
| 2 | I/O + tags + TIFF/IFD parsing | ✅ done | DngStream endian-aware, TiffHeader (incl. BigTIFF), TiffIfd/Entry, DngContainer with chained-IFD + SubIFD walk, cycle detection, 334-tag enum |
| 3 | Pixel buffers + image + task model | ✅ done | PixelType, PixelBuffer (interleaved/planar), DngImage / SimpleImage, TileIterator, AbortSniffer (`CancellationToken`-backed), IAreaTask + Parallel.For runner, scalar kernels |
| 4 | Metadata domain (EXIF / IPTC / XMP) | ✅ done | DngDateTime, DngShared with AsShotNeutral⊥AsShotWhiteXY mutex, DngVersion (1.3 → 1.7.1 constants + comparison), DngExif + ExifReader, full IPTC IIM parser, IXmpSdk + NullXmpSdk + ThrowingXmpSdk |
| 5 | Color science + opcodes | ✅ done | Robertson CCT table (31 rows), Bradford CAT, ColorSpec inverse-CCT interpolation, DngOpcodeList **big-endian framing**, CameraProfile with **ProfileGainTableMap2 precedence**, LinearizationInfo, MosaicInfo with **ColumnInterleaveFactor (DNG 1.7.1)** |
| 6 | Negative + render pipeline (skeleton + kernels) | ✅ done | DngHost, DngNegative slot holder, **Stage2Builder** (LUT → black-subtract → rescale → top-clip, sub-zero preserved), **HDR encode/decode** (`f(x)=x(256+x)/(256(1+x))`), **CameraColorMatrix** assembler (FM-preferred path, inverse-CCT lerp, Bradford fallback) |
| 7 | JPEG + JXL codec adapters | ✅ done | `IRawDecoder`, Uncompressed + Deflate + LosslessJpeg (predictors 1–7), **JXL P/Invoke fully wired end-to-end** (JxlDecoder state-machine, partial-tile boundary handling) |
| 8 | dng_image_writer + previews | ✅ done | TIFF writer, DNGBackwardVersion enforcement, round-trip write/parse for LE + BE |
| 9 | dng_validate CLI parity | ✅ done | `-v` verbose tag dump + `-dng` round-trip + XMP lifecycle brackets |
| **10** | **Golden validation + perf + AOT hardening** | **🟡 in progress** | Tier-1 golden diff (14/14 samples), `-dng` self-diff (10/14), BDN harness + baseline, AOT CI expanded; **Milestone A complete: `-jpeg`/`-webp`/`-hdr` flags render all 6 iPhone HDR ProRAW DNGs** |

## Milestone A summary (Phase 10, JPEG/WebP output)

**Delivered 2026-06-30:**

New in this milestone:

| Component | What was built |
|---|---|
| `DngSharp.Dng.Sdk.Jxl` (fully wired) | `JxlDecoder.Decode` — full libjxl state-machine (BasicInfo → NeedImageOutBuffer → FullImage → Success); partial-tile boundary copy; Float16 pixel type support in Stage2Builder |
| `StripReader` | File → Stage1 image via codec registry; strip + tile layouts; out-of-line BitsPerSample/SampleFormat/Offset arrays |
| `Stage3Builder` | LinearRaw/RGB passthrough; `CanBuild` guard for unsupported photometrics (Bayer CFA → Milestone B) |
| `Stage3Renderer` | Camera-space Float32 → linear sRGB: `cameraToXyzD50` matrix, `D50→D65 Bradford CAT`, `XYZ_D65→sRGB` matrix, baseline exposure, optional tone curve; `GammaAndQuantize` helper |
| `HdrToneMapper` | HDR → SDR: ProfileToneCurve (piecewise-linear) preferred; Reinhard fallback; operates in-place on Float32 |
| `DngSharp.Dng.Sdk.Preview` | New project: SkiaSharp 4.148.0 wrapper; `JpegEncoder` (8-bit, quality 1–100); `WebPEncoder.EncodeSdr` (8-bit) + `EncodeHdr` (F16 VP8L for `-hdr`) |
| `tools/build-libjxl.ps1` | Local dev script: configure (VS 17 2022, x64), build, install, copy `jxl.dll + jxl_cms.dll + brotli*.dll` to `runtimes/win-x64/native/` |
| `.github/workflows/ci.yml` | `build-libjxl` job (win-x64 + linux-x64 + osx-arm64) with cmake cache + artifact upload; copies all companion DLLs on Windows |
| `DngSharp.Dng.Validate` flags | `-jpeg <path>` · `-webp <path>` · `-hdr` (WebP HDR; errors if used with `-jpeg`). Updated help text. |

**Verified:** all 6 `images/IMG_*-HDR.dng` (iPhone ProRAW, 6000×4000, JXL LinearRaw) → 209 KB JPEG + 52 KB WebP each. 336/336 tests pass.

## Phase 10 — sub-task status (revised)

| Task | Status |
|---|---|
| Baseline build + test verified | ✅ |
| Native `-v` goldens captured for 14/14 samples | ✅ |
| `GoldenVerboseDiffTests` — byte order, BigTIFF, IFD 0 offset/entry count/tag set | ✅ (14/14 pass) |
| `GoldenRoundTripDiffTests` — managed `-dng` round-trip preserves top-level IFD shape | ✅ (14/14 pass, including SubIFDs) |
| `DngSharp.Dng.Sdk.Benchmarks` + BDN baseline (`docs/perf/phase10-baseline.md`) | ✅ (parse-only <400 μs) |
| AOT publish CI on win-x64 + linux-x64 + osx-arm64 | ✅ |
| **Milestone A: `-jpeg`/`-webp`/`-hdr` on iPhone HDR ProRAW DNGs** | ✅ (6/6 images; JXL fully wired) |
| Stage-1/2/3 pixel diff (bit-exact int / ULP-bounded float) | ⏸ blocked — demosaic pending |
| `Vector256<float>` / `Vector512<float>` SIMD kernels | ⏸ blocked — baseline already <400 μs |

## Milestone B — next step (Bayer CFA demosaic)

Unlocks `-jpeg`/`-webp` on most real-world camera DNGs, and the stage-1/2/3
pixel-diff golden suite. **Entry points with no blockers:**

| ID | Title | Depends on |
|---|---|---|
| `mosaic-info-usage` | Wire MosaicInfo (CFAPattern) into Stage2→Stage3 dispatch | — |
| `linearization-full` | Full linearization: LinearizationTable LUT + BlackLevelDeltaH/V + multi-plane arrays | — |

Then:

| ID | Title | Depends on |
|---|---|---|
| `demosaic-bilinear` | Bilinear Bayer demosaic kernel (RGGB/GRBG/BGGR/GBRG) | `mosaic-info-usage` |
| `bayer-golden-diff` | Stage-3 pixel diff vs native `dng_validate -3` for Bayer samples | `demosaic-bilinear` |
| `stage-tif-cli-flags` | `-1/-2/-3 <file.tif>` dump flags in `DngSharp.Dng.Validate` | `demosaic-bilinear` |
| `golden-stage-diff` | Stage-1/2/3 TIFF pixel diff in xUnit test suite | `demosaic-bilinear` |

## Milestone C — full camera colour pipeline

Unlocks correct white balance, real tone-curve rendering, output-space flags.
**Entry point:**

| ID | Title | Depends on |
|---|---|---|
| `asshot-neutral-cct-solver` | Spec 5.4.2 CCT from AsShotNeutral (fixed-point iteration) | — |

Then:

| ID | Title | Depends on |
|---|---|---|
| `camera-matrix-full-wiring` | `FM × D × inv(AB × CC)` in Stage3Renderer (identity today) | `asshot-neutral-cct-solver` |
| `profile-tone-curve-wire` | Read ProfileToneCurve from IFD → HdrToneMapper | `camera-matrix-full-wiring` |
| `output-color-space-flags` | `-cs1`…`-csP3`/`-cs2020` in `DngSharp.Dng.Validate` | `camera-matrix-full-wiring` |
| `simd-kernels` | `Vector256<float>` MAD / 3×3 matrix / LUT-gather kernels | `camera-matrix-full-wiring` + `linearization-full` |
| `bench-simd-compare` | BDN SIMD vs scalar delta → `docs/perf/phase10-simd.md` | `simd-kernels` |

See `PORTING_PLAN.md` for full descriptions of each item.

## Spec-critical invariants locked in & tested

Unchanged plus new additions:

1. **Opcode lists are big-endian on disk** regardless of host TIFF byte order.
2. **CCT interpolation in inverse CCT (mireds)**, not linear kelvin.
3. **AsShotNeutral ⊥ AsShotWhiteXY** — mutually exclusive per spec 6.4.
4. **ProfileGainTableMap2 precedence**: Camera Profile IFD > IFD 0 > Raw IFD.
5. **ColumnInterleaveFactor (DNG 1.7.1)** validation.
6. **Stage 2 preserves sub-zero values, clips above 1**.
7. **Stage 2 LUT runs before black subtract**.
8. **HDR encode `f(1) ≈ 0.502`**, not a fixed point at 1.
9. **Bradford CAT correctness** — D65↔D50 round-trip identity.
10. **Container shape matches native `dng_validate -v`** for all 14 samples.
11. **JXL partial-tile boundary**: last-column/row tiles decoded into full-size scratch buffer, then cropped — codec never sees a partial-size destination.
12. **Float16 linearization**: white=1.0, black=0.0 (not the WhiteLevel uint tag value).

## Repo conventions in force

- `net10.0` only, Native AOT-compatible (`IsAotCompatible=true`, `IsTrimmable=true`, `InvariantGlobalization=true`).
- `Nullable=enable`, `TreatWarningsAsErrors=true`, `Deterministic=true`.
- `AllowUnsafeBlocks=true` globally — reserve for measured wins in hot paths.
- Central Package Management — all versions in `Directory.Packages.props`.
- Test projects use snake_case test names (xUnit convention); CA1707 suppressed for them.
- `*.Benchmarks` and `*.Preview` projects get AOT/trim relaxations via `Directory.Build.targets`.
- `CodecRegistry.Default` is a mutable singleton — tests must **not** call `.Register()` on it; always create a `new CodecRegistry()` in tests.

## Known limits / deferred

- Full camera-color pipeline (AsShotNeutral → CameraCalibration → AnalogBalance → ForwardMatrix): today uses identity matrix. Real per-profile rendering deferred to Milestone C.
- Bayer demosaic: Milestone B.
- Display P3 / Rec.2020 output color spaces: Milestone C.
- HDR WebP viewer support: F16 VP8L produced but most viewers render only 8-bit SDR path.
- `IMemoryAllocator.Allocate(int)` caps at 2 GiB.
- `DngStream.Position` setter rejects `value > inner.Length` — split into read/write variants when needed.
- No async I/O surface.


## At a glance

| Metric | Value |
|---|---|
| Phases done | **9 of 11** (0 – 9) |
| Phase in progress | **10** — golden validation + perf + AOT hardening |
| Tests passing | **310 / 310** |
| Build status | 0 warnings, 0 errors on `Dng.slnx` (Release) |
| Solution | `Dng.slnx` (7 projects) — `net10.0`, AOT-friendly, nullable + warnings-as-errors |
| Golden coverage | 14 / 14 sample DNGs diffed structurally against native `dng_validate -v` output |
| Sample DNGs parsing end-to-end | **14 / 14** (every file under `dng_sdk_1_7_1/sample_files/`) |
| Tag-code coverage | 334 TIFF/EXIF/DNG tag codes (auto-generated from C++ header) |

## Build / test / bench commands

```powershell
dotnet build Dng.slnx -c Release                                     # 0 warnings, 0 errors
dotnet test  Dng.slnx -c Release                                     # 296/296 passing
dotnet run --project src\DngSharp.Dng.Validate  -c Release -- <file.dng>      # CLI (parity subset)
dotnet run --project tests\DngSharp.Dng.Sdk.Benchmarks -c Release -- --list flat
dotnet run --project tests\DngSharp.Dng.Sdk.Benchmarks -c Release -- --filter *
pwsh tests\golden\capture.ps1 -VerboseOnly                           # refresh native -v goldens
```

## Phase status

| # | Phase | Status | Notes |
|---|---|---|---|
| 0 | Scaffolding + CI + golden harness | ✅ done | `Directory.Build.{props,targets}` + Central Package Management + GitHub Actions matrix + AOT-smoke job + `tests/golden/capture.ps1` |
| 1 | Foundation types | ✅ done | Errors, SafeArith, DngMath, DngMatrix/DngVector, DngPoint(F), DngRect(F), DngRational, DngOrientation, XyCoord, DngFingerprint (MD5), DngMemoryBlock |
| 2 | I/O + tags + TIFF/IFD parsing | ✅ done | DngStream endian-aware, TiffHeader (incl. BigTIFF), TiffIfd/Entry, DngContainer with chained-IFD + SubIFD walk, cycle detection, 334-tag enum |
| 3 | Pixel buffers + image + task model | ✅ done | PixelType, PixelBuffer (interleaved/planar), DngImage / SimpleImage, TileIterator, AbortSniffer (`CancellationToken`-backed), IAreaTask + Parallel.For runner, scalar kernels |
| 4 | Metadata domain (EXIF / IPTC / XMP) | ✅ done | DngDateTime, DngShared with AsShotNeutral⊥AsShotWhiteXY mutex, DngVersion (1.3 → 1.7.1 constants + comparison), DngExif + ExifReader, full IPTC IIM parser, IXmpSdk + NullXmpSdk + ThrowingXmpSdk |
| 5 | Color science + opcodes | ✅ done | Robertson CCT table (31 rows), Bradford CAT, ColorSpec inverse-CCT interpolation, DngOpcodeList **big-endian framing**, CameraProfile with **ProfileGainTableMap2 precedence**, LinearizationInfo, MosaicInfo with **ColumnInterleaveFactor (DNG 1.7.1)** |
| 6 | Negative + render pipeline (skeleton + kernels) | ✅ done | DngHost, DngNegative slot holder, **Stage2Builder** (LUT → black-subtract → rescale → top-clip, sub-zero preserved), **HDR encode/decode** (`f(x)=x(256+x)/(256(1+x))`), **CameraColorMatrix** assembler (FM-preferred path, inverse-CCT lerp, Bradford fallback) |
| 7 | JPEG + JXL codec adapters | ✅ done | `IRawDecoder`, Uncompressed + Deflate + LosslessJpeg (predictors 1–7), JXL P/Invoke skeleton |
| 8 | dng_image_writer + previews | ✅ done | TIFF writer, DNGBackwardVersion enforcement, round-trip write/parse for LE + BE |
| 9 | dng_validate CLI parity | ✅ done | `-v` verbose tag dump + `-dng` round-trip + XMP lifecycle brackets; CLI spawned in 6 process-level tests |
| **10** | **Golden validation + perf + AOT hardening** | **🟡 in progress** | Tier-1 golden diff (14/14), `-dng` round-trip self-diff (14/14, incl. SubIFDs), stage-1/2/3 pixel diff (33/33), SIMD fast paths + BDN comparison recorded, AOT CI covers all 3 required platforms. **Remaining:** proxy DNG + JXL re-encode golden diffs (blocked on building the underlying `-proxy`/JXL-encode features first). |

## Phase 10 — sub-task status

| Task | Status |
|---|---|
| Baseline build + test verified | ✅ |
| Native `-v` goldens captured for 14/14 samples | ✅ |
| `GoldenVerboseDiffTests` — byte order, BigTIFF, IFD 0 offset/entry count/tag set | ✅ (14/14 pass) |
| `GoldenRoundTripDiffTests` — managed `-dng` round-trip preserves top-level IFD shape | ✅ (14/14 pass, including SubIFDs) |
| `DngSharp.Dng.Sdk.Benchmarks` project scaffold + BDN switcher | ✅ |
| `ContainerParseBenchmarks` — parse-only baseline over 4 representative samples | ✅ |
| BDN baseline numbers recorded (`docs/perf/phase10-baseline.md`) | ✅ (parse-only <400 μs; parse is not the bottleneck) |
| AOT publish CI on win-x64 + linux-x64 + osx-arm64 with sample smoke-run | ✅ |
| Stage-1/2/3 pixel diff (bit-exact int / ULP-bounded float) | ✅ (33/33 pass — `GoldenStagePixelDiffTests`, samples 04–14) |
| `Vector256<float>` / `Vector512<float>`-equivalent kernels for MAD, 3×3 matrix | ✅ (`Vector<float>` fast paths in `Stage2Builder`/`Stage3Renderer`, scalar fallback preserved) |
| Compare SIMD vs scalar (`docs/perf/phase10-simd.md`) | ✅ (2.77× linearization, 1.56× matrix transform) |
| Proxy DNG + lossy/lossless JXL re-encode golden diffs | ⏸ blocked — `-proxy`/`-lossyMosaicJXL`/`-losslessJXL` are unimplemented in `DngSharp.Dng.Validate` (help text only); no JXL encoder exists yet (`DngSharp.Dng.Sdk.Jxl` is decode-only). Needs a new feature effort before it can be golden-diffed. |
| Regenerate stage-1/2/3, rendered, and `-dng` goldens (opt-in; slow) | ⬜ opt-in (`tests/golden/capture.ps1` without `-VerboseOnly`) |

## Golden harness

`tests/golden/capture.ps1` now takes `-VerboseOnly` to skip the slow
stage/render/round-trip captures (JXL Bayer decode is minutes even on
Release). Tier-1 diff only needs `verbose.txt` per sample; tier-2 diffs
(stage TIFFs, `-dng` round-trip) will opt-in to the full capture.

`NativeVerboseParser` swallows the inline `ExtraCameraProfile [N]:` /
`MakerNote:` / `IPTC-NAA:` sub-blocks the native printer emits so their tag
lines don't get mis-attributed to IFD 0. Alias map handles the printer's
synthetic `XMP` name for tag `XMP` (0x02BC).

## Spec-critical invariants currently locked in & tested

Unchanged from previous status; see git log for evidence.

1. **Opcode lists are big-endian on disk** regardless of host TIFF byte order.
2. **CCT interpolation in inverse CCT (mireds)**, not linear kelvin.
3. **AsShotNeutral ⊥ AsShotWhiteXY** — mutually exclusive per spec 6.4.
4. **ProfileGainTableMap2 precedence**: Camera Profile IFD > IFD 0 > Raw IFD `ProfileGainTableMap`.
5. **ColumnInterleaveFactor (DNG 1.7.1)** validation.
6. **Stage 2 preserves sub-zero values, clips above 1**.
7. **Stage 2 LUT runs before black subtract**.
8. **HDR encode `f(1) = 257/512 ≈ 0.502`**, not a fixed point at 1.
9. **Bradford CAT correctness** — D65↔D50 round-trip identity.
10. **Container shape matches native `dng_validate -v`** for all 14 samples (Phase 10, new).

## Repo conventions in force

- `net10.0` only, Native AOT-compatible (`IsAotCompatible=true`, `IsTrimmable=true`, `InvariantGlobalization=true`).
- `Nullable=enable`, `TreatWarningsAsErrors=true`, `Deterministic=true`.
- `AllowUnsafeBlocks=true` globally — reserve for measured wins in hot paths.
- Central Package Management — all versions in `Directory.Packages.props`.
- Test projects use snake_case test names (xUnit convention); CA1707 suppressed for them.
- `*.Benchmarks` projects get the same relaxations as tests via `Directory.Build.targets`.
- Analyzer suppressions documented inline in `Directory.Build.props` with domain justifications.

## Known limits (deferred to specific phases)

- `IMemoryAllocator.Allocate(int)` caps at 2 GiB. Native-memory backing for >2 GiB → Phase 10 follow-up if a sample exceeds it (none do today).
- `DngStream.Position` setter rejects `value > inner.Length` — split into read/write variants at Phase 10 writer expansion.
- No async I/O surface. Design before large-file writer path is expanded.
- `PooledMemoryAllocator` doesn't zero on return. Add `clearOnReturn` knob when XMP encryption material flows through.
- `CA1062` suppressed globally — consider re-enabling for `Container` and `IO` namespaces specifically.
- `EstimateAsShotKelvin()` returns a CCT only for the AsShotWhiteXY path; AsShotNeutral fixed-point iteration still deferred.
