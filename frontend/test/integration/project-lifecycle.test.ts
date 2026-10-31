// W5 工程闭环与导出在真实 Server 上的往返（W5 设计 §2.2、§4 第 2 步）：
// 四种新建组合与导入规划、改名、背景图片与视频、替换与清除音频、资源清单；
// 保存到目录、另存为、目录浏览、只读打开；歌词导出四种格式；正式验证；MP4／MOV 导出与下载、取消；恢复候选与恢复。
// 测试按顺序共享状态；每个真实 JSON 响应都用冻结 OpenAPI 校验。

import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { BackendError, MakoClient, MemoryIdentityStore } from '../../src/client';
import {
  isExportFailureReport,
  micros,
  type JobResponse,
  type LyricsExportFormat,
  type ProjectCommand,
  type ProjectCommandResponse,
  type ProjectSessionSummary,
} from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';
import { SAMPLE_LRC, makePng, makeWav } from '../support/fixtures';

let backend: TestBackend;
let client: MakoClient;
const recorder = new ContractRecorder();
const runId = `w5-${Date.now().toString(36)}`;
const opened: string[] = [];

beforeAll(async () => {
  backend = await startTestBackend('lifecycle');
  client = await MakoClient.connect(backend.provider, {
    identityStore: new MemoryIdentityStore(),
    environment: null,
    onExchange: recorder.observer,
    jobPollIntervalMs: 250,
  });
});

afterAll(async () => {
  for (const id of opened) await client.projects.close(id, { policy: 'discard' }).then((job) => client.jobs.waitForTerminal(job.jobId)).catch(() => undefined);
  client?.dispose();
  await backend?.stop();
});

const upload = (bytes: Uint8Array<ArrayBuffer> | string, fileName: string) => client.assets.upload(new Blob([bytes]), { fileName });

async function create(input: {
  name: string;
  audio?: string;
  lyrics?: string;
  outputDurationUs?: number;
  size?: [number, number];
}): Promise<ProjectSessionSummary> {
  const summary = await client.projects.create({
    name: input.name,
    ...(input.audio ? { audioAssetId: input.audio } : {}),
    ...(input.lyrics ? { lyricsAssetId: input.lyrics } : {}),
    composition: {
      canvasWidth: input.size?.[0] ?? 320,
      canvasHeight: input.size?.[1] ?? 180,
      frameRateNumerator: 30,
      frameRateDenominator: 1,
      ...(input.outputDurationUs ? { outputDurationUs: input.outputDurationUs } : {}),
    },
  });
  opened.push(summary.projectSessionId);
  return summary;
}

const run = (id: string, command: ProjectCommand): Promise<ProjectCommandResponse> => client.projects.command(id, command);

async function rejected(promise: Promise<unknown>): Promise<BackendError> {
  try {
    await promise;
  } catch (error) {
    if (error instanceof BackendError) return error;
    throw error;
  }
  throw new Error('应当被拒绝');
}

async function finish(accepted: JobResponse, timeoutMs = 120_000): Promise<JobResponse> {
  return client.jobs.waitForTerminal(accepted.jobId, { timeoutMs });
}

const settingsOf = (id: string) => client.projects.settings(id, client.projects.knownRevision(id)!);
const resourcesOf = (id: string) => client.projects.resourcesAll(id, client.projects.knownRevision(id)!);

async function directory(relativePath: string, access: 'read' | 'readWrite' = 'readWrite') {
  return (await client.files.createHandle({ rootGrantId: 'projects', relativePath, access })).handleId;
}

const sha = (bytes: ArrayBuffer) => createHash('sha256').update(new Uint8Array(bytes)).digest('hex');

const state = {
  main: '',
  wav: '',
  wav3: '',
  lrc: '',
  timed: '',
};

