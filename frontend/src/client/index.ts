// 协议客户端库，与 UI 框架无关：本目录不得 import react 或任何 UI 代码，只依赖 ../protocol。
// 模块划分见 docs/web-client-w0-design.md §3；依赖边界由 test/unit/dependency-boundaries.test.ts 检查。

export { PROTOCOL_VERSION } from '../protocol';
export * from './binary';
export * from './bootstrap';
export * from './clock';
export * from './command-queue';
export * from './errors';
export * from './event-stream';
export * from './identity-store';
export * from './job-watcher';
export * from './mako-client';
export * from './observable';
export * from './redact';
export * from './retry';
export * from './session';
export * from './sse-parser';
export * from './transport';
