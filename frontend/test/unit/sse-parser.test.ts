import { describe, expect, it } from 'vitest';
import { SseParser, type SseMessage } from '../../src/client';

function parseAll(chunks: string[]): { messages: SseMessage[]; comments: string[] } {
  const parser = new SseParser();
  const messages: SseMessage[] = [];
  const comments: string[] = [];
  for (const chunk of chunks) {
    const result = parser.push(chunk);
    messages.push(...result.messages);
    comments.push(...result.comments);
  }
  return { messages, comments };
}

const SAMPLE = 'id: 7\nevent: project.changed\ndata: {"eventSeq":"7"}\n\n';

describe('SSE 解析器', () => {
  it('解析 id／event／data 并在空行分发', () => {
    expect(parseAll([SAMPLE]).messages).toEqual([{ event: 'project.changed', data: '{"eventSeq":"7"}', id: '7' }]);
  });

  it('任意分块边界得到相同结果', () => {
    const whole = parseAll([SAMPLE + SAMPLE.replace('7', '8')]).messages;
    for (let size = 1; size <= 7; size++) {
      const chunks: string[] = [];
      const text = SAMPLE + SAMPLE.replace('7', '8');
      for (let i = 0; i < text.length; i += size) chunks.push(text.slice(i, i + size));
      expect(parseAll(chunks).messages).toEqual(whole);
    }
  });

  it('多行 data 用换行连接', () => {
    expect(parseAll(['data: a\ndata: b\ndata\n\n']).messages[0]?.data).toBe('a\nb\n');
  });

  it('支持 CRLF 与单独的 CR，包括 CR 和 LF 落在不同分块', () => {
    const crlf = parseAll(['event: x\r\ndata: 1\r', '\n\r\n']).messages;
    expect(crlf).toEqual([{ event: 'x', data: '1', id: undefined }]);
    const cr = parseAll(['event: y\rdata: 2\r\r']).messages;
    expect(cr).toEqual([{ event: 'y', data: '2', id: undefined }]);
  });

  it('注释行不分发事件，但交给调用方（用于活性检测）', () => {
    const result = parseAll([': heartbeat\n\n', SAMPLE]);
    expect(result.comments).toEqual(['heartbeat']);
    expect(result.messages).toHaveLength(1);
  });

  it('没有 data 的块不分发；event 缺省为 message；冒号后只去掉一个空格', () => {
    const result = parseAll(['event: lonely\n\n', 'data:  two spaces\n\n']).messages;
    expect(result).toEqual([{ event: 'message', data: ' two spaces', id: undefined }]);
  });

  it('id 跨事件保留；含 NUL 的 id 被忽略；去掉开头 BOM', () => {
    const result = parseAll(['﻿id: 1\ndata: a\n\n', 'data: b\n\n', 'id: x\u0000y\ndata: c\n\n']).messages;
    expect(result.map((m) => m.id)).toEqual(['1', '1', '1']);
    expect(result[0]?.event).toBe('message');
  });

  it('resync.required 控制事件按普通事件分发，由调用方识别', () => {
    const text = 'event: resync.required\ndata: {"eventCursor":"h:c:0","events":[],"resyncRequired":true}\n\n';
    expect(parseAll([text]).messages[0]?.event).toBe('resync.required');
  });
});
