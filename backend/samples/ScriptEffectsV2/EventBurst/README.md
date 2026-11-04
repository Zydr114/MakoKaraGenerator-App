# SDK2 真实事件 Burst

包身份 `makokara.v2.event-burst`，入口 `EventBurst`，导出 `event-burst`。要求 Vulkan/GPU。
在 `Declare` 读取固定 TimelineIndex，对真实 C# payload 做条件选择，再冻结 EventId；没有 OnEvent、作者时间戳或逐帧出生计数。

- `anchor=token` 使用 TokenStarted 的 SourceToken；`line` 使用 LinePerformanceStarted 的 SourceLine；`mark` 只匹配 `ElementVisible { Element: SceneElementIdentity.Mark }`，使用 TextMark；`cursor` 对 TokenStarted 和明确 `ProgressSource.Line` 的行回退使用 BindingCursors。
- `scope=project` 查询完整订阅，`firstBranch` 固定源目录首分支，`firstOccurrence` 固定首个真实 TokenStarted 的 occurrence。不存在首 occurrence 时返回空计划，不伪造行回退的 TokenId。
- `matchText` 非空时按精确原文过滤 token/line 事件；`minimumDurationUs` 检查真实 payload 时长；`everyNth` 按所选 scope 的真实 occurrence ordinal 选择逐字事件。重复装载不增加 occurrence 计数。
- 无逐字时间的行不会伪造 TokenStarted；cursor 模式由真实行依据事件覆盖。宿主重新核验事件、scope、subscription、锚点装载与同边界优先级，Rows 顺序不是事件顺序。

`count` 是每个真实锚点的整数数量，范围 0～64；`everyNth` 为 1～16；寿命 1～5,000,000 µs。
默认 token、project、不过滤原文、每个事件每个锚点 3 粒子、600,000 µs、0 散布。
修改源码或本文件后须更新清单的确切文件 SHA-256。这里只提供实施样例，不代表运行验收通过。
