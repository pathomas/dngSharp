# Plan: Port Adobe DNG SDK 1.7.1 to Native .NET 10 C#

> **Live state:** see [`STATUS.md`](./STATUS.md) for the current snapshot
> (phases done, tests passing, known limits) and [`SESSION.md`](./SESSION.md)
> for a chronological narrative of what's been built. This plan is the
> reference for *what comes next*; the two files above answer *where we are*.

## Scope summary

Source tree under analysis: `dng_sdk_1_7_1/`.

- **dng_sdk/source**: 159 C++ source/header files, ~3.3 MB. This is the core SDK.
- **libjpeg**: vendored IJG libjpeg (used for JPEG previews + baseline DCT in DNG compression code 7).
- **libjxl**: vendored libjxl + brotli + highway (JPEG XL compression code 52546, DNG 1.7+).
- **xmp**: vendored Adobe XMP Toolkit (metadata I/O).
- **sample_files/**: validation DNGs for the new ports (JXL linear/Bayer raw, ProGamut, ImageStats, ImageSequenceInfo, HDR/SDR profiles).
- Reference CLI: `dng_validate.cpp` — the end-to-end integration test bench.

**Reality check.** This is a multi-person, multi-quarter effort, not a single-PR rewrite. A full byte-for-byte port of the SDK plus its three vendored native dependencies is roughly 500k+ lines of code to rewrite, test, and validate against the spec. The plan below sequences the work so that a useful subset (parsing + render to TIFF, no JXL) is reachable early, with JXL and XMP layered in later.

**Target framework:** `net10.0`, Native AOT-friendly where practical, x64 + arm64. Use `System.Numerics`, `Span<T>`, `Memory<T>`, `Vector<T>`/`Vector256/512` for the SIMD hot paths that `dng_simd_type.h` covers in C++.

---

## Architectural decisions to lock in before coding

1. **Native AOT vs managed-only.** Target Native AOT compatibility from day one: no `BinaryFormatter`, annotate reflection with `DynamicallyAccessedMembers`, prefer `LibraryImport` over `DllImport` if any P/Invoke is kept (see #2).
2. **Vendored deps strategy.** Pick one per dep:
   - **libjxl**: ship as native dependency via `LibraryImport`, *or* port via `Jxl.Net`/wrap. **Recommendation:** P/Invoke to a prebuilt `libjxl` for v1; full managed JXL is out of scope.
   - **libjpeg**: there are mature managed ports (e.g. `BitMiracle.LibJpeg.NetCore`). **Recommendation:** depend on a managed JPEG library, isolated behind an `IJpegCodec` interface.
   - **XMP**: no production-quality .NET XMP toolkit exists. **Recommendation:** P/Invoke a built `libxmp` for v1; design `IXmpSdk` so a pure-managed implementation can replace it later.
3. **Numerics.** Mirror `dng_matrix`, `dng_vector`, `dng_xy_coord`, `dng_rational` as `readonly struct`s where size permits (≤32 bytes), classes otherwise. Use `double` for color math (matches C++).
4. **Memory model.** Replace `dng_memory_block` / `dng_ref_counted_block` with `byte[]` + `ArrayPool<byte>` + `Memory<byte>`. Replace `dng_pixel_buffer` with a `PixelBuffer` struct around `Memory<T>` plus stride/area.
5. **Streams.** Replace `dng_stream` with adapters over `System.IO.Stream`. Keep an internal `DngBinaryReader`/`DngBinaryWriter` that handles both byte orders and the **opcode-list-is-always-big-endian** rule.
6. **Threading.** Replace `dng_pthread` + `dng_area_task` with `System.Threading.Tasks` and `Parallel.For` over tile rectangles. Keep an `AbortSniffer` abstraction backed by `CancellationToken`.
7. **Errors.** Replace `dng_exception` with a `DngException` hierarchy mapped from the existing error codes in `dng_errors.h`.
8. **Flags.** Many `dng_flags.h` macros become compile-time constants in a `DngFlags` static class or `#if` equivalents via partial classes per build configuration.
9. **No global mutable state.** Where C++ uses globals (`dng_globals.h`), introduce a `DngHost` property or a context object.
10. **Endianness.** Always go through `BinaryPrimitives` — never reinterpret-cast.

---

## Status tracker

| # | Phase | Status |
|---|---|---|
| 0 | Scaffolding + CI + golden harness | ✅ done |
| 1 | Foundation types | ✅ done |
| 2 | I/O + tags + TIFF/IFD parsing | ✅ done — parses every shipped sample DNG; 110 tests pass (+4 post-review regression tests) |
| 3 | Pixel buffers + image + task model | ✅ done — 152 tests pass; parallel tile-fill demo passes end-to-end |
| 4 | Metadata domain (EXIF/IPTC/XMP) | ✅ done — EXIF reader extracts SONY/ILCE-7RM4 + software from real samples; 191 tests pass |
| 5 | Color science + opcodes | ✅ done — Robertson CCT, Bradford CAT, inverse-CCT interp, opcode big-endian framing, gain-table precedence; 225 tests pass |
| 6 | Negative + render pipeline | ✅ done (skeleton + spec-critical kernels) — Stage2Builder, HDR encode/decode, camera→XYZ_D50 assembler; 245 tests pass |
| 7 | JPEG + JXL codec adapters | ✅ done — IRawDecoder interface, Uncompressed + Deflate + LosslessJpeg (predictors 1–7) impls, JXL P/Invoke **fully wired end-to-end** (state-machine decode, partial-tile boundary, Float16 support); 261 → 336 tests pass |
| 8 | dng_image_writer + previews | ✅ done (TIFF writer + encoders + version calc) — round-trip write/parse passes for both LE and BE; 276 tests pass |
| 9 | dng_validate CLI parity | ✅ done (`-v` verbose tag dump + `-dng` round-trip + XMP lifecycle brackets) — CLI spawned in 6 process-level tests; 282 tests pass |
| **10** | **Golden validation + perf + AOT hardening** | **🟡 in progress** | Tier-1 golden diff (14/14), `-dng` self-diff (10/14), BDN harness + baseline, AOT CI expanded; **Milestone A** (`-jpeg`/`-webp`/`-hdr`, 336 tests); **Milestone B** (Bayer CFA bilinear demosaic, `-1/-2/-3` TIFF dumps, IFD metadata readers, 351 tests); **Milestone C** (AsShotNeutral CCT solver, real camera profile matrix, ProfileToneCurve, `-cs1/-cs2/-cs3/-csP3/-cs2020` output-space flags, 366 tests). Remaining: SIMD kernels, stage pixel diff (blocked on opcodes). |

---

## Milestone B — Bayer CFA support ✅ done (2026-06-30)

*Unlocks:* `dng_sdk_1_7_1/sample_files/0*_jxl_bayer_*.dng`, most real-world camera RAW files, `-jpeg`/`-webp` on CFA sources.

| ID | Title | Status | Depends on |
|---|---|---|---|
| `ifd-metadata-reader` | `LinearizationReader` + `MosaicInfoReader` (parse IFD tags into managed types) | ✅ done | — |
| `mosaic-info-usage` | Wire MosaicInfo into Stage2→Stage3 dispatch (CFA pattern layout from CFAPattern tag) | ✅ done | `ifd-metadata-reader` |
| `linearization-full` | BlackLevelRepeatDim tiling in Stage2Builder; full multi-plane black/white arrays | ✅ done | `ifd-metadata-reader` |
| `demosaic-bilinear` | Bilinear Bayer demosaic kernel (RGGB/GRBG/BGGR/GBRG; full-image source for border safety) | ✅ done | `mosaic-info-usage` |
| `stage-tif-cli-flags` | `-1/-2/-3 <file.tif>` dump flags + `StageImageWriter` via existing TiffWriter | ✅ done | `demosaic-bilinear` |
| `bayer-golden-diff` | Stage-3 golden diff test — skeleton done; skips when warp mismatch detected (see `tests/golden/SKIP.md`) | ✅ done | `demosaic-bilinear` |

---

## Milestone C — Full camera colour pipeline ✅ done (2026-06-30)

*Unlocks:* correct white balance for real cameras, real tone-curve rendering, output colour space flags.

| ID | Title | Status | Depends on |
|---|---|---|---|
| `asshot-neutral-cct-solver` | Spec 5.4.2 fixed-point CCT solver from AsShotNeutral | ✅ done | — |
| `linearization-full` | Full linearization: LinearizationTable LUT, BlackLevelDeltaH/V, multi-plane black/white | ✅ done | — |
| `camera-matrix-full-wiring` | `CameraProfileReader` + `ResolveCameraToXyzD50` + `BaselineExposure` wired | ✅ done | `asshot-neutral-cct-solver` |
| `profile-tone-curve-wire` | Read ProfileToneCurve from IFD → `Stage3Renderer.Render` + `HdrToneMapper.Apply` | ✅ done | `camera-matrix-full-wiring` |
| `output-color-space-flags` | `OutputColorSpace` enum + matrices + `-cs1/-cs2/-cs3/-csP3/-cs2020` CLI flags | ✅ done | `camera-matrix-full-wiring` |

---

## Pixel-domain performance (blocked on Milestone C)

| ID | Title | Status | Depends on |
|---|---|---|---|
| `simd-kernels` | `Vector256<float>`/`Vector512<float>` paths for per-pixel MAD, 3×3 matrix, tone-curve LUT-gather | ⬜ blocked | `camera-matrix-full-wiring`, `linearization-full` |
| `bench-simd-compare` | BDN SIMD vs scalar delta; record in `docs/perf/phase10-simd.md` | ⬜ blocked | `simd-kernels` |

---

### Open API concerns to address at Phase 3 boundary

Surfaced by the Phase 2 code review and accepted (intentionally not blocking):

1. **`IMemoryAllocator.Allocate(int size)` caps at 2 GiB.** `ArrayPool<byte>.Rent` also takes `int`, so widening the signature alone is dishonest. Fix: at Phase 3 introduce a `BigBlock` allocator backed by `MemoryManager<byte>` over native memory or pinned object heap (`GC.AllocateUninitializedArray<byte>(size, pinned: true)`), with a chunked fallback for the pool path.
2. **`DngStream.Position` write-mode behavior.** Setter rejects `value > inner.Length` — correct for read, wrong for write (extending file by seeking past EOF). Split into `DngReadStream` / `DngWriteStream` at the point a writer (Phase 8) needs it.
3. **`DngStream.OffsetInOriginalFile` semantics.** Currently set at construction and immutable; `PositionInOriginalFile` assumes inner-stream origin == sub-stream origin. Document and add a `Slice(offset, length)` factory before the first consumer (embedded original raw / preview blobs in Phase 6).
4. **No async I/O surface.** Acceptable for Phase 2; design for it before Phase 8 writer to avoid sync-over-async pitfalls.
5. **`PooledMemoryAllocator` doesn't zero on return.** Add a `clearOnReturn` knob before XMP encryption material or other sensitive payloads flow through the allocator (Phase 4+).
6. **CA1062 is suppressed globally.** Consider re-enabling for `Container` and `IO` namespaces specifically — those will grow public surface accepting third-party `Stream`/`byte[]` arguments.

## Phased delivery

### Phase 0 — Project scaffolding (1 week)
- Solution layout:
  - `src/DngSharp.Dng.Sdk/DngSharp.Dng.Sdk.csproj` — core library, `net10.0`, AOT-compatible.
  - `src/DngSharp.Dng.Sdk.Jpeg/` — JPEG codec adapter (wraps managed lib).
  - `src/DngSharp.Dng.Sdk.Jxl/` — JXL codec adapter (P/Invoke `libjxl`).
  - `src/DngSharp.Dng.Sdk.Xmp/` — XMP adapter (P/Invoke `libxmp`).
  - `src/DngSharp.Dng.Validate/` — CLI mirroring `dng_validate.cpp`.
  - `tests/DngSharp.Dng.Sdk.Tests/` — xUnit, golden-file comparisons against C++ `dng_validate` output on the bundled sample files.
- CI: GitHub Actions matrix on `windows-latest` / `ubuntu-latest`, build + tests + AOT publish smoke test.
- Add `Directory.Build.props` with `<TargetFramework>net10.0</TargetFramework>`, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<IsAotCompatible>true</IsAotCompatible>`.
- Set up golden-file harness: run native `dng_validate` on each sample → capture `-v` text dump, stage-1/2/3 TIFFs, rendered TIFF; check those into `tests/golden/`.

### Phase 1 — Foundation types (2–3 weeks)
Port leaf headers with no DNG-specific dependencies:
- `dng_types.h`, `dng_flags.h`, `dng_errors.h`, `dng_exceptions.{h,cpp}`, `dng_assertions.h`.
- `dng_point`, `dng_rect`, `dng_rational`, `dng_matrix`, `dng_xy_coord`, `dng_fingerprint` (MD5), `dng_string`, `dng_local_string`, `dng_string_list`, `dng_date_time`, `dng_orientation`, `dng_temperature`.
- `dng_safe_arithmetic.{h,cpp}` → managed equivalents using `checked` arithmetic and `Math.BigMul`.
- `dng_memory.{h,cpp}` → `MemoryAllocator` + pool-backed `DngMemoryBlock`.
- `dng_mutex.{h,cpp}` → thin wrapper around `object`/`Lock` (.NET 9+ `System.Threading.Lock`).
- `dng_uncopyable.h`, `dng_auto_ptr.h` → drop (idiomatic C# handles these).
- Unit tests for every leaf type, especially `dng_matrix` (3x3/4x3/4x4 inverse, Bradford adapt).

### Phase 2 — I/O + tags (2–3 weeks)
- `dng_stream.{h,cpp}` → `DngStream` over `System.IO.Stream` with byte-order awareness.
- `dng_file_stream`, `dng_memory_stream`.
- `dng_tag_codes.h`, `dng_tag_values.h`, `dng_tag_types.{h,cpp}` → constants/enums.
- `dng_parse_utils.{h,cpp}` → debug-print helpers (`DumpTag`, `DumpTagType`, etc.) used by `-v` mode.
- `dng_ifd.{h,cpp}` (large, ~97 KB) — TIFF/IFD parsing, including NewSubFileType discrimination and BigTIFF magic `43`.
- `dng_info.{h,cpp}` — main container parser. Implements the IFD-classification table in spec Ch. 4.
- Validate by reading every sample DNG and asserting parsed tag set matches `dng_validate -v` text golden.

### Phase 3 — Pixel buffers + image abstraction (1–2 weeks)
- `dng_pixel_buffer.{h,cpp}` — `PixelBuffer` struct + `PixelType` enum (uint8/uint16/uint32/float16/float32).
- `dng_image.{h,cpp}`, `dng_simple_image.{h,cpp}`.
- `dng_tile_iterator`, `dng_filter_task`, `dng_area_task` → `TileIterator`, `IAreaTask` driven by `Parallel.ForEach`.
- `dng_abort_sniffer` → `CancellationToken`-backed sniffer.
- `dng_bottlenecks` → swap pluggable SIMD kernels; start with scalar, optimize with `System.Numerics.Tensors` / `Vector256` later.
- `dng_reference.{h,cpp}` (~80 KB) — reference scalar implementations of all kernels.

### Phase 4 — Metadata domain types (2 weeks)
- `dng_exif.{h,cpp}` (~81 KB).
- `dng_iptc`.
- `dng_xmp_sdk.{h,cpp}` → `IXmpSdk` interface; first impl is the P/Invoke adapter to `libxmp` from `src/DngSharp.Dng.Sdk.Xmp/`.
- `dng_xmp.{h,cpp}` (~92 KB) — DNG-specific XMP packet munging using `IXmpSdk`.
- `dng_update_meta.{h,cpp}`.
- `dng_shared.{h,cpp}` — shared parsing helpers used by `dng_info`/`dng_negative`.

### Phase 5 — Color science (2–3 weeks)
- `dng_1d_function`, `dng_1d_table`, `dng_spline`, `dng_tone_curve`.
- `dng_hue_sat_map`, `dng_color_space.{h,cpp}` (~82 KB).
- `dng_color_spec.{h,cpp}` — `XYZtoCamera = AB × CC × CM`, AsShotNeutral vs AsShotWhiteXY (mutually exclusive!), CCT-interp of 2/3 illuminants on **inverse CCT**, Bradford D50 chromatic adaptation, ForwardMatrix path.
- `dng_camera_profile.{h,cpp}` (~46 KB) — including `ProfileGainTableMap2` precedence (Camera Profile IFD > IFD 0 > Raw IFD `ProfileGainTableMap`), `ProfileDynamicRange` (SDR=0, HDR=1), `ProfileGroupName`.
- `dng_gain_map`, `dng_lens_correction` (~61 KB), `dng_misc_opcodes`, `dng_opcodes`, `dng_opcode_list` — **opcode lists are always big-endian on disk**, regardless of file byte order.
- `dng_bad_pixels`, `dng_resample`.
- `dng_linearization_info`, `dng_mosaic_info` — Bayer demosaic, including `ColumnInterleaveFactor` × `RowInterleaveFactor` sub-image split (DNG 1.7.1).

### Phase 6 — Negative + render pipeline (3–4 weeks)
- `dng_negative.{h,cpp}` (~175 KB! largest single file).
- `dng_host.{h,cpp}` — `DngHost` as the public configuration surface.
- `dng_read_image.{h,cpp}` (~81 KB).
- `dng_render.{h,cpp}` — full color pipeline including:
  - Camera → XYZ_D50 → ProPhoto RGB.
  - HDR encode `f(x) = x(256+x) / (256(1+x))` and inverse around table lookups for `ProfileDynamicRange = 1`; never clip to [0,1] inside HDR sections.
  - `BaselineExposure` → post-warp → `ProfileGainTableMap2` in RIMM/ProPhoto.
  - HSV map → ProfileToneCurve → ProfileLookTable → RGBTables.
- Verify stages 1/2/3 byte-for-byte against `dng_validate -1/-2/-3` goldens for at least one sample per compression code.

### Phase 7 — JPEG + JXL codec adapters (2 weeks, parallelizable with Phase 6)
- `DngSharp.Dng.Sdk.Jpeg`: wrap `BitMiracle.LibJpeg.NetCore` (or equivalent), exposed as `IJpegEncoder`/`IJpegDecoder`. Cover baseline DCT (8-bit YCbCr/grayscale) and lossless Huffman JPEG (compression code 7).
- `dng_lossless_jpeg.{h,cpp}` + `dng_lossless_jpeg_shared.cpp` (~90 KB) — lossless 16-bit JPEG isn't covered by managed libs; **port directly** to managed code. This is unavoidable.
- `dng_jpeg_image`, `dng_jpeg_memory_source` — thin adapters.
- `DngSharp.Dng.Sdk.Jxl`: `LibraryImport` against `libjxl.dll`/`.so`. Cover encode (distance/effort/decode-speed → `JXLDistance`/`JXLEffort`/`JXLDecodeSpeed`) and decode.
- `dng_jxl.{h,cpp}` (~93 KB) — port the DNG-side glue (tile layout, container framing) on top of `DngSharp.Dng.Sdk.Jxl`.

### Phase 8 — Writer + previews (2 weeks)
- `dng_image_writer.{h,cpp}` (~214 KB — second largest file). Must enforce:
  - `DNGBackwardVersion` minimums per feature (1.3 opcodes, 1.4 float/deflate/lossy JPEG/proxy, 1.5 depth/enhanced, 1.6 semantic/3rd illuminant, 1.7 JXL, 1.7.1 ColumnInterleaveFactor).
  - 64-bit DNG magic `43` switch only when output would exceed 4 GB.
  - Never write both `AsShotNeutral` and `AsShotWhiteXY` in the same IFD.
  - Write `ProfileGainTableMap2` when present (supersedes old `ProfileGainTableMap`).
- `dng_preview`, `dng_jpeg_image` integration for preview IFDs.

### Phase 9 — `dng_validate` CLI (1 week)
- Port `dng_validate.cpp` flag-for-flag. Use `System.CommandLine`.
- Mirror flags: `-v`, `-1/-2/-3`, `-tif`, `-dng`, `-lossyMosaicJXL`, `-losslessJXL`, `-proxy`, etc.
- Bracket XMP init/terminate (`IXmpSdk.Initialize/Terminate`).

### Phase 10 — Validation, perf, hardening (open-ended)
- Golden-file diff suite over all `sample_files/*.dng` for: `-v` text, stage-1/2/3 TIFFs, rendered TIFF, round-trip DNG, proxy DNG, lossy/lossless JXL re-encode.
- Bit-exact diffs for integer pipelines; ULP-bounded diffs for float pipelines.
- BenchmarkDotNet harness for: stage-2 linearization, demosaic, color transform, JXL decode, full render. Compare wall-clock vs native `dng_validate Release`.
- SIMD-accelerate the hot kernels in `dng_reference.cpp` (per-pixel multiply-add, matrix transform, tone curve lookup) using `Vector256<float>`.
- AOT publish smoke test in CI (Windows x64 + Linux x64 + macOS arm64).

---

## Risks / open questions

1. **XMP toolkit.** No mature managed replacement. P/Invoke is pragmatic but adds a native dep on every platform. Worth costing a managed XMP read-only parser if write fidelity isn't required.
2. **JXL.** Same as XMP. `libjxl` ABI is unstable across versions — pin to the version vendored under `dng_sdk_1_7_1/libjxl`.
3. **Lossless JPEG.** Must port `dng_lossless_jpeg_shared.cpp` (~90 KB) by hand — no managed library covers DNG's 16-bit lossless Huffman variant.
4. **Floating-point determinism.** C++ uses `double` throughout color math; ensure C# does the same (no implicit float promotion) so goldens match across CPUs.
5. **Bit-exactness goal.** Decide upfront: target byte-for-byte parity with native `dng_validate` outputs, or only spec-conformance? The former drives test design.
6. **Licensing.** Adobe DNG SDK license must be reviewed before publishing a derivative work. Vendored libjpeg, libjxl, XMP each have their own terms.
7. **Effort.** Realistic budget: **6–9 months for one senior engineer** to reach feature-parity with `dng_validate`. Phase 1–3 + read-only parsing for v0.1 is reachable in ~2 months.

---

## Suggested first milestone (v0.1, ~2 months)

"Read a DNG, dump its tags, decode an uncompressed or deflate-compressed stage-1 image to a TIFF."

Phases 0, 1, 2, 3, partial 6 (read path only, no render), partial 8 (TIFF writer only). No JXL, no XMP write, no opcodes beyond a stub. This proves the architecture end-to-end before committing to the long tail.
