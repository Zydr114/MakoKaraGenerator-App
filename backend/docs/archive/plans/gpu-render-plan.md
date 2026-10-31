# 标准 GPU 渲染管线实施计划

状态：`dev/gpu-render` 本机实施与验收完成（2026-10-26），见[验收报告](../acceptance/gpu-render-acceptance-20261026.md)。

目标：在现有 Skia CPU 光栅管线之上接入可由本机真实运行的 Skia Ganesh/Vulkan 管线，并保持现有 `IRenderPipeline`、`RenderRequest`、`RenderFrame` 和 HTTP 合同不变。

## 1. 目标与验收边界

本批的完成条件是：同一套生产渲染代码可以显式选择 CPU 或 Vulkan GPU；GPU 路径在当前开发机上能够创建真实 Vulkan instance/device/queue、创建 GPU-backed Skia surface、执行主画布与中间表面绘制、flush/submit，并把最终图像读回现有的 premultiplied BGRA `RenderFrame`。静态图像、透明输出、背景、文字、图效果、捕获和组隔离必须通过同一设备的 surface factory 创建表面，不能只把最外层画布换成 GPU。

本批不修改公开 HTTP 路由、DTO、请求／响应语义或合同版本；产品版本和源码指纹由既有合同生成器刷新，不能保留失效的 manifest。这里不承诺跨平台 GPU 矩阵、性能提升、位级 CPU/GPU 一致、GPU 视频编码或 GPU compute。PNG/FFmpeg 仍消费 CPU 侧的最终 BGRA；CPU 后端继续作为显式可选路径和回归基线。

## 2. 固定技术选择

1. 使用 SkiaSharp 3.119.4 的 Ganesh GPU API，首个实际后端为 Vulkan。
2. 使用 `SkiaSharp.Vulkan.SharpVk` 与其匹配的 `SharpVk` 绑定创建 Vulkan instance/device/graphics queue，再由 `GRContext.CreateVulkan` 创建 Skia direct context。
3. Linux 上不要求用户修改系统库。对 SharpVk 默认查找 `libdl.so` 与本机只有 `libdl.so.2` 的差异，使用进程内 `DllImportResolver`/native loader 在首次 Vulkan 调用前映射到系统真实库；不得提交 `/usr/lib` 链接或依赖手工 root 操作。
4. 不引入独立 Vulkan 绘制器。文字、路径、shader、图合成和现有 Skia 语义继续由 `SkiaRenderPipeline` 执行，设备层只负责 context、surface、flush/submit、readback 和资源生命周期。

## 3. 设备层设计

新增内部设备抽象，至少包含：

- backend kind（CPU/Vulkan）；
- `CreateSurface(SKImageInfo, ...)` 和 GPU 中间表面创建；
- `FlushAndSubmit()`；
- `ReadPixels(SKSurface/SKImage, destination, rowBytes)`；
- 能力/设备诊断（vendor、device、driver、API 版本）；
- 可重复、幂等的 `Dispose`。

CPU 实现使用 `SKSurface.Create(SKImageInfo)` 创建 raster surface；Vulkan 实现持有 Vulkan instance/device/queue 和 SkiaSharp 3.119.4 的 `GRContext`，由 Skia 管理 GPU surface 的底层存储。两者最终都读回 `ArrayPool<byte>` 租借数组。设备抽象不泄漏到 `Rendering.Abstractions`，只存在于 `Rendering.Skia` 和 Runtime 组合根。

`SkiaRenderPipeline` 注入借用的 device，主 `Render` 不再直接调用面向 CPU buffer 的 `SKSurface.Create(info, pointer, stride)`。GPU 路径先创建 GPU surface，按现有绘制顺序绘制，flush/submit 后用 `ReadPixels` 读回池化 BGRA，再构造现有 `RenderFrame`。读回失败、context 丢失或 surface 创建失败都转成明确的渲染异常并保留 backend/阶段信息。

## 4. 全链路改造范围

必须把同一 device 传到以下路径，避免出现 GPU 主画布混用 CPU 中间表面的隐式回退：

- `SkiaRenderPipeline` 主画布与背景/歌词/overlay 绘制；
- `SkiaRenderPipeline.SceneSurfaces` 的图效果表面；
- capture、group isolation、opacity 及 material capture 表面；
- `CompiledImageGraph` 的输入、输出和组合临时表面；
- sprite/image 资源上传到 GPU 的路径（CPU `SKBitmap` 输入可以保留，绘制时上传）；
- 颜色空间、透明度和 premultiplied BGRA 的最终 readback。

GPU `SKImage` 不得调用 `PeekPixels()` 作为验证或取数入口。需要 CPU 验证的颜色数据统一经过显式 `ReadPixels`；只在 CPU raster surface 上允许使用现有的直接像素访问。

## 5. 选择、失败与生命周期

内部配置通过 `MAKO_RENDER_BACKEND=cpu|vulkan` 选择；默认保持 `cpu`，GPU 验收通过显式选择 `vulkan`。选择值非法直接拒绝。选择 Vulkan 后，设备初始化失败、Vulkan 不可用或任一路径不支持 GPU 时必须失败并报告诊断，禁止偷偷回落 CPU。

