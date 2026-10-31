# R2 固定帧服务合同与实施记录

> 2026-10-02；程序 0.9.4，schema 21／15／3。依据已接受 R0 合同 A／B／UI、ADR-0071 和 [SDK 合同](scripting-sdk-contract.md)。本文件保留 0.9.4 批次记录；0.9.12 实际分析与初始化 required 门禁见 [音频合同](scripting-audio-analysis-contract.md)。本批补齐参数与只读基础服务，**不代表六阶段、音频分析器、包系统或脚本工作区已交付**。

## 1. 参数和曲线

`ParameterValues` 已支持完整九类参数：number、integer、boolean、enum、color、duration、curve、asset、text。整数和微秒不用 double；缺失／类型错误列出参数键，不能自动把路径文本变为资源或把颜色变为向量。

`ColorRgba` 是 sRGB 直乘 double RGBA，各分量有限且在 [0,1]。`MultiplyRgb` 仅乘 RGB、保留 alpha；结果越界报告错误，不夹值。预乘转换属于像素后端，SDK 不预乘第二次。

`CurveValue` 是封闭的纯描述，不接受用户函数或表达式字符串：

- Easing：Linear、三次 EaseOut、`3p²−2p³` EaseInOut。
- CubicBezier：端点 (0,0)、(1,1)，两控制点 X 各在 [0,1]，Y 有限，可明确过冲。按 X 反解参数，固定 56 次二分，端点直接返回。
- ControlPoints：首尾 (0,0)、(1,1)，X 严格递增，分段线性，Y 允许有限过冲；乱序／重复不排序修复。

求值进度必须在 [0,1]，结果必须有限；曲线可以过冲，但最终颜色／视觉比例仍分别执行其范围校验。`Curves.LinearCurve／EaseOutCurve／EaseInOutCurve` 供作者直接复用。

## 2. 固定资源句柄

`ResourceCatalog` 由宿主在加载前建立，只包含已声明的材质、图和图片身份。`GetMaterial／GetGraph／GetImage` 按类型读取；缺失明确报 `MissingResource`，不改取同名其他类型。声明中重复 ID 即拒绝，即使两个同名声明的内容相同。

句柄身份是 `(packageId, sha256:完整小写64位修订, exportId)`，没有可写文件路径、native shader／image 或加载函数。目录为不可变集合；`Contains` 要求完整身份和正确资源类型。空目录不虚构包或图片。

`ParameterValue.Asset` 持有宿主已解析的 `ImageHandle`。在构造脚本模块前，纯帧工厂检查全部资产参数属于本实例固定目录；不匹配报 `EffectFailureException(stage=ValidateInputs, code=InvalidParameterResource)`。另一个包／修订的同名图片不能替代，失败也不发布准备帧。

本批只接通中立身份、参数与查询，**不执行材质、图或图片绘制**。R3 增补导出 schema／uniform／输出依赖及图执行；R4 的真实包解析器提供目录与完整内容闭包，不将测试构造的目录视为可导入包。

## 3. PCG32-v1 的确切字节与索引规则

随机服务只接受固定 UInt64 seed 与有效 EffectInstanceId；`GetUInt32(streamKey,index)` 不消耗游标。index 是完整 UInt64，反复、倒序、跨会话读取同一输入得到同一值。

分流 SHA-256 的输入顺序为：

```text
UTF8("MakoKara/PCG32-v1")
+ seed: unsigned64 little endian
+ instance UTF8 byte count: unsigned32 little endian
+ instance: strict UTF8 bytes
+ stream UTF8 byte count: unsigned32 little endian
+ streamKey: strict UTF8 bytes
```

没有分隔符歧义或 Unicode 归一化；不完整 surrogate 拒绝。摘要前 8 字节 little endian 为 initstate，接下来的 8 字节为 initseq；按官方 PCG32 两步播种，再按 index 跳到该项。LCG 使用 multiplier=6364136223846793005、odd increment；64 位乘加按模 2⁶⁴，输出 XSH-RR。跳跃通过仿射幂组合 O(log index)，不逐项遍历到未来，也不推进主会话。`GetUnitDouble` 为同一 UInt32 除以 2³²，落在 [0,1)。

