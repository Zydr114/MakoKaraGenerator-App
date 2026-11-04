# 后端文档副本

本 App 的后端源码位于本仓库 `backend/`。`contract/` 五个文件整体复制自 `backend/web/`，行为文档 `backend-api.md` 复制自 `backend/docs/architecture/backend-api.md`。

当前副本：App 后端源码提交 `cf1a055`（2026-11-04），冻结合同 **1.42.4**、产品 **0.110.10**、HTTP 协议 `"1"`，源码基线 `6284dddc1a020b30b519118cfab9cd4b5a845415`。本次同步 M06 背景视频按目标采样、拒帧所有权修复及 Vulkan 采样保护后的整套合同；CPU 保留符合几何门槛的目标采样，Vulkan 为保持主画面与背景捕获像素一致而使用原分辨率，不宣称 GPU 解码或搬运字节缩减。公共 HTTP 入口和浏览器静态预览协议保持；合同同步、前端自动化检查、匹配部署和真实浏览器验收分别记录。

- `contract/` 不得手改；其 SHA-256 由 `contract-manifest.json` 和前端合同测试核对。升级时整体复制五个文件与行为文档，并同步 `src/protocol/index.ts` 的版本常量。
- App 根 `pnpm build` / `pnpm build:desktop` 发布本仓库匹配 Server 与 Worker，`pnpm desktop` 仅运行经过 native manifest 校验的本地部署。旧上游 artifacts 路径不再是 App 运行入口。
- 本目录其余交接、工作流和历史审查文档保留来源记录；与本仓库实时源码及冻结合同不一致时，以后者为准。
