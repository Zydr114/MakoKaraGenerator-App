# 脚本效果系统：逐字强调、粒子与回调 SDK／打包方案

> 日期：2026-10-02；依据程序 **0.9.0**、工程／歌词／模板 schema **21／15／3**。
> 状态：**设计提案，未实施**。用户已确认本文 §1 的七项行为及 C#＋Roslyn 平台；包格式、公开 API、工程 JSON、项目拆分和 UI 仍须整体审阅。
> 本文接续 [脚本架构方案](scripting-system-architecture-plan.md)，扩展 ADR-0061 的首期写权；已确认方向见 ADR-0068，完成度见 roadmap §18.26。文中的类型、方法、扩展名及版本均为拟议合同，不是现有程序 API。
> 后续高阶需求见 [材质／整行效果／渲染图评审](advanced-material-render-graph-plan.md)及 §17：本文固定字段输出、单入口单阶段和“不开放 shader”是前轮范围。最新施工见[具体落地路线](scripting-system-implementation-roadmap.md)，须按 R0 修订合同再实施。

优先审阅 **§7 打包、§8 SDK、§10 三个代码示例、§11 数据／UI、§15 待确认项**；§3／13 是当前渲染接缝及技术限制，§14 是逐批验收路线。

## 1. 本轮需求与已确认决定

| 用户需求／问题 | 已确认行为 |
|---|---|
| 随当前元素进度逐步放大 | 使用元素自己的演唱时间；采用**视觉放大，保持原排版**，相邻文字不让位 |
| 重复文本再次出现时加深、变大 | **实际起唱时计数**，前奏提前显示和槽位提前装载不增加次数；“变大”同样是视觉缩放 |
| 粒子从逐字播放头出发，带拖尾 | 起点由宿主提供真实文字几何，不按字符串长度或播放器控件坐标猜位置 |
| 粒子跨行 | 提供**沿路径飞行／直接换发射点**两种参数；旧粒子继续按自己的寿命消散 |
| 两句之间没有演唱空档 | **直接换到下一句**，不延迟下一句，不占用它开头的时间飞行 |
| 重复计数范围 | 提供**分支内／全工程**两种参数，绑定时明确显示所选范围 |
| 基础字与注音 | 注音**跟随基础字一起放大和移动**；翻译仍是独立来源／显示对象 |
| 语言与平台 | 首版 **C#＋Roslyn**；用户已在比较建议后选择此项 |

继续沿用：受信主进程内按会话串行执行，不占 UI 线程；预览／导出独立重建状态；参数进入撤销；当前启用脚本失败阻止导出；源码及本地编译产物持久保存。不建设恶意脚本防护、脚本市场或播放操作录制。

## 2. 新能力的边界

本轮初稿由三个示例反推的范围如下；后续材质／整行效果扩展按 §17，不能将此清单视为最终 SDK 能力上限。脚本读完整的只读内容和场景投影，输出三类临时效果：

1. **外观效果**：填充／渐变、描边、阴影、不透明度。
2. **元素视觉效果**：在完成整行排版后，对基础元素及关联注音做局部缩放。首版开放等比缩放及有名称的枢轴；不由脚本改字体、字号、字距、排版宽度或容器位置。
3. **临时特效对象**：发射器、粒子、拖尾及用于绘制它们的圆点、带状路径和图片精灵。

字号参数仍是模板／源样式的排版输入。脚本中的 `VisualScale` 是独立通道；其数值不写入模板，不改 TokenStyleSupport 的静态覆盖合同。

粒子归入派生场景的**特效层**：有稳定的效果实例和临时对象身份，可由画布显示／检查；不保存成歌词行、槽位或 `programs.clips`，也不允许脚本插入／删除工程的持久容器节点。前奏节点继续由展示编排负责。

## 3. 当前代码基础与改造接缝

| 当前位置 | 已有能力 | 本轮需要增加 |
|---|---|---|
| `Rendering.Abstractions/LyricSampler.cs` | TokenSample 的 IsCurrent、Progress、时间可信度；未定时元素不推算进度 | SDK 中公开元素区间、持续时间及可靠进度，保留 Source／Unavailable |
| `Rendering.Abstractions/SceneDefinition.cs`、`LyricSceneSampler*.cs` | 源／场景／装载身份、容器矩阵、片段与准备显示 | 公开投影与类型化事件；源演唱发生和画布装载发生分开 |
| `Text/ShapedLine.cs` | 整行布局、文本元素的 X／Advance、Unicode／整形字簇范围 | 逐 Token 的可绘制单元映射、墨迹边界、播放头基线及视觉几何 |
| `Rendering.Skia/SkiaTextLayoutEngine.cs` | 已按整形结果的文本元素绘制，封装字体和 DrawShapedText | 可复用的选择性绘制入口，保持合字、回退字体、字距和字形归属 |
| `Rendering.Skia/SkiaRenderPipeline.cs` | 元素样式依靠裁切整行的样式遍完成；有效样式在绘制中解析 | 拆出外观帧计划；元素独立变换与未唱／已唱区域共用同一局部坐标；消除原字重影和装饰裁断 |
| `Rendering.Skia/SkiaRenderPipeline.Ruby.cs` | 注音按基础元素的进度绘制，已有正确对齐 | 基础字与其全部注音共用元素视觉变换 |
| `Rendering.Abstractions/CachedRenderPipeline.cs` | 快照／时间／画质等组成像素缓存键，命中直接返回 | 状态推进先于像素缓存；增加效果／参数／产物／模拟修订 |
| `Application/ProjectPackage.cs`、Core Project／DTO | 工程目录、当前版本门禁、不可变数据和历史 | 工程脚本包及实例数据、资源收拢和原子保存；暂无实现 |

上述路径相对于 `src/MakoKaraGenerator.*`。当前没有脚本 SDK、编译器、发射器／粒子运行时或脚本 UI；复用基础不等于这些能力已经存在。

## 4. 与容器树共存的场景结构

```text
工程源数据
  ├─ 源歌词：分支／行／元素／时间／注音
  ├─ 展示定义：持久容器树／槽位／片段／前奏
  └─ 脚本包与效果实例：源码／资源／参数／绑定／顺序

派生场景
  ├─ 现有容器和本次装载的歌词
  │    └─ 元素绘制单元：基础字形＋关联注音＋局部视觉缩放
  └─ 特效层：效果实例 → 发射器／粒子／拖尾绘制项
```

