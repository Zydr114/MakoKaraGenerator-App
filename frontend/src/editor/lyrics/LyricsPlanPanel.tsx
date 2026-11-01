import { useEffect, useMemo, useState } from 'react';
import type { LyricsImportOptions, LyricsImportPlan } from '../../protocol';
import { client } from '../connection';
import {
  chosen,
  encodingChoices,
  formatChoices,
  needsFormatChoice,
  planErrorText,
  planNotes,
  planSummary,
  previewRows,
  sourceChoices,
  type PlanPurpose,
} from '../model/lyrics-import';
import { errorText } from '../session/editor-session';
import { Select } from '../ui/legacy/fields';

/** 用户在识别结果下的改选；AUTO 表示沿用后端的识别或默认来源。 */
export interface PlanChoices {
  importer: string;
  encoding: string;
  source: string;
}

export interface LyricsPlanState {
  assetId?: string;
  plan?: LyricsImportPlan;
  loading: boolean;
  error?: string;
}

/** 改选转成请求选项：只带用户改过的项。 */
export function planOptions(choices: PlanChoices): LyricsImportOptions {
  const importerId = chosen(choices.importer);
  const encodingName = chosen(choices.encoding);
  const sourceBranchId = chosen(choices.source);
  return { ...(importerId ? { importerId } : {}), ...(encodingName ? { encodingName } : {}), ...(sourceBranchId ? { sourceBranchId } : {}) };
}

/**
 * 选了歌词文件就上传并做导入规划（只读，不改任何工程）；改选格式、编码或来源时用同一个资源重新规划，不重新上传。
 * 重新规划期间仍显示同一文件上一次的结果（改选用的下拉框不会消失），loading 为 true；换文件或关闭对话框时丢弃。
 */
export function useLyricsPlan(file: File | null, choices: PlanChoices): LyricsPlanState {
  const [assetId, setAssetId] = useState<string>();
  const [uploadError, setUploadError] = useState<string>();
  const [plan, setPlan] = useState<{ key: string; assetId: string; plan?: LyricsImportPlan; error?: string }>();
  const { importer, encoding, source } = choices;
  const options = useMemo(() => planOptions({ importer, encoding, source }), [importer, encoding, source]);

  useEffect(() => {
    setAssetId(undefined);
    setUploadError(undefined);
    setPlan(undefined);
    if (!file) return;
    let active = true;
    client.assets.upload(file, { fileName: file.name }).then(
      (asset) => active && setAssetId(asset.assetId),
      (error: unknown) => active && setUploadError(`上传失败：${errorText(error)}`),
    );
    return () => {
      active = false;
    };
  }, [file]);

  const key = `${assetId}|${importer}|${encoding}|${source}`;
  useEffect(() => {
    if (!assetId) return;
    const abort = new AbortController();
    client.assets.lyricsImportPlan({ assetId, ...(Object.keys(options).length ? { options } : {}) }, abort.signal).then(
      (result) => !abort.signal.aborted && setPlan({ key, assetId, plan: result }),
      (error: unknown) => !abort.signal.aborted && setPlan({ key, assetId, error: `识别失败：${errorText(error)}` }),
    );
    return () => abort.abort();
  }, [assetId, options, key]);

  if (!file) return { loading: false };
  if (uploadError) return { loading: false, error: uploadError };
  const current = assetId && plan?.assetId === assetId ? plan : undefined;
  return {
    loading: plan?.key !== key,
    ...(assetId ? { assetId } : {}),
    ...(current?.plan ? { plan: current.plan } : {}),
    ...(current?.error ? { error: current.error } : {}),
  };
}

/** 规划可以用来导入：识别成功、资源已上传，没有正在进行的重新识别。 */
export function planReady(state: LyricsPlanState): boolean {
  return !state.loading && !!state.assetId && !!state.plan && !planErrorText(state.plan);
}

/**
 * 识别结果（W5 设计 §2.6，W6 设计 §2.3）：概要、提示、改选（来源、格式、编码）和前几行预览。
 * 新建工程只在识别有疑问时给出格式、编码改选，也不选来源（全部恢复）；导入文件总是给出。
 */
export function LyricsPlanPanel(props: {
  state: LyricsPlanState;
  purpose: PlanPurpose;
  choices: PlanChoices;
  onChange: (choices: PlanChoices) => void;
  previewCount: number;
  disabled?: boolean;
}) {
  const { state, purpose, choices } = props;
  const plan = state.plan;
  const planError = plan && planErrorText(plan);
  const rows = plan && !planError ? previewRows(plan, props.previewCount) : [];
  const set = (patch: Partial<PlanChoices>) => props.onChange({ ...choices, ...patch });
  const showSources = purpose === 'import' && !!plan && plan.sourceBranches.length > 1;
  const showFormats = !!plan && (purpose === 'import' || needsFormatChoice(plan));

  return (
    <div className="lyrics-plan" aria-live="polite">
      {state.loading && <p className="muted">正在识别歌词文件……</p>}
      {state.error && <p className="dialog-error">{state.error}</p>}
      {plan && planError && <p className="dialog-error">{planError}</p>}
      {plan && !planError && <p className="lyrics-plan-summary">{planSummary(plan)}</p>}
      {plan &&
        planNotes(plan, purpose).map((note) => (
          <p key={note} className="hint">
            {note}
          </p>
        ))}
      {showSources && (
        <Select
          label="导入哪个来源"
          value={chosen(choices.source) ?? plan.selectedSourceBranchId ?? plan.sourceBranches[0]!.id}
          options={sourceChoices(plan)}
          disabled={!!props.disabled}
          onChange={(source) => set({ source })}
          className="plan-source"
        />
      )}
      {showFormats && (
        <div className="dialog-grid">
          <Select label="格式" value={choices.importer} options={formatChoices(plan)} disabled={!!props.disabled} onChange={(importer) => set({ importer })} />
          <Select label="编码" value={choices.encoding} options={encodingChoices(plan)} disabled={!!props.disabled} onChange={(encoding) => set({ encoding })} />
        </div>
      )}
      {plan && rows.length > 0 && (
        <ol className="lyrics-plan-preview">
          {rows.map((row) => (
            <li key={row.id}>
              <span className="plan-line-text">{row.text}</span>
              {row.ruby && <span className="plan-line-ruby">{row.ruby}</span>}
              <span className="plan-line-timing">{row.timing}</span>
            </li>
          ))}
          {plan.previewLines.length > rows.length && <li className="muted">……</li>}
        </ol>
      )}
    </div>
  );
}
