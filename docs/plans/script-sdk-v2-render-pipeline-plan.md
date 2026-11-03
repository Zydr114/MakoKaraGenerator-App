# 脚本 SDK v2 与渲染管线重构计划

状态：**计划（只读调研产出，尚未实施）**。日期：2026-11-03。
源码基线：App `41dc381`（工作树中他人未提交的导入相关修改不在本计划范围）；产品 `0.110.1`，
HTTP 合同 `1.42.1`／协议 `1`，工程 schema `29`，歌词 schema `18`，模板库 `7`，脚本配置库 `1`，
效果包 profile `packageVersion 2`／`sdkVersion "1.0.0"`／`skia-raster-3.119.4-v1`。

本文所有 SDK 代码、接口名、性能门槛均为**候选设计**，不代表已实现的公共 API 或测量结果。
历史性能数字均引自既有报告，本次没有重新测量。

## 0. 摘要

现有 v1 脚本系统的卡顿有三个来源：

1. **会话生命周期**：每次 seek、编辑都会新建 Worker 进程。编译缓存目录又按进程 PID 隔离，
   等于每次都冷编译 Roslyn。
2. **时间模型**：带状态脚本只能从 0 按 120 Hz 重放到目标时刻，所以 seek 的开销是 O(t)。
3. **GPU 执行方式**：每帧有多次同步 Submit、整面回读、逐个粒子绘制和大量 SaveLayer。

扩展性差的根因是 SDK 由"写入方法 × 目标类型"组成的封闭矩阵，状态又藏在作者对象里，宿主无法管理。

v2 的方向（均已由用户确认，见 §1）：

- 作者脚本只写 `Evaluate(t, 查询)` 纯函数，**RenderAt(t) 是一等能力**。
- 所有与历史相关的信息，由 Worker 内的渲染宿主在加载时预计算成**轻量只读索引**，脚本只查询。
- 输出改为"目标 + 属性键"的数据写入。像素工作全部留在 GPU，CPU 与 GPU 之间只传小数据。
- 渲染层保留 Skia Ganesh Vulkan，中间加一层与后端解耦的**渲染计划（Render Plan）**，做批处理和区域裁剪。
- 编译归 Server，执行归常驻 Worker。seek 不再换进程，编辑也不再冷编译。
- v1 不兼容、直接淘汰。工程 schema 按现行门禁从 29 升到 30。

## 1. 已确认决策

| # | 决策 | 结论 |
|---|---|---|
| D1 | v1 兼容 | 不兼容，直接淘汰；无适配层、无源码迁移 |
| D2 | 脚本形态 | 继续 C#（Roslyn）；只允许纯函数 `Evaluate`，禁止 `Advance/OnEvent`/字段累积状态 |
| D3 | 状态归属 | Worker 内渲染宿主在加载时预计算只读索引；脚本每帧本地查询，无跨进程往返 |
| D4 | 后端范围 | 本期只实现 Vulkan；CPU 后端遇到 v2 效果明确报不支持（导出也走 Vulkan），保留后端抽象 |
| D5 | SVG | 允许新增依赖（选型见 §5.6） |
| D6 | 旧工程 | 按现行精确门禁整体升级 schema 29→30，旧工程一律拒绝；模板库、脚本配置库同步升版，按既有机制隔离旧数据 |
| D7 | 渲染引擎 | 保留 Skia Ganesh Vulkan，重构为渲染计划 + 执行器；计划层与后端解耦，将来可替换为自研 Vulkan |
| D8 | 首期范围 | 对齐 v1 全部可用能力，并能写出目标复杂样式；重写 9 个样例后再删除 v1 |
| D9 | 媒体自适应 | GPU 着色器采样背景 + 后台预计算低分辨率视频特征轨（仿照音频分析）；C# 与着色器都可读，支持时间平滑 |
| D10 | 行退场窗 | 包声明退场时长；宿主在行原本隐藏之后再延长一段只用于视觉的"退场副本"，不影响布局，可与下一行重叠 |
| D11 | 粒子出生锚点 | 采用脚本变换后的最终几何；宿主按 120 Hz 缓存最终播放头轨迹，seek 只需补算最长寿命窗口（寿命设上限） |
| D12 | 成本约束 | SDK 设计必须显式考虑 GPU 绘制与 CPU↔GPU 拷贝成本（§4.5） |

目标复杂样式（验收样例）：随当前播放头所在的字放大强调；当前句退场时各字符倾斜、扭曲后向左下方飞行并淡出；
随播放头有飞行粒子，星星 SVG 扫过整句；字符描边随背景媒体颜色自动变色以保证可读性。

## 2. 现状诊断（证据）

### 2.1 会话生命周期：seek 和编辑都会换进程、冷编译

- HTTP 预览 seek 先换代次并调用 `Coordinator.Invalidate()`
  （`backend/src/MakoKaraGenerator.Backend.Hosting/BackendPreviewSessionRegistry.cs:299-310`）。
  协调器随后销毁并重建会话（`BackendPreviewCoordinator.cs:214/221/245`）。
- 每个会话都 `new WorkerProcessProxy`（`WorkerPackageRenderFrameSessionFactory.cs:118-133`），
  再 `Process.Start` 并握手（`WorkerProcessProxy.cs:161-199`）。
