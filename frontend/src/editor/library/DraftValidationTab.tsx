// 作者草稿的验证页签（W8 设计 §2.7）：运行条件（默认跟随试样画布）、固定规格、进度与取消、报告、问题与诊断、需重新验证。
// 有路径和行的问题点击后跳到源码页签的对应行；运行时异常没有行号，点击只打开验证的修订。

import { useEffect, useState } from 'react';
import type { TemplateMaterialSlot, TemplateWorkshopSampleId } from '../../protocol';
import { JobProgressView } from '../export/RenderJobCards';
import { SLOT_LABEL, shortRevision } from '../model/full-template';
import { recordedBackendText } from '../model/render-device';
import { diagnosticText, isTerminal } from '../model/render-job';
import { formatTime } from '../model/time';
import { Button } from '../ui/Button';
import { cls } from '../ui/cls';
import { toast } from '../ui/feedback';
import { Select } from '../ui/fields';
import { useObservable, useObservableSelector } from '../ui/hooks';
import { Icon } from '../ui/Icon';
import { Group, Note } from '../workspace/panel';
import { type FullDraft } from './author-draft-store';
import type { DraftSourceModel } from './draft-source';
import {
  AUTHOR_VALIDATION_SPEC,
  extraDiagnostics,
  ORIGIN_TEXT,
  validationConditions,
  validationIssueRows,
  validationOutcomeText,
  validationStale,
  type DraftValidationModel,
  type ValidationConditions,
  type ValidationIssueRow,
} from './draft-validation';
import { confirmLoseEdits } from './DraftSourceTab';
import { SAMPLE_LABEL, targetLabel, type TemplateWorkshop, type WorkshopState } from './template-workshop';

/** 问题和诊断每次多显示多少条。报告里是完整列表，只是分批显示。 */
const PAGE = 50;

const NO_CANVAS = { subscribe: () => () => undefined, get: (): WorkshopState | undefined => undefined };

