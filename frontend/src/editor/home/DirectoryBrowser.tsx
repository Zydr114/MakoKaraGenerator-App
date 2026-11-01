import { useCallback, useEffect, useState, type ReactNode } from 'react';
import type { ProjectDirectoryEntry } from '../../protocol';
import { client } from '../connection';
import { filterEntries, pathSegments } from '../model/project-directory';
import { directoryErrorText, projectRoot } from '../session/project-files';
import { TextField } from '../ui/legacy/fields';

export interface DirectoryListing {
  entries: ProjectDirectoryEntry[];
  loading: boolean;
  error?: string;
  reload: () => void;
}

/**
 * 读授权根下某一层的全部条目（W5 设计 §2.6）。路径变化时取消上一次读取；翻页途中目录变了由客户端从第一页重读。
 */
export function useDirectoryListing(ready: boolean, path: string): DirectoryListing {
  const [entries, setEntries] = useState<ProjectDirectoryEntry[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const [generation, setGeneration] = useState(0);

  useEffect(() => {
    if (!ready) return;
    const abort = new AbortController();
    setLoading(true);
    setError(undefined);
    void (async () => {
      try {
        const all = await client.files.listAll(await projectRoot(client), path, abort.signal);
        if (!abort.signal.aborted) setEntries(all);
      } catch (e) {
        if (abort.signal.aborted) return;
        setEntries([]);
        setError(directoryErrorText(e));
      } finally {
        if (!abort.signal.aborted) setLoading(false);
      }
    })();
    return () => abort.abort();
  }, [ready, path, generation]);

  const reload = useCallback(() => setGeneration((g) => g + 1), []);
  return { entries, loading, reload, ...(error ? { error } : {}) };
}

/**
 * 工程目录浏览器：路径导航、当前层搜索、刷新，工程行与普通目录行。
 * 普通目录点进去；工程行的动作由调用方给（首页是打开／只读打开，保存对话框是选为覆盖目标）。
 */
export function DirectoryBrowser(props: {
  path: string;
  listing: DirectoryListing;
  onNavigate: (path: string) => void;
  disabled?: boolean;
  /** 工程行右侧的动作。 */
  projectActions?: (entry: ProjectDirectoryEntry) => ReactNode;
  /** 点工程行本身。 */
  onProject?: (entry: ProjectDirectoryEntry) => void;
  /** 当前选中的条目名（保存对话框里与输入的目录名同名的那一项）。 */
  highlight?: string;
  className?: string;
}) {
  const { listing } = props;
  const [query, setQuery] = useState('');
  // 换了一层就清空搜索：搜索只针对当前这一层。
  useEffect(() => setQuery(''), [props.path]);
  const shown = filterEntries(listing.entries, query);
  const segments = pathSegments(props.path);
  const disabled = !!props.disabled;

  return (
    <div className={`directory-browser${props.className ? ` ${props.className}` : ''}`}>
      <div className="directory-toolbar">
        <nav className="breadcrumb" aria-label="当前目录">
          <mdui-button variant="text" disabled={disabled || segments.length === 0} onClick={() => props.onNavigate('')}>
            工程目录
          </mdui-button>
          {segments.map((segment, i) => (
            <span key={segment.path} className="breadcrumb-part">
              <span className="breadcrumb-sep">/</span>
              <mdui-button variant="text" disabled={disabled || i === segments.length - 1} onClick={() => props.onNavigate(segment.path)}>
                {segment.name}
              </mdui-button>
            </span>
          ))}
        </nav>
        <TextField label="搜索这一层" value={query} variant="outlined" className="directory-search" disabled={disabled} onChange={setQuery} onEscape={() => setQuery('')} />
        <mdui-button-icon disabled={disabled || listing.loading} onClick={listing.reload} aria-label="刷新">
          <mdui-icon-refresh />
        </mdui-button-icon>
      </div>
      {listing.loading && <mdui-linear-progress className="directory-progress" />}
      {listing.error && (
        <div className="error-banner" role="alert">
          {listing.error}
          {segments.length > 0 && (
            <mdui-button variant="text" onClick={() => props.onNavigate('')}>
              回到工程目录
            </mdui-button>
          )}
        </div>
      )}
      <div className="directory-list" role="list">
        {segments.length > 0 && (
          <div className="directory-row directory-up" role="listitem">
            <button type="button" className="directory-main" disabled={disabled} onClick={() => props.onNavigate(segments.at(-2)?.path ?? '')}>
              <mdui-icon-arrow-upward />
              <span className="directory-name">上一层</span>
            </button>
          </div>
        )}
        {shown.map((entry) => (
          <div
            key={entry.relativePath}
            role="listitem"
            className={`directory-row${entry.isProject ? ' is-project' : ''}${props.highlight === entry.name ? ' is-highlight' : ''}`}
            data-path={entry.relativePath}
          >
            <button
              type="button"
              className="directory-main"
              disabled={disabled || (entry.isProject && !props.onProject)}
              onClick={() => (entry.isProject ? props.onProject?.(entry) : props.onNavigate(entry.relativePath))}
            >
              {entry.isProject ? <mdui-icon-music-note /> : <mdui-icon-folder />}
              <span className="directory-name">{entry.name}</span>
              <span className="directory-kind">{entry.isProject ? '工程' : '目录'}</span>
            </button>
            {entry.isProject && props.projectActions?.(entry)}
          </div>
        ))}
        {!listing.loading && !listing.error && shown.length === 0 && (
          <p className="directory-empty muted">{listing.entries.length === 0 ? '这个目录是空的。' : `这一层没有名称包含“${query.trim()}”的项。`}</p>
        )}
      </div>
    </div>
  );
}
