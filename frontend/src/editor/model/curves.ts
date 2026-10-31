// 效果包的曲线参数（W8 设计 §2.9）：解析、校验、写出、求值与换类型。不依赖 React。
// 规则与后端一致（Core EffectPackageParameterParser.ReadCurve，求值见 Scripting CurveValue）：
// - easing：{"easing","kind"}，easing 是 linear／easeOut／easeInOut；
// - cubicBezier：{"first","kind","second"}，控制点 [x,y]，x 在 [0,1]，y 不限（允许过冲）；
// - controlPoints：{"kind","points"}，首尾是 (0,0)、(1,1)，x 严格递增，y 不限。
// 各种形式的键都不能多也不能少。写出时键按名称排序、数值保留 4 位小数，与后端的规范形式一致。

export const EASINGS = ['linear', 'easeOut', 'easeInOut'] as const;
export type Easing = (typeof EASINGS)[number];

export const EASING_LABEL: Record<Easing, string> = { linear: '线性', easeOut: '减速', easeInOut: '先加速后减速' };

export type CurvePoint = readonly [number, number];

export type Curve =
  | { kind: 'easing'; easing: Easing }
  | { kind: 'cubicBezier'; first: CurvePoint; second: CurvePoint }
  | { kind: 'controlPoints'; points: readonly CurvePoint[] };

export type CurveKind = Curve['kind'];

export const CURVE_KIND_LABEL: Record<CurveKind, string> = { easing: '缓动', cubicBezier: '贝塞尔', controlPoints: '折线' };

/** 折线相邻两点 x 至少相差这么多（图形编辑时的限制；写出后 4 位小数仍严格递增）。 */
export const MIN_POINT_GAP = 0.001;

/** 解析并校验曲线原文；不合法时返回原因（用后端的说法）。 */
export function readCurve(raw: string): { curve: Curve } | { problem: string } {
  let value: unknown;
  try {
    value = JSON.parse(raw);
  } catch {
    return { problem: '不是合法的 JSON。' };
  }
  if (!isRecord(value)) return { problem: '曲线要写成对象。' };
  const keys = Object.keys(value).sort().join(',');
  switch (value.kind) {
    case 'easing':
      if (keys !== 'easing,kind') return { problem: '缓动曲线只有 kind 和 easing 两个字段。' };
      return EASINGS.includes(value.easing as Easing) ? { curve: { kind: 'easing', easing: value.easing as Easing } } : { problem: '未知缓动。' };
    case 'cubicBezier': {
      if (keys !== 'first,kind,second') return { problem: '贝塞尔曲线只有 kind、first、second 三个字段。' };
      const first = readPoint(value.first);
      if (typeof first === 'string') return { problem: first };
      const second = readPoint(value.second);
      if (typeof second === 'string') return { problem: second };
      return { curve: { kind: 'cubicBezier', first, second } };
    }
    case 'controlPoints': {
      if (keys !== 'kind,points') return { problem: '折线只有 kind 和 points 两个字段。' };
      if (!Array.isArray(value.points)) return { problem: 'points 要是数组。' };
      const points: CurvePoint[] = [];
      for (const item of value.points) {
        const point = readPoint(item);
        if (typeof point === 'string') return { problem: point };
        points.push(point);
      }
      return controlPointsValid(points) ? { curve: { kind: 'controlPoints', points } } : { problem: '折线须有首尾 (0,0)、(1,1) 且 X 严格递增。' };
    }
    default:
      return { problem: '未知曲线类型。' };
  }
}

export function curveProblem(raw: string): string | undefined {
  const result = readCurve(raw);
  return 'problem' in result ? result.problem : undefined;
}

/** 规范写法：键按名称排序，数值最多 4 位小数。 */
export function curveToJson(curve: Curve): string {
  const point = (p: CurvePoint) => `[${num(p[0])},${num(p[1])}]`;
  switch (curve.kind) {
    case 'easing':
      return `{"easing":"${curve.easing}","kind":"easing"}`;
    case 'cubicBezier':
      return `{"first":${point(curve.first)},"kind":"cubicBezier","second":${point(curve.second)}}`;
    case 'controlPoints':
      return `{"kind":"controlPoints","points":[${curve.points.map(point).join(',')}]}`;
  }
}

/** 在进度 p（0～1）上求值，不夹值；与后端 CurveValue.Evaluate 相同。 */
export function evaluateCurve(curve: Curve, p: number): number {
  switch (curve.kind) {
    case 'easing':
      if (curve.easing === 'linear') return p;
      if (curve.easing === 'easeOut') return 1 - (1 - p) ** 3;
      return p * p * (3 - 2 * p);
    case 'cubicBezier': {
      if (p === 0 || p === 1) return p;
      let low = 0;
      let high = 1;
      // 与后端相同的固定 56 次二分。
      for (let i = 0; i < 56; i++) {
        const middle = (low + high) / 2;
        if (bezier(middle, curve.first[0], curve.second[0]) < p) low = middle;
        else high = middle;
      }
      return bezier((low + high) / 2, curve.first[1], curve.second[1]);
    }
    case 'controlPoints': {
      const points = curve.points;
      for (let i = 1; i < points.length; i++) {
        const [x1, y1] = points[i]!;
        if (p <= x1) {
          const [x0, y0] = points[i - 1]!;
          return y0 + ((p - x0) / (x1 - x0)) * (y1 - y0);
        }
      }
      return points.at(-1)![1];
    }
  }
}

