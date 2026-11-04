# M09 接口与文件分工记录

日期：2026-11-04。范围为主计划 §5.6、§5.9 与 M09；当前仅完成源码接缝设计，
未开始本节点生产编码、依赖恢复、构建或测试。M08 仍由主代理串行验收和发布。
SVG 官方源码与许可证事实见 [SVG 调研](script-sdk-v2-m09-svg-notes.md)；本文把它们落到
当前 App 内的类型、文件和所有权边界。本文中的新增类型／方法是实施合同提案，须由主代理
统一冻结；不能据此减少主计划功能或把尚未实现的能力标为支持。

## 1. 必须保持的功能和执行边界

- SVG 静态资源需要真正接通 `vector` 和 `atlas` 两条路径。四类合法图形——基础路径、
  CSS／渐变／裁剪／本地复用、内嵌图片、有限滤镜——每类都走两条生产路径；源文件可读、
  image 预览或加载期转成普通 ImageHandle 均不能替代这八项验收。
- 允许的静态元素／属性／CSS／滤镜按 SVG notes §4.1 的明确表冻结；DTD、实体、脚本、
  动画、网络／文件外链及未知语义拒绝。该表明确拒绝 SVG text，不偷偷开放系统字体。
  普通歌词、Ruby 与固定 `font` 角色的真实渲染仍属主计划必需能力，单独登记 D8。
- v2 实际执行与导出遵守 D4：Vulkan；CPU 只做 SVG 准入／参考像素与既有非 v2 功能。
  CPU 遇到 v2 工程的拒绝仍需回归。缓存／直绘 1x 对照沿用 D13，缩放／倾斜质量另有证据。
- 完整 affine、图／组资源、Warp 和最终锚点是 M09 的其他必需内容。SVG 类型不能挤占
  它们的接口或用 SVG 星星可见代替完整能力矩阵。
- 文字阶段全部结束后才执行晚期 SVG；最终锚点补算不执行晚期 SVG，不让粒子依赖自己的
  视觉输出。所有图元继续共享实例／OutputId 名字空间、顺序、数量预算和错误 owner。

## 2. SVG 当前真实接缝

本轮核对以下现状，文件路径均相对 `backend/src/`：

| 接缝 | 当前事实 | 本节点必须补齐 |
| --- | --- | --- |
| `Scripting.Abstractions/ResourceCatalog.cs` | Material／Graph／Image 三类目录；句柄等值依 packageId、完整 revision、逻辑 ID | typed vector／atlas 目录和 variant 身份；旧图片路径保持 |
| `Scripting.Abstractions/V2/VisualOutput.cs` | DrawDots／Segments／Trails／Sprites；同名输出一次写入，Sprite 检查 owning catalog | DrawPaths／DrawAtlasSprites，资源与输出 capability 双重校验 |
| `Scripting.Abstractions/V2/ParticleSchedule.cs` | VisualPrimitiveKind 只有四类 | 增加 Paths；AtlasSpriteBatch 仍属于 Sprites primitive |
| `Rendering.Abstractions/PreparedRenderFrame.Visuals.cs` | 封闭 OverlayBatch 集合，稳定 owner、全实例 draw-item 账与复制 | 新批次完整保留、拒绝 default／坏值；不可仅保留旧 DotBatches 投影 |
| `Rendering.Skia/Effects/SceneImageEffectResources.cs` | 借用 graph／material 程序与普通图片；Dispose 只关闭入口 | SVG 用独立借用集合，避免把 native SVG 生命周期塞入 ImageHandle |
| `Rendering.Skia/Effects/SceneSpriteProgram.cs` | Linear + Mipmap.None；裁片半像素 clamp | atlas 新采样程序与真实 mip，不改变既有 Sprite.Source 协议 |
| `Runtime.Hosting/ProjectRenderSessionFactory.cs` | 实际组合根；创建 device、编译包、图 native、fixed image 持有和最终 renderer | 同一 session 装配 SVG 准入、native owner、固定预算与借用集合 |
| `Runtime.Hosting/ProjectRenderSession.cs` | 先关 source，再在同一 device 锁下依次关 renderer／借用入口／fixed images／inputs／native／budget／device | SVG 加入相同清理顺序；失败创建和取消也完整逆序释放 |

`Backend.Rendering` 负责快照／读模型及协议投影，不是 native 组合根。`CompiledEffectPackage`
负责包资源门禁和作者产物；`CompiledPackageGraphExports` 持有现有图片／图 native，二者
当前各自建立同值 ResourceCatalog。新增 SVG 必须使两处投影相同，不能得到相同包修订下
不同的资源集合。

## 3. SVG 包描述与 Core 所有权

新增 `Core/Effects/EffectPackageSvgResources.cs`、`.Parser.cs`、`SvgResourceBudget.cs`。
Core 只解析严格 JSON 描述和固定包文件关系，不引入 Svg.Skia／ExCSS／Skia 类型。
Xml／CSS／path 的语义准入放在 Rendering.Skia，共用唯一解析实现供准入与 native 加载调用。

```csharp
public enum SvgResourceMode { Vector, Atlas }
public sealed record SvgViewport(double Width, double Height);
public sealed record SvgAtlasVariant(string Id, int Width, int Height);
public sealed class EffectSvgResourceDefinition
{
    public string Id { get; }
    public string DescriptorPath { get; }
    public string SourcePath { get; }
    public string Profile { get; }
    public SvgResourceMode Mode { get; }
    public SvgViewport Viewport { get; }
    public ImmutableArray<string> Images { get; }
    public ImmutableArray<SvgAtlasVariant> Variants { get; }
    public int PaddingPx { get; }
    public bool Mipmaps { get; }
}
public sealed class EffectPackageSvgResources
{
    public ImmutableDictionary<string, EffectSvgResourceDefinition> ById { get; }
    public static EffectPackageSvgResources Read(
        EffectPackageContents contents, CancellationToken token = default);
}
```

这些只读类型由 internal 构造器建立；不能让未验证可变 JSON／DOM 逃逸。新增
`EffectPackageDefinition.SvgResources`，由 Definition.Read 读取并作为唯一元数据保存。
`contents` 继续是唯一源字节；描述不复制 OS 路径，不接受单独目录扫描。

固定 JSON 角色为 `svg` 与 `svgResource`。descriptor 字段沿 SVG notes §6：version=1、
唯一 id、source、profile、mode、viewport、fonts、images，以及 atlas 模式的 atlas 对象。
本期 fonts 明确必须为空；不删除／迁移普通 role:font。vector 模式不得含 atlas；atlas
必须声明至少一个唯一 variant、padding、mipmaps=true。每个 descriptor 只有一种 mode；
同一 source 可由 `star-vector` 和 `star-atlas` 两个不同逻辑 ID 引用。

