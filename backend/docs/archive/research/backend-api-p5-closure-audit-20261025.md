# P5 后端 API 收口审查与前端交接（2026-10-25）

本次从已完成 P4 的 `dev/backend-api-completion-p2` 开展完整后端 API 审查，统一真实路由、请求与响应语义、生成合同、能力声明、前端调用说明及历史验收依据。当前产品为 **0.104.1**，HTTP 协议保持 **1**；合同由 P4 的 **1.38.0** 勘误为 **1.38.1**。0.104.1另修复预览input跨project/authorDraft来源切换的拒绝边界；P4的0.104.0匹配发布与fresh HTTP保留历史归属。当前公开范围保持 **100 个路径／111 个显式 HTTP 方法、96 种工程命令、5 种模板库命令和5种个人配置库命令**。schema 和例子数量以同提交的生成物及最终验证报告为准。

前端使用同一个后端提交中的 [交接入口](../../../web/README.md)、[执行合同](../../../web/api-contract.md)、[完整操作清单](../../../web/api-index.md)、[OpenAPI](../../../web/openapi.json)、[正反例](../../../web/examples.json)、[合同指纹](../../../web/contract-manifest.json)和[详细行为合同](../../architecture/backend-api.md)。计划、历史切片和旧版本示例用于理解来源，不能覆盖当前规范。前端无需启动 Avalonia、直接读写后端库文件或重写模板解析、脚本编译及渲染规则。

## 1. 审查范围与分工

主代理按用户要求派出多个 GPT 6.1 Sol xhigh 子代理并行审查，再统一核对发现、整合修订和执行最终验证。

| 审查线 | 主要检查内容 | 对照源 |
|---|---|---|
| 合同与生成物 | 路径/方法/DTO/严格联合类型、版本、例子、生成指纹与当前 CLR metadata 一致性 | `tools/backend-api-contract/`、`web/openapi.json`、`web/api-index.md`、`web/examples.json`、`web/contract-manifest.json` |
| 路由与运行时 | Server handler、Hosting 准入与持久化、Worker 能力门禁、job/preview 终态、错误与权限 | `src/MakoKaraGenerator.Backend.Server/ServerEndpoints*.cs`、`PreviewBackendService*.cs`、`src/MakoKaraGenerator.Backend.Hosting/ProjectSessionRegistry*.cs`、Contracts |
| 前端交接 | 启动/鉴权、修订与回执、读写顺序、SSE/二进制、模板/作者/配置库调用、可用能力与限制 | `web/README.md`、`web/api-contract.md`、`docs/architecture/backend-api.md`、目标目录 |
| 版本与验收证据 | 当前分支与默认分支关系、产品/合同/发布来源、P4 HTTP 留档和排水、旧状态与过度完成声明 | Git 引用、`Directory.Build.props`、P4 deployment/summary/manifest、README/AGENTS/历史记录 |

此次关注公共后端 API 与交接依据。浏览器交互、跨平台安装/分发、性能矩阵、任意输入组合及长时间压力测试继续独立；历史 P1/P2/P3/P4 的运行结果各保留原目录与计数，不拼成一次新验收。

## 2. 分支与版本基线

审查开始时主工作树为 `/mnt/Data/Program/MakoKaraGenerator-Backend`：

| 对象 | 实际值 | 解释 |
|---|---|---|
| 当前开发分支 | `dev/backend-api-completion-p2` | P3/P4及本次P5所在开发线 |
| 审查开始提交 | `0287175a27d537ac6dae495e97ef65cf1a5cbe7a` | 与 `origin/dev/backend-api-completion-p2` 相同；本次修订提交在其后 |
| `main`／`origin/main` | `3bd211fd1355e36b0566d1674efe98a9ef30a96d` | GitHub 默认分支，仍为产品0.102.0／合同1.36.0 |
| `main...HEAD` | `0 / 42` | `main` 是当前开发线祖先；P3/P4的42个后续提交尚未合入 `main` |
| 当前产品 | `0.104.1` | 来自根 `Directory.Build.props`；P5预览来源边界修复升级PATCH，P4原发布仍为0.104.0 |
| P4合同基线 | `1.38.0`，`sourceBaselineCommit=878f947` | 固定生产源码基线，非当前HEAD；后续合同工具/例子和文档提交另记 |
| P5合同 | `1.38.1` | 公共合同勘误与交接整理，当前产品0.104.1；完整指纹以当前manifest为准 |

