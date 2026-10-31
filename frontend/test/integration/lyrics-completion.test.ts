// 连真实 Server 的 W6 歌词页补全（W6 设计 §2、§4 第 7 步）：applyEdits 的一条历史与分批（G13、G14）、
// 注音片段命令与读模型、“TTML（整个工程）”导出后新建恢复全部来源、再导入时选来源。
// 用 EditorSession（不依赖 React）驱动，与页面走同一条路径；测试按顺序共享一个工程；所有 JSON 响应用冻结 OpenAPI 校验。

import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { MakoClient, MemoryIdentityStore } from '../../src/client';
import { primaryImportBlock, planNotes } from '../../src/editor/model/lyrics-import';
import { rubyOf, splitEdits } from '../../src/editor/model/ruby-segments';
import { EditorSession } from '../../src/editor/session/editor-session';
import { micros, type ProjectCommand, type ProjectCommandResponse, type ProjectEditCommand } from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';
import { makeWav } from '../support/fixtures';

let backend: TestBackend;
let client: MakoClient;
const recorder = new ContractRecorder();
let session: EditorSession;
let primary = '';
let translation = '';
const opened: EditorSession[] = [];

const LRC = '[00:00.20]<00:00.20>朝<00:00.60>の<00:01.00>光<00:01.40>\n[00:01.60]<00:01.60>風<00:02.20>声<00:03.00>\n[00:03.20]<00:03.20>三<00:03.60>行<00:04.00>\n';

async function connect(): Promise<MakoClient> {
  return MakoClient.connect(backend.provider, { identityStore: new MemoryIdentityStore(), environment: null, onExchange: recorder.observer, jobPollIntervalMs: 500 });
}

/** 用一份歌词资源新建工程（8 秒音频，30 fps）并接上编辑会话。 */
async function createProject(name: string, lyricsAssetId: string): Promise<EditorSession> {
  const audio = await client.assets.upload(new Blob([makeWav(8)]), { fileName: `${name}.wav` });
  const project = await client.projects.create({
    name,
    audioAssetId: audio.assetId,
    lyricsAssetId,
    composition: { frameRateNumerator: 30, frameRateDenominator: 1 },
  });
  const created = await EditorSession.attach(client, project.projectSessionId);
  opened.push(created);
  return created;
}

beforeAll(async () => {
  backend = await startTestBackend('lyrics-completion');
  client = await connect();
  const lrc = await client.assets.upload(new Blob([LRC]), { fileName: '补全测试.lrc' });
  session = await createProject('补全测试', lrc.assetId);
  primary = session.state.primaryBranchId!;
});

afterAll(async () => {
  for (const s of opened) s.dispose();
  client?.dispose();
  await backend?.stop();
});

/** 等命令返回、重读完成、建议选择应用完（建议选择在重读完成后的微任务里应用）。 */
async function settle(s = session): Promise<void> {
  for (let i = 0; i < 1000; i++) {
    const state = s.state;
    if (!state.syncing && state.pendingCommands === 0 && state.linesRevision === state.summary.projectRevision) break;
    await new Promise((r) => setTimeout(r, 10));
  }
  await new Promise((r) => setTimeout(r, 0));
}

async function run(command: ProjectCommand, s = session): Promise<ProjectCommandResponse> {
  const response = await s.command(command);
  await settle(s);
  if (!response) throw new Error(`${command.type} 被拒绝：${s.state.notice?.message}`);
  return response;
}

async function applyEdits(edits: readonly ProjectEditCommand[], s = session) {
  const result = await s.applyEdits(edits);
  await settle(s);
  return result;
}

/** 期望被拒绝：工程修订不变。 */
async function rejected(command: ProjectCommand): Promise<void> {
  const before = session.state.summary.projectRevision;
  const response = await session.command(command);
  await settle();
  expect(response, `${command.type} 应被拒绝`).toBeUndefined();
  expect(session.state.summary.projectRevision).toBe(before);
}

async function undo(times = 1, s = session): Promise<void> {
  for (let i = 0; i < times; i++) await s.undo();
  await settle(s);
}

const undoCount = async (s = session) => (await client.projects.history(s.state.summary.projectSessionId)).undoCount;
const lines = (branchId = primary, s = session) => s.linesOf(branchId);
const texts = (branchId = primary, s = session) => lines(branchId, s).map((l) => l.text);

