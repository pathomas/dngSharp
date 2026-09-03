# TODO.md — DNG SDK .NET 10 Port

> Generated from session todo DB. Last updated: 2026-08-15.
> See `PORTING_PLAN.md` for phased plan and `STATUS.md` for current build/test state.

---

## ⬜ Pending (ready to start)

### `acr3-default-tone-curve` — Porting native ACR3 default tone curve fallback

Root cause of the left-edge gray band/streak in JPEG renders of `IMG_3356-HDR.dng` (and
likely other DNGs lacking an embedded `ProfileToneCurve`): native `dng_render.cpp` always
falls back to `dng_tone_curve_acr3_default` (a verbatim 256-point S-curve table, see
`dng_sdk_1_7_1/dng_sdk/source/dng_render.cpp` lines 208-484) concatenated with a separate
shadow-recovery exposure ramp (`fShadows = 5.0`, ~line 2169, applied whenever the negative
is NOT output-referred — see `dng_render::Render` around line 2186-2198, which only zeroes
`fShadows`/uses identity for `IsOutputReferred()` negatives). This lifts dim/near-black raw
regions (e.g. Apple ProRAW optical-black/masked edge columns) to blown-out white, blending
with surrounding highlights. Our `Dng.Sdk.Render.HdrToneMapper.SCurve` is a much weaker
hand-authored heuristic and is also incorrectly gated behind `!opts.HdrMode` in
`Program.cs` (`RenderAndSave`, ~line 439) as if it were HDR-only logic, when native applies
its default curve unconditionally for any scene-referred DNG regardless of
`ProfileDynamicRange`.

**Fix plan:**
1. Port the exact 256-entry `kTable` float array from `dng_tone_curve_acr3_default::Evaluate`
   (`dng_render.cpp` lines 211-470) into a new C# class (e.g.
   `Dng.Sdk/Render/Acr3DefaultToneCurve.cs`), replicating the linear-interpolation lookup
   (`Evaluate`) and inverse (`EvaluateInverse`, ~line 488+) methods.
2. Port the shadow-recovery ramp/exposure-ramp concatenation logic (`dng_function_exposure_ramp`
   used in `dng_1d_concatenate totalTone = exposureTone + ToneCurve`, ~line 1058-1061) so
   `shadows=5.0` behavior matches native when the DNG is scene-referred.
3. Replace the `SCurve` fallback usage in `Stage3Renderer.Render`/`Program.cs RenderAndSave`/
   `HdrToneMapper.Apply` with this faithful curve when `result.CameraProfile?.ToneCurve` is
   null, and remove the `!opts.HdrMode` gating.
4. Re-render `IMG_3356-HDR.dng` with `-jpeg` and re-run the column-brightness diff analysis
   against native `dng_validate -tif` output to confirm the left-edge region now renders as
   clipped white (or at least visually matches native).
5. Add/adjust a `Dng.Sdk.Tests` unit test asserting `Acr3DefaultToneCurve.Evaluate` matches
   known native control points, to guard against regression.
6. Consider whether this should also become the default when no camera profile is present
   at all (not just missing `ProfileToneCurve` within a present profile).

---

### Synthetic test-image fixtures (visual-artifact regression suite)

Follow-ons to the gradient (`GradientDngTests`) and circle (`CircleDngTests`) fixtures
already built in `tests/Dng.Sdk.Tests/TestImages/`. Grouped by what they guard against:

**Color**
- `test-primary-color-patches` — Pure primary/secondary color patch test. Grid of solid
  patches: pure R, G, B, C, M, Y, white, black, 18% gray at max/min DNG values with an
  identity `ColorMatrix1`. Detects channel-swap bugs, cross-talk, and incorrect clipping of
  saturated colors.
- `test-per-channel-gradient` — Per-channel-only gradient tests (R-only, G-only, B-only
  ramps). Three DNGs, each with a 0→max ramp in exactly one of R/G/B and the other two
  channels held at 0. Detects per-channel tone-curve/gain bugs and channel-crosstalk.
