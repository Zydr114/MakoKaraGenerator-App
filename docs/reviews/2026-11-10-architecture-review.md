# 架构审查：渲染、脚本、平台与前后端交互（2026-11-10）

- **审查者**：Claude（Fable 5.2）。本次以独立于项目既有上下文的身份做只读审查，没有参与过此前任何里程碑的设计或实施。
- **基线**：分支 `dev/script-sdk-render-upgrade`，提交 `6e68996`，产品 `0.112.67`，HTTP 合同 `1.49.1`。
  审查时工作区另有并行会话暂存但未提交的 4 个文件改动（`SkiaRenderPipeline.ImageScene*.cs`、`Directory.Build.props`），
  这几个文件在审查期间仍在变动，下文对它们只引用符号名；其余文件的行号以提交 `6e68996` 为准。
- **方法**：阅读源码、架构文档、计划和 M12 记录；用 `git ls-files` 统计规模。**没有运行构建、测试或测速**，
  文中所有性能数字都引自 [M12 测量记录](../plans/script-sdk-render-m12-evidence.md)，不是本次测量。
- **未覆盖**：见 §9。结论的把握程度在各节单独说明。

## 1. 结论

| 问题 | 结论 |
|---|---|
| 架构是否合理 | 分层方向正确，依赖关系干净（脚本层不引用 Skia，Core 零依赖）。问题集中在两处「没拆完」：渲染器是一个兼任五种职责的 5800 行类，计划里的渲染计划层没有落地；SDK v2 的作者接口是好的，但宿主是叠在 v1 上的翻译层。 |
| 脚本扩展性 | 对效果包作者：在现有能力内组合，够用，接口清晰。对宿主：新增一种属性或目标粒度要手工改 8～10 处，成本高。准确的说法是**扩展成本高**，不是扩展性不足。 |
| 渲染是否臃肿耦合 | **臃肿，是；耦合，主要是类内耦合。** 跨程序集的耦合方向是对的，臃肿集中在 `SkiaRenderPipeline` 和 `FrameEffectHost` 两个大类内部。M12 的 F1～F6 每一项都是在补「同一件事每帧做了很多遍」，根因是没有一个能在执行前看到整帧的中间层。 |
| 技术栈 | 合理。风险点有四个：`SharpVk` 的维护状态、自编译的 `libSkiaSharp` 补丁、Electron 较新的共享纹理 API、Node → Python → .NET → C++ 四段启动链。 |
| 平台相关代码 | **不只 GPU 纹理共享一处。** 实际有 9 处 Linux 专属实现（§5.2），其中进程守护和启动链即使不做 GPU 预览也挡着 Windows。 |
| Windows 版难度 | 可行，不需要改架构。能跑起来（CPU／PNG 路径）工作量小；让 v2 脚本可用（Worker 内 Vulkan）中等；GPU 共享纹理预览是大头，且有一项需要先做探针验证的技术风险。 |
| 前后端交互 | 合同的设计本身合理（命令＋读模型＋任务＋事件流）。臃肿在维护方式：618 个 schema 靠前端手写 4200 行类型对照，新增一个端点要改七处。交互层面最大的实际问题是「任何输入改动都换 Worker」。 |

最值得优先处理的三件事（详见 §8）：

1. 先定 **v1 删除与 M12 剩余优化的先后**。现在的优化是在 v1／v2 混合宿主上做的，删 v1 时热路径要再动一次。
2. 把 **几何／布局从渲染器里拆出来**，并落地一个最小的对象级渲染计划。这是 M12 那类问题不再反复出现的前提。
3. Windows 方向先做一个 **Vulkan 到 Electron 的共享纹理探针**，它是唯一可能推翻方案的未知项。

## 2. 规模概览

| 部分 | 源码行数 | 说明 |
|---|---|---|
| 后端 `backend/src`（19 个项目） | 约 135,000 | Core 31k、Backend.Hosting 25k、Rendering.Skia 14k、Contracts 9k、Media 9k、Scripting.Abstractions 8k、Scripting 7.5k |
| 后端测试与工具 | 约 122,000＋7,500 | Rendering.Tests 30k、Hosting.Tests 25k、Core.Tests 17k |
| 前端 `frontend/src` | 约 55,000 | editor 45k、protocol 4.2k、client 4.1k；测试 16k |
| 桌面与原生 | 约 6,500 | `desktop/` 1.3k（JS）、`native/gpu-preview` 2.6k（C++）、`backend/native` 2.6k（C） |
| HTTP 合同 | 119 个操作、618 个 schema | `openapi.json` 1.2 MB，`examples.json` 3.6 MB |

