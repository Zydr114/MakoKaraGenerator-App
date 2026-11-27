import { useEffect, useRef, useState } from 'react';
import type { ImportLyricsDestination, LyricsImportReport, TokenSplitRuleName } from '../../protocol';
import { BRANCH_ROLE_TEXT } from '../model/branches';
import { AUTO, LYRICS_FILE_ACCEPT, NEW_BRANCH_ROLES, chosen, fileStem, pastedLineCount, primaryImportBlock, recommendedSplitRule, type NewBranchRole } from '../model/lyrics-import';
import { SPLIT_RULES } from '../model/split-rules';
import { LyricsCheck } from '../home/FileCheck';
import { type EditorSession, type EditorState } from '../session/editor-session';
import { Switch } from '../ui/choices';
import { toast } from '../ui/feedback';
import { FileBox, Select, TextField } from '../ui/fields';
import { FormDialog } from '../ui/FormDialog';
import { ProgressBar } from '../ui/indicators';
import { PlanChoiceFields, planReady, useLyricsPlan, type PlanChoices } from './LyricsPlanPanel';

/** 目标选择里“新来源”的取值；其余取值是来源 ID。 */
const NEW_TARGET = '__new__';
/** 把解析出的全部分支追加进来，不替换已有来源。 */
const ALL_SOURCES = '__all__';

/** 一次文件导入的报告；保留文件本身，报告里可以对照源文件的行。 */
export interface ImportedReport {
  report: LyricsImportReport;
  file: File;
  branchName: string;
}

/** 打开时的改选：都沿用识别结果。 */
const NO_CHOICES: PlanChoices = { importer: AUTO, encoding: AUTO, source: AUTO };

/**
 * 导入文件（整体设计 §5.2，W6 设计 §2.3）：选文件 → 上传并做导入规划 → 右边显示识别结果与前几行，左边改选 → 选目标与选项 → 导入。
 * 文件只上传一次，改选格式、编码或来源时用同一个资源重新规划；导入发 lyrics.importAsset，整次导入一条历史，完成后显示导入报告。
 * 多来源文件默认仍导入所选的一个来源。选择“导入全部来源”时发 destination.kind=allSources，
 * 不带 sourceBranchId，后端把全部分支追加为新来源，并记成一条可以一次撤销的历史。
 */
