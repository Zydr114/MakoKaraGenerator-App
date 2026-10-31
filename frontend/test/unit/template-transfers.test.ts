// W7 第 11 步：.mktpl 导出的核对、导入的预检与作废、配额说明（W7 设计 §2.6，G32）。

import { describe, expect, it } from 'vitest';
import { BackendError, sha256Hex, type MakoClient } from '../../src/client';
import { PreflightGoneError, quotaText, TemplateTransfers } from '../../src/editor/library/template-transfers';
import type { TemplateImportPreflightResponse } from '../../src/protocol';

const MINUTE = 60_000;
const T0 = Date.parse('2026-10-27T08:00:00Z');
const at = (ms: number) => new Date(ms).toISOString();
const quota = () => new BackendError(429, { code: 'resourceQuotaExceeded', message: '模板传输保留配额不足', protocolVersion: '1' });

describe('.mktpl 导入导出', () => {
  it('配额说明：按最早的过期时间写大约几分钟，不知道时写最多 5 分钟', () => {
    expect(quotaText([], T0)).toBe('刚导出或预检过的归档要保留 5 分钟，请稍后再试（最多 5 分钟）。');
    expect(quotaText([T0 + 4.2 * MINUTE, T0 + 2.1 * MINUTE], T0)).toBe('刚导出或预检过的归档要保留 5 分钟，请稍后再试，大约 3 分钟后可以。');
    expect(quotaText([T0 + 10_000], T0)).toMatch('大约 1 分钟后');
    expect(quotaText([T0 - 1], T0)).toMatch('最多 5 分钟');
  });

  it('导出：下载后核对大小和 SHA-256；配额不足时按本页导出的过期时间说明；导入释放预检，过期的预检要重新选文件', async () => {
    const bytes = new TextEncoder().encode('PK archive');
    const sha = await sha256Hex(bytes.buffer as ArrayBuffer);
    let now = T0;
    let exports = 0;
    let body = new Blob([bytes]);
    const client = {
      templates: {
        exportArchive: async () => {
          if (++exports > 2) throw quota();
          return { exportId: `e${exports}`, sizeBytes: bytes.length, sha256: sha.toUpperCase(), expiresAtUtc: at(now + 5 * MINUTE) };
        },
        archive: async () => ({ status: 200, blob: body }),
        preflightImport: async () => {
          throw quota();
        },
        importArchive: async () => {
          throw new BackendError(404, { code: 'notFound', message: 'gone', protocolVersion: '1' });
        },
      },
      assets: { upload: async () => ({ assetId: 'a1' }) },
    } as unknown as MakoClient;
    const transfers = new TemplateTransfers(client, () => now);

    const first = await transfers.exportArchive('personal', 'r1', ['t1']);
    expect(first.blob.size).toBe(bytes.length);
    now += 2 * MINUTE;
    body = new Blob([new TextEncoder().encode('PK archivX')]);
    await expect(transfers.exportArchive('personal', 'r1', ['t1'])).rejects.toThrow('SHA-256 和后端给的不一致');
    // 第一份 3 分钟后过期。
    await expect(transfers.exportArchive('personal', 'r1', ['t1'])).rejects.toThrow('大约 3 分钟后可以');
    await expect(transfers.preflight(new File(['x'], 'a.mktpl'))).rejects.toThrow('大约 3 分钟后可以');

    const preflight = { preflightId: 'p1', templates: [] } as unknown as TemplateImportPreflightResponse;
    await expect(transfers.importArchive('r1', preflight)).rejects.toBeInstanceOf(PreflightGoneError);
    transfers.forget();
    await expect(transfers.exportArchive('personal', 'r1', ['t1'])).rejects.toThrow('最多 5 分钟');
  });
});
