// resume 所需身份的保存位置（设计 §3.4）。
// 浏览器只用 sessionStorage，不用 localStorage：两个标签页共享同一 client 时，一边 resume 会轮换
// accessToken 把另一边踢掉，所以每个标签页各自是独立 client。只保存 clientId 与 resumeToken，
// 不保存 accessToken／bootstrapToken。

export interface StoredIdentity {
  clientId: string;
  resumeToken: string;
}

export interface IdentityStore {
  load(hostInstanceId: string): StoredIdentity | null;
  save(hostInstanceId: string, identity: StoredIdentity): void;
  clear(): void;
}

export class MemoryIdentityStore implements IdentityStore {
  private entry: { hostInstanceId: string; identity: StoredIdentity } | null = null;

  load(hostInstanceId: string): StoredIdentity | null {
    return this.entry?.hostInstanceId === hostInstanceId ? this.entry.identity : null;
  }

  save(hostInstanceId: string, identity: StoredIdentity): void {
    this.entry = { hostInstanceId, identity };
  }

  clear(): void {
    this.entry = null;
  }
}

/** 最小 Storage 接口，便于在测试中替换。 */
export interface KeyValueStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

export class WebStorageIdentityStore implements IdentityStore {
  private readonly storage: KeyValueStorage;
  private readonly key: string;

  constructor(storage: KeyValueStorage, key = 'mako.client-identity') {
    this.storage = storage;
    this.key = key;
  }

  load(hostInstanceId: string): StoredIdentity | null {
    try {
      const raw = this.storage.getItem(this.key);
      if (!raw) return null;
      const parsed = JSON.parse(raw) as { hostInstanceId?: unknown; clientId?: unknown; resumeToken?: unknown };
      if (
        parsed.hostInstanceId !== hostInstanceId ||
        typeof parsed.clientId !== 'string' ||
        typeof parsed.resumeToken !== 'string'
      ) {
        // 其他 host 的身份已无意义（服务重启不能用旧 client 恢复），直接丢弃。
        this.clear();
        return null;
      }
      return { clientId: parsed.clientId, resumeToken: parsed.resumeToken };
    } catch {
      return null;
    }
  }

  save(hostInstanceId: string, identity: StoredIdentity): void {
    this.storage.setItem(this.key, JSON.stringify({ hostInstanceId, ...identity }));
  }

  clear(): void {
    this.storage.removeItem(this.key);
  }
}
