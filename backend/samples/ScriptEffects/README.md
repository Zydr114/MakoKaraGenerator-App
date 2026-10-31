# 公开效果包索引

这些目录是当前宿主可导入和分享的示例包。目录包必须保留 `manifest.json` 声明的全部文件和 SHA-256；在工作区选择“导入目录”或导入导出的 `.mkfx` 后，包会按固定 `packageId`／`revision` 保存进工程闭包。

| 包 | 导出 | 用途与限制 |
|---|---|---|
| [`CustomInk`](CustomInk/) | `custom-ink` 材质 | 未唱／描边的纯资源波纹材质；`unmasked`，不读取背景；透明输出继续执行 |
| [`LineGlitch`](LineGlitch/) | `line-glitch` 图 | 绑定整行的 `ColorOnly` 分片位移；不改变几何，不支持 Token、组图或 `ImageOnly` 映射 |
| [`GlassInk`](GlassInk/) | `glass-media`、`glass-backdrop` 材质 | 分别读取媒体背景和真实下层；透明输出跳过背景依赖并使用包内 solid fallback，同时留下诊断 |
| [`CursorTrail`](CursorTrail/) | `cursor-trail` 控制器 | 光标拖尾示例，支持 Jump／Bridge；见包内 README |
| [`VectorTrail`](VectorTrail/) | `vector-trail` 控制器 | 线段／带状拖尾和粒子输出；见包内 README |
| [`SpriteTrail`](SpriteTrail/) | `sprite-trail` 控制器 | 固定图片精灵拖尾；资源必须在包闭包内 |
| [`EventBurst`](EventBurst/) | `event-burst` 控制器 | 事件 Burst 粒子来源；预算和边界失败保持显式诊断 |
| [`WordEmphasis`](WordEmphasis/) | `word-emphasis` 控制器 | 文字变换与强调示例；脚本包仍按作者工作区的源码修订合同编译 |
| [`RepeatEmphasis`](RepeatEmphasis/) | `repeat-emphasis` 控制器 | 重复强调；绑定元素的已唱基线必须是 solid，不能用包材质或渐变猜测 |

## 使用边界

公开包使用 profile2、SDK 协议声明 1.0.0 和当前 shader profile。导入、验证、试样和导出都会重新检查文件闭包、shader／ABI、参数范围、实际目标和资源依赖；修改文件后先更新 manifest 摘要，再导入新的修订。保存工程、模板或 `.mkfx` 后可以删除原目录，正式会话只读取工程持有的固定闭包。

当前生产路径支持 TokenFamily／Line／TextMark 的 `ColorOnly` 图和元素／标记材质。组图、父捕获、owned overlay、跨输出计划、`ImageOnly`／`MappedWarp` 和复杂图映射仍是关闭门禁。公开包的自动回归覆盖目录与 `.mkfx` 闭包、物理重开、普通／透明 Preview 与 Export 同帧、透明回退、非法 shader／参数和 RepeatEmphasis 组合；0.40.2已通过R8完整作者工程成片、备份及中途失败／取消恢复自动验收；2026-10-06用户确认文字／粒子／资源效果均无问题，并接受本轮goal完成。当前记录及复查入口见[R8成片验收](../../docs/scripting-r8-acceptance.md)／roadmap §18.108。各包内README保留制作时的版本状态，当前完成度以该记录为准。
