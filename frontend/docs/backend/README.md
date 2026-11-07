# 后端文档副本

本 App 的后端源码位于本仓库 `backend/`。`contract/` 五个文件整体复制自 `backend/web/`，行为文档 `backend-api.md` 复制自 `backend/docs/architecture/backend-api.md`。

当前合同来源：App 后端源码提交 `e36ca6210d6a523f2837fc70a046a7b827e7836f`（2026-11-07），冻结合同 **1.47.0**、产品 **0.112.37**、HTTP 协议 `"1"`。本版接通宿主媒体特征轨：SSE `project.mediaFeaturesChanged`（只带 summary，不改修订；前端已订阅并按同一修订重新固定预览输入）、包能力 `media.features`、四个 `MediaFeature*` 失败码，透明输出固定为 NoMedia；HTTP 形状未变。保留作者保留源码的精确声明检查与可选完整八字段配置采用。HTTP 路径／方法／schema 为113／126／598，3528个合成例；工程／歌词／模板schema为30／18／8。匹配部署、真实HTTP／浏览器／导出单独记录，合同同步不等于这些验收完成。

- `contract/` 不得手改；其 SHA-256 由 `contract-manifest.json` 和前端合同测试核对。升级时整体复制五个文件与行为文档，并同步 `src/protocol/index.ts` 的版本常量。
- App 根 `pnpm build` / `pnpm build:desktop` 发布本仓库匹配 Server 与 Worker，`pnpm desktop` 仅运行经过 native manifest 校验的本地部署。旧上游 artifacts 路径不再是 App 运行入口。
- 本目录其余交接、工作流和历史审查文档保留来源记录；与本仓库实时源码及冻结合同不一致时，以后者为准。