Core 完成以下全包检查，包含未被 controller 引用的描述：

1. descriptor 严格 UTF-8／JSON、重复／未知字段、版本、profile、ID、mode 与数值范围。
2. source 精确指向同包 role:svg；images 精确指向同包 role:image；所有原字节 hash 已由
   EffectPackageContents 固定。不要按扩展名或同名文件代替 role。
3. `svgResource` 逻辑 ID 全包唯一；variant 在资源内唯一。所有 role:svg 都须被至少一个
   descriptor 覆盖并完成语义准入，不能把未用坏 SVG 留到以后才失败。
4. 字体／图片／源以及 descriptor 摘要进入依赖闭包；不同 bytes 或 profile 产生新 revision／
   prepared key。路径继续使用已有 canonical／case／prefix 规则。
5. `resources.svg.vector`／`resources.svg.atlas` 只在相应资源实际完整接通后加入
   SupportedV2Capabilities；资源类型需要的 capability 缺失时报包级具名错误。

独立描述没有给 v3 manifest 根添加新必填字段。Worker 快照、持久化与作者文件端点仍传原
`{path,role,sha256}` 和字节，新增描述由本包严格读模型解释。

## 4. SDK typed handles、输出和归属

新增 `Scripting.Abstractions/SvgResourceHandles.cs` 与 `SvgOverlays.cs`，修改现有
ResourceCatalog／V2 VisualOutput／VisualPrimitiveKind／PreparedEffectDeclaration。
句柄只保存逻辑身份，不包含 path、DOM、native pointer、设备或可写缓存。

```csharp
public sealed record VectorHandle
{
    internal VectorHandle(ResourceIdentity identity);
    public ResourceIdentity Identity { get; }
}
public sealed record SpriteHandle
{
    internal SpriteHandle(ResourceIdentity identity, string variantId);
    public ResourceIdentity Identity { get; }
    public string VariantId { get; }
}

// ResourceCatalog 的新增接口；缺类型、缺 variant 均明确失败。
VectorHandle GetVector(string resourceId);
SpriteHandle GetSprite(string resourceId, string variantId);
bool Contains(VectorHandle handle);
bool Contains(SpriteHandle handle);

public readonly record struct VectorVisual
{
    public VectorVisual(VectorHandle vector, Affine2 unitToWorld, ColorRgba tint);
    public VectorHandle Vector { get; }
    public Affine2 UnitToWorld { get; }
    public ColorRgba Tint { get; }
    public bool IsValid { get; }
}
public readonly record struct AtlasSprite
{
    public AtlasSprite(SpriteHandle sprite, Affine2 unitToWorld, ColorRgba tint);
    public SpriteHandle Sprite { get; }
    public Affine2 UnitToWorld { get; }
    public ColorRgba Tint { get; }
    public bool IsValid { get; }
}
public sealed record PathBatch(EffectInstanceId InstanceId, OutputId Id,
    OverlayLayer Layer, ImmutableArray<VectorVisual> Paths)
    : OverlayBatch(InstanceId, Id, Layer);
public sealed record AtlasSpriteBatch(EffectInstanceId InstanceId, OutputId Id,
    OverlayLayer Layer, ImmutableArray<AtlasSprite> Sprites)
    : OverlayBatch(InstanceId, Id, Layer);

// V2.VisualOutput
void DrawPaths(OutputId id, ImmutableArray<VectorVisual> items, OverlayLayer layer);
void DrawAtlasSprites(OutputId id, ImmutableArray<AtlasSprite> items, OverlayLayer layer);
```

`ResourceIdentity.ExportId` 对 SVG 表示 descriptor 的逻辑 ID；SpriteHandle 另有结构化
VariantId，禁止拼接 `id+":"+variant` 形成歧义身份。两个 Contains 比较真实目录中的完整
句柄，包括 packageId／完整 revision／resourceId／variant。不能只比逻辑 ID，也不能从
句柄字符串反向查文件。现有 public ResourceCatalog 构造器保留；新增 host 构造重载明确
接受 vector ID 与 `resourceId → variant IDs`。所有构建目录的位置使用同一元数据 helper，
不再分别扫描文件猜测 variant。作者自行创建的其他目录不能让真实 writer 接受伪造归属。
建议完整构造签名为现有 packageId、revision、materials、graphs、images 五个参数后追加
`ImmutableArray<string> vectors` 与
`ImmutableDictionary<string, ImmutableArray<string>> sprites`；旧构造转发空集合。
默认／空 ID／重复 variant／vector-atlas 同 ID 跨型冲突全部拒绝，不生成 fallback handle。

VectorVisual／AtlasSprite 均无作者 source crop，完整单位方形映射 descriptor viewport
一次。有限／可逆 affine、四角和 native 精度检查沿现有 Sprite 规则；default 无效，空数组
有效，default 数组无效。Tint 沿现有 Sprite 的直乘 sRGB RGB／Alpha 语义，预乘仅做一次。

两个 Batch 的 ItemCount 都为实例数；透明实例仍计数。加载期 SVG expanded operations
另计准备预算，实际帧录制开销另计 `SvgRenderWorkBudget`，不能只收一个 Path 图元就无上限
重复二十万条内部命令。DrawPaths 对应 Paths／visuals.paths；DrawAtlasSprites 对应
Sprites／visuals.sprites。两者都要求包的相应 resources.svg.*，且 controller 的同名
Overlay/canvas 输出 requires 同时含绘制与资源 capability。PreparedEffectDeclaration
检查 VisualOutputDeclaration；VisualOutput 写入时再检查批次类型／层、重复 ID、两类
capability、Contains、draw-item／frame-byte 账。旧 SpriteBatch 继续只接受 ImageHandle。

`ScriptV2PurityAnalyzer` 仅补 VectorVisual／AtlasSprite 与真实新句柄白名单；句柄当前虽有
预留名字，仍要验证其真实类型与成员。Svg.Skia、Skia、XmlReader、CSS AST 与 Core 包描述
都不进作者参考集。不可因为支持 SVG 便开放 Stream、文件、URI、native 或动态类型。

`PreparedRenderFrame.WithOverlaySnapshot` 保存完整批次和 owner，计全实例数量与字节，
跨实例同名依旧允许、同实例跨类型同名拒绝。新字节账应复用 M08 宿主 frame budget 接缝；
不要把 VisualOutputBudget 目前只有 DrawItemLimit 误写成已有通用 byte limit。若需扩展
共同输出估算，新增内部 `OverlayMemoryCost`，对旧四类与两类新批次一起计费，而不只对 SVG
收费；数值算术 checked，超额整帧失败，后续合法帧可恢复。

