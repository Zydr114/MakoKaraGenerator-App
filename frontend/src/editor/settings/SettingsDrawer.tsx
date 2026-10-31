import { useEffect, useRef, useState } from 'react';
import type { AnalysisCapabilities, AnalysisProgress, ProjectCommand, ProjectResource, ProjectSettings } from '../../protocol';
import { client } from '../connection';
import {
  BACKGROUND_FILE_ACCEPT,
  BACKGROUND_KIND_TEXT,
  FIT_OPTIONS,
  PLAYBACK_OPTIONS,
  audioResource,
  audioStatusText,
  backgroundKindOf,
  backgroundResource,
  checkProjectName,
  formatBytes,
  resourceStatusText,
  resourceUsable,
} from '../model/media';
import type { SettingsGroup } from '../model/export-readiness';
import { AUDIO_FILE_ACCEPT } from '../model/new-project';
import {
  CANVAS_OPTIONS,
  CUSTOM,
  FPS_OPTIONS,
  analysisProgressText,
  formatColor,
  formatFrameRate,
  parseDimension,
  parseFrameRate,
  type Rgba,
} from '../model/settings';
import { formatTime, parseTime, toMicros } from '../model/time';
import { errorText, type EditorSession, type EditorState } from '../session/editor-session';
import { replaceAudio, uploadMedia, uploadedName } from '../session/project-media';
import { confirmAction } from '../ui/dialogs';
import { toast } from '../ui/feedback';
import { ColorField, CommitField, FileButton, Select } from '../ui/fields';
import { useDomEvent, useObservable } from '../ui/hooks';
import { useProjectResources, useResourceObjectUrl } from './project-resources';

/**
 * 工程设置（整体设计 §5.6、W5 设计 §2.5）：右侧面板，分工程、画布、背景、音频、输出时长五组。
 * 每次修改立即发送，一条历史，可以撤销；值以后端为准，刷新后显示。媒体的大小和状态取自资源清单（面板打开时按修订读）。
 */
export function SettingsDrawer(props: { session: EditorSession; open: boolean; focus?: SettingsGroup | undefined; onClose: () => void }) {
  const { session } = props;
  const state = useObservable(session.state$);
  const ref = useRef<HTMLElement>(null);
  useDomEvent(ref, 'close', (event) => event.target === ref.current && props.onClose());
  const settings = state.settings;
  const { resources } = useProjectResources(session, props.open);

  // 从导出页的修复入口打开时，滚到那一组并短暂高亮。
  useEffect(() => {
    if (!props.open || !props.focus) return;
    const group = ref.current?.querySelector<HTMLElement>(`[data-group="${props.focus}"]`);
    if (!group) return;
    group.scrollIntoView({ block: 'start', behavior: 'smooth' });
    group.classList.add('settings-group-focus');
    const timer = window.setTimeout(() => group.classList.remove('settings-group-focus'), 1600);
    return () => {
      window.clearTimeout(timer);
      group.classList.remove('settings-group-focus');
    };
  }, [props.open, props.focus]);

  return (
    <mdui-navigation-drawer ref={ref} open={props.open} modal close-on-esc close-on-overlay-click placement="right" className="settings-drawer">
      <div className="drawer-content">
        <header className="drawer-header">
          <span className="drawer-title">工程设置</span>
          <span className="spacer" />
          <mdui-button-icon aria-label="关闭工程设置" onClick={props.onClose}>
            <mdui-icon-close />
          </mdui-button-icon>
        </header>
        {state.summary.isReadOnly && <p className="hint">工程以只读方式打开，不能修改设置。</p>}
        {settings ? (
          <>
            <ProjectGroup session={session} name={state.summary.name} readOnly={state.summary.isReadOnly} />
            <CanvasGroup session={session} settings={settings} readOnly={state.summary.isReadOnly} />
            <BackgroundGroup session={session} settings={settings} readOnly={state.summary.isReadOnly} resources={resources} />
            <AudioGroup session={session} state={state} settings={settings} open={props.open} resources={resources} />
            <DurationGroup session={session} settings={settings} readOnly={state.summary.isReadOnly} />
          </>
        ) : (
          <p className="muted">后端没有提供工程设置的读取。</p>
        )}
      </div>
    </mdui-navigation-drawer>
  );
}

