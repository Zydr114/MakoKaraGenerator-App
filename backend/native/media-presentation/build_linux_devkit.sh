#!/usr/bin/env bash
set -euo pipefail

# Build a self-contained, minimal LGPL 2.1+ FFmpeg 9.0.2 Linux x64 devkit
# matching backend/native/media-presentation/build_windows_devkit.ps1.

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
APP_ROOT=$(cd "$SCRIPT_DIR/../../.." && pwd)

FFMPEG_ARCHIVE="ffmpeg-9.0.2.tar.xz"
FFMPEG_SHA256="8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e"
FFMPEG_URL="https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz"

ZLIB_ARCHIVE="zlib-1.4.tar.gz"
ZLIB_SHA256="02dd159783677a749c04adbdd02b55b8a2bcc7e054f51b76b549199a916ad584"
ZLIB_URL_PRIMARY="https://github.com/madler/zlib/releases/download/v1.4/zlib-1.4.tar.gz"
ZLIB_URL_FALLBACK="https://zlib.net/fossils/zlib-1.4.tar.gz"

CACHE_DIR="$APP_ROOT/.local/linux-media-cache"
OUTPUT_DIR="$APP_ROOT/.local/linux-media-presentation-devkit"
JOBS=$(nproc 2>/dev/null || echo 4)
if (( JOBS > 8 )); then
  JOBS=8
fi
FORCE=0

usage() {
  cat <<'EOF'
Usage: build_linux_devkit.sh [--cache-dir <dir>] [--output-dir <dir>] [--jobs <n>] [--force]
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --cache-dir)
      CACHE_DIR="$2"
      shift 2
      ;;
    --output-dir)
      OUTPUT_DIR="$2"
      shift 2
      ;;
    --jobs)
      JOBS="$2"
      shift 2
      ;;
    --force)
      FORCE=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if [[ "$(uname -s)" != "Linux" || "$(uname -m)" != "x86_64" ]]; then
  echo "Linux FFmpeg 9.0.2 devkit requires a Linux x86_64 host." >&2
  exit 1
fi

sha256_of() {
  sha256sum "$1" | awk '{print $1}'
}

verify_existing_devkit() {
  local dir="$1"
  local manifest="$dir/devkit-manifest.json"
  [[ -f "$manifest" ]] || return 1
  python3 - "$dir" "$manifest" "$FFMPEG_SHA256" "$ZLIB_SHA256" <<'PY'
import hashlib, json, pathlib, sys

root = pathlib.Path(sys.argv[1])
manifest_path = pathlib.Path(sys.argv[2])
ffmpeg_sha = sys.argv[3]
zlib_sha = sys.argv[4]

try:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
except Exception:
    sys.exit(1)

if manifest.get("schemaVersion") != 1 or manifest.get("target") != "linux-x64":
    sys.exit(1)
files = manifest.get("files")
if not isinstance(files, dict):
    sys.exit(1)

required = [
    "lib/libavutil.so.61",
    "lib/libavcodec.so.63",
    "lib/libavformat.so.63",
    "include/libavformat/avformat.h",
    "include/libavcodec/avcodec.h",
    "include/libavutil/avutil.h",
    "FFmpeg-LICENSE.txt",
    "zlib-LICENSE.txt",
    "ffmpeg-9.0.2.tar.xz",
    "zlib-1.4.tar.gz",
]
for rel in required:
    if rel not in files:
        sys.exit(1)
if files["ffmpeg-9.0.2.tar.xz"] != ffmpeg_sha or files["zlib-1.4.tar.gz"] != zlib_sha:
    sys.exit(1)

for rel, expected in files.items():
    p = root / rel
    if p.is_symlink() or not p.is_file():
        sys.exit(1)
    actual = hashlib.sha256(p.read_bytes()).hexdigest()
    if actual != expected:
        sys.exit(1)
sys.exit(0)
PY
}

if [[ "$FORCE" -eq 0 ]] && verify_existing_devkit "$OUTPUT_DIR"; then
  echo "[linux-media-devkit] Verified existing devkit at $OUTPUT_DIR"
  exit 0
fi

mkdir -p "$CACHE_DIR"

fetch_pinned() {
  local filename="$1"
  local expected="$2"
  shift 2
  local dest="$CACHE_DIR/$filename"

  if [[ -f "$dest" ]]; then
    local actual
    actual=$(sha256_of "$dest")
    if [[ "$actual" == "$expected" ]]; then
      return 0
    fi
    echo "[linux-media-devkit] Cached $filename hash mismatch ($actual), re-downloading..." >&2
    rm -f "$dest"
  fi

  local proxy_args=()
  local proxy_url="${MAKO_BUILD_PROXY:-${HTTPS_PROXY:-${https_proxy:-}}}"
  if [[ -n "$proxy_url" ]]; then
    proxy_args=(--proxy "$proxy_url")
  fi

  local part="$dest.part"
  rm -f "$part"
  local url
  for url in "$@"; do
    echo "[linux-media-devkit] Downloading $filename from $url"
    if curl --fail --location --retry 3 --connect-timeout 20 --max-time 180 "${proxy_args[@]}" "$url" -o "$part"; then
      local actual
      actual=$(sha256_of "$part")
      if [[ "$actual" == "$expected" ]]; then
        mv -f "$part" "$dest"
        return 0
      fi
      echo "[linux-media-devkit] SHA-256 mismatch for $url (got $actual, expected $expected)" >&2
      rm -f "$part"
    fi
  done

  echo "[linux-media-devkit] Failed to fetch verified archive $filename (expected SHA-256 $expected)" >&2
  exit 1
}

