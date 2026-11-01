# 动画、场景编排与组合预设参考

本文依据 App 仓库后端源码 `f3fdfdf210c69dcf1af15537e855d5559ec9ce09`、产品版本 `0.109.0` 静态整理。时间为整数微秒，区间为 `[startUs, endUs)`；下文模型名采用 C# 拼写，HTTP 字段和枚举以对应合同及 JSON 转换器为准，不能机械地把 C# 枚举名当作 wire 值。范围描述的是对应模型的校验，不暗示所有合法模型都能通过某一个编辑命令构造。静态歌词样式字段和脚本 SDK 方法另见同目录参考。

## 行动画

[`LineAnimation`](../../src/MakoKaraGenerator.Core/Projects/LineAnimation.cs) 是进入动画，结束恒为 opacity=1、scale=1、offsetY=0；不是关键帧动画。普通显示从行起点开始，前奏准备显示从揭示起点开始，起唱不重播。动画 opacity、scale 分别与静态样式相乘。行级 null 继承工程默认；[编辑合同](../../src/MakoKaraGenerator.Backend.Contracts/BackendEditingCompletionContracts.cs)提供 `orchestration.setLineEnterAnimation` 和 `orchestration.setProjectEnterAnimation`，实际解析与 null 语义见[处理器](../../src/MakoKaraGenerator.Backend.Hosting/ProjectSession.EditingCompletion.cs)。

| 字段 | 默认 | 校验 / 意义 |
| --- | --- | --- |
| `DurationUs` | 400000 | 0–10000000；0 立即到位 |
| `Easing` | `EaseOut` | 下表四种 |
| `StartOpacity` | 0 | 有限数 [0,1] |
| `StartScale` | 1 | 有限数 (0,4] |
| `StartOffsetPx` | 12 | 有限数，绝对值 <=10000；正值向下 |

[`AnimationEasing`](../../src/MakoKaraGenerator.Core/Projects/AnimationEasing.cs) 先将进度 t 限制到 [0,1]：`Linear` 为 t；`EaseIn` 为 t²；`EaseOut` 为 1-(1-t)²；`EaseInOut` 在 t<0.5 时为 2t²，否则为 1-2(1-t)²。此集合不同于效果包参数曲线。

## 槽位序列

[`SlotAnimationSpec`](../../src/MakoKaraGenerator.Core/Projects/SlotAnimationSpec.cs) 存在于布局节点；槽位位置来源于节点，转换为临时 [`SlotSequenceAnimationProfile`](../../src/MakoKaraGenerator.Core/Projects/SlotSequenceAnimationProfile.cs) 调度。后者 `Default` 有 `upper-left` (0.28,0.30) 和 `lower-right` (0.72,0.70) 两槽；普通新 profile 的空槽集合本身无效。槽位至少一个，ID 非空且唯一、位置有效。

| 字段 | 默认 | 有效范围 / 意义 |
| --- | --- | --- |
| `RevealMode` | `Progress` | `Progress` 上一行进度；`TimeAfterPreviousStart` 上行开始后延迟；`LeadBeforeNextStart` 下行开始前提前量 |
| `RevealProgress` | 0.5 | 有限 [0,1] |
| `RevealDelayUs`, `RevealLeadUs` | 0 | 非负整数 |
| `MaxVisibleLines` | 2 | >=1；`AllowOverflow` 可超出该值 |
| `ConflictPolicy` | `QueueUntilSlotFree` | 排队等空槽；`AllowOverflow` 允许溢出；`ReplacePrevious` 提前结束旧行；`Skip` 跳过新行 |
| `RetentionPolicy` | `UntilLineEnd` | 保留至行末；`UntilNextReveal` 保留至下次揭示；`Immediate` 下行满足条件时立即隐藏 |
| `EnterDurationUs`, `ExitDurationUs` | 300000, 250000 | 非负整数 |
| `EnterFrom`, `Hold`, `ExitTo` | opacity 0、Y +12；中性；opacity 0、Y -12 | 均为有效 SceneTransform |
| `Easing`, `ExitEasing` | `EaseOut`, `EaseIn` | 分别用于进入、退出；取上表四种 |

