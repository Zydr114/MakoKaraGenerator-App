import { useCallback, useEffect, useState, type ReactNode } from 'react';
import type { ProjectDirectoryEntry } from '../../protocol';
import { client } from '../connection';
import { filterEntries, pathSegments } from '../model/project-directory';
import { directoryErrorText, projectRoot } from '../session/project-files';
import { Button, IconButton } from '../ui/Button';
import { cls } from '../ui/cls';
import { TextField } from '../ui/fields';
import { Icon } from '../ui/Icon';
import { ProgressBar } from '../ui/indicators';
import './home.css';

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
 * 普通目录点进去；工程行的动作由调用方给（首页是选中看详情、双击或“打开”打开，保存对话框是选为覆盖目标）。
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
  /** 双击工程行。 */
  onProjectDoubleClick?: (entry: ProjectDirectoryEntry) => void;
  /** 选中的条目名（首页右边显示详情的那一项；保存对话框里与输入的目录名同名的那一项）。 */
  highlight?: string | undefined;
  /** 搜索框放在外面时（首页的标题行）由外面给出关键词；不给时浏览器自带一个搜索框。 */
  query?: string;
  className?: string;
}) {
  const { listing } = props;
  const [ownQuery, setOwnQuery] = useState('');
  // 换了一层就清空搜索：搜索只针对当前这一层。
  useEffect(() => setOwnQuery(''), [props.path]);
  const query = props.query ?? ownQuery;
  const shown = filterEntries(listing.entries, query);
  const segments = pathSegments(props.path);
  const disabled = !!props.disabled;

  return (
    <div className={cls('directory-browser', props.className)}>
      <div className="directory-toolbar">
        <nav className="breadcrumb" aria-label="当前目录">
          <Button variant="text" size="sm" icon="folder" isDisabled={disabled || segments.length === 0} onPress={() => props.onNavigate('')}>
            工程目录
          </Button>
          {segments.map((segment, i) => (
            <span key={segment.path} className="breadcrumb-part">
              <Icon name="chevron_right" className="breadcrumb-sep" />
              <Button variant="text" size="sm" isDisabled={disabled || i === segments.length - 1} onPress={() => props.onNavigate(segment.path)}>
                {segment.name}
              </Button>
            </span>
          ))}
        </nav>
        {!listing.loading && !listing.error && <span className="directory-count">{shown.length} 项</span>}
        {props.query === undefined && (
          <TextField
            label="搜索这一层"
            labelPlacement="none"
            size="sm"
            icon="search"
            placeholder="搜索这一层"
            value={ownQuery}
            className="directory-search"
            disabled={disabled}
            onChange={setOwnQuery}
            onEscape={() => setOwnQuery('')}
          />
        )}
        <IconButton icon="refresh" size="sm" label="刷新" isDisabled={disabled || listing.loading} onPress={listing.reload} />
      </div>
      {listing.loading && <ProgressBar label="正在读取目录" className="directory-progress" />}
      {listing.error && (
        <div className="banner error" role="alert">
          <Icon name="error" />
          <span>{listing.error}</span>
          {segments.length > 0 && (
            <Button variant="text" size="sm" onPress={() => props.onNavigate('')}>
              回到工程目录
            </Button>
          )}
        </div>
      )}
      <div className="directory-list" role="list" aria-label="目录内容">
        {segments.length > 0 && (
          <div className="directory-row directory-up" role="listitem">
            <button type="button" className="directory-main" disabled={disabled} onClick={() => props.onNavigate(segments.at(-2)?.path ?? '')}>
              <Icon name="arrow_upward" />
              <span className="directory-name">上一层</span>
            </button>
          </div>
        )}
        {shown.map((entry) => {
          const selected = props.highlight === entry.name;
          return (
            <div
              key={entry.relativePath}
              role="listitem"
              className={cls('directory-row', entry.isProject && 'is-project', selected && 'is-highlight')}
              data-path={entry.relativePath}
            >
              <button
                type="button"
                className="directory-main"
                disabled={disabled || (entry.isProject && !props.onProject)}
                {...(selected ? { 'aria-current': true } : {})}
                onClick={() => (entry.isProject ? props.onProject?.(entry) : props.onNavigate(entry.relativePath))}
                onDoubleClick={() => entry.isProject && props.onProjectDoubleClick?.(entry)}
              >
                <Icon name={entry.isProject ? 'music_note' : 'folder'} className="directory-icon" />
                <span className="directory-name">{entry.name}</span>
                {!entry.isProject && <Icon name="chevron_right" className="directory-go" />}
              </button>
              {entry.isProject && props.projectActions?.(entry)}
            </div>
          );
        })}
        {!listing.loading && !listing.error && shown.length === 0 && (
          <p className="directory-empty">{listing.entries.length === 0 ? '这个目录是空的。' : `这一层没有名称包含“${query.trim()}”的项。`}</p>
        )}
      </div>
    </div>
  );
}
