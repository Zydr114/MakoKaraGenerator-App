// 画布几何（W4 设计 §13.6～§13.8、§14.8）：同帧测量的角点按 4 个一组成四边形，给出叠加层要画的框、点选顺序、
// 双击选中的句子和拖动换算。
// 坐标都是画布坐标（如 1920×1080），与输出 PNG 的尺寸无关。无 DOM。

import type { GeometryPoint, GeometryReference, SceneMeasurement, SceneMeasurementElement } from '../../protocol';
import { programOfNode, type OrchestrationModel } from './orchestration';

/** 四个角点：左上、右上、右下、左下（经世界矩阵变换后可以旋转、斜切）。 */
export type Quad = readonly [GeometryPoint, GeometryPoint, GeometryPoint, GeometryPoint];

/** 角点按 4 个一组；不足 4 个的尾巴丢掉。 */
export function quads(corners: readonly GeometryPoint[]): Quad[] {
  const result: Quad[] = [];
  for (let i = 0; i + 4 <= corners.length; i += 4) result.push([corners[i]!, corners[i + 1]!, corners[i + 2]!, corners[i + 3]!]);
  return result;
}

/** 四边形面积（鞋带公式，取绝对值）。 */
export function quadArea(q: Quad): number {
  let sum = 0;
  for (let i = 0; i < 4; i++) {
    const a = q[i]!;
    const b = q[(i + 1) % 4]!;
    sum += a.x * b.y - b.x * a.y;
  }
  return Math.abs(sum) / 2;
}

/** 点在四边形里（含边上）：四条边的叉积同号。矩形经仿射变换后仍是凸四边形；退化（面积为 0）时不算命中。 */
export function pointInQuad(p: GeometryPoint, q: Quad): boolean {
  if (quadArea(q) < 1e-6) return false;
  let sign = 0;
  for (let i = 0; i < 4; i++) {
    const a = q[i]!;
    const b = q[(i + 1) % 4]!;
    const cross = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
    if (Math.abs(cross) < 1e-9) continue;
    const s = Math.sign(cross);
    if (sign === 0) sign = s;
    else if (s !== sign) return false;
  }
  return true;
}

/**
 * 参考框差不多铺满画布（面积达到画布的 90%）：来源的根、铺满画布的容器，以及快速布局里整画布大小、只是挪了位置的槽位。
 * 点空白时不选它们（否则点哪里都会选中），拖动它们要按在文字上。
 */
export function coversCanvas(q: Quad, canvasWidth: number, canvasHeight: number): boolean {
  return quadArea(q) >= canvasWidth * canvasHeight * 0.9;
}

/** 画布显示框里的点（相对左上角的屏幕像素）换成画布坐标。 */
export function toCanvasPoint(x: number, y: number, box: { width: number; height: number }, canvas: { width: number; height: number }): GeometryPoint {
  return { x: (x / box.width) * canvas.width, y: (y / box.height) * canvas.height };
}

/**
 * 画布上的位移换成父级参考里的比例位移：用编辑参考世界矩阵的线性部分求逆，得到父级局部的位移，再除以参考的宽高。
 * 矩阵不可逆或参考宽高不大于 0 时返回 undefined。
 */
export function ratioDelta(reference: GeometryReference, dx: number, dy: number): { dx: number; dy: number } | undefined {
  const { a, b, c, d } = reference.worldMatrix;
  const det = a * d - b * c;
  if (!(Math.abs(det) > 1e-12) || !(reference.width > 0) || !(reference.height > 0)) return undefined;
  const localX = (d * dx - c * dy) / det;
  const localY = (-b * dx + a * dy) / det;
  return { dx: localX / reference.width, dy: localY / reference.height };
}

const INK_KINDS = new Set(['line', 'ruby', 'mark']);
const FRAME_KINDS = new Set(['container', 'slot']);

function hasInk(e: SceneMeasurementElement): boolean {
  return INK_KINDS.has(e.kind) && e.visible && e.worldCorners.length >= 4;
}

