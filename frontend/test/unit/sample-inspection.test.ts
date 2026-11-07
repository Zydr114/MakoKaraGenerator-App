// 试样画布的检查投影（合同 1.48.0，SDK v2 检查投影设计 §5）：手写类型对得上冻结 schema，
// 客户端按 inspectionUrl 鉴权读取并核对帧身份，试样画布按开关带 inspect，叠加层与成本模型的换算。

import { afterEach, describe, expect, it, vi } from 'vitest';
import { MakoClient, MemoryIdentityStore, staticBootstrapProvider } from '../../src/client';
import { TEMPLATE_PREVIEW } from '../../src/editor/library/template-thumbnails';
import { TEMPLATE_PREVIEW_OPTIONS, TemplateWorkshop } from '../../src/editor/library/template-workshop';
import {
  costModel,
  DEFAULT_INSPECTION_PREFS,
  INSPECTION_PREFS_KEY,
  overlayModel,
  readInspectionPrefs,
  truncationText,
  wantsInspection,
  writeInspectionPrefs,
} from '../../src/editor/library/sample-inspection';
import { micros, type PreviewFrameInspectionResponse, type PreviewFrameMetadata, type TemplatePreviewOptionsResponse, type TemplateSummary } from '../../src/protocol';
import { createFakeFetch, installFakeBackend, json, problem, type FakeFetch } from '../support/fake-fetch';
import { OpenApiContract } from '../support/openapi';

const contract = OpenApiContract.load();

const SQUARE = [0, 0, 10, 0, 10, 10, 0, 10];
/** 变形后的轮廓：16 个点。 */
const WARPED = Array.from({ length: 16 }, (_, i) => [100 + 50 * Math.cos((i * Math.PI) / 8), 100 + 50 * Math.sin((i * Math.PI) / 8)]).flat();

function response(): PreviewFrameInspectionResponse {
  return {
    frameId: 'f1',
    previewId: 'v1',
    previewGeneration: '1' as PreviewFrameInspectionResponse['previewGeneration'],
    sourceRevision: 'sr',
    timeUs: micros(3_000_000),
    width: 640,
    height: 360,
    renderBackend: 'cpu',
    inspection: {
      canvas: { width: 1920, height: 1080 },
      prepared: true,
      tokens: [
        {
          tokenId: 't1',
          bindingId: 'b1',
          lineId: 'l1',
          independent: false,
          diagnosticCode: 'ClusterSpansElements',
          ink: WARPED,
          clusters: [
            { unitIndex: 0, ownerTokenIds: ['t1', 't2'], independent: false, diagnosticCode: 'ClusterSpansElements', ink: SQUARE },
            { unitIndex: 1, ownerTokenIds: ['t1'], independent: true, ink: SQUARE },
          ],
          ruby: [{ rubyId: 'r1', ink: [0, -20, 10, -20, 10, -12, 0, -12] }],
          omittedClusters: 3,
        },
      ],
      lines: [{ lineId: 'l1', bindingId: 'b1', bounds: [0, 0, 300, 0, 300, 60, 0, 60] }],
      cursors: [{ lineId: 'l1', bindingId: 'b1', tokenId: 't1', progress: 0.42, progressSource: 'fromToken', point: [40, 30], start: [0, 30], end: [300, 30] }],
      particles: [{ instanceId: 'sparks', births: 2, alive: 9, trailSamples: 4, birthPoints: [1, 2, 3, 4], omittedBirthPoints: 5 }],
      effects: [
        { instanceId: 'sparks', packageId: 'demo.sparks', exportId: 'main', sdkMajor: 2, bindingKind: 'lines', lineIds: ['l1'], scriptId: 'ABCDEF0123456789', omittedLines: 0 },
        { instanceId: 'glow', sdkMajor: 1, bindingKind: 'project', lineIds: ['l1'], omittedLines: 0 },
      ],
      cost: {
        wallMs: 12.5,
        prepareMs: 3,
        scriptMs: 1.5,
        graphMs: 0,
        materialMs: 0,
        particleMs: 0.25,
        rasterMs: 4,
        validationMs: 0,
        readbackMs: 0.5,
        scriptCalls: 3,
        readbackBytes: 2048,
        allocatedBytes: 4096,
        gc0: 1,
        gc1: 0,
        gc2: 0,
        scripts: [{ scriptId: 'ABCDEF0123456789', stage: 'Advance', calls: 2, wallMs: 1.5, maxMs: 1 }],
        scriptOverflow: 0,
        anchors: { uniqueTimes: 1, textCalls: 2, futureEndpoints: 0, memoHits: 3, memoMisses: 1 },
        sessionOpenMs: 40.125,
      },
      truncated: { tokens: 0, lines: 0, cursors: 0, particles: 0, effects: 2 },
    },
    host: { renderRoundTripMs: 15.75, encodeMs: 2.5, pngBytes: 48213 },
    protocolVersion: '1',
  };
}