绑定到分支的粒子实例跨行持续存在；切换槽位或片段只改变它跟随的光标和目标矩阵。粒子既能跨双槽，也能跨双槽→居中→三槽，不需要知道“双行”这个预设名称。

特效层读最终画布几何。元素缩放位于已有父组／槽位／单句微调／原入场变换内部；父变换只应用一次。设当前行已有世界矩阵为 `W`、元素在行坐标中的固定枢轴为 `c`，则其视觉矩阵为：

```text
W_visual = W × Translate(c) × Scale(s) × Translate(-c)
```

`W` 已含的入场变换不再重复叠加。基础字、未唱／已唱裁切、描边、阴影及注音使用同一 `W_visual`，不会出现基础字变大而注音留在原位的问题。

## 5. 三个效果的行为合同

### 5.1 逐字强调

对有效元素区间 `[startUs, endUs)`，宿主提供：

```text
durationUs = endUs - startUs
elapsedUs  = timeUs - startUs
p          = 宿主 LyricSampler 的元素 Progress
scale      = startScale + (peakScale - startScale) × Ease(p)
```

只对 `IsCurrent` 的元素驱动这段生长，持续时间来自该元素，用户设置峰值和曲线。样例可另设结束策略：恢复原大小、按恢复时长回落、保持至本句离场；这些是可见参数，示例的初始值在整体审阅时确认，不在代码中藏默认值。

视觉放大允许字形覆盖相邻区域，但相邻字的原排版位置不变。枢轴建议提供“基础字墨迹中心／基线中心”，注音围绕**同一个基础字枢轴**变换。预览质量只能改变栅格质量，不能改变缩放值。

未定时元素有可见事件，没有 TokenStarted 和可靠持续时间；此样例不对它伪造进度。多分支同时演唱时存在多个当前元素，脚本按绑定范围分别处理。

### 5.2 播放头粒子与拖尾

“播放头”指当前元素中未唱／已唱的几何分界，不是界面时间轴的指针。宿主提供 `PlaybackCursor`：

- SourceTokenId、OccurrenceId、BindingId、ProgramId、CursorTrackId；
- 当前区间、进度及可靠性；
- 基础／最终视觉坐标中的分界线、选定发射锚点和运动切向；
- 当前／下一演唱段、跨行类型、空档区间及目标几何；
- Active／Gap／Bridge／Unavailable 状态。

对于现有横向布局，先用整形 Advance 区间和相同 Progress 求分界，再经元素视觉矩阵和行世界矩阵映射到画布。锚点高度由参数选择，例如墨迹中心高度或基线偏移。一个 Token 可含多个字，播放头沿该 Token 的实际布局推进，不按 UTF-16 长度移动。

**直接换行**：新行首个定时元素起点更换发射点；旧粒子的出生坐标和历史轨迹不跟着搬走，继续运动／消散；新发射点使用新行真实容器、槽位和视觉变换。

**沿路径飞行**：从上一句最后定时元素终点到下一句第一个定时元素起点的实际空档内，建立线性／贝塞尔桥接。建议参数为“最多飞行时长”与“长空档上限”；实际飞行使用入唱前最后 `min(最多飞行时长, 可用空档)` 的区间，这是显示在参数说明里的适配规则，不修改歌词时间。参数非法则报告；没有空档直接换行，这是已确认行为。

长间奏和前奏准备区间没有实际逐字起唱，不凭可见文字创建活动演唱光标。长空档是否飞行、是否在 Bridge 阶段发射、拖尾长度和寿命属于公开参数；前奏不触发重复计数。无有效下一目标时停止连续发射，已有粒子完成寿命。

桥接目标必须依据实际显示计划，不能拿槽位一／二名称猜位置。宿主可在独立的只读外观求值会话中取得下一边界的最终锚点，处理重复缩放等组合；该查询不推进当前会话的事件游标，也不求值粒子，避免外观与粒子互相递归。初期技术验证必须覆盖这一组合。

粒子位置使用**合成画布像素**，与窗口大小／HiDPI 无关。出生时捕获变换后的坐标；参数或布局修改使快照修订变化后，从歌曲时间线重新生成全部相关历史。首期由宿主提供固定时间的粒子与拖尾辅助设施，脚本也可在自己的固定步状态中实现运动，再提交批量绘制项。

### 5.3 文本重复 callback

事件为 `TokenStarted`，按整份源 Token 的文本区分，不把一个多字 Token 再拆成字符。建议首版比较策略为**原文精确匹配、区分大小写**；若需要 Unicode 归一化／忽略空白作为参数，须显式选择，不修改源文字。比较策略属于待审阅字段。

计数以 `OccurrenceId` 去重。一次源演唱发生可以对应多个画布装载，但不因多槽、前奏、重复测量或几何查询而再次计数。

```text
On TokenStarted:
    key = (countScope, token.Text)
    n   = count[key] + 1
    count[key] = n
    occurrenceAppearance[event.OccurrenceId] = ColorAndScaleFor(n)
```

**为每次发生保存自己的效果结果**：第三次出现时只赋予第三次发生更深颜色和更大视觉比例；第一、第二次仍在其他槽位保留时，保留各自结果，不能统一读一个最新计数并一起改变。

分支内模式用该分支文本计数；全工程模式可合并不同歌词分支的有效演唱事件。注音和提示文字不是新的演唱 Token，不额外计数；关联翻译仅在它自身存在有效元素起唱区间时属于源演唱事件，不能由关联主行自动伪造一次起唱。

直接跳到歌曲后段时重建之前的计数。循环和重新播放同一时间不会把软件使用次数累加为第四、第五次；次数反映歌曲时间线里的发生顺序。`IsReplaying` 仅供诊断，不允许据它改变成片视觉。

## 6. 平台选择与调研

| 方案 | 适用之处 | 本项目需要承担的工作 | 本轮结论 |
|---|---|---|---|
| C#＋Roslyn | 与 .NET 宿主和类型化 SDK 直接衔接；可输出程序集及调试符号 | 编译引用集、代次加载／释放、历史重建、诊断和持久产物均由产品实现 | **用户已选择，首版采用** |
| JavaScript＋Jint | .NET 内的 JS 解释器，可预解析／复用脚本；便于写较短的效果 | SDK 桥接、数值／类型转换和同一生命周期；不能把解释执行说成每个包会产出 C# DLL | 可作后续适配器，不并行实施 |
| Lua＋嵌入运行时 | 有官方 C API，可将函数输出为可再次装载的 binary chunk | .NET 桥接及本机运行库；源码仍须携带，产物是运行时相关 Lua chunk，不是本项目脚本 DLL | 可作后续适配器，不并行实施 |

