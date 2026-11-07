# SDK v2 调试叠加层与成本视图：设计

日期：2026-11-07。属于阶段一 M11 的遗留项（[M11证据 §7](script-sdk-render-m11-evidence.md)）。要求来自
[主计划 §8](script-sdk-v2-render-pipeline-plan.md)、[验收表 M11](script-sdk-v2-render-acceptance.md)
和[作者流程笔记 §4](script-sdk-v2-m11-author-notes.md)。验收按 M10／M11 的放宽口径：先保证功能接通、
真实可用，只排除数量级的性能崩坏。

## 1. 目标与边界

作者在样式库试样画布上打开“调试叠加层”，可以在当前帧上看到：

- **字簇**：每个基础字的真实整形单元。能独立变形的字簇画实线；跨元素、不能拆开的字簇画红色虚线，
  并标出原因代码。
- **Ruby 归属**：每段注音的墨迹框，并用连线指向它所属的基础字。
- **播放头**：每条演唱光标在最终几何上的位置、进度和进度来源（字／行）。
- **粒子出生锚点**：本帧出生窗内各粒子的出生位置，以及存活数和拖尾数。
- **效果范围**：每个效果实例绑定的源行，在画面上画出这些行的最终外框，并标注包和导出名。

“成本”页签列出同一帧的耗时与工作量：
- Worker 内的准备、脚本、图、材质、粒子、栅格、验证、读回各阶段耗时；
- 脚本调用次数，以及按实例和阶段拆开的脚本耗时；
- 锚点补算的工作量与 memo 命中；
- 粒子出生、存活和拖尾数；读回字节数；进程分配量与 GC 次数；
- 会话打开耗时（含编译产物装载与 Declare）；
- Host 侧的渲染往返和 PNG 编码耗时。

不做：GPU 共享纹理流的叠加层、正式导出和验证任务的成本、跨帧聚合统计。也不改作者输出、hit-test、
工程内容、缓存指纹和导出路径。

## 2. 决定

| 编号 | 决定 | 理由 |
| --- | --- | --- |
| I1 | 按帧请求开启：`PreviewFrameRequest.inspect=true`，省略或 false 时完全不采集 | 默认关闭，关闭时没有额外开销；不新增会话状态，也不改既有请求的语义 |
| I2 | 后端对所有静态预览会话都接受 `inspect`；前端只在样式库试样画布（作者草稿与模板试样）提供开关 | 实现统一，不按预览种类分支；入口先放在作者流程里 |
| I3 | Worker 在同一次 Prepare＋Render 后，从同一个 `PreparedRenderFrame` 生成投影，随帧回执一起返回 | 与像素属于同一次输入、代次、时刻和修订；不另跑一次准备，也不在 Host 重新整形 |
| I4 | Host 把投影的 JSON 字节和帧放在同一个保留条目里，寿命和租约与帧相同 | 帧过期时投影一起失效；不会出现“新帧配旧投影” |
| I5 | 新增 `GET /api/v1/preview-frames/{id}/inspection`；帧元数据只多一个可选的 `inspectionUrl` | 帧元数据会随 SSE 推送，保持小；投影单独读取 |
| I6 | 叠加层只在前端用 SVG 绘制在图片上方，PNG 不变 | 像素、导出与关闭时完全一致，叠加层不可能进入正式导出 |
| I7 | 成本按帧采集：`PerformanceTelemetry` 新增不依赖全局收集器的单帧捕获模式；全局日志模式保持原样 | 现有遥测默认关闭，只写日志；单帧捕获不进入日志，也不影响原有采样 |

## 3. 投影内容与预算

坐标统一用工程画布坐标（`canvas.width×height`），前端按图片的实际显示尺寸缩放。四边形用 4 个点表示，
因为 Warp 和旋转后不一定是轴对齐矩形。投影只包含身份、数值和代码，不含作者对象、句柄、本机路径或凭证。

