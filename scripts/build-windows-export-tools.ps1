#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '../.local/windows-media-cache'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../.local/windows-export-tools'),
    [ValidateRange(1, 64)] [int]$Jobs = [Math]::Min(8, [Environment]::ProcessorCount)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'This recipe requires native x64 Windows.'
}

$pins = [ordered]@{
    'ffmpeg-946fcce07b.tar.gz' = @('86f82e82456005881d1b97e9ce02f8a40bf2d181c8c958f8ae736886fdba2a34', 'https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/946fcce07b')
    'x264-31e19f92.tar.gz' = @('d053c9d86988d6bc78237ca5205865c5ddf99c98ef4cd9927eec8f6d388f6dd9', 'https://code.videolan.org/videolan/x264/-/archive/31e19f92f00c7003fa115047ce50978bc98c3a0d/x264-31e19f92f00c7003fa115047ce50978bc98c3a0d.tar.gz')
    'zlib-1.4.tar.gz' = @('02dd159783677a749c04adbdd02b55b8a2bcc7e054f51b76b549199a916ad584', 'https://zlib.net/fossils/zlib-1.4.tar.gz')
    'llvm-mingw-20261117-ucrt-x86_64.zip' = @('4d002e330a0f703625aa5ccbc37e96f8f824413e163bfc7336774aabd2c3a412', 'https://github.com/mstorsjo/llvm-mingw/releases/download/20261117/llvm-mingw-20261117-ucrt-x86_64.zip')
    'msys2-base-x86_64-20260927.tar.xz' = @('ea2f31a0b6ade63914ce441ffb022f0f6aa96982bfefa2326460a26d5fb01322', 'https://repo.msys2.org/distrib/x86_64/msys2-base-x86_64-20260927.tar.xz')
    'make.tar.zst' = @('af0bdba17f06fe037f0194069adaa31a8fe45f1a11381501896aea1fae37bd5d', 'https://repo.msys2.org/msys/x86_64/make-4.4.1-3-x86_64.pkg.tar.zst')
    'nasm.tar.zst' = @('1ab3d9d7f86f57a66cc506799648527b9215c3eb1b7bd404d886d59b503a05b7', 'https://repo.msys2.org/msys/x86_64/nasm-3.02-1-x86_64.pkg.tar.zst')
    'pkgconf.tar.zst' = @('38efd4928ac0cb06e9bd558baa533fc923c0592115bf9bd44970250f3ae2cb7e', 'https://repo.msys2.org/msys/x86_64/pkgconf-2.5.1-1-x86_64.pkg.tar.zst')
}

function Assert-Pin([string]$Path, [string]$Hash, [string]$Url) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Hash) {
        throw "Missing or SHA-256 mismatch: $Path. Expected $Hash from $Url"
    }
}

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command exited with code $LASTEXITCODE" }
}

$cache = (Resolve-Path -LiteralPath $CacheDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory, (Get-Location).Path)
if (Test-Path -LiteralPath $output) { throw "Output already exists: $output" }
foreach ($name in $pins.Keys) {
    Assert-Pin (Join-Path $cache $name) @($pins[$name])[0] @($pins[$name])[1]
}