fetch_pinned "$FFMPEG_ARCHIVE" "$FFMPEG_SHA256" "$FFMPEG_URL"
fetch_pinned "$ZLIB_ARCHIVE" "$ZLIB_SHA256" "$ZLIB_URL_PRIMARY" "$ZLIB_URL_FALLBACK"

WORK_DIR=$(mktemp -d "${TMPDIR:-/tmp}/mako-linux-ffmpeg-devkit.XXXXXX")
trap 'rm -rf "$WORK_DIR"' EXIT

echo "[linux-media-devkit] Extracting pinned sources in $WORK_DIR"
tar -xf "$CACHE_DIR/$ZLIB_ARCHIVE" -C "$WORK_DIR"
tar -xf "$CACHE_DIR/$FFMPEG_ARCHIVE" -C "$WORK_DIR"

echo "[linux-media-devkit] Building static PIC zlib 1.4"
(
  cd "$WORK_DIR/zlib-1.4"
  CFLAGS="-O2 -fPIC" ./configure --static --prefix="$WORK_DIR/zlib-install"
  make -j"$JOBS"
  make install
)

echo "[linux-media-devkit] Configuring and building minimal LGPL FFmpeg 9.0.2"
(
  cd "$WORK_DIR/ffmpeg-9.0.2"
  ./configure \
    --prefix="$WORK_DIR/ffmpeg-install" \
    --enable-shared \
    --disable-static \
    --disable-autodetect \
    --disable-everything \
    --disable-programs \
    --disable-doc \
    --disable-debug \
    --disable-network \
    --disable-avdevice \
    --disable-avfilter \
    --disable-swscale \
    --disable-swresample \
    --disable-x86asm \
    --enable-zlib \
    --extra-cflags="-I$WORK_DIR/zlib-install/include" \
    --extra-ldflags="-L$WORK_DIR/zlib-install/lib -Wl,--disable-new-dtags" \
    --enable-decoder=h264,hevc,mpeg4,vp8,vp9,ffv1,png,mjpeg \
    --enable-demuxer=mov,matroska,image_png_pipe,image_jpeg_pipe
  printf '\nLDFLAGS += -Wl,-rpath,\x27$$$$ORIGIN\x27\n' >> ffbuild/config.mak
  make -j"$JOBS"
  make install
)

STAGE_DIR="$WORK_DIR/stage"
mkdir -p "$STAGE_DIR/lib" "$STAGE_DIR/include"

for lib in libavutil.so.61 libavcodec.so.63 libavformat.so.63; do
  src="$WORK_DIR/ffmpeg-install/lib/$lib"
  if [[ ! -e "$src" ]]; then
    echo "[linux-media-devkit] Expected built library missing: $src" >&2
    exit 1
  fi
  cp -L "$src" "$STAGE_DIR/lib/$lib"
  chmod 0755 "$STAGE_DIR/lib/$lib"

  # Verify RPATH/RUNPATH contains literal $ORIGIN and no libz.so dynamic dependency leaked in.
  dyn=$(LC_ALL=C readelf -d "$STAGE_DIR/lib/$lib")
  if ! grep -Eq 'Library (rpath|runpath): \[\$ORIGIN\]' <<<"$dyn"; then
    echo "[linux-media-devkit] $lib does not have literal \$ORIGIN RPATH/RUNPATH:" >&2
    echo "$dyn" >&2
    exit 1
  fi
  if grep -Eq 'Shared library: \[libz\.so' <<<"$dyn"; then
    echo "[linux-media-devkit] $lib unexpectedly dynamically linked system libz.so:" >&2
    echo "$dyn" >&2
    exit 1
  fi
done

cp -R "$WORK_DIR/ffmpeg-install/include/libavutil" "$STAGE_DIR/include/"
cp -R "$WORK_DIR/ffmpeg-install/include/libavcodec" "$STAGE_DIR/include/"
cp -R "$WORK_DIR/ffmpeg-install/include/libavformat" "$STAGE_DIR/include/"

cp "$WORK_DIR/ffmpeg-9.0.2/COPYING.LGPLv2.1" "$STAGE_DIR/FFmpeg-LICENSE.txt"
cp "$WORK_DIR/zlib-1.4/LICENSE" "$STAGE_DIR/zlib-LICENSE.txt"
cp "$CACHE_DIR/$FFMPEG_ARCHIVE" "$STAGE_DIR/$FFMPEG_ARCHIVE"
cp "$CACHE_DIR/$ZLIB_ARCHIVE" "$STAGE_DIR/$ZLIB_ARCHIVE"

python3 - "$STAGE_DIR" <<'PY'
import hashlib, json, pathlib, sys

root = pathlib.Path(sys.argv[1])
files = {}
for p in sorted(root.rglob("*")):
    if p.is_symlink():
        raise RuntimeError(f"Symlink not allowed in devkit: {p}")
    if p.is_file():
        rel = p.relative_to(root).as_posix()
        files[rel] = hashlib.sha256(p.read_bytes()).hexdigest()

manifest = {
    "schemaVersion": 1,
    "target": "linux-x64",
    "ffmpegVersion": "9.0.2",
    "zlibVersion": "1.4",
    "files": files,
}
(root / "devkit-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
PY

mkdir -p "$(dirname "$OUTPUT_DIR")"
rm -rf "$OUTPUT_DIR"
mv "$STAGE_DIR" "$OUTPUT_DIR"
echo "[linux-media-devkit] Built verified Linux x64 FFmpeg 9.0.2 devkit at $OUTPUT_DIR"
