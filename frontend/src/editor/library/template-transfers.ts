// .mktpl 导入导出（W7 设计 §2.6，规范 §17.5）：导出所选模板并核对下载的大小与 SHA-256；导入先上传预检，确认后再写库。
// 不依赖 React。每个客户端最多同时保留 2 个传输对象（导出和还没导入的预检），各 5 分钟，下载完也不释放（G32）：
// 这里记下本页拿到的过期时间，429 时写明大约还要等几分钟。

import { BackendError, sha256Hex, type MakoClient } from '../../client';
import type { TemplateArchiveExportResponse, TemplateImportPreflightResponse, TemplateImportResponse, TemplateLibraryId, TemplateTransferAvailability } from '../../protocol';

export const TRANSFER_FILE_NAME = 'MakoKara-templates.mktpl';
export const EXPORT_FEATURE = 'template-libraries.exports';
export const IMPORT_FEATURES = ['template-libraries.imports.preflight', 'template-libraries.imports'] as const;
/** 一次最多导出的模板数（规范 §17.5）。 */
export const MAX_TRANSFER_TEMPLATES = 100;

export const AVAILABILITY_TEXT: Record<TemplateTransferAvailability, string | undefined> = {
  staticAvailable: undefined,
  advancedRuntimeAvailable: '含效果',
  advancedUnsupported: '含效果 · 这个后端不能运行，导入后不能试看和应用',
};

/** 配额不足（429）时的说明：按已知最早的过期时间写大约几分钟；不知道时写最多 5 分钟。 */
export function quotaText(expiries: readonly number[], now: number): string {
  const pending = expiries.filter((t) => t > now);
  const base = '刚导出或预检过的归档要保留 5 分钟，请稍后再试';
  if (pending.length === 0) return `${base}（最多 5 分钟）。`;
  const minutes = Math.max(1, Math.ceil((Math.min(...pending) - now) / 60_000));
  return `${base}，大约 ${minutes} 分钟后可以。`;
}

/** 预检过期或已经用过（404）：要重新选文件。 */
export class PreflightGoneError extends Error {}

export interface TransferFeatures {
  canExport: boolean;
  canImport: boolean;
}

export class TemplateTransfers {
  /** 本页拿到的传输对象：ID → 过期时刻（毫秒）。 */
  private readonly held = new Map<string, number>();

  constructor(
    private readonly client: MakoClient,
    private readonly now: () => number = Date.now,
  ) {}

  /** 连接重置后是新的客户端身份，旧的传输对象不再占它的配额。 */
  forget(): void {
    this.held.clear();
  }

  async features(): Promise<TransferFeatures> {
    const { features } = await this.client.capabilities();
    return { canExport: features.includes(EXPORT_FEATURE), canImport: IMPORT_FEATURES.every((f) => features.includes(f)) };
  }

  /** 导出并下载归档，核对大小和 SHA-256；不一致时抛错，不交出字节。 */
  async exportArchive(libraryId: TemplateLibraryId, expectedRevision: string, templateIds: string[]): Promise<{ blob: Blob; response: TemplateArchiveExportResponse }> {
    const response = await this.quota(() => this.client.templates.exportArchive(libraryId, expectedRevision, templateIds));
    this.hold(response.exportId, response.expiresAtUtc);
    const content = await this.client.templates.archive(response.exportId);
    if (!content.blob) throw new Error(`归档没有正文（HTTP ${content.status}）。`);
    if (content.blob.size !== response.sizeBytes) throw new Error(`下载的归档有 ${content.blob.size} 字节，后端说是 ${response.sizeBytes} 字节，没有保存。`);
    if ((await sha256Hex(content.blob)) !== response.sha256.toLowerCase()) throw new Error('下载的归档 SHA-256 和后端给的不一致，没有保存。');
    return { blob: content.blob, response };
  }

  /** 上传 .mktpl 并预检（只读，不写库）。 */
  async preflight(file: File): Promise<TemplateImportPreflightResponse> {
    const asset = await this.client.assets.upload(file, { fileName: file.name });
    const response = await this.quota(() => this.client.templates.preflightImport('personal', asset.assetId));
    this.hold(response.preflightId, response.expiresAtUtc);
    return response;
  }

  /** 凭预检导入；导入后预检作废，配额随之释放。预检过期或已用过（404）时同样不再计入。 */
  async importArchive(expectedRevision: string, preflight: TemplateImportPreflightResponse): Promise<TemplateImportResponse> {
    try {
      const response = await this.client.templates.importArchive('personal', expectedRevision, preflight.preflightId);
      this.held.delete(preflight.preflightId);
      return response;
    } catch (error) {
      if (error instanceof BackendError && error.status === 404) {
        this.held.delete(preflight.preflightId);
        throw new PreflightGoneError('预检已经过期或用过了，请重新选择文件。');
      }
      throw error;
    }
  }

  /** 记下传输对象的过期时刻，顺便去掉已经过期的。 */
  private hold(id: string, expiresAtUtc: string): void {
    const at = Date.parse(expiresAtUtc);
    this.held.set(id, Number.isNaN(at) ? this.now() + 300_000 : at);
    const now = this.now();
    for (const [key, t] of this.held) if (t <= now) this.held.delete(key);
  }

  private async quota<T>(run: () => Promise<T>): Promise<T> {
    try {
      return await run();
    } catch (error) {
      if (error instanceof BackendError && error.status === 429) throw new Error(quotaText([...this.held.values()], this.now()));
      throw error;
    }
  }
}
