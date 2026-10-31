# 颜色取色交互方案

> **状态：已实施（2026-09-29）**。`ColorSwatchPicker` 与 `ThemePaletteDialog` 共用
> `MaterialColorPickerPanel`：默认只展示少量圆形常用色，展开“高级取色”后才创建 Avalonia
> `ColorView` 及 RGB/HSV、十六进制和 Alpha 编辑区。对应主题资源在
> `Theme/MaterialYou.axaml`、`Theme/Colors.axaml`，文案在 `Resources/Strings.axaml`。
> 颜色写入仍沿用现有草稿、命令和撤销链路。

## 调研与入口盘点

样式工作区的颜色属性行已有十六进制输入框和只读色块；完整渐变使用多行的“位置 颜色”文本；主窗口合成检查器的背景色也有十六进制输入框和只读色块。这三类入口覆盖当前所有可编辑颜色。`RgbaColor` 接受 `#RRGGBB` / `#RRGGBBAA`，其中 Alpha 在**末尾**；Avalonia `Color` 的通道顺序不同，必须按 RGBA 通道显式转换。

Avalonia 官方将 `ColorView` 定位为完整的可嵌入取色面板，将 `ColorPicker` 定位为自带下拉入口的控件。这里采用“现有色块变为按钮 + 弹层内放 `ColorView`”：不会挤占属性检查器的宽度，也可以保持现有文本框。控件本体需 `Avalonia.Controls.ColorPicker`；现有 Semi 主题的取色器模板是单独的 `Semi.Avalonia.ColorPicker` 包。两个包都锁在当前家族版本，沿用 Semi 模板和本项目 Material You 色阶。

新增包均为 MIT 许可证，NuGet 包内只有托管程序集、无新增 native asset；Avalonia 与 Semi 均面向 Linux/Windows 桌面，当前锁定组合 `Avalonia.Controls.ColorPicker 12.1.3` 与 `Semi.Avalonia.ColorPicker 12.1.0.1` 已能恢复、构建。Semi 的同版本主题包于 2026-07 发布，维护状态随当前 Semi 主主题版本同步核对。

资料：[Avalonia ColorPicker 文档](https://docs.avaloniaui.net/controls/input/selectors/colorpicker)、[Avalonia ColorView API](https://docs.avaloniaui.net/api/avalonia/controls/colorview)、[Semi 安装说明](https://github.com/irihitech/Semi.Avalonia#how-to-use)。

## 交互与数据约束

1. 所有可点色块使用一个可复用按钮。点开先显示少量常用色的圆形色板；展开“高级取色”后才显示交互式颜色谱、RGB/HSV、十六进制和 Alpha 控件。再次点击、点外部或按 Esc 关闭。按钮保留键盘焦点与可读提示。半透明颜色在色块中可识别。
2. 样式颜色属性：选色立即写入现有字段，复用字段校验和草稿预览链路；可选颜色为空时，以当前歌词主色初始化取色器。原文本框继续接受十六进制输入；禁用效果和恢复继承仍是独立动作。
3. 渐变：停止点列表给每个停止点独立色块，点击只更改该停止点的颜色；下方“位置 颜色”文本仍可直接编辑位置和颜色。文本暂时非法时不覆盖用户输入，也不从错误内容猜测停止点。
4. 工程背景色：选色走已有 `SetBackgroundColor` 命令和撤销栈，保持文本框、色块与画面同步；在浮层打开前，以工程当前生效色初始化，而非尚未通过校验的输入片段。
5. 色值往返采用通道转换，生成文本统一 `#RRGGBBAA`，不依赖 Avalonia 的十六进制解析顺序。浮层内的 Alpha 不改变“未设置”语义；清空仍须走现有按钮。

## 验收

- 样式必填色、可选色、渐变停止点和工程背景色均能用鼠标及键盘打开取色器；改变后草稿/工程预览刷新，关闭重开显示当前值。
- 原十六进制输入仍可用；非法输入不会悄悄写入模型；透明度往返一致。
- 默认态只显示常用色，展开后才出现交互式面板；再次点击、点外部或 Esc 均可关闭，且两个入口使用同一套圆角 Material You 表面。
- 全量构建、全量测试通过（当前基线为 1330 项、0 警告/0 错误）；已完成一次带窗口的 Avalonia 实际加载检查，日志除运行环境的 ALSA/JACK 设备告警外无未处理异常。真机点击、弹层在窄窗口的位置和键盘操作由用户补充验收。
