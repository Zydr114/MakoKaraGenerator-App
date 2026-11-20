import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, readdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';
import { stageWindowsMedia, verifyMediaPin, windowsMediaInputs } from '../scripts/package-windows-media.mjs';

const MANIFEST = 'media-bundle.json';
const FILES = [
  'ffmpeg.exe', 'ffprobe.exe', 'FFmpeg-Provenance.txt', 'build-windows-export-tools.ps1',
  'ffmpeg-946fcce07b.tar.gz', 'x264-31e19f92.tar.gz', 'zlib-1.4.tar.gz',
  'FFmpeg-LICENSE.txt', 'FFmpeg-COPYING.GPLv2.txt', 'x264-COPYING.txt', 'zlib-LICENSE.txt',
  'LLVM-LICENSE.txt', 'MinGW-w64-runtime-LICENSE.txt',
];
const hash = (content) => createHash('sha256').update(content).digest('hex');

function fixture(context) {
  const directory = mkdtempSync(join(realpathSync(tmpdir()), 'mako export media '));
  context.after(() => rmSync(directory, { recursive: true, force: true }));
  return directory;
}

test('export media requires a locally built bundle with matching source pins', (context) => {
  const root = fixture(context);
  const options = { 'ffmpeg-dir': root };
  assert.throws(() => windowsMediaInputs(root, {}), /--ffmpeg-dir/);
  assert.throws(() => windowsMediaInputs(root, { 'ffmpeg-source': 'old.tar.gz' }), /no longer supported/);
  assert.throws(() => windowsMediaInputs(root, options), /Missing export media bundle manifest/);
  const files = Object.fromEntries(FILES.map((name) => [name, hash(name)]));
  const manifest = { schemaVersion: 1, target: 'windows-x64', files };
  const path = join(root, MANIFEST);
  writeFileSync(path, JSON.stringify({ ...manifest, files: { ...files, '../escape': hash('escape') } }));
  assert.throws(() => stageWindowsMedia({ directory: root }, root), /Invalid Windows export media bundle manifest/);
  assert.equal(existsSync(join(root, 'media-tools')), false);
  writeFileSync(path, JSON.stringify(manifest));
  assert.throws(() => windowsMediaInputs(root, options), /pin mismatch for ffmpeg-946fcce07b.tar.gz/);
});

test('export media file hash rejects changed bytes and missing files', (context) => {
  const payload = join(fixture(context), 'ffmpeg.exe');
  assert.throws(() => verifyMediaPin(payload, hash('exact bytes')), /Missing or linked/);
  writeFileSync(payload, 'exact bytes');
  verifyMediaPin(payload, hash('exact bytes'));
  assert.throws(() => verifyMediaPin(payload, hash('different bytes')), /SHA-256 mismatch/);
});

