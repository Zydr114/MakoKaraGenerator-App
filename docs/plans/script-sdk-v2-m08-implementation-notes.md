# SDK v2 M08 实施接续笔记

日期：2026-11-04。状态：**首批实现已接通，有限测试通过，M08 验收尚未关闭**。
下文保留设计推导与候选接口；实际公开类型以 `Scripting.Abstractions/V2` 为准。
当前证据和待补门禁见[阶段一执行记录](script-sdk-v2-phase-one-progress.md)。

本文依据[主计划 §4.3、§5.5、§5.9](script-sdk-v2-render-pipeline-plan.md)、
[M08 验收行](script-sdk-v2-render-acceptance.md)及
[D8 能力台账](script-sdk-v2-capability-ledger.md)整理源码接缝和建议切片；
精确配额、episode 与溢出规则同时遵守[配额算法附录](script-sdk-v2-m08-quota-algorithm.md)。
不修改已经确认的 D1–D17，也不将下列类型名当成已经存在的公共 API。
M08 的依赖门禁仍是 [M07 完成](script-sdk-v2-phase-one-progress.md)；本文不宣布 M07 或 M08 通过。

## 1. M08 必须完成的范围

- 固定、分段常速和有限分段线性速率轨；精确整数微秒配额，从累计量首次达到 1 出生。
- Burst 的类型化 payload 条件、来源 scope、真实 occurrence、同边界顺序，以及
  `SourceToken`、`SourceLine`、`TextMark`、`BindingCursors` 四类既有锚点和 line fallback。
- 最终文字锚点、未来桥接端点、解析运动与有界解析拖尾；粒子寿命不超过 5 s。
- 120 Hz 有理网格 memo、完整几何依赖闭包的断点、不能证明连续时的精确出生时刻求值。
- 晚期 Dots／Segments／Trails／Sprites，Custom 只读取本实例粒子；自动和 Custom 不重复绘制。
- 真实样例编译与生产路径、像素、随机访问、冷热缓存、淘汰、取消、数量和成本预算负例。

M09 接续 Paths／SVG、Warp 及相关资源；M08 需留下最终锚点的几何映射接缝，
以便 M09 将 Warp 正向映射接入同一锚点链。ColorOnly 图位移不构成几何位移。
三个粒子样例成功也不能直接将 C05–C09 或所有 v1 状态用途标为对齐。

## 2. 当前实现的复用边界

| 当前源码 | 可复用部分 | M08 需要的新实现 |
| --- | --- | --- |
| `backend/src/MakoKaraGenerator.Scripting/Particles/ContinuousParticleSimulation*.cs` | 出生散布、外观和运动公式、已有边界用例及诊断语义 | 声明式速率轨、前缀积分、任意时间的有界枚举；不能复用依赖 `_boundary`、`NextIndex`、`Register/Commit`、可变粒子表和累计 Trail 的推进引擎 |
| `Particles/ExactEmissionQuota.cs` | 输入 double 到精确有理数的转换及配额边界案例 | 无可变游标的积分索引、线性速率积分及出生序号定位 |
| `Scripting.Abstractions/ParticleDescription.cs`、`ParticleSample.cs`、`ParticleSource.cs` | 运动／年龄曲线、出生来源和只读采样值 | 检查准备值递归只读、v2 身份与枚举窗口语义；不能带入旧帧边界状态 |
| `Execution/SemanticAnchorQuery.cs` | 固定源身份检查、`EndUs-1`／`StartUs` 语义、绑定后继和失败归属 | 可复用固定准备结果的文字阶段查询器、任意时刻锚点、连续性和预算；不能逐节点新建完整会话并重复构造事件表 |
| `Particles/ResolvedCursorBridge.cs` | 有界飞行区间内的曲线插值公式 | 与新最终锚点 provider 对接，端点另计成本，排除粒子／晚期视觉递归 |
| `Scripting.Abstractions/V2/SourceIndexes.cs`、`Projection/PureSourceIndexes.cs` | 源装载、基础布局和基础播放头区段，明确的 subscription 与取消 | 将源区段用于 head 活动窗和断点；基础播放头不是最终作者几何锚点 |
| `VisualWriter.cs`、`VectorOverlays.cs`、`SpriteOverlays.cs` 与 PreparedFrame 视觉快照 | 已有图元值、批次、完整仿射和渲染器 | v2 阶段句柄、capability／owning 包校验和本实例 Custom 输出 |

`ContinuousParticleSimulation.Read` 虽复制内部状态后读取，仍只推进已提交边界之后的区间；
它不提供远端 seek 的纯解析能力。包装该方法不能作为“不再从零重放”的证据。
当前 `FrameSessionFactory` 在全 v2 输入时已不启用 legacy replay；
`SemanticAnchorQuery` 的历史注释不意味着全 v2 实际仍重放，也不意味着它已经具备 M08 memo。

## 3. 候选 SDK 形状与阶段约束

保留 M07 已固定的 `IPureKaraokeEffect.Declare/Evaluate`。建议新增可选晚期接口：

```csharp
// 候选接口；尚未实现。
public interface IPureVisualEffect
{
    void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output);
}
```

