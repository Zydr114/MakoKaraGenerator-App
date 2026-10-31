// 工程的新建、打开、恢复、保存、关闭（W1 设计 §2、W5 设计 §2.6）。打开、恢复、保存、关闭都是 202 job，等到成功终态才算完成。

import { BackendError, type MakoClient } from '../../client';
import type { ClosePolicy, Composition, FileAccess, JobResponse, ProjectSessionSummary, SplitRule } from '../../protocol';
import { uploadMedia } from './project-media';

/** 已上传并做过导入规划的歌词文件；importerId／encodingName 为空表示自动识别。 */
export interface UploadedLyrics {
  file: File;
  assetId: string;
  importerId?: string;
  encodingName?: string;
}

/** 音频、歌词都可以没有（合同 1.36.0 的四种组合）。 */
export interface NewProjectInput {
  name: string;
  audio: File | null;
  lyrics: UploadedLyrics | null;
  composition: Composition;
  splitRule?: SplitRule;
  estimateSplitTimes?: boolean;
}

export type ProgressReporter = (step: string) => void;

/**
 * 新建工程。歌词在选择时已上传；未被引用的资源 600 秒后过期，对话框开太久导致创建时歌词资源已不存在，
 * 就重新上传一次再建（W5 设计 §2.6）。
 */
export async function createProject(client: MakoClient, input: NewProjectInput, progress: ProgressReporter = () => undefined): Promise<ProjectSessionSummary> {
  let audioAssetId: string | undefined;
  if (input.audio) {
    progress(`正在上传音频 ${input.audio.name}`);
    audioAssetId = await uploadMedia(client, input.audio);
  }
  const lyrics = input.lyrics;
  const create = (lyricsAssetId: string | undefined) => {
    progress(audioAssetId ? '正在准备音频并新建工程（可能需要数十秒）' : '正在新建工程');
    return client.projects.create({
      name: input.name,
      ...(audioAssetId ? { audioAssetId } : {}),
      composition: input.composition,
      ...(lyricsAssetId
        ? {
            lyricsAssetId,
            ...(lyrics?.importerId ? { importerId: lyrics.importerId } : {}),
            ...(lyrics?.encodingName ? { encodingName: lyrics.encodingName } : {}),
            ...(input.splitRule !== undefined ? { splitRule: input.splitRule } : {}),
            ...(input.estimateSplitTimes !== undefined ? { estimateSplitTimes: input.estimateSplitTimes } : {}),
          }
        : {}),
    });
  };
  try {
    return await create(lyrics?.assetId);
  } catch (error) {
    if (!lyrics || !(error instanceof BackendError) || !ASSET_GONE.has(error.code ?? '')) throw error;
    progress(`歌词文件已过期，正在重新上传 ${lyrics.file.name}`);
    const again = await client.assets.upload(lyrics.file, { fileName: lyrics.file.name });
    return create(again.assetId);
  }
}

const ASSET_GONE = new Set(['resourceNotFound', 'resourceExpired']);

/** 从恢复候选恢复工程（W5 设计 §2.6）；成功终态的 result 是新会话的摘要。 */
export async function recoverProject(client: MakoClient, recoveryId: string, progress: ProgressReporter = () => undefined): Promise<ProjectSessionSummary> {
  progress('正在恢复');
  const job = await finish(client, await client.projects.recover({ recoveryId }), progress);
  return job.result!;
}

/** 授权的工程目录根。启动器只给一个根（projects）。 */
export async function projectRoot(client: MakoClient): Promise<string> {
  const roots = await client.files.roots();
  const rootGrantId = roots.rootGrantIds[0];
  if (!rootGrantId) throw new Error('后端没有授权任何工程目录。');
  return rootGrantId;
}

/** 授权根下相对目录的句柄。 */
async function directoryHandle(client: MakoClient, relativePath: string, access: FileAccess): Promise<string> {
  const handle = await client.files.createHandle({ rootGrantId: await projectRoot(client), relativePath, access });
  return handle.handleId;
}

