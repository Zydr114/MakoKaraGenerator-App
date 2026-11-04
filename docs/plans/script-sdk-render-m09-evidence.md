# M09 SVG、Warp 与图资源证据

2026-11-05，分支 `dev/script-sdk-render-upgrade`。本节点进行中，不代表 M09 或阶段一通过。
按用户最新指示，性能门槛暂不作为验收标准；正确性、像素、确定性、预算、取消和实际作者链继续验收。

## 已交付的几何接缝

此前 `670663d` 提供封闭 `GridWarp` 与 `TextGeometryMapping`；`a863b39` 将网格接入准备帧、
播放头与最终锚点。网格仍须满足规范 float 顶点、正向三角、简单边界与闭定义域要求。

本次增加 `ProjectRectangle` 和不可变 `TextGeometryProjectionMesh`。多级 Grid 按规范三角
精确剪交，同步插值源纹理坐标与目标坐标；Affine／反射保持同一映射顺序。累计投影顶点账
与 `ushort` 索引上限均为 65,536 顶点；这不是完整 CPU 剪交成本的计量。取消在入口、每层、
候选三角与发布前检查；预取消不扣账，同一预算随后可恢复。

`.local/script-sdk-m09-20261105/build-01.log`：全后端解决方案构建零警告、零错误。
`mesh-tests-02.log`：GridWarp／Mapping／Mesh 定向 26/26 通过，其中 Mesh 6 项。
该构建同时包含尚在验收的像素接缝，不能把这个构建记录当作尚未开放 capability 的作者端到端证据。

## Warp 像素接缝（产品 0.112.5，能力门禁仍关闭）

Skia 使用源坐标局部表面与精确带 UV 网格。基础字／Ruby 的演唱裁切、填充、阴影、辉光
先在源空间完成，再按完整局部映射绘制；world 矩阵只由父画布应用一次。相同映射只合并
相邻字簇，保留绘制顺序；仅 Ruby Warp 保留共享基础连字原路径。identity 网格继续使用
原矢量绘制。装饰框与准备阶段共用 3σ 保守范围，透明 gutter 不扩大作者定义域。

源纹理密度按每个三角的 Jacobian 乘实际画布矩阵后取最大奇异值上界，包括输出倍率与
后续 affine；超尺寸／字节限制明确拒绝。源表面及内部显式 layer 使用共享帧账，投影顶点
按同一帧累计；取消原样传播，非取消错误带 Warp 实例／制品／包／阶段／时刻归属。

本地证据：

- `warp-density-02.log`：CPU 21/21；`warp-vulkan-02.log`：显式 Vulkan 21/21，未跳过 GPU。
  后补标记 identity／局部仿射组合，修正 identity 网格不能丢失 mark affine；
  `warp-final-cpu-03.log` 与 `warp-final-vulkan-02.log` 各 23/23。
  覆盖 identity、family/base/cluster/Ruby/mark、非线性形变／锚点、双色演唱、三级 affine、
  共享连字 Ruby、宽阴影／辉光、透明／重复、2x 输出／4x 局部放大，以及预算失败／中途取消恢复。
- `warp-mutant-01.log`：临时绕过实际 DrawVertices 后 13/18 失败；已恢复生产绘制，后续测试为绿。
- `rendering-full-01.log`：1396 通过、1 项 SourceMaterialRuntime 的 Roslyn Emit 空引用失败。
  `source-material-recheck-01.log` 同类 12/12；`rendering-full-02.log` 全套 1400/1400。
  首次失败不删除，不凭后续通过断言已证明根因。
- `scripting-full-01.log`：475/475。
- `warp-package-red-01.log`：10 个新增真实包用例均被 `token.warp` 未开通门禁拒绝。
  此记录证明门禁有效，不是实际作者链通过；该测试文件留待下一准入切片提交。

标记的重采样 affine 对照最初使用独立的 48 像素阈值，实测差异 85 像素；最终与其他
Warp 平移用例统一采用相对位移画面的有限差异断言，并独立验证锚点。它不是 D13 的
优化像素容差通过证据，缩放／重采样质量仍保留后续独立验收。

当前记录没有宣称导出、HTTP、作者 UI、SVG 或整个 M09 完成，也没有作性能通过结论。

## Warp 真实包准入（产品 0.112.6）