- GPU 流的每次输入替换同样新开 Worker（`GpuStreamWorkerSession.cs:54`）。
- Worker 的编译产物目录是 `makokara-worker-package-{ProcessId}`（`Backend.Worker/PackageRenderSession.cs:55`），
  按进程隔离，所以 `ScriptCompiler.cs:96` 在新进程里永远命中不了缓存，每次都冷启动 Roslyn。
- 既有数据：组合场景单建会话约 1.1–1.2 s；GPU 流从 UI 调用到输入替换帧 p95 为 1202 ms，
  从新代次 hello-ready 起算是 164 ms（`docs/performance/gpu-preview-production-2026-11-01.md:156-159`）。

### 2.2 时间模型：O(t) 重放

- 往回请求会重置会话（`Scripting/Execution/FrameSession.cs:33`）。重置时重新 Load
  （新 `ScriptModule`，`FrameEffectHost.cs:74`），再从 0 按 120 Hz 重放。
  文档记载：第一次请求 60 s 处要执行 7,201 次 Evaluate。
- `CompiledProjectEffects.cs:121` 一律返回时间线工厂；纯帧工厂只有测试在用。
- 放大器：
  - 每个不同的出生时刻多做一次完整 Evaluate（`FrameEffectHost.Particles.cs:26`）；
  - 每个桥接端点都单独从 0 重放一个会话（`SemanticAnchorQuery.cs:44`）；
  - 重置时清空端点缓存（`FrameEffectHost.cs:245`）。
- 粒子发射配额是逐个光标头累加的精确分数（`Particles/ExactEmissionQuota.cs`），依赖逐步推进的历史。

### 2.3 每帧宿主开销

- 每次 Evaluate 都完整采样场景、重建绑定范围和样式基线，并对每个实例做 `Where(...Any(...))` 过滤。
  按 30 fps 输出，每帧约 4 个 120 Hz 边界。
- 场景元素测量对"元素 × 行"做 LINQ 嵌套，并重复解析样式和注音布局
  （`Rendering.Skia/SkiaRenderPipeline.SceneFrame.cs`）。
- 字族变换循环内用 `TokenGeometries.Single(...)`，复杂度 O(n²)
  （`SkiaRenderPipeline.FamilyTransforms.cs`）。
- 两个 Skia 管线（一个提供几何、一个负责渲染）各自维护整形缓存。

### 2.4 GPU 执行与拷贝点

| 位置 | 现状 | 代价 |
|---|---|---|
| `SkiaRenderBackend.cs:330-336` | `FlushAndSubmit` 调用 `Submit(true)`，CPU 同步等待 GPU | 每个中间表面一次全管线停顿 |
| `SkiaRenderPipeline.ImageScene.Surfaces.cs:14`、`GroupIsolation.cs:107`、`SceneSurfaces.cs:64` | 中间表面前后各一次同步 flush | 单帧多次停顿 |
| `SkiaRenderPipeline.ImageScene.cs:272-286` | `IsEmptyOriginalSurface` 整面回读到 CPU 判断是否为空 | GPU→CPU 拷贝 + 同步，按部件触发 |
| `SkiaRenderPipeline.Visuals.cs:46-72` | 点、线段、精灵逐个调用 `DrawCircle/DrawLine/DrawImage` | 预算上限 10 万项，绘制调用数随项数线性增长 |
| `SkiaRenderPipeline.TextPainting.cs:65/97`、`Glow.cs:21-28` | 描边、材质、辉光、阴影都走 `SaveLayer`/图像滤镜 | 离屏目标切换与填充率 |
| `BackgroundFrameSources.cs:217-245`、`FfmpegVideoFrameProvider.Source.cs:110` | ffmpeg 解码出全分辨率 BGRA → 管道 → 托管数组 → 逐行拷进 `SKBitmap` → 绘制时隐式上传 | 1080p 每帧 8.29 MB，经过 3 次 CPU 拷贝和 1 次上传 |
| `SkiaRenderPipeline.cs:112-131` | HTTP 预览／导出：整帧同步回读 | 必需，但当前与渲染串行 |
| `SkiaRenderPipeline.cs:144` | GPU 流：一次 GPU blit 到导出槽，不回读 | 已是零回读路径 |

既有基准（`backend/docs/performance/20261028-structural-optimization/`）：材料场景每帧中位数，
CPU 后端约 2.9–3.4 s，Vulkan 约 0.3 s。10-28 报告中的后续项 C（惰性空部件检测）、D（GPU 小结果校验）、
E（区域裁剪、图融合、GPU 常驻输出）都还没做。

### 2.5 扩展性根因

1. **能力矩阵封闭**：共 27 个写入重载。比如 Host、预备帧和 Skia 早就支持任意仿射，
   SDK 却只给字族开放缩放（`Scripting.Abstractions/FrameWriter.cs:155`）。
   每加一个能力都要同时改 SDK、宿主合成、`PreparedRenderFrame` 校验、Skia 和文档计数。
