# SukiUI 7.0.1／Avalonia 12.1.3 兼容关卡

2026-10-06 独立兼容验证，2026-10-07补充生产迁移。独立探针与产品验收分别记录；生产UI已迁移至0.41.0，具体证据见 [产品验收](ui-redesign-acceptance.md)。用户已确认 [P0 交互原型](../../design/ui-redesign-prototype.html)，施工合同见 [迁移计划](ui-redesign-and-suki-migration-plan.md)。

## 固定输入及复现

正式 NuGet SukiUI 7.0.1，仓库提交 `495a238268b8778ecd71b20f6727e82a17df38da`；Avalonia／Desktop／Wayland／Skia／ColorPicker／Themes.Simple 均为 12.1.3，DataGrid 为已有的 12.1.2，.NET 10.0。包本身是 net8.0。

```bash
bash tools/ui-suki-compatibility/run.sh --native
```

脚本生成独立 `artifacts/ui-suki-compatibility.*` 工程（首轮历史探针位于 `/tmp`）、配置／缓存／状态目录及日志，不修改产品程序集集合或用户库。引用现有产品控件，使用既有测试友元程序集名访问 DemoContent；不加载 SemiTheme 或 Material 模拟层。原生探针使用 niri IPC 调整和截图自己的窗口，需要正在运行的 niri／Wayland 会话；现在默认拒绝运行，只有显式传入`--native`才会启用桌面自动化。用户已要求停止本轮窗口测试，代理不得自行运行这一入口。退出码和截图只验证自动检查，不能替代物理输入。

最终运行目录 `/tmp/makokara-suki-compatibility.STTkpG`，build.log **0 警告／0 错误**，run.log 自动检查通过。实际窗口客户区 **1200×820／760×640**、RenderScaling **1.25**；以合成器截图检查可见颜色、中文、长诊断、画布和时间轴。离屏 RenderTargetBitmap 不完整捕获 Suki 合成动画与背景，本批视觉证据只采用合成器截图。

## 已验证和接入要求

- SukiWindow 实际创建／关闭、Blue／zh-CN 主题、静态 Flat 背景，深浅主题切换。
- TextBox 焦点、Unicode 文本和选择；NumericUpDown 数值；ComboBox 弹层和选择；Flyout 打开／关闭。
- SukiDialogHost／SukiDialogManager 显示和取消；SukiToastHost／SukiToastManager 通知。
- TabControl 切换、Expander 长诊断、ListBox 选择、DataGrid 两列生成；颜色组件可见且换色。
- 当前正式 KaraokeCanvas 显示实际 DemoContent 快照；TimelineControl 自绘标尺／播放头可见，尺寸未受主题模板破坏。本关卡未声称通过完整打轴拖动和播放链。

**应用资源必须先于 SukiTheme 初始化。** 固定源码的 SetResource 写入 Application.Resources；在 SukiTheme 后替换整个 Resources 会清除强调色，造成选中项和进度条使用错误颜色。当前探针已按官方 Demo 的顺序接入。

**ColorView 需要显式上游模板。** 固定版本 Suki 包提供 ColorPicker 主题；官方 Demo 先加载 `avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml` 再加载 SukiTheme。仅装 SukiTheme 的 ColorView 没有完整可见内容。当前按官方示例补齐，不能用 Color 属性赋值成功冒充控件已绘制。

这些是接入要求，未更改固定包版本、未修改上游源码。生产迁移已遵循这两项要求；逐窗口、动态控件、输入和输出的实际结果另见产品验收。

## 许可证与依赖

| 新增运行依赖 | 版本／许可证 | 平台与资产 |
|---|---|---|
| SukiUI | 7.0.1；固定提交 LICENSE 为 MIT（nupkg 未声明 license 字段） | 受管 net8.0；没有额外平台 native 库；使用现有 Avalonia／Skia 图形栈 |
| Avalonia.Themes.Simple | 12.1.3；MIT | 受管原生控件模板，无独立 native 资产 |
| Avalonia.Controls.DataGrid | 12.1.2；MIT | 原有集中固定版本，现在作为 Suki 传递依赖参与实际运行 |
| System.Linq.Dynamic.Core | 1.7.2；nuspec 声明 Apache-2.0 | .NET 10 走受管路径，无 native 资产 |

Suki 自带 Quicksand／Roboto 字体资源；上游字体分别使用 SIL OFL 1.1／Apache-2.0，发布时随第三方通知归档。中文与 emoji 继续采用本机 Noto 回退，字体配置和视频字体链保持分离。产品迁移不会新增系统字体安装要求。

NuGet 实际解析沿用 SkiaSharp 3.119.4、HarfBuzzSharp 8.3.1.5 及 Linux native 包，没有两套版本。Suki 声明 Avalonia 最低 12.0.3／DataGrid 12.0.0，不代表其他任意组合通过；本记录只针对上述固定组合。

[官方包](https://www.nuget.org/packages/SukiUI/7.0.1)、[固定源码许可证](https://github.com/kikipoulet/SukiUI/blob/495a238268b8778ecd71b20f6727e82a17df38da/LICENSE)、[官方 Demo 样式入口](https://github.com/kikipoulet/SukiUI/blob/495a238268b8778ecd71b20f6727e82a17df38da/SukiUI.Demo/App.axaml)。

## 待验证

真实中文 IME 组合／候选与提交、键盘 Tab／弹层焦点循环、物理指针取色、屏幕间 DPI 切换、Windows，均**未完成人工验收**。本机 1.25 合成器缩放已经记录，不能扩大为任意 HiDPI／多屏通过。

P0 原型脚本通过新建、显式作用范围、任务切换、草稿保留、关闭取消／放弃、首页离开门禁、主题、导出取消和恢复的 DOM 交互检查；测试依赖 jsdom 26.1.0 安装在 `/tmp/makokara-ui-prototype-check`，不加入产品依赖。当前电脑工具没有可用浏览器，浏览器像素与物理交互不记为自动通过。

```bash
NODE_PATH=/tmp/makokara-ui-prototype-check/node_modules \
  node tools/ui-suki-compatibility/prototype-check.cjs
```

## 无桌面验证入口

用户反馈原生探针影响桌面使用后，已停止窗口自动化。以下检查使用Avalonia.Headless12.1.3内存平台及Skia离屏绘制，不连接X11／Wayland、不创建桌面窗口；依赖仅存在于临时验证项目。检查短窗口内部列表高度、保存按钮、长消息、滚动可达性，以及深浅主题实际弹层模板的背景／像素、页签选择和按钮尺寸，不冒充合成器像素验证。SukiWindow在Headless没有原生Popup实现，因此其实际Flyout内容挂到普通内存Window的overlay入口验证呈现器；主窗口弹层的物理位置和焦点仍由用户人工复查。

```bash
bash tools/ui-suki-compatibility/layout-check.sh
```

新增依赖及字体许可证副本见 [第三方通知](../../licenses/suki-ui/README.md)，构建与发布复制至 `licenses/ui`。
