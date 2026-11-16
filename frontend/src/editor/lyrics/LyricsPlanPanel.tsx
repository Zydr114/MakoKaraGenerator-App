import { useEffect, useMemo, useState } from 'react';
import type { LyricsImportOptions, LyricsImportPlan } from '../../protocol';
import { client } from '../connection';
import { chosen, encodingChoices, formatChoices, planErrorText, sourceChoices } from '../model/lyrics-import';
import { errorText } from '../session/editor-session';
import { Select } from '../ui/fields';

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
 * 导入文件时对识别结果的改选（W6 设计 §2.3）：多来源文件选导入哪个来源，以及格式、编码。
 * 识别结果本身（概要、提示、前几行）在对话框右边的 LyricsCheck；新建工程只在识别有疑问时给出格式、编码，在它自己的表单里。
 */
export function PlanChoiceFields(props: { plan: LyricsImportPlan | undefined; choices: PlanChoices; onChange: (choices: PlanChoices) => void; disabled?: boolean; hideSource?: boolean }) {
  const { plan, choices } = props;
  if (!plan) return null;
  const set = (patch: Partial<PlanChoices>) => props.onChange({ ...choices, ...patch });
  const disabled = !!props.disabled;
  return (
    <>
      {plan.sourceBranches.length > 1 && !props.hideSource && (
        <Select
          label="导入哪个来源"
          value={chosen(choices.source) ?? plan.selectedSourceBranchId ?? plan.sourceBranches[0]!.id}
          options={sourceChoices(plan)}
          disabled={disabled}
          onChange={(source) => set({ source })}
        />
      )}
      <div className="g2">
        <Select label="格式" value={choices.importer} options={formatChoices(plan)} disabled={disabled} onChange={(importer) => set({ importer })} />
        <Select label="编码" value={choices.encoding} options={encodingChoices(plan)} disabled={disabled} onChange={(encoding) => set({ encoding })} />
      </div>
    </>
  );
}
