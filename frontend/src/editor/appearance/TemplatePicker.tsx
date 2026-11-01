import { useEffect, useMemo, useState } from 'react';
import type { LyricBranchSummary, PreviewSize, TemplateKind, TemplateLibraryId, TemplateSummary } from '../../protocol';
import { templateFacts } from '../connection';
import { filterTemplates, KIND_TEXT, type LibraryList, type TemplateLibraryStore } from '../library/template-library-store';
import { TemplateThumb } from '../library/TemplateThumb';
import type { TemplateThumbnails } from '../library/template-thumbnails';
import { applicability, type SampleTargetKey } from '../model/appearance-targets';
import { branchInputsField, defaultInputMapping, missingInputs } from '../model/branch-inputs';
import { BRANCH_ROLE_TEXT } from '../model/branches';
import { propertySummary } from '../model/template-groups';
import { formatTime } from '../model/time';
import { Select, TextField } from '../ui/legacy/fields';
import { FormDialog } from '../ui/legacy/FormDialog';
import { useObjectUrl, useObservable } from '../ui/hooks';
import type { ApplyResult, TrialState } from './appearance-store';
import type { Fact } from './template-facts';

const KIND_OPTIONS: ReadonlyArray<{ value: TemplateKind | 'all'; label: string }> = [
  { value: 'all', label: '全部用途' },
  { value: 'line', label: KIND_TEXT.line },
  { value: 'token', label: KIND_TEXT.token },
  { value: 'composite', label: KIND_TEXT.composite },
];

export interface TemplateChoice {
  libraryId: TemplateLibraryId;
  templateId: string;
}
type Choice = TemplateChoice;

const NO_INPUTS: Fact<readonly string[]> = { status: 'ready', value: [] };

/** 模板能不能用于这类目标：选择器打开时按后端的试样选项判断（W7 设计 §2.7），还没读到或读失败时按本地规则。 */
function reasonOf(open: boolean, libraryId: TemplateLibraryId, list: LibraryList, summary: TemplateSummary, kind: SampleTargetKey): string | undefined {
  if (!open) return applicability(summary, kind);
  const fact = templateFacts.targetKeys(libraryId, list.revision, summary);
  return applicability(summary, kind, fact.status === 'ready' ? fact.value : undefined);
}

/**
 * 模板选择器（整体设计 §5.4）：内置与我的模板按缩略图（G17 固定试样）、名称、用途、属性摘要列成卡片，不适用的灰显并写明原因。
 * 点选适用的模板后在顶部试看（G16，W4 设计 §9.1）：当前修订、播放头时刻的候选画面，不写工程；确认后才应用。
 * 模板要读取其他来源时（W7 设计 §2.7），先在下面为每个输入名选一个来源，选好之前不能试看和应用。
 */
