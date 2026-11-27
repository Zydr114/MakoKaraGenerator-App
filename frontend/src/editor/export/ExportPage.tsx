import { useEffect, useMemo, useState, type CSSProperties } from 'react';
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
import { RENDER_BACKEND_LABEL, resolveChoice } from '../model/render-device';
import { formatFrameRate } from '../model/settings';
import { downloadName, exportFooterMode } from '../model/render-job';
import { formatTime, frameStartUs, type FrameRate } from '../model/time';
import { fitPreviewSize } from '../preview/preview-controller';
import { errorText, type EditorSession, type EditorState } from '../session/editor-session';
import { useProjectResources } from '../settings/project-resources';
import { Button } from '../ui/Button';
import { RadioGroup, Segmented, Switch } from '../ui/choices';
import { saveBlob } from '../ui/download';
import { Select } from '../ui/fields';
import { useObservable } from '../ui/hooks';
import { Icon, type IconName } from '../ui/Icon';
import { Spinner } from '../ui/indicators';
import { Group, Note } from '../workspace/panel';
import type { ExportState, ExportStore } from './export-store';
import { ExportJobCard, ValidationCard, jobRunning } from './RenderJobCards';
import './export.css';

const THUMBNAIL_MAX = { width: 384, height: 216 };
const NO_BRANCHES: NonNullable<EditorState['branches']> = [];

const FORMAT_TEXT: Record<ExportFormat, string> = {
  opaqueMp4: '直接播放和上传；不透明，背景按工程设置',
  transparentProRes4444: '保留透明背景，叠到其他视频上；文件大很多',
};

/**
 * 导出页（整体设计 §5.7、W5 设计 §1、界面改版 §4 批 7）：左边一块是准备检查、格式、正式验证、任务和开始导出，
 * 右边幕后区看首帧与末帧。从顶栏“导出视频”进入，“返回编辑”回到原来的页面。离开时任务不停（ExportStore 由 App 持有）。
 */
export function ExportPage(props: { session: EditorSession; store: ExportStore; onFix: (fix: CheckFix) => void }) {
  const { session, store } = props;
  const state = useObservable(session.state$);
  const exportState = useObservable(store.state$);
  const settings = state.settings;
  if (!settings) {
    return (
      <div className="export-page">
        <aside className="export-form">
          <Note className="export-unavailable">后端没有提供工程设置的读取，不能导出。</Note>
        </aside>
      </div>
    );
  }
  return <ExportWork session={session} store={store} state={state} exportState={exportState} settings={settings} onFix={props.onFix} />;
}

interface WorkProps {
  session: EditorSession;
  store: ExportStore;
  state: EditorState;
  exportState: ExportState;
  settings: ProjectSettings;
  onFix: (fix: CheckFix) => void;
}

function ExportWork(props: WorkProps) {
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
  const isBlocked = blocked(checks);

  // 首帧与末帧：按修订、尺寸和渲染设备偏好渲染，修订（回来时）或偏好变了重渲。
  const lastFrameUs = frames !== undefined ? frameStartUs(frames - 1, rate) : undefined;
  const device = useObservable(renderDevice.state$);
  useEffect(() => {
    if (lastFrameUs === undefined) return;
    store.loadThumbnails(revision, fitPreviewSize(settings.canvasWidth, settings.canvasHeight, THUMBNAIL_MAX), lastFrameUs);
  }, [store, revision, settings.canvasWidth, settings.canvasHeight, lastFrameUs, device.preference]);

  const target = resolveChoice(device.preference, device.backends).target;
  const summary = [
    `${settings.canvasWidth} × ${settings.canvasHeight}`,
    formatFrameRate(rate),
    frames !== undefined ? `${frames} 帧` : '时长未定',
    includeAudio ? '含音轨' : '不含音轨',
    target ? RENDER_BACKEND_LABEL[target] : '后端默认设备',
  ].join('，');

  return (
    <div className="export-page">
      <aside className="export-form" aria-label="导出设置">
        <div className="export-sections">
          <Group
            title="准备检查"
            extra={
              exportState.capabilities === null && (
                <Button variant="text" size="sm" icon="refresh" onPress={() => void store.loadCapabilities()}>
                  重新读取导出能力
                </Button>
              )
            }
          >
            <ReadinessList checks={checks} onFix={props.onFix} />
          </Group>
          <FormatGroup store={store} exportState={exportState} hasAudio={hasAudio} />
          <ValidationCard store={store} exportState={exportState} revision={revision} frames={frames} />
          <ExportJobCard store={store} exportState={exportState} revision={revision} />
        </div>
        <div className="export-go">
          <span className="export-sum">{summary}</span>
          {isBlocked && <Note>准备检查里还有阻断项，处理之后才能开始导出。</Note>}
          <ExportActions store={store} exportState={exportState} revision={revision} projectName={state.summary.name} blocked={isBlocked} includeAudio={includeAudio} />
        </div>
      </aside>
      <ExportStage
        exportState={exportState}
        settings={settings}
        rate={rate}
        frames={frames}
        lastFrameUs={lastFrameUs}
        backgroundText={BACKGROUND_KIND_TEXT[settings.background?.kind ?? 'color']}
      />
    </div>
  );
}