提交 413 个，历时约 10 天。测试代码与产品代码接近 1:1，`docs/plans` 下有 28 份计划与证据文档。

## 3. 渲染系统

### 3.1 现状

一帧的数据流：

```text
ProjectSnapshot（固定输入）
  → FrameSession.PrepareAsync      脚本宿主求值，产出 PreparedRenderFrame（中立的帧描述）
  → SkiaRenderPipeline.Render      背景 → 下层叠加 → 歌词或图场景 → 上层叠加
  → 输出：CPU 读回（HTTP／导出）或 GPU blit 到共享槽（桌面）
```

设备抽象是 `ISkiaRenderDevice`（CPU、Vulkan 两个实现），会话组合根是 `ProjectRenderSessionFactory`。

### 3.2 做得好的部分

- **准备与绘制分离**。`PreparedRenderFrame` 作为中立产物，绘制阶段不回调作者代码，这条边界守得很严。
- **依赖方向正确**。`Scripting`、`Scripting.Abstractions`、`Rendering.Abstractions` 都不引用 SkiaSharp；Core 零包引用。
- **预算与失败归属**。表面预算、图执行预算、错误到实例的归属都是显式的，没有静默降级。
- **设备接缝干净**。`ISkiaRenderDevice`（`SkiaRenderBackend.cs:40`）只有 9 个成员，CPU 与 Vulkan 的差异基本收在里面。

### 3.3 问题

**R1　`SkiaRenderPipeline` 是一个兼任五种职责的类（5,817 行，43 个 partial 文件）。**

它同时是：布局与几何提供者（实现 `ILineGeometryProvider`，脚本宿主通过它取场景几何）、歌词直绘器、
材质／图场景合成器、叠加图元绘制器、GPU 输出入口。证据：

- `ProjectRenderSessionFactory.cs:55`、`:130`～`:134`：一个会话要建两个管线实例，一个只用来给脚本量几何，一个用来画，
  两者靠共享 `device` 和 `LayoutCache` 才不重复整形。
- 构造器 10 个位置参数，其中 2 个是所有权布尔（`SkiaRenderPipeline.cs:82`～`:85`）。
- 类注释写「本类型不保存任何与帧有关的可变状态」（`:33`），但 F1 为了共用背景帧加了可变字段 `_backgroundScope`（`:57`）。
  这不是 F1 做错了，而是这个类没有地方放「本帧上下文」。

影响：几何是脚本求值的输入，却住在绘制后端里；M12 发现的「补算锚点时反复取基础场景帧」（O1 剖析里占准备的约 30%）
正是这层关系的代价。

**R2　计划中的渲染计划层没有落地。**

主计划 D7 和 §4.4 把 `RenderExecutionPlan` 定为重构核心（计划层与后端解耦、批处理、区域传播）。
源码里搜不到这个类型，绘制仍是立即模式加逐对象闭包（`SkiaRenderPipeline.ImageScene.cs` 里 `DrawImageEffectScene` 的
`DrawObject` 是一个约 180 行、内含 8 个局部函数且嵌套三层的局部函数）。

M12 的修复清单可以直接读成这一层缺失的症状：

| 修复 | 实质 |
|---|---|
| F1 背景帧每帧只取一次 | 缺少帧级共享资源的归属 |
| F2 表面只提交不逐张等待 | 缺少帧级的提交调度 |
| F3 先判空再分配 | 缺少执行前的静态分析步骤 |
| F4 相邻无历史层单遍绘制 | 缺少层合并的优化步骤 |
| F6 门禁按实例只做一次 | 校验混在执行路径里 |
| O7 捕获框收紧到目标区域 | 缺少区域传播 |

这六项都修对了，但每一项都是在立即模式代码里加一个特判。下一种重内容出现时，同类问题会换个位置再出现。

**R3　绘制有三条调度路径，要求结果一致。**

