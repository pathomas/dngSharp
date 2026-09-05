# Phase 10 — SIMD kernels vs. scalar fallback (2026-08-01)

Compares the two vectorized fast paths added under the `simd-kernels` task
against their scalar fallbacks, on a synthetic 6000×4000 image (matching the
iPhone ProRAW sample dimensions under `images/`). Per the task's acceptance
criteria ("only accept SIMD variant when BDN shows a statistically
meaningful margin"), both kernels are kept — see Decision below.

## Environment

```
BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8875)
AMD Ryzen 5 3500U with Radeon Vega Mobile Gfx
1 CPU, 8 logical / 4 physical cores
.NET SDK 10.0.302
[Host]   : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2
ShortRun : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2
Job=ShortRun  IterationCount=3  LaunchCount=1  WarmupCount=3
```

Reproduce:

```powershell
dotnet run --project tests\DngSharp.Dng.Sdk.Benchmarks -c Release --no-build -- `
    --filter *SimdKernelBenchmarks* --job Short
```

The "ScalarFallback" variants call the exact same public API but pass inputs
that deliberately disqualify the fast-path guard (a `BlackLevelRepeatDim`
pattern for linearization; a non-null identity tone curve for the matrix
transform) — same math, same output shape, scalar code path only.

## Results

| Method                          | Mean      | Allocated | Speedup vs. scalar |
|----------------------------------|----------:|----------:|--------------------:|
| `Linearization_Simd`             |  35.3 ms  |  91.6 MB  | **2.77×** |
| `Linearization_ScalarFallback`   |  98.0 ms  |  91.6 MB  | (baseline) |
| `MatrixTransform_Simd`           | 144.5 ms  | 274.7 MB  | **1.56×** |
| `MatrixTransform_ScalarFallback` | 225.9 ms  | 274.7 MB  | (baseline) |

(Allocations are identical between SIMD/scalar pairs — the fast paths only
change the arithmetic loop, not the output buffer allocation strategy.)

## Decision

**Keep both SIMD kernels.** Both show a large, consistent margin (>50%)
across all 3 iterations with tight StdDev, well above noise:

- `Stage2Builder` linearization fast path (single-plane, no LUT/repeat/
  deltas): **~2.8× faster** — the dominant win. This is the common case for
  real Bayer CFA sensor data (`BlackLevelRepeatDim` present but with
  effectively-constant values per plane is the exception, not the rule; true
  per-cell RGGB bias still falls back correctly to scalar, verified by
  `Repeating_black_level_pattern_falls_back_to_scalar_and_is_correct`).
- `Stage3Renderer` 3×3 matrix + exposure fast path (no tone curve): **~1.56×
  faster**. Smaller margin than linearization because of the AoS
  (interleaved RGB) de-interleave/re-interleave overhead the kernel pays to
  batch the matrix multiply — still a clear net win over the naïve
  per-pixel scalar loop.

## Notes on implementation choice

- Both kernels use the portable `System.Numerics.Vector<float>` API rather
  than explicit `Vector256`/`Vector512` — the JIT maps `Vector<float>` to the
  widest SIMD ISA available on the host (AVX2/AVX-512 on x86-64-v2/v3, NEON
  on Arm64) without per-ISA code paths, matching this repo's
  AOT/cross-platform-first conventions.
- Linearization vectorizes cleanly because Bayer mosaic stage-1 data
  (`Planes == 1`) is contiguous per row under the interleaved
  `PixelBuffer` layout — no gather/scatter needed, just a widen-convert →
  vector multiply-add → clip.
- The matrix transform pays an AoS→SoA→AoS conversion cost per row (since
  `SimpleImage` always stores 3 interleaved planes) — this is why its
  speedup is smaller than linearization's. A larger win would require
  restructuring `SimpleImage` to a planar (SoA) layout, which is out of
  scope for this task (would touch every consumer of `PixelBuffer`).
- Tone-curve LUT gather (the third target named in the original task) was
  evaluated and **deliberately not vectorized**: the profile tone curve is a
  short piecewise-linear list (typically <32 points) evaluated via binary
  search, applied only when a `ProfileToneCurve` tag is embedded (uncommon
  in the current 14-sample corpus) and only per-channel on already-
  vectorization-hostile branchy control flow. A flat high-resolution LUT +
  hardware gather was judged not worth the added complexity for a rarely-hit
  path; the existing `HdrToneMapper.EvaluateCurve` binary search remains
  scalar. Revisit if profiling on real camera profiles shows this path is
  hot.
