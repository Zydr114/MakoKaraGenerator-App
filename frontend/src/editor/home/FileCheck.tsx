import { useEffect, useState, type ReactNode } from 'react';
import type { LyricsPlanState } from '../lyrics/LyricsPlanPanel';
import { encodingLabel, planErrorText, planNotes, planStats, previewRows } from '../model/lyrics-import';
import { audioFormatName, audioProbeText, wavFormat, waveBars, type AudioProbe } from '../model/new-project';
import { cls } from '../ui/cls';
import { Icon, type IconName } from '../ui/Icon';
import { Spinner } from '../ui/indicators';

const BARS = 110;
const PREVIEW_LINES = 4;
/** 只用来画波形和读时长、声道，低采样率省内存；时长以重采样前后相同为准。 */
const PROBE_SAMPLE_RATE = 8000;

export type AudioProbeState = { status: 'none' } | { status: 'loading' } | { status: 'ok'; probe: AudioProbe } | { status: 'error'; message: string };

/**
 * 新建工程的音频检查（用户决定 7）：在浏览器里解码，得到时长、声道和波形；WAV 再从文件头读采样率和精确时长。
 * 不发请求；浏览器解不开时只是提示，新建时后端仍会尝试。换文件时丢弃上一次的结果。
 */
export function useAudioProbe(file: File | null): AudioProbeState {
  const [state, setState] = useState<AudioProbeState>({ status: 'none' });
  useEffect(() => {
    if (!file) {
      setState({ status: 'none' });
      return;
    }
    let active = true;
    setState({ status: 'loading' });
    void (async () => {
      try {
        const bytes = await file.arrayBuffer();
        // 先读文件头：decodeAudioData 会把这块内存转走。
        const wav = wavFormat(bytes);
        const buffer = await new OfflineAudioContext(1, 1, PROBE_SAMPLE_RATE).decodeAudioData(bytes);
        const channels = Array.from({ length: buffer.numberOfChannels }, (_, i) => buffer.getChannelData(i));
        if (!active) return;
        setState({
          status: 'ok',
          probe: {
            durationUs: wav?.durationUs ?? Math.round(buffer.duration * 1_000_000),
            channels: wav?.channels ?? buffer.numberOfChannels,
            format: audioFormatName(file),
            bars: waveBars(channels, BARS),
            ...(wav ? { sampleRate: wav.sampleRate } : {}),
          },
        });
      } catch (e) {
        if (active) setState({ status: 'error', message: e instanceof Error ? e.message : String(e) });
      }
    })();
    return () => {
      active = false;
    };
  }, [file]);
  return state;
}

/**
 * 新建工程对话框右边的“文件检查”（稿件 #new）：音频的波形和规格，歌词的识别结果、统计、提示和前几行。
 * 都只是查看；格式、编码不对时在左边改选。
 */
export function FileCheck(props: { audio: File | null; probe: AudioProbeState; lyrics: File | null; plan: LyricsPlanState }) {
  return (
    <div className="file-check">
      <h3 className="file-check-head">文件检查</h3>
      <AudioCheck audio={props.audio} probe={props.probe} />
      <LyricsCheck lyrics={props.lyrics} plan={props.plan} />
    </div>
  );
}

type CheckLevel = 'none' | 'loading' | 'ok' | 'warn' | 'error';

const LEVEL_ICON: Record<Exclude<CheckLevel, 'loading'>, IconName> = { none: 'radio_button_unchecked', ok: 'check_circle', warn: 'warning', error: 'error' };

function Section(props: { id: string; level: CheckLevel; title: ReactNode; children?: ReactNode }) {
  return (
    <section className={cls('dsec', props.level)} data-check={props.id} data-state={props.level} aria-live="polite">
      <h4>
        {props.level === 'loading' ? <Spinner /> : <Icon name={LEVEL_ICON[props.level]} />}
        {props.title}
      </h4>
      {props.children}
    </section>
  );
}

