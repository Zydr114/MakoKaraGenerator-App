import { useCallback, useEffect, useState } from 'react';
import type { FileAccess, ProjectDirectoryEntry, ProjectSessionSummary } from '../../protocol';
import { client } from '../connection';
import { EditorSession } from '../session/editor-session';
import { openProject, recentProjects, rememberProject } from '../session/project-files';
import { DirectoryBrowser, useDirectoryListing } from './DirectoryBrowser';
import { NewProjectDialog } from './NewProjectDialog';
import { RecoveryDialog } from './RecoveryDialog';

/**
 * 首页（整体设计 §5.1，W5 设计 §2.6）：继续工作（已打开的工程）、新建、最近打开、工程目录浏览。
 * 打开失败的原因留在页面上，直到下一次操作或手动关闭。
 */
export function Home(props: { ready: boolean; current: EditorSession | null; onEnter: (session: EditorSession) => void; onResume: () => void }) {
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [openSessions, setOpenSessions] = useState<ProjectSessionSummary[]>([]);

  const [dialog, setDialog] = useState<'new' | 'recover' | null>(null);

  const [path, setPath] = useState('');
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

  const projectActions = (entry: ProjectDirectoryEntry) => (
    <span className="directory-actions">
      <mdui-button variant="text" disabled={disabled} onClick={() => open(entry.relativePath)}>
        打开
      </mdui-button>
      <mdui-dropdown placement="bottom-end">
        <mdui-button-icon slot="trigger" disabled={disabled} aria-label={`${entry.name} 的更多操作`}>
          <mdui-icon-more-vert />
        </mdui-button-icon>
        <mdui-menu>
          <mdui-menu-item onClick={() => open(entry.relativePath, 'read')}>只读打开</mdui-menu-item>
        </mdui-menu>
      </mdui-dropdown>
    </span>
  );

  const attach = (id: string) => (id === props.current?.projectSessionId ? props.onResume() : run(() => EditorSession.attach(client, id)));

  return (
    <div className="home">
      {busy && <mdui-linear-progress className="home-progress" />}
      {busy && <p className="home-busy">{busy}</p>}
      {error && (
        <div className="error-banner home-error" role="alert">
          <span>{error}</span>
          <mdui-button-icon onClick={() => setError(null)} aria-label="关闭提示">
            <mdui-icon-close />
          </mdui-button-icon>
        </div>
      )}

      {openSessions.length > 0 && (
        <section className="home-section">
          <h2>继续工作</h2>
          <div className="session-cards">
            {openSessions.map((s) => {
              const editing = s.projectSessionId === props.current?.projectSessionId;
              return (
                <mdui-card
                  key={s.projectSessionId}
                  variant={editing ? 'filled' : 'outlined'}
                  clickable
                  disabled={disabled}
                  className="session-card"
                  data-session-id={s.projectSessionId}
                  onClick={() => attach(s.projectSessionId)}
                >
                  <span className="session-card-name">{s.name}</span>
                  <span className="session-card-meta">
                    {editing && <span className="session-card-badge">正在编辑</span>}
                    修订 {s.projectRevision} · {s.isReadOnly ? '只读' : s.isDirty ? '未保存' : '已保存'}
                    {!s.hasPath && ' · 还没有保存位置'}
                  </span>
                </mdui-card>
              );
            })}
          </div>
        </section>
      )}

      <div className="home-actions">
        <mdui-button disabled={disabled} onClick={() => setDialog('new')}>
          <mdui-icon-add slot="icon" />
          新建工程
        </mdui-button>
        <mdui-button variant="tonal" disabled={disabled} onClick={() => setDialog('recover')}>
          <mdui-icon-restore slot="icon" />
          从备份恢复…
        </mdui-button>
      </div>

      <section className="home-section">
        <h2>工程目录</h2>
        {recent.length > 0 && (
          <div className="chips recent-projects">
            <span className="muted">最近打开</span>
            {recent.map((p) => (
              <mdui-chip key={p} disabled={disabled} onClick={() => open(p)}>
                {p}
              </mdui-chip>
            ))}
          </div>
        )}
        <DirectoryBrowser
          path={path}
          listing={listing}
          onNavigate={setPath}
          disabled={disabled}
          onProject={(entry) => open(entry.relativePath)}
          projectActions={projectActions}
        />
      </section>
      <NewProjectDialog open={dialog === 'new'} onClose={() => setDialog(null)} onCreated={enterFromDialog} />
      <RecoveryDialog open={dialog === 'recover'} onClose={() => setDialog(null)} onRecovered={enterFromDialog} />
    </div>
  );
}
