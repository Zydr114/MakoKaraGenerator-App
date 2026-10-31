# Skia GPU 后端架构与验收边界

本文记录 `Rendering.Skia` 当前的显式 CPU／Vulkan 设备选择和共享设备接入。它补充[Skia 绘制与一帧输出调查](render-skia-output.md)；后者是早期 CPU 基线调查，不能覆盖本实现新增的 GPU 路径。

## 0.107.0 结构优化与监控

内部同设备、仍存活的自有 BGRA8888/Premul/sRGB 图像，在目标未请求颜色空间转换且尺寸相同时，保留 Flush/Submit 后直接读取到颜色匹配目标，省去 raster 中间产物。外借图像、未知颜色空间与转换请求保留原路径；BottomLeft、完整像素验证与最终 CPU 可见帧出口不变。

每次行或标记绘制至多保留一张 16 MiB 的成功无输入作者图。图对象固定包修订和设备，完整帧上下文、类型化参数与 uniform 按值匹配；不复用有声明输入、coverage 或诊断的结果。每个目标仍独立进行覆盖、透明度、预算和错误归属检查。缓存占用真实存储预算，预算压力先在账本锁外释放缓存，再回到锁内进行原子准入；帧/对象结束及失败释放结果。

Runtime、脚本、Skia 与 Worker 共用有界性能采集器，默认每 10 秒写摘要，正常退出尝试输出尾摘要；Hosting 将校验后的 Worker stderr 性能事件作为不可信来源转入 Server 日志。阶段为包含嵌套工作的宿主 wall 时间；进程 CPU、工作集、分配及 GC 不表示 GPU 硬件时间、显存或脚本独占资源。配置、脚本归属及丢弃语义见[监控说明](../backend-performance-monitoring.md)，验收按[实施计划](../archive/plans/backend-gpu-structural-performance-optimization-plan-20261028.md)分层记录。

## 设备选择

设备由 `SkiaRenderBackendOptions.FromEnvironment()` 读取 `MAKO_RENDER_BACKEND`：

| 值 | 行为 |
| --- | --- |
| 未设置或 `cpu` | 创建 `CpuSkiaRenderDevice`，保持 raster surface 和既有 CPU 行为 |
| `vulkan` | 创建 `VulkanSkiaRenderDevice`，使用 SharpVk 建立 Vulkan instance、physical device、graphics queue、device 和 Skia Ganesh `GRContext` |
| 其他值 | 立即抛出配置错误 |

Vulkan 是显式选择。Vulkan instance、设备、队列、Skia context 或 GPU surface 创建失败时，设备工厂抛出带有原始异常的初始化错误，不静默改用 CPU。未设置环境变量时仍选择 CPU，因此已有调用方不会因为主机没有 Vulkan 而改变默认行为。

设备选择按独立、集成、虚拟 GPU 排序，并要求 graphics queue；CPU/软件与未知类型明确拒绝。当前实际支持范围是本机 Linux/RADV；虚拟 GPU 的候选分类不代表虚拟化环境已验收。该选择在会话创建时读取，运行中的会话保持原设备。

## 共享设备和表面

`ProjectRenderSessionFactory` 为一个 `ProjectRenderSession` 创建一个设备，并把同一实例借给：

- 最终 `SkiaRenderPipeline` 的主画布；
- `CompiledPackageGraphExports.CompileWithDevice` 创建的包图和材质图；
- 图节点 raster、场景捕获、组隔离和图像效果中间表面；
- 最终帧的显式 GPU surface。

这样同一会话中的主画布和中间图像都使用同一个 Ganesh context。`SkiaRenderPipeline` 和借用该设备的 `CompiledImageGraph` 不会重复释放设备；会话释放时先结束 source、renderer、固定图和包图，再释放共享设备。

独立创建的 pipeline/graph 拥有自己的设备。返回的图结果、组合产物及 capture surface 持有设备租约，创建者释放后仍可读取，最后一个产物释放才销毁设备。外部借用图像继续由调用者维持生命周期。不同设备的图像在传递给目标 canvas/shader 前显式转为 raster 图像；同设备图像保持原纹理。session gate 保护完整 prepare/render/readback 生命周期，不承诺同一 context 的并行绘制。

CPU 设备仍通过 `SKSurface.Create(SKImageInfo)` 创建 raster surface。Vulkan 设备通过 `SKSurface.Create(GRContext, true, SKImageInfo)` 创建 GPU surface。两种设备都对外提供相同的 `CreateSurface`、`FlushAndSubmit` 和 `ReadPixels` 接口，生产像素格式仍为 `BGRA8888`、premultiplied alpha。

## 提交与 readback

GPU 绘制结束后，设备执行 surface flush、`GRContext.Flush()` 和同步 `Submit(true)`。需要把图像交给中立 API、诊断或图效果验证时，生产代码调用设备的显式 `ReadPixels`；不依赖 `SKImage.PeekPixels()` 是否能直接暴露 GPU 后端存储。

主帧 readback 写入 `RenderFrame` 使用的租借数组。图节点和捕获表面在固定为 `SKImage` 后按同一设备执行 readback，随后才释放中间表面和预算租约。GPU readback 失败会报告后端和设备信息，并终止本次渲染。

## 0.106.1 内部验证与临时缓冲

`dev/performance-optimization` 在不改变公开后端选择的前提下，为内部拥有的不可变图像记录成功预乘验证。同设备的非拥有别名共享该事实，拥有者释放立即使其失效；外借输入、跨设备和新图不复用先前事实，像素验证失败或验证阶段取消不登记成功。结构、颜色空间、几何、尺寸和预算验证继续执行；事实不持有额外图像、设备或预算租约。

