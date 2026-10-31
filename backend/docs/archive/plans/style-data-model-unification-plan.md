# 统一样式数据模型设计与实施计划

> 状态：0.4.1 实施完成；核心、持久化、模板映射与渲染验证已落地
>
> 日期：2026-09-30
>
> 交付结果：程序 0.4.1，工程 schema v16，歌词 schema v13，模板库 schema v2

## 1. 目标与决定

当前样式系统已经支持纯色、渐变、描边、阴影、字体和多层覆盖，但这些能力仍以一组平面字段保存。同一件事同时存在“基础字段”“新版描述对象”和“旧版渐变端点”三种表示，解析器、覆盖合并器、模板属性列表和渲染器各自理解这些字段。结果是：

- 一个视觉效果可能有多个互相覆盖的来源，读写顺序不直观；
- 模板属性可以写入一部分平面字段，却不能表达一个完整的视觉对象；
- 样式继承和显式关闭描边／阴影的语义容易在边界处不一致；
- 预览、工程持久化和导出之间缺少一个明确的“已解析样式”契约。

0.4.0 起采用一个规范的嵌套模型。样式模板只保存样式，不包含行位置、分支槽位或动画时间；位置和动态场景继续由歌词行、分支和采样器负责。当前 JSON 不再保留旧平面字段，也不增加旧工程迁移步骤。旧字段只允许在本轮代码中作为非持久化的源码兼容外观，待界面完成迁移后删除。

## 2. 规范对象

### 2.1 `TypographySpec`

字体相关参数统一放入 `LyricStyle.Typography`：

| 字段 | 语义 |
|---|---|
| `fontFamilyName` | 请求的字体族；`null` 使用渲染后端默认字体 |
| `fontSizePx` | 画布像素字号，必须大于 0 |
| `fontWeight` | 100～900 的字重 |
| `isBold` | 粗体请求 |
| `isItalic` | 斜体请求 |
| `letterSpacingPx` | 字距，单位为画布像素 |

本轮不新增用户可编辑的字体回退列表。当前 `ITextLayoutEngine` 由后端发现并报告缺字回退字体，尚未有稳定的“用户指定回退链”契约；在该契约建立前，不能把一个没有渲染实现的列表写入工程。

### 2.2 `FillSpec`

未唱和已唱分别是完整的填充对象：

```text
FillSpec {
    color: RgbaColor       // 纯色，也是缺少渐变时的基色
    gradient: GradientSpec? // null = 纯色，非 null = 使用渐变
}
```

`color` 始终存在，保证删除渐变后仍有明确的纯色结果；`gradient` 是唯一的渐变开关。旧的起止色字段不再出现在当前 JSON，也不参与当前解析优先级。渐变本身继续使用现有的 `GradientSpec`，包括线性／径向类型、坐标空间、插值色彩空间、色相路径和停止点。

### 2.3 `LyricStyle`

规范的歌词样式由以下成员组成：

```text
LyricStyle {
    typography: TypographySpec
    unsungFill: FillSpec
    sungFill: FillSpec
    stroke: StrokeSpec?
    shadow: ShadowSpec?
    horizontalAlignment: LyricHorizontalAlignment
    verticalCenterRatio: double
    opacity: float
    scale: float
}
```

`stroke == null` 和 `shadow == null` 表示该效果没有启用。非空对象必须通过各自的 `IsValid` 校验；描边对象仍由 `StrokeSpec` 描述颜色／渐变、宽度、连接方式和外部位置，阴影对象仍由 `ShadowSpec` 描述颜色、不透明度、角度、距离和模糊半径。

位置不属于 `LyricStyle`，因此模板不能改变歌词在画布中的位置。0.6.0 起，统一容器与内部 `LinePlacement`、双行槽位集中到工程 `lyricPresentation`，专用 `DualLineGroupTransform` 已退出，见 ADR-0062。预览和导出先得到同一个解析后的 `LyricStyle`，再由场景采样器应用位置和动画。

## 3. 覆盖与继承

样式解析顺序固定为：

```text
默认样式 → 工程基线 → 分支覆盖 → 行覆盖 → 元素覆盖
```

每一级覆盖都只写自己明确提供的成员。嵌套对象按成员合并：例如只覆盖描边宽度时保留上一层的颜色和连接方式；填充、描边和阴影的覆盖均使用嵌套部分对象，未设置的成员继承上一层；解析完成后才生成完整绘制对象。

覆盖模型使用以下规范成员。填充覆盖是嵌套的部分对象，以保留模板“只改颜色”或“只关闭渐变”的细粒度操作：

