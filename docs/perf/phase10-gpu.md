# Phase 10 — ILGPU back-end (`feature/ilgpu`)

Opt-in GPU port of the render pipeline in `src/DngSharp.Dng.Sdk.Gpu`, built
on [ILGPU](https://ilgpu.net) 1.5.3. Everything after Stage 1 decode that
was a `Parallel.For` over pixels on the CPU — linearize, demosaic, colour
transform, tone map, gamma/quantize — runs as five kernels over
device-resident planar buffers; the raw image is uploaded once and only the
final interleaved RGB8 comes back.

Host for every number on this page: Intel i7-8665U (4 cores / 8 threads,
AVX2) + **NVIDIA Quadro P520** (Pascal GP108, 384 CUDA cores, 2 GB, PCIe
3.0 ×4), driver 550.163, CUDA 12.4, .NET 10.0.12, Debian 13. The P520 is a
2018 ultrabook GPU with 48 GB/s memory bandwidth — roughly the weakest CUDA
device one would realistically run this on — so treat these as a floor.

## Headline

Stage 1 → SDR RGB8 on the three 6288×4056 iPhone ProRAW files under
`images/` (same pipeline as `EndToEndBenchmarks.RenderToSdrRgb8` plus
gamma/quantize; lossless-JPEG decode excluded from both sides because it
is not GPU-portable and identical for both):

| Path | Mean | vs. CPU (8 threads) | Managed alloc |
|---|---:|---:|---:|
| `Cpu_Stage1ToRgb8` — current CPU pipeline, all cores | 2.98 – 3.26 s | 1.00× | 1007 MB |
| `Gpu_Stage1ToRgb8` — resident CUDA path incl. upload + download | **0.207 – 0.218 s** | **≈14×** | 69 MB |

Put in the context of the full `-jpeg` CLI run measured in
`phase10-end-to-end.md` (≈4.6 s per file, of which ≈1.0 s is Stage 1 read
+ decode and ≈0.7 s is the serial JPEG encode): swapping the middle of the
pipeline for the GPU path takes a file from **≈4.6 s to ≈1.9 s** (2.4×
end-to-end). The remaining time is now entirely the two serial codec steps.

## Per stage

Each `Gpu_*` row below is the per-stage helper, which **includes its own
upload and download** of 100–300 MB float32 planar images across PCIe 3.0
×4 (~3 GB/s effective). That is why the stage sum (≈690 ms) is far larger
than the resident total (≈210 ms): the resident path pays for one 51 MB
upload and one 76 MB download, not five round-trips.

| Stage | CPU 8 thr | GPU (+ transfer) | Speed-up | Notes |
|---|---:|---:|---:|---|
| Linearize (Stage 1 → 2, 2×2 black repeat, white 4095) | 198 – 253 ms | 68 ms | 3–4× | 51 MB up + 102 MB down; kernel itself is a few ms |
| Demosaic (bilinear, Stage 2 → 3) | 1095 – 1227 ms | 219 – 228 ms | 5× | 102 MB up + 306 MB down dominates |
| Colour render (matrix + HueSatMap + exposure + curve) | 655 – 697 ms | 252 – 270 ms | 2.6× | 306 MB up + 306 MB down |
| Gamma + quantize (sRGB, float → RGB8) | 728 – 804 ms | 145 ms | 5× | 306 MB up + 76 MB down; CPU side is `Math.Pow`-bound |
| **Resident total** | **2.98 – 3.26 s** | **0.21 s** | **14×** | |

Reproduce:

```sh
DNGSHARP_GPU_BACKEND=cuda dotnet run -c Release \
  --project tests/DngSharp.Dng.Sdk.Benchmarks -- --filter '*GpuPipeline*'
```

`DNGSHARP_GPU_BACKEND` accepts `cuda`, `opencl`, `cpu` (ILGPU's CPU
accelerator — useful for CI) or `auto` (default: CUDA → OpenCL → CPU).

## Correctness

`tests/DngSharp.Dng.Sdk.Gpu.Tests` (30 tests) checks every kernel against
the CPU reference on synthetic Bayer/LinearRaw inputs and the whole
resident path against the CPU chain. The GPU evaluates every expression in
float32 (Pascal FP64 is 1/32 rate) where the CPU path uses double
intermediates, so all comparisons are tolerance-based. Observed vs. asserted:

| Comparison | Observed max abs diff (Quadro P520) | Asserted |
|---|---:|---:|
| Linearize (plain / LUT / deltas / 3-plane) | 6e-8 | < 1e-5 |
| Demosaic (4 CFA phases, odd sizes) | 0 (bit-exact) | < 1e-6 |
| Render, no HueSatMap, 6 colour spaces ± tone curve | 1.2e-7 | < 2e-5 |
| Render with 2.5D / 3D HueSatMap | 4.5e-7 max, 4.4e-8 mean | < 1e-3 / < 1e-5 |
| Tone map (profile curve / luminance S-curve) | 3.6e-7 | < 1e-4 |
| Gamma + quantize | 1 LSB, mean < 5e-5 | ≤ 1 LSB |
| Resident `RenderToRgb8` vs CPU chain (Bayer + LinearRaw) | ≤ 1 LSB, > 1 LSB on 0 % | ≤ 2 LSB, > 1 LSB < 0.1 % |

Every difference is at the float32 rounding floor (the CPU reference
stores its intermediates as `float` too, so double-vs-float only matters
inside one expression). The asserted bounds are deliberately ~100× looser
so the suite also passes on GPUs with fused-multiply-add contraction or
fast-math transcendental approximations. Each test logs its observed
`max`/`mean` via `ITestOutputHelper` — run with
`--logger "console;verbosity=detailed"` to see them for a given device.

The suite passes identically on the CPU accelerator and on the P520; a
guard test fails if `DNGSHARP_GPU_BACKEND=cuda` silently falls back.

## Memory budget

For a 6288×4056 Bayer frame the resident path holds, all at once:

| Buffer | Type | Size |
|---|---|---:|
| Stage 1 | ushort ×1 | 51 MB |
| Stage 2 | float ×1 | 102 MB |
| Stage 3 | float ×3 | 306 MB |
| Rendered RGB (crop) | float ×3 | ≤ 306 MB |
| RGB8 | byte ×3 | 76 MB |
| LUT / HueSatMap / curve | float | < 1 MB |
| | | **≈ 840 MB** |

Fits a 2 GB card with headroom. Freeing Stage 1 and Stage 2 before the
render kernel would cut peak to ≈690 MB; not done yet because it doesn't
matter on any card ≥ 2 GB.

## What is and isn't ported

Ported, device-resident: `Stage2Builder` (LUT, per-plane or repeating black,
row/column deltas, per-plane white, clip), `DemosaicBilinear` (period-2
edge wrap), `Stage3Renderer.Render` (camera→XYZ→output matrix chain,
HueSatMap 2.5D/3D via `HueSatMap.CopyTo`, exposure, profile tone curve),
`HdrToneMapper.Apply` (per-channel curve or luminance S-curve),
`Stage3Renderer.GammaAndQuantize` (sRGB). LinearRaw / RGB 3-plane inputs
skip demosaic.

Not ported: **opcode lists**. `RenderToRgb8` documents that it assumes all
three lists are empty (true of every file under `images/`; the benchmark
throws if not). A caller with opcodes should run `OpcodeList1Applier` on
the host, use the per-stage helpers, or wait for a device port of the
common opcodes (`GainMap`, `WarpRectilinear`, `FixVignetteRadial`).

Also not touched: Stage 1 decode (lossless JPEG / JXL / Deflate — inherently
serial per tile) and the JPEG encoder. Those are now ≈90 % of a `-jpeg`
run with the GPU path enabled.

## Integration notes

- `DngSharp.Dng.Sdk.Gpu` sets `IsAotCompatible=false` / `IsTrimmable=false`
  — ILGPU JIT-compiles kernels at runtime via reflection and cannot be
  Native-AOT published. **Do not reference it from
  `DngSharp.Dng.Validate`** (which is AOT-published). A GPU-enabled CLI
  would need a separate non-AOT entry point or a plugin load.
- First call to a `GpuRenderPipeline` compiles five kernels to PTX (≈1–2 s
  on this host). Keep one pipeline per device for the process lifetime.
- `HueSatMap.EntryCount` / `HueSatMap.CopyTo(Span<float>)` were added to
  the core SDK so the table can be uploaded without exposing internals.
- `Render(...)` with a crop returns a `SimpleImage` rebased to origin
  (0,0), matching `ImageCrop.Crop`.

## Next levers

1. **Overlap transfer with compute.** Upload Stage 1 tiles on a second
   stream while the first tiles linearize; hide most of the 51 MB upload.
2. **Fuse linearize + demosaic** into one kernel reading `ushort` directly
   — saves the 102 MB Stage 2 write/read and one launch.
3. **Fuse tone map + gamma** — both are per-pixel; saves a 306 MB
   read/write pass.
4. **Port opcodes** so the resident path covers non-iPhone files.
5. **GPU JPEG encode** (or a `libjpeg-turbo` P/Invoke) — the encode is now
   the single largest step.
6. On a bandwidth-rich card (any desktop GPU) items 1–3 matter less; there
   the kernel launches themselves are ~10 ms total and the PCIe transfer
   is the whole cost.
