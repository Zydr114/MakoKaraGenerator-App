import { useState } from 'react';
import type { OrchestrationProgram } from '../../protocol';
import type { EditorLine } from '../model/lyrics';
import { clipCreateBlock, containersOf, defaultClipContainer, linesBetween, nodeName, roleText, type LineRange, type OrchestrationModel } from '../model/orchestration';
import { formatTime } from '../model/time';
import { Select, TextField } from '../ui/fields';
import { FormDialog } from '../ui/FormDialog';
import { Note } from '../workspace/panel';
import type { NewClip } from './orchestration-store';

/** 句号输入：正整数，否则 NaN（linesBetween 判为无效）。 */
export const parseLineNumber = (text: string) => (/^\s*\d+\s*$/.test(text) ? Number(text) : NaN);

/** 句号范围无效时的原因。 */
export const rangeInvalidText = (count: number) => `范围无效：请填 1～${count} 之间的句号，起句不晚于止句。`;

/** 片段的范围：“从第 [a] 句到第 [b] 句”，下面写句数、时间、首尾句文字。新建片段与调整句子（§15.3）共用。 */
export function ClipRangeFields(props: { first: string; last: string; lines: readonly EditorLine[]; onFirst: (value: string) => void; onLast: (value: string) => void }) {
  const { lines } = props;
  const a = parseLineNumber(props.first);
  const b = parseLineNumber(props.last);
  const valid = linesBetween(lines, a, b) !== undefined;
  const firstLine = valid ? lines[a - 1] : undefined;
  const lastLine = valid ? lines[b - 1] : undefined;
  return (
    <>
      <div className="clip-range">
        <TextField label="从第" labelPlacement="side" suffix="句" value={props.first} onChange={props.onFirst} ariaLabel="起句" />
        <TextField label="到第" labelPlacement="side" suffix="句" value={props.last} onChange={props.onLast} ariaLabel="止句" />
        <span className="faint">共 {lines.length} 句</span>
      </div>
      {firstLine && lastLine && (
        <Note className="clip-range-lines">
          {b - a + 1} 句
          {firstLine.startUs !== undefined && lastLine.endUs !== undefined ? ` · ${formatTime(firstLine.startUs)}～${formatTime(lastLine.endUs)}` : ''} · 「{firstLine.text}」
          {b > a ? `～「${lastLine.text}」` : ''}
        </Note>
      )}
    </>
  );
}

/**
 * 新建片段（G19-B，W4 设计 §11.2）：连续几句、目标容器、可选名称。提交前就地写明不能创建的原因；
 * 后端拒绝时原因也写在对话框里，对话框不关闭。
 */
export function NewClipDialog(props: {
  open: boolean;
  branchName: string;
  model: OrchestrationModel | undefined;
  program: OrchestrationProgram | undefined;
  lines: readonly EditorLine[];
  /** 时间轴上选中的句子：预填范围。 */
  range: LineRange | undefined;
  busy: boolean;
  /** 返回后端拒绝的原因；成功时返回 undefined，由调用方关闭对话框。 */
  onCreate: (clip: NewClip, range: { first: number; last: number }) => Promise<string | undefined>;
  onClose: () => void;
}) {
  const { model, program, lines } = props;
  const [first, setFirst] = useState('1');
  const [last, setLast] = useState('1');
  const [containerId, setContainerId] = useState('');
  const [name, setName] = useState('');
  const [error, setError] = useState<string | undefined>();
  // 每次打开时重置：有选中的句子就预填它们（不连续时取首尾），目标选第一个不是默认容器的容器。在渲染时调整，不留一帧旧状态。
  const [opened, setOpened] = useState(false);
  if (props.open !== opened) {
    setOpened(props.open);
    if (props.open) {
      setFirst(String(props.range?.first ?? 1));
      setLast(String(props.range?.last ?? 1));
      setContainerId((model && program && defaultClipContainer(model, program)) || '');
      setName('');
      setError(undefined);
    }
  }

  const containers = model && program ? containersOf(model, program) : [];
  const a = parseLineNumber(first);
  const b = parseLineNumber(last);
  const lineIds = linesBetween(lines, a, b);
  const block = !program
    ? '这个来源还没有编排程序。'
    : !lineIds
      ? rangeInvalidText(lines.length)
      : clipCreateBlock(program, lines, lineIds);
  const container = containers.find((n) => n.id === containerId);

  // 改了输入就去掉上一次后端拒绝的原因。
  const edit = (set: (value: string) => void) => (value: string) => {
    set(value);
    setError(undefined);
  };

  const submit = async () => {
    if (block || !lineIds || !container || props.busy) return;
    setError(undefined);
    const failure = await props.onCreate({ lineIds, containerId: container.id, name }, { first: a, last: b });
    if (failure) setError(failure);
  };

  return (
    <FormDialog
      open={props.open}
      headline={`新建片段 · ${props.branchName}`}
      submitText="创建片段"
      submitDisabled={!!block || !container}
      busy={props.busy}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="orch-dialog new-clip"
    >
      <ClipRangeFields first={first} last={last} lines={lines} onFirst={edit(setFirst)} onLast={edit(setLast)} />
      {block && (
        <Note error className="new-clip-block">
          {block}
        </Note>
      )}
      <Select
        label="目标容器"
        value={containerId}
        options={model ? containers.map((n) => ({ value: n.id, label: `${nodeName(model, n)} · ${roleText(model, n)}${n.id === program?.defaultContainerId ? '（默认容器）' : ''}` })) : []}
        onChange={edit(setContainerId)}
      />
      <Note>
        {container && container.id === program?.defaultContainerId
          ? '目标是默认容器：这几句仍在默认容器显示，但单独成一段。'
          : '这几句改在目标容器里显示。'}
        对话框里不能新建容器：要放进新容器，先在画面结构里新建，或用“快速布局…”。
      </Note>
      <TextField label="名称" value={name} onChange={setName} placeholder="可以不填，显示为“片段 N”" onEnter={() => void submit()} />
      {error && (
        <Note error className="new-clip-error">
          {error}
        </Note>
      )}
    </FormDialog>
  );
}
