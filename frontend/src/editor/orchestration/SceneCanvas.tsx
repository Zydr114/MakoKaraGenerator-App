import { useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent, type PointerEvent as ReactPointerEvent } from 'react';
import { ObservableValue } from '../../client';
import type { GeometryPoint, GeometryReference, LineLocalTransform, SceneMeasurement } from '../../protocol';
import type { AudioController, AudioState } from '../audio/audio-controller';
import { client, renderDevice } from '../connection';
import { nodeName, type OrchestrationModel } from '../model/orchestration';
import { lineDragTransform } from '../model/orchestration-lead-ins';
import { geometryCommand, roundTo } from '../model/orchestration-properties';
import { dragReference, hitLine, hitNode, lineDragReference, overlayShapes, quadPoints, ratioDelta, toCanvasPoint, type LineRef } from '../model/scene-geometry';
import { PreviewPanel, previewTimeUs, type ShownFrame } from '../preview/PreviewPanel';
import { fitPreviewSize } from '../preview/preview-controller';
import type { EditorSession } from '../session/editor-session';
import { Switch } from '../ui/fields';
import { useObservable } from '../ui/hooks';
import type { LineActions } from './LineInspector';
import type { OrchestrationState } from './orchestration-store';
import type { PropertyActions } from './PropertySections';
import { SCENE_MEASUREMENT, SceneMeasurementController, type MeasurementWant, type SceneMeasurementViewState } from './scene-measurement';

type CanvasMode = 'time' | 'layout';

/** 时间预览模式只要几何，PNG 用小图。 */
const TIME_MEASURE_SIZE = { width: 320, height: 180 };
/** 暂停后播放头或修订停住这么久才测量（W4 设计 §13.6）。 */
const SETTLE_MS = 300;
/** 位移小于这么多屏幕像素时当作点选（§13.7）。 */
const DRAG_THRESHOLD_PX = 2;
const NO_MEASUREMENT = '后端没有开放画面测量，不能显示框、点选和拖动。';
const MODE_HINT: Record<CanvasMode, string> = {
  time: '点文字或框选中节点，双击文字选中这一句；拖动可以移动位置。',
  layout: '每个容器和槽位放一句代表文字，不看时刻。',
};

/** 句子焦点的那一句（§14.8）：ready 表示单句展示已读到当前修订，可以拖动。 */
export interface FocusedLine extends LineRef {
  transform: LineLocalTransform | null;
  ready: boolean;
  /** 提示里的“第 N 句”。 */
  label: string;
}

/**
 * 编排页的画布（W4 设计 §13.6～§13.8、§14.8）：时间预览显示成片帧，布局参考显示 layout 测量的图；上面叠一层按同帧测量画的框，
 * 可以点选节点、拖动移动位置（松手时一条 setNodeGeometry，只改 X、Y）；双击句子选中这一句，拖动它改本句偏移。
 */