建议由 `PreparedEffect` 提供两个递归只读的封闭值：`ParticleSchedule` 和
`GeometrySamplingPolicy`，默认分别为 Empty 与 Exact，并声明本实例允许的具名晚期输出。
采用普通非 virtual 的 `get; init;`，宿主在 Declare 完成后读取、验证并固定一次，
不通过可覆盖 getter 增加隐式作者回调。§9 给出待集成评审的具体类型与成员名。
不能通过保存作者 delegate、Emitter callback、OnEvent 或 Advance 恢复历史推进。

建议的计划值分工如下，名称均为候选：

| 值 | 语义 |
| --- | --- |
| `RateSegment` | 有限非负的起止速率和真实半开时间窗，表达常速或线性段 |
| `RateTrack` | 有界且有序的段集合，以及显式启用窗；区分零速率和停止发射 |
| `CursorEmissionPlan` | EmitterId、速率轨、固定 ParticleSpec、锚点类型及桥接策略 |
| `EventBurstPlan`／准备后的事件行 | 由 TimelineSelector 和 payload 条件选出的真实事件身份、描述、锚点及数量；宿主重新校验事件、时刻与 scope |
| `GeometrySamplingPolicy` | 默认 Exact；对可证明连续的明确区段提供 memo 策略及断点；策略进入固定准备身份 |

作者可以在 Declare 查询固定 TimelineIndex，使用真实事件的 C# 类型模式和 payload 条件
建立有限 Burst 行；不能保留任意回调到运行期，也不能用未经检查的裸时间代替真实事件 identity。
不另建一套不完整的 payload predicate 语言；宿主重验行中的 EventId 属于声明的 selector、
固定 subscription 和真实事件表，事件时间、occurrence 与边界优先级均由宿主取得。
计划、段、事件行、发射器和参数各有独立上限。

`VisualFrame` 提供只读源服务、已经合成的最终几何／播放头、本实例仍存活粒子、
明确窗口内的完整出生记录和解析拖尾查询。已经离开当前文字展示范围的本实例粒子仍可读取。
不能暴露上次请求时间、History、已提交 boundary、其他实例的原始输出或可写发射计划。

`VisualOutput` 在本阶段写入临时缓冲，复用已有 Dots／Segments／Trails／Sprites 描述。
宿主成功后 Complete、异常或离开阶段时 Invalidate；作者不能关闭句柄、越过包资源归属或能力声明。
文字阶段不能读取粒子；晚期视觉不能回写文字或速率轨。

纯度分析器已预留 `IPureVisualEffect` 与 `VisualFrame/VisualOutput` 名称识别，
但这不表示 API 已实现。新增封闭值、阶段句柄、构造入口、Complete／Invalidate 限制都需补正反例；
规则变化同步编译身份，避免沿用旧 analyzer 结果。

## 4. 精确出生配额与稳定身份

首先固定 v1 已有的有效语义：旧 `HeadKey` 是 track＋Binding，桥接另加 transition／flight start，
不是逐 token 的 cursor identity。因此同装载中连续词语之间传递小数配额；
真实头消失或停止发射清空配额；头存在时速率为零保留余量；桥接头使用独立配额。

候选 v2 规则：

1. 将速率计划与真实源 Current 区段、实例输出范围相交。相邻同 track／Binding 的活动区段
   合为配额 episode；锚点采样仍保留每个 cursor identity 和真实断点。
2. RateTrack 的零速率段保留累计余量；显式 disable 窗或真实 source gap 开始新 episode。
   不用“该时刻没有段”同时暗示零速率和停止，二者必须有唯一解释。
3. 使用非负精确有理累计函数 `Q(t)`。常速段是一次积分；有限线性段以起止 double 的
   精确二进制有理值积分，得到二次表达式，不能重复浮点相加。
4. 出生序号从 `n=1` 起；出生时刻是满足 `Q(x) >= n` 的最小整数微秒。
   用有理数比较和单调查找／二分求解，避免浮点开方在阈值附近漂移。
5. 连续启用的相邻半开段，恰在左段 end 达到的阈值归到下一段 start；不能重复或丢失。
   若该处 head 消失，end 出生排除并重置。下一段速率为零但左端已有阈值时，也只能兑现一次。
6. 单帧先定位 `[max(0,t-5s),t]` 附近的可能出生序号，再按 `birth <= t < death` 筛选；
   不从工程 0 时刻枚举历史。时间加法、出生数和寿命溢出均明确失败。

连续粒子的随机键至少包含实例种子、EmitterId、head／真实装载和出生序号；
Burst 还包含真实 EventId／occurrence、锚点序号与 burst 内序号。不能使用进程字符串 hash，
也不能使用请求顺序递增计数。相同输入在播放、逆序 seek、重开与撤销重建后产生同一身份。

现有 `ParticleId(instance, emitter, ulong BirthIndex)` 不能凭空容纳全部新身份。
编码前应选定无冲突的 v2 结构身份，或有证明的全局前缀编号；hash 碰撞不能静默合并粒子。
随机展开可以复用既有散布公式，但不承诺与 v1 的递增随机序列逐项相同。