/**
 * 逐字时间的增强 LRC：导出要求每句、每字都有时间（领域检查 MissingTime 在导出时是错误）。
 * 普通 LRC 只给句子时间，字没有时间，导出会被 422 exportInputRejected 拒绝，且不说明原因（G29）。
 */
const TIMED_LRC = '[00:00.00]<00:00.00>你<00:00.20>好<00:00.40>\n[00:00.50]<00:00.50>Se<00:00.70>cond<00:00.95>\n';

describe('W5 能力', () => {
  it('部署公布 W5 用到的接口，验证与导出可用', async () => {
    const capabilities = await client.capabilities();
    for (const feature of [
      'file-roots.entries',
      'recovery-candidates.read',
      'project-sessions.recover-jobs',
      'project-sessions.resources.read',
      'project-sessions.lyrics-export-plans',
      'project-sessions.lyrics-exports',
      'project-sessions.commands.project.rename',
      'project-sessions.commands.audio.replaceAsset',
      'project-sessions.commands.audio.clear',
      'project-sessions.commands.composition.setBackgroundAsset',
      'project-sessions.commands.composition.clearBackground',
      'project-sessions.validation-jobs',
      'project-sessions.export-jobs',
      'jobs.export-content',
    ]) {
      expect(capabilities.features, feature).toContain(feature);
    }
    const validation = await client.validationCapabilities();
    expect(validation.enabled, validation.disabledReason?.message).toBe(true);
    expect(validation.maxFramesPerOutput).toBeGreaterThan(0);
    const exports = await client.exportCapabilities();
    expect(exports.enabled, exports.disabledReason?.message).toBe(true);
    expect(exports.formats).toEqual(expect.arrayContaining(['opaqueMp4', 'transparentProRes4444']));
  });
});

describe('新建：四种组合与导入规划', () => {
  it('导入规划识别 LRC，不改任何工程', async () => {
    state.wav = (await upload(makeWav(2), `${runId}.wav`)).assetId;
    state.wav3 = (await upload(makeWav(3, 8000, 660), `${runId}-3s.wav`)).assetId;
    state.lrc = (await upload(SAMPLE_LRC, `${runId}.lrc`)).assetId;
    state.timed = (await upload(TIMED_LRC, `${runId}-timed.lrc`)).assetId;
    const plan = await client.assets.lyricsImportPlan({ assetId: state.lrc });
    expect(plan.preparationError).toBeUndefined();
    expect(plan.detectedImporterId).toBeTruthy();
    expect(plan.encodingName).toBeTruthy();
    expect(plan.previewLines.map((l) => l.text)).toEqual(['你好，协议控制台', 'Second line']);
    expect(plan.formatCandidates.length).toBeGreaterThan(0);
  });

  it('导入规划解析失败时给 preparationError，不是空成功', async () => {
    const bad = await upload(new Uint8Array(new ArrayBuffer(8)).fill(0), `${runId}-bad.lrc`);
    const plan = await client.assets.lyricsImportPlan({ assetId: bad.assetId }).catch((e: unknown) => e);
    if (plan instanceof BackendError) expect(plan.status).toBeGreaterThanOrEqual(400);
    else expect((plan as { preparationError?: unknown }).preparationError).toBeTruthy();
  });

  it('音频加歌词、只有歌词、只有音频、都没有', async () => {
    const both = await create({ name: `${runId} 两者`, audio: state.wav, lyrics: state.timed, outputDurationUs: 1_000_000 });
    expect(both.lyricsLineCount).toBe(2);
    const lines = await client.projects.allLines(both.projectSessionId, 'primary', both.projectRevision);
    const untimed = lines.flatMap((l) => [l.time, ...l.tokens.map((t) => t.time)]).filter((t) => t.startUs === undefined || t.endUs === undefined);
    expect(untimed, '主工程的每句、每字都要有时间').toEqual([]);
    expect(both.audioAssetId).toBeTruthy();
    state.main = both.projectSessionId;

    const lyricsOnly = await create({ name: `${runId} 只有歌词`, lyrics: state.lrc });
    expect(lyricsOnly.lyricsLineCount).toBe(2);
    expect(lyricsOnly.audioAssetId).toBe('');
    const waveform = await client.projects.waveform(lyricsOnly.projectSessionId);
    expect(waveform).toMatchObject({ available: false, reason: 'noAudio' });

    const audioOnly = await create({ name: `${runId} 只有音频`, audio: state.wav });
    expect(audioOnly.lyricsLineCount).toBe(0);
    expect(audioOnly.audioDurationUs).toBe(2_000_000);

    const empty = await create({ name: `${runId} 空工程` });
    expect(empty).toMatchObject({ lyricsLineCount: 0, audioAssetId: '', projectRevision: '0', isDirty: true, hasPath: false });
    const settings = await client.projects.settings(empty.projectSessionId, empty.projectRevision);
    expect(settings.outputDurationUs).toBeUndefined();
  });

  it('空工程之后可以导入歌词、换上音频', async () => {
    const empty = opened[3]!;
    await run(empty, { type: 'lyrics.importAsset', assetId: state.lrc, destination: { kind: 'existing', branchId: 'primary' } });
    const audio = await run(empty, { type: 'audio.replaceAsset', assetId: state.wav });
    expect(audio.changed).toBe(true);
    const summary = await client.projects.get(empty);
    expect(summary).toMatchObject({ lyricsLineCount: 2, audioDurationUs: 2_000_000 });
    expect(summary.audioAssetId).toBeTruthy();
  });
});

