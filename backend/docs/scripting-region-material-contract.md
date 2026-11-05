# 区域材质的覆盖与生产接入合同

## SDK v2 Base 独立材质增量（App 0.112.12）

`TextTarget.Base` 可独立写入两态填充、外描边与 `material.uniform`，只修改真实基础字；
Ruby 继续读取最终 Family 外观。Base 先继承最终 Family 中立 `StyleView`，再应用叶覆盖。
显式 Solid／Gradient／Stroke=null 遮蔽同部位父包材质；仅 opacity／affine 写入不改变材质 owner。
继承源材质保留真实源路径，Base Replace 或最终存活 uniform 记录实际脚本实例。

Base 捕获不含 Ruby，Family 及后继 Line 图读取已处理的完整 Base＋Ruby 颜色；Base 不是
新的图绑定目标。材质使用已经应用最终仿射／Warp 的区域，不二次变换输出，局部 alpha 只乘一次。
共享预算／原生捕获失败归材质 owner，Warp 几何错误保留 Warp owner，取消保持原 token。

像素参考遵循下述 raw 区域合同：先合并白色字形覆盖，再着色和应用 alpha。普通半透明
有色字形逐个绘制，在抗锯齿和相邻描边重叠处可以与该区域语义不同，不能直接作为包材质的
逐像素 oracle。当前增量的测试与实际流程证据见
[M09 记录](../../docs/plans/script-sdk-render-m09-evidence.md)；历史版本结果仍归原版本。

2026-10-04；依据已接受 R0／R3 和用户本轮三项答复。本文描述施工合同，实施证据在末尾更新。

## 1. 已确认的行为

- 材质输出限于实际绑定的填充或外描边区域；向外扩散使用元素／行图效果。
- 材质 `TargetColor` 是包内纯色／完整渐变基础外观在该区域内的真实图像。它不读取被替换的旧填充，不形成自身反馈。
- 材质导出必填 `coverageMode`：`unmasked` 表示尚未乘字形覆盖的预乘颜色，`masked` 表示已经覆盖的预乘图像。没有默认模式，不转换旧清单。

两种模式都采用现行 sRGB BGRA8 预乘颜色。`unmasked` 的最终颜色及独立替换覆盖各乘一次原始区域覆盖；`masked` 的颜色及替换覆盖不得超出原始区域，宿主检查并复制，不再次乘覆盖。零覆盖处必须严格透明；非零处最多允许一个字节的现行 BGRA8 舍入差，不夹值或补蒙版来掩盖错误。

## 2. 基础外观和透明输出

宿主从固定 glyph、真实逐字裁切、最终矩阵和实际外描边生成 raw 区域覆盖。基础外观捕获使用同一几何与包内 fallback，不烘入共同入场／组 alpha；这些 alpha 在对象与容器合成时各应用一次。基础外观自身的颜色 alpha 保留。

透明输出遇到实际依赖背景的材质时，跳过整张材质图，返回包内基础外观并记录 `TransparentBackgroundDependency`。即使图的普通旁路指向别的输入，材质的回退仍只由材质导出决定。纯色／渐变回退不是失败包或错误 shader 的兜底；编译、资源、参数、取消及预算错误仍使本帧失败并阻止导出。

## 3. 执行接缝与顺序

1. Core 保存强类型覆盖模式并严格解析包。新增必填字段使材质包编码不兼容；当前源码示例和测试的包 profile 升至 2，SDK 字节继续进入编译缓存身份。
2. 固定编译图完成区域限制／已覆盖验证，返回独立拥有的颜色与替换覆盖；它们与原图结果、输入及包编译集合的生命周期分开。别名、旧结果与新产物同时活动的峰值纳入现有单图／共享账本。
3. 准备帧保留真实材质句柄、完整参数和逐区域最后写入者，几何描述不得冒充最终白色外观。生产绘制将材质写入基础／注音填充和描边，再供元素与整行图捕获；原始捕获未接通最终材质时明确拒绝。
4. 元素族／文字标记进入现有主画布部件协调器。已唱与未唱分别使用互补裁切，不混合两种覆盖模式；后续图读取处理后的彩色部件，原始 Coverage 仍是几何。
5. 真实预览与 MP4／Alpha 同帧验证后，才把生产材质记为完成。独立 CPU 区域原语不等于已交付材质工作区或主 Render。

