// 曲线参数的图形编辑（W8 设计 §2.9）：缓动、贝塞尔、折线三种。拖动中只改画面，松手才提交；
// 折线在空白处双击加点，选中后按 Delete 删除。规则与写法见 model/curves.ts。

import { useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';
import {
  CURVE_KIND_LABEL,
  EASINGS,
  EASING_LABEL,
  convertCurve,
  curveToJson,
  curveYRange,
  insertPoint,
  moveBezierPoint,
  movePoint,
  readCurve,
  removePoint,
  round4,
  sampleCurve,
  type Curve,
  type CurveKind,
  type CurvePoint,
  type Easing,
} from '../model/curves';
import { NumberField, Select } from '../ui/legacy/fields';

const W = 220;
const H = 180;
const PAD = 14;

type Handle = { kind: 'first' | 'second' } | { kind: 'point'; index: number };

export function CurveField(props: { label: string; raw: string; onCommit: (raw: string) => void; disabled: boolean }) {
  const read = readCurve(props.raw);
  if ('problem' in read) {
    return (
      <span className="param-readonly" data-curve-invalid>
        <span className="param-label">{props.label}</span>
        <code>{props.raw}</code>
        <span className="notice-error">{read.problem}</span>
      </span>
    );
  }
  return <CurveEditor {...props} curve={read.curve} />;
}

function CurveEditor(props: { label: string; curve: Curve; onCommit: (raw: string) => void; disabled: boolean }) {
  const { disabled } = props;
  const svg = useRef<SVGSVGElement>(null);
  // 拖动中的曲线与固定的纵轴范围（拖动时不跟着变，免得点跑掉）。
  const [drag, setDrag] = useState<{ handle: Handle; curve: Curve; range: [number, number] } | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  const curve = drag?.curve ?? props.curve;
  const [yMin, yMax] = drag?.range ?? curveYRange(curve);
  const commit = (next: Curve) => {
    const raw = curveToJson(next);
    if (raw !== curveToJson(props.curve)) props.onCommit(raw);
  };

  const sx = (x: number) => PAD + x * (W - 2 * PAD);
  const sy = (y: number) => PAD + ((yMax - y) / (yMax - yMin)) * (H - 2 * PAD);
  const toCurve = (event: { clientX: number; clientY: number }): [number, number] => {
    const rect = svg.current!.getBoundingClientRect();
    const px = ((event.clientX - rect.left) / rect.width) * W;
    const py = ((event.clientY - rect.top) / rect.height) * H;
    return [(px - PAD) / (W - 2 * PAD), yMax - ((py - PAD) / (H - 2 * PAD)) * (yMax - yMin)];
  };
  const moved = (base: Curve, handle: Handle, x: number, y: number): Curve => {
    if (handle.kind === 'point') return base.kind === 'controlPoints' ? movePoint(base, handle.index, x, y) : base;
    return base.kind === 'cubicBezier' ? moveBezierPoint(base, handle.kind, x, y) : base;
  };

  const startDrag = (handle: Handle) => (event: PointerEvent) => {
    if (disabled || event.button !== 0) return;
    event.preventDefault();
    event.stopPropagation();
    svg.current!.setPointerCapture(event.pointerId);
    svg.current!.focus();
    if (handle.kind === 'point') setSelected(handle.index);
    setDrag({ handle, curve: props.curve, range: curveYRange(props.curve) });
  };
  const onPointerMove = (event: PointerEvent) => {
    if (!drag) return;
    const [x, y] = toCurve(event);
    setDrag({ ...drag, curve: moved(drag.curve, drag.handle, x, y) });
  };
  const onPointerUp = () => {
    if (!drag) return;
    setDrag(null);
    commit(drag.curve);
  };
  const onDoubleClick = (event: { clientX: number; clientY: number }) => {
    if (disabled || props.curve.kind !== 'controlPoints') return;
    const [x, y] = toCurve(event);
    const inserted = insertPoint(props.curve, x, y);
    if (!inserted) return;
    setSelected(inserted.index);
    commit(inserted.curve);
  };
  const onKeyDown = (event: KeyboardEvent) => {
    if (disabled || props.curve.kind !== 'controlPoints' || selected === null) return;
    if (event.key !== 'Delete' && event.key !== 'Backspace') return;
    event.preventDefault();
    setSelected(null);
    commit(removePoint(props.curve, selected));
  };

  const line = sampleCurve(curve)
    .map(([x, y], i) => `${i === 0 ? 'M' : 'L'}${sx(x).toFixed(2)} ${sy(y).toFixed(2)}`)
    .join(' ');
  const handle = (key: string, point: CurvePoint, h: Handle, active: boolean) => (
    <circle
      key={key}
      className={`curve-handle${active ? ' selected' : ''}`}
      data-handle={key}
      cx={sx(point[0])}
      cy={sy(point[1])}
      r={6}
      onPointerDown={startDrag(h)}
    />
  );

  return (
    <div className="curve-field" data-curve-kind={curve.kind}>
      <div className="curve-head">
        <span className="param-label">{props.label}</span>
        <Select<CurveKind>
          label="类型"
          value={props.curve.kind}
          options={(['easing', 'cubicBezier', 'controlPoints'] as const).map((k) => ({ value: k, label: CURVE_KIND_LABEL[k] }))}
          onChange={(kind) => {
            setSelected(null);
            commit(convertCurve(props.curve, kind));
          }}
          disabled={disabled}
        />
        {props.curve.kind === 'easing' && (
          <Select<Easing>
            label="缓动"
            value={props.curve.easing}
            options={EASINGS.map((e) => ({ value: e, label: EASING_LABEL[e] }))}
            onChange={(easing) => commit({ kind: 'easing', easing })}
            disabled={disabled}
          />
        )}
      </div>
      <div className="curve-body">
        <svg
          ref={svg}
          className="curve-graph"
          viewBox={`0 0 ${W} ${H}`}
          tabIndex={disabled ? -1 : 0}
          aria-label={`${props.label}曲线图`}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={() => setDrag(null)}
          onDoubleClick={onDoubleClick}
          onKeyDown={onKeyDown}
          onPointerDown={() => setSelected(null)}
        >
          <rect className="curve-frame" x={sx(0)} y={sy(1)} width={sx(1) - sx(0)} height={sy(0) - sy(1)} />
          <line className="curve-diagonal" x1={sx(0)} y1={sy(0)} x2={sx(1)} y2={sy(1)} />
          <path className="curve-line" d={line} />
          {curve.kind === 'cubicBezier' && (
            <>
              <line className="curve-arm" x1={sx(0)} y1={sy(0)} x2={sx(curve.first[0])} y2={sy(curve.first[1])} />
              <line className="curve-arm" x1={sx(1)} y1={sy(1)} x2={sx(curve.second[0])} y2={sy(curve.second[1])} />
              {handle('first', curve.first, { kind: 'first' }, drag?.handle.kind === 'first')}
              {handle('second', curve.second, { kind: 'second' }, drag?.handle.kind === 'second')}
            </>
          )}
          {curve.kind === 'controlPoints' &&
            curve.points.map((point, index) =>
              index === 0 || index === curve.points.length - 1 ? (
                <circle key={index} className="curve-anchor" cx={sx(point[0])} cy={sy(point[1])} r={3.5} />
              ) : (
                handle(`point-${index}`, point, { kind: 'point', index }, selected === index)
              ),
            )}
        </svg>
        <div className="curve-numbers">
          {props.curve.kind === 'cubicBezier' && <BezierNumbers curve={props.curve} onCommit={commit} disabled={disabled} />}
          {props.curve.kind === 'controlPoints' && (
            <>
              {selected !== null && props.curve.points[selected] && (
                <PointNumbers curve={props.curve} index={selected} onCommit={commit} disabled={disabled} />
              )}
              <p className="hint">双击空白处加点；选中一个点后按 Delete 删除。首尾两点固定。</p>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

function BezierNumbers(props: { curve: Extract<Curve, { kind: 'cubicBezier' }>; onCommit: (curve: Curve) => void; disabled: boolean }) {
  const { curve } = props;
  const field = (label: string, which: 'first' | 'second', axis: 0 | 1) => (
    <NumberField
      label={label}
      value={curve[which][axis]}
      {...(axis === 0 ? { min: 0, max: 1 } : {})}
      onCommit={(v) => {
        const point = curve[which];
        props.onCommit(moveBezierPoint(curve, which, axis === 0 ? v : point[0], axis === 1 ? v : point[1]));
      }}
      disabled={props.disabled}
    />
  );
  return (
    <div className="curve-number-grid">
      {field('x1', 'first', 0)}
      {field('y1', 'first', 1)}
      {field('x2', 'second', 0)}
      {field('y2', 'second', 1)}
    </div>
  );
}

function PointNumbers(props: { curve: Extract<Curve, { kind: 'controlPoints' }>; index: number; onCommit: (curve: Curve) => void; disabled: boolean }) {
  const { curve, index } = props;
  const point = curve.points[index]!;
  const left = curve.points[index - 1]![0];
  const right = curve.points[index + 1]![0];
  return (
    <div className="curve-number-grid">
      <NumberField
        label={`点 ${index} · x`}
        value={point[0]}
        greaterThan={left}
        check={(v) => (v >= right ? `必须小于 ${right}。` : undefined)}
        onCommit={(v) => props.onCommit(movePoint(curve, index, v, point[1]))}
        disabled={props.disabled}
      />
      <NumberField label={`点 ${index} · y`} value={point[1]} onCommit={(v) => props.onCommit(movePoint(curve, index, point[0], round4(v)))} disabled={props.disabled} />
    </div>
  );
}