export function SceneCanvas(props: {
  session: EditorSession;
  audio: AudioController | null;
  view: OrchestrationState;
  properties: PropertyActions;
  /** 有句子焦点、只选了一句时。 */
  line: FocusedLine | undefined;
  lineActions: LineActions;
  onSelect: (id: string) => void;
  onFocusLine: (line: LineRef) => void;
}) {
  const { session, audio, view } = props;
  const state = useObservable(session.state$);
  const available = session.supports(SCENE_MEASUREMENT);
  const [mode, setMode] = useState<CanvasMode>('time');
  const [showFrames, setShowFrames] = useState(true);
  const canvasWidth = state.settings?.canvasWidth;
  const canvasHeight = state.settings?.canvasHeight;
  const layoutSize = useMemo(() => fitPreviewSize(canvasWidth, canvasHeight), [canvasWidth, canvasHeight]);
  const timeSize = useMemo(() => fitPreviewSize(canvasWidth, canvasHeight, TIME_MEASURE_SIZE), [canvasWidth, canvasHeight]);

  const [controller, setController] = useState<SceneMeasurementController | null>(null);
  useEffect(() => {
    if (!available) return;
    const next = new SceneMeasurementController(client, session.projectSessionId, { device: renderDevice });
    setController(next);
    return () => next.dispose();
  }, [available, session]);

  // 每 100 毫秒看一次想测什么：布局参考按修订测；时间预览在暂停时按播放头测，播放头或修订停住 300 毫秒后才测。
  // 同一次只请求一回（失败了也不重复请求），变了才再请求。
  const latest = useRef({ mode, layoutSize, timeSize });
  latest.current = { mode, layoutSize, timeSize };
  useEffect(() => {
    if (!controller) return;
    let requested = '';
    let pending: { key: string; at: number } | undefined;
    const timer = setInterval(() => {
      const { mode: current, layoutSize: big, timeSize: small } = latest.current;
      let want: MeasurementWant | undefined;
      if (current === 'layout') want = { mode: 'layout', revision: session.latestRevision, timeUs: null, ...big };
      else if (!audio || (audio.state.status === 'ready' && !audio.playing))
        want = { mode: 'time', revision: session.latestRevision, timeUs: Math.max(0, Math.round(previewTimeUs(audio))), ...small };
      if (!want) {
        pending = undefined;
        return;
      }
      const key = JSON.stringify(want);
      if (key === requested) return;
      const now = performance.now();
      if (pending?.key !== key) pending = { key, at: now };
      if (current === 'time' && now - pending.at < SETTLE_MS) return;
      requested = key;
      controller.measure(want);
    }, 100);
    return () => clearInterval(timer);
  }, [controller, audio, session]);

  const measured = useObservable(controller?.state$ ?? IDLE_MEASUREMENT);
  const audioState = useObservable(audio?.state$ ?? IDLE_AUDIO);
  const playing = !!audio && audioState.playing;
  const result = measured.status !== 'error' && measured.result?.want.mode === mode ? measured.result : undefined;

  /** 叠加层用的测量：时间预览要与正在显示的帧同修订、同时刻，播放中不用。 */
  const measurementFor = (shown: ShownFrame | undefined): SceneMeasurement | undefined => {
    if (!result) return undefined;
    if (mode === 'layout') return result.measurement;
    if (playing || !shown || result.want.revision !== shown.revision || result.want.timeUs !== shown.timeUs) return undefined;
    return result.measurement;
  };

  const overlay = (shown: ShownFrame | undefined) => {
    const measurement = measurementFor(shown);
    return measurement && view.model ? (
      <CanvasOverlay
        measurement={measurement}
        model={view.model}
        selectedId={view.selectedId}
        line={mode === 'time' ? props.line : undefined}
        showFrames={showFrames}
        properties={props.properties}
        lineActions={props.lineActions}
        onSelect={props.onSelect}
        onFocusLine={props.onFocusLine}
      />
    ) : null;
  };

  const status = canvasStatus(available, mode, playing, measured);
  return (
    <>
      <div className="canvas-modes" role="radiogroup" aria-label="画布模式">
        {(['time', 'layout'] as const).map((m) => (
          <button
            key={m}
            type="button"
            role="radio"
            aria-checked={mode === m}
            className={`canvas-mode${mode === m ? ' selected' : ''}`}
            disabled={m === 'layout' && !available}
            title={m === 'layout' && !available ? NO_MEASUREMENT : undefined}
            onClick={() => setMode(m)}
          >
            {m === 'time' ? '时间预览' : '布局参考'}
          </button>
        ))}
        <span className="hint">{!available ? NO_MEASUREMENT : mode === 'time' && props.properties.block ? `点画布上的文字或框选中节点。${props.properties.block}` : MODE_HINT[mode]}</span>
        <span className="canvas-frames">
          <Switch label="显示框" checked={showFrames && available} disabled={!available} onChange={setShowFrames} />
        </span>
      </div>
      <PreviewPanel
        session={session}
        audio={audio}
        overlay={available ? overlay : undefined}
        replacement={
          mode === 'layout'
            ? {
                title: result ? `布局参考 · 修订 ${result.want.revision}` : '布局参考',
                imageUrl: result?.imageUrl,
                placeholder: measured.status === 'error' ? (measured.error ?? '测量失败。') : '正在测量布局……',
                hint: `画布 ${canvasWidth && canvasHeight ? `${canvasWidth}×${canvasHeight}` : '未知'} · 时间轴和播放不影响画布。`,
              }
            : undefined
        }
      />
      {status && <p className={`canvas-status ${status.error ? 'notice-error' : 'hint'}`}>{status.text}</p>}
    </>
  );
}

/** 画布下方的测量状态：失败写明原因（叠加层隐藏，不影响预览）；播放中写明不显示框。 */
function canvasStatus(available: boolean, mode: CanvasMode, playing: boolean, measured: SceneMeasurementViewState): { text: string; error?: boolean } | undefined {
  if (!available) return undefined;
  if (measured.status === 'error') return { text: measured.error ?? '测量失败。', error: true };
  if (mode === 'time' && playing) return { text: '播放中不显示框，暂停后再点选或拖动。' };
  if (measured.status === 'measuring') return { text: '正在测量画面……' };
  return undefined;
}

