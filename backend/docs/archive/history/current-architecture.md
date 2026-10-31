# 当前架构盘点（0.49.2修复版）

> 历史文档提示（2026-10-24）：下文描述拆仓前的桌面架构与当时状态，不能作为当前无窗口后端的能力结论。当前分支的完整项目图、模块说明与冗余/测试审计请阅读 [后端架构审计](../research/backend-structure-audit-20261024.md)；HTTP 行为仍以 [统一 API](../../architecture/backend-api.md) 和冻结合同为准。

2026-10-11 补充：[独立后端与 Web 前端可行性调研](../research/backend-web-feasibility-report.md)基于 `f7940d4 / 0.49.14` 核对当前组件、性能调用链和拆分成本，并评估“暂时保留 Avalonia、先拆独立后端；Web 为长期前端，Tauri 为可选桌面外壳”的方向。该报告属于调研与建议，未实施后端服务或 Web／Tauri 客户端；下文历史架构及当批验证记录保持原范围。

后续设计入口：[独立后端拆分方案](../plans/backend-extraction-plan.md)与[前后端 API v1 草案](../plans/backend-api-v1-draft.md)。用户已确定 Avalonia 继续可用、逐步复用共享后端用例，首期不要求全部改走 HTTP；方案按业务切片安排同步修复与验收。当前仅完成设计文档，尚未新增后端服务、Worker 或网络接口。

> 本文只记录已实现代码。日期：2026-10-09。
> 0.49.2按R13／ADR-0181补整形回退缓存：完整元素的自动回退及缺字结果每FontSet最多1024项／64个UTF-16代码单元，已持有族不重开；字号、字重、粗斜体及显式链／引擎隔离与原生释放保持。公共整形API／作者SDK、格式及程序集保持，Rendering76／App5、Headless417及三项编译后变异通过。原fixture五次trace及精确原执行体对照全部记录，局部查找累计采样减少而整体P99仍波动，F6b排队边界及最终连续／人工路线继续。证据见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.154；全量仍暂停。
> 0.49.1已接通F8c2：当前317处AXAML模型绑定、17个项目模板及12处C#数据绑定明确实际类型，命令范围与父／子DataContext分开，当前无需生产反射例外。实际模板／上下文／错误／保存门禁与零Binding警告通过后移除App全局关闭开关，固定12.1.3及当前MSBuild默认为true。材质槽不重建未变集合，模板字符串不遍历包材质，只读配方标签和空行标题安全。App135／Core57、Headless665、六项编译后变异及一项编译门禁通过，详见[作者模块与绑定](../legacy-ui/authoring-module-and-bindings-contract.md)／ADR-0180／roadmap §18.153。F8c定向范围收口，最终连续／人工路线及F6b剩余UI延迟仍待；全量继续暂停。
> 0.49.0已接通F8c1：无当前入口的旧实例／基线窗口、专属VM和请求事件／直接静态命令退出产品；共用样式字段、原始输入／编解码器和真实绑定说明迁入App.Authoring。IStyleDraftContext明确LivePreview／SupportsProperty，工坊Composite范围及场景显式提交保持，独立脚本配置入口不受影响；历史内核回归输入只在测试程序集。四样式视图明确类型并局部编译绑定，实际上下文／错误／命令状态受检。App133＋真实后端19、Headless561及三项编译后变异／一项绑定编译门禁通过，当前solution零警告／错误。合同见[作者模块与绑定](../legacy-ui/authoring-module-and-bindings-contract.md)／ADR-0179／roadmap §18.152。F8c2剩余视图和动态例外、全局默认及最终连续路线继续；暂不跑全量，F6b剩余UI延迟不由模块迁移推定解决。
> 0.48.0已接通F8b：Runtime.Hosting的RuntimeFrameValidator接收固定快照／帧计划，返回结构化进度、完整发生及来源诊断；Authoring模块拥有固定试样构建、真实媒体和等待生成／释放的独立目录租约。App仅捕获UI请求和格式化中文。BasicLayoutOperation及拆分引用映射迁入Application，生产仍只引用Core；UI输入新节点名称并映射结构化错误，确认、精确选区及一次撤销保持。宿主16／布局21／App56、Headless通用332／布局确认29／分页11及四项编译后变异通过。合同见[验证与布局](../../runtime-validation-and-layout-contract.md)／ADR-0178／roadmap §18.151。新增公开用例与作者服务，格式、SDK、中立接口及固定依赖保持；F8c旧入口／编译绑定和最终路线继续，暂不跑全量。
> 0.47.0已接通F8a：Runtime.Hosting独立于GUI，组合固定快照编译、原生资源装配和正式像素会话；App组合根只创建并注入工厂，Export仍通过中立接口消费。会话渲染／取消／关闭串行、释放顺序及完整来源诊断保持，未引入共享native单例。新增无Avalonia的宿主测试项目，检查实际固定包、双输出帧、取消／释放及完整依赖图。合同见[正式宿主](../../runtime-hosting-contract.md)／ADR-0177／roadmap §18.150。MINOR0.47.0新增生产宿主程序集，格式28／16／6、配置1、profile2、作者SDK与固定包版本保持；F8b验证／布局用例、F8c旧入口／编译绑定及最终验收继续，暂不跑全量。
> 0.46.0已接通F7b：Rendering.Abstractions的串行RuntimeDiagnosticBuffer共享32条不可变块，RuntimeDiagnosticSnapshot保存完整稳定发生、Count／Error标记及按需兼容数组；RuntimeDiagnosticCursor按代次／顺序读取新后缀，分数／绘制后缀独立。正式Scripting、Skia、缓存、预览、导出及运行验证消费快照，Error来源／门禁保持；导出报告和验证模型保留完整发生，界面最多50项一页。180秒Warning正式路径诊断读取58,336,201降至21,601条，累计分配约6100 MB降至504 MB；明确完整数组及Records仍有重复物化成本，不泛化为全部线性。合同及真实证据见[诊断快照](../../scripting-diagnostic-snapshot-contract.md)／roadmap §18.149／ADR-0176。格式28／16／6、配置1、profile2、SDK报告方法、固定依赖及生产程序集保持；F8正式宿主／用例／旧入口和最终验收继续，暂不跑全量。
> 0.45.5已接通F7a：SDK内部ValidatedEventTable由执行宿主在每个重放代次完整校验一次，固定ReplayReason；TimelineHistory保存共享表、可见数量和边界，同刻事件逐条可见，旧快照不增长。GetStarts只访问可见索引，公开Records保持ImmutableArray签名、显式访问时物化并缓存；公共构造严格校验保持，没有作者可信开关。回退／取消及输出变化重新建表，独立端点及桥接缓存语义保持。10／30／180秒成本、校验／复制计数、过去／未来端点及双输出全部编码帧证据见[历史前缀](../../scripting-history-prefix-contract.md)／roadmap §18.148／ADR-0175。显式Records仍有成本，F7b接续见上文，当批尚未交付的F8宿主抽离现已在0.47.0接通，见上文。
> 0.44.2已实施修复计划F1／F2a／F2b：导出由VideoEncodingArtifact交接暂存所有权，在验证／释放成功后发布；Media层AudioSessionCoordinator按工程会话及AudioSourceIdentity统一播放器、波形和异步请求，导入／历史／打开／恢复／新建采用同一入口，失败目标可明确重试，普通编辑不重载。同一协调器统一命令／轮询及有身份的后台结束队列，EOF／循环状态与按钮一致，K幂等Pause、Space按下沿切换；SoundFlow内层解码段也过滤旧完成。依赖方向保持，无新程序集或包；其余宿主抽离仍待后续。当前只执行构建与定向回归，详见[修复验收](../acceptance/code-review-remediation-acceptance.md)／ADR-0163～0176；下列三工作区0.43.10完整回归为历史基线。
> 0.44.3已接通F3：Application层TemplateLibraryRepository／ScriptConfigurationRepository统一实际索引字节修订、进程内门禁及OS文件范围锁；生产保存／删除／导入经仓储提交，资源先发布、索引最后替换。成功返回本次固定快照并通知一次，提交后取消仍成功；VM刷新只换目录，来源变化保持旧作者草稿门禁并允许明确新ID副本。独立Core存储继续严格闭包及各自schema，历史不可变资源不删除。任意外部编辑器不参加协作锁，最后检查与替换之间的外部写入仍有边界；F6b目录缓存见下文，当批尚未交付的F8正式运行宿主抽离现已接通，见上文；F6a后台保存见下文。实际验收见roadmap §18.139。
> 0.44.4已接通F4a：模板作者独立快照同时保存最后合法LyricStyle、字段原文／错误／写入选择、复合控件输入、材质槽及配方表单。合法值提交仅同步必要的无待处理派生字段，非法输入进入dirty／历史／离开门禁。材质参数逐项提交与控件保持，隐藏错误阻止保存；复合输入编码只存在会话与撤销栈，不进入持久数据。当前控件保存读取及Thumb／方向盘手势边界已验证；相关原始草稿证据见roadmap §18.140。

