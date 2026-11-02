import { useState } from 'react';
import { Button as AriaButton, type Key } from 'react-aria-components';
import type { ConnectionState } from '../../client';
import { LYRICS_EXPORT_FEATURE, LyricsExportDialog } from '../export/LyricsExportDialog';
import type { RenderDeviceStore } from '../preview/render-device';
import type { EditorSession } from '../session/editor-session';
import { logoIcon } from '../theme/logo/logo';
import { Button, IconButton } from '../ui/Button';
import { cls } from '../ui/cls';
import { useObservable } from '../ui/hooks';
import { Icon, type IconName } from '../ui/Icon';
import { Menu, MenuItem, MenuSeparator, MenuTrigger } from '../ui/Menu';
import { Tooltip } from '../ui/Tooltip';
import { StatusChip } from './StatusChip';
import './shell.css';

export type Page = 'home' | 'lyrics' | 'orchestration' | 'appearance' | 'library' | 'export';
export type EditPage = 'lyrics' | 'orchestration' | 'appearance';

const EDIT_PAGES: readonly { value: EditPage; label: string; icon: IconName }[] = [
  { value: 'lyrics', label: '歌词', icon: 'lyrics' },
  { value: 'orchestration', label: '编排', icon: 'dashboard' },
  { value: 'appearance', label: '外观', icon: 'palette' },
];

export interface AppBarProps {
  page: Page;
  session: EditorSession | null;
  connection: ConnectionState;
  renderDevice: RenderDeviceStore;
  /** 切换页面；离开样式库时可能要先确认，所以不保证切过去。 */
  onPage: (page: Page) => void;
  /** 导出页的“返回编辑”。 */
  onBack: () => void;
  /** 保存工程；saveAs 时总是打开“保存到…”。返回是否保存成功。 */
  onSave: (saveAs?: boolean) => Promise<boolean>;
  onClose: () => Promise<void>;
  onProjectSettings: () => void;
  onAppSettings: () => void;
  onAbout: () => void;
}

/**
 * 顶栏（界面改版 §2.5）：取代原来的顶栏加导航栏。
 * 有工程时：标志（回首页）、工程名菜单、保存状态、页面（歌词、编排、外观）、样式库；右侧状态胶囊、撤销重做、工程设置、
 * 应用设置、保存、导出视频。没有工程时：标志加应用名、样式库、状态胶囊、应用设置、关于。导出页：返回编辑加标题。
 */
export function AppBar(props: AppBarProps) {
  const { session } = props;
  if (!session) {
    return (
      <header className="app-bar">
        <Brand onHome={() => props.onPage('home')} />
        <b className="app-name">MakoKara Generator</b>
        <span className="spacer" />
        <LibraryButton page={props.page} onPage={props.onPage} />
        <span className="vsep" />
        <StatusChip store={props.renderDevice} connection={props.connection} />
        <IconButton icon="settings" label="应用设置" onPress={props.onAppSettings} />
        <IconButton icon="info" label="关于" onPress={props.onAbout} />
      </header>
    );
  }
  return props.page === 'export' ? <ExportBar {...props} session={session} /> : <ProjectBar {...props} session={session} />;
}

function Brand(props: { onHome: () => void }) {
  return (
    <Tooltip label="首页">
      <AriaButton className="brand" aria-label="首页" onPress={props.onHome}>
        <img src={logoIcon} alt="" />
      </AriaButton>
    </Tooltip>
  );
}

function LibraryButton(props: { page: Page; onPage: (page: Page) => void }) {
  const on = props.page === 'library';
  return (
    <Button variant="ghost" icon="style" className={cls(on && 'on')} aria-current={on ? 'page' : false} onPress={() => props.onPage('library')}>
      样式库
    </Button>
  );
}

/** 保存、关闭进行中时停用会改工程的按钮。 */
function useBusy() {
  const [busy, setBusy] = useState(false);
  const run = async (task: () => Promise<unknown>) => {
    setBusy(true);
    try {
      await task();
    } finally {
      setBusy(false);
    }
  };
  return [busy, run] as const;
}

