# 作者模块、旧入口与编译绑定合同（F8c分批）

2026-10-09。承接[修复计划](../plans/code-review-remediation-plan.md) §11／ADR-0179～0180。F8c第一批0.49.0完成旧入口清理及共享作者能力，第二批0.49.1完成当前稳定视图与C#数据绑定，移除全局关闭开关。F8c定向范围收口，最终连续／人工验收仍待完成。

## 清理前引用清单

按生产C#、AXAML、资源、测试和验证工具分别检索，记录当前入口、共享能力和仅历史消费者；原行号清单保存在`artifacts/code-review-repair/f8c1/references-before.json`。

| 原入口／模型 | 清理前实际引用 | 本批处理 |
| --- | --- | --- |
| EffectWorkspaceWindow及两个C#分部 | 产品无构造／导航入口，只有通用Headless工具仍创建旧窗口 | 删除窗口及专属视图接线，工具退出旧窗口检查 |
| EffectWorkspaceViewModel、实例／参数模型、源码模型及预览／诊断分部 | 只供旧窗口和历史后端测试使用，独立脚本配置没有引用 | 从产品删除；历史内核回归夹具移至测试程序集的LegacyAuthoring并明确命名，不作为产品能力 |
| ProjectBaselineStyleWindow／ViewModel | 窗口无生产入口，模型仅旧基线测试使用 | 删除窗口；旧领域回归保留测试夹具 |
| AppearanceEditContext与直接静态字段编辑命令 | 当前画面区使用预设应用；只剩旧字段测试与StyleDesignField的具体类型判断 | 从产品删除；测试夹具保留旧领域输入，当前作者字段改用明确接口能力 |
| EffectWorkspaceRequested／RequestEffectWorkspaceAsync、旧基线命令与定位助手 | 无生产订阅者／调用者，仅旧测试事件转发 | 产品删除，历史渲染测试直接提供固定目标／时间／实例，不再虚构当前UI事件入口 |
| EffectTargetList | 只有声明和旧资源，无当前调用 | 删除旧动态按钮列表 |
| StyleDesignField、IStyleDraftContext、输入草稿与三个编解码器 | 当前TemplateWorkshop与真实前奏样式控件复用 | 迁入App.Authoring，保留输入、历史、解析与控件行为 |
| EffectTargetCatalog | 当前画面对象说明、固定预设继承及后端关联回归复用 | 迁入App.Authoring，真实帧查询与名称格式保持 |
| 独立脚本配置源码／图形参数／运行验证模型 | 当前ScriptConfigurationWorkspace及工坊使用 | 保留原模型及全部入口，不与旧实例模型混为一谈 |

合成区原“编辑工程基线样式…”按钮实际早已进入画面区。本批改为明确的“画面与样式”入口名称与标签，行为仍进入同一任务；没有恢复直接样式编辑。正式ProjectEffects事务方法及底层闭包／渲染能力保持，删除的是无消费者作者入口。

## 共用作者能力

`App.Authoring`持有共享样式字段、最后合法值以外的原始输入记录、渐变／描边／阴影编解码器及绑定显示。它是App中的明确作者模块，不是新的后端程序集，不创建重复工程或作者状态；F8a／F8b正式运行宿主边界保持。

`IStyleDraftContext`显式声明`LivePreview`和`SupportsProperty`，默认分别为false及true。模板工坊声明实时预览，并继续拒绝Composite中的画布位置／对齐字段；真实场景样式保留显式提交。`StyleDesignField`不再判断具体TemplateWorkshop或已经退出的AppearanceEditContext类型。旧原始草稿、错误、外部复合输入、写入选择、手势与历史语义保持。

历史LegacyAuthoring仅编译进测试程序集，保存既有包、实例、来源、像素、闭包和撤销回归所需输入适配。其函数不供产品引用，也不是当前UI端到端验收；测试仍通过当前正式宿主与真实Skia／FFmpeg验证后端。当前UI的配置制作、预设应用及作者窗口由独立定向／Headless流程验证。

## 首批编译绑定

StylePropertyRowView、StyleGradientEditorView、StrokeSpecEditorView、ShadowSpecEditorView显式标注`author:StyleDesignField`并开启`x:CompileBindings=True`。属性行AXAML中的模型绑定编译检查；后三个复合编辑器主要通过C#接线，类型标注说明其上下文，并不把没有Binding的控件虚报为新增编译绑定数量。C#动态控件、输入事件和原始草稿同步仍由各控件持有。

0.49.0当批只迁移共享样式视图，保留全局关闭开关。0.49.1的剩余视图与最终默认出口见下节；不得用编译成功代替交互验证。