> 0.44.5已接通F4b：共用作者请求会话以稳定草稿GUID／代次绑定picker与读取，字段编辑与持久模板ID分离；切目标／离开取消读取，关闭等待读取释放，不等待不可取消picker，迟到结果／错误不污染新草稿。每作者一个请求槽，包导入保留普通编辑并独立一步撤销，`.mktpl`保留原传输门禁；脚本普通编辑接受候选但保持当前字段及包／导出。会话身份不持久化，无新程序集或格式，实际证据见roadmap §18.141／ADR-0168。
> 0.44.6已接通F4c：App内部TaskCommandContext由各作者持有动作、标签／手势、显示与可用判定、当前输入提交及执行互斥。Main选择首页／工程／工坊，菜单、按钮与键盘共用目标；工坊不暴露工程另存为／导出，Ctrl+Shift+S不误存模板，复制走明确动作。独立脚本配置保存与源码修订动作分开，文本撤销优先，离开／关闭等待期间外部命令受阻，内部确认保存仍有效。无全局事件总线、新程序集或数据格式；实际证据见roadmap §18.142／ADR-0169。
> 0.45.0已接通F5a：Media内部OwnedMediaProcess统一背景视频进程、双流读取、期限及显式管道释放；10秒期限覆盖一次探测／取帧的完整IO，调用者取消沿RenderRequest到resolver及source。旧同步生产入口桥接不捕获UI上下文的异步IO；第三方旧接口默认桥不能打断其阻塞。UI视频导入后台准备，只回传纯结果，正式导出取消保护旧片。新增宿主取消重载，无新生产程序集、作者SDK或格式；0.45.1已接通F5b，只有完整帧才发布最近源帧身份，同帧复用而失败／覆写／释放失效，保持每source独立借用缓冲；0.45.2已接通F5c，独立resolver文件以种类／规范路径／固定Hash和长度／视频模式作为键，适配及原名称不进入来源身份；锁外候选在发布锁内复核取消／关闭和胜者，未采用候选释放，关闭分别释放所有条目。无逐帧hash或任意容量阈值，正式每会话独立来源及缓冲保持；证据见roadmap §18.145／ADR-0172。合同见[背景视频进程](../../background-video-process-contract.md)／ADR-0170，实际证据见roadmap §18.143。
> 0.45.3已接通F6a：Application层ProjectSaveCoordinator仅消费UI捕获的ProjectSaveSnapshot，独立单写入任务合并最新自动待办、串行手工保存并按实际写出的Project更新saved引用。Generation／Epoch保护完成，手工写入期间的新编辑仍dirty；离开先失效、取消待办、等待完整事务安全点并仅清理自有恢复标记，OnClosed不再补写。根入口已发布后标记清理失败单独警告，不谎报数据失败。Core闭包／刷盘与独立备份保持，新增应用服务无Avalonia或新生产依赖；大媒体基准、实际进程崩溃恢复及定向边界见[后台保存合同](../../project-save-queue-contract.md)／roadmap §18.146／ADR-0173。F6b接续见下文，不把保存IO迁出等同全部交互性能已解决。
> 0.45.4已接通F6b：Application层TemplateLibraryCatalogue通过只读来源接受仓储并由组合根共享，画面控件显式注入。工程／选择变化仅过滤内存；激活和失效由单后台读取者复核实际索引及必要的完整闭包，文件通知只作提示。隐藏不启动读取，迟到结果及乱序发布受代次和实际字节复核保护。错误保留上次有效目录并禁用新应用／更新，已有工程固定应用及作者草稿不自动升级；关闭释放订阅／租约。普通编辑零目录IO、外部真实进程更新、资源损坏及带窗口Headless基准见[目录合同](../../template-library-catalogue-contract.md)／roadmap §18.147／ADR-0174；剩余UI排队延迟不由此推定已解决，F7～F8继续。
> 三工作区施工依据见[合同](../legacy-ui/ui-workspace-ownership-plan.md)／ADR-0145～0162。当前0.43.10／工程28／歌词16／模板6：共享ScriptConfiguration/schema1闭包、Composite预设、固定PresetApplication、原子事务、严格持久化及正式ProjectSnapshot／静态基线／源材质／脚本准备投影已接入。生产画布的歌词、节点、片段和提示标记从实际装载帧解析同一固定应用；普通／透明像素回归通过。模板制作已成为主窗口第三个同级任务，画面与样式使用“应用对象＋预设应用＋布局”入口，已覆盖工程、来源、行、文字、片段和节点的直接预设管理；歌词任务使用只读样式的 `NeutralLyricsPreview`；模板工坊下属的 `ScriptConfigurationWorkspaceWindow` 首个生产入口和 `Composite` 组合预设创建已接通，高级编排已作为画面任务的内部模式：BasicCompositionProjection剥离预览实例与配方，全部源包材质使用声明回退；CanvasSnapshot、编辑几何和前奏候选共用基础规则，正式Snapshot和导出保留完整应用。四种可视布局与精确分段已接通，未选片段通过scheduleContext保留槽位和时钟；内部编排草稿无论是否处于当前任务都进入退出检查；工坊首页入口、自有媒体／播放和完整固定快照复制已接通；歌词编辑器已移除样式、脚本、前奏与实时打点，来源设置只改源元数据；关联翻译与试听使用中性源采样，LRC一次损失确认保护迟到结果；具名面板、窄窗中央空间与统一普通控件规格已接通；三工作区短作者链、完整闭包搬移重开、普通／透明导出及失败／取消／恢复已自动验证，当前3369项完整回归及零警告构建已通过；本批参数面板人工验收通过，证据见roadmap §18.134，完整连续制作的人工路线另行记录。
> 0.41.1 已接入原生 Suki、首页、两个基础任务及三个临时高级工作区，并按人工反馈修复弹层实底、统一页签和按钮密度、移除视图的重复效果入口（ADR-0144）；当前自动证据及人工验收边界见 [UI验收记录](../legacy-ui/ui-redesign-acceptance.md)。下列脚本版本接续为历史证据，领域模型与生产渲染保持既有合同。
> 0.40.2修正透明导出报告无条件记录x264的缺陷，按实际请求记录ProRes4444参数；导出格式／质量在动态列表填充后设置默认项，修正首次打开选择为空；透明MOV音频报告说明PCM及不直接复制，编码行为、格式及SDK不变。本计划R8同一作者工程的57秒普通／透明完整成片、独立副本／备份及失败／取消恢复自动验收通过，最终3187项全量、0警告／错误构建通过；产物、原生证据与人工路线见[验收表](../acceptance/scripting-r8-acceptance.md)／ADR-0140、roadmap §18.107。2026-10-06用户确认文字／粒子／资源效果均无问题，并接受当前goal完成；总体记录见roadmap §18.108／ADR-0141。下列版本接续保留当批历史状态，原路线高级门禁不变。
> 0.40.1按本计划第5批收口R7自动连续验收：真实工坊制作／模板分享导入／行与元素套用／精确实例编辑／历史及物理重开与独立普通／透明会话同帧，模板来源坏源码及合法容器另链通过。材质影响比较使用完整typed值，资源库存按固定身份排序，窄窗完整诊断独立滚动并保留旧画布。3172项全量、0警告／错误构建及73项宽窄原生断言通过；R8第1步已补公开 CustomInk／LineGlitch／GlassInk 包、作者索引和自动生产回归，下一步为完整成片和备份／取消恢复，用户人工验收与高级门禁继续。见[组合验收表](../acceptance/scripting-r7-acceptance.md)／[R8公开包记录](../acceptance/scripting-r8-public-packages.md)／ADR-0138～0139、roadmap §18.105～106。
> 0.40.0 接通工坊 `.mktpl` 完整模板分享／导入入口：已保存个人模板分享使用完整归档校验与原子替换；整批导入保留身份，精确重复跳过，同身份冲突全部拒绝，合并预算及磁盘当前内容通过后先发布库、再更新清单。实际传输暂停编辑，关闭取消并等待；归档与保存不执行作者，导入选择后的试样沿正式异步会话验证。R7组合、R8及用户人工验收继续。见[模板传输合同](../../scripting-r7-template-transfer-contract.md)／ADR-0137、roadmap §18.104。
> 0.39.0 接通行模板整行／元素族相对目标选择，元素模板固定单元素族；按目标筛选固定包导出，元素族非图元输出只接受 `tokenFamily`，UI 与 Core 同时门禁。编辑已有配方先恢复目标再精确匹配导出，显式作用范围更新保留 `RecipeId` 并在完整参数／闭包校验后提交；切换模板清除尚未提交字段与选择。正式试样按行内全部元素建立单目标实例，保存重开保持目标和包闭包。分享／导入 UI 已由0.40.0接续，R7 组合和 R8 人工验收继续。见[相对目标合同](../../scripting-r7-template-target-contract.md)／ADR-0136、roadmap §18.103。
> 0.38.0 接通模板配方原位编辑与顺序调整：编辑器用稳定 `RecipeId` 载入当前包／修订／导出，名称／参数／导出替换经过完整 `StyleTemplate.Create` 闭包校验；上移／下移只交换有序列表，保存重开保留身份与顺序。编辑模式与新增模式分开，失败保护旧草稿和预览。该批尚缺的行模板 `TokenFamily` 制作已在 0.39.0 接通；`.mktpl` 分享／导入 UI 及完整 R7 组合继续。见[配方编辑合同](../legacy-ui/scripting-r7-recipe-editing-contract.md)／ADR-0135、roadmap §18.102。
> 0.37.0 直接图接续：`CompiledFrameEffect` 将控制器的 DLL／入口与直接图声明分开；纯图由宿主 `DirectGraphEffect` 按固定包修订、资源句柄、参数和声明绑定进入同一准备帧／Skia ImageEffects 路径，不创建假 DLL 或作者加载上下文。当前开放目标为 TokenFamily／Line／TextMark，组图、跨输出、额外依赖及非 ColorOnly 映射仍由生产门禁拒绝。模板工坊的包列表是候选目录，`ReferencedPackages` 只把当前源材质和配方引用的修订交给正式试样与保存。0.38.0 已补齐配方原位编辑／重排；完整 R7 仍包括分享入口和最终人工验收。见[直接图接续合同](../../scripting-r7-direct-graph-contract.md)／[配方编辑合同](../legacy-ui/scripting-r7-recipe-editing-contract.md)／ADR-0134～0135、roadmap §18.101～18.102。
> **0.36.0 R7目标效果与定位：** 歌词行／元素的“相关效果实例”页和展示树检查器列出有序相关实例，包含禁用、多条同绑定及可修复失效引用。Project／Branch／Lines／Tokens稳定源关联在未装载时仍保留，Node／Clips间接关联使用正式几何帧当前BindingId与实际父树；`FrameBindingScope`由UI、Evaluate和Visuals共用。父组仅自身绑定的仿射／透明度仍可能传播到孩子，清单另列当前祖先组关联，不扩大元素／行／标记写权；Tokens可读所属行但不能写行、标记或组。清单以实例ID打开工作区，绑定名称／ID和源订阅分别说明；节点定位遵循Save／Discard／Cancel且拒绝未应用／过期场景。0.35.0入口时间／动态预览边界保持，组图／父捕获、跨输出及其余高级门禁继续；R7组合与R8人工验收尚未收口。见[目标效果合同](../../scripting-r7-target-effects-contract.md)／ADR-0133、roadmap §18.100。
> 0.34.0接续基线：本计划第4批R6自动出口已收口，第5批 R7 模板配方与上下文绑定入口接通：工作区正式画布、参数／播放头／透明／质量、旧图失败保护、来源诊断定位、宽窄切换和关闭等待已经接通；模板工坊固定包导出选择、正式试样、保存闭包和行／元素范围说明已接入；歌词行／元素和场景对象可从当前目标打开同一效果工作区。容器／组高级能力、完整R7组合及最终人工验收继续，见[实时预览合同](../legacy-ui/scripting-workspace-preview-contract.md)／[模板配方接缝](../legacy-ui/scripting-r7-template-recipe-contract.md)／[上下文入口合同](../legacy-ui/scripting-r7-binding-entry-contract.md)／ADR-0129～0131、roadmap §18.96～18.98。
> **0.34.0 R7 上下文入口接续：** 工作区使用真实 ProjectSnapshot 与正式异步会话；输出身份包括快照、时间、解析目标、透明模式、质量和布局，迟到帧按当前输入与视口再次校验。失败帧保留上一成功像素并传递不可变运行／编译／材质／图诊断，已保存且匹配工程的失败才反馈所有者导出门禁；“验证工程效果”实际渲染当前时刻。模板工坊从固定包闭包选择行／元素控制器或图导出，严格解析参数，正式试样通过 `StyleTemplateApplicator` 写入来源、绑定、实例和输入指纹；歌词行编辑器显示当前行／元素的实际模板实例范围，并可从本行／本元素入口打开统一效果工作区；场景对象检查器以 Node 绑定打开同一工作区，默认包含后代。窗口宽度低于1250逻辑像素进入显式编辑／预览页，保存源码、接受修订和运行仍分开；关闭先停止播放、取消并等待画布工作器与显式编译，再释放会话。其后真实时间与相关实例接续见上文0.36.0；组图／父捕获及最终R7组合验收仍未完成。

