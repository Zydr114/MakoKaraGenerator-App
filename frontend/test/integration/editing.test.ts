// 连真实 Server 的 W1 读模型与打轴命令（合同 1.1.0／1.2.x，规范 §6.4、§6.5；前端设计 W1 §8.1、§8.2）。
// 测试按顺序共享一个工程；每条命令后用 S1 窗口按当前修订重读核对。所有 JSON 响应用冻结 OpenAPI 校验。

import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { BackendError, MakoClient, MemoryIdentityStore } from '../../src/client';
import {
  micros,
  revision,
  type EditableLyricLine,
  type ProjectCommand,
  type ProjectCommandResponse,
  type ProjectSessionSummary,
  type TokenTarget,
} from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';
import { makeWav } from '../support/fixtures';

let backend: TestBackend;
let client: MakoClient;
const recorder = new ContractRecorder();
let project: ProjectSessionSummary;
let primaryBranchId = '';
let lines: EditableLyricLine[] = [];

const LRC = '[00:00.50]朝の光\n[00:03.00]Hello world\n[00:05.00]终\n';

beforeAll(async () => {
  backend = await startTestBackend('editing');
  client = await MakoClient.connect(backend.provider, {
    identityStore: new MemoryIdentityStore(),
    environment: null,
    onExchange: recorder.observer,
    jobPollIntervalMs: 1000,
  });
});

afterAll(async () => {
  client?.dispose();
  await backend?.stop();
});

const id = () => project.projectSessionId;
const current = () => client.projects.knownRevision(id())!;

async function reread(): Promise<EditableLyricLine[]> {
  lines = await client.projects.allLines(id(), primaryBranchId, current());
  return lines;
}

const token = (lineIndex: number, tokenIndex: number) => lines[lineIndex]!.tokens[tokenIndex]!;
const target = (lineIndex: number, tokenIndex: number): TokenTarget => ({
  branchId: primaryBranchId,
  lineId: lines[lineIndex]!.id,
  tokenId: token(lineIndex, tokenIndex).id,
});

async function run(command: ProjectCommand): Promise<ProjectCommandResponse> {
  const response = await client.projects.command(id(), command);
  await reread();
  return response;
}

async function rejected(command: ProjectCommand): Promise<BackendError> {
  try {
    await client.projects.command(id(), command);
  } catch (error) {
    if (error instanceof BackendError) return error;
    throw error;
  }
  throw new Error(`命令 ${command.type} 没有被拒绝`);
}

describe('W1 读模型（S1，真实 Server）', () => {
  it('能力公布三个读取与六个打轴命令', async () => {
    const { features } = await client.capabilities();
    for (const feature of [
      'project-sessions.lyrics.branches.read',
      'project-sessions.lyrics.window.read',
      'project-sessions.settings.read',
      'project-sessions.commands.lyrics.stampTokenStart',
      'project-sessions.commands.lyrics.stampTokenEnd',
      'project-sessions.commands.lyrics.clearTokenTime',
      'project-sessions.commands.lyrics.nudgeTokenTime',
      'project-sessions.commands.lyrics.shiftLine',
      'project-sessions.commands.lyrics.setTokenTimes',
    ]) {
      expect(features, feature).toContain(feature);
    }
  });

  it('新建（混合拆分、30 fps）后按修订 0 读来源、设置与全部行', async () => {
    const audio = await client.assets.upload(new Blob([makeWav(6)]), { fileName: '编辑测试.wav' });
    const lrc = await client.assets.upload(new Blob([LRC]), { fileName: '编辑测试.lrc' });
    project = await client.projects.create({
      name: '编辑测试',
      audioAssetId: audio.assetId,
      lyricsAssetId: lrc.assetId,
      composition: { frameRateNumerator: 30, frameRateDenominator: 1 },
      splitRule: 1,
    });
    expect(project.projectRevision).toBe('0');

    const branches = await client.projects.branches(id(), revision('0'));
    primaryBranchId = branches.primaryBranchId;
    expect(branches.branches).toHaveLength(1);
    expect(branches.branches[0]).toMatchObject({ id: primaryBranchId, role: 'primary', lineCount: 3 });

    const settings = await client.projects.settings(id(), revision('0'));
    expect(settings).toMatchObject({ frameRateNumerator: 30, frameRateDenominator: 1 });
    expect(Number(settings.audioDurationUs)).toBeGreaterThan(5_900_000);

    await reread();
    expect(lines.map((l) => l.text)).toEqual(['朝の光', 'Hello world', '终']);
    expect(lines[0]!.tokens.map((t) => t.text)).toEqual(['朝', 'の', '光']);
    expect(lines[0]!.time).toEqual({ startUs: '500000', endUs: '3000000' });
    expect(lines[0]!.tokens.every((t) => t.timingStatus === 'missing' && Object.keys(t.time).length === 0)).toBe(true);
  });

  it('过期修订读取 409，不改变工程', async () => {
    await client.projects.command(id(), { type: 'lyrics.stampTokenStart', target: target(0, 0), playheadUs: micros(600_000) });
    await expect(client.projects.lyricWindow(id(), primaryBranchId, revision('0'))).rejects.toSatisfy(
      (e) => e instanceof BackendError && e.code === 'revisionConflict',
    );
    await client.projects.undo(id());
    await reread();
    expect(token(0, 0).timingStatus).toBe('missing');
  });

  it('分页：limit=1 沿 nextOffset 读到同样的三行', async () => {
    const first = await client.projects.lyricWindow(id(), primaryBranchId, current(), { offset: 0, limit: 1 });
    expect(first).toMatchObject({ offset: 0, totalCount: 3, nextOffset: 1 });
    const last = await client.projects.lyricWindow(id(), primaryBranchId, current(), { offset: 2, limit: 1 });
    expect(last.nextOffset).toBeUndefined();
    expect(last.lines[0]!.id).toBe(lines[2]!.id);
  });
});

