# 独立后端拆分实施记录

> **当前入口：2026-10-28性能开发线合入main。** `1b2d375` → `77643f8`快进保留19个提交，产品0.107.0／合同1.39.0／协议1。两批优化和统一性能日志已进入main；本次合同、915生产输入／1022发布文件与1391归档文件复核见[合入记录](backend-main-integration-20261028.md)和[§84](#main-performance-integration-20261028)。原回归、HTTP和性能采样保持dev验收来源，本次未重新运行。

> **历史入口：2026-10-27 main合入。** P3/P4/P5及两批GPU工作已从 `dev/gpu-render` 的 `9ab24b0` 快进纳入 `main`，完整保留95个提交，产品0.106.0／合同1.39.0／协议1保持。当前定向回归、合同与原匹配发布/留档复核见[main合入记录](backend-main-integration-20261027.md)；本次没有新生产修改、发布或fresh HTTP，下方各批数字和限制保持历史归属。

> **历史入口：2026-10-26 CPU/GPU显式API（0.106.0／合同1.39.0）。** `dev/gpu-render`新增 `GET /api/v1/render-backends`、`PUT /api/v1/preview-sessions/{id}/render-backend`，并让各类预览、验证、导出与测量共用严格 `cpu|gpu` 选择。GPU实际API为Vulkan；创建/任务省略或null使用启动默认，更新已有预览省略或null保留原选择，候选测量省略沿候选固定选择。任务准入时固定后端并在input/report回显，历史缺失值保持未知。设备切换保留原固定来源与媒体，GPU失败不回落。匹配clean发布 `9506942` 的本机fresh HTTP770请求／5013断言、独立GPU不可用负例46／343通过；分层回归、硬件、媒体与失败历史见[实施§81](#render-backend-api-01060)和[验收报告](../acceptance/render-backend-api-acceptance-20261026.md)，行为见[统一API§25](../../architecture/backend-api.md#render-backend-selection)、[实施计划](../plans/backend-render-backend-api-plan.md)和[ADR-0240](../decisions/decisions.md#adr-0240)。以下0.105及更早数字不并入本批。

> **历史入口：2026-10-26 本机GPU管线已完成（0.105.0／合同1.38.1）。** `dev/gpu-render`继承P5 API收口基线，新增显式Vulkan并保留默认CPU；详细实现、原有集成缺口修复、完整CPU6159/6159及实际GPU HTTP2376请求／18405断言见[GPU验收报告](../acceptance/gpu-render-acceptance-20261026.md)。匹配发布来源ca3b495，Server退出0，129个观察身份排水；跨平台、性能、浏览器和GPU编码不在本次结论内。开发分支未合入main。

> **历史入口：2026-10-25 P1/P2代表后端范围已完成（0.102.0／合同1.36.0）。** 工程/编辑/统一Runtime/成片接口与当时验收见[§80](#api-completion-01020)、[计划§8](../plans/backend-api-completion-plan.md#8-p1p2-实施记录)和[行为§23](../../architecture/backend-api.md#unified-runtime-input-01020)。P5后续见[P5报告](../research/backend-api-p5-closure-audit-20261025.md)。下方旧版本导航与“未开放/未完成”描述仅指各批当时范围。

> **历史2026-10-19 TTML/ruby 时间轴（0.96.0／合同1.27.0）已完成 P0～P6：** 现有 `LyricsDocument → Branches → Lines → Tokens` 结构保持不变，ruby segment 获得独立时间轴，并提供 `ttml.project` 往返与 `ttml.amll` 交换。最终证据见[§59](#ttml-ruby-timeline-0960)与[实施计划](../plans/backend-ttml-ruby-timeline-plan.md)；未覆盖的 spacing solver、完整B3/B4、B5/B6、浏览器/用户及性能/长片/GUI/平台矩阵仍独立。

本记录接续 [拆分方案](../plans/backend-extraction-plan.md)与 [API v1 草案](../plans/backend-api-v1-draft.md)。用户于 2026-10-11 授权开始实施，保留可用 Avalonia，按批次调用共享 Application 用例；长期 Web、可选 Tauri 的方向保持。工程格式与产品版本仍遵守既有 ADR。此前 package 无音轨导出 fresh HTTP 有限切片见[§76](#b4-p2-package-no-audio-export-http-0981)，package 资源闭包与连续预览 fresh HTTP 切片见[§74](#b4-p2-package-resource-closure-http-1310)，受控 asset 接口见[§73](#b4-p2-package-resource-asset-lease-1310)，连续预览代次回归见[§72](#b4-p2-package-continuous-preview-1302)，资源闭包读模型有限切片见[§71](#b4-p2-package-resource-closure-1302)；无音轨 package 导出代码接缝见[§70](#b4-p2-package-no-audio-export)，B4-P1 package validation 主成功路径多帧有限收口见[§69](#b4-p1-package-validation-success-multiframe-0981)；warning多帧与crash/recovery分别见[§68](#b4-p1-package-validation-warning-multiframe-0981)、[§67](#b4-p1-package-validation-crash-recovery-0981)，原0.98.1收口见[§65](#b4-p1-package-validation-0981)；此前诊断切片见[§64](#b4-p1-package-validation-diagnostics-1300)，核心HTTP切片见[§63](#b4-p1-package-validation-1300)。TTML/ruby 时间轴切片为 **0.96.0／合同1.27.0，见[§59](#ttml-ruby-timeline-0960)**；0.95.0／合同1.25.0基础业务仍见[§58](#basic-business-0950)。0.94.0原G20～G24关闭见[§57](#g20-g24-completion-0940)，0.94.1纠错见[独立修复记录](../acceptance/backend-basic-business-bugfix-0941.md)。以下0.93及更早导航保持历史归属：G20-B有限节点创建与同父复制见[§56](#g20b-node-creation-0930)，G21-B/G22-A/G23/G24基础编排见[§55](#g21b-g24-basic-orchestration-0910)，G20结构编辑见[§53](#g20-structure-0910)，G19-B片段生命周期见[§52](#g19b-clip-lifecycle-0910)，G19片段范围见[§51](#g19-clip-range-0910)，G18快速布局见[§50](#g18-quick-layout-0900)，G17模板固定试样见[§49](#g17-template-preview-0890)，G16候选试看见[§48](#g16-preset-candidate-preview-0880)；R3合同修复见[§47](#r3-contract-nullable-source-0871)；W4首个有限编排读模型见[§46](#w4-orchestration-0870)，F1无音轨静态预览修复见[§44](#f1-no-audio-preview-0861)，G15工程预设分配读模型见[§45](#g15-preset-application-read-model-0861)；公共静态预设预览见[§43](#web-static-preset-preview-0860)；工程显式预设分配0.85.0／合同1.10.0的历史见[§42](#web-preset-assignment-0850)；模板库0.84.0／合同1.9.0见[§41](#web-template-library-0840)；静态样式底层0.83.0／合同1.8.0见[§40](#web-static-style-0830)；G11波形见[§39](#web-g11-waveform-0820)；S3d见[§38](#web-s3d-import-0810)；首轨修复见[§37](#first-audio-stream-0801)；S3c见[§36](#web-s3c-tokens-0800)；S3b见[§35](#web-s3b-branches-0790)；S3a见[§34](#web-s3a-content-0780)；G3/S2见[§33](#web-s2-timing-0770)；S1读模型见[§32](#web-s1-read-models-0760)。此前0.75.0 B3-G内部package三类完整诊断有限出口已完成，统一1001／1001及独立部署／真实旧版本拒绝见[§30](#b3-package-diagnostics-0750)；公共HTTP包执行未开放。B3-F971及独立部署保持[§29](#b3-package-worker-isolation-0740)历史归属。B3-E的824／824及同进程Runtime等价保留[§28](#b3-package-source-codec-0730)原始归属。此前B4-I的repair2统一775／775与严格真正HTTP699请求／4239断言及原失败保持[§27](#b4-managed-audio-preparation-0720)历史归属；完整B3／B4及原暂停范围继续独立。

## 1. 首批范围与基线

工作分支 `refactor/individual-backend`，起点 `1fc7792a2cd47dde067e369d76a21d935ee0df5d`，产品基线 `0.49.14`。首批选取 B1 的“单行／元素时间组提交”，仅建立这条调用链必需的 B0 有限基线。

迁移目标：稳定来源／行／元素 ID 定位、整数微秒、原时间及工作区 Generation 门禁、Core 时间与呈现校验、一次历史、无变化零历史；App 保留输入法、非法原文和分组草稿，只适配共享结果。相关界面更新需保留行／元素实例与选择，更新实际时间轴，并在读模型一致后发布工程通知；场景草稿过期和撤销回原 Source 的语义保持。

本批没有实现 HTTP／SSE、Backend.Contracts／Hosting／Server／Worker、协议修订、目录写入租约或独立进程链路；不会把 App 直接调用用例记作 B2 完成。歌词正文、导入、设置等其余 B1 用例及 B0 代表工程的完整保存／渲染基准继续未完成。

以上为首批起点状态；正文／行操作、固定字节导入／标量设置、来源与打点接续见第6～9节；0.56.0的共享编辑收口和当前阶段边界见[第10节](#b1-closure-0560)。历史段落中的“未迁出”只描述各批当时状态。0.57.0的无GUI新建候选见第11节；0.58.0 B2-A传输种子和0.59.0 B2-B具名时间／历史切片的当前状态分别见第12／13节。

## 2. B0 必要的有限原基线

在生产编辑前从精确起点编译 solution；随后只使用该构建的 `--no-build` 测试执行体。`dotnet build MakoKaraGenerator.slnx -m:1` 退出 0，零警告／错误。定向测试均零失败、跳过或中止：

| 项目／过滤范围 | 通过数 |
|---|---:|
| Application：ProjectWorkspace／ProjectHistory／ProjectSaveCoordinator／ProjectRevisionWorkspace／PresentationEditSession | 56 |
| Core：LyricTimingEditor | 11 |
| App：LyricsTimeDraft／TimeInputCodec／AudioProjectHistory | 18 |
| Runtime.Hosting：BackendDependency | 2 |
| 合计 | 87 |

原始日志、终态 TRX、完整命令及计数在 `artifacts/backend-extraction/b1-timing/baseline/`，机器可读清单为 `manifest.json`。保存协调器包含保存期间编辑、旧 Generation 拒绝和实际快照归属；它不代表新增网络保存链路已经通过。没有执行全量、性能／采样、长片或原生窗口，也未操作用户既有窗口。

## 3. B1 首个时间组切片

### 3.1 共享用例及 App 适配

现有 Application 增加 `Editing/LyricTimingEditUseCase`、`LyricTimingEditTarget`、`LyricTimingEditRequest`、`LyricTimingEditResult` 和 `ProjectTimingChangeSet`，公共成员补 XML 说明。用例的传递依赖仍只有 Core，不加载 Avalonia／Suki／App；没有新增生产程序集或依赖包。

请求捕获同一 `ProjectWorkspace` 引用与 Generation，按明确分支／行／元素 ID 读取原时间并验证仍相同，随后检查端点、已准备媒体时长、Core 包含性／顺序和呈现规则。端点为 `long`；省略端点直接保留原微秒，未定时目标须提供两端。超过双精度整数边界的 `9_007_199_254_740_993` 微秒与媒体终点 ±1 微秒均有回归。拒绝不发布候选、不增加历史或改变 dirty；接受变化执行一次不可合并命令。相同端点保留来源状态、原引用、干净基线和重做栈。无关工程编辑不会使原时间未变的输入永久过期。

App 的 `LyricsTimeTarget` 也实际捕获工作区引用、输入会话、Generation 和稳定目标，离开组的迟到回调逐项核对；`TryCommitLyricsTime` 只调用共享用例并把中立原因映射为原提示。Core 规则、稳定目标定位和历史提交已从这条 VM 路径移出。输入法预编辑、非法原文、跨行／来源草稿、保存／导出前准备及取消恢复继续由既有客户端草稿承担。保持 FollowPrimary 的原领域语义与 UI 禁用规则，没有加入新的纯用例门禁。

### 3.2 时间增量、通知与场景草稿

用例返回变化目标，撤销／重做通过同一 `DescribeChanges` 分类。时间与来源状态可按明确行更新；正文、注音、结构、样式、来源配置及不能证明的其他变化保守完整刷新。正式 `ProjectSnapshot` 仍从完整当前工程建立，所有实际时间变化使预览失效，不宣称解决快照重建或整体延迟。

时间更新保留行／元素 VM、选择、展开状态与无关行的通知状态；更新受影响行的时间、来源标签和当前演唱状态。时间块是持久不可变列表，仅替换受影响行，先前列表仍固定；补时／清除导致的后行选中块序号同步调整，纯行区间修改保留逐字块列表。当前选中跟随来源时，原主来源的提交不重建当前行，但实际展示时间通知仍更新。

同工程 `ProjectEdited` 从 `UpdateProject` 移到 `AdoptProject` 完成行、时间块、选择、历史后发布；枚举确认 `UpdateProject` 只有此调用方。四类消费者为主窗口标题／历史、歌词编辑器刷新、画面候选失效／目标与歌词目录刷新。`ReplaceProject` 与 `SelectBranch` 继续各自发布原生命周期事件。`RefreshPlacementState` 已执行场景刷新，删除 State 中两处显式重复调用，普通时间提交／撤销／重做各一份场景通知。

场景草稿继续持有原 Source 和 Draft。时间编辑使它过期，拒绝提交；撤销恢复精确 Source 后允许继续原提交，跨工程不复活。独立回归发现原消息在过期→恢复时会覆盖实际操作错误；修复为 `SceneMessage` 直接由当前 Source 身份派生过期提示，不改写 `_sceneMessage` 中的真实反馈。过期预演／提交拒绝也不覆写它，恢复原 Source 后显示原准备／错误消息，取消显示原取消反馈。

### 3.3 独立审查与有限验证

代码冻结后从产品 `0.50.0` 构建 solution，退出 0，零警告／错误；定向 xUnit **205 项通过**，零失败／跳过／中止：Application 93（新增用例22／摘要14／依赖1，原范围56）、App 99（新增增量9／跨工作区1及原相关回归）、Core 时间11、Runtime.Hosting 依赖2。

增强现有 `--lyrics-drafts` Headless 流程，工具构建零警告／错误，**70 项断言通过**。实际 Enter／组离开、undo／redo 检查行／元素、卡片、焦点与时间框；实际 `NeutralPreview.Snapshot` 绑定新快照，固定时刻由旧已唱采样变为新当前采样。原非法输入、IME 预编辑、原回调／换工程、深浅与 1440／1100／760 视口及长正文／错误布局继续通过。Headless 为隔离虚拟窗口，未启动原生／常驻执行体或操作用户窗口。

过程失败如实保留：第一次 Application 93 中一项 `ImmutableArray` 本体相等重载误报，改为逐项 `Assert.Collection` 后复验；第一次 App 99 中一项真实错误消息丢失，修产品后保持原断言并全部复验。原失败终态在 `initial/` 与 `integration-initial/`，不作为最终通过证据。

最终命令／TRX／日志／源码哈希在 `artifacts/backend-extraction/b1-timing/final/`；Headless 探针来源在 `artifacts/ui-headless-layout.K2aZmn/`，复制的构建日志也保存在 final。Headless 进程实际退出 0，日志以 `PASS isolated lyric draft window is closed` 与 `PASS lyric draft headless flow completed` 结束，70 项来自实际 PASS 计数，未使用未结束的日志作证。

### 3.4 变异与恢复

针对本切片的回归执行五项有限变异，每项从冻结字节备份开始，**各自构建均退出 0、零警告／错误**，随后精确测试均退出 1 并因实际断言失败被拒绝：

| 变异 | 被拒绝的真实断言 |
|---|---|
| 强制时间变化走完整 Reload | 行／元素实例未保持，1 项失败 |
| ProjectEdited 提前到读模型同步之前 | 通知订阅者读到旧时间，1 项失败 |
| 在 UpdateProject 恢复重复场景刷新 | 提交／撤销／重做场景通知数量错误，1 项失败 |
| 过期时写坏真实场景反馈 | 恢复后准备消息／真实错误丢失，2 项失败 |
| 忽略捕获原时间比较 | 旧输入被接受覆写，1 项失败 |

变异命令、构建日志、失败 TRX、原字节及 SHA 在 `artifacts/backend-extraction/b1-timing/mutations/`，脚本的最外层 `finally` 恢复所有暂改文件；没有使用 `git checkout` 覆盖协作改动，也没有把编译错误／空过滤记为阴性通过。`final/restoration-check.json` 核对全部 **21 个冻结源码文件**的原／恢复 SHA 完全一致。

恢复后重新构建完整 solution，退出 0，零警告／错误；再次运行同一精确定向，Application 93、App 99、Core 11、依赖2，共 **205 项全部通过**，零失败／跳过／中止。最终正常程序集已重新生成，默认构建产物不保留变异执行体。Headless 单独构建目录已在复制日志并核对终态后清理，探针源、隔离配置与日志保留。以上组成本批 B1 首个时间组切片的自动验证出口。

## 4. 首批结束时的版本、阶段完成度与下一步

新增公开 Application 能力按 ADR-0194 使用 MINOR `0.50.0`。工程／歌词／模板格式保持28/16/6、配置1、profile2、SDK、生产程序集划分和固定依赖不变；精确产品版本打开门禁继续有效。

B0 必要有限基线与 B1 首个时间组切片已完成本批自动验证，完整 B0／B1 与独立后端尚未完成。旧拖动／微调／清除时间用例仍在 App，尚未迁到共享用例；其变化可由摘要识别并增量刷新，但不能记为业务迁移完成。下一切片先补齐这三类时间共享用例，再提取歌词正文／行操作，随后推进导入与工程设置；每批先固定原行为和变化范围，保留未迁路径完整刷新。B2 的服务程序集、单写者队列、传输修订／重试、目录租约、无 GUI 媒体准备与真实 HTTP 保存重开链路仍待实施。

全量、性能优化／基准／采样、长片、既有 UI 整页重构及设置／导出往返、用户人工物理输入／IME／听辨、Windows／多屏、未知失败提示和原暂停 goal 不自动恢复。本批没有正式导出、像素等价、失控 Worker 或完整制作路线的新证据，不把局部计算减少记成整体性能改善。

<a id="timing-actions-0510"></a>

## 5. 第二批：拖动、微调与元素清除（0.51.0）

本批从 `d8fd88282fade4dddb81aa07bcc0f9d0835c8668 / 0.50.0` 接续，用户继续授权共享时间用例与 Avalonia 接入。范围是现有逐字块拖动／边界调整、行边界拖动、元素微调与选中元素清除；不新增行／批量清除。新增公开 Application 能力经确认采用 MINOR `0.51.0`，格式28/16/6、配置1、profile2、SDK与固定依赖保持。

### 5.1 必要有限原基线与缺陷来源

生产编辑前的原 solution 构建退出0、零警告／错误，沿上一批范围加入本批已有独占呈现、lead-in、历史和轴映射／命中夹具：Application94、App105、Core11、宿主依赖2、Timeline56，共 **268项实际执行通过**，零失败／跳过。完整过滤、命令、日志与TRX在 `artifacts/backend-extraction/b1-timing-actions/baseline/manifest.json`；未恢复全量、性能、正式导出或原生窗口。

原始已编译App／Application／Core／Timeline执行体另由仓库内隔离探针直接引用，保存来源DLL的SHA：真实VM两次 `TryApplyBlockTimeChanges` 间隔950毫秒后形成两步历史；切换稳定元素目标后的两次 `TryNudgeSelectedToken` 合成一步。实际 `ProjectWorkspace.History` 使用默认900毫秒，并无另一编辑器配置。探针构建／执行均退出0、零警告／错误，程序、日志和来源哈希在 `original-reproduction/`；这是机制及真实VM调用路径证据，不冒充完整指针操作或性能采样。

同一原执行体还确认旧 `BeginGroup` 的满容量边界：组开始记录Count，裁剪后Count不增，第二条命令不能并回组起点。本批选择独立显式合并身份，不使用持续 `BeginGroup`，旧组容量缺陷仅记录、未修复；候选新路径需另验满容量、无关命令与历史恢复隔离。

### 5.2 共享动作与实际接入

Application 新增 `LyricTimingEditBaseline`、`LyricTimingTokenBatchRequest`、`LyricTimingEditGesture` 以及 `SetTime`、`Nudge`、`ClearToken`、`CommitTokenBatch`、`TryBeginGesture`。输入仍按同工作区引用、Generation、明确来源／行／元素 ID 与原时间验证，不使用时间轴下标作为共享目标。标量设置、微调和清除的领域／呈现校验及一次历史提交从App迁出；旧即时批次先验证所有原时间，再按原顺序校验候选，失败整批不提交。用例与传递依赖仍只有Core，无新生产程序集或依赖包。

原公开 `ProjectHistory.Execute` 与Core命令合同保持；内部增加明确合并身份的窄调用。同拖动使用唯一身份且不受900毫秒时间窗拆步，微调用稳定目标及操作身份并沿原时间窗。仅同名／同身份、当前栈顶After引用及未Break的连续历史可以合并，无身份旧命令与显式身份互相隔离。每个真实命令与Undo／Redo／Break／Clear／组边界更换连续性身份；满容量200仍合并本手势自身步骤，不吞无关命令。换工程、外部编辑后撤销恰好回到同Project引用均不能复活旧手势，旧End不能中断新手势。

保持实时提交，正常松开、捕获丢失与拆除保留已接受变化并结束一次，不增加Esc回滚或自动停播。无变化不产生历史、dirty、redo或变化通知；显式不可合并提交即使端点相同，仍保持原Break分隔，普通无变化手势更新不结束身份。微调从有理帧率换算整数微秒，checked处理加法和帧数边界，越界直接拒绝；原拖动吸附、邻字／行边界钳制继续在控件。标量及行边沿沿原媒体上界，元素拖动／旧批次不新增该限制；FollowPrimary保留原直接调用语义和既有UI限制。

App捕获Workspace／Session／Generation、来源和稳定目标。生产控件开始事件先准备时间字段，再重新定位同ID读取新端点；更新与完成携带同一唯一对象，MainWindow拒绝不匹配或没有被接受的开始。所有接受结果进入前批的保守时间增量和一致通知出口，行／元素身份、选择、时间块和实际预览持续同步。选择／来源／工程／其他编辑和历史边界结束当前手势；迟到旧Update／Completed不能改写或结束新手势。

### 5.3 独立审查、失败来源与最终验证

独立审查发现公开 `IsActive` 在自身提交的同步 `History.Changed` 通知中，会因工作区赋值与续写token尚未完成而误置永久失效。先加入纯只读订阅回归，成功编译后真实执行1项失败，证据在 `readonly-reproduction/`；修复自身Update同步区段后只读观察保持有效，重入更新拒绝，显式End／Dispose仍生效且不被接受结果复活。其他重入工程写入不受支持，与工作区单一所有者同步调度合同一致。

首次solution有两项测试CS8602；首次集成333项范围中Application145有7项失败：上述真实只读缺陷1项，以及时间与来源状态不一致的夹具创建失败6项。后者改为合法构造，特殊旧异常状态用record副本表达，原业务断言保持。初始编译、失败TRX和日志分别保留在 `initial/`、`integration-initial/`，不计入最终通过。

恢复变异源码后solution构建退出0、零警告／错误，定向 **335项全部执行通过**，零失败／跳过／中止：Application147（原94＋动作27／手势26）、App119（原105＋动作14）、Core11、宿主依赖2、Timeline56。覆盖整数微秒、>2^53与帧数极值、原媒体／包含性／独占呈现规则、稳定来源及FollowPrimary、旧工作区／Generation／原时间、无变化／dirty／redo、批次原子性、满容量、无关编辑与历史恢复隔离。原打点短组和文本应用的精确端点合同继续通过，不将尚未迁出的打点流程记为共享业务完成。

隔离Headless从真实MainWindow与TimelineControl执行指针按下／移动／松开、键盘微调／undo／redo和清除按钮：**时间动作41项、原时间草稿70项断言通过**，各自进程实际退出0且窗口关闭。动作流程包括跨900毫秒仍一次撤销、邻接钳制、行端点独立、非法字段拒绝开始、合法字段在Start提交后重读、Esc保留实时变化、capture loss／detach只完成一次、复挂后新动作独立及同ID换工程拒绝。旧Update／Completed由探针合成并送入真实窗口消费者，确认旧事件不污染新手势；这部分不冒充物理操作系统排队事件。实际时间框、块列表、VM身份及NeutralPreview快照与固定时刻采样同步。未启动原生／常驻程序、未操作用户窗口或新增截图。

最终命令、计数、源码／正常DLL哈希、solution及Headless构建日志、终态TRX与窗口关闭证据在 `artifacts/backend-extraction/b1-timing-actions/final/manifest.json`。最终Headless来源为 `artifacts/ui-headless-layout.hTqiQA/`，探针源／隔离配置与日志保留；专用构建目录在证据归档后清理。

### 5.4 有限变异与恢复

四项变异从冻结原字节备份开始，各自构建退出0、零警告／错误；定向执行退出1且因真实断言失败被拒绝：

| 变异 | 实际拒绝证据 |
|---|---|
| 拖动取消跨时间窗合并 | 同次拖动历史从1变2，1项失败 |
| 微调合并忽略稳定目标 | 不同元素误合并，1项失败 |
| 手势忽略历史连续性token | 8项历史边界执行，其中6项错误复活旧手势 |
| MainWindow忽略旧Completed身份 | 新手势后续实际移动被旧完成中断，Headless断言失败，finally关闭窗口 |

`mutations/manifest.json` 保存逐项命令、构建／失败日志、TRX、原字节与SHA；最外层finally恢复，不使用git checkout覆盖协作文件，不把编译失败或空过滤记为阴性通过。`mutations/restoration-check.json` 核对全部 **25个冻结源码文件** 完全恢复。随后只按批准范围澄清普通命令／共享结果出口的XML注释，独立记录最终来源哈希；无行为修改。正常solution、335项定向和Headless41／70最终正向复验确认构建产物恢复。

### 5.5 阶段边界与接续

B0必要有限基线及B1时间组、现有拖动／微调／元素清除两个切片完成本批自动验证，完整B0／B1仍部分。下一切片提取歌词正文／行操作，随后导入与工程设置；打点流程、整体平移、拆分／合并等仍在App，不把所有时间业务都记为迁完。旧BeginGroup满容量缺陷保持已知未修边界。HTTP／SSE、Contracts／Hosting／Server／Worker、队列／协议修订／目录租约、无GUI媒体准备和真实独立保存重开链路均未实施。全量、性能／采样、正式导出、长片、完整UI重构、用户物理输入／IME／听辨及平台／人工边界和原暂停goal不自动恢复。

<a id="lyrics-content-0520"></a>

## 6. 第三批：正文、注音与新增／复制／删除行（0.52.0）

本批从 `bad4fd80e6d80491076cc28b5029a3ef6969ee52 / 0.51.0` 接续。用户继续授权B1，共享五类现有编辑入口并保留可用Avalonia；新增公开Application能力按ADR-0196采用MINOR `0.52.0`。本批不改产品布局、格式28/16/6、配置1、profile2、SDK、生产程序集划分或固定依赖。

### 6.1 必要有限原基线与真实缺陷来源

生产编辑前原solution构建退出0、零警告／错误；沿前批时间／历史／保存／依赖范围，加入现有正文重切、行复制、Unicode、来源、呈现、导入及相邻App夹具：Application147、App140、Core98、宿主依赖2、Timeline56，共 **443项实际执行通过**，零失败／跳过／中止。完整命令、终态TRX和日志在 `artifacts/backend-extraction/b1-lyrics/baseline/manifest.json`；未重跑该已完成基线或恢复全量／性能／正式导出。

直接引用并保留原编译DLL的隔离真实VM探针，构建／执行退出0且零警告／错误，复现五项行为：连续复制带注音行产生 `token-1001` 全局标识冲突；不同稳定行的正文及不同基础元素的注音分别误合成一步撤销；新增行的 `ProjectEdited` 订阅者先读到旧选择；删除唯一已定时行后Blocks为空而SelectedBlockIndex仍为0。程序、原日志、来源DLL SHA和保留副本在 `original-reproduction/`，最终另逐一核对原DLL副本仍匹配这些SHA。这是原VM调用路径和历史机制证据，不扩大为完整物理交互验收。

原执行体的实际注音字段聚焦、排队TextChanged和同ID换工程路线 **未复现污染**：替换工程和字段都保留新注音，Reload清空元素选择并刷新字段。该缺陷假设探针退出1，虚拟窗口已关闭；日志与明确未复现清单在 `original-ruby-headless/`，最终仅对齐固定依赖后成功编译的探针未再执行。不能把静态身份风险、这次假设失败或新路径防护通过写成已证旧GUI污染。

### 6.2 共享用例、历史与ID规则

Application新增 `LyricContentEditUseCase`、稳定目标／请求／基线／结果、`ProjectLyricsChangeSet`及 `LyricsEditIdAllocator`，传递依赖仍只有Core。请求验证Workspace引用／Generation、明确来源／行／元素和被编辑原值；正文验证原文字，并使用当前行的时间、样式等其余数据，注音验证完整原注音，结构操作验证完整原行。相同内容保留原工程引用、dirty、redo、快照与零变化通知；拒绝不提交部分候选。

正文重切继续调用Core完整Unicode文本元素规则；未改区域保留稳定元素、样式及精确整数微秒，新元素未定时，悬空注音保持原Warning。注音修改保留ID、语言、比例、间距和对齐，空文字删除指定基础字注音。新增是未定时空行；复制沿原时长整体后移、重映射基础字注音并复制展示覆盖，原邻行重叠反馈保留；删除仅清理已经删除的展示来源，不静默修正其他来源的FollowPrimary关联。领域与独占呈现规则继续拒绝非法候选。

内部明确合并身份泛化为 `ProjectEditMergeIdentity`，正文按稳定行、注音按稳定基础字与操作区分，沿实际900毫秒窗口合并；不同目标、正文／注音、旧普通命令和时间手势互相隔离。现有时间公开API、唯一拖动身份、满容量／历史连续性门禁、公开Execute／BeginGroup合同保持，旧BeginGroup容量缺陷未修。

ID分配扫描所有来源、行、元素及注音，沿原line-/token-编号和1000下界，分配即保留；复制注音继续使用token-编号。达到32位正整数上界后明确返回 `IdentifierExhausted`，不回绕、无限搜索或提交半个候选。客户端用中文资源反馈且保持选择／历史／重做；未迁移的元素拆分、规则重切和粘贴也复用该分配规则，并仅捕获公开的 `LyricsIdentifierExhaustedException`。这不等于邻接业务已经迁出App。

### 6.3 Avalonia输入、增量与一致通知

App保留输入状态，捕获Workspace、Session、Generation、选择会话、明确来源／行／元素及原值。真实TextBox使用同步TextProperty变化消费捕获目标，延后的TextChanged不重新捕获新选择。正文、注音仍即时编辑；正文Enter／失焦断合并，Esc重读已提交值，不撤回已接受的即时编辑；IME预编辑Enter／Esc仍交输入法。来源、行、元素或同ID工程替换会刷新实际目标，旧输入拒绝且不结束另一个新时间手势。

正文／注音按保守摘要只更新受影响行；未改行、元素身份、选择、展开及原微秒保持，删除选中元素回退到仍存在目标。实际时间块按受影响行更新，注音单独变化不替换块列表；主来源／FollowPrimary和当前来源读模型保持。结构变化及不能证明的未知字段完整刷新，未迁元素拆合／排序不因摘要识别就自动改增量路径。所有实际变化仍使正式预览快照失效，正式Snapshot继续完整重建，不作整体性能声明。

新增／复制／删除的结果选择在统一Adopt内部完成，随后才发布一次 `ProjectEdited`；最后一行删除同时清空行／元素／时间块选择，输入字段禁用。撤销／重做沿同一变化分类与读模型出口，保持原“历史不保存界面选择”规则；恢复内容不会留下悬空目标。

### 6.4 最终有限验证与过程失败

恢复变异源码后正常solution再次构建退出0，零警告／错误；定向 **519项全部实际执行通过**，零失败／跳过／中止：Application205（原147＋本批58）、App158（原140＋本批18）、Core98、宿主依赖2、Timeline56。包括五类用例、原值／身份拒绝、无变化／dirty／redo、稳定合并、ID全命名空间／耗尽、完整Unicode、注音元数据、精确微秒、呈现映射／拒绝、FollowPrimary、正文／注音增量、通知时最终选择以及前批时间／历史／依赖合同。

最终隔离Headless工具构建退出0且零警告／错误，真实MainWindow／歌词编辑器 **正文33项断言通过**；受本批刷新／输入与ID邻接路径影响的原流程有限复查：**时间草稿70、时间动作41、工程设置与统一添加歌词73项**通过，各自进程退出0且虚拟窗口关闭。正文流程覆盖真实字段即时输入、连续合并、Enter／Esc／IME预编辑、撤销恢复、行／元素身份与精确时间、Unicode簇、实际NeutralPreview.Snapshot、同ID工程替换、来源选择、FollowPrimary、实际新增／复制／删除按钮、连续带注音复制、最终选区通知、最后定时行清空和中文ID耗尽反馈。不把Snapshot绑定相等记为新增字形像素或原生输入验收。

初次integration构建有三项编译／分析错误（CA1826索引集合调用、TryBeginGesture缺少out参数、CA1861重复常量数组）；integration-fixed构建另有TimelineTokenBlock值类型误用Assert.Same的xUnit2005。修正为有意义的值相等断言，原row／chip／块快照引用断言保持；这两次失败日志均完整保留，不能当作通过执行体。integration-resumed首次正常solution、519项和Headless全部通过；变异恢复后的final又完整复验同一有限范围，不弱化业务断言。

最终来源为 `artifacts/ui-headless-layout.Aaz2rf/`，源码／隔离配置／构建与运行日志保留；正常solution、完整命令、终态TRX、Headless计数／关闭证据、40个源码SHA、正常DLL SHA和原DLL副本复核归档到 `artifacts/backend-extraction/b1-lyrics/final/manifest.json`。只运行本批及相邻有限范围，没有全量、性能／采样、正式导出、长片或用户窗口自动交互／截图。

### 6.5 四项成功编译后变异与恢复

四项变异逐项从冻结原字节开始，各自构建退出0、零警告／错误；随后各自两项精确回归均因真实断言失败退出1：

| 变异 | 实际拒绝证据 |
|---|---|
| ID扫描遗漏注音标识 | 全命名空间编号错误、连续复制后标识数量不唯一 |
| 合并身份忽略稳定目标 | 不同正文／注音目标的撤销步数被错误合并 |
| ProjectEdited先于结果选择／读模型同步 | 订阅者读到旧行列表或旧选择 |
| 正文／注音强制完整Reload | 当前行／元素实例不再相同 |

逐项命令、成功构建、失败TRX／断言、原字节和SHA在 `mutations/manifest.json`；最外层finally恢复，不用git checkout覆盖协作文件，不把编译错误、空过滤或夹具异常当作有效变异拒绝。`mutations/restoration-check.json`核对 **40个冻结源码文件逐字节／SHA完全恢复**，随后正常solution、519项和Headless33／70／41／73全部正向复验，默认产物为正常执行体。ID分配器两处XML仅澄清实际公开耗尽异常，在冻结前纳入，无行为变化。

### 6.6 普通手测启动与阶段边界

按AGENTS§2从最终正常App输出复制并逐字节核验 **338文件**的独立普通0.52.0包，以独立XDG配置／状态／缓存启动：PID **1374333**，niri窗口 **707**，标题 `MakoKara — 首页 0.52.0`；日志 `/tmp/makokara-backend-0520-startup.log`仅版本行。可复启入口为 `artifacts/backend-extraction/b1-lyrics/manual-0.52.0/package/MakoKaraGenerator.Spike.App`；隔离profile、文件SHA、命令与启动观察在该目录上层 `manifest.json`。没有操作用户工程、既有窗口或截图，也未自动输入；普通启动仅供人工手测，不记原生交互／人工平台验收完成。

B0必要有限基线和B1时间组、现有时间动作、五类正文／行操作三个切片完成各自自动出口；完整B0／B1仍部分。本节交付时下一切片为导入与工程设置，实际接续见§7；打点、整体平移、元素拆分／合并、规则重切、行排序等业务仍在App。HTTP／SSE、Contracts／Hosting／Server／Worker、单写者队列、协议修订／目录租约、无GUI媒体准备和真实独立保存重开未实施。全量、性能／采样、正式导出／长片、完整UI重构、用户物理输入／IME／听辨、Windows／多屏、未知提示及原暂停goal继续不自动恢复。

<a id="lyrics-import-settings-0530"></a>

## 7. 第四批：固定字节导入／追加与标量工程设置（0.53.0）

本批从 `5dce95b575a358aa55c0481b796084548ffa28e5 / 0.52.0` 接续。用户授权继续B1，保留可用Avalonia，新增公开Application能力按ADR-0197采用MINOR `0.53.0`。格式28/16/6、配置1、profile2、SDK、生产程序集划分与固定依赖保持。本批不迁移背景／音频的读取、持久化、播放器或声卡生命周期；新建工程只共享纯歌词解析与固定候选，不提取整个准备链。

### 7.1 必要有限原基线与原DLL实证

生产编辑前solution `-m:1` 构建退出0、零警告／错误。现有导入格式／来源／ID、composition、草稿、历史／保存和受影响共享入口的有限过滤：Application76、App61、Core84、宿主依赖2，共 **223项实际执行通过**，无失败／跳过／中止；Core非法尺寸模型原测试继续执行。原 `--project-settings` Headless **73项**通过，工具构建零警告／错误，退出0且窗口关闭。338文件原App副本逐字节／SHA固定于 `artifacts/backend-extraction/b1-import-settings/baseline/original-app/`，基线完整命令与TRX见 `baseline/manifest.json`，原SHA清单摘要为 `06ece9919bc98cce76142e4f81a96790defab217bd4475ec0c8e413e5f88d0f3`。

隔离探针直接引用该副本的51个原DLL，无产品ProjectReference；成功构建零警告／错误，执行退出0、**8项检查**实证五类旧行为：

| 原0.52执行体路径 | 实际观察 |
|---|---|
| `AppendLyricsText`追加 | 工程订阅者先见旧行选择，返回后才选中新行 |
| 显式文件导入新来源 | 一次导入发布两次工程通知，分别看到旧来源和新来源 |
| Detect后外部改写同文件 | 检测A后改写B，Import重新读取并采用B |
| 两种公开空文件入口 | 裸path入口清空既有行并写历史；selection入口拒绝且工程／历史保持 |
| 原呈现含重复节点ID | Editor可构造且PresentationValidator报错；导入被门禁拒绝而仍返回并写入正常成功报告 |

最后一项使用原非法呈现树和有效候选，观察同Project、零工程事件／历史及一次拒绝事件，不把拒绝误当解析失败。命令、结果和原DLL哈希在 `original-reproduction/manifest.json`。这是实际VM调用链证据，不宣称已做原生文件选择或物理交互；同session／generation跨Editor的设置Connect身份风险只作接口防护，未写作原GUI已复现缺陷。

### 7.2 共享用例与App边界

`LyricsImportSource` 防御复制输入字节；Detect／Prepare只读同一固定候选，Prepare继续使用Core注册表和既有格式／编码／切分／估时语义，不读取路径、不写工作区。完整报告不可被外部集合继续改写，容错解析保留有用行。提交按Workspace引用／Generation、既有完整ExpectedBranch或新来源完整ExpectedLyrics验证；替换或文件新Independent来源、粘贴末尾追加或新Translation FollowPrimary来源沿原语义一次不可合并历史。全局ID与注音基础字映射保持，耗尽原子拒绝；呈现校验拒绝保留工程、选择、报告、dirty和redo。所有文件入口统一拒绝空候选。

Application允许目标原值未变的无关composition／其他来源编辑。GUI文件导入额外使用工作区／Session／Generation及整份Project／Snapshot引用门禁：任何工程编辑使旧候选不能提交，编辑后Undo回同一Project引用也不能复活；取消、关窗、换工程、目标过期和迟到失败不发布成功报告或覆盖当前反馈。成功报告、目标来源／首行选择及读模型在Adopt完成后只通知一次。实际Main虚拟窗口曾跑红：来源Combo在ItemsSource／SelectedItem同步期间把旧主来源写回；保留日志，改为显式同步时暂时解除选择处理器，相同ImmutableArray身份不重赋列表，用户SelectBranch仍正常。最终选区和一次通知断言保持，未接受旧选择。

新建Options内部携带固定候选，确认前原路径被删除仍采用已选字节；原媒体／字体／播放准备、失败保留旧工程与候选释放继续留App。ChooseLyrics请求按身份／CTS在每次await与异常检查当前请求、关闭和preparing；确认或重选使旧选择失效，旧结果不弹过时对话框，晚异常不覆盖新候选。

`ProjectSettingsEditUseCase` 共享画布宽高、精确有理帧率、底色、时长四组以及UseLyricsEnd／已准备媒体时长标量。字段组原值和Workspace／Generation验证允许无关组变更；歌词末尾按提交时当前歌词计算。无变化保留原引用、dirty、redo和零历史，有效变化一次历史。用户入口拒绝非正宽高／帧率／时长，允许时长未确定和奇数尺寸；Core模型继续允许表达非法数据，未夹值或改ADR-0006，未扩展保存门禁。App保留非法原文、微秒精度、IME、草稿和返回任务；Connect额外验证实际Editor身份。设置变化保守Adopt，不宣称整页增量或媒体生命周期已提取。

### 7.3 最终有限验证

恢复变异后正常solution退出0、零警告／错误。终态TRX **Application143、App99、Core84、宿主依赖2，共328项**全部实际执行通过，无失败／跳过／中止；新增Application67、App18，另有限执行控件接线15项以验证实际Combo接线。未恢复全量或背景／音频整类测试。正常Headless工具构建零警告／错误；冻结源码恢复相同字节后复用该正常独立构建，三次最终运行各退出0并关闭虚拟窗口：

| 实际流程 | 通过断言与重点 |
|---|---|
| `--lyrics-import-settings` | **51**：实际Main按钮→真实ImportLyricsDialog→提交；固定A、Independent新来源、最终选区／报告／一次通知／undo／redo及用户来源切换；无关编辑／Undo复活／换工程／取消／失败／空候选／呈现拒绝；实际新建Choose延迟读后确认或重选、旧结果／晚异常、删除路径固定候选；设置组／非法原文／精确微秒／IME／当前歌词末尾／返回 |
| `--project-settings` | **73**：原草稿、精度、IME、过期、返回、统一添加歌词现有／新来源及媒体兼容入口 |
| `--lyrics-content` | **33**：受Adopt变化影响的正文／注音及行操作字段、身份、结果选择、通知和历史 |

新建夹具使用自有已准备波形和播放器验证原准备链及一次释放，不等同声卡／媒体解码验收。数据绑定和选择不等同字形像素或物理IME。探针源／日志／隔离配置在 `artifacts/ui-headless-layout.A24sQY/`；命令、exits、计数、终态TRX及源码／DLL／证据SHA在 `artifacts/backend-extraction/b1-import-settings/final/manifest.json`。原探针首轮5项编译／分析错误、首个solution两项编译错误、真实Combo重入失败及诊断执行均保留各自日志，不记为通过；修复未弱化业务断言，未用失败solution二进制作正常验收。

### 7.4 四项变异与精确恢复

每项变异从冻结备份开始，构建均退出0、零警告／错误，定向测试均退出1且真实断言失败：

| 变异 | 实际拒绝证据 |
|---|---|
| 既有来源只验证ID、忽略完整原值 | 替换／追加2例错误接受，2项Assert.False失败 |
| 文件新来源改FollowPrimary | 三种角色应Independent，3项等值断言失败 |
| 画布组原值忽略Width | 6入口执行；仅宽变更的kind0错误接受，1项Assert.False失败 |
| GUI异步门禁忽略Snapshot | 编辑→Undo回同Project后错误复活旧准备，1项Assert.False失败 |

共12项执行、7项实际断言失败。`mutations/manifest.json`、各TRX／日志保留命令、exits、差异和SHA；最外层finally恢复，不使用git checkout覆盖协作修改。`mutations/restoration-check.json` 核对全部 **31冻结源码逐字节／SHA完全恢复**，随后正常solution、328项及Headless51／73／33确认正常产物与行为。

### 7.5 普通手测与阶段边界

按AGENTS§2复制并逐字节核验 **338文件** 普通隔离0.53.0包，独立XDG配置／状态／缓存，已启动PID **1420716**／niri **731**，标题 `MakoKara — 首页 0.53.0`，日志 `/tmp/makokara-backend-0530-startup.log`仅 `MakoKaraGenerator 0.53.0`，无异常。包SHA／命令／进程证据在 `manual-0.53.0/manifest.json`。旧0.52.0 PID1374333／niri707及包保留；无自动交互、用户工程操作或新增截图，启动仅供手测，不等同人工／原生验收。

B1本批共享in-process能力完成，B1整体仍部分。来源新增／删除／配置／主行关联、打点、整行时间平移、元素拆分／合并、选中行重切／全行规则重切与行内元素重排仍由App提交；背景／音频准备及生命周期留App，旧BeginGroup满容量缺陷未修。完整B0代表工程保存／渲染基准、HTTP／SSE／Contracts／Hosting／Server／Worker、队列／协议修订／目录租约、无GUI媒体及真实独立保存重开未完成。全量、性能／采样、正式导出、长片、完整UI重构、物理输入／IME／听辨、Windows／多屏、未知提示和原暂停goal不自动恢复。

<a id="lyrics-branches-0540"></a>

## 8. 第五批：来源新增／删除／受控配置／主行关联（0.54.0）

从 `0d71ecb4c9aaafa0b9e5ef1cf38a9b3ec1d80fbb / 0.53.0` 接续。用户授权继续B1，按ADR-0198交付MINOR `0.54.0`。新增公开共享能力并收窄旧UpdateBranch配置兼容合同；格式28/16/6、配置1、profile2、SDK、生产程序集与固定依赖保持。媒体生命周期、打点、平移、拆分／合并、规则重切与行内重排不随本批迁出。

### 8.1 必要有限原基线与原DLL证据

生产编辑前solution `-m:1` 退出0、零警告／错误；Application68、App24、Core22、宿主依赖2，共 **116项实际执行通过**，无失败／跳过／中止。范围只含来源／FollowPrimary、展示引用清理／独占片段、Workspace／history／save／变化摘要及实际接线；原 `--lyrics-content` Headless **33项**通过、退出0且窗口关闭。338文件原App副本逐字节／SHA固定于 `artifacts/backend-extraction/b1-branches/baseline/original-app/`，SHA清单摘要 `a6e85733811cddc07aee9bb638ae75474fe76419785962ee2a5b987c02e7850f`。命令、有限过滤和4份TRX见 `baseline/manifest.json`。

隔离探针直接引用副本的 **51个原DLL**，无产品ProjectReference；最终构建零警告／错误、执行退出0，**12项检查**确证六类旧行为：

| 原0.53执行体实际路径 | 观察 |
|---|---|
| 新增空翻译来源 | 一次历史但两次工程通知，先见主来源，再见新来源 |
| 非法原呈现树中新增 | 重复主容器ID触发真实呈现拒绝，原工程／历史／事件保持，却返回非空新来源ID |
| 同非法树删除当前翻译 | 拒绝后来源仍在、历史／事件为零，却返回true |
| 实际BranchSettings Save | 有独占片段的主来源改None被拒绝，实际Save仍关闭窗口 |
| 实际新增TextInput等待中编辑→Undo | 原Project引用恢复、Snapshot不同，确认后仍添加旧modal来源 |
| 实际删除Confirm等待中编辑→Undo | 原Project引用恢复后，旧确认仍删除来源 |

前两条假成功使用Editor可构造且PresentationValidator真实报错的原树，不把拒绝当作解析失败。后两条走真实Main→BranchSettings→原默认对话框，并程序化修改工程／Undo，属于隔离Avalonia Headless实证，不宣称物理输入或原生平台缺陷。带当前显式关联的未修改Save没有写历史；实际关闭来源窗口会关闭拥有的TextInput，未发生晚到提交：**两条均未复现**，保留记录，不并入六类。

`original-reproduction/manifest.json`保留所有命令／exits、原51DLL与实际运行51DLL完全相同的SHA及338副本复核。初次探针CPM恢复错误、缺少原native运行库、Headless包选到不同HarfBuzzSharp SHA的执行均保留并排除；显式装入原DLL与原native后才取最终12检查作证据。

### 8.2 共享业务与桌面边界

`LyricBranchEditUseCase` 公开AddTranslation／Remove／Configure／LinkPrimaryLine及请求／结果。单一工作区所有者同步调用，Workspace引用／Generation为共同门禁。新增验证完整ExpectedLyrics，生成全局不冲突ID，空Translation默认FollowPrimary；删除与配置验证完整ExpectedBranch，允许无关composition／其他来源变化。配置只携带元数据、原高级StyleOverride、可选单行PrimaryLineId和可选展示投影；展示同时验证原有效投影，只保护明确来源，保留当前非投影片段。稳定Link只验证原PrimaryLineId，采用当前行其余字段，不覆盖后来正文、元素、注音、样式或精确自身时间；三种时间策略仍可显式关联，空值解除，未改悬空关联保留原Warning。

显式非法元数据／展示在no-op前拒绝，包含展示IsValid、slot-policy和目标行归属；合法相同投影不物化默认program，未提供展示的无变化配置不借机校验／修补旧整树。真实变化沿原呈现校验与一次不可合并命令；无变化保持Project引用、dirty、redo和零通知。删除仅RemoveDeletedSources清理program／clip／lineoverride源引用，保留容器节点、其他来源、效果及预设，悬空效果／预设依旧由原ProjectValidator诊断，不自动删除或重绑。

App旧公开入口作为共享适配；成功结果在Adopt同步来源、行、块、Snapshot与预览后通知一次。新增选择空来源并清空行，删除回到主来源并采用原选择回退规则；呈现拒绝返回真实失败。旧UpdateBranch保留元数据／样式／可选展示与至多一行关联，明确拒绝夹带正文、时间、元素、行结构或多行关联，不再提供任意来源整树替换。旧bool的无变化false保持；内部TryConfigure接受无变化，让实际窗口关闭而不写历史。

来源窗口额外捕获Workspace／Session／Generation／Project／Snapshot；await前后及关闭检查同一固定目标。外部编辑后Undo回同Project仍拒绝旧结果，失败留窗口、原文和本地错误；无变化可关闭。Remove保留确认期间SelectedBranchId等于原来源的门禁，纯选择变化不会删除A或跳回A；Application显式target本身不绑定界面选择。受控晚到关闭回调只作防护回归，未记作原实际关闭已污染。

### 8.3 FollowPrimary集成发现与修复

该问题先作为静态风险保留，后在本批实际窗口流程跑红，**不混入原DLL六类实证**。Independent自身时间为1000499～2000499微秒，待提交草稿0.8～2.2秒包含原元素区间，Core预检证明合法；实际来源窗口改FollowPrimary并关联主行，再切到另一可编辑主来源。旧CommitTimeInputs实际accepted=true、history1→2、自身Time被写800000～2200000，原文重置为派生显示，强断言失败。

App仅在CommitTimeDraft按捕获的稳定BranchId／LineId查询当前行权限；关联FollowPrimary的行时间草稿拒绝并保留原文／错误／工程／历史。不使用当前选中来源判断，不改Core或共享显式自身时间能力；逐字时间API保持。Undo恢复Independent后，同一保留草稿可正常提交。首次探针错误缩短行起点，被token越界拒绝，且dirty关闭确认遮盖末尾错误；该次不作为权限通过。随后合法区间、受控自有Main关闭与accepted／history／time状态记录确证红绿，业务断言未弱化。

### 8.4 最终有限验证与变异恢复

恢复变异后正常solution退出0、零警告／错误。4份终态TRX **Application115、App51、Core22、宿主依赖2，共190项全部实际执行通过**，无失败／跳过／中止；本批新增Application47、App18，另加原时间草稿／编解码9项。原范围未机械扩成前批328或全量，也未恢复旧Import／Settings流程。首轮高级展示的ImmutableArray backing比较错误，仅改为槽位ID／覆盖内容SequenceEqual，身份与内容业务断言保持；首失败日志保留，不记成功。

正常隔离Headless工具构建零警告／错误；恢复相同源码字节后复用该正常独立构建，最终三次运行各退出0并关闭所有虚拟窗口：

| 流程 | 实际通过断言 |
|---|---|
| `--lyrics-branches` | **34**：Main实际来源设置、元数据+主行关联单笔／一次一致通知、精确自身数据／预览／选择、undo／redo、无变化关闭、非法／独占呈现拒绝留窗；默认新增TextInput／删除Confirm与取消、空态、实际来源切换；编辑Undo旧modal、选择变化、换同ID工程、受控关窗迟到；FollowPrimary合法草稿红绿与Undo恢复 |
| `--lyrics-drafts` | **70**：受提交策略变化影响的原合法／非法草稿、精度、IME、过期、导航、保存准备与恢复 |
| `--lyrics-content` | **33**：来源切换／Adopt相邻正文、注音、行操作字段、身份、通知和历史 |

正常最终探针源／日志位于 `artifacts/ui-headless-layout.x6V83X/`。全部命令、exits、TRX、原／正常DLL、源码与日志SHA见 `artifacts/backend-extraction/b1-branches/final/manifest.json`；成功断言不是字形像素、物理IME或原生平台验收。

四项变异各从冻结字节开始，均构建退出0、零警告／错误，再被最窄真实回归断言拒绝，测试退出1：完整来源门禁松成ID（3执行／2失败）；Remove擦除Effects（1／1）；Link擦除自身Time（1／1）；GUI modal忽略Snapshot（1／1），共 **6执行／5断言失败**。`mutations/manifest.json`记录逐项命令／TRX／失败信息；最外层finally恢复，`restoration-check.json`证明 **16冻结源码逐字节／SHA全部恢复**，随后190及34／70／33正常复验。未用编译失败、空过滤或无关夹具错误冒充变异拒绝。

### 8.5 普通手测与阶段边界

按AGENTS§2复制并逐字节／SHA核验 **338文件**普通隔离0.54.0包，独立XDG配置／状态／缓存，启动PID **1463388**／niri **758**，标题 `MakoKara — 首页 0.54.0`；日志 `/tmp/makokara-backend-0540-startup.log`仅版本行，无异常。包、命令、profile与启动观察见 `manual-0.54.0/manifest.json`。没有自动输入、用户工程操作或新增截图，普通启动仅供手测，不等于人工／原生验收。

旧0.53／0.52运行包保持，未操作其窗口或进程；最终只读检查旧PID1420716／1374333已退出，原因未知，未重新启动，不宣称仍存活。仅清理本批自有重复Headless build目录，源／日志／TRX／原DLL和运行包保留，清理清单归入最终manifest。

B1本批in-process来源管理有限出口完成，整体B1仍部分：打点、整行时间平移、元素拆分／合并、选中行重切／全行规则重切与行内重排的业务提交仍在App，共享Core算法或局部时间端点不等于整条流程迁出。媒体／播放生命周期、新建整体准备、旧BeginGroup满容量缺陷、完整B0代表保存／渲染基准继续。HTTP／SSE／Contracts／Hosting／Server／Worker、单写者队列、协议修订／目录租约、无GUI媒体及独立保存重开未实施。全量、性能／采样、正式导出／长片、完整UI重构、物理输入／IME／听辨、Windows／多屏、未知提示与原暂停goal不自动恢复。
B1打点／时间点切片（0.55.0）的实际验证记录见下方第9节。

<a id="timing-stamps-0550"></a>

## 9. 第六批：播放打点共享稳定元素时间用例（0.55.0）

本批从 `d76995b / 0.54.0` 接续。用户授权继续 B1，按 ADR-0199 交付 MINOR `0.55.0`。新增 `LyricTimingEditUseCase.StampTokenStart`／`StampTokenEnd` 与中立 `LyricTimingStampResult`；格式28/16/6、配置1、profile2、SDK、生产程序集和固定依赖保持。应用层接管稳定来源／行／元素目标、Workspace／Generation／原时间门禁、播放头微秒边界、估算占位、状态确认和一次历史；Avalonia保留选择／消息／下一个元素选择及媒体播放生命周期。

### 9.1 语义与调用链

起点打点在元素无有效终点时按工程帧率补一帧，最终状态为 `Estimated`；已有终点仍晚于新起点时保留该终点。终点打点要求已有有效起点且终点严格晚于起点，确认后状态为 `Edited`，即使播放头恰好落在占位终点。播放头始终夹到非负值，正的已准备媒体时长再提供上界，未知时长不把正播放头归零。实际变化只执行一次不可合并的 `token.stamp` 命令；无变化、拒绝不改变工程、历史、dirty、redo或通知。用例不依赖时间轴块列表，App通过 `SelectedTimingBaseline` 捕获稳定 BranchId／LineId／TokenId 和原时间，再由统一 `AdoptProject` 增量同步。

### 9.2 定向回归与变异边界

Application打点回归覆盖未定时补帧／Estimated、未知媒体时长、已有终点、终点确认、占位末端确认、过早／媒体边界拒绝、旧Workspace／原值失效以及一次历史的撤销／重做；原 `LyricsTimingActionsTests` 继续覆盖Avalonia适配与估算提示。定向命令如下：

```text
dotnet test tests/MakoKaraGenerator.Application.Tests/MakoKaraGenerator.Application.Tests.csproj -m:1 --no-restore --filter "FullyQualifiedName~LyricTimingStampTests|FullyQualifiedName~LyricTimingActionsTests"
dotnet test tests/MakoKaraGenerator.Spike.App.Tests/MakoKaraGenerator.Spike.App.Tests.csproj -m:1 --no-restore --filter "FullyQualifiedName~LyricsTimingActionsTests"
```

两条命令均退出0；前者34项、后者14项实际通过，零失败／跳过／中止。Application与App项目分别以 `dotnet build ... -m:1 --no-restore` 构建，零警告／错误。成功编译后的起点占位变异与终点确认变异分别被新增状态／时间断言拒绝，随后恢复源码并重跑上述34项定向回归；变异只验证本切片，不冒充全量或HTTP验收。

### 9.3 阶段边界

B1本批仅完成播放打点in-process有限出口，整体B1仍部分：整行时间平移、元素拆分／合并、选中行重切／全行规则重切与行内重排仍由App提交；背景／音频读取、准备、持久化、播放器与播放轮询生命周期继续留在App。完整B0代表工程保存／渲染基准、HTTP／SSE／Contracts／Hosting／Server／Worker、单写者队列、目录租约、无GUI媒体及独立保存重开未完成。未启动原生／常驻程序，未操作用户窗口或新增截图；全量、性能、正式导出／长片、人工输入／IME／听辨、Windows／多屏、未知提示和原暂停goal不自动恢复。

<a id="b1-closure-0560"></a>

## 10. 第七批：B1共享编辑用例收口（0.56.0）

**已交付：B1共享编辑收口验收完成。** 分支`refactor/individual-backend`从`5f92374cf6f9a379789bc03235edda584f574f67 / 0.55.0`接续。本批统一MINOR`0.56.0`，新增Application公开编辑能力，依据ADR-0200；格式28/16/6、配置1、profile2、SDK、生产程序集和固定依赖保持。

### 10.1 共享入口与原语义

| 现有入口 | 共享用例 | 保留语义与门禁 |
|---|---|---|
| 整行平移预览／提交 | `LyricContentEditUseCase.PreviewShiftLine`／`ShiftLine` | Workspace／Generation、来源／行ID及完整原行；精确有符号微秒、非负／溢出／已准备媒体上界；只平移行和已定时元素 |
| 行内元素重排预览／提交 | `PreviewReorderToken`／`ReorderToken` | 完整原行、稳定元素；下标只在固定原行内解释；时间冲突仍要求明确确认重分配时间槽 |
| 元素拆分／合并 | `LyricTokenEditUseCase.SplitToken`／`MergeToken` | 按Unicode文本元素中间拆分，左身份／属性保留、右新ID，两半Estimated；合并保留第一身份／属性，任一未定时则Missing，双时间取并集及原状态规则 |
| 固定行手工边界重切 | `ResplitLine` | 完整原行、无空段、拼接等于正文且边界不拆Unicode文本元素；原Core保时／估时计数与样式规则 |
| 明确来源全行规则重切 | `ResplitBranch` | 完整原来源与有效规则；全歌词含注音ID分配；全部行准备完再提交，耗尽／领域拒绝不留部分修改 |
| 播放头起点／终点打点 | 0.55.0的`StampTokenStart`／`StampTokenEnd` | 本批补接主窗口歌词任务`[`／`]`；文本焦点和字段草稿门禁保持；终点结果选择在工程通知前同步 |

预览只返回候选及摘要，不修改工作区、历史、dirty或redo。实际变化一次不可合并命令，拒绝与合法无变化保留原工程引用、历史、dirty和redo。拆合／重切保留行正文、行区间、样式、主行关联和原注音记录，不自动重绑悬空注音，不删除真实Warning。比例端点使用`Int128`中间乘积和整数偶数舍入，修复双精度在大微秒值下的端点精度损失；Core重切仍沿原UTF-16文本长度权重，直接拆分仍按Unicode文本元素数量，未换权重合同。

App删除迁出入口的领域改写副本与`ReplaceLineTokens`，只捕获目标、草稿、确认和本地化结果。行平移／时间槽重分配确认捕获Workspace／Session／Generation、Project／Snapshot和稳定来源／行／元素；重切对话框持有固定来源与完整原行，按捕获目标提交。编辑后Undo恰好回原Project时Snapshot仍拒绝旧确认；拒绝保留窗口／输入。FollowPrimary的GUI行时间只读策略与领域显式时间能力保持分离，不新增领域权限规则。

### 10.2 一次提交、增量与选择边界

前批时间、正文和注音按稳定ID更新受影响行／元素及时间块的出口继续复用。整行纯时间平移走时间摘要；正文和元素同时变化且能证明范围的重排可走内容摘要；拆合／重切、来源或行结构与未知字段允许保守完整`Reload`。结构变化按稳定ID找回选择：拆分选择新右元素，合并选择保留元素，单行重切选择明确结果行；终点打点按原路线移到下一个元素。读模型、选择、预览快照和历史就绪后一次`ProjectEdited`，无变化不发布工程变化通知。

0.50.0已删除State的重复`RefreshSceneEditor`，本批沿`UpdateProject`→`RefreshPlacementState`单一刷新出口，不重新添加调用。正式`ProjectSnapshot`仍完整建立；结构／未知变化的保守重建及该快照成本保持，不宣称所有局部动作无整表重建或性能已改善。

删除无消费者的App`Group`适配后，`samples/`与`src/`中`BeginGroup`业务调用只剩`ProjectWorkspace`转发。旧满容量缺陷未修：组起点保存`_undo.Count`，容量截断后`HasGroupEntry`的Count比较无法确认新组已有条目，后续命令可能各占一步。B1新路径不用显式组；本次移除路径依赖，不是底层修复，也不改变公开合同。

### 10.3 验收归档入口

最终清单为`artifacts/backend-extraction/b1-completion/final/manifest.json`。solution构建退出0，零警告／错误；有限定向Application **285**、App **113**、Core **59**、宿主依赖 **2**，共 **459项**全部实际执行通过，无失败／跳过／中止。隔离Headless六流分别为`--b1-line-transforms` **58**、`--b1-tokens-stamps` **68**、`--lyrics-drafts` **70**、`--lyrics-content` **33**、`--timing-actions` **41**、`--lyrics-branches` **34**，共 **304项**，全部退出0并关闭虚拟窗口。三项变异均成功编译、各由真实回归断言拒绝；32个冻结源码文件逐字节／SHA恢复，恢复后再次完成正常构建与定向验收。普通338文件隔离手测包已从最终输出逐字节核验并启动PID **6260**，日志`/tmp/makokara-backend-0560-startup.log`仅`MakoKaraGenerator 0.56.0`；未自动输入、操作用户工程或新增截图。上述计数、命令、TRX、日志、SHA、进程和隔离配置均由最终manifest与其证据文件保存。

本批不运行原生自动化、不新增截图。Headless通过真实隔离Avalonia控件／弹窗／确认／键路由验证，不能记作字形像素、物理键盘／IME、听辨、正式导出／长片或平台人工通过。普通独立包启动仅供手测，全量与性能仍按用户旧指示暂停。

### 10.4 B1裁决与后续B2最小切片

B1收口准确范围是方案§6所列歌词／时间／固定字节导入、来源和标量设置的进程内共享编辑及保留Avalonia适配，最终成立仍须本节验收证据，不意味着所有`EditorViewModel.Execute`已消失。剩余直接路径：背景图像／视频／播放／适配设置与音频准备／绑定（B2媒体与生命周期），分析任务（B4），模板套用／继承恢复、动画、场景／预设／作者（B5）；后续提取不能用整棵工程替换绕过。

新建仍由App`NewProjectPreparation`组合固定歌词解析、解码、工程组装、播放设备创建与`LoadAsync`，只共享纯歌词准备。B2下一最小切片先提取无GUI固定歌词候选＋音频解码／时长／波形及领域工程组装，返回结构化报告和可释放候选；App再做播放设备／字体／正式资源门禁，全部成功后才切换旧工程。需要组合Application／Media时增加`Backend.Hosting`，Application仍只依赖Core。窄出口为无窗口／声卡准备成功、空输入／无音轨拒绝、取消／失败释放、旧结果拒绝和App失败保留原工程／历史／播放。

该后续切片不等于B2完成；B2还须资源授权、Contracts／Server、单写者队列／单调修订、目录租约、去重／事件恢复及真实HTTP创建→时间编辑→撤销／重做→保存→重开。完整B0代表保存／渲染基准、HTTP／SSE、服务／Worker、独立保存重开及整体拆分未完成。全量、性能／采样、正式导出／长片、完整UI重构、物理输入／IME／听辨、Windows／多屏、未知提示和原暂停goal不自动恢复。


<a id="b2-new-project-0570"></a>

## 11. 第八批：B2无GUI新建候选首切片（0.57.0）

**已交付本批首切片。** 本批从 `3fdcdce / 0.56.0` 接续；用户授权继续按拆分方案逐批提取，保留可用Avalonia。本批按ADR-0201新增 `Backend.Hosting` 的公开无GUI新建候选能力，统一MINOR `0.57.0`；不改变格式28/16/6、配置1、profile2、作者SDK或固定依赖。

### 11.1 首切片接口与输入边界

`NewProjectPreparationService` 组合Application与Media，接收 `NewProjectPreparationRequest` 中的名称、画布、音频路径、固定 `LyricsImportSource` 和 `LyricsImportOptions`，不读取或写入当前工作区。结果 `NewProjectPreparationResult` 区分成功候选和结构化错误；候选 `PreparedNewProjectData` 包含领域工程、实际解码波形及原结构化导入报告。Hosting不依赖App、Avalonia、ASP.NET、GUI本地化或播放器工厂；Application继续只依赖Core。取消按 `OperationCanceledException` 传播，不将取消记成导入错误。错误码为InvalidRequest／NoAudioStream／AudioPreparationFailed／LyricsImportFailed／EmptyLyrics；未知实现异常继续抛出，不全部归为用户输入错误。

固定输入仅指歌词字节与解析选项。音频仍是受信本机路径：媒体探测、波形解码及桌面播放准备可能分别读同一路径，期间外部改写／删除未被不可变资产协议防止。本批不提供音频内容快照、上传资产、资源授权、目录租约或HTTP能力。候选仅持托管工程／波形／报告，无暂存文件或原生播放资源，因此不添加没有实际资源职责的Dispose；App新增播放器才承担显式释放与移交。

### 11.2 Avalonia适配与发布边界

App的 `NewProjectPreparation` 只捕获必要输入、调用共享服务、格式化报告与结构化错误、另行创建桌面播放器并完成 `LoadAsync`。歌词路径只在未携带固定候选的兼容入口读一次，实际已选候选不因后来删除路径而重读。原字体Shape门禁保持在工程发布及候选播放器取走之前；播放器准备失败、取消及过期结果释放候选播放器并保留旧工程／历史／播放。

主窗口新建确认额外捕获Workspace、输入Session、Generation、Project及Snapshot，拒绝换工程、关闭、外部编辑和编辑后Undo回原Project引用的旧结果。此处保护异步结果消费，不新增传输revision，也不承诺任意UI事件回调异常都能在StartNew之后事务回滚。VM释放另以disposed门禁拒绝迟到候选；播放器Take／Dispose先用Interlocked.Exchange清除所有权，释放自身抛异常也不重复释放。实际回归证据见本节11.3。

### 11.3 验收归档与状态

原0.56.0必要有限基线solution退出0、零警告／错误，Application92／App57／Media53／依赖2共204项实际执行通过；338文件原App输出逐字节核验并保留。直接引用固定原DLL的实际新建菜单→真实modal→延迟播放器准备证明：外部编辑后Undo回原Project引用时，旧新建结果仍替换工程、清redo并释放旧播放。正式复现9个PASS、构建零警告／错误；首个直接引用警告版本排除，不代替固定复验。

19个冻结源码文件在变异后逐字节／SHA恢复。两项变异均成功零警告／错误编译，再被真实断言拒绝：去掉Host解码后取消门禁使迟到成功被采用，取消定向TRX实际执行1项失败1项；去掉VM Snapshot门禁使编辑后Undo复活，实际菜单／modal的原工程保持断言失败。恢复后正常solution退出0、零警告／错误，有限定向Application **92**、App **64**、Media **53**、Backend.Hosting **31**、Runtime.Hosting依赖 **2**，共 **242项**全部实际执行通过，无失败／跳过／中止。Hosting包含托管候选／原默认合同、奇数画布、实际采样时长、报告集合冻结、固定Unicode歌词、空／非法／无音轨拒绝、未知异常与取消等待；App包含原生命周期及新建Snapshot／Generation／Workspace／disposed拒绝和一次播放移交／释放。

恢复后隔离Headless的 `--b2-new-project` **89** 项、邻接 `--lyrics-import-settings` **51** 项，共 **140** 项，全部退出0。前者覆盖真实Main菜单→默认NewProjectDialog→共享准备→字体门禁→发布、失败、取消后晚成功／晚IO、空歌词、编辑Undo／同Project新Generation拒绝、旧工程／dirty／redo／选择／波形／报告／播放保持及专用窗口释放；后者复验原导入／设置／固定歌词路径选择链。均为内存平台虚拟窗口，不是字形像素、物理输入／IME、声卡听辨或平台人工验收。

最终清单已归档至 `artifacts/backend-extraction/b2-new-project/final/manifest.json`，终态为 `normal-final-acceptance-complete-package-ready`。solution零警告／错误；Application92、App64、Media53、Backend.Hosting31、Runtime.Hosting依赖2共242项有限定向全部通过。真实无窗口媒体使用ffmpeg／ffprobe 9.0.2，30项全部通过：16000Hz单声道32000采样、2000000微秒、0.8／0.25／0.6峰值、无音轨／非法容器／路径丢失／取消后子进程回收与重试均覆盖。隔离Headless B2 89项、邻接51项全部退出0；19个冻结源码逐字节／SHA恢复。341文件普通隔离包已逐字节核验并以独立XDG配置启动，PID 167170，日志`/tmp/makokara-backend-b2-0570-startup.log`。初轮Host误将媒体末端计作Fallback、App漏using、Headless探针编译／文件夹具问题、原DLL首次引用警告和独立媒体探针编译／序列化／活跃PID断言失败均保留排除。真实decoder短片自然过早退出造成活跃PID检查失败时，只调整artifact探针的输入节奏与进程追踪；生产媒体源码和取消断言不改。终态退出码、TRX、日志、manifest与SHA已齐备，本批裁决为首切片交付。

### 11.4 后续边界

本批仅为B2无GUI新建候选切片，不是独立API链路或B2完成。Backend.Contracts／Server／Worker、HTTP／SSE、单写者队列／单调修订、资源授权、固定音频资产、请求去重／事件恢复、目录租约、无GUI字体／正式资源准备和真实HTTP创建→编辑→撤销／重做→保存→重开继续未完成；背景／音频绑定及完整媒体生命周期仍待后续。完整B0代表保存／渲染基准、全量、性能／采样、正式导出／长片、完整UI重构、物理输入／IME／听辨、Windows／多屏、未知提示和原暂停goal不自动恢复。


<a id="b2-transport-seed-0580"></a>

## 12. 第九批：B2-A传输与loopback会话种子（0.58.0）

**种子代码已提交并推送，验收边界有限。** `a8c8a63 / 0.58.0` 从 `2006222 / 0.57.0` 接续，按ADR-0202新增BCL／JSON传输契约 `Backend.Contracts` 和ASP.NET Core组合根 `Backend.Server`，Hosting保持不依赖ASP.NET／App。公开API与程序集递增MINOR；工程／歌词／模板格式28/16/6、配置1、profile2、作者SDK和固定依赖保持。

### 12.1 固定资源与创建会话

资源入口接受有界上传字节，复制到服务控制目录，完成后发布assetId、SHA-256、大小、MIME和原文件名；拒绝空内容、单文件超限、总配额超限、路径穿越和其他客户端标签下的资源访问。默认单文件16MiB、累计资源128MiB，允许MIME由capabilities公布。客户端不能指定服务存储路径；创建只引用上传assetId，歌词解析与音频准备使用托管副本。这里的固定是服务托管上传输入，不承诺抵御拥有本机文件权限的外部改写或完整资源租约／回收。

`POST /api/v1/project-sessions` 同步准备音轨／波形和主歌词并返回201摘要；失败不注册会话。创建使用既有无GUI共享准备服务，不访问窗口或声卡。每次服务启动生成hostInstanceId，摘要包含projectSessionId与初始字符串修订 `"0"`；会话及资源按客户端标签隔离。种子工作区曾以 `isSaved:true` 收养，初始isDirty为false且没有hasPath／savedRevision；这与Avalonia `StartNew(...,isSaved:false)` 不一致，属于本批B2-B修正的新建状态缺陷，不是磁盘保存保证。Avalonia继续其原进程内调用，独立服务不共享App的裸工作区。

### 12.2 初批证据与后续补验

0.58.0种子提交时Backend.Hosting共35项通过，Server成功编译；本机缺少 `Microsoft.AspNetCore.App 10.x`，初次HTTP探针未实际启动，不能记作HTTP／真实媒体验收通过。早期探针使用注入的假媒体，只能作为控制流诊断，不能作为FFmpeg解码证据。0.59.0批次继续补独立进程self-contained启动、真实WAV与HTTP断言，实际最终证据归入第13节；不把补验倒记为0.58.0提交时已通过。

### 12.3 本机边界

当前 `X-Mako-Client-Id` 是调用方自报的临时隔离标签，不是随机凭证认证。标签归属检查不能抵御冒用同一标签。随机启动token、clients握手／租约、Host／Origin约束、正式根授权和资源内容／Range入口尚未完成，不能把此种子当作可供任意网页或远程客户端使用的服务。产品目标仅为本机loopback服务；Web前端与Tauri外壳没有在本批交付。


<a id="b2-timing-http-0590"></a>

## 13. 第十批：B2-B具名时间命令、历史与一致查询（0.59.0）

**本批有限切片已完成验收。** 本批接续 `a8c8a63 / 0.58.0`，按ADR-0203为独立会话开放 `lyrics.setTimeRange`、`history.undo`、`history.redo` 与最小歌词／历史查询。MINOR0.59.0新增公开HTTP／Hosting能力；格式28/16/6、配置1、profile2、作者SDK、固定依赖和Avalonia进程内调用保持。

### 13.1 单写者、精确修订与历史

每个会话的命令和摘要／历史／歌词查询共用同一Writer闸门。请求按clientId会话归属与hostInstanceId隔离；registry先校验旧host再查session，重启后旧session已不存在仍返回HostRestarted，而非误报SessionNotFound，Writer内继续校验主机身份；expectedRevision是非负十进制字符串，实际写入前校验。时间命令使用branchId／lineId／可选tokenId，两个端点为十进制微秒字符串，不按列表位置定位，不暴露Core工程对象或允许任意对象补丁。Hosting映射到同一 `LyricTimingEditUseCase.Commit`，沿用媒体时长与领域包含／顺序规则。

一次实际时间提交执行一次历史；撤销／重做也是实际工程变化，分别递增projectRevision。合法相同端点返回changed:false，不改变原工程引用、dirty、redo、历史数量、修订或eventCursor。失败不写入工程。本批同时修正B2-A新建错误的保存基线，改为 `isSaved:false` 并在摘要返回 `hasPath:false`；初始新建及首次编辑后撤销回初始内容仍为dirty:true，与Avalonia新建一致，尚无已落盘快照／savedRevision。命令返回previousRevision／projectRevision、dirty、历史可用状态与保守changeSet；歌词投影和历史读模型返回各自原子捕获的工程修订。一次查询内部一致，不承诺多个独立GET自动共享同一快照；完整分页／指定读修订尚未实现。

### 13.2 命令回执与错误

命令必须携带requestId、hostInstanceId和validUntilUtc，Hosting独立校验，不只依赖HTTP路由。当前回执作用域为 `(clientId, projectSessionId, requestId)`，同requestId在不同会话可以各执行一次；不等于API草案跨方法／路径的client级统一去重。回执在会话Writer内先查后校验expectedRevision；相同ID与相同规范化DTO载荷返回首次结果，旧expectedRevision的合法重试不重复执行。相同ID不同载荷返回409 RequestIdReuse，过期／超出允许窗口／旧host与无效字段在副作用前拒绝；回执有界，不驱逐仍有效结果。默认最大重试窗口600秒、每会话最多1024条有效回执，Hosting通过 `ProjectCommandRetentionOptions` 配置；capabilities公布serverTimeUtc、maxCommandRetryWindowSeconds、maxCommandReceiptsPerSession。每次锁内清理已到期回执，有效槽已满则拒绝新请求，不提前驱逐。指纹使用DTO固定字段序列化的SHA-256、合法整数的十进制规范化和UTC规范化截止时间；未知命令请求／命令／target DTO字段拒绝，history命令携带时间目标／端点也拒绝；它不是任意JSON／multipart的通用规范化协议。

本批修正资源上传卫生：每次写入前将staging字节纳入累计quota，并发上传不能在检查配额前先写完整文件；发布重命名前关闭独占写句柄，失败／取消回滚配额并清理临时文件。该修复不代替TTL、资源租约或Windows人工验收。Server限制监听地址为显式loopback，拒绝非本机绑定；不等于完成Host／Origin和客户端认证。

Server以稳定code／message／可选field返回结构化错误，媒体类型为 `application/problem+json`，最终HTTP探针确认51个错误响应头与预期状态码，不宣称完整RFC problem字段已实现。projectRevision与时间微秒的字符串精度合同已进入DTO；其他64位字段仍有数字形式，完整精度映射待后续。eventCursor当前返回null，HTTP省略该字段；无SSE／事件日志／缺口补传，不可用于断连恢复。

### 13.3 最终有限验收归档入口

本批统一终态入口为 `artifacts/backend-extraction/b2-commands/final/http-and-solution-evidence.json`；Hosting TRX／计数与HTTP manifest分别保留独立明细，以下计数仅来自最终冻结执行体。

Backend.Hosting最终61项实际执行全部通过，零失败／跳过，测试进程退出0：Transport22、NewProjectPreparation29、Dependency3、Resource7。终态TRX为 `artifacts/backend-extraction/b2-commands/final/hosting-final.trx`，计数及7文件SHA-256在同目录 `hosting-final-evidence.json`。过程中的拆分漏using（3个CS0246）、测试格式化文化门禁（3个CA1305）及误以为hello只有一个token的测试断言（第三轮59过1失败）均保留排除；按稳定tokenId查找修正后60项通过，再补服务重启回归取得最终61项；未把夹具／测试错误记为生产缺陷。

最终solution构建退出0，零警告／错误，日志 `artifacts/backend-extraction/b2-commands/final/solution-build.log` 用时3.47秒。独立Server编译与linux-x64 self-contained发布成功；本机未安装ASP.NET 10运行时，探针自动使用独立包，不需root安装。Server生产假媒体开关已删除，测试通过默认FFmpeg／ffprobe n9.0.2读取真实上传PCM16 WAV：44100Hz单声道、88200采样、2秒，创建摘要音频时长为2000000微秒；不使用GUI或声卡。

最终HTTP归档 `artifacts/backend-extraction/b2-commands/http/run-20261012T112424Z-53vh6j/manifest.json` 为passed，688个断言／110次真实请求覆盖创建／字符串修订查询、行与元素精确微秒、同ID／规范化UTC截止重试、409修订冲突、撤销／重做、no-op保留redo、到期回执、跨标签资源／会话拒绝与非法DTO。51个错误响应实际为application/problem+json；小配额服务验证multipart恰好1024字节成功、1025字节413、raw超限413和累计2048字节满后429。新增真实重启边界验证无Host头时正文旧host／未知旧session仍409，以及新host拒绝另一host旧会话。非loopback `0.0.0.0:0` 启动退出-6且没有BACKEND_READY，按预期拒绝监听。

主服务和小配额服务均实际退出0、未强杀，/proc进程已不存在，受控资源目录已清理。最终manifest逐项源码／响应／fixture／日志SHA与当前字节一致；汇总 `artifacts/backend-extraction/b2-commands/final/http-and-solution-evidence.json` 记录manifest／构建／实际Server DLL哈希；同目录 `probe-path-safety.json` 验证 `/`、仓库根和artifacts根不能作为探针删除目标。初次HTTP `run-20261012T111538Z-7PMYxv` 因探针误要求AssetResponse的protocolVersion字段，在23断言／3请求后失败，修正夹具后重跑；其后678断言／108请求是旧host修复前的中间完整正向，不替代最终688／110。原0.58.0缺运行时和假媒体探针不作真实媒体正向证据。

最终暂存检查另发现新增 `ProjectSession.cs` EOF多余空行，仅删除该空行后重跑Hosting61项、solution与HTTP688／110，刷新精确源码哈希；之前的终态保存在 `final/before-eof-trim/`，没有行为或验收范围变化。

本批没有修改App，没有执行Avalonia Headless／原生窗口、变异或全量／性能验收；只裁决上述Hosting与独立HTTP有限出口完成。

### 13.4 后续阶段与保留暂停范围

本批窄出口是上传→独立新建→一致查询→时间编辑→撤销／重做及有限命令重试，不是完整B2。上传与创建尚不具备上述命令回执协议；客户端认证、Host／Origin、资源租约／TTL／Range、会话关闭／崩溃恢复、目录写租约、独立保存／打开／重开、SSE／事件恢复、任务／Worker、帧／正式字体资源准备及完整API继续未完成。当前服务内存会话失去进程即失效，eventCursor不能补偿这一缺口。后续先完善本机安全握手与生命周期，再接目录租约和真实保存重开链路。

完整B0代表保存／渲染基准、全量、性能／采样、正式导出／长片、完整UI重构、物理输入／IME／听辨、Windows／多屏、未知提示和原暂停goal不自动恢复。本批不启动Avalonia常驻窗口，不把无GUI HTTP通过扩展为原生或人工平台验收。

---

<a id="b2-lifecycle-persistence-0600"></a>

## 14. B2-C：本机生命周期与独立持久化闭环（0.60.0，有限出口已验收）

### 14.1 授权范围、版本与阶段出口

用户已授权继续完成B2剩余范围。起点为 `99debfe / 0.59.0`；本批统一使用产品版本 `0.60.0`，按实际共享接口交付一个可独立构建的聚焦批次，不把互相依赖的接口人为拆成不可运行的版本。工程／歌词／模板格式28／16／6、配置1、包profile2、作者SDK、生产程序集划分与固定依赖保持。产品版本精确加载门禁继续有效，最终工程夹具必须由0.60.0生成。

本机发布、可信凭证文件、授权目录根、Origin、Bearer／SSE／Range访问方式见[Server启动说明](../../backend-server-runbook.md)。

本批目标是首条完整独立链路：本机客户端登记与受控资源 → 新建／打开 → 精确时间编辑与撤销／重做 → 捕获快照保存、期间继续编辑 → 显式关闭 → 重开核对内容与资源；同时覆盖基本事件恢复、目录写租约、保存任务退出与崩溃结果不明的诊断。上述有限出口已验收；Worker／帧／预览、正式导出／验证／分析任务和场景／作者闭环仍按B3～B5接续。

### 14.2 已固化的本批合同

- 服务仅用于用户本机。可信启动通道提供随机启动凭证，登记后使用独立访问／恢复凭证及clientId；检查实际loopback连接、Host与显式Origin。凭证不放URL或日志，不将本批写成远程账户或恶意脚本沙箱。
- 有副作用请求的回执由Hosting单层拥有，按clientId与requestId限定作用域，同时绑定操作和语义载荷、hostInstanceId及UTC截止时间；先查询回执，再校验领域修订。有效占位不驱逐，结果按数量和字节有界，执行或结果序列化失败也必须终结占位。HTTP取消只结束准备／响应等待，不能重放已接受副作用。
- 工程命令、保存完成采用及相应事件位于同一会话Writer。客户端state锁定成员集合及按稳定顺序排列的全部会话Writer后捕获摘要与事件游标；SSE有界重放，窗口缺口明确要求重新查询state。访问令牌失效或连接断开不隐式放弃工程。
- 上传、准备、工程与读取响应实际持有资源引用；未引用资源按TTL回收，读取结束才释放流及引用。音频Range／ETag使用独立字节响应，资源字节和准备并发受限。
- 目录写租约复用Application，服务内与跨进程的参与者共同使用物理目录身份。保留App的打开、手工保存、另存为、自动备份、换工程及关闭路径也参与；保存候选失败保留旧租约，执行中的备份／保存到安全点后才释放。旧不参与租约的执行体及外部工具仍不具备互斥保证。
- 保存捕获第N版的不可变输入，IO期间允许编辑到N+1。完成只将实际写出的第N版标为已保存，dirty仍按内容引用判断；savedRevision不替代dirty。关闭显式处理未保存内容与活动任务，进入不可撤回发布后拒绝取消；失败保留会话。正常服务退出停止新请求并等待已接受写入，再释放目录与资源。
- 持久profile保存有界的最小任务账本及恢复快照。账本记录接受、提交阶段和终态与输出身份；重启时缺少确定终态的记录标为结果不明，不推断成功／失败、不自动重放。客户端核对实际工程；不承诺跨崩溃exactly-once、完整撤销历史或作者草稿恢复。

### 14.3 独立审查与验证状态

独立审查已沿真实App保存协调器、Workspace租约、Hosting请求回执／事件与新任务调用链开展。施工中发现并交回对应实施者处理的边界包括：HTTP与Hosting双层回执会造成RequestIdReuse或等待自身占位；回执结果序列化失败可能留下永久pending；state与关闭的锁顺序；查询在等待Writer后需重新检查已关闭状态；自动备份在换工程前的租约保留；打开后的托管音频必须实际进入保存输入。这些项目以最终源码与回归证据裁决，代码调整本身不算修复验收。

最终冻结验证入口为 `artifacts/backend-extraction/b2-final/http/run-20261012T134242Z-oMcgcE/manifest.json`：真实HTTP 1224断言／241请求，主服务、配额服务、租约／重启／崩溃／取消子服务均按预期退出并清理；外部监听拒绝，GUI／声卡未使用。solution构建0警告／错误，Hosting定向82/82通过。manifest逐项源码与证据哈希已核对，包含凭证轮换、SSE重放／缺口、Range／ETag、资源TTL、路径根、双进程租约、保存期间编辑、关闭重开、崩溃账本unknown和shutdown排水。探针已改为只记录脱敏响应／状态／哈希，全部文本证据扫描未发现access/resume/bootstrap凭证。先前0.59.0的61项Hosting、688／110及本批中间941／175、1134／220、1218／239和1221／240只保留为过程证据，不替代该冻结出口。

### 14.4 保留范围

不自动恢复完整B0、全量、性能／采样、正式导出／长片、原生常驻窗口、用户窗口操作／截图、完整UI重构、物理输入／IME／听辨、Windows／多屏、未知提示或原暂停goal。完成B2基本协议与保存链路也不代表完整API、Web产品、Worker、渲染等价或所有平台已经验收。


<a id="b3-preview-worker-0610"></a>

## 15. B3-A：中立预览协调器与受管理 Worker IPC 接缝（0.61.0，2026-10-13，有限技术出口已完成）

### 15.1 交付范围

本批接续 `1ed127d / 0.60.0`，将 B3 拆成可回滚的第一条技术接缝。新增 `BackendPreviewCoordinator` 位于 `Backend.Hosting`，只依赖 `Rendering.Abstractions` 的 `IRenderFrameSessionFactory`／`IRenderFrameSession`，不依赖 Avalonia、SukiUI 或 `TechnicalSpike.App`。每个协调器最多保持一个执行中的帧请求和一个最新待处理请求；播放头向前移动只替换待处理槽，输入代次、来源修订、快照、工厂或静态渲染选项变化时取消在途工作并释放旧会话。

请求显式携带非负 `PreviewGeneration`、不透明 `SourceRevision` 和单调 `RequestSequence`。结果只在代次／来源／快照／工厂／静态选项仍匹配且实际时间没有倒退时发布；协调器复制像素与诊断快照后立即释放底层 `RenderFrame`。事件在内部锁释放后调用，订阅者可重入提交或开始释放；回调异常不会污染渲染工作。该修复同时覆盖原 Avalonia `PreviewFrameCoordinator` 的锁内事件问题，并保留原有最新槽与旧输入门禁。

`BackendPreviewContracts` 新增只依赖 BCL/JSON 的预览来源、尺寸、透明模式、会话／帧请求、二进制句柄元数据、诊断分页和 B3 稳定错误码。它们只定义传输边界，没有把 Core 对象、Skia、Avalonia 或裸像素对象暴露给 HTTP。

`Backend.Worker` 与 `WorkerProcessProxy` 提供受管理进程生命周期：输入通过 `ArgumentList` 启动，不拼接 shell；私有 `worker-1` 协议采用有界 little-endian 长度前缀 JSON，单消息上限 8 MiB、opaque payload 上限 4 MiB，并校验协议版本、requestId、输入 SHA-256。Hosting 先握手，再串行调用；取消发送同请求的 `cancel`，超时杀死进程树；正常释放发送 `shutdown` 并等待退出。当前 `render` 仅延迟后回显校验过的 opaque 输入，真实 Runtime.Hosting 会话和二进制帧块留到后续 B3-B。

### 15.2 代码卫生与验证

本批修复了 Avalonia 预览协调器在 `_gate` 内调用 `Updated` 的锁内通知问题；新增回归通过线程池重入提交验证事件确实在锁外。Worker 的并发活动请求表使用 `ConcurrentDictionary`；Hosting 依赖图测试将独立 Worker 排除在宿主程序集图之外，而不是把可执行 Worker 误判成服务依赖。

最终验证：

- `dotnet build MakoKaraGenerator.slnx --no-restore -m:1 /p:UseSharedCompilation=false`：0 警告、0 错误。
- `MakoKaraGenerator.Backend.Hosting.Tests`：93/93 通过，其中真实子进程 Worker IPC 3/3。
- `MakoKaraGenerator.Spike.App.Tests` 预览协调器定向：20/20 通过。

对应提交为 `2d0759f`（Avalonia 锁外发布修复）、`1ad3355`（预览契约）、`0cd0ca4`／`36850ab`／`5bd605f`（Worker 进程、并发与依赖测试边界）及 `fd8b933`（Hosting 中立预览协调器）。本批版本统一为 `0.61.0`，格式 28／16／6、配置 1、profile2、作者 SDK 和固定依赖保持。

### 15.3 有意保留的边界

这不是 B3 完成，也不是完整预览或 Web API 完成。当前没有 Server 预览路由、PNG／二进制帧句柄实际发布、诊断分页查询、真实 Worker Runtime.Hosting 渲染、脚本失控的渲染级隔离、持续播放与真实透明像素等价验收。后续 B3-B 应先把固定渲染输入与 Runtime.Hosting 会话接入 Worker，再增加受限帧块／句柄和 Server/SSE 事件；B4 导出／验证／分析、B5 作者闭环、完整 Web 客户端及全量／性能／Windows／人工平台验收仍不自动完成。


<a id="b3-frame-integrity-0620"></a>

## 16. B3-B：预览帧发布完整性合同（0.62.0，2026-10-13，有限技术出口已完成）

### 16.1 交付范围

本批在 `0.61.0 / fd8b933` 的中立预览协调器、传输DTO和Worker IPC接缝上，新增 `PreviewFrameContract.Validate`。发布方必须同时提供当前预览会话、代次、来源修订、上一个已接受请求序号、服务端限制和实际二进制内容；验证器按固定顺序检查会话归属、代次／来源、严格递增序号、正数尺寸与尺寸上限、`image/png` 内容类型、内容大小、句柄租约、句柄字段格式和 SHA-256。验证失败只返回稳定 `BackendErrorResponse`，不生成可发布句柄。

`PreviewFrameExpired`、`PreviewGenerationConflict`、`PreviewRequestOutOfOrder` 和 `PreviewFrameNotFound` 分别表达租约到期、输入过期、序号倒退和句柄／内容不一致；普通字段越界使用 `InvalidRequest`。合同只验证已经生成的字节，不访问文件路径、不编码PNG、不创建Worker、不添加HTTP路由。

### 16.2 验证与边界

版本提升为 `0.62.0`，格式28／16／6、配置1、profile2、作者SDK和固定依赖保持。solution构建0警告／错误；`MakoKaraGenerator.Backend.Hosting.Tests` 98/98通过，覆盖合法帧、过期句柄、旧代次、序号倒退、哈希不一致以及此前的Worker／Hosting／预览回归。

本批不宣称真实Runtime.Hosting渲染、PNG编码、句柄租约存储、Server帧路由、诊断分页、脚本失控隔离、持续播放或透明像素等价。下一批应把固定渲染输入接入Worker，再由Server使用本合同发布受限帧句柄；B4导出／验证／分析、B5作者闭环、完整Web客户端及全量／性能／Windows／人工平台验收仍未完成。


<a id="b3-runtime-frame-0630"></a>

## 17. B3-C：真实 Runtime 帧适配与受保护读取（0.63.0，2026-10-13，有限出口已完成）

### 17.1 交付范围

本批在 `0.62.0 / 1bc24db` 的完整性合同上接入真实 `Runtime.Hosting` 同进程帧适配。`RenderFrameV1SessionFactory` 固定 `ProjectSnapshot` 引用，校验目标尺寸和 `width × height × 4` 预算，合并调用令牌与 `RenderRequest.CancellationToken`，串行化会话渲染／释放，并在输出目标、时间、像素格式或长度不一致时先释放帧再拒绝。真实静态无包工程通过既有 `ProjectRenderSessionFactory` 进入该适配器；这证明了中立帧接缝与真实Runtime的同进程连接，不代表跨进程Worker已经承载Runtime。

`PreviewFrameHandleStore` 只接受经过会话／代次／来源／序号、PNG signature、大小、租约和 SHA-256 验证的实际字节，按 clientId 隔离，帧数量和总字节有界。`PreviewDiagnosticsStore` 绑定客户端、预览代次和来源修订，限制页大小并拒绝失效游标。Server 新增：`GET/HEAD /api/v1/preview-frames/{id}/content`（ETag、单 Range、If-Range、HEAD、TTL与410过期反馈）以及 `GET /api/v1/preview-sessions/{id}/diagnostics`；路由只读已登记句柄，不创建会话、不读取任意路径、不伪造像素。

同时修复 `WorkerProcessProxy.StopAsync` 在取消路径下只释放 `Process` 而未回收子进程树的风险；现在取消后仍杀死并等待整个树、排空 stderr，再传播取消异常。

### 17.2 验证与边界

版本提升为 `0.63.0`，格式28／16／6、配置1、profile2、作者SDK和固定依赖保持。solution构建0警告／错误；Backend.Hosting 105/105、Worker IPC4/4、Runtime.Hosting真实静态帧1/1、Avalonia预览20/20通过。Server self-contained 实际启动验证未知帧返回 `previewFrameNotFound`、未知诊断返回 `previewSessionNotFound`，problem+json，服务正常排水退出。

本批仍不宣称：跨进程Worker真实Runtime渲染、预览会话创建/帧生成HTTP任务、持续播放帧流、透明像素等价、脚本失控渲染隔离、完整诊断生产链、B4导出／验证／分析、B5作者闭环或完整Web客户端。下一步应把固定 render-frame-v1 输入序列化到Worker，并把预览会话／任务状态接入Server，而不是让当前读取路由自行生成帧。

<a id="b3-static-preview-0640"></a>

## 18. B3-D：静态工程Worker与HTTP预览（0.64.0，2026-10-13，有限出口已完成）

### 18.1 能力范围

本批从 `bc6b26d / 0.63.0` 接续，完成固定静态工程→专用私有IPC→真实Worker→原Runtime→真实PNG→受认证HTTP读取的有限链路。40字节纯色Worker帧只作最初进程接缝，不能代表工程。专用 `snapshot-v1 / static-lyrics-scene-v1` 保留真实歌词、多来源、注音、静态样式与场景、整数微秒及完整三态；全部15个源字段有明确位置，源成员白名单排除计算属性和可写访问外观。编解码不复用工程文件或读取路径。AudioPath／AudioMedia无损保留声明，Hosting另持授权资源租约；path-only不制造固定身份。非空效果包、预设、包材质、分析和背景图片／视频明确拒绝。

已提交步骤：`3c44225`修帧／诊断存储有界与真实PNG结构验证，`c7a0b06`纯色真实Worker裸帧，`810e69d`预览代次与固定工程所有者，`5d516c6`静态codec，`d4be13f`依赖图合同，`1195efb`PNG适配，`c36d0e3`持久静态Worker，`c1e439d`Server接线，`60b877d`版本与solution，`c52ac15`像素预算与诊断来源验证，`46c0b2a`Worker首次Create／open的中立失败修复，`d640807`真实HTTP成功／失败验收工具。产品代码统计终点为 `d640807c8ec5597f0b472ded598b855af69d8897`；本次验收文档提交不计入该源码统计。

Worker每次Create拥有独立进程，open只解码一次并创建一次真实 `ProjectRenderSessionFactory` 会话，同代次连续前向帧复用该Runtime。私有Worker接口可在同快照／同会话请求后退时刻；HTTP seek会换generation并由Coordinator.Invalidate排水／释放旧Runtime及Worker，下一帧重新open。这两种寻址路径不能混同，也不据此宣称HTTP seek性能提升。每帧绑定固定session、源SHA、序号、目标、整数时间、透明模式与质量，验证紧密BGRA premultiplied像素、输出长度和hash；close／取消／超时排水并回收拥有的进程树。诊断发生以有界专用DTO保留身份与编号，`RenderSessionFailureException`跨中立接缝保留稳定code和实际Runtime快照。首次启动后执行文件被删除、无执行权限、open错误或畸形回执也沿同一失败链；没有真实快照时不伪造作者发生。

Hosting固定工程的精确修订，持输入保留租约和独立运行租约；单执行＋最新待处理槽、输入换代、seek、关闭、客户端休眠与主机退出都有释放出口。Server新增创建／换输入／帧请求／状态／关闭和runtime-info，沿原client认证、Host／Origin与单层回执；HTTP断开不取消已接受工作。SSE发小型状态／帧提示，PNG仍由受保护句柄读取。独立 `Backend.Rendering.Skia` 只做像素传输编码，将预乘BGRA转换为straight-alpha PNG；Hosting不引Runtime／Skia，Contracts保持BCL。实际路由、DTO、部署要求和配额见[Server合同](../../backend-preview-server-static.md)及[Worker合同](../../backend-worker-static-session-contract.md)。

生产最大尺寸1280×720且裸像素不超过4MiB；尺寸必填，不把上限称为自动默认尺寸。快照净预算4,194,280字节已扣24字节open头；每会话保留2帧／8MiB，最多32会话／每client4会话，帧TTL默认30秒（可配置1～300秒），诊断页128项。Worker自身4MiB裸帧预算不能支持1920×1080完整帧；完整诊断预算256项／64KiB，超限明确失败而不截断。

### 18.2 最终统一验证

源代码冻结后按共享 `flock /tmp/mkg-b3d-dotnet.lock` 串行正常restore／构建／测试，分析器和警告即错误保持。solution构建终态exit0、0警告／错误，以下六个明确文件均终态完成，合计315执行／315通过／0失败／跳过／中止：

| 范围 | 通过 | 最终TRX（统一证据目录内） |
|---|---:|---|
| Backend.Hosting全部当前测试 | 221/221 | `backend-hosting-final.trx` |
| Runtime.Hosting依赖检查＋真实静态Worker | 11/11 | `runtime-worker-final.trx` |
| Avalonia原预览协调器定向 | 20/20 | `app-preview-final.trx` |
| 静态snapshot codec | 40/40 | `static-snapshot-codec-final.trx` |
| 真实PNG编码适配 | 9/9 | `preview-png-encoder-final.trx` |
| Server真实监听器测试（linux-x64 self-contained） | 14/14 | `preview-server-final.trx` |

统一证据为 `artifacts/backend-extraction/b3-d-final-verification/manifest.json`（SHA-256 `948dd1bd2bc46d8c7d5c7ce9d1bed3029cfbaa9a9375fd142e9df0d3b14068a9`），包含明确的六个final文件、正常命令、终态计数、日志、166个测试源文件及16个当前构建DLL的SHA，并引用下节HTTP证据。Runtime范围实际执行原依赖检查2项和原生静态Worker9项，没有排除依赖断言；原生六组合覆盖opaque／transparent×Draft／Standard／Final，各连续三个时刻、回退seek和关闭后重建，并与原Runtime逐字节相等。另三项覆盖同进程Worker close／reopen及旧session／源SHA／序号拒绝。

最后Create／open修复之前的solution日志和Hosting217/217保留为 `*-prefinal.*` 与 `prefinal-excluded.json`，明确排除最终315计数；历史Worker89＋9、registry21、Server11及P2局部19＋14分别保留其原范围，不重复相加。缺ASP.NET共享runtime造成的framework-dependent Server testhost中止也不算通过；最终Server采用正常restore的self-contained运行。

### 18.3 真实HTTP成功与失败链

最终同一组Server／Worker发布体的真实链路通过112个请求、458个工作流断言和247个凭证产物扫描，共705项断言。真实上传音频／歌词→创建工程→固定预览→帧请求→状态与SSE→PNG读取／独立解码，三个实际帧各57,600像素与独立真实Runtime oracle的RGB预乘值／alpha最大误差均0；变化时刻真实像素不同，seek恢复同一PNG。覆盖回执重试、跨client拒绝、HEAD／Range／ETag／TTL、旧代次、输入更新、显式关闭与客户端休眠后的资源释放。探针配置10秒帧TTL仅为夹具，产品默认仍30秒。

同发布体还受控终止探针拥有的真实Worker，实际状态为failed、`renderFailureCode=WorkerSessionConflict`；仅删除探针自己的0700 exec-launcher后，首次Create实际失败为 `WorkerUnavailable`，仓库Worker执行体未改。两条诊断均为 `originKind=transportFailure`、没有occurrence和实际Runtime快照。成功静态帧实际diagnostics=[]，不把这些结果宣称为脚本诊断发生或真实失控脚本验收。

证据为 `artifacts/backend-b3d-encoder-http/http-final3-verified/manifest.json`，同目录含实际SSE、三份逐像素比较及两条受控失败记录。`http-final3-all-sha-manifest.json` 的SHA-256为 `d1c15fdf5703f793e2a68c141111770b4c23b974790230d4cfc3841cbd69bcd9`，112源码、17发布文件、248证据及另列7份补充记录的bytes／SHA逐项核对一致；三工具源清单 `tool-final3-source-manifest.json` 的SHA为 `213d42996a0b7154c23f95fc13e3145aa39cefeab7b314a72d2a0944b7dcbfbe`。Server PID277659终态exit0、四个观察到的Worker均已退出，凭证／launcher无残留、两个缓存已清理。HTTP final／final2为历史过程，不混入final3验收。

### 18.4 编解码与依赖合同的过程证据

codec初次实现曾暴露null媒体类型的元数据递归，以及MemoryStream两个写入重载递归；已修为专用媒体converter与独立有界Stream，不计入成功证据。恢复源码后的codec定向40/40通过，提升到0.64.0后再次40/40、无失败／跳过／中止，实际Core程序集版本及EngineIdentity=`MakoKaraGenerator:0.64.0`已核对。普通／透明各3个时刻原Runtime像素相等，注音与倒计时另由移除／隐藏对照证明真实贡献像素，实际Noto Sans CJK SC字体stream SHA为 `b76b0433203017ca80401b2ee0dd69350349871c4b19d504c34dbdd80541690a`。剥离注音和把set-null变unset两项变异均成功编译后各1/1真实断言失败；两个源文件逐字节／SHA恢复并正常复验。

旧Runtime.Hosting依赖检查实际执行2项，1项因Server／Worker的合法Exe组合根失败、1项通过；规则修正仅允许这两个已命名根作为执行体，所有引用边仍禁止共享库反向依赖Exe／GUI，Runtime源码引用仅Worker白名单、Hosting递归禁止Runtime／Skia、Core禁Rendering.Skia、Contracts无ProjectReference。修后2/2通过，无排除该检查。

codec过程证据：`artifacts/backend-b3-snapshot-codec/static-snapshot-codec-0640-final.trx`、`manifest-0640.json`与`mutations/manifest.json`；最初源文件／证据清单保留于`manifest.json`，依赖检查before/after TRX位于其`dependency-contract/`。该历史codec40／依赖2计数不再加到最终315；最终当前程序集证据以上节统一清单为准。

### 18.5 本批之外

MINOR提升为0.64.0，格式28／16／6、配置1、profile2、作者SDK和固定依赖保持。保留原正式Runtime／Skia／Export业务；`RuntimeDiagnosticSnapshot`新增工厂只用于传输还原，不宣称渲染引擎重写、端到端性能改善或任意机器字体等价。**本批有限出口完成，完整B3仍未完成。** 完整包／脚本／预设／分析与图片／视频闭包、字体能力门禁、持久脚本Worker及真实失控脚本恢复、完整诊断生产／geometry、B4／B5、完整Web产品和全量／性能／原生输入／人工平台及原暂停范围继续未完成。完整工程接续落点见[完成计划](../plans/backend-b3-completion-plan.md)，语义见ADR-0208；相对原基线的历史及当前统计见[变更报告§6](../research/backend-baseline-change-report.md#6-0640静态工程worker与http增量)。

<a id="b4-static-validation-0650"></a>

## 19. B4-A：固定静态工程正式验证任务（0.65.0，2026-10-13，有限出口已完成）

### 19.1 实际交付

用户要求维护统一API并开始B4，完整B3余项不作为整体前置。本批限定既有 `static-lyrics-scene-v1`：工程Writer内捕获精确修订、snapshot及授权资源引用，32字节私有任务头绑定源SHA／帧计划，真实Worker调用原RuntimeFrameValidator，普通／透明各独立会话、Final质量。本地逐帧验证仅回实际进度和完整报告，不传pixels，不以纯色探针或HTTP202替代成功。包脚本／预设／分析／图片视频等unsupported非空字段明确拒绝。

正式任务沿唯一 `_jobs`／BackendJob／BackendJobLedger、原client回执和SSE；调度只拥有执行许可，不新增第二个任务账本。running全局2／client1／project1、queued全局16／client4，已接受任务不受随后编辑、HTTP断连或client休眠影响。Worker／Runtime排水后释放音频引用、最后取消检查，再以原committing闸门发布原子报告sidecar和终态；真实失败报告可读取，执行／预算／发布失败不伪造报告。close reject／wait／cancelAndWait沿原生命周期，shutdown主动取消可取消正式任务并等待拥有的进程。

每输出1～600帧、双输出≤1200，帧起点按Composition整数帧率向下取整；要求显式正OutputDurationUs且末帧不超时长。本地1920×1080／8MiB不扩大preview的4MiB裸帧合同，任务净snapshot预算4,194,272已扣32字节头。报告≤256KiB／256issues／512occurrences、sidecar总16MiB；单ledger记录32KiB只存引用／hash／固定输入摘要。授权保留默认1小时，启动和接受新任务时物理prune，读取复核身份／大小／SHA／profile／计划／摘要。deadline默认60秒，可信host可配1～60，实际cap公布；探针8秒仅夹具，取消宽限2秒。

五个新增HTTP路由为validation-capabilities、工程validation-jobs、live validation-result／validation-diagnostics及显式recovery validation-result，具体DTO／例子／错误只维护于[统一API](../../backend-api.md)，不再维护第二份当前列表。live job／报告／诊断严格owner；previousHost且唯一ledger登记reportref的恢复读取沿B2本机profile-wide权限，当前持bootstrap登记的profile client以accessToken读取。旧client／resume不跨host恢复、不live fallback、不自动重跑或unknown升成功。新建可以主动传显式时长，省略时合法null但正式任务400拒绝；没有事后settings HTTP。

Runtime验证器保留primary失败／OCE，中立失败stable code与实际发生身份，cleanup仅附加，不覆盖主因；累积预算在加入集合前检查。现有Export不再以静态背景fallback吞中立失败，原其他普通绘制fallback保持。产品版本为MINOR0.65.0，Core／Worker等生成AssemblyInfo实际0.65.0.0／Informational0.65.0，codec EngineIdentity与同批程序集一致；格式28／16／6、配置1、profile2、SDK及固定依赖保持。

提交步骤：`c55bfad`统一当前API／B4计划；`4cf266b`Export窄兼容失败修复；`bae9f4e`BCL公开合同；`3e6ac13`Runtime主失败／预算；`671b5d8`Worker／0.65／solution；`6944c00`Hosting／Server正式任务；`d53c861`真实HTTP工具。产品代码终点为 `6944c00f57694bfce405b39cf15cd2414b7f6131`，工具终点为 `d53c861`；最后验收文档提交另列，不改变该产品范围。

### 19.2 最终统一有限验证

两条生产链和变异恢复均终态冻结后，使用 `flock -o /tmp/mkg-backend-b4-dotnet.lock` 串行正常restore、`-m:1 /p:UseSharedCompilation=false`，分析器／警告即错误保持。原solution session30499终态exit0、0警告／错误。明确六个原测试session全部终态，合计332执行／332通过／0失败／跳过／中止：

| 范围 | 原session／通过 | 最终TRX（统一目录内） |
|---|---|---|
| Hosting全部当前测试，含旧持久jobs与预览 | 26983；268/268 | `backend-hosting-final.trx` |
| Runtime验证器14＋真实正式Worker3＋旧静态Worker9＋依赖2 | 78559；28/28 | `runtime-validation-final.trx` |
| 新Worker test-only adapter项目 | 31151；8/8 | `backend-worker-final.trx` |
| Server全部，linux-x64 self-contained | 5614；16/16 | `preview-server-final.trx` |
| Export中立失败6＋原Committing4 | 62344；10/10 | `export-failure-commit-final.trx` |
| App作者Runtime原取消／等待释放两项 | 18773；2/2 | `app-runtime-cancel-final.trx` |

统一manifest为 `artifacts/backend-extraction/b4-a-final-verification/manifest.json`，SHA-256 `06ad37c1eefaf775a18989921604ec0f833a0bc714dd891a70ae1af65bebcceb`。它显式列六个finalFiles、原session、正常命令／TRX／日志、按类计数、193个测试源、15个当前DLL、6份生成版本记录及源／HTTP清单，共232个引用哈希逐项一致。没有替代／重复运行、排除依赖断言或关闭分析器。

局部Worker8／Hosting40／Runtime14／native26及Hosting264／Server16保留其原范围，不再相加；最终Hosting268已包含Worker最后cases。原7559变异过程正常exit0，两目标均成功编译后由真实断言拒绝，Runtime源SHA精确恢复为 `a389438e7913bc1ce3c7fea84ec23f64588763efa9e0f3bfb173bbc958659c5f` 并正常14复验。Worker过程证据 `artifacts/backend-b4-worker/worker-validation-evidence-manifest.json` SHA `b2a322085bc0f45c8998e8ea457d2596e6bf6170445f997755f59dba9eb3df3a`；Host17源及2旧文件删除清单为 `artifacts/backend-b4-validation/host-source-manifest.json` SHA `abee124bd6ab8c32f203ae31cec3b27c35d971dbb39253e4a478c6e545779d08`。中间WIP编译失败和变异预期失败不计正向通过。

Export6是controlled encoder边界，原Committing4实际跑既有小型FFmpeg事务回归；两者不等于新服务导出、全Export或长片验收。Server16使用可控执行器验证真实监听接线／ACL／报告合同，真正Worker任务另见下节；App仅原作者VM取消两项，无GUI／Headless／输入自动化。

### 19.3 真正HTTP与Worker完整任务链

最终探针原71951真实exit0，同一冻结产品发布体的final4通过389个HTTP请求、1344工作流断言＋808凭证产物扫描共2152项。FullHD1920×1080普通3＋透明3帧的完整报告与独立原RuntimeFrameValidator一致；实际进度／SSE、整数时刻及同一拥有的固定闭包的codec SHA／profile精确匹配job.input。报告只对指定计划成立；静态Runtime实际diagnostics为空，不代表脚本实际发生。

真实链覆盖精确修订／回执、排队及运行取消、8秒可信host缩短deadline与显式cancel不同分类、Worker SIGTERM及首次open执行体丢失、sidecar完整性500／逐字节恢复／只读发布失败、client休眠／恢复、project close、带活动任务shutdown及重启profile报告读取。current live新client无权读取404；previousHost登记报告从显式recovery路径200，按既有profile权限，不恢复旧owner凭证。

证据位于 `artifacts/backend-b4-validation-http/http-final4/manifest.json`；总清单 `final4-source-deployment-evidence-sha-manifest.json` 的SHA-256为 `0269781cb45b3bdc7f706d83d13745b7a2bb08623e43c9188699753d47255f66`。2源清单、39源码、367Server／289Oracle／293Worker发布文件、809证据、3构建日志共1802哈希全部核对一致。三工具源清单 `tool-final4-source-manifest.json` SHA为 `953fb526089449c4f816075838ce6a7ffad60b73da54f8862bc9c1d0a143d2bd`。两个Server PID377039／377678退出0，八观察到的Worker均退出、无forced kill，凭证未落入证据。

首三次工具夹具13／29／43请求未完成，保留并排除final4计数：分别为默认create未填显式正时长被400正确拒绝、lyrics/lines查询漏branchId=primary、篡改报告误期望400而真实500；均按冻结生产事实修正工具，没有修改生产以迎合探针。成功链用自有保存的20秒工程→授权open-job→同固定闭包oracle；create本身也可主动传正OutputDurationUs，不能据该夹具暗示HTTP无法创建可验证工程。

### 19.4 接续与边界

**B4-A固定静态正式验证有限出口完成，完整B3／B4未完成。** 只验证指定帧计划；本批HTTP比对为输入身份／报告／进度，不是PNG像素、FFmpeg影片、完整音轨或真实脚本验收。完整包／预设／分析／图片视频与可搬移字体、失控脚本隔离、完整诊断／几何、导出／分析HTTP、作者／Web产品继续按[B4计划](../plans/backend-b4-plan.md)和[B3接续](../plans/backend-b3-completion-plan.md)分别推进，B3余项不作为B4整体前置。全量／性能／GUI／长片／人工平台及原暂停范围不自动恢复。


<a id="b4-static-export-0660"></a>
## 20. B4-B：固定静态工程独立导出与不可变下载产物（0.66.0，2026-10-13）

**B4-B固定静态工程独立导出与不可变下载产物的有限出口已完成，完整B3／B4未完成。** 只按[B4计划§6](../plans/backend-b4-plan.md#b4-static-export-plan)接续独立导出，不等待完整B3；实际HTTP合同唯一维护于[backend-api](../../backend-api.md)。

### 20.1 实际交付范围与源合同

固定精确工程修订、原snapshot-v1 codec SHA与独立audio grant hash／size。仅static-lyrics-scene-v1，原Export领域门禁保持；包／脚本／预设／分析／图片视频背景明确拒绝。单job普通H.264 MP4或透明ProRes4444 MOV，Standard编码／Final渲染，download-only不可变产物；HTTP不能指定路径／工具／FFmpeg参数。includeAudio默认true，直接pin授权ResourceStore中的自包含PCM16 WAV mono／stereo、不复制音频；源PCM采样帧派生时长严格覆盖派生视频，Worker强制WAV及实际hash／size复核。false纯视频不给音频grant。名义及派生时长≤10秒、≤60fps／600帧、FullHD／raw8MiB，本地渲染／编码，不扩preview IPC。

原RoundAway／至少一帧及整数时刻提为Core CompositionFramePlan。IsVerified保原nominal验证值，新StrictlyVerified要求实际derived-plan／完整decoded帧数／尺寸／有理fps／codec／Alpha及size／hash匹配；failed／dropped为0。源audioDurationUs与产物actualAudioDurationUs分别记录；产物音轨50ms量化容差、视频200ms容差不代表放宽精确帧数或源PCM时长，不声称AAC逐样本无损。受限WAV的-shortest在真实透明NTSC试样裁59／60末帧，原红保留；只该路径跳过-shortest并保**原已有**-t，默认桌面行为保持。

validation／export共用唯一scheduler／jobs／ledger，running2全局／1client／1project、queued16／4，持久化控制job不占slot；每接受export预约64MiB媒体＋256KiB报告，总512MiB含queued／running／retained／active-read。Linux-x64单文件RLIMIT_FSIZE硬限制，不支持则disabled，不宣称全磁盘或跨平台。deadline默认120／可信host1～120，取消宽限2秒，实际caps公布。Worker原Exporter只私有candidatePublish，preparing-result仍可public取消；实际Worker／Runtime／FFmpeg排水→严格报告／媒体校验及fsync→lastcancel→唯一Job.EnterCommitting→同FS一次Directory.Move媒体＋report包→ledger，迟到cancel不改成功，ledger写入失败unknown不重放。

完整报告≤256KiB／512occurrences，超限不截断。真实非空snapshot才生成kind=exportFailure的report-only包／FailureReportRef，无Artifact／content；transport-only只保stable code，不造报告，cancel先赢公开闸门不发表。failure-only链由Hosting和受控Server实际诊断回归覆盖，不能宣称当前unsupported脚本已有真实发生验收。live job／结果／内容／诊断严格owner，previousHost唯一登记引用显式recovery沿既有profile权限，不恢复旧client／resume。默认授权1h，启动／admission prune，流式read lease保持实际容量，占位无法删除不提前释放。

源码步骤：`accab07`已决计划；`e5cc51c`Processes／0.66／solution；`8350372`Core帧计划；`6935eeb`Exporter启动primary；`8212e6f`公开DTO；`54d07e0`Media有界及受限WAV末帧；`5ef1988`Worker／private group；`012a32c`Host／Server；`2a29329`实际全Hosting暴露的legacy subreaper和控制写入窄修。**产品最终代码端点`2a29329`**；工具终点`6d41c5b`（五源码），最终验收文档另列，不把早期012a32c元数据误作缺窄修的实际部署。MINOR0.66新增公开能力与BCL-only Processes；格式28／16／6、配置1、profile2、作者SDK及固定依赖保持。

### 20.2 原进程终态与统一有限验证

正常共享锁`flock -o /tmp/mkg-backend-b4-dotnet.lock`、restore／分析器保持，`-m:1 /p:UseSharedCompilation=false`。初solution77756通过0警告／错误；原Hosting66359实际320通过／3失败／323执行，旧普通Worker超时／取消子树回收断言失败，证据原样保留。定位为进程级subreaper使原legacy非private-group后代被收养却未回收；同时窄修控制写入阻塞宽限。修后focused64855的原3＋新增2＋真实orphan1通过6；不放宽Alive断言，不调用waitpid(-1)抢.NET Process所有权。唯一修后原fullHosting1735通过325／325，作为最终Hosting来源，不再重复全范围。

修后原solution36362实际exit0、0警告／错误。最终7组8个显式TRX均终态，合计436执行／436通过／0失败／跳过／中止：

| 范围 | 原session／通过 | 最终TRX（统一目录内） |
|---|---|---|
| Hosting全项目，旧323＋新legacy／满pipe控制2 | 1735；325/325 | `hosting-control-lifetime-final.trx` |
| Worker test-only adapter全项目 | 51720；8/8 | `backend-worker-final.trx` |
| Server全项目，linux-x64 self-contained | 2122；19/19 | `backend-server-final.trx` |
| Export原提交边界52＋中立6＋启动3／有界1／策略1／真实NTSC1 | 41649；64/64 | `export-transaction-final.trx` |
| Media有界Probe／强格式／实际帧率3，最终持续writer夹具 | 77256；3/3 | `media-bounded-final.trx` |
| Processes实际group／FSIZE／bounded text全项目 | 35906；5/5 | `processes-final.trx` |
| Runtime真实Worker export4＋生产依赖2 | 26935；6/6 | `runtime-export-native-final.trx` |
| Core直接帧计划helper定向（同第7组），非全Core | 37270；6/6 | `core-frame-plan-final.trx` |

统一manifest：`artifacts/backend-extraction/b4-b-final-verification/manifest.json`，SHA-256 `5b3f0a609377232062087ee80e4e853246c8fc52465cd561d505911efdd129ff`。显式finalFiles选择修后Hosting新名，原backend-hosting-final.trx失败文件不覆盖，保其他历史manifest引用SHA；原77756构建属窄修前prefinal。修后1,162个生产／测试源文件SHA无漂移，productTreeMatchesEndpoint:true；清单有1,239个去重路径hash引用，不能把递归层级／重复引用计成独立文件数。生成版本及产品DLL均0.66，计数不混局部或过程测试。

三作者原freeze与追加修清单分别位于`artifacts/backend-b4b-export/host-source-manifest.json`、`artifacts/backend-b4b-processes/owned-source-evidence-final-freeze-manifest.json`、`artifacts/backend-b4-export-worker/worker-export-evidence-manifest.json`、`control-lifetime-followup-evidence-manifest.json`及`worker-control-write-fix-manifest.json`。旧22源码阶段freeze曾撤回，真实NTSC原5265失败59／60、全Hosting66359失败，以及新tree捕获瞬态group的15725失败、compile-only21290均保留排除；修后精确PID／startTicks路径及原断言通过。Server19使用受控执行器测真实HTTP接线／failure-only来源，不代替下面真正Worker／影片证据。没有全量／性能／App／GUI／长片或分析HTTP测试。

### 20.3 真正HTTP影片与完整音轨终态

独立探针原74546实际exit0，第三轮764个HTTP请求、4057工作流断言＋1625凭证产物扫描＝5682；普通／透明／30000/1001透明三部影片各实际完整解码90帧。保存／关闭／授权打开同固定工程后，独立原Runtime Final会话按同计划产oracle；原codec sourceSHA／profile与job.input一致。画面含正文、注音及场景，移除注音／场景对照分别改变3371／1082像素；RGB平均绝对误差约普通2.100899、透明0.022178、有理透明0.022145，不声称有损成片逐像素相等。全音轨解码单声道样本132300／132300／132432且实际非静音；透明alpha与半透明内容逐项比较。独立离线附证另29断言实际exit0，不增加HTTP请求或改5682计数：两PCM MOV每通道132300／132432样本与源逐值相同、RMSE0；普通AAC每通道132300样本，左右相关系数约0.999551／0.999796、RMSE约212.03／114.27，明确有损而非逐样本相等。

真实链覆盖固定来源／接受后编辑、相同回执／异载荷拒绝、普通／透明／有理fps及显式纯视频、来源／领域门禁、queue／running／reservation、运行取消／deadline／真实Worker失效、产物或报告完整性失败、发布失败／旧成功artifact保护、关闭／休眠／恢复／shutdown和previousHost显式profile读取；最终成功静态diagnostics为空，真实transport故障不造snapshot。两Server实际退出0，19个Worker及13个同组子进程共32个观察对象均gone，独立三个解码进程退出0。权威总清单为`artifacts/backend-b4b-export-http/final-source-deployment-evidence-sha-manifest-v2.json`，SHA-256 `812e1e5d3627289f7ddfb3325e08ba2dca2c59eb7cd194d610099351ee234b3b`，source／部署／证据共2616处hash引用核验一致，含68个产品源匹配最终`2a29329`。原HTTP manifest位于`http-final3/manifest.json`，保留当时运行HEAD `012a32c`；发布体含已冻结生命周期窄修，wrapper据源hash明确对应最终2a29329，不冒称运行时Git HEAD已是后续提交。工具五源码已以`6d41c5b`提交推送；前两轮工具22／180请求过程失败保留排除，不混任何计数，也未重跑成功HTTP。

实际HTTP没有等待1h TTL到期，也不缩短生产TTL；它验证expires元数据／lease／close／restart，真正到期与active-read容量由Hosting受控TimeProvider回归。FSIZE真实硬限执行在自有C#子进程回归，HTTP核对能力和预约／输入预算，不把所有风险均宣传为HTTP亲历。最终影片与音轨证据不以B4-A的报告比对替代。

### 20.4 接续与尚未覆盖

**本批静态导出有限出口完成，完整B3／B4未完成。** 首批仅静态工程＋PCM16 WAV／纯视频；其他音频、包／预设／脚本／图片视频／分析闭包、可搬移字体、作者／完整Web和分析HTTP继续独立接续。没有扩大原Export领域数据语义，已知未定时空白冲突仍不改；夹具全部合法timing。

legacy OwnedProcessTree只对leader存活时验证／登记PPID＋pid／startTicks的后代提供回收保证；未捕获且leader先亡不能推定全树释放，export RequireExportGuards拒绝该legacy。StartAsync握手catch叠加二次Stop异常仍可能遮主因的卫生项未fresh复现／验收，不虚称所有Worker启动双故障已修。本批已测试的Invoke／Export／Validation运行期控制写入与cleanup primary保持明确有限范围。全量／性能／GUI／长片／人工平台及原暂停边界不自动恢复，既有screenshots保持。


<a id="b4-audio-analysis-0670"></a>
## 21. B4-C：独立离线音频分析结果任务（0.67.0，2026-10-13）

**B4-C独立离线音频分析结果任务有限出口已完成，完整B3／B4仍未完成。** 按[B4计划§7](../plans/backend-b4-plan.md#b4-audio-analysis-plan)接续，不等待完整B3，也不把任务接受／完成解释成分析结果应用工程。实际HTTP合同只维护于[统一API§12](../../backend-api.md#audio-analysis)。

### 21.1 实际实现与固定输入

工程Writer按exactRevision固定当前授权audio asset／hash／size，直接pin ResourceStore，不接受HTTP路径或默认复制音频；不捕获ProjectSnapshot，不走staticcodec。来源profile为`pcm16-wave-analysis-v1`，私有执行profile为`analysis-pcmwav-v1`；含effects／已有AudioAnalysis的工程可只分析其授权音频，不剥字段、不扩大`static-lyrics-scene-v1`。接受、完成、读取均不调用SetAudioAnalysisCommand，不改Project.AudioAnalysis、修订、dirty或undo；本批没有apply。

复用原AudioFeatureAnalyzer／AudioFeatureBuilder及`bass-pulse-rbj-v1`，强WAV、自包含PCM16 mono／stereo、≤192kHz／16MiB／300秒；实际输出48k mono／960sample hop，最多14.4M样本／15k points。预算检查在原Builder.Append之前，原整曲归一化、float32 PCM hash、double特征、ceil时长及真实短尾保持，不裁切或用空结果伪成功。NoStream／Unavailable／分析失败为failed，OCE保取消；beatPhase仍为原近似估计。

完整结果沿原Core AudioAnalysisJson规范字节≤4MiB，保留文件内整数number和全部double；job input／sample进度／结果摘要的长整数为string。sourceHash／pcmHash、语义Revision、完整ContentHash和私有wire inputSHA分别校验，ContentHash=完整文件SHA=report.sha256，不混工程revision或snapshot SHA。四个新模板承载能力、接受、live／previousHost结果GET／HEAD；结果支持Range／ETag／读租约，无points分页或analysis-diagnostics。

复用唯一jobs／scheduler／ledger／client回执，正式validation／export／analysis共用running2全局／1client／1project、queued16／4；每analysis先预约4MiB，总store64MiB含queue／running／retained／expired active-read。默认deadline60秒、可信host1～60、宽限2秒；独立私有group／subreaper与有界工具启动探测，不要求export编码器／FSIZE。ToolStartupVerified只证明有界启动，不代表任意音频解码验收。Worker／native排水→有界规范结果准备／flush／hash→lastcancel→唯一EnterCommitting→原子发布→ledger；unknown不重放、不自动升成功。

结果默认保留1h，启动／admission物理prune，active-read持续占实际字节直到释放；live严格owner，显式previousHost仅ledger已登记结果沿B2当前bootstrap登记profile客户端授权，不恢复旧client／resume、不猜孤立文件。抽取共用BCL sidecar IO不改变validation目录、ID、原bytes、预算或ACL。Sidecar Prepare写入／flush与删除双故障保primary，未删tmp继续占预算；启动孤立tmp按实际字节计费。本项为原源码定位加当前真实chmod／文件故障回归，**没有旧binary跑红复现证据**。

源码步骤：计划`be6ed6c`；公开合同／PCM／codec／0.67 `a028016`；Media强WAV与bounded stderr `b575646`；私有分析Worker `a821ce8`；Host／Server与sidecar窄修`be56180`。**产品代码端点`be561802bf1cb996e986197b7ffc6b284e15804a`**；工具端点`fdce074`，最终验收文档另列。MINOR0.67新增公开有限能力，格式28／16／6、配置1、profile2、作者SDK及固定依赖保持。

### 21.2 唯一原进程与统一有限验证

正常`flock -o /tmp/mkg-backend-b4-dotnet.lock`、restore／分析器、`-m:1 /p:UseSharedCompilation=false`。原solution83918实际exit0、0警告／错误；五个显式TRX均实际终态，共460执行／460通过／0失败／跳过／中止：

| 范围 | 原session／通过 | 最终TRX（统一目录内） |
|---|---|---|
| Hosting全项目，含既有frame／export兼容与新sidecar文件故障3 | 52636；369/369 | `backend-hosting-final.trx` |
| Worker test-only adapter全项目，原8＋分析预算4 | 49568；12/12 | `backend-worker-final.trx` |
| Server全项目，linux-x64 self-contained | 11639；21/21 | `backend-server-final.trx` |
| Media原分析器／来源桥接／bounded PCM及两项原解码回归 | 5293；30/30 | `media-analysis-final.trx` |
| Core仅MediaAnalysisStorageTests，非全Core | 96328；28/28 | `core-analysis-storage-final.trx` |

统一manifest为`artifacts/backend-extraction/b4-c-final-verification/manifest.json`，SHA-256 `b75c18784be703149602b98a57ad8e8bc7095d533642f4d8251c0cc50e764e15`。它显式选择五个finalFiles，记录原command／session／TRX／日志、1444个生产／测试／资源输入无SHA漂移、当前DLL及测试执行体／RID Server／生成0.67版本，productTreeMatchesEndpoint:true。作者阶段4／26／6／30与Host17等计数不相加，阶段mutable bin hash不冒充当前最终binary。

Hosting全范围保留既有RenderFrame／WorkerExportLifetime等兼容测试，不过滤它们，也不宣称这次运行完全未渲染／编码。本批未新增独立渲染／影片验收；独立实际oracle只验收音频。Server含受控执行器的接线测试，不能替代真正HTTP→Worker→FFmpeg证据。不另跑Processes旧FSIZE、Runtime／App／Application、GUI、全量、性能或长片；既有screenshots未动。

### 21.3 真正HTTP音频与原算法oracle

独立原9085实际exit0，最终http-final2通过789个HTTP请求、4243工作流断言＋1700凭证产物扫描＝5943。六个默认真实FFmpeg输入覆盖48k静音／混合频带／脉冲短尾、32k重采样、44.1k stereo和96k来源；总651,120实际48k mono样本／680个全部points／71,287完整canonical bytes，与独立原AudioFeatureAnalyzer.CreateDefault→ToSourceData→AudioAnalysisJson逐字节相等。每个job的固定sourceProfile／接受修订／audio内容SHA、sourceHash／pcmHash／Revision／ContentHash、真实SSE采样进度与全部数值分别核对；静音为真实零特征与null phase，真实短尾和半开边界保持。完成不改工程修订／dirty／历史，随后显式编辑不改变已捕获音频身份。

第一个Server使用原默认真实/usr/bin/ffmpeg、不限速；第二个Server仅为进程控制夹具，经自有可信0700 launcher在实际解码-i时加-re，-version直接调用真实工具。该控制链单独覆盖实际排队／运行取消、8秒可信host deadline与默认60不同、Worker／native失效及进程归属排水，不当默认性能／解码oracle。另覆盖精确修订／单层回执、live跨client拒绝、HEAD／Range／ETag、sidecar篡改500与逐字节恢复、发布失败、64MiB预约容量、关闭／休眠／恢复／shutdown及previousHost显式profile恢复；旧client身份不恢复，unknown不重放。

两个Server PID972563／973383均exit0、无forced kill，20个Worker leader＋23同组member共43个观察身份全gone。权威原HTTPmanifest为`artifacts/backend-b4c-analysis-http/http-final2/manifest.json`，SHA-256 `da3598fbf943c18dbd0cc69438612c99b6d3aaf4837c5ce5fbc6fb209f400921`；总源码／部署／证据清单为同目录上层`final-source-deployment-evidence-sha-manifest.json`，SHA `2f777d387b2318589a584d7267f4ec02ca587de7d7a63a1203a379252fdc919a`，root已核验全部递归引用与选定生产源匹配be56180；四工具源码已提交`fdce074`，与产品端点分列，原HTTP运行时HEAD仍是be56180。

首轮原44375退出1，20请求／125工作流＋53扫描保留排除：探针试图把行终点改成1,011,000µs，而导入行及子元素仍覆盖真实音频1,013,000µs，原领域422正确拒绝；工具改为在既有行内调整token终点，没有修改生产来迎合探针。新增归档JSON结构扫描两项另列，不增加789／5943。实际接受最长来源6.513秒；300秒门禁用约60KiB的100Hz／30,100采样帧来源在入队前拒绝，不宣称完成300秒／14.4M真实解码。产品TTL仍1h，HTTP只验expires元数据／读取／生命周期，实际到期与expired active-read记账由受控Host时钟回归；不把短deadline夹具称为缩短TTL。

### 21.4 接续与边界

**本批独立音频分析有限出口已完成；完整B3／B4继续未完成。** 后续分析结果apply、其他音频容器、完整包脚本／图片视频／分析渲染输入、字体能力、作者／Web闭环分别推进。既有legacy未登记且leader先亡的后代、未fresh复现的Start握手叠Stop双故障卫生边界仍保留，不把新analysis真实group能力扩大为所有旧Worker路径的全树保证。全量／性能／GUI／长片／人工平台及原暂停范围不自动恢复。


<a id="b4-analysis-apply-0680"></a>
## 22. B4-D：显式采用／清除独立分析结果（0.68.0，2026-10-14，有限出口已完成）

**B4-D显式采用／清除独立分析结果的有限出口已完成，完整B3／B4仍未完成。** 依[B4计划§8](../plans/backend-b4-plan.md#b4-analysis-apply-plan)及ADR-0212接续；当前接口唯一维护于[统一API§12.5](../../backend-api.md#audio-analysis-apply)。完整B3／B4与分析渲染profile不由本次采用推导为完成。

### 22.1 实际合同与必要新建接缝修复

沿唯一commands开放`audio.analysis.apply(analysisJobId)`与`audio.analysis.clear`，当前精确expectedRevision、client单层回执与同一Writer保持。只采用live owner、同session、succeeded且未过期登记分析结果；sourceRevision只作来源证据，不阻止后续歌词编辑后按当前修订显式采用。previousHost结果仍只读，不能apply；不新增job、调度器或结果副本。

完整canonical／Core网格／revision／摘要、当前audioAssetId／hash／size／AudioMedia／授权路径及≤16MiB实际文件hash均核实；所有IO及fallible lease释放先于mutation，最后复查live／TTL及工程来源。声明异源422；已登记sidecar／音频实际文件缺失／link／hash／size／IO／cleanup损坏500；未知／跨client／过期／旧host404。真实文件释放故障回归验证当前失败零写入，不称旧binary曾跑红。

共享Application基线绑定Workspace／Generation／Project／音频／原分析及EditContinuityToken，一次实际变化仅原`audio.analysis`领域历史。活动History group实际变化拒绝；相同完整结果重复采用／空clear为no-op，保留redo、dirty、修订与事件。成功读模型一致后一次通知，变化摘要沿project完整刷新；summary只给available／failed／noStream及有界成功身份／采样计数，不发points、路径或原诊断。保存重开沿原Core完整canonical数据。

原HTTP CreateCore fresh candidate只SetAudioPath、AudioMedia为空，会令强同源门禁全部拒绝；本批在revision0发布前从已授权StoredAsset固定完整hash／size／安全显示名。没有新增文件IO／hash、apply补metadata或第二历史，原新建dirty／hasPath、租约及共享Preparation／App语义保持。static-lyrics-scene-v1仍拒绝非空分析；clear／undo回空恢复原静态能力，未开放analysis-aware渲染。

源码步骤：计划与0.67固定统计`b1c8e86`；公开DTO／0.68版本`1ac0cd3`；共享Application`f7647ca`；Host／Server及新建接缝`6264a94`；独立HTTP工具`07d4567`。产品代码端点`6264a94`，当前工具端点`f4787bd`；MINOR0.68保持格式28／16／6、配置1、profile2、作者SDK与固定依赖。

### 22.2 原进程统一有限验证（已终态）

正常`flock -o /tmp/mkg-backend-b4-dotnet.lock`，restore／分析器保持，`-m:1 /p:UseSharedCompilation=false`。原solution34313退出0、零警告／错误，耗时57.13秒。四个显式TRX共977执行／977通过，零失败／跳过／中止：

| 范围 | 原session／通过 | 最终TRX（统一目录内） |
|---|---|---|
| Application全项目，含新共享用例25项 | 1066；530/530 | `application-analysis-final.trx` |
| Hosting全项目，含新采用21项与旧frame／export兼容 | 79994；390/390 | `backend-hosting-final.trx` |
| Server全项目，linux-x64 self-contained，含新采用8项 | 21182；29/29 | `backend-server-final.trx` |
| Core仅MediaAnalysisStorageTests | 15628；28/28 | `core-analysis-storage-final.trx` |

统一清单`artifacts/backend-extraction/b4-d-final-verification/manifest.json` SHA-256 `0bc2a8653f95dce2a5d5d3be1e415656aad8e4490a301465fec86ca4488f3a0f`；source-freeze SHA `9f2ad406729a8930d5a186e7fbb3a0783c428469e1bc8d9bf25f1f2aa24ebced`。统一验证当时1533个源／资源／构建输入无漂移，root已核1845处递归SHA及四TRX counters，原invocation／tool session／PID／起止／exit／log、class计数、DLL及生成0.68版本在内。原运行时HEAD为b1c8e86与已freeze待提交源码，不冒称当时HEAD已是随后产品提交。

作者阶段Application25／Host21／ServerSC8不并入977；Application三项成功编译后变异被真实断言拒绝并精确恢复。Host首轮CA1822为测试compile-only；record内数组Equals测试断言19/20失败后改为完整JSON值比较，原scope复跑21/21；Server首次filter只匹配旧analysis transport2项，明确排除新增8计数。作者源清单及排除见`artifacts/backend-extraction/b4-d-author-verification/host-server-source-freeze.json`，SHA `8af6010c4ae84abaa438ab189a40f985c4e78278ab25048210218334c60c90ee`。不把这些过程失败写成产品缺陷或旧binary跑红。

Runtime／Worker／Media和static codec未改，不机械重复旧全项目；Hosting全项目保留实际frame／export兼容，不称本次完全未触及渲染编码。没有恢复全量、性能、GUI、长片、screenshots或人工平台矩阵。

统一验证后工具`probe.py`因schema28清除状态（`1d34fa4`）、PNG格式转换（`55900f2`）及open结果快照clone（`f4787bd`）三处工具修正而变化，只有同一项源输入漂移；原977 manifest保持不重写，不能把其1533初始freeze称为当前全部无漂移。产品C#／测试源仍匹配，工具补充冻结见`artifacts/backend-extraction/b4-d-final-verification/tool-followup-source-freeze-f4787bd.json`，最终部署证据须引用新工具SHA。

### 22.3 真正HTTP与静态能力恢复终态

首轮原94740实际exit1，288请求／1364工作流＋627凭证扫描＝1991断言保留排除：工具将schema28清除分析状态误期望JSON null，而原`ProjectResourceJson`实际使用`{state:notAnalyzed}`；仅修工具单处并提交`1d34fa4`，产品6264a94及四隔离publish不变。第二轮原96602实际exit1，303请求／1395工作流＋660凭证扫描＝2055断言保留排除：真实PNG已成功，但工具将straight RGBA直接与原Runtime premultiplied BGRA比较；按原helper补alpha乘法及通道转换，不松容差，已保存57,600像素正确转换后RGB／alpha误差0。两次均仅改工具，产品及部署不变；后续完整成功轮次另列。

原final3的61870实际exit0／356请求／2431断言，但root归档复核发现工具open返回terminal原dict，随后client源摘要更新经可变alias改写manifest.results内历史nested summary，原HTTP raw response正确，非产品问题。仅新工具clone断开别名并提交f4787bd，原final3与其integrity f61b3cb4e6f5a268b0a5c3708c40cbaecdd3e276ecdf8f1c8c3773768b00d82c保留为运行过程通过，不作最终工具归档证明。

独立最终原21974的http-final4实际exit0，367请求、1715工作流＋807凭证扫描＝2522断言；其16个terminal记录（含4个open）全部与原末次poll完整JSON值逐项一致，clone可靠。两个默认真实音频（48k mono、44.1k stereo）经原Worker分析的21,749完整canonical bytes／202全部points／193,248实际48k mono样本，与独立原Analyzer逐字节一致。原sourceRevision0在歌词编辑至currentRevision2后按精确当前修订显式采用；完整summary身份、单层回执、no-op保redo／事件、undo／redo／clear、保存关闭重开canonical、跨client／其他session／只读／旧host拒绝以及登记结果／音频文件篡改500恢复均通过，previousHost仍只读且不可apply。

采用后非空analysis输入的preview／validation／export明确拒绝；clear后真实PNG的57,600像素经正确straight RGBA→premultiplied BGRA转换，与独立原Runtime的RGB／alpha误差均0，正式验证普通1＋透明1帧报告成功，320×180／1秒纯视频MP4实际完整解码30帧且无音轨。该影片首帧对原Runtime RGB平均绝对误差约0.3790567、误差>40比例0；不称有损影片逐像素相等或每帧独立oracle。202接受状态不替代这些实际终态。

两个Server PID49681／50393实际exit0、无forced kill；本轮16个观察到的private-group身份全部gone，不把member数写成Worker数。原HTTP manifest为`artifacts/backend-extraction/b4-analysis-apply/http/http-final4/manifest.json`，SHA `f2a373bd6ed1ead1c3b4fb3c598ac1d35629bf34ddbc5633e1c147141dfa940f`；最终总清单`artifacts/backend-extraction/b4-analysis-apply/http/http-final4-integrity.json` SHA `e71b9ea882f98a273f1a5d3996da32b0a89aa5a3a8a2e93a5e64350d69580ea3`，root已核总清单3806处递归SHA引用、另877个部署文件及其路径集合逐项一致；3774项证据索引另列，三者口径不混同；独立索引`http-final4-files-sha256.json` SHA `f36ab966197be03eb4b04ccdb6ec7df0b372c92b5c0230c25e5f5f6723b66481`。最终工具补充freeze-f4787bd SHA `9bc5e2514482f9c3d679634defb92c894649e9b9f391e9f05da49bceed4aa1bf`，probe.py SHA `275901f669b3ddcf999266116908f4ea9d23afc1620847a5f9d25affdcdf0449`匹配工具端点；1533原输入只该工具变化，产品C#保持6264a94。原两个失败轮次及final3过程通过单列，不混最终367／2522或本轮16个group身份。

### 22.4 保留边界

本批显式采用／清除有限出口已完成，但完整B3／B4、其他音频、包脚本／预设／图片视频／analysis-aware输入与字体闭包、作者／Web仍分别推进。产品1h TTL不缩短，真实HTTP未实等到期；受控TimeProvider边界另列。全量／性能／GUI／长片／人工平台与原暂停范围保持。


<a id="b4-analysis-static-profile-0690"></a>
## 23. B4-E：完整已接受分析的静态工程profile（0.69.0，2026-10-14，有限出口已完成）

**B4-E完整已接受分析静态profile有限出口已完成，完整B3／B4仍未完成。** 依据[B4计划§9](../plans/backend-b4-plan.md#b4-analysis-static-profile-plan)、ADR-0213，复用唯一snapshot-v1／Source及原Runtime，不新增项目／算法／视觉语义；当前接口唯一维护于[API§9](../../backend-api.md#9-静态工程预览)。

### 23.1 双profile与完整源

中央BCL BackendSnapshotProfiles定义旧static-lyrics-scene-v1（仅null分析）及新static-lyrics-scene-analysis-v1（必须nonnull）。Host按唯一SelectProfile自动选择，默认Encode仍旧能力；Available完整canonical／contentHash／semantic Revision／所有points保真并核sourceHash，Failed／NoStream保完整code／message，三状态要求AudioPath＋AudioMedia。纯BoundedAudioAnalysisCodec抽取校验，Host独立结果为薄适配，原分析结果字节／算法保持。

总snapshot／IPC仍4MiB、200k值，歌词／场景／分析／wrapper共用；JsonElement深层全部计数。首发现Envelope额外4值只是入口，Media.ByteLength／FrameRate分量／Optional isSet／多态kind等converter也产生额外wire值。保前置图guard，Encoder最终有界wire复用Decode原RejectDuplicateFields同一规则，提前拒绝原本不能自decode的边界，不复制第二计数器／改旧wire。15,000hops＋19,000tokens各自合法、组合字节<4MiB但总值超限回归明确拒绝；旧39591捕获的0.68 snapshot fixture与同engine基线保持。

preview／validation扣24／32字节头在admission拒413；export实际metadata＋32在EncodeInput、spawn／send前guard，可beforeRegistration时413且清candidate，未知实际metadata不重造jobId，202后也可稳定WorkerSnapshotBudgetExceeded失败。组合超限不截断／剥分析。private export probe新增EngineIdentity／双SourceProfiles，实际probe匹配当前engine才enabled，缺字段／错engine／重复未知profile为WorkerExportProfileMismatch；public旧sourceProfile保留、尾增sourceProfiles，disabled export为[]，preview／validation仍configured-unverified。

纯视频保持完整Source／分析／AudioPath／AudioMedia声明，只IncludeAudio=false禁止源音频Probe／编码器音轨IO；true沿可信grant重绑定访问路径，保原Media身份／显示名，不强求声明路径等于访问路径。sourceSHA为原编码，grant另列；原Runtime静态无脚本画面不因分析而变，不宣传音频驱动画面。恢复只接受双固定profile allowlist，未知拒绝，不重跑unknown／扩大旧client权限；包脚本／预设／图片视频／字体闭包仍unsupported。

提交步骤：计划及0.68归属统计4944e78；中央profile／版本374c96a；codec cb03e34；Worker e4e28bb；Host／Server产品端点5727145；工具端点4378b60。0.69 MINOR保持格式28／16／6、配置1、profile2、作者SDK及固定依赖；无新项目，复用既有HTTP helpers／oracle入口。

### 23.2 原进程统一有限验证（已终态）

正常shared flock／restore／分析器，-m:1／UseSharedCompilation=false。原solution94483实际exit0，0警告／错误，56.58秒；六个显式TRX559执行／559通过，零失败／跳过／中止：

| 范围 | 原session／通过 | 最终TRX（统一目录内） |
|---|---|---|
| Backend.Rendering全项目 | 45413；61/61 | backend-rendering-final.trx |
| Backend.Hosting全项目，保旧frame／export兼容 | 90674；402/402 | backend-hosting-final.trx |
| Backend.Worker全项目 | 28756；20/20 | backend-worker-final.trx |
| Server全项目，linux-x64 self-contained | 88151；30/30 | backend-server-final.trx |
| Core仅MediaAnalysisStorageTests | 60376；28/28 | core-analysis-storage-final.trx |
| Runtime仅依赖／真实Worker静态／正式验证／导出四类 | 70862；18/18 | runtime-static-worker-final.trx |

统一`artifacts/backend-extraction/b4-e-final-verification/manifest.json` SHA9227195818f6e65a8364a1af162a4b14a449e647d4d8ed94c64a765db75bcbd9；source-freeze SHA3cc903e9a49b504cc7496247687ab6e7ba1a92347e7c78869c0eac88f5561700。1542源／资源／构建输入无漂移，root核1862处递归SHA；37生成程序集版本均0.69，原command／tool session／PID／日志／TRX class及binary hash在内，不将后续提交HEAD冒称运行时HEAD。

作者codec恢复21／Host41与SC相关9／Worker恢复8不加统一559。codec过程CA1305 compile-only和随机sceneID基线假设仅修测试保失败；Host80755为nullable测试compile-only、4321原36/41失败5因新plainLRC夹具缺formal token timing、在新profile／metadata分支前被原领域正确拒绝，仅改增强timed夹具后同scope41全绿。原55640／95160、codec28075与Worker90525恢复源SHA及两组编译成功变异反证由root核验，过程旧0.68产物不作0.69当前验证；最终codec变异直接调用ValidateSupported的组合断言：旧profile guard单点使3断言失败，JsonElement深图guard单点使1断言失败；wire终验仍保留且不干扰该前置校验回归。Worker两变异各4断言失败，不称单位置覆盖全部预算。作者Host18源清单SHA943c6b1b2d0bdce5cbba41eb36aaa78713111a860b370aa5112a8305abd2a85b；其他精确证据集中于作者manifest。

不跑Application／App／Media旧项目或整Runtime；原Runtime四类为BackendDependencyTests／WorkerStaticSessionIntegrationTests／WorkerValidationIntegrationTests／WorkerExportIntegrationTests，真实旧v1普通／透明／取消／音频兼容保持。不重复已绿scope，不扩全量／性能／GUI／长片／平台矩阵。

### 23.3 真正HTTP与固定部署终态

原23038实际exit0，232请求、1313工作流＋551凭证扫描＝1864断言。一个默认真实授权音频经原Analyzer所得10,862完整canonical bytes／101全部points／96,624实际48k样本byteexact；受信同声明归一化后的完整snapshot SHA与HTTP实际source SHA相等。oracle将自有保存包AudioPath还原HTTP当前声明后原codec Encode，声明重绑zeroIO，不将package原路径SHA直接等于HTTP。

新profile PNG、接受后clear仍固定的新profile PNG、旧null profile PNG各57,600像素，RGB／alpha误差均0。新／旧profile各正式普通1＋透明1帧报告及restart后认证读取保持；fixed input之后clear不改变捕获revision／profile／sourceSHA。320×180／1秒纯视频MP4、授权PCM16音频MP4、透明纯视频MOV三片各完整解码30帧，首帧与独立原Runtime oracle比较：两MP4 RGB MAE约0.38010995，MOV预乘RGB MAE约0.006707、alpha最大误差1。音频MP4实际AAC解码48,000样本，MAE约0.000491738／相关约0.99998036；明确有损，不称逐样本无损或每帧独立像素oracle。

自有源声明路径实际不存在时，静态preview／validation／纯视频MP4仍成功，includeAudio:true明确422，不授声明路径IO。真实0.68旧Worker短混版export禁用、旧／新profile重启结果读取、unsupported背景／预算拒绝、完整固定source SHA与ACL均实际覆盖；Python shape caps受控回归不冒充此真实旧Worker证明。12个terminal与原HTTP终态响应完整JSON值逐项相等（部分为clear后GET同一终态，不强称最初末poll）。

三个Server PID106271／107218／107315均实际exit0／无forced kill，其中107218只执行真实旧0.68 Worker混版能力检查，独立单列；本轮26观察private-group PID／startTicks身份全部gone，不混历史批次／member与Worker数。原HTTP manifest `artifacts/backend-extraction/b4-analysis-static-profile/http/http-final1/manifest.json` SHA5382188ecbd2c7a17a04a51732004de410bcede7c5abc9198bd2116281f3c9ba；最终总清单`artifacts/backend-extraction/b4-analysis-static-profile/http/http-final-integrity.json` SHA68b1b74e60c07fd710ef915988b871b6a6dee6f4e158f39ccdced58fefce8584。root核3028处递归SHA引用（含1542源／1441证据索引）及另1130部署文件／集合（其中legacy247），不同计数不混成独立文件总数。证据索引SHAc9e82c78bc20ac3ebb916ec6ac3a2cdaed24558d9304438554c768b4a9cd084c，部署索引SHA86fe6a6d4ad5a77f254411be548f48869e161ced98a7c15ccdeaa8359e8b466f；6同声明归一化source records全SHA一致，12终态JSON与原响应一致，三Server／26观察身份当前再次gone。产品5727145／工具4378b60未改，原1542输入无漂移，统一559 manifest保持不重写，不重复已绿测试。

### 23.4 保留范围

新profile只保完整分析进入静态Runtime，包脚本消费／音频驱动视觉、其他媒体、完整资源／字体、B3／B4／作者／Web仍独立；Core／schema／SDK／视觉语义不改。产品1h TTL不缩短，不宣称实际等满。全量、性能、GUI、screenshots、长片、人工平台与原暂停边界保持。


<a id="b4-compressed-analysis-0700"></a>
## 24. B4-F：固定MP3／FLAC独立分析（0.70.0，2026-10-14，有限出口已完成）

**B4-F固定MP3／FLAC独立分析有限出口已完成，完整B3／B4仍未完成。** 范围见[B4计划§10](../plans/backend-b4-plan.md#b4-compressed-analysis-plan)、ADR-0214，公开接口唯一维护于[统一API§12](../../backend-api.md#audio-analysis)。只扩独立分析及显式apply／clear／保存重开兼容，Export／VideoExporter／StaticExportTask不改，includeAudio:true仍PCM16 WAV，不新增音轨影片矩阵／项目／任务体系。

### 24.1 实际来源与受限解码

中央BackendAudioInputProfiles保旧PCM16 WAV，增加mp3-layer3-self-contained／native-flac-self-contained及fixed-compressed-audio-analysis-v1。BCL按bytes轻识别MP3非free LayerIII首完整帧／有界ID3与footer、native FLAC首STREAMINFO，候选不承诺完整解码。Worker实际probe codec／mono-stereo／正采样率≤192kHz门禁，强wav／mp3／flac＋file,pipe及strict -xerror／err_detect explode；格式错配／损坏不发布partial。原默认Media参数不改变，本批只受信强格式入口扩展。

原WAV exact SourceDurationUs保持；compressed为null省略，不写0／probe估值，无durationBasis字段。public AudioRef尾nullable AudioProfile旧WAV省略，仅旧sourceProfile按WAV解释，新compressed必须显式MP3／FLAC；两nullable字段显式JsonIgnoreWhenNull保持ledger／default serializer形状。Hosting／private grant尾AudioProfile默认WAV，中央GetInputFormat唯一派生，不重复InputFormat状态；execution旧analysis-pcmwav-v1仅WAV，新analysis-compressed-audio-v1严格匹配压缩来源。真实cap需当前engine／三个audioProfiles／两个sourceProfiles，旧0.69 Worker缺／错字段WorkerAnalysisProfileMismatch禁用。

固定asset pin／SHA／size／16MiB及原Analyzer／ToSourceData／canonical、48k mono／960hop／14.4M样本／15k points／4MiB／60s与唯一任务／预约／取消／ledger保持。实际采样budget权威；合法完整帧删尾可成合法短音频，不声称识别所有截短。新旧结果读取／恢复／apply按profile＋metadata固定allowlist，不泛accept；sourceHash原压缩bytes、PCM hash实际decode、Revision／ContentHash职责保持。

计划8d55df0；公共合同／0.70及签名澄清583a5a6；Host0edc503；Worker产品端点af77449；薄工具端点c00c3a5。无新项目、Core／schema／SDK／算法不改，原导出能力不扩大。

### 24.2 唯一统一有限验证（已终态）

正常flock／restore／分析器、-m:1／UseSharedCompilation=false；原solution23492 exit0／0警告错误、97.07秒。四个显式TRX551执行／551通过、零失败／跳过／中止：

| 范围 | 原session／通过 | 最终TRX（统一目录） |
|---|---|---|
| Hosting全项目，含原真实Worker分析／frame／export兼容 | 96370；442/442 | backend-hosting-final.trx |
| Worker全项目 | 25034；37/37 | backend-worker-final.trx |
| Server全项目，linux-x64 self-contained | 2580；33/33 | backend-server-final.trx |
| Media受限reader／probe／输入＋原Analyzer／SourceData五类 | 86290；39/39 | media-compressed-analysis-final.trx |

统一`artifacts/backend-extraction/b4-f-final-verification/manifest.json` SHA324b8ab0d46f7899189bc66cebb1c00a6e8dd4b4c58372861471c435d05a753a，source-freeze SHA149f31ee3ffe5cdeeb7b11a73175d8c71b91d332507aa4b19d80ef16df078eba；统一验证当时1553源／工具helpers／oracles／build输入无漂移，root核1867处SHA引用；随后工具修正另列，不能声称当前全部输入无漂移。原session／PID／日志／TRX class／DLL及版本在内；不把作者局部／变异计数相加，不另跑Rendering／Core／Runtime／Export／App／Application。

作者Media原26728正常19／compiled mutation37355真实3失败后Reader字节restore再19绿；Host原13819 69／SC48517 14及default serializer形状通过，owner15-source清单SHA9c4135dbd8bfa2c72cc204e6147905225b76f9705ae3d0a437b3939ee6d2e008。Host57928为nullable test compile-only，88343／75972／30718为新候选夹具误走真实Persistence loadAudio probe失败，仅newfixture使用原接缝，oldWAV默认保持；原日志/TRX保留。Worker normal21及IPC31和cap guard编译变异恢复不并551，最终StaticAudioAnalysisTask恢复SHA088ed0a4a446d3b3009d2130cbb76fc6674cb6023a0e6ee2b2370f6787eeb662。过程旧0.69产物不冒称当前0.70统一版本。

统一后只有同一新薄probe.py的请求label及重开声明路径oracle两处修正漂移，原551 manifest保持不重写；产品源无变化，补充工具冻结见`artifacts/backend-extraction/b4-f-final-verification/tool-followup-source-freeze-c00c3a5.json`。不能把初始1553 sourcefreeze说成当前全部无漂移。

### 24.3 真正HTTP与最终完整性终态

首轮原8332 exit1、9请求／119断言单列排除：新probe create与start_analysis用了相同client级RequestId，产品409 requestIdReuse正确拒绝；仅一行request label修正提交822c9aa，产品／四隔离发布不变，未重build。firstServer220528退出0／5观察identitygone仅属失败轮次，不并最终计数。第二轮原48281 exit1／116请求／897断言，完整canonical／采用保存重开及PNG／formal过程通过仍不作最终证明；工具把grant访问路径误作重开源码AudioPath，sourceSHA实际与grant不同符合原合同。仅derive自有state/sessions/.../loaded-input/project.json.audioPath并核原完整bytes，保持严格SHA断言，提交c00c3a5后完整final3另跑；产品／原551／1444发布不变，不混失败proc终态。

最终原37041 http-final3实际exit0，152请求／873工作流＋382凭证扫描＝1255断言。四个完整canonical为CBR MP3 mono44.1k／VBR MP3 stereo48k／native FLAC stereo44.1k／原WAV，各101points／96,624实际48k样本，bytes分别10,871／10,882／10,887／10,862，总43,502bytes／404points／386,496samples均与独立原Analyzer对相同encoded bytes逐字节一致；仅前三为新增压缩输入，不称四种新格式或与编码前原PCM无损相等。

三压缩结果显式采用／保存关闭重开保完整canonical与身份；FLAC另验证clear及undo恢复。accepted FLAC的静态PNG全部57,600预乘RGB／alpha像素实算误差0，正式普通1＋透明1帧报告通过，无新增影片导出。一个完整normalized snapshot按自有loaded-input/project.json源码AudioPath还原声明后Encode，13902bytes／15源字段及内嵌FLAC canonical完整SHA一致；访问grant路径另授IO，不等于声明sourceSHA，也不称package原路径SHA直接等HTTP。16个任务终态完整JSON值均与原HTTP raw json／body一致（4成功analysis＋3save＋3close＋3open＋1formal＋1failed＋1cancelled）。

实际strict MP3 native取消使用独立自有-re控制音源，不改变默认3输入解码速度，取消无partial result；真实0.69旧Worker能力禁用、原WAV／新三结果restart只读恢复完整bytes及job一致。坏输入与unsupported门禁保持，正常采样budget不截尾；默认1h TTL未实等、300秒／14.4M整曲未实际解码，不扩称所有截短能识别。

三个Server PID228932／229819／229901均exit0／无forced kill；本轮32个观察PID／startTicks身份（14独立private groups）当前全gone，不写成32 Worker或混失败run5身份。原HTTP manifest `artifacts/backend-extraction/b4-compressed-analysis/http/http-final3/manifest.json` SHA584cb30d2f4031cfb249375a803d98efeb881b997f2bdb118b42e27c84ff5d35；最终总清单`http-final3-verified-integrity.json` SHA9b82f57ca83000947429f8c52e6c868231407e308abf6b037a82a37c866f698c，权威索引`http-final3-verified-files-sha256.json` SHA51e6bc644e729866dbdbce2d250e8d6566366d0f1ac770a36e5d4a29e290e7bc，2198文件实际SHA全匹配，含current1444＋legacy247部署（1691）；独立publish原2292／21338／47851／3764各exit0。最初checker仅stdout log归档checksum问题，旧raw／index保留，最终verified独立清单替代，不改HTTP终态。

原551 manifest保持immutable，1553初始输入当前只有新probe.py两处修正漂移（最终SHA4a5dd969d2dec733e808308baca6c8809fbda8b6d4daaf2d3c7c18c2dca2f356），其他1552源不变／产品af77449不变；最终工具c00c3a5及补充freeze-c00c3a5 SHA dbdca2f12820fbffd278816ef57d29e09cd7e10de0bb51c50df7b517fbb32517。不重复原已绿测试／rebuild发布，不混两工具失败过程的请求、断言或观察退出数。

### 24.4 已确认未修媒体准备卫生与保留边界

Media发现原create／open默认autodetect nativeargv可读取owned同目录ffconcat所引用WAV：改变引用WAV后，manifest原bytes SHA保持但decode PCM hash改变，严格wav拒绝。证据`artifacts/backend-extraction/b4-compressed-analysis/media/development/autodetect-reference-review/review.json` SHAa76e16e38eadb9b0ff11699b8e174b37e9f02ac405b3a6a3ad94cd634c0a2ee8。此为自有same-directory／nativeargv复现，**未复现完整HTTP、跨client或任意路径读取**，不扩称权限逃逸／远程漏洞；root将旧服务媒体准备列为后续优先卫生修复。本批analysis强demuxer不表示旧create／open已修，也不突然收窄旧M4A等准备能力。

完整B3／B4、其他音频／export音轨、包脚本／字体资源／作者／Web及全量／性能／GUI／长片／人工平台和原暂停范围保持；本批有限完成不覆盖这些后续范围。


<a id="b4-self-contained-preparation-0701"></a>
## 25. B4-G：Backend自包含固定音频准备与未发布候选释放（0.70.1，2026-10-14，有限出口已完成）

**Backend自包含固定音频准备与未发布候选释放有限出口已完成，完整B3／B4仍未完成。** 按[B4计划§11](../plans/backend-b4-plan.md#b4-self-contained-preparation-plan)／ADR-0215修复旧默认autodetect引用卫生及候选cleanup。PATCH无新route／profile／schema／SDK／项目，不扩analysis／export，App／默认Media保留。

### 25.1 实际门禁与所有权

两个Backend default NewProjectPreparationService.CreateDefault／ConfigurePersistence切CreateForSelfContainedFiles；wav／mp3／flac／aac／mov／ogg受限probe＋typed强格式reader共file,pipe、MOV enable_drefs0／use_absolute_path0。原native选流／source rate／PeakBuilder及非PCM16 WAV／M4A／AAC／Ogg保持。九健康组合完整info／PCM／pyramid等价属本机有限支持，不是所有QuickTime／polyglot保证。

known／plausible首8／16BMFF含unknown合法prefix在probe前有界结构扫；非box首部被probe为MOV的fallback及所有MOV decode前再requiredscan。扫描url／urn／alis v0 flags恰1仅header＋4flags零payload、非标带payload拒；rmra／rdrf结构层拒，depth32／100k boxes／4096drefentries，64bit headers／opaque mdat保持。不得称所有字节排列的MOV均在nativeprobe前识别，格式／协议政策独立保护。

Host共用bounded hashreader前后核actualSHA／size／canonical fullpath及自身／祖先no-link，fallible read lease先于mutation／publish结束；不等OS immutable／任意ABA检测。open存在AudioMedia才附加此identity匹配门禁，缺metadata不主动补写／改变历史，始终先受原Core格式／版本／闭包约束，不称已真开任意旧版本path-only音频包；专用current-version合法AudioPath:null无音频回归成功，有声明源丢失不再silent无音频成功。导入后真实missing回归拒／0refs／重开lease恢复；pin后Import前微小窗口仅代码review，不冒称专用实跑。

发布前create registerthrow／open initthrow候选释放保primary／OCE／write lease／pinned目录；membership已加入后event／post异常不回滚真实已发布session，保resource pin。retention refs不是要求发布前全部清空，只有fallible固定读取与未发布candidate各有出口。失败job／cursor事件可存在，不承诺所有HTTP错误无session或完全无事件；Core准确版本门禁保持。

计划0b40e1a；Media7a95c2b；Host963a365；版本／生产端点8000f4b；工具ce588bb。原分析／带音轨export及GUI能力不扩，准备迁Worker与deadline隔离另批。

### 25.2 原统一有限验证

原solution99164 actualexit0／0警告错误／112.45秒，正常sharedflock／restore／analyzers／-m:1／UseSharedCompilation=false。三显式TRX557执行／557通过／零fail／skip／abort：

| 范围 | 原session／通过 | TRX |
|---|---|---|
| Hosting全项目，含共用分析hashreader兼容 | 12192；460/460 | backend-hosting-final.trx |
| Server全项目linux-x64 self-contained | 64339；33/33 | backend-server-final.trx |
| Media两新self-contained类／原bounded probe与reader／restricted input／FfmpegAudio六类 | 77651；64/64 | media-self-contained-preparation-final.trx |

`artifacts/backend-extraction/b4-g-final-verification/manifest.json` SHAa204fa4fc42ab6ddeed4b6385bcdd253f36b0e70cbf05378f67d7de8ae40af2a，source-freeze SHA2cf3f02b9dfa7b4ab305331a92ad1eecba7eadef5ef674a52ac180ac472463c0；1561源／test／tool／oracle／build inputs无漂移，原session／PID／TRX／class／日志／生成0.70.1与binaryhash在内。Media64为58＋原FfmpegAudio6，不跑全Media／Waveform／Video或未改其他域，不重复绿scope。

Media原flags变异90833正常build0→7实际4red，driver错估3导致exit1，finally字节restore再71947原54绿；新入口73736原58绿→compiled退first-known4/4red→字节restore再58绿。旧flags反证不重做；新6-sourcefreeze7bdbd2f2f1d9f014ed5b124a34edfc1c0313d062b56698d049ea5f91b96abcc0与旧510ab清单保留。Host原94608两compiled变异candidate1/1red、postidentity4/9red，源exact恢复后113原scope绿；7-sourcefreeze e498a51fdfe8494943fd448f801f3f6a1a9aac5cf4daf699755a00717adb73f7。首88127 actualexit1／108pass2fail／aborted为两fixture错误预期及自持membershipgateReadClientState self-lock，被作者TERM自有testhost297878结束而非产品自发crash；修tests76997／48937及最终113过程保留，不并557。

### 25.3 旧0.70真正HTTP修前证据（独立）

隔离old原38709 exit0，30请求／161workflow＋77scan＝238断言。47B同一manifest完整SHA41416c494c01358a5e3c2a2af63b319e27fcf9afbffe182281a0a36b25fd70c9等于upload／oldsaved／imported，修改same-client-owned邻接引用WAV令create／open真实准备duration1005000→1066344，3次sentinelFD直接Servernative读取。证据从旧nativeargv提升为实际HTTP，但仅自有同client同目录，不扩称跨client或任意路径逃逸。

`artifacts/backend-extraction/b4-safe-waveform/old-http`原manifest SHA05f9478869f95422457647a86fd52789316ca94523f32339f8f37571216b9fac；integrity SHA8aef037f0f8b0ab448fdc5db369d13cb52f2e1045125eee57fadc70e1d7a40d5；81文件index SHAd66cc722a3693f783823057f54eed97a21c894adb8947667008540351bf679a7。原Server286185 exit0／4directnative PIDstartTicks gone、5观察Worker成员属2groups gone；-re PATH观察wrapper为注入，不是native-owned-group。sentinel补充SHA27978e556814a6e5df70a8e48050b2197fe46d71ba2eaf84c7a17eb4818d56e4：create48240samples48k元数据SHA仅确定generator事后内存重建，初始文件已被open覆盖，不称事前capturedSHA；open34123samples32k现存file SHA7794ada6cb7b97c921d35f16962e31775bb5506b147ef7fa3bf434c26435bf6e另列。固定manifest＋真实duration／FD变化为硬证据，旧30／238不并修后接受。

### 25.4 新修后真正HTTP与完整性终态

原69113 http-final1实际exit0，131请求／570工作流＋366凭证扫描＝936断言。六家族固定encoded bytes创建→保存关闭→重开，实际准备duration与独立原default loader一致；AAC实际样本时长2,048,000µs，不把probe估值2,058,476µs当实测。HTTP未公开whole waveform pyramid；完整AudioStreamInfo／PCM／pyramid兼容由作者九健康组合另证，不把HTTP时长查询扩称完整波形公开验收。29任务终态完整JSON值与原末poll响应逐项一致。

新HTTP拒绝旧47B ffconcat create／open，未观察sentinel FD读取；外部MOV引用拒绝属于Media作者回归／原native实验，不称本轮HTTP运行了MOV负例。新负例包保持准确0.70.1版本及hash／size；只读路径附录核上传create337465／present open337466／deleted-reference open337467的真实ffprobe：三个固定47B输入SHA均41416c494c01358a5e3c2a2af63b319e27fcf9afbffe182281a0a36b25fd70c9，实际到达六format whitelist／file,pipe／enable_drefs0／use_absolute_path0，两个open不是Core版本／hash门禁提前拒绝。三次无-re注入、无后续decoder且身份gone；job与native输入只按原探针串行等待终态及三唯一次序关联，native记录不含jobId，未另归档独立stderr，不虚构这些字段。附录`artifacts/backend-extraction/b4-safe-waveform/http/http-final1-negative-native-path-proof.json` SHAa9eb56a31249a1ed927224e0410c3c9164b64f3dd18b086a05e463ae550b793a单列，原index不重写。

实际open取消的direct Server native PID337469／startTicks1921857在任务terminal前gone，同一handle重试成功；不用Worker group代替Host native取消证明。Server336696实际exit0／无forced kill；本轮31个direct Server native PID／startTicks身份全gone，另4个Worker成员属2个private groups全gone，两种计数不混为Worker数量，不合并修前观察身份。

三个matching SC发布原86652 Server／42841 Worker／48392原oracle均实际exit0，370＋434＋210＝1014独立物理部署文件SHA已核，renderOracle为同一oracle别名、不是第四部署。部署manifest SHA0ad7f962055ba8ec94f0f3e40a4e147d5d4d1bd61a6464c8ab0145a2076ae09f；产品8000f4b／工具ce588bb及1561 frozen inputs无漂移，无工具修正／重跑统一测试。

| 最终证据（`artifacts/backend-extraction/b4-safe-waveform/http/`） | SHA-256 |
|---|---|
| http-final1/manifest.json，原HTTP终态 | 1da6c9075fb5ccae235bdd6e47ed147aabfc7671db8c63d424afa62f08999231 |
| http-final1-integrity.json，完整性总清单 | b75a3133752571029a76605b2081f45572b9c777a3e93026c6e2cd445f0215db |
| http-final1-files-sha256.json，1391文件全部实际重算 | 5d2d8b4edeba1a932b26f756a64c883bcf475e8256d8e271265ffa3dd633be0d |
| http-final1-handoff.json，部署／源码／退出交接 | 7a802a92e914c8c643cf291f1fd6ca154dcf21aed86b5ac6c5bf1f4de4ce1db1 |

1391证据索引与1014部署物理文件是各自范围，不将清单引用数相加当独立文件总量。root逐项SHA、29完整终态、1561输入及PID／startTicks二次核验通过。原0.70的30请求／238断言仅修前复现，作者113／58与变异过程只作过程，不并新131／936或统一557终态。

### 25.5 保留边界

本批有限卫生／候选事务，不是完整QuickTime／polyglot parser／任意ABA防护，仍Host直接native而未新Worker／deadline。完整B3／B4、压缩音轨export、包脚本／字体资源／作者Web及原全量／GUI／性能／长片／人工平台暂停范围保持。

<a id="b4-compressed-export-0710"></a>
## 26. B4-H：固定MP3／native FLAC带音轨导出（0.71.0，2026-10-14，有限出口已完成）

**本批固定压缩音轨导出有限出口已完成，完整B3／B4仍未完成。** 起点`df5dcf7 / 0.70.1`，最终产品／工具固定`99189f2688bf7d44d3e441b038c72d7cad4e6f1d`，按[B4计划§12](../plans/backend-b4-plan.md#b4-compressed-export-plan)／ADR-0216实施。MINOR0.71.0新增有限公开能力，格式28／16／6、配置1、包profile2、SDK／依赖／项目划分保持；App／Core／GUI未扩。本轮Render调查保`feda459`的只读源码基线，不算实现或验收。唯一当前HTTP规范为[backend-api.md](../../architecture/backend-api.md)。

### 26.1 实际合同与兼容边界

旧`static-export-pcmwav-v1`仍用于PCM16 WAV／纯视频，新增`static-export-compressed-audio-v1`用于固定自包含MP3 LayerIII／native FLAC；公开保singular执行profile并增两个executionProfiles，三个audioProfiles、300秒maxCompressedAudioDurationUs与`strict-source-rate-decoded-sample-frames`依据。真实Worker探测当前EngineIdentity、恰两个source／三个audio／两个execution profiles，缺失／错配／未知／重复禁用；真实旧0.70.1 Worker实际disabled，不靠文件存在虚称兼容。

private尾AudioProfile、public音频AudioProfile和exportInput.ExecutionProfile均nullable且null省略，旧WAV／纯视频旧wire／inputSHA／报告字节保持；新压缩显式marker与profile。恢复仅接受严格旧／新配对，单字段缺失／错配／unknown拒绝；同时协调删新marker和profile改成合法legacy，在不读取源音频的恢复合同下不可辨识，不声称签名或穷尽任意篡改。

Host复用固定资源／实际SHA／size、no-link与read lease／pin门禁；声明snapshot SHA与重绑访问grant身份分开。Worker强profile demuxer及file,pipe，完整strict decode至EOF、实际exit0，用64KiB流只计源采样率每声道sample frames；源≤16MiB、mono／stereo、正rate≤192kHz，压缩帧数≤rate×300。足够性精确检查`sampleFrames × 1_000_000 >= derivedVideoDurationUs × sampleRate`，少一帧也拒；报告源时长为向下取整真实分数。300秒只属压缩export，不套旧WAV、通用prepare或48k分析。

完整源解码／前后实际identity先于Runtime factory／candidate，普通MP4固定AAC、透明MOV固定PCM16。受信nullable AudioEncodingPlanOverride默认null保持原Media／Export和AudioCopied语义；新增strict编码强格式／协议／xerror，不用-shortest，保-t和计划末帧。50ms仅输出音轨量化容差，不放宽源足够性。原唯一jobs／scheduler／ledger、120秒deadline／2秒grace、两层发布、64MiB Linux-x64单文件guard、报告／store预算沿用；取消与cleanup保primary／OCE并等私有组实际排水。

### 26.2 真正HTTP发现、最小Host修复及独立审查

final2在重开工程上复现：Core持久闭包的合法AudioPath与ResourceStore导入副本路径不同，原分析采用却要求两者相等，导致同源新分析不能采用。修复只捕获ProjectSession构造时get-only `PreparedAudioSourcePath`，采用验证当前非空fully-qualified canonical path等于该会话准备路径；继续核session／assetId／SHA／size、实际resource和lease后才一次领域历史。未改OpenCore、sourceSHA或授权资源所有权；任意同字节替代路径／null／空／relative仍拒，旧job仍不复活。

作者原证据`artifacts/backend-extraction/b4-h-analysis-reopen-author-20261014T092016Z-retry3/manifest.json` SHA`9fba5df179d353b03e37aa1340799bab55f84c40e8874c0776e3f61e41ff547f`：初末39／39，六WAV／MP3／FLAC open／recover组合及替代／非法路径；三成功编译变异分别6red→6green、1red→1green、1／3red（relative）→3green，源码精确恢复。受控作者fixture不是实际媒体HTTP证明。

文档／root独立沿三个改动源文件的调用链核固定session路径、资源身份／租约和一次历史出口：`artifacts/backend-extraction/b4-h-host-apply-repair-final-verification/independent-source-review.json` SHA`36d3b8891f0d73df8ebdf1e4be39a810db6554e7673b90a330b9f25d34fc7405`。Host另独核真正HTTP原始响应、完整ledger与显式enum serializer投影、报告／媒体／PCM／取消argv及实际identity排水，见26.5；这两类独立审查不由作者测试或短摘要替代。

### 26.3 正常统一验证：原716与修后540分开

全部dotnet用原统一flock、正常restore／audit／analyzers、`-m:1 /p:UseSharedCompilation=false`。首attempt原75890因NuGet网络23个NU1900于607.01秒exit1，未完成产品编译／测试；原invocation／log保持，未关闭audit／analyzers。隔离任务代理只作用任务环境，未改用户Clash配置。

原重试manifest `artifacts/backend-extraction/b4-h-final-verification-retry1/manifest.json` SHA`f4926a81dcc9029ab2f8bc319743ff558eb509f0bbe3c5d6aafceddb3f750e8b`，freeze SHA`046e63ad66d4efedfb982c4cf7da9ee5be7f0db52f2a62529cece43560ac7f4c`；1568输入与261binary／37生成0.71.0版本核过。原solution6054实际exit0／66.06秒／零警告错误，六TRX如下：

| 原scope／exec | 执行／通过 | 最终归属 |
|---|---:|---|
| Hosting／68483 | 494／494 | 晚期Host修复前 |
| Worker／75274 | 54／54 | 未改原scope，SHA引用 |
| linux-x64 self-contained Server／2730 | 36／36 | 晚期Host修复前，后续重跑 |
| Media受影响限定／93920 | 24／24 | 未改原scope，SHA引用 |
| Export含完整CommitBoundary／59012 | 100／100 | 未改原scope，SHA引用 |
| Runtime.Hosting WorkerExportIntegrationTests／51206 | 8／8 | 未改原scope，SHA引用 |
| **原合计** | **716／716** | **不是修后新全套数字** |

Host修复后同一正常solution原29952实际exit0／84.47秒／零警告错误；Hosting全原48239为504／504，self-contained Server全原60242为36／36，**新两TRX540／540**，未改四原TRX54＋24＋100＋8＝**186／186**只引用原SHA、不重复执行／计入新总数。所有原TRX零failed／skipped／aborted／errors／timeouts。manifest `artifacts/backend-extraction/b4-h-host-apply-repair-final-verification/manifest.json` SHA`d0a04a82ebc485401024f9f51714096b85abd3686a21952162072403f6247628`，freeze SHA`2069a64c0e363a63f4f9a51c9986cb3e4a1a175d9bf74615488f079f3cd65644`；1568当前输入无漂移、37生成版本全0.71.0。相对原716仅三Host／test源及此前薄probe.py工具修复桥接，原716不回写，不虚构新726／726。

### 26.4 真正HTTP终态与内容／音轨证据

原probe exec**47134实际exit0**；`artifacts/backend-extraction/b4-compressed-export/http/http-final3/manifest.json` SHA`73dcf7edcda621ca37d3a48a936333539456e83b2d0c2449084603c3e99842b2`。**385请求／2621工作流＋939凭证扫描＝3560断言**，28完整authoritative终态JSON逐体与原响应wrapper的json值一致。七成功影片报告／media与登记size／SHA绑定，六旧新恢复whole终态及12报告／media原bytesexact。

CBR MP3 mono44.1k、VBR MP3 stereo48k、FLAC stereo48k有理尾源、FLAC透明、旧WAV及取消后同handle重试共六影片，各90完整解码帧、合540帧与同声明sourceSHA／profile的原Runtime内容oracle核验。第七纯视频完整计数90帧／无音轨，Host独补ffprobe证据缺口；没有单独视觉内容oracle，不借六影片内容证据扩大。普通RGB MAE约2.1009，透明约0.0221451（并非像素零差）；透明186708 glyph matches、4979682全透明及178507半透明像素覆盖。

六带音轨影片的实际encoded source按native源rate／每声道PCM独核：CBR源132873frames／44.1k mono、产物132300；VBR源144624／48k stereo、产物144000；FLAC有理30000／1001派生3003000us，source与透明输出144144frames精确。AAC逐channel相关系数≥0.9994927并核RMS／RMSE，有损容差，不拿编码前PCM称MP3无损。透明FLAC完整stereo native PCM与产物逐字节一致，SHA`f93f3be96d3c4e666c77dc1d0e41c61a91d9eceecda2e5ab448aa0414b3628e9`。

重开后新FLAC分析完整canonical16442B／151points／144144samples，与原Analyzer byteexact；apply200／revision1、采用后保存canonical保持，随后分析static profile导出通过。短源含少一sample frame、source前后变化、预算、损坏／owned ffconcat拒绝；4failed＋1cancel均无artifact。active strict decode取消／同handle重试、真实旧0.70.1 Worker disabled及新旧artifact恢复通过。仅对拥有的一条MP3预解码以-re作源变化／取消控制，普通影片／重试不节流，不作为性能证据。

### 26.5 最终SHA、实际进程排水与失败归档

当前四self-contained publish原48567实际exit0／零警告错误，全0.71.0；server370＋worker434＋export-oracle430＋analysis-oracle210＝**1444物理部署文件**，真实旧0.70.1 Worker434另列，共1878分别核。deployment inputs1308与统一1568是不同集合，不混数；部署manifest `http/deployment-final2/manifest.json` SHA`de3ddb6df12633a1510c467b7083bc4ba0ae50aa779fbb15b8aa0c1cd375572f`。27publish观察身份gone单列，不并HTTP成员数。

HTTP index `http/http-final3.evidence-files-sha256.json` SHA`f88fc7af7a57d4ff3fdf81c14a7e1a898be50b97f74c58be9f78e74bb20d18de`，**942文件**逐SHA核；original-process SHA`bfc3a1c75d10a7bff09087d9d73ef5fde3f348f9970b51d0bbaa12a219e8fbde`，probe708885／start3913887 gone，三Server709031／3913948、712619／3916259、712762／3916440均exit0／gone，**13private groups共38观察身份**gone，不称38Workers；6原oracle decoders exit0。观察成员身份排水不宣称穷尽从未观察到的进程。

文档独核942 evidence／1444deploy／1308HTTP inputs／1568repair inputs／28whole终态及精确identity：`http/http-final3.docs-independent-audit.json` SHA`62fb7d3207610efc41c1362b1f5b32f4151108b0af9fed87f0b93cc478a34343`。Host独立原HTTP／媒体／PCM核验 `artifacts/backend-extraction/b4-h-http-final3-independent-audit-20261014T103924Z/manifest.json` SHA`a2abdad6c00716a7c19d652ee952dbaf8864d6134c6a29ed7c0d6b70f4567e9d`；PCM exact companion SHA`5b5277a05fa4392a0f20eb96155deddbac6b44d8fdeb0b82654a93987c5a1625`。最终原执行／index／deployment／audit入口汇于`http/http-final3-handoff.json` SHA`fde2c497facba4e4cd99ea63565fbd5603a1283b452113e164ab9262f465a39d`（本节http相对路径均以`artifacts/backend-extraction/b4-compressed-export/`为根）。源码与当前部署／旧Worker／原证据最终无漂移。

两次原失败独立保留：final1原82146 exit1／20请求191断言为薄probe重复requestId，最小工具修正66c8ef7；final2原56358 exit1／94请求911断言为26.2实际同源路径缺陷，最小产品修复99189f2。final2独audit `artifacts/backend-extraction/b4-h-http-final2-failure-audit-20261014T092806Z/manifest.json` SHA`62141edcbda76240755c223b111be8fa5b71759c526541e57d649de5be448ee6`，269文件／12终态／8private身份及Servergone；原失败handoffs保留，不并成功385／3560，不重写原716或旧B4-G557／HTTP131／936。

### 26.6 保留范围与统计

固定Git统计见[报告§15](../research/backend-baseline-change-report.md#current-statistics-0710)，不含随后本次终态doc commit、artifact或screenshots。本批300秒完整源strict预算是有限能力；产物仍≤10秒／600frames，不表示整曲／任意压缩格式或一般音频准备Worker隔离完成。完整B3／B4、包脚本／图片视频／完整资源字体／作者Web、全量／性能／GUI／长片／人工平台及原暂停范围保持，下一批须独立授权／冻结合同／验收。

<a id="b4-managed-audio-preparation-0720"></a>

## 27. B4-I：Backend受管理媒体准备／deadline隔离（0.72.0，2026-10-15，有限出口已完成）

**状态：本批有限出口已完成，完整B3／B4仍未完成。** 本批按[B4计划§13](../plans/backend-b4-plan.md#b4-managed-audio-preparation-plan)实施，合同固化于ADR-0217，唯一当前接口见[API§13](../../architecture/backend-api.md#b4-managed-audio-preparation-api)。起点`76b7529 / 0.71.0`，API迁移`c7ca282`已独立提交；受管理准备主体`6a7140d`后切到0.72.0。真正HTTP final1发现open错误wire兼容回归，`1c70fae`最小产品修复后，原八scope repair2统一775／775及不放宽断言的严格HTTP final2通过；原失败和repair1独立保留，不由切版或局部结果推定完成。

### 27.1 当前实现与原入口边界

Backend Server组合显式选择Worker-only：create继续同步201及原client receipt，open／recover继续原jobs、ledger、目录租约与candidate，不增加公开route、job种类、store、schema或项目。`audioPreparation`尾部能力字段由真实Worker握手核EngineIdentity、`self-contained-native-waveform-v1`、精确六家族、预算、工具启动与private-group／subreaper能力；不满足时disabled／`taskUnavailable`，不回退Host native。App的原`AudioWaveformLoader.CreateDefault()`保持，未配置managed准备的进程内Hosting仍用Backend默认`CreateForSelfContainedFiles()`，两者不混称同一默认loader。

Worker复用原自包含六家族wav／mp3／flac／aac／mov／ogg的受限probe、typed decode及MOV外部引用门禁。结果保原`AudioStreamInfo`的DurationUs／SampleRate／Channels／CodecName／ContainerName五字段；Info的probe时长与源rate完整解码sampleCount分别保原语义。base桶宽64 samples，包含尾部不足64 samples的桶，传输原min／max float32 bits，Host复用原pairwise merge重建完整pyramid。base层上限500000桶，在接受下一decoded block前检查；超限明确`WorkerAudioPreparationBudgetExceeded`，不能返回截断前缀。4MiB是额外协议payload边界，合法结果先受500000桶约4004160字节上限约束，不宣称真实HTTP独立触发payload-budget失败。此准备预算独立于snapshot／analysis；不套分析48kHz重采样、压缩export的300秒、mono／stereo或192kHz上限。

Host继续固定授权path／size／SHA、实际身份、read lease与未发布candidate所有权，Worker前后复核实际文件与祖先no-link；Worker复核实际来源改变按`taskExecutionFailed`／`WorkerAudioPreparationSourceChanged`完整性执行失败；Host原`VerifyFixedAudioSource`保原`taskExecutionFailed`且不新增failureCode。普通输入拒绝按入口与种类分开：Worker确认InputRejected转sanitized IOException，create保原422 `audioPreparationFailed`及`field:AudioPath`，open／recover保`projectLoadFailed`且无failureCode；NoAudioStream仍null，create为`noAudioStream`，open／recover为原`audioPreparationFailed`，均不额外添failureCode。仅open／recover generic fallback不因mapper新增`TaskExecutionFailed`；其他job种类保原逻辑，typed Worker deadline／transport／protocol／SourceChanged等真正执行失败保专用稳定failureCode。结果中的来源SHA、Info、完整计数、桶shape、float值和IPC关联不符均拒绝，不发布半成品。

### 27.2 排队、取消与本轮修正

准备内部许可从入队计deadline，默认60秒、配置1～120秒，取消宽限2秒；全局／每client running1／1、queued4／4。create名称、composition、资源ID非空及splitRule等基础字段先校验，再预约许可并进入原create gate，保原`invalidRequest`优先级；实际资源授权仍由原资源门禁执行。deadline为`WorkerAudioPreparationTimedOut`执行失败，用户取消保OCE／cancel语义；queued、active与shutdown沿各自所有者等待Worker及private-group/native成员实际排水后给结果。停止后同时拒绝新准备和重新配置，不以父进程退出或丢弃Task代替排水。

`6a7140d`已包含CA2016取消参数修正、create基础字段校验先于Worker admission，以及stop后拒绝重新配置的修正和对应回归。真实HTTP下的cancel／deadline／quota／shutdown、同句柄重试、断流后回执及旧Worker禁用已由§27.5独立验证；完整Info／桶／pyramid与协议预算属于原Worker／Media／Hosting回归，不由HTTP摘要字段推定。

### 27.3 repair2统一验证、历史尝试与进程归属

当前统一证据为`1c70fae / 0.72.0`的repair2。原八commands全部实际exit0，正常solution零警告／错误。七TRX共775执行／775通过，零失败、跳过、中止；不是全solution测试或真正HTTP计数：

| 本次范围 | 通过数 |
|---|---:|
| Backend.Hosting全项目 | 555 |
| Backend.Worker全项目 | 70 |
| Backend.Server linux-x64 self-contained全项目 | 44 |
| Media受影响七类 | 80 |
| Processes原LinuxProcessGuardTests | 2 |
| Application歌词准备三方法的Theory展开 | 11 |
| App原ProjectCreationTests | 13 |

权威manifest `artifacts/backend-extraction/b4-i-final-verification-repair2/manifest.json` SHA`c4304684c49cadb4f88a22150a3d0de5332c98e1f70846be57b3cb4daed09c55`；1602源码／输入与5849实际最终bin文件SHA无漂移，465个实际观察身份全部gone：70产品Worker、180 native media、45 private-group成员、20 SDK测试工具、146 fixture后代与4普通SDK collector，不全称为Worker。此前repair1的753／753及其manifest SHA`62259e28759cc586cf77591ac7c9aef530569a894a469ebe3db0ca59742fbdc8`保持独立历史，不与HTTP数字相加。

原`b4-i-final-verification/`的self-contained Server已执行42／通过39／失败3：新增测试第一次`ReadFromJsonAsync`关闭底层stream，随后再次读取同一响应失败。`dbc3f9b`只将该新增测试改为单次缓存完整body，原产品C#字节不变；随后以新repair1目录重跑相同八scope，无缩小范围。原Server42／39／3、原log／TRX／exit／green=false保持；两目录的`root-adjudication.json`／`prior-attempt-adjudication.json`内容同SHA`b7b815e74b4c3192ce5630dc853d4b995b74741872492d42bc64b600685b28fc`。更早0.71标记下solution零警告错误及Hosting535／Worker70／Media198只作阶段定向结果；普通Server因缺ASP.NET Core10 runtime中止的历史不改写，不并repair2 775。

原solution实际exit0、零警告错误，但原3秒观察窗仍有普通Avalonia SDK collector，原observer的green=false保持；原late-drain后核41个观察身份全gone。repair1的433个观察身份与延迟附证保持独立历史。原CA1869首轮编译失败为零测试执行；其原invocation的runner SHA指向旧脚本字节，期间ignored targeted_runner.py曾改30B，不能把当前脚本字节倒称为原失败runner快照；原失败log／exit及后续repair1／repair2证据分别保留。repair2的465个实际观察身份含4普通SDK collector，均在finalize前gone。

### 27.4 真正HTTP final1失败与最小兼容修复

真正HTTP final1原进程58071／startTicks208080实际exit1，137请求／611工作流＋376凭证扫描＝987断言，未完成本批有限出口。原`artifacts/backend-extraction/b4-worker-prepare/http/http-final1/manifest.json` SHA`518735fff52bee3480e65726b7663729f97b05fcacd252929c05f849aea39b7c`，original terminal `http-final1.terminal.json` SHA`a03599e49e8ebb90db503116451ffa2fa53e42e310910c8f224e576d3407b420`；378项证据文件SHA已核，Server58078正常exit0、无forced kill，17private groups的55个观察成员均gone，不把失败改写为成功或与后续成功计数合并。

失败在owned ffconcat的open拒绝合同：旧持久化入口为`projectLoadFailed`且无failureCode，新mapper改成error `audioPreparationFailed`及job `failureCode:TaskExecutionFailed`。独立review确认是产品错误wire兼容回归；create同输入仍应保持原422／`audioPreparationFailed`。决定最小修产品保原open／recover wire：Worker InputRejected恢复sanitized IOException，generic fallback仅open／recover不因mapper新增`TaskExecutionFailed`；NoAudioStream仍null及原`audioPreparationFailed`，不能全部写成projectLoadFailed。探针断言不放宽，修后原scope重跑见§27.5。该失败之前的六家族创建／保存／重开只作此次执行的局部观察，不把后续成功倒记到final1。

`1c70faeebeeddc6e08c73ecc71d73d6f9de2b8ee`已提交上述最小兼容修复：2产品文件＋6测试文件共192新增／4删除，未放宽probe断言。定向Hosting25／self-contained Server5与源码恢复后Hosting25均exit0通过；两项成功编译回退变异分别执行7／通过3／失败4及执行4／通过0／失败4，均实际exit1并由对应真实断言拒绝，随后两产品文件字节／SHA精确恢复。原新增测试CA1869编译失败实际零测试执行，保原失败不计通过。独立manifest `artifacts/backend-extraction/b4-i-load-error-compatibility/manifest.json` SHA`0e9b26d92fc2020dca36ee80c5dd52d9e4abd61fbd7a1faeb6ad6769fb795963`，52证据文件SHA已核，1602source无漂移／73观察身份gone；manifest head保创建时的修前`dbc3f9b`，修复内容由上述提交与实际fixed源码SHA绑定，不改写原manifest。此定向结果不并repair1的753、后续统一或真正HTTP数字。

### 27.5 新部署、真实握手与严格HTTP final2终态

`1c70fae / 0.72.0`源码冻结后，原publish session11867实际exit0，Server／Worker分别370／434文件；21个实际观察publish身份全gone。`artifacts/backend-extraction/b4-worker-prepare/http/deployment-final3/manifest.json` SHA`93dbb38af6ee5388a0bc528b0b57efceb555a80dc5825a4aa5346b784d4c8009`，独立publish后实物复核`deployment-final3.independent-postpublish.json` SHA`ef895f7b3480bc440ca531e9876a0bace227cb67f6d54b342af3a368141d1cc1`。原被拒部署和更早握手保留，不改写成当前执行体。

独立raw IPC握手实际读取当前`MakoKaraGenerator:0.72.0`及原旧`MakoKaraGenerator:0.71.0`执行体，当前返回完整prepare能力，旧Worker不支持prepare kind；两个Worker均exit0，13个实际观察private成员全gone。`handshake-final2/manifest.json` SHA`8fd4bde6ee1dac36a5535baa0a3d76aec0240c707321468673bb10b8f0aaba2d`。该范围只证明实际握手／生命周期，不并HTTP断言或波形／影片验收。

严格HTTP final2沿原探针与错误断言重跑，未改源码／工具或放宽scope。原session38819、进程93474／startTicks411598实际exit0，699请求／2655工作流＋1584凭证扫描＝4239断言。六固定真实家族创建／保存／重开与原loader同encoded bytes的精确duration一致；六真实未保存session经正常shutdown产生原autosave，再由新Host按实际marker恢复且encoded bytes保持。HTTP仅证明公开duration与state；Info五字段、源rate完整sampleCount、base float bits和完整pyramid一致由实际Worker作者回归证明，没有新增波形HTTP路由。

七control均通过：active取消、deadline、终止实际Worker、同步create断流后receipt继续、同步create deadline、queued取消／deadline／quota、active与queued shutdown；同句柄可重试，真实旧0.71 Worker disabled且无Host native fallback，同当前版本工程切回当前Worker后恢复。生命周期夹具仅对marker-owned WAV实际decode限速以观察native PID／startTicks，正常六家族流程未限速。owned同client ffconcat是HTTP直接负例；MOV外部引用、损坏／NoStream、base预算和完整payload拒绝属于独立Worker／Media／Hosting回归，不扩称HTTP全负例或任意／跨client路径隔离。

四Server PID93480／95591／95972／96067均正常exit0、无forced kill；52个private groups的172个实际观察成员身份全gone，publish21与握手13另计，不把父PID消失当成员排水或宣称穷尽未观察进程。58项whole终态与原响应或shutdown账本完整JSON一致，`http-final2.whole-terminals.json` SHA`d95068c6dd41e60595ceb2baa36e0f20fccea69610984654c798851a8a9da8d3`。1586个HTTP证据文件全部重核，`http-final2/evidence-sha256.json` SHA`4ad2f834d35a1421f4c763ff212ea10521818be5893fc98474ab80e85504d551`。

HTTP manifest `artifacts/backend-extraction/b4-worker-prepare/http/http-final2/manifest.json` SHA`4022d9e3186e11d18a1c2c10dd4017aa12291700a9aaf92a93f14e6c4b33c04e`；最终handoff `http-final2-handoff.json` SHA`24c658513fc6e013a2c2a59b7c8cd374f2d59d95624a88c6fbb533d6897196c4`。统一1602源码当前仍exact，freeze SHA`c30a73dd67bf8207e9b7aab5823a6ed7e9c8847e6b2522bc4ae25a9a03f20ed3`；1342 HTTP输入sourceStart／Final同SHA`6d36ce155c5c3210ebb8ead5d654e4f78d9dec674075ce905184fabbb109e532`。当前Server370＋Worker434、原waveform oracle210＋旧Worker434共1448部署文件全部exact，deploymentStart／Final同SHA`9894627a9dddd9c39d9b72c55a5c6332454181d9190ae6d7ff0b92bb2724c412`。原oracle0.70.1与旧Worker0.71部署来源由handoff分别绑定，不改写旧文件归属。

独立只读终态审查重新hash全部1586证据，逐份核58whole与原响应或shutdown账本完整JSON、六恢复marker／完整bytes／duration／dirty／rev0／history、七control实际identity与wire及ffconcat两whole的`projectLoadFailed`／无failureCode／无result；并复核1342＋1602输入、1448部署文件、四Servers退出与172成员排水、43握手证据／8 raw帧的真实0.72／0.71身份及13成员排水，publish21另计。审查通过，不新增或重跑产品验收范围。

### 27.6 有限完成与保留边界

repair2统一775／775与严格HTTP final2的699／4239分别完成本批有限出口，repair1 753、final1失败137／987、定向修复／变异和更早过程不相加。4MiB协议额外边界由定向合同验证，不宣称合法结果在HTTP独立触发payload-budget失败；此前analysis／export兼容由当前统一测试覆盖，本批没有新增Runtime像素、影片或音轨验收。

0.71 B4-H原716／修后540与原186、HTTP385请求3560断言、942证据SHA及失败归档保持§26原始归属，不并入本批。MINOR0.72保持格式28／16／6、配置1、包profile2、作者SDK、固定依赖与生产程序集划分，不新增项目；完整B3／B4、包脚本／媒体字体资源／作者Web与原全量／性能／GUI／长片／人工平台暂停范围继续独立。

<a id="b3-package-source-codec-0730"></a>

## 28. B3-E：固定效果包源 codec 与同进程 Runtime 有限出口（0.73.0，2026-10-15，有限出口已完成）

**状态：本批有限出口已完成，完整 B3／B4 仍未完成。** 产品提交为`8de271a`，合同见[计划§8](../plans/backend-b3-completion-plan.md#b3-package-source-codec-plan)与[ADR-0218](../decisions/decisions.md#adr-0218)。本批在 `Backend.Rendering` 增加 `PackageProjectSnapshotCodec` 与 `package-lyrics-scene-v1` 源 codec；不加入 Contracts 运行 allowlist，不开放 package Worker／IPC、HTTP 包 route、隔离／失控恢复或图片／视频背景能力。完整包脚本、图片／视频资源、可搬移字体、作者 Web、全量／性能／GUI／平台人工范围和原暂停矩阵保持独立。

### 28.1 合同与同进程出口

codec 覆盖 ProjectSnapshot 的 15 个可写源属性，严格保留完整 package manifest/files bytes、顺序、SHA／长度、PackageVersion／SDK／profile、预算、`PackageMaterial`，以及直接效果实例全部字段、执行顺序、原规范 JSON 与数字精度、绑定／读取范围、seed、启用状态和五项 `ProjectEffectBudgets`。完整 `AudioAnalysis` 联合状态、source／PCM hash、sampleCount、points／hops、revision 与诊断字段均闭合；`EffectInputsFingerprint` 是原 `ProjectEffectInputs` 算法的固定源指纹，不是 snapshot wire bytes SHA。4MiB、总 200000 个源值（含 JSON 深层值）、JSON MaxDepth64、提前图深度60、规范 manifest bytes byte-equal 和当前 PackageProfile 门禁均由 codec 严格验证；非空 PresetApplications、图片／视频 Background 及其媒体引用／外部引用拒绝，AudioMedia 源声明不因该门禁拒绝。

同进程原正式 Runtime 的普通／透明目标、效果包实例真实像素贡献、前进／回退 seek／再次前进和完整 round-trip 通过。四类夹具为 C# 缩放、shader／graph、sprite 资源与无控制器实例的源 PackageMaterial，双输出各六个时刻逐帧 BGRA 精确一致；移除贡献者后像素有差，真实非空 Warning 诊断及独立会话释放均检查。固定字体 SHA 为`b76b0433203017ca80401b2ee0dd69350349871c4b19d504c34dbdd80541690a`。该结果仅证明固定字体／包 bytes 夹具下的同进程源还原与 Runtime 等价，不证明 Worker、HTTP、跨机器字体、脚本失控回收、图片／视频闭包或完整 B3/B4。

### 28.2 统一五 scope 证据

权威 manifest 为 `artifacts/backend-extraction/b3-e-final-verification/manifest.json`，SHA `f1351ce60eb4e8a5f199a6b621d8d373a813dca5db4308701f0de1618fc60848`。五 commands 均 exit 0，solution 零警告／错误；Rendering 155／155（旧 61＋新合同 86＋Runtime 8）、Hosting 555／555、Worker 70／70、linux-x64 self-contained Server 44／44，合计 824／824。1609 source、5849 binary 无漂移，37 个 solution 输出版本均为 0.73.0，输出 DLL 与对应 intermediate DLL 逐字节一致；362 个实际观察 PID／startTicks 在 finalize 均 gone。

root 独立最终审查 `root-final-audit.json` SHA `0bba0794b05e820d7d8dff591d52cfc271e815248a214452517284867b179741` 重核 7484 个唯一文件（1609 source＋5849 binary＋26 evidence），零 hash mismatch；作者审查 `root-author-evidence-audit.json` SHA `69417b5491920f590cbfa5861213ad64a2f2edd6f3e265aff6a6e7bfd6c4463d`、作者过程和 mutation 过程另列，不并入 824。预算顺序证据来自源审查而非 allocation telemetry，不声称运行时资源分配遥测。统一 manifest 的冻结起点仍是主 head `08c1807`，不改写为后续产品提交。

原失败保持原始归属，不回写为成功；未改 Media／Runtime 生产矩阵不因本批重跑原完整范围。下一步 package Worker／隔离是独立合同和验收切片。

五条原命令的实际进程分别为 solution 62274、Rendering 66268、Hosting 66726、Worker 70699、Server 73395，完整命令／exit／TRX／日志见同目录各`*.invocation.json`与`*.evidence.json`。`planned-scopes.json`经root批准后冻结，正常restore／audit／analyzers保持；dotnet统一使用`flock -o /tmp/mkg-backend-b4-dotnet.lock`、`-m:1 /p:UseSharedCompilation=false`。MINOR0.73.0保持格式28／16／6、配置1、包profile2、作者SDK、固定依赖与生产程序集划分；本批未新增项目或执行真正HTTP，旧B4-I证据不倒记到当前版本。

### 28.3 作者过程、缺口修复与变异边界

原合同初轮75项中74失败／1通过暴露PackageMaterial getter元数据处理问题，后以仅包codec启用的严格converter修复；原manifest规范字节三项负例首次3失败，修为原decoded manifest与Core规范结果byte-equal。独立源码审查又发现Encode／Validate未核包profile而Decode固定当前profile的不对称缺口：新增version／SDK／shader profile三负例修前3失败，修后Rendering155／155通过。编译及工具目录的更早失败保原日志，不记为测试通过；原79／82／83合同与152 Rendering过程数字也不并824。

成功编译后的指纹门禁删除变异由7项真实断言拒绝，decoded seed改为1的变异由1项在指纹门禁先行拒绝，不能将该变异另称为直接字段断言的独立证据；回退profile门禁的变异由3项真实断言拒绝。四产品文件按各阶段保存的bytes／SHA精确恢复并正常复验，最终源由统一1609输入冻结绑定。总源图与effects DTO提前合并计数后才复制base64，decode先核全部encoded长度与累计预算再分配binary；预算负例证明拒绝，分配顺序由源码审查证明，不是allocation telemetry。

作者合同manifest `artifacts/b3e-author-contract/final-manifest.json` SHA`dd585dce93d000401101497e8adaae52d1f0c1cb5fd453b9760022eb10a4516f`与profile修复manifest `artifacts/b3e-author-profile/final-manifest.json` SHA`8aebf2154896a162d8776dbb3a2d13745d771f196eade3f93fd7211a32e3ffab`保持各阶段原源码归属；Runtime作者证据位于`artifacts/backend-b3e-author-runtime/`。root作者审查分别重核39及17引用hash零漂移，统一824始终只统计§28.2四份最终TRX。

<a id="b3-package-worker-isolation-0740"></a>

## 29. B3-F：内部 package Worker 会话与失控隔离有限出口（0.74.0，2026-10-15，有限出口已完成）

**状态：本批有限出口已完成，完整 B3／B4 仍未完成。** 产品提交`1524625`已推送`origin/refactor/individual-backend`；合同见[计划§9](../plans/backend-b3-completion-plan.md#b3-package-worker-isolation-plan)与[ADR-0219](../decisions/decisions.md#adr-0219)。本批增加私有`PackageRenderWorkerContract`与显式`WorkerPackageRenderFrameSessionFactory`，不接入公共HTTP包执行，不扩大公共source profile／旧Static SelectProfile、route、task、recovery、权限、schema或保存历史。

### 29.1 固定包会话、关联与诊断

capability／open／render／close绑定同一实际Worker Process及原pipe；在发送包open bytes前核实际EngineIdentity、`package-lyrics-scene-v1`、PackageVersion2、SDK1.0.0、shader profile、完整compiler refs（含TokenId）的identity／count与可构造性。Host expected refs由Worker部署refs bytes及Host自身TokenId assembly actual hash计算；`ScriptCompiler.ReferenceIdentity`／`ReferenceCount`只公开实际参考集身份与数量，不改编译算法。能力仅报告实际读取／构造成功，不构成完整SDK参考集签名或文件immutable保证；Worker变化不能沿用旧capability。

复用B3-E完整固定包源codec，open净snapshot wire上限为4MiB−24B＝4,194,280字节；open一次捕获包bytes与source，逐帧仅传session handle、source hash、sequence、time、quality、target、transparent，不逐帧重编码工程、重读包目录或重新编译。sessionId就是固定输入生命周期，没有独立generation字段。结果严格核input／output／session／source hash、帧序／时间／尺寸／stride／像素格式／quality／alpha与完整`RuntimeDiagnosticSnapshot`；Runtime Error禁止发布像素，保留真实occurrence epoch／sequence及作者诊断身份；实际作者Error回归保留`ActualPackageRuntimeError`及完整snapshot，不改写成通用`WorkerRuntimeDiagnosticError`。

完整编译诊断／source diagnostic DTO尚未传输，实际编译失败明确返回`WorkerPackageSourceDiagnosticsUnsupported`；非空`ImageEffectDiagnostics`／`MaterialDiagnostics`明确返回`WorkerPackageDiagnosticsUnsupported`，不丢弃后发布成功帧，不伪造Runtime发生。正常持久Worker与直接原Runtime对四类固定夹具（scale、graph、sprite、无控制器material）、普通／透明双输出、六时刻含前进／回退逐帧BGRA与诊断等价，移除贡献者的反事实帧证实实际效果像素贡献。真实Warning、Error禁像素、编译／图片／材质诊断拒绝及恢复均有实际Runtime回归。

### 29.2 失控脚本与停止边界

Program reader与同步脚本执行分离。同一Worker的static／package Runtime会话互斥，双向open拒绝且保留原已打开会话。真实Initialize／Evaluate runaway marker后执行cancel／deadline／Dispose六组合，核实际private group与Host subreaper观察成员排水；脚本仍活跃时Host并行健康帧、停止后新会话／新Worker恢复通过，unrelated进程不受影响。只有内部deadline独赢映射`WorkerPackageTimedOut`，caller cancel保`OperationCanceledException`。

合作取消空ack只有真实取消竞争且原input／session／source／sequence关联完整时接受，不要求成功帧尺寸／像素／诊断；伪取消、错关联、含输出或错误的取消回执拒绝。会话失败后禁止继续渲染，释放等待实际Worker、pipe与观察成员排水。本结论限本机Linux实际观察的private group／常规后代，不构成任意恶意脚本的OS sandbox、未观察短命进程的全量计数或跨平台保证。

### 29.3 统一八 scope 终态证据

权威manifest为`artifacts/backend-extraction/b3-f-final-verification/manifest.json`，SHA`3c978536ed679938f9bb92368573da5a1d9ffa9ce0950c0df7116c6f921e28f8`。八commands全部exit0，正常solution零警告／错误；七TRX共971／971：

| Scope | 通过／总数 |
| --- | ---: |
| Backend.Rendering全量 | 155／155 |
| Backend.Hosting全量 | 639／639 |
| Backend.Worker全量 | 70／70 |
| Runtime.Hosting定向 | 31／31（package20＋旧static9＋dependency2） |
| Processes LinuxProcessGuardTests | 2／2 |
| linux-x64 self-contained Server全量 | 44／44 |
| Scripting精确CompilerTests／EffectPackageCompilationTests／CompiledEffectPackageTests | 30／30 |

1618source与5849binary无漂移；37个solution输出均0.74.0、output／intermediate DLL逐字节一致，448个实际观察PID／startTicks在finalize全部gone。source freeze SHA`f7e321d6d9839b6c2844d8858f099f20ca23605a7128460019a792754ae9235f`。统一manifest原HEAD为`7c31865`，是验证执行基线；产品`1524625`与冻结源码bytes一致，不把manifest HEAD改写成后续提交。原B3-E manifest SHA`f1351ce60eb4e8a5f199a6b621d8d373a813dca5db4308701f0de1618fc60848`已重核未变，824不并971。

原命令、exit、TRX、日志、进程及SHA详见同目录各`*.invocation.json`／`*.evidence.json`；root批准计划八scope并冻结，正常restore／audit／analyzers保持，dotnet使用`flock -o /tmp/mkg-backend-b4-dotnet.lock`、`-m:1 /p:UseSharedCompilation=false`。本批未执行真正HTTP；最近HTTP仍属0.72 B4-I原始证据。

### 29.4 独立self-contained部署与实际refs

以下三manifest全部green，source与主freeze匹配、runner及实际部署bytes保持；这些证据不并统一971。

| 独立阶段／manifest（`artifacts/backend-extraction/b3-f-deployment/`） | 终态 | manifest SHA |
| --- | --- | --- |
| `publish-final1/manifest.json` | Release／linux-x64／SelfContained=true发布exit0，零警告错误；434文件，15观察身份gone | `6568726ba62f5f66cd098388163fa022ab45c38faef4e7984d11fbad907521c3` |
| `ipc-final1/manifest.json` | raw IPC实际健康能力与单删System.Runtime.dll副本的WorkerPackageUnavailable；两Worker各exit0，2身份gone | `4e02e82547186f319ce2cf52a7b9cd817d5108f419a38832717c00a755f654ba` |
| `runtime-final1/manifest.json` | 匹配Release／linux-x64／SelfContained=true Host通过实际发布apphost运行package20／20，exit0；47观察身份gone | `0728b50c52b90a763d5927e904d49162796e2dae51403ebe0064a2ce21e34421` |

实际发布apphost SHA`166abbe5634d2d31409fa56fb65f021b2663678da514326c94e7e6150850c27c`，`MAKO_B3F_TEST_WORKER_PATH`指向该绝对路径；实际capability为EngineIdentity`MakoKaraGenerator:0.74.0`、shader`skia-raster-3.119.4-v1`、refsCount168、refs identity`54065d5730fb15dac384bcd350321f964303aed9b75e9ce53daa378753e5d089`。负例先复制完整434文件并核byte-equal，再只删除`script-refs/net10.0/System.Runtime.dll`；433剩余副本文件核验，不改健康发布。已发布Runtime TRX SHA`f067e721147144e6fe8984bb9536094ac42e794cfcaa1cbf6135f0e825d57d49`；配置相同不替代实际TokenId／refs hash匹配。

### 29.5 独立审查、作者过程与未完成范围

独立最终审查`artifacts/backend-extraction/b3-f-independent-audit/final-review-summary.json` SHA`fb6d036621f6cb8e964c762fe438aaea2a95b8f681a6d7c113c0b8a94470a622`：9482个SHA引用、18份TRX及1058个唯一实际观察身份重核，零mismatch、身份全部gone；这是跨统一／部署／作者证据的审查口径，不能替换统一448或累加为测试总数。旧B3-E manifest及18份作者历史文件保持原始归属；历史binary按原scope快照保存，当前bytes只由final-binaries核验。

作者合同历史见`artifacts/b3f-author-contract/author-history.json`，最终合作取消scope为`cooperative-final84/`的84／84；双模式作者证据见`artifacts/backend-b3f-mode-author-20261015/`的2／2，均不并971。初轮fixture缺完整refs、非canonical capability身份、foreign task/error、伪取消、错误input关联及capability后Worker换代等失败保原日志；最终82、此前80／81及Runtime18项中17pass的两失败轮次不回写，不与最终统一或部署20相加。本批作者未运行mutation，不虚构变异证据。

MINOR0.74.0保格式28／16／6、配置1、包profile2、SDK1.0.0、固定依赖及生产程序集，不新增项目。完整B3／B4、公共HTTP包profile及正式任务、预设／完整媒体字体闭包、完整source diagnostics DTO、作者Web、全量／GUI／性能／长片／跨平台人工与原暂停矩阵继续独立；本有限隔离出口不自动恢复这些范围。


<a id="b3-package-diagnostics-0750"></a>

## 30. B3-G：内部package三类完整诊断传输有限出口（0.75.0，2026-10-15，有限出口已完成）

**状态：本批有限出口已完成，完整B3／B4仍未完成。** 本批接续B3-F，合同见[计划§10](../plans/backend-b3-completion-plan.md#b3-package-diagnostics-plan)与[ADR-0220](../decisions/decisions.md#adr-0220)。仅补内部package的完整source／image／material诊断；公共HTTP包profile／route／正式任务、预设／外部媒体字体闭包、完整B3／B4及原全量／GUI／性能／长片／跨平台人工暂停保持独立。统一与独立部署证据分别计数，产品提交号随后由Git记录。

### 30.1 完整值、固定catalog与预算

capability显式协商独立`PackageDiagnosticsVersion=1`，缺失或旧版明确拒绝，不以空数组回退旧诊断能力。source保PackageId／Revision／InstanceId／ExportId／Code／Message／IsError／Path／Line／Column十字段，0行列合法，nullable实例／导出成对；实际编译失败以`WorkerPackageCompilationFailed`及`RenderSessionFailureException.SourceDiagnostics`完整返回，不伪装为Runtime occurrence。

图效果保实例／artifact／Graph资源三元身份／InputId／Code；材质保Material三元身份／InputId／Code与Script或Source判别owner。Script的PackageIdentity必非null，实例／artifact／package与Source的path／region／slot／SourceMaterial／规范ParametersJson不得混用；Host按固定snapshot的包catalog、启用实例及SourceMaterialCatalog恢复原SDK资源和owner，陌生来源拒绝。controller ArtifactHash只核64hex格式及固定包／实例关联；直接图完整revision须等于固定实例源。Host不独立重编译，也不证明compiler代次DLL字节匹配。

三类各≤256条／64KiB实际UTF8序列化数组，合计≤512条／128KiB完整诊断DTO实际UTF8字节（含结构）；wire实际UTF8亦受预算检查。新增诊断DTO及capability JSON严格核必填／未知／重复字段，既有WorkerMessage的nullable字段并非整体改为必填，顶层只特别扫描重复packageDiagnostics容器。超限`WorkerPackageDiagnosticsBudgetExceeded`、非法字段／身份`WorkerPackageDiagnosticsInvalid`，不截断、不附超预算容器。成功帧显式包含image／material且source空；真实警告可随像素完整返回，原Runtime occurrence身份与Error禁pixels、ActualPackageRuntimeError完整snapshot及无伪Runtime发生保持。

### 30.2 统一六scope终态证据

权威manifest`artifacts/backend-extraction/b3-g-final-verification/manifest.json` SHA`4c5ca351816072651f79e241e34d9cce43c2af5698979cd932b0d61f65701891`，六commands全部exit0，正常solution零警告／错误；五TRX1001／1001＝Rendering155＋Hosting699＋Worker70＋Runtime定向33（package22／旧static9／dependency2）＋linux-x64 self-contained Server44。1623source／5849latest binary无漂移，37个solution输出0.75.0且output／intermediate DLL逐字节一致，476实际观察PID／startTicks在finalize全部gone。

source freeze SHA`2921307de583e3aef31a46c90c164296f048a6be17482747c9ca3ba0ab802a2e`；manifest原HEAD`0549705`为执行基线，不改写为后续提交号。原命令／exit／TRX／日志／进程／SHA见同目录各invocation与evidence；正常restore／audit／analyzers保持。Scripting／Processes生产未变，本批不重复其完整矩阵；init／evaluate×cancel／deadline／Dispose六组合由WorkerPackage定向覆盖，Host并行健康、新Worker恢复、同Process／原pipe关联、static／package互斥及合作取消边界保持。本机Linux实际组隔离不是任意恶意脚本OS sandbox或跨平台保证。

### 30.3 独立部署与真实旧协议拒绝

`artifacts/backend-extraction/b3-g-deployment/publish-final1/manifest.json`已green，SHA`6617ff73f34611bc2c1748f41c60e8f2ebe8d287e49ba70812d7666b7f5fac6e`；发布Release／linux-x64／SelfContained=true，exit0、零警告错误，434文件与15实际观察身份gone。`ipc-final1/manifest.json`已green，SHA`5b43d25ebaaa80c2e0bb590857751d1923f144f3edd6b474796f7923c95e5814`，健康Worker报告diagnosticsVersion1，单删System.Runtime.dll副本明确拒绝。`runtime-final1/manifest.json`已green，SHA`9218859f0897be4a454d01ec1e336ef510306120c32c5e201d3be5885c7d71a9`，匹配Release／linux-x64／SelfContained Host通过实际发布apphost的22／22，exit0、50观察身份gone；TRX SHA`f5230825b37fc2cbe7955978d8965ef6661457b6057a90143392ab614bae6303`。实际refsCount168／identity`090810ae3a8389d5b473c1f6e919bdacbfd93f24d30d8dcba98a329d3ec653b0`；raw两Worker各exit0、合计2身份gone。真实0.74旧Worker的raw缺诊断版本已捕获，但legacy-final1 Host harness因CA1869构建失败、0tests，保原失败；legacy-final2只修ignored harness的静态options缓存后已green：manifest SHA`8824497f105ed81c90a1f7fce0dbbd494533671d4865923180d48ad5d60befaf`，publish／run均exit0、零警告错误。真实旧0.74 Worker raw capability缺PackageDiagnosticsVersion，当前Host精确拒绝`WorkerPackageCapabilitiesInvalid`、ProcessId归null，旧434部署文件全部SHA未变；raw／publish／run分别1／3／2观察身份gone。原CA1869失败Program源码按SHA匹配保存在`legacy-failure-source-preservation1/`，不改写旧证据。各独立证据不并统一1001。

### 30.4 作者过程与历史边界

作者过程位于`artifacts/backend-extraction/b3-g-author-attempt1/`至`attempt9/`：attempt1为6处CS1591 XML注释构建失败，attempt4为mapper夹具Composition参数编译失败，attempt5为130中126pass／4fail的ImmutableArray断言重载误选，保原记录；修为数组比较后attempt6为130／130、attempt7为137／137、attempt8最终144／144，attempt9为null Script owner门禁修复的mapper22／22。attempt2 Hosting build零警告错误、attempt3为108／108，均独立作者过程，不并1001；本批未做mutation，不虚构变异证据。直接图身份要求完整revision及export＝固定实例export；控制器artifact仍只做格式和source association验证。

Runtime作者证据位于`artifacts/backend-b3g-author-runtime/`，attempt2的22／22与attempt1原日志保持独立。最终独立审查`artifacts/backend-extraction/b3-g-independent-audit/final-review.json` SHA`931b30bd135e4be9955dc9e085e5a97315922ce5e48b211bb65e4017a915875e`，核10186个唯一SHA引用、13份TRX的hash／counter完整性及1104个唯一实际观察身份，零mismatch、身份全部gone。13份TRX包括attempt5原126pass／4fail，不能写成13份全部通过；统一通过仅五TRX1001，实际发布另一TRX22。12个本批actual runaway终态（统一6＋已发布6）及48个marker／JSON文件均入SHA索引；不是新增48次测试。旧B3-F／B3-E manifest SHA重核不变。

原B3-F971、独立部署20及作者84／mode2、B3-E824与旧B4-I真正HTTP699／4239保持原始归属，旧manifest SHA重核未变；本批不执行真正HTTP。MINOR0.75.0保持格式28／16／6、配置1、包profile2、SDK1.0.0、固定依赖及生产程序集，不新增项目；后续公共package HTTP仍需独立合同与验收。


<a id="web-g1-backend-0751"></a>

## 31. Web W0后端基础：G1过期空闲client按需回收与稳定headless启动（0.75.1，2026-10-15）

### 31.1 范围与合同

用户已明确授权恢复G1与稳定Web后端基础，编码、测试、启动验证与独立审查分工执行；本批不是完整B3／B4或外部Web W0验收。起点产品0.75.0／`696acea`，执行HEAD为交接文档`cea473d`；新增PATCH0.75.1，生产代码／14项新增回归与版本已由root提交推送`e60376a`；该提交源码与下列执行freeze逐字节匹配；稳定启动工具与Python缓存忽略规则另提交推送`51c29b1`。完整合同见[ADR-0221](../decisions/decisions.md#adr-0221)与[唯一API§3](../../architecture/backend-api.md#3-客户端登记心跳与状态)，后续交接见[web/README.md](../../../web/README.md)。

HTTP异步新登记配额不足时才两阶段claim过期且无operation pin的client，锁外检查／清理；同步Register兼容入口不自动回收。有效lease、工程、运行／排队任务、保留期内终态job、有效／在途回执、SSE／流式响应等认证操作与资源引用均保护。claim期间认证／resume临时401，失败解除claim；实际移除后access与resume均失效。晚到预览通知不创建已删除事件流。只清进程内client-local状态，不删除持久工程、ledger、报告或上传文件；资源继续原TTL／引用语义。不把过期等同注销、自动discard或必定马上回收。

### 31.2 可重复发布与启动

`tools/backend-web-host/host.py publish`在新目录分别发布self-contained Release/linux-x64两端、保留完整Worker及`script-refs/net10.0`；使用同一dotnet锁、`-m:1 /p:UseSharedCompilation=false`。清单记录version、HEAD、dirty diff指纹、生产输入与全部部署文件SHA，发布期间输入变化拒绝成功。`run`校验部署、清除继承MAKO变量，固定native工具、显式HTTP Origin、随机loopback；稳定state/projects与每次新0700 run／0600 bootstrap分离，不覆盖旧凭证。正常SIGINT／SIGTERM仅转发自有Server并等待排水，正常退出删除本次bootstrap。stdout保原BACKEND_READY与结构化WEB_BACKEND_READY，凭证只通过私有文件交付。

最终部署：`artifacts/backend-g1-w0-0751/deployment-final2/deployment.json` SHA `d7a0ec7d0799b3ca3719d2f5d02e6b7bde55f304a1680eec84f8ddb957aa0a13`，version0.75.1、执行源码HEAD`cea473d`＋dirty生产输入清单／diff SHA，不冒充当时已有最终提交。首次deployment-final1两端publish成功但工具把refs路径误写少了net10.0，整体exit1独立保留；仅修工具后重新发布final2成功。

### 31.3 实际HTTP与启动验证

默认生产access120秒／lease300秒、单资源16MiB不变，主证据为 `http-default-final2/summary.json`：**79请求／491断言**、SHA `2629078f732e5ad67561333480c542c641c06506568e4ea50d534150df18658e`。真实bootstrap→登记→heartbeat→resume、capabilities.audioPreparation.enabled、短PCM16 WAV与LRC上传／Worker准备／新建、读歌词／改时／撤销／重做、静态帧、保存成功终态→关闭成功终态→重开查询、正常SIGTERM与同profile重启后旧access401／新身份打开成功。PNG核完整SHA／尺寸、全RGBA解码、alpha255与可见歌词内容；这是传输身份与内容存在验证，不是独立Runtime像素oracle。两Server正常exit0，采样遍历所有task/children后12个唯一实际观察身份全部gone。

G1独立受控HTTP为 `http-g1-short-lease-final1/summary.json`：**70请求／494断言**、SHA `a00b62fceef7c95b865934a7d6a1a4dd4610787c9ab6eaf553069eeae93bba39`；显式环境测试lease2秒、源码默认300不改。同一Server四批共64个新client成功登记，16有效lease时四次quota429，过期真正空闲回收与首个旧access／resume401通过；Server SIGTERM exit0，凭证不落最终证据。

`launcher-checks-final1/summary.json` SHA `1ed38cf1ec63ebfaf743cd44d31085cae6de322a0c5b7345dc7fa21286335123`：真实同profile第二实例在ready前exit1、第一实例bootstrap保留；随后对仅idle自有Server精确PID发SIGKILL验证ready后的异常EOF，启动器及时exit1并清bootstrap。该异常测试只核launcher退出／清理，不称正常排水。正常排水由主HTTP两Server证明。

原默认HTTP `http-default-final1` 的89请求／541断言同样成功，仅进程采样未覆盖全部.NET线程children；保留旧轮历史，不与最新79／491合并。所有请求／响应已脱敏，凭证扫描数量各自记录在summary，最终统一SHA独立核对。G1受保护状态／可控clock竞争回归及solution结果由本批统一测试单列，不把HTTP断言合并成单元测试数。

### 31.4 统一回归与证据冻结

`artifacts/backend-g1-w0-0751/final1/summary.json`记录三commands实际exit0：正常solution构建0 warning／error、Hosting711／711、self-contained Server46／46，合计**757执行／757通过**，无skip／abort。1375个输入SHA前后无漂移；独立审查和root复核按原TRX与manifest核对，不把先前focused14或HTTP断言并入757。原1375项包含4个生成的`__pycache__/*.pyc`；提交前仅清理这4缓存，余1371项实际源与当前SHA逐一一致。补充分类证据为 `artifacts/backend-g1-w0-0751/final1/post-validation-cache-cleanup.json`，SHA `ae0a3c9b582eb5de1b63aac1a8d8f9e2c82d7649d01be233aa407da6de10d11c`；原before／after／summary保持逐字节不变，不重跑或改写757。

`handoff-evidence-final1.json` SHA `089d7ddb06e303bc9cad5191842517b5ada0013696cb0e386e31a6e77ef4407e`冻结324个最新HTTP／launcher证据文件SHA；重新验证806项部署SHA及708生产输入与当前源码一致，另记录启动工具SHA。测试、HTTP与部署分别归属，不冒充完整全solution测试或浏览器验收。

独立审查记录 `artifacts/backend-g1-w0-0751/independent-review-final1.json` SHA `09cce895cac8db18e7b5acc774c8ca54c160e1ff4b3d23eae6cca7aa9e6d4447`，复核324证据文件／806部署项／708生产输入及两TRX，保持上述后端有限范围与浏览器W0未验收边界。原token已销毁，独立审查仅核凭证字段结构脱敏；known-token逐字节扫描由原probe在持有token期间完成，不把结构扫描冒充再次逐字节校验。

正常供Web联调的常驻实例另用`artifacts/backend-g1-w0-0751/live-dev/profile`，由root按稳定入口启动；实际URL、PID与bootstrap路径从当次ready交付，不能写死为长期配置。该实例不并入上述两Server已关闭统计；关服向其launcher PID发SIGTERM并等待退出。

### 31.5 验证边界与接续

本批仅完成后端G1和Linux-x64开发启动前置；不新增公开编辑命令、波形route、包profile或完整资源闭包。外部Web `dev-backend.mjs`、bootstrap桥接、浏览器协议客户端与SSE／页面W0闭环继续独立。现有五命令与双静态profile保持；后续W1按命令＋读模型成对交付。此前B3-G1001、B3-F971、B3-E824、B4-I775与HTTP699／4239保持各自历史归属；完整B3／B4、性能、GUI、长片、人工平台原暂停范围不自动恢复。


<a id="web-s1-read-models-0760"></a>

## 32. S1：精确修订Web编辑读模型（0.76.0，2026-10-16）

用户已授权恢复后端编辑补齐，按[接续方案](../plans/backend-web-editing-plan.md)及审查S1→S2→S3推进。主要源、测试和HTTP工具由子代理实现，主代理审查、统一构建、合同冻结与交付；本批只完成S1，不提前记S2/S3或W1浏览器完成。产品0.76.0／合同1.1.0／HTTP协议1；schema28/16/6、SDK、依赖及公共静态profile保持。新增API详见[行为§6.4](../../architecture/backend-api.md#64-精确修订的编辑读模型0760)及ADR-0222。

### 32.1 实现与兼容

来源列表含primaryBranchId与完整元数据；歌词窗口保稳定来源/行/元素/注音身份、权威正文、全部时间状态及FollowPrimary关联；设置读取保有理fps、RGBA和字符串微秒。三个GET在同一Writer内验证expectedRevision，不能混修订；分页默认0/100，最多100行/来源，1MiB UTF-8 JSON及20000合计项硬预算，超限413且不截断。读取不改变历史/dirty/redo/事件。合法长来源ID可读取，不套用旧写命令128字符门禁。旧lyrics/lines、五命令及回执保持兼容。

### 32.2 统一验证与匹配发布

证据根 `artifacts/backend-web-s1-0760/`。`final/solution.log`原进程exit0、零警告错误；相关Hosting72/72、完整linux-x64 self-contained Server64/64，两个TRX共136/136，零失败/跳过/中止。Hosting范围为新读模型、原传输编辑、持久化、客户端回收与依赖；没有重跑全部Hosting/Worker/渲染或全量solution测试。元数据工具零警告错误；55路径/61方法/114schema、48正反例（29正/19反）及245份本批真实HTTP JSON响应通过，覆盖20种操作。

匹配部署 `deployment-final1/`，完整Server/Worker self-contained Release/linux-x64，806文件SHA全部通过；manifest SHA `aed5aa7c4578df3c1eb45135279baefbea3af19a9ac1ebe38a3977da41502779`。部署诚实记录执行HEAD `ee6270c`＋dirty生产输入，不冒充当时已有最终提交；712生产输入与验收结束当前源码SHA一致。`host.py run`参数/stdout/私有bootstrap合同无变化，前端切换版本须使用本目录或重新匹配发布完整两端。版本精确加载门禁沿用，不修改旧工程版本冒充兼容。

### 32.3 真正HTTP与独立核对

版本管理探针 `tools/backend-web-editing/probe.py`；最终 `http-final3/summary.json` SHA `bc470d547043c85083a7ad39383e7b6009d66fdd90c73bb1c991c956b0ea8c38`：245请求/1784断言，11个完整成功job终态（save2/open4/close5）。真实Worker短WAV准备、新来源发现/窗口/设置/非法分页/跨client中立拒绝、页间编辑与undo后旧修订409、旧端点兼容、完整保存关闭重开值/身份一致均通过。富fixture离线从自有关闭工程生成合法歌词hash闭包，用于HTTP读取全部注音字段/组合Unicode、六时间状态、零时间/缺失/非法原值、FollowPrimary及超JS安全整数；这不代表HTTP正文/注音/来源写入已实现。

跨行预算超限413后缩小窗口返回整行；单行超限与20000项fixture明确拒绝，拒绝前后summary/history/eventCursor一致；项数fixture可能同时超字节，项数独立门禁由Hosting回归验证。最终526文件（含summary/profile/日志）凭证扫描、启动器/Server退出0、16观察身份gone（含5Worker）；不宣称观察到所有短命进程。冻结1.3.0后3116/3116实际JSON通过，零skip/未配对；`http-final3-contract-validation.json` SHA `47b7c2a90457a626945101f771b965635a2cb3f2d1ea8026b4166eccbd79c9e1`，独立终审 `http-final3-final-audit.json` SHA `6cb8062c186fc61b9fb39e0a7325e81d4c0663b2b32b2b9e372909ad076c3dbf`。

审计 `http-final3-audit.json` SHA `663441007ce28a0039ebf6d8c55d2b41fcf335ddff924503a5c72d92d66001de`。

作者测试早期分析警告、fixture显示程序不一致及共享ASP.NET runtime缺失中止保留在 `artifacts/backend-s1-read-model/`，不并通过数字。统一server首命令误用了dotnet test不支持的--self-contained选项，日志与exit1保留；改为-p:SelfContained=true后完整64项通过。HTTP final1为探针将增强LRC行收窄至已定时元素外，领域422正确拒绝；final2为离线空/Missing的20000token制造20008诊断、超过既有32KiB账本终态预算而unknown，不算成功。仅把预算fixture改为合法有序Imported token后final3完整通过；未修改产品账本，不把延长等待当修复。原失败目录及原终态保持。

完整B3/B4、公共package/媒体字体、作者、性能/长片/全量/人工平台仍独立。Claude报告W0用户验收及前端W1开发，不等于本仓库重跑浏览器验收；后端接续G3/S2时间命令，再波形与S3编辑。


<a id="web-s2-timing-0770"></a>

## 33. G3／S2：严格分型的时间编辑命令（0.77.0，2026-10-16）

本批接续S1，完成G3及G10六时间动作，未交付波形G11或S3。合同1.2.0／产品0.77.0／HTTP协议1；格式28/16/6、SDK、依赖、公共静态profile保持。候选及实际行为见[接续方案§3](../plans/backend-web-editing-plan.md#3-g3--s2-时间候选合同)、[行为§6.5](../../architecture/backend-api.md#65-时间动作与严格命令分型0770)与ADR-0223。

### 33.1 命令与审查修复

原五JSON含无关已知nullable槽的显式null兼容；六新type分别限定字段，拒未知/跨type/重复字段，不用无约束字段袋。元素start/end打点、clear、按工程fps微调、整行shift、1..1000项元素时间批量通过原共享Application和唯一Writer执行。每次HTTP动作断自动合并、最多一次历史；no-op保redo，回执先于修订。打点沿原夹紧/一帧estimated/终点edited规则，返回真实timingResult；领域原因在稳定failureCode，field定位command输入，旧五错误wire不改。元素不自动扩行，batch按输入顺序验证中间candidate且保原无媒体上界。

主审确定性复现并修两界限：①原batch数组入队后可变导致指纹与执行不同；改为准入固定一次规范化请求供回执和执行共用，持Writer→改原数组回归修前失败/修后通过。②100个128字符Unicode ID的成功changeSet达到75235默认JSON字节，已编辑后回执超64KiB会变InternalError；完整changeSet超过16KiB时保areas、ID数组全空、requiresFullRefresh=true，小结果保精确IDs，成功/重放/undo-redo保持。不是截断ID清单。修前/后单例及37项作者回归保存在 `artifacts/backend-s2-command-snapshot/`、`backend-s2-change-set-budget/`，不并最终225项。

### 33.2 最终统一与匹配发布

证据根 `artifacts/backend-web-s2-0770/`。`final3/commands.json`三命令均原进程exit0，正常solution零警告错误；相关Hosting148/148、完整linux-x64 self-contained Server77/77，225/225且零失败/跳过/中止，786生产/相关测试输入前后SHA一致。Hosting范围为全部新时间/原传输/读模型/分析采用/持久化/回收/依赖，未跑全Hosting/Worker/渲染或全量solution tests。metadata真实继承反射、55路径61方法/124schema/72例（37正35反）通过；旧五与S1全部schema逐项相等、派生字段类型/未知字段漂移拒绝另留工具证据。

最终 `deployment-final2/` 两端完整Release/linux-x64 self-contained，806文件SHA通过；715生产输入与验收结束源码一致。manifest SHA `2d9ebdb9d4e936e9d2cc37e9dbe90ab139e0e0f6a87d7b9e72525dfcd9b8c569`，诚实记录HEAD `6c10486`＋dirty生产输入。`host.py run`参数/stdout/bootstrap合同不变；须此目录或重新匹配发布完整Server＋Worker，旧产品精确加载门禁保持。

### 33.3 真正HTTP与短样本

版本管理 `tools/backend-web-editing/timing_probe.py`，最终 `http-final1/summary.json` SHA `c1a83c0dc4dd85801bc12e050dd4c468beabd936841eb14e23d5f9d75c72b476`：2155请求/17720断言，19个完整成功终态（save4/open7/close8），六type逐个查询/undo-redo、状态与反馈、no-op保redo、派生载荷/时间规范化/截止时间的回执匹配与拒绝、真实同修订/同request并发、原子batch失败、普通1000 ASCII精确ID与Unicode完整刷新、FollowPrimary明确来源及保存关闭重开通过。合法长行fixture明确验证batch无媒体上界而单元素受真实2秒音频限制。

2155份实际HTTP JSON全通过合同，零skip/未配对；最终4371文件含summary/profile/日志凭证扫描，启动器/Server退出0、25观察身份gone（8Worker），不宣称抓到全部短命进程。审计 `http-final1-audit.json` SHA `edb108aef6db91fcc76ecac0d55a6348a0f46d7bb3c9c4919118b5d025d5593a`；合同报告 SHA `8c61dfb929280364309787c34bceedc0ca82f6f8e3188eb4d63489cc233da6cd`。

本机六代表HTTP短样本毫秒：start12.436、end1.987、clear2.311、nudge2.268、shift1.677、batch2项1.125。计时含客户端校验/日志，是单次短样本，不能推出5～8次/秒或浏览器体验/吞吐保证，不恢复暂停的性能基准。

隔离作者原Hosting32/33失败为STJ converter与Unmapped特性组合，已修后原scope通过；隔离全Server2/59为未build Worker apphost前置，补后59/59。主目录final/final2及deployment-final1是最终field路径调整前的中间轮次，原位保留；结论仅使用final3/deployment-final2与本HTTP。field修后最小Hosting/Server各1项另存 `artifacts/backend-s2-command-error-path/`，不并225。S1的136项/245HTTP保持原批归属。

Claude接受S1/S2候选并报告W0用户验收；前端整体同步本合同后须独立回报W1 HTTP/浏览器结果。S3a正文/注音/行/设置在隔离树接续；来源管理随后，G11波形、完整B3/B4、公共package/作者及原全量/性能/长片/平台矩阵仍独立。


<a id="web-s3a-content-0780"></a>
## 34. S3a：正文、注音、行操作与标量设置（0.78.0，2026-10-16）

接续S2，新增十一type，总计二十二命令；产品0.78.0／合同1.3.0／协议1。详细行为见[§6.6](../../architecture/backend-api.md#66-正文行操作与标量设置0780)、[候选§4](../plans/backend-web-editing-plan.md#4-s3a-正文行操作与标量设置候选合同)及ADR-0224。原格式28/16/6、SDK、固定依赖及公开双静态profile保持。

### 34.1 实现与审查边界

五歌词动作setLineText/setRubyText/addLine/duplicateLine/removeLine，以及六composition设置均调用原Application业务；一次HTTP动作一次历史，no-op保redo。正文只保留公共前后缀内完整元素，不能把任意相同中间文字当作身份保留；注音保原元数据/悬空规则，无独立发音时间。副本沿原行时长后移，可以超过音频并报告下一行重叠，不擅改领域规则。反馈只含真实有界选择建议、行身份、位置与统计，不返回臆造大映射。

共享Prepare→固定不可变candidate→CommitPrepared，提交核workspace/generation/source；桌面入口复用，不受HTTP预算。Hosting提交前按实际下一修订复用S1完整单行投影1MiB/20000项，含9→10字节边界；必要文本字节先验及新ID分配前callback避免无界新token，旧大合并元素不按grapheme数误拒。413保工程/历史/dirty/redo/修订/事件，删除旧超大行可修复。内容/结构失效四区域完整刷新，原S2的16KiB changeSet保护保持。

setOutputDuration的durationUs必填，null表示尚未确定；useLyricsEnd/useAudioDuration均显式动作。正时长导出成功后只清空时长，真实HTTP返回422 exportInputRejected且不建job。此对照不包含独立像素oracle，不能扩大成新渲染能力验收。

### 34.2 统一验证与匹配发布

证据根 `artifacts/backend-web-s3a-0780/`。`final/commands.json`五命令均exit0，正常solution零警告错误；Application105、相关Hosting170、完整linux-x64 self-contained Server88、相关App VM44，共407/407，零失败/跳过/中止。961生产/相关测试输入前后SHA一致。不是全solution tests、完整Hosting/渲染或GUI验收。

`deployment-final1/` 匹配Release self-contained Server＋完整Worker，718生产输入与806部署文件SHA核验，manifest SHA `f03de0ec961e581ac015bdc818b4b06691970961dd1aa87a03a43e973cfb624f`。manifest如实记录e558f7e＋dirty生产输入；启动器参数/stdout不变。metadata从本次源码编译导出，55路径/61方法/137schema/125例（58正67反），旧72例与十一命令schema保持。

### 34.3 真正HTTP及原失败保留

版本管理入口 `tools/backend-web-editing/content_probe.py`。最终 `http-final3/summary.json` SHA `2e834c216496b91b77b006fcb01165d61f5c7976bed6bf9e10dc901ccccb047e`：3116请求/23627断言；43成功终态（save7/open17/close18/export1）。覆盖十一type逐个读回/undo/redo、no-op保redo、所有派生字段与回执重试/复用拒绝、真实同修订/同回执并发、Unicode与注音完整性、选择反馈、FollowPrimary明确来源、严格错误/只读/跨client、单行和转义预算、旧大合并元素及删除超限修复、完整保存关闭重开。6365文件含最终summary凭证扫描；Server及launcher退出0，55观察身份gone，其中18Worker。不宣称观察到所有短命成员。

审计 `http-final3-audit.json` SHA `f7b4be6d0d8fdc6e4161a714fa4d2b99bafbcc7d7748d386c9942026cf87e6cf`。首轮探针误把两端改变后的相同中间文字视为保留，原共享业务正确重切；第二轮无音频fixture仍声明notAnalyzed，原加载正确拒绝。只修工具/自有fixture，原失败原位保留，未改产品迎合。短客户端耗时不作性能/吞吐保证。

Claude已回执：前端4f7083f整体同步e558f7e合同1.2.1，S1/S2合同接受、真实HTTP28项通过、S1浏览器读取通过；S2六动作尚未接界面，浏览器不记完成。S3由后端先交付供前端后续W2消费，来源/元素/再次导入、G11波形、完整B3/B4/作者以及原全量/性能/GUI/长片/平台范围仍独立。


<a id="web-s3b-branches-0790"></a>
## 35. S3b：来源管理（0.79.0，2026-10-16）

新增addTranslationBranch/configureBranch/removeBranch/linkPrimaryLine四type与可选branchResult，总二十六命令；产品0.79.0／合同1.4.0／协议1，原格式28/16/6、SDK、依赖及静态profile保持。实际字段、历史、预算及错误见[行为§6.7](../../architecture/backend-api.md#67-来源管理0790)、候选§5与ADR-0225。

共享原来源用例改为纯Prepare＋唯一CommitPrepared；新ID只分配一次，提交核workspace/gen/source。HTTP沿原Primary角色/身份保护、名字Trim与保原名的区别、原语言、关联、StyleOverride及Presentation规则；不额外禁止主来源合法TimingMode。原候选曾写“主来源不能改时间策略”过严，编码未引入该禁令，8f19c63已核正文案并告Claude。关联不把主行时间写成独立时间，原同值悬空关联no-op保持。

提交前按实际下一修订验证完整单来源/受影响单行S1投影；选区反馈再验完整配置回执预算。删除旧超大来源可修复，不能在已提交后才遇到64KiB回执失败。新来源type的合法ID不套旧时间128字符上限，旧type保持。Add/Remove真变化给实际新来源/主来源选择，其他/失败/no-op省略；结构变化完整project刷新。

### 35.1 统一验证与发布

证据根 `artifacts/backend-web-s3b-0790/`：正常solution零警告错误、原进程exit0；最终Application64＋Hosting181＋完整linux-x64 self-contained Server100＝345/345，零失败/跳过/中止，1289源码/测试输入前后SHA不变。Application初次filter误写新Prepare测试类名只跑56，原TRX保留；纠正filter后64独立TRX是最终口径，不相加。并未执行完整Hosting/solution tests或渲染/GUI。

匹配 `deployment-final1/` 核722生产输入与806文件，manifest SHA `c912817a0e76492511ad318629c1fa067322273c57425ce5fcf12b1c3854d9a4`，记录8f19c63＋dirty生产输入；启动参数/stdout不变。主审 `main-source-deployment-review.json` 固定共享规则、前置预算、唯一提交与完整输入/部署SHA。真实编译metadata、55路径/61方法/142schema/163例（68正95反）通过，原125例及二十二命令schema保持。

### 35.2 真正HTTP

版本管理 `tools/backend-web-editing/branch_probe.py`，首轮 `http-final1/` 原进程exit0，全通过：3081请求/25117断言，40完整成功终态（save7/open16/close17），6285文件含最终summary凭证扫描。Server与launcher退出0、52观察身份gone，其中17Worker，不宣称抓到全部短命进程。

最终summary SHA `b74e693364250ec152192cd36a2219532950a50e4b0e14bab058f374431784f5`；3081/3081实际JSON合同通过、零skip/未配对，报告SHA `da3575443a03077d31cc60f52ccd1b3c4cf841aaa9455f9f54af4050213b1df6`，独立审计SHA `82b25da3fc835035a93e614d3c4246d3f147eca95ef209341a15196a67962ca1`。

四type逐个查询/undo/redo、完整回执及真实同修订/同回执并发、strict类型/显式nullable/错误/只读/跨client、FollowPrimary关联和三时间策略、空展示主来源三策略、完整样式/展示保存闭包与保存关闭重开均通过。长ID、单来源600KiB×2可提交但组合页413（按limit=1分页）、删除旧超大来源修复、巨大主来源选择回执及关联单行413均真实验证。预算拒绝全状态与redo/游标保持，没有产品修复或重跑覆盖原结果。

元素结构与再次导入仍待独立交付；G11波形、前端W2、完整B3/B4/作者及原全量/性能/长片/GUI/平台暂停范围保持。


<a id="web-s3c-tokens-0800"></a>
## 36. S3c：元素拆合、重排与重切（0.80.0，2026-10-16）

新增splitToken/mergeToken/reorderToken/resplitLine/resplitBranch五type与可选tokenResult，共三十一命令；产品0.80.0／合同1.5.0／协议1。字段、历史、真实统计/选择、时间和ID语义见[行为§6.8](../../architecture/backend-api.md#68-元素拆合重排与重切0800)、候选§5.1与ADR-0226；原格式28/16/6、SDK、依赖及公开静态profile保持。

### 36.1 实现与主审

原Application四元素API及重排复用纯Prepare/CommitPrepared，固定candidate/ID、workspace/gen/source，宿主预检后最多一次历史，no-op保redo。单元素拆分按grapheme中点及Int128 ties-to-even，规则/手工比例估算按原UTF-16权重，两者不混同。merge/reorder省略切分统计；选择与计数只来自真实结果，不生成巨大映射。原样式、未定时状态和悬空注音规则保持。

Core仅一个拆分规则算法，增加流式Unicode枚举和真实新段/新ID前观察器；手工段以原文span与流式边界校验。固定旧0.78程序集SHA `615456e57b21f47b764632ffa1697e9781b023dfb9706cb3d71739590db06301` 与新程序集独立ALC反射，种子20261016、1237输入×六rule＝7422片段结果精确相等（含空白、标点、emoji、组合字与非法surrogate）；独立oracle位于 `artifacts/worktrees/backend-s3-tokens/artifacts/token-tests/core-oracle.json`，不并后端HTTP计数。

手工segments超过20000在字符串数组分配前413；最终单行按真实下一修订复用S1 1MiB/20000项，整个来源累计改变行投影16MiB，任何拒绝不提交前半来源/不回滚历史。大旧合并元素可合法重切小结果，删除旧超大行可修复；既有oldTokens映射仍按原旧行规模分配，不宣称所有旧数据heap被封顶。segments准入复制一次，回执和排队执行共用。

主审发现stale目标可遮住缺必填bool/null segments导致422，增加converter前置形状校验并补15条Server回归；仍保持20001非string项优先413、20000内错型400。四条旧converter往返fixture本为测试派生字段，补target:{}使其继续测往返；原73通过/4失败和修后77保留 `artifacts/token-shape-fix`，未更改原断言目的。该修复及源码review/合同delta独立留档，不重写作者398项证据。

### 36.2 统一验证与发布

证据根 `artifacts/backend-web-s3c-0800/`。`final/commands.json`六命令全部exit0：正常solution零警告错误；Core43＋Application131＋相关Hosting199＋完整linux-x64 self-contained Server125＋App VM20＝518/518，零失败/跳过/中止。1297源码/测试输入前后SHA相同；不是全solution tests、完整Hosting/Worker/渲染或GUI验收。

匹配 `deployment-final1/` 核726生产输入/806部署文件，manifest SHA `6792bc729be9a3dba5370616bdd0b7f8a140f5939429b10a1885cba353f6830e`，如实记录3554a98＋dirty生产输入；launcher参数/stdout不变。当前编译metadata、55路径/61方法/152schema/210例（85正125反）均通过；原163例和140未扩schema保持，仅命令union/response按新type和可选tokenResult扩展。

### 36.3 真正HTTP与前端边界

`tools/backend-web-editing/token_probe.py` 首轮 `http-final1/` 原进程exit0：3367请求/26552断言，53成功终态（save10/open21/close22），6891文件含最终summary凭证扫描。Server和launcher退出0，67观察身份gone，其中22Worker，不宣称抓到全部短命进程。3367/3367实际JSON合同通过，零skip/未配对。

summary SHA `470f8f04a0a77f83b9bc29a601f0dbcdc8dfc1af00d0b8bd81e04b7c01d706e7`；合同报告SHA `a70b2af287a735ef9575cb3015bdc90421a7c0f550d3b8f44e656172175799c0`；独立审计SHA `343b375bcc77e04eeece4ec1a429b8bb0ab828fc1a94646081a5876d2627d5e8`。覆盖五type完整前后/undo-redo、no-op/TokenResult、Unicode与UTF16、六rule、预算/形状/领域拒绝、回执/并发、只读/跨client、FollowPrimary/样式闭包和保存关闭重开；无工具修补重跑。短样本含日志/校验，不作持续性能保证。

Claude回执b6d65f0：S2六动作真实Chrome21/21通过，cc3cfca静态预览面板24/24；仅按前端报告记，不替代本仓库独立运行。W1仍固定1.2.1/0.77；S3a/b虽已交付，前端合同接受/真实HTTP/浏览器均未开始，S3c同样等待前端W2决定。再次导入/追加与G11波形继续独立；完整B3/B4/作者及原全量/性能/长片/GUI/平台矩阵不自动恢复。


<a id="first-audio-stream-0801"></a>
## 37. self-contained准备的首音轨一致性修复（0.80.1，2026-10-16）

G11前置独立PATCH；不新增route/DTO/profile、PCM或波形资源，产品0.80.1／合同1.5.1／协议1，格式28/16/6、SDK及固定依赖保持。原prepare probe选a:0，self-contained decode却排除了显式map；双轨MOV首轨8k单声道、默认次轨48k双声道会用8k Info配48000样本，1秒误算6秒。实际Worker修前回归2总1过1失败和旧0.79发布真实HTTP88请求579断言（明确expected6s，仅缺陷特征）独立保留于 `artifacts/worktrees/backend-audio-first-stream/artifacts/first-stream/`，不作为修复通过数。

产品只将forcedInputFormat分支的-map0:a:0覆盖selfContained；不改-ar/strict/协议/输入预算，普通Media默认argv不变。修后真实Worker与direct受限loader分别对独立首轨PCM oracle核Info全部字段、8000完整样本/1秒、全部pyramid层和min/max float bits；单轨控制保持。独立作者2＋Media22及隔离HTTP94/609另列，不并主目录。

### 37.1 主目录统一与发布

证据根 `artifacts/backend-audio-first-stream-0801/`，原四命令全部exit0：正常solution零警告错误；Media22＋相关Hosting11（含真实Worker首轨2与持久化9）＋完整linux-x64 self-contained Server125＝158/158，零失败/跳过/中止；1298源码/测试输入前后SHA相同。没有重新跑S3c全部518项或全量/渲染。

匹配 `deployment-final1/` 核726生产输入/806部署文件，manifest SHA `6c0b9f6579c66cc78d36f093303aff84e48070e0e7da932105923c59ff525f63`，如实记录6b39a3f＋dirty生产输入；launcher参数/stdout不变，完整Server＋Worker一起重发，旧工程精确产品门禁保持。合同保持55路径/61方法/152schema/210例，不改wire，仅固定新实际选流行为。

### 37.2 真正HTTP

版本管理 `tools/backend-first-stream-http-probe/probe.py`，主目录首轮 `http-final1/` 原进程exit0：93请求604断言，8成功终态（save2/open2/close4）；双轨与单轨创建/设置/保存关闭重开均为1000000µs。212文件含最终summary凭证扫描，Server随launcher正常退出0，13观察身份gone（4Worker），不宣称抓到全部短命进程。93/93实际JSON合同通过、零skip/未配对。

summary SHA `aa9ed26112f5adf3c028247a8cd96114ed70f86f2d90ec8aba0166f5a2bb25dd`；合同报告SHA `9e0b9c7b92a99a65ff303f37ca79958be761fd4b061107bcd5d307b1aab1504c`；独立审计SHA `b1e41cf1be577e38147ef9abb5efe7378b55eeffb37e99f34dfe57c032293904`。HTTP只验证时长/身份/持久化，完整floatbits/pyramid等价属于Worker回归，不能混成波形HTTP已开放。G11缓存/窗口与浏览器压缩音频时间基仍独立。


<a id="web-s3d-import-0810"></a>
## 38. S3d：再次导入与粘贴追加（0.81.0，2026-10-16）

新增importAsset/appendText、严格目标union、可选importResult及完整27字段报告，共三十三命令；产品0.81.0／合同1.6.0／协议1。行为见[§6.9](../../architecture/backend-api.md#69-再次导入与粘贴追加0810)、候选§5.2及ADR-0228。格式28/16/6、SDK、依赖和静态profile保持。文件existing完整替换/新来源Independent，粘贴保空白/Unicode/新翻译FollowPrimary；Error报告与合法行可同提交。新意图新ID一次原历史，旧回执不重读/重解析/重分配，成功完整project刷新。

### 38.1 事务、预算及审查

原receipt owner内捕获固定快照→Host2/client1无队列名额→授权固定bytes/hash/size与所有lease清理→Writer外纯prepare→最终Writer核修订/close/readOnly/source及Application workspace/gen→唯一提交。Hosting确定性回归验证并发改设置409、关闭不复活、等待方取消不撤owner/同回执固定ID/停机排水、关工程后过期client仍受owner pin、读与cleanup双失败保primary/OCE；HTTP普通竞态不代替内部时序证据。

64MiB累计逻辑工作（不是堆硬帽/OS sandbox）在集合/字符串/旧新ID前计费；每最终行固定ID后、builder.Add前立即验真实下一修订S1单行1MiB/20000项及累计16MiB。完整来源元数据和完整报告回执按实际64KiB配置预检；后行失败不提交前半批，追加不扫旧未触ID的128地址性门禁。主审补齐上述早停/旧ID集合、继承base槽指纹及真实旧public/protected List签名。桌面默认不套HTTP预算。

LRC明确修复EncodingOverride被忽略：GB18030自动保中文，显式UTF8沿原replacement解码（plain3替代字、enhanced插标签4）单列回归。默认八格式×两编码自动×三换行×六rule×估算两值＝576组旧/新独立runtime完整文档/报告投影全等；只排除会throw的计算DurationUs，存储Time/Status仍对比。额外旧consumer换DLL因0.79→隔离0.78程序集身份拒绝、未到API，不记ABI运行通过；旧签名反射通过。

### 38.2 最终统一与发布

证据根 `artifacts/backend-web-s3d-0810/`：final2六命令全部exit0，正常solution零警告错误；Core140＋Application56＋Hosting222＋完整linux-x64 self-contained Server141＋App VM43＝602/602，零失败/跳过/中止，1309输入前后SHA不变。原final594保留；最后Guard.RemoveRange逆序避免新增二次方搬移、补allocator8后重跑final2，不累加。作者200、增量Core109及oracle独立；不是全solution tests/渲染/GUI。

匹配deployment-final1核733生产输入/806文件，manifest SHA `bf7f905d11db5bc4da1a81064043f1205f8870279fa97a723d43dd2cade5cadf`，如实记录467efb9＋dirty输入。launcher参数/stdout不变、完整Server＋Worker一起重发。当前metadata、55路径/61方法/168schema/301例（106正195反）通过；原210例及150未扩schema保持。源码/合同delta审查见main-review.json。

### 38.3 真正HTTP

版本管理import_probe.py，最终http-final2原进程exit0：5089请求45600断言、16成功终态（save3/open6/close7），25原始上传SHA不漂移，10284文件含最终summary凭证扫描。Server/launcher退出0，22观察身份gone（7Worker）；不宣称抓到全部短命进程。5089/5089 JSON合同通过，零skip/未配对。

八格式完整report逐值oracle、Error+合法TTML200、LRC编码修复、Unicode/严格union/options、全派生回执/新意图ID/同回执双200、同修订本轮200+429、权限/同大小源SHA损坏422、全部四层预算413、目标128/129/原未触ID129及保存关闭重开通过。17×80000CJK单token行合法正对照、18行累计413真实验证。HTTP不证明内部checkpoint/名额完全穷尽或分配性能。

summary SHA `5fd2c911a447fa7c2e7e7e99162597d515266a8eb8b9fea8e9b7888147a18758`；审计SHA `b9bfbd79f3a22e780e779d21e974ce4d6f1185b7529b379d8d512e7a823334ce`；合同报告SHA `56e0457bb189f003714deb85ec8bfffa21a0e8338149b84475cd9b97dd8fa0e7`。首轮工具重复手写plain无歧义预期与已通过完整oracle冲突，只删错误重复预期，失败/cleanup原位保留，无产品迁就。

计划S1/S2时间/S3有限编辑出口已完成，G11继续独立；S3前端合同接受/HTTP/浏览器待W2决定。完整样式/场景/公共package/作者、完整B3/B4与原全量/性能/长片/GUI/平台范围不自动恢复。


<a id="web-g11-waveform-0820"></a>
## 39. G11：同源有界波形读取（0.82.0，2026-10-16）

新增GET waveform元数据与GET/HEAD peaks窗口，两路径三方法；产品0.82.0／合同1.7.0／协议1。行为见[§6.10](../../architecture/backend-api.md#610-有界同源波形读取0820)、实施候选§6.1及ADR-0229；三十三编辑命令、格式28/16/6、SDK、依赖和静态渲染profile保持。

### 39.1 归属与预算

只保原create/open/recover一次准备结果；固定源校验后Bind，open资源lease先转candidate再做易失败绑定，注册失败/关闭/Dispose释放。完整源SHA/profile/采样信息/首轨mono codec身份/全部层floatbits去重，64MiB仅计实际flat峰值payload；不持LoadedWaveform/Peaks旁路，不当总heap封顶。满额先命中同身份可增引用；不足时原创建/打开/恢复仍成功，元数据cacheBudgetExceeded，不重decode；另noAudio/audioIdentityChanged明确不可用且省payload。legacy空AudioMedia保持，当前声明存在则核hash/size及固定asset/path。

读取按session授权，知道hash不能越权。Writer内核身份和完整坐标，窗口count1..8192、最多65536B独立数组，关闭后在途窗口不持全缓存。metadata无revision/无源IO/native/Worker握手，歌词编辑后身份不变；HEAD所有状态无body，认证先于ETag/304，Range明确400，无裁剪伪完整。源码主审及独立只读审均未见本范围可复现违规，具体责任见main-review.json及源码。

### 39.2 统一验证与匹配发布

证据根 `artifacts/backend-web-g11-0820/`，final三命令全exit0：正常solution零警告错误，相关Hosting238＋完整linux-x64 self-contained Server144＝382/382，零失败/跳过/中止；1315源码/测试输入前后SHA不变。Host受控低预算覆盖去重、预算不阻create/open/recover、候选失败/close/Dispose释放、inflight独立窗口和声明变更；不谎称已HTTP耗尽实际64MiB。未重复S3d的602整组范围（Core/App未重跑）或全solution tests/GUI。

匹配deployment-final1核737生产输入/806文件，manifest SHA `a604b73acd76ac788d44f755cda14fd7cf5b90a158694e2e416ad9f0f83c27a3`，记录f5df4b3＋dirty实际输入；launcher参数/stdout不变，完整Server＋Worker一起重发。当前metadata、57路径/64方法/172schema/350例（112正238反）通过，原168schema/301例逐项不变。

### 39.3 正式HTTP与完整字节终审

版本管理 `tools/backend-waveform-http-probe/probe.py`，正式http-final1原进程exit0：340请求2766断言，86次完整层比较，16成功终态（save4/open4/close8）。WAV/MP3/FLAC/双轨MOV真实Worker；独立审计再次-map0:a:0解码并纯Python重算43层，全部bits/SHA等原oracle及HTTP。长WAV8376基础桶覆盖8192最大窗和9sample尾；MP3/FLAC32019@16k的2001187.5µs按ties-even得2001188；双轨MOV只用首8k mono轨8000样本，默认48k stereo次轨不误选。

239 JSON全过合同；另外101无JSON逐项独立核验：91 binary GET200、HEAD200一项、GET304四项、HEAD304四项、HEAD400一项，坐标/ETag/length/MIME/bodySHA与oracle slice全部一致，无未知/未配对响应。保存重开后完整层/身份、歌词编辑不变ID、认证先于304、跨client、strict query/Range/HEAD及关闭404通过。两自建WAV全部原样本、五媒体输入SHA、四保存工程歌词寻址/媒体闭包逐bytes核验。

863证据文件SHA闭合为原manifest861项＋其自身＋summary，每项都有凭证扫描标签；Server/launcher正常退出0、25观察身份gone（8Worker）、bootstrap删除，不宣称抓到全部短命进程。summary SHA `7901ad3e1c376e74d9824b34cdcf1bab61299765e4337d6934d2a16e0982135a`；合同报告SHA `c72c70b14401419f5681ed5d1c94f67faaa89d3efc17be82bef9b6bf18c76e13`；审计SHA `7768f490770f8c463cd8936f3b2f331adfae9ef897f021b1fa630c37d035d92a`。与作者11源中两共享文件的S3d分派/capability广告差异已逐项记录，其余9源及工具完全同，root本轮无失败重试。

作者0.80基线25/128/HTTP346另保隔离树；首轮工具微秒误向下取整及缺Debug Worker前置失败原位保留，不并正式结果。不承诺浏览器压缩播放逐样本一致、总heap/性能/人工平台；完整B3/B4、样式/场景/公共package/作者与原暂停矩阵独立。当前计划S1/S2时间/S3编辑与G11有限后端出口完成，前端S3/G11是否接受/HTTP/浏览器仍待Claude用户侧W2安排。


<a id="web-static-style-0830"></a>

## 40. 四层canonical静态样式底层API（0.83.0，2026-10-16）

用户先确认现有前端编辑、撤销／重做和后端接口无误，再在得知Claude的模板先行原则与直接目标写入差别后，明确选择“作为底层能力一并交付，再补模板库与分配”。本批不要求前端在歌词页放颜色／字体入口；后端保直接能力，模板作者与分配接续。候选9570429及[方案](../plans/backend-static-style-api-plan.md)、ADR-0230和[行为§6.11](../../architecture/backend-api.md#611-四层静态样式读写0830)给出精确边界。产品0.83.0／合同1.8.0／协议1，格式28/16/6、SDK、依赖与公共双静态profile不变。

### 40.1 实现与审查

新增共享Application四生产文件，24个typed canonical属性、四层稳定目标、纯Prepare／预算observer／CommitPrepared；直接按真实branch/line/token更新，不持久化临时primary投影，不调用内容编辑的RemoveDeletedSources。旧Core及App生产未改。新增GET style、styles.patch／restore，strict target/property/material union、显式null和内容相等、真实changedTargetCount、staticHierarchy与包只读身份、完整前置预算均接通；不扩公共包执行，不把静态继承称实际帧最终外观。未选场景、预设应用、材质参数及音频身份完整保留。

单writer内固定递归payload，复用原client级回执；完整新GET单项1MiB/20000及累计16MiB、新值展开16MiB、意图64KiB和真实配置完整回执先于唯一历史。new font256 UTF-16/fallback32/stops256只限新值；旧长ID不新增128限制，旧大覆盖可no-op／恢复。changeSet大于16KiB清空全部ID并完整刷新，不截断。主审及两独立只读审覆盖DTO→domain、null／package、预算／close／只读／提交与深快照。审中实际复现进程内NaN/±Inf序列化Taskfault及旧非法基线GET异常，style专用最小映射后分别400／422，缺目标才404；非法基线修复仍按原规则明确拒绝，不新增自动修数据。

### 40.2 统一验证与匹配发布

证据根 `artifacts/backend-static-style-0830/`。final五commands均实际exit0：正常solution零警告错误；Core148＋Application147＋Hosting267＋完整linux-x64 self-contained Server188＝750/750，零失败/跳过/中止；1329源码/测试输入前后SHA一致。作者Application108/Core52、Hosting136/Server188分别归隔离证据，不累加。旧Author fixture/缺Worker前置/真实boundary4fail及修后回归原位保留。

匹配 `deployment-final1/` 核746生产输入、806部署文件全SHA；manifest SHA `33c6d54657d68fb12e10c21171523b253fbd56a332d5bc956f3a230e6c443749`，如实记录9570429＋dirty实际输入。完整Server＋Worker须一起更新，launcher参数/stdout/bootstrap合同不变。当前编译metadata、58路径/65方法/264schema/35命令、756例（236正/520反）通过；原172仅命令union及Response可选styleResult增量，170旧schema、33旧命令与350例逐项不变。manifest另固定Application样式与canonical Core真值，不把hash当语义证明。

### 40.3 正式HTTP与独立终审

最终版本管理工具 `tools/backend-style-http-probe/probe.py` SHA `86961ea75c7b210a5642701ad5746abd0a1e5144833a47b415dd1f88a5720c9d`。正式http-final4原进程exit0，38779请求/414328断言、17896成功样式读取，全部四层、24属性、secondary/129字符/前后空格ID、父子继承、null关闭与恢复、等值redo、严格错误、回执/并发、只读/跨client及预算完整通过。1000个实际合法来源小patch成功；600目标隔离新值展开16MiB，400 Unicode目标独立检完整changeSet清ID。旧数据新值限额与读预算分别验证，具体HTTP／Hosting归属见下段。

38776 JSON全部合同通过，3 PNG分别完整SHA/解码，纯色编辑换入精确新修订后像素改变、undo换入后逐像素恢复；不称独立Runtime内容oracle、预设/node/clip最终上下文或浏览器验收。旧300字体/33回退/300色标完整读取属于Hosting受控回归，HTTP另核旧257色标及其他超旧读预算修复，不混计。47成功终态为save10/open18/close19；19持久工程歌词／媒体闭包、完整包5资源与高精度参数独立核验。77724证据文件SHA闭合、77674所有JSON可解析，成功原内存exact-secret扫描包含最终summary；自有launcher/server正常exit0，61观察身份全部gone，其中22 Worker，bootstrap删除。不外推全部短命进程均被观察。

summary SHA `27fe66ccd16d5405c489f3fcf2beb11377eb9b094d385439f1d970bd850124c2`；合同报告SHA `26c8b8b71ce33d26f510831278be9108220d5bd42e843a2ffe6d53faaca6cfe9`；独立终审SHA `11018eb09e07f93697eeab0c0d4ef66604458936c87f1c4f62899f5d75f05ce4`；闭合清单SHA `3048b17a32295052b1a23af613f0121a06f424926553dc744609736c1bcde8ef`，不宣称清单自哈希。

原final1/2/3分别11909/14913/14887部分响应均exit1、原位保留不并成功。第一轮工具误期望只读错误field存在；后两轮bulk fixture先空来源、后Imported时间制造1000条无关诊断，触原任务终态账本预算而unknown，未改产品；最终每来源有合法Confirmed行/元素，保1000目标及所有预算断言。三轮cleanup exit0及13/37/37观察身份gone；失败未到原内存final扫描，事后仅做完整配对/JSON和结构token/Bearer检查，如实不冒充exact-secret终审。独立审计脚本原O(n²)检查中断、改set索引及补漏resolution字段的过程失败均单列，不改正式HTTP内容。

本批已交付有限静态样式底层编辑；用户已授权接续模板库／显式分配。前端此次新增能力、完整场景／作者、公共package、完整B3/B4及原全量/性能/长片/GUI/平台矩阵仍独立。

<a id="web-template-library-0840"></a>

## 41. 独立模板库（0.84.0，合同1.9.0，2026-10-16）

用户确认继续后端功能，并按[模板库与显式分配接续方案](../plans/backend-template-library-assignment-plan.md)先交独立模板库，再评审工程内显式分配。模板库为 profile 级个人资源，不绑定工程、播放头或工程历史；内置库只读，个人库支持完整静态定义的 create／replace，以及静态和高级对象的 copy／rename／delete。高级对象的参数、配方、包和未选材质保持完整，不能降级成静态定义；模板库操作不改变工程 revision、dirty、undo／redo或事件。静态样式底层仍按§40保留，C2前奏外观走分配，C3水平对齐／垂直中心留在编排，均未混入本批。

### 41.1 实现与合同

新增 Contracts 严格模板命令 union、完整静态定义与高级能力摘要，Application 纯 Read／Prepare／Commit CAS 服务，Hosting profile 个人库读模型、回执／预算／持久化／关闭排水，Server 四条HTTP路由，以及Core统一模板身份编码。修复响应预算流递归栈溢出、超长名称错误映射和Server启动遗漏 `ConfigureTemplateLibrary()`；取消竞态使用固定快照并覆盖重放／关闭排水。个人索引修订为真实canonical入口的小写64位SHA-256，缺失为 `missing`；坏库／版本／资源闭包不当空库覆盖。新command canonical JSON 256KiB、名称／字体256 UTF-16、回退32、渐变256色标；读响应1MiB／20000值和完整回执预算先于发布。

合同工具从当前metadata重新生成并通过：**62 paths／69 methods／291 schemas／1011 examples（319正／692反）**，其中0.83的旧264 schema／756例逐项保留（旧工程命令union及response的已审增量除外）。`web/openapi.json`、`web/api-index.md`、`web/examples.json`和`web/contract-manifest.json`与工具输出一致；离线 `validate.py` exit0，未把离线形状检查当作运行验收。

### 41.2 回归、匹配发布与真实HTTP

定向回归最终为 Core 模板库 **9/9**、Application **14/14**、Hosting **9/9**；Server Release 构建 **0警告／0错误**。普通 Server 测试因本机没有共享 `Microsoft.AspNetCore.App 10.0` 运行时不能启动，未计入通过数；self-contained 发布绕过该环境限制。

匹配发布目录为 `artifacts/backend-template-library-0840/deployment-final6/`，linux-x64 self-contained Server＋完整Worker，版本0.84.0，部署manifest SHA-256 `e68ac0146c89c07f44b0fc1db3c3934ebb108902a3f064e99a112bcf4c9685c6`。真实探针 `tools/backend-template-library-http-probe/probe.py` SHA-256 `506da645ec7eff6788d50d24e512c153cd27327783efd168ec5e37cff6ba901c`，证据目录 `artifacts/backend-template-library-0840/http-final6/`；进程正常退出并清理自有bootstrap。

最终真实HTTP **9442请求／115212断言**通过，覆盖发现、内置只读、个人共享、完整静态读写、五种命令、严格JSON/query、Origin／权限、CAS竞态、同request回执重放、取消／排水、预算边界、重启持久化、坏库保护，以及高级模板 copy／rename／delete 后的完整闭包保留。探针在验收中发现并修复超长名称被映射为400的问题，最终名称边界返回413；富样式夹具按当前Core支持矩阵使用line/canvas坐标，不宣称未接入的glyph/progress/Oklch能力。

### 41.3 边界与接续

本批不交付工程显式分配、预设应用、静态预设Worker渲染主工作区集成、模板试样、作者包资源HTTP、公共package执行、完整B3/B4、前端浏览器W2或原暂停的全量／性能／GUI／平台矩阵。静态预设渲染候选在独立worktree已完成定向验证但尚未集成；下一批须单独完成分配合同、undo／redo、库更新不自动生效及匹配Worker实际像素／恢复验收。

<a id="web-preset-assignment-0850"></a>

## 42. 工程显式预设分配（0.85.0，合同1.10.0，2026-10-16）

用户确认模板库有限出口后继续补工程内显式预设分配。本批新增四种严格工程命令：`presetApplications.apply`、`presetApplications.update`、`presetApplications.setEnabled`、`presetApplications.remove`。四种命令接入 Server capabilities、严格 `type` converter、Hosting receipt owner 与 ProjectSession 唯一 Writer，沿用精确 `expectedRevision`、一次HTTP一次工程历史、事件、undo／redo及 no-op 保 redo。

### 42.1 快照、目标与提交边界

`apply`／`update`必须提供`templateLibraryId + templateId + templateRevision`。builtin／personal模板在工程 Writer 外由 profile catalog 读取并固定完整静态 `StyleTemplate` 快照；随后 Writer 内执行 Prepare→Commit，提交阶段复核工程修订。库之后改名、替换或删除不会改变已经写入工程的模板快照。`apply`目标严格为四类 union：project（无ID）、branch（branchId）、line（branchId/lineId）、token（branchId/lineId/tokenId）；1..1000个目标不得重复。`branchInputs`为可省略或null的字符串映射，不能扩大静态模板能力为高级作者参数。

`update`按 applicationId更新同一应用，保留应用身份、实例身份／种子和 enabled 状态；不同模板身份须先 remove 后 apply。`setEnabled`只改变一个应用的启用状态；`remove`接受1..1000个应用身份，失效旧应用也可直接移除。模板读取失败、目标／领域校验失败及提交冲突均整批拒绝，不提交部分变化；失败不改变工程修订、dirty、redo或事件。

### 42.2 代码、合同与回归

代码提交 `0803e3f` 接通四类 DTO／converter、ProjectSession 分配分支、ProjectSessionRegistry 模板捕获与关闭排水，以及四项 capability。合同工具新增严格目标 union、Dictionary schema、四个命令分支和 28 个增量例子；合同为1.10.0，产品为0.85.0。文档同步本节、ADR-0232、统一API§13.3、`web/README.md`及模板库接续方案。

定向回归：Application 分配用例 **5/5**，Hosting 分配用例 **3/3**，Backend.Rendering 预设相关 **58/58**；Backend.Worker 预设测试筛选未匹配测试名，不能计入通过数。Server Release 构建已完成，0警告／0错误；安装共享 ASP.NET Core 10 runtime 后，Server 测试完整运行 **247/247**（0 failed／skipped），TRX 为 `artifacts/backend-preset-assignment-0850/server-tests-after-runtime-final/server-tests-after-runtime-final.trx`。

### 42.3 匹配部署的真实HTTP有限验收

已用匹配 self-contained Server＋完整 Worker 发布 `artifacts/backend-preset-assignment-0850/deployment-final1/`（版本0.85.0，deployment manifest SHA-256 `ccf966df9e29bcea813747e75458324429b349ea73e8545a89044a97a761cbdd`）运行 `tools/backend-preset-assignment-http-probe/probe.py`（探针 SHA-256 `1600fef49f2d1e6b70f1cec1fae2ec79df6f3dee8d37c036efd166355830bbac`）。证据根为 `artifacts/backend-preset-assignment-0850/http-final8/`，通过 **167 个 HTTP 请求／991 条断言**；实际观察到的7个 Worker 身份在排水后全部消失，bootstrap／access／resume 凭证扫描通过。

这次有限验收覆盖：builtin 与 personal 静态模板读取；四种工程命令；一次历史、undo／redo、no-op 保 redo；同一应用身份更新；个人库替换不改已保存的工程快照；移除、保存、关闭、重开及空应用列表持久化；普通预览基线 PNG 的完整传输／SHA／解码和移除后像素恢复；带预设快照的 validation 与纯视频 export 实际 Worker 终态。两个任务的 `job.input.sourceProfile` 均为 `static-lyrics-scene-presets-v1`；validation 报告为1个 opaque＋1个 transparent 帧，export 报告严格帧计数通过，媒体下载 SHA／大小及独立 ffprobe 帧数一致。

公共 `preview-sessions` 对含 preset application 的来源按既有边界返回 `422 commandNotSupported`（field=`source`），探针将此作为明确拒绝证据；因此没有把公共预览拒绝误记为预览像素闭环。重开会产生新的会话修订 `0`，但保存的完整 `presetApplications` 快照和空列表结果均已核对。该批仍不代记公共预览对预设的最终外观、独立 Runtime 像素 oracle、Server 重启恢复／跨 client 矩阵、前端W2、完整作者／公共package、完整B3/B4、性能、长片、GUI或平台矩阵。

<a id="web-static-preset-preview-0860"></a>

## 43. 公共静态预设预览（0.86.0，合同1.11.0，2026-10-16）

本批把已完成的模板库与工程显式分配接到公共 `preview-sessions`：含非空 `PresetApplications` 的完整静态工程由 `StaticProjectSnapshotCodec` 选择 `static-lyrics-scene-presets-v1`，保留模板快照、目标绑定、启用状态及可选完整分析，经过既有认证、资源租约、generation、SSE、PNG句柄和关闭排水链路进入实际 Worker Runtime。预设快照中的包、配方、作者身份、脚本、图片／视频背景仍在codec与公共预览门禁中拒绝。

Worker 新增私有 `static-session-capabilities-v1` 握手。Server 启动时探测实际 Worker 的静态来源列表；预设预览创建前再次确认所选 profile。旧 Worker 或未提供握手的工厂返回稳定 `503 previewUnavailable`／`failureCode:WorkerStaticProfileMismatch`，不会创建预览会话；普通与分析静态预览保持原行为。`runtime-info.sourceProfiles` 仅列出实际握手确认的预设 profile，路径存在本身仍不是像素验收。

新增的 Server transport 回归覆盖真实预设工程创建预览、实际 PNG 下载、分配后 PNG SHA 变化及移除后的基线 SHA 精确恢复；已有 PreviewTransport 248 项和 Hosting 35 项定向回归通过，solution 编译零警告／错误。匹配 self-contained 发布 `artifacts/backend-static-preset-preview-0860/deployment-final1/` 的 deployment SHA-256 为 `a2a77d3dec97b7fe7e13ae180c296d59b475fe154cdd45337fb0b50e03e25640`；更新后的探针 SHA-256 为 `11262bbf0e3127b78f4c32375df3620deaef2be756834904797099e73444548e`，最终证据 `http-final2/summary.json` SHA-256 为 `f7dc6ddd2b1ff09e54fff9036427b369373061a784f697caff189dec30bddbf3`，通过 **210 个 HTTP 请求／1242 条断言**，实际观察到13个Worker身份并在排水后全部消失，凭证扫描通过。探针还保留此前验证／导出、持久化、跨边界与PNG变化／恢复场景。

本批不交付完整场景编排、作者包资源、公共 package 执行、浏览器 W2、完整 B3/B4、性能、长片、GUI 或平台矩阵；静态预设最终外观只对完整静态模板快照与当前 Worker profile负责，不宣称任意字体、外部媒体或脚本能力。

<a id="f1-no-audio-preview-0861"></a>

## 44. F1 无音轨静态预览修复（0.86.1，合同1.12.0，2026-10-17）

修复 `PreviewBackendService.CaptureAsync` 对空 `AudioAssetId` 无条件申请资源引用的问题。存在音频身份时仍保留固定输入租约和每次 Runtime 创建的运行租约；无音轨工程跳过空资源身份，继续执行静态快照捕获与 Worker PNG 渲染。HTTP wire、路由和合同主版本保持不变。

代码提交 `477a083`，随后产品版本升为 `0.86.1`。新增 Server transport 回归通过真实 HTTP open 无音轨持久工程，再覆盖 preview create/frame、权威 state、认证 PNG 下载、SSE `preview.frameReady`、preview close 及关闭后的404；既有有音频租约、跨client与排水回归保持。定向测试 **1/1**，完整 `Backend.Server.Tests` **249/249**，零失败／跳过。

合同工具从当前源码重新生成并核验 63 paths／70 methods、合同1.12.0、产品0.86.1 及 manifest 指纹；F1修复本身无新增路由或 wire 字段，G15读模型路由与DTO使合同升为1.12.0。该修复只扩大合法无音轨静态预览入口，不代表音频播放、波形或完整 B3/B4 能力。

<a id="g15-preset-application-read-model-0861"></a>

## 45. G15 工程预设分配读模型（0.86.1，合同1.12.0，2026-10-17）

在既有0.85.0四种工程预设分配命令基础上，新增 `GET /api/v1/project-sessions/{id}/preset-applications?expectedRevision=...`。该读模型在工程 Writer 内按精确修订返回完整应用列表，逐项提供稳定 `applicationId`、目标 union、`targetValid`、`enabled`、模板快照摘要、`branchInputs` 及明确的来源占位字段；失效目标保留在读取结果中，不由读取删除或修复。

当前工程快照没有持久化来源模板库身份与库级 revision，因此 `source` 明确返回 `comparable:false`、`libraryId:null`、`revision:null`、`newVersionAvailable:null`，不把模板内容哈希冒充库修订。读取遵守统一1MiB JSON／20000项预算，超限整批返回 `413 resourceTooLarge`。四种既有命令的成功回执补充 `presetApplicationResult.applicationIds`，变更区域包含 `presetApplications` 与 `renderInputs`；不扩展 node／clips 写入或作者包能力。

代码提交 `9906fd7`；Server 定向传输回归 **1/1**，覆盖应用、读取、停用、更新、移除、精确修订、失效目标投影和样式存在性投影；ContractMetadata、freeze 与离线合同检查按 **63 paths／70 methods／302 schemas** 重新生成。G15未宣称库版本比较、完整场景编排、公共作者包或前端浏览器验收。

<a id="w4-orchestration-0870"></a>

## 46. W4 首个有限编排读写切片（0.87.0，合同1.13.0，2026-10-17）

本批交付首个可被 Web 编排页消费的有限后端切片：新增 `GET /api/v1/project-sessions/{id}/orchestration?expectedRevision=...` 精确修订读模型，返回当前工程真实 `nodes` 与 `programs` 结构投影。node 保留稳定 `id`、可空 `name`／`parentId`、placement、referenceRect、可空 layout 与 contentKind；layout 保留 kind、defaultPlacement、safeArea、maxWidthRatio、slotIds；program 保留来源与根／默认容器身份及可空 animationPolicy。读模型沿既有 Writer、client 归属、关闭会话中立错误、精确修订和1MiB／20000项预算，不返回最终样式、模板、字体、片段、前奏、PNG或完整场景 JSON。

新增严格工程命令 `orchestration.setDefaultContainer`。请求只接受非空白 `branchId` 与 `containerId`，两者最多128字符；来源必须存在，容器必须是当前工程现有歌词容器。缺失、null、空白、未知字段由 converter 拒绝；领域目标不存在、非容器或工程校验失败不改变工程。命令不提供节点创建、层级／排序／槽位／片段／前奏编辑，也不提供 nullable 清除默认值。

成功改变默认容器时只产生一次修订与历史、标记 dirty、清理 redo，回执返回 `orchestrationResult`，`changeSet.areas` 为 `orchestration` 与 `renderInputs`；相同容器为 no-op，保留修订、历史、dirty、redo 和事件语义。requestId 重试沿既有回执缓存，undo／redo、保存、关闭、重开保留状态。Server capabilities 同时公布读模型与命令能力。

实现提交为 `e9ea859`（读模型）、`b4e7282`（命令）、`a3081fa`／`d3e9f2f`／`79cde2a`（严格字段、changeSet 与回执修正）。本批合同工具同步为 **64 paths／71 methods／1.13.0／产品0.87.0**；真实 HTTP 与完整浏览器 W4 页面、结构编辑、几何布局、片段／前奏、最终外观和完整场景编排仍需独立验收。

<a id="r3-contract-nullable-source-0871"></a>

## 47. R3 合同 nullable source 与能力声明修复（0.87.1，合同1.14.0，2026-10-17）

本批只修复 G15 工程预设分配读模型的合同映射：`ProjectPresetApplicationSourceDto` 的 `libraryId`、`revision` 与 `newVersionAvailable` 实际由 `JsonIgnore(Never)` 固定输出，即使当前值为 null 也不会省略。冻结 OpenAPI 现将三字段声明为对应的 `string|null`／`boolean|null` 并列入 required；新增正例覆盖三字段显式 null。Server `/api/v1/capabilities` 独立公布 `project-sessions.preset-applications.read`，与四种写命令能力分开。

本批不新增路由、DTO 或 G16/G17 能力；行为仍返回 `comparable:false` 及三个 null，不进行库版本比较。合同工具、examples、manifest 与本行为正文同步升至合同 **1.14.0**／产品 **0.87.1**，需以 ContractMetadata、freeze、validate 和相关 Server capability/read-model 回归结果为准。

<a id="g16-preset-candidate-preview-0880"></a>

## 48. G16 工程预设候选试看（0.88.0，合同1.15.0，2026-10-17）

本批新增 `POST /api/v1/project-sessions/{id}/preset-application-previews`。请求同时固定工程修订、模板库修订、模板内容修订、严格 `project`／`branch`／`line`／`token` 目标 union、branchInputs、尺寸与透明模式；库或模板修订过期返回409，目标重复／不存在、静态能力不支持及快照预算超限在创建会话前拒绝。只接受无包、无配方、无脚本、无图片／视频背景的静态模板，node／clips、作者包、公共 package 与任意字体闭包不在本切片。

候选模板先在模板库只读操作中固定，再在工程 Writer 内核对当前修订并复制不可变 `Project` 到隔离 `ProjectWorkspace`，只执行 `PresetApplicationsEditUseCase.PrepareApply`，不执行 commit。候选因此不改变 projectRevision、ProjectHistory、dirty、redo、应用身份、工程事件或模板库；响应 `previewKind:"presetCandidate"`、`committable:false`，后续帧请求、状态、PNG、诊断、SSE、认证、TTL 与 close 排水复用既有预览注册表。候选会话不支持 PUT input，工程或库变化后须重新创建候选；正式 `presetApplications.apply/update` 仍是唯一提交入口。

验证：Server Debug 构建（含 Debug Worker）0警告／0错误；新增真实 HTTP 回归覆盖候选 PNG 与正式 apply 后 PNG SHA 一致、候选前后工程 revision／dirty／undo／redo 不变、过期工程修订及重复目标拒绝且无残留会话；冻结工具生成 **65 paths／72 methods／312 schemas／1045 examples**，`validate.py` exit0（examples 332正／713反，具体统计以报告为准）。本批不宣称 G17 模板固定试样、前端 W3/W4 浏览器验收或完整 B3/B4。

<a id="g17-template-preview-0890"></a>

## 49. G17 模板固定试样（0.89.0，合同1.16.0，2026-10-17）

本批新增 `POST /api/v1/template-preview-sessions`，来源为 `savedTemplate` 或请求内完整 `draft` 二选一。savedTemplate 同时锁定库修订与模板修订，draft 仅保存在会话内；当前固定样本为 `default-v1`，仅支持静态整行／逐元素模板。组合模板、包材质、效果配方、脚本、图片／视频背景与任意字体闭包在渲染前拒绝。

模板先经现有 canonical 模板库校验，再按模板实际目标语义套用到独立固定歌词样本，复用现有静态 Worker、PNG 编码、ETag/HEAD、SSE、TTL、诊断及 close 排水。返回 `previewKind:"template"`、`committable:false`；不会创建工程或修改工程修订、dirty、redo、历史和个人库。模板试样是卡片样本，不等价于目标工程最终外观。

验证：Debug solution 构建 0 警告／0 错误；Hosting 全量 **909/909**、Server 全量 **259/259**、Worker 全量 **78/78** 通过。Server G17 真实 Worker transport 回归覆盖 savedTemplate/draft PNG、个人库不变、不可更新、union 混用拒绝、Worker capability 不匹配不建会话及非法来源拒绝；模板注册表生命周期回归通过。合同工具从当前源码生成 **66 paths／73 methods／317 schemas／1048 examples（714反例）**，`validate.py` exit0。PNG/ETag 稳定性、跨 client 与重启排水仍按后续真实部署证据补充；本节不宣称浏览器 W3 或完整作者能力完成。

<a id="g18-quick-layout-0900"></a>

## 50. G18 快速布局（0.90.0，合同1.17.0，2026-10-17）

本批在现有工程 commands 端点新增 `orchestration.previewBasicLayout` 与 `orchestration.applyBasicLayout`。G18 首批只验收整来源模式；连续范围模式由 G19 后续切片补齐。整来源请求中 `selectedLineIds` 省略或显式 null。预演不写历史／修订／dirty／redo；应用在精确修订下执行一次历史，并返回 `basicLayoutResult` 与 `orchestration`／`renderInputs` 刷新区域。结果提供识别预设、受影响行、既有 clips、保留节点和拆分 clips 统计。

当时仅支持既有歌词容器／结构树可表达的整来源快速布局；未知预设、槽位数量与预设要求不匹配、非空 selectedLineIds、无效来源和领域失败在提交前稳定拒绝。G19 随后放开连续范围模式，并以当前合同规定的来源与结构校验为准。R4 来源编排程序缺口的隐式默认程序兼容修复与本批一并完成。完整 clips／leadIns、geometry／layout、公共 package／媒体／字体闭包、B5 作者和 B6 收口仍未交付；完整 W4、完整 B3/B4 与浏览器验收不因本批完成。


<a id="g19-clip-range-0910"></a>

## 51. G19 片段范围与编排投影（0.91.0，合同1.18.0，2026-10-17）

本批放开快速布局 `selectedLineIds` 的连续范围模式，并为 `basicLayoutResult` 增加本次新建 `clipIds`。范围输入必须是非空、唯一、按来源顺序连续且真实存在的行 ID；仅独立计时、Karaoke 播放策略来源可进入片段布局，FollowPrimary／Static 来源稳定返回 `UnsupportedSource`。整来源模式继续省略或显式 null。预演不写历史，应用一次历史并返回完整刷新区域。

`GET .../orchestration` 的 program 投影新增 clips 及 scheduleContext 摘要：稳定 id、名称、连续成员、容器身份和拆分调度边界。leadIns、公共 package、媒体／字体闭包、完整 W4/B3/B4 与浏览器验收仍未交付。

<a id="g19b-clip-lifecycle-0910"></a>

## 52. G19-B 显式片段生命周期（0.91.0，合同1.19.0，2026-10-18）

本批在既有工程 commands 端点新增四种严格编排命令：`orchestration.createClip`、`orchestration.renameClip`、`orchestration.setClipContainer` 与 `orchestration.deleteClip`，并在 `ProjectCommandResponse` 增加可选 `clipResult`。Server capabilities 同步公布四项命令能力；不新增 HTTP 路由。

四种命令均要求当前来源 `branchId` 与片段 `clipId`。创建还要求1..1000个非空、唯一、按来源顺序的 `lineIds` 和合法 `containerId`，名称可省略或显式 `null`；改名必须显式提供 `name`（可用 `null` 清除）；移动要求非空 `containerId`；删除只接受来源与片段身份。ID 采用稳定非空白字符串并受128字符上限约束，未知字段和缺少字段在 converter 层拒绝。

领域准入限制为已存在的独立计时、Karaoke 来源程序；容器必须属于该来源展示根且具有歌词布局。来源／片段／容器不存在、重复身份、行范围无效或提交后的展示校验冲突均整批拒绝，使用稳定 `failureCode`，不发布部分候选。成功结果以 `clipResult` 返回完整片段身份、成员和容器；真实变化一次提交一次历史并刷新 `orchestration`／`renderInputs`，no-op 保留既有修订与 redo 语义。删除后的默认容器回退由核心编辑器负责。

合同工具已从当前 Contracts metadata 生成并校验 **66 paths／73 methods／328 schemas／1063 examples（721反例）**。本批 Hosting 全量 **923/923**、Server 全量 **263/263**（含新增真实 HTTP 片段链 **2/2**）通过，Debug solution 构建为0警告／0错误；这些证据只覆盖当前有限命令切片，不等同完整 HTTP 场景或浏览器 W4 验收。完整作者包、leadIns、公共 package、媒体／字体闭包、最终渲染外观、性能、长片、GUI、平台矩阵与前端 W4 浏览器验收仍未交付。

<a id="g20-structure-0910"></a>

## 53. G20 有限节点结构编辑（0.91.0，合同1.20.0，2026-10-18）

本批新增 `orchestration.setNode`、`reparent`、`reorderChildren`、`addSlot`、`removeNode`、`reorderSlots` 六种严格命令、`ProjectCommandResponse.structureResult` 及对应六项 capability；路由仍为既有工程 commands。setNode 只修改现有节点名称，不开放通用节点 JSON；名称必须显式出现，可为 null。重挂与子节点排序必须显式给出可空 parentId，null 表示根层；槽位容器身份必须非空。两种排序均要求1..2000个唯一稳定 ID，必须完整覆盖对应同级或槽位集合。

Hosting 在 Writer 内调用 Core SceneTreeEditor 的纯候选编辑与完整展示校验；重挂保持世界外观，子节点显示顺序与槽位轮换顺序分离，删除仍被程序／片段／前奏引用的子树会拒绝。成功真实变化只提交一次历史／修订，并完整刷新 orchestration/renderInputs；no-op 沿核心结果保留修订与 redo。structureResult 仅提供当前操作真实身份与顺序，空值响应字段省略。源错误、候选冲突和非法输入在提交前拒绝，不部分改树。

合同工具从当前源码重新生成 metadata，冻结检查与 validate 通过 **66 paths／73 methods／335 schemas／1097 examples（745反例）**，52种工程命令的 discriminator 完整。实现提交 `a7e6275`；主批次验收通过 Hosting 全量 **925/925**、Server 全量 **263/263**、新增 G20 HTTP 定向 **3/3**，Debug solution 构建为0警告／0错误；三组计数各按其范围列出，不相加。未交付任意节点创建／复制、直接几何／布局参数编辑、leadIns、完整编排作者、公共 package、浏览器 W4 或完整 B3/B4。

<a id="g21-layout-placement-0910"></a>
## 54. G21 内部布局默认位置（0.91.0，合同1.21.0，2026-10-18）

本批新增 `orchestration.setLayoutDefaultPlacement` 严格命令与 `ProjectCommandResponse.layoutResult`，沿既有工程 commands 路由、精确修订、单写者、回执与历史门禁。命令必须提供已有布局容器的 `containerId` 与显式 `placement`；placement 为完整位置对象或显式 null，null 清除内部布局默认位置。成功变化一次提交 `orchestration`／`renderInputs`，同值与重复清除为 no-op 保留 redo。

合同范围只覆盖内部布局默认位置及编排读模型中的 nullable `layout.defaultPlacement`，不开放外层几何、参考矩形、动画、任意节点创建／复制、leadIns 或 G22-G24。G21-A 已验收 c9e629b：Hosting 定向 2/2、Server HTTP 1/1、Debug solution 构建 0 警告／0 错误；这些计数仅属于 G21-A，不代表完整 G21。

<a id="g21b-g24-basic-orchestration-0910"></a>

## 55. G21-B／G22-A／G23／G24 基础编排有限切片（0.92.0，合同1.22.0，2026-10-18）

本批产品按交付版本规则递增至0.92.0，HTTP协议保持1；最终合同实现基线固定为版本提交 `cc6d632`，G21-B主体为 `34fe3f5`，测试时序修复为 `d225c4b`／`96f6b2a`，前序切片分别为G22-A `1a38f2d`、G23 `13da56b`、G24 `987311b`。新增两个GET和九个严格工程命令，工程commands累计62种，保留G21-A内部布局默认位置、G20结构、G19片段与既有编辑出口。

### 55.1 G21-B 属性与动画

新增setNodeGeometry／setLayoutSettings／setSlotAnimation／setProgramAnimationPolicy，完整替换已有节点外层几何、布局安全区/宽度、槽位动画与来源动画策略；不开放任意节点JSON。位置六种anchor、画布外有限坐标、正缩放、正参考尺寸；完整slotAnimation十四字段与三组完整通道、四Int64字符串时间按Core约束准入。slotAnimation显式null仅普通布局可清除，animationPolicy显式null恢复按来源角色解释。propertiesResult返回实际目标与changed；完整候选编排投影、回执预算在一次历史提交前预检，no-op保redo。

G21-B定向Hosting **6/6**、Server真实HTTP **7/7**通过，其中HTTP包含保存／关闭／重开。Debug正常solution构建 **0警告／0错误**；本节各切片定向计数分列，不与后续全量相加。

### 55.2 G22-A 固定容器参考

新增 `GET .../orchestration/geometry`，严格唯一expectedRevision/nodeId，返回Core固定参考与父参考、世界矩阵、pivot和四角，固定referenceKind=fixedContainerReferenceV1、finalPixelGeometry=false。节点数1024、深度64、输入20000项/累计ID64KiB、目标ID1024 UTF-8字节独立准入；坏树或不可逆数值、目标缺失与超预算分别稳定拒绝，不截断。复用Core固定容器几何，不把它称为文字、自动布局、动画、效果或最终像素几何。G22-A定向Hosting **13/13**、Server真实HTTP **11/11**通过。

### 55.3 G23 手工前奏与提示容器

新增createCountdown／createLeadIn／updateLeadIn／deleteLeadIn；提示容器限已有普通组、1..64标记并由Core生成真实稳定身份；手工前奏显式引用来源行与同根提示容器，正durationUs、布尔enabled、required-null名称。完整编排投影返回leadIns、有限countdown/textMark内容和完整slotAnimation（普通布局无配置时显式null）；不自动生成前奏。删除保留提示容器与自动生成设置，修改真实变化沿Core转Manual。G23定向Hosting **4/4**、Server真实HTTP **9/9**通过；Rendering静态snapshot/codec相关 **111/111**单独通过，这些codec证据不等同Runtime最终像素验收。

### 55.4 G24 单句局部微调

新增单句 `GET .../orchestration/line-local-transform` 与setLineLocalTransform；GET严格唯一expectedRevision/branchId/lineId，写入transform完整四字段或显式null清除。比例偏移、旋转与缩放必须有限、scale>0；响应与回执始终保留transform，即使null。目标缺失稳定TargetStale，真实变化一次历史并完整刷新orchestration/lineLocalTransform/renderInputs，同值与重复清除保redo；不修改歌词源时间。G24定向Hosting **4/4**、Server真实HTTP **1/1**通过。

### 55.5 合同与验收边界

合同从最终0.92.0源码 `cc6d632` 重新编译Release Contracts metadata（exit0，无警告／错误诊断），独立overlay逐字段审阅九种命令的继承形状与strict wire、动画嵌套完整性、Int64范围、required-null与两个GET的查询约束。冻结write/check与validate均exit0：**68 paths／75 methods／367 schemas／1390 examples（989反例）**，工程命令discriminator共62种。正反例独立维护在orchestration_properties_fixtures.py，校验工具额外核对两GET唯一必填query/capability与响应required-null/const标记。manifest显式固定15个相关Core真值文件，任一关键文件缺失即失败，不以空glob静默漏掉几何／前奏／展示编辑规则。

最终0.92.0 Debug正常solution构建 **0警告／0错误、exit0**；以下四模块按各自范围独立通过，不与§55.1～55.4的定向切片计数重复累加：

| 最终回归模块 | 结果 |
|---|---|
| Backend.Hosting | **955/955，exit0** |
| Backend.Server | **295/295，exit0** |
| Backend.Rendering | **214/214，exit0** |
| Backend.Worker | **78/78，exit0** |

最终TRX位于 `artifacts/test-results/g20-g24-final-0920/*.trx`。原0.91统一轮的Hosting **953通过／1失败／954总数**保留在 `artifacts/test-results/g20-g24-final/hosting.trx`，不改写为通过。定位表明旧测试在job终态已可见而Completion仍未完成的窗口默认close，close合法拒绝后测试未检查Response，继而空引用；`d225c4b`／`96f6b2a`仅修改测试，显式使用ActiveJobs wait并补确定性收尾回归，定向 **13/13**通过。这是测试时序与断言修正，不是本批生产缺陷修复；定向13项同样不并入最终全量计数。

上述为有限后端编排切片；最终Runtime像素、浏览器W4、任意节点创建／复制、完整G20-G24、完整作者包、公共package／媒体／字体闭包、完整B3/B4、性能／长片／GUI／平台矩阵均未据此宣称完成。

<a id="g20b-node-creation-0930"></a>

## 56. G20-B 有限节点创建与子树复制（0.93.0，合同1.23.0，2026-10-18）

实现基线 `d045eae`，产品升至0.93.0，HTTP协议保持1。新增 `orchestration.createNode`、`orchestration.copySubtree` 两个严格命令及对应capability，既有commands累计64种，不新增路由。行为见[统一API§13.17](../../architecture/backend-api.md#g20b-node-creation-0930)，范围与后续工作见[实施计划](../plans/backend-g20b-node-creation-plan.md)。

### 56.1 结构与原子语义

createNode全部显式提供kind/parentId/name/slotCount：group、auto要求slotCount:null，slotSequence要求1..64整数；kind大小写精确。parentId:null为画布，否则必须已有普通组；名称可null且最长256 UTF-16代码单元。Server生成真实唯一ID，采用默认位置及Canvas参考；Auto使用Core默认LyricContainerLayout，序列同次生成根/全部槽位与SlotAnimation，maxVisibleLines=min(2,count)。

copySubtree全部显式提供nodeId/name，同父复制完整子树；name:null清副本根名，保留后代名称。复用Core既有复制，重映射内部parent/slot/mark引用；单槽位或mark复制按Core语义追加外部owner序列，完整含owner子树只改副本内部引用。不复制歌词、来源程序、默认容器归属、片段、前奏或预设分配绑定，不改源时间。

成功structureResult返回createNode/copySubtree、新根nodeId及changed:true，parentId根层省略，containerId/orderedIds省略。新增createdNodeIds完整唯一，根第一、其余候选新增顺序；旧六结构命令省略。一次成功一次历史，undo/redo与同一有效回执重放保持已接受ID。源树/候选统一展示校验、MutationBlocked、精确修订、回执clone与完整预算在一次提交前成立；失败无部分变更，保留redo。

### 56.2 有限额度与错误

command16KiB，源及完整候选各最多2000节点/深64，copy完整子树最多128节点；64槽位仅限制createNode初始slotCount。源/候选验证输入另限20000项，含节点、程序、引用、覆盖、歌词branch/line/token及程序关联遍历项，累计身份/名称/标记文本最多1048576 UTF-16代码单元。长歌词即使编排树很小也可能413，此额度不等同编排输出条目数。

完整编排输出独立1MiB JSON字节/20000项，实际配置的完整成功回执预算提交前验证；不截断或部分提交。严格形状400 invalidRequest；缺失源/父422 TargetStale，非普通组父422 ContainerInvalid，坏源/候选422 PresentationInvalid；超限413 resourceTooLarge。

### 56.3 合同冻结

从 `d045eae` 当前源码重新编译Release Contracts metadata，实际exit0，无警告/错误诊断。node_creation_schema.py独立审阅两种派生record继承形状、CLR类型/nullable/default与strict wire；create三个kind/slotCount互斥闭合分支、名称UTF-16说明和新旧结果差异均固定。createdNodeIds沿真实nullable/default-null/omitNull声明，新增操作必须返回1..128唯一ID，旧六命令不出现。

冻结write/check与validate均exit0：**68 paths／75 methods／369 schemas／1522 examples（1092反例）**，64种工程命令discriminator完整。node_creation_fixtures.py新增132例（103反例），原1390例的name/schema/valid/value逐项保留。manifest显式固定20个编排Core真值源：原15及SceneContainerDefinition.cs、SceneContent.cs、LyricPresentationDefinition.Tree.cs、两PresentationValidator文件；关键文件缺失即失败。隔离Python mock只模拟LyricPresentationDefinition.Tree.cs缺失，明确触发Missing reviewed orchestration behavior source，生产文件未改/未移动；日志missing-materializer-guard.log保留，真实路径恢复后默认check通过。离线日志、metadata与报告位于 `artifacts/backend-g20b-contract/`；合成例子和schema校验不代替真实HTTP或像素验收。

### 56.4 最终构建与回归

正常Debug solution最终构建 `artifacts/g20b-build-acceptance.log` **exit0，0警告／0错误**；下列模块各按本身范围独立通过，TRX在 `artifacts/test-results/g20b-acceptance/`，全部exit0、无失败/跳过/中止：

| 模块 | 最终结果 |
|---|---|
| Backend.Hosting | **979/979** |
| Backend.Server | **321/321** |
| Backend.Rendering | **214/214** |
| Backend.Worker | **78/78** |

Server包含本批26项真实HTTP定向场景及最终cue原子拒绝补强，覆盖三类创建、完整/槽位/mark复制、引用重映射、原绑定保持、候选容量/节点/深度门禁、严格JSON、回执复放、旧修订冲突、单步历史、配置/分配与保存关闭重开；这些是测试fixture内真正HTTP传输证据，不冒称独立self-contained发布或浏览器验收。Hosting定向最终67/67及早期Server定向26/26分别保持原归属，不与最终模块计数相加。

初次构建 `artifacts/g20b-build-initial.log` 因新Server测试CA1861失败；初次Hosting定向 `artifacts/g20b-hosting-initial.log` 因新测试CA1861/CA1869编译失败，均为零执行测试，不记为运行回归失败或通过。修复只调整测试静态数组/缓存序列化配置，后续定向、正常solution与最终模块结果以上述证据为准，初始日志保留不改写。

本批完成有限后端创建与Core同父复制出口；通用节点JSON、任意内容/mark/附着编辑、自动前奏、完整G20-G24、完整编排作者、最终Runtime像素、浏览器W4/用户验收、公共package及完整B3/B4/B5/B6继续独立。原全量、性能、长片、GUI与平台矩阵未恢复。


<a id="g20-g24-completion-0940"></a>

## 57. G20-G24 后端整体补齐实施（0.94.0，合同1.24.0，2026-10-18，原定义后端范围已验收）

本节按只读Web `web-client-w4-design.md` §3原始G20-G24表及 `web-ui-design.md` §6.5逐项追溯，范围见[整体计划](../plans/backend-g20-g24-completion-plan.md)。产品源码 `c50516d` 已提交并推送，原定义G20-G24后端范围经逐项验收关闭；本轮不扩大为完整Web产品或全部后端完成。§55/§56保留原有限基线与验收归属。

### 57.1 G20结构与G21/G24配置

G20原需求的新建、删除、改名、改父、同级排序、槽位增删排序已由既有命令可达，本轮提供跨操作联合验收，不重复增加通用节点JSON。G21补Auto/SlotSequence布局kind切换及project enter animation；G24补line enter override/null继承和presentation读取。project/program/line动画层级、布局切换保留与拒绝语义、一次历史及源歌词时间不变已在HTTP和真实Runtime中核验。

### 57.2 G23生成、标记、模板及附着

补generation settings、完整preview、selected apply与delete exclude；完整候选/诊断不截断，preview不隐式提交工程，应用在当前修订重新校验选择和冲突。标记增加/正文/排序/均匀排列、倒计时整体与单mark外观配置接入已有领域语义；标记删除复用既有 `orchestration.removeNode`，由Core同步owner的 `MarkNodeIds` 并执行完整候选验证，不新增删除命令（行为见统一API§13.11）。静态Node模板映射满足原Web §6.5，保持模板来源与实际工程节点的身份及持久化归属。

附着切换使用真实Worker测量reference保持世界appearance，不以固定参考矩形替代文本/布局测量。Writer外测量后在提交前核原修订、目标、deadline和完整候选；取消或过期不部分提交。严格字段、错误和静态模板限制以最终统一API/合同为准。

### 57.3 G22同帧测量

一次性POST经真实Worker执行 `scene-measurement-v1`，time与layout模式输出同帧geometry和PNG，包含世界角点、编辑reference及slot/line等身份。`sourceSha256`与`sampleInputFingerprint`分别表达原固定源和实际采样输入，不把layout代表采样冒充原工程时刻；代表元素明确标记。几何reference不自动等于字形墨迹或任意效果的最终像素边界。

完整响应2MiB、4096 elements、20000 corners，layout代表样本最多512；最大输出1280×720、单次20秒、全局并发2/client并发1。超限整批失败；source profile保持公开静态输入范围。shutdown停止接收新测量、取消并排水；在途测量与附着准备参与client回收保护，提交deadline再次检查。这些生命周期修复已纳入最终回归。

### 57.4 最终合同、构建与验收

产品 **0.94.0**，最终生产源码 **`c50516da6cf421e1bb3fbc3cc01fc11d0235e93b`** 已提交并推送；独立探针源码 `9a6b4f4`。冻结合同 **1.24.0／70 paths／77 methods／401 schemas／1913 examples（1394反例）／78种工程命令**，合同提交 **`b312a83540b55a2cfcf5fa37ad7179c72f6fcfbd`**。fresh metadata、freeze write/check与validate全部exit0；root独立重导metadata、check/validate同样通过。实际http-final3的73份JSON全部匹配schema，0跳过，覆盖20操作。原1522例顺序和原值保持，仅一个响应例增加两个必返null字段。报告位于 `artifacts/g20g24-contract/{final-validation.json,root-final-validation.json,route-and-fixture-preservation.json}`。

正常solution最终日志 `artifacts/g20g24-solution-final2.log`：**exit0，0警告／0错误**。最终相关回归按以下不重复范围登记：

| 范围 | 结果 | TRX（位于 `artifacts/test-results/g20g24-acceptance/`） |
|---|---|---|
| Backend.Hosting完整 | 1033/1033 | `hosting-final1.trx` |
| Backend.Server完整 | 352/352 | `server-final1.trx` |
| Backend.Rendering完整 | 214/214 | `backend-rendering-final1.trx` |
| Backend.Worker完整 | 78/78 | `worker-final1.trx` |
| Rendering受影响七类定向（另一测试项目） | 40/40 | `rendering-directed-final1.trx` |

以上共 **1717/1717**，均通过；前四模块1677项与另一项目Rendering定向40项无重复。Server精确工作流 **31/31** 已包含在352项中，不再相加；其 `server-workflow-final2.trx` 包含真实动画opacity、几何/PNG对照，完整10帧透明纯视频导出解码及0.6s/1.4s两时刻Runtime oracle。低层测量Hosting16、Rendering同帧/LeadIn8、Worker static24及早期过程范围不另并总数。

独立匹配self-contained Server/Worker **0.94.0** 发布位于 `artifacts/web-backend/0.94.0-g20g24-final1`，publish exit0；806部署文件与804生产输入逐SHA核验。发布发生在生产源码提交前，原manifest仍记录 `sourceCommit=958bbd6`、`sourceDirty=true`；`artifacts/g20g24-production-commit-verification.json` 证明全部804生产输入与 `c50516d` Git blob一致。不将该发布描述为clean clone构建。

`tools/backend-g20g24-http-probe/` 独立探针最终证据 `artifacts/backend-g20g24/http-final3`：**exit0，73请求／781断言，6次实际测量，8个成功job终态**。覆盖匹配部署能力、结构/布局、project/line动画、前奏生成/mark、time/layout同帧测量、真实reference附着、过期修订拒绝及保存/关闭/重开。11个观察进程身份（含8个Worker）全部gone；170文件凭证扫描、171证据文件SHA检查通过。它与测试fixture中的真实HTTP及10帧成片oracle分别保留范围，不重复统计。

G22无能力兼容测试使用Python模拟旧Worker协议，不是某已发布旧版本binary；SlotId修复前Debug Worker的真实失败属于独立过程证据。更早Server fixture失败、独立探针http-final1的前置时间顺序错误与http-final2的可选slotId读取错误均保留原失败及正常排水证据；两次探针修正没有改变生产源码，不改写为产品缺陷或计入最终通过。

### 57.5 完成边界

本轮按原始任务表定义的G20-G24后端能力已逐项验收关闭：G20结构、G21布局/动画、G22真实测量、G23前奏/mark/模板/附着、G24单句覆盖及变换均有对应证据。G19片段扩缩拆合、G25、独立时钟、关键帧和任意轨道不是原任务前置；完整B3/B4资源输入、B5/B6、浏览器/用户及原性能/长片/GUI/平台矩阵仍独立。

### 57.6 前后端交付状态

2026-10-18尝试向独立Web的PinkPike发送正式交付邮件，Agent Mail返回 `CONTACT_REQUIRED`，明确 `nothing was sent`：跨项目联系尚未批准，本次正式交付未送达，等待PinkPike批准现有pending联系。不代替对方批准、不绕用旧项目，也不记为已接受或浏览器已验收；该协作状态不改变本轮后端功能已验收关闭的结论。产品c50516d及合同b312a835均已推送origin。


<a id="basic-business-0950"></a>
## 58. 基础工程业务补齐（0.95.0，2026-10-18）

### 58.1 范围与实现

本轮接续用户授权审计建议1/2及[计划](../plans/backend-basic-business-completion-plan.md)。A项纠错已单独以0.94.1／合同1.24.1交付，证据见[修复记录](../acceptance/backend-basic-business-bugfix-0941.md)，不并入本轮通过数字。0.95.0目标合同1.25.0，基础业务实际行为见[统一API§17](../../architecture/backend-api.md#basic-business-0950)。

B～G包括授权根单层目录读取、工程改名与实际资源清单、Core单来源LRC计划/确认/固定内容、受管理音频替换/清除及历史绑定同步、完整.mktpl归档预检/新ID一次CAS/固定导出、真实Node路径含动态text叶的21属性来源解释、clip完整成员扩缩/共享调度拆分/静态安全合并。全部工程写入复用唯一Writer/精确修订/单层history与receipt，完整候选及回执先校验后提交。

计划E原静态-only限制已调整为StyleTemplateTransfer当前合法高级对象完整保真传输，不执行、不编译、不安装；导入不等于公共包运行或完整B5。拆分新clipId由client显式提供；FollowPrimary使用自身容器上下文，不伪造主来源调度槽位。

验收过程中发现内置模板的 `builtin:` 保留ID被个人库归档codec正确拒绝，公共导出此前返回LibraryInvalid。修复仅在生成内置归档时将捕获对象ID确定映射为 `exported:<原builtinID>`，响应templateIds仍原选择ID；个人模板ID原样、其他定义/包资源完整保留、源库不变，导入仍全部新ID。Core保留身份保护未放松，修复前失败作为过程证据保留，不计入最终通过。

另外两项集成修复：

- 模板预检/导出使用每次独有的临时目录，Unix显式0700（Windows沿继承ACL）；archive.mktpl与原codec的.tmp均位于该目录，finally仅清理本次自有目录。成功和坏ZIP失败均须验无残留及上传租约释放。
- history.undo/redo按真实before/after差异合并audio/audioAnalysis/waveform/timeline/renderInputs/project刷新区域，保留原其他区域及稳定IDs。音频绑定改变时requiresFullRefresh=true；no-op不发变更事件，避免资源已恢复但前端未刷新的断链。

### 58.2 验收记录（后端批次已完成，含生产源码基线重放）

0.95.0／合同1.25.0的历史 final2 证据仍按该批次独立归属：独立匹配发布与真实 HTTP 已通过，257 请求／1756 断言、9 个 job 全部成功，发布源提交为 `40ee152`；下表的定向回归和合同数字也保持该历史批次口径。

| 范围 | 当前实际终态证据 | 结果 |
|---|---|---|
| 正常solution | build-final4.log | exit0，0警告/0错误 |
| Backend.Hosting | hosting-final4.trx | 1059/1059，exit0 |
| Application定向 | application-final2 | 65/65 |
| Core分析定向 | core-analysis-final2 | 31/31 |
| Backend.Rendering | rendering-final1 | 214/214 |
| Worker | worker-final1 | 78/78 |
| Server | server-final4 | 385/385，exit0 |
| 合同生成/check/validate | 第6轮最终校验，产品0.95.0／合同1.25.0 | 80 paths／89 methods／436 schemas／84命令，2319例（1748反例）全部通过 |

六组正式回归去重合计 **1832/1832**，零失败、零跳过。机器清单 `artifacts/basic-business/accepted-test-results.json` 记录TRX及SHA，`accepted-source-inputs.json` 固定src/tests/props输入；`contract-check-precommit` exit0、指纹一致。server-final3的385/385保留过程归属，不重复相加。独立匹配发布与HTTP现已通过：`artifacts/basic-business-http-0950-final2` status=passed，257请求/1756断言，credentialScannedFiles=616，观察9个Worker身份，9个job全部成功；deployed-shutdown exit0且remaining=[]。`contract validate --http-log` exit0：175 JSON checked、81 non-JSON/unmapped skipped、1 unpaired binary artifact、covered 42 operations；合同总计80/89/436/2319/1748。deployment manifest SHA-256=`40c51379f46426b0244a9c8b0c5553ec2944812c420e60fc2ccb2083ecba6ba0`，发布源commit=`40ee152`；probe-only修复commit=`1ffd5f4`。首轮probe失败仅metadata脚本问题，旧证据保留，不并入最终通过。

独立HTTP证明不包含浏览器/用户验收、完整公共B3/B4输入、advanced template执行或完整B5/B6；高级模板仍仅保真惰性传输。生产源码先提交再作匹配self-contained发布；

本次验收生产源码基线 `38f2fa9` 的 0.96.0／合同1.27.0 fresh replay 已另行登记于 `artifacts/basic-business-current-38f2fa9/assessment.json`：匹配 self-contained 发布核对 834 个源码输入、806 个部署文件，`sourceDirty=false`；真实 HTTP 257 请求／1757 断言、9 个 job 全部成功，18 个观察进程身份退出、617 个 evidence 文件哈希核验通过；合同 HTTP 校验为 175 个 JSON response、63 个成功 JSON 请求。该 B～G 有限重放用于确认该生产源码基线的基础业务链路，不覆盖 TTML document scope 或 ruby segment API，后者由 §59 的独立证据负责。

初始编译错误、夹具问题、ImmutableArray比较、内置模板导出LibraryInvalid、analysis显式通知回归及旧fixtureCompletion观察窗口均作为过程失败保留，不并入最终通过；core-analysis-final1零匹配不计为测试通过。完整路径、退出码和最终边界已按各轮生产输入分别登记。

### 58.3 完成边界

本轮不扩大完整公共B3/B4、媒体背景/字体授权闭包、整曲正式任务、B5/B6、G25、关键帧/任意轨道、性能/长片/GUI/平台矩阵。对端此前W3用户验收仍是PinkPike邮件68历史报告，本轮1.25浏览器与用户验收未做；后台实现及冻结不代记前端验收。

<a id="ttml-ruby-timeline-0960"></a>
## 59. TTML/ruby 独立时间轴与导入导出（0.96.0，合同1.27.0，2026-10-19，P0～P6已完成）

### 59.1 范围与实现

本轮按用户确认的边界实施：歌词分支继续使用原有 `LyricsDocument → Branches → Lines → Tokens` 结构，只在 `RubyAnnotation` 下增加有稳定 ID 的 `RubyTextSegment` 列表；每个 segment 保存独立绝对微秒区间和 `TimingStatus`。旧 `text` 保留为拼接兼容投影，旧 v16 文档迁移为单个 `Missing` segment，不猜测逐字时间。`setRubyText` 对 timed/multi-segment 注音保持保护，新的文本、结构和时间变化使用七个显式 segment 命令。

TTML 支持拆为两个明确 profile：`ttml.project` 保留多分支、身份、关联、权威行正文、raw 时间/status 和 ruby 参数；`ttml.amll` 输出选定来源并使用 AMLL ruby 结构。导入器处理标准父容器、`par/seq`、`begin/end/dur`、frame/tick、AMLL 绝对时间标记、XML 安全限制和有界报告；多歌手 `agent/role`、旁轨及未映射样式只进入损失报告，不自动创建新的领域分支。再次导入多来源文档必须显式选择 `sourceBranchId`，所有内部 ID 重新分配并同步关联。

下游已同步 segment 时间：sampler 按段计算并保留基础 token 语义；Skia 维持聚合整形并按 cluster 归属裁切；普通/package snapshot、Worker、PNG/BGRA/geometry、脚本只读投影和 HTTP 读写均使用同一份数据。spacing solver、独立歌手实体和完整工程样式/媒体/脚本序列化不属于本轮。

### 59.2 分层验收证据

| 层级 | 结果 | 证据 |
|---|---:|---|
| solution | 0 warning / 0 error | `artifacts/ttml-ruby/validation/final/solution.log` |
| Core | 1314/1314 | `core.trx` |
| Application | 695/695 | `application.trx` |
| Hosting | 1109/1109 | `hosting.trx` |
| Server（非发布分类） | 387/387 | `server.trx` |
| Backend.Rendering + Skia | 217/217 + 9/9 | `backend-rendering.trx`、`backend-skia.trx` |
| Rendering 定向（含复杂 cluster） | 100/100 | `rendering.trx` |
| Worker / Runtime Worker | 80/80 + 6/6 | `worker.trx`、`runtime-worker.trx` |
| Scripting ruby 投影 | 12/12 | `scripting.trx` |
| 去重定向合计 | **3929/3929** | `results.json` |

真实 Worker 的 6/6 覆盖 static/package、普通/透明、seek/reopen、PNG/BGRA/geometry。官方 AMLL parser 对五组由真实 C# exporter 生成的 XML 通过；独立审计及消费端实际损失见[独立审计报告](../research/backend-ttml-ruby-independent-audit.md)。其中 `Missing` ruby、毫秒舍入、普通未计时元素和基础元素时间被 ruby 范围替代均已计数，不把 XML 写出数量冒充消费端保真数量。

### 59.3 合同、发布与真实 HTTP

合同冻结为 **1.27.0／80 paths／89 methods／449 schemas／91 commands／2648 examples（2040 negative）**；metadata、OpenAPI、examples、manifest 和行为文档来自同一源码快照。匹配 self-contained Server+Worker 发布核对 **834 个生产输入、806 个部署文件，`sourceDirty=false`**；生产发布/源码核验基线为 `71d39bd`，产品与 HTTP 验证源码提交为 `4fe7809`。随后入口文档先以 `e6847b4` 收口，`38f2fa9` 修复 v17 长 ruby ID fallback；本次验收的生产源码基线为 `38f2fa9`，后续为文档提交。

三组外部真实 HTTP 通过 **3/3**：合计 **183 请求、180 组脱敏 exchange、9 个 job 全部 `succeeded`**；3 个 Server、3 个 Worker 共 6 个观察身份全部退出。证据为 `artifacts/ttml-ruby/validation/final/published-http-final.trx` 与 `published-http-summary.json`。冻结合同 `validate.py --http-log` 对以下三组目录逐组通过，汇总保存为 `artifacts/ttml-ruby/validation/final/contract-http-validation.json`：

- `artifacts/ttml-ruby/http/b7073f2a033046258ac027f4eb92ea75/http-exchanges`
- `artifacts/ttml-ruby/http/c4b10b76547b472da2bc422420c3caf9/http-exchanges`
- `artifacts/ttml-ruby/http/d10cc363e9c84fc394928439ddcffca3/http-exchanges`

HTTP 覆盖导入、显式多来源追加、七命令中的真实编辑/历史、读取、保存关闭重开、document/branch scope 计划、认证下载和 TTML 回读。首轮夹具路径错误、第二轮旧分支参数错误及最终修复重跑均保留在 `published-http-*.log/.trx`，不并入 3/3 通过数字。

00:24 后另外生成的 `39dfe4…`、`7a28d1…`、`ba265c…` 三组 HTTP 目录属于同一发布输入的后续重跑；它们已独立通过合同校验，但没有对应的最终 summary/TRX，明确标为非权威重跑，不替换本节列出的三组权威目录或 3/3 关闭数字。

### 59.4 完成边界

本轮关闭的是 ruby 独立时间轴、TTML 两 profile、持久化/编辑/HTTP/采样/渲染/Worker/脚本只读投影及其合同与真实后端链路。自动 ruby spacing solver、完整公共 B3/B4、B5/B6、浏览器/用户验收、全量性能/长片/GUI/平台矩阵和独立歌手/任意轨道仍未由此完成。

<a id="comprehensive-acceptance-0960"></a>
## 60. TTML/ruby 与基础业务综合验收（0.96.0／合同1.27.0，2026-10-19）

本次验收生产源码基线为 `38f2fa97f6b15abee041ab5198d636a03e231e96`，后续为文档提交。最终 b 轮 `artifacts/comprehensive-acceptance/38f2fa9-b/summary.json` 记录 16 条命令全部 exit0、正常 solution 零警告/错误、1760 个 tracked 输入前后稳定；十组 TRX 合计 **3930/3930**，零失败、零跳过。主代理已独立复核 summary 所列 evidence 哈希、TRX 计数和 16 条命令退出码。该结果接续 §59 历史 3929/3929，不重写旧数字。

| 定向范围 | 通过数 |
|---|---:|
| Core | 1315/1315 |
| Application | 695/695 |
| Backend.Hosting | 1109/1109 |
| Backend.Rendering | 217/217 |
| Backend.Rendering.Skia | 9/9 |
| Worker | 80/80 |
| Server | 387/387 |
| Rendering | 100/100 |
| Scripting | 12/12 |
| Runtime 真实 Worker | 6/6 |

本轮 runner 祖先链内观察到的 121 个 Server/Worker 产品进程身份均已退出；并行代理进程不计入，未观察到的短命后代不作穷尽声明。合同冻结保持 **1.27.0／80 paths／89 methods／449 schemas／91 commands／2648 examples（2040 negative）**。对旧 TTML 留档离线复核 173 个 JSON 响应、42 个成功 JSON 请求、15 个操作通过（7 个非 JSON 跳过、0 未配对），证据为同目录 `contract-http.json`；这是已留档交换的合同复核，不是新一次 HTTP 运行。

独立 fresh B～G 链路见 `artifacts/basic-business-current-38f2fa9/assessment.json` 与 `assessment-manifest.json`：匹配 self-contained Server/Worker 发布 **834 个源码输入／806 个部署文件**，`sourceDirty=false`；真实 HTTP **257 请求／1757 断言**、9 个 job 全部成功、18 个观察进程身份退出、617 个 evidence 文件核验通过；合同核对 175 个 JSON 响应和 63 个成功 JSON 请求。其 scope 仍为原基础业务 B～G，不把它算作 TTML document scope/ruby API 的新 HTTP 验收，也不扩展高级模板实际执行范围。

关键 SHA-256（本地留档路径均在仓库 `artifacts/` 下）：

- `comprehensive-acceptance/38f2fa9-b/summary.json`：`0331cc07933897abb2ede34f4a41ecbeb20f732ce15292b0db932fa9756ec81e`。
- `basic-business-current-38f2fa9/assessment.json`：`e8d0c320a4ea96e93027c17107cd7136a5e0a6d8011347a32fc5ebd0f271c759`。
- `basic-business-current-38f2fa9/assessment-manifest.json`：`2a16461b9a3eaa604e09533e2e928f0e16c8060a6b4d74cd89fc0e95daddb96b`。
- `basic-business-current-38f2fa9/http-attempt1/evidence-manifest.json`：`84adb49c8144f623daf4856840940c35ff95d4c41ab8b231253ece6ba76d3f2f`。

过程失败独立保留：首轮 `artifacts/comprehensive-acceptance/1c81cd7-a/failure-summary.json` 因并行修改失去固定输入基线，不并入最终通过；最终 b 轮的脚本变量和命令计数后处理错误见 `harness-postprocess-failure.json`、`finalize-count-assertion.log`，修正仅用于独立汇总输入/输出/进程证据，未重跑或修改测试结果。spacing solver 仍是后续规划；完整公共 B3/B4、B5/B6、浏览器/用户、性能/长片/GUI/平台及独立歌手/任意轨道边界保持。


<a id="ruby-spacing-0970"></a>

## 61. Ruby自动水平间距与共享布局（0.97.0／合同1.28.0，2026-10-19）

按用户授权，以子代理为主完成生产实现、独立回归及HTTP探针，主代理负责源码和证据验收。生产代码80a1943、工具时间夹具修复f402bc4已提交推送。`RubySpacingSolver`仅检查垂直墨迹严格相交的所有此前ruby，跨无注音token同样约束；最小gap固定1布局像素，安全token/cluster刚性组整体平移，不改变glyph/自然advance/聚合整形。ruby锚点取原始自然边界加组位移，正文progress路径沿最终位置；spacing与segment时间/progress无关。绘制、SceneLayout、SceneFrame、TokenGeometry和FamilyTransforms等6处入口统一消费GetLineLayout；扩宽联合墨迹沿原全家族宽度缩放。

正常solution零警告/错误；五组去重1330/1330（1012+9+217+80+12），新增25已包含其中。旧36帧像素/PNG/geometry golden原文件不改。gap变异红3例、恢复19/19，最终solver bytes精确恢复；初次夹具/编译/Worker构建产物失败独立保留。证据见`artifacts/ruby-spacing/validation/assessment.json`和主代理`root-code-acceptance.json`、`root-static-review.json`。

合同80/89/449/91/2648/2040不变，无wire/schema扩张；fresh metadata/write/check/validate及主代理paths/schemas/examples独立对比通过，见`artifacts/ruby-spacing/contract/final/`和`root-contract-validation.json`。fresh self-contained发布源码为`cc30d4c2172cc9dfa4b2bf2706a736d7abc392c6`，`sourceDirty=false`；835个生产输入、806个部署文件和542个evidence文件SHA由主代理独立核验。真实HTTP **179请求／2689断言**，27组同帧比较完整source SHA、PNG及完整definition geometry全部一致，21个job全部succeeded；55个观察身份（含36个Worker）全部退出，Server exit0。541个证据文件凭证扫描通过。合同对179个实际JSON响应及56个成功JSON请求校验通过，0跳过；HTTP请求/响应不能相加记为235次请求。

同源Runtime oracle只对`AudioPath`声明做标准化，前提是两侧音频size和SHA相同；标准化之后仍强制完整source hash匹配，没有忽略整个音频声明或其它source字段。layout模式采用现有固定代表性句子，不渲染真实ruby，实际spacing验收采用time模式；27组不能全部称作实际ruby碰撞帧。旧工具三次失败独立保留：37请求/242断言（AudioPath身份）、35/243（nullable来源）、40/477（layout固定definition），不并最终179/2689。最终发布不是这些失败发布的复用。

最终证据为`artifacts/ruby-spacing/root-final-acceptance.json`、`assessment-final4-cc30d4c.json`、`http-final4-cc30d4c-contract.json`及`http-final4-cc30d4c/summary.json`；summary SHA256为`3d95e19feb2759f5cbc31d2b049cf94d3e31a0c118e18826b00247d2272274a6`。部署与证据manifest、排水身份和源码/测试稳定性核验详见主验收JSON。

旧0.96 EngineIdentity拒绝目前由codec/不匹配回归覆盖，不能称实跑旧0.96 Worker二进制；匹配新Worker12/12是真实跨进程结果。共享cluster组内冲突、非单调映射保守保留，完整RTL/竖排、用户变换出画、完整公共B3/B4/B5/B6、浏览器/用户及原性能/长片/GUI/平台矩阵不扩大。


<a id="public-package-preview-0980"></a>

## 62. P2-A直接效果包公共项目预览与完整诊断（0.98.0／合同1.29.0，2026-10-19，有限HTTP验收完成）

### 62.1 实施边界与当前状态

**本批有限HTTP验收已完成，最终证据见§62.5。** 用户要求继续执行下一步，主体编码和验证交子代理、主代理验收；计划`b9f02af`已提交推送，生产实现、Release Oracle、匹配部署及正式HTTP终态均已登记。设计见[计划](../plans/backend-public-package-preview-plan.md)、[ADR-0235](../decisions/decisions.md#adr-0235)，实际行为仍以冻结合同和统一API§20为准。

现有合法工程中的直接包经预览专用selector/组合工厂进入真实受管理Worker；原三静态profile、B4任务、同帧测量、静态模板/候选范围不扩。非空presetApplications、非Color背景/外媒体字体闭包继续拒绝。snapshot/精确修订/音频asset和快resource reservation同Writer捕获，运行引用使用捕获身份；Worker/编码在Writer外。

真实HTTP准入链为Program→PreviewWorkerConfiguration.ResolveAsync提供非null SourceProfiles，capture资源回调返回实现能力接口的ReferencedFactory；其Ensure若inner缺接口明确返回PreviewUnavailable，故accepted包必经实际Ensure。可信手工组合的null SourceProfiles仍属旧兼容，不声称新加显式null拒绝。通过后设置可信进程内VerifiedProfile，Registry再复核project来源/标记/实际完整包闭包；标记不在wire。

三类公共诊断保source十字段、image真实graph与script/source材质owner，compile failure与runtime Error保同期旁报。新像素在完整诊断/预算验证前不可发布，failed无latest；旧成功frame lease保原TTL。公共768项/512KiB，私有runtime与package限额独立，完整分页128/最多32页、超限不截断。私有wire不增加字段；为真实runtime Error旁报修复WorkerProtocol/PackageRenderWorkerContract/proxy三个错误guard，open编译失败与render旁报保持严格操作关联。

公共HTTP不返回完整source SHA；预览代次/修订/序列和PNG hash与内部source SHA/Runtime oracle为不同证据层。

### 62.2 已通过的源码/合同与相关回归

经主代理验收的最终合同重导为 **1.29.0／80 paths／89 methods／463 schemas／91命令／2790例（2149反例）**；旧2648例逐对象、顺序和字段值保持。新增142例含109反例，Source/ImageEffect/Material独立公共DTO，nullable不适用payload省略，旧raw报告新增字段可选。metadata fresh build、freeze write/default check及validate序列exit0，构建零warning/error；证据`artifacts/public-package-preview/contract-final/summary.json`。最终manifest SHA为`c0c3f513145bb3701fe4df97c9e7933e59619cfff482f8bf2e3bd11bf59649bc`，OpenAPI SHA为`24dbf83f1b780558cf0479741f35abe6def27095b4f8ef335efa9fa185bc8efd`；387个合同输入指纹无mismatch，后续行为源修改须再次冻结。

S2实际9条命令全部exit0，七组TRX去重**1920/1920**：Backend.Rendering225、Hosting1131、Server400、Worker80、Runtime包定向48、Rendering owner定向30、Export neutral定向6。1386个输入稳定；772个本轮观察后代身份全部gone，包含构建/测试基础设施，不能称772个Worker。权威`artifacts/public-package-preview/regression-final/summary.json` SHA256为`78b265d9c458851c4db77f1294c6871be6261a0950601fe4ff5caee84125c38b`。 作者局部与mutation不并正式scope；先前Worker旁报guard首跑红及修复恢复、开发合同fixture顺序问题分别保留，不能把失败改写为最终绿灯。

### 62.3 fresh发布、真实HTTP与最终完整性（已完成，详见§62.5）

最终工具、匹配部署、Release Oracle、HTTP请求/断言、诊断/像素/租约/失控矩阵、三变体、source/deployment/evidence SHA与凭证扫描均已在§62.5登记；独立失败目录仍按失败原因保留，不并最终计数。

### 62.4 协作与范围

Agent Mail #97/#98目前均仅登记persisted、未ack；不代表Claude已阅读、接入或完成浏览器/用户验收。旧0.97的1330/1330及HTTP179/2689、旧B3/B4/TTML数字保历史归属，不并本批。完整公共B3/B4、预设包/配方、媒体/可搬移字体、整曲、B5/B6、浏览器/用户与原性能/长片/GUI/平台矩阵保持独立；Linux组排水不构成任意恶意脚本OS sandbox。


### 62.5 最终clean发布与真实HTTP（2026-10-19）

最终工具HEAD `ff6fff1` 已推送。验收使用不可变 `artifacts/public-package-preview/final2-6f4293d/dist`，deployment manifest sourceCommit 为 `6f4293d` 且生产输入匹配；Oracle采用Release配置，SDK、script-refs、Roslyn实际DLL字节SHA与Worker一致。单次证据目录 `artifacts/public-package-preview/final6-ff6fff1` 的真实HTTP **1513请求／14090断言**全部通过，包含59组PNG/完整解码像素、47诊断场景、6组失控矩阵；84个Worker/子进程身份全部排水，3656个证据文件凭证扫描通过。evidence manifest SHA-256：`bfb411ddabf94a5036d521710923fdb2e5167edecd98a60893cce6597099957f`；部署manifest SHA-256：`30d11a581ef7da98c97f9e0ba8ee8aa9c06a74bc36c1efbc412bccc378766e11`。

独立失败证据保留：final2 Debug Oracle artifactHash不匹配、final3项目会话配额耗尽、final5 B4 measurement预期码错误；均未并入最终计数。三变体中missingrefs与historical-original通过，historical-current的静态旧schema实际返回 `SnapshotVersionMismatch`，只记拒绝边界。公共HTTP未提供完整snapshot source SHA，内部Oracle哈希不冒充HTTP字段；完整公共B3/B4、B5/B6、浏览器/用户及性能/平台矩阵仍独立。

合同离线校验亦已完成：`validate.py --http-log` 对 final6 及三变体的 JSON 响应统计为 `checkedJsonResponses=1449+28+15+24=1516`、`checkedSuccessfulJsonRequests=209+9+4+8=230`、`skippedNonJsonOrUnmapped=63+1=64`、`unpaired=0`；合同版本1.29.0、80 paths、89 methods、463 schemas、2790 examples（2149反例）与实际 wire 形状一致。binary/SSE按工具规则跳过，不计入JSON响应。

<a id="b4-p1-package-validation-1300"></a>

## 63. B4-P1 `package-lyrics-scene-v1` validation 有限HTTP切片（合同1.30.0，2026-10-20）

本条只登记已有 package codec／Worker 能力进入正式 validation job 链的有限切片，不代表完整 B3/B4。实现源码基线为 `c031b5b`；匹配 self-contained Server／Worker 发布留在 `artifacts/package-validation-dist-c031/`，合同为 **1.30.0／80 paths／89 methods／463 schemas／2790 examples（2149 negative）**。发布目录的 deployment manifest SHA-256 为 `f17fab9e9921c9baa10b784a9865acd974dbae52541c1a87385513075f749f66`；该文件的 `sourceCommit` 字段记录合同刷新后的 `96ab10b`，但其运行时源码输入与 `c031b5b` 一致，合同刷新提交仅更新合同指纹，不改变运行时输入。

### 63.1 已登记的真实HTTP证据

- 正向匹配发布：`artifacts/package-validation-http-direct5/manifest.json`，**43 requests／158 assertions**，manifest SHA-256 `8336f8ba8d9d74b02eae2bcda0e4ddb84d4a9d494a9fa7cba8f78fcfbb055135`。真实链路公布 package validation capability，打开带直接包的工程，提交 `package-lyrics-scene-v1` validation，等待成功终态，读取不可变 JSON report 与完整 diagnostics；同时通过无效帧计划400、旧 revision 409、跨 client job 404，并确认静态 validation profile 仍公布。Server exit code 0；观察到的 Worker 身份全部排水（`survivingWorkerPids`为空）。
- 负向旧 Worker：`artifacts/package-validation-http-old1/manifest.json`，**12 requests／58 assertions**，manifest SHA-256 `6f39423335116a70e5bb6c5a6f6d87890c6d407677f1fb2610a711da32ff03de`。旧 Worker 在能力／任务边界返回稳定 `commandNotSupported`（HTTP422），未回退 Host 或静态 codec；非法计划、revision 与静态能力边界保持。Server exit code 0，未遗留 Worker。
- 之前 direct2/direct3/direct4、run1/run2/run3 失败目录保留，不并入上述计数；失败原因分别包括 host identity、诊断断言与 stale revision 夹具问题。

### 63.2 当前能力与未完成范围

本切片证明 package profile 的能力协商、精确 revision／owner ACL、任务入队与成功报告／诊断原始字节链路，以及旧 Worker 的明确拒绝。它没有完成 compile/runtime Error、image/material warning、诊断预算失控、取消、deadline、closeAndWait、重启恢复及完整 recovery report 矩阵，也没有证明普通/透明双输出、完整脚本／媒体／字体闭包或任意包执行。preset+package、图片／视频背景、外部媒体、字体闭包、export、analysis、scene measurement、作者写入、浏览器／用户验收、B5/B6 与完整平台／性能矩阵继续独立。

因此 B4-P1 只能标记为“有限真实HTTP切片已登记”；完整 B3/B4 仍未完成。合同、计划和证据分层不能替代上述缺失矩阵。

<a id="b4-p1-package-validation-diagnostics-1300"></a>

## 64. B4-P1 `package-lyrics-scene-v1` validation 诊断有限HTTP切片（合同1.30.0，2026-10-20）

本条接续[§63](#b4-p1-package-validation-1300)，登记同一 package validation 合同和匹配发布上的真实诊断矩阵。它只扩大已实现 validation 链路的可观察失败边界，不代表完整 B4-P1、B3 或 B4。

### 64.1 真实HTTP证据

- 匹配发布证据：`artifacts/package-validation-http-diagnostics8/manifest.json`，**161 requests／552 assertions**，manifest SHA-256 `2b2127babd359d7699a22b903c63ac2270f688b29edc81c7ceae1ad6172960ea`。Server exit code 0，`survivingWorkerPids=[]`。
- 正常 package validation 成功链路和不可变 report／diagnostics 仍通过；同一探针还复核 package capability、静态 validation profile、无效帧计划400、stale revision 409、跨 client job 404 与 owner ACL。
- runtime Error 夹具实际进入失败终态 `validationFailed`，持久 report 的 issue code 为 `ActualPackageRuntimeError`，并保留 runtime diagnostic union、报告 SHA 和失败状态。
- compile 失败夹具实际进入失败终态 `taskExecutionFailed`，其 wire `failureCode` 为 `WorkerProtocolInvalid`，当前没有 validation report；compile source report 的完整传播仍待后续修复。
- image effect 诊断夹具与 material 诊断夹具实际进入失败终态 `validationFailed`；image report 的 issue 与 diagnostic union 保留 `GraphRequiredInputUnavailable`；material report 的 issue 同样保留该错误（其 diagnostics 为空），不把错误误记为 warning，也不伪造空图像或成功帧。
- 超预算夹具实际进入失败终态 `taskExecutionFailed`，wire `failureCode` 为 `WorkerValidationReportBudgetExceeded`。所有上述 job 的 Worker 均在探针结束前排水。

### 64.2 范围边界

本条证明 package validation 对 runtime Error、compile failure、image/material diagnostic failure 和诊断预算拒绝的真实 HTTP／Worker 失败传播与可用报告持久化边界；compile failure 当前没有 report。它没有覆盖取消、deadline、closeAndWait、重启恢复、完整 recovery report、普通／透明双输出，或完整脚本／媒体／字体闭包；preset+package、图片／视频背景、外部媒体、export、analysis、scene measurement、作者写入、浏览器／用户验收、B5/B6 与完整平台／性能矩阵仍独立。

因此 B4-P1 仍只能标记为“有限真实HTTP诊断切片已登记”；§63 的成功／旧 Worker 边界和失败目录保持原历史归属，完整 B3/B4 仍未完成。

<a id="b4-p1-package-validation-0981"></a>

## 65. B4-P1 `package-lyrics-scene-v1` 有限切片收口（产品0.98.1／合同1.30.1，2026-10-20）

本条接续§63～§64，登记编译诊断修复、握手 deadline 修复、null 输入稳定拒绝以及 fresh 匹配发布上的有限 HTTP 生命周期矩阵。它关闭本轮有限切片的验收边界，不代表完整 B4-P1、B3 或 B4。

### 65.1 实现与定向回归

- `WorkerValidationContract.ValidDiagnostic` 现在要求 `source`、`imageEffect`、`material` 三类 payload 恰好一类，拒绝零/多类、错误 `originKind`、runtime 混入，并将 `Revision`、`Graph`、`Material` 及嵌套必填值的 JSON `null` 稳定转为 `WorkerProtocolInvalid`；旧报告省略或显式 `diagnostic:null` 仍可读取。
- 编译失败的 `compileSource` 报告不再被联合校验误拒；报告保留 `source`、`isError`、源码位置和错误码。包能力握手纳入同一任务 deadline，过期稳定映射 `WorkerTaskTimedOut`，用户主动取消仍保持取消语义。
- 正常 solution build 为 **0 warning／0 error**；`StaticValidationTaskTests` **23/23**，`WorkerValidationTests` **42/42**。红测证据保留在 `artifacts/package-validation-compile-fix/`（compile union、handshake timeout、null payload），不把红测计入通过数字。

### 65.2 Fresh 发布与真实 HTTP

- 源码 HEAD `9ec7ca5`，产品 **0.98.1**，合同 **1.30.1**；匹配 self-contained 发布为 `artifacts/package-validation-dist-0981-final3/`，`deployment.json` SHA-256 `334b9e0f4dc6b478ee2bac6e55f4945c0ed05ccde6a5472baacc435ad89303b8`，`sourceCommit=9ec7ca5`，`sourceDirty=false`。Server apphost SHA-256 为 `d6709eea46f986dfff2444df7e680e527874ab71a1cdcde143d650ac246d6ed8`，Worker apphost SHA-256 为 `166abbe5634d2d31409fa56fb65f021b2663678da514326c94e7e6150850c27c`；当前 Release Oracle SHA-256 为 `3d3857b4a8b091e7f13c7ac9886b2a9398865f569e5ad0801c30d057c33319ba`。
- 权威证据为 `artifacts/package-validation-http-0981-final3/manifest.json`，SHA-256 `6e990f275199ca14705efe09113a55f014c573c160020da0e5dbe66ca9833eac`，真实 HTTP **386 requests／1264 assertions**，Server exit code 0。合同离线复核统计 **382 JSON responses／25 successful JSON requests**，合同固定计数为 **80 paths／89 operations／463 schemas／2790 examples（2149 negative）**；4 个未配对文件按工具规则跳过，不计为 JSON 响应。
- 匹配 package validation 成功报告为不可变 JSON，普通/透明各1帧，报告引用的原始 bytes、size 和 SHA 一致；compile failure 现在保存 `compileSource` 报告（`CS1519`、source revision/path/line/column）；runtime、image、material 夹具均保留实际失败报告和诊断。当前 image/material 夹具因故意缺少 `background` 输入而是 `GraphRequiredInputUnavailable` 错误，不能记为 warning 与成功帧共存。
- 生命周期矩阵包含 active cancel、deadline、project close 的 reject→cancelAndWait；验证终态含 **1 succeeded、4 validationFailed、1 WorkerValidationReportBudgetExceeded、1 WorkerTaskTimedOut、2 cancelled**。4 个首次终态排水检查均记录 `liveAtTerminal=[]`；9 个 observed Worker identities、无 `survivorsBeforeForcedCleanup`、无 `forcedCleanup`、无 `survivingObservedProcesses`，证据目录共 **937** 个文件并通过凭证扫描。SIGSTOP 只证明 active job/owned Worker 控制，不声称脚本已执行或完整后代穷尽观察。

### 65.3 完成边界

本轮关闭的是 package validation 能力握手、成功报告、compileSource union、runtime/compile/image/material 失败传播、报告预算拒绝以及 cancel/deadline/close 的有限真实后端链路。仍未完成 restart/recovery、合法 image/material warning 与成功帧共存矩阵、完整普通/透明多帧矩阵、完整脚本／媒体／字体闭包、preset+package、package export/analysis/measurement、公共浏览器/用户验收、B5/B6 和完整性能/长片/GUI/平台矩阵。完整 B3/B4 仍未完成；§63、§64及其失败目录保持历史归属。

<a id="b4-p1-package-validation-warning-recovery-0981"></a>

## 66. B4-P1 `package-lyrics-scene-v1` warning 与 restart/recovery 有限HTTP切片（产品0.98.1／合同1.30.1，2026-10-20）

本条接续§65，补齐两个明确的有限缺口：合法 image/material warning 与普通/透明成功帧共存，以及一次 previous-host restart/recovery 读取。它扩大真实证据边界，但不把 package validation 宣称为完整 B4-P1、B3 或 B4。

### 66.1 夹具、定向回归与发布

- `MixedEffectPackageFixture` 增加 `BackgroundColor` 输入模式，同时保留 `backgroundRequirement=media`。image/material/script-material 夹具在 opaque 输出使用真实背景色继续生成成功帧；transparent 输出使用既有旁路并保留 `TransparentBackgroundDependency` warning。material 图额外保留背景 child，输出仍限制在字形区域，避免把外部媒体闭包误记为已支持。
- `WorkerPackageSessionIntegrationTests` 对 image、material、script-material 各执行 opaque 与 transparent 四个时间点（含回退时刻），实际定向 **3/3**；Worker build 与 Release FixtureOracle build 均 **0 warning／0 error**。该回归证明同进程与真实 package Worker 的帧、诊断和像素结果一致，不证明公共 HTTP 的完整媒体闭包。
- 源码 HEAD `104df8d`，产品仍为 **0.98.1**、合同仍为 **1.30.1**。匹配 self-contained 发布为 `artifacts/package-validation-dist-0981-final4/`，`deployment.json` SHA-256 `72160e7e0b1264c702f4dd89f4d8c765d684b3f74c4df7d83c1ceceaefe81cec`，`sourceCommit=104df8d`、`sourceDirty=false`；Server apphost SHA-256 `d6709eea46f986dfff2444df7e680e527874ab71a1cdcde143d650ac246d6ed8`，Worker apphost SHA-256 `166abbe5634d2d31409fa56fb65f021b2663678da514326c94e7e6150850c27c`，Release FixtureOracle SHA-256 `44e0950895bd99d3bde242f8b6e41df3c04182d448bd606aef4fe8933d7b1782`。

### 66.2 Fresh真实HTTP证据

- 权威证据为 `artifacts/package-validation-http-0981-final4/manifest.json`，SHA-256 `5d3e64e49e2a837b43daa1d6004f10ced292decf5c87bb9d62bcc0577a912901`；真实 HTTP **432 requests／1427 assertions**，最终 Server exit code **0**。证据目录共 **1032** 个文件并通过凭证扫描。
- image warning 与 material warning 都以 `succeeded` 终态登记，普通／透明各 **1** 帧；完整报告和 diagnostics 的原始 bytes、size、SHA 一致，诊断 union 分别保留 `originKind=imageEffect`／`originKind=material` 和 `TransparentBackgroundDependency`，warning 与成功帧共存。
- 一次 previous-host 重启矩阵先保留成功 package 报告，再以受控 SIGSTOP 的活动 package Worker触发正常 Server shutdown。新 Server 具有不同 host identity；旧成功 job 不进入 live ACL，只在 `/api/v1/recovery-jobs/{id}/validation-result` 以同一报告 bytes 读取；活动 job 在 recovery ledger 中为 `cancelled` 且没有报告，并确认没有自动重放。当前 probe 记录两次 Server exit0、**10** 个观察 Worker identity、**4** 个终态前排水检查、`forcedCleanup=[]`、`survivingObservedProcesses=[]`。

### 66.3 边界

本条关闭的是合法 image/material warning 与成功帧共存、一次 previous-host package report recovery 和活动任务 shutdown 持久化。仍未完成多代 crash/restart/recovery、完整普通/透明多帧公共矩阵、外部媒体/字体闭包、preset+package、package export/analysis/measurement、真正作者脚本失控穷尽、浏览器/用户验收、B5/B6 以及完整性能/长片/GUI/平台矩阵；完整 B3/B4 仍未完成。§63～§65及其失败目录保持历史归属。

<a id="b4-p1-package-validation-crash-recovery-0981"></a>

## 67. B4-P1 package validation abrupt crash/recovery有限HTTP切片（产品0.98.1／合同1.30.1，2026-10-20）

本条接续§66，增加一条 G1 abrupt Server crash→G2 recovery 边界；它不把 package validation 宣称为完整 B4-P1、B3 或 B4。当前生产语义保持：正常 orderly shutdown 的活动任务为 `cancelled`，SIGKILL 后未记录终态的活动任务由 `BackendJobLedger` 转为 `unknown`，并带 `internalError`、`canCancel=false`、无报告、不自动重放。

### 67.1 实施与fresh验收

- probe 最终提交 `242cb11`（在 R2 工具提交 `10feebf` 基础上修正 recovered wrapper 的 `previousHostInstanceId` 读取及 manifest scope）；Debug solution 与 Release FixtureOracle build 均 0 warning／0 error；匹配 self-contained 发布为 `artifacts/package-validation-dist-r2-242cb11/`，deployment manifest SHA-256 `0d5b2d4ded28bafa3fbcb9109610bab337b11a4ee08e1307c22e3a1c05d1c0ac`，`sourceCommit=242cb11`、`sourceDirty=false`。
- fresh证据目录 `artifacts/package-validation-http-r2-242cb11/` 的 manifest SHA-256 `6a21089c88c69ce7e4d1d9104eb391c7535dc05d18140e1ba89a32228798e496`；真实HTTP **457 requests／1530 assertions**，manifest `succeeded=true`，最终Server exit code 0。
- 该证据保留§66的一次正常 shutdown/recovery，并新增一次 SIGKILL Server（exit `-9`）后恢复：成功 package report 以相同原始bytes/SHA通过 recovery route 读取；旧 host/client credential 返回401；新 bootstrap profile 可读取 recovery report，live job 404；crash中的活动 package job 在G2为 `unknown`、`internalError`、`canCancel=false`、无 `validationResult`，且没有Worker自动重放。
- 进程证据记录12个观察对象（12个Worker identity，PID/startTicks/父身份）；4个控制任务首次终态排水检查均通过，crash worker在SIGCONT后观察到排水；`serverRuns`含正常shutdown exit0、crash exit-9、final shutdown exit0，`forcedCleanup=[]`、`survivorsBeforeForcedCleanup=[]`、`survivingObservedProcesses=[]`；证据目录1087个文件凭证扫描通过。

### 67.2 边界

本条关闭的是一次 abrupt crash/restart/recovery 的成功报告读取、旧凭证拒绝、活动任务 unknown 持久化和观察到的Worker排水。它仍不覆盖多次 crash generation、任意Worker阶段、crash前未观察到的后代穷尽、报告损坏恢复、脚本失控、完整多帧普通/透明矩阵、外部媒体/字体闭包、preset+package、package export/analysis/measurement、浏览器/用户验收、B5/B6及完整性能/长片/GUI/平台矩阵；完整B3/B4仍未完成。§63～§66及失败目录保持原历史归属。

<a id="b4-p1-package-validation-warning-multiframe-0981"></a>

## 68. B4-P1 package validation image/material warning 多帧有限HTTP切片（产品0.98.1／合同1.30.1，2026-10-20）

本条接续§67，只扩展公共 HTTP warning 夹具的固定多帧验证与诊断 union 断言，不改变生产执行语义，也不把合法 BackgroundColor 夹具扩大解释为外部媒体或字体闭包。

### 68.1 实施与fresh验收

- 公共 probe 提交 69481ed；image/material warning 请求固定 framesPerOutput=4。每个任务终态validationResult严格断言 opaqueFrames=4、transparentFrames=4、终态 succeeded，所有 issue 都是 TransparentBackgroundDependency 的非错误 warning，severity=warning，originKind 分别为 imageEffect／material；对应 union 分支独占非空，其他 source／imageEffect／material 分支为 null，且 inputId=background。material 允许保留多条 issue，不按帧数要求 warning 条数。
- 匹配 self-contained 发布为 artifacts/package-validation-dist-r3-69481ed/，deployment manifest SHA-256 686fccd0a8c7decdfa7943b86160bdee225748661751879b19026b9fa0006e5b，sourceCommit=69481ed、sourceDirty=false。fresh HTTP 证据为 artifacts/package-validation-http-multiframe-69481ed/manifest.json，manifest SHA-256 04ba72c33c6664f1aa93fd01eb0f2af3cf9797af46134d8eb4b89f937c0bfa57。
- 真实 HTTP 474 requests／1597 assertions，succeeded=true、最终 Server exit0。image 报告为普通／透明各4帧、1条 imageEffect warning；material 报告为普通／透明各4帧、2条 material warning；本轮 warning helper 在运行中核对终态帧计数与报告 SHA，未请求这两个 warning job 的 diagnostics page，也未在 helper 中断言报告 sizeBytes 或报告正文帧计数。后续独立只读审计为 `artifacts/package-validation-http-multiframe-69481ed/independent-r3-warning-report-assessment-20261020.json`，SHA-256 `2c2e83e3ceb37e1352fd10b7cd43aaaa663c9e7ee28ec6d57c84a4813758fb08`：image773B／4+4帧／1issue、material1833B／4+4帧／2issues的sidecar、原HTTP下载及终态reference之间bytes/size/SHA/counts一致；不能记为本 probe 的 diagnostics page 验证。原probe当时1121个文件凭证扫描通过，新增独立assessment不计入该数。
- 进程证据记录12个观察 Worker identity；4个控制任务首次终态排水检查与1个 crash 排水检查均通过，serverRuns 含 orderly exit0、crash exit-9、final exit0，forcedCleanup=[]、survivorsBeforeForcedCleanup=[]、survivingObservedProcesses=[]。§67 的 crash/recovery 断言在本轮 fresh 复跑中保持通过。

### 68.2 边界

本条关闭 image/material warning 的固定四帧公共 HTTP 矩阵与结构化 warning union 断言。仍未完成完整普通／透明多帧公共矩阵、外部图片／视频／字体闭包、preset+package、package export/analysis/measurement、脚本失控穷尽、浏览器／用户验收、B5/B6及完整性能／长片／GUI／平台矩阵；完整B3/B4仍未完成。§63～§67及其失败目录保持原历史归属。


<a id="b4-p1-package-validation-success-multiframe-0981"></a>

## 69. B4-P1 package validation 主成功路径多帧有限HTTP切片（产品0.98.1／合同1.30.1，2026-10-20）

本条接续§68，只把公共基础成功 package validation 扩展为固定普通／透明各4帧，并收紧已有成功 helper 的输入与报告一致性断言。生产执行语义及合同保持不变；本条不是完整 B4-P1、B3 或 B4。

### 69.1 实施与fresh验收

- probe 提交 `0fdf080`：`package-validation-scale` 请求 `framesPerOutput=4`，成功 helper 断言 `sourceKind=project`、sourceId/revision/profile/frame plan、64字符小写十六进制 source SHA 格式（未由独立Oracle重算）、320×180 画布、30/1 fps、700000us，以及成功终态进度 completedFrames=totalFrames=8。不可变 report 的原始 bytes/size/SHA、正文 `succeeded=true`、普通／透明各4帧和完整 diagnostics page 的语义内容均与终态引用一致。
- 匹配 self-contained 发布为 `artifacts/package-validation-dist-r4-0fdf080/`，deployment manifest SHA-256 `2699a2f5957e629297ef97349220482a291549ef18851f83cce9a5506d054435`，`sourceCommit=0fdf080`、`sourceDirty=false`。fresh HTTP证据为 `artifacts/package-validation-http-success-multiframe-0fdf080/manifest.json`，manifest SHA-256 `706cb5851adc5be5dbbe7256f28ea27cdd1950616a62560d2d7558af55de6330`。
- 独立只读审计为 `artifacts/package-validation-http-success-multiframe-0fdf080/independent-acceptance-assessment-20261020.json`，SHA-256 `0acbc7c1c4cfbd2fc729edae45ce62a15f8731b3f3d265ecc330455080d43229`。6份sidecar的size/hash/frame/result全部一致，其中5份有匹配HTTP下载；compile报告仅在sidecar存在，probe未通过authenticated report route下载。r3/r4发布各806文件均与发布manifest hash匹配。该审计不重跑HTTP、不覆盖原manifest，与运行期helper断言分开记账。
- 真实 HTTP **450 requests／1533 assertions**，`succeeded=true`，最终 Server exit0。基础 scale 成功报告普通／透明各4帧、2条 issue、26条 runtime diagnostics；image/material warning 保持§68的各4+4帧与结构化非错误 warning，warning helper 的验证边界仍按§68，不冒称读取其 diagnostics page。crash 前新建的 `package-crash-success` 仍是普通／透明各1帧，其报告 bytes/SHA recovery 语义保持。
- 最终合同离线校验报告为 `artifacts/api-contract/package-success-multiframe-final-validation.json`：合同1.30.1、80 paths／89 operations／463 schemas／2790 examples（2149 negative）通过；该检查未重跑HTTP，不并入450／1533计数。
- 12个观察 Worker identity、4个控制任务首次终态排水检查、1个 crash 排水检查均通过；本轮观察树只有Worker本体，不记为实际native子进程样本。crash后由探针发出SIGCONT，Worker观察pipe EOF退出，不代表崩溃Host主动排水。Server依次 orderly exit0、crash exit-9、final exit0，`forcedCleanup=[]`、`survivorsBeforeForcedCleanup=[]`、`survivingObservedProcesses=[]`。原probe当时证据目录1073个文件凭证扫描通过；后增独立assessment不计入该扫描文件数。

### 69.2 边界

本条只关闭主成功路径的固定4+4帧公共 HTTP 证据与已有成功 helper 的输入、进度、不可变报告和 diagnostics page 一致性。完整普通／透明多帧矩阵、完整 warning 输入闭包、多次 crash generation、任意Worker阶段、外部图片／视频／字体闭包、preset+package、package export/analysis/measurement、脚本失控穷尽、浏览器／用户验收、B5/B6及完整性能／长片／GUI／平台矩阵仍独立；完整B3/B4仍未完成。§63～§68及失败目录保持原历史归属。

<a id="b4-p2-package-no-audio-export"></a>

## 70. B4-P2 `package-lyrics-scene-v1` 无音轨导出代码切片（2026-10-21，未做真实HTTP验收）

本条登记当前 `dev/backend-development` 分支上的最小代码接缝，不能把静态编译或定向 Hosting/Worker 回归当作完整导出或真实 HTTP 交付。既有 `export-jobs`、`export-result`、`export-content`、`export-diagnostics` 路由、唯一 job/ledger、artifact storage、read lease、认证与恢复读取语义保持复用，没有新增路由或第二套任务注册表。

- `package-lyrics-scene-v1` 现在可作为导出来源能力显式协商；Host 从工程完整效果包快照使用 `PackageProjectSnapshotCodec` 固定 source SHA，Worker 同样按 package codec 解码、重编码校验并沿既有候选→报告→artifact 原子提交链执行。现有 static profile 的来源选择和拒绝边界保持。
- 本切片只允许 `includeAudio:false`。Host admission 与 Worker metadata 两层都明确拒绝 `includeAudio:true`；纯视频仍保留工程内音频声明，不读取声明路径，不申请音频 grant。短片、画布、帧计划、报告预算、取消/deadline、artifact hash/size、资源租约及下载 ACL 沿既有静态导出合同。
- 定向回归覆盖 package codec/metadata 无音轨 roundtrip、带音轨稳定拒绝、Worker 把 package bytes 冒充 static profile 的 source mismatch、Hosting package admission 及旧 static profile 保持。Worker/Hosting 相关构建为零警告/错误；本条没有 fresh self-contained 发布、真实 HTTP/SSE、PNG/视频像素 oracle 或完整进程矩阵证据。

仍明确拒绝或未接入：package 带音轨导出、图片/视频/字体资源闭包、preset+package、package-aware analysis/scene measurement、连续预览与跨任务资源租约闭环、B5 作者草稿→源码编译→采用→发布、浏览器/跨平台/完整工程验收。该切片也不扩大现有 package validation 的有限能力，不宣称完整 B4、B5 或 B6。

<a id="b4-p2-package-resource-closure-1302"></a>

## 71. B4-P2 package 资源闭包可观察化有限切片（产品0.98.1／合同1.30.2，2026-10-21）

本条把工程资源清单从“包被引用”扩展为可审计的 manifest/file 闭包投影，为连续预览、导出和后续资源租约设计提供稳定身份。它只补读模型与合同字段，不改变 package 编译、执行或任务生命周期，也不把资源清单的 `validated` 状态解释成完整可运行能力。

### 71.1 实施内容

- `ProjectResourceDto` 新增可选 `packageId`、`packageRevision`、`packagePath`、`packageRole` 与 `packageClosure`；新增 `ProjectPackageClosureSummaryDto`。普通音频、背景、分析和模板资源保持原身份与状态语义。
- 工程效果包和预设快照中的包均展开为 `effectPackageManifest` 与 `effectPackageFile`。manifest 的身份为 `packageManifest:{packageRevision}`，文件身份为 `packageFile:{packageRevision}:{packagePath}`；`roles` 按引用来源合并、去重并稳定排序。包文件公开实际 role，未知 role 保留在清单中并标为 `unsupported`。
- `PackageProjectSnapshotCodec.InspectResourceClosure` 在内存中重建同一包，重新检查 manifest/files identity、文件长度与 SHA、包定义引用和内容预算。当前读模型可识别 `asset`、`csharp`、`graph`、`image`、`shader`、`shaderProgram`；`packageClosure.status` 为 `validated` 只表示这些有限检查和 role 识别都通过。`validatedFileCount` 是可识别 role 的文件数，不能单独推出包身份或引用校验成功。
- `packageClosure` 只出现在 manifest 项，包含 manifest/总大小、manifest SHA、文件计数及 validated/unsupported 计数；包资源不提供 assetId 或 contentUrl。该投影不会枚举主机路径、外部图片/视频/字体，也不执行作者代码、编译 C#、解码图片或打开媒体。

### 71.2 定向验证

- `MakoKaraGenerator.Backend.Rendering.Tests` 的 `PackageProjectSnapshotCodecTests`：**27/27**，覆盖合法闭包、manifest/file identity、SHA/长度、已知/未知 role、定义引用和预算边界。
- `MakoKaraGenerator.Backend.Hosting.Tests` 的 `BackendBasicMediaTests`：**6/6**，覆盖工程包与预设包的清单展开、稳定 package revision/path/role、closure summary 计数及 unsupported 状态。
- `MakoKaraGenerator.Backend.Server.Tests` 的 `HTTP资源清单公开效果包闭包身份role和有限摘要`：**1/1**，验证 in-process HTTP transport 返回 manifest/file identity、role、summary counts 与 unsupported 条目。该回归不是 fresh self-contained Server/Worker 发布，也没有下载包文件或验证连续预览租约。

合同工具已补充 `ProjectPackageClosureSummaryDto`、package 字段、`validated`／`unsupported` 状态及对应正反例；合同校验与生成物计数需以本轮 1.30.2 的最终 manifest 为准。该离线合同检查不替代真实 HTTP、Worker、PNG、资源下载 ACL 或跨任务租约验收。

### 71.3 边界与下一步

本条仍未完成 package 图片/视频/字体资源闭包、preset+package、package-aware analysis/scene measurement、package 连续预览的跨任务资源租约闭环、无音轨 package 导出的 fresh HTTP 矩阵、B5 作者草稿→源码编译→采用→发布，以及 B6 分发/浏览器/跨平台/完整工程验收。`validated` 仅是当前只读闭包检查结果；它不证明编译、运行、图片解码、字体加载、视频读取、导出或分析已经可用。§65～§70 的有限 HTTP 证据和失败目录保持各自历史归属，不与本条定向回归计数合并。

<a id="b4-p2-package-continuous-preview-1302"></a>

## 72. B4-P2 package 连续预览代次回归有限切片（产品0.98.1／合同1.30.2，2026-10-21）

本条接续已有直接效果包公共预览链路，验证连续取帧时的同代次复用、换代淘汰和关闭排水。实现提交为 `4bc0959`；它只扩展 Server transport 与本地 probe 的回归覆盖，不新增路由或把 in-process 回归提升为真实发布验收。

### 72.1 覆盖矩阵

- 同一 package preview session 的连续前向帧沿同一 Worker 会话复用；换尺寸或透明度会产生新 generation，旧 generation 的待处理帧和句柄被淘汰。
- 旧帧句柄在换代后不可读取；旧 generation 请求返回稳定冲突；向后 seek 创建新 generation，并保持新代次帧的发布顺序。
- 高频连续请求只保留一个执行帧与一个最新 pending 帧，迟到帧不覆盖当前代次；close 后帧/诊断资源读取失效，随后可重新建立 session。
- Worker 创建、代次切换、关闭和资源释放均完成排水；现有租约/ACL规则保持，尚未把跨任务资源租约闭环扩展到外部图片、视频或字体。

### 72.2 定向验证与边界

`MakoKaraGenerator.Backend.Server.Tests` 连续 package preview 定向测试 **1/1**；公共 preview lifecycle probe 的 Python 语法检查通过，`git diff --check` 通过。README 已明确该回归不计为 fresh self-contained Server/Worker HTTP、浏览器或资源闭包验收证据。

本条仍未完成普通/透明完整多帧公共 HTTP、warning/error 全组合、seek/resize 长时间压力、lease expiry/restart/recovery 多代矩阵、外部图片/视频/字体闭包、preset+package、package export/analysis/measurement、B5/B6 或跨平台浏览器验收。当前结果证明已有 package preview transport 的 generation/关闭语义，不能单独证明连续播放产品体验或完整资源租约闭环。

<a id="b4-p2-package-resource-asset-lease-1310"></a>

## 73. B4-P2 package 资源受控 asset 与租约有限切片（产品0.98.1／合同1.31.0，2026-10-21）

本条在§71的闭包读模型上增加一个受控写入入口：客户端可以把已经通过有限闭包检查的包内 manifest/file 惰性投影为当前客户端的不可变 asset，再复用既有认证读取和 TTL。它建立了 package 资源从“可观察引用”到“可授权字节”的最小后端接口，但不扩大 package 执行能力。

### 73.1 接口与生命周期

- 新增 `POST /api/v1/project-sessions/{id}/resources/package-asset`，请求 `expectedRevision/resourceId/metadata`，成功返回 `ProjectPackageResourceAssetResponse`。服务端只接受当前 client 拥有的精确工程修订、清单中已验证的 manifest/file resourceId 和支持 role，不接受裸路径、未知包、unsupported role 或外部文件。
- 包资源按 client、工程会话和 resourceId 惰性复用同一 asset；内容复制到现有 `ResourceStore` 后使用现有 asset content GET/HEAD、Range、ETag、304、读取引用和未引用 TTL。响应不含主机路径，`contentUrl` 仍要求 Authorization 和当前 client。
- 工程关闭、客户端回收或 registry 停止时释放工程持有的 package asset 引用；正在进行的内容读取由 `ResourceReadLease` 保持，引用释放后按资源TTL清理。工程修订冲突、跨client会话访问、未知resourceId和过期内容保持稳定错误。
- `PreviewSessionResponse.packageClosure` 现在返回固定直接 package preview 的闭包摘要（closureRevision、包身份、文件数/总字节/角色），随预览输入租约和会话 ACL 失效；该摘要不暴露包原始字节。

### 73.2 定向验证

- `MakoKaraGenerator.Backend.Hosting.Tests` package asset lease：**7/7**，覆盖同资源复用、client ACL、引用释放和TTL清理。
- `MakoKaraGenerator.Backend.Server.Tests` package resource asset transport：**2/2**，覆盖manifest/file内容读取、receipt幂等、精确修订、跨client拒绝、工程关闭后的TTL过期；package continuous preview矩阵§72新增用例后为**3/3**，覆盖租约到期/恢复、ACL、迟到Worker帧换代。
- Contracts metadata、OpenAPI、api-index、examples与manifest已重生成并通过合同校验：**1.31.0，81 paths／90 methods／468 schemas／2794 examples（2150反例）**。该离线校验和in-process回归不等同fresh self-contained HTTP、浏览器或完整媒体闭包验收。

### 73.3 边界

本条仍未完成匹配发布上的fresh HTTP资源文件HEAD/GET/Range/ETag完整矩阵、连续普通/透明多帧与SSE长时压力、跨重启恢复、外部图片/视频/字体闭包、preset+package、package带音轨导出、package-aware analysis/scene measurement、B5/B6以及浏览器/跨平台/完整工程验收。当前证明的是受控包内原始字节的后端ACL/TTL接缝和in-process行为，不是图片/视频/字体解码或完整B3/B4关闭。

<a id="b4-p2-package-resource-closure-http-1310"></a>

## 74. B4-P2 package 资源闭包与连续预览 fresh HTTP 有限矩阵（产品0.98.1／合同1.31.0，2026-10-21）

本条把§73的受控 asset 接口和§72的连续预览回归放入匹配 self-contained Server/Worker 进行一次真实 HTTP 切片。运行时发布目录为 `artifacts/backend-development-dist-4758b96-package-closure/`，`deployment.json` SHA-256 为 `49f9a12cc4b231f7b13a58dface8f846f7251398bd957d74b88f1e02e34ab6f5`，`sourceCommit=c010648`、`sourceDirty=false`；probe 工具最终提交为 `bd237a9`。

### 74.1 资源闭包与预览矩阵

- `assets` 阶段对当前 package 工程修订的 manifest 与全部三个 validated 文件逐项调用 `POST /api/v1/project-sessions/{id}/resources/package-asset`，验证 receipt 幂等、精确 project revision/resource identity、package path/role、资源投影 size/SHA、GET 完整字节和 content type、HEAD、合法与越界 Range、ETag/304，以及匿名和跨 client 读取拒绝。
- 独立 `sprite` 工程继续验证 `image` role 的 `image/png` asset 投影、GET/HEAD/Range/ETag、跨 client `resourceUnauthorized` 和关闭后的 TTL 过期，证明当前包内图片字节可以进入同一受控 ResourceStore。
- `lifecycle` 阶段验证同一 package 工程的资源闭包读模型、预览会话幂等、SSE frameReady、普通/透明各四帧、resize/透明度换代、旧帧句柄和旧 diagnostics cursor 失效、项目修订切换、seek 新 generation、关闭后重建，以及 project close 后受控 package asset 的 TTL 过期。
- 本次 fresh 输入覆盖 `scale` 的 `asset`、`csharp`、`manifest` role 以及 `sprite` 的 `image` role；外部图片/视频/字体闭包、解码和跨平台字体加载仍不在本条范围。

### 74.2 真实 HTTP 证据

- 权威证据目录为 `artifacts/backend-development-http-package-closure-bd237a9-r5/`；`summary.json` SHA-256 为 `c65f367a03c8c48401ddcf0d0a1e3e95e44043dc21bdea83df61bcdb85117e0b`，`evidence-manifest.json` SHA-256 为 `f4889009bd9f2028b99c337e82aaa9b3889689e515154a82707d0b94c704afe7`。
- 真实 HTTP **295 requests／1863 assertions** 通过，`status=passed`、最终 Server exit code 0。证据目录共 **762** 个文件，其中 **760** 个纳入凭证扫描；发现的 **36** 个观察进程 identity 全部排水，`remaining=[]`，无强制清理残留。
- 合同离线校验仍为 **1.31.0／81 paths／90 operations／468 schemas／2794 examples（2150 negative）**；该检查不并入197／1303，也不替代浏览器、长时压力或完整 B3/B4 验收。

### 74.3 边界与下一步

本条关闭当前包内原始资源投影和连续预览的一个真实 HTTP 主成功切片，不能解释为完整资源闭包。仍需按 gap audit §13 推进 package 无音轨导出的 fresh HTTP、图片/视频/字体资源闭包及其解码/租约、preset+package、package-aware analysis/scene measurement、B5 作者草稿到发布、B6 分发/浏览器/跨平台和完整工程验收。普通/透明长时多帧、跨重启多代恢复及完整 warning/error 组合也仍独立。

## 75. B4-P2 package resource Worker video/font capability seam（2026-10-21，私有接缝）

本条在现有 `package-resource-closure-v1` grant/lease 边界内补齐 video 与 font 的受控 Worker 能力；不新增公共 HTTP 路由，不修改 package export、作者工作流或既有资源提交文件。Worker capability 现在声明 `ffmpeg-video-metadata-v1`、`ffmpeg-video-frame-bgra-v1` 和 `skia-font-shape-v1`，字体 provider identity 固定为 `skia-harfbuzz-v1`。

- `video` grant 在 Worker 私有 staging 文件上探测有限容器/视频元数据，并读取时间 0 的 BGRA 首帧摘要；视频时长上限 30 分钟，首帧解码上限 16 MiB，回执包含宽高、codec、是否含音频、首帧字节数、SHA-256 和帧时刻。
- `font` grant 仅从固定字节建立 Skia typeface，以固定文本 `MakoKara` 经 HarfBuzz 整形，回执包含族名、字形数、shape profile 和 advance；不发布 native provider、主机路径或可执行对象。
- Worker/Hosting 定向回归覆盖真实 testsrc 视频和真实系统字体：Worker package resource tests **104/104**，Hosting real Worker proxy resource tests **2/2**；Worker build **0 warning / 0 error**。

该条证明的是受控 Worker 内的短视频元数据/首帧和字体 provider/固定文本整形接缝，不是完整外部媒体闭包、连续预览、验证、导出、分析、跨任务租约、跨平台像素等价或 fresh HTTP。下一阶段应把能力接入具体任务调用链，并为其建立匹配发布上的真实 HTTP、取消/超时、资源替换和排水证据。

<a id="b4-p2-package-no-audio-export-http-0981"></a>

## 76. B4-P2 package 无音轨导出 fresh HTTP 有限矩阵（产品0.98.1／合同1.32.0，2026-10-22）

本条把§70的 `package-lyrics-scene-v1` 无音轨导出代码接缝放入验收源码基线 `4e0f909` 的匹配 self-contained Server/Worker，完成一次新的真实 HTTP 验收。它只收口直接效果包的无音轨短片导出，不扩大 package 的媒体、字体、分析或作者能力。

### 76.1 发布与成功矩阵

- 匹配发布目录为 `artifacts/package-export-20261022/dist/`，`deployment.json` 记录 `sourceCommit=4e0f909236c733201520a8b7436fee82e94ca137`、`sourceDirty=false`、产品 `0.98.1`，包含1006个部署文件；`deployment.json` SHA-256 为 `e771e030bbe69d715fbb6404cab41b772733f92efe5c05a66990ddb029199ad1`。FixtureOracle 使用同一验收源码 Release 构建，发布目录在验收期间保持不变。
- fresh HTTP 证据目录为 `artifacts/package-export-20261022/http/`；probe 通过，**457 requests／2575 assertions**，1097个证据文件参与凭证扫描；`evidence-manifest.json` SHA-256 为 `2594e9573cc59889465c918453b61891f1686a0dc5a32307e24bddf40ed1b193`。普通 `opaqueMp4` 与透明 `transparentProRes4444` 均成功完成21帧；report 与 artifact 的 bytes、size、SHA 在job ledger、报告正文和认证下载之间一致。
- 独立 `ffprobe` 核对视频帧数和无音频流；独立 `ffmpeg` 将全部帧解码为BGRA，核对每帧完整字节长度、帧数和透明输出的alpha覆盖；本次未将导出视频逐帧与 Runtime 内容 oracle 比较，不宣称完整像素一致。BMFF顶层 `ftyp` 检查、SSE terminal 事件、认证 ACL、匿名/跨client拒绝、Range、HEAD、ETag/304和诊断分页均通过。
- `includeAudio:true` 稳定拒绝；长控制夹具的实际 Worker cancel、deadline（`WorkerExportTimedOut`）、project close 的 `reject`/`cancelAndWait`、一次 previous-host orderly restart/recovery 和成功报告/媒体读取均通过。两次 Server exit0，shutdown 记录 `remaining=[]`，`summary.json` 记录的50个观察身份全部排水，无强制清理残留。

### 76.2 定向回归与边界

该验收源码基线的 Debug solution build 为0 warnings／0 errors；完整 solution tests 全部通过，Hosting **1150/1150**、Server **407/407**、Rendering **227/227**、Worker **105/105**。Worker capability generation 修复与回归见提交 `4e0f909`；无音轨导出独立媒体校验探针见提交 `37b3cce`。合同离线校验为1.32.0、87 paths／97 operations／481 schemas／2818 examples（2161 negative），不并入本条HTTP计数。

本条仍不完成 package 带音轨导出、外部图片/视频/字体资源闭包及其跨任务租约、`preset+package`、package-aware analysis/scene measurement、完整 B4、B5 作者草稿到发布、B6 分发/浏览器/跨平台、性能/长片/GUI/平台矩阵。普通/透明长时多帧、crash 多代恢复和浏览器用户验收仍需独立证据；代码、定向测试和本次有限HTTP不能解释为完整 B3/B4/B5/B6。


<a id="project-image-preview-0990"></a>

## 77. 已保存工程图片背景受管预览实现与合同（产品0.99.0／合同1.33.0，2026-10-23）

### 77.1 有限实现

已保存并由ProjectStore加载的Image主背景，通过原project create/input预览路由固定为独立 `project-image-preview-v1` 来源。Host在Writer固定snapshot和短期图片读引用，Writer外从loaded-input核完整内容地址、no-link、size/SHA；Worker使用独立image能力与open，在同一实际进程完整解码固定bytes，并由原Runtime/Skia背景/图输入规则消费。原图2MiB、单边4096、BGRA16MiB、含base64完整envelope4194280字节，完整基础源保原codec门禁。正常/失败/取消/换代/close沿原持久Worker、音频reservation、frame lease和native对象所有权排水。

只包裹无预设的static/static-analysis/直接package基础源，不开放video/font、图片与preset或模板候选/固定试样，不把图片加入validation/export/measurement或原静态allowlist。公共请求无新增profile/bytes/path/grant；runtime-info的singular兼容值保持，sourceProfiles只在独立实际Worker探测后追加图片。失败码和完整边界见[统一API§22](../../architecture/backend-api.md#project-image-preview-0990)、[计划](../plans/backend-project-image-preview-plan.md)与[ADR-0236](../decisions/decisions.md#adr-0236)。

### 77.2 本地分层证据

已执行有限codec变异：仅移除媒体SHA比较，wrongHash负例红1/24；精确恢复后24/24绿，恢复前后/绿测后源码SHA一致。原始assessment/TRX/log见 `artifacts/project-image-preview-local/mutation/`，该组是定向子集，不与最终整合回归重复相加。最终合同从稳定当前源码重编metadata、freeze/write/check/validate，证据置于 `artifacts/project-image-preview-local/contract/`；最终通过87路径/97方法/481 schemas/91命令、2848例（2175反例），原2821例完整保留；ContractMetadata Release构建0警告/0错误，freeze/write/check、venv validate、py_compile与git diff --check通过。根代理最终0.99.0整合的solution构建0警告/0错误；Hosting预览相关152/152、Server预览相关371/371、图片Rendering定向39/39通过，均为有限筛选范围，不声称全模块/完整solution tests；codec24例包含于Rendering39例，不重复相加。最终Runtime Worker兼容/图片/失控49/49通过，四个独立程序集的本轮有限范围合计611/611（152+371+39+49）。原始TRX/log与assessment位于 `artifacts/project-image-preview-0990-acceptance/regressions/`；此前266/10/7局部运行及mutation保留各自归属，不重复累加。

### 77.3 匹配发布fresh HTTP有限验收

匹配self-contained Server/Worker发布源码 `d961895`、`sourceDirty=false`；fresh HTTP `artifacts/project-image-preview-0990-acceptance/http-final6/` 通过1175请求／9760断言。static/static-analysis/直接package普通与透明各4帧及回退seek、40组原Runtime完整PNG/像素比较、Initialize/Evaluate×cancel/deadline/close六组实际Worker/child控制与排水通过；Server exit0、105个观察身份（含83个Worker身份）全部退出，2752个证据文件凭证扫描通过。

匹配发布位于 `artifacts/project-image-preview-0990-acceptance/dist/`，`deployment.json` 固定生产源码 `d961895f619657c44dda97e66c6595da0184872b`、Release linux-x64 self-contained及 `sourceDirty=false`。探针工具冻结提交为 `353e390`；该工具提交不改变生产发布基线。后续工具或文档提交不重标发布源码；HTTP运行前核对当前全部生产输入与不可变deployment manifest一致。

final6覆盖三基础源各普通/透明4帧及各自回退seek，完整PNG字节与独立原Runtime Oracle相等，并核完整解码PNG BGRA对应，共40组（含resize、input换代、固定输入与工程关闭后继续预览等附加组）。普通帧验证图片背景反事实贡献，直接包另核关闭包效果的反事实；透明输出按原Runtime语义，不把背景贡献断言强加给透明帧。static-analysis固定保存原Available分析及canonical身份，这证明该声明保留，不代表新音频分析或音频驱动画面。原始BGRA与保存project.json SHA只记录Oracle身份，后者不是image envelope SHA；公共HTTP没有完整源SHA，不声称核对了公共完整源SHA。38组诊断中compileSource/imageEffect/material为完整有序精确列表；runtime逐条包含Oracle期望occurrence（含artifactHash），并单独校验实际epoch/sequence连续性，不声称完整runtime数组严格相等。

HTTP还覆盖能力、精确修订/ACL、认证SSE、坏图解码失败、源文件与固定loaded-input变化/缺失、capture后固定bytes稳定、resize/input/generation、含Available分析的工程成功输入、无音频工程close后固定预览、Host shutdown。音频引用生命周期按本地回归证据登记，不泛化为HTTP关闭有音频工程后继续预览已测。4项文件变异均为探针拥有fixture的内部故障注入，不是公开媒体编辑API。Initialize/Evaluate分别执行input-replacement cancel、deadline、explicit close六组，核对真实marker、Worker与child身份、并行健康图片帧及恢复；六组均排水，deadline要求稳定Worker超时代码且无成功像素。29个open/close job终态另列，不当作导出/验证任务。

`image-shutdown.json`记录Server exit0、105个观察身份和 `remaining=[]`；summary中的83个Worker身份为该105个身份的子集，不相加。`evidence-manifest.json`固定2753个文件，凭证扫描2752个证据文件；manifest SHA-256为 `50014625656932d861878122c0f01473c23724b92f698c099aaeae27a48022e9`。离线合同留档校验 `http-final6-contract-validation.json`通过1129份JSON响应、166份成功JSON请求，40项非JSON/未映射跳过、未配对0；该离线检查不增加1175/9760。

根独立复核 `artifacts/project-image-preview-0990-acceptance/root-final6-acceptance.json`重新核验857个生产输入、1006个发布文件、436个Oracle文件、359项三方依赖及2753个证据文件SHA；40份实际下载PNG重新经ffmpeg完整解码，核长度及其与原Oracle的PNG/SHA对应；六组控制、105个观察身份当前全gone、Server exit0与资源清理再次核对通过。该复核不重跑611项本地回归，也不增加HTTP覆盖。

### 77.4 Oracle身份与历史失败归属

最终 `build_oracle.py`只构建工具入口，无生产ProjectReference，显式引用不可变dist Worker DLL，保留独立ProjectStore.Load→原Runtime期望，HTTP输出从不构造期望像素。`oracle-final6/input-manifest.json`固定构建命令、工具/生成项目/发布输入；`http-final6/oracle-input-identity.json`在HTTP前要求192个相邻依赖程序集和167个framework refs共359项与Worker/deployment三方逐字节一致，包含真正的脚本SDK与Roslyn。产品版本、Release标签或refs相同不能替代完整依赖SHA。独立 `oracle-equivalence/assessment.json`的188项预检无HTTP，不计入final6。

保留原 `http/`失败118请求／1014断言：直接包普通首帧的原Runtime occurrence字段精确比较失败。随后发现源码版本、构建路径及发布方式不同的Oracle/Worker依赖字节不一致；PathMap尝试未解决，不把任一因素臆定为全部根因。`http-final2/3/4`各为HTTP前依赖身份guard拒绝、0请求／8断言，不能称真实HTTP失败或验收通过。`http-final5`通过419请求／5346断言，缺static-analysis与六组控制，保留其独立有限归属；最终以final6覆盖本次完整有限矩阵，数字不累加、不覆盖旧目录。

视频/字体、图片与preset、validation/export/measurement图片输入、完整B3/B4/B5/B6、浏览器/跨平台、性能长片和完整工程验收继续独立。


<a id="project-image-validation-01000"></a>

## 78. 已保存工程图片正式验证fresh HTTP有限切片（产品0.100.0／合同1.34.0，2026-10-24）

已按[独立计划](../plans/backend-project-image-validation-plan.md)完成已有validation job的图片有限接缝：仅无预设static/static-analysis/直接package基础工程的已保存Image主背景。source profile为`project-image-validation-v1`，capability kind为`project-image-validation-capabilities-v1`，task kind为`task-project-image-validation-v1`／`KTI1`；envelope codec仍为`project-image-preview-v1`，decoder profile是`memory-image-bgra-v1`。完整任务输入4,194,272字节（4MiB−32），base snapshot保原4MiB／200000 source values，图片2MiB／4096单边／BGRA16MiB，不能使用preview的4,194,280字节输入上限。

Host第一次Writer捕获精确snapshot、短期图片读lease与音频reference，Writer外核无链接、完整size/SHA及封包预算；第二次Writer须重新核owner、注册session对象身份、revision及ClosePending，之后才取得reservation并登记任务。所有读取、编码、预算、二次门禁、配额和StartJob失败均释放未转移lease/reference/reservation；接受后由work在实际Worker/native排水后释放。Worker以独立能力门禁和每Runtime session独占内存resolver接入原RuntimeFrameValidator，报告、issues/diagnostics、progress、cancel/deadline/close及owner/recovery读取复用既有链路。

### 78.1 源码、本地回归与合同

生产功能提交`eb54f8d`，合同修复/冻结提交`7a03863`，独立HTTP工具提交`7543436`；`e170926`仅是入口文档补录，不能当作本次发布源码。正常solution构建0警告／0错误；`root-regressions/assessment.json`核定定向本地回归去重242/242通过。`local-agent/05-second-gate-mutation-red.log`保二次Writer门禁变异真实4红；恢复后的`local-agent/07-host-job-green.trx`与`local-agent/10-host-image-all.trx`均包含对应四个用例全绿；这些定向子集不与242相加。上述路径均在本节证据根目录内。合同1.34.0从`eb54f8d`metadata生成并经`7a03863`冻结：87路径／97方法／481 schemas／91命令、2873例（2188反例），新增25例（13反例），完整保留1.33.0原2848例。合同输入见`artifacts/api-contract/image-validation-1340-applied/`；本次文档验收补录重新生成并核验固定源码指纹，产品、HTTP协议与合同版本保持。

### 78.2 匹配发布与fresh HTTP

证据根目录`artifacts/project-image-validation-01000-acceptance/`，匹配发布为`dist-7a03863/deployment.json`：Release、linux-x64、self-contained，`sourceCommit=7a03863d4c6c8d13f5f43e1c44ea4efbde1d3942`、`sourceDirty=false`、空diff SHA；859个生产输入与1006个发布文件按manifest固定。独立`oracle-final1/input-manifest.json`引用同一不可变Worker依赖与script-refs，核完整依赖字节身份。

fresh HTTP `http-final1/manifest.json`：`succeeded=true`、1412请求／10582断言，SHA-256 **`89b55cb0badb98d611d892e73ad8f64c66dec7afa7529eddfeaf3e96ee511f1a`**。本次有限矩阵包括：

- static、static-analysis、直接package三基础源普通／透明各4帧；完整报告原始bytes/size/SHA、diagnostics分页、SSE、owner/跨client/recovery ACL与精确revision。13组报告比较中，10组与独立原Runtime报告Oracle匹配（session epoch按首次出现映射归一化）；损坏图片、单边超限与decoded预算3组只按实际报告/稳定诊断核验，不冒称Oracle一致。正式validation没有PNG，所有报告比较均不构成像素验收。
- compile、compileSource、image/material/script-material warning、Runtime Error、130条诊断分页128+2、输入和完整报告预算、损坏/维度/decoded失败；接受后探针自有图片内部变异仍保持固定输入与metadata回执重放。fixture生成与内部注入见`fixture-source-manifest.json`、`owned-fixture-mutations.json`及manifest的`ownedMutations`，不是公共作者编辑能力。
- queued/running cancel/deadline/close；实际Initialize/Evaluate×cancel/deadline/close六组Worker/child身份与终态排水；deadline保`WorkerTaskTimedOut`。一次previous-host orderly restart/recovery保原成功报告bytes、active任务持久化cancelled且不自动重放，旧access拒绝、新bootstrap授权身份可恢复读取；仅这一代有限恢复，不推定完整多代recovery。

`serverRuns`两次均exit0、`forcedKill=false`；33个观察进程身份含26个Worker身份，10个终态排水检查均`liveAtTerminal=[]`，最终`survivorsBeforeForcedCleanup=[]`、`forcedCleanup=[]`、`survivingObservedProcesses=[]`。3349个文件凭证扫描通过。进程身份与扫描均按该manifest登记，不由job终态代推。

### 78.3 旧Worker独立负例与边界

`http-final1/unsupported-worker/manifest.json`独立记录真实0.99预览Worker（源码`d961895`）负例：10请求／476断言、199个文件凭证扫描，Server exit0。能力不公布`project-image-validation-v1`，实际创建拒绝503 `taskUnavailable`／`WorkerImageValidationUnavailable`、field为worker；只证明负能力边界，不宣称旧Worker执行图片validation，也不将其数字并入1412／10582。

图片export/measurement、preset/video/font、完整B3/B4/B5/B6、浏览器/跨平台、性能长片与完整工程验收继续独立；0.99预览HTTP与PNG/像素数字保持§77历史归属。

## 79. 已保存工程Image背景导出合同与实现登记（产品0.101.0／合同1.35.0，2026-10-24，HTTP待验收）

生产实现提交 `1276982` 已稳定并推送。该批新增独立 `project-image-export-v1` Worker能力、KIE1封包、Host二次Writer门禁及导出账本配对字段；匹配发布和 fresh HTTP 尚未完成，因此本节不登记请求/断言计数或排水证据。合同生成物基于该提交的metadata，87路径／97方法／481 schemas／2890例（2199反例）。图片measurement、preset/video/font、完整B3/B4/B5/B6、浏览器/跨平台及性能长片保持独立。


<a id="api-completion-01020"></a>

## 80. P1/P2统一API代表后端范围完成（产品0.102.0／合同1.36.0，2026-10-25）

[API补齐计划](../plans/backend-api-completion-plan.md)的P1/P2已达到本计划停止条件，前端可接入工程创建/编辑/候选测量/保存重开与代表成片流程。新增四个HTTP操作为歌词导入规划、Worker字体目录/检查及编辑候选；工程命令新增四个媒体背景操作和`project.applyEdits`，总96种。空创建、raw/multipart流式上传、音频content/波形、settings/resources、候选generation和原子历史接入原会话/回执。Image/Video、完整包与预设、包字体与已接受分析通过统一固定Runtime输入供预览、测量、正式验证和导出消费；两导出格式、三质量与可空宿主资源策略保持原Exporter语义。实际行为和预算见[统一API§23](../../architecture/backend-api.md#unified-runtime-input-01020)。

### 80.1 源码、本地检查与合同

统一导出适配器`035afcd`、测量消费者`2b2a5e6`、正式验证纯Host门禁`57e5777`、媒体候选附着`adbe871`、高级Line/Token入口`ab41907`、部分token完整行ink修复`086f103`、模板预算对齐`d0bc2fa`与候选背景命令修复`2f64c19`保留独立提交追溯。`2f64c19`正常solution Debug构建0警告/0错误。定向TRX位于`artifacts/api-completion-20261024/tests/`；以下套件存在交叉，不合计：

| 定向检查 | 结果 |
|---|---:|
| runtime-preview-measurement-integrated | 49/49 |
| unified-runtime-export-hosting / unified-runtime-export-worker | 53/53；10/10 |
| package-font-validation-matched / package-font-measurement-worker / package-font-export-worker | 11/11；10/10；1/1 |
| candidate-media-attachment / candidate-background-fixed | 11/11；22/22 |
| partial-token-measurement / advanced-template-workflow / large-font-template-transfer | 4/4；7/7；1/1 |
| optional-project-creation / p1-server-integration / upload-http-transport | 96/96；9/9；6/6 |
| fonts-real-worker / analysis-admission | 1/1；18/18 |

真实19,484,784字节TTC、Color无媒体但含包字体、固定snapshot门禁、已有高级Token/update/候选/固定试样及六音轨家族等按各自定向检查登记，不扩写为本轮全HTTP矩阵。`GET /fonts`与`POST /fonts/inspect`只诊断实际Worker系统字体环境，包字体通过固定资源链消费；音频content/波形不等于浏览器对任意源编码均可直接播放。

合同1.36.0冻结为91路径／101方法／500 schemas／96工程命令、2974例（2248反例）。`9dcd394`初次生成后，`92c3e84`同步候选背景修复生产基线；最终合同`eda4ee5`的source baseline为`2f64c19`；主代理独立freeze/validate通过，final3实际JSON形状检查通过307个响应与91个成功请求，13个非JSON跳过、0未配对、覆盖38个操作。记录为`artifacts/api-completion-20261024/contract-http-final3.json`，最终生成物见[执行合同](../../../web/api-contract.md)。合同形状验证与下节业务运行分别登记，不互相替代；P5的全部目标目录最终收口仍待P3/P4实施后完成。

### 80.2 匹配发布与fresh HTTP

证据根目录为`artifacts/api-completion-20261024/`。不可变`dist-92c3e84/deployment.json`记录Release/linux-x64/self-contained、产品0.102.0、源码`92c3e849e56830e5bdd1c17c7e25a5720da9843b`、`sourceDirty=false`、空diff SHA、1006发布文件与885生产输入。部署manifest SHA-256为`f915cf541825af8aa91026868f1971cc1cdc77a7c838122a297c35279ca2c852`；主代理逐项复核885生产输入与当前集成源码字节一致。Oracle位于`oracle-92c3e84/`，探针最终源码为`dfc5451`。

fresh HTTP `http-final3/summary.json`为**passed／320请求／2215断言**，13个job终态为12成功、1取消。`evidence-manifest.json` SHA-256为`b8f99893281fd4611ca598b2b27e2b3baa84a086102e8ed3c438cdd31a138dbb`；主代理独立复核其中782个条目SHA全部一致。代表流程包括：

- 公开无音频/歌词asset的空创建，18行自制增强LRC导入规划/完整报告与实际导入，180秒双声道48kHz PCM16 WAV（超过历史16MiB上传限制）的实际准备与波形。Worker字体目录/检查可读取实际环境；这与包字体测试分别记账。
- Image基础工程上的Color编辑候选：候选revision/generation、完整逐edit反馈、同一时刻预览/测量完整RGBA相等、PNG SHA和真实geometry；正式工程summary除全client事件游标外全部字段及完整history保持不变。一次apply/undo/redo、恢复图片、保存/关闭/重开后完整设置/定时歌词与背景content保真。该候选测量是Color，不冒称此处跑了Image测量或独立Runtime Oracle。
- 真实180秒、320×180、24fps、4320帧Image+歌词+音轨`opaqueMp4`成片；H.264视频与AAC音轨各180秒。独立ffprobe完整媒体读取/计帧、报告和下载完整bytes/size/SHA一致；另有实际running Worker导出取消且不发布媒体/报告。2秒48帧ProRes4444+PCM16透明短片和draft/high短片通过；透明样本帧实际解码有透明与可见像素。导出检查不宣称全片Runtime内容Oracle、音频逐样本等价或性能矩阵。
- 公开`.mktpl` import/apply高级Line模板，包含有序效果配方、分支输入、最大UInt64种子及19,484,784字节独立`Mako Sans CJK JP`包字体（字体bytes与原已安装Noto不同，避免系统fallback满足测试）。Color+该模板/包字体time测量与预览完整RGBA相等；Video+同一闭包在250ms、1250ms、显式seek回250ms的三帧与独立ProjectStore.Load→原Runtime Oracle完整RGBA一致，前进改变、回退重现。实际time/layout测量、普通/透明各2帧正式validation及2秒48帧带音轨导出通过。正式validation无PNG，报告成功不表示像素验收；三帧Oracle范围不外推完整视频或输入笛卡尔积。

Server正常exit0；64个观察进程身份含27个Worker身份全部退出，remaining为空，主代理复核`/proc`身份均已不存在。781个证据文件凭证扫描通过。进程排水来自观察身份检查，不由job成功推断；782个manifest条目与781个扫描文件分属各工具实际计数。

### 80.3 失败归属与后续阶段

`http-initial1/`与`http-final1/`、`http-final2/`各自保留，不并入final3。final1为64请求／527断言，暴露edit-previews的clearBackground detached命令分发缺口；产品`2f64c19`修复复用原命令与媒体历史，22/22定向回归通过。该失败目录141文件凭证扫描通过、Server exit0、9观察身份（6Worker）全部退出。final2为76请求／618断言，候选预览/测量完整RGBA已通过，探针把预览推进的全client `eventCursor` 24→28误当领域变更；正式revision/dirty/history未变。探针仅排除summary根eventCursor、保留其余领域字段和完整history比较；170文件凭证扫描通过、Server exit0、12观察身份退出。回退帧探针另修为显式seek并采用accepted新generation进行后续帧/关闭；这不是放宽产品playback的单调时间规则。

P1/P2本计划停止条件已达到；下一步P3完整模板定义读取/编辑、试样与高级分配，P4作者草稿/编译/采用/个人配置，P5最终完整API目录。已有高级Line/Token闭包可运行不代表这些编辑/作者能力完成。浏览器/用户、跨平台、任意媒体/字体组合、性能及完整产品分发仍独立；不以历史B3/B4碎片追加本阶段门槛，也不把本次代表后端验收宣称为全部Web产品完成。


<a id="render-backend-api-01060"></a>

## 81. CPU/GPU显式渲染API本机有限HTTP验收（产品0.106.0／合同1.39.0，2026-10-26）

`dev/gpu-render` 已接通公开严格 `cpu|gpu` 选择（GPU实际API为Vulkan）、设备可用性查询、五种固定来源预览的独立后端切换，以及验证/导出/测量的固定选择和回显。创建/任务省略或null用启动默认，已有预览更新省略或null保留选择，候选测量沿候选固定选择；历史缺失值未知、GPU失败不回落。切换不重新捕获工程/草稿，原来源、媒体租约、metadata回执和generation CAS继续生效。完整行为见[统一API§25](../../architecture/backend-api.md#render-backend-selection)、[计划§7](../plans/backend-render-backend-api-plan.md#7-实施与验收记录2026-10-26)和[ADR-0240](../decisions/decisions.md#adr-0240)。

最后生产基线 `9506942118efce3f1869e0cbc9e7da1af1869b42` 的匹配self-contained linux-x64 Server/Worker位于 `artifacts/render-backend-api-20261026-r1/dist-final1/`，`sourceDirty=false`，911生产输入/1022部署文件与Git和文件SHA一致。部署manifest SHA-256 `5467a15380a6aaa8075b1b9c903e269ce1675150077fe3e437ec585ae1a3d966`。合同从新metadata生成，1.39.0为102路径／113方法／560 schemas／3331例（2483反例）；实际JSON744响应/104成功请求与43/13匹配，0无配对产物。

最终本机硬件可用fresh HTTP `http-final2/` 为770请求／5013断言；独立GPU不可用环境 `http-unavailable-final1/` 为46／343，均通过。五类预览CPU→GPU→CPU、no-op/重放/旧代次/ACL、两client、原工程修改及关闭后仍用固定source、project/author input省略保留GPU、CPU/GPU正式验证、GPU作者普通/透明各360共720帧、普通/透明各24帧无音轨导出及普通/候选测量通过。主代理独立解码媒体、report/size/SHA和完整alpha核对通过；未作独立Runtime内容oracle或CPU/GPU跨后端相同断言。

48个Worker取得本机硬件证据；GPU帧与运行中正式任务绑定新鲜Worker，测量仅定位同步请求区间，不能精确区分设备探测与测量执行Worker。两组Server均exit0，157/40观察身份（含119/20 Worker）全部退出、remaining为空，1680/116证据文件凭证扫描和完整清单SHA通过。独立来源/HTTP/媒体核验、本地回归和探索失败完整保留于[验收报告](../acceptance/render-backend-api-acceptance-20261026.md)。`http-final1` 的409请求／2746断言因令牌过期失败，probe `70e45c8` 增加逐client 25秒heartbeat后改用全新final2；其余explore目录各保原计数，不能并入最终通过。

该交付仅覆盖当前Linux/RADV机器的有限CPU/GPU选择API矩阵。旧0.105完整管线回归保持历史归属；浏览器、跨平台/驱动、性能/长片、GPU视频编码和完整产品分发另行验收。分支未合入main，无关未跟踪 `tools/backend-project-image-export-probe/` 保留。


<a id="gpu-performance-optimization-01061"></a>

## 82. GPU迁移后的第一批内部性能优化（产品0.106.1／合同1.39.0，2026-10-27～28）

本批在`dev/performance-optimization`实现成功预乘验证去重、可证明无ruby时的alpha判空读回省略和有界数组复用，保留完整作者图检查、捕获表面、预算与非拥有别名生命周期。生产提交`33b9cc5`／`feb7f23`、工具`a38e598`、合同与匹配发布`68b63ce`分步提交推送；公开合同和HTTP协议保持。详细代码范围、变异和本地回归见[验收报告](../acceptance/backend-gpu-performance-optimization-acceptance-20261027.md)。

CPU/Vulkan四个相关项目各1498/1498，正常solution零警告错误；真实GPU smoke单独1/1，不与整集叠加为独立用例。fresh metadata及合同3331例通过。匹配self-contained Server/Worker发布为912生产输入／1022部署文件、`sourceDirty=false`；fresh HTTP625请求／5689断言、20组PNG及16反事实贡献、8短片168帧、28硬件Worker、78观察身份退出、1664文件凭证扫描通过。视频为完整解码检查，没有逐帧Runtime内容oracle；本轮不含controls阶段。

新旧直接Runtime正式采样已完成并归档到`docs/performance/20261027-optimization/`：七个标签均`assessment.valid=true`，主矩阵材质／组合GPU中位耗时下降45.57%／49.42%，新版最大值1112.641／1428.095ms，500ms固定样本目标未达到。源码与部署归属、静态合同和实际JSON检查（549响应／54成功请求）、采样方法及归档SHA统一在[验收报告](../acceptance/backend-gpu-performance-optimization-acceptance-20261027.md)登记；不扩大为浏览器、跨平台、任意工程／长片或GPU编码支持。


<a id="gpu-structural-optimization-01070"></a>

## 83. GPU结构优化与统一性能日志（产品0.107.0／合同1.39.0，2026-10-28）

`dev/performance-optimization`按二次调研实施内部拥有图像的颜色匹配直接读回、同对象无输入作者图共享和统一Worker性能日志。生产基线`5802ac1`，匹配Server／Worker发布`103ba2c`，sourceDirty=false；保留BottomLeft、同步、完整像素验证、逐目标coverage／预算／诊断及设备租约，不引入ROI或静默CPU回落。默认summary，detailed支持有限脚本身份和帧采样，Server转发受限的不可信遥测。具体语义见[监控说明](../../backend-performance-monitoring.md)及[ADR-0241](../decisions/decisions.md#adr-0241)。

正常solution零警告／零错误；CPU和Vulkan四模块各1556次，加Scripting266、Worker105、Server471、Hosting聚焦49、显式GPU子集42，共4045次执行通过，含重复与子集而非4045个独立测试。合同102路径／113方法／560 schemas／3331例保持；915生产输入与1022部署文件匹配。默认摘要HTTP2032请求／13313断言，32组PNG（含控制健康检查）、8短片168帧、六组预览失控和三组导出控制通过；详细模式304／3951、30组PNG独立通过。82／23硬件Worker、152／54观察身份退出、4586／987证据文件扫描全部通过。首次HTTP的Oracle构建来源不一致失败独立保留，重建后213个共有DLL逐字节相同。

摘要日志107事件含2条活动心跳，详细66事件含30帧与36摘要，本有限样本两侧丢弃计数为0；详见[验收报告](../acceptance/backend-gpu-structural-performance-optimization-acceptance-20261028.md)和[精简证据](../../performance/20261028-structural-optimization-acceptance/README.md)。用户恢复测试后，四份固定执行体完成正式七标签矩阵，246进程／4560项目帧／246小probe；1956对旧新、608对原版／插桩、432对监控模式完整像素诊断相同，123硬件GPU进程及全部Runner正常退出。图／材质／组合Vulkan中位降低64.89%／73.82%／73.61%，材质／组合主矩阵最大346.453／373.706ms。首帧和独立开销对照仍有超500ms样本；默认summary的组合GPU观测中位开销10.70%，detailed限速丢弃27条而摘要完整。正式来源与边界见验收报告§5及[性能证据](../../performance/20261028-structural-optimization/README.md)。浏览器、跨平台、长片及GPU编码继续独立。该批在dev完成验收，随后按用户授权合入main，见[2026-10-28合入记录](backend-main-integration-20261028.md)。

<a id="main-performance-integration-20261028"></a>

## 84. 性能优化开发线合入main（产品0.107.0／合同1.39.0，2026-10-28）

`main`由`1b2d375`快进到`dev/performance-optimization`的`77643f8`，完整保留19个提交。纳入第一批验证去重／空ruby读回省略／缓冲复用，结构调研及第二批颜色匹配读回／作者图共享／统一性能日志。产品0.107.0、合同1.39.0、协议1；原开发分支保留。

合入前远程与本地引用相同，main为严格祖先，merge-tree无冲突且等于开发树。合入后915个生产输入与`5802ac1`及匹配发布`103ba2c`一致，1022个原发布文件、5份归档内1391文件和合同523个guard／4个生成物指纹通过；freeze与3331例静态合同校验通过。无关未跟踪目录六文件逐项保留。详情见[main合入记录](backend-main-integration-20261028.md)。

本次只做Git集成、文档及静态复核，没有新增生产修改、构建、运行测试、发布、fresh HTTP或性能采样。4045次相关测试执行、两种日志模式HTTP及七标签性能矩阵均保留[原dev验收](../acceptance/backend-gpu-structural-performance-optimization-acceptance-20261028.md)来源；主矩阵500ms结果不扩大为首帧、任意脚本或全请求保证。
