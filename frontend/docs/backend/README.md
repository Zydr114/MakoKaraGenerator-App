# 后端文档副本

本 App 的后端源码位于本仓库 `backend/`。`contract/` 五个文件整体复制自 `backend/web/`，行为文档 `backend-api.md` 复制自 `backend/docs/architecture/backend-api.md`。

当前合同来源：App 后端源码提交 `c2fbff4`（2026-11-15，完整源码基线见 manifest），冻结合同 **1.53.0**、产品 **0.113.0**、HTTP 协议 `"1"`。1.53.0 完成 M14 SDK v1 移除与版本收敛：效果包、作者源码检查、编译、预览、验证与导出仅准入 SDK v2（`packageVersion=3`、`shaderProfile="skia-ganesh-3.119.4-v2"`），`AuthorSourceInspectionResponse` 将 `packageVersion` enum 收窄为 `[3]`、`shaderProfile` enum 收窄为 `["skia-ganesh-3.119.4-v2"]`，并移除残余 `IsV2` 桥接。HTTP 路由与请求 DTO 结构不变；保留 1.52.1 失效预设筛选与布局参考测量修复、1.52.0 `sourceMaterialFailure` 安全诊断与默认256张场景表面上限（512MiB字节上限不变）、1.51.1 输入固定媒体与试样固定身份／种子、1.51.0 导出／场景测量背景兜底及 1.50.0 两态静态样式。HTTP 路径／方法／schema 为115／128／667，3932个合成例（2842反例）；工程／歌词／模板 schema 为31／19／9。匹配部署、真实HTTP／浏览器／导出单独记录，合同同步不等于这些验收完成。

- `contract/` 不得手改；其 SHA-256 由 `contract-manifest.json` 和前端合同测试核对。升级时整体复制五个文件与行为文档，并同步 `src/protocol/index.ts` 的版本常量。
- App 根 `pnpm build` / `pnpm build:desktop` 发布本仓库匹配 Server 与 Worker，`pnpm desktop` 仅运行经过 native manifest 校验的本地部署。旧上游 artifacts 路径不再是 App 运行入口。
- 本目录其余交接、工作流和历史审查文档保留来源记录；与本仓库实时源码及冻结合同不一致时，以后者为准。
