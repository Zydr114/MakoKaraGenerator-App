import { useReducer, useState } from 'react';
import type {
  OrchestrationNode,
  OrchestrationPlacement,
  OrchestrationProgram,
  OrchestrationPropertiesCommand,
  SceneTransform,
  SlotAnimation,
} from '../../protocol';
import { animationPolicyText, layoutText, nodeName, type OrchestrationModel } from '../model/orchestration';
import {
  anchorOptions,
  animationPolicyBlock,
  animationPolicyCommand,
  animationPolicyOptions,
  autoLayoutCommand,
  CONFLICT_POLICY_OPTIONS,
  defaultPlacementCommand,
  EASING_OPTIONS,
  fromSeconds,
  karaokeOnlyReason,
  geometryCommand,
  layoutSettingsCommand,
  MAX_SLOTS,
  POSE_TEXT,
  RETENTION_POLICY_OPTIONS,
  REVEAL_MODE_OPTIONS,
  reusableSlotChildren,
  roundTo,
  safeAreaBlock,
  slotAnimationCommand,
  slotSequenceCommand,
  toPercent,
  toSeconds,
  withCurrent,
  type PolicyChoice,
  type PoseKey,
} from '../model/orchestration-properties';
import { FormDialog } from '../ui/legacy/FormDialog';
import { NumberField, Select, TextField } from '../ui/legacy/fields';
import type { OrchestrationResult } from './orchestration-store';

/** 属性编辑（G21）：不可用时 block 是原因；run 发一条属性命令，成功时提示 done 并可撤销。 */
export interface PropertyActions {
  block: string | undefined;
  busy: boolean;
  run: (command: OrchestrationPropertiesCommand, done: string) => Promise<OrchestrationResult>;
}

/**
 * 下拉框提交没成功（被拒绝或 no-op）时，受控值没变，mdui-select 却已显示新选的项：换个 key 重建，显示回到受控值。
 * 重建推迟到下一个宏任务：mdui-select 发出 change 后还要等自己更新完再做校验，这期间被摘掉会抛异常。
 */
function useCommit(actions: PropertyActions): [number, (command: OrchestrationPropertiesCommand | undefined, done: string) => Promise<void>, () => void] {
  const [epoch, bump] = useReducer((x: number) => x + 1, 0);
  const reset = () => void setTimeout(bump, 0);
  const commit = async (command: OrchestrationPropertiesCommand | undefined, done: string) => {
    if (!command || (await actions.run(command, done)) !== 'applied') reset();
  };
  return [epoch, commit, reset];
}

const ratio = (percent: number) => percent / 100;

/** 已经是轮换时不用提示不能改成轮换。 */
const kindBlockOf = (block: string | undefined, node: OrchestrationNode) => (node.layout?.kind === 'SlotSequence' ? undefined : block);

/** 位置（X、Y、锚点、旋转、缩放）的五个输入框：外层位置与内部布局的默认位置共用。 */
function PlacementFields(props: { placement: OrchestrationPlacement; disabled: boolean; epoch: number; className: string; onCommit: (patch: Partial<OrchestrationPlacement>) => void }) {
  const p = props.placement;
  return (
    <div className={`prop-grid ${props.className}`}>
      <NumberField label="X" suffix="%" value={toPercent(p.x)} disabled={props.disabled} className="prop-x" onCommit={(v) => props.onCommit({ x: ratio(v) })} />
      <NumberField label="Y" suffix="%" value={toPercent(p.y)} disabled={props.disabled} className="prop-y" onCommit={(v) => props.onCommit({ y: ratio(v) })} />
      <Select
        key={props.epoch}
        label="锚点"
        value={p.anchor}
        options={anchorOptions(p.anchor)}
        disabled={props.disabled}
        className="prop-anchor"
        onChange={(anchor) => props.onCommit({ anchor })}
      />
      <NumberField label="旋转" suffix="°" value={roundTo(p.rotationDegrees, 2)} disabled={props.disabled} className="prop-rotation" onCommit={(rotationDegrees) => props.onCommit({ rotationDegrees })} />
      <NumberField label="缩放" suffix="倍" value={roundTo(p.scale, 3)} greaterThan={0} disabled={props.disabled} className="prop-scale" onCommit={(scale) => props.onCommit({ scale })} />
    </div>
  );
}

