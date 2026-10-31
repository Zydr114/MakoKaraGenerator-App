import { useEffect, type ReactNode } from 'react';
import type { RenderBackend } from '../../protocol';
import { availabilityOf, availabilityText, RENDER_BACKEND_LABEL, type RenderDevicePreference } from '../model/render-device';
import { useObservable } from '../ui/hooks';
import type { RenderDeviceStore } from './render-device';

/**
 * 顶栏“渲染设备”（渲染设备设计 §2.1）：按钮写偏好解析出的设备，菜单里选跟随后端默认、CPU、GPU，最后是重新检测。
 * 连接就绪后自动检测一次；GPU 不可用时那一项禁用并写明原因。偏好是 GPU 而 GPU 不可用时不自动改。
 */
export function RenderDeviceMenu(props: { store: RenderDeviceStore; ready: boolean }) {
  const { store, ready } = props;
  const state = useObservable(store.state$);
  const { preference, backends } = state;

  useEffect(() => {
    if (ready && !state.backends && !state.checking && !state.checkError) void store.check();
  }, [ready, store, state.backends, state.checking, state.checkError]);

  const target = store.choice().target;
  const targetAvailability = target ? availabilityOf(backends, target) : undefined;
  const label = target ? `${RENDER_BACKEND_LABEL[target]}${targetAvailability && !targetAvailability.available ? '（不可用）' : ''}` : '渲染设备';
  const defaultBackend = backends?.defaultBackend;

  const option = (value: RenderDevicePreference, title: string, detail: string, disabled = false): ReactNode => (
    <mdui-menu-item key={value} disabled={disabled} onClick={() => store.setPreference(value)}>
      <div slot="custom" className={`render-device-option${preference === value ? ' is-selected' : ''}`} data-value={value}>
        <div className="render-device-option-text">
          <span>{title}</span>
          <span className="render-device-detail">{detail}</span>
        </div>
        {preference === value && <mdui-icon-check />}
      </div>
    </mdui-menu-item>
  );
  const backendOption = (backend: RenderBackend) => {
    const item = availabilityOf(backends, backend);
    // 偏好已经是它时不禁用：用户要能看到原因，选别的项来改。
    return option(backend, RENDER_BACKEND_LABEL[backend], availabilityText(item), !!item && !item.available && preference !== backend);
  };

  return (
    <mdui-dropdown placement="bottom-end">
      <mdui-button slot="trigger" variant="text" className="render-device-button" title="渲染设备">
        <mdui-icon-memory slot="icon" />
        {label}
      </mdui-button>
      <mdui-menu className="render-device-menu">
        {option(
          'default',
          `跟随后端默认${defaultBackend ? `（${RENDER_BACKEND_LABEL[defaultBackend]}）` : ''}`,
          '请求里不指定设备，用后端启动时的默认',
        )}
        {backendOption('cpu')}
        {backendOption('gpu')}
        <mdui-divider />
        {state.checkError && (
          <mdui-menu-item disabled>
            <div slot="custom" className="render-device-option">
              <span className="render-device-detail notice-error">检测失败：{state.checkError}</span>
            </div>
          </mdui-menu-item>
        )}
        <mdui-menu-item disabled={state.checking || !ready} onClick={() => void store.check()}>
          <mdui-icon-refresh slot="icon" />
          {state.checking ? '正在检测…' : '重新检测'}
        </mdui-menu-item>
      </mdui-menu>
    </mdui-dropdown>
  );
}