调度以实际行时间计算揭示和隐藏，不修改源歌词时间；无效或缺少时间的处理以[调度器](../../src/MakoKaraGenerator.Core/Projects/SlotSequenceAnimationProfile.cs)为准。`orchestration.setSlotAnimation`、槽位增删排序以及布局设置见[编辑合同](../../src/MakoKaraGenerator.Backend.Contracts/BackendOrchestrationPropertiesContracts.cs)和[命令转换器](../../src/MakoKaraGenerator.Backend.Contracts/ProjectCommandJsonConverter.cs)。

HTTP 的槽位时长、延迟和提前量使用仅含 ASCII 十进制数字且可解析为非负 `Int64` 的字符串，不接受 `+1` 等带符号写法；随后执行 profile 校验，见[JSON 转换器](../../src/MakoKaraGenerator.Backend.Contracts/OrchestrationPropertiesCommandJson.cs)。枚举名按大小写不敏感解析。槽位动画命令要求三个 transform 完整提供，具体字段见[处理器](../../src/MakoKaraGenerator.Backend.Hosting/ProjectSession.OrchestrationProperties.cs)。行进入动画命令的缓动名则按大小写精确匹配，时长仍受 Core 的 0–10000000 上限约束，见[处理器](../../src/MakoKaraGenerator.Backend.Hosting/ProjectSession.EditingCompletion.cs)。

## 位置与容器

[`ScenePlacement`](../../src/MakoKaraGenerator.Core/Projects/ScenePlacement.cs) 和 [`SceneTransform`](../../src/MakoKaraGenerator.Core/Projects/SceneTransform.cs) 是不同层：前者是容器固定参考矩形内的位置，后者是动画通道。世界矩阵和父级关系见[几何解析](../../src/MakoKaraGenerator.Core/Projects/SceneTreeGeometry.cs)。

| 值对象及字段 | 默认 | 约束 |
| --- | --- | --- |
| Placement `Position.X/Y` | 0.5, 0.5 | 有限；可超出 [0,1] |
| Placement `Anchor` | `Center` | `Center`, `TopLeft`, `TopCenter`, `BottomRight`, `LeftCenter`, `RightCenter` |
| Placement `RotationDegrees`, `Scale` | 0, 1 | 有限；Scale >0 |
| Transform `Opacity`, `Scale` | 1, 1 | 有限；opacity [0,1]，scale >0 |
| Transform `OffsetXPx`, `OffsetYPx`, `RotationDegrees` | 0 | 有限 |
| `SceneReferenceRect.WidthRatio/HeightRatio` | 1, 1 | 有限且 >0；根相对画布，其余相对父参考尺寸 |
| `NormalizedSafeArea.Left/Top/Right/Bottom` | 0,0,1,1 | 有限且在 [0,1]，左<右、上<下；仅约束自动流式布局 |

[`SceneContainerDefinition`](../../src/MakoKaraGenerator.Core/Projects/SceneContainerDefinition.cs) 有必填 `Id`、可空 `Name`/`ParentId`，以及 `Placement`、`ReferenceRect`、可空 `Layout`/`Content`/`Attachment`。Layout 的 `Kind` 为 `Auto` 或 `SlotSequence`；`DefaultPlacement` 可空，`SafeArea` 默认全幅，`MaxWidthRatio` 默认 0.9 且 (0,1]，`SlotIds` 默认空，`SlotAnimation` 可空；完整结构约束以[展示校验器](../../src/MakoKaraGenerator.Core/Validation/PresentationValidator.cs)及其分部文件为准。节点树可编辑几何、布局种类和设置、父子关系、顺序、槽位与片段；具体命令以[命令转换器](../../src/MakoKaraGenerator.Backend.Contracts/ProjectCommandJsonConverter.cs)和[补全合同](../../src/MakoKaraGenerator.Backend.Contracts/BackendEditingCompletionContracts.cs)为准。