export function TemplatePicker(props: {
  open: boolean;
  /** 目标在试样选项里的键：按它判断模板适不适用。 */
  targetKind: SampleTargetKey;
  /** “2 行”“这个前奏提示组”。 */
  countText: string;
  targetPath: string;
  /** 工程里的来源：来源输入从这里选。 */
  branches: readonly LyricBranchSummary[];
  library: TemplateLibraryStore;
  thumbnails: TemplateThumbnails;
  busy: boolean;
  /** 试看画面的尺寸（与成片预览相同）。 */
  previewSize: PreviewSize;
  canTrial: boolean;
  trial: TrialState | undefined;
  onTrial: (libraryId: TemplateLibraryId, libraryRevision: string, template: TemplateSummary, branchInputs: Record<string, string> | undefined) => void;
  onEndTrial: () => void;
  onApply: (libraryId: TemplateLibraryId, libraryRevision: string, template: TemplateSummary, branchInputs: Record<string, string> | undefined) => Promise<ApplyResult>;
  onClose: () => void;
  /** 打开时先选中的模板（从样式库“保存并返回试看”回来时）。 */
  initialChoice?: TemplateChoice | null;
}) {
  const { library } = props;
  const state = useObservable(library.state$);
  const [query, setQuery] = useState('');
  const [kind, setKind] = useState<TemplateKind | 'all'>('all');
  const [choice, setChoice] = useState<Choice | null>(() => (props.open ? (props.initialChoice ?? null) : null));
  const [message, setMessage] = useState<string | null>(null);
  /** 用户为来源输入选的来源；没选的按名称或用途自动对上（defaultInputMapping）。 */
  const [mapping, setMapping] = useState<Record<string, string>>({});
  useObservable(templateFacts.version$);
  const { targetKind, countText } = props;

  // 每次打开时取初始选择、清掉上次的提示。放在渲染里而不是 effect 里：否则打开后的第一帧还是上次选的模板，
  // 会挂上它的来源输入下拉框又立刻卸掉（mdui-select 来不及初始化就抛异常），还会对旧模板发一次试看。
  const [wasOpen, setWasOpen] = useState(props.open);
  if (props.open !== wasOpen) {
    setWasOpen(props.open);
    if (props.open) {
      setChoice(props.initialChoice ?? null);
      setMessage(null);
    }
  }

  // 每次打开都重读两个库：别处（样式库页）改过的模板要以最新为准。
  useEffect(() => {
    if (props.open) void library.load();
  }, [props.open, library]);

  const chosen = choice ? library.summary(choice.libraryId, choice.templateId) : undefined;
  // 选中的卡片滚到看得见的地方（初始选择可能在列表下面）。
  const chosenId = chosen?.templateId;
  useEffect(() => {
    if (props.open && chosenId) document.querySelector(`.template-picker .template-card[data-template-id="${CSS.escape(chosenId)}"]`)?.scrollIntoView({ block: 'nearest' });
  }, [props.open, chosenId]);
  const chosenList = choice ? state[choice.libraryId] : undefined;
  const blocked = chosen && chosenList ? reasonOf(props.open, choice!.libraryId, chosenList, chosen, targetKind) : undefined;
  const inputsFact = props.open && chosen && chosenList && !blocked ? templateFacts.branchInputs(choice!.libraryId, chosenList.revision, chosen) : NO_INPUTS;
  const inputNames = inputsFact.status === 'ready' ? inputsFact.value : [];
  const effective = defaultInputMapping(inputNames, props.branches, mapping);
  const missing = missingInputs(inputNames, effective, props.branches);
  const inputsReady = inputsFact.status === 'ready' && missing.length === 0;
  const branchInputs = inputsReady ? branchInputsField(inputNames, effective) : undefined;

  // 点选（或模板库重读后模板修订变了、来源输入选好或换了）就试看；不适用、来源没选好或关闭时结束试看。
  const { canTrial, onTrial, onEndTrial } = props;
  const trialKey =
    props.open && canTrial && chosen && chosenList && !blocked && inputsReady
      ? `${choice!.libraryId}|${chosenList.revision}|${chosen.templateId}|${chosen.templateRevision}|${JSON.stringify(branchInputs ?? {})}`
      : undefined;
  useEffect(() => {
    if (!trialKey || !choice || !chosen || !chosenList) {
      onEndTrial();
      return;
    }
    onTrial(choice.libraryId, chosenList.revision, chosen, branchInputs);
    // 只按 trialKey 触发：同一个模板的摘要对象在重读后会换引用。
  }, [trialKey]);

  const submit = async () => {
    if (!choice || !chosen || !chosenList || blocked || !inputsReady) return;
    setMessage(null);
    const result = await props.onApply(choice.libraryId, chosenList.revision, chosen, branchInputs);
    if (result === 'applied' || result === 'unchanged') props.onClose();
    else if (result === 'stale') {
      await library.load();
      setMessage('模板库已更新，已重新读取。请确认后再应用。');
    }
  };

  return (
    <FormDialog
      open={props.open}
      headline={`选择模板 · 用于 ${countText}`}
      description={props.targetPath}
      submitText={`应用到 ${countText}`}
      submitDisabled={!chosen || !!blocked || !inputsReady}
      busy={props.busy}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="template-picker"
    >
      <TrialView
        trial={props.trial}
        canTrial={canTrial}
        active={!!trialKey}
        size={props.previewSize}
        idleText={chosen && !blocked && inputsFact.status !== 'loading' && !inputsReady ? '为这个模板选好要读取的来源后再试看。' : undefined}
      />
      {choice && chosen && !blocked && (
        <BranchInputs
          fact={inputsFact}
          names={inputNames}
          mapping={effective}
          missing={missing}
          branches={props.branches}
          onChange={(name, branchId) => setMapping((m) => ({ ...m, [name]: branchId }))}
          onRetry={() => templateFacts.retry(choice.libraryId, chosen)}
        />
      )}
      <div className="picker-filters">
        <TextField label="搜索模板" value={query} onChange={setQuery} />
        <Select label="用途" value={kind} options={KIND_OPTIONS} onChange={setKind} />
      </div>
      {state.status === 'loading' && !state.builtin && <mdui-linear-progress />}
      {state.status === 'failed' && <p className="notice-error">读取模板库失败。{state.notice?.message ?? ''}</p>}
      <div className="picker-groups">
        <PickerGroup title="内置" libraryId="builtin" list={state.builtin} open={props.open} thumbnails={props.thumbnails} query={query} kind={kind} targetKind={targetKind} choice={choice} onChoose={setChoice} />
        <PickerGroup title="我的" libraryId="personal" list={state.personal} open={props.open} thumbnails={props.thumbnails} query={query} kind={kind} targetKind={targetKind} choice={choice} onChoose={setChoice} />
      </div>
      <p className="hint">
        {chosen ? (blocked ? `“${chosen.name}”不能用于${countText}：${blocked}。` : `将把“${chosen.name}”的固定副本写入工程；目标原有的分配会被替换。`) : '选择一个模板。'}
        之后在样式库里改这个模板，不会影响已经应用的副本。
      </p>
      {message && <p className="notice-warning">{message}</p>}
    </FormDialog>
  );
}

