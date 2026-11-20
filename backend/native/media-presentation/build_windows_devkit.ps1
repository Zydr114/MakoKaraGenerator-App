param(
    [Parameter(Mandatory)][string] $InputDirectory,
    [Parameter(Mandatory)][string] $BuildDirectory,
    [Parameter(Mandatory)][string] $OutputArchive,
    [Parameter(Mandatory)][string] $Bash,
    [ValidateRange(1, 64)][int] $Jobs = 8
)

$ErrorActionPreference = 'Stop'

function Invoke-Checked([string] $Executable, [string[]] $Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

function Get-VerifiedSource([string] $Name, [string] $Sha256) {
    $source = Join-Path $InputDirectory $Name
    if (!(Test-Path -LiteralPath $source -PathType Leaf) -or (Get-FileHash -LiteralPath $source).Hash -ne $Sha256) {
        throw "Missing or incorrect pinned source archive: $Name"
    }
    return (Resolve-Path -LiteralPath $source).Path
}

if ((Invoke-Checked $Bash @('-c', 'uname -s')) -notmatch '^(MSYS|MINGW)') {
    throw 'Use a native Windows MSYS2 or Git for Windows bash.exe, not WSL.'
}
if ((Invoke-Checked 'x86_64-w64-mingw32-clang' @('--version') | Select-Object -First 1) -notmatch '5b10d2d74929eae78912771222c9855af97496f4') {
    throw 'Use the pinned llvm-mingw 20261117 toolchain.'
}
$ffmpeg = Get-VerifiedSource 'ffmpeg-9.0.2.tar.xz' '8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e'
$zlib = Get-VerifiedSource 'zlib-1.4.tar.gz' '02dd159783677a749c04adbdd02b55b8a2bcc7e054f51b76b549199a916ad584'
$OutputArchive = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputArchive)
if (Test-Path -LiteralPath $OutputArchive) { throw 'Choose a new output archive path.' }
if (Test-Path -LiteralPath $BuildDirectory) { throw 'Choose a new build directory for the pinned sources.' }
$buildRoot = (New-Item -ItemType Directory -Path $BuildDirectory).FullName

Push-Location -LiteralPath $buildRoot
try {
    Invoke-Checked 'cmake' @('-E', 'tar', 'xf', $ffmpeg)
    Invoke-Checked 'cmake' @('-E', 'tar', 'xf', $zlib)
    Invoke-Checked 'cmake' @('-S', 'zlib-1.4', '-B', 'zlib-build', '-G', 'Ninja', '-DCMAKE_BUILD_TYPE=Release',
        '-DCMAKE_C_COMPILER=x86_64-w64-mingw32-clang', '-DZLIB_BUILD_TESTING=OFF', '-DZLIB_BUILD_SHARED=OFF',
        '-DZLIB_BUILD_STATIC=ON', "-DCMAKE_INSTALL_PREFIX=$buildRoot/zlib-install")
    Invoke-Checked 'cmake' @('--build', 'zlib-build', '--target', 'install', '--parallel', $Jobs)
    Copy-Item -LiteralPath 'zlib-install/lib/libzs.a' -Destination 'zlib-install/lib/libz.a'

    Push-Location -LiteralPath 'ffmpeg-9.0.2'
    try {
        $configure = @(
            './configure --target-os=mingw32 --arch=x86_64 --cc=x86_64-w64-mingw32-clang',
            '--ar=llvm-ar --nm=llvm-nm --ranlib=llvm-ranlib --strip=llvm-strip --windres=x86_64-w64-mingw32-windres',
            '--prefix=../ffmpeg-install --enable-shared --disable-static --disable-autodetect --disable-everything',
            '--disable-programs --disable-doc --disable-debug --disable-network --disable-avdevice --disable-avfilter',
            '--disable-swscale --disable-swresample --disable-x86asm --enable-zlib',
            '--extra-cflags=-I../zlib-install/include --extra-ldflags="-L../zlib-install/lib -Wl,--no-insert-timestamp"',
            '--enable-decoder=h264,hevc,mpeg4,vp8,vp9,ffv1,png,mjpeg --enable-demuxer=mov,matroska,image_png_pipe,image_jpeg_pipe'
        ) -join ' '
        Invoke-Checked $Bash @('-c', "$configure && make -j $Jobs && make install")
    } finally { Pop-Location }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $package = [IO.Compression.ZipFile]::Open($OutputArchive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $inputs = [ordered]@{
            'bin/avutil-61.dll' = 'ffmpeg-install/bin/avutil-61.dll'
            'bin/avcodec-63.dll' = 'ffmpeg-install/bin/avcodec-63.dll'
            'bin/avformat-63.dll' = 'ffmpeg-install/bin/avformat-63.dll'
            'FFmpeg-LICENSE.txt' = 'ffmpeg-9.0.2/COPYING.LGPLv2.1'
            'zlib-LICENSE.txt' = 'zlib-1.4/LICENSE'
            'ffmpeg-9.0.2.tar.xz' = $ffmpeg
            'zlib-1.4.tar.gz' = $zlib
        }
        $include = (Resolve-Path -LiteralPath 'ffmpeg-install/include').Path
        $paths = @(Get-ChildItem -LiteralPath $include -Recurse -File | ForEach-Object FullName)
        [Array]::Sort($paths, [StringComparer]::Ordinal)
        foreach ($header in $paths) {
            $inputs['include/' + $header.Substring($include.Length + 1).Replace('\', '/')] = $header
        }
        foreach ($item in $inputs.GetEnumerator()) {
            $path = (Resolve-Path -LiteralPath $item.Value).Path
            $entry = $package.CreateEntry($item.Key, [IO.Compression.CompressionLevel]::NoCompression)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $from = [IO.File]::OpenRead($path)
            try {
                $to = $entry.Open()
                try { $from.CopyTo($to) } finally { $to.Dispose() }
            } finally { $from.Dispose() }
        }
    } finally { $package.Dispose() }
    Get-FileHash -LiteralPath $OutputArchive
} finally { Pop-Location }
