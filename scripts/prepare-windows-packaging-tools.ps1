#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '../.local/windows-media-cache'),
    [string]$ExportToolsDirectory = (Join-Path $PSScriptRoot '../.local/windows-export-tools'),
    [string]$DevkitDirectory = (Join-Path $PSScriptRoot '../.local/windows-media-devkit'),
    [string]$ToolchainDirectory = (Join-Path $PSScriptRoot '../.local/windows-toolchain'),
    [ValidateRange(1, 64)][int]$Jobs = [Math]::Min(8, [Environment]::ProcessorCount)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'Windows packaging tool preparation requires native x64 Windows.'
}

$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory, $RepoRoot)
$ExportToolsDirectory = [IO.Path]::GetFullPath($ExportToolsDirectory, $RepoRoot)
$DevkitDirectory = [IO.Path]::GetFullPath($DevkitDirectory, $RepoRoot)
$ToolchainDirectory = [IO.Path]::GetFullPath($ToolchainDirectory, $RepoRoot)

$ExpectedDevkitSha256 = 'a707bed50c7d5ccb3f2d62ee229eadf515b7f6f40f783b9db60c72afb898b7bb'
$DevkitArchive = Join-Path $DevkitDirectory 'mako-ffmpeg-9.0.2-lgpl-win-x64.zip'
$LlvmMingwDir = Join-Path $ToolchainDirectory 'llvm-mingw-20261117-ucrt-x86_64'
$LlvmMingwBin = Join-Path $LlvmMingwDir 'bin'
$CompilerPath = Join-Path $LlvmMingwBin 'x86_64-w64-mingw32-clang.exe'
$TarExe = Join-Path $env:SystemRoot 'System32/tar.exe'
$CurlExe = Join-Path $env:SystemRoot 'System32/curl.exe'

$Pins = [ordered]@{
    'ffmpeg-946fcce07b.tar.gz' = @{
        Sha256 = '86f82e82456005881d1b97e9ce02f8a40bf2d181c8c958f8ae736886fdba2a34'
        Urls   = @('https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/946fcce07b')
    }
    'x264-31e19f92.tar.gz' = @{
        Sha256 = 'd053c9d86988d6bc78237ca5205865c5ddf99c98ef4cd9927eec8f6d388f6dd9'
        Urls   = @('https://code.videolan.org/videolan/x264/-/archive/31e19f92f00c7003fa115047ce50978bc98c3a0d/x264-31e19f92f00c7003fa115047ce50978bc98c3a0d.tar.gz')
    }
    'zlib-1.4.tar.gz' = @{
        Sha256 = '02dd159783677a749c04adbdd02b55b8a2bcc7e054f51b76b549199a916ad584'
        Urls   = @(
            'https://github.com/madler/zlib/releases/download/v1.4/zlib-1.4.tar.gz',
            'https://zlib.net/fossils/zlib-1.4.tar.gz'
        )
    }
    'llvm-mingw-20261117-ucrt-x86_64.zip' = @{
        Sha256 = '4d002e330a0f703625aa5ccbc37e96f8f824413e163bfc7336774aabd2c3a412'
        Urls   = @('https://github.com/mstorsjo/llvm-mingw/releases/download/20261117/llvm-mingw-20261117-ucrt-x86_64.zip')
    }
    'msys2-base-x86_64-20260927.tar.xz' = @{
        Sha256 = 'ea2f31a0b6ade63914ce441ffb022f0f6aa96982bfefa2326460a26d5fb01322'
        Urls   = @('https://repo.msys2.org/distrib/x86_64/msys2-base-x86_64-20260927.tar.xz')
    }
    'make.tar.zst' = @{
        Sha256 = 'af0bdba17f06fe037f0194069adaa31a8fe45f1a11381501896aea1fae37bd5d'
        Urls   = @('https://repo.msys2.org/msys/x86_64/make-4.4.1-3-x86_64.pkg.tar.zst')
    }
    'nasm.tar.zst' = @{
        Sha256 = '1ab3d9d7f86f57a66cc506799648527b9215c3eb1b7bd404d886d59b503a05b7'
        Urls   = @('https://repo.msys2.org/msys/x86_64/nasm-3.02-1-x86_64.pkg.tar.zst')
    }
    'pkgconf.tar.zst' = @{
        Sha256 = '38efd4928ac0cb06e9bd558baa533fc923c0592115bf9bd44970250f3ae2cb7e'
        Urls   = @('https://repo.msys2.org/msys/x86_64/pkgconf-2.5.1-1-x86_64.pkg.tar.zst')
    }
    'ffmpeg-9.0.2.tar.xz' = @{
        Sha256 = '8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e'
        Urls   = @('https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz')
    }
}

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable failed with exit code $LASTEXITCODE."
    }
}

