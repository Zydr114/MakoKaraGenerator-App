import { useEffect, useState } from 'react';
import type { LyricTokenResult, TokenSplitRuleName } from '../../protocol';
import type { EditorLine } from '../model/lyrics';
import { SPLIT_RULES } from '../model/split-rules';
import { SEPARATOR, currentSegments, parseSegments } from '../model/token-segments';
import type { EditorSession } from '../session/editor-session';
import { toast } from '../ui/legacy/feedback';
import { FormDialog } from '../ui/legacy/FormDialog';
import { Select, Switch, TextField } from '../ui/legacy/fields';

/** “按规则重切本行…”（整体设计 §5.2）：在正文里用“|”标出切分位置，下面预览切分结果。 */
export function ResplitLineDialog(props: { session: EditorSession; line: EditorLine | undefined; open: boolean; onClose: () => void }) {
  const { session, line } = props;
  const [text, setText] = useState('');
  const [estimate, setEstimate] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!props.open || !line) return;
    setText(currentSegments(line).join(SEPARATOR));
    setEstimate(false);
    // 只在打开时取当前切分；打开期间行被刷新不覆盖用户的输入。
  }, [props.open, line?.id]);

  const parsed = line ? parseSegments(text, line.text) : undefined;
  const submit = async () => {
    if (!line || !parsed?.ok) return;
    setBusy(true);
    try {
      const response = await session.command({
        type: 'lyrics.resplitLine',
        target: { branchId: line.branchId, lineId: line.id },
        segments: parsed.segments,
        estimateTimes: estimate,
      });
      if (!response) return;
      if (response.changed && response.tokenResult) toast(`已重切本行：${tokenStats(response.tokenResult)}`);
      props.onClose();
    } finally {
      setBusy(false);
    }
  };

  return (
    <FormDialog
      open={props.open && !!line}
      headline="按规则重切本行"
      description="用“|”标出切分位置。去掉“|”后必须与正文完全一致。"
      submitText="重切"
      submitDisabled={!parsed?.ok}
      busy={busy}
      onSubmit={() => void submit()}
      onClose={props.onClose}
    >
      <TextField label="切分" value={text} error={parsed && !parsed.ok ? parsed.error : undefined} onChange={setText} onEnter={() => void submit()} className="dialog-field" />
      <div className="segment-preview" aria-label="切分预览">
        {parsed?.ok && parsed.segments.map((s, i) => <span key={i} className="segment-chip">{s}</span>)}
        {parsed?.ok && parsed.segments.length === 0 && <span className="muted">空行，没有文本单元。</span>}
      </div>
      <Switch label="按字数估算新单元的时间（结果标记为“估算”）" checked={estimate} onChange={setEstimate} />
    </FormDialog>
  );
}

/** “按规则重切整个来源…”：选规则和是否估算，整个来源一条历史。 */
export function ResplitBranchDialog(props: { session: EditorSession; branchId: string; open: boolean; onClose: () => void }) {
  const { session } = props;
  const [rule, setRule] = useState<TokenSplitRuleName>('mixed');
  const [estimate, setEstimate] = useState(false);
  const [busy, setBusy] = useState(false);
  const branch = session.state.branches?.find((b) => b.id === props.branchId);
  const lineCount = session.linesOf(props.branchId).length;

  const submit = async () => {
    setBusy(true);
    try {
      const response = await session.command({ type: 'lyrics.resplitBranch', branchId: props.branchId, rule, estimateTimes: estimate });
      if (!response) return;
      if (response.changed && response.tokenResult) toast(`已重切 ${response.tokenResult.changedLineCount} 行：${tokenStats(response.tokenResult)}`);
      props.onClose();
    } finally {
      setBusy(false);
    }
  };

  return (
    <FormDialog
      open={props.open}
      headline="按规则重切整个来源"
      description={`按所选规则重切「${branch?.name ?? '当前来源'}」的全部 ${lineCount} 行。整个来源记一条历史，可以撤销。`}
      submitText="重切"
      submitDisabled={rule === 'none' || lineCount === 0}
      busy={busy}
      onSubmit={() => void submit()}
      onClose={props.onClose}
    >
      <Select
        label="拆分规则"
        value={rule}
        options={SPLIT_RULES.filter((r) => r.name !== 'none').map((r) => ({ value: r.name, label: r.label }))}
        onChange={setRule}
        className="dialog-field"
      />
      <Switch label="按字数估算新单元的时间（结果标记为“估算”）" checked={estimate} onChange={setEstimate} />
    </FormDialog>
  );
}

function tokenStats(result: LyricTokenResult): string {
  const parts = [`保留 ${result.preservedTokenCount ?? 0} 个文本单元`, `新建 ${result.createdTokenCount ?? 0} 个`];
  if (result.estimatedTokenCount) parts.push(`估算 ${result.estimatedTokenCount} 个`);
  return parts.join('，');
}