interface GroupProps {
  session: EditorSession;
  settings: ProjectSettings;
  readOnly: boolean;
}

/** 发一条设置命令；真实变化时提示 message。返回是否有变化。 */
async function send(session: EditorSession, command: ProjectCommand, message: string): Promise<boolean> {
  const response = await session.command(command);
  if (response?.changed) toast(message);
  return !!response?.changed;
}

function ProjectGroup({ session, name, readOnly }: { session: EditorSession; name: string; readOnly: boolean }) {
  const can = !readOnly && session.supportsCommand('project.rename');
  return (
    <section className="settings-group" aria-label="工程" data-group="project">
      <div className="section-title">工程</div>
      <CommitField
        label="名称"
        value={name}
        disabled={!can}
        helper="只改显示名称，不移动保存目录"
        validate={checkProjectName}
        onCommit={(v) => void send(session, { type: 'project.rename', name: v }, `工程名已改为“${v.trim()}”`)}
        className="drawer-field"
      />
    </section>
  );
}

/** 一组里的媒体操作：进行中的文字和失败原因留在这一组（失败时原来的媒体不变）。 */
function useMediaAction() {
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string>();
  const run = async (task: (progress: (step: string) => void) => Promise<void>) => {
    setError(undefined);
    setBusy('请稍候');
    try {
      await task(setBusy);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(null);
    }
  };
  return { busy, error, setError, run };
}

/** 发命令；被拒绝时把原因（session 的 notice）作为错误抛出，交给这一组显示。 */
async function commandOrThrow(session: EditorSession, command: ProjectCommand) {
  const response = await session.command(command);
  if (!response) throw new Error(session.state.notice?.message ?? '操作没有成功。');
  return response;
}

