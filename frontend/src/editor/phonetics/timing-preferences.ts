// 本地持久化的打轴与注音偏好设置：按键延迟补偿、画布字号、F2 暂停策略、长音/拨音合并与键位重绑。

export type TimingActionId =
  | 'togglePlay'
  | 'strikeBeat'
  | 'closeBeat'
  | 'undoBeat'
  | 'clearLineTiming'
  | 'speedDown'
  | 'speedUp'
  | 'seekBack'
  | 'seekForward'
  | 'openRubyPopover'
  | 'prevLine'
  | 'nextLine'
  | 'prevToken'
  | 'nextToken'
  | 'prevBeat'
  | 'nextBeat';

export type ScrollMode = 'follow' | 'lock' | 'free';

export interface TimingPreferences {
  /** 按键延迟补偿（毫秒，落点自动减去该值；默认 0，范围 -500 ~ 500）。 */
  tagOffsetMs: number;
  /** 动态打轴画布当前行正文字号（像素；默认 42，范围 28 ~ 60）。 */
  stageFontSize: number;
  /** 按 F2 弹出原地注音浮窗时是否自动暂停播放（默认 true）。 */
  pauseOnRubyPopover: boolean;
  /** 日语自动注音与罗马音分拍时是否合并汉字内部尾随长音与拨音（默认 false）。 */
  mergeLongAndNasalInKanji: boolean;
  /** 动态画布滚动模式（默认 follow）。 */
  scrollMode: ScrollMode;
  /** 自定义词典镜像基地址（空字符串表示使用默认地址）。 */
  dictMirrorUrl: string;
  /** 自定义快捷键覆盖（键名为 KeyboardEvent.code 或小写按键名）。 */
  keymapOverrides: Partial<Record<TimingActionId, string[]>>;
}

export const DEFAULT_TIMING_PREFERENCES: TimingPreferences = {
  tagOffsetMs: 0,
  stageFontSize: 42,
  pauseOnRubyPopover: true,
  mergeLongAndNasalInKanji: false,
  scrollMode: 'follow',
  dictMirrorUrl: '',
  keymapOverrides: {},
};

const STORAGE_KEY = 'mako.lyrics.timingPreferences.v1';

let memoryPrefs: TimingPreferences = { ...DEFAULT_TIMING_PREFERENCES };

function getStorage(): Storage | undefined {
  try {
    if (typeof globalThis !== 'undefined' && 'localStorage' in globalThis && globalThis.localStorage) {
      return globalThis.localStorage;
    }
  } catch {
    // Ignore restricted storage environments.
  }
  return undefined;
}

export function clampTagOffsetMs(ms: number): number {
  if (!Number.isFinite(ms)) return DEFAULT_TIMING_PREFERENCES.tagOffsetMs;
  return Math.max(-500, Math.min(500, Math.round(ms)));
}

export function clampStageFontSize(px: number): number {
  if (!Number.isFinite(px)) return DEFAULT_TIMING_PREFERENCES.stageFontSize;
  return Math.max(28, Math.min(60, Math.round(px)));
}

function sanitize(raw: Partial<TimingPreferences>): TimingPreferences {
  const scrollMode: ScrollMode =
    raw.scrollMode === 'follow' || raw.scrollMode === 'lock' || raw.scrollMode === 'free'
      ? raw.scrollMode
      : DEFAULT_TIMING_PREFERENCES.scrollMode;

  return {
    tagOffsetMs: clampTagOffsetMs(typeof raw.tagOffsetMs === 'number' ? raw.tagOffsetMs : DEFAULT_TIMING_PREFERENCES.tagOffsetMs),
    stageFontSize: clampStageFontSize(
      typeof raw.stageFontSize === 'number' ? raw.stageFontSize : DEFAULT_TIMING_PREFERENCES.stageFontSize,
    ),
    pauseOnRubyPopover:
      typeof raw.pauseOnRubyPopover === 'boolean' ? raw.pauseOnRubyPopover : DEFAULT_TIMING_PREFERENCES.pauseOnRubyPopover,
    mergeLongAndNasalInKanji:
      typeof raw.mergeLongAndNasalInKanji === 'boolean'
        ? raw.mergeLongAndNasalInKanji
        : DEFAULT_TIMING_PREFERENCES.mergeLongAndNasalInKanji,
    scrollMode,
    dictMirrorUrl: typeof raw.dictMirrorUrl === 'string' ? raw.dictMirrorUrl.trim() : '',
    keymapOverrides:
      raw.keymapOverrides && typeof raw.keymapOverrides === 'object' ? { ...raw.keymapOverrides } : {},
  };
}

export function loadTimingPreferences(): TimingPreferences {
  const storage = getStorage();
  if (!storage) return { ...memoryPrefs };
  try {
    const raw = storage.getItem(STORAGE_KEY);
    if (!raw) return { ...DEFAULT_TIMING_PREFERENCES };
    return sanitize(JSON.parse(raw) as Partial<TimingPreferences>);
  } catch {
    return { ...DEFAULT_TIMING_PREFERENCES };
  }
}

export function saveTimingPreferences(prefs: TimingPreferences): TimingPreferences {
  const clean = sanitize(prefs);
  memoryPrefs = clean;
  const storage = getStorage();
  if (storage) {
    try {
      storage.setItem(STORAGE_KEY, JSON.stringify(clean));
    } catch {
      // Ignore quota errors.
    }
  }
  return clean;
}

export function updateTimingPreferences(patch: Partial<TimingPreferences>): TimingPreferences {
  return saveTimingPreferences({ ...loadTimingPreferences(), ...patch });
}

/** 计算应用按键延迟补偿后的打轴落点微秒（不小于 0）。 */
export function applyTagOffsetUs(rawPlayheadUs: number, tagOffsetMs: number): number {
  return Math.max(0, Math.round(rawPlayheadUs - clampTagOffsetMs(tagOffsetMs) * 1000));
}
