import { useState, type ReactNode } from 'react';
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
import { Button } from '../ui/Button';
import { Segmented } from '../ui/choices';
import { NumberField, Select, TextField } from '../ui/fields';
import { FormDialog } from '../ui/FormDialog';
import { Group, Note } from '../workspace/panel';
import type { OrchestrationResult } from './orchestration-store';

/** 属性编辑（G21）：不可用时 block 是原因；run 发一条属性命令，成功时提示 done 并可撤销。 */
export interface PropertyActions {
  block: string | undefined;
  busy: boolean;
  run: (command: OrchestrationPropertiesCommand, done: string) => Promise<OrchestrationResult>;
}

/** 发一条属性命令；没有命令（选项不能选）时什么都不做。下拉框是受控的，没成功时显示自动回到当前值。 */
const commitWith = (actions: PropertyActions) => (command: OrchestrationPropertiesCommand | undefined, done: string) => {
  if (command) void actions.run(command, done);
};

const ratio = (percent: number) => percent / 100;

/** 已经是轮换时不用提示不能改成轮换。 */
const kindBlockOf = (block: string | undefined, node: OrchestrationNode) => (node.layout?.kind === 'SlotSequence' ? undefined : block);

/** 位置（X、Y、旋转、缩放、锚点）：外层位置与内部布局的默认位置共用。 */
function PlacementFields(props: { placement: OrchestrationPlacement; disabled: boolean; className: string; onCommit: (patch: Partial<OrchestrationPlacement>) => void }) {
  const p = props.placement;
  return (
    <div className={`g2 ${props.className}`}>
      <NumberField label="X" labelPlacement="inside" suffix="%" value={toPercent(p.x)} disabled={props.disabled} onCommit={(v) => props.onCommit({ x: ratio(v) })} />
      <NumberField label="Y" labelPlacement="inside" suffix="%" value={toPercent(p.y)} disabled={props.disabled} onCommit={(v) => props.onCommit({ y: ratio(v) })} />
      <NumberField
        label="旋转"
        labelPlacement="inside"
        suffix="°"
        value={roundTo(p.rotationDegrees, 2)}
        disabled={props.disabled}
        onCommit={(rotationDegrees) => props.onCommit({ rotationDegrees })}
      />
      <NumberField label="缩放" labelPlacement="inside" suffix="×" value={roundTo(p.scale, 3)} greaterThan={0} disabled={props.disabled} onCommit={(scale) => props.onCommit({ scale })} />
      <Select label="锚点" labelPlacement="inside" value={p.anchor} options={anchorOptions(p.anchor)} disabled={props.disabled} className="span2" onChange={(anchor) => props.onCommit({ anchor })} />
    </div>
  );
}

/** 位置与变换、参考框（W4 设计 §13.2）：每次提交一条 setNodeGeometry，带完整的位置和参考框。 */
export function GeometrySection(props: { model: OrchestrationModel; node: OrchestrationNode; actions: PropertyActions }) {
  const { node, actions } = props;
  const commit = commitWith(actions);
  const disabled = !!actions.block || actions.busy;
  const name = nodeName(props.model, node);
  return (
    <>
      <Group title="位置与变换">
        <PlacementFields
          placement={node.placement}
          disabled={disabled}
          className="node-placement"
          onCommit={(placement) => commit(geometryCommand(node, { placement }), `已调整“${name}”的位置`)}
        />
        <Note>X、Y 是锚点在父级参考框里的位置，可以超出 0～100%（在父级参考框外）。</Note>
      </Group>
      <Group title="参考框">
        <div className="g2 node-reference">
          <NumberField
            label="宽"
            labelPlacement="inside"
            suffix="%"
            value={toPercent(node.referenceRect.widthRatio)}
            greaterThan={0}
            disabled={disabled}
            onCommit={(v) => commit(geometryCommand(node, { referenceRect: { widthRatio: ratio(v) } }), `已调整“${name}”的参考框`)}
          />
          <NumberField
            label="高"
            labelPlacement="inside"
            suffix="%"
            value={toPercent(node.referenceRect.heightRatio)}
            greaterThan={0}
            disabled={disabled}
            onCommit={(v) => commit(geometryCommand(node, { referenceRect: { heightRatio: ratio(v) } }), `已调整“${name}”的参考框`)}
          />
        </div>
      </Group>
    </>
  );
}

