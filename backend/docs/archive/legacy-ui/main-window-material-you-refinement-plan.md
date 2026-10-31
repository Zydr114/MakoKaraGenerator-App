# 主界面字体与 Material You 视觉优化方案

> 状态：主窗口第一轮、字体/调色板、取色器和窄屏到宽屏的检查器状态修复已实施。依据当前工作树及根目录的主界面、样式编辑器和侧栏问题截图。尺寸均指 Avalonia **logical px**；截图可能经过窗口管理器缩放，不用截图像素直接推断控件尺寸。

## 1. 结论与边界

当前深蓝色阶、分栏、选中状态和圆角已经建立了 Material You 的基本气质，适合创作工具；但控件的文字层级、按钮强调级别和信息密度还不统一。特别是歌词栏底部把六个重字重的胶囊按钮排成两行，红圈中的「复制行」因此显得笔画粘连、难读。截图不能证明字体文件损坏或字形缺失，应先按**字重、尺寸、排布、缩放**四项排查。

本方案遵守 [ADR-0045](../decisions/decisions.md)：保留 Semi.Avalonia 模板，继续用 `Theme/Colors.axaml` 管深浅色和 `Theme/MaterialYou.axaml` 管控件外观；不更换组件库，不改变歌词或视频渲染规则。目标是遵循 Material 3 的颜色角色、文字层级、按钮强调与自适应布局原则，不能仅凭蓝色和圆角声称组件已完整实现 Material 3 规范。

## 2. 截图与代码证据

| 位置 | 看见的问题 | 当前实现 | 判断 |
|---|---|---|---|
| 红圈「复制行」 | 字形偏粗，复杂汉字笔画拥挤；与相邻操作挤在同一排 | 文案 `ToolbarDuplicateLine` 确为「复制行」；`DuplicateLineButton` 无局部字体设置；三个按钮在宽 240 的歌词栏内由横向 `StackPanel` 排列 | **文字内容和字体文件缺失可排除**；粗重样式与密度是优先排查方向，是否有缩放栅格化问题仍待运行时对比 |
| 歌词列表第三行 | `🏳️‍🌈` 在列表中拆成旗帜和方框，时间轴中呈现彩色旗帜 | 示例字符串确为完整 ZWJ 序列；列表使用普通 Avalonia `TextBlock`，时间轴走自绘文本路径；本机已安装 `Noto Color Emoji` | **另一个字体回退/复合 emoji 显示问题**，不能用调整按钮字重解决 |
| 左下编辑区 | 行操作、元素时间、样式入口挤在歌词栏下方，列表空间被压缩 | 左栏固定 240；行操作两排 6 个按钮，下面还有上下文摘要和时间输入 | 任务层级需要重排，单改间距不足以解决 |
| 画布 | 当前时刻无歌词时，中心主要是一大片暗色区域，画面边界和导出比例不醒目 | `KaraokeCanvas` 内部按输出比例 `FitWithin`，外围 `Border` 却填满整个可用区域 | 渲染比例本身已有保障；**承载画面的 UI** 应明确标出真正的 16:9 画面边界 |
| 右侧属性 | 工程合成、行文本、媒体与报告在同一长滚动区，说明与数值混排 | 右栏固定 340；唯一「属性」页签含多类设置和诊断 | 信息架构不够清楚，需拆成上下文编辑与工程设置 |
| 样式窗口 | 三栏方向正确，但试样文字在截图中很小，底部应用区被长摘要占满 | 预览/属性/样式库和固定应用栏均已存在 | 保留已实现的交互，调整预览比例、摘要层级与应用栏密度 |

### 2.1 红圈字体的诊断结论与待证项

