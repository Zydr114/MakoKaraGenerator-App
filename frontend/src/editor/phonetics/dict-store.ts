// 日语离线词典（NAIST-jdic 0.6.3b，BSD-3-Clause）按需下载、SHA-256 校验、OPFS/IndexedDB 持久化缓存与离线本地导入。
// 基础安装包 0 体积增重，绝不包含任何 IPADIC 资产。

export const KUROMOJI_DICT_FILES = [
  'base.dat.gz',
  'check.dat.gz',
  'tid.dat.gz',
  'tid_pos.dat.gz',
  'tid_map.dat.gz',
  'cc.dat.gz',
  'unk.dat.gz',
  'unk_pos.dat.gz',
  'unk_map.dat.gz',
  'unk_char.dat.gz',
  'unk_compat.dat.gz',
  'unk_invoke.dat.gz',
] as const;

export type KuromojiDictFileName = (typeof KUROMOJI_DICT_FILES)[number];

export const NAIST_JDIC_INFO = {
  id: 'naist-jdic-0.6.3b',
  name: 'NAIST-jdic 0.6.3b',
  license: 'BSD-3-Clause',
  estimatedSizeLabel: '约 14~16 MB',
  defaultBaseUrl: 'https://github.com/Zydr114/MakoKaraGenerator-App/releases/download/naist-jdic-0.6.3b',
} as const;

export interface DictFileManifestEntry {
  size: number;
  sha256: string;
}

export interface DictManifest {
  schemaVersion: number;
  id: string;
  name: string;
  version: string;
  license: string;
  totalBytes: number;
  bundleSha256?: string;
  installedAt?: string;
  files: Record<string, DictFileManifestEntry>;
}

export type DictStorageKind = 'opfs' | 'indexeddb' | 'memory' | 'none';

export interface DictCacheStatus {
  installed: boolean;
  backend: DictStorageKind;
  version?: string;
  name?: string;
  license?: string;
  totalBytes?: number;
  installedAt?: string;
}

export interface DictDownloadProgress {
  phase: 'manifest' | 'downloading' | 'verifying' | 'saving' | 'importing';
  currentFile?: string;
  completedFiles: number;
  totalFiles: number;
  loadedBytes: number;
  totalBytes: number;
  percent: number;
}

export interface LocalDictFileInput {
  name: string;
  bytes: () => Promise<Uint8Array>;
}

export interface DictStorageBackend {
  readonly kind: DictStorageKind;
  readFile(name: string): Promise<Uint8Array | null>;
  writeFile(name: string, data: Uint8Array): Promise<void>;
  clear(): Promise<void>;
}

const MANIFEST_FILE = 'manifest.json';
const OPFS_DIR_ROOT = 'mako-phonetics-dict';
const OPFS_DIR_PACK = 'naist-jdic-0.6.3b';
const IDB_DB_NAME = 'mako-phonetics-dict-v1';
const IDB_STORE_NAME = 'files';

export function isGzipBytes(data: Uint8Array): boolean {
  return data.byteLength >= 2 && data[0] === 0x1f && data[1] === 0x8b;
}

export async function sha256Hex(data: Uint8Array): Promise<string> {
  const copy = new Uint8Array(data.byteLength);
  copy.set(data);
  const digest = await globalThis.crypto.subtle.digest('SHA-256', copy.buffer);
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, '0')).join('');
}

/** 将可能经过 gzip 压缩的词典文件解压为 kuromoji 所需的原始二进制 ArrayBuffer。 */
export async function gunzipBytes(data: Uint8Array): Promise<ArrayBuffer> {
  if (!isGzipBytes(data)) {
    const plain = new Uint8Array(data.byteLength);
    plain.set(data);
    return plain.buffer;
  }
  if (typeof DecompressionStream === 'undefined') {
    throw new Error('当前运行环境不支持 DecompressionStream(gzip)，无法解压词典文件。');
  }
  const copy = new Uint8Array(data.byteLength);
  copy.set(data);
  const stream = new Blob([copy]).stream().pipeThrough(new DecompressionStream('gzip'));
  return new Response(stream).arrayBuffer();
}