没有 ruby 注解的原始 ruby 颜色部件仍执行完整捕获、绘制检查、表面创建和预算预留，仅跳过可证明为空的 alpha 判空读回。作者图执行和全域非法输出检查保持。单对象 alpha 临时数组最多保留16MiB；独立图验证读回池最多保留3个数组、单个≤16MiB、合计≤32MiB，超限临时分配、异常归还、扫描只访问有效长度。这些是各自数组保留上限，不是整个进程或GPU内存预算。

实现、语义回归、匹配HTTP和独立性能证据见[本批验收](../archive/acceptance/backend-gpu-performance-optimization-acceptance-20261027.md)。同步提交、同帧作者图共享、GPU全域小结果归约和ROI尚未调整。

## Vulkan interop 细节

SharpVk 的 managed handle 在传给 Skia 的 procedure callback 前转换为 raw `VkInstance`／`VkDevice` handle；`GRVkExtensions.Create` 和 `GRSharpVkBackendContext.GetProcedureAddress` 使用同一个 raw callback。没有 instance 或 device 的全局查询走 `libvulkan.so.1` 导出的 `vkGetInstanceProcAddr`。当前 context 的 `MaxAPIVersion` 固定为 Vulkan 1.0，避免把驱动报告的更高版本误当作 Skia 初始化合同。

设备对象、context、extensions、procedure delegate 和 Vulkan 动态库句柄均由设备对象保留到 `Dispose`。释放前调用 `WaitIdle`，再释放 Skia context、backend context、extensions、device、instance 和动态库；初始化中途失败清理已建立对象。某步失败不会中断后续清理；保留首个异常并附加后续清理异常。驱动版本按 vendor-defined 原始位报告，API 版本与所申请的 Vulkan 1.0 分别记录。

Linux loader 以线程安全 `Lazy` 安装 SharpVk 的 `DllImportResolver`，把其 `libdl.so` 请求映射到系统 `libdl.so.2`；不需要写系统目录或创建 root 链接。

## 歌词分区的 GPU 裁切

Ganesh 的非抗锯齿 path `Intersect`／`Difference` 在半像素边界和不同 `SaveLayer` 结构下并非始终得到互补像素。真实回归曾出现应为83的描边 alpha 变成139，以及部件重组与材质回退同一位置重复覆盖。GPU 路径将已唱区域按当前 `TotalMatrix` 转到设备坐标，以 `SKRegion.SetPath` 在设备 clip bounds 内固定整数像素集合，再让两侧分别 intersect/difference 这个集合。CPU 保留原裁切路径；填充、描边、阴影和材质沿同一分区规则绘制。

独立最小复现另观察到半像素边界空隙；区域方案同时避免重复覆盖与空隙。该修复保持原 alpha 断言和 CPU 历史图像，不通过放宽容差或更换旧 golden 接受缺陷。

## 验收入口与使用

本机 Linux 的条件 smoke 测试位于 [`VulkanRenderDeviceSmokeTests`](../../tests/MakoKaraGenerator.Rendering.Tests/VulkanRenderDeviceSmokeTests.cs)。它只有在 `MAKO_GPU_SMOKE=1` 时执行，命令为：

```bash
MAKO_GPU_SMOKE=1 VK_DRIVER_FILES=/usr/share/vulkan/icd.d/radeon_icd.json dotnet test \
  tests/MakoKaraGenerator.Rendering.Tests/MakoKaraGenerator.Rendering.Tests.csproj \
  --no-restore -m:1 \
  --filter FullyQualifiedName~VulkanRenderDeviceSmokeTests
```

该测试覆盖真实 surface 创建、清屏和绘制、flush／submit、BGRA readback、角点与中心像素断言以及设备释放。2026-10-26 最终运行通过 `1/1`；设备为 AMD Radeon 880M Graphics（RADV STRIX1），Mesa `26.2.5-arch3.1`，设备 Vulkan API `1.4.354`。最终完整 CPU solution 为 `6159/6159`；Rendering 在显式 Vulkan 环境下为 `1055/1055`，其中历史 CPU golden 显式选择 CPU；Vulkan Runtime/Backend.Rendering/Backend.Rendering.Skia/Worker 集成为 `513/513`。构建零警告、零错误，源码输入无漂移。原始失败、修复、最终源码、匹配发布与实际 HTTP 证据统一见[验收报告](../archive/acceptance/gpu-render-acceptance-20261026.md)，不同范围不重复计数。

使用[匹配发布入口](../../tools/backend-web-host/README.md)准备完整 Server/Worker 后，在启动服务时设置 `MAKO_RENDER_BACKEND=vulkan`。本机可同时设置 `VK_DRIVER_FILES=/usr/share/vulkan/icd.d/radeon_icd.json` 固定 RADV；该路径属于本机驱动环境，其他硬件不能照搬。已有 CPU 使用方式保持，显式 `MAKO_RENDER_BACKEND=cpu` 可切回。

真实 HTTP 的复现步骤在[GPU 探针说明](../../tools/backend-gpu-render-probe/README.md)。它检查同一 Worker 身份的环境选择、Vulkan/硬件 ICD 映射与 DRM render node，避免将环境变量配置等同于实际硬件运行。

本次结论限定当前 Linux 主机。没有 Windows、macOS、其他驱动或软件 Vulkan 兼容性、CPU/GPU 逐像素等价、吞吐、延迟、显存或长片性能结论。媒体解码、文字布局与最终读回仍可使用 CPU；GPU 渲染不表示 GPU 视频编码。
