# M09 SVG 只读调研与候选接入方案

日期：2026-11-04。依据主计划 `script-sdk-v2-render-pipeline-plan.md` §5.6 与 M09 验收行。
本轮只读取本仓库、官方 NuGet 页面和固定 `v4.9.1` tag 源码；未恢复、安装、构建或运行 SVG，
未修改生产源码。下文“已核验”指源码／元数据证据，“候选”不表示已经冻结接口或测得性能。
M09 的 Warp、图参数及组资源另行拆分，本文只定义 SVG 与现有 Sprite／资源接缝。

## 1. 已核验的依赖与许可证

- 候选精确版本仍为 `Svg.Skia 4.9.1`，不是随上游最新版移动。官方项目面向
  `netstandard2.0;net461;net6.0;net8.0;net10.0`。[S1][S2]
- 官方包元数据列出的 SkiaSharp 下限为 `3.119.2`，HarfBuzzSharp 下限为 `8.3.1.3`；
  固定 tag 的中央版本也使用这两值。App 当前固定 SkiaSharp `3.119.4`、HarfBuzzSharp
  `8.3.1.5`，满足这些下限；这不证明 native ABI、Vulkan 或实际渲染兼容。[S1][S3]
- `Svg.Skia` 项目引用 `Svg.Animation`、`Svg.SceneGraph`、`Svg.Model`、`Svg.Custom`；
  Model／SceneGraph 进一步引用 `ShimSkiaSharp`。Custom 引用 `ExCSS`，该 tag 中为 `4.3.1`。
  Svg.Skia 还引用 Linux／macOS／Win32 的 HarfBuzz native 包。[S2][S3][S4]
- `Svg.Skia`、`Svg.Model`、`Svg.SceneGraph`、`Svg.Animation`、`ShimSkiaSharp` 项目声明 MIT。
  **`Svg.Custom` 项目明确声明 `MS-PL`**，并编入 `externals/SVG` 源码；根 LICENSE 的 MIT
  不能替代该传递项的声明。[S4][S5]
- 固定 tag 的 SVG 子模块指向 `fd33bed4ff14c803b800214ddec977ca0a2e0f8e`，tag tree 为
  `8b23ee1e01b1f2ba90eb74b71ab8c7f27d49f037`。[S6]

实际恢复时仍须核对 `project.assets.json`／锁定清单中每个最终版本、目标 RID 的 native
资产与许可证文本，记录完整传递闭包和 notices。本文不是完整法律判断或恢复结果；也不授权
给本 App 添加许可证。仅在 Rendering.Skia 引用解析依赖，不把 Svg.Skia、Skia 或 DOM 类型
加入作者参考集，不顺带升级 Skia/HarfBuzz。

## 2. 已核验的官方 API 与默认行为

固定 tag 存在以下公开接缝，后续原型应按此 tag 复核编译签名：[S7][S8][S9]

```csharp
// Svg.Skia
SKSvg.CreateFromXmlReader(XmlReader reader);
SKSvg.CreateFromSvgDocument(SvgDocument svgDocument);
SKSvg.ToPicture(SvgFragment fragment, SkiaModel model, ISvgAssetLoader loader);
SKSvg.Draw(SKCanvas canvas, SvgFragment fragment, SkiaModel model, ISvgAssetLoader loader);

// Svg.Model
SvgService.Open(XmlReader reader);

public interface ISvgAssetLoader
{
    bool EnableSvgFonts { get; }
    ShimSkiaSharp.SKImage LoadImage(Stream stream);
    List<TypefaceSpan> FindTypefaces(string? text, ShimSkiaSharp.SKPaint preferred);
    ShimSkiaSharp.SKFontMetrics GetFontMetrics(ShimSkiaSharp.SKPaint paint);
    float MeasureText(string? text, ShimSkiaSharp.SKPaint paint, ref ShimSkiaSharp.SKRect bounds);
    ShimSkiaSharp.SKPath? GetTextPath(string? text, ShimSkiaSharp.SKPaint paint, float x, float y);
}
```

可选的 `ISvgTextRunTypefaceResolver`、`ISvgTextGlyphRunResolver`、
`ISvgTextDirectedGlyphRunResolver` 也在同一接口文件，用于固定文字／字形解析。[S9]
建议调用 `SvgService.Open(受控XmlReader)` 后的静态 `ToPicture`，避免作者资源进入完整动画宿主。
普通 `new SKSvg()` 的 `AssetLoader` 为只读属性，默认构造 `SkiaSvgAssetLoader`，不能在构造后
直接把应用 loader 填入；静态接缝允许显式传入 loader。[S7]

### 2.1 外部读取不能只靠自定义 loader 阻止

