# Phase 10 — performance notes

Reference points for the .NET 10 DNG SDK port. Kept intentionally short —
concrete numbers change with hardware; the strategy is what needs to be
durable.

## Benchmark project

`tests/DngSharp.Dng.Sdk.Benchmarks/` — BenchmarkDotNet 0.14, one entry point,
`BenchmarkSwitcher`-based discovery.

```powershell
# Discover benchmarks
dotnet run --project tests\DngSharp.Dng.Sdk.Benchmarks -c Release -- --list flat

# Full run (default BDN job — a few minutes)
dotnet run --project tests\DngSharp.Dng.Sdk.Benchmarks -c Release -- --filter *

# Fast smoke (Job.Short — seconds to a minute)
dotnet run --project tests\DngSharp.Dng.Sdk.Benchmarks -c Release -- --filter * --job Short
```

## Current benchmarks

| Class | Measures | Why |
|---|---|---|
| `ContainerParseBenchmarks.Parse` | `DngContainer.Parse` over 4 representative samples (240 KB uncompressed, 5.7 MB SubIFD-heavy, 1.2 MB ExtraCameraProfiles, 24 MB JXL) | Parse-only cost every pipeline stage pays before doing real work — the number to beat when adding SIMD or reworking the IFD scanner |
| `SimdKernelBenchmarks` | `Stage2Builder` linearization and `Stage3Renderer` matrix-transform, SIMD fast path vs. scalar fallback | See `phase10-simd.md` for results and the keep/drop decision |
| `DemosaicBenchmarks.Demosaic_Bilinear` | `DemosaicBilinear.Build` (Stage 2 → Stage 3) on a synthetic 6000×4000 single-plane RGGB CFA image | Cost of the per-pixel bilinear kernel + border wrap; candidate for a future SIMD pass if profiling shows it's hot relative to linearization/matrix-transform |
| `ParallelScalingBenchmarks` | Every `AreaTaskRunner` / `ParallelWork.For` kernel (linearization, demosaic, render, opcodes, gamma/quantize, multi-strip and tiled Deflate decode) swept over `DngHost.MaxThreads` = 1/2/4/all on a synthetic 6000×4000 image | See `phase10-parallel.md` for the scaling table and the memory-bandwidth-ceiling analysis; `phase10-soa.md` re-measures the layout-sensitive subset after the planar (SoA) storage switch |
| `EndToEndBenchmarks` | The whole `-jpeg` render pipeline minus the JPEG encode (parse → decode → linearize → demosaic → crop → colour transform → tone-map) on the real camera DNGs under `images/`, serial vs. all cores | The number a CLI user actually experiences; see `phase10-end-to-end.md` for the cross-revision wall-clock table and per-step breakdown |
| `GpuPipelineBenchmarks` | Decoded Stage 1 → SDR RGB8 on the real `images/*.dng`: CPU pipeline (all cores) vs. the device-resident ILGPU path in `DngSharp.Dng.Sdk.Gpu`, plus per-stage CPU/GPU pairs | Requires a CUDA/OpenCL device (`DNGSHARP_GPU_BACKEND=cuda`); see `phase10-gpu.md` — ≈14× on a Quadro P520, transfer-bound |
| `JxlDecodeBenchmarks.ReadStage1` | Full `StripReader.ReadStage1` (parse + strip-read + `libjxl` decode) on `03_jxl_bayer_raw_integer.dng` | Requires `libjxl` on the loader path (`tools/build-libjxl.ps1`); pair with `ContainerParseBenchmarks.Parse` on the same file to isolate decode-only cost (`ReadStage1 − Parse ≈ JXL decode + strip copy`) |

## End-to-end / cross-revision

Microbenchmarks isolate one kernel on synthetic data; two more tools measure
what a user of the CLI sees on real files:

- `DngSharp.Dng.Validate -timing …` prints wall-clock milliseconds for every
  render step (read Stage 1, linearize, demosaic, colour transform, tone-map,
  gamma/quantize, encode). First stop when a real file is slower than the
  microbenchmarks predict.
- `tools/bench-revisions.sh [-n RUNS] REV… WORKTREE` builds the CLI at each
  git revision in a throwaway worktree and times `-jpeg` over every
  `images/*.dng`, interleaving revisions per run so thermal throttling hits
  them equally. Emits a Markdown table with the speed-up relative to the
  first revision. Results: `phase10-end-to-end.md`.
- `dotnet run -c Release --project tests/DngSharp.Dng.Sdk.Benchmarks -- corpus
  [--backend cuda|opencl|cpu|auto] [--out report.md] [--cpu-only|--gpu-only]`
  converts **every** `images/*.dng` to JPEG once on the CPU path and once via
  `GpuRenderPipeline.RenderToRgb8`, reporting per-file decode/render/encode
  time, managed allocations, peak working set and peak device memory, with a
  CPU-vs-GPU summary. Not BenchmarkDotNet — one pass over a large corpus is
  the measurement. Results: `corpus-2026-09-24.md`.

## Results

- `phase10-end-to-end.md` — **start here**: cross-revision wall clock on
  real files (serial baseline → parallel → SoA), per-step breakdown, and
  the ranked list of remaining levers.
- `phase10-parallel.md`, `phase10-simd.md`, `phase10-soa.md` — per-kernel
  microbenchmarks behind each optimisation.
- `corpus-2026-09-24.md` — 55-file DNG → JPEG corpus, CPU vs GPU, times and
  memory per file; `corpus-2026-09-24-run1-unthrottled.md` is the same corpus
  before the laptop GPU hit its thermal clock cap.
- `phase10-gpu.md` — opt-in ILGPU back-end (`feature/ilgpu`): CPU vs GPU
  per stage and resident, equivalence tolerances, memory budget, what is
  not ported (opcodes, codecs).

## Baselines

See `phase10-baseline.md` (generated by
`dotnet run --project tests/DngSharp.Dng.Sdk.Benchmarks -c Release -- --filter *ContainerParseBenchmarks* --job Short`).

## Planned follow-ups

1. **Stage-2 linearization SIMD kernel.** `Pipeline/Stage2Builder.cs` currently
   walks samples one at a time via `BinaryPrimitives.ReadUInt16LittleEndian`.
   The common case (interleaved `PixelType.UInt16`, no LUT, no row/column
   deltas) can process 8 samples/step with `Vector256<float>` MAD, or 16
   with `Vector512<float>` where available. Keep the scalar fallback; add
   an equivalence test (same input → same output within FP tolerance).
2. **Color-transform 3×3 matrix on interleaved RGB.** Same shape — MAD over
   8 or 16 pixels' worth of interleaved samples per step.
3. **Tone-curve LUT gather.** `Vector256<int>` gather instructions where the
   ISA supports them; scalar loop fallback otherwise.

For each SIMD kernel: add a `*SimdBenchmarks` class comparing the vectorized
path against the scalar reference, and only keep the SIMD variant when BDN
shows a statistically meaningful win. Record deltas in `phase10-simd.md`.
