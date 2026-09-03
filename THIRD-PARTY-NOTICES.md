# Third-Party Notices

This file catalogs the licenses of dependencies used by this project. It is
split into two sections:

1. **In-repo dependencies** — NuGet packages restored as part of building this
   repository's solution (`Dng.slnx`). Versions are centrally managed in
   `Directory.Packages.props`.
2. **External reference dependency** — the vendored Adobe DNG SDK 1.7.1 tree
   (`dng_sdk_1_7_1/`). **This tree is intentionally excluded from git**
   (see `.gitignore`) and is not distributed as part of this repository. Each
   contributor obtains it separately (e.g. from Adobe's DNG SDK download) and
   places it locally at `dng_sdk_1_7_1/` to run golden-file validation, the
   tag-code extraction script, and native `dng_validate` builds. Its license
   terms are documented here for awareness only — they govern that tree
   independently of this repository's own `LICENSE`.

## In-repo dependencies (NuGet)

| Package | License |
|---|---|
| xunit | MIT |
| xunit.runner.visualstudio | MIT |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |
| BenchmarkDotNet | MIT |
| SkiaSharp | MIT |
| SkiaSharp.NativeAssets.Win32 | MIT |
| SkiaSharp.NativeAssets.Linux | MIT |
| SkiaSharp.NativeAssets.macOS | MIT |
| System.CommandLine | MIT |

## External reference dependency (not tracked in git)

The vendored `dng_sdk_1_7_1/` tree is Adobe's official DNG SDK 1.7.1
distribution, which itself bundles several third-party libraries.

| Component | License | Location within `dng_sdk_1_7_1/` |
|---|---|---|
| Adobe DNG SDK (`dng_sdk`) | Adobe DNG SDK License Agreement (custom, permissive — see file) | `LICENSE.txt` |
| libjpeg (IJG) | Independent JPEG Group License | `libjpeg/README` |
| libjxl (JPEG XL reference implementation) | BSD-3-Clause | `libjxl/libjxl/libjxl/LICENSE` |
| brotli (libjxl third-party dependency) | MIT | `libjxl/libjxl/libjxl/third_party/brotli` |
| highway (libjxl third-party dependency) | Apache-2.0 | `libjxl/libjxl/libjxl/third_party/highway` |
| skcms (libjxl third-party dependency) | BSD-3-Clause | `libjxl/libjxl/libjxl/third_party/skcms` |
| Adobe XMP Toolkit | Adobe DNG SDK License Agreement (same distribution; no separate LICENSE file found in the toolkit tree) | `xmp/toolkit` |

### Adobe DNG SDK License Agreement — summary

Adobe grants a non-exclusive, worldwide, royalty-free license to use,
reproduce, modify, publicly display/perform, distribute and sublicense the
SDK software for any purpose, provided copyright/notice text is preserved.
Documentation may be copied for development purposes but not modified. See
`dng_sdk_1_7_1/LICENSE.txt` (local copy) for the full text.
