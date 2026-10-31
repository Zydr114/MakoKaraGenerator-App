// 工程目录浏览（W5 设计 §2.3）：路径导航的各段、当前层的搜索、拼出目标路径、保存目标是否已存在。
// 只处理授权根下的相对路径（后端规范 §7）；不依赖 DOM。

import type { ProjectDirectoryEntry } from '../../protocol';

export interface PathSegment {
  name: string;
  /** 从根到这一段的相对路径。 */
  path: string;
}

/** 'a/b' → [a, a/b]；根（''）没有段。 */
export function pathSegments(relativePath: string): PathSegment[] {
  const parts = relativePath.split('/').filter(Boolean);
  return parts.map((name, i) => ({ name, path: parts.slice(0, i + 1).join('/') }));
}

export function joinPath(parent: string, name: string): string {
  const child = name.trim();
  return parent ? `${parent}/${child}` : child;
}

export function parentPath(relativePath: string): string {
  const i = relativePath.lastIndexOf('/');
  return i < 0 ? '' : relativePath.slice(0, i);
}

/** 只过滤当前这一层：名称包含关键字，不分大小写。 */
export function filterEntries(entries: readonly ProjectDirectoryEntry[], query: string): ProjectDirectoryEntry[] {
  const q = query.trim().toLocaleLowerCase();
  return q ? entries.filter((e) => e.name.toLocaleLowerCase().includes(q)) : [...entries];
}

/** 相对目录名的客户端预检，与后端规则一致：非空、非绝对路径、无 `.`／`..`／空片段／冒号。 */
export function checkRelativePath(path: string): string | undefined {
  const trimmed = path.trim();
  if (!trimmed) return '请填写目录名。';
  if (trimmed.startsWith('/') || trimmed.startsWith('\\')) return '只能填授权目录下的相对路径。';
  if (trimmed.includes(':')) return '目录名不能包含冒号。';
  const parts = trimmed.split(/[\\/]/);
  if (parts.some((p) => p === '' || p === '.' || p === '..')) return '目录名不能包含空片段、“.” 或 “..”。';
  return undefined;
}

/** 新目录名（一段）：在 checkRelativePath 之外不能含斜杠。 */
export function checkDirectoryName(name: string): string | undefined {
  if (/[\\/]/.test(name.trim())) return '目录名不能包含斜杠；要放进子目录，请先在上面进入那个目录。';
  return checkRelativePath(name);
}

export type SaveTarget =
  | { kind: 'invalid'; message: string }
  /** 当前层没有同名项：保存时新建目录。 */
  | { kind: 'new'; path: string }
  /** 同名的是工程：保存会覆盖它（后端不拦，集成测试实测），要先确认。 */
  | { kind: 'project'; path: string }
  /** 同名的是普通目录：工程文件写进这个目录。 */
  | { kind: 'directory'; path: string }
  /** 就是工程现在的保存目录：等于直接保存。 */
  | { kind: 'current'; path: string };

/**
 * 在 parent 层（entries 是这一层的全部条目）用 name 保存时的目标。同名按大小写精确比较，与后端的 Ordinal 一致。
 * current 是工程现在的保存目录（知道时）。
 */
export function saveTarget(entries: readonly ProjectDirectoryEntry[], parent: string, name: string, current?: string): SaveTarget {
  const error = checkDirectoryName(name);
  if (error) return { kind: 'invalid', message: error };
  const path = joinPath(parent, name);
  if (path === current) return { kind: 'current', path };
  const existing = entries.find((e) => e.name === name.trim());
  if (!existing) return { kind: 'new', path };
  return existing.isProject ? { kind: 'project', path } : { kind: 'directory', path };
}

/** 保存对话框里就地写明的目标说明。 */
export function saveTargetText(target: SaveTarget): string {
  switch (target.kind) {
    case 'invalid':
      return target.message;
    case 'new':
      return `会新建目录“${target.path}”。`;
    case 'directory':
      return `“${target.path}”是已有的普通目录，工程文件会写进这个目录。`;
    case 'project':
      return `“${target.path}”里已经有一个工程，保存会覆盖它。`;
    case 'current':
      return `这是工程现在的保存位置，等于直接保存。`;
  }
}

/** 保存目录名的建议：工程名去掉斜杠、冒号和开头的点。 */
export function suggestDirectoryName(projectName: string): string {
  return projectName.trim().replace(/[\\/:]+/g, '-').replace(/^\.+/, '') || 'my-song';
}
