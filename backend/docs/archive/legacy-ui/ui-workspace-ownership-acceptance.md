# 三工作区连续制作验收（0.43.10施工版）

2026-10-08。实施来源：[三工作区方案](ui-workspace-ownership-plan.md)、ADR-0145～0162。当前工程28／歌词16／模板6／脚本配置1／包profile2，精确程序版本门禁保持。自动证据与用户人工验收分别记录；目标尚未完成。

## 已通过的连续链

最新0.43.9专用原生窗口从首页新建320×180工程，先选择自有音频与两行TXT主歌词，完整准备后进入未保存工程；再填写真实行与逐字时间及注音，导入背景。进入模板制作，新建Composite，在独立脚本窗口导入实际GlassInk／WordEmphasis并保存配置；工坊复制材质和效果，修改字号、保留非法字段与最后合法值、验证一次手势一次撤销，自己的试播不改变当前工程。

保存完整预设后，画面区刷新候选；取消应用不改工程，整首应用和局部行应用／移除各一次撤销。局部行静态覆盖不改变另一行，移除恢复全局继承。卡拉OK双行布局只改编排，保持源歌词及预设身份。保存后搬移工程，删除原音频、背景和外部模板／配置库，重新打开仍保留完整固定资源与实例身份。

普通MP4与透明ProRes4444各60帧编码输入与另建正式预览会话逐像素一致。真实2秒产物探测、带音轨、透明Alpha及背景依赖回退报告通过。这里比较的是编码输入；完整长片另验证解码产物的全部帧与音轨，不能混为同一证据。

两种格式分别执行真实编码器接受十五帧后的IOException和原生取消按钮。既有成片字节保持完整，编码器及帧全部释放，无残留encoding临时文件，不报告成功。IOException属于测试注入的编码边界，真实作者脚本Error由既有R8领域测试验证。

未保存工程通过正式备份动作写完整闭包，搬移备份后从首页取消恢复保留标记，再明确恢复。恢复保留两个包、预设实例ID与种子、原工程入口和备份目录，保持未保存状态，明确保存后才发布。模板切换任务保留草稿；回首页取消／放弃及独立脚本窗口的源码关闭保护分别通过。

## 自动证据

| 验证 | 结果与位置 |
|---|---|
| 0.43.5隔离solution构建 | 零警告／零错误；`artifacts/ui-ownership-final-0.43.5`，`/tmp/makokara-ownership-final-build.log` |
| 0.43.5 Headless | 274项通过；`artifacts/ui-headless-layout.nvhNVT`，`/tmp/makokara-ownership-continuous-headless-final.log` |
| 0.43.5原生连续链／宽窄控件 | 265项／29图通过；`artifacts/ui-suki-compatibility.dktDUy`，`/tmp/makokara-ownership-continuous-native-final.log` |
| 独立脚本关闭与迟到保护 | 24项／2图通过；`artifacts/ui-suki-compatibility.cTNr4z`，roadmap §18.128 |
| 模板分享／删除外部包／原生导入 | 62项／7图通过；`artifacts/ui-suki-compatibility.19PjrV`，`/tmp/makokara-ownership-continuous-transfer-native.log` |
| 0.43.5最新提交隔离全量 | 3317项全部通过、零失败／跳过；含57秒双输出全部帧和音轨；`/tmp/makokara-ownership-final-full-regression.log`。此前非长片运行因新范围边界修复主动中止，不计为全量通过 |
| 0.43.5较早构建完整solution | 3317项全部通过，零失败／跳过，包含57秒成片全部帧及音轨；`/tmp/makokara-ownership-continuous-full-regression.log`。此构建早于候选名称／范围同步修复，与当前UI快照分开 |
| 0.43.6隔离solution构建 | 零警告／错误；`artifacts/ui-ownership-applications-0.43.6`，`/tmp/makokara-ownership-applications-build.log` |
| 0.43.6应用事务与继承定向 | 新4项＋既有2项共6项通过；`/tmp/makokara-ownership-applications-targeted.log` |
| 0.43.6 Headless | 299项通过；`artifacts/ui-headless-layout.ffS8BM`，`/tmp/makokara-ownership-applications-headless-final.log` |
| 0.43.6多选／来源原生 | 36项／6图通过；`artifacts/ui-suki-compatibility.DPxo57`，`/tmp/makokara-ownership-applications-native-final.log` |
| 0.43.6非长片solution | 3320项全部通过、零失败／跳过；`/tmp/makokara-ownership-applications-regression-final.log`，不记为最终全量 |
| 0.43.4非长片solution | 3316项通过，零失败／跳过；`/tmp/makokara-ownership-routes-regression.log` |

