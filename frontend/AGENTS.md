# AGENTS.md — MakoKaraGenerator Web 前端

本项目是 MakoKaraGenerator 的 Web 前端，通过本机 HTTP／SSE 连接后端 `Backend.Server`。后端是独立仓库 `/mnt/Data/Program/MakoKaraGenerator-Backend`（C#／.NET 10；当前交付分支见下方合同来源）。2026-10-17 后端从 `/mnt/Data/Program/NewMakoKaraGenerator` 迁出，旧仓库已归档（标签 `archive/backend-migration-20261017-8526b84`），只在找历史部署时读取。

## 当前阶段

**后续路线（2026-10-26 用户决定）**：后端补齐计划 P1～P5 已完成（合同 1.38.1，产品 0.104.1，后端 `aef1af4`，分支 `dev/backend-api-completion-p2`）。顺序是：第 0 步合同同步与回归（已完成）→ W4 收尾（已完成，2026-10-26 验收）→ W5 工程闭环与导出（已完成，2026-10-27 验收）→ 第 0 步同步合同 1.39.0（已完成）→ 渲染设备（CPU/GPU，已完成，2026-10-27 验收）→ W6 歌词页补全（已完成，2026-10-27 验收）→ W7 完整模板（已完成，2026-10-29 验收）→ W8 效果组件与作者（已完成，2026-10-31 用户验收）。分期见 [docs/web-ui-design.md](docs/web-ui-design.md) §10。Agent Mail 暂不使用，新缺口只登记在设计文档里。

**界面改版（台前幕后），实施中。** 2026-11-01 用户选定设计稿“台前幕后”第二版并确认计划：React Aria Components 加自写的紧凑 M3 样式，主题色可自选（默认取自标志 `#019BFE`），浅色、深色统一。共分 7 批：批 1 底座已完成（主题引擎、令牌与字体、图标、全部新控件、`scripts/ui-query.mjs`）；批 2 外壳与工作区已完成（2026-11-02：顶栏、状态胶囊、应用设置、关于，共享工作区布局与底部区，保留细状态栏），用户看过，风格符合预期；批 3 首页与工程生命周期已完成（2026-11-02：首页与右侧详情、目录浏览、分步新建与文件检查、恢复、保存位置、工程设置右侧面板）；批 4 歌词页已完成（2026-11-02：行列表勾选式多选与右键菜单、行编辑区与时间栏、只读的“外观”“显示在”、管理来源右侧面板、导入与导入报告等对话框、逐字预览）；批 5 编排页已完成（2026-11-02：左栏的画面结构树与歌词分配、画布工具栏、分组的检查器、快速布局与结构对话框）；批 6、7 完成后与前几批一起验收。过渡期旧的 mdui 封装在 `src/editor/ui/legacy/`，页面逐批改用 `src/editor/ui/` 的新控件，批 7 移除 mdui。计划、步骤与实施记录见 [docs/web-client-redesign-design.md](docs/web-client-redesign-design.md)，设计稿在 `docs/ui-style-drafts/`。

**W8：效果组件与作者流程（样式库），已完成（2026-10-31 用户验收）。** 三批实现与自动化联调完成后，用户确认当前 W8 可以认定为完成。已完成效果组件库、作者草稿配置与试样、CodeMirror 源码与编译诊断、作者验证报告与源码跳转、模板效果／材质“从效果组件选”。2026-10-31 同步合同 1.40.0／产品 0.108.0；后端 G33～G36、R10、G31 已交付，草稿认领、包文本编辑、完整修订历史、`.mkfx` 下载的新增 UI 留作后续，不扩大已审阅的 W8 三批范围。新登记 R11（普通脚本异常的预览诊断为空，非阻断），前端指引到验证页查看具体问题。新增 `smoke:author`，复用已有测试工具，只补作者关键链路。实施记录、出口对照、联调证据和限制见 [docs/web-client-w8-design.md](docs/web-client-w8-design.md) §6～§8。