/** 位置与变换、参考框（W4 设计 §13.2）：每次提交一条 setNodeGeometry，带完整的位置和参考框。 */
export function GeometrySection(props: { model: OrchestrationModel; node: OrchestrationNode; actions: PropertyActions }) {
  const { node, actions } = props;
  const [epoch, commit] = useCommit(actions);
  const disabled = !!actions.block || actions.busy;
  const name = nodeName(props.model, node);
  return (
    <>
      <h3 className="section-title">位置与变换</h3>
      <PlacementFields
        placement={node.placement}
        disabled={disabled}
        epoch={epoch}
        className="node-placement"
        onCommit={(placement) => void commit(geometryCommand(node, { placement }), `已调整“${name}”的位置`)}
      />
      <p className="hint">X、Y 是锚点在父级参考框里的位置，可以超出 0～100%（在父级参考框外）。</p>
      <h3 className="section-title">参考框</h3>
      <div className="prop-grid node-reference">
        <NumberField
          label="宽"
          suffix="%"
          value={toPercent(node.referenceRect.widthRatio)}
          greaterThan={0}
          disabled={disabled}
          className="prop-width"
          onCommit={(v) => void commit(geometryCommand(node, { referenceRect: { widthRatio: ratio(v) } }), `已调整“${name}”的参考框`)}
        />
        <NumberField
          label="高"
          suffix="%"
          value={toPercent(node.referenceRect.heightRatio)}
          greaterThan={0}
          disabled={disabled}
          className="prop-height"
          onCommit={(v) => void commit(geometryCommand(node, { referenceRect: { heightRatio: ratio(v) } }), `已调整“${name}”的参考框`)}
        />
      </div>
    </>
  );
}

type KindChoice = 'Auto' | 'SlotSequence';

/**
 * 内部布局（§13.3）：类型、默认位置、安全区、最大宽度。改成轮换时先填槽位数。
 * kindBlock 是不能改成轮换的原因（它是动画不是逐字的来源的默认容器）。
 */