`SkiaRenderPipeline.cs:242`～`:247`：帧里只要有任一材质、图或组透明度，就从 `DrawLyrics` 切到 `DrawImageEffectScene`；
后者内部又分 `DrawGroupCompositionScene` 和 `DrawIsolatedScene` 两个调度器（`DrawImageEffectScene` 开头按有无下层快照二选一）。
M12 记录里「两条调度路径须一致」反复出现，第 12 条为此要重写下层与组合测试。
图场景路径把每个对象拆成 8 个色彩部件（底字与注音各自的辉光、描边、阴影、填充）乘以成员数，各占一张离屏表面
（`ColorPartOrder` 与 `AddParts`），这是「每行约 19 张整屏表面」的结构来源。

**R4　`PreparedRenderFrame` 既是数据，又做校验，又算派生几何。**

7 个构造器重载（`PreparedRenderFrame.cs:40`～`:79`）加一串 `With*` 方法；构造时全量校验并用 LINQ 重建
`FinalGeometry`、`Playback`、`FinalGroupGeometry`（`:95`～`:145`）。它不只在正式帧构造，锚点补算的每个出生时刻也构造一次。

**R5　热路径上的 LINQ 与分配是普遍写法，修复方式是加记忆层。**

管线各 partial 里约 379 处 LINQ 调用，`FrameEffectHost` 各 partial 约 275 处，其中不少在逐对象、逐实例循环内
（例如 `FrameEffectHost.cs:294` 每个变换一次 `Single` 线性查找，`:231`～`:233` 的 `Where` 里套 `Any`）。
M12 O1 测到过 995 MiB/s 的分配速率。O1 的八步修复几乎都是「按不可变输入的引用做记忆」，效果确实好
（96 行相对 8 行从 3.3 倍降到 1.04 倍），代价是缓存键和失效规则现在分散在八处以上，
每处都依赖「输入对象不可变且引用稳定」这个隐含约定。

顺带一提：纯度分析器对作者的逐帧 LINQ 会报 `MKV2009` 警告（`ScriptV2PurityAnalyzer.cs:22`），宿主自己没有遵守同一条标准。

**R6　代码密度偏高，影响审查。**

`Backend.Worker` 最长行 743 字符，`Backend.Hosting` 542，`Rendering.Skia` 398；`desktop/main.cjs` 319 行里有 36 行超过
200 字符，最长 1,944 字符。后者是 GPU 帧生命周期和进程身份校验所在，属于安全边界，这种密度下很难人工复核。

**R7　文档落后于代码。**

`backend/docs/architecture/render-pipeline.md` 的基线是 0.70.1，写着「正式像素后端明确是 Skia CPU 光栅」；
`SkiaRenderPipeline` 的类注释还在引用界面线程和 Avalonia 时代的文档。新读者会被误导。

### 3.4 判断

渲染系统的**臃肿是真实的**，但范围有限：集中在一个大类和一条图场景路径上，不是整体架构问题。
拆分的切口也清楚：先把布局／几何提供者独立出来（它本来就该是脚本宿主和绘制器共同的上游），
再在 `PreparedRenderFrame` 和 Skia 调用之间加一个对象级的计划步骤，哪怕第一版只做「部件清单 → 判空与合并 → 表面分配 → 执行」。

## 4. 脚本系统

### 4.1 作者接口：好

`IPureKaraokeEffect`（`V2/PureEffect.cs:6`）只有 `Declare` 和 `Evaluate` 两个方法；准备结果由宿主持有；输出句柄按能力和绑定范围
校验；随机数按流键取值所以调用顺序不影响结果。样例 `ClusterExit` 45 行就完成了带错峰的字簇退场，可读性好。
纯度分析器、能力声明、预算三样配合，设计上是完整的。

### 4.2 问题

**S1　v2 宿主是叠在 v1 上的翻译层，M12 的优化正在这套混合结构上进行。**

- `FrameEffectHost.Pure.cs:97`～`:171`：`ComposePureOutput` 把 v2 的 `TextWrite` 逐条翻译成 v1 的 `StyleUpdate` 和 `FrameOutput`。
- `FrameEffectHost.cs:235`～`:240`：每个 v2 实例求值前仍然先构造一份 v1 的 `FrameWriter` 和 `FrameContext`。
- `FrameEffectHost.Pure.cs:33`：v2 的 `ActiveEffect` 用 `null!` 填 v1 实例槽。
- `IsV2` 分支分布在 24 个文件；`FrameSession` 仍带完整的 120 Hz 重放循环和「v1 覆写了哪些入口」的反射判断
  （`FrameEffectHost.cs:103`～`:106`）。
