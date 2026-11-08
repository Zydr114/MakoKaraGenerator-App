// 试样画布的调试叠加层、图例与单帧成本面板（SDK v2 检查投影设计 §5）。
// 叠加层是盖在图片上的 SVG（不接收指针），只在画面与投影属于同一帧时画；失败仍显示旧帧时不画。

import { useEffect, useMemo, useState } from 'react';
import { ToggleButton } from 'react-aria-components';
import type { PreviewFrameInspectionResponse } from '../../protocol';
import type { TemplateWorkshop } from './template-workshop';
import {
  costModel,
  formatMs,
  INSPECTION_LAYERS,
  LAYER_LABEL,
  overlayModel,
  readInspectionPrefs,
  stageLabel,
  wantsInspection,
  writeInspectionPrefs,
  type InspectionLayer,
  type InspectionPrefs,
  type OverlayShape,
} from './sample-inspection';

const storage = typeof localStorage === 'undefined' ? undefined : localStorage;

/** 叠加层与成本开关（记在本机）；任一打开时让试样取帧带检查。 */
export function useInspectionPrefs(workshop: TemplateWorkshop): [InspectionPrefs, (patch: Partial<InspectionPrefs>) => void] {
  const [prefs, setPrefs] = useState(() => readInspectionPrefs(storage));
  useEffect(() => workshop.setInspect(wantsInspection(prefs)), [workshop, prefs.overlay, prefs.cost]);
  const update = (patch: Partial<InspectionPrefs>) =>
    setPrefs((current) => {
      const next = { ...current, ...patch };
      writeInspectionPrefs(storage, next);
      return next;
    });
  return [prefs, update];
}

/** 盖在试样图片上的叠加层；坐标是工程画布坐标，按 viewBox 缩放（与图片同样按比例放进框里）。 */
export function InspectionOverlay(props: { inspection: PreviewFrameInspectionResponse; hidden: readonly InspectionLayer[] }) {
  const model = useMemo(() => overlayModel(props.inspection.inspection, props.hidden), [props.inspection, props.hidden]);
  return (
    <svg
      className="inspection-overlay"
      viewBox={`0 0 ${model.width} ${model.height}`}
      preserveAspectRatio="xMidYMid meet"
      aria-hidden
      data-frame={props.inspection.frameId}
      data-shapes={model.shapes.length}
      style={{ fontSize: model.fontSize }}
    >
      {model.shapes.map((shape, index) => (
        <Shape key={index} shape={shape} radius={model.fontSize * 0.28} />
      ))}
    </svg>
  );
}

function Shape(props: { shape: OverlayShape; radius: number }) {
  const { shape } = props;
  const className = `inspect-${shape.tone}`;
  switch (shape.kind) {
    case 'polygon':
      return <polygon points={shape.points} className={className} data-layer={shape.layer}>{shape.title && <title>{shape.title}</title>}</polygon>;
    case 'line':
      return <line x1={shape.x1} y1={shape.y1} x2={shape.x2} y2={shape.y2} className={className} data-layer={shape.layer} />;
    case 'dot':
      return <circle cx={shape.x} cy={shape.y} r={props.radius} className={className} data-layer={shape.layer}>{shape.title && <title>{shape.title}</title>}</circle>;
    case 'label':
      return <text x={shape.x} y={shape.y} className={className} data-layer={shape.layer}>{shape.text}</text>;
  }
}

/**
 * 叠加层图例：每类一个开关（按下＝显示），写出这一帧的数量；再写明投影的状况
 * （读取失败、几何未经完整准备、超出上限、画面不是这一帧）。
 */
