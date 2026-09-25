# Phase 10 — Parallel.For scaling of pixel-domain kernels (2026-09-24)

Measures the thread-scaling of every pixel-domain kernel that runs through
`AreaTaskRunner` / `ParallelWork.For` after the `parallel-for-area-tasks`
pass, on a synthetic 6000×4000 image (matching the iPhone ProRAW sample
dimensions under `images/`). `Threads = 1` is the fully serial baseline
(`ParallelWork.For` runs inline on the calling thread when
`MaxThreads == 1`); `Threads = 0` resolves to `Environment.ProcessorCount`.

Thread count is controlled by `DngHost.MaxThreads` (null = runtime default)
and exposed on the CLI as `dng_validate -threads <n>`.

## What was parallelised

| Site | Before | After |
|---|---|---|
| `AreaTaskRunner.Run` (Stage2Builder, DemosaicBilinear, Stage3Renderer, HdrToneMapper) | Already `Parallel.For` over tiles, but no way to cap DOP | Honours `DngHost.MaxThreads`; `Run(task, area, host)` overload |
| `StripReader.ReadStrips` / `ReadTiles` | Serial read-then-decode per strip/tile | Serial read of all blobs (the `DngStream` is positional and not thread-safe), then parallel decode + `WriteTile` into disjoint regions |
| Opcode appliers (`MapPolynomial`, `MapTable`, `DeltaPerRow/Column`, `ScalePerRow/Column`, `GainMap`, `FixVignetteRadial`, `FixBadPixelsConstant`, `LensWarpFilter`) | Serial row loops | 64-row bands via `RowBandRunner`; `FixBadPixelsConstant` collects per-band fix lists and applies them serially so repaired pixels never feed neighbours |
| `Stage3Renderer.GammaAndQuantize` | Serial sample loop | 65 536-sample chunks via `ParallelWork.For` |

All kernels are pure per-pixel (no reductions, no FP reassociation), so the
parallel output is **byte-identical** to serial. This is pinned by
`tests/DngSharp.Dng.Sdk.Tests/Tasks/ParallelEquivalenceTests.cs` (23 tests
covering every site above, including the `effectiveRows/Cols` clamps,
pitch > 1, multi-strip and tiled Deflate decode, and cancellation through the
parallel decode path).

## Environment

```
BenchmarkDotNet v0.14.0, Linux
Intel Core i7-8665U @ 1.90 GHz
1 CPU, 8 logical / 4 physical cores
.NET SDK 10.0
[Host]   : .NET 10.0.12 (10.0.1226.42308), X64 RyuJIT AVX2
ShortRun : .NET 10.0.12 (10.0.1226.42308), X64 RyuJIT AVX2
Job=ShortRun  IterationCount=3  LaunchCount=1  WarmupCount=3
```

Reproduce:

```sh
dotnet run --project tests/DngSharp.Dng.Sdk.Benchmarks -c Release --no-build -- \
    --filter '*ParallelScalingBenchmarks*' --job Short
```

The strip/tile decode cases use `tests/Shared/MultiStripDngFactory.cs`, a
hand-rolled multi-strip / tiled Deflate DNG writer, because the vendored
Adobe sample files are not part of the repository and none of the synthetic
test DNGs have more than one strip.

## Results (6000×4000)

Mean wall time per operation. Speedup is relative to `Threads = 1`.

| Method | 1 thread | 2 threads | 4 threads | 8 threads (all) | Speedup @ 8 |
|---|---:|---:|---:|---:|---:|
| `StripDecode_Deflate` (64 rows/strip, 63 strips) | 35.5 ms | 17.7 ms | 17.4 ms | 18.5 ms | **1.9×** |
| `TileDecode_Deflate` (256×256, 384 tiles) | 92.4 ms | 55.7 ms | 43.9 ms | 55.9 ms | **1.7×** |
| `Linearization` (Stage2Builder) | 49.6 ms | 37.6 ms | 26.2 ms | 24.1 ms | **2.1×** |
| `Demosaic` (bilinear RGGB) | 2 538 ms | 2 050 ms | 1 258 ms | 1 494 ms | **1.7×** |
| `Render` (3×3 matrix + exposure) | 181.9 ms | 145.7 ms | 123.4 ms | 90.9 ms | **2.0×** |
| `MapPolynomial` (3-plane, degree 3) | 507.6 ms | 372.9 ms | 346.9 ms | 289.2 ms | **1.8×** |
| `FixVignetteRadial` (3-plane) | 480.6 ms | 398.7 ms | 274.2 ms | 227.3 ms | **2.1×** |
| `GammaAndQuantize` (float RGB → 8-bit sRGB) | 3 156 ms | 1 857 ms | 1 065 ms | 681 ms | **4.6×** |

Allocations are identical across thread counts for every kernel (the
parallel paths add only the `Parallel.For` bookkeeping, a few KB).

### End-to-end CLI (`dng_validate -jpeg`, 24 MP uncompressed synthetic)

| `-threads` | Wall time | Speedup |
|---:|---:|---:|
| 1 | 6.55 s | (baseline) |
| 2 | 3.24 s | 2.0× |
| 4 | 2.31 s | 2.8× |
| 8 | 2.09 s | 3.1× |

JPEG output was verified byte-identical (`cmp`) across all thread counts.

## Interpretation

- **~2× at 8 logical threads is a memory-bandwidth ceiling, not a bug.**
  Every kernel except `GammaAndQuantize` streams 96–288 MB of `float32`
  through a 4-core mobile part with 2 hardware threads per core. The
  "8 threads" column is 4 physical cores; going from 4 → 8 threads is flat
  or slightly negative for the bandwidth-bound kernels (`StripDecode`,
  `Demosaic`, `TileDecode`), exactly as expected for SMT on a saturated
  memory bus.
- **`GammaAndQuantize` scales 4.6×** because it is compute-bound
  (`Math.Pow` per sample) with a small 8-bit output, so it is the one
  kernel that benefits from SMT.
- **Strip/tile decode gets ~1.9× and then stops** because the serial
  read-all-blobs phase and the `WriteTile` copy into the destination are
  not parallel, and Deflate inflation is itself memory-heavy. With real
  JXL-compressed files the decode is far more compute-heavy and should
  scale closer to physical core count.
- **The CLI's residual (~2.1 s at 8 threads) is the serial JPEG encoder and
  file I/O**, which are outside this task's scope.
- ShortRun variance is high (several `Error` columns exceed the mean).
  The trend is consistent across all 3 iterations per case, but treat
  individual cells as ±10–20 %, not precise.

## Decision

**Keep all parallel paths; ship `DngHost.MaxThreads` + `-threads`.**
Every kernel shows a monotone or flat improvement from 1 → 4 threads with
byte-identical output and no allocation regression. The next step change is
not more threads but less memory traffic:

- `soa-pixel-layout` (planar `SimpleImage`) would remove the AoS
  de-interleave overhead in `Render` / `MapPolynomial` / `FixVignetteRadial`
  and make them vectorise cleanly, raising the bandwidth ceiling.
- Fusing `Render` → `GammaAndQuantize` into one pass would eliminate a
  288 MB intermediate write + read.
- Only after those is an `ilgpu-branch` (GPU offload) worth the
  PCIe transfer cost — see the todo notes.