出生记录与当前存活表分别定义：查询窗口内已经死亡的粒子仍有完整出生记录。
建议 v2 使用显式的有界窗口查询。若复用 `EmissionSchedule`，区间必须由固定网格和真实边界
唯一决定，不能将“上一个 UI 请求”作为区间左端，否则顺序与随机访问会不同。

## 5. 最终锚点、memo 与桥接

生产阶段固定为：源场景／基线 → 所有必要实例文字 Evaluate → 稳定合成／最终几何 →
粒子出生锚点与解析采样 → 晚期视觉 → PreparedFrame／渲染。
出生锚点补算只执行文字依赖闭包；首版不能安全拆分 C# helper 时保守执行完整文字阶段并计入预算。
应共享固定 TimelineProjection、Declare 结果和基础几何，避免每个 memo 点重新加载作者实例。

基础播放头 identity 区段只解决源 head 的存在性；最终锚点还需包含 token／cluster、line、
所有祖先组以及所有参与文字合成实例的变换。断点集合必须是整个依赖闭包的并集。
尚无连续性证明的任一依赖使对应锚点区段采用精确出生时刻 Evaluate，不能用缓存命中推断连续。

建议网格节点以 `floor(k*1_000_000/120)` 的有理定义映射到整数微秒，
实际公式与左右端点规则在实现前固定；不能重复加 8333 µs。保留源身份切换和断点两侧的真实极限，
禁止跨 identity、Binding、源 gap、瞬时几何变化或不可证明连续的区段插值。
冷热缓存、淘汰重算和不同请求方向必须使用相同策略；精确模式不能在热缓存时切成插值模式。

5 s × 120 Hz 约为 600 个区间，通常包含 601／602 个端点；断点、桥接端点和多实例调用另计。
缓存键至少覆盖固定输入／准备身份、OutputMode、受影响 RenderTarget、track、Binding、
anchor 类型、连续区段及时间节点。只有证明与设备和密度无关的数据可以跨目标复用。

桥接取前段真实 `EndUs-1` 和后段真实 `StartUs` 的最终锚点，支持有限未来查询，
允许端点位于当前存活窗口之外并另计预算。真实后继、正 gap、多个合法后继的选择、
不可用端点和 line fallback 仍遵守已有语义；不能把空档飞行冒充 Current。
桥接端点 provider 不能递归进入粒子或晚期视觉。混合 v1／v2 若依赖 v1 历史输出，必须明确
拒绝该新路径或登记独立兼容路径及成本，不能把 legacy replay 称为有界 v2 查询。

## 6. 解析运动、拖尾与预算

从出生最终锚点、固定速度／恒加速度、年龄曲线解析得到任意 t 的位置和外观。
拖尾按固定、有界采样规则计算过去位置，不读取逐帧积累的 Trail，也不依赖已淘汰帧。
自动模式由宿主生成图元；Custom 由作者使用本实例粒子绘制，宿主不再自动绘制同批粒子。

预算至少分别限制准备计划／段／事件行、发射器、出生记录、存活粒子、拖尾节点、
绘制图元、memo 条目、唯一查询时刻、文字实例调用、枚举总量、补算时间和内存。
不能只检查最终 draw 数量，也不能以“约 600 个节点”代替多轨道、多实例和断点成本上限。
超限给出准确实例／包／阶段与稳定代码；取消和失败不发布半份出生表、memo 或视觉结果。
不根据机器快慢随机删粒子，不通过放宽限制吞掉错误。

## 7. 建议实施切片及并行边界

先冻结 §9–§12 的 SDK／计划值／身份／预算接口，随后按以下四块并行；
构建、测试运行、性能采样、共享宿主接线及 Git 集成由主代理串行。

| 工作面 | 主要所有权 | 必须交给集成方的结果 |
| --- | --- | --- |
| **A：SDK／纯度／包准入** | `Scripting.Abstractions/V2`、Compiler、Core 包声明；相应 SDK／准入测试文件 | §9 公开封闭值与阶段句柄；声明、能力和生命周期校验；递归只读／句柄逃逸正反例、版本化编译身份 |
| **B：精确发射算法** | 新 `Scripting/Particles/V2`；独立配额、身份、解析运动测试文件 | `CompiledParticleSchedule` 与未求锚点的 `ScheduledBirth`；常速／线性精确积分、episode、Burst 有序索引、有限出生窗、结构身份、运动／拖尾公式；不调用完整渲染会话 |
| **C：运行时最终锚点与视觉** | 新文字 provider／memo／桥接／晚期视觉实现文件；最终锚点与生命周期定向测试 | §10 仅文字求值接缝、依赖闭包和采样策略；预算／取消事务、当前／未来端点；`VisualFrame` 投影、Auto／Custom 和 owning 资源核验 |
| **D：样例与跨层测试** | `samples/ScriptEffectsV2/{CursorTrail,VectorTrail,EventBurst}`、独立 Scripting／Rendering／Export 集成测试及验收文档 | 可实际编译的包、清单摘要与参数边界；结构／像素、随机访问、冷热／淘汰／重开、失败归属、D8 对应行证据；不改 A–C 实现来迁就测试 |

