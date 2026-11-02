# 界面设计稿：台前幕后

一份设计稿，覆盖开屏、新建工程、歌词、编排、外观、样式库（模板与作者草稿）、导出，另加工程设置、
应用设置两个侧边面板和关于对话框。第一版提供了三个方向，用户选定“台前幕后”，并提出第二版的要求：
统一的浅色、深色模式，带 Material 风味但更紧凑，用户可以自选主题色，界面里使用软件标志，
默认主题色从标志取色。改版计划（[`../web-client-redesign-design.md`](../web-client-redesign-design.md)）
第 0 步补了外观页、模板选择器、样式库各页签、作者草稿和工程设置。

页面分区、交互和用词都沿用 [`../web-ui-design.md`](../web-ui-design.md)，这里只改视觉和布局。
稿件是手写的 HTML 与 CSS，用来定方向，不是组件库的实际渲染；歌词和工程都是虚构的演示数据。
第一版的“暗场”“歌本”两份稿已删除，可在提交 `5ccae19` 中查看。

## 查看

```sh
xdg-open frontend/docs/ui-style-drafts/index.html
```

- `index.html`：总览。浅色、深色对照，其他画面，外观与样式库，四个主题色下的编排页，颜色角色，紧凑尺寸对照，
  组件库许可表。顶部可以切换主题色和本页模式，缩略图跟着变。
- `desk-stage.html`：设计稿。地址后加画面名切换画面：`#home`、`#new`、`#lyrics`、`#orchestration`、
  `#appearance`、`#picker`、`#library`、`#draft`、`#export`、`#project-settings`、`#settings`、`#about`；
  有页签的画面用“画面-页签”直接打开某个页签：`#library-material`、`#library-fx`、`#library-adv`、
  `#draft-source`、`#draft-validate`。地址参数 `mode=light|dark|auto`、`seed=019bfe`（不带 #）、
  `vivid=1|1.8` 设定主题；加 `?shot` 隐藏底部浮动条，方便截图。浮动条可以切换画面、模式和主题色。
- `shared/theme.css`：由主题色推出全部颜色角色，浅色、深色各一套；加载 Roboto Flex。
- `shared/mock.js`：演示数据、Material Symbols 图标、渲染函数（编辑器顶栏、行列表、文本单元、逐字预览、时间轴、
  渲染画面、模板缩略图、工程列表、主题色色板）、主题切换和画面、页签切换。
- `shared/base.css`：重置、时间轴结构、渲染画面、浮动条。
- `shared/fonts/`：Roboto Flex 子集（只含拉丁字母、数字和常用标点）及其 OFL 许可文本。
- 标志在 `frontend/assets/logo/`：`logo-icon.png` 用于顶栏左上角和主题色的“从标志取色”，
  `logo-wide.png` 用于关于对话框。

稿件按 1440 × 900 设计和检查。

## 布局

沿用第一版的布局，密度原则不变：“现状拥挤杂乱，要有秩序”，比 mdui 默认紧，比视频剪辑软件松，
靠分组、对齐和统一的控件尺寸建立秩序。

| 现状 | 改为 |
|---|---|
| 底部叠着 4～5 条横栏 | 一条播放条（播放、速度、循环、本页开关、缩放、吸附）加时间轴 |
| 所选文本单元的时间字段随编辑区滚动 | 固定在行编辑区底部 |
| “歌词分配”占一条底部横栏 | 移到编排页左栏 |
| 56px 描边输入框挨着原生下拉框 | 控件只有 32、28 两档高度 |
| 快捷键和提示常驻在各处 | 收进设置里的“快捷键”；状态只在一处显示 |
| 白色平面，没有层级 | 台面、面板、输入框三层色调 |

- **台前**：填写、选择、列表、属性，放在面板上。**幕后**：预览、画布、时间轴，是一块凹下去的区域。
  面板之间留 8px 缝；幕后的预览板伸进缝里和时间轴相接，接缝处补内凹圆角：歌词页是 L 形（逐字预览加时间轴），
  编排页是 T 形（画布加时间轴）。
- 顶栏：标志（回到首页）、工程名、保存状态、页面切换（选中的页签用填充图标）、样式库；右侧是连接状态、
  撤销重做、工程设置、应用设置、保存、导出视频。
- 开屏左边找工程，右边幕后显示选中工程的画面、信息、打轴进度、最近导出和备份。
- 新建工程对话框左边填写，右边幕后检查文件（波形、识别结果、前几行歌词）。
- 导出页左边设置和任务，右边幕后看首帧、末帧和导出范围。
- 歌词页的行编辑区补了“外观”和“显示在”两项，只读：这一行生效的模板和来源、所在的片段和容器，
  点击跳到外观页或编排页。不显示槽位和同屏的行（现有数据给不出）。
- 工程设置是右侧面板，与应用设置同一种外形，从顶栏的“工程设置”打开；尺寸与帧率、适配方式与底色两两并排，
  音频分析并进“音频”一组。