`SvgService.GetImage` 会解析 URI，data URI 走独立分支；绝对 URI 在全局 external-image
策略允许时走 `GetImageFromWeb`。后者先 `WebRequest.Create(uri)`／`GetResponse()`，取得
stream 后才调用 `assetLoader.LoadImage(stream)`。异常可能被外层捕获并返回空图。[S8]
因此自定义 `LoadImage` 不是 URL 防火墙，也不能把“没有抛错”当作资源完整渲染。
应用必须在上游 DOM／scene 编译前拒绝外链，把合法包内图片引用改写成经过预算验证的内存内容。
若使用上游全局 `SvgDocument.ResolveExternalImages` 开关，应在 Worker 初始化固定为禁用，
不得按请求切换形成跨包竞争；它只是第二层保护，不替代语义准入。

`SKSvgSettings` 默认 `EnableJavaScript=false`，但 `EnableExternalJavaScript=true`、
`EnableSvgFonts=true`、`EnableTextReferences=true`；还具有静态
`DefaultJavaScriptRuntimeFactory`。应用不使用脚本／动画入口，仍须结构化拒绝这些元素和属性，
不能依赖“当前没有安装 JavaScript runtime”。[S10]

### 2.2 字体存在两个系统回退入口

`SKSvgSettings.TypefaceProviders` 默认包含 `FontManagerTypefaceProvider` 和
`DefaultTypefaceProvider`。更换该列表后，`SkiaModel.ResolveSKTypeface` 在 provider 返回空时
仍调用系统 `SKFontManager.Default`／`SKTypeface.FromFamilyName`，最终还有 default face；
存在共享 typeface cache。[S10][S11]

默认 `SkiaSvgAssetLoader.FindTypefaces` 还按字符找字体；自定义 face 缺字时进入
`MatchPlatformCharacter`／系统 MatchCharacter。因此“只放一个 CustomTypefaceProvider”
不足以证明包内字体闭包，缺字也不能默默变成另一台机器的字体。[S12]

候选应用 loader 必须覆盖文字解析，按 descriptor 的固定字体别名／face index／style 和明确
fallback 顺序做整形、字形覆盖与路径生成；未声明字体、缺字或不支持的整形模式具名失败。
其传给 SkiaModel 的严格 provider 对未知请求抛错，不能返回 null 后让系统继续寻找。
不支持的 SVG 文字属性在准入时拒绝；不能把“预检查字符串 glyph coverage”单独当作上游
每一次字体请求都不会回退的证明。若原型无法封闭这些调用，`text/tspan` 仍保持未开通，
先由作者工具把固定字体文字转换为 path。主代理允许首期明确拒绝 SVG 文字；按下文推荐
profile 冻结并登记 D8 后，可以验收该有限静态子集，不能宣称完整 SVG 文字能力。

### 2.3 释放边界

`SKSvg.Dispose()` 调用 Reset 并释放 original stream；Reset 释放 Picture、WireframePicture
并清理动画／retained 状态。它没有替应用释放自定义 provider 和 Settings 创建的颜色空间。
`CustomTypefaceProvider.Dispose()` 释放其 Typeface。[S7][S10][S13]
候选实现不把 live SKSvg／DOM／动画控制器放入帧数据。SKPicture、字体、图片、颜色空间、
atlas surface 与纹理分别拥有明确 owner，失败／取消须释放部分创建物。SkiaModel 中的
native cache 也要在原型中逐项核对，不能由 SKSvg.Dispose 推导为已经全部清空。

## 3. 本仓库已核验的接入点

