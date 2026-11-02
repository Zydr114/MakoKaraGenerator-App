import { useCallback, useEffect, useState, type ReactNode } from 'react';
import type { FileAccess, ProjectDirectoryEntry, ProjectSessionSummary } from '../../protocol';
import { client } from '../connection';
import { formatTime } from '../model/time';
import { EditorSession } from '../session/editor-session';
import { openProject, recentProjects, rememberProject } from '../session/project-files';
import { logoIcon } from '../theme/logo/logo';
import { Button, IconButton } from '../ui/Button';
import { cls } from '../ui/cls';
import { TextField } from '../ui/fields';
import { Icon } from '../ui/Icon';
import { ProgressBar, Tag } from '../ui/indicators';
import { Menu, MenuItem, MenuTrigger } from '../ui/Menu';
import { DirectoryBrowser, useDirectoryListing } from './DirectoryBrowser';
import { NewProjectDialog } from './NewProjectDialog';
import { RecoveryDialog } from './RecoveryDialog';
import './home.css';

type Selection = { kind: 'session'; id: string } | { kind: 'project'; entry: ProjectDirectoryEntry };

const sessionState = (s: ProjectSessionSummary) => (s.isReadOnly ? '只读' : s.isDirty ? '未保存' : '已保存');

/**
 * 首页（整体设计 §5.1，W5 设计 §2.6，界面改版 §2.7）：左边找工程（正在编辑的会话、最近打开、工程目录），
 * 右边幕后区显示选中项的信息。单击选中，双击或按钮打开；普通目录单击进入。
 * 右边只写已有的数据：会话摘要里的修订、保存状态、歌词行数和音频，工程目录里的工程只有名称和位置（用户决定 6）。
 * 打开失败的原因留在页面上，直到下一次操作或手动关闭。
 */