export function LayoutSection(props: { model: OrchestrationModel; node: OrchestrationNode; kindBlock: string | undefined; actions: PropertyActions }) {
  const { model, node, actions } = props;
  const kindBlock = kindBlockOf(props.kindBlock, node);
  const layout = node.layout!;
  const [epoch, commit, reset] = useCommit(actions);
  const [kindOpen, setKindOpen] = useState(false);
  const disabled = !!actions.block || actions.busy;
  const area = layout.safeArea;
  const def = layout.defaultPlacement;
  const sideCheck = (side: 'left' | 'top' | 'right' | 'bottom') => (percent: number) => safeAreaBlock({ ...area, [side]: ratio(percent) });
  const setSide = (side: 'left' | 'top' | 'right' | 'bottom') => (percent: number) => void commit(layoutSettingsCommand(node, { safeArea: { [side]: ratio(percent) } }), '已调整安全区');

  const changeKind = (kind: KindChoice) => {
    if (kind === 'SlotSequence') {
      setKindOpen(true);
      return;
    }
    const count = layout.slotIds.length;
    void commit(autoLayoutCommand(node.id), count > 0 ? `已改成自动布局：${count} 个槽位留作普通子节点` : '已改成自动布局');
  };

  return (
    <>
      <h3 className="section-title">内部布局</h3>
      <Select<string>
        key={`kind-${epoch}`}
        label="类型"
        value={layout.kind}
        options={withCurrent<KindChoice>(
          [
            { value: 'Auto', label: layoutText('Auto') },
            { value: 'SlotSequence', label: layoutText('SlotSequence'), disabled: !!kindBlock },
          ],
          layout.kind,
        )}
        disabled={disabled}
        className="prop-field layout-kind"
        onChange={(kind) => changeKind(kind as KindChoice)}
      />
      {kindBlock && <p className="hint kind-block">{kindBlock}</p>}
      <div className="prop-subtitle">
        <span>默认位置</span>
        {def ? (
          <mdui-button variant="text" className="default-placement-clear" disabled={disabled} onClick={() => void commit(defaultPlacementCommand(node, null), '已清除默认位置')}>
            清除
          </mdui-button>
        ) : (
          <mdui-button variant="text" className="default-placement-set" disabled={disabled} onClick={() => void commit(defaultPlacementCommand(node, {}), '已设置默认位置')}>
            设置
          </mdui-button>
        )}
      </div>
      {def ? (
        <PlacementFields
          placement={def}
          disabled={disabled}
          epoch={epoch}
          className="default-placement"
          onCommit={(patch) => void commit(defaultPlacementCommand(node, patch), '已调整默认位置')}
        />
      ) : (
        <p className="hint default-placement-unset">未设置：句子按布局自己的规则摆放。“设置”用节点当前的位置作为初值。</p>
      )}
      <div className="prop-subtitle">安全区</div>
      <div className="prop-grid safe-area">
        <NumberField label="左" suffix="%" value={toPercent(area.left)} min={0} max={100} check={sideCheck('left')} disabled={disabled} className="safe-left" onCommit={setSide('left')} />
        <NumberField label="上" suffix="%" value={toPercent(area.top)} min={0} max={100} check={sideCheck('top')} disabled={disabled} className="safe-top" onCommit={setSide('top')} />
        <NumberField label="右" suffix="%" value={toPercent(area.right)} min={0} max={100} check={sideCheck('right')} disabled={disabled} className="safe-right" onCommit={setSide('right')} />
        <NumberField label="下" suffix="%" value={toPercent(area.bottom)} min={0} max={100} check={sideCheck('bottom')} disabled={disabled} className="safe-bottom" onCommit={setSide('bottom')} />
      </div>
      <NumberField
        label="最大宽度"
        suffix="%"
        value={toPercent(layout.maxWidthRatio)}
        greaterThan={0}
        max={100}
        disabled={disabled}
        className="prop-field max-width"
        onCommit={(v) => void commit(layoutSettingsCommand(node, { maxWidthRatio: ratio(v) }), '已调整最大宽度')}
      />
      <SlotSequenceDialog
        open={kindOpen}
        model={model}
        node={node}
        busy={actions.busy}
        onSubmit={async (count) => {
          const result = await actions.run(slotSequenceCommand(model, node.id, count), `已改成轮换（${count} 个槽位）`);
          if (result !== 'failed') setKindOpen(false);
        }}
        onClose={() => {
          // 取消时下拉框还显示着“轮换”，重建回到当前类型（改成功了也无妨）。
          setKindOpen(false);
          reset();
        }}
      />
    </>
  );
}

/** 改成轮换（§13.3）：填槽位数；容器下已有直属普通子节点时预填它们的个数，并写明会用作前几个槽位。 */
function SlotSequenceDialog(props: { open: boolean; model: OrchestrationModel; node: OrchestrationNode; busy: boolean; onSubmit: (count: number) => Promise<void>; onClose: () => void }) {
  const reusable = reusableSlotChildren(props.model, props.node.id).length;
  const [text, setText] = useState('2');
  const [opened, setOpened] = useState(false);
  if (props.open !== opened) {
    setOpened(props.open);
    if (props.open) setText(String(reusable > 0 ? Math.min(reusable, MAX_SLOTS) : 2));
  }
  const count = /^\s*\d+\s*$/.test(text) ? Number(text) : NaN;
  const error = count >= 1 && count <= MAX_SLOTS ? undefined : `槽位数是 1～${MAX_SLOTS} 的整数。`;
  const used = Math.min(reusable, count || 0);
  const helper =
    reusable === 0
      ? '槽位由后端新建。'
      : count > reusable
        ? `现有 ${reusable} 个子节点会用作前 ${reusable} 个槽位，其余 ${count - reusable} 个新建。`
        : `用现有的前 ${used} 个子节点作槽位${reusable > used ? `，其余 ${reusable - used} 个留作普通子节点` : ''}。`;
  const submit = () => {
    if (!error && !props.busy) void props.onSubmit(count);
  };
  return (
    <FormDialog
      open={props.open}
      headline={`把“${nodeName(props.model, props.node)}”改成轮换`}
      submitText="改成轮换"
      submitDisabled={!!error}
      busy={props.busy}
      onSubmit={submit}
      onClose={props.onClose}
      className="structure-dialog slot-sequence-dialog"
    >
      <TextField label="槽位数" value={text} onChange={setText} error={error} helper={error ? '' : helper} className="dialog-field slot-sequence-count" onEnter={submit} />
      <p className="hint">句子按顺序轮流放进槽位；“同时最多显示”等调度参数改完后在“槽位动画”里调。</p>
    </FormDialog>
  );
}