| 当前源码 | 已有行为与 M09 缺口 |
| --- | --- |
| `Core/Effects/EffectPackageContents.cs` | 文件闭包逐项 hash、路径、大小写／前缀冲突、canonical manifest 与包 revision；未知 role 仍保存。只有既有代码／图／shader role 做严格 UTF-8 读取，没有 SVG 语义验证。 |
| `Core/Effects/EffectPackageDefinition.cs` | 只有 `ImagePaths`，没有 SVG 描述、字体绑定或 atlas 档位元数据。 |
| `Scripting.Abstractions/ResourceCatalog.cs` | 只有 MaterialHandle／GraphHandle／ImageHandle；身份为 packageId + 完整 revision + 逻辑 ID，Contains 做 owning 修订检查。没有 VectorHandle／SpriteHandle／FontHandle。 |
| `Scripting/Compilation/CompiledEffectPackage.cs` | 启用前实际 image／shader／graph／material validator，全部成功才公开资源目录；没有 SVG／font 验证器。适合追加全包 SVG 门禁。 |
| `Rendering.Skia/PackageImageValidator.cs` | SKCodec 读取尺寸、分配前检查预算、完整解码；可复用其位图预算逻辑，不能仅凭文件名判断内嵌位图。 |
| `Rendering.Skia/Effects/PackageGraphImages.cs` | 加载全部 role:image（包括未用项），累计字节／surface 预算，持有并释放 SKImage。 |
| `Rendering.Skia/Effects/SceneImageEffectResources.cs` | 借用固定包图与 sprite 图片，Dispose 只关闭借用入口；调用方拥有实际资源，先释放管线／帧再释放被借用资源。 |
| `Scripting.Abstractions/SpriteOverlays.cs`、`SkiaRenderPipeline.Sprites.cs` | Sprite 保留完整 affine、归一化 source crop、tint、图层与 owner 错误归属，帧内不读文件；适合 atlas 延伸。 |
| `Rendering.Skia/Effects/SceneSpriteProgram.cs` | crop shader 有半像素夹取；采样明确为 Linear + `SKMipmapMode.None`，现有边缘保护不等于 mipmap 支持。 |
| `Backend.Rendering/PackageProjectSnapshotCodec.cs` | SupportedResourceRoles 当前没有 svg、svgResource，甚至未列入 font；必须补完整闭包观察能力。 |
| `Backend.Worker/PackageResourceTask.cs` | 单资源已有 font 的受控字节／SKTypeface／SKShaper 检查；并不表示该字体已进入效果包运行期 SVG provider。 |

包通用预算现为 256 MiB／1024 files／单文件 64 MiB；图表面现为 512 MiB／8192 px／64 surfaces。
它们不能替代 SVG 的更窄结构预算与累计展开预算。以上为本次读取事实，未测实际峰值。

## 4. 候选静态 profile 与准入流程

建议明确 profile `mako-svg-static-1`，所有未列入的能力具名拒绝，不交给上游忽略。
它的解析／画布／颜色／字体规则进入资源 identity，升级 profile 必须使缓存失效。

1. 先读取唯一不可变包字节，计通用文件预算。SVG 首期不接受 svgz、外部 XML stylesheet 或
   任意 base URI。使用 XmlReader 的 `DtdProcessing.Prohibit`、`XmlResolver=null`、
   `MaxCharactersInDocument` 等限制；按 namespace URI + local name 验证，保留行列位置。
   DTD／entity 声明、PI、foreign namespace、`xml:base`、脚本及任何 `on*` 属性均拒绝。
2. 流式统计深度、元素、属性、文本长度；再建立受限 DOM／IR。需要完整匹配属性 grammar，
   不用正则扫描黑名单判断 SVG 是否安全。未知属性、重复 ID、歧义 href/xlink:href 冲突失败。
3. CSS 用真实 tokenizer／parser（候选复用 ExCSS 4.3.1），检查 AST、完整消费和所有 diagnostic。
   首期 selector 只允许 type／class／ID 及简单 descendant／child；属性仅允许已支持的静态
   fill/stroke/opacity/geometry/text 样式。拒绝所有 at-rule、import、font-face、animation、
   transition、var/env、伪类／状态规则和非本地 url。CSS escape、注释、大小写、quoted url
   按解析后的值检查，不能用 `Contains("url(")` 或 regex 替代。可将已验证 cascade 固化为
   computed presentation 属性，再交上游，避免第二套解析器解释差异。
4. 路径 `d`、points、transform 与数值列表用带完整消费检查的 grammar parser；限制 command、
   coordinate、subpath、arc 数量，拒绝 NaN／Infinity／溢出、过大坐标／变换与退化 viewBox。
   原始节点数和 use 展开后的节点／路径／绘制命令都要计数。检查最终 bounds／native float
   可表示性；不能仅统计 path 字符串的逗号或字母。
5. href、xlink:href 以及所有 CSS／presentation URI 值由同一个资源解析器处理。本地 `#id`
   建类型化引用图，检查目标类型、缺失与循环；use／gradient／clip／mask/filter 依赖各有深度
   与展开预算。允许的包内图片路径只精确映射已验证、同包且 role:image 的文件；禁止网络、
   file、绝对路径、scheme-relative、反斜线、`..`、encoded traversal 和包外 URI。不得从
   操作系统 cwd 或 SVG 文件位置推导资源。跨 SVG 的 use／image SVG 首期拒绝，避免递归文档。
6. 内嵌图片仅允许显式 `data:image/png;base64,...`／`image/jpeg`，先限制编码／解码字节，
   再 SKCodec 检查真实格式、宽高、像素和完整解码；拒绝 SVG data、animated GIF/WebP、
   MIME 与内容不匹配。包内图片与 data 图片计同一累计 decode 账，重复嵌入的工作量也计入。
   合法包引用可改写为已验证的内存 data，但改写后文档也有限额并记录 source map。
