// 作者草稿的验证页签（W8 设计 §2.7）：运行条件（默认跟随试样画布）、固定规格、进度与取消、报告、问题与诊断、需重新验证。
// 有路径和行的问题点击后跳到源码页签的对应行；运行时异常没有行号，点击只打开验证的修订。

import { useEffect } from 'react';
import type { TemplateMaterialSlot, TemplateWorkshopSampleId } from '../../protocol';
import { JobProgressView } from '../export/RenderJobCards';
import { SLOT_LABEL, shortRevision } from '../model/full-template';
import { recordedBackendText } from '../model/render-device';
import { diagnosticText, isTerminal } from '../model/render-job';
import { formatTime } from '../model/time';
import { toast } from '../ui/legacy/feedback';
import { Select } from '../ui/legacy/fields';
import { useObservable, useObservableSelector } from '../ui/hooks';
import type { FullDraft } from './author-draft-store';
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

/** 问题列表最多显示多少条；其余只写数量。 */
const MAX_LISTED = 50;

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
  return (
    <section className="draft-validation" aria-label="验证" data-job-id={job?.jobId} data-state={job?.state} data-stale={stale ? '' : undefined}>
      <p className="hint">按固定规格逐帧检查：{AUTHOR_VALIDATION_SPEC}。渲染设备按顶栏的选择。</p>
      {source?.dirty && <p className="notice-warning">源码页签还有改动没保存成修订，本次验证只检查草稿的在用修订。</p>}
      {conditions ? (
        <div className="validation-conditions">
          <Select<TemplateWorkshopSampleId>
            label="样本"
            value={conditions.sampleId}
            options={options.samples.map((s) => ({ value: s.sampleId, label: SAMPLE_LABEL[s.sampleId] ?? s.sampleId, disabled: s.targets.length === 0 }))}
            onChange={(sampleId) => change({ sampleId })}
            disabled={running}
          />
          <Select
            label="目标"
            value={conditions.targetKey}
            options={(sample?.targets ?? []).map((t) => ({ value: t.key, label: targetLabel(t.key) }))}
            onChange={(targetKey) => change({ targetKey })}
            disabled={running}
          />
          {conditions.materialSlot && (
            <Select<TemplateMaterialSlot>
              label="材质部位"
              value={conditions.materialSlot}
              options={options.materialSlots.map((slot) => ({ value: slot, label: SLOT_LABEL[slot] }))}
              onChange={(materialSlot) => change({ materialSlot })}
              disabled={running}
            />
          )}
          {state.chosen ? (
            <mdui-button variant="text" disabled={running} onClick={() => model.choose(undefined)}>
              跟随试样画布
            </mdui-button>
          ) : (
            <span className="hint">{canvas ? '跟随试样画布' : '默认样本'}</span>
          )}
          <mdui-button variant="tonal" className="validation-start" loading={!!state.submitting} disabled={running || props.disabled} onClick={() => void model.start(conditions)}>
            {state.submitting ? '正在提交验证…' : job && !running ? '重新验证' : '开始验证'}
          </mdui-button>
        </div>
      ) : (
        <p className="notice-error">这个草稿没有可用的试样目标，不能验证。</p>
      )}
      {state.submitError && <p className="dialog-error">{state.submitError}</p>}
      {state.restoring && <p className="muted">正在读取上一次的验证…</p>}
      {!job && state.error && <p className="dialog-error">{state.error}</p>}
      {state.retryable && (
        <mdui-button variant="text" disabled={!!state.restoring || props.disabled} onClick={() => void model.retry()}>
          重新读取验证结果
        </mdui-button>
      )}
      {job && (
        <p className="hint job-device">
          {validated ? `验证的是 ${conditionsText(validated)} · 修订 ${shortRevision(validated.sourceRevision)} · ` : ''}
          渲染设备：{recordedBackendText(job.input?.renderBackend)}
        </p>
      )}
      {job && !terminal && <JobProgressView job={job} cancelling={state.cancelling} error={state.error} onCancel={() => void model.cancel()} />}
      {terminal && !state.settled && <p className="muted job-result">正在读取验证报告……</p>}
      {terminal && state.settled && <ValidationResult state={state} stale={stale} onOpen={(row) => void openIssue(row)} canOpen={!!source} />}
    </section>
  );
}

function ValidationResult(props: { state: DraftValidationModel['state']; stale: boolean; canOpen: boolean; onOpen: (row: ValidationIssueRow) => void }) {
  const { state } = props;
  const report = state.report;
  const rows = report ? validationIssueRows(report) : [];
  const extra = report ? extraDiagnostics(report) : [];
  return (
    <div className="job-result validation-result" data-outcome={report ? (report.succeeded ? 'passed' : 'failed') : undefined}>
      {props.stale && <p className="notice-warning validation-stale">草稿已经改过，结果对应旧版本，需要重新验证。</p>}
      {report ? (
        <p className={`validation-outcome ${report.succeeded ? 'job-ok' : 'dialog-error'}`}>
          {validationOutcomeText(report)} · 设备 {recordedBackendText(report.renderBackend)}
        </p>
      ) : (
        <p className={state.job?.state === 'cancelled' ? 'muted' : 'dialog-error'}>{state.error ?? '没有读到验证报告。'}</p>
      )}
      {report && state.error && <p className="dialog-error">{state.error}</p>}
      {rows.length > 0 && (
        <ul className="compile-rows validation-issues">
          {rows.slice(0, MAX_LISTED).map((row) => (
            <li key={row.key}>
              <button
                type="button"
                className="compile-row validation-issue"
                data-severity={row.isError ? 'error' : 'warning'}
                disabled={!props.canOpen || !row.script}
                title={row.script && props.canOpen ? (row.location ? '到源码页签看这一行' : '到源码页签看验证的修订（运行时异常没有行号）') : undefined}
                onClick={() => props.onOpen(row)}
              >
                {row.isError ? <mdui-icon-error className="compile-icon" /> : <mdui-icon-warning className="compile-icon" />}
                <span className="compile-location">
                  {formatTime(row.timeUs)} · {row.transparent ? '透明' : '普通'} · {ORIGIN_TEXT[row.origin]}
                </span>
                <code>{row.code}</code>
                {row.location && (
                  <span className="compile-location">
                    {row.location.path}:{row.location.line}:{row.location.column}
                  </span>
                )}
                <span className="compile-message">{row.message}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
      {rows.length > MAX_LISTED && <p className="hint">还有 {rows.length - MAX_LISTED} 个问题没有列出。</p>}
      {extra.length > 0 && (
        <details className="validation-diagnostics">
          <summary>其他诊断（{extra.length} 条）</summary>
          <ul className="job-issues">
            {extra.slice(0, MAX_LISTED).map((d, i) => (
              <li key={i}>{`${d.transparent ? '透明 · ' : ''}${diagnosticText(d.diagnostic)}`}</li>
            ))}
          </ul>
        </details>
      )}
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