不新增程序集、依赖或旧工程迁移。本批按路线图保留当前静态样式适配，工程／歌词／模板材质持久化在 R4 完成。组图／父捕获的三个待答事项不使用默认值。

## 4. 验证要求

- 纯色 `unmasked` 与读取基础外观的 `masked` 在抗锯齿边缘只覆盖一次，填充／外描边都有真实像素证据。
- 错误声明、越界 alpha、替换覆盖越界、坏颜色、错误密度／坐标／图代次及释放／取消明确失败；不修复作者输出。
- 背景依赖透明回退、完整渐变、真实资源身份／参数和按输出顺序的替换范围可追溯。
- 活动表面与局部／共享预算包含区域限制产物；错误后全部归还。
- 全量构建、测试、程序版本、相关文档、提交与远程同步共同收尾。

### 4.1 本轮生产接入细化

准备帧为未唱填充、已唱填充、描边分别保存最后写入者，后续透明度／阴影写入不改变材质归属。保留真实 StyleView；当前 LyricStyle 只作明确的几何绘制描述，包材质用白色几何种子，普通外观读取拒绝将其冒充最终颜色。无归属或不匹配的包材质不能进入成功准备帧。

主画布在元素／标记部件生成时执行材质，已唱／未唱分别裁切，注音使用同一族输出。材质产物先于元素图与整行图，后续 TargetColor 读取已处理部件。源样式 opacity 在材质颜色／擦除量上各一次；标记共同 alpha、行入场及父组 alpha 仍由现有协调器执行。原始彩色捕获遇到尚未重组的材质明确拒绝，raw 覆盖与包基础外观捕获不受此限制。

固定资源集合增加独立 MaterialHandle 入口，不伪造 GraphHandle。透明材质提示单独保存真实材质身份，缓存与导出保留并提示使用包内基础外观。没有固定资源、坏参数、材质输出越界和中途失败仍阻止导出。这些是已接受合同的宿主接入，不新增工程编码、程序集或依赖。

## 5. 实施状态

**0.13.0 已完成严格包编码、真实输入和独立固定包执行接缝。** Core新增覆盖枚举及必填字段，源码示例／测试profile2；CompiledImageGraph.ApplyMaterialCoverage、CompiledPackageGraphExports.ExecuteMaterial与SkiaRenderPipeline.CaptureSceneMaterialRegion已实现。真实白色glyph后着色解决彩色AA与raw覆盖不同的问题，完整渐变alpha不再乘首个停止点alpha。区域捕获的两个产物及显式SaveLayer共用帧账；输出限制含旧产物与新独立颜色／擦除的峰值，失败回收。

新增Core6、Rendering32；定向Core146项与修正后的材质／原图合成84项通过，最终全量包含补充渐变alpha／未知输入／局部预算和像素网格用例。全量构建0警告／0错误，**2444项全部通过、0失败／跳过**（Application83／Core1006／Export74／Media87／Rendering761／Scripting170／Spike.App179／Timeline84）。真实填充、外描边和注音／1×与2×密度、局部仿射及逐字互补、一次覆盖、已覆盖不二次淡化、独立替换覆盖、渐变单次alpha、透明包基础外观、输入旧外观被替换、错误模式／区域／参数／句柄／密度／网格／代次、取消及预算回收验证通过。

