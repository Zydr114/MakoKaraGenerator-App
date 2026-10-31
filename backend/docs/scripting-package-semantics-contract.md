# 效果包语义解析合同与实施记录

> 2026-10-03；依据已接受 R0 A／B、ADR-0071／0083。用户本轮已确认 §2 三项编码；0.9.20 实施九类参数、控制器及真实编译消费。内容闭包不代表全部可运行；本批不修改 project／lyrics／template 格式，schema 21／15／3 保持。

## 1. 直接实施的已确认部分

- Core 解析固定清单，提供不可变的参数定义和控制器导出。默认值、标签、单位、区间和 enum 选项来自同一清单；图沿用 `EffectParameterConstraint`，不另设约束真值。
- number 使用有限 double；integer 和 duration 使用完整 long，duration 非负。boolean 不接受 0／1；text 允许空串；enum 必须明确包含默认选项。默认值不夹值、不转换类型。
- 通用参数字段为 `id/type/label/default`；数值类额外需要 `min/max`，enum 需要带 `value/label` 的 `values`；`unit` 是可选说明字段。未知／缺失字段、重复 ID、反向区间、越界默认值均失败。
- 控制器按 R0 示例读取 `id/kind/name/entryType/parameters/bindingKinds/outputs`。参数引用必须存在且不重复，绑定种类只能是 R0 六类。输出保留明确的类型、真实目标、能力需求和本导出内命名依赖；缺失依赖、重复引用和环拒绝，前向引用允许。
- 结构解析不宣称宿主已支持全部 writer；建立运行描述时按实际生产能力矩阵检查。当前 `requires` 仅接受空集合，非空能力需求明确失败；跨输出执行／旁路尚未接入，依赖虽可校验和查看，仍不能据此启用。清单声明也不扩展运行时的目标绑定。
- Scripting 将已解析默认值／显式覆盖转换为 SDK 输入，按清单选定入口和能力。覆盖仅限该导出的参数，继续执行原区间／类型校验；改变默认值和图片不重新编译相同 C# 源码。
- 材质、图、捕获配方、shader ABI 和尚未确认的参数编码继续按相应合同推进。本批控制器解析不得忽略其他导出，未完整解析的包不能冒充全部语义验证成功。

## 2. 已确认的剩余参数编码

R0 已接受九类参数，SDK 已支持九类值，原示例清单只给出了 number／duration／enum 的具体写法。本轮用户明确回答「采用这三项编码」，剩余三类采用：

| 参数 | 建议 `default` 编码 | 校验 |
|---|---|---|
| color | `{"r":0.3,"g":0.5,"b":0.8,"a":1}` | sRGB 直乘 double，各分量 [0,1]；保留精度，不量化成 8 位色 |
| curve | `{"kind":"easing","easing":"easeOut"}`；或 `{"kind":"cubicBezier","first":[0.2,0],"second":[0.8,1]}`；或 `{"kind":"controlPoints","points":[[0,0],[0.5,0.8],[1,1]]}` | 对应现有 SDK 三种纯曲线；不支持表达式，不整理点序 |
| asset | `"assets/noise.png"` | 包内逻辑相对路径；该文件明确声明 `role:"image"`。`role:"asset"` 继续表示普通随包资源，不自动当成图片；实际图片解码成功后才进入 SDK 图片目录 |

三者仍使用 §1 的通用字段；不增加外部路径、包间引用或另一份资源清单。曲线同时支持三种编码；颜色保留直乘 double；asset 的路径只在宿主投影成已验证的图片句柄，脚本仍只拿逻辑身份。

## 3. 实际入口和当前能力

1. `EffectPackageContents` 保持规范清单／不可变文件闭包。`EffectPackageContentBudget/Usage` 落实 R0 256 MiB／1024／64 MiB 初始值，包括清单，空目录不占文件内容；目录／ZIP 按真实展开长度在读取和分配前检查，读取长度变化失败。规范后的闭包再次检查，分享时不会发布在相同预算下不可读的包。这是逻辑内容预算，不宣称进程峰值内存等于该数值。
2. `EffectControllerPackage.Read` 一次验证全部九类默认值、元数据、控制器及输出依赖，保留原声明顺序，提供稳定拓扑顺序。`ResolveParameters` 将导出默认值与明确覆盖组合，同一函数检查默认与覆盖；未知／未引用键、重复键拒绝，完整 long 不经 double。
3. `IPackageImageValidator` 位于已有 Rendering.Abstractions。Skia 的 `PackageImageValidator` 实际解码 CPU 图像，检查尺寸、完整像素及明确容量，释放验证时的 native 对象。不注入验证器或编码失败不能建立 SDK 图片目录；此批没有图片绘制缓存或包 ResourceImage 图消费。
4. `CompiledControllerPackage.CompileAsync` 用全部入口集合参与 Artifact 指纹。真实 DLL 逐导出检查公共／非抽象／非开放泛型／公共无参／SDK 接口；反射验证不调用作者构造、Initialize 或 Dispose。编译失败保留真实诊断，不创建帧实例。
5. `CreateEffect` 从导出读取入口、默认及覆盖，验证六类绑定，接入原纯帧／语义工厂。当前能创建 `VisualTransform → tokenFamily/line/textMark`、`StyleModulation → tokenFamily/textMark`、`Overlay → tokenFamily/line/textMark`。组、整行 Style、图／映射及跨输出计划仍明确失败。生产帧在合成前验证实际文字输出类别；视觉批次须使用已声明 Overlay OutputId，不能用粗粒度能力位扩大目标类别或偷偷新增批次。
6. `samples/ScriptEffects/WordEmphasis` 已有真实摘要清单，可用目录／ZIP API 消费。默认 1.25 倍和 120000 us 来自清单；导入编辑器的工作区按钮仍未开发。

