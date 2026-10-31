// 工程音频波形（G11，合同 1.7.0，规范 §6.10）。峰值窗口是二进制 minmax-f32le-v1：每桶一对小端 float32（min, max）。

import type { Micros } from './int64';

export interface WaveformLevel {
  level: number;
  /** 十进制 Int64 字符串。 */
  bucketSamples: string;
  bucketCount: number;
}

export interface ProjectWaveformAvailable {
  projectSessionId: string;
  available: true;
  protocolVersion: '1';
  /** 绑定当前实际音频身份，与歌词修订无关。 */
  waveformId: string;
  sourceSha256: string;
  profile: 'self-contained-native-waveform-v1';
  sampleRate: number;
  sampleCount: string;
  durationUs: Micros;
  sourceChannels: number;
  waveformChannels: 1;
  baseBucketSamples: number;
  format: 'minmax-f32le-v1';
  levels: WaveformLevel[];
}

export interface ProjectWaveformUnavailable {
  projectSessionId: string;
  available: false;
  protocolVersion: '1';
  reason: 'noAudio' | 'cacheBudgetExceeded' | 'audioIdentityChanged';
}

export type ProjectWaveform = ProjectWaveformAvailable | ProjectWaveformUnavailable;

/** 单次峰值窗口最多 8192 桶。 */
export const WAVEFORM_WINDOW_MAX_BUCKETS = 8192;

/** 一次峰值窗口请求：某层从 start 起的 count 个桶。count 1..8192，窗口不得越过该层的 bucketCount（后端越尾拒绝，不裁剪）。 */
export interface WaveformWindow {
  waveformId: string;
  level: number;
  start: number;
  count: number;
}

/** minmax-f32le-v1 窗口字节 → 交错的 [min₀, max₀, min₁, max₁, …]。按小端读取，不依赖本机字节序。 */
export function decodePeakWindow(bytes: ArrayBuffer): Float32Array {
  if (bytes.byteLength % 8 !== 0) throw new Error(`峰值窗口长度 ${bytes.byteLength} 不是 8 的倍数`);
  const view = new DataView(bytes);
  const values = new Float32Array(bytes.byteLength / 4);
  for (let i = 0; i < values.length; i++) values[i] = view.getFloat32(i * 4, true);
  return values;
}
