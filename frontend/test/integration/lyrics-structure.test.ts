// 连真实 Server 的 W2 歌词结构命令（S3a～S3d，合同 1.3.0～1.6.0，规范 §6.6～§6.9）与工程设置、音频分析（§12）。
// 用 EditorSession（不依赖 React）驱动：命令 → 整体重读 → 应用建议选择，与页面走同一条路径。
// 测试按顺序共享一个工程；所有 JSON 响应用冻结 OpenAPI 校验。

import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { MakoClient, MemoryIdentityStore } from '../../src/client';
import { EditorSession } from '../../src/editor/session/editor-session';
import { micros, type ProjectCommand, type ProjectCommandResponse } from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';
import { makeWav } from '../support/fixtures';

let backend: TestBackend;
let client: MakoClient;
const recorder = new ContractRecorder();
let session: EditorSession;
let primary = '';

const LRC = '[00:00.50]朝の光\n[00:03.00]Hello world\n[00:05.00]终\n';

beforeAll(async () => {
  backend = await startTestBackend('structure');
  client = await MakoClient.connect(backend.provider, {
    identityStore: new MemoryIdentityStore(),
    environment: null,
    onExchange: recorder.observer,
    jobPollIntervalMs: 500,
  });
  const audio = await client.assets.upload(new Blob([makeWav(8)]), { fileName: '结构测试.wav' });
  const lrc = await client.assets.upload(new Blob([LRC]), { fileName: '结构测试.lrc' });
  const project = await client.projects.create({
    name: '结构测试',
    audioAssetId: audio.assetId,
    lyricsAssetId: lrc.assetId,
    composition: { frameRateNumerator: 30, frameRateDenominator: 1 },
    splitRule: 1,
  });
  session = await EditorSession.attach(client, project.projectSessionId);
  primary = session.state.primaryBranchId!;
});

afterAll(async () => {
  session?.dispose();
  client?.dispose();
  await backend?.stop();
});

/** 等命令返回、重读完成、建议选择应用完（建议选择在重读完成后的微任务里应用）。 */
async function settle(): Promise<void> {
  for (let i = 0; i < 500; i++) {
    const s = session.state;
    if (!s.syncing && s.pendingCommands === 0 && s.linesRevision === s.summary.projectRevision) break;
    await new Promise((r) => setTimeout(r, 10));
  }
  await new Promise((r) => setTimeout(r, 0));
}

async function run(command: ProjectCommand): Promise<ProjectCommandResponse> {
  const response = await session.command(command);
  await settle();
  if (!response) throw new Error(`${command.type} 被拒绝：${session.state.notice?.message}`);
  return response;
}

/** 期望被拒绝：返回会话给出的中文提示。 */
async function rejected(command: ProjectCommand): Promise<string> {
  const before = session.state.summary.projectRevision;
  const response = await session.command(command);
  await settle();
  expect(response, `${command.type} 应被拒绝`).toBeUndefined();
  expect(session.state.summary.projectRevision).toBe(before);
  return session.state.notice?.message ?? '';
}

async function undo(times = 1): Promise<void> {
  for (let i = 0; i < times; i++) await session.undo();
  await settle();
}

const texts = (branchId = session.state.branchId) => session.linesOf(branchId).map((l) => l.text);
const lineAt = (index: number, branchId = primary) => session.linesOf(branchId)[index]!;
const lineTarget = (index: number, branchId = primary) => ({ branchId, lineId: lineAt(index, branchId).id });
const tokenTarget = (lineIndex: number, tokenIndex: number, branchId = primary) => {
  const line = lineAt(lineIndex, branchId);
  return { branchId, lineId: line.id, tokenId: line.tokens[tokenIndex]!.id };
};

