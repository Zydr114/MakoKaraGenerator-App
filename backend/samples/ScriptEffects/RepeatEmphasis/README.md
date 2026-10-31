# 重复文本强调控制器包

`src/RepeatEmphasis.cs` 与公开 SDK 合同 §6.2 使用同一作者源码。真正的 `TokenStarted` 按 OccurrenceId 去重，按 ordinal 精确文本累计；前奏提前显示、暂停重绘和槽位保留不增加次数。每次起唱保存自己的序号，保留的旧句不会读取后来新的总次数。

控制器导出为 `repeat-emphasis`。默认参数：`countScope=branch`、`scaleStep=0.08`、`maxScale=1.8`、`darkenStep=0.06`、`minBrightness=0.35`。首唱为第 1 次，第 2 次开始增加缩放及降低已唱亮度。计数范围可以改为 `project`；绑定决定写入对象，源订阅单独决定收到哪些起唱。要累计绑定以外的重复源，请保留工程订阅；选择“绑定源”则只累计实际订阅源。

在编辑器脚本效果工作区导入本目录，新增 `repeat-emphasis` 实例，设置绑定／订阅／参数，启用并执行“验证工程效果”，再应用草稿。新实例默认禁用。工作区可分享为 `.mkfx`；工程保存后持有完整包修订，移动工程不再需要原目录。公开 API 使用 `EffectPackageStorage.ReadDirectoryAsync`、`EffectPackageDefinition.Read` 和 `CompiledProjectEffects.CompileAsync`／`CreateFactory`。必须使用时间线会话，纯帧模式不能跳过回调并伪造计数。

本例显式输出已唱纯色，要求元素的基线已唱材质是 solid；渐变和包材质会给出具名错误并阻止导出，不采样某个像素当基色。基础字与注音一起缩放，不修改文字、时间或排版。与逐字强调一起启用时两份缩放相乘；样式按工程实例顺序写入。

源码和 README 同属包修订，修改后需重新计算清单摘要。帧率、持久化、前奏、重绘、跳转、音频组合和取消重建证据见 [R5 组合验收](../../../docs/scripting-session-combination-acceptance.md)。完整 R5～R8 与最终 GUI 验收仍按后续计划进行。