function CanvasGroup({ session, settings, readOnly }: GroupProps) {
  const size = `${settings.canvasWidth}x${settings.canvasHeight}`;
  const rate = `${settings.frameRateNumerator}/${settings.frameRateDenominator}`;
  const sizePreset = CANVAS_OPTIONS.some((o) => o.value === size);
  const ratePreset = FPS_OPTIONS.some((o) => o.value === rate);
  // 选了“自定义…”就显示输入框，即使当前值恰好是常用值。
  const [customSize, setCustomSize] = useState(false);
  const [customRate, setCustomRate] = useState(false);
  const canSize = !readOnly && session.supportsCommand('composition.setCanvasSize');
  const canRate = !readOnly && session.supportsCommand('composition.setFrameRate');

  const setSize = (width: number, height: number) =>
    void send(session, { type: 'composition.setCanvasSize', width, height }, `画布已改为 ${width} × ${height}`);
  const setRate = (numerator: number, denominator: number) =>
    void send(session, { type: 'composition.setFrameRate', numerator, denominator }, `帧率已改为 ${formatFrameRate({ numerator, denominator })}`);

  return (
    <section className="settings-group" aria-label="画布" data-group="canvas">
      <div className="section-title">画布</div>
      <Select
        label="尺寸"
        value={customSize || !sizePreset ? CUSTOM : size}
        options={[...CANVAS_OPTIONS, { value: CUSTOM, label: sizePreset ? '自定义…' : `自定义（${settings.canvasWidth} × ${settings.canvasHeight}）` }]}
        disabled={!canSize}
        onChange={(value) => {
          setCustomSize(value === CUSTOM);
          if (value === CUSTOM) return;
          const [width, height] = value.split('x').map(Number) as [number, number];
          setSize(width, height);
        }}
        className="drawer-field"
      />
      {(customSize || !sizePreset) && (
        <div className="dialog-grid">
          <CommitField
            label="宽"
            value={String(settings.canvasWidth)}
            disabled={!canSize}
            validate={(v) => (parseDimension(v).ok ? undefined : '请填正整数')}
            onCommit={(v) => setSize(Number(v.trim()), settings.canvasHeight)}
          />
          <CommitField
            label="高"
            value={String(settings.canvasHeight)}
            disabled={!canSize}
            validate={(v) => (parseDimension(v).ok ? undefined : '请填正整数')}
            onCommit={(v) => setSize(settings.canvasWidth, Number(v.trim()))}
          />
        </div>
      )}
      <Select
        label="帧率"
        value={customRate || !ratePreset ? CUSTOM : rate}
        options={[
          ...FPS_OPTIONS,
          {
            value: CUSTOM,
            label: ratePreset ? '自定义…' : `自定义（${formatFrameRate({ numerator: settings.frameRateNumerator, denominator: settings.frameRateDenominator })}）`,
          },
        ]}
        disabled={!canRate}
        onChange={(value) => {
          setCustomRate(value === CUSTOM);
          if (value === CUSTOM) return;
          const [numerator, denominator] = value.split('/').map(Number) as [number, number];
          setRate(numerator, denominator);
        }}
        className="drawer-field"
      />
      {(customRate || !ratePreset) && (
        <CommitField
          label="自定义帧率"
          value={settings.frameRateDenominator === 1 ? String(settings.frameRateNumerator) : rate}
          helper="例如 30、29.97 或 30000/1001"
          disabled={!canRate}
          validate={(v) => {
            const parsed = parseFrameRate(v);
            return parsed.ok ? undefined : parsed.error;
          }}
          onCommit={(v) => {
            const parsed = parseFrameRate(v);
            if (parsed.ok) setRate(parsed.value.numerator, parsed.value.denominator);
          }}
          className="drawer-field"
        />
      )}
      <p className="hint">已打的时间不会移动；吸附和逐帧微调按新的帧率计算。</p>
    </section>
  );
}

