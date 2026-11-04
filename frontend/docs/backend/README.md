# 后端文档副本

本 App 的后端源码位于本仓库 `backend/`。`contract/` 五个文件整体复制自 `backend/web/`，行为文档 `backend-api.md` 复制自 `backend/docs/architecture/backend-api.md`。

当前合同来源：App 后端源码提交 `dc6a2e9`（2026-11-04），冻结合同 **1.43.0**、产品 **0.111.3**、HTTP 协议 `"1"`，源码基线 `dc6a2e9065d36900b28548ec94606736092075fb`。本次同步 SDK2 作者来源编译、显式采用与 GPU 准入：最新源码、编译结果、已采用与在用来源分别固定，编译成功后仍须显式采用，配置写入不能绕过成功编译门禁。SDK2 工程和作者预览、正式验证与导出拒绝 CPU，并返回 `commandNotSupported/PackageBackendUnsupported` 和 `field:renderBackend`。工程/歌词/模板 schema 为30/18/8。公共 HTTP 入口和浏览器静态预览协议保持；合同同步、前端自动化检查、匹配部署和真实浏览器验收分别记录。

- `contract/` 不得手改；其 SHA-256 由 `contract-manifest.json` 和前端合同测试核对。升级时整体复制五个文件与行为文档，并同步 `src/protocol/index.ts` 的版本常量。
- App 根 `pnpm build` / `pnpm build:desktop` 发布本仓库匹配 Server 与 Worker，`pnpm desktop` 仅运行经过 native manifest 校验的本地部署。旧上游 artifacts 路径不再是 App 运行入口。
- 本目录其余交接、工作流和历史审查文档保留来源记录；与本仓库实时源码及冻结合同不一致时，以后者为准。
