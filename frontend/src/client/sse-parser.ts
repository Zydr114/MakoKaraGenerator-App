// 按 WHATWG HTML “Server-sent events” 解析规则实现的增量解析器（设计 §3.5）。纯模块，无 I/O。
// 输入是已经解码的文本分块（调用方用 TextDecoder 的 stream 模式），分块可以在任意字符处切开。

export interface SseMessage {
  /** event 字段；缺省为 message。 */
  event: string;
  /** 多行 data 以 \n 连接。 */
  data: string;
  /** 分发时的 last event ID 缓冲（id 字段跨事件保留）。 */
  id: string | undefined;
}

export interface SseParseResult {
  messages: SseMessage[];
  /** 以 : 开头的注释行（去掉冒号），例如服务端的 heartbeat。 */
  comments: string[];
}

export class SseParser {
  private buffer = '';
  private sawFirstChunk = false;
  private skipLeadingLf = false;
  private eventType = '';
  private dataLines: string[] = [];
  private hasData = false;
  private lastEventId: string | undefined;

  push(chunk: string): SseParseResult {
    const result: SseParseResult = { messages: [], comments: [] };
    let text = chunk;
    if (!this.sawFirstChunk && text.length > 0) {
      this.sawFirstChunk = true;
      if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
    }
    if (this.skipLeadingLf && text.length > 0) {
      // 上一块以 CR 结尾：若这一块以 LF 开头，它属于同一个 CRLF。
      if (text[0] === '\n') text = text.slice(1);
      this.skipLeadingLf = false;
    }
    this.buffer += text;

    let start = 0;
    const buffer = this.buffer;
    for (let i = 0; i < buffer.length; i++) {
      const c = buffer[i];
      if (c !== '\n' && c !== '\r') continue;
      this.processLine(buffer.slice(start, i), result);
      if (c === '\r') {
        if (i + 1 < buffer.length) {
          if (buffer[i + 1] === '\n') i++;
        } else {
          this.skipLeadingLf = true;
        }
      }
      start = i + 1;
    }
    this.buffer = buffer.slice(start);
    return result;
  }

  /** 当前 last event ID 缓冲。 */
  get lastId(): string | undefined {
    return this.lastEventId;
  }

  private processLine(line: string, result: SseParseResult): void {
    if (line === '') {
      this.dispatch(result);
      return;
    }
    if (line[0] === ':') {
      result.comments.push(line.slice(1).replace(/^ /, ''));
      return;
    }
    const colon = line.indexOf(':');
    const field = colon === -1 ? line : line.slice(0, colon);
    let value = colon === -1 ? '' : line.slice(colon + 1);
    if (value[0] === ' ') value = value.slice(1);
    switch (field) {
      case 'event':
        this.eventType = value;
        break;
      case 'data':
        this.dataLines.push(value);
        this.hasData = true;
        break;
      case 'id':
        if (!value.includes('\u0000')) this.lastEventId = value;
        break;
      default:
        // retry 与未知字段忽略：重连节奏由客户端自己管理。
        break;
    }
  }

  private dispatch(result: SseParseResult): void {
    if (!this.hasData) {
      this.eventType = '';
      return;
    }
    result.messages.push({
      event: this.eventType || 'message',
      data: this.dataLines.join('\n'),
      id: this.lastEventId,
    });
    this.eventType = '';
    this.dataLines = [];
    this.hasData = false;
  }
}