7. 结构／资源全部通过后才用受控 XmlReader 建上游 DOM，传应用 loader 编译 model／Picture。
   检查空 Picture／无效 cull bounds／command 数／native 分配预算，失败不发布部分资源。
   对上游静默跳过的资源必须有应用端完整性断言。加载前后检查取消；不能声称上游同步方法
   支持任意时刻取消或硬中断，超时强隔离需 Worker 生命周期另行设计。

调研列出的候选元素：`svg/g/defs/symbol/use/path/rect/circle/ellipse/line/polyline/polygon`、
`linearGradient/radialGradient/stop/clipPath`，静态 `image`；文字只按前述固定字体门禁接入。
`title/desc/metadata` 可作为受预算的非绘制内容。滤镜候选仅 `feGaussianBlur/feOffset/
feColorMatrix/feFlood/feComposite/feBlend/feMerge/feMergeNode`，其输入图必须有界无环，
region／sigma／pass 计入表面预算。暂拒绝 turbulence、displacement、convolve、lighting、
未知 filter 以及非缩放 stroke、textPath、SVG font、tref、foreignObject、script、SMIL 动画。
mask／pattern 如不能在同轮明确计区域／展开和做像素证据，应保持拒绝，不隐式降级。
这些是选型空间；推荐首期采用 §4.1 的更窄范围，不把上段候选全集视为已经冻结。

### 4.1 建议本轮冻结的有限子集

为避免把所有“候选支持”同时变成实施承诺，建议首期采用以下明确边界。它是供主代理冻结的
实施建议，不是已完成行为；冻结后应生成元素／属性／CSS 属性表，解析器默认拒绝未列项。

| 维度 | 建议 `mako-svg-static-1` 接受范围 | 必须拒绝／固定的边界 |
| --- | --- | --- |
| 文档与坐标 | 唯一 SVG namespace；根 `svg` 明确正有限 viewBox 与 descriptor viewport；`g/defs`；matrix/translate/scale/rotate/skewX/skewY 完整有限变换 | 嵌套 svg、百分比 viewport、物理长度／设备 DPI、外部 namespace、xml:base；不得由当前画布猜测尺寸 |
| 形状 | `path/rect/circle/ellipse/line/polyline/polygon`；path 的 M/L/H/V/C/S/Q/T/A/Z 大小写命令；数值使用完整 SVG grammar | 非有限／溢出／超预算坐标、未消费尾串、无效 arc flag／半径；语法错误不能截断后继续 |
| 样式 | 静态 fill/stroke、fill-rule、stroke-width／linecap／linejoin／miterlimit／dasharray／dashoffset、fill-opacity／stroke-opacity／opacity、display／visibility；sRGB 颜色与 local paint URI | CSS 动画、变量、动态状态、非缩放 stroke、外部 paint；所有数值有明确范围，dash 列表预算与全零退化规则固定 |
| CSS | presentation 属性、`style` 属性与受限 `style` 元素；AST 只接受 type／单 ID／单 class selector，按固定 specificity／源顺序级联并输出 computed 属性 | at-rule、combinator、伪类／伪元素、属性 selector、selector list、`!important`；错误／未知声明即失败，不依赖上游忽略 |
| 渐变 | `linearGradient/radialGradient/stop`；明确 gradientUnits、gradientTransform、spreadMethod、有限坐标和 stop-offset；本地 href 继承经类型图展开 | 外部渐变、循环／缺失 stop／非单调 offset、未列单位；继承顺序和缺省值固定在 profile 中 |
| 本地复用与裁剪 | 普通绘制的 `symbol/use` 只引用同文档类型正确的 ID；`clipPath` 只接受直接形状及其有限局部变换；显式 clipPathUnits | clip 内的 `g/symbol/use`（含有界本地引用）具名拒绝；引用循环、跨 SVG use、超展开预算；mask／pattern 首期一律拒绝，不静默改成普通 clip |
| 图片 | 静态 `image`，仅 descriptor 白名单内同包 role:image 的 PNG/JPEG 或经完整解码验证的 PNG/JPEG data URI；x/y/width/height 与 preserveAspectRatio 固定 | 网络、file、包外、递归 SVG、动画图片、MIME 不符；加载失败必须使资源失败，不允许空图替代 |
| 有限滤镜 | `filter` 加 `feGaussianBlur/feOffset/feColorMatrix/feMerge/feMergeNode`；colorMatrix 仅 matrix 且恰好 20 个有限值；输入仅 SourceGraphic／SourceAlpha／已定义 result | forward result、重复 result、循环、BackgroundImage／BackgroundAlpha／FillPaint／StrokePaint、其他 filter primitive；明确 filterUnits／primitiveUnits／有限 region，sigma 外扩和每 pass 预留预算 |
| 非绘制元数据 | 受字符数／节点数预算的 `title/desc/metadata`，不参与资源解析 | 元数据中也不得接受 DTD、foreign namespace 或被重新解释为可执行内容 |
| 文字 | **首期显式拒绝 `text/tspan/textPath/tref`、SVG font 和任何 font-* 声明**；需要字形的 SVG 由作者预先转换为 path | 绝不调用系统字体回退；descriptor `fonts` 首期必须为空，保留字段不能让未实现字体绑定通过 |