**W7：完整模板（外观与样式库），已完成（2026-10-29 用户验收）。** 三批 2026-10-27 实施。第一次验收（后端 0.107.0）时试样全部失败（429 `sessionQuotaExceeded`），原因是离开页面时成片预览会话没有关闭，已修复（`a99423a`），复验通过。 样式库按完整定义编辑：导入效果包、材质与效果页签、参数控件、工坊多试样、字体选择与检查、`.mktpl` 导入导出；外观页：含效果的模板可试看和应用、来源输入映射、编排对象（容器和组、片段、前奏提示组与标记）作为分配目标、外观来源、在样式库中编辑副本。新登记的后端缺口 G31、G32 不阻断。2026-10-27 用户决定（§9）：效果包在 W7 直接导入 `.mkfx`；提示组与标记只通过分配模板改外观；分三批实施，全部做完后一起验收。新增 `pnpm smoke:library`，`smoke:appearance` 补了 W7 部分。实测记录、范围、缺口、步骤与验收见 [docs/web-client-w7-design.md](docs/web-client-w7-design.md)，实施记录、出口对照与已知限制见同文 §6～§8。

**W6：歌词页补全，已完成（2026-10-27 用户验收）。** 多选删除与翻译关联改为一条历史（G13、G14，用 `project.applyEdits`，超过 200 条分批）；导入对话框改用导入规划，TTML 多来源时选择导入哪个来源；注音片段编辑（行编辑区、时间栏、逐字预览）；时间轴加注音轨、翻译行轨和主歌词参考轨。时间轴扩展（设计 §2.6）2026-10-27 用户审阅通过。新登记的后端缺口 G30 不阻断。验收时发现时间轴向右拖共用边界松手后回弹，已修复（`a0d1aa7`），复验通过。设计、实测记录、缺口、步骤与验收见 [docs/web-client-w6-design.md](docs/web-client-w6-design.md)，实施记录、出口对照与已知限制见同文 §6～§8。

**渲染设备（CPU/GPU）：已完成（2026-10-27 用户验收）。** W6 之前插入的小批次。顶栏加“渲染设备”菜单，提供跟随后端默认、CPU、GPU 三项，偏好记在本机。成片预览、一次性渲染、测量、正式验证、导出都按偏好传 `renderBackend`；已开的预览用 `PUT .../render-backend` 换设备，GPU 不可用时写明原因，不自动退回 CPU。设计、步骤与验收见 [docs/web-client-render-backend-design.md](docs/web-client-render-backend-design.md)，实施记录、出口对照与已知限制见同文 §6～§8。

**W5：工程闭环与导出，已完成（2026-10-27 用户验收）。** 首页目录浏览、四种新建组合、恢复，保存位置与另存为，工程设置的改名、背景图片或视频、替换与移除音频，导出页（准备检查、正式验证、导出任务与下载），导出歌词文件。新登记的后端缺口 G26～G29 和缺陷 R9 都不阻断，只登记在设计文档里。设计、缺口、步骤与验收见 [docs/web-client-w5-design.md](docs/web-client-w5-design.md)，实施记录、出口对照与已知限制见同文 §6～§8。

**W4：编排页，已完成（2026-10-26 用户验收）。** 第一、二批 2026-10-17 验收；第三～五批（G19 范围快速布局与片段、G19-B 片段编辑、G20 结构编辑）2026-10-18 实施，第六～八批（G21 属性编辑，G22 画布叠加层、点选、拖动与布局参考，G23 前奏提示，G24 本句微调与进入动画，片段成员调整、拆分与合并）2026-10-26 实施，第三～八批 2026-10-26 一起验收。G25 尚未交付。设计、缺口与步骤见 [docs/web-client-w4-design.md](docs/web-client-w4-design.md)，实施记录、出口对照与已知限制见同文 §6～§8，第二批见 §9，第三批见 §10，第四批见 §11，第五批见 §12，第六批见 §13，第七批见 §14，第八批见 §15。