export function Home(props: {
  ready: boolean;
  current: EditorSession | null;
  /** 页面顶部的说明（例如连接重置后请重新打开工程）。 */
  notice?: string | null;
  onEnter: (session: EditorSession) => void;
  onResume: () => void;
}) {
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [openSessions, setOpenSessions] = useState<ProjectSessionSummary[]>([]);
  const [selection, setSelection] = useState<Selection | null>(null);
  const [dialog, setDialog] = useState<'new' | 'recover' | null>(null);

  const [path, setPath] = useState('');
  const [query, setQuery] = useState('');
  const listing = useDirectoryListing(props.ready, path);
  const [recent, setRecent] = useState(recentProjects);

  useEffect(() => {
    if (!props.ready) return;
    client
      .clientState()
      .then((state) => setOpenSessions(state.sessions))
      .catch(() => setOpenSessions([]));
  }, [props.ready]);

  const run = useCallback(
    async (task: (progress: (step: string) => void) => Promise<EditorSession>) => {
      setError(null);
      setBusy('请稍候');
      try {
        props.onEnter(await task(setBusy));
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e));
      } finally {
        setBusy(null);
      }
    },
    [props],
  );

  const enterFromDialog = (session: EditorSession) => {
    setDialog(null);
    props.onEnter(session);
  };

  const disabled = !props.ready || busy !== null;

  const open = (relativePath: string, access: FileAccess = 'readWrite') =>
    run(async (progress) => {
      const summary = await openProject(client, relativePath, progress, access);
      rememberProject(relativePath);
      setRecent(recentProjects());
      progress('正在读取歌词');
      return EditorSession.attach(client, summary.projectSessionId);
    });

  const attach = (id: string) => (id === props.current?.projectSessionId ? props.onResume() : run(() => EditorSession.attach(client, id)));

  const navigate = (next: string) => {
    setPath(next);
    setQuery('');
  };

  // 没选时默认看正在编辑的工程，其次是第一个还开着的会话。
  const fallback = props.current?.projectSessionId ?? openSessions[0]?.projectSessionId;
  const shown: Selection | null = selection ?? (fallback ? { kind: 'session', id: fallback } : null);
  const shownSession = shown?.kind === 'session' ? openSessions.find((s) => s.projectSessionId === shown.id) : undefined;
  const selectedName = shown?.kind === 'project' ? shown.entry.name : undefined;

  const projectActions = (entry: ProjectDirectoryEntry) => (
    <span className="directory-actions">
      <Button variant="ghost" size="sm" isDisabled={disabled} onPress={() => void open(entry.relativePath)}>
        打开
      </Button>
      <MenuTrigger>
        <IconButton icon="more_vert" size="sm" label={`${entry.name} 的更多操作`} isDisabled={disabled} />
        <Menu label={`${entry.name} 的更多操作`} placement="bottom end" onAction={() => void open(entry.relativePath, 'read')}>
          <MenuItem id="read" icon="lock">
            只读打开
          </MenuItem>
        </Menu>
      </MenuTrigger>
    </span>
  );

  return (
    <div className="home">
      <main className="home-main" aria-label="工程">
        <header className="home-head">
          <h2>工程</h2>
          <span className="grow" />
          <TextField
            label="搜索这一层"
            labelPlacement="none"
            size="sm"
            icon="search"
            placeholder="搜索这一层"
            value={query}
            className="home-search"
            disabled={!props.ready}
            onChange={setQuery}
            onEscape={() => setQuery('')}
          />
          <Button variant="ghost" icon="settings_backup_restore" isDisabled={disabled} onPress={() => setDialog('recover')}>
            从备份恢复…
          </Button>
          <Button variant="filled" icon="add" isDisabled={disabled} onPress={() => setDialog('new')}>
            新建工程
          </Button>
        </header>
        {busy && (
          <div className="home-busy">
            <ProgressBar label={busy} />
            <span>{busy}</span>
          </div>
        )}
        {(props.notice || error) && (
          <div className="home-banners">
            {props.notice && (
              <div className="banner" role="status">
                <Icon name="info" />
                <span>{props.notice}</span>
              </div>
            )}
            {error && (
              <div className="banner error home-error" role="alert">
                <Icon name="error" />
                <span>{error}</span>
                <IconButton icon="close" size="sm" label="关闭提示" onPress={() => setError(null)} />
              </div>
            )}
          </div>
        )}

        <div className="home-body">
          {openSessions.length > 0 && (
            <section aria-label="正在编辑">
              <h3 className="grouphead">
                正在编辑<span>后端里还开着的会话</span>
              </h3>
              <div className="sessions" role="list">
                {openSessions.map((s) => {
                  const editing = s.projectSessionId === props.current?.projectSessionId;
                  const selected = shown?.kind === 'session' && shown.id === s.projectSessionId;
                  return (
                    <div key={s.projectSessionId} role="listitem" className={cls('session-row', selected && 'is-sel')} data-session-id={s.projectSessionId}>
                      <button
                        type="button"
                        className="session-main"
                        disabled={!props.ready}
                        {...(selected ? { 'aria-current': true } : {})}
                        onClick={() => setSelection({ kind: 'session', id: s.projectSessionId })}
                        onDoubleClick={() => !disabled && void attach(s.projectSessionId)}
                      >
                        <span className="session-icon">
                          <Icon name={editing ? 'edit' : 'music_note'} />
                        </span>
                        <span className="session-text">
                          <b className="session-name">{s.name}</b>
                          <span className="session-meta">
                            修订 {s.projectRevision}，{sessionState(s)}
                            {!s.hasPath && '，还没有保存位置'}
                          </span>
                        </span>
                      </button>
                      <Button variant={editing ? 'tonal' : 'outlined'} size="sm" isDisabled={disabled} onPress={() => void attach(s.projectSessionId)}>
                        {editing ? '继续编辑' : '接回'}
                      </Button>
                    </div>
                  );
                })}
              </div>
            </section>
          )}

          {recent.length > 0 && (
            <section aria-label="最近打开">
              <h3 className="grouphead">
                最近打开<span>点击直接打开</span>
              </h3>
              <div className="recent-projects">
                {recent.map((p) => (
                  <Button key={p} variant="tonal" size="sm" icon="history" isDisabled={disabled} onPress={() => void open(p)}>
                    {p}
                  </Button>
                ))}
              </div>
            </section>
          )}

          <section aria-label="工程目录" className="home-directory">
            <h3 className="grouphead">
              工程目录<span>单击看详情，双击打开</span>
            </h3>
            <DirectoryBrowser
              path={path}
              listing={listing}
              query={query}
              onNavigate={navigate}
              disabled={disabled}
              highlight={selectedName}
              onProject={(entry) => setSelection({ kind: 'project', entry })}
              onProjectDoubleClick={(entry) => !disabled && void open(entry.relativePath)}
              projectActions={projectActions}
            />
          </section>
        </div>
      </main>

      <aside className="home-detail stage-zone" aria-label="详情">
        {shownSession ? (
          <Detail
            title={shownSession.name}
            tag={
              shownSession.projectSessionId === props.current?.projectSessionId ? (
                <Tag tone="accent">正在编辑</Tag>
              ) : (
                <Tag>{shownSession.isReadOnly ? '只读会话' : '还开着'}</Tag>
              )
            }
            facts={[
              ['会话', `修订 ${shownSession.projectRevision}，${sessionState(shownSession)}`],
              ['保存位置', shownSession.hasPath ? '已有' : '还没有，第一次保存时选择'],
              ['歌词', shownSession.lyricsLineCount > 0 ? `${shownSession.lyricsLineCount} 行` : '没有歌词'],
              ['音频', shownSession.audioAssetId ? (shownSession.audioDurationUs ? formatTime(shownSession.audioDurationUs) : '有，时长未知') : '没有音频'],
              ['音频分析', shownSession.audioAnalysis?.status === 'available' ? '已采用' : '未分析'],
            ]}
            actions={
              <Button variant="filled" size="lg" className="grow" isDisabled={disabled} onPress={() => void attach(shownSession.projectSessionId)}>
                {shownSession.projectSessionId === props.current?.projectSessionId ? '继续编辑' : '接回'}
              </Button>
            }
          />
        ) : shown?.kind === 'project' ? (
          <Detail
            title={shown.entry.name}
            tag={<Tag>工程目录</Tag>}
            facts={[['位置', shown.entry.relativePath]]}
            note="打开后可以看到画面、歌词和音频。"
            actions={
              <>
                <Button variant="filled" size="lg" className="grow" isDisabled={disabled} onPress={() => void open(shown.entry.relativePath)}>
                  打开
                </Button>
                <Button size="lg" icon="lock" isDisabled={disabled} onPress={() => void open(shown.entry.relativePath, 'read')}>
                  只读打开
                </Button>
              </>
            }
          />
        ) : (
          <Detail title="还没有选中工程" note="在左边单击一个工程看它的信息，双击直接打开；也可以新建工程或从备份恢复。" />
        )}
      </aside>

      <NewProjectDialog open={dialog === 'new'} onClose={() => setDialog(null)} onCreated={enterFromDialog} />
      <RecoveryDialog open={dialog === 'recover'} onClose={() => setDialog(null)} onRecovered={enterFromDialog} />
    </div>
  );
}

/** 右边的详情：画面位置放标志占位（没有不打开工程就能取的画面），下面是标题、信息和操作。 */
function Detail(props: { title: string; tag?: ReactNode; facts?: readonly (readonly [string, string])[]; note?: string; actions?: ReactNode }) {
  return (
    <div className="detail">
      <div className="detail-frame" aria-hidden>
        <img src={logoIcon} alt="" />
      </div>
      <div className="detail-title">
        <h2>{props.title}</h2>
        {props.tag}
      </div>
      {props.facts && (
        <dl className="dl detail-facts">
          {props.facts.map(([k, v]) => (
            <div key={k}>
              <dt>{k}</dt>
              <dd>{v}</dd>
            </div>
          ))}
        </dl>
      )}
      {props.note && <p className="detail-note">{props.note}</p>}
      {props.actions && <div className="detail-actions">{props.actions}</div>}
    </div>
  );
}