function BackgroundGroup({ session, settings, readOnly, resources }: GroupProps & { resources: ProjectResource[] | null }) {
  const color: Rgba = { red: settings.backgroundRed, green: settings.backgroundGreen, blue: settings.backgroundBlue, alpha: settings.backgroundAlpha };
  const canSet = !readOnly && session.supportsCommand('composition.setBackgroundColor');
  const setColor = (next: Rgba) => void send(session, { type: 'composition.setBackgroundColor', ...next }, `背景色已改为 ${formatColor(next)}`);
  const background = settings.background;
  const media = background && background.kind !== 'color' ? background : undefined;
  const resource = media && resources ? backgroundResource(resources, media.resourceId) : undefined;
  const thumbnail = useResourceObjectUrl(media?.kind === 'image' ? resource : undefined);
  const action = useMediaAction();
  const canMedia = !readOnly && session.supportsCommand('composition.setBackgroundAsset');

  const choose = (file: File) => {
    const kind = backgroundKindOf(file);
    if (!kind) {
      action.setError(`“${file.name}”不是图片或视频。`);
      return;
    }
    void action.run(async (progress) => {
      const assetId = await uploadMedia(client, file, progress);
      progress('正在设置背景');
      const response = await commandOrThrow(session, {
        type: 'composition.setBackgroundAsset',
        assetId,
        kind,
        ...(media ? { fit: media.fit } : {}),
        ...(kind === 'video' && media?.kind === 'video' ? { playback: media.playback } : {}),
      });
      if (response.changed) toast(`背景已${media ? '替换' : '设为'}${BACKGROUND_KIND_TEXT[kind]} ${file.name}`);
    });
  };
  const remove = () =>
    void action.run(async () => {
      if ((await commandOrThrow(session, { type: 'composition.clearBackground' })).changed) toast('已移除背景媒体，只用底色');
    });

  const name = uploadedName(resource?.assetId);
  const size = formatBytes(resource?.sizeBytes);
  return (
    <section className="settings-group" aria-label="背景" data-group="background">
      <div className="section-title">背景</div>
      <ColorField label="底色" value={color} onCommit={setColor} disabled={!canSet} />
      {media ? (
        <>
          <div className="background-media">
            {media.kind === 'image' ? (
              thumbnail ? <img className="background-thumb" src={thumbnail} alt="背景图片缩略图" /> : <div className="background-thumb placeholder"><mdui-icon-image /></div>
            ) : (
              <div className="background-thumb placeholder"><mdui-icon-movie /></div>
            )}
            <div className="background-info">
              <span className="background-name">{name ?? BACKGROUND_KIND_TEXT[media.kind]}</span>
              <span className="hint">
                {[name ? BACKGROUND_KIND_TEXT[media.kind] : undefined, size, resource ? resourceStatusText(resource) : resources ? '资源清单里没有' : undefined].filter(Boolean).join(' · ')}
              </span>
            </div>
          </div>
          {resource && !resourceUsable(resource) && <p className="dialog-error">背景媒体{resourceStatusText(resource)}，预览和导出用不了它。可以替换或移除。</p>}
          <div className="dialog-grid">
            <Select
              label="适配方式"
              value={media.fit}
              options={FIT_OPTIONS}
              disabled={readOnly || !session.supportsCommand('composition.setBackgroundFit') || !!action.busy}
              onChange={(fit) => void send(session, { type: 'composition.setBackgroundFit', fit }, `适配方式已改为${FIT_OPTIONS.find((o) => o.value === fit)!.label}`)}
            />
            {media.kind === 'video' && (
              <Select
                label="播放"
                value={media.playback}
                options={PLAYBACK_OPTIONS}
                disabled={readOnly || !session.supportsCommand('composition.setBackgroundPlayback') || !!action.busy}
                onChange={(playback) => void send(session, { type: 'composition.setBackgroundPlayback', playback }, `视频播完后${PLAYBACK_OPTIONS.find((o) => o.value === playback)!.label}`)}
              />
            )}
          </div>
          <div className="form-actions">
            <FileButton label="替换…" accept={BACKGROUND_FILE_ACCEPT} disabled={!canMedia || !!action.busy} onPick={choose} />
            <mdui-button variant="text" disabled={readOnly || !session.supportsCommand('composition.clearBackground') || !!action.busy} onClick={remove}>
              移除
            </mdui-button>
          </div>
          <p className="hint">底色在媒体透明处或留白处可见。</p>
        </>
      ) : (
        <div className="form-actions">
          <FileButton label="选择图片或视频…" accept={BACKGROUND_FILE_ACCEPT} disabled={!canMedia || !!action.busy} onPick={choose} />
        </div>
      )}
      {action.busy && (
        <div className="dialog-busy">
          <mdui-linear-progress />
          <span>{action.busy}</span>
        </div>
      )}
      {action.error && <p className="dialog-error">{action.error}</p>}
    </section>
  );
}

