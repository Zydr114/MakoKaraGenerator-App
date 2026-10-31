// 曲线参数（W8 设计 §2.9）：解析与校验和后端一致、规范写法、求值、换类型与图形编辑；图片参数的本地校验。

import { describe, expect, it } from 'vitest';
import {
  convertCurve,
  curveProblem,
  curveToJson,
  evaluateCurve,
  insertPoint,
  moveBezierPoint,
  movePoint,
  readCurve,
  removePoint,
  type Curve,
} from '../../src/editor/model/curves';
import { parameterProblem } from '../../src/editor/model/effect-params';
import type { EffectPackageFile, EffectPackageParameter } from '../../src/protocol';

const curve = (raw: string): Curve => {
  const read = readCurve(raw);
  if ('problem' in read) throw new Error(read.problem);
  return read.curve;
};

describe('解析与校验', () => {
  it('三种形式都接受，贝塞尔与折线的 y 可以过冲', () => {
    expect(curveProblem('{"kind":"easing","easing":"easeOut"}')).toBeUndefined();
    expect(curveProblem('{"kind":"cubicBezier","first":[0.3,-0.5],"second":[0.7,1.6]}')).toBeUndefined();
    expect(curveProblem('{"kind":"controlPoints","points":[[0,0],[0.4,1.3],[0.6,-0.2],[1,1]]}')).toBeUndefined();
  });

  it('不合法时给出与后端相同的原因', () => {
    expect(curveProblem('{"kind":"easing","easing":"bounce"}')).toBe('未知缓动。');
    expect(curveProblem('{"kind":"spline"}')).toBe('未知曲线类型。');
    expect(curveProblem('{"kind":"cubicBezier","first":[1.2,0],"second":[0.5,1]}')).toBe('曲线进度必须在 [0,1]。');
    expect(curveProblem('{"kind":"cubicBezier","first":[0.2],"second":[0.5,1]}')).toBe('控制点须有两个分量。');
    expect(curveProblem('{"kind":"controlPoints","points":[[0,0],[0.5,0.2],[0.5,0.4],[1,1]]}')).toBe('折线须有首尾 (0,0)、(1,1) 且 X 严格递增。');
    expect(curveProblem('{"kind":"controlPoints","points":[[0,0.1],[1,1]]}')).toBe('折线须有首尾 (0,0)、(1,1) 且 X 严格递增。');
    expect(curveProblem('{"kind":"easing","easing":"linear","extra":1}')).toMatch(/只有/);
  });
});

describe('写法与求值', () => {
  it('键按名称排序，数值最多 4 位小数（与后端规范形式一致）', () => {
    expect(curveToJson(curve('{"kind":"easing","easing":"linear"}'))).toBe('{"easing":"linear","kind":"easing"}');
    expect(curveToJson({ kind: 'cubicBezier', first: [0.123456, -0.00001], second: [0.7, 1.6] })).toBe('{"first":[0.1235,0],"kind":"cubicBezier","second":[0.7,1.6]}');
    expect(curveToJson({ kind: 'controlPoints', points: [[0, 0], [0.5, 0.25], [1, 1]] })).toBe('{"kind":"controlPoints","points":[[0,0],[0.5,0.25],[1,1]]}');
  });

  it('求值公式与后端 CurveValue 相同', () => {
    expect(evaluateCurve({ kind: 'easing', easing: 'easeOut' }, 0.5)).toBeCloseTo(0.875, 12);
    expect(evaluateCurve({ kind: 'easing', easing: 'easeInOut' }, 0.25)).toBeCloseTo(0.15625, 12);
    expect(evaluateCurve({ kind: 'controlPoints', points: [[0, 0], [0.5, 1], [1, 1]] }, 0.25)).toBeCloseTo(0.5, 12);
    // 线性的贝塞尔等于 p；先加速后减速的预设关于中点对称。
    expect(evaluateCurve({ kind: 'cubicBezier', first: [0, 0], second: [1, 1] }, 0.3)).toBeCloseTo(0.3, 9);
    const ease: Curve = { kind: 'cubicBezier', first: [0.42, 0], second: [0.58, 1] };
    expect(evaluateCurve(ease, 0.5)).toBeCloseTo(0.5, 9);
    expect(evaluateCurve(ease, 0.2) + evaluateCurve(ease, 0.8)).toBeCloseTo(1, 9);
  });
});

