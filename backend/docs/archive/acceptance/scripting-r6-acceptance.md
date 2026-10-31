# 本计划 R6 自动组合验收

> 2026-10-05；对照 `scripting-system-next-work-plan.md` 第4批及§4 R6。本文区分本计划的自动出口、原路线高级能力和最终用户人工验收。当前程序0.31.0；本次仅增加回归和验收记录，没有修改生产运行或包字节。最终构建／回归／启动证据以 roadmap §18.95 为准。

## 1. 验收要求与实际来源

| 计划明确要求 | 当前实现和证明它的独立回归 |
|---|---|
| 连续发射正式绘制与精确rate | `ContinuousParticleTests.cs` 每条合法头独立60/s、137/s精确出生时刻；最终非线性族几何，解析速度／加速度／年龄／寿命半开；公开CursorTrail包由正式会话消费 |
| Burst正式绘制与数量 | `EventBurstTests.cs` 立即出生、同事件最后登记、同刻Burst先于连续、零／短寿命、每区间完整记录；公开EventBurst包整数初始化门禁及真实输出 |
| Cursor／Token／Line／Mark锚点 | `ProjectRenderSessionTests.ParticlePackages.Burst.cs` 四类完整包来源，无逐字时间行依据、并发事件各锚点独立数量，三真实标记／槽位／前奏与不伪造Token／Track；`EventBurstTests.cs` 最终几何 |
| 多播放头、出生身份和顺序 | `ContinuousParticleTests.cs` 每条头独立；`EventBurstTests.cs` 并发每事件／绑定数量及BirthIndex；`VisualOverlayRenderingTests.cs` 半透明多实例层序，`TemplateOverlayLayerTests.cs` 实际层覆盖 |
| 无空档跨行直接换点 | `ProjectRenderSessionTests.Particles.Cancellation.cs` 显式零空档真实跨行，从400000us边界前后到799999us与Jump比较全部粒子、固定拖尾和出生身份，不分配Bridge；默认Vector／Sprite作者在真实有空档时也保持Jump |
| 有空档按配置桥接 | `ContinuousParticleTests.Bridges.cs` 45001us网格内起点、半开终点、独立全实例未来几何、每条并发边配额、句内／整行／重叠／长空档／零时长及权限；`ContinuousParticleTests.BridgeLifetime.cs` 三槽前奏及端点缓存释放 |
| 圆点、精灵、线段、带状拖尾 | `VectorOverlayRenderingTests.cs` 独立矩形、平端／渐宽／折点／反向／密度；`SpriteOverlayRenderingTests.cs` 原图裁片、旋转／镜像、直乘Tint／Alpha及共享帧账；真实VectorTrail／SpriteTrail包与Burst进入正式Render |
| seed与跨FPS重放 | `ContinuousParticleTests.cs` 同种子／改种子真实散布；`ContinuousParticleTests.Bridges.cs` 24／30／60FPS、暂停／直达／回退／模式／密度；完整包混合工程逐帧编码输入与独立Initial预览比较，回退后全段重放 |
| 预算超限明确失败而不裁剪 | `EventBurstTests.cs`、`ContinuousParticleTests.Bridges.cs` 全实例记录／存活／拖尾／绘制预算；`ProjectRenderSessionTests.ParticlePackages.Burst.cs` 共享存活38在500000us第二次Burst中止真实编码，旧字节／真实包来源／暂存保护 |
| 粒子预览与导出逐帧一致 | 新 `ProjectRenderSessionTests.Particles.Acceptance.cs` 三FPS共六次真实音轨视频；所有编码输入帧与独立预览BGRA字节、时刻及作者诊断相等；完整解码检查每帧Alpha／RGB、数量和顺序，回退后再次逐帧比较 |
| 持久包／资源闭包和真实组合 | `ProjectRenderSessionTests.ParticlePackages.cs` `.mkfx`分享／导入／保存重开；Vector／Sprite／Burst、双文字、真实48kHz音频及ColorOnly整行Glitch共同参与正式准备帧和CPU Render；关闭Glitch改变真实像素而不改变语义出生 |
| Error／取消／结束释放与恢复 | `ContinuousParticleTests.BridgeLifetime.cs` 独立端点正常／失败／取消／换目标释放；`ProjectRenderSessionTests.ParticlePackages.Lifecycle.cs` 新源码单实例接受、旧／当前会话回收；新 `ProjectRenderSessionTests.Particles.Cancellation.cs` 两种实际编码取消、旧产物保护、全部帧／作者上下文释放及重新逐帧成功导出 |

