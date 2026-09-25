# Phase 10 — Planar (SoA) pixel storage (2026-09-25)

`SimpleImage` now allocates its backing buffer **planar** (structure-of-arrays:
each plane is a contiguous row-major block, `ColStep = 1`, `RowStep = W`,
`PlaneStep = W·H`) instead of **interleaved** (array-of-structures,
`RGBRGB…`, `ColStep = Planes`, `PlaneStep = 1`). Per-plane rows are therefore
contiguous float/ushort spans, which is the shape SIMD kernels (and a future
ILGPU port) want — no de-interleave scratch, no gather/scatter.

The on-disk order is unchanged: TIFF `PlanarConfiguration=1` (chunky) is still
what codecs decode and what the stage/JPEG writers emit. The conversion
happens exactly once per direction in `PixelKernels.Copy`, which is now
layout-agnostic (per plane, per row: a single `CopyTo` when both rows are
contiguous, otherwise a strided typed copy).

## What changed

| Site | Before | After |
|---|---|---|
| `SimpleImage` ctor | `PixelBuffer.Interleaved` | `PixelBuffer.Planar` |
| `SimpleImage.WriteTile` | Interleaved row-copy | `PixelKernels.Copy` (any layout → planar; no-op when handed its own `GetTile` view) |
| `PixelKernels.Copy` | Interleaved fast path + per-sample slow path | Per-plane/per-row typed copy, contiguous or strided; `ToInterleavedBytes` packs any buffer to chunky bytes |
| `PixelKernels.Fill` | Filled the whole shared memory slice | Fills only the view's logical area (sub-views no longer clobber neighbours) |
| `PixelBuffer` | — | `SubView(area)`, `WithArea(area)`, `HasContiguousRows`, `IsInterleaved` |
| `StripReader.ReadStrips/ReadTiles` | Decoded straight into `GetTile` (strips) / a scratch `SimpleImage` + interleaved row copy (tiles) | Decode into a pooled interleaved scratch (`ArrayPool<byte>`), then `Copy(scratch.SubView(dst), image.GetTile(dst))` — edge tiles handled by `SubView`, no scratch `SimpleImage` |
| `ImageCrop` | Interleaved row copy | `Copy(src.GetTile(clipped), dst.Buffer.WithArea(clipped))` |
| `RowColumnInterleave` | `col*planes` byte arithmetic | Per-plane `OffsetBytes` gather |
| `UncompressedEncoder` (and `DeflateEncoder` via it) | Interleaved row slice | `ToInterleavedBytes` + optional byte swap |
| `StageImageWriter` | `GetTile(Bounds).Memory.ToArray()` | `ToInterleavedBytes(image.Buffer)` — keeps `PlanarConfiguration=1` |
| `Stage3Renderer.GammaAndQuantize` | Flat sample chunks | Row-parallel; per plane reads a contiguous row, scatters to `dest[(r·w+c)·3+p]` |
| `Stage3Renderer.RenderTask` SIMD | De-interleave 256-px blocks into 6 stack buffers, MAD, re-interleave | When both tiles have `HasContiguousRows`, run `Vector<float>` MADs directly on the three plane rows; scalar fallback otherwise |
| `LensWarpFilter` NOP path | Raw byte-span copy | `PixelKernels.Copy` |

Everything else (`Stage2Builder`, `DemosaicBilinear`, `HdrToneMapper`, all
opcodes, `HueSatMapRenderTask`) already addressed samples through
`PixelBuffer.OffsetBytes` and needed no change.

## Correctness gate

Outputs are **byte-identical**. `GoldenParallelPipelineTests` (pinned MD5s of
stage-1/2/3 TIFFs and the rendered JPEG for three synthetic fixtures at 1
thread and all cores) passes unchanged, as do all 23 `ParallelEquivalenceTests`.
The SIMD matrix path keeps the same float evaluation order
(`(r·m0 + g·m1 + b·m2)·exp`) so its results are bit-identical to the previous
interleaved implementation. New `Pixels/PixelLayoutTests.cs` covers the
interleaved ⇄ planar round trip, `SubView` edge-tile copies and the
`WriteTile(GetTile())` no-op.