interface Gesture {
  pointerId: number;
  clientX: number;
  clientY: number;
  start: GeometryPoint;
  /** 按下处可以拖动的节点（或句子焦点那一句）和它的编辑参考。 */
  drag?: { kind: 'node'; id: string; reference: GeometryReference } | { kind: 'line'; id: string; line: FocusedLine; reference: GeometryReference };
  moved: boolean;
}

const lineKey = (line: LineRef) => `line:${line.branchId}:${line.lineId}`;

/** 拖动中（或刚松手、还没等到新测量）时选中框的位移，画布坐标。id 是节点 ID 或 lineKey。 */
interface Offset {
  id: string;
  dx: number;
  dy: number;
  settled?: boolean;
}

/**
 * 叠加层（§13.6、§13.7、§14.8）：SVG 的坐标就是画布坐标；点选按 hitNode，双击按 hitLine，
 * 拖动按 dragReference（句子焦点那一句按 lineDragReference）与 ratioDelta 换算。
 */
function CanvasOverlay(props: {
  measurement: SceneMeasurement;
  model: OrchestrationModel;
  selectedId: string | undefined;
  line: FocusedLine | undefined;
  showFrames: boolean;
  properties: PropertyActions;
  lineActions: LineActions;
  onSelect: (id: string) => void;
  onFocusLine: (line: LineRef) => void;
}) {
  const { measurement, model, selectedId, properties, line } = props;
  const focusBranch = line?.branchId;
  const focusLine = line?.lineId;
  const focus = useMemo(() => (focusBranch && focusLine ? { branchId: focusBranch, lineId: focusLine } : undefined), [focusBranch, focusLine]);
  const shapes = useMemo(() => overlayShapes(measurement, model, selectedId, focus), [measurement, model, selectedId, focus]);
  const canDrag = !properties.block && !properties.busy;
  const canDragLine = !!line?.ready && !props.lineActions.block && !props.lineActions.busy;
  const gesture = useRef<Gesture | undefined>(undefined);
  const [offset, setOffset] = useState<Offset | undefined>();
  const [movable, setMovable] = useState(false);

  // 新的测量到了：松手后保留的位移不再需要。
  useEffect(() => setOffset((o) => (o?.settled ? undefined : o)), [measurement]);

  // 拖动中按 Esc 取消。
  const dragging = !!offset && !offset.settled;
  useEffect(() => {
    if (!dragging) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      event.stopPropagation();
      gesture.current = undefined;
      setOffset(undefined);
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [dragging]);

  const pointOf = (event: ReactMouseEvent<SVGSVGElement>): GeometryPoint => {
    const box = event.currentTarget.getBoundingClientRect();
    return toCanvasPoint(event.clientX - box.left, event.clientY - box.top, box, { width: measurement.canvasWidth, height: measurement.canvasHeight });
  };

  /** 这一点能拖动谁：先看句子焦点那一句（按在它的墨迹上），再看选中的节点，最后看点中的节点。 */
  const dragTarget = (point: GeometryPoint): Gesture['drag'] => {
    if (line && canDragLine) {
      const reference = lineDragReference(measurement, model, line, point);
      if (reference) return { kind: 'line', id: lineKey(line), line, reference };
    }
    if (!canDrag) return undefined;
    for (const id of [selectedId, hitNode(measurement, model, point)]) {
      const reference = id ? dragReference(measurement, id, point) : undefined;
      if (id && reference) return { kind: 'node', id, reference };
    }
    return undefined;
  };

  /** 拖动句子松手：按编辑参考换成本句偏移，保留旋转和缩放。 */
  const commitLine = async (target: FocusedLine, reference: GeometryReference, dx: number, dy: number) => {
    const transform = lineDragTransform(target.transform, reference, dx, dy);
    if (!transform) {
      setOffset(undefined);
      return;
    }
    setOffset({ id: lineKey(target), dx, dy, settled: true });
    const command = { type: 'orchestration.setLineLocalTransform' as const, branchId: target.branchId, lineId: target.lineId, transform };
    const result = await props.lineActions.run(command, `已移动${target.label}`);
    if (result !== 'applied') setOffset(undefined);
  };

  const commit = async (id: string, reference: GeometryReference, dx: number, dy: number) => {
    const node = model.byId.get(id);
    const delta = ratioDelta(reference, dx, dy);
    if (!node || !delta) {
      setOffset(undefined);
      return;
    }
    setOffset({ id, dx, dy, settled: true });
    const placement = { x: roundTo(node.placement.x + delta.dx, 4), y: roundTo(node.placement.y + delta.dy, 4) };
    const result = await properties.run(geometryCommand(node, { placement }), `已移动“${nodeName(model, node)}”`);
    if (result !== 'applied') setOffset(undefined);
  };

  const onPointerDown = (event: ReactPointerEvent<SVGSVGElement>) => {
    if (event.button !== 0) return;
    const start = pointOf(event);
    const drag = dragTarget(start);
    gesture.current = { pointerId: event.pointerId, clientX: event.clientX, clientY: event.clientY, start, ...(drag ? { drag } : {}), moved: false };
    event.currentTarget.setPointerCapture(event.pointerId);
  };

  const onPointerMove = (event: ReactPointerEvent<SVGSVGElement>) => {
    const g = gesture.current;
    if (!g || g.pointerId !== event.pointerId) {
      if (!g) setMovable(!!dragTarget(pointOf(event)));
      return;
    }
    if (!g.drag) return;
    if (!g.moved && Math.hypot(event.clientX - g.clientX, event.clientY - g.clientY) < DRAG_THRESHOLD_PX) return;
    if (!g.moved) {
      g.moved = true;
      if (g.drag.kind === 'node' && g.drag.id !== selectedId) props.onSelect(g.drag.id);
    }
    const p = pointOf(event);
    setOffset({ id: g.drag.id, dx: p.x - g.start.x, dy: p.y - g.start.y });
  };

  const onPointerUp = (event: ReactPointerEvent<SVGSVGElement>) => {
    const g = gesture.current;
    if (!g || g.pointerId !== event.pointerId) return;
    gesture.current = undefined;
    const p = pointOf(event);
    if (g.moved && g.drag) {
      if (g.drag.kind === 'line') void commitLine(g.drag.line, g.drag.reference, p.x - g.start.x, p.y - g.start.y);
      else void commit(g.drag.id, g.drag.reference, p.x - g.start.x, p.y - g.start.y);
      return;
    }
    // 点在句子焦点那一句上不改选择（双击时也不先丢掉焦点）。
    if (g.drag?.kind === 'line') return;
    const hit = hitNode(measurement, model, p);
    if (hit) props.onSelect(hit);
  };

  const onDoubleClick = (event: ReactMouseEvent<SVGSVGElement>) => {
    const hit = hitLine(measurement, model, pointOf(event));
    if (hit) props.onFocusLine(hit);
  };

  const onPointerCancel = () => {
    gesture.current = undefined;
    setOffset((o) => (o?.settled ? o : undefined));
  };

  const shownId = shapes.line && line ? lineKey(line) : selectedId;
  const shift = offset && offset.id === shownId ? `translate(${offset.dx} ${offset.dy})` : undefined;
  const cursor = dragging ? 'grabbing' : movable && canDrag ? 'move' : 'default';
  return (
    <svg
      className="canvas-overlay"
      viewBox={`0 0 ${measurement.canvasWidth} ${measurement.canvasHeight}`}
      preserveAspectRatio="none"
      style={{ cursor }}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerCancel}
      onPointerLeave={() => setMovable(false)}
      onDoubleClick={onDoubleClick}
    >
      {props.showFrames && (
        <>
          {shapes.frames.map((f) => (
            <polygon key={`f-${f.id}`} className="overlay-frame" points={quadPoints(f.quad)} data-node-id={f.id} />
          ))}
          {shapes.inks.map((ink, i) => (
            <polygon key={`i-${ink.id}-${i}`} className="overlay-ink" points={quadPoints(ink.quad)} />
          ))}
        </>
      )}
      {(props.showFrames || shift) && (
        <g transform={shift}>
          {shapes.selectedInks.map((q, i) => (
            <polygon key={`s-${i}`} className={shapes.line ? 'overlay-selected-ink overlay-line' : 'overlay-selected-ink'} points={quadPoints(q)} />
          ))}
          {shapes.selectedFrame && <polygon className="overlay-selected" points={quadPoints(shapes.selectedFrame)} />}
        </g>
      )}
    </svg>
  );
}

const IDLE_MEASUREMENT = new ObservableValue<SceneMeasurementViewState>({ status: 'idle' });
const IDLE_AUDIO = new ObservableValue<AudioState>({ status: 'loading', durationUs: 0, playing: false, rate: 1 });