远程 `main` 与当前开发线的 SHA 已通过 `git ls-remote --heads origin` 核对。审查时共有27个本地分支、31条工作树登记；P3/P4 已产生新开发和任务分支。[2026-10-25分支整理记录](../history/backend-branch-development-records-20261025.md)的“19个本地分支／远程仅main”属于更早整理时快照，本次只修正文档指向，不执行新的分支删除、改名或合并。

主工作树原有未跟踪 `tools/backend-project-image-export-probe/` 保留原字节；它不属于本次提交，不作为当前完整 API 或旧图片导出独立验收通过的依据。目录名、分支名和旧版本README均不能代替实际Git来源与运行结果。

## 3. 当前实现与前端依据

| 前端需要完成的工作 | 依据与调用边界 |
|---|---|
| 启动与连接 | [Web交接入口](../../../web/README.md)及[匹配发布工具](../../../tools/backend-web-host/README.md)；使用匹配的完整Server/Worker目录、私有bootstrap和实际loopback地址，读取当前capabilities/runtime-info |
| 创建/打开/编辑/保存 | [行为合同§3～§8](../../architecture/backend-api.md)；工程、库、作者草稿及预览代次分别管理修订；202只表示接受，须等待job或preview权威终态 |
| 歌词、时间、样式和编排 | [完整命令清单](../../../web/api-index.md)及[目标目录](../../backend-api-target-catalog.md)；精确修订读模型、稳定ID、命令回执与undo/redo沿现有合同 |
| 媒体、字体、预览、测量、验证与导出 | [统一Runtime输入§23](../../architecture/backend-api.md#unified-runtime-input-01020)及各任务capability；不能用某个任务成功或兼容单值sourceProfile推断其它任务可用 |
| 完整模板与预设工坊 | [P3行为§24](../../architecture/backend-api.md#24-p3-完整模板预设工坊01030合同1370)及[补齐计划§9](../plans/backend-api-completion-plan.md#9-p3-实施记录)；完整定义、包检查、试样选项、clips/高级Node目标与`.mktpl`往返沿已实现入口 |
| 作者与个人配置库 | [P4行为§21](../../architecture/backend-api.md#backend-author-p4)及[补齐计划§10](../plans/backend-api-completion-plan.md#10-p4实施记录)；固定源码与配置、同步/显式异步编译、作者试样、显式采用、个人配置CAS互相独立 |
| 事件、资源和下载 | [执行合同](../../../web/api-contract.md)及OpenAPI；SSE与资源读取携带认证头，按实际Content-Type消费，contentUrl不拼token；Range/HEAD/ETag/TTL遵守各资源行为 |
| 类型生成与联调 | 使用当前提交 `web/openapi.json` 生成类型，再按行为合同实现请求顺序和业务门禁；OpenAPI形状校验不能替代运行验收 |

公开草稿、个人配置和完整模板入口已存在；浏览器页面仍需实现相应编辑、选择及交互。后端工作流覆盖与浏览器/用户验收分别记账。作者持久化意味着状态根内正常读写，不宣称已跑过新宿主/新client恢复作者草稿的公开HTTP验收；编译结果读取只针对当前宿主live job。

## 4. 发现与修订

| 发现 | 本次处理 | 来源依据 |
|---|---|---|
| 根README仍称当前产品0.102.0／合同1.36.0，并将P3/P4列为后续 | 更新到产品0.104.1／合同1.38.1，提供当前冻结合同、P4历史证据和P5报告入口 | `Directory.Build.props`、当前 `web/contract-manifest.json`、补齐计划§9～§10 |
| README/AGENTS宣称main是当前入口，实际P4开发线尚未合入main | 明确当前分支为dev/p2、main是祖先及默认分支；不宣称已经合并 | Git HEAD/upstream、`merge-base`、`rev-list`与远程heads |
| 分支记录顶部数量及远程列表已被后续开发推进 | 标注为整理时快照，并指向本报告的审查开始事实 | 本地/远程引用与worktree登记 |
| 旧能力缺口审计容易被当成当前限制 | 顶部增加P5导航，原历史基线和证据保持归属 | 当前P1～P4合同与旧审计的固定基线说明 |
| API目录、调用说明和生成合同需要最终交叉核对 | 按真实路由/DTO/handler核对，合同勘误为1.38.1；生成物只经freeze工具维护 | Server/Hosting源码、当前metadata、OpenAPI与合成例子 |
| 工程预览的PUT input可进入authorDraft来源分支，使previewKind与实际来源不一致 | 产品0.104.1增加project来源类型门禁，并补跨project/authorDraft换源与原会话可用性回归；最终运行结果见§6 | `PreviewBackendService.UpdateAsync`、`PreviewTransportTests.AuthorSource.cs` |
| P4发布SHA、合同生产基线与最终文档提交不同 | 分别登记实现/合同/发布/探针/文档来源，保留证据分层 | P4 deployment、合同manifest和Git历史 |

本表描述本次修订目的；最终生成与定向回归结果记录于§6。生成物指纹是漂移报警，不证明handler语义、运行部署或前端产品体验。

## 5. P4发布与HTTP证据复核

本次独立复核的是现存P4产品0.104.0留档完整性，未将其重命名为P5产品0.104.1新跑HTTP。证据根为 `artifacts/p4-author-01040-r1/`，该忽略目录不会随普通Git clone交付。

| 证据 | 记录与独立核对结果 |
|---|---|
| `dist/deployment.json` | 产品0.104.0、源码 `aa663b4242c2ab6987931bbffafebd0d33d58bc3`、`sourceDirty=false`、Release/linux-x64/self-contained；1018个发布文件SHA全部一致 |
| 发布manifest SHA-256 | `300027a4d8d8fa8a93e40efd72652f243c10d66f39e09d208a1e93ada071b4cf`；此值固定 **deployment.json** |
| 审查开始时生产输入与发布对照 | 899个 `sourceInputs` 全部存在且SHA一致；0.104.1修复后复核为897项仍匹配，差异为 `Directory.Build.props` 和 `PreviewBackendService.cs`，无缺失；当前源码已不与该历史发布完全匹配，不据此假称重新发布 |
| `http-final1/summary.json` | `passed`，167请求／1142断言，431文件凭证扫描；五个登记job终态为四成功／一取消 |
| `http-final1/deployed-shutdown.json` | Server exit0，24个观察身份，`remaining=[]` |
| `http-final1/evidence-manifest.json` | 432个证据条目全部存在且SHA一致 |
| HTTP证据manifest SHA-256 | `08eb76b4df84cf3b049e95f3ec802025a02ef1486e899d52bcddd59e2a56d1a7`；此值固定 **evidence-manifest.json**，与发布manifest不同 |
| `contract-validation-final.json` | 合同1.38.0：100 paths／111 operations／556 schemas／3156 examples（2374反例）；回放163份JSON响应、42份成功JSON请求，4份跳过、0份未配对 |

该P4工作流覆盖授权包检查、草稿列表/源码/配置、同步与固定输入异步编译、authorDraft创建/更新、360帧正式验证/报告/诊断及取消、显式采用、个人配置CAS增删改/材质与建草稿、完整模板与工程应用后的Worker帧。它不包含浏览器、重启恢复作者草稿、任意恶意脚本OS sandbox、跨平台或性能验收。24个观察身份的退出只陈述本轮实际观察集合，不外推所有瞬时子进程均已被采到。

P1/P2与P3运行证据继续分别见[补齐计划§8～§9](../plans/backend-api-completion-plan.md#8-p1p2-实施记录)。180秒带音轨成片、视频/高级Line/包字体代表链路等结果保留原范围，不以本次文档审查增加请求、断言或像素计数。

### 5.1 P5当前产品增量HTTP

为验证0.104.1边界修复没有破坏已交付P4作者工作流，使用提交 `f4d5e2a` 的干净匹配发布 `artifacts/p5-final-01041/dist-r1/` 和新 Oracle，运行 `--phase p4` 增量探针至新目录 `artifacts/p5-final-01041/http-r1/`。结果为 **passed，205请求／1368断言，509个凭证扫描文件**；覆盖空工程、包检查、作者草稿/完整源码、配置、同步与固定源码异步编译、authorDraft预览换样本、360帧验证成功与取消、显式采用、个人配置CAS/材质配置、完整模板保存与工程应用。发布为产品0.104.1、Release/linux-x64/self-contained、`sourceDirty=false`；deployment manifest SHA-256 为 `c9f9fd24b6b3991d6bb2e5b493023aadd14af5f7cd3bce131cf509176e3b739f`，evidence manifest SHA-256 为 `a13c091f6e62043ed0c96d90a3e34a25876a87180d50157b1fa87df9ead9b0c5`。Server exit0，观察到24个身份，`remaining=[]`。该结果是P4作者工作流的P5增量证据，不是完整浏览器、跨平台、任意输入组合或性能验收。

## 6. 最终验证与交付边界

| 验证层 | 应记录的结果 | 可证明的范围 |
|---|---|---|
| 路由/DTO/行为审查 | 多审查线交叉核对，具体发现及修订见§4 | 当前公开边界与调用语义；静态审查不等于运行 |
| 当前metadata、freeze与validate | `artifacts/p5-contract-audit/clr-metadata-final.json`；`artifacts/p5-contract-audit/validation-final-1381-http.json`；ContractMetadata Release构建、freeze write/check、validate均通过：100 paths／111 methods／556 schemas／3156 examples（2374反例），HTTP日志为空；manifest `sourceBaselineCommit=ac685e1` | 当前CLR类型、OpenAPI/index/examples/manifest和行为合同的生成指纹一致；生成检查仍不代替运行部署 |
| 受影响回归 | `dotnet build MakoKaraGenerator.slnx -c Debug -m:1 --no-restore`：0 warnings／0 errors；`artifacts/p5-contract-audit/p5-focused-final.log`：新增跨project/authorDraft双向换源与作者草稿传输共3/3通过 | 证明0.104.1边界修复和相邻作者HTTP回归；不扩大为全预览/Worker矩阵 |
| 宽筛选边界 | `artifacts/p5-contract-audit/preview-author-final.log` 的旧 `PreviewTransportTests|AuthorWorkflowTransportTests` 筛选为385/395通过、10项失败；失败分组为旧Worker能力/环境门禁及历史failureCode期望差异，未归因于本次修复，也不记作整套预览通过 | 保留失败归属，避免把环境不可用或旧测试语义当作P5成功证据 |
| 0.104.1匹配发布／fresh HTTP | `artifacts/p5-final-01041/dist-r1`（源码 `f4d5e2a`、0.104.1、sourceDirty=false）与 `http-r1/summary.json`：passed，205请求／1368断言、509凭证扫描文件；Server exit0、24 identities、remaining=[]；deployment/evidence manifest SHA见§5.1 | 证明P5修复后的P4作者工作流增量可运行；不扩大为浏览器、平台或性能验收 |
| P4留档复核 | §5的1018个发布文件、432个证据条目与审查开始时899个生产输入全部SHA一致 | 原P4证据及审查开始来源；后续0.104.1生产变化不在旧发布内，不新增HTTP验收 |
| 文档链接与差异 | 最终相对链接/锚点和 `git diff --check` 由主代理统一记录 | 前端可定位到正确同提交依据 |

P5后端 API 交接的停止条件是：当前100路径／111方法及96工程命令无遗漏；模板/配置库命令和P1～P4目标动作都有实际读写/结果路径；生成合同与行为说明一致；前端能按启动和调用说明使用现有公共入口；发现的合同/文档问题已修订并通过对应检查。上述条件完成后可关闭本次API交接任务，浏览器接入、跨平台分发、性能和任意输入组合仍按各自任务验收。

## 7. 复核命令

分支与来源核对从仓库根执行：

```bash
git status --short --branch
git merge-base --is-ancestor main HEAD
git rev-list --left-right --count main...HEAD
git ls-remote --heads origin
git for-each-ref --format='%(refname:short)' refs/heads
git worktree list --porcelain
```

每次新冻结必须从当前稳定源码重新构建metadata，再执行工具；完整命令见[合同校验工具](../../../tools/backend-api-contract/README.md)。不要复用未核对来源的旧metadata，或手改OpenAPI/index/manifest。

```bash
dotnet build tools/backend-api-contract/ContractMetadata.csproj -c Release -m:1
tools/backend-api-contract/bin/Release/net10.0/ContractMetadata artifacts/api-contract/p5-clr-metadata.json
python3 tools/backend-api-contract/freeze.py --metadata artifacts/api-contract/p5-clr-metadata.json
artifacts/api-contract/venv/bin/python tools/backend-api-contract/validate.py --report artifacts/api-contract/p5-validation.json
git diff --check
```

只有明确需要更新生成物时才对freeze增加 `--write`；随后重跑默认检查。真实HTTP留档可另用 `validate.py --http-log`核对JSON形状；二进制内容、SSE语义、像素、媒体和进程排水仍需各自证据。
