// W5 工程设置里的媒体（W5 设计 §2.5）：背景种类判断、大小、资源状态、工程名预检。

import { describe, expect, it } from 'vitest';
import { acceptsFile, audioResource, audioStatusText, backgroundKindOf, backgroundResource, checkProjectName, formatBytes, resourceUsable } from '../../src/editor/model/media';
import type { ProjectResource } from '../../src/protocol';

const resource = (overrides: Partial<ProjectResource>): ProjectResource => ({ resourceId: 'r', kind: 'media', roles: [], status: 'available', ...overrides });

describe('工程设置的媒体', () => {
  it('背景种类先看 MIME，没有类型时看扩展名', () => {
    expect(backgroundKindOf({ name: 'a.bin', type: 'image/png' })).toBe('image');
    expect(backgroundKindOf({ name: 'clip.MOV', type: '' })).toBe('video');
    expect(backgroundKindOf({ name: 'song.wav', type: 'audio/wav' })).toBeUndefined();
  });

  it('大小写成 B、KB、MB', () => {
    expect([formatBytes('512'), formatBytes(95_437), formatBytes('1258291'), formatBytes(undefined)]).toEqual(['512 B', '93.2 KB', '1.2 MB', undefined]);
  });

  it('按角色找音频和背景；状态文字只在不可用时写出资源状态', () => {
    const list = [resource({ resourceId: 'a', roles: ['audio'], preparationStatus: 'prepared', waveformStatus: 'available' }), resource({ resourceId: 'b', roles: ['background'], status: 'missing' })];
    expect(audioResource(list)?.resourceId).toBe('a');
    expect(backgroundResource(list, 'b')?.resourceId).toBe('b');
    expect(backgroundResource(list, undefined)?.resourceId).toBe('b');
    expect(audioStatusText(list[0]!)).toBe('已准备 · 波形可用');
    expect(resourceUsable(list[1]!)).toBe(false);
  });

  it('工程名：空白不行，原输入超过 256 个字符不行', () => {
    expect(checkProjectName('  ')).toMatch(/不能为空/);
    expect(checkProjectName('a'.repeat(256))).toBeUndefined();
    expect(checkProjectName(` ${'a'.repeat(256)}`)).toMatch(/太长/);
  });

  it('拖进来的文件按选择范围检查：扩展名、MIME 和通配', () => {
    const accept = 'audio/*,.wav,.LRC';
    expect(acceptsFile({ name: 'a.flac', type: 'audio/flac' }, accept)).toBe(true);
    expect(acceptsFile({ name: 'B.WAV', type: '' }, accept)).toBe(true);
    expect(acceptsFile({ name: 'c.lrc', type: '' }, accept)).toBe(true);
    expect(acceptsFile({ name: 'd.png', type: 'image/png' }, accept)).toBe(false);
    expect(acceptsFile({ name: 'e.json', type: 'application/json' }, 'application/json')).toBe(true);
  });
});