## 5. 唯一受控解析入口与外部 IO 禁止

新增 `Rendering.Abstractions/IPackageSvgValidator.cs`。它只引用 Core 的固定描述与
不可变 inspection DTO，不返回 native：

```csharp
public interface IPackageSvgValidator
{
    PackageSvgInspection Validate(EffectPackageContents contents,
        EffectPackageSvgResources resources, CancellationToken token = default);
}
```

inspection 包含 profile、解析规则版本、资源 ID／mode／viewport、原始与展开 operation
计数、引用闭包摘要、图片 decode 字节、atlas level 尺寸／预算等可观察事实。它不是 native
cache key，也不把“要求创建 mip”的计划数伪装成 GPU 已观察到的层数。

新增 Rendering.Skia 文件组：

- `PackageSvgValidator.cs`：调用同一个准入器，完成所有资源真实 parse／image decode／
  Picture 编译检查，最后释放临时 native。缺验证器而包有 SVG 时不能只运行控制器。
- `Effects/Svg/SvgStaticCompiler.cs`、`.Xml.cs`、`.Css.cs`、`.Path.cs`、`.References.cs`：
  单一受限 IR 与规则表，先完成 XML／CSS AST／path grammar／引用闭包／预算，再触碰
  上游 DOM。准入与真实 PackageSvgResources.Load 不能有两套语义。
- `Effects/Svg/SvgStaticDocument.cs`：应用内部不可变 IR、source map、expanded draw／
  filter plan、依赖 image IDs；不得成为作者 SDK 值。
- `Effects/Svg/StrictSvgAssetLoader.cs`：只接收已验证的内存图片映射；所有文字方法和
  类型化 glyph resolver 首期直接具名拒绝。默认 loader、系统 typeface provider 不使用。

入口只接受 EffectPackageContents 中已固定的 ReadOnlyMemory 字节和 typed descriptor。
不提供 filename／directory／Uri 重载，不为 XmlReader 设置 BaseURI；XmlResolver=null、
DtdProcessing.Prohibit，限制文档字节／字符／深度／元素／属性。所有 xmlns／属性按
namespace URI+local name 判定；href 与 xlink:href 同时出现直接拒绝，避免解析分歧。

CSS 必须真实 tokenizer／AST 并完整消费；变量、at-rule、未知属性、任何外部 URI 等错误
在应用端报告。通过的 cascade 固化为 computed 属性，再生成受控内存文档给 SvgService。
路径／points／transform grammar 也完整消费并统计展开操作，不使用正则字母计数。
本地 `#id` 与图片路径走同一类型化 resolver：片段不猜文件，图片 href 只精确匹配 descriptor
的 package path 白名单；不从 SVG 所在目录、cwd、base URI 或机器字体名补资源。

data image 先限制编码／解码字节，再检查真实 PNG/JPEG 与尺寸并完整解码；包图片同样通过
PackageImageValidator。允许图片可改写成已验证的内存 data 内容，记录 source map 和改写
预算，使上游永远看不到外部相对／绝对地址。global ResolveExternalImages 在 Worker 初始化
禁用并固定，不按请求切换。自定义 loader 不能单独阻止上游 WebRequest，所以禁止绕过
该结构化准入直接使用文件名／URI 的 SKSvg.Load 或普通默认 SKSvg loader。

原型按固定 Svg.Skia 4.9.1 的 `SvgService.Open(受控XmlReader)` 加静态
`SKSvg.ToPicture(fragment, model, strictLoader)` 接口接入。所有无法确认已编译的资源必须
失败：图片不可静默为空、未知节点不可由上游跳过、Picture 与 cull bounds 必须有效。同步
上游方法前后和每个应用阶段检查取消；不宣称任意 native 调用可被 token 硬中断。

## 6. native owner、Scene 借用和渲染接缝

新增 `Rendering.Skia/Effects/PackageSvgResources.cs`，由 Runtime.Hosting 按每个固定包
修订创建并拥有；它不是脚本产物的静态全局 cache。

```csharp
public sealed class PackageSvgResources : IDisposable
{
    internal static PackageSvgResources LoadWithDevice(
        EffectPackageDefinition definition, PackageGraphImages images,
        SvgResourceBudget limits, ISkiaRenderDevice device,
        ImageSurfaceBudgetSession surfaces, CancellationToken token);
    internal SvgVectorProgram GetVector(VectorHandle handle);
    internal SvgAtlasPage GetSprite(SpriteHandle handle);
}
```

`SvgVectorProgram` 持有单位空间 SKPicture、完整实际绘制 bounds、展开 operation／filter
成本和准备依赖租约。资源的 viewBox／viewport normalization 在录制时只做一次；renderer
仅乘 UnitToWorld。vector 必须保持 Picture 矢量执行，不能准备期全部转 PNG 再走 Sprite。
静态滤镜在 load 时固定结构／有限 bounds；实际绘制所需物理外扩还受完整 affine／density
影响，要在帧 preflight 中计算。

`SvgAtlasPage` 持有唯一 resource+variant、实际 page 尺寸、content rect、padding、完整
mip 层尺寸和 device image；首期每个 variant 独立 page，避免多 tile 在小 mip 汇合。原 SVG
按声明档位真实栅格化；atlas 不冒充 role:image 或普通 GraphSurface 原图。

同包 role:image 应直接从已有 PackageGraphImages 借用完整解码对象，不另设一份未计账
decode。给 CompiledPackageGraphExports 增加受限的同包图片借用接缝，或由 root 把其现有
PackageGraphImages 注入同 session 的 SVG owner；禁止公开可写 dictionary。data image
由 SVG owner 拥有，按源内容去重后计固定存储，原始重复嵌入仍计解析／decode 工作量。

新增 `Effects/SceneSvgEffectResources.cs`：只保存完整 typed handle 到对应 program/page
的借用映射，并持 PackageSvgResources 的 scene lease；Dispose 关闭入口并归还 lease，
不主动释放 owner native。其 `GetVector`／`GetSprite` 检查关闭、精确 owning 修订和设备。

SkiaRenderPipeline 新增一个 `SceneSvgEffectResources?` 字段／构造参数及文件：

- `SkiaRenderPipeline.Paths.cs`：ValidateNativeVector／DrawPaths／每帧依赖借用。
- `SkiaRenderPipeline.AtlasSprites.cs`：ValidateNativeAtlasSprite／DrawAtlasSprites／mip
  采样；保留完整 affine，不直接用只支持相似变换的 DrawAtlas 降级旋转／非等比／斜切。
- `Effects/SceneAtlasSpriteProgram.cs`：采样整个独立 page 的真实 mip image，用宿主
  content rect 映射单位方形。作者无任意 atlas UV；不得复用原 no-mip crop shader 后宣称完成。