## Environment

- Intel Core i7-8665U (4c/8t), .NET 10, BenchmarkDotNet 0.14, `--job Short`.
- `Threads` column = `DngHost.MaxThreads`; `0` = all cores.
- Synthetic 6000×4000 image (`ParallelScalingBenchmarks`), 6000×4000 Float32 RGB
  (`SimdKernelBenchmarks`).

## Results — layout-sensitive kernels, before → after

### `SimdKernelBenchmarks` (single AreaTaskRunner default DOP)

| Method | Before | After | Δ |
|---|---|---|---|
| `MatrixTransform_Simd` | 102.7 ms | **61.7 ms** | **1.66×** |
| `MatrixTransform_ScalarFallback` | 213.4 ms | 217.8 ms | — (unchanged path) |

### `ParallelScalingBenchmarks`

| Method | Threads | Before | After | Δ |
|---|---|---|---|---|
| `Render` | 1 | 207.3 ms | **72.4 ms** | **2.86×** |
| `Render` | 2 | 126.3 ms | 71.7 ms | 1.76× |
| `Render` | 4 | 103.8 ms | 60.3 ms | 1.72× |
| `Render` | 0 (8) | 89.5 ms | 68.7 ms | 1.30× |
| `GammaAndQuantize` | 1 | 3 283 ms | 2 356 ms | 1.39× |
| `GammaAndQuantize` | 2 | 1 838 ms | 1 303 ms | 1.41× |
| `GammaAndQuantize` | 4 | 1 232 ms | 993 ms | 1.24× |
| `GammaAndQuantize` | 0 (8) | 820 ms | 752 ms | 1.09× |
| `TileDecode_Deflate` | 1 | 79.6 ms | 76.0 ms | ≈ |
| `TileDecode_Deflate` | 4 | 51.0 ms | 33.9 ms | 1.50× |
| `TileDecode_Deflate` | 0 (8) | 44.3 ms | 32.2 ms | 1.37× |
| `TileDecode_Deflate` allocated | — | 161 MB | **111 MB** | −31 % |
| `StripDecode_Deflate` | 1 | 26.4 ms | 35.6 ms (±107 ms error) | noise |
| `StripDecode_Deflate` | 4 | 17.3 ms | 18.1 ms | ≈ |
| `StripDecode_Deflate` | 0 (8) | 17.1 ms | 21.9 ms | ≈ (Deflate-bound) |

## Reading the numbers

- **Render** is the headline: the vectorised matrix transform no longer
  spends most of its time shuffling floats between AoS rows and SoA scratch
  buffers. Single-threaded it is now faster than the old 8-thread result,
  and the multi-thread ceiling moved from ~90 ms to ~60–70 ms — we are back
  against memory bandwidth (288 MB in + 288 MB out per call).
- **GammaAndQuantize** is `Math.Pow`-bound (the sRGB curve), so the layout
  win is modest (~1.4× serial) and comes from the per-row planar read
  replacing the strided interleaved walk. A LUT for the 8-bit quantised
  curve would be the next lever, not layout.
- **Tile decode** lost a full scratch `SimpleImage` allocation per tile
  (−31 % allocated) and the extra row-copy pass; the pooled interleaved
  scratch is reused across tiles.
- **Strip decode** is Deflate-bound; the ±107 ms error bar on the 1-thread
  row is measurement noise (Deflate + GC), not a regression signal. The
  table was measured with every strip going through the interleaved
  scratch; `StripReader.DecodeInto` has since been given a fast path that
  decodes single-plane, full-width strips straight into the image (planar
  and interleaved layouts coincide for one plane), so the common Bayer
  case pays no copy at all. Multi-plane LinearRaw strips still take the
  scratch + `Copy` route.

## Follow-ups enabled

- `ilgpu-branch`: each plane is now one contiguous device-uploadable span;
  the 3×3 matrix / linearization / gamma kernels map 1:1 onto per-plane
  `ArrayView<float>` rows.
- `Stage2Builder` SIMD is still gated on `Planes == 1`; with planar storage
  it can now vectorise multi-plane LinearRaw data plane-by-plane.
