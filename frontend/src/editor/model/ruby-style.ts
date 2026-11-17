import type { StyleCommand, StylePropertyPath, StyleTarget } from '../../protocol';

export const RUBY_VALUE_MAX = 3.4028234663852886e38;

export type RubyProperty = Extract<StylePropertyPath, 'ruby.relativeScale' | 'ruby.gapPx'>;
export type RubyMode = 'inherit' | 'lyrics' | 'custom';

/** 恢复继承与显式跟随歌词是不同的样式操作。 */
export function rubyStyleCommand(target: StyleTarget, property: RubyProperty, mode: RubyMode, value: number): StyleCommand | undefined {
  if (mode === 'inherit') return { type: 'styles.restore', targets: [target], properties: [property] };
  if (mode === 'lyrics') return { type: 'styles.patch', targets: [target], changes: [{ property, value: null }] };
  if (!Number.isFinite(value) || value > RUBY_VALUE_MAX || (property === 'ruby.relativeScale' ? value <= 0 : value < 0)) return undefined;
  return { type: 'styles.patch', targets: [target], changes: [{ property, value }] };
}
