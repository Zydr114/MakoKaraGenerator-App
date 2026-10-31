# R2 语义会话与诊断接缝

> 2026-10-03；当前实现基线 0.9.6。本批设计先于代码，按已接受 R0 §4／SDK 生命周期实施；完整工作区／持久包及 R5 出口仍后置。
> 用户已确认：Diagnostics.Report(Error, ...) 使本次求值失败并阻止导出，Info／Warning 仅记录。
> 0.45.5接续F7a：每重放代次拥有一次校验的固定事件表，历史按可见数量共享，Records保持公开数组兼容入口并按需物化；120Hz／精确事件、同刻回调、取消及输出重建语义保持。合同和独立端点／双输出成本证据见[历史前缀](scripting-history-prefix-contract.md)／ADR-0175。0.46.0接续F7b：完整诊断以不可变分块／发生身份共享，正式消费者按代次及序号读取新后缀；旧完整数组仍按需取得，分数与绘制报告独立、Error门禁保持，见[诊断快照](scripting-diagnostic-snapshot-contract.md)／ADR-0176。按用户要求不跑全量、不启动用户窗口；F8及最终验收继续。
> 本文保留 0.9.7 分批实施证据；**0.9.11 已接通两个视觉入口及实际圆点输出**，当前边界与最终几何协议见 [视觉阶段合同](scripting-r2-visual-stages-contract.md)／ADR-0075，完整 R2 尚未完成。

## 1. 本批接通的生产路径

六阶段中共享状态先接通 Initialize→OnEvent／Advance→Evaluate→Dispose。另两个视觉阶段需要最终光标／映射／发射／粒子协议，继续按 R2／R6 实施，不提供假的空粒子或无操作视觉运行时。

- 同一个入口实例接收源事件并输出样式，不为事件和画面创建两份计数器。
- 固定时间网格仍为已接受 120 Hz：floor(n×1,000,000/120)，以商余数避免中间乘法溢出。
- 事件精确时刻并入网格。每边界先 Advance([a,b))，起点输入／History 截至 a，再按固定事件顺序派发 b，再纯 Evaluate(b)。
- 任意帧 t 使用最后处理边界 b≤t 的共享状态与 t 的真实进度，不额外 Advance 到 t。
- 同帧重绘不派发事件；后退／显式重新建立会话从 0 重放。参数／源码／目标变更由新的固定工厂处理，不可修改正在执行的闭包。
- 同时刻事件逐个纳入已处理历史再派发，OnEvent 读到当前事件及之前事件，不暴露尚未处理的同边界后项。订阅只筛选回调，完整只读工程历史不按输出绑定裁剪。
- Initialize 在 0 一次；0 的事件不制造负区间或零长 Advance。

原 PureFrameSourceFactory 仍是纯外观接缝。作者请求历史或实现有状态入口时明确要求语义会话，不能静默跳过 callback。新的语义工厂是 R2 受控接缝，不代表工程格式／GUI 已能保存脚本实例。

## 2. 上下文与共用外观执行

新增 SimulationStep、StateContext、EventContext；FrameContext.History／LastSimulationBoundaryUs 来自真实会话。纯帧模式没有虚构历史，读取它时报告 TimelineSessionRequired。所有上下文固定 Project／Parameters／Resources／Random；State 输入按 step.StartUs，事件身份／时间来自同一强类型事件。

抽出共用字形／样式帧求值器，纯外观与语义会话使用同一 Prepare→PreparedRenderFrame 路径；不在 Skia 中调用作者代码。完整源和场景投影不受输出绑定限制，源订阅单独验证。纯帧／语义失败均结束 writer，取消后旧实例状态不继续发布；后续请求须从固定输入重建。

真实 RepeatEmphasis 使用 SDK §6.2 同一源码：实际 TokenStarted 按 Occurrence 去重，精确 ordinal 文本，分支／工程计数可选；旧句仍使用自己的次数。前奏／槽位／静态显示不增加计数。当前仅受控源码产物，不是 .mkfx／模板／工作区交付。

## 3. 诊断

中立 severity／code／message／sourceLocation 值，不引入 Roslyn、Core 或 UI 类型。宿主追加实例、固定产物、阶段、整数时刻。Reporter 只在本次阶段内有效；退出后不允许保存 reporter 并在下一阶段假冒旧上下文。

Error 在阶段结束前阻止发布输出；作者 catch 或返回不能把已经报告的 Error 改成成功。Info／Warning 留在不可变准备帧诊断中，不改样式。异常与 Error 仍沿 EffectFailureException 传播，不能进入普通编码占位 catch。Dispose 失败同样释放所有模块引用，再报告，不保留旧代次。

## 4. 本批验收

1. 固定网格精确值、事件分割、边界及 long 极值；不靠浮点计算时间。
2. 事件／Advance／Evaluate 次序、同界历史前缀、分数帧、重绘、跳转、重建原因与取消。
3. 真正 RepeatEmphasis 源码编译并进入共享 Skia 后端，第二／第三次颜色和视觉放大与独立静态外观／等价变换对照。
4. 分支／工程计数、绑定之外的订阅、隐藏／前奏／缺时间／槽位及独立会话；24／30／60 fps 与直接中段请求同一时刻状态一致。
5. Info／Warning、Error 即使被作者 catch 仍失败、阶段失效、错误定位、事件／推进／初始化／求值／释放异常与取消清理。
6. 全量 build／test；同步实际版本／roadmap／ADR／AGENTS；直接启动当前构建。