describe('改名', () => {
  it('改名一条历史；同名 no-op；空白名拒绝；撤销恢复', async () => {
    const renamed = await run(state.main, { type: 'project.rename', name: `  ${runId} 新名字  ` });
    expect(renamed.changed).toBe(true);
    expect((await settingsOf(state.main)).name).toBe(`${runId} 新名字`);
    expect((await client.projects.get(state.main)).name).toBe(`${runId} 新名字`);
    expect((await run(state.main, { type: 'project.rename', name: `${runId} 新名字` })).changed).toBe(false);
    const blank = await rejected(run(state.main, { type: 'project.rename', name: '   ' }));
    expect(blank.status).toBeGreaterThanOrEqual(400);
    await client.projects.undo(state.main);
    expect((await settingsOf(state.main)).name).toBe(`${runId} 两者`);
    await client.projects.redo(state.main);
  });
});

describe('背景媒体', () => {
  it('图片：设置、资源清单与内容、改适配、清除、撤销', async () => {
    const png = makePng();
    const asset = await upload(png, `${runId}-cover.png`);
    const set = await run(state.main, { type: 'composition.setBackgroundAsset', assetId: asset.assetId, kind: 'image', fit: 'cover' });
    expect(set.changed).toBe(true);
    const settings = await settingsOf(state.main);
    expect(settings.background).toMatchObject({ kind: 'image', fit: 'cover' });
    expect(settings.background!.resourceId).toBeTruthy();

    const resources = await resourcesOf(state.main);
    const background = resources.find((r) => r.resourceId === settings.background!.resourceId)!;
    expect(background).toMatchObject({ kind: 'media', status: 'available' });
    expect(background.roles.join(',')).toMatch(/background/i);
    expect(background.contentUrl).toBeTruthy();
    expect(background.sizeBytes).toBe(String(png.byteLength));
    const content = await client.fetchBinary(background.contentUrl!);
    expect(sha(await content.blob!.arrayBuffer())).toBe(asset.sha256);
    const audio = resources.find((r) => r.roles.some((role) => /audio/i.test(role)) && r.kind === 'media');
    expect(audio?.preparationStatus).toBe('prepared');

    expect((await run(state.main, { type: 'composition.setBackgroundFit', fit: 'contain' })).changed).toBe(true);
    expect((await settingsOf(state.main)).background?.fit).toBe('contain');
    expect((await run(state.main, { type: 'composition.setBackgroundFit', fit: 'contain' })).changed).toBe(false);

    await run(state.main, { type: 'composition.clearBackground' });
    expect((await settingsOf(state.main)).background?.kind ?? 'color').toBe('color');
    await client.projects.undo(state.main);
    expect((await settingsOf(state.main)).background?.kind).toBe('image');
  });

  it('视频：设置播放策略（本机有 ffmpeg 时）', async (context) => {
    let video: Buffer;
    const dir = mkdtempSync(join(tmpdir(), 'mako-w5-'));
    try {
      const file = join(dir, 'clip.mp4');
      execFileSync('ffmpeg', ['-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc=size=160x90:rate=10:duration=1', '-pix_fmt', 'yuv420p', file]);
      video = readFileSync(file);
    } catch {
      context.skip();
      return;
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
    const asset = await upload(new Uint8Array(video), `${runId}-clip.mp4`);
    expect(asset.contentType).toBe('video/mp4');
    await run(state.main, { type: 'composition.setBackgroundAsset', assetId: asset.assetId, kind: 'video', playback: 'loop' });
    expect((await settingsOf(state.main)).background).toMatchObject({ kind: 'video', playback: 'loop' });
    expect((await run(state.main, { type: 'composition.setBackgroundPlayback', playback: 'freezeLastFrame' })).changed).toBe(true);
    expect((await settingsOf(state.main)).background?.playback).toBe('freezeLastFrame');
    // 回到图片，后面的导出用图片背景。
    await client.projects.undo(state.main);
    await client.projects.undo(state.main);
    expect((await settingsOf(state.main)).background?.kind).toBe('image');
  });
});

describe('替换与清除音频', () => {
  it('替换后时长变化、分析清除、输出时长不变；同一资源 no-op；清除后没有音频；撤销恢复', async () => {
    const before = await settingsOf(state.main);
    const replaced = await run(state.main, { type: 'audio.replaceAsset', assetId: state.wav3 });
    expect(replaced.changed).toBe(true);
    const summary = await client.projects.get(state.main);
    expect(summary.audioDurationUs).toBe(3_000_000);
    expect((await settingsOf(state.main)).outputDurationUs).toBe(before.outputDurationUs);
    expect((await run(state.main, { type: 'audio.replaceAsset', assetId: state.wav3 })).changed).toBe(false);

    await run(state.main, { type: 'audio.clear' });
    const cleared = await client.projects.get(state.main);
    expect(cleared.audioAssetId).toBe('');
    expect((await resourcesOf(state.main)).some((r) => r.roles.some((role) => /audio/i.test(role)) && r.kind === 'media')).toBe(false);
    expect((await run(state.main, { type: 'audio.clear' })).changed).toBe(false);

    await client.projects.undo(state.main);
    await client.projects.undo(state.main);
    const restored = await client.projects.get(state.main);
    expect(restored.audioDurationUs).toBe(2_000_000);
  });
});

describe('保存位置与目录浏览', () => {
  const folder = `${runId}`;
  const first = `${folder}/first`;
  const copy = `${folder}/copy`;

  it('新工程保存到嵌套目录；目录浏览列出工程与普通目录', async () => {
    const job = await finish(await client.projects.save(state.main, { targetHandleId: await directory(first) }));
    expect(job.state, job.error?.message).toBe('succeeded');
    expect(job.result?.hasPath).toBe(true);

    const root = await client.files.listAll('projects', '');
    expect(root.find((e) => e.name === folder)).toMatchObject({ relativePath: folder, isProject: false });
    const inside = await client.files.listAll('projects', folder);
    expect(inside).toEqual([{ name: 'first', relativePath: first, isProject: true }]);
  });

  it('另存为：有路径的工程带新目标保存，之后的保存写到新位置', async () => {
    await run(state.main, { type: 'project.rename', name: `${runId} 另存` });
    const job = await finish(await client.projects.save(state.main, { targetHandleId: await directory(copy) }));
    expect(job.state, job.error?.message).toBe('succeeded');
    const inside = await client.files.listAll('projects', folder);
    expect(inside.map((e) => [e.name, e.isProject])).toEqual([
      ['copy', true],
      ['first', true],
    ]);
  });

  it('保存到已有别的工程的目录：记录后端行为', async () => {
    const other = opened[1]!;
    const handle = await directory(first).catch((e: unknown) => e);
    if (handle instanceof BackendError) {
      expect(handle.status).toBeGreaterThanOrEqual(400);
      return;
    }
    const job = await finish(await client.projects.save(other, { targetHandleId: handle as string }));
    // 只记录：成功表示覆盖，失败给出原因。界面按这个结果决定要不要先确认（W5 设计 §2.6）。
    console.log(`[W5] 保存到已有工程目录：${job.state}${job.error ? ` ${job.error.code}/${job.error.failureCode ?? ''} ${job.error.message}` : ''}`);
    expect(['succeeded', 'failed']).toContain(job.state);
  });

  it('只读打开另存的目录', async () => {
    const job = await finish(await client.projects.open({ projectDirectoryHandleId: await directory(copy, 'read'), access: 'read' }));
    expect(job.state, job.error?.message).toBe('succeeded');
    const summary = job.result!;
    opened.push(summary.projectSessionId);
    expect(summary).toMatchObject({ isReadOnly: true, name: `${runId} 另存`, hasPath: true });
    const write = await rejected(run(summary.projectSessionId, { type: 'project.rename', name: 'x' }));
    expect(write.status).toBe(403);
  });
});

describe('歌词导出', () => {
  const cases: { format: LyricsExportFormat; scope?: 'document'; marker: RegExp }[] = [
    { format: 'lrc.plain', marker: /\]你好/ },
    { format: 'lrc.enhanced', marker: /<00:00\.20>|你.*好/ },
    { format: 'ttml.project', scope: 'document', marker: /<tt[\s>]/ },
    { format: 'ttml.amll', marker: /<tt[\s>]/ },
  ];

  it.each(cases)('$format：评估、导出、下载', async ({ format, scope, marker }) => {
    const id = state.main;
    const rev = client.projects.knownRevision(id)!;
    const plan = await client.projects.lyricsExportPlan(id, rev, {
      format,
      options: { title: '测试', millisecondPrecision: true },
      ...(scope ? { scope } : { branchId: 'primary' }),
    });
    expect(plan.projectRevision).toBe(rev);
    expect(plan.scope).toBe(scope ?? 'branch');
    const exported = await client.projects.lyricsExport(id, rev, { planId: plan.planId, acceptLosses: plan.requiresLossAcceptance });
    const content = await client.lyricsExports.content(exported.exportId);
    const bytes = await content.blob!.arrayBuffer();
    expect(bytes.byteLength).toBe(exported.sizeBytes);
    expect(sha(bytes)).toBe(exported.sha256);
    expect(new TextDecoder().decode(bytes)).toMatch(marker);
    expect(content.fileName ?? '').toMatch(format.startsWith('lrc') ? /\.lrc$|^$/ : /\.ttml$|^$/);
  });

  it('有损失却不接受时拒绝，不产生文件', async () => {
    const id = state.main;
    const rev = client.projects.knownRevision(id)!;
    const plan = await client.projects.lyricsExportPlan(id, rev, { format: 'lrc.plain', options: {}, branchId: 'primary' });
    if (!plan.requiresLossAcceptance) return;
    const error = await rejected(client.projects.lyricsExport(id, rev, { planId: plan.planId, acceptLosses: false }));
    expect(error.status).toBe(422);
  });
});

