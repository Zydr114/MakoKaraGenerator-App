// 界面主题偏好（界面改版 §2.2）：模式、种子色、强度。记在本机 localStorage，对所有工程生效。
// 算好的变量写到 <html> 上；同时缓存两套变量串，index.html 的内联脚本在 React 启动前先写上，首帧不闪。

import { ObservableValue } from '../../client';
import { DEFAULT_SEED, buildScheme, normalizeSeed, schemeCss } from './scheme';

export type ThemeMode = 'auto' | 'light' | 'dark';

export interface ThemePrefs {
  mode: ThemeMode;
  seed: string;
  vivid: boolean;
}

export interface ThemeState extends ThemePrefs {
  /** 实际用的是不是深色（跟随系统时取系统的）。 */
  dark: boolean;
}

export const DEFAULT_THEME: ThemePrefs = { mode: 'auto', seed: DEFAULT_SEED, vivid: false };
export const THEME_STORAGE_KEY = 'mako.theme';
/** 首帧缓存：{ key, light, dark }，每次应用主题时更新。index.html 的内联脚本按明暗取其中一份。 */
export const THEME_CACHE_KEY = 'mako.theme-cache';

const MODES: readonly ThemeMode[] = ['auto', 'light', 'dark'];

/** 逐项解析偏好，哪项不合法就用默认值，整体不合法（不是 JSON 对象）就全用默认值。 */
export function parseThemePrefs(raw: string | null): ThemePrefs {
  let value: unknown;
  try {
    value = raw ? JSON.parse(raw) : undefined;
  } catch {
    value = undefined;
  }
  if (!value || typeof value !== 'object') return { ...DEFAULT_THEME };
  const { mode, seed, vivid } = value as Record<string, unknown>;
  return {
    mode: MODES.includes(mode as ThemeMode) ? (mode as ThemeMode) : DEFAULT_THEME.mode,
    seed: (typeof seed === 'string' && normalizeSeed(seed)) || DEFAULT_THEME.seed,
    vivid: typeof vivid === 'boolean' ? vivid : DEFAULT_THEME.vivid,
  };
}

type ThemeStorage = Pick<Storage, 'getItem' | 'setItem'>;
type ColorSchemeQuery = Pick<MediaQueryList, 'matches' | 'addEventListener' | 'removeEventListener'>;
type ThemeRoot = { style: Pick<CSSStyleDeclaration, 'setProperty'>; dataset: DOMStringMap };

export class ThemeStore {
  readonly state$: ObservableValue<ThemeState>;
  private cache: { key: string; light: string; dark: string } | undefined;

  constructor(
    private readonly storage: ThemeStorage | undefined,
    private readonly query: ColorSchemeQuery | undefined,
    private readonly root: ThemeRoot,
  ) {
    const prefs = parseThemePrefs(storage?.getItem(THEME_STORAGE_KEY) ?? null);
    this.state$ = new ObservableValue<ThemeState>({ ...prefs, dark: this.resolveDark(prefs.mode) });
    this.query?.addEventListener('change', this.onSystemChange);
    this.apply();
  }

  get state(): ThemeState {
    return this.state$.get();
  }

  set(patch: Partial<ThemePrefs>): void {
    const current = this.state;
    const next: ThemePrefs = {
      mode: patch.mode ?? current.mode,
      seed: (patch.seed !== undefined && normalizeSeed(patch.seed)) || current.seed,
      vivid: patch.vivid ?? current.vivid,
    };
    if (next.mode === current.mode && next.seed === current.seed && next.vivid === current.vivid) return;
    this.storage?.setItem(THEME_STORAGE_KEY, JSON.stringify(next));
    this.state$.set({ ...next, dark: this.resolveDark(next.mode) });
    this.apply();
  }

  dispose(): void {
    this.query?.removeEventListener('change', this.onSystemChange);
  }

  private readonly onSystemChange = () => {
    const dark = this.resolveDark(this.state.mode);
    if (dark === this.state.dark) return;
    this.state$.set({ ...this.state, dark });
    this.apply();
  };

  private resolveDark(mode: ThemeMode): boolean {
    return mode === 'dark' || (mode === 'auto' && !!this.query?.matches);
  }

  private apply(): void {
    const { seed, vivid, dark } = this.state;
    const key = `${seed}${vivid ? ':vivid' : ''}`;
    if (this.cache?.key !== key) {
      this.cache = { key, light: schemeCss(buildScheme({ seed, vivid, dark: false })), dark: schemeCss(buildScheme({ seed, vivid, dark: true })) };
      try {
        this.storage?.setItem(THEME_CACHE_KEY, JSON.stringify(this.cache));
      } catch {
        // 存不下只影响首帧，忽略。
      }
    }
    for (const declaration of this.cache[dark ? 'dark' : 'light'].split(';')) {
      const colon = declaration.indexOf(':');
      if (colon > 0) this.root.style.setProperty(declaration.slice(0, colon), declaration.slice(colon + 1));
    }
    this.root.dataset.mode = dark ? 'dark' : 'light';
  }
}

let installed: ThemeStore | undefined;

/** 页面启动时调用一次：读偏好、写变量、跟随系统。 */
export function installTheme(): ThemeStore {
  installed ??= new ThemeStore(localStorage, matchMedia('(prefers-color-scheme: dark)'), document.documentElement);
  return installed;
}

export function themeStore(): ThemeStore {
  if (!installed) throw new Error('主题还没有初始化（installTheme）。');
  return installed;
}
