// W5 导出歌词文件（W5 设计 §1）：评估请求的拼装、元数据检查、损失文字、文件名。

import { describe, expect, it } from 'vitest';
import { checkMetadata, lossText, lyricsFileName, planInput, type LyricsExportForm } from '../../src/editor/model/lyrics-export';

const form = (overrides: Partial<LyricsExportForm> = {}): LyricsExportForm => ({
  format: 'lrc.enhanced',
  wholeProject: true,
  branchId: 'b1',
  title: ' 歌名 ',
  artist: '',
  album: '',
  crlf: false,
  bom: false,
  millisecondPrecision: true,
  ...overrides,
});

describe('导出歌词文件', () => {
  it('LRC 带来源、标题和毫秒精度，空的标签省略', () => {
    expect(planInput(form())).toEqual({
      format: 'lrc.enhanced',
      scope: 'branch',
      branchId: 'b1',
      options: { newLine: '\n', writeByteOrderMark: false, millisecondPrecision: true, title: '歌名' },
    });
  });

  it('TTML 整个工程不带 branchId；TTML 不写标签和毫秒精度', () => {
    expect(planInput(form({ format: 'ttml.project', crlf: true, bom: true }))).toEqual({
      format: 'ttml.project',
      scope: 'document',
      options: { newLine: '\r\n', writeByteOrderMark: true, millisecondPrecision: false },
    });
    expect(planInput(form({ format: 'ttml.project', wholeProject: false }))).toMatchObject({ scope: 'branch', branchId: 'b1' });
    expect(planInput(form({ format: 'ttml.amll' }))).toMatchObject({ scope: 'branch', branchId: 'b1' });
  });

  it('标签不能含控制字符或“]”，最多 1024 个字符', () => {
    expect(checkMetadata('歌名')).toBeUndefined();
    expect(checkMetadata('a]b')).toMatch(/控制字符/);
    expect(checkMetadata('a\u0007')).toMatch(/控制字符/);
    expect(checkMetadata('a'.repeat(1025))).toMatch(/1024/);
  });

  it('补充损失的英文键换成中文；文件名用工程名', () => {
    expect(lossText({ feature: 'lineEndTimes', affectedCount: 2, message: 'LRC仅写行起点。' })).toBe('行终点（2 处）：LRC仅写行起点。');
    expect(lossText({ feature: '逐字时间', affectedCount: 0, message: 'x' })).toBe('逐字时间：x');
    expect(lyricsFileName('MakoKara-lyrics.lrc', '我的歌', 'lrc.plain')).toBe('我的歌.lrc');
    expect(lyricsFileName('MakoKara-lyrics.ttml', ' ', 'ttml.amll')).toBe('MakoKara-lyrics.ttml');
  });
});