describe('S3a 行操作、正文与注音（真实 Server）', () => {
  it('新建后读到主来源三行，混合拆分', () => {
    expect(session.state.readModel).toBe('editable');
    expect(texts()).toEqual(['朝の光', 'Hello world', '终']);
    expect(lineAt(0).tokens.map((t) => t.text)).toEqual(['朝', 'の', '光']);
  });

  it('在第 1 行下方插入：feedback 给新行 ID，重读后建议选择落到新行', async () => {
    const response = await run({ type: 'lyrics.addLine', branchId: primary, afterLineId: lineAt(0).id });
    expect(response.feedback?.createdLineId).toBeTruthy();
    expect(texts()).toEqual(['朝の光', '', 'Hello world', '终']);
    expect(session.state.selection?.lineId).toBe(response.feedback!.createdLineId);
  });

  it('改正文：新行按混合规则拆出文本单元；再改为相同正文是 no-op', async () => {
    const response = await run({ type: 'lyrics.setLineText', target: lineTarget(1), text: '新しい行' });
    expect(response.changed).toBe(true);
    expect(lineAt(1).text).toBe('新しい行');
    expect(lineAt(1).tokens.map((t) => t.text).join('')).toBe('新しい行');
    const again = await session.command({ type: 'lyrics.setLineText', target: lineTarget(1), text: '新しい行' });
    await settle();
    expect(again?.changed).toBe(false);
    expect(session.state.notice?.message).toBe('没有变化。');
  });

  it('注音：「朝」加上「あさ」，重读后在行的 ruby 里', async () => {
    await run({ type: 'lyrics.setRubyText', target: tokenTarget(0, 0), text: 'あさ' });
    const ruby = lineAt(0).ruby;
    expect(ruby).toHaveLength(1);
    expect(ruby[0]).toMatchObject({ baseTokenId: lineAt(0).tokens[0]!.id, text: 'あさ' });
  });

  it('复制行：副本在下方并被选中；删除副本后选择移开；两步都可撤销', async () => {
    const copy = await run({ type: 'lyrics.duplicateLine', target: lineTarget(0) });
    expect(texts()).toEqual(['朝の光', '朝の光', '新しい行', 'Hello world', '终']);
    expect(session.state.selection?.lineId).toBe(copy.feedback?.createdLineId);
    const removed = await run({ type: 'lyrics.removeLine', target: lineTarget(1) });
    expect(removed.feedback?.deletedLineId).toBe(copy.feedback?.createdLineId);
    expect(texts()).toEqual(['朝の光', '新しい行', 'Hello world', '终']);
    expect(session.state.selection?.lineId).not.toBe(copy.feedback?.createdLineId);
    await undo();
    expect(texts()).toEqual(['朝の光', '朝の光', '新しい行', 'Hello world', '终']);
    await session.redo();
    await settle();
    expect(texts()).toEqual(['朝の光', '新しい行', 'Hello world', '终']);
  });

  it('领域拒绝：目标行不存在时不改修订，会话给出中文提示', async () => {
    const message = await rejected({ type: 'lyrics.setLineText', target: { branchId: primary, lineId: 'line-does-not-exist' }, text: 'x' });
    expect(message).toMatch(/[一-鿿]/);
    expect(message).not.toBe('');
  });
});

describe('工程设置（S3a composition，真实 Server）', () => {
  it('画布、帧率、背景色、输出时长逐项修改，设置读取跟上', async () => {
    await run({ type: 'composition.setCanvasSize', width: 1280, height: 720 });
    await run({ type: 'composition.setFrameRate', numerator: 25, denominator: 1 });
    await run({ type: 'composition.setBackgroundColor', red: 16, green: 32, blue: 48, alpha: 255 });
    expect(session.state.settings).toMatchObject({ canvasWidth: 1280, canvasHeight: 720, frameRateNumerator: 25, frameRateDenominator: 1, backgroundRed: 16, backgroundGreen: 32, backgroundBlue: 48 });
    expect(session.frameRate).toEqual({ numerator: 25, denominator: 1 });

    expect(session.state.settings?.outputDurationUs).toBeUndefined();
    await run({ type: 'composition.useAudioDuration' });
    expect(session.state.settings?.outputDurationUs).toBe(session.state.settings?.audioDurationUs);
    await run({ type: 'composition.setOutputDuration', durationUs: micros(5_500_000) });
    expect(session.state.settings?.outputDurationUs).toBe('5500000');
    await run({ type: 'composition.setOutputDuration', durationUs: null });
    expect(session.state.settings?.outputDurationUs).toBeUndefined();
  });

  it('按歌词末尾：取当时最后一行的结束时间（LRC 行级时间推定出来的）', async () => {
    const response = await run({ type: 'composition.useLyricsEnd' });
    expect(response.changed).toBe(true);
    const end = Math.max(...session.linesOf(primary).map((l) => l.endUs ?? 0));
    expect(Number(session.state.settings?.outputDurationUs)).toBe(end);
  });

  it('非法画布尺寸被拒绝，提示用中文', async () => {
    const message = await rejected({ type: 'composition.setCanvasSize', width: 0, height: 720 });
    expect(message).toMatch(/[一-鿿]/);
  });

  it('七步设置逐一撤销后回到新建时的值', async () => {
    await undo(7);
    expect(session.state.settings).toMatchObject({ canvasWidth: 1920, canvasHeight: 1080, frameRateNumerator: 30, frameRateDenominator: 1 });
    expect(session.state.settings?.outputDurationUs).toBeUndefined();
  });

  it('音频分析：建任务、等成功终态、采用；清除；都可撤销', async () => {
    const stages: string[] = [];
    const applied = await session.analyzeAudio((progress) => progress && stages.push(progress.stage));
    await settle();
    expect(applied?.changed).toBe(true);
    expect(session.state.summary.audioAnalysis?.status).toBe('available');
    await run({ type: 'audio.analysis.clear' });
    expect(session.state.summary.audioAnalysis?.status).not.toBe('available');
    await undo(2);
    expect(session.state.summary.audioAnalysis?.status).not.toBe('available');
  });
});

