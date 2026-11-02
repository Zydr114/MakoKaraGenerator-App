import { useEffect, useState } from 'react';
import { ObservableValue } from '../../client';
import { client } from '../connection';
import { saveTarget, saveTargetText, type SaveTarget } from '../model/project-directory';
import { errorText } from '../session/editor-session';
import { cls } from '../ui/cls';
import { confirmAction } from '../ui/dialogs';
import { TextField } from '../ui/fields';
import { FormDialog } from '../ui/FormDialog';
import { useObservable } from '../ui/hooks';
import { Icon, type IconName } from '../ui/Icon';
import { ProgressBar } from '../ui/indicators';
import { DirectoryBrowser, useDirectoryListing } from './DirectoryBrowser';
import './home.css';

const TARGET_ICON: Record<SaveTarget['kind'], IconName> = {
  invalid: 'error',
  new: 'add',
  directory: 'folder',
  project: 'warning',
  current: 'check_circle',
};

export interface SaveLocationRequest {
  headline: string;
  /** 开始浏览的上一层目录（'' 是工程目录根）。 */
  parent: string;
  /** 建议的目录名。 */
  name: string;
  /** 工程现在的保存目录（知道时）。 */
  current?: string;
  /** 真正保存；抛错时对话框留着并写明原因。 */
  save: (path: string, progress: (step: string) => void) => Promise<void>;
}

interface Pending {
  request: SaveLocationRequest;
  resolve: (saved: boolean) => void;
}

const pending = new ObservableValue<Pending | null>(null);

/**
 * 打开“保存到…”对话框（W5 设计 §1）：新工程第一次保存、另存为都用它。保存成功返回 true，取消返回 false。
 * 对话框由 App 里的 SaveLocationHost 渲染，这里只排一个请求，调用方可以是不在 React 里的 async 函数。
 */
export function askSaveLocation(request: SaveLocationRequest): Promise<boolean> {
  pending.get()?.resolve(false);
  return new Promise((resolve) => pending.set({ request, resolve }));
}

export function SaveLocationHost() {
  const current = useObservable(pending);
  const done = (saved: boolean) => {
    current?.resolve(saved);
    pending.set(null);
  };
  return <SaveLocationDialog request={current?.request ?? null} onDone={done} />;
}

/**
 * 上面是目录浏览器（选上一层目录，点工程行就选它作为覆盖目标），下面填目录名。
 * 目标是新目录、已有普通目录、已有工程还是工程现在的位置，就地写明；覆盖已有工程要先确认。
 */
function SaveLocationDialog(props: { request: SaveLocationRequest | null; onDone: (saved: boolean) => void }) {
  const { request } = props;
  const open = request !== null;
  const ready = useObservable(client.state$).state === 'ready';
  const [parent, setParent] = useState('');
  const [name, setName] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const listing = useDirectoryListing(open && ready, parent);

  useEffect(() => {
    if (!request) return;
    setParent(request.parent);
    setName(request.name);
    setError(null);
  }, [request]);

  const target = saveTarget(listing.entries, parent, name, request?.current);
  const blocked = target.kind === 'invalid' || listing.loading || !!listing.error;

  const submit = async () => {
    if (!request || blocked || busy) return;
    if (
      target.kind === 'project' &&
      !(await confirmAction({ headline: '覆盖已有工程？', description: `“${target.path}”里的工程会被当前工程替换，原来的内容不能找回。`, confirmText: '覆盖', danger: true }))
    )
      return;
    setError(null);
    setBusy('正在保存');
    try {
      await request.save(target.path, setBusy);
      props.onDone(true);
    } catch (e) {
      setError(`保存失败：${errorText(e)}`);
    } finally {
      setBusy(null);
    }
  };

  return (
    <FormDialog
      open={open}
      headline={request?.headline ?? '保存到'}
      description="先选上一层目录，再填目录名；目录不存在时会新建。"
      submitText={target.kind === 'project' ? '覆盖' : '保存'}
      submitDisabled={blocked}
      busy={busy !== null}
      onSubmit={() => void submit()}
      onClose={() => props.onDone(false)}
      className="save-location-dialog"
    >
      <DirectoryBrowser
        path={parent}
        listing={listing}
        onNavigate={setParent}
        disabled={busy !== null}
        onProject={(entry) => setName(entry.name)}
        highlight={name.trim()}
        className="save-browser"
      />
      <TextField label="目录名" value={name} disabled={busy !== null} onChange={setName} onEnter={() => void submit()} />
      <p className={cls('save-target', `save-target-${target.kind}`)} data-kind={target.kind} aria-live="polite">
        <Icon name={TARGET_ICON[target.kind]} />
        <span>{saveTargetText(target)}</span>
      </p>
      {busy && (
        <div className="busyline">
          <ProgressBar label={busy} />
          <span>{busy}</span>
        </div>
      )}
      {error && (
        <p className="errline" role="alert">
          {error}
        </p>
      )}
    </FormDialog>
  );
}
