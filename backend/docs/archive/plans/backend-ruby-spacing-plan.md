# Ruby spacing solver 有限实施计划

日期：2026-10-19。设计基线：`refact/ttml-first-support` / `e896127`，产品 0.96.0、合同 1.27.0。本批产品0.97.0／合同1.28.0的有限范围已完成，分层验收、发布及边界见[§7实施结果](#7-实施结果)；下文设计基线与门槛保留原归属。

## 1. 本批范围和兼容边界

实现横排歌词中ruby 聚合墨迹碰撞的自动消解：只在必要的安全文字边界加入水平间距，保留原字形、原 Start/Center/End 对齐和 ruby 外伸。共享结果用于画面、场景布局、token/family/ruby geometry、进度路径及后端几何读取。

启用条件与时间完全无关。`RubyAnnotation.Segments` 的数量、时间状态和当前 progress 不参与求解；有时间和无时间的同一文本得到相同几何。无需新样式模式、编辑命令或磁盘字段。

- 没有碰撞的行直接返回原始布局和原始定位；单 token/单 ruby 保持现状，包括宽 latin、组合字符和 emoji。
- 存在碰撞的历史无时间 ruby 会得到新的排版，这是本批预期行为变化。TTML P5 中的旧画面零差属于时间轴切片；本批保留既有无碰撞 golden 的零差，不承诺保留错误碰撞画面。
- 不做竖排、完整 Unicode 双向排版改造、TTML 全部 ruby 排版属性、自动断行、自动缩小某一 ruby、独立歌手/轨道或桌面 UI。
- 不把本批等同于完整公共 B3/B4、B5/B6、浏览器验收或性能/长片/平台矩阵完成。

## 2. 当前代码与完整调用链

当前 `SkiaRenderPipeline.Ruby.cs::MeasureRuby` 对 `annotation.Text` 聚合整形，根据基础 token 两端 `shaped.XAt()` 计算 ruby 的 Start/Center/End 横坐标；竖直位置是基础墨迹顶部减 `GapPx` 再减 ruby 墨迹底部。它不扩大基础 token 间距。`GroupInk` 合并基础和 ruby 墨迹。

当前入口链：

1. Core `LyricsDocument/Branches/Lines/Tokens/RubyAnnotations` → `ProjectSnapshot`。
2. `ProjectRenderSessionFactory` / Runtime frame session → `SkiaRenderPipeline.GetSceneFrame` / prepared frame。
3. `SceneLayout.cs::MeasureSceneGeometry` 取得完整行 shaped，调用 `MeasureRuby` 和 `GroupInk`，交 `Geometry.cs::BuildGeometry` 进行锚定、maxWidthRatio 缩放及世界变换。
4. `SkiaRenderPipeline.cs` 实际绘制再次取得 shaped 和 ruby；`TextPainting`/`SkiaTextLayoutEngine.DrawLine` 消费固定 draw units；`DrawRuby` 只根据 segment progress 裁切聚合 ruby。
5. `TokenGeometry.cs` 生成 base/token/family/ruby 墨迹、draw units、progress path；`SceneFrame.cs` 生成场景元素；`FamilyTransforms.cs`、capture/动态覆盖等使用同一套 glyph/geometry。
6. Backend snapshot codec → 固定版本 Worker → 同一 Runtime/Skia → PNG、场景测量/几何、导出和验证。当前没有新的通用后端画布命中 API；消费者依赖返回的 geometry 做拾取，不能声称本批交付完整浏览器交互。

以上各条路径不能各自重算一种 spacing。只加宽 `GroupInk`，或者只平移 ruby 而不修改基础字形/进度/geometry，均不合格。

## 3. 确定性求解策略

### 3.1 输入、输出和单位

纯求解器放 `MakoKaraGenerator.Text`，不依赖 Skia。输入是完整行 `ShapedLine`、token UTF-16 范围和有效 ruby 的聚合 `ShapedLine`/墨迹、对齐及竖直 gap。输出包含重新定位的基础 shaped、每个 ruby 的统一位置和联合墨迹。布局坐标均为缩放前像素。

`GapPx` 保持现有竖直间距语义，不能改作水平间距。水平最小 gap 冻结为 **1 个布局像素**，不增加用户参数。水平策略与现有竖直 GapPx 语义独立；布局缩放后视觉 gap 随行统一缩放。两组墨迹的垂直区间须有严格正长度交集（仅相切不算），水平方向间距小于 1 时视为需修正的间距冲突，等于 1 时原样保留。此定义不依赖 raster 像素采样或当前时间。

### 3.2 安全边界和相邻约束

1. 完整行整形一次，每个 ruby 的完整 `Text` 整形一次。绝不按 token、segment 或碰撞后的子串重新整形。
2. 合并 grapheme 与 HarfBuzz draw-unit 的相交源范围，构造不可拆单元；只能在完整单元之间插入 gap。平移需同步 Elements、DrawUnits、glyph X/ink、Advance、Width、InkBounds 和 `XAt` 的映射。
3. **ruby 锚点使用原始 token 的自然左右边界加 token/刚性组的整体位移**，仍使用现有 Start/Center/End 公式。新增 gap 不属于前 token 的自然 advance，不能再直接使用扩宽后 `shaped.XAt(tokenEnd)` 作为前 token 的 ruby 右锚点：该值可能包含间隙，使 End/Start 两侧 ruby 同步移动而无法消解碰撞。求解器须显式保留自然边界与单元位移。外伸是 ruby 墨迹相对基础范围的真实差值；不强制把所有基础列扩到 `max(baseWidth,rubyWidth)`，避免无碰撞场景不必要变化。
4. 以基础位置的视觉单调顺序考虑有效 ruby；相同位置按稳定输入顺序处理。当前 ruby 必须与**所有此前**有严格纵向墨迹交集的 ruby 检查间距，包括跨一个或多个无注音 token 的宽注音；不能只比较列表前一项。对不足 1 像素的间距，在两者基础范围之间的安全边界插入满足全部此前约束的最小非负间距，并平移后续完整单元/ruby。保留已经足够的间距，使用确定性顺序和有界扫描，不依赖时间、不迭代到经验阈值。
5. token 边界落在共享 shaping cluster 内时不能拆 glyph。实施必须选择可验证的保守策略：把共享范围合成刚性组，仅能在组间插 gap；组内无法用合法边界消解的碰撞保留原定位。本批不能声称解决此类组内冲突，需内部结果或判定分支明确标识 unsupported 并有专用回归和文档列明；不增加公共诊断 DTO、failureCode 或 wire 契约。不能通过重整形子串伪造解决。
6. 空白、空墨迹、无效 ruby/base 关联、正文与 token 拼接不符维持现有容错；无效/无法单调映射的输入不能产生 NaN、无界循环或不一致 draw units。完整 RTL/bidi 最优约束不在本批，必须保留共享字簇且给出有界保守行为。

### 3.3 画布、对齐和时间轴

扩宽后的基础和全部 ruby 联合墨迹交给既有 `BuildGeometry`；其 maxWidthRatio/safe-area 宽度限制统一缩放整个行家族。单个很宽的 ruby 仍按既有联合墨迹自然缩放。不得对 ruby 或 segment 独立横向压缩。

本批保证正常中立位置、未旋转的固定验收场景在既有宽度限制内；用户手动平移、旋转或变换造成出画，不自动钳制。几何墨迹仍遵守现有不含描边/阴影外扩的定义。

ruby segment clipping 继续按聚合 shaping cluster 分配，只增加整个 annotation 的位置平移；segment 时间、ID、边界归属、sung/unsung 与基础 token 采样不变。正文 `XAt` 和进度路径使用新的完整单元位置：间隙按现有连续 offset 边界语义归前一个 token 的 progress interval，下一 token 从平移后起点开始；这只延长前 token 无墨迹部分的进度路程，不移动其自然右锚点、不拉伸 glyph。line/token/family 几何与实际 draw units 使用平移后的墨迹，ruby 锚点使用上一节独立自然边界，不能混用。时间变化只能改变 paint/clip，不能改变 spacing。

## 4. 文件与版本影响

| 层 | 预计修改 | 验收关注 |
| --- | --- | --- |
| Text | 新增纯 `RubySpacingSolver.cs` 及必要布局结果；必要时扩展 `ShapedLine.cs` 的完整单元平移工具 | 无 native 依赖，保留字形/映射和 identity fast path |
| Skia | 新增共享行+ruby 布局入口；修改 `SkiaRenderPipeline.Ruby.cs`、主文件、`SceneLayout.cs`、`TokenGeometry.cs`、`SceneFrame.cs`、`FamilyTransforms.cs` 和实际发现的 glyph 消费点 | 每个路径消费同一结果，缓存不能只按原文本误复用 |
| 测试 | Rendering 的 solver 数值/画面/geometry 回归；Backend.Rendering、Runtime.Hosting/Worker、Server 的匹配发布链路 | 固定 oracle，独立进程，终态与排水 |
| 文档/发布 | Directory.Build.props、合同版本/生成物、API 行为说明、ADR、roadmap/实施记录/入口 | 产品建议 0.97.0，合同建议 1.28.0，最终以主代理冻结决定 |

不新增磁盘字段，Lyrics schema 保持 v17，项目 schema 不因布局算法改变；无新 HTTP route/DTO/command，预计 path/method/schema/command 数量保持。布局可见行为改善采用产品 MINOR、合同 MINOR，保证 Server/Worker 的 `EngineIdentity` 匹配，旧 Worker 明确拒绝新身份。snapshot profile 与 wire shape 无需为同构数据升级。

缓存 key 应覆盖基础 shaping options、token 边界、ruby 文字/对齐/scale/gap 与 solver 策略；不含 progress。若不新增跨帧缓存，可使用有界局部结果并沿用原 shaping cache，不在本批扩展缓存生命周期。

## 5. 分阶段验收门槛

### S1 纯 solver 与独立数值 oracle

- 人工构造的基础字形/墨迹输入，手算至少三组相邻碰撞、连续三组传播、跨无注音 token 与更早 ruby 的冲突、Start/Center/End、负 bearing、垂直不相交/仅相切、0.99/1/1.01 像素临界间距、单 ruby、无 ruby 和已足够间距。
- 不用生产 solver 计算 expected；验证准确 gap/位移、无 glyph ID/数量变化、无子串整形、单调映射、联合墨迹完整覆盖。特别以 End/Start 相邻宽 ruby 验证新增 gap 不被前 token 的 ruby 右锚点吞入，精确数值一次求解到位。
- 一个组合字符/连字跨 token 边界的例子证实完整 cluster 保持；不隐藏组内未解决边界。
- 无碰撞输入原对象直接返回，或证明全部坐标/浮点值严格一致。

### S2 Skia 共享链路

- 现有 `RubyLegacyBaselineProbe` 的 3 文本 × 6 时间 × 普通/透明共 36 帧，像素 SHA、PNG SHA、geometry JSON 零差；不得重新生成 golden 以使测试通过。
- 新的相邻宽 ruby 碰撞夹具，逐组验证实际聚合墨迹 gap、Start/Center/End 锚点、glyph 位移、line/token/family/ruby geometry、progress path 与实际 draw units 一致。
- 多时间帧 spacing/geometry 恒定；相同文本有时间/无时间一致；旧 sampler/segment 进度 oracle 保持。
- 覆盖变换/capture/覆盖绘制实际路径，不能只验证主画面；中立窄画布验证全家族缩放及边缘不意外裁切。

### S3 匹配 Worker/HTTP 与发布证据

- 发布匹配 Server+Worker，登记 clean source ref、输入/部署 hash 和冻结合同。
- 通过真实 HTTP 导入/建立至少一个相邻 ruby 碰撞工程，执行 geometry+PNG 同帧测量及多个 segment 时间帧；与独立同源输入的原 Runtime oracle 比较。
- 额外保留无碰撞 legacy 输入及单个宽 ruby 输入，证明原几何和画面不变。
- 新旧 EngineIdentity 不匹配明确失败；所有 job 等待终态，Server/Worker/native 观察身份退出。
- 合同 freeze/check/validate 和真实 HTTP exchange 校验通过；失败过程独立存档，不混入通过数字。

### S4 关闭

相关构建/定向回归通过，主代理审核 solver 的约束、独立 oracle、真实发布证据及边界，再登记实施结果，按步骤提交推送。未达到 S3 的阶段不能标记“后端 spacing 全链路完成”。

## 6. 独立性与证据归属

数值 oracle 使用手算合成字形；像素/geometry 兼容性使用本批之前已固化的 legacy golden；Worker 接缝使用同一输入的独立本地 Runtime 实际输出，三者分别回答算法正确、旧行为保留及跨进程一致性，不能互相替代。

现有 TTML 3929、综合验收 3930、0.95 HTTP 257/1757 均为历史基线，不计作本批新跑结果。新证据需登记唯一目录、源码 ref、命令退出码、计数、hash 和观察身份。


## 7. 实施结果

### 7.1 S1～S4状态

| 阶段 | 状态 | 有限证据 |
| --- | --- | --- |
| S1 纯solver | 已通过 | 手算合成glyph/墨迹oracle；固定1布局px gap、全此前ruby约束、严格垂直相交、安全token/cluster刚性组、自然anchor、保守unsupported及无变化identity |
| S2 共享Skia | 已通过 | Rendering1012/1012；旧36帧BGRA/PNG/geometry golden原文件不改；新增宽ruby/对齐/时间无关/进度/窄画布/变换/capture路径；6处GetLineLayout共享消费 |
| S3 Worker/HTTP | 已通过 | 匹配新Worker12/12、fresh发布HTTP179/2689、27组完整同帧source/PNG/definition geometry、21成功job及全部观察身份退出 |
| S4 主验收关闭 | 本批有限范围已关闭 | 主代理核对源码/测试稳定性、正常solution、合同及835输入/806部署/542证据SHA；完整边界见§7.4 |

产品0.97.0／合同1.28.0，无新route/DTO/命令/持久化字段，80 paths/89 methods/449 schemas/91命令/2648 examples（2040 negative），歌词schema17及既有工程schema保持。旧TTML3929与综合3930均属历史，不并入本批1330。

### 7.2 定向回归和独立验收

源码提交80a1943。正常solution零警告/错误；去重相关回归1330/1330=Rendering1012＋Backend.Rendering.Skia9＋Backend.Rendering217＋Backend.Worker80＋Runtime.Hosting匹配Worker12；新增25例已包含其中，不能再加。旧36帧golden不是36个额外test case。日志/TRX与源码hash见`artifacts/ruby-spacing/validation/assessment.json`，主代理独立核验见`root-code-acceptance.json`及`root-static-review.json`。

`MinimumGap 1f→0f`单项变异使3例实际失败，精确bytes恢复后19/19通过；恢复solver SHA256为`8cf54f9319aec8af851efe2bcfdbb6fa95f83b8dc6d52798fb3b5e3ca754273e`。初次编译缺命名空间、ImmutableArray引用比较、无效token时间/旧Worker构建产物及package夹具旧binding等失败过程独立保留，不并最终通过数字。

合同fresh metadata→freeze/write→check→validate全exit0，最终证据`artifacts/ruby-spacing/contract/final/`；主代理独立确认OpenAPI全部paths/schemas与examples未变，只有版本/基线元数据变化，legacy golden未变。发布/HTTP必须使用匹配0.97 Server+Worker。

### 7.3 HTTP和证据边界

fresh self-contained发布源码为`cc30d4c2172cc9dfa4b2bf2706a736d7abc392c6`，`sourceDirty=false`；835个生产输入、806个部署文件和542个evidence文件SHA由主代理独立核验。真实HTTP **179请求／2689断言**，27组同帧比较完整source SHA、PNG及完整definition geometry全部一致，21个job全部succeeded；55个观察身份（含36个Worker）全部退出，Server exit0。541个证据文件凭证扫描通过。合同对179个实际JSON响应及56个成功JSON请求校验通过，0跳过；HTTP请求/响应不能相加记为235次请求。

同源Runtime oracle只对`AudioPath`声明做标准化，前提是两侧音频size和SHA相同；标准化之后仍强制完整source hash匹配，没有忽略整个音频声明或其它source字段。layout模式采用现有固定代表性句子，不渲染真实ruby，实际spacing验收采用time模式；27组不能全部称作实际ruby碰撞帧。旧工具三次失败独立保留：37请求/242断言（AudioPath身份）、35/243（nullable来源）、40/477（layout固定definition），不并最终179/2689。最终发布不是这些失败发布的复用。

最终证据为`artifacts/ruby-spacing/root-final-acceptance.json`、`assessment-final4-cc30d4c.json`、`http-final4-cc30d4c-contract.json`及`http-final4-cc30d4c/summary.json`；summary SHA256为`3d95e19feb2759f5cbc31d2b049cf94d3e31a0c118e18826b00247d2272274a6`。部署与证据manifest、排水身份和源码/测试稳定性核验详见主验收JSON。

EngineIdentity negative证据为codec/版本不匹配回归，不能称实际运行旧0.96二进制完成新旧Worker验收。匹配0.97 Worker的12项是真实跨进程测试；本批未实跑旧0.96 Worker二进制，不合并口径。

### 7.4 有限关闭范围

只关闭横排单调映射的自动ruby间距与共享渲染/geometry链路。共享cluster组内无法合法消解的冲突保守保留，非单调/不精确映射不强行排版；自然overhang保留，用户主动平移/旋转出画不自动钳制；无新增公共诊断或画布命中API。完整RTL/竖排、完整公共B3/B4、B5/B6、浏览器/用户、性能/长片/GUI/平台矩阵仍独立。