2. **输入面窄**：
   - 内部有 `ExitProgress`，但传给脚本时被丢掉（`Projection/FrameProjection.cs:27-32`），
     而且只有槽位序列布局有退场窗（`LyricSceneSampler.LeadIns.cs:104-117`）；
   - 没有媒体分析、矢量路径和逐字簇目标；
   - 图里 Transform 节点的矩阵只能写字面量。
3. **状态模型隐式**：状态存在作者字段和宿主游标里，没法快照，也没法跳到任意时刻。
4. **输出逐帧重新下发命令**：没有可复用的场景节点或光栅缓存，难以做增量和 GPU 常驻。
5. **版本严格相等**：`ProjectEffectsJson.cs:12` 要求 `sdkVersion == "1.0.0"`，没有能力协商。

## 3. 设计原则

- **P1 纯时间函数**：作者输出只取决于 `(固定输入, t)`。任意时刻都可以单独求值，
  求值顺序和次数不影响结果。
- **P2 宿主持有一切历史**：事件、相位、播放头轨迹、粒子出生表和媒体特征都是宿主建立的不可变索引。
  脚本只读，可查询过去和未来的**源**时间线，但不能读其他实例的输出。
- **P3 小数据过界，像素留在 GPU**：脚本只产出参数，每帧通常是 KB 量级。
  任何依赖像素的判断都交给着色器或预计算特征轨。SDK 不提供从 GPU 读像素到 C# 的接口。
- **P4 声明能力，宿主编排**：包清单声明所需能力、资源和发射器，宿主据此规划区域、批次和预算。
  新能力以"属性键/图元类型"的方式加法扩展，不新增重载矩阵。
- **P5 交互成本有上界**：
  - seek 与 t 无关，只多补算"最长粒子寿命"窗口；
  - 参数编辑不重新编译；
  - 源码编辑只重编被改的包，而且在已预热的 Server 编译器里完成。

## 4. 目标架构

### 4.1 进程分工与生命周期

```text
Server（长寿命）                        Worker（常驻，Vulkan）
├─ 包校验、Roslyn 编译                  ├─ 加载 PE（可回收程序集上下文），不编译
├─ 产物缓存 {StateRoot}/script-artifacts├─ 建立宿主索引（§4.2），按输入差量重建
├─ 视频/音频特征分析调度与缓存           ├─ 每帧：采样 → Evaluate → 合成 → 渲染计划 → 执行
└─ 现有 HTTP、owner/CAS、500ms 控制     └─ 输出：GPU 租约（桌面）/ 回读（HTTP、导出）
```

- **编译**：Server 已为作者流程持有 `ScriptCompiler`（`ProjectSessionRegistry.Author.cs:78`）。
  v2 统一在 Server 编译，产物按内容地址存在 profile 内。Worker 通过运行时输入收到 PE/PDB 字节与 `ArtifactHash`，
  校验后加载；Worker 自身不再加载 Roslyn。
- **seek**：同一 Worker、同一运行时会话内完成。HTTP 的"换代次"语义保留在 Host 侧
  （帧缓存失效、请求序号），不再销毁会话。
- **输入变化**：
  - 参数变化：只替换参数值，并重建受影响的索引；
  - 歌词或布局变化：差量重建受影响行的布局与相位索引；
  - 包源码变化：Server 增量编译，Worker 在同一进程卸载旧模块、加载新模块。
- **GPU 流**：现有 v1 设计是"每个输入代次换新 Worker"，并有对应的身份复核证据。
  首选保留这一语义，同时维持一个**预热的备用 Worker**（已握手，Vulkan 与 Skia 已初始化，已加载引用集），
  切换时只付输入装载成本。进程内替换作为后续候选，需同步修订 `docs/architecture/gpu-preview-stream-v1.md` 与其回归。
- **失败隔离**：作者异常导致的失败只隔离该实例和该帧，不污染宿主索引。宿主索引不可变，失败后无需重放。

### 4.2 宿主索引（轻量只读数据结构）

全部在加载或差量更新时建立。查询复杂度为 O(log n) 或 O(1)，内存与工程规模成线性关系。

| 索引 | 内容 | 典型查询 |
|---|---|---|
| `TimelineIndex` | 13 类源事件按类型、来源排序的数组（起唱、行演唱、装载进出、可见性、前奏、光标转换、音频 tick） | `Last(kind, filter, t)`、`Next(...)`、`Count(kind, filter, [a,b))`、`NthOccurrence` |
| `PresentationIndex` | 每次行装载的揭示、隐藏、进场窗、演唱区间、**退场副本窗 [hide, hide+exit)**、槽位 | `Phase(binding, t)` 返回 Preparing/Entering/Performing/Retained/Exiting 以及各段进度 |
| `LayoutIndex` | 行与样式 → 整形结果、字簇（DrawUnit）、注音布局（缓存，两条管线共享） | 基础几何；时变变换由 `LyricSceneSampler` 纯函数给出 |
| `CursorIndex` | 每条 `CursorTrackId` 的分段进度路径 | `BaseCursorAt(track, t)` |
| `FinalCursorMemo` | 实例链求值后的最终播放头位置，按 120 Hz 网格懒计算，窗口化 LRU 缓存 | 供粒子出生锚点使用（D11），两个网格点之间线性插值 |
| `ParticleSchedule` | 每个发射器声明 × 光标头的闭式出生序列；Burst 来自 `TimelineIndex` | `AliveAt(t)`：枚举 [t−maxLife, t] 内的出生 |
| `MediaFeatureTrack` | 背景视频的低分辨率颜色与亮度网格轨（§5.7） | `Sample(rect, t, window)`；着色器侧以纹理形式访问 |
| `AudioFeatures` | 沿用现有分析结果 | `GetAt/GetRange` |

