// 作者草稿的完整源码修订历史、profile 草稿发现与认领（合同 1.40.0 起）。

import type { AuthorCompilationSummary, AuthorDraft } from './author';

/** 草稿保留的一个源码修订；savedAtUtc／compilation 可省略。 */
export interface AuthorSourceRevisionEntry {
  sourceRevision: string;
  savedAtUtc?: string;
  compilation?: AuthorCompilationSummary;
}

/** 精确草稿修订下按源码修订字典序排列的一页历史。 */
export interface AuthorSourceRevisionsResponse {
  draftId: string;
  draftRevision: string;
  revisions: AuthorSourceRevisionEntry[];
  nextAfterSourceRevision?: string;
  protocolVersion: '1';
}

/** active：别的客户端在用；dormant：原 owner 已不活动；orphaned：owner 已不存在；ownedByCaller：本客户端所有。 */
export type ProfileAuthorDraftOwnership = 'active' | 'dormant' | 'orphaned' | 'ownedByCaller';

/** profile 里可发现的作者草稿摘要；不含包内容。 */
export interface ProfileAuthorDraft {
  draftId: string;
  draftRevision: string;
  name: string;
  ownershipRevision: string;
  ownershipState: ProfileAuthorDraftOwnership | (string & {});
  canClaim: boolean;
}

export interface ProfileAuthorDraftListResponse {
  drafts: ProfileAuthorDraft[];
  nextAfterDraftId?: string | null;
  protocolVersion: '1';
}

export interface AuthorDraftClaimResponse {
  draft: AuthorDraft;
  ownershipRevision: string;
  protocolVersion: '1';
}