export function InspectionLegend(props: {
  inspection: PreviewFrameInspectionResponse | undefined;
  error: string | undefined;
  stale: boolean;
  waiting: boolean;
  hidden: readonly InspectionLayer[];
  onHiddenChange(hidden: InspectionLayer[]): void;
}) {
  const { inspection, hidden } = props;
  const counts = useMemo(() => (inspection ? overlayModel(inspection.inspection, INSPECTION_LAYERS).counts : undefined), [inspection]);
  const notes = [
    ...(props.stale ? ['画面是上一张成功的帧，不画叠加层'] : []),
    ...(props.error ? [props.error] : []),
    ...(!props.stale && !props.error && !inspection && !props.waiting ? ['等下一帧带上检查投影'] : []),
    ...(inspection && !inspection.inspection.prepared ? ['没有脚本效果，这一帧不经过效果准备，没有几何可画'] : []),
  ];
  return (
    <div className="inspection-legend" role="group" aria-label="叠加层图例">
      {INSPECTION_LAYERS.map((layer) => (
        <ToggleButton
          key={layer}
          className="legend-chip"
          data-layer={layer}
          isSelected={!hidden.includes(layer)}
          onChange={(shown) => props.onHiddenChange(shown ? hidden.filter((l) => l !== layer) : [...hidden, layer])}
        >
          <span className={`legend-swatch swatch-${layer}`} aria-hidden />
          {LAYER_LABEL[layer]}
          {counts && <span className="legend-count">{counts[layer]}</span>}
        </ToggleButton>
      ))}
      {notes.map((note) => (
        <span key={note} className="legend-note">
          {note}
        </span>
      ))}
    </div>
  );
}

/** 单帧成本：左边 Worker 内各阶段耗时条，右边计数；下面是脚本表与 Host 侧耗时，最后写明口径。 */
export function CostPanel(props: { inspection: PreviewFrameInspectionResponse | undefined; error: string | undefined; stale: boolean }) {
  const { inspection } = props;
  const model = useMemo(() => (inspection ? costModel(inspection) : undefined), [inspection]);
  if (!inspection || !model) {
    return (
      <section className="sample-cost empty" aria-label="单帧成本">
        <p className="cost-empty">{props.stale ? '这次没有新画面，没有成本可看。' : (props.error ?? '等下一帧带上成本……')}</p>
      </section>
    );
  }
  const time = (Number(inspection.timeUs) / 1_000_000).toFixed(3);
  return (
    <section className="sample-cost" aria-label="单帧成本" data-frame={inspection.frameId}>
      <header className="cost-head">
        <strong>单帧成本</strong>
        <span>
          {time} s · {inspection.renderBackend.toUpperCase()} · Worker 共 {formatMs(model.wallMs)}
        </span>
      </header>
      <div className="cost-grid">
        <div className="cost-phases" role="table" aria-label="Worker 各阶段耗时">
          {model.phases.map((phase) => (
            <div key={phase.key} className="cost-phase" role="row" data-phase={phase.key}>
              <span role="cell" className="cost-label">
                {phase.label}
              </span>
              <span role="cell" className="cost-bar">
                <span style={{ width: `${Math.min(100, (phase.ms / model.scaleMs) * 100)}%` }} />
              </span>
              <span role="cell" className="cost-value">
                {formatMs(phase.ms)}
              </span>
            </div>
          ))}
        </div>
        <dl className="cost-counts">
          {model.counts.map((row) => (
            <div key={row.label}>
              <dt>{row.label}</dt>
              <dd>{row.value}</dd>
            </div>
          ))}
        </dl>
      </div>
      {model.scripts.length > 0 && (
        <table className="cost-scripts">
          <thead>
            <tr>
              <th>脚本（效果实例）</th>
              <th>阶段</th>
              <th>调用</th>
              <th>耗时</th>
              <th>单次最长</th>
            </tr>
          </thead>
          <tbody>
            {model.scripts.map((row) => (
              <tr key={`${row.scriptId}-${row.stage}`} data-script={row.scriptId}>
                <td title={row.scriptId}>{row.owner}</td>
                <td>{stageLabel(row.stage)}</td>
                <td>{row.calls}</td>
                <td>{formatMs(row.wallMs)}</td>
                <td>{formatMs(row.maxMs)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {model.scriptOverflow > 0 && <p className="cost-note">另有 {model.scriptOverflow} 次脚本调用超出 16 项汇总，只计入总数。</p>}
      <p className="cost-host">
        <strong>Host</strong>
        {model.host.map((row) => (
          <span key={row.label}>
            {row.label} {row.value}
          </span>
        ))}
      </p>
      {model.truncated && <p className="cost-note">{model.truncated}</p>}
      <p className="cost-note">
        各阶段是墙钟包含时间，不能相加当作总耗时，也不是 GPU 硬件时间；分配量是全进程增量；SDK v1 的 Evaluate 不单独计时。同一帧重复取也有抖动，只看量级。
      </p>
    </section>
  );
}