所有原生探针只操作自己的PID窗口，独立配置／状态／缓存，有限动作后自行退出。对话框和当前正式画布等待稳定后截图。最新完整探针先设置并核对视口、等待工程操作完成，避免把合成器初始窄铺排误记为产品布局；早一轮在此处失败，不作为完整通过证据。实际中文字符串赋值、自动按钮点击及1.25缩放不能证明物理IME、真实视听、多屏DPI或Windows。

历史0.43.2完整solution有3314项中的46项渲染编译失败，App554项含普通／透明57秒全部帧及音轨通过；独立Rendering944项复查通过，根因未定位。历史失败不能因新一次全绿记为已修复。

## 稳定画面

[歌词与时间](../../design/ui-workspace-ownership/author-lyrics-timed.png)、[组合预设制作](../../design/ui-workspace-ownership/author-composite-preset.png)、[双行布局](../../design/ui-workspace-ownership/author-applied-layout.png)、[透明报告](../../design/ui-workspace-ownership/author-transparent-report.png)、[取消保护](../../design/ui-workspace-ownership/author-mp4-cancel.png)、[备份恢复](../../design/ui-workspace-ownership/author-recovered.png)。全部来自同一作者连续链；脚本配方动作换行后完整可见。

## 人工验收路线

1. 首页新建时选择自己的音频和主歌词，核对必填、失败／取消及未保存状态；追加翻译时明确新建或已有分支，试取消非空分支替换并检查一次撤销。检查IME、长行、逐字时间、注音／翻译与循环。歌词任务只显示中性文本和时间，间奏显示 `···`。
2. 从首页或工程进入模板制作，确认没有工程歌词列表、波形和播放条。制作基础／材质／效果组合，试播固定样本；独立脚本窗口保存失败源码、重开候选、明确编译及保存配置。
3. 画面区从默认可见的“歌词行”目录选择分支及歌词行，多选后进入预设面板；窄窗选行应保持列表，明确点击“选择预设”才切换面板。再试整首／字及其他应用对象，检查标题、影响确认、局部覆盖、移除继承和撤销。工坊保存新预设后返回画面区可直接选择。
4. 试用四种布局与连续选段，检查未选歌词位置／轮换不变；复杂布局从内部高级编排进入。切任务保留草稿，回首页／换工程／退出分别试应用或保存、放弃、取消。
5. 在760×640和宽窗切换具名面板，检查中央编辑／画布、完整配方动作、长诊断、焦点和深浅主题。使用真实系统文件选择分享／导入`.mktpl`和`.mkfx`。
6. 导出普通和透明视频并试听，取消后检查旧片；保存搬移重开与首页备份恢复。Linux结果先记录；Windows、物理多屏DPI单列。

用户人工验收尚未提交；不标记最终目标完成。

历史0.43.6记录：生产提交`2dcade0`当时已推送同名远程分支。旧0.43.5人工PID4092862已退出，日志仅版本且未记退出原因。确认没有当前产品窗口后，已从0.43.6隔离solution产物启动人工专用窗口：PID4166585／niri1429，标题`MakoKara — 首页 0.43.6`，日志`/tmp/makokara-ownership-0.43.6-startup.log`仅版本记录，未见托管异常。配置／状态／缓存使用`artifacts/ui-ownership-current-0.43.6`，与用户库独立。此窗口交给用户试用，不执行自动交互。既有人工验收结果问题仍待回复；新功能和最终验证尚未全部完成，启动不等同人工通过。


0.43.6补齐画面区多选批量、具名输入和实际上级来源，已验证映射撤销重做、单实例身份与种子、取消及迟到输入变化。截图：[窄窗暗色来源菜单](../../design/ui-workspace-ownership/preset-input-dark-760.png)、[宽窗浅色来源菜单](../../design/ui-workspace-ownership/preset-input-light-1440.png)。最终原生视口实际核对，不把早一轮请求尺寸当成实际尺寸。

0.43.6时期的下一项为独立脚本窗口正式运行验证及弹层打开期间调整窗口；最新接续见下文0.43.8～0.43.9。迟到音频保护已在0.43.7接通。脚本窗口编译与工坊正式预览不替代独立运行动作；演示加载迟到清空历史由roadmap §18.131的0.43.7修复；重复打开弹层的栈溢出仍待核对。该批最终全量与用户人工验收尚未完成，当时人工专用窗口为0.43.6，未自动操作。