退场副本（D10）：

- 包声明 `lineExit.durationUs` 后，宿主让该装载在 [hide, hide+exit) 内继续存在，阶段为 `Exiting`。
- 这段时间只参与绘制，不参与布局、不占槽位、不产生演唱事件。光标不进入退场副本。
- 多个实例声明的时长取最大值。
- 必须在预算内（候选上限 3 s），超出直接拒绝，不静默截断。

### 4.3 每帧执行流

1. `SceneSample(t)`：纯采样。同一 t 内的几何、Evaluate 和出生锚点共享同一份结果。
2. 实例按工程顺序执行 `Evaluate(in Frame, Output)`，写入结构化数组形式的输出缓冲（无每帧托管分配）。
3. **合成**：按属性键规则合并多个实例的输出（仿射按调用顺序组合，透明度相乘，Set 覆盖），并校验预算。
4. 生成**渲染计划**（§4.4）：排序、批次、区域、缓存命中、表面预算，在执行前确定性地完成预算检查。
5. 由 Skia Vulkan 执行器执行：单帧只在最后提交一次，中途不同步等待。
6. 输出：
   - 桌面：GPU blit 到导出槽（现有路径）；
   - HTTP 预览与导出：回读整帧，可与下一帧的 CPU 阶段流水重叠。

### 4.4 渲染计划 IR 与 Skia 执行器

渲染计划是与后端无关的有序指令集，包含以下节点：

- 背景（静态图／视频纹理）
- `BehindText` 图元批次
- 文字项：行、字族、字簇。每项带变换、透明度、填充、描边、阴影、辉光、材质及图绑定
- 图节点通道（带区域）
- `AboveText` 图元批次

执行器策略：

- **光栅缓存**：外观在一段时间内不变、只有仿射或透明度变化时（强调、飞出等最常见的情况），
  把字项的完整外观（阴影、辉光、描边、填充）光栅化为 GPU 纹理一次，之后每帧只画一个带变换的贴图四边形。
  - 缓存键为（整形结果、样式哈希、栅格比例档位）；
  - 放大超过档位时升档重栅格化，生成 mipmap；
  - 外观本身随时间变化（材质 uniform 改变等）时，回到矢量直绘。
- **网格扭曲**：非仿射扭曲用缓存纹理配合 `DrawVertices` 网格。脚本只写低分辨率网格偏移，
  替代 v1 的 ColorOnly 图位移。
- **批处理**：
  - 点、精灵、粒子用 `DrawAtlas`，一个批次一次调用；
  - 拖尾和线段合并为一个 `DrawVertices`；
  - SVG 图元按 §5.6 选择矢量回放或图集。
- **区域裁剪**：材质、图、模糊按目标墨迹框加 `expand` 分配表面，不再使用整画布。
  完成 10-28 报告中的 E 项（区域裁剪、图融合）。
- **去掉同步点**：中间表面不调用 `Submit(true)`；空部件判断改为由渲染计划在 CPU 侧根据几何与样式推断，
  删除整面回读（10-28 报告 C 项）。
- **背景视频**（候选）：ffmpeg 直接解码到目标分辨率，或上传 NV12（每像素 1.5 字节）再由着色器转色。
  这能减少约 60% 上传量；具体收益以 S0 基准为准。

### 4.5 GPU/CPU 成本模型与 SDK 约束

| 数据或操作 | 量级（按 1080p 计算） | SDK／宿主规则 |
|---|---|---|
| 脚本每帧输出 | 200 个字 × ~64 B ≈ 13 KB | 允许；整体上传一次 |
| 粒子实例 | 2 万 × ~36 B（xform + uv + color）≈ 720 KB | 用 `DrawAtlas` 数组一次上传；不允许逐个调用 |
| 整帧 BGRA | 8.29 MB | 只在 HTTP 与导出时回读；脚本永远拿不到 |
| 视频帧上传 | 8.29 MB（BGRA）／3.1 MB（NV12） | 宿主负责；脚本只能经着色器或特征轨使用 |
| 中间表面 | 区域面积 × 4 B | 必须声明或推断区域；预算在渲染计划阶段检查 |
| GPU→CPU 同步 | 一次完整管线停顿 | 单帧最多一次（最终输出）；SDK 不提供像素读取 |
| 媒体特征轨 | 32×18 × 4 B ≈ 2.3 KB/帧；3 分钟 30 fps ≈ 12 MB | 预计算并持久化；C# 查询走 CPU，着色器绑定为纹理 |

时间成本（ms）统一在 S0 基准中测量后再登记，本文不预估。

## 5. SDK v2 设计（候选）

### 5.1 包清单 v3