该范围需要用对应规范 grammar 完整列出长度／颜色／变换／viewport 的允许值。表中“固定”
表示实现前必须落到可测试 schema／枚举，不授权 renderer 自选缺省值。允许的 `filter` 图同样
只有静态 DAG，准备期完成结构与区域预算；每次绘制仍按实际变换后的物理区域检查总表面预算。

2026-11-05 native 接入核查将应用规则提升为 `mako-svg-static-1/grammar-2`：固定上游的 clip
编译接缝不能完整保留容器／use 的裁剪语义，首期以 `PackageSvgClipContentUnsupported` 明确拒绝，
包括未引用 defs；普通绘制的 g／symbol／use 不受此限制。descriptor schema 与静态 profile 名称不变。
同次修正 transform 参数前瞻误吞空白的问题；合法空白／逗号接受，缺分隔符与未消费尾串仍拒绝。
受控 XML 还将每个直接裁剪形状的有效 `clip-rule` 固定为叶子属性，并移除 clip 本体的该属性，
保留子级覆盖和祖先继承，避免固定上游反向用父级声明覆盖子级而填满孔洞；普通 use 不参与此转换。

SVG 文字拒绝不影响主计划 §5.6 的固定 `font` 角色：包内字体仍须完成资源加载、实际歌词
glyph／Ruby／shaping、Vulkan 上传与导出验证。D8 应分别列出“固定包字体渲染”和“SVG
静态 profile（不含文字）”；后续若支持 SVG 文字，升级 profile 并加入固定字体闭包／缺字
拒绝证据，不能借用现有普通歌词字体测试宣称 SVG 已无系统回退。

### 4.2 四类合规静态图形与双路径验收

建议用四份独立的合法资源覆盖上述语义；每一份都声明 vector 和 atlas 资源，生成至少
**4 × 2 = 8 个真实生产路径用例**。SVG 源只作为输入；接受／预览 source image 不算完成
Paths 或 atlas 的 typed batch 执行。若首期冻结表调整，fixture 与能力表同步修改，不能用
“库可以解析”替代像素证据。

| 合规 fixture | 必须显示的内容 | 必须识别的错误变体 |
| --- | --- | --- |
| `star-path.svg` | 用 M/L/C/Q/A/Z、fill-rule、stroke／dash 与组 opacity 构造星星及孔洞，覆盖基础形状和完整 affine | 非法 arc flag／残余 path token／巨大坐标／错误 opacity |
| `gradient-clip-use.svg` | CSS AST 固化样式，本地 gradient 继承、symbol/use、clipPath 与局部变换产生可见边界 | 转义外链 URI、渐变／use 循环、重复 ID、错误目标类型、CSS 未允许 selector |
| `embedded-image.svg` | 包内 PNG/JPEG 与 data PNG/JPEG 各有明确裁剪／透明区域；图片与矢量混合 | 丢失／错误 role 图片、MIME 伪装、超解码预算、外链／traversal、图片为空后仍成功 |
| `bounded-filter.svg` | blur→offset→colorMatrix→merge 的固定 result DAG，有限 region，并出现可核对的外扩／裁边 | 未定义／前向 result、超 sigma／region／pass、拒绝的 filter、surface 预算超限 |

八个用例均需检查镜像／旋转／斜切／非等比 affine、tint／整体 opacity、透明预乘与裁剪、
重复／随机／冷暖及不同包修订的一致性。atlas 另测声明档位、padding、实际 mip 层与缩小
LOD 边缘污染；vector 保留 Picture 路径，不能加载期全部栅格化再冒充 Paths。CPU 栅格图
只作为解析／参考像素证据，生产 v2 仍遵守 D4：Vulkan 执行与导出，CPU v2 明确拒绝。
同一输出实际 Vulkan 预览／导出和直接绘制的 1x 对照沿用 D13 通道差 ≤2/255；缩放、倾斜
及不同 atlas 档位的质量阈值另记测量，不能把纹理分辨率差异当作确定性容差。

## 5. 候选数量与内存预算（需主代理冻结）

