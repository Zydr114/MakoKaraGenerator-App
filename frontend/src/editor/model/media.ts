// 工程设置里的媒体（W5 设计 §2.5）：背景图片／视频的判断与选项、资源清单里的大小和状态、工程名的预检。纯函数，不依赖 React。

import type { BackgroundFit, BackgroundMediaKind, BackgroundPlayback, ProjectResource } from '../../protocol';

const IMAGE_EXTENSIONS = ['png', 'jpg', 'jpeg', 'webp', 'gif', 'bmp', 'avif'];
const VIDEO_EXTENSIONS = ['mp4', 'mov', 'webm', 'mkv', 'avi', 'mpg', 'mpeg'];

/** 背景文件的选择范围：后端接受的图片和视频。 */
export const BACKGROUND_FILE_ACCEPT = ['image/*', 'video/*', ...[...IMAGE_EXTENSIONS, ...VIDEO_EXTENSIONS].map((e) => `.${e}`)].join(',');

/** 文件是否符合文件选择框的 accept（扩展名、MIME 或 audio/* 这样的通配）。拖进来的文件不经过系统对话框的过滤，要自己查。 */
export function acceptsFile(file: { name: string; type: string }, accept: string): boolean {
  const name = file.name.toLowerCase();
  const type = file.type.toLowerCase();
  return accept
    .split(',')
    .map((rule) => rule.trim().toLowerCase())
    .some((rule) => (rule.startsWith('.') ? name.endsWith(rule) : rule.endsWith('/*') ? type.startsWith(rule.slice(0, -1)) : !!rule && type === rule));
}

/** 按文件的 MIME 判断背景种类，浏览器没给类型时看扩展名；都不是时为 undefined。 */
export function backgroundKindOf(file: { name: string; type: string }): BackgroundMediaKind | undefined {
  if (file.type.startsWith('image/')) return 'image';
  if (file.type.startsWith('video/')) return 'video';
  const extension = file.name.slice(file.name.lastIndexOf('.') + 1).toLowerCase();
  if (IMAGE_EXTENSIONS.includes(extension)) return 'image';
  if (VIDEO_EXTENSIONS.includes(extension)) return 'video';
  return undefined;
}

export const FIT_OPTIONS: { value: BackgroundFit; label: string }[] = [
  { value: 'contain', label: '完整显示' },
  { value: 'cover', label: '铺满' },
  { value: 'original', label: '原始大小' },
];

export const PLAYBACK_OPTIONS: { value: BackgroundPlayback; label: string }[] = [
  { value: 'loop', label: '循环' },
  { value: 'freezeLastFrame', label: '停在最后一帧' },
];

export const BACKGROUND_KIND_TEXT: Record<BackgroundMediaKind | 'color', string> = { color: '底色', image: '图片', video: '视频' };

/** 字节数（十进制字符串或数字）写成“850 KB”“1.2 MB”。 */
export function formatBytes(size: string | number | undefined): string | undefined {
  if (size === undefined || size === '') return undefined;
  const bytes = Number(size);
  if (!Number.isFinite(bytes) || bytes < 0) return undefined;
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value >= 100 ? Math.round(value) : Number(value.toFixed(1))} ${units[unit]}`;
}

const STATUS_TEXT: Record<ProjectResource['status'], string> = {
  available: '可用',
  validated: '已核对',
  embedded: '内嵌',
  missing: '文件缺失',
  identityUnavailable: '无法核对文件',
  unsupportedContent: '格式不受支持',
  unsupported: '不受支持',
  noStream: '没有可用的音视频流',
  failed: '读取失败',
};

/** 资源能不能用于渲染（导出准备检查也用）。 */
export function resourceUsable(resource: ProjectResource): boolean {
  return resource.status === 'available' || resource.status === 'validated' || resource.status === 'embedded';
}

export function resourceStatusText(resource: ProjectResource): string {
  return STATUS_TEXT[resource.status] ?? resource.status;
}

const hasRole = (resource: ProjectResource, role: RegExp) => resource.roles.some((r) => role.test(r));

/** 工程当前的音频资源。 */
export function audioResource(resources: readonly ProjectResource[]): ProjectResource | undefined {
  return resources.find((r) => r.kind === 'media' && hasRole(r, /audio/i));
}

/** 设置里背景声明对应的资源；没有声明 resourceId 时按角色找。 */
export function backgroundResource(resources: readonly ProjectResource[], resourceId: string | undefined): ProjectResource | undefined {
  return resources.find((r) => (resourceId ? r.resourceId === resourceId : r.kind === 'media' && hasRole(r, /background/i)));
}

const WAVEFORM_TEXT: Record<NonNullable<ProjectResource['waveformStatus']>, string> = {
  available: '波形可用',
  noAudio: '没有音轨',
  cacheBudgetExceeded: '音频太长，没有波形',
  audioIdentityChanged: '音频已变，波形待重建',
};

/** 音频的准备状态：“已准备 · 波形可用”。 */
export function audioStatusText(resource: ProjectResource): string {
  const parts: string[] = [];
  if (!resourceUsable(resource)) parts.push(resourceStatusText(resource));
  if (resource.preparationStatus) parts.push(resource.preparationStatus === 'prepared' ? '已准备' : '无法准备');
  if (resource.waveformStatus) parts.push(WAVEFORM_TEXT[resource.waveformStatus] ?? resource.waveformStatus);
  return parts.length ? parts.join(' · ') : resourceStatusText(resource);
}

/** 工程名：去掉首尾空白后不能为空；原输入不超过 256 个 UTF-16 单元（后端规则，按原输入算）。 */
export function checkProjectName(name: string): string | undefined {
  if (!name.trim()) return '名称不能为空。';
  if (name.length > 256) return '名称太长：最多 256 个字符。';
  return undefined;
}
