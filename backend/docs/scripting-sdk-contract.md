# 公开脚本 SDK 参考

> 2026-11-01 源码盘点入口：[后端样式系统与脚本 SDK](style-reference/README.md)，
> 其中 [SDK 成员与当前门禁](style-reference/script-sdk.md)原按 App `f3fdfdf`、后端 `0.109.0` 核对，[静态辉光](style-reference/glow.md)按后续 `0.110.0` 工作树补充。
> 本文以下保留各历史版本合同和旧桌面操作说明；历史版本号、待实施状态及 GUI 路线不代表当前 App 能力。

> 当前运行宿主0.46.0补充：作者SDK报告方法与下述能力门禁保持，Rendering.Abstractions新增`RuntimeDiagnosticSnapshot`／`RuntimeDiagnosticBuffer`／`RuntimeDiagnosticCursor`供宿主使用；现有完整Diagnostics数组仍按需可读。正式预览／导出／运行验证以发生身份增量消费，完整日志和同文多次报告不截断，Error来源与失败门禁保持。见[诊断快照合同](scripting-diagnostic-snapshot-contract.md)／ADR-0176；当前数据格式28／16／6、配置1、包profile2保持，后述0.40.2版本和格式为该轮能力盘点基线。

> 当前能力与作者路线更新至 **0.40.2，2026-10-06**。语义依据[已接受实施合同](archive/plans/scripting-system-implementation-contract.md)与已实施 ADR；完成度以 [roadmap §18](archive/history/roadmap.md) 为准。目标 SDK **1.0.0 尚未冻结**；包清单中的 `sdkVersion:1.0.0` 是当前宿主要求的协议声明，程序集随程序为 0.40.2，实际 SDK 字节参与编译缓存身份。工程／歌词／模板 schema26／16／5、包 profile2 与 shader profile `skia-raster-3.119.4-v1` 保持。

当前施工计划的 R3 Core、R4 持久闭包组合、R5 会话、R6 粒子与 R7 工作区／模板自动出口已按各自限定范围收口；证据见[下一步计划](archive/plans/scripting-system-next-work-plan.md)、[R6 验收](archive/acceptance/scripting-r6-acceptance.md)与 [R7 验收](archive/acceptance/scripting-r7-acceptance.md)。这不代表原路线的全部 R2～R8 能力已交付。R8 第1步已完成 CustomInk／LineGlitch／GlassInk 公开包、作者索引和自动生产回归；0.40.2已通过本计划R8同一作者工程的完整成片连续自动验收和备份／取消恢复，见[R8成片验收](archive/acceptance/scripting-r8-acceptance.md)；2026-10-06用户确认文字／粒子／资源效果均无问题并接受当前goal完成，见roadmap §18.108／ADR-0141。完整SDK冻结及原路线高级能力仍为后续工作。

## 当前能力与门禁

| 能力 | 0.40.2 正式路径与限制 |
|---|---|
| 源／场景目录、几何、播放头与事件 | 稳定身份、只读目录、120 Hz 固定共享推进、精确事件、历史／源订阅、最终文字几何及独立语义端点已接通；读取不授予写权 |
| 生命周期 | 同一作者对象的七阶段与 Dispose；预览和导出使用独立固定会话，不以重绘次数推进共享状态 |
| 元素族／前奏标记 Style | solid／gradient／包材质、描边、阴影和透明度；基础字与注音共用族外观；标记使用真实 Cue／Binding 身份 |
| 文字与组变换 | 元素族缩放、整行／标记／组 ApplyAffine 已接通；组仿射与透明度只允许工程／显式 Node 写权；整行 Style 及组填充／描边／阴影没有开放 |
| 源包材质与逐节点 uniform | 未唱／已唱／描边源槽位及 TokenFamily／TextMark 的帧材质进入正式 Render；`Materials.SetUniform` 显式部位＋节点 ID＋名字，七类值／数组严格校验；不支持整行／组材质 writer |
| 图像效果 | TokenFamily／Line／TextMark 的 ColorOnly 图、真实彩色部件、BackgroundMedia／BackdropBelow 与替换合成；同实例同目标最后绑定，跨实例按工程顺序串接；纯图导出可直接创建实例，无假 C#／DLL |
| 图元和粒子 | Dot／Segment／Trail／Sprite、连续发射／事件 Burst、四类真实来源、固定拖尾、Jump／Bridge 和并发后继；出生、存活、拖尾与绘制使用统一预算，超限明确失败 |
| 工程资源 | 固定源码／图／shader／图片包、实例／绑定／订阅／seed／参数、源材质、媒体及已接受音频分析保存为完整闭包；另存为和自动备份独立持有资源 |
| 作者工作区与模板 | 包目录／`.mkfx` 导入分享，实例 CRUD／顺序、参数与源码草稿、单实例修订接受、诊断定位、正式异步试样；模板相对配方、目标选择与 `.mktpl` 闭包分享／导入已接通 |
| 透明输出 | 背景依赖图按显式旁路合同跳过，背景依赖材质使用自身包内 solid／gradient 基础外观并提示；仍执行包、shader／ABI、参数和 uniform 校验 |
| 保持关闭的门禁 | 组图／可按部件读取的父捕获、owned overlay／目标内粒子捕获、ImageOnly／MappedWarp／实际图像锚点映射、跨输出依赖计划及未实施的多具名图输出；中立类型或宿主接缝不等于生产能力 |

`AnchorMap` 数学值和 `Mappings` 描述不能代替实际映射出口；当前公开 LineGlitch 是 **ColorOnly 像素位移**，不改变几何／播放头，也不声称实现 ImageOnly。`ImageOnlyPolicy.IncludeInTargetCapture` 仍明确拒绝。RepeatEmphasis 调用 `RequireSungSolidColor()`，其绑定元素的已唱基线必须为 solid；自定义材质组合可放在未唱／描边槽位或独立目标，不从 gradient／shader 猜基色。

## 作者操作路线

### 工程实例与源码

1. 从主窗口“打开脚本效果工作区…”进入，或从歌词行／元素“本行效果…”／“本元素效果…”、展示对象“对象效果…”进入。上下文入口保留稳定目标和播放头；存在未应用或过期场景草稿时，先应用或放弃该草稿。已有同绑定实例优先选中。
2. “导入目录”选择包含 `manifest.json` 的完整包目录，或“导入 .mkfx”。选择控制器／图导出后“新建实例”；新实例默认禁用。按真实导出支持设置写入绑定、独立读取订阅、十六位十六进制 seed、参数与执行顺序。包材质通过模板源材质槽位使用，不作为直接图实例。
3. 显式启用并“验证工程效果”，查看当前时间的正式普通／透明试样和诊断。“当前时刻渲染成功”只证明该时刻；导出仍验证整段。“保存工程改动”将工作区草稿提交到工程撤销历史，再保存主工程得到磁盘闭包。保存和运行验证分开。
4. “包源码”只编辑已有 `csharp` 文件。跨包／文件草稿分别保留；“保存修订并编译”发布新的不可变完整修订并执行完整包门禁。编译成功不升级实例；“将此修订用于当前实例”只改变当前同包实例，保留身份、入口、参数、seed、绑定、订阅、启用与顺序，兄弟实例保持原修订。
5. 编译失败的源码修订仍可保存、重开和撤销，真实诊断定位文件行列。引用失败修订的启用实例阻止正式运行／导出；旧预览是最近成功图像，不是当前效果成功，也不回退旧 DLL。“分享包”写出所选固定修订的 `.mkfx`，不把未保存源码草稿混入归档。

作者编辑器不提供 manifest／graph／SkSL 编辑器。修改这些包文件后应在外部更新清单摘要，再完整导入新修订；所有声明和资源须过当前严格门禁。运行 C# 是主进程内的受信作者合同，不是技术沙箱。

### 模板材质与配方

在模板工坊导入完整目录或 `.mkfx`，于未唱／已唱／描边槽位选择声明兼容的材质导出并编辑规范参数；未引用候选不会写入模板闭包。正式中央试样使用异步工程会话，包含效果／源材质时按工程时间运行，Preparing 和旧图都不计作成功；卡片的静态样式摘要不能替代正式试样。工坊固定试样只含底色，不读取当前工程背景；R8交付模板的 `glass-media` 在普通试样中会明确报告 `GraphRequiredInputUnavailable`，应在带真实媒体的工程中检查正式画面，透明输出按包声明回退。

