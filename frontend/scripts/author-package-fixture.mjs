// Read-only, bounded ZIP inspection. Never extracts or rewrites the supplied HTTP fixture.
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { crc32, inflateRawSync } from 'node:zlib';

export const sha = (value) => createHash('sha256').update(value).digest('hex');
const EDITABLE = new Set(['csharp', 'shader', 'shaderProgram', 'graph', 'svg', 'svgResource']);
const MAX_ARCHIVE = 32 * 1024 * 1024;

function readZip(bytes) {
  if (bytes.length > MAX_ARCHIVE) throw new Error('本轮有限素材 ZIP 超过 32 MiB。');
  let end = -1;
  for (let i = bytes.length - 22; i >= Math.max(0, bytes.length - 65_557); i--) {
    if (bytes.readUInt32LE(i) === 0x06054b50 && i + 22 + bytes.readUInt16LE(i + 20) === bytes.length) { end = i; break; }
  }
  if (end < 0 || bytes.readUInt16LE(end + 4) || bytes.readUInt16LE(end + 6)) throw new Error('仅支持完整单卷 ZIP 素材。');
  const count = bytes.readUInt16LE(end + 10);
  if (count > 512 || count !== bytes.readUInt16LE(end + 8)) throw new Error('素材 ZIP 条目数超出有限范围。');
  let offset = bytes.readUInt32LE(end + 16);
  const directoryEnd = offset + bytes.readUInt32LE(end + 12);
  if (directoryEnd !== end) throw new Error('素材 ZIP 中央目录不完整。');
  const files = new Map();
  let expanded = 0;
  for (let i = 0; i < count; i++) {
    if (offset + 46 > directoryEnd || bytes.readUInt32LE(offset) !== 0x02014b50) throw new Error('素材 ZIP 中央目录条目无效。');
    const flags = bytes.readUInt16LE(offset + 8), method = bytes.readUInt16LE(offset + 10);
    const crc = bytes.readUInt32LE(offset + 16), compressed = bytes.readUInt32LE(offset + 20), size = bytes.readUInt32LE(offset + 24);
    const nameSize = bytes.readUInt16LE(offset + 28), extraSize = bytes.readUInt16LE(offset + 30), commentSize = bytes.readUInt16LE(offset + 32);
    const local = bytes.readUInt32LE(offset + 42);
    const name = bytes.subarray(offset + 46, offset + 46 + nameSize).toString('utf8');
    if (!name || name.startsWith('/') || name.includes('\\') || name.split('/').some((part) => part === '..' || part === '.'))
      throw new Error('素材 ZIP 路径无效。');
    if ((flags & 1) || ![0, 8].includes(method) || local + 30 > bytes.length || bytes.readUInt32LE(local) !== 0x04034b50)
      throw new Error('素材 ZIP 使用了有限场景不支持的编码。');
    const start = local + 30 + bytes.readUInt16LE(local + 26) + bytes.readUInt16LE(local + 28);
    expanded += size;
    if (expanded > MAX_ARCHIVE || start + compressed > offset) throw new Error('素材 ZIP 解压预算或边界无效。');
    const data = method === 0 ? Buffer.from(bytes.subarray(start, start + compressed))
      : inflateRawSync(bytes.subarray(start, start + compressed), { maxOutputLength: MAX_ARCHIVE });
    if (data.length !== size || crc32(data) !== crc) throw new Error('素材 ZIP 字节校验失败。');
    if (!name.endsWith('/')) {
      if (files.has(name)) throw new Error('素材 ZIP 存在重复路径。');
      files.set(name, data);
    }
    offset += 46 + nameSize + extraSize + commentSize;
  }
  if (offset !== directoryEnd) throw new Error('素材 ZIP 目录长度不匹配。');
  return files;
}

export function readVectorFixture(path, requiredBinaryPath) {
  const archive = readFileSync(path), files = readZip(archive);
  const manifestBytes = files.get('manifest.json');
  if (!manifestBytes) throw new Error('素材必须有根 manifest.json。');
  const manifest = JSON.parse(manifestBytes.toString('utf8'));
  if (manifest.packageVersion !== 3 || !/^2\./.test(manifest.sdkVersion) || manifest.exports?.length !== 1
    || manifest.exports[0].kind !== 'controller') throw new Error('本轮要求 package3/SDK2 单 controller 素材。');
  if (!Array.isArray(manifest.files) || manifest.files.length + 1 !== files.size) throw new Error('素材 manifest 必须覆盖全部 ZIP 文件。');
  const byPath = new Map();
  for (const entry of manifest.files) {
    const data = files.get(entry.path);
    if (!data || byPath.has(entry.path) || sha(data) !== entry.sha256) throw new Error('素材 manifest 文件或 SHA 无效。');
    byPath.set(entry.path, { ...entry, data, editable: EDITABLE.has(entry.role) });
  }
  const oneRole = (role) => {
    const matches = [...byPath.values()].filter((file) => file.role === role);
    if (matches.length !== 1) throw new Error(`本轮素材要求恰好一个 ${role} 文件。`);
    return matches[0];
  };
  oneRole('csharp');
  const svg = oneRole('svg'), descriptor = oneRole('svgResource');
  const descriptorJson = JSON.parse(descriptor.data.toString('utf8'));
  if (descriptorJson.mode !== 'vector' || descriptorJson.source !== svg.path
    || !Number.isFinite(descriptorJson.viewport?.width) || descriptorJson.viewport.width <= 0)
    throw new Error('本轮要求引用该 SVG 且有有效 viewport 的 vector descriptor。');
  const svgText = svg.data.toString('utf8');
  const fill = /\bfill=(['"])(#[\da-f]{6})\1/i.exec(svgText);
  if (!fill || !svgText.includes('</svg>')) throw new Error('本轮可视证据要求素材 SVG 有六位纯色 fill 与 svg 闭合标签。');
  const initialColor = fill[2].toLowerCase();
  const repairedColor = initialColor === '#2060e0' ? '#e04020' : '#2060e0';
  const repairedSvg = svgText.replace(fill[0], `fill="${repairedColor}"`).replace('</svg>', '<!-- UI repaired immutable SVG --></svg>');
  const binaries = [...byPath.values()].filter((file) => !file.editable);
  if (requiredBinaryPath && !binaries.some((file) => file.path === requiredBinaryPath)) throw new Error('--binary-path 未指向素材内的只读文件。');
  return { manifest, manifestBytes, archiveSha256: sha(archive), byPath, svg, descriptor, descriptorJson,
    initialColor, repairedColor, repairedSvg, binaries: requiredBinaryPath ? binaries.filter((file) => file.path === requiredBinaryPath) : binaries };
}
