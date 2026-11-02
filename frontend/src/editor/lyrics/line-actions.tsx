import { useMemo, useState, type ReactNode } from 'react';
import type { ProjectCommand } from '../../protocol';
import type { EditorLine } from '../model/lyrics';
import type { EditorSession } from '../session/editor-session';
import { toast } from '../ui/feedback';
import type { IconName } from '../ui/Icon';
import { MenuItem } from '../ui/Menu';
import { LinkDialog } from './LinkDialog';
import { ResplitLineDialog } from './ResplitDialogs';

export type LineAction = 'insertAfter' | 'duplicate' | 'remove' | 'resplit' | 'link';

const ACTIONS: ReadonlyArray<{ action: LineAction; label: string; icon: IconName; command: ProjectCommand['type']; danger?: boolean }> = [
  { action: 'insertAfter', label: '在下方插入', icon: 'playlist_add', command: 'lyrics.addLine' },
  { action: 'duplicate', label: '复制本行', icon: 'content_copy', command: 'lyrics.duplicateLine' },
  { action: 'resplit', label: '按规则重切本行…', icon: 'wand_stars', command: 'lyrics.resplitLine' },
  { action: 'link', label: '关联到主歌词某行…', icon: 'link', command: 'lyrics.linkPrimaryLine' },
  { action: 'remove', label: '删除本行', icon: 'delete', command: 'lyrics.removeLine', danger: true },
];

/** 单行的操作与它们的对话框（重切本行、对照编辑）；行编辑区标题栏的“行操作”、行列表的右键菜单、文本单元的“重切”共用。 */
export interface LineActions {
  run: (action: LineAction, line: EditorLine) => void;
  /** 打开对照编辑：一个非主来源的行与主歌词的关联；给出 focusLineId 时滚到这一行。 */
  openLink: (branchId: string, focusLineId?: string) => void;
}

export function useLineActions(session: EditorSession, onUndo: () => void): { actions: LineActions; dialogs: ReactNode } {
  const [resplitLineId, setResplitLineId] = useState<string | null>(null);
  const [link, setLink] = useState<{ branchId: string; focusLineId?: string } | null>(null);

  const actions = useMemo<LineActions>(
    () => ({
      openLink: (branchId, focusLineId) => setLink({ branchId, ...(focusLineId ? { focusLineId } : {}) }),
      run: (action, line) => {
        const number = session.state.lines.findIndex((l) => l.id === line.id) + 1;
        const target = { branchId: line.branchId, lineId: line.id };
        if (action === 'resplit') setResplitLineId(line.id);
        else if (action === 'link') setLink({ branchId: line.branchId, focusLineId: line.id });
        else if (action === 'insertAfter')
          void session.command({ type: 'lyrics.addLine', branchId: line.branchId, afterLineId: line.id }).then((r) => r?.changed && toast(`已在第 ${number} 行下方插入空行`));
        else if (action === 'duplicate')
          void session.command({ type: 'lyrics.duplicateLine', target }).then((r) => {
            if (!r?.changed) return;
            if (r.feedback?.overlapsNext) session.notify({ level: 'warning', message: `第 ${number} 行的副本与下一行时间重叠，请调整时间。` });
            toast(`已复制第 ${number} 行`);
          });
        else void session.command({ type: 'lyrics.removeLine', target }).then((r) => r?.changed && toast(`已删除第 ${number} 行`, { label: '撤销', run: onUndo }));
      },
    }),
    [session, onUndo],
  );

  const dialogs = (
    <>
      <ResplitLineDialog session={session} line={session.state.lines.find((l) => l.id === resplitLineId)} open={resplitLineId !== null} onClose={() => setResplitLineId(null)} />
      <LinkDialog session={session} branchId={link?.branchId ?? null} focusLineId={link?.focusLineId} onClose={() => setLink(null)} />
    </>
  );
  return { actions, dialogs };
}

/** 行操作的菜单项；“关联到主歌词某行…”只在非主来源出现。 */
export function lineActionItems(session: EditorSession, isPrimary: boolean): ReactNode[] {
  return ACTIONS.filter((a) => a.action !== 'link' || !isPrimary).map((a) => (
    <MenuItem key={a.action} id={a.action} icon={a.icon} danger={!!a.danger} disabled={!session.supportsCommand(a.command)}>
      {a.label}
    </MenuItem>
  ));
}