- `test-color-checker-chart` — Macbeth-style 24-patch color checker DNG. Synthetic DNG
  encoding the 24 standard ColorChecker patch reference values (via a known ColorMatrix),
  rendered and compared against expected sRGB reference values with a delta-E or
  per-channel tolerance. Broad-spectrum color-accuracy regression test.
- `test-white-balance-illuminants` — Multi-illuminant white balance test set. DNGs with
  `AsShotNeutral` set for a few reference illuminants (A/tungsten, D50, D65, fluorescent)
  rendered from a known neutral-gray raw pattern; assert rendered output stays neutral
  (R=G=B). Also add a dual/triple `ColorMatrix` (two/three illuminant calibration) DNG at
  an intermediate CCT to test the inverse-CCT linear interpolation path.

**Tone / dynamic range**
- `test-step-wedge-tone-curve` — Discrete step-wedge (11-step 0-100% gray) test. Flat gray
  patches at 0%,10%,...,100% of max value make exact per-step tone-curve output values easy
  to assert precisely, complementing the continuous gradient test.
- `test-embedded-tone-curve` — Embedded `ProfileToneCurve` DNG test. DNG with a real
  embedded `ProfileToneCurve` tag plus the step-wedge or gradient pattern. Verifies the
  embedded curve is read and applied correctly — currently untested (all existing fixtures
  omit `ProfileToneCurve` deliberately to test the fallback).
- `test-hdr-profile-dynamic-range` — `ProfileDynamicRange=1` (HDR) test image. Gradient/
  step-wedge DNG with values intentionally exceeding 1.0 in linear space, to exercise the
  HDR encode/decode function `f(x)=x(256+x)/(256(1+x))` around table lookups.
- `test-black-white-level-edge-cases` — Non-trivial `BlackLevel`/`WhiteLevel` test. DNG
  with `BlackLevel > 0` and `WhiteLevel < max`, plus `BlackLevelDeltaH/V` per-pixel offsets,
  using a known ramp so the exact linearization math can be asserted precisely.

**Mosaic / compression / opcodes** (larger effort)
- `test-bayer-cfa-pattern` — Synthetic Bayer/CFA mosaic test. A raw CFA
  (`PhotometricInterpretation=CFA`, not `LinearRaw`) DNG with a known per-cell pattern to
  verify demosaic output and correct `CFAPattern`/mosaic-order handling. Exercises the
  demosaic path, which no current LinearRaw fixture touches.
- `test-tile-boundary-gradient-jxl` — JXL-tiled sharp-discontinuity test (reproduces the
  original streak bug directly). A DNG compressed with JPEG XL with tile boundaries at
  known column positions and a sharp step-function discontinuity placed exactly at a tile
  boundary. Requires wiring the JXL encoder path into `SyntheticDngBuilder` (currently only
  uncompressed fixtures exist).
- `test-jxl-float16-decode-precision` — Regression test for the 2026-08-15 JXL/Float16
  destination-buffer precision-loss fix (see Bug fixes table below). A JXL-compressed,
  `BitsPerSample=16`/`SampleFormat=3` (Float16-declared) synthetic DNG with a shallow
  low-slope gradient region, asserting `StripReader.ReadStage1` decodes at `Float32`
  working precision (no visible flat/duplicate-column quantization runs) for
  `compression == Compression.Jxl`. Needs the JXL encoder wired into
  `SyntheticDngBuilder` — can likely share fixture-generation work with
  `test-tile-boundary-gradient-jxl` above.
- `test-vignette-gainmap-opcode` — GainMap/vignette OpcodeList test. Flat-gray DNG with an
  OpcodeList2 GainMap opcode encoding a known radial vignette-correction falloff. Assert the
  corrected output matches the expected radially-varying gain exactly at sampled radii.

---

## ⏸ Blocked

### `golden-stage-diff` — Stage-1/2/3 TIFF pixel diff in xUnit
**Blocked by:** native stage golden needs warp/opcode corrections not yet ported (see
`tests/golden/SKIP.md`).