Roslyn 的 `Compilation.Emit` 支持将编译代码的 IL 输出到流，PE／PDB 通道适合本地 DLL 方案。[官方 API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.compilation.emit?view=roslyn-dotnet-4.13.0)。Jint 官方建议缓存 prepared script、单引擎一次处理一个操作，并用实际宿主调用测量性能；本文不据第三方基准承诺三种运行时谁更快。[Jint 官方说明](https://github.com/sebastienros/jint#recommended-usage)。Lua 官方提供 binary chunk 的输出与装载接口。[Lua 官方手册](https://www.lua.org/manual/5.5/manual.html#lua_dump)。页面版本不作为本项目依赖锁定值。

本方案使用普通 C# 类编译为程序集，不以多次 `CSharpScript.RunAsync` 代替长期效果会话。SDK、包格式和帧协议与编译器适配器分离，未来更换语言不要求重建容器树；首版不为尚未选择的语言实现多个 SDK。

## 7. 脚本如何打包

### 7.1 可编辑包、分享包和本机产物

建议采用三种形态，共用同一份内容合同：

```text
GlowWords/                    可编辑的包目录
  manifest.json               包／SDK／效果入口／参数元数据
  src/
    TokenEmphasis.cs
    CursorParticles.cs
    RepeatAccent.cs
    SharedCurves.cs
  assets/
    spark.png                 可选资源；圆点／路径可不用图片
  README.md                   作者、用途、参数说明
  preview.png                 可选预览图，不参与运行

GlowWords.mkfx                上述目录内容的 ZIP 分享包，扩展名为提案

~/.local/share/MakoKaraGenerator/scripts/<artifactHash>/
  effect.dll                  本机编译产物
  effect.pdb
  build.json                  编译环境及引用指纹
```

**源码包是可编辑真值，DLL 是持久化的可重建产物。** 分享／移动工程携带源码、清单和资源；在目标机器按 SDK 和运行时重新编译或复用匹配产物。首版不接收只有 DLL、缺少可编辑来源的包，不增加第三方 NuGet／原生依赖管理。

一个包允许导出多个效果；上述三个效果既可单独绑定，也可组合。每个效果实例都有自己的 InstanceId、参数、绑定、顺序和状态；同一编译产物可被多个实例复用。编辑一个包的新版本不会静默替换工程副本。

### 7.2 清单示意

下面只示意一个效果；示例版本和数值不是已接受的程序／SDK 版本或产品默认值。

```json
{
  "formatVersion": 1,
  "packageId": "org.makokara.examples.glow-words",
  "packageVersion": "1.0.0",
  "displayName": "逐字强调与光尾",
  "sdkApiVersion": "1.0.0",
  "runtime": { "language": "csharp", "targetFramework": "net10.0" },
  "sources": ["src/TokenEmphasis.cs", "src/SharedCurves.cs"],
  "assets": [{ "id": "spark", "path": "assets/spark.png", "kind": "image" }],
  "effects": [{
    "id": "token-emphasis",
    "entryType": "GlowWords.TokenEmphasis",
    "phase": "appearance",
    "capabilities": ["appearance", "token-visual-scale"],
    "supportedBindings": ["sourceBranch", "sourceLine", "sourceToken"],
    "parameters": [{
      "key": "peakScale", "type": "number", "label": "峰值放大",
      "default": 1.4, "min": 1.0, "max": 3.0, "step": 0.05
    }]
  }]
}
```

参数声明以 manifest 为单一事实源；SDK 按键提供类型化读取，UI 从相同声明派生，避免类属性、清单和表单各存一份范围。入口必须实现指定 SDK 生命周期；类名、效果 ID、参数和资源不一致时报告文件／字段诊断。

首版参数类型建议为 number／integer／boolean／enum／color／duration／curve／assetId／text；duration 内部是整数微秒，曲线是显式控制点／缓动描述。每个字段包含中文标签、单位和适用条件；峰值、恢复策略、计数范围等初始值在 E0 审阅，不沿用示例数字直接成为默认配置。

`capabilities` 表示宿主支持的效果输出类别，用于兼容校验和 UI 说明，不是新增权限审批。SDK 精确版本不匹配则报告并拒绝运行；首版不承诺旧包迁移。包版本、SDK 版本、程序版本、工程 schema 和模拟合同各有职责，不混为一个版本号。

### 7.3 导入与工程固定

建议工程内保存：

```text
Song.mkgproj/
  project.json                scriptEffects 的包引用和实例
  lyrics.json
  scripts/
    <projectPackageId>/        完整源码包副本
      manifest.json
      src/...
      assets/...
```

用户打开包目录或导入 `.mkfx` → 检查清单／源码／资源 → 复制为工程受管包 → 编译并展示诊断 → 创建效果实例并选择绑定 → 明确启用。修改工程内源码并保存、另存为、自动备份都需要覆盖脚本文件；不依赖导入时的外部路径。

工程编辑以“源码草稿／已接受修订／实际运行代次”分开表示。接受源码、参数或绑定修改进入工程历史；草稿逐键输入不写工程。源码接受但编译失败时，工程保留该修订并显示未生效，预览可继续上一成功代次，导出被阻止；可撤销或修复。

包文件的接受与历史需保存不可变的内容修订，而不是对同一磁盘目录原地覆盖：工程数据引用包修订，工作区可按修订还原文件，保存时原子发布完整集合。旧成功产物也不能覆盖掉。具体包修订存储方式和清理语义属于实施前需确认的数据合同。

### 7.4 指纹与持久 DLL

- `PackageContentHash`：全部受管包文件的规范相对路径和内容，含清单／源码／资源／README／预览图；固定工程副本和可恢复的编辑修订。
- `ArtifactHash`：源码、入口／API 元数据、SDK 及引用程序集指纹、编译器／选项、目标框架与运行时合同；参数值不进编译键。
- `ResourceRevision`：图片等运行资源的内容；仅换贴图不必无条件重新编译 C#。
- `ExecutionRevision`：工程／展示／源时间、效果实例顺序、绑定、参数、种子、包及产物、资源、音频分析和模拟合同修订。

在同一产物目录树里写完临时代次，验证 DLL／PDB／build.json 后原子发布。缺失或指纹失配时从源码重编译；重开仍保留并复用有效 DLL。当前运行／导出使用的产物不得在清理时移除。

编译服务由应用携带 Roslyn 和固定的 SDK／目标框架参考程序集，不依赖用户运行 `dotnet build` 或临时联网下载。开发机装有 SDK 不能作为发布软件的隐含前提；引用版本在 E0／技术验证时锁定并计入指纹，发布时检查分发清单。

热替换流程：编译新代次 → 建立实例 → 从原点重放 → 验证外观及特效 → 会话边界替换 → 释放旧订阅、资源和加载上下文。SDK 公共程序集由宿主统一提供，避免加载副本导致接口类型不相等。可回收 AssemblyLoadContext 的卸载需要引用及执行线程释放，不是强制终止。[官方卸载说明](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability)

## 8. 暴露给脚本的 API

### 8.1 公共 SDK 的投影边界

建议公共程序集为 `MakoKaraGenerator.Scripting.Abstractions`，仅公开自身的不可变视图、ID、数值、颜色／效果描述及生命周期接口。宿主转换 Core／渲染内部数据；SDK 不引用 Avalonia、Skia、ViewModel 或可写 Project，不暴露 native 字体／字形对象。

“API 只读”是产品数据合同，不是在受信 C# 进程内宣称建立权限沙箱。用户可以写普通 C# 算法和会话状态；成片一致性要求使用宿主的歌曲时间、种子和资源输入，不以系统钟、设备随机源或外部实时状态驱动画面。

| API 模块 | 暴露内容／拟议查询 | 用途与限制 |
|---|---|---|
| `Project.Content` | Branch／Line／Token／Ruby 只读目录；GetToken(id)、FindTokens(text)、SourceTimeRange、TimingStatus | 读完整源内容与关联，不只当前可见行；无源数据 setter |
| `Project.Scene` | 持久节点／父组／参考矩形、programs／clips／leadIns、关联源身份 | 理解容器结构，纯组没有假 LineId；展示定义与本帧装载分开 |
| `Frame.Presentations` | 当前行／元素／标记实例、BindingId、ProgramId、ClipId、CueId、DisplayPhase、进入状态 | 当前可见实例和来源；文字标记没有假 TokenId |
| `Frame.Performance` | ActiveTokens；各元素 start／end／duration／elapsed／Progress／IsCurrent／Source | 实际逐字时间；Missing／Unavailable 显式返回，不猜演唱 |
| `Frame.Geometry` | 基础 Advance／Ink 边界、基线、关联注音、BaseWorldMatrix、绘制单元映射及锚点 | 基础排版与已有入场变换；单位与坐标空间标注清楚 |
| `VisualFrame.Geometry` | 合并全部元素视觉效果后的矩阵、边界和播放头几何 | 提供给粒子／绘制阶段；此阶段不能再修改文字外观或变换 |
| `VisualFrame.Playback` | ActiveCursors、CursorTrack、NextTransition／NextBoundary | 多个分支／重叠演唱返回多个光标，不选一个全局“当前字” |
| `Simulation.Particles` | 宿主管理出生记录／最终出生锚点／速度／年龄／轨迹样本的只读视图 | 便于自定义纯绘制和运动；脚本自管状态模式与宿主管理模式明确分开 |
| `Session.History` | 截至当前歌曲时间的源演唱和装载事件；GetStarts(range, filter)、OccurrenceId／Ordinal | 读可重建历史；完整源目录可读，未来事件不是已发生计数 |
| `Project.Audio` | GetAt(timeUs)、GetRange(startUs,endUs) 的低频量／脉冲／基本节奏与 Availability | 复用前版的离线音频合同；没有数据就不可用，不伪造 |
| `Parameters` | GetNumber／GetBool／GetEnum／GetColor 等类型化读取 | 清单声明为唯一元数据；参数在会话期间固定，修改后重建 |
| `Resources` | 按包内 assetId 解析 ImageHandle 等逻辑资源 | 宿主加载／释放，脚本不持有 SKImage；可选贴图缺失报告 |
| `Random` | 按种子、InstanceId、EmitterId、BirthIndex 拆分的确定性随机序列 | 帧刷新、并行会话和遍历无关的 ID 不改变粒子抽样 |
| `GeometryQueries` | 指定时间的只读基础场景／锚点查询 | 不推进当前会话；下一边界的最终外观查询由宿主专用路径完成，不开放递归全帧求值 |
| `Diagnostics` | Log、Report，包含效果／包／源文件／目标 ID／时间 | 日志不成为视觉输入；运行错误走统一诊断和导出门禁 |

ID 至少区分：`NodeId`（持久树）、`SourceTokenId`（源元素）、`BindingId`（一次画布装载）、`OccurrenceId`（一次源演唱发生）、`CueId`（前奏）、`EffectInstanceId`、`CursorTrackId`、`EmitterId` 和 `ParticleId`。新增 OccurrenceId 是事件层身份，不要求把某次运行对象写入 lyrics.json。

同一 Token 在前奏与演唱期保持装载连续，OccurrenceId 在实际起唱时产生；前奏不另建一次发生。计数按源 OccurrenceId，文字输出按 `(OccurrenceId, BindingId)` 或明确的源目标解析至本帧所有合法实例。

跨行使用 `PreviousEndAnchor` 与 `NextStartAnchor`：前者取离场前的视觉终点极限，不能在 EndUs 直接采样而误取半开区间外的下一句；后者按下一次源起唱的实际绑定与最终外观解析。两者在同一合成画布坐标系中。

### 8.2 生命周期接口草案

以下是职责签名示意，完整参数字段和 XML 合同在 SDK 实施前定稿：

```csharp
public interface IKaraokeEffect : IDisposable
{
    void Initialize(InitializationContext context);
    void OnTimelineEvent(TimelineEvent evt, EventContext context);
    void Advance(SimulationStep step, StateContext context);
    void EvaluateAppearance(FrameContext frame, AppearanceWriter output);
    void EvaluateVisuals(VisualFrameContext frame, VisualWriter output);
}
```

- `Initialize`：创建实例状态、读取固定参数、登记所需源／装载事件范围和资源。
- `OnTimelineEvent`：按歌曲时间更新计数、发生记录，或登记以事件 ID 去重的粒子 Burst 意图。
- `Advance`：固定模拟步／事件边界推进自有状态，不依赖帧刷新；Appearance 阶段实例读基础几何，Visual 阶段实例在文字外观完成后读最终几何，能据此在固定步记录粒子出生坐标。
- `EvaluateAppearance`：读状态，返回当帧外观和局部缩放；重复调用不累计状态。
- `EvaluateVisuals`：读最终文字几何，返回连续发射器描述和绘制项；重复调用不创建新的出生次数。
- `Dispose`：释放宿主管理订阅／逻辑资源引用；代次卸载时实例、委托和缓存一同退出。

SDK 可提供无操作的 EffectBase，作者只覆写本效果需要的方法；不能为了简短示例省略生命周期语义。阶段不匹配的 writer／资源调用按能力校验拒绝。

“全局上下文”由 ProjectContext 的完整只读目录／历史和效果实例跨行持有的会话状态组成，不使用应用静态字典。一个分支级实例在整首歌持续存在；多个实例不共享可写计数。需要全工程计数时，该实例订阅全工程源起唱事件，但仍只给其绑定目标写效果。

**初稿建议每个导出效果声明 `phase: appearance` 或 `phase: visual`。** 前者可输出外观及元素缩放，后者可输出粒子／拖尾／图元并读全部文字效果后的几何；组合效果通过一个包的多个导出入口完成。此初稿阶段划分未获实施批准；§17 的高阶提案改为同一实例声明多个有依赖的输出，保留阶段约束和禁止同帧反向反馈，避免把复合材质与粒子人为拆成不同状态实例。

### 8.3 事件集合和排序

拟议事件按源演唱、装载显示等类别区分：

| 组 | 事件 | 身份与含义 |
|---|---|---|
| 源演唱 | TokenStarted／TokenCompleted | 一次源 Token 的有效区间起止；不按每个装载副本复制 |
| 源行 | LinePerformanceStarted／Completed | 从首末有效元素区间派生的演唱摘要；无元素时间则标为不可用，不把行可见起点或前奏起点当起唱 |
| 装载显示 | ElementBecameVisible／Hidden、BindingEntered／Left | 每个画布装载；未定时字、注音、标记也有适用的可见事件 |
| 前奏 | LeadInStarted／StepChanged／Completed | CueId 和目标绑定；不自动增加 TokenStarted |
| 光标连续性 | CursorTransition | 换元素／跨行／换片段／空档／不可用，附轨道与目标身份 |
| 音频 | AudioFeatureTick | 固定分析时间上的数据，不是音频设备回调 |

事件从源区间与共享显示计划生成，不依赖两张预览画面做差。事件包含 TimeUs、EventId、SourceRef、OccurrenceId／BindingId（适用时）及 ReplayReason。

同一微秒：结束／离场 → 装载进入／前奏可见 → 源起唱 → 光标衔接 → 对该时刻的状态／外观／特效求值；同组用稳定源顺序和 ID 排序。顺序是 SDK 合同，必须在一次普通换字、前奏归零、无空档换行及多分支同起点上测试。

全工程计数仅计算有效源元素区间。与主行关联显示的翻译，不因为主行起唱而得到自己的伪 TokenStarted；只有翻译自身有效逐字区间才构成其源事件。显式筛选器可限定主歌词／指定分支，避免脚本作者不知哪些文本正在计数。

### 8.4 外观与视觉输出

拟议 writer 提供名称明确的操作：

```text
Appearance.SetUnsungFill(target, fill)
Appearance.SetSungFill(target, fill)
Appearance.SetStroke(target, strokeOrDisabled)
Appearance.SetShadow(target, shadowOrDisabled)
Appearance.MultiplyOpacity(target, factor)
Visual.ScaleTokenWithRuby(tokenTarget, factor, pivot)
```

基础字与注音统一缩放不可由遗漏一个 writer 调用而破坏。外观是否同时影响注音由目标类型明确表达；沿用当前基础元素覆盖影响注音的语义时用 `TokenWithRuby`，只写基础字时用 `BaseToken`。

建议合并合同：先解析工程→分支→行→元素的静态样式，按用户效果列表顺序应用脚本；同属性 Set 由后项覆盖，乘法不透明度／视觉比例按序相乘。脚本读取同一基线和自己的历史状态；Visual 阶段读最终合并结果。旧入场 alpha 与样式 alpha各应用一次，不把二者先混成一个值后又乘一次。

输出只对目标帧有效。值非有限、比例非正、不透明度越界、目标不属于绑定范围或能力未声明时报告错误，不夹值，不把非法结果写回项目。明确的数学辅助函数可以由作者主动调用 Clamp。

### 8.5 粒子、拖尾与绘制输出

建议同时提供易用的宿主辅助设施和可定制的批量绘制接口：

```text
Visual.Emitters.Set(id, descriptor)
    CursorTrack／发射锚点／Rate／Lifetime／Velocity／Spread／SeedStream
    TransitionMode／BridgeCurve／MaxBridgeDurationUs／GapLimit
    PrimitiveOrAsset／ColorCurve／SizeCurve／OpacityCurve／Trail

Events.Particles.Burst(eventId, emitterId, count, descriptor)

Visual.Overlay.DrawParticleBatch(id, items, layer)
Visual.Overlay.DrawTrailBatch(id, points, widthCurve, colorCurve, layer)
```

`Set` 是描述当前配置，**不是每次渲染都新建一个发射器**。宿主按固定歌曲时间计算 BirthIndex、发射配额和寿命；Burst 仅在事件阶段登记，按 EventId 去重。自定义脚本也可在 Advance 中模拟自己的粒子列表，再在 EvaluateVisuals 中读状态绘制；两个模式不对同一对象同时更新。

每个粒子 ID 为 `(InstanceId, EmitterId, BirthIndex)`，拖尾记录固定歌曲时间的样本，不是最近 N 次 UI 重绘。辅助运动可采用按出生时间解析的位置函数；需要积分的脚本使用相同固定步，渲染时只读取／插值固定状态。

初稿绘制项为圆点、图片精灵、带状拖尾；资源用逻辑句柄，数据批量提交。层级提供 `BehindText`／`AboveText`，同层按效果顺序和稳定对象 ID。原“不开放 shader”范围由 §17 的材质／渲染图提案扩展；继续不暴露任意 Skia 调用或可写工程背景。新增的是显式图像输入和经校验的处理图，而非让脚本控制媒体源时间。具体图元、合成位置、能力和预算仍待审阅。

已有粒子在行离场后可活到寿命结束，因此不能把整个特效层可见性直接绑定到旧行。绑定到分支／共同父组的实例最适合跨行；仅绑单句时必须说明接续范围，不能暗中遍历另一分支来找下一句。

## 9. 共享执行管线与确定性

```text
源码包 → 版本化 SDK 校验 → Roslyn → 持久 DLL／PDB → 运行代次
                                                          ↓
不可变工程快照 + 源事件／展示计划 + 音频特征 + 参数／种子
  → 基础场景／整行布局／元素绘制单元
  → 会话事件 → Appearance 实例固定步推进／EvaluateAppearance
  → 合并外观与局部视觉变换
  → 最终元素／注音／逐字光标几何
  → Visual 实例固定步推进／EvaluateVisuals → 发射描述／临时对象
  → 固定时间粒子运行时／拖尾状态
  → 已校验 FramePlan
  → 像素缓存 → Skia → 预览或视频导出
```

宿主为每个模拟边界完成上述外观和发射解析；在任意渲染时刻可重新纯求值外观／图元并读取粒子状态。绘制次数不增加出生数。固定步建议技术验证从 120 Hz 开始，最终频率、积分器、出生采样和插值规则作为 SimulationContract 待审阅；不把这项建议当成已经锁定的默认值。

微秒时基不能重复累加截断后的 `1_000_000 / Hz`：第 n 个边界使用 `floor(n × 1_000_000 / Hz)`，并在精确歌词事件处拆分区间。没有步点的短元素区间也要处理其起止，出生数量依据真实活动区间。

先派发区间内历史，再取当前画面；停止发射与粒子死亡是不同事件。像素缓存位于会话推进之后，缓存命中只省绘制／纯求值，不能跳过 callback、发射和消亡。几何查询、模板缩略图和导出各自的会话不会重复污染主预览状态。

跳转／循环／参数撤销／源时间变化／包修订／热替换：建立新会话，从原点重放到目标时间，预览显示重建状态并可协作取消；不沿用污染过的缓存。首版不自动序列化任意脚本对象，检查点留作以后优化。

导出固定工程、包及编译代次、参数／种子、资源／音频分析和模拟合同；独立重放到导出起点，中段导出也包含之前的计数和存活粒子。当前启用源码的编译／初始化／回调／外观／粒子求值错误阻止或终止导出；预览保留上一成功代次时明确“当前源码未生效”。

宿主统一校验粒子／拖尾资源预算；超限按错误报告，不能只在预览默默减粒子或让导出随机丢对象。具体上限在实现前确认；低画质缩小栅格目标不改变数量、出生时刻或运动轨迹。

工程合成时长仍由用户设置／现有时长规则决定，脚本不静默延长音频或工程；超出输出终点的尾迹会被截断，应在效果范围摘要中说明，用户可主动调整合成时长。

## 10. 三个脚本怎样使用 SDK

以下为 **C# 风格的合同示意**，成员尚未实现，不能当作现有 API 可直接运行。初始化／释放等重复部分省略。

### 10.1 按当前元素持续时间放大

```csharp
public void EvaluateAppearance(FrameContext frame, AppearanceWriter output)
{
    foreach (var token in frame.Performance.ActiveTokensInBinding)
    {
        if (token.ProgressSource != ProgressSource.FromToken) continue;
        double p = token.Progress; // 来自统一采样，duration／elapsed 同时可读
        double s = parameters.StartScale
            + (parameters.PeakScale - parameters.StartScale)
            * parameters.GrowthCurve.Evaluate(p);
        output.Visual.ScaleTokenWithRuby(token.Target, s, parameters.Pivot);
    }
}
```

末尾恢复策略由同一效果根据 TokenCompleted 和歌曲时间实现；不读取系统时间。未定时字不放大，参数 UI 必须显示这一适用范围。

### 10.2 从最终播放头发射粒子

```csharp
public void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
{
    foreach (var track in frame.Playback.TracksInBinding)
    {
        output.Emitters.Set(track.Id, new EmitterDescriptor
        {
            Anchor = CursorAnchor.FinalHighlightBoundary(track.Id),
            Rate = parameters.Rate,
            LifetimeUs = parameters.LifetimeUs,
            Transition = parameters.Transition,
            NoGapPolicy = NoGapPolicy.JumpAtNextStart,
            Trail = parameters.Trail,
            Asset = resources.TryImage("spark"),
            SeedStream = random.ForEmitter(track.Id)
        });
    }
}
```

Rate 和 Lifetime 决定出生／存活，由宿主模拟；这段函数只是配置，不在每帧 Emit 一批。轨道无活动元素时是否处于合法 Bridge，由共享计划决定。停发后旧粒子仍存在。

### 10.3 文本再次起唱时加深和放大

```csharp
public void OnTimelineEvent(TimelineEvent evt, EventContext context)
{
    if (evt is not TokenStarted e || !seen.Add(e.OccurrenceId)) return;
    var key = TextKey.For(e.Text, e.BranchId, parameters.CountScope);
    int n = counts.GetValueOrDefault(key) + 1;
    counts[key] = n;
    occurrences[e.OccurrenceId] = new RepeatAccent(n);
}

public void EvaluateAppearance(FrameContext frame, AppearanceWriter output)
{
    foreach (var token in frame.Presentations.TokensInBinding)
    {
        if (token.OccurrenceId is not { } id
            || !occurrences.TryGetValue(id, out var accent)) continue;
        output.Appearance.SetUnsungFill(token.WithRuby,
            Darken(token.BaseAppearance.UnsungFill, accent.Ordinal, parameters));
        output.Appearance.SetSungFill(token.WithRuby,
            Darken(token.BaseAppearance.SungFill, accent.Ordinal, parameters));
        output.Visual.ScaleTokenWithRuby(token.Target,
            RepeatScale(accent.Ordinal, parameters), parameters.Pivot);
    }
}
```

开始唱的事件先更新本次发生，再求其当帧效果；第一帧不会晚一帧变色。Scale 上限、颜色最低亮度、首次是否变化由公开参数决定，不逐次把源字号乘大；相同元素在暂停重绘时不再加一次。

## 11. 工程绑定、参数和 UI 草案

### 11.1 拟议工程数据

建议 `project.json` 增加独立 `scriptEffects`，与 `lyricPresentation`、`LyricStyle` 分开。示意：

```json
{
  "scriptEffects": {
    "packages": [{
      "id": "package-reference-id",
      "path": "scripts/package-reference-id",
      "contentRevision": "sha256:..."
    }],
    "instances": [{
      "id": "effect-instance-id",
      "packageReferenceId": "package-reference-id",
      "effectId": "cursor-particles",
      "enabled": true,
      "binding": { "kind": "sourceBranch", "id": "main-branch-id" },
      "parameters": { "transition": "bridge", "rate": 30 },
      "seed": 24601
    }],
    "simulation": { "contractVersion": "proposal", "stepHz": 120 }
  }
}
```

示例数值只用于说明类型；真实实例仅含自己导出效果的参数。数组顺序就是用户阶段内的效果顺序，不再另存一份可能不一致的 Order；UI 明确显示“文字外观”和“粒子／绘制”阶段，跨阶段拖动不能改变处理依赖。

建议绑定类型：

| 类型 | 用户含义 | 适用样例 |
|---|---|---|
| sourceBranch | 某歌词分支的连续演唱内容，跨槽位／片段持续 | 播放头粒子、分支重复计数 |
| presentationNode | 某容器／父组中实际装载的后代 | 指定显示区域的特效；装载过滤与源订阅分开 |
| displayClip | 某展示片段成员 | 副歌片段的强调 |
| sourceLine | 某一句，跟随它在不同容器中的显示 | 特殊句强调 |
| sourceToken | 一个源元素的本次演唱 | 单字强调；不等同按相同文本找全部 |

一个实例可以在源事件范围内持续保留状态，输出目标仍受绑定过滤。例如绑定副歌片段、CountScope 为全工程，则计数包括之前的源起唱，只有副歌目标收到输出；不能在进入副歌时把历史丢掉。

实例复制产生新 InstanceId，参数／绑定复制，运行状态重建；删除实例不删源码包。删除仍被引用的包须明确处理引用；删除已被效果绑定的源行／片段／容器先展示影响并确认解绑或取消，不静默换到别的对象。源／绑定和包的删除语义随具体格式一起审阅。

实际实施后提高工程 schema 和程序版本，不迁移旧工程。歌词／模板是否需要改结构由实施合同决定：此方案将效果放在工程数据，**不以静态样式扩展保存粒子或 VisualScale**。

### 11.2 操作界面

建议主窗口提供明确的**脚本效果…**入口，打开脚本工作区，保留现有画布作为实际工程预览。界面按以下对象分开：

```text
工程脚本包／导出效果       所选效果实例／当前绑定         实际工程画布
  导入包／新建包            启用状态与生效反馈             当前时刻效果
  编辑源码／导出包          参数与效果顺序                 同一预览管线
  包内多个效果              绑定目标摘要                   字形／粒子检查

源码页：工程内草稿 → 保存并编译
诊断页：文件／行／列／目标／歌曲时间 → 点击定位
```

- **效果实例**负责绑定、参数、启用和顺序；**源码包**负责编辑／分享。不会因选择一个模板就把代码应用到当前行。
- 包导入后列出其多个导出效果；创建实例时明确选择对象。跨行粒子突出显示“绑定到主歌词分支”，不让用户误以为绑到一个槽位就能跨任意容器。
- 参数从清单派生；曲线、色板、范围、计数范围、跨行方式、峰值比例、寿命、拖尾长度直接显示。常用参数不藏在“更多操作”里。
- 单一**保存并编译**接受源码修订并编译；逐键编辑只改草稿。失败时准确显示“源码已保存／编译失败／预览仍为上一成功版／导出不可用”，不把保存成功说成效果已生效。
- 参数回车／失焦提交，滑块手势一次历史；改参数不重编译无关代码，而是重建实例和帧结果。
- 源码未保存时离开有保存／放弃／取消；已接受源码、参数、绑定、启用和顺序统一可撤销。
- 窄窗口显式切换源码／参数／诊断面板，避免在现有编排属性底部堆代码控件。UI 稿需在进入实现前展示，本文只是组织草案。

## 12. 项目分层建议

| 模块 | 建议职责 |
|---|---|
| Core | 效果实例／绑定／包修订的源合同、参数值、严格校验及纯编辑，不运行 C# |
| Application | 包导入／保存／历史、源码草稿接受、工作区命令及会话请求编排 |
| Scripting.Abstractions（拟新增） | 版本化只读 SDK、生命周期、效果输出和诊断；独立于 Core 领域类型与图形后端 |
| Scripting（拟新增） | Roslyn、引用集、产物库、加载代次、串行脚本会话及事件重放 |
| Text／Rendering.Abstractions | 绘制单元映射、基础／视觉几何、光标与帧计划，粒子抽象／固定模拟接缝 |
| Rendering.Skia | 元素选择绘制、局部视觉变换、图元／粒子批量绘制与最终合成 |
| Media | 离线低频／节奏特征；SDK 通过中立只读接口访问 |
| Export | 使用中立帧提供接口、独立脚本会话、固定代次及失败门禁；不反向引用 Application |
| App | 脚本工作区、控件、诊断定位和组合根；UI 线程不编译／重放 |

粒子运行时具体放在 Rendering 侧的哪个中立模块，须以依赖图验证：编译器不需要图形后端，渲染器也不直接启动 Roslyn；SDK／帧提供接口不能引入依赖环。项目拆分、依赖新增和公共 API 是 AGENTS.md §8.3 的实施前审阅事项，本轮没有创建项目。

## 13. 必须验证的渲染细节

1. **真实绘制单元**：可缩放元素不能只放大当前横向裁切矩形，否则会夹断字形／描边或保留原字重影。基础位置沿用整行布局，选择实际整形单元绘制，装饰和双色裁切同步变换。
2. **字簇边界**：源 Token 不一定等于一个整形单元；合字、组合附加符、Emoji ZWJ 不能按 UTF-16 切开。SDK 应提供可独立变换能力和不可分割字簇映射；建议跨 Token 共享同一字簇的独立缩放请求报告不支持，提示合并元素／作用于完整字簇，不能默默带着另一字一起变化。这项策略在合同审阅时确认。
3. **几何真实性**：墨迹与 Advance 是不同边界；播放头用与高亮相同的 Advance／进度规则，视觉边界用真实字形／注音包围框；不可混用。
4. **放大后渐变和命中**：行／画布坐标渐变沿用其坐标合同；效果后的联合可见边界用于特效检查。容器布局编辑仍用静态参考，不因脚本粒子把拖动框撑大。
5. **光标集合与衔接**：并唱返回多个轨道；若无唯一合法下一行，报告衔接不确定并停止桥接，不能随机选一个。下一目标须考虑独占片段、槽位换装及前奏隐藏规则。
6. **组合依赖**：先求所有文字外观和缩放，再供粒子取最终光标；粒子不得反过来改同帧文字矩阵。跨行目标的独立求值只重建外观历史，不递归建立下一套粒子。

## 14. 推荐实施批次与验收

此处记录三个示例阶段的 E0～E5 顺序；新增高阶需求后应先按 §17 所引方案 H0～H2 验证材质／图接口，再复用本节三个效果的验收，不能先冻结固定 writer 后重新推翻。不在设计完成时标记 SCRIPT 任务完成。

| 批次 | 内容 | 验收判据 |
|---|---|---|
| E0：合同审阅 | 本方案、SDK 字段／类型补齐、包／工程 JSON、依赖图、宽窄 UI 稿、组合事件走查 | 具体格式／拆分／删除语义获确认；七项行为和平台决定不重问 |
| E1：基础几何与强调 | 元素／注音绘制单元、VisualScale、真实播放头和视觉帧计划；先用受控效果验证 | 按元素时长放大，邻字不让位，注音跟随，颜色／描边不重影，枢轴与最终光标一致，预览／MP4 同帧 |
| E2：包与执行会话 | C# SDK、Roslyn、源码包、持久 DLL、诊断、事件与独立重放 | 编译／热替换／重开复用；暂停不重复 callback，直跳计数正确；源码错误阻止导出；工程源数据只经命令编辑 |
| E3：文本重复效果 | 分支／全工程范围、起唱事件、每次发生的外观和比例、参数撤销 | 连续相同文本按次变化；旧槽位保持旧结果；前奏与未定时字不计，循环／跳转可重建 |
| E4：粒子与拖尾 | 发射／寿命／随机／轨迹、临时层、两个跨行模式、最终锚点及未来边界外观查询 | 稳定跟随放大后的光标；有空档桥接、无空档直接换；旧尾迹不瞬移；双槽→居中→三槽连续；粒子历史重建正确 |
| E5：工程编辑与完整验收 | UI、绑定、源码／参数／顺序历史、保存／备份／另存为、基本音频输入接缝 | 三个示例可由界面制作／分享；当前格式往返；宽窄操作明确；预览与导出组合测试通过 |

E1 是宿主几何接缝，不提供第二套私有脚本语言；后续示例必须使用同一公开 SDK。基本音频仍按前版边界加入，三个本轮核心示例不以存在音频特征为前提。

组合验收至少覆盖：

- 中文、日文、Latin 多字 Token、组合音标、Emoji、字体回退／不支持字簇诊断；
- 行内空档、未定时元素、前奏提前可见、半开边界及非常短的有效元素；
- 父组旋转／缩放、单句微调、注音、独占居中、两槽／三槽；
- 多次相同文本、两个实例不同范围、两个脚本的比例／颜色合并；
- 24／30／60 fps 同时刻的状态／粒子 ID／轨迹一致，栅格比较按可解释容差；
- 暂停重绘／像素缓存、中段跳转／导出、循环、参数和源码撤销、热替换；
- 放大＋重复加深＋播放头粒子同时启用，跨行目标的外观求值不重复污染计数；
- 包缺文件／SDK 不符、资源缺失、编译失败、非法输出、粒子超限及旧代次释放。

性能基准继续在脚本系统功能完成后；功能阶段仍须证明重放可协作取消、临时对象有数量约束并能释放，避免把不可用的会话交付给人工验收。

## 15. 已确认与待整体审阅

**已确认**：C#＋Roslyn；不重排的视觉放大；注音跟随；起唱计数；分支／全工程两种范围；两种粒子跨行方式；无空档直接换行。既有运行信任、串行会话、独立导出、参数撤销和持久 DLL 决定保持。

**待整体审阅，未按默认值实施**：

- `.mkfx` 分享包／清单字段／包修订持久存储、工程 `scriptEffects` 格式、引用与删除规则；
- SDK 精确版本／类型和字段、事件排序、绑定粒度、效果叠加及绘制单元不支持策略；
- 发射／拖尾图元、层级、固定步配置、预算阈值和三个示例的可见初始参数；
- 新项目／依赖图、脚本工作区入口和宽窄 UI 稿。

本轮只交付设计并同步文档，程序仍为 0.9.0，schema 21／15／3；没有引入 Roslyn 或更改数据结构。下一轮先完成 E0 的可审阅合同／UI，再经授权进入 E1～E5，遇到新的产品或数据决策继续询问。

## 16. 本轮设计交付验证

全量 `dotnet build MakoKaraGenerator.slnx -m:1` 为 **0 警告、0 错误**；`dotnet test MakoKaraGenerator.slnx --no-build -m:1` 的 **1512 项现有测试通过**。Markdown 本地链接、代码围栏、两个 JSON 示意的语法及新增 ADR-0068 唯一性检查通过。

这些证据确认现有程序基线和文档一致性，不是新效果的功能验收。C# SDK 示例尚未实现或编译验证，JSON 也未成为实际工程格式。本次没有启动新验收窗口；下一批确认合同并实施后再提供实际效果窗口／成片。

## 17. 高阶材质与整行效果修订（2026-10-02）

用户要求重新评估自定义文字／描边 shader、液态玻璃／毛玻璃及整行 glitch。已确认玻璃输入支持选择仅背景媒体／目标后方的全部已合成内容；透明模式跳过背景依赖效果、提示和包内纯色／渐变基础外观已获确认，见 ADR-0070／具体路线 §3。

[新增评审](advanced-material-render-graph-plan.md)提出区域材质、孤立目标／蒙版／背景输入、通用多遍渲染图和同一 C# 实例的多阶段输出，区分图像变形与可映射的播放头几何。`.mkfx` 包建议增加 SkSL、图配方和材质导出项；C# DLL 持久化保持，shader 源码及后端缓存另外管理。C#＋Roslyn 和 ADR-0068 的七项效果行为不变；原 §8 的阶段二选一、§8.5 不开放 shader、§11 的两阶段 UI／JSON 示例均是历史提案，待统一修订合同。

H0～H5 保留高阶评审层次，具体施工统一按[落地路线](scripting-system-implementation-roadmap.md) R0～R8，保留 E1／E3／E4 三个效果作为完整验证目标。SkSL／Skia 首后端、模型／图／能力／模板依赖和 UI 是待审阅建议；当前仍无代码或依赖变化，程序 0.9.0、schema 21／15／3。运行基线、源数据只读、参数撤销、失败阻止导出和性能基准后置继续有效。