行模板可选择“整行”或“行内全部元素族”相对配方；元素模板固定所选元素族。候选按目标及导出绑定筛选，非图元元素族输出必须为 tokenFamily，不借 Tokens 写行／标记／组。显式“添加”或“更新”提交作者表单，更新保留 RecipeId，移动改变有序配方。保存模板不隐式提交尚未添加／更新的配方输入。

保存到个人模板库后，从歌词行／元素编辑器套用；静态样式与相对实例一步进入工程历史。重套用保留该来源实例的身份／seed／启用状态，独立实例与其他模板来源保持。分享已保存的个人模板为 `.mktpl`，包含静态样式、源材质、配方和精确包闭包；内置模板先复制保存。同 ID 同规范内容导入幂等跳过，同 ID 不同内容整批拒绝。具体路线见[目标合同](scripting-r7-template-target-contract.md)、[传输合同](scripting-r7-template-transfer-contract.md)与[公开包索引](../samples/ScriptEffects/README.md)。

以下 §1～7 保留公开值与 API 参考。标有版本的实施记录描述**该版本当时的状态**；当前可用能力与关闭门禁以上表为准，历史“待实施”不覆盖后续已交付接续。

## 1. 程序集和基础值

namespace `MakoKaraGenerator.Scripting`，程序集 `MakoKaraGenerator.Scripting.Abstractions`，完整目标 SDK 1.0.0 尚未冻结；当前程序集随程序为 0.41.0，缓存包含实际 SDK 字节。无 Core、Skia、Avalonia、Roslyn 引用，只有自身只读值和 .NET 基础类型。公共成员全部 XML 注释。0.21.0 U1 增加应用层模板材质编辑与正式试样会话，0.22.0 U2 基础增加应用层工程效果实例工作区，0.23.0 接通工作区的包闭包导入与原子分享，0.24.0 接通跨文件／跨包源码草稿、新固定修订独立编译与诊断定位、显式单实例接受及失败源码保存；工作区保存与工程正式会话验证分开，新建实例初始禁用。源码保存或编译成功不自动升级引用。这些批次都不增加作者 SDK 方法或包 profile；具体作者工具边界见[源码编辑合同](archive/legacy-ui/scripting-workspace-source-editing-contract.md)／ADR-0110／roadmap §18.76。

ID 为独立 `readonly record struct`，内容为 ordinal 字符串；不允许 NodeId 隐式转换成 TokenId。TimeRange(StartUs,EndUs) 为微秒半开区间。Point2／Vector2／Rect2／Affine2 为有限 double；Affine2 有 six components／Compose／TryInverse，矩阵组合顺序以父矩阵×局部矩阵定义，不沿用库特有约定。

`ColorRgba` 为 sRGB 直乘的有限 [0,1] 四分量；明确颜色与原始数据向量不同。MaterialValue 只可 Solid／Gradient／PackageExport（已固定的资源句柄和参数集），不可包含 native shader。Gradient 沿用规范色标及空间，不隐藏第二个基色。时长和 seed 不用 double 保存。

| 公共投影 | 属性／查询合同 |
|---|---|
| ContentCatalog | Branches／Lines／Tokens／Ruby；GetToken(TokenId)／TryGetToken；FindTokens(text,StringComparison.Ordinal)；源顺序、源区间和 TimingStatus |
| SceneCatalog | 节点／父 ID／参考空间／clips／leadIns，只读；GetNode(NodeId)；无假行或假字 |
| ProjectContext | Content／Scene／CanvasSize／DurationUs／Audio；不返回原 Project |
| SourceSubscription | BoundSources／Branch／Project；构建时验证，不在脚本运行时悄悄扩写绑定 |
| ParameterValues | GetNumber／GetInteger／GetBoolean／GetEnum／GetColor／GetDuration／GetCurve／GetText／GetAsset；缺字段／类型错抛出具名诊断 |
| ResourceCatalog | GetMaterial(exportId)／GetGraph(exportId)／GetImage(assetId)；包内逻辑句柄，固定修订 |
| DeterministicRandom | GetUInt32(streamKey,index)／GetUnitDouble(streamKey,index)；纯索引 PCG32-v1，不消耗一个全局会变的帧随机游标 |
| TimelineHistory | 已发生事件（截至会话边界），GetStarts(TimeRange,SourceFilter)；公开不可变 EventRecord，不暴露实例对象。0.45.5宿主按代次共享已验证表，Records维持ImmutableArray签名且显式访问才物化；公共构造严格校验，详见[历史前缀](scripting-history-prefix-contract.md) |
| AudioFeatures | GetAt(timeUs)／GetRange(range)；BassLevel／Pulse／BeatPhase／BeatConfidence／Availability；无数据不返回假的 0=已分析 |
| Diagnostics | Report(severity,code,message,sourceLocation?)；宿主追加实例／时间上下文；Info／Warning 只记录，Error 使阶段失败并阻止导出；不擅自改外观 |
| GeometryQueries | 已实施 GetBaseAt(timeUs,SourceFilter)，限 Initialize／OnEvent／Advance／Evaluate 本阶段；完整源筛选独立于输出绑定。宿主 SemanticAnchorQuery 独立重放最终端点，不开放脚本递归 PrepareFrame |
| BaselineStyles | 元素／行／标记 Get(target) → 只读 StyleView，组 Get(GroupTarget) → 只有静态 Opacity 的 GroupStyleView；RequireSungSolidColor() 在非 solid 材质时报告具名错误；不是从 shader／gradient 自动猜基色 |

Media 使用真实流式离线分析：48 kHz、20 ms、规范 PCM／源文件／算法修订，低频近似／Pulse／节奏相位和 confidence；已接受完整分析从工程闭包注入 AudioFeatures。`RequiresAudio` 只要求初始化前 Available；音轨范围外返回 OutsideRange／null，不制造静音值。包声明、保存及正式会话投影已在0.16.0接通，见[音频合同](scripting-audio-analysis-contract.md)与[媒体／分析存储合同](scripting-media-analysis-storage-contract.md)。

只读集合用 ImmutableArray／不可变记录；byte 资源仅通过受控只读句柄查询，不把包缓冲暴露成可写 Memory。

## 2. 生命周期

```csharp
public interface IKaraokeEffect : IDisposable
{
    void Initialize(EffectInitializationContext context);
    void OnEvent(TimelineEvent timelineEvent, EventContext context);
    void Advance(SimulationStep step, StateContext context);
    void Evaluate(FrameContext frame, FrameWriter output);
    void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output);
    void AdvanceVisuals(VisualSimulationStep step, VisualStateContext context);
    void EvaluateVisuals(VisualFrameContext frame, VisualWriter output);
}
```

六个历史入口在 0.9.11 接入；0.10.0 已按用户确认新增纯 EvaluateEmitters，七个入口及 Dispose 使用同一作者对象。Emitters 从 VisualWriter 移除；作者 Particles／EmissionSchedule、事件 Burst、ColorOnly 图与 Jump／Bridge 已接通；目标内捕获及未实施高级输出继续保持门禁。`EffectBase` 当前方法为无操作 virtual；具体参数访问与能力验证仍由宿主实施。一个 EntryType 对应一次实例，不按 writer 阶段重新构造。

