# 发射描述与粒子只读查询的阶段实施合同

> 2026-10-03；0.9.23 的连续发射已实施。**用户已确认新增独立 EvaluateEmitters 阶段、替换旧接口及 100000 条独立出生记录预算**。0.10.0 已接入七阶段、作者只读粒子和真实 CursorTrail 包；全量验证证据见 §6。

## 1. 当前接缝

EvaluateVisuals 同时 Set 发射描述并产生 Overlay。宿主在作者返回后才收到描述，才能解析本帧粒子。因而不能在同一个只读上下文中提前提供包含该描述结果的完整 Particles。重复调用作者视觉方法收集描述会使诊断、writer 生命周期和作者理解复杂化。

## 2. 已采用：独立纯发射求值阶段

新增 `EvaluateEmitters(EmissionFrameContext, EmitterWriter)`；每次仍使用同一作者对象，不另建实例。上下文提供最终文字几何、合法播放头、边界／历史、绑定、参数／资源／随机／音频和诊断／取消，不提供粒子集合或文字／图像 writer。输出只登记发射描述。

固定边界流程：先解析上一半开区间的精确出生几何 → 共享 Advance → 事件 → 所有实例 Evaluate 文字 → EvaluateEmitters 描述 → 注册当前边界描述 → 解析本边界 Burst 意图（0.11.0）→ 解析粒子／固定拖尾 → AdvanceVisuals → EvaluateVisuals 读取完整不可变 Particles 并写 Overlay。

分数帧：共享状态不推进，纯求值最终文字；当前区间继续使用其左边界发射描述，粒子在副本上解析至 t；EvaluateVisuals 读取同一份最终 Particles 并写 Overlay。AnchorQuery 只执行语义和文字，跳过发射／粒子及视觉阶段。事件 Burst 之后在同一最终几何接缝接入，不在 OnEvent 读取最终图像。

作者粒子查询只读取本实例的宿主管理对象，包括已离场绑定的存活对象；事件订阅不扩大输出绑定。EmissionSchedule 保存真实出生记录／区间身份，不能让作者重复推进同一粒子。存活／拖尾／绘制预算不变，错误仍阻止导出。

## 3. 备选：在初始化／视觉推进登记

不新增方法，把描述放到 Initialize 与 AdvanceVisuals 的阶段服务。六个回调数量保持，但初始注册和后续更新分处两种上下文，固定边界与分数帧的发射配置规则更复杂；作者首次学习的入口不如独立纯方法清楚。

## 4. 破坏性变更与实施门禁

建议新增第七个作者方法并删除 VisualWriter.Emitters；公开 CursorTrail 源码、编译入口验证、真实阶段探针和作者文档同时改。旧 DLL 不复用，当前源码失败继续阻止导出；不追加旧脚本兼容通道。SDK 1.0.0 尚未冻结。若采用建议，程序按不兼容规则升 MINOR；project／lyrics／template schema 本项不变，精确程序版本门禁继续。

用户已确认阶段建议，本批完成新入口、实际作者 Particles／EmissionSchedule 及 CursorTrail 包消费。Burst、桥接、sprite／线段拖尾、实际映射、包材质／工程保存及工作区仍属完整路线图，不因该接口调整关闭。

## 5. 发射记录预算（已确认）

EmissionSchedule 须提供上一固定半开区间的完整真实出生，包括已在该区间内死亡的粒子；不能以期末存活集合冒充出生记录。极短寿命可使每区间总出生远大于同时存活数量，因此需要独立的记录数量门禁，分配前检查、全实例合计且错误归属真实实例。记录只保存本区间，不建立未接受的长期粒子历史。

用户已确认固定工厂的 ParticleSimulationBudget 增加 `EmissionRecordLimit=100000`（每模拟区间所有实例合计），不与 200000 拖尾样本共享计数。可显式修改；不静默裁掉记录或降低 rate。固定区间和分数帧副本的真实出生流都执行同一门禁；不以每帧重复读取累计预算。初始 [0,0) 无出生，右边界出生属于下一半开区间；分数帧 EmissionSchedule 保持最近完成区间，当前临时出生通过 Particles 读取。

## 6. 实际接缝与验收

- `EmissionFrameContext` 提供最终几何／播放头和固定服务／完整历史，不提供 Particles 或图像／文字 writer。`EmitterWriter.Set` 的同身份最后描述生效，阶段结束后拒绝复用，能力和清单命名输出门禁保持。
- `VisualStateContext` 提供 PreviousParticles、Particles 与 EmissionSchedule；`VisualFrameContext` 提供同一已经解析的 Particles 及上一固定区间 EmissionSchedule。两者仅包含本实例，不以当前绑定可见集合裁掉旧行粒子。
- `ParticleBirthSample` 保存稳定 ID、轨道／装载、出生／死亡微秒、实际出生世界位置和该出生固定描述。先捕获完整出生，再清理寿命末端；不把期末存活对象当作完整记录。
- PrepareParticles 在 AdvanceVisuals 之前完成一次，EvaluateVisuals 只读该准备帧，不重新模拟。共享推进／源回调次数不变，0 无零长推进；分数帧仅用左端描述，独立 AnchorQuery 跳过全部发射及视觉。
- CursorTrail 的真实 manifest.json／README／src 清单带完整摘要，默认／覆盖接入正式帧；.mkfx 离开原目录后仍离线编译并调用独立发射入口。不是工作区导入按钮或工程持久实例已经交付。
- 程序 0.10.0，schema 21／15／3；旧 VisualWriter.Emitters／CompleteOutput 已删除，旧源码编译失败，不复用旧 DLL。SDK 1.0.0 仍未冻结，缓存指纹包含实际 SDK 字节。

验证状态以 roadmap §18.53 为准；日志 `/tmp/makokara-emission-build-final.log`、`/tmp/makokara-emission-tests-full.log`。Burst／桥接／sprite／独立线段拖尾、映射、主画布协调器、包材质／持久事务和工作区继续按原路线实施。