上述测试文件位于 `tests/MakoKaraGenerator.Rendering.Tests` 和 `tests/MakoKaraGenerator.Spike.App.Tests/Rendering`；作者协议测试位于 `tests/MakoKaraGenerator.Scripting.Tests`，并验证能力、生命周期、名字、default数组／非法几何及固定包入口。已实施协议分别见 ADR-0087～0089、0123～0127。状态来自实际断言和最终运行，不能单凭测试名称或历史绿色数量认定。

## 2. 全帧验证的口径

六段视频分别为 dots/24FPS、segments/30FPS、ribbon/60FPS 的普通MP4和透明ProRes4444。每段都同时含Sprite／Burst／Bridge、双文字、真实音频和整行Glitch；每种呈现不是单独静态图元。沿现有输出总帧规则，1.8s歌曲分别43／54／108帧，共410帧；本批不改变导出帧数取整策略。

在真实编码器写帧前，独立Initial预览使用同一快照输入和Final质量，逐帧比较时间、预乘BGRA全部字节及作者诊断。真实编码器负责每帧释放，验证完整成功／结束及回退后全段重放。FFmpeg以原帧顺序完整解码为直乘BGRA，不通过单张截图、重新采样或只比最后一帧代替逐帧验证；核对EOF保证没有缺帧／多帧。每帧所有像素Alpha误差≤3；RGB只对透明帧参考Alpha≥24的像素计算，每帧平均误差≤9，普通帧计算所有像素。透明参考低Alpha的RGB不作可靠颜色证据，但所有Alpha仍检查；完全空帧也必须通过。角落Alpha分别0／255。编码输入字节要求相等，损失编码产物按明确误差判据比较，不能把两者混为一个相等承诺。

另外逐帧统计Alpha≥24且最大RGB通道≥32的前景像素，解码前景不得少于参考的一半，避免黑色背景在平均误差中掩盖整个前景丢失；这项检查不放宽上述RGB和Alpha判据。暂时只将导出第43帧时间偏移10000us的变异，触发1750000us与1760000us不等；此前966666us抽帧不受影响，证明新回归实际覆盖后段输入。生产文件已逐字节恢复，最终全量使用正常源码。

取消发生在30FPS的1266666us：39帧已进入真实管道，编码未Complete／发布；保留旧输出字节、清空暂存、关闭像素和作者会话。用新token从固定输入重新成功导出54帧，所有新输入再次与独立预览比较。

## 3. 自动出口与剩余范围

上述全量／定向验证已通过，收口本计划第4批R6自动出口，接续第5批R7工作区与模板整合。这个结论只覆盖计划明确列出的连续／Burst、四类锚点、Jump／Bridge、四类图元、顺序／随机／预算和逐帧导出。

原实施路线的IncludeInTargetCapture、owned overlay／自身后置粒子、父组图、跨输出依赖、非ColorOnly实际映射仍保留既有明确门禁。后续计划§2.2已将这类高级合成放在R3.1，不以本次验收开放或删除它们；原路线完整SDK／R6、高级输出及完整R4／R7的剩余项不自动完成。性能基准仍后置。R7实时效果工作区、模板配方入口、绑定诊断、宽窄窗口和撤销，以及R8最终成片／用户人工验收继续执行，不以自动视频或代理查看PNG替代用户反馈。