/** 节点的固定参考框（容器、组、槽位、标记都可能有）。 */
function frameOf(e: SceneMeasurementElement | undefined): Quad | undefined {
  return e?.fixedReference ? quads(e.fixedReference.worldCorners)[0] : undefined;
}

/**
 * 墨迹属于哪个节点：标记就是它自己；句子和注音沿测量的父级往上，找到第一个在结构里的节点（句子所在的容器或槽位）。
 */
function inkOwner(e: SceneMeasurementElement, byId: ReadonlyMap<string, SceneMeasurementElement>, model: OrchestrationModel): string | undefined {
  if (e.kind === 'mark') return model.byId.has(e.id) ? e.id : undefined;
  const seen = new Set<string>();
  for (let parentId = e.parentId; parentId && !seen.has(parentId); parentId = byId.get(parentId)?.parentId) {
    if (model.byId.has(parentId)) return parentId;
    seen.add(parentId);
  }
  return undefined;
}

/**
 * 在画布上点一下选中哪个节点（W4 设计 §13.7）：
 * 1. 命中句子、注音或标记的墨迹：句子和注音选它所在的容器或槽位，标记选它自己（后画的在上面，先查）；
 * 2. 否则选包含这个点、固定参考框面积最小的容器或槽位；差不多铺满画布的不算；
 * 3. 都没有时返回 undefined，不改变选择。
 * 只考虑结构里有的节点。
 */
export function hitNode(measurement: SceneMeasurement, model: OrchestrationModel, point: GeometryPoint): string | undefined {
  const byId = new Map(measurement.elements.map((e) => [e.id, e]));
  for (let i = measurement.elements.length - 1; i >= 0; i--) {
    const e = measurement.elements[i]!;
    if (!hasInk(e) || !quads(e.worldCorners).some((q) => pointInQuad(point, q))) continue;
    const owner = inkOwner(e, byId, model);
    if (owner) return owner;
  }
  let best: { id: string; area: number } | undefined;
  for (const e of measurement.elements) {
    if (!FRAME_KINDS.has(e.kind) || !model.byId.has(e.id)) continue;
    const frame = frameOf(e);
    if (!frame || coversCanvas(frame, measurement.canvasWidth, measurement.canvasHeight) || !pointInQuad(point, frame)) continue;
    const area = quadArea(frame);
    if (!best || area < best.area) best = { id: e.id, area };
  }
  return best?.id;
}

/**
 * 能不能从这一点开始拖动节点（§13.7）：点在它的固定参考框里；参考框差不多铺满画布（或没有参考框）时，要点在它的墨迹上
 * （容器的墨迹是全部后代文字）。节点要有编辑参考才能换算位移。可以时返回编辑参考。
 */
export function dragReference(measurement: SceneMeasurement, nodeId: string, point: GeometryPoint): GeometryReference | undefined {
  const e = measurement.elements.find((x) => x.id === nodeId);
  if (!e?.editReference) return undefined;
  const frame = frameOf(e);
  const onInk = () => quads(e.worldCorners).some((q) => pointInQuad(point, q));
  const full = !frame || coversCanvas(frame, measurement.canvasWidth, measurement.canvasHeight);
  return (full ? onInk() : pointInQuad(point, frame)) ? e.editReference : undefined;
}

/** 来源里的一句。 */
export interface LineRef {
  branchId: string;
  lineId: string;
}

/** 句子元素是哪个来源的哪一句：来源取测量给的，没有时按它所在的节点找；布局参考的代表文字不算。 */
function lineOf(e: SceneMeasurementElement, byId: ReadonlyMap<string, SceneMeasurementElement>, model: OrchestrationModel): LineRef | undefined {
  if (e.kind !== 'line' || !e.lineId || e.representative) return undefined;
  const owner = inkOwner(e, byId, model);
  const branchId = e.branchId ?? (owner ? programOfNode(model, owner)?.branchId : undefined);
  return branchId ? { branchId, lineId: e.lineId } : undefined;
}

