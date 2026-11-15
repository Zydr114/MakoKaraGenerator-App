// 前奏提示（G23，W4 设计 §14.2～§14.6）：前奏列表、前奏属性、提示组与标记两节、添加前奏与自动生成两个对话框。

import { useEffect, useRef, useState, type ChangeEvent } from 'react';
import type {
  CountdownAnimationMode,
  CountdownAnimationSpec,
  CountdownShapeKind,
  CountdownShapeSpec,
  CountdownSvgColorMode,
  LeadInAttachmentMode,
  LeadInGenerationCommandResult,
  LeadInGenerationSettings,
  OrchestrationLeadIn,
  OrchestrationNode,
  OrchestrationProgram,
} from '../../protocol';
import type { EditorLine } from '../model/lyrics';
import { nodeName, type OrchestrationModel } from '../model/orchestration';
import {
  COUNTDOWN_ANIMATION_OPTIONS,
  COUNTDOWN_SHAPE_OPTIONS,
  COUNTDOWN_SVG_COLOR_OPTIONS,
  countdownAnimationOf,
  countdownShapeOf,
  DEFAULT_CUSTOM_SVG,
  inlineSvgBlock,
  MAX_COUNTDOWN_SVG_BYTES,
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
import { Button } from '../ui/Button';
import { Checkbox, Switch } from '../ui/choices';
import { List } from '../ui/collections';
import { CommitField, NumberField, Select, TextField } from '../ui/fields';
import { FormDialog } from '../ui/FormDialog';
import { Icon } from '../ui/Icon';
import { ProgressBar, Tag } from '../ui/indicators';
import { Group, InspectorHead, Note, Row, ToolButton } from '../workspace/panel';
import type { GenerationPreviewState, NewLeadIn, OrchestrationResult } from './orchestration-store';

const ATTACHMENT_OPTIONS: readonly { value: LeadInAttachmentMode; label: string }[] = [
  { value: 'CueTargetText', label: '跟着目标句的文字' },
  { value: 'CueTargetAboveStart', label: '放在目标句首字上方（避让注音）' },
  { value: 'FixedParent', label: '固定在父级' },
];
const APPEARANCE_HINT = '提示组的独立双态配色、描边与字体可在“外观”页选中提示组后直接设置。';

/** 提示组显示形态（图形 / SVG）与动画设置。 */
export interface PresentationActions {
  block: string | undefined;
  busy: boolean;
  onShape: (containerId: string, shape: CountdownShapeSpec, done: string) => Promise<OrchestrationResult>;
  onAnimation: (containerId: string, animation: CountdownAnimationSpec, done: string) => Promise<OrchestrationResult>;
}

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

/** 前奏列表（§14.2）：与前奏轨显示同一份数据，联动选中；上面可以添加前奏、自动生成。 */
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
    <div className="assign-list lead-in-list">
      <div className="assign-tools">
        <Button variant="text" size="sm" icon="add" isDisabled={disabled} onPress={() => actions.onAdd()}>
          添加前奏
        </Button>
        <Button variant="text" size="sm" icon="wand_stars" isDisabled={disabled} onPress={actions.onGenerate}>
          自动生成…
        </Button>
      </div>
      {actions.block && <Note>{actions.block}</Note>}
      <List
        label="前奏列表"
        items={props.leadIns}
        textValue={(leadIn) => lineLabel(props.lines, leadIn.targetLineId)}
        selectedKeys={props.selectedId ? [props.selectedId] : []}
        onSelectionChange={(keys) => {
          const [key] = keys;
          if (key !== undefined && key !== props.selectedId) props.onSelect(String(key));
        }}
        rowClassName={(leadIn) => (leadIn.enabled ? undefined : 'is-off')}
        emptyText="这个来源还没有前奏提示。点“添加前奏”给某一句加，或用“自动生成…”按开头和长间奏一次生成。"
      >
        {(leadIn) => (
          <span className="clip lead-in" data-lead-in-id={leadIn.id}>
            <Icon name="pending" />
            <span className="clip-text">
              <span className="clip-name">{lineLabel(props.lines, leadIn.targetLineId)}</span>
              <span className="faint">
                {[timeText(leadIn), `▸ ${nameOf(model, leadIn.indicatorContainerId)}`, leadIn.name ? `“${leadIn.name}”` : '', originText(leadIn.origin), leadIn.enabled ? '' : '已停用']
                  .filter(Boolean)
                  .join(' · ')}
              </span>
            </span>
            <span className="faint tnum">{secondsText(leadIn.durationUs)}</span>
          </span>
        )}
      </List>
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
  const disabled = !!actions.block || actions.busy;
  const update = (patch: LeadInPatch, done: string) => void actions.onUpdate(leadIn, patch, done);
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
    <div className="insp lead-in-inspector">
      <InspectorHead crumbs={[props.branchName, '前奏提示']} title={lineNumberText(lines, leadIn.targetLineId)} kind="前奏提示" id={leadIn.id} />
      <Group title="前奏">
        <CommitField
          key={leadIn.id}
          label="名称"
          labelPlacement="inside"
          value={leadIn.name ?? ''}
          placeholder="不命名"
          disabled={disabled}
          onCommit={(name) => update({ name }, name.trim() ? `前奏已改名为“${name.trim()}”` : '已清除前奏名称')}
        />
        <Select
          label="目标句"
          labelPlacement="inside"
          value={leadIn.targetLineId}
          options={targetOptions}
          disabled={disabled}
          onChange={(targetLineId) => update({ targetLineId }, `前奏已改到${lineNumberText(lines, targetLineId)}`)}
        />
        <Select
          label="提示组"
          labelPlacement="inside"
          value={leadIn.indicatorContainerId}
          options={indicatorOptions}
          disabled={disabled || indicatorOptions.length < 2}
          onChange={(indicatorContainerId) => update({ indicatorContainerId }, `前奏改用“${nameOf(model, indicatorContainerId)}”提示`)}
        />
        <div className="g2">
          <NumberField
            label="时长"
            labelPlacement="inside"
            suffix="秒"
            value={toSeconds(leadIn.durationUs)}
            greaterThan={0}
            disabled={disabled}
            onCommit={(seconds) => update({ durationUs: fromSeconds(seconds) }, '已调整前奏')}
          />
          <Switch label="启用" checked={leadIn.enabled} disabled={disabled} onChange={(enabled) => update({ enabled }, enabled ? '已启用前奏' : '已停用前奏')} />
        </div>
        <dl className="info-list">
          <Row label="时间" value={leadInSpan(leadIn) ? timeText(leadIn) : '没有时间（目标句没有时间，或起唱前不够一个时长）'} />
          <Row label="来源" value={originText(leadIn.origin)} />
        </dl>
        {leadIn.origin === 'Generated' && <Note>自动生成的前奏：改动后变为手工，自动生成不会再改它。</Note>}
      </Group>
      <AttachmentField
        model={model}
        branchId={program.branchId}
        indicator={indicator}
        leadIn={leadIn}
        block={actions.block ?? props.attachmentBlock}
        busy={actions.busy}
        onAttachment={actions.onAttachment}
        onShow={indicator ? () => actions.onSelectNode(indicator.id) : undefined}
      />
      <footer className="insp-foot">
        <div className="insp-actions">
          <Button danger size="sm" icon="delete" isDisabled={disabled} onPress={() => void actions.onDelete(leadIn, false)}>
            删除前奏
          </Button>
          {leadIn.origin === 'Generated' && (
            <Button danger size="sm" isDisabled={disabled} onPress={() => void actions.onDelete(leadIn, true)}>
              删除并不再自动生成
            </Button>
          )}
        </div>
        <Note>{actions.block ?? '删除前奏不会删除提示组。改动都可以撤销。'}</Note>
      </footer>
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
  /** 在结构树里选中提示组（前奏属性里给出）。 */
  onShow?: (() => void) | undefined;
}) {
  const { model, indicator, leadIn } = props;
  if (!indicator) return null;
  const mode = (indicator.content?.attachmentMode ?? 'CueTargetText') as LeadInAttachmentMode;
  const users = leadInsUsing(model, indicator.id).length;
  const options = ATTACHMENT_OPTIONS.some((o) => o.value === mode) ? ATTACHMENT_OPTIONS : [...ATTACHMENT_OPTIONS, { value: mode, label: mode }];
  return (
    <Group
      title="附着方式"
      extra={
        props.onShow && (
          <Button variant="text" size="sm" onPress={props.onShow}>
            查看提示组
          </Button>
        )
      }
    >
      <Select
        label="提示组放在哪里"
        labelPlacement="inside"
        value={mode}
        options={options}
        disabled={!!props.block || props.busy || !leadIn}
        onChange={(next) => {
          if (leadIn && props.branchId) void props.onAttachment(props.branchId, leadIn, next);
        }}
      />
      <Note>
        {!leadIn
          ? '还没有前奏用这个提示组，添加前奏后才能切换附着方式。'
          : `附着方式属于提示组“${nodeName(model, indicator)}”，用它的 ${users} 个前奏都跟着变。切换时按目标句文字的真实位置换算，画面上不动。`}
      </Note>
    </Group>
  );
}

