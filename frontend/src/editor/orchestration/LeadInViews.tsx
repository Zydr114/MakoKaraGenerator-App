// 前奏提示（G23，W4 设计 §14.2～§14.6）：前奏列表、前奏属性、提示组与标记两节、添加前奏与自动生成两个对话框。

import { useEffect, useState } from 'react';
import type { LeadInAttachmentMode, LeadInGenerationCommandResult, LeadInGenerationSettings, OrchestrationLeadIn, OrchestrationNode, OrchestrationProgram } from '../../protocol';
import type { EditorLine } from '../model/lyrics';
import { nodeName, type OrchestrationModel } from '../model/orchestration';
import {
  candidateReasonText,
  candidateSelectable,
  candidateStatusText,
  candidateSummary,
  countdownsOf,
  DEFAULT_LEAD_IN_SECONDS,
  defaultLeadInTarget,
  defaultSelection,
  generationSettingsBlock,
  initialGenerationSettings,
  leadInsUsing,
  leadInSpan,
  lineLabel,
  lineNumberText,
  markTextBlock,
  MAX_MARKS,
  newLeadInBlock,
  originText,
  secondsText,
  type LeadInPatch,
} from '../model/orchestration-lead-ins';
import { fromSeconds, toSeconds } from '../model/orchestration-properties';
import { formatTime } from '../model/time';
import { FormDialog } from '../ui/FormDialog';
import { CommitField, NumberField, Select, Switch, TextField } from '../ui/fields';
import type { GenerationPreviewState, NewLeadIn, OrchestrationResult } from './orchestration-store';
import { Row, ToolButton, useSelectReset } from './parts';

const ATTACHMENT_OPTIONS: readonly { value: LeadInAttachmentMode; label: string }[] = [
  { value: 'CueTargetText', label: '跟着目标句的文字' },
  { value: 'FixedParent', label: '固定在父级' },
];
const APPEARANCE_HINT = '标记的颜色和字体属于外观，外观页支持编排对象后在那里设置。';

/** 前奏的增删改（§14.2～§14.4）：block 是整体不可用的原因（只读、后端没开放、来源不支持）。 */
export interface LeadInActions {
  block: string | undefined;
  busy: boolean;
  /** 打开添加前奏对话框；lineId 是预填的目标句。 */
  onAdd: (lineId?: string) => void;
  onGenerate: () => void;
  onUpdate: (leadIn: OrchestrationLeadIn, patch: LeadInPatch, done: string) => Promise<OrchestrationResult>;
  onDelete: (leadIn: OrchestrationLeadIn, exclude: boolean) => Promise<void>;
  /** 改提示组的附着方式：带一个用它的前奏（可以在别的来源）。 */
  onAttachment: (branchId: string, leadIn: OrchestrationLeadIn, mode: LeadInAttachmentMode) => Promise<OrchestrationResult>;
  /** 选中某个来源里的前奏（分配条跟着换来源）。 */
  onSelect: (branchId: string, leadInId: string) => void;
  onSelectNode: (id: string) => void;
}

/** 标记的编辑（§14.6）；上移、下移、删除借用结构编辑。 */
export interface MarkActions {
  block: string | undefined;
  busy: boolean;
  onText: (nodeId: string, text: string) => Promise<void>;
  onAdd: (containerId: string) => Promise<void>;
  onSpace: (containerId: string) => Promise<void>;
  /** 结构编辑整体不可用的原因。 */
  structureBlock: string | undefined;
  blocks: (id: string) => { up: string | undefined; down: string | undefined; remove: string | undefined };
  onReorder: (id: string, delta: -1 | 1) => Promise<void>;
  onRemove: (id: string) => Promise<void>;
}

const timeText = (leadIn: OrchestrationLeadIn) => {
  const span = leadInSpan(leadIn);
  return span ? `${formatTime(span.startUs)}～${formatTime(span.endUs)}` : '没有时间';
};

const nameOf = (model: OrchestrationModel, id: string) => {
  const node = model.byId.get(id);
  return node ? nodeName(model, node) : `${id}（不存在）`;
};

// ── 前奏列表 ──