正式开放 `token.warp`、`cluster.warp`、`mark.warp` 三项 v2 能力；SVG 和图组的门禁仍独立。
`V2WarpPackageTests` 以真实 v3 包走 Declare → SetWarp → Prepare → 实际绘制，覆盖 family、
base、cluster 和 mark，以及近端／3 分钟／10 分钟乱序、重复和重开。后实例 null 关闭、
不同包修订覆盖后的最终 owner、256／257 次写入及稠密网格累计预算、三级 Warp 后的
playback cursor 与真实 CursorTrail 粒子出生锚点均纳入。

- `warp-package-green-01.log`：CPU 参考像素 10/10；
  `warp-package-vulkan-01.log`：显式 Vulkan 10/10，没有跳过。
- 生产 CPU v2 准入仍明确拒绝；CPU 参考绘制不是生产支持声明。
- `build-warp-admission-01.log`：本机 SDK restore 缺少 ASP.NET prune 数据，构建未开始；
  使用既有完整还原资产的 `build-warp-admission-02.log` 全解决方案构建零警告／零错误。

- `core-warp-admission-full-01.log`：1471/1471；`scripting-warp-admission-full-01.log`：475/475。
- `rendering-warp-admission-full-01.log`：1408 通过、4 项 MixedEffectPackage 动态 DLL
  TypeLoadException；`mixed-package-recheck-01.log` 同类 83/83，随后
  `rendering-warp-admission-full-02.log` 全套 1412/1412。失败日志保留，未确认根因，
  不以复验通过宣称这一间歇问题已经修复。

此处完成的是包与渲染接缝；导出、HTTP、作者工作流和 M09 整体验收继续推进。

## SVG native／SDK 基础（产品 0.112.7，能力门禁仍关闭）

固定恢复 `Svg.Skia 4.9.1`；最终依赖保持 SkiaSharp `3.119.4`、HarfBuzzSharp `8.3.1.5`。
还原日志 `svg-restore-01.log` 与本地 `svg-dependency-closure.json` 保留精确依赖／native 资产。
第三方许可证见 [SVG notices](../../backend/docs/third-party/svg-skia/README.md)，随后端构建／发布复制；
Svg.Custom 的 MS-PL 独立保留，未给 App 添加许可证。尚无 Windows/macOS 运行证据。

native 只消费静态编译器的 SafeXml；受限 loader 按已验证 hash+bytes 查图片，禁止字体服务。
Svg.Skia 负责 DOM／几何／filter 模型，应用窄 recorder 显式拥有临时 native 资源，不调用
上游非确定释放的 Picture／paint 缓存。vector 保留真实 Picture；atlas 在原点内容表面绘制后，
一比一复制到透明 padding page，再请求真实设备 mip 上传。内容／页／filter／上传并存量先预留。
同包 role:image 借现有完整解码；data image 在包内按内容去重。Scene lease 退役与帧存储借用分开，
最后租约才释放固定 native；跨会话帧账明确拒绝。

SDK 增加 VectorHandle／SpriteHandle、PathBatch／AtlasSpriteBatch、固定档位目录、完整 affine
与双 capability 校验。目录用同一元数据投影；作者纯度只开放值与句柄，不开放 DOM／IO／native。
六类图元共用字节估算，含真实拖尾节点与 batch header；预算失败锁存，作者捕获异常也无法提交
该帧。v1 已有图元与 v2 暂存共同计入共享帧账，失败 owner 保留实际跨线实例。

本地证据（均在 `.local/script-sdk-m09-20261105/`）：

- `build-svg-foundation-01..03.log` 保留取消参数顺序、API in/ref 和测试 analyzer 的首次编译失败；
  `build-svg-foundation-04..06.log` 完整解决方案构建零警告／零错误，后续功能修正另行完整复验。
- `svg-sdk-budget-red-01.log` 4 失败／7 通过，确认作者能吞预算错误；修复后
  `svg-sdk-budget-green-03.log` SDK／纯度／相邻粒子合同 39/39。
- `mixed-overlay-budget-red-01.log` 2/2 失败，确认混合 v1/v2 漏记；修复后两条 full/fast
  路径纳入 `svg-native-cpu-04.log`，含同会话恢复、实际 owner 和精确上限成功。
- `svg-native-cpu-01.log` 暴露 gradient clip 全空及 filter 色偏；诊断发现上游空可选 ClipPath
  占位被当作空相交，以及上游 255 尺度 colorMatrix bias 与固定 Skia 的 0..1 单位不一致。
  窄转换修正这两项，SaveLayer 沿完整 region；没有降低语义像素断言。
