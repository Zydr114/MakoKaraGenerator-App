import type { ReactNode } from 'react';
import type { ExportReport, JobResponse } from '../../protocol';
import { backgroundFallbackLine, exportFallbackText } from '../model/background-fallback';
import { defaultValidationFrames } from '../model/export-readiness';
import { diagnosticText, exportReportFacts, isTerminal, progressFraction, progressText, validationIssueText } from '../model/render-job';
import { recordedBackendText } from '../model/render-device';
import { Button } from '../ui/Button';
import { NumberField } from '../ui/fields';
import { Icon } from '../ui/Icon';
import { ProgressBar } from '../ui/indicators';
import { Group, Note } from '../workspace/panel';
import type { ExportState, ExportStore, JobTrack } from './export-store';
import './export.css';

/** 问题列表最多显示多少条；完整报告里的其余条目只写数量。 */
const MAX_LISTED = 50;

/** 任务还在进行：没到终态，或终态的报告还没读完。 */
export function jobRunning(track: JobTrack | undefined): boolean {
  return !!track && (!isTerminal(track.job.state) || !track.settled);
}

/** 正式验证（可选）：从 0 时刻起逐帧检查开头 N 帧，普通与透明各一遍。 */
export function ValidationCard(props: { store: ExportStore; exportState: ExportState; revision: string; frames: number | undefined }) {
  const { store, exportState } = props;
  const caps = exportState.validationCapabilities;
  const track = exportState.validation;
  const submitting = exportState.submitting?.validation;
  const running = jobRunning(track) || !!submitting;
  const max = caps && props.frames !== undefined ? Math.min(props.frames, caps.maxFramesPerOutput) : undefined;
  const frames = max !== undefined ? Math.min(exportState.validationFrames ?? defaultValidationFrames(props.frames!, caps!.maxFramesPerOutput), max) : undefined;
  const unavailable = caps === null ? '读取验证能力失败。' : caps && !caps.enabled ? (caps.disabledReason?.message ?? '后端的正式验证不可用。') : undefined;
  const submitError = exportState.submitError?.kind === 'validation' ? exportState.submitError.text : undefined;

  return (
    <Group title="正式验证" hint="可选">
      <Note>用和导出相同的渲染逐帧检查开头一段，普通和透明画面各一遍；完整成片与音轨编码仍需在导出时检查。</Note>
      {unavailable ? (
        <Note error>{unavailable}</Note>
      ) : caps === undefined ? (
        <Note>正在读取验证能力……</Note>
      ) : frames !== undefined && max !== undefined ? (
        <div className="job-start">
          <NumberField
            label="检查帧数"
            labelPlacement="side"
            className="job-frames"
            value={frames}
            min={1}
            max={max}
            integer
            suffix="帧"
            disabled={running}
            helper={`每种画面最多 ${max} 帧`}
            onCommit={(v) => store.setValidationFrames(v)}
          />
          <Button variant="tonal" icon="fact_check" isPending={!!submitting} isDisabled={running} onPress={() => void store.startValidation(frames)}>
            {submitting === 'reconciling'
              ? '正在核对状态……'
              : submitting === 'submitting'
                ? '正在提交……'
                : track && !running
                  ? '重新验证'
                  : '开始验证'}
          </Button>
        </div>
      ) : (
        <Note>输出时长确定后才能验证。</Note>
      )}
      {submitting && (
        <Note>
          {submitting === 'reconciling' ? '提交结果未知，正在向后端核对验证任务状态……' : '正在提交验证任务……'}
        </Note>
      )}
      {submitError && <Note error>{submitError}</Note>}
      {track && (
        <div className="job">
          <JobDevice track={track} />
          <ValidationResult track={track} revision={props.revision} onCancel={() => void store.cancel('validation')} />
        </div>
      )}
    </Group>
  );
}

function ValidationResult(props: { track: JobTrack; revision: string; onCancel: () => void }) {
  const { track } = props;
  const { job } = track;
  if (!isTerminal(job.state)) return <JobProgressView job={track.job} cancelling={track.cancelling} error={track.error} onCancel={props.onCancel} />;
  if (!track.settled) return <Note className="job-result">正在读取验证报告……</Note>;
  const report = track.validationReport;
  const issues = report?.issues ?? [];
  const diagnostics = report?.diagnostics ?? [];
  const lines = [...issues.map(validationIssueText), ...diagnostics.map((d) => `${d.transparent ? '透明 · ' : ''}${diagnosticText(d.diagnostic)}`)];
  return (
    <div className="job-result" data-state={job.state}>
      <StaleNote revision={props.revision} sourceRevision={job.input?.sourceRevision} />
      {report ? (
        report.succeeded ? (
          <OkLine>
            验证通过：普通 {report.opaqueFrames} 帧、透明 {report.transparentFrames} 帧都没有问题。
          </OkLine>
        ) : (
          <Note error>
            验证没有通过：{issues.length} 个问题{diagnostics.length ? `，${diagnostics.length} 条诊断` : ''}。
          </Note>
        )
      ) : (
        <Note error={job.state !== 'cancelled'}>{track.error ?? '没有读到验证报告。'}</Note>
      )}
      {report && track.error && <Note error>{track.error}</Note>}
      <IssueList lines={lines} />
    </div>
  );
}