type KindChoice = 'Auto' | 'SlotSequence';

/**
 * 内部布局（§13.3）：类型（标题右边的分段按钮）、槽位、默认位置、安全区、最大宽度。改成轮换时先填槽位数。
 * kindBlock 是不能改成轮换的原因（它是动画不是逐字的来源的默认容器）。slots 是槽位列表（结构编辑，由页面给出）。
 */
export function LayoutSection(props: { model: OrchestrationModel; node: OrchestrationNode; kindBlock: string | undefined; actions: PropertyActions; slots?: ReactNode }) {
  const { model, node, actions } = props;
  const kindBlock = kindBlockOf(props.kindBlock, node);
  const layout = node.layout!;
  const commit = commitWith(actions);
  const [kindOpen, setKindOpen] = useState(false);
  const disabled = !!actions.block || actions.busy;
  const area = layout.safeArea;
  const def = layout.defaultPlacement;
  const sideCheck = (side: 'left' | 'top' | 'right' | 'bottom') => (percent: number) => safeAreaBlock({ ...area, [side]: ratio(percent) });
  const setSide = (side: 'left' | 'top' | 'right' | 'bottom') => (percent: number) => commit(layoutSettingsCommand(node, { safeArea: { [side]: ratio(percent) } }), '已调整安全区');
  const side = (key: 'left' | 'top' | 'right' | 'bottom', label: string) => (
    <NumberField label={label} labelPlacement="inside" suffix="%" value={toPercent(area[key])} min={0} max={100} check={sideCheck(key)} disabled={disabled} onCommit={setSide(key)} />
  );

  const changeKind = (kind: KindChoice) => {
    if (kind === 'SlotSequence') {
      setKindOpen(true);
      return;
    }
    const count = layout.slotIds.length;
    commit(autoLayoutCommand(node.id), count > 0 ? `已改成自动布局：${count} 个槽位留作普通子节点` : '已改成自动布局');
  };

  return (
    <Group
      title="内部布局"
      className="layout-group"
      extra={
        <Segmented<string>
          label="内部布局类型"
          size="sm"
          value={layout.kind}
          options={withCurrent<KindChoice>(
            [
              { value: 'Auto', label: layoutText('Auto') },
              { value: 'SlotSequence', label: layoutText('SlotSequence'), disabled: !!kindBlock },
            ],
            layout.kind,
          )}
          disabled={disabled}
          onChange={(kind) => changeKind(kind as KindChoice)}
        />
      }
    >
      {kindBlock && <Note className="kind-block">{kindBlock}</Note>}
      {props.slots}
      <div className="sub">
        <span>默认位置</span>
        {def ? (
          <Button variant="text" size="sm" isDisabled={disabled} onPress={() => commit(defaultPlacementCommand(node, null), '已清除默认位置')}>
            清除默认位置
          </Button>
        ) : (
          <Button variant="text" size="sm" isDisabled={disabled} onPress={() => commit(defaultPlacementCommand(node, {}), '已设置默认位置')}>
            设置默认位置
          </Button>
        )}
      </div>
      {def ? (
        <PlacementFields placement={def} disabled={disabled} className="default-placement" onCommit={(patch) => commit(defaultPlacementCommand(node, patch), '已调整默认位置')} />
      ) : (
        <Note className="default-placement-unset">未设置：句子按布局自己的规则摆放。“设置”用节点当前的位置作为初值。</Note>
      )}
      <div className="sub">安全区</div>
      <div className="g2 safe-area">
        {side('left', '左')}
        {side('top', '上')}
        {side('right', '右')}
        {side('bottom', '下')}
      </div>
      <NumberField
        label="最大宽度"
        labelPlacement="inside"
        suffix="%"
        value={toPercent(layout.maxWidthRatio)}
        greaterThan={0}
        max={100}
        disabled={disabled}
        onCommit={(v) => commit(layoutSettingsCommand(node, { maxWidthRatio: ratio(v) }), '已调整最大宽度')}
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
        onClose={() => setKindOpen(false)}
      />
    </Group>
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
      className="orch-dialog slot-sequence-dialog"
    >
      <TextField label="槽位数" value={text} onChange={setText} error={error} {...(error ? {} : { helper })} onEnter={submit} autoFocus />
      <Note>句子按顺序轮流放进槽位；“同时最多显示”等调度参数改完后在“调度”里调。</Note>
    </FormDialog>
  );
}

