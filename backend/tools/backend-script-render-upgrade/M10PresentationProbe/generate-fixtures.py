#!/usr/bin/env python3
"""Offline finite fixture authoring. This script is not run as part of drafting or building the probe.

PNG bytes/pixels and Matroska EBML timing are authored with Python stdlib. An explicitly supplied local
FFmpeg/ffprobe encodes small compressed fixtures and provides a separately disclosed codec reference;
the Python converter computes all reference linear pixels without using the product converter.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import struct
import subprocess
from fractions import Fraction
import zlib


def sha(data):
    return hashlib.sha256(data).hexdigest()


def run(argv, output_limit=4 * 1024 * 1024):
    result = subprocess.run([str(value) for value in argv], stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False, timeout=30)
    if result.returncode != 0:
        # Diagnostics only contain local generated fixture/tool paths; the private generator journal retains them.
        raise RuntimeError(f"local fixture tool failed ({result.returncode}): {result.stderr[-4096:].decode(errors='replace')}")
    if len(result.stdout) > output_limit or len(result.stderr) > output_limit:
        raise RuntimeError("fixture tool output exceeded finite generator capacity")
    return result.stdout


def png_chunk(name, payload):
    return struct.pack('>I', len(payload)) + name + payload + struct.pack('>I', zlib.crc32(name + payload))


def png(width, height, bits, pixels, sar=(1, 1), orientation=1):
    result = b'\x89PNG\r\n\x1a\n'
    result += png_chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, bits, 6, 0, 0, 0))
    result += png_chunk(b'sRGB', b'\0')
    if sar != (1, 1):
        # pHYs stores x/y density, so y/x is sample aspect ratio.
        result += png_chunk(b'pHYs', struct.pack('>IIB', sar[1], sar[0], 0))
    if orientation != 1:
        tiff = b'II' + struct.pack('<HIH', 42, 8, 1)
        tiff += struct.pack('<HHI', 0x0112, 3, 1) + struct.pack('<H', orientation) + b'\0\0' + struct.pack('<I', 0)
        result += png_chunk(b'eXIf', tiff)
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        for value in pixels[y * width * 4:(y + 1) * width * 4]:
            rows.extend(struct.pack('>H', value) if bits == 16 else bytes([value]))
    result += png_chunk(b'IDAT', zlib.compress(bytes(rows), level=9))
    return result + png_chunk(b'IEND', b'')


def srgb_linear(value):
    return value / 12.92 if value <= .04045 else ((value + .055) / 1.055) ** 2.4


def bt709_linear(value):
    return value / 4.5 if value < .081 else ((value + .099) / 1.099) ** (1 / .45)


def packed_png_reference(width, height, bits, values, rotation=0):
    maximum = (1 << bits) - 1
    pixels = []
    for i in range(0, len(values), 4):
        alpha = values[i + 3] / maximum
        pixels.append([srgb_linear(values[i + component] / maximum) * alpha for component in range(3)] + [alpha])
    if rotation == 1:
        pixels = [pixels[(height - 1 - x) * width + y] for y in range(width) for x in range(height)]
    return b''.join(struct.pack('<4f', *pixel) for pixel in pixels)


def vint(value):
    for width in range(1, 9):
        if value < (1 << (7 * width)) - 1:
            return (value | (1 << (7 * width))).to_bytes(width, 'big')
    raise ValueError('EBML finite element too large')


def elem(identifier, value):
    return identifier.to_bytes((identifier.bit_length() + 7) // 8, 'big') + vint(len(value)) + value


def uint(identifier, value):
    return elem(identifier, value.to_bytes(max(1, (value.bit_length() + 7) // 8), 'big'))


def matroska_vp9(width, height, packets, default_duration_ns=None, tail_duration_ms=None):
    # No libav muxer writes the timing authority under test. SimpleBlock positions are actual EBML payloads
    # (TrackNumber first); only the final BlockGroup carries BlockDuration in the tail-only fixture.
    header = elem(0x1A45DFA3, uint(0x4286, 1) + uint(0x42F7, 1) + uint(0x42F2, 4) + uint(0x42F3, 8)
                  + elem(0x4282, b'matroska') + uint(0x4287, 4) + uint(0x4285, 2))
    info = elem(0x1549A966, uint(0x2AD7B1, 1_000_000) + elem(0x4D80, b'm10-python-fixture')
                + elem(0x5741, b'm10-python-fixture'))
    # BT709 matrix/transfer/primaries, limited range, left horizontally and centered vertically.
    colour = elem(0x55B0, uint(0x55B1, 1) + uint(0x55B2, 8) + uint(0x55B3, 1) + uint(0x55B4, 1)
                  + uint(0x55B7, 1) + uint(0x55B8, 2) + uint(0x55B9, 1) + uint(0x55BA, 1) + uint(0x55BB, 1))
    video = elem(0xE0, uint(0xB0, width) + uint(0xBA, height) + colour)
    track = uint(0xD7, 1) + uint(0x73C5, 1) + uint(0x83, 1) + uint(0x9C, 0) + elem(0x86, b'V_VP9')
    if default_duration_ns is not None:
        track += uint(0x23E383, default_duration_ns)
    tracks = elem(0x1654AE6B, elem(0xAE, track + video))
    blocks = bytearray(uint(0xE7, 0))
    for i, (timestamp, packet) in enumerate(zip((0, 33, 67), packets)):
        if tail_duration_ms is not None and i == len(packets) - 1:
            block = b'\x81' + struct.pack('>hB', timestamp, 0) + packet
            blocks.extend(elem(0xA0, elem(0xA1, block) + uint(0x9B, tail_duration_ms)))
        else:
            block = b'\x81' + struct.pack('>hB', timestamp, 0x80) + packet
            blocks.extend(elem(0xA3, block))
    return header + elem(0x18538067, info + tracks + elem(0x1F43B675, bytes(blocks)))


def extract_ivf(data, count):
    if data[:4] != b'DKIF' or data[8:12] != b'VP90' or len(data) < 32:
        raise RuntimeError('VP9 IVF reference framing invalid')
    packets, cursor = [], 32
    while cursor < len(data):
        if cursor + 12 > len(data):
            raise RuntimeError('truncated IVF header')
        length, _timestamp = struct.unpack_from('<IQ', data, cursor)
        cursor += 12
        if length == 0 or cursor + length > len(data):
            raise RuntimeError('truncated IVF packet')
        packets.append(data[cursor:cursor + length])
        cursor += length
    if len(packets) != count:
        raise RuntimeError('unexpected IVF reference count')
    return packets


def raw_yuv(width, height, count):
    result = bytearray()
    for frame in range(count):
        result.extend(30 + ((x * 9 + y * 13 + frame * 27) % 185) for y in range(height) for x in range(width))
        result.extend(75 + ((x * 13 + y * 9 + frame * 11) % 95) for y in range(height // 2) for x in range(width // 2))
        result.extend(80 + ((x * 7 + y * 17 + frame * 19) % 90) for y in range(height // 2) for x in range(width // 2))
    return bytes(result)


def bilinear(plane, width, height, x, y):
    x, y = min(max(x, 0), width - 1), min(max(y, 0), height - 1)
    x0, y0 = math.floor(x), math.floor(y)
    x1, y1 = min(x0 + 1, width - 1), min(y0 + 1, height - 1)
    a, b = x - x0, y - y0
    top = plane[y0 * width + x0] * (1 - a) + plane[y0 * width + x1] * a
    bottom = plane[y1 * width + x0] * (1 - a) + plane[y1 * width + x1] * a
    return top * (1 - b) + bottom * b


def yuv_reference(data, width, height, count):
    luma, chroma = width * height, (width // 2) * (height // 2)
    if len(data) != (luma + 2 * chroma) * count:
        raise RuntimeError('reference decoder raw frame count/layout mismatch')
    output = bytearray()
    for frame in range(count):
        start = frame * (luma + 2 * chroma)
        yy, cb, cr = data[start:start + luma], data[start + luma:start + luma + chroma], data[start + luma + chroma:start + luma + 2 * chroma]
        for y in range(height):
            for x in range(width):
                lum = (yy[y * width + x] - 16) / 219
                u = (bilinear(cb, width // 2, height // 2, x / 2, (y - .5) / 2) - 128) / 224
                v = (bilinear(cr, width // 2, height // 2, x / 2, (y - .5) / 2) - 128) / 224
                encoded = (lum + 1.5748 * v, lum - .1873242729306488 * u - .4681242729306488 * v, lum + 1.8556 * u)
                linear = [bt709_linear(min(max(value, 0), 1)) for value in encoded]
                output.extend(struct.pack('<4f', *linear, 1))
    return bytes(output)


def time_value(value):
    value = Fraction(value)
    return {'ticks': value.numerator, 'numerator': 1, 'denominator': value.denominator}


def ceil_us(value):
    value = Fraction(value) * 1_000_000
    return -(-value.numerator // value.denominator)


def make_case(root, name, filename, content, pixels, frames, *, width, height, bits=8, video=False,
              sar=(1, 1), rotation=0, tail='Timeless', endpoint=None, oracle='python-stdlib-authored-lossless-png'):
    (root / filename).write_bytes(content)
    pixels_file = name + '.rgba32f'
    (root / pixels_file).write_bytes(pixels)
    return dict(name=name, file=filename, byteLength=len(content), sha256=sha(content),
                family=1 if filename.endswith('.mp4') else 2 if filename.endswith('.mkv') else 3,
                kind='Video' if video else 'Still', streamIndex=0, width=width, height=height,
                canonicalWidth=height if rotation % 2 else width, canonicalHeight=width if rotation % 2 else height,
                sarNumerator=sar[0], sarDenominator=sar[1], rotation=('None', 'Clockwise90', 'Clockwise180', 'Clockwise270')[rotation],
                mirror=False, transfer='Bt709' if video else 'Srgb', matrix='Bt709' if video else 'Rgb',
                range='Limited' if video else 'Full', alpha='Opaque' if video else 'Straight',
                chromaLocation='Left' if video else 'None', nativePixelFormat='Yuv420P8' if video else ('Rgba64Le' if bits == 16 else 'Rgba32'),
                missingTagOverride=False, frames=frames, pixelsFile=pixels_file, pixelsByteLength=len(pixels),
                pixelsSha256=sha(pixels), pixelAbsoluteTolerance=0.000002, pixelOracle=oracle,
                tailAuthority=tail, videoDurationUs=None if endpoint is None else ceil_us(endpoint))


def probe(ffprobe, source):
    data = run([ffprobe, '-v', 'error', '-select_streams', 'v:0', '-show_streams', '-show_frames',
                '-show_entries', 'stream=time_base,width,height,sample_aspect_ratio,color_range,color_space,color_transfer,color_primaries,chroma_location:frame=pts,pict_type,color_range,color_space,color_transfer,color_primaries,chroma_location',
                '-of', 'json', source])
    parsed = json.loads(data)
    if len(parsed.get('streams', [])) != 1:
        raise RuntimeError('fixture must contain exactly one selected video stream')
    return parsed


def decoded_reference(ffmpeg, source, width, height, count):
    data = run([ffmpeg, '-nostdin', '-v', 'error', '-threads', '1', '-flags', '+bitexact', '-noautorotate',
                '-i', source, '-map', '0:v:0', '-fps_mode', 'passthrough', '-c:v', 'rawvideo', '-pix_fmt', 'yuv420p',
                '-threads', '1', '-f', 'rawvideo', 'pipe:1'])
    return yuv_reference(data, width, height, count), sha(data)


def assert_probe_timing(result, wanted, need_b_frames=False):
    frames = result.get('frames', [])
    base = Fraction(result['streams'][0]['time_base'])
    if len(frames) != len(wanted) or [int(frame['pts']) * base for frame in frames] != wanted:
        raise RuntimeError('independent authored timing differs from reference demux/decode PTS')
    if need_b_frames and not any(frame.get('pict_type') == 'B' for frame in frames):
        raise RuntimeError('H264 B-frame fixture contains no real decoded B frame')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--ffmpeg', type=Path, required=True)
    parser.add_argument('--ffprobe', type=Path, required=True)
    args = parser.parse_args()
    if not all(path.is_absolute() for path in (args.output, args.ffmpeg, args.ffprobe)):
        parser.error('all paths must be absolute')
    if args.output.exists():
        parser.error('output must be a new private directory')
    if not args.ffmpeg.is_file() or not args.ffprobe.is_file():
        parser.error('explicit installed local ffmpeg and ffprobe are required')
    args.output.mkdir(mode=0o700)
    root = args.output
    still_frames = [dict(pts=None, end=None, startUs=None, endUs=None)]
    values8 = [255, 0, 64, 255, 0, 128, 255, 128, 30, 60, 90, 0, 17, 121, 242, 64, 210, 42, 90, 200, 1, 2, 3, 255]
    values16 = [65535, 0, 16384, 65535, 0, 32768, 65535, 32768, 8192, 4096, 32768, 0,
                1024, 31000, 60000, 16384, 51000, 10000, 22000, 50000, 1, 2, 3, 65535]
    cases = [make_case(root, 'png8-srgb-alpha', 'png8.png', png(3, 2, 8, values8),
                       packed_png_reference(3, 2, 8, values8), still_frames, width=3, height=2),
             make_case(root, 'png16-srgb-alpha', 'png16.png', png(3, 2, 16, values16),
                       packed_png_reference(3, 2, 16, values16), still_frames, width=3, height=2, bits=16),
             make_case(root, 'png8-sar-exif90', 'png8-sar-exif90.png', png(3, 2, 8, values8, (2, 1), 6),
                       packed_png_reference(3, 2, 8, values8, rotation=1), still_frames, width=3, height=2, sar=(2, 1), rotation=1)]
    width, height, count = 16, 12, 6
    raw = root / 'authored-yuv420p.yuv'
    raw.write_bytes(raw_yuv(width, height, count))
    common = [args.ffmpeg, '-nostdin', '-v', 'error', '-f', 'rawvideo', '-pixel_format', 'yuv420p',
              '-video_size', f'{width}x{height}', '-framerate', '5', '-i', raw, '-map', '0:v:0', '-an']
    mp4 = root / 'h264-bframes.mp4'
    run(common + ['-frames:v', str(count), '-c:v', 'libx264', '-threads', '1', '-pix_fmt', 'yuv420p', '-crf', '12',
                  '-x264-params', 'bframes=2:b-adapt=0:keyint=30:scenecut=0:threads=1:colorprim=bt709:transfer=bt709:colormatrix=bt709',
                  '-color_range', 'tv', '-colorspace', 'bt709', '-color_trc', 'bt709', '-color_primaries', 'bt709',
                  '-chroma_sample_location', 'left', '-video_track_timescale', '1000', '-movflags', '+faststart', mp4])
    reference = probe(args.ffprobe, mp4)
    wanted = [Fraction(i, 5) for i in range(count)]
    assert_probe_timing(reference, wanted, need_b_frames=True)
    expected_color = dict(color_range="tv", color_space="bt709", color_transfer="bt709", color_primaries="bt709", chroma_location="left")
    for decoded in reference["frames"] + reference["streams"]:
        if any(decoded.get(key) != value for key, value in expected_color.items()):
            raise RuntimeError("H264 bitstream and container color authorities must agree")
    (root / 'h264-reference-ffprobe.json').write_text(json.dumps(reference, indent=2) + '\n')
    pixels, decoded_sha = decoded_reference(args.ffmpeg, mp4, width, height, count)
    frames = [dict(pts=time_value(pts), end=time_value(Fraction(i + 1, 5)), startUs=ceil_us(pts),
                   endUs=ceil_us(Fraction(i + 1, 5))) for i, pts in enumerate(wanted)]
    oracle = 'authored-timing;local-ffmpeg-codec-reference;independent-python-BT709-converter'
    cases.append(make_case(root, 'h264-bframes', mp4.name, mp4.read_bytes(), pixels, frames, width=width, height=height,
                           video=True, tail='FrameDuration', endpoint=Fraction(count, 5), oracle=oracle))
    ivf = root / 'vp9-packets.ivf'
    run(common + ['-frames:v', '3', '-c:v', 'libvpx-vp9', '-threads', '1', '-g', '1', '-deadline', 'best', '-crf', '4',
                  '-b:v', '1000k', '-pix_fmt', 'yuv420p', '-color_range', 'tv', '-colorspace', 'bt709',
                  '-color_trc', 'bt709', '-color_primaries', 'bt709', '-chroma_sample_location', 'left', '-f', 'ivf', ivf])
    packets = extract_ivf(ivf.read_bytes(), 3)
    pts = [Fraction(value, 1000) for value in (0, 33, 67)]
    mkv_provenance = []
    for name, default, tail_ms in [('mkv-default-duration', 33333333, None), ('mkv-tail-only-block-duration', None, 41)]:
        content = matroska_vp9(width, height, packets, default, tail_ms)
        source = root / (name + '.mkv')
        source.write_bytes(content)
        reference = probe(args.ffprobe, source)
        assert_probe_timing(reference, pts)
        expected_color = dict(color_range="tv", color_space="bt709", color_transfer="bt709", color_primaries="bt709", chroma_location="left")
        for decoded in reference["frames"] + reference["streams"]:
            if any(decoded.get(key) != value for key, value in expected_color.items()):
                raise RuntimeError("Matroska container and decoded-frame color authorities must agree")
        (root / (name + '-reference-ffprobe.json')).write_text(json.dumps(reference, indent=2) + '\n')
        pixels, native_sha = decoded_reference(args.ffmpeg, source, width, height, 3)
        end = pts[-1] + (Fraction(default, 1_000_000_000) if default else Fraction(tail_ms, 1000))
        ends = pts[1:] + [end]
        frames = [dict(pts=time_value(start), end=time_value(stop), startUs=ceil_us(start), endUs=ceil_us(stop))
                  for start, stop in zip(pts, ends)]
        cases.append(make_case(root, name, source.name, content, pixels, frames, width=width, height=height,
                               video=True, tail='FrameDuration', endpoint=end, oracle=oracle))
        mkv_provenance.append(dict(name=name, codec="vp9", frameColorChecked=True, timestampScaleNs=1_000_000, authoredPtsMs=[0, 33, 67],
                                   defaultDurationNs=default, onlyFinalBlockDurationMs=tail_ms, referenceNativeSha256=native_sha,
                                   blockPositionConvention='first-TrackNumber-byte-in-element-payload'))
    tools = []
    for tool in (args.ffmpeg, args.ffprobe):
        tools.append(dict(path=str(tool.resolve()), sha256=sha(tool.read_bytes()),
                          version=run([tool, '-version']).decode().splitlines()[0]))
    manifest = dict(version=1, oracle='PNG and EBML timing authored independently; compressed codec reference shares FFmpeg ancestry; no product output reused', cases=cases)
    (root / 'fixtures.json').write_text(json.dumps(manifest, indent=2) + '\n')
    (root / 'generation.json').write_text(json.dumps(dict(tools=tools, h264ReferenceNativeSha256=decoded_sha,
                                                        matroska=mkv_provenance, sourceRawSha256=sha(raw.read_bytes())), indent=2) + '\n')
    print(f'Authored {len(cases)} finite fixtures in {root}; no production acceptance has run.')


if __name__ == '__main__':
    main()