- 测试与样例里仍有 86 个文件引用 v1 入口。

在 M13 删除 v1 之前这是预期状态，问题在**顺序**：O1 的记忆层、锚点补算、`Evaluate` 的跳过逻辑都长在这个混合宿主上。
删 v1 会重写 `FrameEffectHost.Evaluate` 和 `FrameSession`，届时这些优化要重新验证一遍。

**S2　属性 × 目标粒度的矩阵是手工展开的。**

`FrameEffectHost.Evaluate`（`:153`～`:369`，一个方法约 220 行）里有 16 个按「目标粒度 × 属性」分开的字典（`:173`～`:191`）；
`ComposePureOutput` 末尾按目标类型做了 9 遍过滤（`:151`～`:169`）。新增一种文字属性大致要改：

1. `TextOutput` 加方法；2. `TextWrite` 加子类；3. 能力字符串；4. 纯度分析器的调用白名单；
5. `ComposePureOutput` 的翻译；6. `StyleUpdate`／`StyleView`；7. `FrameEffectHost` 的跨实例合成；
8. `PreparedRenderFrame` 的字段与校验；9. `StaticStyleProjection`；10. `SkiaRenderPipeline` 的绘制。

以现有属性为例，辉光贯穿 7 个文件、网格变形 8 个（都不含绘制端）。

**S3　能力是散落的字符串字面量。**

约 40 个能力名（`token.affine`、`style.glow`、`visuals.dots` 等）以字面量出现在 SDK、宿主和 Core 的清单解析三处，
各 2～4 次，没有单一注册表。清单里顶层 `requires`、每个导出的 `outputs`、代码里的实际写入三处要保持一致，
其中前两处是作者手写 JSON。计划原则 P7 写的是「强类型键与能力描述共同驱动校验、补全、文档和 UI」，目前只做到了校验。

**S4　作者 SDK 程序集里混着宿主内部。**

`Scripting.Abstractions` 对 `Scripting` 和 `Rendering.Abstractions` 开了 `InternalsVisibleTo`；`TextOutput` 的构造器、
`Complete()`、`Invalidate()` 是公开成员，靠注释约定「作者不得自行调用」（`V2/TextOutput.cs:120`）。
v1 的 `IKaraokeEffect`、`FrameWriter` 与 v2 类型在同一程序集，作者的补全列表里两套都在。

**S5　每个实例各装载一份脚本模块。**

`FrameEffectHost.Pure.cs:24`：`LoadPure` 对每个实例 `new ScriptModule`。逐行应用模板时 120 行乘 5 个效果就是 600 份。
M12 把它列为已定位的线性来源，但 O1 的完成表里没有对应项，从记录看尚未处理。它主要影响装载和首帧。

**S6　作者体验的小项。**

- `Evaluate` 里要 `(ClusterExitData)data` 强转；泛型接口可以消掉。
- 清单里每个文件的 `sha256` 是手写的。
- `TextOutput` 构造器对每个字元做一次 `Any` 线性扫描（`V2/TextOutput.cs:28`、`:35`），每实例每帧一次。

### 4.3 判断

| 层次 | 扩展性 | 说明 |
|---|---|---|
| 包作者在现有能力内组合 | 好 | 变换、外观、粒子、晚期图元、材质、图都能用，纯函数模型降低了出错面 |
| 包作者需要新的图像处理 | 够用 | 图节点集是封闭的（Blur、Blend、Mask、Transform、MappedWarp 等），但有 `RuntimeShader` 作为出口 |
| 宿主新增一种属性或目标粒度 | 成本高 | 见 S2，没有表驱动的属性注册 |

所以「脚本扩展性不足」这个担心，落点应该在宿主一侧。只要属性集稳定，作者感受不到；一旦要加能力，每次都是一次贯穿六层的手工改动。

## 5. 技术栈与平台

### 5.1 技术栈