For samples whose pipeline the managed port covers, diff pixel data against native
`dng_validate -1/-2/-3` stage TIFFs. Bit-exact for integer pipelines; ULP-bounded for float.
Skip and document samples requiring unimplemented opcodes.

---

## ✅ Done

| ID | Title |
|---|---|
| `baseline-build-test` | Verified baseline build + tests pass before Phase 10 |
| `capture-goldens` | Captured native `-v` goldens for all 14 samples |
| `inventory-goldens` | Inventoried golden artifacts per sample |
| `golden-verbose-diff` | `GoldenVerboseDiffTests` — byte order, BigTIFF, IFD 0 shape (14/14) |
| `golden-roundtrip-diff` | `GoldenRoundTripDiffTests` — managed `-dng` self-diff (10/14) |
| `bench-harness` | `Dng.Sdk.Benchmarks` project + BDN switcher |
| `bench-baseline-record` | Baseline recorded in `docs/perf/phase10-baseline.md` (parse-only < 400 μs) |
| `aot-smoke-ci` | AOT publish CI on win-x64 / linux-x64 / osx-arm64 with sample smoke-run |
| `refresh-status-md` | `STATUS.md` and `PORTING_PLAN.md` reconciled |
| `jxl-libjxl-build-ci` | libjxl built + cached in CI; runtime assets placed in `Dng.Sdk.Jxl/runtimes/` |
| `jxl-decode-full` | `JxlDecoder.Decode` — full libjxl state-machine; partial-tile boundary; Float16 |
| `strip-tile-reader` | `StripReader.ReadStage1` — strip + tile layouts; out-of-line offset arrays |
| `linearraw-passthrough` | `Stage3Builder` LinearRaw/RGB passthrough; `CanBuild()` guard |
| `color-pipeline-wire` | `Stage3Renderer` — camera → XYZ_D50 → Bradford CAT → sRGB; baseline exposure |
| `hdr-tonemap` | `HdrToneMapper` — `ProfileToneCurve` preferred; Reinhard fallback |
| `preview-project` | `Dng.Sdk.Preview` (SkiaSharp 4.148) — `JpegEncoder`, `WebPEncoder` (SDR + HDR F16) |
| `cli-jpeg-webp-flags` | `-jpeg` / `-webp` / `-hdr` CLI flags in `Dng.Validate` |
| `milestone-a-status` | STATUS / PORTING_PLAN updated for Milestone A |
| `ifd-metadata-reader` | `LinearizationReader` + `MosaicInfoReader` (parse BlackLevel, WhiteLevel, CFAPattern, …) |
| `linearization-full` | Stage2Builder full linearisation — `BlackLevelRepeatDim` tiling, multi-plane arrays |
| `mosaic-info-usage` | `Stage3Builder.Build(stage2, photometric, mosaicInfo?)` dispatches to demosaic |
| `demosaic-bilinear` | `DemosaicBilinear` — bilinear Bayer (RGGB/GRBG/BGGR/GBRG); full-image source |
| `stage-tif-cli-flags` | `-1/-2/-3 <file.tif>` CLI flags + `StageImageWriter` |
| `bayer-golden-diff` | `GoldenBayerDiffTests` — skips when warp mismatch detected (see `SKIP.md`) |
| `asshot-neutral-cct-solver` | `DngNegative.EstimateAsShotKelvin()` — AsShotNeutral fixed-point CCT (spec 5.4.2) |
| `camera-matrix-full-wiring` | `CameraProfileReader` + `Stage3Renderer.ResolveCameraToXyzD50` + BaselineExposure; full `FM × D × inv(AB × CC)` |
| `profile-tone-curve-wire` | `ProfileToneCurve` read from IFD → `Stage3Renderer.Render()` + `HdrToneMapper.Apply()` |
| `output-color-space-flags` | `OutputColorSpace` enum (sRGB / AdobeRGB / ProPhoto / DisplayP3 / Rec.2020) + `-cs1`…`-cs2020` flags |
| `scurve-tonemap` | `HdrToneMapper.SCurve` — Hill-function S-curve default (shadow lift, mid-tone slope > 1, highlight roll-off), applied in luminance space to preserve hue; `ProfileToneCurve` still takes priority per-channel |
| `simd-kernels` | `Vector<float>` fast paths: `Stage2Builder` linearization multiply-add (single-plane, no LUT/repeat/deltas) and `Stage3Renderer` 3×3 matrix + exposure (no tone curve); scalar fallback retained for all other cases |
| `bench-simd-compare` | `SimdKernelBenchmarks` — SIMD ~2.8× faster (linearization) and ~1.56× faster (matrix transform) vs. scalar fallback; recorded in `docs/perf/phase10-simd.md` |
| `test-checkerboard-tiles` | `CheckerboardDngTests` — 4096×4096, 16px checkerboard; asserts uniform horizontal/vertical transition spacing (no duplicated/missing rows/columns) |
| `test-border-crop-margin` | `BorderCropMarginDngTests` — 2000×2000 raw with centered 1600×1600 `ActiveArea`/`DefaultCropArea`; mid-gray "poison" padding must not leak past crop, 4px border measured at all 4 edges |
| `test-orientation-tags` | `OrientationTagDngTests` — all 9 TIFF `Orientation` values (1-9); locks in current scope (tag not applied — dimensions/pixels identical across all values) |
| `test-odd-dimensions-tiny-image` | `OddDimensionsTinyImageDngTests` — 1x1, 3x3, 4x4, 7x5, 401x299; exact-dimension + monotonic-gradient assertions |

