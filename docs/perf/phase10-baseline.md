# Phase 10 — parse-only baseline (2026-06-30)

Reference measurement for `DngContainer.Parse` before any Phase 10
optimization work. Establishes the number to beat when reworking the IFD
scanner or adding SIMD.

## Environment

```
BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8655)
AMD Ryzen 5 3500U with Radeon Vega Mobile Gfx
1 CPU, 8 logical / 4 physical cores
.NET SDK 10.0.301
[Host]   : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2
ShortRun : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2
Job=ShortRun  IterationCount=3  LaunchCount=1  WarmupCount=3
```

Reproduce:

```powershell
dotnet run --project tests\DngSharp.Dng.Sdk.Benchmarks -c Release --no-build -- \
    --filter *ContainerParseBenchmarks* --job Short
```

## Results

| Sample                         | Size    | Mean     | StdDev   | Gen0    | Allocated |
|--------------------------------|---------|---------:|---------:|--------:|----------:|
| `05_PGTM2_unsigned8.dng`       | 240 KB  | 246.0 μs | 10.23 μs | 35.6445 |  73.57 KB |
| `04_PGTM2_per_profile.dng`     | 5.7 MB  | 274.6 μs | 19.96 μs | 41.5039 |  86.77 KB |
| `14_hdr_sdr_profiles.dng`      | 1.2 MB  | 325.6 μs | 36.53 μs | 35.6445 |  75.05 KB |
| `03_jxl_bayer_raw_integer.dng` | 24 MB   | 373.7 μs | 69.23 μs | 44.9219 |  92.71 KB |

## Observations

- Parse cost is **dominated by IFD entry count and SubIFD walks, not file
  size**. `03_jxl_bayer_raw_integer.dng` (24 MB) is only ~52 % slower than
  `05_PGTM2_unsigned8.dng` (240 KB) — parse doesn't touch strip data.
- Allocations are all under 100 KB per parse. Most of it is the
  `TiffIfdEntry` / `TiffIfd` object graph. Gen-0 pressure only; no Gen-1
  or LOH activity.
- Every result is <400 μs on a modest 3-year-old mobile Ryzen. Parse-only
  is not the bottleneck for any realistic pipeline; SIMD work should target
  the pixel-domain kernels (stage 2 linearization, color transform, tone
  curve), not the IFD scanner.

## Note on `StdDev`

`--job Short` is 3 iterations × 3 warmups. The 03_jxl sample's ±69 μs
StdDev shows this run is noisy; rerun with the default job
(`--filter *ContainerParseBenchmarks*`, no `--job` override) for tighter
error bounds before basing decisions on absolute numbers.
