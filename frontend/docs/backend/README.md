# 后端文档副本

本 App 的后端源码位于本仓库 `backend/`。`contract/` 五个文件整体复制自 `backend/web/`，行为文档 `backend-api.md` 复制自 `backend/docs/architecture/backend-api.md`。

当前合同来源：App 后端源码提交 `c8c8a24`（2026-11-04），冻结合同 **1.44.1**、产品 **0.112.2**、HTTP 协议 `"1"`，源码基线 `c8c8a2432b840bd1fa5bf1075033706831106247`。本次接通 SDK2 精确连续／Burst 粒子及晚期图元，固定出生窗、最终文字锚点与预算失败诊断；仍使用既有作者、预览、验证和导出入口，HTTP DTO 与数量保持。作者源码编译与显式采用、精确修订/CAS、GPU 准入保持：SDK2 的 CPU 选择返回 `commandNotSupported/PackageBackendUnsupported`。工程/歌词/模板 schema 为30/18/8。合同同步、前端自动化检查、匹配部署和实机验收分别登记。

- `contract/` 不得手改；其 SHA-256 由 `contract-manifest.json` 和前端合同测试核对。升级时整体复制五个文件与行为文档，并同步 `src/protocol/index.ts` 的版本常量。
- App 根 `pnpm build` / `pnpm build:desktop` 发布本仓库匹配 Server 与 Worker，`pnpm desktop` 仅运行经过 native manifest 校验的本地部署。旧上游 artifacts 路径不再是 App 运行入口。
- 本目录其余交接、工作流和历史审查文档保留来源记录；与本仓库实时源码及冻结合同不一致时，以后者为准。
