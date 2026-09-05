# dng — a native .NET 10 port of the Adobe DNG SDK

This repository contains two parallel codebases:

1. **The .NET 10 port** (`src/DngSharp.Dng.Sdk*`, `src/DngSharp.Dng.Validate`,
   `tests/DngSharp.Dng.Sdk.Tests`) — an in-progress, from-scratch C# rewrite of the
   Adobe DNG SDK 1.7.1: TIFF/DNG container parsing, the linearization →
   demosaic → color-render pipeline, JPEG/JPEG XL codec adapters, and a CLI
   (`dng_validate`-equivalent) that renders DNGs to JPEG/WebP.
2. **The vendored Adobe DNG SDK 1.7.1** (`dng_sdk_1_7_1/`) — the official C++
   SDK, used as the spec/behavior reference we validate against (golden-file
   diffing). This tree is **not checked into git**; see
   [Vendored native SDK](#vendored-native-sdk-not-in-git) below.

See [`PORTING_PLAN.md`](./PORTING_PLAN.md) for the phased plan, and
[`STATUS.md`](./STATUS.md) / [`SESSION.md`](./SESSION.md) for the current
progress snapshot and a chronological build log.

## Status

| Metric | Value |
|---|---|
| Phases done | 9 of 11 (0–9), phase 10 in progress |
| Tests passing | 366 / 366 |
| Build | 0 warnings, 0 errors on `Dng.slnx` (Release), 8 projects |
| Golden coverage | 14 / 14 sample DNGs diffed structurally against native `dng_validate -v` |
| Sample DNGs parsing end-to-end | 14 / 14 |
| Rendering | All supported photometrics render to JPEG/WebP (LinearRaw + Bayer CFA via bilinear demosaic) |

See [`STATUS.md`](./STATUS.md) for the full breakdown, known limits, and
upcoming milestones.

## Repository layout

```
Dng.slnx                     .NET 10 SDK-style solution
Directory.Build.props        net10.0, nullable on, warnings-as-errors, AOT-friendly
Directory.Build.targets      test-project overrides
Directory.Packages.props     Central Package Management
src/DngSharp.Dng.Sdk/                 core managed port (container/TIFF parsing, math,
                              color science, negative + render pipeline, codecs)
src/DngSharp.Dng.Sdk.Jpeg/            JPEG codec adapter
src/DngSharp.Dng.Sdk.Jxl/             JPEG XL codec adapter (P/Invoke to libjxl)
src/DngSharp.Dng.Sdk.Xmp/             XMP metadata adapter
src/DngSharp.Dng.Sdk.Preview/         JPEG/WebP preview rendering (SkiaSharp-backed)
src/DngSharp.Dng.Validate/            CLI mirroring dng_validate.cpp
tests/DngSharp.Dng.Sdk.Tests/         xUnit test suite
tests/golden/                golden-file capture + fixtures for diffing against
                              the native dng_validate reference
tools/                       tag-code extraction, libjxl build script, etc.
docs/                        architecture notes, DNG read workflow, perf notes
dng_sdk_1_7_1/                vendored Adobe DNG SDK 1.7.1 (not tracked in git)
```

## Build, test, run

```powershell
dotnet build Dng.slnx -c Release          # 0 warnings, 0 errors
dotnet test  Dng.slnx -c Release          # xUnit suite, 366/366 passing
dotnet run --project src\DngSharp.Dng.Validate -c Release -- <file.dng>                      # CLI summary
dotnet run --project src\DngSharp.Dng.Validate -c Release -- -jpeg out.jpg <file.dng>        # render to JPEG
dotnet run --project src\DngSharp.Dng.Validate -c Release -- -webp out.webp <file.dng>       # render to WebP
```

Native AOT publish smoke test (requires the platform's native toolchain for
the link step; CI runs this on Windows, Linux, and macOS):

```powershell
dotnet publish src/DngSharp.Dng.Validate -c Release -r win-x64 --self-contained -p:PublishAot=true
```

## Vendored native SDK (not in git)

`dng_sdk_1_7_1/` is Adobe's official DNG SDK 1.7.1 distribution, used locally
as:

- the spec/behavior reference for golden-file diffing (`tests/golden/`),
- the source for the auto-generated TIFF tag-code enum
  (`tools/extract-tag-codes.ps1`), and
- a buildable native `dng_validate` reference binary for manual comparisons.

It is intentionally excluded from version control (see `.gitignore`) — each
contributor obtains it separately (e.g. from Adobe's DNG SDK download) and
places it at `dng_sdk_1_7_1/` locally. See
[`THIRD-PARTY-NOTICES.md`](./THIRD-PARTY-NOTICES.md) for the licenses of all
in-repo and vendored dependencies, and
[`.github/copilot-instructions.md`](./.github/copilot-instructions.md) for
build commands for the native reference tool.

## License

MIT — see [`LICENSE`](./LICENSE). The vendored `dng_sdk_1_7_1/` tree (not
included in this repo) is governed by its own license terms; see
[`THIRD-PARTY-NOTICES.md`](./THIRD-PARTY-NOTICES.md).
