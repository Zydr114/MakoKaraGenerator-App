// 句子的属性（G24，W4 设计 §14.7）：这一句的本句微调、进入动画与前奏；选了几句时只显示范围；工程默认进入动画。

import type { LineEnterAnimation, LineLocalTransform, OrchestrationLeadIn, OrchestrationProgram } from '../../protocol';
import { isTimed, type EditorLine } from '../model/lyrics';
import { lineRangeText, type LineRange, type OrchestrationModel } from '../model/orchestration';
import {
  ANIMATION_SOURCE_TEXT,
  DEFAULT_ENTER_ANIMATION,
  ENTER_EASING_OPTIONS,
  enterAnimationOf,
  enterAnimationText,
  enterDurationUs,
  isNeutralAnimation,
  leadInOfLine,
  lineContainerText,
  lineEnterCommand,
  lineNumberText,
  lineTransformCommand,
  NO_TRANSFORM,
  projectEnterCommand,
  secondsText,
} from '../model/orchestration-lead-ins';
import { roundTo, toPercent, toSeconds, withCurrent } from '../model/orchestration-properties';
import { formatTime } from '../model/time';
import { NumberField, Select, Switch } from '../ui/fields';
import type { LineCommand, LinePresentationState, OrchestrationResult } from './orchestration-store';
import { Row, useSelectReset } from './parts';

/** 句子命令（§14.7）：block 是整体不可用的原因（只读、后端没开放）；run 发一条命令，成功时提示 done 并可撤销。 */
export interface LineActions {
  block: string | undefined;
  busy: boolean;
  run: (command: LineCommand, done: string) => Promise<OrchestrationResult>;
}

const ratio = (percent: number) => percent / 100;

/** 这一句（§14.7）：头部、本句微调、进入动画、工程默认进入动画、前奏。 */
export function LineInspector(props: {
  model: OrchestrationModel;
  program: OrchestrationProgram | undefined;
  branchId: string;
  branchName: string;
  line: EditorLine;
  lines: readonly EditorLine[];
  presentation: LinePresentationState | undefined;
  actions: LineActions;
  /** 来源不支持前奏时是 undefined（不显示前奏一项）。 */
  leadIns: { block: string | undefined; onAdd: (lineId: string) => void; onSelect: (leadIn: OrchestrationLeadIn) => void } | undefined;
  onSeek: (us: number) => void;
}) {
  const { model, program, line, lines, actions, presentation } = props;
  const number = lineNumberText(lines, line.id);
  const response = presentation?.lineId === line.id && presentation.branchId === props.branchId ? presentation.response : undefined;
  const leadIn = props.leadIns && program ? leadInOfLine(program, line.id) : undefined;
  return (
    <div className="inspector line-inspector">
      <div className="inspector-path">
        <span className="pane-title-text">{[props.branchName, number].join(' / ')}</span>
        <span className="muted">句子</span>
        <code className="node-id">{line.id}</code>
      </div>
      <p className="line-inspector-text">{line.text || '（空行）'}</p>
      <dl className="override-list">
        <Row label="时间" value={isTimed(line) ? `${formatTime(line.startUs)}～${formatTime(line.endUs)}` : '没有时间'} />
        {program && <Row label="显示在" value={lineContainerText(model, program, line.id)} />}
      </dl>
      <p className="clip-container">
        <mdui-button variant="text" className="line-seek" disabled={!isTimed(line)} onClick={() => isTimed(line) && props.onSeek((line.startUs + line.endUs) / 2)}>
          跳到这一句
        </mdui-button>
      </p>
      {actions.block && <p className="hint property-block">{actions.block}</p>}
      {!response ? (
        presentation?.status === 'error' ? (
          <p className="notice-error line-presentation-error">{presentation.error}</p>
        ) : (
          <p className="hint">正在读取……</p>
        )
      ) : (
        <>
          {presentation?.status === 'error' && <p className="notice-error line-presentation-error">{presentation.error}</p>}
          <TransformSection key={`transform-${line.id}`} branchId={props.branchId} lineId={line.id} transform={response.localTransform} actions={actions} />
          <LineAnimationSection
            key={`animation-${line.id}`}
            branchId={props.branchId}
            lineId={line.id}
            local={response.localEnterAnimation}
            effective={response.effectiveEnterAnimation}
            source={response.animationSource}
            actions={actions}
          />
        </>
      )}
      <ProjectAnimationSection animation={model.projectEnterAnimation} actions={actions} />
      {props.leadIns && (
        <>
          <h3 className="section-title">前奏</h3>
          {leadIn ? (
            <p className="clip-container line-lead-in">
              <span>
                有前奏：{secondsText(leadIn.durationUs)}
                {leadIn.enabled ? '' : '（已停用）'}
              </span>
              <mdui-button variant="text" className="line-lead-in-show" onClick={() => props.leadIns!.onSelect(leadIn)}>
                查看
              </mdui-button>
            </p>
          ) : (
            <mdui-button
              variant="text"
              className="line-lead-in-add"
              disabled={!!props.leadIns.block || !isTimed(line)}
              title={props.leadIns.block ?? (isTimed(line) ? undefined : '这一句没有时间。')}
              onClick={() => props.leadIns!.onAdd(line.id)}
            >
              为这一句添加前奏…
            </mdui-button>
          )}
        </>
      )}
    </div>
  );
}

