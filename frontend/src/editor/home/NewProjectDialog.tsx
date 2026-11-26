import { useEffect, useRef, useState } from 'react';
import type { Composition, TokenSplitRuleName } from '../../protocol';
import { client } from '../connection';
import { planReady, useLyricsPlan, type PlanChoices } from '../lyrics/LyricsPlanPanel';
import { AUTO, LYRICS_FILE_ACCEPT, chosen, encodingChoices, formatChoices, needsFormatChoice, recommendedSplitRule } from '../model/lyrics-import';
import { formatBytes } from '../model/media';
import { AUDIO_FILE_ACCEPT, KIND_TEXT, defaultProjectName, newProjectKind } from '../model/new-project';
import { CANVAS_OPTIONS, FPS_OPTIONS } from '../model/settings';
import { SPLIT_RULES } from '../model/split-rules';
import { EditorSession, errorText } from '../session/editor-session';
import { createProject } from '../session/project-files';
import { Switch } from '../ui/choices';
import { DialogSteps } from '../ui/Dialog';
import { FileBox, Select, TextField } from '../ui/fields';
import { FormDialog } from '../ui/FormDialog';
import { ProgressBar } from '../ui/indicators';
import { FileCheck, useAudioProbe } from './FileCheck';
import './home.css';

const SPLIT_OPTIONS = SPLIT_RULES.map((rule) => ({ value: rule.name, label: rule.label }));
const STEPS = ['文件', '画面与拆分'] as const;

type CanvasValue = (typeof CANVAS_OPTIONS)[number]['value'];
type FpsValue = (typeof FPS_OPTIONS)[number]['value'];

function composition(canvas: CanvasValue, fps: FpsValue): Composition {
  const [w, h] = canvas.split('x').map(Number) as [number, number];
  const [num, den] = fps.split('/').map(Number) as [number, number];
  return { canvasWidth: w, canvasHeight: h, frameRateNumerator: num, frameRateDenominator: den };
}

/** 值存在时才放进对象（exactOptionalPropertyTypes）。 */
function pick<K extends string>(key: K, value: string | undefined): { [P in K]?: string } {
  return value ? ({ [key]: value } as { [P in K]?: string }) : {};
}

const NO_CHOICES: PlanChoices = { importer: AUTO, encoding: AUTO, source: AUTO };

/**
 * 新建工程（W5 设计 §1、§2.6，稿件 #new）：两步。左边填写，右边幕后区检查文件。
 * 第一步选音频、歌词（都可以不选，也可以拖进来）和名称；格式或编码有疑问时可以改选。
 * 第二步是画布、帧率，有歌词时才有拆分规则和逐字估算。成功后进入歌词页。
 */
