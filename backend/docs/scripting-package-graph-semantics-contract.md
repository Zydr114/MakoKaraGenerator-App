# 效果包材质／图语义补充合同

> 2026-10-03；**A 独立程序文件已获用户确认，0.9.21 实施；B 图编码随后已确认，0.9.22 实施；C 已获明确接受，0.12.0 实施统一语义／真实目录与执行接缝，生产区域材质和主画布仍待接入**。接续已接受 R0／ADR-0071、shader／图原语（ADR-0077／0078）及 0.9.20 包参数合同（ADR-0084）。本文件只补齐原示意 JSON 缺少的编码，保留已确认的效果行为；不修改工程／歌词／模板格式。

## 1. 现状和本批出口

0.9.20 已能解析控制器包的九类参数并编译、消费真实 DLL；混有材质／图导出的包仍整体拒绝。正式后端已有固定 `ShaderProgramDefinition`、图拓扑校验、五类 CPU 操作、真实捕获、背景输入、透明旁路及合成链，尚没有把包文件解析成这些对象的完整入口。

本批将建立**完整包语义目录**：控制器／材质／图共享一份参数定义，读取闭包中的固定 shader／图／图片，逐文件校验和正式原生编译，提供真实 CPU 图执行及透明回退验证。工程持久材质、主画布父子图调度、未纳入部件叠放、工作区 UI 等后续出口保持原路线，不以独立包执行代替它们。

## 2. 已确认 A：独立 shader 程序文件

建议为清单增加语义角色 `shaderProgram`，程序文件与 SkSL 文件各进入同一哈希闭包。图节点以包相对路径引用程序，不逐节点重复声明类型；`role:shader` 的 SkSL 不能用正则推断 ABI。

```json
{
  "programVersion": 1,
  "source": "shaders/glass.sksl",
  "profile": "skia-raster-3.119.4-v1",
  "uniforms": [
    { "name": "timeSeconds", "type": "float" },
    { "name": "logicalResolution", "type": "float2" },
    { "name": "strengthPx", "type": "float" }
  ],
  "children": [ { "name": "background", "type": "color" } ]
}
```

- 文件示例为 `shaders/glass.program.json`。`source` 必须在本包中且 role 为 shader；profile 与清单、宿主锁定值完全一致。
- type 沿用已接受的 float／float2／float3／float4／int／color／matrix3x3。数组仅通过可选正整数 `arrayLength` 声明；字面值数组为按顺序展开的分量，矩阵按行。child 类型仅 color／coverage。
- 所有字段严格检查，未知／重复字段拒绝；未使用但声明的程序也编译检查。正式 Skia 反射校验实际 uniform／child 名称、类型和数组，不接受只通过 JSON 的错误 shader。
- RuntimeShader 节点改用 `program: "shaders/glass.program.json"`，不再保存另一份 file／profile。原 docs 示意尚非可用分享包，直接更新，不保留旧编码兼容。

## 3. 已确认 B：输入捕获与完整图编码

每个捕获输入独立声明 capture，背景／资源输入不能带 capture：

```json
{
  "id": "original", "source": "TargetColor", "type": "color",
  "availability": "required", "backgroundRequirement": "none", "space": "canvas",
  "capture": {
    "includeRuby": true, "includeStroke": true, "includeShadow": true,
    "includeDescendantGroups": true, "includeOwnedOverlay": false,
    "region": "fillAndStroke"
  }
}
```

