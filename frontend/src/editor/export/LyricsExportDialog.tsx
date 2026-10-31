import { useEffect, useState } from 'react';
import { BackendError } from '../../client';
import type { LyricsExportFormat, LyricsExportPlan, LyricsExportResponse } from '../../protocol';
import { client } from '../connection';
import {
  LYRICS_EXPORT_FORMAT_OPTIONS,
  checkMetadata,
  isLrc,
  lossLines,
  lyricsFileName,
  planInput,
  planSummary,
  reportSummary,
  type LyricsExportForm,
} from '../model/lyrics-export';
import { errorText, type EditorSession } from '../session/editor-session';
import { saveBlob } from '../ui/download';
import { Select, Switch, TextField } from '../ui/fields';
import { FormDialog } from '../ui/FormDialog';
import { useObservable } from '../ui/hooks';

export const LYRICS_EXPORT_FEATURE = 'project-sessions.lyrics-export-plans';

type Step = 'form' | 'plan' | 'done';

const FORMAT_SELECT = LYRICS_EXPORT_FORMAT_OPTIONS.map((o) => ({ value: o.value, label: o.label }));

/**
 * 导出歌词文件（W5 设计 §1）：选来源、格式和选项 → 评估，看会丢失什么 → 导出并下载。
 * 有损失时要勾选“接受这些损失”。评估和导出都不改工程；计划和文件在后端只保留 5 分钟。
 */
