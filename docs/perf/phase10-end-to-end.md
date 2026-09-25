# Phase 10 — end-to-end results

The microbenchmarks in `phase10-parallel.md`, `phase10-simd.md` and
`phase10-soa.md` each isolate one kernel on synthetic data. This page
answers the question a user actually asks — *how much faster is the CLI on
my photos?* — by timing the whole `-jpeg` render across git revisions on the
three real iPhone ProRAW captures under `images/` (6288×4056 16-bit
lossless-JPEG-tiled Bayer, 54–62 MB each; the directory is gitignored).

Host for every number on this page: Intel i7-8665U (4 cores / 8 threads,
1.9 GHz base, laptop — thermally throttles under sustained load), .NET SDK
10.0.401, Linux.

## Tools

| Tool | What it measures |
|---|---|
| `tools/bench-revisions.sh -n 5 REV… WORKTREE` | Builds `DngSharp.Dng.Validate` at each revision in a throwaway worktree, then times `-jpeg` over every `images/*.dng` — median of N whole-process wall-clock runs, revisions interleaved per run so throttling hits them equally |
| `DngSharp.Dng.Validate -timing …` | Per-step wall-clock inside one render (read Stage 1, linearize, demosaic, colour transform, tone-map, gamma/quantize, encode) |
| `EndToEndBenchmarks` (BDN) | Same pipeline minus the JPEG encode and file I/O, `Threads` = 1 vs. all, on each `images/*.dng` |

## Cross-revision wall clock (`tools/bench-revisions.sh -n 5`)

Whole process, including ~0.1 s runtime start-up and writing the JPEG.
Median of 5 runs per cell.

| Revision | IMG_0815 | IMG_0827 | IMG_0828 | total (3 images) | speed-up |
|---|---:|---:|---:|---:|---:|
| `e5d1e16` — before Phase 10 (`AreaTaskRunner` already `Parallel.For`; decode, opcodes, gamma/quantize serial) | 6.39 s | 6.36 s | 6.36 s | **19.11 s** | 1.00× |
| `e12501c` — `Parallel.For` hot paths + `DngHost.MaxThreads` | 4.43 s | 4.55 s | 4.43 s | **13.41 s** | **1.42×** |
| working tree — + planar (SoA) pixel storage, lossless-JPEG & DefaultCrop fixes | 4.62 s | 4.68 s | 4.57 s | **13.87 s** | **1.37×** |
| working tree, `-threads 1` (fully serial, for reference; single run) | 12.32 s | 12.19 s | 12.13 s | **36.64 s** | 0.52× |

Note the pre-Phase-10 baseline was *not* single-threaded: the stage 2/3
area tasks were already `Parallel.For` over tiles. Phase 10 parallelised
what remained (strip/tile decode, opcode row bands, gamma/quantize) and made
the degree of parallelism controllable. Against a genuinely serial run
(`-threads 1`) the current build is **≈2.5× faster end-to-end** including
the serial JPEG encode, **2.8–2.9×** on the pipeline alone (BDN table below).

Two earlier batches of the same script gave 22.07 / 15.56 / 16.68 s and
21.09 / 14.91 / 15.27 s — the *absolute* numbers move ±10 % with the
laptop's temperature, the *ratios* are stable: parallelisation buys
**≈1.4× end-to-end**, and the SoA switch is **neutral (−3 %, inside
batch-to-batch noise)** on these files even though it wins clearly in
isolation (`phase10-soa.md`: 1.66× on the SIMD matrix transform, 2.9× on
the serial render path).

### Where the time goes

`-timing` on `IMG_0828` shows where the seconds actually go, and how each
step scales (working tree, all cores vs. `-threads 1`):

| Step | 8 threads | 1 thread | Scaling | Notes |
|---|---:|---:|---:|---|
| read Stage 1 (parse + lossless-JPEG tile decode) | 0.87 s | 1.81 s | 2.1× | Huffman decode is inherently serial per tile; 384 tiles decode in parallel but the strip *read* is serial (positional `DngStream`) |
| linearize Stage 2 | 0.42 s | 0.59 s | 1.4× | Per-CFA-position `BlackLevel` (2×2 repeat) → scalar path; the SIMD kernel only covers the single-black-level case |
| demosaic Stage 3 | 1.28 s | 2.94 s | 2.3× | Largest single step; scalar bilinear, memory-bound |
| crop | 0.08 s | 0.07 s | — | One planar copy |
| colour transform | 0.85 s | 2.54 s | 3.0× | Real profiles carry a `HueSatMap` + `ProfileToneCurve`, which route to the scalar per-pixel path (HSV conversion + delegate call ×3 per pixel). The SIMD matrix kernel measured in `phase10-simd.md` is *not* used for these files |
| tone-map | 0.27 s | 0.88 s | 3.3× | Binary-search `EvaluateCurve` per sample |
| gamma + quantize | 0.74 s | 2.27 s | 3.1× | `Math.Pow`-bound (see `phase10-soa.md` follow-ups: LUT) |
| JPEG encode | 0.80 s | 0.73 s | 1.0× | Managed baseline encoder, serial |
| **total render** | **5.3 s** | **11.8 s** | **2.2×** | (this batch ran hot — same build measured 4.6 s a few minutes earlier) |