function AudioCheck({ audio, probe }: { audio: File | null; probe: AudioProbeState }) {
  if (!audio || probe.status === 'none')
    return (
      <Section id="audio" level="none" title="没有选音频">
        <p className="small">之后可以在工程设置里选择。</p>
      </Section>
    );
  if (probe.status === 'loading') return <Section id="audio" level="loading" title="正在读取音频……" />;
  if (probe.status === 'error')
    return (
      <Section id="audio" level="warn" title="浏览器无法解码这个文件">
        <p className="small">新建时后端仍会尝试读取。{probe.message}</p>
      </Section>
    );
  const { bars } = probe.probe;
  const w = 1000 / bars.length;
  const path = bars
    .map((v, i) => {
      const h = Math.max(1.5, v * 46);
      return `M${(i * w + w * 0.18).toFixed(2)} ${(50 - h).toFixed(2)}h${(w * 0.64).toFixed(2)}v${(h * 2).toFixed(2)}h-${(w * 0.64).toFixed(2)}z`;
    })
    .join('');
  return (
    <Section id="audio" level="ok" title="音频可用">
      <div className="wavebox" aria-hidden>
        <svg viewBox="0 0 1000 100" preserveAspectRatio="none">
          <path d={path} />
        </svg>
      </div>
      <p className="small audio-facts">{audioProbeText(probe.probe)}</p>
      {/\.mp3$/i.test(audio.name) && <p className="small">MP3 在浏览器与后端之间可能有几十毫秒的时间偏移；对时间要求高时请用 WAV。</p>}
    </Section>
  );
}

function LyricsCheck({ lyrics, plan: state }: { lyrics: File | null; plan: LyricsPlanState }) {
  if (!lyrics)
    return (
      <Section id="lyrics" level="none" title="没有选歌词">
        <p className="small">之后可以在歌词页导入。</p>
      </Section>
    );
  const plan = state.plan;
  if (state.error) return <Section id="lyrics" level="error" title={state.error} />;
  if (!plan) return <Section id="lyrics" level="loading" title="正在识别歌词文件……" />;
  const error = planErrorText(plan);
  const notes = planNotes(plan, 'create');
  if (error || !plan.report)
    return (
      <Section id="lyrics" level="error" title="无法识别">
        <p className="errline">{error ?? '没有识别结果。'}</p>
        <p className="small">可以在左边改选格式或编码再试，或换一个文件。</p>
      </Section>
    );
  const stats = planStats(plan);
  const rows = previewRows(plan, PREVIEW_LINES);
  const more = plan.previewLines.length - rows.length;
  return (
    <Section id="lyrics" level={state.loading ? 'loading' : notes.length > 0 ? 'warn' : 'ok'} title={`识别为${plan.report.importerName}，${encodingLabel(plan.report.encodingName)}`}>
      {stats && (
        <div className="stats">
          <span>
            <b>{stats.lines}</b>行
          </span>
          {stats.wordTimed > 0 && (
            <span>
              <b>{stats.wordTimed}</b>行带逐字时间
            </span>
          )}
          {stats.lineTimed > 0 && (
            <span>
              <b>{stats.lineTimed}</b>行只有行时间
            </span>
          )}
          {stats.untimed > 0 && (
            <span>
              <b>{stats.untimed}</b>行没有时间
            </span>
          )}
        </div>
      )}
      {notes.map((note) => (
        <p key={note} className="small check-note">
          {note}
        </p>
      ))}
      {rows.length > 0 && (
        <ol className="found">
          {rows.map((row) => (
            <li key={row.id}>
              <span className="found-time">{row.start ?? '—'}</span>
              <span className="found-text">
                <span className="plan-line-text">{row.text}</span>
                {row.ruby && <span className="plan-line-ruby">{row.ruby}</span>}
              </span>
            </li>
          ))}
        </ol>
      )}
      {more > 0 && <p className="small faint">还有 {more} 行</p>}
    </Section>
  );
}