describe('S3b 来源与翻译关联（真实 Server）', () => {
  let translation = '';

  it('添加翻译来源：branchResult 建议切到新来源', async () => {
    const response = await run({ type: 'lyrics.addTranslationBranch', name: 'English', language: 'en' });
    translation = response.branchResult!.selectedBranchId;
    expect(session.state.branchId).toBe(translation);
    expect(session.state.branches).toHaveLength(2);
    expect(session.state.branches?.find((b) => b.id === translation)).toMatchObject({ role: 'translation', language: 'en', lineCount: 0 });
  });

  it('粘贴追加到翻译来源：跳过空白行，importResult 建议选中新加入的第一行', async () => {
    const response = await run({ type: 'lyrics.appendText', text: 'Morning light\n\nA new line\n', destination: { kind: 'existing', branchId: translation } });
    expect(response.importResult?.lineCount).toBe(2);
    expect(texts(translation)).toEqual(['Morning light', 'A new line']);
    expect(session.state.selection?.lineId).toBe(lineAt(0, translation).id);
  });

  it('关联主歌词：翻译行带 primaryLineId；改名、改时间策略；解除关联', async () => {
    await run({ type: 'lyrics.linkPrimaryLine', branchId: translation, lineId: lineAt(0, translation).id, primaryLineId: lineAt(0).id });
    await run({ type: 'lyrics.linkPrimaryLine', branchId: translation, lineId: lineAt(1, translation).id, primaryLineId: lineAt(1).id });
    expect(lineAt(0, translation).primaryLineId).toBe(lineAt(0).id);
    expect(lineAt(1, translation).primaryLineId).toBe(lineAt(1).id);
    await run({ type: 'lyrics.configureBranch', branchId: translation, name: '英文', language: 'en', role: 'translation', timingMode: 'followPrimary' });
    expect(session.state.branches?.find((b) => b.id === translation)).toMatchObject({ name: '英文', timingMode: 'followPrimary' });
    await run({ type: 'lyrics.linkPrimaryLine', branchId: translation, lineId: lineAt(1, translation).id, primaryLineId: null });
    expect(lineAt(1, translation).primaryLineId).toBeUndefined();
  });

  it('主来源不能删除；删除翻译来源后回到主来源，撤销后来源与关联都恢复', async () => {
    expect(await rejected({ type: 'lyrics.removeBranch', branchId: primary })).not.toBe('');
    const response = await run({ type: 'lyrics.removeBranch', branchId: translation });
    expect(response.branchResult?.selectedBranchId).toBe(primary);
    expect(session.state.branchId).toBe(primary);
    expect(session.state.branches).toHaveLength(1);
    await undo();
    expect(session.state.branches).toHaveLength(2);
    expect(lineAt(0, translation).primaryLineId).toBe(lineAt(0).id);
  });
});