export function DraftValidationTab(props: {
  draft: FullDraft;
  model: DraftValidationModel;
  source: DraftSourceModel | undefined;
  workshop: TemplateWorkshop | null;
  onShowSource: () => void;
  disabled: boolean;
}) {
  const { draft, model, source } = props;
  const state = useObservable(model.state$);
  // 试样画布当前的样本、目标和材质部位（画布在这个草稿上时）；只订阅这几个值，免得每帧都重画。
  const canvasKey = useObservableSelector<WorkshopState | undefined, string>(props.workshop?.state$ ?? NO_CANVAS, (s) => canvasKeyOf(s, draft.draftId));
  useEffect(() => void model.restore(), [model]);

  const options = draft.sampleOptions;
  const canvas = canvasKey ? (JSON.parse(canvasKey) as Partial<ValidationConditions>) : undefined;
  const conditions = validationConditions(options, state.chosen, canvas);
  const job = state.job;
  const running = model.running;
  const terminal = !!job && isTerminal(job.state);
  const stale = !!job && terminal && validationStale(job, draft);
  const validated = job?.authorValidationInput;

  const change = (patch: Partial<ValidationConditions>) => {
    if (!conditions) return;
    const next = { ...conditions, ...patch };
    // 换样本时目标退回新样本的第一个（原来的键还在就留着）。
    model.choose(validationConditions(options, next, undefined));
  };

  const openIssue = async (row: ValidationIssueRow) => {
    const revision = validated?.sourceRevision;
    if (!source || !revision) return;
    if (source.state.revision !== revision && source.dirty && !(await confirmLoseEdits('读进验证的修订'))) return;
    // 先开始读修订（同步标为读取中），再切页签：源码页签就不会另外去读在用修订。
    const going = source.goTo(revision, row.location);
    props.onShowSource();
    if (!(await going)) toast(row.location ? '验证的修订里没有这个文件。' : '读不到验证的修订。');
  };

  const sample = options.samples.find((s) => s.sampleId === conditions?.sampleId);
  const inside = { labelPlacement: 'inside', size: 'sm' } as const;
  return (
    <section className="draft-validation" aria-label="验证" data-job-id={job?.jobId} data-state={job?.state} data-stale={stale ? '' : undefined}>
      <Group title="条件" hint={state.chosen ? undefined : canvas ? '跟随试样画布' : '默认样本'}>
        {conditions ? (
          <>
            <div className="g2 validation-conditions">
              <Select<TemplateWorkshopSampleId>
                {...inside}
                label="样本"
                value={conditions.sampleId}
                options={options.samples.map((s) => ({ value: s.sampleId, label: SAMPLE_LABEL[s.sampleId] ?? s.sampleId, disabled: s.targets.length === 0 }))}
                onChange={(sampleId) => change({ sampleId })}
                disabled={running}
              />
              <Select
                {...inside}
                label="目标"
                value={conditions.targetKey}
                options={(sample?.targets ?? []).map((t) => ({ value: t.key, label: targetLabel(t.key) }))}
                onChange={(targetKey) => change({ targetKey })}
                disabled={running}
              />
              {conditions.materialSlot && (
                <Select<TemplateMaterialSlot>
                  {...inside}
                  label="材质部位"
                  className="span2"
                  value={conditions.materialSlot}
                  options={options.materialSlots.map((slot) => ({ value: slot, label: SLOT_LABEL[slot] }))}
                  onChange={(materialSlot) => change({ materialSlot })}
                  disabled={running}
                />
              )}
            </div>
            <Note>按固定规格逐帧检查：{AUTHOR_VALIDATION_SPEC}。渲染设备按顶栏的选择。</Note>
            <Note>SDK 2 验证需要 Vulkan，请在顶栏明确选择 GPU。</Note>
            {source?.dirty && <Note error>源码页签还有改动没保存成修订，本次验证只检查草稿的在用修订。</Note>}
            <div className="row-actions">
              <Button
                variant="filled"
                size="sm"
                icon="fact_check"
                className="validation-start"
                isPending={!!state.submitting}
                isDisabled={running || props.disabled}
                onPress={() => void model.start(conditions)}
              >
                {state.submitting ? '正在提交验证…' : job && !running ? '重新验证' : '开始验证'}
              </Button>
              {state.chosen && (
                <Button variant="text" size="sm" isDisabled={running} onPress={() => model.choose(undefined)}>
                  跟随试样画布
                </Button>
              )}
            </div>
          </>
        ) : (
          <Note error>这个草稿没有可用的试样目标，不能验证。</Note>
        )}
      </Group>
      <Group title="报告" hint={validated ? `修订 ${shortRevision(validated.sourceRevision)}，${conditionsText(validated)}` : undefined} className="validation-report">
        {state.submitError && <Note error>{state.submitError}</Note>}
        {state.restoring && <Note>正在读取上一次的验证…</Note>}
        {!job && state.error && <Note error>{state.error}</Note>}
        {state.retryable && (
          <div className="row-actions">
            <Button variant="text" size="sm" icon="refresh" isDisabled={!!state.restoring || props.disabled} onPress={() => void model.retry()}>
              重新读取验证结果
            </Button>
          </div>
        )}
        {!job && !state.restoring && !state.error && !state.submitError && <Note>还没有验证过。</Note>}
        {job && <Note className="job-device">渲染设备：{recordedBackendText(job.input?.renderBackend)}</Note>}
        {job && !terminal && <JobProgressView job={job} cancelling={state.cancelling} error={state.error} onCancel={() => void model.cancel()} />}
        {terminal && !state.settled && <Note className="job-result">正在读取验证报告……</Note>}
        {terminal && state.settled && <ValidationResult key={job.jobId} state={state} stale={stale} onOpen={(row) => void openIssue(row)} canOpen={!!source} />}
      </Group>
    </section>
  );
}

