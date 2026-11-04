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

## 未完成项

- Warp 像素接缝、真实包准入与 Declare/Evaluate、取消／预算／owner、导出及作者工作流。
- SVG 生产 Picture／atlas／实际 mip、资源生命周期及完整 affine Sprite；目前描述与静态验证已提交。
- v2 graph／运行时 Transform 绑定、组图调度与 BackdropBelow。
- M09 的匹配合同／发布／fresh HTTP；M10 和 M11 尚未关闭。