> 0.27.0 已接通封闭混合 Overlay 批次、世界线段／带状拖尾、Custom 粒子绘制和完整 VectorTrail 包。图元在正式准备帧及 Skia 画布排序与计费，图／组复制保留完整输出；宿主继续真实模拟，作者纯视觉求值输出。Bridge、图片精灵和完整 R6 继续，见[向量图元合同](../../scripting-vector-overlay-contract.md)／ADR-0123。
> 0.26.3 已补齐参数／源码独立撤销、单实例修订接受和热替换回收，以及真实 MP4／ProRes4444 中途取消、旧输出保护及新 token 重新导出；预览、独立会话和保存重开像素相同。结合 0.26.0～0.26.2 证据，收口本计划第 3 批 R5 自动出口；下一步 R6。见[修订与取消验收](../acceptance/scripting-session-revision-acceptance.md)／ADR-0122，完整 SDK、R3.1 和 R7／R8 人工验收继续。
> 0.26.0 的运行诊断来源见[合同](../../scripting-runtime-diagnostics-contract.md)／ADR-0119；0.26.1 的持久文字／音频／FPS 见[验收](../acceptance/scripting-session-combination-acceptance.md)／ADR-0120；0.26.2 的模式／目标变化及迟到结果取消规则见[预览输入合同](../../scripting-preview-input-contract.md)／ADR-0121。
> **0.31.0 完整粒子包及本计划R6自动出口基线。** `IRenderFrameSessionFactory` 将 ProjectSnapshot 装配为独立异步会话：预览后台串行合并请求，导出在编码器启动前建立自己的会话；固定 shader／图／材质／包图片在会话外验证并纳入预算，失败保留上一成功像素并阻止导出。模板工坊现在编辑未唱／已唱／描边完整材质及包参数，模板卡与中央试样统一走正式异步会话；脚本效果工作区分栏管理不可变源码包与工程效果实例，支持目录／`.mkfx`闭包导入和原子分享；源码草稿按包修订与文件保存，切换文件或包不会丢失编辑。所选新修订独立编译并显示可定位的真实诊断，显式接受只升级所选实例并保留入口和参数。保存工程效果与验证正式会话分开，失败源码仍可提交和写盘；新建实例初始禁用。含效果的布局编辑按真实时间采样，暂停仍可改基础布局；Token／Line／Mark 的完整捕获、部件层序、Coverage、下层、透明旁路和预览／MP4／Alpha 同帧证据已收口；工程及备份的歌词、媒体、分析和图／材质包可在同一闭包中搬移重开，后置包发布失败保留旧入口；自动备份完整目录发布，同秒不覆写、失败不公开半成品，旧恢复现场保留并可重试；组图绑定在正式主 Render 与单绑定执行入口返回 `GroupImageEffectUnsupported`，父级合成仍属 R3.1；模板分享、工程副本及备份搬移后在已有／空 DLL 缓存保持普通／透明同帧，缓存不掩盖当前资源错误，损坏 DLL／PDB 在编码前失败；本计划第 2 批持久闭包已自动验收；七阶段、音频门禁和图／材质／组隔离／释放错误保留实际控制器包／修订／导出，准备帧 Info／Warning 进入普通文字及合成的像素帧、缓存和导出，不改变像素。本计划R5／R6自动出口已验证；原完整SDK／R6高级输出、R3.1、R4／R7高级输出和R7／R8工作区及人工验收仍未交付。
> 历史0.41.1交付基线；当批工程／歌词／模板 schema：26／16／5。通用编排及前奏内容、准备显示和自动生成已实现，构建／测试及 0.9.0 用户人工验收通过。0.8.1 体验优化、0.7.1 注音及更早验收记录保持；R0 脚本合同已接受，R1 最小编译／原生 shader 探针已实现，R2 已实施源／场景目录、固定字形、纯帧求值／准备帧、生产元素族缩放、固定帧服务及事件／订阅／历史协议及静态元素基线／Style writer；受控共享语义会话／诊断、RepeatEmphasis、最终播放头及几何查询、场景目标／标记生产输出和整行运行父树仿射、六阶段及生产圆点已验证；0.9.12 已实现基础离线音频分析及初始化 required 门禁，0.9.13 已实现严格 shader 原语与 SDK uniform 值，0.9.14 已实施中立图和独立 CPU 五类操作／合成／透明旁路，0.9.15 已实现生产捕获、中立图 writer 及真实绑定执行适配；0.9.16 已接入会话／共享帧表面账、存活产物与显式临时图层计费；0.9.17 已实现保留历史替换范围的合成链与真实工程媒体／底色输入；0.9.18 已实现六类真实彩色部件、共同 alpha 分离和显式顺序重组；0.9.19 已实现 R4 独立内容闭包、目录／ZIP 传输与固定 C# 投影；0.9.20 已实现九类包参数、控制器语义及清单到生产帧／像素路径、真实图片解码门禁和包展开预算；0.9.21 已实现独立包 shader 程序声明／真实原生门禁及固定逐帧集合；0.9.22 已接通包图编码／逐输入真实捕获及完整解码图片图输入；主画布当前元素／整行／标记的图与部件协调、真实下层及组共同alpha已接通；区域材质已有真实输入／执行接缝、准备帧与主Render自动材质调用及实际成片；0.15.0已接入工程持久效果包／实例和真实控制器投影；0.16.0已接入完整媒体／分析内容地址与固定音频投影。0.17.0已接入模板配方／资源闭包和幂等套用；0.20.0接入异步运行装配；0.21.0接入材质工坊选择／参数／真实试样；0.22.0接入效果实例编辑工作区；0.23.0接入包闭包导入／分享；0.24.0接入跨文件／跨包源码草稿、新固定修订独立编译与诊断定位、显式单实例接受及失败源码保存；源码保存或编译成功不自动升级已有实例。U0运行组合根已经完成；0.25.4 已验证工程入口同时持有歌词、媒体、分析和图／材质包闭包，0.25.5 已验证后置包发布失败保留旧入口，0.25.6 接通自动备份完整目录发布和恢复重试；0.25.7 完成固定 DLL／模板／工程资源与失败源码工作区的组合验收；0.26.0 修复 R5 实际包来源及作者提示传入像素帧的接缝，0.26.1 补齐两个公开文字包并验证持久化、三种 FPS、音频、前奏和取消重建；真实普通／透明成片保留音轨并同帧对照；0.26.2 补齐预览输出输入与取消接缝，0.26.3 补齐参数／源码撤销、热替换释放和实际编码取消／恢复，收口本计划 R5 自动出口。0.32.0已接入R7实时预览／诊断／宽窄布局／关闭等待；0.33.0已接入模板配方正式试样、包闭包保存和行／元素范围说明；0.34.0已接入歌词行／元素和场景对象的上下文效果入口，0.35.0保留真实时间并区分静态参考，0.36.0接入相关实例清单及精确定位；0.38.0接入模板配方原位编辑／重排，保持RecipeId及精确包闭包。0.39.0接通行模板元素族制作与显式目标修改。0.40.0接通模板分享／导入入口，0.40.1完成本计划R7连续自动验收及三项缺陷修复；组图／父捕获、跨输出计划和最终用户人工验收继续，当前证据见[R7组合验收表](../acceptance/scripting-r7-acceptance.md)／ADR-0138、roadmap §18.105。

> 0.31.0：完整VectorTrail／SpriteTrail包1.1.0导出Bridge参数，默认Jump保持；EventBurst包从真实Token／Line起唱、当前播放头及TextMark可见事件登记意图。宿主统一出生、独立最终几何、拖尾和四类预算，整行ColorOnly图实际像素位移不改变语义锚点。保存重开的混合包／双文字／音频／Glitch、正式视频、热替换及释放有自动证据；目标内捕获、完整R6及高级输出继续，见[粒子包合同](../../scripting-particle-packages-contract.md)／ADR-0127。
>
> 0.30.0：Bridge 在正空档末段沿独立最终端点发射，每条合法并发边独立配额；模拟器裁切网格内飞行起点，语义时轴不增加边界。来源 Bridge 保存真实 From／To／转换／范围，空档不伪造 Current，终点半开。端点缓存只有固定值，未来作者错误保留真实来源，原出生／寿命／拖尾／随机／四类预算继续共用；完整 CursorTrail 1.1.0 包已进入持久工程和正式预览／视频，目标内捕获及完整 R6 继续，见[桥接合同](../../scripting-cursor-bridge-contract.md)／ADR-0126。

> 0.29.0：TimelineProjection 保存与正式播放头共用 Current 源／装载投影的 CursorTransitions，固定转换事件进入 callback／历史。GetCursorSuccessors 按真实输出绑定检查两端，project 源订阅不扩大输出范围；同时起唱形成并发组，不猜唯一后继。SemanticAnchorQuery.ResolveSuccessorsAsync 通过独立 AnchorQuery 返回全部边及 EndUs-1／StartUs 最终端点，保留包来源、Error／取消和释放，不执行发射／粒子／视觉。Bridge 发射尚未开放，下一步接入；见[后继合同](../../scripting-cursor-successor-contract.md)／ADR-0125。

> 0.28.0 已接通 SpriteBatch／DrawSprites、真实固定包图片表、归一化裁片／世界仿射与 Tint。宿主验证资源归属，OverlayOwners 保留实际编译／包来源；主 Render 的同一帧账覆盖两层精灵、文字隔离、捕获与图合成，重复图片借用去重。作者纯视觉求值从真实 Custom 粒子输出图片，不建帧内图片副本。SpriteTrail 完整目录包可分享及持久重开，Bridge／目标内捕获和完整 R6 继续；见[精灵合同](../../scripting-sprite-overlay-contract.md)／ADR-0124。

## 0. 当前 UI 责任与编辑会话

`AppearanceLyricsView`只读当前分支的文本／预设摘要，以稳定行ID接入既有AppearanceWorkspaceView的多选对象和原子事务。MainWindow.Panels管理列表与预设辅助栏的宽窄互斥，窄窗选行保留列表、明确点“选择预设”才打开应用面板，来源变化清除旧目标，空选择保持明确；没有第二套工程状态。`NewProjectPreparation`只准备独立候选工程、歌词和音频，`PreparedNewProject`显式移交播放器所有权；EditorViewModel.ProjectCreation使用原ProjectWorkspace建立未保存基线，自动备份和离开提醒共用。后续歌词导入明确分支，创建分支和导入作为一条既有历史命令；UI在异步文件选择和确认后检查工程身份。Core的PlainTextImporter与原导入登记／编码报告共用，缺失时间不估算。两个表单使用集中高度和正文滚动，动作不随正文滚走。

`ScriptConfigurationRuntimeViewModel`保存一次验证的固定配置、试样选择、进度和独立结果。App的`ScriptRuntimeSample`捕获无标签请求，`ScriptRuntimeValidator`仅转换报告文案；Runtime.Hosting.Authoring的`ScriptSampleValidator`创建自有媒体并调用固定快照执行器，普通／透明各360帧，验证实际像素及完整诊断。不接受当前工程，既有配置和源码保存路径保持独立。窗口关闭先取消并等待会话释放，最后释放媒体租约；运行中作者字段冻结，取消动作常驻。原入口见roadmap §18.132，F8b职责边界见§18.151。

0.43.6画面目录多选以稳定绑定集合预演，具名来源输入与目标一起传入既有原子事务；单目标API保留。AppearanceWorkspaceView.Objects／Inputs拆分目录与映射职责，PresetInheritanceSummary只读调用正式NodePath，不重建工程状态。动态来源字段沿用Suki／UiControlMetrics；播放摘要每100ms刷新，选择和编辑立即刷新。独立脚本运行验证已在0.43.8接通。

0.43.7的EditorViewModel.AudioLoading持有单个请求身份与取消令牌，候选波形／播放器在两次准备完成后才接入既有历史。替换工程、摘掉音频和新请求使旧结果失效；失败保留旧音频。演示初始化校验未经编辑的原工程及实际接入结果后才能建立保存基线，迟到结果不清空历史。IAudioPlayback及媒体服务保持，内部加载／播放工厂用于确定性取消和释放回归，不建立第二份工程状态。

0.43.5画面任务入口刷新当前预设库，预设名称由显式TextSearch绑定消费，不读取包材质的纯色。整首／行／文字快捷选择同步实际稳定绑定；高级对象标题显示真实对象，缺失对象不能回退成工程应用。空库新建Composite具有默认基线，配方动作自动换行；连续制作证据见[验收记录](../legacy-ui/ui-workspace-ownership-acceptance.md)。

0.43.4主导航已移除临时效果工作区和直接工程效果作者入口；脚本制作只由工坊的独立窗口进入，编排只由画面内部进入。`ScriptConfigurationSourceEditorViewModel` 与配置字段职责分开，源码参与统一离开检查；`ScriptAuthorPackageStore` 使用配置库已有不可变包目录保存并发现历史候选，不改配置index或自动升级已保存引用。迟到导入／编译结果检查窗口与草稿代次。实际状态见roadmap §18.128。

`MainWindow` 是原生 `SukiWindow`。`MainWindow.Navigation` 负责首页和任务导航，`MainWindow.Project` 串行处理新建／打开／保存及离开确认，`MainWindow.Feedback` 接入 Suki 对话框和通知宿主。`RecentProjectStore` 只保存应用级路径、名称和成功时间，失败打开不写记录；备份恢复由首页显式进入，失败或取消保留现场。

歌词与时间、画面与样式共用原有 `EditorViewModel`／`ProjectWorkspace`。主歌词任务使用内嵌行编辑器和中性 `NeutralLyricsPreview`；源编辑器已清理样式／脚本与实时打点，只保留文本、时间、注音和关联。画面区通过稳定目标和固定组合预设应用写工程，不直接编辑样式字段。`MainWindow.Navigation` 的 `_hasActiveProject` 区分工程与独立工坊，首页进入工坊不创建工程，工程IO／导出和自动备份都受实际工程状态保护。

`MainWindow.Panels` 管理具名来源、应用对象、预设库、布局和合成面板。窄窗辅助面板分配列宽，中性预览移到源编辑下方；源表单完整滚动。`TemplateWorkshopWindow.Layout` 管理工坊资源库／样式编辑的互斥和宽窗偏好，视图复用草稿控件，预览填充剩余任务空间。高级编排同样保留中央空间。

`TemplateWorkshopViewModel`持有应用级库、自己的草稿历史和固定试样工程；App的`TemplateSampleFactory`只映射显示目标，Runtime.Hosting.Authoring的`TemplateSampleScene`用正式`PresetApplicationsEditor`投影相对范围，不写旧歌词样式覆盖。试样身份按目标更新保留，目标切换移除旧应用。宿主`TemplateSampleMedia`生成自有背景／PCM并完成真实音频分析；App的`TemplateSamplePlayback`只消费自己的`IAudioPlayback`，不会写工程播放头。视图按激活代次拒绝迟到播放，退出取消并等待加载／播放器／正式预览释放后关闭媒体租约，租约仅删除自己的子目录。画面复制入口只传完整`StyleTemplate`快照，工坊新保存取得新ID，工程身份与应用保持。

