// 工程资源清单与背景缩略图（W5 设计 §2.5）：设置面板和导出页的准备检查共用。

import { useEffect, useState } from 'react';
import { BackendError } from '../../client';
import type { ProjectResource, Revision } from '../../protocol';
import { client } from '../connection';
import { errorText, type EditorSession, type EditorState } from '../session/editor-session';
import { useObservableSelector } from '../ui/hooks';

export interface ResourcesState {
  /** 还没读到时为 null。 */
  resources: ProjectResource[] | null;
  error?: string;
}

/**
 * active 时按当前修订读全部资源，修订变了就重读。读取途中修订又变了（409）不报错，等下一次。
 * 后端没有开放资源清单时 resources 一直是 null。
 */
export function useProjectResources(session: EditorSession, active: boolean): ResourcesState {
  const revision = useObservableSelector<EditorState, Revision>(session.state$, (s) => s.summary.projectRevision);
  const supported = session.supports('project-sessions.resources.read');
  const [state, setState] = useState<ResourcesState>({ resources: null });

  useEffect(() => {
    if (!active || !supported) return;
    let current = true;
    client.projects.resourcesAll(session.projectSessionId, revision).then(
      (resources) => current && setState({ resources }),
      (error: unknown) => {
        if (!current || (error instanceof BackendError && error.code === 'revisionConflict')) return;
        setState((s) => ({ ...s, error: `读取资源清单失败：${errorText(error)}` }));
      },
    );
    return () => {
      current = false;
    };
  }, [session, active, supported, revision]);

  return state;
}

/** 认证读取资源内容并转成对象 URL（图片缩略图）。内容哈希不变就不重读；卸载或换图时释放。 */
export function useResourceObjectUrl(resource: ProjectResource | undefined): string | undefined {
  const contentUrl = resource?.contentUrl;
  const key = resource ? `${resource.resourceId}|${resource.sha256 ?? ''}` : '';
  const [url, setUrl] = useState<string>();

  useEffect(() => {
    setUrl(undefined);
    if (!contentUrl) return;
    const abort = new AbortController();
    let objectUrl: string | undefined;
    client.fetchBinary(contentUrl, { signal: abort.signal }).then(
      (result) => {
        if (abort.signal.aborted || !result.blob) return;
        objectUrl = URL.createObjectURL(result.blob);
        setUrl(objectUrl);
      },
      () => undefined,
    );
    return () => {
      abort.abort();
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
    // 每次读清单的 contentUrl 不保证相同，只按资源与内容哈希决定是否重读。
  }, [key]);

  return url;
}
