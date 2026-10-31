import { useState } from 'react';
import { client } from '../connection';
import { AUDIO_FILE_ACCEPT } from '../model/new-project';
import { errorText, type EditorSession, type EditorState } from '../session/editor-session';
import { replaceAudio } from '../session/project-media';
import { toast } from '../ui/feedback';
import { FileButton } from '../ui/fields';
import { useObservableSelector } from '../ui/hooks';

/**
 * 工程没有音频时底部区的空态（W5 设计 §1）：“还没有音频 · [选择音频…]”，选好就直接替换音频（一条历史）。
 * 有音频时不显示；时间轴和播放控制要有音频才显示，这一点不变。
 */
export function NoAudioBar(props: { session: EditorSession }) {
  const { session } = props;
  const summary = useObservableSelector<EditorState, EditorState['summary']>(session.state$, (s) => s.summary);
  const [busy, setBusy] = useState<string | null>(null);
  if (summary.audioAssetId) return null;
  const canReplace = !summary.isReadOnly && session.supportsCommand('audio.replaceAsset');

  const pick = async (file: File) => {
    try {
      const response = await replaceAudio(client, session, file, setBusy);
      if (response?.changed) toast(`已选择音频 ${file.name}`);
    } catch (error) {
      session.notify({ level: 'error', message: `选择音频失败：${errorText(error)}` });
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="no-audio-bar">
      <mdui-icon-music-off />
      <span className="no-audio-text">{busy ?? '还没有音频'}</span>
      {busy ? (
        <mdui-circular-progress />
      ) : (
        <FileButton label="选择音频…" accept={AUDIO_FILE_ACCEPT} disabled={!canReplace} onPick={(file) => void pick(file)} />
      )}
      <span className="hint">有音频后才显示播放控制和时间轴。</span>
    </div>
  );
}