本机 `fc-match` 能解析 `Noto Sans CJK SC` 的 Regular、Medium 和 Bold，也能解析 `Maple Mono NF CN`。`MainWindow.axaml` 对「复制行」没有特别的 `FontFamily` 或 `FontWeight`，而当前版本 Semi 的按钮默认字重资源指向 **Bold**；本项目全局 `Button` 样式覆盖了背景、圆角和内边距，**没有覆盖字重与字号**。所以三字按钮继承了与 Material 3 常用 `labelLarge` Medium 不同的粗重外观。这是有源代码支持的主要原因推断，不把截图里可能的缩放抗锯齿问题误判成已证实的字形缺陷。

实施前做一次短探针：记录这三个按钮运行时实际 `FontFamily` / `FontWeight` / `FontSize` / `Bounds`，在 100%、125%、180% 缩放下截同区域，确认是否存在文字越界或只有特定缩放下的栅格化异常。探针完成后删除。验收看实际控制边界与清晰度，不以仅有“代码设了 Medium”为完成依据。

彩虹旗问题已经按这个诊断路径收口：Avalonia 启动时通过 `FontManagerOptions.FontFallbacks` 为 `U+1F000–U+1FAFF` 与 `U+2600–U+27BF` 注册 `Noto Color Emoji`；ZWJ/变体选择符仍作为同一个文本元素交给整形器。Skia 时间轴和画布继续使用已有的按文本元素回退链，未把界面字体资源传入视频渲染。

### 2.2 可读性量化

以当前 `Colors.axaml` 的前景/背景角色和 XAML `Opacity` 混合粗算：`UiOnSurfaceBrush` 在暗色 `UiSurfaceBrush` 上约 **15.1:1**，文字基础色足够；`TextBlock.rowIndex` 的 0.45 透明度约 **3.93:1**，浅色主题约 **2.82:1**；`rowTiming` 的 0.5 透明度在浅色主题约 **3.25:1**。实际选中行背景和抗锯齿会改变局部数值，因此这些数字用于**定位风险**，最终应按每个实际状态重新测量。参考 WCAG 2.2 AA 的普通文字 4.5:1、较大文字 3:1；这是桌面界面的质量目标，不声称该 Web 标准自动适用于整个应用。

## 3. Material You 观感评估

| 维度 | 现状评价 | 优化方向 |
|---|---|---|
| 色阶与表面 | 深蓝表面和 `primaryContainer` 选中行协调；大面积背景平稳 | 保留蓝色方向。主工作区、侧栏、浮层只用现有 tonal role；减少等强度的边线和卡片套卡片 |
| 文字层级 | 主窗口多数标题 13、辅助 11 或 12，按钮默认 Bold，长说明还用等宽字体 | 建立 `title / body / label / numeric` 四组语义样式；按钮 Medium，说明 Regular，等宽只给时间码和数值 |
| 按钮层级 | 常规操作几乎都是同样的灰色填充胶囊，主次和破坏性操作难区分 | 播放/应用用 primary；新建等重要次级操作用 tonal；复制用 text/outlined；删除用低强调的危险操作并在执行前保留原确认流程 |
| 形状与密度 | 整体圆角一致，但 30 高按钮和 16 半径形成满屏小胶囊 | 常用按钮高 40–44，圆角按容器/输入/按钮分级；高密度时间轴继续保持紧凑，不整体照搬移动端留白 |
| 状态反馈 | 选中歌词行清晰；禁用控件、焦点、未应用改动易与一般文字混在一起 | 为 hover、press、focus、disabled、selected、error 定义可辨状态；状态不用颜色单独表达 |
| 自适应 | 样式窗口已有三档布局；主窗口仍是固定 240/340 左右栏 | 主窗口按**可用窗口宽高**重排，检查器变抽屉，歌词栏操作变上下文菜单 |
| 个性化色彩 | 主题已有蓝色种子色和双主题角色字典 | 主题菜单开放 Material You 调色板；种子色保存到应用级 `theme.json`，不进入工程文件，浅/深角色同时预览并通过统一资源覆盖 |