完整六阶段、视觉状态／光标／映射、行／组／标记目标、材质／图、持久包、音频分析、UI 与六包成片继续保持原范围。本记录不把受控语义测试当作完整 R5 或 R6 已完成。


## 5. 0.9.7 实际实现与边界

- SDK 增加 StateContext／EventContext、SimulationStep、FrameContext.History／LastSimulationBoundaryUs、DiagnosticReporter／EffectDiagnostic／RuntimeDiagnostic／SourceLocation。纯帧读取 History 报 TimelineSessionRequired。
- TimelineFrameSourceFactory 固定实例、源订阅与重放原因；FrameSession 串行推进整数网格及精确事件，同一实例回调与样式输出。FrameEffectHost 是纯帧／语义工厂共用的模块、范围、几何、writer 和合并实现，不新增程序集或可写源层。取消／失败清空模块引用，下次重建。
- 实际起唱累计的 [RepeatEmphasis 源码](../samples/ScriptEffects/RepeatEmphasis/src/RepeatEmphasis.cs) 与 SDK §6.2 完全相同。前奏、槽位、未定时字和隐藏源遵循真实演唱事件，基础字与注音不重排地一起缩放。
- 准备帧保存不可变非失败诊断；作者 Error 保留逻辑位置并经 EffectFailureException 传播。释放所有模块后报告具名 Dispose 失败；当前只有逻辑位置／实例／产物身份，持久包／export／目标定位待后续接入。
- 中途脚本失败测试先复现原目标被部分 MP4 覆盖，编码器现以同目录唯一临时文件执行，成功后发布，失败等待管道／进程退出并清理。测试受控注入真实语义会话和 Skia；尚不代表 GUI 或 VideoExporter 已持久读取实例。

本批不会将没有视觉／发射类型的阶段伪装为空实现。共享回放目前从 0 重建；完整 R5 检查点／包热替换、未来查询、参数历史与分析器仍后续实施，性能基准后置。

### 5.1 验证入口

```bash
dotnet build MakoKaraGenerator.slnx -m:1
dotnet test MakoKaraGenerator.slnx --no-build -m:1
```

新增语义测试位于 SemanticSessionTests；重复效果生产像素位于 RepeatEmphasisSessionTests；真实 Error 编码测试位于 EffectFailureExportTests。最终数量和构建结论记录在 roadmap §18.36 和 R2 记录 §3.5；日志 `/tmp/makokara-r2-session-build.log`、`/tmp/makokara-r2-session-tests.log`。

当前构建已直接启动，PID **186388**，日志 `/tmp/makokara-f93b-r2-session.log`；版本 0.9.7，未见托管启动异常。完整脚本 UI 尚未交付。

## 6. 0.26.0 R5 运行来源与作者提示接续

上文关于仅实例／Artifact 身份和包来源待接入的描述保留为 0.9.7 历史。当前真实包会话通过 `CompiledFrameEffect.PackageIdentity` 从固定包与控制器声明取得 `EffectPackageIdentity`，七阶段运行诊断和失败、required 音频初始化、图／材质绘制及聚合释放都保留 PackageId／Revision／ExportId；独立源码接缝保持 null，不伪造包。

准备帧的 Info／Warning 现在进入 `RenderFrame.SceneDiagnostics`，普通文字与图／材质合成都先保留作者诊断，再追加实际绘制提示，缓存和导出报告继续保留。修复前七阶段的包来源为 null，正式普通文字帧的作者诊断为空；本批真实工程保存／重开、共享 DLL 不同来源、主 Render、缓存及视频回归复现并验证修复。Error 和取消规则保持，作者位置不丢失，200 ms 真实 Error 编码中止并保护旧产物。完整 R5 的 FPS／两个文字效果／音频／重建组合仍后续，具体合同见[运行诊断来源](scripting-runtime-diagnostics-contract.md)／ADR-0119，实际结果见 roadmap §18.86。

## 7. 0.26.1 真实包与持久会话组合

RepeatEmphasis 现有公开源码补齐目录清单／README 和摘要，可独立分享为 `.mkfx`；上文“仅受控产物”是 0.9.7 历史。与 WordEmphasis 一起保存重开后，在正式像素会话中验证 24／30／60 FPS、音频、前奏、旧句序号、计数范围／源订阅、重绘、回退、取消后重建及独立 Export；实际 MP4／Alpha 编码保留音轨并同帧比较。required 仅要求初始化 Available，音轨外与 optional 不可用分支不制造静音。没有修改七阶段或重复计数源码，参数／源码撤销及热替换／释放组合和完整 R5～R8 仍继续。具体例包、断言及证据见[会话组合验收](archive/acceptance/scripting-session-combination-acceptance.md)／ADR-0120、roadmap §18.87。