/** 轻量无依赖 ZIP 解包器（支持 Stored=0 与 Deflate=8），用于离线手动导入词典 .zip 包。 */
export async function extractZipEntries(zipBytes: Uint8Array): Promise<Map<string, Uint8Array>> {
  const view = new DataView(zipBytes.buffer, zipBytes.byteOffset, zipBytes.byteLength);
  const len = zipBytes.byteLength;
  // Locate End of Central Directory (EOCD) signature 0x06054b50 from end of archive.
  let eocdOffset = -1;
  const searchStart = Math.max(0, len - 65557);
  for (let i = len - 22; i >= searchStart; i--) {
    if (view.getUint32(i, true) === 0x06054b50) {
      eocdOffset = i;
      break;
    }
  }
  if (eocdOffset < 0) {
    throw new Error('无效的 ZIP 压缩包：未找到中央目录结尾标记。');
  }

  const totalEntries = view.getUint16(eocdOffset + 10, true);
  const cdOffset = view.getUint32(eocdOffset + 16, true);
  const entries = new Map<string, Uint8Array>();
  const decoder = new TextDecoder('utf-8');

  let cursor = cdOffset;
  for (let i = 0; i < totalEntries; i++) {
    if (cursor + 46 > len || view.getUint32(cursor, true) !== 0x02014b50) {
      throw new Error('ZIP 中央目录结构损坏。');
    }
    const method = view.getUint16(cursor + 10, true);
    const compSize = view.getUint32(cursor + 20, true);
    const nameLen = view.getUint16(cursor + 28, true);
    const extraLen = view.getUint16(cursor + 30, true);
    const commentLen = view.getUint16(cursor + 32, true);
    const localHeaderOffset = view.getUint32(cursor + 42, true);
    const entryName = decoder.decode(zipBytes.subarray(cursor + 46, cursor + 46 + nameLen));
    cursor += 46 + nameLen + extraLen + commentLen;

    if (entryName.endsWith('/')) continue;
    if (localHeaderOffset + 30 > len || view.getUint32(localHeaderOffset, true) !== 0x04034b50) {
      throw new Error(`ZIP 本地文件头损坏：${entryName}`);
    }
    const localNameLen = view.getUint16(localHeaderOffset + 26, true);
    const localExtraLen = view.getUint16(localHeaderOffset + 28, true);
    const dataStart = localHeaderOffset + 30 + localNameLen + localExtraLen;
    const rawSlice = zipBytes.subarray(dataStart, dataStart + compSize);

    let extracted: Uint8Array;
    if (method === 0) {
      extracted = new Uint8Array(rawSlice);
    } else if (method === 8) {
      if (typeof DecompressionStream === 'undefined') {
        throw new Error('当前环境不支持解压 Deflate ZIP 条目。');
      }
      const copy = new Uint8Array(rawSlice.byteLength);
      copy.set(rawSlice);
      const stream = new Blob([copy]).stream().pipeThrough(new DecompressionStream('deflate-raw'));
      extracted = new Uint8Array(await new Response(stream).arrayBuffer());
    } else {
      throw new Error(`不支持的 ZIP 压缩方式（method=${method}）：${entryName}`);
    }

    const baseName = entryName.split('/').pop() ?? entryName;
    if (baseName) entries.set(baseName, extracted);
  }
  return entries;
}

export class MemoryDictStorage implements DictStorageBackend {
  readonly kind: DictStorageKind = 'memory';
  private readonly files = new Map<string, Uint8Array>();

  async readFile(name: string): Promise<Uint8Array | null> {
    const found = this.files.get(name);
    return found ? new Uint8Array(found) : null;
  }