```text
PreviewFrameInspectionResponse
  frameId, previewId, previewGeneration, sourceRevision, timeUs, width, height, renderBackend
  canvas {width, height}
  tokens[]     tokenId, bindingId, lineId, ink(quad|null), independent, diagnosticCode?
               clusters[] unitIndex, ownerTokenIds[], independent, ink(quad|null)
               ruby[]     rubyId, ink(quad|null)
  lines[]      lineId, bindingId, bounds(quad|null)
  cursors[]    lineId, bindingId, tokenId?, progress, progressSource, point, start, end
  particles[]  instanceId, births, alive, trailSamples, birthPoints[]（有上限）
  effects[]    instanceId, packageId?, packageRevision?, exportId?, sdk(1|2), lineIds[], scriptId?
  cost         wallMs, prepareMs, scriptMs, graphMs, materialMs, particleMs, rasterMs, validationMs, readbackMs,
               scriptCalls, readbackBytes, allocatedBytes, gc0/1/2, scripts[] {scriptId, stage, calls, wallMs, maxMs},
               scriptOverflow, anchors {uniqueTimes, textCalls, futureEndpoints, memoHits, memoMisses},
               sessionOpenMs?, host {renderRoundTripMs, encodeMs, pngBytes}
  truncated {tokens, clusters, lines, cursors, birthPoints, effects}（各自被省略的数量）
```

预算：tokens 2000、每个 token 的 clusters 32、lines 256、cursors 64、每个实例的 birthPoints 512、
effects 64，scripts 沿用遥测的 16 项。序列化后最大 256 KiB，超出就从末尾整类截断并计入 `truncated`，
不会无声地只画一半。

成本口径（写进页签说明）：
- 各阶段是 Host 墙钟的包含时间，不能相加当作总耗时，也不是 GPU 硬件时间。
- 分配量是全进程增量，不是某个作者的精确分配。
- 脚本只计 Declare、Advance、OnEvent 等阶段，热路径 Evaluate 不单独计时，与现有遥测一致。

## 4. 数据流

1. 前端在 `frame-requests` 中带 `inspect:true`。Host 把它写进 `RenderRequest.Inspect`。
2. Host 到 Worker 的二进制帧请求用第 7 字节（原保留位，值必须为 0）作为检查标志。Host 与 Worker
   总是同一发布，不存在混合版本。
3. Worker 的 `ProjectRenderSession` 在检查帧上开启单帧捕获，Render 后用 `RenderFrameInspection.Build`
   从 `PreparedRenderFrame` 和实例目录（`IEvaluatedFrameInspectionSource`）生成投影，回执多一个可选字段。
4. Host 解码并校验预算，测量往返和 PNG 编码耗时，与帧一起发布。帧元数据带 `inspectionUrl`。
5. 前端读到带 `inspectionUrl` 的帧后再读取投影，和帧图片一起保存；关闭开关后不再请求，也不显示旧投影。

## 5. 前端

- 试样画布工具条新增两个开关：“叠加层”和“成本”，默认关闭，状态记在本机。任一开关打开时，帧请求都带
  `inspect:true`。
- 叠加层用 SVG 盖在图片上方（`pointer-events:none`），图例可以按类别隐藏。失败显示旧帧时不画叠加层，
  避免把旧帧的几何说成当前帧。
- 成本面板放在画布下方，左边是阶段耗时条，右边是计数表；脚本行把 `scriptId` 映射回实例和包名。
  Worker 与 Host 两段分开显示。

## 6. 合同

HTTP 新增 1 个路径和 1 个方法；`PreviewFrameRequest` 多一个可选的 `inspect`，`PreviewFrameMetadataDto`
多一个可选的 `inspectionUrl`，再加上新的响应 DTO。原请求和响应都兼容，合同升到 **1.48.0**，产品升到
0.112.38（沿用 0.112 系列逐片升补丁号的做法）。投影没有开启时返回 404 `PreviewInspectionUnavailable`，帧过期时
沿用 `PreviewFrameExpired`。

## 7. 验证

- 单元测试：投影构建（字簇、Ruby、光标、截断）；帧请求编码中检查位的往返和拒绝；单帧捕获。
- 真实链路：fresh publish 后，隔离 profile 并绑定 loopback，对一个 v2 样例分别请求开启和关闭检查的帧。
  两帧 PNG 的 SHA 必须一致，投影字段非空，关闭时读取投影返回 404。
- 浏览器：真实 Chrome 打开作者草稿试样，开关叠加层和成本，截图并确认页面没有异常。
- 性能只看数量级：开启检查后单帧耗时不能成倍增加。
