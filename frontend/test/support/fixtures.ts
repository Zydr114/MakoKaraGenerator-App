// 测试素材用代码生成：短 WAV、LRC 与 PNG（设计 §7）。保持在默认 16 MiB 资源上限以内。

import { crc32, deflateSync } from 'node:zlib';

/** 单声道 PCM16 WAV，带一个正弦音，便于音频准备产生非零波形。 */
export function makeWav(seconds = 2, sampleRate = 8000, frequency = 440): Uint8Array<ArrayBuffer> {
  const samples = Math.round(seconds * sampleRate);
  const dataBytes = samples * 2;
  const buffer = new ArrayBuffer(44 + dataBytes);
  const view = new DataView(buffer);
  const ascii = (offset: number, text: string) => [...text].forEach((c, i) => view.setUint8(offset + i, c.charCodeAt(0)));
  ascii(0, 'RIFF');
  view.setUint32(4, 36 + dataBytes, true);
  ascii(8, 'WAVE');
  ascii(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true); // PCM
  view.setUint16(22, 1, true); // mono
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  ascii(36, 'data');
  view.setUint32(40, dataBytes, true);
  for (let i = 0; i < samples; i++) {
    view.setInt16(44 + i * 2, Math.round(Math.sin((2 * Math.PI * frequency * i) / sampleRate) * 8000), true);
  }
  return new Uint8Array(buffer);
}

/** 两行 LRC，第一行含中文。 */
export const SAMPLE_LRC = '[00:00.00]你好，协议控制台\n[00:01.00]Second line\n';

/** 纯色 RGBA PNG（不压缩滤波），用作背景图片。 */
export function makePng(width = 64, height = 36, rgba: [number, number, number, number] = [200, 40, 90, 255]): Uint8Array<ArrayBuffer> {
  const row = 1 + width * 4;
  const raw = new Uint8Array(row * height);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) raw.set(rgba, y * row + 1 + x * 4);
  }
  const chunk = (type: string, data: Uint8Array) => {
    const out = new Uint8Array(12 + data.length);
    const view = new DataView(out.buffer);
    view.setUint32(0, data.length);
    out.set(new TextEncoder().encode(type), 4);
    out.set(data, 8);
    view.setUint32(8 + data.length, crc32(out.subarray(4, 8 + data.length)));
    return out;
  };
  const header = new Uint8Array(13);
  const hv = new DataView(header.buffer);
  hv.setUint32(0, width);
  hv.setUint32(4, height);
  header.set([8, 6, 0, 0, 0], 8); // 8 位、RGBA、deflate、无滤波方法、不隔行
  const parts = [new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header), chunk('IDAT', deflateSync(raw)), chunk('IEND', new Uint8Array())];
  const png = new Uint8Array(new ArrayBuffer(parts.reduce((n, p) => n + p.length, 0)));
  let offset = 0;
  for (const part of parts) {
    png.set(part, offset);
    offset += part.length;
  }
  return png;
}