  async writeFile(name: string, data: Uint8Array): Promise<void> {
    this.files.set(name, new Uint8Array(data));
  }

  async clear(): Promise<void> {
    this.files.clear();
  }
}

class OpfsDictStorage implements DictStorageBackend {
  readonly kind: DictStorageKind = 'opfs';

  private async getPackDir(create: boolean): Promise<FileSystemDirectoryHandle | null> {
    const nav = globalThis.navigator as Navigator & {
      storage?: { getDirectory?: () => Promise<FileSystemDirectoryHandle> };
    };
    if (!nav?.storage?.getDirectory) return null;
    try {
      const root = await nav.storage.getDirectory();
      const parent = await root.getDirectoryHandle(OPFS_DIR_ROOT, { create });
      return await parent.getDirectoryHandle(OPFS_DIR_PACK, { create });
    } catch {
      return null;
    }
  }

  async readFile(name: string): Promise<Uint8Array | null> {
    const dir = await this.getPackDir(false);
    if (!dir) return null;
    try {
      const handle = await dir.getFileHandle(name);
      const file = await handle.getFile();
      return new Uint8Array(await file.arrayBuffer());
    } catch {
      return null;
    }
  }

  async writeFile(name: string, data: Uint8Array): Promise<void> {
    const dir = await this.getPackDir(true);
    if (!dir) throw new Error('OPFS 目录不可用。');
    const handle = await dir.getFileHandle(name, { create: true });
    const writable = await handle.createWritable();
    try {
      const copy = new Uint8Array(data.byteLength);
      copy.set(data);
      await writable.write(copy);
    } finally {
      await writable.close();
    }
  }

  async clear(): Promise<void> {
    const nav = globalThis.navigator as Navigator & {
      storage?: { getDirectory?: () => Promise<FileSystemDirectoryHandle> };
    };
    if (!nav?.storage?.getDirectory) return;
    try {
      const root = await nav.storage.getDirectory();
      const parent = await root.getDirectoryHandle(OPFS_DIR_ROOT, { create: false });
      await parent.removeEntry(OPFS_DIR_PACK, { recursive: true });
    } catch {
      // Already absent.
    }
  }
}

class IndexedDbDictStorage implements DictStorageBackend {
  readonly kind: DictStorageKind = 'indexeddb';

  private openDb(): Promise<IDBDatabase> {
    return new Promise((resolve, reject) => {
      const req = globalThis.indexedDB.open(IDB_DB_NAME, 1);
      req.onupgradeneeded = () => {
        const db = req.result;
        if (!db.objectStoreNames.contains(IDB_STORE_NAME)) {
          db.createObjectStore(IDB_STORE_NAME);
        }
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error ?? new Error('打开 IndexedDB 词典缓存失败。'));
    });
  }

  async readFile(name: string): Promise<Uint8Array | null> {
    const db = await this.openDb();
    try {
      return await new Promise<Uint8Array | null>((resolve, reject) => {
        const tx = db.transaction(IDB_STORE_NAME, 'readonly');
        const req = tx.objectStore(IDB_STORE_NAME).get(name);
        req.onsuccess = () => {
          const val = req.result;
          if (val instanceof Uint8Array) resolve(new Uint8Array(val));
          else if (val instanceof ArrayBuffer) resolve(new Uint8Array(val));
          else resolve(null);
        };
        req.onerror = () => reject(req.error ?? new Error(`读取 IndexedDB 文件失败：${name}`));
      });
    } finally {
      db.close();
    }
  }

  async writeFile(name: string, data: Uint8Array): Promise<void> {
    const db = await this.openDb();
    try {
      await new Promise<void>((resolve, reject) => {
        const tx = db.transaction(IDB_STORE_NAME, 'readwrite');
        const copy = new Uint8Array(data.byteLength);
        copy.set(data);
        tx.objectStore(IDB_STORE_NAME).put(copy, name);
        tx.oncomplete = () => resolve();
        tx.onerror = () => reject(tx.error ?? new Error(`写入 IndexedDB 文件失败：${name}`));
      });
    } finally {
      db.close();
    }
  }

