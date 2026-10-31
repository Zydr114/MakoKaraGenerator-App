// BootstrapProvider：浏览器读不到 Server 的私有凭证文件，凭证交付由宿主各自实现（设计 §4.2）。

export interface BootstrapInfo {
  /** Server 实际监听地址，例如 http://127.0.0.1:39959。 */
  baseUrl: string;
  hostInstanceId?: string;
  bootstrapToken: string;
}

export interface BootstrapProvider {
  get(): Promise<BootstrapInfo>;
}

export function staticBootstrapProvider(info: BootstrapInfo): BootstrapProvider {
  return { get: async () => info };
}

/**
 * Vite 开发宿主：同源读取开发服务器插件提供的 /__mako/bootstrap（只在 vite dev 中存在）。
 * 这是对页面自身 origin 的请求，不经过 HttpTransport；返回的令牌只保存在内存里。
 */
export function devServerBootstrapProvider(path = '/__mako/bootstrap'): BootstrapProvider {
  return {
    async get() {
      const response = await fetch(path, { cache: 'no-store', credentials: 'same-origin' });
      if (!response.ok) {
        const detail = await response.text().catch(() => '');
        throw new Error(
          response.status === 503
            ? '后端未启动：请先运行 pnpm dev:backend。'
            : `读取开发启动信息失败（HTTP ${response.status}）${detail ? `：${detail}` : ''}`,
        );
      }
      const info = (await response.json()) as Partial<BootstrapInfo>;
      if (typeof info.baseUrl !== 'string' || typeof info.bootstrapToken !== 'string') {
        throw new Error('开发启动信息格式不正确。');
      }
      return {
        baseUrl: info.baseUrl,
        bootstrapToken: info.bootstrapToken,
        ...(typeof info.hostInstanceId === 'string' ? { hostInstanceId: info.hostInstanceId } : {}),
      };
    },
  };
}
