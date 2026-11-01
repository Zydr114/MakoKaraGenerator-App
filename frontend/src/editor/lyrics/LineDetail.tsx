import type { AudioController } from '../audio/audio-controller';
import { isTimed, type EditorLine } from '../model/lyrics';
import { formatTime } from '../model/time';
import type { EditorSession, EditorState } from '../session/editor-session';
import { toast } from '../ui/legacy/feedback';
import { CommitField } from '../ui/legacy/fields';
import { RubySection } from './RubySection';
import { StatusBadge } from './TimeBar';
import { TokenSection } from './TokenSection';
import { TranslationSection } from './TranslationSection';

/** 行编辑区：选中行的正文、文本单元、注音片段与翻译关联。时间字段在底部的时间栏。 */
export function LineDetail(props: {
  session: EditorSession;
  state: EditorState;
  audio: AudioController | null;
  line: EditorLine;
  lineNumber: number;
  tokenId?: string | undefined;
  segmentId?: string | undefined;
}) {
  const { session, audio, line } = props;
  const timed = isTimed(line);
  const target = { branchId: line.branchId, lineId: line.id };

  const setText = async (text: string) => {
    const response = await session.command({ type: 'lyrics.setLineText', target, text });
    const feedback = response?.changed ? response.feedback : undefined;
    if (feedback) toast(`正文已更新：保留 ${feedback.preservedTokenCount ?? 0} 个文本单元的时间，重新拆分 ${feedback.resetTokenCount ?? 0} 个`);
  };

  return (
    <div className="line-detail">
      <header className="line-detail-header">
        <span className="line-number">第 {props.lineNumber} 行</span>
        <span>{timed ? `${formatTime(line.startUs)} – ${formatTime(line.endUs)}` : '未打轴'}</span>
        {line.status && <StatusBadge status={line.status} />}
        <span className="spacer" />
        <mdui-button variant="text" disabled={!timed || !audio} onClick={() => timed && audio?.seek(line.startUs)}>
          <mdui-icon-my-location slot="icon" />
          定位到此行
        </mdui-button>
        <mdui-button variant="text" disabled={!timed || !audio} onClick={() => timed && audio?.playRange(line.startUs, line.endUs)}>
          <mdui-icon-headphones slot="icon" />
          试听此行
        </mdui-button>
      </header>
      <CommitField
        key={line.id}
        label="正文"
        value={line.text}
        placeholder="（空行）"
        helper="Enter 或离开输入框时提交，Esc 撤回"
        disabled={!session.supportsCommand('lyrics.setLineText')}
        onCommit={(text) => void setText(text)}
        className="line-text-field"
      />

      <TokenSection session={session} line={line} tokenId={props.tokenId} />

      <RubySection session={session} line={line} tokenId={props.tokenId} segmentId={props.segmentId} />

      <TranslationSection session={session} state={props.state} line={line} />
    </div>
  );
}
