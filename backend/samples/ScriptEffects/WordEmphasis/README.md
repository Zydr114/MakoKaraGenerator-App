# 逐字强调控制器包

`src/WordEmphasis.cs` 是公开 SDK 合同 §6.1 的同一份作者源码。生产路径测试直接读取此文件，通过分发的 Roslyn／BCL 引用编译、加载，交给纯帧会话和共享 Skia 渲染；不改用测试私有入口。

`manifest.json` 包含真实文件摘要、唯一参数定义和控制器导出 `word-emphasis`。默认 `peakScale=1.25`、`returnUs=120000`；区间分别为 [1,4] 倍及 [0,2000000] 微秒。允许六种绑定；只输出元素族视觉变换，不重排文字。

在编辑器的脚本效果工作区导入本目录，选择包后新增 `word-emphasis` 实例，设置绑定并启用，使用“验证工程效果”检查运行，再应用草稿。新实例默认禁用；保存草稿不代替运行验证。可以从工作区分享为 `.mkfx`，工程保存后持有完整包，不依赖本目录。基础字与注音一起缩放；无独立字时间的元素跳过，已唱回落只持续到本句离场。

公开 API 可用 `EffectPackageStorage.ReadDirectoryAsync` 读取闭包、`EffectPackageDefinition.Read` 校验声明，并通过 `CompiledProjectEffects.CompileAsync` 和 `CreateFactory` 建立固定工程会话。正式预览和导出都消费这份会话准备帧；本效果只用 Initialize／Evaluate，独立源码试验仍可使用 `PureFrameSourceFactory`。

包中的源码和 README 同属内容修订，修改后需重新计算清单摘要；不改源码或入口的参数修改不会重编译同一 C# 产物。与重复文本强调一起启用时缩放按实例相乘；重复效果只改变已唱纯色。组合回归、帧率／跳转／取消规则见 [R5 组合验收](../../../docs/scripting-session-combination-acceptance.md)。完整工作区和最终成片验收仍按后续计划进行。
