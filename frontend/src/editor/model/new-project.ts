// 新建工程对话框（W5 设计 §1、§2.6）：四种组合、默认名称、文件检查里的音频信息（界面改版批 3）。
// 歌词导入规划的识别结果与改选候选在 lyrics-import.ts。纯函数，不依赖 React。

import { fileStem } from './lyrics-import';
import { formatTime } from './time';

/** 音频文件的选择范围（新建工程、空态、设置共用）。 */
export const AUDIO_FILE_ACCEPT = 'audio/*,.wav,.mp3,.flac,.m4a,.ogg,.aac';

export type NewProjectKind = 'full' | 'lyricsOnly' | 'audioOnly' | 'empty';

export function newProjectKind(hasAudio: boolean, hasLyrics: boolean): NewProjectKind {
  if (hasAudio) return hasLyrics ? 'full' : 'audioOnly';
  return hasLyrics ? 'lyricsOnly' : 'empty';
}

/** 第二步顶部的说明：这次会建什么样的工程、之后去哪里补。 */
export const KIND_TEXT: Record<NewProjectKind, string> = {
  full: '用所选的音频和歌词新建工程。',
  lyricsOnly: '只有歌词：没有时间轴，之后可以在设置里选择音频。',
  audioOnly: '只有音频：之后可以在歌词页导入歌词文件。',
  empty: '会创建一个空工程，之后可以在歌词页导入歌词、在设置里选择音频。',
};

/** 名称不填时依次用音频文件名、歌词文件名、“新工程”。 */
export function defaultProjectName(audioFileName?: string, lyricsFileName?: string): string {
  return (audioFileName && fileStem(audioFileName)) || (lyricsFileName && fileStem(lyricsFileName)) || '新工程';
}

// ── 文件检查：音频 ──

export interface AudioProbe {
  durationUs: number;
  channels: number;
  /** 只有 WAV 能从文件头读出；浏览器解码时会重采样，读不到原始采样率。 */
  sampleRate?: number;
  /** 格式名（WAV、MP3…）。 */
  format: string;
  /** 波形条，每个 0～1。 */
  bars: number[];
}

/** 波形条：把时间等分成 count 段，每段取各声道绝对值最大的样本，按最响的一段归一到 0～1。 */
export function waveBars(channels: readonly Float32Array[], count: number): number[] {
  const length = Math.max(0, ...channels.map((c) => c.length));
  const bars = new Array<number>(count).fill(0);
  if (length === 0 || count <= 0) return bars;
  for (let i = 0; i < count; i++) {
    const from = Math.floor((i * length) / count);
    const to = Math.max(from + 1, Math.floor(((i + 1) * length) / count));
    let peak = 0;
    for (const channel of channels) {
      for (let j = from; j < Math.min(to, channel.length); j++) {
        const v = Math.abs(channel[j]!);
        if (v > peak) peak = v;
      }
    }
    bars[i] = peak;
  }
  const max = Math.max(...bars);
  return max > 0 ? bars.map((b) => Math.min(1, b / max)) : bars;
}

/**
 * WAV 文件头（RIFF/WAVE）：fmt 块里的采样率和声道；有 data 块时按字节数算出精确时长。
 * 不是 WAV 或文件头不完整时为 undefined。只读前面的块，data 块的内容不读。
 */
export function wavFormat(bytes: ArrayBuffer): { sampleRate: number; channels: number; durationUs?: number } | undefined {
  if (bytes.byteLength < 12) return undefined;
  const view = new DataView(bytes);
  const tag = (offset: number) => String.fromCharCode(view.getUint8(offset), view.getUint8(offset + 1), view.getUint8(offset + 2), view.getUint8(offset + 3));
  if (tag(0) !== 'RIFF' || tag(8) !== 'WAVE') return undefined;
  let format: { sampleRate: number; channels: number; byteRate: number } | undefined;
  for (let offset = 12; offset + 8 <= bytes.byteLength; ) {
    const id = tag(offset);
    const size = view.getUint32(offset + 4, true);
    if (id === 'fmt ' && offset + 24 <= bytes.byteLength) {
      format = { channels: view.getUint16(offset + 10, true), sampleRate: view.getUint32(offset + 12, true), byteRate: view.getUint32(offset + 16, true) };
    } else if (id === 'data' && format) {
      const { sampleRate, channels, byteRate } = format;
      return { sampleRate, channels, ...(byteRate > 0 ? { durationUs: Math.round((size / byteRate) * 1_000_000) } : {}) };
    }
    offset += 8 + size + (size % 2);
  }
  return format && format.sampleRate > 0 ? { sampleRate: format.sampleRate, channels: format.channels } : undefined;
}

/** 格式名：扩展名大写；没有扩展名时用 MIME 的子类型。 */
export function audioFormatName(file: { name: string; type: string }): string {
  const ext = /\.([a-z0-9]+)$/i.exec(file.name)?.[1];
  if (ext) return ext.toUpperCase();
  return (file.type.split('/')[1] ?? '音频').toUpperCase();
}

const CHANNEL_TEXT: Record<number, string> = { 1: '单声道', 2: '立体声' };

/** 文件检查里的一行：“3:45.000，48 kHz，立体声，WAV”。 */
export function audioProbeText(probe: AudioProbe): string {
  const rate = probe.sampleRate ? `${Number((probe.sampleRate / 1000).toFixed(2))} kHz` : undefined;
  const channels = CHANNEL_TEXT[probe.channels] ?? `${probe.channels} 声道`;
  return [formatTime(probe.durationUs), rate, channels, probe.format].filter(Boolean).join('，');
}