- `svg-native-cpu-03.log` 仍有 gradient 边缘 alpha 差 4；原点绘制后复制到 padding page，
  配合同一 sRGB 对照，`svg-native-cpu-04.log` 15/15。四类 fixture 各比较 vector／atlas，
  仍要求 alpha 差≤2、预乘颜色差≤2/255，并有独立非空／颜色／孔洞／外扩断言。
- `svg-native-vulkan-01.log` 13/13，明确设置 `MAKO_GPU_SMOKE=1`。实际 GPU 测试验证
  texture-backed、设备身份、完整计划 mip 字节账及缩小时相对 no-mip 的高频混叠下降。
  绑定未提供 mip 层查询：此证据是计划层数＋请求上传＋真实采样差异，不是直接枚举 native mip 层。

- `scripting-svg-foundation-full-01.log` 492/492；`rendering-svg-foundation-full-01.log` 1427 通过、
  15 个新 clip profile 用例失败。失败均属于新增用例：合法空白 transform 被误拒绝、clip use 未具名拒绝。
- `svg-clip-transform-red-01.log` 21 失败／9 通过；修复非消费前瞻后
  `svg-transform-green-clip-red-01.log` 23 通过／7 个尚待限制的 clip 内容失败。
  grammar-2 只接受直接形状裁剪，包含未引用 defs；普通绘制的组／symbol／use 保持可用。
- `svg-clip-rule-red-01.log` 3 失败／35 通过：上游 clip 父规则覆盖子显式规则导致孔洞错误。
  受控 XML 固定叶子的有效规则后，`svg-clip-foundation-green-02.log` 53/53；包括显式空裁剪
  与可选空占位的独立像素对照。第一次空裁剪断言误用 straight 透明白，已改为真实预乘透明黑。
  SDK 纯度规则身份提升为 `sdk2-purity-4`，旧编译制品不会冒充新增白名单规则。
- 最终 `build-svg-foundation-07.log` 完整解决方案零警告／零错误；
  `scripting-svg-foundation-full-02.log` 492/492、`rendering-svg-foundation-full-02.log` 1465/1465。
  `svg-native-vulkan-02.log` 51/51，显式开启 GPU 测试，再验 grammar-2 后资源／mip 路径。

基础用例中的默认 GPU 测试会在未设置环境变量时提前返回，故 CPU 数量不能单独作为 GPU 证据。
此切片还未接生产 typed overlay 分派／Runtime.Hosting，因此 SVG capability 保持关闭。

## SVG 运行与作者编译接线（产品 0.112.8）

正式开放 `visuals.paths`、`resources.svg.vector`、`resources.svg.atlas`。typed handle 固定
包修订／档位；Runtime.Hosting 先持普通图片，再准备 SVG owner 与 scene leases，并把全部
资源纳入同一 session/frame 预算。所有编译入口在发布目录前校验完整 inspection 与资源闭包。
vector 整体 tint、atlas 完整 affine/mip 采样及后端 float 矩阵检查进入真实 Overlay 分派。
绘制／准备失败保留真实 owner，取消原样传播，释放顺序在设备锁内完成。

作者完整包编辑识别 `svg/svgResource`，要求完整文本集合和精确草稿 CAS。编译前验证全部
SVG 正文，失败回执保留具体 code/path、artifactHash=none；异步 job 成功产生失败报告时
job 为 succeeded 而报告 succeeded=false。修复生成新 source revision；编译不自动采用。
资源读模型同时识别这两种角色，未知角色仍 unsupported，固定二进制字节不被改写。

本地证据仍为 `.local/script-sdk-m09-20261105/`：

- `svg-runtime-admission-red-02.log`：能力关闭时17失败／1通过；开放能力后
  `svg-runtime-admission-green-02.log` **18/18**，显式 `MAKO_GPU_SMOKE=1`。
  真实 v3 包覆盖四类 SVG×vector/atlas、乱序／重复／远时刻／Export reason 重开、取消恢复、
  revision 双会话隔离、tint、native 失败真实 owner、CPU 拒绝／disabled、准备预算失败恢复。