| 选型 | 评价 |
|---|---|
| .NET 10、C#、Roslyn 脚本 | 合理。脚本用宿主同语言，分析器能做到语义级；代价是 Worker 内存偏大（记录中 400～600 MiB） |
| SkiaSharp 3.119.4，Ganesh Vulkan | 合理。SkSL 着色器、文字、路径都现成。Ganesh 在 Skia 上游处于维护态，中期要留意 Graphite |
| SharpVk 0.4.2 | **风险。** 只用来建实例／设备／队列。印象中该库多年未发版，需核实；用量很小，替换成本低 |
| 自编译 `libSkiaSharp`（两个 `mako` 导出） | **风险。** `VulkanGpuFramePool.cs:190`～`:195` 依赖补丁符号，要钉死 Skia 提交并为每个平台各编一份 |
| Electron 45.2.1 `sharedTexture` | 可接受。API 较新，README 已固定版本；升级 Electron 需重新验收 |
| React 19、Vite 8、TypeScript 7，无状态库 | 合理。自写 `ObservableValue` 加按页 store，依赖很少 |
| FFmpeg 命令行（CliWrap） | 合理，跨平台 |
| 启动链 Node → Python → .NET → C++ | **偏重。** `scripts/app.mjs` 调 `host.py` 调 `dotnet publish`，再加原生构建脚本；打包分发时需要收敛 |

### 5.2 平台相关代码清单

「除了 GPU 纹理共享应该没有平台相关代码」这个判断不成立。实际如下：

| # | 位置 | 内容 | 是否 GPU 预览专属 | Windows 上的现状 |
|---|---|---|---|---|
| 1 | `native/gpu-preview/`（C++，2.6k 行） | DMA-BUF 导出池、N-API broker（Unix 套接字、`SO_PEERCRED`、`SCM_RIGHTS`） | 是 | 无实现 |
| 2 | `Rendering.Skia/VulkanGpuFramePool.cs`、`GpuPreviewNative.cs` | 对 #1 的 P/Invoke；描述符含 fd、fourcc、modifier | 是 | 无实现 |
| 3 | `Backend.Worker/GpuNativeChannel.cs` | Worker 侧原生通道 | 是 | 无实现；已有 `IGpuStreamChannel` 接口可挂第二实现 |
| 4 | `Rendering.Skia/SkiaRenderBackend.cs:235`、`:524` | 硬编码 `libvulkan.so.1`、`libdl.so.2` | **否**，所有 Vulkan 渲染都经过 | 会加载失败 |
| 5 | `Processes/LinuxProcessGuards.cs`、`OwnedProcessTree.cs`、`OwnedProcessGroup.cs` | 进程组、`RLIMIT_FSIZE`、子进程收割、读 `/proc` | **否** | 全部返回 false／null；导出要求的守护不成立（`WorkerProcessProxy.cs:197`～`:223`） |
| 6 | `Processes/MediaEnvelope/`（管理代码约 1.4k 行）＋`backend/native/media-envelope`（C，1k 行） | 通过 systemd transient unit 和 cgroup 限制解码进程 | 否，服务于媒体特征轨 | `IsInstalled` 返回 false，功能不可用 |
| 7 | `Media/MediaPresentationDeploymentDiscovery.cs` | 用 `ldd` 解析加载闭包 | 否，同上 | 同上 |
| 8 | `backend/tools/backend-web-host/host.py:128`、`:272`，`scripts/app.mjs:100` | `flock`、`-r linux-x64`、显式拒绝非 Linux x86_64、读 `/proc`、POSIX 信号 | **否** | 启动与发布都走不通 |
| 9 | NuGet 原生包 | 只引用了 `SkiaSharp.NativeAssets.Linux`、`HarfBuzzSharp.NativeAssets.Linux` | **否** | 缺 Win32 原生库 |

另外有约 25 处 `OperatingSystem.IsWindows()` 分支（路径大小写、Unix 文件权限），这些已经写对了，不算缺口。

有两点值得单独指出：

- **Linux 假设已经进了合同，不只在实现里。** [GPU 预览流 v1 合同](../architecture/gpu-preview-stream-v1.md)的字段里有
  `brokerPath`（`/tmp/...sock`）、`drmRenderMajor／Minor`、`modifier`、`planes[].fdIndex`。Windows 版需要合同的平台分支或 v2。