[`LyricDisplayProgram`](../../src/MakoKaraGenerator.Core/Projects/SceneContainerDefinition.cs) 保存 `Id`、`BranchId`、`RootContainerId`、`DefaultContainerId`、可空 `AnimationPolicy`、`Clips`、`LeadIns` 与可空 `LeadInGeneration`。策略 `Karaoke` 使用逐字进度，`Static` 不参与逐字动画，`FollowPrimary` 跟随主行整体可见区间；null 按角色解析，主歌词默认 Karaoke，其他默认 Static。`LyricDisplayClip` 包含 ID、可空名称、按源顺序连续的 `LineIds`、`ContainerId` 和可空内部 `ScheduleContext`；该上下文保存拆分前成员、是否默认段及前后边界行，以保留调度，不是另一份时间轴。没有进入显式片段的行由默认容器调度。行展示覆盖还包含 `LocalTransform` 和可空 `EnterAnimation`。见[展示模型](../../src/MakoKaraGenerator.Core/Projects/LyricPresentationDefinition.cs)与[片段编辑器](../../src/MakoKaraGenerator.Core/Projects/SceneTreeEditor.Clips.cs)。

[`LyricLocalTransform`](../../src/MakoKaraGenerator.Core/Projects/LyricLocalTransform.cs) 的 `OffsetXRatio`、`OffsetYRatio`、`RotationDegrees` 默认 0，`Scale` 默认 1；数值必须有限、scale 必须为正，偏移允许超出画布。

### 快速基础布局

[`BasicLayoutPreset`](../../src/MakoKaraGenerator.Application/BasicLayoutPreset.cs) 是固定槽位几何，不包含样式或效果。下表位置为父参考矩形的归一化坐标，未特别注明时锚点为 Center。

| 预设 | 槽位位置和锚点 |
| --- | --- |
| `SingleCentered` | (0.5, 0.75) |
| `KaraokeDouble` | (0.05, 0.6) LeftCenter；(0.95, 0.8) RightCenter |
| `DoubleCentered` | (0.5, 0.55)；(0.5, 0.77) |
| `TripleCentered` | (0.5, 0.55)；(0.5, 0.66)；(0.5, 0.77) |

`orchestration.previewBasicLayout` 先预演，`orchestration.applyBasicLayout` 才提交；两者需要布局名称、与预设槽数匹配的槽位名称、选中及保留片段名称。选择整条来源或源顺序连续的非空行范围；操作可能建立布局节点和拆分片段，并返回影响计数和新片段 ID。只接受独立计时且策略为 Karaoke 的分支，不接受静态或 FollowPrimary 来源。预设采用槽位序列默认动画，但 `MaxVisibleLines` 设为槽位数。详细范围和无变化判定见[操作](../../src/MakoKaraGenerator.Application/BasicLayoutOperation.cs)及[HTTP 处理器](../../src/MakoKaraGenerator.Backend.Hosting/ProjectSession.cs)。

## 前奏和文字标记

[`LyricLeadIn`](../../src/MakoKaraGenerator.Core/Projects/LyricLeadIn.cs) 字段：必填 `Id`、`TargetLineId`、`IndicatorContainerId`；`Name` 可空；`Enabled` 默认 true；`DurationUs` 默认 3000000 且 >0；`Origin` 默认 `Manual`，另一值 `Generated`。用户修改的手工项不被自动生成覆盖。目标必须有有效行时间与提示容器；时长不短于标记数。锚点优先首个有有效时间的 token，否则使用行起点；完整准备区间必须能放在锚点之前，不足时不会自动截短。标记按区间等时分步，原始歌词时间不被改写。具体关联、冲突与步骤校验见[前奏验证](../../src/MakoKaraGenerator.Core/Validation/PresentationValidator.LeadIns.cs)和[采样](../../src/MakoKaraGenerator.Rendering.Abstractions/LyricSceneSampler.LeadIns.cs)。

| 自动生成字段 | 默认 | 含义及限制 |
| --- | --- | --- |
| `IncludeOpening`, `IncludeLongGaps` | true, true | 开头、长间奏候选 |
| `GapThresholdUs` | 8000000 | >=0；等于阈值也纳入 |
| `DurationUs` | 3000000 | >0 |
| `IndicatorContainerId` | null | 所选提示容器 |
| `ExcludedLineIds` | 空 | 明确排除的行 |

`CountdownSequenceContent` 按顺序引用直接子文字标记 `MarkNodeIds`，有 `Appearance`；`TextMarkContent` 有 `Text`（默认 `●`）和可空局部 `AppearanceOverride`。序列至少一个标记，引用唯一且必须与直接子标记一致。`SceneAttachment.Mode` 为 `FixedParent` 或 `CueTargetText`；后者跟随目标句的槽位、微调和动画。见[内容模型](../../src/MakoKaraGenerator.Core/Projects/SceneContent.cs)。

