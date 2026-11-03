// 合同 1.42.0 的真实 HTTP 消费：模板分配、三态继承、来源、帧 SHA 与严格版本门禁。
// 每次运行生成独立 profile；旧格式样本仅从本次保存的工程复制，所有 202 均等待终态。
import { createHash } from 'node:crypto';
import { cp, readFile, readdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { MakoClient, MemoryIdentityStore } from '../../src/client';
import { CONTRACT_PRODUCT_VERSION, micros, type JobResponse, type PresetApplicationTarget, type ProjectCommand, type ProjectSessionSummary, type StaticTemplateDefinition, type StyleGlow } from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';
import { makeWav } from '../support/fixtures';

let projectsRoot = '';
const recorder = new ContractRecorder();
const opened = new Set<string>();
const size = { width: 320, height: 180 };
const glow: StyleGlow = { color: { red: 80, green: 180, blue: 255, alpha: 255 }, opacity: 0.75, blurRadiusPx: 8, spreadPx: 2 };
const leafGlow: StyleGlow = { color: { red: 255, green: 80, blue: 60, alpha: 255 }, opacity: 0.5, blurRadiusPx: 4, spreadPx: 3 };
let backend: TestBackend;
let client: MakoClient;
let project: ProjectSessionSummary;
let branchId = '';
let lineId = '';
let fullDefinition: StaticTemplateDefinition;
let fullTemplateId = '';
let fullApplicationId = '';
let leafApplicationId = '';
let unlitFrame = '';
let glowFrame = '';
const id = () => project.projectSessionId;
const current = () => client.projects.knownRevision(id())!;
const target = () => ({ kind: 'line' as const, branchId, lineId });
const command = (value: ProjectCommand) => client.projects.command(id(), value);
const style = () => client.projects.style(id(), target(), current());
const resolved = () => client.projects.resolvedStyle(id(), current(), { branchId, lineId });
const finish = (accepted: JobResponse) => client.jobs.waitForTerminal(accepted.jobId, { timeoutMs: 120_000 });
const handle = async (relativePath: string) => (await client.files.createHandle({ rootGrantId: 'projects', relativePath, access: 'readWrite' })).handleId;
const sha = (bytes: Uint8Array) => createHash('sha256').update(bytes).digest('hex');

beforeAll(async () => {
  backend = await startTestBackend('glow');
  projectsRoot = join(backend.profile, 'projects');
  client = await MakoClient.connect(backend.provider, {
    identityStore: new MemoryIdentityStore(), environment: null, onExchange: recorder.observer, jobPollIntervalMs: 250,
  });
  const runtime = await client.preview.runtimeInfo();
  expect(runtime.enabled, runtime.disabledReason?.message).toBe(true);
  const audio = await client.assets.upload(new Blob([makeWav(4)]), { fileName: 'glow-it.wav' });
  const lyrics = await client.assets.upload(new Blob(['[00:00.00]辉光\n[00:02.00]Glow\n']), { fileName: 'glow-it.lrc' });
  project = await client.projects.create({
    name: '辉光 HTTP 集成', audioAssetId: audio.assetId, lyricsAssetId: lyrics.assetId,
    composition: { canvasWidth: 320, canvasHeight: 180, frameRateNumerator: 30, frameRateDenominator: 1 }, splitRule: 1,
  });
  opened.add(id());
  branchId = (await client.projects.branches(id(), current())).primaryBranchId;
  lineId = (await client.projects.allLines(id(), branchId, current()))[0]!.id;
});

afterAll(async () => {
  if (client) {
    for (const sessionId of opened) await client.projects.close(sessionId, { policy: 'discard' }).then(finish).catch(() => undefined);
    client.dispose();
  }
  await backend?.stop();
});

async function still(): Promise<string> {
  // renderStill 等待帧 ready，并验证实际下载 PNG 的长度和 SHA，再关闭预览。
  const result = await client.preview.renderStill({ projectSessionId: id(), sourceRevision: current(), timeUs: micros(1_000_000), size, renderBackend: 'cpu' });
  expect(result.verified).toBe(true);
  expect(result.frame).toMatchObject(size);
  return result.sha256;
}

async function apply(templateId: string, assignmentTarget: PresetApplicationTarget = target()): Promise<string> {
  // 此命令的 templateRevision 是库快照修订；模板预览另有内容修订字段。
  const library = await client.templates.listAll('personal');
  const response = await command({ type: 'presetApplications.apply', templateLibraryId: 'personal', templateId, templateRevision: library.revision, targets: [assignmentTarget] });
  expect(response.changed).toBe(true);
  expect(response.presetApplicationResult?.applicationIds).toHaveLength(1);
  return response.presetApplicationResult!.applicationIds[0]!;
}

function expectSources(value: Awaited<ReturnType<typeof resolved>>, properties: string[], path: string): void {
  for (const property of properties) expect(value.sources.find((source) => source.property === property)?.sourcePath, property).toBe(path);
  expect(value.finalPixelAppearance).toBe(false);
}

const glowProperties = ['glowSpec', 'glowColor', 'glowOpacity', 'glowBlurRadiusPx', 'glowSpreadPx'];

describe('辉光模板与来源（真实 Server）', () => {
  it('创建整体模板，应用只改变辉光；快照、来源和禁用/重新启用的帧 SHA 一致', async () => {
    const builtin = await client.templates.listAll('builtin');
    const base = (await client.templates.detail('builtin', 'builtin:line-classic', builtin.revision)).definition!;
    fullDefinition = { ...base, name: '集成整体辉光', style: { ...base.style, glow }, properties: ['glowSpec'] };
    const library = await client.templates.listAll('personal');
    const created = await client.templates.command('personal', library.revision, { type: 'create', definition: fullDefinition });
    fullTemplateId = created.templateId;
    const detail = await client.templates.detail('personal', fullTemplateId, created.revision);
    expect(detail.definition).toEqual(fullDefinition);
    unlitFrame = await still();
    fullApplicationId = await apply(fullTemplateId, { kind: 'branch', branchId });
    const apps = (await client.projects.presetApplications(id(), current())).applications;
    expect(apps[0]).toMatchObject({ applicationId: fullApplicationId, target: { kind: 'branch', branchId }, enabled: true, snapshot: { templateId: fullTemplateId, properties: ['glowSpec'] } });
    const value = await resolved();
    expect(value.style.glow).toEqual(glow);
    expectSources(value, glowProperties, `presetApplications.${fullApplicationId}.snapshot.style`);
    expect((await style()).staticHierarchy.glow).toBeNull();
    glowFrame = await still();
    expect(glowFrame).not.toBe(unlitFrame);
    await command({ type: 'presetApplications.setEnabled', applicationId: fullApplicationId, enabled: false });
    expect((await resolved()).style.glow).toBeNull();
    expect(await still()).toBe(unlitFrame);
    await command({ type: 'presetApplications.setEnabled', applicationId: fullApplicationId, enabled: true });
    expect(await still()).toBe(glowFrame);
  });

  it('叶属性模板覆盖整体模板；移除后恢复整体辉光和帧 SHA', async () => {
    const definition: StaticTemplateDefinition = { ...fullDefinition, name: '集成叶辉光', style: { ...fullDefinition.style, glow: leafGlow }, properties: ['glowColor', 'glowOpacity', 'glowBlurRadiusPx', 'glowSpreadPx'] };
    const library = await client.templates.listAll('personal');
    const created = await client.templates.command('personal', library.revision, { type: 'create', definition });
    expect((await client.templates.detail('personal', created.templateId, created.revision)).definition).toEqual(definition);
    leafApplicationId = await apply(created.templateId);
    const value = await resolved();
    expect(value.style.glow).toEqual(leafGlow);
    expectSources(value, definition.properties, `presetApplications.${leafApplicationId}.snapshot.style`);
    expect(await still()).not.toBe(glowFrame);
    await command({ type: 'presetApplications.remove', applicationIds: [leafApplicationId] });
    expect((await resolved()).style.glow).toEqual(glow);
    expect(await still()).toBe(glowFrame);
  });
});

describe('直接覆盖的三态继承', () => {
  it('项目→行继承，null 关闭，叶 restore 不撤销 null，整体 restore 恢复；叶覆盖单独恢复', async () => {
    await command({ type: 'presetApplications.remove', applicationIds: [fullApplicationId] });
    await command({ type: 'styles.patch', targets: [{ kind: 'project' }], changes: [{ property: 'glow', value: glow }] });
    expect((await style()).staticHierarchy.glow).toEqual(glow);
    expectSources(await resolved(), glowProperties, 'lyricStyle');
    const inheritedFrame = await still();
    await command({ type: 'styles.patch', targets: [target()], changes: [{ property: 'glow', value: null }] });
    expect((await style()).local).toContainEqual({ property: 'glow', value: null });
    expect((await style()).staticHierarchy.glow).toBeNull();
    expect(await still()).toBe(unlitFrame);
    const unchanged = await command({ type: 'styles.restore', targets: [target()], properties: ['glow.opacity'] });
    expect(unchanged.changed).toBe(false);
    expect((await style()).staticHierarchy.glow).toBeNull();
    await command({ type: 'styles.restore', targets: [target()], properties: ['glow'] });
    expect((await style()).staticHierarchy.glow).toEqual(glow);
    expect(await still()).toBe(inheritedFrame);
    await command({ type: 'styles.patch', targets: [target()], changes: [{ property: 'glow.opacity', value: 0.5 }] });
    expect((await style()).staticHierarchy.glow).toEqual({ ...glow, opacity: 0.5 });
    expectSources(await resolved(), ['glowOpacity'], `lyrics.branches.${branchId}.lines.${lineId}.styleOverride`);
    await command({ type: 'styles.restore', targets: [target()], properties: ['glow.opacity'] });
    expect((await style()).local.some((change) => change.property.startsWith('glow'))).toBe(false);
    expect((await style()).staticHierarchy.glow).toEqual(glow);
    expect(await still()).toBe(inheritedFrame);
    // 项目样式是 baseline，关闭使用 patch null；restore 仅用于覆盖层。
    await command({ type: 'styles.patch', targets: [{ kind: 'project' }], changes: [{ property: 'glow', value: null }] });
    expect((await style()).staticHierarchy.glow).toBeNull();
    expect(await still()).toBe(unlitFrame);
  });
});

// 对目录中每个文件取摘要，同时核对文件集合，验证失败打开没有迁移或改写工程闭包。
async function fileHashes(directory: string, prefix = ''): Promise<Record<string, string>> {
  const hashes: Record<string, string> = {};
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const name = join(prefix, entry.name);
    if (entry.isDirectory()) Object.assign(hashes, await fileHashes(join(directory, entry.name), name));
    else hashes[name] = sha(await readFile(join(directory, entry.name)));
  }
  return hashes;
}

