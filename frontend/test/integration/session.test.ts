// 连真实 Server：登记 → 心跳 → resume；刷新页面（共享 sessionStorage）通过 resume 恢复同一 client。
// 需要后端仓库的匹配部署，见 scripts/backend-launcher.mjs。

import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { MakoClient, MemoryIdentityStore } from '../../src/client';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';

let backend: TestBackend;
const recorder = new ContractRecorder();

beforeAll(async () => {
  backend = await startTestBackend('session');
});

afterAll(async () => {
  await backend?.stop();
});

function connect(store: MemoryIdentityStore) {
  return MakoClient.connect(backend.provider, { identityStore: store, environment: null, onExchange: recorder.observer });
}

describe('客户端会话（真实 Server）', () => {
  it('新登记、心跳、resume 轮换令牌但保持 clientId', async () => {
    const store = new MemoryIdentityStore();
    const client = await connect(store);
    try {
      const first = client.state$.get();
      expect(first).toMatchObject({ state: 'ready', origin: 'registered', hostInstanceId: backend.current.info.hostInstanceId });
      expect(first.heartbeatIntervalMs).toBeGreaterThanOrEqual(5_000);

      await client.heartbeat();
      expect(client.state$.get().heartbeat).toBe('ok');

      const before = recorder.count('clients.resume');
      await Promise.all([client.resume(), client.resume(), client.resume()]);
      expect(recorder.count('clients.resume') - before).toBe(1);
      expect(client.state$.get()).toMatchObject({ state: 'ready', clientId: first.clientId });

      // resume 后旧令牌失效、新令牌可用：后续请求仍成功。
      const caps = await client.capabilities(true);
      expect(caps.protocolVersion).toBe('1');
    } finally {
      client.dispose();
    }
  });

  it('连续刷新 20 次都通过 resume 恢复同一 client，不新增登记、不触发 clientQuotaExceeded', async () => {
    const store = new MemoryIdentityStore();
    const registrationsBefore = recorder.count('clients.register');
    const first = await connect(store);
    const clientId = first.state$.get().clientId;
    first.dispose();
    for (let i = 0; i < 20; i++) {
      const page = await connect(store);
      expect(page.state$.get()).toMatchObject({ state: 'ready', clientId, origin: 'resumed' });
      page.dispose();
    }
    expect(recorder.count('clients.register') - registrationsBefore).toBe(1);
  });

  it('client state 带事件游标；所有 JSON 响应符合冻结 OpenAPI', async () => {
    const client = await connect(new MemoryIdentityStore());
    try {
      const state = await client.clientState();
      expect(state.eventCursor).toContain(state.clientId);
      expect(state.dormant).toBe(false);
    } finally {
      client.dispose();
    }
    expect(recorder.issues).toEqual([]);
    expect(recorder.validated.size).toBeGreaterThanOrEqual(5);
  });
});
