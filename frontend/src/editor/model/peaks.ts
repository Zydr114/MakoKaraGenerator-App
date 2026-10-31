// 波形峰值（W2 设计 §2.3）：时间轴只通过 PeakSource 取每列的 min／max，不关心峰值从哪里来。
// 两个来源：后端波形（backend-peaks.ts，按块懒加载）；后端不可用时，浏览器解码音频后在这里算金字塔。
// 金字塔第 0 层每桶 64 个样本（与后端准备时的桶宽一致），往上逐级两两合并。

export const BASE_BUCKET_SAMPLES = 64;

export interface PeakLevel {
  bucketSamples: number;
  min: Float32Array;
  max: Float32Array;
}

export interface PeakPyramid {
  sampleRate: number;
  sampleCount: number;
  /** levels[0] 最细。 */
  levels: PeakLevel[];
}

/** 多声道合并为一条：每桶取所有声道的最小值与最大值。最粗一层不超过 minTopBuckets 桶后停止。 */
export function buildPyramid(channels: readonly Float32Array[], sampleRate: number, minTopBuckets = 256): PeakPyramid {
  const sampleCount = channels.reduce((n, c) => Math.max(n, c.length), 0);
  const baseCount = Math.ceil(sampleCount / BASE_BUCKET_SAMPLES);
  const min = new Float32Array(baseCount).fill(Infinity);
  const max = new Float32Array(baseCount).fill(-Infinity);
  for (const channel of channels) {
    for (let bucket = 0; bucket < baseCount; bucket++) {
      const from = bucket * BASE_BUCKET_SAMPLES;
      const to = Math.min(channel.length, from + BASE_BUCKET_SAMPLES);
      let lo = min[bucket]!;
      let hi = max[bucket]!;
      for (let i = from; i < to; i++) {
        const v = channel[i]!;
        if (v < lo) lo = v;
        if (v > hi) hi = v;
      }
      min[bucket] = lo;
      max[bucket] = hi;
    }
  }
  for (let bucket = 0; bucket < baseCount; bucket++) {
    if (min[bucket] === Infinity) {
      min[bucket] = 0;
      max[bucket] = 0;
    }
  }

  const levels: PeakLevel[] = [{ bucketSamples: BASE_BUCKET_SAMPLES, min, max }];
  for (let top = levels[0]!; top.min.length > minTopBuckets; top = levels[levels.length - 1]!) {
    const count = Math.ceil(top.min.length / 2);
    const nextMin = new Float32Array(count);
    const nextMax = new Float32Array(count);
    for (let i = 0; i < count; i++) {
      const a = 2 * i;
      const b = Math.min(a + 1, top.min.length - 1);
      nextMin[i] = Math.min(top.min[a]!, top.min[b]!);
      nextMax[i] = Math.max(top.max[a]!, top.max[b]!);
    }
    levels.push({ bucketSamples: top.bucketSamples * 2, min: nextMin, max: nextMax });
  }
  return { sampleRate, sampleCount, levels };
}

/**
 * 选最粗但每桶样本数不超过半列的层；放大到比第 0 层还细时用第 0 层。
 * 桶与列边界不对齐时，列会多算进最多一个桶；取半列使这种外溢不超过半个像素。
 */
export function chooseLevel(pyramid: PeakPyramid, samplesPerColumn: number): PeakLevel {
  return pyramid.levels[chooseLevelIndex(pyramid.levels, samplesPerColumn)]!;
}

/** chooseLevel 的下标版本；levels 按桶宽从细到粗排列。 */
export function chooseLevelIndex(levels: readonly { bucketSamples: number }[], samplesPerColumn: number): number {
  let chosen = 0;
  levels.forEach((level, index) => {
    if (level.bucketSamples <= samplesPerColumn / 2) chosen = index;
  });
  return chosen;
}

export interface ColumnPeaks {
  /** 每列的最小／最大值；该列没有音频时为 NaN。 */
  min: Float32Array;
  max: Float32Array;
}

/** 视口的每个像素列聚合一对 min/max。startUs 可以为负或超出音频，超出部分为 NaN。 */
export function columnPeaks(pyramid: PeakPyramid, startUs: number, usPerPx: number, columns: number): ColumnPeaks {
  const min = new Float32Array(columns).fill(NaN);
  const max = new Float32Array(columns).fill(NaN);
  const samplesPerUs = pyramid.sampleRate / 1_000_000;
  const level = chooseLevel(pyramid, usPerPx * samplesPerUs);
  const buckets = level.min.length;
  for (let c = 0; c < columns; c++) {
    const s0 = (startUs + c * usPerPx) * samplesPerUs;
    const s1 = (startUs + (c + 1) * usPerPx) * samplesPerUs;
    if (s1 <= 0 || s0 >= pyramid.sampleCount) continue;
    const b0 = Math.max(0, Math.floor(s0 / level.bucketSamples));
    const b1 = Math.min(buckets, Math.max(b0 + 1, Math.ceil(s1 / level.bucketSamples)));
    let lo = Infinity;
    let hi = -Infinity;
    for (let b = b0; b < b1; b++) {
      if (level.min[b]! < lo) lo = level.min[b]!;
      if (level.max[b]! > hi) hi = level.max[b]!;
    }
    if (lo !== Infinity) {
      min[c] = lo;
      max[c] = hi;
    }
  }
  return { min, max };
}

/** 时间轴的峰值来源。 */
export interface PeakSource {
  readonly kind: 'backend' | 'browser';
  readonly sampleRate: number;
  readonly sampleCount: number;
  /** 视口每列的 min／max。后端来源会顺便按需请求缺的块；还没有数据的列为 NaN。 */
  columns(startUs: number, usPerPx: number, columns: number): ColumnPeaks;
  /** 新数据到达、需要重画时通知。 */
  subscribe(listener: () => void): () => void;
  dispose(): void;
}

/** 浏览器自算的金字塔：数据一次到齐，不会再变。 */
export function pyramidSource(pyramid: PeakPyramid): PeakSource {
  return {
    kind: 'browser',
    sampleRate: pyramid.sampleRate,
    sampleCount: pyramid.sampleCount,
    columns: (startUs, usPerPx, columns) => columnPeaks(pyramid, startUs, usPerPx, columns),
    subscribe: () => () => {},
    dispose: () => {},
  };
}
