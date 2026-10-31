// W5 工程目录浏览的页面模型（W5 设计 §2.3）：路径导航、当前层搜索、保存目标。

import { describe, expect, it } from 'vitest';
import { checkDirectoryName, checkRelativePath, filterEntries, joinPath, parentPath, pathSegments, saveTarget, saveTargetText, suggestDirectoryName } from '../../src/editor/model/project-directory';
import type { ProjectDirectoryEntry } from '../../src/protocol';

const entries: ProjectDirectoryEntry[] = [
  { name: 'Album', relativePath: 'a/Album', isProject: false },
  { name: 'my-song', relativePath: 'a/my-song', isProject: true },
  { name: '夜に駆ける', relativePath: 'a/夜に駆ける', isProject: true },
];

describe('路径', () => {
  it('路径导航的各段与上一层', () => {
    expect(pathSegments('')).toEqual([]);
    expect(pathSegments('a/b/c')).toEqual([
      { name: 'a', path: 'a' },
      { name: 'b', path: 'a/b' },
      { name: 'c', path: 'a/b/c' },
    ]);
    expect(parentPath('a/b/c')).toBe('a/b');
    expect(parentPath('a')).toBe('');
    expect(joinPath('', ' x ')).toBe('x');
    expect(joinPath('a/b', 'x')).toBe('a/b/x');
  });

  it('相对路径与目录名的预检', () => {
    expect(checkRelativePath('a/b')).toBeUndefined();
    expect(checkRelativePath('/abs')).toMatch(/相对路径/);
    expect(checkRelativePath('a/../b')).toMatch(/“\.\.”/);
    expect(checkRelativePath('c:x')).toMatch(/冒号/);
    expect(checkDirectoryName('a/b')).toMatch(/斜杠/);
    expect(checkDirectoryName('  ')).toMatch(/请填写/);
  });
});

describe('搜索与保存目标', () => {
  it('只按名称过滤当前层，不分大小写', () => {
    expect(filterEntries(entries, 'SONG').map((e) => e.name)).toEqual(['my-song']);
    expect(filterEntries(entries, '駆')).toHaveLength(1);
    expect(filterEntries(entries, ' ')).toHaveLength(3);
  });

  it('新建、覆盖已有工程、写进已有目录、名字不合法', () => {
    expect(saveTarget(entries, 'a', 'new-song')).toEqual({ kind: 'new', path: 'a/new-song' });
    expect(saveTarget(entries, 'a', 'my-song')).toEqual({ kind: 'project', path: 'a/my-song' });
    expect(saveTarget(entries, 'a', 'Album')).toEqual({ kind: 'directory', path: 'a/Album' });
    // 与后端一样按大小写精确比较。
    expect(saveTarget(entries, 'a', 'album').kind).toBe('new');
    expect(saveTarget(entries, 'a', 'x/y').kind).toBe('invalid');
    // 另存为到工程现在的位置：等于直接保存，不要求确认覆盖。
    expect(saveTarget(entries, 'a', 'my-song', 'a/my-song')).toEqual({ kind: 'current', path: 'a/my-song' });
    expect(saveTargetText(saveTarget(entries, 'a', 'my-song'))).toMatch(/覆盖/);
  });

  it('目录名建议去掉斜杠、冒号和开头的点', () => {
    expect(suggestDirectoryName(' A/B:C ')).toBe('A-B-C');
    expect(suggestDirectoryName('..hidden')).toBe('hidden');
    expect(suggestDirectoryName('  ')).toBe('my-song');
  });
});
