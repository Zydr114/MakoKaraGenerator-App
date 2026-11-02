// 工作区布局（界面改版 §2.6）：本机记的栏宽与时间轴高度，窗口宽度断点。

import { describe, expect, it } from 'vitest';
import { SIDE_LIMITS, isOverlaySide, parseLayout, widthMode } from '../../src/editor/workspace/layout-prefs';

describe('工作区尺寸', () => {
  it('没有记录或记录坏了时用默认值，时间轴是自然高度', () => {
    const initial = { left: SIDE_LIMITS.left.initial, right: SIDE_LIMITS.right.initial, timeline: undefined };
    expect(parseLayout(null)).toEqual(initial);
    expect(parseLayout('{坏')).toEqual(initial);
    expect(parseLayout('[1,2]')).toEqual(initial);
    expect(parseLayout('{"left":"300","timeline":null}')).toEqual(initial);
  });

  it('越界的值夹到范围里，小数取整', () => {
    expect(parseLayout('{"left":100,"right":999,"timeline":180.6}')).toEqual({ left: 240, right: 440, timeline: 181 });
    expect(parseLayout('{"timeline":5000}').timeline).toBe(320);
  });
});

describe('宽度断点', () => {
  it('≥1440 两栏常驻，1200～1439 右栏覆盖，更窄时两栏都覆盖', () => {
    expect(widthMode(1440)).toBe('docked');
    expect(widthMode(1439)).toBe('rightOverlay');
    expect(widthMode(1200)).toBe('rightOverlay');
    expect(widthMode(1199)).toBe('bothOverlay');
    expect(isOverlaySide('rightOverlay', 'left')).toBe(false);
    expect(isOverlaySide('rightOverlay', 'right')).toBe(true);
    expect(isOverlaySide('bothOverlay', 'left')).toBe(true);
  });
});