  async clear(): Promise<void> {
    const db = await this.openDb();
    try {
      await new Promise<void>((resolve, reject) => {
        const tx = db.transaction(IDB_STORE_NAME, 'readwrite');
        tx.objectStore(IDB_STORE_NAME).clear();
        tx.oncomplete = () => resolve();
        tx.onerror = () => reject(tx.error ?? new Error('清理 IndexedDB 词典缓存失败。'));
      });
    } finally {
      db.close();
    }
  }
}

export async function createDefaultDictStorage(): Promise<DictStorageBackend> {
  // Prefer OPFS in browsers and Electron (zero-RAM-resident large file persistence).
  const nav = globalThis.navigator as
    | (Navigator & { storage?: { getDirectory?: () => Promise<FileSystemDirectoryHandle> } })
    | undefined;
  if (nav?.storage?.getDirectory) {
    try {
      const root = await nav.storage.getDirectory();
      if (root) return new OpfsDictStorage();
    } catch {
      // Fall through to IndexedDB.
    }
  }
  if (typeof globalThis !== 'undefined' && 'indexedDB' in globalThis && globalThis.indexedDB) {
    return new IndexedDbDictStorage();
  }
  return new MemoryDictStorage();
}

function normalizeBaseUrl(url: string): string {
  const trimmed = url.trim().replace(/\/+$/, '');
  return trimmed || NAIST_JDIC_INFO.defaultBaseUrl;
}

function validateManifestShape(raw: unknown): DictManifest {
  if (typeof raw !== 'object' || raw === null) {
    throw new Error('词典 manifest.json 格式无效。');
  }
  const obj = raw as Record<string, unknown>;
  if (typeof obj.version !== 'string' || !obj.version.trim()) {
    throw new Error('词典 manifest.json 缺少有效的 version 字段。');
  }
  if (typeof obj.files !== 'object' || obj.files === null) {
    throw new Error('词典 manifest.json 缺少 files 列表。');
  }
  const files = obj.files as Record<string, DictFileManifestEntry>;
  for (const required of KUROMOJI_DICT_FILES) {
    const entry = files[required];
    if (!entry || typeof entry.size !== 'number' || entry.size <= 0 || typeof entry.sha256 !== 'string' || !/^[a-f0-9]{64}$/i.test(entry.sha256)) {
      throw new Error(`词典 manifest.json 缺少或损坏了必需文件声明：${required}`);
    }
  }
  return {
    schemaVersion: typeof obj.schemaVersion === 'number' ? obj.schemaVersion : 1,
    id: typeof obj.id === 'string' ? obj.id : NAIST_JDIC_INFO.id,
    name: typeof obj.name === 'string' ? obj.name : NAIST_JDIC_INFO.name,
    version: obj.version,
    license: typeof obj.license === 'string' ? obj.license : NAIST_JDIC_INFO.license,
    totalBytes:
      typeof obj.totalBytes === 'number' && obj.totalBytes > 0
        ? obj.totalBytes
        : KUROMOJI_DICT_FILES.reduce((sum, name) => sum + files[name]!.size, 0),
    ...(typeof obj.bundleSha256 === 'string' ? { bundleSha256: obj.bundleSha256 } : {}),
    ...(typeof obj.installedAt === 'string' ? { installedAt: obj.installedAt } : {}),
    files,
  };
}

export class JapaneseDictStore {
  private backendPromise: Promise<DictStorageBackend>;

  constructor(backend?: DictStorageBackend) {
    this.backendPromise = backend ? Promise.resolve(backend) : createDefaultDictStorage();
  }

  private async backend(): Promise<DictStorageBackend> {
    return this.backendPromise;
  }