/** 槽位动画（§13.4）：每改一项提交一条 setSlotAnimation，带完整的十四个字段。时间用秒。 */
export function SlotAnimationSection(props: { node: OrchestrationNode; animation: SlotAnimation; actions: PropertyActions }) {
  const { node, animation: a, actions } = props;
  const [epoch, commit] = useCommit(actions);
  const disabled = !!actions.block || actions.busy;
  const set = (patch: Parameters<typeof slotAnimationCommand>[2]) => void commit(slotAnimationCommand(node.id, a, patch), '已调整槽位动画');
  const setPose = (key: PoseKey) => (patch: Partial<SceneTransform>) => set({ [key]: patch });
  return (
    <>
      <h3 className="section-title">槽位动画</h3>
      <div className="prop-grid slot-animation">
        <Select<string>
          key={`reveal-${epoch}`}
          label="出现时机"
          value={a.revealMode}
          options={withCurrent(REVEAL_MODE_OPTIONS, a.revealMode)}
          disabled={disabled}
          className="reveal-mode"
          onChange={(revealMode) => set({ revealMode: revealMode as SlotAnimation['revealMode'] })}
        />
        {a.revealMode === 'Progress' && (
          <NumberField label="进度" suffix="%" value={toPercent(a.revealProgress)} min={0} max={100} disabled={disabled} className="reveal-progress" onCommit={(v) => set({ revealProgress: ratio(v) })} />
        )}
        {a.revealMode === 'TimeAfterPreviousStart' && (
          <NumberField label="延迟" suffix="秒" value={toSeconds(a.revealDelayUs)} min={0} disabled={disabled} className="reveal-delay" onCommit={(v) => set({ revealDelayUs: fromSeconds(v) })} />
        )}
        {a.revealMode === 'LeadBeforeNextStart' && (
          <NumberField label="提前" suffix="秒" value={toSeconds(a.revealLeadUs)} min={0} disabled={disabled} className="reveal-lead" onCommit={(v) => set({ revealLeadUs: fromSeconds(v) })} />
        )}
        <NumberField label="同时最多显示" suffix="句" value={a.maxVisibleLines} min={1} integer disabled={disabled} className="max-visible" onCommit={(maxVisibleLines) => set({ maxVisibleLines })} />
        <Select<string>
          key={`conflict-${epoch}`}
          label="槽位已满时"
          value={a.conflictPolicy}
          options={withCurrent(CONFLICT_POLICY_OPTIONS, a.conflictPolicy)}
          disabled={disabled}
          className="conflict-policy"
          onChange={(conflictPolicy) => set({ conflictPolicy: conflictPolicy as SlotAnimation['conflictPolicy'] })}
        />
        <Select<string>
          key={`retention-${epoch}`}
          label="保留到"
          value={a.retentionPolicy}
          options={withCurrent(RETENTION_POLICY_OPTIONS, a.retentionPolicy)}
          disabled={disabled}
          className="retention-policy"
          onChange={(retentionPolicy) => set({ retentionPolicy: retentionPolicy as SlotAnimation['retentionPolicy'] })}
        />
        <NumberField label="进入时长" suffix="秒" value={toSeconds(a.enterDurationUs)} min={0} disabled={disabled} className="enter-duration" onCommit={(v) => set({ enterDurationUs: fromSeconds(v) })} />
        <NumberField label="退出时长" suffix="秒" value={toSeconds(a.exitDurationUs)} min={0} disabled={disabled} className="exit-duration" onCommit={(v) => set({ exitDurationUs: fromSeconds(v) })} />
        <Select<string>
          key={`easing-${epoch}`}
          label="进入缓动"
          value={a.easing}
          options={withCurrent(EASING_OPTIONS, a.easing)}
          disabled={disabled}
          className="enter-easing"
          onChange={(easing) => set({ easing: easing as SlotAnimation['easing'] })}
        />
        <Select<string>
          key={`exit-easing-${epoch}`}
          label="退出缓动"
          value={a.exitEasing}
          options={withCurrent(EASING_OPTIONS, a.exitEasing)}
          disabled={disabled}
          className="exit-easing"
          onChange={(exitEasing) => set({ exitEasing: exitEasing as SlotAnimation['exitEasing'] })}
        />
      </div>
      <details className="slot-poses">
        <summary>姿态：进入起点、停留、退出终点</summary>
        {(['enterFrom', 'hold', 'exitTo'] as const).map((key) => (
          <PoseFields key={key} title={POSE_TEXT[key]} pose={a[key]} disabled={disabled} className={`pose-${key}`} onCommit={setPose(key)} />
        ))}
      </details>
    </>
  );
}

