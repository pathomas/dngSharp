# Golden-file harness

Captures reference outputs from Adobe's native `dng_validate` binary so the .NET
port can be diffed bit-for-bit (integer) or ULP-bounded (float) against ground
truth.

## Layout

```
tests/golden/
  README.md                 (this file)
  capture.ps1               regenerates everything below
  <sample>/
    verbose.txt             output of: dng_validate -v <sample>.dng
    stage1.tif              output of: dng_validate -1 stage1.tif <sample>.dng
    stage2.tif              output of: dng_validate -2 stage2.tif <sample>.dng
    stage3.tif              output of: dng_validate -3 stage3.tif <sample>.dng
    rendered.tif            output of: dng_validate -tif rendered.tif <sample>.dng
    roundtrip.dng           output of: dng_validate -dng roundtrip.dng <sample>.dng
```

## Regenerating

1. Build the native CLI (see `/.github/copilot-instructions.md` or
   `PORTING_PLAN.md`):

   ```powershell
   $msbuild = "D:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
   & $msbuild dng_sdk_1_7_1\dng_sdk\projects\win\dng_validate.sln `
       /p:Configuration="Validate Release" /p:Platform=x64 `
       /p:PlatformToolset=v143 /p:LanguageStandard=stdcpp17 /m
   ```

2. Run the capture script:

   ```powershell
   pwsh tests/golden/capture.ps1
   ```

   It looks for `dng_validate.exe` under
   `dng_sdk_1_7_1\dng_sdk\targets\win\release64_x64\` and walks every `*.dng`
   under `dng_sdk_1_7_1\sample_files\`.

Goldens for the bundled Adobe sample files (`01`–`14`) are committed so CI can
diff against them without rebuilding the native CLI on every run; refresh them
locally with `capture.ps1` when the native SDK version changes.

## Synthetic fixtures for opcodes with no matching sample file

None of the bundled `sample_files/*.dng` exercise `FixVignetteRadial`,
`WarpFisheye`, `WarpRectilinear2`, or `GainMap` (only `WarpRectilinear`, via
`03_jxl_bayer_raw_integer.dng`). For those, a synthetic DNG with a
hand-encoded opcode list is generated instead of relying on a real sample:

```
tests/golden/15_synthetic_fixvignetteradial/
  sample.dng    32x32 gradient DNG with a FixVignetteRadial opcode in
                OpcodeList2 (k0=0.3, center=(0.5,0.5))
  stage2.tif    native `dng_validate -2 stage2.tif sample.dng` capture
```

Regenerate `sample.dng` with:

```powershell
dotnet run --project tools/SyntheticImageGenerator -c Release -- --golden-opcodes
```

`tools/SyntheticImageGenerator/Program.cs`'s `WriteGoldenOpcodeFixtures()` builds
the DNG using `tests/Dng.Sdk.Tests/TestImages/OpcodeListTestBuilder.cs`, which
encodes an arbitrary opcode-list byte blob (`Build(params Entry[])`) plus a
`BuildFixVignetteRadialBody(...)` helper for that specific opcode's wire
format. To add coverage for `WarpFisheye`, `WarpRectilinear2`, or `GainMap`,
add a matching `Build<Opcode>Body(...)` helper, extend
`WriteGoldenOpcodeFixtures()` to emit a new `tests/golden/16_synthetic_.../`
fixture, capture its native output the same way, and add a test in
`tests/Dng.Sdk.Tests/Golden/GoldenSyntheticOpcodeDiffTests.cs` following
`Stage2_fixvignetteradial_matches_native()` (run the opcode through whichever
applier stage the opcode lives in — List2 for Stage 2, List3 for Stage 3 —
and diff against the captured `.tif`).