| 阶段 | 上下文 | 可写能力 |
|---|---|---|
| Initialize | Project、Parameters、Resources、Random、CancellationToken | 初始化本实例共享／视觉状态；不提交本帧输出 |
| OnEvent | Project、History、Parameters、EventId、TimeUs、CancellationToken | 已实施共享历史状态及 context.Bursts.Queue；只登记当前真实事件意图，文字完成后解析最终几何 |
| Advance | `[start,end)`、固定起点输入、Project／History／Audio、CancellationToken | 推进共享状态；不访问最终粒子或图表面 |
| Evaluate | 时刻真实采样、共享状态、基线／基础几何 | 纯写 Style／Transforms／Materials／ImageEffects／Mappings 的**帧描述**；writer 不回写工程 |
| EvaluateEmitters | 最终文字几何／播放头、完整历史、固定服务；无粒子集合 | 纯写 EmitterWriter.Set，登记左边界描述，不推进或绘图 |
| AdvanceVisuals | 前一／当前最终文字几何、明确左／右历史、PreviousParticles／Particles／完整区间 EmissionSchedule | 仅推进视觉状态；禁止改同边界共享历史和已求值文字 |
| EvaluateVisuals | 最终几何／光标、最近边界几何、分数时长、完整 Particles／上一固定区间 EmissionSchedule | 已实施圆点、线段、带状拖尾与图片精灵；不得反向改变文字几何 |
| Dispose | 无新帧输入 | 释放实例的所有资源引用／临时对象；不能留静态订阅钩子 |

Evaluate／EvaluateEmitters／EvaluateVisuals 必须纯求值。受信 C# 可持有字段，不是技术沙箱；这项是作者合同。宿主诊断 writer 阶段错误、非法目标、非有限输出及未声明能力；不承诺检测任意脚本内部副作用。

## 3. 事件和采样对象

TimelineEvent 为封闭判别记录，kind：TokenStarted／TokenCompleted、LinePerformanceStarted／Completed、BindingEntered／Left、ElementVisible／Hidden、LeadInStarted／StepChanged／Completed、CursorTransition、AudioFeatureTick；新增 kind 提升 SDK 合同。

所有事件都有 TimeUs、EventId、稳定源顺序与 ReplayReason（Initial／Seek／Loop／InputsChanged／Export／AnchorQuery）。可选身份通过各派生事件的强类型字段表示，无适用源身份不造空 TokenId。TokenStarted 有 TokenId、OccurrenceId；前奏有 CueId、目标 LineId／BindingId。完整排序见实施合同 §4.1。

| 帧值 | 精确字段／语义 |
|---|---|
| FrameContext | TimeUs、LastSimulationBoundaryUs、Project、Parameters、Audio、History、Presentations、Baseline、Geometry、GeometryQueries、Binding、OutputMode、CancellationToken |
| FrameToken | TokenId、BranchId、LineId、BindingId、OccurrenceId?、SourceText、Performance、WithRuby、BaseOnly |
| PerformanceSample | TimeRange?、DurationUs?、ElapsedUs?、Progress?、IsCurrent、IsSung、ProgressSource(Token／Line／Missing／Unavailable)；Missing 不猜进度 |
| FrameLine | LineId、BindingId、NodeId、ProgramId、ClipId?、CueId?、DisplayPhase(Preparing／Performing／Retained)、EnterProgress |
| FrameMark | TextMarkTarget(NodeId／CueId／目标 BindingId)、TargetLine、ParentId、Text、IsElapsed、AnimationOpacity；无 TokenStarted |
| FrameGroup | GroupTarget(NodeId)、运行 ParentId、IsVisible、AnimationOpacity；没有假文字填充 |
| GroupGeometrySample | 真实 ReferenceBounds／BaseWorldMatrix／WorldPivot、实际后代 WorldInkCorners；空白没有墨迹 |
| GeometrySample | AdvanceBounds／InkBounds／Baseline／BaseWorldMatrix、绘制单元 ID／字簇映射／Ruby、可独立变形状态 |
| VisualFrameContext | 固定服务、FinalGeometry(FinalSceneGeometry)／同一 Playback、BoundaryGeometry、History／FractionalElapsedUs；无文字 writer，Particles 按 R6 |
| VisualStateContext | 固定服务、PreviousGeometry／CurrentGeometry、PreviousHistory／History；无文字 writer 或未来查询 |
| FinalSceneGeometry | 真实对象、最终 Token／Ruby／Line／Mark／Group 与绑定内 Playback；不可用不伪造空服务 |
| PlaybackCursor | CursorTrackId、TokenId?／LineId、BindingId、OccurrenceId?、TimeUs、Position、Direction、ProgressSource、AnchorMode |
| CursorTransition | From／To 身份、GapUs、Reason、PreviousEndLimit、NextStart；多个合法后继返回集合 |

0.9.8 的 `PreparedRenderFrame.Playback` 已提供最终文字矩阵的光标集合；通过 `GetCursors(CursorAnchorMode)` 明确选择基线或 Advance 中心。缺后端几何用 IsAvailable=false 并拒绝 GetCursors，实际无演唱为合法空集合。全行没有逐字时间才使用行依据；行内空档、Missing、准备和保留不造播放头。0.29.0 的 `CursorTransitionEvent` 已进入固定源事件及真实 callback／历史；`TimelineProjection.GetCursorSuccessors` 按实际输出绑定筛选两端，宿主 `ResolveSuccessorsAsync` 返回全部合法边及两端最终几何，具体规则见[后继合同](scripting-cursor-successor-contract.md)／ADR-0125。0.30.0 已将 Bridge 接入固定发射时轴，按每条合法后继独立计额；末段半开飞行及未来 Error／取消规则见[桥接合同](scripting-cursor-bridge-contract.md)。宿主专用 `SemanticAnchorQuery` 的 NextStart／PreviousEndLimit 使用 StartUs／EndUs-1，独立 AnchorQuery 重放共享状态及文字求值；装载不可见明确不可用，Error／取消不回退。具体合同见 [播放头与查询](scripting-r2-playback-geometry-contract.md)。

Frame.Presentations 的 TokensInBinding／LinesInBinding／MarksInBinding 已经过实例输出绑定筛选，Groups 是全部组的只读运行目录；读取不授予写权限。FrameContext 的 LineGeometry／MarkGeometry 匹配相应输出范围，GroupGeometry 为完整基础组几何。完整源目录不受绑定限制；没有可见对象时 shared callback 仍按源订阅发生。若作者需要当前帧之外的已唱计数，用 History／自身记录，不能靠可见 Token 数量计数。

## 4. 输出 API

0.11.1 时 Style writer 接受 TokenFamilyTarget 和 TextMarkTarget；Transforms 已有族缩放、标记、整行及真实组 ApplyAffine。标记两态分别为 Pending／Elapsed，完整 Cue／装载身份、范围和几何规则见 [场景目标合同](scripting-r2-scene-target-contract.md)；执行边界、无变化和合并语义沿用 [样式帧合同](scripting-r2-style-frame-contract.md)。组只有已接通的 alpha＋变换，没有 SetUnsungMaterial／SetSungMaterial／Stroke／Shadow 或组图 writer。整行 Style 尚未开放，不能从整行只读基线推断写权；组写权限仅工程／显式节点，不从源筛选推断。

目标值由宿主提供（TokenFamilyTarget／BaseTokenTarget／LineTarget／GroupTarget／TextMarkTarget）；不可随意把源 ID 拼成任意 native 对象。OutputId 标识包内有声明的独立输出／依赖关系。

```text
FrameWriter.Style.SetUnsungMaterial(target, MaterialValue)
FrameWriter.Style.SetSungMaterial(target, MaterialValue)
FrameWriter.Style.SetStroke(target, StrokeValue?); SetShadow(target, ShadowValue?)
FrameWriter.Style.MultiplyOpacity(target, factor) // 0.12.2 包含 GroupTarget，须有明确组写权
FrameWriter.Transforms.ScaleTokenFamily(TokenFamilyTarget, factor, pivot)
FrameWriter.Transforms.ApplyAffine(TextMarkTarget, Affine2) // 已实施
FrameWriter.Transforms.ApplyAffine(LineTarget, Affine2) // 0.9.10 已实施
FrameWriter.Transforms.ApplyAffine(GroupTarget, Affine2) // 0.11.1 已实施
FrameWriter.Materials.SetUniform(materialHandle, TokenFamilyTarget／TextMarkTarget, MaterialSlot, nodeId, name, UniformValue) // 0.13.3 已接通
FrameWriter.ImageEffects.Bind(LineTarget／TokenFamilyTarget／TextMarkTarget, GraphHandle, ParameterValues) // 0.9.15 描述／执行适配已实施
FrameWriter.Mappings.Bind(target, AnchorMap) // 中立描述；实际映射生产出口未开放
EmitterWriter.Set(emitterId, CursorEmitterDescriptor) // 独立 EvaluateEmitters；Jump／Bridge＋SemanticCursor
EventContext.Bursts.Queue(emitterId, BurstDescriptor) // 仅当前 OnEvent；0.11.0 已实施
VisualWriter.Overlay.DrawDots(OutputId batchId, ImmutableArray<Dot>, OverlayLayer layer) // 0.9.11 已实施
VisualWriter.Overlay.DrawSprites(batchId, ImmutableArray<Sprite>, layer) // 0.28.0 已实施
VisualWriter.Overlay.DrawSegments(batchId, ImmutableArray<Segment>, layer) // 0.27.0 已实施
VisualWriter.Overlay.DrawTrails(batchId, ImmutableArray<Trail>, layer) // 0.27.0 已实施
```