export function NewProjectDialog(props: { open: boolean; onClose: () => void; onCreated: (session: EditorSession) => void }) {
  const [step, setStep] = useState<0 | 1>(0);
  const [audio, setAudio] = useState<File | null>(null);
  const [lyricsFile, setLyricsFile] = useState<File | null>(null);
  const [choices, setChoices] = useState(NO_CHOICES);
  const [name, setName] = useState('');
  const [canvas, setCanvas] = useState<CanvasValue>('1920x1080');
  const [fps, setFps] = useState<FpsValue>('30/1');
  const [split, setSplit] = useState<TokenSplitRuleName>('mixed');
  const [estimate, setEstimate] = useState(false);
  const splitEdited = useRef(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const lyrics = useLyricsPlan(lyricsFile, choices);
  const probe = useAudioProbe(audio);

  // 每次打开都从头开始。
  useEffect(() => {
    if (!props.open) return;
    setStep(0);
    setAudio(null);
    setLyricsFile(null);
    setName('');
    setSplit('mixed');
    setEstimate(false);
    splitEdited.current = false;
    setError(null);
  }, [props.open]);

  const chooseLyrics = (file: File | null) => {
    setLyricsFile(file);
    setChoices(NO_CHOICES);
    splitEdited.current = false;
    if (!file) {
      setSplit('mixed');
      setEstimate(false);
    }
  };

  const lyricsReady = !lyricsFile || planReady(lyrics);
  const kind = newProjectKind(!!audio, !!lyricsFile);
  const fallbackName = defaultProjectName(audio?.name, lyricsFile?.name);
  const plan = lyrics.plan;
  const showFormats = !!plan && needsFormatChoice(plan);
  const set = (patch: Partial<PlanChoices>) => setChoices({ ...choices, ...patch });

  useEffect(() => {
    if (!plan || splitEdited.current) return;
    const next = recommendedSplitRule(plan);
    setSplit(next);
    if (next === 'none') setEstimate(false);
  }, [plan]);

  const submit = async () => {
    if (step === 0) {
      setStep(1);
      return;
    }
    setError(null);
    setBusy('请稍候');
    try {
      const comp = composition(canvas, fps);
      const summary = await createProject(
        client,
        {
          name: name.trim() || fallbackName,
          audio,
          lyrics: lyricsFile && lyrics.assetId ? { file: lyricsFile, assetId: lyrics.assetId, ...pick('importerId', chosen(choices.importer)), ...pick('encodingName', chosen(choices.encoding)) } : null,
          composition: comp,
          splitRule: SPLIT_RULES.find((rule) => rule.name === split)!.code,
          estimateSplitTimes: split === 'none' ? false : estimate,
        },
        setBusy,
      );
      setBusy('正在读取工程');
      props.onCreated(await EditorSession.attach(client, summary.projectSessionId, { composition: comp }));
    } catch (e) {
      setError(`新建失败：${errorText(e)}`);
    } finally {
      setBusy(null);
    }
  };

  return (
    <FormDialog
      open={props.open}
      headline="新建工程"
      headerExtra={<DialogSteps steps={STEPS} current={step} />}
      submitText={step === 0 ? '下一步' : '新建'}
      submitDisabled={step === 0 && !lyricsReady}
      busy={busy !== null}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="new-project-dialog"
      footerNote={step === 0 ? '下一步设置画布、帧率和文本单元拆分' : undefined}
      side={<FileCheck audio={audio} probe={probe} lyrics={lyricsFile} plan={lyrics} />}
      {...(step === 1 ? { secondaryAction: { text: '上一步', onClick: () => setStep(0) } } : {})}
    >
      {step === 0 ? (
        <div className="new-form">
          <FileBox
            label="音频"
            icon="music_note"
            accept={AUDIO_FILE_ACCEPT}
            file={audio}
            detail={formatBytes(audio?.size)}
            emptyText="不选：之后可以在工程设置里选择"
            rejectText={(file) => `“${file.name}”不是音频文件。`}
            onChange={setAudio}
          />
          <FileBox
            label="歌词"
            icon="description"
            accept={LYRICS_FILE_ACCEPT}
            file={lyricsFile}
            detail={formatBytes(lyricsFile?.size)}
            emptyText="不选：之后可以在歌词页导入"
            rejectText={(file) => `“${file.name}”不是支持的歌词文件（LRC、TXT、SRT、VTT、ASS、TTML）。`}
            onChange={chooseLyrics}
          />
          <TextField label="工程名" value={name} placeholder={fallbackName} helper={`第一次保存时也用作建议的目录名；不填时用“${fallbackName}”`} onChange={setName} />
          {showFormats && (
            <div className="g2">
              <Select label="格式" value={choices.importer} options={formatChoices(plan)} onChange={(importer) => set({ importer })} />
              <Select label="编码" value={choices.encoding} options={encodingChoices(plan)} onChange={(encoding) => set({ encoding })} />
            </div>
          )}
        </div>
      ) : (
        <div className="new-form">
          <p className="new-kind">{KIND_TEXT[kind]}</p>
          <div className="g2">
            <Select label="画布" value={canvas} options={CANVAS_OPTIONS} onChange={setCanvas} />
            <Select label="帧率" value={fps} options={FPS_OPTIONS} onChange={setFps} />
          </div>
          {lyricsFile && (
            <Select
              label="拆分歌词元素"
              value={split}
              options={SPLIT_OPTIONS}
              onChange={(value) => {
                splitEdited.current = true;
                setSplit(value);
                if (value === 'none') setEstimate(false);
              }}
            />
          )}
          {lyricsFile && (
            <Switch
              label="按字数估算逐字时间（结果标记为“估算”）"
              checked={estimate}
              disabled={split === 'none'}
              onChange={setEstimate}
            />
          )}
        </div>
      )}
      {busy && (
        <div className="busyline">
          <ProgressBar label={busy} />
          <span>{busy}</span>
        </div>
      )}
      {error && <p className="errline" role="alert">{error}</p>}
    </FormDialog>
  );
}
