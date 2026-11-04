// 后端协议的手写 TypeScript 类型，对应后端仓库 src/MakoKaraGenerator.Backend.Contracts，
// 形状以冻结合同 docs/backend/contract（OpenAPI 1.44.1，产品 0.112.2）为准。
// 设计约定见 docs/web-client-w0-design.md §5。本目录不依赖任何其他模块。

/** 后端 BackendProtocol.Version。 */
export const PROTOCOL_VERSION = '1';
/** 本项目固定消费的冻结 API 合同版本与对应产品版本。 */
export const CONTRACT_VERSION = '1.44.1';
export const CONTRACT_PRODUCT_VERSION = '0.112.2';

export * from './int64';
export * from './errors';
export * from './common';
export * from './client';
export * from './project';
export * from './editing';
export * from './lyrics-structure';
export * from './style';
export * from './presets';
export * from './orchestration';
export * from './templates';
export * from './effect-packages';
export * from './author';
export * from './script-configurations';
export * from './fonts';
export * from './template-transfer';
export * from './waveform';
export * from './jobs';
export * from './render-jobs';
export * from './lyrics-export';
export * from './preview';
export * from './gpu-preview';
export * from './events';
