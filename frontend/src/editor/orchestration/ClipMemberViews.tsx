// 片段的调整句子、拆分与合并（W4 设计 §15）：片段属性区“句子”一节的操作，调整句子与拆分两个对话框。

import { useState } from 'react';
import type { OrchestrationClip, OrchestrationProgram } from '../../protocol';
import type { EditorLine } from '../model/lyrics';
import { clipRangeBlock, lineRange, lineRangeText, linesBetween, type OrchestrationModel } from '../model/orchestration';
import { defaultSplitPoint, mergeNeighbour, scheduleContextNote, splitBlock, splitPoints, splitResultText, type MergeSide } from '../model/orchestration-clip-members';
import { Button } from '../ui/Button';
import { Select } from '../ui/fields';
import { FormDialog } from '../ui/FormDialog';
import { ClipRangeFields, parseLineNumber, rangeInvalidText } from './NewClipDialog';
import { Note } from './parts';

/** 调整句子、拆分、合并：不可用时 block 是原因。 */
export interface ClipMemberActions {
  block: string | undefined;
  busy: boolean;
  onAdjust: (clip: OrchestrationClip) => void;
  onSplit: (clip: OrchestrationClip) => void;
  onMerge: (clip: OrchestrationClip, label: string, other: OrchestrationClip, otherLabel: string) => Promise<void>;
}

const MERGE_TEXT: Record<MergeSide, string> = { previous: '与前一个片段合并', next: '与后一个片段合并' };

/** 片段属性区“句子”一节下面的三个操作（§15.2）：不能做时按钮停用，原因写在悬停说明里，也写在下面。 */
export function ClipMemberButtons(props: {
  model: OrchestrationModel;
  program: OrchestrationProgram;
  lines: readonly EditorLine[];
  clip: OrchestrationClip;
  label: string;
  actions: ClipMemberActions;
}) {
  const { clip, actions } = props;
  const disabled = !!actions.block || actions.busy;
  const split = splitBlock(clip);
  const sides = (['previous', 'next'] as const).map((side) => ({ side, merge: mergeNeighbour(props.model, props.program, props.lines, clip, side) }));
  const reasons = actions.block
    ? [actions.block]
    : [...(split ? [`拆分：${split}`] : []), ...sides.flatMap(({ side, merge }) => (merge.block ? [`${MERGE_TEXT[side]}：${merge.block}`] : []))];
  return (
    <>
      <div className="row-actions clip-member-actions">
        <Button size="sm" isDisabled={disabled} onPress={() => actions.onAdjust(clip)}>
          调整句子…
        </Button>
        <Button size="sm" isDisabled={disabled || !!split} onPress={() => actions.onSplit(clip)}>
          拆分…
        </Button>
        {sides.map(({ side, merge }) => (
          <Button key={side} size="sm" isDisabled={disabled || !!merge.block} onPress={() => merge.clip && void actions.onMerge(clip, props.label, merge.clip, merge.label)}>
            {MERGE_TEXT[side]}
          </Button>
        ))}
      </div>
      {reasons.length > 0 && (
        <ul className="note clip-member-reasons">
          {reasons.map((reason) => (
            <li key={reason}>{reason}</li>
          ))}
        </ul>
      )}
    </>
  );
}

/**
 * 调整片段的句子（§15.3）：预填片段现在的范围，提交前就地写明不能调整的原因；
 * 后端拒绝时原因也写在对话框里，对话框不关闭。clip 为空时关闭。
 */
