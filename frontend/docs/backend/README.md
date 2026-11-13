# 后端文档副本

本 App 的后端源码位于本仓库 `backend/`。`contract/` 五个文件整体复制自 `backend/web/`，行为文档 `backend-api.md` 复制自 `backend/docs/architecture/backend-api.md`。

当前合同来源：App 后端源码提交 `3c2ac9e`（2026-11-14，完整源码基线见 manifest），冻结合同 **1.52.0**、产品 **0.112.89**、HTTP 协议 `"1"`。1.52.0 新增可选的真实源材质失败诊断：`originKind=sourceMaterialFailure` 保留完整源部位、材质包修订／导出、参数、阶段、微秒与稳定码；实际表面超限可附活动／申请／上限的表面数和十进制字符串字节数，不制造脚本 occurrence、不包含异常文本或堆栈。默认场景表面数上限256张，512MiB字节预算不变。保留1.51.1输入换模式时固定媒体与试样固定身份／种子，1.51.0导出／场景测量背景兜底及1.50.0两态静态样式；既有runtime、编译来源和兜底诊断语义保持，已登记报告无需补新字段。HTTP 路径／方法／schema 为115／128／667，3930个合成例；工程／歌词／模板 schema 为31／19／9。匹配部署、真实HTTP／浏览器／导出单独记录，合同同步不等于这些验收完成。

- `contract/` 不得手改；其 SHA-256 由 `contract-manifest.json` 和前端合同测试核对。升级时整体复制五个文件与行为文档，并同步 `src/protocol/index.ts` 的版本常量。
- App 根 `pnpm build` / `pnpm build:desktop` 发布本仓库匹配 Server 与 Worker，`pnpm desktop` 仅运行经过 native manifest 校验的本地部署。旧上游 artifacts 路径不再是 App 运行入口。
- 本目录其余交接、工作流和历史审查文档保留来源记录；与本仓库实时源码及冻结合同不一致时，以后者为准。