- 五个 include 字段均为必填布尔值；不把省略解释为全部纳入。region 为 fill／stroke／fillAndStroke；材质图额外允许 `materialRegion`，由正在绘制的填充／描边区域解析。非 TargetRegionCoverage 的输入保留配方，但不会把 region 当作蒙版偷偷乘到 TargetColor 上。
- ResourceImage 显式 `resource: "images/noise.png"`，只能引用同闭包 role:image；条件来源仍沿用 `source:"parameter:<enum>"` 与完整 choices，不能以文件缺失自动换来源。图片位置／空间转换通过显式 Transform，加载不默认铺满或拉伸。
- 每个节点 bounds 必填 expandPx、maxExpandPx、**maxSampleOffsetPx**；edge 同为必填。外扩继续只接受非负常量或 `ceil(k * parameter:<number>)`。无取样偏移也须明确写 0。
- Transform 明确保存 `matrix:[a,b,c,d,tx,ty]`（与现有 SceneMatrix 对应，x'=a*x+c*y+tx，y'=b*x+d*y+ty）及 `outputBounds:[left,top,right,bottom]`，均为有限数且矩阵可逆、框次序有效。不隐式取包图片大小或猜画布。
- 图根 bounds 的 source 当前只接受 `targetRegion`；space 保留 token／line／node／canvas。JSON 可以声明四种空间，实际执行适配未完成的空间必须报不支持，不能按 canvas 执行。图参数只能引用清单的同一参数定义，导出必须包含图所需参数。
- 图根可选 `bypassInput:"input:original"`，仅指向同图 required／none 的 TargetColor 输入，按已接受透明旁路合同执行。普通无背景依赖图不要求旁路；背景依赖整行／组图必须声明它。
- RuntimeShader 的 uniform 字面量由所引用程序的声明类型解析：标量为数值，向量／矩阵／定长数组为展开数组；color 为已确认的精度 RGBA 对象，color 数组为 RGBA 对象数组。参数／宿主绑定保留原 `parameter:`／`host:` 封闭字符串，不引入表达式解释器。
- 节点端口、拓扑、参数类型／范围、采样／累计外扩和资源预算使用已有唯一图编译器。optional 输入缺失行为仍待之前的问题答复，本批不选择默认策略；唯一例外是 BackgroundMedia（0.112.61 起，2026-11-10 用户确认）：不透明输出缺背景媒体时，optional 输入以工程底色（BackgroundColor 捕获）代替，required 输入与透明输出同路旁路（材质退回包内基础外观），诊断原因为 `BackgroundMediaUnavailable`。

## 4. 已确认 C：导出与材质回退编码

- controller 保留现行字段，material 保留 R0 的 graph／parameters／regions／fallback／outputs；graph 导出为 id／kind／name／graph／parameters／bindingKinds／outputs，允许绑定种类仍为已有六类。
- material 的唯一 ImageGraph 输出 target 为 region，bypass 显式 `{ "kind":"materialFallback", "mapping":"identity" }`；图导出的 ImageGraph 输出 target 为 tokenFamily／line／group／textMark，背景依赖时 bypass 显式 `{ "kind":"graphInput", "input":"input:original", "mapping":"identity" }`，须与图根 bypassInput 一致。依赖输出的 ColorOnly／identity 等执行仍沿用 R0；未实施的组合拒绝启用，不能只运行剩余输出。
- fallback 必须是纯色或现有完整渐变：solid 为 `{kind:"solid",color:"#RRGGBBAA"}`；gradient 为 `{kind:"gradient",gradient:{...}}`，内对象必填 kind（Linear／Radial）、angleDegrees、centerX／centerY、radiusX／radiusY、coordinateSpace（Canvas／Line／Glyph／Progress）、colorSpace（LinearRgb／Hsv／Oklch）、huePath（Shorter／Longer／Increasing／Decreasing）、stops（offset／color）。不靠字段缺失补初始值。沿用现有 GradientSpec 校验／实际后端支持，不静默降级不支持的颜色空间。
- 包中声明的全部程序／图都验证，不能把未引用错误资源藏到后续启用时；同一个包可混合三类导出，但每个导出的参数、输出依赖、捕获及 fallback 都必须完整。文件路径就是闭包资源身份，不另设重复可变资源表。

## 5. 实施与验证顺序

1. 提取已确认的参数／严格 JSON 检查，保持控制器现行行为；为混合导出复用，不复制九类验证规则。
2. 用户确认 A／B／C 后，完成统一语义目录与程序／图解析；使用已有 Core 图编译器及正式 Skia 原生编译，不新拆程序集。
3. 用真实包文件验证 shader ABI、图端口／边界／捕获／图片／fallback，错误附带真实文件和字段；覆盖取消和 native 生命周期。
4. 从目录／.mkfx 读取同一包，运行真实 CPU 图与透明回退；与手工构造同定义的真实像素和短片帧比较。包中控制器继续共享实际 DLL，不为每个输出复制作者实例。
5. 更新版本、ADR、roadmap、AGENTS 及接续文档，构建／全测通过后启动当前程序，按一步一个提交推送。本批不提前宣称 R3／R4 或脚本系统完整交付。

## 6. A 的实际实施路径（0.9.21）

`EffectPackageShaderPrograms.Read(contents)` 读取全部 role:shaderProgram 文件，以**程序文件路径**索引固定 `ShaderProgramDefinition`，源码路径只来自同闭包 role:shader。程序版本、profile、七类类型／数组、两类 child、名字、严格字段／UTF-8 和跨 uniform／child 重名均检查；长度 1 保持数组语义。声明不从源码推断，不默认补缺字段。

