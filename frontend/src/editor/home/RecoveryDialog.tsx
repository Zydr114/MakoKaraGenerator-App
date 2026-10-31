import { useEffect, useState } from 'react';
import type { RecoveryCandidate } from '../../protocol';
import { client } from '../connection';
import { candidateDescription, sortCandidates } from '../model/recovery';
import { EditorSession, errorText } from '../session/editor-session';
import { recoverProject } from '../session/project-files';
import { FormDialog } from '../ui/FormDialog';
import { TextField } from '../ui/fields';

/**
 * 从备份恢复（W5 设计 §1、§2.6）：候选按保存时间倒序，可按工程名搜索（候选不能清理，会越积越多，G27）。
 * 选一项恢复，任务进度显示在对话框里；成功后进入歌词页。恢复出的工程是否记得原位置由摘要的 hasPath 决定。
 */
export function RecoveryDialog(props: { open: boolean; onClose: () => void; onRecovered: (session: EditorSession) => void }) {
  const [candidates, setCandidates] = useState<RecoveryCandidate[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!props.open) return;
    setCandidates(null);
    setLoadError(null);
    setQuery('');
    setSelected(null);
    setError(null);
    let active = true;
    client.recoveryCandidates().then(
      (response) => active && setCandidates(response.candidates),
      (e: unknown) => active && setLoadError(`读取备份失败：${errorText(e)}`),
    );
    return () => {
      active = false;
    };
  }, [props.open]);

  const shown = sortCandidates(candidates ?? [], query);
  const now = new Date();

  const submit = async (recoveryId = selected) => {
    if (!recoveryId) return;
    setError(null);
    setBusy('正在恢复');
    try {
      const summary = await recoverProject(client, recoveryId, setBusy);
      setBusy('正在读取工程');
      props.onRecovered(await EditorSession.attach(client, summary.projectSessionId));
    } catch (e) {
      setError(`恢复失败：${errorText(e)}`);
    } finally {
      setBusy(null);
    }
  };

  return (
    <FormDialog
      open={props.open}
      headline="从备份恢复"
      description="Server 上次退出时没有保存的工程会留下备份。恢复后是一个新打开的工程，撤销记录不会恢复。"
      submitText="恢复"
      submitDisabled={!selected}
      busy={busy !== null}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="recovery-dialog"
    >
      <div className="dialog-form">
        {candidates === null && !loadError && <mdui-linear-progress />}
        {loadError && <p className="dialog-error">{loadError}</p>}
        {candidates && candidates.length === 0 && <p className="muted">没有可以恢复的备份。</p>}
        {candidates && candidates.length > 0 && (
          <>
            <TextField label="按工程名搜索" value={query} onChange={setQuery} />
            <div className="recovery-list" role="listbox" aria-label="备份">
              {shown.map((c) => (
                <button
                  key={c.recoveryId}
                  type="button"
                  role="option"
                  aria-selected={selected === c.recoveryId}
                  className={`recovery-item${selected === c.recoveryId ? ' is-selected' : ''}`}
                  disabled={busy !== null}
                  data-recovery-id={c.recoveryId}
                  onClick={() => setSelected(c.recoveryId)}
                  onDoubleClick={() => {
                    setSelected(c.recoveryId);
                    if (busy === null) void submit(c.recoveryId);
                  }}
                >
                  <span className="recovery-name">{c.projectName}</span>
                  <span className="recovery-meta">{candidateDescription(c, now)}</span>
                </button>
              ))}
              {shown.length === 0 && <p className="muted">没有名称包含“{query.trim()}”的备份。</p>}
            </div>
            <p className="hint">共 {candidates.length} 份备份。</p>
          </>
        )}
        {busy && (
          <div className="dialog-busy">
            <mdui-linear-progress />
            <span>{busy}</span>
          </div>
        )}
        {error && <p className="dialog-error">{error}</p>}
      </div>
    </FormDialog>
  );
}
