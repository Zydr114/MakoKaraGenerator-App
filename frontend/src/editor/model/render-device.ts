// 渲染设备偏好的纯模型（渲染设备设计 §2、§3）：偏好怎么解析成请求里的值与想要的设备，以及设备的文案。
// 请求里带什么（request）与想要的实际设备（target）分开：跟随后端默认时请求不带字段，
// 但给已开的预览换设备时要填具体值，用检测结果里的 defaultBackend。

import type { PreviewStreamApi, PreviewStreamTransport, RenderBackend, RenderBackendAvailability, RenderBackendsResponse } from '../../protocol';

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
 * 桌面版的成片预览走不走连续预览流：后端没开放连续预览时不走；明确选 GPU 或 CPU 时走；
 * 跟随后端默认时，待后端设备检测返回后，只要 GPU 或 CPU 任一可用就走连续预览流。
 * 浏览器里没有桌面接口，始终维持静态 PNG 预览。
 */
export function usesGpuStream(preference: RenderDevicePreference, backends: RenderBackendsResponse | undefined, streamsSupported: boolean): boolean {
  if (!streamsSupported) return false;
  if (preference === 'gpu' || preference === 'cpu') return true;
  if (!backends) return false;
  return availabilityOf(backends, 'gpu')?.available === true || availabilityOf(backends, 'cpu')?.available === true;
}

/** 将设备偏好与后端检测结果解析为连续预览流的 api（vulkan / cpu）。 */
export function resolvePreviewStreamApi(preference: RenderDevicePreference, backends: RenderBackendsResponse | undefined): PreviewStreamApi {
  if (preference === 'cpu') return 'cpu';
  if (preference === 'gpu') return 'vulkan';
  if (availabilityOf(backends, 'gpu')?.available === true) return 'vulkan';
  if (backends?.defaultBackend === 'gpu') return 'vulkan';
  return 'cpu';
}

/** 连续预览流设备与传输模式标签：明确区分 GPU · DMA-BUF、GPU · SHM 与 CPU · SHM。 */
export function previewStreamBadge(api: PreviewStreamApi, transport: PreviewStreamTransport | undefined): string {
  const deviceLabel = api === 'cpu' ? 'CPU' : 'GPU';
  if (!transport) return deviceLabel;
  return `${deviceLabel} · ${transport === 'shm' ? 'SHM' : 'DMA-BUF'}`;
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