/** 画图用的采样点（n+1 个，含两端）。 */
export function sampleCurve(curve: Curve, n = 64): readonly CurvePoint[] {
  if (curve.kind === 'controlPoints') return curve.points;
  return Array.from({ length: n + 1 }, (_, i) => [i / n, evaluateCurve(curve, i / n)] as const);
}

/** 纵轴范围：包住曲线（贝塞尔包括控制点），至少 −0.25～1.25。 */
export function curveYRange(curve: Curve): [number, number] {
  const ys = sampleCurve(curve).map((p) => p[1]);
  if (curve.kind === 'cubicBezier') ys.push(curve.first[1], curve.second[1]);
  return [Math.min(-0.25, ...ys), Math.max(1.25, ...ys)];
}

/** 贝塞尔的常用预设，对应三种缓动（CSS 的 linear、ease-out、ease-in-out）。 */
export const BEZIER_PRESETS: Record<Easing, { first: CurvePoint; second: CurvePoint }> = {
  linear: { first: [0, 0], second: [1, 1] },
  easeOut: { first: [0, 0], second: [0.58, 1] },
  easeInOut: { first: [0.42, 0], second: [0.58, 1] },
};

/** 换类型时的近似转换：缓动→贝塞尔取预设，其余→折线取 5 个采样点，→缓动取“减速”（线性的折线或贝塞尔取“线性”）。 */
export function convertCurve(curve: Curve, kind: CurveKind): Curve {
  if (curve.kind === kind) return curve;
  switch (kind) {
    case 'easing':
      return { kind: 'easing', easing: isLinear(curve) ? 'linear' : 'easeOut' };
    case 'cubicBezier':
      return { kind: 'cubicBezier', ...BEZIER_PRESETS[curve.kind === 'easing' ? curve.easing : isLinear(curve) ? 'linear' : 'easeOut'] };
    case 'controlPoints':
      return { kind: 'controlPoints', points: [0, 0.25, 0.5, 0.75, 1].map((x) => [x, x === 0 || x === 1 ? x : round4(evaluateCurve(curve, x))] as const) };
  }
}

// ── 图形编辑 ───────────────────────────────────────────

/** 贝塞尔控制点：x 夹到 [0,1]，y 不限。 */
export function moveBezierPoint(curve: Extract<Curve, { kind: 'cubicBezier' }>, which: 'first' | 'second', x: number, y: number): Curve {
  return { ...curve, [which]: [round4(clamp(x, 0, 1)), round4(y)] as const };
}

/** 折线的中间点：x 夹在左右邻点之间（至少相差 MIN_POINT_GAP）；首尾不能动。 */
export function movePoint(curve: Extract<Curve, { kind: 'controlPoints' }>, index: number, x: number, y: number): Curve {
  const points = curve.points;
  if (index <= 0 || index >= points.length - 1) return curve;
  const left = points[index - 1]![0] + MIN_POINT_GAP;
  const right = points[index + 1]![0] - MIN_POINT_GAP;
  const next = [...points];
  next[index] = [round4(clamp(x, left, right)), round4(y)];
  return { kind: 'controlPoints', points: next };
}

/** 在 x 处加一个点（y 取当前折线在 x 处的值，或给定值）；离已有点太近时不加，返回 undefined。 */
export function insertPoint(curve: Extract<Curve, { kind: 'controlPoints' }>, x: number, y?: number): { curve: Curve; index: number } | undefined {
  const px = round4(x);
  const points = curve.points;
  const index = points.findIndex((p) => p[0] > px);
  if (index <= 0) return undefined;
  if (px - points[index - 1]![0] < MIN_POINT_GAP || points[index]![0] - px < MIN_POINT_GAP) return undefined;
  const next = [...points];
  next.splice(index, 0, [px, round4(y ?? evaluateCurve(curve, px))]);
  return { curve: { kind: 'controlPoints', points: next }, index };
}

/** 删掉一个中间点；首尾不能删。 */
export function removePoint(curve: Extract<Curve, { kind: 'controlPoints' }>, index: number): Curve {
  if (index <= 0 || index >= curve.points.length - 1) return curve;
  return { kind: 'controlPoints', points: curve.points.filter((_, i) => i !== index) };
}

// ── 内部 ──────────────────────────────────────────────

function readPoint(value: unknown): CurvePoint | string {
  if (!Array.isArray(value) || value.length !== 2) return '控制点须有两个分量。';
  const [x, y] = value as unknown[];
  if (typeof x !== 'number' || typeof y !== 'number' || !Number.isFinite(x) || !Number.isFinite(y)) return '控制点要是数字。';
  if (x < 0 || x > 1) return '曲线进度必须在 [0,1]。';
  return [x, y];
}

function controlPointsValid(points: readonly CurvePoint[]): boolean {
  if (points.length < 2) return false;
  const [first, last] = [points[0]!, points.at(-1)!];
  if (first[0] !== 0 || first[1] !== 0 || last[0] !== 1 || last[1] !== 1) return false;
  return points.every((p, i) => i === 0 || p[0] > points[i - 1]![0]);
}

function isLinear(curve: Curve): boolean {
  return [0.25, 0.5, 0.75].every((x) => Math.abs(evaluateCurve(curve, x) - x) < 1e-6);
}

function bezier(t: number, first: number, second: number): number {
  const rest = 1 - t;
  return 3 * rest * rest * t * first + 3 * rest * t * t * second + t * t * t;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function clamp(v: number, lo: number, hi: number): number {
  return Math.min(hi, Math.max(lo, v));
}

export function round4(v: number): number {
  const r = Math.round(v * 10000) / 10000;
  return Object.is(r, -0) ? 0 : r;
}

function num(v: number): string {
  return String(round4(v));
}