function PoseFields(props: { title: string; pose: SceneTransform; disabled: boolean; className: string; onCommit: (patch: Partial<SceneTransform>) => void }) {
  const p = props.pose;
  return (
    <>
      <div className="prop-subtitle">{props.title}</div>
      <div className={`prop-grid ${props.className}`}>
        <NumberField label="透明度" suffix="%" value={toPercent(p.opacity)} min={0} max={100} disabled={props.disabled} className="pose-opacity" onCommit={(v) => props.onCommit({ opacity: ratio(v) })} />
        <NumberField label="缩放" suffix="倍" value={roundTo(p.scale, 3)} greaterThan={0} disabled={props.disabled} className="pose-scale" onCommit={(scale) => props.onCommit({ scale })} />
        <NumberField label="X 偏移" suffix="px" value={roundTo(p.offsetXPx, 2)} disabled={props.disabled} className="pose-x" onCommit={(offsetXPx) => props.onCommit({ offsetXPx })} />
        <NumberField label="Y 偏移" suffix="px" value={roundTo(p.offsetYPx, 2)} disabled={props.disabled} className="pose-y" onCommit={(offsetYPx) => props.onCommit({ offsetYPx })} />
        <NumberField label="旋转" suffix="°" value={roundTo(p.rotationDegrees, 2)} disabled={props.disabled} className="pose-rotation" onCommit={(rotationDegrees) => props.onCommit({ rotationDegrees })} />
      </div>
    </>
  );
}

/** 来源的动画策略（§13.5）：只能用逐字时（有片段、前奏提示，或默认容器是轮换），实际不是逐字的选项不能选，写明原因。 */
export function AnimationPolicyField(props: { model: OrchestrationModel; program: OrchestrationProgram; primary: boolean; actions: PropertyActions }) {
  const { model, program, primary, actions } = props;
  const [epoch, commit] = useCommit(actions);
  const options = animationPolicyOptions(model, program, primary);
  const value: PolicyChoice = (program.animationPolicy as PolicyChoice | null | undefined) ?? 'role';
  const restricted = options.some((o) => o.disabled && o.value !== value) ? karaokeOnlyReason(model, program) : undefined;
  return (
    <>
      <Select<string>
        key={epoch}
        label="动画"
        value={value}
        options={options}
        disabled={!!actions.block || actions.busy}
        className="prop-field animation-policy"
        onChange={(choice) => {
          const reason = animationPolicyBlock(model, program, primary, choice as PolicyChoice);
          const label = choice === 'role' ? '按来源角色' : animationPolicyText(choice);
          void commit(reason ? undefined : animationPolicyCommand(program.branchId, choice as PolicyChoice), `动画已改为“${label}”`);
        }}
      />
      {restricted && <p className="hint policy-block">{restricted}</p>}
    </>
  );
}