function Ensure-PinnedArchive([string]$Name) {
    $null = New-Item -ItemType Directory -Path $CacheDirectory -Force
    $entry = $Pins[$Name]
    $expected = $entry.Sha256.ToLowerInvariant()
    $target = Join-Path $CacheDirectory $Name

    if (Test-Path -LiteralPath $target -PathType Leaf) {
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -eq $expected) {
            return $target
        }
        Write-Host "[windows-packaging-tools] Cached $Name hash mismatch ($actual); re-downloading."
        Remove-Item -LiteralPath $target -Force
    }

    $part = "$target.part"
    if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force }

    foreach ($url in $entry.Urls) {
        Write-Host "[windows-packaging-tools] Downloading $Name from $url"
        $curlArgs = @('--fail', '--location', '--retry', '3', '--connect-timeout', '20', '--max-time', '300', $url, '-o', $part)
        & $CurlExe @curlArgs
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $part -PathType Leaf)) {
            $actual = (Get-FileHash -LiteralPath $part -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -eq $expected) {
                Move-Item -LiteralPath $part -Destination $target -Force
                return $target
            }
            Write-Warning "[windows-packaging-tools] SHA-256 mismatch from $url (got $actual, expected $expected)"
            Remove-Item -LiteralPath $part -Force
        }
    }

    throw "Failed to fetch verified archive $Name (expected SHA-256 $expected)."
}

function Find-NinjaDirectory() {
    $existing = Get-Command ninja -ErrorAction SilentlyContinue
    if ($existing) {
        return Split-Path -Parent $existing.Source
    }
    $pf86 = ${env:ProgramFiles(x86)}
    if ($pf86) {
        $vswhere = Join-Path $pf86 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
            $vsPath = (& $vswhere -latest -products * -property installationPath | Select-Object -First 1)
            if ($vsPath) {
                $vsNinja = Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
                if (Test-Path -LiteralPath $vsNinja -PathType Leaf) {
                    return Split-Path -Parent $vsNinja
                }
            }
        }
    }
    Write-Host '[windows-packaging-tools] Installing ninja via chocolatey...'
    Invoke-Checked 'choco' @('install', 'ninja', '--no-progress', '-y')
    $installed = Get-Command ninja -ErrorAction SilentlyContinue
    if ($installed) {
        return Split-Path -Parent $installed.Source
    }
    $chocoRoot = if ($env:ChocolateyInstall) { $env:ChocolateyInstall } else { 'C:\ProgramData\chocolatey' }
    $chocoBin = Join-Path $chocoRoot 'bin'
    if (Test-Path -LiteralPath (Join-Path $chocoBin 'ninja.exe') -PathType Leaf) {
        return $chocoBin
    }
    throw 'Unable to locate ninja.exe after installation.'
}

function Test-ExportToolsValid() {
    if (-not (Test-Path -LiteralPath (Join-Path $ExportToolsDirectory 'media-bundle.json') -PathType Leaf)) {
        return $false
    }
    $verifyScript = @'
import { windowsMediaInputs } from './scripts/package-windows-media.mjs';
windowsMediaInputs(process.cwd(), { 'ffmpeg-dir': process.argv[1] });
'@
    Push-Location -LiteralPath $RepoRoot
    try {
        & node --input-type=module -e $verifyScript $ExportToolsDirectory 2>$null
        return ($LASTEXITCODE -eq 0)
    } catch {
        return $false
    } finally {
        Pop-Location
    }
}

function Test-DevkitValid() {
    if (-not (Test-Path -LiteralPath $DevkitArchive -PathType Leaf)) {
        return $false
    }
    $actual = (Get-FileHash -LiteralPath $DevkitArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    return ($actual -eq $ExpectedDevkitSha256)
}

$ninjaDir = Find-NinjaDirectory
if ($ninjaDir -and -not ($env:PATH.Split(';') -contains $ninjaDir)) {
    $env:PATH = "$ninjaDir;$env:PATH"
}

# 1. Ensure llvm-mingw compiler is extracted (needed both to build devkit and by dotnet publish).
if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    $llvmArchive = Ensure-PinnedArchive 'llvm-mingw-20261117-ucrt-x86_64.zip'
    $null = New-Item -ItemType Directory -Path $ToolchainDirectory -Force
    if (Test-Path -LiteralPath $LlvmMingwDir) {
        Remove-Item -LiteralPath $LlvmMingwDir -Recurse -Force
    }
    Write-Host "[windows-packaging-tools] Extracting llvm-mingw toolchain to $ToolchainDirectory"
    Invoke-Checked $TarExe @('-xf', $llvmArchive, '-C', $ToolchainDirectory)
    if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
        throw "Extracted llvm-mingw toolchain is missing $CompilerPath"
    }
} else {
    Write-Host "[windows-packaging-tools] Reusing cached llvm-mingw toolchain at $LlvmMingwDir"
}

