// 后端波形（G11，W2 设计 §2.3）：元数据给出各层的桶宽与桶数，峰值按 8192 桶一块懒加载。
// 一个来源对象只对应一个 waveformId，块缓存跟着对象走；音频换了就换一个对象。
// 请求在途或失败时，用已加载的更粗一层顶上；块到达后通知重画。不依赖 React，取数函数由外部注入。

import { WAVEFORM_WINDOW_MAX_BUCKETS, type ProjectWaveformAvailable } from '../../protocol';
import { chooseLevelIndex, type ColumnPeaks, type PeakSource } from './peaks';

/** 每块的桶数，等于单次窗口上限。 */
export const CHUNK_BUCKETS = WAVEFORM_WINDOW_MAX_BUCKETS;

export interface PeakWindowRequest {
  /** 后端的层号。 */
  level: number;
  start: number;
  count: number;
}

/** 取一段窗口，返回 count 对交错的 (min, max)。 */
export type PeakWindowFetcher = (request: PeakWindowRequest, signal: AbortSignal) => Promise<Float32Array>;

export interface BackendPeakOptions {
  /** 同时在途的请求数。 */
  maxInFlight?: number;
  /** 单块失败后最多重试几次。 */
  maxAttempts?: number;
  /** 这类错误说明整个波形已失效（例如 waveformId 过期 404），不再重试，交给 onLost。 */
  isFatal?: (error: unknown) => boolean;
  onLost?: (error: unknown) => void;
  now?: () => number;
  schedule?: (callback: () => void, ms: number) => void;
}

interface Level {
  level: number;
  bucketSamples: number;
  bucketCount: number;
}

interface Failure {
  attempts: number;
  retryAt: number;
}

const RETRY_BASE_MS = 1000;

export class BackendPeakSource implements PeakSource {
  readonly kind = 'backend';
  readonly sampleRate: number;
  readonly sampleCount: number;
  readonly waveformId: string;
  /** 按桶宽从细到粗。 */
  private readonly levels: Level[];
  /** 概览层：一块就能取完的最细一层，先整层取回，任何位置都有东西可画（后端会一直合并到只剩 1 桶，最粗几层太糙）。 */
  private readonly overview: number;
  private readonly chunks = new Map<string, Float32Array>();
  private readonly inFlight = new Set<string>();
  private readonly failures = new Map<string, Failure>();
  private readonly listeners = new Set<() => void>();
  private readonly abort = new AbortController();
  private wanted: string[] = [];
  private lost = false;

  constructor(
    meta: ProjectWaveformAvailable,
    private readonly fetchWindow: PeakWindowFetcher,
    private readonly options: BackendPeakOptions = {},
  ) {
    this.waveformId = meta.waveformId;
    this.sampleRate = meta.sampleRate;
    this.sampleCount = Number(meta.sampleCount);
    this.levels = meta.levels
      .map((l) => ({ level: l.level, bucketSamples: Number(l.bucketSamples), bucketCount: l.bucketCount }))
      .filter((l) => l.bucketSamples > 0 && l.bucketCount > 0)
      .sort((a, b) => a.bucketSamples - b.bucketSamples);
    const fits = this.levels.findIndex((l) => l.bucketCount <= CHUNK_BUCKETS);
    this.overview = fits >= 0 ? fits : this.levels.length - 1;
    if (this.overview >= 0) this.want(this.overviewKeys());
  }

  /** 已加载的块数（冒烟与调试用）。 */
  get loadedChunks(): number {
    return this.chunks.size;
  }

  columns(startUs: number, usPerPx: number, columns: number): ColumnPeaks {
    const min = new Float32Array(columns).fill(NaN);
    const max = new Float32Array(columns).fill(NaN);
    if (this.levels.length === 0 || columns <= 0) return { min, max };
    const samplesPerUs = this.sampleRate / 1_000_000;
    const chosen = chooseLevelIndex(this.levels, usPerPx * samplesPerUs);
    const first = Math.max(0, startUs * samplesPerUs);
    const last = Math.min(this.sampleCount, (startUs + columns * usPerPx) * samplesPerUs);
    if (last <= first) return { min, max };
    this.requestVisible(chosen, first, last);

    for (let c = 0; c < columns; c++) {
      const s0 = (startUs + c * usPerPx) * samplesPerUs;
      const s1 = (startUs + (c + 1) * usPerPx) * samplesPerUs;
      if (s1 <= 0 || s0 >= this.sampleCount) continue;
      for (let index = chosen; index < this.levels.length; index++) {
        if (this.fill(index, s0, s1, c, min, max)) break;
      }
    }
    return { min, max };
  }

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  dispose(): void {
    this.abort.abort();
    this.listeners.clear();
    this.wanted = [];
  }

