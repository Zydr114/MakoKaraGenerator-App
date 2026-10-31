# 中立图定义与共享 CPU 图执行合同

> 2026-10-03；0.9.14，依据用户已接受的 R0 §6／§8（ADR-0071），实施记录见 ADR-0078。本批接续 0.9.13 shader 原语，实现可执行图及其资源规则；不改持久 JSON，完整 R3 出口仍含生产文字捕获与材质接入。

## 1. 模型和执行

Core 在既有 Effects 内保存不可变图、输入、参数约束、边、节点、bounds／采样及 composite 描述。输入源显式区分 coverage、目标图像、媒体、下层合成、背景颜色和图片资源；可以用已声明 enum 选择来源，不从名字猜依赖。所有声明、包括未执行节点，先校验；悬空边、环、端口类型、参数、shader 声明和输出错误均阻止执行。

Rendering.Skia 持有一次编译的图和 native shader。帧传入固定 ParameterValues、时刻、画布尺寸、目标逻辑框和具名图像输入；同一图可以反复执行，不在帧内编译源码。操作为 RuntimeShader、Blur、Mask、Transform、Blend；Input 是显式引用，不额外绘制。拓扑排序允许边引用后面的节点。

v1 产物为预乘 sRGB BGRA8888，输入允许同编码的 BGRA／RGBA8888；颜色输入必须有真实 sRGB 元数据，RGB 不能超过 alpha。每张颜色产物逐行检查真实字节，错误报告 GraphColorNotPremultiplied，不补乘 alpha 或夹值。Coverage 作为原始数据使用 alpha 通道，不做颜色空间转换，也不拿 raw RGB 值套颜色预乘规则。Mask 只乘一次覆盖；Blend 仅支持 sourceOver／plus／multiply。Transform 显式提供有限可逆矩阵和输出框。Shader uniform 使用上批完整类型和数组；只能 literal／parameter／timeSeconds／canvasSize 绑定，不增加任意表达式解释器。

## 2. 坐标与边界

surface 保存逻辑框、space、localToCanvas 和像素密度，图像到逻辑框的映射明确。首个执行后端在 Canvas 空间工作，输入须已经由捕获端投影到 Canvas；模型保留 Token／Line／Node，未经相应适配明确拒绝，不冒充完整 R3 空间支持。所有操作距离是画布逻辑像素，缩图不改变参数。

节点显式声明 edge、实际 expand 和最大 expand／采样距离。expand 仅非负常量或 ceil(k×number parameter)；参数 min／max 预检查，当前值再次检查。Blur 要求 expand 至少 ceil(3σ)，最大 σ 初始 64。节点外扩沿依赖累加，必须在图的最大外扩内；Transform 的显式框在帧目标最大框内。中间框按密度向外对齐像素网格，不用固定 64 px 装饰截断。

执行前计算活动节点和实际像素尺寸／内存，再创建表面。默认每图 32 pass、64 个同时表面、512 MiB、每边 8192 px、每输入外扩 256 逻辑 px；宿主可显式指定预算。执行和合成的输入按 native 图像身份去重，返回产物也纳入统计，不静默降分辨率。节点在最后一次使用后释放，中间产物异常／取消时清理；借用输入不由图销毁，返回产物由调用者释放。每个节点绘制到独立表面，只合成一次。