**W3：外观与样式库模板，已完成（2026-10-17 用户确认）。** 用户决定把原 W4 提前为 W3，编排页顺延为 W4。设计、后端缺口（G15～G17）与步骤见 [docs/web-client-w3-design.md](docs/web-client-w3-design.md)，实施记录、出口对照与已知限制见同文 §6～§8。

**W2：应用外壳与完整歌词编辑，已完成（2026-10-16 用户确认）。** 设计、实施记录、出口对照与已知限制见 [docs/web-client-w2-design.md](docs/web-client-w2-design.md) §6～§8；整体前端设计稿 [docs/web-ui-design.md](docs/web-ui-design.md) 已由用户审阅通过（三条原则、四个页面、§11 决定），后续各阶段都以它为准。时间轴波形轨道目前是最小验证版：将来可能以 TTML 为首选格式，并在波形轨道里统一支持，组件预计还要扩展（见 W2 设计 §8）。

**W1：打轴最小链路，已完成（2026-10-16 用户确认）。** UI 库用 mdui 2。设计、后端缺口（G9～G12）与实施记录见 [docs/web-client-w1-design.md](docs/web-client-w1-design.md) §8、§9。

当前消费后端冻结 API 合同 **1.40.0**（产品 0.108.0，后端 `a4783fa`，分支 `chore/repository-hygiene-20261029`，源码基线 `245be6c`，2026-10-31 同步；后端 `main` 仍是旧基线）。1.40.0 新增草稿发现与认领、包文本编辑、修订历史和包下载，修复 `canCompile` 与完整模板错误定位。默认优先使用后端 `artifacts/w8-authoring-20261030/dist-final1`（发布源码 `6569bf1`，匹配 Server＋Worker）；也可用 `MAKO_BACKEND_DIST` 指定，后备目录是后端 `artifacts/web-backend/0.108.0` 和本仓库 `.dev/backend-dist/0.108.0`。合同来源与版本历史见 `docs/backend/README.md`，W0 开发底座见 [docs/web-client-w0-design.md](docs/web-client-w0-design.md) §12。

## 先读什么

| 文件 | 内容 |
|---|---|
| `docs/web-client-redesign-design.md` | 界面改版：依赖、主题引擎、令牌、控件、外壳与工作区布局、各页改法、冒烟脚本改写，分批步骤与验收，实施记录（§7），用户决定（§9） |
| `docs/web-client-w8-design.md` | W8 设计：实测记录（§0）、效果组件库、作者草稿生命周期、配置／源码（CodeMirror 6）／验证页签、作者草稿试样、曲线与图片参数的图形编辑、模板里“从效果组件选”；后端缺口 G33～G36、R10，步骤与验收，用户决定（§9） |
| `docs/web-client-w7-design.md` | W7 设计：实测记录（§0）、完整定义与效果包、材质与效果页签、参数控件、工坊多试样、字体、`.mktpl`；外观页的高级模板、来源输入、编排对象目标、外观来源、编辑副本；后端缺口 G31、G32，步骤与验收；实施记录、出口对照与已知限制（§6～§8），用户决定（§9） |
| `docs/web-client-render-backend-design.md` | 渲染设备（CPU/GPU）批次：偏好与顶栏菜单、各处怎么传 `renderBackend`、成片预览换设备与失败处理、步骤与验收 |
| `docs/web-client-w6-design.md` | W6 设计：实测记录（§0）、一条历史的批量操作（G13、G14）、导入规划与 TTML 来源选择、注音片段编辑与时间栏、逐字预览、时间轴扩展（§2.6，2026-10-27 审阅通过）；后端缺口 G30、步骤与验收；实施记录、出口对照与已知限制（§6～§8） |
| `docs/web-client-w5-design.md` | W5 设计：首页与新建、恢复、保存位置、工程设置补全、导出页与正式验证、歌词导出；后端缺口（G26～G29、R9）、步骤与验收；实施记录、出口对照与已知限制（§6～§8） |
| `docs/web-client-w4-design.md` | W4 设计：编排页骨架、后端缺口（G18～G24、R4～R7）、各批步骤与验收（第二批 §9，第三批 §10，第四批 §11，第五批 §12，第六批 §13，第七批 §14，第八批 §15） |
| `docs/web-client-w3-design.md` | W3 设计：外观页与样式库模板、后端缺口（G15～G17）、步骤与验收 |
| `docs/web-client-w2-design.md` | W2 设计：范围、结构调整、后端缺口（G13、G14）、步骤与验收 |
| `docs/web-ui-design.md` | 整体 Web 前端设计稿（已审阅）：三条原则、四个领域加外观分配、页面与流程、后端能力需求、分期 |
| `docs/web-client-w1-design.md` | W1 设计：范围、mdui 界面、编辑器架构、§8 后端缺口（G9～G12）、§9 实施记录 |
| `docs/web-client-w0-design.md` | W0 设计、§9 后端缺口（含 G7／G8 实测）、§11 已确认决策、§12 实施记录 |
| `docs/backend/contract/` | 冻结 API 合同 1.40.0：执行合同、接口清单、OpenAPI、示例、指纹（来源与版本见 `docs/backend/README.md`） |
| `docs/backend/backend-api.md` | 合同的详细行为正文 |
| `docs/backend/web-handoff.md` | 后端交接：可用能力、启动方式、W0 后端缺口处理 |
| `docs/backend/frontend-workflows.md` | 按用户动作查调用顺序、修订域、恢复与错误处理（后端整理，W5 起常用） |