[`SceneTextAppearance`](../../src/MakoKaraGenerator.Core/Projects/SceneTextAppearance.cs) 字段为 `Typography`（默认字体配置但字号 32px）、`PendingFill`（白色 RGBA 255,255,255,255）、`ElapsedFill`（95,95,95,255）、可空 `Stroke`/`Shadow`/`Glow`、`Opacity`（1）。辉光按后续工作树源码补充，详见[静态辉光](glow.md)。它复用静态样式值的校验；局部覆盖允许分别覆盖这些外观，显式 null 可关闭描边/阴影/辉光。组外观是子标记基线，不再作为父透明度相乘。前奏创建、更新、删除，生成预览/应用/排除，标记增改排序/均匀分布，外观及附着命令见[前奏合同](../../src/MakoKaraGenerator.Backend.Contracts/BackendLeadInContracts.cs)和[编辑合同](../../src/MakoKaraGenerator.Backend.Contracts/BackendLeadInEditingContracts.cs)。

HTTP 限制比仅查看 Core 值对象更具体：创建倒计时的标记数为 1–64，添加和排序也受 64 个标记上限及节点预算约束；标记文本非空白且 UTF-16 长度不超过 4096。生成设置的排除 ID 与应用选择 ID 各至多 1000 项，分析的分支至多 1000 行，并受源树、响应和回执预算限制。时间字符串须解析为 64 位整数，duration >0、gap >=0；目标、引用及可用区间还须通过领域校验。见[创建处理](../../src/MakoKaraGenerator.Backend.Hosting/ProjectSession.LeadIns.cs)和[生成与标记处理](../../src/MakoKaraGenerator.Backend.Hosting/ProjectSession.LeadInEditing.cs)。

## 场景媒体与临时图元边界

持久编排树的内容联合类型目前只有倒计时组和文字标记；歌词与注音由源数据投影。渲染器重建的 [`SceneDefinition`](../../src/MakoKaraGenerator.Rendering.Abstractions/SceneDefinition.cs) 节点类型为 Group/Text，内部布局有 FreeLine、SlotSequence、TranslationFlow、Ruby、None；这不等于通用持久 image/shape 场景编辑器。脚本可以输出点、线段、轨迹、精灵以及绑定图像效果，详见[临时图元](../../src/MakoKaraGenerator.Scripting.Abstractions/VisualWriter.cs)、[向量](../../src/MakoKaraGenerator.Scripting.Abstractions/VectorOverlays.cs)、[精灵](../../src/MakoKaraGenerator.Scripting.Abstractions/SpriteOverlays.cs)和[图像效果目标](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameWriter.ImageEffects.cs)。这些是效果执行输出及其受限绑定，并非可持久自由编辑的通用 shape/image 节点。背景媒体和材质另有独立模型，本页不将其等同编排节点。

### 画布与背景

[`Composition`](../../src/MakoKaraGenerator.Core/Projects/Composition.cs) 的 `CanvasWidth`、`CanvasHeight` 默认为 1920、1080，均须 >0；`FrameRate` 默认 30 fps；`BackgroundColor` 默认不透明黑色；`OutputDurationUs` 默认 null，指定时须 >0。背景色先铺满画布。[`BackgroundSource`](../../src/MakoKaraGenerator.Core/Projects/BackgroundSource.cs) 是其上的独立媒体层。

| 背景字段 | 默认 | 选项与意义 |
| --- | --- | --- |
| `Kind` | `Color` | `Color` 仅底色；`Image` 图片；`Video` 视频帧 |
| `Path`, `Media` | null | 内存中媒体路径及受管内容身份；发布和读取时执行资源校验 |
| `Fit` | `Contain` | `Contain` 保持比例完整显示；`Cover` 保持比例铺满并裁切；`Original` 原像素居中并可能裁切 |
| `Playback` | `Loop` | `Loop` 循环；`FreezeLastFrame` 结束后停在最后一帧；仅视频参与采样 |

