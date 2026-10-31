# 后端能力缺口审计

> **2026-10-25 P5导航：** 当前开发线已接入P1/P2统一Runtime、P3完整模板/预设工坊与P4作者/个人配置库，当前产品0.104.1／合同1.38.1；P4有限fresh HTTP证据仍归属0.104.0。完整 API 目录、前端依据和证据边界见 [P5收口审查](archive/research/backend-api-p5-closure-audit-20261025.md)、[Web交接入口](../web/README.md)与[当前行为合同](architecture/backend-api.md)。下方“最新导航”及第1～9节保留各批历史口径，不用于判定当前能力缺口；浏览器、跨平台及性能范围仍独立。

**最新导航（2026-10-24）：已保存工程Image正式验证0.100.0／合同1.34.0的fresh HTTP有限切片见[实施§78](archive/history/backend-extraction-progress.md#project-image-validation-01000)（1412请求／10582断言、报告Oracle无PNG、一次orderly recovery与实际Worker/child排水；图片export/measurement、preset/video/font及完整B3/B4/B5/B6独立）；图片预览与PNG/像素证据独立见[实施§77](archive/history/backend-extraction-progress.md#project-image-preview-0990)。package 无音轨导出 fresh HTTP 有限收口见[实施§76](archive/history/backend-extraction-progress.md#b4-p2-package-no-audio-export-http-0981)，资源闭包与连续预览见[实施§74](archive/history/backend-extraction-progress.md#b4-p2-package-resource-closure-http-1310)，G20-G24后端整体补齐状态见[第9节](#9-g20-g24-后端整体补齐更新原定义后端范围已验收关闭)；第1～8节保持各自历史基线。** 下方旧“当前状态看第7节”属于历史实施口径。

**首次审计：2026-10-18，基线 `9dbc735`。实施更新：2026-10-18。** 本文区分原始缺口快照与后续有限切片，不把历史测试数量换算成完成度。当前状态看第 7 节；第 1～6 节原文保留其审计时口径，其中“当前”“尚未满足”均指 `9dbc735`，不覆盖后续交付。

G20-G24 的有限范围和验收入口见 [实施计划](archive/plans/backend-g20-g24-plan.md)。本轮最终冻结版本为 **1.22.0**；原快照中的1.19.0保留历史归属。最终模块回归见第7节及实施计划第5节。

## 1. 当前基线

审计对象是分支 `refactor/individual-backend` 的 HEAD `9dbc735240413eae020d70332d7e0f9d8bcc6c4e`（`test: cover clip lifecycle noop redo`）。根目录 `Directory.Build.props` 的产品版本为 **0.91.0**；`web/README.md`、`docs/architecture/backend-api.md` 和冻结合同共同表明当前 HTTP 合同为 **1.19.0，66 paths／73 methods**。

G19-B 已交付四个片段生命周期命令：`orchestration.createClip`、`renameClip`、`setClipContainer`、`deleteClip`。实施 §52 记录了 Hosting 923/923、Server 263/263、片段真实 HTTP 链 2/2，以及合同冻结校验；因此 `docs/backend-g19b-gap-analysis.md` 第 1 节的“无 HTTP 片段命令”是实施前快照，不能当作当前状态。

当前可用的 HTTP 是受能力清单约束的有限出口：歌词／时间／来源／设置编辑、波形读取、样式与模板库、工程预设分配、静态预设候选试看、静态模板试样、G18 快速布局、G19 编排读模型和 G19-B 片段命令，以及静态 profile 的预览、验证、导出、分析和受管理音频准备。`web/README.md` 明确写明前端 W3/W4 浏览器接入、完整 Web 编辑后端和完整独立后端仍未完成。

## 2. 证据分层

以下分类按“另一端能否通过当前公共合同稳定调用并完成真实验收”划分。

| 分类 | 当前结论 | 证据与边界 |
|---|---|---|
| **代码已有，但未开放 HTTP** | 包快照／包 Worker／诊断、脚本编译与运行、图片图、材质、字体和媒体引用的内部链路已有实现；Core 也有节点树、重排、片段、前奏、局部变换、行动画和场景几何类型。 | `PackageProjectSnapshotCodec`、`WorkerPackageRenderFrameSessionFactory`、`PackageRenderDiagnosticsMapper`、`ProjectRenderSessionFactory`，以及 `SceneTreeEditor.*`、`LeadInEditor`、`LineAnimation*`、`SceneTreeGeometry`。B3-G 只证明内部 package 诊断/隔离出口，公共 Server 仍未把 package profile 放入运行 allowlist。代码存在不等于公共路由、能力、授权、资源闭包或真实 HTTP 证明。 |
| **HTTP 已有，但只限有限 profile** | 当前合同对静态歌词/场景快照、静态模板/预设快照和固定资源输入提供有限预览；B4 A-I 提供有限验证、短片导出、分析采用/清除、压缩音轨和受管理媒体准备。样式、模板库、预设分配和 G15-W4/G16-G19 也有明确合同。 | `docs/architecture/backend-api.md` §9～§14、§13.3～§13.10；`web/README.md` §1.1。静态 profile 不携带包、配方、脚本、图片/视频背景或任意字体闭包；B3/B4 的有限切片不能外推为完整工程等价。 |
| **Core／设计基础** | 领域模型和桌面/内部用例为后续切片提供基础，但尚未形成成对的公共 DTO、读模型、命令历史和错误合同。 | 场景树、`LyricDisplayClip`/调度解析、`LeadIn*`、`LineAnimation*`、`SceneTransform`、`ProjectRenderSessionFactory` 和作者样例类可复用；它们需要 Hosting 单写者、精确 revision、预算和 Server capability 才能成为 Web 能力。 |
| **缺真实 HTTP 或浏览器验收** | 已有实现或计划接缝仍缺匹配发布后的公共链路证据，尤其是连续预览、完整编排和 Web 页面。 | G19-B 的 2/2 只覆盖片段有限链；W3 样式库/外观和 W4 编排页面尚无浏览器验收；完整 package/媒体/字体输入没有公共 HTTP 成片、失败恢复和跨重启证据；不能用 Hosting/Core 回归替代。 |
| **完全未开始（就当前公共能力而言）** | 完整作者闭环、完整协议/分发收口和代表完整工程的端到端 Web 产品验收尚未交付。 | `web/README.md` 的 B5/B6 表格和 P3；不存在可宣称完成的公共草稿、源码修订、编译/采用/发布、完整分发和浏览器产品验收。 |

## 3. G20-G24 编排缺口

G19 只交付连续范围、clips/scheduleContext 投影、默认容器/快速布局，以及四个片段生命周期命令。`docs/backend-g19b-gap-analysis.md` §7 明确把以下项目列为后续缺口；当前代码中的 Core 类型或渲染入口不能代替 HTTP 交付。

| 切片 | 缺口 | 当前可复用基础 | 完成判据（尚未满足） |
|---|---|---|---|
| **G20 结构节点编辑** | 节点树增删、移动/重排、容器与程序结构的稳定命令和读模型；需定义冲突、默认归属、历史和预算。 | `SceneTreeEditor.Nodes.cs`、`SceneTreeEditor.Reparent.cs`、场景容器模型。 | 冻结严格 DTO/capability/OpenAPI，精确 revision 单写者回执，真实 HTTP 正反例、保存重开和 W4 页面操作。 |
| **G21 位置／内部布局／动画** | 场景位置、内部布局参数、局部变换和动画的可编辑投影与命令；不能把静态 style 读取或快速布局冒充完整布局。 | `SceneTransform`、`ScenePlacement`、`LineAnimation`、`SlotAnimationSpec`、`BasicLayoutOperation`。 | 逐字段预算和语义冻结；编辑后静态帧/动画输入与 revision 对齐；真实 Worker/HTTP 预览、撤销重做及浏览器拖动/编辑验收。 |
| **G22 几何参考渲染** | 面向编辑器的几何参考/布局测量输出及其与最终 Runtime 坐标的合同。 | `SceneTreeGeometry`、`SceneEditorGeometry`、`LineRenderGeometry`、Rendering Abstractions。 | 明确参考几何不是最终像素；公开 route/DTO、尺寸与预算、修订绑定，真实 HTTP 与代表工程像素/几何对照。 |
| **G23 前奏提示** | `leadIns` 的创建、修改、删除、生成策略和提示容器编排；当前预览/编排投影不公开它。 | `LeadInEditor`、`LeadInGeneration`、`PresentationValidator.LeadIns`、持久化 DTO。 | 命令/读模型/错误和时间冲突合同，真实预览/导出与保存重开；明确与动画、音频和浏览器时间轴的边界。 |
| **G24 本句微调** | 以本句/片段为目标的局部时序或布局微调，需明确不破坏来源行/元素语义及历史粒度。 | 现有时间编辑、片段范围和 `LyricContentEditUseCase.LineTransforms` 等局部用例。 | 冻结目标 ID、量化单位、no-op/冲突/undo 语义；真实 HTTP 与编辑后预览、跨修订并发和浏览器交互验收。 |

## 4. B3/B4/B5/B6 缺口

### B3：预览与 Worker

已完成的是静态工程异步帧、PNG 句柄/ETag/Range/HEAD、SSE、静态预设快照预览，以及内部 package codec/Worker/诊断基础。缺口是把受控 package 执行安全地变成公共合同：包/配方/脚本资源、图片/视频背景、可搬移字体闭包、授权读取、能力协商、脚本失败和失控回收的真实 HTTP；还缺完整工程连续预览（换输入、seek、resize、代次淘汰）和浏览器验收。内部 package 测试或渲染工厂存在，不能宣称 B3 公共完成。

### B4：验证、导出、分析与媒体

验证、短片导出、独立分析及采用/清除、压缩音轨和受管理媒体准备均是固定输入与预算下的有限出口。仍缺完整渲染输入（package/预设/脚本/图片视频/字体）的正式任务、实际脚本故障与恢复、扩大工程范围、完整成片和跨 profile 的统一验收。静态预览成功不推出 export、analysis 或音频驱动视觉已支持；B4 历史 §19～§27 的数字只对各自输入范围成立。

### B5：场景与作者

Core/Application/Runtime 有模板、效果包、脚本编译和作者样例等可复用能力，但没有完整无 GUI 作者工作流的公共闭环：草稿、库、预设映射、源码固定修订、编译诊断、采用、发布及其权限/版本/CAS/回滚。作者包资源 HTTP 和公共 package 执行也未交付。

### B6：协议与部署收口

Contracts/Server/Worker 分层、OpenAPI、冻结 manifest 和匹配启动入口已有。缺口是前端类型同步与版本策略、完整分发/升级/回滚、代表完整工程的端到端部署、浏览器验收、依赖/runtime 矩阵和旧入口收口。当前启动器或历史自包含发布证据不能代表所有平台或完整产品收口。

## 5. 推荐实施顺序

按依赖和可验收边界建议：

1. **G20 结构节点编辑**：先建立结构稳定 ID、读模型和冲突语义，作为 G21/G22/G23 的目标基础。
2. **G21 位置/内部布局/动画**，随后 **G22 几何参考渲染**：先让编辑有可保存的领域状态，再提供与 Runtime 对齐的参考几何和真实预览。
3. **G23 前奏提示** 与 **G24 本句微调**：在结构、布局和时间目标稳定后分别冻结时间冲突及局部编辑语义；每个切片都要合同、真实 HTTP、预览和浏览器证据。
4. **B3 公共 package/资源闭包**：把包脚本、媒体、字体和诊断纳入能力协商、授权、失控隔离及连续预览；再扩大 B4 正式验证/导出/分析输入并做完整成片验收。
5. **B5 作者闭环**：草稿→源码修订→编译诊断→采用→库发布，逐步接入上述公共 package/资源合同。
6. **B6 收口**：固定前端类型和协议版本，完成匹配分发、升级/回滚、跨平台依赖与代表完整工程的浏览器/部署验收。

该顺序不表示任何阶段已获完整验收；每一步都必须区分 Core/Hosting 回归、公共 HTTP、真实 Worker/媒体证据和浏览器验收。

## 6. 资料口径

当前状态以 `web/README.md`、`docs/architecture/backend-api.md`、`docs/backend-extraction-progress.md` §52、`docs/backend-g19b-gap-analysis.md` 第 7 节及当前源码为准。早期 `backend-review-and-web-contract-plan.md`、B3/B4 计划和历史实施章节只说明设计或当时切片，不能覆盖当前合同，也不能把历史“计划/内部能力/有限测试”改写为已完成的公共能力。

## 7. 2026-10-18 实施后矩阵

本节接续原 `9dbc735` 审计。本批功能交付产品版本升为 **0.92.0**（`Directory.Build.props`，源码baseline `cc6d632`）；本轮最终冻结合同为 **1.22.0，68 paths／75 methods／367 schemas／1390 examples（989反例）／62种工程命令**。G20／G21-A／G21-B／G22-A／G23基础／G24基础源码均已提交，功能主体 `34fe3f5`、版本提交／最终源码baseline `cc6d632` 均已推送；最终Debug正常solution构建0警告／0错误，Hosting **955/955**、Server **295/295**、Backend.Rendering **214/214**、Worker **78/78**分别通过，均exit0，不与历史或切片定向重复累计；这些基础出口不表示完整G20-G24、完整编排或全后端已完成。

| 切片 | 本次有限范围及状态 | 可追溯验收 | 明确保留的缺口 |
|---|---|---|---|
| G20 | 已提交六种节点结构命令：现有节点改名、重挂、完整子节点排序、增加槽位、删除节点、完整槽位排序；合同1.20.0。 | 实现 `a7e6275`、合同 `bdbffd6`；实施§53记录 Hosting 925/925、Server 263/263、G20 HTTP 3/3及构建0警告／错误，分别保留范围。 | 任意节点创建／复制；完整节点内容编辑；浏览器交互和完整编排。 |
| G21-A | 已提交已有布局容器内部默认位置命令；显式 null 清除；合同1.21.0。 | `641be21`／`c9e629b`、合同 `1c7f61f`；实施§54记录 Hosting 2/2、HTTP 1/1及构建0警告／错误。 | 本项单独不代表几何、布局参数和动画全部可编辑。 |
| G21-B | 已提交 `34fe3f5`：已有节点 placement/referenceRect、布局 safeArea/maxWidthRatio、完整槽位动画、来源程序命名动画策略四命令及编排属性投影。 | 合同1.22.0；最终模块回归已通过，证据见[计划§5](archive/plans/backend-g20-g24-plan.md#5-最终验收证据)。 | 任意动画轨道、任意场景属性／内容、完整 Runtime 动画与浏览器验收。 |
| G22-A | 已提交 `1a38f2d`：固定容器参考几何 GET：精确修订、单节点与父参考矩形、世界矩阵／角点、固定输入预算。 | 合同1.22.0；最终模块回归已通过，证据见[计划§5](archive/plans/backend-g20-g24-plan.md#5-最终验收证据)。 | 不是 Runtime 文本测量、自动布局测量、动画／效果变换或最终像素几何；完整G22仍缺代表工程对照。 |
| G23 基础 | 已提交 `13da56b`：倒计时提示容器创建及手工前奏创建／完整更新／删除四命令、前奏与有限内容投影。 | 合同1.22.0；最终模块回归已通过，证据见[计划§5](archive/plans/backend-g20-g24-plan.md#5-最终验收证据)；静态快照测试不等同真实Worker像素或成片。 | 自动生成策略编辑、mark编辑、附着模式切换、完整前奏时间轴及浏览器闭环。 |
| G24 基础 | 已提交 `987311b`：单句局部 transform 精确修订读取和设置／显式null恢复；偏移比例、旋转、缩放。 | 合同1.22.0；最终模块回归已通过，证据见[计划§5](archive/plans/backend-g20-g24-plan.md#5-最终验收证据)。 | 不改源行／元素时间，不承诺局部时序编辑、最终帧几何或浏览器微调工具。 |

### 7.1 前端与产品证据

Claude邮件报告 G19／G19-B 浏览器联调通过，前端提交分别为 `f4d5e23`／`ffc007b`，合同来源分别为 `f16206a`／`9dbc735`。这是前端报告的既有有限切片证据，本仓库未把它改写为独立重跑或用户验收；**用户验收尚未做**，也不覆盖G20-G24。W3其他能力及完整W4页面不能从这两项外推完成。

G25 `wouldChange` 已登记待排期；片段成员扩缩、拆分、合并仍未开放。完整编排、B3公共package／资源闭包、B4完整渲染任务、B5作者闭环与B6分发／平台／产品收口继续是缺口。旧源仓库 `/mnt/Data/Program/NewMakoKaraGenerator` 保持保护，本仓库只维护无窗口后端。

### 7.2 阅读规则

代码存在、Hosting回归、公共真实HTTP、真实Worker／像素、浏览器、用户验收和完整产品是不同证据层。第1～6节的原始审计不删除；新证据只更新本节与对应实施记录，不回写历史计数。具体HTTP行为以冻结合同与统一API为准。

最终冻结与独立validate均通过。TRX位于`artifacts/test-results/g20-g24-final-0920/{hosting,server,rendering,worker}.trx`。原0.91批次Hosting953/954失败保留于`artifacts/test-results/g20-g24-final/hosting.trx`；`d225c4b`／`96f6b2a`仅修测试的合法异步观察窗口，不记为产品BUG。该过程结果不与最终模块回归混记。

## 8. G20-B 节点创建与复制实施更新

**2026-10-18。** 本节接续实施前基线 `7fe43b1`（产品0.92.0／合同1.22.0），第1～7节保留各自历史归属。产品 **0.93.0** 的G20-B最终源码 `d045eae` 已提交并推送，最终模块验收通过；冻结合同 **1.23.0／68 paths／75 methods／369 schemas／1522 examples（1092反例）／64命令**，Release metadata、freeze write/check与独立validate均通过，日志见实施计划；详细字段、预算及验收入口见[实施计划](archive/plans/backend-g20b-node-creation-plan.md)。

| 能力 | 本批有限范围 | 边界 |
|---|---|---|
| 节点创建 | `orchestration.createNode`：严格 `group`／`auto`／`slotSequence`，画布或已有普通组为父；Server生成身份，序列根及1..64槽位原子创建。 | 使用默认几何/参考矩形；几何、布局和动画配置复用既有命令，不开放任意节点JSON。 |
| 子树复制 | `orchestration.copySubtree`：同父复制、重建全部节点身份和内部parent/slot/mark引用；单槽位/mark加入外部owner序列，根名null清除。 | 不复制Lyrics、Programs、Clips、LeadIns或Preset绑定；单次最多128节点。 |
| 稳定结果与准入 | `structureResult.createdNodeIds` 根优先、其余新增顺序；旧六命令省略该字段，不公开旧新ID映射。源/候选结构、验证输入、完整读投影与回执在提交前检查。 | 源/候选各2000节点、深度64、验证输入20000项和1048576累计UTF-16代码单元；歌词branch/line/token计入，长歌词可能413。完整编排输出独立受1MiB/20000项约束。 |

### 8.1 验收及证据边界

正常solution构建 **0警告/0错误**，日志 `artifacts/g20b-build-acceptance.log`。最终Hosting **979/979**、Server **321/321**、Backend.Rendering **214/214**、Worker **78/78**，全部exit0、无失败跳过；TRX分别在 `artifacts/test-results/g20b-acceptance/{hosting,server,rendering,worker}.trx`。Hosting包含24项新例；Server包含26项本批真实HTTP及cue原子拒绝断言，覆盖保存/关闭/重开与并发回执。Hosting切片定向67/67独立保留，不与最终模块或HTTP初轮过程计数重复累计。

当前无本批新的匹配发布、真实Worker像素、浏览器或用户验收结论；不将静态输入/合同/HTTP或Hosting回归外推为上述完成。第7.1节G19/G19-B前端报告保留原范围，不覆盖G20-B。

### 8.2 剩余缺口与下一步

下一批优先 **G23-B前奏生成策略、mark和附着编辑**。其全部功能并非硬依赖G20-B，Core已有生成及编辑基础；G20-B先补可复用目标、身份与结构准入。

随后依次补片段成员扩缩/拆分/合并，复杂布局/任意动画及Runtime几何，公共完整B3资源与渲染输入，再扩大B4完整验证/导出任务，并接续B5作者与B6协议/分发/平台收口。G25 `wouldChange` 为独立P3待排期。

完整节点内容、完整编排、最终像素/动画、浏览器与用户验收仍需独立证据；性能、长片、GUI与平台原矩阵保持边界。源仓库 `/mnt/Data/Program/NewMakoKaraGenerator` 继续保护，本仓库不重新引入Avalonia/SukiUI。


## 9. G20-G24 后端整体补齐更新（原定义后端范围已验收关闭）

本节接续基线 `6717539`（产品0.93.0／合同1.23.0），第1～8节保留当时有限范围和历史统计。按原始Web `web-client-w4-design.md` §3及 `web-ui-design.md` §6.5收敛本轮范围，产品0.94.0源码c50516d已推送，原定义后端范围已验收关闭；冻结合同1.24.0见实施§57，见[整体计划](archive/plans/backend-g20-g24-completion-plan.md)及[实施§57](archive/history/backend-extraction-progress.md#g20-g24-completion-0940)。

| 任务 | 本轮实现/验收对象 | 最终关闭依据 |
|---|---|---|
| G20 | 既有结构与槽位命令满足原表操作，新增联合流程验收。 | 结构→配置/引用→undo/redo→保存重开，拒绝原子性。 |
| G21 | layout kind切换、project enter animation及既有位置/布局/程序策略。 | 公共读写、继承、历史、真实Runtime消费。 |
| G22 | 真实Worker `scene-measurement-v1` time/layout，geometry+PNG同帧；world corners、edit reference与稳定身份。 | 原sourceSHA与实际sample fingerprint分开核验；代表布局样本、预算及生命周期。 |
| G23 | generation settings/完整preview/selected apply/delete exclude、mark配置、静态Node模板映射、真实测量reference附着。 | 选择/冲突/模板归属、保世界appearance、真实HTTP/Worker及持久化。 |
| G24 | line enter override/null继承、presentation读取及既有line transform。 | 层级动画、局部布局、no-op/并发/历史与实际预览；不改源时间。 |

### 9.1 最终证据

正常solution-final2为0警告/0错误；Hosting1033/1033、Server352/352、Backend.Rendering214/214、Worker78/78、另一Rendering项目定向40/40，共1717/1717无重复。Server31项精确工作流为352的子集，包含动画opacity/几何/PNG及完整10帧透明纯视频、0.6s/1.4s oracle；低层定向不另加。

匹配self-contained0.94独立发布806文件/804生产输入SHA核验；发布时manifest为958bbd6/dirty=true，commit-verification证明生产输入等于c50516d，不冒充clean clone。独立http-final3为73请求/781断言、6实际测量、8成功job终态，11观察身份含8Worker全gone，170文件凭证扫描及171证据文件SHA通过。详情与路径见[实施§57.4](archive/history/backend-extraction-progress.md#g20-g24-completion-0940)。

G22旧能力拒绝是Python模拟旧Worker协议；Debug Worker SlotId修前失败、Server fixture失败和探针final1/final2脚本fixture失败独立保留。原定义G20-G24五项均具备公共HTTP、历史/持久化及实际渲染相关证据，后端整体关闭；不是只完成G23。合同提交b312a835，最终冻结1.24.0/70 paths/77 methods/401 schemas/1913例（1394反例）/78命令；fresh metadata/write/check/validate及root独立重导/check/validate通过，实际73 JSON全匹配、0跳过/20操作。原1522例仅一个响应新增两个必返null字段，顺序及原值保留；报告见实施§57。

### 9.2 独立范围

G19成员扩缩拆合、G25、独立时钟、关键帧/任意轨道不作为原G20-G24欠账。完整B3/B4的package/脚本/图片视频/字体闭包及完整任务、B5作者、B6分发平台、浏览器/用户与原性能/长片/GUI矩阵保持独立，不从本轮测量/静态模板出口外推完成。

## 10. 基础业务补齐接续（2026-10-18，规划基线54cf920）

用户已授权执行最近审计建议1/2，具体有限范围、复用内核、候选合同与验收门禁见[后端基础业务补齐计划](archive/plans/backend-basic-business-completion-plan.md)。本节追加新范围，第1～9节历史状态、计数和原G20～G24关闭结论均保持。此时仅完成规划记录，产品仍0.94.0、冻结合同仍1.24.0，不新增功能完成或HTTP/Worker验收结论。

### 10.1 先修可确认的偏差

`tools/backend-api-contract/freeze.py`中`ExportJobRequest.format`生成`transparentMov`，实际Hosting/Worker接受`transparentProRes4444`，应纠正生成源及正反例，保持实际wire。R8附着拒绝路径需锁定稳定`failureCode`/`field`矩阵，包括真实参考为空和核心参考变换拒绝，保留修订冲突、权限与Worker不可用的区别。现行入口中的旧版本示例和过期能力限制需按实际证据整理；历史说明不回写为新结果。

### 10.2 本轮新增基础业务范围

| 范围 | 已有基础 | 接续目标 |
|---|---|---|
| G12目录浏览 | root grant/目录句柄/链接拒绝与保存打开已有。 | 授权根与有界目录条目发现，贯通现有打开/保存。 |
| 普通/增强LRC导出 | Core已有Plan/Export、完整损失和输出报告。 | 精确修订评估→显式确认→固定执行→认证下载。 |
| 工程音频替换/清除 | 固定音频、受管理准备、波形/分析/资源租约已有。 | Writer外准备、原子工程历史、同源清理、undo/redo及保存重开。 |
| 静态`.mktpl`传输 | 静态库/定义草稿/试样及Application归档codec已有。 | 有界上传导入、库CAS及固定归档下载；包执行不扩大。 |
| 最终静态样式/来源 | `PresetStyleResolver`和Runtime静态消费已有。 | preset/真实Node父链/clip的有限最终值与逐属性来源解释，区别四层staticHierarchy。 |
| clip成员/拆合 | G19/B、TrySetClip、快速布局拆片引用迁移已有。 | 显式扩缩、拆分、合并、引用冲突及一次历史事务。 |
| 改名/资源清单 | Project.Name、资源租约及保存闭包已有。 | 显示名命令、工程当前引用的有界授权资源投影。 |

场景编辑/草稿基础及静态模板能力已存在，不能把完整B5尚未交付误写为“场景草稿与模板全无”。本轮只补上述公共基础业务；高级包/脚本执行、媒体背景/字体闭包、整曲任务、完整B3/B4/B5/B6、G25、浏览器/用户及原暂停矩阵保持独立。

### 10.3 证据要求

推荐按合同纠错→目录/改名/资源→LRC→音频→模板→最终静态样式→clip拆合→集成冻结实施。新DTO/命令/路由均属候选，须随实际实现回填并冻结；所有写入保持精确revision、唯一Writer、一次历史、no-op保redo、完整候选/回执预算和资源租约。每批以相关回归、真实HTTP、保存重开及必要的真实Worker证据分别验收，不用现有内核或规划文本宣告公共功能完成。


## 11. 基础业务实施接续（0.95.0／合同1.25.0）

2026-10-18按用户授权审计建议1/2继续实施；§10为原规划时状态，§1～9原G20～G24关闭和历史统计保持。0.94.1纠错独立证据见[修复记录](archive/acceptance/backend-basic-business-bugfix-0941.md)。本轮实际路由、DTO及预算已回填[计划§5～10](archive/plans/backend-basic-business-completion-plan.md)与[统一API§17](architecture/backend-api.md#basic-business-0950)。

| 审计缺口 | 本轮实现边界 |
|---|---|
| G12目录 | 授权root单层目录、offset分页、4096扫描上限；不持目录快照。 |
| LRC | 单branch Core完整评估/额外损失→同plan显式确认→固定UTF-8认证下载；不合成跟随时间。 |
| 工程音频/改名/资源 | 替换/清除采用受管理准备与历史租约同步；仅显示名修改；实际引用闭包有界读取。 |
| 模板传输 | 原传输器支持的完整合法.mktpl保真导入/导出，全归档新ID一次CAS，高级对象惰性不执行。 |
| 最终静态样式 | 真实container/slot/动态text叶父链、FollowPrimary自身容器与21属性Core来源；不表示最终像素。 |
| G19新增成员/拆合 | 完整连续成员、client新ID二分、原调度保留及相同纯静态应用安全合并；冲突原子拒绝。 |

最终验收证据待[实施§58](archive/history/backend-extraction-progress.md#basic-business-0950)录入，不以文件存在或测试计划推断通过。高级公共B3、媒体背景/字体、整曲任务、B5/B6、G25及浏览器/用户矩阵独立，原暂停范围不自动恢复。

## 12. package 资源闭包与连续预览接续（2026-10-21）

当前 `dev/backend-development` 已把 package 能力推进到以下有限公共边界：

| 能力 | 当前证据 | 仍未证明 |
|---|---|---|
| 包闭包读模型 | manifest/file 身份、role、SHA、大小、引用和预算检查；合同1.30.2的资源清单字段。 | 外部图片/视频/字体闭包、解码、跨机器可搬移性。 |
| 包文件受控读取 | 合同1.31.0新增 `POST .../resources/package-asset`；同工程/资源复用 asset，沿现有认证、Range/HEAD/ETag和TTL，工程关闭释放引用；Hosting 7/7、Server 2/2。 | fresh self-contained HTTP 上的完整 HEAD/GET/Range/ETag/304、跨重启和长读取排水。 |
| 连续 package preview | Server 3/3覆盖连续帧、换尺寸/透明度、seek、旧帧/旧代次、租约到期、跨client ACL、迟到Worker帧和关闭重建；`PreviewSessionResponse.packageClosure`固定闭包摘要。 | fresh self-contained HTTP 多帧/SSE完整矩阵、重启恢复、压力/长片和浏览器生命周期。 |

本批代码和 in-process 证据不关闭完整 B3/B4。下一步优先使用匹配 self-contained 发布完成包资源读取与连续 preview 的 fresh HTTP 证据，再独立推进外部图片/视频/字体闭包、preset+package、package export/analysis/measurement、B5 作者闭环和 B6 分发/浏览器/跨平台验收。

## 13. 2026-10-21 重点后端缺口与实施优先级

本节原审计表保留2026-10-21的历史缺口口径，并补记§74／§76有限HTTP收口；它不是 `dev/backend-development` HEAD `4e0f909` 的逐项当前代码清单。尤其下表 preset 混合、scene measurement 与作者工作流的旧缺口须结合表后2026-10-22增量阅读。这里按“可先独立验收、再解除后续能力阻断”的顺序排列；不把已有合同字段或 in-process 回归计为公共能力完成。

| 原审计优先级 | 历史缺口与有限HTTP收口补记 | 原审计建议的最小完成边界 |
|---|---|---|
| **已收口** | **package 资源读取与连续预览的 fresh HTTP 主成功切片**。实施§74已在匹配发布上完成 manifest/file asset 的 GET/HEAD/Range/ETag/304、ACL、普通/透明四帧、seek/resize、SSE、关闭重建、TTL 和排水；长时、跨重启多代及完整资源闭包仍独立。 | 后续推进图片/视频/字体解码与跨任务租约，并补齐长时、跨重启多代和完整 warning/error 矩阵。 |
| **已收口** | **package 无音轨导出 fresh HTTP 主成功与控制切片**。实施§76已在 `4e0f909` 匹配发布上完成普通/透明21帧、report/media bytes/size/SHA、独立ffprobe帧数/无音轨与ffmpeg全帧长度/帧数/alpha检查（未对Runtime内容oracle）、SSE、ACL、Range/HEAD/ETag/304、`includeAudio:true`拒绝、cancel/deadline/close、一次 orderly restart/recovery 和排水。 | 后续只扩展 package 带音轨、外部图片/视频/字体闭包及其租约；本条不代表完整 B4 或完整工程导出。 |
| **P1** | **图片/视频/字体资源闭包**。`PackageProjectSnapshotCodec.InspectResourceClosure` 当前只复核 manifest/files 身份、SHA/长度、定义引用和有限 role（含 `image`）；明确不解码图片、不打开媒体、不枚举外部图片/视频/字体路径。`package-asset` 只复制已验证包内原始字节，`validated` 不表示可运行。字体还需要跨机器字形/整形身份，视频需要受管理解码与时长/轨道策略。 | 先冻结资源引用的逻辑身份、来源修订、内容 SHA/长度、媒体类型和授权 grant；按 image → video → font 分步接入 Worker capability。每步必须有受控包内资源和外部引用拒绝、解码/整形诊断、跨任务 lease、取消/deadline/restart 回收，以及 fresh HTTP 的像素/帧/字体 fingerprint 证据。禁止让主机路径进入 snapshot 或公共 wire。 |
| **P2** | **preset + package**。现有工程预设分配和 `static-lyrics-scene-presets-v1` 只支持完整静态模板快照；统一 API §20 与 package codec 仍拒绝非空 `PresetApplications`，公共 package preview/validation 不公布混合输入。 | 先定义 preset 快照与 package revision 的合并顺序、冲突/覆盖来源、完整输入 fingerprint 和诊断 owner，再加入 capability/profile。需要保存重开、undo/redo、旧 package/preset revision 拒绝、普通/透明 preview 和 validation/export 代表样本；不能把静态 `staticHierarchy` 当最终 package 像素。 |
| **P2** | **package-aware analysis 与 scene measurement**。分析部署仍由 `ProjectSessionRegistry.Analysis` 限定 `LegacyAnalysisSource`/`CompressedAnalysisSource`；同帧测量 Worker capability 由 `WorkerProcessProxy.SceneMeasurements` 固定三种 static source profile。统一 API §12/§13.21 明确 package analysis/measurement 仍拒绝。 | 复用同一 package snapshot/资源 grant，在 Worker 内固定 package source、audio analysis（若有音频）和 sample fingerprint；analysis 要保持原完整 canonical bytes/采用语义，measurement 要保持 geometry+PNG 同帧、source SHA 与 sample fingerprint 分离。需覆盖无音轨 package、带音频声明但未授权、外部资源缺失/解码失败、preset 混合、取消/deadline/restart，以及 fresh HTTP 成功/失败矩阵。 |

**2026-10-22代码增量（不计为fresh HTTP收口）：**

- `e1942c0`／`9b907c5` 已新增并隔离 package+preset scene measurement 的 snapshot/profile 与 Worker 调用链及定向回归；因此不能再将“仅三种 static profile／preset+package一律拒绝”描述为当前代码事实。这些提交不证明 preset+package 在 preview/validation/export 的完整组合、完整资源闭包或 scene measurement 的 fresh self-contained HTTP 成功/失败矩阵已验收；package-aware analysis 仍须单独核验与验收。
- `339a88b` 已补齐无GUI作者草稿的编译诊断读取、源码修订、采用、删除、回执幂等、CAS 与客户端保护，并冻结合同1.32.0；已有代码与定向测试不等于匹配发布上的作者 fresh HTTP、完整草稿到发布链路或 B5 整体关闭。
- Worker video/font 私有接缝见实施§75；它仍不构成外部图片/视频/字体完整闭包，也不由§76的直接包无音轨导出HTTP切片补足。

这些优先级有明确依赖：P0 先证明资源授权与代次生命周期；P1 无音轨导出可以在当前直接包边界内独立收口；图片/视频/字体闭包及共享资源身份与 Worker grant 仍是 P2 后续完整资源验收的依赖；已有 preset+package measurement 代码按上述增量单独记账。B5 作者草稿→源码编译→采用→发布、B6 分发/浏览器/跨平台，以及完整长片/性能矩阵仍不由上述切片自动关闭。

每个切片的证据必须分别登记：合同生成/校验、相关 Hosting/Worker 回归、匹配 self-contained 发布、真实 HTTP/SSE/二进制或像素验收、进程与租约排水、保存重开和失败恢复。`PackageProjectSnapshotCodec` 的闭包 `validated`、预览 `packageClosure` 摘要、或单次 in-process 测试只能作为前置证据，不能替代这些公共验收层。
