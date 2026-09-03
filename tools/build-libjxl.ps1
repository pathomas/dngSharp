<#
.SYNOPSIS
    Builds libjxl from the vendored source and places the shared library
    where Dng.Sdk.Jxl.csproj expects it.

.DESCRIPTION
    Mirrors the CI `build-libjxl` job so local development works without
    a GitHub Actions run.  Requires CMake and a C++ compiler (MSVC on
    Windows, clang/gcc on Linux/macOS).

.PARAMETER Rebuild
    Force a clean rebuild even when the output already exists.

.EXAMPLE
    pwsh tools/build-libjxl.ps1

.EXAMPLE
    pwsh tools/build-libjxl.ps1 -Rebuild
#>

[CmdletBinding()]
param([switch]$Rebuild)

$ErrorActionPreference = 'Stop'
$repoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..')
$libjxlSrc  = Join-Path $repoRoot 'dng_sdk_1_7_1\libjxl\libjxl'
$buildDir   = Join-Path $repoRoot 'libjxl-build'
$instDir    = Join-Path $repoRoot 'libjxl-inst'

# Detect RID
if ($IsWindows) {
    $rid     = 'win-x64'
    $libname = 'jxl.dll'
} elseif ($IsLinux) {
    $rid     = 'linux-x64'
    $libname = 'libjxl.so'
} else {
    # macOS — treat as arm64 (works for x64 too; adjust if needed)
    $rid     = 'osx-arm64'
    $libname = 'libjxl.dylib'
}

$outDir = Join-Path $repoRoot "src\Dng.Sdk.Jxl\runtimes\$rid\native"
$outLib = Join-Path $outDir $libname

if ((Test-Path $outLib) -and -not $Rebuild) {
    Write-Host "libjxl already built: $outLib (use -Rebuild to force)" -ForegroundColor Cyan
    return
}

Write-Host "Building libjxl for $rid ..." -ForegroundColor Cyan

$cmakeArgs = @(
    '-B', $buildDir,
    '-S', $libjxlSrc,
    '-DCMAKE_BUILD_TYPE=Release',
    '-DBUILD_SHARED_LIBS=ON',
    '-DBUILD_TESTING=OFF',
    '-DJPEGXL_ENABLE_TOOLS=OFF',
    '-DJPEGXL_ENABLE_TESTS=OFF',
    '-DJPEGXL_ENABLE_BENCHMARK=OFF',
    '-DJPEGXL_ENABLE_EXAMPLES=OFF',
    '-DJPEGXL_ENABLE_MANPAGES=OFF',
    '-DJPEGXL_ENABLE_JNI=OFF',
    '-DJPEGXL_ENABLE_JPEGLI=OFF',
    '-DJPEGXL_ENABLE_VIEWERS=OFF',
    '-DJPEGXL_ENABLE_SJPEG=OFF',
    '-DJPEGXL_FORCE_SYSTEM_BROTLI=OFF',
    '-DJPEGXL_FORCE_SYSTEM_HWY=OFF'
)

# On Windows use the VS 2022-bundled cmake (3.31+) which knows about the
# VS 17 2022 generator; fall back to the system cmake on other platforms.
if ($IsWindows) {
    # Use vswhere to find the VS cmake, which is the only cmake new enough to
    # support the "Visual Studio 17 2022" generator.
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vsInstallDir = $null
    if (Test-Path $vswhere) {
        $vsInstallDir = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath 2>$null
    }
    if ($vsInstallDir) {
        $cmake = Join-Path $vsInstallDir 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    }
    if (-not $cmake -or -not (Test-Path $cmake)) {
        # Search common VS installation drives as a fallback
        $cmake = @('C:', 'D:') |
            ForEach-Object { "$_\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
                             "$_\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
                             "$_\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
    }
    if (-not $cmake -or -not (Test-Path $cmake)) { $cmake = 'cmake' }   # last resort
    $cmakeArgs += '-G', 'Visual Studio 17 2022', '-A', 'x64'
} else {
    $cmake = 'cmake'
}

& $cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }

& $cmake --build $buildDir --config Release --parallel
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

& $cmake --install $buildDir --prefix $instDir --config Release
if ($LASTEXITCODE -ne 0) { throw "cmake install failed" }

# Find and copy the library to the project runtimes/ directory.
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if ($IsWindows) {
    # jxl.dll and jxl_cms.dll are installed to instDir/bin/.
    foreach ($dll in @('jxl.dll', 'jxl_cms.dll')) {
        $src = Join-Path $instDir "bin\$dll"
        if (-not (Test-Path $src)) { throw "Could not locate $dll under $instDir\bin" }
        Copy-Item $src (Join-Path $outDir $dll) -Force
    }
    # Brotli DLLs are in the build dir (not installed — only .libs are installed on Windows).
    $brotliRelDir = Join-Path $buildDir 'third_party\brotli\Release'
    foreach ($dll in @('brotlicommon.dll', 'brotlidec.dll', 'brotlienc.dll')) {
        $src = Join-Path $brotliRelDir $dll
        if (-not (Test-Path $src)) { throw "Could not locate $dll under $brotliRelDir" }
        Copy-Item $src (Join-Path $outDir $dll) -Force
    }
    Write-Host "Done: $outDir" -ForegroundColor Green
} elseif ($IsLinux) {
    $src = Get-ChildItem $instDir -Filter 'libjxl.so.*' -Recurse |
           Where-Object { -not $_.LinkType } |
           Select-Object -First 1
    if ($null -eq $src) { throw "Could not locate libjxl.so.* under $instDir" }
    Copy-Item $src.FullName $outLib -Force
    Write-Host "Done: $outLib" -ForegroundColor Green
} else {
    $src = Get-ChildItem $instDir -Filter 'libjxl.*.dylib' -Recurse |
           Where-Object { -not $_.LinkType } |
           Select-Object -First 1
    if ($null -eq $src) { throw "Could not locate libjxl.*.dylib under $instDir" }
    Copy-Item $src.FullName $outLib -Force
    Write-Host "Done: $outLib" -ForegroundColor Green
}