describe('一条历史的批量操作（G13、G14）', () => {
  it('多选删除三行：一次 applyEdits、一条历史；一次撤销全部恢复，行 ID 不变', async () => {
    await run({ type: 'lyrics.appendText', text: '甲\n乙\n丙\n', destination: { kind: 'existing', branchId: primary } });
    const added = lines().slice(3);
    expect(added.map((l) => l.text)).toEqual(['甲', '乙', '丙']);
    const before = await undoCount();
    const result = await applyEdits(added.map((l) => ({ type: 'lyrics.removeLine', target: { branchId: primary, lineId: l.id } })));
    expect(result).toEqual({ applied: 3, batches: 1, total: 1 });
    expect(texts()).toEqual(['朝の光', '風声', '三行']);
    expect(await undoCount()).toBe(before + 1);
    await undo();
    expect(lines().slice(3).map((l) => l.id)).toEqual(added.map((l) => l.id));
    await undo(); // 撤销追加，回到三行
    expect(texts()).toEqual(['朝の光', '風声', '三行']);
  });

  it('翻译关联：三处修改一次 applyEdits，一次撤销全部还原', async () => {
    const response = await run({ type: 'lyrics.appendText', text: '晨光\n风声\n三行\n', destination: { kind: 'newTranslation', name: '中文' } });
    translation = response.branchResult?.selectedBranchId ?? session.state.branches!.find((b) => b.role === 'translation')!.id;
    const links = lines(translation).map((l, i) => ({ type: 'lyrics.linkPrimaryLine' as const, branchId: translation, lineId: l.id, primaryLineId: lines()[i]!.id }));
    const before = await undoCount();
    await applyEdits(links);
    expect(lines(translation).map((l) => l.primaryLineId)).toEqual(lines().map((l) => l.id));
    expect(await undoCount()).toBe(before + 1);
    await undo();
    expect(lines(translation).every((l) => l.primaryLineId === undefined)).toBe(true);
    await session.redo();
    await settle();
    expect(lines(translation).map((l) => l.primaryLineId)).toEqual(lines().map((l) => l.id));
  });

  it('超过 200 条时按 200 条分批：每批一条历史', async () => {
    const big = Array.from({ length: 260 }, (_, i) => `[00:${String(Math.floor(i / 60)).padStart(2, '0')}.${String(i % 60).padStart(2, '0')}]第${i}行`).join('\n');
    const asset = await client.assets.upload(new Blob([big]), { fileName: '分批测试.lrc' });
    const s = await createProject('分批测试', asset.assetId);
    const id = s.state.primaryBranchId!;
    expect(lines(id, s)).toHaveLength(260);
    const before = await undoCount(s);
    const result = await applyEdits(
      lines(id, s)
        .slice(0, 250)
        .map((l) => ({ type: 'lyrics.removeLine', target: { branchId: id, lineId: l.id } })),
      s,
    );
    expect(result).toEqual({ applied: 250, batches: 2, total: 2 });
    expect(lines(id, s)).toHaveLength(10);
    expect(await undoCount(s)).toBe(before + 2);
    await undo(2, s);
    expect(lines(id, s)).toHaveLength(260);
  });
});

describe('注音片段（W6 设计 §2.4、§2.5）', () => {
  const asa = () => lines()[0]!;
  const token = () => asa().tokens[0]!;
  const ruby = () => rubyOf(asa(), token().id)!;
  const group = () => ({ branchId: primary, lineId: asa().id, tokenId: token().id, rubyId: ruby().id });

  it('整段输入注音，按字拆开是一条历史，建议选中原片段', async () => {
    await run({ type: 'lyrics.setRubyText', target: { branchId: primary, lineId: asa().id, tokenId: token().id }, text: 'あさ' });
    const original = ruby().segments[0]!.id;
    expect(ruby().segments.map((s) => s.text)).toEqual(['あさ']);
    const before = await undoCount();
    await applyEdits(splitEdits(asa(), ruby()));
    expect(ruby().segments.map((s) => s.text)).toEqual(['あ', 'さ']);
    expect(ruby().segments[0]!.id).toBe(original);
    expect(await undoCount()).toBe(before + 1);
    expect(session.state.selection).toEqual({ lineId: asa().id, tokenId: token().id, segmentId: original });
  });

  it('多片段时整段改字被拒绝（要逐个片段修改）', async () => {
    await rejected({ type: 'lyrics.setRubyText', target: { branchId: primary, lineId: asa().id, tokenId: token().id }, text: 'あした' });
  });

  it('加片段建议选中新片段；左右移给出完整顺序；删除片段建议选中相邻片段', async () => {
    const [a, sa] = ruby().segments;
    await run({ type: 'lyrics.addRubySegment', target: group(), text: 'ひ', afterSegmentId: sa!.id });
    const added = ruby().segments[2]!;
    expect(added.text).toBe('ひ');
    expect(session.state.selection?.segmentId).toBe(added.id);
    await run({ type: 'lyrics.reorderRubySegments', target: group(), orderedSegmentIds: [added.id, a!.id, sa!.id] });
    expect(ruby().segments.map((s) => s.text)).toEqual(['ひ', 'あ', 'さ']);
    await run({ type: 'lyrics.removeRubySegment', target: { ...group(), segmentId: added.id } });
    expect(ruby().segments.map((s) => s.text)).toEqual(['あ', 'さ']);
    expect(session.state.selection?.segmentId).toBe(a!.id);
  });

  it('片段时间：一次改两个片段、读模型带时间与状态，可以超出文本单元的范围；清除后回到没有时间', async () => {
    const [a, sa] = ruby().segments;
    const before = await undoCount();
    await run({
      type: 'lyrics.setRubySegmentTimes',
      changes: [
        { target: { ...group(), segmentId: a!.id }, startUs: micros(200_000), endUs: micros(400_000) },
        { target: { ...group(), segmentId: sa!.id }, startUs: micros(400_000), endUs: micros(700_000) }, // 「朝」到 0.6 秒
      ],
    });
    expect(await undoCount()).toBe(before + 1);
    expect(ruby().segments.map((s) => [s.time.startUs, s.time.endUs, s.timingStatus])).toEqual([
      ['200000', '400000', 'edited'],
      ['400000', '700000', 'edited'],
    ]);
    await run({ type: 'lyrics.clearRubySegmentTime', target: { ...group(), segmentId: sa!.id } });
    expect(ruby().segments[1]!.time).toEqual({});
    await undo();
    expect(ruby().segments[1]!.time).toEqual({ startUs: '400000', endUs: '700000' });
  });
});