function ProjectBar(props: AppBarProps & { session: EditorSession }) {
  const { session } = props;
  const summary = useObservable(session.state$).summary;
  const [busy, run] = useBusy();
  const [lyricsExportOpen, setLyricsExportOpen] = useState(false);

  const onMenu = (id: Key) => {
    if (id === 'saveAs') void run(() => props.onSave(true));
    else if (id === 'exportLyrics') setLyricsExportOpen(true);
    else if (id === 'close') void run(props.onClose);
    else if (id === 'console') window.open('/console.html', '_blank', 'noopener');
  };

  return (
    <header className="app-bar">
      <Brand onHome={() => props.onPage('home')} />
      <MenuTrigger>
        <AriaButton className="pname" aria-label={`工程菜单：${summary.name}`}>
          <span className="project-name">{summary.name}</span>
          <Icon name="keyboard_arrow_down" />
        </AriaButton>
        <Menu label="工程" onAction={onMenu}>
          <MenuItem id="saveAs" icon="save" disabled={busy || summary.isReadOnly}>
            另存为…
          </MenuItem>
          <MenuItem id="exportLyrics" icon="description" disabled={busy || !session.supports(LYRICS_EXPORT_FEATURE)}>
            导出歌词文件…
          </MenuItem>
          <MenuItem id="close" icon="close" disabled={busy}>
            关闭工程
          </MenuItem>
          <MenuSeparator />
          <MenuItem id="console" icon="open_in_new">
            协议控制台
          </MenuItem>
        </Menu>
      </MenuTrigger>
      <SaveState readOnly={summary.isReadOnly} dirty={summary.isDirty} />
      <nav className="pages" aria-label="页面">
        {EDIT_PAGES.map((p) => {
          const on = props.page === p.value;
          return (
            <AriaButton key={p.value} className={cls('page-tab', on && 'on')} aria-current={on ? 'page' : false} onPress={() => props.onPage(p.value)}>
              <Icon name={p.icon} filled={on} />
              <span className="page-label">{p.label}</span>
            </AriaButton>
          );
        })}
      </nav>
      <span className="vsep" />
      <LibraryButton page={props.page} onPage={props.onPage} />
      <span className="spacer" />
      <StatusChip store={props.renderDevice} connection={props.connection} />
      <IconButton icon="undo" label="撤销" isDisabled={!summary.canUndo || busy} onPress={() => void session.undo()} />
      <IconButton icon="redo" label="重做" isDisabled={!summary.canRedo || busy} onPress={() => void session.redo()} />
      <IconButton icon="tune" label="工程设置" onPress={props.onProjectSettings} />
      <IconButton icon="settings" label="应用设置" onPress={props.onAppSettings} />
      <span className="vsep" />
      <Button aria-label="保存工程" isDisabled={busy || summary.isReadOnly} onPress={() => void run(() => props.onSave())}>
        保存
      </Button>
      <Button variant="filled" icon="movie" onPress={() => props.onPage('export')}>
        导出视频
      </Button>
      <LyricsExportDialog session={session} open={lyricsExportOpen} onClose={() => setLyricsExportOpen(false)} />
    </header>
  );
}

/** 导出页的顶栏：返回编辑、标题与工程名，右侧状态胶囊和保存。 */
function ExportBar(props: AppBarProps & { session: EditorSession }) {
  const { session } = props;
  const summary = useObservable(session.state$).summary;
  const [busy, run] = useBusy();
  return (
    <header className="app-bar">
      <Button variant="ghost" icon="arrow_back" onPress={props.onBack}>
        返回编辑
      </Button>
      <span className="vsep" />
      <h1 className="bar-title">导出视频</h1>
      <span className="project-name muted">{summary.name}</span>
      <SaveState readOnly={summary.isReadOnly} dirty={summary.isDirty} />
      <span className="spacer" />
      <StatusChip store={props.renderDevice} connection={props.connection} />
      <Button aria-label="保存工程" isDisabled={busy || summary.isReadOnly} onPress={() => void run(() => props.onSave())}>
        保存
      </Button>
    </header>
  );
}

function SaveState(props: { readOnly: boolean; dirty: boolean }) {
  const state = props.readOnly ? 'readOnly' : props.dirty ? 'dirty' : 'saved';
  return (
    <span className="project-state" data-state={state}>
      {props.readOnly ? '只读' : props.dirty ? '未保存' : '已保存'}
    </span>
  );
}