/** 导出任务：进度、取消、报告、失败原因与诊断；开始、下载与重新导出操作在导出页底部。还没有任务时不显示。 */
export function ExportJobCard(props: { store: ExportStore; exportState: ExportState; revision: string }) {
  const { store, exportState } = props;
  const track = exportState.exportJob;
  const submitting = exportState.submitting?.export;
  const submitError = exportState.submitError?.kind === 'export' ? exportState.submitError.text : undefined;
  if (!track && !submitError && !submitting) return null;
  return (
    <Group title="导出任务">
      {submitting && (
        <Note>
          {submitting === 'reconciling' ? '提交结果未知，正在向后端核对导出任务状态……' : '正在提交导出任务，准备工程与媒体输入……'}
        </Note>
      )}
      {submitError && <Note error>{submitError}</Note>}
      {track && (
        <div className="job">
          <JobDevice track={track} />
          <ExportResult track={track} store={store} revision={props.revision} />
        </div>
      )}
    </Group>
  );
}

function ExportResult(props: { track: JobTrack; store: ExportStore; revision: string }) {
  const { track, store } = props;
  const { job } = track;
  if (!isTerminal(job.state)) return <JobProgressView job={track.job} cancelling={track.cancelling} error={track.error} onCancel={() => void store.cancel('export')} />;
  if (!track.settled) return <Note className="job-result">正在读取导出报告……</Note>;

  const report = track.exportReport;
  const diagnostics = track.failureReport?.diagnostics ?? [];
  return (
    <div className="job-result" data-state={job.state}>
      <StaleNote revision={props.revision} sourceRevision={job.input?.sourceRevision} />
      {report ? (
        <>
          <OkLine>导出完成，成片在后端保留 1 小时，请及时下载。</OkLine>
          <dl className="dl export-facts">
            {exportReportFacts(report).map((fact) => (
              <div key={fact.label}>
                <dt>{fact.label}</dt>
                <dd>{fact.value}</dd>
              </div>
            ))}
          </dl>
          <ExportFallback report={report} />
        </>
      ) : (
        <Note error={job.state !== 'cancelled'} {...(job.state === 'cancelled' ? { className: 'job-cancelled' } : {})}>
          {track.error ?? '没有读到导出报告。'}
        </Note>
      )}
      <IssueList lines={diagnostics.map(diagnosticText)} />
    </div>
  );
}

/** 导出用了缺背景兜底：摘要加逐条（最多 MAX_LISTED 条），只提示，成片照常可用。 */
function ExportFallback(props: { report: ExportReport }) {
  const text = exportFallbackText(props.report);
  if (!text) return null;
  return (
    <div className="export-fallback" role="status">
      <p className="hint notice-warning">{text}</p>
      <IssueList lines={(props.report.fallbackDiagnostics ?? []).map(backgroundFallbackLine)} />
    </div>
  );
}

/** 任务进度与取消（作者验证页签也用）。 */
export function JobProgressView(props: { job: JobResponse; cancelling: boolean; error?: string | undefined; onCancel: () => void }) {
  const { job } = props;
  const cancelling = props.cancelling || job.cancellationRequested;
  const fraction = progressFraction(job.progress);
  return (
    <div className="job-progress" data-state={job.state}>
      <div className="job-progress-row">
        <span className="job-progress-text">{progressText({ ...job, cancellationRequested: cancelling })}</span>
        <Button variant="text" size="sm" isDisabled={cancelling || !job.canCancel} onPress={props.onCancel}>
          取消
        </Button>
      </div>
      <ProgressBar label="任务进度" value={fraction !== undefined ? fraction * 100 : undefined} />
      {!job.canCancel && !cancelling && <Note>正在发布结果，已经不能取消。</Note>}
      {props.error && <Note error>{props.error}</Note>}
    </div>
  );
}

function OkLine(props: { children: ReactNode }) {
  return (
    <p className="job-ok">
      <Icon name="check_circle" />
      <span>{props.children}</span>
    </p>
  );
}

/** 任务在接受时固定的渲染设备（渲染设备设计 §2.4）；之后改偏好不影响它。没有记录时写“未记录”。 */
function JobDevice(props: { track: JobTrack }) {
  return <p className="job-device">渲染设备：{recordedBackendText(props.track.job.input?.renderBackend)}</p>;
}

/** 任务固定在接受时的修订上：之后又改了就写明。 */
function StaleNote(props: { revision: string; sourceRevision: string | undefined }) {
  if (!props.sourceRevision || props.sourceRevision === props.revision) return null;
  return <Note>这是修订 {props.sourceRevision} 的结果，之后又有修改。</Note>;
}

function IssueList(props: { lines: readonly string[] }) {
  if (props.lines.length === 0) return null;
  return (
    <>
      <ul className="job-issues">
        {props.lines.slice(0, MAX_LISTED).map((line, index) => (
          <li key={index}>{line}</li>
        ))}
      </ul>
      {props.lines.length > MAX_LISTED && <Note>还有 {props.lines.length - MAX_LISTED} 条没有列出。</Note>}
    </>
  );
}
