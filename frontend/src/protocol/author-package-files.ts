// 作者包文件（行为 §21）：直接消费当前 package-files 合同；role 是开放字符串。
// manifestJson/text 保持原文，不经过 JSON.parse/stringify，避免损失包内 Int64 参数精度。

export interface AuthorPackageFile {
  path: string;
  role: string;
  sizeBytes: number;
  sha256: string;
  /** 由服务判断；非文本文件不返回 text。 */
  editable: boolean;
  text?: string;
}

export interface AuthorPackageFilesResponse {
  draftId: string;
  draftRevision: string;
  sourceRevision: string;
  packageId: string;
  packageRevision: string;
  manifestJson: string;
  files: AuthorPackageFile[];
  protocolVersion: '1';
}

/** 必须完整提交服务返回的全部可编辑文本；二进制身份由 manifest 保持。 */
export interface AuthorPackageFilesInput {
  manifestJson: string;
  textFiles: { path: string; text: string }[];
}
