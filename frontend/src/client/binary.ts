// BinaryReader 与上传（设计 §3.8，按后端审查 F3 修订）。
// 资源内容、预览 PNG、导出成片都需要 Authorization 头，不能直接写进 <img src>／<audio src>：
// 先鉴权 fetch 成 Blob，再由页面决定如何显示，并负责释放旧对象。

import type { BackendCapabilities } from '../protocol';
import type { TransportRequest, TransportResponse } from './transport';

export interface BinaryResult {
  /** 200、206、304 或 416。304／416／HEAD 没有正文。 */
  status: number;
  blob: Blob | null;
  etag: string | null;
  contentType: string | null;
  contentLength: number | null;
  contentRange: string | null;
  /** Content-Disposition 里的文件名（导出成片、歌词文件）；没有时为 null。 */
  fileName: string | null;
  /** 预览帧的 X-Mako-Preview-Expires-At。 */
  previewExpiresAt: string | null;
}

export interface FetchBinaryOptions {
  method?: 'GET' | 'HEAD';
  /** 单段 Range，例如 bytes=0-1023。 */
  range?: string;
  ifNoneMatch?: string;
  ifRange?: string;
  signal?: AbortSignal;
  /** 默认 120 秒；大文件（导出成片）可以放宽。 */
  timeoutMs?: number;
}

export type Sender = <T>(request: TransportRequest) => Promise<TransportResponse<T>>;

export async function fetchBinary(send: Sender, url: string, options: FetchBinaryOptions = {}): Promise<BinaryResult> {
  const headers: Record<string, string> = {};
  if (options.range) headers.Range = options.range;
  if (options.ifNoneMatch) headers['If-None-Match'] = options.ifNoneMatch;
  if (options.ifRange) headers['If-Range'] = options.ifRange;
  const response = await send<Blob | null>({
    method: options.method ?? 'GET',
    path: url,
    auth: 'access',
    headers,
    response: 'blob',
    okStatuses: [304, 416],
    timeoutMs: options.timeoutMs ?? 120_000,
    label: 'binary',
    ...(options.signal ? { signal: options.signal } : {}),
  });
  const length = response.headers.get('content-length');
  return {
    status: response.status,
    blob: response.data,
    etag: response.headers.get('etag'),
    contentType: response.headers.get('content-type'),
    contentLength: length === null ? null : Number(length),
    contentRange: response.headers.get('content-range'),
    fileName: dispositionFileName(response.headers.get('content-disposition')),
    previewExpiresAt: response.headers.get('x-mako-preview-expires-at'),
  };
}

/**
 * Content-Disposition 的文件名：优先 RFC 5987 的 filename*=UTF-8''…，其次 filename="…"／filename=…。
 * 去掉路径片段，只留最后一段。
 */
export function dispositionFileName(header: string | null): string | null {
  if (!header) return null;
  let name: string | undefined;
  const extended = /filename\*\s*=\s*([^']*)'[^']*'([^;]+)/i.exec(header);
  if (extended) {
    try {
      name = decodeURIComponent(extended[2]!.trim());
    } catch {
      name = undefined;
    }
  }
  if (!name) {
    const plain = /filename\s*=\s*(?:"((?:[^"\\]|\\.)*)"|([^;]+))/i.exec(header);
    name = plain ? (plain[1] !== undefined ? plain[1].replace(/\\(.)/g, '$1') : plain[2]!.trim()) : undefined;
  }
  const base = name?.split(/[\\/]/).pop()?.trim();
  return base ? base : null;
}

/** 上传前的客户端预检失败：给出提前、可读的错误，不发请求。 */
export class UploadRejectedError extends Error {
  override readonly name = 'UploadRejectedError';
  readonly reason: 'empty' | 'tooLarge' | 'unsupportedType' | 'badFileName';
  constructor(reason: UploadRejectedError['reason'], message: string) {
    super(message);
    this.reason = reason;
  }
}

const EXTENSION_TYPES: Record<string, string> = {
  wav: 'audio/wav',
  mp3: 'audio/mpeg',
  flac: 'audio/flac',
  ogg: 'audio/ogg',
  m4a: 'audio/mp4',
  aac: 'audio/aac',
  lrc: 'text/plain',
  elrc: 'text/plain',
  txt: 'text/plain',
  srt: 'text/plain',
  vtt: 'text/plain',
  ass: 'text/plain',
  ssa: 'text/plain',
  ttml: 'application/ttml+xml',
  xml: 'application/ttml+xml',
  png: 'image/png',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  webp: 'image/webp',
  gif: 'image/gif',
  bmp: 'image/bmp',
  avif: 'image/avif',
  mp4: 'video/mp4',
  mov: 'video/quicktime',
  webm: 'video/webm',
  mkv: 'video/x-matroska',
  avi: 'video/x-msvideo',
  mpg: 'video/mpeg',
  mpeg: 'video/mpeg',
};

/** 选择上传用的 Content-Type：显式值 → 浏览器给的 file.type → 扩展名 → application/octet-stream。 */
export function inferContentType(fileName: string, blobType: string, allowed: readonly string[]): string {
  const candidates = [blobType, EXTENSION_TYPES[fileName.split('.').pop()?.toLowerCase() ?? '']];
  for (const candidate of candidates) {
    if (candidate && allowed.includes(candidate)) return candidate;
  }
  return 'application/octet-stream';
}

export function preflightUpload(
  file: Blob,
  fileName: string,
  contentType: string,
  capabilities: BackendCapabilities | undefined,
): void {
  if (!fileName || /[\\/]/.test(fileName) || fileName === '.' || fileName === '..') {
    throw new UploadRejectedError('badFileName', `文件名不能是路径：${fileName}`);
  }
  if (file.size === 0) throw new UploadRejectedError('empty', `${fileName} 是空文件。`);
  if (!capabilities) return;
  if (capabilities.maxAssetBytes !== null && file.size > capabilities.maxAssetBytes) {
    throw new UploadRejectedError(
      'tooLarge',
      `${fileName} 有 ${formatBytes(file.size)}，超过 Server 单个资源上限 ${formatBytes(capabilities.maxAssetBytes)}。`,
    );
  }
  if (!capabilities.assetMimeTypes.includes(contentType)) {
    throw new UploadRejectedError('unsupportedType', `Server 不接受类型 ${contentType}（${fileName}）。`);
  }
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MiB`;
}

export async function sha256Hex(data: Blob | ArrayBuffer): Promise<string> {
  const buffer = data instanceof Blob ? await data.arrayBuffer() : data;
  const digest = await crypto.subtle.digest('SHA-256', buffer);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

/** 从 PNG 头读宽高；不是 PNG 时返回 null。 */
export function pngDimensions(bytes: Uint8Array): { width: number; height: number } | null {
  const signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
  if (bytes.length < 24 || signature.some((b, i) => bytes[i] !== b)) return null;
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  return { width: view.getUint32(16), height: view.getUint32(20) };
}