0.43.7音频准备与迟到保护：solution隔离构建零警告／错误，`artifacts/ui-ownership-audio-0.43.7`／`/tmp/makokara-ownership-audio-build.log`；9项新增生命周期回归及原播放／循环／合成共43项通过，`/tmp/makokara-ownership-audio-targeted.log`；Headless299项通过，`artifacts/ui-headless-layout.cb8gn7`／`/tmp/makokara-ownership-audio-headless.log`。本批无新增可视控件，不额外启动原生窗口或操作0.43.6人工窗口。最新0.43.5隔离完整回归已结束exit0，3317项全部通过；不是0.43.7当前构建最终全量。脚本独立运行动作、弹层缩放和最终人工验收仍未完成。


## 0.43.8～0.43.9 本批接续

独立脚本运行验证已经接通，原§3缺口退出；正式路径由新增12项及既有配置7项覆盖，真实WordEmphasis／背景材质分别验证普通／透明720帧，失败源码、中途Error、取消及越界材质不报告通过。新建4项与分支导入6项合计29项定向通过，`/tmp/makokara-ownership-feedback-targeted.log`。仅源码编译或工坊预览不替代该独立出口。

| 当前证据 | 结果与位置 |
|---|---|
| 0.43.9 solution构建 | 零警告／错误；`artifacts/ui-ownership-final-0.43.9`，`/tmp/makokara-ownership-final-0.43.9-build.log` |
| 0.43.9 Headless | 321项通过；`artifacts/ui-headless-layout.SbeivY`，`/tmp/makokara-ownership-feedback-headless-0.43.9.log`；299项既有布局与22项歌词应用 |
| 0.43.9 原生反馈流程 | 47项／10图通过；实际宽窄、分支同步、多选／一次撤销、必填及取消、明确导入分支、长表单动作常驻、脚本冻结及关闭等待；日志`/tmp/makokara-ownership-feedback-native-0.43.9.log` |
| 0.43.9 更新后作者链 | 266项／29图通过；`artifacts/ui-suki-compatibility.0hB43i`，`/tmp/makokara-ownership-feedback-author-0.43.9.log`；新建直接导入歌曲和TXT，后续普通／透明编码输入、失败／取消／恢复保持 |
| 保持打开的来源弹层与窗口调整 | 36项／6图通过；`artifacts/ui-suki-compatibility.ln3YLF`，`/tmp/makokara-ownership-popup-resize.log`；打开一次后调整并核对三种实际视口，未复现旧递归，未宣称定位其根因 |
| 当前最终solution全量 | 3358项全部通过、零失败／跳过，exit0；App592项含57秒双输出全部1710帧和音轨，48分12秒；`/tmp/makokara-ownership-final-0.43.9-tests.log`。Application105／Core1207／Export102／Media89／Rendering944／Scripting235／Timeline84，当前构建零警告／错误 |

Headless不执行Suki 7.0.1禁用／模态GPU模糊绘图，软件和假绘图后端均触发第三方绘图错误；相关界面及等待边界使用原生专用窗口，正式释放和错误语义用服务回归分别证明，没有隐藏产品控件来获得通过。注入的运行结果只用于验证窗口生命周期；正式720帧由真实会话测试提供。原生截图等待实际视口和内容稳定，初次过早截图不计为最终画面。

新增操作画面：[窄窗歌词目录](../../design/ui-workspace-ownership/feedback-lyrics-Dark-760.png)、[宽窗歌词目录](../../design/ui-workspace-ownership/feedback-lyrics-Light-1440.png)、[必填新建](../../design/ui-workspace-ownership/feedback-new-project.png)、[明确翻译分支](../../design/ui-workspace-ownership/feedback-translation-branch.png)、[长导入表单](../../design/ui-workspace-ownership/feedback-long-import.png)、[独立运行报告](../../design/ui-workspace-ownership/feedback-runtime-report.png)。旧0.43.6／0.43.7人工进程均已退出，日志没有退出原因，不记为产品失败；当前原生探针均已正常退出。用户人工验收、真实IME、多屏DPI及Windows结果仍待获得。

## 方案要求对照

本表按方案§1～5及§19核对生产入口和实际断言范围。当前完整solution3358项已全部通过；表中的定向、原生、全量证据分别证明其范围，自动结果不能替代用户人工验收。测试文件提供可复查的具体断言，批次执行结果以roadmap和上表日志为准。