---

## Dependency graph (pending + blocked)

```
golden-stage-diff       ← demosaic-bilinear ✅ + inventory-goldens ✅
                          → unblocked but native golden has warp mismatch; see SKIP.md
```

---

## Bug fixes applied (not tracked as todos)

| Date | Bug | Fix |
|---|---|---|
| 2026-06-30 | `StageImageWriter` threw on Float16 Stage-1 TIFF | Upcasts Float16 → Float32 before writing |
| 2026-06-30 | `StripReader.ReadTiles` — compact vs. strided buffer → black bands | Always decode into compact scratch buffer, copy row-by-row |
| 2026-06-30 | `LinearizationReader` hard-coded `WhiteLevel=1.0` for float images | Always read the actual tag value |
| 2026-06-30 | `Stage3Renderer.ResolveCameraToXyzD50` — missing white balance diagonal | Full `FM × D × inv(AB × CC)` formula applied |
| 2026-08-15 | Left-edge streak/banding in JPEG renders of JXL-compressed, Float16-tagged DNGs (e.g. `IMG_3356-HDR.dng`, Apple ProRAW/HDR). Root cause: `StripReader.ReadStage1` derived the JXL *decode destination buffer* type straight from the file's declared `BitsPerSample=16`/`SampleFormat=3` tags (`PixelType.Float16`), so `JxlDecoder` asked libjxl to emit `Float16` directly — discarding libjxl's full float32 reconstruction precision. The resulting quantization steps (exact Float16 ULPs, verified algebraically against native's float32 Stage-1 output) created multi-pixel flat "stuck" runs wherever local raw signal slope was low (worst at the image's left edge), which gamma/tone-curve encoding then amplified into a visible band. Confirmed via `dng_validate -1`/`-3` numeric diffs: pre-fix, adjacent-column diffs in the rendered JPEG were 100% bit-identical for 8 columns; post-fix, normal per-column variation (mean diff ≈0.22) with no duplication, and Stage-1 values are bit-exact vs. native (0.0 max/mean abs diff). | `StripReader.ReadStage1` now promotes the working pixel type to `Float32` specifically when `compression == Compression.Jxl && pixelType == PixelType.Float16` — JXL is a lossy transform codec whose reconstruction is inherently float32-precision regardless of the file's declared bit depth; `Uncompressed` strips are untouched since their on-disk bytes genuinely are Float16 with no extra precision to recover. |
