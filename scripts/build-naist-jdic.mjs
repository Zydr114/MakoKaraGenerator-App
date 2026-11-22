#!/usr/bin/env node
/**
 * 开发/发布期离线词典构建脚本（不打入客户端安装包）。
 *
 * 输入：官方 mecab-naist-jdic-0.6.3b（纯 BSD-3-Clause）源目录，包含 *.csv、matrix.def、char.def、unk.def、COPYING。
 * 处理：
 *   1. 列裁剪优化：保留 surface、leftId、rightId、cost、pos、basic_form、reading，
 *      清空注音无关的细分类/活用/发音与 NAIST 扩展列，同时保持 IpadicFormatter 索引兼容（pos=1, basic_form=7, reading=8）。
 *   2. 调用 @patdx/kuromoji DictionaryBuilder 编译并 gzip 输出 12 个 *.dat.gz 二进制文件（约 14~16 MB）。
 *   3. 输出 LICENSE.naist-jdic.txt（BSD-3-Clause 原文）与 manifest.json（版本号、单文件/总包 SHA-256 与字节大小）。
 *
 * 用法：
 *   node scripts/build-naist-jdic.mjs --src /path/to/mecab-naist-jdic-0.6.3b --out ./artifacts/naist-jdic-0.6.3b [--version 0.6.3b-pruned-1]
 */

import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { gzipSync } from 'node:zlib';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);

function decodeSourceText(bytes) {
  // Official NAIST-jdic 0.6.3b tarballs are typically EUC-JP; support UTF-8 automatically as well.
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    return new TextDecoder('euc-jp').decode(bytes);
  }
}

/**
 * Simple RFC-4180 CSV line splitter handling quoted commas (used in MeCab dictionaries).
 */
export function parseCsvLine(line) {
  const cols = [];
  let current = '';
  let inQuotes = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (inQuotes) {
      if (ch === '"') {
        if (line[i + 1] === '"') {
          current += '"';
          i++;
        } else {
          inQuotes = false;
        }
      } else {
        current += ch;
      }
    } else if (ch === '"') {
      inQuotes = true;
    } else if (ch === ',') {
      cols.push(current);
      current = '';
    } else {
      current += ch;
    }
  }
  cols.push(current);
  return cols;
}

function escapeCsvCell(value) {
  if (value.includes(',') || value.includes('"') || value.includes('\n')) {
    return `"${value.replace(/"/g, '""')}"`;
  }
  return value;
}

/**
 * Prunes a NAIST-jdic CSV row to keep only furigana-essential columns while preserving
 * kuromoji IpadicFormatter column offsets (surface=0, left=1, right=2, cost=3,
 * pos=4[f1], pruned 5..9[f2..f6]="", basic_form=10[f7], reading=11[f8]).
 */
export function pruneNaistJdicCsvLine(line) {
  const cols = parseCsvLine(line);
  if (cols.length < 12) return null;
  const surface = cols[0] ?? '';
  const leftId = cols[1] ?? '';
  const rightId = cols[2] ?? '';
  const cost = cols[3] ?? '';
  const pos = cols[4] ?? '*';
  const basicForm = cols[10] ?? '*';
  const reading = cols[11] ?? '*';
  return [
    surface,
    leftId,
    rightId,
    cost,
    pos,
    '',
    '',
    '',
    '',
    '',
    basicForm,
    reading,
  ]
    .map(escapeCsvCell)
    .join(',');
}

function toUint8Array(typedOrBuffer) {
  if (typedOrBuffer instanceof Uint8Array) return typedOrBuffer;
  if (ArrayBuffer.isView(typedOrBuffer)) {
    return new Uint8Array(typedOrBuffer.buffer, typedOrBuffer.byteOffset, typedOrBuffer.byteLength);
  }
  return new Uint8Array(typedOrBuffer);
}

function sha256Hex(bytes) {
  return createHash('sha256').update(bytes).digest('hex');
}

async function loadKuromojiBuilder() {
  const kuromojiPath = resolve(import.meta.dirname, '../frontend/node_modules/@patdx/kuromoji/build/index.mjs');
  if (!existsSync(kuromojiPath)) {
    throw new Error(`找不到 @patdx/kuromoji：${kuromojiPath}，请先安装 frontend 依赖。`);
  }
  const mod = await import(kuromojiPath);
  return mod.DictionaryBuilder;
}

