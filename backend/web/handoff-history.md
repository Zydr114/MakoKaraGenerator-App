# Web 交接历史快照（截至 P4）

> 本文保存 P5 整理前的交接正文及历史证据来源。下文的“当前”“本轮”、版本、配额和静态输入限制均按各自历史阶段解释，不作为现行调用依据。当前入口见 [README](README.md)、[前端调用速查](frontend-workflows.md) 和同一提交的 [执行合同](api-contract.md)／[行为合同](../docs/architecture/backend-api.md)。

# 前端后端开发交接文档

**当前交付入口（2026-10-25）：产品0.104.0／合同1.38.0，HTTP协议1。** P1/P2与P3代表后端范围已有独立证据；P4作者草稿、固定源码编译/结果、authorDraft预览、验证试样和个人配置库已进入当前合同。实际调用以[冻结合同](api-contract.md)、[完整清单](api-index.md)和[行为合同](../docs/architecture/backend-api.md)为准；P4有限fresh HTTP已通过（167请求／1142断言）；完整浏览器、平台及性能验收继续独立登记。以下旧版本入口保留历史范围。

**协作入口（2026-10-16）：** [AGENTS.md §8.4 前后端协作](../docs/archive/history/AGENTS-20261029-history.md#frontend-backend-collaboration)。Codex 负责后端、Claude 负责独立 Web 前端，通过 MCP Agent Mail 交流需求、合同与交付；首次联通及约定记录见 [§7](#collaboration-established)。

**历史图片正式验证有限验收入口（2026-10-24）：产品0.100.0／合同1.34.0，基线`eb54f8d`。** 已保存工程Image主背景经独立`project-image-validation-v1`能力复用validation路由、job与报告；客户端仍只交精确修订、帧计划与metadata。完整输入4194272字节、基础snapshot4MiB，仅无preset的static/static-analysis/直接package；两段Writer门禁与资源排水见[计划](../docs/archive/plans/backend-project-image-validation-plan.md)、[统一API§10.1](../docs/architecture/backend-api.md#project-image-validation-api)及[实施§78](../docs/archive/history/backend-extraction-progress.md#project-image-validation-01000)。匹配self-contained发布的fresh HTTP有限切片已通过1412请求／10582断言，Server exit 0；证据目录为`artifacts/project-image-validation-01000-acceptance/http-final1/`。该批保留10请求／476断言的不支持Worker负例边界；图片export/measurement、preset/video/font及完整B3/B4/B5/B6继续独立。

**此前图片预览有限HTTP验收入口（2026-10-23）：产品0.99.0／合同1.33.0，HTTP protocol1。** 已保存工程Image主背景进入独立 `project-image-preview-v1` 预览profile，实际Worker能力握手、固定内存闭包与原Runtime/Skia消费见[统一API§22](../docs/architecture/backend-api.md#project-image-preview-0990)及[计划](../docs/archive/plans/backend-project-image-preview-plan.md)。无新公共路由/命令；客户端只交工程ID与精确修订。仅无预设的static/static-analysis/直接package基础源；该0.99预览验收未覆盖validation/export/measurement与模板试看图片输入；当前图片正式验证范围见上方独立入口。视频/字体、图片与预设、图片export/measurement和模板试看继续独立。源码d961895的clean匹配发布fresh HTTP final6已通过1175请求／9760断言，三基础源普通/透明各4帧、seek及六组失控控制见[实施§77](../docs/archive/history/backend-extraction-progress.md#project-image-preview-0990)；启动器参数/stdout不变，本入口仅关闭该有限图片预览矩阵。

**此前B4-P1入口（2026-10-20）：产品0.98.1／合同1.30.1，HTTP protocol1。** 既有合法项目的直接效果包新增公共项目预览及完整compileSource/imageEffect/material诊断，见[统一API§20](../docs/architecture/backend-api.md#public-package-preview-0980)及[实施计划](../docs/archive/plans/backend-public-package-preview-plan.md)。不新增路由或工程命令；三静态profile保持，包profile须实际Worker握手。含preset的包工程、非Color背景、外媒体/字体闭包、B4包任务与作者写入仍独立；package validation 仅在实际 Worker 能力握手通过后公布，见 B4-P1 计划。Server/Worker须整体匹配，schema冻结与运行验收分别记录，不据本入口宣称浏览器/用户或完整B3/B4完成。

**此前spacing入口（2026-10-19）：产品0.97.0／合同1.28.0。** 自动ruby水平墨迹间距、共享画面/几何、时间无关及保守边界见[统一API§19](../docs/architecture/backend-api.md#ruby-spacing-0970)和[实施计划](../docs/archive/plans/backend-ruby-spacing-plan.md)。无新增wire/schema/命令，Server/Worker须匹配；运行验收独立登记。

**此前TTML/ruby入口（2026-10-19）：产品0.96.0／合同1.27.0。** 独立注音segments/七严格编辑命令、真实rubyResult与history反馈、TTML完整document范围及项目往返、AMLL选定来源交换见[统一API§18](../docs/architecture/backend-api.md#18-ttml-与独立注音时间轴0960合同1270)及[实施计划](../docs/archive/plans/backend-ttml-ruby-timeline-plan.md)。合同描述实际wire；最终分层运行验收及整体关闭以实施记录为准。以下基础业务继续有效：新增授权目录entries、单来源LRC/TTML评估→确认→认证下载、audio.replaceAsset/audio.clear与历史资源同步、project.rename/resources、完整.mktpl保真惰性传输、真实container/slot/text叶上下文静态样式及21来源、clip成员扩缩拆合。0.95基础业务命令仍按单来源 branch scope；完整 `ttml.project` document scope 由合同1.27与[实施§59](../docs/archive/history/backend-extraction-progress.md#ttml-ruby-timeline-0960)提供，`ttml.amll`仍为选定来源交换。严格DTO、实际路由/预算和边界见[统一API§17](../docs/architecture/backend-api.md#basic-business-0950)；0.95 final2与本次生产源码基线 fresh replay证据见[实施§58](../docs/archive/history/backend-extraction-progress.md#basic-business-0950)。前端须整体同步生成合同、行为源与匹配Server/Worker；高级归档成功本身不授予公共执行；直接包项目预览另按本批有限能力门禁。浏览器/用户、整曲及完整B3/B4/B5/B6和G25继续独立。

**历史补丁（2026-10-18）：产品0.94.1／合同1.24.1。** 透明导出请求枚举修正为`transparentProRes4444`；附着无效来源、前奏与容器返回稳定failureCode/field。无新路由或命令；前端整体同步冻结合同与详细行为源，并重新匹配发布Server＋Worker。证据及边界见[修复记录](../docs/archive/acceptance/backend-basic-business-bugfix-0941.md)。下方0.94.0与合同1.24.0的验收数字保持历史归属。

历史2026-10-18 API合同 **1.24.0**：[执行合同](api-contract.md) · [全部API清单](api-index.md) · [OpenAPI](openapi.json) · [指纹](contract-manifest.json)。产品 **0.94.0**（70 paths／77 methods）本批已验收关闭原始任务表定义的G20～G24后端范围；新增14命令、line-presentation GET与同帧geometry/PNG测量POST，Node静态预设目标，见[行为§13.18～21](../docs/architecture/backend-api.md#1318-布局类型与单句展示继承合同1240)；保留G20-B有限createNode/copySubtree、structureResult.createdNodeIds及两项capability（[行为§13.17](../docs/architecture/backend-api.md#g20b-node-creation-0930)／[实施§56](../docs/archive/history/backend-extraction-progress.md#g20b-node-creation-0930)）；保留 G21-B 四种属性/动画命令、G22-A 固定参考读取、G23 四种手工前奏/提示容器命令、G24 单句局部微调读写，共九命令与两个GET（[行为§13.13～16](../docs/architecture/backend-api.md#g21b-orchestration-properties-0910)／[实施§55](../docs/archive/history/backend-extraction-progress.md#g21b-g24-basic-orchestration-0910)）；保留 G21-A 内部布局默认位置命令（placement 可显式 null 清除）及 G20 六种有限结构命令（[行为§13.11](../docs/architecture/backend-api.md#g20-structure-0910)／[实施§53](../docs/archive/history/backend-extraction-progress.md#g20-structure-0910)），并保留 G19-B 片段生命周期命令、 G19 片段范围与编排投影，保留 G18 快速布局， G17 模板固定试样、G16 只读预设候选试看、G15 工程预设分配读模型 `GET .../preset-applications` 与 W4 `GET .../orchestration`／`orchestration.setDefaultContainer` 有限切片，见[行为§13.4](../docs/architecture/backend-api.md#g15-preset-applications)、[行为§13.5](../docs/architecture/backend-api.md#w4-orchestration-default)、[行为§13.6](../docs/architecture/backend-api.md#g16-preset-candidate-preview)、[行为§13.7](../docs/architecture/backend-api.md#g17-template-fixed-sample)、[行为§13.8](../docs/architecture/backend-api.md#g18-basic-layout)、[行为§13.9](../docs/architecture/backend-api.md#g19-clip-range)；[行为§13.10](../docs/architecture/backend-api.md#g19b-clip-lifecycle-0910)及[实施§52](../docs/archive/history/backend-extraction-progress.md#g19b-clip-lifecycle-0910) 与 [实施§51](../docs/archive/history/backend-extraction-progress.md#g19-clip-range-0910)；保原公共静态预设预览、编辑与 G11 波形。库更新不自动修改已分配工程。前端既有W3用户验收为PinkPike于邮件68报告的2026-10-17历史结果（d33051a/f571dfb）；本仓库未重跑，本轮合同1.24浏览器/用户验收未做。清空输出时长仍是尚未确定，导出前须显式设置。

**本轮验收：** 生产源码 `c50516d` 已推送；正常solution零警告/错误，四模块1677项及另一Rendering项目40项共1717/1717无重复。独立匹配self-contained0.94发布HTTP为73请求/781断言、6实际测量、8成功job终态；11观察身份含8Worker全gone。time/layout同帧geometry+PNG、真实reference附着、动画及完整10帧透明纯视频oracle证据、提交前dirty发布输入的Git blob核验均见[实施§57](../docs/archive/history/backend-extraction-progress.md#g20-g24-completion-0940)和[整体计划](../docs/archive/plans/backend-g20-g24-completion-plan.md)。冻结合同提交`b312a835`：1.24.0/70 paths/77 methods/401 schemas/1913例（1394反例）/78命令；fresh及root独立metadata/check/validate通过，73实际HTTP JSON全部匹配、0跳过/20操作。此后端关闭不代表浏览器/用户、完整公共B3/B4/B5/B6或全部Web产品完成。

2026-10-16 补充：[独立后端审查与双端合同建议](../docs/archive/plans/backend-review-and-web-contract-plan.md)。包含本轮原型复验、W0设计差异、已复现的无音轨预览问题及后续批次建议；该审查不新增已实现API或恢复其余暂停范围。

历史批次（2026-10-15）：后端分支：`refactor/individual-backend`；产品：`0.75.1`；HTTP 协议：`"1"`；本批产品源码提交：`e60376a`（0.75.1）；稳定启动工具提交：`51c29b1`；部署与验证执行时的dirty输入清单及完整证据见实施§31。

**用户于2026-10-16授权继续补齐后端编辑能力，按S1→S2→S3推进；其他原暂停范围保持。** 本目录保存交接文档与冻结API合同；前端代码继续在独立的 Web 项目中开发。下方0.75.1提交与G1证据为历史，本批版本及证据以上方入口为准。

## 1. 交接结论与完成距离

后端已能作为独立进程启动，不创建 Avalonia 窗口、不使用桌面播放器或物理声卡。Web 开发连接 `Backend.Server` 即可；Avalonia 不再是启动服务、调用 HTTP 或验收 Web 链路的前置条件。现有桌面实现仍可用于核对业务语义与渲染结果。

当前已完成API补齐计划P1/P2的代表后端范围。基础设施、编辑/媒体与统一Runtime链路已有实际HTTP证据；P3完整高级定义与P4作者/配置库已接入；P4运行证据和P5最终交接收口分别登记。需按三个交付目标分别判断：

| 目标 | 当前进度 | 主要剩余工作 |
|---|---|---|
| 独立进程运行与有限工作流 | 已可用 | 匹配部署 Server／Worker／native 工具，并按实际 capabilities 使用；完整分发与跨平台验收继续待办 |
| 支撑 Web 歌词／打轴编辑器 | 现有编辑/历史、样式库/分配/静态预览及原G20-G24后端已交付；PinkPike邮件68报告既有W3于2026-10-17获用户验收（d33051a/f571dfb），属于对端历史报告 | 当前合同前端接入与浏览器/用户验收仍待独立完成；统一Runtime代表后端范围见§80 |
| 覆盖现有产品功能的完整独立后端 | 原G20-G24后端已关闭，完整产品仍未完成 | P4运行证据、P5最终目录与分发平台收口；P3/P4接口已接入 |

独立服务及P1/P2代表后端工作流已可调用；完整Web产品仍需P5最终交接、浏览器接入与分发平台收口。已有高级包/媒体/字体Runtime消费和G20-G24不列为未做。现有测试数或 B4 切片数不能折算为功能完成百分比。

### 1.1 各阶段的真实边界

| 阶段 | 已有能力 | 仍缺的出口 |
|---|---|---|
| B1 共享编辑 | 时间、正文／注音、行、导入、来源、设置、拆合／重排及静态样式已有共享用例与有限HTTP出口 | 原G20-G24及P1/P2代表后端范围已完成；P3/P4高级编辑与作者接口已接入；浏览器验收独立 |
| B2 会话与持久化 | 本机鉴权、资源上传、创建／打开、精确修订、历史、保存／恢复、目录授权、SSE、任务账本已有有限闭环 | 前端所需的完整工程查询与编辑覆盖、客户端长期使用体验仍须补齐 |
| B3 预览与 Worker | 静态工程 HTTP 异步帧预览已通；0.73～0.75 完成内部包快照、真实包 Worker／失控回收及完整诊断；0.86.0 新增静态预设快照公共预览，0.86.1 修复无音轨入口 | P1/P2统一媒体/包字体/高级预设代表HTTP已通过；任意完整工程等价、浏览器及平台范围独立 |
| B4 验证／导出／分析 | A～I 有限切片已完成；静态正式验证、短片导出／下载、独立音频分析及采用／清除、受管理媒体准备已通 | P1/P2统一输入正式任务与180秒代表成片已通过；任意输入组合、性能及平台范围独立 |
| B5 场景与作者 | 场景、完整模板、作者草稿/固定源码/编译/采用、试样和个人配置库已接入HTTP | P4运行验收、P5最终交接及浏览器产品体验分别登记 |
| B6 收口与发布 | Contracts／Server／Worker 边界、当前接口OpenAPI及匹配启动入口已有实现 | 前端类型同步、完整分发、代表完整工程与浏览器验收、依赖及旧入口收口 |

历史 B3-G 的统一测试为 1001／1001，独立发布 Worker 的 22／22 另计。该历史阶段真正HTTP大范围有限验收属于 B4-I：699 请求／4239 断言。**这些证据各有输入范围；B3-G 没有开放或验收公共包脚本 HTTP。** 原全量、性能、长片、GUI 与人工平台暂停范围继续保持。

## 2. 当前前端可以使用什么

当前 HTTP 执行入口为 [api-contract.md](api-contract.md)，详细行为正文继续维护于 [docs/architecture/backend-api.md](../docs/architecture/backend-api.md)，OpenAPI与其指纹一同冻结。读取实际 capabilities 后决定功能是否可用；规划文档、共享 C# 用例、私有 Worker 协议都不等于已开放的前端接口。

### 2.1 工程命令：当前九十六种（按能力组列出）

入口：`POST /api/v1/project-sessions/{id}/commands`。

| 当前接受的 command.type | 能力与限制 |
|---|---|
| `lyrics.setTimeRange` | 设置一行或一个元素的起止时间；两端必填，不能用它清除时间 |
| `history.undo` | 撤销当前工程的一次历史 |
| `history.redo` | 重做当前工程的一次历史 |
| `audio.analysis.apply` | 显式采用同源、当前 live 成功分析任务的完整结果 |
| `audio.analysis.clear` | 清除已采用分析 |
| `lyrics.stampTokenStart` / `lyrics.stampTokenEnd` | 元素播放头打点，真实估算结果由timingResult返回 |
| `lyrics.clearTokenTime` | 元素清除，缺失状态重复清除无变化 |
| `lyrics.nudgeTokenTime` | 按工程fps微调元素时间，每次HTTP确认一次撤销 |
| `lyrics.shiftLine` | 整行与已定时元素原子平移 |
| `lyrics.setTokenTimes` | 1..1000条元素时间整批提交，沿原无媒体上界语义 |
| 正文／注音与行五命令 | setLineText／setRubyText／addLine／duplicateLine／removeLine，见行为§6.6 |
| 四个媒体背景命令 | setBackgroundAsset／clearBackground／setBackgroundFit／setBackgroundPlayback；既有setBackgroundColor只改底色，见行为§6.12 |
| `project.applyEdits` | 固定edits顺序准备、整体拒绝、一次历史；候选预览不改正式历史，见行为§6.12 |
| composition 六命令 | 画布／帧率／底色／显式时长／采用歌词末尾／采用音频时长，见§6.6 |
| 来源四命令 | 新增翻译／配置／删除／主行关联，见§6.7 |
| 元素结构五命令 | splitToken／mergeToken／reorderToken／resplitLine／resplitBranch，见§6.8 |
| lyrics.importAsset／appendText | 固定授权资源导入或文本追加，见§6.9 |
| styles.patch／styles.restore | 四层canonical属性局部写入／恢复继承，见§6.11；底层能力，前端不必直接暴露 |
| `presetApplications.apply` | 捕获builtin/personal模板的精确revision快照，应用到project/branch/line/token/Node目标；库更新不追改工程 |
| `presetApplications.update` | 按applicationId替换模板快照，保留应用身份／启用状态 |
| `presetApplications.setEnabled` | 设置一个应用的启用状态；no-op保redo |
| `presetApplications.remove` | 移除1..1000个应用身份，失效旧应用可直接移除 |
| `orchestration.setDefaultContainer` | 为现有歌词来源设置当前工程已有歌词容器；branchId/containerId非空，不提供null清除 |
| `orchestration.setLayoutDefaultPlacement` | 已有布局容器内部默认位置；placement完整对象或显式null清除，与外层几何分离 |
| `orchestration.setNodeGeometry` | 已有节点完整placement/referenceRect；六anchor、画布外有限坐标、正缩放与正参考尺寸比例 |
| `orchestration.setLayoutSettings` | 完整safeArea/maxWidthRatio；安全区0..1且有序、宽度比例在(0,1]，不改布局类型/槽位身份 |
| `orchestration.setSlotAnimation` | 完整十四字段与三组通道，时间为非负Int64字符串；null仅普通布局可清除 |
| `orchestration.setProgramAnimationPolicy` | 来源程序Karaoke/Static/FollowPrimary或显式null恢复按来源角色解释 |
| `orchestration.createCountdown` | 已有普通组下创建1..64标记的提示容器，Core生成真实稳定身份；parentId/name/count全必填，name可null |
| `orchestration.createLeadIn` / `orchestration.updateLeadIn` | 七业务字段全必填；name可null、正durationUs、显式enabled；引用真实行与同程序根提示容器，不开启自动生成 |
| `orchestration.deleteLeadIn` | branchId/leadInId全必填；删除前奏，保留提示容器与自动生成设置 |
| `orchestration.setLineLocalTransform` | branchId/lineId/transform全必填；完整四数值对象或显式null清除，比例偏移/旋转有限、scale>0，不改歌词时间 |
| `orchestration.createNode` | kind/parentId/name/slotCount全部显式；kind为group/auto/slotSequence，前两者slotCount:null，序列1..64；parentId:null为画布，否则已有普通组；Server生成全部ID |
| `orchestration.copySubtree` | nodeId/name全部显式；同父完整复制，name:null清副本根名；内部父/槽位/mark引用重映射，单槽位/mark复制追加外部owner引用，不复制歌词或程序/片段/前奏/预设绑定 |
| `orchestration.setNode` | 仅修改现有节点名称；nodeId/name必填，name可显式null |
| `orchestration.reparent` | 保持世界外观重挂；nodeId/parentId必填，parentId可显式null到根层 |
| `orchestration.reorderChildren` | parentId与完整orderedIds必填；仅改同级显示顺序 |
| `orchestration.addSlot` | 向SlotSequence追加空槽位；containerId/slotId/name必填，name可null |
| `orchestration.removeNode` | 删除未被引用的节点子树；仍被程序／片段／前奏引用时拒绝 |
| `orchestration.reorderSlots` | containerId非空，orderedIds完整覆盖全部槽位；两种排序均1..2000个唯一ID |
| `orchestration.createClip` | 创建显式片段；branchId/clipId/containerId非空，lineIds为1..1000个唯一连续行ID，name可省略或显式null；仅独立计时Karaoke来源 |
| `orchestration.renameClip` | 修改片段名称；name必须显式提供，可用null清除 |
| `orchestration.setClipContainer` | 将片段移动到当前来源展示根下的歌词容器 |
| `orchestration.deleteClip` | 删除片段并由核心编辑器回到来源默认容器 |
| `orchestration.previewBasicLayout` / `orchestration.applyBasicLayout` | 快速布局整来源预演／应用；selectedLineIds可省略或显式null表示整来源，非null数组表示连续范围，必须唯一、按来源顺序且真实存在；仅独立计时、Karaoke来源支持片段布局，其他来源返回UnsupportedSource；应用一次历史并返回识别与新建clipIds统计 |

G20～G24补齐新增 `orchestration.setLayoutKind`、`setLineEnterAnimation`、`setProjectEnterAnimation`、`setLeadInGeneration`、`previewLeadInGeneration`、`applyLeadInGeneration`、`deleteLeadInAndExclude`、`setLeadInAttachment`、`addMark`、`setMarkText`、`reorderMarks`、`spaceMarks`、`setCountdownAppearance`、`setMarkAppearance`。完整显式字段、只读预览、选中应用与预算见统一API§13.18～21；公开测量返回同一次真实Worker场景采样的几何与base64 PNG。

基础业务另有`audio.replaceAsset/audio.clear/project.rename`及`orchestration.setClipMembers/splitClip/mergeClips`六命令。注音时间轴另有`lyrics.setRubySegmentTime/clearRubySegmentTime/setRubySegmentText/addRubySegment/removeRubySegment/reorderRubySegments/setRubySegmentTimes`七命令，四级组/五级片段目标与旧token目标分开，wire字段/反馈/预算见统一API§18和完整生成清单。

新建、上传、打开、保存、关闭、验证、导出与分析仍有各自入口。上述九十一命令保持唯一写者、精确修订、一次HTTP一次历史、no-op保redo；各新命令领域错误与反馈分别见对应行为节。完整changeSet超过16KiB时保areas、三类ID数组全空、requiresFullRefresh=true；前端须完整刷新，不能把空ID当无变化。

读模型已有summary/history、旧最小lyrics/lines、S1来源列表/完整正文注音时间窗口/标量settings。另有 G11 同源有界波形读取、四层 style 静态投影、模板库／分配命令、静态预设公共预览及 W4 `GET .../orchestration?expectedRevision=...` 结构投影；G15 提供 `GET .../preset-applications?expectedRevision=...` 读取 applicationId、targetValid、enabled、模板快照摘要与 branchInputs。 当前能力列表独立公布 `project-sessions.preset-applications.read`；source 的 libraryId、revision、newVersionAvailable 即使为 null 也固定返回。完整场景、作者包和最终预设外观解析仍独立，不能从未开放的整体 Project JSON 推断。

本批新增的精确修订读取：

| GET | 严格query与有限响应 |
|---|---|
| `/api/v1/project-sessions/{id}/orchestration/geometry` | 唯一expectedRevision/nodeId；返回固定容器reference/parentReference及矩阵、pivot、四角，referenceKind=fixedContainerReferenceV1、finalPixelGeometry=false；不是文本/动画/最终像素几何 |
| `/api/v1/project-sessions/{id}/orchestration/line-local-transform` | 唯一expectedRevision/branchId/lineId；transform始终返回，对象或显式null，不将未覆写伪装成默认对象 |

两GET拒绝未知/重复/缺失query，修订冲突409；分别读取`project-sessions.orchestration.geometry-reference.read`和`project-sessions.orchestration.line-local-transform.read`能力。geometry独立限制1024节点、64深度、20000输入项、64KiB累计ID与1024 UTF-8字节目标ID；响应沿共同1MiB/20000预算。orchestration.programs返回完整leadIns及派生可用时间，nodes提供有限countdown/textMark内容，layout.slotAnimation始终返回对象或null。新增写命令各有`project-sessions.commands.orchestration.*`能力，保持一次历史、no-op保redo与提交前完整候选/回执预算。上述切片不代表完整G20-G24、完整作者、最终Runtime像素或浏览器W4验收。

G20-B两命令的名称最多256 UTF-16 units，输入ID最多128且非空白；创建初始槽位≤64，复制子树≤128，源/候选各≤2000节点/深64。验证输入独立限20000项，包含歌词branch/line/token及程序关联遍历项，累计身份/名称/标记文本≤1048576 UTF-16 units；长歌词也可能413。完整编排输出另限1MiB/20000项，完整回执在提交前预检。成功structureResult新增createdNodeIds（根第一、其余候选新增顺序），旧六命令不出现；重放保持原ID，一次历史支持undo/redo。详细默认几何、Auto布局、槽位动画与稳定错误见[行为§13.17](../docs/architecture/backend-api.md#g20b-node-creation-0930)。

后续扩展优先复用 [Application/Editing](../src/MakoKaraGenerator.Application/Editing)，由 Hosting 映射 DTO、串行执行并维护同一历史；前端维护输入草稿、选择、播放头和显示状态。工程内容与历史继续以后端为唯一权威。当前预设分配命令在Writer外捕获模板快照，再在工程Writer内提交；库修改不会追改已分配快照。

S1新增三种精确修订读取：`GET .../lyrics/branches` 可发现primaryBranchId与所有来源；`GET .../lyrics/window` 读取正文、完整注音、时间状态及主行关联；`GET .../settings` 读取标量设置。三个请求都必须带expectedRevision，两个窗口默认offset=0、limit=100，最多100行/来源、1MiB与20000项预算；409后重新读当前修订。它们不增加写命令，也不改旧 `/lyrics/lines`。

### 2.2 预览：已有异步帧接口，完整实时体验尚未交付

当前实际路由如下，精确 JSON 见统一 API §9：

| 操作 | 路由 |
|---|---|
| 配置与限制 | `GET /api/v1/runtime-info` |
| 创建固定输入预览 | `POST /api/v1/preview-sessions` |
| 创建模板固定试样 | `POST /api/v1/template-preview-sessions` |
| 工程编辑后换输入／调整尺寸 | `PUT /api/v1/preview-sessions/{id}/input` |
| 请求指定时刻的帧 | `POST /api/v1/preview-sessions/{id}/frame-requests` |
| 读取权威状态／最新帧身份 | `GET /api/v1/preview-sessions/{id}` |
| 事件提示 | `GET /api/v1/events`（SSE） |
| PNG 字节 | `GET/HEAD /api/v1/preview-frames/{frameId}/content` |
| 当前代次诊断 | `GET /api/v1/preview-sessions/{id}/diagnostics` |
| 关闭并等待排水 | `POST /api/v1/preview-sessions/{id}/close` |

可立即联调的流程：

1. 登记客户端，读取 capabilities／runtime-info，并读取 client state 后按 eventCursor 订阅 SSE。
2. 创建／打开工程，取得实际 `projectSessionId` 与 `projectRevision`。
3. 以该精确修订创建 preview，明确传输出尺寸、alphaMode，保存返回的 previewId／generation。
4. 发出带递增 `requestSeq` 的帧请求，intent 使用 `playback` 或 `seek`。202 只代表接受。
5. 接收 `preview.frameReady`／`preview.stateChanged`／`preview.failed`／`preview.closed` 事件提示，再 GET 状态；也可在等待时有界查询状态。事件可能合并或丢失，GET 状态为准。
6. 用鉴权 fetch 读取 latestFrame.content.contentUrl，解码 PNG；只显示仍匹配 previewId／generation／sourceRevision 且未被更新序号取代的结果，释放旧 bitmap／Blob URL。
7. 编辑工程后，显式 PUT input 捕获新的精确修订，再请求帧；原 preview 不会自动追随工程最新修订。
8. 寻址使用 seek，序号仍须大于此前已接受值，并采用其响应中的新 generation；换输入／seek／关闭后拒绝晚到帧。HTTP seek 当前会重建 Worker 会话，频繁寻址的体验需联调验收。

当前保留 `static-lyrics-scene-v1`、`static-lyrics-scene-analysis-v1` 与 `static-lyrics-scene-presets-v1`；既有合法项目中的直接效果包及包材质可按独立 `package-lyrics-scene-v1` 进入项目预览，须通过实际 Worker 能力握手，由 Host 按完整固定快照自动选择。直接包 profile 仅支持 Color 背景且要求无非空 `PresetApplications`，完整静态预设仍由原静态 profile 渲染；模板固定试样与预设候选试看保持静态范围，图片／视频背景、预设中的包／配方及外部媒体／可搬移字体闭包仍明确拒绝。完整诊断与能力边界见[统一API§20](../docs/architecture/backend-api.md#public-package-preview-0980)。最大预览为 1280×720，裸帧≤4MiB；一执行＋一个最新 pending 槽，每会话默认保留两帧，PNG 默认 TTL 30 秒。`configured-unverified` 仅表示路径已配置，不能代替一次真实帧验证。

**“缺少实时预览”应落实为两项工作：先在浏览器接通并验收现有静态异步帧链路，再补齐完整工程输入与低延迟交互。** 当前没有通用 WebSocket 视频流，也没有已验收的固定 30／60fps 服务承诺。是否更换帧传输方案，应依据同一代表工程的实际延迟与吞吐决定。

### 2.3 其他前端约束

- 工程 revision、预览 generation／sequence、微秒等 Int64 值按合同使用十进制字符串；转换到浏览器播放秒数时才做边界转换。
- 同一工程写命令串行发送，带 `expectedRevision`；冲突后读取新状态并保留用户草稿，不盲目重放旧意图。
- 写操作复用统一 requestId／hostInstanceId／validUntilUtc 回执；超时并不证明没有执行，重试保持原载荷、原 ID 与原截止时间。
- 浏览器播放音频；服务提供受授权字节。音频内容已有 GET／HEAD／Range，G11 已提供受授权、有界窗口的波形 GET／HEAD／ETag／304；这不是整曲 PCM 或持续流式波形服务。
- PNG、音频、成片均需鉴权。直接设置 `<img src>`／`<audio src>` 不能附加当前所需请求头；先按合同 fetch，再由前端选择 Blob／播放适配方式，不能把 token 拼到 URL。
- SSE 使用带鉴权头的 fetch 流；原生 EventSource 无法直接满足这套请求头合同。缺口事件走 state 重同步，不能靠本地事件缓存猜工程状态。
- 默认访问令牌 120 秒、客户端租约 300 秒；使用 heartbeat／resume。默认16客户端上限保持；0.75.1 在新登记配额不足时回收已过租约且无保护依据的空闲身份。工程／任务／在途请求／SSE／流式读取／有效回执及未到保留期终态job保护，见§6.1；实际回收后旧access与resume均401，须新登记。

## 3. 在本仓库启动 headless 后端

### 3.1 本机前提

- 从仓库根执行；SDK 基线为 [global.json](../global.json) 的 .NET 10.0.112（latestFeature）。
- 当前机器有 `Microsoft.NETCore.App`，缺共享 `Microsoft.AspNetCore.App`；直接使用普通 `dotnet run` 启动 Server 会受此限制。下面采用 self-contained 发布，无须安装系统 ASP.NET runtime。
- 本机 `ffmpeg`／`ffprobe` 位于 `/usr/bin`；Worker 渲染仍需字体、Fontconfig／FreeType／ICU 等实际 native 环境。self-contained 仅包含 .NET runtime，不代表全部系统依赖或字体均被打包。
- Server 与 Worker 分别发布，保持同一源码、版本、Configuration、RID 和 self-contained 模式。Server publish **不会自动带上 Worker**；只复制一个 Worker apphost 也不完整，须保留整个发布目录及 `script-refs`。
- 下面只适用于本机已验证的 Linux x64 方向。服务只监听 loopback；浏览器开发 Origin 要显式允许。

### 3.2 匹配发布 Server 与 Worker

可重复使用入口为 [tools/backend-web-host/host.py](../tools/backend-web-host/host.py)，命令与 stdout 契约见其 [README](../tools/backend-web-host/README.md)。使用不存在的新发布目录，保留失败日志：

```bash
python3 tools/backend-web-host/host.py publish \
  --dist "$PWD/artifacts/web-backend/0.95.0"
```

入口分别 self-contained Release/linux-x64 发布 Server 与完整 Worker／`script-refs`，共用 dotnet 锁、单节点和禁用共享编译；记录版本、源码提交、dirty 指纹、生产输入与部署文件 SHA，生产输入在两端发布间变化则失败。后续版本更新必须重新发布两端，不能混用旧 Worker。依赖恢复如需代理，按需设置 `http_proxy=http://127.0.0.1:7897`、`https_proxy=http://127.0.0.1:7897`；loopback HTTP 应直连。

### 3.3 稳定 profile 与前台运行

```bash
python3 tools/backend-web-host/host.py run \
  --dist "$PWD/artifacts/web-backend/0.95.0" \
  --profile "$PWD/artifacts/web-backend/dev-profile" \
  --origin http://localhost:5173 --origin http://127.0.0.1:5173
```

入口验证部署 SHA，固定 `/usr/bin/ffmpeg`／`/usr/bin/ffprobe`（可用绝对路径参数更换），只监听随机 loopback 端口；显式允许上述 Origin。它清除继承的 `MAKO_BACKEND_*`，使用生产默认16MiB、access120秒／lease300秒，不靠开发环境隐式提高配额。

profile 中 `state/` 与 `projects/` 跨重启复用；每次运行在 `runs/` 新建0700私有目录，资源、0600 bootstrap 和日志独立。Server 独占 state lease 拒绝并发使用同一 profile。启动 stdout 保留 `BACKEND_READY <实际地址> <hostInstanceId>`，另提供 `WEB_BACKEND_READY <JSON>`：含 addresses、hostInstanceId、credentialFile、runDirectory、version、pid，**不含 token**。外部可信 `dev-backend.mjs` 可用 spawn 解析该行；浏览器不能直接读私有文件，Server 不提供任意页面可访问的 bootstrap 接口。

随机端口从 ready 结果或私有文件读取；不能假定5000。用 Ctrl+C 或向启动器 PID 发送 SIGTERM，等待 Server／Worker／native 排水及启动器退出后再重启；正常退出删除本次自有 bootstrap，保留持久数据与日志。不按进程名杀进程、不清空用户目录。日志为 `<runDirectory>/server.log`，启动失败非零退出并给出日志路径。

ready 与 `/health/live` 只证明启动／存活。Worker 缺失、不匹配或 native 能力探测失败会分别禁用功能；Server 音频准备不回退 Host native。必须先核对 `audioPreparation.enabled=true` 才能新建有音频工程；`configured-unverified` 仍须真实 PNG 验证。

### 3.4 最小握手与能力核对

在另一终端，将 `MAKO_BACKEND_CREDENTIAL_FILE` 设置为上一步输出的**文件路径**，运行下面的示例。它只打印身份和能力，不打印 bootstrap／access／resume token：

```bash
export MAKO_BACKEND_CREDENTIAL_FILE=/替换为本次实际路径/bootstrap.json
python3 - <<'PY'
import json, os
from urllib.request import Request, build_opener, ProxyHandler

opener = build_opener(ProxyHandler({}))  # 本机 loopback 请求不经代理
with open(os.environ['MAKO_BACKEND_CREDENTIAL_FILE'], encoding='utf-8') as stream:
    bootstrap = json.load(stream)
base = bootstrap['addresses'][0]
def read(path, headers=None, body=None):
    payload = None if body is None else json.dumps(body).encode()
    request = Request(base + path, data=payload, headers=headers or {})
    with opener.open(request, timeout=15) as response:
        return json.load(response)

print('health:', read('/health/live'))
client = read('/api/v1/clients', {
    'Authorization': 'Bearer ' + bootstrap['bootstrapToken'],
    'Content-Type': 'application/json'
}, {'protocolVersion': '1'})
headers = {
    'Authorization': 'Bearer ' + client['accessToken'],
    'X-Mako-Client-Id': client['clientId'],
    'X-Mako-Host-Instance-Id': client['hostInstanceId']
}
print('client:', client['clientId'], client['hostInstanceId'])
for path in ['/api/v1/capabilities', '/api/v1/runtime-info',
             '/api/v1/validation-capabilities', '/api/v1/export-capabilities',
             '/api/v1/analysis-capabilities']:
    print(path, json.dumps(read(path, headers), ensure_ascii=False))
PY
```

普通命令行请求可不带 Origin。Web 页面需由可信本机开发启动器读取私有启动文件并向明确允许的页面交付启动信息；浏览器不能直接读取该文件，Server 目前也不托管 Web 页面或提供任意网页可访问的 bootstrap 接口。凭证放在 Authorization 头，不能进 URL、前端仓库或公开日志。完整登记、心跳、恢复及回执字段见统一 API §1～4。

Server 重启后旧 access 可能先返回401 `unauthorizedClient`，因为认证先于旧 host 检查。前端应通过可信启动器取得新 bootstrap，并以 health／新握手比较 hostInstanceId；不能只等待 `hostRestarted`。保存、关闭、打开均等待202 job达到成功终态，保存成功才关闭、关闭成功才打开；不能把202当作已经完成。外部 Web 设计中的0.74.0、默认 dotnet run／无 Worker 模式与此有差异，应按本交接及实际 capabilities 更新。

工程文件仍要求精确产品版本匹配；本轮使用当前0.98.0新建／保存的自有工程或真正兼容的当前版本输入，不通过手改工程版本字段绕过校验。正式验证／导出前必须显式设置正的输出时长，可在create或composition.setOutputDuration等命令设置；当前公开导出还有≤10秒、≤600帧等有限合同。

## 4. 调整后的开发步调

后续以 Web 用户能完成的业务闭环安排批次。原“下一步直接做公共 package HTTP”调整为先完成下列 P0／P1；已做好的内部包执行与诊断作为后续复用基础。

| 优先级 | 后端工作 | 前端配合与验收出口 |
|---|---|---|
| P0 联调基础 | G1与匹配Server／Worker稳定启动已交付；接续完成W0浏览器验收、冻结W1命令／查询清单 | 真实浏览器登记／resume／心跳、上传→新建→时间修改→撤销／重做→保存／重开；能力禁用显示原因 |
| P1a 时间编辑 | S1/S2有限后端出口已交付：来源/稳定ID/精确修订读模型、打点/清除/微调/平移，见行为§6；前端闭环独立验收 | 播放头打点、拖动／微调、清除、一次动作一次撤销、过期修订拒绝；用真实 HTTP 验证 |
| P1b 正文与工程编辑 | S3有限后端出口已交付：正文/注音/行/再次导入/设置/来源/元素拆合重排/规则重切，见行为§6；前端闭环独立验收 | 编辑→查询→撤销／重做→保存重开后完全一致；读模型与命令成对交付，不先堆空按钮 |
| P1c 播放与预览，与 P1a／b 联调 | G11波形、现有静态帧及换输入/seek/resize有限后端出口已交付；接续浏览器联调并记录代表输入实际延迟 | 浏览器音频＋波形＋时间轴；编辑后画面对应新修订，持续请求不饥饿，晚到帧不覆盖新状态，能关闭并恢复 |
| P1d 编排联调 | 原定义G20-G24后端已验收关闭：布局/动画、真实同帧测量、前奏/mark/静态Node模板/附着与单句展示，见实施§57 | 前端接入当前91命令、line-presentation与scene-measurement，独立验证选择/编辑/撤销/保存重开；浏览器与用户验收另记 |
| P2 完整渲染输入 | 公共 package HTTP／诊断投影，继续预设、受授权图片／视频／字体闭包；同步扩正式验证与导出能力 | 用含真实包／媒体的代表工程逐项证明普通／透明结果、脚本失败与失控后恢复 |
| P3 场景作者及收口 | B5 草稿／库／编译／采用／发布，B6 协议类型与部署；按实测需要优化帧传输 | 完整作者与应用闭环，完整工程端到端等价、成片及独立部署验收 |

新增命令在编码前明确 JSON 类型、目标稳定 ID、精确修订、历史粒度、no-op、冲突及 changeSet；按 type 区分请求，避免持续扩充无约束字段袋。当前执行 [OpenAPI 1.30.0合同](openapi.json)；前端从固定版本生成类型，W1扩展必须同步合同与真实HTTP验收。已有五命令与回执合同继续保持兼容，合同变更由前后端一起核对。

每批按“HTTP 用例＋对应读模型＋真实浏览器／HTTP 场景＋文档”交付，继续一步一个提交。运行 Web 链路不以启动 Avalonia 为门槛；共享业务和渲染语义继续复用原有回归证据。新性能目标、完整长片及平台矩阵需单独确定范围，本次不恢复执行。

## 5. 文档与代码导航

| 路径 | 用途／读取顺序 |
|---|---|
| [docs/architecture/backend-api.md](../docs/architecture/backend-api.md) | **当前唯一 HTTP 合同**：§1～4 鉴权／回执／SSE；§5 资源；§6 编辑；§7～8 持久化／任务；§9 预览；§10～13 正式任务与媒体准备；§14 部署 |
| [docs/roadmap.md](../docs/archive/history/roadmap.md) | 已完成范围的权威记录，当前G1／启动基础为 §18.200；此前交接与步调调整为 §18.199 |
| [docs/backend-extraction-progress.md](../docs/archive/history/backend-extraction-progress.md) | 各批实现与实际验收，§27 为 B4-I，§28～30 为 B3-E／F／G |
| [docs/backend-extraction-plan.md](../docs/archive/plans/backend-extraction-plan.md) | 总体拆分、状态归属、B0～B6 完成定义及本次优先级调整 |
| [docs/backend-b3-completion-plan.md](../docs/archive/plans/backend-b3-completion-plan.md) | B3 资源／Worker／完整预览缺口；历史计划与已验收切片分开读 |
| [docs/backend-b4-plan.md](../docs/archive/plans/backend-b4-plan.md) | B4 正式任务、音频及 A～I 有限合同，完整 B4 仍待完成 |
| [docs/backend-server-runbook.md](../docs/backend-server-runbook.md) | 启动机制与生命周期背景；含旧版本示例，当前版本／部署命令优先用本文件及统一 API |
| [docs/decisions.md](../docs/archive/decisions/decisions.md) | 业务语义与架构裁决；ADR-0202～0220 记录近期后端合同 |
| [docs/architecture/render-entrypoints.md](../docs/architecture/render-entrypoints.md) | Runtime 到像素及不同入口的边界；历史源码调查不代表所有能力已开放 HTTP |
| [docs/backend-api-v1-draft.md](../docs/archive/plans/backend-api-v1-draft.md) | 未来设计草案，不能作为前端当前接口清单 |
| [src/MakoKaraGenerator.Backend.Server](../src/MakoKaraGenerator.Backend.Server) | Program 组合根、实际路由、鉴权及 PreviewBackendService；前端核查接口从这里开始 |
| [src/MakoKaraGenerator.Backend.Contracts](../src/MakoKaraGenerator.Backend.Contracts) | 当前 HTTP DTO；其中 Worker DTO 属私有 IPC，浏览器不消费 |
| [src/MakoKaraGenerator.Backend.Hosting](../src/MakoKaraGenerator.Backend.Hosting) | 工程会话、唯一写者、任务、资源、预览与 Worker 生命周期 |
| [src/MakoKaraGenerator.Application/Editing](../src/MakoKaraGenerator.Application/Editing) | 可供新 HTTP 编辑命令复用的共享用例 |
| [src/MakoKaraGenerator.Backend.Worker](../src/MakoKaraGenerator.Backend.Worker) | 无 GUI 渲染／正式任务／媒体准备执行体，独立发布 |

前端接口差异应记录成可复现的“所需操作／当前路由及响应／缺失数据或命令／验收场景”，由后端逐批补齐。每次接口交付按执行合同同步更新行为正文、OpenAPI、示例与指纹，再更新本文件的能力与待办；单纯更新设计稿不记为实现完成。

## 6. W0 后端缺口临时补齐计划（依据 Web 设计 §9）

本节对应 `/mnt/Data/Program/MakoKaraGenerator-Web/docs/web-client-w0-design.md` §9 的 G1～G6，属于临时施工顺序，不改变协议版本或把设计稿内容提前写成已实现能力。

| 缺口 | 临时处理 | 当前状态 |
|---|---|---|
| **G1 客户端租约回收** | 先做。登记达到上限时惰性回收已过租约、无工程、无活动任务、无在途操作且不再需要保留回执的 client；采用两阶段 claim，避免在 ClientRegistry 锁内调用 Hosting。有效租约、工程、运行／排队任务、SSE／资源读取和关键回执均保护；回收时清理进程内 client-local 状态，不删除持久工程、ledger、正式产物或仍被引用资源。 | **0.75.1 已实现并有后端验收证据** |
| G2 非 HTTP Origin | 暂缓。W0 浏览器使用明确 `http(s)` Origin；Tauri／`file://` 需要打包方案后再决定同源托管或扩展规则。 | 不实施 |
| G3 多态命令 DTO | 排在 W1 命令扩展前。先冻结每个命令的 type、稳定目标、expectedRevision、历史粒度、no-op、冲突和 changeSet，再改 DTO；兼容当前五种命令。 | **0.77.0已交付，保旧五wire的严格分型** |
| G4 OpenAPI | 2026-10-16首版冻结52路径／58方法，当前冻结70路径／77方法的OpenAPI与行为源，提供类型/路由/指纹检查；未来W1另走合同变更。 | **当前接口合同1.24.1；前端整体同步与浏览器验收待接续** |
| G5 资源上限 | 暂不为开发绕过默认16 MiB。产品需要更大资源时另定能力／配额合同，并保持单文件、总量、流式读取和租约边界。 | 暂缓 |
| G6 W1 命令与读模型 | W1首批：来源列表和完整歌词投影、打点／清除／微调／平移；随后正文／注音、行操作、导入、工程设置、来源／元素结构操作及波形读取；模板库与四种预设分配命令已补入底层合同。 | S1/S2时间/S3/G11、静态样式、模板库与工程分配有限后端出口已交付；前端W2与完整渲染仍独立 |

### 6.1 G1 实际合同与验收边界

- ClientRegistry 只负责凭证和生命周期状态；不在其内部锁中调用 ProjectSessionRegistry、任务、预览或资源清理，避免反向锁死。
- 登记配额不足时先原子 claim 候选，再在锁外检查 Hosting：无未关闭工程、无未完成 job、无未到保留期限的终态 job、无在途或有效 receipt／认证操作；任何不确定状态都保护 client。claim 期间的 resume、heartbeat、认证和新操作失败或让候选回到 protected，不能被并发回收。
- 认证请求取得 operation pin 并在请求／SSE 生命周期结束释放；预览和流式资源读取按已有租约保护。晚到的预览通知不得在回收后重新创建 client 的事件流状态。
- 终态 job 的持久报告、ledger、项目文件和仍有引用的资源不随 client 删除；仅清除不再需要的内存索引、事件、回执和可安全释放的 client-local 句柄。
- 回收不表示注销工程或自动 discard；有工程或活动任务的过期 client 保留，继续通过 resume 恢复。只有真正空闲且无保护依据的 client 才能被移除。

定向回归覆盖：连续超过16次过期登记、有效租约保护、工程／运行任务保护、在途请求保护、resume 与 claim 竞争、空闲回收后旧凭证拒绝、SSE／预览晚通知不复生事件状态，以及持久报告／工程未被删除。完整 W0 浏览器出口仍由外部 Web 项目验证；本仓库本轮只负责 G1 后端合同、定向测试和文档。

### 6.2 本批后端启动与 HTTP 证据

统一三commands实际exit0，正常solution零警告错误；Hosting711＋self-contained Server46＝757／757，无skip／abort，1375输入SHA无漂移。构建、两TRX、部署与HTTP证据分列于实施§31，不与旧B3／B4批次合并。

默认生产TTL／配额真实链路：`artifacts/backend-g1-w0-0751/http-default-final2/summary.json`，79请求／491断言；短WAV真实Worker准备、歌词改时／撤销／重做、静态PNG完整RGBA解码与SHA／尺寸／不透明alpha／可见歌词、保存终态→关闭终态→重开、正常SIGTERM及同profile重启后旧access401／新身份打开已通过。两Server正常exit0，12个唯一实际观察进程身份全部gone。PNG只核内容存在及传输身份，不冒充独立Runtime像素oracle。

G1受控真实HTTP独立归档于 `http-g1-short-lease-final1/summary.json`，显式测试lease2秒（生产默认300不变）：70请求／494断言，同一Server登记64个新身份，16有效租约时配额429、过期空闲回收与旧access／resume401通过。并发同profile拒绝和ready后自有Server异常退出均使启动器结束且不泄漏自有bootstrap，见 `launcher-checks-final1/summary.json`。

首轮publish两端成功但工具误判`script-refs/net10.0`路径，整体exit1，保留 `deployment-final1`；修工具后 `deployment-final2`成功。最新默认HTTP轮79／491之前还有89／541成功轮，仅该首轮进程采样未遍历全部.NET线程的children，作为原历史单列，不合并计数。部署与最终证据SHA见实施§31。**这些只满足本仓库的后端前置条件；外部Web项目的W0浏览器协议客户端、bootstrap桥接、SSE重同步和实际页面闭环仍需独立开发验收。**

<a id="collaboration-established"></a>

## 7. 前后端协作建立记录（2026-10-16）

后端 Codex `CoralCarp`（`/mnt/Data/Program/NewMakoKaraGenerator`）与前端 Claude `PinkPike`（`/mnt/Data/Program/MakoKaraGenerator-Web`）已通过 MCP Agent Mail 完成健康检查、各自项目登记、双向联系批准、跨项目 PING／PONG 及收件确认。线程 `frontend-backend-collab-20261016` 中，消息 #3 为提案与 PING，#4 为匹配标记的 PONG 与逐项补充，#5 为后端确认；#3／#4 均已 acknowledge。验证包含对方实际回复；投递与收悉不代表会话自动唤醒。

双方确认的职责、任务编号、文档权威、合同更新、交付与验收规则统一维护在 [AGENTS.md §8.4](../docs/archive/history/AGENTS-20261029-history.md#frontend-backend-collaboration)。前端由 Claude 更新其 `AGENTS.md`，`CLAUDE.md` 保持引用入口。缺口 G#／R# 跨阶段连续、分任务线程交流；候选合同必须固定提交并明确 mock 状态；后端交付提交／版本、部署与启动器变化及真实 HTTP 证据，前端整体同步合同后回报客户端和浏览器结果。

本次只建立协作规则，产品仍为 0.75.1，API 合同仍为 1.0.0，不新增已实现接口。前端 W0 的实施与测试由 Claude 在其设计文档 §12 报告，本仓库未在本次重跑前端测试；G7／G8／R1 与后续 W1 范围继续按用户安排接续。
