# R2 中立 SDK 与共享文字几何实施记录

> 2026-10-03；**0.12.0，R2 源几何、纯帧生产变换、固定帧服务、事件／订阅／历史、元素基线／Style writer 及共享语义会话／诊断／最终播放头／几何查询、行／组／标记读取、标记生产输出、整行层次仿射及六阶段／圆点输出已实施；R5 基础音频和 R2／R3 固定 shader 原语、中立 CPU 图、生产捕获／图绑定执行适配、共享中间表面账及保留替换范围的合成链／真实背景输入、六类彩色部件和 R4 包内容／九类参数／控制器语义消费及独立包 shader 程序／真实 ABI 门禁已有，完整 R2 出口尚未达成**。
> 工程／歌词／模板 schema 仍为 **21／15／3**；R0 A／B／UI 的已接受合同及 R1 能力结论保持。施工依据为 [R2 路线](../plans/scripting-system-implementation-roadmap.md#r2中立-sdk-与宿主几何)、[公开 SDK 合同](../../scripting-sdk-contract.md)和 ADR-0071。

## 1. 已进入代码的内容

| 范围 | 当前实际实现 |
|---|---|
| 中立身份 | 源／展示／装载／演唱／事件／效果／光标／绘制单元的独立 ordinal ID；默认结构无效，禁止隐式混用；Occurrence／Event 由稳定长度编码导出，不含采样时刻或 UI 序号 |
| 几何／时间 | 有限 Point2、Vector2、Rect2、Affine2；父 × 子组合、有限求逆；整数微秒半开 TimeRange；默认区间不能用作演唱 |
| 完整源目录 | SourceBranch／Line／Token／Ruby、源顺序、时间状态及具名查询；完整目录与当前可见集合分离；不依赖 Core／图形库 |
| 真正源进度 | PerformanceSample 与 FrameToken；复用 LyricSampler，未定时字进度／时长为空且无 OccurrenceId，行进度不复制为字进度 |
| 原生字形绘制单元 | ShapedLine 保存每个完整字簇的固定 glyph ID、行坐标、真实墨迹、字体 ascent／descent；后续绘制和测量直接复用 |
| 字簇关联 | GlyphPartitioner 按完整源区间关联固定单元；跨两个独立元素的 ffi／其他合字明确返回 UnsupportedGlyphPartition，不切 UTF-16 或再次整形 |
| 共享基础几何 | SceneFrame.TokenGeometries：TokenFamilyTarget／BaseTokenTarget、基础 Advance、族联合墨迹、注音单独框及已有最终世界矩阵；GetSceneFrame 和实际绘制来自同一布局 |
| 关联分支装载 | 共用 CreateSample 填写 BindingId，关联翻译不再为空；装载、提前显示与源起唱分别保留；提示标记不制造 TokenId |

0.9.2 的基础上，0.9.3 已加入下列纯帧生产接缝；**尚未提供持久脚本实例、完整生命周期或编辑工作区**。新程序集的更多类型不代表全部 SDK／SCRIPT-01 已完成。

| 0.9.3 新范围 | 当前实际实现 |
|---|---|
| 场景／行装载 | 完整 SceneCatalog（nodes、programs、clips、leadIns）及 FrameLine；固定父树与当前槽位父节点分开，准备／常规／保留阶段明确，前奏不造字 |
| 固定输入 | number／integer／boolean／enum／duration／text 类型值，缺失或类型不符报告键名，长整数微秒不经过浮点；颜色／曲线／资源值继续接入 |
| 当前生命周期子集 | 真正 IKaraokeEffect／EffectBase 下 Initialize／Evaluate；PureFrameSourceFactory 每会话独立模块／实例／后端、串行求值并释放引用；六阶段的其他四个入口尚未加入，不假装历史／模拟已工作 |
| 输出绑定 | 工程／分支／行／元素／片段／节点（可含后代）；完整源和场景目录始终可读；失效／空／重复绑定拒绝，不换到默认范围。源事件订阅仍另行实施 |
| 类型化 writer | 当前仅 Transforms／ScaleTokenFamily；能力、绑定、有限正值、溢出／可逆性和跨源字簇校验；恒等请求不制造变化；结束或失败后 writer 失效 |
| 准备帧 | 中立工厂／source 协议与不可变 PreparedRenderFrame；精确快照引用、时刻、模式和密度门禁；标识启用效果的输入缺帧即错误；缓存先校验并包含帧身份 |
| 生产族变换 | 原行不再绘制被移动的固定单元；同一基础字／注音族执行变换，双色／描边／阴影／Alpha 只沿共享规则绘制，邻字不重排；FinalGeometry 包含同一族矩阵 |
| 错误分类 | EffectFailureException 与 PreparedFrameException 不进入 VideoExporter 普通占位 catch；真实编码中止、临时产物清理、原输出保留。完整包／源位置诊断按 R4～R5 增补 |
| 作者样本 | samples/ScriptEffects/WordEmphasis/src/WordEmphasis.cs 是 SDK §6.1 同一源码；直接 Roslyn 编译并运行生产共享后端，不用测试私有作者入口。当前还不是可导入 `.mkfx` 包 |

0.9.4 补齐 color／curve／asset，参数九类全部可严格读取；ColorRgba 保持直乘，显式曲线不夹值。ResourceCatalog 的材质／图／图片句柄带完整包修订，加载前验证资产参数归属。PCG32-v1 固定编码／核心和中文分流向量，纯索引读取不依赖调用顺序。AudioFeatures 接受固定分析输入，明确区分无音频／未分析／失败、真实静音和半开越界；**没有新增音频分析器或材质执行**。Initialize／FrameContext 接通同一固定服务。具体字节、查询语义与当前边界见 [固定帧服务合同](../../scripting-r2-frame-services-contract.md)。

0.9.5 新增封闭 TimelineEvent／ReplayReason、SourceSubscription、EventRecord／TimelineHistory 和源筛选；宿主 TimelineProjection 从真实源时间与共享显示采样构造稳定事件表，ResolvedSourceSubscription 单独解析源订阅。前奏／静态绘制／槽位提前显示不增加源起唱，准备→演唱不重新装载；历史前缀拒绝未来、重复、乱序及伪造源事件。具体口径见 [事件协议](../../scripting-r2-timeline-protocol.md)。十三类事件合同已声明，当前仅光标转换仍待几何阶段生成；没有接入 OnEvent／120 Hz 会话，不把协议当作 callback 效果已交付。

## 2. 绘制与进度口径

旧引擎先整形完整字体 run，但测墨迹及绘制时又整形各字簇子串。现在以完整字体 run 为单位取得唯一字形结果；多字体回退仍按实际字体分 run，后续不重整形子串。缓存 DrawShapedText 入口使用 positioned text blob，保持原 glyph ID／位置、字体回退、绘制 shader／描边和阴影；**不使用 DrawText(string) 重猜字形**。

该入口与锁定版本的上游最终字形绘制流程一致。[SkiaSharp.HarfBuzz 3.119.4 原始实现](https://github.com/mono/SkiaSharp/blob/v3.119.4/source/SkiaSharp.HarfBuzz/SkiaSharp.HarfBuzz/CanvasExtensions.cs)。真实像素测试逐字节对比完整上游 DrawShapedText：AVATAR、office、组合音标和阿拉伯文均一致。涉及上下文的拉丁合字／连接字现在保持整行结果；旧子串重整形可能改变的轮廓属于按已接受合同修正，不是新增源字体／排版效果。

SDK 的 Performance 是**源演唱状态**。Static／FollowPrimary 分支的原画可按显示策略不做逐字染色，但不能因此抹掉有时间的源元素；FrameProjection 仍调用同一个 LyricSampler 取得真实进度。缺源时间的翻译不会获得假演唱区间、假起唱或假零进度。

Advance 使用实际字体度量；族的缩放枢轴仍取基础字 Advance 中心，联合注音墨迹不移动这个枢轴。基础／注音的逻辑位置乘已有行矩阵一次，包含父组、槽位、单句微调和入场动画。墨迹不含装饰；空白无墨迹时明确为空。

## 3. 自动验证

```bash
dotnet build MakoKaraGenerator.slnx -m:1
dotnet test MakoKaraGenerator.slnx --no-build -m:1
```

全量构建 **0 警告／0 错误**，八个项目 **1563 项通过、0 失败／跳过**。本部分新增 **26 项**：固定字形／分组绘制 11、中立目录／进度／身份／矩阵／静态源语义 10、共享 Token／Ruby 世界几何及前奏装载 5。现有真实 MP4／Alpha、前奏与样式回归继续通过。

证明范围包括：

- 缓存字形与完整上游结果逐字节相同；把探针的元素子串换为 X 不影响固定字形，证明不会依赖子串重整形。
- 同一组 glyph 单元分别绘制后像素等于整行，包含 emoji 回退／字距／组合音标；受控画布缩放一组单元不移动邻字。这里尚不是完整效果 writer／生产缩放验证。
- ffi 全组绑定可独立，拆成 f／f／i 时三者均明确不可独立；静态原画仍可用。
- 前奏→起唱 BindingId／OccurrenceId 连续；准备时有目标文字而未起唱，三个提示不制造源元素；父组／槽位／单句／入场与注音共用矩阵。
- Token／Ruby 墨迹包住真实透明字形像素，基本字 Advance 不被注音联合框替代。

本机日志 `/tmp/makokara-r2-build.log`、`/tmp/makokara-r2-tests.log`；最终结果以本批提交后的实际输出为准，文档不替代测试。

### 3.1 0.9.3 纯帧生产验证

本批新增 **22 项**（Rendering 11、Scripting 8、Export 3）；全量构建 **0 警告／0 错误**，八个项目 **1585 项通过、0 失败／跳过**。证明范围为：

- 真正作者源码在前奏、普通／任意槽位、父组和单句变换下执行；准备时不缩放，首字进度使用源时间，结束后按整数回落时长衔接。
- 单字＋注音的脚本族变换与现有等价父组整体变换**逐字节相同**，覆盖半透明填充、半透明填充＋描边／阴影、透明填充＋外部描边；不会叠原字。
- 只改变中间元素时邻字像素不变；未定时字没有假放大。多实例组合、同帧重绘、往返跳转和独立会话得到相同纯帧结果；这不是 R5 的有状态重放证明。
- 完整目录在九种输出范围／时刻组合中仍完整；失效绑定、跨源 ffi、非法比例／能力／阶段／目标、原生坐标越限明确失败；类型参数复制和超 2^53 的微秒精确性覆盖。
- 相同输入的缓存命中一致，不同准备帧不污染；缺帧、另一快照、时刻、模式、密度和布局编辑混用均在命中前拒绝。
- 真实 FFmpeg 编码下两个效果错误类型均中止，原输出字节保留，临时文件清理；普通后端错误仍按原合同占位并记一帧失败。

当前日志 `/tmp/makokara-r2-frame-build.log`、`/tmp/makokara-r2-frame-tests.log`；三个 focused 日志为同前缀 frame／writer／export-error-focused。不把旧 focused 中间失败当作最终门禁，也不把测试里的构造工厂当作已存在 GUI。

### 3.2 0.9.4 固定帧服务验证

本批新增 Scripting **13 项**，全量构建 **0 警告／0 错误**，八个项目 **1598 项通过、0 失败／跳过**。覆盖九类严格参数、直乘颜色／曲线端点／过冲／非法输入、资源类型／完整修订／重复声明、官方 PCG 核心及独立实现核对的分流向量（含中文、最大种子、全宽索引）、左端音频 hop／半开区间／真实静音／未分析与失败状态。真正 Roslyn 编译入口在初始化、任意顺序纯帧和独立会话读取同一固定服务；资源参数越界在加载前归类失败。日志为 `/tmp/makokara-r2-services-{build,tests,focused}.log`。

### 3.3 0.9.5 固定事件协议验证

本批新增 Scripting **31 项**，全量构建 **0 警告／0 错误**，八个项目 **1629 项通过、0 失败／跳过**。真实半开演唱区间、行依据／缺时间、静态及隐藏分支、前奏三点与跨片段／三槽案例、五种重放原因、音频 hop／取消、历史前缀、订阅／精确注音／纯标记／范围外光标及封闭 SDK 覆盖。装载事件重建的可见集合在所有精确边界及 24／30／60 fps 邻近时刻直接对照生产 LyricSceneSampler；真正 Roslyn 消费者只按真实 TokenStarted／OccurrenceId 去重计数。日志为 `/tmp/makokara-r2-events-{build,tests,focused}.log`。

没有生产画面或 JSON 结构变化；纯帧 source 仍只 Initialize／Evaluate。事件协议、历史查询及重放身份稳定性不能替代有状态会话、callback 像素和粒子／120 Hz 积分证明；后续保持原验收范围。

### 3.4 0.9.6 元素基线与生产样式帧验证

本批新增 Rendering **22 项**、Scripting **9 项**，全量构建 **0 警告／0 错误**，八个项目 **1660 项通过、0 失败／跳过**。BaselineStyles 固定全部源元素的静态继承外观，不受绑定或可见范围影响；MaterialValue 为唯一两态材质描述，Style writer 提供 Set 与透明度乘数、明确装饰关闭及资源／阶段／目标门禁。准备帧在跨实例合并后校验 alpha、消除无变化，并与族缩放共同进入生产字形／注音绘制。

实际 Roslyn 作者调用与静态外观逐字节一致，覆盖颜色、描边、阴影、Alpha 和现有渐变；样式＋缩放等于静态外观＋等价父组变换。前奏／槽位／父组／局部微调／入场、缺时间字、邻字像素与固定几何通过。回归暴露静态矩形覆盖重画邻字阴影及裁掉边缘的问题，静态独立元素覆盖现与脚本共用固定绘制单元；未改变源排版／时间／JSON，原无覆盖画面保持。包材质、渐变描边及尚未支持的渐变组合当前具名失败，不冒充 R3 执行。

具体数值、诊断、像素变化与剩余边界见 [样式帧合同](../../scripting-r2-style-frame-contract.md)。日志 `/tmp/makokara-r2-style-{build,tests,pixels,sdk}.log`；只有最终全量输出是门禁结果。不将本批纯样式输出当作 RepeatEmphasis／120 Hz 重放或可导入包完成。

0.9.6 批次已直接启动当时构建（PID 139833）；日志 `/tmp/makokara-f93b-r2-style.log` 有 JACK／ALSA 设备探测警告，进程存活且未出现托管启动异常。这是当前应用启动记录，不是新脚本 GUI 工作区或音频实际播放的验收。

### 3.5 0.9.7 共享语义会话与诊断验证

本批新增 Scripting **23**、Rendering **10**、Export **1**；全量构建 **0 警告／0 错误**，八个项目 **1694 项通过、0 失败／跳过**（Application 66、Core 840、Export 43、Media 69、Rendering 310、Scripting 103、App 179、Timeline 84）。共享生命周期接通 OnEvent／Advance 和真实前缀历史，同一实例回调计数及 Style／Transforms 求值。固定 120 Hz 网格采用整数商余数，精确事件分割区间；同刻历史逐项前进，分数帧不推进，取消／回退重建，纯帧拒绝有状态入口或历史请求。

同一公开 RepeatEmphasis 作者源码编译进入生产 Skia，第二第三次样式＋缩放与静态外观＋等价父组矩阵逐字节一致；基础字／注音共同变化。24／30／60 fps 与直接中段 Export／往返跳转相同，分支／全工程、隐藏源、绑定外订阅、前奏／槽位旧句和未定时字不误计数均覆盖。首次缩放 1 的恒等输出被省略，不将无变换误判为未接回调。

作者 Error 即使 catch／返回仍失败，保留源码逻辑行列／实例／产物／阶段／时刻；Info／Warning 进入不可变帧，旧 Reporter 失效；各阶段执行异常、取消和所有实例释放失败覆盖。真实脚本中途 Error 进入真实 FFmpeg 后曾覆盖旧目标，本批修复同目录临时发布和等待退出后的清理，原目标字节保留。此前首帧失败证据只覆盖其自身范围，不能据此宣称中途安全。

日志 `/tmp/makokara-r2-session-build.log`、`/tmp/makokara-r2-session-tests.log`，合同和实际代码边界见 [语义会话记录](../../scripting-r2-semantic-session-contract.md)／ADR-0072。此处 Export 会话与编码为受控注入，尚不读取持久脚本实例，不是 GUI 工作区交付。已直接启动 0.9.7 当前构建，PID **186388**，日志 `/tmp/makokara-f93b-r2-session.log` 显示版本 0.9.7，未出现托管启动异常。niri 已确认同 PID 的 0.9.7 主窗口及「工程版本不一致喵」提示窗口；这是启动恢复旧工程触发现行精确门禁，不转换旧文件。此记录不代替实际脚本工作区或音频播放验收。

### 3.6 0.9.8 播放头与查询验证

本批新增 Rendering **16**、Scripting **6**；全量构建 **0 警告／0 错误**，八个项目 **1716 项通过、0 失败／跳过**（Application 66、Core 840、Export 43、Media 69、Rendering 326、Scripting 109、App 179、Timeline 84）。生产字形、布局和换色像素保持原规则；高亮插值与光标路径共用同一浮点计算。LineGeometrySample 包含无逐字时间的完整行，不靠元素均分宽度。PreparedRenderFrame.Playback 保存最终矩阵下的全部真实 Current 光标；行依据没有假元素，空档／缺时间不造光标，后端未提供几何明确不可用。

真实 CJK／拉丁换色边界像素、字距／空白／共享合字、Emoji 完整字簇、前奏／任意槽位／父组／微调／族缩放／注音、整行依据及合法并发装载均覆盖。彩色 Emoji 有固有颜色，其验证使用真实整形边界和完整字簇，不使用纯色字的换色像素判据。

真正 Roslyn 作者在 Initialize／OnEvent／Advance／Evaluate 使用阶段限定 GeometryQueries，查询完整源且不受输出绑定限制；未来基本布局不调用作者／派发事件／绘制背景。宿主 SemanticAnchorQuery 独立 AnchorQuery 会话重放共享状态及文字求值，24／30／60 fps Export 的同刻端点一致，主计数和变换不受未来查询影响。NextStart 用真实 StartUs；PreviousEndLimit 用整数 EndUs-1，不伪造进度一。不可见装载明确不可用，Error／取消／错误源／过期通道明确失败。

AnchorMap.Affine／AxisWave 已有有限有界数学值、正逆及切线；组合次序、域外／不可逆／溢出和极长时间浮点舍入有测试。**生产 Mappings writer／MappedWarp 尚未执行**，不能把这组数学类型算作图像映射完成。当前不生成 CursorTransition 后继，重叠接续规则已问用户，答复前不选默认。

日志 `/tmp/makokara-r2-geometry-build.log`、`/tmp/makokara-r2-geometry-tests.log`；公开接缝和时间边界见 [播放头实施合同](../../scripting-r2-playback-geometry-contract.md)。当前没有新增脚本 UI 或粒子入口，R2～R8 保持原范围。

已直接启动 0.9.8 当前构建，PID **204283**，日志 `/tmp/makokara-f93b-r2-geometry.log` 显示版本 0.9.8，未出现启动异常。niri 确认主窗口及精确版本不一致提示；旧工程未转换。当前启动不代替尚未交付的脚本工作区／粒子验收。

### 3.7 0.9.9 场景目标与标记生产验证

本批新增 Rendering **25**、Scripting **7**；全量构建 **0 警告／0 错误**，八个项目 **1748 项通过、0 失败／跳过**（Application 66、Core 840、Export 43、Media 69、Rendering 351、Scripting 116、App 179、Timeline 84）。用户确认组仅 alpha＋变换／渲染图，固化为 ADR-0073；完整源行／真实组／标记基线、FrameMark／FrameGroup、固定标记 glyph 和组参考框／矩阵／枢轴／后代墨迹进入 SDK。无元素行仍有继承基线，组不提供假填充，空白标记保留 Advance 而无墨迹；GroupGeometry 不将参考框作为墨迹。

真正 Roslyn MarkProbe 直接消费公开帧／基线和四阶段基本查询接缝；当前测试在 Evaluate 查询未来另一片段的标记／组祖先，读取不授予写权限。标记目标包含 Node／Cue／真实 Binding，同一节点的两次前奏保持不同装载。生产 Style／ApplyAffine 两种附着、两态颜色、渐变、外部描边／阴影／alpha 和仿射与等价静态编辑逐字节相同；入场／父组／槽位／局部微调和动态附着透明度各一次，源数据保持引用身份。多个实例的 Set／最终 alpha、纯重绘／后退、基线无变化及不同会话覆盖。

全部输出范围按真实目标关联：Tokens 不写整套提示，Node 使用运行父树，固定位置提示不被附着目标文字的 Node 绑定误选。缺失／重复／旧 Cue 几何、错误装载、阶段／能力、非法 alpha、包材质尚未实现和原生超限均明确拒绝；既有真实 MP4／Alpha、Error 阻止导出及原输出保护回归继续通过。当前没有 GUI 脚本入口或材质包执行；行／组／BaseOnly 其余输出、capture alpha 和两个视觉阶段仍待实施。

日志 `/tmp/makokara-r2-target-build.log`、`/tmp/makokara-r2-target-tests.log`；定向日志同前缀 target-focused／target-sdk-focused（首次 25＋6，后补行继承验证由最终全量覆盖）。精确目标及阶段合同见 [场景目标合同](../../scripting-r2-scene-target-contract.md)。此批无效果画面保持现有共享绘制规则，R2～R8 原范围保持，性能基准后置。

已直接启动 0.9.9 当前构建，PID **289570**，日志 `/tmp/makokara-f93b-r2-target.log` 显示版本 0.9.9，未见启动异常；niri 确认当前主窗口和精确版本提示。当前 SDK 生产接缝验证不代替尚未交付的脚本工作区验收。

### 3.8 0.9.10 整行仿射与运行父树验证

本批新增 Rendering **18**、Scripting **4**；全量构建 **0 警告／0 错误**，八个项目 **1770 项通过、0 失败／跳过**（Application 66、Core 840、Export 43、Media 69、Rendering 369、Scripting 120、App 179、Timeline 84）。`Transforms.ApplyAffine(LineTarget, Affine2)` 按真实 LineId／BindingId 和能力／阶段授予权限；Project／Branch／Lines／Clips 与实际运行 Node 范围可写整行，Tokens 可读所属行而不能写整行。调用和实例顺序右乘，恒等／抵消保留原帧。

`SceneAffineProjection` 在运行父树中传播矩阵，基础字体布局、槽位、单句微调和入场不重算；附着目标句的提示组及其标记跟随，FixedParent 提示和独立翻译仍按自己的实际父关系。`PreparedRenderFrame.Scene` 保留基础帧，`HierarchyScene` 用于实际绘制；FinalLineGeometry／Token／Ruby／Mark／Group 和 Playback 共用最终层次矩阵，独立族与标记局部输出再各乘一次。组参考框保持，墨迹使用真实最终固定单元，不用参考框替代。

真正 Roslyn LineProbe 在准备／演唱、三槽／普通行、旋转父组／单句微调／入场和两种前奏附着下编译执行。整行移动与保持字形逻辑起点的等价父组移动**逐字节像素相同**；固定提示补偿父组移动后保持世界位置。整行＋族缩放＋标记移动再与独立静态整体移动＋局部输出逐字节对照，验证不会重复组合。原单句位置平移会重算字形起点而发生亚像素取整差异，因此不拿它作为固定字形矩阵的逐字节基准；几何和真实播放头仍由最终矩阵断言。

无逐字时间的整行依据、拆分 ffi 共享合字、实际片段准备装载、未来 AnchorQuery、非均匀仿射／多实例顺序、重绘／后退／独立会话和源引用保持均覆盖。旧 Binding、重复输出、缺几何、循环父树、阶段／能力／范围和组合数值越限明确失败。原生下溢回归先复现 double 可逆、float 矩阵退化却无异常，随后增加实际 Skia 逆矩阵有限性门禁；巨大平移与极小缩放均阻止绘制。既有真实 MP4／Alpha、Error 诊断／中途编码失败及原输出保护回归继续通过。

最终日志 `/tmp/makokara-r2-hierarchy-build.log`、`/tmp/makokara-r2-hierarchy-tests.log`；原生问题复现日志 `/tmp/makokara-r2-hierarchy-native-red.log` 仅是修复前证据。合同见 [整行与层次仿射](../../scripting-r2-hierarchy-output-contract.md)／ADR-0074；生产 JSON 和 schema 21／15／3 保持，SDK 缓存随实际程序集字节断代。组 writer／capture、整行 Style、两个视觉阶段及全部 R3～R8 继续保留；没有新增 GUI 脚本入口。

已直接启动 0.9.10 当前构建，PID **317207**，日志 `/tmp/makokara-f93b-r2-hierarchy.log` 显示版本 0.9.10，未见启动异常；niri 确认同 PID 的主窗口和「工程版本不一致喵」窗口。版本提示来自旧工程恢复的精确门禁，未转换旧文件；启动记录不代替未交付脚本工作区的人工验收。

### 3.9 0.9.11 视觉阶段与生产圆点验证

本批新增 Rendering **21**、Scripting **4**、Export **2**；全量构建 **0 警告／0 错误**，八个项目 **1797 项通过、0 失败／跳过**（Application 66、Core 840、Export 45、Media 69、Rendering 390、Scripting 124、App 179、Timeline 84）。同一实例增加 AdvanceVisuals／EvaluateVisuals；每个固定边界先共享推进／事件，再全实例文字求值和最终几何，再视觉推进／呈现。0 无零长推进，分数帧／暂停重绘不积分或派发事件；输出模式／密度变化重建，原因 InputsChanged。

VisualStateContext 保存前一／当前最终几何与明确左／右历史前缀，VisualFrameContext 提供当前／最近边界几何、分数时长及同一 Playback。几何按实际绑定筛选，Tokens 不获得行依据的假光标；组仍只读。纯 EvaluateVisuals 可用，纯工厂明确拒绝有状态 AdvanceVisuals；无历史和不可用后端不伪造空服务。独立 AnchorQuery 固定跳过两个视觉阶段及 overlay，不触发未来粒子／视觉 Error。

Overlay.DrawDots 提交真实逻辑画布圆点，能力／阶段／批次／数组／半径和全实例预算校验；初始 100,000 项按已接受 B，固定工厂可明确改预算，R4 才持久化。批次按实例顺序及 ordinal ID，数组内部有序。PreparedRenderFrame 附加输出时保留源、文字、最终几何和 Playback 闭包；Skia 实际在背景后／文字前与全部文字／提示后绘制两个层级，alpha 只预乘一次，原生越限／下溢明确失败。

真实 Roslyn 作者验证六阶段在同一对象、120 Hz＋精确事件次序和最终族／注音／播放头；24／30／60 fps、中段直接请求／回退／独立会话得到同一实际画面。独立 Skia 绘制逐字节对照覆盖透明 alpha、等比／非等比密度和多实例叠加，前后层级遮挡明确；无逐字元素的行依据、范围外演唱、明确不可用及三槽前奏两种附着矩阵覆盖。两个视觉阶段的 Error、异常／取消、旧 writer、预算／能力及准备帧失败通过；真实编码中途 Error 保留旧输出并清理临时产物。

最终日志 `/tmp/makokara-r2-visual-build.log`、`/tmp/makokara-r2-visual-tests.log`，规则和代码入口见 [视觉阶段合同](../../scripting-r2-visual-stages-contract.md)／ADR-0075。当前只有实际圆点输出，不提供假 Particles／EmissionSchedule；Emitters／Sprites／Trails／宿主管理出生／Burst／桥接按 R6 继续，通用图按 R3，持久包／音频分析／工作区按 R4～R8；没有新增 GUI 脚本入口。schema 21／15／3 保持，SDK 按实际程序集字节断代，完整 SDK 1.0.0 未冻结。

已直接启动 0.9.11 当前构建，PID **344380**，日志 `/tmp/makokara-f93b-r2-visual.log` 显示版本 0.9.11，未见启动异常；niri 确认同 PID 的主窗口及「工程版本不一致喵」提示。版本提示来自旧工程恢复的精确门禁，未转换旧文件；本批没有新增脚本 GUI，启动记录不代替其人工验收。

### 3.10 0.9.12 独立基础音频验证

真实离线 48 kHz／20 ms 分析、内容修订、节奏／低频和初始化 required 门禁已完成。新增 Media 18、Rendering 9，八项目 1824 项通过，0 警告／错误；真实解码／取消、四种采样率、静音／持续音／纯高音、规则／不规则脉冲及公开作者的重放／像素验证见 [音频合同](../../scripting-audio-analysis-contract.md)、roadmap §18.41 和 ADR-0076。音轨外 required 策略待答；分析持久闭包和 UI 没有完成。

### 3.11 0.9.13 固定 shader 与帧 uniform

按已接受 R0 §6.2／§8 将声明和校验放入 Core，SDK 提供七类有限值及定长数组；既有 Rendering.Skia 一次编译固定 SkSL，真实原生反射对照清单，拒绝同字节数错误类型、color／数组标记和长度不符。每次创建完整的新帧值，矩阵按行输入／按列打包，128 标量及实际 float32 越限校验；明确借用 child 与拥有的 effect／返回 shader，内容身份覆盖实际后端版本。

真实 Roslyn 作者能离线编译 SDK 帧值，正式后端实际执行全部类型／数组、非对称矩阵、直乘颜色与 raw coverage。原生查询直接复制 std::string_view，名称的两个槽与生成的 C 头描述不同；专项测试先暴露差异，适配器复用公共名字查询而不解释这两个槽。数组、字节数相同的矩阵／向量、颜色编码、值集完整性、取消、预算、释放及已创建 shader 的独立引用均验证。详细证据与完整资源释放限制见 [shader 合同](../../scripting-shader-program-contract.md) §5。

没有 PackageMaterial 解析、Materials／ImageEffects／Mappings writer 或完整图执行，不把后端 shader 原语当作生产歌词材质已经可用；生产 JSON 和 schema 21／15／3 保持，完整 R2／R3～R8 仍未完成。

新增 Core 10、Scripting 3、Rendering 26；全量构建 0 警告／0 错误，八项目 **1863 项通过、0 失败／跳过**。日志 `/tmp/makokara-shader-build.log`、`/tmp/makokara-shader-tests.log`；详见 roadmap §18.42／ADR-0077。

### 3.12 0.9.14 中立图与严格 CPU 执行

按已接受 R0 将不可变图／所属参数约束／拓扑和端口校验放入现有 Core；正式 Skia 固定编译并执行 RuntimeShader／Blur／Mask／Transform／Blend，不在帧内编译源码。完整 uniform／child、参数最大范围／累计外扩、实际 sRGB 预乘字节和 Canvas 密度／逻辑矩阵校验；替换合成不重复乘覆盖，透明 explicit bypass 不绕过编译错误，后续独立图照常执行。

借用输入、返回产物及最后使用释放明确，单次图预算在分配前预估，输入／合成按 native 身份去重；Blur 保守 scratch 及内部终结器限制不构成全局资源已完成。当前 Canvas 以外适配、生产文字／父组 capture／组 alpha、其余目标 writer、材质包与真实实例错误门禁仍未接通；optional 缺失规则已询问且未默认选择。

新增 Core 10、Rendering 20、Export 2，全量 1895 项通过，0 警告／错误。真实节点、多密度、半透明替换、严格预算、失败／取消／释放及 MP4／ProRes 4444 同帧通过，非预乘红灯和两类变异确认测试能检出问题。规则、样本范围及剩余见 [图合同](../../scripting-image-graph-contract.md)／ADR-0078，日志见 roadmap §18.43；schema 21／15／3 保持，完整 R2～R8 仍未完成。


已直接启动最终 0.9.14 构建，PID **585153**，日志 `/tmp/makokara-f93b-graph-final.log` 显示版本 0.9.14、未见启动异常；niri 确认同 PID 的主窗口及「工程版本不一致喵」提示。提示是旧工程恢复的精确门禁，不转换旧数据；本批没有新增脚本 GUI，启动不代替其人工验收。

### 3.13 0.9.15 生产固定字形捕获与真实图绑定适配

正式绘制入口共用 CapturePaint，真实行、元素族、提示标记和组的 TargetColor／Glyph／Stroke／Sung／Region 使用固定字形、最终父树及族／标记矩阵；显式配方和 Canvas 框，没有重整形或影子源。TargetColor 与生产 Render 逐字节一致，包括缩小和非等比密度、注音和入场。原始覆盖忽略外观／入场 alpha，外部描边用同一白色字形减去内部；未定时字及其注音不借后字变为已唱。共享源覆盖的旧颜色矩形限制保持，新 raw 外扩不截 64 px。

用户确认同实例同目标最后一次 ImageEffects.Bind，跨实例串接。SDK 限时 writer 复用真实装载／独立分区／能力和固定包修订；中立帧保留实例、编译代次和视觉输出复制闭包。ExecuteSceneImageEffect 只接受本帧绑定，固定 native 图在帧外关联，真实捕获送入图，失败附加 EffectFailureException；自建捕获与外部借用生命周期分开，上一实例产物可作为下一 TargetColor。活跃输入复用并在分配前检查局部预算。

全量 build 0 警告／0 错误，八项目 **1922 项通过、0 失败／跳过**；新增 Rendering 21、Scripting 3、Export 3。真实 Roslyn 行／族／标记最后绑定及跨实例链、真实父树／前奏、共享合字、组一次 alpha 与逐后代 alpha 的区别、取消／预算／资源／旧装载通过。真实歌词与注音 MP4／ProRes 4444 同帧 RGB 平均误差小于 9、alpha 每像素误差不超过 3；真实图错误中止编码、保留旧输出。最后绑定、白色 raw mask 和 64 px 截断三个变异均被测试检出并恢复。日志 `/tmp/makokara-capture-build.log`、`/tmp/makokara-capture-tests-full.log`；详情见 [生产捕获合同](../../scripting-production-capture-contract.md)／ADR-0079／roadmap §18.44。

没有主画布自动图调度、真实媒体／下层生产、子图到父图与全局资源账本、包材质或脚本 UI；直接 Render 遇图绑定明确失败，不忽略它输出无效果成片。组写权及既有五项决策仍待答，完整 R2／R3～R8 保持原范围，schema 21／15／3 不变。


已直接启动最终 0.9.15 构建，PID **611937**，日志 `/tmp/makokara-f93b-capture-final.log` 显示版本 0.9.15；niri 确认同 PID 的主窗口（窗口 113，演示工程）。未见托管异常，但日志有 JACK／ALSA 音频设备探测警告，未单独进行音频播放人工验收；不把该提示当作已验证的播放正常或程序失败。本批未新增脚本 GUI，窗口启动不代替完整脚本人工验收。

### 3.14 0.9.16 中间表面会话／帧账与真实预算失败

按已接受 R0 §8，在现有正式 Skia 后端加入固定会话／帧账；每帧活动数量与会话字节独立于单图限制。捕获产物、图节点和合成持有预留至 Dispose，返回结果不提前减账；输入别名按 native 句柄去重，跨帧共享存储只增加新帧活动数，不重复会话字节。图预检包括上一实例及其他目标仍活跃的产物，Blur 和显式捕获 SaveLayer 预留已计峰值。

全量 build 0 警告／0 错误；八项目 **1938 项通过、0 失败／跳过**（新增 Rendering 15、Export 1）。真实捕获／图／合成在等比／非等密度连续执行后归零，透明旁路保有借用计费，关闭帧／会话不夺取产物，跨图／临时层／取消／坏 shader 的失败与释放通过。实际 C# 作者在 200 ms 增加图外扩，预算错误成为真实实例失败，中止编码、保留旧输出并回收受管表面。三个变异（忽略当前占用、漏显式层、漏 Blur scratch）均以测试红灯检出并恢复。日志 `/tmp/makokara-surface-build-final.log`、`/tmp/makokara-surface-tests-full.log`，规则及证据见 [账本合同](../../scripting-surface-budget-contract.md)／ADR-0080／roadmap §18.45。

没有新 JSON 或写权限变化，schema 21／15／3 保持。账本基础不等于主画布协调器或全部 native RSS 出口；媒体／下层、子图进入父捕获、材质包、工作区及完整 R2～R8 继续实施，已有五项行为不默认选择。


已直接启动最终 0.9.16 构建，PID **27363**；niri 确认窗口 14，标题和日志版本正确。日志 `/tmp/makokara-f93b-surface-final.log` 未见托管异常，有 JACK／ALSA 音频设备探测警告；未逐项验收音频播放，本批没有新增脚本 GUI。

### 3.15 0.9.17 保留替换范围的合成链与真实背景输入

用户确认每个图的合成规则生效、后续普通图保留早期替换范围；同时确认捕获排除的注音／描边／阴影应保持原绘制。CompiledImageGraph.CompositeChain 记录已执行替换 raw 覆盖，max 并集不重复扣抗锯齿边缘；最后 SourceOver／旁路仍保留已有范围，颜色不再乘覆盖，opacity 同时控制颜色与范围。绑定级合成要求本帧同一目标的完整顺序，错误带最后实际实例／代次／阶段／时刻。

CaptureSceneBackground 与正常 Render 共用真实媒体解析／时刻／适配，媒体与工程底色分开，画布外透明。真实绑定自动捕获未提供的媒体／底色，来源别名复用；透明依赖只旁路 TargetColor、不解析媒体，缺失媒体不伪造图片。背景和目标复用同一捕获表面构建、实际密度、sRGB／像素对齐及共享账，原生产目标捕获回归保持。

全量构建 **0 警告／0 错误**；八项目 **1969 项通过、0 失败／跳过**（新增 Rendering 28／Export 3）。多密度、半透明范围、三个替换并集、透明旁路、跨帧／模式门禁、历史覆盖外框、并集／合成峰值及回收、真实 PNG 分层与普通 Render 逐字节、实际作者绑定完整顺序及媒体来源去重通过。真实 C# 双实例、工程图片、歌词／注音／描边的 MP4／ProRes 4444 第 15 帧与同一链预览相符；RGB 平均误差 <9、透明 alpha 每像素误差 ≤3。合成错误在 200 ms 附最后实例停止编码、保护已有成片并回收整链。丢历史范围、叠加 max 覆盖、媒体混入底色三项变异实际失败后恢复。

最终日志 `/tmp/makokara-chain-build-final.log`、`/tmp/makokara-chain-tests-full.log`；变异 `/tmp/makokara-chain-range-mutation.log`、`/tmp/makokara-chain-union-mutation.log`、`/tmp/makokara-chain-media-mutation.log`。规则见 [场景合成合同](../../scripting-scene-composition-contract.md)／ADR-0081／roadmap §18.46。

**正式父子 Render 调度、排除部件保留绘制、混合配方重组及真实 BackdropBelow 未实施**；当前编码为显式单目标宿主接缝，不作为完整场景出口。包材质、组权限及原五项待答、完整 R2～R8 保持；schema 21／15／3 不变。



已直接启动最终 0.9.17 构建，PID **55967**；niri 确认窗口 23，标题与日志版本一致。日志 `/tmp/makokara-f93b-chain-final.log` 未见托管异常，有 JACK／ALSA 音频设备探测警告；未单独验收音频播放，本批没有新增脚本工作区 UI。

### 3.16 0.9.18 真实彩色部件及显式重组

CaptureSceneColorPart 按固定字形／最终矩阵提供基础字和注音的填充／外描边／真实阴影，保留两态颜色／渐变、装饰及局部外观 alpha；共同入场／标记 alpha 单独返回生产量化值，不逐部件烘入。组没有自己的字形，真实 Line／TokenFamily／Mark 及共享合字门禁沿用原入口；两种前奏附着及实际脚本样式／族变换保持。

AssembleSceneColorParts 要求同一源／准备帧／目标／时刻／输出／质量和显式顺序，不重复部件或裁掉捕获框；返回独立颜色表面及尚未应用的共同 alpha。输入借用、输出和临时捕获层进入共享账，局部峰值在分配前校验，释放输入不使产物失效。原生产绘制按职责提取到 TextPainting partial；无部件路径的行为保持。

新增 Rendering 25 项；全量构建 **0 警告／0 错误**，八项目 **1994 项通过、0 失败／跳过**（Rendering 534）。三密度六类真实颜色、渐变、逐字裁切／缺时间、真实样式／变换、共享合字和前奏、共同 alpha、身份／预算／取消／释放通过。指定顺序重组与完整生产捕获每通道差 ≤2；逐部件 alpha 明确不同。四项变异分别破坏入场分离、注音分区、shadow-only 和重组原颜色，均真实失败后恢复。

最终全量日志 `/tmp/makokara-parts-build-final.log`、`/tmp/makokara-parts-tests-full.log`；具体规则及变异日志见 [部件合同](../../scripting-color-parts-contract.md)／ADR-0082／roadmap §18.47。本批不宣称任意多源单元交错顺序逐像素等价，不提供默认效果叠放顺序；该项已询问用户。混合配方自动重组、父子 Render 协调器、真实 BackdropBelow 和完整 R2～R8 仍须实施，schema 21／15／3 保持。

已直接启动最终 0.9.18 构建，PID **86794**；niri 确认窗口 36，标题和日志版本一致。日志 `/tmp/makokara-f93b-parts-final.log` 未见托管启动异常；niri 同时显示「工程版本不一致喵」窗口 37，为恢复旧版本工程触发现行精确门禁，不转换或修改旧文件。本批未增加脚本 UI，未逐项人工验收音频播放。

### 3.17 0.9.19 R4 独立包内容闭包与离线源码消费

Core 的 EffectPackageContents 固定完整规范清单及不可变文件字节，校验实际 SHA-256、严格 UTF-8、顶层／files、版本／profile、逐路径段大小写和文件／目录前缀；规范对象排序保留数组与数字词形、拒绝嵌套重复键，RevisionHash 无机器路径或运行缓存。Application 的目录／ZIP 读取获得同一内容集合，链接不跟随、不先解压；分享完整写入后一次发布，实际写入取消及失败保留原目标并清理临时文件。

Scripting 将固定 csharp 内容传给现有离线 Roslyn。真实分享归档移走原目录后仍编译／加载 IKaraokeEffect 并产生指定 1.5 倍 SDK 元素族变换；资源修改改变 RevisionHash，但同 C# 的 ArtifactHash／DLL 相同。错误源码保留包内文件／行列，无 DLL；无源码不伪造控制器。

本批是文件闭包，参数／导出、输出依赖／图／材质语义仍须解析并验证，不能直接启用；ResourceCatalog 未由此自动扩权。工程／歌词／模板内容地址事务及 UI 未改，schema 21／15／3 保持，整个 R2～R8 未完成。新增 Application **11**、Core **25**、Scripting **3**；全量构建 **0 警告／0 错误**，八项目 **2033 项通过、0 失败／跳过**（77／885／54／87／534／133／179／84）。三项变异破坏真实摘要、规范属性排序与原子分享，均被实际回归检出后恢复；日志和完整验证见 [内容合同](../../scripting-package-content-contract.md)／ADR-0083／roadmap §18.48。

已直接启动最终 0.9.19 构建，PID **113225**；niri 确认窗口 52，标题及日志版本一致。日志 `/tmp/makokara-f93b-pack-content-final.log` 未见托管启动异常，有 JACK／ALSA 设备探测警告；未逐项人工验收音频播放，本批无新增脚本 UI。


### 3.18 0.9.20 R4 参数／控制器与实际包消费

EffectControllerPackage 一次读取九类元数据／默认、控制器允许绑定及输出依赖；ResolveParameters 对默认和覆盖共用严格检查。用户确认颜色直乘 double 对象、三类曲线及本包 role:image 路径，资源仍投影为固定 SDK 句柄。实际后端完整解码先于目录建立；读取前执行 R0 展开预算，规范后的闭包也检查。

CompiledControllerPackage 离线编译固定源码和入口集合，验证全部公共普通类／SDK／无参入口而不运行作者构造；CreateEffect 按清单入口／参数及允许绑定进入原纯帧／语义工厂。实际合成前检查声明外目标类别，视觉批次须有声明 OutputId，不能用粗粒度能力扩权。未实施的 writer、非空 requires 和跨输出执行明确失败；混合包的材质／图／旁路未完整解析时拒绝整包。

真实 WordEmphasis 包已带完整摘要清单，在两种密度验证默认／覆盖确实改变像素，注音共同矩阵和原排版保持；九类值真实 Initialize、错误入口／不运行构造、图片完整像素／容量及声明外输出门禁通过。新增 Application 3、Core 47、Rendering 9、Scripting 14；全量构建 0 警告／0 错误，八项目 2106 项通过（80／932／54／87／543／147／179／84）。实际边界及日志见 [包语义合同](../../scripting-package-semantics-contract.md)／ADR-0084／roadmap §18.49；工程持久事务、材质／图与依赖执行、工作区及完整 R2～R8 仍未完成。

已直接启动最终 **0.9.20** 构建，PID **71976**；niri 确认窗口 **23**，标题及日志版本一致。日志 `/tmp/makokara-f93b-pack-semantics-final-verified.log` 未见托管启动异常，有 JACK／ALSA 设备探测警告；未逐项人工验收音频播放，本批无新增脚本工作区 UI。

### 3.19 0.9.21 R4 独立 shader 程序及真实启用门禁

用户确认补充合同 A，类型声明放在独立 role:shaderProgram 文件，图引用程序路径；实际程序源码须来自同闭包 role:shader。Core 读取全部固定程序，包括未使用声明，七类类型／数组及 child、名称／严格字段／版本／profile 检查沿用唯一模型；九类参数和严格 JSON 读取提取为内部共用代码。Rendering.Abstractions 注入 IPackageShaderValidator，正式 Skia 对全部资源编译／反射后才继续控制器 DLL；没有验证器或坏程序不能部分启用。

CompiledPackageShaderPrograms 持有固定编译集合，逐帧只创建带新值的真实 shader，不重编译。释放／取消／中途失败回收集合，已经返回 shader 的原生引用独立有效；不把程序文件冒充 SDK GraphHandle。实际 0.25／0.75 值改变 CPU 像素、包资源修订和程序键改变而同 C# Artifact 保持，真实目录／归档往返通过。新增 Application 1、Core 20、Rendering 9；全量构建 0 警告／0 错误，八项目 2136 项通过（81／952／54／87／552／147／179／84）。日志及实际边界见 [补充合同 §6](../../scripting-package-graph-semantics-contract.md#6-a-的实际实施路径0921)／ADR-0085／roadmap §18.50。

0.9.21 本批时 B／C 尚未确认，因此未实施；随后 B 获确认并按下节接续，C 仍待答；主画布协调器、完整 native 资源出口、工程事务、其余 writer／粒子／工作区及整个 R2～R8 继续。最终 0.9.21 已启动，PID 136774／niri 窗口 46，日志 `/tmp/makokara-f93b-pack-shaders-final.log` 未见托管异常，有 JACK／ALSA 探测警告；未逐项人工验收音频或脚本 UI。

## 4. R2 尚需完成的出口

1. 封闭事件、源订阅及 TimelineHistory 已进入代码，六阶段与诊断、最终文字／播放头和生产圆点已接通；行／组／标记基线及真实目标已接入。九类参数、资源逻辑目录／随机服务与固定音频输入已接通；基础 PCM 分析及初始化 required 门禁在 0.9.12 已实施，见 [音频合同](../../scripting-audio-analysis-contract.md)；真实包解析及分析持久闭包按 R4～R5。音轨外 required 策略待答。发射／粒子服务仍按 R6，六个入口不代表最终 SDK 冻结。
2. 元素族 Style、两态 MaterialValue 和装饰值已接入；固定 shader 声明、uniform 帧值和后端严格编译／CPU 执行原语及独立五类操作图执行已实施，ImageEffects 中立绑定、阶段／目标校验及真实执行适配已实施，仍需其主画布调度及 Materials／Mappings writer；标记 Style／仿射、整行仿射与实际运行父树传播已生产绘制，0.11.1 已补真实组仿射及明确组写权限；0.12.2 已补组 alpha；整行 Style／组图 writer 与输出依赖仍须接通。整行与元素同属性覆盖顺序仍待答；仅工程／显式节点可写组已确认；组 alpha 必须真实隔离合成。R3 再执行完整材质／图，R4～R5 将中立帧工厂接到持久实例与预览／导出独立会话。
3. 生产元素族缩放和真实 WordEmphasis 最小作者路径已验证；保持双色／注音／装饰证明，后续输出不能使 glyph 重整形或原字重影。
4. 最终 Line／Token／Ruby／Mark／Group 几何及真实播放头、typed 映射值、基础／独立未来查询已有；CursorTransition 后继投影及实际 MappedWarp 仍须完成。重叠接续待用户答复；未来查询不能推进主会话或递归绘制背景／粒子。
5. 公开 SDK 类型清单／引用库存及完整作者入口继续验证；RepeatEmphasis 同一源码及重放／像素已验证；CursorTrail 的作者源码按 R6 编译执行，六包／UI／成片按 R8 验收。

**整个 R2 仍未完成，R3～R8 的原范围保持**；性能基准后置。重叠句的光标接续、行／元素样式覆盖顺序、音轨外 required 及 optional 图输入缺失策略为当前待答决策；组写权、包编码 C 和保留部件最高层叠放已确认（ADR-0090／0091）；不重复询问已接受合同。

### 3.20 0.9.22 R4 包图编码与逐输入捕获／真实图片

用户确认补充合同 B；完整固定图目录按文件路径建立，不解析 C 未确认的混合导出／材质回退。五类节点、类型化 ABI 字面量／封闭参数／宿主、严格字段、采样偏移／外扩、矩阵／输出框、role:image 路径及旁路由唯一 Core 编译器校验。控制器保留全部图计划，实际 CPU 门禁验证所有图，缺少验证器拒绝；ResourceCatalog 不扩权。

真实捕获按输入自己的完整配方和来源复用，注音差分逐字节符合独立捕获；包图片完整解码并显式指定原始尺寸／密度，通过 Transform 移动，不默认铺满。尚未接入 owned overlay／材质区域的捕获和逐部件跨图重组，明确失败；上一个整体产物不能冒充全部输入。Canvas 之外声明保留，但当前实际执行报不支持；optional 缺失及部件叠放待答，未选择默认。

新增 Application 1、Core 23、Rendering 9、Export 2；全量构建 0 警告／0 错误，八项目 2171 项通过（82／975／56／87／561／147／179／84）。真实目录／.mkfx、CPU 像素／图片变换／透明原输入、生产注音差分、取消／释放及真实 MP4／Alpha 同帧通过；规则见 [补充合同 §8](../../scripting-package-graph-semantics-contract.md#8-b-的实际实施路径0922)／ADR-0086／roadmap §18.51。完整 R2～R8、C、主画布及工程持久事务保持未完成，性能基准后置。

### 3.21 0.9.23 连续粒子与真实出生几何

用户确认同一绑定的每条合法头分别发射 rate。SDK Emitters／完整 VisualOutput、解析 ParticleMotion／年龄曲线、正式粒子视图及固定预算接到同一串行 FrameSession。精确有理配额与半开整数出生，出生时刻纯最终文字查询，不执行视觉／事件；分数帧副本读取，固定边界提交。世界出生／解析运动保留旧行粒子，固定网格／事件和末端拖尾由公共圆点呈现。公开 CursorTrail 源码实际编译加载，未提供工作区 UI。

Jump／SemanticCursor 已接通；Bridge 因重叠行后继待答明确拒绝，IncludeInTargetCapture 因主画布协调器未完成拒绝。Burst、作者 Particles／EmissionSchedule、sprite／线段拖尾、输出依赖／映射和包／工程持久集成仍须后续实施。细则见 [合同](../../scripting-continuous-particles-contract.md)，验证统计与版本启动见 roadmap §18.52。

### 3.22 0.10.0 独立发射阶段与作者完整粒子／区间记录

用户确认新增独立纯 EvaluateEmitters 和全实例每区间100000条完整出生预算。SDK 旧 VisualWriter.Emitters／CompleteOutput 移除；同一对象七阶段，事件／共享推进次数保持。当前边界描述登记后，准备完整 Particles 及真实 EmissionSchedule，再 AdvanceVisuals／EvaluateVisuals。只读查询按实例隔离并保留旧装载，不借源订阅扩展输出；短命粒子仍保留出生记录，右端归下一区间。分数帧用左端描述和模拟副本，不登记新描述／推进，AnchorQuery 跳过全部视觉。

真实 CursorTrail 具备完整 manifest／摘要／参数，包分享离开原目录仍离线编译；默认／颜色与 rate 覆盖进入实际帧／像素。预算、作者 Error、过期 writer、跨阶段同名输出和纯帧门禁明确失败，真实编码保护原输出。程序不兼容升 MINOR 到0.10.0，schema21／15／3保持，缓存包含 SDK 字节，不兼容旧源码。验证与状态见 roadmap §18.53／[发射阶段合同](../../scripting-emission-stage-proposal.md)。Burst／桥接／sprite／实际映射、包材质／保存事务／工作区和完整 R2～R8继续实施。


### 3.23 0.11.0 事件 Burst 与四类真实来源

用户确认每锚点独立 count、同事件同发射器最后登记、显式源元素／行／标记／全部当前播放头；指定锚点缺失阻止导出，同刻 Burst 在前；封闭 ParticleSource 四类来源及 nullable TrackId／真实 EventId 获确认。EventContext.Bursts 只登记本真实事件意图，全实例最终文字完成后解析，不改变事件次数或历史。

边界粒子立即可读，出生记录归下一完成半开区间一次；连续与 Burst 共用身份／精确配额（Burst 不改配额）及全部实例预算，固定物理外观和世界出生坐标保持。AnchorQuery 不解析意图，失败／取消／重放清理。最小公开 EventBurst 源码实际离线编译并运行，前奏／可见与起唱分开。完整 R2～R8、原待答和持久 UI 保持；规则及最终验证见 [Burst 合同](../../scripting-event-burst-contract.md)／ADR-0089／roadmap §18.54。

### 3.24 0.11.1 真实组仿射与明确写权限

用户确认仅工程／显式节点绑定可写组。FrameWriter.GroupTransforms 与真实 GroupGeometry 范围、ComposeOutputs、SceneAffineProjection 及 PreparedRenderFrame 已接通；Node 后代按运行父树授权，不按源歌词集合推断所属组。基线目录仍可读，不从读取扩权。

组／槽位、整行、族、标记各组合一次，隐藏组数值仍校验；前奏两种附着、独立翻译、最终组参考框／墨迹、Playback／未来端点、捕获和连续粒子出生共用最终几何。旧构造签名保留，新能力不改 JSON 或增加程序集。规则及最终全量证据见[组仿射合同](../../scripting-group-affine-contract.md)／ADR-0090／roadmap §18.55。整行 Style、组隔离 alpha／图、原待答与 R3～R8 未完成。


### 3.25 0.11.2 真实包组仿射创建门禁修复

0.11.1写组及生产传播已实现，真实包创建能力表仍遗漏group；已补通VisualTransform/group。新增真实包Rendering9／Scripting2，九条新生产回归在修复前均失败；修复后参数、绑定／声明约束、归档消费与静态布局像素对照通过。Export3改用真实包创建链，真实MP4／Alpha及失败保护继续通过。全量2286项、构建0警告／错误，缓存／启动限制和证据见 roadmap §18.57及[组仿射合同 §5](../../scripting-group-affine-contract.md#5-真实包创建接缝修复0112)。材质C／最高层叠放已确认待实施，完整R2～R8保持。

### 3.26 0.12.0 C 统一包目录与真实图绑定

用户确认 C 后，EffectPackageDefinition／CompiledEffectPackage 统一三类导出、唯一参数及完整资源门禁。完整 RGBA／现有渐变不补字段或修正值；支持情况由正式绘制后端验证。纯资源包无假 DLL；有源码时全部编译，失败不公开部分资源。实际 SDK 材质／图句柄按真正导出建立，控制器图输出同时检查双方目标／绑定和完整参数集。固定 native 集合把句柄接到包真实图及输入配方，显式执行使用生产固定歌词字形／注音捕获，透明背景依赖图返回原输入并诊断。

回退投影与静态规范外观逐字节一致，图 shader 改 RGB 保持 alpha；真实 MP4／Alpha通过显式唯一行适配和预览同帧。该适配不代替通用父子主画布；PackageExport材质、部件自动重组、最高层叠放、实际下层、跨输出计划及持久事务仍待完成。API更名不保留别名，0.12.0 不改 schema21／15／3。完整验证见 roadmap §18.58／[合同 §10](../../scripting-package-graph-semantics-contract.md#10-c-实际实施接缝0120)／ADR-0092。


### 3.27 0.12.1 主画布图、最高层部件来源与真实下层

SceneImageEffectResources 固定已编译程序／显式框／图片，主 Render 自动处理元素族、行与标记，不再以私有显式合成代替整个画布。元素图从剩余行 glyph 排除对应字形，注音随元素处理，整行读取当前孩子。每输入选择真实来源，最高层融合，不能切分已融合图片；无目标输入图替换全目标。这两项新边界由用户确认。

真实下层、历史替换覆盖、排除部件与共同 alpha 共用对象合成；透明跳过保留原输入和原顺序，不伪造融合限制。实际旁路诊断经 RenderFrame、缓存和导出报告保留／去重；真实混合包和媒体玻璃链通过同一 Render 入口编码，中途坏 shader 像素保护已有输出。共用帧账清理输入／捕获／图／合成／快照，超限不降质。

没有新 JSON 或源修改，schema21／15／3 保持；兼容宿主扩展交付 PATCH。组图／alpha 隔离、跨行父组与自身粒子排除、owned overlay、区域材质及非 ColorOnly 映射继续后续；涉及真实后代或自身后置粒子时明确拒绝。完整 R2～R8 未完成，具体验证见[主画布合同](../../scripting-main-canvas-composition-contract.md)／roadmap §18.59／ADR-0093。

### 3.28 0.12.2 真实组 alpha 与嵌套主画布隔离

增加组 MultiplyOpacity、不可变 GroupOpacity 和有效准备输出；真实控制器包的 StyleModulation/group 能力／类别门禁已接通。有限非负因子按调用和实例组合后最终校验，隐藏组仍校验、恒等省略，保持明确组写权、源引用与重放。

正式 Render 按运行父树和首个原绘制序隔离父子组，孩子实际绘制／行图先完成，再混合原下层与处理后画面应用共同 alpha。未叠加背景或逐孩子乘组 alpha，前奏、注音及外部分支保持实际归属；交错后代成为连续单位时具名提示，经缓存及导出保留。共用资源账、取消和真实归因通过；组透明度宿主程序独立于借用的作者图资源。

原始 TargetColor 捕获涉及本范围组 alpha 时报告 GroupOpacityCapturePending；Coverage 保留原始几何，不返回假最终颜色。组图／部件化父捕获、owned overlay、自身粒子、区域材质、其余 writer 和持久工作区继续，不关闭 R2～R8。合同／出口证据见[组隔离合同](../../scripting-group-opacity-isolation-contract.md)／roadmap §18.60／ADR-0094。


### 3.29 0.12.3 父捕获所需可传递合成产物

现有CPU图后端新增前景／擦除双表面产物，提取同目标历史max及最终普通alpha、顺序合成独立孩子和真实下层应用。独立80%擦除孩子重叠后为96%，不混同同目标历史max；共同alpha同时缩放两通道。真正拥有复制表面，原输入／产物／编译图释放后仍可使用；固定宿主程序帧外创建，局部／共享峰值、别名／跨帧、失败／取消及原生下溢门禁验证。

这是组图父捕获的CPU接缝，不扩大作者写权或开放组图writer，未改正式Render输出规则。父级部分alpha、无可见孩子及继承实际擦除三项已询问待答，未使用默认规则。完整R2～R8／R3出口保持未完成，schema21／15／3不变，证据见[产物合同](../../scripting-parent-composition-contract.md)／roadmap §18.61／ADR-0095。


### 3.30 0.13.0 区域材质输入与固定图执行接缝

用户三项答复已落入[区域材质合同](../../scripting-region-material-contract.md)：真实绑定区域限制、包基础外观TargetColor、两种显式覆盖模式。Core严格编码及当前包profile2，Skia真实glyph／注音／外描边和互补逐字区域成对捕获、固定包执行、覆盖限制与已覆盖验证、背景透明基础外观旁路已实施；白色几何后着色消除彩色AA的覆盖差异，独立颜色／擦除产物与共享预算保持。

这是R3生产接入所需输入和执行接缝，不是完整writer：准备帧PackageExport仍明确拒绝，逐区域真实写入者、主Render调用、图捕获处理后材质和完整成片仍继续。组图三项及原四项待答保持，无默认行为；工程／模板／分析事务和工作区按原路线。验证以roadmap §18.62为准。

### 3.31 0.13.1 区域材质进入真实准备帧与主 Render

0.13.1 已接通元素族／真实前奏标记的 PackageExport 准备帧和正式主 Render。未唱／已唱／描边逐属性保留最后写入者，真实 StyleView 保留包句柄／参数，LyricStyle 只作明确几何绘制种子；没有归属或固定资源拒绝。已唱／未唱互补裁切，基础字及注音使用实际材质，后续整行图读取已处理彩色部件；元素局部 alpha、标记／行共同 alpha、组 alpha 各一次。透明背景依赖使用自身包基础外观并保留真实 MaterialHandle 提示，缓存和导出按实例去重。原始 TargetColor／彩色部件捕获遇到未重组材质报告 MaterialProcessedCaptureRequired，不返回白色种子。

新增 Rendering13／Export3；八项目全量 **2460 项全部通过，0失败／跳过**（83／1006／77／87／774／170／179／84），全量构建0警告／0错误。定向渲染119项、导出7项通过。真实 C# 包验证两种覆盖模式、填充／描边／注音和密度、参数／区域、归属不被后续alpha抢走及恢复纯色解除绑定、整行图读取材质结果、真实前奏标记、透明纯色／渐变基础alpha与缓存、预算／坏覆盖释放；实际MP4与ProRes4444同帧对照及200ms中途坏材质保护已有输出通过。

沿用已授权依赖缓存：dotnet build MakoKaraGenerator.slnx --no-restore -m:1、dotnet test MakoKaraGenerator.slnx --no-build -m:1。最终日志 /tmp/makokara-material-production-build-final.log、/tmp/makokara-material-production-tests-full.log；定向 /tmp/makokara-material-production-focused-tests.log、/tmp/makokara-material-production-export-tests.log。本批未改依赖，没有新的在线恢复／审查证据。

本批不新增工程字段、包编码、程序集或依赖；程序升 PATCH 0.13.1，schema21／15／3与package profile2保持。Materials.SetUniform、组图／父配方、映射、跨输出执行计划、工程／模板／分析事务和工作区仍待实施；原七项待答不使用默认值。完整 R2～R8／R3 出口保持未完成。 验证权威见roadmap §18.63／ADR-0097。


### 3.32 0.13.2 直接渐变描边与渐变单次 alpha

按R0 §5／§6.1，现有元素族／标记SetStroke可直接写入支持的GradientMaterial；准备帧保留全部色标和参数，主Render复用固定字形及注音，外部轮廓先扩张再减内部。Core渐变描边允许Color=null；不制造第二个基色。Linear／Radial、Canvas／Line、LinearRgb／Hsv及三种连接方式沿现有定义，未支持组合和原生退化仍明确失败。

共用渐变绘制已去掉画笔中重复的历史纯色alpha／style.Opacity，shader色标各含样式alpha一次。静态／脚本的半透明支持渐变会按合同修正，纯色与包材质覆盖路径保持。此为现有SDK能力补齐，无新schema／包编码／程序集；PATCH0.13.2，完整R2～R8保持未完成。实际证据及剩余范围见roadmap §18.64／[样式帧合同 §5](../../scripting-r2-style-frame-contract.md)。


### 3.33 0.13.3 显式部位与逐图节点 uniform

用户确认 U1／U2／U3 后新增 FrameWriter.Materials.SetUniform 的元素族／标记重载，明确固定句柄、MaterialSlot、节点及名字。StyleUpdate.MaterialEdits 保留 ReplaceMaterial／Uniform 顺序；宿主逐实例检查完整声明、实际区域、七类／数组及原生数值，再按当时材质解析身份。Set 清除旧覆盖，后实例可修改同修订前实例输出，alpha／阴影保持不抢材质归属。最终 alpha 仍在全部实例合并后检查。

PackageMaterial.Uniforms 为不可变逐节点值集，完整参数和资源修订不变。准备帧保留最后写入者，主Render传入 ImageGraphFrameContext；节点值优先于原字面量／参数／宿主值，固定程序不重编译。透明回退之前同样校验覆盖，非法早期操作不被后来的Set掩盖。标记准备与跳回重放不延续前一帧覆盖。

本批不改JSON、schema或依赖，增量SDK交付PATCH0.13.3。完整R2～R8仍未完成；组图／父配方三项与原四项待答保持，整行Style、实际映射、桥接／sprite、跨输出执行、持久事务和工作区继续。合同见[材质uniform合同](../../scripting-material-uniform-writer-proposal.md)，精确验证见roadmap §18.65／ADR-0099。

### 3.34 0.27.0～0.28.0 R6 向量与固定图片图元接续

0.27.0 的封闭 OverlayBatch／DrawSegments／DrawTrails 与 Custom 粒子纯绘制进入正式准备帧和 Render，真实 VectorTrail 包已验证；0.28.0 接通 SpriteBatch／DrawSprites、固定包图片归属、裁片单位方形到世界仿射和 Tint，SpriteTrail 完整图片包可分享及持久重开。宿主继续真实模拟和固定拖尾，不通过作者绘制推进状态。OverlayOwners 保留实际编译／包来源，同一 Render 帧账覆盖精灵与图合成，图／组复制保留全部输出。具体规范见[向量合同](../../scripting-vector-overlay-contract.md)、[精灵合同](../../scripting-sprite-overlay-contract.md)；权威证据见 roadmap §18.90～18.91／ADR-0123～0124。Bridge 后继／独立未来端点、目标内捕获、整行 Style／复杂映射及完整 SDK／R6／R7／R8 继续，未以图元接续关闭完整路线。

### 3.35 0.29.0 真实后继与独立最终端点

固定 Current 源／装载投影与正式 Playback 共用；CursorTransitionEvent 进入固定事件、callback 和历史。源顺序、同时起唱并发组、缺行时间阻断、重叠零空档及真实 BindingId 两端筛选依[后继合同](../../scripting-cursor-successor-contract.md)／ADR-0125。宿主 ResolveSuccessorsAsync 独立重放全部效果，保留最终文字／父树几何、Error／取消、包来源和及时释放，不污染主会话。Bridge 发射、目标内捕获、映射及完整 R6／SDK／R7／R8 继续，权威证据见 roadmap §18.92。此前各节的重叠后继待答属于当时状态，当前由本批裁决取代。

### 3.36 0.30.0 Bridge 飞行与统一粒子模拟

桥接沿全部合法后继逐边独立积累，实际头与 Bridge 身份分开；宿主只准备下一固定区间相交路径，从真实飞行起点精确积分，不增加语义推进边界。独立 AnchorQuery 固定全部效果／音频／源材质，缓存仅保留不可变播放头值，关闭／重建／失败清空；未来 Error 不改成请求实例。正式圆点、解析运动、固定拖尾、记录和全部预算复用原模拟。真实 CursorTrail 1.1.0 包分享／持久重开、三帧率、双文字／音频与实际 MP4／Alpha 同帧见[桥接合同](../../scripting-cursor-bridge-contract.md)／ADR-0126／roadmap §18.93；目标内捕获、复杂映射、完整 SDK／R6／R3.1／R7／R8 继续。