/** 槽位动画（§13.4）：分“调度”“动画”两组，每改一项提交一条 setSlotAnimation，带完整的十四个字段。时间用秒。 */
export function SlotAnimationSection(props: { node: OrchestrationNode; animation: SlotAnimation; actions: PropertyActions }) {
  const { node, animation: a, actions } = props;
  const commit = commitWith(actions);
  const disabled = !!actions.block || actions.busy;
  const set = (patch: Parameters<typeof slotAnimationCommand>[2]) => commit(slotAnimationCommand(node.id, a, patch), '已调整槽位动画');
  const setPose = (key: PoseKey) => (patch: Partial<SceneTransform>) => set({ [key]: patch });
  const pick = <K extends 'revealMode' | 'conflictPolicy' | 'retentionPolicy' | 'easing' | 'exitEasing'>(key: K, label: string, options: readonly { value: string; label: string }[], span?: boolean) => (
    <Select<string>
      label={label}
      labelPlacement="inside"
      value={a[key]}
      options={withCurrent(options, a[key])}
      disabled={disabled}
      {...(span ? { className: 'span2' } : {})}
      onChange={(value) => set({ [key]: value as SlotAnimation[K] })}
    />
  );
  return (
    <>
      <Group title="调度">
        <div className="g2 slot-schedule">
          {pick('revealMode', '出现', REVEAL_MODE_OPTIONS, a.revealMode !== 'Progress' && a.revealMode !== 'TimeAfterPreviousStart' && a.revealMode !== 'LeadBeforeNextStart')}
          {a.revealMode === 'Progress' && (
            <NumberField label="进度" labelPlacement="inside" suffix="%" value={toPercent(a.revealProgress)} min={0} max={100} disabled={disabled} onCommit={(v) => set({ revealProgress: ratio(v) })} />
          )}
          {a.revealMode === 'TimeAfterPreviousStart' && (
            <NumberField label="延迟" labelPlacement="inside" suffix="秒" value={toSeconds(a.revealDelayUs)} min={0} disabled={disabled} onCommit={(v) => set({ revealDelayUs: fromSeconds(v) })} />
          )}
          {a.revealMode === 'LeadBeforeNextStart' && (
            <NumberField label="提前" labelPlacement="inside" suffix="秒" value={toSeconds(a.revealLeadUs)} min={0} disabled={disabled} onCommit={(v) => set({ revealLeadUs: fromSeconds(v) })} />
          )}
          <NumberField
            label="同时最多显示"
            labelPlacement="inside"
            suffix="句"
            value={a.maxVisibleLines}
            min={1}
            integer
            disabled={disabled}
            className="span2"
            onCommit={(maxVisibleLines) => set({ maxVisibleLines })}
          />
          {pick('conflictPolicy', '已满时', CONFLICT_POLICY_OPTIONS)}
          {pick('retentionPolicy', '保留到', RETENTION_POLICY_OPTIONS)}
        </div>
      </Group>
      <Group title="动画">
        <div className="g2 slot-animation">
          <NumberField label="进入" labelPlacement="inside" suffix="秒" value={toSeconds(a.enterDurationUs)} min={0} disabled={disabled} onCommit={(v) => set({ enterDurationUs: fromSeconds(v) })} />
          <NumberField label="退出" labelPlacement="inside" suffix="秒" value={toSeconds(a.exitDurationUs)} min={0} disabled={disabled} onCommit={(v) => set({ exitDurationUs: fromSeconds(v) })} />
          {pick('easing', '进入缓动', EASING_OPTIONS, true)}
          {pick('exitEasing', '退出缓动', EASING_OPTIONS, true)}
        </div>
        <details className="slot-poses">
          <summary>姿态：进入起点、停留、退出终点</summary>
          {(['enterFrom', 'hold', 'exitTo'] as const).map((key) => (
            <PoseFields key={key} title={POSE_TEXT[key]} pose={a[key]} disabled={disabled} className={`pose-${key}`} onCommit={setPose(key)} />
          ))}
        </details>
      </Group>
    </>
  );
}

