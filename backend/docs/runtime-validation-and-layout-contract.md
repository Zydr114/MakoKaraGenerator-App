# 固定运行验证与布局应用合同（F8b，0.48.0）

2026-10-08。承接[修复计划](code-review-remediation-plan.md) §11／ADR-0178；正式会话所有权继续遵循[宿主合同](runtime-hosting-contract.md)。本文记录已经接通的用例边界，旧作者窗口、共享视图模型和编译绑定继续F8c。

## 固定输入的双输出执行器

[RuntimeFrameValidator](../src/MakoKaraGenerator.Runtime.Hosting/RuntimeFrameValidator.cs)只接收既有`IRenderFrameSessionFactory`、固定`ProjectSnapshot`、正整数`RuntimeValidationPlan`、可选进度及取消。它不读App状态、控件、当前歌曲或本地化资源，不创建试样媒体。

每种输出创建独立正式会话，以Final质量逐帧执行。时间为整数`index * 1_000_000 * denominator / numerator`，保持现有向下取整规则；实际像素尺寸、时刻和缓冲必须属于固定快照。返回帧每次释放，会话在切换模式、失败或取消返回前释放；忽略取消的后端必须先返回到安全点，不提前报告关闭完成。

[结构化结果](../src/MakoKaraGenerator.Runtime.Hosting/RuntimeValidationResult.cs)保留已成功帧数、输出模式、整数时刻及完整发生记录。Issues继续既有汇总去重规则；实际作者同文多次报告仍分别出现在Diagnostics。编译来源、材质、图及源码位置作为类型化字段返回，普通后端消息保持，宿主不拼中文输出标签。作者Error、编译失败、非法像素及正式资源失败保持失败门禁；取消向调用者传播，不伪造Error。

无GUI调用者可直接使用：

```csharp
var validator = new RuntimeFrameValidator(sessions);
var result = await validator.ValidateAsync(snapshot, new RuntimeValidationPlan(360),
    progress, cancellationToken);
```

## 作者试样与自有媒体

`Runtime.Hosting.Authoring`包含[TemplateSampleScene](../src/MakoKaraGenerator.Runtime.Hosting/Authoring/TemplateSampleScene.cs)、[ScriptSampleBuilder](../src/MakoKaraGenerator.Runtime.Hosting/Authoring/ScriptSampleBuilder.cs)和[ScriptSampleValidator](../src/MakoKaraGenerator.Runtime.Hosting/Authoring/ScriptSampleValidator.cs)。试样歌词、640×360／30fps／12秒内容、目标Key／真实绑定、固定种子及正式PresetApplications投影保持。作者请求只持有固定配置、无标签的目标和材质槽枚举；目标Key和绑定均须属于当前固定试样。默认参数、采用修订、相对对象身份及分支映射仍走既有领域投影，不读取工程歌曲。

[TemplateSampleMedia](../src/MakoKaraGenerator.Runtime.Hosting/Authoring/TemplateSampleMedia.cs)生成真实48kHz PCM、固定图片及实际音频分析。[TemplateSampleLease](../src/MakoKaraGenerator.Runtime.Hosting/Authoring/TemplateSampleLease.cs)拥有根目录下自己的唯一子目录，最多生成一次；并发关闭共享同一等待任务，生成失败或取消后仍清理。关闭等待生成结束，只删除自己的目录，不删除根目录或其他试样。正式验证先释放两个会话，再释放媒体；模板试播仍先等待加载、播放器和正式预览释放，再关媒体租约。

`ScriptSampleValidator`固定普通／透明各360帧，共720帧；纯执行器允许其他明确帧计划。App的`ScriptRuntimeSample`／`ScriptRuntimeValidator`仅捕获原UI请求、转换进度／结果及格式化中文标签；原VM注入、运行按钮、分页、取消及关闭等待保持。`TemplateSampleFactory`仅映射显示标签并调用正式作者服务；`TemplateMaterialSlot`迁到作者模块，枚举值保持。试样文字属于固定内容，不是控件文案。