describe('换类型与图形编辑', () => {
  it('换类型按近似转换，结果都合法', () => {
    const easeOut: Curve = { kind: 'easing', easing: 'easeOut' };
    expect(convertCurve(easeOut, 'cubicBezier')).toEqual({ kind: 'cubicBezier', first: [0, 0], second: [0.58, 1] });
    const poly = convertCurve(easeOut, 'controlPoints');
    expect(poly.kind === 'controlPoints' && poly.points.map((p) => p[0])).toEqual([0, 0.25, 0.5, 0.75, 1]);
    expect(curveProblem(curveToJson(poly))).toBeUndefined();
    expect(convertCurve({ kind: 'cubicBezier', first: [0.2, 0.2], second: [0.8, 0.8] }, 'easing')).toEqual({ kind: 'easing', easing: 'linear' });
    expect(convertCurve(poly, 'easing')).toEqual({ kind: 'easing', easing: 'easeOut' });
  });

  it('贝塞尔控制点 x 夹到 [0,1]，y 不限', () => {
    const bezier = { kind: 'cubicBezier' as const, first: [0.3, 0] as const, second: [0.7, 1] as const };
    expect(moveBezierPoint(bezier, 'first', 1.4, -0.75)).toEqual({ ...bezier, first: [1, -0.75] });
  });

  it('折线：中间点夹在邻点之间，首尾不动；加点、删点', () => {
    const poly = { kind: 'controlPoints' as const, points: [[0, 0], [0.5, 0.5], [1, 1]] as const };
    expect(movePoint(poly, 1, 2, 1.5)).toEqual({ kind: 'controlPoints', points: [[0, 0], [0.999, 1.5], [1, 1]] });
    expect(movePoint(poly, 0, 0.3, 0.3)).toBe(poly);
    const inserted = insertPoint(poly, 0.25, 0.8)!;
    expect(inserted.index).toBe(1);
    expect(inserted.curve).toEqual({ kind: 'controlPoints', points: [[0, 0], [0.25, 0.8], [0.5, 0.5], [1, 1]] });
    expect(insertPoint(poly, 0.5)).toBeUndefined();
    expect(insertPoint(poly, 1.2)).toBeUndefined();
    expect(removePoint(inserted.curve as typeof poly, 1)).toEqual(poly);
    expect(removePoint(poly, 2)).toBe(poly);
  });
});

describe('参数校验', () => {
  const param = (type: 'curve' | 'asset'): EffectPackageParameter => ({ id: 'p', type, label: 'P', defaultValueJson: '""', options: [] });
  const files: EffectPackageFile[] = [
    { path: 'assets/spark.png', role: 'image', size: 123, sha256: 'x' },
    { path: 'README.md', role: 'documentation', size: 10, sha256: 'y' },
  ];

  it('曲线参数按曲线规则检查', () => {
    expect(parameterProblem(param('curve'), '{"kind":"easing","easing":"linear"}')).toBeUndefined();
    expect(parameterProblem(param('curve'), '{"kind":"easing","easing":"nope"}')).toBe('未知缓动。');
  });

  it('图片参数只能指向包里 role=image 的文件', () => {
    expect(parameterProblem(param('asset'), '"assets/spark.png"', files)).toBeUndefined();
    expect(parameterProblem(param('asset'), '"README.md"', files)).toBe('包里没有图片“README.md”。');
    expect(parameterProblem(param('asset'), '3', files)).toBe('要是包里图片的路径。');
  });
});