function AudioGroup(props: { session: EditorSession; state: EditorState; settings: ProjectSettings; open: boolean; resources: ProjectResource[] | null }) {
  const { session, state, settings } = props;
  const hasAudio = !!state.summary.audioAssetId;
  const resource = props.resources ? audioResource(props.resources) : undefined;
  const fileName = uploadedName(state.summary.audioAssetId);
  const media = useMediaAction();
  const analysis = state.summary.audioAnalysis;
  const adopted = analysis?.status === 'available';
  const audioDurationUs = Number(settings.audioDurationUs);
  const [capabilities, setCapabilities] = useState<AnalysisCapabilities | null>(null);
  const [running, setRunning] = useState<{ progress?: AnalysisProgress } | null>(null);
  const [error, setError] = useState<string>();
  const abort = useRef<AbortController | null>(null);

  useEffect(() => {
    if (!props.open || capabilities) return;
    let active = true;
    client
      .analysisCapabilities()
      .then((value) => active && setCapabilities(value))
      .catch(() => active && setCapabilities(null));
    return () => {
      active = false;
    };
  }, [props.open, capabilities]);
  useEffect(() => () => abort.current?.abort(), []);

  const readOnly = state.summary.isReadOnly;
  const canAnalyze = !readOnly && !!capabilities?.enabled && session.supportsCommand('audio.analysis.apply');
  const reason = capabilities && !capabilities.enabled ? (capabilities.disabledReason?.message ?? '后端的音频分析不可用。') : undefined;

  const analyze = async () => {
    setError(undefined);
    setRunning({});
    abort.current = new AbortController();
    try {
      const response = await session.analyzeAudio((progress) => setRunning(progress ? { progress } : {}), abort.current.signal);
      if (response?.changed) toast('已采用音频分析');
      else if (response) toast('分析结果与当前采用的相同');
      else setError(session.state.notice?.message ?? '采用没有成功。');
    } catch (e) {
      if (!abort.current?.signal.aborted) setError(`分析失败：${errorText(e)}`);
    } finally {
      setRunning(null);
      abort.current = null;
    }
  };
  const clear = () => void send(session, { type: 'audio.analysis.clear' }, '已清除音频分析');
  const progress = running ? analysisProgressText(running.progress, audioDurationUs) : undefined;
  const canReplace = !readOnly && session.supportsCommand('audio.replaceAsset');

  const replace = (file: File) =>
    void media.run(async (step) => {
      const hadAnalysis = adopted;
      const response = await replaceAudio(client, session, file, step);
      if (!response) throw new Error(session.state.notice?.message ?? '替换音频没有成功。');
      if (response.changed) toast(hasAudio ? `已替换为 ${file.name}；${hadAnalysis ? '已清除音频分析，' : ''}输出时长没有变` : `已选择音频 ${file.name}`);
    });
  const removeAudio = async () => {
    const ok = await confirmAction({
      headline: '移除音频？',
      description: '移除后没有时间轴和播放控制，音频分析也会清除；输出时长不变。可以撤销。',
      confirmText: '移除',
    });
    if (!ok) return;
    await media.run(async () => {
      if ((await commandOrThrow(session, { type: 'audio.clear' })).changed) toast('已移除音频');
    });
  };

  return (
    <section className="settings-group" aria-label="音频" data-group="audio">
      <div className="section-title">音频</div>
      <dl className="settings-facts">
        <div>
          <dt>文件</dt>
          <dd className="audio-file">{hasAudio ? (fileName ?? '（重开后不显示文件名）') : '没有音频'}</dd>
        </div>
        <div>
          <dt>时长</dt>
          <dd>{audioDurationUs > 0 ? formatTime(audioDurationUs) : hasAudio ? '未知' : '—'}</dd>
        </div>
        {hasAudio && resource && (
          <div>
            <dt>状态</dt>
            <dd className="audio-status">{[audioStatusText(resource), formatBytes(resource.sizeBytes)].filter(Boolean).join(' · ')}</dd>
          </div>
        )}
        <div>
          <dt>音频分析</dt>
          <dd className="analysis-status">
            {adopted
              ? `已采用 · ${analysis.pointCount ?? 0} 个分析点，覆盖 ${analysis.durationUs ? formatTime(Number(analysis.durationUs)) : '整首'}`
              : '未分析'}
          </dd>
        </div>
      </dl>
      <div className="form-actions">
        <FileButton label={hasAudio ? '替换音频…' : '选择音频…'} accept={AUDIO_FILE_ACCEPT} disabled={!canReplace || !!media.busy || !!running} onPick={replace} />
        {hasAudio && (
          <mdui-button variant="text" disabled={readOnly || !session.supportsCommand('audio.clear') || !!media.busy || !!running} onClick={() => void removeAudio()}>
            移除音频
          </mdui-button>
        )}
      </div>
      {media.busy && (
        <div className="dialog-busy">
          <mdui-linear-progress />
          <span>{media.busy}</span>
        </div>
      )}
      {media.error && <p className="dialog-error">{media.error}</p>}
      <div className="form-actions">
        <mdui-button variant="tonal" loading={!!running} disabled={!canAnalyze || !hasAudio || !!running || !!media.busy} onClick={() => void analyze()}>
          {adopted ? '重新分析并采用' : '分析并采用'}
        </mdui-button>
        <mdui-button variant="text" disabled={readOnly || !adopted || !!running || !session.supportsCommand('audio.analysis.clear')} onClick={clear}>
          清除
        </mdui-button>
      </div>
      {progress && (
        <div className="analysis-progress">
          {progress.fraction === undefined ? <mdui-linear-progress /> : <mdui-linear-progress value={progress.fraction} />}
          <span className="hint">{progress.text}</span>
        </div>
      )}
      {reason && <p className="hint">{reason}</p>}
      {error && <p className="dialog-error">{error}</p>}
      <p className="hint">分析音乐的低音和节拍，供编排里的音频驱动效果使用。分析时可以继续编辑；采用和清除都可以撤销。</p>
    </section>
  );
}