```text
LyricStyleOverride {
    typography: TypographySpecOverride?
    unsungFill: OptionalValue<FillSpecOverride?>
    sungFill: OptionalValue<FillSpecOverride?>
    stroke: OptionalValue<StrokeSpecOverride?>
    shadow: OptionalValue<ShadowSpecOverride?>
    horizontalAlignment: LyricHorizontalAlignment?
    verticalCenterRatio: double?
    opacity: float?
    scale: float?
}
```

`OptionalValue<T>` 的“未设置”和“已设置为 null”必须保持可区分：描边和阴影的显式 `null` 会关闭该层效果，并阻止继续继承；填充显式 `null` 是无效数据，应报告诊断而不是静默恢复成默认颜色。元素覆盖仍遵守现有能力边界：元素只能覆盖颜色族、描边、阴影和不透明度，字体、字号、对齐、纵向位置和缩放由校验器拒绝。

`StrokeSpecOverride` 逐成员覆盖颜色、渐变、宽度、连接方式和位置；`ShadowSpecOverride` 逐成员覆盖颜色、不透明度、角度、距离和模糊半径。完整效果模板写满成员，局部模板只写选中成员；整个效果的 `null` 才表示关闭。

旧渐变端点和阴影 XY 编辑地址退出现行模板属性清单；渐变只通过完整编辑器修改，阴影通过方向／距离编辑器修改。它们的源码外观只能即时转换为规范描述，不能进入当前 JSON。

`FillSpecOverride` 只有两个三态成员：`color: OptionalValue<RgbaColor?>` 和 `gradient: OptionalValue<GradientSpec?>`。未设置的成员继承上一层，`gradient` 设置为 `null` 表示关闭渐变并保留当前基色，`color` 设置为 `null` 属于无效数据。解析完成后才合并为完整的 `FillSpec`。

模板编辑器继续使用细粒度的 `LyricStyleProperty`（例如 `StrokeColor`、`StrokeWidthPx`、`UnsungGradient`），但这些属性只是编辑地址，最终写入上述嵌套对象。这样可以保留“只复制描边宽度”的模板操作，同时 JSON 和解析器只有一套真实结构。

## 4. 当前格式

工程基线样式的当前 JSON 形状如下（字段名为规范名称，示例值仅用于说明）：

```json
{
  "typography": {
    "fontFamilyName": null,
    "fontSizePx": 72,
    "fontWeight": 400,
    "isBold": false,
    "isItalic": false,
    "letterSpacingPx": 0
  },
  "unsungFill": {
    "color": "#4A5060FF",
    "gradient": null
  },
  "sungFill": {
    "color": "#4C8DFFFF",
    "gradient": null
  },
  "stroke": null,
  "shadow": null,
  "horizontalAlignment": "Center",
  "verticalCenterRatio": 0.5,
  "opacity": 1,
  "scale": 1
}
```

行或元素的覆盖在 `typography`、`unsungFill`、`sungFill`、`stroke`、`shadow` 下使用同样的成员名；缺少整个成员表示“不覆盖”。当前格式禁止同时写旧的 `fontSizePx`、`unsungColor`、`unsungGradientSpec`、`strokeSpec` 等平面字段。缺失的基线对象按默认样式补齐；解析器遇到非法范围或未知的旧字段时给出结构诊断并拒绝打开，不把旧数据猜测成新数据。

## 5. 版本与边界

这是一次不兼容的数据合同变更，实施交付版本为 **0.4.0**；0.4.1 是模板库版本提示与新版空个人库使用体验的修复版本：

| 文件 | 0.3.2 | 0.4.0 |
|---|---:|---:|
| `project.json` | schema 15 | schema 16 |
| `lyrics.json` | schema 12 | schema 13 |
| `templates.json` | schema 1 | schema 2 |
| 程序 | 0.3.2 | 0.4.0（当前修复版 0.4.1） |

工程文件仍要求 `applicationVersion` 精确等于程序版本；版本不一致或缺失时拒绝打开。0.4.0 不读取、不迁移 0.3.x 的样式字段。0.4.1 不读取旧模板库内容，只在版本不匹配时显示提示并以空个人模板库继续。程序版本由 `Directory.Build.props` 的 `VersionPrefix` 单一管理，程序集、关于界面、日志和新工程写入值必须一致。

## 6. 实施顺序

