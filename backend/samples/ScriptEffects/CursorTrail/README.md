# CursorTrail 效果源码包

本目录是完整的源码／资源闭包，可由 EffectPackageTransfer 分享为 .mkfx；归档离开原目录后仍可离线编译与运行。清单中的 cursor-trail 导出使用真实 CursorTrail 类，cursor 是唯一 Overlay 输出。默认作用于绑定内每条实际播放头，各自按 rate 发射。

参数由 manifest.json 唯一声明：rate=60/s、lifeUs=600000、sizePx=4、spreadPx=8、trailUs=250000、transition=jump、bridgeUs=180000、bridgeGapLimitUs=2000000；color 的示例值为白色，可用 typed RGBA 覆盖。数值范围是 SDK 输入类型的完整合法范围，实际存活／拖尾／绘制／每区间出生记录预算仍由工程会话校验；不把参数上限误称为性能承诺。

当前 Jump／Bridge＋SemanticCursor 已接通。Bridge 在合法正空档的最后 min(bridgeUs,实际空档) 内，沿独立最终端点连接的世界线段按 EaseInOut 移动发射点；超过 bridgeGapLimitUs、重叠或相接时直接换点。并发后继每条路径独立按 rate 积累，粒子 Source.Bridge 保留真实两端／转换身份，空档不伪造 Current。终点半开，旧粒子保持出生坐标并按寿命消散。未来作者 Error 或预算失败阻止输出，不回退基础几何。IncludeInTargetCapture 仍明确拒绝，目标内捕获须后续实施。固定拖尾由公共圆点输出呈现；VectorTrail／SpriteTrail 是独立完整示例包。工程持久实例和脚本工作区已接通，最终 R7／R8 仍未交付。SDK 1.0.0 未冻结；当前使用独立 EvaluateEmitters／EmitterWriter.Set 接口，桥接正式运行见程序 0.30.0。EvaluateVisuals 可只读查询本实例完整 Particles 和上一固定区间的 EmissionSchedule，不推进粒子；本效果由宿主自动生成圆点，所以不再重复绘制粒子。
