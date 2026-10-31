import { useEffect, useMemo } from 'react';
import type { ExportFormat, ExportQuality, ProjectSettings } from '../../protocol';
import { renderDevice } from '../connection';
import {
  FORMAT_OPTIONS,
  QUALITY_OPTIONS,
  blocked,
  readinessChecks,
  timingProblems,
  totalFrames,
  type CheckFix,
  type ReadinessCheck,
  type ReadinessInput,
} from '../model/export-readiness';
import { BACKGROUND_KIND_TEXT, audioResource, backgroundResource } from '../model/media';
import { formatFrameRate } from '../model/settings';
import { formatTime, frameStartUs, type FrameRate } from '../model/time';
import { fitPreviewSize } from '../preview/preview-controller';
import type { EditorSession, EditorState } from '../session/editor-session';
import { useProjectResources } from '../settings/project-resources';
import { Select, Switch } from '../ui/fields';
import { useObservable } from '../ui/hooks';
import type { ExportState, ExportStore } from './export-store';
import { ExportJobCard, ValidationCard } from './RenderJobCards';

const THUMBNAIL_MAX = { width: 384, height: 216 };
const NO_BRANCHES: NonNullable<EditorState['branches']> = [];

/**
 * 导出页（整体设计 §5.7、W5 设计 §1）：准备检查、格式、摘要、正式验证、任务。
 * 不在导航栏里，从顶栏“导出视频”进入，“返回编辑”回到原来的页面。离开时任务不停（ExportStore 由 App 持有）。
 */
export function ExportPage(props: { session: EditorSession; store: ExportStore; onBack: () => void; onFix: (fix: CheckFix) => void }) {
  const { session, store } = props;
  const state = useObservable(session.state$);
  const exportState = useObservable(store.state$);
  const settings = state.settings;

  return (
    <div className="export-page">
      <header className="export-header">
        <mdui-button variant="text" onClick={props.onBack}>
          <mdui-icon-arrow-back slot="icon" />
          返回编辑
        </mdui-button>
        <h1>导出视频</h1>
      </header>
      {settings ? (
        <ExportSections session={session} store={store} state={state} exportState={exportState} settings={settings} onFix={props.onFix} />
      ) : (
        <p className="muted">后端没有提供工程设置的读取，不能导出。</p>
      )}
    </div>
  );
}

interface SectionsProps {
  session: EditorSession;
  store: ExportStore;
  state: EditorState;
  exportState: ExportState;
  settings: ProjectSettings;
  onFix: (fix: CheckFix) => void;
}

