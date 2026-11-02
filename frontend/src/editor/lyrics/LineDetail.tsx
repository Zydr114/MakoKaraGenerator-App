import type { AppearanceStore } from '../appearance/appearance-store';
import type { AudioController } from '../audio/audio-controller';
import { isTimed, type EditorLine } from '../model/lyrics';
import { formatTime } from '../model/time';
import type { OrchestrationStore } from '../orchestration/orchestration-store';
import type { EditorSession, EditorState } from '../session/editor-session';
import { Button, IconButton } from '../ui/Button';
import { toast } from '../ui/feedback';
import { CommitField } from '../ui/fields';
import { Menu, MenuTrigger } from '../ui/Menu';
import { lineActionItems, type LineAction, type LineActions } from './line-actions';
import { LineLinks } from './LineLinks';
import { StatusTag } from './LineList';
import { RubySection } from './RubySection';
import { TokenSection } from './TokenSection';
import { TranslationSection } from './TranslationSection';

/**
 * 行编辑区（界面改版稿件 #lyrics）：标题行是行号、起止、时间状态、定位、试听和行操作；
 * 下面是正文、文本单元、注音与翻译、外观与显示在。时间字段在下面的时间栏。
 */
export function LineDetail(props: {
  session: EditorSession;
  state: EditorState;
  audio: AudioController | null;
  line: EditorLine;
  lineNumber: number;
  tokenId?: string | undefined;
  segmentId?: string | undefined;
  actions: LineActions;
  appearance: AppearanceStore | null;
  orchestration: OrchestrationStore | null;
  onOpen: (page: 'appearance' | 'orchestration') => void;
}) {
  const { session, state, audio, line, actions } = props;
  const timed = isTimed(line);
  const target = { branchId: line.branchId, lineId: line.id };
  const isPrimary = line.branchId === state.primaryBranchId || !state.primaryBranchId;

  const setText = async (text: string) => {
    const response = await session.command({ type: 'lyrics.setLineText', target, text });
    const feedback = response?.changed ? response.feedback : undefined;
    if (feedback) toast(`正文已更新：保留 ${feedback.preservedTokenCount ?? 0} 个文本单元的时间，重新拆分 ${feedback.resetTokenCount ?? 0} 个`);
  };

  return (
    <div className="edit">
      <header className="lhead">
        <h2>第 {props.lineNumber} 行</h2>
        <span className="tnum">{timed ? `${formatTime(line.startUs)} – ${formatTime(line.endUs)}` : '未打轴'}</span>
        {line.status && <StatusTag status={line.status} />}
        <span className="spacer" />
        <Button variant="ghost" size="sm" icon="my_location" isDisabled={!timed || !audio} onPress={() => timed && audio?.seek(line.startUs)}>
          定位
        </Button>
        <Button variant="ghost" size="sm" icon="headphones" isDisabled={!timed || !audio} onPress={() => timed && audio?.playRange(line.startUs, line.endUs)}>
          试听
        </Button>
        <MenuTrigger>
          <IconButton icon="more_horiz" label="行操作" size="sm" />
          <Menu label="行操作" placement="bottom end" onAction={(id) => actions.run(id as LineAction, line)}>
            {lineActionItems(session, isPrimary)}
          </Menu>
        </MenuTrigger>
      </header>

      <section className="fs">
        <header>
          <h4>正文</h4>
          <span className="faint small">Enter 或离开输入框时提交，Esc 撤回</span>
        </header>
        <CommitField
          key={line.id}
          label="正文"
          labelPlacement="none"
          value={line.text}
          placeholder="（空行）"
          disabled={!session.supportsCommand('lyrics.setLineText')}
          onCommit={(text) => void setText(text)}
          className="line-text"
        />
      </section>

      <TokenSection session={session} line={line} tokenId={props.tokenId} actions={actions} />

      <div className="pair">
        <RubySection session={session} line={line} tokenId={props.tokenId} segmentId={props.segmentId} />
        <TranslationSection session={session} state={state} line={line} actions={actions} />
      </div>

      <LineLinks appearance={props.appearance} orchestration={props.orchestration} state={state} line={line} onOpen={props.onOpen} />
    </div>
  );
}
