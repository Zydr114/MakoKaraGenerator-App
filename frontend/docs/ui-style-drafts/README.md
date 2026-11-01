# 界面风格稿

三份方向不同的风格稿，覆盖开屏、新建工程、歌词、编排、导出五个画面，供挑选实际采用的一份。

页面分区、交互和用词都沿用 [`../web-ui-design.md`](../web-ui-design.md)，这里只改视觉和布局。
稿件是手写的 HTML 与 CSS，用来定方向，不是组件库的实际渲染；图标是为稿件手绘的占位，
歌词和工程都是虚构的演示数据。

## 查看

```sh
xdg-open frontend/docs/ui-style-drafts/index.html
```

- `index.html`：三份稿的缩略图、对比和组件库许可表。
- `a-dark-stage.html`、`b-songbook.html`、`c-desk-stage.html`：各一份稿。地址后加 `#home`、`#new`、
  `#lyrics`、`#orchestration`、`#export` 切换画面；页面底部的浮动条可以在稿件和画面之间跳转。
  地址加 `?shot` 会隐藏浮动条，方便截图。
- `shared/mock.js`：共用的演示数据、图标和渲染函数（行列表、文本单元、逐字预览、时间轴、渲染画面、工程列表）。
- `shared/base.css`：共用的重置、时间轴结构和渲染画面。颜色和字体由各稿自己定义。

稿件按 1440 × 900 设计和检查。

## 共同前提

**密度。** “现状拥挤杂乱，要有秩序”：比 mdui 默认紧，比视频剪辑软件松。靠分组、对齐和统一的控件尺寸
建立秩序，而不是靠加大留白。

三份稿都做了这些布局改动：

| 现状 | 改为 |
|---|---|
| 底部叠着 4～5 条横栏 | 一条播放条（播放、速度、循环、本页开关、缩放、吸附）加时间轴 |
| 所选文本单元的时间字段随编辑区滚动 | 固定在行编辑区底部 |
| “歌词分配”占一条底部横栏 | 移到编排页左栏（A、C）或画布下方（B） |
| 56px 描边输入框挨着原生下拉框 | 控件统一 24～30px 高 |
| 快捷键和提示常驻在各处 | 收进一个“快捷键”入口；状态只在一处显示 |
| 白色平面，没有层级 | 每份稿各自用明度或材质区分层级 |

**共同记号。** 卡拉 OK 的逐字填充：标志 “M” 从左到右被填色，同样的填充出现在时间轴的文本单元、
波形已播放部分和逐字预览里。强调色只用在“已唱、当前、主操作”上。

**字体。** 界面用 Inter 和 Noto Sans CJK（均为 OFL-1.1）；时间码用 Inter 的等宽数字，不用等宽字体。
渲染画面属于工程内容，不随界面主题变化。

## 三个方向

### A 暗场

深色，像剪辑软件。整块石墨灰，面板之间只留 1px 缝。

| 角色 | 值 |
|---|---|
| 底与面板 | `#16171a`、`#1e2024`、`#262930`、`#30343b` |
| 文字 | `#e6e7ea`、`#a2a7af`、`#6b717a` |
| 强调（琥珀） | `#eeb34f` |

- 页签放在 40px 标题栏正中，去掉左侧导航栏；底部 24px 状态栏放连接、修订、快捷键和 GPU。
- 开屏：左侧是开始面板（新建、恢复、最近打开），右侧是“继续工作”卡片和工程目录。
- 编辑页：三栏加底部区，属性面板用“标签在框内”的紧凑字段。
- 适合长时间打轴、在暗处看画面；代价是日文注音和中文小字在深底上更费眼，首页和导出显得沉。

### B 歌本

浅色，像排好版的歌本。白纸放在浅灰桌面上。

| 角色 | 值 |
|---|---|
| 桌面与纸面 | `#eceef1`、`#ffffff`、`#f6f7f9` |
| 线 | `#e2e5e9`、`#cfd4da` |
| 文字 | `#1b1f24`、`#59616b`、`#8b929c` |
| 强调（靛蓝） | `#2c4d9e` |

- 顶栏正中是真实的制作顺序“1 歌词、2 编排、3 外观”。
- 行列表按歌本排印：行号悬在页边，注音在上，译文在下。属性面板做成表格式的属性单，表单里标签在字段上方。
- 开屏是居中的单列：三张“继续工作”卡片（第三张放新建和恢复）加工程目录。
- 适合以文字为主的工作，新用户最容易上手；代价是预览四周是白的，判断画面明暗不如暗底准。

### C 台前幕后

动手处浅，看画处暗。填写、选择、列表在浅色台面上；画面和时间轴在暗场里，并连成一整块。

| 角色 | 值 |
|---|---|
| 台面、面板、输入框 | `#e9eaec`、`#f6f6f7`、`#ffffff` |
| 文字 | `#1f2226`、`#61666e`、`#9196a0` |
| 暗场 | `#1b1d21`、`#222429`，线 `#34383e` |
| 强调（青绿） | 浅底 `#13806f`，暗底 `#3cc9b3` |