async function main() {
  const { values } = parseArgs({
    options: {
      src: { type: 'string' },
      out: { type: 'string' },
      version: { type: 'string', default: '0.6.3b-pruned-1' },
      help: { type: 'boolean', default: false },
    },
  });

  if (values.help || !values.src || !values.out) {
    console.log(
      [
        '用法: node scripts/build-naist-jdic.mjs --src <mecab-naist-jdic-0.6.3b 目录> --out <输出目录> [--version 0.6.3b-pruned-1]',
        '',
        '从官方 BSD-3-Clause NAIST-jdic 0.6.3b 源码裁剪编译 kuromoji 词典并生成 manifest.json。',
      ].join('\n'),
    );
    process.exit(values.help ? 0 : 1);
  }

  const srcDir = resolve(values.src);
  const outDir = resolve(values.out);
  const dictOutDir = join(outDir, 'dict');

  for (const required of ['matrix.def', 'char.def', 'unk.def']) {
    if (!existsSync(join(srcDir, required))) {
      throw new Error(`源目录缺少必需文件：${join(srcDir, required)}`);
    }
  }

  const csvFiles = readdirSync(srcDir)
    .filter((name) => name.toLowerCase().endsWith('.csv'))
    .sort();
  if (csvFiles.length === 0) {
    throw new Error(`源目录未找到任何 .csv 词条文件：${srcDir}`);
  }

  const DictionaryBuilder = await loadKuromojiBuilder();
  const builder = new DictionaryBuilder();

  let entryCount = 0;
  for (const csvName of csvFiles) {
    const text = decodeSourceText(readFileSync(join(srcDir, csvName)));
    for (const rawLine of text.split(/\r?\n/)) {
      const trimmed = rawLine.trim();
      if (!trimmed) continue;
      const pruned = pruneNaistJdicCsvLine(trimmed);
      if (!pruned) continue;
      builder.addTokenInfoDictionary(pruned);
      entryCount++;
    }
  }

  for (const line of decodeSourceText(readFileSync(join(srcDir, 'matrix.def'))).split(/\r?\n/)) {
    if (line.trim()) builder.putCostMatrixLine(line);
  }
  for (const line of decodeSourceText(readFileSync(join(srcDir, 'char.def'))).split(/\r?\n/)) {
    if (line.trim()) builder.putCharDefLine(line);
  }
  for (const line of decodeSourceText(readFileSync(join(srcDir, 'unk.def'))).split(/\r?\n/)) {
    if (line.trim()) builder.putUnkDefLine(line);
  }

  console.log(`[build-naist-jdic] 正在编译 ${entryCount} 条裁剪词条...`);
  const dic = builder.build();

  mkdirSync(dictOutDir, { recursive: true });

  const rawFiles = {
    'base.dat.gz': toUint8Array(dic.trie.bc.getBaseBuffer()),
    'check.dat.gz': toUint8Array(dic.trie.bc.getCheckBuffer()),
    'tid.dat.gz': toUint8Array(dic.token_info_dictionary.dictionary.buffer),
    'tid_pos.dat.gz': toUint8Array(dic.token_info_dictionary.pos_buffer.buffer),
    'tid_map.dat.gz': toUint8Array(dic.token_info_dictionary.targetMapToBuffer()),
    'cc.dat.gz': toUint8Array(dic.connection_costs.buffer),
    'unk.dat.gz': toUint8Array(dic.unknown_dictionary.dictionary.buffer),
    'unk_pos.dat.gz': toUint8Array(dic.unknown_dictionary.pos_buffer.buffer),
    'unk_map.dat.gz': toUint8Array(dic.unknown_dictionary.targetMapToBuffer()),
    'unk_char.dat.gz': toUint8Array(dic.unknown_dictionary.character_definition.character_category_map),
    'unk_compat.dat.gz': toUint8Array(dic.unknown_dictionary.character_definition.compatible_category_map),
    'unk_invoke.dat.gz': toUint8Array(dic.unknown_dictionary.character_definition.invoke_definition_map.toBuffer()),
  };

  const manifestFiles = {};
  let totalBytes = 0;
  const bundleHasher = createHash('sha256');

  for (const [filename, rawBytes] of Object.entries(rawFiles).sort(([a], [b]) => a.localeCompare(b))) {
    const gzBytes = gzipSync(rawBytes, { level: 9 });
    writeFileSync(join(dictOutDir, filename), gzBytes);
    const hash = sha256Hex(gzBytes);
    manifestFiles[filename] = { size: gzBytes.length, sha256: hash };
    totalBytes += gzBytes.length;
    bundleHasher.update(`${filename}:${gzBytes.length}:${hash}\n`);
  }

  const copyingPath = join(srcDir, 'COPYING');
  const licenseText = existsSync(copyingPath)
    ? decodeSourceText(readFileSync(copyingPath))
    : [
        'NAIST Japanese Dictionary (naist-jdic) 0.6.3b',
        'License: BSD-3-Clause',
        'Please include the upstream COPYING file from mecab-naist-jdic-0.6.3b.',
      ].join('\n');
  writeFileSync(join(outDir, 'LICENSE.naist-jdic.txt'), licenseText, 'utf8');

  const manifest = {
    schemaVersion: 1,
    id: 'naist-jdic-0.6.3b',
    name: 'NAIST-jdic 0.6.3b (Pruned for Furigana)',
    version: values.version,
    license: 'BSD-3-Clause',
    entryCount,
    totalBytes,
    bundleSha256: bundleHasher.digest('hex'),
    builtAt: new Date().toISOString(),
    files: manifestFiles,
  };
  writeFileSync(join(outDir, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n', 'utf8');

  const mb = (totalBytes / (1024 * 1024)).toFixed(2);
  console.log(`[build-naist-jdic] 完成：${Object.keys(manifestFiles).length} 个文件，合计 ${mb} MiB -> ${outDir}`);
}

if (import.meta.url === `file://${process.argv[1]}`) {
  await main();
}
