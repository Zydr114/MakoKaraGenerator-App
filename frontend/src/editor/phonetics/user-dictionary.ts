// 本地用户读音词典（最高优先级覆盖自动注音）：
// 支持 CRUD、本地持久存储，以及导入/导出 StrangeUtaGame / RL 外部用户词典格式。

import {
  formatRubySegments,
  hasExplicitRubySeparator,
  splitExplicitRubySegments,
  splitJapaneseMora,
  type MoraSplitOptions,
} from './mora-splitter';

export interface UserDictionaryEntry {
  surface: string;
  segments: string[];
  note?: string;
}

const STORAGE_KEY = 'mako.phonetics.userDictionary.v1';

let memoryFallback: UserDictionaryEntry[] = [];

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

export function normalizeUserEntries(entries: readonly UserDictionaryEntry[]): UserDictionaryEntry[] {
  const bySurface = new Map<string, UserDictionaryEntry>();
  for (const entry of entries) {
    const surface = entry.surface.trim();
    const segments = entry.segments.map((s) => s.trim()).filter((s) => s.length > 0);
    if (!surface || segments.length === 0) continue;
    bySurface.set(surface, {
      surface,
      segments,
      ...(entry.note?.trim() ? { note: entry.note.trim() } : {}),
    });
  }
  // Sort longest surface first so longest-match lookup is deterministic.
  return [...bySurface.values()].sort((a, b) => b.surface.length - a.surface.length || a.surface.localeCompare(b.surface));
}

export function loadUserDictionary(): UserDictionaryEntry[] {
  const storage = getStorage();
  if (!storage) return [...memoryFallback];
  try {
    const raw = storage.getItem(STORAGE_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw) as unknown;
    if (!Array.isArray(parsed)) return [];
    return normalizeUserEntries(
      parsed.filter(
        (item): item is UserDictionaryEntry =>
          typeof item === 'object' &&
          item !== null &&
          typeof (item as UserDictionaryEntry).surface === 'string' &&
          Array.isArray((item as UserDictionaryEntry).segments),
      ),
    );
  } catch {
    return [];
  }
}

export function saveUserDictionary(entries: readonly UserDictionaryEntry[]): UserDictionaryEntry[] {
  const normalized = normalizeUserEntries(entries);
  const storage = getStorage();
  if (!storage) {
    memoryFallback = normalized;
    return normalized;
  }
  try {
    storage.setItem(STORAGE_KEY, JSON.stringify(normalized));
  } catch {
    memoryFallback = normalized;
  }
  return normalized;
}

export function upsertUserDictionaryEntry(entry: UserDictionaryEntry): UserDictionaryEntry[] {
  const current = loadUserDictionary();
  const next = current.filter((item) => item.surface !== entry.surface.trim());
  next.push(entry);
  return saveUserDictionary(next);
}

export function removeUserDictionaryEntry(surface: string): UserDictionaryEntry[] {
  const target = surface.trim();
  return saveUserDictionary(loadUserDictionary().filter((item) => item.surface !== target));
}

/**
 * 解析 StrangeUtaGame / RL 或 JSON 格式的用户读音词典文本：
 * - JSON 数组：`[{"surface":"二人","segments":["ふ","た","り"]}]`
 * - 文本行格式（忽略 `#` / `//` 注释与空行）：
 *   `表层形<TAB>ふ,た,り`、`表层形=ふ・た・り`、`表层形 ふたり`
 */
export function parseExternalUserDictionary(rawText: string, moraOptions: MoraSplitOptions = {}): UserDictionaryEntry[] {
  const trimmed = rawText.trim();
  if (!trimmed) return [];

  if (trimmed.startsWith('[')) {
    const parsed = JSON.parse(trimmed) as unknown;
    if (!Array.isArray(parsed)) throw new Error('JSON 词典必须是数组格式。');
    return normalizeUserEntries(
      parsed.map((item) => {
        const obj = item as Record<string, unknown>;
        const surface = typeof obj.surface === 'string' ? obj.surface : '';
        const segments = Array.isArray(obj.segments)
          ? obj.segments.filter((s): s is string => typeof s === 'string')
          : typeof obj.reading === 'string'
            ? hasExplicitRubySeparator(obj.reading)
              ? splitExplicitRubySegments(obj.reading)
              : splitJapaneseMora(obj.reading, moraOptions)
            : [];
        return {
          surface,
          segments,
          ...(typeof obj.note === 'string' ? { note: obj.note } : {}),
        };
      }),
    );
  }

  const entries: UserDictionaryEntry[] = [];
  for (const rawLine of trimmed.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith('#') || line.startsWith('//') || line.startsWith(';')) continue;

    let surface = '';
    let readingPart = '';
    let note: string | undefined;

    if (line.includes('\t')) {
      const cols = line.split('\t');
      surface = (cols[0] ?? '').trim();
      readingPart = (cols[1] ?? '').trim();
      note = cols[2]?.trim() || undefined;
    } else if (line.includes('=')) {
      const eq = line.indexOf('=');
      surface = line.slice(0, eq).trim();
      readingPart = line.slice(eq + 1).trim();
    } else {
      const match = /^(\S+)\s+(.+)$/.exec(line);
      if (!match) continue;
      surface = match[1]!.trim();
      readingPart = match[2]!.trim();
    }

    if (!surface || !readingPart) continue;
    const segments = hasExplicitRubySeparator(readingPart)
      ? splitExplicitRubySegments(readingPart)
      : splitJapaneseMora(readingPart, { ...moraOptions, isKanjiBase: true });
    if (segments.length === 0) continue;
    entries.push({ surface, segments, ...(note ? { note } : {}) });
  }

  return normalizeUserEntries(entries);
}

/** 导出为 StrangeUtaGame / RL 兼容的制表符纯文本格式。 */
export function exportUserDictionaryText(entries: readonly UserDictionaryEntry[]): string {
  const lines = [
    '# MakoKaraGenerator 用户读音词典（兼容 StrangeUtaGame / RL 格式）',
    '# 每行格式：表层形<TAB>拍1・拍2・拍3',
  ];
  for (const entry of normalizeUserEntries(entries)) {
    const reading = formatRubySegments(entry.segments);
    lines.push(entry.note ? `${entry.surface}\t${reading}\t${entry.note}` : `${entry.surface}\t${reading}`);
  }
  return lines.join('\n') + '\n';
}