describe('正式验证与导出', () => {
  it('正式验证：任务固定修订，报告与诊断可读', async () => {
    const rev = client.projects.knownRevision(state.main)!;
    const accepted = await client.projects.validate(state.main, { framesPerOutput: 3 });
    expect(accepted.kind).toBe('validation');
    const job = await finish(accepted);
    expect(job.state, job.error?.message ?? job.failureCode).toBe('succeeded');
    expect(job.input?.sourceRevision).toBe(rev);
    expect(job.validationResult).toMatchObject({ passed: true, opaqueFrames: 3, transparentFrames: 3 });
    const report = await client.jobs.validationResult(job.jobId);
    expect(report.succeeded).toBe(true);
    const page = await client.jobs.validationDiagnostics(job.jobId);
    expect(page.reportSha256).toBe(job.validationResult!.report.sha256);
  });

  it('MP4 含音轨：报告与成片一致', async () => {
    const job = await finish(await client.projects.exportVideo(state.main, { format: 'opaqueMp4', quality: 'draft', includeAudio: true }), 180_000);
    expect(job.state, job.error?.message ?? job.failureCode).toBe('succeeded');
    expect(job.exportInput).toMatchObject({ format: 'opaqueMp4', includeAudio: true, quality: 'draft' });
    const artifact = job.exportResult!.artifact;
    expect(artifact.contentType).toBe('video/mp4');
    const report = await client.jobs.exportResult(job.jobId);
    expect(isExportFailureReport(report)).toBe(false);
    if (isExportFailureReport(report)) return;
    expect(report).toMatchObject({ format: 'opaqueMp4', audioIncluded: true, width: 320, height: 180, expectedFrames: 30, strictlyVerified: true });
    const content = await client.jobs.exportContent(job.jobId);
    const bytes = await content.blob!.arrayBuffer();
    expect(bytes.byteLength).toBe(artifact.sizeBytes);
    expect(sha(bytes)).toBe(artifact.sha256);
    expect(content.fileName ?? 'MakoKara-x.mp4').toMatch(/\.mp4$/);
  });

  it('MOV 透明不含音轨', async () => {
    const job = await finish(await client.projects.exportVideo(state.main, { format: 'transparentProRes4444', includeAudio: false }), 180_000);
    expect(job.state, job.error?.message ?? job.failureCode).toBe('succeeded');
    const report = await client.jobs.exportResult(job.jobId);
    if (isExportFailureReport(report)) throw new Error(report.failureCode);
    expect(report).toMatchObject({ format: 'transparentProRes4444', audioIncluded: false, hasAlpha: true, audioCodec: null });
  });

  it('没有音频的工程：没有输出时长时拒绝；要音轨时拒绝；只导画面成功', async () => {
    const silent = (await create({ name: `${runId} 无音频导出`, lyrics: state.timed })).projectSessionId;
    const noDuration = await rejected(client.projects.exportVideo(silent, { includeAudio: false }));
    expect(noDuration.status).toBe(422);
    console.log(`[W5] 无输出时长导出：${noDuration.status} ${noDuration.code}/${noDuration.failureCode ?? ''} ${noDuration.message}`);

    await run(silent, { type: 'composition.setOutputDuration', durationUs: micros(1_000_000) });
    const withAudio = await rejected(client.projects.exportVideo(silent, { includeAudio: true }));
    expect(withAudio.status).toBe(422);
    console.log(`[W5] 无音频含音轨导出：${withAudio.status} ${withAudio.code}/${withAudio.failureCode ?? ''} ${withAudio.message}`);

    const job = await finish(await client.projects.exportVideo(silent, { includeAudio: false, quality: 'draft' }), 180_000);
    expect(job.state, job.error?.message ?? job.failureCode).toBe('succeeded');
    expect(job.exportResult?.artifact.contentType).toBe('video/mp4');
  });

  it('字没有时间的工程被拒绝，错误里没有具体原因（G29）', async () => {
    const lyricsOnly = opened[1]!;
    await run(lyricsOnly, { type: 'composition.setOutputDuration', durationUs: micros(1_000_000) });
    const error = await rejected(client.projects.exportVideo(lyricsOnly, { includeAudio: false }));
    expect(error).toMatchObject({ status: 422, code: 'exportInputRejected' });
    expect(error.field).toBeUndefined();
  });

  it('导出中途取消，等到真正的终态', async () => {
    const big = await create({ name: `${runId} 取消`, audio: state.wav, lyrics: state.timed, outputDurationUs: 20_000_000, size: [1920, 1080] });
    const accepted = await client.projects.exportVideo(big.projectSessionId, { includeAudio: false, quality: 'high' });
    expect(accepted.canCancel).toBe(true);
    const cancelling = await client.jobs.cancel(accepted.jobId);
    expect(cancelling.cancellationRequested || cancelling.state === 'cancelled').toBe(true);
    const job = await finish(accepted);
    expect(job.state).toBe('cancelled');
  });
});