### 宿主组合示例

```csharp
var contents = await EffectPackageTransfer.ReadArchiveAsync(path, profile, cancellationToken: token);
var definition = EffectControllerPackage.Read(contents, token);
var compiled = await CompiledControllerPackage.CompileAsync(definition, compiler,
    new PackageImageValidator(), token);
var effect = compiled.CreateEffect("word-emphasis", instanceId, binding, seed,
    parameterOverrides, subscription, token);
// effect 交给既有 PureFrameSourceFactory 或 TimelineFrameSourceFactory。
// 输入指纹／工程修订、预览刷新和实例持久化仍由后续 R4/R5 组合根实施。
```

## 4. 本批验证与边界

清单真实默认值及覆盖 → SDK → Roslyn 入口 → 生产准备帧／真实像素；错误清单在运行作者入口前拒绝。包内容修订与编译 Artifact 分离，取消和失败不发布半成品。自动验证包括九类值、整数／颜色精度、三种曲线、部分覆盖、输出拓扑／坏依赖、真实 DLL 入口／不运行构造、声明外输出门禁、图片完整解码／容量，以及真实逐字强调包在两种输出密度的像素及注音共同矩阵。

材质／图导出或输出 bypass 存在时，当前控制器入口拒绝整个包；不会只运行碰巧能解析的控制器。剩余：材质／图／shader ABI／捕获配方解析、背景旁路依赖执行、图片图消费、工程聚合及歌词内容地址保存／历史／模板依赖与工作区。完整 R4 和 R2～R8 尚未完成；原六项待答（含效果与排除部件叠放）不默认选择。

新增 Application **3**、Core **47**、Rendering **9**、Scripting **14**；全量构建 **0 警告／0 错误**，八项目 **2106 项通过、0 失败／跳过**（80／932／54／87／543／147／179／84）。日志 `/tmp/makokara-pack-semantics-build-final.log`、`/tmp/makokara-pack-semantics-tests-full.log`；针对语义／编译／像素的定向日志同前缀 `core/scripting/rendering.log`。正常测试首次因沙箱本机 socket 禁止而中止，正常构建曾遇 NuGet 审计网络限制；通过已授权环境和本机代理完成门禁，未关闭审计／升级依赖。

已直接启动最终 **0.9.20** 构建，PID **71976**；niri 确认窗口 **23**，标题及日志版本一致。日志 `/tmp/makokara-f93b-pack-semantics-final-verified.log` 未见托管启动异常，有 JACK／ALSA 设备探测警告；未逐项人工验收音频播放，本批无新增脚本工作区 UI。

## 6. 独立程序接续（0.9.21）

控制器目录现已包含全部独立 role:shaderProgram 声明，固定版本／profile 和 ABI 不从源码推断；有声明时必须注入正式 shader 验证器，全部原生编译／反射后才能创建控制器包。九类参数／默认和覆盖改用同一内部读取器，现行行为保持。材质／图及 bypass 仍拒绝整个包，不部分接受，B 图／捕获编码随后获确认并在 0.9.22 实施；C fallback／混合导出仍待答复。具体格式和当前证据见 [图语义补充合同](scripting-package-graph-semantics-contract.md)／ADR-0085／roadmap §18.50。

## 7. 固定包图接续（0.9.22）

EffectControllerPackage 保留全部 role:graph 的唯一计划和逐输入配方；CompiledControllerPackage 须注入 IPackageImageGraphValidator，正式 CPU 验证后才继续控制器编译。包图片可完整解码并按原始几何供图读取，非 Canvas／未实施捕获或重组明确失败。仍不公开伪 GraphHandle 或忽略混合包中未解析导出；C、工程事务及主画布后续实施。详见 [图补充合同 §8](scripting-package-graph-semantics-contract.md#8-b-的实际实施路径0922)／ADR-0086／roadmap §18.51。
