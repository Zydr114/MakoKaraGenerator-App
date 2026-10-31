# 生产固定字形捕获与图输入适配

> 2026-10-03；0.9.15 已实施目标捕获、中立图 writer 和真实绑定执行适配，依据已接受 R0 §6.2～6.3／ADR-0071 和用户的最后绑定答复（ADR-0079）。完整输出协调器继续；不改生产 JSON，不把捕获接口当完整 R3 出口。

## 1. 固定目标与调用边界

正式 SkiaRenderPipeline 提供只读的 CaptureSceneTarget。目标使用真实 LineTarget／TokenFamilyTarget／TextMarkTarget／GroupTarget，当前 BindingId／CueId 必须存在；元素捕获遵守固定 glyph 独立分区，不按子串重整形。有启用效果的快照仍须有匹配 PreparedRenderFrame，最终层次矩阵、元素族变换、样式及前奏几何与 Render 共用。

调用显式给出捕获框、输入源、区域和配方（注音、描边、阴影、子组）；没有隐藏“完整捕获”的默认值。坐标是 Canvas 逻辑单位，密度来自实际 RenderTarget。捕获框向外对齐像素网格，越过预算报错；新原始覆盖通道不截到旧的 64 px。TargetColor 忠实保留正式 Render 的颜色规则，包括共享字簇源覆盖在旧矩形路径中的 64 px 外扩限制；独立固定单元不走该旧路径。这项既有局限不在此批偷偷改变，后续源覆盖改进应共同调整 Render／捕获并单独记录差异。配方不改源时间、源样式、排版或框；这批是宿主只读输入生产，不授予脚本新的组写权限。

## 2. 通道

- TargetColor：使用当前两态填充、规范渐变、可选描边／阴影及外观／入场 alpha，保留现有真实绘制规则。
- GlyphCoverage：基础字及可选注音的原始字形 alpha；忽略填充、渐变、样式和入场不透明度，位置及独立族变换保持。
- StrokeCoverage：真实外部轮廓，保留宽度／连接／固定字形减去内部；不从填充或描边颜色 alpha 推算区域。
- SungCoverage：当前实际已唱的字形区域；缺逐字时间字不会借后一个字染亮。注音使用自身基础元素进度，前奏标记按实际 elapsed 状态。
- TargetRegionCoverage：显式选择填充、外部描边或两者；不以透明填充决定轮廓内外，也不含阴影。

Coverage 仅存原始 alpha，图执行按 raw 读取；TargetColor 为真实 sRGB 预乘 BGRA8888。Coverage 的几何与 TargetColor 的外观 alpha 是不同输入，后续材质协调器必须按配方合并不透明度一次，不能将原 TargetColor 的 alpha 再当几何乘进去。

## 3. 组、顺序与资源

组捕获在透明独立表面绘制所选真实后代，保持现有源绘制次序；运行父关系用于判断附着前奏是否属于该组／行。子组是否纳入由配方明确。整组不透明度可以对捕获结果一次合成验证，不分别乘到每个后代；共享树／组 writer 的完整调度仍按 R3 后续。

图输入由调用者持有，产物可交给正式 CompiledImageGraph；释放包装和 owned 图像的边界明确。创建前检查真实像素框、单张尺寸及产物字节限制，取消／异常不返回部分图像。原绘制中的 SaveLayer／滤镜临时资源和跨目标／图的统一账本仍待后续协调器落实，不把单张捕获检查宣称为 R0 全局资源出口。

自有 overlay 的捕获依赖真实实例／目标归属元数据，当前不借用全局圆点冒充目标自有粒子；后续按 R3／R6 接入。背景媒体／BackdropBelow、包材质、组图调度、模式去重提示及非 Canvas 空间仍保留完整合同；真实实例图执行失败接缝见 §6。

## 4. 验证

0.9.15 全量 build **0 警告／0 错误**，八项目 **1922 项通过、0 失败／跳过**；新增 Rendering **21**、Scripting **3**、Export **3**。日志 `/tmp/makokara-capture-build.log`、`/tmp/makokara-capture-tests-full.log`。

正式 Render／TargetColor 在行两态、透明 alpha、注音／描边／阴影、最终行／族样式与变换、入场和前奏两种附着、实际等比／非等比密度下逐字节一致。Glyph mask 独立于源透明填充，Stroke 不漏不透明内部；未定时字及注音没有 Sung 区域，前奏依据真实 elapsed；共享合字允许整行、拒绝独立族。真实重叠后代的组捕获一次半透明合成为 alpha 128，逐后代各半透明得到约 191～192，证明两者不同。

真实离线 Roslyn 作者最后绑定、多个实例串接、真实资源／旧 Binding／Cue、Tokens 读行不授写权、生命周期和局部输入预算已验证。真实歌词和注音经同一捕获／图适配的 MP4／ProRes 4444 第 15 帧与离屏结果对比，RGB 平均误差小于 9，透明 alpha 每像素误差不超过 3；中途参数错误带真实实例／代次／ExecuteImageGraph／时刻，停止编码、保留原文件并清理临时产物。这里使用受控编码接缝，不是主画布调度／持久实例 UI 已交付。

