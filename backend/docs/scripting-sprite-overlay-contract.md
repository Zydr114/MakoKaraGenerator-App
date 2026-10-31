# R6 固定图片精灵生产合同

> 2026-10-05；后续计划第 4 批，0.28.0。完整 R6、Bridge、目标内捕获和高级映射仍未交付，实际验收以 roadmap §18.91 为准。

## 1. 中立输出

`VisualWriter.Overlay.DrawSprites(OutputId, ImmutableArray<Sprite>, OverlayLayer)` 与圆点／线段／带状拖尾共用封闭 `OverlayBatch`、声明能力、名字空间、工程实例顺序及 ordinal 批次顺序；数组内部顺序保持。每个精灵计一项，包括完全透明的精灵；不另建 UI 层或模拟器。`ParticleRenderMode.Custom` 的真实粒子、出生记录、固定拖尾、seed 和其他预算保持。

`Sprite` 包含本实例固定修订的 `ImageHandle`、`Affine2 UnitToWorld`、`ColorRgba Tint`、归一化原图内 `Rect2 Source`。Source 默认完整图片，必须在 [0,1]² 内且非空；裁片被映射到单位方形，再由 UnitToWorld 映射到世界逻辑空间一次。矩阵可表达旋转、镜像、非等比缩放和剪切，须可逆且四角有限；不按文字矩阵再变换。作者显式选择中心或其他支点，无隐含图片尺寸或场景自动适配。正几何转换到 float32 后下溢、不可逆或裁片消失明确失败。

图片在 sRGB 中解码为预乘 Alpha；Tint 的 RGB 分别乘直乘图片 RGB，Tint.Alpha 乘图片 Alpha，最终只预乘一次。白色 Tint 保持原图片；不叠加文字／组共同 Alpha。CPU Skia 采用线性采样、无 mipmap，管线创建时编译固定宿主采样程序，采样坐标限制到像素裁片的半像素内缩范围；宽／高小于一个像素时对应轴固定在裁片中心。整数像素边界的图集裁片不混入相邻图块，分数像素裁片沿该坐标继续线性插值；不在帧内编译程序。Draft 仅沿原画布抗锯齿选择，不减少粒子。完全透明输出仍校验资源和几何，不借透明模式掩盖坏句柄。

## 2. 固定资源及失败

图片参数或 Resources.GetImage 只给逻辑句柄。EvaluateVisuals 收取输出时检查句柄属于本实例固定目录；同路径、另包或另修订拒绝 `InvalidSpriteResource`。正式异步组合根将已完整解码的所有包图片登记到固定 `SceneImageEffectResources` 精灵表；与图输入借用相同 SKImage，不重新解码、编译、读取磁盘或在每帧建立图片副本。全包解码门禁、hash、当前修订及工程预算保持。

缺少固定集合报告 `SpriteResourcesRequired`，没有当前修订图片报告 `SpriteResourceUnavailable`。PreparedRenderFrame.OverlayOwners 由宿主固定真实实例、ArtifactHash 及 PackageIdentity，图／组／视觉帧复制保留它；后端精度、资源或存储错误以 RenderOverlays 保留作者来源，不输出无效果成片。作者 writer 不接受编译身份。独立受控宿主未提供作者来源时保留原准备帧错误。

## 3. 生命周期与共享账

会话固定图片只记一次字节，多个精灵、图输入与别名借用同一 native 存储。正式 Render 的一张帧账同时覆盖两层精灵、文字／组隔离、捕获、图执行与合成；精灵借用持续至整帧完成，不能在各操作的独立账之间绕过峰值表面限制。失败或取消回收借用、图产物和像素缓冲；先关闭像素／作者会话，再释放资源表、固定账、包装和拥有图片的包集合。资源表自身只借用。

自身 BehindText 精灵沿既有 `ImageEffectOwnedBackdropPending` 门禁，未完成 owned overlay／自身排除时拒绝，不让新类型偷偷进入自身捕获。

## 4. 本步验收要求

- 中立几何、裁片、能力、名字、绘制预算和过期 writer；原生矩阵越限／下溢及缺失／另修订资源。
- 1×／2×／非等比密度的裁片、旋转、镜像、Tint 与 Alpha；与独立矩形的全部像素比较。
- 多图元同图存储去重、精灵与实际图同时占用同一帧预算，成功／失败／取消后归零。
- SpriteTrail 完整图片包分享、保存重开、双文字组合、24／30／60 FPS 同刻、暂停／回退及独立预览／Export。
- 仅图片改变形成独立包修订，可复用 DLL 但消费当前图片；旧修订仍可重放。
- 实际 MP4／ProRes4444 同帧和中途预算失败保护旧输出，模板／实例沿既有图片参数与层覆盖路径。

实际组合和像素证据见 roadmap §18.91。本步不代替 Bridge、完整 R6 或 GUI／R8 用户人工验收。

## 5. 实际接续证据

新增17项回归，完整图片包、当前资源修订、双文字及三种FPS同刻像素、重放、两次真实视频和中途失败保护均通过。共享账变异被回归拒绝且已恢复；最终同源码全量2927项通过、构建0警告／0错误，见roadmap §18.91。实际1.1s CPU帧 `/tmp/makokara-sprite-evidence/sprites.png` 已查看，包含21个真实存活粒子／602个旋转图片及固定拖尾图元，未声明用户GUI或完整R6验收。