## 硬约束

- **不修改后端仓库代码。** 需要后端改动时，登记到当前阶段设计文档的“后端缺口”节，再按下文“前后端协作”通过 Agent Mail 交给后端 Codex。
- 后端协议以后端仓库和实际 Server 行为为准；本项目 `docs/backend/` 只是副本，可能过期。`docs/backend/contract/` 不得手改（单元测试核对 SHA）。
- `src/client` 与 `src/protocol` 不得依赖 React 或任何 UI 代码；依赖方向 `console → client → protocol`（`test/unit/dependency-boundaries.test.ts` 检查）。
- 凭证（bootstrap／access／resume token）不得写进 URL、日志或 `localStorage`；`.dev/` 不得提交。
- 64 位整数（修订、微秒时间、序号）在协议层保持字符串，见设计 §5。
- 页面只通过 `src/client` 的 `MakoClient` 访问后端；不要在页面里直接 fetch Server。
- 暂不做 CI。

## 前后端协作

后端由 Codex 在后端仓库开发，两个代理通过本机 MCP Agent Mail 异步通信。本节于 2026-10-16 与 Codex 商定，原始讨论见邮件线程 `frontend-backend-collab-20261016`；后端的对应规则在后端仓库 `AGENTS.md` §8.4。

### 身份与分工

| | 前端（本仓库） | 后端 |
|---|---|---|
| 代理 | Claude，邮箱名 `PinkPike` | Codex，邮箱名 `CoralCarp` |
| project_key | `/mnt/Data/Program/MakoKaraGenerator-Web` | `/mnt/Data/Program/MakoKaraGenerator-Backend` |
| 负责 | 协议消费、`src/client`、React UI、草稿／播放／显示、浏览器验证 | Core／Application／Contracts／Hosting／Server／Worker、API 合同、匹配发布与后端验收 |

- 每次会话开始，先用 `list_agents`／`whois` 确认身份，再用 `macro_start_session` 复用 `PinkPike`，不要新登记身份。
- 给后端发信时，收件人写 `CoralCarp@/mnt/Data/Program/MakoKaraGenerator-Backend`（迁移后的项目；旧项目 `NewMakoKaraGenerator` 下的历史邮件仍可查）。2026-10-17 已申请把新项目的联系有效期延到 2027-10-16。
- 不修改对方仓库。可以只读查看后端仓库，用来定位问题和写复现；结论以 Codex 的回复和后端提交为准。
- 谁来决定：W1 等阶段的范围与优先级由用户定。已授权范围内的 API 细节（路由、DTO、错误码）由 Codex 定，前端负责反馈需求和实现代价。产品与持久化语义、重大兼容性破坏、恢复后端暂停范围，都交给用户决定。