整行 ApplyAffine 使用固定逻辑坐标，包含注音和真实运行后代，附着前奏跟随而 FixedParent 按自己父树保持；局部族／标记输出在最终层次矩阵后各应用一次。Tokens 可读所属行但不能写整行。PreparedRenderFrame 的基础 Scene 与绘制 HierarchyScene 分开，最终行／族／标记／组几何及 Playback 共用同一矩阵；具体权限和生产边界见 [层次输出合同](scripting-r2-hierarchy-output-contract.md)。

0.11.1 增加 `Transforms.ApplyAffine(GroupTarget, Affine2)` 和 `FrameOutput.GroupTransforms`。Project 可写真实组；Node 可写自身组，includeDescendants=true 再授予本帧运行后代组；Branch／Lines／Tokens／Clips 不获组写权。组局部仿射与整行、族、标记各组合一次，隐藏真实组仍严格校验；可读 Groups 目录不授予写权限。生产像素、捕获、播放头及粒子出生共用最终父树。旧构造签名保留，具体见[组仿射合同](scripting-group-affine-contract.md)／ADR-0090。

material／graph 是固定包导出句柄，不编译帧内字符串；未声明 uniform 名称或值类型拒绝。Transforms 的局部视觉缩放不写 Typography.FontSizePx。非有限、比例≤0、alpha超[0,1]、写出绑定范围或未声明输出均为错误，不静默 Clamp。

0.9.13 已实现公开 `UniformValue.Float／Float2／Float3／Float4／Int／Color／Matrix3x3／Array` 和不可变 `UniformValues`。颜色使用 ColorRgba，矩阵按行给九项；数组非空、同类型、不嵌套，长度 1 仍是数组。SDK 不依赖 Core／Skia，不加载源码。现有 Core 的 ShaderProgramDefinition 和正式 Skia 原语已消费这些值并验证真实像素；当时 Materials writer 尚未接通；0.13.3 的逐节点／部位 API 见本文末尾；ImageEffects 描述／执行适配见后文，不能把值类当完整材质接口。具体数值、预算、原生 ABI 和释放边界见 [shader 合同](scripting-shader-program-contract.md)。

0.9.14 的图定义／参数约束由 Core 唯一持有，Rendering.Skia 已有 Canvas 输入的独立五类操作／合成／透明旁路执行器，并通过真实 MP4／Alpha 同帧验证。它不是 SDK 的 ImageEffects writer 或生产捕获协调器；脚本仍不能直接持有或请求 native 表面。图的边界、资源预算范围、背景依赖和待答 optional 行为见 [图合同](scripting-image-graph-contract.md)。

0.9.15 的 ImageEffects 需独立声明能力（值 8），仅 Evaluate 可写真实 Line／TokenFamily／TextMark，沿用现有范围；组写权限随后已确认仅工程／显式节点，组图 writer 尚未实施。GraphHandle 与资产参数须属于本实例固定包修订，元素族须有独立字形分区。同实例同目标只保留最后一次完整绑定，不合并旧参数；不同实例按工程顺序串接。中立准备帧保留实例／编译代次和视觉帧复制闭包；真实捕获／图执行适配随后进入主画布，0.13.1／0.13.3 材质及0.37.0直接图实例已接通；Mappings、组图及其余未开放能力继续具名拒绝，不能忽略绑定继续导出。具体能力、生命周期、像素及编码证据见 [生产捕获合同](scripting-production-capture-contract.md)／ADR-0079。


OverlayLayer 为 BehindText／AboveText；实际圆点按效果数组顺序／ordinal OutputId 批次／数组内部顺序。Dot 的 Position 为最终画布逻辑点、Radius 为正有限逻辑像素、Color 为 sRGB 直乘值，不再乘文字矩阵；同帧同实例批次唯一。VisualOutputBudget 默认全实例 100,000 项，可由工程效果预算／固定工厂明确调整；能力／阶段／预算／原生越限及下溢明确失败，不裁剪。阶段和实际生产证据见 [视觉阶段合同](scripting-r2-visual-stages-contract.md)。颜色、拖尾宽度和衰减可以提供固定曲线；不会隐式延长工程输出时长。

0.27.0 全部图元共用跨类型 OutputId、工程实例及 ordinal 批次顺序和统一预算；圆点／线段一项，拖尾每点一项。TrailPoint 的 Width 是世界截面全宽，零宽可作尖端；直乘颜色与密度只应用一次。CompleteOverlay 收取封闭协议，旧 Complete 不返回残缺非圆点输出；图／组复制及自身下层门禁保留完整图元。详见[生产合同](scripting-vector-overlay-contract.md)。

## 5. 发射和映射对象

CursorEmitterDescriptor 的 v1 字段：AnchorMode（必须显式选择）、TrackFilter（BindingCursors）、RatePerSecond、LifetimeUs、SizePx、Color、Velocity、SpreadPx、TrailDurationUs、TransitionMode(Jump／Bridge)、BridgeDurationUs、BridgeGapLimitUs、BridgeCurve、ImageOnlyPolicy(SemanticCursor／IncludeInTargetCapture)、Layer、SeedStreamKey、Motion（解析函数合同标识）。每帧 Set 是描述，不是增加出生次数。

ParticleDescription.Rendering 默认为 Dots；Custom 关闭自动绘制，保留所有模拟、只读粒子／轨迹／发射记录及四类预算，由 EvaluateVisuals 输出头部或拖尾。公开示例见 `samples/ScriptEffects/VectorTrail`；源 BirthIndex／旧行世界位置不因绘制方式改变。

宿主解析型运动首版支持匀速＋恒定加速度、年龄曲线透明度／大小；支持有界自管批量图元，不以一个内置粒子算法限制作者。自管状态必须在 AdvanceVisuals 中推进，纯绘制方法不再更新对象。宿主和自管模式不能双重更新同一 ParticleId。

AnchorMap.Affine／AxisWave 数学值已实施；Mappings 描述和真实 MappedWarp 尚未接入生产，继续按 R3.1 保持门禁。参数有限、逻辑单位、有界定义域；ImageOnly 不是假映射。v1 不提供任意 C# native shader mapping 函数。跨行空档桥接终点使用独立语义会话；PreviousEndLimit 为离场前极限，不能采样半开 EndUs 而误取下一行。

## 6. 三个 C# 作者示例

以下三份源码对应可导入的 WordEmphasis／RepeatEmphasis／CursorTrail 完整目录包，工作区导入与 `.mkfx` 分享已接通。九类参数的 JSON／SDK 投影、控制器入口、绑定和声明外输出门禁见[包语义合同](scripting-package-semantics-contract.md)；全部九个公开目录包及验证状态见[示例索引](../samples/ScriptEffects/README.md)，不因此冻结完整 SDK。

以下保留三个示例的核心作者代码；初始化／关闭由 EffectBase 提供。实际可消费的参数、导出和当前源码以对应目录的 `manifest.json` 与 `src/*.cs` 为准；生产回归直接读取公开文件，不改用测试私有入口。

### 6.1 逐字强调（以回落模式为例）