| 项目 | 候选首期上限 |
| --- | --- |
| SVG UTF-8 源／单份 CSS | 4 MiB／256 KiB |
| XML 深度／元素／每元素属性 | 64／20000／64 |
| 原始路径 command／展开后 command | 100000／200000（包累计也计） |
| 引用依赖深度／use 展开后元素 | 32／40000；循环直接拒绝 |
| CSS rules／declarations／selector steps | 2048／8192／16384 |
| 单图片编码／累计图片编码 | 8 MiB／32 MiB |
| 单图片边长／像素／累计完整解码字节 | 4096 px／16 Mpixel／128 MiB |
| SVG 固定字体数／单字体／累计字体字节 | static-1 为 0；未来文字 profile 候选 16／16 MiB／64 MiB |
| SVG 文本 codepoints／shaped glyphs | static-1 为 0；未来文字 profile 候选 32768／65536（包括展开实例） |
| 单 SVG filter passes／全部工作表面字节 | 16／128 MiB，同时受工程图预算约束 |
| 单资源 atlas 档位／单页边长／累计 mip 字节 | 8／4096 px／128 MiB |
| 包 SVG prepare 估计活动字节／Picture 记录操作 | 256 MiB／200000；采用更严格工程限制 |

这些是候选值，不是当前实现已有保证。命令展开、shape、Picture record、内嵌图片和 atlas
应在分配前预留并有累计账，不能只依靠最终 `SKPicture` 大小或 GC 分配估计。滤镜／clip／
surface 外扩按有限 bounds 计费；mip 链每层实际尺寸及 padding 均计入，不能忽略约三分之一
的附加存储。超额整次失败，不删节点、不降低分辨率、不临时改变档位。

## 6. 候选包接口与 SDK 最小扩展

沿用根 files 的严格 `{path,role,sha256}`，新增 role `svg` 与 `svgResource`。
描述放独立 JSON 文件，避免直接向 v3 manifest 根添加一组必填字段；资源 ID 与描述文件路径
分别存在。所有 descriptor／svg，包括未被 controller 引用的项都需验证；未知版本拒绝。
候选 descriptor 示例（字段与 JSON 格式仍待冻结）：

```json
{
  "svgResourceVersion": 1,
  "id": "star",
  "source": "assets/star.svg",
  "profile": "mako-svg-static-1",
  "mode": "atlas",
  "viewport": { "width": 100, "height": 100 },
  "fonts": [],
  "images": [],
  "atlas": {
    "variants": [{ "id": "s64", "width": 64, "height": 64 }],
    "paddingPx": 4,
    "mipmaps": true
  }
}
```

`mode` 精确为 vector／atlas；vector 不允许 atlas 字段。字体绑定候选项含 alias、font 文件
path、face index、weight／width／slant；真实文件必须 role:font，哈希由包闭包固定。
按 §4.1 推荐首期冻结时，`fonts` 必须为空；上述字体绑定只作为后续 profile 设计记录。
images 为 SVG 中允许引用的源路径白名单，必须 role:image，实际引用必须落入它；不从
descriptor 获取机器路径。viewport 与正有限 viewBox、preserveAspectRatio 规则一致性明确，
不得在无尺寸时使用当前画布或设备 DPI 猜测。atlas 档位由作者选固定 ID，不按帧率自动变化。

候选 SDK（全部为封闭值／宿主句柄，无 DOM 或 native 对象）：

```csharp
// ResourceCatalog：仅完整验证后发布；Contains 检查 owning 修订。
VectorHandle GetVector(string resourceId);
SpriteHandle GetSprite(string resourceId, string variantId);

// VectorHandle/SpriteHandle 内含 ResourceIdentity；SpriteHandle 另含固定 variant 身份。
public sealed record VectorVisual(VectorHandle Vector, Affine2 UnitToWorld, ColorRgba Tint);
public sealed record AtlasSprite(SpriteHandle Sprite, Affine2 UnitToWorld, ColorRgba Tint);

// Paths 是独立声明；atlas 复用 Sprites 的 primitive/capability，但有独立 typed batch。
void DrawPaths(OutputId id, ImmutableArray<VectorVisual> items, OverlayLayer layer);
void DrawAtlasSprites(OutputId id, ImmutableArray<AtlasSprite> items, OverlayLayer layer);
```

这保留现有 ImageHandle／Sprite API；不把 derived atlas image 冒充 role:image，作者不能指定
任意 atlas 裁片。atlas 的实际页面、content rect、padding 与 mip metadata 均在宿主内部。
`VisualPrimitiveKind.Paths`／`visuals.paths` 新增后仍走 v2 canvas、具名声明、输出 cap 双核验、
唯一 ID、全局图元预算与 owner 错误归属。加载能力候选 `resources.svg.vector`／
`resources.svg.atlas` 独立于实际绘制 `visuals.paths`／`visuals.sprites`，只在全部执行器接通后
开通。字体不必首期增加作者 FontHandle：可先仅供 descriptor 固定 SVG 资源使用。
以上名称是可实施拆分建议，必须由 root 与绘制代理冻结后再编码。

