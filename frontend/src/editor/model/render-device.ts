// 渲染设备偏好的纯模型（渲染设备设计 §2、§3）：偏好怎么解析成请求里的值与想要的设备，以及设备的文案。
// 请求里带什么（request）与想要的实际设备（target）分开：跟随后端默认时请求不带字段，
// 但给已开的预览换设备时要填具体值，用检测结果里的 defaultBackend。

import type { RenderBackend, RenderBackendAvailability, RenderBackendsResponse } from '../../protocol';

export type RenderDevicePreference = RenderBackend | 'default';

/** 偏好解析的结果。request 是请求里带的值（跟随默认时 undefined）；target 是想要的实际设备（还不知道默认时 undefined）。 */
export interface RenderBackendChoice {
  request: RenderBackend | undefined;
  target: RenderBackend | undefined;
}

/** 一次性渲染、测量、任务只需要知道请求里带什么。 */
export interface RenderBackendRequest {
  choice(): RenderBackendChoice;
}

/** 测试或没有偏好时：一律跟随后端默认。 */
export const FOLLOW_DEFAULT: RenderBackendRequest = { choice: () => ({ request: undefined, target: undefined }) };

/** 请求体里展开用：跟随默认时什么都不加。 */
export function renderBackendField(device: RenderBackendRequest): { renderBackend?: RenderBackend } {
  const { request } = device.choice();
  return request ? { renderBackend: request } : {};
}

export function resolveChoice(preference: RenderDevicePreference, backends: RenderBackendsResponse | undefined): RenderBackendChoice {
  if (preference !== 'default') return { request: preference, target: preference };
  return { request: undefined, target: backends?.defaultBackend };
}

/**
 * 桌面版的成片预览走不走 GPU 共享纹理（2026-11-03 用户决定）：后端没开放连续预览、明确选 CPU 时不走；
 * 选 GPU 时走；跟随后端默认时检测到 GPU 可用就走。浏览器里没有桌面接口，始终是静态预览。
 */
export function usesGpuStream(preference: RenderDevicePreference, backends: RenderBackendsResponse | undefined, streamsSupported: boolean): boolean {
  if (!streamsSupported || preference === 'cpu') return false;
  return preference === 'gpu' || availabilityOf(backends, 'gpu')?.available === true;
}

export const RENDER_BACKEND_LABEL: Record<RenderBackend, string> = { cpu: 'CPU', gpu: 'GPU' };

export function availabilityOf(backends: RenderBackendsResponse | undefined, backend: RenderBackend): RenderBackendAvailability | undefined {
  return backends?.backends.find((item) => item.renderBackend === backend);
}

/** 设备摘要，例如“AMD Radeon 880M Graphics · Vulkan 1.4.354”；不可用时写原因。 */
export function availabilityText(item: RenderBackendAvailability | undefined): string {
  if (!item) return '还没有检测';
  if (!item.available) {
    const reason = item.unavailableReason;
    return `不可用：${reason?.message ?? '没有原因'}${reason?.failureCode ? `（${reason.failureCode}）` : ''}`;
  }
  const device = item.device?.device ?? '';
  const api = item.api === 'vulkan' ? `Vulkan${item.device?.apiVersion ? ` ${item.device.apiVersion}` : ''}` : '';
  return [device, api].filter(Boolean).join(' · ');
}

/** 任务、报告里的设备：没有字段或 null 表示当时没记录，不能当成 CPU。 */
export function recordedBackendText(backend: RenderBackend | null | undefined): string {
  return backend ? RENDER_BACKEND_LABEL[backend] : '未记录';
}
