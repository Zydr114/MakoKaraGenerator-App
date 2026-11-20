# Native media presentation helper

The existing Linux build uses `pkg-config` and the controlled FFmpeg 9 ABI
bundle. Windows is opt-in and supported on native Windows x64. No WSL,
installed `ffmpeg.exe`, or FFmpeg package on `PATH` is used.

| Input | Pin |
| --- | --- |
| FFmpeg source | [FFmpeg 9.0.2](https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz), SHA-256 `8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e` |
| zlib source | [zlib 1.4](https://github.com/madler/zlib/releases/download/v1.4/zlib-1.4.tar.gz), SHA-256 `02dd159783677a749c04adbdd02b55b8a2bcc7e054f51b76b549199a916ad584` |
| Compiler | [llvm-mingw 20261117 UCRT x64 zip](https://github.com/mstorsjo/llvm-mingw/releases/download/20261117/llvm-mingw-20261117-ucrt-x86_64.zip), SHA-256 `4d002e330a0f703625aa5ccbc37e96f8f824413e163bfc7336774aabd2c3a412` |
| GNU Make | [MSYS2 make 4.4.1-3](https://repo.msys2.org/msys/x86_64/make-4.4.1-3-x86_64.pkg.tar.zst), SHA-256 `af0bdba17f06fe037f0194069adaa31a8fe45f1a11381501896aea1fae37bd5d` |

Put CMake 3.24+, Ninja and `x86_64-w64-mingw32-clang` on `PATH`; devkit
preparation also needs GNU Make and the native Git for Windows or MSYS2
`bash.exe` and its utilities. The local build uses CMake 4.1.3 and Ninja
1.13.2. Put the two source archives in a local cache and run:

```powershell
./backend/native/media-presentation/build_windows_devkit.ps1 `
  -InputDirectory $cache -BuildDirectory "$cache/ffmpeg-win-build" `
  -OutputArchive "$cache/mako-ffmpeg-9.0.2-lgpl-win-x64.zip" -Bash $nativeBash
```

Pass that archive's absolute path with `-p:MakoMediaPresentationWindowsDevkitArchive=...`
and opt in with `-p:MakoMediaPresentationEnabled=true`. To use the compiler by
absolute path, set `MakoMediaPresentationWindowsCompiler`. The native build
requires archive SHA-256 `a707bed50c7d5ccb3f2d62ee229eadf515b7f6f40f783b9db60c72afb898b7bb`,
does not download dependencies,
and copies the helper, three DLLs, licenses, source archives and build recipe
into `media-tools/` for build and publish. Do not commit the archives,
extracted devkit or build output.

To build and run the native tests independently (Python 3.11+ is required
only for the Windows wire test), from the repository root:

```powershell
cmake -S backend/native/media-presentation -B "$env:TEMP/mkg-native" -G Ninja `
  -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=x86_64-w64-mingw32-clang `
  "-DMAKO_PRESENTATION_WINDOWS_DEVKIT_ARCHIVE=$archive"
cmake --build "$env:TEMP/mkg-native"
ctest --test-dir "$env:TEMP/mkg-native" --output-on-failure
```

`mkg-presentation.exe --closure` initializes the same loader as decode and
inspect. It never opens a media source and writes only type 4 records and a
type 5 terminator on stdout, then exits. Paths are canonical disk-backed DOS
paths (UTF-8, no `\\?\` prefix). The closure includes the executable and
Windows system modules, not just the runtime DLLs. DLL loads are explicit
absolute paths beside the executable, with transitive lookup restricted to
already loaded modules and System32. `apphelp.dll` is explicitly loaded from
System32 in both modes to eliminate first-launch AppCompat differences.
In decode and inspect mode, metadata is
written to the inherited pipe whose decimal Win32 `HANDLE` is passed in
`MAKO_MEDIA_METADATA_HANDLE`; stdout and stdin retain the raster/ACK protocol.

The native bundle dynamically loads a minimal LGPL 2.1-or-later FFmpeg build.
It uses zlib for PNG and the demuxers, and does not use GPL components,
hardware decoders, network protocols or the FFmpeg CLI. The delivered notices
and complete source for those libraries are separate from the app's licensing.