A 先交付一个可审阅的接口提交；B、C、D 可在接口固定后同时编码。B 的锚点输入是不可变值，
C 的发射输入是 B 的未求锚点出生行，二者通过接口测试夹具并行，不互相调用作者代码。
D 可以先写独立预期值和负例，接通后再运行真实包和生产路径；不能用同一生产公式计算测试期望。
`FrameEffectHost.Pure.cs`、`FrameEffectHost.cs`、`FrameSession.cs` 及共享工程／测试项目文件
由主代理作为单一编辑所有者接线；C 提交小型适配器和所需改动清单，避免四块同时改共享分派。
A 可以保留自身 SDK／分析器单元测试，B、C 保留自己的定向测试，D 负责跨层验收，互不覆盖。

M08 内部建议分三段交付，但三段全部关闭后才能关闭 M08：

- **第一段**：固定／分段常速、最终锚点 Exact 模式、自动 dots、v2 CursorTrail。
- **第二段**：有限分段线性轨、类型化 Burst 行、四类锚点／line fallback、v2 EventBurst。
- **第三段**：连续性声明与 120 Hz memo／淘汰、桥接、解析拖尾、Custom 晚期视觉、v2 VectorTrail。

当前 `EffectPackageContents` 明确拒绝非空 emitters，`SupportedV2Capabilities` 只含 M07 文字能力。
按实际接通能力逐项增加声明与验证，并核对 emitter 类型、≤5 s 寿命、控制器输出及 owning 资源。
相关作者 metadata、HTTP 合同、Worker 运行身份和版本随行为同步，不能先放开准入再留空执行器。

## 8. 关闭门禁与仍需决策的事项

§9–§12 已给出准备值成员、v2 粒子身份、出生窗口、连续性和内部调用的具体候选。
实施前仍由集成方固定能力字符串、各独立预算的数值、包 Overlay 的 `canvas` 目标增量、
PreparedFrame 承载适配和混合 v1 依赖的拒绝范围；这些选择不改变 D1–D17 或 M08 必须验收项。

验收至少覆盖阈值前后 1 µs、区段接缝、零速率／停用／头消失、同 track 连续换词、
多个 head／Binding、同边界 Burst 顺序与 payload、真实 occurrence、四锚点及 fallback、
出生／死亡、5 s 边界、未来桥接、多个后继、Ruby／连字／父组／多实例最终变换、
连续性未知走 Exact、断点左右极限、冷热／淘汰／重开／撤销／取消恢复和各预算失败。
输出一致性同时检查结构化出生／粒子／图元身份和真实像素；样例走实际包编译和生产会话。

D8 台账对 C05／C06／C07／C08／C09 与 S03／S04／S06 逐项补表达、作者入口、执行、
诊断、确定性和性能证据。任意最终几何反馈速率或旧字段用途不能表达时，记录具体场景与替代方案，
保留发布阻塞；不能用固定速率示例成功证明全部能力。性能阈值仍按阶段一记录另行测量与确认。

## 9. 待冻结的公开 C# 合同

本节全部位于 `MakoKaraGenerator.Scripting.V2`，除明确复用的既有值以外均为候选。
代码展示成员与调用形状，省略构造校验和方法体，不是可以直接复制发布的 SDK。
集合必须为非 default 的 `ImmutableArray`；record 的 init 赋值或 `with` 不绕过宿主二次校验。

### 9.1 Declare 的固定结果

```csharp
public abstract record PreparedEffect
{
    public ParticleSchedule Particles { get; init; } = ParticleSchedule.Empty;
    public GeometrySamplingPolicy GeometrySampling { get; init; } = GeometrySamplingPolicy.Exact;
    public ImmutableArray<VisualOutputDeclaration> VisualOutputs { get; init; } = [];
    // 保留 M07 的 Empty；已有作者派生记录不必增加成员。
}

public sealed record RateSegment(TimeRange Range, double StartPerSecond, double EndPerSecond);
public sealed record RateTrack(ImmutableArray<RateSegment> Segments,
                              ImmutableArray<TimeRange> EnabledRanges);
public sealed record ParticleSpec : ParticleDescription;
public sealed record BridgePolicy(TransitionMode Mode, long DurationUs,
                                  long GapLimitUs, CurveValue Curve);
public sealed record CursorEmissionPlan(EmitterId Id, RateTrack Rate, ParticleSpec Particle,
                                       CursorAnchorMode AnchorMode, BridgePolicy Bridge);
public sealed record BurstEventRow(EventId EventId, int Count,
                                   BurstAnchor Anchor, ParticleSpec Particle);
public sealed record EventBurstPlan(EmitterId Id, TimelineSelector Selector,
                                    ImmutableArray<BurstEventRow> Rows);
public sealed record ParticleSchedule(ImmutableArray<CursorEmissionPlan> Continuous,
                                     ImmutableArray<EventBurstPlan> Bursts)
{
    public static ParticleSchedule Empty { get; } = new([], []);
}

public enum GeometrySamplingMode { Exact, Continuous120Hz }
public sealed record GeometrySamplingPolicy(GeometrySamplingMode Mode,
    ImmutableArray<TimeRange> ContinuousRanges, ImmutableArray<long> DiscontinuitiesUs)
{
    public static GeometrySamplingPolicy Exact { get; } = new(GeometrySamplingMode.Exact, [], []);
}

public enum VisualPrimitiveKind { Dots, Segments, Trails, Sprites }
public sealed record VisualOutputDeclaration(OutputId Id, VisualPrimitiveKind Primitive,
                                             OverlayLayer Layer);
```