```jsonc
{
  "packageVersion": 3,
  "sdkVersion": "2.0.0",              // 主版本严格一致；宿主次版本 ≥ 包次版本即可接受
  "shaderProfile": "skia-ganesh-3.119.4-v2",
  "requires": ["token.affine", "cluster.affine", "line.exit", "media.features", "path.svg"],
  "lineExit": { "durationUs": 600000 },
  "emitters": [ { "id": "sparks", "kind": "cursor", "maxLifetimeUs": 1200000 } ],
  "files": [ /* path/role/sha256，新增 role: "svg" */ ],
  "parameters": [ ... ], "exports": [ ... ]
}
```

- **能力协商**：宿主公布能力集。`requires` 中有不支持的能力时，在加载阶段报
  `PackageCapabilityUnsupported`；次版本新增能力属于加法扩展。
- **删除的声明**：订阅（`subscription`）、`simulation.stepHz`、只与历史推进相关的预算项。

### 5.2 入口与纯度保证

```csharp
public sealed class MyEffect : KaraokeEffect          // 候选名
{
    public override void Declare(DeclareContext d) { } // 加载期一次：只读预计算，可写 readonly 字段
    public override void Evaluate(in Frame f, Output o) { } // 纯函数
}
```

C# 没有语言级沙箱，纯度靠三层保证：

1. **编译期分析器**（随 `ScriptCompiler` 发布）：
   - 实例字段必须 `readonly`，且只能在 `Declare` 里赋值；
   - 禁止可变静态字段；
   - 禁止 `System.Random`、`DateTime.Now`、`Stopwatch`、`Environment`、IO、线程、反射发射；
   - 拒绝 `async`/`unsafe`。
   违反时报编译诊断（沿用作者诊断 DTO）。
2. **运行期确定性探针**：作者校验任务按"顺序 / 乱序 / 重复"三种方式在随机时刻求值，
   比对输出哈希。不一致报 `NonDeterministicEffect`。
3. **文档合同**：明确作者代码是本机可信代码，分析器只防误用，不防恶意。

### 5.3 输入 `Frame`

- `TimeUs`，`Canvas`。
- `Lines`：每次装载的身份、`Phase` 及 `EnterProgress/PerformProgress/ExitProgress`、几何（基础框、基线、世界矩阵）。
- `Tokens`／`Clusters`：
  - `Active` 进度：未唱 0、演唱中 (0,1)、已唱 1；
  - `LocalTimeUs`（相对起唱的有符号时间）；
  - 墨迹框、`DrawUnit` 范围；
  - 是否可独立变换。
- `Cursors`：基础播放头（`CursorIndex`）。
- 查询服务：
  - `Events`：`TimelineIndex` 的只读查询，可查未来的源事件；
  - `Layout.At(t)`：任意时刻的基础布局；
  - `Media`：特征轨；
  - `Audio`：音频特征；
  - `Random`：沿用现有 PCG32 计数式接口，`(streamKey, index)` 纯读取；
  - `Parameters`、`Resources`。
- 不提供：其他实例的输出、上一帧输出、像素。

### 5.4 输出：目标 × 属性键

目标：`Line`、`Token`（字族，含注音）、**`Cluster`**（新增，逐字簇）、`Mark`、`Group`、`Ruby`（候选）。

| 属性键 | 合成规则 | 说明 |
|---|---|---|
| `Transform` | 按实例和调用顺序右乘 | 完整仿射；要求可逆；对所有目标开放 |
| `Opacity` | 相乘 | 最终结果须在 [0,1] |
| `Fill.Unsung/Sung`、`Stroke`、`Shadow`、`Glow` | 后写覆盖 | 辉光改为可动态写入 |
| `Material(slot).Uniform(node, name)` | 后写覆盖 | 要求该部位已安装对应包材质 |
| `Graph` | 整体替换 | 图参数和 Transform 节点矩阵可绑定脚本值 |
| `Warp` | 后写覆盖 | 低分辨率网格偏移（如 8×4），走 `DrawVertices` |

图元：`Dots`、`Segments`、`Trails`、`Sprites`（位图／SVG 图集）、`Paths`（SVG 矢量或 SVG path data）。
每个批次只调用一次。粒子由宿主自动绘制，或以 `Custom` 模式交给作者用图元绘制。

扩展方式：新增一个属性键或图元类型，只需加注册项、合成规则和执行器分支，不再新增重载矩阵。

### 5.5 粒子、Burst 与桥接（纯化）

- **连续发射**：在清单和 `Declare` 中声明发射器（速率、寿命上限、外观描述）。
  出生时刻按光标头起点做闭式计算：`t_k = start + k / rate`（分段常速）。
  速率可以是参数，但不能随帧任意改变，所以闭式计算始终成立。
- **随机**：用 `(实例种子, 发射器, 光标头, k)` 计数式 PCG32 生成，替代 v1 的递增 `NextIndex`。
- **锚点**（D11）：出生位置取 `FinalCursorMemo` 中出生时刻的插值。seek 到 t 时最多补算
  `maxLifetime × 120` 次实例链求值（候选寿命上限 5 s，即 ≤ 600 次），超出预算直接报错。