export function ImportLyricsDialog(props: {
  session: EditorSession;
  state: EditorState;
  open: boolean;
  onClose: () => void;
  onImported: (imported: ImportedReport) => void;
}) {
  const { session, state } = props;
  const branches = state.branches ?? [];
  const [file, setFile] = useState<File | null>(null);
  const [choices, setChoices] = useState(NO_CHOICES);
  const [target, setTarget] = useState(state.branchId);
  const [name, setName] = useState('');
  const nameEdited = useRef(false);
  const [role, setRole] = useState<NewBranchRole>('translation');
  const [split, setSplit] = useState<TokenSplitRuleName>('mixed');
  const [estimate, setEstimate] = useState(false);
  const splitEdited = useRef(false);
  const [progress, setProgress] = useState<string | null>(null);
  const [error, setError] = useState<string>();
  const lyrics = useLyricsPlan(props.open ? file : null, choices);
  const plan = lyrics.plan;

  useEffect(() => {
    if (!props.open) return;
    setFile(null);
    setChoices(NO_CHOICES);
    setTarget(session.state.branchId);
    setName('');
    nameEdited.current = false;
    setSplit('mixed');
    setEstimate(false);
    splitEdited.current = false;
    setError(undefined);
  }, [props.open, session]);

  const chooseFile = (next: File | null) => {
    setFile(next);
    setChoices(NO_CHOICES);
    splitEdited.current = false;
    if (!next) {
      setSplit('mixed');
      setEstimate(false);
    }
    setError(undefined);
    if (next && !nameEdited.current) setName(fileStem(next.name));
  };

  useEffect(() => {
    if (!plan || splitEdited.current) return;
    const next = recommendedSplitRule(plan);
    setSplit(next);
    if (next === 'none') setEstimate(false);
  }, [plan]);

  // 多来源文件：新来源的名称和角色默认跟所选来源一致。
  const source = plan && plan.sourceBranches.length > 1 ? plan.sourceBranches.find((b) => b.id === plan.selectedSourceBranchId) : undefined;
  useEffect(() => {
    if (!source) return;
    if (!nameEdited.current) setName(source.name);
    if ((NEW_BRANCH_ROLES as readonly string[]).includes(source.role)) setRole(source.role as NewBranchRole);
  }, [source?.id]);

  const targetOptions = [
    ...branches.map((b) => ({ value: b.id, label: `替换「${b.name}」` })),
    { value: NEW_TARGET, label: '作为新来源' },
    ...(plan && plan.sourceBranches.length > 1 ? [{ value: ALL_SOURCES, label: '导入全部来源（不覆盖已有来源）' }] : []),
  ];
  const effectiveTarget = targetOptions.some((option) => option.value === target) ? target : branches[0]?.id ?? NEW_TARGET;
  useEffect(() => {
    if (target === ALL_SOURCES && !(plan && plan.sourceBranches.length > 1)) {
      setTarget(branches[0]?.id ?? NEW_TARGET);
    }
  }, [target, plan?.sourceBranches.length, branches[0]?.id]);

  const isNew = effectiveTarget === NEW_TARGET;
  const allSources = effectiveTarget === ALL_SOURCES;
  const replaced = branches.find((b) => b.id === effectiveTarget);
  const newName = name.trim();
  const blocked = plan && !allSources && primaryImportBlock(plan, !isNew && effectiveTarget === state.primaryBranchId);
  const ready = planReady(lyrics) && !blocked && (allSources || (isNew ? !!newName : !!replaced));

  const submit = async () => {
    if (!file || !lyrics.assetId || !ready || progress) return;
    setError(undefined);
    setProgress('正在导入');
    try {
      const destination: ImportLyricsDestination = allSources
        ? { kind: 'allSources' }
        : isNew
          ? { kind: 'newBranch', name: newName, role }
          : { kind: 'existing', branchId: effectiveTarget };
      const response = await session.command({
        type: 'lyrics.importAsset',
        assetId: lyrics.assetId,
        destination,
        options: {
          importerId: chosen(choices.importer) ?? null,
          encodingName: chosen(choices.encoding) ?? null,
          splitRule: split,
          estimateSplitTimes: split === 'none' ? false : estimate,
          ...(!allSources && source ? { sourceBranchId: source.id } : {}),
        },
      });
      const result = response?.importResult;
      if (!result) {
        setError(session.state.notice?.message ?? '导入没有成功。');
        return;
      }
      const branchName = allSources ? '全部来源' : isNew ? newName : (replaced?.name ?? '');
      toast(allSources ? `已导入 ${result.lineCount} 行，全部来源记为一条撤销` : `已导入 ${result.lineCount} 行到「${branchName}」`);
      props.onClose();
      if (result.report) props.onImported({ report: result.report, file, branchName });
    } finally {
      setProgress(null);
    }
  };

  return (
    <FormDialog
      open={props.open}
      headline="导入歌词文件"
      description="支持 LRC、增强 LRC、纯文本、SRT、WebVTT、ASS、TTML、UltraStar。导入整体记一条历史，可以撤销。"
      submitText="导入"
      submitDisabled={!ready}
      busy={!!progress}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="import-dialog"
      side={
        <div className="file-check">
          <h3 className="file-check-head">识别结果</h3>
          <LyricsCheck lyrics={file} plan={lyrics} purpose="import" />
        </div>
      }
    >
      <FileBox
        label="歌词文件"
        icon="description"
        accept={LYRICS_FILE_ACCEPT}
        file={file}
        emptyText="点“选择…”，或把文件拖到这里"
        rejectText={(f) => `“${f.name}”不是支持的歌词文件。`}
        disabled={!!progress}
        onChange={chooseFile}
      />
      <PlanChoiceFields plan={plan} choices={choices} onChange={setChoices} disabled={!!progress} hideSource={allSources} />
      <div className="g2">
        <Select
          label="导入到"
          value={effectiveTarget}
          options={targetOptions}
          disabled={!!progress}
          onChange={(value) => {
            setTarget(value);
            if (value === ALL_SOURCES) setChoices((current) => ({ ...current, source: AUTO }));
          }}
        />
        {isNew && !allSources && (
          <Select
            label="新来源的角色"
            value={role}
            options={NEW_BRANCH_ROLES.map((r) => ({ value: r, label: BRANCH_ROLE_TEXT[r] }))}
            disabled={!!progress}
            onChange={setRole}
          />
        )}
      </div>
      {allSources ? (
        <p className="hint import-replace">
          文件里的 {plan?.sourceBranches.length ?? 0} 个来源会追加为新来源。已有来源保持不变，包括主歌词。这次导入只记一条历史，撤销会把它们一起拿掉。
        </p>
      ) : isNew ? (
        <TextField
          label="新来源名称"
          value={name}
          error={file && !newName ? '名称不能为空' : undefined}
          helper={source ? '默认用所选来源的名称' : '默认用文件名'}
          disabled={!!progress}
          onChange={(value) => {
            nameEdited.current = true;
            setName(value);
          }}
        />
      ) : (
        replaced && (
          <p className="hint import-replace">
            {replaced.lineCount > 0
              ? `「${replaced.name}」现有的 ${replaced.lineCount} 行会被全部替换；来源名称、角色和时间策略保留。`
              : `导入到空的「${replaced.name}」；来源名称、角色和时间策略保留。`}
            {replaced.id !== state.primaryBranchId && replaced.lineCount > 0 && ' 原有的主歌词关联会随旧行一起删除。'}
          </p>
        )
      )}
      {blocked && (
        <p className="errline" role="alert">
          {blocked}
        </p>
      )}
      <Select
        label="拆分文本单元"
        value={split}
        options={SPLIT_RULES.map((r) => ({ value: r.name, label: r.label }))}
        disabled={!!progress}
        onChange={(value) => {
          splitEdited.current = true;
          setSplit(value);
          if (value === 'none') setEstimate(false);
        }}
      />
      <Switch label="按字数估算新单元的时间（结果标记为“估算”）" checked={estimate} disabled={!!progress || split === 'none'} onChange={setEstimate} />
      {progress && (
        <div className="busyline">
          <ProgressBar label={progress} />
          <span>{progress}……</span>
        </div>
      )}
      {error && (
        <p className="errline" role="alert">
          {error}
        </p>
      )}
    </FormDialog>
  );
}

