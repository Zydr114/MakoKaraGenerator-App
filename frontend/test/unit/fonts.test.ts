// W7 第 6 步：字体目录的搜索与标记、字体检查结果的分段、字体目录的读取与作废（W7 设计 §2.5）。

import { describe, expect, it } from 'vitest';
import type { MakoClient } from '../../src/client';
import { FontCatalog } from '../../src/editor/library/font-catalog';
import { catalogName, FONT_CHECK_SAMPLE, fontCheckText, fontRuns, hasFamily, markText, searchFamilies } from '../../src/editor/model/fonts';

const FAMILIES = ['Noto Sans CJK SC', 'Noto Serif CJK SC', 'DejaVu Sans', 'Source Han Sans', 'Sans Fallback'];

describe('字体目录', () => {
  it('搜索不区分大小写，开头匹配在前；没有输入时按目录顺序；最多 limit 个', () => {
    expect(searchFamilies(FAMILIES, '')).toEqual({ items: FAMILIES, total: 5 });
    expect(searchFamilies(FAMILIES, 'sans').items).toEqual(['Sans Fallback', 'Noto Sans CJK SC', 'DejaVu Sans', 'Source Han Sans']);
    expect(searchFamilies(FAMILIES, 'NOTO', 1)).toEqual({ items: ['Noto Sans CJK SC'], total: 2 });
    expect(searchFamilies(FAMILIES, '宋体').items).toEqual([]);
  });

  it('目录里有没有、按目录的写法', () => {
    expect(hasFamily(FAMILIES, 'dejavu sans')).toBe(true);
    expect(hasFamily(FAMILIES, '微软雅黑')).toBe(false);
    expect(catalogName(FAMILIES, ' dejavu sans ')).toBe('DejaVu Sans');
    expect(catalogName(FAMILIES, '微软雅黑')).toBe('微软雅黑');
  });

  it('检查结果按实际字体、回退、缺字归成连续的段，空白段不列；检查文字带上试样', () => {
    const e = (text: string, actualFamily: string, isFallback = false, hasMissingGlyph = false, i = 0) => ({ elementIndex: i, utf16Start: i, text, actualFamily, isFallback, hasMissingGlyph });
    expect(
      fontRuns([
        e('模', 'Noto Sans CJK SC', false, false, 0),
        e('板', 'Noto Sans CJK SC', false, false, 1),
        e(' ', 'DejaVu Sans', true, false, 2),
        e('m', 'DejaVu Sans', true, false, 3),
        e('ó', 'DejaVu Sans', true, false, 4),
        e('𠀀', 'Noto Sans CJK SC', false, true, 5),
      ]),
    ).toEqual([
      { text: '模板', family: 'Noto Sans CJK SC', isFallback: false, hasMissingGlyph: false },
      { text: ' mó', family: 'DejaVu Sans', isFallback: true, hasMissingGlyph: false },
      { text: '𠀀', family: 'Noto Sans CJK SC', isFallback: false, hasMissingGlyph: true },
    ]);
    expect(fontRuns([e(' ', 'X')])).toEqual([]);
    // 后端只列出回退和缺字的元素：序号不相邻的不合并。
    expect(fontRuns([e('模', 'A', true, false, 0), e('喜', 'A', true, false, 5)]).map((r) => r.text)).toEqual(['模', '喜']);
    const s = (text: string, utf16Start: number, actualFamily: string, isFallback: boolean, hasMissingGlyph: boolean) => ({ elementIndex: utf16Start, utf16Start, text, actualFamily, isFallback, hasMissingGlyph });
    expect(markText('ab龘𠀀c', [s('龘', 2, 'CJK', true, false), s('𠀀', 3, 'CJK', false, true)])).toEqual([
      { text: 'ab', status: 'main' },
      { text: '龘', status: 'fallback', family: 'CJK' },
      { text: '𠀀', status: 'missing', family: 'CJK' },
      { text: 'c', status: 'main' },
    ]);
    expect(markText('ab', [])).toEqual([{ text: 'ab', status: 'main' }]);
    expect(fontCheckText('  ')).toBe(FONT_CHECK_SAMPLE);
    expect(fontCheckText(' 龘 ')).toBe(`${FONT_CHECK_SAMPLE} 龘`);
  });

  it('目录读一次；失败后等重试；作废后重读，作废前的请求回来不写状态', async () => {
    const calls: ((value: { families: string[]; environment: string } | Error) => void)[] = [];
    const client = {
      fonts: {
        list: () =>
          new Promise((resolve, reject) => calls.push((v) => (v instanceof Error ? reject(v) : resolve({ ...v, protocolVersion: '1' })))),
      },
    } as unknown as MakoClient;
    const catalog = new FontCatalog(client);
    catalog.ensure();
    catalog.ensure();
    expect(calls).toHaveLength(1);
    expect(catalog.state.status).toBe('loading');
    calls[0]!(new Error('断开'));
    await Promise.resolve();
    await Promise.resolve();
    expect(catalog.state).toMatchObject({ status: 'failed', error: '断开' });
    catalog.ensure();
    expect(calls).toHaveLength(1);
    catalog.retry();
    expect(calls).toHaveLength(2);
    catalog.forget();
    calls[1]!({ families: ['Old'], environment: 'w' });
    await Promise.resolve();
    expect(catalog.state.status).toBe('idle');
    catalog.ensure();
    calls[2]!({ families: ['b', 'a', 'b'], environment: 'worker-skia' });
    await Promise.resolve();
    await Promise.resolve();
    expect(catalog.state).toEqual({ status: 'ready', families: ['a', 'b'], environment: 'worker-skia' });
  });
});