export function ClipMembersDialog(props: {
  clip: OrchestrationClip | undefined;
  label: string;
  program: OrchestrationProgram | undefined;
  lines: readonly EditorLine[];
  busy: boolean;
  /** 返回后端拒绝的原因；成功时返回 undefined，由调用方关闭对话框。 */
  onApply: (clip: OrchestrationClip, lineIds: string[], rangeText: string) => Promise<string | undefined>;
  onClose: () => void;
}) {
  const { clip, program, lines } = props;
  const [first, setFirst] = useState('1');
  const [last, setLast] = useState('1');
  const [error, setError] = useState<string | undefined>();
  // 每次打开时预填片段现在的范围；在渲染时调整，不留一帧旧状态。
  const [opened, setOpened] = useState<string | undefined>();
  if (clip?.id !== opened) {
    setOpened(clip?.id);
    if (clip) {
      const range = lineRange(lines, clip.lineIds);
      setFirst(String(range?.first ?? 1));
      setLast(String(range?.last ?? 1));
      setError(undefined);
    }
  }

  const a = parseLineNumber(first);
  const b = parseLineNumber(last);
  const lineIds = linesBetween(lines, a, b);
  const block = !clip || !program ? '这个片段已经不在了。' : !lineIds ? rangeInvalidText(lines.length) : clipRangeBlock(program, lines, lineIds, clip);
  const note = clip && scheduleContextNote(clip, lines);
  const edit = (set: (value: string) => void) => (value: string) => {
    set(value);
    setError(undefined);
  };

  const submit = async () => {
    if (block || !clip || !lineIds || props.busy) return;
    setError(undefined);
    const failure = await props.onApply(clip, lineIds, lineRangeText(lineRange(lines, lineIds)!));
    if (failure) setError(failure);
  };

  return (
    <FormDialog
      open={!!clip}
      headline={`调整片段的句子 · ${props.label}`}
      submitText="调整"
      submitDisabled={!!block}
      busy={props.busy}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="orch-dialog clip-members"
    >
      <ClipRangeFields first={first} last={last} lines={lines} onFirst={edit(setFirst)} onLast={edit(setLast)} />
      {note && <Note className="clip-members-context">{note}</Note>}
      {block && (
        <Note error className="clip-members-block">
          {block}
        </Note>
      )}
      <Note>片段的名称和目标容器不变。改动可以撤销。</Note>
      {error && (
        <Note error className="clip-members-error">
          {error}
        </Note>
      )}
    </FormDialog>
  );
}

/** 拆分片段（§15.4）：选从哪一句开始拆出，下面写明结果。后端拒绝时原因写在对话框里。clip 为空时关闭。 */
export function SplitClipDialog(props: {
  clip: OrchestrationClip | undefined;
  label: string;
  lines: readonly EditorLine[];
  /** 打开时的播放头：在片段里、不是第一句时预填它所在的句子。 */
  playheadUs: number | undefined;
  busy: boolean;
  onSplit: (clip: OrchestrationClip, splitLineId: string) => Promise<string | undefined>;
  onClose: () => void;
}) {
  const { clip, lines } = props;
  const [lineId, setLineId] = useState('');
  const [error, setError] = useState<string | undefined>();
  const [opened, setOpened] = useState<string | undefined>();
  if (clip?.id !== opened) {
    setOpened(clip?.id);
    if (clip) {
      setLineId(defaultSplitPoint(clip, lines, props.playheadUs) ?? '');
      setError(undefined);
    }
  }

  const points = clip ? splitPoints(clip, lines) : [];
  const result = clip && lineId ? splitResultText(clip, props.label, lines, lineId) : undefined;
  const block = !clip ? '这个片段已经不在了。' : (splitBlock(clip) ?? (result ? undefined : '请选择从哪一句开始拆出。'));

  const submit = async () => {
    if (block || !clip || props.busy) return;
    setError(undefined);
    const failure = await props.onSplit(clip, lineId);
    if (failure) setError(failure);
  };

  return (
    <FormDialog
      open={!!clip}
      headline={`拆分片段 · ${props.label}`}
      submitText="拆分"
      submitDisabled={!!block}
      busy={props.busy}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="orch-dialog split-clip"
    >
      <Select
        label="从哪一句开始拆出"
        value={lineId}
        options={points.map((p) => ({ value: p.lineId, label: p.label }))}
        onChange={(value) => {
          setLineId(value);
          setError(undefined);
        }}
      />
      {result && <Note className="split-clip-result">{result}</Note>}
      {block && (
        <Note error className="split-clip-block">
          {block}
        </Note>
      )}
      {error && (
        <Note error className="split-clip-error">
          {error}
        </Note>
      )}
    </FormDialog>
  );
}