describe('协议类型', () => {
  it('手写的检查投影符合冻结 schema；帧元数据可带 inspectionUrl，取帧可带 inspect', () => {
    expect(contract.validateNamed('PreviewFrameInspectionResponse', response())).toEqual([]);
    const tooLong = response();
    tooLong.inspection.tokens[0]!.ink = [...WARPED, 1, 2];
    expect(contract.validateNamed('PreviewFrameInspectionResponse', tooLong).length).toBeGreaterThan(0);
    const metadata = { requestId: 'r', hostInstanceId: 'h', validUntilUtc: '2026-11-07T12:01:00Z' };
    expect(contract.validateNamed('PreviewFrameCommandRequest', { expectedSourceRevision: '1', previewGeneration: '1', requestSeq: '1', timeUs: '0', intent: 'seek', metadata, inspect: true })).toEqual([]);
  });
});

const clients: MakoClient[] = [];
afterEach(() => {
  for (const client of clients.splice(0)) client.dispose();
});

async function connect(fake: FakeFetch) {
  const client = await MakoClient.connect(staticBootstrapProvider({ baseUrl: 'http://127.0.0.1:4000', bootstrapToken: 'boot-token' }), {
    fetch: fake.fetch,
    identityStore: new MemoryIdentityStore(),
    environment: null,
  });
  clients.push(client);
  return client;
}

function metadata(inspectionUrl?: string): PreviewFrameMetadata {
  return {
    frameId: 'f1',
    previewId: 'v1',
    previewGeneration: '1',
    sourceRevision: 'sr',
    requestSeq: '1',
    timeUs: '3000000',
    width: 640,
    height: 360,
    alphaMode: 'opaque',
    content: { handleId: 'h', mediaType: 'image/png', sizeBytes: 1, sha256: 'x', expiresAt: '2026-11-07T00:00:00Z', contentUrl: '/api/v1/preview-frames/f1/content' },
    renderBackend: 'cpu',
    ...(inspectionUrl ? { inspectionUrl } : {}),
  } as unknown as PreviewFrameMetadata;
}

describe('客户端读取检查投影', () => {
  it('按 inspectionUrl 鉴权读取并核对帧身份；没有 inspectionUrl 时不请求；投影不可用时抛后端错误', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    let body: unknown = response();
    fake.route('GET', '/api/v1/preview-frames/f1/inspection', (req) =>
      req.headers.get('authorization')?.startsWith('Bearer ') ? (body === 'missing' ? problem(404, 'previewInspectionUnavailable') : json(200, body)) : problem(401, 'resourceUnauthorized'),
    );

    expect(await client.preview.readInspection(metadata())).toBeUndefined();
    expect(fake.calls.some((c) => c.path.endsWith('/inspection'))).toBe(false);

    const read = await client.preview.readInspection(metadata('/api/v1/preview-frames/f1/inspection'));
    expect(read?.inspection.cost.wallMs).toBe(12.5);

    body = { ...response(), timeUs: '2999999' };
    await expect(client.preview.readInspection(metadata('/api/v1/preview-frames/f1/inspection'))).rejects.toThrow('帧身份不一致');
    body = 'missing';
    await expect(client.preview.readInspection(metadata('/api/v1/preview-frames/f1/inspection'))).rejects.toMatchObject({ code: 'previewInspectionUnavailable' });
  });
});

