# 决策记录（ADR）

本文件记录**在实现过程中做出的、与 `development.md` / `roadmap.md` 存在偏差或需要固化的技术决策**。

约定：

- 每条记录一个决策，编号 `ADR-NNNN`，只追加不重写；
- 状态为 `已接受` / `已取代` / `待定`；
- 若某条决策推翻了文档中的建议，必须写明原文位置和偏差原因；
- 决策被后续推翻时，不改旧条目，而是新增一条并把旧条目标为 `已取代`。

---

## ADR-0001：GUI 主题基线采用 Semi.Avalonia 而非 Avalonia Fluent

- **状态**：已取代（仅外观取向部分，见 ADR-0045）
- **日期**：2026-09-25
- **阶段**：阶段 0 / 阶段 2 前置

### 背景

`docs/development.md` §2.1 与 §21.1 建议首期使用 `Avalonia.Themes.Fluent` 作为桌面 UI 主题。

### 决策

首期 GUI 主题基线采用 **`Semi.Avalonia` 12.1.0.1**（Semi Design 取向，观感接近 Ant Design），
应用为深色主题，并满足以下配套约定：

1. **深色为主，架构预留浅色**：所有颜色不得在 XAML 中硬编码，
   必须定义在本项目自己的资源字典（`Theme/Colors.axaml` 等）中，
   并以 `ThemeVariant` 区分。首期只实装 `Dark`，但切到 `Light` 时不得需要重构。
2. **中文默认，文案资源化**：首期只提供中文界面，
   但所有面向用户的字符串必须走资源（`Resources/Strings.axaml` 或 `.resx`），
   不得直接硬编码到 XAML/代码里。
3. `SemiTheme` 以 `Locale="zh-CN"` 载入。

### 备选方案

| 方案 | 未采用原因 |
|---|---|
| `Avalonia.Themes.Fluent`（文档建议） | 控件较素，`NumberBox` / `DataGrid` / 取色器需自行搭建或另引包；对"列表 + 属性检查器 + 取色"这种编辑器形态帮助小 |
| `FluentAvaloniaUI` 3.1.0 | 观感最好，但为社区包且强绑 Avalonia 12.1.0，与 SkiaSharp native asset 的耦合面更大 |
| `Material.Avalonia` 3.20.0 | Material 密度低、行高松，不适合高密度时间轴/打轴界面 |

### 影响

- `docs/development.md` §2.1 与 §21.1 中"`Avalonia.Themes.Fluent` 首期采用"的表述**已过时**，以本 ADR 为准。
- 时间轴与画布仍然使用自定义绘制（`Avalonia.Skia` + `ICustomDrawOperation`），主题只影响外围控件。
- 对应 `docs/development.md` §18 待决策第 12 项（默认快捷键与界面语言范围）已部分确认：**中文默认 + 资源化**。
- **2026-09-26 追加**：本条中**外观取向**的表述（"Semi Design 取向，观感接近 Ant Design"）已由
  **ADR-0045** 取代；控件模板、版本锁定、颜色与文案资源化三条约束仍然有效。

---

## ADR-0002：Avalonia 采用 12.1.3，SkiaSharp 锁定 3.119.4（不使用 4.x）

- **状态**：已接受
- **日期**：2026-09-25

### 背景

- 文档多处引用 "Avalonia 11" 的文档链接与行为假设，但当前稳定版已是 **Avalonia 12.1.3**。
- `SkiaSharp` 的 NuGet 最新稳定版是 **4.152.1**，但 `Avalonia.Skia 12.1.3` 内部依赖的是 **`SkiaSharp 3.119.4`**。
  若显式引用 4.x，会出现两套 Skia native asset —— 正是 `docs/roadmap.md` §15 点名的风险
  （"第三方包 native asset 冲突 / Linux 启动崩溃或找不到 so"）。

### 决策

1. 目标框架统一 **`net10.0`**，Avalonia 组统一 **`12.1.3`**。
2. `SkiaSharp` / `SkiaSharp.HarfBuzz` 锁定 **`3.119.4`**，`HarfBuzzSharp` 锁定 **`8.3.1.5`**。

   版本推导（已核对 nuspec，非猜测）：

   ```text
   Avalonia.Skia 12.1.3          -> SkiaSharp 3.119.4, HarfBuzzSharp 8.3.1.3
   SkiaSharp.HarfBuzz 3.119.4    -> SkiaSharp 3.119.4, HarfBuzzSharp 8.3.1.5
   取并集（就高）                 -> SkiaSharp 3.119.4, HarfBuzzSharp 8.3.1.5
   ```

3. `Directory.Packages.props` 开启 `CentralPackageTransitivePinningEnabled`，
   把上述版本钉进传递依赖，防止 restore 时被静默抬高。
4. 文档中的 "Avalonia 11" 链接与假设一律按 Avalonia 12 处理，遇到行为差异以实测为准。

### 影响

- 升级 Avalonia 时必须同步复查 SkiaSharp / HarfBuzzSharp 的配对版本，不能单独 `dotnet add package SkiaSharp`。
- 后续若要使用 SkiaSharp 4.x 的新 API，必须先确认 Avalonia.Skia 已跟进到 4.x。

---

## ADR-0003：工程结构按阶段增量创建，不一次性铺满 13 个项目

- **状态**：已接受
- **日期**：2026-09-25

### 背景

`docs/roadmap.md` §3.1 列出了 13 个 `src/` 项目与 7 个 `tests/` 项目的完整目标结构。

### 决策

**按阶段需要增量创建项目**，而不是在阶段 0 就搭出全部空壳。

- 阶段 0 / 1 只创建 `MakoKaraGenerator.Core` 与 `MakoKaraGenerator.Core.Tests`；
- 新增项目必须在某一阶段的出口判据中确实需要它时才创建；
- 目标结构与依赖方向（§3.2）视为**约束**，在任何时刻都不允许违反，但不强制提前存在空项目。

### 影响

- 不会出现长期零引用的空程序集（避免误以为某层已可用）。
- 依赖方向约束从第一个项目起就生效：`Core` 的 `.csproj` 保持零 `PackageReference`。

---

## ADR-0004：本仓库行尾策略为显式 LF

- **状态**：已接受
- **日期**：2026-09-25

### 背景

本机 Git 全局配置为 `core.autocrlf=input`，会导致工作区为 CRLF 的文件在索引中被归一化为 LF，
使"工作区字节"与"检出字节"不一致，难以做字节级比对（金样测试对此敏感）。

### 决策

使用 `.gitattributes` 显式声明：文本文件 `text eol=lf`，二进制资源 `binary`，
`tests/**/golden/**` 使用 `-text -diff` 以保证金样按字节比对。

### 影响

- Windows 上检出也会得到 LF。对 C# 源码无影响，且与 CI 的 Linux 构建一致。
- 金样（golden）数据不得依赖 CRLF，必须显式指定字节内容。

---

## ADR-0005：schema v1 只包含阶段 1 已有的数据

- **状态**：已接受
- **日期**：2026-09-25

### 背景

`docs/development.md` §6.2 的示例歌词数据包含 `styleRef` 与 `overrides` 两个样式字段，
但样式系统（`STYLE-01`..`STYLE-04`）排在阶段 4。

### 决策

`schemaVersion = 1` 的 JSON <b>不包含</b>样式字段，也不包含媒体与轨道：

| 字段 | 何时加入 |
|---|---|
| `styleRef` / `overrides` | 阶段 4（样式系统） |
| 媒体（音频/背景） | 阶段 3（媒体与导入） |
| 多歌词轨道 | 阶段 4 之后 |

加入这些字段时必须提升 `schemaVersion` 并提供迁移器（`CORE-06`，尚未实现），
`docs/development.md` §6.4 明确要求这一点。

> **后续（2026-09-25，阶段 4）**：样式字段已随 **schema v2** 落地，`CORE-06` 也已实现，
> 详见 [ADR-0024](#adr-0024schema-v2-引入歌词样式并落地-cgo-06-迁移框架)。
> 媒体与多歌词轨道仍未进入 schema。

### 理由

在样式语义（继承优先级、覆盖范围、渐变表达）尚未定型前就把字段写进源数据格式，
会迫使迁移器去处理一套猜测出来的结构。推迟比补齐更便宜。

### 影响

- 阶段 1 的验收语料是 `schemaVersion = 1` 的最小结构，迁移框架尚未实现，
  读取到版本不匹配的文件时只报告并拒绝，不尝试转换。
- `docs/development.md` §6.2 的示例在样式字段上属于"目标状态"而非"当前格式"。

---

## ADR-0006：非法数据可表示，持久化 DTO 与领域模型分离

- **状态**：已接受
- **日期**：2026-09-25

### 背景

`docs/roadmap.md` §6 要求校验器能报告"元素越出所属行""时间倒序""ID 重复"等问题，
`docs/development.md` §6.3 要求把结果分为错误/警告/提示三级。

但如果领域模型的构造函数在遇到非法数据时直接抛异常，就<b>无法</b>把一个有问题的项目文件
读进内存并给出可读诊断——用户只能看到一个异常。

### 决策

分两层处理：

1. **领域模型允许表示非法数据。**
   `TimeRange` 的构造函数不校验，另提供 `Create`（可信路径，抛异常）与 `TryCreate`（外部输入）。
   `LyricLine` / `LyricToken` 同理：`Create` 把关，对象初始化器不把关。
   于是 `TimingStatus.Invalid` 才真正可达。
2. **持久化使用独立的 DTO。**
   `ProjectFileDto` / `LyricsFileDto` 等全部成员可空，能表达"字段缺失"这一模型里不存在的状态，
   由 `ProjectDtoMapper` 逐项映射并在每处可疑点追加诊断（缺失字段会说明"已暂时使用什么默认值"）。

### 理由

单纯依赖 `System.Text.Json` 直接反序列化领域模型会撞上两个问题：
`required` 成员在字段缺失时抛异常；`TimeRange` 之类的值类型无法表达"非法且要报告"。
把 DTO 与映射独立出来后，诊断位置可以精确到 `lyrics.lines[0].tokens[2].endUs`，
并且 schema 迁移（`CORE-06`）只需改 DTO 与映射，不触碰领域语义。

### 影响

- 读取<b>绝不</b>静默修正数据：非法时间区间按原样写回，只报告不修改
  （`docs/development.md` §19 设计底线）。
- 行与元素的标识共用<b>同一个命名空间</b>，文档内所有标识必须互不相同。
  选择、撤销栈与缓存键都按标识引用对象，跨层级重名会让引用变歧义。
- `LyricLine.Create` / `LyricToken.Create` 会当场拦住重复标识；
  测试要构造非法数据必须使用对象初始化器，这本身就是"可信路径与数据路径分离"的体现。
- 读取从不修改磁盘文件，`迁移失败时保留原文件` 的约定由 `ProjectStore.Load` 保证。

---

## ADR-0007：Linux 上必须显式引用 SkiaSharp / HarfBuzzSharp 的 NativeAssets.Linux

- **状态**：已接受
- **日期**：2026-09-25
- **阶段**：阶段 0（技术验证）

### 背景

在只引用 `SkiaSharp` + `SkiaSharp.HarfBuzz` 的控制台/库项目里，运行时立刻失败：

```text
System.DllNotFoundException: Unable to load shared library 'libSkiaSharp'
```

### 根因

`SkiaSharp 3.119.4` 的主包依赖了**除 Linux 之外所有平台**的 native 资产：

```text
SkiaSharp 3.119.4 -> SkiaSharp.NativeAssets.Android / Win32 / macOS / iOS / MacCatalyst / tvOS / Tizen
                     （刻意不含 Linux）
HarfBuzzSharp 8.3.1.5 -> 同上，同样不含 Linux
```

Linux 被排除是上游的有意设计（Linux 上更倾向于使用发行版提供的库），
但托管绑定仍需要一个可用的 `libSkiaSharp.so`。

### 决策

在 `Directory.Packages.props` 中同时声明并在非 Avalonia 项目里引用：

```text
SkiaSharp.NativeAssets.Linux    3.119.4
HarfBuzzSharp.NativeAssets.Linux 8.3.1.5
```

### 容易忽略的地方

`Avalonia.Skia` 自己会传递引入这两个包，因此**纯 Avalonia 项目不会暴露此问题**。
只有"无 Avalonia 的离屏渲染 / 导出 / 命令行工具"这类项目才会踩到，
而它们恰恰是导出链路的核心。测试项目若引用离屏渲染代码，也必须能拿到 native 资产。

### 影响

- 新增任何"不含 Avalonia 但要用 Skia"的项目，必须显式引用这两个包。
- 这与 ADR-0002 的版本配对规则绑定：升级 SkiaSharp 时必须同时改这两个包的版本。

---

## ADR-0008：`SKShaper.Result` 的 `Codepoints` 是字形 ID、`Clusters` 是 UTF-8 字节偏移

- **状态**：已接受
- **日期**：2026-09-25

### 背景

`SkiaSharp.HarfBuzz` 的 `SKShaper.Result` 暴露三个数组，官方文档的描述**具有误导性**：

| 成员 | 文档说法 | 实测事实 |
|---|---|---|
| `Codepoints` | "glyph Unicode code points" | **字形 ID**。`夜`（U+591C）在该字体里返回 `0x36E5` |
| `Clusters` | "glyph clusters" | **UTF-8 字节偏移**。七个汉字得到 `0,3,6,…,18`；`é`（e + 组合符）占 3 字节 |
| `Points` | 字形位置 | 与文档一致，可安全使用 |

### 决策

1. `Clusters` 一律按 **UTF-8 字节偏移**解释，并用一张
   `UTF-8 字节偏移 → UTF-16 下标` 映射表换算成 .NET 字符串下标。
   映射表在码点边界上取值，因此落在多字节序列中间的偏移会指向该码点起点。
2. 同一 `cluster` 的多个字形**归并为一个文本元素**（连字、ZWJ emoji、叠加记号都是这种情形）。
   逐字高亮按元素推进，视觉上不会把连字拆开。
3. `Codepoints` 因为实际是字形 ID，被反过来用作**缺字检测**：
   glyph id 为 0 即该字形在字体里不存在。这为 docs/development.md §9.5 要求的
   "字体缺失提示"提供了可靠信号（`ShapedLine.MissingGlyphCount`）。

### 为什么不按码点自己走位

最初实现是按 `Codepoints.Length` 与码点数量一一对应来推算 UTF-16 下标。
实测在含组合符的文本上直接错位：`cafe\u0301 na\u0308ive` 里 HarfBuzz 把组合记号并入前一个字形，
`Codepoints` 与输入码点不再一一对应，于是下标全部偏移，`n` 甚至被跳过。
当时的兜底检查（元素区间是否首尾相接覆盖整行）把这种情形标记为 `MappingIsExact = false`，
正是这个哨兵暴露了问题。改用 `Clusters` 后所有样例均为精确覆盖。

### 影响

- 不要用 `Codepoints` 当码点使用，也不要直接拿 `Clusters` 当下标使用。
- `ShapedLine.MappingIsExact` 必须保留为回归哨兵：它一旦变假，说明有人改坏了这套换算。

---

## ADR-0009：Avalonia 12 不再提供官方 DevTools

- **状态**：已接受
- **日期**：2026-09-25

### 背景

按 Avalonia 11 的习惯引入 `Avalonia.Diagnostics`（用于 F12 开发工具）会还原失败：

```text
error NU1102: 找不到版本为 (>= 12.1.3) 的包 Avalonia.Diagnostics
  - 在 nuget.org 中找到 109 个版本[ 最接近版本: 11.3.22 ]
```

即 `Avalonia.Diagnostics` 的最后一个版本停在 **11.3.22（Avalonia 11）**，
Avalonia 12 的 `Avalonia` / `Avalonia.Desktop` 包里也不含 DevTools 实现
（`AttachDevTools` 只出现在分析器规则表中）。

### 决策

- **不引入** `Avalonia.Diagnostics`。
- 调试手段以下列方式为主：
  1. `Program.cs` 里的 `.LogToTrace()` 与 stderr；
  2. 应用自有的诊断面板（本项目在属性检查器里直接显示整形诊断，
     这正是"缺字 / 回退字体 / 边界精确性"的可视入口）；
  3. 离屏渲染出 PNG 做对照（`OffscreenFrameRenderer`），
     比在 DevTools 里看视觉树更贴近"导出结果是否一致"这个问题。
- 若日后确实需要视觉树检查，再单独评估第三方 `AvaloniaUI.DiagnosticsSupport`，
  并记录许可证与维护状态（docs/roadmap.md §3.3 的第三方包准入要求）。

---

## ADR-0010：字体回退按"忽略默认可忽略字符"判定，绘制必须走整形

- **状态**：已接受
- **日期**：2026-09-25

### 背景

彩虹旗 `🏳️‍🌈`（U+1F3F3 U+FE0F U+200D U+1F308）被渲染成一排豆腐块，
而诊断却报告"回退字体元素 0"——回退逻辑没有生效却也没有报警。

### 根因（两个独立缺陷）

**缺陷一：覆盖率判定的语义陷阱。**
`SKTypeface.ContainsGlyphs(string)` 的语义是**"所有码点都必须有字形"**。
变体选择符 `U+FE0F` 在**任何**字体里都没有字形（glyph id 为 0），
于是真正能画彩虹旗的 `Noto Color Emoji` 被判成"不覆盖"，
回退链一路退到中文字体，最终画出豆腐块。

**缺陷二：绘制时重新整形。**
`SKCanvas.DrawText` 会重新整形，且**不走 HarfBuzz 的合字规则**。
HarfBuzz 已把 ZWJ 序列合成一个字形，而 `DrawText` 把它拆成多个字形并按同一位置重叠绘制，
于是看到一个"白旗 + 彩虹"叠在一起的怪东西。

### 决策

1. 覆盖率判定改为**自己的规则**（`TextElementRules.IsElementCoveredBy`）：
   每个**非默认可忽略**码点都必须有字形；ZWJ、变体选择符、格式字符等一律忽略。
   判定依据以 Unicode 的 Default_Ignorable 性质为准（`TextElementRules.IsDefaultIgnorable`）。
2. 库现成的 `ContainsGlyphs` 不再用于回退判定。
3. 绘制一律使用 `SKCanvas.DrawShapedText(shaper, text, x, y, align, font, paint)`，
   并复用按字体缓存的 `SKShaper`，使**绘制与布局使用同一套整形结果**。
4. 缺字通过 `Codepoints` 里的 glyph id 0 计数（见 ADR-0008），
   暴露为 `ShapedLine.MissingGlyphCount`，供界面提示"字体缺失"。

### 影响

- 正式实现必须**保持布局与绘制同源**；任何"绘制时再整形一次"的捷径都会引入这类不一致。
- `ShapedLine.MissingGlyphCount` 是"字体缺失提示"（docs/development.md §9.5）的数据来源，
  阶段 2 起应在界面上显式显示，导出报告中也要列出。
- 回归防线：`TextElementRulesTests.变体选择符会让库的整体判定误判为不覆盖`
  会在有人改回 `ContainsGlyphs` 时立即失败。
- 交叉引用：ADR-0008（`Result` 数组的单位问题）与本条同属"实测推翻文档描述"的一类，
  实现这类代码时不能只凭 API 名称推断语义。

---

## ADR-0011：时间轴对齐靠"唯一映射 + 共享布局"保证，不靠调像素

- **状态**：已接受
- **日期**：2026-09-25
- **规格**：[timeline-ui.md](../legacy-ui/timeline-ui.md)

### 背景

用户对底部时间轴提出了明确要求：

> 轨道分成三个轨道，但是对齐必须要做好。简单来说：我选择任意一个时间点
> （不论是 Token 的首尾、波形上的一处）都需要在两轨上对齐
> （不考虑缩略图，缩略图的时间度量与下二轨不同），同时标尺需要跨轨道对齐。

同时，旧项目（`/mnt/Data/Program/MakoKaraGenerator`）的审查已经证明这类代码的典型失败方式：

- 绘制与命中测试**各自**实现一遍时间↔像素换算（`Render` 用 `Width-12`，`HitTest` 用 `Width-24`）；
- 结果是视觉上勉强对齐、点击却偏几像素，且偏差随缩放放大；
- 时间轴还从未重绘过（纯数据变化不触发 `InvalidateVisual`）。

### 决策

把"对齐"做成**结构上无法违反**的东西，而不是靠控件里对齐像素：

1. 新增 `MakoKaraGenerator.Timeline` 程序集（不含 Avalonia），提供
   **唯一的** `TimeAxis`（值类型、不可变）承担全部时间↔像素换算。
2. 新增 `TimelineLayout`：持有三条主轨道**共享**的 `TimeAxis`，
   以及缩略条**独立**的 `OverviewAxis`；轨道矩形一律由它给出。
3. 控件**不得**自行计算映射或内边距，只能通过 `TimelineLayout` 取。
4. `AllMainLanesShareHorizontalExtent` 作为自检属性，测试断言其为真。
5. 五条不变式与测试分布见 `timeline-ui.md` §2。

### 理由

"三条轨道看起来对齐"是个**无法通过目视长期维持**的性质：
只要有人给某条轨道加一个内边距，或在某一处顺手写一次换算，它就会悄悄失效。
把它变成类型约束 + 单元测试之后，违反者会在编译期或测试期立刻暴露。

### 影响

- 时间轴的一切几何计算都必须在 `MakoKaraGenerator.Timeline` 内完成且可测；
  控件只做"把 `TimeAxis` 的结果画出来"。
- 该程序集不引用 Avalonia，因此这些不变式可以在无窗口环境下验证。
- `TimeAxis` 的缩放采用"**以锚点为基准**"（`ZoomedAt`）：
  锚点处的时间在缩放前后保持不变。视口左端不能为负，
  因此靠近曲首缩小时左端会被钳制到 0、锚点随之偏移——这是被显式测试记录的行为，
  不是意外。

---

## ADR-0012：音频波形走 FFmpeg 解码 + 自研多分辨率峰值，暂不出声

- **状态**：已接受
- **日期**：2026-09-25
- **规格**：[timeline-ui.md](../legacy-ui/timeline-ui.md) §5

### 背景

用户要求时间轴显示**真实波形**（"每个单独 Token 的位置、整体的波形、
完整的音频波形预览"），并确认本轮**暂不出声**。

调研结论（见下一条 ADR 的姊妹结论）：**不存在可用的现成波形时间轴控件**，
波形数据也必须自己准备。

### 决策

1. 沿用 `development.md` §21.1 的既定路线：**FFmpeg 解码 PCM → 自研峰值聚合**，
   不引入音频设备库（NAudio 等）。
2. 新增 `MakoKaraGenerator.Media`（不含 Avalonia）：
   - `FfmpegMediaProbe`：`ffprobe -v error -select_streams a:0 -show_entries stream=codec_name,sample_rate,channels,duration -show_entries format=duration,format_name -of json <file>`
     （注意 FFprobe 的数值常以**字符串**形式出现，解析时两种形态都要接受）
   - `FfmpegPcmReader`：`ffmpeg -v error -i <file> -vn -ac 1 -f f32le -acodec pcm_f32le -`
     并**流式**读取标准输出（已实测 5 秒 44.1 kHz 得到 882000 字节 = 5×44100×4）
   - `WaveformPeakPyramid`：多分辨率峰值金字塔，第 0 层桶大小 64 采样，
     每层减半，`GetPeak` 自动选层
3. 界面层只接触 `AudioWaveformLoader` / `WaveformPeakPyramid` / `TimeAxis`，
   **不允许**出现 `ffmpeg` / `ffprobe` 字符串（`roadmap.md` §8 的设计要求）。
4. **本轮不出声**。播放只推进播放头与波形跟随；
   音频输出后端（miniaudio 系的 SoundFlow、libvlc、OpenAL 等）另开一轮验证。

### 为什么不逐块解码后全部保留采样

一首 5 分钟 44.1 kHz 单声道 float 采样约 52 MB，一小时超过 600 MB。
采用增量构建器 `WaveformPeakBuilder`，边解码边归并成峰值，
只保留金字塔（约为采样数的 1/32），内存不随歌曲长度线性膨胀。

### 影响

- 峰值有**分辨率下限**（第 0 层桶大小，44.1 kHz 下约 1.45 ms）。
  放大到更细时返回所在桶的峰值，界面上必须提示已达分辨率上限，
  不能让用户误以为看到的是精确波形（与 `development.md` §10.3 对"预览质量"的要求同理）。
- 峰值桶边界按采样 0 对齐，因此**不能用峰值反推精确采样值**。
  逐字打轴需要的是"字头对应的波形峰在哪"，这个精度足够。
- 后续若要更细的波形，可以按需从 FFmpeg 重新解码一小段，而不是把整首歌留在内存里。

---

## ADR-0013：结构体的 `new S()` 不会使用主构造函数的默认参数值

- **状态**：已接受
- **日期**：2026-09-25

### 背景

`TimelineLaneHeights` 最初写成：

```csharp
public readonly record struct TimelineLaneHeights(
    double Overview = 28d,
    double Ruler = 22d,
    double Waveform = 60d,
    double Token = 56d);
```

`TimelineLayout.Create` 里用 `heights ?? new TimelineLaneHeights()` 取默认值。
结果所有轨道高度变成 **0**，`LaneAtY` 对任何纵坐标都返回 `null`。

### 根因（已实测确认）

结构体**总是**有一个隐式无参构造函数（零初始化）。
`new S()` 绑定到它，而**不是**绑定到"所有参数都有默认值的主构造函数"：

```text
new H()         -> X=0, Y=0      // 期望 X=10, Y=20
new H(1, 2)     -> X=1, Y=2
null ?? new H() -> X=0, Y=0
(H)default      -> X=0, Y=0
```

### 决策

- **不要在结构体的主构造函数上依赖"默认参数值"**；
  默认值通过显式的静态属性提供（`TimelineLaneHeights.Default`）。
- 需要"可不填的一整组默认值"时，用 `static` 属性 + `?? Default` 的写法。

### 影响

- 这是一类**编译通过、逻辑静默出错**的陷阱：默认值看起来写了，实际从未生效。
- 同类提醒：`ImmutableArray<T>` 的相等性按引用比较、`Dictionary.TryGetValue` 的
  `out` 变量可空性等也属于"看名字推断语义会出错"的一类——
  本项目已多次因此改为显式写清（见 ADR-0006、ADR-0008）。

---

## ADR-0014：不做波形/时间轴自研轮子的替代方案评估结论

- **状态**：已接受
- **日期**：2026-09-25

### 背景

用户要求"先看看我的需求有没有现成轮子满足，没有再自己做"。

### 调研结论

**时间轴 / 波形控件：没有可用现成轮子。**

| 候选 | 版本 | Avalonia 支持 | 否决理由 |
|---|---|---|---|
| `Aldwych.TimelineEditor` | **不在 NuGet** | 未知 | 作者自述 "work in progress / not anywhere close to finished / an experimental sample rather than a ready to use control / the result of just a single afternoon of hacking"；README 中 `waveform`/`audio`/`drag` **零命中**；GitHub ★27，最后推送 2021-04-27 |
| `WaveformTimeline.Avalonia` | 1.2.1 | ✅ net10.0 + Avalonia `[12.0.0,13.0.0)` | 仅 729 次下载；Avalonia 移植来自标题为 "Vibe-code an Avalonia version" 的 PR（AI 生成原型）；**只画波形与进度**，无逐字块、无拖拽改边界，峰值数据需自己提供 |
| `AvaloniaTimeLineControl` | 1.0.1 | ❌ 仅 Avalonia 11.2.2 | 321 次下载 |

GitHub 仓库搜索佐证：`avalonia waveform` **命中 0 个仓库**；
`avalonia timeline editor` 仅 **1 个**（即上表第一行）。

**左侧歌词栏：顺手的轮子现在是商业授权。**

`Avalonia.Controls.TreeDataGrid` 自 **11.2.0** 起需要 Avalonia Pro 授权
（依赖 `AvaloniaUI.Licensing`，包描述为 "license validation infrastructure for Avalonia Pro"）。
免费仅剩 GitHub 上的 legacy MIT 版本。

### 决策

1. 时间轴与波形**自己做**，参考实现而非依赖：
   `stakira/OpenUtau` 的 `WaveformImage.cs`（Avalonia + Skia 的生产级波形控件）。
2. 左侧歌词栏用 `ItemsControl` / `ListBox` + 选中驱动实现"行内展开"，
   **不引入** `TreeDataGrid`（避免商业授权，且本需求用不到数据网格能力）。
3. 未来若再评估同类控件，准入条件按 `roadmap.md` §3.3：
   许可证、Linux/Windows 支持、native asset、外部运行时、维护状态、发行包体积。

### 影响

- 波形渲染与逐字块交互的全部成本由本项目承担；
  因此 ADR-0011 的"结构保证对齐"与本文档的测试覆盖更重要。
- 自带 DevTools 也已不可用（见 ADR-0009），调试依赖 stdout 日志 + 自有诊断面板 + 离屏出图。

---

## ADR-0015：Skia 自定义绘制必须先裁到控件范围，再 Clear

- **状态**：已接受
- **日期**：2026-09-25

### 背景

加入时间轴控件后，界面出现一个非常奇怪的现象：
**工具栏、歌词栏、画布、属性检查器全部"消失"**，只剩绘制顺序靠后的时间轴与状态栏。

可视树 dump 证明布局完全正常（每个控件的 `Bounds` 都对、`IsVisible=True`），
把根 `Grid` 的 `Background` 换成不透明的洋红也依然"看不见"。

### 根因

`KaraokeFrameRenderer` 与 `TimelineRenderer` 开头都会 `canvas.Clear(背景色)`。
Avalonia 通过 `ISkiaSharpApiLeaseFeature` 交出的 Skia 画布**并没有按控件裁剪**——
`Clear` 会作用到整个窗口表面。

于是绘制顺序成了关键：时间轴位于三栏之后，
它一 `Clear` 就把先画好的工具栏与三栏全擦掉，只留下自己与之后的控件。

### 决策

自定义绘制的 `Render` 里**先裁到控件范围，再交给渲染器**：

```csharp
canvas.Save();
canvas.ClipRect(SKRect.Create(0f, 0f, (float)Bounds.Width, (float)Bounds.Height));
Renderer.Render(canvas, frame);
canvas.Restore();
```

### 影响

- 任何"渲染器自己会 Clear/铺底"的自定义绘制控件都必须显式裁切，不能依赖 Avalonia 给裁剪。
- 这条与 ADR-0011 的"绘制与命中同坐标"是同一类要求：**控件边界也是坐标系的一部分**。
- 症状具有误导性（看起来像布局塌了/背景透明），排查时应先 dump 可视树确认布局，再怀疑绘制。

---

## ADR-0016：一次 `sed` 误改 `x:Class` 造成的连锁假象（教训）

- **状态**：已接受
- **日期**：2026-09-25

### 背景

把 XAML 里的 `Class="..."` 批量改成 `Classes="..."` 时用了
`s/\bClass="/Classes="/g`。因为 `:` 不是单词字符，`\b` 在 `x:Class` 前照样成立，
于是 **`x:Class="..."` 被改成了 `x:Classes="..."`**。

### 症状（极具误导性）

```text
error CS0103: 当前上下文中不存在名称 "InitializeComponent"
error CS0103: 当前上下文中不存在名称 "<每个 x:Name>"
error AVLN3000: Unable to find suitable setter ... for property Click ... for argument System.String
error AVLN3000: Unable to find emitter for node type: XamlX.Ast.XamlAstXmlDirective Line 1, position 2.
```

于是错误地被推断为"名称生成器坏了 / Avalonia 12 的 XAML 编译链路有问题"，
并围绕它做了一系列无效尝试（关 `AvaloniaNameGeneratorAttachDevTools`、
关 `EnableAvaloniaXamlCompilation`、改用 `AvaloniaXamlLoader.Load + FindControl`）。
其中"改成运行时解析 XAML"还引出了新问题（缺少预编译 XAML），进一步加深了误判。

真正的原因直到把最小可复现项目与完整文件**逐段对照**才暴露：
最小项目能过、完整文件的**任意单节**都过不了 → 说明问题不在内容而在**共享的根部**。

### 决策与教训

1. **对 XAML 做批量文本替换时，必须用更精确的模式**，
   例如 `(?<![:\w])Class="` 或干脆只替换确切的 `Classes="` 目标串。
   改完必须 `git diff` 检查 `x:Class` 是否被牵连。
2. **"整个生成链路都坏了"这类结论要先怀疑自己的改动**，
   而不是先怀疑工具链版本。
3. **bisect 时优先切"共享的根"**：本次若一开始就做
   "最小文件 vs 完整文件的头部对照"，几分钟就能定位。
4. 最终保留的写法是 `AvaloniaXamlLoader.Load(this)` + `FindControl` 显式取名
   （与生成器产出的 `InitializeComponent` 等价，少一层隐式依赖），
   事件也全部在代码里接线。这**不是**因为生成器有问题，而是因为它更显式；
   `x:Class` 修复后两条路径都能用。
5. 资源字典的键要查重：同一份 `Strings.axaml` 里重复定义 `ToolbarPause`
   会在运行时抛 `ArgumentException: An item with the same key has already been added`。

---

## ADR-0017：Linux HiDPI —— 用 Wayland 后端拿合成器缩放，不用 X11

- **状态**：已接受
- **日期**：2026-09-25

### 背景

用户反馈"整个窗口缩放太小"。实测（应用内打印 + 截图量像素）：

| 项 | 值 |
|---|---|
| 显示器 | `eDP-1` 2880×1800 物理，300×190 mm |
| niri 逻辑尺寸 | **1600×1000**，即桌面缩放 **1.8** |
| Avalonia X11 看到的屏幕 | `Bounds=0,0,2880,1800`，`Scaling=1` |
| 应用窗口 | 请求 1360×860 *逻辑* → 屏上约 1360 **物理**像素（1:1） |

也就是说：XWayland 把整个 X 空间按物理像素暴露给 X 客户端（1:1），
而原生 Wayland 客户端拿到的缩放是 1.8。
于是同一个 14 px 字号，原生应用画成 25 物理像素、本应用只有 14 物理像素——
**UI 整体只有原生应用的 1/1.8 大**，看起来"极小"。

### 排查过程（都排除掉了）

- `Avalonia.X11.X11PlatformOptions` **没有任何缩放开关**（运行时枚举全部属性确认），
  `TopLevel.RenderScaling` 只读。
- Avalonia 12 的 `UsePlatformDetect()` **不含 Wayland 检测**（`Avalonia.Desktop.dll` 里没有相关字符串），
  Wayland 是独立包 `Avalonia.Wayland`（12.1.3 存在）。
- X11 的缩放来自 `PhysicalDpiScalingProvider`（RandR 物理尺寸），
  而 XWayland 上报的是"96 DPI 的假尺寸"，因此算出 1；应用侧无法纠正。
- 环境变量 `AVALONIA_GLOBAL_SCALE_FACTOR` 在 Avalonia 12 中**不存在**。

### 决策

引用 `Avalonia.Wayland` 12.1.3，并在 `Program.BuildAvaloniaApp()` 中：

```csharp
AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .UseWaylandWithFallback()   // 有 Wayland 就用原生，没有就回退 X11
    .LogToTrace();
```

### 实测结果

- `PlatformImpl = Avalonia.Wayland.WindowImpl`；屏幕实测 UI 尺度与原生应用一致（与顶栏、终端文字同尺寸）、矢量清晰。
- 帧率高、无额外开销；`grim` 截图正常；窗口无多余标题栏（niri 自己画边框）。
- 修好后窗口逻辑尺寸就是真实布局尺寸，各栏比例不再需要为"屏幕很大"做补偿。

### 注意

- `Loaded` 时机上 `Screens.All` 可能为空、`RenderScaling` 可能仍是 1 —— 缩放要等 surface 配置完成后才生效。
  **判断缩放要看屏幕实测，不要只看启动瞬间的属性。**
- 仍待人工验证的点：中文输入法（`text-input-v3`）、`StorageProvider` 文件选择器（XDG portal）、
  剪贴板与拖放。若日后在纯 X11 会话下运行，会自动回退 X11，此时缩放又会偏小（这是 X11 侧无解的部分）。

---

## ADR-0018：自定义绘制操作的 HitTest 决定输入路由（否则控件"看着在、点不着"）

- **状态**：已接受
- **日期**：2026-09-25

### 背景

时间轴控件画得好好的，但**所有交互都失灵**：滚轮不缩放、点色块没反应、拖不动、平移不了。
绘制代码、命中测试算法（有 73 个单测覆盖）都是对的。

### 根因

```csharp
public bool HitTest(Point p) => false;   // ← 一行就够让整个控件对输入"透明"
```

Avalonia 的指针输入走场景图命中测试，而 `ICustomDrawOperation.HitTest(Point)` 是
自定义绘制控件能否被命中的**唯一**依据。返回 `false` 时指针事件会落到下面的父级
（Border/Grid），控件自身的 `OnPointerPressed` / `OnPointerWheelChanged` 永远不触发。

它比"不实现"更隐蔽：控件一切正常，只是收不到输入，且没有任何报错。

### 决策

自定义绘制控件里的 `HitTest` 必须做真实判定：

```csharp
public bool HitTest(Point p) => Bounds.Contains(p);
```

（`Bounds` 为创建绘制操作时传入的控件矩形，坐标为控件局部坐标。）

### 教训

- **"画出来了"与"能点得动"是两条独立链路**，不要以为自绘控件的输入会自动工作。
- 自绘控件若不需要输入（比如纯预览画布），显式返回 `false` 是可以的，
  但那时应当**写明理由**，避免日后有人以为能交互却查不出原因。
- 症状是"控件完全无交互"时，先在 `HitTest` 上找，不要先怀疑命中测试数学。

---

## ADR-0019：音频播放后端用 SoundFlow + 自家 FFmpeg PCM（MEDIA-04）

- **状态**：已接受
- **日期**：2026-09-25
- **取代**：ADR-0012 中"本轮不出声"的临时状态

### 背景

`docs/development.md` §21.2 要求"播放也应通过统一的媒体适配层抽象"，
`docs/roadmap.md` §8 的 `MEDIA-04` 验收标准是"UI 不直接依赖具体播放器"。
候选路线有三条：SoundFlow（miniaudio 的 .NET 封装）、libmpv（本机已装，需自己写 P/Invoke）、
只做接口 + 静音实现。用户选择第一条。

### 决策

- 依赖 `SoundFlow` 1.4.1（**MIT**，自带 `linux-x64` 的 `libminiaudio.so`，**零 NuGet 依赖**）。
- 音频数据<b>由本项目自己的 FFmpeg 管线解码</b>（单声道 f32le，与波形解码参数一致），
  经 SoundFlow 的设备输出。因此"能播什么格式"由 FFmpeg 决定，不由播放库决定。
- 拿不到音频设备时回退到 `SilentAudioPlayback`（位置按墙上时间推进）：
  没有声音时编辑器依然能用，打轴依赖的是"播放头在动"。
- 界面只认 `IAudioPlayback`，任何播放器名字都不出现在 ViewModel 与控件里。

### 实现要点（都是实测踩出来的）

1. **库自带的 `StreamDataProvider` 要求输入流可定位**：它的构造函数会<b>无条件</b>执行
   `stream.Position = 0`，播放开始时还会再来一次。FFmpeg 的 stdout 管道不可定位，
   直接传进去会抛 `NotSupportedException`。
   → 改用库的正当扩展点 `ISoundDataProvider` 自己实现（顺序读、不定位），
   长度用媒体探测的时长换算。
2. **`InitializePlaybackDevice` 之后必须 `device.Start()`**。
   只 Initialize 的话：`State` 显示 `Playing`、位置却永远是 0 —— 因为设备根本没在拉数据。
   这个坑没有任何报错，只表现为"静默无声"。
3. **跳转 = 重建解码会话**：管道不可定位，所以用 `-ss`（输入侧定位）重新起一个 FFmpeg
   进程并重建播放器。代价是几十到几百毫秒，换来不把整首歌的 PCM 缓存到磁盘
   （单声道 48kHz f32 五分钟约 55 MB，而本机 `/tmp` 是内存盘，缓存代价很高）。
4. **位置由界面轮询**（`PositionUs`），不指望播放器往 UI 线程推事件：
   播放器的位置更新发生在音频线程上，让界面直接吃跨线程事件很容易变成偶发崩溃。
   `PlaybackEnded` 事件保留，但明确写明"可能在非 UI 线程触发"。

### 验证

- 单元测试：静音实现用可控时间源断言位置推进/暂停/钳制/结束；
  SoundFlow 实现真实加载 WAV 播放，断言位置推进、暂停后位置不动、跳转后位置正确。
- **客观验证出声**：播放期间 `pw-cli ls Node` 里出现了播放节点
  （不是"没抛异常"就算过）。

---

## ADR-0020：歌词导入/导出的接口形状，以及"未定时元素"的打点语义

- **状态**：已接受
- **日期**：2026-09-25

### 1) 导入器返回"文档 + 报告"，不是一个文档

`docs/development.md` §7.1 给的原型是 `LyricsDocument Import(Stream, LyricsImportContext)`，
但 §7.3 要求导入报告（行数、元素数、逐字时间数、缺失与估算数、跳过的行、
无法识别的标签、编码与换行处理结果）。报告不是"附加信息"，而是导入的必要产出。
因此改成返回 `LyricsImportResult`（文档 + 报告）：
用一个返回值把两者绑在一起，调用方就不可能忘记取报告。

### 2) 导出器拆成 Plan 与 Export 两步

§7.4 要求"导出前必须先给出损失提示"。若只有一个 `Export`，
"先提示"就只能是文档里的口号。拆成 `Plan`（只评估、不写）与 `Export`（真正写）之后，
这条要求在类型层面绕不过去。

### 3) 未定时的元素必须能被打点

**问题**：元素没有时间时不会出现在时间轴上（这是有意的——绝不把没有的时间画成一块）。
但"补齐缺失的逐字时间"是打轴的第一步：若改时间必须经过"时间轴上的块索引"，
就会出现"因为没时间所以不在时间轴上，所以无法给它时间"的死循环。
本喵的实现最初就踩了这个坑（打点直接报"该元素当前不在时间轴上"）。

**决策**：改元素时间只改工程数据，不依赖时间轴的块列表。
时间轴是时间的**视图**，不是时间的**来源**。

### 4) 打点的诚实性

按 `[` 记起点时，元素原本没有终点，这时程序只能给一个占位终点（一帧）。
占位值是程序给的，因此状态必须是 `Estimated`（估算）而不是 `Edited`；
用户按 `]` 定下终点后，状态才变成 `Edited`（已修改）。
把程序猜的值标成"用户已修改"是明确禁止的（docs/development.md §19）。

### 5) 播放后端优先级

miniaudio 的默认探测在本机会选中 **OSS**（`/dev/dsp` 在新系统上通常不可用），
因此显式指定优先级：`PulseAudio → Alsa → Jack`。
现代 Linux 桌面（PipeWire）通过 `pipewire-pulse` 提供 PulseAudio 兼容层，
指定后实际激活的是 `PulseAudio`（截图与自检都确认过）。

### ADR-0019 补充：轮询定时器必须在播放时启动

播放改造后发现一个很迷惑的现象：**声音正常播放，播放头却一动不动**，
于是基于播放头的打点全部落在同一个位置（用户实测反馈）。

根因很小：旧实现里"启动轮询定时器"写在 `OnPlayClicked` 的墙上时钟分支里，
改成走 `IAudioPlayback` 时把这一句一起删掉了——定时器只被 `Stop()`、从未被 `Start()`。
`SyncPlayheadFromPlayback` 本身没问题，只是从来没被调用过。

修法：把"按钮文案 + 定时器启停"收进一个 `UpdatePlaybackUi()`，
播放开始与每次 tick 都调用它（播放自然结束时也会随之停掉定时器）。

**教训：把状态同步集中到一个方法里**，而不是散落在各个分支里各写一半——
"启动/停止成对出现"这类约定，散开写迟早会漏。

---

## ADR-0020 补充：增强 LRC 导出格式（逐字时间）

导出普通 LRC 时用户反馈"逐字时间被丢掉了"。LRC 其实有逐字形式：

```text
[00:14.20] <00:14.25> When <00:14.67> the <00:15.12> night
```

### 决策

新增 `EnhancedLrcExporter`，并把它作为界面默认导出器
（没有逐字时间的行自动退化成普通行级 LRC，因此一条「导出歌词」覆盖两种需求）。
格式规则：`[行时间标签]` 之后，每个带时间的元素前面放一个 `<mm:ss.xx>` 标签。

### 关键取舍：标签前后<i>不</i>额外插入空格

标签后面的文本一直到下一个标签为止都属于同一个词，因此"标签 + 空格 + 词"会让空格
变成词的一部分。本项目把词间空白单独当作一个**无时间**的元素（空白不参与演唱），
导出时若再补空格，文本就会与工程里的文本不一致——而"禁止静默修改用户数据"
（docs/development.md §19）是设计底线。

所以导出规则是"一个字都不加"：**导出的文本与工程文本逐字相同**，
需要空格的场合本来就作为元素存在于数据里。英文歌词因此会写成
`<t1>When<t2> the`（空格在数据里）而不是 `<t1> When`，
两者对解析器等价（词边界由标签决定）。

### 另一个必须守住的点

若某行**没有元素**，或元素的文本拼接与行文本不一致（数据被外部改坏过），
该行按**行文本整行导出**，不写逐字时间——宁可少写时间，也绝不能把行里的字丢掉。
这两种情况都会写进损失清单。

### 逐字时间的往返

导出再导入后，逐字时间与行时间都能原样恢复（有往返测试）。
唯一的固有损失是：没有逐字时间的元素会被并入相邻元素——
LRC 没有表达"这里没有时间"的语法，因此这件事被如实写进损失清单，
而不是悄悄按行时间平均分配（那是被明令禁止的"伪装成实测数据"）。

---

## ADR-0021：渲染层拆成 Text / Rendering.Abstractions / Rendering.Skia 三个项目

**状态**：已采纳（2026-09-25，阶段 4 起点）

**背景**：阶段 4 要落地 `IRenderPipeline` 与不可变 `ProjectSnapshot`
（docs/development.md §10.2）。此前"文字整形 + 帧渲染"代码全部住在
`samples/TechnicalSpike` 这个阶段 0 的验证示例里，产品代码反而不能引用它；
而"渲染输出"这类数据一旦直接暴露 Skia 类型，后续的导出模块和脚本 SDK
就会被某个具体渲染后端绑死。

**决定**：按 docs/roadmap.md §3.1 / §3.2 建立三个产品项目，依赖方向单向：

```text
Rendering.Skia -> Rendering.Abstractions -> Core
               -> Text                  -> Core
App ----------> Rendering.Abstractions（只认接口）
```

| 项目 | 放什么 | 刻意不引用 |
|---|---|---|
| `MakoKaraGenerator.Text` | `ShapedLine` / `ShapedTextElement`、`ITextLayoutEngine`、`TextLayoutOptions`、`TextElementRules` | Skia、HarfBuzz、Avalonia |
| `MakoKaraGenerator.Rendering.Abstractions` | `IRenderPipeline`、`RenderRequest`、`RenderTarget`、`RenderFrame`、`PixelFormat`、`RenderQuality`、`ProjectSnapshot` | Skia、Avalonia |
| `MakoKaraGenerator.Rendering.Skia` | `SkiaTextLayoutEngine`、`SkiaTypefaceCoverage`、渲染实现 | Avalonia |

**关键取舍**

- **`RenderFrame` 是裸像素（BGRA 8888 预乘），不是 Skia 位图**。两端都不需要转换：
  Avalonia 的 `WriteableBitmap` 直接支持该格式，FFmpeg 的原始帧输入也认它。
  代价是预览要付出一次内存拷贝（1200×700 约 3.4 MB），换来的是
  "预览、离屏导出、金样测试走同一条代码路径"——这正是阶段 4 出口要求的
  "相同快照与时间得到一致的视觉结果"。
- **字体覆盖率判断（`SkiaTypefaceCoverage`）留在 Skia 项目**：它必须问具体的字体对象，
  而 `TextElementRules` 只保留不依赖图形库的纯规则（ADR-0010 的判据本身不变）。
- **`ITextLayoutEngine` 把字号放进 `TextLayoutOptions`**（而不是构造参数）：
  一个引擎实例可以按"族名 + 字号"缓存字体对象，预览与导出各自持有一个实例，
  避免每帧重新打开字体文件（docs/development.md §10.5）。
  native 字体对象不跨线程共享，因此接口明确<b>不</b>保证跨线程可用。
- **`ITextLayoutEngine` 不实现"绘制"**：绘制留在渲染后端，布局只产出位置数据。
  这使布局结果可被时间轴、测量与金样测试复用，而不必创建画布。

**连带的清理**

- `samples/TechnicalSpike/Text/` 整体提升进产品项目（与 `GraphemeSplitter`
  提升进 `Core` 同一做法），示例项目改为引用新项目；
  它现在只剩下阶段 0 的帧渲染器作为离屏验证工具。
- `tests/MakoKaraGenerator.Spike.Tests` 里的整形与字体回退测试全部迁入
  `tests/MakoKaraGenerator.Rendering.Tests`（项目与测试按关注点对齐），
  空掉的原测试项目已删除。

#decision #rendering #architecture

---

## ADR-0022：预览改为渲染管线产出的裸像素位图

**状态**：已采纳（2026-09-25，阶段 4）

**背景**：阶段 0 的预览是"控件自己把 Skia 画布从 Avalonia 的场景图里借出来直接画"：
零拷贝、很直接，但渲染规则住在控件里，等于**预览与最终输出各有一套规则**
（docs/development.md §3.2 明令禁止）。而且它读的是界面挑出来的"当前行文本 + 逐字时间表"，
即渲染输入里混着编辑状态。

**决定**：`KaraokeCanvas` 退化为一个纯视图——拿 `ProjectSnapshot` + 时刻问
`IRenderPipeline` 要一帧裸像素，再贴成 `WriteableBitmap` 显示。
渲染规则、采样规则、字体布局全部在管线里，界面再也无法影响画面内容。

**代价与取舍**

- 多一次内存拷贝（1200×700 约 3.4 MB/帧）。换来的是预览、离屏导出、金样测试
  走同一条代码路径，这正是阶段 4 出口"相同快照与时间得到一致视觉结果"的前提。
- 组合根在 `App`：只有它写 `new SkiaRenderPipeline()`，界面只认 `IRenderPipeline`。
- 画布自己按"快照实例 + 时刻 + 样式 + 质量 + 目标尺寸"缓存位图；快照按**引用**比较，
  内容相等要遍历全部歌词行，逐帧做纯属浪费。真正的帧缓存后续下沉进管线（RENDER-05）。

**同时修掉的真 bug**：填充边界曾经取"所有元素已唱到的最右位置"的最大值，
而尚未开始的元素会被算成"已唱到它的左边缘"，于是一个还没唱到的元素
把边界直接拉到整行末尾（一整句瞬间全亮）。正确规则是"完全没开始的元素不参与计算"，
再加上"每个元素的位置随时间单调不减、参与计算的数量只增不减"，
既得到正确边界，又保证填充永不倒退。这条有专门的回归测试。

**验证方式**：屏幕会话当时被锁屏覆盖（合成器不再给窗口发帧回调，界面根本不渲染），
因此改用离屏核对——把管线在若干时刻输出的帧导出成 PNG 逐张检查
（半字填充、当前元素高亮、未唱颜色、整行不重排）。**界面上的实时预览仍需人工确认**。

#decision #rendering #preview

## ADR-0023：样式覆盖先在运行时建立来源追踪，再接入项目 schema

- **状态**：已接受
- **日期**：2026-09-25
- **阶段**：阶段 4（STYLE-01）

### 决策

样式解析使用固定优先级：默认样式 → 命名预设 → 歌词行覆盖 → 文本元素覆盖。
解析结果同时保存每个属性的来源，供属性检查器显示“来自默认/预设/行/元素”。

本轮只加入 Core 中的运行时模型与纯逻辑测试，不把样式字段写入 schema v1。
样式持久化仍需先完成 `CORE-06`，届时提升 schema 版本并提供迁移器。

### 理由

样式属性必须区分“未覆盖”和“覆盖后的值”，否则恢复继承时会丢失默认值。
先固定合并语义和来源追踪，可以让 UI、预览和后续 JSON 迁移共享同一套规则，避免把继承逻辑散落在控件和 ViewModel 中。

### ADR-0023 补充：样式模型复审后的六处修正

首版合入后做了一轮复审，改动如下（都在接入 schema 之前完成，避免把问题带进 JSON 形状）：

1. **字体族换成三态值**。`FontFamilyName` 的"值"本身可以是 `null`（=用渲染后端默认字体），
   所以可空类型表达不了"覆盖成默认字体"——那是"覆盖成空"，不是"不覆盖"。
   新增 `OptionalValue<T>` 表示"未设置 / 设置为空 / 设置为具体值"三态，
   并支持隐式转换，使覆盖里可以直接写 `FontFamilyName = null`（覆盖成空），
   而**不写这个属性**才是"不覆盖"。
   落进 JSON 时两者不会混淆：**键缺失**=未设置，**键为 null**=设置为空。
   （类型名不叫 `Optional`：那是部分语言的保留字，CA1716；工厂方法放在非泛型的
   `OptionalValue` 上，因为泛型类型不声明静态成员，CA1000。）
2. **来源表改用枚举键** `LyricStyleProperty`。原来按属性名字符串查，
   界面里一个拼写错误会**安静地**显示错误来源；现在拼错在编译期就被拦住，
   越界的枚举值直接抛异常而不是回退成"默认层"。
3. **分层校验**。原来无论哪一层产生非法值，异常的参数名恒为 `lineOverride`
   （预设写错字号会被报成"行覆盖有问题"，排查方向直接跑偏）；
   现在每一层应用后立刻校验，异常指向 `defaultStyle` / `preset` / `lineOverride` /
   `tokenOverride` 中真正出错的那一层，消息里也带上层级名与实际取值。
4. **预设加稳定 `Id`**（与显示名 `Name` 分开：显示名允许随时改措辞，而它一旦参与持久化，
   改名字就会让旧工程找不到预设），并新增 `LyricStylePresetLibrary`——预设要有统一栖身处，
   否则"当前预设"没有稳定表示。库校验标识唯一，带三个起始预设
   （`default` / `karaoke-large` / `karaoke-warm`，只在当前可渲染的属性上有区别）。
5. **写明预设语义**：预设提供的是**一整份基线**（替换默认样式的全部属性，全部标为 `Preset`），
   行/元素的覆盖在其上逐项生效；预设本身不是"只覆盖一项"的叠加层。
6. **partial 文件注释约定**：类型级 `<summary>` 只写在主文件里，
   各分部只写一行"本文件负责什么"，不再七个文件重复同一段注释。

#decision #style #lesson

---

## ADR-0024：schema v2 引入歌词样式，并落地 CORE-06 迁移框架

- **状态**：已接受
- **日期**：2026-09-25
- **阶段**：阶段 4（STYLE-01 / STYLE-02、CORE-06）

### 背景

样式模型（ADR-0023）先只做了运行时对象。要让它真正可用，样式必须能跟着工程存下来：
基线样式、当前预设标识、行级与元素级覆盖。这一步同时触发两件早就该做的事——
提升 `schemaVersion`（ADR-0005 明确规定"加入字段必须提版本并提供迁移器"）
以及实现一直缺着的 `CORE-06` 迁移框架。

### 决策

**1. 样式数据放在哪**

| 数据 | 位置 | 理由 |
|---|---|---|
| 基线样式 `lyricStyle` | `project.json` | 属于合成级设置，与画布、帧率同级 |
| 预设标识 `lyricStylePresetId` | `project.json` | 只作来源信息，供界面显示"当前预设" |
| 行级覆盖 `styleOverride` | `lyrics.json` 的行上 | 与行同生命周期 |
| 元素级覆盖 `styleOverride` | `lyrics.json` 的元素上 | 同上 |

**存的是样式本身，而不是"预设的名字"**：预设库日后可以改，已定稿项目的外观不能因此跟着变。
`lyricStylePresetId` 只是为了让界面能说"这是从哪个预设改出来的"。

**2. 迁移框架（CORE-06）在 JSON 层工作**

```text
文件文本 → JsonObject → [迁移链] → 反序列化成 DTO → 映射成领域模型
```

- 迁移步骤实现 `IProjectSchemaMigration`，每次只跨<b>一个</b>版本，多步串成链。
- 每一步只关心相邻两版之间的差异，不必写"从任意旧版直达当前版"的组合代码。
- 注册表缺步骤属于<b>程序缺陷</b>，执行器立刻抛异常；读取层把它转成可读诊断
  （"这个文件读不了"）而不是让异常冒到界面上。
- **读取从不改写磁盘文件**：迁移结果只存在于内存，迁移日志按提示级进入报告。
- 迁移后文档的 `schemaVersion` 在内存中推进到当前版本，因此 DTO 与映射器永远只面对当前版本，
  不必到处兼容旧字段（docs/development.md §6.4 的原话）。

**3. 覆盖的三态在 JSON 里怎么表示**

覆盖的语义是"键缺失 = 不覆盖"，而字体族的<b>值</b>本身可以是 `null`（=用后端默认字体）。
但序列化设置会忽略 null，于是"缺失"与"null"在 JSON 里长得一样，两者就分不开了。

因此用一个显式字段表达"覆盖成默认字体"：

```json
"styleOverride": { "clearFontFamilyName": true, "fontSizePx": 60 }
```

两个字段同时出现会被判为错误并说明按哪一个处理（宁可报错，也不猜用户意图）。

**4. v1 → v2 迁移不做数据改写**

v2 新增字段全部遵循"缺失即默认"：没有 `lyricStyle` 就是默认样式，没有 `styleOverride`
就是不覆盖。往旧文件里插入这些默认值是<b>在用户数据里凭空造内容</b>，
所以这一步只登记版本、留一条日志说明"你的文件被按 v2 解释了"。

**5. 元素级覆盖目前只有颜色生效**

整行只整形一次是"高亮不跳动"的前提（RENDER-02），逐元素改字号/字体/纵向位置会改变整行度量。
因此领域模型<b>允许</b>存这些属性（不拒绝用户的意图），但校验器会把它们明确报成警告，
而不是让渲染默默忽略——静默忽略会让人以为设置已经生效。

### 影响

- `Project.CurrentSchemaVersion` 与 `LyricsDocument.CurrentSchemaVersion` 均为 2。
- 读取路径新增 `schemaVersion` 判定与迁移；写入路径不变（永远写当前版本）。
- 新增诊断码：`InvalidStyleValue`、`UnsupportedTokenStyleOverride`、`ProjectSchemaMigrated`。

#decision #persistence #style #migration

### ADR-0024 补充：样式编辑规则放进 Core，而不是界面

样式编辑有几条**很容易写错、错了又不容易发现**的规则，因此实现成 Core 里的纯函数
（`LyricStyleEditor`）而不是散在 ViewModel 里：

- **改一项就只写一项的覆盖**。把"当前生效的整份样式"都写成行覆盖，等于让这一行脱离继承：
  以后改工程基线它就再也不跟着变了（这正是 STYLE-02 要防的事）。
- **手工改动后清掉预设标识**，否则界面会一边显示"当前预设：大字"、一边显示的却不是大字。
- **空覆盖收敛成"没有覆盖"**，文件里不会留下空对象。

界面上新增的三个控件与实际语义对应：

| 控件 | 作用 |
|---|---|
| 样式作用范围（工程 / 本行） | 决定下面两项写进基线还是写进选中行的覆盖 |
| 样式预设 | 把整份预设**写进工程**（不是只记名字） |
| 样式来源 | 逐属性显示"默认 / 预设 / 本行覆盖 / 元素覆盖"，即 STYLE-01 的验收项 |
| 清除本行覆盖 | 恢复完全继承 |

元素级覆盖目前只有颜色参与绘制，且界面尚未提供入口（模型与渲染都已支持，
校验器会对不支持的属性明确报警）。

#decision #style #ui

### ADR-0022 补充：空时刻的提示画在界面层

当前时刻没有可显示的歌词行时（`t=0`、或第一句之前），画布区域显示一个浅色胶囊提示。
要点：

- 提示是**界面层的控件**，不进入渲染输出。预览与最终视频共用同一条管线，
  在管线上多画一个字就等于视频里多一个字。
- 提示的显隐判据与管线取行**用同一个 `LyricSampler`**，否则会出现
  "提示说没有歌词、画面却在画歌词"这种自相矛盾（预览必须等于视频）。
- 胶囊底色 + 固定浅色字，两个主题变体刻意取同一组值：它压在**渲染出来的画面**上，
  而画面背景色是用户可以改的，跟界面主题无关。
- 加它的理由很实际：`t=0` 时画布本来就该是空的，但"空"与"渲染坏了"长得一模一样，
  实际排查时就因此多绕了一圈（见 `df97b19` 的提交信息）。

#decision #rendering #ui

## ADR-0025：用例层（Application）与工程生命周期

### 为什么现在建 Application 层

docs/development.md §13 的推荐工程结构里有 `MakoKaraGenerator.Application`（用例、服务、
项目操作），§16 阶段 0 也明确要求"建立 Core、Application、App 三层"。此前直接跳过了这一层，
所有界面逻辑都堆在 ViewModel 里；`EDITOR-02` 的验收恰好是"UI 操作调用 Application 层"，
所以随保存/打开一起补上。

依赖方向按文档：`App -> Application -> Core`。Application 不引用 Avalonia、不引用 Skia、
也不直接启动外部进程。

**副作用：命名空间撞名。** `MakoKaraGenerator.Application` 会遮住 Avalonia 的 `Application`
（`App : Application` 直接编译失败）。处理办法是在 App 里把该类型写成 `Avalonia.Application`，
只有两处（`App.axaml.cs` 与 `Loc.cs`）。给 Avalonia 起别名反而会让整个项目到处都要记别名。

### 工程会话（`ProjectWorkspace`）

- 持有当前 `Project`、落盘目录、以及"最后落盘的那个实例"。
- **脏 = 当前实例与最后落盘实例不是同一个对象**。Core 的 `Project` 是不可变记录，
  界面每次编辑都换新实例，所以引用比较既正确又便宜（按值比较要遍历全部歌词行）。
- 打开失败时**不动**当前工程：点错目录不该让手上的活消失。
- 打开成功但有校验错误时照常打开：能打开与能导出是两件事（§6.3）。
- 窗口与视图模型始终共享同一个 `ProjectWorkspace` 实例；新建通过 `StartNew` 在原会话内替换工程，不能只替换窗口字段。
- 内置演示工程启动时注入的临时音频属于初始化数据，加载完成后通过 `AcceptCurrentAsSaved` 建立干净基线，不能让启动过程制造未保存改动。

配套的一条硬规则，写进 `LyricStyleEditor`：**没有实际变化时必须返回原实例**。
启动时初始化预设下拉曾顺手"应用"了一次默认预设，结果工程实例被换掉，
标题立刻显示 `演示工程 *`——刚打开就"有未保存改动"。同类问题以后还会出现，
所以规则下沉到 Core 并有回归测试，而不是只在界面加护栏。

### 工程包与保存位置

- 工程是**目录**（§6.1），入口 `project.json`，歌词同目录的 `lyrics.json`。
- 推荐后缀 `.mkgproj`，只用于新建时拼目录名；**打开**时不检查后缀，
  任何含 `project.json` 的目录都算工程。
- 另存为走"选父目录 + 输入工程名"两步（Avalonia 没有"保存目录"对话框），
  名字按文件名规则校验；`/`、`\` 与 `.`、`..` 一律拒绝——Linux 的
  `Path.GetInvalidFileNameChars()` 不含 `\`，只靠它会让 `a\b` 通过校验，
  拿到 Windows 上就是路径穿越。
- 保存**不做校验拦截**（§6.3 把校验时机限定在打开、导入、导出）；
  覆盖已有工程必须先确认（§19：可能丢数据的操作不得静默执行）。

### 未保存改动

新建 / 打开 / 关闭窗口前若工程是脏的，弹「保存 / 放弃改动 / 取消」。
关掉对话框（Esc 或点关闭）一律算"取消"，**不**当成默许放弃——那是最容易丢工作的默认值。
标题栏同时显示 `工程名 *`。

新建工程先显示参数对话框，填写工程名、画布宽高与帧率后才替换当前会话；
取消参数对话框不改变当前工程。快捷键与菜单点击共用一个工程操作入口，
同一次按键的重复 `KeyDown` 事件只允许提交一次请求。

### 本轮范围：不持久化媒体

工程只存画布、歌词、样式与样式覆盖。**音频文件路径不进工程**，因此重新打开工程后
需要再导入一次音频才有波形与播放（schema v3 的媒体引用留待后续）。

#decision #application #persistence #editor

## ADR-0026：命令与撤销/重做

### 分层

docs/development.md §13 把「命令」放在 Core、把「撤销/重做」放在 Application，照此落地：

- **Core**：`IProjectCommand`（标识 + 是否可合并 + 纯函数 `Apply`）与 `ProjectCommand`
  （委托工厂、`Combine` 合成）。
- **Application**：`ProjectHistory`（撤销栈、重做栈、手势合并、容量裁剪），
  由 `ProjectWorkspace` 持有；`Workspace.Execute/Undo/Redo`。

### 为什么不做 Do/Undo 成对接口

`Project` 与歌词模型都是**不可变**的，撤销只需要把旧实例换回来，反向逻辑根本不必写。
传统 Do/Undo 的 Undo 是每个操作各写一遍、最容易两侧不对称的地方；这里从设计上不存在。
历史项因此就是 `(标识, 改动前实例, 改动后实例)`，而整棵树结构共享，
多占的内存只有被改动的节点。

判据是**按引用**比较：撤销回到"最后落盘的那个实例"时工程就重新变干净，
不需要额外的"已保存"标记。也因此有一条硬规则：**没有实际变化必须返回原实例**
（见 ADR-0025 里"刚打开就显示未保存"那次事故）。

### 扩展性：新增操作要做什么

1. 在视图模型里把原来的 `UpdateProject(新工程)` 换成
   `Execute("域.动作", project => …)`——一行；
2. 若需要撤销菜单显示中文，在 `EditorViewModel.DescribeCommand` 加一个 `case`
   与一条资源文案。

引擎、历史项结构、界面接线都不需要动。命令的 `Name` 是**稳定标识**而不是显示文本：
标识要能被代码比较（合并判断），文案必须资源化（ADR-0001），两者职责不同。

### 手势合并与合并组

- **合并**（`Mergeable`）：拖拽每帧产生一条命令，同名且允许合并、且在时间窗（默认 900ms）内的
  会合成一步。时间窗只是兜底——拖动结束时时间轴控件发出 `BlockTimeChangeCompleted`，
  视图模型据此 `BreakMergeGroup`，靠"手势结束了"这个事实划界。
- **合并组**（`BeginGroup`）：一次用户动作产生多次数据改写时用，例如打点会同时写时间
  与把占位终点标成估算。组内第一条命令建立组长（名字取自组名），后续改动并入它；
  **组的第一条不会并进组之前的旧步骤**（否则会静默吃掉一个无关的撤销步，有回归测试）。

### 不变量与防线

- 视图模型里**唯一**改写工程的地方是 `UpdateProject`，而它只有一个调用点（命令采纳路径）。
  越过这条线直接改工程的动作永远不会进撤销栈，且表面看不出来。
- `Undo`/`Redo` 会校验"传入的工程正是栈顶步骤的产物"，不一致直接抛错。
  有人绕过历史改工程时，宁可立刻报错也不要静默把那些改动一起卷走。
- 打开/新建工程清空历史；保存**不**清空（保存后撤销回去就该重新变成"有未保存改动"）。

### 界面

工具栏「撤销 / 重做」，`Ctrl+Z` / `Ctrl+Shift+Z` / `Ctrl+Y`，按钮提示写明
"撤销：修改元素时间"这类具体说明。**焦点在输入框里时 Ctrl+Z 让给文本框自己的撤销栈**，
否则会变成"撤销了工程而不是刚打的字"。撤销不还原选中与展开状态，但会检查选中是否悬空
（撤销一次拆分/删行后原下标可能越界），必要时退到有效位置。

#decision #commands #undo #editor

## ADR-0027：撤销之后必须把"跟着数据走"的视图一起刷新

### 事故

撤销一次拖动之后：画布回退了（渐变色块重新出现），但时间轴色块位置、元素时间输入框
都停在原样。数据层是对的，界面自相矛盾。

**成因**：工程数据是界面上好几处视图的共同来源——画布读渲染快照，时间轴读逐字块列表，
歌词栏读行视图模型，元素时间输入框读选中的芯片。而"视图 → 数据"的同步分散在两处：

- `UpdateProject`（换快照 + 发通知）；
- `Reload`（重建逐字块与歌词栏、并重新选上原来的元素）。

`Reload` 原先只写在各个编辑动作的末尾（拖动、改时间、拆分、合并）。撤销/重做走的是
**另一条路**（命令执行 → `AdoptProject`），那条路只调用 `UpdateProject`，
于是"数据回去了、派生视图没回去"。这类 bug 下层测试全绿：引擎没错，错的是接线。

### 修法

把"刷新到什么程度"收进唯一通道 `AdoptProject`，由它按**换工程之前**的差异决定：

| 变化 | 动作 |
|---|---|
| 歌词结构变了（`Lyrics` 不是同一个引用） | `Reload`，按原来的行/元素下标重新选上 |
| 样式变了 | 通知 `LyricStyle` / `CurrentPresetId` + 刷新来源说明与整形诊断 |
| 都没有（一般不会发生） | 只换快照 |

同时**删掉**编辑动作里那几处自己写的 `Reload`：撤销与编辑既然共用这条通道，
各自补一次必然漏一处——这次漏的就是撤销。拆分/合并调用点保留自己的 `Reload`，
因为它们要选中**另一个**元素（拆完选后半段），那是动作语义而不是同步问题。

### 新增视图时的检查清单

在这里加同步时问两句话：

1. 这个视图是从工程数据推出来的吗？（是 → 必须由 `AdoptProject` 负责刷新）
2. 撤销回去之后它还会正确吗？**只在"编辑时"刷新的视图，撤销时一定错**。

### 附带建立的防线：视图模型级测试

新增 `tests/MakoKaraGenerator.Spike.App.Tests`（roadmap `EDITOR-09` 的第一批）：
不创建窗口，只驱动 `EditorViewModel`。这次的事故正好说明它的必要性——
引擎测试覆盖不到"界面接线"，而人工点击才能发现的问题应该在提交前就被抓住。
已验证该测试确实能抓住此 bug：把修复停用后 2 个测试立刻变红。

#decision #undo #testing #lesson

### ADR-0026 补充：连续型输入必须两件事都做

字号滑块从 70 拉到 100 会生成几十个撤销步，撤销只能一格格退——因为**只有 `token.time`
被标了可合并**，样式编辑默认不可合并。修复走通用机制，不给滑块开特例：

1. **命令标 `mergeable: true`**：`style.baseline`（工程范围）与 `style.line-override`（行范围）
   都是连续型输入，作用范围只决定写哪一层，不改变"一次拖动应记成一步"。
   行范围这一半是**测试发现的**——主人只测了工程范围，同一个 bug 在另一条路径上同样存在。
2. **手势结束时 `BreakMergeGroup()`**：滑块松手（`PointerReleased`，隧道路由一定经过滑块）
   就是这一步的终点；时间窗（900ms）只是兜底。
   ⚠️ 不用 `PointerCaptureLost`：它是 **Direct 路由**、实际源是滑块内部的 thumb，
   父控件收不到——写上去会得到一个永远不会触发的处理器，比不写更糟。

**新增连续型控件时的检查清单**：标 `mergeable` + 接一个"手势结束"断组信号，
两件都做才算接完。判断方法很直接：**从控件一端拖到另一端，撤销栈应该只多一步。**

#decision #undo #ui

## ADR-0028：歌词行文本编辑（EDITOR-03）

### 改文本时逐字时间怎么办

这是本任务里唯一需要想清楚的语义。用户改一个错字，**不该**把打好的逐字时间全丢掉，
也不该把时间错位到旁边的字上。采用的规则（`LyricTextResplitter`，Core 里的纯函数）：

1. **行文本与元素文本必须一致**：所有元素文本拼接后正好等于新的行文本；
2. **时间只在没被改动的位置上继承**——正文的公共前缀与公共后缀里、且**整体**落在未改动
   区域内的旧元素原样保留（含标识、时间与状态）；改动区间一律变成"未定时"的单个文本元素。

**为什么按文本对齐而不是按下标对齐**：下标对齐下，往中间插一个字会把后面每个元素的时间
整体错位一格——那比"需要重新打点"更糟，因为错位的时间看着是对的，实际全错。

**跨边界的旧元素**（例如合并过的"夜空"只改了一半）：它不能整体保留，只能退成未定时；
但落在未改动区域里的那些字**依然要在**。第一版实现按"前缀 + 改动区 + 后缀"三段拼接，
正是这里漏字（前缀里属于跨边界元素的那个字被整段来源覆盖掉了）——改成
**按新文本的位置装配**（旧元素放到原位、空缺补成未定时）之后从结构上不会再有这类漏字。
多文本元素占多个位置，因此覆盖标记必须单独记，不能只看槽位是否为空。

### 提交时机

输入框**每敲一次就提交一次**，命令标为可合并（连续输入 = 一步撤销），
回车或失焦时断开合并。不等到回车再提交，是因为拖时间轴、切行、撤销都可能在不触发失焦的
情况下重载界面，那时输入框里没提交的文本会被悄悄盖掉。逐字提交还顺带带来两个好处：
画布实时跟着变；`ApplyLineText` 在文本没变时直接返回，界面回填不会反复入栈。

⚠️ **`TextBox.TextChanged` 是延后到调度器回合才触发的**：程序里改了文本立刻读状态会读到旧值
（验证探针因此误判过一次）。写这类验证必须等一拍。

### 新建与删除行

- 新行**不预填时间**：预填会让时间轴显示一段并不存在的演唱区间，而"程序猜的"和"用户给的"
  必须能区分（§19）。
- 行与元素**共用标识命名空间**（ADR-0006），新行标识用 `line-<n>`、新元素用 `token-<n>`，
  编号规则改为**扫描现有文档**取下一个可用值（原来的自增计数器需要在导入后手工对齐，
  忘了就会撞 id 抛异常）。
- 删除后选中上一行，接着还能继续改；一行不剩时自然没有选中。

#decision #editor #lyrics

## ADR-0029：音频路径随工程保存（schema v3）

### 存什么

`Project.AudioPath` 在**内存里是绝对路径**，写盘时由 `ProjectStore` 交给
`ProjectMediaPath` 换算：只有音频**确实位于工程目录之内**时才改存相对路径（整个工程目录
因此可以搬走），位于别处的一律存绝对路径——跨目录树的一串 `..` 既不美观，也一样搬不走。
读取时相对路径按工程目录解析回绝对路径。打开工程后音频文件缺失只提示、不报错、
更不改动工程里的路径（工程本身仍然可用，只是没有波形与播放）。

### 两条版本线

顺带修掉一个潜在 bug：校验器原先拿**歌词文件的 schema 版本**去和**工程文件的当前版本**比，
`ProjectStore` 也把歌词文件迁移到工程的版本上。两份文件各有各的 `schemaVersion` 字段、
各有各的版本线，此前恰好都是 2 才没暴露；工程文件升到 3 之后，一个完全合法的工程立刻被
报成"版本不支持"。

**规则**：每份文件迁移到**它自己的**当前版本，校验器也各比各的。
只在确实一起变化时才同步升版——v2 的样式字段同时改了 `project.json`（基线）与
`lyrics.json`（覆盖），所以两者都升到 2；v3 只加了音频路径，歌词文件因此留在 2。

同时把两处测试里硬编码 `"schemaVersion": 2` 的替换改成用 `Project.CurrentSchemaVersion`——
那种写法在版本升级后会**静默失效**（替换不中，文件仍然是合法的，测试却"通过"了别的分支）。

### 恢复音频不能把工程弄脏

导入音频是工程改动（进撤销栈，标识 `media.import-audio`）。但打开工程后按记录恢复音频时，
路径本来就一致，因此 `ProjectMediaEditor.SetAudioPath` 在**没有变化时返回原实例**——
界面的"有未保存改动"按引用比较，凭空造实例会让刚打开的工程立刻显示成已修改
（与 ADR-0025 里预设那次是同一类坑，规则同样下沉到 Core 并有测试）。

### 顺带

新建工程时先摘掉旧音频（`DetachAudioAsync`），否则界面会留着上一个工程的波形。

#decision #persistence #media #migration

## ADR-0030：完成度状态只维护一份（roadmap §18），AGENTS.md 走"行动视角"

### 背景

项目有三处适合写"现在做到哪了"的地方，很容易变成三份互相矛盾的清单：
`README.md` 的进度段、`docs/roadmap.md` 的各阶段实现状态标注、以及新增的 `AGENTS.md`。

这不是假想的问题：`README.md` 的进度段当时就停在"565 个测试、`CORE-06`/`CORE-07` 未勾选"，
比实际状态落后了 200 多个测试和整个阶段 2/3，而它偏偏是**新读者和新代理最先看的文件**。

### 决定

| 文件 | 角色 | 写什么 |
|---|---|---|
| `docs/roadmap.md` §18 | **唯一权威状态源** | 阶段出口达成情况、MVP 验收 14 条对照、全部任务号状态 |
| `docs/roadmap.md` 各阶段小节 | 就地注解 | 一句话"做了什么、还差什么"（不重复任务号表格） |
| `AGENTS.md` §5 | **行动视角** | 每项要碰哪些文件、验收判据、有哪些坑；**不重复状态表格**，只给指针 |
| `README.md` | 面向人的入口 | 阶段级摘要 + 测试数量；**不再维护逐项勾选清单**（它是最容易漂移的形式） |

规则：**同一个事实只在一个地方维护**；`AGENTS.md` 明确写"两份不一致时以 `roadmap.md` §18 为准"，
使漂移可被发现而不是变成两个都对的假象。

### 理由

- 逐项 checkbox 清单放在 README 里读起来舒服，但**没人会在每次提交时去勾它**，
  它的信息量又最大（所以最容易被当成事实）——这是最坏组合。
- "状态"和"怎么动手"是两类内容：前者属于项目文档（人也要看），
  后者只有代理需要。按这个切分，`AGENTS.md` 不必复述 `roadmap`，也不至于变成空壳。

### 顺带

盘点时发现 4 处**需求自身的矛盾**（自动保存/崩溃恢复被列为 P0 却排在阶段 7、
描边/阴影在 P0 与 P1 重复分级、阶段 4 出口要求超出 P0 范围、
`development.md` §16 与 `roadmap.md` 的阶段编号不一致）。
这些属于需求层面的裁决，当时未自行改动，只记入 `roadmap.md` §18.5 与 `AGENTS.md` §8；
同日经用户逐条确认，结论见 **ADR-0031**。

#decision #docs #process

## ADR-0031：需求裁决——阶段编号、P0 样式下限、自动保存归属、导出技术底线

### 背景

一次完整的需求盘点（对照 `development.md` 的功能分级与 MVP 验收 14 条、
`roadmap.md` 的 68 个任务号）发现四处**需求文档自身的矛盾**，以及若干在"该开工时"必然挡路的待决策项。
这些属于需求层面的裁决，不由实现者自行决定，因此逐条提请确认。

### 决定

| 事项 | 决定 |
|---|---|
| 两份文档阶段编号不一致 | **以 `roadmap.md` 为准**（8 个阶段）。`development.md` §16 只作技术演进说明，并就地注明"阶段归属与编号以 roadmap 为准" |
| P0 的样式下限 | **照 P0 原文全做**：纯色、水平渐变、描边、阴影、不透明度、基础缩放（`STYLE-03` 全覆盖） |
| 阶段 4 出口的"进入动画" | **不计入阶段出口**。动画依赖 `STYLE-04`（关键帧与缓动，属 P1），`STYLE-04` 仍留在阶段 4 任务表但不阻塞出口 |
| 自动保存与崩溃恢复 | **提前到阶段 2 之后做最小版**，新增任务 `EDITOR-10`；阶段 7 的 `QA-05` 改为回归与强化 |
| 视频容器/编码器/质量预设 | MP4 + `libx264`（`yuv420p`）；草稿 CRF 26/`-preset fast`、标准 CRF 20/`medium`、高 CRF 16/`slow` |
| 是否随应用分发 FFmpeg | **不自带**。启动时探测 `ffmpeg`/`ffprobe`，缺失给可读提示并指向文档中的安装说明 |
| 透明视频首期格式 | **ProRes 4444（`.mov`）**；`EXPORT-07` 允许延后，不阻塞普通 MP4 |

### 理由

- **阶段编号**：任务号体系（`CORE`/`EDITOR`/`STYLE`/`EXPORT`…）、阶段出口判据与 DoD 都在 `roadmap.md`，
  提交记录与代码注释也一直引用它（实测有 15 处以上 `docs/roadmap.md §X` 引用）。
  反过来改 roadmap 会牵动全部小节编号并让这些引用失效。
- **P0 样式下限**："描边/阴影"在 P0 与 P1 各出现一次，不下死定义就无法判断 `STYLE-03` 何时算达标。
  选"照原文全做"是**尊重既有验收标准**：MVP 第 7 条本来就要求"基本渐变"，而描边与阴影是
  卡拉 OK 歌词样式的常见下限，推迟到 P1 会让 MVP 的成品外观明显不达标。P1 那一条相应改写为
  **自由多色渐变、描边渐变、发光、遮罩**，与 P0 形成"基础 / 高级"而不是重复分级。
- **进入动画**：这是**出口标准超出 P0 范围**的唯一一处。两种修法里选了"降出口"而不是"升任务"，
  理由是 MVP 验收 14 条（产品的硬口径）从未要求动画，把 `STYLE-04` 提进 P0 会让 MVP 范围悄然变大。
- **自动保存提前**：它是 P0 明确要求，却排在最后一个阶段。提前到阶段 2 之后的另一个原因是
  **越晚做成本越高**：此后每改一次数据格式（v3 音频路径、将来的 v4 背景媒体）都要照顾恢复路径。
  阶段 7 保留 `QA-05` 但改为长时运行与多工程场景的回归，不再承担"从零实现"。
- **导出底线**：H.264 + MP4 兼容性最好、编码最快，符合"先能出片"的目标；三档 CRF 覆盖
  草稿预览与成片两种诉求。**不自带 FFmpeg** 让仓库与发布包保持轻量（单文件发布约 30MB），
  代价是首启必须友好提示缺依赖——已在"风险"里记下。透明输出选 **ProRes 4444** 是因为
  它是不透明丢失 Alpha 风险最低的路径，且该任务本来就允许延后，不阻塞 `EXPORT-06`。

### 影响

- `development.md`：§16 加权威性说明；§4.2 的 P1 去掉与 P0 重复的项；§18 就地标注已决定项。
- `roadmap.md`：阶段 2 新增 `EDITOR-10`；阶段 4 任务的 `STYLE-03` 范围补全、出口去掉动画；
  阶段 5 记录技术底线；阶段 7 的 `QA-05` 改为回归；§18.1/§18.3/§18.5 同步。
- `AGENTS.md`：§5 的 A 类与 C 类、§8 的裁决状态同步。
- 实现侧：这些决定只影响 `EXPORT-01`~`09` 的**默认参数与预设命名**（以及 `EDITOR-10` 的存在），
  不改变它们的接口形状——按 `roadmap.md` §5 的既定原则，暂定方案不写死进领域模型。

### 仍然悬空

`development.md` §18 尚有 4 项未定，各自在对应阶段开工前确认：
Linux 发布形式（阶段 7 前）、是否支持多轨道（动轨道模型前）、
脚本进程模型（阶段 6 前）、节拍与频谱是否进 P1（`STYLE-04` 之后）。

#decision #requirements #process #export

## ADR-0032：STYLE-03 收口——三态渐变、元素级覆盖契约、schema 两条版本线各自推一步

> 历史说明：本 ADR 记录 STYLE-03 实施时的三态样式方案。当前实现已由 ADR-0040 收口为未唱/已唱
> 两种字幕颜色状态；当前播放或选中元素属于编辑状态，不再拥有独立输出颜色。

### 背景

`STYLE-03`（纯色/线性渐变/描边/阴影/不透明度/基础缩放）的实现过程中撞到三处"必须先把契约定下来"的地方：

1. **渐变只有一对颜色时，逐字扫描会消失**：同一对起止色被用在"未唱"与"已唱"两遍绘制上，
   两遍画出的像素完全相同——填充边界看不见了，卡拉 OK 最核心的效果被自己的样式功能抹掉。
2. **元素级覆盖的渲染跳过条件只看颜色**：只设描边/阴影/不透明度的元素会被整段跳过，
   而模型与校验器认为它是生效的（静默失效）；另有一项（缩放）在现有管线里**根本无法**按元素正确实现。
3. **两份文件的字段同时新增**：`project.json` 的 `lyricStyle` 与 `lyrics.json` 的 `styleOverride`
   都加了新字段，而两条版本线此前共用一张迁移步骤表。

### 决定

| 事项 | 决定 |
|---|---|
| 渐变语义 | **三态各自一对起止色**（`UnsungGradient*` / `SungGradient*` / `CurrentGradient*`，共 6 个字段）；某一对的两端都设置才走渐变，只设一端退回该状态的纯色 |
| 渐变几何 | 横向，起点对齐**行左端**、终点对齐**行右端**；随整行缩放（缩放是画布变换的一部分） |
| 元素级覆盖契约 | 允许：颜色族（含渐变）、描边、阴影、不透明度；拒绝：缩放、字号、字体、纵向位置 |
| 元素重画判据 | 改为"有任何**参与绘制**的属性"（原来只看颜色），并把排版/整体变换类属性一并排除 |
| schema 版本 | **两条线各自推一步**：`project.json` 3→4、`lyrics.json` 2→3 |
| 迁移步骤表 | **按文件拆开**：`ProjectSchemaMigrator.Default`（工程文件）与 `LyricsDefault`（歌词文件），并让 `TryReadSchemaVersion` 按**该文件自己的**支持版本比较 |
| 开发期旧字段 | 单一对 `gradientStartColor` / `gradientEndColor` **翻译**成"已唱渐变"并写日志，而不是静默丢弃 |
| 界面颜色值语义与输入约束 | 十六进制文本框（`#RRGGBB` / `#RRGGBBAA`）继续可用；**留空＝未设置**；非法文本不提交、只在状态栏报错。取色器只是另一种输入入口，不能改变这些语义 |

### 理由

- **三态各自可渐变**：两个"省事"的选择都不行——共用一对颜色会抹掉扫描效果（见背景第 1 条）；
  把渐变当整行装饰则等于接受"进度不可见"。三态各一对是唯一既能做渐变又不牺牲逐字效果的形状，
  也正好对上阶段 4 出口那句"未唱/当前/已唱渐变样式"。
- **只设一端时退回纯色**：宁可不渐变，也不要替用户猜另一端该是什么颜色（与"禁止静默修正用户数据"同源）。
- **元素级拒绝缩放与排版类**：缩放靠**画布变换**实现、作用范围是整行，套在元素上会把整行一起缩放；
  字号/字体/纵向位置会改变整行度量，而整行只整形一次（改它们要逐元素重排版，属另一件大工程）。
  这三类若不说出来，用户会以为生效了——因此校验器报 `UnsupportedTokenStyleOverride`，渲染明确忽略。
- **元素重画判据放宽**：描边、阴影、不透明度、渐变都能按元素裁切绘制，跳过它们没有任何道理，
  原来的"只看颜色"是 STYLE-01/02 时期"元素级只支持颜色"的残留。
- **两条版本线各自推**：版本号描述的是**文件形状**，两份文件的形状这次都变了。共用一张步骤表时，
  歌词文件会被工程文件的步骤"顺手"迁移一遍（日志与实际改动对不上）；拆开之后，
  歌词文件的 `1 → 2` 必须自己登记——**拆分当场就暴露了这个隐藏依赖**（八个诊断测试立刻变红，
  它们用的正是 v1 歌词文件）。
- **翻译旧字段而不是丢弃**：那对字段出自未发布的开发版，但仍可能出现在用户已存的工程里。
  直接无视等于静默丢数据；译成"已唱渐变"是语义上最接近原文的落点，并且**在迁移日志里说清楚**。
- **颜色值仍以十六进制语义为基线**：当时先用文本框避免引入新依赖，并保证无窗口测试可覆盖；
  现在由 ADR-0049 增加取色器作为同一语义的另一种输入入口。"留空＝未设置"是让可选颜色
  （描边、阴影、渐变）可清除的必要条件——否则用户没办法把描边去掉。

### 影响

- **Core**：`LyricStyle` / `LyricStyleOverride` / `LyricStyleProperty` / `LyricStyleResolver` /
  `ProjectValidator` / `PersistenceDtos` / `ProjectDtoMapper`；新增 `V3ToV4Migration`、
  `LyricsV1ToV2Migration`、`LyricsV2ToV3Migration`、`LegacyGradientFields`；
  `Project.CurrentSchemaVersion = 4`、`LyricsDocument.CurrentSchemaVersion = 3`；
  `ProjectSchemaMigrator.LyricsDefault`；`ProjectStore` 的 `Deserialize` 接收步骤表。
- **Rendering**：`SkiaRenderPipeline` 按状态取渐变对、元素重画判据放宽、当前元素的区间也套上完整样式。
- **App**：检查器新增 11 个颜色入口（三态各 3 + 描边 + 阴影）与 3 个阴影滑块；
  `EditorViewModel` 新增按属性通用的 `GetStyleColor` / `SetStyleColor` 与
  `DescribeStyleProperty`（原先用 `_ => "当前颜色"` 兜底，新属性全被显示成"当前颜色"）。
- **预设库**：新增"渐变高亮"（三态渐变）与"描边阴影"两档。
- **测试**：807 项；渲染侧以像素断言（渐变不抹平进度、描边增加像素、阴影下移、不透明度变淡、
  缩放变小、元素级只设描边也生效）。

### 已知限制（有意保留）

- 每个状态只能是一对起止色：不支持多段渐变、径向/垂直渐变。
- 元素级覆盖**没有界面入口**（`StyleScope` 只有 工程/行 两档）；模型与渲染已就绪。
- 阴影的 X/Y 模糊半径相同（`SKImageFilter.CreateDropShadow` 的两个 sigma 由同一个值给出）。
- 渐变按行宽横向铺开，因此行越短渐变越"陡"；这是"随行缩放"的必然结果，不是缺陷。

#decision #style #schema #rendering

## ADR-0058：0.5.0 样式引擎下一阶段顺序

- **状态**：已接受并于 0.5.0 实施
- **日期**：2026-09-30
- **范围**：统一样式模型之后的高级渐变、模型收尾和字体回退

### 决定

0.5.0 已按“高级渐变几何和渲染闭环 → 规范模型收口 → 可持久化字体回退链”的顺序完成。性能基准暂缓到脚本系统完成之后。

第一批高级渐变只支持线性／径向和画布／行坐标，并保留现有线性 RGB／HSV 插值。字形坐标、逐字进度坐标和 Oklch 不在第一批实现。任何不支持的组合必须在编辑器或诊断中明确说明，不得静默回退成旧端点颜色。

### 依据

当前 `GradientSpec` 仍保留后续批次的描述能力，但首批矩阵已经由 GradientSupport、校验器、编辑器和 Skia 共同执行。统一模型的渐变端点与阴影 XY 平面编辑地址已从渲染和界面移除。

### 脚本基础判断

仓库已经具备脚本未来可复用的边界：不可变 `ProjectSnapshot`、统一 `LyricSceneSampler`、规范样式解析和预览／导出共享的 `IRenderPipeline`。仓库尚未实现脚本 SDK、`ILyricEffect`、Roslyn 编译服务、可回收加载上下文、脚本持久化或脚本 UI；`SPIKE-07` 与 `SCRIPT-01`～`09` 仍保持未开工。脚本阶段开始前另行裁决主进程或独立进程模型。

### 版本

本阶段交付版本为 0.5.0；工程 schema v17、歌词 schema v14、模板库 v3。不增加旧格式迁移。

#decision #style #gradient #font #scripting

## ADR-0033：视频导出落地——会话式编码器、裸帧直通、进度与报告

### 背景

阶段 5（`EXPORT-01`~`09`）。ADR-0031 已经定下容器与编码器（MP4 + `libx264` yuv420p）、
三档质量预设、**不随应用分发 FFmpeg**、透明输出首期用 ProRes 4444；
`development.md` §12.2 给了接口（`IMediaProbe`/`IMediaEncoder` 与"不要拼命令行"的底线）、
§12.4 给了流程（校验 → 快照 → 探测 → 编码器 → 按帧渲染 → 写帧 → 合并音频 → 校验 → 报告）、
§13 给了结构（`MakoKaraGenerator.Export`，依赖 `Rendering.Abstractions` / `Media`）。
本轮把这三条落到可运行的代码，余下的是若干实现层的取舍。

### 决定

| 事项 | 决定 |
|---|---|
| 代码落点 | 新增 `src/MakoKaraGenerator.Export`（依赖 Core / Rendering.Abstractions / Media + CliWrap），测试 `tests/MakoKaraGenerator.Export.Tests` |
| 编码接口形态 | **会话式**：`IMediaEncoder.StartAsync` → `IVideoEncodingSession.WriteFrameAsync` → `CompleteAsync`；帧的**所有权**在写帧时移交给会话 |
| 帧输入 | 裸 BGRA 经标准输入（`-f rawvideo -pix_fmt bgra … -i pipe:0`）：`RenderFrame` 本来就是预乘 BGRA（ADR-0021），因此"最后一根管子"零转换。为此给 `RenderFrame` 加了 `PixelMemory`（零拷贝写流） |
| 音频进入 MP4 | 能用就 `-c:a copy`（aac / mp3 / ac3 / eac3 / alac / opus），否则重编码 `aac 192k`；**理由写进报告** |
| 时长 | 以 `Composition.OutputDurationUs` 为准（导出上下文里它是必填项，校验器已兜底）；音频时长只用于报告差值。`-shortest` 让成片以较短的一方为准 |
| 进度记账 | 以**我方写出的帧数**为进度来源；FFmpeg 的 `-progress pipe:1` 只取 `drop_frames`。剩余时间按已用时间线性外推，**完成度 < 5% 时不给估计** |
| 单帧渲染失败 | **不中断导出**：计入失败帧数、用背景色顶一帧继续，时间轴因此不会错位 |
| 取消与超时 | 取消＝杀掉编码进程（CliWrap 语义）；超时（默认 6 小时）**单独记账**，报成"编码超时"而不是"用户取消" |
| 界面边界 | 界面只认 `VideoExporter`；**界面层不得出现编码器名与命令行参数**，由源码守卫测试钉住；导出用**独立的渲染管线实例**（导出在后台线程上跑） |
| 质量档位 | 三档只改 CRF 与编码 preset，**不动分辨率与帧率**（它们属于工程设置） |
| 本轮不做 | `EXPORT-04` 的图片/视频背景（需 schema v5 与渲染背景层）、`EXPORT-07` 的 Alpha 输出（需 `SPIKE-06`） |

### 理由

- **会话式而不是一次性接口**：开发文档要求"启动进程 / 写入帧 / 合并音频 / 读进度 / 取消并清理临时文件"
  这几件事，一次性 `EncodeAsync(所有帧)` 表达不了"边渲染边写"（内存会堆下整部片子）、
  也表达不了"随时取消"。会话式还让"进程起不来"能在 `StartAsync` 就报出来。
- **帧所有权移交**：如果调用方还要负责释放交给编码器的帧，每条路径都得成对地想着它，
  迟早漏掉一个。写帧时移交给会话，会话在 `finally` 里释放，取消与异常路径也都覆盖到。
- **不做像素格式转换**：ADR-0021 决定 `RenderFrame` 是预乘 BGRA，而 FFmpeg 的 `bgra` 输入正是这个布局，
  于是导出不需要"再转一次"——少一次转换就少一处颜色偏差的可能。
- **音频优先复制**：复制是无损且快的；但 MP4 只接受少数几种音频编码，
  与其在封装阶段失败（用户已经等了十分钟），不如在开始前按源编码决定"复制还是重编码"，
  并把决定写进报告——毕竟"我的音轨被重编码了吗"是用户会问的问题。
- **进度不解析 FFmpeg 的输出**：FFmpeg 的进度是"它吃进了多少"，而我方知道"我写出了多少"，
  后者才是准确的（尤其在被 `-shortest` 提前掐断时）。`drop_frames` 只有它知道，所以只取那一项。
- **单帧失败不中断**：卡拉 OK 成片动辄几分钟到十几分钟，一帧渲染异常就丢掉整部片子是不可接受的；
  用背景色顶一帧能保住音画同步，失败数与原因都进报告，用户自己判断要不要重导。
- **导出用独立管线实例**：导出在后台线程、预览在界面线程，共用一条管线会让两边同时碰它的布局缓存。
  "预览与成片一致"靠的是**同一套代码 + 同一份 `ProjectSnapshot`**（docs §3.2 的原话是"共享时间采样与渲染规则"，
  不是"共享对象"）。
- **把"界面不碰编码器"变成测试**：这条规则没有编译期约束，写一行 `"libx264"` 就能绕过；
  于是加了一条扫描界面层源码的守卫测试（禁编码器名与命令行参数），并顺带把 `FfmpegAvailability`
  改名成后端中性的 `EncoderAvailability`，让视图模型不必知道后端叫什么。

### 影响

- 新增：`ExportQuality`（三档预设）、`VideoExportOptions`、`ExportProgress`/`ExportStage`、
  `ExportReport` + `ExportReportFormatter`、`IMediaEncoder`/`IVideoEncodingSession`、
  `AudioEncodingPlan`、`FfmpegCommandLine`、`FfmpegMediaEncoder`、`EncoderAvailability`、`VideoExporter`。
- `Media`：`IMediaProbe` 增加 `ProbeFileAsync`（容器 + 全部流），用于校验产物；
  `FfmpegMediaProbe` 相应实现。
- `Rendering.Abstractions`：`RenderFrame` 增加 `PixelMemory`。
- 应用：`App.axaml.cs` 装配导出器（组合根）、工具条「导出视频…」、
  `ExportDialog`（路径/质量/进度/取消/报告）、`EditorViewModel.Export.cs`（状态机）。
- 测试：`Export.Tests` 24 项（命令行拼装、音频策略、可用性探测、端到端导出、产物校验、
  抽帧确认画面内容、界面纯净度守卫）。

### 已知限制（有意保留）

- **画面里烧入歌词**，没有独立的透明歌词层输出（`EXPORT-07` 待 `SPIKE-06`）。
- 不支持图片/视频背景（`EXPORT-04` 的另一半，需要 schema v5）。
- 没有导出队列与自动重试：重试＝再点一次导出（会覆盖同一路径）。
- 不支持自定义分辨率/帧率覆盖：导出严格按工程合成设置。
- `-shortest` 意味着音视频不等长时成片取较短的一方，差值在报告里给出。

### 补记（同日）：进度回调的线程契约

首次联调时"点导出直接闪退"：`Progress<T>` 被写在 `Task.Run` 的 lambda 里构造，
而 `Task.Run` 里没有 SynchronizationContext → `Progress<T>` 退化成"在线程池线程上直接叫回调"
→ 对话框更新控件触发 `The calling thread cannot access this object`。

修法两条，缺一不可：
1. **在界面线程上构造** `IProgress<T>`（捕获正确的上下文），再交给后台导出；
2. 对话框侧再加一道 `Dispatcher.UIThread.CheckAccess()` 兜底——"某处漏了一次 marshal"的代价是整个应用闪退，
   这个代价不该由"记得写对"来承担。

回归测试的写法也值得记：第一版断言"`Post` 被调用过"是**假通过**（`await` 的续体也会投递），
改成数"回调真正跑在哪个线程上"（排队式上下文 + 测试线程自己泵空）之后，
用**变异检查**（把 bug 改回去）确认它确实会红——`Expected: 27 / Actual: 16、20、13`。

#decision #export #ffmpeg #architecture

---

## ADR-0034：合成参数界面、循环选区与 J/K/L 播放控制

日期：2026-09-25　状态：已采纳

这一轮把 roadmap §18.4 里"属于 P0 但没有任务号"的三件事补齐：合成参数界面、循环选区、
播放控制键。三件都是**界面与视图模型**的活，一行数据格式都没动（因此没有 schema 迁移）。

### 决定一：合成参数放在检查器末尾，「回车 / 失焦才提交」

- `Composition` 的五个字段（画布宽/高、帧率、底色、输出时长）放进检查器最下面的「合成」区块。
  它们是**工程级设置**（改一次影响整片），与上面带作用范围的样式编辑分开放，避免混在一起。
- **数值框不逐键提交**：逐键会让 `1920` 在敲到 `1` 时就写进工程——画布宽度先变成 1，
  撤销栈里还多出三步。敲回车或失焦才算一次提交。字符型的颜色框保持逐键提交（更跟手）。
- **输出时长留空 = `null`（"还没有定"），不是 `0`（"非法值"）**：两者在校验与导出里的
  处理完全不同——前者在编辑期只是提示，后者直接报错。另有「按音频」「按歌词末尾」两个按钮，
  没有依据时置灰（按音频要求已加载音频；按歌词末尾要求歌词里至少有一个已知时间）。
  这两个按钮只是**替用户算一个数值再走同一条命令**，不是让模型自己去猜。
- **非法输入不提交**：状态栏说明 + 输入框退回生效值。不取整、不夹范围、不猜（docs §19）。
  奇数边长**照存不误**（模型允许，ADR-0006），但区块里提示 H.264 导出会被拒绝——
  这属于"如实告知"，不是"阻止用户"。
- 「按歌词末尾」取行时间与元素时间里最靠后的终点；缺失时间与非法区间**一律跳过**，
  绝不为了让按钮有结果而替用户猜一个位置。一行都没有时按钮置灰。

### 决定二：循环选区是**界面状态**，且允许"无效"这个状态

- 入点/出点是界面状态：**不随工程保存、不进撤销栈**（与"缩放到哪里"同类），
  因此退出重进不会留着上次的选区，也不会因此把工程标成"有未保存改动"。
- 两个端点**各自独立设置**（可以先按 `O` 再按 `I`），所以"入点晚于出点"是真实可到达的状态。
  这时**不静默交换两端**，而是当作没有选区并在状态栏说明原因：静默修正会让人以为
  自己设反的那次操作被接受了（docs §19）。只有**拖拽**出来的端点才由手势自己排序——
  方向是那个手势的一部分，不存在"他其实想反过来"的歧义。
- 手势分工：**Shift+拖标尺 = 画选区**，不按 Shift 的拖动仍然只是定位播放头（既有手势一个没动）。
  按住 Shift 期间"抓住播放头"不再抢占指针——那是两种意图，不该互相打架。
  选区画在标尺带上（色带 + 两端竖线），颜色走 `Colors.axaml`（ADR-0001）。
- 播放到出点即跳回入点；**开始播放时若播放头已在选区之外，先跳进选区再播**，
  否则"有选区"要等播到末尾才有反应，手感上像是选区没生效。
  播放头在选区**之前**时不动它：它会自然播进选区再开始循环。
- 两端都夹到 `[0, 整曲时长]`（时间轴本身也只铺这么长）；还没加载音频时只保证非负。

### 决定三：`J/K/L` —— 但 `J` 不是倒放

- `K` = 播放/暂停（与空格同义）；`L` = **只负责开始播放**；`J` = 后退 1 秒（Shift 为 5 秒）并继续播放。
- **`L` 不做成开关**：播放键被做成切换键时，连按两下会把声音关掉，而用户的意图是
  "再按一次重新听"。这条用测试钉住（`播放键只开始播放不会切反`）。
- ⚠️ **`J` 退化成"后退"而不是"倒放"**：播放后端（SoundFlow，ADR-0019）只支持正向播放。
  这里记明原因，免得日后有人以为漏了倒放实现。退到开头就停在 0，不绕回去。

### 决定四：绘制的验证要落到像素

时间轴那类"画出来了没有"的东西最容易悄悄失败——不报任何错，界面上只是"拖了没反应"。
因此这一轮给时间轴加了 3 条**像素断言**（选区确实画在标尺上、选区之外一个像素没动、
无效选区什么都不画）。为此给 `samples/TechnicalSpike.App` 加了
`InternalsVisibleTo MakoKaraGenerator.Spike.App.Tests`，与 `MakoKaraGenerator.Export`
的做法一致（引擎内部细节对测试可见，但对其它项目不可见）。

#decision #editor #timeline #playback #testing

---

## ADR-0035：行复制的时间语义、水平对齐，以及自动保存的落盘策略

日期：2026-09-25　状态：已采纳

这一轮补齐 `development.md` P0 清单里剩下的三项（批次 2）。其中自动保存的实现细节
随实现一并记录在下面第三节。

### 决定一：复制歌词行＝"紧跟原行，时间整体后移"

- 副本的文本、切分、逐字时间、样式覆盖全部照搬，**时间整体后移一个"原行时长"**，
  于是副本紧接在原行之后：做重复段落（副歌）时只需微调，不必重新打轴。
- **只在原行时间合法时才平移**；没有时间（普通 LRC 导入后的常见状态）或时间非法时，
  那份状态**原样保留**。不得为了让副本有个位置而编造时间（docs §19）。
- 副本可能与下一行重叠（原行紧挨下一行时必然如此）：**照插不误，但不去挪别的行**——
  那是在替用户改他没选中的数据。重叠交给校验器报出来，状态栏另外提示一句。
- 标识由调用方提供：行与元素共用同一个标识命名空间（ADR-0006），
  分配器要知道整份文档才知道哪些号是空的。

### 决定二：水平对齐三档，且**没有边距参数**

- `LyricHorizontalAlignment{Left, Center, Right}`，默认 `Center`——必须与加入本字段
  之前的行为一致（横向恒定居中），否则老工程一打开就"变样"。
- **没有边距**：左/右对齐就是贴着画布边缘。理由是"文字该离边缘多远"属于风格问题，
  自己拍一个数字不如等真需要时做成显式设置；而且居中本来也没有边距，
  三档之间的规则因此是一致的。
- 能覆盖的范围：工程基线 + **行级**覆盖。元素级设置了会被校验器拒绝——
  对齐与字号/字体/纵向位置同属"整行排版"（ADR-0032 的契约）。
- 渲染侧只有**一处**改动：文本原点 `originX` 按对齐取 `0` / 居中 / `画布宽 − 文字宽`。
  文字度量与逐字裁切（"已经唱到哪"）全部基于 `originX`，所以对齐自动贯穿到高亮裁剪。
- schema v5（`V4ToV5Migration`）：**不改写数据**——旧文件没有该字段正是"还没设过"，
  等价于默认居中。读盘时遇到不认识的写法**不猜**：回退居中并报 `InvalidStyleValue`。

### 决定三：自动保存的落盘策略（`EDITOR-10` 最小版）

> 下面三条由主人选定；实现细节在实现提交里，这里只固定口径。

1. **备份位置**：已保存的工程写 `<项目目录>/backups/`（docs §6.1 就是这么定义的，
   备份不属于源数据、可删）；还没另存为的新工程写 `~/.local/state/MakoKaraGenerator/`
   （否则"没目录"就等于完全没有保护）。
2. **写备份时机**：每 30 秒轮询、**只在"自上次写完又改过"时**才写（`IsDirty` 是引用比较，
   没有变化的编辑不会产生新实例，因此这个判据天然可靠）；正常关闭窗口时再补写一份。
3. **恢复方式**：启动时发现备份比盘上的工程新 → 弹对话框问"恢复 / 丢弃"，
   由主人决定。**恢复只把内容装进内存（不写盘）**，要不要真的保存再由主人决定——
   禁止静默覆写用户文件。

**实现（已完成）**：

- 备份目录名带 UTC 时间戳（`autosave-20260925-184012Z`），于是<b>按名字排序即按时间排序</b>，
  不依赖文件系统 mtime（那玩意会被备份工具、同步盘、解压操作改掉）。
  写盘复用 `ProjectStore.Save`（原子替换 ✓），每次写完按保留份数剪掉最旧的。
- **"有改动"的判据是引用比较**（工程实例不是上次备份时那个）：这条之所以可靠，
  是因为编辑函数在没有实际变化时必须返回原实例（ADR-0025）。顺带避免了每 30 秒
  重写一份内容完全相同的备份。
- **需要"上次会话标记"**（`<状态目录>/last-session.json`）：启动时装载的是内置演示工程，
  应用并不知道上次开的是哪个工程；没有标记，"崩溃后重开能恢复"对已保存的工程就落不了地。
  标记里存的是**备份目录的绝对路径**，读的时候不必再推导一遍命名规则。
  标记损坏一律当作"没有"——它存在的唯一目的是帮用户找回工作，不该拦住启动。
- **恢复走 `ProjectWorkspace.Recover`**（`asSaved: false` + 清空撤销栈）：
  恢复出来的内容与盘上的文件不同，必须算"有未保存改动"，否则恢复完界面显示"已保存"，
  用户一关窗口就真的丢了。恢复出来的工程若自带音频路径，启动时**不再**导入演示音频
  （否则会把音频路径指到演示文件上），改走 `RestoreAudioFromProjectAsync`。
- 自动保存失败（磁盘满、无权限）**不弹窗、不改数据**：在状态栏说一句，编辑照常继续——
  它是兜底，不该因为自己出问题就打断用户。

#decision #editor #lyrics #style #persistence #autosave

---

## ADR-0036：背景媒体（图片背景、适配方式、资源收拢、视频预留）

**日期**：2026-09-25　**状态**：已实施

### 1. 范围：本轮只做图片背景，视频背景属 P1

`development.md` §4.1 的 P0 原文是"支持图片、纯色背景和视频背景中的**至少一种组合方式**"，
字面上纯色早已满足；但 `EXPORT-04` 的原文是"**图片**和纯色背景"，所以本轮把图片补上，
MVP 第 9 条因此由 ⚠️ 变 ✅。而"**背景视频完整支持**，以及背景裁切、缩放和定位"在
`development.md` line 176 被明确列为 **P1**，且它自带实时解码、循环/冻结语义、
seek 粒度与性能（依赖 `RENDER-05` 帧缓存）一整套，单独一轮做。

### 2. 数据形状：背景是"底色之上的一层"

- `Project.Background`（schema **v6**）= `BackgroundKind{Color,Image}` + `Path` + `BackgroundFit`。
- **`Composition.BackgroundColor` 不动**，它仍是画布的底色（永远铺满）。图片画在它**之上**，
  因此带透明度的图片会与底色叠加——这不是附赠功能，而是把两者分开的理由，
  也为"视频 + 底色"留出同一套结构。
- 适配三档：`Contain`（保持比例完整显示，留白是底色，**默认**）、`Cover`（铺满并裁掉超出部分）、
  `Original`（原始像素尺寸居中）。**刻意没有 stretch**：拉伸变形不是"适配"，是损坏画面。
- 几何只有一处实现：`Core/Media/BackgroundPlacement.Compute`（纯函数、无绘图库依赖）。
  三种方式统一成同一段算法——先按比例算出完整尺寸，再只保留画布可见部分并同步缩小源矩形。
  于是 `Cover` 的裁切是精确的，不靠裁剪区，缩放后的预览也不会出现接缝。
- schema v6 **不改写数据**（与 v2→v5 同理由）：旧文件没有背景字段正是"只有底色"，
  等价于加入该字段之前的行为。读到不认识的 `kind`/`fit` 时回退到默认并报
  `InvalidBackgroundSource` 警告——不认识的写法不该让整个工程打不开。

### 3. 资源收拢：保存时把背景图拷进工程包 `assets/`

- 只在**用户明确保存**时收拢（`ProjectWorkspace.SaveAs`）。**自动保存不收**：
  备份目录里再塞一份大图没有意义。（音频仍按 ADR-0029 只做相对化、不复制——
  音频动辄几十上百 MB，而图片小得多、"原图被挪走"却是更常见的坏法。）
- 收拢是**幂等**的：同名同内容就复用已有文件，同名不同内容才加内容哈希后缀，
  **绝不覆盖**。只按"文件已存在"判断会让每次保存都多出一个 `bg-xxxxxxxx.png`。
- 找不到源文件时**什么都不改**，只报告 `MissingBackgroundAsset` 警告——
  不静默把用户的引用丢掉（docs §19）。收拢结果（提示级）与缺失（警告级）都由界面说给用户听：
  "文件里悄悄多出一个路径"不可接受。
- 内存里的工程仍指向用户选的那个文件，收拢只影响写盘用的路径，
  因此不必在每次保存后改写编辑器持有的实例（避免撤销历史被清掉）。

### 4. 预览与导出自动一致，不需要额外接线

背景层画在**同一条管线**里（`SkiaRenderPipeline` 的 `Clear` 之后、歌词之前，画布坐标系），
而导出走的是同一个管线 + 同一份 `ProjectSnapshot.FromProject`（`VideoExporter` line 83），
所以"预览里看到的背景"与"MP4 里的背景"结构上不可能不一致（docs §3.2）。

### 5. 视频背景将来怎么接进来（预留接口清单）

本轮已经**把接口留好**，接视频时应只需要以下几处，**不改绘制代码**：

| 位置 | 要做的事 |
|---|---|
| `background.kind`（DTO/文件） | 增加 `video` 取值（**无需再升 schema**：不认识的值已经会回退并报告） |
| `BackgroundSource` | 若视频需要新参数（循环/冻结、起始偏移），在这里加字段（同样无需升版） |
| `Rendering.Skia` | 实现 `VideoBackgroundFrameSource : IBackgroundFrameSource`（`GetFrame(timeUs)` 里做 seek + 解码），并在 `IBackgroundFrameSourceResolver` 里按 `BackgroundKind` 分派 |
| 组合根 | 构造 `SkiaRenderPipeline` 时传入自己的解析器（构造参数已留） |
| 管线 | **不需要改**：每帧只问 `GetFrame(timeUs)`；预览与导出会一起获得视频背景 |
| 待决策 | `development.md` §12.1 要求"明确循环 / 冻结最后一帧 / 裁切 / 填充策略"——**开工前需先问**（roadmap §18.5） |
| 已知风险 | 实时解码在预览里可能跟不上（需要帧缓存或代理，`RENDER-05`）；seek 粒度与音频主时间线对齐 |

一个反面取舍：**不给 `BackgroundKind` 预占 `Video` 成员**。写一个没有实现的取值会让
"看起来支持但没实现"在类型层面合法，反而更容易漏掉处理分支；预留体现在上表的接口与分派点上，
而不是枚举里一个空壳。

#decision #background #schema #media #export #video

---

## ADR-0039：预览渲染帧缓存按快照引用失效

**日期**：2026-09-25　**状态**：已实施

预览侧使用 `CachedRenderPipeline` 包裹实际渲染管线，缓存键包含项目快照引用、时间、目标尺寸、
质量档位与透明背景标志。缓存容量有上限并按 LRU 淘汰，命中时返回像素副本，调用方仍按原有
约定负责释放 `RenderFrame`，不会把池化帧的生命周期泄漏到缓存中。

编辑产生新的 `ProjectSnapshot` 实例时清空旧快照缓存；因此缓存不会因项目内容变化而复用旧画面，
也不需要对歌词和样式做逐字段哈希。导出仍使用独立的未缓存管线，避免后台导出帧占用预览缓存。

#decision #rendering #cache #preview

---

## ADR-0040：移除独立的当前元素颜色

**日期**：2026-09-25　**状态**：已实施

预览中的“当前元素颜色”把时间播放状态误当成了用户关心的样式状态。用户真正需要编辑的是
未唱与已唱两种外观；当前元素的未唱部分继续显示未唱样式，已唱部分继续显示已唱样式，进度
边界本身已经足以表达正在演唱到哪里。

因此从 `LyricStyle`、`LyricStyleOverride`、样式属性枚举、检查器和 Skia 绘制中移除当前颜色及
其两组渐变字段。时间轴仍保留“播放中的元素”与“用户选中元素”两种视觉标记，但它们属于编辑
辅助状态，不属于字幕输出样式。

这是持久化结构变更：`project.json` 从 schema v6 升到 v7，`lyrics.json` 从 v3 升到 v4。迁移
会删除旧文件中的 `currentColor`、`currentGradientStartColor` 和 `currentGradientEndColor`，
其他颜色与覆盖保持不变。新文件不再写出这些字段。

#decision #style #rendering #editor #schema

---

## ADR-0037：透明输出（EXPORT-07、SPIKE-06 的实测结论）

**日期**：2026-09-25　**状态**：已实施

### 1. SPIKE-06：Alpha 路径是**实测**出来的，不是查文档得来的

用 5 帧 RGBA 原始帧（四周全透明 + 中间一块红方块，左半 `alpha=255`、右半 `alpha=128`）
逐条试编码路径，然后**把产物解码回 RGBA 读像素**——只有读回来的 Alpha 才算是证据：

| 路径 | 实际 pix_fmt | 解回来的像素 | 结论 |
|---|---|---|---|
| **ProRes 4444**（`prores_ks -profile:v 4444 -pix_fmt yuva444p10le`） | `yuva444p12le` | 角 `(0,0,0,0)`、半透明 `(255,0,0,129)` | ✅ **首期采用** |
| QuickTime 动画（`qtrle -pix_fmt argb`） | `argb` | 半透明 `(255,0,0,128)` | ✅ 可用（无损、体积大） |
| FFV1（`.mkv`，`yuva444p`） | `yuva444p` | 半透明 `(255,0,0,129)` | ✅ 可用 |
| PNG（mov，`rgba`） | `rgba` | 半透明 `(255,0,0,128)` | ✅ 可用 |
| **VP9 / webm**（`yuva420p`，另试 `-auto-alt-ref 0`、`alpha_mode=1` 元数据） | **`yuv420p`** | 半透明变成 `(253,0,0,255)` | ❌ **静默丢 Alpha** |
| H.264（对照） | `yuv420p` | `(253,0,0,255)` | ❌ 预期如此 |

**webm 那三行是这次探测最重要的产出**：三种参数组合都"成功导出"，扩展名、容器都正常，
只有把像素读回来才看出透明度没了。这正是 docs §12.3 那句"不能仅根据文件扩展名判断成功"
的具体形态。**结论：webm 这一档干脆不上架**——宁可少一个选项，也不要给出一个
"看起来透明、其实不透明"的产物。

### 2. 触发方式：导出选项，不是工程数据

导出对话框新增「输出类型」：不透明成片（MP4）／透明歌词视频（ProRes 4444）。
它是**导出选项**（`VideoExportOptions.Format` + `RenderRequest.TransparentBackground`），
工程里一个字段都不用改——同一次编辑可以两种格式各导一份。
扩展名跟着输出类型走（`.mp4`／`.mov`），切换时只改扩展名，路径与文件名一个字都不碰。

### 3. 透明输出 = 背景色与背景图**都不画**

画布从全透明开始，并且跳过背景层。理由：这一帧的用途是给后期合成当叠加层，
任何底色都会让它失去意义。对话框里明说这一点（`ExportFormatTransparentHint`），
免得用户以为是产物坏了。歌词样式照常生效，只是它们变成带 Alpha 的像素。

### 4. 预乘 vs 直乘：一处真实的颜色错误

渲染管线产出的是**预乘** BGRA（ADR-0021），而 ffmpeg 的 `bgra` 指的是**直乘**。
全不透明时两者数值相同，所以不透明成片一直没有问题；但透明输出里半透明像素占多数，
直接喂预乘数据会把颜色**二次压暗**（50% 的红会变成 `(128,0,0,128)`，合成后是暗红）。
因此编码器写管道前做一次反预乘（`AlphaFrameConverter`），只处理 `0 < alpha < 255` 的像素
（Alpha 为 0 时颜色必然是 0，为 255 时除法是恒等变换）。换算放在编码器那一步是因为
它本来就要拷一遍像素，顺手做不额外花钱；不透明路径按格式分支跳过，一格都不多花。

### 5. 验收标准：探测产物的像素格式，并把结论写进报告

docst §12.3 要求"导出前/后验证 Alpha 能力"。落法是：
导出后用既有的 `IMediaProbe` 读出 `pix_fmt`（本轮给 `MediaStreamInfo.PixelFormat` 补上了这个字段），
再按 `HasAlphaPixelFormat` 判断（`yuva*`、RGBA 四种排列、`gbrap`、`pal8`、`ya8`），
结论作为**提示**进 `ExportReport`：

- 通过 → "透明输出已确认：产物像素格式 yuva444p12le，带 Alpha 通道。"
- 不通过 → "透明输出没有生效：产物像素格式是 yuv420p，不含 Alpha 通道。"

不做"编码器能力预检"（跑 `ffmpeg -h encoder=…`）：显式写了 `-pix_fmt yuva444p10le` 之后，
像素格式不受支持时 ffmpeg 会**直接报错失败**（不是静默降级），而真正危险的那一类
"静默丢 Alpha"只能由产物探测抓到。多跑一个进程换不来更多保障，反而多一处可能误判的地方。

### 6. 附带修正：奇数尺寸的限制是"格式专属"的

yuv420p（不透明 MP4）要求宽高为偶数，ProRes 4444 不需要。原来这条校验写在编码器入口、
对所有格式一视同仁，等于拿一种格式的限制去挡另一种。现在按格式分别要求，
透明档可以正常输出 1080p 之外的奇数尺寸。

### 7. 透明输出带音轨（PCM）

MOV 里放 `pcm_s16le` 而不是复用 AAC/复制：产物常要进剪辑软件继续加工，
那里对 PCM 的支持比 AAC 更稳；而 ProRes 4444 的体积本来就不在意这点音频。
`-shortest` 等既有规则不变。

#decision #export #alpha #video #ffmpeg #spike #lesson

---

## ADR-0038：元素级覆盖的界面作用范围（P1 批次①）

**日期**：2026-09-25　**状态**：已实施

### 1. 缺的从来不是模型，而是"谁来写它"

模型（`LyricToken.StyleOverride`）、渲染（按元素裁切绘制，ADR-0032）、持久化与校验
早就支持元素级覆盖；缺的只有界面入口——检查器的作用范围下拉只有「工程 / 行」两档。
因此这一批**没有新增数据字段、没有升 schema**：只是把已有的那一层接到界面上，
外加一个领域层写入口（`LyricStyleEditor.SetTokenOverride` / `UpdateTokenOverride` /
`ClearTokenOverride`，与行级三件套一一对应，规矩也一样：空覆盖收敛成 `null`、
没有实际变化返回原实例）。

### 2. 一份事实：`TokenStyleSupport`

"哪些属性能用在元素级"同时被两处需要：界面（要说明白为什么不给改）与校验器（要拒绝非法文件）。
两边各写一份清单，迟早会出现"界面允许改、一保存就报错"的组合，所以清单只在
`Core/Projects/TokenStyleSupport.cs` 里维护一份，界面据此挡、校验器据此拒，
`TokenStyleOverrideTests.支持范围与校验器一致` 把两者钉在一起。

不支持的五项：`fontFamilyName`、`fontSizePx`、`verticalCenterRatio`、`horizontalAlignment`
（整行排版：整行只整形一次、整行一起移动）与 `scale`（整体变换：靠画布变换实现，
元素级套上去会误缩放整行）。

### 3. 作用范围只决定"写哪一层"

11 处样式入口（字号、字体、颜色、描边、阴影、不透明度、缩放、对齐…）原本各自写着
`if (作用范围 == 行) 写行覆盖 else 写基线`。这一批把分派收成一个中转
`EditScopedOverride(update)`：元素档转给 `EditTokenOverride`，其余走行覆盖；
"只写这一项、其余继续继承"的语义在所有层里完全一致。

⚠️ 踩到的坑（已记进 AGENTS §7）：只把**条件**从"== 行"改成"!= 工程"，**忘记改调用**——
于是元素档的改动静默写进了**行**覆盖，界面看着像生效了，其实改错了层。
分派必须跟调用一起改，或者干脆只留一个调用点。

### 4. 不支持的属性：拒绝 + 说明，不静默

在元素档改字号/缩放这类属性时，写入前就用 `TokenStyleSupport.UnsupportedSet` 拦下，
并把原因写进来源说明（"元素级不支持「字号」：它是整行排版或整体变换…"）。
理由：让它写进工程只会得到"存盘即报错"——那不是"改了没效果"，而是把用户的文件弄成非法状态。
（"非法输入不提交只报错"这条底线在 UI 层的形态。）

### 5. 下标失效按"没有覆盖"处理

元素被重新拆分、或切换选中行之后，界面上的芯片可能还指着已经不存在的下标。
这时读路径返回 `null`（当作没有覆盖），写路径由 `LyricStyleEditor` 的下标检查原样返回工程
——**绝不凭空造一个元素出来**。

#decision #style #editor #p1 #lesson

**追记（2026-09-25 真机验收）**：主人验收时报"对单个元素的修改（例如字号、颜色）并没有生效"。

先如实记下结论与范围：

- **字号不生效是设计如此**（元素级不支持整行排版类属性，见本文第 2 节），
  但当时的做法只是把说明写进检查器的"来源说明"文本里——它不醒目，
  用户看到的是"改了没反应"。**这说明"拒绝"的表达方式不够好**，而不只是提示位置问题。
- **颜色不生效是真问题**：领域层（`LyricStyleEditor`）与视图模型层的测试全绿，
  但它们只能证明"覆盖被写进了模型"，证明不了"渲染把它画了出来"。
  批次① 恰恰**没有在渲染层加像素断言**——ADR-0032 声称元素级可按元素裁切绘制，
  但那句话本身没有为"元素级颜色覆盖"写过测试。
- 因此本文第 1 节"模型/渲染早就支持元素级覆盖"这一前提**需要重新验证**：
  修复时第一步应当是补一条像素测试（给某个元素设覆盖色 → 渲染 → 断言只有那个元素的像素变了），
  再用它区分"渲染没读覆盖"与"界面没写进去 / 没重绘"。

**这条偏差的教训**：把"某层支持"当成事实写进文档之前，要在那一层留下断言。
没有断言的那一层，"支持"只是推测。

**修复（2026-09-25）**：渲染层像素测试已存在并确认元素颜色覆盖只影响目标元素；同时修复了
检查器元素作用范围仍读取工程基线、选中元素或提交覆盖后没有刷新生效样式的问题。现在颜色覆盖
会写入 `LyricToken.StyleOverride`，检查器显示工程→行→元素的合成结果；字号等不支持属性仍会
按第 2、4 节拒绝并提示。视图模型回归测试覆盖了元素覆盖写入层级与生效样式读取。

---

## ADR-0041：背景视频（时间策略、分层与定位取舍）

**日期**：2026-09-25　**状态**：已实施

### 1. 范围

`development.md` line 176 的"背景视频完整支持，以及背景裁切、缩放和定位"（P1）。
裁切/缩放/定位在 ADR-0036 已经做好（图片与视频共用同一套 `BackgroundFit` 与
`BackgroundPlacement`），这一轮补上的是"视频本身"：解码、时间策略、资源收拢、界面入口。

### 2. 时间策略：可切换、默认循环

`development.md` §12.1 要求"明确循环 / 冻结最后一帧 / 裁切 / 填充策略，以及视频短于/长于音频时的行为"。
裁决（主人 2026-09-25）：

| 情形 | 行为 |
|---|---|
| 视频**短于**音频 | 默认**循环**播放；另一档可选"停在最后一帧"（`BackgroundPlaybackMode`） |
| 视频**长于**输出时长 | 多余部分不被使用（渲染只走到输出时长），**不需要**单独的"裁切"策略 |
| 视频自带音轨 | 忽略：音频时间轴以工程导入的音频为准 |
| 音频时间轴为 0 之前 | 取第一帧（`BackgroundPlayback.ResolveSourceTimeUs`） |

映射是**一处纯函数**（`Core/Media/BackgroundPlayback`），预览与导出都走它；
测试盯住循环取模、冻结夹到最后一微秒、时长未知与非正时刻取第一帧这些边界。

### 3. 分层：抽象在 Abstractions，实现在 Media

ADR-0036 第 5 节原计划的接法是"在 `Rendering.Skia` 里实现 `VideoBackgroundFrameSource`
做 seek + 解码"。落地时改成：

| 层 | 内容 |
|---|---|
| `Rendering.Abstractions` | `VideoFrame`（裸 BGRA8888、直乘 alpha）、`IVideoFrameSource`、`IVideoFrameProvider` |
| `Media` | `FfmpegVideoFrameProvider`（FFprobe 探测 + 常驻 FFmpeg 解码进程），新增一条 `Media → Rendering.Abstractions` 引用边 |
| `Rendering.Skia` | `VideoBackgroundFrameSource`（裸帧 → Skia 位图）、`BackgroundFrameSourceResolver` 按种类分派（因此改为 **public**，组合根要自己造它来注入提供者） |
| 管线 | **绘制代码只改了一处**：见下面第 5 节的坑 |

理由：FFmpeg 命令行属于"外部运行时"，渲染层不该认识它（ADR-0025 的依赖方向）。
代价是抽象层多了一组类型；收益是换渲染后端时视频支持跟着走，而 FFmpeg 只在一个地方。

### 4. 实现取舍（写进代码注释，也记在这里）

- **常驻进程 + 顺序读帧**：预览与导出都是时间向前走，一次启动成本换来整段流畅；
  只有"往回跳"或"往前跳超过 30 帧"才用输入侧 `-ss` 重新定位。
- **同步探测**：渲染线程是同步的，`Open` 用 `Process.WaitForExit(10s)` 而不是 async——
  在 UI 线程上同步等 async 有死锁风险。
- **帧缓冲复用**：约定"内容在下一次取帧后失效"，30 fps 下不新建几十 MB 的数组。
- **加锁串行化**：解帧是有状态的（一个进程 + 顺序读），预览与导出若共用同一个来源，
  正确性优先于吞吐。
- **已知偏差**：帧率按常帧率处理（变帧率视频的帧号略偏），定位精度是"目标时刻或其后最近的一帧"。
  两者都只影响一帧以内的画面。
- **资源收拢**：视频与图片一样在用户保存时收进 `<工程目录>/assets/`（主人拍板），
  因此工程可整包搬走；代价是磁盘占第二份，所以复制后的提示里**报出体积**。
- **schema v8**：`BackgroundDto` 新增 `playback`；`V7ToV8Migration` 不改写数据（缺省即循环）。

### 5. 抓到的真错：`HasImage` 挡住了视频

时间策略、分派、位图搬运都写完之后，渲染测试仍然全红：画面只剩背景色。
真因是 `SkiaRenderPipeline.DrawBackground` 的早退条件写的是 `!background.HasImage`
——那是"还没有视频"的年代留下的。视频背景会因此被**整个跳过，而且不报错**。

这正是 ADR-0036 特意不给 `BackgroundKind` 预占 `Video` 成员的理由：空壳取值会让
"漏了处理分支"在类型层面合法。教训也再次成立——**加一个新的种类取值时，
要把所有 `HasXxx` 之类的早退条件逐个过一遍**，它们不会因为类型变多而自动变宽。

另一个坑：FFprobe 的 JSON 里宽高是**数字**、时长是**字符串**、帧率是 `"10/1"` 分数。
只按字符串读会让宽高静默变成 0，表现为"这个视频里没有画面"。

#decision #background #video #schema #media #rendering #lesson

---

## ADR-0042：关键帧与缓动（最小版「行进入动画」）

**日期**：2026-09-25　**状态**：已实施

### 1. 范围：不先做通用关键帧表

`development.md` §11 的最终形态是"关键帧曲线 + 内置进入/持续/退出动画"。
这一轮只做**最小版行进入动画**（主人 2026-09-25 拍板），理由：先用一种真实需求
（"整首歌的行统一入场、个别行例外"）把数据模型、时间语义、渲染与界面走通；
通用关键帧表是它的超集，等有了具体需求再长出来，避免先造一个没人用的曲线编辑器。

### 2. 口径

| 项 | 决定 |
|---|---|
| 通道 | 不透明度 / 缩放 / 纵向偏移 |
| 缓动 | 线性、缓入、缓出、缓入出（二次曲线，公式用测试钉住） |
| 时间 | **从行起点起算**；时长走完即回到行自己的样式，因此**不需要结束值字段**，也不需要关键帧表 |
| 数据归属 | **工程级默认 + 行级覆盖**；行级 `null` = 跟随工程默认 |
| 参数 | 时长 + 缓动 + 三个起始值（结束值恒为"行自己的样式"） |
| 启用方式 | **显式**：两层都为 `null` 时不动画，老工程迁移后画面不变 |
| 作用范围 | 只有工程与行两档，**没有元素级**：逐字各自的入场会与逐字时间语义打架 |

### 3. 边界（都有测试）

- 行开始之前：停在**起始值**（动画第一帧），而不是"从零开始往回算"。
- 时长走完，或时长 ≤ 0：取中性值 `(1, 1, 0)`。
- 行没有时间（`Time is null`）：起点按 0 处理，不抛异常。
- 越界取值（时长 0~10s、起始不透明度 0~1、起始缩放 (0,4]、偏移 ±10000px）
  在打开/导入时报 `InvalidLineAnimation`：这类错误在界面上不容易看出是自己填错了。

### 4. 实现要点

- 动画语义**只有一处实现**：`LineAnimationEvaluator`（纯函数），预览与导出都走它。
  `LineAnimationEditor.Resolve` 有两个重载（给 `Project` 或给"工程默认值 + 行"），
  因为渲染器只有快照、没有工程对象；继承规则仍然只有一处。
- 渲染：不透明度用 **`SaveLayer` + 白色 alpha 图层**，对渐变、描边、阴影一视同仁——
  逐个颜色去乘 alpha 必然会漏掉某条绘制路径，而漏掉的那条只在半透明时才看得出来；
  位移与缩放走画布变换，且作用在"行自己的样式缩放"之外。
  中性动画或没有动画时**不进图层**，不给正常预览加开销。
- schema：工程 **v9** / 歌词 **v5**，两个迁移都**不改写数据**（缺省即"不做动画"）。
- 界面：检查器「进入动画」区块（启用 / 作用 / 时长 / 缓动 / 三个起始值），
  在**本行**范围取消勾选＝回到跟随工程默认，在工程范围取消＝整首不做动画：
  两者都是"清掉这一层的显式设置"，不替用户改别层的值。

### 5. 已知取舍

- 缓动只有四条二次曲线，没有自定义贝塞尔；"持续/退出动画"与逐字动画都还没做。
- 缩放与偏移的轴是**行中心**，没有可配置锚点。
- 动画在行结束后不再参与（没有出场效果），与"最小版"的范围一致。

#decision #style #animation #schema #rendering #editor

---

## ADR-0043：样式系统的单一事实，以及脚本效果层的接缝（预留）

**日期**：2026-09-26　**状态**：已实施（前两节）／待定（第三节的脚本接口）

### 1. 背景：一次 review 发现的"同一件事实写了三遍"

主人验收动画后提出"四种缓动看起来一样"，顺带要求 review 单行-元素样式系统的构成。
结论：**类型与依赖层面没有错误耦合**（分层单向、合并只有 `LyricStyleResolver` 一处、
写入口只有 `LyricStyleEditor`、来源可追溯），但"元素级能不能用某个属性"这件事被表述了三遍：

| 表述 | 位置 | 是否被测试钉住 |
|---|---|---|
| `UnsupportedAtTokenLevel` | `TokenStyleSupport` | ✅ |
| 属性名字符串清单 | `ProjectValidator` | ✅ 与①对齐 |
| `HasRenderableOverride` | `SkiaRenderPipeline`（私有） | ❌ **无人看守** |

③ 最危险：①放开一项而③忘了加，结果是"界面允许设、数据也存了、**画面上什么都没有**"——
不报错、不提示。本仓库已经有过两次同类体验（元素级覆盖"改了没反应"、行级动画静默丢失）。
至于缓动观感，**与耦合无关**：三条通道里缩放默认不变、偏移只有 12px，差异全落在"淡入"上，
而淡入对缓动最不敏感（幅度加大到 60px／1.2 秒后四种缓动立刻可辨）。

### 2. 改法：一份事实 + 派生

- `TokenStyleSupport.SetProperties` 是"覆盖里有什么"的**唯一**入口（一份 18 项属性→字段映射）。
- `UnsupportedSet` = 它的子集；`HasDrawableOverride`（渲染是否为该元素重画）= 它的另一派生；
  ⇒ 渲染器那份私有清单**整块删除**，"支持却画不出来"在结构上不再可能。
- 校验器不再自己列字符串表，改用 `UnsupportedSet` + `LyricStylePropertyExtensions.ToJsonName`
  （属性→JSON 字段名的唯一映射，诊断信息也用它）。
- 新增闸门测试 `TokenStyleSupportTests`：遍历 `LyricStylePropertyExtensions.All`，
  要求每一项都能被这一层看见、都让覆盖不再为空、支持与不支持互斥且合计等于全部、
  "渲染是否重画"与支持范围完全一致。**变异检查**：把 `SetProperties` 里一项注释掉 ⇒ 3 条用例变红。

**这条经验的普适形式**：新加一个字段/属性时，要在"模型、映射、渲染、诊断"每一处都问一遍
"这里需要知道它吗"；能派生的地方就派生，不能派生（必须逐项列举）的地方**必须配一条
遍历全部成员的测试**，否则漏掉的那一处永远是静默的。

### 3. 界面层：两套"作用范围"合并成一套

样式有 `Project/Line/Token` 三档，动画有 `Project/Line` 两档，原来各自实现（两个字段、两套判定）。
现在：

- 一个枚举 `EditScope`（样式与动画共用类型，两组下拉仍然独立）。
- 一个判定点 `TryResolveEditTarget(scope, tokenSupported, …)`：唯一一处判断
  "选中行/选中元素是否存在、下标是否还有效、这一组改动支不支持元素级"。
- **契约**：不支持的组合**拒绝并说明**（`EditRefusal` → 各自的文案），
  **绝不悄悄落到别的层上**——那是替用户改了别的东西。测试钉住了这条
  （动画的范围被设成元素时，工程与行都不会被改动）。
- 视图模型里那两处"每属性一条 setter"的机械重复**仍然保留**：它没有错，只是增长成本；
  等下一个样式属性到来时再抽"属性描述表"，现在重构的收益小于风险。

### 4. 脚本效果层的接缝（只梳理，未实现）

`development.md` 的管线图里，"脚本效果层（粒子、路径、外部动画对象）"位于
**样式与逐字绘制之后、交给编码器之前**。本轮把它需要遵守的契约写清楚，**不加任何接口**：

| 契约 | 内容 |
|---|---|
| 输入 | 项目快照 + 时刻。与预览/导出同一份（ADR-0006），因此"预览与成片一致"对脚本同样成立 |
| 输出 | 只往画布上画；**不得修改工程数据**（渲染器不得改项目源数据是既有铁律） |
| 可复用 | 样式解析 `LyricStyleResolver` 与动画求值 `LineAnimationEvaluator` 都是纯函数，脚本可直接用同一份结果，不必自己重算 |
| 失败 | 脚本出错不得崩 UI，也不得让导出产出半截文件（`SPIKE-07` 的目标之一） |
| 挂点 | 渲染管线里"歌词画完、`canvas.Restore()` 之前"这一段；组合根负责注入 |

**为什么现在不加接口**：脚本的**进程模型**（主进程 vs 独立进程）是 `roadmap.md` §18.5 里
待主人拍板的事项；接口形状与进程模型强相关，先定形状几乎必然返工。
本喵建议的顺序是：`SPIKE-07`（Roslyn 编译/加载/卸载最小效果类）跑通后再定 API，
届时上面的契约表就是它的输入要求。

#decision #style #refactor #scripting #robustness

---

## ADR-0044：样式设计面板与批量应用（含可选颜色三态）

**日期**：2026-09-26　**状态**：已实施（引擎侧、面板侧、统一样式工作区、弃用标记）；小窗口与真机验收见"待收尾"

### 1. 要解决的问题

原样式入口把"设计"和"应用"绑死：控件直接调 setter、按当前作用范围立刻写进工程，
改错了要走撤销；预设还固定写工程基线（选了预设会误改整首）。主人要求：
**沿用现有 UI 样式，先在一个独立面板里设计，再批量应用到元素 / 行 / 工程。**

### 2. 面板形态（已实施）

- 右侧栏改为两个**平级页签**：**样式设计**（设计稿来源、试样、参数、进入动画、应用）
  与**属性**（当前对象的生效样式与只读来源、合成与背景、诊断）。
  页签只切换工具，**不再有"当前样式作用范围"下拉**——三个应用按钮把目的写清楚。
- "独立"首期指**独立视图 + 独立视图模型 + 独立草稿状态**，停靠右侧；不做模态、不做浮动停靠系统。
- 试样用**独立快照 + 独立渲染管线实例**，可看未唱 / 进行中 / 已唱与动画进度，
  无音频无歌词也能用内置样例；试样文字与进度**不写进工程**。
- 主画布始终显示**已提交**的工程，不把试样当成最终效果。

### 3. 草稿语义（已实施）

- 普通输入**只改草稿**：工程引用、脏标记、自动保存内容、撤销栈、主画布都不动。
- 草稿是**编辑会话状态**：切换页签、播放、切换选择都保留；面板收起不丢；
  工程保存不保存草稿；界面常驻说明"设计稿仅在本次会话保留喵"。
- 首期**只有一份会话草稿**：不建可命名样式库，不引入 `styles.json` 或动态样式引用。
- 应用是**一条**工程命令（一次 `Execute` → `AdoptProject`）：整批生效或整批不生效，
  撤销一步回到应用前；连续两次应用是两步；无实际变化不新增历史、不污染脏标记。

### 4. 可选颜色三态（已实施，**数据格式变更**）

6 个可选颜色（4 个渐变起止色 + 描边色 + 阴影色）由"`null` 即继承"升级为三态：

| 状态 | 语义 | 由谁产生 |
|---|---|---|
| 未设置 | 继承上层 | "恢复继承" |
| 设为颜色 | 覆盖上层 | 应用设计稿 |
| **明确设置为空** | **本层关闭该效果** | 应用设计稿中的"关闭"状态 |

- 复用 `OptionalValue<RgbaColor?>` 表达，**不新增样式层级**；解析、来源、`IsEmpty`、
  `TokenStyleSupport.SetProperties`、校验、DTO、渲染判断全部同步适配。
- JSON 区分"属性缺失（继承）"与"显式 `null`（关闭）"：DTO 用 `JsonElement` +
  `WhenWritingDefault`，**序列化器不能省略显式 null**。
- schema：`project.json` **v9→v10**、`lyrics.json` **v5→v6**，两条线各自登记一条
  `OptionalColorMigration`；旧文件里这些覆盖字段的**缺失或 null 都迁移为"未设置"**，
  因此老工程渲染结果不变。基线的可空颜色语义不变（不含三态）。
- **主人拍板的渲染口径**：关闭渐变 / 描边 / 阴影 ⇒ **回退到最简单的纯色效果**
  （用该层已解析出的纯色画字，不再画渐变、描边、阴影）。

### 5. 批量应用（引擎侧，已实施）

`StyleBatchEditor.Apply(project, level, targets, style, properties, includeAnimation, animation, restoreInheritance)`：

- 层级 `Project` / `Line` / `Token` **一次只写一层**（避免父子同时覆盖导致的叠罗汉）。
- 逐属性写入（默认**合并**：只替换勾选属性在目标层的值，其余覆盖保留）；
  "全部静态样式"是显式的**全量固化**（这些属性从此不再跟随上层）。
- `restoreInheritance` **只清选定的那些属性**，不动其他属性、不递归清空子层；
  工程层不允许"恢复继承"。
- 目标**去重**；下标越界、元素不支持项、元素上带动画 ⇒ **整批拒绝**（`ArgumentException` /
  `ArgumentOutOfRangeException`），不做部分成功；无实际变化**返回原实例**。
- ⚠️ 实施中抓到的真 bug：`CopyProperties` 的"恢复继承"分支原为
  `restoreInheritance ? default : style.X`——`style.X` 是**非空**类型，三元表达式因此把
  `default` 推断成 `0f` / 默认色，于是"恢复继承"变成"覆盖成 0"（18 个属性里 11 个中招）。
  遍历 `LyricStylePropertyExtensions.All` 的用例抓到；已改为显式 `null` / `default(T)`。

### 6. 渲染：每个区域只画一次最终样式（已实施）

- 元素级覆盖**不再"先铺整行、再盖元素"**（那会留下旧描边/阴影，也会把半透明元素与基线
  重复叠加）：整行那一遍把有覆盖的元素**排除**，元素那一遍按自己的样式补上。
- 两遍用的**是同一个矩形**，且**含装饰外扩量**（描边半宽、阴影偏移与模糊的较大者）。
  ⚠️ 只按字框排除时，整行那一遍仍会把元素的描边**外沿**画出来——元素即使明确关闭了描边
  也擦不掉（实测表现为字框边界一列基线颜色）。两条像素用例正因此变红。
- 卡拉 OK 进度那条边界不参与"排除"（它是"已唱到哪"，继续按自己的方式画）。

### 7. 已知取舍与待收尾

**交互重排（2026-09-26，主人确认）**：原歌词栏「样式选择」入口已并入右侧样式工作区。
工作区分「样式库／草稿编辑」两个视图，样式库再分「内置／我的」；内置只读，个人样式可更新、
重命名和删除。草稿可直接应用到工程默认、当前行或当前元素，提交区在应用前显示目标、属性集合与
元素不支持项；按行分组的目标选择器支持多目标、全选和清空。歌词栏保留当前对象样式摘要与打开工作区快捷入口。
库项的编辑／更新只改应用级样式库，应用按钮才写工程；已应用的工程不与库项建立动态链接。

- **交互收尾**：小窗口（最小 940×640）与键盘、窗口管理器真机验收已完成；新增参数交互仍待验收，
  "恢复继承"还要进一步收进统一应用区。
  文档整备**已完成**（2026-09-26：`development.md` §8.1/§21.1、`timeline-ui.md` §4.1/§6、
  `roadmap.md` §18 抬头与 §2.2、README 文档表；外观口径见 ADR-0045）。
- 首期不做：可命名样式库 / 时间轴多选拖动 / 草稿撤销栈 / "单行禁用上层动画"的新契约。
- 旧样式即时写入路径**暂时保留并标记弃用**（`EditorViewModel.Style.cs`，22 个成员
  `[Obsolete]`，旧用例仍在跑）；主人口径：**新路径出问题再回头用**，稳定后再连同旧用例删除。
- 批量清单绑定当前工程与结构版本：行增删/重排、元素拆合、歌词导入、结构撤销会清空失效目标
  并提示；**首期不为此新增持久化 ID**。

#decision #style #schema #rendering #ui

---

## ADR-0045：界面外观改为 Material You（保留 Semi 控件模板）

- **状态**：已接受
- **日期**：2026-09-26
- **阶段**：阶段 5 之后的界面完善（不改时间模型、数据格式与渲染结果）

### 背景

`docs/development.md` §2.1 与 §21.1 建议首期使用 `Avalonia.Themes.Fluent`；
ADR-0001 改为以 **`Semi.Avalonia` 12.1.0.1** 作为 GUI 主题基线（Semi Design 取向、观感接近 Ant Design）。

2026-09-26，用户**直接指定**界面外观改为**蓝色 Material You 风格的扁平界面**。
该变更先记在 `AGENTS.md` §3.3（记为"用户直接指定的例外"），并注明"后续整理 ADR 时应同步该口径"，
本条即该同步。

### 决策

1. **外观取向改为 Material You（深色蓝调）**：`RequestedThemeVariant="Dark"` 保持不变；
   控件形状、密度与状态由 `samples/TechnicalSpike.App/Theme/MaterialYou.axaml` 覆盖，
   色阶由 `Theme/Colors.axaml` 提供，深／浅两套仍是各自独立的字典（两边都要加键）。
2. **控件模板仍由 `Semi.Avalonia` 12.1.0.1 提供**：`App.axaml` 里的 `SemiTheme Locale="zh-CN"` 保留，
   本次只覆盖**资源层外观**，**不是**换成 `Material.Avalonia` 包——该包在 ADR-0001 中已因
   "Material 密度低、行高松，不适合高密度时间轴/打轴界面"被否，那条结论继续成立。
3. **强调色留运行时可调用入口**：`Theme/UiAccentPalette` 暴露 `Apply(Color seed)`（就地替换应用资源）
   与 `CreateOverrides(Color seed)`（返回覆盖字典，便于测试）。本轮**只留入口**：
   不新增持久化设置、不改 `project.json` / `lyrics.json`。将来接设置界面时在应用初始化后调用；
   界面颜色不得写进工程文件，渲染也不得读取界面色阶。
4. **界面色阶与视频渲染解耦**：渲染与导出（`Rendering.*`、`Export`）不引用界面资源；
   时间轴与画布仍是自定义绘制，主题只影响外围控件（与 ADR-0001 一致）。
5. **版本锁定不变**：Avalonia 12.1.3、Semi.Avalonia 12.1.0.1 等仍按 ADR-0002 与
   `Directory.Packages.props` 锁定，不因换外观而升级组件。

### 取代范围

- 被取代的只有 ADR-0001 的**外观取向**表述。ADR-0001 的其余约定**继续有效**：
  颜色不得在 XAML 中硬编码、必须定义在本项目资源字典并以 `ThemeVariant` 区分深浅；
  面向用户的文案必须资源化（`Resources/Strings.axaml`）；`SemiTheme` 以 `Locale="zh-CN"` 载入。
- 因此 ADR-0001 状态记为 `已取代（仅外观取向部分）`；决策记录仍不改旧条目正文，只追加本条。

### 影响

- `docs/development.md` §21.1 的组件表、`docs/roadmap.md` §2.2 的首期技术栈清单，已按本条就地标注。
- **强调色的持久化**后来由 ADR-0048 收口：只写应用级 XDG 配置目录下的 `theme.json`，
  不进入工程文件，也不影响渲染结果。
- 真机验收补充项：浅色主题与最小窗口 940×640 下的可读性（深色为默认）。

#decision #ui #theme

---

## ADR-0046：歌词格式识别、更多导入格式，以及切分的单一实现

- **状态**：已接受
- **日期**：2026-09-26
- **阶段**：阶段 5 之后的导入能力扩展（不改时间模型与渲染结果）

### 背景

普通 LRC 与增强 LRC 已能导入，“按扩展名 + 文件头识别”的骨架也已在
（`ILyricsImporter.CanImport` + `LyricsImporterRegistry`）。缺口是：格式覆盖、识别结果对用户不可见、
切分只有“选中芯片按 S”一条窄路。另发现一处语义瑕疵：拆分把“按文本比例推导的时间”标成 `Edited`，
而该枚举对 `Estimated` 的定义正是“按字符权重分配”。

### 决策

1. **识别固定成三层**：①扩展名登记表 `LyricsFormatCatalog` 给出候选（`.lrc` → 增强 → 普通）；
   ②候选各自用 `CanImport` 看内容；③候选都不认或扩展名未登记时，**退回“所有导入器都试一遍”**，
   不因为多了目录而变脆。选中的理由与“还有哪些候选”都写进报告（`ILyricsImporter.MatchReason`）。
   **不许静默降级**：把逐字当行级读、丢掉格式特性，都必须进报告。
2. **首期格式**：P0 = 增强 LRC、普通 LRC（已有）+ **SRT** + **WebVTT**（cue 内 `<00:00.000>` 按逐字读）；
   P1 = **ASS/SSA**（`\k` 家族，厘秒）、**UltraStar**（`#BPM`/`#GAP` 把拍换秒）、
   **TTML**（`XmlReader` 流式，首期只读第一条轨道）。
   **不做** YRC/QRC/KRC（私有 + 加密，维护与法律边界都不划算）；音高、翻译轨、和声、样式标签一律进“丢弃项”。
3. **解析与建模分层**：新格式共用 `ParsedLyricsImporterBase` —— 解析器只产出中间表示，
   行区间推导、标识分配、“元素文本拼接 == 行文本”的校验与报告计数集中在一处
   （LRC 家族仍走 `LrcImporterBase`；两条骨架的口径以测试钉住）。
4. **切分只有一份实现**：`TokenSplitRule` → `LyricTokenSplitter`（只在 Unicode 文本元素边界切，
   **拼接恒等于原文**）→ `LyricLineTokenSplitter`（可按规则或按手工边界切）→
   `LyricsDocumentTokenSplitter`（文档级、可按行筛选）。导入后按规则切分也走这条通路（默认关）。
5. **切分默认不猜时间**：文本与位置都相同的段原样沿用（标识/时间/状态不动）；被切开的旧元素
   **第一段继承原区间与状态**（不截断、不推算），其余段 `Missing`；段横跨多个旧元素时只在都有时间时取**并集**，
   状态都相同就沿用、否则 `Estimated`；只有显式勾选估算才按文本长度比例分配并全标 `Estimated`。
   手动拆分/合并跟此对齐：拆分的时间改标 `Estimated`，合并取共同状态（原 `Edited` 与定义不符）。
6. **时间来源在界面上可见**：芯片提示显示“时间 + 状态”（`LyricChipTooltip`），
   导入报告显示识别理由、候选、丢弃项与切分计数。

### 影响

- `docs/development.md` §7、`docs/timeline-ui.md` §4/§7 就地补注；进度记在 `roadmap.md` §18。
- 切分编辑器采用**独立模态对话框**（主人裁决），适用于“对一行做一次明确的重切”；
  手工边界只作用当前行，“应用到全部行”只用规则。
- 已知取舍：暂不做“过短/过长高亮”（需要新告警色阶与阈值）；YRC/QRC/KRC 与 TTML 的多轨特性仍不做。

#decision #lyrics #import #split

## ADR-0047：样式、布局与歌词分支动画系统的下一轮改造

- **状态**：已接受（基础口径；样式引擎首个渲染纵向切片已实现）
- **日期**：2026-09-27
- **阶段**：阶段 5 之后的样式与动画扩展

### 背景

当前工程已经有样式工作区、样式批量应用和最小版行进入动画，但歌词仍只有一个分支，位置主要由样式中的纵向比例表达，动画只解析单行。继续在现有入口上增加双行卡拉 OK、翻译和注音会把布局、时序和样式混在一起，也会让预览与导出出现两套推导逻辑。

### 提案

本轮先按 [`docs/style-animation-system-plan.md`](../plans/style-animation-system-plan.md) 建立四层契约：样式值对象、布局求解、分支时间采样、动画编排。样式解析优先级增加“分支样式”；`LyricsDocument` 预留 `LyricBranch` 容器；动画由 `AnimationProfile` 和 `LayoutProfile` 驱动；渲染器消费统一场景快照。当前已完成可验证纵向切片：`GradientSpec`/`GradientSampler` 接入 `LyricStyle`、project schema v11、持久化、校验和 Skia 实际渲染；`StrokeSpec`/`ShadowSpec` 接入 project schema v12、持久化、校验和 Skia，分别落实外部描边与方向/距离阴影；旧版字段保持兼容。第三步参数区进一步接入行/元素方向阴影覆盖、阴影方向手柄、字重/粗体/斜体/字距和逐元素字体缺字诊断，project/lyrics schema 分别升至 v14/v8。字体参数目前作为 `LyricStyle` 的可覆盖字段保存，独立 `TypographySpec` 值对象仍属后续工作。

样式方面，提案把现有颜色字段迁移为带停止点、方向和插值语义的 `FillSpec`/`GradientSpec`，描边明确为外部，阴影拆出方向、距离和模糊，字体增加字重、粗体、斜体和字体回退诊断，并允许元素级填充表达单字渐变和多色。

交互方面，提案新增独立 `StyleEditorWindow`，选择器按类别、来源、标签、收藏和最近使用组织；修改先写草稿，应用区明确目标和属性。画布布局模式支持拖动整句歌词，画布下方显示位置，批量位置应用仍通过一个可撤销命令完成。

动画方面，提案内置 `DualLineStaggered`：第 n 行在第一槽位播放，达到阈值后第 n+1 行进入第二槽位；第 n+1 行达到阈值后第 n+2 行进入第一槽位，槽位交替轮换。翻译分支默认静态显示，不进入逐字动画和双行槽位。注音先做数据对象和动态布局接口，时间默认跟随基础字。

脚本边界收紧为只返回样式调制值；脚本不能修改分支时序、位置、槽位、动画曲线或创建场景对象。预览和导出共享同一场景采样结果。

### 用户确认的基础口径

1. 渐变默认采用线性 RGB 和短色相路径，编辑器提供方向、色相路径和插值选项。
2. 双行显示阈值是可配置项，默认建议 50%；默认旧行保留到自身结束，槽位冲突时排队，其他策略作为可选项。
3. 双行动画需要较大的自定义空间：槽位、触发、可见行数、冲突策略、进入/保持/退出参数、分支过滤和继承覆盖都必须进入动画配置。
4. 翻译分支允许独立行级时间，不要求逐字切分；没有关联键时也可以独立显示，默认仍使用静态样式和安全区。
5. 位置使用归一化坐标保存，界面同时显示画布像素和百分比。
6. 注音先做数据对象、基础字关联和动态布局预留，不要求本轮完成导入和编辑入口。
7. 脚本可以调制颜色、渐变、描边、阴影和样式不透明度，但不能改变可见性、位置、槽位、动画曲线、分支时序或场景对象。

仍可独立调整的模板命名、感知色空间选项、无关联翻译的默认安全区和注音第一种输入方式，见计划文档第 12 节。

#decision #style #layout #lyrics-branch #animation #proposal

## ADR-0048：界面字体回退与 Material You 色板设置

- **状态**：已接受
- **日期**：2026-09-28
- **阶段**：阶段 5 之后的界面完善

### 背景

主窗口控件以 `Noto Sans CJK SC` 为主字体时，Avalonia 的普通文本控件没有项目级的
emoji 回退配置；复合 emoji 可能被拆成旗帜、方框或缺字标记。界面主题已有深/浅两套
Material You 资源，但动态入口只覆盖少量强调色，表面色阶和用户可选种子色没有进入主题流程。

### 决策

1. 应用启动通过 `AppBuilder.With(FontManagerOptions)` 注册 `Noto Sans CJK SC` 为默认字体，
   并为 `U+1F000–U+1FAFF`、`U+2600–U+27BF` 注册 `Noto Color Emoji` 回退。ZWJ 和变体选择符
   不拆成独立 UI 文本，Skia 渲染继续使用既有的按文本元素整形回退链。
2. `UiAccentPalette` 暴露同一种子色的浅/深 Material You 角色字典，覆盖主色、次色、表面、
   输入、边框、编辑器外壳和时间轴资源；`Colors.axaml` 保留默认蓝色色板作为无运行时覆盖时的基线。
3. 主题菜单提供调色板对话框，浅色/深色表面与主色同时预览。确认后的种子色只写应用级
   XDG 配置目录 `MakoKaraGenerator/theme.json`，不写 `project.json`、`lyrics.json`，渲染与导出不读取它。
4. 主题种子色应用只替换 Avalonia 资源字典，保留 `ThemeVariant` 深浅切换和 Semi.Avalonia 控件模板；
   界面色阶不改变工程快照、视频像素或导出参数。

### 影响

- 主题资源的角色键是可复用的入口，未来增加种子色设置不需要在各个 XAML 控件中写颜色。
- 设置文件损坏、缺失或写入失败时回退默认蓝色；写入采用临时文件替换，避免留下半份 JSON。
- 字体回退测试验证主字体、emoji 范围和复合文本；真实窗口启动验证主题资源和调色板对话框可加载。

#decision #ui #theme #fonts

---

## ADR-0049：Material You 取色器的常用色/高级色分层

- **状态**：已接受
- **日期**：2026-09-29
- **阶段**：阶段 5 之后的界面完善

### 背景

样式检查器和主题调色板都需要选色。完整的交互式面板适合精确调整，但直接把 RGB/HSV、十六进制、Alpha
和颜色谱铺在每次弹层里会挤压属性检查器，也不符合主界面当前的低密度 Material You 视觉口径。

### 决策

1. `ColorSwatchPicker` 与 `ThemePaletteDialog` 共用 `MaterialColorPickerPanel`；可点色块仍保留原十六进制文本框，
   取色结果继续经过现有颜色校验、草稿或命令和撤销链路。
2. 弹层默认只显示少量圆形常用色。用户点击“高级取色”后，才创建 Avalonia `ColorView` 和 RGB/HSV、十六进制、
   Alpha 编辑区；收起高级区不清除当前颜色。
3. 取色器表面、按钮状态、颜色谱和滑块使用 `MaterialYou.axaml` 的圆角与角色色；预设颜色集中在 `Colors.axaml`，
   文案集中在 `Strings.axaml`。色值仍按 RGBA 通道显式转换，透明色块使用棋盘底衬托。
4. 关闭行为统一为再次点击、点外部或 Esc；键盘焦点和可读提示保留。该 ADR 只规定 UI 入口与展示层级，
   不修改工程/歌词 schema，也不把界面色写入渲染快照。

### 影响

- 默认路径更短，常用色选择不需要理解颜色空间；需要精确值时仍能进入完整面板。
- 取色器交互细节和验收清单维护在 [`docs/color-picker-interaction-plan.md`](../legacy-ui/color-picker-interaction-plan.md)。

#decision #ui #color-picker #material-you

---

## ADR-0050：模板工坊、歌词行编辑器与程序版本边界

- **状态**：已接受（2026-09-29 用户明确确认；0.2.0 功能链已接入，后续实窗反馈见 ADR-0051）
- **日期**：2026-09-29
- **阶段**：阶段 5 之后的交互与项目格式断代

### 背景

现有独立样式窗口同时设计样式、读取当前目标、选择应用范围和提交工程；用户验收后认为这套流程不直观，也不适合模板化。行时间与元素时间已分别存在于数据模型，但元素编辑会自动扩张行时间，时间轴还会在相邻元素共享边界时联动两侧。用户要求明确分开职责，并从新架构开始，不迁移旧样式或保留旧工程兼容路径。

### 决策

1. **模板工坊只产出模板**：独立制作并保存行模板、元素模板；不依赖工程选择，不出现目标、应用和恢复继承入口。模板类型在创建时确定，元素模板只能包含元素级可渲染属性。
2. **歌词行编辑器独占套用入口**：选中行或元素后才选择对应模板、预览差异并按值写入该层覆盖；这里也负责行时间、元素时间、可视化重排与局部试听。工程基线配置不放在模板工坊。旧样式窗口的目标与应用流程在新版本发布前移除，不作为回退入口。
3. **时间约束与单边拖动**：行时间决定可见区间；元素时间必须在行内、按文本顺序不重叠；行内空档保持整行可见而逐字动画静止。拖动选中元素的边沿只改变该元素，邻元素和行时间不跟着移动。旧采样与共享边界联动不保留兼容分支。
4. **断代**：新模板库使用 `templates.json` v1；旧 `styles.json` 不读取、不迁移、不展示，磁盘原文件不主动删除。新版本不加载无程序版本标记的旧工程，也不为其编写迁移器。`project.json` / `lyrics.json` 各自保留 `schemaVersion` 表达文件形状；新结构变动提升对应 schema 并覆盖当前格式的读写测试，但不要求旧版迁移步骤。
5. **正式程序版本**：`Directory.Build.props` 已有 `VersionPrefix=0.1.0` 作为构建元数据；下一次完成程序改造时以 **0.2.0** 开始正式递增。每一批完成并交付的程序修改至少增加一个 `MAJOR.MINOR.PATCH` 版本，普通修复增 PATCH、1.0 前不兼容改动增 MINOR、1.0 后不兼容改动增 MAJOR；只改文档和未交付的中间构建不占版本。程序集信息版本、应用“关于”、日志和工程保存共享 `VersionPrefix` 的三段产品版本；四段 `AssemblyVersion` 不用于工程匹配。
6. **工程版本门禁**：新 `project.json` 必填 `applicationVersion`，保存和自动备份写当前程序版本。打开时在读取歌词与替换工作区前比较；缺失、无效或与当前版本不完全相同均弹窗显示工程与程序版本并拒绝打开，原工程和当前工作区保持不变。不能忽略提示或自动覆盖保存。这意味着每次版本升级后旧版本工程无法直接打开，是本次无向后兼容决定的直接代价。

### 影响

- 本 ADR 取代 ADR-0044 与 ADR-0047 中“样式窗口选目标并应用”的交互口径，也取代 `timeline-ui.md` 中共享边界双边联动的目标行为。它对旧工程的断代策略覆盖 ADR-0005、ADR-0024、ADR-0029 中历史版本迁移约定在**新版本之后**的适用范围；旧条目继续记录当时实现事实。
- 0.2.0 是完整模板／歌词行编辑器与版本门禁一起交付的版本，内部步骤不作为不完整版本发布。具体结构、时间真值表和验收见 [`docs/temporary-style-line-editor-optimization.md`](../legacy-ui/temporary-style-line-editor-optimization.md)。
- 0.2.0 的功能链已接入并通过自动验证；人工验收与待修问题以 [`docs/temporary-style-line-editor-optimization.md`](../legacy-ui/temporary-style-line-editor-optimization.md) §11～12 和 `roadmap.md` §18.7 为准。
- **0.2.5 套用范围细化**：行编辑器内的行模板可以作用于当前行或工程的全部行，元素模板可以作用于当前元素或工程的全部元素；全部包含未定时对象。全部套用先预演目标数与实际覆盖数据改动数、显示确认摘要，再以一步命令提交。模板工坊继续不含套用目标。界面结构与当前行波形的独立视口见 [`template-application-ux-plan.md`](../legacy-ui/template-application-ux-plan.md) §10；它取代早期方案中“批量只从歌词列表多选进入”的限制。

#decision #templates #lyrics-editor #versioning #breaking-change

---

## ADR-0051：模板工坊的基线创建、编辑状态与实时预览

- **状态**：已接受并于 0.2.1 实施（2026-09-29；指定的新交互已获用户实窗验收）
- **日期**：2026-09-29
- **范围**：ADR-0050 的模板工坊交互细化；不改变歌词行编辑器的套用职责

### 背景

0.2.0 的模板工坊已有内置／个人模板、行／元素类型与保存能力，但底部「新草稿／保存为新模板／更新我的模板／删除我的模板」四个动作含义重叠，右侧可在编辑既有模板时直接切换类型，用户无法清楚判断编辑状态。用户还在真实窗口复现：编辑行模板右侧样式并点击更新后，中央预览不变；须先切到元素模板再切回才看到新效果。歌词行编辑器界面与主要操作已获用户验收。

### 决策

1. 软件提供只读**内置模板**与可编辑**个人模板**。新建个人模板必须选择已有模板为基线，复制样式值和属性清单，生成新 ID；默认「行模板／经典整行」。创建时交互输入名称与选择模板类型。若选元素类型，基线候选限定为元素模板，默认「逐字强调」，不得默默丢弃行专有属性。右键「复制此模板」以所指模板预填同一创建流程。
2. 个人模板选中后明确进入编辑状态；模板名称、类型、未保存状态可见。类型在编辑既有模板时固定。内置模板只读，修改它要先复制。新模板在创建对话框确认后成为**未保存草稿**，保存前不进入个人模板库。有效草稿改动须实时刷新中央对应类型试样；保存只负责持久化，不能充当预览刷新开关。
3. 底部只保留「保存模板」「删除模板」。保存统一处理首次创建和个人模板原位更新；删除只针对已保存个人模板，采用主题警告红色并要求确认。列表上方新增「新建模板」，模板卡右键菜单提供「复制此模板」「删除此模板」；内置模板不提供可执行删除。右键命令必须作用于所指卡片。
4. 切换模板、重新新建或关闭窗口时，有未保存修改就提供保存、放弃和取消；放弃后恢复已保存内容，新草稿不落盘。编辑或删除模板库条目不追改工程中已按值套用的效果。

### 实施边界

行模板预览的直接原因是 `LyricLine` 自定义相等比较漏掉 `StyleOverride` 和 `EnterAnimation`，使草稿变化后的新快照不发属性通知；0.2.1 已补齐相等与哈希字段，并通过新快照及渲染像素断言。用户在 0.2.1 实窗验收中确认行模板改色即时预览、新建／复制、保存／放弃、右键删除和最小窗口操作均通过；详细状态表、操作路径与未逐项确认的分支见 [`docs/temporary-style-line-editor-optimization.md`](../legacy-ui/temporary-style-line-editor-optimization.md) §12。0.2.0 不具备本次修订；0.2.1 依 ADR-0050 的版本规则分配。

#decision #templates #ux #preview

## ADR-0052：完整描边模板覆盖与外部字形遮罩

- **状态**：已接受（用户批准第一项样式引擎收尾；0.2.7 已实施，改色／宽度／连接方式实窗验收通过）
- **日期**：2026-09-30

完整 `StrokeSpec` 作为可写模板属性，支持行和元素；覆盖的未设置、显式 null、对象分别表示继承、关闭整个效果和完整参数。完整对象优先于同层基础字段，显式关闭必须阻断上层基础颜色／宽度回退；基础颜色／宽度的局部覆盖可修改上层完整对象并保留连接方式。工坊只制作模板，行编辑器负责套用，工程基线仍在合成设置中编辑。

外部描边不能依赖填充覆盖内半边：半透明或透明填充会露出内部轮廓。Skia 在独立图层绘制外扩轮廓，再用不透明字形遮罩减去内部，最后绘制实际填充；填充 alpha 和样式 opacity 不改变字形遮罩。元素关闭效果继续使用同一装饰外扩区域排除基线和重画。

覆盖进入 `lyrics.json` schema v9；`project.json` v15、`templates.json` v1 保持已有结构。程序版本升至 0.2.7，不追加旧工程迁移。测试、实际渲染差异与用户验收路线见样式改造计划 §13 和 roadmap §18.8。

#decision #style #template #stroke


## ADR-0053：独立行位置、共享几何与布局编辑事务

- **状态**：已接受（用户要求按既定顺序进入画布位置阶段）；0.2.8 已实施，用户确认布局编辑可用、功能全部正常
- **日期**：2026-09-30

`LyricLine.Placement` 是独立于样式与时间的可选值；持久化归一化 X/Y、锚点、旋转与相对缩放。空位置由生效样式的对齐和纵向比例解析排版默认，显式行位置优先；恢复默认只清位置。样式模板不携带行位置。所有排版统一使用实际字形边界求锚点，缩放为样式基础缩放乘位置缩放。

Skia 的 `ILineGeometryProvider` 同时供渲染、边界与命中使用。界面只换算控件／画布坐标；预览与导出沿用同一几何。布局模式的静态试样由请求明确携带行 ID，禁用时间动画并保持元素未唱样式；这是编辑试样，不修改任何时间，也不进入正常帧缓存。正常请求始终使用 `LyricSampler`。

拖动预演只产生临时快照；松开检查源工程及稳定行 ID，成功一次 `Execute`。取消、切行、源工程变化丢弃预演。批量使用独立稳定 ID 勾选清单，确认之前显示目标与位置结果，失效源整批拒绝。平均分布按文档行序使用已有最小／最大坐标；没有跨度时不提交。

程序 0.2.8、歌词 schema v10，工程 v15、模板库 v1；可选行扩展不重构容器，不为旧工程追加迁移。分支默认位置和分支批量随阶段 E 接入。实现证据与验收见样式计划 §14、roadmap §18.9。

#decision #layout #placement #undo


## ADR-0054：显式歌词分支、共享场景采样和基础字注音

- **状态**：已接受（用户直接授权歌词分支、注音和动态布局）；0.3.0 已实施，用户实窗验收待反馈
- **日期**：2026-09-30
- **关联**：ADR-0047、ADR-0050、ADR-0053；样式计划 §15

### 决策

1. 歌词 schema v11 的唯一容器为 `primaryBranchId/branches`，主分支便捷访问不重复持久化。
   主分支唯一；分支、行、元素和注音标识全局唯一。行关联使用稳定主行 ID，不按数组索引补齐。
2. 工坊只制作模板；分支选择后的现有行编辑器编辑当前分支。分支元数据／动态布局用独立设置草稿提交，
   所有写入走工作区命令，一步撤销。单分支命令用纯编辑投影复用，真实主分支始终在保存／渲染前恢复。
3. 样式按工程 → 分支 → 行 → 元素解析。独立行位置优先于槽位／分支默认，宽度约束在布局求解中生效。
4. `LyricSceneSampler` 统一选择可见行和动画通道；`LyricSampler` 唯一解释逐字时间。
   双行默认按行时间 50% 提前显示，缺失行不占槽位，冲突策略从时间完整重建；跳转不依赖 UI 状态。
   短行压缩进入／退出时长，不制造或改写源时间。本行进入覆盖优先于分支进入通道。
5. 翻译默认静态，关联主行的实际可见区间；无关联可使用独立时间。显式整体跟随只复制整体动画通道。
   同时可见的静态关联行按测量占用在安全区分行；用户显式定位的行不被自动重排。
6. 注音跟随基础字的时间与样式；联合占用先测量后定位，基础字进度边界保持不变。
   本批只完成数据、布局、诊断与示例，不引入完整注音输入或第二时间轴。
7. 容器断代升至程序 0.3.0；工程 schema v15、歌词 v11、模板库 v1。继续精确程序版本门禁，不追加旧工程迁移。

### 证据与边界

完整当前格式往返、视图模型、像素／抽帧、变异与实窗探针证据见样式计划 §15；状态以 roadmap §18.10 为准。
用户已验收 0.2.8，本版实际操作验收待反馈。个人动画库、完整双行配置继承及其他高级编排不在本批完成范围。


## ADR-0055：双行模式的槽位布局所有权

- **状态**：已接受（用户确认翻译、注音和分支切换无问题，并授权按复盘结论逐步修改）；0.3.1 已实施，双行实窗验收待反馈
- **日期**：2026-09-30
- **关联**：ADR-0053、ADR-0054；样式计划 §15～16

### 决策

1. 普通模式使用逐行 `LyricLine.Placement`；双行模式使用 `DualLineAnimationProfile.Slots`。两者不能同时拥有同一行的最终布局位置。
2. 双行模式保留已有本行位置数据，不删除、不改写，但渲染、几何命中、静态试样和布局编辑均忽略这些位置。关闭双行后原位置恢复。
3. 双行模式下禁用逐行拖动、位置输入、恢复默认和批量位置操作；用户通过分支设置修改槽位一和槽位二。
4. 进入／保持／退出动画在槽位布局解析完成后应用，只改变动画通道，不重新决定行的静态位置。
5. 分支级整体变换属于后续扩展；它应包住槽位布局并保持槽位一、槽位二的相对关系，不能复用 `LyricLine.Placement`。

### 原因和边界

当前代码的几何优先级曾让本行显式位置覆盖场景槽位，导致槽位参数已保存但画面不变。0.3.1 将布局所有权改为按模式分派，避免隐式覆盖。槽位坐标继续表示槽位锚点在画布中的归一化位置；整体变换和槽位局部偏移暂不进入本版数据格式。

## ADR-0056：分支级双行整体变换

- **状态**：已接受并于 0.3.2 实施（2026-09-30；用户确认整体变换全部正常）
- **关联**：ADR-0053、ADR-0054、ADR-0055；样式计划 §15.7

### 决策

1. 双行模式的位置分两层解析：`DualLineAnimationProfile.Slots` 决定槽位一和槽位二的相对布局，分支的 `DualLineGroupTransform` 决定这组槽位的外层变换。不得把整体变换写入 `LyricLine.Placement`。
2. 整体变换保存归一化横向偏移、纵向偏移、绕画布中心的旋转和整体缩放。恒等值为零偏移、零旋转、一倍缩放；普通模式不消费它。
3. 同一个变换函数同时用于统一场景采样、编辑试样几何、预览和导出。变换位于槽位解析之后、进入／保持／退出动画之前，因此动画只改变通道，不破坏槽位相对关系。
4. 分支设置以显式开关控制是否写入整体变换。关闭开关清除该分支本次草稿中的变换；关闭双行时不删除已保存值，重新启用双行即可恢复。
5. 歌词 schema 升至 v12，不追加旧工程迁移。无效整体变换拒绝打开或保存，禁止静默夹紧用户数值。

### 原因和边界

双行槽位需要同时满足“每句有独立相对位置”和“组合可以整体移动”两个需求。复用逐行位置会让两个布局系统再次争夺所有权；把变换放在分支层能保持槽位关系，也让普通模式的逐行编辑继续独立工作。本批提供分支设置数值入口，不实现画布拖动手柄、关键帧或分支级动画曲线。

保存／重开、采样位置、几何枢轴和实际 Skia 画面差异由自动测试覆盖；用户已完成整体变换的移动、旋转、缩放、关闭恢复、保存、撤销和取消验收。

#decision #layout #dual-line #transform

## ADR-0057：统一样式数据模型与 0.4.0 断代

- **状态**：已接受（用户要求先完成设计再实现）
- **日期**：2026-09-30
- **关联**：ADR-0032、ADR-0040、ADR-0050、ADR-0056；[`style-data-model-unification-plan.md`](../plans/style-data-model-unification-plan.md)

### 决策

1. 样式的持久化真值改为嵌套对象：`TypographySpec`、未唱／已唱各一个 `FillSpec`、可选 `StrokeSpec`、可选 `ShadowSpec`，以及对齐、纵向比例、不透明度和缩放。覆盖层使用嵌套的部分 `FillSpecOverride`，以支持只改颜色或只关闭渐变。旧的颜色、渐变端点、描边和阴影平面字段不再写入当前 JSON。
2. `GradientSpec`、`StrokeSpec` 和 `ShadowSpec` 继续作为值对象复用；`FillSpec.gradient == null` 唯一表示纯色，填充对象始终保留一个基色。位置、双行槽位和分支级整体变换不属于样式模板。
3. 覆盖按默认 → 工程 → 分支 → 行 → 元素合并。嵌套对象按成员合并，描边／阴影的显式 `null` 关闭本层效果；填充显式 `null` 属于无效数据。元素级排版限制保持不变。
4. `LyricStyleProperty` 保留为模板和批量编辑的细粒度地址，但必须映射到嵌套对象，不得再形成第二套持久化结构。
5. 工程、歌词和模板 schema 分别升至 16、13、2，程序版本升至 0.4.0。0.4.0 不为 0.3.x 样式 JSON 增加迁移；缺失或不一致的程序版本继续拒绝打开。模板库版本不匹配时只显示版本提示并按空的个人模板库继续，保存新模板才写入当前 v2，不读取或转换旧模板。
6. 为减少一次性界面改动，本轮允许旧平面成员作为非持久化源码外观，访问器直接读写嵌套对象，不维护第二份值；新 DTO 只读写规范对象。界面迁移完成后删除该外观。

### 原因和边界

当前模型同时维护基础颜色、旧渐变端点和完整效果对象，解析优先级不能从数据结构本身看出。统一对象让模板、覆盖、解析和渲染共享一个契约，同时保留模板细粒度操作。字体回退链、关键帧和脚本效果不属于本次数据断代，避免把尚未接入渲染接口的能力写入工程。

#decision #style-model #schema #versioning

## ADR-0059：脚本系统先收口版本化消息边界，再决定运行进程（初版，已由 ADR-0060 修订）

- **状态**：历史初版提案；运行模型、状态语义和统一场景方向已由 ADR-0060 修订；代码仍未实现
- **日期**：2026-09-30
- **关联**：ADR-0043、ADR-0047、ADR-0058；[`docs/scripting-system-architecture-plan.md`](../plans/scripting-system-architecture-plan.md)

### 背景

仓库已经有 `ProjectSnapshot`、`LyricSceneSampler`、`LyricSampler`、`LyricStyleResolver`、行几何提供器和共享的 `IRenderPipeline`，但没有脚本 SDK、编译/加载服务、持久化或 UI。`development.md §11` 的早期概念接口允许脚本持有可变场景并创建对象，与 ADR-0047 已确认的“首期脚本只调制样式”边界冲突。

### 本次提案

1. 首期脚本只读取由快照、场景采样、样式解析和几何求解组成的版本化只读投影，返回当前帧的填充、渐变、描边、阴影和不透明度调制。
2. 脚本不得修改歌词源数据、时间、分支、槽位、位置、动画曲线或可见性，不直接引用 Core 领域类型、Avalonia 或 Skia。
3. 通过 `MakoKaraGenerator.Scripting.Abstractions` 定义可序列化的 `ScriptFrameInput`、`ScriptFrameResult`、参数元数据、诊断和 `IScriptRuntime` 消息边界。预览与导出共用同一个脚本评估服务和结果合并规则。
4. 第一条垂直切片可使用受信任主进程和可回收 `AssemblyLoadContext`，但运行时实现不得泄漏进程内类型；同一消息协议必须可以迁移到独立 worker。
5. 粒子、路径、额外文本和其他临时对象另立第二层 `OverlayCommand` 协议，待新 ADR 明确对象生命周期、资源权限、合成层级和性能后再做。
6. 新脚本不得在项目打开时自动执行；信任、启用、源码哈希、SDK API 版本和错误状态必须可见。`AssemblyLoadContext` 不被当作安全沙箱。

### 尚未决定

> 本列表属于 ADR-0059 的历史初版，当前运行边界已由 ADR-0060/0061 收口。

- 首期使用受信任主进程还是直接使用独立 worker；
- 首期脚本来源是项目内源码包还是允许外部脚本目录；
- 是否允许依赖播放历史的状态效果，还是强制按绝对时间可重建；
- 导出遇到脚本错误时默认阻止，还是允许用户明确禁用失败脚本后继续；
- 脚本参数是否作为工程数据进入撤销栈并使渲染缓存失效。

在这些事项确认前，不创建脚本程序集、不提升工程 schema、不引入 Roslyn 包，也不把脚本入口写入 `RenderRequest`。完整输入/输出字段、测试矩阵和 S0～S6 落地顺序见脚本架构方案。

#decision #scripting #architecture #trust #rendering

## ADR-0060：主进程内有状态脚本与统一歌词场景容器（提案）

- **状态**：已按用户反馈收口设计，代码尚未实施
- **日期**：2026-09-30
- **关联**：ADR-0055、ADR-0056、ADR-0059；[`scripting-system-architecture-plan.md`](../plans/scripting-system-architecture-plan.md)、[`unified-scene-element-plan.md`](../plans/unified-scene-element-plan.md)

### 已确认的运行边界

1. 脚本在主程序进程内运行，但使用每个会话独立的串行调度器，不占用 Avalonia UI 线程。当前不建设独立 worker、恶意脚本沙箱或脚本市场。
2. 允许 Roslyn 热编译和运行时代次替换。编译失败时预览可以继续显示上一代次，但必须明确标记；当前启用脚本在导出前编译、初始化、重放或求值失败时，导出阻止并报告原因。
3. 脚本可以持有工程会话的全局状态，读取可重建的歌词时间线历史和离线音频特征，并接收“字已出现”等类型化 callback。预览和导出使用独立会话，不共享用户拖动播放头的残留状态。
4. 脚本参数属于工程源数据，参数一次提交进入撤销栈；撤销／重做会重建脚本会话并失效相关缓存。

### 统一场景容器决定

1. 普通歌词行、动态双行组合、翻译和注音在渲染层统一为 `SceneElement` 画布实例。实例拥有唯一的外层 `Placement`；单行或双行只是 `LocalLayout` 的不同策略。
2. 普通模式的行位置和双行模式的分支整体变换都先适配为容器外层位置；槽位只负责双行容器内部的相对位置。渲染器不再保留 `ignoreLinePlacement` 这种模式级位置所有权分支。
3. `SceneElementId` 表示画布实例，`LineId`／`TokenId` 表示歌词源对象，`BindingId` 表示槽位或关联本次装载。选择、命中、callback、缓存和脚本效果必须明确使用哪一种 ID。
4. 场景容器、帧结果和脚本效果是可重建的临时结果；工程只保存源码、参数、绑定和可重建的场景定义，不保存某一帧的几何缓存。

### 约束与落地顺序

先冻结普通行、双行、翻译和注音的 `SceneFrame` 行为，再建立旧字段到容器的适配器；随后统一几何、静态试样、命中测试、预览和导出，最后接入 Roslyn 会话与音频特征。首期脚本仍只返回经过主机校验的样式调制，容器变换、粒子和额外对象另立协议。详细设计和验收以两份方案文档为准。

#decision #scripting #state #scene-element #layout

## ADR-0061：统一场景模型定稿、脚本首期输入与本地编译库

- **状态**：已接受（用户确认场景方向并将字段设计交给代理，确认样式权限、音频范围和 DLL 本地持久化）；代码尚未实施
- **日期**：2026-09-30
- **关联**：ADR-0060；[`scripting-system-architecture-plan.md`](../plans/scripting-system-architecture-plan.md)、[`unified-scene-element-plan.md`](../plans/unified-scene-element-plan.md)

### 决策

1. “真实播放操作”专指用户在预览编辑器中点击播放／暂停、拖动播放头、循环和跳转等控制历史，不是渲染帧。首期不把它作为脚本视觉输入；预览和导出都从歌曲绝对时间线独立重建 `ScriptSession`。
2. 统一节点类型为 `Group`／`Text`，定义含 `Id`、`ParentId`、`Kind`、`SourceBinding`、`Placement`、`ReferenceRect`、`LocalLayout`、`Order`。分支显示容器和双行槽位具有稳定身份，换行不重建身份或枢轴；当前绑定、`BindingId`、可见区间和进度进入帧结果。单行／双行是 `FreeLine`／`DualSlots` 策略，共用同一个容器外层位置。
3. 容器配置与局部布局集中到工程 `lyricPresentation`，歌词保存源语义与样式覆盖，正式断代退出旧展示字段。适配阶段忠实投影当前“画布槽位 → 绕画布中心整体变换”；不把正常双行未消费的 `branch.DefaultPlacement` 再叠加。世界矩阵按父世界变换 × 本节点静态位置 × 本节点局部动画合成。
4. 脚本首期只能返回主机校验的填充／渐变、描边、阴影和不透明度调制；不得改变字体、字号、字距、缩放、可见性、位置、局部布局、时序、对象集合或源数据。容器变换、粒子和额外对象另立协议。
5. 音频输入只包含可重现的节奏和低频可视化数据：低频 RMS／能量及包络、短时变化和节奏脉冲；可靠时才提供拍点相位／序号／置信度与 BPM，不将低频脉冲等同准确拍点。首期不暴露完整 FFT、高频频谱或通用高潮检测器。
6. 工程 `scripts/` 保存源码和清单；成功编译的 DLL、PDB、构建清单按源码、SDK、编译器、引用程序集和运行时指纹分代发布到应用本地数据目录。Linux 使用 `$XDG_DATA_HOME/MakoKaraGenerator/scripts/`，未设置时使用 `~/.local/share/MakoKaraGenerator/scripts/`，程序退出后保留。DLL 可由源码重建；指纹不一致先重编译，失败阻止导出。导出固定成功代次，当前启用／正在使用代次不能清理；复制工程携带源码和参数，目标机器重新生成 DLL。首期从 C# 源码编译，现成 DLL 导入和第三方依赖包管理后置。

### 边界

这些设计决定不改变当前 0.5.0 的程序或 schema，也不代表用户已恢复程序开发。先完成统一场景 S0～S3，再做脚本。后续播放会话录制、粒子或场景变换应新增独立协议，不扩大首期样式 SDK 权限。

#decision #scripting #audio #persistence #style-only


## ADR-0062：统一场景与共用位置编辑正式断代（2026-09-30）

### 状态

用户批准按 ADR-0061 开始开发，先完成统一场景及共用位置编辑。0.6.0 已实施 S0～S3；2026-10-01 用户确认双行分支及内部槽位位置生效，其他操作细项未逐项反馈，脚本不在本批实施。新增的容器编排需求仍在讨论，不修改本 ADR 的现行实现口径。

### 决策

1. 每个分支持有稳定 Group 容器，普通和双行共用外层 Placement 与固定 ReferenceRect；模式只选择内部 FreeLine／DualSlots，翻译使用内部安全区流式排布，注音作为 Text 子节点联合测量。
2. 新增工程级 LyricPresentationDefinition。源歌词退出所有旧展示字段，包括行位置／进入覆盖和分支默认位置／安全区／宽度／双行配置／专用整体变换／动画策略。工程级默认 EnterAnimation 留在 project.json，行覆盖由新展示定义关联；两份文件不重复保存展示状态。
3. SceneDefinitionBuilder 投影 Group／Text 定义；LyricSceneSampler 重建绑定和进度，Skia 的 GetSceneFrame 统一完成流式布局、联合测量及节点世界几何。绘制、布局试样、命中与导出消费同一入口，删除 ignoreLinePlacement／slotOwnsLayout 和 DualLineGroupTransform 类型。
4. 外层拖动和数值默认编辑容器；普通模式可明确选择当前行局部位置，双行可明确选择槽位一／二。内部坐标归一化到父参考矩形，拖动先应用父矩阵逆变换；有限的画布外值由裁切处理，不夹紧源数据。容器枢轴不随文本测量边界变化。
5. 世界变换顺序为父容器 × 局部静态位置 × 局部动画；样式字号／缩放只作用于文字排版，不缩放动画位移单位，父容器旋转／缩放会变换动画方向和距离。静态编辑试样关闭时间动画和逐字进度；双行试样同时显示两槽位，未定时试样不制造源时间。
6. 展示编辑、复制源行的展示覆盖、删除源对象的关联清理均属于显式工程命令，进撤销栈；草稿只生成预览快照。读盘校验只报告失效关联，不清理用户文件。
7. 程序升至 0.6.0，工程／歌词 schema 升至 v18／v15；模板 v3 不变。精确版本门禁及未知旧键拒绝继续生效，不读取或转换旧工程。

### 验证与后续

验收及自动检查证据见 roadmap §18.17 与统一场景方案 §10。脚本 SDK、事件历史、音频输入、Roslyn 和本地 DLL 留到后续；当前 SceneFrame 是宿主模型，不等同已经发布的脚本 SDK。

本决策取代 ADR-0054～0056 中旧模式的位置所有权和专用双行整体变换实现口径；这些历史批次的用户验收保持原记录。ADR-0061 的脚本权限与会话合同不变。

#decision #scene #placement #persistence


## ADR-0063：原生通用容器树与源行独占片段

- 日期：2026-10-01
- 状态：用户明确选择数据结构及全部显示行为；0.7.0 已实施，已确认双行显示与整体位置编辑，其他细项未逐项反馈；0.7.1 注音修订见 ADR-0064。
- 依据：容器编排复盘及 [完整案例方案](../plans/lyric-exclusive-display-case-plan.md)。

### 决策

1. 直接将工程展示配置替换为独立的通用容器树：`nodes`、`programs`、`lineOverrides`。容器、槽位、常规显示、独立显示和纯父组使用同一节点类型；允许嵌套，自定义 ID 不依赖 BranchId。旧展示 branches 不读取或转换。
2. 节点保存唯一 Placement 和相对于父固定尺寸的 ReferenceRect；Layout 保存槽位节点 ID 顺序，SlotAnimationSpec 不重复保存位置。LyricBranchPresentation 只是现有窗口的临时适配，不能成为第二份持久化配置。
3. 显示程序关联源分支、共同根和常规容器；独占片段只保存稳定身份、源 LineId 与独立容器 ID。普通句和特殊句不复制源歌词，首批同一源行仅一个活动显示归属。
4. 独占句严格按源行半开时间显示，期间暂时退出双行；边界直接切换。该句自身进入动画仍按源起点播放。结束后常规片段从槽位一重新轮换，独占句不消耗槽位；提前显示不得跨越片段。
5. 独立容器位于共同父组下，随整组移动、旋转和缩放。关联翻译继续在自己的显示位置；注音继承基础字的最终矩阵。父子变换真实递归解析，不要求节点存储按拓扑排列。
6. 普通演唱区间与独占区间重叠时拒绝配置，报告冲突行；半开边界相接合法。配置预演、行时间／平移、打开和导出共用校验；失败不改原数据。保存收集诊断并保留草稿原值，读盘不清理关联。显式删除源对象才清理对应程序／片段／行覆盖，保留闲置容器，撤销恢复。
7. 本批先完成五句案例、原生模型、持久化、校验、调度、几何、现有编辑入口适配、预览与成片；容器树／片段归属／槽位增删重排 UI 后置。脚本及性能基准不进入本批。
8. 程序 0.7.0，工程 schema v19；歌词 v15、模板 v3 不变。精确程序版本门禁继续生效。

### 取代与证据

本决策取代 ADR-0062 中按分支持久化展示配置及固定展示层级的口径；原有共用位置、源歌词职责、动画顺序和历史验收保持。脚本权限与会话继续按 ADR-0061。验证、实窗探针及后续验收以 roadmap §18.18 和案例方案 §7 为准。

#decision #scene #container #exclusive #persistence

## ADR-0064：注音按基础元素对齐与演示关联修订

- 日期：2026-10-01
- 状态：用户明确选择按元素注音；0.7.1 已实施演示修订，用户确认注音恢复正常。
- 依据：用户截图与答复，现行 RubyAnnotation 的 BaseTokenId 合同。

### 决策与差异

1. 注音围绕关联基础元素的文本区间居中，并跟随该元素的进度和最终父矩阵。一个基础元素可以包含多个字；本轮继续使用单 BaseTokenId，不新增整句或跨元素注音数据。
2. 原演示 `qǐng zhào` 只关联「请」，使整段围绕单字居中。按用户选择修订为「请→qǐng／照亮→zhào liàng」两个注音；显式字号比例 0.4，避免相邻字形交叠。保留「请／照亮／我／前行」及原演唱区间，已唱状态分别跟随自身元素。
3. 字形联合占用、位置、预览与导出继续使用同一测量与绘制路径；用户已有错误注音数据不静默改写。此次修订适用于重新构造的演示工程。
4. 程序 0.7.1；工程 v19、歌词 v15、模板 v3 不变。精确程序版本门禁继续生效。

### 验证与下一阶段

实际演示资源回归测试先在原错误关联上失败，修订后验证关联／时间／保存重开、基础元素中心、父组旋转缩放、相邻注音间距及各自已唱／未唱像素。将比例改回 0.5 时两个间距测试按预期失败，恢复后通过。全量构建、测试及实际渲染画面证据见 roadmap §18.19。

用户已确认后续“分段选择容器与布局”“槽位内单句局部微调”。[通用容器编辑器方案](../legacy-ui/scene-container-editor-plan.md)记录当前能力、详细提案与剩余决策；本轮未实施新编辑器或具体数据合同，也未改变当前槽位模式的行位置规则。

#decision #ruby #rendering #demo

## ADR-0065：通用编排的片段区间、重挂与槽位重新分配

- 日期：2026-10-01
- 状态：已接受；用户明确确认行为、schema 20 和 UI，0.8.0 已实施 G1～G4，基本编排流程获人工确认，界面需优化，其他边界待反馈。
- 依据：用户要求先确定数据结构和 UI，以及对三项具体产品问题的答复。

### 已确认的决策

1. 同一源分支可分段使用不同容器与布局；自动槽位允许单句局部微调。双槽、三槽及自由布局沿用同一容器编辑机制。
2. 多句片段的区间为首句源行起点至末句源行终点，随源时间更新。内部演唱时间仍由各源行／元素决定，片段边界直接切换。
3. 容器重挂到另一父组时保持画布上的位置、旋转和大小，将位置换算为新父组下的局部参数。
4. 增删或重排槽位后，整个片段按新槽位顺序从第一个槽位重新分配；布局编辑暂停播放，预演后原子提交，不按当前播放进度保留旧分配。
5. 本轮先提交已验收的 0.7.1 状态到远程，再审阅详细数据结构及宽／窄 UI，随后分 G1～G4 实施。脚本和性能基准暂不进入本批。

### 具体合同的审阅边界

[实施合同](../legacy-ui/scene-editor-implementation-contract.md)细化为通用 nodes／programs.clips／lineOverrides.localTransform、动态 NodeId 选择及三栏工作区。用户已回复「按这份数据合同实施」「按这套 UI 实施」，批准默认容器兜底、片段删除回归、连续且定时的成员、子树复制不复制绑定及具体 schema 20。G0 完成并按批准合同实施 G1～G4，交付 0.8.0。

G0 时程序为 0.7.1／工程 v19。现行程序 0.8.0，工程／歌词／模板 v20／v15／v3：通用片段保存连续源成员、区间随源时间解析；单句微调保存 LocalTransform，NodeId 选择和纯 Core 世界矩阵贯通编辑／采样／渲染。未分配成员由默认容器接收；片段删除回默认，源删除只清理明确关联；复制子树不复制绑定。草稿一次提交入历史，过期拒绝，结构和时间失败不静默修正。1470 项测试、真实控件探针及 MP4 对比通过；用户实际制作工程并确认容器／片段／轮换基本生效，其他边界未逐项反馈。界面优化另见新方案及 ADR-0066。完成度以 roadmap §18.21 为准。

#decision #scene #editor #composition


## ADR-0066：树中槽位顺序直接表达歌词轮换顺序

- 日期：2026-10-01
- 状态：**已接受并在 0.8.1 实施，人工验收通过**；用户批准完整体验优化方案，自动验证通过，随后明确回复“测试验收通过”。
- 依据：用户实际使用 0.8.0 后提出树菜单／拖动需求，并明确答复“拖动槽位就改变轮换顺序”。
- 关联：ADR-0065、[编排体验优化方案](../legacy-ui/scene-editor-ux-optimization-plan.md) §6。

### 决策

1. 轮换容器的槽位在树中按现有 `SlotIds` 排列，显示轮换序号。树上槽位拖放、上移／下移与槽位列表使用同一重排操作，直接改变轮换次序；不再把树中的普通节点存储次序呈现为另一套槽位次序。
2. 其他同级子节点继续按 `Nodes` 的顺序排列，与槽位分组显示，避免把两种排序目标混用。该交互不引入媒体绘制层级。
3. 名称及稳定 NodeId 保持。相关片段按 ADR-0065 从新的首槽重新分配，编辑暂停播放，改动先进入展示草稿，一次应用进入历史；无变化保持原引用。
### 现状澄清

槽位数量和正整数同屏上限是独立参数，现行合同不要求上限小于或等于槽位数量；原文档中“减少槽位导致容量非法时降低上限”纠正为实际合同，删除最后一个有效槽位仍拒绝。这是对当前代码的文档纠正，不是本轮新设的容量规则。

### 实施与审阅边界

方案阶段先交付文档和 UI 稿；用户随后明确“按方案实施 0.8.1”。现已在 App 实施按钮式入口、直接布局方式、槽位列表、树菜单／拖放排序、重挂确认、来源／默认容器／片段分层及对齐。树和属性栏的槽位列表均调用同一重排；边缘同级排序、主体重挂保持世界外观，Esc 取消。草稿应用、放弃、一次撤销及 Core 校验沿用 ADR-0065。

程序 0.8.1，工程／歌词／模板 schema 保持 v20／v15／v3；没有新增格式、兼容层或脚本协议。1480 项测试与真实控件事件探针通过；用户现已明确本批“测试验收通过”。具体证据、历史原型与验收记录分开记录在[方案 §13](../legacy-ui/scene-editor-ux-optimization-plan.md#13-081-实施与验收)，权威状态见 roadmap §18.23。

#decision #scene #editor #slot-order #ux

## ADR-0067：前奏提示的起唱锚点与准备显示行为

- 日期：2026-10-01
- 状态：**行为及具体合同已接受；0.9.0 已实施，自动验证及用户人工验收通过**。原始设计审阅过程保留，批准、实施及验收补记见本节末尾。
- 依据：用户要求卡拉 OK 前奏调研，并对八项显示问题逐项答复。
- 关联：[前奏方案](../plans/karaoke-lead-in-plan.md)、ADR-0061／0065／0066，roadmap §18.24。

### 已确认的决策

1. 自动生成范围为歌曲开头及长间奏后的再入唱，长空档阈值可配置。
2. 倒计时归零使用目标句首个有有效时间的元素起点；无逐字时间使用行起点。
3. 前奏也是容器场景元素。期间提前显示目标歌词，前奏开始切换原有保留歌词，只显示目标句及提示；关联注音和翻译沿用其关联规则。
4. 目标句在前奏开始时播放自身入场动画一次，起唱连续衔接。若原轮换规则会更早显示该句，有前奏的目标句必须等到倒计时开始再显示。
5. 开头不足放入完整前奏时跳过并列出原因，由用户决定手工添加；不平移音频或源歌词。
6. 提示同时支持跟随目标句和父组内固定位置。

### 原始实施合同审阅记录

方案建议在现有 nodes 上增加内容／附着，programs 增加 leadIns；提示组拥有可增删重排的文字标记，三点只是预设；共享显示计划加入准备阶段，保持目标原片段、槽位和装载身份，预览／导出消费同一内容帧。自动分析使用源演唱空档和时间依据，候选先审阅，手工结果受保护，进入既有展示草稿与一次撤销。

上述具体字段、显示计划优先权、生成／删除规则、UI 和建议初始参数属于[方案 §4～11](../plans/karaoke-lead-in-plan.md)的待审阅内容，尚未获得数据格式实施授权；必须按 AGENTS.md §8.3 确认后修改代码。ADR 本次仅固化用户已经答复的显示行为，不批准完整 JSON 或宣告实现。

设计交付时程序／schema 为 0.8.1／20／15／3；实施目标为 0.9.0／工程 v21，不读取或迁移旧工程。脚本权限继续按 ADR-0061，前奏不开放脚本改时间和布局。

#decision #scene #karaoke #lead-in #timing

### ADR-0067 实施合同批准补记（2026-10-01）

用户明确授权按 `docs/karaoke-lead-in-plan.md` 逐步实施。采用该文 §4 的 nodes.content／attachment、programs.leadIns／leadInGeneration 合同，以及 §9 的工作区 UI、候选保护、删除语义和可修改初始参数。目标为 0.9.0／工程 v21；源歌词时间不变，歌词／模板 schema 不变。

### ADR-0067 实施补记（2026-10-02）

L1～L4 已完成；提示内容和准备阶段贯通持久化、绝对时间计划、共同 Skia 渲染及既有展示草稿。16 项 Core、5 项采样、5 项像素／矩阵、2 项 MP4 对比及 4 项 App 回归新增，全量 1512 项通过、构建 0 警告／0 错误。真实 Avalonia 宽窄界面和候选控件验证通过。完整证据和使用路线见前奏方案 §13～14，权威状态见 roadmap §18.24.1。脚本未开工；SDK 应明确识别标记内容、CueId、准备／演唱阶段和真实 BindingId。

### 人工验收补记（2026-10-02）

用户确认“测试完成，功能全部正常，前奏验证可用，且前奏确实也属于特殊容器，绑定在目标句上，符合预期”。0.9.0 本批人工验收通过；记录总体反馈，不追加未明确反馈的逐项检查结论。后续脚本实施方向见脚本方案 §15，SDK 和执行代码仍未开工。

## ADR-0068：脚本逐字视觉缩放、跨行粒子与文本重复起唱回调

- 日期：2026-10-02。
- 状态：**用户已确认效果行为和平台；具体 SDK／包／数据／UI 为待审阅提案，未实施**。
- 关联：ADR-0060／0061／0065／0067；[效果 SDK 与打包方案](../plans/scripting-effects-sdk-package-plan.md)、[脚本运行基线](../plans/scripting-system-architecture-plan.md)，roadmap §18.26。

### 用户确认的方向

1. 逐字强调根据当前元素自己的持续时间和进度驱动；逐字及重复文本的“变大”均采用**不重排的视觉缩放**。注音随基础字一起放大和移动，源字号、字距、槽位和容器位置不因此改变。
2. 文本重复 callback 在**实际起唱**时计数，提前可见／前奏／暂停重绘不增加次数；提供分支内／全工程范围选择。按源演唱发生区分结果，后来的计数不把保留中的旧句一起修改。
3. 播放头粒子从实际文字几何取得锚点，有拖尾；跨行提供**沿路径飞行／直接更换发射点**两种参数，旧尾迹继续消散。没有空档时直接换到下一句，不修改演唱时间，也不占用下一句开头继续飞行。
4. Roslyn 经本轮重新比较后由用户明确选定：首版 **C#＋Roslyn**。原受信主进程内串行、不占 UI 线程、可重建状态、导出失败阻止、参数撤销及本地持久 DLL 规则继续沿用。

### 权限与提案边界

本轮设计扩展 ADR-0061 中“首期仅填充／描边／阴影／不透明度，缩放及粒子后置”的范围，加入独立的元素视觉缩放和临时粒子输出协议。工程源内容、时间、持久树和排版仍由宿主决定；不是把可写 Project 或 SkiaCanvas 暴露给脚本。

源码资源包／工程副本／本机 DLL、Appearance／Visual 阶段、OccurrenceId 和光标轨道、包／工程 JSON、事件合并顺序、数量预算、绑定／删除语义及项目拆分是完整方案的**拟议合同**，必须按 AGENTS.md §8.3 审阅具体结构后实施。此处不将用户效果需求等同于已批准每个字段／SDK 方法／UI 稿。

当前程序仍 0.9.0、schema 21／15／3；脚本代码、Roslyn 依赖、粒子和视觉缩放均未进入实现。下一步按方案 E0 补齐并确认实施合同，之后 E1～E5 逐步验证三个完整示例，性能基准后置。

## ADR-0069：高阶材质与整行处理的渲染图方向

- 日期：2026-10-02。
- 状态：**评审提案，未实施；背景来源已确认，透明输出规则按 ADR-0070；具体架构／平台／格式仍待审阅**。
- 关联：ADR-0057／0058／0061／0068；[高阶材质与渲染图评审](../plans/advanced-material-render-graph-plan.md)，roadmap §18.27。

### 需求与已确认项

用户要求重新评估自定义文字／描边 shader、液态玻璃／毛玻璃与整行 glitch，指出前轮仅固定样式 writer、元素缩放和粒子不足以表达高阶自定义。用户确认玻璃背景来源应支持选择**仅背景媒体／目标后方全部已合成内容**。

ADR-0068 的 C#＋Roslyn、保留排版的缩放、注音跟随、实际起唱计数及粒子跨行行为继续有效。高级图像外观不改源文字、时间、整形或持久场景树；此前“首期不开放 shader”仅为历史提案范围，当前正在评估扩展，并不代表 shader 已实现。

### 待审阅提案

1. 区域材质、通用效果栈／渲染图和 C# 语义控制分层；建议 SkSL／Skia RuntimeEffect 作为首个材质后端，现有锁定版本不升级。当前输出是 CPU raster，须先做实际能力验证，不宣称已经支持 GPU 或复杂玻璃。
2. 填充／描边以同一材质判别结构接入内置与自定义，轮廓几何独立；模板保存配方与包修订，不保存目标或运行历史。图以中立句柄承接字形／描边／已唱蒙版、孤立行／组、背景和下层合成快照，不公开 SkiaCanvas。
3. 同一效果实例可声明多个有阶段依赖的输出；同帧图为有向无环图。光标区分语义几何、显式 AnchorMap 与无唯一坐标的图像外观，不能从任意 shader 自动猜变形后的播放头。
4. `.mkfx` 提案增加材质导出项、SkSL 和图配方；C# DLL 继续本地持久化，shader 源码及后端编译缓存分别管理。历史图像需歌曲时间合同，不能取最近 UI 帧当确定状态。
5. 原透明导出缺背景提问已由 ADR-0070 收口：跳过依赖效果、提示、使用包内基础外观。具体材质／图／uniform／模板依赖、合成位置、删除语义、预算、项目依赖及 UI 按 AGENTS.md §8.3 审阅后实施；不把本次需求等同于已批准全部结构。

建议顺序改为 H0 合同、H1 锁定后端能力验证、H2 共用骨架，随后包／运行时与原三个效果闭环。用户本轮只要求重新设计，程序仍 0.9.0、schema 21／15／3，未增加依赖或实现 shader／脚本；完整性能基准继续留到脚本系统完成后。

## ADR-0070：透明导出跳过背景依赖效果并采用包内基础外观

- 日期：2026-10-02。
- 状态：**产品规则已确认，具体 bypass／数据／SDK 合同待审阅，尚未实施**。
- 关联：ADR-0037／0061／0068／0069；[脚本具体落地路线图](../plans/scripting-system-implementation-roadmap.md) §3／8，[高阶评审](../plans/advanced-material-render-graph-plan.md) §11.2，roadmap §18.28。

### 用户已确认的规则

1. 透明输出没有背景媒体时，直接尝试忽略背景依赖的着色器效果，**必须提示用户**；不因本次模式造成的缺背景输入阻止导出。
2. 用户进一步选择：文字／描边材质跳过后，使用**材质包提供的纯色／渐变基础外观**。整行图像滤镜恢复处理前图像。

### 与原合同的关系

此规则取代高阶评审原“建议缺背景时阻止透明导出”的待选方案，属于有意的输出模式差异；不修改工程材质、启用状态或源数据。不依赖背景的效果不因 shader 类型被全部禁用。

既有“当前启用源码失败阻止导出”继续有效；本次决定不是捕获所有编译／运行／资源错误的豁免。导出应区分有提示的 intentional bypass 与效果执行失败，后者不能被当前 VideoExporter 的占位帧兜底吞掉。

包内基础外观字段、背景依赖标记、混合实例／子图的绕过传播、缓存／提示清单、错误协议及版本化格式是路线图 R0 的待审阅合同。本轮只整理 R0～R8 路线及破坏性变更，没有实现或依赖授权；程序仍 0.9.0、schema 21／15／3。

## ADR-0071：脚本 R0 实施合同、确定性执行和源码资源格式

- 日期：2026-10-02。
- 状态：**用户已接受完整 A／B／UI；R1 技术验证已完成，完整功能按 R2～R8 实施**。
- 关联：ADR-0068～0070；[实施合同](../plans/scripting-system-implementation-contract.md)、[SDK 参考](../../scripting-sdk-contract.md)、roadmap §18.29。

用户逐项确认“接受合同 A，按此实施”“接受合同 B，按此实施”“接受 UI，继续 R1”。采用两中立脚本程序集、固定 Roslyn／参考集、源订阅与输出绑定分离、同实例多输出、120Hz 精确事件边界／状态分区、资源预算和初始参数；同一 SDK／帧路径贯穿预览／导出。

材质唯一判别结构、外部描边几何、typed DAG、背景两种来源／合成位置、映射及透明回退按合同 §5～6。包和修订内容地址、实例表、模板配方和资源闭包按 §7；根 project.json 最后原子提交并指向不可变歌词文件，新 templates-v4 库保留旧库，被引用包拒绝删除，失效绑定不自动重挂。源码接受和运行成功分开，失败阻止导出，上一预览须标为旧版。

UI 按已展示交互稿：实例／源码包分开，常用参数一级显示，源码及诊断明确切换，窄窗显式覆盖面板。R1 技术验证不改用户工程，能力不足须讨论，不默认引入 GPU 或縮减范围；R4 格式断代后不兼容旧工程。R0 的接受不是 SCRIPT 功能完成，程序／schema 仍按实际批次记录。

#### 0.9.1 R1 实施记录

两个新程序集、最小 TokenId、Roslyn 5.0.0 与分发 net10.0 参考集、持久 DLL／PDB 及可回收加载已实现。真实 CPU raster 技术样本验证材质／外部描边、行 glitch、两种玻璃输入、密度／父组及透明编码；当前能力可进入 R2，不引入 GPU 或升级锁定库。支持边界和 1537 项验证见 [R1 报告](../research/scripting-r1-capability-report.md)。生产材质／渲染图／工程数据仍待 R2～R4，不把探针当成实际工作区。

TryGetPixel 的预乘饱和通道直通读取错误已修正，五种 Alpha 回归通过；底层 BGRA／编码字节不改变，不产生样式画面差异。版本 0.9.1，schema 21／15／3 保持；完整 SCRIPT 与性能门禁仍按后续批次。

#### 0.9.2 R2 源投影及固定字形实施记录

已接受 §5 要求整行一次整形并保存真实绘制单元。后端现保存完整字体 run 的 glyph ID／位置／墨迹及 Advance，DrawShapedText 缓存入口直接绘制 positioned blob，不重新整形子串或使用 DrawText(string)。这一实现与上游字形绘制流程一致；上下文相关合字／连接字恢复整行结果，字体回退仍按 run 保持。跨独立源元素的字簇明确诊断，不拆字形。

SDK 增加中立身份／目录／源进度及 Token 族几何，SceneFrame 共用实际布局和已有世界矩阵。关联翻译补齐真实 BindingId；SDK 演唱输入复用源 LyricSampler，不以 Static 绘制清零代替源进度或制造缺失时间。原画显示策略保持。1563 项验证与边界见 [R2 记录](../research/scripting-r2-sdk-geometry-progress.md)；0.9.2／21／15／3，完整上下文、writer、prepared frame、生产族缩放及光标仍须继续，R2 未标记完成。

#### 0.9.3 R2 纯帧准备与生产族变换实施记录

按已接受合同实现完整源／场景目录与输出范围分离、类型参数子集、纯 Initialize／Evaluate、变换 writer 和中立准备帧；当前两阶段明确是未完成 SDK 的子集，既有六阶段、订阅／历史、材质／图及视觉协议不缩减。独立纯帧会话串行求值、结束 writer 并释放实例引用，不冒充有状态运行时。

生产渲染从原行移除受影响的固定 glyph，基础字与其全部注音绕基础 Advance 中心执行局部视觉矩阵，保留父组／槽位／单句和源排版。FinalGeometry 使用同一矩阵；准备帧身份门禁和缓存校验拒绝静默无效果回退。WordEmphasis 同一公开作者源码已直接编译运行，三种基础／描边／阴影／Alpha 组合与等价父组整体变换逐字节相同；不变邻字及真实前奏装载通过。

EffectFailureException／PreparedFrameException 已不进入 VideoExporter 普通占位路径；真实编码验证中止、临时清理和原输出保留，普通后端错误仍报告占位。透明背景 intentional bypass 尚待 R3，不把这些错误当作跳过 shader。全量 1585 项通过、0 警告／错误，0.9.3／21／15／3；详见 [R2 记录](../research/scripting-r2-sdk-geometry-progress.md)，整个 R2 及 R3～R8 未完成。

#### 0.9.4 R2 固定帧服务实施记录

九类类型参数已补齐 color／curve／asset；颜色保持直乘，曲线为封闭纯描述，资源句柄仅含固定包／完整修订／导出身份。加载前验证资产参数归属，重复资源声明和跨修订同名资源明确拒绝。固定 UInt64 种子与实例分流的 PCG32-v1 使用纯索引查询，精确编码及官方／独立测试向量进入 [服务合同](../../scripting-r2-frame-services-contract.md)，改算法须提升模拟 profile。

只读音频序列接通初始化／帧上下文：未分析、无音频、失败与已分析静音分开，未知节拍不造相位，半开 EndUs 明确越界。当前是固定服务投影，不是 R5 分析器或有状态会话；不改变既有源时间、工程格式或画面。0.9.4／21／15／3，1598 项通过、0 警告／错误；六阶段、其他 writer、光标及 R3～R8 继续保持原范围。

#### 0.9.5 R2 事件、源订阅与历史实施记录（2026-10-03）

按已接受源／装载／起唱分离、稳定身份和事件排序实施封闭十三类事件，源订阅与输出绑定独立，真实前奏／共享槽位调度投影及不可变历史前缀进入代码。缺时间字、注音、标记和纯组不制造 TokenStarted；准备与演唱沿用 BindingId，入场／可见次数不计入源演唱。步骤共享 Core 商余数整数边界；真实已分析 hop 才生成 AudioFeatureTick。

BoundSources 结合实际槽位装载解析整个时间线，精确注音仅关联基础字；纯提示读前奏事件而不假装目标字，未知／空／重复绑定拒绝。HistoryAt 是受控事件前缀，不是作者实例重放；GetStarts 请求未来区间也只能读取已提供前缀。ReplayReason 不影响 EventId，几何阶段尚未生成 CursorTransition；六阶段、120 Hz 会话和真实 callback 没有在本批接通。

0.9.5／21／15／3，全量构建 0 警告／错误，1629 项通过，新增 31 项及真正编译消费者验证。当前协议与剩余边界见 [事件记录](../../scripting-r2-timeline-protocol.md)；生产画面和持久格式不变，不缩减 R2～R8 或引入旧格式转换，性能基准后置。

#### 0.9.6 R2 元素基线与生产样式帧实施记录（2026-10-03）

按已接受 §5.1 实施完整源元素静态外观投影和限时 Style writer：唯一两态材质、完整渐变、外部描边几何、阴影和 alpha。所有实例读同一基线，明确 Set 后项覆盖、装饰 null 关闭、透明度乘数在全部合并后校验；基线值写入不提前丢弃，无变化最终保留原源／绘制路径。当前是元素族子集，行／组／标记及其他输出继续实施，不冻结或缩减完整 SDK。

原静态矩形覆盖会重画邻字阴影并裁掉装饰／旋转边缘，像素回归先暴露差异。静态独立元素覆盖和脚本输出现共用固定 glyph／Ruby 绘制单元，原画移除变化单元后只画一次；源共享字簇静态策略保持，无覆盖普通画面保持，不重排或修改源时间。Style alpha 与行入场 alpha 各一次，几何仍不包含装饰外扩。生产只执行现有填充渐变和纯色外部描边等已兑现能力，包材质／渐变描边／未支持组合报错；不是透明缺背景 bypass。

0.9.6／21／15／3，全量构建 0 警告／错误，1660 项通过；新增 31 项包含真正 Roslyn 入口、逐字节静态及等价父组对照、前奏／槽位／入场／注音／缺时间／邻字和门禁。数值与诊断见 [样式帧合同](../../scripting-r2-style-frame-contract.md)。R2 其余出口和 R3～R8 未完成，生产 shader／图、callback／120 Hz 与 UI 保持后续范围，性能基准后置。


## ADR-0072：作者 Error 诊断与共享语义会话失败门禁（2026-10-03）

**状态：已接受，0.9.7 实施。** 用户明确回答 Diagnostics.Report(Error, ...) 使本次求值失败并阻止导出；Info／Warning 只记录。宿主在阶段结束检查 Error，作者 catch 或正常返回不改变失败；保留代码、说明、包内逻辑源码位置，以及实例、固定产物、阶段和整数时刻。Reporter 阶段结束即失效；OperationCanceledException 保持取消，不伪装成错误。

按 ADR-0071 的 120 Hz 合同接通同一实例 Initialize→OnEvent／Advance→Evaluate→Dispose。精确源事件加入固定网格，推进左端输入／历史，事件按既有稳定次序逐项纳入工程前缀再回调；源订阅只筛选回调，不裁剪完整历史。任意分数帧不额外推进；后退、取消后再请求从固定输入重建。PureFrameSourceFactory 明确拒绝有状态入口或历史请求，不能静默忽略 callback。真正 RepeatEmphasis 源码复用生产样式／元素族／注音绘制。

中途 Error 的真实编码回归暴露旧目标可能已被覆盖，早期首帧失败证据不足以支持中途安全。编码器现写同目录唯一临时文件，完整成功后才按原覆盖许可发布；失败／取消等待本会话进程和管道退出，再清理临时文件，原目标保留。此修复兑现已接受失败合同，不增加工程格式或历史范围。

本批不是完整六阶段、R5 或脚本工作区交付。AdvanceVisuals／EvaluateVisuals、光标／映射／粒子、行组标记目标、材质图、持久包、实际音频分析和 UI 继续按原路线；详见 [语义会话记录](../../scripting-r2-semantic-session-contract.md)。SDK 1.0.0 仍为拟冻结完整目标，本批程序集版本随应用 0.9.7，编译缓存按实际 SDK 字节断代，不能加载旧接口产物冒充当前入口。


## ADR-0073：真实行／组／文字标记目标与组级外观边界（2026-10-03）

**状态：已接受；0.9.9 实施目标读取及文字标记输出，完整组 writer／合成仍待 R2～R3。** 用户确认「采用组级不透明度＋变换／渲染图」。组没有文字或填充，不增加假已唱／未唱材质、描边或阴影；GroupStyleView 只公开真实静态 alpha（当前源组没有此字段，值为中性 1）。整组改色通过其真实行／文字标记目标；整组特效通过组变换或捕获渲染图。未来组 alpha 作用于隔离后的组合，不能以逐后代乘 alpha 替代组合成。

LineTarget 保留源行和 BindingId；完整行基线按工程→分支→行解析，不读元素覆盖。TextMarkTarget 保留 NodeId＋CueId＋目标 BindingId，同一提示节点服务多个前奏时不能串用装载身份；标记两态对应 Pending／Elapsed，不制造歌词 TokenStarted。固定标记基线解析提示组外观及自身覆盖，包括当前不可见或禁用配置。FrameMark／FrameGroup、固定字形几何、组参考框／运行矩阵／枢轴与后代墨迹进入 SDK；持久父树和跟随目标句的运行父树保持分开。

0.9.9 的文字标记 Style／ApplyAffine 进入限时 writer、跨实例合并、不可变准备帧和真实生产绘制。Project／Branch／Lines／Clips 按目标句关联，Node 按真实运行父树，Tokens 不自动取得整套前奏写权限。伪造 Cue／装载、缺少或重复几何、范围外、失效阶段、能力未声明、非法 alpha 和后端未支持材质明确失败。静态 alpha 和动态附着 alpha 各应用一次，固定整行 glyph 保持，源文字／排版／树不修改；无效果画面保持原规则。

具体能力及剩余范围见 [场景目标合同](../../scripting-r2-scene-target-contract.md)。本批不改生产 JSON，schema 21／15／3 保持；SDK DLL／编译缓存按实际字节断代。R2 两个视觉阶段、行／组／BaseOnly 等其余 writer、转换表，以及 R3～R8 保持原范围；重叠歌词接续尚待原问题答复，性能基准后置。

## ADR-0074：整行视觉仿射沿真实运行父树传播（2026-10-03）

**状态：按已接受 R0 §3／§5 实施，0.9.10 生产路径已验证。** 整行仿射使用真实 LineId＋当前 BindingId 和固定字形逻辑坐标，不修改字体、排版、时间或持久树。同目标按实例／调用顺序右乘；不同层级只沿真实父×子关系组合一次。未变祖先保留原矩阵，避免无变化路径反复求逆；恒等或抵消保持原帧。

SceneAffineProjection 共享中立父树传播，PreparedRenderFrame 保留基础 Scene，并提供实际绘制的 HierarchyScene。注音、运行时附着目标句的前奏组／标记跟随；FixedParent 提示和独立翻译按自身父关系显示。最终行／元素族／注音／标记／组几何与 Playback 使用同一最终矩阵，族／标记独立局部输出再各应用一次；组参考尺寸不随字形墨迹扩大。后端须检查实际 float 矩阵的有限可逆性，不能让 double 可逆但原生下溢的效果静默消失。

Project／Branch／Lines／Clips 和实际 Node 范围可写匹配的行；Tokens 读取所属行不授予整行写权。组输出范围和整行／元素样式覆盖尚未裁决，相关 writer 不在本批默认实现；组 alpha 继续遵守 ADR-0073 的隔离合成。光标重叠后继也保持待答。

全量构建 0 警告／0 错误，1770 项通过；真实 Roslyn 作者整行及组合输出与保持固定字形起点的等价静态整体移动逐字节一致，最终矩阵、未来端点、共享合字、无元素行和严格范围／数值门禁覆盖。生产格式 21／15／3 保持，SDK 缓存按实际字节断代；R2 出口和 R3～R8 尚未完成。具体证据见 [层次输出合同](../../scripting-r2-hierarchy-output-contract.md)、[R2 记录 §3.8](../research/scripting-r2-sdk-geometry-progress.md#38-0910-整行仿射与运行父树验证)和 roadmap §18.39。

## ADR-0075：共享实例的视觉阶段与最终几何圆点输出（2026-10-03）

**状态：按已接受 R0 §4～5／§8 实施，0.9.11 验证通过。** 同一实例执行完整六个作者入口及 Dispose；文字 Evaluate 完成全部实例合并和最终几何后，AdvanceVisuals 推进前一固定区间，EvaluateVisuals 纯呈现。初始 0 不制造零长推进，分数帧不积分／计数；上下文提供真实前一／当前几何与明确的历史前缀，不提供文字 writer 或未来递归查询。输出模式／目标密度变化从 0 重建并记 InputsChanged。受信脚本须遵守共享／视觉字段分区，宿主不承诺检测任意字段副作用。

VisualFrameContext 的最终几何及 Playback 按实际绑定筛选，Unavailable 与合法空演唱分开。纯帧允许纯 EvaluateVisuals，拒绝有状态 AdvanceVisuals；独立 AnchorQuery 只重放语义／文字，不执行两个视觉阶段或 overlay。视觉 Error 与共享阶段一样使求值失败、阻止导出；诊断和 writer 随阶段失效。

当前 Overlay.DrawDots 是实际生产能力：最终画布逻辑点、正有限半径、sRGB 直乘颜色，按实例顺序／ordinal 批次／数组内部顺序绘制；BehindText 在背景后、AboveText 在全部文字／注音／提示后。初始全实例 100,000 绘制项预算按已接受 B、可由固定工厂显式调整，R4 再持久化；超限不裁剪。准备帧保持文字及几何闭包，native 越限／下溢明确失败。

全量构建 0 警告／0 错误，1797 项通过。真实 Roslyn 同实例阶段／120 Hz／精确事件／最终几何、24／30／60 fps／中段／回退／独立会话、透明 alpha／密度／图层及独立 Skia 逐字节像素验证通过；两种前奏附着、无元素行／不可用和严格能力／阶段／预算门禁覆盖。两个视觉阶段的真实编码中途 Error 均保留原输出并清理临时产物。没有 Emitters／粒子／Sprites／Trails 或生产通用图，不把圆点当 R6 完成；R2 其余出口和 R3～R8 保持完整范围，三项待答决策不默认推进。schema 21／15／3 不变；详见 [视觉阶段合同](../../scripting-r2-visual-stages-contract.md)、[R2 记录 §3.9](../research/scripting-r2-sdk-geometry-progress.md#39-0911-视觉阶段与生产圆点验证)和 roadmap §18.40。

## ADR-0076：固定离线基础音频分析与初始化依赖门禁（2026-10-03）

**状态：按已接受 R0 §8.2／ADR-0071 实施，0.9.12 自动验证通过。** R5 音频分支可在包／图尚未完成时独立建立，但不据此关闭完整 R5。Media 复用原 PCM 读取器并公开明确重采样入口；原波形／播放的源采样率入口保持。分析固定 48 kHz、960 样本／20 ms，实际样本决定半开时长，短尾保留，源文件／规范 PCM／算法／零点形成修订；不保留整曲 PCM或新增磁盘格式。

20～200 Hz 用二阶高通／低通与包络近似；归一化参照整曲全频音量，不能把纯高音的微小残余放大成满低频。Pulse／峰检测和近期连续间隔提供近似相位／confidence，没有足够或稳定证据时为空，不把静音或持续音叫作节拍。非有限、空解码、计数不符或检测到源内容变化明确失败；失败无部分修订，取消仍传播取消。

固定 AudioFeatures 与真实 hop 进入原工厂／事件／只读历史；CompiledFrameEffect.RequiresAudio 初始依赖门禁拒绝无源、未分析及失败。**音轨结束后的 required 语义已向用户提问，答复前不新增越界失败规则**；所有查询仍返回真实 OutsideRange／空值。参数／声明／分析修订须进入后续执行闭包指纹，持久包及分析生命周期／UI 后置。

全量构建 0 警告／0 错误，1824 项通过。真实解码、四种源采样率、规则／不规则脉冲、静音／持续音／纯高音、短尾、分块／修订／失败／实际取消验证通过；公开 C# 作者消费真实分析和 hop，24／30／60 fps、中段／回退／独立会话和静态外观＋独立圆点逐字节像素对照通过。具体算法／可用性及尚未裁决的终点策略见 [音频合同](../../scripting-audio-analysis-contract.md)和 roadmap §18.41；完整 R2～R8、性能基准后置的原范围保持。

## ADR-0077：固定 shader 声明与严格帧 uniform ABI（2026-10-03）

**状态：按已接受 R0 §6.2／§8（ADR-0071）实施，0.9.13 自动验证通过。** 固定 SkSL 程序声明由现有 Core 唯一持有；SDK 的 UniformValue／UniformValues 仅为中立帧投影，七类值及同类型定长数组不持有源码／native 资源。没有新增程序集、JSON 或旧格式转换。

正式 Rendering.Skia 的 CompiledRuntimeShader 一次编译固定 profile，通过锁定版原生反射检查实际 uniform 类型、color／数组标记、长度和缓冲区，以及 shader child 类型；不以字节数或源码字符串猜类型。颜色为直乘 sRGB RGBA 和 layout(color) float4／half4；SDK 矩阵按行给值，后端按 SkSL 列顺序打包。完整值集不补零、不沿用上帧，128 标量默认预算可由固定宿主显式调整；实际 float32 溢出或非零下溢具名失败。未知 profile、编译、声明和输入错误不是透明背景绕过。

程序键包含源码、profile、固定 ABI、实际 managed／native 版本和 ordinal 声明。程序拥有 effect，返回 shader 由调用者拥有，输入 child 为借用；取消／失败释放直接持有资源，已创建 shader 的原生引用独立有效。锁定版公共 ToShader 内部复制数据包装依赖终结器，相关限制明确记录，不用私有反射绕过，也不据此关闭完整 R3 资源预算出口。原生名字存储槽与生成 C 头描述不同，经真实测试暴露后复用公共名字查询；适配器只读取正确的类型／数量／偏移字段。

全量 build 0 警告／0 错误，1863 项通过，新增 39 项。真实离线 Roslyn 消费、全部类型／数组及非对称矩阵像素、color 与 raw coverage 颜色空间对照、严格输入／预算／原生数值、取消／释放均验证。仍没有 Materials／ImageEffects／Mappings writer、包解析、生产歌词捕获或通用图执行；R2～R8 保持完整范围。规则与证据见 [shader 原语合同](../../scripting-shader-program-contract.md)／roadmap §18.42。

## ADR-0078：中立图定义与严格 Canvas CPU 图执行（2026-10-03）

**状态：按已接受 R0 §6／§8（ADR-0071）实施，0.9.14 自动验证通过；完整 R3 仍未完成。** 在现有 Core.Effects 保存唯一不可变图定义、所属参数约束、类型／边界及编译计划；不增程序集或 JSON。节点是版本化 RuntimeShader／Blur／Mask／Transform／Blend；全声明校验包括未执行坏节点，确定性拓扑排序支持后定义边，同帧环、悬空／混型端口、错误 uniform／child、参数最大外扩明确拒绝。

正式 Rendering.Skia 的 CompiledImageGraph 固定编译全部引用 shader；帧只输入固定参数／整数时间／逻辑尺寸和具名真实图像。首后端显式 Canvas，颜色输入须是有 sRGB 元数据的预乘 BGRA／RGBA8888，产物 BGRA8888；颜色像素 RGB 超过 alpha 明确失败，raw Coverage 不做颜色转换或颜色预乘判定。逻辑 σ／位移与缩图密度分开，输出框向外对齐实际像素网格；三种 sampler edge、三种 blend、完整 3σ 外扩和 native 有限可逆矩阵严格执行。

图执行按 native 图像身份计费，输入和产物计入单次图预算，中间产物在最后使用后释放；取消／失败清理本图所有权，输入借用及返回产物所有权不混淆。Blur 的两张保守 scratch 预留不是全部 native 分配实测；R0 每帧／每会话全局资源账本及 ADR-0077 的内部终结器限制仍需后续落实，不以本批局部预算关闭完整 R3 出口。

sourceOver 和 replaceCoveredBackdrop 分开，后者使用独立 raw 覆盖、结果不重复乘覆盖，opacity 同时控制产物和替换范围。输入背景依赖不靠名字猜，媒体来源不得声明 none；透明模式按 explicit bypass 返回原 TargetColor、报告原因，后续独立图照常执行；结构和 native 源码失败不能旁路。optional 缺失行为已经询问用户，答复前只报告 GraphOptionalInputPolicyPending，不填透明空图或默认替代。

全量构建 0 警告／0 错误，1895 项通过，新增 Core 10／Rendering 20／Export 2。真实节点／参数／多密度／替换边缘／旁路／预算／取消／释放及实际 MP4／ProRes 4444 同帧验证；覆盖度平方与合成重复计费变异均被回归检出。编码样本明确提供椭圆图像输入，不证明生产歌词 capture；非 Canvas 适配、真实捕获／层级隔离、Material／Graph writer、全局资源和附真实实例的导出失败门禁尚未接入，R2～R8 保持完整范围。具体证据见 [图合同](../../scripting-image-graph-contract.md)／roadmap §18.43。

## ADR-0079：生产固定字形捕获及同实例最后图绑定（2026-10-03）

**状态：捕获按已接受 R0 §6 实施；同实例重复 Bind 由用户本轮明确确认，0.9.15 自动验证通过。** 同实例同目标同帧只保留最后一次完整图／参数绑定；多个实例按工程数组顺序串接，后一个 TargetColor 是前一个产物。不同目标独立，未写目标不扩大范围，不保留上一绑定的参数字段。

FrameWriter.ImageEffects 在 Evaluate 内只写中立不可变描述，需 ImageEffects 能力；Line／TokenFamily／TextMark 遵守现有装载和绑定权限、固定包修订及独立 glyph 分区，Tokens 可读行不授整行写权。组写权仍待答，不从只读组目录开放。PreparedRenderFrame 保留真实实例／编译代次和跨实例顺序，视觉帧复制不丢图描述。

生产捕获复用固定整行 HarfBuzz 绘制单元、最终父树／行／族／注音／标记矩阵，提供真实 TargetColor 和四类 raw 覆盖；显式配方／Canvas 框／实际密度，不按子串重排。Coverage 不以填充／入场 alpha 决定几何，外部轮廓减除与 GlyphCoverage 一致的白色字形。TargetColor 保留原 Render 像素及共享字簇源覆盖的旧 64 px 矩形限制；新 raw 外扩不截 64 px，不隐式修改既有源样式边界。组隔离捕获真实后代，整组 alpha 可在结果上一次合成，不能逐后代分别相乘。

SceneImageGraphProgram 在帧外关联固定资源和已编译 native 图；ExecuteSceneImageEffect 只消费本帧真实绑定，以真实捕获或显式前实例产物构造输入。局部活跃输入去重并在分配前检查预算，自建捕获与外部借用生命周期分开；错误附加真实 InstanceId／ArtifactHash／阶段／时刻，取消保持取消。背景／下层／包图片仍由调用者提供；主画布父子图调度、整组 writer／alpha 输出、全局资源账本、材质包和脚本 UI 尚未完成。直接 Render 遇到未调度图绑定明确失败，不能无声降回原画。

全量构建 0 警告／0 错误，八项目 1922 项通过、0 失败／跳过；真实 Roslyn 行／族／标记最后绑定与链、像素等价、原始外部轮廓、缺时间字、前奏／组／合字和预算／取消／生命周期覆盖，三个变异确认能检出回归。真实歌词／注音 MP4／透明 ProRes 4444 同帧，以及图错误中途阻止编码、保留原输出通过。生产 schema 21／15／3 保持，不迁移旧工程；SDK 按实际程序集字节断代，完整 1.0.0 未冻结，完整 R2～R8 保持范围，性能基准后置。证据见 [生产捕获合同](../../scripting-production-capture-contract.md)、[R2 记录 §3.13](../research/scripting-r2-sdk-geometry-progress.md#313-0915-生产固定字形捕获与真实图绑定适配)和 roadmap §18.44。

## ADR-0080：中间表面共享账与产物生命周期预算（2026-10-03）

**状态：按已接受 R0 §8（ADR-0071）实施，0.9.16 自动验证通过。** 固定 ImageSurfaceBudgetSession／Frame 放在既有 Skia 后端，不增程序集、生产 JSON 或脚本 native API。初始每帧 64 张、每会话 512 MiB 与单图执行限值独立；宿主可共用同一帧账，自动创建的调用账不冒充整个场景协调器。

分配前预留真实密度的 BGRA 像素存储，捕获、节点和合成产物持有预留至释放；图输入借用计费保留至结果 Dispose，透明旁路同样保持。按 native 图像句柄去重，相同存储进入另一帧只增加该帧活动量，不重复会话字节；关闭账本拒绝新申请，不提前销账或夺取外部图像。图峰值预检包括前实例／其他目标仍存活的产物，显式 Blur 两张 scratch 及捕获入场／标记 alpha／外描边 SaveLayer 按实际捕获框保守预留，超限列当前／申请／限制，不降质。

正式绑定适配将资源错误附加实际实例、代次、ExecuteImageGraph 和时刻，取消保持取消。全量 build 0 警告／0 错误，八项目 1938 项通过；跨图保留、跨帧别名、透明旁路、图／合成／临时峰值、密度、错误／取消及归零通过，三项变异确实检出。真实作者增加外扩在 200 ms 触发预算失败，停止编码、保留原输出并回收受管图像。

主画布父子协调器、媒体／下层输入、包材质、R4 持久设置和完整 native 资源出口仍未完成；文字阴影内部 scratch、缓存及上游 ToShader 终结器不能算作已测量像素账。schema 21／15／3 保持，完整 R2～R8 与性能基准后置不变。规则及证据见 [账本合同](../../scripting-surface-budget-contract.md)、[R2 §3.14](../research/scripting-r2-sdk-geometry-progress.md#314-0916-中间表面会话帧账与真实预算失败)和 roadmap §18.45。


## ADR-0081：同目标保留合成范围与捕获排除部件（2026-10-03）

**状态：两条行为已获用户确认；0.9.17 实施合成链与真实背景输入，正式父子调度仍未完成。** 用户确认「每个效果的合成规则都生效，后续普通效果保留先前替换范围」，以及「未纳入的部件仍照常绘制，不受该效果处理」。捕获排除 ruby／stroke／shadow 不等于从主画布删除它们；正式协调器须分区保留，混合配方须重新组装目标，不能直接转用上一次整体颜色。

同目标前一颜色进入后一 TargetColor；最终一次合成保留所有实际 ReplaceCoveredBackdrop 的 raw 范围。覆盖并集为逐像素 max，避免同一抗锯齿边缘重复扣除；最后为 SourceOver 时，额外以最后颜色 alpha 负责范围外合成。最终颜色不重新乘覆盖，opacity 同时作用于颜色和扣除范围。透明背景依赖旁路不新增替换范围，保留独立图已有范围和每个旁路诊断；不从任意 shader 推断几何映射。

CompiledImageGraph.CompositeChain 是固定编译后端原语；CompositeSceneImageEffectChain 要求本准备帧同目标的全部实例及原顺序，错误附加最后真实实例／代次／CompositeImageGraph／时刻。历史范围、并集临时表面及一次合成计入共享帧账，不跨帧／密度／画布／模式，不裁掉历史覆盖；借用结果不夺取所有权。

CaptureSceneBackground 复用正常背景解析、时刻与摆放，BackgroundMedia 不含底色／歌词，BackgroundColor 不含媒体；画布外透明、真实密度和 sRGB。缺失媒体返回 unavailable，required／optional 仍由图门禁唯一决定；透明输出不解析背景。绑定执行适配可自动读取未提供的工程媒体／底色，并按来源去重。

没有新增生产 JSON 或组权限。正式 Render 父子图调度、排除部件保留绘制、BackdropBelow、包材质和 UI 尚未接入；受控单目标编码验证不作为完整协调器证据，完整 R2～R8 和性能基准后置保持。具体规则见 [场景合成合同](../../scripting-scene-composition-contract.md)，验证结果见 roadmap §18.46。

## ADR-0082：真实彩色部件与共同透明度分离（2026-10-03）

**状态：按已确认的捕获排除部件规则（ADR-0081）实施；0.9.18 完成部件及显式顺序重组，自动效果叠放决策待答。** 不新增程序集、生产 JSON、组写权或作者 native API。

基础字／注音各有 Fill／Stroke／Shadow 六类真实彩色部件，固定字形、逐字裁切、最终矩阵及实际两态外观复用生产入口；外描边与 shadow-only 滤镜不猜测白色覆盖或以图像相减还原阴影。仅真实 Line／TokenFamily／TextMark 有字形部件；组由实际孩子形成，Mark 没有注音部件，合法缺装饰为空、失效装载和不可独立字形分区明确失败。

共同合成 alpha 从部件颜色分离：Line／Token 的入场 alpha、Mark 的附着 alpha × 实际样式 opacity 保存原值及生产量化值，重组完成后才应用一次。各部件自身外观 alpha 保持原生产规则。AssembleSceneColorParts 仅接受调用者显式顺序的同源／准备帧／时刻／输出／质量／目标部件，禁止重复或裁掉输入框；产物独立拥有表面，不夺取输入，共同 alpha 尚未烘入。借用输入及输出分配前检查局部／共享预算，错误和取消清理。

新增 Rendering 25 项；全量 build 0 警告／0 错误，八项目 1994 项通过、0 失败／跳过。指定顺序组合验证在三种密度及两种前奏附着下与完整生产捕获每通道差 ≤2；这不代表任意多源单元交错顺序都已验证。正式效果与排除部件的叠放、混合配方、子图到父捕获和真实下层仍待接入；叠放已询问，不默认选择。无效果绘制保持，Render 继续拒绝未调度图绑定，完整 R2～R8 和 native RSS 出口未关闭。规则见 [部件合同](../../scripting-color-parts-contract.md)，验证与完成度见 roadmap §18.47。

## ADR-0083：不可变效果包内容闭包与原子分享（2026-10-03）

**状态：按已接受 R0 §7.1（ADR-0071）实施；0.9.19 完成文件闭包／目录 ZIP 传输和固定源码消费，完整 R4 未完成。** 不新增程序集、生产反向引用或工程 JSON 字段；schema 21／15／3 保持。

Core 唯一保存规范清单和已复制不可变字节，精确检查顶层及 files、声明／实际集合、完整小写 SHA-256、UTF-8、路径段大小写和文件／目录冲突。JSON 对象拒绝重复键并 ordinal 排序，数组及合法数字词形保留；支持版本由宿主显式传入。RevisionHash 包含规范清单及排序文件的路径／Int64 长度／原摘要，以长度前缀消除歧义；不持有机器路径或可写数组。parameters／exports 完整 JSON 先保留，其语义及输出依赖未被此内容类型宣告通过。

Application 读取目录或 ZIP 形成同一集合，拒绝链接，不先解压任意条目；独立 ZIP 分享完整写入同目录临时文件后一次发布，失败及写入中取消保留已有目标。Scripting 从固定 csharp 内容投影到既有离线编译器，不重读原目录；图片修改改变资源修订，不改变同 C#／同工具链编译指纹。测试里的 Application 引用仅用于真实跨模块验证，生产 Scripting 依赖保持。

目录搬走后归档仍能实际编译／加载 IKaraokeEffect 并产生 SDK 变换描述；源码诊断保持文件行列。全量 build 0 警告／0 错误，八项目 2033 项通过（新增 39）。完整参数／导出、图／材质解析、工程历史及内容地址保存／模板依赖、UI 尚未完成，内容闭包不能直接启用；正式画布调度和原决策仍保持待完成，完整 R2～R8 不关闭。规则见 [内容合同](../../scripting-package-content-contract.md)，验证状态见 roadmap §18.48。


## ADR-0084：九类包参数与控制器语义消费（2026-10-03）

**状态：按已接受 R0／本轮编码确认实施，0.9.20 自动验证通过，完整 R4 未完成。** 用户确认 color 为保留 double 精度的直乘 RGBA 对象；curve 为 easing／cubicBezier／controlPoints 判别对象；asset 为本包明确 role:image 的逻辑路径。无包间引用、机器路径或新程序集；schema 21／15／3 保持。

Core 从固定清单读取唯一参数元数据／默认值、完整 long 与类型化颜色／曲线；图沿用同一 EffectParameterConstraint。默认和部分覆盖共用类型／范围校验，未知键、重复键、反向区间和坏曲线拒绝，不夹值／排序修复。控制器声明解析真实入口、允许绑定、具名输出／能力需求和稳定依赖拓扑，悬空／重复／环失败。材质／图导出及旁路尚未完整解析时拒绝整个包，不只接受其中控制器。

Scripting 用源码、完整入口集合、SDK／工具链参与 Artifact 指纹，参数和图片不进入编译指纹。全部真实 DLL 入口检查公共普通类、非抽象／非开放泛型、SDK 接口和公共无参构造；反射不运行作者构造／初始化。Rendering.Abstractions 注入后端实际图片验证，Skia 完整解码检查像素和容量后才建立 SDK 句柄；验证对象随调用释放，不冒充图片绘制缓存。R0 包展开预算在目录／ZIP 文件读取分配前及 Core 闭包复制前执行，包含清单，规范后的闭包再验证；逻辑内容上限不等于内存峰值。

CreateEffect 读取清单默认／覆盖及绑定，映射当前真实 writer；合成前检查文字输出类别，Overlay 批次须有已声明 OutputId。粗粒度 SDK 能力位不能扩展清单目标；非空 requires、未实现 writer、跨输出执行／旁路计划仍明确失败。真实 WordEmphasis 包在两种密度呈现清单默认与覆盖的像素差异，注音同矩阵且原排版保留；九类 Initialize 输入、图片／入口错误及声明外输出门禁通过。全量构建 0 警告／0 错误，八项目 2106 项通过（新增 73）。

包材质／图／shader ABI／捕获配置、输出依赖执行及透明绕过、工程聚合／内容地址事务／历史／模板依赖和工作区尚未完成；主画布协调器、原六项待答及完整 R2～R8 保持未完成。规则见 [包语义合同](../../scripting-package-semantics-contract.md)，真实证据见 roadmap §18.49。

## ADR-0085：独立包 shader 程序声明与完整原生验证（2026-10-03）

**状态：用户确认补充合同 A；0.9.21 实施并自动验证，B／C 未确认，完整 R4 未完成。** 使用独立 role:shaderProgram `.program.json` 保存精确 programVersion、固定 role:shader 源码路径、profile、七类 typed uniform／定长数组和两类 shader child；图引用程序路径，不复制另一套 ABI，也不正则分析 SkSL。包完整资源修订包含程序字节；不改变 project／lyrics／template schema 或新增旧工程兼容。

Core 读取全部声明，包括未引用项，严格校验类型／字段／名字及源码用途。Rendering.Abstractions 增加正式 shader 验证门禁，控制器包有声明却未注入验证器时拒绝；Skia 编译并反射全部程序后才继续 DLL 校验，错误程序不能被未引用或无图 writer 掩盖。固定程序集合逐帧只换值／child，原生程序键包含源码／ABI／后端，包资源修订与同 C#／入口 Artifact 分离；程序文件不公开为 SDK GraphHandle，也不扩大控制器能力。

失败／取消清理直接拥有的已编译集合，释放幂等，返回 shader 与程序各有原生引用；不声明全部 native RSS 已受控。九类参数与 JSON 检查提取为内部共用读取器。全量构建 0 警告／0 错误，八项目 2136 项通过（新增 Application 1、Core 20、Rendering 9），真实像素／归档／启用门禁及资源验证见 [补充合同 §6](../../scripting-package-graph-semantics-contract.md#6-a-的实际实施路径0921)／roadmap §18.50。B 捕获／完整图编码及 C 导出／fallback 待答，混合包解析、主画布、持久事务及整个 R2～R8 未完成。

## ADR-0086：固定包图编码、逐输入捕获及明确执行门禁（2026-10-03）

**状态：用户明确接受补充合同 §3 B；0.9.22 实施并自动验证，C 导出／材质回退仍待答，完整 R3／R4 未完成。** 全部 role:graph 按文件路径进入固定中立目录，引用包唯一参数和独立 shader ABI；图 JSON 显式保存五个捕获布尔值／区域、每节点完整采样边界、封闭值／边、矩阵与输出框、包图片路径及 required／none 原输入旁路。唯一 ImageGraphCompiler 校验拓扑／类型／累计外扩／预算，不增加另一套图规则或旧格式兼容。背景／资源输入不默认捕获，省略字段拒绝。

正式 CPU 门禁验证全部图后才能启用控制器；缺少验证器、错误源码或坏图不能部分启用。程序和图片仍走实际反射／完整解码。PackageGraphImages 显式原始尺寸／密度，经 Transform 移动，不默认铺满；解码集合按明确预算和生命周期管理。SceneImageGraphProgram.FromPackage 将配方逐输入接到真实捕获，按来源＋配方复用，实际注音差分不错误别名。owned overlay／materialRegion／非 Canvas 的实际适配未实施时明确失败；逐输入程序不能把上一整体产物替代全部捕获，跨图部件协调器接续完成。该临时门禁不是修改最终串接语义。

不公开伪 SDK GraphHandle，不解析 C 未确认的混合导出／回退，不改变工程／歌词／模板 schema 21／15／3。真实目录／归档、CPU／图片变换、透明旁路、生产注音差分、取消／释放、MP4／Alpha 同帧及全量 **2171** 项通过，构建 0 警告／0 错误。包材质／完整主画布、部件叠放与既有待答、持久事务、其余 writer／映射／粒子和工作区仍须 R2～R8，性能基准后置。格式、代码与边界见 [补充合同 §8](../../scripting-package-graph-semantics-contract.md#8-b-的实际实施路径0922)／roadmap §18.51。

## ADR-0087：每条合法头独立连续发射与精确出生几何（2026-10-03）

**状态：按已接受 R0 §4／§8 和用户本轮 rate 决策实施；0.9.23 连续基础已自动验证，完整 R6 未完成。** 同一绑定多个合法播放头分别按 rate 发射。实际 `(TrackId, BindingId)` 保留每头配额，不猜测后继；ParticleId 为 `(InstanceId, EmitterId, BirthIndex)`。IEEE double 输入转精确有理积分，整数微秒出生、末端归下一半开区间，同刻按 ordinal 身份排列；分数呈现副本读取，不提交出生。

出生几何在共享 Advance 前以左边界状态解析出生时刻的纯最终文字，源事件已拆出精确边界；不执行视觉、发射或图像，不以边界插值近似非线性变换。粒子保留世界出生坐标、常速度／常加速度及年龄曲线，固定边界／事件拖尾及解析末端采用公共圆点绘制。描述层级统一当前发射器，出生外观／运动保留出生描述；死亡末端不可见。

VisualWriter.Emitters.Set 同身份最后描述生效，CompleteOutput 保存全部输出；旧 Complete 不能丢失发射描述。命名输出和能力／包声明不扩大写权，纯帧拒绝连续发射；AnchorQuery 跳过。存活 20,000／拖尾 200,000 及全实例绘制 100,000 沿用已接受预算，可在固定工厂显式更改；先清理死亡再检查逐出生峰值，不以历史总量误判。错误归属真实实例并阻止导出；卸载和失败清理，不持有作者模块引用。

Jump／SemanticCursor 已实现；Bridge 因待答后继协议、IncludeInTargetCapture 因未完成协调器明确拒绝。Burst、作者粒子服务、sprite／线段拖尾、包／工程持久与完整图层 UI 后续继续，不改变生产 schema 或增加程序集。全量构建 0 警告／0 错误，2196 项通过；真实像素／MP4／Alpha、预算中断及两项回归变异见 [连续粒子合同](../../scripting-continuous-particles-contract.md)／roadmap §18.52。

## ADR-0088：独立纯发射阶段、作者完整粒子与区间出生记录（2026-10-03）

**状态：用户确认独立 EvaluateEmitters 及100000条记录预算；0.10.0已实施并自动验证，完整R6未完成。** 原EvaluateVisuals收集描述后才能解析粒子，不能同次预先提供完整粒子。新增同实例第七个纯入口 EvaluateEmitters(EmissionFrameContext, EmitterWriter)，删除 VisualWriter.Emitters／CompleteOutput；不重复调用视觉方法收集描述，不为旧脚本保留兼容入口。

固定边界先提交上一半开区间真实出生，按原次序共享推进／事件／最终文字，随后纯发射登记、一次粒子准备、AdvanceVisuals及纯绘图。VisualStateContext读PreviousParticles／Particles和完整EmissionSchedule，VisualFrameContext读同一准备粒子及最近已完成区间记录；只含本实例，包括已离场绑定存活对象。分数帧用左端描述和临时副本，不额外回调或推进；AnchorQuery跳过全部发射及视觉，纯帧读取服务明确失败。

EmissionSchedule记录真实区间出生／固定描述，含期末已死亡对象；右端归下一半开区间，初始[0,0)为空，不建立长期粒子历史。用户确认独立EmissionRecordLimit默认100000，全实例每区间计入；固定／临时出生流均在分配前检查，可由固定工厂显式调整，不裁剪或降低rate，错误归真实实例并阻止导出。已有存活／拖尾／绘制预算保持。

CursorTrail完整摘要清单、参数／默认／覆盖、目录／.mkfx离线编译与新入口调用、真实帧／像素和编码验证通过。程序不兼容升MINOR至0.10.0，持久schema21／15／3不变，精确程序版本门禁保持；SDK1.0.0未冻结、编译指纹包含真实SDK字节。构建0警告／0错误，2213项全通过；实际证据见 roadmap §18.53和[发射阶段合同](../../scripting-emission-stage-proposal.md)。Burst／桥接／sprite／实际映射、主画布、包材质／持久事务／工作区及既有待答继续原路线。


## ADR-0089：真实事件 Burst、四类粒子来源及边界出生顺序（2026-10-03）

**状态：用户已确认全部六项边界，0.11.0 已实施，2244项全通过，构建0警告／0错误；完整 R6 未完成。** 每个匹配锚点独立 count，同事件同发射器最后登记；明确源元素、源行、真实文字标记或绑定内全部播放头。指定锚点无当前最终几何时报错阻止导出；非法身份与输出越权独立报错，合法无人演唱的播放头空集合零出生。

EventContext.Bursts.Queue 只接收当前真实事件意图；事件／历史仍按原次序执行。所有实例文字输出完成后解析最终 Advance 中心或播放头，同刻先 Burst，再连续到期出生，共用 ParticleId 的 BirthIndex，不改连续配额。物理外观与运动采用 SDK 封闭 ParticleDescription，固定到每次出生；同 emitter 沿用最近登记 Layer，不制造第二套图层或积分时基。

ParticleSample／ParticleBirthSample 保存封闭 ParticleSource.Cursor(TrackId, CursorIdentity)、Token(TokenFamilyTarget)、Line(LineTarget)、TextMark(TextMarkTarget)。BindingId 来自真实来源；只有 Cursor 有 TrackId；只有 Burst 有真实 EventId。禁止为标记伪造光标或源演唱元素。公共 SDK 更改升 MINOR 至0.11.0，schema21／15／3 不变，精确应用版本门禁保持，未冻结 SDK profile 的缓存包含实际字节。

边界出生立即存在于本帧 Particles，上一 [a,b) 记录不包含 b，下一 [b,c) 接纳一次；已死亡出生仍留完整记录。副本读取不重复事件或提交出生，记录／存活／拖尾／绘制预算统一全实例检查。AnchorQuery 接收语义回调和登记校验但不解析视觉意图；失败及重放清理。公开实际 EventBurst 源码、真实像素／编码和错误保护由 [合同](../../scripting-event-burst-contract.md)及 roadmap §18.54 记录；完整脚本 UI、保存事务、桥接／映射、材质C与原待答继续路线。

---

## ADR-0090：显式组写权限与真实运行父树仿射（2026-10-03）

**状态：用户已确认仅工程／显式节点可写组；0.11.1 已接通，完整 R2～R8 未完成。** Project 允许真实组，Node 允许自身真实组，includeDescendants=true 再允许本帧运行后代组；Branch／Lines／Tokens／Clips 不授予组写权。组目录可读不意味着可写。变换组本身自然影响全部运行后代，includeDescendants=false 只限制后代是否能单独写入。

Transforms.ApplyAffine(GroupTarget, Affine2)、FrameOutput.GroupTransforms 与 PreparedRenderFrame.GroupTransforms 使用组自身参考空间；同目标按调用及实例次序右乘。SceneAffineProjection 在实际运行父树上将组、槽位、整行、元素族及标记各组合一次；动态附着前奏跟随当前文本，固定提示按固定父组，翻译按自身所属树。恒等／精确抵消保留原帧；源数据、字形、调度和绑定不改。

最终行／族／标记／组参考框与墨迹、播放头／未来端点、生产捕获及粒子出生共用几何；视觉和图绑定帧复制保留组输出。明确范围／能力／阶段／包声明、父树／重复／不可逆／溢出及原生 float 退化门禁，隐藏空组也校验。父子组合失败附加到造成错误的真实实例。旧构造签名保留，新增能力交付 PATCH 0.11.1，schema21／15／3保持，SDK 内容指纹继续使缓存断代。

真实脚本、静态父组逐像素对照、两种前奏附着、父子组合、多实例与独立会话、粒子世界出生和 MP4／Alpha 同帧及中途错误保护已定向验证。详细合同与最终全量证据见[组仿射合同](../../scripting-group-affine-contract.md)／roadmap §18.55。组 alpha 隔离合成、组图 writer、整行 Style、包材质 C、主画布协调器和工作区继续原路线，不因本批关闭完整阶段。


ADR-0090接续证据：0.11.2修复真实控制器包创建时的旧group仿射门禁遗漏；既有权限／矩阵合同不变。真实包归档、生产像素及MP4／Alpha路径已验证，全量2286项、构建0警告／错误，见roadmap §18.57。

## ADR-0091：混合包导出编码与保留部件的最高层叠放（2026-10-03）

**状态：用户明确接受合同 C 和最高层规则；代码尚待实施，不作为 R3／R4 完成证据。** 材质／图包编码按 [补充合同 §4](../../scripting-package-graph-semantics-contract.md#4-已确认-c导出与材质回退编码待实施)：允许控制器、材质和图混合导出；材质必须提供包内纯色 #RRGGBBAA 或完整现有 GradientSpec 回退；背景依赖图必须提供与图根一致的显式原输入透明旁路。全部声明完整验证，未实现的组合不能被部分启用掩盖。工程／歌词／模板的持久数据断代仍按 R0，不因本次授权提前更改。

图处理后融合图片放在所处理部件中原本最高的层级，未处理部件保留相对顺序。只处理基础填充时，基础描边／阴影在下、注音在上；同时处理基础和注音填充时，保留的装饰在融合图片下方。这个明确结果可能改变重叠部件的外观，不能将融合图片伪造为独立六部件、重复画原基础字或默选另一层级。共同 alpha 一次合成、已有替换范围、真实父子捕获及透明跳过规则保持，具体见[叠放合同](../../scripting-retained-parts-stacking-proposal.md)。

接续按该合同实施主画布协调器和包语义。重叠光标接续、行／元素同属性顺序、音轨外 required 和 optional 图输入缺失仍需原待答；本确认不替代它们，也不增加旧版兼容。完成状态由 roadmap 记录。

## ADR-0092：统一三类包导出与真实资源消费接缝（2026-10-03）

**状态：按用户接受的补充合同 C／ADR-0091 实施，0.12.0 统一语义、资源目录和显式生产消费已验证；完整 R3／R4 未完成。** EffectPackageDefinition／CompiledEffectPackage 取代原控制器专用公开类型，不保留别名。控制器、材质、图按清单顺序保存且跨种类 ID 唯一，共用唯一九类参数、输出及依赖拓扑；图路径引用同闭包 role:graph，全部图参数必须由导出声明。材质唯一 region 图输出、fill／stroke 允许区域和 materialFallback／identity 明确；背景依赖图的原输入旁路须与根 bypassInput 一致。

回退严格为 #RRGGBBAA 或完整现有 GradientSpec；不补字段、alpha、端点、排序或转换枚举。正式后端先验证全部程序／图／图片及回退支持，再进行全部 C#／入口门禁；不支持的 Glyph／Progress／Oklch 不偷偷降级为纯色。无 C# 的纯资源包 ControllerArtifact 为 null，不制造 DLL；有源码时即使无控制器也编译，失败资源目录为空，参数／回退／实例消费不能部分启用。ResourceCatalog 的材质和图来自真实导出 ID，不来自资源文件路径。

真实控制器 ImageEffects.Bind 接到图导出，运行时同时检查控制器／图的目标、绑定类别、完整参数集和类型／范围，固定句柄不能扩权。当前 ColorOnly 单一同类图输出可经生产准备帧和真实捕获执行；requires、跨输出或多个同类具名执行、非 ColorOnly 实际映射仍明确失败。CompiledPackageGraphExports 帧外持有全部实际 CPU 图和图片，按固定修订选择程序，使用每输入自己的配方；资源集合本身不代替作者源码门禁。返回程序／图片借用集合，调用方按明确生命周期释放。

GetMaterialFallback 仅显式投影已验证的基础外观；正常包材质区域绘制仍报 UnsupportedMaterialBackend，通用主画布仍报 SceneImageEffectsCoordinatorPending。回退、原输入旁路及唯一行显式编码验证不代替最高层叠放／排除部件自动重组、父子捕获／真实下层、完整 native RSS 资源出口、工程／模板持久事务或工作区。已接受最高层叠放保持，原四项待答不选择默认。公共宿主类型不兼容更名按1.0前规则升 MINOR；工程／歌词／模板 schema21／15／3不变，不转换旧版本工程。

真实混合包参数／句柄／权限、目录与 .mkfx、纯包／失败包、完整渐变回退、CPU 图像素和 MP4／Alpha 同帧证据见[补充合同 §10](../../scripting-package-graph-semantics-contract.md#10-c-实际实施接缝0120)／roadmap §18.58。沿用已有缓存构建授权，无依赖变更；本轮未完成在线依赖恢复／审查。


## ADR-0093：主画布字形图协调、融合来源及无目标输入全替换（2026-10-03）

**状态：用户确认两项边界，0.12.1 元素／整行／标记主 Render 已实施；完整 R3 与 R2～R8 未完成。** 后续图不能只选择既有融合图片的部分来源；任意输入或替换范围发生该冲突时，拒绝整个组合，提示调整效果顺序或捕获范围。没有实际目标颜色／覆盖输入的图替换绑定目标全部部件，不猜保留范围。已有最高层叠放和原输入透明旁路合同保持。

固定 SceneImageEffectResources 借用帧外程序／图片，Render 消费不可变准备帧，不解析包／编译作者源码。实际部件带来源集合，元素图先完成并从剩余行 glyph 排除，整行图读取孩子当前结果；独立配方可继续完整捕获融合结果，不将单一整体图片冒充六个通道。每目标内按原实例顺序，融合放于最高原部件层级，未选部分保留相对顺序。

对象合成前取得真实下层，各实例读取同一下层；历史替换覆盖持续有效。共同入场／标记 alpha 在完整对象与原下层之间混合一次，覆盖和 alpha 不再逐部件乘入。捕获、下层快照、输入／图／重组／合成使用同一会话帧账，错误归真实实例，取消／失败释放中间对象，不输出无效果替代帧。无图对象保持旧路径，有图对象按批准的独立部件层级，不能据此承诺任意交错场景和旧复合滤镜逐像素相同。

透明背景依赖图只恢复指定实际输入，保持原部件顺序，不新增融合层或替换覆盖。RenderFrame 中立提示保存真实实例、图和输入；缓存保留，导出报告按固定身份去重。未传固定集合报告 ImageEffectResourcesRequired；未支持的真实运行后代、自身后置粒子下层排除及非 ColorOnly 映射明确失败。容器组图／alpha 隔离、跨行父组、owned overlay、包材质区域与持久工作区继续后续。

宿主构造兼容扩展交付 PATCH，不新增 JSON；schema21／15／3和精确应用版本门禁保持，仍不转换旧工程。完整合同、像素／真实源码包与 MP4／Alpha、失败保护证据见[主画布合同](../../scripting-main-canvas-composition-contract.md)／roadmap §18.59；其余四项待答不使用默认值。

## ADR-0094：组共同透明度与嵌套主画布隔离（2026-10-04）

按已接受 R0 §6.3、ADR-0073／0090 实施真实组 alpha，不新增 JSON、项目或源属性。Style.MultiplyOpacity(GroupTarget, factor) 仅工程／显式节点可写，因子有限非负、按调用和实例相乘、最终检查[0,1]；组基线仍仅静态Opacity，当前为1。真实包需 StyleModulation/group 声明，不能从 Style 能力或只读组目录推断权限。

有效组在第一项原绘制位置成为连续单位，孩子按源绘制序而非树槽位序；嵌套父子逐层先完成孩子真实行图／原字形，再用外部下层与处理后画面的线性混合应用共同alpha一次。替换下层范围随同淡出，下层不叠两次。交错顺序差异作为真实实例诊断进入帧、缓存和导出报告。恒等输出维持原路径。

宿主合成程序独立于作者固定图资源；工作表面／下层／产物、混合及显式文字图层共用帧账，预算和native失败附加真实组或子图归属，取消释放。组图 writer、可按配方读取的父捕获、owned overlay、自身粒子排除和完整native scratch继续后续。独立原始 TargetColor 涉及范围内有效组alpha时明确失败，Coverage保持原始几何；不能以背景截图冒充目标前景。

兼容公开SDK／宿主扩展交付0.12.2 PATCH，schema21／15／3和精确应用版本门禁保持，不转换旧工程。合同／验证见[组隔离合同](../../scripting-group-opacity-isolation-contract.md)／roadmap §18.60。


## ADR-0095：可传递前景与下层擦除产物（2026-10-04）

**0.12.3 已实施独立CPU接缝；父图捕获语义三项仍待答，完整R3未完成。** 现有图合成 `C + B × (1 − E)` 分离为拥有的颜色及raw擦除两张表面。同目标历史替换仍取max，最后普通输出与其alpha取max；独立孩子按绘制顺序展开为 `C2 + C1 × (1 − E2)`、`E2 + E1 × (1 − E2)`。两次80%擦除实际为96%，不能当作同目标历史max。共同alpha同时缩放前景与擦除一次，不将背景截图传递为孩子前景。此为既有合成的代数展开，不替用户决定父图继承策略。

产物复制而非借用原结果，图／输入释放后仍可由另一固定图应用；局部及共享预算同时校验，完整框／密度、别名、跨帧、旧累积产物与新成对分配纳入峰值。固定宿主程序帧外编译，失败／取消回收；非零float32下溢明确失败。没有新程序集、源字段、JSON或作者native权限，兼容宿主扩展升PATCH，schema21／15／3及精确版本门禁保持。

父配方部分选择共同alpha孩子、无可见孩子组图行为、父图是否继承实际擦除仍已询问未获答，依赖行为未实施；已有融合不可拆、最高层、无目标输入全替换和组写权保持。合同／证据见[父捕获产物合同](../../scripting-parent-composition-contract.md)／roadmap §18.61。


## ADR-0096：材质显式覆盖模式与真实区域基础外观（2026-10-04）

用户已确认：材质输出由宿主限制在真实填充／外描边区域，扩散用行／元素图；TargetColor 读取包内纯色／完整渐变基础外观；输出明确区分未覆盖颜色与已覆盖图像。材质导出必填 coverageMode:unmasked／masked，不猜测或迁移旧编码，当前源码示例及测试 package profile升至2；不兼容包编码升MINOR至0.13.0，工程／歌词／模板schema21／15／3保持。

unmasked 对颜色及独立替换覆盖各乘一次区域；masked 验证实际alpha不超区域后复制，不二次乘蒙版。零区域严格透明，非零区域只允许现行BGRA8一个字节舍入，不修正作者输出。真实基础外观先绘白色固定glyph覆盖再应用纯色／渐变，避免Skia彩色字体抗锯齿gamma与raw覆盖不一致；渐变alpha只有一个真值。实际逐字互补裁切、元素仿射、注音和外描边均沿用生产几何。透明依赖背景材质跳过整张图并使用自己的已覆盖基础外观，与材质模式和普通图旁路无关，保留诊断。

包句柄／区域／完整参数／资源与映射门禁、输入未知键、真实密度／网格／像素及代次检查、独立产物生命周期、局部／共享峰值和取消回收保持。此批是生产输入和独立原生包执行接缝；PackageExport准备帧和主Render自动调用尚未接通，工程持久化及完整R3／R4未完成。合同见[区域材质](../../scripting-region-material-contract.md)，证据见roadmap §18.62。

## ADR-0097：包材质逐区域归属与正式主画布接入（2026-10-04）

依据已接受R0／R3及ADR-0096接通生产路径，不新增JSON、程序集或作者native权限。FrameEffectHost分别跟踪未唱／已唱／描边最后Set；非材质属性不改变来源，恢复纯色／关闭描边移除相关绑定。准备帧保留完整StyleView及真实材质描述，缺少／重复／不匹配归属拒绝。当前LyricStyle适配只作明确几何种子，普通最终样式读取和原始彩色捕获不返回假白色。

基础字／注音及标记沿用固定glyph、最终矩阵和互补逐字裁切。主画布先完成材质，再供元素与整行图读取真实彩色部件；材质颜色及替换覆盖各应用一次局部alpha，共同对象／父组alpha依既有隔离规则。透明依赖背景的材质整体跳过并使用包内基础外观，独立材质身份提示经缓存与导出保留。不改变之前图链历史替换范围、最高层叠放、融合不可拆规则。

固定SceneMaterialProgram／资源集合借用真实包而不伪造GraphHandle，所有表面进入既有帧账；错误附加逐属性实际实例／artifact，取消／预算失败释放，导出中途失败不覆盖旧产物。兼容SDK／宿主扩展交付0.13.1PATCH，schema21／15／3及profile2不变。组图三项及原四项待答、其余writer／映射、跨输出／持久事务和UI仍未完成。合同／证据见[区域材质](../../scripting-region-material-contract.md)／roadmap §18.63。


## ADR-0098：直接渐变描边与普通渐变单次透明度（2026-10-04）

按已接受R0 §5／§6.1实施现有SDK与Core描述，不新增数据格式、程序集或依赖。SetStroke对真实元素族／文字标记接受已支持GradientMaterial；Core StrokeSpec.Gradient提供颜色时允许Color=null，宽度及外部位置、连接方式保持。基础字／注音仍按固定字形及原排版绘制，标记保留真实装载身份，不伪造行或元素。

渐变shader的采样色标已包含style.Opacity，普通填充和描边画笔因此使用不透明白色，不能再次乘历史纯色alpha或style.Opacity。此修正会改变原先偏淡的半透明支持渐变像素；无渐变纯色及包基础外观独立覆盖保持。入场／对象／父组alpha继续沿既有共同图层各一次。支持范围为Linear／Radial、Canvas／Line、LinearRgb／Hsv，非法组合／原生角度或实际径向退化明确失败；描边shader不可用不回退或隐藏。

程序升PATCH0.13.2，schema21／15／3及包profile2不变，精确版本门禁保持，不转换旧工程。完整R2～R8、组图／父配方待答及其余接口／持久工作区不在本批交付。设计及实际验证见[样式帧合同 §5](../../scripting-r2-style-frame-contract.md)／roadmap §18.64。


## ADR-0099：显式部位／图节点 uniform 与有序材质写入（2026-10-04）

用户本轮确认三项规则：图节点 ID＋uniform 名字定位，不要求整图名字唯一；显式选择 UnsungFill／SungFill／Stroke，多个部位分别调用；按调用／实例顺序，每次材质 Set 清除该部位旧覆盖，后续 uniform 必须匹配当时真实材质。只有真实元素族／文字标记、Style 能力和现行目标写权获得作者入口，不赋予 native 句柄、源码编译或源工程写权。

StyleUpdate 保留封闭材质操作序列；宿主逐实例校验全部写入，再以静态基线和前实例输出解析身份。只写 uniform 的后实例须拥有同一包完整修订；不同句柄或无包材质明确失败，后面的 Set 不能隐藏坏的早期操作。最终 PackageMaterial 保存不可变逐节点覆盖和原始完整参数，准备帧按部位记录最后材质／uniform 写入者，后续其他样式属性不抢归属。中途材料解析不校验最终 alpha，既有所有实例合并后检查保持。

原图／shader 声明仍为唯一 ABI；未知节点、非 shader 节点、未知名字、类型／数组、实际 float32 越限及非零下溢拒绝。节点执行先用本帧覆盖，随后原字面量／参数／宿主值；不修改参数或资源修订、不重编译。透明背景依赖回退前仍校验所有覆盖，不能借忽略效果导出坏协议。

增量 SDK／宿主 API 交付 PATCH0.13.3；工程／歌词／模板 schema21／15／3、包profile2与精确版本门禁保持，不转换旧工程。完整R2～R8仍未交付，组图三项和其余四项待答不默认。具体合同／API／验证见[材质uniform合同](../../scripting-material-uniform-writer-proposal.md)、公开SDK及roadmap §18.65。

## ADR-0100 — 工程入口最后发布与不可变歌词修订（0.14.0）

**2026-10-04；按已接受合同A§7.3实施。** 用户同日更新目标，新的脚本决策可参考已有文档按代理最优理解执行；原明确选择继续有效。此前悬空项不再强制等待新答复，但必须完成设计及真实实现后才能标为交付。

工程schema22新增必填lyricsFile，严格指向data/lyrics/<64位小写SHA-256>.json。哈希是实际UTF-8歌词DTO字节的SHA-256；歌词schema15、模板3及包profile2保持，程序不兼容MINOR0.14.0，精确版本门禁／拒绝旧工程保持。根lyrics.json不读取、不新写、不删除；历史修订及失败留下的未引用对象无自动GC。

先发布歌词内容地址：已有文件必须逐字节一致；唯一同目录暂存、Flush(true)、验证实际暂存字节后以不覆写方式发布。并发相同修订只在完整比对后复用。根project.json最后使用唯一临时文件原子替换，成功后ProjectWorkspace才更新路径和saved reference。读取根入口一次供版本／schema／引用共用，歌词hash与解析也共用一次读出的字节；UTF-8标记、注释与尾随逗号保留原解析容忍度，hash仍涵盖全部真实字节。

非法路径、data／lyrics／文件链接、缺失文件、hash不符或JSON／schema坏数据使结构性加载失败Project=null；不恢复为可保存空歌词。语义非法草稿仍保留原值及诊断。内存Project不增加磁盘路径真值，引用只在入口DTO。AutoSaveStore及另存为复用同一协议，每份歌词闭包独立。保留既有备份份数策略，不追加修订GC。

本批仅完成歌词与入口的事务接续，不声称已有媒体收拢已成为内容地址事务，不声称效果包／分析闭包和模板资源保存完成。不新增程序集或依赖；长ProjectStore解析拆为内部ProjectJsonReader，未改变依赖方向。具体合同见[存储合同](../../scripting-project-revision-storage-contract.md)，实际验证见roadmap §18.66。

## ADR-0101 — 工程效果闭包、实例顺序与真实控制器投影（0.15.0）

2026-10-04；依据已接受合同A §3／7及当前脚本目标的决策授权。工程schema23新增必填effects，固定包、顺序实例、模拟版本与预算构成不可变Project.Effects；歌词15／模板3／包profile2保持，不新增程序集或依赖，不转换旧工程。

六类绑定与三类独立订阅按合同编码；seed用16位小写十六进制保留UInt64全范围。参数保存排序的不可变JSON对象，重复字段拒绝、整数／RGBA精度不量化；唯一EffectPackageDefinition.ResolveParameters负责九类语义，不另造解析器。语法合法的坏参数、编译失败源码和失效源引用保留供修复；启用非法实例为Error，禁用实例为Warning。包字节、身份、修订或入口结构坏数据不能恢复为空效果工程。

包目录effects/packages/<hash>持有规范清单和全部声明字节，不含DLL／PDB。共享目录读取移入Core.Persistence，Application目录分享复用它；原ZIP协议保持。保存逐文件刷盘、重新固定闭包核验，唯一暂存目录不覆写发布；已有修订只校验。先包、再歌词、最后根入口，失败不改旧入口；另存为及每份备份独立拥有闭包。链接／路径／缺失／多余文件／哈希／身份严格门禁，不删除历史字节或做GC。

SetProjectEffectsCommand一次原子历史恢复；同内容返回原工程。包注销拒绝全部引用（包括禁用实例），复制只生成源实例新ID／seed，接受修订只升级显式选择实例。源对象删除保留失效绑定。Snapshot自动持有源闭包和指纹，启用实例缺少准备帧时明确失败。CompiledProjectEffects按真实修订复用离线编译，按实例数组逐一投影真实绑定／订阅／参数／种子；任一源码失败不创建部分会话，不回退旧DLL。视觉／粒子预算进入真实会话；表面预算以GraphBudget供正式资源组合根使用。

本批宿主投影支持既有控制器能力；纯材质／图包可注册存储，直接实例和跨输出计划仍须后续实施。音频分析修订与输入事务、完整媒体／模板闭包及工作区仍未交付；不因此关闭R2～R8。合同与实际证据见[scripting-project-effects-contract.md](../../scripting-project-effects-contract.md)／roadmap §18.67。

## ADR-0102 — 完整媒体与音频分析内容地址及固定输入（0.16.0）

2026-10-04；依据已接受合同A§7.3、基础音频合同和当前目标的决策授权。工程schema24必填media与audioAnalysis；程序不兼容MINOR0.16.0，歌词15／模板3／包profile2保持，无新增程序集或依赖，不转换旧工程。

媒体实际字节SHA-256、长度和原文件名组成MediaFileRevision；受管位置唯一为data/media/<hash>.media，原生解码按真实内容探测，界面取originalName。源领域值不依赖SDK。保存／另存为／每份备份全部收拢音频及背景，不持久外部绝对引用；流式复制、唯一暂存、刷盘、验证副本及源未变，不覆写已有内容地址。默认单媒体16GiB／分析256MiB，可通过ProjectStoreLimits明确修改；超限失败，不裁切。

已接受分析保存原算法、48kHz／960样本、零点、源／PCM hash、实际样本数和完整double hop序列。AudioAnalysisProfile复用原语义身份；规范UTF-8文件hash另覆盖全部特征值。入口noAudio／notAnalyzed／failed／available明确；实际无流与失败分别保存availability及原因，不发布伪静音或部分序列。available源hash必须等于本次音频修订，文件结构、profile、时间网格、数值、hash及revision严格验证；加载不调用分析器。资源先发布再包／歌词，唯一入口最后替换；失败不动旧入口及saved reference，历史和未引用对象保留，无GC。

SetAudioAnalysisCommand通过一次工程历史接受结果；换源清除描述与分析，撤销恢复。CompiledProjectEffects新增整个Project投影，固定原AudioFeatures／事件／历史及完整输入指纹，不使用播放器状态。控制器requires:[AudioFeatures]复用原RequiresAudio；按初始化已有完整修订解释required，输出超过音轨不制造新失败或静音，查询仍OutsideRange／Values=null；作者Report(Error)仍阻止导出。纯分析值修改需新会话，但复用同一C#编译身份。

这批覆盖存储与真实控制器输入；模板资源、自动资源组合、分析／脚本工作区、直接图／材质实例、跨输出及其余R2～R8能力继续。设计与实际证据见[存储合同](../../scripting-media-analysis-storage-contract.md)／roadmap §18.68。


## ADR-0103：模板效果配方、独立资源与幂等套用（2026-10-04）

**状态：按已接受合同A§7.4与当前脚本目标授权实施，0.17.0。**

StyleTemplate保存静态属性、有序EffectRecipes和恰好所需的完整包修订。配方只表达相对line／tokenFamily、导出、参数和图元层，不保存工程目标、实例、随机种子或会话。元素模板不接受行／组输出，直接材质不伪装为配方；仅Overlay可覆盖层。纯效果模板允许空静态属性，完全空模板拒绝。

模板库断代为templates-v4/index.json schema4及packages/<hash>，旧templates.json不读取、转换或覆盖。包先发布、入口最后原子替换，坏入口／资源阻止写回。全部文件共享预算，资源身份／完整哈希／profile／路径／参数严格验证；删除只改索引，历史资源不GC。.mktpl ZIP具有独立内容闭包，非法／多余／缺失条目拒绝，取消和失败保留旧产物。

工程schema25在实例保存nullable templateOrigin和overlayLayer。单绑定目标／templateId／recipeId构成套用槽；owner行／元素用于清理已移除配方和原行成员。重套用保持ID、种子、enabled，更新参数与固定修订，按配方顺序重排本次块，其他模板／直接实例相对顺序不变；完全相同不入历史。静态与效果一次应用／撤销。显式清除只删该模板来源；直接复制清除来源成为独立实例。工程完整包不依赖原模板库。

宿主实际应用层覆盖到视觉圆点、连续描述与事件Burst；ParticleSample.Layer公开本帧实际批次层。声明／写权／预算／出生与重放规则保持。现有工坊改名／编辑／复制／删除保存配方与资源，静态预览范围持续可见；完整配方编辑、分享入口及效果画布按R7接续。图／材质直接实例、MaterialSpec源格式、跨输出计划等尚未交付，不把R4此切片标作完整脚本完成。

全量遇到Roslyn内部Emit异常、单组80项复跑通过后，编译入口收口为进程内可取消串行门禁及concurrentBuild=false，选项进入产物指纹；原源码诊断和取消保持，不吞内部异常。六次并发相同复杂SDK源码验证DLL／PDB一致，根因结论不超出证据。

格式和验证见[模板合同](../../scripting-template-recipes-contract.md)，完成证据见roadmap§18.69。没有新增生产程序集或第三方依赖；Export测试额外引用既有Application验证真实分享链。工程／歌词／模板25／15／4，包profile2，旧工程不迁移。


## ADR-0104 — 源材质唯一真值、完整覆盖与资源归属（0.18.0，2026-10-04）

状态：依据已接受合同A§6.1／§7及当前脚本目标授权实施；本批为M1，M2／M3继续。

Core使用独立MaterialSpec判别值：Solid、完整Gradient、固定PackageMaterial。填充仅保存一个材质；描边几何单独保存，材质整体覆盖。颜色／渐变旧源码属性仅为访问外观；渐变颜色取／修改首色标，关闭得到首色标solid，无隐藏旧颜色。包材质不提供伪造纯色，旧颜色编辑不能悄悄替换它。恢复任一填充编辑地址恢复完整材质继承，两种地址不再是独立持久覆盖。透明solid不绘制可见描边，但StrokeCoverage按正宽度真实几何生成；彻底删除几何使用stroke:null或零宽。

当前源JSON严格区分solid／gradient／packageMaterial，不接受旧color＋gradient、混合变体、null填充、缺失／不完整或非法材质对象。颜色为#RRGGBBAA；渐变完整字段／合法枚举／排序色标明确校验，不修正坏数据。描边为geometry＋material，覆盖几何可逐项，材质整体。程序MINOR0.18.0，工程26／歌词16／模板5，包profile2保持，不迁移。个人库templates-v5保留v4和旧templates.json。

源基线、分支／行／元素及倒计时／标记的材质与效果实例共用effects.packages。SourceMaterialCatalog是闭包、语义校验和注销的统一入口；材质须引用真实material导出、支持区域和合法参数。缺失修订拒绝打开／保存并保护旧入口；非法参数或语义引用保留原源值及诊断，不能退回静态颜色。模板完整存储样式（含未勾选属性）与配方共同决定恰好完整包闭包；分享／套用／保存仍资源先发布，入口最后替换，历史不GC。含源样式的注销使用Project重载；低层ProjectEffects重载只知道实例目录，不应由工作区直接使用。

M1只交付模型／持久／闭包与静态编辑保护。源包材质的正式准备与渲染尚未接入，CompileAsync(Project)、基线投影及Render／捕获共用门禁报SourceMaterialPreparationPending；不伪造脚本ID／DLL，也不返回无效果成片。工坊保存、改名、复制保留材质／资源；旧颜色控件只读，未就绪预览留空并持续提示。既有脚本帧写入材质与uniform继续真实路径。M2将区分真实源归属与真实脚本归属，固定资源预先验证；M3接R7材质参数与明确替换操作。具体见[源材质合同](../../scripting-source-material-contract.md)，状态／验证见roadmap§18.70。完整R2～R8保持未完成。

## ADR-0105 — 源材质固定投影与真实运行归属（0.19.0，2026-10-04）

状态：按已接受合同A／源材质合同M2及当前脚本目标授权实施；完整R2～R8保持未完成。

CompiledProjectEffects.CompileAsync(Project,...)验证启用实例和全部存储源材质的包修订并集，包含不可见及被覆盖对象；同修订固定资源只验证一次。材质导出、真实区域和完整参数在会话前解析，PreparedSourceMaterials固定快照指纹及原始存储路径。纯材质包无需C#控制器或假实例；低层ProjectEffects重载仍不推断源样式。改变源／参数拒绝复用旧工厂，不在帧内解析包或编译shader。

准备帧／透明诊断使用PreparedMaterialOwner.Source或Script，Source保留SourceMaterialReference及显式部位，Script保留真实EffectInstanceId／ArtifactHash。Source的实例／编译代次访问值为null；材质错误使用SourceMaterialFailureException附真实路径、包修订、阶段及时刻，阻止导出并保护旧成片。透明导出使用包内基础外观，缓存和导出提示保留真实源身份。此前Host协议消费者须处理两种来源，不能假定全部材质来自脚本。

继承源按基线→分支→行→元素以及提示组→标记实际材质写入层确定；描边几何覆盖不抢材质来源，前奏路径使用真实pendingFill／elapsedFill。BaselineStyles返回真实MaterialHandle和参数，只有显式DrawingStyle使用白色几何种子。源绑定进入现有元素族／标记区域Render，基础字、注音、描边、共同alpha、下层及透明回退沿同一路径。后续脚本Set／uniform仅在真实写入后接管该部位，alpha／阴影不抢来源；原有显式未唱／已唱／描边及节点ID＋uniform名规则不变。纯帧／时间线／独立端点均可传入同一固定投影，端点不绘图或执行材质图。

未提供正式准备帧／固定native资源分别明确SourceMaterialResourcesRequired／MaterialResourcesRequired。共享字簇分区、跨输出依赖、owned overlay及实际映射保持现有明确拒绝，不因来源不同绕过。工作区自动组合根、材质选择／参数和模板实时预览保持M3／R7；此批直接运行与真实成片验证不等于GUI已能选择或实时预览包材质。

准备帧公开来源协议不兼容，程序MINOR0.19.0；工程26／歌词16／模板5及包profile2保持，无新工程字段、包编码、程序集或第三方依赖，不转换旧工程。实际证据见[源材质合同](../../scripting-source-material-contract.md)§8及roadmap§18.71。

## ADR-0106 — 脚本工作区运行装配与异步像素会话（0.20.0，2026-10-04）

### 状态

已接受并实施 U0。M3 材质工作区、完整 R7 交互和 R2～R8 总出口仍未完成。

### 决策

1. `Rendering.Abstractions` 只公开中立异步 `IRenderFrameSessionFactory`／`IRenderFrameSession`。组合根负责把完整 `ProjectSnapshot`、既有 `CompiledProjectEffects`、Skia 原生资源、真实音频投影及预算装配为会话；Export 不依赖 Scripting 实现。
2. 每个预览画布拥有独立后台串行会话；最新请求槽合并播放推进，旧快照／旧工厂／旧目标／旧尺寸／旧播放头结果不能发布。UI 只复制中立像素，失败保留上一成功像素并显示真实错误。
3. 每次导出在编码器启动前建立独立会话，按帧准备和绘制；效果、源材质、作者 `Error` 或释放失败阻止导出并保护旧文件。没有效果的普通静态工程仍走同步管线。
4. 固定包图片按真实解码尺寸和声明坐标空间装配，所有包资源在会话期间计入表面预算；DLL/PDB 缓存按源码、入口、SDK、引用及缓存格式内容地址复用，损坏和旧格式拒绝，不回退其他代次。
5. 含效果的布局编辑只按真实时间采样；暂停可改源布局参数，布局试样行 ID 不进入脚本，脚本产生的变换不写回源工程。

### 影响与边界

此决策新增中立宿主接口并把程序版本升为 0.20.0，但不新增工程 JSON 字段、作者 SDK 方法、包 profile 或第三方依赖。它解决“代码已经能渲染但应用没有运行组合根”的接缝，不能替代 M3 材质选择／参数、模板实时预览、直接图／跨输出计划、父组捕获配方或完整脚本工作区。后续 R7 必须复用本会话协议，不另建 UI 专用渲染链。

### 证据

`docs/scripting-runtime-composition-contract.md`；八项目全量 2751 项测试全部通过、构建 0 警告／0 错误；组合根、会话、预览协调器和 Export 接缝的自动测试；真实窗口探针 `/tmp/makokara-runtime-ui-probe-second.log`。探针包含 ALSA／JACK 设备探测警告，但未出现托管启动异常；错误切换场景验证了诊断可见、上一像素保留和新快照恢复。

## ADR-0107 — 模板工坊材质选择、参数与正式实时试样（0.21.0，2026-10-04）

### 状态

已实施 U1。完整脚本工作区、直接图／跨输出执行及 R2～R8 总出口仍未完成。

### 决策

1. 工坊把未唱填充、已唱填充和外部描边作为三个独立材质槽位；每次选择都提交完整 `MaterialSpec`。静态材质沿用当前颜色／渐变，包导出只列出声明支持当前 `fill`／`stroke` 区域的导出，不把回退外观写回源样式。
2. 包参数编辑由清单声明驱动。九类参数先编码为严格 `EffectParameterOverrides`，再调用 `EffectPackageDefinition.ResolveParameters`；错误只标记字段并阻止保存，不产生部分写入。目录与 `.mkfx` 都由既有 `EffectPackageTransfer` 读取，包内容只有在模板保存时进入模板闭包。
3. 模板试样和模板卡使用同一个 `TemplateSampleFactory` 生成快照，并将完整包放入 `ProjectSnapshot.Effects.Packages`。画布统一使用 `App.PreviewSessions`，源材质因此经过真实固定资源、准备帧和正式材质 Render；错误由异步画布显示，不再以留空快照表示成功。
4. 不改变工程／歌词／模板 schema、包 profile、作者 SDK 或程序集边界；U1 只新增应用层编辑入口和正式预览接缝，继续保持模板不持有工程目标或运行历史。

### 证据

材质槽位／参数草稿、完整包保存与真实异步试样测试位于 `TemplateWorkshopPreviewTests` 及 `ProjectRenderSessionTests`；最终构建、全量测试和窗口探针记录在 roadmap §18.73。

## ADR-0108 — 工程效果实例工作区基础（0.22.0，2026-10-04）

### 状态

已实施 U2 基础切片；完整脚本工作区、自动组合根、直接图／跨输出配方及 R2～R8 总出口仍未完成。用户 GUI 人工验收待进行。

### 决策

1. 工程效果实例与不可变源码包在工作区中分栏显示。实例数组顺序就是执行顺序，新增、复制、删除、上移和下移直接作用于该顺序；复制生成新的实例身份和确定性 seed，并解除模板来源。
2. `ProjectEffectsEditor` 是实例编辑的唯一 Core 入口。新增和替换必须验证固定包、导出、绑定类型、参数和 Overlay 层；删除实例不删除包闭包，便于撤销和其他实例继续使用。
3. 工作区所有编辑先写入不可变草稿。保存先经过 `ProjectValidator` 导出校验，再用正式 `IRenderFrameSessionFactory` 建立独立会话；只有两者成功才通过一个 `SetProjectEffectsCommand` 写入工程历史。放弃和关闭确认不写入工程。
4. 本批只增加应用层工作区和 Core 编辑语义，不增加工程／歌词／模板 JSON 字段、包 profile、作者 SDK、程序集或第三方依赖。包导入／分享、源码编辑、自动组合根、直接图／跨输出和窄窗口覆盖面板保持后续范围。

### 证据

`ProjectEffectsStorageTests` 新增实例顺序、参数和非法绑定回归；`EffectWorkspaceTests` 覆盖包／实例分栏、复制删除、顺序移动、参数与订阅编辑。最终构建 0 警告／0 错误；全量串行测试 2756 项通过，证据见 roadmap §18.74 和 `/tmp/makokara-f93b-u2-tests-final.log`。

## ADR-0109 — 工作区效果包导入与原子分享（0.23.0，2026-10-04）

### 状态

已实施 U2 接续切片；0.23.0中间构建时源码编辑未实施（接续见ADR-0110）；U0运行组合根已接通，完整 R7、直接图／跨输出和 R2～R8 总出口仍未完成。

### 决策

1. 工作区包栏同时支持受管目录和 `.mkfx` 导入。两者都必须经过既有 `EffectPackageTransfer`、精确 profile、完整文件集合、哈希、路径和预算门禁；工作区不跟随链接，也不从外部目录补齐资源。
2. 导入成功后只调用 `ProjectEffectsEditor.Register` 修改不可变草稿。相同 `(PackageId, Revision)` 不重复登记；取消、解析失败和保存失败都不改当前工程或旧入口。
3. 分享读取草稿中的完整 `EffectPackageContents`，调用现有原子 ZIP 发布。分享文件不进入工程引用，也不保存用户机器路径；包与实例由一次工程效果历史提交共同保存；0.24.0进一步将保存和编译验证分开，见ADR-0110。
4. 本批不增加任何持久字段、包 profile、作者 SDK 或程序集。未来源码编辑器仍必须编辑受管包源码并生成新修订，不能直接改写当前固定修订。

### 证据

`EffectWorkspaceTests` 新增导入草稿、重复修订和当前包选择回归；工作区构建及控件 wiring 通过。最终全量证据记录在 roadmap §18.75。

## ADR-0110 — 源码草稿、固定修订与运行验证分开（0.24.0，2026-10-04）

### 状态

U2 作者操作接续已实施；完整 R7／R2～R8 保持未完成。0.23.0 是本批包传输的中间构建，和源码操作链一起以 0.24.0 交付。

### 决策

1. 包内既有 `role:csharp` 文件按 `(packageId, revision, path)` 保存独立文本草稿。切文件、切包、包／实例列表重建不会丢失文字；全部源码草稿进入工作区脏状态与关闭确认。保存当前包时合并其全部脏文件，保存工程改动时先构造所有包的新修订，任何内容闭包失败不发布部分草稿。
2. 源码保存只生成完整不可变包修订并登记到工作区。清单摘要由真实 UTF-8 字节更新，其他资源保持原字节，旧修订保留；不隐式更新实例或源材质。独立包编译通过正式资源门禁及分发参考集实际离线编译全部 C#，无需包已被启用实例引用，不运行作者构造。
3. 诊断属于明确固定修订，保留真实文件／行／列。选择源码诊断定位当前修订下的文件及字符位置，未保存编辑保持；新编辑明确标为尚未编译。包成功编译不代表当前工程已成功求值，也不自动启用效果。
4. 显式接受操作本批只升级当前所选实例。同包修订切换保留入口、全部参数、绑定、订阅、身份、seed、enabled、来源及顺序；导出或参数不被新修订接受时保持原实例并报告。新建实例初始禁用。多引用勾选升级与源材质／模板升级列表仍后续。
5. 修正 ADR-0108 基础切片的保存前编译门禁：按已接受合同 A §7.5，失败源码仍可保存。工作区“保存工程改动”通过一次历史命令提交源草稿，不要求成功编译；“保存修订并编译”验证所选固定包，“验证工程效果”验证实际启用引用，三者是不同动作。失败源码不恢复旧 DLL；正式预览／导出仍报告当前启用错误并阻止成片。
6. 编译期禁用工作区编辑和重入，固定任务输入；取消和关闭仍可操作，关闭先等待合作取消及释放，再处理保存／放弃／取消。保存后工作区保持打开并显示反馈，关闭流程的保存成功才退出。

### 影响与证据

程序 0.24.0，工程／歌词／模板 schema26／16／5、包profile2保持。没有作者 SDK、程序集、第三方依赖或 JSON 编码变更，不转换旧工程。运行组合根在 U0 已存在；后续准确缺口是直接图／跨输出执行计划、组／父捕获、映射、粒子桥接及完整工作区与模板交互。

具体职责及验证合同见[源码编辑合同](../legacy-ui/scripting-workspace-source-editing-contract.md)，实际构建、源码保存重开／导出门禁及窗口探针记录见 roadmap §18.76。自动证据不代替用户 GUI 人工验收。

0.24.0 作者工作区输入收尾：参数／名称／种子／绑定／订阅各自保存输入错误，只清被修正字段；非法输入进入脏状态，禁止保存及当前实例接受修订，刷新保持原文本，显式放弃重置。既有包定义／失效导出诊断与未提交输入分开，不制造未编辑脏状态；仍可显式选合法导出或换包修复。自动回归与变异证据见 roadmap §18.76。

## ADR-0111 — 组图目标与父级独立合成产物（0.25.0，2026-10-04）

### 状态

R3 接续已实施宿主目标、准备帧门禁和独立合成产物辅助；正式主画布父组协调、预览／导出成片证据仍未完成。完整 R2～R8 保持未完成。

### 决策

1. `ImageEffectTarget.Group` 是真实运行父树中的容器目标。只有工程绑定或显式节点绑定获得组写权；分支、行、元素和片段筛选不会隐式取得组写权。隐藏组仍验证身份、资源、参数和声明，但无可见孩子时不生成伪内容。
2. 组图执行必须在孩子元素、注音、整行和子组完成后读取独立前景／擦除产物。每个产物保存预乘颜色 C、下层擦除 E、来源集合和绘制顺序；父级使用 `C + B(1-E)`，不得把不同孩子的历史替换范围展平成同一目标历史。
3. 孩子共同透明度在交给父级前按有序后缀擦除进行因式分解，保证完整重组与部分捕获在实际像素容差内一致；父级继承孩子 E 与自身替换／最终 alpha 的逐像素最大值。未选部件保持原绘制，融合结果位于所选部件最高层；已融合来源不能再被局部拆选。
4. 透明旁路保留来源身份和层序；无目标输入的图替换绑定目标全部部件。现有 owned overlay、自身后置粒子排除、映射和跨输出门禁不因组图开放而绕过。
5. 这次只新增宿主 SDK 的组目标和内部固定图原语，程序升为 MINOR `0.25.0`；工程／歌词／模板 schema26／16／5、包 profile2保持，无新 JSON、第三方依赖或旧格式迁移。正式 Render 协调器完成前，准备帧上的组绑定不能宣称已经产生组图成片。

### 证据与后续

`docs/scripting-group-image-composition-contract.md` 定义输入配方、共同 alpha、空组、预算和取消边界。新增宿主／渲染测试覆盖组写权、真实后代、隐藏组、声明不匹配、重复绑定以及 C/E 产物。当前串行构建 0 警告／0 错误；最终全量测试、正式主 Render、MP4／Alpha 同帧和人工验收留给下一批。

## ADR-0112 — R3 Core 组图绑定明确拒绝（0.25.1，2026-10-05）

### 状态

已实施 R3 Core 首步；父组捕获与正式父级合成仍留在 R3.1。

### 决策

1. R3 Core 的正式主 Render 只消费 Token／Line／Mark 图目标。准备帧仍可携带合法 `ImageEffectTarget.Group`，但不能把它误当作普通目标缺失。
2. 主 Render 在图场景调度前检查组绑定，并以绑定实例归属抛出 `GroupImageEffectUnsupported`，阶段固定为 `RenderImageEffects`，内部保留 `PreparedFrameException` 说明当前父组图像效果需等待 R3.1。
3. 该门禁不改变组写权、准备帧协议或内部 C/E 原语；它只保证尚未开放的父组效果不会静默生成无效果画面。后续父组捕获、部件叠放、透明旁路和成片验证必须在正式协调器接入时删除该门禁并补齐完整验收。

### 证据

主画布回归测试覆盖合法组绑定的实例、阶段和稳定错误码；合同与路线记录见[组图与真实父级部件合成合同](../../scripting-group-image-composition-contract.md)、roadmap §18.79。

## ADR-0113 — R3 Core 单绑定入口复用组图拒绝（0.25.2，2026-10-05）

### 状态

已实施；父组捕获与正式父级合成仍留在 R3.1。

### 决策

1. 主画布批处理入口和 `ExecuteSceneImageEffect` 单绑定入口必须共享同一组图能力边界。两者都只允许 Token／Line／Mark 进入 R3 Core 的真实 CPU 图链。
2. 组目标在主画布阶段返回 `GroupImageEffectUnsupported`／`RenderImageEffects`，在单绑定执行阶段返回同一错误码／`ExecuteImageGraph`；实例 ID、artifact hash 和内部 `PreparedFrameException` 均保留。
3. 不为旧的单绑定适配入口保留一条绕过主画布边界的组图路径。R3.1 正式父级协调器接入时，必须同时更新两个入口的实现与回归，并补预览／MP4／Alpha 同帧证据。

### 证据

主画布和单绑定执行入口均有合法组绑定回归测试；路线和错误阶段见 roadmap §18.80 及[组图合同](../../scripting-group-image-composition-contract.md)。

## ADR-0114 — R3 Core 出口收口与后置边界（0.25.3，2026-10-05）

### 状态

已实施；R3.1 父级合成、R4 持久闭包及后续阶段仍未完成。

### 决策

1. R3 Core 的完成口径限定为 Token／Line／Mark：完整目标／区域捕获、六类部件顺序、Coverage、真实下层／背景媒体、CPU 图节点、输出外扩、透明旁路、密度／矩阵、预算回收及预览／MP4／Alpha 同帧。
2. `ImageEffectTarget.Group` 在主画布和单绑定执行入口继续明确拒绝；父组捕获、保留部件重组、owned overlay、自身后置粒子、非 ColorOnly 映射和跨输出不因 Core 收口而隐式启用。
3. 逐输入混合重组等已有稳定错误继续保留为后续边界。只有接入真实部件／历史协调器并补齐同帧证据后，才能从这些错误转为生产能力。

### 证据

主画布、捕获、预算／取消、透明旁路和编码回归已通过 880 项渲染／2799 项全量测试；实际状态和下一批入口见 roadmap §18.81 及[scripting-system-next-work-plan.md](../plans/scripting-system-next-work-plan.md)。

## ADR-0115 — R4 统一工程内容闭包的保存／搬移接缝（0.25.4，2026-10-05）

### 状态

已实施首个接续；R4 失败事务、自动备份／固定 DLL 工作区及 R5～R8 仍未完成。

### 决策

1. `project.json` 仍是唯一最后提交点；歌词、媒体、分析、效果包及源材质引用必须从同一 Project 快照发布，内容地址和 profile 校验沿各自现有合同，不建立第二套临时真值。
2. 另存为、目录搬移后重开和再次另存为都必须只依赖受管 `data/`／`effects/packages/` 闭包；原始外部音频／背景删除不能使已提交工程退化为空资源或重算分析。
3. 本批只收口统一闭包组合证据，不把包失败回滚、备份并发、固定 DLL 和源码工作区诊断提前标记为完成。

### 证据

`UnifiedProjectClosureTests` 覆盖同一工程的歌词、媒体、分析、图／材质包及源材质引用，搬移后重开和再次另存为通过；路线见 roadmap §18.82 和工程修订存储合同 §7。

## ADR-0116 — R4 后置包发布失败保留旧工程入口（0.25.5，2026-10-05）

### 状态

已实施回归接续；自动备份、固定 DLL 工作区及 R5～R8 仍未完成。

### 决策

1. 媒体等前置资源发布后，若效果包目标修订目录已被不一致内容占用，保存必须明确失败，不能替换旧 `project.json` 或把新资源混入旧入口。
2. 失败必须清理暂存文件；已经发布而未引用的不可变内容仍保留，失败路径不执行自动回收。
3. 本批验证现有事务边界，不新增 schema、包 profile、程序集或旧工程迁移，也不把一项失败回归等同于 R4 总出口完成。

### 证据

`UnifiedProjectClosureTests` 在真实保存目录预置冲突包修订，验证保存失败后入口字节不变、旧工程仍可加载且没有 `*.tmp`；路线见 roadmap §18.83 和工程修订存储合同 §8。

## ADR-0117 — 自动备份完整目录发布与同秒独立身份（0.25.6，2026-10-05）

### 状态

已实施；R4 固定 DLL／模板／工作区组合验证和 R5～R8 继续。

### 决策

1. 每份备份在唯一暂存目录完整写入后整体发布；失败清理本次未发布目录，不让半成品进入列表或触发裁剪。恢复标记及已备份实例只在成功后更新。
2. 保留原 UTC 秒名称，同秒占用追加二十位递增序号，取现存最大值而不复用裁剪空位。文件和未完成目录的占用都保留，已发布入口不覆写。
3. 同一进程的发布／裁剪串行，跨进程名称冲突重新选择序号；其他 IO 失败继续报告。列表要求真实根入口，缺入口的目录不参与裁剪。保存裁剪始终保护本次发布，时钟回拨也不能返回失效恢复路径。
4. 本批保持独立完整歌词、媒体、分析及图／材质包字节闭包；本机 DLL 缓存、schema、profile 与作者 SDK 不改变。

### 证据

原代码定向回归复现同秒覆盖、并发混合、失败半成品可见和重试覆盖旧备份。修复后覆盖同秒独立快照与裁剪空位、占用文件／目录、六次并发发布、分析失败清理、时钟回拨、完整资源搬移，以及恢复标记保留和同一编辑实例重试。合同见[自动备份发布](../../scripting-backup-closure-contract.md)，实际结果见 roadmap §18.84。

## ADR-0118 — 持久闭包组合出口与包／编译产物独立身份（0.25.7，2026-10-05）

### 状态

已有生产协议及组合回归已验证；本计划第 2 批持久闭包自动出口收口，完整 R4／R7、R3.1 及 R5～R8 保持后续。

### 决策

1. 工程、模板与分享文件持有完整固定包字节；DLL／PDB 仍属本机缓存，不成为工程第二份源真值。Artifact 按 C# 源码／入口、编译选项、Roslyn 及实际参考程序集身份；包修订按完整清单和所有资源字节。
2. 只改资源可以复用相同 C# Artifact，每个正式会话仍须验证本修订 shader／ABI／图片并消费当前字节。命中缓存不能代替原生资源门禁；损坏当前 DLL／PDB 不回退旧代次或重编译覆写。
3. 本计划第 2 批持久闭包的完成证据必须跨模板分享、工程副本、备份搬移、已有／空缓存和真实会话；失败源码工作区保存／定位／撤销与编码前旧输出保护沿同一协议验证。预览／导出重放像素回归与实际视频编码证据分别记录。
4. 收口仅对应本计划第 2 批的存储／资源出口。原实施路线的完整直接图／材质实例、跨输出执行、完整 SDK 和最终 GUI 不由这个出口替代，下一批进入 R5 会话语义。

### 证据

`ProjectRenderSessionTests.Closure` 新增 8 项组合回归，正式会话定向共 27 项通过；既有 `CompilerTests`、存储／模板及失败源码工作区回归共同覆盖验收表。具体身份和验收映射见[持久闭包组合验收](../acceptance/scripting-persistent-closure-acceptance.md)，全量结果见 roadmap §18.85。

## ADR-0119 — R5 固定控制器来源及作者提示贯通像素帧（0.26.0，2026-10-05）

### 状态

已实施 R5 接续；完整 FPS／两个文字效果／音频及取消重建组合、完整 R5～R8 保持后续。

### 决策

1. C# ArtifactHash 与包内容修订分别保留。中立 `EffectPackageIdentity` 固定实际 PackageId／完整 Revision／控制器 ExportId，帧实例从固定包及声明取得，不保存第二份可写来源；独立源码没有包时保持 null。
2. 七阶段作者诊断、普通异常、required 音频初始化门禁及真实图／材质／组隔离失败均保留来源。材质／图导出句柄不能替代控制器导出；聚合释放的外层和内部失败各自保留来源，仍继续释放全部资源。
3. 正式 Render 先传入准备帧作者诊断，再追加实际绘制提示。普通文字和图／材质路径都保留 Info／Warning，缓存保留完整数组，导出按既有规则记录重复提示；不因记录诊断改变像素或 Error／取消策略。
4. 本批新增中立公共宿主来源协议，程序升 MINOR 0.26.0；schema26／16／5、包 profile2、Shader ABI、SDK 名义版本、程序集集合和依赖不变，不迁移旧工程。

### 证据

修复前七阶段来源回归全部得到 null，修复后又复现普通像素帧丢失作者 Info／Warning。最终新增 16 项回归、会话定向共 43 项通过，覆盖共享 DLL 的不同包／修订／导出、七阶段 Error／异常、作者捕获 Error、初始化音频门禁、多包释放、真实材质／组图错误、普通／材质像素帧、缓存和视频提示，以及 200 ms 真实编码失败保留旧输出。合同见[运行诊断来源](../../scripting-runtime-diagnostics-contract.md)，全量结果见 roadmap §18.86。

## ADR-0120 — 两个公开文字包与持久会话组合证据（0.26.1，2026-10-05）

### 状态

已补齐真实重复文本示例包并验证组合；参数／源码撤销、热替换、模式／目标变化与取消导出释放仍后续，完整 R5～R8 未关闭。

### 决策

1. RepeatEmphasis 由原公开 SDK §6.2 源码补齐 profile2 清单、README 与摘要，声明元素族 VisualTransform／StyleModulation；WordEmphasis 更新当前 API／工作区说明及修订，两个作者源码不改。例包依正式目录／`.mkfx` 导入、工程持久化与运行组合根消费，不增加测试专用入口。
2. 重复参数默认值沿合同 A §8.1；未规定的编辑范围取 scaleStep [0,3]、maxScale [1,4]、darkenStep／minBrightness [0,1]，严格校验，不夹紧。绑定仅限定输出，源订阅独立决定收到的起唱；精确文本和每次 occurrence 的序号保持。
3. 组合证据必须同时检查状态数值、真实像素和成片。三种 FPS、有／无音频、前奏／保留旧句、独立中段 Export、两种 token 入口取消后重建、required／optional 及真实 OutsideRange 共同覆盖；音频测试控制器不冒充产品随应用示例。
4. 取消只在受控测试几何中发送信号，正式包、共享会话和 Skia 绘制保持生产实现；不依赖延时竞争、不改作者行为。实例缩放相乘，样式最后写入，基线仍固定源样式；重复纯色示例遇到真实包材质明确失败。
5. 示例及验证接续升 PATCH 0.26.1；schema26／16／5、profile2、Shader ABI、SDK 名义版本、程序集与依赖不改。当前会话组合证据不替代最终工作区和 R8 人工验收。

### 证据

新增 26 项组合回归，会话定向 69 项通过；6 次真实 MP4／ProRes4444 编码保留音轨并抽帧对照。验收映射、具体数值及下一步见[会话组合验收](../acceptance/scripting-session-combination-acceptance.md)，完整构建／测试结果见 roadmap §18.87。

## ADR-0121 — R5 预览输出输入与取消接缝（0.26.2，2026-10-05）

### 状态

已实施预览调度修复；参数／源码撤销、单实例修订热替换和取消导出释放仍后续，完整 R5～R8 未关闭。

### 决策

1. 预览当前输入身份由工厂引用、快照引用、解析后的像素目标、透明模式、质量档位和布局试样行组成。时间只用于帧顺序与播放合并；同一输出输入下的时间推进不清除真实失败门禁。
2. 输出输入改变会清除旧失败并取消旧工作；Preparing、Failed、Ready 均必须仍属于当前输入。完成发布前再次检查工作器取消，旧请求即使忽略取消并迟到返回，也不能覆盖当前像素或错误。
3. 快照／工厂改变时释放旧会话并建立独立会话；模式／目标改变沿现有 FrameSession 的 InputsChanged 规则重建同一会话。请求自身取消和作者抛出的 OperationCanceledException 保持取消语义，不写入失败门禁。
4. 这是私有预览调度与测试接缝修复，不改变公开 SDK、作者源码、七阶段顺序、工程字段、会话指纹、schema26／16／5、包 profile2、Shader ABI、程序集或依赖；程序 PATCH 升至 0.26.2。

### 证据

首轮新增的 7 项回归在修复前稳定复现输出改变不重试、迟到错误覆盖和请求取消记失败；修复后预览定向回归 **20 项全部通过**。保存重开的 WordEmphasis／RepeatEmphasis 与真实 48 kHz／20 ms 音频在 24／30／60 FPS 下依次切换普通、透明、2×、非等比尺寸、Draft、Final 并切回原设置，全部与独立 Export 会话像素相等；只建立一个预览会话，两个可回收脚本上下文在关闭后均回收。合同见[预览输入与取消合同](../../scripting-preview-input-contract.md)，完整结果见 roadmap §18.88。

## ADR-0122 — R5 修订撤销、热替换与真实取消导出验收（0.26.3，2026-10-05）

### 状态

已完成本计划第 3 批 R5 会话自动验收；完整 SDK、R3.1、R6～R8 及最终工作区／人工验收继续。

### 决策

1. 参数保存与源码新修订接受分别提交现有 SetProjectEffectsCommand，保持两步独立撤销。源码保存及独立编译只登记不可变修订；显式接受仅升级选中实例，保持实例其他字段、顺序和未选中实例的旧修订。不自动替换同包其他引用。
2. 每个新工程快照建立独立预览会话并释放旧会话；同一旧像素会话拒绝新快照。组合验收检查全部像素、历史数值、持久闭包、DLL 代次及精确 Artifact 对应的加载上下文弱引用，不以工厂 Dispose 计数代替卸载证据。
3. 取消实际编码的测试信号在真实编码器接管第 39 帧后发送；必须保持取消异常、旧输出字节、临时文件清理、帧缓冲／会话／作者上下文释放，并以同一固定工程及新 token 成功重新编码。图资源使用正式 shader／图／材质门禁，不放宽运行校验。
4. 本批仅新增组合验收与文档，程序 PATCH 升至 0.26.3，schema26／16／5、profile2、Shader ABI、公开 SDK、程序集及依赖保持。测试中的作者源码新修订不改变随应用示例。原生资源释放沿已有 R3 Core 证据，不把加载上下文弱引用当作 native 性能基准。
5. 按后续计划第 3 批范围收口 R5 自动出口，下一步进入 R6 粒子；原完整路线中尚未交付的 SDK／高级输出及 R7／R8 人工验收继续保留。

### 证据

普通／透明两组修订回归各验证七次原始／参数／源码／撤销／重做状态，预览、独立 Export 和保存重开像素相同；未升级实例保持旧行为，参数与源码热替换及关闭后四个作者上下文全部回收。实际 MP4／ProRes4444 各在第 39 帧取消，旧产物保持、临时文件与全部帧释放，随后各成功导出 54 帧及音轨。会话／预览／失败源码组合定向 94 项通过。完整证据见[验收表](../acceptance/scripting-session-revision-acceptance.md)，全量结果见 roadmap §18.89。

## ADR-0123 — R6 封闭混合图元与作者粒子绘制（0.27.0，2026-10-05）

### 状态

已接通线段／带状拖尾与完整 VectorTrail 包，验收证据以 roadmap §18.90 为准；Bridge、图片精灵、目标内捕获和完整 R6 继续。

### 决策

1. DotBatch、SegmentBatch、TrailBatch 共用封闭 OverlayBatch 身份、跨类型 OutputId、工程实例／ordinal 批次顺序和统一预算。宿主 CompleteOverlay 收取全部图元；旧 Complete 遇到其他类型明确失败，DotBatches 仅是完整准备帧的圆点视图。图／组复制和自身下层门禁消费完整图元，不能丢失新类型。
2. 平端线段使用最终世界逻辑端点、正宽和直乘颜色。带状拖尾用不可变位置／截面全宽／直乘颜色；重复位置取最后样本，首尾用线段法向，折点用单位法向和。和的长度 ≤1e-12 时断带，处理反向及浮点消去；不使用无限 miter，不改作者自交路径。CPU Skia 三角带负责颜色插值，原生网格立即释放，不创建离屏表面。
3. ParticleDescription.Rendering 默认 Dots；Custom 只关闭自动绘制，宿主继续保存真实出生、轨迹、随机、寿命、源、发射记录和全部预算。纯 EvaluateVisuals 从只读 Particles 输出图元；命名仍拒绝与本实例存活发射器重复。VectorTrail 的公开源码分别选择点状、线段、渐宽带，不更新模拟或使用 UI 时钟。
4. 每圆点／线段一项，每拖尾点一项，全实例统一预算，零面积和透明样本也计入；不以一条超长带逃避限制。世界矩阵不再乘文字父矩阵，画布密度各应用一次；native float32 越限／宽度下溢明确失败。独立矩形的 Alpha 与直带逐像素一致，Skia 顶点及画笔 RGB 量化至多差 1，不为测试改变正式颜色语义。
5. 新公共 SDK 图元及粒子绘制协议升 MINOR 0.27.0；schema26／16／5、profile2、Shader ABI、SDK 名义版本、程序集集合及依赖不变。固定 DLL 仍校验当前 SDK 字节，不复用旧字节产物；本步不冻结完整 SDK，不关闭完整 R6。

### 证据

公开包分享／导入、保存重开、双文字缩放、Custom／Dots 粒子及真实出生记录、不同 FPS 的同刻像素、暂停／回退及独立预览／导出、四次真实 MP4／Alpha 同帧与编码失败保护均有定向证据。跨类型名字、实例顺序／层覆盖、四类工程预算、原生精度／Alpha／密度、图及组复制、自身下层拒绝见[线段与拖尾合同](../../scripting-vector-overlay-contract.md)。最终构建／全量和启动证据见 roadmap §18.90。

## ADR-0124：固定图片精灵、单位方形几何与同帧资源账（2026-10-05）

- **状态**：按当前脚本目标授权裁决并实施，实际验证见 roadmap §18.91。
- **依据**：后续计划 R6 要求图片精灵，SDK 已给出 DrawSprites 入口，现有包图片为固定完整解码的资源，作者没有 native 权限。
- **决定**：Sprite 持有固定 ImageHandle、归一化图片内非空 Source、单位方形到世界可逆 Affine2 和直乘 sRGB Tint。裁片默认全图，支点由作者矩阵明确，支持旋转／镜像／非等比／剪切，不把机器路径或隐含图片尺寸引入几何。Tint RGB 和 Alpha 分别乘图片直乘颜色与 Alpha，只预乘一次；采用线性采样且不建帧内图片副本。独立矩形验证发现直接 DrawImage 的裁片边缘会混入相邻图块，使用管线创建时固定的宿主采样程序，把采样点限制在半像素内缩框；不足一像素的轴取中心，分数裁片继续按线性采样定义。帧内只绑定 uniform／子图像，不编译程序。
- **生产接续**：精灵与其他 Overlay 共用声明、名字、排序、层和绘制预算，宿主检查真实实例资源目录；全包固定图片表借用现有解码图像，缺失／另修订／原生精度明确失败。宿主准备帧保留实际编译／包来源，原生错误不丢来源。
- **预算与所有权**：把主 Render 帧账移到两层 Overlay 和正式文字／图合成外侧，精灵与图同时占用同一帧存储限额，相同图片别名去重，取消／失败及时减账。原会话固定图片字节和关闭顺序保持。
- **格式与范围**：公共图元／准备帧协议升 MINOR0.28.0；schema26／16／5、package profile2、Shader ABI、SDK名义版本、程序集及依赖不变。完整 SpriteTrail 原创图片资源包进入现有资产参数、工程保存和正式 Render。Bridge、目标内捕获、复杂映射及完整 R6／R3.1／最终工作区仍未交付。

## ADR-0125：真实光标后继、并发源组与独立最终端点（2026-10-05）

- **状态**：依据当前脚本目标“未指明的决策按最优方案执行并记录”的授权裁决；0.29.0 证据见 roadmap §18.92。取代历史重叠后继待答，本步不开放 Bridge 发射。
- **顺序与并发**：按同一分支／程序的源顺序，句内连接相邻定时元素，跨句连接相邻源行末／首定时元素，行依据不制造 TokenId。相邻源行同时起唱形成并发组，组间保留全部真实边，不按首项猜光标。缺行时间阻断跨句，后组早于前组不建立逆时间边；不排序改写源或绕过未知区间。重叠边保留，GapUs=0，在 To.StartUs 进入转换事件，桥接不占用下一句时间。
- **唯一真值**：抽取正式 Playback 共用的 Current 源／装载投影；旧端取 EndUs-1，新端取 StartUs。真实共享显示器决定 BindingId、片段、槽位和前奏，不可见端点不补造装载；句内仅同 BindingId，跨句保留真实装载边。
- **输出与订阅**：固定接续先建立，查询两端按输出范围筛选；不跳过范围外目标重新接续。原 BoundSources 转换筛选仅检查源 ID，现补上实际 BindingId／行／节点或片段；宽源订阅仍独立，不扩大粒子输出权。
- **最终端点**：宿主 ResolveSuccessorsAsync 返回全部边及独立 PreviousEndLimit／NextStart，重放共享 callback／Advance／文字 Evaluate 和全部实例最终几何，不调用发射／粒子／视觉，不写主状态。Error／取消传播并释放两次独立后端，不回退。
- **版本与后续**：公开宿主投影／查询及真实事件行为升 MINOR0.29.0，schema26／16／5、profile2、Shader ABI、SDK名义版本、程序集及依赖保持。本步保留 Bridge 明确门禁；下一步接入固定半开飞行区间、真实来源、配额及正式绘制。完整 SDK／R6／R3.1／R7／R8 仍未交付。

## ADR-0126：Bridge 半开飞行、逐边配额与独立最终端点运行（2026-10-05）

- **状态**：按当前脚本目标授权实施，自动验证和剩余范围见 roadmap §18.93；完整 R6 未关闭。
- **路径策略**：沿 ADR-0125 固定的全部合法后继逐边独立发射，保留并发分叉／汇合，不挑首项。仅正空档且不超过 BridgeGapLimitUs、时长大于0时飞行，范围 `[To.StartUs-min(BridgeDurationUs,GapUs),To.StartUs)`；重叠、相接、长空档及无后继直接换点，不延迟源起唱或跳过范围外行重新接续。
- **几何**：PreviousEndLimit／NextStart 独立重放相同快照、全部效果、音频及源材质，跳过发射／粒子／视觉。世界线段用现有 BridgeCurve 作时间缓动，保留有限过冲，非有限曲线／坐标明确失败；不引入空间控制点。粒子出生后共用既有解析运动、散布、年龄外观和拖尾，切句不改旧出生坐标。
- **时间和配额**：不添加飞行边界到共享120Hz／精确事件时轴，否则作者 Advance 切分会改变未来最终端点。左端注册只准备下一固定区间相交路径，模拟从实际飞行起点精确有理积分；实际头与每条转换／飞行起点分别存配额，终点半开且消失时舍弃余量。分数读取使用同一调度副本；全实例同刻按原身份顺序再加转换 ID／飞行起点，Burst 仍在连续出生之前。
- **来源和权限**：新增封闭 ParticleSource.Bridge，保存真实轨道、两端源／装载、转换事件 ID 和实际范围，BindingId 归旧端；不伪造 TokenId、OccurrenceId 或 Current。两端仍按真实输出绑定筛选，宽源订阅不扩大写权。事件 ID 字段保留给 Burst，连续桥接转换身份从 Source 读取。
- **所有权与失败**：缓存只保存不可变端点值，键含真实身份、端点种类、锚点、模式和目标；不持有独立作者／native 实例，重建／失败／取消／关闭清空。未来 Error 原样传播真正失败控制器的身份及定位，不包装为请求发射器。成功查询但端点不可用时报告请求实例 Warning 并停发该路径；不回退基础点。全部记录／存活／拖尾／绘制预算和编码旧产物保护继续共用原路径。
- **版本与边界**：公共来源及 Bridge 实际行为升 MINOR0.30.0；schema26／16／5、profile2、Shader ABI、SDK名义版本、程序集和依赖保持。CursorTrail 完整包升1.1.0并更新闭包hash，默认仍Jump。纯帧连续发射及 IncludeInTargetCapture 门禁保持；目标内捕获、复杂映射、完整 SDK／R6／R3.1／R7／R8继续，不新增旧工程转换。

## ADR-0127：完整粒子作者包、真实触发选择与混合生产组合（2026-10-05）

- **状态**：按当前脚本目标授权实施，实际验证和剩余范围见 roadmap §18.94。
- **作者包**：VectorTrail／SpriteTrail 升1.1.0，显式导出 transition／bridgeUs／bridgeGapLimitUs，保留原 Jump、发射速率、运动和图片字节。Bridge 消费同一宿主后继／配额／独立端点，三种向量呈现和图片星芒不会自建时间模拟器；源码与 README 按不可变完整包更新摘要。
- **事件包**：新增 profile2 EventBurst 1.0.0／event-burst，输出 burst。token 在 TokenStarted 选择 SourceToken；line 在 LinePerformanceStarted 选择 SourceLine；cursor 在 TokenStarted 或无逐字时间的 LinePerformanceStarted 选择全部 BindingCursors，避免同一逐字起唱另从行事件重复登记；mark 在真实 ElementVisible/Mark 选择 TextMark。并发源事件各自登记，每个事件／每个实际锚点获得 count，不能合并不同事件。前奏准备不制造起唱，标记保留真实节点／前奏／装载。
- **参数与权限**：count 使用已有 number，范围0～Int32.MaxValue；初始化进一步要求整数，零不分配出生，分数不静默截断。默认6条、600000us寿命、6px直径、12px散布、180000us拖尾、颜色(1,.4,.1,.9)，固定世界速度(0,-10)。源订阅和输出权限独立，默认绑定源；扩大订阅的显式来源越权保留实际包诊断，不放大输出或隐藏错误。
- **运行**：包只登记描述／意图及纯视觉图元，正式宿主统一最终几何、随机、寿命、固定拖尾、实例／批次顺序和四类预算。混合工程把三种向量呈现、Bridge 图片、Burst、双文字／真实音频及 ColorOnly 整行 Glitch 接到同一准备帧和正式 Render；图效果实际位移像素，保持既有语义锚点合同。
- **版本和后续**：新增可消费作者包及参数升程序 MINOR0.31.0，公共宿主 SDK 方法／协议不改；schema26／16／5、profile2、Shader ABI、SDK名义版本、程序集集合及依赖保持。IncludeInTargetCapture、复杂映射、owned overlay和父组图门禁保持；不以本批自动证据关闭完整 SDK／R6／R3.1／R7／R8或用户人工验收。

## ADR-0128：本计划 R6 全帧自动验收与范围收口（2026-10-05）

- **状态**：按当前脚本目标及后续计划第4批收口自动出口，最终验证以 roadmap §18.95 为准。
- **完成范围**：对照[后续计划](../plans/scripting-system-next-work-plan.md)第4批及§4，连续／Burst、Cursor／Token／Line／Mark、Jump／Bridge、圆点／图片精灵／线段／带状拖尾、出生顺序／seed／预算和逐帧导出使用实际断言及生产包证据验收。高级合成按该计划§2.2留在R3.1；不把原路线完整R6或SDK的目标内捕获／跨输出等剩余项视作完成。
- **全帧口径**：独立预览与正式导出会话逐帧比较时刻、全部预乘BGRA字节及诊断；六段真实音轨MP4／ProRes4444覆盖24／30／60FPS全部410个编码输入帧，回退后再次全段重放。完整解码按帧数量、EOF、RGB及Alpha明确容差和前景保留检查；损失编码不承诺与输入逐字节相等，也不以抽帧替代全帧。具体判据见[验收表](../acceptance/scripting-r6-acceptance.md)。
- **取消与释放**：混合文字／音频／粒子／图像工程在1266666us写入39帧后取消，真实编码器未完成发布，旧输出及暂存清理保持；全部像素帧和作者上下文回收。新token独立重建并成功导出54帧，两种格式的全部108个恢复输入帧再次与独立预览比较。
- **版本与后续**：本批只增加回归及验收文档，没有生产／包／公开协议变更，程序保持0.31.0，工程版本门禁不为验收批次断代；schema26／16／5、profile2、Shader ABI、SDK名义版本、程序集及依赖保持。下一步R7实时工作区／模板整合，R8用户成片验收继续；IncludeInTargetCapture、owned overlay、父组图和复杂映射门禁保持，性能基准后置。

## ADR-0129：R7 效果工作区实时预览、诊断与生命周期（0.32.0，2026-10-05）

### 状态

已实施本计划第5批 R7 的实时预览接续；模板工坊配方、歌词行／元素／容器入口、完整绑定场景整合、R3.1 高级输出及最终用户人工验收继续。

### 决策

1. 工作区预览固定工程与实例草稿快照，正式画布请求同时携带精确时间、解析后的 RenderTarget、质量、透明模式和布局行身份。播放只推进 UI 播放头，不写工程时间轴；无输出时长时使用真实歌词／已接受音频末端并至少提供5秒范围。
2. 参数、启用、绑定、订阅、seed、顺序或显式接受修订改变草稿输入并重建快照；未提交源码不升级实例。非法字段保留用户原文并停止预览输入更新，合法输入的运行／编译失败保留上一成功像素。错误输入、旧快照、旧模式／质量和迟到的未来时间结果不能发布到当前画布。
3. 运行、编译、图／材质和透明回退诊断只传递不可变值，必须带实际包／修订／实例／导出、阶段／时刻、代码和可选源码位置；不把作者对象、异常对象或 native 表面交给 UI。诊断按钮定位真实固定包和文件并保留已有源码草稿。保存与运行独立；未保存失败仍可保存，已保存且匹配工程的失败才反馈主窗口导出门禁；验证操作必须真正渲染当前时刻。
4. 工作区宽度低于1250逻辑像素时显示显式编辑／预览页，宽窗口同时显示两者。切换保留页面选择、源码文本和播放头，不把 UI 状态写入工程；控件继续使用现有 Material You 资源和本地化文案。
5. 关闭先停止播放，取消并等待画布串行工作器及显式源码编译，再释放会话、位图和事件；关闭后的迟到像素／诊断不得回写所有者。合作取消语义保持，不承诺强制终止作者代码。
6. 本批程序升 MINOR **0.32.0**；工程／歌词／模板 schema **26／16／5**、package profile2、作者 SDK、程序集集合、Shader ABI 和依赖不变。开放实时预览不解除组图、直接图／材质实例、复杂映射、跨输出及其他 R3.1／R7 门禁。

### 证据

新增 8 项 `EffectWorkspacePreviewTests`、2 项调度诊断回归及4项真实工程／正式会话回归；串行全量构建 **0 警告／0 错误**，全量回归 **3022 项全部通过，0 失败／跳过**（Application103／Core1140／Export94／Media89／Rendering928／Scripting206／Spike.App378／Timeline84）。真实原生探针在 1520×900 与 940×640 下操作参数改图、播放／暂停、750000 微秒播放头、透明 alpha、质量切换、源码保存／显式接受、CS1029 定位、失败保存、恢复、当前时刻验证和关闭释放；截图仅作控件证据，运行语义由正式会话回归证明。日志为 `/tmp/makokara-0.32.0-build.log`、`/tmp/makokara-0.32.0-tests.log`，原生探针构建／运行日志为 `/tmp/makokara-0.32.0-ui-probe-build.log`、`/tmp/makokara-0.32.0-ui-probe.log`。

## ADR-0130：R7 模板效果配方、正式试样与行／元素范围可见性（0.33.0，2026-10-05）

### 状态

已实施本计划第5批 R7 的模板配方接续；容器／组效果入口、完整绑定场景整合、R3.1 高级输出及最终用户人工验收继续。实现合同见[模板效果配方接缝合同](../legacy-ui/scripting-r7-template-recipe-contract.md)。

### 决策

1. 模板工坊只从当前草稿固定包闭包中列出控制器／图导出。行模板要求 `lines` 绑定，元素模板要求 `tokens` 绑定；含 `group` 输出的导出不进入列表。名称、参数 JSON、包修订、导出 ID 和完整闭包在进入草稿前严格校验。
2. 配方按添加顺序保存为 `TemplateEffectRecipe`。中央试样统一调用 `StyleTemplateApplicator.TryPreview`，因此样式覆盖、模板来源、绑定、效果实例和 `EffectInputsFingerprint` 同时生成；失败保留上一份可用画面。保存、复制、分享和重开保持包修订及配方身份。
3. 歌词行编辑器继续使用同一模板预览结果，在当前行／元素影响说明中显示实际 `TemplateOrigin` 实例数量和名称。容器／组效果仍由主画布组图门禁管理，不新增模板字段或静默降级路径。
4. 程序升 MINOR **0.33.0**；工程／歌词／模板 schema **26／16／5**、package profile2、作者 SDK、程序集、Shader ABI 和依赖不变，不迁移旧模板库。新增模板工坊回归，完整 R7／R8 与最终人工验收保持开放。

### 证据

模板工坊回归覆盖元素配方创建、参数严格解析、正式试样绑定／指纹、保存重开闭包和无效参数保留旧预览。最终构建、全量回归及启动证据记录在 roadmap §18.97。

## ADR-0131：R7 歌词行／场景对象的上下文效果入口（0.34.0，2026-10-05）

### 状态

已实施本计划第5批 R7 的上下文入口接续；完整绑定场景整合、容器／组高级效果、R3.1、R8 和最终用户人工验收继续。实现合同见[上下文效果入口合同](../legacy-ui/scripting-r7-binding-entry-contract.md)。

### 决策

1. 歌词行编辑器的“本行效果…”和“本元素效果…”以及场景对象检查器的“对象效果…”统一打开现有 `EffectWorkspaceWindow`，只传入不可变的 Lines、Tokens 或 Node 绑定，不复制实例 CRUD、源码编辑或预览路径。
2. Node 入口默认 `includeDescendants=true`，让容器和槽位继续遵守展示树的父子／装载规则；效果系统不把容器改写成模板，也不把组图静默降级到文字目标。
3. 工作区显示上下文目标；已有相同绑定实例优先选中。新建实例仅当控制器导出声明支持该 binding kind 时使用上下文绑定，否则保留现有默认绑定并让用户在实例属性中修正，不自动启用或保存。
4. 入口不提交未应用的场景草稿；工程校验继续负责发现不存在的目标 ID。程序升 MINOR **0.34.0**；工程／歌词／模板 schema **26／16／5**、package profile2、作者 SDK、程序集、Shader ABI 和依赖不变。

### 证据

新增工作区上下文绑定回归覆盖 Node 导出支持、已有目标实例选中和新实例绑定复用；定向构建／测试及最终全量证据记录在 roadmap §18.98。

## ADR-0132：R7 绑定入口的真实时间预览与静态参考边界（0.35.0，2026-10-05）

### 状态

已实施本计划第5批 R7 的预览上下文接续；完整绑定场景整合、容器／组高级效果、R3.1、R8 和最终用户人工验收继续。实现合同见[场景预览上下文合同](../legacy-ui/scripting-r7-scene-preview-context-contract.md)。

### 决策

1. 统一入口请求携带只读 `EffectWorkspacePreviewContext`。歌词行／元素入口固定当前播放头和所属参考行；场景对象入口固定场景编辑器播放头和 specimen 行。主窗口独立入口保持默认全局预览，初始时间限制在工程输出范围内。
2. 正式准备帧代表真实时间场景，不能与静态 `LayoutLineId` 混用。无效果时保留上下文布局参考；启用效果或含源材质时有效布局行自动为 null，与独立导出会话使用同一动态场景，播放头不跳变。UI 明确提示两种范围；不保留没有实际消费者的节点／源行画布参数。节点仍由真实 Node 绑定标识。
3. 有效布局行参与迟到帧门禁，显式验证与画布使用同一工作区请求。静态参考的成功／失败不反馈主窗口导出状态；保存本身不能被当作运行成功，只在当前实际发布 Ready／Failed 或显式验证失败时反馈已保存且匹配的动态工程结果。
4. 场景有未应用／已过期草稿时禁用“对象效果…”并提示先应用或放弃。0.34.0 关于“保存会拒绝未应用新节点”的文档陈述不成立：工程保存允许保留禁用失效绑定供修复，本批保护来自场景入口门禁，不改原保存合同，不自动提交草稿。
5. 程序升 MINOR **0.35.0**；工程／歌词／模板 schema **26／16／5**、package profile2、作者 SDK、程序集、Shader ABI 和依赖保持不变。完整容器／组效果、跨输出、R3.1、R8 和最终人工验收保持开放。

### 证据

上下文单元回归验证初始播放头、有效布局身份、迟到帧及保存不能冒充运行成功；真实包像素与原生工作区探针证据和最终全量记录在 roadmap §18.99。

## ADR-0133：R7 目标相关效果与实例定位（0.36.0，2026-10-05）

### 状态

已实施本计划第5批 R7 的目标效果清单、绑定详情和精确实例／节点定位接续；本批实际验证以 roadmap §18.100 为准。完整容器／组高级效果、跨输出、R3.1、R8 和最终用户人工验收继续。

### 决策

1. 歌词行／元素及场景对象检查器显示工程中的相关实例，按工程顺序保留每个真实实例 ID、名称和启用状态；同绑定实例不合并，禁用实例不隐藏。Project、Branch、Lines、Tokens 的稳定源关联不依赖当前装载，未显示的源行／元素仍能找到这些实例。清单的直接关联按同类绑定的 ID 相交判断，Node 同 ID 不因后代开关不同而消失；合法空槽仍可列出自身节点绑定。
2. Node／Clips 的间接关联使用真实时间场景的当前装载、BindingId、片段及运行父树，不能沿持久源树猜测某行正在槽位或片段中。共享 `FrameBindingScope` 从完整 `SceneFrame` 查询元素族、可读行、真实标记和组范围；运行宿主、视觉上下文与工作区共用这一实现，工程绑定通过 `ProjectEffectBindingProjection` 保持六类身份和后代策略。缺失／循环的运行父树明确失败，不退回静态定义。清单查询不编译或执行作者，不改工程、场景草稿或撤销历史。
3. 相关范围不等于作者已向全部目标产生输出，也不授予新写权。Tokens 绑定可以读取所属行，整行 writer 继续拒绝 Tokens；Tokens 不包含标记或组。Branch／Lines／Clips 的行及关联标记范围不能获得组写权，组 writer 仍仅允许 Project 或显式 Node。父组的自身绑定可通过该组仿射／共同 alpha 影响实际装载孩子，因此清单另标“当前祖先组绑定”；只沿本帧真实祖先链查找，不将未装载源对象按持久树猜为孩子，也不把这种关联变成元素、行或标记写权。
4. 工作区继续保留全部失效实例供修复；目标清单只列相关项，不把无关缺失绑定混入每个目标。含有效目标与缺失 ID 的相关绑定集合保持完整身份并显示缺失说明，不能过滤掉坏 ID 后宣称绑定已修复。实例详情分别解释输出绑定与源订阅，显示实际行／元素文字、分支／节点／片段名称及稳定 ID；缺失对象仍显示原 ID。扩大源订阅不会扩大输出权限或相关目标的写范围。
5. 点击清单项携带精确 `SelectedInstanceId`、上下文目标和播放头／布局参考进入统一工作区；存在的指定实例 ID 优先于相同绑定的首项，列表重建／移动保持当前选择 ID。指定 ID 已失效时，回退到现存的相同完整绑定实例，再回退到首个现存实例；删除操作只选择现存项。新增实例、源码修订接受、保存、预览及撤销继续走现有协议，不因定位自动创建、启用或保存实例。
6. “定位绑定对象”只对存在的 Node 绑定开放。工作区有未保存内容时先完成现有 Save／Discard／Cancel 离开协议：Save 成功或 Discard 后才发布节点定位请求，保存拒绝和 Cancel 保留工作区及草稿。关闭继续取消并等待预览／编译释放；主窗口随后重新检查实际工程节点。场景存在未应用／已过期草稿或目标已缺失时拒绝定位并显示失败，不能通过定位覆盖或提交场景草稿；定位成功仅改变编辑器页面和选择，不写工程或增加撤销项。
7. 程序升 MINOR **0.36.0**；工程／歌词／模板 schema **26／16／5**、package profile2、作者 SDK、Shader ABI、程序集集合和依赖保持不变。本批共享查询属于宿主投影，不新增作者 SDK 方法，不解除组图／父捕获、复杂映射、跨输出和其余高级输出门禁。

### 证据

回归覆盖未装载源关联、真实槽位轮换／反向恢复、前奏准备与起唱的片段连续性、父组自身绑定关联、禁用失效集合及多个同绑定实例顺序；实例 ID 选择、重建／保存／删除回退、请求转发和未应用／过期场景的只读定位另有 App 回归。共享范围的实际宿主输出与权限验证、构建／全量回归、原生控件和最终启动证据记录在 roadmap §18.100；这些自动证据不代替用户人工验收。

## ADR-0134：R7 直接图运行与模板候选闭包（0.37.0，2026-10-05）

### 状态

已实施本计划第5批 R7 的直接图基础运行、模板引用闭包和连续自动回归；完整 R7、R3.1、R8、模板分享／导入及最终用户人工验收继续。实际证据见 roadmap §18.101。

### 决策

1. `EffectGraphDefinition` 作为真实导出声明进入 `CompiledFrameEffect`，控制器继续使用 `Artifact`／`EntryType`，直接图明确使用空 DLL／入口和包修订作为运行代次；`PackageIdentity` 保留包、修订和导出 ID，不伪造控制器或加载上下文。
2. 宿主用固定图资源句柄和参数直接产生 `ImageEffectBinding`，沿既有准备帧、目标校验、矩阵、透明输出、预算、诊断和 Skia 执行路径。当前只开放 TokenFamily、Line、TextMark；组图、额外 requires／跨输出、同目标多输出和非 ColorOnly 映射继续返回明确门禁。Tokens 的可读行范围不扩大 Line／Mark／Group 写权。
3. 含控制器的混合包仍先完成其全部源码与资源校验；选择图导出不能掩盖坏 C#。纯资源图包不创建 DLL 缓存。失败字段在直接图场景承载真实包修订，不能据此声称存在编译产物。
4. 模板工坊的导入包保留为当前草稿候选目录；中央试样、配方添加校验和保存只提取当前材质槽位与有序配方的精确引用闭包。删除配方或切换纯色仅移除不再引用的包，未引用候选不成为持久模板内容；缺失真实引用仍拒绝试样／保存。
5. 效果工作区新建实例按导出声明选择控制器或图，优先复用合法上下文；没有真实目标时保持禁用待修复绑定，不把占位 ID 宣称为工程对象。复制、参数、订阅、seed、顺序、保存和撤销沿原实例协议。

### 证据

直接图宿主新增 20 项回归，模板候选／闭包与工作区新增 13 项回归，正式 ProjectRenderSession 新增 3 项（普通／透明保存重开与模板工坊→套用→工作区→撤销／重做→重开）。将图分派临时恢复为旧控制器路径时，直接图回归 **19 项失败、1 项通过**，源码逐字节恢复后构建恢复通过。完整构建、全量测试及启动证据写入 roadmap §18.101；既有 Roslyn 内部 Emit 间歇风险仍保持原记录。

## ADR-0135：R7 模板配方原位编辑与顺序调整（0.38.0，2026-10-05）

### 状态

已实施本计划第5批 R7 的模板作者工作流接续；行模板 `TokenFamily` 制作、`.mktpl` 分享／导入 UI、R7 组合、R3.1、R8 和最终用户人工验收继续。实现合同见[配方编辑合同](../legacy-ui/scripting-r7-recipe-editing-contract.md)，实际证据见 roadmap §18.102。

### 决策

1. 工坊把新增与编辑分成两个模式。选择已有 `TemplateEffectRecipe` 时载入当前草稿闭包中完全匹配的包、修订和导出；编辑模式禁用添加，更新按钮只替换同一 `RecipeId`。
2. 更新保留 `RecipeId`、目标、列表位置和 `OverlayLayer`，可改名称、规范参数、包修订及导出。参数先由 `EffectParameterOverrides.Parse` 与包定义解析，候选完整配方再通过 `StyleTemplate.Create`，失败不改变配方、预览或保存版本。
3. 上移／下移只交换有序配方数组中的两个项，并以同一模板闭包校验；边界操作保持原顺序并报告状态。保存、复制、重开及模板套用保留配方身份和顺序。
4. 编辑模式随模板切换、放弃或成功更新清除；当前导出选择变化会保留尚未提交的编辑字段。未开放的 `TokenFamily` 行模板制作、分享／导入入口和容器／组高级效果不因本批改变。
5. 程序升 MINOR **0.38.0**；工程／歌词／模板 schema **26／16／5**、package profile2、作者 SDK、程序集、Shader ABI 和依赖保持不变。

### 证据

`TemplateWorkshopRecipeEditingTests` 覆盖编辑模式门禁、名称／参数原位更新保留 `RecipeId`、顺序保存重开和无效更新保护旧预览；定向模板套件与完整构建／回归、启动证据记录在 roadmap §18.102。

## ADR-0136：R7 行模板元素族制作与显式相对目标更新（0.39.0，2026-10-06）

### 状态

已实施本计划第5批的模板目标选择接续；分享／导入 UI、R7 组合、R8 和最终用户人工验收继续。合同见[相对目标合同](../../scripting-r7-template-target-contract.md)，实际验证见 roadmap §18.103。

### 决策

1. 使用现有 `TemplateEffectTarget`。行模板明确选择整行或行内全部元素族，默认整行；元素模板固定所选元素族。选择目标不提前提交，切换模板清除未提交字段与候选选择。
2. 整行／元素族候选分别要求 `lines`／`tokens` 绑定支持，元素族非图元输出必须为 `tokenFamily`。UI 筛选与 Core 模板校验共同拒绝非图元行／标记输出及组输出，不把声明 tokens 当作跨目标写权；非图元的语义不合法配方明确拒绝。澄清ADR-0103的行输出限制指实际目标writer；Overlay没有行／标记writer，行／元素模板均保留真实粒子包的line／textMark输出声明，来源、预算和图元层仍按绑定及原合同限制。
3. 选择已有配方先恢复目标，再匹配精确包／修订／导出；找不到时清除旧选择，保留该配方自身字段、身份及诊断，禁用更新。目标候选使用稳定列表，清单显示可读作用范围。
4. 扩展 ADR-0135 的更新边界：显式目标修改保留配方 ID、位置与图元层，参数与闭包验证后才提交。重新套用按已有真实来源槽更新；跨目标会建立新目标实例，不继承旧实例 ID／seed／enabled，新来源实例按首次套用规则启用，其他模板与直接实例保持。
5. 程序升 MINOR **0.39.0**；工程／歌词／模板 schema **26／16／5**、包 profile2、作者 SDK、程序集、Shader ABI 与依赖保持不变。

### 证据

Core 相对输出门禁、工坊目标候选／原位更新／保存重开／失败保护回归及宽窄原生控件验证记录在 roadmap §18.103；自动验证不替代用户人工验收。


## ADR-0137：R7 模板完整闭包分享与整批导入（0.40.0，2026-10-06）

### 状态

已实施模板工坊 `.mktpl` 分享／导入入口；R7 连续组合、R8 及最终用户人工验收继续。合同见[模板传输合同](../../scripting-r7-template-transfer-contract.md)，证据见 roadmap §18.104。

### 决策

1. 分享当前已保存的个人模板，包含静态源材质、有序配方和精确包闭包；内置模板先复制保存。当前未保存修改或尚未提交样式／材质／配方输入不隐式进入分享文件。查看已有配方的相同字段不算pending；成功添加／更新或取消清空表单，失败保留输入。分享拒绝库入口／受管包目录和父目录链接，防止替换破坏自身库。
2. 导入完整归档后整批合并：保留模板／配方身份、目标及顺序；同身份规范内容完全相同则跳过，同身份不同内容整批拒绝，不自动改 ID 或覆盖。来源工坊复制保存是产生新独立身份的明确操作；空归档拒绝。
3. 完整合并校验和重读磁盘当前库在发布前完成；已观察到外部修改、删除或损坏时拒绝，不能用内存旧状态覆盖。包先发布、索引最后替换，成功后才更新清单并选中首份新增模板。完全重复不写盘、不改选择／预览；失败可以留下无引用不可变包，但不公开部分模板。
4. 实际传输暂停编辑及二次传输；关闭先取消并等待，再处理草稿和画布释放。导入发布前允许取消，原子提交已开始则完成后同步 UI；分享沿已有唯一暂存／读回／刷盘／原子替换协议保护旧文件。后台校验不读取 UI 资源；旧渐变视图仅同步可编辑渐变字段，不在包材质模板切换时读取虚假纯色。材质候选重建拒绝控件回写空／默认选择，保持包来源与参数，并把两框填满属性栏。
5. 数据读取／保存不执行作者；导入选择后的正式试样继续独立编译／运行和旧图保护。程序升 MINOR **0.40.0**，schema **26／16／5**、profile2、SDK、Shader ABI、程序集及依赖保持。

### 证据

`TemplateWorkshopTransferTests` 及原生按钮探针覆盖独立闭包、重复／冲突、损坏／外部变化、未保存输入、失败保护、取消及正式试样；最终构建、回归和启动记录见 roadmap §18.104。自动验证不替代用户人工验收。

## ADR-0138：R7 连续组合的来源身份与自动验收边界（2026-10-06）

### 状态

已实施；本计划第5批自动出口的连续回归、全量、原生布局及启动通过，R7／R8用户人工验收继续；当前结果见[R7组合验收表](../acceptance/scripting-r7-acceptance.md)。依据已授权后续计划补连续证据，不修改作者SDK、数据格式或既有高级输出门禁。

### 决策

1. 连续链从实际工坊Add／Update／Move／Save开始，经VM完整模板分享／导入、删除来源资源、真实套用、精确实例编辑、一次历史提交、撤销重做与物理工程重开，最终以独立正式普通／透明会话核对像素和诊断。不能用预先构造配方或多个互不相连的绿色分项替代这条链。
2. 模板来源实例继续保持单目标与owner关系；通过真实复制生成脱离来源的直接实例来验任意绑定编辑，不放宽`TemplateOrigin`结构门禁。副本删除／排序、原实例调参／seed／订阅与源码单实例接受均按已有生产入口执行。
3. 容器链只验当前合法Node／组仿射及共同透明度，组图／父捕获等未实现能力不计入本计划自动出口。原生操作单独证明实际入口、宽窄切换、旧图可见、诊断定位及关闭等待；自动证据和用户人工验收分别标记。
4. 仅测试／文档的验收批次不递增生产版本；若发现产品缺陷，按实际修复提高版本并补回归。完整R7用户人工验收与第6批R8、作者文档和原路线高级能力继续，不能因本批绿色测试宣布完整R2～R8交付。
5. 本批真实原生链发现包材质模板选择崩溃，影响比较改为typed完整材质／描边值，避免读取历史纯色shim；纯色、渐变、包及修订／参数差异保持真实，不用虚假代表色或异常吞掉替代比较。作为普通缺陷修复升PATCH **0.40.1**，schema26／16／5、profile2、SDK及依赖保持。
6. 连续作者链发现模板闭包包数组在保存和重读时按不同引用顺序枚举，误触发规范内容核验失败。资源库存按PackageId／Revision的Ordinal顺序规范化；它没有执行顺序语义，模板、写入属性、配方及工程实例的有序列表保持实际顺序。修复编码／重读一致性，不把资源顺序漂移当作身份冲突，不放宽包文件或摘要校验。
7. 原生截图复核发现窄窗失败时错误／诊断的Auto行挤掉画布，旧字虽保存在Canvas却无法查看。预览剩余区域按画布／详情2:1分配，完整状态、错误和诊断在独立纵向滚动区保留定位按钮；成功与失败采用同一布局，不通过截断错误或降低门禁保住画面。正式宽窄复验需检查自然旧字可见、滚动后的真实诊断定位及关闭等待。

## ADR-0139：R8 第 1 步公开纯资源包与透明回退验收边界（2026-10-06）

### 状态

已实施；完成 R8 公开资源包、作者索引和自动生产回归。R8 完整成片、备份／取消恢复、用户人工验收及 R3.1 高级门禁继续。实际证据见[公开包记录](../acceptance/scripting-r8-public-packages.md)与 roadmap §18.106。

### 决策

1. 首批公开包固定为 `CustomInk` 材质、`LineGlitch` `ColorOnly` 整行图和 `GlassInk` 的媒体／真实下层两种材质。它们使用 profile2、SDK 协议声明 1.0.0 和当前 shader profile，所有 README、graph、shaderProgram 与 SkSL 都纳入 manifest SHA-256；不新增宿主格式、SDK 方法、程序集或依赖。
2. `LineGlitch` 只能改变真实整行颜色图，保留准备帧的字形、注音、几何和播放头；不把像素分片位移解释成 `ImageOnly`／`MappedWarp`，Token、组图、父捕获、owned overlay、跨输出等关闭门禁继续生效。
3. `GlassInk` 的背景输入是显式声明的真实 `BackgroundMedia` 或 `BackdropBelow`。透明输出跳过依赖图并使用包内固定 solid fallback，保留 `TransparentBackgroundDependency`、MaterialHandle 和 `PreparedMaterialOwner.Source` 路径；参数、shader／ABI、文件和目标门禁仍先校验，不能用透明模式掩盖错误。
4. 透明回退验收比较真实区域覆盖掩码、包内预乘基础色和样式 alpha，并严格检查诊断身份、输入名和来源路径。材质按 token／区域捕获重组，与无效果整帧路径的边缘抗锯齿字节允许不同；不能因此放宽包 fallback 颜色、alpha、覆盖或诊断合同。
5. 自动回归必须覆盖目录／`.mkfx` 闭包离开来源、工程物理重开、普通／透明 Preview 与 Export 独立会话、背景缺失、坏 shader／参数以及 RepeatEmphasis 已唱 solid 基线。此批只增加示例／测试／文档，程序保持 0.40.1；同一作者工程完整成片和取消恢复另行验收。

### 证据

R8 公开包定向回归 **10 项全部通过**，包含目录未声明文件拒绝、目录／归档字节闭包、物理重开、透明回退与诊断、坏 shader／参数、媒体缺失和 RepeatEmphasis 组合；solution 串行构建 0 警告／错误。完整测试、启动和远程提交仍按本批收尾记录写入 roadmap §18.106。

## ADR-0140：R8 同一作者工程的完整成片与恢复证据（0.40.2，2026-10-06）

### 状态

本计划第6批R8完整成片与恢复自动验收已通过，最终构建0警告／错误及3187项全量通过；92项原生固定输入断言通过，交付证据见[成片验收表](../acceptance/scripting-r8-acceptance.md)／roadmap §18.107。
2026-10-06用户随后确认文字脚本、粒子脚本与资源效果均无问题，并接受当前goal完成；总体验收及范围见ADR-0141／roadmap §18.108。原路线高级能力仍为后续工作。

### 决策

1. 接续既有57秒六句／翻译／三槽／跨片段前奏案例，以真实工坊制作、分享导入和歌词套用贯穿九个公开包。
   模板未唱CustomInk、描边GlassInk、已唱solid，整行ColorOnly LineGlitch与两种文字强调使用明确相对目标；
   四个粒子包保存实际绑定／订阅／seed及Jump／Bridge参数，不另造简化的短片冒充完整成片。
2. RepeatEmphasis实例通过真实工作区订阅主分支，保持单元素写权；准备帧另验重复次数1／2／6、
   缩放／已唱亮度、注音跟随、翻译写权和四类实际粒子，避免两个独立会话同时漏执行仍给出假阳性。
3. 工程、副本和备份各模式复查完整闭包与六个关键时刻；普通／透明完整编码各选搬移后的同一工程，
   1710个输入帧逐字节比较独立正式预览，再完整流式解码全部帧和EOF。RGB／Alpha容差仅用于有损编码后的解码，
   编码前像素与脚本／图／材质诊断不使用容差。不把副本／备份的关键帧说成也全部编码。
4. 每种格式先经历30秒作者Error和12.266666秒活动帧取消，再以新token成功导出同一固定工程。
   先生成同作者工程14秒可播放旧片，Error提交900帧，取消在第369个输入帧确实写入管道后触发；
   提交序号独立于异步写入进度，不以后台瞬时计数作为逐帧预览序号。
   旧片全部字节、未Complete、全部帧／会话及实际作者上下文释放、临时文件清理共同验收；
   原生预览探针及用户人工验收与API／ViewModel证据分开。
5. 真实可复查工程、模板、普通／透明成片、报告／探测和hash留在显式证据目录，
   由实际运行产出。默认临时回归的绿色结果不自动等于已向用户交付成片。
6. 本批发现透明报告无条件记x264配置的普通缺陷，按实际请求记录ProRes4444参数，
   普通档继续记录真实CRF／preset。修复不改变编码器行为；程序升PATCH0.40.2，
   schema26／16／5、profile2、shader ABI、SDK方法和依赖保持。
7. 报告截图复核发现首次导出动态列表的XAML默认索引在空集合上被重置为-1；
   改为加入全部选项后设置MP4／标准质量默认项，原生实际控件先捕获-1失败，再检查索引及非空名称。
   不以报告文本可见代替格式／质量控件可用，最终构建与全量回归在此修复后重新执行。
8. 真实MOV报告的音频Reason沿用MP4规划，AAC源还会误报AudioCopied=true；仅按实际透明编码分支修正报告为pcm_s16le及未直接复制。PCM／AAC两源×两格式实际短片交叉核对报告与FFprobe，FFmpeg命令、正式像素路径和帧协议不改。最终全量在这一修复后重新执行；会话消失且无终态的final3不计通过。

## ADR-0141：按用户总体验收收口当前脚本goal（0.40.2，2026-10-06）

### 状态

用户已明确接受；当前goal已设为complete。完成度权威记录见 roadmap §18.108，[用户反馈与复查路线](../acceptance/scripting-r8-acceptance.md)保留实际验收范围。

### 决策

1. 用户确认“文字脚本、粒子脚本与资源效果均无问题”，并明确表示可以认为goal完成。当前goal按后续计划第1～6批的限定范围收口，完成状态取代此前暂停与等待人工反馈。
2. 总体人工反馈与既有自动证据分开记录，不推断全部文件门户、失败／取消交互或成片视听细项均已逐项人工检查。
3. 原路线完整SDK、R3.1、父组捕获、复杂映射及跨输出等尚未实现能力继续保持门禁，既往Emit／TypeLoad间歇风险不记作修复；不把本轮goal完成等同整个原始路线全部交付。
4. 本次仅更新文档，不改代码、包修订或依赖，不递增程序0.40.2、schema26／16／5或profile2，也不重复运行已完成的长片回归。后续开发由新的用户任务安排。


## ADR-0142 — UI 任务重设计、启动首页与 SukiUI 全面迁移（2026-10-06）

### 状态与来源

用户批准前一代理的完整计划并要求直接实施，随后确认“接受原型，按此实施”。本决策取代 ADR-0045 的后续 UI 外观供应商与模拟资源层选择；当前生产迁移的完成度以 roadmap §18.109 起为准，不能以批准替代实现证据。

### 决策

1. 首页提供新建／打开／最近工程及备份恢复；两个常驻任务为歌词与打轴、画面与样式，三个高级任务以可关闭的主窗口工作区按需进入，共用底部播放与波形。
2. 原生 SukiUI 7.0.1、Avalonia 12.1.3 固定；清除 Semi 依赖、模板部件耦合和 Material 模拟层。自有样式限定于编辑器布局、专业绘图区和必要密度，界面主题仅为应用级设置。
3. 基础外观即时合法值、一次手势一次撤销；明确整首／行／文字目标。编排、模板与源码保留显式草稿及现有领域合同，保存／编译／单实例接受／运行验证保持区别。
4. 保留项目路径、产品程序集、领域模型、正式预览／输出、schema26／16／5与包profile2；程序交付升版本，严格版本门禁继续拒绝旧工程，不追加升级器。
5. P0 原型已由用户接受；兼容验证失败或新的结构／交互取舍仍需询问。独立原生关卡已验证固定组合及当前 Linux 环境，ColorView 模板和资源初始化按固定版本官方 Demo 接入；真实 IME、Windows及最终用户制作验收另行记录。
6. 按独立分支、每步构建／测试／文档、提交并推送推进；不擅自委派代理。

完整能力去向、编辑／离开／异步合同及P0～P6验收见 [UI施工计划](../legacy-ui/ui-redesign-and-suki-migration-plan.md)，独立探针与依赖见 [Suki兼容关卡](../legacy-ui/ui-suki-compatibility.md)。


## ADR-0143：Suki生产工作区、合法即时编辑与短窗可达性（0.41.0，2026-10-07）

### 状态

已实施。用户接受P0后按ADR-0142施工，完整入口及自动／人工证据见[使用指南](../legacy-ui/ui-workspace-guide.md)、[验收记录](../legacy-ui/ui-redesign-acceptance.md)及roadmap §18.110～18.112。

### 决策

1. 保留原App路径、程序集与领域模型。MainWindow负责首页／外壳接线；StartPageView、AppearanceWorkspaceView、PlaybackBarView和WorkspaceView分别负责首页、作用范围、共享播放与临时工作区生命周期。既有窗口类名保留引用兼容，歌词／模板／效果作者界面作为内嵌工作区呈现，不复制工程状态。
2. 两个基础任务共享同一EditorViewModel／ProjectWorkspace／命令历史。外观输入固定BranchId／LineId／TokenId，合法值即时提交，非法文本留在字段中；焦点、指针及颜色Flyout边界合并历史。切换任务／目标、撤销前结束手势；恢复继承与无变化继续由原命令语义决定。
3. 基础布局保留已有节点与片段身份，只调整选定绑定的布局目标，段落选择扩展到完整交叉片段；新增独立容器与槽位，引用保持。预览影响后才执行单个SetPresentation历史事务；自定义结构进入高级编排，不删除复杂树。
4. 工作区按稳定目标或精确InstanceId复用；切换保留草稿。离开采用准备检查后关闭释放，任一取消保留全部页；编排应用、模板保存、效果保存、源码编译、单实例接受与运行验证分别保留。工程IO期间阻止再导航，窗口关闭等待预览与音频释放；关闭后迟到包读取不写工作区。
5. SukiWindow／SukiTheme与原生对话／通知宿主覆盖全部生产窗口、弹窗及动态控件。ColorView按固定官方Demo加载上游模板；Application.Resources先初始化。SukiBackground为透明合成资源，编辑器及覆盖面板使用不透明SukiCardBackground／StrongBackground；专业自绘区保留自己的语义颜色。许可证随产品构建／发布归档。
6. 宽窄基础任务保留共享时间轴；高级编排按布局／分配／预览分阶段减少同时占用。短窗中预设／效果中央内容提供垂直滚动，保存与关闭仍常驻，避免内部列表被Auto工具条挤至零高度。完整错误仍能从提示或诊断滚动查看。
7. 本次改变用户可见工作流，程序升MINOR0.41.0；schema26／16／5、profile2与严格版本门禁不变。旧工程只读拒绝、不转换；完整SDK、R3.1、组图／父捕获等既有未交付边界不扩展。
8. 用户指出原生自动化影响使用后停止所有窗口测试，不再自动运行。已有合成器证据保留；最终短窗修正使用无窗口布局检查，未重新进行桌面像素检查。真实中文IME、物理操作、多屏DPI、Windows及用户连续制作验收单独留待验证。

## ADR-0144：人工反馈的弹层实底与同级任务导航（0.41.1，2026-10-07）

用户在0.41.0实测指出高级／诊断／帮助／取色等弹层透明、视图菜单仍含脚本入口、任务页签及按钮样式不一致，直接授权修复。

1. 固定Suki7.0.1的FlyoutPresenter模板将LayoutRoot写死为Transparent，Background属性未参与绘制。以原生SukiFlyoutPresenterStyle为基础，仅替换该外壳为绑定背景、边框、圆角及原生ScrollViewer；弹层表面集中读取SukiPopupBackground，不更换Flyout输入、焦点、关闭或手势生命周期。菜单／右键／提示／色板共用同一语义表面，ComboBox保留原生实底模板。
2. 全局高级能力集中到“高级”，移除视图菜单的效果入口及事件接线；选中高级入口后收起菜单。歌词／对象／精确实例上下文入口保留。
3. 常驻及临时工作区统一workspaceTab原生ToggleButton，由NavigateTask排他设置IsChecked；同一字号、留白、高度、前景和淡色底／底线规则。不再以Flat主操作样式或整页签透明度表达选中。导航状态即时更新，关闭当前高级页仍按原离开门禁处理并回到歌词任务。
4. Flat／Outlined／Basic仅表达主／次／辅助动作，标准文本按钮36px、14px字，紧凑区32px、13px字；统一留白并垂直居中，避免父菜单行拉高按钮。图标、系统窗口按钮、色块和卡片按自身形态使用专用尺寸。
5. 作为普通UI修复升PATCH0.41.1；schema26／16／5、包profile2、固定依赖、命令历史及严格程序版本门禁保持。验证使用Headless真实控件树与Skia离屏弹层像素，不恢复桌面自动化；窗口位置、物理取色和输入焦点仍留用户复查。自动结果见roadmap §18.113与[验收记录](../legacy-ui/ui-redesign-acceptance.md)。


## ADR-0145：三工作区职责、组合预设和独立脚本配置库（2026-10-07）

用户批准[三工作区方案](../legacy-ui/ui-workspace-ownership-plan.md)并启动开发目标。本决策取代ADR-0142～0144的后续导航／样式作者职责及分段范围规则；原生Suki、弹层、渲染、历史和安全闭包合同继续保留。批准不等于已实施，完成度见roadmap §18.114起。

1. 三个常驻同级任务为歌词与时间、模板制作、画面与样式。歌词只处理文本和行／逐字时间，使用无样式依赖的中性实时预览；实际行空档显示···，不猜间奏，移除按键实时落点。
2. 所有外观、材质和效果先制作组合预设；同一对象只选择一个，局部外观覆盖并继承上级效果。应用保存固定快照，模板／配置库改变不自动跟随；移除恢复继承，手动更新为原子历史事务。
3. 工坊与当前工程独立，固定试样沿正式渲染，拥有局部试播；不显示工程播放、波形、时间轴或歌词列表。独立脚本窗口制作可复用材质／效果配置及固定源码修订；全局是范围而非第三种脚本类型。
4. 画面区保留所有现有支持的绑定种类，布局与样式分离。高级编排是区内模式，预览禁用脚本并以声明回退显示包材质，实际源数据与正式输出不改变。默认卡拉OK双行锚点为5%／60%左中心和95%／80%右中心。
5. 布局精确修改连续选区，必要时拆分片段，维护已知引用、稳定实例及种子；不能安全维护时整次拒绝，不静默扩大选区。各工作区具名辅助面板，窄窗一次打开一个。
6. 集中UiControlMetrics规范标准36px、紧凑32px及字号／留白／面板；AXAML与动态控件共用，不任意发挥尺寸。模板／脚本草稿拥有自身历史，工程继续只有ProjectWorkspace；关闭和迟到结果受统一门禁保护。
7. 目标0.42.0、工程／歌词／模板schema27／16／6、共享脚本配置库1、包profile2；旧工程和旧模板库原样保留不转换，不扩展未交付SDK。第1步仍保持当前0.41.1／26／16／5。
8. 用户在本目标明确重新授权窗口交互测试和截图；必要时使用专用窗口与隔离配置，不操作既有窗口、不留下自动化循环。默认Headless，自动证据、真实原生证据和用户人工验收分别记录。

## ADR-0146：固定组合预设应用的源合同与原子历史（0.42.0施工版，2026-10-07）

依据已批准ADR-0145实施第2步模型／存储，当前尚未完成正式渲染与新UI接线。实际进度见roadmap §18.116及[合同§8](../legacy-ui/ui-workspace-ownership-plan.md#8-第2步进度组合预设与固定工程应用0420施工版)。

1. 复用StyleTemplate及完整包闭包，新增Composite类型与Application范围配方。配方保存独立读取范围、具名输入、初始启停和种子策略；全局是应用范围，不是脚本类型。屏幕位置与对齐不进入组合预设声明属性。
2. 每个具体对象只有一条PresetApplication，集合绑定多选由事务建立多条。记录完整固定快照、真实输入映射和配方／相对成员身份；现有Project.Effects包库存注册完整资源，运行实例由投影产生，不同时保存重复实例真值。
3. 同预设手动更新保留匹配配方／成员的实例ID、种子及启停；新增成员创建新身份，单成员绑定变化保留同配方身份。替换不同预设建立新配方身份；移除仅清除直接应用，祖先继续。一次SetPresetApplicationsCommand进入现有历史，无变化返回原工程。
4. 静态属性按工程、分支、实际场景父链、片段、行、文字逐级解析；效果祖先先于局部且内部保留配方顺序。工程／分支控制器即使选择相对行／文字目标也保留单个范围实例；节点继续使用动态node绑定，不能将槽位父关系展开为固定词／行绑定。相对目标与实际声明的绑定能力共同过滤，既有组写权门禁保留。
5. 工程schema27必填固定应用入口，模板schema6/templates-v6，不读取或覆写旧库，不增加迁移。程序按已批准破坏性工作流升MINOR0.42.0；歌词16、脚本配置1、包profile2及既有SDK边界保持。旧程序版本先拒绝，资源先发布根入口最后替换，备份／副本独立保存全部资源。
6. 固定应用的结构／快照／完整身份错误整批拒绝；歌词编辑导致的对象失效保留快照并诊断，不静默改绑，允许保存和明确移除。当前纯投影助手不证明正式画布或输出已消费，生产接线及新三个工作区继续开发，不把施工版本视作完整交付。

## ADR-0147：画面与样式工作区只应用预设（0.42.0施工版，2026-10-07）

### 状态

已实施第一步；应用入口和目标确认已接入，歌词时间区与完整模板／脚本工作流继续施工。实际证据见 roadmap §18.119。

### 决策

1. 画面与样式工作区不再直接编辑字体、颜色、描边或阴影字段；这些内容必须在模板工坊制作并保存为行／文字预设。画面区只显示当前目标、直接应用的预设／继承状态和布局操作。
2. 目标选择映射为工程、具体行或具体文字的稳定 `ProjectEffectBinding`。选择预设后先调用 `PresetApplicationsEditor` 预演，显示影响范围；确认后使用 `SetPresetApplicationsCommand` 将静态样式与效果配方作为一条可撤销历史提交。
3. 预设应用不写入歌词的样式覆盖源；替换、重复应用和撤销遵循固定快照及实例身份合同。非法目标或失效选区在提交前拒绝，保留原工程。
4. 布局预设与样式预设保持独立；高级编排继续作为画面区的临时工作区入口。旧 `AppearanceEditContext` 和底层即时字段命令暂时保留给兼容的旧作者窗口与测试，不作为三工作区主入口。
5. 应用状态卡片使用统一 Suki 表面和集中控件规格；不在动态代码中指定自定义尺寸。Headless 布局、目标应用撤销测试和零警告构建是本步出口，真实桌面连续制作仍需用户验收。

## ADR-0148：歌词与时间工作区的中性实时预览（0.42.0施工版，2026-10-07）

### 状态

已实施首个预览控件；歌词编辑、导入和时间轴仍沿用现有命令与共享播放状态。实际证据见 roadmap §18.120。

### 决策

1. 歌词与时间任务隐藏正式带样式 `KaraokeCanvas`，显示 `NeutralLyricsPreview`。它只读取 `ProjectSnapshot.Lyrics`、当前分支和播放头，并调用统一 `LyricSampler`；不读取工程样式、场景布局或效果实例。
2. 有效行区间显示整行文本；当逐字时间与行文本一致时，用中性颜色区分已唱／当前元素。缺失逐字时间不制造进度或高亮，避免把估算时间伪装成逐字打轴。
3. 播放头不在有效行区间、分支无可用时间或间奏时显示固定 `···`。该提示只属于编辑器预览，不进入正式渲染或导出。
4. 切换到画面与样式或高级工作区恢复正式画布；同一播放头、循环和快捷键状态继续共享。切换只改变呈现控件，不复制工程状态或时间语义。

## ADR-0149：模板工坊下属的独立脚本配置窗口（0.42.0施工版，2026-10-07）

### 状态

已实施首个生产入口；完整应用对象清单、基础编排模式和连续制作验收继续施工。证据见 roadmap §18.121。

### 决策

1. 模板工坊的“脚本工作区”打开独立 SukiWindow。窗口不读取当前工程、歌词列表、播放头或波形；它只读取应用级 `script-configurations-v1`，维护材质／效果配置及完整固定包闭包。
2. 配置编辑区按实际导出分类为材质和效果。材质固定为模板材质槽位所需的应用范围／绑定来源；效果可配置相对目标、读取范围、分支输入、种子和图元层。全局继续表示应用范围，不成为第三类脚本。
3. 源码编辑、保存新修订、编译、诊断、取消和 `.mkfx` 分享在同一独立窗口中进行。保存源码只产生新的不可变包修订，已有配置不会自动升级；配置保存仍使用严格的包先发布、入口最后原子协议。
4. 工坊面板加载共享配置并提供“应用到当前模板”：材质复制到当前材质槽位，效果复制为带 `ConfigurationId` 的模板配方。复制后模板保留独立包闭包和参数，后续库修改不改变已保存模板。
5. 窗口草稿切换、配置库外部变化、关闭、编译取消和离开确认都拒绝静默覆盖；配置保存／源码保存／编译验证／分享分别保持明确动作。输入撤销／重做按同一字段手势合并。
6. 普通按钮、源码输入和面板宽度引用 `UiControlMetrics`；独立窗口接受统一密度与短窗滚动检查。Headless 仍为默认验证，原生窗口仅使用隔离配置。

## ADR-0150：画面区应用对象清单与工坊组合预设（0.42.1施工版，2026-10-07）

### 状态

已实施应用对象管理和组合预设制作入口；基础编排、窄窗具名面板和连续制作验收继续施工。证据见 roadmap §18.122。

### 决策

1. 画面与样式工作区提供统一的应用对象目录，覆盖工程、歌词来源、歌词行、文字、展示片段和画面节点。目录按稳定 `ProjectEffectBinding` 展示直接预设、继承状态、失效引用和节点后代策略，不把当前选中行偷偷扩大成整首歌词。
2. 目录上的替换／应用、启用／停用、同身份更新和移除均通过 `EditorViewModel` 的工程命令进入同一撤销历史。移除只清除当前对象的直接应用；更新要求选中相同预设身份并保留已有实例 ID、seed 与启停状态。
3. 工坊新增 `Composite` 组合预设创建入口。它可以组合基础样式、材质和脚本配方，但不包含屏幕位置和对齐属性；这些属性继续由画面区布局或高级编排负责。组合预设沿正式固定试样预览，保存后才进入 templates-v6。
4. 组合预设的脚本相对目标可选整行或逐字族；元素模板仍固定逐字族。组合预设试样借用整行几何作为静态预览承载，不改变工程应用时的稳定目标解析。
5. 应用对象操作和工坊类型选择使用现有集中控件密度；Headless 布局检查覆盖对象目录、动作按钮和短窗可用性。当前程序升 PATCH 0.42.1，工程／歌词／模板 schema 27／16／6、脚本配置库1、包profile2保持。

## ADR-0151：画面区内部基础编排预览（0.42.2施工版，2026-10-07）

### 状态

已接通内部编排模式、基础投影和草稿离开检查。窄窗具名面板、歌词区完整清理与最终连续制作验收继续；证据见 roadmap §18.123。

### 决策

1. 高级编排从画面与样式的布局入口进入，保持画面页签选中，不生成第四个任务页签，也不在全局高级菜单重复提供入口。返回动作明确回到画面与样式；主任务切换保留原编排会话和选择。
2. 基础编排的独立不可变预览快照保留歌词、布局、前奏和组合预设声明的静态属性；直接实例和预设配方不执行。所有源材质（工程、分支、行、文字、提示组、标记和应用快照）使用固定包声明的完整纯色／渐变回退，失败源码无需编译。
3. 基础快照不包含效果输入指纹，几何、主编排画布与前奏候选试样消费同一规则。按源工程引用复用快照，草稿改变时重新投影；回退失败拒绝进入或保留上次合法基础画面并显示错误，不能悄悄执行完整脚本。
4. 基础模式保留设置布局、分配歌词段和时间预览；属性面板不显示脚本实例入口。模式切换不写工程，不删除资源、固定快照、实例身份或种子；正式画布与导出继续读取完整工程。
5. 内部模式的明确返回动作处理应用／放弃／取消。换工程或退出时，不论当前任务是否仍为编排，统一检查保留的展示草稿；应用作为一次工程历史提交，取消不丢草稿。
6. 编排动作采用 UiControlMetrics 的紧凑高度、字号和留白，返回动作与主导航采用现有同级规则。程序升 PATCH 0.42.2；工程／歌词／模板 schema27／16／6、配置库1、包profile2及精确版本门禁保持。


## ADR-0152：精确布局分段与原调度上下文（0.43.0施工版，2026-10-07）

### 状态

用户明确允许增加调度上下文并提升工程格式28，要求完整保留未选歌词布局。四种布局、原子精确拆分、引用映射及正式路径已接通；自动证据见roadmap §18.124，完整作者与人工验收继续。

### 决策

1. 首批布局为单行居中、卡拉OK双行、双行居中、三行居中，坐标／锚点按三工作区方案§2。图示和动作沿用集中控件规格，布局只改变编排，不写静态外观或脚本参数。
2. 连续选区不扩张。完整选中复用片段ID；部分选中把原ID留给最早未选部分，其余部分取得新ID。保留原树、其他分支及前奏，成功作为一次工程历史；预演失败、取消、迟到结果与无变化不写工程。
3. 工程28新增可空 `scheduleContext`，包含原有序源 `lineIds`、`isDefaultSegment` 及可空的 `previousLineId`／`nextLineId`。显式段由原成员首末时间裁切，默认段由相邻片段末行／首行时间裁切，无前后边界分别是0／无穷。上下文不保存时间或槽位副本、不扩张实际片段成员；后续时间编辑读取当前源，源删除显式清理。
4. 未选部分沿原容器规则读取完整上下文，只输出本段实际成员；正式场景、静态编辑几何及持久化共同保持槽位序号和揭示／隐藏时钟，允许未来的未选歌词在选区内提前显示。非法上下文报告且阻止导出，不静默修正读盘数据。仅重命名片段保留上下文，主动改成员或容器建立独立调度。
5. 直接片段效果绑定映射到全部拆分结果，保留实例ID、种子、参数和启停。静态应用快照复制到每段，逐行／逐字族相对配方将原身份按实际成员分配，原应用ID跟随原片段。单个Application范围配方不能安全拆成多份应用时整次取消并说明冲突，全片段移动保持支持。
6. 程序提升不兼容MINOR0.43.0，工程28；歌词16／模板6／脚本配置库1／包profile2及固定依赖保持。旧工程按精确程序与格式门禁拒绝，不转换、不覆写。
7. 固定Suki7.0.1的原生ComboBox下拉外壳补齐语义底色，嵌套图示文字使用主题前景；保留原模板、焦点、键盘和弹层生命周期。验证需要检查实际根部及内容像素，不能以存在一个不透明子Border代替整层验证。


## ADR-0153：独立模板作者状态、固定试样与完整快照复制（0.43.1施工版，2026-10-07）

### 状态

依据已批准三工作区方案§2～4／10第2项实施，无新增结构取舍。首页独立入口、自有试播、相对范围、固定快照复制及工坊历史已接通；验证见roadmap §18.125。歌词清理、具名面板、完整连续制作与人工验收继续。

### 决策

1. 主窗口明确区分首页、实际工程与独立工坊。首页可直接打开工坊，不创建工程；工程任务和文件动作不可用。进入工坊暂停工程播放，隐藏工程保存／导出／保存状态与工程时间轴，标题不含歌曲名。自动备份只消费实际工程状态。
2. 工坊只消费自己的12秒固定试样、测试背景、48kHz PCM和真实音频分析；注音、多行、重复词、歌词空档及前奏标记均为正式源／场景。相对试样对象按预设实际合同筛选，当前试样只保留一个应用，按目标复用自身身份与种子。
3. 工坊试样使用正式PresetApplication投影、ProjectSnapshot及异步渲染会话，不降级Composite为旧行模板或复制歌词源覆盖。自有IAudioPlayback控制试播／暂停／重播／循环，离开停止，非循环结束停止计时器；激活代次拒绝迟到播放，关闭等待加载和预览释放再删除资源。
4. 配方提供合法Application／整行／逐字族相对范围，候选共用ScriptConfiguration实际写权门禁。编辑保留RecipeId、订阅／输入名、seed、enabled、图层和配置来源；启停和排序进入作者草稿。没有扩张组图或其他尚未交付SDK能力。
5. 画面区只从工程的完整固定StyleTemplate快照复制作者草稿，不读取外部库；保存产生新预设ID，保留静态声明、材质、配方及固定资源，不复制工程对象／应用身份或改写工程。已有作者草稿先统一处理保存、放弃、取消。
6. 工坊拥有独立样式／名称／资源／配方历史。同次输入、滑块及取色合并，试样目标和播放不进历史；保存后撤销仍可产生新修改。主窗口历史路由到当前工坊，文本框保留文本撤销，Ctrl+S保存模板。回首页、换工程、退出仍检查保留的模板和脚本草稿。
7. 程序升PATCH0.43.1；工程28、歌词16、模板6、配置库1、包profile2和固定依赖保持。旧工程继续精确版本门禁，不做转换或覆写。


## ADR-0154：歌词源编辑职责、中性关联采样与一次LRC确认（0.43.2施工版，2026-10-07）

### 状态

依据已批准三工作区方案§2／10第3项实施，不增加持久格式或SDK能力。歌词编辑职责清理已接通；自动证据见roadmap §18.126。具名窄窗面板及完整连续制作仍在施工。

### 决策

1. 歌词内联编辑器只提供源文本、行／逐字时间、注音、重排、拆合和平移试听；移除模板／样式／脚本／前奏入口与重复隐藏时间轴，主窗口旧逐字表单退出。样式影响比较保留原领域断言并移到预设帮助类。
2. 来源设置只修改名称、语言、时间模式与主行关联，已有静态外观、预设快照及画面布局保持。新增翻译不写字号或布局；外观及编排由对应工作区制作和应用，源元数据仍一次历史提交。
3. 中性预览与试听只调用歌词时间采样器。FollowPrimary的关联行使用主源真实行区间，未关联行用自身时间；失效关联或None不猜测区间。翻译逐字进度只来自自己的真实时间，不制造字轴，不跟随场景提前显示和进入动画。
4. 歌词任务移除实时按键打点；文本拆合／时间微调／时间提交快捷键限定在歌词任务。播放、定位和循环仍为工程共享动作，文本框不抢键。时间变化复用稳定卡片、保留输入焦点，显式目标切换更新字段与可用状态；非法时间保留原文和最后合法值，并就近显示错误。注音回填比较目标及当前值，不用短时布尔保护延后事件。
5. 增强LRC损失通过一次明确对话确认，取消或迟到的文件选择／确认不写文件。平移和重排也检查确认前的工程、分支及稳定选择，避免提交到其他对象。试听结束暂停在本行尾，任务离开终止试听计时器。
6. 新控件尺寸引用UiControlMetrics，普通动作沿用标准36px，文字卡片使用紧凑32px，时间码输入使用统一130px；程序升PATCH0.43.2，格式28／16／6、配置1、包profile2及固定依赖保持。旧工程继续精确门禁，不转换或覆写。


### ADR-0155：具名面板、窄窗保留中央空间及统一控件规格（2026-10-07，0.43.3施工）

承接ADR-0145～0154及已批准方案§2／§4。通用侧栏开关退出主窗口：歌词任务提供“歌词来源”，工坊提供“预设库／样式编辑”，画面任务提供“应用对象／预设库／布局／合成”。面板切换复用原控件、稳定对象和草稿，不建立另一份工程状态。

宽窗资源库240px、属性面板360px。窄窗工坊与高级编排一次只展开一个辅助面板；面板占用明确列宽，中央画布保留可用空间。窄窗中性预览放在编辑区下方；源编辑器整体可滚动，首屏保留可编辑文本，不以空宿主的尺寸代替字段可见性。正式画布和提示使用独立视觉层，不能盖住歌词区的 `···`。

工坊取消固定420px高度和整体页面滚动，预览填充任务区域、编辑属性局部滚动，内置／个人库共享可滚动的页签区域。试样播放器仍只操作工坊资源。高级编排使用同一具名面板规则，草稿保存／取消和正式输出语义保持。

AXAML和动态普通控件的动作、输入、文字层级与面板尺寸共用 `UiControlMetrics`，同一动作条采用标准36px或紧凑32px。专业画布、波形、布局图示和颜色绘图区保持自身几何语义；不修改Suki原生窗口部件。程序升PATCH0.43.3，格式28／16／6、配置1及包profile2保持。自动证据和未完成的连续制作／人工验收见roadmap §18.127。

## ADR-0156：预设作者唯一入口与独立源码离开安全（0.43.4施工版，2026-10-07）

依照三工作区方案§10第5项清理生产入口。主窗口只提供三个同级任务；“高级→效果编辑”、隐藏编排按钮、临时效果页签与重复模板创建路径退出。编排只能由画面任务内部进入，脚本只能由模板制作打开独立窗口并复用。旧领域效果模型与正式运行能力保留，历史测试的纯领域API不作为当前GUI入口。

源码文本与配置字段分别处理保存／放弃／取消，重复离开请求共享确认；父窗口成功离开后关闭已准备的脚本子窗口。未保存源码阻止包／文件／配置切换和删除，编译冻结原生源码输入；取消编译等待完成，过期编译结果不覆盖后续文本。导入记录窗口生命周期与草稿代次，迟到结果不替换当前配置或已关闭窗口。

保存源码修订发布到脚本配置库已有的 `packages/<hash>` 不可变目录，不改变index、不自动升级配置，也不要求编译成功。独立作者重开时把这些完整历史包作为候选读取；链接、摘要、声明及统一展开预算受检，坏候选单独报告，当前配置仍由严格入口加载，不能消费坏字节。保存失败保留源文本与旧入口。源码编辑拆为独立ViewModel职责文件。

程序升PATCH0.43.4；工程28／歌词16／模板6、配置1与包profile2及依赖保持。连续制作、最终当前构建全量和用户人工验收仍未完成，实际证据见roadmap §18.128。

## ADR-0157：预设候选名称与实际应用目标一致（0.43.5施工版，2026-10-07）

承接已批准的三工作区合同及连续验收。新建Composite始终提供默认基线，不依赖个人库已有组合预设。进入画面任务刷新当前库；预设列表、新建基线与个人库显式使用名称作为TextSearch文本，包材质的纯色读取继续拒绝，不修改领域材质语义。

整首／当前行／所选文字快捷选择改变实际稳定ProjectEffectBinding，应用对象目录反向同步快捷范围。来源、片段和节点显示实际对象标题，不保留误导的行／文字选中。缺失对象不回退成工程应用。局部静态覆盖、移除恢复继承以及各一次撤销共用原工程历史。效果配方操作使用换行布局，普通按钮仍沿用UiControlMetrics。

连续作者与输出保护使用专用窗口、隔离配置及真实正式会话。系统文件选择通过既有宿主接口提供测试文件；编码失败由真实编码器接受十五帧后注入，不能记为真实作者脚本Error；取消使用原生按钮。完整长片领域测试、最新UI回归和人工验收分别记录。程序升PATCH0.43.5，格式28／16／6、配置1、包profile2与依赖保持；证据见roadmap §18.129及连续验收文档。


## ADR-0158：多选应用与明确的预设输入来源（0.43.6施工版，2026-10-07）

按已批准三工作区方案§2～3补齐已有领域能力的界面出口。多选仅扩大明确应用的对象集合，按全部目标能力筛选候选，所有目标及具名输入先共同验证、影响确认，再共用一条SetPresetApplicationsCommand。任一失效或确认期间改变工程／目标／预设／输入，整次不提交。

具名来源输入必须明确映射当前工程的稳定歌词分支，不默认为主来源；对象切换、更新及撤销重做显示对应保存值。同身份更新继续保留应用、实例、种子和启停。对象身份不因Node的IncludeDescendants开关建立第二条应用。

继承来源显示上级预设名称及实际对象，沿正式NodePath查询动态槽位与附着关系；当前没有装载的歌词明确提示等待，停用和自身直接应用不混入上级链。来源摘要是只读UI提示，不建立工程状态或另一套渲染解析器；每100ms播放摘要刷新不改变正式时钟。

视图分为目标目录、预设输入及外壳职责，普通动态控件沿用UiControlMetrics和Suki样式。PATCH0.43.6，格式28／16／6、配置1、包profile2及依赖保持。独立脚本正式运行验证与迟到音频保护仍待实施；证据见roadmap §18.130。

## ADR-0159：音频准备只发布当前请求且保护演示基线（0.43.7施工版，2026-10-07）

按已批准三工作区异步离开保护补齐§18.130暴露的实际问题。波形和候选播放器先完整准备，两个异步边界均检查请求身份／取消，最后共用已有音频命令。换工程、摘除及后发请求取消旧请求；底层忽略取消仍拒绝发布并释放候选。准备失败保留旧播放和工程，同工程普通编辑不因导入等待而丢失。

演示加载额外校验未经编辑的原工程，成功结果仍是当前工程才可AcceptCurrentAsSaved；不允许迟到启动初始化清除用户编辑或另一工程历史。窗口只负责接线，加载生命周期在EditorViewModel.AudioLoading；复用IAudioPlayback／既有媒体服务，无新状态模型、格式或依赖。PATCH0.43.7，实际证据见roadmap §18.131，脚本运行验证与最终验收继续。


## ADR-0160：独立脚本配置用自有试样完成正式运行验证（0.43.8施工版，2026-10-07）

承接三工作区方案§3及§10第5b项。独立脚本窗口提供与保存配置、保存源码修订、编译分别命名的“验证当前配置”。输入是完整不可变ScriptConfiguration及工坊试样目标，不接受当前工程／歌曲或实际对象。未保存源码阻止运行；已保存源码候选不会自动替换配置固定修订，报告明确提示如何选择新修订。

试样使用自有12秒PCM、背景和已接受分析，640×360／30FPS。普通和透明输出分别创建正式渲染会话，按正式编码的整数向下取整时刻各验证360帧，使用Final质量；全部帧、尺寸及像素所有权有效且没有Error才能通过。材质部位及目标按实际能力筛选，具名来源映射到试样分支，作者固定种子优先，否则只在试样使用固定种子、不写回配置。背景依赖透明回退保留真实诊断；编译及中途运行错误保留固定包、时间、源码位置或材质来源，不回退旧DLL。

运行期间冻结作者输入，滚动区之外保留取消动作。关闭和父窗口离开先取消并等待帧／会话／媒体释放；忽略取消的迟到结果不能报告成功。配置、源码候选或试样选择改变后旧结果失效，旧报告的固定身份保留。试样验证不是实际工程导出验收，不修改库、工程、历史或schema。固定依赖和格式28／16／6、配置1及profile2保持，实际证据见roadmap §18.132。


## ADR-0161：歌词应用目录、必填新建与明确分支导入（0.43.9施工版，2026-10-08）

用户手测要求画面与样式有直观的歌词行列表，新建工程先导入歌词和音频，追加歌词明确分支。AppearanceLyricsView按当前分支显示只读行与直接预设／继承状态，宽窄窗初次进入均可见；Ctrl／Shift多选传给原有稳定应用对象，选择不写文本或时间。宽窗选行打开预设面板，窄窗选行仍可继续多选，明确点击“选择预设”再切换辅助面板；“歌词行”按钮及视图菜单可重新打开。分支变化同步列表并清除旧目标，空选择不得重新猜选其他分支；工程、文字、片段和节点目录保留。

NewProjectPreparation独立解析主歌词、解码音频并准备候选播放器，全部成功后由EditorViewModel交接播放所有权并建立新工程。缺文件、无可用音频、空歌词、失败或取消不发布工程，不改原历史和播放；关闭取消等待候选释放。新工程含已导入数据，必须标为未保存，使用原自动备份和离开提醒。初始歌词明确为主分支；首页独立模板制作不要求工程或音频。

后续导入不默认选中任何分支，必须选择已有分支或填写新分支名称与翻译／注音／其他角色。替换非空分支先确认行数及引用影响。创建分支和导入作为同一命令，保留唯一主分支身份及其他分支；新翻译采用独立时间，不猜关联行，用户随后明确关联。文件选择、设置与影响确认返回都检查原工程身份，迟到结果拒绝发布。

新流程需要普通TXT歌词。新增text.plain导入器保留非空行、Unicode字素、编码报告及缺失时间，不制造时间或估算；实际LRC、增强LRC和UltraStar仍按内容优先识别。两个导入表单使用统一DialogMaxHeight，正文可滚动，确认／取消常驻。PATCH0.43.9，格式28／16／6、配置1及profile2保持，不增加迁移或SDK能力。实际验证见roadmap §18.133。


## ADR-0162：固定包声明驱动的脚本参数面板（0.43.10，2026-10-08）

用户要求在独立脚本工作区增加图形参数编辑，枚举用下拉，其余按类型选择控件；无法合理滑动的范围只提供精确文本框。本批直接消费已有EffectPackageDefinition、当前导出的ParameterIds、默认值、约束、单位、选项及固定图片声明，不另建参数格式或改变包修订。

枚举显示包内标签，保存稳定选项值；number／integer／duration仅在上下限有限、非相等、端点绝对值与跨度均不超过1,000,000时提供滑块＋文本。duration先换算为毫秒判断滑动范围；未满足条件的数值仅提供文本，不猜测或自动收窄范围。整数和时长保持完整Int64，时长用decimal毫秒输入且必须对应整微秒；文本输入不因显示滑块而量化。boolean使用复选框，text使用多行文本，asset仅从固定包声明的image文件选择。color使用已有原生取色入口及四个0～1分量输入，打开或显示取色器不量化未编辑的double颜色；明确选色才采用颜色控件的字节值。curve使用现有三种类型、缓动选项及控制点；X为0～1滑块＋文本，未限制的Y只用文本，内部点可增删。

有效覆盖仍由原ScriptConfiguration.Parameters持有并经Core.ResolveParameters校验。仅显示默认值不写覆盖；恢复单项默认删除对应键，全部恢复清除全部覆盖。非法字段保留原输入，覆盖保持最后合法值；其他参数的有效编辑不得清除其错误。非法原始JSON停用图形输入，仍可在默认折叠的“高级：参数JSON”入口修复。未知键保留并报告，不静默删除；两种入口写同一覆盖对象。

参数草稿输入加入现有独立作者历史，不写工程或库schema。连续输入、拖动、键盘滑动及一次选色合并，失焦、松键、实际Thumb捕获丢失或弹层关闭结束手势；撤销重做重建对应导出及非法输入，旧行的迟到回调不写新目标。非法输入参与离开检查并阻止保存／正式运行验证，保存源码、编译和采用修订的语义保持。普通AXAML及动态输入共用UiControlMetrics。PATCH0.43.10，工程28／歌词16／模板6、配置1及profile2、SDK和固定依赖保持；实际验证见roadmap §18.134。


## ADR-0163：导出暂存所有权与最终发布事务（0.44.0，2026-10-08）

按已授权的代码审查修复计划F1修复R01。编码完成不再暗含最终路径更新：IVideoEncodingSession.CompleteAsync返回VideoEncodingResult及VideoEncodingArtifact，调用方接管暂存文件发布／清理；已完成会话释放不再删除已转交产物。暂存位于最终目标同目录，Publish禁止原路径或跨目录，单一导出事务串行拥有产物。

导出顺序为渲染及编码→完成暂存→探测暂存并形成最终路径报告→释放渲染及编码资源→最后取消检查→Committing→发布→成功。探测告警严重性保持；物理暂存路径不进入报告。渲染、编码、验证、资源释放及最终检查的失败／取消均保护旧文件，只清理本事务暂存；禁止覆盖在实际发布时仍受检。发布失败报告真实失败。

Committing仅包含短暂文件发布，没有探测、脚本或取消工作；从此阶段开始的取消不撤回提交。发布完成通知失败不能改写成功，已发布产物释放不再执行文件IO。应用的取消判断先读取后台实际阶段，界面通知仍回到UI线程，迟到请求通知不改变已完成结果。

此为公开编码宿主协议的不兼容变化，升MINOR0.44.0；不改变作者SDK、程序集或依赖，工程28／歌词16／模板6／配置1及包profile2保持。实际回归、变异和当前全量证据见[修复验收](../acceptance/code-review-remediation-acceptance.md)及roadmap §18.136。F2～F8与完整连续人工验收不由本批推定。

## ADR-0164：工程音频身份与媒体会话所有权（0.44.1，2026-10-08）

依据修复计划F2a／R02，Media层的AudioSessionCoordinator拥有实际播放器、波形、时长信息及准备请求，独立于领域历史和GUI。AudioSourceIdentity使用规范来源路径、已接受媒体hash及byteLength，显示名不参与身份；工程会话切换和音频身份切换增加代次，普通歌词／外观编辑不重载。

导入保持“候选完整准备→一条纯数据命令→安装候选”，失败或提交前取消保持旧曲，提交之后不因迟到取消谎报未发生。撤销／重做及打开／恢复先采用领域目标，再立即摘除不匹配的旧会话、异步恢复；Empty清空播放器、波形、时长和播放标记，Unavailable保留领域目标及历史，并提供明确重试。恢复不是导入命令，不追加历史或标脏。预先准备的新工程经同一所有权入口安装，保持必填音频与主歌词门禁，不重复加载。

请求同时检查代次、目标身份和自身身份，异步完成回到调用方UI上下文；换工程即使路径相同仍拒绝旧准备。释放只由协调器执行，先摘除再停止／释放，重复关闭幂等；忽略取消的候选在返回后释放。旧播放器释放错误真实报告，不反向撤销已采用目标。安装新会话后按新时长夹取并定位播放头，加载时不读取旧位置。

此为修复及新增非破坏性服务，PATCH0.44.1，工程28／歌词16／模板6、配置1及包profile2不变，无新程序集或依赖。F2b的EOF及键盘语义继续下一批；实际证据见roadmap §18.137及[修复验收](../acceptance/code-review-remediation-acceptance.md)，定向结果不代表全量或人工试听通过。


## ADR-0165：媒体会话统一播放结束及按键状态（0.44.2，2026-10-08）

- **背景**：R05／R06的可复现问题为后端EOF后VM仍为Playing，以及K在暂停／停止时启动音频。F2a已统一来源生命周期，本批按[修复计划F2b](../plans/code-review-remediation-plan.md)延伸同一协调器，保留ADR-0034的J后退并正放及循环规则。
- **决定**：完成事件只入队，由调用方线程按播放器／会话／播放段身份消费，同时轮询后端状态和位置。无循环自然结束统一Stopped及末尾播放头，重播归零；有效循环继续入点，运行时夹取出点而不改变选区。手动Stop归零，Seek／Start使旧播放段通知失效。K采用幂等Pause，Space切换按下沿，KeyUp和窗口失焦清除记录，文本输入焦点先行退出全局播放路由。
- **适配边界**：SoundFlow内层SoundPlayer各解码段是独立身份；后台不直接改写外层状态，在所有者读取／命令时消费，仅接受当前段。重播完成段重建，暂停／继续不强制Seek；手动Stop须释放不可定位FFmpeg段，下一次从头新建，不能依赖内层Stop的Seek。官方SoundFlow1.4.1固定[源提交](https://raw.githubusercontent.com/LSXPrime/SoundFlow/16aa7783cf512cb2c89b11f0b774407104b4361a/Src/Abstracts/SoundPlayerBase.cs)用于核对event sender。Media程序集不依赖Avalonia，UI只调用会话命令及消费状态。
- **验证与范围**：新增App14项、Media桥2项；相关App71／Media21、Headless133＋音频33、专用原生147／5图及5项运行变异通过。原生路由使用可控Silent，另外真实FFmpeg／SoundFlow设备以零音量验证停止、EOF及循环；不宣称试听或物理键盘／IME通过。按用户最新要求不跑全量；详细证据见[修复验收](../acceptance/code-review-remediation-acceptance.md)。PATCH0.44.2；格式28／16／6、配置1、profile2、SDK与固定依赖保持，F3～F8及最终验收继续。

## ADR-0166：应用库的实际修订与跨进程协作提交（0.44.3，2026-10-08）

- **背景**：修复计划F3／R03已在两个从空库开始的工坊重现后保存者静默丢失先保存模板。原ADR-0137／模板传输合同只在Save前比较规范内容，未提供跨进程事务；本决定扩展该并发边界，不改变归档身份冲突规则。
- **决定**：Application层提供TemplateLibraryRepository和ScriptConfigurationRepository，读取返回完整内容、诊断及实际索引字节SHA-256；缺失、已发布空库和损坏内容有不同状态，损坏UTF-8仍保留读取字节修订。提交携带expected revision，经同路径进程内门禁及OS文件范围锁，在锁内严格验证当前修订与完整闭包；资源先发布，索引发布前再检查实际字节及取消。成功返回自身已发布字节的修订和固定快照，释放锁后通知一次；观察者异常或迟到取消不改写已提交成功，失败不通知。
- **锁与边界**：`index.json.commit.lock`保留且从不删除或替换，OS文件描述符拥有锁，进程退出释放。同步入口只尝试一次，忙时明确反馈；异步入口等待可取消。Unix FileShare是尽力协作机制，不能当成已成功持锁，采用显式FileStream.Lock并拒绝不支持的平台；实现依据[.NET10官方Unix SafeFileHandle](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs)与[文件范围锁](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/Strategies/FileStreamHelpers.Unix.cs)。本机Linux以真实子进程验证，未宣称Windows运行通过。任意外部编辑器不参加此协议；检测到的外部变化拒绝，但最后检查与替换之间仍可能有非协作写入。
- **作者行为**：保存、删除和整批模板导入参加同一协议；重复导入和固定分享在锁内检查。冲突保留草稿、非法输入及历史。“刷新库”只更新目录，已变更／删除的来源不赋予旧草稿覆盖权；明确新ID副本使用新读取修订，仍受再次竞争保护。用户重新选择当前对象或明确放弃后采用当前库版本，已删除则回到基线／空白。锁文件不能作为分享目标。没有自动合并或自动丢弃。
- **验证与范围**：仓储14项含7项真实子进程（先后／同时新增、同对象编辑、删除与归档导入双顺序、等待取消／持锁进程退出、脚本配置竞争）；新增App16项，相关App93／Core23，Headless57、专用原生64／8图及四项实际运行变异通过。恢复后当前构建零警告／错误；按用户要求未跑全量。PATCH0.44.3，格式28／16／6、配置1、profile2、SDK和固定依赖保持，无新生产程序集；独立测试worker只用于进程验收。F4～F8、目录缓存／后台保存及最终连续人工验收继续，详见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.139。

## ADR-0167：作者原始输入快照与逐字段合法提交（0.44.4，2026-10-08）

- **背景**：F4a／R04已在0.44.3确定性复现：合法透明度提交把非法字号原文改为72，撤销也不能恢复原文，保存门禁被错误解除。脚本参数面板已有默认无覆盖、精确整数／整微秒和错误草稿历史规则，本批把相同边界补到模板作者，不重写领域模型或脚本SDK。
- **决定**：作者历史保存最后合法领域样式及独立原始字段状态（文本、解析／外部错误、写入选择、复合控件输入）；材质槽和配方表单一起保存。合法字段提交保留其他未完成输入，只同步确实改变且没有待处理输入的派生展示。非法输入标脏并参加离开／保存／运行门禁，撤销恢复原文与错误；明确放弃、载入另一草稿及明确单项清除才重置。复合控件编码仅用于会话和历史，不增加持久格式。
- **材质与控件**：参数逐项提交当前最后合法覆盖；其他错误保持，隐藏槽仍阻止保存。同一固定材质的普通提交保留参数对象及控件。默认不制造覆盖，回到默认删除本键，修正回到既有相同合法值不改写旧覆盖；字符串读真实内容，整数／整微秒不经double。保存先读取复合控件，未编辑建议值不因失焦启用效果；修改描边几何不替换渐变。滑块真实Thumb与方向盘捕获丢失结束手势，接口依据固定[Avalonia12.1.3指针事件源码](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/12.1.3/src/Avalonia.Base/Input/PointerEventArgs.cs)，实际路由及中断由定向控件流程验证。
- **验证与范围**：新增App9项，相关112项、Headless129项、专用原生160项／8图及五项运行变异通过，当前构建零警告／错误；非法草稿的正式画布仍使用成功渲染的合法快照。按用户要求未跑全量；注入路由不推定物理输入、Windows或最终人工验收。PATCH0.44.4，格式28／16／6、配置1、profile2、SDK及依赖保持。F4b／F4c及F5～F8继续，详见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.140。


## ADR-0168：作者导入请求的稳定草稿身份与读取释放（0.44.5，2026-10-08）

- **背景**：F4b／R08在0.44.4上补齐四项确定性运行复现：目录／`.mkfx`分别在picker或读取期间切换新草稿，迟到结果都污染新目标候选包及撤销栈。工坊只检查是否离开；脚本窗口把普通编辑修订也当作目标失效，丢弃同一配置的正常导入。
- **决定**：共用`AuthorRequestSession`持有会话内草稿GUID、代次、独有取消源及读取完成出口。新建、载入／明确放弃使目标失效；普通输入、撤销／重做及首次保存获得持久ID不改变当前作者身份。picker前获取请求，picker返回后检查再启动读取，读取完成后再检查，成功仅对原目标提交。不能取消的picker完成后不启动过期IO；可取消读取收到取消，忽略取消的读取仍须完成并释放后才完成离开／关闭。错误仅在请求仍属于原目标时显示。
- **编辑与传输**：同一作者只持有一个导入／模板传输请求，其他作者独立。包导入期间保留普通编辑；成功先结束现有手势，候选包独立一步撤销，原始输入与最后合法值不被覆盖。配方目录刷新保护ItemsSource变化并恢复实际选中项，不能通过普通选择setter重置尚未提交的名称／JSON。脚本普通配置编辑仍接受候选包，但不自动切包／导出以覆盖刚编辑的参数；源码编辑及运行保留既有门禁。`.mktpl`共用生命周期，仍先处理离开检查、只导入／分享清洁模板，在实际闭包读取／发布时保留原有编辑冻结和原子提交。
- **验证与范围**：Headless99项、相关App119项、专用原生100＋脚本24项／2图及六项成功编译后触发的变异通过；目录与`.mkfx`使用正式固定闭包读取。当前构建零警告／错误，按用户要求未跑全量。系统portal交互、物理键盘／IME、Windows及最终连续／人工验收不由注入picker／读取推定。PATCH0.44.5；格式28／16／6、配置1、包profile2、SDK及固定依赖保持，无新程序集或迁移。F4c及F5～F8继续，详见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.141。


## ADR-0169：任务保存命令共用目标与门禁（2026-10-08，已实施0.44.6）

**背景：** R07真实复现工坊Ctrl+S保存模板、菜单写工程，Ctrl+Shift+S也保存模板；标签、快捷键、可用状态和执行目标分属不同分支。

**决定：** App内部TaskCommandContext按窗口作者持有保存／另存为／复制／导出动作、标签、快捷键、显示及可用判定和当前控件提交。菜单、按钮及键盘共用动作，先读取当前输入，再检查同一作者门禁；执行互斥及离开／关闭等待拒绝另一个请求。Main只选择首页、工程或工坊上下文，不增加全局事件总线。工程任务Ctrl+S保存工程、Ctrl+Shift+S另存为；工坊保存当前模板，隐藏工程另存为与导出，不把Ctrl+Shift+S当普通保存，复制通过明确的“复制为新预设”动作。首页保存／导出禁用且无保存快捷键提示。

独立脚本Ctrl+S共用配置保存动作和既有源码／参数门禁；源码修订使用独立明确动作，配置保存不隐式保存、编译或采用源码。未提供Ctrl+Shift+S动作时不回落普通保存或背后工程。文本Ctrl+Z由输入控件优先处理；新增／打开继续现有离开检查。离开确认中用户明确选择保存仍使用内部保存流程，不被外部命令的等待门禁阻断。

**验证与边界：** 0.44.5有／无工程取得五项实际旧断言失败，包括project.json字节被误写及工程dirty被清除。当前Headless93、相关App133、专用原生94／2图及五项已编译变异通过；原草稿／库／导入／播放定向继续通过，solution构建零警告／错误。按用户要求未跑全量，不推定物理键盘／IME、portal、Windows或最终人工验收。PATCH0.44.6；格式28／16／6、配置1、profile2、SDK、固定依赖及程序集保持。证据见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.142；F5～F8及最终连续路线继续。

## ADR-0170：背景视频操作的统一期限与取消所有权（0.45.0，2026-10-08）

**背景：** R11旧10秒等待只在同步stdout读完后执行，实际探测停滞、写满stderr和半帧读取三项复现均超过12秒外部护栏。F5a落实[进程合同](../../background-video-process-contract.md)，不把Task.Run当成已可取消的IO。

**决定：** Media内部OwnedMediaProcess拥有启动、双流、退出及释放；探测／一次完整取帧各共用10秒linked deadline，取消回调终止自身进程树，所有者等待退出与排空后显式释放reader及Process。错误文本有限保留并继续排空，元数据输出超限拒绝，部分帧／取消不发布成功。调用者取消沿正式渲染／背景捕获传到打开与读取，旧同步生产入口仍有限；默认接口桥不能打断第三方旧阻塞实现，必须如实记载。UI导入后台准备，结果及模型提交回到调用方线程。

**验证边界：** 本机.NET10／Linux可控真实子进程与实际FFmpeg均有定向证据。额外句柄断言先发现仅Dispose Process仍留下外部引用管道，显式释放后连续八次关闭通过；依据.NET官方[Close](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.close?view=net-10.0)及[Kill](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0)明确不将根退出等同所有后代退出。取消不触碰独立进程，独立后代及管道有实际观测；不宣称脱离树的进程、Windows或用户原生人工验收通过。

MINOR0.45.0对应宿主取消协议扩展，作者SDK、格式28／16／6、配置1、profile2及固定依赖保持，无新生产程序集。按用户要求只构建与定向验证，F5b／F5c、F6～F8及最终连续路线继续；证据见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.143。

## ADR-0171：背景视频复用最近完整帧（0.45.1，2026-10-08）

**背景：** F5b／R12在真实30fps视频按60fps请求前六次时重现四次解码器启动；既有像素回归不能发现该成本。F5a取消与期限保持。

**决定：** source独立保存最近成功源帧号及有效标记，与下一待读帧号分开。同一源帧直接返回借用缓冲，仍先检查取消／释放；少量前进顺读，真正回退／远跳保留原重启规则。开始覆写、部分读取、失败、重启、取消及释放使缓存失效，只有完整成功后记录新源帧号。缓存仅属于单个source，不共享跨会话可变缓冲，也不更改帧号取整、seek精度或VFR策略。

真实六次请求和顺读到末帧再冻结均只启动一次解码器；像素与独立顺序来源严格相同。可控进程补充回退／远跳、失败后回到旧时刻、缓存命中取消、重复释放及两来源隔离／原借用合同；三项编译后变异捕获实际计数、陈旧半帧及回退错误。PATCH0.45.1，无新公开入口、数据格式、SDK、依赖或程序集；按用户要求只构建和定向验证。证据见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.144，F5c、F6～F8及最终验收继续。

## ADR-0172：背景来源缓存包含播放语义及固定媒体身份（0.45.2，2026-10-08）

**背景：** F5c／B01旧resolver只按路径缓存，复用API切换Loop／Freeze仍用首次策略；0.45.1两方向源时刻及真实一秒视频冻结像素三项复现失败。正式快照重建本就限制影响范围，不扩大为所有正式预览错误。

**决定：** resolver键使用媒体种类、规范完整路径、已接受媒体Hash／ByteLength及视频播放模式；Windows路径键按既有音频身份规则忽略大小写。适配由绘制负责、原名称只供显示，均不进入来源身份；图片忽略播放模式。不每帧重新hash文件，不替代持久化闭包校验。无固定修订的外部同路径内容替换仍由新resolver生命周期处理。

来源条目保留至resolver关闭，分别释放；不新增任意淘汰阈值。探测在缓存锁外，每请求持有未发布候选；发布锁内复核取消／关闭及同键胜者，未采用或迟到候选释放。关闭清空全部条目并阻止后续发布，已识别IO／状态／取消释放错误收集后继续尝试其他来源并报告AggregateException。提供者归组合根，不由resolver释放；正式会话继续各自拥有可变来源与缓冲。

**验证：** 新增11项，相关Rendering42、正式会话及背景取消App8、Media缓存／真实视频15、Export普通／透明22项通过。包括真实一秒视频500000／999999μs对应像素、反向切换、规范路径、修订／长度、种类／适配／原名称、两会话隔离、并发候选、迟到取消／关闭及释放失败。五项编译后变异捕获真实断言，恢复后构建零警告／错误。PATCH0.45.2，格式28／16／6、配置1、profile2、作者SDK、固定依赖及公开宿主入口保持；按用户要求未跑全量，未操作或启动用户窗口，不推定Windows或人工验收。证据见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.145；F6～F8及最终验收继续。

## ADR-0173：工程保存只消费捕获快照并按实际提交完成（0.45.3，2026-10-08）

**背景：** F6a／R13旧自动保存在UI线程同步hash／复制完整媒体；手工Task.Run读取可变workspace，可能以完成瞬间的新Project标为已保存。关闭后补写还可能重建已放弃现场。真实63 MB工程、五份独立备份已记录UI输入优先级延迟与管理线程保存栈，不以旧调用链或主观流畅替代测量。

**决定：** Application协调器只消费UI捕获的不可变ProjectSaveSnapshot，工作区Generation与协调器Epoch共同保护完成。一个后台写入者、一个最新自动待办及受互斥保护的手工请求；手工优先并按实际另存为目录继续备份，saved／lastBackedUp使用真正写出的Project，新编辑仍dirty。手工实际已写实例不重复备份，撤销回旧实例仍重建更新现场；最近列表在后台使用捕获输入，UI完成与命令收尾一致。

备份数据与标记分开：自动标记失败可重试，根入口已发布后的标记清理错误为单独警告。放弃先失效并取消待办，异步等完整资源事务到安全点，再仅清理自有现场；当前同步Core事务不半途破坏性取消。关闭等已接受手工保存后确认，资源使用结束后释放；OnClosed不再补写，旧结果不能串工程。完整闭包、hash／长度、刷盘、版本门禁与独立备份保持，不共享备份媒体。

**证据与边界：** 新增13项／相关Application47、Core46、App17、Headless保存23／命令93／音频33及六项编译后变异通过，零警告构建；固定媒体同条件分位数／线程栈、独立闭包搬移及SIGKILL后新进程恢复均有实际证据。基准为输入优先级任务排队延迟，管理线程采样不等同native全栈或CPU测量；50ms仅是当前有限Headless P99检查。普通编辑库IO仍属于未完成F6b，进程恢复不等同断电或用户人工验收。PATCH0.45.3新增应用服务，不破坏既有宿主／作者SDK协议，格式28／16／6、配置1、profile2及生产依赖／程序集保持。按用户要求未跑全量，详见[后台保存合同](../../project-save-queue-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.146。

## ADR-0174：模板目录只读内存并在后台复核失效（0.45.4，2026-10-08）

**背景：** F6b／R13旧画面控件由ProjectEdited和对象选择同步展开整个个人库，隐藏时仍执行；固定63 MB工程／三个包／100材质模板的实际40次编辑及选择，测量中199次全库读取全部在UI线程，隐藏阶段98次。旧读取诊断被忽略，损坏库可能看似空库。

**决定：** 应用共享TemplateLibraryCatalogue，经窄只读来源和显式注入消费F3仓储。工程与选择仅过滤内存；激活租约和失效由单后台读取者合并并复核真实索引字节及必要闭包。资源通知强制完整校验，文件通知仅提示失效；缺失库监视最近存在的上级，不创建目录。最后租约释放后不读，旧代次不发布；窗口取消订阅与租约，应用拥有目录生命周期。

提交锁释放后的本进程通知可能乱序，发布快照先标过期，后台实际字节复核后才允许新应用。错误保留上次成功内容并显示过期／错误，禁用新应用／更新且路由同样受检，确认前后校验目录代次。已有工程固定应用和作者草稿不升级，既有启停／移除／复制及作者冲突门禁保持。

**证据与边界：** 新增11项，Application27、Core18、App40、Headless目录29及相关回归通过；另一个真实进程更新、同索引资源损坏、迟到读取／乱序通知及六项编译后变异覆盖。最终基准普通编辑／选择零全库和索引IO，首次激活一次后台读取；输入优先级排队P99由9545.9833ms降至最终1456.9587ms，首次复测767.6535ms也保留。剩余UI延迟不宣称解决，带窗口Headless基准不沿用F6a保存基准50ms检查；线程采样不等同完整native栈或CPU测量。构建零警告／错误，按用户要求未跑全量，不操作用户窗口，不推定原生／物理输入、Windows或人工验收。PATCH0.45.4，格式28／16／6、配置1、profile2、作者SDK、生产依赖及程序集保持，F7～F8与最终验收继续。见[目录合同](../../template-library-catalogue-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.147。

## ADR-0175：每个重放代次一次校验并共享只读历史前缀（0.45.5，2026-10-08）

**背景：** F7a／R09原事件及120Hz边界将完整已发生记录复制到新数组并再次检查身份／来源。固定180秒50Hz作者累计校验并复制170,127,900条，分配11,377,934,376字节；同条件隔离预热基准、完整Warning和显式Records作者成本均保留。

**决定：** SDK内部ValidatedEventTable一次校验固定不可变记录，执行宿主按代次建立正确ReplayReason。TimelineHistory只持共享表、可见数量及边界；同刻回调逐条推进，旧对象不增长。查询只访问可见索引；公开Records维持ImmutableArray签名，显式访问物化当前前缀并在该对象内缓存。公开构造严格校验保持，内部路径仅friend执行程序集可见，不添加作者可信开关。回退、失败／取消及输出变化重建代次，独立端点仍按原协议重放，桥接缓存保持，不引入检查点。

**证据与边界：** 新增9项，Scripting59、Rendering82＋独立端点计量1、App46及六项编译后变异通过。前后各36个10／30／180秒隔离样本，180秒50Hz无数组作者校验9000条、宿主及兼容前缀复制0，分配673,289,400字节；正常空效果仍有其他语义分配。显式Records作者仍复制129,623,400条，分配1,711,375,200字节；Warning完整消费者仍访问58,336,201条且分配约6100 MB，属于F7b待处理。过去／未来端点和首次桥接／缓存新边界另测，不把命中成本归给独立重放；真实双输出全部编码帧及24／30／60fps保持。2ms托管占用采样不等同绝对峰值／native内存；具体原结果见[历史前缀合同](../../scripting-history-prefix-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.148。

PATCH0.45.5，公开作者／宿主接口签名不变，格式28／16／6、配置1、profile2、生产依赖及程序集保持；内部friend路径不宣称脚本安全沙箱。最终构建零警告／错误，按用户要求未跑全量，不操作或启动用户窗口，不扩大为Windows或连续人工验收。F7b、F8及最终路线继续，整个修复目标未完成。

## ADR-0176：完整诊断以不可变分块共享并按发生身份增量消费（0.46.0，2026-10-08）

**背景：** F7b／R10在F7a后仍逐个120Hz边界复制／校验累积诊断，正式导出再遍历全部历史。固定180秒Warning前测完整21601条报告，生产累计复制／校验各699,937,203条，消费者访问58,336,201条，累计分配约6100 MB。直接清空会漏掉中间作者阶段；按消息去重会删除真实多次发生。

**决定：** Rendering.Abstractions公开单串行所有者RuntimeDiagnosticBuffer、共享32条不可变分块的Snapshot及按代次／顺序号读取新后缀的Cursor。记录加入时校验、保持Error标记，快照不持可变尾部或作者／native对象；旧完整数组入口签名保持，明确读取才物化并在快照内缓存。共享边界、分数及本次绘制后缀分别固定，不把临时报告并入未来边界；取消后的实际重建、回退、模式／目标及新会话有新代次。缓存和失败缓存保留同一稳定快照，释放原会话后仍能读取。

Scripting、Skia、预览、导出及运行验证走快照；完整实际报告始终保留，导出Warnings和验证Issues仍沿旧汇总规则，另有完整发生记录。UI每页最多50项，不截断数据。作者捕获Error仍失败，普通异常／早期门禁及释放失败保留此前日志；成功帧检查Error标记。内部bridge只取固定Cursor，公开独立端点完整数组仍可取；内部出生几何查询的既有临时诊断范围不扩大。没有Warning预算、磁盘分段、重放检查点或作者SDK方法变化。

**证据与边界：** 新增32项，当前Scripting63、Rendering144、App69、Export58、Headless分页11及10项编译后变异通过。前／后四模式10／30／180秒各3进程36样本，另9个明确完整数组样本，共81；每个有复制／校验／交付计数断言。180秒正式增量路径尾部复制356401条、校验／读取各21601条、完整数组复制0，累计分配504,299,176字节；所有报告数量保持。明确完整数组仍复制并读取58,336,201条、约1016 MB分配；明确History.Records仍有二次物化，空作者仍有其他语义分配，不宣称所有路径或UI延迟解决。采样托管占用不是绝对峰值或native内存，正常基准不执行早期失败参数的后续补充。详见[诊断快照合同](../../scripting-diagnostic-snapshot-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.149。

MINOR0.46.0对应公开宿主快照／读取／导出报告入口，旧作者及完整数组签名、格式28／16／6、配置1、profile2、固定依赖及生产程序集保持。最终构建零警告／错误，按用户要求未跑全量，不操作或新开用户0.43.10窗口；Headless及短片不推定物理输入、Windows或用户连续人工验收。F8和最终路线继续，整个修复目标未完成。

## ADR-0177：固定快照的正式运行装配由独立宿主持有（0.47.0，2026-10-08）

**背景：** F8a／A01的ProjectRenderSessionFactory和ProjectRenderSession位于GUI可执行程序集，无控件调用者不能只引用正式后端完成真实固定包编译、资源装配和像素运行。源码定位记录也由App作者编译入口声明，直接搬工厂会留下反向依赖。

**决定：** 新增Runtime.Hosting，只组合Core、Media、Rendering中立接口／Skia和Scripting。正式工厂公开并实现既有IRenderFrameSessionFactory，会话实现仍internal；App组合根显式提供参考目录、产物目录、视频提供者和ReplayReason。编译异常与不可变来源诊断一同迁入，共享源码位置记录保持原字段和固定DisplayLabel，不持有作者或控件。App只消费并展示；Export仍通过中立接口，不引用宿主。每个预览／导出／验证拥有独立控制器、native资源、预算和绘制管线；只复用惰性编译器及磁盘产物，不把可变运行资源做静态单例。

迁移保持执行代码和资源释放顺序。两个取消入口和快照引用检查、关闭等待及失败后继续清理保持；首个清理异常及全部异常不丢失。后端不反向引用GUI或宿主。验证执行器、试样作者服务和布局变换继续F8b，旧窗口／共享作者模型／编译绑定继续F8c，不将整份依赖VM和本地化的ScriptRuntimeValidator直接搬入宿主。

**证据与边界：** 新增无Avalonia宿主9项，当前App63项和四项成功编译后变异通过；实际固定包／shader／图、双输出帧和完整来源、取消／关闭及真实图片／native／预算回收受检。迁移前原执行体与新宿主在同0.47.0后端／SDK下12次帧／完整诊断相等，规范化源码SHA一致；当前solution零警告／错误。原App普通／透明各360帧和六短片全部68编码输入帧／完整解码保持。完整项目图与编译后依赖守卫阻止GUI和反向引用；指定测试friend不构成产品引用。见[正式宿主合同](../../runtime-hosting-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.150。

MINOR0.47.0对应新增公开宿主程序集，作者SDK和中立接口签名、格式28／16／6、配置1、profile2及固定NuGet版本保持。按用户要求暂不跑全量，不操作或新开用户窗口，不扩大为Windows、物理输入或用户连续人工验收。F8b／F8c和最终路线仍待完成，整个目标未完成。

## ADR-0178：固定验证、作者媒体与布局变换按无控件职责分离（0.48.0，2026-10-08）

**背景：** F8b／A01的原ScriptRuntimeValidator混合VM请求、试样媒体、双输出执行和中文报告；BasicLayoutOperation混合工程变换、Loc错误及新节点命名。整文件迁移会把GUI数据带入正式宿主，改变名称或拆分身份又会破坏现有工程语义。

**决定：** Runtime.Hosting公开RuntimeFrameValidator，以固定ProjectSnapshot及明确帧计划执行双输出并返回结构化进度／来源诊断；媒体准备不属于执行器。Authoring模块拥有固定试样、无标签目标／材质槽、正式预设投影和ScriptSampleValidator；正式作者验证继续普通／透明各360帧。TemplateSampleLease只拥有唯一子目录，并发关闭共享等待任务，生成及会话／播放器／预览释放后才清理；不删除根目录或邻居试样。App捕获原请求，转换原VM结果和中文报告，不复制执行状态。

Application接收BasicLayoutOperation、四种几何及片段引用映射，显式输入BasicLayoutNames并返回BasicLayoutError。App沿原Loc资源提供名称／失败文案及影响确认，EditorViewModel继续检查源引用并一次撤销。原片段ID留给最早未选部分，未选槽位／时钟、直接绑定、相对成员实例身份／seed保持；不能安全拆分的范围配方整次拒绝。生产Application只引用Core，迁移像素测试新增的Skia引用仅在测试项目。模板和独立脚本仍共用现有固定样本，不改变作者保存／编译／运行三类动作。

**证据与边界：** 新增16项正式回归，宿主16／Application布局21／App56项通过；原布局17项迁移并保留像素／闭包／身份断言，实际App入口和原各360帧／全部68短片编码帧及完整解码继续通过。Headless通用332、真实布局控件／确认29及诊断分页11通过。四项变异均成功编译后因少帧、同文发生丢失、调度上下文缺失或提前媒体清理触发实际失败；原字节恢复并重新构建／复查。正式后端项目图与传递程序集仍不引用GUI。见[验证与布局合同](../../runtime-validation-and-layout-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.151。

MINOR0.48.0新增公开验证／作者／Application用例，作者SDK与中立渲染接口、格式28／16／6、配置1、profile2及固定依赖保持。当前solution构建零警告／错误；按用户要求暂不跑全量，不操作或新开用户原生窗口，不推定Windows、物理输入或连续人工验收。F6b剩余UI延迟不由职责迁移推定解决，F8c共享作者模块／旧入口／编译绑定及最终路线继续，整个目标未完成。

## ADR-0179：共享作者能力与历史入口分离并分批开启类型绑定（0.49.0，2026-10-09）

**背景：** F8c／A04的旧实例窗口和工程基线窗口已无产品导航消费者，但仍编译进App且被历史后端回归及通用布局工具使用。当前工坊／前奏样式复用StyleDesignField和编解码器；独立脚本配置已有自己的参数／源码／运行模型，不能按旧名字一并删除。原共享字段还按具体工坊及旧AppearanceEditContext判断实时写入和属性范围。

**决定：** 按引用清单删除无生产入口窗口、专属VM、请求事件和直接静态编辑命令；正式ProjectEffects事务及中立后端保持。共享字段、原始输入、渐变／描边／阴影编解码器和真实绑定说明迁入App.Authoring；IStyleDraftContext明确LivePreview和SupportsProperty，工坊仍实时预览且拒绝Composite画布位置／对齐，场景草稿仍显式提交。历史实例／基线／直接样式输入仅作为测试程序集LegacyAuthoring夹具，保留当前正式宿主像素、闭包、身份和撤销回归，不记为当前作者UI入口。合成区进入画面任务的按钮名称／文案与实际行为统一。

StylePropertyRowView和三个复合样式编辑器明确StyleDesignField并局部开启编译绑定；后三者主要C#接线，不虚报绑定数量。其他稳定视图、动态例外和全局默认继续F8c2；完成前保持csproj原默认。真实DataContext切换、错误、保存启用、范围门禁、实时／显式提交及空上下文通过有限Headless验证，不以编译成功代替交互。

**证据与边界：** App133、真实后端19、Headless作者15／草稿129／通用313／任务93／分页11通过，solution及原生工具编译零警告／错误。旧窗口布局19个断言随入口删除退出，当前布局313不冒充旧332。三项成功编译后变异捕获旧类型恢复、范围放开及实时预览失效；另一个错误字段名由编译绑定门禁捕获，不计为编译后运行变异。全部源码字节恢复。真实后端首轮四个旧诊断断言失败，以e3e448c原模型执行体在同0.49.0后端复现后更新断言：保留完整此前阶段并翻页定位Error，未修改后端输出。原执行体不是旧SDK测试。详见[作者模块与绑定合同](../legacy-ui/authoring-module-and-bindings-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.152。

MINOR0.49.0对应退出旧公开UI类型／命令及共享作者类型位置变化；作者SDK、中立接口、格式28／16／6、配置1、profile2和固定依赖保持。按用户要求未跑全量；原生工具只编译，不操作或新开用户0.43.10窗口。F8c整体、最终连续／人工路线及F6b剩余UI延迟仍未完成，不推定Windows或物理输入验收。

## ADR-0180：稳定模型绑定与命令范围明确后启用生产编译默认（0.49.1，2026-10-09）

**背景：** F8c2的剩余稳定视图仍依赖反射默认。模板项目、DisplayMemberBinding泛型选项、命令子DataContext及父控件读取各有自己的实际类型，单次编译不能证明初始化、选择切换与保存门禁正确。

**决定：** 所有当前稳定模型路径明确x:DataType，17个项目模板及选项展示声明实际类型。内部IScriptConfigurationChoice只共享只读Label，泛型Value保持。C#动态来源、搜索、试样播放及场景状态用12.1.3的CompiledBinding.Create，原source／模式保持；当前无需生产反射例外。命令控件初始DataContext明确空，实际动作仍由已有协调器设置；空行标题使用已有总值属性，配方范围只读转换显式OneWay。材质槽刷新只替换内容变化的选项集合，避免双向选择期间重置；模板字符串只返回名称，避免主题测量遍历包材质的类型限定访问器，不修改记录相等性或持久值。

实际交互及零Binding警告通过后，才移除App.csproj全局false与旧技术原型理由。固定12.1.3包默认和当前MSBuild解析值均为true。策略回归防止重新全局关闭／未登记反射回退／模板类型遗漏，无模型绑定控件和资源不虚报迁移数量。

**证据与边界：** App135／Core模板57、Headless绑定53／通用313／语义22／作者15／草稿129／分页11／任务93／布局29，共665个断言通过；六项成功编译后的变异触发实际断言，另一个错误属性名在编译门禁失败。全部源码字节恢复，构建与恢复复查证据见[作者模块与绑定合同](../legacy-ui/authoring-module-and-bindings-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.153。报告消费的替身不冒充完整帧执行，Headless不冒充原生／人工检查。

PATCH0.49.1保持工程／歌词／模板28／16／6、配置1、profile2、作者SDK／中立接口、生产程序集和固定依赖。按用户要求不跑全量，不启动／操作用户0.43.10窗口。F8c定向范围收口，整个目标仍待最终连续／人工验收；F6b剩余UI延迟不记为解决。

## ADR-0181：整形自动回退按有界文本元素结果复用（0.49.2，2026-10-09）

**背景：** F6b已解决普通编辑的目录IO，F8c2后的当前线程栈仍确认歌词选择／文本命令的整形诊断重复系统匹配和打开已有回退族。字体对象缓存未缓存完整元素的选择结果，不能以已有FontSet误记为没有重复查找。

**决定：** 保留主字体和显式回退链顺序、完整grapheme覆盖判据、请求字重／粗斜体以及缺字回到主字体的规则。自动路径缓存元素到FontEntry的成功与缺字结果；每FontSet最多1024项，FIFO淘汰，超过64个UTF-16代码单元的元素不缓存。新元素仍真实匹配，但已持有族不再次打开。既有FontKey隔离字体族、字号、字重、粗斜体和显式回退链；缓存不跨引擎／线程／会话共享，释放时清空引用并按原拥有关系释放字体。系统字体变更需新建引擎，未增加字体热更新功能。

内部构造注入仅供测试观察原生匹配／打开，公共构造及作者SDK保持。测试委托仍调用真实Skia，比较完整字形／诊断和绘制像素，不用替身结果代替字形正确性。基准补齐索引读取也必须为零的断言；不改输入优先级、操作序列、渲染或诊断。

**证据与边界：** 76项Rendering／5项App定向、实际Headless路线及三项成功编译后变异见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.154。精确取`dea76c9`原整形执行体作同宿主对照，源码字节恢复；采样为整条序列累计线程墙钟时间，不能称CPU测量或单次输入延迟。自动回退栈累计约796～1249ms降至29～31ms，但整体输入P99仍波动，未宣称全部UI卡顿解决。初次选错trace profile及未编译变异保留为无效尝试，不计产品失败或成功变异。

PATCH0.49.2保持作者SDK／中立接口、公共整形API、生产程序集、格式28／16／6、配置1、profile2及固定依赖。全量继续按用户要求暂停；不操作或另开用户0.43.10窗口，最终连续／人工路线及剩余排队边界继续，整个修复目标未完成。


## ADR-0182：画面区展示复用并按稳定ID保留分段端点（0.49.3，2026-10-09）

**背景：** R13的当前控件路径仍反复替换对象／预设／分段展示模板及相同项目；歌词Reload清空活行集合时，两个分段下拉框丢失原选区。减少目录IO和整形回退不等同于这些展示问题已经解决。

**决定：** 项目模板在控件构造时建立一次。对象绑定、名称、详情、启用及缺失内容相同才复用实际项目与多选集合；变化照常更新目标、继承、输入及动作。预设候选按实际目录对象引用比较，同内容重读也替换引用，保持现有目录有效性和确认前后门禁。分段下拉框保存上一份行展示快照，在歌词编辑／撤销／重做后按ID映射当前行及文字；端点删除回退到当前行，换工程明确重置。领域快照、固定应用、ID／seed及保存合同保持。

**证据与边界：** App20／Application11、Headless656和三项成功编译后变异通过，源码按原字节恢复。当前有限原生54项／6图通过并关闭专用窗口；实际多选、具名映射、固定身份、后代范围及撤销／重做保持。原执行体同样在1.8倍缩放下触发旧尺寸断言，先复现再将探针改为共享MinHeight和半物理像素取整，未修改产品尺寸。截图在稳定视口重新打开弹层，不能用它证明保持打开的旧原生弹层搬动位置正确；该平台／缩放边界及物理输入继续。

同夹具六个未插桩样本全部保留，当前整体P99未见改善；另记含启动的整条序列累计线程采样，歌词元素重建和主题模板布局继续跟进。构建零警告／错误，证据见[模板目录与展示合同](../../template-library-catalogue-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.157。PATCH0.49.3保持公开签名、SDK、中立接口、程序集、格式28／16／6、配置1、profile2及固定依赖；全量继续暂停，整个目标未完成。


## ADR-0183：歌词卡片保持树连接并以当前身份维护焦点与拖动（0.49.4，2026-10-09）

**背景：** R13当前线程仍有歌词元素重建成本。仅复用Border实例但清空／重新加入仍重新装配样式；直接沿用创建时捕获的行号会把输入发给旧目标。原焦点边框临时增加尺寸，真实Headless排列和原生滚动视口均已暴露问题。

**决定：** 按当前展示位置保留实际卡片与树连接，只创建新增项／移除多余项；空行／空分支清空，不建立整首歌曲或隐藏控件池。事件显式核对当前行、位置、元素ID和文字后消费当前目标。捕获固定发起工程引用和分支，换工程（即使内容ID相同）、换分支、换行、清空、离开及关闭取消捕获，原重排时间确认和迟到身份门禁保持。卡片仍持有焦点时，按模型最终当前元素ID同步；不在重载临时状态恢复旧对象，空行清理请求，源输入保持自己的焦点。

焦点边框集中到UiControlMetrics，始终预留相同空间，聚焦只改变颜色。控件尺寸和位置不因焦点状态变化，原CompactHeight／Padding及主题资源保持。此GUI尺寸项不扩展作者SDK。

**证据与边界：** App50／Headless650、九项成功编译后变异、有限原生及当前构建证据见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.158。原生150高度旧断言先在精确原执行体复现，再采用已有紧凑90检查并验证真实卡片在所有滚动视口内；该检查随后要求产品修复焦点引起的尺寸变化，未改容差掩盖。当前内核更新后重新建立前后三次基准，草稿与最终测量分开；累计线程墙钟采样不代表CPU／单次延迟，整体交互预算、旧原生弹层搬动及人工边界继续。

PATCH0.49.4保持作者SDK／中立接口、生产程序集、格式28／16／6、配置1、profile2与固定依赖。全量继续按用户要求暂停，整个修复目标未完成。

**后续用户指示（2026-10-09）：暂时跳过性能相关。** 不再自动优化或运行性能基准／采样；已有测量保留，整体预算、Reload／主题／字体成本暂缓，不阻挡本批功能交付。最后相同ID复制工程的捕获门禁按功能缺陷继续修复和验证，性能不复测。

## ADR-0184：具名来源菜单随位置变化收起并保留选择（0.49.5，2026-10-09）

**用户裁决：** 旧原生来源菜单在窗口搬成浮动、缩放及祖先滚动后仍停在原位置。公开位置属性刷新／绘制试验未得到正确截图，当前Suki窗口也没有可直接使用的窗口内弹层宿主。用户明确选择“接受自动收起并保留选择”。此前保持打开的该条验收由本决定接替，不记为旧菜单重新定位已修复。

**决定：** 画面区具名歌词来源使用内部PresetInputComboBox，StyleKey沿用Suki ComboBox。仅在菜单实际打开期间记录输入相对窗口的矩形、客户区大小、缩放及可报告的位置；改变时收起。输入拆除亦关闭，关闭／拆除／模板重建解除监听，重开跟踪当前宿主。稳定布局保持打开，后续布局不自动重开；选中来源、待应用映射、工程和撤销历史保持。仅用公开事件／生命周期，不升级依赖或改Suki模板。

**证据与边界：** App领域9项、Headless483个断言、原生186个断言／6图、两项成功编译后变异及零警告构建见[菜单位置合同](../legacy-ui/preset-input-popup-motion-contract.md)／roadmap §18.160。源码字节恢复，所有自建窗口关闭。原生验证了尺寸、祖先滚动、拆除／再挂接与新菜单，Headless不冒充原生打开；Wayland纯搬动的全局通知、物理输入／IME、听辨、多屏和Windows继续保留平台／人工边界。

PATCH0.49.5保持作者SDK／中立接口、程序集、格式28／16／6、配置1、profile2与固定依赖。全量、性能及用户暂缓的未知失败提示均不自动恢复，整个修复目标未完成。

## ADR-0185：普通入口新建先验证可读字体并保留失败候选（0.49.6，2026-10-09）

**触发：** 用户要求继续暂停原goal，另行报告普通0.49.5确认新建后首页无反馈、模板入口退出。真实日志是SKShaper的空`asset`；旧普通执行体故障注入证明部分工程发布与异常退出。用户原字体流为空的环境原因未确认，不归因于素材、字体文件或NTFS。

**决定：** Skia引擎以当前字体管理器匹配请求族与样式，读取字体流门禁，主／显式／自动回退均拒绝不可读条目。请求族不可读时尝试系统默认及既有CJK族，全不可读报告中文错误；不重定义工程默认字体或修改合法字体布局语义。新建发布前验证Reload实际将选中的行、字体与字号，失败不取走准备结果播放器、不替换原工作区／历史／音频；候选由准备结果释放。工程操作捕获明确的IO、访问、参数、不支持及状态异常，日志与首页／状态栏反馈一致，finally恢复门禁，两个入口允许重试。

**证据与边界：** Rendering64／App5、普通程序32断言／6图、真实无字体进程、三项成功编译后变异及恢复定向／原生复查见[新建合同](../legacy-ui/new-project-startup-contract.md)／roadmap §18.161。此次覆盖所报告的字体失败边界，不宣称任意后续UI异常可原子回滚，不把受控无字体环境认定为用户原因。合法字形／诊断／像素与实际字体变体保持，PATCH0.49.6保持公开签名、SDK／中立接口、程序集、格式28／16／6、配置1、profile2与固定依赖。

原NTFS有独立内核写入阻塞，健康home同分支副本源于准确`1f67606`，该副本提交推送；原目录及旧启动脚本未同步。完整普通0.49.6包在健康文件系统校验并启动，无探针或自动交互；不覆盖旧媒体／配置，不执行root或自行修复磁盘。原goal继续暂停，全量、性能及暂缓的未知提示均不自动恢复，用户原环境及整体人工出口未完成。


## ADR-0186：自然引导 UI 重构复用共用规格与分作用域反馈（0.49.7，2026-10-10）

**依据：** 用户要求按[UI重构规划](../legacy-ui/ui-0496-refactoring-plan.md)开始工作；D-01／已确认布局决定引导方式，原代码与原ADR决定功能和数据语义。先建立入口清单、共用规格与反馈，再按页迁移，不把原型未画出的能力视为删除授权。

**决定：** UiControlMetrics集中新增4／8／12／16／24间距，AXAML与动态控件共用；原Suki控件模板、按钮密度及固定版本保持。共用反馈以代次防止旧成功计时器／晚到成功清掉新消息；成功约4.5秒收起，错误与进行中状态不设置成功超时。错误按任务和稳定目标保留，切任务不串页，返回仍显示；晚到失败保留在发起任务并与已有错误共同显示，换工程清空旧请求。直接写Text的旧调用保守视为持续状态，不擅自推断成功。拆除释放计时器；派生TextBlock声明样式键，继续消费Suki语义色。工程保存只显示一次底栏反馈，原快照写入／脏判断／警告与命令门禁保持；画面操作沿用原用例分类结果，取消不制造反馈。

**范围与证据：** 首个基础切片仅接工程保存与画面操作，画面／工坊间距消费者及入口清单；尚未实现整页新布局、真实临时快照试看或作者返回。36项定向、Headless52＋118、原生57／12图及零警告构建见[实施记录](../legacy-ui/ui-0496-refactoring-progress.md)／roadmap §18.164。PATCH0.49.7只增加GUI规格／反馈，格式28／16／6、配置1、profile2、SDK、程序集划分与固定依赖保持；原goal、全量、性能及平台／人工边界不自动推进。

## ADR-0187：工程全局操作与活动作者保存分别使用原命令上下文（0.49.8，2026-10-10）

**背景：** 已批准自然引导方案§5.1与问题A01要求工程任务相邻、工坊独立分组，工程身份/保存/导出跨任务保留；0.49.7外壳仍在工坊隐藏这些工程入口。作者菜单保存放在“工程”子菜单内，也无法清楚表达实际目标。

**决定：** 三任务统一为“歌词与打轴／画面与编排／预设工坊”。全局保存工程、视频导出与歌词导出复用_projectCommands；菜单保存、Ctrl+S与作者按钮复用CurrentTaskCommands及原作者上下文。没有工程的工坊显示个人预设库，隐藏工程动作。全局工程名与保存状态分开布局，长名称省略不遮蔽状态；工坊内单独呈现作者名称/脏状态和“保存预设”，不新增第二份草稿、脏标记或历史。菜单保存移到文件菜单，另存工程仍仅属于活动工程任务。原输入准备、快照写盘、串行操作、关闭确认、导出探测入口与编码门禁保持。

**范围与证据：** 本批仅完成第2步中的外壳切片，实际命令去向见[迁移表§5](../legacy-ui/ui-0496-feature-migration.md#shell-commands)，测试/原生截图与边界见[实施记录](../legacy-ui/ui-0496-refactoring-progress.md)。歌词组提交、正式试看和作者返回未由此完成。PATCH0.49.8保持格式28／16／6、配置1、profile2、SDK、程序集划分及固定依赖；原goal、全量和性能继续暂停。

## ADR-0188：歌词时间以稳定目标整组提交，独立于正文与预览布局（0.49.9，2026-10-10）

**背景：** 已批准自然引导方案§5.2/I-09/V-10要求时间两端成组、Esc恢复、非法输入保留，并使所选时间区始终可达。旧字段离组已提交却仍有写入按钮；刷新按单个字段焦点覆写另一端及非法草稿。长错误/预览也可能裁切同一中央编辑区内的时间字段。

**决定：** 两个时间组分别使用Enter/离开整组提交，Esc恢复当前已提交值，取消重复写入按钮。草稿仅属于App UI，按编辑器会话、工作区Generation、来源/行/文字稳定ID保存，不按行号回写；同步捕获字符串，延迟回调核对实例/代次/身份。原时间改变或目标移除时拒绝，原目标不存在时可明确撤回全部未提交时间。工程命令统一准备歌词时间草稿，非法/过期及输入法预编辑不能绕过；合法组用原时长、包含性、相邻时间及呈现校验，通过原ProjectWorkspace历史发布，一组一次撤销，未改端点保留整数微秒。

正文与时间区仍由同一工作区实例管理；主窗口将时间控件放到播放栏上方独立布局行。时间组使用原Suki分组按钮，长错误有界滚动；紧凑布局集中定义间距，窄窗预览随可用高度缩小。文本/注音仍即时可撤销；普通行选择不寻址，明确定位和原音频试听分别可达。不增加第二份工程、作者或选择状态，不改数据格式、继承、媒体、渲染与导出算法。

**范围与证据：** 入口对应[迁移表§6](../legacy-ui/ui-0496-feature-migration.md#lyrics-commands)，实际定向/Headless/原生与变异终态见[实施记录§6](../legacy-ui/ui-0496-refactoring-progress.md)。PATCH0.49.9保持格式28／16／6、配置1、profile2、SDK、程序集划分和固定依赖。工程设置及完整UI重构继续，原goal、全量、性能和平台/人工边界不自动恢复。


## ADR-0189：工程设置独立工作页与明确来源的原子歌词追加（0.49.10，2026-10-10）

**依据：** 用户要求按已批准规划继续下一切片；规划§5.2/§5.7、S1/S8与自然引导规则要求音频归属工程、字段就地提交、统一添加和空来源可执行动作。历史来源/文件菜单、高级能力、固定身份和撤销语义保持。

**决定：** 设置由顶栏打开，持久ProjectSettingsView持有字段草稿而非第二份工程；画布/背景/音频/输出分组，返回原任务并保留原作者实例/草稿/历史。宽高整组Enter/离组提交，颜色和时长为独立组；Esc恢复当前已提交值，预编辑不提交，非法草稿跨任务保留。旧会话/Generation/原值改变/回调实例和代次不符时拒绝；工程操作准备先检查，失败返回设置，作者自己的保存仍走原上下文。自定义帧率、整数微秒和取值一次语义保持。

媒体准备及缩略图在后台；图片解码成功或原视频提供者打开成功才采用，失败保留原媒体与历史并就地反馈。只读缩略图含视频首帧，不参与工程或正式渲染；目标/路径身份、取消和视图释放保护旧结果，清除/撤销按实际媒体重建，关闭释放Bitmap/源。摘要从同一模型当前值刷新，避免旧派生属性通知缺口显示“未设置”。音频替换/重试沿原串行请求和音频会话，恢复不制造工程历史。

歌词工具栏统一导入/粘贴/新行，空来源展开同一弹层。粘贴明确已有来源末尾或新建来源；保留行/元素分配、Unicode拆分与未定时状态。新建来源采用已有翻译来源/跟随主来源默认语义，来源和整批歌词合为一个ProjectWorkspace命令；会话/Generation或来源失效时保留输入并拒绝，不随当前选中来源改写旧目标。来源管理/关闭与原行操作全部保留。

紧凑中性预览旁置240宽，来源列表200宽；添加与行操作共用现有歌词工具栏，使长错误/预览并存时列表与正文仍可操作，时间区保持独立。尺寸均消费集中指标；没有新正式渲染或候选预设模型。

**证据与边界：** 定向回归、Headless、原生深浅三视口、媒体/来源失败和两项成功编译后变异见[实施记录§7](../legacy-ui/ui-0496-refactoring-progress.md#7-工程设置与添加歌词入口04910)及roadmap§18.167。PATCH0.49.10保持格式28/16/6、配置1、profile2、SDK、程序集划分和固定依赖；精确应用版本门禁保持。设置/导出草稿往返、对象/预设闭环和完整重构仍继续；原goal、含渲染全量、性能、长片及平台/物理输入/IME/听辨和人工出口不自动恢复。

## ADR-0190：对象选择与候选试看复用正式会话并按稳定目标失效（0.49.11，2026-10-10）

**背景：** 规划第 3 步要求画面对象和歌词行可以多选，预设来源必须明确，用户需要看到候选效果后再决定是否应用。静态颜色摘要或第二套试渲染无法证明正式画布会消费同一份闭包；目标、映射和工程变化也不能让旧异步结果回写新对象。

**决定：** 对象目录和歌词目录都使用稳定 `ProjectEffectBinding` 集合；多选、全选、清空和单选模式只改变界面选择，不新增领域状态。带 `BranchInput` 的预设逐项选择真实歌词分支，缺失或失效映射就地阻止应用与试看。试看先由编辑器生成不写入历史的临时 `Project`，再转为 `ProjectSnapshot`，交给正式 `IRenderPipeline` 和 `IRenderFrameSessionFactory` 由 `KaraokeCanvas` 显示；播放头沿当前快照更新，候选状态和失败信息只在快照仍是当前值时发布。

改变对象、来源映射、工程、预设目录、任务或视图生命周期必须清除快照并停止预览协调器，迟到像素/失败回调不能覆盖新目标。应用继续使用既有确认、批量原子事务和一次撤销，取消不制造工程修改；撤销/重做后按稳定目标恢复可见选择。歌词与画面目录只通过导航层同步多选模式，不复制工程、作者或历史。

**范围与证据：** 定向 xUnit 35 项、Headless 339 项、原生 132 项／12 图和零警告构建见[UI 重构实施记录](../legacy-ui/ui-0496-refactoring-progress.md#8-第-3-步对象选择来源映射与候选预览04911)及 roadmap §18.168。复制到工坊、作者返回、设置/导出往返、正式导出、全量、性能及物理/平台人工边界仍未完成。PATCH0.49.11保持格式28/16/6、配置1、profile2、SDK、程序集划分及固定依赖。


## ADR-0191：工坊保存返回按工程会话和作者草稿身份恢复临时候选（0.49.12，2026-10-10）

**依据：** 已批准自然引导规划第3步及§5.3/§5.5要求“当前应用→制作副本→保存个人预设→原目标试看→明确应用”，保存期间换工程/删除目标不得误回写。

**决定：** 返回上下文仅在App导航中保存Workspace/Generation、稳定目标、来源快照/映射、原任务和播放位置；不建立第二份工程或作者历史。复制前刷新库修订，新副本仍由原作者保存门禁与仓储闭包发布。作者返回绑定原请求会话的DraftId，换作者使旧动作失效；请求失效和同草稿正常保存不误判为换作者。保存并试用与普通保存共用串行命令和字段门禁，已保存草稿不重复写库。

返回在异步确认及目录复核后重查工程会话和目标，不用行号或当前选择替代原目标；工程已换/目标删除时保留资源并要求重新选择。有效返回恢复精确当前已保存候选与原具名映射，在既有正式临时会话试看；不自动应用、不生成工程历史。目录、字节身份或兼容性失败不退回默认候选，新增来源仍须用户选择。应用/取消及一次撤销沿原命令；返回原对象沿原作者离开确认。工坊底栏消息与换行动作常驻。

**证据与边界：** [实施记录§9](../legacy-ui/ui-0496-refactoring-progress.md#workshop-return-04912)及roadmap§18.169记录定向、Headless、原生与变异终态。PATCH0.49.12不改格式28/16/6、配置1、profile2、SDK、程序集或固定依赖。工坊/效果作者整理、设置/导出往返及整体出口继续；原goal与全量/性能/长片/人工平台边界不自动推进。


## ADR-0192：工坊基础外观首屏与资源库状态显式化（0.49.13，2026-10-10）

**背景：** 工坊已有完整材质、脚本、效果和资源库能力，但右侧滚动区先出现高级材质，基础外观字段需要继续滚动才能找到；个人库为空时也没有直接说明下一步，库版本/修订信息只在异常时出现。

**决定：** 右侧属性区按基础外观、材质与包资源、脚本配置、效果配方排序。基础字段首屏可达，高级区仍保留原控件、绑定、输入草稿、撤销和保存门禁。个人库空态只增加解释和现有新建/导入入口，不创建占位模板；库底部显示当前 schema、个人模板数量和索引字节修订短标识。版本不匹配、损坏库、冲突和旧库保留继续使用原门禁。

**证据与边界：** [实施记录§10](../legacy-ui/ui-0496-refactoring-progress.md#workshop-first-screen-04913)及roadmap§18.170记录 xUnit 67、Headless 378、原生 80/8图和零警告构建。PATCH0.49.13保持格式28/16/6、配置1、profile2、SDK、程序集及固定依赖；效果作者、源码状态、设置/导出往返、全量、性能和平台人工边界仍未完成。


## ADR-0193：效果组件工作区分离配置、验证与源码修订（0.49.14，2026-10-10）

**背景：** 规划 S7 要求效果组件同时保留包导入、参数配置、正式试样和源码作者能力；如果把编译、采用和运行验证合并为一个按钮，失败源码、未采用修订和旧验证报告会被误认为当前配置已经通过。将验证视图放入 TabControl 后，Avalonia 的 TabItem 内容也不会可靠继承工作区 Runtime 上下文。

**决定：** `ScriptConfigurationWorkspaceWindow` 固定提供配置、验证、源码三页。无包显示空态并保留导入入口；源码按固定修订缓存编译报告，源码编辑使当前报告过期，切回固定修订恢复对应缓存。保存源码、编译、采用修订和正式运行验证保持四个独立动作。采用只允许同一 `PackageId` 的新固定修订，通过 `ScriptConfiguration.Create` 重新检查导出与原配置语义，成功后将当前草稿切到该修订并要求重新验证；不会自动升级已保存配置、工程或预设副本。验证视图由窗口组合根显式绑定同一个 `ScriptConfigurationRuntimeViewModel`，不依赖 TabItem 内容的数据上下文继承。

**证据与边界：** [实施记录§11](../legacy-ui/ui-0496-refactoring-progress.md#effect-component-workflow-04914)及 roadmap §18.171 记录 solution 零警告/错误、xUnit 97、Headless 380、原生脚本/参数/库/作者流程及两组 720 帧试样验证。PATCH0.49.14保持格式28/16/6、配置1、profile2、SDK、程序集、固定依赖和既有运行能力；设置/导出往返、工坊整页、全量、性能、长片、正式导出及物理/平台人工边界继续未完成。


## ADR-0194：共享时间组用例与保守增量刷新（0.50.0，2026-10-11）

**背景：** 用户已授权按 [拆分方案](../plans/backend-extraction-plan.md)开始实施，保留可用 Avalonia 并逐步复用共享后端用例。现有时间组的稳定目标、校验和历史仍在 EditorViewModel，任何歌词变化都会清空行模型；工程通知又先于行与时间块同步发布，依靠重新选择行补一次刷新。`RefreshPlacementState` 已刷新场景，后续显式刷新形成重复。

**决定：** 第一个切片在现有 Application 的 `Editing` 中公开 `LyricTimingEditUseCase` 及请求、结果、稳定目标和时间变化摘要，继续只依赖 Core。进程内调用由工作区的单一所有者同步调度；输入绑定工作区引用、Generation、明确分支／行／元素 ID 与原时间，拒绝失效输入。新端点使用 `long` 微秒，省略端点保留原精确值；未定时目标须完整输入。沿原 Core 时间／呈现校验执行一次不可合并历史，原数据不修正，无变化保持工程引用、dirty 与重做栈。App 保留客户端会话、输入法、非法原文和组提交，移出的规则只调用共享用例，不保留第二份实现。

时间摘要区分明确的行／元素时间或来源状态变化与不能证明的完整刷新；所有实际变化保守失效正式预览，因为脚本和跟随来源可能读取它们。撤销／重做用同一分类规则。App 仅刷新受影响行与元素的时间属性，保持行／元素模型、选区和展开状态；时间块使用不可变列表替换受影响行，旧列表保持固定。结构、正文、注音、外观、来源配置及未知变化保留完整刷新。新增源字段必须同时维护领域相等合同与分类回归。

同工程 `ProjectEdited` 在行、时间块、选择与历史同步完成后发布一次，换工程和切换来源的原生命周期继续。去掉 `RefreshPlacementState` 后的重复场景刷新；场景仍保留同一草稿和原 Source，工程时间变化使草稿过期，撤销恢复精确 Source 后允许原提交。过期提示直接由当前 Source 身份派生，不覆写真实操作反馈；恢复 Source 时自动撤回过期提示，取消与真实错误保持。

**版本与边界：** MINOR `0.50.0` 新增公开 Application 能力，保持格式28/16/6、配置1、profile2、SDK、生产程序集划分和固定依赖；精确产品版本打开门禁继续有效。本批只交付 B1 的首个时间组切片与 B0 必要有限基线，完整 B0／B1 未完成；HTTP／SSE、Contracts／Hosting／Server／Worker、传输修订、目录租约及 B2 独立链路尚未实现。[实施记录](../history/backend-extraction-progress.md)与 roadmap §18.172 记录实际验证出口。全量、性能、长片、用户原窗口、人工物理输入和平台边界不由本批扩大。

## ADR-0195：时间动作共享用例与明确手势合并身份（0.51.0，2026-10-11）

**背景：** B1 时间组切片之后，拖动、微调与元素清除仍在 App。原时间轴只传列表下标，换工程／来源后的迟到回调可能重定向；原真实VM证据确认同次拖动停顿超过实际900毫秒窗口会拆步，不同稳定目标的连续微调会误合并。持续打开 `BeginGroup` 会吞入无关命令并禁止撤销，且原满容量组起点另有Count边界，不适合作为时间手势实现。

**决定：** 在既有 Application 中共享时间动作，按工作区引用／Generation、明确来源／行／元素 ID 和原时间验证；App捕获输入Session与真实控件手势身份。维持实时提交：每个接受的更新立即产生工程和保守变化摘要；同次拖动用唯一显式身份跨时间窗合并，连续微调用稳定目标与操作身份并保留原900毫秒窗口。合并仅限同名称／身份、当前栈顶结果引用和未断开的连续历史；无身份旧命令与显式身份互不合并。Application内部增加窄历史／工作区调用，不改变Core命令与原公开Execute、BeginGroup合同。

历史真实提交、Undo／Redo／Break／Clear和组边界改变连续性身份；手势只消费自己接受更新后的身份。失效输入永久结束，撤销恰好回到旧Project引用也不能复活；旧手势的迟到结束不得断开新手势。没有变化不写历史、dirty或redo；相同端点的显式不可合并提交仍沿原合同断开前后合并，普通无变化手势更新保持手势连续性。微调使用整数微秒及checked边界，越界／重叠／包含性／呈现失败直接拒绝，保持时长；拖动原吸附与邻接边界钳制仍由控件承担。仅迁出现有选中元素清除，不新增行／批量清除。

控件开始时先准备时间草稿，再按同一稳定目标重读基线；每次更新和结束携带同一手势对象，窗口拒绝没有匹配开始的回调。普通松开、捕获丢失与拆除均保留已接受变化且结束一次，不增加Esc回滚或自动停播。旧 `BeginGroup` 满容量缺陷只记录，本批新路径不依赖它；格式28/16/6、配置1、profile2、SDK、生产程序集与固定依赖保持。

支持自身提交通知期间的同步只读手势观察与显式End／Dispose，重入Update拒绝，其他重入工程写入不受支持；同步观察不应使自身提交永久失效。

**交付边界：** 新增公开Application能力按MINOR0.51.0记录，原有限基线268项、最终335项定向及Headless动作41／草稿70、四项成功编译后变异和源码恢复证据见[实施记录§5](../history/backend-extraction-progress.md#timing-actions-0510)与roadmap §18.173。完整B0／B1、歌词正文／行操作、导入／设置、HTTP／SSE／服务／Worker及独立保存重开仍未完成，全量／性能／正式导出／人工平台边界不自动恢复。

## ADR-0196：共享正文、注音与行操作按稳定目标提交（0.52.0，2026-10-11）

**背景：** 用户继续授权B1，迁移现有正文、注音、新增行、复制行和删除行五类入口。原0.51.0真实VM执行体确认：跨行正文和跨元素注音会按同文案键误合并；连续复制带注音的行会因编号扫描遗漏注音ID产生全局标识冲突；行新增的工程通知先于最终选择，删除最后定时行后选中块下标仍指向空列表。原同ID换工程的注音虚拟窗口路线未复现污染，不能把静态身份风险写作已证GUI缺陷。

**决定：** Application公开 `LyricContentEditUseCase` 及稳定请求、结果、歌词变化摘要和全歌词ID分配器。请求验证工作区引用／Generation、明确来源／行／元素及被编辑原值；正文以原文字门禁使用当前行的其余数据，注音验证完整原注音，结构操作验证完整原行。重切继续使用Core完整Unicode文本元素规则，未改区域保留元素身份、样式和整微秒时间；新元素未定时。注音保留ID、语言、比例、间距与对齐，空文字按原规则删除。复制继续整体后移原时长、重映射基础字注音和展示覆盖；删除只清理已删除展示来源，原悬空注音和FollowPrimary关联保持Warning，不静默修正。

连续正文／注音输入按稳定目标与操作身份合并，保留工作区实际900毫秒窗口；内部历史身份泛化，现有时间API、唯一拖动身份、连续性门禁与旧公开Execute／BeginGroup合同保持。无变化保持工程引用、dirty、redo及零变化通知，成功结构操作一次历史。ID分配扫描所有来源、行、元素和注音，沿原line-/token-编号，分配即保留；范围耗尽用中立代码拒绝，客户端使用中文资源反馈，未迁邻接入口也捕获这一明确异常。

App只适配当前输入，同步TextProperty变化消费捕获的工作区／Session／Generation、选择会话、来源与稳定ID及原值，避免延后TextChanged重新捕获当前选择。正文仍即时提交；Enter／失焦断合并，Esc重读已提交当前值，输入法预编辑Enter／Esc仍由输入法处理。正文和注音按摘要更新受影响行／元素；未改模型、时间和选区保留，结构及未知字段走保守重建，所有实际变化失效预览。行操作的结果选择在统一Adopt中完成，再发布一次工程通知；空来源同时清空选中时间块。撤销／重做使用同一变化分类和读模型出口。

**验证：** 原有限基线443项通过，原DLL真实VM探针复现上述五项行为，原同ID换工程注音路线未复现并保留记录。恢复变异后solution零警告／错误、519项有限定向全部执行通过；隔离Headless正文33、时间草稿70、时间动作41、设置与添加歌词73项通过，各自退出0且窗口关闭。四项成功编译后变异分别被ID唯一、跨目标历史、通知时读模型／选择与正文／注音实例断言拒绝，40个冻结源码逐字节／SHA恢复并正常复验。338文件普通隔离包已启动供人工手测，PID1374333／niri707，日志仅版本；普通启动不等于原生交互验收。证据及初始编译失败见[实施记录§6](../history/backend-extraction-progress.md#lyrics-content-0520)／roadmap§18.174。

**版本与边界：** 新增公开Application能力采用MINOR0.52.0，格式28/16/6、配置1、profile2、SDK、生产程序集与固定依赖保持。仅本批五类正文／行操作自动出口完成；B1仍部分：打点、平移、拆分／合并、规则重切、行排序、导入和设置未迁完。完整B0、HTTP／SSE／独立服务／Worker及独立保存重开未完成，全量／性能／正式导出与人工平台边界及原暂停goal不自动恢复。

## ADR-0197：固定字节导入与标量工程设置共享，桌面交互保留会话门禁（0.53.0，2026-10-11）

**背景：** 用户授权接续B1导入／设置。原0.52.0 DLL实证追加通知先见旧选区、文件新来源导入双通知、Detect与Import双读不同内容、裸path空文件清空而selection入口拒绝，以及实际呈现门禁拒绝后仍发布成功报告。共享解析不能依赖GUI路径，设置标量不应引入媒体或声卡生命周期。

**决定：** Application公开防御复制的 `LyricsImportSource`、Detect／Prepare与稳定提交请求／结果；Prepare只消费固定字节并沿Core格式注册表、编码、切分和估时，不读路径或写工作区。替换／追加验证Workspace引用／Generation及完整ExpectedBranch，新来源验证完整ExpectedLyrics；无关字段编辑不阻塞目标未改的共享请求。保留实际报告、容错有效行、全局ID及注音映射；空候选统一拒绝，耗尽／呈现拒绝原子保持工程、历史、dirty、redo、选择和报告。文件新来源保持Independent，粘贴新翻译来源保持FollowPrimary，新来源连同行一次历史。

GUI文件导入另外捕获Session、整份Project及Snapshot引用，在每次await后检查关闭／取消／工作区身份；任何工程编辑拒绝旧准备，编辑后Undo回同一Project不能复活。成功结果先在Adopt同步来源、首行、读模型及报告，再一次工程通知；取消／过期／失败不发布成功报告或覆盖当前反馈。此保守GUI边界与Application目标粒度门禁有意不同，本批不新增传输revision或历史会话连续性协议。来源Combo同步期间暂停选择处理器并避免同集合重赋，实际用户切换保持。

新建只共享纯解析并内部透传固定候选，已选路径删除或改写不改变候选；原音频／字体／播放准备与失败保留旧工程继续由App负责。异步Choose请求有独立身份及CTS，确认进入准备、重选或关闭使旧请求失效，旧结果／异常不能重开过时对话框或覆盖新候选。

设置共享画布、帧率、底色、时长四字段组，原值组与Workspace／Generation允许无关组编辑；UseLyricsEnd按当前歌词计算，媒体入口只传已准备整数微秒。用户设置拒绝非正数，允许时长未确定和奇数尺寸；Core模型继续允许表达非法数据，既有保存／导出门禁保持。App保留非法草稿、IME、精度、返回任务及实际Editor身份；有效变化一次历史，无变化保留原引用及redo。

**验证与边界：** 原solution零警告／错误、223项有限基线及Headless73通过；原DLL隔离探针51直接引用、8检查证明上述五类行为。恢复31冻结源码后正常solution零警告／错误、328项有限测试和Headless51／73／33通过；四项成功编译后变异由完整来源、Independent、宽高原值组及Snapshot真实断言拒绝。普通338文件隔离包PID1420716／niri731已启动，日志仅0.53.0版本，旧0.52 PID1374333保留，不算人工／原生验收。详见[实施记录§7](../history/backend-extraction-progress.md#lyrics-import-settings-0530)／roadmap§18.175。

MINOR0.53.0新增Application能力，格式28/16/6、配置1、profile2、SDK、生产程序集和固定依赖保持。本批in-process能力完成，B1来源管理、打点、整行时间平移、元素拆分／合并、规则重切与行内元素重排等仍在App；媒体生命周期、新建整体准备、完整B0／B1、HTTP／SSE／服务／Worker和独立保存重开未完成。全量／性能／正式导出／长片、人工平台与原暂停goal不自动恢复。

## ADR-0198：来源管理共享受控候选，桌面确认保留固定目标与草稿权限（0.54.0，2026-10-11）

**背景：** B1来源操作仍由App提交。原0.53.0 DLL实证新增双通知、呈现拒绝后新增／删除假成功、实际Save拒绝仍关闭，以及新增／删除modal经外部编辑Undo回原Project后复活；未修改关联Save写历史与实际关窗晚到污染两条未复现，不能记成修复缺陷。

**决定：** Application公开AddTranslation／Remove／Configure／LinkPrimaryLine。请求验证Workspace引用／Generation，新增验证完整歌词，删除／配置验证完整来源，单行关联只验证原PrimaryLineId并保留当前其余行数据。新增空Translation沿FollowPrimary默认；主来源不能删除或关联。配置仅元数据、StyleOverride、可选单行关联和可选高级展示；原展示投影保护明确目标，当前片段／自定义节点／槽位身份保持。显式非法元数据与展示在no-op前拒绝，合法相同投影不补默认存储，未写展示的无变化不修补旧整树。有效变化一次不可合并历史，无变化保持引用／dirty／redo。

删除只清理已删来源的program／clip／lineoverride引用；节点、效果、预设及原悬空诊断保持，不自动删或重绑。稳定关联允许三种时间策略与空值解除，未改悬空关联仍Warning。旧UpdateBranch收窄为配置兼容适配，只允许元数据／样式／可选展示和至多单行PrimaryLineId，不再任意替换正文／时间／元素／行结构或多行关联；旧bool无变化false保持。

GUI来源窗口额外捕获Session、整份Project和Snapshot，await／关闭消费固定目标；外部编辑Undo回同Project不复活旧结果。Remove另保留原选择门禁；拒绝保留窗口／草稿／本地错误，无变化可关闭。成功在Adopt同步选择／读模型／预览后通知一次，失败不报告假成功。Application允许无关字段变化，与GUI保守会话边界有意不同。

本批实际合法时间草稿在FollowPrimary配置后先红，再由App按捕获的稳定来源行检查提交权限；拒绝保留精确原时间、原文、工程和历史，Undo恢复Independent后原草稿可提交。Core／共享显式修改自身时间能力仍有效，GUI只读策略不等于领域非法，不改变逐字时间API。

**证据与边界：** 原116有限基线与Headless33通过，51原DLL／12检查区分六类实证和两条未复现；最终solution零警告／错误、190有限测试与Headless34／70／33通过。四项成功编译后变异被真实断言拒绝，16源码原字节／SHA恢复并正常复验。338文件普通隔离0.54包PID1463388／niri758已启动，日志仅版本；旧包保留、旧PID已退出原因未知，无自动交互。细节见[实施记录§8](../history/backend-extraction-progress.md#lyrics-branches-0540)／roadmap§18.176。

MINOR0.54.0新增公开Application能力并收窄旧公开入口合同，格式28/16/6、配置1、profile2、SDK、生产程序集和固定依赖保持。完整B0／B1、未迁打点／平移／拆合／规则重切／行内重排、媒体生命周期、HTTP／SSE／服务／Worker及独立保存重开未完成；全量、性能、正式导出／长片、人工平台和原暂停goal不自动恢复。
## ADR-0199：播放打点共享稳定元素时间用例，估算占位与确认分开（0.55.0，2026-10-11）

**背景：** 起点／终点打点仍由 `EditorViewModel.TimelineEditing` 直接改工程。原入口按时间轴选择与当前 VM 状态计算端点，未定时元素必须先补时间；起点的占位终点是程序猜测值，不能冒充用户已编辑。时间组已经共享稳定 ID 端点规则，本批只迁移打点的业务提交，不迁媒体／播放生命周期。

**决定：** Application公开 `LyricTimingEditUseCase.StampTokenStart` 与 `StampTokenEnd`。两者都捕获 `ProjectWorkspace`、Generation、BranchId／LineId／TokenId 和原 `TimeRange?`，拒绝旧工作区、旧原值、非元素目标或领域冲突；不依赖时间轴块下标。播放头统一使用整数微秒：始终夹到非负，媒体时长为正时再施加上界，未知时长不把正播放头归零。起点没有有效终点时按工程帧率补一帧，并把最终元素状态写成 `Estimated`；存在仍晚于新起点的终点时保留原终点。终点必须已有有效起点且晚于起点，确认后状态为 `Edited`，即使播放头恰好落在估算占位末端。每次实际打点只写一次不可合并的 `token.stamp` 历史；无变化、拒绝不改工程、dirty、redo或通知。

Avalonia仅保留选中行／元素、播放头会话、本地化提示和终点成功后的下一个元素选择；成功结果经统一 `AdoptProject` 增量同步，失败保留现有读模型。媒体读取、播放轮询、快捷键和其他时间业务继续由App负责。

**验证与边界：** 新增Application打点回归覆盖未定时补帧／Estimated、未知媒体时长、既有终点、终点确认、同端点状态变化、过早／媒体边界拒绝、Workspace／原值失效、一次历史及undo／redo；Avalonia原动作回归继续通过。成功编译后的起点占位／终点确认变异必须被断言拒绝并恢复源文件后复验。完整B1、整行平移、拆分／合并、规则重切、行内重排、媒体生命周期、HTTP／SSE／Worker和独立保存重开仍未完成。

MINOR0.55.0新增公开Application能力，格式28/16/6、配置1、profile2、SDK、生产程序集和固定依赖保持；本批仅完成打点in-process有限出口，完整B0／B1及人工／平台边界不由此扩大。


## ADR-0200：B1共享编辑收口，桌面适配只保留交互边界（0.56.0，2026-10-12）

**背景：** 0.50.0～0.55.0已经把时间组、现有时间动作、正文／行操作、固定字节导入／标量设置、来源管理和播放打点迁入Application，但整行平移、元素拆合、固定行重切、明确来源全行重切、行内重排及主窗口打点快捷键仍由Avalonia路径直接改工程。继续保留这些领域规则的第二份实现，会让未来HTTP／Web入口重复校验并继续放大UI线程延迟和过期结果风险。

**决定：** 0.56.0在现有Application中公开并接入`PreviewShiftLine`／`ShiftLine`、`PreviewReorderToken`／`ReorderToken`、`SplitToken`、`MergeToken`、`ResplitLine`和`ResplitBranch`；`StampTokenStart`／`StampTokenEnd`的`[`／`]`路由接入主窗口。所有请求按Workspace／Generation、稳定来源／行／元素ID和完整原值或完整来源基线校验；预览无副作用，成功变化一次不可合并历史，无变化与拒绝保留原工程引用、dirty和redo。重切和拆合使用全歌词ID命名空间，比例端点用`Int128`和整数偶数舍入；非法持久时间范围明确拒绝。Avalonia只保留输入法、非法草稿、确认／窗口生命周期、选择、结果消息和媒体／播放生命周期，统一在Adopt后更新读模型并发布一次工程通知。

**验证与边界：** solution零警告／错误；Application285、App113、Core59、Runtime.Hosting依赖2共459项有限定向通过；六条隔离Headless流程58／68／70／33／41／34共304项通过。三项成功编译后的变异由真实断言拒绝，32个冻结源码逐字节／SHA恢复并正常复验；普通338文件隔离包PID6260仅作手测入口。旧`ProjectHistory.BeginGroup`满容量缺陷仍存在，本批仅删除新路径依赖，未宣称修复。该ADR仍是进程内B1收口，不等于Backend.Contracts／Hosting／Server／Worker、HTTP／SSE、无GUI媒体、独立保存重开或性能出口完成；后续B2先处理无GUI媒体与工程组装。详见[实施记录§10](../history/backend-extraction-progress.md#b1-closure-0560)与roadmap§18.178。


## ADR-0201：B2首切片分离无GUI新建候选与桌面播放发布（0.57.0，2026-10-12）

**背景：** B1进程内共享编辑已收口，但新建仍由App组合固定歌词解析、媒体解码、工程组装、声卡创建与播放加载。直接把这条链迁到服务会让无窗口入口继续依赖VM、本地化和物理播放设备；歌词固定候选也不能被误写成音频资产授权或固定音频内容保证。

**决定：** 新增 `Backend.Hosting` 组合Application／Core／Media，不依赖App、Avalonia或ASP.NET；Application继续只依赖Core。`NewProjectPreparationService` 接收名称、Composition、受信本机音频路径、固定 `LyricsImportSource` 及导入选项，返回领域工程、实际解码波形与结构化导入报告，或结构化准备错误；不修改工作区、历史或dirty，取消继续抛 `OperationCanceledException`。未知实现异常保留原异常，不统统冒充用户输入错误。候选只包含托管数据，不提供没有资源职责的Dispose。

仅歌词输入固定；音频探测、解码、桌面播放仍可能多次读取路径，本批不承诺不可变媒体快照、assetId上传／授权、内容一致性或任意服务端路径访问。App只适配文件输入、报告／错误本地化与桌面播放器准备；成功播放器显式移交，失败、取消、过期结果释放，字体Shape前置门禁保留。主窗口按Workspace／Session／Generation／Project／Snapshot拒绝旧候选，编辑后Undo回原Project不能复活；此门禁不扩写成StartNew之后任意UI回调异常的事务回滚保证。

**验证与边界：** solution零警告／错误；Application92、App64、Media53、Backend.Hosting31、Runtime.Hosting依赖2共242项有限定向通过，Headless 89＋51、真实媒体30项全部通过；两项成功编译后的变异被真实断言拒绝，19个冻结源码逐字节／SHA恢复，341文件普通隔离包已启动。证据见[实施记录§11](../history/backend-extraction-progress.md#b2-new-project-0570)／roadmap§18.179。MINOR0.57.0新增公开Hosting能力与生产程序集，保持格式28/16/6、配置1、profile2、作者SDK及固定依赖。B2的Contracts／Server、HTTP／SSE、队列／修订／目录租约、资源授权与真实独立保存重开未实现，完整B0、性能／全量／正式导出／长片及人工平台出口和原暂停goal不自动恢复。


## ADR-0202：B2-A采用传输契约与托管上传创建本机会话种子（0.58.0，2026-10-12）

**背景：** 0.57.0仅从App提取无GUI新建候选，音频仍来自受信本机路径；Web调用需要中立DTO、上传字节和独立进程入口，不能把浏览器路径当后端访问权限。

**决定：** `a8c8a63 / 0.58.0` 新增只依赖BCL／JSON的Backend.Contracts和ASP.NET Core Backend.Server，Hosting不依赖ASP.NET／App。资源复制到受控目录后发布固定assetId／SHA-256／MIME／大小，执行单文件与累计配额、文件名越界及客户端标签归属门禁。创建只引用上传资源，使用原共享无GUI准备服务，成功同步返回201与初始projectRevision `"0"`；服务新启动生成hostInstanceId，会话不与Avalonia共享裸工作区。

**验证与边界：** 种子Backend.Hosting35项通过、Server编译通过；当时本机缺Microsoft.AspNetCore.App，HTTP探针没有实跑，不能记真实媒体或独立API验收。临时自报clientId只做标签隔离，不等于token认证；Host／Origin、clients握手／租约和正式资源生命周期尚未实现。早期生产假媒体开关及探针不能作为解码证据，B2-B改用默认真实媒体路径。种子误用isSaved:true的新建基线也由B2-B修正，不把旧isDirty:false当作落盘。MINOR0.58.0新增生产程序集与公开契约，格式28/16/6、配置1、profile2、SDK与固定依赖保持。实际历史和后续补验见[实施记录§12](../history/backend-extraction-progress.md#b2-transport-seed-0580)／roadmap§18.180；完整B2未完成。

## ADR-0203：B2-B以单写者执行具名时间历史命令并保留有界回执（0.59.0，2026-10-12）

**背景：** 传输种子只有上传／创建／摘要，缺少编辑修订与HTTP历史；不能允许HTTP直接改Core树、并行读取半提交状态或靠Generation充当编辑版本。

**决定：** 每个服务会话的时间命令、撤销／重做、摘要／历史／歌词投影共用一个Writer闸门。只开放lyrics.setTimeRange、history.undo、history.redo；稳定branchId／lineId／可选tokenId映射到既有LyricTimingEditUseCase，整数微秒与projectRevision以字符串传输。每次实际工程变更递增修订，撤销／重做也递增；合法no-op保持原引用、历史／redo、dirty、修订和eventCursor。各读结果内部与其修订一致，独立GET之间不承诺快照相同；复杂变化保守要求完整刷新。

回执作用域限定为 `(clientId, projectSessionId, requestId)`，同ID在不同会话可各执行，不宣称client级跨方法／路径去重。命令在Hosting校验requestId／hostInstanceId／validUntilUtc，回执先于expectedRevision检查，同ID同DTO语义返回首次结果，异载荷拒绝。默认允许600秒重试窗口／每会话1024有效回执；过期回执锁内清理，有效容量满时拒新请求，不驱逐仍可重试结果。指纹是固定DTO字段序列化SHA-256、合法整数十进制及UTC截止时间规范化，不等于通用任意JSON或multipart去重。capabilities公布服务时间与实际上限；上传／创建未自动获得此协议。

新建改为isSaved:false，摘要显式hasPath:false，保持初始及编辑后undo未保存，与Avalonia StartNew一致；尚无savedRevision／磁盘保存。eventCursor当前为null并在HTTP省略，无SSE或断连恢复；错误采用稳定code／message／field与problem+json媒体类型，不宣称完整RFC字段。0.59.0移除Server生产假媒体开关，实际媒体只能使用默认共享FFmpeg准备服务；监听限定显式loopback，拒绝非本机绑定。上传staging每chunk先占quota，失败／取消回滚，关闭独占写句柄后才发布重命名；不把该卫生修复扩大为完整资源回收或Windows平台验收。

**验证与边界：** 最终solution退出0且零警告／错误，Hosting61项通过；独立HTTP真实2秒WAV经默认FFmpeg准备成功，688断言／110请求通过，51错误实际problem+json；两测试服务正常退出0并清理资源，最终源码／证据SHA一致。本机缺ASP.NET运行时，通过linux-x64 self-contained实际执行。旧host先于session查找的补丁与真实重启边界已验证；首次夹具失败和中间678正向均排除。最终日志、TRX、manifest与哈希汇总见[实施记录§13](../history/backend-extraction-progress.md#b2-timing-http-0590)。MINOR0.59.0新增公开API，格式28/16/6、配置1、profile2、作者SDK与固定依赖保持。本批不交付客户端认证／Host／Origin、SSE事件恢复、资源／目录租约、独立保存重开、任务／Worker、帧或完整B2；不恢复完整B0、全量、性能、正式导出／长片、原生／人工平台或原暂停goal。roadmap§18.181记录当前有限出口。

## ADR-0204：B2以本机生命周期、单层回执及固定快照持久化闭环接续（0.60.0，2026-10-12）

**状态：已接受并完成本批有限出口验收。** 用户授权完成B2剩余范围。0.60.0统一交付相互依赖的生命周期与保存接口，保持精确产品版本门禁、格式28／16／6、配置1、包profile2、作者SDK与固定依赖。

**决定：** 继续采用用户本机loopback服务。随机启动凭证由可信本机通道交付；客户端获得独立短期访问凭证与恢复凭证，Host／Origin明确限制。工程与任务不会因SSE断开或短期令牌过期被隐式丢弃；访问恢复不等于复活旧host的写请求。

有副作用请求统一由Hosting单层保留client级回执，绑定requestId、操作、语义载荷、主机和截止时间；HTTP适配器只处理传输与已接受操作的生命周期登记，不能再包一层同一回执。有效结果与占位按数量／字节有界，先回执后修订，失败或序列化错误也终结占位。客户端state在成员集合与全部有序Writer共同边界内捕获摘要和eventCursor；事件有界重放，缺口重新读state。

媒体准备、工程与响应持有实际资源引用，未引用上传按TTL回收。目录写租约在Application实现，同批App打开／保存／备份和服务路径共同参与；候选保存失败保持原租约，旧写入到安全点后才释放，旧不参与执行体不在互斥保证内。

保存捕获第N版，在后台串行IO，完成在Writer内仅采用实际保存快照；期间第N+1版保持dirty，撤销回保存对象可clean但工程修订仍递增。保存发布阶段不可安全取消，关闭须显式处置dirty与任务，失败保留会话；正常shutdown等待已接受事务再释放资源。持久profile中的最小有界任务账本记录queued／committing／终态与输出身份；重启未完成记录标为结果不明，不推断发布成功、不自动重放，不承诺跨崩溃exactly-once或完整历史恢复。

**验收与边界：** 0.60.0 solution零警告／错误，Hosting82/82通过，真实HTTP1224断言／241请求通过；最终manifest为 `artifacts/backend-extraction/b2-final/http/run-20261012T134242Z-oMcgcE/manifest.json`，源码／执行体／证据SHA一致，探针证据不含凭证。该ADR完成B2本机生命周期与持久化有限出口，但不恢复全量／性能／原生窗口／人工平台或原暂停goal；Worker／预览、导出／验证／分析及作者闭环仍按B3～B5接续。

## ADR-0205：B3-A以中立预览协调器和受管理Worker IPC建立渲染接缝（0.61.0，2026-10-13）

**状态：已接受并完成本批有限技术出口。** B2-C已提供本机生命周期与保存链路，但预览仍留在Avalonia内部，Worker尚未有可回归的进程协议。本批先固定会话调度、旧帧门禁和子进程生命周期，不把尚未接入真实Runtime.Hosting的回显Worker写成完整渲染服务。

**决定：** `BackendPreviewCoordinator` 位于 `Backend.Hosting`，只面向 `Rendering.Abstractions`。每个协调器保留一个执行工作和一个最新待处理槽；输入变化取消在途请求，向前播放允许在途帧完成；每个结果带代次、来源修订和单调序号，只有当前输入且未倒退的帧可发布。像素与诊断在发布前复制，事件锁外调用，订阅者可以重入提交或释放。Avalonia原有协调器的锁内事件改为锁外发布。

`BackendPreviewContracts` 仅使用BCL/JSON，固定预览来源、尺寸、透明模式、代次、序号、帧句柄元数据、诊断分页和稳定错误码，不暴露Core／Skia／Avalonia对象。`Backend.Worker` 使用独立 `worker-1` 协议：little-endian长度前缀JSON有8MiB消息／4MiB payload上限，校验版本／requestId／SHA-256；`WorkerProcessProxy` 用 `ArgumentList` 启动，先hello，取消先发cancel，宽限期后杀进程树，正常关闭发shutdown并等待退出。保存格式不作为IPC格式，当前render只回显opaque payload。

**验证与边界：** solution构建0警告／错误，Hosting93/93、预览20/20、Worker3/3通过；提交与证据见[实施记录§15](../history/backend-extraction-progress.md#b3-preview-worker-0610)。本ADR不完成真实Worker渲染、HTTP帧路由、PNG句柄、诊断分页、脚本失控渲染隔离、持续播放／透明像素等价、B4导出验证分析、B5作者闭环或全量／性能／平台人工验收。

## ADR-0206：B3-B以发布前完整性合同保护预览帧（0.62.0，2026-10-13）

**状态：已接受并完成本批有限技术出口。** B3-A已经固定预览代次、来源修订和Worker生命周期，但尚无真实帧进入句柄或HTTP边界的完整性门禁。本批先把发布前的可验证条件固化，避免后续Server／Worker接入时把过期或损坏字节当成成功帧。

**决定：** `PreviewFrameContract.Validate` 接收当前预览会话、代次、来源修订、上一请求序号、服务限制和实际内容。它依次验证会话／代次／来源、严格递增请求序号、正数尺寸、`image/png`、内容大小、句柄租约、句柄字段和SHA-256。错误映射使用稳定 `PreviewGenerationConflict`、`PreviewRequestOutOfOrder`、`PreviewFrameExpired`、`PreviewFrameNotFound` 或 `InvalidRequest`。验证器不访问路径、编码PNG或创建任务。

**验证与边界：** solution构建0警告／错误，Backend.Hosting 98/98通过；细节见[实施记录§16](../history/backend-extraction-progress.md#b3-frame-integrity-0620)。本ADR不完成真实Runtime.Hosting帧、Worker跨进程输入、句柄租约存储、Server路由、诊断分页、持续播放／透明像素等价、B4／B5或完整Web产品。

## ADR-0207：B3-C以真实同进程 Runtime 帧适配和受保护句柄读取接续（0.63.0，2026-10-13）

**状态：已接受并完成本批有限出口。** B3-B已经固定帧发布完整性，但真实渲染会话仍未进入Backend.Hosting，Server也没有受保护的帧读取边界。本批先接入既有Runtime.Hosting同进程会话，并把已验证字节的读取路径做成独立、有界、可过期的资源边界。

**决定：** `RenderFrameV1SessionFactory` 不重写Skia或脚本运行时，只包裹既有 `IRenderFrameSessionFactory`，固定快照身份、目标预算、取消和所有权。`PreviewFrameHandleStore` 只保存已经通过元数据、PNG签名和SHA验证的内存副本；`PreviewDiagnosticsStore` 绑定client、代次、来源和游标。Server只提供帧句柄 GET/HEAD和诊断页GET，沿用认证、ETag、单Range、If-Range、TTL和稳定错误码，不提供任意路径访问或隐式帧生成。

Worker停止取消必须回收整个子进程树并等待stderr排空，不能仅释放 `Process` 对象。跨进程真实Runtime渲染需要下一批独立的render-frame-v1 IPC输入合同，不把当前同进程适配器冒充为Worker渲染。

**验证与边界：** solution构建0警告／错误，Backend.Hosting105/105、Worker4/4、Runtime.Hosting真实静态帧1/1、Avalonia预览20/20通过；Server self-contained未知帧／诊断路由实际返回404 problem+json。细节见[实施记录§17](../history/backend-extraction-progress.md#b3-runtime-frame-0630)。本ADR不完成跨进程Worker真实渲染、预览会话创建、持续帧流、透明像素等价、B4/B5或完整Web产品。

## ADR-0208：B3-D采用专用静态工程快照连接Worker与HTTP预览（0.64.0，2026-10-13）

**状态：已接受并完成本批有限出口。** B3-C只提供同进程真实Runtime接缝和已发布帧读取。纯色40字节Worker探针能证明进程接线，但不能表达用户工程。本批已接通真实歌词、注音与场景的固定静态工程Worker和HTTP预览；完整B3仍按[完成计划](../plans/backend-b3-completion-plan.md)接续。

**决定：** 新增 `Backend.Rendering` 的 `snapshot-v1 / static-lyrics-scene-v1` 源合同，不调用ProjectStore、不复用工程文件DTO或容错选项。全部15个Snapshot源字段明确登记；源成员白名单排除计算属性和可写兼容外观，FrameRate、MaterialSpec、SceneContent、OptionalValue与MediaFileRevision使用严格转换。读取拒绝缺失／重复／未知字段、错版本、非法领域数据、无效判别类型和超限输入，保留整数微秒及三态覆盖。音频路径与身份作为声明性字段无损保留，codec零IO、不授予文件能力；媒体身份不得脱离非空路径，path-only不推断固定内容。效果／预设／包材质／分析和图片／视频背景明确拒绝，不能静默删掉再返回成功帧。

Worker消费该静态合同并调用既有ProjectRenderSessionFactory，open创建一次真实Runtime会话，同代次连续前向帧复用该会话。私有Worker接口支持同快照下的后退时刻；HTTP seek按合同换代，Coordinator.Invalidate排水／释放旧Runtime与Worker，再new open重建，不把私有接口能力解释为HTTP seek复用。Hosting代理实现原IRenderFrameSessionFactory，校验输入和裸BGRA帧元数据／hash，并复用取消、超时及进程树回收。主服务只捕获授权工程的精确修订与资源租约；预览所有者固定Snapshot、factory及租约，维护独立代次、客户端请求序号、一执行＋最新待处理槽、seek／input换代和关闭排水。客户端断连不变成已接受帧请求的取消。

PNG编码位于独立 `Backend.Rendering.Skia` 适配层；Hosting只依中立接口，Contracts保持BCL。预乘BGRA在PNG边界转换为straight alpha，真实编码字节经结构／尺寸／CRC／预算／hash门禁进入帧句柄；SSE只传状态和元数据，二进制沿受认证的帧读取路线。帧与失败的实际Runtime发生快照以中立 `RenderSessionFailureException` 和专用Worker DTO保留稳定编号及发生身份；没有Runtime快照的失败明确作为transport诊断，不能制造一条虚构的作者发生。保留的诊断按有限预算分页，不按文本相同吞掉真实发生；这不是完整脚本诊断生产链的验收。

首次Create／open的Worker协议错误、固定输入错误和缺失／不可执行执行体也转换为中立失败，保留stable code、实际存在的快照及内部异常；HTTP公开消息脱敏，无实际快照时只报告transportFailure。这保证启动失败和后续帧失败具有同一状态／SSE／诊断出口。

**验证与边界：** 最终solution构建0警告／错误；Hosting221、Runtime依赖＋真实Worker11、Avalonia预览20、codec40、PNG9、self-contained Server14，共315执行／315通过，无失败／跳过／中止。固定实际字体身份的同进程及真实Worker普通／透明帧与原Runtime逐字节相等；最终真实HTTP112请求、458工作流断言＋247凭证产物扫描，三帧各57,600像素RGB／alpha误差0。实际Worker终止和自有启动launcher删除分别保留WorkerSessionConflict／WorkerUnavailable，两者均明确transportFailure无作者occurrence；静态成功诊断为空，不代表真实脚本发生。最终manifest、SHA与排除的历史／prefinal证据见[实施记录§18](../history/backend-extraction-progress.md#b3-static-preview-0640)。

MINOR0.64.0新增公开有限能力与专用适配程序集，格式28／16／6、配置1、包profile2、作者SDK和固定依赖保持。真实包／脚本／预设／分析、图片／视频闭包、可搬移字体能力、持久Worker脚本会话和真实失控脚本恢复、完整几何、B4／B5及完整Web客户端未完成；不宣称渲染引擎重写或性能提升，不恢复全量／性能／原生／平台人工及原暂停范围。

## ADR-0209：B4-A以固定静态工程正式验证接入唯一任务账本（0.65.0，2026-10-13）

**状态：已接受并完成本批有限出口。** 用户要求开始B4并维护统一当前API；完整B3余项不作整体前置。首批限定现有static-lyrics-scene-v1，以原RuntimeFrameValidator和受管理Worker运行固定普通／透明帧计划；服务导出、分析和完整脚本／媒体另批开放，不能由正式静态验证推导为已实现。

**决定：** 在工程Writer内门禁精确修订、捕获不可变快照与已授权资源引用，使用专用32字节任务头和固定源SHA／计划；Worker内渲染真实帧，仅回有界进度与完整报告。两个输出各建独立真实Runtime会话。每输出600／总1200帧，最后整数时刻不超过固定时长；本地最大1920×1080／8MiB不改变HTTP预览的4MiB裸帧预算。

同一 `_jobs`／BackendJob／BackendJobLedger承载持久操作及正式验证，不再新增另一套job身份、回执或恢复账本。正式任务配额为running全局2／每client1／每project1、queued全局16／每client4；接受后不受随后编辑、HTTP断连或client休眠的取消影响，拥有输入引用到实际Worker排水。显式取消／deadline先协作再按2秒宽限回收拥有进程树；deadline默认60秒，可信宿主可配置1～60，实际capabilities公布，探针8秒仅为夹具。服务停止接收并取消可取消正式任务、等待释放，不能强制撤回已有保存committing。

报告最多256KiB／256issues／512occurrences，超过预算明确失败，禁止截断成假完整报告。报告原子sidecar发布，ledger的32KiB单记录只存有界身份／引用／hash；授权读取、保留淘汰及重启核对须沿同一job生命周期。live job／报告／诊断严格当前owner；previousHost且唯一ledger登记reportref的显式recovery结果读取，沿B2本机profile-wide恢复权限，由当前凭bootstrap登记的profile client以accessToken读取。旧client／resume不跨host恢复，不回退live ACL、不自动重跑或把unknown升成功。无真实Runtime报告时明确transport failure，不造作者发生。RuntimeFrameValidator保留primary失败与OCE，cleanup失败只补充；中立失败保stable code及实际发生快照。现有Export也不得用静态fallback吞掉中立会话失败，此兼容修复不开放HTTP导出。

**验证与边界：** 正常solution构建0警告／错误，最终限定Hosting268、Runtime28、Worker8、self-contained Server16、Export10、作者取消2，合计332执行／332通过／0失败／跳过／中止。真正HTTP389请求，1344工作流＋808凭证扫描共2152断言；FullHD3＋3帧的报告、实际进度／SSE、整数时刻及固定codec源SHA／profile与独立原验证器一致。排队／运行取消、8秒可信host deadline、真实Worker终止／首次启动丢失、报告篡改500及恢复、发布失败、休眠／关闭／shutdown／profile恢复均实际覆盖，两个Server退出0、八Worker全退出。完整source／发布／证据SHA与排除过程见[实施§19](../history/backend-extraction-progress.md#b4-static-validation-0650)。

MINOR0.65.0保持格式28／16／6、配置1、profile2、作者SDK与固定依赖。当前路由只维护于[统一API](../../backend-api.md)，[B4计划](../plans/backend-b4-plan.md)保留接续；完整B3／B4、包脚本／媒体、导出／分析HTTP及Web产品仍未完成。本批正式报告／进度比对不是PNG像素、FFmpeg影片或脚本验收；Export6项受控encoder与既有4项短片Committing只是兼容回归，不恢复全量／性能／GUI／长片及人工平台／原暂停范围。


## ADR-0210：B4-B以固定静态导出与不可变产物包接续正式任务（0.66.0，2026-10-13）

**状态：已接受并完成本批有限出口。** 接续B4-A，以既有Runtime／VideoExporter／FFmpeg实际编码，不等待完整B3，但不能把正式验证报告当作成片证明。首批仅static-lyrics-scene-v1和download-only产物；单job选普通H.264 MP4或透明ProRes4444 MOV，Standard编码／Final渲染。

**决定：** 精确工程修订在Writer内固定原codec SHA、计划及授权资源。includeAudio默认true，只接受受租约保护的自包含PCM16 WAV mono／stereo，直接pin ResourceStore文件、独立记录audio grant hash／size并在Worker复核，不因snapshot AudioPath授任意IO；源采样时长严格覆盖派生视频时长，不静音降级。false显式纯视频。非空包／脚本／预设／分析／图片视频背景拒绝，原Export领域校验保持。帧数沿Round(AwayFromZero)／至少1帧，整数时刻，nominal及派生时长均≤10秒、≤60fps／600帧，FullHD／raw8MiB。源／产物音频时长分别记录；实际音轨50ms量化容差不放宽源覆盖或精确decoded帧数。IsVerified保原nominal验证值，StrictlyVerified才是新派生计划门禁；低fps可保前者false而后者true。受限WAV路径跳过-shortest避免真实NTSC末帧丢失，保原已有-t，默认桌面路径保持。

validation／export共用唯一scheduler、jobs和ledger，running2全局／1client／1project、queued16／4；持久化控制job不占slot。每export先预留64MiB＋256KiB，总512MiB含queued／running／retained／active-read；guard只保证Linux-x64单文件RLIMIT_FSIZE，不支持时disabled，不宣称全磁盘或跨平台限额。deadline默认120秒、可信host1～120，宽限2秒，实际能力公布。私有Exporter只提交candidate，preparing-result仍可public cancel；实际Worker／Runtime／FFmpeg排水、严格媒体／报告校验及最后取消检查后，唯一Job.EnterCommitting→同文件系统一次Directory.Move媒体与报告包→唯一ledger终态。迟到cancel不改成功，ledger失败unknown不重跑。

完整报告256KiB／512occurrences不截断。真实非空snapshot失败可发布kind=exportFailure的report-only包，Job只留FailureReportRef、无Artifact／content；transport-only只保stable code，不造报告或发生，cancel先赢公开闸门不发布。live读取严格owner，previousHost登记引用的显式recovery沿B2当前bootstrap登记profile客户端权限，不恢复旧owner／resume；默认授权保留1小时，启动／admission prune，流式read lease保持容量占位。公开HTTP唯一规范见[backend-api](../../backend-api.md)。

新增BCL-only Processes负责私有进程组／文件guard／有界文本，真实Worker leader先退出时仍回收登记group；普通legacy路径按leader存活时登记的PPID／PID／startTicks后代回收，不调用waitpid(-1)抢其他.NET Process退出权。运行期取消／shutdown控制写入及回执共用宽限deadline，清理失败保primary。普通legacy未登记且leader先亡的后代不在全树保证内，export拒绝缺guard路径；StartAsync握手失败叠加Stop异常的主因保真未在本批fresh复现，作为后续卫生项，不虚称所有启动双故障已修。

**验证与边界：** 修后正常solution0警告／错误，显式7组8TRX合计436／436、无失败／跳过／中止；Hosting325／Worker8／self-contained Server19／Export64／Media3／Processes5／Runtime6／Core6。初Hosting320／323的旧回收3失败保留，窄修后同全范围325含原断言通过，不过滤失败；真实NTSC59／60原红与修后60也保留。独立HTTP原74546实际764请求／4057工作流＋1625凭证扫描共5682断言，三个实际90帧影片及完整音轨／原Runtime内容oracle通过，双声道离线29另列；最终source／部署／证据SHA核验一致，详见[实施§20](../history/backend-extraction-progress.md#b4-static-export-0660)。MINOR0.66保持格式28／16／6、配置1、profile2、作者SDK与固定依赖。完整B3／B4、其他音频输入／包脚本／图片视频／分析HTTP／Web及全量／性能／GUI／长片／平台人工仍未完成，原暂停范围保持。


## ADR-0211：B4-C以固定音频和独立规范结果接入唯一任务链（0.67.0，2026-10-13）

**状态：已接受并完成本批有限出口。** 分析不依赖完整B3输入能力，不把接受或完成任务解释为写工程。只固定当前工程exactRevision及已授权audio grant，直接pin不可变资源；不捕获snapshot／staticcodec，含effects或已有analysis的工程也可分析音频。来源`pcm16-wave-analysis-v1`与私有执行`analysis-pcmwav-v1`独立，`static-lyrics-scene-v1`保持不变。没有apply端点，不调用SetAudioAnalysisCommand，不改修订／dirty／undo。

**决定：** 保留原AudioFeatureAnalyzer／Builder及Core AudioAnalysisJson，强自包含PCM16 WAV mono／stereo、≤192kHz／16MiB／300秒；输出48k mono／960sample hop，实际14.4M样本／15k points，Append前守限，不截短成功。整曲归一化、短尾、double精度、source／PCM hash与ceil时长不改，NoStream／Unavailable／失败不伪造available，OCE保取消。完整canonical≤4MiB；原文件整数number均在本profile JSsafe上限内，摘要／进度长整数为string。语义Revision与完整ContentHash、工程revision及wire输入SHA分别使用；ContentHash等于登记原字节SHA。

validation／export／analysis共享唯一jobs／scheduler／ledger／client回执，running2全局／1client／1project、queued16／4；持久化控制不占slot。每analysis预约4MiB，store64MiB含queued／running／retained／expired active-read；TTL默认1h，启动及admission物理prune。deadline默认60／可信1～60，宽限2秒；单独验证实际private group＋subreaper及工具有界启动，不要求export编码器／FSIZE；ToolStartupVerified不代替实际解码证明。全Worker／native排水→有界prepare／flush／hash→lastcancel→唯一EnterCommitting→原子发布→ledger，unknown不重放。

live结果严格owner；previousHost唯一ledger登记引用的显式recovery沿既有B2 profile-wide bootstrap登记客户端权限，不恢复旧owner／resume。完整结果GET／HEAD支持Range／ETag／读取租约，重启同样复核规范网格、身份／hash／摘要，不猜孤立文件或重跑。共用BCL sidecar IO保持旧validation路径／IDs／bytes／预算／权限；写入／flush叠删除失败保primary，残留tmp和启动孤立tmp继续计费。该窄修由源码定位及当前真实文件故障回归证明，不称旧binary复现红。

**当前证据与边界：** 正常solution0警告／错误，Hosting369／Worker12／self-contained Server21／Media30／Core28共460／460，无失败／跳过；产品端点be56180，原session／显式五TRX／源码／版本SHA见[实施§21](../history/backend-extraction-progress.md#b4-audio-analysis-0670)。真正HTTP原9085已exit0，789请求／4243工作流＋1700凭证扫描＝5943，六默认native音频的680全点／651,120样本／71,287canonical bytes与独立原Analyzer byteexact；两Server退出0／43观察group身份全gone。第二Server的-re只用于真实进程控制，不作默认性能证明；最长接受6.513秒，未执行300秒整曲或等满1h TTL。总source／部署／证据SHA已核验，工具端点fdce074。Hosting完整保留旧frame／export兼容，本批不增加独立渲染／影片验收。MINOR0.67保持格式28／16／6、配置1、profile2、SDK与固定依赖；apply／其他音频、完整B3／B4、作者／Web及全量／性能／GUI／长片／平台人工／原暂停范围继续独立。当前HTTP只维护于[统一规范](../../backend-api.md)。


## ADR-0212：B4-D以显式命令采用／清除live同源分析（0.68.0，2026-10-14）

**状态：已接受并完成本批有限出口。** 冻结来源为[B4计划§8](../plans/backend-b4-plan.md#b4-analysis-apply-plan)，实际证据见[实施§22](../history/backend-extraction-progress.md#b4-analysis-apply-0680)。

分析任务仍只产独立结果，接受／完成／读取均不写工程。沿唯一commands增加audio.analysis.apply(analysisJobId)／clear，当前expectedRevision、同client回执、单Writer和只读策略保持；不增加job、scheduler、结果副本或recovery写入。只采用live owner、同session、成功未过期的登记结果；job源修订只作来源，不阻止同音频后续歌词编辑后的显式采用。previousHost恢复继续只读。

完整canonical及当前授权音频声明／实际bytes均有界校验，IO与fallible lease释放必须先于mutation，最后重查live／TTL／来源。声明异源422；登记结果或当前音频实际文件损坏／IO／cleanup失败500；未知／跨client／过期／旧host404。共享基线绑定Workspace／Generation／Project／音频／原分析及历史连续性，一次变化只有原audio.analysis独立历史；活动History group变化拒绝，no-op仍保修订／dirty／redo／事件。摘要只给有界状态及成功身份，不给points或原诊断。

HTTP新建发布前从授权StoredAsset固定AudioMedia hash／size／安全显示名，修原fresh candidate metadata为空的强同源校验接缝；保持rev0／dirty／hasPath及Preparation／App／租约，不在apply补metadata或增加历史。static-lyrics-scene-v1维持拒绝非空分析，clear／undo回空恢复原能力；支持分析渲染的新profile另批决定。0.68 MINOR保持格式28／16／6、配置1、profile2、SDK和固定依赖。

正常solution0警告／错误，Application530／Hosting390／SC Server29／Core限定28共977通过，原session／TRX与SHA见实施§22。真正HTTP原21974退出0，367请求／2522断言、两音频完整canonical byteexact／保存重开／clear后PNG零差及正式1＋1验证／30帧纯视频恢复通过；两Server退出0、本轮16观察group身份全gone，原两工具断言失败、final3归档alias及后续单input漂移另列。最终源码／部署证据见实施§22；完整B3／B4、其他音频、作者／Web与全量／性能／GUI／长片／平台人工及原暂停范围不随本决定完成。


## ADR-0213：B4-E以独立静态profile保完整分析并闭合总wire预算（0.69.0，2026-10-14）

**状态：已接受并完成本批有限出口。** 依据[B4计划§9](../plans/backend-b4-plan.md#b4-analysis-static-profile-plan)，证据见[实施§23](../history/backend-extraction-progress.md#b4-analysis-static-profile-0690)。

复用snapshot-v1唯一Source，中央BackendSnapshotProfiles及唯一SelectProfile将null留旧static-lyrics-scene-v1，nonnull进入新static-lyrics-scene-analysis-v1。默认旧Encode保持拒分析；新profile完整Available canonical／contentHash／sourceHash／Revision／points与Failed／NoStream code/message均保，三状态需要AudioPath＋AudioMedia。不新增项目或更改Core／算法／schema／SDK；无脚本时静态像素不因分析变化，不能据此声称音频驱动视觉。

总4MiB／200k值共享，深JsonElement及Envelope全部计入。首Envelope4值缺口之后确认converter隐含字段亦增加wire值，前置graph guard与Encoder最终有界wire复用Decode原RejectDuplicateFields同规则，不复制另一计数器／改旧wire。组合合法hops／tokens超过总值明确拒绝，不截断／剥数据。preview24／validation32头admission413；export实际metadata＋32在spawn/send前guard，可beforeRegistration413清candidate，否则202后稳定budget失败，不虚称所有动态metadata事前已知。

公开旧sourceProfile兼容，尾加双sourceProfiles；private export EngineIdentity与profiles经实际probe强匹配，旧Worker缺字段／错engine／重复未知profiledisabled。preview/validation仍configured-unverified，配置不是真实运行证明。purevideo保持完整声明／分析，以IncludeAudio=false禁source Probe与encoder音轨；true使用原可信grant重绑路径而保Media身份名称，原encoded sourceSHA与grant分别记录。恢复双明确allowlist，未知拒绝，不扩大权限／重跑unknown。

source oracle按受信自有声明路径重绑保存包后Encode，zeroIO该声明，核同声明SHA而非直接package原SHA=HTTP。正常solution零警告／错误、六TRX559终态与1542sourceSHA见实施§23；真正HTTP原23038 exit0，232请求／1864断言；原Analyzer完整canonical及同声明sourceSHA、新／固定／旧PNG像素零差、新旧各普通1＋透明1报告／restart、三片各30完整解码帧及真实旧Worker export disabled通过；三Server退出0／26观察group身份gone。影片／AAC保有损边界，完整部署证据见实施§23。完整B3／B4／资源／字体／作者Web与全量／GUI／性能／长片／平台暂停边界保持。


## ADR-0214：B4-F只扩固定MP3／native FLAC独立分析（0.70.0，2026-10-14）

**状态：已接受并完成本批有限出口。** 按[B4计划§10](../plans/backend-b4-plan.md#b4-compressed-analysis-plan)，详细证据见[实施§24](../history/backend-extraction-progress.md#b4-compressed-analysis-0700)。

旧WAV来源／精确duration／公开wire省略与账本兼容保持，私有grant尾字段只承诺默认WAV语义兼容，新fixed-compressed-audio-analysis-v1只接受明确MP3 LayerIII／native FLAC AudioProfile。压缩SourceDurationUs未知null省略，无0／probe估值；完成样本／duration以原48k实际解码为准。public nullable属性显式JsonIgnoreWhenNull，旧WAV缺profile只能按旧来源解释WAV，新compressed缺／错拒绝；Hosting/private AudioProfile默认WAV，demuxer中央唯一派生。engine＋三个audio／两个source真实probe禁用缺／错／旧0.69 Worker，configuration不等任意解码证明。

BCL只轻量候选bytes识别，不造完整decoder；Worker强格式、file/pipe、实际codec/channels/rate门禁与-xerror／err_detect explode严格解码，partial不发布。合法帧截短仍可能有效，不承诺查全删尾。原算法／sourceHash／PCM hash／semanticRevision／canonical完整精度／预算／任务／明确apply及保存重开保持；恢复新旧固定metadata allowlist。Export／VideoExporter／StaticExportTask不改，PCM16之外带音轨导出另批，不新增项目／schema／SDK。

本批严格分析入口不能推定修复旧create/open autodetect owned ffconcat引用卫生缺口；自有同目录nativeargv证据、未HTTP/跨client/任意路径的有限边界与后续优先修复见实施§24.4。完整B3/B4及原暂停验证范围保持。正常solution零警告错误、统一四TRX551／551；真正HTTP原37041退出0，152请求／1255断言、四canonical（仅3新增压缩）43,502B／404points／386,496samples byteexact，三采用保存重开／PNG零差／正式1＋1／strict MP3取消／旧Worker禁用和旧新恢复通过，三Serverexit0／32观察身份gone；原两工具失败、单probe两修／不可变551与权威source部署SHA分列实施§24，不混作者过程数，不称新增音轨导出。


## ADR-0215：B4-G收紧Backend音频准备外部引用并明确候选所有权（0.70.1，2026-10-14）

**状态：已接受并完成本批有限出口。** 正常solution零警告错误／统一三TRX557／557；真正HTTP原69113 exit0、131请求／936断言及最终1391证据文件SHA核验通过，修前30／238独立不并。 依据[B4计划§11](../plans/backend-b4-plan.md#b4-self-contained-preparation-plan)，证据见[实施§25](../history/backend-extraction-progress.md#b4-self-contained-preparation-0701)。两个Backend默认入口共CreateForSelfContainedFiles，App／Media原default不变；六demuxer家族＋typed强格式／file,pipe及MOVdref禁用，known/plausibleBMFF preprobe结构拒外部dref／rmra／rdrf，actualMOV fallbackprobe后／所有decode前强scan。非标flags1带payload拒、有限depth／box／entry预算，不宣传所有QuickTime/polyglot支持。

前后actualSHA／size／fullpath／祖先no-link及fallible readlease释放先publish，retention/published resourcepin沿真实所有者，不是OS immutable／ABA检测。open有AudioMedia才加identity门禁、缺metadata不补写且仍先受Core格式／版本／闭包约束，current-version合法无音频实跑保持；不承诺任意旧path-only包兼容，声明源丢失不silent无audio。发布前失败无candidate session半发布，register／initthrow候选及writeleasecleanup保primary/OCE；membership后event／post异常保真实已发布成功session，不把任一HTTP错误当无session／无job事件。Core准确版本门禁保持，PATCH不新schema／SDK／route／profile，分析／export不扩。

old0.70真正HTTP30／238及manifesthash固定／duration／sentinelFD变化单列，不并新accept；只能报告owned sameclient同目录，create sentinelSHA事后generator重建不称事前捕获。修后真正HTTP六家族兼容、ffconcat拒绝／directHostnative cancel及同handle重试已终态通过；MOV外部引用拒绝属于Media作者回归／native实验，不是HTTP负例，不能以Worker groups替代Host native；原557无scope扩张。Worker准备迁移／deadline、完整B3/B4及原暂停矩阵仍独立。

## ADR-0216：B4-H固定压缩音轨按完整源采样帧验证并保旧执行合同（0.71.0，2026-10-14）

- **状态**：已实施，固定MP3 LayerIII／native FLAC带音轨导出的有限出口已完成；完整B3／B4保持未完成。
- **来源**：[B4计划§12](../plans/backend-b4-plan.md#b4-compressed-export-plan)、[实施§26](../history/backend-extraction-progress.md#b4-compressed-export-0710)；当前HTTP只维护于[统一API](../../backend-api.md)。

**决策：** 旧WAV／纯视频保`static-export-pcmwav-v1`，固定MP3／native FLAC采用`static-export-compressed-audio-v1`；真实Worker严格探测EngineIdentity、双source／三audio／双execution profiles，旧0.70.1 Workerdisabled。尾部nullable profile／marker省略null保旧wire／inputSHA／报告；新压缩显式profile，恢复严格旧新配对，不声称辨识协调改写成合法legacy的任意篡改。

Worker强格式file,pipe完整strict decode至EOF、exit0，64KiB流只计源rate每声道sample frames，≤rate×300；coverage用整数交叉式，少一帧拒绝，源报告时长真实向下取整。不得用probe／header／48k分析估计代替；300秒只属压缩export，不套WAV或通用prepare。完整解码／前后actual identity先于Runtime和candidate，普通MP4固定AAC、透明MOV PCM16，trusted nullable encoding override默认null保原Media／Export语义，strict路径不用-shortest；输出50ms容差不放宽源足够性。

复用原唯一任务／账本／调度器／资源pin与租约、120秒deadline／2秒grace、双层发布和native私有组排水。重开工程分析采用以session构造时捕获的PreparedAudioSourcePath核当前canonical绝对路径，再核原asset／SHA／size与实际resource／lease；不要求Core闭包路径等于导入副本，不放宽任意替代路径或旧job，不修改sourceSHA／OpenCore。

**验证与版本：** MINOR0.71.0，格式／配置／包profile／SDK／依赖／项目保持。原修前716／716与修后新540／540＋未改原186／186明确分开；修后正常solution零警告错误，真正HTTP385请求3560断言、28whole终态、六影片各90完整帧与Runtimeoracle、透明144144PCMframes exact及新旧恢复通过；纯视频另90帧／无音轨，无独立内容oracle。独立三source审查及原HTTP／media／PCM审查、1568／1308不同source集合、1444当前deploy／434旧Worker及942evidence hashes、3Servers／38观察private成员gone见实施§26。两次失败保持原记录，不并成功计数；完整B3／B4、其他音频／脚本资源字体／作者Web和原暂停矩阵不恢复。

## ADR-0217：B4-I以独立Worker隔离Backend媒体准备并传输完整原生波形（0.72.0，2026-10-15）

**状态：合同已接受，本批有限出口已完成，完整B3／B4仍未完成。** 来源为[B4计划§13](../plans/backend-b4-plan.md#b4-managed-audio-preparation-plan)，完整实际证据与历史失败见[实施§27](../history/backend-extraction-progress.md#b4-managed-audio-preparation-0720)，HTTP仅维护于[唯一API](../../architecture/backend-api.md)。主体`6a7140d`后切到0.72.0，晚期open错误wire兼容回归由`1c70fae`最小修复；repair2统一775／775与不放宽断言的严格真正HTTP699请求／4239断言均通过。

Server可信组合显式选择Worker-only prepare，实际能力握手必须匹配EngineIdentity、`self-contained-native-waveform-v1`、六families、完整预算与native工具／private-group／subreaper能力；缺失、重复、未知、错配或失败明确disabled，不回退Host native。App保原`AudioWaveformLoader.CreateDefault()`，未配置managed准备的进程内Hosting保Backend自包含默认`CreateForSelfContainedFiles()`。create继续同步201及原client receipt，open／recover仍用原jobs／ledger／candidate／目录租约，不新增route、公开job、schema、store或项目。

六家族wav／mp3／flac／aac／mov／ogg沿原自包含probe／typed decode／MOV外部引用门禁。Info的DurationUs／SampleRate／Channels／CodecName／ContainerName五字段、源采样率与完整解码sampleCount不改；传输64samples base桶的原min／max float32 bits及尾桶，Host复用原pairwise merge还原完整pyramid，不合成PCM再聚合。下一decoded block前核500000桶容量，超限稳定失败、不截断；4MiB另作协议payload边界，合法结果先受500000桶约4004160字节上限约束，不宣称真实HTTP独立触发payload-budget失败；该预算独立于snapshot／analysis，不套48kHz分析重采样、300秒压缩export或mono／stereo／192kHz上限。

准备许可从入队计deadline，默认60秒、配置1～120，2秒grace；全局／每client running1／1、queued4／4。create基础字段先验证再预约Worker许可，保原invalidRequest优先级；停止后拒绝新准备及重配，取消已拥有操作并等待真实private-group/native排水。timeout使用稳定执行失败，用户取消保OCE／cancel；授权固定path／size／SHA及read lease／candidate释放先于发布，Worker前后actual源变化属于`taskExecutionFailed`／`WorkerAudioPreparationSourceChanged`完整性执行失败，Host原`VerifyFixedAudioSource`仍为原`taskExecutionFailed`且不新增failureCode，普通输入拒绝按入口／种类保旧：Worker InputRejected转sanitized IOException，create保422 `audioPreparationFailed`／`field:AudioPath`，open／recover保`projectLoadFailed`且无failureCode；NoAudioStream仍null，create保`noAudioStream`，open／recover保原`audioPreparationFailed`，均不额外添failureCode。仅open／recover的generic fallback不因mapper新添通用`TaskExecutionFailed`，其他job类别保原逻辑，typed Worker执行失败仍保稳定failureCode。nullable failureCode在null时省略，保旧错误wire。

**验证与保留范围：** `1c70fae / 0.72.0` repair2原八commands exit0、正常solution零警告错误，七TRX775／775（555／70／44／80／2／11／13），1602source／5849 actual bin无漂移，465实际观察身份gone，manifest SHA`c4304684c49cadb4f88a22150a3d0de5332c98e1f70846be57b3cb4daed09c55`。严格真正HTTP原session38819 exit0，699请求／4239断言，58whole终态／六actual-marker恢复／七controls通过，四Servers exit0／172观察private成员（52groups）gone；publish21与握手13另计。1342HTTP输入／1448部署文件／1586HTTP证据SHA与独立审查已核。HTTP只证明公开duration／state，完整Info／sampleCount／float bits／pyramid及协议预算由独立实际Worker／Media／Hosting回归证明；HTTP直接负例为owned同client ffconcat，不扩称任意或跨client路径隔离。repair1 753、final1失败137／987、原测试与CA1869零执行／runner快照缺口和定向修复变异各自保留，不并旧B4-H716／540／186及385／3560。MINOR0.72保格式28／16／6、配置1、包profile2、SDK／固定依赖／程序集；完整B3／B4及原全量／性能／GUI／长片／人工平台暂停范围保持。

<a id="adr-0218"></a>

## ADR-0218：B3-E固定效果包源 codec 与有限同进程出口（0.73.0，2026-10-15）

**状态：已接受并完成本批有限出口；完整 B3／B4 仍未完成。** 本决定依据 [B3 接续计划§8](../plans/backend-b3-completion-plan.md#b3-package-source-codec-plan)。仅在 `Backend.Rendering` 增加 `PackageProjectSnapshotCodec` 与 `package-lyrics-scene-v1`；不加入 Contracts 运行 allowlist，不新增公共 Worker/HTTP 能力、route、job、schema、SDK、store 或权限，旧 Static SelectProfile 保持原能力。

Codec 必须严格覆盖 `ProjectSnapshot` 15 个可写源属性，保留完整包 manifest/files bytes、顺序、SHA/长度、PackageVersion/SDK/profile、预算与 `PackageMaterial`，直接还原效果实例全部源字段、参数原规范 JSON 及数字精度、绑定/读取范围、seed、执行顺序和五项 `ProjectEffectBudgets`；完整 `AudioAnalysis` 联合状态与 source/PCM hash、sampleCount、points/hops、revision，以及由原 `ProjectEffectInputs` 算法产生的固定源 `EffectInputsFingerprint`（不是 snapshot wire bytes SHA）均须验证。4MiB wire、总 200000 个源值（含 JSON 深层值）、JSON `MaxDepth=64` 与提前图深度 60 统一计数；非 canonical manifest、manifest 非当前 `PackageProfile`、规范 manifest bytes 不 byte-equal、重复/缺失/额外文件、路径越界、hash 不符、未知/重复/缺失字段、错版本/enum、非有限数和非法领域值一律拒绝，不截断、不补默认、不做 codec IO。非空 `PresetApplications`、图片或视频 `Background` 及其非空媒体引用/外部引用明确拒绝；`AudioMedia` 源声明不因该门禁被拒绝。

同进程原 Runtime 的普通／透明帧、前进／回退 seek／再次前进、真实效果像素贡献和完整 round-trip 已通过；固定字体／包夹具不构成跨机器、Worker、HTTP、脚本失控、图片／视频闭包或完整 B3/B4 证据。权威 manifest `f1351ce60eb4e8a5f199a6b621d8d373a813dca5db4308701f0de1618fc60848` 对应五 scope、824／824、1609 source、5849 binary、362 identities；root 独立 `root-final-audit.json` SHA `0bba0794b05e820d7d8dff591d52cfc271e815248a214452517284867b179741` 重核 7484 唯一文件零 hash mismatch。预算顺序证据是源审查而非 allocation telemetry；作者过程、变异和原失败独立保留，不并入 824。包 Worker／隔离是后续独立切片，完整 B3/B4 及原暂停范围保持未完成。

<a id="adr-0219"></a>

## ADR-0219：B3-F以私有 package Worker 会话和实际进程组排水隔离效果包（0.74.0，2026-10-15）

**状态：已接受并完成本批有限出口；产品`1524625`已提交推送，完整B3／B4仍未完成。** 本决定接续 [B3-E 计划§8](../plans/backend-b3-completion-plan.md#b3-package-source-codec-plan) 与 [B3-F 计划§9](../plans/backend-b3-completion-plan.md#b3-package-worker-isolation-plan)。

仅新增显式 `WorkerPackageRenderFrameSessionFactory` 与私有 `PackageRenderWorkerContract` capability/open/render/close/results；旧 `hello` 不改，公开HTTP运行allowlist、旧静态SelectProfile、HTTP route／task／recovery、项目schema与保存历史不扩。open 前能力握手必须实际匹配 EngineIdentity、codecProfile、PackageVersion、SDK、shader 与完整 compiler references（含 TokenId）的 identity、非空及可构造性；Host expected refs 以 Worker 部署目录 refs bytes 加 Host 自身 TokenId assembly actual hash 计算核对；`ScriptCompiler.ReferenceIdentity`／`ReferenceCount` 仅暴露身份／数量、不改算法。capability 只报告实际 read／compiler 构造成功；非空不是完整 SDK 参考集签名证明或 immutable 保证。

复用 B3-E 完整包 bytes 与 source 合同，净 snapshot wire 上限为 4MiB−24B（既有 24B header）。open 固定一次 source；逐帧只传 session handle、sequence、time、quality、target、transparent，sessionId 即固定输入生命周期，没有独立 generation 字段；结果携带完整 input/output/session/source hash、帧元数据与 RuntimeDiagnosticSnapshot。Program reader 与同步 script execution 分离；`ImageEffectDiagnostics`／`MaterialDiagnostics` 结果非空返回 `WorkerPackageDiagnosticsUnsupported`，compiler failure 返回 `WorkerPackageSourceDiagnosticsUnsupported`，完整 source diagnostics DTO 后续另批。

真实 runaway init/evaluate marker 后必须验证 init/evaluate × cancel/deadline/Dispose（停止）六种组合的强杀、private group 与 Host subreaper 实际排水、Host 新建健康会话和新 Worker 恢复；deadline 为 `WorkerPackageTimedOut`，取消保 OCE，unrelated 进程不受影响。此有限出口不开放 HTTP、不覆盖完整预设／媒体／字体闭包、作者 Web 或完整 B3/B4。统一八commands全部exit0，solution零警告错误、七TRX971／971（155／639／70／31／2／44／30），1618source／5849binary无漂移、448观察身份gone；manifest SHA`3c978536ed679938f9bb92368573da5a1d9ffa9ce0950c0df7116c6f921e28f8`。原HEAD`7c31865`保执行基线，产品源码与freeze一致；完整证据见[实施§29](../history/backend-extraction-progress.md#b3-package-worker-isolation-0740)，原B3-E证据不重写。

统一验证已覆盖 Scripting.Tests 精确 `CompilerTests|EffectPackageCompilationTests|CompiledEffectPackageTests`，Runtime.Hosting 定向保持。真正 self-contained Worker 部署、实际脚本运行与 refs 缺失探测是独立证据，不并统一测试计数。

capability／open／render／close 必须绑定同一已经验证的实际 Worker 进程身份；进程变化即拒绝，不能沿用旧 capability 发送新包。同一 Worker 的 static／package Runtime 会话互斥，双向 open 都须稳定拒绝而保留已打开会话。隔离声明只限本机 Linux 实际观察的 private group 与常规后代，不构成任意恶意脚本的 OS sandbox 或跨平台保证。

合作取消 ack 仅在本次取消或 caller cancellation 真实发生并且请求关联完整时接受，不要求取消回执携带成功帧的尺寸／像素／诊断；伪取消或含输出／错误字段拒绝。caller cancel 保 OCE，内部 deadline 独赢才映射 `WorkerPackageTimedOut`。独立已发布 Worker 验证的 publish/test 同用 Release、linux-x64、SelfContained=true，实际 refs identity 不放宽，部署证据计数不并统一 scope。

ADR-0219本批终态：self-contained发布434文件／零警告错误，raw IPC健康及仅缺System.Runtime.dll副本真实拒绝，匹配Release／linux-x64／SelfContained Host的实际发布Runtime20／20均独立green；actual refs168／identity`54065d5730fb15dac384bcd350321f964303aed9b75e9ce53daa378753e5d089`。编译失败与非空图片／材质诊断已实际验证明确拒绝，完整source DTO仍未传输。独立审查9482 SHA引用／18TRX／1058唯一观察身份gone、零mismatch；作者合同84、mode2、早期82及失败轮次保持独立，不并971。各manifest SHA及范围见实施§29。本机Linux实际组隔离不等于任意恶意脚本OS sandbox，公共HTTP包能力、完整B3／B4及原暂停矩阵保持独立。


<a id="adr-0220"></a>

## ADR-0220：B3-G内部package三类完整诊断与固定catalog身份恢复（0.75.0，2026-10-15）

**状态：已接受并完成本批有限出口，完整B3／B4仍未完成。** 依据[B3计划§10](../plans/backend-b3-completion-plan.md#b3-package-diagnostics-plan)，内部package会话显式协商独立`PackageDiagnosticsVersion=1`；旧Worker、缺字段或错诊断版本明确拒绝，不回退成不传完整诊断的旧能力。公开HTTP包profile／route／正式任务、预设／外部媒体字体闭包、完整B3／B4与原暂停矩阵保持独立。

完整source诊断保PackageId、Revision、InstanceId、ExportId、Code、Message、IsError、Path、Line、Column，0行列合法、负值拒绝，nullable实例／导出成对。实际编译失败以`WorkerPackageCompilationFailed`及独立`SourceDiagnostics`返回，不伪装为Runtime occurrence。图效果保实例／artifact／graph资源身份／input／code；材质保完整material身份／input／code及Script或Source owner判别联合，Script的实例／artifact／必非null package身份与Source的path／region／slot／source material／规范参数不得混用。Host依固定snapshot包catalog、启用实例及SourceMaterialCatalog验证归属并恢复SDK资源与owner，不信任远端任意句柄。controller ArtifactHash只核64hex格式及固定包／实例关联；直接图完整revision须等于固定实例源。不在Host独立重编译，不证明compiler代次DLL bytes匹配。

三类分别≤256条／64KiB实际UTF8序列化数组，合计≤512条／128KiB完整DTO实际序列化字节（含结构）；任一超限返回`WorkerPackageDiagnosticsBudgetExceeded`，不截断、不附超预算容器；新增诊断DTO及capability JSON的未知／重复／缺失字段或非法来源返回`WorkerPackageDiagnosticsInvalid`。这独立于原RuntimeDiagnosticSnapshot及既有IPC预算。完整Runtime occurrence身份、Error禁pixels、同实际Process／原pipe能力绑定、static／package互斥、取消／deadline／Dispose实际组排水保持。

有限验证按source impact审定：正常solution、Rendering／Hosting／Worker全项目、Runtime package／旧static／dependency定向及linux-x64 self-contained Server，六commands exit0、solution零警告错误、五TRX1001／1001（155／699／70／33／44），1623source／5849binary无漂移、476观察身份gone。独立发布434文件、raw健康／缺refs拒绝、实际发布Runtime22／22及真实0.74缺诊断版本Host精确WorkerPackageCapabilitiesInvalid拒绝均green，不并1001；legacy-final1 CA1869构建失败与作者历史另列。完整manifest／SHA见[实施§30](../history/backend-extraction-progress.md#b3-package-diagnostics-0750)，旧B3-F验收不重写。本机Linux组隔离仍不构成任意恶意脚本OS sandbox或跨平台保证；MINOR0.75.0保schema、SDK、包profile、固定依赖与项目边界。公共package HTTP仍需后续独立合同与验收，详见计划§10。


<a id="adr-0221"></a>

## ADR-0221：G1以配额按需两阶段claim回收过期空闲客户端，并固定W0后端启动基础（0.75.1，2026-10-15）

**状态：合同已接受，本批有限出口已完成。** 用户已明确恢复本批G1与稳定headless启动基础；完整Web、B3／B4和其余暂停范围不随本批恢复。接口事实见[统一API§3](../../architecture/backend-api.md#3-客户端登记心跳与状态)，实际证据见[实施§31](../history/backend-extraction-progress.md#web-g1-backend-0751)。

- 只在HTTP异步新登记达到客户端配额时，才对已过lease且operation pin为0的身份原子claim；ClientRegistry锁外检查Hosting与清理，再完成移除／解除claim。同步`Register`兼容入口仍不自动回收。默认maxClients16、access120秒／lease300秒保持，未新增注销路由。
- 有效lease、未关闭工程、运行／排队任务、未到保留期限终态job、有效或在途回执、认证请求全生命周期（含SSE／流式读取）及真实资源引用均保护。不确定或检查失败保留身份，故过期不保证立即释放配额；不能自动discard工程。claim期间resume／heartbeat／认证临时401，若保护／清理失败则恢复可resume；实际移除后旧access与resume永久失效，使用bootstrap重新登记。
- 回收按现有预览策略排水已过租约预览，拒绝晚到通知复生事件状态；只清client-local内存索引、过期job、事件、回执与目录句柄。持久工程、ledger、报告与上传文件不删，引用资源保护，未引用资源由原TTL清理。
- `tools/backend-web-host/host.py`提供匹配self-contained Release/Linux-x64 Server＋完整Worker／script-refs发布、输入与部署SHA、稳定profile、显式Origin、固定native工具、随机loopback、私有0700 run／0600 bootstrap，以及只对自有Server的SIGINT／SIGTERM转发和等待。端口ready不替代实际audioPreparation／PNG健康；无Avalonia、物理声卡或共享ASP.NET runtime前置。
- 本批PATCH不变更HTTP协议、五命令、project/schema／SDK／固定依赖或公共静态profile范围。外部Web项目仍须实现bootstrap桥接、协议客户端、SSE重同步与真实浏览器W0验收；后续进入W1命令＋读模型，公共package HTTP与完整媒体字体闭包另批。

本批正常solution零警告错误，Hosting711＋self-contained Server46＝757／757、1375输入SHA无漂移；默认TTL HTTP79／491与独立2秒lease HTTP70／494分别归档，不合并为单元测试数。两正常Server退出与12观察身份gone、真实PNG解码、64登记与旧凭证拒绝、launcher故障路径均核对，详见实施§31。


<a id="adr-0222"></a>

## ADR-0222：Web编辑查询按精确修订投影，保留原值与有界窗口（0.76.0，2026-10-16）

**状态：已接受，S1有限出口已完成。** 用户已授权继续编辑能力，候选依据[接续方案](../plans/backend-web-editing-plan.md)，已实现合同见[行为§6.4](../../architecture/backend-api.md#64-精确修订的编辑读模型0760)，证据见[实施§32](../history/backend-extraction-progress.md#web-s1-read-models-0760)。

独立新增来源、完整正文/注音/时间窗口和标量设置三个GET，不修改旧最小歌词wire。查询持有命令同一Writer，必须携带精确expectedRevision；页间写入、undo/redo后旧修订仍409，不能拼接不同修订。保稳定身份、权威正文、原时间/状态、FollowPrimary关联与注音全部元数据，不合成独立时间或发音计时。新微秒使用Int64字符串，旧composition/summary number保持。

每页最多100行/来源，完整UTF-8 JSON最多1MiB、来源/行/token/ruby最多20000项；超过整体413，单行不截断，无历史/dirty/redo/事件副作用。兼容新增为产品MINOR0.76.0、合同MINOR1.1.0，HTTP主版本及文件schema/SDK/依赖不变。写命令、波形、样式/场景完整模型与公共package均另批，不能由读模型完成推定可用。


<a id="adr-0223"></a>

## ADR-0223：具名时间命令按type分型，固定HTTP动作与回执边界（0.77.0，2026-10-16）

**状态：已接受，G3/S2有限出口已完成。** 用户按计划授权继续编辑，Claude接受六命令候选；实际合同见[行为§6.5](../../architecture/backend-api.md#65-时间动作与严格命令分型0770)，证据见[实施§33](../history/backend-extraction-progress.md#web-s2-timing-0770)。

原五wire兼容，新命令独立DTO由converter按type严格解析，未知type仍明确CommandNotSupported；继承反射与显式字段表保证机器合同不吞派生字段。HTTP确认动作前后断桌面合并，不跨请求悬挂组；no-op零修订/事件/历史且保redo。batch准入固定一份规范化payload给指纹与执行共用，任一字段改变不能误取原回执。

六动作复用既有打点/清除/微调/平移/批量业务语义；保原夹紧/估算状态与batch无媒体上界，不自动扩行/重排batch。打点返回可选timingResult；新领域failureCode稳定且field为输入路径，旧错误不改。完整changeSet默认JSON超过16KiB时保区域、所有目标ID清空并要求完整刷新，保护64KiB留存回执；不截断为不完整ID列表。产品0.77.0／合同1.2.0／协议1，schema/SDK/依赖不变，正文/结构/设置写入与波形另批。


## ADR-0224：内容编辑先纯准备再按完整读模型预算一次提交（0.78.0，2026-10-16）

**状态：已接受，S3a有限出口完成。** 用户按计划授权补齐后端编辑。十一严格type复用共享Application的正文/注音/行/标量设置规则；原桌面入口调用同一Prepare/CommitPrepared，不复制Unicode、ID、时间或展示清理规则。Hosting在固定candidate与实际下一修订上验证S1完整单行预算，失败不提交、不回滚、不重新生成ID；允许删除旧超大行。新token创建前计数和必要字节先验只服务HTTP资源预算，不能把旧大合并元素字符数当作token数。

反馈只提供原结果中的选择/身份/位置/统计，changeSet保守完整刷新。正文身份仅公共前后缀内完整元素保留；注音不增加独立计时。输出时长null为尚未确定、导出前阻断，采用末端均显式命令。一次HTTP确认一次历史、no-op保redo。产品0.78.0／合同1.3.0／协议1；407相关回归、3116真实HTTP与边界见[实施§34](../history/backend-extraction-progress.md#web-s3a-content-0780)。来源、元素结构、再次导入、波形及完整B3/B4另批。


## ADR-0225：来源管理保原业务，以完整读投影与回执预算先验提交（0.79.0，2026-10-16）

**状态：已接受，S3b有限出口完成。** 四严格命令复用共享来源Prepare/Commit，保Primary身份/角色、原合法时间策略、名字/语言/展示及悬空关联语义；不把候选过严文案变成新业务限制。单来源/单行完整S1投影及必要选择的完整回执在唯一历史前校验，允许删除旧超大来源；不按默认组合页大小限制整个工程。新来源ID沿Core非空白规则，旧时间目标128上限不变。反馈仅Add/Remove真变化提供实际选择。产品0.79.0／合同1.4.0／协议1，345相关回归与3081真实HTTP及限制见[实施§35](../history/backend-extraction-progress.md#web-s3b-branches-0790)。


## ADR-0226：元素结构复用唯一拆分规则，固定候选后原子预算提交（0.80.0，2026-10-16）

**状态：已接受，S3c有限出口完成。** 五type复用原Application/Core时间、身份、样式与注音规则；流式Unicode和新段/ID观察器只提供宿主资源门禁，默认桌面API不受HTTP预算。手工20000段/完整S1单行/整来源改变行累计16MiB在历史前检查，失败全状态保持，准入segments副本固定回执与执行。真实tokenResult不臆造映射或merge/reorder统计；shape先于领域查找。产品0.80.0／合同1.5.0／协议1；518统一相关回归、7422独立旧新片段对照及3367真实HTTP边界见[实施§36](../history/backend-extraction-progress.md#web-s3c-tokens-0800)。再次导入/波形与完整B3/B4另批。


## ADR-0227：受限准备探测与解码明确使用同一首音轨（0.80.1，2026-10-16）

**状态：已接受，独立修复完成。** self-contained probe的源采样率/通道Info必须描述实际解码的音轨；forcedInputFormat decode固定-map0:a:0，保原mono downmix、不重采样。实际双轨Worker修前1秒变6秒，修后完整样本与全部峰值等独立首轨oracle；产品0.80.1／合同PATCH1.5.1，无新route/DTO或Worker profile。158相关回归、主目录93HTTP与证据边界见[实施§37](../history/backend-extraction-progress.md#first-audio-stream-0801)。不承诺浏览器与FFmpeg压缩音频逐样本时间基一致；G11仍独立。


## ADR-0228：导入归原回执owner，固定快照逐最终行预算再提交（0.81.0，2026-10-16）

**状态：已接受，S3d有限出口完成。** 两命令复用原八格式/来源业务，严格目标union，完整报告允许Error与合法行，不截断。Writer外准备归receipt/client pin/停机排水，易失败清理和逻辑工作/逐最终行/累计/完整回执预算先于唯一历史，重入精确修订拒绝旧结果。保public/protected签名，LRC显式编码单列修复。产品0.81.0／合同1.6.0／协议1；602回归、576独立默认oracle、5089HTTP与边界见[实施§38](../history/backend-extraction-progress.md#web-s3d-import-0810)。


## ADR-0229：波形复用一次准备，以session授权保留峰值并窗口读取（0.82.0，2026-10-16）

**状态：已接受，G11有限出口完成。** 固定源验证后绑定完整峰值身份，Host64MiB只计已保留flat payload并同源去重；不足不阻create/open/recover、不保旁路或重decode，明确不可用。session绑定授权/close最后ref释放，有界窗口独立≤64KiB，歌词修订不改音频身份；GET/HEAD/ETag/认证/Range按行为§6.10执行。不新增PCM或承诺浏览器压缩音频逐样本一致。产品0.82.0／合同1.7.0／协议1，382相关回归及正式340HTTP/86完整层oracle与边界见[实施§39](../history/backend-extraction-progress.md#web-g11-waveform-0820)。


## ADR-0230：静态样式以canonical属性局部编辑，保留为模板流程之外的底层能力（0.83.0，2026-10-16）

**状态：已接受，有限出口完成。** 用户确认现有前端编辑／撤销重做接口无误，并在三工作区／模板先行原则核对后明确选择直接接口“作为底层能力一并交付，再补模板库与分配”。这不要求前端按具体歌词目标制作模板；样式作者与显式分配仍独立。

工程／来源／行／元素用稳定ID、24个typed canonical属性与明确restore命令；缺省继承、显式null关闭／系统字体、真实值不能压平。Material只一个真值，新写solid/gradient明确替换槽，未选包身份／完整参数保持；不用兼容Color getter伪造颜色。共享纯Prepare→前置新值／完整读投影／实际回执预算→一次Commit，不清理未选场景、不改Core格式、no-op保redo和旧预设标识。GET只解释staticHierarchy并提示固定预设存在，不声称实际帧最终外观。

产品0.83.0／合同1.8.0／协议1；750相关回归、38779正式HTTP及失败独立归档见[实施§40](../history/backend-extraction-progress.md#web-static-style-0830)。模板库与分配是下批，公共包执行、完整作者／场景及原暂停矩阵不随本批恢复。


## ADR-0231：模板库作为profile级独立资源，先交静态完整定义与高级闭包操作（0.84.0，2026-10-16）

**状态：已接受，模板库有限出口完成；工程显式分配另批。** 用户确认现有前端编辑、撤销／重做和静态样式底层接口无误，并选择先补模板库，再补工程内显式分配。模板定义不持有工程目标、播放头或工程历史；模板库操作不改变工程 revision、dirty、undo、redo 或事件。

内置目录只读，个人目录按 profile 共享；发现只返回两个逻辑库，不让损坏个人入口遮蔽内置库。索引修订使用真实 canonical JSON 的小写64位 SHA-256，缺失使用 `missing`，不使用数组位置、修改时间或工程 Int64。静态 detail 提供完整21属性选择和完整 canonical style；包含配方、包或未选包材质的高级模板只返回能力摘要与显式 `advancedUnsupported`，不能降级成静态定义。

个人库开放 create／replace／copy／rename／delete。新建与整体替换只接受完整静态定义；copy 固定源快照后对个人库 CAS，不承诺跨库分布式原子性；rename／delete 保留高级模板的参数与资源闭包，删除不清理历史包文件。所有ID生成、存在性、CAS、响应预算和发布在同一回执 owner 内完成，成功回执可在库继续变化后重放，不重复生成身份。

新 command、名称／字体、回退链和渐变色标沿独立模板库合同受限，旧数据读取与服务端 copy／rename／delete不套新输入长度限制。模板库 HTTP、合同和行为正文一起冻结为产品0.84.0／合同1.9.0；工程显式分配及实际预设渲染另行验收，不把 `staticHierarchy` 或摘要称为最终帧外观。具体行为见[统一API§13](../../architecture/backend-api.md#13-独立模板库0840)、[接续方案](../plans/backend-template-library-assignment-plan.md)和[实施§41](../history/backend-extraction-progress.md#web-template-library-0840)。

## ADR-0232：工程显式预设分配固定模板快照并按一次工程历史提交（0.85.0，2026-10-16）

**状态：已接受，有限后端出口完成；完整预览／验证／导出及前端浏览器闭环仍独立。** 用户授权在模板库之后继续补工程分配。四种工程命令为 `presetApplications.apply`、`presetApplications.update`、`presetApplications.setEnabled` 和 `presetApplications.remove`，复用工程精确修订、唯一写者、回执、事件、撤销／重做与 no-op 保 redo 语义。

`apply`／`update` 只接受 `builtin` 或 `personal` 库、模板身份及精确 `templateRevision`。Hosting 在工程 Writer 外取得完整静态模板快照，Writer 内只执行纯 Prepare→Commit；模板库随后改名、替换或删除不改变已经分配的工程。`apply` 目标严格分为 project、branch、line、token 四种 union，批量1..1000且不重复；`branchInputs`只作为字符串映射输入，静态模板不借此伪造高级参数。

`update` 按 applicationId 保留应用身份、实例身份／种子和启用状态；变更模板身份须先移除再应用。`setEnabled`只修改启用状态，`remove`接受1..1000个身份并保留失效记录的可移除性。领域失败、目标不存在、模板快照缺失或提交修订冲突均整批拒绝，不产生部分工程变化；成功变化只产生一次修订和历史。

新增四项 capability 和合同1.10.0／产品0.85.0。静态 preset snapshot profile 已有独立 Rendering／Worker 定向回归，但公共 HTTP 预览、正式验证／导出像素与恢复必须在匹配 Server＋Worker 部署中单独验收；不能把分配记录、模板摘要或 `staticHierarchy` 称为最终帧外观。完整作者包、场景编排、B3/B4、Web W2及原暂停矩阵不因本批恢复。

<a id="adr-0233"></a>

## ADR-0233：注音片段拥有独立时间，TTML 用作有明确 profile 的交换格式

**状态：已接受，实施与验收进行中。** 用户明确要求保留现有歌词分支结构，为 ruby 增加时间轴并提供 TTML 导入导出；后续授权按照[实施计划](../plans/backend-ttml-ruby-timeline-plan.md)编码与验收。此 ADR 固定实施细节，不作为完成证明。

1. `LyricsDocument → LyricBranch → LyricLine → LyricToken` 保持权威；`RubyAnnotation.Segments` 保存稳定 ID、正文、绝对整数微秒半开区间及时间状态。保留磁盘/HTTP 的 `text` 拼接投影，读取不一致时保留原数据并报告错误。基础 token 时间和脚本 token 事件不变。
2. 合法片段时间允许空档、重叠以及超出 token/行区间；不自动夹紧或延长行的可见窗口。采样按各片段时间执行，显示仍遵守行/分支/场景策略。缺少时间的兼容片段沿原基础 token 高亮规则；不把行时间写成注音时间。
3. 旧 `setRubyText` 同文为 no-op；更改已有独立时间或多个片段的注音时明确拒绝 `RubySegmentsRequireExplicitEdit`。新的片段文字、结构和时间命令必须显式指定 ruby/segment，Prepare 后核对完整结果及回执预算，再一次历史提交；失败保留 revision、dirty、redo 和文件。
4. lyrics schema v17 只新增经过实现验证的 v16→v17 迁移；旧聚合文本生成一个稳定的 Missing 片段。移除仅改版本数字的 v8→v16 桥接；v1..v15、未来版本继续拒绝。工程 applicationVersion 的既有门禁保持，读取不就地改写文件。此项是本任务明确规划的有限迁移，不能恢复历史断代格式支持。
5. 标准 TTML 使用父容器与 `par/seq` 时间语义；AMLL 绝对时间约定必须由可识别 profile 明确选择，不能静默重解释通用 TTML。无法计算的时间基、agent、旁轨或样式进入报告；禁止静默误读、伪造精确时间或自动按歌手拆分分支。
6. `ttml.project` 须恢复歌词分支、身份、关联、时间状态和 ruby 参数；项目行/元素样式等未承载字段显式列损失。HTTP 省略 scope 或 `branch` 维持单来源语义，`document` 仅允许 `ttml.project` 且禁止 branchId 字段；全文档导出与完整往返分别需要合同及真实链路证据。再次导入多来源文档必须显式选择 `sourceBranchId`，分配新的 line/token/ruby/segment 身份并同步内部关联；跨省略来源的 PrimaryLineId 保留原值并报告不可解析，不清空、不猜配。中间实现的损失报告不能降低计划中 P2/P3 的最终门槛。
7. 聚合注音一次整形，片段高亮只改变裁切，不改变布局；cluster 边界、普通/透明 PNG、几何、static/package 快照及真实 Worker 必须同源核对。长注音 spacing/overhang solver 仍是计划明确独立的排版工作，不随时间轴宣称完成。
8. `ttml.amll` 用于选定来源的外部互换，必须由官方消费端解析实际输出验证。XML 保留微秒原值，不为消费端主动量化或伪造缺失时间；消费端按毫秒舍入、丢弃无时间 ruby 片段及以注音范围替代基础元素时间等可复现限制，分别计数并进入显式损失确认。XML 写出数量与消费端保留数量不能混称；项目 profile 不受这些消费端降级规则影响。

产品交付按新 MINOR 版本登记；HTTP 协议主版本、固定依赖与桌面仓库边界保持。证据分层登记于实施计划和实施记录，Core 单测、Hosting、HTTP、Worker/PNG 各自证明对应范围。


<a id="adr-0234"></a>

## ADR-0234：Ruby 水平间距采用时间无关的共享字簇平移求解

**状态：已接受，本批有限后端范围已完成。** 用户授权在TTML/ruby时间轴与综合验收之后继续执行下一步，本批按[spacing计划](../plans/backend-ruby-spacing-plan.md)完成有限排版改善；1330项相关回归、匹配发布HTTP179/2689与主验收证据见[实施§61](../history/backend-extraction-progress.md#ruby-spacing-0970)及[计划§7](../plans/backend-ruby-spacing-plan.md#7-实施结果)。

1. 对横排单调布局自动求解，无样式mode或持久化字段；行为与注音时间/progress无关。垂直墨迹严格相交且水平gap小于1布局像素时检查所有此前ruby，包括跨无注音token；相切/不相交不约束。
2. 完整正文及每组ruby聚合整形保持，安全token/grapheme/HarfBuzz cluster刚性组仅整体平移；保留glyph、自然advance、Start/Center/End和overhang。ruby锚点取原始token自然边界加组位移，避免gap进入前token End锚点导致无法解开。正文progress path使用最终位置，gap归前token无墨迹区间。
3. 共享结果同时供PNG、布局、line/token/family/ruby几何、progress path、脚本几何及捕获/覆盖绘制消费；禁止只改联合包围盒或只移动ruby。联合墨迹沿原maxWidthRatio统一缩放，不自动钳制用户变换，不按segment单独缩小或重排。
4. 无冲突保留原布局与既有legacy golden；历史无时间碰撞画面可预期改变。共享cluster组内无法安全消解、非单调/不精确映射与非法坐标使用内部保守回退和专用测试，不增加公共诊断契约、不声称完整RTL/竖排完成。
5. 产品0.97.0／合同1.28.0表达可见行为变化；无wire shape/schema/命令扩张，Server和Worker EngineIdentity须匹配。手算oracle、旧golden、真实Worker/HTTP分别证明对应层，完整公共B3/B4、B5/B6及浏览器/性能/平台范围保持独立。


<a id="adr-0235"></a>

## ADR-0235：直接效果包通过独立预览能力进入公共HTTP，完整诊断先于像素发布

**状态：已接受，P2-A有限HTTP验收完成（2026-10-19）；完整平台范围仍独立。** 用户授权继续下一步并通过Agent Mail向Claude同步；计划提交`b9f02af`已推送，按[实施计划](../plans/backend-public-package-preview-plan.md)推进，实际状态见[实施§62](../history/backend-extraction-progress.md#public-package-preview-0980)。本ADR固定有限合同，不把内部实现或合同schema当作真实HTTP验收。

1. 复用既有合法项目open及公共project预览路由，不新增任意源码/裸snapshot执行入口。预览专用选择器按完整包/实例/非默认预算/fingerprint/源包材质选`package-lyrics-scene-v1`；原三静态profile、B4正式任务/场景测量和模板/候选试看保持原门禁，不能把package塞入静态codec allowlist。非空presetApplications、非Color背景、外媒体与可搬移字体闭包继续拒绝，不剥字段后执行。
2. 当前HTTP组合根`Program → PreviewWorkerConfiguration.ResolveAsync`始终提供非null SourceProfiles，capture资源回调始终返回实现能力接口的ReferencedFactory；其Ensure在inner缺能力接口时明确返回PreviewUnavailable，因此真实HTTP包路径必经实际协商。可信进程内手工组合的null SourceProfiles保留旧兼容，并未新增显式null拒绝；核EngineIdentity、完整refs、profile/SDK/shader和diagnosticsVersion1。固定输入的`VerifiedProfile`只由可信组合根在协商和编码预算通过后设置，不是HTTP字段；Registry对project来源、标记和实际完整包闭包再次验证，不能仅信标记。源码/合同和测试须共同证明该准入。
3. 精确revision、snapshot、对应音频asset身份与快速resource reservation在同一Writer原子捕获；握手/编码/脚本在Writer外。retention与runtime leases分别持有捕获的固定asset，换输入/close/失败/取消完整归还；逐帧不重读工程/库，不重新编译固定包。
4. 三类公共诊断使用独立Source/ImageEffect/Material payload和明确origin，保完整source十字段（0行列合法）、图资源身份与script/source材质owner。无真实runtime发生不得伪造occurrence。compile failure以及runtime Error都保留实际报告和同期图/材质旁报；私有wire复用已有PackageDiagnostics字段，仅修复基础shape、包帧及proxy operation错误guard允许真实render HasError旁报，open编译失败仍只能source报告且禁止像素/geometry。
5. 公共完整数组最多768条/512KiB，runtime及各类包各256、三包类合计512；私有字节预算独立，分页128条/最多32页及实际更小配置。不截断，不把超限变空成功。投影/UTF8预算在Registry锁外，回锁重核仍当前后完整发布；新PNG必须经过全部诊断验证，source IsError/runtime Error或非法owner/预算禁止本次新帧，failed无latest。旧成功frame lease按原TTL保留。
6. 公共HTTP没有完整snapshot sourceSha字段，只证明返回的预览/来源修订/序列/PNG内容hash。完整源SHA、直接Runtime像素等价和真实包贡献依赖独立固定输入oracle与内部Worker证据；不得把内部hash对照写成HTTP响应字段证明。
7. 产品0.98.0、合同1.29.0、protocol1；路由80/方法89、工程命令91不变。原2648例逐值保持，附加可选payload不要求迁移旧报告。实际Worker缺refs、历史0.74原版与仅VersionPrefix重编变体分别记证据；canonical发布不受缺refs测试污染。历史原版因engine拒绝不能称诊断版本隔离证明。
8. Agent Mail消息persisted/signaled/acknowledged分别记录。当前#97/#98仅persisted未ack，不推断Claude已接入。有限P2-A不是完整公共B3/B4、B5/B6或浏览器/用户、性能/长片/GUI/平台验收；本机Linux进程组隔离不是任意恶意脚本OS sandbox。


### ADR-0235最终验收补录（2026-10-19）

工具`ff6fff1`以不可变final2部署（sourceCommit `6f4293d`、生产输入匹配）和Release Oracle完成单次真实HTTP：final6为1513请求／14090断言，59组PNG/像素、47诊断、6组失控、84个Worker/子进程身份排水、3656文件凭证扫描；evidence manifest SHA `bfb411ddabf94a5036d521710923fdb2e5167edecd98a60893cce6597099957f`。Debug artifactHash、项目会话配额、B4码预期等失败目录独立保留；历史current-version静态旧schema为SnapshotVersionMismatch。该ADR不扩大公共B3/B4、B5/B6、浏览器/用户或性能平台验收。


<a id="adr-0236"></a>

## ADR-0236：工程Image背景以独立固定内存来源进入受管预览（0.99.0／合同1.33.0，2026-10-23）

状态：已实施有限合同并完成匹配发布fresh HTTP有限矩阵；匹配self-contained Server/Worker发布源码 `d961895`、`sourceDirty=false`；fresh HTTP `artifacts/project-image-preview-0990-acceptance/http-final6/` 通过1175请求／9760断言。static/static-analysis/直接package普通与透明各4帧及回退seek、40组原Runtime完整PNG/像素比较、Initialize/Evaluate×cancel/deadline/close六组实际Worker/child控制与排水通过；Server exit0、105个观察身份（含83个Worker身份）全部退出，2752个证据文件凭证扫描通过。本地分层回归、Oracle预检及合同检查按[计划](../plans/backend-project-image-preview-plan.md)与[实施§77](../history/backend-extraction-progress.md#project-image-preview-0990)分别登记。

1. 保留公共project预览请求与工程ID/精确修订，不提供图片bytes、profile、路径或grant输入。新增独立 `project-image-preview-v1` 私有能力/open，runtime-info启动探测成功才公布，创建与同一实际Worker的open再次复核engine、基础来源、refs、诊断版本与预算。图片探测失败不移除原static/package能力；兼容singular sourceProfile不改值。
2. 只授权ProjectStore已加载的Image背景内容地址。在唯一Writer内固定snapshot、音频reservation和短期图片读引用，Writer外核所有路径无链接与完整size/SHA后持独立bytes。工程close等待短期引用排水，成功固定后不依赖原目录；无Host Skia/图片头读取或native decode，无新staging目录。
3. 严格envelope包裹原static/static-analysis/直接package基础codec，完整保留kind/fit/playback/Media、底色与效果fingerprint，使用逻辑内容地址恢复背景。只在包裹时Color替换并依原规则重算fingerprint，不放宽原codec或正式任务allowlist。图片与preset、video、外部font及任意主机路径仍拒绝。
4. Worker完整核同一bytes身份，使用每Runtime session独立的内存resolver/native bitmap接入原生产ProjectRenderSessionFactory/SkiaRenderPipeline。原图2MiB、单边4096、BGRA16MiB、完整envelope4194280字节（含base64与扣头）各自门禁；预算先于像素分配，不缩放或截断。损坏/超预算即失败，无替代黑帧；透明输出同样先完整解码核验。
5. open/render/close复用既有包会话、完整诊断与进程排水；保留ReferencedFactory音频租约。resolver创建在工厂所有权try内，无效果、编译/准备失败、取消及dispose均释放。generation/seq防迟到发布沿原协调器，failed无latest，旧frame lease依TTL。
6. 输入失败统一无路径逻辑字段：503 WorkerImagePreviewProfileMismatch/Unavailable，422 ProjectImageSourceUnavailable/Changed，413 SnapshotSizeExceeded；异步PackageImageDecodeFailed/PackageImageBudgetExceeded通过renderFailureCode，通用failure仍用原internalError。超时及包编译/Runtime Error保持既有WorkerPackage*语义。
7. 生产版本0.99.0、合同1.33.0、协议1；无新公共路由、命令或工程格式。schema/runtime-info/合成例与Backend及实际Runtime/Skia/Core背景真值共同冻结。短期变异红/恢复绿、同进程传输、真实Worker、Oracle预检、clean匹配发布HTTP和浏览器分别记证据；本ADR不表示图片validation/export/measurement、video/font跨任务闭包、preset+package、完整B3/B4/B5/B6或跨平台性能矩阵完成。


<a id="adr-0237"></a>

## ADR-0237：已保存工程Image背景进入独立正式验证Worker（0.100.0／合同1.34.0，2026-10-23）

状态：2026-10-24已完成匹配发布fresh HTTP有限切片。见[独立计划](../plans/backend-project-image-validation-plan.md)、[统一API§10.1](../../architecture/backend-api.md#project-image-validation-api)与[实施§78](../history/backend-extraction-progress.md#project-image-validation-01000)。

1. 复用validation-capabilities、validation-jobs、result、diagnostics、job取消/deadline/close及owner/recovery ACL，不新增公共图片输入DTO；客户端只交精确工程revision、帧计划和metadata。
2. 独立source profile为`project-image-validation-v1`，capability kind为`project-image-validation-capabilities-v1`，task kind为`task-project-image-validation-v1`／`KTI1`。envelope codec仍`project-image-preview-v1`，decoder必须是`memory-image-bgra-v1`；能力与实际任务核engine、恰三个基础profile、refs/package capability、diagnostics版本及预算。预览握手不代替验证握手，不回退原静态任务或Host native。
3. 完整任务输入4,194,272字节（4MiB−32），嵌套base snapshot4MiB／200000 source values，图片2MiB／单边4096／BGRA16MiB；不套preview的4,194,280字节envelope上限。报告保原256KiB／256 issues／512 occurrences门禁，不截断。
4. Host在第一次Writer固定snapshot、受管Image读lease与音频reference，Writer外核路径无链接、完整size/SHA及编码预算；第二次Writer重新核owner、注册session对象身份、revision和ClosePending，之后才reserve并登记job。读取/编码/预算/复核/配额/登记任何失败均归还未转移的lease/reference/reservation；接受后等实际Worker/native排水再释放。
5. 每个Runtime session独占内存resolver，复用RuntimeFrameValidator及完整报告链路。仅无预设static/static-analysis/direct-package基础源；图片错误保留稳定解码/预算码。图片export/measurement、preset/video/font、模板试看继续独立。
6. 功能源码`eb54f8d`、合同`7a03863`、工具`7543436`分层登记；clean匹配发布源码`7a03863`的fresh HTTP `http-final1`通过1412请求／10582断言，两次Server exit0、33个观察身份（26个Worker）全退出、3349文件凭证扫描通过。三基础源普通／透明各4帧、报告/诊断/控制与一次orderly recovery在有限范围内；正式validation无PNG，原Runtime报告Oracle不代称像素验收。真实旧0.99 Worker独立10请求／476断言、199文件负例拒绝503 `taskUnavailable`／`WorkerImageValidationUnavailable`；本地去重242/242与二次Writer变异4红／恢复4绿分列。不推定图片export/measurement、preset/video/font、完整B3/B4/B5/B6、浏览器、跨平台或性能矩阵完成。

## ADR-0238：已保存工程Image背景进入独立导出Worker（0.101.0／合同1.35.0）

决定为已保存Image主背景建立独立 `project-image-export-v1` 能力和 KIE1 封包，沿既有导出job/report/content路由交付有限切片。客户端不新增输入字段；Image task/base profile 只在登记账本中成对出现，旧导出账本保持兼容。范围限无预设三基础源，package 禁止音轨；图片measurement、preset/video/font及完整B3/B4/B5/B6继续独立。本文记录合同与实现边界，fresh HTTP 尚待验收。

<a id="adr-0239"></a>

## ADR-0239：Skia 统一设备接入本机 Vulkan GPU 渲染（2026-10-26）

用户授权在 `dev/gpu-render` 实施标准 GPU 渲染，由 GPT 6.1 Sol xhigh 子代理编码、主代理审查与验收。本批以本机真实硬件运行作为 GPU 验收边界；详细范围见[实施计划](../plans/gpu-render-plan.md)与[设备架构](../../architecture/render-skia-gpu-backend.md)。

1. 保留 CPU 默认路径；内部环境变量 `MAKO_RENDER_BACKEND=cpu|vulkan` 在创建会话时选择设备。Vulkan 创建失败明确报错，拒绝 CPU/软件 physical device，不静默回退。HTTP 路由、DTO、合同版本和工程格式不因该选择改变。
2. 使用现有 SkiaSharp 3.119.4 的 Ganesh Vulkan API，加匹配的 `SkiaSharp.Vulkan.SharpVk` 和 SharpVk 0.4.2。一个 Runtime 会话共享一个 device/context，主画布、图效果、材质、capture 和 group isolation 统一创建 GPU surface；原 Skia 绘制、字形与 Runtime 业务逻辑继续复用。
3. 最终 GPU surface 同步 flush/submit，再读回现有 premultiplied BGRA `RenderFrame`；PNG 与 FFmpeg 边界保持。CPU 内存图片允许上传，跨 context 的图像显式读回后传递。此决定没有引入 GPU 视频编码或零拷贝承诺。
4. 会话 gate 串行保护 Runtime 和 native GPU 对象。借用设备由会话最终释放；独立 graph/pipeline 的返回产物持有设备租约，创建者关闭后仍允许消费产物。图像、预算、编译资源和设备均明确拥有者；native 清理失败仍继续释放后续资源。
5. raw 材质区域与基础外观使用一致字形 layer 绘制路径，防止 GPU 字形栅格原点不同而触发合法内容越界。保留零覆盖区域严格禁止输出的材质验证，不扩大 alpha 容差。CPU golden 维持 CPU 基线，GPU 差异须逐项分类并检查几何、颜色、透明度及实际效果贡献。
6. 验收分为默认 CPU 回归、本机 GPU surface/readback 测试及匹配 self-contained Server/Worker 的 fresh HTTP。Worker 硬件证据绑定 PID/start ticks、发布 exe、Vulkan loader/指定 ICD 和 DRM render node；凭证扫描、终态与进程排水分别登记。有限本机验收不代表跨平台、驱动矩阵、吞吐或长片性能验收。

本 ADR 固定技术选择；最终源码基线和验收结果以本批独立验收记录为准。

<a id="adr-0240"></a>

## ADR-0240：CPU/GPU 作为固定预览和任务的显式执行选择（2026-10-26）

状态：设计已接受并实施，匹配发布的本机有限fresh HTTP及GPU不可用负例已通过；分层回归、来源、硬件和媒体证据见[本批验收报告](../acceptance/render-backend-api-acceptance-20261026.md)及[实施计划](../plans/backend-render-backend-api-plan.md)，公共行为见[统一 API §25](../../architecture/backend-api.md#render-backend-selection)。用户要求前端可切换渲染方式，继续 `dev/gpu-render`，未合入main；实际编码由 GPT 6.1 Sol xhigh 子代理完成，主代理审查与验收。

1. 公开值为 `cpu|gpu`，GPU 当前映射 Vulkan。部署环境仍为 `cpu|vulkan`，在启动时解析一次作为默认；每个请求可以覆盖。前端维护界面偏好，后端不新增全局可变开关或工程历史。创建/任务省略使用启动默认，已有预览更新省略保留当前选择。
2. 选择写入固定输入和排队任务，WorkerOptions 只覆写新子进程的环境。Worker 捕获不可变选择并用于实际 Runtime 工厂，不在执行期间重新读取可变环境。不同客户端、会话及排队任务不会因后续切换互相改变。
3. 可用性通过受管理 Worker 真实创建设备、绘制和读回检查。实际执行 Worker 在业务输入前也进行对称核验；缺能力、设备不匹配或 GPU 初始化失败明确拒绝，无 CPU 自动回退。设备检查、来源能力、业务执行成功及硬件运行证据分别记账。
4. 单独的预览后端切换接口适用于全部预览类型，复用原不可变 snapshot 和整组媒体租约。两次 generation/身份核对保护准备与提交边界；失败不替换原代次，相同值 CAS 后 no-op，成功换代失效旧结果。不得为切换设备重新捕获工程/草稿内容。
5. 验证、导出与测量共用同一选择语义；任务在准入时固定 executor，持久账本和报告记录所选后端。历史缺失字段继续表示未知，不伪造 CPU。恢复只读取原记录，不随当前偏好重跑。
6. 产品 0.106.0、合同 1.39.0、协议 1；更新同一提交的行为、metadata、生成合同与真实 HTTP 证据。有限本机 GPU 验收不推导前端浏览器、跨平台、性能或 GPU 编码完成。

<a id="adr-0241"></a>

## ADR-0241：受限图像读回复用与有界性能日志（2026-10-28）

状态：代码、相关回归、匹配发布与有限fresh HTTP已完成；用户恢复后正式性能七标签及监控开销矩阵也已通过。材质／组合主矩阵最大346.453／373.706ms满足固定样本500ms目标，首帧和独立监控对照仍有超限，不能作为全请求保证。精确范围见[验收记录](../acceptance/backend-gpu-structural-performance-optimization-acceptance-20261028.md)及[本批计划](../plans/backend-gpu-structural-performance-optimization-plan-20261028.md)。实现与验收在 `dev/performance-optimization` 完成；2026-10-28按用户后续授权将该分支的 `77643f8` 快进合入 `main`，见[合入记录](../history/backend-main-integration-20261028.md)。原运行证据保持dev来源，本次Git集成不增加验收范围。产品 0.107.0，公开合同 1.39.0／HTTP 协议 1 保持。

1. 颜色匹配直接读回设为内部专用入口，只供具有明确同设备所有权的 sRGB BGRA8888／Premul 快照和同尺寸原始 BGRA 检查使用。通用颜色转换、外借图像和跨设备 raster 转换保持原入口；原表面原点、同步、完整预乘和 masked 检查不变。
2. 无声明图像输入、无独立覆盖输出的成功作者图可在同一场景对象绘制期间共享。编译图身份固定包修订、设备和编码；时间、画布、边界、密度、透明模式、参数及逐节点 uniform 全部进入等价判断。只省略相同作者图的 native 执行，逐目标协议验证、覆盖、透明度、顺序和错误归属继续执行。
3. 缓存最多保留一张 16 MiB 图像，真实图像及预算预留一起存活；预算压力先释放可选缓存，再作原准入判断。缓存命中仍重验原执行峰值。不得只扣减账面数字却保留实际资源，也不能把缓存延长的资源生命周期隐藏为零成本。
4. 性能监控采用共享帧及阶段计量、有界异步队列和结构化日志。后端默认周期摘要，详细模式受采样及事件速率限制，关闭模式保留快速空路径。阶段为宿主 wall，可包含嵌套和 GPU 等待；读回字节为应用请求量，CPU／分配／GC／工作集为进程观察值，不解释为 GPU 硬件计时或单脚本独占资源。
5. 详细脚本身份仅记录有数量上限的摘要，不写歌词、作者源码、路径或凭证。Worker stdout 仍专用于原 framed IPC；Hosting 对 stderr 遥测限长、限速、固定字段重编码，显式标记为不可信 Worker 观察，因为作者代码也可写该流。日志不是安全审计或公共 HTTP 合同。
6. 日志故障、慢消费者及队列溢出不能替换原渲染结果、异常、取消或阻塞其主路径。周期进程观察覆盖长帧和初始化窗口；正常关闭有有界尾部刷新。源码回归、同后端完整像素／诊断、日志开销、匹配发布与 HTTP 排水分别验证。

<a id="adr-0242"></a>

## ADR-0242：作者草稿恢复、包编辑、历史与精确下载（0.108.0／合同1.40.0，2026-10-30）

状态：已实施并完成36项不同用例的定向回归、合同静态校验，以及匹配发布`6569bf1`的有限fresh HTTP（144请求／984断言）。精确范围、失败归属与排水见[W8作者修复记录](../../backend-w8-authoring-fixes-20261030.md)。

1. 作者草稿继续以状态根中的 `draft.json` 和不可变 `packages/<sourceRevision>` 为权威。profile 列表只暴露草稿摘要、草稿修订和所有权状态；新 client 必须用 `draftRevision` 与 `ownershipRevision` 显式 claim。claim 在草稿锁、ClientRegistry 所有权 reservation 和存储发布之间保持 CAS，活动 owner、在途操作、回收和配额门禁都拒绝抢占。
2. 包编辑只开放明确的文本 role（C#、shader、shaderProgram、graph）。客户端提交完整 manifest 与全部可编辑文本；服务端重新计算文本哈希，复用效果包结构/配置闭包校验，再一次发布新包。二进制和其它非文本文件的路径、角色、哈希和字节集合不可变，避免作者接口变成任意文件写入。
3. 源码历史从实际保留包目录派生，sidecar 只补充可证明的保存时间和编译摘要；旧草稿缺少时间时保持未知。历史读取按源码修订游标分页，游标不属于保留集合即拒绝。无变化保存保留草稿修订和最近编译摘要。
4. 草稿包与个人配置包均按精确修订导出 `.mkfx`，下载在认证流租约内完成。临时归档和最多两个并发下载槽由 lease 关闭统一释放，断开和宿主停止也走同一释放路径；下载前复核 owner/CAS、包身份和导入闭包。
5. W8 是公共能力新增，产品版本升至0.108.0、合同升至1.40.0。合同、行为源、OpenAPI、示例、manifest、前端交接和本 ADR 同步更新；静态合同、定向回归、匹配发布/fresh HTTP、浏览器和跨平台证据分别报告，不相互替代。

<a id="adr-0243"></a>

## ADR-0243：独立静态外辉光（0.110.0／合同1.42.0，2026-11-01）

采用独立 `GlowSpec` 和逐叶覆盖，默认关闭；以不透明真实字形聚合遮罩、round 外扩、高斯模糊、减去内部实现外辉光。它不依赖填充 alpha，不占用阴影或描边配置，也不随已唱边界重复绘制。参数、继承、捕获与兼容边界见[静态外辉光](../../style-reference/glow.md)。

基础字/注音分别有独立 Glow 颜色部件，进入现有图像/材质合成链；coverage 与原始材质区域明确排除。脚本只读基线，现有 writer 保留静态字段。本轮不增加动态辉光 writer。工程/歌词/模板 schema 升为 29/18/7，不新增自动迁移。实现、定向像素回归、合同、匹配发布与 HTTP 证据分别登记。

<a id="adr-0244"></a>

## ADR-0244：按模板格式隔离 Host 个人库（0.110.1／合同1.42.1，2026-11-03）

辉光升级已把模板格式改为 schema 7，但 Host 沿用未带版本的 `libraries/templates/index.json`，使保留 schema 6 的桌面 profile 无法使用个人模板库。新库改用 Core 的 `StyleTemplateLibrary.ResolvePath`；明确声明旧 schema 的固定入口保留原位，新库独立建立，不做旧数据转换。已在固定入口保存的当前 schema 库继续使用原位及其包闭包。

选址在首次个人库操作时有界检查，成功后固定；内置目录始终独立。未知版本、未来版本、损坏入口及两个当前入口的歧义都失败关闭，不用空库掩盖数据问题。现有 CAS、完整闭包验证与文件锁继续负责读写；HTTP 路由和 DTO 保持原形状。具体规则见[API §13.1](../../architecture/backend-api.md#131-读取与修订)。

<a id="adr-0245"></a>

## ADR-0245：Host 共享编译与 Worker 固定产物消费（2026-11-03）

App `dev/script-sdk-render-upgrade` 的 M01–M02 将 Roslyn 编译从渲染 Worker 分离到 Host。
作者与运行请求共享内容寻址的持久缓存；Worker 接收绑定实际字节摘要、SDK／参考集／引擎身份
和完整入口的固定产物，继续承担装载及 shader／图像／图／材质校验。失败诊断与成功产物分离，
保持原错误归属，禁止损坏产物回退。输入目录由 Host 持有到终态或实际进程排水，删除失败保留
所有权重试。规则与预算见[固定产物架构](../../architecture/script-compilation-artifacts.md)。

本阶段保留 SDK 1、包 2 和 v1 时间线重放；HTTP 公共形状保持，Worker 私有封包独立演进。
同输入 seek 和 GPU 备用池在 M03／M04 分开验收，不把进程预热当成作者输入准备完成。
用户已授权完成至 M06 的功能与测试，并在设备空闲窗口进行正式性能对照；证据按 App 验收清单登记。

<a id="adr-0246"></a>

## ADR-0246：HTTP seek 固定输入复用与可重试排水（2026-11-03）

App M03 保持公开 seek 换代、旧帧隔离与单执行者最新请求槽，将进程复用判断改为固定工厂／快照对象、
来源、输出规格及模式。未发出的请求取消保留 Runtime；严格匹配的协作取消 ACK 消费完毕后，在相同
Worker 内关闭并重开 Runtime，继续使用 M02 的固定产物和租约。硬超时、作者错误、IPC／设备失败不复用。
GPU stream 的每输入新 Worker 规则不在本节点改变，v1 的时间线重放也保留。

创建或关闭失败仍由协调器、包装会话及注册表保有清理所有权；未确认排水前不释放输入或创建替代会话。
重复关闭可以重试实际清理，失败不报告 closed。真实文件系统删除失败的最后引用保留并禁止继续 Acquire；
组合租约分别记录成功项，只重试失败部分。原始 Worker 诊断与清理失败分别保留。
产品 0.110.6／合同 1.42.4 记录该行为修正，不新增 HTTP 路由或 DTO；验收证据按 App M03 清单单列。

<a id="adr-0247"></a>

## ADR-0247：单次绑定的完整 GPU 备用 Worker（2026-11-04）

App M04 在每 host 两个 GPU stream Worker 硬名额内保留最多一个未绑定备用；开启、回收中和
排水失败也占名额。备用实际载入运行依赖、保留参考字节，创建所选 Vulkan device／Skia context，
并在原设备绘制验证。业务第一次 open 消费准备结果、移交原对象，失败也不退回备用。
作者输入、编译产物装载、业务索引与资源准备仍属于业务 open，不用进程预热代替这些阶段。

已绑定对象只排水退出，输入换代仍取得新 Worker。备用 30 秒过期，失败指数退避为 250 ms–30 秒。
部署身份按实际 Worker 完整文件闭包和清单内容计算，取用前后核对，不用 mtime 或清单自述代替字节。
availability 复用现有存活准备对象，两个活动流时不产生第三个 helper。
关闭重试必须先确认所属进程排水，再释放对应输入；测试、双消费端、真实初始化和三轮交互性能分别验收。

## ADR-0248：固定字体会话共享整形与有限中间等待优化（2026-11-04）

M05 在同一固定快照／字体集合的 Runtime 会话内共享受限 CPU 整形结果；主字体和显式回退可跨渲染器复用，
依赖系统动态回退的结果保留在各自引擎的弱引用缓存中。布局键包含回退链，单个渲染器释放不清空共享结果。
Ruby 布局按不可变行与样式缓存，索引 token 避免逐注音重复扫描；无注音行不建立索引。捕获只取正文时使用
独立空列表，不能清空后续绘制仍使用的缓存。缓存有明确容量，不扩大字体／原生资源所有权。

仅移除 SnapshotSceneBelow 在同一 device/context 内取得快照前的主动提交；既有图校验、合成顺序、
最终读回与外部 GPU 发布完成边界保持。完整像素、诊断、取消、CPU/Vulkan 有限 A/B 和实际提交计数
分别验收，不能由源码推断性能通过。图预乘小结果回读和更激进的空捕获优化仍交 M12，
本节点不宣称整个 S2 或零中间回读完成。产品 0.110.8，HTTP 合同形状和 SDK v1 求值语义保持。

## ADR-0249：保留原始几何的有限视频目标采样与编码队列所有权（2026-11-04）

M06 为视频来源增加可选目标尺寸请求，旧来源默认返回完整原尺寸；背景放置与图 BackgroundMedia 捕获
仍以原始媒体几何计算。仅完整来源铺满画布、两轴同整数倍缩小的路径请求较小输出；裁切、留边、
非整数／非均匀缩放沿用完整尺寸。显示矩阵或旋转标签存在时保守回退，不改写既有旋转语义。

FFmpeg 在 BGRA 颜色转换之后执行 point 采样；偶数倍补偿采样相位，与原 Skia 最近邻像素对照。
尺寸变化先排空旧解码进程、清除缓存、重建缓冲，再从正确时间重新取帧；滤镜不可用时回退完整尺寸。
优化减少 stdout、管道复制与接收缓冲，编解码器仍完整解码，构造时仍分配原尺寸缓冲，
不宣称 codec 原生缩放、峰值内存已下降或 GPU 异步回读。

编码器已有容量 2 的有界队列和渲染／写出重叠，M06 保留帧序、反压、取消及排水；
修复已释放会话拒绝新帧时未释放传入帧的所有权漏洞，并补充队列满、失败和顺序的真实进程回归。
产品 0.110.9；HTTP 形状、v1脚本求值与原透明背景行为保持。像素、输出字节、HTTP／导出和正式性能分别验收。

## ADR-0250：GPU 背景采样保留原尺寸以保持像素（2026-11-04）

0.110.9 的真实 Vulkan 视频质量回归出现 7 项失败；坐标编码探针显示偶数倍缩小时，
同一次绘制中不同像素会选择相邻的不同源样点，局部捕获又改变选择分布。CPU 的固定 point
采样相位不能直接替代这条 GPU 路径，不能通过放宽颜色或捕获门槛接受差异。

0.110.10 仅对实际 `CpuSkiaRenderDevice` 启用已验证的目标尺寸请求；Vulkan 与其他设备
保留原尺寸解码、上传和采样。主画面与 BackgroundMedia 捕获共用同一选择，避免二者交替
重启不同尺寸的 FFmpeg。CPU 仍受 ADR-0249 的几何和显示变换限制，公共接口及脚本系统不变。
本轮只主张 CPU 路径的输出字节缩减；GPU 目标采样需后续独立证明稳定等价后再开放。
原 7 项失败完整保留，修复后继续执行相同主画面通道差和精确捕获断言。


## ADR-0251：Base 材质保留中立继承与逐部位归属（2026-11-05）

SDK v2 的 Base 外观在最终 Family 中立 StyleView 上归约，不能从白色 LyricStyle 绘制种子
反推真实材质。新增 PreparedBaseMaterialBinding 与 SceneCaptureTarget.Base，保持 ImageEffectTarget
原范围；Family／Line 图继续消费完整 Base＋Ruby 来源集合。Base 显式纯色、渐变或关闭描边遮蔽父部位，
无材质写入则继承已验证的父 Source／Script owner；仅 opacity／affine 不改变该归属。

父 uniform 先完成，Base uniform 再基于最终父材质归约。原始 Replace／Uniform 仍逐次校验并计费，
最终存活写入才检查节点兼容性。材质捕获应用最终仿射／Warp一次，结果不再 Warp；实际材质预算／native
错误保留材质 owner 和 Base TextTarget，Warp 几何错误与取消保留原责任。

像素验收遵循既有区域合同的白色覆盖合并、一次着色与一次 alpha；独立 CPU 颜色合成对照公开 raw 几何，
保留≤2/255全图门槛。普通逐字半透明描边的重叠不等价于已合并材质区域，不能用它要求材质重复叠加alpha。
产品0.112.12；HTTP／导出／作者完整闭环证据仍按后继匹配版本分别验收，不由内部像素测试推定完成。

## ADR-0252：组图闭合 C/E、完整范围与 owner 下层（2026-11-05）

SDK v2 用 `image.graph.group` 加 `image.graph` 开放真实 source Group 的 ColorOnly 输出，
controller与实际graph各自声明能力；Project／Node按真实父树授权，alias只读，旧v1组描述具名拒绝。
组调度继承完整孩子擦除E，独立孩子顺序合成，不能把孩子的History混成同目标历史。
父组只读固定进入前缀，孩子读前序兄弟；真实背景与直接owner图元重建下层，最终画布仍含所有图元。

实数alpha因式分解写入BGRA8后存在累计量化；同时保存完整范围的“先合成后乘alpha”C/E与
可选择有效部件。完整连续身份匹配才复用范围，写入后失效，partial不复原遮挡内容。闭合复制
接管后立即释放原始捕获／缓存，保持默认64表面限额；不以放宽像素容差或预算解决资源峰值。
实际捕获／作者图／宿主分配共享帧pass租约，空组预检复用实际执行的峰值算法，均先于native分配。

产品0.112.13；真实renderer／包准入、Vulkan环境及取消恢复已有有限证据，HTTP／作者UI／导出
仍按匹配版本后继验收。详见组图合同与M09证据，不视为M09、M11或性能通过。
