// 集成测试的真实 Server：每个测试文件用自己的 profile 与启动信息文件，互不干扰，也不占用开发用的 Server。

import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { DEV_DIR, startBackend, type RunningBackend } from '../../scripts/backend-launcher.mjs';
import type { BootstrapProvider, ExchangeRecord } from '../../src/client';
import { OpenApiContract, type SchemaIssue } from './openapi';

export interface TestBackend {
  bootstrapFile: string;
  current: RunningBackend;
  provider: BootstrapProvider;
  restart(): Promise<RunningBackend>;
  stop(): Promise<void>;
}

export async function startTestBackend(name: string): Promise<TestBackend> {
  const profile = join(DEV_DIR, `it-${name}`);
  const bootstrapFile = join(DEV_DIR, `it-${name}.json`);
  const log = (line: string) => {
    if (process.env.MAKO_IT_VERBOSE) console.log(line);
  };
  const backend: TestBackend = {
    bootstrapFile,
    current: await startBackend({ profile, bootstrapFile, log }),
    // 集成测试宿主：直接读取启动信息文件（设计 §4.2）。每次调用都重新读取，以便识别 Server 重启。
    provider: {
      async get() {
        const info = JSON.parse(await readFile(bootstrapFile, 'utf8')) as {
          baseUrl: string;
          hostInstanceId: string;
          bootstrapToken: string;
        };
        return { baseUrl: info.baseUrl, hostInstanceId: info.hostInstanceId, bootstrapToken: info.bootstrapToken };
      },
    },
    async restart() {
      await backend.current.stop();
      backend.current = await startBackend({ profile, bootstrapFile, log });
      return backend.current;
    },
    async stop() {
      const code = await backend.current.stop();
      if (code !== 0) throw new Error(`后端没有正常退出（${code}）`);
    },
  };
  return backend;
}

/** 收集每次 HTTP 往返，并用冻结 OpenAPI 校验 JSON 响应（设计 §5 类型漂移防护）。 */
export class ContractRecorder {
  readonly records: ExchangeRecord[] = [];
  readonly issues: { exchange: string; issues: SchemaIssue[] }[] = [];
  readonly validated = new Set<string>();
  private readonly contract = OpenApiContract.load();

  readonly observer = (record: ExchangeRecord) => {
    this.records.push(record);
    // 二进制、SSE 与无正文响应（304／416／HEAD）没有 JSON schema 可校验。
    if (record.status === undefined || record.responseBody == null || typeof record.responseBody === 'string') return;
    const pathname = record.url.split('?')[0]!;
    const result = this.contract.validateResponse(record.method, pathname, record.status, record.responseBody);
    if (!result) return;
    this.validated.add(`${record.method} ${result.template} ${record.status}`);
    if (result.issues.length) this.issues.push({ exchange: `${record.method} ${record.url} → ${record.status}`, issues: result.issues });
  };

  count(label: string): number {
    return this.records.filter((r) => r.label === label).length;
  }
}
