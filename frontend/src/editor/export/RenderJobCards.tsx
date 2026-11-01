import { useState } from 'react';
import type { JobResponse } from '../../protocol';
import { defaultValidationFrames } from '../model/export-readiness';
import { diagnosticText, downloadName, exportReportFacts, isTerminal, progressFraction, progressText, validationIssueText } from '../model/render-job';
import { recordedBackendText } from '../model/render-device';
import { errorText } from '../session/editor-session';
import { saveBlob } from '../ui/download';
import { NumberField } from '../ui/legacy/fields';
import type { ExportState, ExportStore, JobTrack } from './export-store';

/** 问题列表最多显示多少条；完整报告里的其余条目只写数量。 */
const MAX_LISTED = 50;

/** 正式验证（可选）：从 0 时刻起逐帧检查开头 N 帧，普通与透明各一遍。 */
export function ValidationCard(props: { store: ExportStore; exportState: ExportState; revision: string; frames: number | undefined }) {
  const { store, exportState } = props;
  const caps = exportState.validationCapabilities;
  const track = exportState.validation;
  const running = !!track && (!isTerminal(track.job.state) || !track.settled);
  const max = caps && props.frames !== undefined ? Math.min(props.frames, caps.maxFramesPerOutput) : undefined;
  const frames = max !== undefined ? Math.min(exportState.validationFrames ?? defaultValidationFrames(props.frames!, caps!.maxFramesPerOutput), max) : undefined;
  const unavailable = caps === null ? '读取验证能力失败。' : caps && !caps.enabled ? (caps.disabledReason?.message ?? '后端的正式验证不可用。') : undefined;
  const submitError = exportState.submitError?.kind === 'validation' ? exportState.submitError.text : undefined;

  return (
    <section className="export-card" aria-label="正式验证">
      <h2>正式验证</h2>
      <p className="hint export-card-intro">可选。用和导出相同的渲染逐帧检查开头一段，普通和透明画面各一遍，能更早发现渲染问题。</p>
      {unavailable ? (
        <p className="dialog-error">{unavailable}</p>
      ) : caps === undefined ? (
        <p className="muted">正在读取验证能力……</p>
      ) : (
        <div className="job-start">
          {frames !== undefined && max !== undefined ? (
            <NumberField
              label="检查帧数"
              value={frames}
              min={1}
              max={max}
              integer
              disabled={running}
              helper={`每种画面最多 ${max} 帧`}
              onCommit={(v) => store.setValidationFrames(v)}
            />
          ) : (
            <p className="muted">输出时长确定后才能验证。</p>
          )}
          <mdui-button variant="tonal" disabled={running || frames === undefined} onClick={() => frames !== undefined && void store.startValidation(frames)}>
            {track && !running ? '重新验证' : '开始验证'}
          </mdui-button>
        </div>
      )}
      {submitError && <p className="dialog-error">{submitError}</p>}
      {track && <JobDevice track={track} />}
      {track && <ValidationResult track={track} revision={props.revision} onCancel={() => void store.cancel('validation')} />}
    </section>
  );
}

function ValidationResult(props: { track: JobTrack; revision: string; onCancel: () => void }) {
  const { track } = props;
  const { job } = track;
  if (!isTerminal(job.state)) return <JobProgressView job={track.job} cancelling={track.cancelling} error={track.error} onCancel={props.onCancel} />;
  if (!track.settled) return <p className="muted job-result">正在读取验证报告……</p>;
  const report = track.validationReport;
  const issues = report?.issues ?? [];
  const diagnostics = report?.diagnostics ?? [];
  const lines = [...issues.map(validationIssueText), ...diagnostics.map((d) => `${d.transparent ? '透明 · ' : ''}${diagnosticText(d.diagnostic)}`)];
  return (
    <div className="job-result" data-state={job.state}>
      <StaleNote revision={props.revision} sourceRevision={job.input?.sourceRevision} />
      {report ? (
        <p className={report.succeeded ? 'job-ok' : 'dialog-error'}>
          {report.succeeded
            ? `验证通过：普通 ${report.opaqueFrames} 帧、透明 ${report.transparentFrames} 帧都没有问题。`
            : `验证没有通过：${issues.length} 个问题${diagnostics.length ? `，${diagnostics.length} 条诊断` : ''}。`}
        </p>
      ) : (
        <p className={job.state === 'cancelled' ? 'muted' : 'dialog-error'}>{track.error ?? '没有读到验证报告。'}</p>
      )}
      {report && track.error && <p className="dialog-error">{track.error}</p>}
      <IssueList lines={lines} />
    </div>
  );
}