- 修改 `SkiaRenderPipeline.Visuals.cs` 两个 switch，所有新批次在绘制前精度／资源完整
  检查，在对应 layer 和稳定顺序绘制。未知封闭批次必须失败，不能 default 静默跳过。

把现有 SpriteOperation 提取为 `OverlayOperation(frame, OverlayBatch, operation, action)`，
旧 Sprite 也使用同一 owner 归属；保留 OperationCanceledException 与已包装 EffectFailure，
其余 PreparedFrame／budget／SVG 错误经 PreparedOverlayOwner.Failure 保留实例、artifact、
package、stage、TimeUs。不得抛成无 owner 的全局 native 错误。

白色不透明 tint 的 vector 直接 concat+DrawPicture；需要整体 tint／opacity 时使用受预算
的显式紧区域中间 surface 和合成 color filter，或等价且有明确表面预留的受限 layer。
先检查变换后 bounds／clip／密度与分配，再应用整体颜色；不能把每条 path alpha 逐项乘色
冒充整图透明度，重叠区域必须有证据。任何中间表面不能超出预检的物理界限。

SVG filter DAG 的每项输出、存活输入和 blur scratch 需要显式计划／峰值账，至少沿现有
图 Blur 的输出加两张 scratch 预留。若直接录入上游 SKImageFilter，必须将其每个静态
filter region 的保守 scratch 预留纳入成本，并证明绘制 clip／bounds 限制；若无法建立该
闭包，就在应用内部拆出 filter plan 通过有界 surface 执行。不能把 library 内部不可见分配
省略后声称完整 native RSS 已被预算覆盖；有限滤镜功能仍要实现，不能因此降成不支持。

## 7. 真实 mip、内存账和释放

当前 ISkiaRenderDevice 只提供 CreateSurface／ReadPixels／RasterizeImage，没有明确图片
上传接缝。新增内部 `CreateTextureImage(SKImage raster, bool mipmapped)`，Vulkan 实现按
同一 SynchronizationRoot 调用锁定 SkiaSharp 的 `ToTextureImage(context, true, true)`，
失败明确拒绝；CPU 参考路径不冒充 device mip 上传。该 overload 的 mipmapped 参数在
当前固定包 XML API 中存在；尚无本轮运行证据，不能只记录这个布尔参数便标验收通过。
输出还须验证 `IsTextureBacked` 和本 device 归属；该 API 可能返回输入自身，所有权转移
要按 ReferenceEquals 处理，不对别名重复 Dispose，也不误释放仍被 Picture 借用的原图片。

推荐 atlas 首期使用独立 page 的标准硬件 mip 链。准备时先算足以下确定值：

```text
pageWidth  = variantWidth  + 2 * paddingPx
pageHeight = variantHeight + 2 * paddingPx
w[0], h[0] = pageWidth, pageHeight
w[i+1] = max(1, floor(w[i]/2)); h[i+1] = max(1, floor(h[i]/2))
until w[i] == 1 && h[i] == 1
mipBytes = sum_i checked(w[i] * h[i] * 4)
```

content rect 与透明外区按固定 profile 生成；直乘 RGB／alpha、透明边缘和下采样在 premul
颜色空间一致，不重复预乘。独立 page 可使任意低 LOD 不与另一个 SVG 混色；padding 与
content rect 进入缓存 identity。采样同时启用 Linear filter 与 Linear mip；绘制局部坐标
保留完整变换导数，不通过 per-pixel clamp 抹掉选择 LOD 所需信息。非等比／斜切使用完整
纹理四边形／等价 shader，不按 X scale 单独猜档位。variant 始终由作者固定 ID 选择。

真实验收必须产生 GPU backed mip image，证明 LOD 缩小采样相对 no-mip 的像素／边缘
行为，覆盖透明轮廓、强对比细纹、旋转／斜切、极小尺寸和两种资源紧邻。若绑定没有公开
实际 mip 层查询，inspection 写“计划层数／请求 mip／实际上传成功”的不同字段；借助
native helper 观察或像素测试补足证据，不能假造 `HasMipmaps`／层数。

内存账分三层，全部在分配前 checked 预留：

| 账 | 内容和 owner | 释放时机 |
| --- | --- | --- |
| 固定包准备账 | XML／CSS／IR 字符及节点、展开命令、图片 decode、Picture 与保守记录成本、data image、atlas raster 构建峰值 | 临时项编译结束释放，固定项保持至包 owner 最后 lease |
| session 固定像素账 | role:image 基础层、SVG data image、atlas 全部 mip bytes；同一存储去重；CPU 构建副本与 GPU 上传并存时两份都计 | session owner 释放实际 native 存储后再减账 |
| frame 图账 | 本帧借用固定 inputs 的活动量、vector tint／filter 以及 group／graph 的显式表面与 scratch、实际 density | 本帧产物最后消费者完成再归还 reservation |

ImageSurfaceBudgetSession 当前 BorrowInputs 固定用 Width*Height*4，不能拿它给 atlas mip
记账。新增内部 storage-aware 借用／持有接缝，明确 `storageKey/bytes/surfaceCount`，普通
图片转发既有基础层，atlas 传完整 mipBytes。同一 native 存储的账必须始终相等；每个 mip
不伪造不同 image handle，身份也不能跨 device generation。固定 reserve 先于上传，bind
在成功 native 创建之后，失败／取消回滚。不得仅在 Usage report 上补数字而未真正拒绝分配。

所有者与依赖顺序：

```text
ProjectRenderSession
  owns device + common budgets
  owns CompiledPackageGraphExports (including shared role:image)
  owns PackageSvgResources (Picture/data image/atlas+device upload)
  owns SceneSvgEffectResources (borrows package owner, scene lease)
  owns renderer + frame source (borrows scene resources)
```

source／session gate 等待在途 frame；device 的同步 owner 在同一锁下退休。随后依次关
renderer 和 scene 借用入口、归还 frame／fixed leases、释放 atlas GPU images 与 Picture／
录制依赖、关闭 SVG owner、再释放共享图片／图 native 和 budget，最后 device。若
SKPicture 仍引用 image／font／color space，依赖必须存活到 Picture 关闭；不能仅凭
SKSvg.Dispose 推断其自定义 loader／provider／SkiaModel cache 都已释放。

PackageSvgResources 内部状态建议 Active → Retiring → Disposed：Retiring 不再接受新
lease，旧租约可继续；最后 lease 后在设备锁下清理。失败的部分创建顺序逆序释放；取消
不得发布半个 ResourceCatalog。新修订加载不覆盖旧修订对象，不用进程全局 clear 破坏另
一个包。现有 session 若始终串行并整体持有 owner，也应明确该约束并验证在途外部 GPU
frame lease；不能只以 UI 请求已结束作为纹理可释放证据。

