// W0 出口 7：src/client 不依赖任何 UI 框架。依赖方向 console → client → protocol，editor → client → protocol。
// W1 设计 §4.1：editor 与 console 互不依赖；editor/model 是纯 TS，只依赖自身与 protocol。
// 扫描 import／export from／动态 import 的目标，按目录检查。

import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const SRC = resolve(fileURLToPath(new URL('../../src', import.meta.url)));
const IMPORT = /(?:import|export)\s[^'"]*?from\s*['"]([^'"]+)['"]|import\s*\(\s*['"]([^'"]+)['"]\s*\)|import\s+['"]([^'"]+)['"]/g;

function files(dir: string): string[] {
  if (!existsSync(dir)) return [];
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : /\.(ts|tsx)$/.test(name) ? [path] : [];
  });
}

function imports(file: string): string[] {
  const text = readFileSync(file, 'utf8');
  return [...text.matchAll(IMPORT)].map((m) => m[1] ?? m[2] ?? m[3]!).filter(Boolean);
}

interface LayerRules {
  /** 允许相对导入的目录（相对 src，例如 'protocol'、'editor/model'）。 */
  allowed: string[];
  /** 允许依赖的外部包（file 是相对 src 的路径）；缺省为不允许任何包。 */
  packages?: (target: string, file: string) => boolean;
  /** 是否允许 .tsx 文件。 */
  tsx?: boolean;
}

/** 返回违规项：layer 下的文件只能相对导入 allowed 中的目录，外部包只能是 packages 放行的。 */
function violations(layer: string, rules: LayerRules): string[] {
  const root = join(SRC, layer);
  const result: string[] = [];
  for (const file of files(root)) {
    if (!rules.tsx && /\.tsx$/.test(file)) result.push(`${relative(SRC, file)}: 不允许 .tsx 文件`);
    for (const target of imports(file)) {
      if (!target.startsWith('.')) {
        if (!rules.packages?.(target, relative(SRC, file).split(sep).join('/'))) result.push(`${relative(SRC, file)} → ${target}（不允许依赖外部包）`);
        continue;
      }
      const resolved = relative(SRC, resolve(file, '..', target)).split(sep).join('/');
      const inside = rules.allowed.some((dir) => resolved === dir || resolved.startsWith(`${dir}/`));
      if (!inside) result.push(`${relative(SRC, file)} → ${target}（${layer} 不能依赖 ${resolved}）`);
    }
  }
  return result;
}

// 界面改版（§2.1）：React Aria Components 与 material-color-utilities。
const UI_PACKAGES = (target: string) => /^(react|react-dom|react-aria-components|@material\/material-color-utilities)(\/|$)/.test(target);
/** 代码编辑器（W8 设计 §2.6）：CodeMirror 只在 SourceEditor.tsx 里用，它只被动态 import，打成单独的分块。 */
const CODE_EDITOR = 'editor/library/SourceEditor.tsx';
const EDITOR_PACKAGES = (target: string, file: string) =>
  UI_PACKAGES(target) || (file === CODE_EDITOR && /^(@codemirror\/(state|view|commands|language|legacy-modes|lint)|@lezer\/highlight)(\/|$)/.test(target));

describe('依赖边界', () => {
  it('src/protocol 只依赖自身', () => {
    expect(violations('protocol', { allowed: ['protocol'] })).toEqual([]);
  });

  it('src/client 只依赖自身与 protocol，不依赖 React 或任何 UI 代码', () => {
    expect(violations('client', { allowed: ['client', 'protocol'] })).toEqual([]);
  });

  it('src/editor 只依赖自身、client、protocol 与 UI 包，不依赖 console', () => {
    expect(violations('editor', { allowed: ['editor', 'client', 'protocol'], packages: EDITOR_PACKAGES, tsx: true })).toEqual([]);
  });

  it('SourceEditor.tsx 只被动态 import（CodeMirror 不进首屏分块）', () => {
    const IMPORTER = /(?:import|export)\s[^'"]*?from\s*['"]([^'"]*SourceEditor)['"]|import\s+['"]([^'"]*SourceEditor)['"]/g;
    const statics = files(join(SRC, 'editor')).filter((file) => [...readFileSync(file, 'utf8').matchAll(IMPORTER)].length > 0);
    expect(statics.map((file) => relative(SRC, file))).toEqual([]);
  });

  it('src/editor/model 是纯 TS：只依赖自身与 protocol', () => {
    expect(violations('editor/model', { allowed: ['editor/model', 'protocol'] })).toEqual([]);
  });

  it('src/console 只依赖自身、client、protocol 与 React，不依赖 editor', () => {
    expect(violations('console', { allowed: ['console', 'client', 'protocol'], packages: UI_PACKAGES, tsx: true })).toEqual([]);
  });

  it('检查器本身能发现违规', () => {
    const sample = "import { useState } from 'react';\nimport { App } from '../console/App';";
    expect([...sample.matchAll(IMPORT)].map((m) => m[1])).toEqual(['react', '../console/App']);
  });
});