function ExportSections(props: SectionsProps) {
  const { session, store, state, exportState, settings } = props;
  const revision = state.summary.projectRevision;
  const { resources } = useProjectResources(session, true);
  const rate: FrameRate = { numerator: settings.frameRateNumerator, denominator: settings.frameRateDenominator };
  const durationUs = settings.outputDurationUs !== undefined ? Number(settings.outputDurationUs) : undefined;
  const frames = durationUs !== undefined && durationUs > 0 ? totalFrames(durationUs, rate) : undefined;
  const hasAudio = !!state.summary.audioAssetId;
  const includeAudio = hasAudio && exportState.includeAudio;
  const background = settings.background && settings.background.kind !== 'color' ? settings.background : undefined;

  useEffect(() => void store.loadPolicies(revision), [store, revision]);

  const policies = exportState.policies?.byBranch;
  const branches = state.branches ?? NO_BRANCHES;
  const timing = useMemo(
    () => timingProblems(branches.map((b) => ({ id: b.id, name: b.name, role: b.role, timingMode: b.timingMode, animationPolicy: policies?.[b.id] })), state.linesByBranch),
    [branches, state.linesByBranch, policies],
  );

  const input: ReadinessInput = {
    capabilities: exportState.capabilities,
    ...(exportState.capabilitiesError ? { capabilitiesError: exportState.capabilitiesError } : {}),
    format: exportState.format,
    includeAudio,
    canvasWidth: settings.canvasWidth,
    canvasHeight: settings.canvasHeight,
    frameRate: rate,
    ...(durationUs !== undefined ? { outputDurationUs: durationUs } : {}),
    hasAudio,
    resources,
    ...withValue('audioResource', resources ? audioResource(resources) : undefined),
    ...withValue('backgroundResource', resources && background ? backgroundResource(resources, background.resourceId) : undefined),
    hasBackgroundMedia: !!background,
    timing,
    branchName: (id) => branches.find((b) => b.id === id)?.name ?? id,
    validation: validationStatus(exportState, revision),
  };
  const checks = readinessChecks(input);

  // 首帧与末帧：按修订、尺寸和渲染设备偏好渲染，修订（回来时）或偏好变了重渲。
  const lastFrameUs = frames !== undefined ? frameStartUs(frames - 1, rate) : undefined;
  const devicePreference = useObservable(renderDevice.state$).preference;
  useEffect(() => {
    if (lastFrameUs === undefined) return;
    store.loadThumbnails(revision, fitPreviewSize(settings.canvasWidth, settings.canvasHeight, THUMBNAIL_MAX), lastFrameUs);
  }, [store, revision, settings.canvasWidth, settings.canvasHeight, lastFrameUs, devicePreference]);

  return (
    <>
      <section className="export-card" aria-label="准备检查">
        <h2>准备检查</h2>
        <ReadinessList checks={checks} onFix={props.onFix} />
        {exportState.capabilities === null && (
          <mdui-button variant="text" onClick={() => void store.loadCapabilities()}>
            重新读取导出能力
          </mdui-button>
        )}
        {blocked(checks) && <p className="hint">还有阻断项，处理之后才能开始导出。</p>}
      </section>
      <FormatSection store={store} exportState={exportState} hasAudio={hasAudio} />
      <section className="export-card" aria-label="摘要">
        <h2>摘要</h2>
        <dl className="settings-facts export-facts">
          <Fact label="画布" value={`${settings.canvasWidth} × ${settings.canvasHeight}`} />
          <Fact label="帧率" value={formatFrameRate(rate)} />
          <Fact label="时长" value={durationUs !== undefined ? formatTime(durationUs) : '未确定'} />
          <Fact label="帧数" value={frames !== undefined ? String(frames) : '—'} />
          <Fact label="格式" value={FORMAT_OPTIONS.find((o) => o.value === exportState.format)?.label ?? exportState.format} />
          <Fact label="音轨" value={includeAudio ? '包含' : '不含'} />
          <Fact label="背景" value={BACKGROUND_KIND_TEXT[settings.background?.kind ?? 'color']} />
        </dl>
        <Thumbnails exportState={exportState} lastFrameUs={lastFrameUs} />
      </section>
      <ValidationCard store={store} exportState={exportState} revision={revision} frames={frames} />
      <ExportJobCard store={store} exportState={exportState} revision={revision} blocked={blocked(checks)} includeAudio={includeAudio} projectName={state.summary.name} />
    </>
  );
}

function withValue<K extends string, V>(key: K, value: V | undefined): { [P in K]?: V } {
  return value === undefined ? {} : ({ [key]: value } as { [P in K]?: V });
}

/** 正式验证在准备检查里的状态：验证的修订不是当前修订时算“旧的”。 */
function validationStatus(exportState: ExportState, revision: string): ReadinessInput['validation'] {
  const track = exportState.validation;
  if (!track) return 'none';
  const { job } = track;
  if (job.state === 'queued' || job.state === 'running' || job.state === 'committing') return 'running';
  if (job.state === 'cancelled' || job.state === 'unknown') return 'none';
  if (job.input?.sourceRevision !== revision) return 'stale';
  return job.validationResult?.passed ? 'passed' : 'failed';
}

const LEVEL_TEXT: Record<ReadinessCheck['level'], string> = { ok: '通过', info: '提示', warning: '注意', block: '阻断' };