export function LyricsExportDialog(props: { session: EditorSession; open: boolean; onClose: () => void }) {
  const { session } = props;
  const state = useObservable(session.state$);
  const branches = state.branches ?? [];
  const revision = state.summary.projectRevision;
  const [form, setForm] = useState<LyricsExportForm>(() => initialForm(session));
  const [step, setStep] = useState<Step>('form');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [plan, setPlan] = useState<LyricsExportPlan>();
  const [accepted, setAccepted] = useState(false);
  const [result, setResult] = useState<LyricsExportResponse>();

  // 每次打开从第一步开始，来源默认是歌词页当前的来源。
  useEffect(() => {
    if (!props.open) return;
    setForm((f) => ({ ...f, branchId: session.state.branchId }));
    setStep('form');
    setError(undefined);
    setPlan(undefined);
    setResult(undefined);
  }, [props.open, session]);

  const update = (patch: Partial<LyricsExportForm>) => setForm((f) => ({ ...f, ...patch }));
  const branchName = (id: string) => branches.find((b) => b.id === id)?.name ?? id;
  const lrc = isLrc(form.format);
  const metadataErrors = { title: checkMetadata(form.title), artist: checkMetadata(form.artist), album: checkMetadata(form.album) };
  const needsBranch = !(form.format === 'ttml.project' && form.wholeProject);
  const formInvalid = (lrc && Object.values(metadataErrors).some(Boolean)) || (needsBranch && !form.branchId);
  const stale = !!plan && plan.projectRevision !== revision;
  const losses = plan ? lossLines(plan) : [];

  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setError(undefined);
    try {
      await action();
    } catch (e) {
      setError(failureText(e));
    } finally {
      setBusy(false);
    }
  };

  const evaluate = () =>
    run(async () => {
      const next = await client.projects.lyricsExportPlan(session.projectSessionId, revision, planInput(form));
      setPlan(next);
      setAccepted(false);
      setStep('plan');
    });

  const download = async (exported: LyricsExportResponse) => {
    const content = await client.lyricsExports.content(exported.exportId);
    if (!content.blob) throw new Error(`歌词文件没有正文（HTTP ${content.status}）。`);
    saveBlob(content.blob, lyricsFileName(content.fileName, state.summary.name, exported.format));
  };

  const exportAndDownload = () =>
    run(async () => {
      if (!plan) return;
      const exported = await client.projects.lyricsExport(session.projectSessionId, plan.projectRevision, { planId: plan.planId, acceptLosses: accepted });
      setResult(exported);
      setStep('done');
      await download(exported);
    });

  const submit = () => {
    if (step === 'form') void evaluate();
    else if (step === 'plan') void (stale ? evaluate() : exportAndDownload());
    else props.onClose();
  };

  return (
    <FormDialog
      open={props.open}
      headline="导出歌词文件"
      className="lyrics-export-dialog"
      submitText={step === 'form' ? '评估' : step === 'plan' ? (stale ? '重新评估' : '导出并下载') : '完成'}
      submitDisabled={step === 'form' ? formInvalid : step === 'plan' && !stale && !!plan?.requiresLossAcceptance && !accepted}
      busy={busy}
      onSubmit={submit}
      onClose={props.onClose}
      {...(step === 'plan'
        ? { secondaryAction: { text: '上一步', onClick: () => setStep('form') } }
        : step === 'done' && result
          ? { secondaryAction: { text: '再次下载', onClick: () => void run(() => download(result)) } }
          : {})}
    >
      {step === 'form' && (
        <div className="lyrics-export-form">
          <Select<LyricsExportFormat> label="格式" value={form.format} options={FORMAT_SELECT} onChange={(format) => update({ format })} />
          <p className="hint">{LYRICS_EXPORT_FORMAT_OPTIONS.find((o) => o.value === form.format)?.description}</p>
          {form.format === 'ttml.project' && (
            <Switch label="导出整个工程（全部来源）" checked={form.wholeProject} onChange={(wholeProject) => update({ wholeProject })} />
          )}
          {needsBranch && (
            <Select label="来源" value={form.branchId} options={branches.map((b) => ({ value: b.id, label: b.name }))} onChange={(branchId) => update({ branchId })} />
          )}
          {lrc && (
            <div className="dialog-grid">
              <TextField label="标题（可选）" value={form.title} error={metadataErrors.title} onChange={(title) => update({ title })} />
              <TextField label="歌手（可选）" value={form.artist} error={metadataErrors.artist} onChange={(artist) => update({ artist })} />
              <TextField label="专辑（可选）" value={form.album} error={metadataErrors.album} onChange={(album) => update({ album })} />
            </div>
          )}
          <div className="lyrics-export-switches">
            {lrc && <Switch label="时间精确到毫秒" checked={form.millisecondPrecision} onChange={(millisecondPrecision) => update({ millisecondPrecision })} />}
            <Switch label="Windows 换行（CRLF）" checked={form.crlf} onChange={(crlf) => update({ crlf })} />
            <Switch label="写入 BOM" checked={form.bom} onChange={(bom) => update({ bom })} />
          </div>
          {lrc && !form.millisecondPrecision && <p className="hint">LRC 通常精确到 1/100 秒，更细的时间会被舍入。</p>}
        </div>
      )}
      {step === 'plan' && plan && (
        <div className="lyrics-export-plan">
          <p>{planSummary(plan, branchName)}</p>
          {stale && <p className="dialog-error">工程在评估之后又改了，需要重新评估。</p>}
          {losses.length > 0 ? (
            <>
              <p className="section-title">会丢失的内容</p>
              <ul className="job-issues">
                {losses.map((line, i) => (
                  <li key={i}>{line}</li>
                ))}
              </ul>
            </>
          ) : (
            <p className="job-ok">没有损失。</p>
          )}
          {plan.plan.issues.length > 0 && (
            <>
              <p className="section-title">问题</p>
              <ul className="job-issues">
                {plan.plan.issues.map((issue, i) => (
                  <li key={i}>{issue.message}</li>
                ))}
              </ul>
            </>
          )}
          {plan.requiresLossAcceptance && !stale && (
            <mdui-checkbox checked={accepted} onClick={() => setAccepted((a) => !a)}>
              接受这些损失
            </mdui-checkbox>
          )}
        </div>
      )}
      {step === 'done' && result && (
        <div className="lyrics-export-done">
          <p className="job-ok">{reportSummary(result.report, result.sizeBytes)}</p>
          <p className="hint">文件已开始下载。后端只保留 5 分钟，需要时可以再次下载。</p>
        </div>
      )}
      {error && <p className="dialog-error">{error}</p>}
    </FormDialog>
  );
}

function initialForm(session: EditorSession): LyricsExportForm {
  return {
    format: 'lrc.enhanced',
    wholeProject: true,
    branchId: session.state.branchId,
    title: '',
    artist: '',
    album: '',
    crlf: false,
    bom: false,
    millisecondPrecision: false,
  };
}

function failureText(error: unknown): string {
  if (error instanceof BackendError) {
    if (error.code === 'revisionConflict') return '工程在评估之后又改了，请重新评估。';
    if (error.status === 404 || error.status === 410) return '评估或文件已过期（只保留 5 分钟），请重新评估。';
  }
  return errorText(error);
}
