// 界面主题（界面改版 §2.2）：标准角色与 material-color-utilities 一致，偏好解析与回退，跟随系统，首帧缓存。

import { describe, expect, it } from 'vitest';
import { hexFromArgb } from '@material/material-color-utilities';
import { DEFAULT_SEED, STANDARD_ROLES, buildScheme, createScheme, normalizeSeed, schemeCss } from '../../src/editor/theme/scheme';
import { DEFAULT_THEME, THEME_CACHE_KEY, THEME_STORAGE_KEY, ThemeStore, parseThemePrefs } from '../../src/editor/theme/theme-store';

const GETTER: Record<string, string> = { 'surface-low': 'surfaceContainerLow', 'surface-high': 'surfaceContainerHigh', 'surface-highest': 'surfaceContainerHighest' };
const camel = (role: string) => GETTER[role] ?? role.replace(/-(\w)/g, (_, c: string) => c.toUpperCase());

describe('配色', () => {
  it('标志蓝的标准角色与 material-color-utilities 一致', () => {
    for (const dark of [false, true]) {
      for (const vivid of [false, true]) {
        const roles = buildScheme({ seed: DEFAULT_SEED, dark, vivid });
        const scheme = createScheme({ seed: DEFAULT_SEED, dark, vivid }) as unknown as Record<string, number>;
        for (const role of STANDARD_ROLES) expect(roles[role], `${role} dark=${dark} vivid=${vivid}`).toBe(hexFromArgb(scheme[camel(role)]!));
      }
    }
    const light = buildScheme({ seed: DEFAULT_SEED, dark: false, vivid: false });
    const dark = buildScheme({ seed: DEFAULT_SEED, dark: true, vivid: false });
    expect([light.primary, light.tertiary, dark.primary, dark.tertiary]).toEqual(['#35618e', '#6a5778', '#a0cafd', '#d6bee5']);
  });

  it('自定义角色按明暗取色调，变量串带 rgb 三元组', () => {
    const light = buildScheme({ seed: DEFAULT_SEED, dark: false, vivid: false });
    const dark = buildScheme({ seed: DEFAULT_SEED, dark: true, vivid: false });
    expect(light.panel).toBe('#ffffff');
    expect(light['frame-sel']).toBe(dark['frame-sel']);
    expect(schemeCss(light)).toContain('--panel:#ffffff;--panel-rgb:255, 255, 255;');
  });

  it('种子色规范化', () => {
    expect(normalizeSeed(' 019bfe ')).toBe('#019BFE');
    expect(normalizeSeed('#12345')).toBeUndefined();
    expect(normalizeSeed('red')).toBeUndefined();
  });
});

function memoryStorage(initial: Record<string, string> = {}) {
  const map = new Map(Object.entries(initial));
  return { map, getItem: (key: string) => map.get(key) ?? null, setItem: (key: string, value: string) => void map.set(key, value) };
}

function fakeQuery(matches: boolean) {
  const listeners = new Set<() => void>();
  const query = {
    matches,
    addEventListener: (_: string, listener: () => void) => void listeners.add(listener),
    removeEventListener: (_: string, listener: () => void) => void listeners.delete(listener),
    change(next: boolean) {
      query.matches = next;
      for (const listener of listeners) listener();
    },
  };
  return query as typeof query & Pick<MediaQueryList, 'addEventListener' | 'removeEventListener'>;
}

function fakeRoot() {
  const vars = new Map<string, string>();
  return { vars, style: { setProperty: (name: string, value: string) => void vars.set(name, value) }, dataset: {} as DOMStringMap };
}

describe('主题偏好', () => {
  it('逐项回退到默认值', () => {
    expect(parseThemePrefs(null)).toEqual(DEFAULT_THEME);
    expect(parseThemePrefs('not json')).toEqual(DEFAULT_THEME);
    expect(parseThemePrefs('{"mode":"dark","seed":"#ff0000","vivid":true}')).toEqual({ mode: 'dark', seed: '#FF0000', vivid: true });
    expect(parseThemePrefs('{"mode":"dim","seed":"blue","vivid":1}')).toEqual(DEFAULT_THEME);
  });

  it('跟随系统，改偏好时写入本机并更新首帧缓存', () => {
    const storage = memoryStorage();
    const query = fakeQuery(false);
    const root = fakeRoot();
    const store = new ThemeStore(storage, query, root);
    expect(root.dataset.mode).toBe('light');
    expect(root.vars.get('--panel')).toBe('#ffffff');

    query.change(true);
    expect(store.state.dark).toBe(true);
    expect(root.dataset.mode).toBe('dark');

    store.set({ mode: 'light', seed: '8a5cf6' });
    expect(root.dataset.mode).toBe('light');
    expect(JSON.parse(storage.map.get(THEME_STORAGE_KEY)!)).toEqual({ mode: 'light', seed: '#8A5CF6', vivid: false });
    const cache = JSON.parse(storage.map.get(THEME_CACHE_KEY)!) as { light: string; dark: string };
    expect(cache.light).toContain(`--primary:${buildScheme({ seed: '#8A5CF6', dark: false, vivid: false }).primary};`);
    expect(cache.dark).toContain(`--primary:${buildScheme({ seed: '#8A5CF6', dark: true, vivid: false }).primary};`);

    // 选了浅色就不再跟随系统。
    query.change(false);
    query.change(true);
    expect(store.state.dark).toBe(false);
    store.dispose();
  });
});