/** 打开已保存的工程；access 为 read 时只读打开（不取目录写租约）。 */
export async function openProject(
  client: MakoClient,
  relativePath: string,
  progress: ProgressReporter = () => undefined,
  access: FileAccess = 'readWrite',
): Promise<ProjectSessionSummary> {
  progress(`正在${access === 'read' ? '只读' : ''}打开 ${relativePath}`);
  const handleId = await directoryHandle(client, relativePath, access);
  const job = await finish(client, await client.projects.open({ projectDirectoryHandleId: handleId, access }), progress);
  locations.set(job.result!.projectSessionId, relativePath);
  return job.result!;
}

/** 读目录失败的说明（W5 设计 §2.6）。 */
export function directoryErrorText(error: unknown): string {
  if (error instanceof BackendError) {
    if (error.status === 429) return '目录里的项目太多（超过 4096 项），无法列出。请把工程整理到子目录里。';
    if (error.code === 'pathNotAllowed') return '这个目录不存在或不能访问。';
  }
  return error instanceof Error ? error.message : String(error);
}

/** 已有保存位置时 relativePath 可省略；新工程必须给。 */
export async function saveProject(
  client: MakoClient,
  projectSessionId: string,
  relativePath?: string,
  progress: ProgressReporter = () => undefined,
): Promise<ProjectSessionSummary> {
  const targetHandleId = relativePath ? await directoryHandle(client, relativePath, 'readWrite') : undefined;
  progress('正在保存');
  const job = await finish(client, await client.projects.save(projectSessionId, targetHandleId ? { targetHandleId } : {}), progress);
  if (relativePath) locations.set(projectSessionId, relativePath);
  return job.result!;
}

// ── 本页打开或保存过的工程所在目录：摘要只有 hasPath，没有路径；“另存为…”从这里的上一层开始浏览 ──

const locations = new Map<string, string>();

/** 工程的保存目录（本页打开或保存时记下）；恢复出的工程、刷新页面前打开的工程不知道。 */
export function projectLocation(projectSessionId: string): string | undefined {
  return locations.get(projectSessionId);
}

export async function closeProject(client: MakoClient, projectSessionId: string, policy: ClosePolicy, progress: ProgressReporter = () => undefined): Promise<void> {
  progress('正在关闭');
  await finish(client, await client.projects.close(projectSessionId, { policy }), progress);
}

async function finish(client: MakoClient, accepted: JobResponse, progress: ProgressReporter): Promise<JobResponse> {
  const job = await client.jobs.waitForTerminal(accepted.jobId, {
    onProgress: (current) => progress(`${KIND_TEXT[current.kind] ?? current.kind}：${STATE_TEXT[current.state] ?? current.state}`),
  });
  if (job.state === 'succeeded' && job.result) return job;
  if (job.state === 'unknown') throw new Error('结果未知：请稍候再查看工程状态。');
  if (job.error) throw new BackendError(422, job.error);
  throw new Error(`${KIND_TEXT[job.kind] ?? job.kind}没有成功（${job.state}）。`);
}

const KIND_TEXT: Record<string, string> = { open: '打开', save: '保存', close: '关闭', recover: '恢复' };
const STATE_TEXT: Record<string, string> = { queued: '排队中', running: '进行中', committing: '正在写入' };

// ── 最近使用的目录名：只存相对目录名，不存任何凭证 ──

const RECENT_KEY = 'mako.recentProjects';
const RECENT_MAX = 8;

export function recentProjects(): string[] {
  try {
    const value = JSON.parse(localStorage.getItem(RECENT_KEY) ?? '[]') as unknown;
    return Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string').slice(0, RECENT_MAX) : [];
  } catch {
    return [];
  }
}

export function rememberProject(relativePath: string): void {
  const next = [relativePath, ...recentProjects().filter((p) => p !== relativePath)].slice(0, RECENT_MAX);
  localStorage.setItem(RECENT_KEY, JSON.stringify(next));
}