describe('W1 打轴命令（S2，真实 Server）', () => {
  it('起点打点：没有终点时补一帧估算终点，状态 estimated', async () => {
    const response = await run({ type: 'lyrics.stampTokenStart', target: target(0, 0), playheadUs: micros(600_000) });
    expect(response.changed).toBe(true);
    expect(response.timingResult).toEqual({ estimatedPlaceholder: true });
    expect(response.changeSet.tokenIds).toContain(token(0, 0).id);
    expect(token(0, 0).timingStatus).toBe('estimated');
    expect(token(0, 0).time.startUs).toBe('600000');
    const end = Number(token(0, 0).time.endUs);
    expect(end - 600_000).toBeGreaterThanOrEqual(33_333);
    expect(end - 600_000).toBeLessThanOrEqual(33_334);
  });

  it('终点打点：状态 edited，一次打点一条撤销', async () => {
    const response = await run({ type: 'lyrics.stampTokenEnd', target: target(0, 0), playheadUs: micros(900_000) });
    expect(response.timingResult?.estimatedPlaceholder).toBe(false);
    expect(token(0, 0)).toMatchObject({ time: { startUs: '600000', endUs: '900000' }, timingStatus: 'edited' });
    const history = await client.projects.history(id());
    expect(history.canUndo).toBe(true);
  });

  it('按帧微调：+1 帧整体后移一帧（30 fps）', async () => {
    const before = token(0, 0).time;
    await run({ type: 'lyrics.nudgeTokenTime', target: target(0, 0), frameDelta: 1 });
    const after = token(0, 0).time;
    const shift = Number(after.startUs) - Number(before.startUs);
    expect(shift).toBeGreaterThanOrEqual(33_333);
    expect(shift).toBeLessThanOrEqual(33_334);
    expect(Number(after.endUs) - Number(before.endUs)).toBe(shift);
  });

  it('微调 0 帧是 no-op：不加修订', async () => {
    const before = current();
    const response = await client.projects.command(id(), { type: 'lyrics.nudgeTokenTime', target: target(0, 0), frameDelta: 0 });
    expect(response.changed).toBe(false);
    expect(current()).toBe(before);
  });

  it('批量元素时间：一次提交两个相邻元素，一次撤销整体还原', async () => {
    const beforeFirst = token(0, 0).time;
    const response = await run({
      type: 'lyrics.setTokenTimes',
      changes: [
        { target: target(0, 0), startUs: micros(600_000), endUs: micros(800_000) },
        { target: target(0, 1), startUs: micros(800_000), endUs: micros(1_000_000) },
      ],
    });
    expect(response.changed).toBe(true);
    expect(token(0, 0).time).toEqual({ startUs: '600000', endUs: '800000' });
    expect(token(0, 1).time).toEqual({ startUs: '800000', endUs: '1000000' });
    await client.projects.undo(id());
    await reread();
    expect(token(0, 0).time).toEqual(beforeFirst);
    expect(token(0, 1).timingStatus).toBe('missing');
    await client.projects.redo(id());
    await reread();
    expect(token(0, 1).time).toEqual({ startUs: '800000', endUs: '1000000' });
  });

  it('清除元素时间：状态 missing；再清除是 no-op', async () => {
    await run({ type: 'lyrics.clearTokenTime', target: target(0, 1) });
    expect(token(0, 1).timingStatus).toBe('missing');
    expect(token(0, 1).time).toEqual({});
    const again = await client.projects.command(id(), { type: 'lyrics.clearTokenTime', target: target(0, 1) });
    expect(again.changed).toBe(false);
  });

  it('整行平移：行与已定时元素同移，未打轴元素保持缺失', async () => {
    const lineBefore = lines[0]!.time;
    const tokenBefore = token(0, 0).time;
    await run({ type: 'lyrics.shiftLine', target: { branchId: primaryBranchId, lineId: lines[0]!.id }, deltaUs: micros(100_000) });
    expect(Number(lines[0]!.time.startUs)).toBe(Number(lineBefore.startUs) + 100_000);
    expect(Number(token(0, 0).time.startUs)).toBe(Number(tokenBefore.startUs) + 100_000);
    expect(token(0, 1).time).toEqual({});
  });

  it('领域拒绝：422 commandRejected，原因在 failureCode，field 指向命令字段', async () => {
    const untimed = await rejected({ type: 'lyrics.stampTokenEnd', target: target(0, 2), playheadUs: micros(1_500_000) });
    expect(untimed).toMatchObject({ status: 422, code: 'commandRejected', failureCode: 'Untimed' });
    expect(untimed.field).toMatch(/^command\./);

    // 元素不能越出所在行（行 0 在 0.6–3.1 秒）。
    const outside = await rejected({
      type: 'lyrics.setTokenTimes',
      changes: [{ target: target(0, 2), startUs: micros(3_500_000), endUs: micros(3_600_000) }],
    });
    expect(outside).toMatchObject({ status: 422, code: 'commandRejected', failureCode: 'DomainValidationFailed' });
  });

  it('所有 JSON 响应符合冻结 OpenAPI', () => {
    expect(recorder.issues).toEqual([]);
    for (const route of ['/lyrics/branches', '/lyrics/window', '/settings']) {
      expect([...recorder.validated].some((v) => v.includes(route)), route).toBe(true);
    }
  });
});