## 布局变换与UI名称

[BasicLayoutOperation](../src/MakoKaraGenerator.Application/BasicLayoutOperation.cs)、四种预设几何及片段引用映射迁入Application。生产Application仍只引用Core，不引用宿主、Media、Skia或GUI；迁入原像素回归所需Skia只属于测试项目。

调用者显式提供`BasicLayoutNames`，新容器、槽位及所选／保留片段使用这些名称；已有工程名称不改写。`BasicLayoutError`返回枚举及完整领域校验、模板名或冲突细节，不读取Loc。App的`BasicLayoutUi`／`BasicLayoutLabels`使用原资源键提供名称和失败文案，不另造一套工程状态。

预演仍在一个工程事务中完成：原片段ID留给最早未选部分，未选成员保留槽位、显示时钟和原布局；直接片段绑定按拆分映射，相对成员实例身份与种子保持。Application范围配方不能安全拆分时整次拒绝，失败和无变化返回原工程。MainWindow仍先展示影响并确认，再由EditorViewModel检查源引用并提交一次撤销；过期预演、取消和无变化均不新增历史。

## 验证与版本边界

无GUI宿主16、Application布局21、App相关56项通过，新增正式回归16项；原布局17项迁至Application，像素、存储往返及固定身份断言保留。真实App普通／透明各360帧、24／30／60fps六个短片全部68编码输入帧及完整解码继续通过。Headless通用界面332、专用真实布局控件／确认29及诊断分页11个断言通过，均为有限内存平台流程。

四项成功编译后变异触发实际失败：减少帧计划、按消息删除发生记录、删除未选成员调度上下文、媒体生成未结束即清理。全部源码原字节恢复后重新构建并验证。细日志、TRX、SHA、失败构建及恢复路径见`artifacts/code-review-repair/f8b/`；首次Headless构建因工作盘空间不足失败，临时盘隔离runner完成验证，没有把失败构建的旧产物当作成功。

MINOR0.48.0对应新增公开执行器、作者服务及Application布局用例；作者SDK、中立渲染接口、工程／歌词／模板28／16／6、配置1、包profile2及固定依赖保持，无格式迁移。按用户要求暂不跑全量；本批未启动或操作原生用户窗口，不扩大为Windows、物理输入或用户连续人工验收。F6b已记录的剩余UI延迟不由本次职责迁移推定解决；F8c及最终连续路线继续。


## 自动 ruby 水平间距（0.97.0）

`MakoKaraGenerator.Text/RubySpacingSolver` 对完整行shaping和聚合ruby墨迹求解，Skia的共享行布局入口统一服务绘制、SceneLayout、TokenGeometry、SceneFrame和FamilyTransforms。水平最小gap固定1布局像素；只约束垂直墨迹严格相交的所有此前ruby，跨无注音token同样检查。安全token/cluster刚性组整体平移，保留glyph和自然advance，ruby锚点采用原始自然边界加位移；新增gap不能吞入前token的End锚点。正文XAt/progress path的间隙归前token的无墨迹区间。

spacing不读取segment时间或progress；无时间/有时间同文本布局相同，segment仍只裁切完整聚合ruby。无冲突原布局直接保留，既有无碰撞legacy golden不重录；冲突画面允许预期改善。无法合法拆分的共享cluster组内冲突、非单调/不精确映射和非法坐标内部保守回退，不扩张公共诊断合同。

联合墨迹继续经过原BuildGeometry全家族宽度缩放及世界矩阵；geometry与实际draw units必须同源。宽度限制不等于自动阻止用户变换出画，不增加描边/阴影几何外扩或完整命中API。验收须分别提供手算合成glyph oracle、原36帧无碰撞像素/PNG/geometry golden、实际碰撞画面与几何、匹配Worker/HTTP证据；历史TTML及综合验收数字不并入本批。范围与独立门槛见[实施计划](backend-ruby-spacing-plan.md)。
