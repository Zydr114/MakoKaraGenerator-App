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

/** 整份替换基准修订里一个图片或字体：path 不变，字节取自本客户端上传的资源（合同 1.49.0）。 */
export interface AuthorPackageFileReplacement {
  path: string;
  assetId: string;
}

/**
 * 必须完整提交服务返回的全部可编辑文本；二进制身份由 manifest 保持（写基准修订的 sha256）。
 * replacedFiles 列出的图片或字体换成新字节，清单摘要由服务重算；baseSourceRevision 是编辑器所看的修订，省略为最新。
 */
export interface AuthorPackageFilesInput {
  manifestJson: string;
  textFiles: { path: string; text: string }[];
  replacedFiles?: AuthorPackageFileReplacement[];
  baseSourceRevision?: string;
}

/** 只读替换检查的请求（不含 draftId）。 */
export interface AuthorPackageFileReplacementInspectionInput {
  sourceRevision: string;
  path: string;
  assetId: string;
}

/** 替换检查里一份资源：图片带宽高，字体带家族名；另一类字段省略。 */
export interface AuthorPackageResourceInfo {
  /** 图片为编码名小写（png、jpeg…）；字体为 truetype / opentype / collection。 */
  format: string;
  sizeBytes: number;
  sha256: string;
  width?: number;
  height?: number;
  familyNames?: string[];
}

/** 已知提醒码；未来可能新增，未知码按普通提醒显示。 */
export type AuthorPackageReplacementWarningCode = 'imageSizeChanged' | 'fontFamilyChanged';

export interface AuthorPackageReplacementWarning {
  code: AuthorPackageReplacementWarningCode | (string & {});
  message: string;
}

export interface AuthorPackageFileReplacementInspection {
  draftId: string;
  draftRevision: string;
  sourceRevision: string;
  path: string;
  role: 'image' | 'font';
  current: AuthorPackageResourceInfo;
  replacement: AuthorPackageResourceInfo;
  /** 只是提醒，不阻止保存。 */
  warnings: AuthorPackageReplacementWarning[];
  protocolVersion: '1';
}