透明图片会与底色合成，未被媒体覆盖处显示底色及其 alpha；[透明输出请求](../../src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.cs)则清透明并跳过背景层，不等同只把底色 alpha 设为零。输出能力还受目标、图效果和具体 HTTP profile 约束。背景 HTTP 使用 `composition.setBackgroundColor` 的四个 byte 通道、`composition.setBackgroundAsset` 的受管 asset ID、`composition.clearBackground`、`composition.setBackgroundFit`、`composition.setBackgroundPlayback`；wire 值和资源校验见[命令](../../src/MakoKaraGenerator.Backend.Contracts/BackgroundCommandJson.cs)及[处理器](../../src/MakoKaraGenerator.Backend.Hosting/ProjectSession.Background.cs)。静态快照仅接受 Color，图片和视频需匹配支持该媒体的资源准备/profile，见[静态编解码](../../src/MakoKaraGenerator.Backend.Rendering/StaticProjectSnapshotCodec.cs)和[运行时输入](../../src/MakoKaraGenerator.Backend.Rendering/ProjectRuntimeInputCodec.cs)。

## 模板、配方与应用

### 两套内置集合

[`LyricStylePresetLibrary.Default`](../../src/MakoKaraGenerator.Core/Projects/LyricStylePresetLibrary.cs) 是 Core 的不可变整份样式预设集合，不能据此推断它已作为 HTTP 模板库或动态渲染来源公开：

| ID / 名称 | 与基线的主要差异 |
| --- | --- |
| `default` / 默认 | `LyricStyle.Default` |
| `karaoke-large` / 大字 | 字号 120px |
| `karaoke-warm` / 暖色 | 字号 96px；未唱 #6B5140、已唱 #FF8A3D |
| `karaoke-gradient` / 渐变高亮 | 字号 96px；未唱渐变 #4A5060 → #6E768A，已唱渐变 #00E5FF → #7C4DFF |
| `karaoke-outline` / 描边阴影 | 字号 104px；未唱 #F2F4F8、已唱 #FFD166；描边 #101218 宽 6px，黑色 alpha 170 阴影角度 90°、距离 4px、模糊 8px |

独立 [`StyleTemplateLibrary.BuiltIns`](../../src/MakoKaraGenerator.Core/Persistence/StyleTemplateLibrary.cs) 恰有两项：`builtin:line-classic`（经典整行，Line，默认样式及全部样式属性）和 `builtin:token-highlight`（逐字强调，Token，默认样式的未唱/已唱颜色属性）。二者均无效果配方；`builtin` 库只读，个人库 `personal` 可经模板库命令创建、复制、替换、改名和删除，个人模板不可冒用 `builtin:` ID。复制内置项创建个人模板不改变内置项，见[模板库处理](../../src/MakoKaraGenerator.Backend.Hosting/ProjectSessionRegistry.TemplateLibrary.cs)。

[`StyleTemplate`](../../src/MakoKaraGenerator.Core/Projects/StyleTemplate.cs) 为独立模板：`Id`、`Name`、`Kind`（`Line`、`Token`、`Composite`）、完整 `Style`、实际写入的 `Properties`、有序 `EffectRecipes` 和固定 `Packages`。属性与配方至少一项；Token 属性受 token 支持集限制，Composite 禁止 `VerticalCenterRatio` 和 `HorizontalAlignment`。固定包包含实际资源修订，不从外部库临时读取。

| [`TemplateEffectRecipe`](../../src/MakoKaraGenerator.Core/Projects/TemplateEffectRecipe.cs) 字段 | 含义 / 默认 |
| --- | --- |
| `RecipeId`, `Name`, `PackageId`, `Revision`, `ExportId` | 非空身份及固定包导出 |
| `Target` | `Line`、`TokenFamily`、`Application` 相对目标 |
| `Parameters` | 默认空对象，按导出声明解析覆盖 |
| `OverlayLayer` | 默认 `Package`；也可 `BehindText`、`AboveText` |
| `Enabled` | 默认 true |
| `ReadScope` | 默认 `BoundSources`；还可 `Project`、`BranchInput` |
| `BranchInput` | 仅 `BranchInput` 时为必填相对输入名 |
| `FixedSeed` | 可空；空值由应用建立种子 |
| `ConfigurationId` | 可空，供明确更新定位，不是运行时外部依赖 |