### 合同、编号与文档落点

- 合同的权威版本是后端 `web/` 下的 api-contract、api-index、openapi、examples、contract-manifest，加上同一提交里的 `docs/architecture/backend-api.md`。`docs/backend/` 是它们的只读副本：同步时从同一个后端提交整体替换，并更新 `docs/backend/README.md` 的来源提交，以及 `src/protocol` 的 `CONTRACT_VERSION`／`CONTRACT_PRODUCT_VERSION`。
- 合同与实际行为矛盾时，按合同或实现缺陷交给 Codex 修复，前端不猜语义。
- 编号规则：后端缺口记作 G#，缺陷记作 R#，全局连续，换阶段也不重置（W1 登记 G9～G12，W2 登记 G13、G14，W3 登记 G15～G17，W4 登记 G18～G25 和 R4～R8，W5 登记 G26～G29 和 R9，W6 登记 G30，W7 登记 G31、G32，W8 登记 G33～G36 和 R10、R11，下一个新缺口是 G37，下一个缺陷是 R12）。分配新编号前，先在对方文档和邮件里确认没被占用。批次切片沿用后端审查 §6 的 S0～S5。
- 落点：前端的缺口和需求草案写在当前阶段设计文档的“后端缺口”节。W0 是 `docs/web-client-w0-design.md` §9；W1 是 `docs/web-client-w1-design.md` §8，W2 是 `docs/web-client-w2-design.md` §3，W3 是 `docs/web-client-w3-design.md` §3，W4 是 `docs/web-client-w4-design.md` §3，W5 是 `docs/web-client-w5-design.md` §3，W6 是 `docs/web-client-w6-design.md` §3，W7 是 `docs/web-client-w7-design.md` §3，W8 是 `docs/web-client-w8-design.md` §3。后端的计划和候选合同放在后端仓库 `docs/`，邮件里给出路径和提交。
- 邮件不能代替文档。重要决定要写进对应仓库的文档，邮件只放摘要、路径和提交 SHA。

### 变更流程

1. **提出需求**：按“用户场景→现有缺口→请求与响应→稳定 ID／Int64／精确修订→历史粒度／no-op／错误→验收场景”的顺序写进缺口节，再发 `[缺口]` 或 `[提案]` 邮件。
2. **定合同**：Codex 回复范围和合同差异；前端确认可以消费后，后端在已授权范围内实施。不能为候选方案提前改动已冻结的合同。
3. **提前 mock**：只能依据带提交 SHA 的候选合同；代码和文档要标注“候选／mock”，不算后端已完成。
4. **后端交付**（`[交付]`）：给出提交 SHA，合同、产品和协议版本，变更摘要，兼容性，发布目录（前端用 `MAKO_BACKEND_DIST` 指向它），是否需要重新 `host.py publish`，`host.py run` 的参数或 stdout 是否变化（`scripts/backend-launcher.mjs` 依赖它们），以及验证证据和限制。纯文档任务只做相应的文档和合同检查。
5. **前端回执**（`[联调]`）：给出同步后的合同版本与来源提交、前端 SHA，以及 `typecheck`、单元、集成、`smoke:browser` 各自的结果。
6. 以下四种状态分开标注，不互相替代：合同已接受、mock 可用、后端实跑通过、浏览器联调通过。

### 邮件规范

