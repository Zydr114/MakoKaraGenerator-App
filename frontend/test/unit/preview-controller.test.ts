// 成片预览换设备（渲染设备设计 §2.3）：创建带偏好、偏好变了先换设备再取帧、设备错误后停住。
// 会话一定关闭：创建在途时释放、等帧时释放、代次冲突丢弃会话，都关掉服务端会话（W7 验收发现的名额泄漏）。

import { describe, expect, it, vi } from 'vitest';
import { BackendError, type MakoClient } from '../../src/client';
import { revision, type CreatePreviewInput, type PreviewFrameRequestInput, type PreviewRenderBackendInput, type RenderBackend } from '../../src/protocol';
import { PreviewController } from '../../src/editor/preview/preview-controller';

const UNAVAILABLE = { code: 'taskUnavailable', message: 'GPU device unavailable', field: 'renderBackend', failureCode: 'RenderBackendUnavailable', protocolVersion: '1' as const };

/** 只实现预览控制器用到的几个方法；换设备成功时代次加一。gate 让创建或等帧停在半路。 */
function fakeClient(
  options: {
    createFails?: () => boolean;
    switchFails?: () => boolean;
    createGate?: Promise<void>;
    frameGate?: Promise<void>;
    frameFails?: () => BackendError | undefined;
  } = {},
) {
  const calls: string[] = [];
  let generation = 1;
  let sessions = 0;
  let previewId = 'v1';
  let backend: RenderBackend = 'cpu';
  const client = {
    preview: {
      create: async (input: CreatePreviewInput) => {
        calls.push(`create ${input.renderBackend ?? '默认'}`);
        await options.createGate;
        if (options.createFails?.()) throw new BackendError(422, UNAVAILABLE);
        backend = input.renderBackend ?? 'cpu';
        previewId = `v${++sessions}`;
        return { previewId, previewGeneration: String(generation), renderBackend: backend };
      },
      setRenderBackend: async (_id: string, input: PreviewRenderBackendInput) => {
        calls.push(`switch ${input.expectedPreviewGeneration} ${input.renderBackend}`);
        if (options.switchFails?.()) throw new BackendError(422, UNAVAILABLE);
        if (input.renderBackend !== backend) generation++;
        backend = input.renderBackend;
        return { previewId: 'v1', previewGeneration: String(generation), renderBackend: backend };
      },
      requestFrame: async (_id: string, input: PreviewFrameRequestInput) => {
        calls.push(`frame ${input.previewGeneration} ${input.intent}`);
        const failure = options.frameFails?.();
        if (failure) throw failure;
        return { previewGeneration: input.previewGeneration };
      },
      waitForFrame: async (_id: string, _expect: unknown, opts: { signal?: AbortSignal }) => {
        const aborted = new Promise<never>((_, reject) =>
          opts.signal?.addEventListener('abort', () => reject(new DOMException('已中止', 'AbortError')), { once: true }),
        );
        await Promise.race([options.frameGate, aborted]);
        return { renderBackend: backend, content: { contentUrl: '/frame' } };
      },
      replaceInput: async (_id: string, input: { expectedPreviewGeneration: string; expectedSourceRevision: string; source: { sourceRevision: string } }) => {
        calls.push(`replace ${input.expectedPreviewGeneration} ${input.expectedSourceRevision}->${input.source.sourceRevision}`);
        generation++;
        return { previewGeneration: String(generation) };
      },
      closeBestEffort: async (id: string, input: { expectedPreviewGeneration: string }) => {
        calls.push(`close ${id} ${input.expectedPreviewGeneration}`);
      },
    },
    fetchBinary: async () => ({ blob: new Blob(['png']) }),
  } as unknown as MakoClient;
  return { client, calls };
}

const settled = (controller: PreviewController) =>
  vi.waitFor(() => expect(['ready', 'error']).toContain(controller.state$.get().status));