Material 3 的基础口径以 [Google 的颜色与排版说明](https://developer.android.com/develop/ui/compose/designsystems/material3)、[按钮类型与强调级别](https://developer.android.com/develop/ui/compose/components/button)、[自适应分栏示例](https://m3.material.io/foundations/layout/canonical-examples/overview) 为参照。Google 的移动端 48dp 触达建议用于触摸模式，桌面鼠标密度保留本项目的 40–44 logical px 常用目标，并确保键盘可聚焦；不机械地把时间轴上每个字块都放大到 48。

## 4. 目标界面

### 4.1 主窗口的信息架构

```text
工程/编辑/视图                 工程名与保存状态          导出
歌词列表与搜索       ┃ 输出画面（明确 16:9 边界） ┃ 当前对象 / 工程设置
选中行快捷操作       ┃ 播放、时码、预览质量         ┃ 上下文表单或设置表单
────────────────────┴───────────────────────────────┴────────────────────
时间轴工具栏（常用工具 + “操作帮助”）
概览 / 波形 / 逐字块，可调整高度
状态栏：当前动作或错误；快捷键总表从帮助入口展开
```

1. **歌词栏只做导航和行级快捷操作**：列表占据主要高度。底部常驻「新建行」和选中行摘要；复制、删除、拆分、合并放进选中行的「更多」菜单，常用快捷键保留，并在菜单里展示。宽窗口可让「复制行」作为次级按钮显露，但不能靠三个不换行胶囊硬挤。
2. **对象编辑归右侧上下文检查器**：选中行时展示行文本、样式摘要；选中元素时首先展示起止/时长和微调，其次是样式入口。工程级画布、背景、输出时长移到「工程设置」段。报告/整形诊断折叠为状态详情，不和日常表单抢首屏。
3. **预览区标出真实输出画面**：画面外使用较浅/较暗的 `surfaceContainer`，真正的视频内容保留当前 `FitWithin` 行为并给细边框和尺寸标识。当前时刻无歌词时，在画面外说明“此处暂无歌词”，提供跳到选中行/首句的操作；不把 UI 提示画进导出帧。
4. **时间轴工具栏精简**：把截图中横贯整行的手势说明收进「操作帮助」弹层，当前吸附模式与常用操作留在工具栏。时间轴高度允许拖拽；状态栏显示当前操作或校验错误，不长期重复完整快捷键列表。

### 4.2 自适应规则

以下是方案目标；当前实现的单一断点和固定侧栏宽度见表后说明。

| 主窗口可用宽度 | 布局 |
|---|---|
| ≥1280 | 歌词栏 260–280、中央自适应、检查器 340–380；画面优先保持可读宽度 |
| 1040–1279 | 歌词栏 240–260、中央自适应；检查器默认可收起并以覆盖式抽屉打开，不挤压画面 |
| 940–1039 | 歌词栏 220–240；行操作折叠为「更多」；检查器为抽屉；画面与时间轴保留最低可用空间 |

高度也参与判断：在 640–760 的低窗口中，时间轴先缩到约 170–190，侧栏内容独立滚动，标题/主操作不被长说明推出可视区。窗口尺寸以 `WorkspaceGrid.Bounds` 判定（窗口 `Bounds` 只作初始化回退），别以物理显示器分辨率判定。

**当前实现**：工作区宽度低于 1100 logical px 时，检查器以覆盖式浮层打开；达到 1100 时，检查器按用户最近的开关状态回到右侧独立栏。歌词栏固定 270、右栏默认 360 logical px；上表的三档侧栏宽度仍是后续布局目标。切换宽度时同时监听窗口与工作区的尺寸变化，优先用工作区实际宽度判断断点。

### 4.3 样式窗口的后续美化

保留现有样式库、前后对照、草稿属性和固定应用栏，沿用已实施的 [样式编辑器 UX 方案](style-editor-ux-redesign-plan.md)。把试样画面提高到中心区域的主要视觉位置，保证真实歌词的字号在常见窗口下可读；“应用前/候选效果”的边框、缩放和时刻一致。底部应用栏默认只显示**目标、将写入的属性数、不可用项数和主按钮**，长的属性清单和限制说明放可展开详情；错误时保留可见的纠正入口。属性行复用同一排版与取色按钮尺寸，减少混用多个 12/13/14 字号造成的跳动。

## 5. 视觉 Token 与组件规格

| 角色 | 建议值（logical px） | 用法 |
|---|---:|---|
| 窗口标题 | 20/28，Medium 或 SemiBold | 窗口或独立工作区标题 |
| 分区标题 | 16/24，Medium | 歌词行、预览、属性组、时间轴 |
| 正文 | 14/20，Regular | 歌词列表、表单值、说明 |
| 按钮标签 | 14/20，Medium | 普通按钮；不要沿用 Semi 默认 Bold |
| 次要信息 | 12/16，Regular | 时间摘要、来源说明；保持足够对比度 |
| 时间码/数值 | 12–13/18，等宽 | 仅时间、帧率、坐标等需要对齐的内容 |
| 常用按钮 | 高 40–44、左右内边距 12–16 | 鼠标/键盘可靠点击；窗口受限时不压扁文字 |
| 间距 | 4/8/12/16/24 | 小控件内部到区块之间逐级增加 |
| 圆角 | 输入 10–12、按钮 12–16、主容器 18–20 | 少用所有控件相同的胶囊形 |

本轮直接在 `Theme/MaterialYou.axaml` 定义语义文字样式，避免为了几个样式再引入一层资源字典。按钮全局已明确设置 `FontFamily=Noto Sans CJK SC`、`FontWeight=Medium`、`FontSize=14`；歌词文本按钮单独为 Regular。仅在首要动作上使用更强字重。颜色/状态沿用 `Colors.axaml` 中的成对角色（如 `primary/onPrimary`），新增色要同时提供深浅变体。

## 6. 实施顺序与验收

| 批次 | 工作与主要文件 | 完成判据 |
|---|---|---|
| P0 字体与可读性 | `Theme/MaterialYou.axaml`、`Theme/UiFontConfiguration.cs`、`Program.cs`、`MainWindow.axaml` | 红圈按钮在 100%/125%/180% 下无笔画粘连、裁切或越界；中文/Latin/emoji 混排由主字体与按区段回退明确处理；暗/浅主题辅助文字按状态测对比度；不修改歌词原文 |
| P1 主窗口结构 | `MainWindow.axaml`、`MainWindow.Inspector.cs`、窗口布局逻辑、必要的 `Strings.axaml` | 940×640、约 1200×800、1600×1000 logical px 均能看到歌词、真实画面和时间轴；右侧抽屉不遮挡当前交互；行级菜单和键盘入口均可用 |
| P2 预览与时间轴 | `KaraokeCanvas` 外围布局、时间轴工具栏与状态区 | 视频实际 16:9 画面边界清楚；无歌词时不会被误认为渲染失败；长提示不挤占工具栏，时间轴轨道可读 |
| P3 样式窗口细化 | `StyleDesignerPanel`、`StylePreviewStage`、`StyleApplyBarView` | 当前/候选试样清晰可比；底部主按钮在 880×620 可见；长摘要可展开但不会压垮预览 |

各批次做真实窗口截图复核：深/浅主题、选中/未选中/禁用/焦点、缩放 1.0/1.25/1.8，以及 `🏳️‍🌈 emoji 与 café 混排`。自动测试覆盖布局状态切换和色阶计算；像素截图用于检查字体与裁切；键盘 Tab/Enter/Esc、窗口管理器缩放需真机验收。只按视觉方案改外围 UI，不让界面字体或色阶进入视频渲染/工程持久化。

## 7. 参考

- [Material 3 的排版、色阶与可访问性说明](https://developer.android.com/develop/ui/compose/designsystems/material3)：类型角色、成对色阶与对比度。
- [Material 3 按钮](https://developer.android.com/develop/ui/compose/components/button)：filled、tonal、outlined、text 的强调层级。
- [Material 3 自适应布局实例](https://m3.material.io/foundations/layout/canonical-examples/overview)：主内容与辅助面板随空间调整。
- [WCAG 2.2 文字对比度说明](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html)：普通文字 4.5:1、大字 3:1。
- [Semi.Avalonia v12.1.0.1 按钮默认资源](https://raw.githubusercontent.com/irihitech/Semi.Avalonia/v12.1.0.1/src/Semi.Avalonia/Themes/Shared/Button.axaml)：默认按钮字重指向 Bold。
- [Avalonia FontManagerOptions](https://docs.avaloniaui.net/api/avalonia/media/fontmanageroptions)：支持受控的字体回退设置；实际 emoji 序列仍需在当前版本实测。

## 8. 第一轮实施记录

- `Theme/MaterialYou.axaml`：全局按钮改为 Noto Sans CJK SC、14px Medium、40px 最小高度；新增 compact、tonal、danger、sectionTitle、bodyText、supportingText、numericText 语义样式；输入框统一为 40px 和 10px 圆角。
- `MainWindow.axaml`：歌词栏增宽到 270、检查器目标宽度增至 360；行操作按 primary/tonal/outlined/text/danger 分级并允许换行；选中元素的编辑区收进独立表面；画布外围增加 surface container；诊断与报告默认折叠；时间轴手势和快捷键改为弹层帮助。
- `MainWindow.Inspector.cs`：窗口宽度小于 1100 logical px 时自动收起检查器；点击检查器按钮在紧凑宽度以覆盖式抽屉打开，恢复宽度后回到右栏；时间轴高度随窗口高度在 170–220 logical px 间调整。
- `MainWindow.Inspector.cs` 响应式状态修复：紧凑模式浮层的打开/关闭会写回用户选择，切到宽屏时恢复为右侧栏；断点优先读取 `WorkspaceGrid.Bounds.Width`，并同时监听窗口与工作区尺寸，避免测量顺序导致按钮切换不重排。
- `StyleDesignerPanel`、`StylePreviewStage`、`StyleApplyBarView`、`StyleEditorWindow`：保留已有样式工作区交互，提升试样画面最小高度、标题层级和应用栏信息层级，减少长摘要对主操作的挤压。
- 字体回退：`Theme/UiFontConfiguration.cs` 设置 `Noto Sans CJK SC` 为主字体，并按 emoji Unicode 区段注册 `Noto Color Emoji`；`Program.BuildAvaloniaApp()` 通过 `With(FontManagerOptions)` 注入；增加配置回退测试。渲染层的既有 HarfBuzz 复合 emoji 回退保持不变。
- Material You 调色板：`UiAccentPalette.CreatePalette(seed)` 暴露浅/深角色字典，`CreateOverrides(seed)` 覆盖表面、主色、次色、边框、时间轴等主题资源；浅色表面 tone 从接近白色的 0.99 下移到 0.95/0.92/0.89/0.86/0.83，保证根窗口和容器都能看出种子色层级，同时保留最低层纯白作为卡片对比；`UiThemeSettings` 将种子色保存到 XDG 配置目录下的 `theme.json`；主题菜单新增调色板对话框，确认后即时更新界面。
- 取色器：`MaterialColorPickerPanel` 统一服务样式检查器色块和主题调色板；默认只展示少量圆形常用色，点击“高级取色”后才懒加载 `ColorView`、RGB/HSV、十六进制和 Alpha 控件。两处入口共用 `colorPickerSurface` / `colorPickerFlyout` 圆角资源，半透明色块使用棋盘底衬托。
- 验证：`dotnet build MakoKaraGenerator.slnx` 0 警告/0 错误；`dotnet test MakoKaraGenerator.slnx` 全部通过（1330 项）；取色器默认态和高级态均通过实际 Avalonia 初始化路径加载；窄屏打开检查器浮层后扩展到 2000 logical px 的实窗截图确认检查器恢复为右栏、画布重新分配宽度。实窗检查日志只有运行环境的 ALSA/JACK 设备告警，没有 XAML 加载或未处理异常。临时窗口探针已删除。
