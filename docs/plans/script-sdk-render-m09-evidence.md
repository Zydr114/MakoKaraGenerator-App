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

## 未完成项

- Warp 导出、HTTP 及作者工作流；真实包准入和像素接缝的有限证据见上。
- SVG 生产 Picture／atlas／实际 mip、资源生命周期及完整 affine Sprite；目前描述与静态验证已提交。
- v2 graph／运行时 Transform 绑定、组图调度与 BackdropBelow。
- M09 的匹配合同／发布／fresh HTTP；M10 和 M11 尚未关闭。