- **v2 脚本只支持 Vulkan（决策 D4）**，CPU 后端遇到 v2 效果直接拒绝，导出也一样
  （`ScriptBackendRequirement.cs:22`）。所以在 Windows 上，没有可用的 Vulkan 就没有任何脚本效果，
  这和 GPU 预览是否完成无关。

### 5.3 Windows 可行性

分三档看：

| 档 | 目标 | 主要工作 | 难度 |
|---|---|---|---|
| A | 能启动，浏览器 PNG 路径可用，无脚本效果 | #8 启动与发布按 RID 参数化；#9 加 Win32 原生包；`flock` 换成跨平台锁 | 小 |
| B | v2 脚本可用（预览走 PNG、导出可用） | #4 Vulkan 库名按平台解析；#5 用 Job Object 实现进程守护并找到文件大小限额的替代；验证 SharpVk 在 Windows 下可用，否则替换 | 中 |
| C | GPU 共享纹理预览，与 Linux 同等 | #1～#3 的 Windows 实现；Windows 版 `libSkiaSharp` 补丁构建；`desktop/` 改用 `ntHandle`；合同分支；多显卡适配器选择 | 大 |

C 档的技术路线，按我的理解（**未在本机验证，需要探针**）：

- Electron 的 `sharedTexture` 在 Windows 上接收 `ntHandle`（已在 `desktop/node_modules/electron/electron.d.ts:14648` 确认字段存在），
  即 D3D11 共享纹理的 NT 句柄。
- Worker 用 Vulkan 渲染，所以要么创建 D3D11 共享纹理并通过 `VK_KHR_external_memory_win32` 以 D3D11 纹理句柄类型导入 Vulkan 作为 blit 目标，
  要么改用 Skia 的 Direct3D 后端。前者保持单一渲染后端，像素验收可以复用，我倾向前者。
- 句柄跨进程不能像 fd 那样随消息传递，要用 `DuplicateHandle`；对端身份校验从 `SO_PEERCRED` 换成命名管道的客户端进程 ID。
- 双显卡笔记本上，Worker 选的物理设备必须和 Chromium GPU 进程用的适配器一致（按 LUID 匹配），
  否则共享句柄打不开。现有 `SelectGpuIndex` 是「独显优先」（`SkiaRenderBackend.cs:310`），在 Windows 上需要改成跟随消费者。

三项主要风险，按可能推翻方案的程度排序：

1. **Vulkan 与 D3D11 共享纹理在三家驱动上的实际表现**，以及 Electron 导入后的同步语义。这是唯一可能改变路线的未知项，
   建议像 Linux 的 P0 一样先做一个独立探针。
2. **进程守护的等价物**。Job Object 能覆盖进程树回收和内存上限，但 `RLIMIT_FSIZE` 没有直接对应，属于安全合同变更，需要单独设计。
3. **自编译 `libSkiaSharp` 的 Windows 构建**。工具链不同，耗时长，但没有技术未知。

有利条件：三个关键接缝已经存在且形状合适，分别是 `ISkiaRenderDevice`、`IGpuStreamChannel`、`mkg_gpu_pool_*` 的 C ABI
（获取、blit、导出、归还的状态机与平台无关，只有导出的句柄类型和描述符字段要分平台）。所以结论是**不需要改架构，工作量是实打实的移植**。

#6、#7（媒体封套）可以不移植：视频分析目前暂缓，Windows 上表现为功能不可用即可。

已有的 Windows 计划在另一个 worktree（`feat/desktop-packaging`），本次没有阅读，以上判断与它相互独立；两者有出入时建议逐项对一遍。

## 6. 前后端交互

### 6.1 现状

三条通道：

1. **浏览器或 Electron 渲染进程 ↔ Server**：loopback HTTP 加 SSE。119 个操作，结构是「命令端点＋读模型＋异步任务＋事件流」，
   修订用 CAS，请求带幂等回执。
2. **Electron main ↔ Worker**：原生 broker，传 GPU 帧和播放控制，不经过 Server。
3. **Server ↔ Worker**：stdio 上的长度前缀 JSON。

### 6.2 合理的部分

- **职责划分清楚**：后端拥有工程内容、修订和历史，前端只管草稿、选择、播放头。前端只通过 `MakoClient` 访问后端，
  依赖方向 `console → client → protocol` 有测试守着。