/** 来源输入（W7 设计 §2.7）：模板声明的每个输入名配一个来源下拉框；没选好时写明需要读取什么。 */
function BranchInputs(props: {
  fact: Fact<readonly string[]>;
  names: readonly string[];
  mapping: Readonly<Record<string, string>>;
  missing: readonly string[];
  branches: readonly LyricBranchSummary[];
  onChange: (name: string, branchId: string) => void;
  onRetry: () => void;
}) {
  const { fact, names } = props;
  if (fact.status === 'loading') return <p className="hint picker-inputs">正在读取这个模板需要读取的来源……</p>;
  if (fact.status === 'failed') {
    return (
      <p className="notice-error picker-inputs">
        读不到这个模板需要读取的来源：{fact.error}
        <mdui-button variant="text" onClick={props.onRetry}>
          重试
        </mdui-button>
      </p>
    );
  }
  if (names.length === 0) return null;
  const options = props.branches.map((b) => {
    const role = BRANCH_ROLE_TEXT[b.role];
    return { value: b.id, label: b.name && b.name !== role ? `${b.name} · ${role}` : b.name || role };
  });
  return (
    <section className="picker-inputs" aria-label="来源输入">
      <p className={props.missing.length > 0 ? 'notice-warning' : 'hint'}>
        这个模板需要读取：{names.join('、')}。{props.missing.length > 0 ? '选好来源之前不能试看和应用。' : '应用后，分配记住这里选的来源。'}
      </p>
      <div className="picker-input-fields">
        {names.map((name) => (
          <div key={name} className="picker-input" data-input={name}>
            <Select label={`“${name}”读取的来源`} value={props.mapping[name] ?? ''} options={options} onChange={(id) => props.onChange(name, id)} />
          </div>
        ))}
      </div>
    </section>
  );
}

/** 试看画面：标明是“试看”、试的是哪个模板、用于几个目标、哪个时刻，并写明没有写入工程（整体设计 R5）。 */
function TrialView(props: { trial: TrialState | undefined; canTrial: boolean; active: boolean; size: PreviewSize; idleText?: string | undefined }) {
  const { trial } = props;
  const url = useObjectUrl(props.active ? trial?.frame : undefined);
  if (!props.canTrial) return <p className="hint picker-trial-off">后端没有开放试看，或预览不支持模板分配：选中后直接应用，不满意就撤销。</p>;
  const shown = props.active ? trial : undefined;
  return (
    <section className="picker-trial" aria-label="试看">
      <div className="preview-title">
        <span className="pane-title-text">{shown ? `试看 · ${shown.templateName} → ${shown.targetText} · ${formatTime(shown.timeUs)}` : '试看'}</span>
        <span className="muted">{shown?.status === 'loading' ? '渲染中' : shown ? '没有写入工程' : ''}</span>
      </div>
      <div className="preview-frame" style={{ aspectRatio: `${props.size.width} / ${props.size.height}` }}>
        {url && shown?.status === 'ready' && <img src={url} alt="试看画面" data-sha={shown.sha256} />}
        {!shown && <span className="preview-overlay">{props.idleText ?? '点选一个模板，在这里看它用在目标上的样子。'}</span>}
        {shown?.status === 'loading' && <span className="preview-overlay">正在生成试看……</span>}
        {shown?.status === 'error' && <span className="preview-overlay notice-error">{shown.error}</span>}
      </div>
    </section>
  );
}

function PickerGroup(props: {
  title: string;
  libraryId: TemplateLibraryId;
  list: LibraryList | undefined;
  /** 选择器打开时才按后端的试样选项判断适用。 */
  open: boolean;
  thumbnails: TemplateThumbnails;
  query: string;
  kind: TemplateKind | 'all';
  targetKind: SampleTargetKey;
  choice: Choice | null;
  onChoose: (choice: Choice) => void;
}) {
  const { list } = props;
  const templates = useMemo(() => (list ? filterTemplates(list.templates, props.query, props.kind) : []), [list, props.query, props.kind]);
  if (!list) return null;
  return (
    <section className="picker-group" aria-label={props.title}>
      <h3 className="section-title">{props.title}</h3>
      {templates.length === 0 ? (
        <p className="hint">{list.templates.length === 0 ? '还没有模板。' : '没有匹配的模板。'}</p>
      ) : (
        <div className="picker-cards">
          {templates.map((t) => {
            const reason = reasonOf(props.open, props.libraryId, list, t, props.targetKind);
            const selected = props.choice?.libraryId === props.libraryId && props.choice.templateId === t.templateId;
            return (
              <button
                key={t.templateId}
                type="button"
                className={`template-card${selected ? ' selected' : ''}${reason ? ' blocked' : ''}`}
                aria-pressed={selected}
                aria-disabled={!!reason}
                data-template-id={t.templateId}
                title={reason}
                onClick={() => !reason && props.onChoose({ libraryId: props.libraryId, templateId: t.templateId })}
              >
                <TemplateThumb thumbnails={props.thumbnails} libraryId={props.libraryId} libraryRevision={list.revision} summary={t} />
                <span className="template-card-name">{t.name}</span>
                <span className="template-card-meta">
                  {KIND_TEXT[t.kind]} · {propertySummary(t.kind, t.properties)}
                </span>
                {reason && <span className="template-card-reason">{reason}</span>}
              </button>
            );
          })}
        </div>
      )}
    </section>
  );
}
