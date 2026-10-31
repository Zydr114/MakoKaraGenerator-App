// 工程的媒体：音频、背景图片或视频（W5 设计 §1、§2.6）。先上传成资源，再用一条命令换进工程，可以撤销。

import type { MakoClient } from '../../client';
import type { ProjectCommandResponse } from '../../protocol';
import type { EditorSession } from './editor-session';
import type { ProgressReporter } from './project-files';

/** 本页上传过的文件名，按资源 ID 记：资源清单和设置里没有媒体的显示名（G28），重开工程后就显示不出来。 */
const uploadedNames = new Map<string, string>();

export function uploadedName(assetId: string | undefined): string | undefined {
  return assetId ? uploadedNames.get(assetId) : undefined;
}

/** 上传一个媒体文件，返回资源 ID。类型按扩展名推断（客户端 inferContentType）。 */
export async function uploadMedia(client: MakoClient, file: File, progress: ProgressReporter = () => undefined): Promise<string> {
  progress(`正在上传 ${file.name}`);
  const asset = await client.assets.upload(file, { fileName: file.name });
  uploadedNames.set(asset.assetId, file.name);
  return asset.assetId;
}

/** 上传并替换工程音频：一条历史；后端要先探测、解码，可能需要数十秒。命令被拒绝时返回 undefined（原因在 notice 里）。 */
export async function replaceAudio(client: MakoClient, session: EditorSession, file: File, progress: ProgressReporter = () => undefined): Promise<ProjectCommandResponse | undefined> {
  const assetId = await uploadMedia(client, file, progress);
  progress('正在准备音频（可能需要数十秒）');
  return session.command({ type: 'audio.replaceAsset', assetId });
}