控制器目录保留全部程序定义；`CompiledControllerPackage.CompileAsync(definition, compiler, imageValidator, shaderValidator, token)` 通过 Rendering.Abstractions 的 `IPackageShaderValidator` 注入实际后端。有程序却未注入验证器时拒绝；正式 `PackageShaderValidator` 编译并原生反射**全部**程序，再继续真实 DLL 校验，未引用错误程序也不能部分启用。验证结束释放编译对象；不创建伪 GraphHandle 或给控制器增加 ImageEffects 权限。

`CompiledPackageShaderPrograms.Compile(definitions, scalarBudget, token)` 是正式 CPU 后端持有固定程序的入口；`CreateShader(programPath, values, children, token)` 逐帧换值，不重编译，不持有另一份源码。程序键沿用现有源码／ABI／实际后端指纹；包资源修订改变，但同 C#／入口编译 Artifact 不因 shader 改动而改变。错误附带程序文件与原源码反射诊断；失败／取消回收已建集合，释放幂等，已创建 shader 保留自身 native 引用。此集合不证明上游内部临时 SKData 或全部 native RSS 已受控。

已将九类参数和严格 JSON 检查提取为内部共用读取器；现行控制器行为沿用原测试。**0.9.21 本批只实施 A；B 随后已获确认，图 JSON／捕获配方将接续实施；C 的材质回退／混合导出编码仍待答，主画布也未新增**。本批没有 UI 新入口或静态文字材质断代。

新增 Application **1**、Core **20**、Rendering **9**；全量构建 **0 警告／0 错误**，八项目 **2136 项通过、0 失败／跳过**（81／952／54／87／552／147／179／84）。验证包含七类／数组及 child 元数据、严格字段／角色／版本、坏 ABI 的实际反射、真实 0.25／0.75 uniform 像素、逐帧指纹保持、未引用坏程序门禁、释放后已有 shader 仍可绘制、取消及真实目录／.mkfx 资源往返。shader 修改改变包修订／程序键，同 C#／入口 Artifact 不变。源码／类型／数组错误不是透明旁路，明确阻止控制器包启用。

日志 `/tmp/makokara-pack-shaders-build-final.log`、`/tmp/makokara-pack-shaders-tests-full.log`。最终 **0.9.21** 已启动，PID **136774**，niri 窗口 **46**，版本标题／日志一致；`/tmp/makokara-f93b-pack-shaders-final.log` 未见托管启动异常，有 JACK／ALSA 设备探测警告。未逐项人工验收音频或新增脚本 UI，本批仅交付包程序基础接续。

## 7. 接续确认

0.9.21 完成并推送后，用户明确答复「接受 §3 编码」。B 已获授权，下一批直接实施逐输入显式配方、采样偏移上限、矩阵／框、包图片路径和 bypassInput 解析，继续复用唯一 Core 图编译器与正式 CPU 后端。C 尚未答复，不以 B 的确认替代 C；原 optional 缺失和部件叠放等行为仍待答。

## 8. B 的实际实施路径（0.9.22）

用户确认 §3 后直接实施，不追加旧编码兼容。`EffectPackageImageGraphs.Read(contents, budget, token)` 读取**全部** role:graph（包括未引用图），引用同一包参数／独立程序目录，以图文件路径索引 `EffectPackageImageGraph`。严格字段、精确 graphVersion、封闭输入源／空间／边、完整五类节点、ABI 字面量／参数／宿主及明确 capture 集中解析；图端口、参数全区间、拓扑／累计外扩／预算仍只由 `ImageGraphCompiler` 验证。错误保留实际文件／字段，取消不返回半目录；旁路额外要求 required／none 原 TargetColor。root bounds.source 仅 targetRegion，对应显式帧的 TargetBounds；完整主画布协调器解析真实目标框仍属后续，图文件不保存或猜整画布框。

节点编码如下，除列出的字段外均必填 id／op／bounds／edge；bounds 必填 expandPx／maxExpandPx／maxSampleOffsetPx：

| op | 必填字段 |
|---|---|
| RuntimeShader | program、children、uniforms、outputType |
| Blur | input、sigmaPx（数值或 parameter:id） |
| Mask | color、coverage |
| Transform | input、matrix：[a,b,c,d,tx,ty]、outputBounds：[left,top,right,bottom] |
| Blend | foreground、background、mode：sourceOver／plus／multiply |

矩阵不变更字形布局，非 Canvas 声明保留在中立计划中，但当前后端实际 Execute 报 `UnsupportedGraphExecutionSpace`，不会按 Canvas 代跑。材质图可声明 materialRegion，但无真实材质区域绑定的捕获适配报 `PackageMaterialRegionBindingRequired`。owned overlay=true 元数据保留，生产适配报 `PackageOwnedOverlayCapturePending`；不擅自当作 false。