// ── 提示组与标记 ──

function CountdownPresentationSection(props: { node: OrchestrationNode; actions: PresentationActions }) {
  const { node, actions } = props;
  const shape = countdownShapeOf(node.content);
  const animation = countdownAnimationOf(node.content);
  const disabled = !!actions.block || actions.busy;
  const fileRef = useRef<HTMLInputElement>(null);
  const [svgDraft, setSvgDraft] = useState(shape.svgXml ?? DEFAULT_CUSTOM_SVG);
  const [svgUploadError, setSvgUploadError] = useState<string | undefined>();
  const [trackedNodeId, setTrackedNodeId] = useState(node.id);
  const [trackedSvgXml, setTrackedSvgXml] = useState(shape.svgXml);
  if (node.id !== trackedNodeId || shape.svgXml !== trackedSvgXml) {
    setTrackedNodeId(node.id);
    setTrackedSvgXml(shape.svgXml);
    setSvgDraft(shape.svgXml ?? DEFAULT_CUSTOM_SVG);
    setSvgUploadError(undefined);
  }
  const svgError = shape.kind === 'CustomSvg' ? inlineSvgBlock(svgDraft) : undefined;
  const shapeOptions = COUNTDOWN_SHAPE_OPTIONS.some((o) => o.value === shape.kind)
    ? COUNTDOWN_SHAPE_OPTIONS
    : [...COUNTDOWN_SHAPE_OPTIONS, { value: shape.kind, label: shape.kind, note: '' }];
  const animOptions = COUNTDOWN_ANIMATION_OPTIONS.some((o) => o.value === animation.mode)
    ? COUNTDOWN_ANIMATION_OPTIONS
    : [...COUNTDOWN_ANIMATION_OPTIONS, { value: animation.mode, label: animation.mode, note: '' }];
  const shapeNote = COUNTDOWN_SHAPE_OPTIONS.find((o) => o.value === shape.kind)?.note;
  const animNote = COUNTDOWN_ANIMATION_OPTIONS.find((o) => o.value === animation.mode)?.note;

  const commitShapeKind = (kind: CountdownShapeKind) => {
    if (kind === shape.kind) return;
    if (kind === 'CustomSvg') {
      const candidate = inlineSvgBlock(svgDraft) ? DEFAULT_CUSTOM_SVG : svgDraft.trim();
      setSvgDraft(candidate);
      setSvgUploadError(undefined);
      void actions.onShape(node.id, { ...shape, kind: 'CustomSvg', svgXml: candidate }, '提示组已切换为自定义 SVG');
    } else {
      const label = COUNTDOWN_SHAPE_OPTIONS.find((o) => o.value === kind)?.label ?? kind;
      void actions.onShape(node.id, { ...shape, kind, svgXml: null }, `提示组图形已改为“${label}”`);
    }
  };

  const commitSvgFile = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) return;
    if (file.size > MAX_COUNTDOWN_SVG_BYTES) {
      setSvgUploadError('所选 SVG 文件超过 64 KiB 限制。');
      return;
    }
    const text = (await file.text()).trim();
    const issue = inlineSvgBlock(text);
    setSvgDraft(text);
    if (issue) {
      setSvgUploadError(issue);
      return;
    }
    setSvgUploadError(undefined);
    void actions.onShape(node.id, { ...shape, kind: 'CustomSvg', svgXml: text }, `已导入 SVG“${file.name}”`);
  };

  const commitAnimationMode = (mode: CountdownAnimationMode) => {
    if (mode === animation.mode) return;
    const blinkCount = mode !== 'DiscreteSteps' && animation.blinkRatio > 0 && animation.blinkCount < 1 ? 2 : animation.blinkCount;
    const label = COUNTDOWN_ANIMATION_OPTIONS.find((o) => o.value === mode)?.label ?? mode;
    void actions.onAnimation(node.id, { ...animation, mode, blinkCount }, `提示组动画已改为“${label}”`);
  };

  const applyBlinkRatio = (blinkRatio: number) => {
    const blinkCount = animation.mode !== 'DiscreteSteps' && blinkRatio > 0 && animation.blinkCount < 1 ? 2 : animation.blinkCount;
    void actions.onAnimation(node.id, { ...animation, blinkRatio, blinkCount }, '已更新闪烁时长占比');
  };

  return (
    <Group title="图形与动画">
      <Select<CountdownShapeKind>
        label="标记图形"
        labelPlacement="inside"
        value={shape.kind}
        options={shapeOptions}
        disabled={disabled}
        onChange={commitShapeKind}
      />
      {shape.kind === 'SteppedBars' && (
        <NumberField
          key={`${node.id}-barMin`}
          label="最矮竖条比例"
          labelPlacement="inside"
          min={0.15}
          max={1}
          value={shape.barMinHeightRatio}
          disabled={disabled}
          onCommit={(barMinHeightRatio) =>
            void actions.onShape(node.id, { ...shape, kind: 'SteppedBars', svgXml: null, barMinHeightRatio }, '已更新阶梯竖条高度比例')
          }
        />
      )}
      {shape.kind === 'CustomSvg' && (
        <>
          <Select<CountdownSvgColorMode>
            label="SVG 配色"
            labelPlacement="inside"
            value={shape.svgColorMode}
            options={COUNTDOWN_SVG_COLOR_OPTIONS}
            disabled={disabled}
            onChange={(svgColorMode) => {
              if (!svgError) void actions.onShape(node.id, { ...shape, kind: 'CustomSvg', svgXml: svgDraft.trim(), svgColorMode }, '已更新 SVG 配色模式');
            }}
          />
          <div className="countdown-svg-actions">
            <input ref={fileRef} type="file" accept=".svg,image/svg+xml" hidden onChange={(e) => void commitSvgFile(e)} />
            <Button variant="tonal" size="sm" icon="upload_file" isDisabled={disabled} onPress={() => fileRef.current?.click()}>
              选择 SVG 文件…
            </Button>
            <Button
              variant="tonal"
              size="sm"
              isDisabled={disabled || !!svgError || svgDraft.trim() === (shape.svgXml ?? '').trim()}
              onPress={() => {
                setSvgUploadError(undefined);
                void actions.onShape(node.id, { ...shape, kind: 'CustomSvg', svgXml: svgDraft.trim() }, '已应用自定义 SVG');
              }}
            >
              应用 SVG
            </Button>
          </div>
          <TextField
            label="内联 SVG（不超过 64 KiB）"
            className="countdown-svg-editor"
            rows={5}
            value={svgDraft}
            disabled={disabled}
            error={svgUploadError ?? svgError}
            onChange={(text) => {
              setSvgUploadError(undefined);
              setSvgDraft(text);
            }}
          />
        </>
      )}
      <Select<CountdownAnimationMode>
        label="动画模式"
        labelPlacement="inside"
        value={animation.mode}
        options={animOptions}
        disabled={disabled}
        onChange={commitAnimationMode}
      />
      {animation.mode !== 'DiscreteSteps' && (
        <div className="g2">
          <NumberField
            key={`${node.id}-blinkRatio`}
            label="闪烁阶段占比"
            labelPlacement="inside"
            min={0}
            max={0.85}
            value={animation.blinkRatio}
            disabled={disabled}
            onCommit={applyBlinkRatio}
          />
          <NumberField
            key={`${node.id}-blinkCount`}
            label="闪烁次数"
            labelPlacement="inside"
            integer
            min={animation.blinkRatio > 0 ? 1 : 0}
            max={16}
            value={animation.blinkCount}
            disabled={disabled || animation.blinkRatio === 0}
            onCommit={(blinkCount) => void actions.onAnimation(node.id, { ...animation, blinkCount }, '已更新闪烁次数')}
          />
          <NumberField
            key={`${node.id}-feather`}
            label="擦除羽化"
            labelPlacement="inside"
            min={0}
            max={0.5}
            value={animation.featherRatio}
            disabled={disabled}
            onCommit={(featherRatio) => void actions.onAnimation(node.id, { ...animation, featherRatio }, '已更新擦除羽化')}
          />
        </div>
      )}
      <Note>{actions.block ?? [shapeNote, animNote].filter(Boolean).join(' ')}</Note>
    </Group>
  );
}

