#!/usr/bin/env python3
"""Extend the frozen six-case fixture JSON with ten real JPEGs and finite policy rejections.

Requires explicit installed cjpeg, FFmpeg and ffprobe. No download, product converter, product helper,
fixture acceptance, or automatic tool substitution is performed. The script itself is not run in this draft.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import shutil
import struct
import subprocess
import zlib


class GenerationFailure(Exception):
    def __init__(self, code, detail):
        super().__init__(code)
        self.code, self.detail = code, detail


def digest(data):
    return hashlib.sha256(data).hexdigest()


def run(argv, name):
    try:
        result = subprocess.run([str(arg) for arg in argv], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, check=False, timeout=30)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise GenerationFailure('local-tool-unavailable-or-timeout', name) from error
    if result.returncode != 0:
        raise GenerationFailure('local-tool-rejected-explicit-case', name + ': ' + result.stderr[-2048:].decode(errors='replace'))
    if len(result.stdout) > 4 * 1024 * 1024 or len(result.stderr) > 4 * 1024 * 1024:
        raise GenerationFailure('local-tool-output-capacity', name)
    return result.stdout


def srgb(value):
    return value / 12.92 if value <= .04045 else ((value + .055) / 1.055) ** 2.4


def linear_rgba(rgb):
    output = bytearray()
    for at in range(0, len(rgb), 3):
        output.extend(struct.pack('<4f', *(srgb(value / 255) for value in rgb[at:at + 3]), 1))
    return bytes(output)


def sample(plane, width, height, x, y):
    # Centered chroma; coordinates outside a plane hold its nearest edge sample.
    x, y = min(max(x, 0), width - 1), min(max(y, 0), height - 1)
    x0, y0 = math.floor(x), math.floor(y)
    x1, y1 = min(x0 + 1, width - 1), min(y0 + 1, height - 1)
    a, b = x - x0, y - y0
    top = plane[y0 * width + x0] * (1 - a) + plane[y0 * width + x1] * a
    bottom = plane[y1 * width + x0] * (1 - a) + plane[y1 * width + x1] * a
    return top * (1 - b) + bottom * b


def native_to_canonical(raw, pixel_format, width, height):
    count = width * height
    if pixel_format == 'gray':
        if len(raw) != count:
            raise GenerationFailure('native-gray-length', pixel_format)
        return linear_rgba(bytes(channel for value in raw for channel in (value, value, value)))
    if pixel_format == 'gbrp':
        if len(raw) != count * 3:
            raise GenerationFailure('native-gbrp-length', pixel_format)
        green, blue, red = raw[:count], raw[count:count * 2], raw[count * 2:]
        return linear_rgba(bytes(channel for i in range(count) for channel in (red[i], green[i], blue[i])))
    if pixel_format == 'rgb24':
        if len(raw) != count * 3:
            raise GenerationFailure('native-rgb24-length', pixel_format)
        return linear_rgba(raw)
    subsampling = {'yuvj420p': (2, 2), 'yuv420p': (2, 2), 'yuvj422p': (2, 1),
                   'yuv422p': (2, 1), 'yuvj444p': (1, 1), 'yuv444p': (1, 1)}
    if pixel_format not in subsampling:
        raise GenerationFailure('native-jpeg-format-not-supported-by-oracle', pixel_format)
    sx, sy = subsampling[pixel_format]
    cw, ch = (width + sx - 1) // sx, (height + sy - 1) // sy
    if len(raw) != count + 2 * cw * ch:
        raise GenerationFailure('native-planar-length', pixel_format)
    yy, cb, cr = raw[:count], raw[count:count + cw * ch], raw[count + cw * ch:]
    output = bytearray()
    for y in range(height):
        for x in range(width):
            # JPEG full range is divided by 255 once. No limited/full remap, intermediate RGB8,
            # swscale RGB conversion, second transfer conversion, or production converter is used.
            xx = x if sx == 1 else ((x + .5) - 1) / sx
            yyp = y if sy == 1 else ((y + .5) - 1) / sy
            luma = yy[y * width + x] / 255
            u = (sample(cb, cw, ch, xx, yyp) - 128) / 255
            v = (sample(cr, cw, ch, xx, yyp) - 128) / 255
            encoded = (luma + 1.402 * v, luma - .34413628620102216 * u - .7141362862010221 * v, luma + 1.772 * u)
            output.extend(struct.pack('<4f', *(srgb(min(max(value, 0), 1)) for value in encoded), 1))
    return bytes(output)


def jpeg_segments(data):
    """Independent finite marker walk, including stuffed entropy bytes and all progressive scans."""
    if not data.startswith(b'\xff\xd8'):
        raise GenerationFailure('jpeg-encoder-missing-soi', 'SOI')
    result, cursor, scans = [], 2, 0
    while cursor < len(data):
        marker_at = cursor
        if data[cursor] != 255:
            raise GenerationFailure('jpeg-independent-marker-boundary', str(cursor))
        while cursor < len(data) and data[cursor] == 255:
            cursor += 1
        if cursor >= len(data):
            raise GenerationFailure('jpeg-truncated-marker', str(cursor))
        marker = data[cursor]
        cursor += 1
        if marker == 0xD9:
            if cursor != len(data):
                raise GenerationFailure('jpeg-encoder-trailing-data', str(cursor))
            return result, scans
        if marker in (0, 0xD8) or 0xD0 <= marker <= 0xD7 or cursor + 2 > len(data):
            raise GenerationFailure('jpeg-encoder-unexpected-marker', hex(marker))
        length = int.from_bytes(data[cursor:cursor + 2], 'big')
        if length < 2 or cursor + length > len(data):
            raise GenerationFailure('jpeg-encoder-invalid-segment', hex(marker))
        payload_at = cursor + 2
        payload_end = cursor + length
        result.append((marker, marker_at, payload_at, payload_end))
        cursor = payload_end
        if marker == 0xDA:
            scans += 1
            while cursor < len(data):
                if data[cursor] != 255:
                    cursor += 1
                    continue
                candidate = cursor
                while cursor < len(data) and data[cursor] == 255:
                    cursor += 1
                if cursor >= len(data):
                    raise GenerationFailure('jpeg-encoder-truncated-entropy', str(candidate))
                if data[cursor] == 0 or 0xD0 <= data[cursor] <= 0xD7:
                    cursor += 1
                    continue
                cursor = candidate
                break
    raise GenerationFailure('jpeg-encoder-missing-eoi', 'EOI')


def structure(data, progressive, mode, width, height):
    segments, scans = jpeg_segments(data)
    frames = [(marker, start, end) for marker, _at, start, end in segments if marker in (0xC0, 0xC2)]
    if len(frames) != 1 or frames[0][0] != (0xC2 if progressive else 0xC0):
        raise GenerationFailure('jpeg-encoder-sof-mode-differs', mode)
    _marker, start, end = frames[0]
    payload = data[start:end]
    components = payload[5]
    ids = [payload[6 + i * 3] for i in range(components)]
    factors = [payload[7 + i * 3] for i in range(components)]
    wanted = {'420': [0x22, 0x11, 0x11], '422': [0x21, 0x11, 0x11], '444': [0x11] * 3,
              'rgb': [0x11] * 3, 'gray': [0x11]}[mode]
    if payload[0] != 8 or int.from_bytes(payload[1:3], 'big') != height or int.from_bytes(payload[3:5], 'big') != width or factors != wanted:
        raise GenerationFailure('jpeg-encoder-layout-differs', mode)
    jfif = [(at, first, last) for marker, at, first, last in segments if marker == 0xE0 and data[first:last].startswith(b'JFIF\0')]
    adobe = [data[first:last] for marker, _at, first, last in segments if marker == 0xEE and data[first:last].startswith(b'Adobe')]
    if mode == 'rgb':
        if jfif or ids != [ord('R'), ord('G'), ord('B')] or len(adobe) != 1 or len(adobe[0]) != 12 or adobe[0][11] != 0:
            raise GenerationFailure('jpeg-rgb-authority-or-component-layout-differs', mode)
    elif len(jfif) != 1:
        raise GenerationFailure('jpeg-jfif-authority-missing-or-duplicated', mode)
    else:
        _at, first, last = jfif[0]
        authority = data[first:last]
        if len(authority) != 14 or authority[8:12] != b'\0\x01\0\x01' or authority[12:14] != b'\0\0':
            raise GenerationFailure('jpeg-jfif-density-or-thumbnail-differs', mode)
    if progressive and scans < 2:
        raise GenerationFailure('jpeg-progressive-not-multiple-scans', mode)
    return dict(sof='SOF2' if progressive else 'SOF0', componentIds=ids, samplingFactors=factors,
                scans=scans, jfif=bool(jfif), adobeTransform=adobe[0][11] if adobe else None)


def reference(ffmpeg, ffprobe, source, mode, width, height):
    report = json.loads(run([ffprobe, '-v', 'error', '-select_streams', 'v:0', '-show_frames',
        '-show_entries', 'frame=pix_fmt,width,height,color_range,color_space,color_transfer,color_primaries,chroma_location', '-of', 'json', source], source.name + '-probe'))
    if len(report.get('frames', [])) != 1:
        raise GenerationFailure('jpeg-reference-frame-count', source.name)
    frame = report['frames'][0]
    native = frame['pix_fmt']
    wanted = {'420': ('yuvj420p', 'yuv420p'), '422': ('yuvj422p', 'yuv422p'), '444': ('yuvj444p', 'yuv444p'),
              'rgb': ('gbrp', 'rgb24'), 'gray': ('gray',)}[mode]
    if native not in wanted or frame['width'] != width or frame['height'] != height:
        raise GenerationFailure('jpeg-reference-native-layout-differs', source.name + ': ' + native)
    if mode in ('420', '422', '444') and frame.get('color_range') != 'pc':
        raise GenerationFailure('jpeg-reference-native-full-range-unproven', source.name)
    # Same actual native pix_fmt goes from decoder to rawvideo. This explicitly forbids native YUV->RGB
    # conversion inside FFmpeg; only Python below performs the normative matrix/EOTF once.
    native_bytes = run([ffmpeg, '-nostdin', '-v', 'error', '-threads', '1', '-flags', '+bitexact', '-idct', 'auto',
        '-c:v', 'mjpeg', '-noautorotate', '-i', source, '-map', '0:v:0', '-frames:v', '1', '-fps_mode', 'passthrough',
        '-c:v', 'rawvideo', '-pix_fmt', native, '-threads', '1', '-f', 'rawvideo', 'pipe:1'], source.name + '-native-reference')
    canonical = native_to_canonical(native_bytes, native, width, height)
    return canonical, native_bytes, frame


def error_metrics(actual, authored):
    a = struct.unpack('<' + 'f' * (len(actual) // 4), actual)
    b = struct.unpack('<' + 'f' * (len(authored) // 4), authored)
    differences = [a[i] - b[i] for i in range(len(a)) if i % 4 != 3]
    return dict(domain='linear-RGB-after-one-normative-conversion', maxAbsolute=max(map(abs, differences)),
                rmse=math.sqrt(sum(value * value for value in differences) / len(differences)),
                meaning='Loss from authored source through cjpeg sampling/quantization and decoded native reference; diagnostic only, not canonical acceptance tolerance')


def jpeg_segment(marker, payload):
    return b'\xff' + bytes([marker]) + struct.pack('>H', len(payload) + 2) + payload


def png_chunk(kind, payload):
    return struct.pack('>I', len(payload)) + kind + payload + struct.pack('>I', zlib.crc32(kind + payload))


def png_chunks(data):
    cursor, result = 8, []
    while cursor < len(data):
        length = int.from_bytes(data[cursor:cursor + 4], 'big')
        result.append((data[cursor + 4:cursor + 8], cursor, cursor + length + 12))
        cursor += length + 12
    return result


def add_rejection(root, cases, name, content, suffix, expected='ColorUnsupported', native=False, reason='', kind='Still', stream_index=0):
    filename = 'reject-' + name + suffix
    (root / filename).write_bytes(content)
    cases.append(dict(name=name, file=filename, byteLength=len(content), sha256=digest(content), kind=kind,
                      streamIndex=stream_index, expectedFailure=expected, nativeExpected=native, reason=reason))


def append_rejections(root, baseline_jpeg, rgb_jpeg, baseline_png):
    cases = []
    segments, _ = jpeg_segments(baseline_jpeg)
    jfif = next((at, first, last) for marker, at, first, last in segments if marker == 0xE0 and baseline_jpeg[first:last].startswith(b'JFIF\0'))
    at, first, last = jfif
    duplicate = baseline_jpeg[:2] + baseline_jpeg[at:last] + baseline_jpeg[2:]
    no_jfif = baseline_jpeg[:at] + baseline_jpeg[last:]
    add_rejection(root, cases, 'jpeg-icc-policy-prefix', baseline_jpeg[:2] + jpeg_segment(0xE2, b'ICC_PROFILE\0\x01\x01m10-policy-prefix') + baseline_jpeg[2:], '.jpg',
                  reason='APP2 ICC marker prefix rejected before any ICC parsing or native decode; synthetic policy-prefix injection')
    add_rejection(root, cases, 'jpeg-duplicate-jfif', duplicate, '.jpg', reason='Duplicate complete JFIF APP0 segments')
    adobe0 = jpeg_segment(0xEE, b'Adobe' + struct.pack('>HHHB', 100, 0, 0, 0))
    add_rejection(root, cases, 'jpeg-jfif-adobe-rgb-contradiction', baseline_jpeg[:2] + adobe0 + baseline_jpeg[2:], '.jpg',
                  reason='JFIF YCbCr JPEG contradicts explicit Adobe RGB transform0')
    add_rejection(root, cases, 'jpeg-missing-jfif-no-authority', no_jfif, '.jpg', native=True,
                  reason='Real encoded YCbCr JPEG loses only JFIF authority; no trusted override supplied')
    add_rejection(root, cases, 'jpeg-rgb-missing-import-authority', rgb_jpeg, '.jpg', native=True,
                  reason='Same real Adobe0 RGB positive fixture but trusted import override deliberately absent')
    add_rejection(root, cases, 'jpeg-truncated-eoi', baseline_jpeg[:-2], '.jpg', reason='Complete entropy followed by missing EOI')
    add_rejection(root, cases, 'jpeg-concatenated-images', baseline_jpeg + baseline_jpeg, '.jpg', reason='Two complete images cannot become one timeless still')
    precision = bytearray(baseline_jpeg)
    sof = next(first for marker, _at, first, _last in segments if marker == 0xC0)
    precision[sof] = 12
    add_rejection(root, cases, 'jpeg-12bit-sof-policy', bytes(precision), '.jpg',
                  reason='Synthetic SOF precision mutation is rejected structurally, not a claimed real 12bit codec fixture')
    exif = b'Exif\0\0II' + struct.pack('<HIH', 42, 8, 1) + struct.pack('<HHI', 0x0112, 3, 1) + struct.pack('<H', 9) + b'\0\0' + struct.pack('<I', 0)
    add_rejection(root, cases, 'jpeg-exif-orientation-nine', baseline_jpeg[:2] + jpeg_segment(0xE1, exif) + baseline_jpeg[2:], '.jpg',
                  reason='Well bounded TIFF APP1 carries orientation outside eight orthogonal transforms')
    chunks = png_chunks(baseline_png)
    ihdr_end = next(last for kind, _first, last in chunks if kind == b'IHDR')
    add_rejection(root, cases, 'png-icc-policy-chunk', baseline_png[:ihdr_end] + png_chunk(b'iCCP', b'm10\0\0' + zlib.compress(b'policy-prefix')) + baseline_png[ihdr_end:], '.png',
                  reason='CRC-valid iCCP chunk rejected before profile decoding; synthetic policy payload')
    add_rejection(root, cases, 'png-animation-policy-chunk', baseline_png[:ihdr_end] + png_chunk(b'acTL', struct.pack('>II', 1, 0)) + baseline_png[ihdr_end:], '.png',
                  reason='CRC-valid APNG control chunk forbidden in timeless still; policy-header fixture')
    bad_crc = bytearray(baseline_png)
    bad_crc[ihdr_end - 1] ^= 1
    add_rejection(root, cases, 'png-ihdr-crc-mismatch', bytes(bad_crc), '.png', reason='Source hashes match mutation but PNG CRC does not')
    no_srgb = b''.join(baseline_png[first:last] for kind, first, last in chunks if kind != b'sRGB')
    add_rejection(root, cases, 'png-missing-srgb-no-authority', baseline_png[:8] + no_srgb, '.png', native=True,
                  reason='Real PNG color samples lack explicit supported sRGB authority and no import override supplied')
    add_rejection(root, cases, 'source-unsupported-magic', b'M10-unsupported-source\n', '.bin', expected='ProfileUnsupported',
                  reason='Finite input outside PNG JPEG MOV Matroska magic families')
    add_rejection(root, cases, 'request-still-stream-128', baseline_jpeg, '.jpg', expected='ProfileUnsupported',
                  reason='Public inspector refuses stream index outside its fixed selected-stream capacity before native', stream_index=128)
    add_rejection(root, cases, 'request-jpeg-as-timed-video', baseline_jpeg, '.jpg', expected='ProfileUnsupported',
                  reason='Real JPEG cannot be selected as a timed video source', kind='Video')
    return cases


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n')


def generate(args):
    tool_before = {name: digest(tool.read_bytes()) for name, tool in
                   [('cjpeg', args.cjpeg), ('ffmpeg', args.ffmpeg), ('ffprobe', args.ffprobe)]}
    base_bytes = args.base_fixtures.read_bytes()
    if len(base_bytes) > 1024 * 1024:
        raise GenerationFailure('base-fixture-manifest-capacity', str(len(base_bytes)))
    base = json.loads(base_bytes)
    if base.get('version') != 1 or not 1 <= len(base.get('cases', [])) <= 22:
        raise GenerationFailure('base-fixture-set-invalid', 'version/count')
    root, base_root = args.output, args.base_fixtures.parent.resolve()
    for case in base['cases']:
        for name_key, hash_key in [('file', 'sha256'), ('pixelsFile', 'pixelsSha256')]:
            name = case[name_key]
            candidate = base_root / name
            source = candidate.resolve()
            if Path(name).name != name or source.parent != base_root or candidate.is_symlink() or not source.is_file():
                raise GenerationFailure('base-fixture-path-invalid', name)
            if digest(source.read_bytes()) != case[hash_key]:
                raise GenerationFailure('base-fixture-content-changed', name)
            destination = root / name
            if destination.exists():
                raise GenerationFailure('base-fixture-duplicate-filename', name)
            shutil.copyfile(source, destination)
    width, height = 19, 13
    rgb = bytes(component for y in range(height) for x in range(width)
                for component in (20 + (x * 7 + y * 3) % 210, 24 + (x * 3 + y * 11) % 205, 28 + (x * 5 + y * 7) % 200))
    gray = bytes(20 + (x * 7 + y * 9) % 210 for y in range(height) for x in range(width))
    ppm, pgm = root / 'jpeg-authored.ppm', root / 'jpeg-authored.pgm'
    ppm.write_bytes(f'P6\n{width} {height}\n255\n'.encode() + rgb)
    pgm.write_bytes(f'P5\n{width} {height}\n255\n'.encode() + gray)
    cases, references = list(base['cases']), []
    names = {case['name'] for case in cases}
    for progressive in (False, True):
        for mode in ('420', '422', '444', 'rgb', 'gray'):
            name = 'jpeg-' + ('progressive-' if progressive else 'baseline-') + mode
            if name in names:
                raise GenerationFailure('jpeg-fixture-name-collision', name)
            names.add(name)
            source, target = (pgm if mode == 'gray' else ppm), root / (name + '.jpg')
            options = ['-quality', '93', '-dct', 'int', '-baseline']
            if progressive:
                options += ['-progressive']
            if mode == 'rgb':
                options += ['-rgb', '-sample', '1x1,1x1,1x1']
            elif mode == 'gray':
                options += ['-grayscale', '-sample', '1x1']
            else:
                options += ['-sample', {'420': '2x2,1x1,1x1', '422': '2x1,1x1,1x1', '444': '1x1,1x1,1x1'}[mode]]
            run([args.cjpeg, *options, '-outfile', target, source], name + '-encode')
            data = target.read_bytes()
            structural = structure(data, progressive, mode, width, height)
            canonical, native_bytes, native_frame = reference(args.ffmpeg, args.ffprobe, target, mode, width, height)
            native_file, pixels_file = name + '.native', name + '.rgba32f'
            (root / native_file).write_bytes(native_bytes)
            (root / pixels_file).write_bytes(canonical)
            authored = linear_rgba(rgb if mode != 'gray' else bytes(channel for value in gray for channel in (value, value, value)))
            lossy = error_metrics(canonical, authored)
            cases.append(dict(name=name, file=target.name, byteLength=len(data), sha256=digest(data), family=4,
                kind='Still', streamIndex=0, width=width, height=height, canonicalWidth=width, canonicalHeight=height,
                sarNumerator=1, sarDenominator=1, rotation='None', mirror=False, transfer='Srgb',
                matrix='Rgb' if mode in ('rgb', 'gray') else 'Bt601Jpeg', range='Full', alpha='Opaque',
                chromaLocation='Center' if mode in ('420', '422') else 'None',
                nativePixelFormat={'420': 'Yuv420P8', '422': 'Yuv422P8', '444': 'Yuv444P8', 'rgb': 'Rgb24', 'gray': 'Rgb24'}[mode],
                missingTagOverride=mode == 'rgb', frames=[dict(pts=None, end=None, startUs=None, endUs=None)],
                pixelsFile=pixels_file, pixelsByteLength=len(canonical), pixelsSha256=digest(canonical),
                pixelAbsoluteTolerance=.000002, pixelOracle='actual-native-bitexact-mjpeg-planes;independent-Python-BT601-full-center-sRGB-once;shared-FFmpeg-codec-ancestry',
                tailAuthority='Timeless', videoDurationUs=None))
            references.append(dict(name=name, sourceSha256=digest(source.read_bytes()), sourceJpegSha256=digest(data),
                structure=structural, actualNativeFrame=native_frame, nativeFile=native_file,
                nativeByteLength=len(native_bytes), nativeSha256=digest(native_bytes), canonicalSha256=digest(canonical),
                canonicalAbsoluteTolerance=.000002, sourceToDecodedLoss=lossy))
    jpeg_yuv = (root / 'jpeg-baseline-420.jpg').read_bytes()
    jpeg_rgb = (root / 'jpeg-baseline-rgb.jpg').read_bytes()
    base_png = next(case for case in cases if case['family'] == 3 and case['name'] == 'png8-srgb-alpha')
    negatives = append_rejections(root, jpeg_yuv, jpeg_rgb, (root / base_png['file']).read_bytes())
    fixtures = dict(version=1, oracle=base['oracle'] + '; ten JPEG cases use actual native MJPEG planes and one independent Python canonical conversion', cases=cases)
    fixture_path = root / 'fixtures.json'
    write_json(fixture_path, fixtures)
    write_json(root / 'rejections.json', dict(version=1, fixturesSha256=digest(fixture_path.read_bytes()), cases=negatives))
    tools = []
    for name, tool in [('cjpeg', args.cjpeg), ('ffmpeg', args.ffmpeg), ('ffprobe', args.ffprobe)]:
        after = digest(tool.read_bytes())
        if after != tool_before[name]:
            raise GenerationFailure('reference-tool-binary-changed-during-generation', name)
        tools.append(dict(name=name, path=str(tool.resolve()), sha256=after, beforeAfterEqual=True))
    write_json(root / 'jpeg-generation.json', dict(status='authored-not-accepted', baseFixtureManifestSha256=digest(base_bytes),
        tools=tools, cases=references, lossyTolerancePolicy='Source-to-decoded error is measured, not canonical tolerance; canonical checks compare to decoded native reference at 2e-6',
        oracleLimit='Independent color/geometry arithmetic, shared FFmpeg decoder ancestry; no independent JPEG IDCT proof'))
    write_json(root / 'generation-status.json', dict(status='authored-not-accepted', positiveCases=len(cases), jpegCases=10, sourceRejections=len(negatives)))
    print(f'Authored combined fixtures: {len(cases)} positives, {len(negatives)} source rejections; no product acceptance ran.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-fixtures', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--cjpeg', type=Path, required=True)
    parser.add_argument('--ffmpeg', type=Path, required=True)
    parser.add_argument('--ffprobe', type=Path, required=True)
    args = parser.parse_args()
    if not all(path.is_absolute() for path in (args.base_fixtures, args.output, args.cjpeg, args.ffmpeg, args.ffprobe)):
        parser.error('all paths must be absolute')
    if args.output.exists() or not all(path.is_file() for path in (args.base_fixtures, args.cjpeg, args.ffmpeg, args.ffprobe)):
        parser.error('output must be new and explicit local inputs/tools must exist')
    args.output.mkdir(mode=0o700)
    try:
        generate(args)
    except GenerationFailure as error:
        # A partial directory without a complete fixture manifest cannot be promoted into acceptance.
        for name in ('fixtures.json', 'rejections.json'):
            (args.output / name).unlink(missing_ok=True)
        write_json(args.output / 'generation-status.json', dict(status='incomplete-named-failure', code=error.code, detail=error.detail))
        print(f'Fixture authoring incomplete: {error.code}; inspect the private generation status.')
        return 2
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
