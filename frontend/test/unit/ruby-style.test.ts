import { describe, expect, it } from 'vitest';
import { rubyStyleCommand } from '../../src/editor/model/ruby-style';
import { describeValue } from '../../src/editor/model/style-overrides';

const target = { kind: 'line' as const, branchId: 'b', lineId: 'line-1' };

describe('句子注音样式', () => {
  it('清除本层继承与显式回到歌词值使用不同命令', () => {
    expect(rubyStyleCommand(target, 'ruby.relativeScale', 'inherit', 0.5)).toEqual({ type: 'styles.restore', targets: [target], properties: ['ruby.relativeScale'] });
    const lyrics = rubyStyleCommand(target, 'ruby.relativeScale', 'lyrics', 0.5);
    expect(lyrics).toEqual({ type: 'styles.patch', targets: [target], changes: [{ property: 'ruby.relativeScale', value: null }] });
    expect(JSON.stringify(lyrics)).toContain('"value":null');
  });

  it('数字设置允许零间距，并拒绝非法字号、负间距及非有限数字', () => {
    expect(rubyStyleCommand(target, 'ruby.gapPx', 'custom', 0)).toMatchObject({ changes: [{ property: 'ruby.gapPx', value: 0 }] });
    expect(rubyStyleCommand(target, 'ruby.relativeScale', 'custom', 0.75)).toMatchObject({ changes: [{ property: 'ruby.relativeScale', value: 0.75 }] });
    expect(rubyStyleCommand(target, 'ruby.relativeScale', 'custom', 0)).toBeUndefined();
    expect(rubyStyleCommand(target, 'ruby.gapPx', 'custom', -1)).toBeUndefined();
    expect(rubyStyleCommand(target, 'ruby.gapPx', 'custom', Infinity)).toBeUndefined();
    expect(rubyStyleCommand(target, 'ruby.gapPx', 'custom', 3.5e38)).toBeUndefined();
    expect(rubyStyleCommand(target, 'ruby.relativeScale', 'custom', NaN)).toBeUndefined();
  });

  it('来源中区分歌词原值、字号百分比和像素间距', () => {
    expect(describeValue('ruby.relativeScale', null).text).toBe('跟随歌词中的注音设置');
    expect(describeValue('ruby.relativeScale', 0.625).text).toBe('62.5%');
    expect(describeValue('ruby.gapPx', 0).text).toBe('0 px');
  });
});
