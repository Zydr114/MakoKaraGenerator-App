import { useState } from 'react';
import type { CreateNodeKind, OrchestrationNode, OrchestrationProgram } from '../../protocol';
import { insertionParent, isPlainGroup, moveTargets, NODE_KIND_NAME, NODE_KIND_TEXT, nodeName, nodePath, type OrchestrationModel } from '../model/orchestration';
import { DEFAULT_MARK_COUNT, MAX_MARKS } from '../model/orchestration-lead-ins';
import { FormDialog } from '../ui/legacy/FormDialog';
import { Select, TextField } from '../ui/legacy/fields';
import type { NewNode } from './orchestration-store';

/** 下拉框里的“画布根层”：mdui-select 不接受空值，用一个不会是节点 ID 的值代替 null。 */
const CANVAS = '::canvas';
const CANVAS_LABEL = '画布根层（不属于任何来源）';
/** 新建对话框的种类：结构节点三种，加上前奏提示组（G23，§14.6）。 */
type NewKind = CreateNodeKind | 'countdown';
const KIND_TEXT: Record<NewKind, string> = { ...NODE_KIND_TEXT, countdown: '前奏提示组' };
const KIND_NAME: Record<NewKind, string> = { ...NODE_KIND_NAME, countdown: '前奏提示' };

/** 新建的输入：结构节点，或在普通组 parentId 下有 count 个标记的前奏提示组。 */
export type NewNodeInput = NewNode | { kind: 'countdown'; parentId: string; name: string; count: number };

type BranchName = (branchId: string) => string | undefined;

const pathOf = (model: OrchestrationModel, node: OrchestrationNode, branchName: BranchName) => nodePath(model, node.id, branchName).join(' / ');

/**
 * 新建节点（G20-B，W4 设计 §12.3）：种类、放在哪个普通组（或画布根层）、名称、轮换的槽位数。
 * 后端拒绝时原因写在对话框里，对话框不关闭。
 */
export function NewNodeDialog(props: {
  open: boolean;
  model: OrchestrationModel | undefined;
  /** 结构树顺序的全部节点：“放在”从里面取普通组。 */
  candidates: readonly OrchestrationNode[];
  /** 分配条来源的程序：没有选中节点时预填它的根。 */
  program: OrchestrationProgram | undefined;
  selectedId: string | undefined;
  branchName: BranchName;
  busy: boolean;
  /** 后端开放了前奏提示组时可以新建它。 */
  countdown: boolean;
  /** 返回后端拒绝的原因；成功时返回 undefined，由调用方关闭对话框。 */
  onCreate: (node: NewNodeInput) => Promise<string | undefined>;
  onClose: () => void;
}) {
  const { model } = props;
  const [kind, setKind] = useState<NewKind>('group');
  const [parent, setParent] = useState(CANVAS);
  const [name, setName] = useState('');
  const [slots, setSlots] = useState('2');
  const [marks, setMarks] = useState(String(DEFAULT_MARK_COUNT));
  const [error, setError] = useState<string | undefined>();
  // 每次打开时重置并预填父级。在渲染时调整，不留一帧旧状态。
  const [opened, setOpened] = useState(false);
  if (props.open !== opened) {
    setOpened(props.open);
    if (props.open) {
      setKind('group');
      setParent((model && insertionParent(model, props.selectedId, props.program)) ?? CANVAS);
      setName(NODE_KIND_NAME.group);
      setSlots('2');
      setMarks(String(DEFAULT_MARK_COUNT));
      setError(undefined);
    }
  }

  const groups = model ? props.candidates.filter((n) => isPlainGroup(model, n)) : [];
  const slotCount = /^\s*\d+\s*$/.test(slots) ? Number(slots) : NaN;
  const slotError = kind === 'slotSequence' && !(slotCount >= 1 && slotCount <= 64) ? '槽位数是 1～64 的整数。' : undefined;
  const markCount = /^\s*\d+\s*$/.test(marks) ? Number(marks) : NaN;
  const markError = kind === 'countdown' && !(markCount >= 1 && markCount <= MAX_MARKS) ? `标记数是 1～${MAX_MARKS} 的整数。` : undefined;
  const parentError = kind === 'countdown' && parent === CANVAS ? '前奏提示组只能放在普通组里，不能放在画布根层。' : undefined;
  const fieldError = slotError ?? markError ?? parentError;

  // 改了输入就去掉上一次后端拒绝的原因。
  const edit =
    <T,>(set: (value: T) => void) =>
    (value: T) => {
      set(value);
      setError(undefined);
    };
  // 名称还是上一个种类的预填值时，换种类跟着换。
  const changeKind = (next: NewKind) => {
    if (name === KIND_NAME[kind]) setName(KIND_NAME[next]);
    edit(setKind)(next);
  };

  const submit = async () => {
    if (!model || fieldError || props.busy) return;
    setError(undefined);
    let node: NewNodeInput;
    if (kind === 'countdown') node = { kind, parentId: parent, name, count: markCount };
    else {
      node = { kind, parentId: parent === CANVAS ? null : parent, name };
      if (kind === 'slotSequence') node.slotCount = slotCount;
    }
    const failure = await props.onCreate(node);
    if (failure) setError(failure);
  };

  return (
    <FormDialog
      open={props.open}
      headline="新建"
      submitText="新建"
      submitDisabled={!model || !!fieldError}
      busy={props.busy}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="structure-dialog new-node"
    >
      <Select
        label="种类"
        className="dialog-field new-node-kind"
        value={kind}
        options={(['group', 'auto', 'slotSequence', ...(props.countdown ? ['countdown' as const] : [])] as const).map((k) => ({ value: k, label: KIND_TEXT[k] }))}
        onChange={changeKind}
      />
      <Select
        label="放在"
        className="dialog-field new-node-parent"
        value={parent}
        options={[
          ...(model ? groups.map((n) => ({ value: n.id, label: pathOf(model, n, props.branchName) })) : []),
          { value: CANVAS, label: CANVAS_LABEL },
        ]}
        onChange={edit(setParent)}
      />
      {parentError && <p className="notice-error new-node-parent-error">{parentError}</p>}
      <TextField label="名称" value={name} onChange={edit(setName)} placeholder="清空表示不命名" className="dialog-field new-node-name" onEnter={() => void submit()} />
      {kind === 'slotSequence' && (
        <TextField
          label="槽位数"
          value={slots}
          onChange={edit(setSlots)}
          error={slotError}
          helper={slotError ? '' : `同时最多显示 ${Math.min(2, slotCount || 2)} 句，之后可调。`}
          className="dialog-field new-node-slots"
          onEnter={() => void submit()}
        />
      )}
      {kind === 'countdown' && (
        <TextField label="标记数" value={marks} onChange={edit(setMarks)} error={markError} className="dialog-field new-node-marks" onEnter={() => void submit()} />
      )}
      <p className="hint">
        {kind === 'countdown'
          ? `前奏提示组放在某个来源的根下，才能被那个来源的前奏使用。标记的字都是“●”，均匀排列，之后可以改。`
          : `${kind !== 'group' ? '歌词容器放在某个来源的根下，才能被选为那个来源的默认容器或片段的目标。' : ''}新节点铺满父级、居中，位置以后在属性区调整。`}
      </p>
      {error && (
        <p className="notice-error structure-dialog-error" role="alert">
          {error}
        </p>
      )}
    </FormDialog>
  );
}

