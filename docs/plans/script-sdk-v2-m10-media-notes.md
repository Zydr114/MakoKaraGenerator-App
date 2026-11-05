# M10 媒体特征轨接入研究

初稿日期：2026-11-04；实施补充：2026-11-05。范围：为[主计划 §5.7](script-sdk-v2-render-pipeline-plan.md#57-媒体特征轨与自适应描边)补充源码接缝、有限实现方案和验收门禁。
未明确标注实施的章节仍是候选设计，不是已开放 SDK／HTTP／Worker 合同。初稿只做源码研究，
后续各片的构建、运行、红绿回归及边界统一见[M10证据](script-sdk-render-m10-evidence.md)。
当前HTTP合同仍冻结于1.46.1／产品0.112.15；M10将在消费者接通后独立生成合同与验收。

## 1. 当前结论与实施边界

可以复用音频任务的 owner／CAS、调度、取消排水、进度、事务发布和保留租约模式；不能直接复用其小型 JSON 结果格式及数值上限。
M10 同时需要补齐真实背景的展示帧选择与颜色合同。只让分析轨使用 VFR／线性颜色，而实际背景继续 CFR 近似，会制造错误的“相同输入”。
媒体分析完成是新的固定运行输入；工程内容未变也必须换分析输入身份，不能在原快照内修改可用状态。
导出先固定来源，再等待对应分析制品并钉住摘要，最后取得渲染运行许可；等待依赖时占住许可会与同工程分析形成死锁。
低分辨率统计只服务自适应参数；`BackgroundMedia` 真实像素和 `BackdropBelow` 场景下层继续保留各自语义。

首个实现切片建议固定为：单一背景流、SDR 工作颜色、32×18 源空间网格、基础均值与真实亮度二阶量、最长 2 s 窗口。
静态图只有一个源样本；纯色为显式源种类；HDR、未知颜色语义及不能形成可信展示时间轴的输入具名拒绝。
下述文件／类型名标“新增”的均为建议，不意味着当前存在。

## 2. 已核对的源码接缝

表内路径相对 `backend/src/`；`H` = `MakoKaraGenerator.Backend.Hosting/`，`C` = `MakoKaraGenerator.Backend.Contracts/`。
其余项目简称 `X/` 均指 `MakoKaraGenerator.X/`，例如 `Media/` 是 `MakoKaraGenerator.Media/`；下文沿用此简写。

| 现有文件 | 当前职责与 M10 接缝 |
|---|---|
| `C/BackendAnalysisContracts.cs`、`C/BackendLifecycleContracts.cs` | 音频任务请求、固定输入、进度、摘要及权威 Job 投影；新增媒体字段，不把视频帧数塞进音频 samples 字段 |
| `H/ProjectSessionRegistry.Analysis.cs` | Writer 下 CAS／固定音频租约，磁盘与调度预留，执行排水后规范校验、EnterCommitting、发布、Finish |
| `H/BackendAnalysisResultStore.cs`、`H/BackendJobSidecarStore.cs` | 当前 `.json` 小结果、`byte[]` 写入及完整读取；不能直接承载长视频二进制轨 |
| `H/BackendJobArtifactStore.cs` | 大文件候选、预留、Prepare／Publish、读取租约和清理，可借用事务结构 |
| `H/BackendTaskScheduler.cs` | `TryReserve` 可能立即 Grant 并占运行数；不是调用 `WaitAsync` 才占许可 |
| `H/IBackendAnalysisExecutor.cs`、`H/WorkerBackendAnalysisExecutor.cs` | Host 私有源路径 grant、Worker 能力探测、进度、超时与 Dispose 排水模式 |
| `C/WorkerAnalysisContract.cs`、`Backend.Worker/StaticAudioAnalysisTask.cs` | 严格消息、单调进度、输入前后校验、受管解码及有限结果；新增媒体 task 独立使用 profile |
| `Media/FfmpegVideoFrameProvider.cs`、`.Source.cs` | 明确 CFR 近似，`floor(time*fps)` 和 raw BGRA；没有完整展示 PTS／duration／颜色身份 |
| `Rendering.Abstractions/VideoFrames.cs` | 当前帧只有尺寸／stride／BGRA，straight alpha；缺展示时间和颜色元数据 |
| `Rendering.Skia/BackgroundFrameSources.cs`、`SkiaRenderPipeline.cs` | 实际背景时间映射、解码及摆放；须与分析共享展示选择及规范颜色 |
| `Core/Media/BackgroundPlayback.cs`、`BackgroundPlacement.cs` | loop／freezeLastFrame 与 contain／cover／original 的纯函数，继续作为唯一映射依据 |
| `Core/Effects/ProjectEffectInputs.cs`、`Rendering.Abstractions/ProjectSnapshot.cs` | 当前固定效果身份包含包／材质／音频，不含媒体分析就绪身份；新增不可变输入 |
| `Scripting.Abstractions/V2/PureEffect.cs`、`TextFrame.cs`、`VisualFrame.cs` | 当前只读分析服务为 Audio；新增 Media 服务及阶段限制 |
| `Core/Effects/EffectPackageProfiles.cs`、`ImageGraphDefinition.cs` | 当前未开放 `media.features`；图输入、类型、能力与 fallback 均需显式准入 |
| `Backend.Rendering/ProjectRuntimeInputCodec.cs` | KRI1／`runtime-project-input-v1`，固定 background／package grants；增加媒体制品 grant 必须升级显式合同 |
| `H/ProjectSession.RuntimeInput.cs`、`Backend.Worker/ProjectRuntimeInput.cs` | Host 固定文件闭包与租约，Worker no-links／hash 校验及打开；媒体轨接相同授权边界 |
| `Backend.Server/PreviewBackendService.cs`、`H/BackendPreviewSessionRegistry.cs` | HTTP 捕获／Prepare／换输入，generation、旧帧阻断和旧租约排水 |
| `Backend.Server/PreviewBackendService.GpuStream.cs`、`H/GpuPreviewStreamRegistry.cs` | GPU replacement 关旧 Worker并排水，再开新 Worker／inputGeneration／producer／pool |
| `H/ProjectSessionRegistry.Export.cs`、`Backend.Worker/StaticExportTask.cs` | 捕获固定工程、准备运行封包、注册 Job、渲染；新增不占渲染许可的依赖等待阶段 |
| `Core/Persistence/ProjectResourceJson.cs`、`ProjectStore.cs` | 当前音频采用后保存内容地址分析文件；媒体派生缓存不应复制成隐式工程编辑 |

当前音频 `BackendAnalysisOptions` 总结果存储默认且上限为 64 MiB；执行器返回 `Task<byte[]>`。
音频源时长、PCM／点数、60 s 操作限制和 Worker 4 MiB 消息预算均不是媒体轨能力声明。
视频分析不通过消息携带全轨，不向 HTTP 接收者或作者暴露 Host 文件路径。

## 3. 展示帧、时间与规范解码

### 3.1 新增受管 presentation decoder

建议在 `Media/` 新增 `MediaPresentationProfile`、`PresentationFrameInfo` 与受管 decoder；背景视频与特征任务共用该合同。
每个输出展示帧提供序号、原始 PTS、time base、有效 duration／其来源、规范像素颜色／alpha profile 与尺寸。
排序依据是解码器输出的展示顺序，不能以输入 packet 序号或平均 FPS 替代；B 帧尾部须 flush 完成。
固定 profile 规定旋转／display matrix、sample aspect ratio、选定 stream 与可变尺寸的处理；首版拒绝中途尺寸／颜色变更。
真实背景随机访问从同一时间索引找到帧，再从可验证 seek 点解码至该展示身份，不能重新使用 `floor(t*fps)`。

建议采用一个受控解码流程，逐帧关联像素与有界流式时间元数据；像素序号和元数据序号必须一一匹配。
当前 `OwnedMediaProcess` stderr 仅保留 32,768 字符，后续丢弃；不能拿累计诊断文本作为长视频 PTS 数据管道。
新增增量 metadata pump，限制单记录、缓冲队列和诊断环；序号缺失、EOF 数量不符或管道失败均拒绝整件结果。
若实现采用 ffprobe 帧表与另一 ffmpeg raw 流，必须实证两者 stream／旋转／帧筛选／数量／时间选择一致，不能盲目 zip。
所有子进程、stdout／metadata pump 完成或取消排水后，任务才可提交制品或释放输入租约。

本机 FFmpeg 官方安装手册的 `showinfo` 说明含 n／pts／pts_time，但该安装版文档没有承诺 duration 字段。
`-fps_mode passthrough` 也不能让 raw BGRA 自带 PTS；具体可执行文件／filter／输出格式能力须实施时探测并用夹具验证。
`showinfo` 的 plane mean／stdev 是编码分量统计，不能用来声称线性 RGB 均值或亮度方差。

### 3.2 时间轴规范化

- 保留原始有理 time base／PTS；以第一展示帧为源时间零点，并将首帧之前的播放器行为固定为首帧。
- 内部比较使用 checked 有理数／宽整数；转换到微秒边界采用明确的 ceiling，记录原值，禁止依赖浮点字符串误差。
- 相邻帧展示区间按已验证 PTS 及 `hold-until-next-pts-v1` 规则形成 `[startUs,endUs)`；间隙延长前帧并记录该语义。
- 最后一帧必须有可信 duration 或流终点；不能只用 `avg_frame_rate` 猜最后一帧长度。
- 非递增 PTS、冲突 duration、转换后零长区间、溢出与无法判定的尾部具名拒绝；不悄悄丢帧或补成 CFR。
- 规范源时长等于最后区间终点；实际背景选择与特征选择使用同一规范索引及半开边界。
- 零窗查询返回 `t` 所在展示区间；切镜边界精确选新帧，不在两个展示帧间插值。
- 正窗先在工程时间积分 `(t-windowUs,t]`，再按背景播放策略分段；积分端点是否开闭不改变持续时间权重。
- 工程时间 ≤0 按现有 `BackgroundPlayback` 固定为首帧；窗口跨零部分也计首帧权重，分母保持完整窗口长度。
- loop 按既有取模规则，在循环接缝拆段；短源跨多圈用整圈统计乘次数加首尾段，禁止逐圈循环。
- freeze 到末尾后以最后一展示帧补足窗口持续时间；`windowUs > 2,000,000` 直接拒绝。

静态图的持续时间不绑定某个虚构 FPS；任意正窗口都返回同一个源统计。纯色不启动解码器。
算法、索引和真实背景必须使用同一首尾／循环规则；seek 顺序不影响结果。

## 4. SDR 颜色、alpha 与真实统计

### 4.1 固定颜色 profile

首版候选工作空间为线性 sRGB／BT.709 primaries、归一化 full range、显式 straight→linear-premul 转换、禁用 tone mapping。
源 transfer、primaries、matrix、range、位深、alpha、ICC／颜色标签解释和 decoder/tool identity 都进入 profile。
支持的 SDR 标签组合须列白名单；缺失／矛盾元数据只有在受信导入 profile 明确指定解释时准入，不能按分辨率暗猜 BT.601／709。
PQ／HLG、未知 HDR 或未实现的 wide gamut／ICC 转换在分析和同 profile 背景路径一并拒绝。
静态图与视频使用同一个工作颜色定义；PNG 字节平均、nearest 缩图、sRGB gamma 字节均值都不等于线性均值。

当前背景绘制使用 sRGB Skia 表面，尚无证据证明所有 alpha 合成是 linear-light。
因此 M10 必须提供显式版本化背景合成 profile：真实像素和统计都先规范到线性预乘，按同一 source-over／底色合成，再转换到输出颜色。
CPU 可用规范转换路径，GPU 可用对应线性 pass；两者以同源夹具验证，不能仅在分析侧改公式。
若该升级改变既有半透明媒体外观，必须作为显式 runtime／颜色 profile 升级记录和验收，不能称为量化误差。
真实媒体／底色／低分辨统计的取样过滤、边缘和输出透明模式均在映射身份中登记。

### 4.2 32×18 单元格式候选

对每个源单元先在线性源像素上做面积统计，再量化；单元不是先缩成一个 gamma 像素再“求统计”。
存 7 个 `[0,1]` 量：`E(Rp), E(Gp), E(Bp), E(a), E(Yp²), E(a·Yp), E(a²)`。
`Rp/Gp/Bp` 为线性预乘，`Yp = 0.2126 Rp + 0.7152 Gp + 0.0722 Bp`；二阶项来自真实源样本。
每量 uint16，`floor(value*65535+0.5)`，另 2 字节保留零；每单元 16 B，每展示帧 9,216 B。
只允许此 profile 的固定舍入与顺序；非有限量／越界数据拒绝，不夹值伪装有效数据。

对固定底色预乘亮度 `Yb`，合成后的单元统计为：

```text
meanRgbPremul = E(RGBp) + RGBbPremul * (1 - E(a))
meanAlpha     = E(a) + ab * (1 - E(a))
meanY         = E(Yp) + Yb * (1 - E(a))
secondY       = E(Yp²) + 2*Yb*(E(Yp)-E(a*Yp)) + Yb²*(1-2*E(a)+E(a²))
varianceY     = secondY - meanY²
```

返回类型明确命名为线性预乘均值、alpha 与预乘亮度方差；只有合成后 alpha=1 时才能作为可见背景亮度／对比度使用。
不从均值推导纹理对比度，不把预乘方差冒充去预乘像素的方差；需要后者必须增加真实统计并升级 profile。
空间与时间均先对这 7 个原始统计量作归一化加权，最后计算合成和方差；不能先逐单元算方差再平均而丢掉均值变化。
量化单量误差 `δ=1/(2*65535)`；上述方差仅允许 `[-(13δ+1e-12),0]` 范围归零，更负则拒绝／报内部统计错误。
该界基于 SDR `[0,1]`、固定非负加权和归一化权重；颜色变换误差、网格近似和渲染差异不归入此容差。

32×18 网格不能恢复细纹理的空间位置。矩形子单元按面积权重返回网格近似，不能承诺源像素精确均值。
3 min ×30 fps 的主统计体积为 49,766,400 B，约 49.77 MB；尚未包含时间表、累计索引、映射、CPU／GPU 副本。
主计划的 RGBA8 12.44 MB 估算不能当作此 7 量格式的全部成本。

## 5. 时间／空间查询与 GPU 接线

建议新增 SDK `MediaFeatures`／`MediaFeatureSample`／`MediaQueryProfile`；服务只读、无文件／任务／播放器句柄。
`DeclareContext.Media` 固定状态、身份及允许的查询 profile；`TextFrame.Media` 与 `VisualFrame.Media` 复用同一输入。
查询必须显式声明 `SourceNormalized` 或 `CanvasPixels`。矩形需有限非空；越出该空间合法范围时拒绝，不能静默裁剪。
2026-11-05实施决定：SDK现有`Rect2`为binary64，CPU查询profile固定为
`media-grid-area-binary64-v1`，不引入中间f32坐标舍入；既有float helper精确提升后是其子集。
GPU RGBA32F数据路径仍单独验证数值范围和误差，不以CPU通过代替。
Canvas 查询通过 `BackgroundPlacement.Compute` 反映射，cover 裁切、contain／original 留白和 alpha+底色都计入面积。
Source 查询返回未摆放的源统计；Canvas 查询返回当前背景合成统计，类型和返回说明必须区分。
文字早阶段只能查询明确的基础布局区域；最终几何区域留到晚阶段或材质求值，不能经最终样式反向形成布局循环。
`Declare` 可固定窗口／映射 profile，帧内只变 `rect,t`；未声明窗口或过预算的动态窗口具名拒绝。

时间索引建议每 64 展示帧一个累计块：保存时间和 `quantizedValue*durationUs` 的 checked uint64 累计量。
首尾部分最多扫描两个块；完整中间区间用累计差，避免全历史遍历和长时间 double 前缀抵消。
原量保持整数直到权重归一化；所有乘法和总时长先证明上界，恶意偏移／累计溢出拒绝。
每个 `(artifact,mapping,t,window)` 只生成一次当前 32×18 统计网格；建立 2D 积分网格后矩形查询用四角面积积分。
单元内分段常量的积分在分数坐标精确计算；“精确”只相对于网格近似，不相对于原始源图。
缓存须包含状态／制品摘要、映射、窗口、时刻及 profile；冷／热／淘汰结果一致，不能靠作者字段累积滞回。

GPU 使用独立的 typed statistics 输入及两个 RGBA float 数据纹理，不能假冒受预乘颜色校验的普通 `Color` 图输入。
首版候选每固定输入最多 4 种窗口 profile；每帧统一计算／上传当前网格，各 shader 共享，不在输出像素循环读 2 s 历史。
4 窗口的两张 RGBA32F 主数据为 73,728 B／帧；上传和双缓冲仍需登记，不能据体积声称性能通过。
纹理采样规则、数据通道、精度、坐标和 CPU 公式由同一 profile 冻结；统计／颜色输入类型不隐式互转。
GPU 点查询使用与 CPU 相同的单元选择，区域查询使用相同面积积分；普通双线性纹理取样不能悄悄替代该统计合同。
对 `ImageGraphDefinition`、图验证／绑定、`CompiledImageGraph` 和材质输入增加媒体依赖，而非另建旁路 shader 执行器。
`SkiaRenderPipeline.ImageScene.Materials.cs` 与 `.Binding.cs` 继续区分真实 BackgroundMedia、真实 BackdropBelow 和新统计输入。
透明输出固定为 NoMedia 并执行已声明 fallback；不能偷偷采样不被渲染的工程背景。

### 5.1 已实施的包查询声明与纯依赖计划（0.112.26）

v2清单根和每个export可选`mediaQueries`；旧包不强加新字段，v1出现该字段拒绝。根目录每项
为`id`、`space`（`sourceNormalized`／`canvasPixels`）和`window`：后者明确为
`{"kind":"literal","durationUs":200000}`或`{"kind":"parameter","parameter":"window"}`。
参数必须是同包Duration且当前export显式引用，完整允许范围与最终值均须在0..2000000µs。

export的每项引用为`query`和`fallback`，后者明确包含`pending`、`unavailable`、`noMedia`。
每种状态均含`kind`和`outputs`，首版要求精确覆盖当前export的全部输出；controller仅允许
`authorBranch`，material／graph仅允许`outputBypass`并引用原来已验证的材质fallback／图原输入。
不复制另一份颜色、图节点或旁路协议。未知、缺失、重复字段与未声明query均拒绝。

`EffectPackageMediaQueries.Read`返回不可执行catalog，共用internal结构读取器；唯一可执行
`EffectPackageDefinition.Read`仍检查全部包／输出能力。`media.features`仅known-pending，
未开放执行；`media.features.cpu`仍是未知能力。声明存在不证明作者分支或渲染已接通。

`ProjectMediaDependencyResolver`按启用实例及完整`SourceMaterialCatalog`（含不可见override）
求闭包，固定精确包revision／export和唯一参数解析结果，保留真实owner path／region。
未引用包及disabled实例不制造依赖；无查询owner仍严格验证其export和参数。所有依赖的窗口
联合至多四种，batch64不充当包目录或owner数上限。

身份分域为`mako.media.owner-parameters.v1`、`mako.media.query-catalog.v1`和
`mako.media.dependency-plan.v1`：strict UTF8长度前缀、little-endian整数／double原位、显式
集合数量和九类typed参数；query及参数按UTF8字节序，owner沿实际源执行顺序。完整参数、
三状态输出归属和精确包revision进入固定依赖摘要；不反向包含input revision、mapping、
source key或状态，避免身份循环。源码编码见`Core/Effects/MediaDependencyEncoding.cs`。

### 5.2 已实施的 SDK 查询表面与阶段句柄（0.112.28）

`DeclareContext.Media` 为不可变 `MediaQueryCatalog`，仅含固定安全输入、当前export查询
profile及不持资源的目录作用域；准备阶段没有Sample。`TextFrame.Media`／`VisualFrame.Media`
新增本次调用的 `MediaFeatures.Sample(id, rect, projectTimeUs)`。查询id必须由当前目录颁发，
default／另一目录同名id拒绝；窗口固定于profile，四double边与整数微秒显式传入。

SDK输入四状态为NoMedia／Pending／Available／Unavailable；Core两种Available投影到同一
状态，文件才有artifact摘要。Source／Canvas返回不同只读数值类型，非Available两者均空，
仍拒绝非法query／矩形／时间。元数据仅允许安全摘要和诊断码，不含路径、reader或callback。
保留旧构造器：无绑定时Catalog.Input=null且Profiles为空，Sample具名拒绝，不伪造NoMedia。

服务内部以锁覆盖Sample与Invalidate，关闭时清除callback；采样前后检查取消及精确结果
input／query／rect／time。目录、profile、id及数值快照可进只读prepared，服务不得保留。
纯度规则身份提升到`sdk2-purity-6`：递归stage类型、helper参数别名、分支／循环／try和属性
模式纳入检查；普通明确安全重赋值保留接受。goto明确拒绝，loop／try别名保守汇合；该有限
确定性子集检查不是任意C#安全沙箱。没有扩大BCL权限或新增CPU capability。

本片仅实现表面、内部绑定入口与句柄机制。实际Host每次调用finally关闭、精确owner投影、
provider／factory／锚点和图消费者仍待接线；`media.features`继续known-pending。真实作者
编译与相邻回归证据见[M10记录](script-sdk-render-m10-evidence.md)，不据本片宣称媒体可执行。

## 6. 源制品、映射与运行输入身份

建议新增不可变 `MediaFeatureInput(State, SourceKey, InputRevision, Profile, Artifact?, Diagnostic?)`，状态限定如下。

| 状态 | 固定内容 |
|---|---|
| Pending | 精确 source key／算法／颜色 profile、依赖任务引用或请求身份；使用明确 fallback |
| Available | 同 source key、规范制品 hash／length／frame count／duration、颜色／索引 profile |
| Unavailable | 固定失败码及受限诊断；重试成功也必须创建另一个 InputRevision |
| NoMedia | 无媒体／透明输出等明确原因与纯色策略；不冒充分析失败 |

纯色是独立 source kind；普通输出可直接产生解析 Available 常量输入，无需 Job／制品文件。
NoMedia 专指该输出模式没有媒体贡献；纯色可读性策略不能被模糊成“缺视频”。

三层身份分开：

1. **SourceKey**：媒体内容摘要／stream／原始时间轴语义／旋转 SAR／规范 decoder 与 tool identity／颜色／统计／量化 profile。
2. **ArtifactIdentity**：完整规范输出 hash、length、profile；同一 SourceKey 若规范输出不同，不冒充同一制品。
3. **Mapping/InputRevision**：状态与制品身份，加 canvas／fit／playback／底色／输出 alpha／坐标／窗口 profile／工程与包固定输入。

源轨不含工程画布和 loop 策略，允许在不同工程映射复用；映射变化不必重解码，必须重建对应固定输入／memo。
`ProjectEffectInputs.Fingerprint` 需要纳入不可变媒体输入身份；不能只增加 background 路径、任务 id 或工程 SourceRevision。
`ProjectSnapshot` 保存摘要和固定只读轨引用，生命周期由运行 lease 持有；不得保存可变 task／全局缓存对象。
已完成轨进入新快照；旧 Pending／Unavailable 对任意 t 都保持原状态，不能被后台事件原地改写。

持久化首版建议采用**派生缓存**：不自动产生工程历史、dirty 或音频式 apply 编辑。
保存工程继续保存原媒体内容闭包；重开根据 SourceKey 复用验证通过的持久缓存，否则回到新 Pending 输入。
若后续要求随工程离线携带轨，另开显式内容地址引用／闭包 schema 和资源预算；不能保存 TTL JobId 当永久引用。
Job 授权引用与全局内容去重分开，命中相同 hash 不授予跨 client／project 的读取权限。

## 7. 任务、流式制品与恢复

建议新增 `BackendMediaFeatureContracts.cs`、`WorkerMediaFeatureContract.cs`、`IBackendMediaFeatureExecutor`、
`ProjectSessionRegistry.MediaFeatures.cs`、`StaticMediaFeatureTask.cs` 与 `BackendMediaFeatureArtifactStore`，沿现 Job 账本接通。
Worker／Server 能力须共同确认 decoder、颜色／统计 profile 和全部预算；未探测能力保持关闭。
新增消息仅传严格输入 grant、进度与制品摘要／授权候选引用；不把几十 MB 网格写进 4 MiB IPC payload。

任务顺序：Writer 下 CAS／固定 source lease → 权限／profile 检查 → 磁盘与分析 slot 预留 → 注册权威 Job →
等待 slot／执行 → 子进程与 pipes 排水 → 校验完整候选轨 → 取消检查 → EnterCommitting → 原子发布 → Finish。
进度建议 `stage,completedFrames,totalFrames?,presentationTimeUs?,bytesWritten`，每字段明确单调条件，不滥用音频 samples DTO。
任务取消、失败和超时释放候选、slot、source lease；保留主异常，清理异常不能把失败包装成成功。

制品建议固定二进制 profile：little-endian header、源／算法／颜色摘要、时间区间表、帧统计、块累计索引和保留零字段。
offset／count／length 必须无重叠并完整覆盖文件；拒绝尾随字节、未知版本、错误 checksum／累计索引和非规范填充。
全件 content hash 从规范字节计算，hash 不写入自身形成循环；候选经流式校验和 fsync 后才获得已提交引用。
Host 与 Worker 的读取校验覆盖声明 hash／length、完整格式和边界；源文件分析前后核验，不接收 HTTP 提供的本地路径。
使用分块读／有界 mmap／LRU；IPC、HTTP DTO、ProjectSnapshot 和 JS 均不整体 materialize 全轨。
存储租约在 TTL 后仍占账直至最后读者释放；prune 同时考虑 Job／恢复账本／活跃预览／导出 pin。
重启只接受 ledger 及制品双重验证的已提交结果；遗留候选不是 Available。失败恢复进入新输入身份，不修补旧快照。

候选安全能力上限可先取：源时长 ≤3,600,000,000 µs、展示帧 ≤54,000、单轨 ≤512 MiB、
decode 工作内存 ≤256 MiB、映射／轨读取 cache ≤96 MiB、4 窗口，并分别限制源像素尺寸／源字节／总存储／并发。
这组数值是待实现确认的安全边界，**不是已批准性能门槛**；帧数／时长／大小各自独立，不能只检查其中一项。
按该格式 54,000 帧主统计约 497.7 MB，块索引约 27.2 MB；尚须把时间表／header／校验和计入实际文件上限。
所有尺寸、缓存、解码队列、prefix 和 GPU 副本在分配前计账；越限具名拒绝，不能降采样时间或静默减少统计。
每帧 4 窗口最坏首尾块读取工作也登记预算；有限复杂度不等于达到显示帧时要求。

## 8. 必需 DTO 与 runtime grant

| 新增／扩展建议 | 最小固定字段及用途 |
|---|---|
| `MediaFeatureTaskCapabilitiesDto` | source／decoder／color／statistics profiles，stream／HDR 支持，时长／帧数／字节／内存／窗口／调度／超时预算 |
| `MediaFeatureJobRequest` | ExpectedRevision、精确背景来源／SourceKey 或受控推导选择、Metadata；不接受本地路径 |
| `BackendMediaFeatureJobInputDto` | source kind／id／revision、asset hash／size、stream、完整目标 profile |
| `BackendMediaFeatureProgressDto` | stage、frames、可选总数、展示时间和写入字节；有界投影 |
| `MediaFeatureJobResultDto` | SourceKey、ArtifactIdentity、profile、规范 duration／frame count、report；不内联网格 |
| `MediaFeatureInputSummaryDto` | State／InputRevision／SourceKey／制品摘要或失败原因，供 UI 显示明确状态 |
| `PreviewSourceDto`／预览响应及帧元数据 | 增加精确 MediaInputRevision 或独立 runtime input identity；保持 SourceRevision 的工程／草稿原义 |
| GPU stream state／replace | 回显同一媒体输入身份，仍使用 ExpectedStreamRevision 与完整 input replacement |
| `BackendExportInputDto`／report／failure | captured source identity、媒体依赖集合与每项固定 hash／profile／等待结局 |
| `ProjectRuntimeMediaFeatureGrant` | Host 私有文件路径、hash／length／profile／SourceKey／输入修订；只在受信 runtime envelope 中出现 |

`JobResponse`／summary／event／恢复记录须投影同一权威媒体任务状态；新增错误码要保留 owner／依赖归属。
媒体状态接口可供 UI 发现新就绪身份，但获取状态不更改任何已存在预览；明确采用／换代请求才建立新固定输入。
对旧客户端不请求媒体能力的入口保留现有行为；新请求必须明确携带受支持身份，不能让缺省字段偶然选择最新 cache。

runtime envelope 升级 profile／engine gate，Host 完整重编码对照 snapshot，Worker 必须拒绝未知媒体 grants。
`ProjectSession.RuntimeInput.cs` 捕获并 pin 源及轨 lease，Prepare 将其放入受控闭包，校验后与其余 package grants 一起绑定。
Worker Open 在创建 render session 前检查 no-links、hash／length、格式 profile、source key 与 snapshot identity；整个会话仅借用此固定轨。
Prepare／打开失败释放候选 lease；旧输入继续按现有状态使用，不能把失败换代报告成功。

## 9. HTTP／GPU 预览换代

HTTP 流程保持：捕获精确工程／草稿 + MediaInputRevision → Prepare 固定闭包 → 能力校验 →
提交 `ExpectedPreviewGeneration` 换输入 → 新 generation 清空 LatestFrame／pending／diagnostics → 旧资源等在途帧排水后释放。
帧提交继续核 generation、source／media input identity、factory／snapshot；后台完成的旧帧不得覆盖新输入。
`candidate`／`template`／`editCandidate` 等当前不允许的更新来源应新建会话；不能因媒体就绪绕过门禁。
authorDraft 更新保留同一草稿 source identity／CAS，不把媒体修订写成作者源码修订。

GPU 必须调用现有 ChangeGpuStream 路径：验证 expected stream revision → 捕获／Prepare 新输入 →
TryBeginChange → 关闭并排水旧 Worker → 新 inputGeneration 的 Worker → 新 producer／pool generation → CompleteChange。
运行中失败／quarantine 保留具名失败与租约清理；不能只更新 shader／共享纹理、热补旧 snapshot 或伪装成 seek reset。
新 producer／pool 与新分析输入同时回显，消费端旧帧仍带旧身份并按代次拒绝。
本步骤保留既有浏览器静态预览协议及 500 ms 控制节奏；分析完成事件不引入另一套高频控制循环。

## 10. 导出等待与制品 pin

有效依赖从已启用实例、实际绑定材质／图导出、固定参数和能力声明求闭包；包含无 controller 的资源包。
不能只搜索作者源码调用，也不能等导出首帧碰到查询才发现缺轨；未声明使用的能力保持拒绝。
无依赖的导出不启动媒体分析；NoMedia／透明模式只执行已验证 fallback，不等待一个不会参与输出的背景任务。

建议调整运行导出流程如下：

1. Writer 下 CAS，捕获工程／包／媒体 source key／输出模式并 pin 原媒体；释放 Writer 后才执行慢准备。
2. 登记权威 export Job 和有界依赖等待，返回 accepted；依赖阶段不预留会立即 Grant 的 render slot。
3. 查找同 SourceKey 的已授权规范制品，或以同一任务账本启动／复用分析；等待有 timeout／取消及明确阶段。
4. 成功后取得轨 read lease，核摘要／profile／source，再生成固定 MediaInputRevision 和 runtime envelope。
5. 完整固定所有依赖后才预留磁盘候选及 export render slot；后续工程修改不改变已接受的来源。
6. Worker 打开所有 pinned grants 后渲染；中途不查询“最新 cache”，结束后按现有顺序排水并释放源／轨。
7. report 记录 capture identity、各依赖的制品摘要及实际执行 profile；失败报告记录依赖 owner／id／具名原因。

当前调度默认每 client／project 仅 1 个 running，且 `TryReserve` 可立即占位；先拿 export slot 再等同工程 analysis 会死锁。
若增加 waiting-dependency 状态／子阶段，仍只有一份权威 Job；等待数量与源租约也纳入预算，不能另建无界后台任务。
取消 export 只取消自己的 waiter 和保留租约；不能无条件取消其他预览／导出共享的分析任务。
依赖失败、取消、能力不支持、源变化和超时都有限终止；不能改用临时 fallback 导出并声称等价。
恢复中的 export 只有完整固定依赖和源闭包均可验证时才能继续；否则明确终止，不重定向到新工程或新算法结果。
预览／导出一致只针对相同 Available identity、时间、映射与输出模式；Pending fallback 预览不参与该等价声明。

## 11. 实施分片与验收矩阵

建议按顺序独立落地：展示解码／颜色合同 → 规范轨 codec 与 store／job → 固定输入／SDK／图查询 →
HTTP／GPU 换代 → 导出依赖 pin → T01／作者链／性能。每片先有具名拒绝与资源边界，再开放 capability。

| 门禁 | 夹具与必须验证的事实 |
|---|---|
| 展示时间 | VFR、非零 start PTS、B 帧、切镜、尾帧、缺失／重复／乱序 PTS，边界 ±1 µs；背景像素和轨选择同一帧 |
| 颜色／alpha | sRGB／BT.709 合法标签、full／limited、透明 PNG、已知线性棋盘、未知／HDR 拒绝；真实背景与统计共用颜色合成 |
| 真二阶量 | 相同均值的纯灰与黑白纹理具有不同方差；alpha 与底色公式对照直接规范源像素统计，量化误差独立登记 |
| 映射／窗口 | 三种 fit、留白／裁切、loop 接缝、多圈短片、freeze、负窗口起点、0／2 s／超限窗口、非法 rect |
| codec／预算 | 截断／尾随／错 offset／溢出／错 prefix／错 hash，长视频流式读写、磁盘／内存／查询／上传上限，取消后候选清理 |
| 固定身份 | Pending→Available／Unavailable，新旧输入同工程 revision 仍可区分；乱序／重复／memo 冷热／淘汰／重开输出一致 |
| 预览换代 | HTTP 新 generation 与旧帧阻断；GPU 旧 Worker 排水／新 inputGeneration／producer／pool；失败不假 ACK |
| 导出任务 | 无依赖不分析、并发 1 无死锁、共享 waiter 取消隔离、失败／超时有限终止、TTL／prune 不移除已 pin 制品 |
| C#／GPU | 同 profile 网格／区域／窗口统计及材质值对照；真实 BackgroundMedia／BackdropBelow 未被统计轨替代 |
| T01／可读性 | 明暗混杂、细碎纹理、镜头切换、透明、循环；描边切换无作者字段滞回，视觉核查而非单阈值自证 |
| 性能／资源 | 近端／3 min／更长时间分开记录准备、顺序／乱序 seek、显示、端到端、内存／GPU／上传；原始证据保留 |

构建／定向回归、元数据生成合同与 validate、匹配 Server／Worker 的 fresh HTTP、GPU 实帧与浏览器作者链分别登记。
按用户2026-11-05指示，性能门槛暂不参与本轮验收；正确性、像素、确定性及预算／寿命门禁
继续执行。本文件候选预算不关闭D8／T01，也不证明安全边界内都满足实时要求。
本页不单独声明实施验收结果，实际日志、提交范围与未完成接线见M10证据及阶段一进度记录。


### 5.3 已实施的中立 binding 与同步采样器（0.112.29）

`IProjectMediaFeatureSampler`固定完整input和唯一plan，Sample要求exact dependency引用、
四double边界和显式工程微秒，返回无grid／reader／lease的数值快照。binding复核input摘要，
并提供重新resolve完整snapshot闭包、canvas与output的显式入口；后续factory必须调用。
reader必须来自完整codec/store验证并与provider使用同runtime ledger，这由受信组合根保障。

文件每个distinct window只保留一个最近精确时刻槽，至多四槽；同time/window跨owner和
space共享。Source直接积分七raw矩，Canvas复用实际SAR／rotation后的placement与一次底色
合成；Solid以正式量化kernel建立唯一timeless grid。Pending／Unavailable／NoMedia固定
返回无数值状态，不读文件也不分配grid。所有请求先校验身份、rect和time，失败不伪造黑色。

预算公式为checked的`slots * 576 * 7 * 8 + 512 + slots * 64 + WorkingBufferBytes`，
Solid再计固定quantized scratch；分配前预留、失败回滚。Dispose先停query admission，再
等gate排水后退自身预算；借入reader由外层owner在session/provider排水后唯一关闭。
该片仅内部CPU接缝，未开放执行capability；有限数值与寿命证据见[M10记录](script-sdk-render-m10-evidence.md)。
