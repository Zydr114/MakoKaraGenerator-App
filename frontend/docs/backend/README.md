# 后端文档副本

这里的文件从后端仓库 `/mnt/Data/Program/MakoKaraGenerator-Backend`（2026-10-17 前为 `/mnt/Data/Program/NewMakoKaraGenerator`）复制而来，方便在 Web 项目中查阅。

| 文件 | 来源 | 复制时的后端提交 |
|---|---|---|
| `contract/`（`api-contract.md`、`api-index.md`、`openapi.json`、`examples.json`、`contract-manifest.json`） | `web/` | `a4783fa`（分支 `chore/repository-hygiene-20261029`，冻结合同 1.40.0，产品 0.108.0，协议 `"1"`，源码基线 `245be6c`，2026-10-30。新增草稿发现与认领、包文本编辑、源码历史和包下载，修复 R10 与 G31；共 108 条路径、120 个方法、96 种工程命令。此前 `4e70334` 为 `main` 上的合同 1.39.0、产品 0.107.0、源码基线 `5802ac1`；1.39.0 新增 CPU/GPU 渲染选择。此前：1.25.0 基础工程业务，1.27.0 注音片段时间轴与 TTML，1.36.0 统一渲染输入、编辑候选与 `project.applyEdits`，1.37.0 完整模板与效果包检查，1.38.0 作者草稿与个人配置库） |
| `backend-api.md` | `docs/architecture/backend-api.md` | `a4783fa`（合同的行为源，SHA 由 `contract/contract-manifest.json` 固定） |
| `web-handoff.md` | `web/README.md` | `a4783fa`（前后端交接文档） |
| `frontend-workflows.md` | `web/frontend-workflows.md` | `a4783fa`（按用户动作查调用顺序、修订域、恢复与错误处理） |
| `backend-review-20261016.md` | `docs/backend-review-and-web-contract-plan.md` | 旧仓库 `400145d`（审查 F1～F6 与双端合同建议） |
| `backend-server-runbook.md` | `docs/backend-server-runbook.md` | 旧仓库 `696acea`（0.74.0，只作启动机制背景；当前启动方式见 `web-handoff.md` §3） |

- **权威版本始终在后端仓库**。副本可能过期；与后端仓库或实际 Server 行为不一致时，以后端为准，并重新复制。
- `contract/` 下的文件不得手改：`test/unit/contract.test.ts` 会核对它们与 manifest 中的 SHA-256。更新合同时整体替换这个目录和 `backend-api.md`，并同步 `src/protocol/index.ts` 的 `CONTRACT_VERSION`／`CONTRACT_PRODUCT_VERSION`。
- 文中的相对链接指向后端仓库内的其他文档，在本项目中会失效，请到后端仓库查看。
- 本机默认优先使用后端 `artifacts/w8-authoring-20261030/dist-final1`：产品 0.108.0、发布源码 `6569bf1`、`sourceDirty=false`；后端记录该发布后仅修改探针与文档。此处同步合同不代表新增作者接口已经接入前端，也不代表当前浏览器联调通过。