一个 `ProjectRenderSession` 持有一个 device。几何 provider 与最终像素 renderer 使用同一 device，但继续拥有各自的文字布局缓存。session gate 继续串行保护 Runtime、Skia native objects、GPU context 和 readback；不在本批宣称同一 session 帧内并行。实际释放顺序为 source、renderer、resources、fixed-image租约、inputs、native graphs、budget，最后释放GPU device；创建中途失败同样清理已建立对象并保留原始异常。

## 6. 测试与真实验收

### 6.1 静态与定向回归

- CPU 现有 Rendering/Runtime/Backend 测试全部保持通过，确认默认 backend 行为未变。
- 新增 device factory、Vulkan loader、surface/readback、dispose/初始化失败测试；测试不要求机器有 GPU 的部分使用 fake/CPU 实现。
- 新增 GPU 条件测试：设备 smoke 使用 `MAKO_GPU_SMOKE=1`；生产管线回归通过 `MAKO_RENDER_BACKEND=vulkan` 选择后端，覆盖纯色、透明、背景、文字、图效果、中间表面、capture/group isolation 和多次 flush/readback。
- 对 GPU 输出与 CPU 输出做结构和像素容差比较，透明 alpha、BGRA 通道顺序和预乘语义单独断言；不把驱动差异误报为位级一致。

### 6.2 本机真实 GPU smoke

在当前 AMD Radeon 880M、Mesa/RADV Vulkan 环境上运行独立 smoke：打印设备信息，创建 context 与 GPU surface，绘制、flush/submit、readback，验证非空像素与透明像素；随后通过生产 `SkiaRenderPipeline` 渲染普通/透明静态帧，并执行至少一条图效果或捕获路径。日志必须记录 backend、设备、驱动、surface 尺寸、readback SHA 和退出码。

### 6.3 Worker fresh acceptance

以当前源码重新生成 self-contained Server/Worker，显式设置 GPU backend，执行一个新的证据目录：Worker open/render/close、普通/透明静态帧、PNG/裸 BGRA 长度和 SHA、取消/截止时间/关闭排水、Server 与 Worker 进程退出及凭证扫描。HTTP 只验证现有合同上的行为，不把 GPU 选择扩展成公开 API。

## 7. 文档与提交纪律

实现完成后更新渲染架构文档，区分 CPU/GPU 设备、readback、失败语义和当前有限验收边界；同步说明 GPU 仍未形成跨平台/性能承诺。每个独立步骤单独提交并推送：计划、依赖/设备层、管线改造、测试与文档、fresh 验收证据。任何修复都先定向验证再提交；不得提交现有未跟踪的 `tools/backend-project-image-export-probe/`。

## 8. 主代理审查清单

主代理在子代理交付后逐项检查：

1. 是否所有主/中间 surface 都由同一 device 创建，还是只替换了顶层 surface；
2. 是否存在 `PeekPixels()` 访问 GPU image；
3. 是否正确处理 flush/submit、readback、BGRA、premultiplied alpha 和透明背景；
4. GPU 初始化失败是否显式失败，是否错误地静默 fallback；
5. session/device/resource 的拥有者、释放顺序和异常路径是否完整；
6. 几何 pipeline 与最终 pipeline 是否共享 device，session gate 是否仍然有效；
7. CPU 默认路径和现有 HTTP 合同是否无意改变；
8. 定向测试、真实本机 Vulkan smoke、fresh Worker 运行、进程排水和 Git 提交/远程状态是否都有证据。

## 9. 实施审查补充（2026-10-26）

- 独立图调用与生产会话分别验证：生产会话共享一个 device；独立 graph／pipeline 创建的图像跨 context 时必须显式读回再上传，不能直接把外部 GPU texture 交给另一 context。
- 产物拥有权覆盖 `ImageGraphResult`、composition payload、独立 `SkiaGraphSurface` 与捕获产物；创建者关闭后，仍存活的自有产物不能失去其 GPU device。
- 源材质审查发现 raw 区域直接绘制与 fallback 的 `SaveLayer` 路径在 Vulkan 上可能采用不同字形栅格原点。修复应统一绘制路径，保留零覆盖区域严格禁止输出的校验；不得用放宽 alpha 容差掩盖几何错位。
- 验收必须在实际 Worker 中确认选用硬件驱动和 render node；仅继承 `MAKO_RENDER_BACKEND=vulkan` 或看见 Vulkan loader 不足以证明实际 GPU 工作。
- CPU golden 保留其原 CPU 语义；GPU 的几何、非空像素、透明度、预乘与效果贡献独立验证。所有未通过的广泛 GPU 回归须逐项分类，不能统一称为驱动差异。

## 10. 实施与验收结果

产品0.105.0／合同1.38.1，最终生产基线`8fd0672`，匹配发布与合同提交`ca3b495`。完整CPU solution6159/6159、显式Vulkan Rendering1055/1055、Vulkan后端/Runtime集成513/513及真实设备smoke1/1通过，各范围不重复累加。fresh HTTP2376请求／18405断言、52组PNG对比、8个短片168帧解码、6组预览与3组导出控制通过；Server退出0、129个观察进程身份全部排水。完整失败归属、硬件证明、原始证据、使用方式与限制见[验收报告](../acceptance/gpu-render-acceptance-20261026.md)。