- **写操作收敛**：工程编辑走单一 `commands` 端点，而不是每种编辑一个路由。
- **GPU 流的分工合理**：低频可靠操作（创建、换输入、换尺寸、关闭）走 HTTP，高频的帧和控制走本机通道；
  前端控制器只有 137 行，是「最新状态邮箱」模型。
- **两条预览路径各有边界**，浏览器入口没有因为桌面入口被破坏。

### 6.3 问题

**F1　合同规模大，前端靠手写镜像。**

618 个 schema 对应前端 `src/protocol` 28 个文件、4,235 行手写类型，注释里写着「形状以冻结合同为准」；
`mako-client.ts` 单文件 2,210 行。类型没有从 OpenAPI 生成，一致性靠人和冒烟脚本。

**F2　合同副本与冻结流程是双仓库时期的遗留。**

同一仓库里合同存两份（`backend/web/` 与 `frontend/docs/backend/contract/`，本次比对五个文件完全相同）。
清单里记录的产品版本是 0.112.40，当前是 0.112.67，中间 27 个 PATCH 没有重新冻结；清单内嵌了大量源码文件的哈希，
这些必然已经漂移。`frontend/AGENTS.md` 仍然描述独立仓库、合同 1.40.0 和 Agent Mail 协作，
虽然根 `AGENTS.md` 声明了覆盖，但留着容易误导。合并为单仓库后，合同可以只留一份。

**F3　新增一个端点要改七处。**

`Backend.Contracts` 的 DTO、`Backend.Server` 的端点、`ProjectSessionRegistry` 的 partial、`ProjectSession` 的 partial、
行为源文档与生成的合同、前端 `protocol` 类型、前端 `client` 方法。其中 `ProjectSessionRegistry` 是 46 个 partial 文件共 7,749 行的
一个类，`ProjectSession` 是 37 个文件 5,973 行，两个类承担了几乎全部业务入口。

**F4　浏览器预览路径只能做静态预览。**

链路是 Worker 出 BGRA，base64 进 JSON，经 stdio 到 Server，Server 编 PNG、存入帧句柄，前端再 GET 成 object URL。
单条载荷上限 4 MiB（`WorkerProtocol.cs:18`），正好容纳 1280×720；前端 250 ms 检查一次、播放时最多 500 ms 请求一次
（`PreviewPanel.tsx:24`、`:101`），即最多 2 fps。作为静态预览没有问题，但这意味着**流畅预览完全依赖 GPU 共享纹理**。
在 Windows 的 C 档完成之前，Windows 用户没有中间档可用。如果浏览器入口要长期作为产品入口，值得评估一条低成本的中间路径
（例如二进制帧流）；如果它只是开发入口，可以明确写下来，不必投入。

**F5　Server ↔ Worker 的消息是一个扁平的联合记录。**

`WorkerMessage` 一个记录 36 个字段，服务 74 个消息种类常量；`Validate` 是一长串按种类排除字段的判断
（`WorkerProtocol.cs:30`～`:90`）；Worker 入口 `Program.cs` 784 行、43 个 `case`。像素走 base64 且每帧算 SHA-256。
目前能用，但每加一种任务都要在同一个记录和同一个校验函数里加字段和排除规则。

**F6　任何输入改动都换一个 Worker。**

决策 D17 规定 GPU 流每个输入代次用新 Worker。M12 实测：作者改动约 1.5 s 见新帧（其中重新固定输入约 580 ms）；
SDK2 内容下换源约 6.5 s，应用模板后 11～30 s 才见第一帧。**这是交互层面最大的实际臃肿**，
对应的 O3（换源复用 Worker 与已装载实例）尚未开工。改一个数值参数和换一份工程走同一条重路径，粒度太粗。

### 6.4 判断

合同的**设计**不臃肿，**维护方式**臃肿。短期收益最高的两件事是从 OpenAPI 生成前端类型、合同只留一份；
体感收益最高的是 O3。

## 7. 其他观察

- **测试量与约定不符**。项目约定写的是「测试用例随项目规模决定，避免写大量冗余测试代码」，实际测试 122k 行对产品 135k 行，
  `Rendering.Tests` 30k 行是被测项目 `Rendering.Skia` 的两倍多。M12 记录提到全量渲染测试有偶发失败（Roslyn 内部异常、遥测计数），
  靠单独重跑判定。测试越多，重构（R1、R2、S1）的搬迁成本越高，这一点在排期时要算进去。