/** 选中提示组时属性区的各节（§14.6）：用它的前奏、附着方式、图形与动画、标记列表。 */
export function CountdownSections(props: {
  model: OrchestrationModel;
  node: OrchestrationNode;
  lines: (branchId: string) => readonly EditorLine[];
  branchName: (branchId: string) => string;
  actions: LeadInActions;
  attachmentBlock: string | undefined;
  presentation: PresentationActions;
  marks: MarkActions;
  onSelectNode: (id: string) => void;
}) {
  const { model, node, actions, marks } = props;
  const users = leadInsUsing(model, node.id);
  const first = users[0];
  const markIds = node.content?.markNodeIds ?? [];
  const markDisabled = !!marks.block || marks.busy;
  const shapeKind = countdownShapeOf(node.content).kind;
  return (
    <>
      <Group title={`前奏 · ${users.length} 个`}>
        {users.length === 0 ? (
          <Note>还没有前奏用这个提示组。</Note>
        ) : (
          <div className="slots countdown-lead-ins" role="list" aria-label="用这个提示组的前奏">
            {users.map(({ program, leadIn }) => (
              <div key={leadIn.id} role="listitem" className="slot" data-lead-in-id={leadIn.id}>
                <button type="button" className="slot-name" onClick={() => actions.onSelect(program.branchId, leadIn.id)} title="选中这个前奏">
                  {props.branchName(program.branchId)} · {lineLabel(props.lines(program.branchId), leadIn.targetLineId)} · {secondsText(leadIn.durationUs)}
                  {leadIn.enabled ? '' : '（已停用）'}
                </button>
              </div>
            ))}
          </div>
        )}
      </Group>
      <AttachmentField
        model={model}
        branchId={first?.program.branchId}
        indicator={node}
        leadIn={first?.leadIn}
        block={props.attachmentBlock}
        busy={actions.busy}
        onAttachment={actions.onAttachment}
      />
      <CountdownPresentationSection node={node} actions={props.presentation} />
      <Group title={`标记 · ${markIds.length} 个`}>
        <div className="slots mark-list" role="list" aria-label="标记">
          {markIds.map((id, i) => {
            const mark = model.byId.get(id);
            const blocks = marks.blocks(id);
            const own = (reason: string | undefined) => marks.structureBlock ?? (mark ? reason : '这个标记不存在。');
            return (
              <div key={id} role="listitem" className="slot mark-row" data-mark-id={id}>
                <CommitField
                  label={`标记 ${i + 1}`}
                  labelPlacement="inside"
                  size="sm"
                  value={mark?.content?.text ?? ''}
                  disabled={markDisabled || !mark}
                  validate={markTextBlock}
                  onCommit={(text) => void marks.onText(id, text)}
                />
                <ToolButton icon="arrow_upward" label={`上移标记 ${i + 1}`} reason={own(blocks.up)} disabled={marks.busy} onPress={() => void marks.onReorder(id, -1)} />
                <ToolButton icon="arrow_downward" label={`下移标记 ${i + 1}`} reason={own(blocks.down)} disabled={marks.busy} onPress={() => void marks.onReorder(id, 1)} />
                <ToolButton icon="delete" label={`删除标记 ${i + 1}`} reason={own(blocks.remove)} disabled={marks.busy} onPress={() => void marks.onRemove(id)} />
              </div>
            );
          })}
        </div>
        <div className="row-actions">
          <Button variant="text" size="sm" icon="add" isDisabled={markDisabled || markIds.length >= MAX_MARKS} onPress={() => void marks.onAdd(node.id)}>
            加标记
          </Button>
          <Button variant="text" size="sm" isDisabled={markDisabled} onPress={() => void marks.onSpace(node.id)}>
            均匀排列
          </Button>
        </div>
        <Note>
          {marks.block ??
            `${shapeKind !== 'Text' ? '当前提示组使用几何/SVG 图形，标记数与布局间距仍按以下节点驱动。' : ''}${
              markIds.length >= MAX_MARKS ? `最多 ${MAX_MARKS} 个标记。` : ''
            }${APPEARANCE_HINT}`}
        </Note>
      </Group>
    </>
  );
}