`RateTrack` 的启用窗内缺段固定为零率，窗外固定为停用；这与配额附录一致，替换 §4 中
尚待选择的旧表述。`ParticleSpec` 是 SDK 自身封闭派生，覆盖或封闭其校验方法也只能在 SDK 内做；
不接受作者派生 `ParticleDescription`。寿命满足 M08 的 ≤5 s 约束；拖尾窗非负，
实际采样夹在出生后及寿命内，并有独立节点预算。
`BridgePolicy` 的 Jump 不产生桥接 episode；Bridge 只采用真实后继，不构造任意目标位置。

`EventBurstPlan.Selector` 复用已经存在的 kind／scope／sourceText 查询。作者在 Declare 枚举
该有限键的真实事件，对 `TokenStarted`、`LinePerformanceStarted { ProgressSource: Line }`、
`ElementVisible { Element: SceneElementIdentity.Mark }` 等做类型模式匹配，再冻结 `BurstEventRow`。
这覆盖真实 payload 条件与 line fallback，而不保留谓词、TimelineEvent 对象或事件回调。
宿主从固定索引取回 EventId，重新校验种类、scope、subscription、真实 occurrence 和源文本；
行中不接收 TimeUs、ReplayReason 或用户伪造的 source-order。来源 scope 不能扩大输出绑定。
同计划重复 EventId 拒绝；需要同事件多种发射时使用不同 EmitterId，不能以字典覆盖暗示顺序。
一个计划的 Rows 顺序不决定事件顺序；宿主按既有 `TimelineEvent.Comparer` 排序。

`GeometrySamplingPolicy` 声明本实例**全部文字输出**的可证明连续区段；不在声明区段的部分走 Exact。
宿主另加源边界、装载、退出、祖先与其他合成实例的断点，不能把作者声明当成完整依赖证明。
不提供作者插值 delegate，也不允许运行中因缓存命中改策略。策略、断点与准备内容进入固定准备指纹。

### 9.2 结构身份与只读粒子视图

```csharp
public abstract record ParticleHead
{
    private ParticleHead() { }
    public sealed record Normal(CursorTrackId Track, BindingId Binding) : ParticleHead;
    public sealed record Bridge(ParticleSource.Bridge Source) : ParticleHead;
}
public abstract record ParticleOrigin
{
    private ParticleOrigin() { }
    public sealed record Continuous(ParticleHead Head, long EpisodeStartUs,
                                    ulong SequenceStartingAt1) : ParticleOrigin;
    public sealed record Burst(EventId EventId, int PlanOrdinal,
                               ParticleSource Anchor, int ParticleOrdinal) : ParticleOrigin;
}
public sealed record ParticleIdentity(EffectInstanceId InstanceId, EmitterId EmitterId,
                                      ParticleOrigin Origin);
public readonly record struct BirthWindow(long StartInclusiveUs, long EndInclusiveUs);
public sealed record ParticleBirth(ParticleIdentity Id, ParticleSource Source, EventId? EventId,
    long BirthUs, long DeathUs, Point2 BirthPosition, Vector2 InitialVelocity, ParticleSpec Spec);
public sealed record ParticleView(ParticleBirth Birth, Point2 Position, double SizePx, ColorRgba Color);
public sealed record ParticleBirthWindow(BirthWindow Window, ImmutableArray<ParticleBirth> Births);
```

连续 identity 的 head 不包含 token；出生时的 `ParticleBirth.Source` 保留当时真实 token／line
及装载。Binding 使用包含 `/reload/` 的正式身份，不能用 `SourceBindingKey` 截短后合并。
桥接结构包含真实 From／To／TransitionId／FlightRange；不能仅用旧 Binding 合并多条后继。
Burst 的 Anchor 是已经解析的 `ParticleSource`，保留每个装载；不是“本次枚举第几个”。
真实 occurrence 随 CursorIdentity 或 EventId 固定，重复装载不会制造新的源演唱计数。

身份比较及对外结构化证据按字段使用 ordinal 规范顺序；随机编码另外固定版本和长度前缀，
局部序号作为计数式随机 index。全局稠密槽位只可用于旧 PreparedFrame 适配，不能作为随机种子。
`ulong` 连续序号和全部前缀在转换前检查；规范身份始终保留结构键，不允许 hash 碰撞合并。
若准备期无法证明 Burst 所有身份槽位固定，使用独立 v2 PreparedFrame 快照承载，不能伪造旧 ParticleId。

### 9.3 晚期帧与输出