function PoseFields(props: { title: string; pose: SceneTransform; disabled: boolean; className: string; onCommit: (patch: Partial<SceneTransform>) => void }) {
  const p = props.pose;
  return (
    <section className={`pose ${props.className}`} aria-label={props.title}>
      <div className="sub">{props.title}</div>
      <div className="g2">
        <NumberField label="透明度" labelPlacement="inside" suffix="%" value={toPercent(p.opacity)} min={0} max={100} disabled={props.disabled} onCommit={(v) => props.onCommit({ opacity: ratio(v) })} />
        <NumberField label="缩放" labelPlacement="inside" suffix="×" value={roundTo(p.scale, 3)} greaterThan={0} disabled={props.disabled} onCommit={(scale) => props.onCommit({ scale })} />
        <NumberField label="X 偏移" labelPlacement="inside" suffix="px" value={roundTo(p.offsetXPx, 2)} disabled={props.disabled} onCommit={(offsetXPx) => props.onCommit({ offsetXPx })} />
        <NumberField label="Y 偏移" labelPlacement="inside" suffix="px" value={roundTo(p.offsetYPx, 2)} disabled={props.disabled} onCommit={(offsetYPx) => props.onCommit({ offsetYPx })} />
        <NumberField
          label="旋转"
          labelPlacement="inside"
          suffix="°"
          value={roundTo(p.rotationDegrees, 2)}
          disabled={props.disabled}
          onCommit={(rotationDegrees) => props.onCommit({ rotationDegrees })}
        />
      </div>
    </section>
  );
}

/** 来源的动画策略（§13.5）：只能用逐字时（有片段、前奏提示，或默认容器是轮换），实际不是逐字的选项不能选，写明原因。 */
export function AnimationPolicyField(props: { model: OrchestrationModel; program: OrchestrationProgram; primary: boolean; actions: PropertyActions }) {
  const { model, program, primary, actions } = props;
  const commit = commitWith(actions);
  const options = animationPolicyOptions(model, program, primary);
  const value: PolicyChoice = (program.animationPolicy as PolicyChoice | null | undefined) ?? 'role';
  const restricted = options.some((o) => o.disabled && o.value !== value) ? karaokeOnlyReason(model, program) : undefined;
  return (
    <>
      <Select<string>
        label="动画"
        labelPlacement="inside"
        value={value}
        options={options}
        disabled={!!actions.block || actions.busy}
        onChange={(choice) => {
          const reason = animationPolicyBlock(model, program, primary, choice as PolicyChoice);
          const label = choice === 'role' ? '按来源角色' : animationPolicyText(choice);
          commit(reason ? undefined : animationPolicyCommand(program.branchId, choice as PolicyChoice), `动画已改为“${label}”`);
        }}
      />
      {restricted && <Note className="policy-block">{restricted}</Note>}
    </>
  );
}