/** 选中标记时属性区的“文字”（§14.6）。 */
export function MarkSection(props: { model: OrchestrationModel; node: OrchestrationNode; marks: MarkActions }) {
  const { model, node, marks } = props;
  const owner = model.markOf.get(node.id);
  const container = owner && model.byId.get(owner.containerId);
  return (
    <Group title="标记">
      <CommitField
        key={node.id}
        label="文字"
        labelPlacement="inside"
        value={node.content?.text ?? ''}
        disabled={!!marks.block || marks.busy}
        validate={markTextBlock}
        onCommit={(text) => void marks.onText(node.id, text)}
      />
      {container && (
        <Note>
          提示组“{nodeName(model, container)}”的第 {owner.index} 个标记。上移、下移调整它在提示组里的顺序。
        </Note>
      )}
      <Note>
        {marks.block ??
          `${container && countdownShapeOf(container.content).kind !== 'Text' ? '所属提示组当前使用几何/SVG 图形，单标记文本仅在切回文字标记时显示。' : ''}${APPEARANCE_HINT}`}
      </Note>
    </Group>
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
    return <Note className="lead-in-indicator-empty">正在读取新提示组…</Note>;
  }
  if (countdowns.length === 0) {
    return (
      <div className="lead-in-indicator-empty">
        <Note>这个来源还没有前奏提示组。{props.createBlock}</Note>
        <Button variant="tonal" size="sm" icon="add" isDisabled={props.busy || !!props.createBlock} onPress={() => void create()}>
          新建提示组
        </Button>
      </div>
    );
  }
  const options = countdowns.map((n) => ({ value: n.id, label: nodeName(model, n), disabled: false }));
  if (value && !countdowns.some((n) => n.id === value)) options.unshift({ value, label: `${value}（不存在）`, disabled: true });
  return <Select label="提示组" value={value ?? ''} options={options} onChange={props.onChange} />;
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
  const [autoCreated, setAutoCreated] = useState(false);
  const openRef = useRef(props.open);
  openRef.current = props.open;
  if (props.open !== opened) {
    setOpened(props.open);
    setAutoCreated(false);
    if (props.open) {
      setTarget(program ? defaultLeadInTarget(program, lines, props.selected) : undefined);
      setIndicator(countdowns[0]?.id ?? null);
      setSeconds(String(DEFAULT_LEAD_IN_SECONDS));
      setName('');
      setError(undefined);
    }
  }
  const { onCreateCountdown } = props;
  useEffect(() => {
    if (!props.open || !model || !program || countdowns.length > 0 || props.createBlock || props.busy || autoCreated) return;
    setAutoCreated(true);
    void onCreateCountdown().then((outcome) => {
      if (!openRef.current) return;
      if ('containerId' in outcome) setIndicator(outcome.containerId);
      else setError(outcome.error);
    });
  }, [props.open, model, program, countdowns.length, props.createBlock, props.busy, autoCreated, onCreateCountdown]);

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
      className="orch-dialog lead-in-dialog"
    >
      <Select
        label="目标句"
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
      <div className="g2">
        <TextField label="时长" suffix="秒" value={seconds} onChange={edit(setSeconds)} error={secondsError} onEnter={() => void submit()} />
        <TextField label="名称" value={name} onChange={edit(setName)} placeholder="可以不填" onEnter={() => void submit()} />
      </div>
      <Note>{block && block !== secondsError ? block : '提示组在目标句开始前这段时间里倒数。'}</Note>
      {error && <Note error>{error}</Note>}
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
  const [autoCreated, setAutoCreated] = useState(false);
  const openRef = useRef(props.open);
  openRef.current = props.open;
  if (props.open !== opened) {
    setOpened(props.open);
    setAutoCreated(false);
    if (props.open && model && program) {
      const initial = initialGenerationSettings(model, program);
      setSettings(initial);
      setGap(String(toSeconds(initial.gapThresholdUs)));
      setDuration(String(toSeconds(initial.durationUs)));
      setSelection(undefined);
      setError(undefined);
    }
  }

  // 打开时若没有任何提示组，自动以现代默认图形与动画创建一个并选上；同时每次改设置后预演一次。
  const countdownCount = model && program ? countdownsOf(model, program).length : 0;
  const { onCreateCountdown } = props;
  useEffect(() => {
    if (!props.open || !model || !program || countdownCount > 0 || props.createBlock || props.busy || autoCreated) return;
    setAutoCreated(true);
    void onCreateCountdown().then((outcome) => {
      if (!openRef.current) return;
      if ('containerId' in outcome) {
        setSettings((prev) => (prev ? { ...prev, indicatorContainerId: outcome.containerId } : prev));
      } else {
        setError(outcome.error);
      }
    });
  }, [props.open, model, program, countdownCount, props.createBlock, props.busy, autoCreated, onCreateCountdown]);

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
      className="orch-dialog generate-dialog"
      footerNote="“移除”是不再符合规则的自动前奏，默认不勾，确认后再勾上。应用时设置一并保存。"
    >
      <div className="generate-kinds">
        <Switch label="开头第一句" checked={settings.includeOpening} onChange={(includeOpening) => change({ includeOpening })} />
        <Switch label="长间奏之后" checked={settings.includeLongGaps} onChange={(includeLongGaps) => change({ includeLongGaps })} />
      </div>
      <div className="g2">
        <TextField label="间奏阈值" suffix="秒" value={gap} error={gapError} onChange={setGap} onBlur={commitGap} onEnter={commitGap} />
        <TextField label="提示时长" suffix="秒" value={duration} error={durationError} onChange={setDuration} onBlur={commitDuration} onEnter={commitDuration} />
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
        <span>{settings.excludedLineIds.length > 0 ? `已排除 ${settings.excludedLineIds.length} 句` : '没有排除的句子'}</span>
        <Button variant="text" size="sm" isDisabled={settings.excludedLineIds.length === 0} onPress={() => change({ excludedLineIds: [] })}>
          清除排除
        </Button>
      </div>

      {settingsBlock ? (
        <Note>{settingsBlock}</Note>
      ) : !preview || (preview.status === 'loading' && !result) ? (
        <ProgressBar label="正在预演" />
      ) : preview.status === 'error' ? (
        <Note error>{preview.error}</Note>
      ) : (
        result && (
          <div className="generate-result">
            <p className="generate-summary">
              {candidateSummary(candidates)}
              {preview.status === 'loading' && <span className="faint"> · 正在重新预演…</span>}
            </p>
            {result.diagnostics.map((d, i) => (
              <Note key={i} error={d.severity === 'Error'} className="generate-diagnostic">
                {d.message}
              </Note>
            ))}
            <div className="generate-candidates" role="list" aria-label="候选">
              {candidates.map((c) => {
                const selectable = candidateSelectable(c);
                const from = c.existing?.durationUs;
                const to = c.proposed?.durationUs;
                const facts = [
                  c.reason !== 'None' ? candidateReasonText(c.reason) : '',
                  c.startUs !== undefined && c.endUs !== undefined ? `${formatTime(Number(c.startUs))}～${formatTime(Number(c.endUs))}` : '',
                  c.status === 'Update' && from && to && from !== to ? `时长 ${secondsText(from)} → ${secondsText(to)}` : '',
                ].filter(Boolean);
                return (
                  <div key={c.targetLineId} role="listitem" className={`generate-row status-${c.status}`} data-line-id={c.targetLineId}>
                    <Checkbox label={lineLabel(lines, c.targetLineId)} checked={selectable && chosen.includes(c.targetLineId)} disabled={!selectable} onChange={() => toggle(c.targetLineId)} />
                    <Tag tone={c.status === 'Remove' ? 'warn' : c.status === 'Create' ? 'accent' : 'neutral'} className="generate-status">
                      {c.status === 'Remove' ? '移除（不再符合规则的自动前奏）' : candidateStatusText(c.status)}
                    </Tag>
                    {facts.length > 0 && <span className="faint generate-facts">{facts.join(' · ')}</span>}
                    {c.reason === 'Excluded' && (
                      <Button variant="text" size="sm" onPress={() => change({ excludedLineIds: settings.excludedLineIds.filter((id) => id !== c.targetLineId) })}>
                        取消排除
                      </Button>
                    )}
                  </div>
                );
              })}
            </div>
          </div>
        )
      )}
      {error && <Note error>{error}</Note>}
    </FormDialog>
  );
}