Application的`BasicLayoutOperation`将单行居中、卡拉OK双行、双行居中、三行居中映射到真实节点与片段；显式接收新节点名称，返回结构化错误和预演结果。App的`BasicLayoutUi`映射原Loc文案，MainWindow确认后由EditorViewModel检查源引用并一次历史提交。精确连续选择会拆分片段，原ID留给最早未选部分；已知片段绑定与固定应用身份按成员同步，不能安全拆分的Application范围配方整次取消。未选槽位与显示时钟、树和前奏保持，无变化返回原工程。

工程28的 `LyricClipScheduleContext` 保存原有序源成员及默认段相邻边界引用，不保存时间或槽位副本；实际片段成员与效果范围不扩大。`LyricDisplayScheduleBuilder` 用完整上下文算原槽位／揭示／隐藏，再只输出当前片段成员；`LyricSceneSampler` 使用完整计划让未选的未来行保留选区内的提前显示。静态编辑几何同样用原序号；`PresentationValidator`、DTO往返和显式源删除统一验证／清理上下文。严格匹配全部基础几何和调度参数后才识别预设，否则显示自定义布局。证据见ADR-0152／roadmap §18.124。

高级编排复用 `PresentationEditSession`；预设制作和效果编辑由 `WorkspaceView` 承载既有作者模型，导航按稳定绑定／实例复用工作区。切换只卸载视图，草稿继续保留；关闭、换工程和退出先依次处理草稿，再确认工程保存。关闭取消／等待作者编译、传输及预览释放；来自不同源修订的提交明确拒绝。保存、编译、单实例接受与正式运行验证继续是不同操作。

`PlaybackBarView` 和同一 `TimelineControl` 在歌词／画面任务保留，模板任务隐藏并使用自有试播。歌词任务显示行和逐字轨，画面任务显示标尺／波形；隐藏轨不再向波形绘制负高度块。正式 `KaraokeCanvas` 沿用异步 `PreviewSessions`，工程状态、播放抽象和输出会话没有复制。

`SukiTheme`、Suki 原生控件与 `UiSukiTheme` 管理应用外观，`theme.json` 只保存应用种子色；`EditorStyles` 限定布局和密度，自绘画布／波形使用专业绘图颜色。Semi、Material 模拟字典和模板部件选择器已经移除；ColorView 按固定版本官方示例加载上游模板，字体及新增依赖许可证随输出复制。

## 1. 主链

```text
project.json（样式、合成、nodes.content／attachment、programs.clips／leadIns／leadInGeneration、lineOverrides）
  + lyricsFile → data/lyrics/<hash>.json（不可变源歌词修订）
  -> ProjectSnapshot
  -> SceneDefinitionBuilder（固定容器、标记内容、源文本／注音定义）
  -> LyricDisplaySegmentResolver（显式片段与隔离的默认片段）
  -> LyricDisplayScheduleBuilder（有前奏时重建准备／演唱与原槽位计划）
  -> LyricSceneSampler.SampleFrame（容器、槽位绑定、阶段、动画、逐字及提示步骤）
  -> SceneTreeGeometry（Core 的固定参考尺寸与真实父子矩阵）
  -> SkiaRenderPipeline.GetSceneFrame（内部布局、联合测量、世界几何）
  -> SceneFrame -> 共享绘制 -> 预览或视频导出
```

`LyricSampler` 是逐字时间的唯一解释器；`LyricStyleResolver` 按默认→工程→分支→行→元素解析样式。所有结果只读源数据，使用整数微秒和半开区间。未定时元素保持未唱状态。

### 0.25.0 组图目标与父合成接缝

`ImageEffectTarget.Group` 现在可以表示真实运行父树中的容器组。工程绑定或显式节点绑定才允许写组；隐藏组也必须通过真实身份、资源、参数和声明校验。准备帧增加 `FinalGroupGeometry` 校验，旧的节点文本角色不能伪装成组。

Skia 后端新增独立颜色／擦除 `ImageCompositionPayload`。静态颜色可在不捕获背景的情况下生成 C/E，孩子共同透明度按绘制顺序分解，父结果继承孩子擦除并取逐像素最大值。该产物目前由测试和宿主辅助接口验证，正式 `DrawImageEffectScene` 尚未消费父级组图；因此组图尚未达到预览／导出成片出口。实现合同见 [`docs/scripting-group-image-composition-contract.md`](../../scripting-group-image-composition-contract.md) 和 ADR-0111。

## 2. 数据职责

| 对象 | 现行职责 | 持久化位置 |
|---|---|---|
| `LyricBranch`／`LyricLine`／`LyricToken` | 源文字、时间、角色、语言、关联、注音与样式覆盖 | `project.json.lyricsFile` 指向的 `data/lyrics/<hash>.json` |
| `LyricPresentationDefinition` | Nodes、Programs、LineOverrides 三个原生集合 | `project.json.lyricPresentation` |
| `SceneContainerDefinition` | 稳定 ID、名称、ParentId、ScenePlacement、固定 ReferenceRect、可选 Layout／Content／Attachment；内容与歌词布局互斥 | `nodes` |
| `CountdownSequenceContent`／`TextMarkContent` | 有序直接子标记 ID 与外观基线；文字标记及部分覆盖，无源歌词时间 | 节点 Content |
| `SceneAttachment` | 固定父组或运行时跟随实际目标文字，位置仍只保存于 Placement | 节点 Attachment |
| `LyricContainerLayout` | Auto 内部默认位置、安全区、宽度；SlotSequence 的有序槽位 ID 与动画策略 | 节点 Layout |
| `SlotAnimationSpec` | 槽位调度及动画参数，不重复保存槽位位置 | 节点 Layout |
| `LyricDisplayProgram`／`LyricDisplayClip` | 源分支、共同根、默认容器、显示策略；片段保存连续 LineIds 与目标容器，不保存重复时间 | `programs.clips` |
| `LyricLeadIn`／`LeadInGenerationSettings` | 目标句、提示容器、时长、启用及制作来源；生成参数和明确排除项 | `programs.leadIns`／`leadInGeneration` |
| `LyricLinePresentation`／`LyricLocalTransform` | BranchId／LineId 关联的局部偏移、旋转、缩放及进入动画覆盖 | `lineOverrides` |
| `LyricBranchPresentation`／`SlotSequenceAnimationProfile` | 分支显示策略、默认投影及纯调度器适配；不作为新 UI 的树编辑数据源 | 不保存 |
| `Project.EnterAnimation` | 工程级默认进入动画 | `project.json` |
| `SceneDefinition`／`SceneFrame` | 运行定义、活动绑定、动画、矩阵、联合边界、源关联 | 不保存 |
| `PresentationEditSession` | 基准工程引用和不可变展示草稿；校验、提交与过期判断 | 不保存 |

旧 `branches`、`regularContainerId`、`exclusiveClips`、行展示 `placement` 等 JSON 键严格拒绝，不转换旧工程；精确程序版本门禁继续生效。歌词源格式和样式模板职责不变。新建工程建立默认程序，未配置源分支的默认投影不由采样器写回源数据。

主歌词默认 Karaoke，其他角色默认 Static；FollowPrimary 在展示程序，源时间策略仍在源分支。显式片段首期要求 Independent＋Karaoke；关联翻译继续在自己的容器排布，尚不开放其分段绑定。

## 3. 通用树、身份与位置

```text
共同父组（可嵌套）
  ├─ 前段容器（槽位序列，任意数量）
  │   └─ 槽位节点 → 本帧绑定文字 → 注音
  ├─ 副歌容器（Auto）→ 本帧绑定文字 → 注音
  ├─ 尾段容器（槽位序列）→ 槽位节点 → 本帧绑定文字
  └─ 前奏提示组 → 有序文字标记（运行时可附着目标文字）
独立翻译组 → 翻译容器 → 最终可见主行关联的翻译
```

容器与源分支身份独立，纯组和空节点可以独立编辑；父子矩阵递归求值，不依赖节点存储拓扑顺序。普通同级节点按 Nodes 排列，槽位子节点在树和轮换列表都按 SlotIds 排列；拖动槽位同步改变轮换顺序，其他子节点使用分开的排序目标，不隐式改变视频图层顺序。双槽／三槽是同一结构的快速布置预设。

节点 ReferenceRect 相对于父固定参考尺寸，根相对于画布。世界矩阵为父世界矩阵 × 节点位置，文字在基础排版之后附加本句 LocalTransform 和动画。局部偏移参照实际槽位／容器尺寸，节点锚点与参考矩形固定，不随换句变化。注音继承最终文字矩阵；有限画布外位置按裁切处理。

持久 NodeId／ProgramId／ClipId、源 BranchId／LineId／TokenId 分别稳定。ElementId 从源身份派生，本帧 ParentId 随装载变化。BindingId 由程序、显式片段或默认片段首末源成员、实际父容器／槽位和源行构成，不混入采样时间、名称或当前列表索引；源时间改变后身份仍稳定，重新分配槽位改变装载身份。首期每句只有一个活动显示实例。

## 4. 通用片段与调度

片段成员必须是源顺序连续、具有有效行时间的 LineIds，起止为首句起点至末句终点，随源时间更新。成员时间须顺序有效且完整落在区间内，片段间不得重叠；非成员演唱区间跨入片段时拒绝。单句居中是一个成员的普通片段。

片段活动时只调度本片段成员；边界直接切换，本句进入动画保留且不跨边界。未分配成员由默认容器接收，按显式片段边界划为相互隔离的默认片段。显式／默认片段都从首槽重建，前后成员不能跨片段提前显示。槽位数量、顺序及容量变化后整段按新规则重新分配。

调度使用 `SlotSequenceAnimationScheduler`，任意槽位序列共用揭示、保留、容量、冲突和进入／退出规则；三槽数量与同屏上限是两个独立参数。冲突或非法容量明确报告，绝不静默修正。绝对时间重建使随机跳转、连续播放和导出共用绑定、进度和几何。

启用前奏时，共用计划使用首个有效元素起点 A（全缺则行起点）和完整时长 D 派生 `[A-D,A)`。目标原片段／槽位及 BindingId 保持，允许提前装载未来片段；目标不得在 S 前揭示，原保留句在 S 终止，后句不得在 A 前抢先显示。进入动画从 S 开始一次，A 切为演唱但不重置装载；源时间不变。提示按绝对整数时间等分步骤，在 A 消失。翻译保持自身位置并取得准备阶段，注音跟随目标基础元素。

注音关联单个 BaseTokenId，围绕基础元素的文本宽度居中，进度跟随该元素；多字源元素可拥有整组读音。0.7.1 已修订演示「请→qǐng／照亮→zhào liàng」，字号比例 0.4。跨元素范围注音合同未开放。

## 5. 编辑、校验与历史

`SceneTreeEditor` 提供按 NodeId 的节点参数、复制／删除、槽位增删、槽位／同级顺序、重挂、默认归属、片段及 LocalTransform 操作；统一由 `PresentationEditor.TrySetTree` 与 `PresentationValidator` 校验。无变化返回原引用，失败返回原工程和诊断。

重挂使用 Core 的同一矩阵：新父世界逆 × 旧节点世界；分解新的局部位置／旋转／缩放，并换算参考尺寸，保留子树世界外观。循环、不可逆矩阵、显示根约束与原槽位容器失效会拒绝。复制生成新的节点及内部槽位引用，不带源歌词／片段绑定；有展示引用的子树不能直接删除。删除片段回默认；显式删除源只清理其关联并保留未使用容器。

`LeadInGeneration` 纯分析源演唱占用与长空档，校验目标绑定，输出新增／更新／移除建议／保留／跳过。手工或禁用条目受保护，首次实际手工修改自动条目转为 Manual。明确应用时重新校验整体结果；不重建提示内容，移除须确认。删条目保留容器，可显式记录排除；删源清理条目／排除但保留节点。提示组复制重映射 MarkNodeIds，不复制前奏绑定。两种附着切换以实际目标静态几何换算，保持世界外观。

`PresentationEditSession` 只接受展示范围变化；App 持有稳定选择、草稿及反馈。数值回车／失焦、拖动松手完成草稿操作，顶部“应用改动”一次通过现有 `Execute`／`AdoptProject` 入历史。“放弃改动”不写工程；外部源修改使草稿过期，应用拒绝并要求放弃重开。源编辑若破坏已有片段连续性或时间合同也拒绝，避免生成失效工程。