function withValue<K extends string, V>(key: K, value: V | undefined): { [P in K]?: V } {
  return value === undefined ? {} : ({ [key]: value } as { [P in K]?: V });
}

/** 正式验证在准备检查里的状态：验证的修订不是当前修订时算“旧的”。 */
function validationStatus(exportState: ExportState, revision: string): ReadinessInput['validation'] {
  if (exportState.submitting?.validation) return 'running';
  const track = exportState.validation;
  if (!track) return 'none';
  const { job } = track;
  if (job.state === 'queued' || job.state === 'running' || job.state === 'committing') return 'running';
  if (job.state === 'cancelled' || job.state === 'unknown') return 'none';
  if (job.input?.sourceRevision !== revision) return 'stale';
  return job.validationResult?.passed ? 'passed' : 'failed';
}

const LEVEL_TEXT: Record<ReadinessCheck['level'], string> = { ok: '通过', info: '提示', warning: '注意', block: '阻断' };
const LEVEL_ICON: Record<ReadinessCheck['level'], IconName> = { ok: 'check_circle', info: 'info', warning: 'warning', block: 'error' };

/** 一项一行：图标、名称、说明、去修复；注意项与阻断项带底色。 */
function ReadinessList(props: { checks: readonly ReadinessCheck[]; onFix: (fix: CheckFix) => void }) {
  return (
    <ul className="readiness-list">
      {props.checks.map((check) => (
        <li key={check.id} className="ck" data-check={check.id} data-level={check.level}>
          <Icon name={LEVEL_ICON[check.level]} label={LEVEL_TEXT[check.level]} />
          <span className="ck-title">{check.title}</span>
          <span className="ck-text">{check.text}</span>
          {check.fix && check.level !== 'ok' ? (
            <Button variant="text" size="sm" onPress={() => props.onFix(check.fix!)}>
              {check.fix.kind === 'lyrics' ? '去歌词页' : '去设置'}
            </Button>
          ) : (
            <span />
          )}
        </li>
      ))}
    </ul>
  );
}

function FormatGroup(props: { store: ExportStore; exportState: ExportState; hasAudio: boolean }) {
  const { store, exportState } = props;
  const caps = exportState.capabilities;
  const formats = FORMAT_OPTIONS.map((o) => ({ value: o.value, label: o.label, description: FORMAT_TEXT[o.value], disabled: !!caps && !caps.formats.includes(o.value) }));
  const qualities = QUALITY_OPTIONS.map((o) => ({ value: o.value, label: o.label, disabled: !!caps?.qualities && !caps.qualities.includes(o.value) }));
  return (
    <Group title="格式">
      <RadioGroup<ExportFormat> label="格式" hideLabel variant="card" value={exportState.format} options={formats} onChange={(v) => store.setFormat(v)} />
      <div className="export-opts">
        <Select<ExportQuality>
          label="质量"
          labelPlacement="side"
          className="export-quality"
          value={exportState.quality ?? 'standard'}
          options={qualities}
          onChange={(v) => store.setQuality(v)}
        />
        <Switch label="含音轨" checked={props.hasAudio && exportState.includeAudio} disabled={!props.hasAudio} onChange={(v) => store.setIncludeAudio(v)} />
      </div>
    </Group>
  );
}

