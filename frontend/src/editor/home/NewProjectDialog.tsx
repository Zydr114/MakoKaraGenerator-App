import { useEffect, useState } from 'react';
import type { Composition, TokenSplitRuleName } from '../../protocol';
import { client } from '../connection';
import { LyricsPlanPanel, planReady, useLyricsPlan, type PlanChoices } from '../lyrics/LyricsPlanPanel';
import { AUTO, LYRICS_FILE_ACCEPT, chosen } from '../model/lyrics-import';
import { AUDIO_FILE_ACCEPT, KIND_TEXT, defaultProjectName, newProjectKind } from '../model/new-project';
import { CANVAS_OPTIONS, FPS_OPTIONS } from '../model/settings';
import { SPLIT_RULES } from '../model/split-rules';
import { EditorSession, errorText } from '../session/editor-session';
import { createProject } from '../session/project-files';
import { FormDialog } from '../ui/legacy/FormDialog';
import { FilePicker, Select, Switch, TextField } from '../ui/legacy/fields';

const SPLIT_OPTIONS = SPLIT_RULES.map((rule) => ({ value: rule.name, label: rule.label }));
const PREVIEW_LINES = 3;

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
 * 新建工程（W5 设计 §1、§2.6）：两步。
 * 第一步选音频、歌词（都可以不选）和名称，选了歌词就显示识别结果，格式或编码不对时可以改选；
 * 第二步是画布、帧率，有歌词时才有拆分规则和逐字估算。成功后进入歌词页。
 */
export function NewProjectDialog(props: { open: boolean; onClose: () => void; onCreated: (session: EditorSession) => void }) {
  const [step, setStep] = useState<1 | 2>(1);
  const [audio, setAudio] = useState<File | null>(null);
  const [lyricsFile, setLyricsFile] = useState<File | null>(null);
  const [choices, setChoices] = useState(NO_CHOICES);
  const [name, setName] = useState('');
  const [canvas, setCanvas] = useState<CanvasValue>('1920x1080');
  const [fps, setFps] = useState<FpsValue>('30/1');
  const [split, setSplit] = useState<TokenSplitRuleName>('mixed');
  const [estimate, setEstimate] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const lyrics = useLyricsPlan(lyricsFile, choices);

  // 每次打开都从头开始。
  useEffect(() => {
    if (!props.open) return;
    setStep(1);
    setAudio(null);
    setLyricsFile(null);
    setName('');
    setError(null);
  }, [props.open]);

  const chooseLyrics = (file: File | null) => {
    setLyricsFile(file);
    setChoices(NO_CHOICES);
  };

  const lyricsReady = !lyricsFile || planReady(lyrics);
  const kind = newProjectKind(!!audio, !!lyricsFile);
  const fallbackName = defaultProjectName(audio?.name, lyricsFile?.name);

  const submit = async () => {
    if (step === 1) {
      setStep(2);
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
          estimateSplitTimes: estimate,
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
      headline={step === 1 ? '新建工程 · 选择文件' : '新建工程 · 画面'}
      submitText={step === 1 ? '下一步' : '新建'}
      submitDisabled={step === 1 && !lyricsReady}
      busy={busy !== null}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="new-project-dialog"
      {...(step === 2 ? { secondaryAction: { text: '上一步', onClick: () => setStep(1) } } : {})}
    >
      {step === 1 ? (
        <div className="dialog-form">
          <FilePicker label="选择音频" accept={AUDIO_FILE_ACCEPT} file={audio} clearable emptyText="不选：之后可以在设置里选择" onChange={setAudio} />
          <FilePicker label="选择歌词" accept={LYRICS_FILE_ACCEPT} file={lyricsFile} clearable emptyText="不选：之后可以在歌词页导入" onChange={chooseLyrics} />
          {lyricsFile && <LyricsPlanPanel state={lyrics} purpose="create" choices={choices} onChange={setChoices} previewCount={PREVIEW_LINES} />}
          <TextField label="工程名" value={name} placeholder={fallbackName} helper={`不填时用“${fallbackName}”`} onChange={setName} />
        </div>
      ) : (
        <div className="dialog-form">
          <p className="muted">{KIND_TEXT[kind]}</p>
          <div className="dialog-grid">
            <Select label="画布" value={canvas} options={CANVAS_OPTIONS} onChange={setCanvas} />
            <Select label="帧率" value={fps} options={FPS_OPTIONS} onChange={setFps} />
          </div>
          {lyricsFile && <Select label="拆分歌词元素" value={split} options={SPLIT_OPTIONS} onChange={setSplit} />}
          {lyricsFile && <Switch label="按字数估算逐字时间（结果标记为“估算”）" checked={estimate} onChange={setEstimate} />}
          {audio && /\.mp3$/i.test(audio.name) && <p className="hint">MP3 在浏览器与后端之间可能有几十毫秒的时间偏移；对时间要求高时请用 WAV。</p>}
        </div>
      )}
      {busy && (
        <div className="dialog-busy">
          <mdui-linear-progress />
          <span>{busy}</span>
        </div>
      )}
      {error && <p className="dialog-error">{error}</p>}
    </FormDialog>
  );
}