vector 录制时把声明 viewport／viewBox 标准化到单位空间，再只乘一次完整 UnitToWorld；
镜像、旋转、斜切、非等比缩放均保留。Tint 按既有 Sprite 的直乘分量语义定义；vector 的
组 opacity／tint 需要有界离屏 layer，必须计预算并验证重叠区域，不可逐 path 乘 alpha
冒充整体 opacity。atlas 在声明尺寸栅格化并保留 content rect，外框 padding 和各级 mip
做边缘扩展／独立隔离；小图相邻摆放不能在低 mip 混色。最小首期可一个 variant 一张独立
page，布局成本有限且更容易证明隔离，后续再做确定性 packing。

当前 crop shader 是 half-pixel + no mip；必须真实补 mip 构建与采样并按 content rect／LOD
处理边缘，不能只把采样枚举改为 Linear。CPU 参考栅格化与实际 Vulkan 上传／采样分别
验证，并检查透明边缘预乘规则，不重复 premultiply。

## 7. 候选宿主接口、identity 与释放顺序

- Core 新增 `EffectPackageSvgResources.Read(contents, budget)`，返回只读描述与依赖图；
  SourceFile／FontFiles／ImageFiles 的 role、同包 hash、ID／档位唯一性在此检查。
- Rendering.Abstractions 新增 `IPackageSvgValidator`／受限 inspection DTO；Scripting 的
  CompileAsync 在发布 ResourceCatalog 前调用全包 validator。验证器缺失而包含 SVG 时失败，
  不能只运行控制器或忽略未用 SVG。只读 parser profile 与实际 renderer profile 同时固定。
- Rendering.Skia 新增 `PackageSvgResources : IDisposable`，加载期实现 XML/CSS/path 准入、
  strict asset/font loader、Picture／atlas 编译与预算；和 PackageGraphImages 使用同一个
  图片解码／累计内存账或显式预留，避免同包图片被双计漏计。
- Scene 层新增借用 vector／atlas 的资源集合（或窄扩展现有 SceneImageEffectResources）；
  PreparedRenderFrame 只存强类型批次／owner，不放 `SKPicture`。帧查询只查固定句柄，
  不解析 SVG、不读取磁盘、不补字体、不临时栅格化。
- 便携缓存 key 至少包括 package revision、descriptor canonical bytes／hash、SVG source hash、
  按固定序列的所有 font face 与 image hash、profile、Skia/Svg/HB 精确版本、颜色／alpha／
  viewport／atlas variant／padding／mip 规则。不能使用 GetHashCode、native handle、系统字体名
  或实际命中时间作为便携身份。设备上传 cache 另加 device/context identity／generation，
  不能把纹理跨 device 复用。
- 生命周期按包修订持有，活动帧／管线借用有 lease。先停止／等待该 owner 的活动帧，再释放
  scene 借用入口与 GPU 上传／atlas，之后 Picture 及录制依赖，最后字体／图片／颜色空间。
  GPU 释放按当前 device 的同步约定。失败与取消逆序清理，缓存淘汰必须等待最后租约；
  旧修订帧仍在用时不能由新修订加载覆盖对象。库内共享字体 cache 的所有权须在原型中审计，
  不通过进程全局 clear 去破坏另一个活动包。

## 8. 文件归属与下一步拆分建议

| 工作块 | 候选文件范围 | 必要完成条件 |
| --- | --- | --- |
| A：声明、静态准入与 SDK | Core/Effects 新 SvgResources／SvgBudget；Abstractions 句柄与 typed batch；V2 VisualOutput／pure analyzer；独立 Core/SDK 测试 | XML、CSS AST、path grammar、引用展开与预算；同包字体／图片；封闭纯值与声明双核验。 |
| B：SVG 编译与 native owner | Rendering.Abstractions validator；Rendering.Skia/Effects 新 PackageSvgResources／StrictSvgAssetLoader／固定字体 provider | 固定 tag API 实编；无系统／外部读取；Picture、全部依赖与取消释放；CPU 的 vector/atlas 像素证据。 |
| C：生产画布、atlas 与 device | SkiaRenderPipeline 新 Paths／AtlasSprites；Scene 资源借用；SceneSpriteProgram／mip 处理 | 完整 affine、整体 tint、透明与边缘；GPU 上传／mip；全局预算、owner 失败、租约释放。 |
| D：样例／跨层验收 | §4.2 四类静态 SVG × vector/atlas、固定 role:font 的普通文字渲染与独立集成测试；M09 evidence | 顺序／随机／冷暖／修订／导出一致；SVG 文字显式拒绝、固定字体缺字、资源关闭及真实 Vulkan 像素。 |
| root：共享入口与合同 | Directory.Packages.props、引用、CompileAsync／资源装配、Worker／Snapshot/DTO与metadata、版本与文档 | 真正 restore 后完整传递闭包／许可证；SVG resource inspection/profile；匹配运行和合同门禁。 |