- 面板之间留 6px 缝，像放在台面上的板子。暗色的预览板伸进缝里和时间轴相接，接缝处补内凹圆角：
  歌词页是 L 形（逐字预览加时间轴），编排页是 T 形（画布加时间轴）。
- 页面切换是顶栏里的分段按钮，紧跟工程名。
- 开屏是左右两栏：左边找工程，右边暗场显示选中工程的画面、信息、打轴进度和最近导出。
- 新建工程对话框左边填写，右边暗场检查文件（波形、识别结果、前几行歌词）。
- 导出页左边设置和任务，右边暗场看首帧、末帧和导出范围。
- 同一套控件靠 CSS 变量作用域（`.dk`）在浅底和暗底之间切换，不需要两套组件。
- 适合既要长时间读写日文和注音、又要准确看画面的工作；代价是拼接处的细节必须做好，否则显得拼凑。

## 组件库

仓库计划以 GPLv3 开源。MIT、ISC、BSD 可以并入 GPLv2 和 GPLv3；Apache-2.0 只能并入 GPLv3，
与 GPLv2 不兼容，所以本仓库应写 GPLv3 或“GPLv3 或更新版本”，不要写“仅 GPLv2”。
OFL 字体可以随软件一起分发，字体本身仍按 OFL。

| 库 | 许可 | 与 GPLv3 | 说明 |
|---|---|---|---|
| React Aria Components | Apache-2.0 | 兼容 | 无样式。列表多选、拖放排序、树、带单位的数字字段、键盘操作都很完整 |
| Base UI | MIT | 兼容 | 无样式，MUI 团队维护，已到 1.x，shadcn 的默认底座；拖放和树要另配 |
| Radix Primitives | MIT | 兼容 | 无样式，生态最大，与 Base UI 同类 |
| Ark UI | MIT | 兼容 | 无样式，基于状态机，多框架通用 |
| Mantine | MIT | 兼容 | 自带样式和大量组件；做成这三稿的样子需要覆盖大部分样式 |
| Blueprint | Apache-2.0 | 兼容 | 为高密度桌面界面设计，自身风格强，改主题成本高 |
| Semi Design | MIT | 兼容 | 组件全；React 19 需要用单独的包 |
| Fluent UI v9 | MIT（代码） | 仅代码兼容 | 字体和图标资产另有许可，不宜随 GPL 软件分发 |
| react-resizable-panels | MIT | 兼容 | 可拖动的分栏和底部区高度 |
| dockview | MIT（核心） | 仅核心兼容 | 可停靠面板；8.0 起另有商业许可的 Enterprise 附加包，不要引入 |
| Lucide／Phosphor／Tabler | ISC／MIT／MIT | 兼容 | 线性图标，24 网格 |
| Inter、Noto Sans CJK、JetBrains Mono | OFL-1.1 | 可随附 | MiSans、HarmonyOS Sans 是专有许可，不要打包 |
| mdui（现用） | MIT | 兼容 | 可以逐页替换，不必一次推倒 |

**建议。** 交互层用 React Aria Components，分栏用 react-resizable-panels，图标用 Lucide，外观全部由自己的
CSS 变量决定。三份稿都是自定义外观，带样式的库（Mantine、Blueprint、Semi）反而要花力气去掉原样式。
Base UI 是更轻的备选，代价是拖放和树要另找方案。

**风格上推荐 C。** 这个软件一半时间在读写日文和注音，一半时间在看画面和时间轴，C 让两件事各在最合适的
底色上。想要最“专业剪辑软件”的观感选 A，想要最好上手选 B。

## 来源

许可与版本信息于 2026-11-01 查阅：

- Base UI 与 React Aria 对比：https://www.untitledui.com/blog/base-ui-vs-react-aria
- shadcn 的 React Aria 支持：https://ui.shadcn.com/docs/changelog/2026-07-react-aria
- Radix 的替代品：https://designrevision.com/alternatives/radix-ui
- Blueprint：https://github.com/palantir/blueprint 、https://libraries.io/npm/@blueprintjs%2Fcore
- dockview：https://www.npmjs.com/package/dockview-react 、https://dockview.dev/blog/dockview-enterprise/
- Fluent UI v9：https://www.npmjs.com/package/@fluentui/react-components
- Semi Design：https://github.com/DouyinFE/semi-design
- Mantine：https://github.com/mantinedev/mantine
- react-resizable-panels：https://github.com/bvaughn/react-resizable-panels
- GPL 兼容性：https://www.gnu.org/licenses/license-list.html 、https://www.apache.org/licenses/GPL-compatibility.html
- 图标许可：https://www.adastack.io/icon-licenses 、https://dev.to/usapopopooon/what-i-didnt-know-about-icon-library-licenses-and-you-might-not-either-30of
