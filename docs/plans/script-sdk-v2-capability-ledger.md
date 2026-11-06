# D8 能力与样例核对台账

日期：2026-11-04。范围：M00 源码核对与 M07 首批有限证据；**完整 D8 尚未关闭**。
本表落实[主计划 §5.9](script-sdk-v2-render-pipeline-plan.md#59-d8-能力矩阵与九样例验收)，
不改变 D1–D17，不把现有测试入口、历史报告或候选类型写成当前版本运行通过。
M01–M06 保持 v1 API／求值语义，不能据其性能优化关闭下表的 v2 项。

2026-11-05 用户调整：性能数据保留记录，性能门槛暂不作为本轮 M07–M11 验收标准。
下文六字段中的 P 不阻塞本轮功能验收；其余字段及正确性、像素、预算、取消和生命周期继续必需。

## M09 Warp／SVG 有限增量（产品0.112.8）

C10 的 token／cluster／mark Warp 已准入并有真实包像素及最终锚点证据；
C05 与 T01 关联的 SVG vector／atlas typed输出、固定资源、完整affine、tint／mip、
预算与失败owner已接入生产。SVG真实包显式Vulkan18项及图元19项通过；
作者宿主完整包编辑／坏SVG编译／修复及闭包正负10项通过。
详见[M09证据](script-sdk-render-m09-evidence.md)。HTTP、导出、前端完整包编辑、图组和媒体轨仍待；
本增量不关闭C03／C05／C10、样例S05/S07–S09或T01。

## M10 媒体特征轨功能接通（产品0.112.36）

`media.features` 已开放：宿主分析、固定运行输入、Worker 绑定、SDK `frame.Media.Sample`、
预览换代与导出等待均已接通；AdaptiveStroke 示例经真实 HTTP Pending→Available 换代验证。
详见[M10证据第二十一片](script-sdk-render-m10-evidence.md)。按 2026-11-07 用户指示，
性能与全量回归不阻塞本轮；T01 复杂样式、图消费者查询和作者链 720 帧验证归 M11。

## 证据登记方式

每项由稳定 ID 跟踪六类证据，禁止只凭九样例完成而省略能力：

| 字段 | 必须登记的内容 |
|---|---|
| 表达 E | 固定包、SDK／schema、声明与强类型输出；被替换的 v1 用途；不支持输入的具名原因 |
| 作者 A | 导入／编辑／编译／采用／撤销／保存重开步骤，草稿 CAS 与源／采用／显示修订 |
| 执行 X | 输入摘要、源码 SHA、设备／后端、预览与导出结果、帧时刻和像素／结构化输出 |
| 诊断 D | 正反例、包／实例／阶段／目标／TimeUs／源码位置、预算、取消及失败归属 |
| 确定性 T | 顺序、乱序、重复、memo 冷热／淘汰、重开与撤销的同输入输出对照 |
| 性能 P | 编译／准备／seek／显示分开；近端、3 min、长时间；内存、资源与无仪表端到端对照 |

下表“待”表示尚未完成该项全部 E/A/X/D/T/P；M07 已取得的有限子集另在下节登记，不能据此关闭整项。
关闭时逐项填入证据目录及命令、环境、通过／失败／跳过数；缺字段保持未完成。
公共作者链现有入口为 [DraftSourceTab](../../frontend/src/editor/library/DraftSourceTab.tsx)、
[ParameterFields](../../frontend/src/editor/library/ParameterFields.tsx)、
[DraftValidationTab](../../frontend/src/editor/library/DraftValidationTab.tsx) 与
[ProjectSessionRegistry.Author](../../backend/src/MakoKaraGenerator.Backend.Hosting/ProjectSessionRegistry.Author.cs)。
M07 的固定来源编译／显式采用和三例编辑预览已接线；新增 SVG／manifest／shader／图编辑和诊断视图另由 M11 验收。

## M07 有限证据（产品 0.111.3）

完整来源、命令、运行边界及性能候选失败见 [M07 记录](script-sdk-render-m07-evidence.md)。
以下为分项增量，不把部分证据升级为整项关闭。

| 关联 ID | 当前 E／X／D／T | 当前 A／P 与剩余项 |
| --- | --- | --- |
| C01／C02 | 纯文字 Transform／Style／Opacity、Base／Cluster、B*A、Ruby／父链与组隔离接入；shader profile、不可独立字簇、退场／重载、预算和取消有定向正反例；渲染43项、SDK105项通过 | 三例编辑预览可用；完整样式组合和 M09／M12 仍待，正式性能待确认 |
| C04／C09 | Timeline／Presentation／Layout／Cursor 源索引与真实 ordinal 代替 Repeat 字段累加，固定范围、冷暖／淘汰／乱序／重开和装载去重有回归 | HTTP 编译／采用／历史／CAS及三例 UI 通过；任意字段状态用途仍须 M08／M13 逐项映射 |
| C08 | v2 复用固定参数／音频／计数式随机只读输入，纯度分析与句柄限制已接入 | 粒子随机身份和新增出生轨由 M08 验收，完整音频驱动功能不在 M07 关闭 |
| S01／S02及最小 ClusterExit | 实际 SDK2 包与 Vulkan；24组 v1/v2 单 token 像素对照差0，20组跨进程确定性通过；边界与多token语义另有定向回归 | UI 33／17／17检查、9张实际显示帧；保存库重开、完整验证与导出留 M11。加载／seek已测，正式阈值待确认且候选一项未达 |

C03／C05–C07／C10及其余样例仍待所属节点；M08 已开放接口后的独立编码不构成运行通过。

## M09 G2 已开通有限能力：`image.graph`（0.112.10）

`image.graph` 的有限含义为 SDK v2 typed controller 通过 `TextOutput.BindGraph` 向真实
Family／Line／Mark 写入 ColorOnly 固定包图；完整 `GraphBindingValue` 每次整体替换图、参数、
uniform 与 Transform 覆盖，同实例同目标保留第一次插入位置，跨实例保留工程顺序。
Core `SupportedV2Capabilities` 已在关门红例与真实包 CPU／Vulkan 正反例通过后开放此有限能力。

每次写入均检查目标／能力／owning 包修订与资源、值自身形状和 float32 范围；宿主对包括被覆盖值在内的
完整写序列检查导出目标、bindingKinds 与完整参数。只对最终存活绑定检查真实 shader／节点兼容性。
逐 writer 及整帧全部实例分别累计最多 256 次绑定、65,536 个标量和 8 MiB 验证字节；数组与被覆盖写入计费，
图写失败锁存至共享文字 writer 的后续 Transform／Style／Warp／Graph 和 Complete，取消原样传播。
实际密度下的矩阵、输出框、逆采样与表面预算由 G1 图执行器复验。X／D／T 有限证据见
[M09 记录](script-sdk-render-m09-evidence.md)；作者 UI／HTTP／导出图包链仍待。

G2 当时保留的直接图和材质 uniform 门禁由下述 G3 接续。组图、实际 AnchorMap／GeometryMapping、
跨输出依赖和 owned Below 仍待；性能验收依用户指示暂停，本切片不关闭 C03。

## M09 G3 已开通增量：宿主直接图与 `material.uniform`（0.112.11）

`image.graph` 现支持无 C#／DLL 的 host-owned v2 direct graph，按真实 scope／load 生成
Family／Line／Mark ColorOnly 绑定，复用 G1/G2 验证及执行器，不进入 Declare 或旧生命周期。
token 绑定扩大到整行仍拒绝。`material.uniform` 支持 Family／Mark 的明确部位与 owning handle。

Fill／Stroke 完整替换和 uniform 保留全部原始写序列，逐次验证 intrinsic、资源、参数和预算。
每次 Replace（包括相同 handle 和关闭描边）清除该部位旧覆盖；只有最终作用域存活 uniform
验证安装材质／节点／名称／类型／数组，归属真实写入者。全部图／材质共用 256 次、65,536 标量、
8 MiB 验证字节预算，作者捕获错误仍锁存，取消直接传播。旧 v1 即时语义和公开值 equality 保留。

SDK24、真实包CPU／Vulkan各19、Scripting509、Rendering1575、RuntimeHosting153通过，
详见[M09证据](script-sdk-render-m09-evidence.md)。Base包材质／uniform、Group、mapping、
跨输出计划和owned Below仍待，C03与M09未关闭。

## M09 G4 增量：真实组图与 owned Below（0.112.13）

`image.graph.group` 要求同时声明 `image.graph`，controller／direct只可写宿主授予的真实source组。
C03已增加组完整C/E、独立孩子擦除、部分配方、透明旁路、两级alpha及真实父树范围的E/X/D/T证据；
C05／S09增加六类直接owner BehindText排除、固定父前缀／兄弟前缀和Family/Base材质共享resolver。
默认Rendering1697、显式Vulkan环境组合153通过；组预算及空选择有红绿证据，像素容差仍2/255。
Base独立包材质在0.112.12已有CPU／Vulkan34项有限像素证据。

作者A、匹配HTTP／导出X以及完整样例仍待后继验收；Mapping／跨输出计划保持门禁，
不由本切片关闭C03／C05、S09或T01。详见[M09记录](script-sdk-render-m09-evidence.md)。

## 全能力映射

M09 当前消费者增量（0.112.15／合同1.46.1）：C03、C05关联的组、C07相关颜色与C10相邻资源链
由`http-graph-05`取得六场景真实作者→保存重开→透明导出证据，1425请求／10173断言，76张
Vulkan PNG及六件48帧成片。C03包含无DLL直接图、typed动态矩阵、Base填充/描边uniform、
两层真实组和owned Below独立半透明参考。精确声明／完整采用通用UI另有140检查／7显示帧。
详见[M09记录](script-sdk-render-m09-evidence.md)；未据此关闭命名九样例、T01或完整D8，
固定font／普通Sprite／BackgroundMedia的当前v3证据已补齐，见下节。性能门槛暂缓。

路径前缀：`SA` = `backend/src/MakoKaraGenerator.Scripting.Abstractions`，
`SE` = `backend/src/MakoKaraGenerator.Scripting/Execution`，
`ST` = `backend/tests/MakoKaraGenerator.Scripting.Tests`，
`RT` = `backend/tests/MakoKaraGenerator.Rendering.Tests`。路径均相对 App 根。

| ID／v1 可核查用途 | 实际源码与现有回归入口 | v2 候选表达及必须保留的边界 | 后续节点／证据 |
|---|---|---|---|
| C01 字族缩放、行／组／标记仿射 | `SA/FrameWriter.cs`、`FrameWriter.Lines.cs`、`FrameWriter.Groups.cs`、`FrameWriter.Marks.cs`；`ST/FrameWriterContractTests.cs`、`GroupTransformContractTests.cs`；`RT/PreparedGroupTransformIntegrationTests.cs`、`PreparedMarkFrameTests.cs` | 强类型 Transform，增加字簇目标；完整仿射、Ruby 继承、父子矩阵与不可拆字簇诊断；不能将不可拆合字按 UTF-16 切开 | M07／M09，E/A/X/D/T/P 待 |
| C02 样式写入与透明度 | `SA/FrameWriter.Style.cs`、`FrameWriter.Materials.cs`、`FrameWriter.Groups.cs`；`ST/StyleWriterContractTests.cs`、`GroupOpacityContractTests.cs`；`RT/PreparedStyleFrameTests.cs`、`GroupOpacityIsolationTests.cs`、`MixedEffectPackageTests.Glow.cs` | Fill／Stroke／Shadow／Glow／Opacity；基线与叶覆盖、组 alpha 隔离、全域部件顺序；不能让后画字辉光覆盖先画字填充 | M07／M12，E/A/X/D/T/P 待 |
| C03 材质 uniform／图绑定 | `SA/MaterialUniforms.cs`、`FrameWriter.Materials.cs`、`FrameWriter.ImageEffects.cs`；`SE/FrameEffectHost.Materials.cs`、`FrameEffectHost.PackageOutputs.cs`；`ST/MaterialUniformWriterTests.cs`、`ImageEffectWriterTests.cs`、`DirectGraphEffectTests.cs`；`RT/MixedEffectPackageTests.UniformTypes.cs`、`MixedEffectPackageTests.RegionExecution.cs` | 类型化 Graph／uniform 输出；owning 包、参数／矩阵身份、背景和透明 fallback、采样边界与绑定顺序；组图作为新增能力单列 | M09／M12，E/A/X/D/T/P 待 |
| C04 事件与 History | `SA/TimelineEvent.cs`、`DisplayTimelineEvents.cs`、`TimelineHistory.cs`；`SE/FrameSession.cs`；`ST/TimelineEventContractTests.cs`、`TimelineHistoryTests.cs`、`HistoryPrefixSessionTests.cs`；`RT/RepeatEmphasisSessionTests.cs` | TimelineIndex、typed selector、ordinal；真实 occurrence／scope、同边界顺序、跨 branch／重复装载去重；事件前后 1 µs、缺时间与不依赖查询方向 | M07／M08，E/A/X/D/T/P 待 |
| C05 连续发射与 Burst | `SA/EmitterWriter.cs`、`BurstWriter.cs`、`EmissionSchedule.cs`；`SE/FrameEffectHost.Emitters.cs`、`FrameEffectHost.Bursts.cs`；`ST/EmitterWriterContractTests.cs`、`BurstWriterContractTests.cs`；`RT/EmissionStageTests.cs`、`EventBurstTests.cs`、`ContinuousParticleTests.cs` | 累计速率轨／事件出生表；固定、分段常速和有限分段线性速率，payload／scope／occurrence、四类锚点与 line fallback；精确配额、出生死亡边界和随机身份 | M08，E/A/X/D/T/P 待 |
| C06 解析运动、桥接与拖尾 | `SA/ParticleDescription.cs`、`ParticleSource.cs`、`CursorEndpoint.cs`、`GeometryQueries.cs`；`SE/FrameEffectHost.Bridges.cs`、`FrameEffectHost.Queries.cs`；`RT/ContinuousParticleTests.Bridges.cs`、`ContinuousParticleTests.BridgeLifetime.cs`、`CursorSuccessorSessionTests.cs`、`GeometryQuerySessionTests.cs` | 有界解析查询及最终锚点 memo；未来／插值端点另计预算，不跨身份／断点插值；出生后年龄、Custom 访问、≤5 s 寿命、120 Hz memo 冷热一致 | M08，E/A/X/D/T/P 待 |
| C07 手工 Overlay／Sprite | `SA/VisualWriter.cs`、`VectorOverlays.cs`、`SpriteOverlays.cs`；`SE/FrameEffectHost.Visuals.cs`；`ST/VisualWriterContractTests.cs`、`VectorOverlayContractTests.cs`、`SpriteOverlayContractTests.cs`；`RT/MainCanvasImageEffectsTests.VectorOverlays.cs`、`MainCanvasImageEffectsTests.Sprites.cs` | 晚期 Dots／Segments／Trails／Sprites／Paths；完整 Sprite 仿射、图层／批次顺序、数量预算，Custom 不能与自动粒子重复绘制 | M08／M09，E/A/X/D/T/P 待 |
| C08 音频与确定随机 | `SA/AudioFeatures.cs`、`DeterministicRandom.cs`、`ParameterValues.cs`；`ST/FrameServicesTests.cs`；`RT/AudioReactiveFrameTests.cs`、`PersistedAudioFrameTests.cs` | 固定 AudioFeatures、计数式随机；required／optional／无数据状态、固定分析制品身份、实例与粒子稳定身份；参数／seed 的变化不能误命中旧派生结果 | M07／M08，E/A/X/D/T/P 待 |
| C09 字段状态承载的实际功能 | `SA/StateContext.cs`；`SE/FrameSession.cs`；`samples/ScriptEffects/RepeatEmphasis/src/RepeatEmphasis.cs` 的 `counts`／`ordinal`；`ST/SemanticSessionTests.cs`、`HistoryPrefixSharingTests.cs` | 不支持任意累积字段；逐用途登记为源索引、递归只读准备结果或解析表达。重复强调、时变发射等功能仍须保留；无法表达即 D8 发布阻塞，不能自行标为淘汰 | M07／M08／M13，E/A/X/D/T/P 待 |
| C10 非生产 AnchorMap／GeometryMapping | `SA/AnchorMap.cs`、`GeometryValues.cs`；`ST/PlaybackGeometryContractTests.cs` 仅验证映射值；`backend/src/MakoKaraGenerator.Scripting/Compilation/DirectGraphEffectValidation.cs` 与 `backend/src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.ImageScene.Binding.cs` 仍拒绝实际映射；`ST/DirectGraphEffectTests.cs` 有 `PackageGraphMappingPending` 负例 | 有界 Warp；拓扑／退化拒绝、接缝与锚点正向映射一致。原值类型不改变生产文字／光标，不能把它当已执行像素基线 | M09，E/A/X/D/T/P 待；新增能力 |

上表为 §5.9 十类能力的核对入口，不证明每个组合已经执行。组图、SVG、Warp、媒体特征轨、
字簇退场是额外显式范围，见目标样式；C10 的未生产能力不自动形成兼容承诺。

## 九样例

现有源码／资源分别位于 [公开样例目录](../../backend/samples/ScriptEffects/README.md) 下的同名包；
以下每项都须完成 v2 重写后，以同一包修订走作者编辑→编译→采用→预览→保存重开→导出。
使用普通／透明两输出、多实例及固定参数／种子；失败必须定位当前修订，不以旧成功帧代替 ACK。

| ID／包 | v1 实际来源／现有回归 | v2 验收重点 | 后续节点／证据 |
|---|---|---|---|
| S01 WordEmphasis | `WordEmphasis/src/WordEmphasis.cs`；`RT/PreparedStyleFrameTests.cs`、`PackageControllerRenderingTests.cs`；`ST/CompiledEffectPackageTests.cs` | 缺时间状态、字族／Ruby、当前字放大、唱完回落；任意 t 及边界 ±1 µs | M07／M11，E/A/X/D/T/P 待 |
| S02 RepeatEmphasis | `RepeatEmphasis/src/RepeatEmphasis.cs`；`RT/RepeatEmphasisSessionTests.cs`、`MixedEffectPackageTests.cs` | 按真实演唱 occurrence 的 ordinal、project／branch scope、重复装载去重；替换事件累积字典，保留基线材质限制诊断 | M07／M11，E/A/X/D/T/P 待 |
| S03 CursorTrail | `CursorTrail/src/CursorTrail.cs`；`ST/CursorTrailPackageTests.cs`；`RT/ContinuousParticleTests.cs` | 连续发射精确配额、最终锚点、寿命、Jump／Bridge 和稳定随机身份 | M08／M11，E/A/X/D/T/P 待 |
| S04 VectorTrail | `VectorTrail/src/VectorTrail.cs`；`ST/VectorOverlayContractTests.cs`；`RT/VectorOverlayRenderingTests.cs` | Custom 粒子、解析拖尾、断点、批次顺序与图元预算；不重复自动输出 | M08／M11，E/A/X/D/T/P 待 |
| S05 SpriteTrail | `SpriteTrail/src/SpriteTrail.cs`、`assets/spark.png`；`ST/SpriteOverlayContractTests.cs`；`RT/SpriteOverlayRenderingTests.cs` | 固定资源身份、完整仿射、图集边缘、透明颜色与数量预算 | M09／M11，E/A/X/D/T/P 待 |
| S06 EventBurst | `EventBurst/src/EventBurst.cs`；`ST/BurstWriterContractTests.cs`；`RT/EventBurstTests.cs`；`Export.Tests/EventBurstExportTests.cs` | payload 条件、四类锚点、line fallback、同边界顺序、occurrence 与配额 | M08／M11，E/A/X/D/T/P 待 |
| S07 CustomInk | `CustomInk/manifest.json`、`graphs/wave.graph.json`、`shaders/wave.sksl`；`RT/PackageShaderProgramsTests.cs`、`MixedEffectPackageTests.Materials.cs` | 无控制器的纯资源包、材质参数／覆盖、uniform 和透明输出 | M09／M11，E/A/X/D/T/P 待 |
| S08 LineGlitch | `LineGlitch/graphs/glitch.graph.json`、`shaders/glitch.sksl`；`ST/DirectGraphEffectTests.cs`；`RT/PackageImageGraphsTests.cs`、`MixedEffectPackageTests.RegionExecution.cs` | 图节点、目标绑定、动态参数／矩阵、采样范围；保持 ColorOnly 与几何 Warp 的能力边界 | M09／M11，E/A/X/D/T/P 待 |
| S09 GlassInk | `GlassInk/graphs/glass-media.graph.json`、`graphs/glass-backdrop.graph.json`、`shaders/glass.sksl`；`RT/MixedEffectPackageTests.ProductionMaterials.cs`、`MixedEffectPackageTests.Materials.cs` | BackgroundMedia／BackdropBelow 分离、透明 fallback、模糊／折射范围与真实下层顺序 | M09／M11／M12，E/A/X/D/T/P 待 |

样例表中的相对包路径以 `backend/samples/ScriptEffects/<包>/` 为根；`Export.Tests` 为
`backend/tests/MakoKaraGenerator.Export.Tests`。现有混合包回归是相邻消费者入口，不能据名称推定
每个样例、每种新后端或完整作者流程均已覆盖。

## 目标复杂样式与验收脚本

T01 的完整能力为：当前字强调；当前句退场时，具有稳定身份且可独立变换的字簇倾斜／Warp／
向左下飞行并淡出；粒子跟随最终播放头；SVG 星星扫过整句；背景自适应描边改善可读性。
它同时依赖 C01–C08，及 M07 退场冻结、M08 锚点、M09 SVG／Warp、M10 媒体轨、M11 作者闭环。
当前无此完整 v2 样例，E/A/X/D/T/P 全部待执行。

1. 固定测试工程：同句两个实例、Ruby、连字／不可拆字簇、重叠入场退场、场景父子矩阵，
   包含缺时间歌词。记录工程／歌词／包／字体／媒体内容摘要、参数、seed 和分析修订。
2. 从作者页修改参数及源码、编译并采用；记录草稿／编译／采用／显示修订，撤销并保存重开。
   在非法字簇分割、退场 >3 s、粒子 >5 s、SVG 外链／超预算、退化 Warp 时保留具名拒绝证据。
3. 在演唱、退场与粒子出生／死亡边界前后 1 µs 及两行重叠处取帧；核对冻结的退场身份、
   Ruby 归属、字簇完整性、完整文字变换后的锚点、Warp 接缝和 SVG 资源身份。
4. 对同一输入顺序播放、乱序 seek、重复帧、memo 冷暖／淘汰、重开和撤销；比较结构化输出及
   允许容差内像素。补算不能递归执行粒子，不能跨身份／断点插值。
5. 切换复杂明暗、纹理、镜头切换、透明与循环背景；覆盖媒体 Pending→Available／Unavailable、
   分析失败／取消和新输入修订。旧帧必须有旧修订标记；C# 与 GPU 特征采样及时间／空间映射一致。
6. 普通／透明预览和导出分别验收。1x 的 D13 初始容差为预乘 BGRA 每通道绝对差 ≤2/255；
   缩放／倾斜／Warp 的容差须测量后确认。复杂背景可读性须视觉核查，不能由平均亮度推导通过。
7. 在近端、3 min 与更长时间记录加载、准备、seek、显示、内存和资源成本；memo 的 120 Hz ×5 s
   不当作固定求值次数上限。正式性能门槛按 M00 的确认记录，不能把候选值当已批准门槛。

T01 关闭前必须给六字段各一份当前源码证据；任何未映射实际用途都进入 M13 阻塞清单。
M14 删除 v1 以前，完整能力表、九样例与 T01 必须同时完成，且保留通用回归与历史证据。


## M09 Base 独立包材质增量（0.112.12）

`TextTarget.Base` 支持两态包材质、描边与material.uniform，完整继承最终Family外观后叶覆盖，
逐slot Source／Script owner保持，Solid／Gradient／null遮蔽父材质。Base捕获排除Ruby；Family／Line
消费真实处理颜色。没有开放Base图。CPU／显式Vulkan34项覆盖像素、确定性、Warp／Glow组合、
资源账与错误归属；完整证据及共享几何oracle边界见[M09记录](script-sdk-render-m09-evidence.md)。
尚无此增量匹配HTTP／作者UI／导出证据；C03与M09不据此关闭。

## M09 资源映射完成与有限关闭（2026-11-05）

C07普通Sprite和C03／S09相邻的BackgroundMedia语义已有当前v3包显式Vulkan执行：
`M09ResourceRuntimeTests`六项通过，含CPU字体oracle、硬件字体、三密度裁片／完整affine／Tint，
以及独立媒体／下层控制和透明fallback owner诊断。固定font包未安装到系统，实际glyph family
和Ruby已核对；183请求／1349断言的真实作者链保留字节身份，保存重开及48帧透明导出通过。
详细证据与原始失败见[M09记录](script-sdk-render-m09-evidence.md)。本节点功能范围关闭；
表内命名样例的完整E/A/X/D/T映射由M11汇总，P暂缓；不提前关闭完整D8、S05/S07/S08/S09或T01。
