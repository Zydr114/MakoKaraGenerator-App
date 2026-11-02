import type { EditorState } from '../session/editor-session';

/**
 * 状态栏（界面改版 §2.5，2026-11-02 用户决定保留）：窗口最下面一条 24px。
 * 左边是最近的提示（没有时写“就绪”），右边是同步、进行中的命令、工程修订和读模型。
 */
export function StatusBar(props: { state: EditorState }) {
  const { state } = props;
  return (
    <footer className="status-bar">
      <span className={`notice notice-${state.notice?.level ?? 'none'}`} title={state.notice?.message}>
        {state.notice?.message ?? '就绪'}
      </span>
      <span className="status-facts">
        {state.syncing ? '同步中 · ' : ''}
        {state.pendingCommands > 0 ? `${state.pendingCommands} 条命令进行中 · ` : ''}修订 {state.summary.projectRevision}
        {state.readModel === 'minimal' ? ' · 读模型：最小投影' : ''}
      </span>
    </footer>
  );
}