# 2. Ensure Windows export media tools bundle (GPL FFmpeg + x264) is built and pinned.
if (Test-ExportToolsValid) {
    Write-Host "[windows-packaging-tools] Reusing verified Windows export tools at $ExportToolsDirectory"
} else {
    foreach ($name in @(
        'ffmpeg-946fcce07b.tar.gz',
        'x264-31e19f92.tar.gz',
        'zlib-1.4.tar.gz',
        'llvm-mingw-20261117-ucrt-x86_64.zip',
        'msys2-base-x86_64-20260927.tar.xz',
        'make.tar.zst',
        'nasm.tar.zst',
        'pkgconf.tar.zst'
    )) {
        $null = Ensure-PinnedArchive $name
    }
    if (Test-Path -LiteralPath $ExportToolsDirectory) {
        Remove-Item -LiteralPath $ExportToolsDirectory -Recurse -Force
    }
    Write-Host '[windows-packaging-tools] Building Windows export tools bundle...'
    & (Join-Path $PSScriptRoot 'build-windows-export-tools.ps1') `
        -CacheDirectory $CacheDirectory `
        -OutputDirectory $ExportToolsDirectory `
        -Jobs $Jobs
    if (-not (Test-ExportToolsValid)) {
        throw "Built Windows export tools at $ExportToolsDirectory failed pin validation."
    }
}

# 3. Ensure LGPL FFmpeg 9.0.2 Windows devkit archive is built and matches the pinned SHA-256.
if (Test-DevkitValid) {
    Write-Host "[windows-packaging-tools] Reusing verified Windows LGPL FFmpeg 9.0.2 devkit at $DevkitArchive"
} else {
    foreach ($name in @(
        'ffmpeg-9.0.2.tar.xz',
        'zlib-1.4.tar.gz',
        'msys2-base-x86_64-20260927.tar.xz',
        'make.tar.zst'
    )) {
        $null = Ensure-PinnedArchive $name
    }

    $null = New-Item -ItemType Directory -Path $DevkitDirectory -Force
    if (Test-Path -LiteralPath $DevkitArchive) {
        Remove-Item -LiteralPath $DevkitArchive -Force
    }

    $work = Join-Path $DevkitDirectory ('.devkit-work-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $work -Force
    $savedPath = $env:PATH
    try {
        $msys = Join-Path $work 'msys64'
        Invoke-Checked $TarExe @('-xf', (Join-Path $CacheDirectory 'msys2-base-x86_64-20260927.tar.xz'), '-C', $work)
        Invoke-Checked $TarExe @('-xf', (Join-Path $CacheDirectory 'make.tar.zst'), '-C', $msys, 'usr')
        $msysBin = Join-Path $msys 'usr/bin'
        $bashExe = Join-Path $msysBin 'bash.exe'
        $buildDir = Join-Path $work 'ffmpeg-win-build'

        $env:PATH = "$LlvmMingwBin;$ninjaDir;$msysBin;$savedPath"
        Write-Host '[windows-packaging-tools] Building Windows LGPL FFmpeg 9.0.2 devkit...'
        & (Join-Path $RepoRoot 'backend/native/media-presentation/build_windows_devkit.ps1') `
            -InputDirectory $CacheDirectory `
            -BuildDirectory $buildDir `
            -OutputArchive $DevkitArchive `
            -Bash $bashExe `
            -Jobs $Jobs
    } finally {
        $env:PATH = $savedPath
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-DevkitValid)) {
        $actual = (Get-FileHash -LiteralPath $DevkitArchive -Algorithm SHA256).Hash.ToLowerInvariant()
        throw "Built Windows devkit SHA-256 mismatch: got $actual, expected $ExpectedDevkitSha256."
    }
}

# Export environment variables & PATH for GitHub Actions subsequent steps.
if ($env:GITHUB_ENV) {
    "MakoMediaPresentationWindowsDevkitArchive=$DevkitArchive" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
    "MakoMediaPresentationWindowsCompiler=$CompilerPath" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
}
if ($env:GITHUB_PATH) {
    $LlvmMingwBin | Out-File -FilePath $env:GITHUB_PATH -Encoding utf8 -Append
    if ($ninjaDir) {
        $ninjaDir | Out-File -FilePath $env:GITHUB_PATH -Encoding utf8 -Append
    }
}

Write-Host "[windows-packaging-tools] Ready:"
Write-Host "  Export tools: $ExportToolsDirectory"
Write-Host "  Media devkit: $DevkitArchive"
Write-Host "  Compiler:     $CompilerPath"