PCG 算法与核心测试向量核对[作者的最小实现及说明](https://www.pcg-random.org/using-pcg-c-basic.html)、[官方参考源码](https://github.com/imneme/pcg-c-basic/blob/master/pcg_basic.c)；分流编码和索引适配是本 SDK 的固定实现。改播种、编码或输出必须提升模拟 profile，不能悄悄使用 .NET Random。

测试库存（index=0、1、2、5、1,000,000、UInt64.MaxValue）：

| seed／instance／stream | UInt32 十六进制结果 |
|---|---|
| 0／instance／stream | 77b617a6、5233620b、0f4e08b6、ba7c00f5、b17af4b2、04a3a88c |
| UInt64.MaxValue／实例😀／发射/拖尾 | 104464d6、02987f62、f93c19ab、a6c070e9、d7dc06ec、57a06996 |

向量由独立 Python hashlib／整数模运算核对，C# 测试固定期望值；核心另核对官方 initstate=42、initseq=54 的六项，不能用同一实现计算“期望值”。

## 4. 音频可用性与查询

`AudioFeatures` 包装宿主提供的固定分析序列，未提供服务时为 NotAnalyzed。无音频、未分析、分析失败分别为 NoAudio／NotAnalyzed／AnalysisFailed，保留原因；**Values=null，不返回假的零能量**。本批没有 PCM 分析器、节拍识别、AudioFeatureTick 或 required-audio 导出门禁，继续在 R5 实施。

分析序列带固定 revision 和真实 DurationUs。样本从 0 开始、时间严格递增、每点位于 [0,DurationUs)；非法输入不排序、补点或延长时长。值提供 BassLevel／Pulse／BeatConfidence（有限 [0,1]），BeatPhase 为 [0,1) 或 null；没有节拍估计时 phase=null、confidence=0。已分析静音可以是实际零能量，与不可用分开。

`GetAt(timeUs)` 读取左端分析 hop 的值，不插值预测下一拍。恰在 DurationUs 及之后返回 OutsideRange、空值。`GetRange(TimeRange)` 返回时间戳落在半开查询区间内的真实分析点，以及 RequestedRange／AvailableRange／Availability；没有点不补假点，部分或完全越界明确报告。不以 UI 播放状态／帧率改变数据。

## 5. 已接通的宿主入口与边界

Initialize 与 FrameContext 使用同一个固定 Resources 和 DeterministicRandom；`Project.Audio` 与 `FrameContext.Audio` 指向同一固定分析输入，没有两份音频真值。`CompiledFrameEffect` 提供固定 Resources／UInt64 Seed，`PureFrameSourceFactory` 接受分析输入；受控指纹必须包含这些输入，R4 由真实资源闭包生成。

自动验证覆盖九类参数、颜色／曲线、资源类型／修订／重复 ID、随机核心和分流向量、半开音频边界／静音／不可用，以及真正 Roslyn 编译入口在 Initialize／Evaluate 读取相同服务。当前还是明确的纯帧 source，没有 callback／120 Hz 推进／粒子；完整 R2 剩余出口见 [实施记录 §4](archive/research/scripting-r2-sdk-geometry-progress.md#4-r2-尚需完成的出口)。

## 6. 验证命令

```bash
dotnet test tests/MakoKaraGenerator.Scripting.Tests/MakoKaraGenerator.Scripting.Tests.csproj -m:1 --filter FullyQualifiedName~FrameServicesTests
dotnet build MakoKaraGenerator.slnx -m:1
dotnet test MakoKaraGenerator.slnx --no-build -m:1
```

本批新增 13 项服务验证。最终全量结果记录在 roadmap 与 R2 实施记录，不把未完成的音频分析、包解析或 UI 算作当前出口。