describe('成片预览换设备', () => {
  it('创建时带偏好；偏好变了先用当前代次换设备，再在新代次上取帧', async () => {
    const { client, calls } = fakeClient();
    const controller = new PreviewController(client, 'p1', { width: 320, height: 180 }, { request: 'cpu', target: 'cpu' });
    controller.show(500_000, revision('3'));
    await settled(controller);
    expect(controller.state$.get().frameBackend).toBe('cpu');

    controller.setRenderBackend({ request: 'gpu', target: 'gpu' });
    await vi.waitFor(() => expect(controller.state$.get().frameBackend).toBe('gpu'));
    expect(calls).toEqual(['create cpu', 'frame 1 playback', 'switch 1 gpu', 'frame 2 playback']);
    controller.dispose();
  });

  it('媒体特征分析推进：同一修订重新固定输入并重画当前时刻', async () => {
    const { client, calls } = fakeClient();
    const controller = new PreviewController(client, 'p1', { width: 320, height: 180 });
    controller.refreshInput();
    controller.show(250_000, revision('3'));
    await settled(controller);
    controller.refreshInput();
    await vi.waitFor(() => expect(calls).toContain('frame 2 playback'));
    expect(calls).toEqual(['create 默认', 'frame 1 playback', 'replace 1 3->3', 'frame 2 playback']);
    controller.dispose();
  });

  it('跟随默认时创建不带字段；目标就是会话的设备时不换', async () => {
    const { client, calls } = fakeClient();
    const controller = new PreviewController(client, 'p1', { width: 320, height: 180 });
    controller.show(0, revision('3'));
    await settled(controller);
    controller.setRenderBackend({ request: undefined, target: 'cpu' });
    controller.show(100_000, revision('3'));
    await vi.waitFor(() => expect(controller.state$.get().frameTimeUs).toBe(100_000));
    expect(calls).toEqual(['create 默认', 'frame 1 playback', 'frame 1 playback']);
    controller.dispose();
  });

  it('换设备失败：保留旧帧并停住，不再取帧；改回原设备后照常取帧', async () => {
    const { client, calls } = fakeClient({ switchFails: () => true });
    const controller = new PreviewController(client, 'p1', { width: 320, height: 180 }, { request: 'cpu', target: 'cpu' });
    controller.show(0, revision('3'));
    await settled(controller);

    controller.setRenderBackend({ request: 'gpu', target: 'gpu' });
    await vi.waitFor(() => expect(controller.state$.get().deviceBlocked).toBe(true));
    const state = controller.state$.get();
    expect(state.frameUrl).toBeDefined();
    expect(state.frameBackend).toBe('cpu');
    expect(state.error).toBe('没有换到 GPU：渲染设备不可用：GPU device unavailable（RenderBackendUnavailable）。画面是 CPU 渲染的最后一帧。');

    controller.show(200_000, revision('3'));
    await settled(controller);
    expect(calls).toEqual(['create cpu', 'frame 1 playback', 'switch 1 gpu']);

    controller.setRenderBackend({ request: 'cpu', target: 'cpu' });
    await vi.waitFor(() => expect(controller.state$.get().status).toBe('ready'));
    expect(controller.state$.get().frameTimeUs).toBe(200_000);
    expect(calls.at(-1)).toBe('frame 1 playback');
    controller.dispose();
  });

  it('创建时设备不可用：停住不重复创建，重试才再建', async () => {
    let fail = true;
    const { client, calls } = fakeClient({ createFails: () => fail });
    const controller = new PreviewController(client, 'p1', { width: 320, height: 180 }, { request: 'gpu', target: 'gpu' });
    controller.show(0, revision('3'));
    await vi.waitFor(() => expect(controller.state$.get().deviceBlocked).toBe(true));
    expect(controller.state$.get().error).toBe('渲染设备不可用：GPU device unavailable（RenderBackendUnavailable）。可以在顶栏改用别的渲染设备。');
    controller.show(100_000, revision('3'));
    await settled(controller);
    expect(calls).toEqual(['create gpu']);

    fail = false;
    controller.retry();
    await vi.waitFor(() => expect(controller.state$.get().status).toBe('ready'));
    expect(calls).toEqual(['create gpu', 'create gpu', 'frame 1 playback']);
    controller.dispose();
  });
});

describe('成片预览会话一定关闭', () => {
  it('创建还没返回就释放：建好后立即关闭，不再取帧', async () => {
    let open!: () => void;
    const { client, calls } = fakeClient({ createGate: new Promise<void>((resolve) => (open = resolve)) });
    const controller = new PreviewController(client, 'p1', { width: 320, height: 180 });
    controller.show(0, revision('3'));
    await vi.waitFor(() => expect(calls).toEqual(['create 默认']));
    controller.dispose();
    open();
    await vi.waitFor(() => expect(calls).toEqual(['create 默认', 'close v1 1']));
  });

  it('等帧时释放：中止等待，按最新代次关闭', async () => {
    const { client, calls } = fakeClient({ frameGate: new Promise<void>(() => undefined) });
    const controller = new PreviewController(client, 'p1', { width: 320, height: 180 }, { request: 'cpu', target: 'cpu' });
    controller.show(0, revision('3'));
    await vi.waitFor(() => expect(calls).toEqual(['create cpu', 'frame 1 playback']));
    controller.dispose();
    await vi.waitFor(() => expect(calls.at(-1)).toBe('close v1 1'));
  });

  it('代次冲突：先关掉旧会话再重建', async () => {
    let conflicts = 1;
    const conflict = new BackendError(409, { code: 'previewGenerationConflict', message: 'stale', protocolVersion: '1' });
    const { client, calls } = fakeClient({ frameFails: () => (conflicts-- > 0 ? conflict : undefined) });
    const controller = new PreviewController(client, 'p1', { width: 320, height: 180 });
    controller.show(0, revision('3'));
    await vi.waitFor(() => expect(controller.state$.get().status).toBe('ready'));
    expect(calls).toEqual(['create 默认', 'frame 1 playback', 'close v1 1', 'create 默认', 'frame 1 playback']);
    controller.dispose();
    await vi.waitFor(() => expect(calls.at(-1)).toBe('close v2 1'));
  });
});