```csharp
public interface IPureVisualEffect
{
    void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output);
}
public readonly struct VisualFrame
{
    public DeclareContext Source { get; }
    public long TimeUs { get; }
    public EffectBinding Binding { get; }
    public OutputMode OutputMode { get; }
    public FinalSceneGeometry FinalGeometry { get; }
    public ParticleFrame Particles { get; }
    // Parameters / Audio / Resources / Random / Timeline / Indexes / CanvasSize
    // 仅转发 Source 的既有只读服务。
}
public sealed class ParticleFrame
{
    public ImmutableArray<ParticleView> Alive { get; }
    public ParticleBirthWindow Births { get; }
    public ImmutableArray<ParticleTrailSample> GetTrail(ParticleIdentity id);
    // internal 构造、预算与阶段 lease；作者不能延长生命周期。
}
public sealed class VisualOutput
{
    public void DrawDots(OutputId id, ImmutableArray<Dot> items, OverlayLayer layer);
    public void DrawSegments(OutputId id, ImmutableArray<Segment> items, OverlayLayer layer);
    public void DrawTrails(OutputId id, ImmutableArray<Trail> items, OverlayLayer layer);
    public void DrawSprites(OutputId id, ImmutableArray<Sprite> items, OverlayLayer layer);
    // internal 构造、Complete、Invalidate；不公开文字写入或粒子写入。
}
```

仍由同一入口对象实现 M07 `IPureKaraokeEffect`；没有文字写入时其 Evaluate 留空。
`IPureVisualEffect` 是可选附加接口，不新增第二次 Declare，也不新建第二份作者实例。
`VisualFrame` 由宿主建立，`FinalGeometry` 使用 M07 `WithTextPartitions` 完成后的结果，
保留基础字／字簇／Ruby、整行、标记、组和真实 Playback；不读取中途未合成的实例缓冲。
几何按输出绑定投影，源索引仍按 subscription 查询；两者各自保留边界。

`Alive` 只包含本实例满足 `birth <= t < death` 的粒子，不能再按“当前可见文字装载”过滤。
`Births.Window` 固定为闭窗 `[max(0,t-5_000_000),t]`，包含窗内已经死亡的全部出生；
同 t 结果不依赖上一次请求。窗口可在未来另增受限 API，但不能重用 v1 的最近提交区间。
`GetTrail` 只接受本实例当前存活的结构 identity，按其固定 TrailDuration、歌曲网格、出生点
及当前点解析，返回已经计入总预算的不可变节点；其他实例、未知或已死亡 identity 具名拒绝。
晚期代码不能查询未来粒子；未来源事件和桥接端点只由固定的源／锚点服务处理。

自动 Dots 由宿主在晚期前暂存；Custom 保留 Alive／Births／Trail，但该发射器不产生自动批次。
手工输出的 ID 必须在 `PreparedEffect.VisualOutputs` 声明，类型、层与包导出匹配；
自动发射器输出 ID 保留 EmitterId 的同名保留槽，Custom 的手工批次也必须使用另一个 ID。
所有批次按实例顺序、ordinal OutputId、批次内顺序合成，沿用 BehindText／AboveText。
Sprite 仍使用完整 Affine2；图像句柄必须属于本实例固定包修订，不接受别的实例或旧修订资源。

## 10. SDK 与运行时之间的调用接缝

下列均为 `Scripting` 内部宿主合同，不放入作者可保存的 PreparedEffect；类型名也是候选。

```csharp
// A 校验声明后，C 从固定源索引生成只读来源目录，B 不读取当前 UI 帧。
ValidatedParticleDeclaration ValidateDeclaration(PreparedEffect prepared, CompiledFrameEffect owner);
EmissionSourceCatalog BuildEmissionSources(ValidatedParticleDeclaration plan,
    SourceIndexes sources, ResolvedSourceSubscription scope, PrepareBudget budget, CancellationToken token);
CompiledParticleSchedule CompileParticleSchedule(ValidatedParticleDeclaration plan,
    EmissionSourceCatalog sources, ParticleOwner owner, PrepareBudget budget, CancellationToken token);

// B 返回尚未求最终位置的纯出生行，先算数量并扣预算，再分配和枚举。
ImmutableArray<ScheduledBirth> ReadBirths(CompiledParticleSchedule schedule,
    BirthWindow window, ParticleQueryBudget budget, CancellationToken token);
// ScheduledBirth = Id /真实 ParticleSource /EventId? /BirthUs /DeathUs /ParticleSpec。

internal interface ITextStageEvaluator
{
    TextStageSnapshot Evaluate(long timeUs, OutputMode mode, RenderTarget target,
                               AnchorQueryBudget budget, CancellationToken token);
}
internal interface IFinalAnchorProvider
{
    ResolvedParticleAnchor Resolve(ParticleAnchorRequest request,
        AnchorQueryBudget budget, CancellationToken token);
}
// ParticleAnchorRequest = ScheduledBirth.Source + BirthUs + CursorAnchorMode。
// TextStageSnapshot = 无粒子／晚期批次的完整最终文字 PreparedRenderFrame + 固定依赖身份。

ParticleFrame BuildParticleFrame(CompiledParticleSchedule schedule,
    IFinalAnchorProvider anchors, long timeUs, ParticleQueryBudget budget, CancellationToken token);
ImmutableArray<OverlayBatch> EvaluatePureVisuals(PureEffectRuntime owner,
    TextStageSnapshot text, ParticleFrame particles, VisualQueryBudget budget, CancellationToken token);
```