接口冻结应先决定 descriptor schema、支持的 CSS／filter／文字范围和预算，再开始实现。
不要因 source-only 方案完成把 M09 标绿；首次依赖恢复、正式 CPU／Vulkan、导出、HTTP 和
作者 UI 是不同证据。M08 的运行链与 M09 Paths／字体能力不能相互代替。

必须保留负例：DTD/entity／foreignObject／动画／script、href 与 CSS escape 外链、包内
traversal／缺文件／错误 role／hash／重复 ID、引用循环与指数 use 展开、巨大 path／坐标／
滤镜区域、超预算内嵌位图、缺字与同名字体不同 bytes、未知字体 style、无效 viewBox、
atlas tier 超预算、无 mip 或相邻 tile 低 LOD 串色、device 丢失、取消和旧修订租约。

## 9. 官方来源索引

以下均为本轮核对的官方发布／仓库入口；GitHub 链接固定 `v4.9.1`，不使用 main 推导旧版本。

- [S1] NuGet 4.9.1 元数据：https://www.nuget.org/packages/Svg.Skia/4.9.1
- [S2] Svg.Skia 项目：https://github.com/wieslawsoltes/Svg.Skia/blob/v4.9.1/src/Svg.Skia/Svg.Skia.csproj
- [S3] 固定依赖版本：https://github.com/wieslawsoltes/Svg.Skia/blob/v4.9.1/Directory.Packages.props
- [S4] Svg.Custom／MS-PL／ExCSS：https://github.com/wieslawsoltes/Svg.Skia/blob/v4.9.1/src/Svg.Custom/Svg.Custom.csproj
- [S5] 根 MIT：https://github.com/wieslawsoltes/Svg.Skia/blob/v4.9.1/LICENSE.TXT
- [S6] 固定树与 SVG submodule：https://api.github.com/repos/wieslawsoltes/Svg.Skia/git/trees/v4.9.1?recursive=1
- [S7] SKSvg API／构造／释放：https://github.com/wieslawsoltes/Svg.Skia/blob/v4.9.1/src/Svg.Skia/SKSvg.Model.cs
- [S8] URI／data image／WebRequest／XmlReader：https://github.com/wieslawsoltes/Svg.Skia/blob/v4.9.1/src/Svg.Model/Services/SvgService.cs
- [S9] Asset 与 shaping 接口：https://github.com/wieslawsoltes/Svg.Skia/blob/v4.9.1/src/Svg.Model/ISvgAssetLoader.cs
- [S10] Settings 默认值：https://github.com/wieslawsoltes/Svg.Skia/blob/v4.9.1/src/Svg.Skia/SKSvgSettings.cs
- [S11] native model 与系统字体回退：https://github.com/wieslawsoltes/Svg.Skia/blob/v4.9.1/src/Svg.Skia/SkiaModel.cs
- [S12] 默认 loader 与按字符系统回退：https://github.com/wieslawsoltes/Svg.Skia/blob/v4.9.1/src/Svg.Skia/SkiaSvgAssetLoader.cs
- [S13] CustomTypefaceProvider 与 Dispose：https://github.com/wieslawsoltes/Svg.Skia/blob/v4.9.1/src/Svg.Skia/TypefaceProviders/CustomTypefaceProvider.cs
- [S14] Model MIT 与依赖：https://github.com/wieslawsoltes/Svg.Skia/blob/v4.9.1/src/Svg.Model/Svg.Model.csproj
- [S15] SceneGraph MIT 与依赖：https://github.com/wieslawsoltes/Svg.Skia/blob/v4.9.1/src/Svg.SceneGraph/Svg.SceneGraph.csproj
- [S16] Animation MIT 与依赖：https://github.com/wieslawsoltes/Svg.Skia/blob/v4.9.1/src/Svg.Animation/Svg.Animation.csproj
- [S17] Shim MIT：https://github.com/wieslawsoltes/Svg.Skia/blob/v4.9.1/src/ShimSkiaSharp/ShimSkiaSharp.csproj
- [S18] XML DTD 处理 API：https://learn.microsoft.com/en-us/dotnet/api/system.xml.xmlreadersettings.dtdprocessing?view=net-10.0

未执行：依赖恢复、NuGet 内容签名／最终传递版本检查、许可证 notice 产物、SVG 实编、图片／
字体／filter 像素、内存／取消、mipmap 或设备上传测量。以上各项须作为实现与验收工作单保留。
