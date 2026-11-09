# M12 测速用测试效果包

专为定位材质／图路径开销而写的纯资源 package3 包，不是样例，也不提供给用户。每包一个导出，
`scripts/gpu-preview-p3.mjs --v2-samples <目录名>` 可直接取用；`--v2-params blurPx=8` 在打包时改参数默认值。
`PerfFixturePackageTests` 检查准入、原生编译与最小像素。修改文件后须更新各自 `manifest.json` 中的 SHA-256。

| 目录 | 导出 | 内容 | 用来比较 |
|---|---|---|---|
| MaterialConstant | 材质 `constant` | 无输入，输出参数色；`unmasked`，由宿主乘区域 | 材质路径本身的基础开销与表面占用 |
| MaterialCoverage | 材质 `constant` 同色＋`coverage` | 只读目标区域覆盖（`materialRegion`），作者自乘覆盖；`masked` | 加一个捕获输入（覆盖模式也随之不同） |
| MaterialBackdrop | 材质 `backdrop` | 覆盖＋`BackdropBelow` 经 Blur（`blurPx` 0～8，默认 0）后按比例着色；`masked` | 与区域覆盖比读下层；σ=0 与 σ=8 比模糊 |
| MaterialMediaRequired | 材质 `media-required` | 同上，背景改为必需的 `BackgroundMedia` | 有／无图片背景时的正常路径与缺失行为 |
| MaterialMediaOptional | 材质 `media-optional` | 同上，`BackgroundMedia` 为可选 | 缺失时目前报 `GraphOptionalInputPolicyPending` |
| GraphPassthrough | 图 `passthrough` | 整行原色经恒等着色器输出，不外扩 | 图路径的基础开销 |
| GraphBlur | 图 `blur` | 同上，恒等着色器前加 Blur（`blurPx` 0～8，默认 0） | 图路径上模糊的开销 |

- 材质参数色与着色默认 `(0.95, 0.55, 0.2, 1)`，回退纯色 `#3366CCFF`，两者不同，便于区分是否走了回退。
- Blur 在 σ=0 时仍会分配两张临时表面，所以「下层 σ=0」与「区域覆盖」之差是读下层加一次空模糊。
- 三个背景类材质共用同一个着色器，只差背景来源与可用性。
