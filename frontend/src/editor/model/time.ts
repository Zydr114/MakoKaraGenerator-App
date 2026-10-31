// 时间换算：编辑器内部用整数微秒（number），只在协议边界转成 Micros 字符串。
// 一首歌的微秒数远小于 2^53，number 足够；超出安全整数时由 protocol 的 microsToNumber 拦下。

import { micros, microsToNumber, type LyricTime, type Micros } from '../../protocol';

export const US_PER_SECOND = 1_000_000;

export interface FrameRate {
  numerator: number;
  denominator: number;
}

/** 协议时间 → 编辑器时间；缺失端点为 undefined。 */
export function fromLyricTime(time: LyricTime): { startUs?: number; endUs?: number } {
  return {
    ...(time.startUs !== undefined ? { startUs: microsToNumber(time.startUs) } : {}),
    ...(time.endUs !== undefined ? { endUs: microsToNumber(time.endUs) } : {}),
  };
}

export function toMicros(us: number): Micros {
  if (!Number.isSafeInteger(us)) throw new RangeError(`时间必须是安全整数微秒：${us}`);
  return micros(us);
}

/** 秒（例如 audio.currentTime）→ 整数微秒。 */
export function secondsToUs(seconds: number): number {
  return Math.round(seconds * US_PER_SECOND);
}

export function usToSeconds(us: number): number {
  return us / US_PER_SECOND;
}

/** `m:ss.mmm`；一小时以上为 `h:mm:ss.mmm`。负数前加 `-`。 */
export function formatTime(us: number, options: { millis?: boolean } = {}): string {
  const { millis = true } = options;
  const sign = us < 0 ? '-' : '';
  const totalMs = Math.round(Math.abs(us) / 1000);
  const ms = totalMs % 1000;
  const totalSeconds = Math.floor(totalMs / 1000);
  const s = totalSeconds % 60;
  const totalMinutes = Math.floor(totalSeconds / 60);
  const m = totalMinutes % 60;
  const h = Math.floor(totalMinutes / 60);
  const tail = `${String(s).padStart(2, '0')}${millis ? `.${String(ms).padStart(3, '0')}` : ''}`;
  return h > 0 ? `${sign}${h}:${String(m).padStart(2, '0')}:${tail}` : `${sign}${m}:${tail}`;
}

/**
 * 解析用户输入的时间：`ss`、`ss.fff`、`m:ss(.fff)`、`h:mm:ss(.fff)`。
 * 小数部分最多 6 位（微秒）。无法解析返回 null，不猜测。
 */
export function parseTime(text: string): number | null {
  const trimmed = text.trim();
  const match = /^(-)?(?:(?:(\d+):)?(\d{1,2}):)?(\d+)(?:\.(\d{1,6}))?$/.exec(trimmed);
  if (!match) return null;
  const [, sign, h, m, s, frac] = match;
  const hours = h ? Number(h) : 0;
  const minutes = m ? Number(m) : 0;
  const seconds = Number(s);
  if ((m !== undefined || h !== undefined) && seconds >= 60) return null;
  if (h !== undefined && minutes >= 60) return null;
  const fracUs = frac ? Number(frac.padEnd(6, '0')) : 0;
  const us = ((hours * 60 + minutes) * 60 + seconds) * US_PER_SECOND + fracUs;
  if (!Number.isSafeInteger(us)) return null;
  return sign ? -us : us;
}

/** 一帧的微秒数（可能不是整数，例如 30000/1001）。 */
export function frameDurationUs(rate: FrameRate): number {
  return (US_PER_SECOND * rate.denominator) / rate.numerator;
}

/** 时间所在帧号（向下取整到帧起点）。 */
export function frameIndexAt(us: number, rate: FrameRate): number {
  return Math.floor((us * rate.numerator) / (rate.denominator * US_PER_SECOND));
}

/** 帧起点的整数微秒（向上取整，保证落在该帧内）。 */
export function frameStartUs(frame: number, rate: FrameRate): number {
  return Math.ceil((frame * rate.denominator * US_PER_SECOND) / rate.numerator);
}

/** 吸附到最近的帧起点。 */
export function snapToFrame(us: number, rate: FrameRate): number {
  const exact = (us * rate.numerator) / (rate.denominator * US_PER_SECOND);
  return frameStartUs(Math.round(exact), rate);
}