const builtDirectory = process.env.MAKO_WINDOWS_EXPORT_TOOLS_TEST_DIR;
test('native bundle stages exact payload and supplies app export and audio tools', { skip: !builtDirectory }, (context) => {
  const root = fixture(context);
  const inputs = windowsMediaInputs(root, { 'ffmpeg-dir': resolve(builtDirectory) });
  stageWindowsMedia(inputs, root);
  const staged = join(root, 'media-tools');
  assert.deepEqual(readdirSync(staged).sort(), [...FILES, MANIFEST].sort());
  const env = { SystemRoot: process.env.SystemRoot, PATH: join(process.env.SystemRoot, 'System32') };
  const run = (tool, args, options) => execFileSync(join(staged, `${tool}.exe`), args, {
    cwd: root, env, windowsHide: true, timeout: 10_000, ...options,
  });
  const inventory = (tool, query, flags) => new Set(run(tool, ['-hide_banner', `-${query}`], { encoding: 'utf8' })
    .split(/\r?\n/).flatMap((line) => line.match(flags)?.[1].split(',') ?? []));
  for (const tool of ['ffmpeg', 'ffprobe']) {
    const demuxers = inventory(tool, 'demuxers', /^\s*D\s+(\S+)\s+/);
    for (const name of ['wav', 'mp3', 'flac', 'aac', 'mov', 'ogg']) assert.ok(demuxers.has(name), `${tool}: ${name}`);
  }
  const decoders = inventory('ffmpeg', 'decoders', /^\s*A[.A-Z]{5}\s+(\S+)/);
  for (const name of ['pcm_s16le', 'pcm_f32le', 'mp3', 'flac', 'aac', 'alac', 'vorbis', 'opus']) {
    assert.ok(decoders.has(name), `decoder: ${name}`);
  }
  const encode = (args, input) => run('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-nostdin', ...args], { input });
  const probe = (file) => JSON.parse(run('ffprobe', ['-v', 'error', '-show_entries', 'stream=codec_name,pix_fmt', '-of', 'json', file], {
    encoding: 'utf8',
  })).streams;
  const clip = Buffer.alloc(16 * 16 * 4, 128);
  const rawVideo = ['-f', 'rawvideo', '-pixel_format', 'bgra', '-video_size', '16x16', '-framerate', '25', '-i', 'pipe:0'];
  const wave = Buffer.alloc(4410 * 2);
  encode(['-f', 's16le', '-ar', '44100', '-ac', '1', '-i', 'pipe:0', 'source.wav'], wave);
  const silentMp3Frame = Buffer.alloc(104);
  silentMp3Frame.writeUInt32BE(0xfffb10c0);
  writeFileSync(join(root, 'source.mp3'), Buffer.concat(Array(8).fill(silentMp3Frame)));
  for (const [file, codec, extra] of [
    ['source.flac', 'flac', []], ['source.aac', 'aac', []], ['source.mov', 'alac', []],
    ['vorbis.ogg', 'vorbis', ['-ac', '2', '-strict', 'experimental']],
    ['opus.ogg', 'opus', ['-ar', '48000', '-strict', 'experimental']],
  ]) {
    encode(['-i', 'source.wav', '-c:a', codec, ...extra, file]);
  }
  for (const file of ['source.wav', 'source.mp3', 'source.flac', 'source.aac', 'source.mov', 'vorbis.ogg', 'opus.ogg']) {
    const samples = encode(['-i', file, '-c:a', 'pcm_f32le', '-f', 'f32le', 'pipe:1']);
    assert.ok(samples.length >= 1000 * 4 && samples.length % 4 === 0, `invalid PCM output from ${file}`);
  }
  encode([...rawVideo, '-i', 'source.wav', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', 'opaque.mp4'], clip);
  assert.deepEqual(probe('opaque.mp4'), [{ codec_name: 'h264', pix_fmt: 'yuv420p' }, { codec_name: 'aac' }]);
  encode([...rawVideo, '-i', 'source.wav', '-c:v', 'png', '-pix_fmt', 'rgba', '-c:a', 'pcm_s16le', 'alpha.mov'], clip);
  assert.deepEqual(probe('alpha.mov'), [{ codec_name: 'png', pix_fmt: 'rgba' }, { codec_name: 'pcm_s16le' }]);
  assert.deepEqual(encode(['-i', 'alpha.mov', '-frames:v', '1', '-pix_fmt', 'rgba', '-f', 'rawvideo', 'pipe:1']), clip);
  encode([...rawVideo, '-c:v', 'prores_ks', '-profile:v', '4444', '-pix_fmt', 'yuva444p10le', 'prores.mov'], clip);
  assert.equal(probe('prores.mov')[0].codec_name, 'prores');

  const manifestPath = join(staged, MANIFEST);
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  writeFileSync(manifestPath, JSON.stringify({ ...manifest, files: { ...manifest.files, 'ffmpeg.exe': hash('substituted') } }));
  assert.throws(() => windowsMediaInputs(root, { 'ffmpeg-dir': staged }), /pin mismatch.*ffmpeg\.exe/);
  writeFileSync(manifestPath, JSON.stringify(manifest));
  const source = 'x264-31e19f92.tar.gz';
  writeFileSync(join(staged, source), 'substituted source');
  assert.throws(() => windowsMediaInputs(root, { 'ffmpeg-dir': staged }), /SHA-256 mismatch.*x264/);
  manifest.files[source] = hash('substituted source');
  writeFileSync(manifestPath, JSON.stringify(manifest));
  assert.throws(() => windowsMediaInputs(root, { 'ffmpeg-dir': staged }), /pin mismatch.*x264/);
});