### 外观页与样式库

- **外观页**沿用编排页的 T 形幕后区。左栏是外观目标：工程默认、各歌词来源（展开到行，选中的行再展开文本单元），
  底部一节是编排对象（结构和片段）；有直接分配的目标在右侧标出模板名和一个色块。中间是成片预览，
  标题栏写明设备、修订、时刻和预览规格。右栏检查器依次是模板（缩略图、停用、更新、移除、换模板、
  在样式库中编辑副本）、本层直接覆盖（只读）、外观来源（每项的取值和来自哪一层，点来源选中那个目标）。
- **选择模板**是对话框：左边按内置、我的分组列出缩略图卡片，不适用的灰显并写明原因；右边幕后区试看，
  标明“没有写入工程”，下面是来源输入和模板摘要；底部写明应用会替换原有分配。
- **样式库**三栏：列表（模板、效果组件两个页签）、中间幕后区试样（样本、目标、试播、时刻、循环）、
  右边编辑栏。从外观页“在样式库中编辑副本”过来时，顶部有返回栏和“保存并返回试看”。
  - 外观页签按“文字”“颜色与描边”分组，每一项一行，行尾的开关决定这一项是否写进模板，关掉时由目标继承。
    渐变的色标在色条下方拖动，也可以逐个填写。
  - 材质页签先选部位（同时是试样的材质焦点），再选效果包和材质、调参数；下面是模板用到的效果包。
  - 效果页签上面是效果列表（拖动或上下移排序、复制、删除、启用开关），下面编辑选中的一项和它的参数；
    曲线参数直接在图上拖控制点。
  - 高级页签显示完整定义（只读）和效果包里的文件。
- **作者草稿**在效果组件页签里：配置（导出、作用于、叠放层、读取范围、参数）、源码（文件、修订、编辑器、
  编译诊断；这个页签把编辑栏放宽到 680px）、验证（条件、报告；有行号的问题跳到源码对应行）。

## 颜色

**统一模式。** 浅色、深色两套颜色角色同构，所有画面都跟随同一个模式（浅色、深色、跟随系统）。
台前与幕后不再靠明暗反差区分，而是靠颜色角色：

| 角色 | 用在哪里 |
|---|---|
| 主色 primary | 编辑：主要操作、焦点框、选中的文本单元和行块、分组标题 |
| 辅助色容器 secondary-container | 选中的列表行、当前页签、开着的开关按钮 |
| 第三色 tertiary | 时间：播放按钮、播放头、波形已播放部分、逐字填充 |
| 幕后 stage | 带第三色调的凹面；浅色模式比台面略深，深色模式比面板略深 |
| 台面、面板、输入框 | surface-container、surface-lowest、field 三层色调 |

幕后区域里，开关按钮和标记改用第三色容器，与台前的辅助色容器区分。画面上的选中框用亮的主色，
因为渲染画面属于工程内容，始终偏暗；渲染画面的颜色由样式模板决定，不随界面主题变化。

**主题色。** 所有角色由一个种子色推出，默认 `#019BFE`。它是用 material-color-utilities 的取色算法
（`QuantizerCelebi` 加 `Score`）从 `logo-icon.png` 取出的四个候选色之一（`#9D41FF`、`#019BFE`、`#02E1FF`、`#605BFF`），
按用户决定选用蓝色。设置面板提供：

- 主题模式：跟随系统、浅色、深色。
- 主题色：从标志取色（默认）、6 个预设色、自定义（系统取色器）。每个色块按 Android 壁纸取色的样式，
  画出该种子色推出的主色、辅助色和第三色。
- 色彩强度：柔和（M3 默认的 Tonal Spot）、鲜明（色度约 1.8 倍）。

**稿件与正式实现的差别。** 浏览器从 `file://` 打开时不能导入 ES 模块，稿件只好用 CSS 的 OKLCH 相对颜色
近似 M3：明度按 M3 色调换算（L ≈ (T + 16) / 116），第三色取色相加 60°。正式实现用
material-color-utilities 的 `SchemeTonalSpot`（强度“鲜明”可用 `SchemeVibrant`）在 HCT 色彩空间生成，
写成同名的 CSS 变量；`#019BFE` 的 Tonal Spot 结果是浅色 primary `#35618E`、tertiary `#6A5778`，
深色 primary `#A0CAFD`、tertiary `#D6BEE5`，与稿件的近似值接近。

## 紧凑的 Material

保留 M3 的形状、配色、状态层（悬停叠 8% 当前色）、选中记号和分组标题，尺寸按桌面编辑器压紧。