```csharp
using MakoKaraGenerator.Scripting;

public sealed class WordEmphasis : EffectBase
{
    private double peak;
    private long returnUs;

    public override void Initialize(EffectInitializationContext context)
    {
        peak = context.Parameters.GetNumber("peakScale");
        returnUs = context.Parameters.GetDuration("returnUs");
    }

    public override void Evaluate(FrameContext frame, FrameWriter output)
    {
        foreach (FrameToken token in frame.Presentations.TokensInBinding)
        {
            PerformanceSample p = token.Performance;
            if (p.ProgressSource != ProgressSource.Token || p.TimeRange is not { } range)
                continue;
            double factor = 1;
            if (p.IsCurrent && p.Progress is { } progress)
                factor = 1 + (peak - 1) * Curves.EaseOut(progress);
            else if (p.IsSung && returnUs > 0 && frame.TimeUs < checked(range.EndUs + returnUs))
                factor = 1 + (peak - 1) * (1 - (double)(frame.TimeUs - range.EndUs) / returnUs);
            output.Transforms.ScaleTokenFamily(token.WithRuby, factor, Pivot.BaseAdvanceCenter);
        }
    }
}
```

范围的加法实施时检查溢出；peak／returnUs 清单校验。参数 endMode 的其他模式用同一纯求值分支，不能在演唱结束事件直接改源字号。

### 6.2 重复文本 callback

```csharp
using System;
using System.Collections.Generic;
using MakoKaraGenerator.Scripting;

public sealed class RepeatEmphasis : EffectBase
{
    private readonly Dictionary<(string Scope, string Text), int> counts = new();
    private readonly Dictionary<OccurrenceId, int> ordinal = new();
    private bool projectScope;
    private double scaleStep, maxScale, darkenStep, minBrightness;

    public override void Initialize(EffectInitializationContext context)
    {
        projectScope = context.Parameters.GetEnum("countScope") == "project";
        scaleStep = context.Parameters.GetNumber("scaleStep");
        maxScale = context.Parameters.GetNumber("maxScale");
        darkenStep = context.Parameters.GetNumber("darkenStep");
        minBrightness = context.Parameters.GetNumber("minBrightness");
    }

    public override void OnEvent(TimelineEvent evt, EventContext context)
    {
        if (evt is not TokenStarted started || ordinal.ContainsKey(started.OccurrenceId))
            return;
        SourceToken token = context.Project.Content.GetToken(started.TokenId);
        var key = (projectScope ? "project" : token.BranchId.Value, token.Text);
        counts.TryGetValue(key, out int previous);
        int current = checked(previous + 1);
        counts[key] = current;
        ordinal[started.OccurrenceId] = current;
    }

    public override void Evaluate(FrameContext frame, FrameWriter output)
    {
        foreach (FrameToken token in frame.Presentations.TokensInBinding)
        {
            if (token.OccurrenceId is not { } id || !ordinal.TryGetValue(id, out int n))
                continue;
            double scale = Math.Min(maxScale, 1 + (n - 1) * scaleStep);
            double brightness = Math.Max(minBrightness, 1 - (n - 1) * darkenStep);
            ColorRgba baseColor = frame.Baseline.Get(token.WithRuby).RequireSungSolidColor();
            output.Style.SetSungMaterial(token.WithRuby,
                MaterialValue.Solid(baseColor.MultiplyRgb(brightness)));
            output.Transforms.ScaleTokenFamily(token.WithRuby, scale, Pivot.BaseAdvanceCenter);
        }
    }
}
```

这是**明确输出纯色的示例**，要求基线为 solid；RequireSungSolidColor 在 shader／gradient 上报告具名错误，不自动采一像素假冒基色。高阶版本可以直接驱动包材质 uniform。旧句按自身 Occurrence 的 n，不读最终 counts 的最新值。比较是 ordinal 精确，参数归一化如后续添加必须显式。

### 6.3 光标发射器

```csharp
using MakoKaraGenerator.Scripting;

/// <summary>连续光标圆点与固定拖尾；使用真实歌曲时间而非显示器帧数。</summary>
public sealed class CursorTrail : EffectBase
{
    private CursorEmitterDescriptor descriptor = null!;
    public override void Initialize(EffectInitializationContext context)
    {
        var p = context.Parameters;
        descriptor = new()
        {
            AnchorMode = CursorAnchorMode.AdvanceCenter,
            RatePerSecond = p.GetNumber("rate"), LifetimeUs = p.GetDuration("lifeUs"),
            SizePx = p.GetNumber("sizePx"), Color = p.GetColor("color"), SpreadPx = p.GetNumber("spreadPx"),
            TrailDurationUs = p.GetDuration("trailUs"),
            TransitionMode = p.GetEnum("transition") == "bridge" ? TransitionMode.Bridge : TransitionMode.Jump,
            BridgeDurationUs = p.GetDuration("bridgeUs"), BridgeGapLimitUs = p.GetDuration("bridgeGapLimitUs"),
            Layer = OverlayLayer.AboveText, ImageOnlyPolicy = ImageOnlyPolicy.SemanticCursor,
            SeedStreamKey = "cursor-trail",
            Motion = new() { Opacity = new(1, 0, new CurveValue.Easing(EasingKind.Linear)) }
        };
    }
    public override void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output)
        => output.Set(new("cursor"), descriptor);
}
```

RatePerSecond 由固定模拟时基积分，不由 EvaluateVisuals 调用次数累计。每条合法头独立积分；Jump 直接换点，Bridge 按合法正空档末段及独立最终端点发射，两者都保留旧粒子按寿命消散。当前支持宿主圆点／点状拖尾，或作者 Custom 的 DrawSegments／DrawTrails／DrawSprites；不另画 UI 特效层。桥接及完整图元参数见[粒子包合同](scripting-particle-packages-contract.md)。

## 7. 公共 API 和验证门禁

- 每个上下文／writer／目标／输出值有 XML 注释：单位、空间、时间、调用阶段、生命周期、异常与能力。
- SDK 提供类型列表／枚举库存测试；公共类型无 Core／Skia／Avalonia 类型穿透；引用方向构建检查。
- 三个作者源码、材质控制器样例在真正 SDK 下 Roslyn 编译，运行像素／事件／保存／重开验证。示例不可依赖测试可见的内部成员。
- 对比 24／30／60fps、任意跳转／中段导出／前奏／无时间字、参数撤销和粒子映射。writer 拒绝非法／未授权输出，EffectFailureException 不被占位帧吞掉。
- R1 只验证最小编译／shader能力；不能用它的最小接缝声称完整 SDK 已经实现。本参考已随合同 A 接受，新增破坏 API 需要提升 SDK 版本并记录具体变更。

## 历史实施记录

以下小节保留各版本的实现过程、格式与当批限制。历史程序／程序集／schema 数字不表示当前版本；Bridge、生产材质、工作区和模板的后续接续见本文首页。当前验证状态以各批验收表及 roadmap 为准。

### 宿主中间表面预算（0.9.16）

作者仍只输出不可变图绑定，不取得 ImageSurfaceBudgetSession／Frame 或 native 图像。这两者属于正式渲染后端，由预览／导出宿主创建固定账本并共用于整帧操作；当前捕获、图执行、合成及绑定适配已计入存活产物与显式临时层。预算错误经真实 InstanceId／ArtifactHash／ExecuteImageGraph／时刻传播，不能视为透明旁路或普通占位帧错误；完整场景协调器及持久设置继续按 R3／R4。具体所有权和验证见 [账本合同](scripting-surface-budget-contract.md)。


0.9.17 新增的是宿主 Skia 合成链和真实工程媒体／底色生产，未增加脚本 native API。后续普通图保留之前替换范围；排除注音／描边／阴影须保留原绘制的行为已获接受，正式分区调度仍待实施。完整范围、顺序与失败约束见 [场景合成合同](scripting-scene-composition-contract.md)／ADR-0081；不是完整 R2／R3 或 UI 交付。

### 宿主彩色部件（0.9.18）

SceneColorPart 和 CaptureSceneColorPart／AssembleSceneColorParts 属于渲染后端合同，不扩大作者 SDK 权限。六类真实颜色部件保持固定字形／裁切／最终矩阵，共同 alpha 单独保存，显式顺序重组后仍未应用；脚本不读取 native 图像、不负责组装表面。效果与保留部件的自动叠放、混合配方和父子主画布调度仍须完成，叠放顺序已询问、答复前不默认。见 [部件合同](scripting-color-parts-contract.md)／ADR-0082。