  async getStatus(): Promise<DictCacheStatus> {
    const store = await this.backend();
    const manifestBytes = await store.readFile(MANIFEST_FILE);
    if (!manifestBytes) {
      return { installed: false, backend: store.kind };
    }
    try {
      const manifest = validateManifestShape(JSON.parse(new TextDecoder().decode(manifestBytes)));
      // Verify all 12 dictionary files are present in storage.
      for (const file of KUROMOJI_DICT_FILES) {
        const exists = await store.readFile(file);
        if (!exists || exists.byteLength === 0) {
          return { installed: false, backend: store.kind };
        }
      }
      return {
        installed: true,
        backend: store.kind,
        version: manifest.version,
        name: manifest.name,
        license: manifest.license,
        totalBytes: manifest.totalBytes,
        ...(manifest.installedAt ? { installedAt: manifest.installedAt } : {}),
      };
    } catch {
      return { installed: false, backend: store.kind };
    }
  }

  async readDictFile(filename: string): Promise<Uint8Array | null> {
    const store = await this.backend();
    return store.readFile(filename);
  }

  async clearCache(): Promise<void> {
    const store = await this.backend();
    await store.clear();
  }

  /**
   * 从远程静态托管/镜像按需下载 NAIST-jdic 0.6.3b 词典包，逐文件校验 SHA-256 后持久写入 OPFS/IndexedDB。
   */
  async downloadFromUrl(
    baseUrlInput?: string,
    options: {
      signal?: AbortSignal;
      onProgress?: (progress: DictDownloadProgress) => void;
      fetchImpl?: typeof fetch;
    } = {},
  ): Promise<DictCacheStatus> {
    const fetchFn = options.fetchImpl ?? globalThis.fetch;
    const useLocalProxy = !options.fetchImpl && typeof globalThis.location !== 'undefined' && /^https?:/i.test(globalThis.location.origin);
    const baseUrl = normalizeBaseUrl(baseUrlInput ?? '');
    const signal = options.signal;
    const report = (p: DictDownloadProgress) => options.onProgress?.(p);

    const fetchDictAsset = async (targetUrl: string): Promise<Response> => {
      if (useLocalProxy) {
        try {
          const targetOrigin = new URL(targetUrl).origin;
          if (targetOrigin !== globalThis.location.origin) {
            const proxied = await fetchFn(
              `${globalThis.location.origin}/__mako/dict-proxy?url=${encodeURIComponent(targetUrl)}`,
              { ...(signal ? { signal } : {}) },
            );
            // If the host exposes /__mako/dict-proxy (marked with X-Mako-Dict-Proxy: 1), always trust its status (including upstream 404).
            if (proxied.headers.get('x-mako-dict-proxy') === '1' || proxied.status !== 404) {
              return proxied;
            }
          }
        } catch (err) {
          if (signal?.aborted) throw err;
          // Fall through to direct fetch when local proxy is unreachable.
        }
      }
      return fetchFn(targetUrl, { ...(signal ? { signal } : {}) });
    };

    const readHttpErrorDetail = async (res: Response): Promise<string> => {
      try {
        if (res.headers.get('content-type')?.includes('application/json')) {
          const body = (await res.json()) as { error?: unknown };
          if (typeof body?.error === 'string' && body.error.trim()) return body.error.trim();
        }
      } catch {
        // Ignore JSON parse errors.
      }
      return `HTTP ${res.status}`;
    };

    report({
      phase: 'manifest',
      currentFile: MANIFEST_FILE,
      completedFiles: 0,
      totalFiles: KUROMOJI_DICT_FILES.length,
      loadedBytes: 0,
      totalBytes: 0,
      percent: 0,
    });

    const manifestRes = await fetchDictAsset(`${baseUrl}/${MANIFEST_FILE}`);
    if (!manifestRes.ok) {
      const detail = await readHttpErrorDetail(manifestRes);
      throw new Error(`下载词典清单失败（${detail}）。请检查网络、代理或切换镜像地址。`);
    }
    const manifest = validateManifestShape(await manifestRes.json());
    const totalBytes = manifest.totalBytes;
    const downloaded = new Map<string, Uint8Array>();
    let loadedBytes = 0;

    for (let index = 0; index < KUROMOJI_DICT_FILES.length; index++) {
      const filename = KUROMOJI_DICT_FILES[index]!;
      const expected = manifest.files[filename]!;
      if (signal?.aborted) throw new DOMException('词典下载已取消。', 'AbortError');

      // Prefer flat `<baseUrl>/<filename>` (GitHub Releases layout), fallback to `<baseUrl>/dict/<filename>`.
      let res = await fetchDictAsset(`${baseUrl}/${filename}`);
      if (res.status === 404) {
        res = await fetchDictAsset(`${baseUrl}/dict/${filename}`);
      }
      if (!res.ok) {
        const detail = await readHttpErrorDetail(res);
        throw new Error(`下载词典文件 ${filename} 失败（${detail}）。`);
      }

      let bytes: Uint8Array;
      if (res.body && typeof res.body.getReader === 'function') {
        const reader = res.body.getReader();
        const chunks: Uint8Array[] = [];
        let fileLoaded = 0;
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          if (value) {
            chunks.push(value);
            fileLoaded += value.byteLength;
            const currentTotal = loadedBytes + fileLoaded;
            report({
              phase: 'downloading',
              currentFile: filename,
              completedFiles: index,
              totalFiles: KUROMOJI_DICT_FILES.length,
              loadedBytes: currentTotal,
              totalBytes,
              percent: totalBytes > 0 ? Math.min(99, Math.round((currentTotal / totalBytes) * 100)) : 0,
            });
          }
        }
        bytes = new Uint8Array(fileLoaded);
        let offset = 0;
        for (const chunk of chunks) {
          bytes.set(chunk, offset);
          offset += chunk.byteLength;
        }
      } else {
        bytes = new Uint8Array(await res.arrayBuffer());
      }

      loadedBytes += bytes.byteLength;
      report({
        phase: 'verifying',
        currentFile: filename,
        completedFiles: index,
        totalFiles: KUROMOJI_DICT_FILES.length,
        loadedBytes,
        totalBytes,
        percent: totalBytes > 0 ? Math.min(99, Math.round((loadedBytes / totalBytes) * 100)) : 0,
      });

      const actualHash = await sha256Hex(bytes);
      if (actualHash.toLowerCase() !== expected.sha256.toLowerCase()) {
        throw new Error(`词典文件 ${filename} SHA-256 校验失败，可能传输损坏，请重试或更换镜像。`);
      }
      downloaded.set(filename, bytes);
    }

