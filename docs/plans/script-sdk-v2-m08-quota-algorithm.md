# M08 精确配额与出生身份算法附录

日期：2026-11-04。**算法首批实现与有限单元测试已完成；本附录保留设计推导，不替代 M08 完整验收。**
实际实现位于 `Scripting/Particles/V2`，证据见[阶段一执行记录](script-sdk-v2-phase-one-progress.md)。
本附录补充[实施接续笔记](script-sdk-v2-m08-implementation-notes.md) §4，服从
[主计划 §5.5](script-sdk-v2-render-pipeline-plan.md#55-粒子事件桥接与采样预算)。
本轮只读核对 `ExactEmissionQuota`、`ContinuousParticleSimulation*`、`ParticleDescription`、
`ParticleMotion`、`ParticleSample`、`ParticleSource` 与 `EmissionSchedule`；不替代 M07 的验收。

## 1. 建议在编码前冻结的公共值

以下名称尚不是已存在的 API。`PreparedEffect` 建议增加普通 `get; init;` 的计划属性，
默认 `ParticleSchedule.Empty`；宿主在 Declare 成功后验证并固定一次。它无需可覆盖 getter，
因此不会增加宿主隐式执行作者回调的入口。

```csharp
// 候选形状；构造器必须验证，集合使用 ImmutableArray。
public sealed record RateSegment(TimeRange Range, double StartPerSecond, double EndPerSecond);
public sealed record RateTrack(ImmutableArray<RateSegment> Segments,
                              ImmutableArray<TimeRange> EnabledRanges);
public sealed record ParticleSpec : ParticleDescription;
public sealed record CursorEmissionPlan(EmitterId Id, RateTrack Rate, ParticleSpec Particle,
                                       CursorAnchorMode AnchorMode, BridgePolicy Bridge);
public sealed record ParticleSchedule(ImmutableArray<CursorEmissionPlan> Continuous,
                                     ImmutableArray<EventBurstPlan> Bursts);
```

- `RateSegment` 使用真实、正长、非负时间半开窗；端点速率有限且非负。相等端点为常速，
  不相等为线性速率。数学上的右端值用于积分；该段的展示时间仍右端半开。
- 段按开始时间有序且不重叠；启用窗也有序且不重叠。相接启用窗合并，不制造零长停用。
  **启用窗内没有速率段明确表示零速率**；启用窗外才表示停用。这固定了缺段的唯一解释。
- `ParticleSpec` 可以在同一 SDK 程序集中继承现有封闭 `ParticleDescription`，复用其
  外观／速度／加速度／年龄曲线／图层字段，不带连续率或 Burst 数量。
  新验证额外要求 `1 <= LifetimeUs <= 5_000_000`；拖尾查询仍夹到出生后及当前寿命内。
- 一个连续计划固定一个 ParticleSpec；描述不随 UI 请求改变。声明多个物理描述时使用
  明确的多个计划／EmitterId，避免出生后再读取“最新描述”。
- 准备期分别限制计划数、段数、启用窗、source 区段、episode、Burst 行与整数位数。
  `double` 到精确数的转换必须重新验证有限非负，不能把旧 helper 对调用方验证的依赖带入新 API。

## 2. Head、episode 与源身份

配额 head 沿用已经存在的有效分组：普通头为 `(CursorTrackId, BindingId)`；
桥接头另含 `(TransitionEventId, FlightStartUs)`。**普通 head 不含 TokenId 或 CursorIdentity**。
后两者仍决定出生瞬间的真实来源和最终锚点，不能因配额合并而丢失。
这里的 BindingId 必须使用真实装载身份，保留生产索引的 `/reload/` 区分；
`LyricSceneSampler.SourceBindingKey` 仅用于查回布局调度，不能拿它合并 head 或随机键。

宿主从固定源 Current 区段和输出绑定构造 head 存在窗，并与 RateTrack 的启用窗相交。
同 head 相接的存在窗合并为最大连续 episode `[s,e)`；episode 开始时配额为零。

| 边界 | 配额 | 出生来源 |
| --- | --- | --- |
| 同 track／Binding 连续换词，前词 end 等于后词 start | 保留 | 边界出生读取后词真实身份 |
| 相接速率段、速率不连续或进入零速率 | 保留 | 按出生时刻的源身份取锚点 |
| 真实 source gap，哪怕只有 1 µs | 清空，下一窗重新从 n=1 | 不在 gap 内伪造 Current |
| 停用后再次启用 | 清空 | 新 episode |
| 普通头与桥接头，或不同后继桥接 | 各自独立 | Bridge 保留真实转换身份 |
| Binding 改变或同一源重新装载 | 新 head／episode | 使用新装载身份 |

宽 `subscription` 允许查询更多源，不扩大出生输出 head；应使用已经解析的输出绑定再裁剪。
source token 之间的边界仍保留为锚点断点，不因为 episode 合并而跨 token 插值。
若同一 `(track,Binding)` 在同一时刻含多个 Current 身份，不能 `First()` 选取一个：
旧出生路径使用 `SingleOrDefault`，也不能证明这种输入已经获得多头语义。M08 应先具名拒绝
这种歧义并保留 D8 项，或在另行冻结的规则中引入真正的子头身份。

## 3. 无状态精确积分

在宿主内部使用规范化 BigInteger 有理数：分母严格为正，零统一为 `0/1`。
可复用 `ExactEmissionQuota.PerMicrosecond` 的 IEEE-754 解码方法；不要复用其可变
`Schedule._next` 或把 `Remaining` 当成新运行状态。负零规范成零，NaN、Infinity、负率拒绝。

将某原始速率段 `[a,b)` 的端点转换为每微秒速率 `p0/V`、`p1/V`，令 `D=b-a`。
从段起点积分到偏移 `x` 的精确增量为：

```text
G(x) = (2*D*p0*x + (p1-p0)*x*x) / (2*D*V),  0 <= x <= D
G(D) = D*(p0+p1)/(2*V)
```

下降线性段的二次项可以为负；端点非负保证完整积分函数仍单调不减。
所有乘法先转 BigInteger，禁止先用 long 计算 `2*D`、`x*x` 或累计分子。

准备原始 RateSegment 末端的精确累计量，形成非递减的前缀表 `F(t)`；
缺段按零率，使用二分定位原始段并计算 `G`。episode 中使用：

```text
Qepisode(t) = F(t) - F(s)
```

不要在每个 token 裁剪点重新累加局部梯形。使用原始段前缀再相减，可以让同 head
跨 token／零率的结果相同，并避免不必要地累乘各裁剪区间的分母。
在完整原始线性段末端使用化简的 `G(D)`，不保留本可约掉的 D 分母。

建议内部接口：

```csharp
CompiledRateTrack Compile(RateTrack plan, PrepareBudget budget, CancellationToken token);
ExactQuota IntegralAt(long timeUs);             // 固定原始段前缀 F(t)
BigInteger CountThrough(CompiledEpisode e, long timeUs);
long SolveBirth(CompiledEpisode e, ulong sequence); // sequence 从 1 起
BirthWindowResult ReadBirths(BirthWindow window, QueryBudget budget, CancellationToken token);
```

`Compile` 只生成有限段／episode／前缀，不生成全部粒子。每次查询有自己的预算和取消，
成功后才能发布表或 memo；查询方向和缓存命中不改变积分及取整规则。

## 4. 整数阈值、半开窗与出生窗口

第 n 个出生为满足 `Q(t) >= n` 的最小整数微秒，`n=1,2,...`，且必须 `s <= t < e`。
没有 n=0 的初始粒子。高于每微秒一粒子的速率可能使多个不同 n 在同一整数时刻出生。

定义 episode 的累计已兑现数：

```text
C(t) = 0                                  if t < s
C(t) = floor(Q(min(t, e-1)))                otherwise
TotalBirths = C(e-1)
```

**总数必须取 `floor(Q(e-1))`，不能取 `floor(Q(e))`**。
因为连续阈值即使出现在 `(e-1,e)`，向上取整也得到被半开窗排除的 e。
长度为 1 µs 的 episode 因而没有连续出生；这与现有 `NextOffset < duration` 一致。

查询半开出生窗 `[a,b)` 时，只定位序号 `C(a-1)+1 ... C(b-1)`；
查询闭窗 `[a,b]` 时，定位 `C(a-1)+1 ... C(b)`。a=0 时将左计数直接视为 0。
建议新的 `BirthWindow` 明确使用两个包含端点或显式端点种类，避免为包含 t 写 `t+1`。
现有 `EmissionSchedule` 固定是半开窗且表达最近提交区间，不适合直接冒充任意 v2 查询窗。

固定寿命 L 的粒子在 t 存活，当且仅当：

```text
birth <= t && t-birth < L
birth ∈ [max(0, t-L+1), t]
liveCount = C(t) - C(t-L)   // 负时间的 C 为 0
```

先按这些计数和 Burst 的固定行计算查询请求量，超过出生／存活预算立即失败，
再枚举出生和求最终锚点。不要先分配全部历史粒子再截取最近 5 s。
出生记录窗另外定义，必须包含窗内已死亡粒子，不能从存活表反推。

常速段可直接做有理数除法向上取整；有限线性段对整数 x 二分，判断只用精确整数交叉乘法。
可先以原始累计末端值 `lower_bound` 找首个达到目标的段；平坦平台取最早达到处。
解算允许触及原始段右端以归入下段的 start，但 episode 末端仍只允许到 `e-1`。
积分归属段和出生时刻的源身份段是两个概念：在零率后段 start 兑现的阈值只能记录一次。
不使用浮点开方，也不以浮点误差 epsilon 改写出生边界。

`SolveBirth` 的简单基线可直接对 `[s,e-1]` 二分，每次调用原始前缀表；最多 63 次整数时间比较。
正式优化可定位原始速率段后再二分，但必须与简单基线在所有阈值处相同。
比较次数、BigInteger 位数和枚举量应计预算，不能只限制最终 draw 数。

## 5. 身份及现有运行时承载

建议以**结构身份为规范身份**，不把它 hash 后截成 ulong：

```text
ParticleIdentity = (InstanceId, EmitterId, Origin)
ContinuousOrigin = (HeadKey, EpisodeStartUs, SequenceStartingAt1)
BurstOrigin      = (EventId, PlanOrdinal, AnchorKey, ParticleOrdinal)
```

`HeadKey` 分 Normal／Bridge 封闭类型；`AnchorKey` 使用已经存在的真实
ParticleSource（或等价的明确身份值），不使用“此请求第几个锚点”。
Burst 的 PlanOrdinal 是准备计划中的稳定序号，EventId 和来源 scope 必须由宿主重新验证。
ParticleOrdinal 从零开始；连续 Sequence 从一开始，两者不得共用未说明起点的 ordinal。
同时间多个事件仍保持已固定的事件优先级／源顺序；同时间出生再用明确 ordinal comparer
稳定排序。字典内部哈希只用于查找，不作为序号、随机键或对外身份。

现有 `ParticleId(instance,emitter,ulong BirthIndex)`、`ParticleSample`、
`ParticleBirthSample` 与 PreparedFrame 视觉快照形成兼容接缝。推荐：

1. v2 的作者只读视图公开结构身份；v1 三元组的含义不变。
2. 若继续用现有 PreparedFrame.Particles 承载渲染视图，准备期按固定结构顺序为每个
   episode 和已冻结的 Burst 锚点行分配全局前缀槽位：
   连续块为 `BirthIndex = prefix[originBlock] + Sequence - 1`，
   Burst 锚点行为 `BirthIndex = prefix[originBlock] + ParticleOrdinal`。
   连续块总数直接用 `C(e-1)`；Burst 块用已验证的固定 count，不枚举历史出生。
3. 所有前缀以 BigInteger 求和，转换到 ulong 前检查整个映射无溢出；将规范结构键与
   稠密编号的关系保存为有限块表。不能依赖当前存活表重新编号，也不能碰撞后静默覆盖。
4. 若 Burst 锚点槽位无法在准备期由真实源冻结，先用独立 v2 粒子视图／承载；不要把
   未证明无冲突的值塞入现有 BirthIndex。最终锚点位置可以晚求值，身份槽位必须先固定。

随机展开可复用现有 `DeterministicRandom` 和散布公式，但 stream key 应包含长度编码的
发射器／SeedStreamKey／规范 head／episode 或 Burst 身份，index 使用局部出生序号。
版本化编码和分流轴，明确 Ordinal／InvariantCulture；不使用 `GetHashCode()` 或请求顺序。
这样无关 head 的增删不会仅因全局前缀变动而扰动已有 head 的散布。

## 6. 溢出、运动与实施负例

- `D=end-start` 在验证 `0<=start<end<=long.MaxValue` 后安全；二分中点使用
  `lo+(hi-lo)/2`。不构造 `timeUs+1`，不把 Int64 最大时刻环绕成负数。
- 寿命末端采用 `checked(birth+L)`；在已知块的最后出生时间可提前检查。
  溢出具名拒绝，不饱和成 long.MaxValue，也不丢弃该出生。序号／前缀同样拒绝溢出。
- 位置复用 `birthPosition + velocity*seconds + acceleration*seconds²/2`；
  年龄使用 `(t-birth)/LifetimeUs`，尺寸与 alpha 复用 ParticleAgeCurve，重新检查最终有限值。
  这些公式与值类型可以复用；旧 `_particles`、`_boundary`、NextIndex 和累计 Trail 不复用。
- 解析拖尾使用固定歌曲网格、出生端点及查询末端，夹到
  `[max(birth,t-TrailDuration),t]` 后去重。不得读取上次请求积累的 Trail。
  例如 120 Hz 网格用现有 `SemanticSimulationGrid.TimeAt` 的分段整数公式，避免反复加 8333。
- 桥接使用独立 episode，取真实 `From.EndUs-1`／`To.StartUs` 最终端点；
  每个合法 transition 分开编号，未来端点另计查询预算，不能递归进入粒子／晚期视觉。

编码验收最少包含：常速 60/s、非整二进制输入、上升／下降线性、零率平台、相接段的整阈值、
阈值位于末端最后一个微秒的分数处、1 µs episode、同 head 连续换词、1 µs gap、显式 disable、
相同 source 重载、新 Binding、并发 head、桥接分叉、高率同微秒多出生、death=t、
出生窗内已死亡记录、取消、长 seek 的有界枚举、long／ulong 溢出、冷热与乱序结果一致。

这里只给出数据／算法建议；实际最终锚点、像素、作者包及完整 M08 的关闭证据仍需后续实施采集。
