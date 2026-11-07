# 后端文档副本

本 App 的后端源码位于本仓库 `backend/`。`contract/` 五个文件整体复制自 `backend/web/`，行为文档 `backend-api.md` 复制自 `backend/docs/architecture/backend-api.md`。

当前合同来源：App 后端源码提交 `2406703`（2026-11-07），冻结合同 **1.48.0**、产品 **0.112.38**、HTTP 协议 `"1"`。本版新增按帧检查投影：帧请求可选 `inspect`，帧元数据可选 `inspectionUrl`，新增 `GET /api/v1/preview-frames/{id}/inspection`（未开启 404 `previewInspectionUnavailable`），PNG 与导出不变；样式库试样画布的调试叠加层与成本视图使用它。保留 1.47.0 的宿主媒体特征轨与作者源码检查。HTTP 路径／方法／schema 为114／127／613，3561个合成例；工程／歌词／模板schema为30／18／8。匹配部署、真实HTTP／浏览器／导出单独记录，合同同步不等于这些验收完成。

- `contract/` 不得手改；其 SHA-256 由 `contract-manifest.json` 和前端合同测试核对。升级时整体复制五个文件与行为文档，并同步 `src/protocol/index.ts` 的版本常量。
- App 根 `pnpm build` / `pnpm build:desktop` 发布本仓库匹配 Server 与 Worker，`pnpm desktop` 仅运行经过 native manifest 校验的本地部署。旧上游 artifacts 路径不再是 App 运行入口。
- 本目录其余交接、工作流和历史审查文档保留来源记录；与本仓库实时源码及冻结合同不一致时，以后者为准。
