# R5 持久文字效果、音频与重建组合验收（0.26.1）

> 2026-10-05。本批接续 `scripting-system-next-work-plan.md` 第 3 批。
> 保持已接受的时间线和七阶段协议；补齐真实重复文本示例包及跨持久化／像素／编码证据。当前完成状态与最终数量以 roadmap §18.87 为准。参数／源码撤销及热替换的组合释放验收仍后续，完整 R5～R8 未关闭。

## 1. 两个可导入示例

[`WordEmphasis`](../../../samples/ScriptEffects/WordEmphasis/README.md) 使用公开 SDK §6.1 同一源码，只输出元素族视觉变换；默认 peakScale=1.25、returnUs=120000。修订 README 为当前工作区和 `CompiledProjectEffects` 路线，包产品版本为 1.0.1，重新计算内容摘要，C# 源码保持。

[`RepeatEmphasis`](../../../samples/ScriptEffects/RepeatEmphasis/README.md) 补齐 profile2 清单、README、文件摘要和控制器导出 `repeat-emphasis`，源码仍为公开 SDK §6.2。声明 `VisualTransform/tokenFamily` 与 `StyleModulation/tokenFamily`，六种绑定可选；不把事件读取范围误写成输出依赖。

默认参数沿合同 A §8.1：countScope=branch、scaleStep=0.08、maxScale=1.8、darkenStep=0.06、minBrightness=0.35。未规定的编辑范围明确选择：scaleStep [0,3]、maxScale [1,4]、darkenStep／minBrightness [0,1]；非法输入拒绝，不静默修正。范围都是有限值，零增量表示保持，亮度允许降到零，首唱仍为第 1 次。

两个包都通过真实目录闭包读取、`.mkfx` 原子分享／重读、正式包定义与离线编译；新建工程实例固定包修订、参数、订阅、seed 和顺序，保存后重开不从源码目录读取字节。两个作者源码未增加特权、Core 或 native 类型。

## 2. 固定会话语义

120 Hz 整数网格和精确源事件按已有合同合并；共享 Advance 后处理同界事件，再 Evaluate／EvaluateEmitters、固定粒子与两个视觉阶段。任意帧求值不额外推进状态。起唱依 OccurrenceId 去重，前奏／几何查询不增加次数，旧句使用自己的序号。输出绑定与源订阅独立，完整只读历史保持。

Word 与 Repeat 的元素族缩放相乘，基础字和注音共用变换；Repeat 从固定基线读取已唱纯色并显式变暗，实例不读之前实例的样式输出。回归的音频控制器先写未唱／已唱，Repeat 后写已唱，所以最终已唱由 Repeat 输出，未唱仍消费真实低频。音频控制器是测试中的公开 SDK 消费者，未宣称它是已经交付的第七个产品示例。

required 音频只检查初始化 Available；播放头越过真实分析范围返回 OutsideRange／null，由作者分支记录提示，不补造静音。无源、未分析、分析失败和无流分别保留 NoAudio、NotAnalyzed、AnalysisFailed、NoAudio；optional 效果可以进入不可用分支，required 效果在编码前失败。分析内容、真实 hop、时间映射和 package 身份不从 FPS 推断。

取消后的共享状态卸载，下一请求从固定快照重放；取消类型保持。回归在真实 Skia 几何已处理两次起唱和音频 hop 后触发一次受控取消信号，分别从请求及异步方法参数传入。使用正式 `ProjectRenderSession`、真实包编译和 Skia 后端，未用延时竞争或修改作者示例来制造取消。

## 3. 自动验收映射

| 要求 | 当前回归 |
|---|---|
| 可导入／分享的公开文字示例及完整默认参数／输出声明 | `ProjectRenderSessionTests.Emphasis` 的真实目录、`.mkfx` 和编译／CreateEffect 检查 |
| 保存重开后两个文字效果，24／30／60 FPS 与真实音频组合 | 三种 FPS × 有／无音频共 6 组；顺序播放、同帧重绘、回退、中段独立 Export 会话比较全部像素和诊断 |
| 有意义的语义对照 | 第三次的 1.16 倍 Repeat、Word 进度缓动、已唱 RGB(176,88,44)、注音世界矩阵和 63 个真实 20 ms hop 分别断言 |
| 输出绑定／计数范围／订阅独立 | branch／project 与 project／boundSources 三组，隐藏源参与读取但只写指定元素；参数持久化保持 |
| 前奏提前显示不计数，槽位保留旧句自身序号 | 普通与三槽跨片段工程；准备 BindingId 与起唱一致，第 1／2／3 次分别为 1／1.08／1.16，回退后主 Render 像素一致 |
| 取消部分推进后重建与释放 | 请求 token／方法 token 两组，下一帧与独立 Export 会话全部像素／诊断一致；重绘不改变计数，重复 Dispose 安全 |
| 音频可用性与必需策略 | 保存重开后的四种不可用状态逐一验证 required 初始化失败、optional Warning 和合法像素；required 初始化成功后允许真实 OutsideRange |
| 重复示例不能猜测非纯色基线 | 真实源包材质基线得到 `BaselineMaterialNotSolid`，保留 `repeat-emphasis` 的实际包／修订／导出 |
| 真正成片消费同一结果 | 三种 FPS × MP4／ProRes4444 共 6 次编码，保留真实音轨；抽取 1.5 s 帧对照独立预览，Alpha 每像素差 ≤3，平均 RGB 差 ≤9，透明角落 alpha=0 |

七阶段来源／Error、提示传输、缓存、200 ms 中途失败及旧产物保护继续由 0.26.0 回归验证。已有原生图／材质、固定 DLL／工程搬移和失败源码工作区证据继续由全量回归保留；本批不修改这些运行路径来满足测试。

## 4. 边界与下一步

本批不冻结完整 SDK，也不启用直接图／材质实例、父组图、复杂映射或粒子 Bridge／sprite。正式源码使用已存在的编译／帧／像素／导出路径；包 profile2、schema26／16／5、Shader ABI、SDK 名义版本、程序集集合与依赖保持。程序 PATCH 升至 0.26.1，不转换旧工程。

0.26.2 已补齐预览输出输入身份与迟到结果丢弃，见[预览输入与取消合同](../../scripting-preview-input-contract.md)／ADR-0121。下一步在固定源会话上补齐参数／源码撤销、单实例修订热替换及取消导出的组合释放证据，再确认本计划第 3 批出口并继续 R6。最终宽窄工作区和视频人工验收属于 R7／R8，自动像素与编码验证不替代用户验收。