/** 前奏列表（§14.2）：与前奏轨显示同一份数据，联动选中；标题上可以添加前奏、自动生成。 */
export function LeadInList(props: {
  model: OrchestrationModel;
  leadIns: readonly OrchestrationLeadIn[];
  lines: readonly EditorLine[];
  selectedId: string | undefined;
  actions: LeadInActions;
  onSelect: (id: string) => void;
}) {
  const { model, actions } = props;
  const disabled = !!actions.block || actions.busy;
  return (
    <div className="clip-panel lead-in-panel" role="region" aria-label="前奏提示">
      <div className="clip-panel-header">
        <span className="pane-title-text">前奏提示 · {props.leadIns.length} 个</span>
        <mdui-button variant="text" className="lead-in-new" disabled={disabled} title={actions.block} onClick={() => actions.onAdd()}>
          ＋ 添加前奏
        </mdui-button>
        <mdui-button variant="text" className="lead-in-generate" disabled={disabled} title={actions.block} onClick={actions.onGenerate}>
          自动生成…
        </mdui-button>
        {actions.block && <span className="hint">{actions.block}</span>}
      </div>
      {props.leadIns.length === 0 ? (
        <p className="hint clip-empty">这个来源还没有前奏提示。点“＋ 添加前奏”给某一句加，或用“自动生成…”按开头和长间奏一次生成。</p>
      ) : (
        <div className="clip-rows" role="listbox" aria-label="前奏列表">
          {props.leadIns.map((leadIn) => {
            const selected = leadIn.id === props.selectedId;
            return (
              <div
                key={leadIn.id}
                role="option"
                aria-selected={selected}
                data-lead-in-id={leadIn.id}
                className={`clip-row lead-in-row${selected ? ' selected' : ''}${leadIn.enabled ? '' : ' disabled'}`}
                onClick={() => props.onSelect(leadIn.id)}
              >
                <span className="target-label">{lineLabel(props.lines, leadIn.targetLineId)}</span>
                <span className="target-meta">{timeText(leadIn)}</span>
                <span className="target-meta">{secondsText(leadIn.durationUs)}</span>
                <span className="target-meta">▸ {nameOf(model, leadIn.indicatorContainerId)}</span>
                {leadIn.name && <span className="target-meta">“{leadIn.name}”</span>}
                <span className="target-template">{originText(leadIn.origin)}</span>
                {!leadIn.enabled && <span className="tag lead-in-off">已停用</span>}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

// ── 前奏属性 ──

/** 前奏的属性（§14.3）：名称、目标句、提示组、时长、启用，附着方式，删除。 */
export function LeadInInspector(props: {
  model: OrchestrationModel;
  program: OrchestrationProgram;
  leadIn: OrchestrationLeadIn;
  lines: readonly EditorLine[];
  branchName: string;
  actions: LeadInActions;
  /** 附着方式不可用的原因（只读、后端没开放）。 */
  attachmentBlock: string | undefined;
}) {
  const { model, program, leadIn, lines, actions } = props;
  const [epoch, reset] = useSelectReset();
  const disabled = !!actions.block || actions.busy;
  const update = async (patch: LeadInPatch, done: string) => {
    if ((await actions.onUpdate(leadIn, patch, done)) !== 'applied') reset();
  };
  const taken = new Set((program.leadIns ?? []).filter((l) => l.id !== leadIn.id).map((l) => l.targetLineId));
  const targetOptions = lines.map((l) => ({
    value: l.id,
    label: `${lineLabel(lines, l.id)}${taken.has(l.id) ? '（已有前奏）' : l.startUs === undefined ? '（没有时间）' : ''}`,
    disabled: taken.has(l.id) || l.startUs === undefined,
  }));
  if (!lines.some((l) => l.id === leadIn.targetLineId)) targetOptions.unshift({ value: leadIn.targetLineId, label: lineNumberText(lines, leadIn.targetLineId), disabled: true });
  const countdowns = countdownsOf(model, program);
  const indicator = model.byId.get(leadIn.indicatorContainerId);
  const indicatorOptions = countdowns.map((n) => ({ value: n.id, label: nodeName(model, n) }));
  if (!countdowns.some((n) => n.id === leadIn.indicatorContainerId)) indicatorOptions.unshift({ value: leadIn.indicatorContainerId, label: nameOf(model, leadIn.indicatorContainerId) });

  return (
    <div className="inspector lead-in-inspector">
      <div className="inspector-path">
        <span className="pane-title-text">{[props.branchName, '前奏提示', lineNumberText(lines, leadIn.targetLineId)].join(' / ')}</span>
        <span className="muted">前奏提示</span>
        <code className="node-id">{leadIn.id}</code>
      </div>
      <CommitField
        key={leadIn.id}
        label="名称"
        value={leadIn.name ?? ''}
        placeholder="不命名"
        helper="可以不填。"
        disabled={disabled}
        className="lead-in-name"
        onCommit={(name) => void update({ name }, name.trim() ? `前奏已改名为“${name.trim()}”` : '已清除前奏名称')}
      />
      <Select
        key={`target-${epoch}`}
        label="目标句"
        className="lead-in-target"
        value={leadIn.targetLineId}
        options={targetOptions}
        disabled={disabled}
        onChange={(targetLineId) => void update({ targetLineId }, `前奏已改到${lineNumberText(lines, targetLineId)}`)}
      />
      <Select
        key={`indicator-${epoch}`}
        label="提示组"
        className="lead-in-indicator"
        value={leadIn.indicatorContainerId}
        options={indicatorOptions}
        disabled={disabled || indicatorOptions.length < 2}
        onChange={(indicatorContainerId) => void update({ indicatorContainerId }, `前奏改用“${nameOf(model, indicatorContainerId)}”提示`)}
      />
      <div className="prop-grid lead-in-fields">
        <NumberField
          label="时长"
          suffix="秒"
          value={toSeconds(leadIn.durationUs)}
          greaterThan={0}
          disabled={disabled}
          className="lead-in-duration"
          onCommit={(seconds) => void update({ durationUs: fromSeconds(seconds) }, '已调整前奏')}
        />
      </div>
      <Switch
        key={`enabled-${epoch}`}
        label="启用"
        checked={leadIn.enabled}
        disabled={disabled}
        onChange={(enabled) => void update({ enabled }, enabled ? '已启用前奏' : '已停用前奏')}
      />
      <dl className="override-list">
        <Row label="时间" value={leadInSpan(leadIn) ? timeText(leadIn) : '没有时间（目标句没有时间，或起唱前不够一个时长）'} />
        <Row label="来源" value={originText(leadIn.origin)} />
      </dl>
      {leadIn.origin === 'Generated' && <p className="hint">自动生成的前奏：改动后变为手工，自动生成不会再改它。</p>}
      <AttachmentField
        model={model}
        branchId={program.branchId}
        indicator={indicator}
        leadIn={leadIn}
        block={actions.block ?? props.attachmentBlock}
        busy={actions.busy}
        onAttachment={actions.onAttachment}
      />
      {indicator && (
        <p className="clip-container">
          <mdui-button variant="text" className="lead-in-indicator-show" onClick={() => actions.onSelectNode(indicator.id)}>
            在结构树里查看提示组
          </mdui-button>
        </p>
      )}
      <div className="clip-actions">
        <mdui-button variant="outlined" className="lead-in-delete" disabled={disabled} onClick={() => void actions.onDelete(leadIn, false)}>
          删除前奏
        </mdui-button>
        {leadIn.origin === 'Generated' && (
          <mdui-button variant="outlined" className="lead-in-exclude" disabled={disabled} onClick={() => void actions.onDelete(leadIn, true)}>
            删除并不再自动生成
          </mdui-button>
        )}
      </div>
      <p className="hint">{actions.block ?? '删除前奏不会删除提示组。改动都可以撤销。'}</p>
    </div>
  );
}

/** 附着方式（§14.3）：属于提示组，带一个用它的前奏发命令；没有前奏用它时停用。 */
function AttachmentField(props: {
  model: OrchestrationModel;
  branchId: string | undefined;
  indicator: OrchestrationNode | undefined;
  leadIn: OrchestrationLeadIn | undefined;
  block: string | undefined;
  busy: boolean;
  onAttachment: LeadInActions['onAttachment'];
}) {
  const { model, indicator, leadIn } = props;
  const [epoch, reset] = useSelectReset();
  if (!indicator) return null;
  const mode = (indicator.content?.attachmentMode ?? 'CueTargetText') as LeadInAttachmentMode;
  const users = leadInsUsing(model, indicator.id).length;
  const options = ATTACHMENT_OPTIONS.some((o) => o.value === mode) ? ATTACHMENT_OPTIONS : [...ATTACHMENT_OPTIONS, { value: mode, label: mode }];
  const change = async (next: LeadInAttachmentMode) => {
    if (!leadIn || !props.branchId || (await props.onAttachment(props.branchId, leadIn, next)) !== 'applied') reset();
  };
  return (
    <>
      <h3 className="section-title">附着方式</h3>
      <Select
        key={epoch}
        label="提示组放在哪里"
        className="lead-in-attachment"
        value={mode}
        options={options}
        disabled={!!props.block || props.busy || !leadIn}
        onChange={(next) => void change(next)}
      />
      <p className="hint">
        {!leadIn
          ? '还没有前奏用这个提示组，添加前奏后才能切换附着方式。'
          : `附着方式属于提示组“${nodeName(model, indicator)}”，用它的 ${users} 个前奏都跟着变。切换时按目标句文字的真实位置换算，画面上不动。`}
      </p>
    </>
  );
}

// ── 提示组与标记 ──

/** 选中提示组时属性区的两节（§14.6）：用它的前奏与附着方式，标记列表。 */
export function CountdownSections(props: {
  model: OrchestrationModel;
  node: OrchestrationNode;
  lines: (branchId: string) => readonly EditorLine[];
  branchName: (branchId: string) => string;
  actions: LeadInActions;
  attachmentBlock: string | undefined;
  marks: MarkActions;
  onSelectNode: (id: string) => void;
}) {
  const { model, node, actions, marks } = props;
  const users = leadInsUsing(model, node.id);
  const first = users[0];
  const markIds = node.content?.markNodeIds ?? [];
  const markDisabled = !!marks.block || marks.busy;
  return (
    <>
      <h3 className="section-title">前奏 · {users.length} 个</h3>
      {users.length === 0 ? (
        <p className="hint">还没有前奏用这个提示组。</p>
      ) : (
        <div className="slot-list countdown-lead-ins" role="list" aria-label="用这个提示组的前奏">
          {users.map(({ program, leadIn }) => (
            <div key={leadIn.id} role="listitem" className="slot-row" data-lead-in-id={leadIn.id}>
              <button type="button" className="slot-name" onClick={() => actions.onSelect(program.branchId, leadIn.id)} title="选中这个前奏">
                {props.branchName(program.branchId)} · {lineLabel(props.lines(program.branchId), leadIn.targetLineId)} · {secondsText(leadIn.durationUs)}
                {leadIn.enabled ? '' : '（已停用）'}
              </button>
            </div>
          ))}
        </div>
      )}
      <AttachmentField
        model={model}
        branchId={first?.program.branchId}
        indicator={node}
        leadIn={first?.leadIn}
        block={props.attachmentBlock}
        busy={actions.busy}
        onAttachment={actions.onAttachment}
      />
      <h3 className="section-title">标记 · {markIds.length} 个</h3>
      <div className="slot-list mark-list" role="list" aria-label="标记">
        {markIds.map((id, i) => {
          const mark = model.byId.get(id);
          const blocks = marks.blocks(id);
          const own = (reason: string | undefined) => marks.structureBlock ?? (mark ? reason : '这个标记不存在。');
          return (
            <div key={id} role="listitem" className="slot-row mark-row" data-mark-id={id}>
              <CommitField
                label={`标记 ${i + 1}`}
                value={mark?.content?.text ?? ''}
                disabled={markDisabled || !mark}
                className="mark-text"
                validate={markTextBlock}
                onCommit={(text) => void marks.onText(id, text)}
              />
              <ToolButton label="上移" reason={own(blocks.up)} disabled={marks.busy} className="mark-up" onClick={() => void marks.onReorder(id, -1)}>
                <mdui-icon-arrow-upward />
              </ToolButton>
              <ToolButton label="下移" reason={own(blocks.down)} disabled={marks.busy} className="mark-down" onClick={() => void marks.onReorder(id, 1)}>
                <mdui-icon-arrow-downward />
              </ToolButton>
              <ToolButton label="删除" reason={own(blocks.remove)} disabled={marks.busy} className="mark-remove" onClick={() => void marks.onRemove(id)}>
                <mdui-icon-delete />
              </ToolButton>
            </div>
          );
        })}
      </div>
      <div className="mark-actions">
        <mdui-button variant="text" className="mark-add" disabled={markDisabled || markIds.length >= MAX_MARKS} title={markIds.length >= MAX_MARKS ? `最多 ${MAX_MARKS} 个标记。` : undefined} onClick={() => void marks.onAdd(node.id)}>
          ＋ 加标记
        </mdui-button>
        <mdui-button variant="text" className="mark-space" disabled={markDisabled} onClick={() => void marks.onSpace(node.id)}>
          均匀排列
        </mdui-button>
      </div>
      <p className="hint">{marks.block ?? APPEARANCE_HINT}</p>
    </>
  );
}

/** 选中标记时属性区的“文字”（§14.6）。 */
export function MarkSection(props: { model: OrchestrationModel; node: OrchestrationNode; marks: MarkActions }) {
  const { model, node, marks } = props;
  const owner = model.markOf.get(node.id);
  const container = owner && model.byId.get(owner.containerId);
  return (
    <>
      <h3 className="section-title">标记</h3>
      <CommitField
        key={node.id}
        label="文字"
        value={node.content?.text ?? ''}
        disabled={!!marks.block || marks.busy}
        className="mark-text"
        validate={markTextBlock}
        onCommit={(text) => void marks.onText(node.id, text)}
      />
      {container && (
        <p className="hint">
          提示组“{nodeName(model, container)}”的第 {owner.index} 个标记。上移、下移调整它在提示组里的顺序。
        </p>
      )}
      <p className="hint">{marks.block ?? APPEARANCE_HINT}</p>
    </>
  );
}

// ── 对话框 ──

/** 新建提示组（§14.4）：返回新提示组的 ID，或被拒绝的原因。 */
type CreateCountdown = () => Promise<{ containerId: string } | { error: string }>;

/** 提示组下拉框；没有提示组时换成“新建提示组”按钮。 */
function IndicatorField(props: {
  model: OrchestrationModel;
  countdowns: readonly OrchestrationNode[];
  value: string | null;
  busy: boolean;
  createBlock: string | undefined;
  onChange: (id: string) => void;
  onCreateCountdown: CreateCountdown;
  onError: (error: string | undefined) => void;
}) {
  const { model, countdowns, value } = props;
  const [created, setCreated] = useState<string | undefined>();
  const create = async () => {
    props.onError(undefined);
    const outcome = await props.onCreateCountdown();
    if ('error' in outcome) {
      props.onError(outcome.error);
      return;
    }
    setCreated(outcome.containerId);
    props.onChange(outcome.containerId);
  };
  // 刚新建的提示组要等读到新修订才在列表里。
  if (created !== undefined && !countdowns.some((n) => n.id === created)) {
    return <p className="hint lead-in-indicator-empty">正在读取新提示组…</p>;
  }
  if (countdowns.length === 0) {
    return (
      <div className="lead-in-indicator-empty">
        <span className="hint">这个来源还没有前奏提示组。</span>
        <mdui-button variant="tonal" className="countdown-new" disabled={props.busy || !!props.createBlock} title={props.createBlock} onClick={() => void create()}>
          新建提示组
        </mdui-button>
      </div>
    );
  }
  const options = countdowns.map((n) => ({ value: n.id, label: nodeName(model, n), disabled: false }));
  if (value && !countdowns.some((n) => n.id === value)) options.unshift({ value, label: `${value}（不存在）`, disabled: true });
  return <Select label="提示组" className="dialog-field lead-in-indicator" value={value ?? ''} options={options} onChange={props.onChange} />;
}

const parseSeconds = (text: string) => (/^\s*\d+(\.\d+)?\s*$/.test(text) ? Number(text) : NaN);

/** 添加前奏（§14.4）：目标句、提示组（可以新建）、时长、名称。被拒绝时原因写在对话框里，对话框不关。 */
export function AddLeadInDialog(props: {
  open: boolean;
  model: OrchestrationModel | undefined;
  program: OrchestrationProgram | undefined;
  lines: readonly EditorLine[];
  /** 预填目标句用：时间轴上选中的句子，或本句属性里点的那一句。 */
  selected: readonly string[] | undefined;
  busy: boolean;
  createBlock: string | undefined;
  onCreateCountdown: CreateCountdown;
  /** 返回后端拒绝的原因；成功时返回 undefined，由调用方关闭对话框。 */
  onCreate: (leadIn: NewLeadIn) => Promise<string | undefined>;
  onClose: () => void;
}) {
  const { model, program, lines } = props;
  const [target, setTarget] = useState<string | undefined>();
  const [indicator, setIndicator] = useState<string | null>(null);
  const [seconds, setSeconds] = useState(String(DEFAULT_LEAD_IN_SECONDS));
  const [name, setName] = useState('');
  const [error, setError] = useState<string | undefined>();
  const countdowns = model && program ? countdownsOf(model, program) : [];
  const [opened, setOpened] = useState(false);
  if (props.open !== opened) {
    setOpened(props.open);
    if (props.open) {
      setTarget(program ? defaultLeadInTarget(program, lines, props.selected) : undefined);
      setIndicator(countdowns[0]?.id ?? null);
      setSeconds(String(DEFAULT_LEAD_IN_SECONDS));
      setName('');
      setError(undefined);
    }
  }

  const value = parseSeconds(seconds);
  const secondsError = !(value > 0) ? '时长要大于 0 秒。' : undefined;
  const block = program ? newLeadInBlock(program, target, indicator ?? undefined, value) : '这个来源还没有编排程序。';
  const taken = new Set((program?.leadIns ?? []).map((l) => l.targetLineId));
  const edit =
    <T,>(set: (value: T) => void) =>
    (next: T) => {
      set(next);
      setError(undefined);
    };

  const submit = async () => {
    if (block || props.busy || !target || !indicator) return;
    setError(undefined);
    const failure = await props.onCreate({ targetLineId: target, indicatorContainerId: indicator, durationUs: fromSeconds(value), name });
    if (failure) setError(failure);
  };

  return (
    <FormDialog
      open={props.open}
      headline="添加前奏"
      submitText="添加前奏"
      submitDisabled={!!block}
      busy={props.busy}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="structure-dialog lead-in-dialog"
    >
      <Select
        label="目标句"
        className="dialog-field lead-in-target"
        value={target ?? ''}
        options={lines.map((l) => ({
          value: l.id,
          label: `${lineLabel(lines, l.id, 20)}${taken.has(l.id) ? '（已有前奏）' : l.startUs === undefined ? '（没有时间）' : ''}`,
          disabled: taken.has(l.id) || l.startUs === undefined,
        }))}
        onChange={edit(setTarget)}
      />
      {model && (
        <IndicatorField
          model={model}
          countdowns={countdowns}
          value={indicator}
          busy={props.busy}
          createBlock={props.createBlock}
          onChange={edit(setIndicator)}
          onCreateCountdown={props.onCreateCountdown}
          onError={setError}
        />
      )}
      <TextField label="时长（秒）" value={seconds} onChange={edit(setSeconds)} error={secondsError} className="dialog-field lead-in-seconds" onEnter={() => void submit()} />
      <TextField label="名称" value={name} onChange={edit(setName)} placeholder="可以不填" className="dialog-field lead-in-name" onEnter={() => void submit()} />
      <p className="hint">{block && block !== secondsError ? block : '提示组在目标句开始前这段时间里倒数。'}</p>
      {error && (
        <p className="notice-error structure-dialog-error" role="alert">
          {error}
        </p>
      )}
    </FormDialog>
  );
}

/** 自动生成（§14.5）：设置、预演的候选与诊断，只应用勾选的句子。 */
export function GenerateLeadInsDialog(props: {
  open: boolean;
  model: OrchestrationModel | undefined;
  program: OrchestrationProgram | undefined;
  lines: readonly EditorLine[];
  preview: GenerationPreviewState | undefined;
  busy: boolean;
  createBlock: string | undefined;
  onCreateCountdown: CreateCountdown;
  onPreview: (settings: LeadInGenerationSettings) => void;
  /** 返回失败原因；成功时返回 undefined，由调用方关闭对话框。 */
  onApply: (settings: LeadInGenerationSettings, selected: readonly string[], result: LeadInGenerationCommandResult) => Promise<string | undefined>;
  onClose: () => void;
}) {
  const { model, program, lines } = props;
  const [settings, setSettings] = useState<LeadInGenerationSettings | undefined>();
  const [gap, setGap] = useState('');
  const [duration, setDuration] = useState('');
  const [selection, setSelection] = useState<{ result: LeadInGenerationCommandResult; ids: readonly string[] } | undefined>();
  const [error, setError] = useState<string | undefined>();
  const [opened, setOpened] = useState(false);
  if (props.open !== opened) {
    setOpened(props.open);
    if (props.open && model && program) {
      const initial = initialGenerationSettings(model, program);
      setSettings(initial);
      setGap(String(toSeconds(initial.gapThresholdUs)));
      setDuration(String(toSeconds(initial.durationUs)));
      setSelection(undefined);
      setError(undefined);
    }
  }

  // 打开时和每次改设置后预演一次（store 只留最新一次）。
  const { onPreview } = props;
  useEffect(() => {
    if (props.open && settings && !generationSettingsBlock(settings)) onPreview(settings);
  }, [props.open, settings, onPreview]);

  if (!settings || !model) return null;
  const countdowns = program ? countdownsOf(model, program) : [];
  const change = (patch: Partial<LeadInGenerationSettings>) => {
    setSettings({ ...settings, ...patch });
    setError(undefined);
  };
  const gapValue = parseSeconds(gap);
  const durationValue = parseSeconds(duration);
  const gapError = !(gapValue >= 0) ? '阈值是不小于 0 的秒数。' : undefined;
  const durationError = !(durationValue > 0) ? '时长要大于 0 秒。' : undefined;
  // 输入框离开或回车时才改设置（每次改设置都会预演）。
  const commitGap = () => !gapError && fromSeconds(gapValue) !== settings.gapThresholdUs && change({ gapThresholdUs: fromSeconds(gapValue) });
  const commitDuration = () => !durationError && fromSeconds(durationValue) !== settings.durationUs && change({ durationUs: fromSeconds(durationValue) });

  const preview = props.preview?.settings === settings ? props.preview : undefined;
  const result = preview?.result;
  const candidates = result?.candidates ?? [];
  const chosen = result ? (selection?.result === result ? selection.ids : defaultSelection(candidates)) : [];
  const toggle = (lineId: string) => {
    if (!result) return;
    setSelection({ result, ids: chosen.includes(lineId) ? chosen.filter((id) => id !== lineId) : [...chosen, lineId] });
  };
  const settingsBlock = generationSettingsBlock(settings);
  const ready = preview?.status === 'ready' && !!result;

  const submit = async () => {
    if (!ready || chosen.length === 0 || props.busy) return;
    setError(undefined);
    const failure = await props.onApply(settings, chosen, result);
    if (failure) setError(failure);
  };

  return (
    <FormDialog
      open={props.open}
      headline="自动生成前奏"
      submitText={`应用（${chosen.length} 句）`}
      submitDisabled={!ready || chosen.length === 0 || !!gapError || !!durationError}
      busy={props.busy}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="structure-dialog generate-dialog"
    >
      <div className="generate-kinds">
        <Switch label="开头第一句" checked={settings.includeOpening} onChange={(includeOpening) => change({ includeOpening })} />
        <Switch label="长间奏之后" checked={settings.includeLongGaps} onChange={(includeLongGaps) => change({ includeLongGaps })} />
      </div>
      <div className="prop-grid generate-fields">
        <TextField
          label="间奏阈值（秒）"
          value={gap}
          error={gapError}
          className="generate-gap"
          onChange={setGap}
          onBlur={commitGap}
          onEnter={commitGap}
        />
        <TextField
          label="提示时长（秒）"
          value={duration}
          error={durationError}
          className="generate-duration"
          onChange={setDuration}
          onBlur={commitDuration}
          onEnter={commitDuration}
        />
      </div>
      <IndicatorField
        model={model}
        countdowns={countdowns}
        value={settings.indicatorContainerId}
        busy={props.busy}
        createBlock={props.createBlock}
        onChange={(indicatorContainerId) => change({ indicatorContainerId })}
        onCreateCountdown={props.onCreateCountdown}
        onError={setError}
      />
      <div className="generate-excluded">
        <span className="hint">{settings.excludedLineIds.length > 0 ? `已排除 ${settings.excludedLineIds.length} 句` : '没有排除的句子'}</span>
        <mdui-button variant="text" className="generate-clear-excluded" disabled={settings.excludedLineIds.length === 0} onClick={() => change({ excludedLineIds: [] })}>
          清除排除
        </mdui-button>
      </div>

      {settingsBlock ? (
        <p className="hint">{settingsBlock}</p>
      ) : !preview || (preview.status === 'loading' && !result) ? (
        <mdui-linear-progress />
      ) : preview.status === 'error' ? (
        <p className="notice-error" role="alert">
          {preview.error}
        </p>
      ) : (
        result && (
          <>
            <p className="generate-summary">
              {candidateSummary(candidates)}
              {preview.status === 'loading' && <span className="muted"> · 正在重新预演…</span>}
            </p>
            {result.diagnostics.map((d, i) => (
              <p key={i} className={d.severity === 'Error' ? 'notice-error generate-diagnostic' : 'hint generate-diagnostic'}>
                {d.message}
              </p>
            ))}
            <div className="generate-candidates" role="list" aria-label="候选">
              {candidates.map((c) => {
                const selectable = candidateSelectable(c);
                const from = c.existing?.durationUs;
                const to = c.proposed?.durationUs;
                return (
                  <div key={c.targetLineId} role="listitem" className={`generate-row status-${c.status}`} data-line-id={c.targetLineId}>
                    <mdui-checkbox
                      checked={selectable && chosen.includes(c.targetLineId)}
                      disabled={!selectable}
                      aria-label={`应用${lineNumberText(lines, c.targetLineId)}`}
                      onClick={() => selectable && toggle(c.targetLineId)}
                    />
                    <span className="target-label">{lineLabel(lines, c.targetLineId)}</span>
                    <span className="tag generate-status">{c.status === 'Remove' ? '移除（不再符合规则的自动前奏）' : candidateStatusText(c.status)}</span>
                    {c.reason !== 'None' && <span className="target-meta">{candidateReasonText(c.reason)}</span>}
                    {c.startUs !== undefined && c.endUs !== undefined && (
                      <span className="target-meta">
                        {formatTime(Number(c.startUs))}～{formatTime(Number(c.endUs))}
                      </span>
                    )}
                    {c.status === 'Update' && from && to && from !== to && <span className="target-meta">时长 {secondsText(from)} → {secondsText(to)}</span>}
                    {c.reason === 'Excluded' && (
                      <mdui-button
                        variant="text"
                        className="generate-unexclude"
                        onClick={() => change({ excludedLineIds: settings.excludedLineIds.filter((id) => id !== c.targetLineId) })}
                      >
                        取消排除
                      </mdui-button>
                    )}
                  </div>
                );
              })}
            </div>
          </>
        )
      )}
      <p className="hint">“移除”是不再符合规则的自动前奏，默认不勾，确认后再勾上。应用时设置一并保存。</p>
      {error && (
        <p className="notice-error structure-dialog-error" role="alert">
          {error}
        </p>
      )}
    </FormDialog>
  );
}
