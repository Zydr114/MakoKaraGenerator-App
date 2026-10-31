# 运行诊断的固定包来源（R5 接续，0.26.0）

> 2026-10-05。按已接受实施合同 §9 的诊断身份接续；施工前七阶段回归已复现来源丢失。本批只补全包／修订／导出归属，不宣称完整 R5 出口完成。

## 1. 身份

同一 C# 源码／入口能被多个包、资源修订或导出项复用，ArtifactHash 无法代替包修订。新增中立只读 `EffectPackageIdentity`，保存实际 PackageId、完整 `sha256:` Revision 与运行控制器 ExportId。`CompiledFrameEffect.PackageIdentity` 从固定 `PackageDefinition` 和 `PackageDeclaration` 计算，不另存第二份可写真值。

`RuntimeDiagnostic` 与 `EffectFailureException` 保留该来源；真实包实例始终有值，只有受控独立源码没有包时为 null。错误消息显示包／修订／导出，结构化字段仍保留实例、ArtifactHash、阶段、整数时刻及作者 SourceLocation。图／材质句柄的导出不是运行控制器导出，不能拿来替换诊断来源。

## 2. 传播

Initialize、OnEvent、Advance、Evaluate、EvaluateEmitters、AdvanceVisuals、EvaluateVisuals 的 Info／Warning／Error 和普通异常共用固定来源。required 音频初始化门禁、输入验证及模块创建失败保持同一归属。Error 即使被作者捕获也阻止阶段输出；普通异常没有作者位置时不制造 SourceLocation；OperationCanceledException 仍是取消。

正式Render将准备帧的作者诊断与实际绘制追加的组隔离等宿主诊断一起保留。普通文字和图／材质合成都不筛掉Info／Warning，不在采集层按消息去重。0.46.0／F7b补充[持久快照合同](scripting-diagnostic-snapshot-contract.md)：缓存保留完整稳定快照，原`PreparedRenderFrame.Diagnostics`／`RenderFrame.SceneDiagnostics`仍是按需物化的完整数组；预览、导出和运行验证使用发生身份／增量游标，重复缓存不重复消费，实际同文多次报告仍保留。导出提示继续按原规则汇总，完整记录另存`DiagnosticOccurrences`。提示不修改像素或让合法帧失败。

准备帧中的图绑定、脚本材质最后写入者及组透明度最后写入者保留控制器来源。正式图执行／合成、区域材质、组图拒绝、组隔离警告／失败和资源缺失继续使用这份来源，不能到原生阶段丢失包身份。源材质仍沿 `PreparedMaterialOwner.Source` 及 `SourceMaterialFailureException` 保存真实源引用，不建立假控制器实例。

释放多个实例时，外层聚合失败保留第一个失败的来源，每个内部失败保留各自来源；所有实例及后端仍继续释放，重复 Dispose 保持安全。多目标作者阶段不猜测一个目标，本批不改变 writer、绑定或目标归属语义。

## 3. 验证与边界

真实包经工程保存／重开后进入正式组合根，七阶段分别验证 Error 与普通异常，作者捕获 Error 仍失败。共享 DLL 的不同包／修订／导出保留不同来源，Info／Warning 不改变像素；required 音频、多个包释放、主 Render 材质错误及组图拒绝均验证来源。实际编码在 200 ms 作者 Error 时中止，保留旧输出、清理暂存文件并保留源码位置。

中立公共宿主协议新增来源字段，程序升 MINOR 0.26.0；工程／歌词／模板 schema26／16／5、包 profile2、Shader ABI、SDK 名义版本、程序集集合及依赖不变，不迁移旧工程。后续仍需完整持久化放大／重复 callback／音频组合、24／30／60 FPS、前奏、取消重建和会话热替换／释放验收；R6～R8 与 R3.1 继续。实际结果以 roadmap §18.86 为准。
