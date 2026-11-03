# 后端文档副本

本 App 的后端源码位于本仓库 `backend/`。`contract/` 五个文件整体复制自 `backend/web/`，行为文档 `backend-api.md` 复制自 `backend/docs/architecture/backend-api.md`。

当前副本：App 后端源码提交 `7c11eaa`，冻结合同 **1.42.2**、产品 **0.110.2**、HTTP 协议 `"1"`。本次修复 ASS 新建准入及 MakoLrcStudio 源歌词／ruby 导入，补齐上传 MIME 推断；有效 TTML 的 project／AMLL 往返保留正文、时间和注音片段，源文件清空命名空间的情况仍拒绝。接口形状与 schema 不变。

- `contract/` 不得手改；其 SHA-256 由 `contract-manifest.json` 和前端合同测试核对。升级时整体复制五个文件与行为文档，并同步 `src/protocol/index.ts` 的版本常量。
- App 根 `pnpm build` / `pnpm build:desktop` 发布本仓库匹配 Server 与 Worker，`pnpm desktop` 仅运行经过 native manifest 校验的本地部署。旧上游 artifacts 路径不再是 App 运行入口。
- 本目录其余交接、工作流和历史审查文档保留来源记录；与本仓库实时源码及冻结合同不一致时，以后者为准。
