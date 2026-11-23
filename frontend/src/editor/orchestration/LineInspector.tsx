// 句子的属性（G24，W4 设计 §14.7）：这一句的本句微调与前奏；选了几句时只显示范围。

import type { EditorSession } from '../session/editor-session';
import { RubyStyleSection } from './RubyStyleSection';
import type { LineLocalTransform, OrchestrationLeadIn, OrchestrationProgram } from '../../protocol';
import { isTimed, type EditorLine } from '../model/lyrics';
import { lineRangeText, type LineRange, type OrchestrationModel } from '../model/orchestration';
import {
  leadInOfLine,
  lineContainerText,
  lineNumberText,
  lineTransformCommand,
  NO_TRANSFORM,
  secondsText,
} from '../model/orchestration-lead-ins';
import { roundTo, toPercent } from '../model/orchestration-properties';
import { formatTime } from '../model/time';
import { Button } from '../ui/Button';
import { NumberField } from '../ui/fields';
import { Group, InspectorHead, Note, Row } from '../workspace/panel';
import type { LineCommand, LinePresentationState, OrchestrationResult } from './orchestration-store';

/** 句子命令（§14.7）：block 是整体不可用的原因（只读、后端没开放）；run 发一条命令，成功时提示 done 并可撤销。 */
export interface LineActions {
  block: string | undefined;
  busy: boolean;
  run: (command: LineCommand, done: string) => Promise<OrchestrationResult>;
}

const ratio = (percent: number) => percent / 100;

/** 这一句（§14.7）：头部、本句微调、前奏。 */
export function LineInspector(props: {
  model: OrchestrationModel;
  program: OrchestrationProgram | undefined;
  branchId: string;
  branchName: string;
  line: EditorLine;
  lines: readonly EditorLine[];
  presentation: LinePresentationState | undefined;
  session: EditorSession;
  actions: LineActions;
  /** 来源不支持前奏时是 undefined（不显示前奏一项）。 */
  leadIns: { block: string | undefined; onAdd: (lineId: string) => void; onSelect: (leadIn: OrchestrationLeadIn) => void } | undefined;
  onSeek: (us: number) => void;
}) {
  const { model, program, line, lines, actions, presentation } = props;
  const number = lineNumberText(lines, line.id);
  const response = presentation?.lineId === line.id && presentation.branchId === props.branchId ? presentation.response : undefined;
  const leadIn = props.leadIns && program ? leadInOfLine(program, line.id) : undefined;
  const leadInBlock = props.leadIns?.block ?? (isTimed(line) ? undefined : '这一句没有时间。');
  return (
    <div className="insp line-inspector">
      <InspectorHead crumbs={[props.branchName]} title={number} kind="句子" id={line.id} />
      <Group
        title="句子"
        extra={
          <Button variant="text" size="sm" icon="play_arrow" isDisabled={!isTimed(line)} onPress={() => isTimed(line) && props.onSeek((line.startUs + line.endUs) / 2)}>
            跳到这一句
          </Button>
        }
      >
        <p className="line-text">{line.text || '（空行）'}</p>
        <dl className="dl info-list">
          <Row label="时间" value={isTimed(line) ? `${formatTime(line.startUs)}～${formatTime(line.endUs)}` : '没有时间'} />
          {program && <Row label="显示在" value={lineContainerText(model, program, line.id)} />}
        </dl>
        {actions.block && <Note className="property-block">{actions.block}</Note>}
      </Group>
      {!response ? (
        presentation?.status === 'error' ? (
          <Note error className="line-presentation-error">
            {presentation.error}
          </Note>
        ) : (
          <Note className="insp-loading">正在读取……</Note>
        )
      ) : (
        <>
          {presentation?.status === 'error' && (
            <Note error className="line-presentation-error">
              {presentation.error}
            </Note>
          )}
          <TransformSection key={`transform-${line.id}`} branchId={props.branchId} lineId={line.id} transform={response.localTransform} actions={actions} />
        </>
      )}
      <RubyStyleSection session={props.session} branchId={props.branchId} lineId={line.id} />
      {props.leadIns && (
        <Group title="前奏">
          {leadIn ? (
            <div className="row-actions line-lead-in">
              <span>
                有前奏：{secondsText(leadIn.durationUs)}
                {leadIn.enabled ? '' : '（已停用）'}
              </span>
              <Button variant="text" size="sm" onPress={() => props.leadIns!.onSelect(leadIn)}>
                查看前奏
              </Button>
            </div>
          ) : (
            <>
              <div className="row-actions">
                <Button variant="text" size="sm" icon="add" isDisabled={!!leadInBlock} onPress={() => props.leadIns!.onAdd(line.id)}>
                  为这一句添加前奏…
                </Button>
              </div>
              {leadInBlock && <Note>{leadInBlock}</Note>}
            </>
          )}
        </Group>
      )}
    </div>
  );
}

/** 选了几句（§14.7 第 4 种情况）：只显示范围，可以为这几句新建片段。 */
export function LinesSummary(props: {
  branchName: string;
  range: LineRange;
  actions: LineActions;
  clip: { block: string | undefined; busy: boolean; onNew: () => void };
}) {
  const { range } = props;
  const clipBlock = props.clip.block ?? (range.contiguous ? undefined : '选中的句子不连续。');
  return (
    <div className="insp line-inspector">
      <InspectorHead crumbs={[props.branchName]} title="已选句子" kind="句子" />
      <Group title="句子">
        <p className="line-text lines-summary">
          已选{lineRangeText(range)}，共 {range.count} 句
        </p>
        <Note>本句微调一次只对一句：在时间轴上点一句，或在画布上双击文字。</Note>
        <div className="row-actions">
          <Button variant="tonal" size="sm" icon="add" isDisabled={!!clipBlock || props.clip.busy} onPress={props.clip.onNew}>
            为这几句新建片段
          </Button>
        </div>
        {clipBlock && <Note>{clipBlock}</Note>}
        {props.actions.block && <Note className="property-block">{props.actions.block}</Note>}
      </Group>
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
    <Group
      title="本句微调"
      extra={
        <Button
          variant="text"
          size="sm"
          isDisabled={disabled || !transform}
          onPress={() => void actions.run({ type: 'orchestration.setLineLocalTransform', branchId, lineId, transform: null }, '已清除本句微调')}
        >
          清除本句微调
        </Button>
      }
    >
      <div className="g2 line-transform">
        <NumberField label="X 偏移" labelPlacement="inside" suffix="%" value={toPercent(t.offsetXRatio)} disabled={disabled} onCommit={(v) => set({ offsetXRatio: ratio(v) })} />
        <NumberField label="Y 偏移" labelPlacement="inside" suffix="%" value={toPercent(t.offsetYRatio)} disabled={disabled} onCommit={(v) => set({ offsetYRatio: ratio(v) })} />
        <NumberField label="旋转" labelPlacement="inside" suffix="°" value={roundTo(t.rotationDegrees, 2)} disabled={disabled} onCommit={(rotationDegrees) => set({ rotationDegrees })} />
        <NumberField label="缩放" labelPlacement="inside" suffix="×" value={roundTo(t.scale, 3)} greaterThan={0} disabled={disabled} onCommit={(scale) => set({ scale })} />
      </div>
      <Note>偏移按所在容器参考框的宽高算，可以为负；也可以在画布上拖动这一句。</Note>
    </Group>
  );
}