`EmissionSourceCatalog` 在准备期保存有限的 Current 区段、真实后继及 Burst 事件时刻的源锚点身份，
输出范围用解析后的 OutputScope 裁剪；不保存每粒子的几何或从工程 0 枚举出生。
Burst 的身份槽位可通过源装载表和 CursorIndex 在准备期冻结，最终位置留给锚点 provider。
若同 `(track,Binding)` 同时存在多个 Current 身份，首版用 `EmissionHeadAmbiguous` 明确拒绝，
保留具体 D8 场景；不能调用 `First()`，也不能未经规则就发明另一种配额 head。

`ITextStageEvaluator` 从当前 `FrameEffectHost.Evaluate` 提取文字部分，复用固定实例、Declare、
TimelineProjection、source indexes、布局与冻结退出数据；不为每个出生或网格节点重新 Load。
它禁止调用 `EvaluateEmittersAsync`、v1 `AdvanceParticles`、`ResolveBursts`、`AdvanceVisuals`
或 `EvaluateVisuals`。必要时完整执行全部文字实例，计入每次调用成本，再按固定顺序合成。
同一锚点若依赖 v1 状态写入，首版以 `FinalAnchorLegacyDependencyUnsupported` 拒绝新锚点路径；
不能据 M07 混合文字可运行推断这种未来查询已经有界。可以证明无依赖的 v1 实例不扩大拒绝范围。

最终锚点 provider 只取得 `ITextStageEvaluator`，不取得 ParticleFrame 或视觉执行器，
以类型接缝阻断递归。桥接复用真实 Transition 的两个请求：From.EndUs−1 与 To.StartUs，
未来端点独立计数；返回 unavailable 时按原有具名诊断失败，不借邻近缓存或当前新行代替。
`ResolvedParticleAnchor` 与固定出生描述交给 B 的纯运动／散布函数，得到 ParticleBirth／ParticleView。
运动和拖尾计算不能再请求文字阶段；出生点一旦取得即固定，拖尾只是该出生后的解析运动。

120 Hz 使用 `floor(k*1_000_000/120)`。每个整数微秒连续区段 `[s,e)` 附加真实 `s`、`e-1`，
跳变 d 两侧分别为 d−1、d；时间 0 不构造负侧。只在同一完整依赖区段内插值，
不给同一整数时刻制造两个含糊极限。
节点键包含固定输入／准备身份、目标与密度相关身份、OutputMode、绑定、轨道、锚点模式和区段。
M09 的 Warp 通过 provider 内部“局部几何映射 → 局部仿射 → 父链”步骤扩展；
ColorOnly 图仍不提供几何映射。缓存淘汰只使同一节点重新求值，不切换 Exact／memo 策略。

## 11. 所有权、声明、取消和预算的共同规则

| 对象或阶段 | 所有者与生命周期 | 失败与发布边界 |
| --- | --- | --- |
| 包、编译产物、ResourceCatalog | 固定包／导出／修订与宿主租约；作者只有逻辑句柄 | 校验 owning 修订，释放时不让新帧继续引用旧资源；沿用 Worker／ALC 排水规则 |
| PreparedEffect、验证后的声明、CompiledParticleSchedule | 宿主按固定输入持有；参数、seed、绑定、订阅、音频或资源修订变化重建 | Declare 和索引编译成功并完成取消检查后一次发布，不发布半份 prefix／Burst 表 |
| TextStageSnapshot、memo 节点 | 单个固定运行时与对应依赖身份；LRU 有硬容量 | 每次补算事务暂存；取消／失败丢弃该事务节点，既有完整有效节点可保留 |
| ParticleFrame、VisualFrame、VisualOutput | 本实例本次晚期调用的 lease；不可存入准备值／字段／外部捕获 | `finally` 失效；Complete 只由宿主调用；本次失败不发布出生／批次或半帧 |
| 查询预算与 CancellationToken | 当前宿主请求／准备操作拥有；作者不能构造更大限制或重置计数器 | 源查询、积分二分、BigInteger、枚举、节点和实例调用循环逐项检查；无协作作者循环由外部超时处理 |

固定 owner 采用 `InstanceId + PackageIdentity + RuntimeHash`；内部 `ParticleOwner` 另外固定
seed、inputsFingerprint 和准备指纹供随机分流／缓存校验，作者不能覆盖。诊断另外携带阶段、请求时刻及
实际失败锚点的 Binding／结构身份。B、C 抛出的错误必须有 owner，不能由当前遍历到的第一个实例代领。
建议阶段固定为 `PrepareParticles`、`ReadParticleBirths`、`ResolveFinalAnchors`、`EvaluateVisuals`；
复用已有稳定错误码，新增码先写负例与文档，取消仍传播 OperationCanceledException，不能包装成作者成功。

预算至少分成以下不可互换的计数器，数值由主代理在实施前统一配置并进入运行输入身份：

| 预算对象 | 必须限制的量 |
| --- | --- |
| `PrepareBudget` | 发射器、RateSegment、启用窗、源区段、episode、Burst 行、身份块、BigInteger 位数、内存与取消检查间距 |
| `ParticleQueryBudget` | 窗内总出生、存活数、二分比较、整数位数／运算、随机展开、拖尾节点及临时内存 |
| `AnchorQueryBudget` | 唯一查询时刻、实际文字实例调用、枚举量、未来／桥接端点、memo 项／字节、补算耗时 |
| `VisualQueryBudget` | 本实例与全部实例 draw 数、批次数、Trail 节点、Sprite 数及临时内存；沿用全局 VisualOutputBudget 的上限 |