Two things fall out of this table that the synthetic benchmarks hid:

1. **The real-file colour path is ~10× slower than the synthetic one**
   (2.5 s serial vs. 0.18 s for `ParallelScalingBenchmarks.Render`), because
   every real camera profile has a hue/sat map and tone curve. The scalar
   `HueSatMapRenderTask` is the biggest remaining optimisation target, not
   the matrix kernel.
2. **Two serial steps cap the ceiling**: JPEG encode (0.7 s) and the strip
   read inside Stage 1 (~0.3 s) don't scale at all, so Amdahl limits the
   8-thread pipeline to ~3× even if every kernel scaled perfectly. On this
   4-core laptop the parallel kernels themselves top out at 2–3.3× (memory
   bandwidth, see `phase10-parallel.md`).

### Fix made while measuring

The first working-tree run was 6 % slower than `e12501c`. `-timing` traced
it to the two 3-plane float consumers that had no planar fast path —
`HdrToneMapper`'s curve loop and the scalar/`HueSatMap` render tasks — which
were computing six `OffsetBytes` + `BinaryPrimitives` slices per pixel
across three now-distant planes. They now walk each plane-row as one
contiguous `float` span (matrix cells hoisted out of the pixel loop); output
is byte-identical (`-jpeg` MD5 unchanged, golden hashes unchanged) and the
gap closed to the ±3 % noise band.

## BDN `EndToEndBenchmarks` (`--job Short`)

Pipeline only — no JPEG encode, file read once into memory in `GlobalSetup`.
This is the durable in-repo version of the table above; re-run it after any
pipeline change:

```sh
dotnet run -c Release --project tests/DngSharp.Dng.Sdk.Benchmarks -- --filter '*EndToEnd*'
```

| Method | Threads | Image | Mean | StdDev | Ratio | Allocated |
|---|---:|---|---:|---:|---:|---:|
| DecodeStage1 | 8 | IMG_0815 | 569 ms | 34 ms | 0.19 | 103 MB |
| RenderToSdrRgb8 | 8 | IMG_0815 | **2,944 ms** | 21 ms | 1.00 | 1,042 MB |
| DecodeStage1 | 8 | IMG_0827 | 604 ms | 4 ms | 0.21 | 109 MB |
| RenderToSdrRgb8 | 8 | IMG_0827 | **2,921 ms** | 82 ms | 1.00 | 1,048 MB |
| DecodeStage1 | 8 | IMG_0828 | 575 ms | 15 ms | 0.20 | 111 MB |
| RenderToSdrRgb8 | 8 | IMG_0828 | **2,837 ms** | 37 ms | 1.00 | 1,049 MB |
| DecodeStage1 | 1 | IMG_0815 | 1,616 ms | 27 ms | 0.20 | 103 MB |
| RenderToSdrRgb8 | 1 | IMG_0815 | **8,264 ms** | 194 ms | 1.00 | 1,042 MB |
| DecodeStage1 | 1 | IMG_0827 | 1,600 ms | 34 ms | 0.19 | 109 MB |
| RenderToSdrRgb8 | 1 | IMG_0827 | **8,560 ms** | 280 ms | 1.00 | 1,048 MB |
| DecodeStage1 | 1 | IMG_0828 | 1,696 ms | 30 ms | 0.20 | 111 MB |
| RenderToSdrRgb8 | 1 | IMG_0828 | **8,466 ms** | 153 ms | 1.00 | 1,049 MB |

Pipeline-only speed-up from all cores: **2.8–2.9×** (the `-jpeg` wall
clock above is lower because the serial JPEG encode and process start-up
are included there). ~1 GB allocated per render is the Float32 Stage 2
(96 MB) + Stage 3 (288 MB) + render output (288 MB) + crop copies; a
follow-up could run the colour transform in place on Stage 3.

## Next levers, in order of expected end-to-end payoff

1. `HueSatMapRenderTask` — vectorise the two 3×3 matrices, keep only the
   HSV lookup + tone curve scalar; or precompute the tone curve into a LUT
   (changes output → needs a tolerance test, not a golden hash).
2. Gamma LUT in `GammaAndQuantize` (same caveat).
3. Multi-plane / per-CFA-black-level `Stage2Builder` SIMD.
4. Parallel JPEG encode (independent MCU rows → restart intervals).