describe('恢复', () => {
  // 恢复候选只列出“不在当前 Server 里打开”的会话备份（ProjectSessionRegistry.RecoveryEntries）：
  // 有修改的会话在 Server 正常关闭时写一份备份，重启后才成为候选。每 30 秒的自动备份不会让还开着的会话出现在清单里。
  it('有修改的工程在 Server 重启后出现在恢复候选里，可以恢复', { timeout: 180_000 }, async () => {
    const name = `${(await client.projects.get(state.main)).name} 恢复`;
    await run(state.main, { type: 'project.rename', name });
    expect((await client.recoveryCandidates()).candidates.some((c) => c.projectName === name)).toBe(false);

    client.disconnectEvents();
    await backend.restart();
    await client.heartbeat().catch(() => undefined);
    await client.reinitialize('测试：Server 重启后读取恢复候选');
    opened.length = 0;

    const { candidates } = await client.recoveryCandidates();
    const candidate = candidates.find((c) => c.projectName === name);
    expect(candidate, `候选：${candidates.map((c) => c.projectName).join('、')}`).toBeTruthy();
    expect(candidate!.hasOriginalPath).toBe(true);
    // 还没保存过的空工程也有候选，但没有原位置。
    expect(candidates.find((c) => c.projectName === `${runId} 空工程`)?.hasOriginalPath).toBe(false);

    const job = await finish(await client.projects.recover({ recoveryId: candidate!.recoveryId }));
    expect(job.state, job.error?.message).toBe('succeeded');
    const summary = job.result!;
    opened.push(summary.projectSessionId);
    console.log(`[W5] 恢复结果：hasPath=${summary.hasPath} isDirty=${summary.isDirty} revision=${summary.projectRevision} canUndo=${summary.canUndo}`);
    expect(summary.name).toBe(name);
    expect((await client.recoveryCandidates()).candidates.some((c) => c.recoveryId === candidate!.recoveryId)).toBe(false);
  });
});

describe('合同', () => {
  it('所有 JSON 响应符合冻结 OpenAPI（已登记的 R9 除外）', () => {
    // R9（W5 设计 §3.4）：validation-capabilities 的 sourceProfiles 里有合同枚举之外的 author-script-sample-v1。
    const knownR9 = (entry: (typeof recorder.issues)[number]) =>
      entry.exchange.startsWith('GET /api/v1/validation-capabilities') && entry.issues.every((issue) => issue.path.startsWith('$.sourceProfiles['));
    expect(recorder.issues.filter((entry) => !knownR9(entry))).toEqual([]);
    for (const route of ['/entries', '/resources', '/lyrics-export-plans', '/lyrics-exports', '/recovery-candidates', '/export-result', '/validation-result', '/lyrics-import-plans']) {
      expect([...recorder.validated].some((v) => v.includes(route)), route).toBe(true);
    }
  });
});
