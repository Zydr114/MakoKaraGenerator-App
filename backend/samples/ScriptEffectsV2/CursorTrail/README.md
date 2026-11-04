# SDK2 连续播放头粒子

包身份 `makokara.v2.cursor-trail`，入口 `CursorTrail`，导出 `cursor-trail`。
`Declare` 固定歌曲时间速率轨；`Evaluate` 不推进任何状态。要求 Vulkan/GPU。

- `rateShape=fixed` 使用常速；`steps` 在 `splitUs` 切换两段常速；`linear` 在完整真实源时间窗内从 `rate` 线性变为 `finalRate`。
- 同轨道、同真实 Binding 的相接词语保留小数配额；真实 gap、停用与 reload 由宿主重新分 episode。没有 n=0 粒子，出生时间向上取到首次达到整数配额的微秒。
- `transition=bridge` 只连接真实合法后继，在 gap 末段从前词 `EndUs-1` 的最终文字锚点飞向后词 `StartUs`；`jump` 不生成桥接粒子。
- `lifeUs` 为 1～5,000,000 µs，死亡边界不显示。`trailUs` 使用宿主解析拖尾，不积累历史帧。最终锚点在全部文字实例合成后取得。

默认值：60/s、600,000 µs 寿命、6 px、0 散布、120,000 µs 拖尾、Jump。
`manifest.json` 的文件 SHA-256 覆盖这里和实际源码的确切 UTF-8 字节；修改任一文件后须重算。
本目录是 M08 实施样例；代码存在不代表编译、像素、跨平台或性能验收通过。
