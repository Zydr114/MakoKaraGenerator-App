// 后端字体目录（W7 设计 §2.5）：整个应用读一次；连接重置（Server 重启）后作废，下次用到时重读。读失败时可以重试。

import { ObservableValue, type MakoClient } from '../../client';
import { errorText } from '../session/editor-session';

export interface FontCatalogState {
  status: 'idle' | 'loading' | 'ready' | 'failed';
  families: readonly string[];
  /** 例如 worker-skia。 */
  environment?: string;
  error?: string;
}

export class FontCatalog {
  readonly state$ = new ObservableValue<FontCatalogState>({ status: 'idle', families: [] });
  /** 每次作废加一；作废前发出的请求回来后不写状态。 */
  private epoch = 0;

  constructor(private readonly client: MakoClient) {}

  get state(): FontCatalogState {
    return this.state$.get();
  }

  /** 还没读过时读一次；正在读、读到了或失败过（等用户重试）都不重复读。 */
  ensure(): void {
    if (this.state.status === 'idle') void this.load();
  }

  retry(): void {
    if (this.state.status === 'failed') void this.load();
  }

  forget(): void {
    this.epoch++;
    this.state$.set({ status: 'idle', families: [] });
  }

  private async load(): Promise<void> {
    const epoch = this.epoch;
    this.state$.set({ status: 'loading', families: [] });
    try {
      const list = await this.client.fonts.list();
      if (epoch !== this.epoch) return;
      const families = [...new Set(list.families)].sort((a, b) => a.localeCompare(b));
      this.state$.set({ status: 'ready', families, environment: list.environment });
    } catch (error) {
      if (epoch !== this.epoch) return;
      this.state$.set({ status: 'failed', families: [], error: errorText(error) });
    }
  }
}
