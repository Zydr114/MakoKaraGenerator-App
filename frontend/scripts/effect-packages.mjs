// 测试素材：把后端 tests/Fixtures/ScriptEffectsV2 下的测试夹具效果包打成 .mkfx（ZIP，根目录是 manifest.json；W7 设计 §4）。
// 集成测试和 smoke 共用。找不到目录时 sampleEffectsDir() 返回 null，调用方跳过并写明原因。

import { createHash } from 'node:crypto';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { crc32, deflateRawSync, deflateSync } from 'node:zlib';
import { resolveBackendRepo } from './backend-launcher.mjs';

/** 测试夹具效果包目录；不存在时为 null。 */
export function sampleEffectsDir() {
  const dir = join(resolveBackendRepo(), 'tests', 'Fixtures', 'ScriptEffectsV2');
  return existsSync(join(dir, 'WordEmphasis', 'manifest.json')) ? dir : null;
}

/** 把一个测试夹具包（例如 WordEmphasis）打成 .mkfx 的字节；找不到夹具目录时抛错。 */
export function packSampleEffect(name) {
  const root = sampleEffectsDir();
  if (!root) throw new Error(`找不到后端的测试夹具效果包（${resolveBackendRepo()}）；请检查 tests/Fixtures/ScriptEffectsV2。`);
  const dir = join(root, name);
  return zipFiles(listFiles(dir).map((path) => ({ name: relative(dir, path).split(sep).join('/'), data: readFileSync(path) })));
}

/**
 * 在示例包上改出来的测试包（W8 设计 §4），运行时生成，不提交二进制：
 * - WordEmphasisCurves：WordEmphasis 加两个曲线参数（缓动、贝塞尔），脚本不读它们；
 * - SpriteTrailImages：SpriteTrail 多一张图片 assets/square.png（16×16 不透明白色方块），图片参数可以在两张之间换。
 * 包 ID 与原包不同，可以和原包同时导入。
 */
export function packVariantEffect(variant) {
  const root = sampleEffectsDir();
  if (!root) throw new Error(`找不到后端的测试夹具效果包（${resolveBackendRepo()}）；请检查 tests/Fixtures/ScriptEffectsV2。`);
  const base = { WordEmphasisCurves: 'WordEmphasis', SpriteTrailImages: 'SpriteTrail' }[variant];
  if (!base) throw new Error(`没有测试包 ${variant}`);
  const dir = join(root, base);
  const files = listFiles(dir)
    .map((path) => ({ name: relative(dir, path).split(sep).join('/'), data: readFileSync(path) }))
    .filter((f) => f.name !== 'manifest.json');
  // manifest 按文本改：SpriteTrail 的上限是 Int64 最大值，经过 JSON.parse 会丢精度。
  let manifest = readFileSync(join(dir, 'manifest.json'), 'utf8');
  const replace = (from, to) => {
    if (!manifest.includes(from)) throw new Error(`${base}/manifest.json 里没有 ${from}`);
    manifest = manifest.replace(from, to);
  };
  if (variant === 'WordEmphasisCurves') {
    replace('"packageId": "makokara.v2.word-emphasis"', '"packageId": "makokara.v2.word-emphasis-curves"');
    replace('"name": "逐字强调"', '"name": "逐字强调（曲线）"');
    replace(
      '"parameters": [',
      '"parameters": [\n    { "id": "shape", "type": "curve", "label": "放大曲线", "default": { "kind": "easing", "easing": "easeOut" } },' +
        '\n    { "id": "ease", "type": "curve", "label": "回落曲线", "default": { "kind": "cubicBezier", "first": [0.42, 0], "second": [0.58, 1] } },',
    );
    replace('"parameters": [\n        "peakScale",', '"parameters": [\n        "shape",\n        "ease",\n        "peakScale",');
  } else {
    const square = solidPng(16, [255, 255, 255, 255]);
    files.push({ name: 'assets/square.png', data: square });
    replace('"packageId": "makokara.v2.sprite-trail"', '"packageId": "makokara.v2.sprite-trail-images"');
    replace('"name": "图片粒子与固定拖尾",\n  "version"', '"name": "图片粒子（两张图片）",\n  "version"');
    replace('"files": [', `"files": [\n    { "path": "assets/square.png", "role": "image", "sha256": "${createHash('sha256').update(square).digest('hex')}" },`);
  }
  return zipFiles([{ name: 'manifest.json', data: Buffer.from(manifest, 'utf8') }, ...files]);
}

/** 单色 RGBA PNG。 */
function solidPng(size, [r, g, b, a]) {
  const row = Buffer.alloc(1 + size * 4);
  for (let x = 0; x < size; x++) row.set([r, g, b, a], 1 + x * 4);
  const chunk = (type, data) => {
    const head = Buffer.alloc(8);
    head.writeUInt32BE(data.length, 0);
    head.write(type, 4, 'ascii');
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), data])), 0);
    return Buffer.concat([head, data, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr.set([8, 6, 0, 0, 0], 8);
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(Buffer.concat(Array.from({ length: size }, () => row)))),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

function listFiles(dir) {
  return readdirSync(dir)
    .sort()
    .flatMap((entry) => {
      const path = join(dir, entry);
      return statSync(path).isDirectory() ? listFiles(path) : [path];
    });
}

/** 最小的 ZIP 写出：deflate、UTF-8 文件名、固定时间戳，同样的输入得到同样的字节。 */
export function zipFiles(files) {
  const locals = [];
  const centrals = [];
  let offset = 0;
  for (const file of files) {
    const name = Buffer.from(file.name, 'utf8');
    const data = deflateRawSync(file.data);
    const crc = crc32(file.data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4); // 需要的版本
    local.writeUInt16LE(0x0800, 6); // UTF-8 文件名
    local.writeUInt16LE(8, 8); // deflate
    local.writeUInt16LE(0, 10); // 时间
    local.writeUInt16LE(0x5b21, 12); // 2025-09-01
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(file.data.length, 22);
    local.writeUInt16LE(name.length, 26);
    local.writeUInt16LE(0, 28);
    locals.push(local, name, data);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(8, 10);
    central.writeUInt16LE(0, 12);
    central.writeUInt16LE(0x5b21, 14);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(data.length, 20);
    central.writeUInt32LE(file.data.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt32LE(offset, 42);
    centrals.push(central, name);
    offset += local.length + name.length + data.length;
  }
  const directory = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(files.length, 8);
  end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(directory.length, 12);
  end.writeUInt32LE(offset, 16);
  return new Uint8Array(Buffer.concat([...locals, directory, end]));
}