/** 导出任务：开始、进度、取消、报告与下载、失败原因与诊断。 */
export function ExportJobCard(props: { store: ExportStore; exportState: ExportState; revision: string; blocked: boolean; includeAudio: boolean; projectName: string }) {
  const { store, exportState } = props;
  const track = exportState.exportJob;
  const running = !!track && (!isTerminal(track.job.state) || !track.settled);
  const submitError = exportState.submitError?.kind === 'export' ? exportState.submitError.text : undefined;

  return (
    <section className="export-card" aria-label="导出">
      <h2>导出</h2>
      <div className="job-start">
        <mdui-button variant="filled" disabled={props.blocked || running || !exportState.capabilities?.enabled} onClick={() => void store.startExport(props.includeAudio)}>
          <mdui-icon-movie slot="icon" />
          {track && !running ? '重新导出' : '开始导出'}
        </mdui-button>
        {props.blocked && <span className="hint">准备检查里还有阻断项。</span>}
      </div>
      {submitError && <p className="dialog-error">{submitError}</p>}
      {track && <JobDevice track={track} />}
      {track && <ExportResult track={track} store={store} revision={props.revision} projectName={props.projectName} />}
    </section>
  );
}

function ExportResult(props: { track: JobTrack; store: ExportStore; revision: string; projectName: string }) {
  const { track, store } = props;
  const { job } = track;
  const [downloading, setDownloading] = useState(false);
  const [downloadError, setDownloadError] = useState<string>();
  if (!isTerminal(job.state)) return <JobProgressView job={track.job} cancelling={track.cancelling} error={track.error} onCancel={() => void store.cancel('export')} />;
  if (!track.settled) return <p className="muted job-result">正在读取导出报告……</p>;

  const download = async () => {
    setDownloading(true);
    setDownloadError(undefined);
    try {
      const content = await store.download();
      saveBlob(content.blob, downloadName(content.fileName, props.projectName, content.contentType));
    } catch (error) {
      setDownloadError(errorText(error));
    } finally {
      setDownloading(false);
    }
  };

  const report = track.exportReport;
  const diagnostics = track.failureReport?.diagnostics ?? [];
  return (
    <div className="job-result" data-state={job.state}>
      <StaleNote revision={props.revision} sourceRevision={job.input?.sourceRevision} />
      {report ? (
        <>
          <p className="job-ok">导出完成。成片在后端保留 1 小时，请及时下载。</p>
          <dl className="settings-facts export-facts">
            {exportReportFacts(report).map((fact) => (
              <div key={fact.label}>
                <dt>{fact.label}</dt>
                <dd>{fact.value}</dd>
              </div>
            ))}
          </dl>
          <mdui-button variant="tonal" loading={downloading} disabled={downloading} onClick={() => void download()}>
            <mdui-icon-download slot="icon" />
            下载
          </mdui-button>
          {downloadError && <p className="dialog-error">{downloadError}</p>}
        </>
      ) : (
        <p className={job.state === 'cancelled' ? 'muted job-cancelled' : 'dialog-error'}>{track.error ?? '没有读到导出报告。'}</p>
      )}
      <IssueList lines={diagnostics.map(diagnosticText)} />
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
      {fraction !== undefined ? <mdui-linear-progress value={fraction} max={1} /> : <mdui-linear-progress />}
      <div className="job-progress-row">
        <span className="job-progress-text">{progressText({ ...job, cancellationRequested: cancelling })}</span>
        <mdui-button variant="text" disabled={cancelling || !job.canCancel} onClick={props.onCancel}>
          取消
        </mdui-button>
      </div>
      {!job.canCancel && !cancelling && <p className="hint">正在发布结果，已经不能取消。</p>}
      {props.error && <p className="dialog-error">{props.error}</p>}
    </div>
  );
}

/** 任务在接受时固定的渲染设备（渲染设备设计 §2.4）；之后改偏好不影响它。没有记录时写“未记录”。 */
function JobDevice(props: { track: JobTrack }) {
  return <p className="hint job-device">渲染设备：{recordedBackendText(props.track.job.input?.renderBackend)}</p>;
}

/** 任务固定在接受时的修订上：之后又改了就写明。 */
function StaleNote(props: { revision: string; sourceRevision: string | undefined }) {
  if (!props.sourceRevision || props.sourceRevision === props.revision) return null;
  return <p className="hint">这是修订 {props.sourceRevision} 的结果，之后又有修改。</p>;
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
      {props.lines.length > MAX_LISTED && <p className="hint">还有 {props.lines.length - MAX_LISTED} 条没有列出。</p>}
    </>
  );
}
