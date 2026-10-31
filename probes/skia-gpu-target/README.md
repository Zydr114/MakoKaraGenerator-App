# P0 Skia Vulkan target probe

This isolated probe establishes the Skia producer boundary for the Linux GPU
preview. It does not change the production renderer or claim a performance result.

The released SkiaSharp 3.119.4 managed API and native C ABI do not expose Vulkan
backend-image queries or mutable-state flush. `skia-vulkan-state.patch` adds only:

- `gr_mako_direct_context_flush_surface_vk_state`: request a known layout and
  queue family through Skia's `MutableTextureState`, then check synchronous submit.
- `sk_mako_surface_get_vk_image_info`: get a borrowed Vulkan backend image through
  Skia's public GetBackendTexture/GetBackendRenderTarget APIs. It rejects images
  with more than one sample. Drawing to or destroying the surface invalidates the
  borrowed info. The caller must retain the surface until external work completes.

The native library retains the complete original C ABI. The recorded symbol
comparison found no missing original symbols and exactly these two new symbols.
Borrowed shared images use WrapBackendRenderTarget, an empty allocation record,
and no adopt operation; the producer retains ownership of image and memory.

Run from this repository root:

```bash
python3 probes/skia-gpu-target/bootstrap.py
bash probes/skia-gpu-target/build-native.sh
bash probes/skia-gpu-target/build-plugins.sh
dotnet run --project probes/skia-gpu-target/SkiaGpuTargetProbe.csproj -c Release
clang++ -std=c++20 -O2 -mf16c probes/skia-gpu-target/half-intrinsic-probe.cc -o .local/skia-p0/half-intrinsic-probe
.local/skia-p0/half-intrinsic-probe
```

All downloaded sources, tools and build output stay in `.local/skia-p0`. No root
permission or system library installation is needed. The bootstrap verifies
source archive checksums, every expected existing source byte after the approved
patches, all exact DEPS revisions and the GN binary checksum. It refuses source
differences instead of resetting a source tree. Set `MAKO_BUILD_PROXY` to override
the default `http://127.0.0.1:7897`; set `MAKO_BUILD_JOBS` to override six jobs.

Source pins: SkiaSharp v3.119.4 (`f568ac94dd768ef9a2f593537cfde2dd0d348ef5`),
its mono/skia submodule `7dbfc07dd33181f84e0958afb7ee805c6c769f0b`, and GN
`fe330c0ae1ec29db30b6f830e50771a335e071fb`. Linux GN flags mirror the pinned
SkiaSharp `native/linux/build.cake`, with Ganesh and Vulkan enabled. The native ABI
version remains 119.0.0. The local ELF SONAME link and plugin `$ORIGIN` RUNPATH
avoid system installation and `LD_LIBRARY_PATH` changes.

The machine's Clang 23 cannot compile Skia's old direct LLVM F16C builtin.
`skcms-clang23.patch` substitutes the public `_mm256_cvtph_ps` intrinsic for the
same half-to-float conversion. The standalone exhaustive check covers all 65,536
binary16 bit patterns, including subnormals, signed zeros, infinities and NaN
payloads, against an independent IEEE binary32 bit expansion. Initial build
failure and the existing upstream memcpy warnings are recorded in
`evidence/native-build.json`; the plugin itself builds with warnings as errors.

`SkiaGpuTargetProbe.csproj` uses the fixed released NuGet packages and links the
repository's unmodified Vulkan device source. It performs 12 repeated draw,
TRANSFER_SRC flush, image-state query and pixel checks for each of null and sRGB
color spaces, with the production BottomLeft origin. Its 24 explicit pixel
readbacks are validation only, and are not part of the shared transport.

The native producer can load either plugin through its existing `--draw-plugin`
option. `libmako-skia-draw.so` directly wraps a borrowed exportable DRM-modifier
render target with TopLeft/sRGB, flushes to GENERAL/FOREIGN, synchronously submits,
and verifies the reported state before dropping the wrapper. That proves direct
target feasibility for this synthetic pattern; changing production to TopLeft
requires separate semantic acceptance.

`libmako-skia-blit.so` preserves an ordinary Skia BottomLeft GPU surface and uses
one nearest-filter GPU blit with a vertical flip into the shared target. It checks
optimal BGRA BLIT_SRC and DRM modifier 0 BLIT_DST capabilities, actual source
format/tiling/usage/sample/layout/queue, and keeps the source alive through GPU
completion. It leaves the source state unchanged from Skia's known TRANSFER_SRC
state. Its default source color space is null; `MAKO_P0_SKIA_COLORSPACE=srgb`
selects sRGB. This is the approved production candidate. Neither plugin calls a
pixel readback API. The producer records the actual zero/one copy count.

Both modes acquire a reused shared slot only after native consumer completion,
and release its ownership to FOREIGN in GENERAL layout. Queue-idle and synchronous
Skia submits are a P0 correctness baseline, not the future continuous-stream
publication protocol. The plugin shutdown runs while caller Vulkan handles are
alive; it releases the Skia context and command pool before device destruction.
Electron display evidence is maintained separately under the P0 evidence archive.
