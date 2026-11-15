# 后端文档副本

本 App 的后端源码位于本仓库 `backend/`。`contract/` 五个文件整体复制自 `backend/web/`，行为文档 `backend-api.md` 复制自 `backend/docs/architecture/backend-api.md`。

当前合同来源：App 后端源码提交 `36032fa`（2026-11-15，完整源码基线见 manifest），冻结合同 **1.54.0**、产品 **0.113.0**、HTTP 协议 `"1"`。1.54.0 新增前奏提示组图形／自定义静态内联 SVG 与闪烁渐进擦除动画配置命令 `orchestration.setCountdownPresentation`（第 97 种工程命令，`CountdownShapeSpec`／`CountdownAnimationSpec`）、`countdownSequence` 内容投影 `shape`／`animation` 字段，以及 `orchestration.setLeadInAttachment` 的 `CueTargetAboveStart` 首字上方避让注音附着模式；继续保留 1.53.0 的 M14 SDK v1 移除与版本收敛（仅准入 SDK v2：`packageVersion=3`、`shaderProfile="skia-ganesh-3.119.4-v2"`）、1.52.1 失效预设筛选与布局参考测量修复、1.52.0 `sourceMaterialFailure` 安全诊断与默认256张场景表面上限（512MiB字节上限不变）、1.51.1 输入固定媒体与试样固定身份／种子、1.51.0 导出／场景测量背景兜底及 1.50.0 两态静态样式。HTTP 路径／方法／schema 为115／128／672，3995个合成例（2879反例）；工程／歌词／模板 schema 为31／19／9。匹配部署、真实HTTP／浏览器／导出单独记录，合同同步不等于这些验收完成。

- `contract/` 不得手改；其 SHA-256 由 `contract-manifest.json` 和前端合同测试核对。升级时整体复制五个文件与行为文档，并同步 `src/protocol/index.ts` 的版本常量。
- App 根 `pnpm build` / `pnpm build:desktop` 发布本仓库匹配 Server 与 Worker，`pnpm desktop` 仅运行经过 native manifest 校验的本地部署。旧上游 artifacts 路径不再是 App 运行入口。
- 本目录其余交接、工作流和历史审查文档保留来源记录；与本仓库实时源码及冻结合同不一致时，以后者为准。