/** 移到另一个普通组或画布根层（G20，W4 设计 §12.4）：后端保持节点在画面上的位置。 */
export function MoveNodeDialog(props: {
  /** 要移动的节点；undefined 时对话框关闭。 */
  nodeId: string | undefined;
  model: OrchestrationModel | undefined;
  candidates: readonly OrchestrationNode[];
  branchName: BranchName;
  busy: boolean;
  /** 返回失败的原因；成功时返回 undefined，由调用方关闭对话框。 */
  onMove: (parentId: string | null) => Promise<string | undefined>;
  onClose: () => void;
}) {
  const { model, nodeId } = props;
  const node = model && nodeId ? model.byId.get(nodeId) : undefined;
  const targets = model && nodeId ? moveTargets(model, nodeId, props.candidates) : { groups: [], canvas: false };
  const options = [
    ...(model ? targets.groups.map((n) => ({ value: n.id, label: pathOf(model, n, props.branchName) })) : []),
    ...(targets.canvas ? [{ value: CANVAS, label: CANVAS_LABEL }] : []),
  ];
  const [target, setTarget] = useState('');
  const [error, setError] = useState<string | undefined>();
  const [opened, setOpened] = useState<string | undefined>();
  if (nodeId !== opened) {
    setOpened(nodeId);
    if (nodeId) {
      setTarget(options[0]?.value ?? '');
      setError(undefined);
    }
  }
  const chosen = options.find((o) => o.value === target);

  const submit = async () => {
    if (!chosen || props.busy) return;
    setError(undefined);
    const failure = await props.onMove(chosen.value === CANVAS ? null : chosen.value);
    if (failure) setError(failure);
  };

  return (
    <FormDialog
      open={!!nodeId}
      headline={`移动“${node && model ? nodeName(model, node) : (nodeId ?? '')}”`}
      submitText="移动"
      submitDisabled={!chosen}
      busy={props.busy}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="structure-dialog move-node"
    >
      {options.length > 0 ? (
        <Select
          label="移到"
          className="dialog-field move-node-target"
          value={target}
          options={options}
          onChange={(value) => {
            setTarget(value);
            setError(undefined);
          }}
        />
      ) : (
        <p className="notice-error">没有可以移到的普通组：子树里有某个来源的默认容器、片段目标或前奏提示容器时，只能留在那个来源的根下。</p>
      )}
      <p className="hint">移动后画面上的位置不变。只能放进普通组（没有布局、没有内容）或画布根层；放在画布根层的节点不属于任何来源。</p>
      {error && (
        <p className="notice-error structure-dialog-error" role="alert">
          {error}
        </p>
      )}
    </FormDialog>
  );
}