打开和导出仍报告阻断错误；保存不是自动修正或迁移入口。工程历史不保存草稿中间帧，渲染从不修改源数据。

## 6. 窗口与共享几何

主窗口按钮进入“展示编排”，统一由 `OpenSceneEditor` 定位源分支与展示根。`SceneEditorControl` 左侧容器树／源歌词，中间共享 Skia 画布，下方依次为歌词来源、默认容器及“展示片段／前奏提示”页签，右侧所选对象属性；返回脏草稿可应用、放弃或继续。布局参考与时间预览直接切换，布局参考标明忽略当前时间并禁用时间滑杆；实际采样入口保持共用。

角色从 Layout 与 SlotIds 派生。歌词容器直接展示自由布局／槽位轮换、槽位列表和独立快速布置，槽位只展示自身位置及所属容器。树右键／“⋯”负责结构操作，属性栏移除复制、删除和上下移动；树边缘排序、主体重挂确认，属性槽位列表也可拖动排序，Esc 取消。树行按稳定 ID 复用并保留展开与滚动，重挂先移出旧父再挂入新父。选择片段高亮目标容器，画布开始拖动时明确切到该容器；选源行仍微调本句。

代码按 `SceneEditorControl.Navigation`／`Tree`／`SlotDrag`／`Inspector`／`Dialogs`／`LeadIns`／`Generation` 拆分；`SceneTreeInteraction` 解释意图并复用 Core。标记树次序按 MarkNodeIds，拖动改变倒计时次序但不覆盖位置；等距布置是显式操作。生成对话框临时节点与预览不写草稿，加入时统一校验，再由顶部一次应用。行编辑器仅显示关联摘要并导航至编排。

画布 `GetNodeGeometry`／`GetLocalTransformGeometry` 消费最终 SceneFrame，空节点使用固定参考矩形；父组边界来自后代联合几何。拖动用实际父坐标逆矩阵和参考尺寸，吸附参照父边缘／中心与其他歌词；Alt 暂停吸附，方向键微移，Ctrl 点击和重叠命中展示候选对象。

标记先解析规范样式，再由同一 Skia 文字管线整形绘制。跟随参考使用目标文字／注音局部联合墨迹左侧中心，乘目标最终矩阵一次；固定参考使用持久父组矩阵。最终几何、父参考、继承动画透明度和外观进入 SceneFrame，联合边界、命中、拖动及导出消费同一结果。布局参考提供稳定实际目标样本；时间预览禁用位置拖动，避免把动画中间姿态写回。

实际工作区宽度小于 1100 时，左侧约 208 px、属性为 300 px 显式覆盖面板，画布不被额外挤窄；宽区是 224／余量／300 三栏。应用／放弃／创建入口固定可见。新建容器满宽，“分配歌词段”独占中央编辑区域，新建片段位于标题行右端且只一处；属性关闭在标题右端，重置在位置字段组右端。旧固定槽位一／二目标、双行专用表单和批量位置窗口已移除；分支窗口仍编辑源语义与样式，并提供通用编辑器入口。模板工坊和歌词行编辑器职责保持原合同。

## 7. 证据与后续