## 8. SVG 文件归属与串行集成顺序

下表是独立文件边界，实际代理分配由 root 统一发任务；表内“修改”文件只能有一个 owner。

| 工作包 | 独占新增文件 | 修改接缝 | 完成条件 |
| --- | --- | --- | --- |
| SVG-A：Core／SDK | Core/Effects/EffectPackageSvgResources*、SvgResourceBudget；Abstractions/SvgResourceHandles、SvgOverlays | ResourceCatalog、V2 VisualOutput／VisualPrimitiveKind／PreparedEffectDeclaration、purity analyzer；Core Definition／Profiles 修改由 root 合并 | schema、闭包、句柄与新 batch 拒坏输入／归属／能力，保持旧 Sprite |
| SVG-B：准入／native | Rendering.Abstractions/IPackageSvgValidator；Skia/PackageSvgValidator；Effects/Svg/*；PackageSvgResources、SceneSvgEffectResources | PackageGraphImages 的受限借用；共享预算接口由 root 冻结 | 唯一 parse／CSS AST／完整 path／资源图；四类合法 SVG 均可 Picture／atlas 编译，native 失败与取消清理 |
| SVG-C：执行／mip | SkiaRenderPipeline.Paths／AtlasSprites；Effects/SceneAtlasSpriteProgram | Pipeline.Visuals；设备 mip 上传；ImageSurfaceBudgetSession storage-aware 账；Pipeline构造／释放由 root 合并 | 真 Vulkan vector、atlas、affine／tint／mip、共同预算和 owner；不绕图元生产入口 |
| SVG-D：独立验收 | Core／Scripting／Rendering／Runtime tests 新 Svg*.cs；四 fixture ×2 descriptor＋控制器样例 | D8／M09 evidence 的结果由 root 汇总 | 8 项真实路径、负例、冷暖随机／修订／取消／在途租约／导出，CPU 拒绝与固定字体独立证据 |
| root：共享合同／组合根 | 如需新增 PackageResourceCatalogProjection、SVG inspection DTO | Directory.Packages.props／Rendering.Skia.csproj；CompiledEffectPackage／CompiledProjectEffects；ProjectRenderSessionFactory／Session；Worker／Snapshot／Contracts／前端作者资源角色 | 依赖恢复完整闭包／notices；全包门禁；同输入身份；合同生成、匹配发布与 fresh HTTP；中文提交推送 |

按依赖顺序先冻结 Core descriptor、SDK typed values、validator 和预算／native 借用接口，
再分别实现 A／B／C；root 最后接组合根与公共读模型。CompileAsync 新增最终 svgValidator
参数的完整重载，旧重载转发 null；含 SVG 时缺服务失败，CompiledProjectEffects 与所有
生产 compile 调用一并更新。不要只给测试传 SVG validator 而漏生产工厂。

新 Core capabilities 在完整执行器、准入门禁与装配可用后开通；版本／编译分析规则／HTTP
合同身份由 root 一次协调，避免平行修改版本和发布文件。本文不是开通功能开关的证据。

## 9. Warp 接缝（接续只读源码审计）

本节整合 Warp 代理的源码审计和实施建议，尚未实现。当前 TextOutput／ComposePureOutput
没有 Warp write，PreparedRenderFrame.TextPartitions 只合成 affine；CursorGeometry.At
只乘 WorldMatrix，V2SourcePoint 的非 cursor 中心只用 BaseWorldMatrix.Map。旧 AnchorMap
不是现生产映射。因此 Warp 必须一次接通文字值、真实 glyph 像素、共享非 affine mapper
和所有最终锚点；只改变 InkBounds 或晚期 SVG 坐标不算实现。

### 9.1 封闭网格与局部顺序

新增 `Scripting.Abstractions/GridWarp.cs`、`TextGeometryMapping.cs` 与
`V2/TextOutput.Warp.cs`，修改 TextOutputValues／TextOutput 为 partial 并增加：

```csharp
public sealed class GridWarp
{
    public GridWarp(Rect2 domain, int columns, int rows,
        ImmutableArray<Point2> destinationVertices);
    public Rect2 Domain { get; }
    public int Columns { get; }
    public int Rows { get; }
    public ImmutableArray<Point2> DestinationVertices { get; }
}
// null 明确关闭，未写继承；同目标后写覆盖。
void TextOutput.SetWarp(TextTarget target, GridWarp? value);
```

Domain 沿用目标实际行逻辑坐标，不自动重定位到 bbox 左上。Columns／Rows 为 cells，
每格固定 TL→BR 对角线，三角分别 TL/TR/BR 与 TL/BR/BL；dst 顶点 row-major。
每个目标最大 32×32 cells，故最多 1089 vertices／2048 triangles／6144 UInt16 indices。
纹理坐标使用锁定 SKVertices textured overload 的图片像素坐标，不能当归一化 UV。

构造时产生唯一 canonical float 源／目标顶点；CPU mapper 和 native mesh 共用该值。
源轴 float 后严格递增，所有三角有限、不退化、方向一致；边界简单无交叉，不能只验每格
正面积却容许整体自叠。外域 MapPoint／MapVectorAt 具名 WarpOutsideDomain，不 clamp／
外推。闭外边界、内部半开 cell、末行／末列归最后 cell，对角线归第一 tri，规则固定。
MapPoint 使用同一 tri 的 barycentric；MapVectorAt 使用该 tri Jacobian，再走后续 affine。

必须支持 Family 与独立 Cluster，包括带真实 RubyId 的 Ruby cluster；Base／Mark 与现有
可独立粒度一并接入。Line／Group 的 affine 父链保留；未实现其 Warp 时不能声明相应 cap。
按主计划“每层先 Warp 后 affine”，基础字簇的复合固定为：

```text
W_line ∘ A_family ∘ Warp_family ∘ A_base ∘ Warp_base ∘ A_cluster ∘ Warp_cluster
```

Ruby 跳过 Base 层。不同层都存在 Warp 时，像素按叶到父多个有界 pass 合成；不能仅把原
mesh 顶点套另一任意 mesh 而不细分，造成图像与 mapper 不一致。同目标只保留最后网格，
但每次写入都要校验和扣验证预算，非法或超限 write 不能靠后写 null 隐藏。

### 9.2 真实文字与最终锚点

新增 `Rendering.Abstractions/PreparedRenderFrame.Warps.cs`，保存真实目标／装载／退出
副本及最后写入的 instance、artifact、package owner。原 HierarchyScene 几何和所有局部
write 一起构造 `TextGeometryMapping` 的封闭 Affine/Grid 值步骤，不从已经合成的最终矩阵
反推。GeometrySample／Cluster／FinalSceneGeometry 需要 MapPoint／MapVectorAt 投影。
不存 callback／native／可变 mesh；没有 Warp 时应有轻量 affine fast path。

修改 `PlaybackGeometry.cs`：CursorGeometry.At 在原 reveal path 取点后走同映射，并用
Jacobian 映射切线；修改 `FrameEffectHost.V2Anchors.cs` 的 V2SourcePoint 中心投影；
PreparedRenderFrame 的 FinalGeometry、FinalLineGeometry／Mark／Group 等也同步，保留
真实 Binding／Token／DrawUnit／RubyId／ExitReplica occurrence，不能制造新源 ID。
WorldInkCorners、组 ink、图采样 bounds 需考虑 bbox 与 mesh 三角交点的投影或其有界
保守并集，四角不足以涵盖弯曲后范围。advance／reveal 的源几何仍固定，不被 ColorOnly
图位移改变。

新增 `SkiaRenderPipeline.Warps.cs`，在已有 FamilyTransforms.DrawChangedFamilies、
TextPartitions.DrawBase／cluster、Ruby.DrawRuby 的 positioned-glyph callback 上实现局部
surface→textured DrawVertices。复用完整整行 shaping 和 drawUnitIndices，不能按字符重新
整形，也不能直接把已完成 world affine 的 CaptureSceneTarget 当作 pre-warp 输入。

所有 local surface 用同 _device、BGRA premul／sRGB、显式 Clip 再 Clear；源纹理坐标为
`(sourceLogical-allocationOrigin)*density`。共享顶点与单 indexed draw 减少几何不一致，
内部接缝仍须 Vulkan 实测；
Linear／None、透明 gutter、外沿 AA 独立验证。domain 必须覆盖墨迹、stroke／shadow／glow
外扩、advance／cursor 锚点，父 domain 还覆盖子级输出；越界拒绝，不裁掉 Ruby／halo。
透明采样 gutter 属于 allocation 外围，不要求作者把它扩成网格定义域。
保持全域 ColorPartOrder：BaseGlow→BaseStroke→BaseShadow→BaseFill→Ruby 各 part。
不能按族整块预合成，改变相邻族 halo 与填充覆盖。材质／图实际 coverage 同样经过 Warp；
ColorOnly 图仍不改最终锚点 mapper。

FrameEffectHost 的最终锚点补算继续 pureTextOnly。Warp 无完整连续证明默认 Exact；
120Hz 只能在 GeometrySampling 显式证明的区段使用，并合入 Warp 开关／拓扑／
cluster／load 身份断点。固定 piecewise-linear 网格跨 cell 的位置连续、方向可折；作者
网格随 t 变化时不能承诺自动枚举全部 cell crossing。实际切分只使用已证明的开关／拓扑／
装载／cluster 等离散断点；Continuous120Hz 仍是已有显式近似，未知跳变保守 Exact。
不能让 memo 冷暖或前次求值的网格影响同 t 结果。SVG 保持晚期视觉，不自动继承文字 Warp，
作者可通过最终几何 MapPoint／MapVectorAt 明确放置。

### 9.3 Warp 文件分工与预算

| 独占工作 | 主要文件／接缝 | 必须连通 |
| --- | --- | --- |
| Warp value／writer | 新 GridWarp、TextGeometryMapping、TextOutput.Warp；TextOutputValues／FrameEffectHost.Pure 的 write reducer | 不可变／float canonical／域与拓扑；后写、范围、cap与owner |
| Warp geometry／anchor | 新 PreparedRenderFrame.Warps；TextPartitions／GeometrySamples／PlaybackGeometry／V2Anchors | 同 mapper 的位置、切线、bounds、最终粒子锚点 |
| Warp pixel | 新 Pipeline.Warps；FamilyTransforms／TextPartitions／Ruby | 真实 glyph／Ruby、分部件顺序、材质coverage、多个局部层顺序 |
| Warp acceptance | 新 GridWarp／Runtime／Rendering 测试和样例 | 数学边界、真实像素与cursor/出生、退出／多实例、预算与错误归属 |

建议安全硬限为：每目标 32² cells，每次 Evaluate 全实例累计 65536 canonical vertices、
131072 triangles、256 Warp writes、8 MiB 几何／验证数据；全部包含被覆盖 write。
这些值待 root 冻结，是确定性拒绝门槛，不是正式性能阈值。像素与 layer 统一使用既有
ImageSurfaceBudgetFrame，不给每实例新账。density 按所有三角 Jacobian 与后续完整 affine
的最大放大量上界计算；超限拒绝，不自动降清晰度。

必验 identity／弯曲中心／对角线／cell 边界、base+Ruby cluster 的实际像素与 cursor／粒子
出生、family+cluster+line/group affine 顺序、退出冻结／多实例后写、float 退化／翻面／
边界自交／越域／超额 owner，以及 near／远端乱序 seek 和 memo 冷暖。

## 10. 图参数、材质和组资源接缝（接续只读源码审计）

本节整合图／组代理的审计，尚未实现。当前 v2 TextOutput 没有 Graph／uniform；
CompiledEffectPackage.CreateEffect 明确拒 v2 direct graph，FrameEffectHost.Load 的 v2
分支直接走 LoadPure；ImageEffectBinding 只有 Target／Graph／Parameters；Transform
node 的 Matrix／OutputBounds 是固定字面量。组图在正式 ImageScene 的多个入口仍拒绝，
已有 Color/Erasure payload 算法主要只在独立测试中使用，不等于生产组图已接通。

### 10.1 typed 图、uniform 和 Transform node

新增 `Scripting.Abstractions/GraphBindingValue.cs`、`GraphTransformValues.cs` 与
`V2/TextOutput.Graphs.cs`。复用 GraphUniformValues／UniformValue 七类和数组，不以任意
JSON／shader 字符串传逐帧值。建议精确值接口：

```csharp
public sealed record GraphTransformValue(Affine2 Matrix, Rect2 OutputBounds);
public sealed class GraphTransformValues
{
    public GraphTransformValues(
        IEnumerable<KeyValuePair<string, GraphTransformValue>> values);
    public ImmutableDictionary<string, GraphTransformValue> Values { get; }
}
public sealed record GraphBindingValue(GraphHandle Graph, ParameterValues Parameters,
    GraphUniformValues Uniforms, GraphTransformValues Transforms);

// 实际允许目标为 Family／Line／Mark／Group；Base／Cluster 不伪装 Family。
void TextOutput.BindGraph(TextTarget target, GraphBindingValue binding);
void TextOutput.SetMaterialUniform(TextTarget target, MaterialSlot slot,
    MaterialHandle material, string nodeId, string name, UniformValue value);
```

新增 TextWrite.GraphBinding／MaterialUniform，ComposePureOutput 投影真实
ImageEffectBinding 和材料 edit。Graph 的同实例同目标后写为完整替换，跨实例按工程顺序
执行；GraphUniformValues 随 GraphBindingValue 整项替换，不自动和上一绑定合并。当前
不新增独立 SetGraphUniform；将来若新增，再明确它的覆盖作用域。
每个 write 的 intrinsically invalid metadata／UniformValue 自身有限性／声明值类型／array
shape 必须立即失败，即使后来被替换。实际安装 shader 的 uniform existence／type／array
匹配在最终身份阶段验证，不在写入时绑定到当时旧 shader，否则会恢复 v1 的旧身份早验。
MaterialUniform 需要真实包／slot／node/name／类型，不能把旧图的数值套到新图。
v2 material reducer 按主计划先确定最终图身份再检查剩余覆盖；现有 v1 StyleUpdate 的即时
identity 校验不能直接当作 v2 规范。完整 Replace 形成新的覆盖作用域、显式清掉之前覆盖；
该作用域内存活的 uniform 按 identity/name 后写，最终安装 handle 必须匹配。必须用
uniform-before-replace、replace-before-uniform、A→B→A 和非法被覆盖写入固定边界。

ImageEffectBinding 扩充不可变 Uniforms／Transforms；ImageGraphFrameContext 同样带
GraphTransformValues。每个覆盖 key 只指向实际 Transform node；矩阵和 OutputBounds
有限且 float32 可逆，输出框和反向采样需求满足节点声明。CompiledImageGraph 的
Shapes／Preflight／DrawNode 只通过一个共同 resolver 取最终矩阵／框，不能三个路径各算
一遍产生差异，也不能用 shader Matrix3x3 uniform 冒充 Transform node 的真实几何／区域。
这些覆盖进入结构相等／帧 fingerprint／MaterialAuthorGraphCache 和所有缓存 key。

修改 FrameEffectHost.PackageOutputs.ValidatePackageOutput：保留 owning Contains、真实
graph export 的 bindingKinds/output kind+target、完整 PackageSdkParameters、ColorOnly／
Warp 边界，再校验 Uniforms／Transforms 和每次 write 的 scalar／array 数量账。修改
PreparedRenderFrame.WithImageEffects 保留唯一 (instance,target) 与真实 load 检查。
Pipeline.ImageScene.Binding.ApplyImageObjectBinding 及独立 ExecuteSceneImageEffect
都把覆盖传同一个 context，不允许一条路径读新值、另一条仍读固定字面量。

direct graph v2 新增明确 host-owned 活动种类／load 分支：无 DLL、不调用 Declare，按
实际 binding 生成纯确定的图绑定，支持真实 group。不能只删除 CreateEffect 拒绝，让没有
DLL 的资源导出进入 IPureKaraokeEffect 装载，也不借用 v1 有状态生命周期绕过门禁。
source material／纯资源包继续用 PreparedSourceMaterials→CompiledPackageGraphExports
→SceneMaterialProgram，不为 CustomInk 伪造 controller。Base 独立包材质当前仍在
TextPartitions 抛 UnsupportedPartitionMaterial；M09 必须实际实现或在 D8 登记具体阻塞，
不能因 SetFill 接受 MaterialValue 就宣称该粒度已完成。

### 10.2 组树、Color/Erasure 和真实下层

现有 GroupIsolation 的 opacity-only 隔离树扩展为“有 opacity 或 group graph 即隔离”的
固定运行父树，使用真实 SourceNodeId 别名合并 active+exit；保留非连续后代
GroupIsolationChangesOrder 诊断与组插入位置。顺序是叶子 token 材质／token 图→line 图→
按原孩子顺序 child group→parent group，同目标内保留实例顺序。

正式 ImageScene 统一复用现有 ImageCompositionPayload(Color,Erasure)：

1. CaptureCompositionPayload：执行有序同目标图链／普通 surface。
2. ComposePayloads：不同孩子按场景顺序合成，不能套用同目标历史替换范围规则。
3. ResolveCompositionOpacityLayers：共同组 alpha 后仍保留局部层捕获能力。
4. InheritCompositionErasure：父图保留孩子先前替换造成的 erasure。
5. CompositePayload：最终在真实下层应用颜色和独立擦除。

同目标历史替换区域取 max，独立孩子按顺序 composition；这两件事不能混用。Erasure 不
能从颜色 alpha 反推。组 opacity 在 children+自身图完成后对 Color／Erasure 各乘一次，
不能父组对每个子项重复乘。不得用 raw CaptureSceneTarget(Group) 重画原始孩子，丢失
其已执行材质／图；也不得把含 below 的组截图直接当 TargetColor。

BackdropBelow 必须在目标／隔离组插入位置由调度器提供：父组图读取组外既成下层，
子目标读取其之前合法顺序的下层。BackgroundMedia 仍从 CaptureSceneBackground 获取
真实媒体，不含歌词／overlay，不可替代 Below。现有文字前一次绘全部 BehindText 的
flattened canvas 不足以排除当前实例 owned overlay；需保留 owner 标记的 BehindText
层和先前兄弟 payload，按需要重组 Below 时排除当前实例的层，最终画布仍保留它们。
不能 alpha 减法“去掉自己”，也不能只删 ImageEffectOwnedBackdropPending 拒绝。

区域明确三框：semantic target bounds、pass physical bounds、反向传播采样需求。组的
ReferenceBounds 与 WorldInkCorners 语义不同，空组不伪造墨迹。反向区包括 Blur 3σ、
Transform 逆映射、shader maxSampleOffset 和 clamp/repeat。M09 可以暂保留当前整画布
physical allocation 以守正确性，但不能称其已经是 ROI，不能裁掉真实采样区域。

所有 Background snapshot、每组 Color+Erasure、partial 层、blur scratch、borrowed
inputs、owned overlay 重组，以及父／子同时活动的量，统一走同一 ImageSurfaceBudget
Frame／Session，先预留后分配。空／alpha0 组仍先检查全部包／shader／参数／矩阵／
uniform 和预算，不用不可见掩盖错误。再加确定性的组深度／后代／total capture pass／
typed write/scalar 配额，避免每图有限但深层组递归无界。失败、取消与诊断仍保留真实
owner、stage、target、TimeUs、revision。

### 10.3 图／组文件分工与验收

| 独占工作 | 文件／接缝 | 必须连通 |
| --- | --- | --- |
| 纯 SDK 图 write | 新 GraphBindingValue／GraphTransformValues／TextOutput.Graphs；TextOutputValues／Pure reducer／purity analyzer | 完整替换、typed覆盖、最终身份、所有权、cap、写入预算 |
| host direct graph | CompiledEffectPackage.CreateEffect；FrameEffectHost.Load／PackageOutputs；PreparedRenderFrame.ImageEffects | 无DLL纯资源与controller两条生产链；真实目标／load／binding校验 |
| runtime 图矩阵 | ImageGraphFrameContext；CompiledImageGraph Shapes／Preflight／Draw；ImageScene.Binding／Materials；MaterialAuthorGraphCache | 唯一resolver、范围／采样／外扩、相等性／缓存与诊断 |
| 正式组调度 | Pipeline.ImageScene／Binding／Surfaces／GroupIsolation；现有 CompositionPayload* | 处理后孩子、嵌套图、Color+Erasure、组alpha、partial捕获、真实Below排自己 |
| 验收样例 | CustomInk v3／LineGlitch v3／GlassInk v3＋独立 nested group fixture | 全资源与图实际执行，不能用三个样例代替组图测试 |

CustomInk 保留纯资源 material、unmasked、fill/stroke、sung/unsung 和透明执行，不造
空 DLL。LineGlitch 同时有直接 ColorOnly graph 与真实 typed controller 变体，覆盖动态
参数／uniform／Transform node，随机／重复 t 同像素且几何 anchor 不受 ColorOnly 改变。
GlassInk 保留 media 与 backdrop 两种 material，用不同下层实拍证明区别，覆盖 blur24+
refraction8 范围、透明 solid fallback 与 diagnostic。

nested group fixture 另覆盖子材质→子图→父组图、多组 alpha、active+exit、owned overlay
排除、先前替换 erasure、partial 捕获和预算负例。已有 payload 单测提供算法参考；正式
调度、Vulkan 和导出都要新证据。

## 11. M09 共享文件冲突和集成依赖

SVG、Warp、图都会改 TextOutput／Pure reducer／PreparedRenderFrame／Pipeline ctor／
Composition 根和预算。root 应先分配唯一文件 owner；其他代理新增 partial 文件，把必要
的小接缝用消息交 owner。不得平行改 capability switches、版本、合同或 Runtime.Hosting
cleanup。建议先冻结以下共享项，再开放生产：

1. ResourceCatalog 的两类句柄／variant 投影、Svg descriptor／validator／预算签名。
2. TextWrite.Warp／GraphBinding／MaterialUniform 和有序 reducer；frame 输出公共复制。
3. GridWarp／TextGeometryMapping 的 canonical float 与所有几何消费者；最终锚点 DAG。
4. ImageGraphFrameContext.Transforms 与唯一矩阵 resolver；组调度的 Color/Erasure 节点。
5. 固定存储与 frame 预算接口、typed owner、native scene lease 与清理顺序。

| 冲突文件组（相对 backend/src） | 同时消费它的工作 | 开工前处理 |
| --- | --- | --- |
| `Scripting.Abstractions/ResourceCatalog.cs`、`V2/ParticleSchedule.cs`、`V2/PreparedEffectDeclaration.cs`、`V2/VisualOutput.cs` | SVG SDK／native／renderer／样例 | 一名 SDK owner 修改；其余只消费冻结签名 |
| `Scripting.Abstractions/V2/TextOutput.cs`、`TextOutputValues.cs`；`Scripting/Execution/FrameEffectHost.Pure.cs` | Warp write、graph binding、material uniform | 先 partial／新增封闭 write；一名 reducer owner 合并所有 write，逐次校验且事务发布 |
| `Rendering.Abstractions/PreparedRenderFrame*.cs`、`Scripting.Abstractions/GeometrySamples.cs`、`PlaybackGeometry.cs` | Warp最终几何、粒子anchor、图target bounds、SVG批次复制 | geometry owner 定义 mapping；frame公共复制文件唯一 owner，不能各自漏字段 |
| `Rendering.Skia/SkiaRenderPipeline.cs`、`.Visuals.cs`、`.TextPartitions.cs`、`.ImageScene*.cs`、`.GroupIsolation.cs` | SVG渲染、Warp glyph、组图／Backdrop | 新功能尽量新 partial；构造／总调度和共享 switch 由一名 owner 合并 |
| `Rendering.Abstractions/ImageGraphFrame.cs`、`Rendering.Skia/Effects/CompiledImageGraph*.cs`、`MaterialAuthorGraphCache.cs` | typed图矩阵、材质覆盖、SVG滤镜的有界pass | graph owner 固定唯一resolver；SVG只复用冻结预算，不自行扩张图语义 |
| `Rendering.Skia/SkiaRenderBackend.cs`、`Effects/ImageSurfaceBudgetSession.cs` | atlas mip上传、Warp局部surface、组图payload／scratch | 一名device／budget owner；预留、bind、alias／device归属和释放合同一起审查 |
| `Core/Effects/EffectPackageDefinition*.cs`、`EffectPackageProfiles.cs`；`Scripting.Compiler/ScriptV2PurityAnalyzer*.cs` | SVG角色、Warp／graph能力、SDK白名单 | root协调单点集成，执行器接通后再开cap和规则身份 |
| `Scripting/Compilation/CompiledEffectPackage.cs`、`CompiledProjectEffects.cs`；`Runtime.Hosting/ProjectRenderSessionFactory.cs`、`ProjectRenderSession.cs` | validator注入、direct graph分派、SVG native装配／清理 | root保持唯一组合根owner；失败／取消释放与全部compile调用同时接通 |

前端作者资源角色、Worker／Snapshot DTO、metadata、版本、HTTP 合同与发布由 root 统一，
子代理交可审阅的类型／文件变更，不各自改变生成物或测试／构建进程。

组合根准备时先建立全部 metadata／资源闭包与 validator，固定同一包 revision；native 装配
与最终 pipeline 后才发布会话。任何一块失败均不部分启用控制器／资源；也不能把 SVG、
Warp、组图的某一项失败吞成透明替代，除非是包合同明确规定的背景缺失 fallback。

## 12. 本轮检查边界

本轮只读当前源码、已有固定 tag 调研与本地安装 SkiaSharp 3.119.4 XML API；官方 API
索引延续 SVG notes。SkiaSharp mip overload 的上游入口：
https://github.com/mono/SkiaSharp/blob/v3.119.4/binding/SkiaSharp/SKImage.cs 。
当前未运行任何 SVG 样例／Vulkan 上传／mip／资源趋势，未构建或恢复依赖。设计交付后先由
root 冻结共享签名再进入生产编码；最终 acceptance 不能引用本文代替运行证据。
