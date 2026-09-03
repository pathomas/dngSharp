<#
.SYNOPSIS
  Generates golden reference outputs from the native Adobe dng_validate binary
  for every *.dng under dng_sdk_1_7_1/sample_files/.

.DESCRIPTION
  Looks for dng_validate.exe under the Windows Release64 output. Run from the
  repo root (or anywhere — paths are resolved against this script's location).
#>

[CmdletBinding()]
param(
    [string]$ValidateExe,
    [string]$SamplesDir,
    [string]$OutDir,
    # When set, only capture the fast `-v` verbose text dump. Skips the
    # stage-1/2/3, rendered, and round-trip DNG outputs (which pay the full
    # decode cost, and are extremely slow for the JXL samples even on
    # Release). Use this for the tier-1 golden diff (tag structure); rerun
    # without the switch to refresh the pixel goldens when needed.
    [switch]$VerboseOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')

if (-not $ValidateExe) {
    $ValidateExe = Join-Path $repoRoot 'dng_sdk_1_7_1\dng_sdk\targets\win\release64_x64\dng_validate.exe'
}
if (-not $SamplesDir) {
    $SamplesDir = Join-Path $repoRoot 'dng_sdk_1_7_1\sample_files'
}
if (-not $OutDir) {
    $OutDir = $PSScriptRoot
}

if (-not (Test-Path $ValidateExe)) {
    throw "dng_validate not found at $ValidateExe. Build the native solution first; see tests/golden/README.md."
}
if (-not (Test-Path $SamplesDir)) {
    throw "Samples not found at $SamplesDir."
}

$samples = Get-ChildItem -Path $SamplesDir -Filter *.dng -File
Write-Host "Found $($samples.Count) sample(s) under $SamplesDir" -ForegroundColor Cyan

foreach ($sample in $samples) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($sample.Name)
    $target = Join-Path $OutDir $name
    New-Item -ItemType Directory -Force -Path $target | Out-Null

    Write-Host "==> $name" -ForegroundColor Green

    & $ValidateExe -v $sample.FullName *> (Join-Path $target 'verbose.txt')
    if (-not $VerboseOnly) {
        & $ValidateExe -1 (Join-Path $target 'stage1.tif') $sample.FullName | Out-Null
        & $ValidateExe -2 (Join-Path $target 'stage2.tif') $sample.FullName | Out-Null
        & $ValidateExe -3 (Join-Path $target 'stage3.tif') $sample.FullName | Out-Null
        & $ValidateExe -tif (Join-Path $target 'rendered.tif') $sample.FullName | Out-Null
        & $ValidateExe -dng (Join-Path $target 'roundtrip.dng') $sample.FullName | Out-Null
    }
}

Write-Host "Done. Goldens written under $OutDir" -ForegroundColor Cyan