只要任一限制超出就整次请求失败，不能随机删粒子或留下部分数组。缓存可以减少物理工作，
但不会改变规范身份、出生窗口和采样策略。耗时预算只是失败保护，不参与粒子筛选或采样精度选择。
取消前后检查与计数都在宿主内部传递；本轮不为作者增加 CancellationTokenSource 或可重置预算对象。

包声明与 Declare 结果需要两次核验：

1. 包 `requires` 与每个 controller output 的 `requires` 使用真实已接通能力；候选字符串为
   `particles.continuous`、`particles.burst`、`visuals.dots`、`visuals.segments`、`visuals.trails`、
   `visuals.sprites`，在开通对应执行器前继续拒绝。首次接入后才更新 SupportedV2Capabilities。
2. 根 `emitters` 每项使用唯一 Id、明确 `cursor`／`burst` kind 和 `maxLifetimeUs <= 5_000_000`；
   Declare 的计划必须逐项存在、kind 相同，所有实际 ParticleSpec 寿命不超过该声明。
   允许声明的 emitter 在某组参数下不产出计划；不允许未声明的新 emitter 或超期粒子。
3. 候选为 v2 的 Overlay 新增明确 `target: canvas`，坐标仍为最终世界逻辑空间，
   不以 line／tokenFamily 元数据暗示当前文字可见时才绘制。此目标仅给 Overlay，
   不扩大 Transform／Style 写权；v1 清单规则保持既有含义。
4. 自动 emitter 的同名 Overlay output、手工 `VisualOutputs` 具名 output 都必须在 controller
   导出中存在；同一 Id 不得跨自动与手工使用。Declare 的层和 primitive 固定，实际写入再核验。
   `dependsOn` 只接受同导出中已存在且阶段方向合法的声明，不让晚期输出成为文字或发射轨的输入。

`Emitters`、`VisualOutputs` 和 output capability 都是准入上限，不授权其他实例、绑定或资源。
HTTP DTO 如仅转发包的固定声明可以保持现有入口；实际暴露字段变化仍需重新 metadata／合同审阅，
不能从“SDK 包已可编译”推断 Server／Worker、作者 UI 或浏览器传输已经验收。

## 12. 四块交付的对接顺序与验收分配

| 顺序 | 可并行工作 | 合并前交接物 |
| --- | --- | --- |
| 接缝冻结 | A 固定公开值／句柄，B 固定 ScheduledBirth 与精确计数，C 固定 text-only provider，D 固定输入与独立期望 | 本文候选名、默认值、范围、错误和预算数值无矛盾；接口提交后各块才依赖生产类型 |
| 独立实现 | A 的只读／声明校验，B 的算法，C 的 provider／memo／晚期执行，D 的三样例与跨层用例 | 分别按路径提交；共享宿主与项目文件只交主代理接线；M07 契约发布前只准备文档与设计 |
| 首次集成 | 主代理依次接 Declare 编译、文字快照、出生窗、最终锚点、自动粒子、晚期视觉 | 单时刻全链路结构证据；文字补算调用计数证明无粒子递归和重复 Declare |
| 完整 M08 | A–D 定向负例与共同协议／渲染回归汇总，主代理串行构建／测试／匹配运行 | M08 验收行全部通过，D8 C05–C09、S03／S04／S06 分项记录；不将 M09 Paths 或完整 M11 作者流程提前标绿 |

A 覆盖封闭值递归只读、错误 lifetime／rate／枚举／资源、无声明输出、句柄构造／逃逸／Complete
越权及编译身份变化。B 使用人工可推导的小有理数结果和独立简单二分 oracle，覆盖配额附录所有
微秒边界、head／gap／reload、多出生、Burst 次序、溢出和无历史枚举；不能用“顺序与随机都错得一样”证明正确。

C 覆盖 BaseOnly／cluster／Ruby／父组／多实例的最终锚点、装载复用与退场不制造 Current、
断点左右微秒、未知连续性 Exact、未来桥接多个后继、LRU 淘汰、取消恢复、独立 owner 和预算失败。
补算节点、文字实例调用和 Declare 次数均作结构断言；像素另走真实 Vulkan／Skia 生产渲染。

D 的 CursorTrail 覆盖常速／分段线性与 Jump／Bridge；VectorTrail 使用 Custom、完整出生窗口及
解析 Trail 画 Dots／Segments／Trails，再用固定 owning ImageHandle 补 Sprite 仿射正反例；
EventBurst 覆盖真实 payload、scope、occurrence、四类锚点、line fallback 和同边界优先级。
三例同时覆盖近端／3 min／更远时刻、顺序／逆序／重复、冷热／淘汰／重开／撤销、death=t、
5 s 上限、多个 Binding／reload 及参数／seed 重建。结构 identity、出生表、图元与像素分别比较。

本轮只补研究文档，没有新增 M08 生产代码、构建、测试、运行、像素或性能通过结论。