/** 句子在时间预览测量里的元素。 */
function lineElement(measurement: SceneMeasurement, model: OrchestrationModel, line: LineRef): SceneMeasurementElement | undefined {
  const byId = new Map(measurement.elements.map((e) => [e.id, e]));
  return measurement.elements.find((e) => {
    if (e.kind !== 'line' || e.lineId !== line.lineId) return false;
    return lineOf(e, byId, model)?.branchId === line.branchId;
  });
}

/**
 * 双击选中句子（W4 设计 §14.8）：命中句子或它的注音的墨迹（后画的在上面，先查）；标记和布局参考的代表文字不算。
 */
export function hitLine(measurement: SceneMeasurement, model: OrchestrationModel, point: GeometryPoint): LineRef | undefined {
  const byId = new Map(measurement.elements.map((e) => [e.id, e]));
  for (let i = measurement.elements.length - 1; i >= 0; i--) {
    let e: SceneMeasurementElement | undefined = measurement.elements[i]!;
    if (!hasInk(e) || e.kind === 'mark' || !quads(e.worldCorners).some((q) => pointInQuad(point, q))) continue;
    // 注音沿父级找到它的句子。
    const seen = new Set<string>();
    while (e && e.kind !== 'line' && e.parentId && !seen.has(e.id)) {
      seen.add(e.id);
      e = byId.get(e.parentId);
    }
    const line = e && lineOf(e, byId, model);
    if (line) return line;
  }
  return undefined;
}

/** 能不能从这一点开始拖动句子（§14.8）：点在它可见的墨迹上，且它有编辑参考（所在容器的参考）。可以时返回编辑参考。 */
export function lineDragReference(measurement: SceneMeasurement, model: OrchestrationModel, line: LineRef, point: GeometryPoint): GeometryReference | undefined {
  const e = lineElement(measurement, model, line);
  if (!e?.editReference || !hasInk(e)) return undefined;
  return quads(e.worldCorners).some((q) => pointInQuad(point, q)) ? e.editReference : undefined;
}

export interface OverlayShapes {
  /** 容器、槽位的固定参考框（虚线）。 */
  frames: { id: string; quad: Quad }[];
  /** 句子、注音、标记的墨迹（实线）。 */
  inks: { id: string; quad: Quad }[];
  /** 选中节点的参考框（加粗）。 */
  selectedFrame?: Quad;
  /** 选中节点的墨迹：容器是它全部后代的文字，标记是它自己；有句子焦点时是这一句的墨迹。 */
  selectedInks: Quad[];
  /** selectedInks 是句子焦点那一句的墨迹（§14.8）。 */
  line?: boolean;
}

/**
 * 叠加层要画的框（§13.6）：只画结构里有的容器和槽位；墨迹只画可见的。
 * 有句子焦点、这一句在当前帧可见时（§14.8），选中的是它的墨迹，不画节点的选中框。
 */
export function overlayShapes(measurement: SceneMeasurement, model: OrchestrationModel, selectedId: string | undefined, focus?: LineRef): OverlayShapes {
  const frames: OverlayShapes['frames'] = [];
  const inks: OverlayShapes['inks'] = [];
  let selected: SceneMeasurementElement | undefined;
  for (const e of measurement.elements) {
    if (e.id === selectedId) selected = e;
    if (FRAME_KINDS.has(e.kind) && model.byId.has(e.id)) {
      const frame = frameOf(e);
      if (frame) frames.push({ id: e.id, quad: frame });
    }
    if (hasInk(e)) for (const quad of quads(e.worldCorners)) inks.push({ id: e.id, quad });
  }
  const line = focus ? lineElement(measurement, model, focus) : undefined;
  if (line && hasInk(line)) return { frames, inks, selectedInks: quads(line.worldCorners), line: true };
  const selectedFrame = frameOf(selected);
  return { frames, inks, ...(selectedFrame ? { selectedFrame } : {}), selectedInks: selected ? quads(selected.worldCorners) : [] };
}

/** SVG polygon 的 points 属性。 */
export function quadPoints(q: Quad): string {
  return q.map((p) => `${p.x},${p.y}`).join(' ');
}