    const store = await this.backend();
    report({
      phase: 'saving',
      completedFiles: KUROMOJI_DICT_FILES.length,
      totalFiles: KUROMOJI_DICT_FILES.length,
      loadedBytes,
      totalBytes,
      percent: 99,
    });

    await store.clear();
    for (const [filename, bytes] of downloaded.entries()) {
      await store.writeFile(filename, bytes);
    }
    const stampedManifest: DictManifest = {
      ...manifest,
      installedAt: new Date().toISOString(),
    };
    await store.writeFile(MANIFEST_FILE, new TextEncoder().encode(JSON.stringify(stampedManifest, null, 2)));

    report({
      phase: 'saving',
      completedFiles: KUROMOJI_DICT_FILES.length,
      totalFiles: KUROMOJI_DICT_FILES.length,
      loadedBytes: totalBytes,
      totalBytes,
      percent: 100,
    });

    return this.getStatus();
  }

  /**
   * 离线手动导入本地词典包（支持单个 .zip 文件，或包含 12 个 .dat.gz 的本地目录／多文件选择）。
   */
  async importLocalFiles(
    inputs: readonly LocalDictFileInput[],
    onProgress?: (progress: DictDownloadProgress) => void,
  ): Promise<DictCacheStatus> {
    if (inputs.length === 0) {
      throw new Error('未选择任何词典文件。');
    }

    const fileMap = new Map<string, Uint8Array>();

    if (inputs.length === 1 && inputs[0]!.name.toLowerCase().endsWith('.zip')) {
      onProgress?.({
        phase: 'importing',
        currentFile: inputs[0]!.name,
        completedFiles: 0,
        totalFiles: KUROMOJI_DICT_FILES.length,
        loadedBytes: 0,
        totalBytes: 0,
        percent: 10,
      });
      const zipBytes = await inputs[0]!.bytes();
      const extracted = await extractZipEntries(zipBytes);
      for (const [name, data] of extracted.entries()) {
        fileMap.set(name, data);
      }
    } else {
      for (const item of inputs) {
        const baseName = item.name.replace(/\\/g, '/').split('/').pop() ?? item.name;
        if (baseName) {
          fileMap.set(baseName, await item.bytes());
        }
      }
    }

    const missing = KUROMOJI_DICT_FILES.filter((name) => !fileMap.has(name));
    if (missing.length > 0) {
      throw new Error(`本地词典包缺少必需文件：${missing.join('、')}`);
    }

    // If manifest.json is included in the local pack, verify SHA-256 strictly.
    let manifest: DictManifest | undefined;
    const manifestRaw = fileMap.get(MANIFEST_FILE);
    if (manifestRaw) {
      manifest = validateManifestShape(JSON.parse(new TextDecoder().decode(manifestRaw)));
    }

    const computedFiles: Record<string, DictFileManifestEntry> = {};
    let totalBytes = 0;

    for (let i = 0; i < KUROMOJI_DICT_FILES.length; i++) {
      const filename = KUROMOJI_DICT_FILES[i]!;
      const bytes = fileMap.get(filename)!;
      if (bytes.byteLength === 0) {
        throw new Error(`本地词典文件为空：${filename}`);
      }
      const hash = await sha256Hex(bytes);
      if (manifest) {
        const expected = manifest.files[filename]!;
        if (hash.toLowerCase() !== expected.sha256.toLowerCase()) {
          throw new Error(`本地词典文件 ${filename} SHA-256 校验失败。`);
        }
      }
      computedFiles[filename] = { size: bytes.byteLength, sha256: hash };
      totalBytes += bytes.byteLength;
      onProgress?.({
        phase: 'verifying',
        currentFile: filename,
        completedFiles: i + 1,
        totalFiles: KUROMOJI_DICT_FILES.length,
        loadedBytes: totalBytes,
        totalBytes,
        percent: Math.round(((i + 1) / KUROMOJI_DICT_FILES.length) * 90),
      });
    }

    const store = await this.backend();
    await store.clear();
    for (const filename of KUROMOJI_DICT_FILES) {
      await store.writeFile(filename, fileMap.get(filename)!);
    }

    const finalManifest: DictManifest = manifest
      ? { ...manifest, installedAt: new Date().toISOString() }
      : {
          schemaVersion: 1,
          id: NAIST_JDIC_INFO.id,
          name: `${NAIST_JDIC_INFO.name}（本地导入）`,
          version: 'local-import',
          license: NAIST_JDIC_INFO.license,
          totalBytes,
          installedAt: new Date().toISOString(),
          files: computedFiles,
        };
    await store.writeFile(MANIFEST_FILE, new TextEncoder().encode(JSON.stringify(finalManifest, null, 2)));

    onProgress?.({
      phase: 'saving',
      completedFiles: KUROMOJI_DICT_FILES.length,
      totalFiles: KUROMOJI_DICT_FILES.length,
      loadedBytes: totalBytes,
      totalBytes,
      percent: 100,
    });

    return this.getStatus();
  }
}

export const defaultJapaneseDictStore = new JapaneseDictStore();