/** 选了几句（§14.7 第 4 种情况）：只显示范围，可以为这几句新建片段；工程默认进入动画。 */
export function LinesSummary(props: {
  model: OrchestrationModel;
  branchName: string;
  range: LineRange;
  actions: LineActions;
  clip: { block: string | undefined; busy: boolean; onNew: () => void };
}) {
  const { range } = props;
  return (
    <div className="inspector line-inspector">
      <div className="inspector-path">
        <span className="pane-title-text">{[props.branchName, '已选句子'].join(' / ')}</span>
        <span className="muted">句子</span>
      </div>
      <p className="line-inspector-text lines-summary">
        已选{lineRangeText(range)}，共 {range.count} 句
      </p>
      <p className="hint">本句微调一次只对一句：在时间轴上点一句，或在画布上双击文字。</p>
      <mdui-button variant="text" className="lines-new-clip" disabled={!!props.clip.block || props.clip.busy || !range.contiguous} title={props.clip.block} onClick={props.clip.onNew}>
        为这几句新建片段
      </mdui-button>
      {props.actions.block && <p className="hint property-block">{props.actions.block}</p>}
      <ProjectAnimationSection animation={props.model.projectEnterAnimation} actions={props.actions} />
    </div>
  );
}

/** 本句微调：X、Y 偏移（所在容器参考框的百分比）、旋转、缩放；每改一项提交一条完整的四项；清除发 null。 */
function TransformSection(props: { branchId: string; lineId: string; transform: LineLocalTransform | null; actions: LineActions }) {
  const { branchId, lineId, transform, actions } = props;
  const t = transform ?? NO_TRANSFORM;
  const disabled = !!actions.block || actions.busy;
  const set = (patch: Partial<LineLocalTransform>) => void actions.run(lineTransformCommand(branchId, lineId, transform, patch), '已调整本句微调');
  return (
    <>
      <h3 className="section-title">本句微调</h3>
      <div className="prop-grid line-transform">
        <NumberField label="X 偏移" suffix="%" value={toPercent(t.offsetXRatio)} disabled={disabled} className="line-x" onCommit={(v) => set({ offsetXRatio: ratio(v) })} />
        <NumberField label="Y 偏移" suffix="%" value={toPercent(t.offsetYRatio)} disabled={disabled} className="line-y" onCommit={(v) => set({ offsetYRatio: ratio(v) })} />
        <NumberField label="旋转" suffix="°" value={roundTo(t.rotationDegrees, 2)} disabled={disabled} className="line-rotation" onCommit={(rotationDegrees) => set({ rotationDegrees })} />
        <NumberField label="缩放" suffix="倍" value={roundTo(t.scale, 3)} greaterThan={0} disabled={disabled} className="line-scale" onCommit={(scale) => set({ scale })} />
      </div>
      <p className="hint">偏移按所在容器参考框的宽高算，可以为负；也可以在画布上拖动这一句。</p>
      <mdui-button
        variant="text"
        className="line-transform-clear"
        disabled={disabled || !transform}
        onClick={() => void actions.run({ type: 'orchestration.setLineLocalTransform', branchId, lineId, transform: null }, '已清除本句微调')}
      >
        清除本句微调
      </mdui-button>
    </>
  );
}