1. 在 Core 增加 `TypographySpec`、`TypographySpecOverride` 和 `FillSpec`，为 `LyricStyle` 与 `LyricStyleOverride` 增加规范嵌套成员及校验。
2. 将平面属性改为规范对象的访问器，不保留第二份值。`LyricStyleResolver` 只合并嵌套覆盖，解析结果是完整规范对象。
3. 更新 `ProjectDtoMapper`、工程／歌词／模板 schema 常量和当前格式测试；验证 JSON 中不存在旧平面字段，规范对象往返保持语义相等。**已完成：当前 DTO 严格只接受规范键，未知旧键直接拒绝。**
4. 将模板属性、批量编辑和读取当前效果映射到嵌套对象；保留细粒度属性的用户体验，消除各处自行拼接渐变／描边／阴影的代码。**已完成：解析器、批量编辑器、模板工坊和效果开关统一消费规范对象。**
5. 让渲染入口只消费解析后的 `LyricStyle`。预览和导出继续共享 `LyricSceneSampler`，增加填充、显式关闭效果和元素覆盖的像素断言。**已完成：Skia 渲染回归与预览／导出共用解析结果。**
6. 在后续 UI 清理批次删除旧平面源码外观，并把字体回退链作为独立设计，不在本次隐式扩展。

本轮实现允许旧平面成员继续被现有界面和测试构造，但它们是非持久化兼容外观：平面属性的访问器直接读写规范对象，没有第二份字段或优先级；任何新 JSON 只输出嵌套模型。这不是旧工程迁移，也不改变版本门禁。

## 7. 验收清单

- `TypographySpec`、`FillSpec`、`StrokeSpec`、`ShadowSpec` 的有限数值和范围校验完整；无效数据只报告，不静默修正。
- 工程、歌词和模板的当前 JSON 使用嵌套模型，schema 分别为 16、13、2；旧平面键不会被新写入器输出。
- 纯色、完整渐变、描边和阴影的规范对象可保存、重开并保持语义相等。
- 工程→分支→行→元素的覆盖顺序稳定；显式关闭描边／阴影后不被下层恢复；元素排版属性仍被拒绝。
- 模板按细粒度属性套用后，读取当前效果得到同一规范对象；应用到本行和应用到全部仍一步撤销。
- 预览和导出对同一时间点使用同一解析样式；填充、元素覆盖和显式关闭效果有渲染像素断言。
- 全量构建 0 警告、1372 项测试通过，程序启动后可用真实窗口验收。

## 8. 0.4.0 / 0.4.1 实施记录

- `LyricStyle` 的真实存储现在只有 `Typography`、`UnsungFill`、`SungFill`、`Stroke`、`Shadow` 及排版标量；旧平面成员仅为直接读写规范对象的暂时源码外观。
- `LyricStyleOverride` 使用 `TypographySpecOverride`、`FillSpecOverride`、`StrokeSpecOverride`、`ShadowSpecOverride` 逐成员合并；显式 null 的描边／阴影会关闭效果，填充颜色 null 会诊断为非法。
- 当前 JSON DTO 删除旧平面字段并启用未知字段拒绝，`project.json`／`lyrics.json`／`templates.json` 分别使用 v16／v13／v2；程序版本为 0.4.1，工程版本门禁保持精确匹配；模板库版本不匹配时先给出版本提示，不尝试解析旧内容。
- 模板和编辑器的渐变端点、阴影 XY 地址不再列入现行模板属性清单；完整渐变、描边和阴影对象走规范编辑入口。
- 本轮没有增加 0.3.x 迁移；旧工程、旧模板库必须由用户在 0.4.0 中重新建立。
- 全量构建无警告或错误，Core 780、Rendering 198、Spike.App 146、Application 63、Media 69、Export 32、Timeline 84 项测试全部通过。

## 9. 高阶材质模型的后续提案（2026-10-02，未实施）

当前 FillSpec 仍为纯色／规范渐变，StrokeSpec 的当前绘制仍为纯色外部轮廓；本文规范对象实现不等于已经支持自定义 shader。针对文字／描边玻璃材质和整行 glitch，[高阶评审](advanced-material-render-graph-plan.md) §5 建议把轮廓几何与区域材质分开，将内置纯色／渐变和包内自定义材质接入同一判别结构，另用有序效果栈承接行／组图像处理。

模板仍只制作样式及可复用配方，行编辑器负责套用，容器编辑器负责组实例；源内容、时间、排版和布局不由 shader 改写。类型化 uniform、包修订／模板依赖、覆盖／效果合成顺序及具体数据结构须按 ADR-0069 提案和 AGENTS.md §8.3 审阅后实施。本轮不修改当前程序、格式或追加旧工程兼容。

最新[具体落地路线](scripting-system-implementation-roadmap.md) §8 明确材质替换会同时触及工程／歌词／模板三条 schema，模板包资源闭包和源码备份须整体实现。透明输出跳过背景依赖材质后使用包内纯色／渐变基础外观已获确认（ADR-0070）；回退是独立且明确的用途，不重新引入两套竞争的最终填充值。具体数据、旧库保留和 bypass 字段在 R0 审阅，当前规范模型没有变化。
