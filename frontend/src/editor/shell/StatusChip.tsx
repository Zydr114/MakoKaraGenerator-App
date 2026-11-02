import { useEffect } from 'react';
import { Button as AriaButton } from 'react-aria-components';
import type { ConnectionState } from '../../client';
import type { RenderBackend } from '../../protocol';
import { availabilityOf, availabilityText, RENDER_BACKEND_LABEL, type RenderDevicePreference } from '../model/render-device';
import type { RenderDeviceStore } from '../preview/render-device';
import { useObservable } from '../ui/hooks';
import { Icon } from '../ui/Icon';
import { Menu, MenuItem, MenuSection, MenuSeparator, MenuTrigger } from '../ui/Menu';

export const CONNECTION_TEXT: Record<ConnectionState, string> = {
  connecting: '正在连接',
  ready: '已连接',
  refreshing: '正在刷新凭证',
  needsRegistration: '需要重新登记',
  hostRestarted: '后端已重启，正在重新连接',
  failed: '连接失败，正在重试',
};

const PROBLEM: readonly ConnectionState[] = ['failed', 'hostRestarted', 'needsRegistration'];

/** 渲染设备的文字：偏好解析出的设备，不可用时注明；还没检测时没有。 */
export function renderDeviceText(store: RenderDeviceStore): string | undefined {
  const target = store.choice().target;
  if (!target) return undefined;
  const availability = availabilityOf(store.state.backends, target);
  return `${RENDER_BACKEND_LABEL[target]}${availability && !availability.available ? '（不可用）' : ''}`;
}

/**
 * 顶栏状态胶囊（界面改版 §2.5）：写连接状态和渲染设备，例如“已连接，GPU”；点开是渲染设备菜单（渲染设备设计 §2.1）。
 * 菜单里选跟随后端默认、CPU、GPU，最后是重新检测。连接就绪后自动检测一次；GPU 不可用时那一项禁用并写明原因。
 * 偏好是 GPU 而 GPU 不可用时不自动改。
 */
export function StatusChip(props: { store: RenderDeviceStore; connection: ConnectionState }) {
  const { store, connection } = props;
  const state = useObservable(store.state$);
  const { preference, backends } = state;
  const ready = connection === 'ready';

  useEffect(() => {
    if (ready && !state.backends && !state.checking && !state.checkError) void store.check();
  }, [ready, store, state.backends, state.checking, state.checkError]);

  const target = store.choice().target;
  const targetAvailability = target ? availabilityOf(backends, target) : undefined;
  const device = renderDeviceText(store);
  const text = ready && device ? `${CONNECTION_TEXT[connection]}，${device}` : CONNECTION_TEXT[connection];
  const tone = PROBLEM.includes(connection) ? 'error' : !ready ? 'pending' : targetAvailability && !targetAvailability.available ? 'warn' : 'ok';
  const defaultBackend = backends?.defaultBackend;

  const backendItem = (backend: RenderBackend) => {
    const item = availabilityOf(backends, backend);
    // 偏好已经是它时不禁用：用户要能看到原因，选别的项来改。
    return (
      <MenuItem id={backend} description={availabilityText(item)} disabled={!!item && !item.available && preference !== backend}>
        {RENDER_BACKEND_LABEL[backend]}
      </MenuItem>
    );
  };

  return (
    <MenuTrigger>
      <AriaButton className="status-chip" data-connection={connection} data-tone={tone} aria-label={`${text}（渲染设备）`}>
        <i className="status-dot" />
        <span className="status-text">{text}</span>
        <Icon name="arrow_drop_down" />
      </AriaButton>
      <Menu label="渲染设备" placement="bottom end" className="render-device-menu">
        <MenuSection
          title="渲染设备"
          selectionMode="single"
          selectedKeys={[preference]}
          onSelectionChange={(keys) => {
            const [key] = [...keys];
            if (key !== undefined) store.setPreference(key as RenderDevicePreference);
          }}
        >
          <MenuItem id="default" description="请求里不指定设备，用后端启动时的默认">
            {`跟随后端默认${defaultBackend ? `（${RENDER_BACKEND_LABEL[defaultBackend]}）` : ''}`}
          </MenuItem>
          {backendItem('cpu')}
          {backendItem('gpu')}
        </MenuSection>
        <MenuSeparator />
        {state.checkError ? (
          <MenuItem id="check-error" icon="error" disabled description={state.checkError}>
            检测失败
          </MenuItem>
        ) : null}
        <MenuItem id="check" icon="refresh" disabled={state.checking || !ready} onAction={() => void store.check()}>
          {state.checking ? '正在检测…' : '重新检测'}
        </MenuItem>
      </Menu>
    </MenuTrigger>
  );
}
