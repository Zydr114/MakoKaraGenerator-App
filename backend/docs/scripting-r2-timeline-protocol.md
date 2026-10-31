# R2 源事件、订阅与历史协议实施记录

> 2026-10-03；程序 **0.9.5**，工程／歌词／模板 schema **21／15／3**。
> 2026-10-08接续：0.45.5已按ADR-0175完成F7a历史前缀优化，每代次一次完整校验，查询共享只读前缀，Records显式访问按需物化；同刻逐条可见及公开构造严格校验保持。实际合同和成本边界见[历史前缀](scripting-history-prefix-contract.md)。下文0.9.5状态保留历史口径，F7b诊断优化未由此完成。
> 按已接受 R0 合同 §3～4／ADR-0071 实施。本批建立真实固定事件表和只读历史，**没有接通 OnEvent、120 Hz 会话或 callback 效果**。完整出口仍按 [R2 记录](archive/research/scripting-r2-sdk-geometry-progress.md#4-r2-尚需完成的出口)及 R3～R8。

## 1. 当前可调用接口

| 位置 | 接口 | 实际职责 |
|---|---|---|
| 中立 SDK | `TimelineEvent`／`TimelineEventKind` | 十三类封闭事件，强类型适用身份，不提供可写源对象 |
| 中立 SDK | `SourceSubscription` | BoundSources／Branch／Project，独立于实例输出绑定 |
| 中立 SDK | `EventRecord`／`TimelineHistory`／`SourceFilter` | 不可变已发生前缀及真实逐字起唱查询 |
| Scripting | `TimelineProjection.Create(snapshot, reason, audio, cancellationToken)` | 从同一源目录与共享场景计划生成固定事件表 |
| Scripting | `TimelineProjection.HistoryAt(boundaryUs)` | 受控投影的边界前缀，不能推进或调用作者实例 |
| Scripting | `ResolvedSourceSubscription.Create(timeline, binding, subscription)` | 验证和解析固定订阅；绑定验证也供纯帧工厂复用 |

投影器和订阅解析器属于宿主程序集，不进入作者 SDK。SDK 保持仅 BCL 依赖，没有 Core／Skia／Avalonia 引用；作者不能派生新事件种类，新增 kind 必须提升 SDK 合同。真正 Roslyn 编译的消费者已验证现有事件可用，**该消费者测试不等于 RepeatEmphasis 生命周期已经接入**。

## 2. 演唱、装载与可见分开

`TokenStarted`／`TokenCompleted` 只从有效源元素的原始 StartUs／EndUs 生成。多个装载、前奏、槽位提前显示、静态绘制和重放不会新增源演唱次数。缺时间字、注音、翻译关联显示、组和提示标记没有假 TokenStarted。

`LinePerformanceStarted`／`LinePerformanceCompleted` 使用首个定时元素至最后定时元素的包围区间；全无逐字时间才使用原行区间，并标 `ProgressSource.Line`。两种时间均缺则无演唱事件；行依据不伪造逐字起唱。

`BindingEntered`／`BindingLeft` 与 `ElementVisible`／`ElementHidden` 来自共享 `LyricSceneSampler` 在候选边界前后两个整数时刻的真实状态。候选边界由原行／源元素、片段、槽位显示计划和前奏步骤提供，**不按 UI 刷新差分**。可见表示进入逻辑展示区间；没有用入场动画 alpha、遮挡或像素数量重新定义可见。

节点身份为封闭判别：

- Lyric：真实源文本或注音 NodeId、当前 FrameLine／BindingId；注音另带 RubyId。
- Mark：真实文字标记 NodeId、CueId 及目标装载；不拥有 TokenId。
- Group：真实组／槽位 NodeId；仅保留定义中适用的 BranchId，共享组允许为空，无假 LineId。

同一共享父组只产生一个可见身份；同一真实文本的不同装载按 BindingId 区分。准备变为演唱保持 BindingId，因此不再触发一次 BindingEntered。提示在准备终点隐藏，目标连续显示。

## 3. 前奏、音频与光标

`LeadInStarted` 位于准备起点；中间 `LeadInStepChanged` 为已过步骤 1～N−1；最后归零只用 `LeadInCompleted`，位于起唱边界。步骤使用与 Core 相同的商／余数整数公式：

```text
S + floor(D / N) * k + floor((D % N) * k / N)
```

事件固定真实 Cue、连续目标装载及标记数量；不准将任意区间内时刻冒充精确步骤。构造器拒绝空目标、禁用／失效前奏、错误目标关联、非法步骤及非有限显示状态。

`AudioFeatureTick` 只来自宿主已提供、已完成分析序列中的真实 hop。未分析、无音频或失败时不造零值／假 tick；仅有分析修订、样本和全工程顺序，没有歌词身份。**PCM／节奏分析器及 required-audio 导出门禁仍在 R5**。

`CursorIdentity`／`CursorTransitionEvent` 已声明强类型源／装载／演唱区间与转换原因；行依据明确不含 TokenId／OccurrenceId。当前投影器**尚不生成光标转换事件**，后继规则待用户裁决重叠句接续。0.9.8 已接通最终播放头路径及独立未来端点，不把本事件身份记录冒充实际后继；两者的关系及剩余 R2／R6 工作见 [播放头合同](scripting-r2-playback-geometry-contract.md)。

## 4. 身份与边界顺序

OccurrenceId 仍由真实分支／行／元素／区间编码。EventId 包含 kind、TimeUs、真实演唱／装载／节点／Cue／分析修订及适用子序号；不使用随机 GUID 或 UI 帧号。ReplayReason 是 Initial／Seek／Loop／InputsChanged／Export／AnchorQuery，**不进入身份**。同一输入重建保留全部身份及源顺序。

同边界排序按已接受合同：

1. 完成／离场：TokenCompleted、LinePerformanceCompleted、BindingLeft、ElementHidden、LeadInCompleted。
2. 进入／准备／输入：BindingEntered、ElementVisible、LeadInStarted、LeadInStepChanged、AudioFeatureTick。
3. 源起唱：TokenStarted、LinePerformanceStarted。
4. 光标转换。

每组按分支、行、元素源顺序及 ordinal EventId。纯组／全工程输入缺行／元素时用明确的 −1 顺序层，不用假源 ID。重复 EventId 拒绝，不静默去重；构建在源行、分支、前奏和候选边界检查取消，保留 OperationCanceledException。

## 5. 订阅范围

输出绑定仍是原六种，源订阅单独配置。完整 ContentCatalog 和 TimelineHistory 不以输出范围裁剪。

- Project：订阅工程源及真实展示事件；可以跨分支计数，但以后 writer 仍只允许写输出绑定。
- Branch：订阅指定真实源分支，允许与输出分支不同，不替换输出绑定。
- BoundSources：工程／分支／行／精确元素／片段成员按已有合同解析；没有翻译自动扩展。
- 节点含后代：结合固定父树与真实槽位装载，解析整个时间线相关源，而不是仅当前可见句。槽位不含后代时为纯组，无源起唱。
- 单独注音节点：关联其基础元素和源行装载，不订阅句内其他基础字；基础文本和注音的可见身份分别筛选。
- 提示组或单独标记：读取真实可见及所属前奏事件，没有虚构目标字或源装载。需要目标源历史时显式使用 Branch／Project 订阅。
- 光标转换的 BoundSources 要求两个源端点都在范围内；扩大源订阅不代表后续粒子可以跳出输出绑定。
- 音频 hop 是全工程只读输入，对各源订阅均可用。

不存在、空、重复绑定及未知订阅分支报告具名错误，不自动转成工程范围。JSON 订阅／实例持久化仍待 R4；当前解析器不是可用的界面绑定编辑器。

## 6. 历史前缀

`TimelineHistory` 保存固定 ContentCatalog、LastBoundaryUs 和不可变 EventRecord 集合。未来记录、重复／乱序及伪造源元素或演唱区间拒绝，不排序修复。查询 `GetStarts([start,end),filter)` 只返回已有真实 TokenStarted，filter 为 Project／Branch／Lines／Tokens；未知、空或重复筛选身份明确失败。

边界 b 的完整前缀包含 TimeUs≤b，查询本身仍使用半开区间。请求未来区间不会暴露 b 之后的事件；无逐字时间的行也不会得到一条假的逐字记录。

当前 `HistoryAt` 是**固定表的受控前缀构造器**。未来 R5 会话负责按真实处理进度提供 OnEvent／Advance／FrameContext 历史，不能将任意调用 HistoryAt 当成作者状态已重放，也不能以未来事件表冒充已发生历史。本批不修改 IKaraokeEffect、PureFrameSourceFactory 或帧上下文的两阶段运行范围。

## 7. 验证与剩余工作

新增 **31 项 Scripting 测试**：真实源／行依据／共享半开边界、静态和隐藏分支、三个前奏／槽位／跨片段案例、五种重放原因、音频 hop／取消、历史与伪造输入、订阅／节点／标记／注音／光标范围、封闭 API 和真正编译消费者。

显示事件的装载集合在精确边界及 24／30／60 fps 邻近时刻直接对照生产采样器；前奏提前显示没有额外 TokenStarted，准备与起唱 BindingId 连续，缺时间翻译无假演唱。此验证证明事件表正确，**不证明尚未接入的 120 Hz 状态积分、callback 像素或粒子确定性**。

```bash
dotnet test tests/MakoKaraGenerator.Scripting.Tests/MakoKaraGenerator.Scripting.Tests.csproj -m:1 --filter 'FullyQualifiedName~Timeline|FullyQualifiedName~SourceSubscription'
dotnet build MakoKaraGenerator.slnx -m:1
dotnet test MakoKaraGenerator.slnx --no-build -m:1
```

日志 `/tmp/makokara-r2-events-{focused,build,tests}.log`，全量结果记入 roadmap §18.34。工程、歌词、模板 JSON 结构和生产画面均未改变；精确程序版本门禁继续按 0.9.5 工作，不新增旧工程转换。

下一步仍是 R2：六阶段上下文／基线／诊断、其余 writer、最终光标／映射和未来语义查询；R3 材质／图、R4 包与数据、R5 实例调度及真实 callback、R6 粒子、R7 工作区、R8 六包成片保持原范围。性能基准后置。
