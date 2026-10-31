# 事件 Burst 与固定粒子会话实施合同

> 2026-10-03；用户已确认 §5／§6 全部规则。施工基线 0.10.0；本批 SDK 来源结构不兼容，程序交付为 0.11.0，持久 schema 21／15／3 保持。

## 1. 已确定的范围

- 只在 OnEvent 的 `EventContext.Bursts.Queue` 登记当前真实事件的不可变意图，不由作者提供任意EventId或出生时间，不在事件回调里获取最终几何。
- 每个源事件仍派发一次；订阅和输出绑定保持分开。共享状态／所有文字输出完成后，按当前最终几何解析Burst，然后进入粒子准备／视觉推进／绘图。
- 同一 `(InstanceId, EmitterId, BirthIndex)` 身份序列用于连续和事件出生，不建立第二套画布。出生颜色／运动／拖尾由同一宿主解析，同一世界坐标规则及现有预算生效。
- 事件时刻出生在 `[b,b+life)` 存活；当前帧Particles包含它，上一已完成 `[a,b)` 的EmissionSchedule不包含它。下一提交区间记录该出生，初始[0,0)仍为空。固定区间完整出生包含区间内已死亡对象。
- 分数帧、暂停重绘不重复发射；seek／loop／导出／参数改变重新建立独立会话并重放。AnchorQuery不创建或解析粒子，也不能因其跳过视觉而阻断语义／文字端点。
- writer只在所属事件回调有效，错误／未声明Overlay或包输出、取消／非法身份／有限值和预算归真实实例并阻止导出；失败清理未发布队列和本会话资源。不改生产JSON／schema、程序集或第三方依赖。

## 2. 实施接缝

新增独立Burst描述（只保存数量、锚点和固定粒子外观／运动；不使用rate冒充瞬时数量），复用现有ParticleMotion／age curves／Layer等固定值。锚点是中立身份或明确当前播放头选择，不接受C#几何委托或原生对象。

宿主保存本边界事件意图，事件回调结束后固定，文字完成再解析。当前完整粒子与上一完成区间出生分别保留；边界出生先计入新半开区间的记录预算，并在下一提交时与连续出生合并一次。稳定顺序及同身份重复行为按已确认的 §5／§6 执行。

连续发射描述不因Burst自动创建；没有EvaluateEmitters的效果也能事件发射，停止连续发射不会删除仍存活的Burst粒子。

## 3. 计划验证

真实公共C#源码事件调用、源码路径诊断、同事件去重和过期writer；起唱与前奏／可见分开，几何取全实例最终文字；同时多头／不同绑定、世界出生位置／旧装载保留；整数事件、半开出生／死亡／短命完整记录、连续与Burst身份不冲突；暂停／24、30、60fps／任意跳转／中段／独立端点／取消重试；存活／拖尾／记录／绘制全部实例预算、实际Skia像素／MP4／Alpha及错误保护原输出。

## 4. 保持未完成的范围

Bridge及重叠后继待答、sprite／独立线段拖尾、实际图像映射／目标内捕获、正式父子渲染协调器、材质C、工程／模板／音频持久事务和脚本工作区继续原路线。完成本项不关闭完整R6或整个脚本系统。

## 5. 已确认的数量、重复与锚点

1. 同一意图匹配多个锚点时，每个锚点独立产生 count 个粒子。
2. 同事件同发射器多次 Queue 只保留最后登记；不同事件和不同发射器独立。
3. 明确提供 `BurstAnchor.SourceToken(TokenId)`、`SourceLine(LineId)`、`TextMark(NodeId)` 和 `BindingCursors(CursorAnchorMode)`。固定文字使用基础文字 Advance 中心，再通过最终矩阵映射。源身份可匹配多个真实装载，所有输出受实例绑定限制；事件订阅不授予额外输出权。

## 6. 已确认的边界与真实来源

1. 指定元素／行／标记无可用当前装载或最终几何时，`BurstAnchorUnavailable` 失败并阻止导出。不存在的源身份是 `UnknownBurstSource`，超出输出权是 `BurstTargetOutsideBinding`。不偷用上一帧位置。绑定全部播放头无人演唱时合法零出生；后端不可用仍明确失败。
2. 同刻先按真实事件次序处理 Burst，各事件内按实例数组、EmitterId 和真实装载稳定排序；然后处理同刻连续到期出生。两者共享 BirthIndex，Burst 不改变连续配额。
3. 采用封闭 `ParticleSource.Cursor(TrackId, CursorIdentity)`／`Token(TokenFamilyTarget)`／`Line(LineTarget)`／`TextMark(TextMarkTarget)`。ParticleSample／ParticleBirthSample 保存 Source，BindingId 从真实来源读取，TrackId 仅 Cursor 有值，其余 null。Burst 记录真实当前 EventId，连续出生为 null。不创建假光标或假演唱元素。

## 7. 当前代码路径

- `EventContext.Bursts` 只在本次 OnEvent 回调可用，`BurstWriter.Queue` 不接收任意事件或时间。声明 Overlay，包须声明同名 Overlay 输出；完成后及失败后关闭 writer。独立 AnchorQuery 运行相同 OnEvent 状态并校验登记，但不解析意图、不分配粒子。
- `ParticleDescription` 是 SDK 封闭物理描述基类，CursorEmitterDescriptor／BurstDescriptor 共享寿命、颜色、速度、年龄曲线与拖尾；Burst 不借发射率冒充数量。作者无法给会话存入可执行物理描述子类。
- `FrameEffectHost.Bursts` 保留本边界事件意图，最终文字完成后使用实际绑定范围解析。源订阅与只读内容不扩大输出权限；Token 绑定不能发射整行或标记锚点。
- 固定模拟的边界出生缓冲立即生成本帧粒子，却只在下一提交的 [b,c) 记录一次。短命粒子死亡后，缓冲仍保留其完整出生。连续求值副本复制该缓冲，统一执行每区间记录预算，不修改主会话。
- 同一个 emitter 仍是一条 Overlay 批次，沿用最近登记的 Layer；需要不同层级用不同 EmitterId。每次出生的物理外观独立固定，不因停止连续发射删除 Burst 粒子。
- 最小公开源码例子：`samples/ScriptEffects/Examples/EventBurst.cs`。它是作者接口例子，读取显式参数；没有新增默认安装包或 UI 入口。原 CursorTrail 包继续用同一 SDK 编译运行。

## 8. 验证与剩余工作

新增 Rendering24／Scripting3／Export4，八项目2244项全通过（82／975／64／87／614／159／179／84），构建0警告／0错误。真实公开起唱源码、四类来源、多事件／锚点和同刻顺序、源订阅与输出隔离、最终几何、极短寿命／半开记录、作者完整视图、24／30／60fps／暂停／重放／取消、所有实例预算及真实像素通过；MP4／Alpha同帧通过，300000μs锚点缺失及400000μs存活超限中止真实编码并保留原输出。最初两条导出断言沿用连续出生“严格晚于起唱”的条件，已改为包含实际 Burst 起唱边界；最终全量通过。

最终日志 `/tmp/makokara-burst-build-final.log`、`/tmp/makokara-burst-tests-final.log`；详细统计和运行证据由 roadmap §18.54 记录。完整 R2～R8、既有待答规则及 §4 的未完成范围保持。
