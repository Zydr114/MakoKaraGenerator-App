# 后端文档副本

本 App 的后端源码位于本仓库 `backend/`。`contract/` 五个文件整体复制自 `backend/web/`，行为文档 `backend-api.md` 复制自 `backend/docs/architecture/backend-api.md`。

当前合同来源：App 后端源码提交 `907a449`（2026-11-17，完整源码基线见 manifest），冻结合同 **1.55.1**、产品 **0.114.1**、HTTP 协议 `"1"`。1.55.1 保留完整视频帧计划，短音轨补静音、长音轨截断、无音频工程统一纯视频；报告继续区分真实源时长与成片音轨时长，损坏与解码检查保留，音频分析沿用缺失语义。0.114.1 明确兼容读取同格式 0.114.0 工程，读取不改写文件。1.55.0 补齐标准 TTML 与多歌唱者导入：明确 AMLL/Apple 标记下可确认的空命名空间正文会在内存中恢复并报告；agent 与旁轨映射为具名分支；`lyrics.importAsset` 增加 `destination.kind=allSources`，一次追加全部分支且不替换已有来源。继续保留 1.54.0 的前奏图形／SVG／动画与 `CueTargetAboveStart`。HTTP 路径／方法／schema 为115／128／673，4000个合成例（2881反例）。匹配部署、真实 HTTP、浏览器和用真实歌词样本的最终核对单独记录，合同同步不等于这些验收完成。

用真实歌词样本在本机核对时，把文件交给 TTML 导入（新建工程会保留全部分支；已有工程选“导入全部来源”）。预期是 56 行、主唱 53 行、和声 3 行、113 组注音、255 个带时间的注音片段，正文和时间与原文件逐项一致。仓库里的 `synthetic-amll-two-singers.ttml` 只复现这个统计形状，不是那份真实歌词。

- `contract/` 不得手改；其 SHA-256 由 `contract-manifest.json` 和前端合同测试核对。升级时整体复制五个文件与行为文档，并同步 `src/protocol/index.ts` 的版本常量。
- App 根 `pnpm build` / `pnpm build:desktop` 发布本仓库匹配 Server 与 Worker，`pnpm desktop` 仅运行经过 native manifest 校验的本地部署。旧上游 artifacts 路径不再是 App 运行入口。
- 本目录其余交接、工作流和历史审查文档保留来源记录；与本仓库实时源码及冻结合同不一致时，以后者为准。