$work = Join-Path (Split-Path -Parent $output) '.windows-export-work' ([Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
$recipeSnapshot = Join-Path $work 'build-windows-export-tools.ps1'
Copy-Item -LiteralPath $PSCommandPath -Destination $recipeSnapshot
Write-Host "[windows-export-tools] Work directory: $work"
$tar = Join-Path $env:SystemRoot 'System32/tar.exe'
$msys = Join-Path $work 'msys64'
$toolchain = Join-Path $work 'llvm-mingw-20261117-ucrt-x86_64'
$ffmpegSource = Join-Path $work 'FFmpeg-946fcce07b'
$x264Source = Join-Path $work 'x264-31e19f92f00c7003fa115047ce50978bc98c3a0d'
$zlibSource = Join-Path $work 'zlib-1.4'
foreach ($name in $pins.Keys) {
    Write-Host "[windows-export-tools] Extracting $name"
    if ($name.EndsWith('.tar.zst')) {
        Invoke-Checked $tar @('-xf', (Join-Path $cache $name), '-C', $msys, 'usr')
    } else {
        Invoke-Checked $tar @('-xf', (Join-Path $cache $name), '-C', $work)
    }
}

$recipe = @'
set -euo pipefail
export LC_ALL=C TZ=UTC SOURCE_DATE_EPOCH=0
export PATH="$PWD/llvm-mingw-20261117-ucrt-x86_64/bin:/usr/bin"
unset CFLAGS CPPFLAGS CXXFLAGS LDFLAGS PKG_CONFIG_PATH PKG_CONFIG_SYSROOT_DIR \
    MAKEFLAGS MFLAGS MAKEFILES CC CXX AR AS LD NM RANLIB STRIP RC CL C_INCLUDE_PATH \
    CPLUS_INCLUDE_PATH CPATH LIBRARY_PATH INCLUDE LIB LIBPATH
target=x86_64-w64-mingw32
jobs="$1"

printf '%s\n' '[windows-export-tools] Building static zlib'
cd zlib-1.4
make -j"$jobs" -f win32/Makefile.gcc CC="$target-clang" AR=llvm-ar libz.a
cd ..

printf '%s\n' '[windows-export-tools] Building static x264'
cd x264-31e19f92f00c7003fa115047ce50978bc98c3a0d
CC="$target-clang" AR=llvm-ar RANLIB=llvm-ranlib ./configure \
    --host="$target" --cross-prefix="$target-" --prefix=../x264-install \
    --enable-static --disable-cli --disable-opencl --disable-avs --disable-lavf \
    --disable-ffms --disable-gpac --disable-lsmash --disable-swscale
make -j"$jobs"
make install-lib-static
cd ..

printf '%s\n' '[windows-export-tools] Configuring GPL FFmpeg and FFprobe'
cd FFmpeg-946fcce07b
export PKG_CONFIG_LIBDIR=../x264-install/lib/pkgconfig
./configure --arch=x86_64 --target-os=mingw32 --enable-cross-compile \
    --cc="$target-clang" --cxx="$target-clang++" --ar=llvm-ar --nm=llvm-nm \
    --ranlib=llvm-ranlib --strip=llvm-strip --windres="$target-windres" \
    --disable-autodetect --enable-static --disable-shared --disable-pthreads \
    --enable-w32threads --enable-gpl --enable-libx264 --enable-zlib --disable-ffplay \
    --disable-avdevice --disable-network --disable-doc --disable-debug \
    --pkg-config-flags=--static --extra-cflags=-I../zlib-1.4 \
    --extra-ldflags='-L../zlib-1.4 -static -Wl,--no-insert-timestamp'
printf '%s\n' '[windows-export-tools] Compiling FFmpeg and FFprobe'
make -j"$jobs" ffmpeg.exe ffprobe.exe
'@

$recipePath = Join-Path $work 'compile.sh'
[IO.File]::WriteAllText($recipePath, ($recipe -replace "`r`n", "`n") + "`n")
Push-Location -LiteralPath $work
try {
    Invoke-Checked (Join-Path $msys 'usr/bin/bash.exe') @('--noprofile', '--norc', './compile.sh', "$Jobs")
} finally {
    Pop-Location
}

$readobj = Join-Path $toolchain 'bin/llvm-readobj.exe'
$systemDlls = '^(?:api-ms-win-[\w-]+|advapi32|bcrypt|crypt32|gdi32|kernel32|msvcrt|ntdll|ole32|oleaut32|psapi|secur32|shell32|shlwapi|ucrtbase|user32|version|winmm|ws2_32)\.dll$'
$stage = New-Item -ItemType Directory -Path (Join-Path $work 'media-tools')
$evidence = foreach ($tool in 'ffmpeg', 'ffprobe') {
    $binary = Join-Path $ffmpegSource "$tool.exe"
    $inspection = (Invoke-Checked $readobj @('--file-headers', '--coff-imports', $binary)) -join "`n"
    $imports = @([regex]::Matches($inspection, '(?m)^\s*Name: (\S+\.dll)\s*$') | ForEach-Object { $_.Groups[1].Value })
    if ($inspection -notmatch 'Machine: IMAGE_FILE_MACHINE_AMD64' -or -not $imports.Count -or
        ($imports | Where-Object { $_ -notmatch $systemDlls })) {
        throw "Unexpected architecture or non-system DLL imports for ${tool}: $($imports -join ', ')"
    }
    $version = Invoke-Checked $binary @('-version')
    $license = Invoke-Checked $binary @('-L')
    if (($license -join "`n") -notmatch 'GNU General Public License' -or
        ($version -join "`n") -notmatch '(?:^| )--enable-libx264(?: |$)') {
        throw "Expected a GPL libx264 build of $tool"
    }
    Copy-Item -LiteralPath $binary -Destination $stage.FullName
    "$tool.exe Windows imports: $($imports -join ', ')"
    $version
    ''
}

$notices = [ordered]@{
    'FFmpeg-LICENSE.txt' = (Join-Path $ffmpegSource 'LICENSE.md')
    'FFmpeg-COPYING.GPLv2.txt' = (Join-Path $ffmpegSource 'COPYING.GPLv2')
    'x264-COPYING.txt' = (Join-Path $x264Source 'COPYING')
    'zlib-LICENSE.txt' = (Join-Path $zlibSource 'LICENSE')
    'LLVM-LICENSE.txt' = (Join-Path $toolchain 'LICENSE.TXT')
    'MinGW-w64-runtime-LICENSE.txt' = (Join-Path $toolchain 'x86_64-w64-mingw32/share/mingw32/COPYING.MinGW-w64-runtime.txt')
}
foreach ($name in $notices.Keys) {
    Copy-Item -LiteralPath $notices[$name] -Destination (Join-Path $stage.FullName $name)
}

$sources = @('ffmpeg-946fcce07b.tar.gz', 'x264-31e19f92.tar.gz', 'zlib-1.4.tar.gz')
foreach ($name in $sources) {
    $target = Join-Path $stage.FullName $name
    Copy-Item -LiteralPath (Join-Path $cache $name) -Destination $target
    Assert-Pin $target @($pins[$name])[0] @($pins[$name])[1]
}
Copy-Item -LiteralPath $recipeSnapshot -Destination $stage.FullName

$provenance = @(
    'MakoKaraGenerator Windows x64 Portable: standalone FFmpeg and FFprobe'
    ''
    'The application launches these executables as separate processes; it does not link to them.'
    'FFmpeg (946fcce07b6dcd0331c8cc609192aeff5e1924f8) with x264 (31e19f92f00c7003fa115047ce50978bc98c3a0d) is GPL version 2 or later.'
    'See FFmpeg-LICENSE.txt, FFmpeg-COPYING.GPLv2.txt, x264-COPYING.txt, and zlib-LICENSE.txt.'
    'LLVM-LICENSE.txt and MinGW-w64-runtime-LICENSE.txt cover the toolchain runtime components.'
    'The backend media-presentation helper is a separate build with its own notices.'
    ''
    'The exact, unmodified corresponding source archives for FFmpeg, x264, and zlib are in this directory.'
    'Build on x64 Windows with PowerShell 7 using this directory and the pinned build inputs below:'
    '  pwsh -NoProfile -File .\build-windows-export-tools.ps1 -CacheDirectory <cache> -OutputDirectory <new-output-dir>'
    'Put all eight archives (including the three supplied source archives) in <cache> using the listed names.'
    'The recipe contains the compiler, linker and configure settings and does not need WSL or network access.'
    ''
    'Pinned build inputs (SHA-256, filename, origin):'
    foreach ($name in $pins.Keys) { "$($pins[$name][0])  $name  $($pins[$name][1])" }
    ''
    $evidence
)
Set-Content -LiteralPath (Join-Path $stage.FullName 'FFmpeg-Provenance.txt') -Value $provenance -Encoding utf8

$hashes = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $stage.FullName -File | Sort-Object Name) {
    $hashes[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
@{ schemaVersion = 1; target = 'windows-x64'; files = $hashes } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $stage.FullName 'media-bundle.json') -Encoding utf8
[IO.Directory]::Move($stage.FullName, $output)
Write-Host "[windows-export-tools] Complete bundle: $output"