describe('试样画布的检查开关', () => {
  const summary: TemplateSummary = { templateId: 'a', name: 'a', kind: 'line', templateRevision: 'ta1', properties: [], hasEffectRecipes: false, hasPackageMaterials: false, hasPackages: false };
  const options: TemplatePreviewOptionsResponse = {
    source: { kind: 'template', sourceId: 's', sourceRevision: 'sr' } as never,
    samples: [{ sampleId: 'workshop-single-line-v1', multiLine: false, durationUs: '12000000', targets: [{ key: 'line', binding: { kind: 'line', ids: [], includeDescendants: false } }] }] as never,
    materialSlots: [],
    defaultSampleId: 'workshop-single-line-v1',
    protocolVersion: '1',
  };

  /** 假后端：带 inspect 的帧才有 inspectionUrl；failInspection 为真时读投影失败。 */
  function fakeClient() {
    const log = { frames: [] as { timeUs: string; inspect: boolean }[], reads: 0 };
    let failInspection = false;
    const client = {
      capabilities: async () => ({ features: [TEMPLATE_PREVIEW, TEMPLATE_PREVIEW_OPTIONS] }),
      templates: { previewOptions: async () => options },
      preview: {
        createTemplatePreview: async () => ({ previewId: 'v1', previewGeneration: '1', source: { sourceRevision: 'sr' } }),
        requestFrame: async (_id: string, input: { timeUs: string; inspect?: boolean }) => {
          log.frames.push({ timeUs: input.timeUs, inspect: input.inspect === true });
          return { previewGeneration: '1' };
        },
        waitForFrame: async () => {
          const last = log.frames.at(-1)!;
          return { ...metadata(last.inspect ? '/api/v1/preview-frames/f1/inspection' : undefined), content: { sha256: `v1@${last.timeUs}` } };
        },
        fetchFrame: async (frame: { content: { sha256: string } }) => ({ blob: new Blob([frame.content.sha256]), sha256: frame.content.sha256, verified: true }),
        readInspection: async (frame: { inspectionUrl?: string }) => {
          log.reads++;
          if (failInspection) throw new Error('网络断了');
          return frame.inspectionUrl ? response() : undefined;
        },
        closeBestEffort: async () => undefined,
      },
    } as unknown as MakoClient;
    return { client, log, failInspection: (on: boolean) => void (failInspection = on) };
  }

  it('默认不带 inspect；打开后按当前时刻重取并带上投影，读不到投影时画面照常；关闭后去掉投影、不再带', async () => {
    vi.useFakeTimers();
    try {
      const { client, log, failInspection } = fakeClient();
      const workshop = new TemplateWorkshop(client, undefined, 0);
      workshop.show({ kind: 'saved', libraryId: 'builtin', libraryRevision: 'r1', summary }, 'builtin|a');
      await vi.advanceTimersByTimeAsync(0);
      expect(log.frames).toEqual([{ timeUs: '3000000', inspect: false }]);
      expect(workshop.state.frame).not.toHaveProperty('inspection');

      workshop.setInspect(true);
      await vi.advanceTimersByTimeAsync(0);
      expect(log.frames.at(-1)).toEqual({ timeUs: '3000000', inspect: true });
      expect(workshop.state.frame).toMatchObject({ status: 'ready', inspection: { frameId: 'f1' } });

      // 拖时刻：留着的旧画面带着它自己的投影，新帧也带。
      workshop.seek(4_000_000);
      expect(workshop.state.frame).toMatchObject({ status: 'loading', inspection: { frameId: 'f1' } });
      failInspection(true);
      await vi.advanceTimersByTimeAsync(0);
      expect(workshop.state.frame).toMatchObject({ status: 'ready', sha256: 'v1@4000000', inspectionError: '检查投影读取失败：网络断了' });
      expect(workshop.state.frame).not.toHaveProperty('inspection');

      workshop.setInspect(false);
      expect(workshop.state.frame).not.toHaveProperty('inspectionError');
      const before = log.frames.length;
      workshop.seek(5_000_000);
      await vi.advanceTimersByTimeAsync(0);
      expect(log.frames.slice(before)).toEqual([{ timeUs: '5000000', inspect: false }]);
      workshop.dispose();
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('叠加层模型', () => {
  it('不能拆开的字簇画红色虚线并标出原因；注音连到所属文本单元；播放头写进度与来源；效果按行画外框', () => {
    const model = overlayModel(response().inspection);
    expect(model).toMatchObject({ width: 1920, height: 1080, fontSize: 24 });
    expect(model.counts).toEqual({ clusters: 2, ruby: 1, cursors: 1, particles: 7, effects: 2 });
    const clusters = model.shapes.filter((s) => s.layer === 'clusters');
    // 文本单元外框是 16 点的变形轮廓。
    expect(clusters[0]).toMatchObject({ kind: 'polygon', tone: 'broken' });
    expect((clusters[0] as { points: string }).points.split(' ')).toHaveLength(16);
    expect(clusters.filter((s) => s.kind === 'polygon').map((s) => s.tone)).toEqual(['broken', 'broken', 'cluster']);
    expect(clusters.filter((s) => s.kind === 'label').map((s) => (s as { text: string }).text)).toEqual(['ClusterSpansElements', 'ClusterSpansElements']);
    expect(model.shapes.filter((s) => s.layer === 'ruby').map((s) => s.kind)).toEqual(['polygon', 'line']);
    expect(model.shapes.find((s) => s.layer === 'cursors' && s.kind === 'label')).toMatchObject({ text: '42% 按字' });
    expect(model.shapes.filter((s) => s.layer === 'particles')).toHaveLength(2);
    const effectLabels = model.shapes.filter((s) => s.layer === 'effects' && s.kind === 'label') as { text: string; y: number }[];
    expect(effectLabels.map((s) => s.text)).toEqual(['demo.sparks · main', 'glow']);
    expect(effectLabels[1]!.y).toBeLessThan(effectLabels[0]!.y);
  });

  it('隐藏的类别不画但照样计数；坏多边形不画', () => {
    const inspection = response().inspection;
    inspection.lines[0]!.bounds = [1, 2, 3];
    const model = overlayModel(inspection, ['clusters', 'ruby', 'cursors', 'particles']);
    expect(model.shapes).toEqual([]);
    expect(model.counts.clusters).toBe(2);
  });
});

describe('成本模型', () => {
  it('Worker 阶段、计数、脚本对回效果实例，Host 分开写；省略的条目逐类写出', () => {
    const model = costModel(response());
    expect(model.phases.map((p) => p.label)).toEqual(['准备', '脚本', '图', '材质', '粒子', '栅格', '验证', '读回']);
    expect(model.scaleMs).toBe(12.5);
    expect(model.scripts).toEqual([{ scriptId: 'ABCDEF0123456789', stage: 'Advance', owner: 'demo.sparks · main', calls: 2, wallMs: 1.5, maxMs: 1 }]);
    expect(model.counts).toContainEqual({ label: '粒子', value: '出生 2 · 存活 9 · 拖尾 4' });
    expect(model.counts).toContainEqual({ label: '会话打开', value: '40.1 ms' });
    expect(model.host).toEqual([
      { label: '渲染往返', value: '15.8 ms' },
      { label: 'PNG 编码', value: '2.50 ms' },
      { label: 'PNG 大小', value: '47.1 KiB' },
    ]);
    expect(model.truncated).toBe('超出上限未列出：字簇 3，出生点 5，效果实例 2');
    const clean = response().inspection;
    clean.tokens[0]!.omittedClusters = 0;
    clean.particles[0]!.omittedBirthPoints = 0;
    clean.truncated.effects = 0;
    expect(truncationText(clean)).toBeUndefined();
  });
});

describe('开关偏好', () => {
  it('记在本机；读不懂的内容按默认；任一开关打开就要检查', () => {
    const store = new Map<string, string>();
    const storage = { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => void store.set(k, v) };
    expect(readInspectionPrefs(storage)).toEqual(DEFAULT_INSPECTION_PREFS);
    writeInspectionPrefs(storage, { overlay: true, cost: false, hidden: ['ruby'] });
    expect(readInspectionPrefs(storage)).toEqual({ overlay: true, cost: false, hidden: ['ruby'] });
    expect(wantsInspection(readInspectionPrefs(storage))).toBe(true);
    store.set(INSPECTION_PREFS_KEY, '{oops');
    expect(readInspectionPrefs(storage)).toEqual(DEFAULT_INSPECTION_PREFS);
    store.set(INSPECTION_PREFS_KEY, JSON.stringify({ overlay: 'yes', hidden: ['ruby', 'bogus'] }));
    expect(readInspectionPrefs(storage)).toEqual({ overlay: false, cost: false, hidden: ['ruby'] });
    expect(wantsInspection(DEFAULT_INSPECTION_PREFS)).toBe(false);
  });
});
