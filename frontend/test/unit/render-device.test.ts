// 渲染设备偏好（渲染设备设计 §2、§3）：偏好读写、解析、检测结果与文案。

import { describe, expect, it } from 'vitest';
import { BackendError, type MakoClient } from '../../src/client';
import type { RenderBackendsResponse } from '../../src/protocol';
import { availabilityOf, availabilityText, previewStreamBadge, recordedBackendText, renderBackendField, resolvePreviewStreamApi, usesGpuStream } from '../../src/editor/model/render-device';
import { RENDER_DEVICE_STORAGE_KEY, renderBackendErrorText, RenderDeviceStore } from '../../src/editor/preview/render-device';

const UNAVAILABLE = { code: 'taskUnavailable', message: 'GPU device unavailable', field: 'renderBackend', failureCode: 'RenderBackendUnavailable', protocolVersion: '1' as const };

const backends = (gpuAvailable: boolean, defaultBackend: 'cpu' | 'gpu' = 'cpu'): RenderBackendsResponse => ({
  defaultBackend,
  backends: [
    { renderBackend: 'cpu', available: true, api: 'raster', device: { device: 'Skia CPU raster' } },
    gpuAvailable
      ? { renderBackend: 'gpu', available: true, api: 'vulkan', device: { device: 'AMD Radeon 880M Graphics (RADV STRIX1)', apiVersion: '1.4.354' } }
      : { renderBackend: 'gpu', available: false, api: 'vulkan', unavailableReason: UNAVAILABLE },
  ],
});

function memoryStorage(initial: Record<string, string> = {}) {
  const map = new Map(Object.entries(initial));
  return {
    map,
    getItem: (key: string) => map.get(key) ?? null,
    setItem: (key: string, value: string) => void map.set(key, value),
    removeItem: (key: string) => void map.delete(key),
  };
}

function fakeClient(result: () => Promise<RenderBackendsResponse>) {
  let calls = 0;
  const client = {
    preview: {
      renderBackends: () => {
        calls++;
        return result();
      },
    },
  } as unknown as Pick<MakoClient, 'preview'>;
  return { client, calls: () => calls };
}

describe('渲染设备偏好', () => {
  it('没存过或存了无效值时跟随默认；选择写进本机，改回默认时删掉', () => {
    const storage = memoryStorage({ [RENDER_DEVICE_STORAGE_KEY]: 'vulkan' });
    const store = new RenderDeviceStore(fakeClient(async () => backends(true)).client, storage);
    expect(store.state.preference).toBe('default');
    store.setPreference('gpu');
    expect(storage.map.get(RENDER_DEVICE_STORAGE_KEY)).toBe('gpu');
    expect(new RenderDeviceStore(fakeClient(async () => backends(true)).client, storage).state.preference).toBe('gpu');
    store.setPreference('default');
    expect(storage.map.has(RENDER_DEVICE_STORAGE_KEY)).toBe(false);
  });

  it('跟随默认时请求不带字段，目标设备取检测结果里的默认；明确选择时两者都是它', async () => {
    const store = new RenderDeviceStore(fakeClient(async () => backends(true, 'gpu')).client, memoryStorage());
    expect(store.choice()).toEqual({ request: undefined, target: undefined });
    expect(renderBackendField(store)).toEqual({});
    await store.check();
    expect(store.choice()).toEqual({ request: undefined, target: 'gpu' });
    store.setPreference('cpu');
    expect(store.choice()).toEqual({ request: 'cpu', target: 'cpu' });
    expect(renderBackendField(store)).toEqual({ renderBackend: 'cpu' });
  });

  it('并发检测只发一次；失败时保留上一次的结果并写明原因，成功后清掉原因；连接重置后作废', async () => {
    let fail = false;
    const fake = fakeClient(async () => {
      if (fail) throw new Error('连接断了');
      return backends(true);
    });
    const store = new RenderDeviceStore(fake.client, memoryStorage());
    await Promise.all([store.check(), store.check()]);
    expect(fake.calls()).toBe(1);
    fail = true;
    await store.check();
    expect(store.state.backends?.defaultBackend).toBe('cpu');
    expect(store.state.checkError).toBe('连接断了');
    fail = false;
    await store.check();
    expect(store.state.checkError).toBeUndefined();
    store.forget();
    expect(store.state.backends).toBeUndefined();
    expect(store.state.checking).toBe(false);
  });

  it('桌面版：后端开放连续预览时支持 GPU 与 CPU 连续流；跟随默认等待检测结果后按 GPU 优先、CPU 兜底解析', () => {
    expect(usesGpuStream('cpu', backends(true, 'gpu'), true)).toBe(true);
    expect(usesGpuStream('cpu', undefined, true)).toBe(true);
    expect(usesGpuStream('gpu', backends(true), false)).toBe(false);
    expect(usesGpuStream('cpu', backends(true), false)).toBe(false);
    expect(usesGpuStream('gpu', undefined, true)).toBe(true);
    expect(usesGpuStream('default', backends(true), true)).toBe(true);
    expect(usesGpuStream('default', backends(false), true)).toBe(true);
    expect(usesGpuStream('default', undefined, true)).toBe(false);

    expect(resolvePreviewStreamApi('cpu', backends(true, 'gpu'))).toBe('cpu');
    expect(resolvePreviewStreamApi('gpu', backends(false, 'cpu'))).toBe('vulkan');
    expect(resolvePreviewStreamApi('default', backends(true, 'cpu'))).toBe('vulkan');
    expect(resolvePreviewStreamApi('default', backends(false, 'cpu'))).toBe('cpu');

    expect(previewStreamBadge('vulkan', 'dma-buf')).toBe('GPU · DMA-BUF');
    expect(previewStreamBadge('vulkan', 'shm')).toBe('GPU · SHM');
    expect(previewStreamBadge('cpu', 'shm')).toBe('CPU · SHM');
    expect(previewStreamBadge('cpu', undefined)).toBe('CPU');
  });
});

describe('渲染设备文案', () => {
  it('设备摘要：可用时写设备与 Vulkan 版本，不可用时写原因，没检测时写明', () => {
    expect(availabilityText(availabilityOf(backends(true), 'gpu'))).toBe('AMD Radeon 880M Graphics (RADV STRIX1) · Vulkan 1.4.354');
    expect(availabilityText(availabilityOf(backends(true), 'cpu'))).toBe('Skia CPU raster');
    expect(availabilityText(availabilityOf(backends(false), 'gpu'))).toBe('不可用：GPU device unavailable（RenderBackendUnavailable）');
    expect(availabilityText(undefined)).toBe('还没有检测');
  });

  it('只有 field=renderBackend 的错误算设备错误；任务里没记录的设备写“未记录”', () => {
    expect(renderBackendErrorText(new BackendError(422, UNAVAILABLE))).toBe('渲染设备不可用：GPU device unavailable（RenderBackendUnavailable）');
    expect(renderBackendErrorText(new BackendError(409, { code: 'revisionConflict', message: 'x', protocolVersion: '1' }))).toBeUndefined();
    expect(recordedBackendText('gpu')).toBe('GPU');
    expect(recordedBackendText(null)).toBe('未记录');
    expect(recordedBackendText(undefined)).toBe('未记录');
  });
});
