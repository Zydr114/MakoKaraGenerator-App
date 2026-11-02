import { useEffect, useRef, useState } from 'react';
import type { OrchestrationProgram } from '../../protocol';
import { LAYOUT_PRESETS, layoutImpactText, layoutIsNoOp, lineRangeText, type LayoutPresetInfo, type LineRange } from '../model/orchestration';
import { RadioGroup } from '../ui/choices';
import { FormDialog } from '../ui/FormDialog';
import type { LayoutPreviewState } from './orchestration-store';
import { Note } from './parts';

/** 卡片上的示意图（只是图标，不代表后端几何）：每个槽位一条横线，位置大致对应预设。 */
const SKETCH: Record<LayoutPresetInfo['preset'], readonly { x: number; y: number }[]> = {
  SingleCentered: [{ x: 45, y: 64 }],
  KaraokeDouble: [
    { x: 10, y: 50 },
    { x: 80, y: 68 },
  ],
  DoubleCentered: [
    { x: 45, y: 46 },
    { x: 45, y: 66 },
  ],
  TripleCentered: [
    { x: 45, y: 42 },
    { x: 45, y: 54 },
    { x: 45, y: 66 },
  ],
};

const sameLines = (a: readonly string[] | undefined, b: readonly string[] | undefined) => (a ?? []).join('\n') === (b ?? []).join('\n');

/**
 * 快速布局（G18，W4 设计 §9.3、§10.4）：选一个预设和范围（整个来源，或时间轴上选中的连续几句），
 * 先预演写明影响，确认后一次套用（一条历史）。
 */
export function QuickLayoutDialog(props: {
  open: boolean;
  branchName: string;
  lineCount: number | undefined;
  program: OrchestrationProgram | undefined;
  /** 时间轴上选中的句子；没有选中时省略。 */
  range: LineRange | undefined;
  preview: LayoutPreviewState | undefined;
  busy: boolean;
  onPreview: (preset: LayoutPresetInfo, lineIds: readonly string[] | undefined) => void;
  onApply: (preset: LayoutPresetInfo, lineIds: readonly string[] | undefined) => void;
  onClose: () => void;
}) {
  const { range } = props;
  const [chosen, setChosen] = useState<LayoutPresetInfo | null>(null);
  const [scope, setScope] = useState<'source' | 'range'>('source');
  // 每次打开时重置：有连续的选中句子就默认只套这几句。在渲染时调整，不留一帧旧状态；之后选中的句子变了也不改范围。
  const [opened, setOpened] = useState(false);
  if (props.open !== opened) {
    setOpened(props.open);
    if (props.open) {
      setChosen(null);
      setScope(range?.contiguous ? 'range' : 'source');
    }
  }

  const useRange = scope === 'range' && !!range?.contiguous;
  const lineIds = useRange ? range.lineIds : undefined;
  const preview = chosen && props.preview?.preset === chosen.preset && sameLines(props.preview.lineIds, lineIds) ? props.preview : undefined;
  const already = !!chosen && !!props.program && layoutIsNoOp(props.program, chosen, lineIds);

  // 预演跟着“预设＋范围”走：任何一个变了就按当前的值重新预演，不会拿旧范围发请求。
  const onPreview = useRef(props.onPreview);
  onPreview.current = props.onPreview;
  const scopeKey = (lineIds ?? []).join('\n');
  useEffect(() => {
    if (props.open && chosen) onPreview.current(chosen, scopeKey ? scopeKey.split('\n') : undefined);
  }, [props.open, chosen, scopeKey]);

  return (
    <FormDialog
      open={props.open}
      headline={`快速布局 · ${props.branchName}`}
      submitText={useRange ? `套用到${lineRangeText(range)}` : `套用到 ${props.branchName}`}
      submitDisabled={!chosen || preview?.status !== 'ready' || already}
      busy={props.busy}
      onSubmit={() => chosen && props.onApply(chosen, lineIds)}
      onClose={props.onClose}
      className="orch-dialog quick-layout"
      footerNote="套用是一条历史，可以撤销。"
    >
      <RadioGroup<LayoutPresetInfo['preset'] | ''>
        label="布局预设"
        variant="card"
        orientation="horizontal"
        className="layout-presets"
        value={chosen?.preset ?? ''}
        options={LAYOUT_PRESETS.map((preset) => ({
          value: preset.preset,
          label: preset.label,
          description: (
            <>
              <svg className="layout-sketch" viewBox="0 0 160 90" aria-hidden="true">
                <rect x="1" y="1" width="158" height="88" rx="6" className="layout-sketch-canvas" />
                {SKETCH[preset.preset].map((bar, i) => (
                  <rect key={i} x={bar.x} y={bar.y} width="70" height="6" rx="3" className="layout-sketch-bar" />
                ))}
              </svg>
              <span>
                {preset.slots} 个槽位{props.program?.recognizedLayoutPreset === preset.preset ? ' · 当前' : ''}
              </span>
            </>
          ),
        }))}
        onChange={(value) => setChosen(LAYOUT_PRESETS.find((p) => p.preset === value) ?? null)}
      />
      <RadioGroup<'source' | 'range'>
        label="范围"
        orientation="horizontal"
        className="layout-scope"
        value={useRange ? 'range' : 'source'}
        options={[
          { value: 'source', label: `整个来源${props.lineCount !== undefined ? `（共 ${props.lineCount} 句）` : ''}` },
          ...(range
            ? [{ value: 'range' as const, label: `选中的${lineRangeText(range)}${range.contiguous && range.count > 1 ? `（${range.count} 句）` : ''}`, disabled: !range.contiguous }]
            : []),
        ]}
        onChange={setScope}
      />
      <Note>
        {!range
          ? '在时间轴上选连续几句，可以只给这几句套用。'
          : !range.contiguous
            ? '选中的句子不连续，只套几句需要连续歌词段。'
            : useRange
              ? '只给这几句新建片段，其余句子保持原来的调度。'
              : '整个来源换成新布局，并设为默认容器。'}
      </Note>
      <p className={`layout-impact${preview?.status === 'error' ? ' err' : ''}`} aria-live="polite">
        {!chosen
          ? '选择一个预设，先看它会改动什么。'
          : !preview || preview.status === 'loading'
            ? '正在预演……'
            : preview.status === 'error'
              ? preview.error
              : layoutImpactText(preview.result!, chosen, already, useRange ? range : undefined)}
      </p>
    </FormDialog>
  );
}