| 项目 | M3 默认 | 本稿 |
|---|---|---|
| 按钮 | 40px，胶囊 | 32px，工具栏 28px；填充、色调、描边、文字四种，每屏只有最主要的操作用填充 |
| 输入框 | 56px，描边或下划线 | 32px 填充式，8px 圆角，标签和单位放在框内；焦点时 2px 主色内框 |
| 分段按钮 | 40px | 30px（工具栏 26px），选中项加对勾 |
| 列表行 | 56～88px | 单行 32px，两行 44px，12px 圆角 |
| 开关、滑块 | 52 × 32、44px | 36 × 20 开关；缩放用 M3 Expressive 的分段滑块，20px 高 |
| 圆角 | 按组件 | 面板 16，列表行和小卡片 12，输入框 8，标签 6，对话框和侧边面板 28 |
| 间距与字号 | 16～24px，正文 14px | 面板之间 8px，正文 13px，分组标题 12.5px，面板标题 14～18px |
| 层级 | 阴影加色调 | 只用色调；阴影只给对话框和侧边面板 |

**字体。** 拉丁字母和数字用 Roboto Flex（可变字体，字重和光学尺寸两个轴），中文用 Noto Sans CJK SC，
日文用 Noto Sans CJK JP。Roboto Flex 的数字默认等宽，时间码不需要等宽字体。

**图标。** Material Symbols Rounded，字重 400；选中的页签和播放按钮用填充版。

## 组件库

仓库计划以 GPLv3 开源。MIT、ISC、BSD 可以并入 GPLv2 和 GPLv3；Apache-2.0 只能并入 GPLv3，
与 GPLv2 不兼容，所以本仓库应写 GPLv3 或“GPLv3 或更新版本”，不要写“仅 GPLv2”。
OFL 字体可以随软件一起分发，字体本身仍按 OFL。

用户选定“无样式库加自写 M3”。界面改版已完成（2026-11-03），mdui 已移除，现在用的是：

| 库 | 版本 | 许可 | 与 GPLv3 | 用途 |
|---|---|---|---|---|
| React Aria Components | 1.22 | Apache-2.0 | 兼容 | 无样式的交互层：列表多选、拖放排序、树、带单位的数字字段、菜单、对话框、键盘操作 |
| @material/material-color-utilities | 0.4 | Apache-2.0 | 兼容 | M3 取色和配色，从标志或任意颜色生成两套角色 |
| Material Symbols（@material-symbols/svg-400） | 0.48 | Apache-2.0 | 兼容 | 图标；开发依赖，由脚本生成只含用到图标的模块 |
| Roboto Flex | — | OFL-1.1 | 可随附 | 界面字体，按需子集化后入库（`src/editor/theme/fonts/`） |
| Noto Sans CJK | — | OFL-1.1 | 可随附 | 中日文字体；暂用系统字体，不打包 |

改版前用的 mdui（MIT）已在批 7 移除。

考虑过但不采用：

| 库 | 许可 | 原因 |
|---|---|---|
| Material Web（@material/web） | Apache-2.0 | 官方 M3 组件，处于维护模式；尺寸难以压紧 |
| MUI（Material UI） | MIT | 以 M2 风格为主，覆盖样式的成本比自写还高 |
| Base UI、Radix | MIT | 无样式，比 React Aria 轻，可作备选；拖放和树要另找方案 |
| Mantine、Semi Design、Blueprint | MIT、MIT、Apache-2.0 | 自带风格强，要花力气去掉原样式 |
| Fluent UI v9 | MIT（代码） | 字体和图标资产另有许可，不宜随 GPL 软件分发 |
| dockview | MIT（核心） | 8.0 起另有商业许可的 Enterprise 附加包；本软件的分区固定，用不到停靠 |
| react-resizable-panels | MIT | 幕后区的拼接要求预览板和时间轴在同一个网格里，分栏改为自写（见改版计划 §2.1） |
| MiSans、HarmonyOS Sans | 专有 | 不能随 GPL 软件打包 |

## 来源

许可与版本信息于 2026-11-01 查阅：

- Material Design 3：https://m3.material.io/ （颜色角色、形状、状态层、分段按钮、开关、滑块）
- material-color-utilities：https://github.com/material-foundation/material-color-utilities
- Material Symbols：https://github.com/google/material-design-icons 、https://fonts.google.com/icons
- Roboto Flex：https://github.com/googlefonts/roboto-flex
- Material Web：https://github.com/material-components/material-web
- React Aria Components：https://react-spectrum.adobe.com/react-aria/
- Base UI 与 React Aria 对比：https://www.untitledui.com/blog/base-ui-vs-react-aria
- Radix 的替代品：https://designrevision.com/alternatives/radix-ui
- dockview：https://www.npmjs.com/package/dockview-react 、https://dockview.dev/blog/dockview-enterprise/
- Fluent UI v9：https://www.npmjs.com/package/@fluentui/react-components
- react-resizable-panels：https://github.com/bvaughn/react-resizable-panels
- GPL 兼容性：https://www.gnu.org/licenses/license-list.html 、https://www.apache.org/licenses/GPL-compatibility.html