- **遗留代码**：
  - `Media/SoundFlowAudioPlayback.cs`（371 行）及 `SoundFlow` 包依赖，在无窗口后端里只有测试引用。
  - `Backend.Rendering.Skia` 项目只有 1 个文件 69 行。
  - `desktop/p0/`、`probes/skia-gpu-target/` 是历史探针，但后者仍是桌面构建的输入（`scripts/desktop-build.mjs:36`），
    生产构建依赖 `probes/` 目录，位置容易被误删。
  - v1 样例 `samples/ScriptEffects` 与 Worker 的旧 `render` 消息种类。
- **流程成本**。每个 PATCH 配一条版本说明、一份证据登记、变异验证。这套纪律在 10 天 413 个提交的节奏下保证了可追溯，
  代价是文档量很大，且已经出现落后于代码的文档（R7、F2）。

## 8. 建议与优先级

按「现在不定，后面返工更多」排序。

| 优先级 | 建议 | 对应 | 理由 |
|---|---|---|---|
| 1 | 决定 v1 删除（M13）与 M12 剩余项（O3 等）的先后 | S1 | 优化长在混合宿主上；先删 v1，`FrameEffectHost` 和 `FrameSession` 能明显变薄，后续优化的面也小 |
| 2 | 把布局／几何提供者从 `SkiaRenderPipeline` 拆成独立类型 | R1 | 改动面可控（`ILineGeometryProvider` 接口已存在），拆完后一个会话只需要一个绘制管线 |
| 3 | 落地最小的对象级渲染计划 | R2、R3 | 让判空、合并、区域收紧、表面分配成为计划步骤而不是执行路径里的特判；三条调度路径有机会收成一条 |
| 4 | Windows 共享纹理探针 | §5.3 风险 1 | 唯一可能推翻路线的未知项，与渲染重构不冲突，可以并行 |
| 5 | 进程守护抽象成接口，加 Windows 实现的设计 | #5 | 属于安全合同，需要单独审查；不依赖 GPU 工作 |
| 6 | 文字属性改为表驱动注册，能力名集中定义 | S2、S3 | 在删 v1 之后做，届时翻译层已经没有了 |
| 7 | 前端类型从 OpenAPI 生成；合同只留一份 | F1、F2 | 机械性工作，立即降低每次合同变更的成本 |
| 8 | O3：输入改动按变化范围处理 | F6 | 体感收益最大，但牵涉 D17，需要先做第 1 项 |
| 9 | 启动链收敛，发布按 RID 参数化 | #8 | 打包前必做，现在做也不冲突 |
| — | 清理遗留、更新过期文档 | §7、R7 | 随手可做 |

第 1 项是需要用户拍板的顺序问题，我的建议是 **M12 在当前目标达成后尽快收口，先删 v1，再做 O3 和渲染计划**。
理由是 M12 记录显示 720p24 的目标在 F1～F6 之后基本达成（18 组全部 ≥23.99 fps），继续在混合结构上优化的边际收益在下降，
而每多做一项，删 v1 时要重新验证的东西就多一项。

## 9. 未覆盖与不确定

- 没有运行任何构建、测试或测速；没有在 Windows 上验证任何结论。
- 没有细读的部分：`Core/Lyrics`、`Timeline`、`Export`、`Application` 的编辑用例、SVG 编译器、粒子调度的内部实现、
  前端各页面组件（只看了 client、protocol、预览模块和各模块规模）。
- §5.3 的 Windows 技术路线基于对 Vulkan 外部内存扩展和 Electron API 的一般了解。`ntHandle` 字段已在本仓库的 Electron 类型定义里确认；
  驱动行为、同步语义、SkiaSharp 是否提供可用的 Direct3D 后端都需要核实。
- `SharpVk` 的维护状态是凭印象的判断，需核实。
- S5（每实例一份脚本模块）「尚未处理」是从 M12 记录的完成表里没有对应项推断的，没有逐个提交核对。
- 既有 Windows 计划备忘里提到「音频准备和包 Runtime 在 Windows 上也会被拒绝」，本次只核对了导出一项的守护要求。