[`PresetApplication`](../../src/MakoKaraGenerator.Core/Projects/PresetApplication.cs) 固定 `Id`、单个具体 `Target`、模板 `Snapshot`、每配方/相对成员的 `Identities`（recipe、binding、instance ID、seed、enabled）、`BranchInputs` 和 `Enabled`。每对象最多一条直接应用，多选通过事务建立多条；应用启用状态与成员启用状态共同决定实例是否运行。可通过 `presetApplications.apply/update/setEnabled/remove` 修改，并可预览；见[合同](../../src/MakoKaraGenerator.Backend.Contracts/BackendPresetApplicationContracts.cs)。模板和预设应用不是仅保存模板 ID 的实时链接。

## 效果实例和参数

[`ProjectEffectInstance`](../../src/MakoKaraGenerator.Core/Effects/ProjectEffects.cs) 有必填 `Id`、`Name`、`PackageId`、`Revision`、`ExportId`；`Enabled` 默认 true；`Binding` 默认 Project；`Subscription` 默认 boundSources；`Seed` 默认 1；`Parameters` 默认 `{}`；`TemplateOrigin` 默认 null；`OverlayLayer` 默认 Package。`EffectOverlayLayer` 的 BehindText 在背景之后、全部文字之前，AboveText 在全部文字之后。实例集合保持顺序；资源预算默认 surface 536870912 bytes、live particles 20000、trail samples 200000、emission records 100000、draw commands 100000，均须 >0。

上述是工程模型能力，不表示 HTTP 已公开通用实例增删改命令；当前可核对的 HTTP 编辑入口是组合预设应用命令，包作者及资源操作另有合同。不要据此假定可通过 HTTP 单独修改任意直接实例的参数或顺序。

[`ProjectEffectBinding`](../../src/MakoKaraGenerator.Core/Effects/ProjectEffectBinding.cs) 的 `Kind` 可为 Project、Branch、Lines、Tokens、Clips、Node。Branch/Node 要单个 `Id`；Lines/Tokens/Clips 要非空且无重复 `Ids`；只有 Node 允许 `IncludeDescendants`。订阅与写入范围独立：`boundSources`、`project` 或 `branch`，仅 branch 要 `BranchId`。包声明、目标存在性及宿主写权限会进一步约束实际可执行性；声明允许绑定并不自动放宽组输出写权，组输出限定 project 或 node，见[配置验证](../../src/MakoKaraGenerator.Core/Effects/ScriptConfiguration.cs)和[工程效果验证](../../src/MakoKaraGenerator.Core/Validation/ProjectValidator.Effects.cs)。

| 包参数类型 | 默认和覆盖校验 |
| --- | --- |
| `number` | 有限数，清单必填 min/max，闭区间 |
| `integer` | 整数，min/max 闭区间 |
| `duration` | 整数，min/max 闭区间且 min 非负 |
| `boolean` | JSON 布尔 |
| `enum` | 清单非空唯一 values 中的字符串 |
| `text` | JSON 字符串 |
| `color` | `{r,g,b,a}`，每通道有限 [0,1] |
| `asset` | 本包声明为 image 的文件路径 |
| `curve` | `easing` 的 `linear`/`easeOut`/`easeInOut`；或 `cubicBezier` 的 first/second；或 `controlPoints` 折线 |

曲线控制点均为 `[x,y]`，x 在 [0,1]，y 为有限数；折线至少两点，首末固定 (0,0)/(1,1)，x 严格递增。清单还定义每个参数的 id、label、可选 unit 与必填 default；覆盖不得带未知键，也不自动转换类型。具体格式见[唯一参数解析器](../../src/MakoKaraGenerator.Core/Effects/EffectPackageParameterParser.cs)。

执行投影先保留工程直接实例，再追加应用投影实例；应用按目标作用域、节点深度及原始应用顺序排序，应用内按配方和成员顺序；具体作用域排序以[投影源码](../../src/MakoKaraGenerator.Core/Projects/PresetApplicationsProjection.cs)为准。启用实例按该顺序编译；单导出的输出还按依赖拓扑排序，不能把这解释为所有不同渲染阶段都只有一个全局绘制顺序。参见[编译](../../src/MakoKaraGenerator.Scripting/Compilation/CompiledProjectEffects.cs)和[导出定义](../../src/MakoKaraGenerator.Core/Effects/EffectExportDefinition.cs)。这些结论来自源码检查，未执行实时渲染或浏览器验收。
