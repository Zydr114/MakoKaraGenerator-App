// 后端协议的手写 TypeScript 类型，对应后端仓库 src/MakoKaraGenerator.Backend.Contracts，
// 形状以冻结合同 backend/web/（OpenAPI 1.58.0，产品 0.116.1）为准。
// 源码基线和文件指纹见合同 manifest。
// 设计约定见 docs/web-client-w0-design.md §5。本目录不依赖任何其他模块。

/** 后端 BackendProtocol.Version。 */
export const PROTOCOL_VERSION = '1';
/** 本项目固定消费的冻结 API 合同版本与对应产品版本。 */
export const CONTRACT_VERSION = '1.59.0';
export const CONTRACT_PRODUCT_VERSION = '0.116.1';

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
export * from './author-source-inspection';
export * from './author-history';
export * from './script-configurations';
export * from './fonts';
export * from './template-transfer';
export * from './waveform';
export * from './jobs';
export * from './render-jobs';
export * from './lyrics-export';
export * from './preview';
export * from './preview-inspection';
export * from './gpu-preview';
export * from './events';