本批App133及真实后端19、Headless561个断言、三项成功编译后变异及一项编译绑定门禁通过；构建零警告／错误。旧诊断断言先用迁移前原执行体复现，再按完整发生和实际翻页Error来源修正。回归、变异、构建和有限Headless证据见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.152。MINOR0.49.0对应退出旧公开UI类型／命令及共享作者类型的位置变化；作者SDK、中立接口、工程／歌词／模板28／16／6、配置1、profile2与固定依赖保持，无格式迁移。全量测试继续按用户要求暂停；原生工具仅编译，不操作用户窗口。上述为第一批证据；第二批接续见下文，最终连续／人工边界未完成，F6b剩余UI延迟不由职责清理推定解决。

## 剩余稳定视图与最终默认（F8c2，0.49.1）

清单覆盖33个生产AXAML，其中29个视图。现有317处数据Binding及17个DataTemplate均有准确的模型／项目类型：编辑器、播放栏、歌词行／逐字模板、首页最近工程、预设新建／工坊、独立脚本配置、参数子控件及运行报告。无模型绑定的资源／代码接线控件不虚报为新增数据绑定；主题的DynamicResource、TemplateBinding与数据模型绑定分别理解。

DisplayMemberBinding显式声明选项类型；异构泛型枚举通过内部IScriptConfigurationChoice只读Label合同展示，Value仍保留各自泛型类型。参数子DataContext及工坊参数的父控件读取显式转换为实际父模型。首页和新建对话框只标注真实项目模板，不给无模型的根虚构DataType。11个命令控件分别声明TaskCommandAction与初始空DataContext，由已有C#命令协调器设置实际动作，避免初始化短暂继承EditorViewModel。

12处C#字符串Binding构造改为固定版本的CompiledBinding.Create表达式：预设名称搜索、动态来源选择、独立试样TimeUs／PlayLabel／Loop和场景状态。显式source及原TwoWay模式保持，事件／资源观察器保持；当前未发现需要ReflectionBinding或x:CompileBindings=False的生产例外。两项策略回归阻止重新全局关闭、未登记反射回退及列表项目类型遗漏。工具自身的独立试验AXAML设置不代表生产回退。

先完成实际上下文、模板与命令交互，再删除App.csproj的全局false及旧技术原型注释。固定Avalonia12.1.3的buildTransitive/Avalonia.props默认为true，当前生产项目MSBuild解析值也为true；不升级依赖。[官方编译绑定文档](https://docs.avaloniaui.net/docs/data-binding/compiled-bindings)作为API入口，实际类型、编译结果及默认值以已安装12.1.3和当前项目为证据。

交互中修复四个实际问题：

- 命令初始化错误模型强制转换，以及未选行时SelectedLine.Text空路径：用明确命令范围与已有CurrentLineText总值属性处理。
- 材质槽双向选择刷新：只在选项内容确实变化时替换MaterialSlots／Targets，保留不变选项及身份。三槽逐次检查界面选择、模型与运行请求一致；需要变化或输入失效时仍更新真实可用目标。
- 配方范围Run.Text只用于显示，显式OneWay，避免尝试调用不支持的ConvertBack。
- Suki7.0.1列表测量调用模板字符串时，原记录自动遍历会读取包材质禁止的纯色访问器。StyleTemplate.ToString明确返回Name，不遍历材质／包闭包；纯色访问门禁、记录相等性及持久数据保持。真实包材质预设保存后的个人列表通过，未修改上游主题或包版本。

新增有限Headless绑定流程53个断言，包含最近工程替换／清空／实际打开、三类新建基线、歌词行与嵌套逐字、分支选择、播放栏上下文切换／清空、枚举显示、非法参数保存／运行门禁、运行报告、真实Roslyn源码诊断、工坊包参数错误／父编辑能力、配方及个人预设。生产私有试样接线方法另用静音对象检查精确TimeUs、PlayLabel及Loop双向更新，不创建声音后端。Binding日志从开始到结束收集，Warning以上为零；运行报告消费检查使用明确结果替身，不冒充真实720帧，正式执行器另由App定向测试覆盖。

App135／Core模板57、Headless总665个断言通过。六项成功编译后的变异分别触发命令模型、材质槽选择、模板字符串、枚举显示、父控制可见性及全局关闭策略失败；错误属性名另被AVLN2000编译门禁捕获，不混入编译后变异计数。所有临时源码恢复，最终构建和复查见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.153。PATCH0.49.1无新格式、SDK接口、程序集或依赖；全量测试继续暂停，不启动／操作用户窗口，不推定Windows、物理输入或最终连续人工验收。F6b剩余UI排队延迟仍保留未解决边界。