- **运动**：沿用解析运动（初速度、恒加速度、年龄曲线）。拖尾在 [t−trail, t] 内解析采样，无需历史。
- **Burst**：在清单中声明"源事件类型 → 发射描述"的映射，出生时刻取自 `TimelineIndex`，
  不再需要 `OnEvent`。
- **桥接**：端点由 `PresentationIndex` 与 `FinalCursorMemo` 纯计算，不再按端点从 0 重放。

### 5.6 资源：SVG、图像、着色器

- **SVG 依赖**：`Svg.Skia 4.9.1`（MIT）。它依赖 `SkiaSharp ≥ 3.119.2`，与仓库锁定的 3.119.4 兼容，
  无需升级 Skia；`HarfBuzzSharp ≥ 8.3.1.3` 也满足。传递依赖包括 `Svg.Custom/Model/SceneGraph/Animation`、
  `ShimSkiaSharp`、`ExCSS 4.3.1`。5.x 版本要求 SkiaSharp 4.148，不采用。
- **SVG 安全预检**：
  - 拒绝外部引用（`xlink:href` 指向非内嵌资源）、脚本和外部字体；
  - 限制元素数、嵌套深度、路径点数和文件大小；
  - 加载时转换为 `SKPicture`，结果纳入包闭包哈希。
- **SVG 两种用法**（在清单中声明）：
  - `vector`：每帧回放，保持清晰，适合少量大尺寸图元；
  - `atlas`：加载时按声明的最大像素尺寸栅格化进图集（含 mipmap），适合大量粒子或精灵。
- **图与材质**：
  - 保留 RuntimeShader、Blur、Mask、Transform、Blend 节点；
  - Transform 节点矩阵与 uniform 可绑定脚本输出；
  - 输入新增 `MediaFeatures`（特征轨纹理）；
  - 默认按目标区域执行。
- **字体**：沿用 `font` 角色的固定字体目录。

### 5.7 媒体特征轨

- **生成**：仿照音频分析的 Worker 任务（`Backend.Worker/StaticAudioAnalysisTask.cs`）。
  ffmpeg 缩放到 32×18（候选值），按背景帧率输出每格的线性 RGB 均值和亮度，以媒体 SHA-256 为键持久化在 profile 中。
- **C# 侧**：`Media.Sample(rect, t, windowUs)` 返回窗口内时间加权的均值、亮度和对比度。
  只要窗口和数据确定，结果就确定。
- **着色器侧**：作为纹理加时间 uniform 绑定，在 GPU 上做同样的窗口平滑。
- **可用性**：
  - 分析未完成时返回 `Unavailable`，预览照常运行，诊断里提示；
  - 导出必须等分析完成，以保证预览与导出一致；
  - 透明输出或没有背景时返回 `NoMedia`。

### 5.8 目标样式写法（示意，不可编译）

```csharp
public sealed class StarLyric : KaraokeEffect
{
    public override void Evaluate(in Frame f, Output o)
    {
        foreach (var line in f.Lines)
        foreach (var c in line.Clusters)
        {
            var n = o.Cluster(c.Id);
            double a = c.Active.Bell(Ease.OutBack);                 // 唱到时放大，唱完回落
            double x = line.ExitProgress;                           // 退场副本阶段 0→1
            double d = Ease.In(Math.Clamp(x * 1.4 - c.IndexInLine * .03, 0, 1)); // 逐字错开
            n.Transform = Affine2.ScaleAt(1 + .25 * a, c.InkBounds.Center)
                        * Affine2.SkewAt(-.5 * d, 0, c.InkBounds.Center)
                        * Affine2.Translation(-180 * d, 120 * d);
            n.Warp = Warp.Bend(.3 * d);                             // 非仿射扭曲
            n.Opacity *= 1 - d;
            var bg = f.Media.Sample(c.WorldInkBounds, f.TimeUs, windowUs: 250_000);
            n.Stroke = bg.IsAvailable ? Stroke.Solid(bg.Luma > .5 ? Color.Black : Color.White, 3) : n.Stroke;
        }
        foreach (var cur in f.Cursors)                              // 星星沿当前行扫过
            o.Paths("star").Add(Resources.Svg("star"), cur.LinePath.At(cur.LineProgress), scale: .6);
    }
}
// 粒子：在清单中声明 emitters[sparks]（cursor），无需在 Evaluate 中写代码
```

逐像素自适应描边也可以改用材质：描边材质图以 `MediaFeatures` 或 `BackgroundMedia` 为输入，在着色器里计算反差色。

### 5.9 v1 能力映射（D8 对齐清单）