`EffectGraphCapture` 保存每个输入自己的五个 include 和 region。`SceneImageGraphProgram.FromPackage` 借用实际编译图／调用方显式目标框，将各输入适配成真实 `SceneCaptureRecipe`。`ExecuteSceneImageEffect` 以**来源＋完整配方**复用捕获；同源不同注音配方不再错误别名。region 仅影响 TargetRegionCoverage，未偷偷作为 TargetColor 的额外蒙版。跨图混合部件重组尚未接入，包逐输入程序遇 previousTarget 且读取 TargetColor 报 `ImageEffectCaptureReassemblyPending`，附加实际实例／编译代次／时刻；不把上一张整体产物冒充所有配方。原显式共用配方的手工链仍维持既有已验证合同，正式包协调器仍须完成逐部件串接。

`PackageGraphImages.Load` 从闭包完整解码全部 role:image（含未使用项），共用 `PackageImageValidator` 的真实 codec／BGRA 预乘 sRGB 路径；图片逐项尺寸及集合总表面／字节预算在像素分配前检查。`GetInput` 必须给输入自己的原始图片框、空间及密度；不默认铺满／拉伸，位置／缩放使用显式 Transform。返回包装借用固定图像，调用者先释放输入／结果再释放集合；失败／取消清理已解码集合，释放幂等。这里的包加载预算不代替完整主画布／全部 native RSS 资源出口。

控制器目录保留全部图计划；新增 `IPackageImageGraphValidator` 注入正式 `PackageImageGraphValidator`，实际 CPU 编译全部图后再继续 DLL。缺少验证器拒绝启用，不忽略坏图；程序仍由原 shader 门禁验证全部 ABI，图片由实际解码门禁检查。**不新增 SDK GraphHandle，也不部分启用含 C 未解析导出的包**。§4 C、包材质／混合导出、跨输出执行／旁路、真实下层／父子主画布、工程／历史／模板持久事务及工作区继续，完整 R2～R8 未完成；生产 schema 21／15／3 不变，精确应用版本随 0.9.22。

新增 Application **1**、Core **23**、Rendering **9**、Export **2**；最终全量 build **0 警告／0 错误**，八项目 **2171 项通过、0 失败／跳过**（82／975／56／87／561／147／179／84）。真实证据包括：严格配方／边界／参数与五类拓扑，七类完整字面量及长度 1 数组／颜色精度；目录移走后 .mkfx 保留同一真实图／程序／修订；实际 0.25 shader 像素；真实 2×2 图片经 Transform 移动且不拉伸；背景依赖透明图返回指定原输入／诊断，坏 shader 不能借透明旁路通过；生产歌词注音差分与独立捕获逐字节相同、不同配方不误复用，预算资源回收；真实 MP4／Alpha 短片同帧符合 CPU 结果。独立编码样本不代替主画布或六包成片出口。

日志 `/tmp/makokara-package-graphs-build-final.log`、`/tmp/makokara-package-graphs-tests-final.log`。最终 **0.9.22** 已直接启动，PID **179167**／niri 窗口 **59**，标题及日志版本一致；`/tmp/makokara-f93b-package-graphs-final.log` 当前只有版本启动记录，未见异常。本批未新增脚本 UI，不把窗口启动当作脚本工作区或人工音频验收。实施依据为 ADR-0086，权威状态见 roadmap §18.51。

## 9. C 的接续确认（2026-10-03）

用户本轮明确答复「接受 §4 合同 C」。材质／图混合导出、纯色 #RRGGBBAA／完整现有渐变回退、显式图原输入旁路及完整资源校验获得授权。A／B 的历史实施记录中「C 待答」描述的是当时状态；当前 C 已确认，统一语义／真实目录及显式执行接缝见 §10；完整生产区域材质／主画布仍待实施。生产协调器的保留部件采用所处理部件的最高层，见[叠放合同](scripting-retained-parts-stacking-proposal.md)／ADR-0091。其余原待答不随本次确认自动决定。

## 10. C 实际实施接缝（0.12.0）

统一语义类使用 `EffectPackageDefinition`，编译门禁使用 `CompiledEffectPackage`，代替原仅控制器命名；不保留旧公开类型别名。包同时保存有序的控制器／材质／图导出，跨种类 ID 唯一；每一导出的参数共用清单定义，引用图的参数必须全部声明。材质唯一 region 输出及 fallback、图的目标／绑定及显式旁路完整校验。固定资源目录只公开正式后端完整验证过的导出，不把 role:graph 文件路径假装成导出 ID。

