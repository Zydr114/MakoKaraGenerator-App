// 渲染设备偏好（渲染设备设计 §2.1、§3）：记在本机 localStorage，对所有工程生效，不写进工程。
// 偏好怎么解析、设备文案见 model/render-device.ts。

import { BackendError, ObservableValue, type MakoClient } from '../../client';
import { RENDER_BACKENDS, type RenderBackend, type RenderBackendsResponse } from '../../protocol';
import { resolveChoice, type RenderBackendChoice, type RenderBackendRequest, type RenderDevicePreference } from '../model/render-device';

export interface RenderDeviceState {
  preference: RenderDevicePreference;
  /** 最近一次检测成功的结果。 */
  backends?: RenderBackendsResponse;
  checking: boolean;
  /** 最近一次检测失败的原因；成功后清掉。 */
  checkError?: string;
}

export const RENDER_DEVICE_STORAGE_KEY = 'mako.renderBackend';

type PreferenceStorage = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;

export class RenderDeviceStore implements RenderBackendRequest {
  readonly state$: ObservableValue<RenderDeviceState>;
  private checking: Promise<void> | undefined;

  constructor(
    private readonly client: Pick<MakoClient, 'preview'>,
    private readonly storage: PreferenceStorage | undefined = typeof localStorage === 'undefined' ? undefined : localStorage,
  ) {
    this.state$ = new ObservableValue<RenderDeviceState>({ preference: readPreference(storage), checking: false });
  }

  get state(): RenderDeviceState {
    return this.state$.get();
  }

  choice(): RenderBackendChoice {
    return resolveChoice(this.state.preference, this.state.backends);
  }

  setPreference(preference: RenderDevicePreference): void {
    if (preference === this.state.preference) return;
    if (preference === 'default') this.storage?.removeItem(RENDER_DEVICE_STORAGE_KEY);
    else this.storage?.setItem(RENDER_DEVICE_STORAGE_KEY, preference);
    this.state$.set({ ...this.state, preference });
  }

  /** 重新读一次检测结果；并发调用共用同一次。失败时保留上一次的结果，写明原因。 */
  check(): Promise<void> {
    this.checking ??= this.doCheck().finally(() => {
      this.checking = undefined;
    });
    return this.checking;
  }

  /** 连接重置（Server 重启）后，旧的检测结果作废。 */
  forget(): void {
    const { backends: _old, checkError: _error, ...rest } = this.state;
    this.state$.set(rest);
  }

  private async doCheck(): Promise<void> {
    this.state$.set({ ...this.state, checking: true });
    try {
      const backends = await this.client.preview.renderBackends();
      const { checkError: _error, ...rest } = this.state;
      this.state$.set({ ...rest, backends, checking: false });
    } catch (error) {
      this.state$.set({ ...this.state, checking: false, checkError: error instanceof Error ? error.message : String(error) });
    }
  }
}

function readPreference(storage: PreferenceStorage | undefined): RenderDevicePreference {
  const value = storage?.getItem(RENDER_DEVICE_STORAGE_KEY);
  return (RENDER_BACKENDS as readonly string[]).includes(value ?? '') ? (value as RenderBackend) : 'default';
}

/** 设备错误（field=renderBackend，规范 §25.4）的文案；不是设备错误时返回 undefined。 */
export function renderBackendErrorText(error: unknown): string | undefined {
  if (!(error instanceof BackendError) || error.field !== 'renderBackend') return undefined;
  return `渲染设备不可用：${error.message}${error.failureCode ? `（${error.failureCode}）` : ''}`;
}