- 使用中文，日期写绝对日期。
- 主题前缀：`[提案]` `[合同]` `[缺口]` `[交付]` `[联调]` `[问题]` `[协作]`。
- thread_id：缺口和缺陷直接用编号（`G9`、`R1`）；切片用 `S1-read-model` 这类形式；协作流程用 `frontend-backend-collab-20261016`。同一任务一直沿用同一个线程。
- 需要对方明确答复时设 `ack_required`。收到这类邮件先 ack；ack 只表示已收到，不表示同意。`importance=high` 只用于阻塞对方的事项。
- 邮件里不放凭证、`.dev/` 内容或私人 profile。
- 查收件箱的时机：会话开始时、关键步骤完成后、提交或交付前，以及在等待对方时有限次查询。邮件不会唤醒对方会话：等待超时就向用户报告“等待 CoralCarp：<线程>”，由用户转达，不要空转轮询。

### Git

- 本仓库默认一步一提交、一步一推送：每完成一个独立步骤（一个功能点、一次修复、一次文档或合同同步）就提交一次，并推送到 `origin`，不用等用户要求。提交前先跑与改动相应的检查：改了代码至少跑 `pnpm typecheck` 和 `pnpm test:unit`，只改文档就跑 `git diff --check`。每次只提交本步骤的改动。
- 不强推，不改写已推送的历史。推送失败（网络、冲突、远程有新提交）时先停下来向用户报告，不要自行 rebase 或 force。
- 两个仓库不共享文件；只有出现共享文件或同一仓库有多个代理时，才使用 Agent Mail 的 file reservation。

## 工具链与命令

pnpm + Vite + TypeScript + React + Vitest；Node 使用本机 v26。