全量构建 0 警告／0 错误，1938 项测试通过（最新 R1／R2 证据见下文）。0.8.0 完整六句制作、当前格式往返、父组重挂、局部微调、片段边界、三槽同时可见和真实 MP4 对比由全量回归继续覆盖，历史证据见 [实施合同 §8](../legacy-ui/scene-editor-implementation-contract.md#8-080-实施结果与验收路线)。0.8.1 新增 10 项 App 测试与树顺序变异检查；真实控件探针覆盖菜单、树／槽位列表路由指针事件、重挂确认、状态保留及宽窄内容区，证据见 [体验优化方案 §13](../legacy-ui/scene-editor-ux-optimization-plan.md#13-081-实施与验收) 和 roadmap §18.23。真实控件截图与 HTML 原型分开记录。0.8.0 基本编排、0.6.0～0.7.1 历史验收保留；用户现已明确 0.8.1“测试验收通过”，未将这一答复扩大为历史每项边界均已逐项验收。

0.9.0 新增 32 项前奏测试，覆盖模型／当前 DTO、采样、原生像素／矩阵、24／30 fps MP4 对比及 App 草稿／历史。真实 Avalonia 控件探针检查宽窄布局、唯一入口、14 项外观、标记编辑、候选预览与取消隔离；证据和人工验收路线见[前奏方案 §13～14](../plans/karaoke-lead-in-plan.md#13-090-实施与验证2026-10-02)。2026-10-02 用户确认功能全部正常，前奏及真实容器／目标句绑定符合预期，0.9.0 人工验收通过；不将总体反馈扩写为所有历史边界均逐项人工确认。

脚本新增两个中立程序集。Scripting.Abstractions 已有独立身份、有限几何／时间、完整 Content／Scene 目录、FrameToken／FrameLine／FrameMark／FrameGroup、源 Performance、Token／Ruby 族和九类类型参数。ColorRgba、显式曲线、固定资源句柄、PCG32-v1 索引随机服务及 AudioFeatures 已接通 Initialize／FrameContext；音频输入区分真实静音、未分析、失败和半开越界，Media 已有实际离线分析器，见下文。服务字节和查询合同见 [固定帧服务合同](../../scripting-r2-frame-services-contract.md)。封闭事件、源订阅及 TimelineHistory 已进入 SDK；固定事件投影复用真实源时间与共享场景计划，前奏和可见不重复起唱；订阅与输出范围独立，历史前缀不读未来。协议及验证见 [事件记录](../../scripting-r2-timeline-protocol.md)。`IKaraokeEffect`／`EffectBase` 当前已接通同实例七阶段及 Dispose，包含文字求值后 EvaluateEmitters／粒子准备／AdvanceVisuals／EvaluateVisuals；最终几何视觉上下文和实际圆点输出见 [视觉阶段合同](../../scripting-r2-visual-stages-contract.md)。FrameWriter 支持声明后的元素族缩放和 Style、标记 Style／局部仿射及整行 ApplyAffine 写入，检查目标、资源修订、有限值、独立字簇及调用阶段。BaselineStyles 提供完整源元素／行的继承外观、文字标记的提示组外观＋自身覆盖及仅静态 alpha 的真实组外观，各实例读同一份；Set 后项覆盖，alpha 系数合并后校验，明确 null 关闭装饰。组没有两态文字材质，用户已确认组 alpha＋变换／渲染图（ADR-0073）；当前无静态组 alpha 字段，基线为中性 1。整行 Style／组透明度及组图等其余 writer、材质 uniform／图／映射仍待实施；整行／元素同属性覆盖仍待答。0.11.1 组仿射及仅工程／显式节点写权限已接通；发射／粒子进展见下文。

Scripting 有固定引用编译／诊断、持久 DLL／PDB／可回收加载及投影；`PureFrameSourceFactory` 是明确限于纯外观的受控接缝，每个会话独立拥有模块实例和几何后端，串行准备帧，不派发源事件或执行有状态历史／计数／粒子；独立 TimelineProjection 的前缀查询不等于实例重放。`TimelineFrameSourceFactory` 则执行独立共享语义会话：整数 120 Hz＋精确事件，左端推进／逐项前缀回调／边界求值。FrameSession 重绘不派发、分数帧不推进，回退和取消后重建；FrameEffectHost 共用两个工厂的实例加载、绑定、固定几何、样式和族输出。Error 诊断在阶段结束使求值失败，Info／Warning 仅进入不可变帧；Reporter 限于本阶段。真实 RepeatEmphasis 与注音经同一生产路径绘制。完整源和场景目录不受输出绑定限制；工程／分支／行／元素／片段／节点输出范围单独筛选。编译代次、参数、范围、资源目录、UInt64 种子和分析输入由组合根固定，这还不是持久包／工程实例表。

Media 的 AudioFeatureAnalyzer 复用流式 PCM，显式重采样到 48 kHz／960 样本 hop，最后短尾保留；实际样本决定半开时长。低频为二阶 20 Hz 高通／200 Hz 低通与 100 ms 包络，归一化参照整曲全频音量，不把纯高音残余放大成满值；Pulse／峰检测提供近似节奏和 confidence，没有证据时 phase=null。源和规范 PCM 各有内容指纹，算法／样本数／零点生成固定修订；分析失败不发布部分数据，取消仍传播取消。固定序列及真实 hop 直接注入原工厂，RequiresAudio 在初始化前拒绝不可用输入；音轨外 required 策略已问用户，答复前不新增导出失败规则。没有新增分析缓存格式／脚本 UI，完整闭包按 R4～R5，证据见 [音频合同](../../scripting-audio-analysis-contract.md)。

Rendering.Abstractions 新增 IEvaluatedFrameSourceFactory／Source、PreparedRenderFrame 和具名失败协议。快照的非空 EffectInputsFingerprint 标识需要准备帧的受控输入；R4 才由持久实例和完整资源闭包生成。RenderRequest 的帧须属于精确快照引用、时刻、输出模式和目标密度，布局静态编辑请求不能混用。缓存命中前也验证，按准备帧身份区分像素，不借缓存跳过求值或静默忽略效果。尚未在现有 Avalonia 或 VideoExporter 中启用持久脚本实例；实际用户流程按 R4～R7 接入。

生产 Skia 路径消费准备帧的同一基础 SceneFrame 及层次变换后的 HierarchyScene。静态元素覆盖与脚本变化族共用固定单元路径，修复矩形覆盖重画邻字阴影及裁掉装饰边缘。变化族的 glyph 从原行移除，再与注音一起应用最终外观和局部视觉缩放，已唱／未唱、描边遮罩、阴影及 Alpha 沿用共享样式规则；邻字保持排版。FinalGeometry 与同一矩阵一致，后端明确拒绝超出原生有限坐标或转换到 float 后不可逆的输出。WordEmphasis 作者源码已在真正 SDK 下编译并进入此路径；PreparedRenderFrame.Playback 使用高亮同一进度路径和最终族矩阵，保留全部合法 Current；全行无逐字时间时只提供行依据。GeometryQueries 在四个作者阶段只查询基本布局并在阶段后失效；SemanticAnchorQuery 独立重放最终文字端点，不写主会话未来历史。Typed AnchorMap 仅是有界仿射／波形数学值；transition 后继投影和实际 Mappings／MappedWarp 尚未完成，重叠接续待用户答复。实现、逐字节像素对照及边界见 [R2 记录](../research/scripting-r2-sdk-geometry-progress.md)及 [样式帧合同](../../scripting-r2-style-frame-contract.md)。

0.9.10 的 SceneAffineProjection 使用真实运行父树传播整行局部视觉矩阵，不重新调度或改变字形逻辑起点。附着目标句的前奏跟随，FixedParent 提示和翻译按各自父关系显示；FinalLineGeometry／Token／Ruby／Mark／Group 和 Playback 共用最终矩阵，独立族／标记输出各再组合一次。组参考尺寸保持，墨迹由最终字形聚合；恒等和抵消保留原帧。Tokens 绑定可读行元信息而不获整行写权，错误装载／父树／数值明确失败；合同及实际像素对照见 [层次输出合同](../../scripting-r2-hierarchy-output-contract.md)／ADR-0074。

前奏标记目标包含 NodeId／CueId／目标 BindingId，复用同一节点的两次前奏不共享装载身份。FrameMark 关联真实目标 FrameLine／动态附着透明度，FrameGroup 记录运行父树；GroupGeometry 提供真实参考框／矩阵／枢轴，WorldInkCorners 来自整行固定字形、注音和标记，不以布局参考框伪造墨迹。GetBaseAt 对标记按目标源筛选，对组返回真实显示对象祖先，Project 可读完整组。生产标记沿用固定 glyph 和两态绘制，准备帧保存最终标记外观／矩阵及几何，静态和动画 alpha 各一次；缺失／重复／旧 Cue 几何不发布成功帧。两种附着、装饰／Alpha／仿射与等价静态编辑逐字节像素对照，详见 [场景目标合同](../../scripting-r2-scene-target-contract.md)。组隔离 capture alpha 随 R3 真实合成实现，不将逐后代透明度乘法作为已完成组效果。

0.9.11 的视觉上下文包含绑定内最终几何、同一 Playback、前一／当前边界及明确的历史前缀。0 不推进零长区间，分数帧只呈现；模式／密度变化从 0 重建并记 InputsChanged。纯帧可运行纯 EvaluateVisuals，有状态 AdvanceVisuals 须时间线；AnchorQuery 固定跳过两个视觉阶段及 overlay。VisualWriter 仅在视觉求值有效，Overlay.DrawDots 保存逻辑坐标、直乘颜色及正有限半径，检查声明能力／批次／全实例预算，实际在背景后／文字前或全部文字／提示后绘制。准备帧附加输出时保留文字／几何闭包，原生越限／下溢明确失败。初始 100,000 项预算可由固定工厂显式调整，工程设置持久化随 R4；0.9.23 后续接通 Emitters 连续输出及准备帧粒子视图（下文），作者粒子服务已在 0.10.0 接通；Sprites／独立 Trails 仍待接入，不将圆点绘制当通用渲染图完成。

VideoExporter 保留普通后端错误的占位报告，但 EffectFailureException／PreparedFrameException 已独立传播，中断真实编码并保留原输出。中途失败回归补齐同目录临时输出和成功后发布；清理先等待本会话进程／管道退出。生产歌词的背景绕过协议仍未执行：现行 FillSpec 是纯色／渐变，描边为纯色，生产歌词路径尚未连接自定义 shader／通用图，也没有 GPU；R1 样本有真实 CPU 效果，0.9.13 正式渲染程序集已有可复用的 CompiledRuntimeShader 原语。透明规则、资源格式及工作区按已接受 R0 A／B／UI 和 ADR-0068～0071 实施；源时间／排版仍只读，120 Hz 共享历史及真实 RepeatEmphasis callback 已接通；其余 writer／分析持久闭包／粒子继续按 R2～R6 完成。完整 SDK、六包和成片验收未达成，性能基准后置。

0.9.13 将固定 shader 声明放入现有 Core.Effects，SDK 提供不可变 UniformValue／UniformValues；正式 Rendering.Skia 一次编译并用原生反射严格检查实际类型／数组／color 标记／child，再用固定帧值反复创建 shader。矩阵按行输入、按列打包，颜色直乘，raw coverage 保持数据语义；默认 128 标量可显式调整，错值、缺值和原生范围错误不补值。程序键含实际后端版本，程序／返回 shader／借用输入的所有权分开。锁定版公共 ToShader 的内部数据包装依赖终结器释放，不能据此关闭 R3 全部资源预算出口。本批次没有新 JSON、包资源解析、Uniforms writer 或图执行；后续 0.9.14 图原语见下一段，生产歌词集成仍未完成；详见 [shader 原语合同](../../scripting-shader-program-contract.md)。

0.9.14 新增的 ImageGraphDefinition／ImageGraphCompiler 是 Core 唯一图真值，GraphSurfaceDescriptor／ImageGraphFrameContext 是中立帧接缝；正式 Skia 的 CompiledImageGraph 负责固定 native 程序、Canvas 输入、五类操作及独立产物／合成。图像由调用方提供，不自由捕获已合成自身；真实背景来源和透明 explicit bypass 有结构化诊断。类型／参数／边界及实际 sRGB 预乘字节验证、单次图资源预估／及时释放、缩图量纲与真实 MP4／Alpha 同帧通过；当前全量 1895 项。其局部预算未落实每帧／会话全局账本，生产歌词 capture／图 writer／实例失败门禁仍待接通；完整边界见 [图合同](../../scripting-image-graph-contract.md)／ADR-0078。optional 缺输入已询问用户，当前不填空图或擅自旁路。

0.9.15 由同一固定绘制入口捕获真实 Line／TokenFamily／TextMark／Group，TargetColor 与当前 Render 逐字节一致，raw Glyph／Stroke／Sung／Region 独立于颜色与入场 alpha。真实运行父树判断附着前奏和组后代；显式配方、Canvas 框及实际密度，独立 glyph 分区、取消及预算失败。ImageEffects writer 只在 Evaluate 写描述，沿用真实装载／能力／包修订权限；同实例同目标最后一次整体替换，跨实例顺序进入 PreparedRenderFrame，并在视觉帧复制时保留。ExecuteSceneImageEffect 适配固定图并附加真实实例失败，保留自建捕获／借用前次产物的生命周期；真实歌词与注音的 MP4／Alpha 同帧及中途失败已验证。Render 的主画布调度尚未接通，图绑定不得静默忽略；透明提示 UI、媒体／下层生产、子图到父图及全局资源账本、材质／包／工作区仍继续，见 [生产捕获合同](../../scripting-production-capture-contract.md)／ADR-0079。

0.9.16 的 ImageSurfaceBudgetSession／Frame 是渲染后端的固定资源服务，保持脚本无 native 表面、渲染无脚本执行。Skia 管线借用宿主注入的会话账，也可自建；正式捕获／绑定适配和图 Execute／Composite 可共用帧账。默认每帧 64、每会话 512 MiB 与单图预算分开；按实际像素密度估算，图产物／借用输入保留至结果释放，别名与跨帧同图像不重复计会话字节。Blur 两张 scratch 和捕获显式 SaveLayer 预留已接入，跨实例已有产物参与下一图的峰值预检。1938 项全量通过，真实预算错误在编码 200 ms 保护已有输出、回收中间表面；主画布父子协调器与完整 native RSS 仍未交付，见 [账本合同](../../scripting-surface-budget-contract.md)／ADR-0080。


0.9.17 的 CompositeChain 保留每个实际替换的 raw 范围，在最后一次合成时应用；后续 SourceOver／旁路不能丢失历史范围。同一准备帧、实际实例顺序及共享账由绑定级合成适配校验，错误归属最后实例。CaptureSceneBackground 复用正常 Render 的媒体解析／时刻／摆放，只取媒体或底色，画布外透明；透明输出不解析背景，未提供的工程媒体／底色可由真实绑定执行自动补入，缺失不伪造。具体 API 与已接受排除部件语义见 [合成合同](../../scripting-scene-composition-contract.md)／ADR-0081。正式 Render 仍拒绝未调度图绑定；捕获排除部件分区、子图进入父图及真实 BackdropBelow 尚未接入。

0.9.18 的 SceneColorPart 属于中立渲染宿主合同，不新增脚本 native API。CaptureSceneColorPart 复用固定字形、逐字裁切、最终矩阵和实际外观，提供基础字／注音各 Fill／Stroke／Shadow；组没有自己的文字部件。共同入场／标记 alpha 从部件颜色分离，AssembleSceneColorParts 仅按显式顺序重组同源／准备帧／目标／输出／时刻部件，返回独立表面及尚未应用的共同 alpha；借用输入、局部与共享峰值、取消／释放严格校验。旧无效果绘制和完整捕获路径保持，尚未将部件自动接入效果或主画布父子协调器。具体顺序与像素边界见 [部件合同](../../scripting-color-parts-contract.md)／ADR-0082。

0.9.19 的 EffectPackageContents 是 Core 唯一不可变文件闭包，顶层清单／文件声明、实际 SHA-256、严格 UTF-8、路径树与内容修订校验集中于此。Application 的 EffectPackageTransfer 读取目录或 ZIP，不先解压、不读取外部路径补资源；分享采用同目录临时 ZIP、完整写入后一次发布。Scripting 的 EffectPackageSources 只从已固定 csharp 内容建立 ScriptSource，图片不进入既有 C# 编译指纹；没有生产反向依赖，仅测试引用 Application 走真实目录／ZIP／Roslyn 路径。该内容类型不解析参数／导出语义，内容集合不可直接启用，接续见下段；工程、歌词、模板持久格式仍未改。见 [内容合同](../../scripting-package-content-contract.md)／ADR-0083。

0.9.20 的 EffectControllerPackage 从固定清单读取九类唯一元数据／默认值及全部控制器导出，部分覆盖复用相同范围／类型校验；输出前向依赖可查看稳定拓扑，坏依赖／环拒绝。用户确认 color 对象、三类 curve 和 role:image 路径编码；SDK 仍只读直乘 double／完整 long／曲线描述／图片句柄。后端 IPackageImageValidator 注入正式 Skia 完整像素解码门禁；读取前执行 R0 包展开预算，规范后闭包也检查。CompiledControllerPackage 固定源码与入口集合参与编译指纹，逐导出反射不运行作者构造；CreateEffect 读取清单入口和参数，限制允许绑定及当前真实 writer，生产合成前检查声明外输出，Overlay OutputId 须声明。真实 WordEmphasis 包在两种密度与默认／覆盖值的实际像素、注音共同矩阵及九类 Initialize 输入已验证。材质／图导出、非空 requires、跨输出执行／旁路仍明确失败，不把部分控制器成功当作混合包可启用；尚无编辑器导入入口、图片图消费或工程持久事务。见 [包语义合同](../../scripting-package-semantics-contract.md)／ADR-0084。

0.9.21 的 EffectPackageShaderPrograms 按程序文件路径读取全部 role:shaderProgram 声明，引用同闭包 role:shader；版本／profile／七类 ABI／数组／child 与严格字段集中验证。控制器目录保留完整程序集合，IPackageShaderValidator 注入正式 Skia 编译／反射；缺少验证器或未引用错误程序也拒绝启用，不能只接受控制器。CompiledPackageShaderPrograms 持有一次编译的固定程序，逐帧只换 UniformValues／child，失败或取消清理集合，返回 shader 与程序各自所有权保持。源码／ABI 修改改变包修订和程序键，同 C#／入口 Artifact 不变；不产生新的 GraphHandle。0.9.21 当批只实施补充合同 A；后续 B／C 实施按下文 0.9.22／0.12.0，不将当时待答作为当前状态。2136 项全量通过，见 [补充合同 §6](../../scripting-package-graph-semantics-contract.md#6-a-的实际实施路径0921)／ADR-0085／roadmap §18.50；不构成生产歌词材质、完整 native RSS 或完整 R3／R4 出口。

0.9.22 的 EffectPackageImageGraphs 读取全部 role:graph，复用唯一参数／程序目录和 ImageGraphCompiler，严格解析五类节点、ABI 字面量／封闭绑定、完整取样边界、矩阵／框、图片路径及 required／none 原输入旁路。EffectPackageImageGraph.Captures 按输入保存五个布尔值及区域，SceneImageGraphProgram.FromPackage 适配真实捕获，按来源＋完整配方复用；实际注音差分不误用同一表面。owned overlay／materialRegion 适配未实施时明确拒绝，上一整体产物不能替代逐输入配方，混合部件重组仍待正式协调器。PackageGraphImages 完整解码全部 role:image，按固定字节／表面预算加载；输入必须显式给原图片框／密度，变换通过图节点，不默认拉伸。IPackageImageGraphValidator 注入真实 CPU 验证全部图，缺少门禁拒绝控制器启用；不创建伪 SDK GraphHandle。2171 项全量通过，见 [补充合同 §8](../../scripting-package-graph-semantics-contract.md#8-b-的实际实施路径0922)／ADR-0086／roadmap §18.51。0.9.22 当批尚未确认 C；现行统一导出见下文 0.12.0，生产区域材质、主画布及持久事务未完成；schema 21／15／3 不变。

### 固定时间连续粒子与作者查询（0.10.0）

独立 EvaluateEmitters／EmitterWriter.Set 保存不可变描述，粒子准备先完成，再进入 AdvanceVisuals／EvaluateVisuals；按真实包声明授权。ContinuousParticleSimulation 由同一 FrameSession 持有，卸载、失败和重放清理。每条 `(TrackId, BindingId)` 合法头独立 rate，精确二进制有理配额、整数出生、稳定 BirthIndex；共享 Advance 前纯文字求值出生最终几何，既不执行视觉回调也不猜测边界插值。分数帧在副本读取，不提交出生。

粒子保存世界出生坐标，常速度／常加速度和年龄曲线解析；固定边界拖尾及末端样本保留到寿命结束。同一发射器使用最近边界的层级，出生运动／外观保存出生描述。准备帧 Particles 与公共圆点共享，零位移相同位置不重复叠加。会话级存活／拖尾预算及全部实例绘制预算明确失败并保留真实实例，预览与受控编码使用相同计划。

Jump／Bridge＋SemanticCursor 已接通，向量与图片图元见 ADR-0123～0124；目标捕获及剩余跨输出调度继续 R2～R8。桥接使用全部真实后继、独立最终端点和每边配额，飞行与演唱身份分开；规则见[桥接合同](../../scripting-cursor-bridge-contract.md)，最新完成度和证据见 roadmap §18.93。

0.10.0 作者 VisualStateContext 的 PreviousParticles／Particles 和 VisualFrameContext.Particles 都仅含本实例真实宿主粒子，包括离场后仍存活对象。EmissionSchedule 保存最近完成半开区间的全部出生／固定描述，含期末已死亡；分数帧不替换该记录、不提交临时出生。独立出生记录100000条预算已获用户确认，固定及临时出生流执行同一门禁。CursorTrail 带真实闭包清单／参数并可原子分享为 .mkfx、离开原目录离线编译；仍无脚本导入 GUI 或工程持久实例。具体接口见 [发射阶段合同](../../scripting-emission-stage-proposal.md)。


### 事件 Burst 与真实来源（0.11.0）

同一个 FrameSession 在 OnEvent 为每个实际实例提供限时 BurstWriter；事件顺序和历史前缀不改变，同事件同发射器只保留最后意图。全实例文字输出完成后，FrameEffectHost.Bursts 在输出绑定内解析最终基础 Advance 中心或全部实际播放头，缺少指定锚点使本次求值失败，不能使用旧画面位置。

Burst 与连续发射共用 ParticleDescription、ParticleId、随机流、解析运动／固定拖尾及绘制路径。ParticleSource 保存真实 Cursor／Token／Line／TextMark，只有 Cursor 有 TrackId，Burst 另存真实 EventId。同刻 Burst 在前，不改连续配额。边界缓冲立即向当前 Particles 发布出生，并在下一半开区间只记录一次；已死亡对象的出生记录不丢失。副本读取复制缓冲，不提交或重复事件出生，全部实例预算贯通真实编码失败和原输出保护。

AnchorQuery 接收事件回调但不消费视觉意图，失败／取消／重放清理所有未发布意图和粒子。公开源码 `samples/ScriptEffects/Examples/EventBurst.cs` 使用实际 SDK，前奏可见不会产生起唱 Burst。工程 schema21／15／3 保持，SDK 来源修改按 MINOR 交付；正式工程效果保存、完整 R6／主画布及工作区仍未交付。细则见 [Burst 合同](../../scripting-event-burst-contract.md)，最新证据见 roadmap §18.54。

### 真实容器组仿射（0.11.1）

Transforms.ApplyAffine(GroupTarget, Affine2) 使用真实组参考空间；工程和显式节点绑定可写，源分支／行／元素／片段筛选不扩权。Node 的 includeDescendants 只控制后代是否单独可写，组变换自身始终沿运行父树影响所有后代。动态附着前奏按当前文本父关系传播，固定提示按固定父组传播；翻译保持独立。

SceneAffineProjection 将组和整行局部输出组合一次，PreparedRenderFrame 的 GroupTransforms 在视觉／图描述闭包中保留。最终行／族／标记／组参考与墨迹、Playback、未来端点、粒子出生及生产捕获共用该树；Skia 也校验隐藏组原生精度。组 alpha 隔离合成、组图 writer、主画布协调器和完整 UI 未完成。合同、验证和状态见[组仿射合同](../../scripting-group-affine-contract.md)／ADR-0090／roadmap §18.55。

真实控制器包的 VisualTransform/group 已在0.11.2接通 CreateEffect 能力门禁，仍按宿主真实范围校验，不能用清单绑定扩大组写权。真实包参数／分享／生产像素与编码证据见[组仿射合同 §5](../../scripting-group-affine-contract.md#5-真实包创建接缝修复0112)／roadmap §18.57。

## 0.12.0 统一效果包语义与真实资源目录

EffectPackageDefinition 取代原 EffectControllerPackage，三类导出共用唯一参数／输出模型；类型、引用、材质区域、完整纯色／渐变回退及图旁路均严格验证。CompiledEffectPackage 取代原 CompiledControllerPackage，全部资源的正式门禁先于 SDK 目录；没有 C# 的纯资源包 ControllerArtifact 为 null，不制造空 DLL；存在 C# 时即使无控制器也编译全部文件。源码失败时资源目录为空，禁止部分包消费。

实际控制器 ImageEffects.Bind 接通真实图导出；图自身的目标、绑定类别、完整参数约束与控制器声明均检查。CompiledPackageGraphExports 持有实际 CPU 图与图片，句柄按包修订和导出 ID 关联，CreateProgram 使用输入各自的完整捕获配方。GetMaterialFallback 只提供已验证的唯一基础外观；0.12.0 当批正常模式包材质仍报 UnsupportedMaterialBackend，主画布仍有协调器门禁；0.12.1 的字形目标最高层重组和真实下层见下一节。材质区域、完整组隔离与工程／模板事务仍未交付。

公开宿主类型更名及 SDK 变动按不兼容升 MINOR；不保留旧类型别名，不改 schema21／15／3或读取旧版本工程。真实像素、归档与显式短片证据见 [包合同 §10](../../scripting-package-graph-semantics-contract.md#10-c-实际实施接缝0120)／roadmap §18.58／ADR-0092。


## 0.12.1 主画布图与真实部件来源

正式 `SkiaRenderPipeline.Render` 借用帧外 `SceneImageEffectResources`，消费准备帧；逐输入读取当前真实彩色部件或固定几何覆盖，不调用作者或编译源码。无图对象继续原绘制；有元素图的行在固定 glyph 分区移除对应原字形，再读取处理后孩子，避免重复画字／注音。来源集合跨实例保存，融合置于被选择来源最高层；后续只选融合来源的部分时按用户确认报 ImageEffectPartialFusion。没有目标输入的图按用户确认替换全目标。

真实下层在对象绘制前取主画布快照，不包含对象自己或后来内容；同目标各实例借用同一下层。历史替换覆盖持续生效，保留的部件正常参与重组；共同入场／标记 alpha 通过最终画面与原下层混合一次，不能逐部件乘入。固定捕获框、实际密度、输入别名、图与合成产物共用帧账；空的真实部件可共享同帧空表面，来源仍各自保留。取消／错误按实际实例附加并释放中间对象，不提交错误画面。

透明背景依赖图使用声明的原输入且不产生新的融合限制或替换范围。RenderFrame 的中立诊断保存真实实例／图／输入，缓存命中和副本继续保留，导出报告按固定身份去重提示。真实 C# 混合包和玻璃链已通过该 Render 入口编码；测试适配只负责准备帧。

本批仅推进当前已有元素／行／标记图目标。容器组图和隔离、跨行父组、owned overlay、自身后置粒子排除、材质区域、非 ColorOnly 映射及工程／模板事务和工作区仍未交付；遇到实际运行后代或自身粒子下层依赖时明确报告未支持，不能采样原始孩子或自身粒子替代。详见[主画布合同](../../scripting-main-canvas-composition-contract.md)／roadmap §18.59／ADR-0093。

## 0.12.2 组共同透明度与嵌套隔离

公开 Style 仅给真实组增加 MultiplyOpacity；同目标乘数最终校验，静态组基线当前为1，不给组伪造文字材质。仅 Project／显式 Node 写组，源筛选无写权。固定准备帧保存有效 GroupOpacities 与实例代次，复制／重放保留，不改变布局、几何或播放头。

主 Render 将有有效 alpha 的组在第一项原绘制位置隔离为连续单位，子项按源序、父子逐层合成；各孩子原字形或真实行图先完成，组通过处理后画面和外部下层线性混合一次。下层不会重复叠加，入场／标记 alpha 保持原规则。宿主 opacity 程序独立于作者资源生命周期；组 alpha 不需要虚假图集合。交错顺序的具名诊断保存到帧、缓存和导出报告。

本批工作表面、下层、产物／混合和显式文字 SaveLayer 使用同一帧／会话账，预算失败归真实组或子图实例。仍不宣称完整 native 滤镜 scratch 资源出口。独立原始捕获不伪装最终隔离结果：TargetColor 捕获范围含有效组 alpha 时明确失败，Coverage 仍是原始几何。组图 writer、向父图提供按配方读取的真实孩子来源、owned overlay、自身粒子排除、区域材质和工作区继续。合同与验证见[组隔离合同](../../scripting-group-opacity-isolation-contract.md)／ADR-0094／roadmap §18.60。


## 0.12.3 可传递合成产物基础

`ImageCompositionPayload` 属于现有Skia后端，拥有独立sRGB前景与raw擦除表面，不暴露给作者、不保存到工程。`CaptureCompositionPayload` 提取同目标图链的当前颜色及全部历史替换影响；`ComposePayloads` 按实际绘制序合成独立孩子，两者不能互相替代。`CompositePayload` 应用真实外部下层及新一层alpha；颜色和擦除始终同时缩放一次，不能用背景截图或颜色alpha还原替换范围。

产物不依赖原图／输入／编译图存活；成对表面／累积产物纳入局部与共享预算、别名及跨帧去重，失败／取消释放。原主Render／组alpha规则未改，组图writer与父配方捕获未接通：部分孩子alpha、空组执行及父图继承擦除三项等待用户裁决。后续接入时仍遵守真实运行父树、来源不可拆、最高层和真实下层规则。见[合同](../../scripting-parent-composition-contract.md)／roadmap §18.61／ADR-0095。


## 0.13.0 区域材质覆盖接缝

Core材质定义保存明确CoverageMode，包清单必填coverageMode，当前包profile2；不把模式从图输入推测。Skia新增真实区域／基础外观成对捕获、固定图区域约束和CompiledPackageGraphExports.ExecuteMaterial。TargetColor固定使用包基础外观；背景依赖透明输出跳过图并保留提示。白色生产固定glyph先生成raw区域再着色，避免彩色AA导致不同覆盖；外描边也使用同一几何，基础渐变alpha不重复相乘。unmasked各覆盖一次，masked只验证／复制。产物与来源独立生命周期，整个捕获／区域执行纳入原账本。

这些接口由宿主使用，不开放原生对象给作者。正常PackageExport仍拒绝UnsupportedMaterialBackend，准备帧真实逐区域写入者、主画布自动执行／彩色部件与后续图的重组继续接入；不把独立原语记录为材质工作区或成片交付。合同见[区域材质](../../scripting-region-material-contract.md)／ADR-0096／roadmap §18.62。

## 0.13.1 真实区域材质主链

0.13.1 已接通元素族／真实前奏标记的 PackageExport 准备帧和正式主 Render。未唱／已唱／描边逐属性保留最后写入者，真实 StyleView 保留包句柄／参数，LyricStyle 只作明确几何绘制种子；没有归属或固定资源拒绝。已唱／未唱互补裁切，基础字及注音使用实际材质，后续整行图读取已处理彩色部件；元素局部 alpha、标记／行共同 alpha、组 alpha 各一次。透明背景依赖使用自身包基础外观并保留真实 MaterialHandle 提示，缓存和导出按实例去重。原始 TargetColor／彩色部件捕获遇到未重组材质报告 MaterialProcessedCaptureRequired，不返回白色种子。

固定SceneMaterialProgram借用CompiledPackageGraphExports，SceneImageEffectResources分别保存真实GraphHandle／MaterialHandle入口及显式图片几何。FrameEffectHost逐属性登记材质来源并校验完整包约束；PreparedRenderFrame.Materials与复制后的视觉帧保留闭包。主Render先逐区域生成材质，再按最高层规则供元素／整行图捕获，最终对象和父组合成保持既有alpha规则。

本批不新增工程字段、包编码、程序集或依赖；程序升 PATCH 0.13.1，schema21／15／3与package profile2保持。Materials.SetUniform、组图／父配方、映射、跨输出执行计划、工程／模板／分析事务和工作区仍待实施；原七项待答不使用默认值。完整 R2～R8／R3 出口保持未完成。 实际证据见roadmap §18.63／[区域材质合同](../../scripting-region-material-contract.md)。


### 0.13.2 直接渐变描边与透明度修正

现有 StrokeSpec.Gradient 经 StaticStyleProjection 接通元素族／标记 writer 和生产固定 glyph 绘制；Gradient 存在时可不提供 Color。普通填充及描边的渐变色标各含样式alpha一次，画笔不再重复乘历史纯色alpha或style.Opacity，源入场／对象／父组仍沿既有共同合成规则。外部轮廓及纯色路径保持，实际径向退化／原生越限明确失败。包材质的独立覆盖和协调器路径保持。

本批扩展既有能力，不新增数据编码、程序集、依赖或排版属性。schema21／15／3与包profile2不变；完整脚本系统仍按R2～R8继续，组图／父配方的待答语义不默认。证据见roadmap §18.64及[样式帧合同 §5](../../scripting-r2-style-frame-contract.md)。


### 0.13.3 材质有序写入与逐节点 uniform

FrameWriter.Materials.SetUniform 显式使用固定句柄、真实元素族／标记目标、MaterialSlot、图节点 ID、uniform 名字及 UniformValue。StyleUpdate.MaterialEdits 保存材质替换与覆盖的顺序；FrameEffectHost 逐实例验证元数据和当时句柄，通过 ApplyMaterials 保留最终 alpha 的原合并规则，再记录各部位最后写入者。PackageMaterial.Uniforms 是本帧不可变 GraphUniformValues，完整参数、包修订和程序不变。

Rendering.Abstractions 的 MaterialUniformValidation 共用于作者门禁与真实 CPU 执行，检查节点／名字、七类／数组及 float32 范围；ImageGraphFrameContext 携带中立覆盖，ImageGraphBindings 优先使用该节点的值，正式材质主画布和透明回退沿既有覆盖／alpha规则。源／场景不修改，无帧内源码编译。SDK 增量交付 PATCH，不新增 JSON 编码、层或依赖。组图／父配方、映射、整行Style、跨输出计划与工程／模板／分析持久事务、工作区仍未完成；证据见 roadmap §18.65／ADR-0099。

## 工程存储接续（0.14.0）

ProjectStore与内部LyricRevisionStore共同实现入口最后发布：歌词DTO编码为实际UTF-8字节、SHA-256内容地址；已有修订逐字节验证且不覆写，唯一同目录暂存刷盘并验证后原子发布，最后替换project.json。入口根和歌词各读一次，ProjectJsonReader按固定字节解析版本／schema，hash验证同一歌词字节；缺失或损坏引用返回Project=null。语义非法草稿保持现有映射／诊断，没有新增迁移。

内存Project不保存磁盘路径副本；lyricsFile只是入口DTO的存储引用，撤销／渲染快照仍持有不可变LyricsDocument。ProjectWorkspace仅在Save成功后更新路径／saved reference，AutoSaveStore每份备份独立写出歌词。唯一暂存命名也用于已有模板JSON原子写入；没有新程序集或依赖。完整效果实例／包／分析事务尚未接通；当前媒体收拢不宣称完整内容地址事务。见ADR-0100／roadmap §18.66。

### 工程效果源闭包与宿主投影（0.15.0）

Project.Effects持有完整EffectPackageContents、顺序ProjectEffectInstance及五项预算；不持有DLL、用户代码对象或粒子。入口effects引用effects/packages/<hash>，源参数为不可变规范JSON，九类解析继续复用包语义。EffectPackageStorage统一目录读取和不可变发布，ProjectStore先包、再歌词、最后入口；另存为／备份独立拥有包闭包。错误源引用／参数保留，坏字节或结构拒绝打开，参见[工程效果合同](../../scripting-project-effects-contract.md)。

SetProjectEffectsCommand共用现有ProjectHistory。Snapshot.FromProject自动固定源闭包／指纹；启用效果没有prepared frame不能普通Render或导出。CompiledProjectEffects按修订离线编译、按工程数组投影控制器、真实种子／订阅／绑定及参数，并把绘制与粒子限额传给TimelineFrameSourceFactory。表面限额通过GraphBudget由正式图资源组合根消费；音频修订投影及完整工作区尚未接入。本批既不改作者SDK，也不开放图／材质直接实例或尚未实现的输出。验证见roadmap §18.67。

### 完整媒体与已接受分析闭包（0.16.0）

Project.AudioMedia／Background.Media使用中立不可变MediaFileRevision，真实媒体路径保留给后端定位，originalName供界面显示。ProjectStore全部保存／另存为／备份通过ContentAddressFileStore流式校验和发布data/media/<hash>.media；无可选收拢或外部持久绝对路径。原生图片／FFmpeg按真实内容探测。media表的路径／长度／hash／链接必须一致；资源失败不替换根入口或saved reference。

ProjectAudioAnalysis区分完整成功修订、实际无流和失败原因；null由源存在性区分无音频／未分析。AudioAnalysisProfile集中既有算法身份；AudioAnalysisJson固定完整double值、实际PCM／源hash、样本数和严格hop网格，文件hash另覆盖全部特征字节。Media.ToSourceData只投影真实结果；SetAudioAnalysisCommand共用历史，切换源清除旧描述和分析、撤销恢复。加载只读已接受修订，不启动分析器。

CompiledProjectEffects.CompileAsync(Project,...)固定真实ProjectAudioProjection及完整输入指纹；TimelineFrameSourceFactory仍使用同一AudioFeatures／AudioFeatureTick／历史协议。控制器outputs.requires里的AudioFeatures决定RequiresAudio，初始化仅要求完整Available，音轨外查询保留OutsideRange／null；optional保留真实不可用状态。修改分析内容改变会话指纹，不改变C#产物身份。0.20.0已完成资源运行装配；分析UI及其余R7继续，完整R2～R8未交付，证据见roadmap §18.68／[存储合同](../../scripting-media-analysis-storage-contract.md)。


### 模板配方与独立效果实例（0.17.0）

应用级templates-v4/index.json保存schema4、全包目录与模板定义；旧templates.json保留但不读取。每模板只携带相对目标配方、规范参数及完整固定资源，没有歌词ID或实例状态；静态模板effectRecipes为[]。包先发布、index最后替换，坏库不能覆盖，目录及.mktpl分享具有同闭包与共享展开预算。

StyleTemplateApplicator一次产出静态写入和效果源实例。行配方创建单Lines绑定，元素族配方在行内逐元素创建Tokens绑定；模板来源owner和槽位键允许更新旧实例、清理本次来源，并保留直接／其他模板实例。同一来源槽的ID、seed、enabled保持；跨目标新槽按首次套用创建并启用，数组顺序为真实执行顺序，相同应用返回原工程。显式移除模板效果独立于静态继承；复制实例脱离来源。0.33.0模板工坊从固定包闭包选择合法行／元素导出，严格解析参数，正式固定试样通过同一Applicator生成输入指纹；歌词行编辑器显示当前行／元素实际模板实例范围，组导出继续拒绝。0.34.0起行／元素编辑器可从当前目标打开同一效果实例工作区，模板配方与工程效果仍是两条明确的保存路径。

ProjectStore保存schema25的来源与图元层，工程自有包目录与模板库断开。CompiledProjectEffects转入原真实控制器／固定帧路径；宿主层覆盖进入圆点／连续／Burst，本帧粒子也公开实际Layer。工坊保存完整材质／配方闭包，0.33.0起可编辑合法配方并在正式固定试样中显示效果实例；0.34.0起歌词行／元素与场景对象可以把稳定绑定传给统一效果工作区，0.35.0起这些入口保留初始播放头，无效果时显示布局参考，含效果或源材质时使用正式动态时间场景；显式验证复用同一请求，静态试样不反馈导出门禁，保存不会把Preparing冒充成功，未应用／过期场景入口禁用并提示；0.36.0目标效果清单与实例／节点定位已接入，共享实际帧绑定查询；模板分享按钮、组图／父捕获和完整脚本工作区组合验收继续。0.18.0～0.20.0已接入直接材质源字段和运行组合根，0.37.0已接通纯图实例正式运行；0.38.0配方编辑／重排和0.39.0相对目标已接通，模板分享与完整组合验收继续。见[模板合同](../../scripting-template-recipes-contract.md)／[R7接缝合同](../legacy-ui/scripting-r7-template-recipe-contract.md)／[上下文入口合同](../legacy-ui/scripting-r7-binding-entry-contract.md)／[场景预览上下文合同](../legacy-ui/scripting-r7-scene-preview-context-contract.md)／[目标效果合同](../../scripting-r7-target-effects-contract.md)／ADR-0103、0130～0133／roadmap§18.69、18.97～18.100。


### 源材质唯一真值与闭包（0.18.0，M1）

FillSpec只持有MaterialSpec（solid／gradient／packageMaterial），StrokeSpec为独立几何＋MaterialSpec。覆盖完整替换材质，宽度／连接／位置仍可逐项继承；颜色／渐变源码属性无第二份源值。SourceMaterialCatalog统一收集基线、分支、行、元素、倒计时和标记引用，共用effects.packages；模板静态材质与配方共用完整闭包。源引用阻止注销，修订资源仍先发布入口最后替换。

工程26／歌词16／模板5；新库templates-v5，不转换或覆写旧库。0.19.0 M2已接通CompiledProjectEffects的完整源材质包并集、固定参数和真实继承来源；BaselineStyles保留包句柄，纯资源工程无需C#或假实例。PreparedMaterialOwner.Source／Script区分原始源和真实后写者，元素族／注音／描边／前奏共用区域材质Render，透明提示和失败保留真实路径。纯帧／时间线／独立端点接入同一PreparedSourceMaterials，改变源或参数使旧会话失效；帧内不编译或解析包。缺少正式准备帧／native资源明确失败，预算及中途错误保护旧产物。0.21.0 U1已接入三材质槽位、九类包参数、目录／`.mkfx`导入和正式异步试样；0.22.0 U2基础已接入效果实例工作区、实例顺序／绑定／订阅／参数编辑；0.23.0增加包导入／分享，0.24.0将保存与正式会话验证分开，失败源码可以一次命令进入撤销历史。U0运行组合根已完成；直接图／跨输出计划、父捕获和完整R7工作区继续后续。跨输出／owned overlay／映射等门禁保持，完整R2～R8未交付。格式／边界见[源材质合同](../../scripting-source-material-contract.md)／[运行装配合同](../../scripting-runtime-composition-contract.md)／ADR-0104～0110，权威证据见roadmap§18.70～18.76。


### 工作区源码草稿、独立编译与显式接受（0.24.0，U2）

`EffectPackageSourceEditorViewModel` 按包修订和源码路径保存草稿及文件选择；文件／包切换与列表刷新保持未发布文本。源码区保存合并当前包全部脏 C# 文件，更新实际 UTF-8 摘要并生成完整新修订，旧修订和其他包草稿保留。工作区保存先发布所有包源码草稿，再用 `SetProjectEffectsCommand` 一次提交工程效果；C# 编译失败不阻止保存、重开或备份。

`IEffectPackageCompiler` 直接编译所选固定包，不要求它被启用实例引用；后台执行包解析、正式图片／shader／图／材质门禁、Roslyn 编译和入口反射，不运行作者构造。诊断保留代码、路径、行列，选择有源码位置的诊断会切换文件并定位光标。编译状态与具体修订关联；源码发布或编译成功都不会自动更新实例。显式接受只改所选同包实例的修订，保留入口、参数、身份、seed、绑定、订阅、启用状态、来源和顺序。新建实例初始禁用。

“保存工程改动”与“验证工程效果”分开。后者通过正式会话检查当前启用实例和源材质；失败不回退旧 DLL，也不允许无效果成片。编译期间冻结编辑和选择，取消与关闭等待任务退出；离开时全部源码和工程效果草稿共同参与保存／放弃／取消。

程序0.24.0、schema26／16／5及包profile2保持，无新增作者SDK、程序集或依赖。U0运行组合根和U1材质工坊已接通；直接图／材质实例、跨输出执行计划、组／父捕获、实际映射、新建源码包／文件、多引用升级、模板配方完整入口、实时工作区画布及窄窗覆盖面板仍后续。合同见[源码编辑合同](../legacy-ui/scripting-workspace-source-editing-contract.md)／ADR-0110；证据与完成状态见roadmap §18.76。


## 固定脚本参数的UI投影（0.43.10）

ScriptConfigurationWorkspaceViewModel持有ScriptParameterPanelViewModel；它读取固定包／导出的已有元数据，Field与Input模型仅描述控件和未完成输入。ScriptParameterCodec负责typed JSON、整微秒／毫秒及滑动区间映射，字段提交复用Core.ResolveParameters；有效真值仍是原ParametersJson／EffectParameterOverrides。原始JSON入口与图形入口共用覆盖，不增加领域状态。

作者DraftState增加仅内存ParameterInputs，以保留非法字段的撤销与离开检查；重建／切换导出拒绝旧字段回调。ScriptParameterFieldView和ScriptParameterInputView分别呈现类型与共享精确输入，使用UiControlMetrics，负责焦点、键盘、指针及实际Thumb捕获的手势边界。运行验证继续只接受有效固定ScriptConfiguration，不接收UI草稿。配置库schema1、工程28／歌词16／模板6和包profile2保持。