function ReadinessList(props: { checks: readonly ReadinessCheck[]; onFix: (fix: CheckFix) => void }) {
  return (
    <ul className="readiness-list">
      {props.checks.map((check) => (
        <li key={check.id} className={`readiness-item readiness-${check.level}`} data-check={check.id} data-level={check.level}>
          <span className="readiness-icon" aria-label={LEVEL_TEXT[check.level]}>
            {check.level === 'ok' ? <mdui-icon-check-circle /> : check.level === 'block' ? <mdui-icon-error /> : check.level === 'warning' ? <mdui-icon-warning /> : <mdui-icon-info />}
          </span>
          <span className="readiness-body">
            <span className="readiness-title">{check.title}</span>
            <span className="readiness-text">{check.text}</span>
          </span>
          {check.fix && check.level !== 'ok' && (
            <mdui-button variant="text" onClick={() => props.onFix(check.fix!)}>
              {check.fix.kind === 'lyrics' ? '去歌词页' : '去设置'}
            </mdui-button>
          )}
        </li>
      ))}
    </ul>
  );
}

function FormatSection(props: { store: ExportStore; exportState: ExportState; hasAudio: boolean }) {
  const { store, exportState } = props;
  const caps = exportState.capabilities;
  const formats = FORMAT_OPTIONS.map((o) => ({ value: o.value, label: o.label, disabled: !!caps && !caps.formats.includes(o.value) }));
  const qualities = QUALITY_OPTIONS.map((o) => ({ value: o.value, label: o.label, disabled: !!caps?.qualities && !caps.qualities.includes(o.value) }));
  return (
    <section className="export-card" aria-label="格式">
      <h2>格式</h2>
      <div className="dialog-grid">
        <Select<ExportFormat> label="格式" value={exportState.format} options={formats} onChange={(v) => store.setFormat(v)} />
        <Select<ExportQuality> label="质量" value={exportState.quality ?? 'standard'} options={qualities} onChange={(v) => store.setQuality(v)} />
      </div>
      <Switch label="含音轨" checked={props.hasAudio && exportState.includeAudio} disabled={!props.hasAudio} onChange={(v) => store.setIncludeAudio(v)} />
      <p className="hint">
        {exportState.format === 'transparentProRes4444'
          ? 'MOV 透明保留透明背景，适合在剪辑软件里叠到视频上；文件比 MP4 大很多。'
          : 'MP4 适合直接播放和上传；画面不透明，背景按工程设置。'}
        {!props.hasAudio && ' 工程没有音频，只导出画面。'}
      </p>
    </section>
  );
}

function Fact(props: { label: string; value: string }) {
  return (
    <div>
      <dt>{props.label}</dt>
      <dd>{props.value}</dd>
    </div>
  );
}

function Thumbnails(props: { exportState: ExportState; lastFrameUs: number | undefined }) {
  const thumbnails = props.exportState.thumbnails;
  if (props.lastFrameUs === undefined) return <p className="hint">输出时长确定后显示首帧和末帧。</p>;
  return (
    <>
      <div className="export-thumbnails">
        <Thumbnail label={`首帧 · ${formatTime(0)}`} url={thumbnails?.first} loading={!!thumbnails?.loading} />
        <Thumbnail label={`末帧 · ${formatTime(props.lastFrameUs)}`} url={thumbnails?.last} loading={!!thumbnails?.loading} />
      </div>
      {thumbnails?.error && <p className="dialog-error">{thumbnails.error}</p>}
    </>
  );
}

function Thumbnail(props: { label: string; url: string | undefined; loading: boolean }) {
  return (
    <figure className="export-thumbnail">
      <div className="export-thumbnail-frame">
        {props.url && <img src={props.url} alt={props.label} />}
        {props.loading && <mdui-circular-progress />}
      </div>
      <figcaption>{props.label}</figcaption>
    </figure>
  );
}