describe('S3c 文本单元结构（真实 Server）', () => {
  it('合并「朝」「の」，再拆开回到逐字（规范 §6.8：未打轴时拆开是 no-op）', async () => {
    session.selectBranch(primary);
    const merged = await run({ type: 'lyrics.mergeToken', target: tokenTarget(0, 0) });
    expect(merged.tokenResult?.changedLineCount).toBe(1);
    expect(lineAt(0).tokens.map((t) => t.text)).toEqual(['朝の', '光']);
    const untimed = await session.command({ type: 'lyrics.splitToken', target: tokenTarget(0, 0) });
    await settle();
    expect(untimed?.changed).toBe(false);
    await run({ type: 'lyrics.setTokenTimes', changes: [{ target: tokenTarget(0, 0), startUs: micros(500_000), endUs: micros(1_100_000) }] });
    const split = await run({ type: 'lyrics.splitToken', target: tokenTarget(0, 0) });
    expect(split.tokenResult?.createdTokenCount).toBeGreaterThan(0);
    expect(lineAt(0).tokens.map((t) => t.text)).toEqual(['朝', 'の', '光']);
  });

  it('拖动排序：把「光」移到最前，元素保留 ID，正文按新顺序拼接；撤销还原', async () => {
    const light = lineAt(0).tokens[2]!.id;
    const response = await run({ type: 'lyrics.reorderToken', target: tokenTarget(0, 2), destinationIndex: 0, reassignTimeSlots: false });
    expect(response.tokenResult?.changedLineCount).toBe(1);
    expect(lineAt(0).tokens.map((t) => t.text)).toEqual(['光', '朝', 'の']);
    expect(lineAt(0).tokens[0]!.id).toBe(light);
    expect(lineAt(0).text).toBe('光朝の');
    await undo();
    expect(lineAt(0).tokens.map((t) => t.text)).toEqual(['朝', 'の', '光']);
  });

  it('按切分重切本行；切分拼不回原文时拒绝；按规则重切整个来源', async () => {
    const hello = session.linesOf(primary).findIndex((l) => l.text === 'Hello world');
    await run({ type: 'lyrics.resplitLine', target: lineTarget(hello), segments: ['Hello', ' world'], estimateTimes: false });
    expect(lineAt(hello).tokens.map((t) => t.text)).toEqual(['Hello', ' world']);
    expect(await rejected({ type: 'lyrics.resplitLine', target: lineTarget(hello), segments: ['Hello', 'world'], estimateTimes: false })).not.toBe('');
    const branch = await run({ type: 'lyrics.resplitBranch', branchId: primary, rule: 'perTextElement', estimateTimes: false });
    expect(branch.tokenResult?.changedLineCount).toBeGreaterThan(0);
    expect(lineAt(hello).tokens).toHaveLength('Hello world'.length);
    await undo(2);
    expect(lineAt(hello).tokens.map((t) => t.text).join('|')).not.toBe([...'Hello world'].join('|'));
  });
});

describe('S3d 导入与粘贴（真实 Server）', () => {
  it('导入 LRC 为新的翻译来源：报告给出格式与行数，建议切到新来源并选中第一行', async () => {
    const asset = await client.assets.upload(new Blob(['[00:00.50]Morning\n[00:03.00]Hello again\n']), { fileName: '翻译.lrc' });
    const response = await run({
      type: 'lyrics.importAsset',
      assetId: asset.assetId,
      destination: { kind: 'newBranch', name: '导入的翻译', role: 'translation' },
      options: { splitRule: 'latinWords' },
    });
    const result = response.importResult!;
    expect(result.lineCount).toBe(2);
    // 末尾换行后的空串也算一行源文件（与导入报告的“源文件 N 行”一致）。
    expect(result.report).toMatchObject({ importerId: 'lrc.plain', sourceLineCount: 3, lineCount: 2, skippedLineCount: 0 });
    expect(session.state.branchId).toBe(result.selectedBranchId);
    expect(session.state.selection?.lineId).toBe(result.selectedLineId);
    expect(texts(result.selectedBranchId)).toEqual(['Morning', 'Hello again']);
    expect(lineAt(1, result.selectedBranchId).tokens.map((t) => t.text).join('|')).toContain('Hello');
  });

  it('导入替换现有来源：行全部换新，一次撤销整体还原', async () => {
    const target = session.state.branchId;
    const asset = await client.assets.upload(new Blob(['[00:01.00]Replaced\n']), { fileName: '替换.lrc' });
    await run({ type: 'lyrics.importAsset', assetId: asset.assetId, destination: { kind: 'existing', branchId: target } });
    expect(texts(target)).toEqual(['Replaced']);
    await undo();
    expect(texts(target)).toEqual(['Morning', 'Hello again']);
  });

  it('粘贴为新翻译来源；空文本被拒绝', async () => {
    const response = await run({ type: 'lyrics.appendText', text: '第一行\n第二行', destination: { kind: 'newTranslation', name: '粘贴' } });
    expect(response.importResult?.lineCount).toBe(2);
    expect(session.state.branches?.map((b) => b.name)).toContain('粘贴');
    expect(texts(response.importResult!.selectedBranchId)).toEqual(['第一行', '第二行']);
    expect(await rejected({ type: 'lyrics.appendText', text: '\n  \n', destination: { kind: 'existing', branchId: primary } })).not.toBe('');
  });
});

describe('合同', () => {
  it('所有 JSON 响应符合冻结 OpenAPI', () => {
    expect(recorder.issues).toEqual([]);
  });
});