describe('保存重开与严格版本门禁', () => {
  it('固定模板随工程保存重开；有效辉光、来源和实际帧保持一致', async () => {
    fullApplicationId = await apply(fullTemplateId);
    const before = await still();
    const saved = await finish(await client.projects.save(id(), { targetHandleId: await handle('current.mkgproj') }));
    expect(saved.state, saved.error?.message).toBe('succeeded');
    const closed = await finish(await client.projects.close(id(), { policy: 'requireClean' }));
    expect(closed.state, closed.error?.message).toBe('succeeded');
    opened.delete(id());
    const entry = JSON.parse(await readFile(join(projectsRoot, 'current.mkgproj', 'project.json'), 'utf8'));
    expect(entry).toMatchObject({ applicationVersion: CONTRACT_PRODUCT_VERSION, schemaVersion: 29, presetApplications: { templateSchemaVersion: 7 } });
    expect(JSON.parse(await readFile(join(projectsRoot, 'current.mkgproj', entry.lyricsFile as string), 'utf8')).schemaVersion).toBe(18);
    const reopened = await finish(await client.projects.open({ projectDirectoryHandleId: await handle('current.mkgproj') }));
    expect(reopened.state, reopened.error?.message).toBe('succeeded');
    project = reopened.result!;
    opened.add(id());
    await client.projects.get(id());
    expect((await resolved()).style.glow).toEqual(glow);
    expectSources(await resolved(), glowProperties, `presetApplications.${fullApplicationId}.snapshot.style`);
    expect(await still()).toBe(before);
  });

  it.each(['product', 'project-schema', 'lyrics-schema', 'template-schema'] as const)('旧 %s 被拒绝且整个副本的文件集合和摘要保持原样', async (variant) => {
    const relative = `${variant}.mkgproj`;
    const directory = join(projectsRoot, relative);
    await cp(join(projectsRoot, 'current.mkgproj'), directory, { recursive: true });
    const entryFile = join(directory, 'project.json');
    const entry = JSON.parse(await readFile(entryFile, 'utf8'));
    if (variant === 'product') entry.applicationVersion = '0.109.0';
    if (variant === 'project-schema') entry.schemaVersion = 28;
    if (variant === 'template-schema') entry.presetApplications.templateSchemaVersion = 6;
    if (variant === 'lyrics-schema') {
      const lyrics = JSON.parse(await readFile(join(directory, entry.lyricsFile as string), 'utf8'));
      lyrics.schemaVersion = 17;
      const bytes = Buffer.from(JSON.stringify(lyrics));
      entry.lyricsFile = `data/lyrics/${sha(bytes)}.json`;
      await writeFile(join(directory, entry.lyricsFile as string), bytes);
    }
    await writeFile(entryFile, JSON.stringify(entry));
    const before = await fileHashes(directory);
    const result = await finish(await client.projects.open({ projectDirectoryHandleId: await handle(relative) }));
    expect(result.state).toBe('failed');
    expect(result.error?.code).toBe('projectLoadFailed');
    expect(await fileHashes(directory)).toEqual(before);
  });
});

describe('HTTP 合同', () => {
  it('全部 JSON 响应符合当前冻结 OpenAPI，覆盖模板/分配/两种样式读取/保存/打开', () => {
    expect(recorder.issues).toEqual([]);
    for (const route of ['/templates/detail', '/preset-applications', '/resolved-style', '/style', '/save-jobs', '/open-jobs']) {
      expect([...recorder.validated].some((entry) => entry.includes(route)), route).toBe(true);
    }
  });
});