function DurationGroup({ session, settings, readOnly }: GroupProps) {
  const durationUs = settings.outputDurationUs !== undefined ? Number(settings.outputDurationUs) : undefined;
  const can = (type: ProjectCommand['type']) => !readOnly && session.supportsCommand(type);

  const applyLyricsEnd = async () => {
    const response = await session.command({ type: 'composition.useLyricsEnd' });
    if (response?.changed) toast('输出时长已改为歌词末尾');
    else if (response) toast('歌词还没有可用的结束时间，输出时长没有改变');
  };

  return (
    <section className="settings-group" aria-label="输出时长" data-group="duration">
      <div className="section-title">输出时长</div>
      <div className={durationUs === undefined ? 'duration-value undetermined' : 'duration-value'}>
        {durationUs === undefined ? '未确定' : formatTime(durationUs)}
      </div>
      {durationUs === undefined && <p className="hint">导出前必须确定输出时长。</p>}
      <div className="form-actions">
        <mdui-button
          variant="tonal"
          disabled={!can('composition.useAudioDuration')}
          onClick={() => void send(session, { type: 'composition.useAudioDuration' }, '输出时长已改为音频时长')}
        >
          按音频时长
        </mdui-button>
        <mdui-button variant="tonal" disabled={!can('composition.useLyricsEnd')} onClick={() => void applyLyricsEnd()}>
          按歌词末尾
        </mdui-button>
        <mdui-button
          variant="text"
          disabled={durationUs === undefined || !can('composition.setOutputDuration')}
          onClick={() => void send(session, { type: 'composition.setOutputDuration', durationUs: null }, '输出时长已设为未确定')}
        >
          设为未确定
        </mdui-button>
      </div>
      <CommitField
        label="自定义时长"
        value={durationUs === undefined ? '' : formatTime(durationUs)}
        placeholder="例如 3:45.000"
        helper="分:秒.毫秒，Enter 或离开输入框时提交"
        disabled={!can('composition.setOutputDuration')}
        validate={(v) => {
          const us = parseTime(v);
          return us !== null && us > 0 ? undefined : '例如 3:45.000 或 225.5';
        }}
        onCommit={(v) => {
          const us = parseTime(v);
          if (us !== null && us > 0) void send(session, { type: 'composition.setOutputDuration', durationUs: toMicros(us) }, `输出时长已改为 ${formatTime(us)}`);
        }}
        className="drawer-field"
      />
      <p className="hint">“按音频时长”“按歌词末尾”只取一次当时的值，之后改音频或歌词不会自动跟着变。</p>
    </section>
  );
}
