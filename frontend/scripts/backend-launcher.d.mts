// scripts/backend-launcher.mjs 的类型声明，供集成测试使用。

export const PROJECT_ROOT: string;
export const DEV_DIR: string;
export const DEFAULT_BOOTSTRAP_FILE: string;
export const DEFAULT_PROFILE: string;
export const DEV_ORIGINS: string[];
export const EXPECTED_PRODUCT_VERSION: string;

export interface BackendInfo {
  baseUrl: string;
  hostInstanceId: string;
  bootstrapToken: string;
  version: string;
  pid: number;
  runDirectory: string;
  writtenAtUtc: string;
}

export interface RunningBackend {
  info: BackendInfo;
  ready: { addresses: string[]; hostInstanceId: string; credentialFile: string; runDirectory: string; version: string; pid: number };
  stop(): Promise<number | null>;
  exited: Promise<number | null>;
}

export function resolveBackendRepo(): string;
export function resolveDist(explicit?: string): { dist: string; version: string };
export function writeBootstrapFile(file: string, info: object): Promise<void>;
export function startBackend(options?: {
  profile?: string;
  bootstrapFile?: string;
  dist?: string;
  origins?: string[];
  log?: (line: string) => void;
  env?: Record<string,string>;
}): Promise<RunningBackend>;