三个变异均被断言检出、恢复后全量通过：保留第一次 Bind（`/tmp/makokara-last-bind-mutation.log`）、用旧黑色减除代替与 raw 白色 Glyph 一致的 mask（`/tmp/makokara-capture-mask-mutation.log`，不透明内部残留 alpha 6）、新 raw 外扩恢复 64 px 截断（`/tmp/makokara-capture-expand-mutation.log`）。TargetColor 的旧规则和共享源覆盖矩形限制保留，不以这批测试宣称已解决全部源覆盖边界。

用户已确认：同一实例、同一目标在同一帧重复调用 ImageEffects.Bind，只保留最后一次绑定；多个效果使用不同实例，按工程实例顺序串接。不同目标各自保留绑定；最后一次绑定的参数和配方整体替换先前绑定，不合并遗留参数。该规则不授予新的写入范围。

## 5. 中立图 writer 接缝

FrameWriter.ImageEffects 在 Evaluate 内只写不可变绑定描述，需声明 ImageEffects 能力；不持有纹理，不编译 shader。首批目标为 Line／TokenFamily／TextMark，沿用现有真实装载权限，Tokens 绑定不能写整行；组写权限随后获确认仅工程／显式节点，组图 writer 尚未接通，不借只读 Groups 目录开放。0.11.1 组仿射与捕获共用最终几何，见[组仿射合同](scripting-group-affine-contract.md)。元素族须能按固定字形独立绘制。GraphHandle 和参数中的资产必须属于该实例固定包修订。宿主给输出加真实 InstanceId／ArtifactHash，并按实例顺序存入 PreparedRenderFrame；视觉阶段复制帧时必须保留图绑定。接入生产协调器前，Render 遇到图绑定必须明确失败，不能静默忽略；已有独立图执行器仍可消费正式捕获验证真实像素。

## 6. 真实绑定的执行适配

宿主在帧外把固定 GraphHandle 与已编译图、显式捕获配方和 Canvas 框关联为 SceneImageGraphProgram；它借用 native 图，由创建者释放，不是新增工程格式。ExecuteSceneImageEffect 只接受当前准备帧中已有的真实实例绑定；将真实 Glyph／Stroke／Sung／Region／TargetColor 捕获送入图，异常附加实例、编译代次、时刻及 ExecuteImageGraph 阶段。参数仍由图唯一约束校验；模式旁路不跳过程序验证。相同通道复用一张捕获，预先检查当前活跃输入数量和字节，避免先分配全部输入再发现超限；跨图账本仍未完成。

显式 previousTarget 表示同一目标的上一实例产物，下一实例的 TargetColor 读取它；为空才捕获处理前真实目标。返回对象保有其自建捕获直到图产物使用结束，不释放 previousTarget 或外部借用输入。背景／下层／包图片的输入此阶段仍由调用者提供，不能伪造不存在的媒体或目标后方内容；自动隔离、子图到父图、主画布输出调度和背景旁路 UI 提示继续实施。该适配证明真实脚本描述能执行，不代表 Render 已自动完成完整场景图调度。

## 7. 当前构建启动

已直接启动最终 0.9.15 构建，PID **611937**，日志 `/tmp/makokara-f93b-capture-final.log` 显示版本 0.9.15；niri 确认同 PID 的主窗口（窗口 113，演示工程）。未见托管异常，但日志有 JACK／ALSA 音频设备探测警告，未单独进行音频播放人工验收；不把该提示当作已验证的播放正常或程序失败。本批未新增脚本 GUI，窗口启动不代替完整脚本人工验收。

## 8. 共享中间表面账接续（0.9.16）

本合同 §1／6 的捕获与执行接缝现可传入同一 ImageSurfaceBudgetFrame；Skia 管线可借用固定会话账。实际捕获、图执行／合成、输入别名、跨帧保留、Blur 两张 scratch 和捕获显式图层已按 [资源账合同](scripting-surface-budget-contract.md)／ADR-0080 接通。0.9.16 全量 1938 项通过，包含实际 C# 作者改变外扩后在 200 ms 触发预算失败并保护成片。自动创建的调用帧账只证明该接缝受管，主画布父子协调器后续必须沿用整帧账；旧文字阴影的内部 scratch、字形缓存和 ToShader 终结器等完整 native 内存仍未宣告受控。本合同 §5～7 保留 0.9.15 的历史证据。


### 0.9.17 接续

用户确认后续普通图保留之前的替换范围，以及未纳入捕获的注音／描边／阴影继续正常绘制。合成链原语、绑定级完整顺序／错误及真实工程媒体／底色生产已进入本批；覆盖并集／最终合成沿用共享帧账。排除部件分区、正式父子 Render 协调器及 BackdropBelow 仍待实施，受控编码不代替完整场景出口。规则及验证导航见 [场景合成合同](scripting-scene-composition-contract.md)／ADR-0081／roadmap §18.46。

## 0.12.2 主画布组 alpha 与原始捕获边界

真实组透明度已进入主 Render 的嵌套隔离，孩子图先完成，组 alpha 一次；独立 CaptureSceneTarget 仍是原始字形通道。TargetColor 范围自身或显式后代含有效组 alpha 时报告 GroupOpacityCapturePending，不能返回忽略该输出的旧字形图。祖先组 alpha 不混进子对象的局部原始输入；Coverage 始终保持原始覆盖。后续组图／按配方捕获须消费隔离中的真实部件产物，本批不把背景画面的截图作为 TargetColor。见[组隔离合同](scripting-group-opacity-isolation-contract.md)／ADR-0094。
