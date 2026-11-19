# 后端文档副本

本 App 的后端源码位于本仓库 `backend/`。`contract/` 五个文件整体复制自 `backend/web/`，行为文档 `backend-api.md` 复制自 `backend/docs/architecture/backend-api.md`。

当前合同来源：App 后端源码提交 `7411baf5fcd782c3bac18f9a0f130657f61dd966`（2026-11-19），冻结合同 **1.57.0**、产品 **0.116.0**、HTTP 协议 `"1"`。放开桌面端连续预览流 `api` 为 `"vulkan"|"cpu"` 并在 `GpuPreviewStreamState` 新增必需字段 `transport`（`"dma-buf"|"shm"`），严守 `MAKO_PREVIEW_TRANSPORT` 零静默降级语义。工程／歌词／模板格式为32／20／10，旧格式按严格版本保护，旧个人模板库原位保留。HTTP 路径／方法／schema 为115／128／679，4023个合成例（2890反例）。匹配部署、真实 HTTP、浏览器和平台核对分别记录，合同同步不等于这些验收完成。

用真实歌词样本在本机核对时，把文件交给 TTML 导入（新建工程会保留全部分支；已有工程选“导入全部来源”）。预期是 56 行、主唱 53 行、和声 3 行、113 组注音、255 个带时间的注音片段，正文和时间与原文件逐项一致。仓库里的 `synthetic-amll-two-singers.ttml` 只复现这个统计形状，不是那份真实歌词。

- `contract/` 不得手改；其 SHA-256 由 `contract-manifest.json` 和前端合同测试核对。升级时整体复制五个文件与行为文档，并同步 `src/protocol/index.ts` 的版本常量。
- App 根 `pnpm build` / `pnpm build:desktop` 发布本仓库匹配 Server 与 Worker，`pnpm desktop` 仅运行经过 native manifest 校验的本地部署。旧上游 artifacts 路径不再是 App 运行入口。
- 本目录其余交接、工作流和历史审查文档保留来源记录；与本仓库实时源码及冻结合同不一致时，以后者为准。