/** 粘贴文本（整体设计 §5.2）：追加到某个来源末尾，或新建翻译来源。提交后选中新加入的第一行。 */
export function PasteLyricsDialog(props: {
  session: EditorSession;
  state: EditorState;
  open: boolean;
  onClose: () => void;
  onLink: (branchId: string) => void;
}) {
  const { session, state } = props;
  const branches = state.branches ?? [];
  const [text, setText] = useState('');
  const [target, setTarget] = useState(state.branchId);
  const [name, setName] = useState('翻译');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  useEffect(() => {
    if (!props.open) return;
    setText('');
    setTarget(session.state.branchId);
    setName('翻译');
    setError(undefined);
  }, [props.open, session]);

  const isNew = target === NEW_TARGET;
  const appendTo = branches.find((b) => b.id === target);
  const count = pastedLineCount(text);
  const ready = count > 0 && (isNew ? !!name.trim() : !!appendTo);

  const submit = async () => {
    if (!ready || busy) return;
    setBusy(true);
    setError(undefined);
    try {
      const response = await session.command({
        type: 'lyrics.appendText',
        text,
        destination: isNew ? { kind: 'newTranslation', name: name.trim() } : { kind: 'existing', branchId: target },
      });
      const result = response?.importResult;
      if (!result) {
        setError(session.state.notice?.message ?? '追加没有成功。');
        return;
      }
      props.onClose();
      if (isNew) {
        toast(`已新建翻译来源「${name.trim()}」，${result.lineCount} 行`, { label: '关联主歌词', run: () => props.onLink(result.selectedBranchId) });
      } else {
        toast(`已追加 ${result.lineCount} 行到「${appendTo?.name ?? ''}」末尾`);
      }
    } finally {
      setBusy(false);
    }
  };

  const targetOptions = [
    ...branches.map((b) => ({ value: b.id, label: `追加到「${b.name}」末尾` })),
    { value: NEW_TARGET, label: '新建翻译来源' },
  ];
  const secondary = isNew || (appendTo && appendTo.id !== state.primaryBranchId);

  return (
    <FormDialog
      open={props.open}
      headline="粘贴歌词"
      description="每个非空行成为一行歌词，没有时间。整次追加记一条历史，可以撤销。"
      submitText={count > 0 ? `追加 ${count} 行` : '追加'}
      submitDisabled={!ready}
      busy={busy}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="paste-dialog"
    >
      <TextField label="歌词" value={text} rows={10} helper={`将追加 ${count} 行；空白行跳过`} disabled={busy} onChange={setText} autoFocus className="paste-text" />
      <div className="g2">
        <Select label="追加到" value={target} options={targetOptions} disabled={busy} onChange={setTarget} />
        {isNew && <TextField label="新来源名称" value={name} error={name.trim() ? undefined : '名称不能为空'} disabled={busy} onChange={setName} />}
      </div>
      {secondary && <p className="hint">追加的行还没有关联主歌词，之后可以在“关联主歌词…”里按顺序配对。</p>}
      {error && (
        <p className="errline" role="alert">
          {error}
        </p>
      )}
    </FormDialog>
  );
}