/** 进入动画的五个字段（§14.7）：本句与工程默认共用。 */
function AnimationFields(props: { animation: LineEnterAnimation; disabled: boolean; epoch: number; className: string; onCommit: (patch: Partial<LineEnterAnimation>) => void }) {
  const a = props.animation;
  return (
    <div className={`prop-grid ${props.className}`}>
      <NumberField label="时长" suffix="秒" value={toSeconds(a.durationUs)} min={0} max={10} disabled={props.disabled} className="enter-duration" onCommit={(v) => props.onCommit({ durationUs: enterDurationUs(v) })} />
      <Select<string>
        key={props.epoch}
        label="缓动"
        value={a.easing}
        options={withCurrent(ENTER_EASING_OPTIONS, a.easing)}
        disabled={props.disabled}
        className="enter-easing"
        onChange={(easing) => props.onCommit({ easing: easing as LineEnterAnimation['easing'] })}
      />
      <NumberField label="起始不透明度" suffix="%" value={toPercent(a.startOpacity)} min={0} max={100} disabled={props.disabled} className="enter-opacity" onCommit={(v) => props.onCommit({ startOpacity: ratio(v) })} />
      <NumberField label="起始缩放" suffix="倍" value={roundTo(a.startScale, 3)} greaterThan={0} max={4} disabled={props.disabled} className="enter-scale" onCommit={(startScale) => props.onCommit({ startScale })} />
      <NumberField
        label="起始纵向偏移"
        suffix="px"
        value={roundTo(a.startOffsetPx, 2)}
        min={-10000}
        max={10000}
        disabled={props.disabled}
        className="enter-offset"
        helper="正值从下方进入。"
        onCommit={(startOffsetPx) => props.onCommit({ startOffsetPx })}
      />
    </div>
  );
}

const NEUTRAL_HINT = '起始不透明度 100%、缩放 1、偏移 0：这样设置看不出效果。';

/** 本句进入动画：写明实际生效的动画和来源；“本句单独设置”打开时用实际生效的作初值，关掉时清除。 */
function LineAnimationSection(props: {
  branchId: string;
  lineId: string;
  local: LineEnterAnimation | null;
  effective: LineEnterAnimation | null;
  source: string;
  actions: LineActions;
}) {
  const { branchId, lineId, local, actions } = props;
  const [epoch, reset] = useSelectReset();
  const disabled = !!actions.block || actions.busy;
  const run = async (animation: LineEnterAnimation | null, done: string) => {
    if ((await actions.run(lineEnterCommand(branchId, lineId, animation), done)) !== 'applied') reset();
  };
  return (
    <>
      <h3 className="section-title">进入动画</h3>
      <p className="line-animation-effective">
        {enterAnimationText(props.effective)} · {ANIMATION_SOURCE_TEXT[props.source] ?? props.source}
      </p>
      <Switch
        key={`own-${epoch}`}
        label="本句单独设置"
        checked={!!local}
        disabled={disabled}
        onChange={(on) =>
          void (on ? run(props.effective ?? DEFAULT_ENTER_ANIMATION, '已为这一句单独设置进入动画') : run(null, '已清除本句进入动画，改用工程默认'))
        }
      />
      {local && (
        <>
          <AnimationFields animation={local} disabled={disabled} epoch={epoch} className="line-animation" onCommit={(patch) => void run(enterAnimationOf(local, patch), '已调整本句进入动画')} />
          {isNeutralAnimation(local) && <p className="hint">{NEUTRAL_HINT}</p>}
        </>
      )}
    </>
  );
}

/** 工程默认进入动画（§14.7）：开关和五个字段；关掉时发 null。 */
function ProjectAnimationSection(props: { animation: LineEnterAnimation | null; actions: LineActions }) {
  const { animation, actions } = props;
  const [epoch, reset] = useSelectReset();
  const disabled = !!actions.block || actions.busy;
  const run = async (next: LineEnterAnimation | null, done: string) => {
    if ((await actions.run(projectEnterCommand(next), done)) !== 'applied') reset();
  };
  return (
    <>
      <h3 className="section-title">工程默认进入动画</h3>
      <Switch
        key={`project-${epoch}`}
        label="开启"
        checked={!!animation}
        disabled={disabled}
        onChange={(on) => void (on ? run(DEFAULT_ENTER_ANIMATION, '已开启工程默认进入动画') : run(null, '已关闭工程默认进入动画'))}
      />
      {animation && (
        <>
          <AnimationFields animation={animation} disabled={disabled} epoch={epoch} className="project-animation" onCommit={(patch) => void run(enterAnimationOf(animation, patch), '已调整工程默认进入动画')} />
          {isNeutralAnimation(animation) && <p className="hint">{NEUTRAL_HINT}</p>}
        </>
      )}
      <p className="hint">所有句子默认用它，本句可以单独覆盖。</p>
    </>
  );
}