describe('TTML（整个工程）：导出后新建、再导入选来源（W6 设计 §2.3）', () => {
  let ttmlAssetId = '';

  it('导出后新建工程：识别结果写明恢复全部来源；注音片段、时间与翻译关联都恢复', async () => {
    const id = session.state.summary.projectSessionId;
    const rev = client.projects.knownRevision(id)!;
    const plan = await client.projects.lyricsExportPlan(id, rev, { format: 'ttml.project', scope: 'document', options: {} });
    const exported = await client.projects.lyricsExport(id, rev, { planId: plan.planId, acceptLosses: plan.requiresLossAcceptance });
    const content = await client.lyricsExports.content(exported.exportId);
    const asset = await client.assets.upload(content.blob!, { fileName: '整个工程.ttml' });
    ttmlAssetId = asset.assetId;

    const importPlan = await client.assets.lyricsImportPlan({ assetId: ttmlAssetId });
    expect(importPlan.sourceBranches.map((b) => b.id)).toEqual([primary, translation]);
    expect(planNotes(importPlan, 'create')).toContain('这是 MakoKara 工程 TTML，新建时会恢复全部 2 个来源。');

    const restored = await createProject('从工程 TTML 新建', ttmlAssetId);
    expect(restored.state.branches?.map((b) => [b.id, b.name, b.role])).toEqual([
      [primary, '主歌词', 'primary'],
      [translation, '中文', 'translation'],
    ]);
    const first = lines(primary, restored)[0]!;
    expect(first.ruby[0]!.segments.map((s) => [s.text, s.time.startUs, s.time.endUs])).toEqual([
      ['あ', '200000', '400000'],
      ['さ', '400000', '700000'],
    ]);
    expect(lines(translation, restored).map((l) => l.primaryLineId)).toEqual(lines(primary, restored).map((l) => l.id));
  });

  it('再导入：规划按所选来源预览；翻译来源不能导入到主歌词；作为新来源导入只导入那个来源，一次撤销', async () => {
    const plan = await client.assets.lyricsImportPlan({ assetId: ttmlAssetId, options: { sourceBranchId: translation } });
    expect(plan.selectedSourceBranchId).toBe(translation);
    expect(plan.previewLines.map((l) => l.text)).toEqual(['晨光', '风声', '三行']);
    expect(planNotes(plan, 'import')).toContain('这是 MakoKara 工程 TTML，有 2 个来源，一次导入一个。请在下面选择要导入的来源。');
    expect(primaryImportBlock(plan, true)).toMatch(/不能导入到主歌词/);
    expect(primaryImportBlock(plan, false)).toBeUndefined();

    await rejected({ type: 'lyrics.importAsset', assetId: ttmlAssetId, destination: { kind: 'newBranch', name: '再导入', role: 'translation' } });
    await rejected({ type: 'lyrics.importAsset', assetId: ttmlAssetId, destination: { kind: 'existing', branchId: primary }, options: { sourceBranchId: translation } });

    const before = session.state.branches!.length;
    const response = await run({
      type: 'lyrics.importAsset',
      assetId: ttmlAssetId,
      destination: { kind: 'newBranch', name: '中文（再导入）', role: 'translation' },
      options: { sourceBranchId: translation },
    });
    const imported = response.importResult!.selectedBranchId;
    expect(session.state.branches).toHaveLength(before + 1);
    expect(texts(imported)).toEqual(['晨光', '风声', '三行']);
    await undo();
    expect(session.state.branches).toHaveLength(before);
  });
});

describe('契约', () => {
  it('所有 JSON 响应符合冻结 OpenAPI', () => {
    expect(recorder.issues).toEqual([]);
  });
});