纯材质／图包不需要假 DLL；有 C# 文件时全部编译，有控制器时必须有 C# 且全部入口反射通过。资源、回退及源码出错都不能留下可启用的部分包。控制器图输出按真实声明接入生产准备帧，并继续检查图导出的目标、允许绑定和完整参数；未接通的跨输出执行计划／主画布协调器仍明确失败。

本批增加帧外固定图资源集合，将导出句柄关联到实际包图计划；实际 CPU／捕获输入／透明原输入使用现有后端。材质回退用正式后端支持情况校验并投影到唯一纯色／渐变基础外观；完整生产材质绘制、静态 MaterialSpec 断代、工程／模板保存事务及主画布父子协调器接续实施，不把元数据目录当作这些出口。

公开类型更名是不兼容 SDK／宿主 API 修改，交付版本升 MINOR 至 0.12.0；本批不更改工程／歌词／模板 JSON，schema21／15／3保持。真实混合包、归档、严格错误、句柄／参数、CPU 像素与生产帧路径已自动验证；显式短片适配不代替主画布协调器。

验证：新增 Application1／Core25／Rendering19／Export2，共47项。真实混合目录与 .mkfx 保留同修订；资源门禁、纯资源包／坏源码包、实际图绑定范围及完整参数、句柄修订／取消／释放、生产固定歌词／注音 CPU 像素和显式 MP4／Alpha同帧均通过。纯色及线性／径向回退显式消费与静态外观逐字节一致；背景依赖图透明执行返回原输入并诊断。正常材质绘制与主画布未完成时的失败门禁仍由实际测试验证。

最终全量2333项、0失败／跳过，缓存构建0警告／错误。日志 `/tmp/makokara-mixed-build-final.log`、`/tmp/makokara-mixed-tests-full.log`；依赖未改，本轮未完成在线恢复／审查。0.12.0已启动PID470738／niri315，版本与日志 `/tmp/makokara-f93b-mixed-final.log` 一致；旧备份版本拒绝窗口316未转换数据。权威状态及完整边界见 roadmap §18.58／ADR-0092。

## 11. 0.12.1 主画布接续

固定包图程序及图片可交给 SceneImageEffectResources，由正式 Render 自动执行当前元素／整行／标记图和按输入保存的真实配方。来源最高层融合、部分切分拒绝、无目标输入全替换及透明原输入／提示按[主画布合同](scripting-main-canvas-composition-contract.md)实施。包材质、requires／跨输出计划、组隔离、持久事务及 UI 保持未完成；此接缝不代替完整编译／资源门禁。验证见 roadmap §18.59。


## 12. 区域材质覆盖编码（0.13.0）

用户确认后材质导出新增必填coverageMode，值只能unmasked／masked，强类型保存并影响完整包修订。当前源码示例与测试package profile2，不保留旧材质清单自动补字段。区域限制、TargetColor和透明回退语义见[区域材质合同](scripting-region-material-contract.md)；现有C编码其他字段不变。原生输入／执行接缝已接通，PackageExport准备帧与主Render自动调用尚未完成。

## 13. 0.13.1 生产区域材质

0.13.1 已接通元素族／真实前奏标记的 PackageExport 准备帧和正式主 Render。未唱／已唱／描边逐属性保留最后写入者，真实 StyleView 保留包句柄／参数，LyricStyle 只作明确几何绘制种子；没有归属或固定资源拒绝。已唱／未唱互补裁切，基础字及注音使用实际材质，后续整行图读取已处理彩色部件；元素局部 alpha、标记／行共同 alpha、组 alpha 各一次。透明背景依赖使用自身包基础外观并保留真实 MaterialHandle 提示，缓存和导出按实例去重。原始 TargetColor／彩色部件捕获遇到未重组材质报告 MaterialProcessedCaptureRequired，不返回白色种子。

包编码沿用§12的profile2及已接受C字段。宿主固定材质程序使用真实MaterialHandle；包图片仍明确提供原图框／密度，不默认拉伸。真实作者写入经参数／区域和完整包门禁，再由主Render调用固定native集合。当前不能使用requires／dependsOn扩大到未实现执行计划。

本批不新增工程字段、包编码、程序集或依赖；程序升 PATCH 0.13.1，schema21／15／3与package profile2保持。Materials.SetUniform、组图／父配方、映射、跨输出执行计划、工程／模板／分析事务和工作区仍待实施；原七项待答不使用默认值。完整 R2～R8／R3 出口保持未完成。 证据见roadmap §18.63。