本批预算是**单次图的活动图像预估**，Blur 另保守预留两张同尺寸 scratch；PeakBytes 不是原生滤镜全部临时分配的实测值，也没有跨图／capture／overlay／会话的全局资源账本。R0 的每帧 64 表面／每会话 512 MiB 仍须由后续协调器统一落实。公共 ToShader 的内部数据包装仍有 [0.9.13 上游释放限制](scripting-shader-program-contract.md#5-实施和验证)，不据此关闭完整 R3 资源出口。

## 3. 透明计划和合成

背景依赖来自输入声明的 media／opaqueCanvas，BackgroundMedia 来源不得声明 none；BackdropBelow／none 可以处理透明下层。透明模式缺背景时，该命名图输出只能按已声明 bypassInput 返回原 TargetColor，报告结构化原因；native 编译和图结构仍检查。外部串接的后续独立图继续执行。同实例计数／变换／粒子继续维护的完整输出协调器尚未接入本图原语；材质包的 solid／gradient fallback 由后续 Material 集成实现，本图原语不虚构基础材质。

普通 sourceOver 使用预乘图像；replaceCoveredBackdrop 明确提供独立 Coverage，合成为 maskedResult + below×(1−coverage)。产物不再乘一次覆盖。此合成原语以实际下层输入为依据，后续捕获协调器负责层级顺序和排除目标自身。

optional 输入缺失策略已询问用户，答复前不实现透明空图／自动替代。当前图可使用已提供的 optional 输入；真实缺失报告此策略尚未确定，相关路径不算完整 R3 出口。

## 4. 验证与剩余

新增 Core 10、Rendering 20、Export 2 项回归：后定义边／未执行坏节点、不可变集合、完整端口／绑定、参数最大外扩、真实 shader uniform、三种混合与独立 Skia 逐字节一致、1／0.5／非等密度下的逻辑位移、三种 sampler edge、Blur 的逻辑 σ／实际外扩、半透明蒙版和替换边缘、背景来源选择／透明旁路／后续独立图、缺输入／非法来源、实际像素／内存／表面门禁、最后使用释放、取消和 native 矩阵范围。CPU 颜色输入及非预乘 shader 产物明确拒绝。

真实 VideoExporter／编码器导出 320×180 MP4 与 ProRes 4444，并解码第 15 帧与同一图执行结果比较：RGB 平均误差小于 9，透明 alpha 每像素误差不超过 3；透明旁路保留基础外观，后续独立 Blur／Transform 仍有真实像素。该样本显式提供矩形／椭圆输入，不拿它冒充字形捕获、生产歌词或工作区集成。

回归先复现非预乘产物不报错；临时改成覆盖度平方后，边缘 alpha 从约 128 变成 64，测试确实失败，随后恢复；合成输入重复计费变异也被实际预算回归检出。证据 `/tmp/makokara-graph-premul-red.log`、`/tmp/makokara-graph-mask-mutation.log`、`/tmp/makokara-graph-budget-mutation.log`；最终 build／全量 test 见 roadmap §18.43，日志 `/tmp/makokara-graph-build.log`、`/tmp/makokara-graph-tests.log`。

0.9.14 时尚没有生产文字／描边／阴影 capture、父组隔离及组 alpha、实际下层排除自身的协调器、非 Canvas 适配、材质包解析、Materials／ImageEffects／Mappings writer、错误附加真实实例身份并接通导出失败门禁，或图的持久 JSON。CompiledImageGraph 的错误由调用方收到；生产协调器必须转换为现有 EffectFailureException，不能让 VideoExporter 将效果失败降成普通占位帧。完整 R2／R3～R8、六包和工作区仍未完成，性能基准后置。


已直接启动最终 0.9.14 构建，PID **585153**，日志 `/tmp/makokara-f93b-graph-final.log` 显示版本 0.9.14、未见启动异常；niri 确认同 PID 的主窗口及「工程版本不一致喵」提示。提示是旧工程恢复的精确门禁，不转换旧数据；本批没有新增脚本 GUI，启动不代替其人工验收。

## 5. 下一批接入路径

1. 从 `SkiaRenderPipeline`、`FamilyTransforms`、`Ruby`、`TextMarks` 的现有固定字形绘制入口提取共享绘制单元，分别产出 Glyph／Stroke／Sung／TargetRegion coverage 和处理前 TargetColor。单元复用一次整行整形，不用重新 DrawText 猜字形，不拿当前主表面的未标记颜色空间冒充已验证的 sRGB capture。
2. 按 R0 §6.3 的真实父树／分支顺序建立隔离捕获、BackdropBelow 快照和组 alpha；不把组 alpha 分别乘到后代上。引入覆盖全部 capture／图／合成的共同资源账本，保留未启用路径的原像素基线。
3. 将固定图导出句柄／typed 参数绑定接入 PreparedRenderFrame，执行错误补充真实实例／输出／目标／时刻并走 EffectFailureException；在生产预览／导出同帧证明 shader／glitch／两种玻璃／透明替代。writer 的权限、整行 Style 覆盖和组输出范围按仍待答的既有问题，不替用户选择。
4. 随后继续 R4 受管包／持久数据，以及 R5～R8 会话、粒子、工作区、六包成片；Canvas 以外空间和实际 MappedWarp 保留完整合同，不能以独立原语测试代替这些出口。

## 6. 0.9.15 接续状态

生产固定字形／注音／前奏及真实组捕获、中立 ImageEffects writer／准备帧绑定和真实实例执行适配已实施，见 [生产捕获合同](scripting-production-capture-contract.md)／ADR-0079；同实例同目标最后绑定、跨实例链已按用户答复验证。真实歌词而非椭圆样本的 MP4／Alpha 同帧、图失败保留原输出已通过。§5 第 1 项及第 3 项的中立绑定／实例失败接缝已完成；父子图与主画布隔离调度、媒体／下层／包图片生产、全局账本、Materials／Mappings 和其余 R2～R8 继续，optional 缺失策略仍待答。

## 7. 共享资源账接续（0.9.16）

正式 Execute／Composite 支持宿主共享帧账；单图 Preflight／CheckBudget 保持，新增检查会话里已存活的其他目标／前实例产物。输出、借用输入和透明旁路计费保持到结果释放；Blur scratch 在实际执行区间预留，别名和跨帧同图像去重。未传账时使用固定编译图自有的 R0 默认会话限值；它与单图编译限值分开，不能将多张调用帧账称为整帧协调器。完整规则及真实预算失败证据见 [账本合同](scripting-surface-budget-contract.md)／ADR-0080；optional 缺失策略和完整 R3 出口保持未完成。


### 0.9.17 接续

用户确认后续普通图保留之前的替换范围，以及未纳入捕获的注音／描边／阴影继续正常绘制。合成链原语、绑定级完整顺序／错误及真实工程媒体／底色生产已进入本批；覆盖并集／最终合成沿用共享帧账。排除部件分区、正式父子 Render 协调器及 BackdropBelow 仍待实施，受控编码不代替完整场景出口。规则及验证导航见 [场景合成合同](scripting-scene-composition-contract.md)／ADR-0081／roadmap §18.46。