/** 底部的主操作区：导出成功后突出“下载成片”并提供次级“重新导出”；工程已修改时突出“重新导出”并保留“下载旧成片”。 */
function ExportActions(props: {
  store: ExportStore;
  exportState: ExportState;
  revision: string;
  projectName: string;
  blocked: boolean;
  includeAudio: boolean;
}) {
  const { store, exportState } = props;
  const track = exportState.exportJob;
  const submitting = exportState.submitting?.export;
  const running = jobRunning(track) || !!submitting;
  const [downloading, setDownloading] = useState(false);
  const [downloadError, setDownloadError] = useState<string>();
  const jobId = track?.job.jobId;
  useEffect(() => setDownloadError(undefined), [jobId]);

  const mode = exportFooterMode({
    hasTrack: !!track,
    running,
    hasArtifact: !!track?.exportReport,
    stale: !!track?.job.input?.sourceRevision && track.job.input.sourceRevision !== props.revision,
  });
  const canExport = !props.blocked && !running && !downloading && !!exportState.capabilities?.enabled;

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

  if (mode === 'download' || mode === 'staleDownload') {
    const downloadPrimary = mode === 'download';
    const downloadButton = (
      <Button
        variant={downloadPrimary ? 'filled' : 'outlined'}
        size="lg"
        icon="download"
        className={downloadPrimary ? 'export-primary' : 'export-secondary'}
        isPending={downloading}
        isDisabled={downloading}
        onPress={() => void download()}
      >
        {downloadPrimary ? '下载成片' : '下载旧成片'}
      </Button>
    );
    const reexportButton = (
      <Button
        variant={downloadPrimary ? 'outlined' : 'filled'}
        size="lg"
        icon="refresh"
        className={downloadPrimary ? 'export-secondary' : 'export-primary'}
        isDisabled={!canExport}
        onPress={() => void store.startExport(props.includeAudio)}
      >
        重新导出
      </Button>
    );
    return (
      <>
        <div className="export-actions">
          {downloadPrimary ? (
            <>
              {downloadButton}
              {reexportButton}
            </>
          ) : (
            <>
              {reexportButton}
              {downloadButton}
            </>
          )}
        </div>
        {downloadError && <Note error>{downloadError}</Note>}
      </>
    );
  }

  return (
    <Button
      variant="filled"
      size="lg"
      icon="movie"
      className="export-start"
      isPending={!!submitting}
      isDisabled={!canExport}
      onPress={() => void store.startExport(props.includeAudio)}
    >
      {submitting === 'reconciling'
        ? '正在核对导出状态……'
        : submitting === 'submitting'
          ? '正在提交导出……'
          : mode === 'retry'
            ? '重新导出'
            : '开始导出'}
    </Button>
  );
}

type Which = 'first' | 'last';

/** 幕后区：首帧与末帧（两张都留在页面里，只显示选中的一张），下面写输出的画布和范围。 */
function ExportStage(props: {
  exportState: ExportState;
  settings: ProjectSettings;
  rate: FrameRate;
  frames: number | undefined;
  lastFrameUs: number | undefined;
  backgroundText: string;
}) {
  const { settings, frames, lastFrameUs } = props;
  const [which, setWhich] = useState<Which>('first');
  const thumbnails = props.exportState.thumbnails;
  const loading = !!thumbnails?.loading;
  const ratio = { '--rw': settings.canvasWidth, '--rh': settings.canvasHeight } as CSSProperties;
  return (
    <section className="export-stage stage-zone" aria-label="首帧与末帧">
      <div className="canvasbar">
        <Segmented<Which>
          label="看哪一帧"
          size="sm"
          value={which}
          options={[
            { value: 'first', label: '首帧' },
            { value: 'last', label: '末帧' },
          ]}
          onChange={setWhich}
          disabled={lastFrameUs === undefined}
        />
        {lastFrameUs !== undefined && (
          <span className="canvas-hint tnum">{which === 'first' ? `${formatTime(0)}，第 1 帧` : `${formatTime(lastFrameUs)}，第 ${frames} 帧`}</span>
        )}
        <span className="spacer" />
        <span className="canvas-hint">背景：{props.backgroundText}</span>
      </div>
      <div className="export-frames" style={ratio}>
        {lastFrameUs === undefined ? (
          <p className="export-frames-empty">输出时长确定后显示首帧和末帧。</p>
        ) : (
          <>
            <Thumbnail label={`首帧 · ${formatTime(0)}`} url={thumbnails?.first} loading={loading} hidden={which !== 'first'} />
            <Thumbnail label={`末帧 · ${formatTime(lastFrameUs)}`} url={thumbnails?.last} loading={loading} hidden={which !== 'last'} />
          </>
        )}
      </div>
      {thumbnails?.error && (
        <Note error className="export-frames-error">
          {thumbnails.error}
        </Note>
      )}
      <p className="export-caption tnum">
        <span>
          画布 {settings.canvasWidth} × {settings.canvasHeight}，{formatFrameRate(props.rate)}
        </span>
        <span>{lastFrameUs !== undefined && frames !== undefined ? `${formatTime(0)} – ${formatTime(Number(settings.outputDurationUs))}，${frames} 帧` : '输出时长未定'}</span>
      </p>
    </section>
  );
}

function Thumbnail(props: { label: string; url: string | undefined; loading: boolean; hidden: boolean }) {
  return (
    <figure className="export-thumbnail" hidden={props.hidden} aria-label={props.label}>
      {props.url && <img src={props.url} alt={props.label} />}
      {props.loading && <Spinner label={`正在渲染${props.label}`} />}
    </figure>
  );
}
