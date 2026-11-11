# 后端文档副本

本 App 的后端源码位于本仓库 `backend/`。`contract/` 五个文件整体复制自 `backend/web/`，行为文档 `backend-api.md` 复制自 `backend/docs/architecture/backend-api.md`。

当前合同来源：App 后端源码提交 `974e558`（2026-11-12），冻结合同 **1.49.2**、产品 **0.112.72**、HTTP 协议 `"1"`。本版只改行为、不改形状：不透明预览缺背景媒体（未设置、文件丢失或该时刻无帧）时不再整帧失败，图效果旁路为原样、材质退回包内基础外观，诊断列表给出 `code:"BackgroundMediaUnavailable"` 的 warning 并在 message 写明原因；可选背景媒体输入以工程底色代替；导出与静态渲染会话仍把图／材质诊断当失败。保留 1.49.1 试样接入宿主背景分析、straight 预览固定透明无媒体与分析失败退避重试。保留 1.49.0 作者草稿整份替换包内图片与字体、1.48.0 按帧检查投影、1.47.0 宿主媒体特征轨与作者源码检查。HTTP 路径／方法／schema 为115／128／618，3612个合成例；工程／歌词／模板schema为30／18／8。匹配部署、真实HTTP／浏览器／导出单独记录，合同同步不等于这些验收完成。

- `contract/` 不得手改；其 SHA-256 由 `contract-manifest.json` 和前端合同测试核对。升级时整体复制五个文件与行为文档，并同步 `src/protocol/index.ts` 的版本常量。
- App 根 `pnpm build` / `pnpm build:desktop` 发布本仓库匹配 Server 与 Worker，`pnpm desktop` 仅运行经过 native manifest 校验的本地部署。旧上游 artifacts 路径不再是 App 运行入口。
- 本目录其余交接、工作流和历史审查文档保留来源记录；与本仓库实时源码及冻结合同不一致时，以后者为准。