| 要求 | 实现与证据范围 |
|---|---|
| 三个同级任务、工坊独立于工程 | MainWindow.Navigation及TemplateWorkshopWindow；LayoutProbe检查三任务排他、相同尺寸、首页独立进入及工坊隐藏工程播放／时间轴。原生作者链实际切换任务。 |
| 歌词文本、行／逐字时间、拆合、注音翻译及来源关联 | LyricLineEditorWindow和歌词来源面板；LyricsImportAndSplitTests、NeutralLyricsPreviewTests及ProductionLyricsProbe检查源编辑、关联真实区间和缺失时间。源码／样式入口缺席有控件断言。 |
| 中性预览、真实空档、失败脚本不影响歌词 | NeutralLyricsPreviewTests检查统一采样、关联与`···`；ProductionLyricsProbe保存真实失败源码，确认中性预览和编辑仍可用。 |
| 播放／循环、编辑焦点、无按键实时落点、一次LRC损失确认 | LayoutProbe及ProductionLyricsProbe检查字段焦点保持、非法输入、括号不落点、任务快捷键及一次导出确认；物理键盘／输入法仍需人工。 |
| 工坊基础外观、三种材质槽及效果配方 | TemplateWorkshopViewModel、TemplateMaterialEditorViewModel、TemplateEffectRecipeEditorViewModel；TemplateAuthorFlowTests及原生作者链检查实际GlassInk＋WordEmphasis闭包、合法／非法输入与预览。 |
| 自有12秒试样及试播／暂停／重播／循环 | TemplateSampleMedia和TemplateSamplePlayback；TemplateAuthorFlowTests检查600个真实音频分析点、重复词／注音／空档／标记、自有播放头及离开停止。 |
| 工坊相对对象、全局单实例、独立历史与快照复制 | CompositePresetDefinitionTests、TemplateAuthorFlowTests检查不携带屏幕布局、Application配方单实例、固定种子、一次手势撤销、复制后新身份及来源删除后完整资源。 |
| 每对象一个组合预设、范围筛选及具名输入 | PresetApplicationsEditor与画面对象目录；PresetApplicationsTests、PresetApplicationSelectionTests及ApplicationFlow检查多选原子事务、失效整次拒绝、显式输入映射和实际目标同步。 |
| 静态／效果继承、更新／移除／启停、固定身份 | PresetApplicationsTests检查工程→分支→节点→片段→行→文字、祖先效果、移除恢复、手动更新和无变化；PresetApplicationTransactionsTests验证一条历史及完整身份／资源恢复。 |
| 正式投影、几何和普通／透明输出一致 | PresetApplicationRenderTests比较真实像素、LineGeometries、来源材质、编译／准备会话及过期指纹拒绝；TemplateAuthorSessionTests保存重开后双会话逐帧一致。 |
| 四种布局及精确选区、引用和前奏保护 | BasicLayoutTests核对四组坐标／锚点、原ID保留规则、未选歌词像素／槽位／时钟、直接片段引用／成员实例／种子、前奏及范围配方冲突整次拒绝；ProductionLayoutProbe验证真实入口。 |
| 高级编排内部模式、基础回退、正式效果保留 | BasicCompositionTests检查基础预览移除运行效果、材质声明回退和源数据不变；LayoutProbe检查同级任务内切换及模式标记，正式作者输出继续使用完整预设。 |
| 独立脚本库、材质／效果分类、固定闭包 | ScriptConfigurationLibraryTests及ScriptConfigurationWorkspaceTests检查目录搬移、参数精度、种子、真实导出能力、失败源码保存和模板复制；ProductionScriptProbe检查独立窗口复用及不写工程。 |
| 源码保存、编译、采用修订、配置保存、正式运行分开 | ScriptConfigurationWorkspaceTests与ScriptRuntimeValidationTests；失败候选不回退旧DLL，WordEmphasis和背景材质各720帧，中途Error／越界材质失败。FeedbackFlow检查未保存源码门禁、运行冻结及关闭等待。 |
| 草稿切换／离开、迟到异步、取消释放 | 原生作者链／ProductionScriptProbe检查取消保留、源码保存后不升级配置、迟到导入；音频请求及新建／运行验证服务测试检查请求身份、忽略取消的等待、候选释放和旧工程／播放保护。 |
| 统一尺寸、具名面板、宽窄深浅及长字段 | UiControlMetrics＋EditorStyles；LayoutProbe321项及原生反馈47项检查实际视口、普通控件规格、中央空间、面板互斥和640px滚动表单。弹层保持打开调整视口有36项有限序列证据，旧递归根因仍未定位。 |
| 明确歌词行应用入口、必填新建、明确分支导入 | AppearanceLyricsView、NewProjectPreparation及导入确认；FeedbackFlow、ProjectCreationTests、ExplicitLyricsImportTests、PlainTextImporterTests检查实际多选／撤销、准备失败不发布、初始未保存、分支身份保持及TXT不制造时间。 |
| 脱离外部库、保存／另存为／备份／恢复、失败和旧输出保护 | PresetApplicationTransactionsTests、原生连续作者链及R8完整成片测试；短链比较每个编码输入，长片比较两种真实解码产物全部1710帧和音轨，两者分别记录。当前最终全量3358项已通过。 |
| 格式28／16／6、配置1、包profile2及精确门禁 | 当前DTO／库常量、PresetApplicationsTests、CompositePresetDefinitionTests和ScriptConfigurationLibraryTests；旧工程版本先拒绝，旧库字节不转换或覆写，破损闭包不返回部分工程。固定Suki／Avalonia版本及SDK边界保持。 |
| 文档、分步提交推送及人工出口 | 原型保留既有三工作区设计；方案、ADR、roadmap、架构、指南、README、CHANGELOG和AGENTS已同步当前实现。0.43.8运行验证与0.43.9歌词／导入分别有3342／3358项完整门禁，按同名工作分支分步提交推送；当前手测窗口已启动，人工反馈尚未获得，不能标记整个目标完成。 |