```bash
pnpm install
pnpm typecheck          # 类型检查
pnpm test:unit          # 单元测试（假 fetch，不需要 Server）
pnpm test:integration   # 集成测试：每个文件自己启动真实 Server（独立 profile .dev/it-*）
pnpm build              # 类型检查 + 生产构建
pnpm dev:backend        # 启动真实 Server，写出 .dev/backend.json（Ctrl+C 停止）
pnpm dev                # Vite 开发服务器，固定 http://localhost:5173：/ 是编辑器，/console.html 是协议控制台
pnpm smoke:browser      # 真实 Chrome 点完控制台（需 pnpm dev；加 --manage-backend 时自己启动并重启 Server）
pnpm smoke:editor       # 真实 Chrome 走编辑器主链路：新建→改时间→撤销重做→界面主题（切深色、改主题色、刷新后还在）→保存→关闭→重开（需 pnpm dev:backend 与 pnpm dev）
pnpm smoke:appearance   # 真实 Chrome 走 W3：样式库复制编辑与试样／缩略图→选择器试看→外观页应用／撤销→分配停用／更新／移除；W7 部分：含效果的模板在行上试看与应用、外观来源、文本单元灰显、来源输入映射→应用到默认容器、片段、前奏提示组，时间轴片段轨点选同步→在样式库中编辑副本、保存并返回试看（W7 部分要后端仓库的 samples/ScriptEffects，找不到时跳过；需 pnpm dev:backend 与 pnpm dev）
pnpm smoke:orchestration # 真实 Chrome 走 W4：结构树与属性→分配条与片段列表→预览帧→快速布局预演／套用／no-op／撤销→改默认容器→撤销（R6）→时间轴选几句套范围快速布局、片段轨与列表联动（G19）→片段改名／改目标容器／删除／新建与撤销（G19-B）→结构编辑：新建轮换容器、改名、槽位增删排序、复制、移进新组、上移、不能做时写明原因、删除、连续撤销（G20）→画布叠加层、点选、拖动与撤销（G22）→属性：位置与参考框、安全区检查、动画策略、轮换与自动布局往返、槽位动画、布局参考模式、连续撤销（G21）→前奏提示：新建提示组、添加前奏、属性与附着方式、自动生成、标记（G23）→句子：本句微调、进入动画、画布双击选中与拖动、连续撤销（G24）→片段成员：调整句子、拆分、原段限制、合并、连续撤销（第八批）→加翻译来源（R4、R7，快速布局与片段编辑不可用）→撤销（需 pnpm dev:backend 与 pnpm dev）
pnpm smoke:project      # 真实 Chrome 走 W5：首页目录浏览与搜索（单击工程看右侧详情，再从详情打开）→新建空工程→空态导入歌词、选择音频→改名→背景图片适配／移除／撤销→保存到…、关闭、从目录重开→另存为（覆盖提示）→导出增强 LRC（接受损失、大小核对）→导出页：阻断与修复入口、首末帧、正式验证、导出 MP4 并下载、取消→只读打开→只有歌词／只有音频的新建→背景视频、替换音频、移除音频与撤销（撤销用工程设置面板标题栏的按钮）→从备份恢复一份（需 pnpm dev:backend 与 pnpm dev；背景视频要本机有 ffmpeg）
pnpm smoke:render       # 真实 Chrome 走渲染设备：状态胶囊菜单与设备摘要→外观页预览 CPU→GPU 不重开换设备→候选试看、编排预览与测量、样式库缩略图→导出页缩略图、正式验证与导出写明 GPU→改回跟随默认→刷新后偏好还在。GPU 不可用时走另一支：GPU 禁用并写原因，偏好是 GPU 时预览与验证明确失败，改用 CPU 后恢复；可用 VK_ICD_FILENAMES=/nonexistent/icd.json VK_DRIVER_FILES=/nonexistent/icd.json pnpm dev:backend 模拟（需 pnpm dev:backend 与 pnpm dev）
pnpm smoke:lyrics       # 真实 Chrome 走 W6：用工程 TTML 新建（恢复全部来源）→导入文件：识别结果与预览、改选编码、解析失败→再导入工程 TTML：选来源、不能导入到主歌词、作为新来源导入与撤销→注音片段：整段只读、左右移、改字、加删片段、删除注音、按字拆开（一条历史）→片段时间：时间栏起止、±1 帧、清除、取播放头，逐字预览按片段填充→时间轴注音轨：点选、拖边界、共用边界联动与撤销→翻译行轨：单击无动作、拖独立计时的行、双击切来源，主歌词参考轨（需 pnpm dev:backend 与 pnpm dev）
pnpm smoke:library      # 真实 Chrome 走 W7 样式库：复制模板→字体选择与字体检查→导入效果包作材质、材质焦点、改参数与越界校验、移除在用的包被拦→加效果、改参数与固定种子→工坊多试样、换目标、拖时刻、试播→保存后重读一致→.mktpl 导出、导入为新 ID、第三次传输写明要等几分钟→删除（要后端仓库的 samples/ScriptEffects，找不到时跳过；需 pnpm dev:backend 与 pnpm dev）
pnpm smoke:author       # 真实 Chrome 走 W8：组件导入、配置与曲线、源码失败诊断和修正、720 帧验证、保存组件、模板选效果与材质、删除来源后模板快照；需 pnpm dev:backend 与 pnpm dev，效果包取后端 samples/ScriptEffects
node scripts/page-shot.mjs <url> <out.png> [--wait 表达式]   # 开发辅助：真实 Chrome 截图并列出页面异常
# 开发辅助：pnpm dev 下打开 http://localhost:5173/ui-gallery.html 看新控件样张（?mode=dark、?open=dialog 等，见改版设计 §7.1）
```

- `dev:backend` 与集成测试调用后端仓库的 `tools/backend-web-host/host.py run`，需要匹配发布的 Server＋Worker。部署目录取 `MAKO_BACKEND_DIST`，缺省见 `scripts/backend-launcher.mjs`；后端仓库位置取 `MAKO_BACKEND_REPO`（默认 `../MakoKaraGenerator-Backend`）。
- 浏览器页面开着时停止 Server 要等约 30 秒（后端缺口 G8），先关页面可立即退出。
- `smoke:browser` 需要 Chrome：`CHROME_PATH`，缺省在 `~/.cache/ms-playwright` 下查找。
- 访问 npm 需要代理：`HTTPS_PROXY=http://127.0.0.1:7897`。本机 loopback 请求不要走代理。