| v1 | v2 |
|---|---|
| `ScaleTokenFamily`、行/组/标记 `ApplyAffine` | 所有目标的 `Transform` 属性（新增字族与字簇） |
| Style 11 个重载 | `Fill/Stroke/Shadow/Glow/Opacity` 属性键 |
| `Materials.SetUniform` ×2 | `Material(slot).Uniform` |
| `ImageEffects.Bind` ×4 | `Graph` 属性（组目标随新执行器一起支持，原先拒绝） |
| `OnEvent` + `Bursts.Queue` | 清单 Burst 映射 + `TimelineIndex` |
| `EvaluateEmitters` + `EmitterWriter.Set` | 清单或 `Declare` 中的发射器声明 |
| `Advance/AdvanceVisuals` 共享状态 | 删除，改用索引查询 |
| `EvaluateVisuals` + Overlay 4 种 | `Evaluate` 内的图元批次，新增 `Paths` |
| `History.GetStarts`、`GeometryQueries.GetBaseAt` | `Events.*`、`Layout.At(t)` |
| `AudioFeatures`、`DeterministicRandom` | 原样保留 |
| `AnchorMap`/`GeometryMapping`（未生产） | 由 `Warp` 取代；不再保留 |
| 9 个样例 | 全部重写为 v2（S6） |

## 6. 兼容性与版本影响

| 层 | 影响 | 处理 |
|---|---|---|
| 公共 HTTP 结构（112 路径） | 包清单以不透明 JSON 传输（`manifestJson`），OpenAPI 未约束 `sdkVersion`；诊断 `stage` 是字符串 | 结构基本不变。以下变化按加法处理，合同升 MINOR（1.42.x → 1.43.0）：新增错误码（`EffectsRequireGpuBackend`、`PackageCapabilityUnsupported`、`NonDeterministicEffect` 等）、媒体特征分析状态、`stage` 新取值 |
| 浏览器静态预览协议、500 ms 节奏 | 无 | 只做内部优化 |
| GPU 流协议 | 默认无（采用备用 Worker 方案时） | 若改成进程内替换，需修订 v1 架构文档与身份回归 |
| Worker IPC | 私有，会变：新增产物传递和差量输入，去掉 Worker 内编译 | 同步维护 Worker 合同与回归 |
| 工程 schema | 效果嵌在 `project.json`（`ProjectStore.cs:59`）；删除 simulation/subscription | 29→30（`Project.cs:33`）；旧工程按精确门禁拒绝（D6） |
| 效果 JSON `contractVersion` | 1→2 | 随 schema 30 一起发布 |
| 模板库（effect recipe） | 引用 v1 包 | 7→8（`StyleTemplateLibrary.cs:32`），旧库按既有版本隔离机制保留数据但不可用 |
| 脚本配置库 | 引用 v1 包 | 1→2（`ScriptConfigurationLibrary.cs:28`） |
| 预设应用 JSON | 仅当绑定结构变化时 | S6 核对后决定 |
| 包 profile | 2/"1.0.0" → 3/"2.0.0"；新 shader profile | v1 包加载即报 `PackageVersionUnsupported` |
| CPU 后端 | 带 v2 效果的输入不可选 CPU | 明确报错；无效果的工程仍可走 CPU |
| 前端 | 不依赖 SDK 标识（`frontend/src` 中检索 `EffectBase/IKaraokeEffect/sdkVersion` 为 0） | 需要处理：新错误码文案、特征分析状态、旧 `.mktpl` 导入失败提示 |
| 产品版本 | 不兼容修改 | 按 1.0 前规则升 MINOR；S1 属内部行为改进，同样按语义递增 |
| 文档 | SDK 参考、架构、API、ADR | 每阶段同步（§7） |
| 测试 | 涉及 v1 的约 65 个测试文件、约 1.6 万行 | 在 S7 随 v1 删除；v2 测试按规模适度补齐 |

## 7. 实施阶段与出口

每个阶段按"一步一提交、中文 `type(scope): 摘要`"推进，阶段完成后推送私有远程。
代码阶段结束要求 `dotnet build MakoKaraGenerator.slnx` 零警告零错误，并运行相关定向测试。

### S0 基线与基准

- 建立可复现的 Vulkan 基准工具，场景包括：无效果、v1 组合、v1 粒子，以及将来的 v2 目标样式。
- 指标：
  - seek→首帧（HTTP 与 GPU 流）；
  - 参数编辑→首帧；
  - 源码编辑→首帧；
  - 稳态 1080p30 帧时间 p50/p95/p99；
  - 每帧同步次数、回读字节数、上传字节数、中间表面字节数。
- 出口：记录 v1 基线数值，作为后续候选门槛的对照。

### S1 生命周期（与 SDK 无关，最先做）

- Server 统一编译，产物缓存放到 `{StateRoot}/script-artifacts`；Worker 接收产物、不再编译。
- HTTP 预览 seek 复用同一 Worker 会话，Host 侧仍按代次失效帧缓存。
- 增加 GPU 流备用 Worker 预热。
- 不做任何 v1 专属优化（如检查点）。
- 出口：
  - v1 场景下 seek 不再新建进程、编辑不再冷编译；
  - 现有 HTTP、owner/CAS、排水回归全部通过；
  - S0 指标对比登记。

### S2 宿主索引与纯求值内核

- 实现 `TimelineIndex/PresentationIndex/LayoutIndex/CursorIndex`，加入退场副本相位。
- 两条管线共享整形与注音缓存；消除 §2.3 中的 LINQ 与 O(n²) 热点。
- v2 运行时骨架：清单 v3 解析、能力协商、`Declare/Evaluate`、目标 × 属性键输出与合成（先做变换、透明度、样式）。
- 编译期分析器与确定性探针。
- v1 与 v2 按 `packageVersion` 分派，开发期并存。
- 出口：v2 只含变换和样式的包，在任意 t 单独求值的结果，与顺序播放结果逐帧一致；seek 耗时与 t 无关。