0.43.9手测窗口已于2026-10-08启动：PID235563／niri165，当前构建`artifacts/ui-ownership-final-0.43.9`；日志`/tmp/makokara-ownership-0.43.9-startup.log`仅版本，标题及进程存活已核对。配置`artifacts/ui-ownership-current-0.43.9`复制旧0.43.7手测config以保留模板／脚本库，原副本保留；state／cache独立。当前完整回归3358项已通过，已请求用户检查新建、明确分支导入、歌词多选应用和独立运行验证，答复待记录。自动程序不操作该窗口，启动不是验收通过。


## 0.43.10 脚本参数面板追加验收

新增11项参数回归及原配置／正式运行验证共30项通过（`/tmp/makokara-script-parameters-tests.log`）；当前Headless353项通过（`artifacts/ui-headless-layout.FBu7uU`，`/tmp/makokara-script-parameters-headless.log`）。原生独立窗口38项／7图通过（`artifacts/ui-suki-compatibility.3IvPku`，`/tmp/makokara-script-parameters-native.log`），覆盖实际字段／下拉选择、精确文本、非法输入、一次撤销、恢复默认、保存、深浅760／1400×640及125%缩放。按钮MinHeight使用统一36px；实际布局最多相差一个物理像素。弹层打开未写未编辑参数，Headless通用弹层模板继续检查不透明背景；Headless SukiWindow本身没有原生popup host，实际开启只由原生探针验证，不能把后端限制记作产品修复。

[窄窗参数](../../design/ui-workspace-ownership/script-parameters-dark-760.png)、[宽窗参数](../../design/ui-workspace-ownership/script-parameters-light-1400.png)、[曲线输入](../../design/ui-workspace-ownership/script-parameters-curve.png)来自专用探针。真实VectorTrail／SpriteTrail包验证枚举及资源；滑块／布尔／文字／曲线由合法测试包声明补充，未把测试参数写入公开包。原生截图不代替物理输入、弹层位置及多屏缩放的人工确认。

手测路线：导入粒子包后选择绘制方式，修改发射率／寿命；有限区间尝试滑块及精确文本，宽范围只用文本。检查色值打开不变，RGBA分量及曲线Y支持精确输入，非法值保留且无法保存／验证，撤销恢复。恢复一项默认后保存重开；模板复制该固定配置并应用歌词，确认已有工程不因修改库自动改变。用户随后明确要求启动当前窗口：0.43.10／PID496626／niri230，独立profile为`artifacts/ui-parameters-current-0.43.10`，只复制旧模板／脚本库及主题，state／cache独立。旧235563此时已退出，原日志未记原因，未对其执行关闭。新日志有音频设备探测警告，未见托管异常；不自动操作此人工窗口。当前全量和变异证据见roadmap §18.134；本批参数面板的用户确认见下文。


2026-10-08用户在0.43.10窗口手测后反馈“测试完成，无问题”，本批参数面板人工验收通过。当前完整solution3369项随后全部通过；其他连续制作路线及物理输入／多屏／Windows未从这次反馈推定通过。


当前0.43.10完整solution3369项全部通过，零失败／跳过，App603项含57秒普通／透明全部1710帧、音轨、失败取消及恢复；构建零警告／错误，runner退出0。产物`artifacts/ui-parameters-final-0.43.10`，日志`/tmp/makokara-script-parameters-final-build.log`、`/tmp/makokara-script-parameters-final-tests.log`，冻结源码hash已核对。四项实际运行变异均触发失败，恢复后11项重跑通过；日志`/tmp/makokara-script-parameters-mutations.log`。本批用户参数面板手测已接受，其他路线的证据仍按各自版本和范围记录。