  /** 一列在某层上的桶都已加载时写入并返回 true。桶与列边界的取法与 columnPeaks 相同。 */
  private fill(index: number, s0: number, s1: number, column: number, min: Float32Array, max: Float32Array): boolean {
    const level = this.levels[index]!;
    const b0 = Math.max(0, Math.floor(s0 / level.bucketSamples));
    if (b0 >= level.bucketCount) return false;
    const b1 = Math.min(level.bucketCount, Math.max(b0 + 1, Math.ceil(s1 / level.bucketSamples)));
    let lo = Infinity;
    let hi = -Infinity;
    let chunkIndex = -1;
    let data: Float32Array | undefined;
    for (let b = b0; b < b1; b++) {
      const at = Math.floor(b / CHUNK_BUCKETS);
      if (at !== chunkIndex) {
        chunkIndex = at;
        data = this.chunks.get(key(index, at));
        if (!data) return false;
      }
      const offset = (b - at * CHUNK_BUCKETS) * 2;
      const a = data![offset]!;
      const z = data![offset + 1]!;
      if (a < lo) lo = a;
      if (z > hi) hi = z;
    }
    if (lo === Infinity) return false;
    min[column] = lo;
    max[column] = hi;
    return true;
  }

  /** 概览层没取完时始终优先；然后是视口需要的块，两侧各多取一块方便滚动。 */
  private requestVisible(index: number, first: number, last: number): void {
    const level = this.levels[index]!;
    const b0 = Math.floor(first / level.bucketSamples);
    const b1 = Math.min(level.bucketCount, Math.ceil(last / level.bucketSamples));
    const keys = [...this.chunkRange(index, b0, b1)];
    const c0 = Math.floor(b0 / CHUNK_BUCKETS);
    const c1 = Math.floor(Math.max(b0, b1 - 1) / CHUNK_BUCKETS);
    const chunkCount = Math.ceil(level.bucketCount / CHUNK_BUCKETS);
    if (c0 > 0) keys.push(key(index, c0 - 1));
    if (c1 + 1 < chunkCount) keys.push(key(index, c1 + 1));
    this.want([...this.overviewKeys(), ...keys]);
  }

  private overviewKeys(): string[] {
    return [...this.chunkRange(this.overview, 0, this.levels[this.overview]!.bucketCount)];
  }

  private *chunkRange(index: number, b0: number, b1: number): Generator<string> {
    if (b1 <= b0) return;
    for (let c = Math.floor(b0 / CHUNK_BUCKETS); c <= Math.floor((b1 - 1) / CHUNK_BUCKETS); c++) yield key(index, c);
  }

  /** 替换待取队列（只保留最近一次视口要的块），然后补满在途名额。 */
  private want(keys: string[]): void {
    if (this.lost || this.abort.signal.aborted) return;
    this.wanted = keys.filter((k) => !this.chunks.has(k) && !this.inFlight.has(k));
    this.pump();
  }

  private pump(): void {
    const limit = this.options.maxInFlight ?? 2;
    const now = (this.options.now ?? Date.now)();
    while (this.inFlight.size < limit && this.wanted.length > 0) {
      const next = this.wanted.shift()!;
      if (this.chunks.has(next) || this.inFlight.has(next)) continue;
      const failure = this.failures.get(next);
      if (failure && (failure.attempts >= (this.options.maxAttempts ?? 3) || now < failure.retryAt)) continue;
      this.load(next);
    }
  }

  private load(chunkKey: string): void {
    const [index, chunk] = chunkKey.split(':').map(Number) as [number, number];
    const level = this.levels[index]!;
    const start = chunk * CHUNK_BUCKETS;
    const count = Math.min(CHUNK_BUCKETS, level.bucketCount - start);
    this.inFlight.add(chunkKey);
    this.fetchWindow({ level: level.level, start, count }, this.abort.signal)
      .then((values) => {
        if (values.length !== count * 2) throw new Error(`峰值窗口应有 ${count} 个桶，实际 ${values.length / 2} 个`);
        this.chunks.set(chunkKey, values);
        this.failures.delete(chunkKey);
        this.notify();
      })
      .catch((error: unknown) => {
        if (this.abort.signal.aborted) return;
        if (this.options.isFatal?.(error)) {
          this.lost = true;
          this.wanted = [];
          this.options.onLost?.(error);
          return;
        }
        const attempts = (this.failures.get(chunkKey)?.attempts ?? 0) + 1;
        const delay = RETRY_BASE_MS * 2 ** (attempts - 1);
        this.failures.set(chunkKey, { attempts, retryAt: (this.options.now ?? Date.now)() + delay });
        // 到点通知重画，重画时 columns() 会再次请求还缺的块。
        if (attempts < (this.options.maxAttempts ?? 3)) (this.options.schedule ?? defaultSchedule)(() => this.notify(), delay);
      })
      .finally(() => {
        this.inFlight.delete(chunkKey);
        this.pump();
      });
  }

  private notify(): void {
    for (const listener of [...this.listeners]) listener();
  }
}

function key(index: number, chunk: number): string {
  return `${index}:${chunk}`;
}

function defaultSchedule(callback: () => void, ms: number): void {
  setTimeout(callback, ms);
}