### S3 渲染计划与 Skia 执行器

- 实现渲染计划 IR；执行器去掉中间 `Submit(true)` 和整面回读。
- 光栅缓存 + 变换贴图；`Warp` 网格；`DrawAtlas`/`DrawVertices` 批处理；材质、图、模糊的区域裁剪。
- HTTP 与导出的回读与下一帧流水重叠；背景视频按目标分辨率解码（NV12 上传作为候选）。
- 出口：
  - 单帧同步点 ≤ 1，空部件回读为 0；
  - 像素验收按 §9 的容差口径；
  - S0 指标对比登记。

### S4 粒子、Burst、桥接

- 闭式出生表、计数式随机、`FinalCursorMemo`、寿命上限与预算；Burst 映射；桥接纯化；自动绘制改走 atlas。
- 出口：粒子场景在任意 t 单独求值与顺序播放一致；seek 补算次数不超过寿命窗口；预算超限明确失败。

### S5 资源与媒体

- 接入 Svg.Skia 4.9.1 并实现预检，支持 `vector/atlas` 两种用法。
- 图与材质的参数绑定；`MediaFeatures` 输入；媒体特征轨分析任务、持久化，及 HTTP 状态字段。
- 出口：GlassInk 类材质与目标样式的自适应描边可用；导出等待分析完成；SVG 预检负例回归通过。

### S6 样例、文档、合同

- 重写 9 个样例，并新增目标复杂样式样例。
- 重写 `backend/docs/style-reference/script-sdk*.md`；登记 ADR（纯函数模型、宿主索引、渲染计划、v1 淘汰）。
- schema 升级：工程 30、效果合同 2、模板库 8、脚本配置库 2。
- HTTP 合同 1.43.0：用合同工具从新构建的元数据生成并校验。前端同步错误码、分析状态和导入提示。
- 出口：样例全部通过编译、确定性探针和 Vulkan 渲染；合同制品同步；前端单测通过。

### S7 删除 v1 与验收

- 删除 v1 运行时、写入器、时间线会话、120 Hz 模拟、旧样例和测试；产品版本升 MINOR。
- 用匹配的 Server/Worker 发布做 fresh HTTP 验证，覆盖进程排水和凭证扫描。
  GPU 流按已有方式做有限验收；性能结果单独报告。
- 出口：仓库中没有 v1 代码路径；S0 场景全部登记 v2 数据。

## 8. 性能候选门槛（待 S0 后确认，并非测量值）

Vulkan，1080p，目标复杂样式，歌曲 3:00 处：

| 指标 | 候选门槛 |
|---|---|
| GPU 流 seek→新帧 p95 | ≤ 50 ms |
| HTTP 预览 seek→首帧 PNG p95 | ≤ 150 ms |
| 参数编辑→新帧 p95 | ≤ 200 ms |
| 源码编辑→新帧 p95 | ≤ 1.5 s（Server 编译器已预热） |
| 稳态 1080p30 Worker 渲染 p95 | ≤ 16 ms；无 > 250 ms 停顿 |
| 单帧 GPU→CPU 同步 | 桌面路径 0 次，HTTP 与导出 1 次 |

## 9. 风险与对策

| 风险 | 对策 |
|---|---|
| C# 纯度无法绝对保证 | 分析器 + 确定性探针 + 文档合同；作者代码按本机可信代码对待 |
| 光栅缓存导致像素与直绘不完全一致 | 待决策（§10）：验收口径改为容差比较，或对关键样式保留直绘 |
| 最终几何锚点让 seek 成本受寿命影响 | 寿命上限 + 按需记忆 + 预算报错 |
| SkiaSharp 3.119 未开放 SkMesh 等能力 | 网格扭曲用 `DrawVertices`；需要 RSXform 以外的逐字形倾斜时改为逐字簇矩阵绘制 |
| SVG 解析的安全与性能 | 预检 + 预算 + 加载期转换为 Picture／图集 |
| 只支持 Vulkan，测试机可能没有 GPU | 评估 Mesa lavapipe 作为无头 Vulkan 测试后端（待验证）；本机 AMD 做正式验收 |
| 删除 v1 后回归覆盖下降 | S2–S6 期间先补齐 v2 测试，S7 再删除 |
| 媒体特征轨对长视频耗时 | 后台任务 + 持久化缓存；导出前置等待并显示进度 |

## 10. 尚待确认

1. 光栅缓存的像素验收口径：采用容差（例如 1x 缩放下单通道差 ≤ 2/255），还是要求与直绘逐像素一致。
2. 编译期分析器的严格程度：例如是否允许 `Declare` 中使用 LINQ 和集合预计算。
3. 媒体特征轨的网格分辨率、采样帧率和平滑窗口上限。
4. 粒子寿命上限与退场副本时长上限的具体数值（候选分别为 5 s 和 3 s）。
5. S1 中 GPU 流采用备用 Worker，还是直接改为进程内替换。