function ValidationResult(props: { state: DraftValidationModel['state']; stale: boolean; canOpen: boolean; onOpen: (row: ValidationIssueRow) => void }) {
  const { state } = props;
  const report = state.report;
  const rows = report ? validationIssueRows(report) : [];
  const extra = report ? extraDiagnostics(report) : [];
  const [issueCount, setIssueCount] = useState(PAGE);
  const [diagnosticCount, setDiagnosticCount] = useState(PAGE);
  return (
    <div className="job-result validation-result" data-outcome={report ? (report.succeeded ? 'passed' : 'failed') : undefined}>
      {props.stale && <Note error className="validation-stale">草稿已经改过，结果对应旧版本，需要重新验证。</Note>}
      {report ? (
        <>
          <div className="vsum">
            <span>
              <b className={report.succeeded ? 'ok' : 'bad'}>{report.succeeded ? '通过' : '失败'}</b>结果
            </span>
            <span>
              <b>{report.opaqueFrames}</b>普通帧
            </span>
            <span>
              <b>{report.transparentFrames}</b>透明帧
            </span>
            <span>
              <b>{rows.length}</b>个问题
            </span>
          </div>
          <p className="note validation-outcome">
            {validationOutcomeText(report)} · 设备 {recordedBackendText(report.renderBackend)}
          </p>
        </>
      ) : (
        <Note error={state.job?.state !== 'cancelled'}>{state.error ?? '没有读到验证报告。'}</Note>
      )}
      {report && state.error && <Note error>{state.error}</Note>}
      {rows.length > 0 && (
        <ul className="iss-list validation-issues">
          {rows.slice(0, issueCount).map((row) => {
            const open = props.canOpen && row.script;
            return (
              <li key={row.key} className={cls('iss', 'validation-issue', row.isError ? 'e' : 'w')} data-severity={row.isError ? 'error' : 'warning'}>
                <Icon name={row.isError ? 'error' : 'warning'} />
                <span className="iss-text">
                  <b>
                    <code>{row.code}</code> {row.message}
                  </b>
                  <span className="small">
                    {formatTime(row.timeUs)} · {row.transparent ? '透明' : '普通'} · {ORIGIN_TEXT[row.origin]}
                    {row.location ? ` · ${row.location.path}:${row.location.line}:${row.location.column}` : row.origin === 'runtime' ? '，运行时异常没有行号' : ''}
                  </span>
                </span>
                {open ? (
                  <Button variant="text" size="sm" className="validation-open" onPress={() => props.onOpen(row)}>
                    {row.location ? '看这一行' : '看修订'}
                  </Button>
                ) : (
                  <span />
                )}
              </li>
            );
          })}
        </ul>
      )}
      {rows.length > issueCount && <MoreRows rest={rows.length - issueCount} unit="个问题" className="validation-more-issues" onMore={() => setIssueCount((n) => n + PAGE)} />}
      {extra.length > 0 && (
        <details className="validation-diagnostics">
          <summary>其他诊断（{extra.length} 条）</summary>
          <ul className="job-issues">
            {extra.slice(0, diagnosticCount).map((d, i) => (
              <li key={i}>{`${d.transparent ? '透明 · ' : ''}${diagnosticText(d.diagnostic)}`}</li>
            ))}
          </ul>
          {extra.length > diagnosticCount && <MoreRows rest={extra.length - diagnosticCount} unit="条诊断" className="validation-more-diagnostics" onMore={() => setDiagnosticCount((n) => n + PAGE)} />}
        </details>
      )}
    </div>
  );
}

/** 列表没显示完时：写明还剩多少，再显示一批。 */
function MoreRows(props: { rest: number; unit: string; className: string; onMore: () => void }) {
  return (
    <div className={cls('row-actions', props.className)}>
      <span className="note">还有 {props.rest} {props.unit}没有显示。</span>
      <Button variant="text" size="sm" onPress={props.onMore}>
        再显示 {Math.min(PAGE, props.rest)} {props.unit}
      </Button>
    </div>
  );
}

/** 画布停在这个草稿上时，它的样本、目标和材质部位（JSON，便于只在变化时重画）；否则空串。 */
function canvasKeyOf(state: WorkshopState | undefined, draftId: string): string {
  const source = state?.options?.source;
  if (!state || state.source !== 'authorDraft' || source?.kind !== 'authorDraft' || source.sourceId !== draftId) return '';
  return JSON.stringify({ sampleId: state.sampleId, targetKey: state.targetKey, materialSlot: state.materialSlot });
}

function conditionsText(input: { sampleId: TemplateWorkshopSampleId; targetKey: string; materialSlot?: TemplateMaterialSlot | null }): string {
  return [SAMPLE_LABEL[input.sampleId] ?? input.sampleId, targetLabel(input.targetKey), ...(input.materialSlot ? [SLOT_LABEL[input.materialSlot]] : [])].join(' · ');
}