### 包内容投影（0.9.19）

EffectPackageContents／EffectPackageTransfer 属于领域和应用资源服务，不向作者开放机器路径或 native 对象。EffectPackageSources 将固定 csharp 字节投影到现有离线编译器；实际归档离开原目录后仍能编译、加载并调用 IKaraokeEffect。内容修订包含所有资源，ArtifactHash 仍只消费源码和固定编译工具链。参数／导出／资源目录及绑定语义尚须校验，不能把文件闭包当作可启用的控制器或将全部文件自动纳入 ResourceCatalog；详见 [内容合同](scripting-package-content-contract.md)／ADR-0083。

### 包程序接续（0.9.21）

包内 shader ABI 使用独立 `.program.json`／role:shaderProgram 并由正式原生反射门禁验证；不把 native 程序或包路径暴露为作者 writer。SDK UniformValues 仍是固定帧值，包程序集合逐帧创建 shader 不重编译；不因 ABI 解析给控制器新增 ImageGraph 能力。B 随后获确认，0.9.22 已建立包图计划及逐输入捕获；C 导出／回退编码仍待答，完整包材质和可启用导出目录仍未建立，见 [补充合同](scripting-package-graph-semantics-contract.md)／ADR-0085。

### 包图接续（0.9.22）

包图编码 B 已确认并接通唯一 Core 图计划、正式 CPU 门禁及逐输入真实捕获／图片输入。此中立元数据及 native 产物仍由宿主持有，不自动为作者建立 GraphHandle 或扩大 ImageEffects 输出权。非 Canvas 执行、owned overlay／材质区域捕获及跨图逐部件重组未接入时明确失败；C、正式主画布和工程／工作区后续实施。实际证据见 [补充合同 §8](scripting-package-graph-semantics-contract.md#8-b-的实际实施路径0922)／ADR-0086／roadmap §18.51。

### 连续发射与作者查询当前接口（0.10.0）

EmitterWriter.Complete 固定发射描述，VisualWriter.Complete 只完成圆点；旧 VisualWriter.Emitters／CompleteOutput 已删除。EmitterId 即逻辑 Overlay OutputId，同帧最后一次 Set 生效；源绑定及 package declaration 不因 Overlay 能力被扩大。ParticleSimulationBudget 由固定 TimelineFrameSourceFactory 明确传入，初始 20,000 活粒子／200,000 固定样本；VisualOutputBudget 仍约束全部实例绘制项。ParticleMotion 包含常加速度、显式端值＋CurveValue 的年龄 opacity／size。PreparedRenderFrame.Particles 是宿主真实不可变视图，作者上下文已提供按实例隔离的 Particles／EmissionSchedule。

连续时间、精确配额／身份／出生几何、临时帧与生命周期及剩余范围见 [连续粒子合同](scripting-continuous-particles-contract.md)／ADR-0087。项目／模板持久数据不变，SDK 完整 1.0.0 仍未冻结。

0.10.0 按 [发射阶段实施合同](scripting-emission-stage-proposal.md)／ADR-0088：固定边界先登记描述并解析当前粒子，再 AdvanceVisuals／EvaluateVisuals。作者只读集合仅含本实例，保留离场绑定的存活对象；EmissionSchedule 为最近完成 [a,b) 的全部真实出生（含已死亡），初始 [0,0) 为空，分数帧保持该记录。纯帧读取粒子／记录明确 TimelineSessionRequired，AnchorQuery 跳过发射和视觉。独立 EmissionRecordLimit 默认100000，全部实例计入，可由固定工厂调整，分配前超限失败；不是 liveParticleLimit 的别名。CursorTrail 完整源码包及 .mkfx 消费已验证；工程保存／工作区／六包成片仍未交付。


### 事件 Burst 与四类真实粒子来源（0.11.0）

EventContext.Bursts.Queue(EmitterId, BurstDescriptor) 隐含当前真实 EventId／TimeUs，同事件同发射器最后登记；不同事件独立。BurstDescriptor 继承封闭 ParticleDescription 的物理字段，另存 Count 与明确 BurstAnchor，不含 rate。每个匹配锚点独立 Count；固定元素／行／标记使用 Advance 中心和最终矩阵，BindingCursors 使用所选播放头锚点。Overlay 和包内同名输出声明均须满足，订阅不授予额外输出权。

ParticleSample／ParticleBirthSample 保存封闭 ParticleSource：Cursor(TrackId, CursorIdentity)、Token(TokenFamilyTarget)、Line(LineTarget)、TextMark(TextMarkTarget)。BindingId 从 Source 读取，TrackId 只有 Cursor 非 null；EventId 只有 Burst 非 null。这是明确的不兼容 SDK 数据更改，程序升 MINOR，编译缓存使用实际 SDK 字节，不保留旧来源构造接口。

同刻先按实际事件序处理 Burst，再处理连续到期出生；两者共用 BirthIndex，Burst 不改变连续配额。边界出生立即在 Particles 中，上一完成 [a,b) 不含 b，新 [b,c) 才接纳一次；已死亡的短命出生也保留完整记录。记录、活粒子、固定拖尾和绘制预算统一按全部实例检查。指定锚点缺失、非法源、越权、过期 writer 或 Error 明确失败并阻止导出；绑定播放头空集合合法，后端不可用不能当空集合。

独立 AnchorQuery 仍派发相同语义事件并校验登记，但不解析／分配粒子。当时最小作者源码位于 `samples/ScriptEffects/Examples/EventBurst.cs`；当前完整可导入包位于 `samples/ScriptEffects/EventBurst`，源码为 `src/EventBurst.cs`。当时例子的，显式参数为 count（非负整数数值）、lifeUs、sizePx、color、spreadPx、trailUs；完整公开包可导入，但不作为自动启用的默认安装效果。合同及证据见 [事件 Burst 合同](scripting-event-burst-contract.md)／roadmap §18.54／ADR-0089。

0.11.2补齐真实包VisualTransform/group创建门禁；组writer和工程／显式节点权限仍按ADR-0090，包声明不扩大范围。证据见roadmap §18.57。

## 0.12.0 真实包目录与图参数检查

ParameterValues.Ids 是不可变、ordinal 排序的完整参数身份，供宿主验证图导出的完整参数集合；不暴露可写值表。包导出／参数唯一来源为宿主 Core 的 EffectPackageDefinition，真实编译门禁为 CompiledEffectPackage，原控制器专用类型删除，不保留别名。SDK ResourceCatalog 只公开完整验证过的材质／图导出及图片；图文件路径不是 GraphHandle，材质与图 ID 不能互换。

真实包的图输出须同时满足控制器与图的目标声明、实际绑定类别、全部参数类型／范围和固定资源身份。ColorOnly 单一同类别输出已进入实际准备帧；多同类具名输出、requires／跨输出计划及实际图像映射尚未完成时明确失败，不推断默认输出。材质回退可由宿主显式读取为 Solid／Gradient；正常 PackageExport 材质绘制及主画布自动合成仍须 R3 接入。纯资源包无 C# artifact，有源码时仍检查全部。实际证据／边界见[包合同 §10](scripting-package-graph-semantics-contract.md#10-c-实际实施接缝0120)。

0.12.1 宿主接续：PreparedRenderFrame 图描述经固定 native 集合进入正式 Render，逐输入部件来源／真实下层／旁路提示按[主画布合同](scripting-main-canvas-composition-contract.md)处理。脚本没有 native API 或帧内编译入口；组图、材质区域及持久工作区仍未交付。

## 0.12.2 组透明度输出

`Style.MultiplyOpacity(GroupTarget, factor)` 写有限非负乘数；同目标调用和实例依序相乘，最后检查静态基线×乘积在[0,1]，不夹值。FrameOutput.GroupOpacities 为中立描述，宿主 PreparedRenderFrame.GroupOpacities 保存最终有效值及真实归属，恒等省略。公开原有构造入口保留；不新增组填充／描边／阴影。

组权限与 ApplyAffine 相同，仅 Project／显式 Node；includeDescendants 决定额外组写范围。包须声明 StyleModulation/group，Style 能力或只读组目录不能扩大输出类别与权限。主画布在孩子完成后统一应用父组 alpha，嵌套逐层执行；真实行图／替换下层也随组淡出。顺序差异诊断经 RenderFrame.SceneDiagnostics 传递到缓存和报告。完整组图与部件捕获未交付，原始 TargetColor 捕获不会静默忽略已写入的组 alpha，详见[合同](scripting-group-opacity-isolation-contract.md)／ADR-0094。


## 0.12.3 父级合成产物接缝

`ImageCompositionPayload`、`CaptureCompositionPayload`／`ComposePayloads`／`CompositePayload` 仅属于Skia宿主后端，作者不接触native表面。它们保留颜色之外的真实下层擦除，独立孩子的顺序组合与同目标图链历史max分开；共同alpha各一次，拥有复制产物并接受共享预算。该基础接口不开放组图writer或变更SDK权限。父配方部分alpha、空组图执行及父图继承擦除三项尚待用户裁决；正式接入边界见[合同](scripting-parent-composition-contract.md)／ADR-0095。


## 0.13.0 区域材质接续

作者MaterialValue.PackageExport签名不变；材质清单新增必填coverageMode:unmasked／masked，当前示例及测试采用packageVersion2。Skia宿主已有真实区域／包基础外观捕获和固定导出执行，TargetColor不能采样旧填充。透明跳过背景材质使用包内已覆盖基础外观；输出覆盖只发生一次，错误模式／越界像素明确失败。

宿主接口不能代替作者writer完成：PackageExport准备帧仍报UnsupportedMaterialBackend，Materials.SetUniform和主画布自动材质调用尚未开放。不把新增native表面返回给作者，不提升读取目录为写权限。详见[区域材质合同](scripting-region-material-contract.md)／roadmap §18.62。

## 0.13.1 包材质作者输出与生产接入

`Style.SetUnsungMaterial／SetSungMaterial(TokenFamilyTarget／TextMarkTarget, MaterialValue.PackageExport(handle,parameters))`和`SetStroke`的包材质已接到真实准备帧／主Render。基础字与注音共用族样式，逐区域最后Set拥有真实实例归属；后续alpha／阴影不能抢走材质错误。参数必须完整匹配固定导出及范围，区域必须被材质声明。透明输出跳过背景依赖时采用包内基础外观并提示。

作者签名／namespace不变，SDK程序集随程序0.13.1；完整1.0.0仍未冻结。`Materials.SetUniform`不是现有API；0.13.1当批直接渐变描边writer仍有门禁，0.13.2接续见下节，包内渐变基础外观可以通过包材质消费。作者无native表面或帧内编译入口。宿主读取真实外观用GetTokenAppearance／GetMarkAppearance；几何DrawingStyle不能作最终颜色，原始颜色捕获拒绝未重组材质。

真实C#包、像素及MP4／Alpha证据见roadmap §18.63／[区域材质](scripting-region-material-contract.md)。完整组图、映射、跨输出／持久事务和工作区仍待实施。


## 0.13.2 直接渐变描边与一次渐变透明度

`Style.SetStroke(TokenFamilyTarget／TextMarkTarget, new StrokeValue(width, MaterialValue.Gradient(value), join))` 已进入准备帧和正式 Render。支持 Linear／Radial、Canvas／Line、LinearRgb／Hsv；外部宽度及 Miter／Round／Bevel 沿用既有合同。基础字与注音使用同一族外观，前奏标记保留真实 Cue／Binding 身份及共同入场 alpha。

支持的渐变是唯一颜色／alpha 来源；普通填充和描边的 shader 色标包含样式透明度一次，画笔使用不透明白色，不再重复乘历史纯色字段或样式 alpha。直接渐变描边允许 Color=null 的真实 Core 描述，不增加伪造基色。未支持组合报告 UnsupportedEffectGradient，原生精度／实际径向退化报告 UnsupportedNativeStyleValues，原生描边 shader 创建失败报告 GradientShaderUnavailable。包材质继续由现有协调器执行。

没有新增公开方法、包编码、依赖或工程字段；程序集随程序0.13.2，schema21／15／3及包profile2保持，完整SDK1.0.0未冻结。整行Style、Materials.SetUniform、组图／父配方、实际Mappings、持久事务和工作区仍待实施。设计及验证见[样式帧合同 §5](scripting-r2-style-frame-contract.md#5-直接渐变描边与一次渐变-alpha0132-实施设计)／roadmap §18.64。


## 0.13.3 材质逐节点 uniform 的实际 API

用户确认图节点 ID＋uniform 名字、显式未唱／已唱／描边和调用顺序清除语义后，新增 `FrameWriter.Materials`。两个重载的 target 分别为 TokenFamilyTarget／TextMarkTarget，不支持整行／组；沿用 Style 能力与真实字簇／目标写权。以下节点与名字须在实际包图中声明：

```csharp
var handle = frame.Resources.GetMaterial("ink");
var material = MaterialValue.PackageExport(handle,
    new ParameterValues([new("amount", new ParameterValue.Number(.5))]));
foreach (var token in frame.Presentations.TokensInBinding)
{
    output.Style.SetSungMaterial(token.WithRuby, material);
    output.Materials.SetUniform(handle, token.WithRuby, MaterialSlot.SungFill,
        "probe", "amount", UniformValue.Float(.2 + token.Performance.Progress.GetValueOrDefault() * .8));
}
```

MaterialSlot 为 UnsungFill／SungFill／Stroke；标记对应 Pending／Elapsed。同一节点／名字后写值覆盖，另一个节点同名值互不影响。每次材质 Set 建立新外观并清除该部位旧覆盖；后续 uniform 只作用于当时匹配的完整句柄。不能用后面的 Set 掩盖前面无效写入。有序操作和 uniform 数值按内容比较，保留跨会话／重放的原有帧相等性。后实例只写 uniform 时必须有同修订资源、目标写权和声明；不读取或改变全实例共同静态基线。

StyleUpdate.MaterialEdits 保存有序的封闭 ReplaceMaterial／Uniform 操作；ApplyMaterials 仅解析材料顺序，alpha 仍在所有实例合并后校验。PackageMaterial.Uniforms 为不可变 GraphUniformValues，以 (nodeId,name) 为键；参数、完整包修订和程序不变。最后材质写入者包含 uniform 写入者，后续 alpha／阴影不改变材质错误归属。

全部节点／名字、七类值／数组、float32 越限及非零下溢在主画布前校验；透明回退也执行协议门禁。正式图节点优先读显式覆盖，再读原始字面量／参数／宿主绑定，帧内不编译 shader。每次求值重建覆盖，不延续上一呈现帧。

本批没有工程／包 JSON 修改；schema21／15／3、package profile2和未冻结SDK合同保持。完整组图／父捕获、映射、整行Style、跨输出执行、持久事务与工作区继续。实际验证见 roadmap §18.65 与[实施合同](scripting-material-uniform-writer-proposal.md)。

## 0.15.0宿主工程投影（作者SDK保持）

Core.ProjectEffects保存固定源码资源包／实例／绑定／订阅／UInt64 seed和规范参数；无SDK或native依赖。Scripting.Compilation.CompiledProjectEffects按包修订实际编译并保持实例执行顺序，失败保留编译器原始行列，不创建部分会话或使用旧DLL。CreateFactory消费当前无音频输入的固定闭包及真实绘制／粒子预算；required音频仍按既有门禁失败，分析修订事务后续接入。GraphBudget提供表面内存限额供组合根消费。Snapshot自动指纹要求prepared frame。纯图／材质直接实例、跨输出计划与GUI仍未交付，见[工程效果合同](scripting-project-effects-contract.md)。

## 0.16.0固定工程音频投影（作者API保持）

宿主使用CompiledProjectEffects.CompileAsync(Project,...)，将已接受的真实完整音频分析投影为原AudioFeatures；GetAt／GetRange、AudioFeatureTick、TimelineHistory及Availability保持。控制器清单outputs.requires可显式包含AudioFeatures，宿主据此设置RequiresAudio；其他未实施requirements仍拒绝。required仅要求初始化前Available，输出音轨外仍OutsideRange／Values=null，optional明确接收NoAudio／NotAnalyzed／AnalysisFailed。作者Error始终使求值失败。

完整分析文件内容进入会话指纹，回退／帧率／中段独立导出读取同一序列；分析值更改不重编译源码。Core源结构与SDK隔离，Media实际分析只桥接已完成结果；存储、版本断代及真实出口证据见[存储合同](scripting-media-analysis-storage-contract.md)／roadmap §18.68。工作区自动分析、跨输出计划和完整SDK冻结继续。


### 0.17.0 本帧图元层读取与模板来源边界

ParticleSample.Layer为宿主本帧实际绘制的OverlayLayer，包含实例／模板覆盖，与该发射器的正式DotBatch层一致。宿主在描述解析前统一覆盖手工圆点、连续发射和Burst，作者无需修改包源码；原层为package时照作者输出。现有发射器批次规则不改，Layer不是出生时固定元数据。包profile2及SDK版本声明保持，增加此只读视图属性未改变七阶段或writer签名。

模板相对配方和templateOrigin属于Core持久源，不把这些可写对象交给作者。作者仍通过原Binding／Parameters和只读ProjectCatalog使用实例；无公开模板库写API。0.17.0当时工坊只有静态预览；0.21.0正式材质试样、后续效果工作区及0.37.0直接图已接通，当前路线见本文首页。见[模板合同](scripting-template-recipes-contract.md)／ADR-0103。


### 0.18.0 Core源材质与作者SDK的边界（M1）

MaterialSpec属于Core持久源（单一solid／gradient／packageMaterial），不把可写源对象交给作者；作者仍使用MaterialValue／MaterialHandle和显式MaterialSlot，节点ID＋uniform名及Set清除覆盖的已确认规则不变。SDK包profile2不改，程序集字节进入编译缓存身份。M1源包材质在编译工程／基线／Render和捕获入口明确报SourceMaterialPreparationPending，尚未成为可查询的最终包基线；既有脚本在帧内Set及SetUniform继续真实路径。M2接通真实源归属和固定资源，不伪造EffectInstanceId或ArtifactHash。见[源材质合同](scripting-source-material-contract.md)／ADR-0104／roadmap§18.70。

### 0.19.0 源材质基线与宿主来源协议（M2）

经完整Project编译得到的PreparedSourceMaterials现已进入BaselineStyles，作者能读取真实PackageMaterial句柄及完整参数。Style.SetUnsungMaterial／SetSungMaterial／SetStroke和Materials.SetUniform沿既有写权及顺序处理；uniform必须显式传MaterialSlot.UnsungFill／SungFill／Stroke与图节点ID＋名字，实际写入后该部位来源才由真实脚本接管，alpha和阴影不改变材质来源。没有新增作者writer或可写源对象。

宿主PreparedMaterialBinding与RenderMaterialDiagnostic改为Owner.Source／Script。Source没有实例／DLL身份，其InstanceId／ArtifactHash为null，错误为SourceMaterialFailureException；Script仍保留真实身份。这属于公开宿主协议的不兼容变更，不能把源材质当作假控制器。TimelineFrameSourceFactory、PureFrameSourceFactory及SemanticAnchorQuery接受固定sourceMaterials；缺失投影或旧指纹明确拒绝。纯资源工程可以没有CompiledFrameEffect或C#源码，仍使用正式准备帧和固定native资源；端点查询不执行材质图。程序／程序集0.19.0，schema26／16／5与包profile2不变；完整SDK仍未冻结。M3材质工作区及其余能力边界见[源材质合同](scripting-source-material-contract.md)／ADR-0105／roadmap§18.71。


### 0.20.0～0.24.0 宿主运行装配和源码工作区（作者API保持）

U0运行组合根已经接通：中立异步会话工厂固定完整快照、当前启用实例、源材质、真实分析及native资源；预览后台串行，导出使用独立会话，作者脚本不在UI线程执行。U1工坊已提供未唱／已唱／描边材质槽位、九类包参数和正式异步试样。U2工作区管理固定包与工程实例，支持完整目录／.mkfx导入和原子分享；新建实例初始禁用。

源码草稿按包修订／文件路径保留，切换文件和包不丢编辑。保存修订合并当前包全部脏C#文件，更新摘要并保留旧闭包；独立编译固定包会执行完整正式资源门禁和Roslyn入口反射，不依赖实例是否引用该包，也不执行作者构造。真实诊断可定位文件行列。编译成功与源码保存都不自动更新实例；显式接受只升级当前同包实例并保留入口、参数、seed、绑定、订阅、启用状态及顺序。

工作区保存先发布所有源码草稿，再一次提交工程效果；失败源码可保存、重开和撤销，正式运行／导出仍检查当前启用引用并拒绝旧DLL回退。保存与“验证工程效果”分开，不把编译失败当持久化失败。编译和诊断属于作者工具，不增加脚本可写源对象、清单编辑API或native入口。

### 0.26.0 宿主运行诊断的实际包来源

新增中立只读 `EffectPackageIdentity(PackageId, Revision, ExportId)`，修订必须为完整小写 sha256。宿主 `CompiledFrameEffect.PackageIdentity` 从固定包／控制器声明取得；`RuntimeDiagnostic.PackageIdentity`、`EffectFailureException.PackageIdentity` 及准备帧的脚本材质／图绑定／组透明度来源继续保留它。真实控制器诊断区分共享 DLL 的不同包、资源修订及导出项；受控独立源码没有包时为 null。

作者仍只通过本阶段 `Diagnostics.Report` 报告严重度、代码、消息和可选逻辑位置，不自行选择宿主包来源。Info／Warning 经准备帧、正式像素帧及缓存／导出记录；Error 即使被捕获也失败，普通异常不伪造作者位置，取消仍保持取消。图／材质句柄的导出与实际控制器 ExportId 分别保留。协议、七阶段及 200 ms 编码失败证据见[运行诊断合同](scripting-runtime-diagnostics-contract.md)／ADR-0119、roadmap §18.86；完整 SDK 与 R5～R8 仍后续。

### 0.26.1 两个可导入的公开文字示例

WordEmphasis／RepeatEmphasis 现在都具有 profile2 完整目录清单、README 和文件摘要，支持工作区导入和 `.mkfx` 分享，源码保持 §6.1／§6.2。重复实例默认参数沿合同 A §8.1，声明元素族视觉变换与样式；计数范围和源订阅独立，重复纯色示例遇到非纯色基线明确失败。真实持久会话、三种 FPS、音频、前奏、重绘／跳转／取消重建及 MP4／Alpha 同帧证据见[会话组合验收](archive/acceptance/scripting-session-combination-acceptance.md)／ADR-0120、roadmap §18.87。

0.26.1当时程序／程序集为0.26.1，schema26／16／5及包profile2保持。运行组合根已完成；本段列出的参数／源码撤销与热替换组合释放、直接图实例、粒子及模板工作区随后已按限定范围接续，跨输出／组图／父捕获／实际映射仍未开放，完整SDK1.0.0仍未冻结。源码工作区实际边界见[源码编辑合同](archive/legacy-ui/scripting-workspace-source-editing-contract.md)／ADR-0110／roadmap §18.76。

0.29.0 的宿主后继／端点接缝与旧事件身份协议兼容，固定转换事件实际进入 OnEvent／历史；同时起唱保留并发边，不选择唯一后继。Bridge 随后在0.30.0接入固定发射时轴，未来端点查询不改变作者当前状态；后继与桥接证据见 roadmap §18.92～18.93。


### 0.41.0 作者工具入口迁移

预设制作和效果编辑已成为主窗口内临时工作区，经“高级”进入。歌词行、所选文字和场景对象的上下文入口携带稳定绑定或精确实例，复用已打开的工作区；切换保留草稿，关闭／换工程／退出明确保存、放弃或取消。源码保存、独立编译、单实例接受和正式验证保持区别。包 profile、源修订、配方与当前 SDK 方法不变；路线见 [工作区指南](archive/legacy-ui/ui-workspace-guide.md)。