- `svg-integration-vulkan-03.log` **19/19**，显式 Vulkan；alpha≤2，预乘通道差≤2/255。
  最初 fixture 在歌词上叠 tint、mip采样矩阵谐振导致失败，分别改为空歌词场景与非谐振矩阵；
  未为这两项改生产绘制或放宽像素断言。原失败保留于 `svg-overlay-validation-01.log`、
  `svg-overlay-vulkan-01.log`。
- `svg-author-workflow-red-01.log` 新增10项全部失败；补 editable/closure role 后
  `svg-author-workflow-green-01.log` **10/10**。包含vector/atlas×sync/async真实编译、
  保存冲突、坏SVG诊断、历史源隔离、修复编译、缺完整文本拒绝、下载二进制不变及未知role拒绝。
- `rendering-svg-integration-full-01.log` 1517通过／2失败：已有坐标错误现在携带真实owner外层。
  旧断言更新为同时检查 EffectFailureException 的实例／产物／阶段／时间和内部原始错误码；
  `rendering-svg-integration-full-02.log` **1519/1519**。
- `build-svg-integration-final-01.log` 全解决方案 **0警告／0错误**；
  `core-svg-integration-full-01.log` **1471/1471**；
  `scripting-svg-integration-full-01.log` **492/492**；
  `runtime-svg-integration-full-01.log` **153/153**；
  `backend-svg-author-regression-01.log` **41/41**。
  默认 Runtime/Rendering 数量不代表GPU执行，以上两个显式GPU记录单独归属。

该切片测试证明包运行与宿主作者链；匹配发布、fresh HTTP、工程导出和前端包编辑继续独立验收。
性能门槛按用户要求暂停，不据此声称性能通过。

## 匹配发布与 fresh HTTP（0.112.8／合同1.45.0）

实现提交 `05dbe85` 已推送，合同提交 `36f3b6f` 已推送。新构建 CLR metadata 生成
112 paths／125 methods／597 schemas，3434合成例（含2548反例）及漂移检查通过。
前端同源合同副本、类型检查与3937项单元测试通过；这不是完整包编辑UI验收。

匹配 self-contained Release/linux-x64 发布目录为
`.local/script-sdk-m09-20261105/dist-svg-0.112.8-01`；Server/Worker 的
`sourceCommit=05dbe8509cfd467b0f79e6a561e1a45ec8c0821d`、`sourceDirty=false`，
完整源指纹及两端依赖匹配。两端均包含4份Svg第三方声明，发布日志无warning/error。

`m09_author_probe.py` 的最终独立证据目录 `http-svg-04/`：**314请求／2385断言通过**。

- SVG vector／atlas：完整包GET/PUT、精确CAS冲突拒绝、坏SVG的sync/async具名错误与路径、
  禁止采用失败源码、第三修订修复、成功编译与显式采用。下载包逐文件SHA精确复核。
- 13张实际Vulkan透明PNG完整解码；包含三个fixture的同t重复、离开／返回，以及Base Warp
  同来源零位移反事实。SVG固定内部块每通道≤2/255，同t完整RGBA逐字节相同。
- 每包建立组合模板并应用工程，受管Image背景保存、关闭、重开后正式GPU导出。
  三个2秒／320×180／24fps的transparentProRes4444任务均抵达succeeded，报告严格验证，
  各48/48帧写出与完整独立RGBA解码、零失败／丢帧。SVG编码内部块容差12/255，
  此编码容差不替代直接PNG或渲染像素门禁。透明输出不绘制背景，但仍验证Image源闭包保留。
- 当前合同离线复核295份JSON响应、85份成功JSON请求，覆盖36个操作；19份二进制／未映射按实际范围排除。
- 13个硬件GPU Worker观测，843文件凭证扫描通过，HTTP排水无错误、无待终态任务，
  launcher按本次PID/startTicks排水；没有按名称停止其他进程。
- 前三轮保留为 `http-svg-01..03/`：探针先后误用alphaMode=transparent、缺模板metadata、
  project-only包误装入line模板；分别改为合同straight、显式metadata、composite模板。
  三处均为探针修正，未改生产实现以绕过服务门禁。

本轮仅Linux有限输入；未证明浏览器包编辑、共享纹理、任意SVG/Warp组合、跨平台或性能通过。

## 未完成项

- SVG／图／manifest 前端完整包编辑与实际浏览器作者工作流。
- v2 graph／运行时 Transform 绑定、Base 材质、组图调度与 BackdropBelow。
- M10 媒体轨与 M11 全样例／目标复杂样式仍待所属节点完整验收。