沿用用户已有依赖缓存授权，执行dotnet build MakoKaraGenerator.slnx --no-restore -m:1／dotnet test MakoKaraGenerator.slnx --no-build -m:1。日志：/tmp/makokara-material-region-build-final.log、/tmp/makokara-material-region-tests-full.log；定向/tmp/makokara-material-region-core.log和/tmp/makokara-material-region-focused-second.log。本批无依赖变更，没有新的在线恢复／审查证据。

最终0.13.0已启动，PID624872／niri332，标题与/tmp/makokara-f93b-region-material-final.log一致，当前启动日志仅版本记录、无托管异常；niri333为旧备份精确版本门禁提示，没有转换或删除旧文件。没有新脚本工作区验收入口。

**生产准备帧和主画布尚未接入区域材质。** 当前正常PackageExport仍报告UnsupportedMaterialBackend，原测试继续验证该门禁。此批未新增材质主Render／MP4／Alpha成片验证，原有导出路径全量继续通过，不把输入／原语测试记为完整R3。下一批保留逐区域真实写入者并接到现有部件协调器和后续图捕获；工程／模板事务、完整native资源出口、组图及工作区继续原路线。

## 6. 0.13.1 生产接入结果

0.13.1 已接通元素族／真实前奏标记的 PackageExport 准备帧和正式主 Render。未唱／已唱／描边逐属性保留最后写入者，真实 StyleView 保留包句柄／参数，LyricStyle 只作明确几何绘制种子；没有归属或固定资源拒绝。已唱／未唱互补裁切，基础字及注音使用实际材质，后续整行图读取已处理彩色部件；元素局部 alpha、标记／行共同 alpha、组 alpha 各一次。透明背景依赖使用自身包基础外观并保留真实 MaterialHandle 提示，缓存和导出按实例去重。原始 TargetColor／彩色部件捕获遇到未重组材质报告 MaterialProcessedCaptureRequired，不返回白色种子。

新增 Rendering13／Export3；八项目全量 **2460 项全部通过，0失败／跳过**（83／1006／77／87／774／170／179／84），全量构建0警告／0错误。定向渲染119项、导出7项通过。真实 C# 包验证两种覆盖模式、填充／描边／注音和密度、参数／区域、归属不被后续alpha抢走及恢复纯色解除绑定、整行图读取材质结果、真实前奏标记、透明纯色／渐变基础alpha与缓存、预算／坏覆盖释放；实际MP4与ProRes4444同帧对照及200ms中途坏材质保护已有输出通过。

沿用已授权依赖缓存：dotnet build MakoKaraGenerator.slnx --no-restore -m:1、dotnet test MakoKaraGenerator.slnx --no-build -m:1。最终日志 /tmp/makokara-material-production-build-final.log、/tmp/makokara-material-production-tests-full.log；定向 /tmp/makokara-material-production-focused-tests.log、/tmp/makokara-material-production-export-tests.log。本批未改依赖，没有新的在线恢复／审查证据。

本批不新增工程字段、包编码、程序集或依赖；程序升 PATCH 0.13.1，schema21／15／3与package profile2保持。Materials.SetUniform、组图／父配方、映射、跨输出执行计划、工程／模板／分析事务和工作区仍待实施；原七项待答不使用默认值。完整 R2～R8／R3 出口保持未完成。

0.13.0 §5 的未接入门禁是历史状态。本批正常包材质已可由真实控制器经宿主执行并成片；手工准备帧缺少逐区域归属改报MaterialOwnershipRequired，未提供固定集合报MaterialResourcesRequired。GetTokenAppearance／GetMarkAppearance读取实际最终外观，DrawingStyle只用于固定glyph。新增RenderFrame.MaterialDiagnostics与缓存／报告，材质不冒充图。

最终0.13.1已直接启动，PID668689／niri334，标题及 /tmp/makokara-f93b-material-production-final.log 一致；启动日志只有版本记录、未见托管异常。旧0.13.0窗口PID624872／niri332保留，未终止进程。没有新增脚本GUI验收入口，启动不作为材质人工验收证据。
