// 作者在用配置与独立候选分别寄存未失焦原文，防止保存、采用或关闭漏掉无效输入。
import { createContext, useContext, type ReactNode } from 'react';

export interface CommitDraft {
  text: string;
  base: string;
  problem?: string;
}
export interface CommitDraftPort {
  entries: Readonly<Record<string, CommitDraft>>;
  set(key: string, draft: CommitDraft | undefined): void;
  clear(prefix: string): void;
}
interface Scope extends CommitDraftPort { prefix: string }
const DraftContext = createContext<Scope | null>(null);

export function CommitDraftProvider(props: CommitDraftPort & { children: ReactNode }) {
  return <DraftContext.Provider value={{ entries: props.entries, set: props.set, clear: props.clear, prefix: 'configuration' }}>{props.children}</DraftContext.Provider>;
}
export function CommitDraftGroup(props: { name: string; children: ReactNode }) {
  const parent = useContext(DraftContext);
  return parent ? <DraftContext.Provider value={{ ...parent, prefix: `${parent.prefix}/${props.name}` }}>{props.children}</DraftContext.Provider> : props.children;
}
export function useCommitDraftScope(): Scope | null { return useContext(DraftContext); }
