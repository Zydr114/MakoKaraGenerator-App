# M13 补充：Aorist 视频背景复制与导出计时

日期：2026-11-14。范围：优化视频背景不可变快照复制，补齐必要阶段计时；不启动 M14。

## 问题与边界

较新的 `Aorist - nayuta & ARForest - 1` 为 1080p24。0.112.90 的真实导出记录为 6081 帧，
平均渲染调用约 72.9 ms；GPU 占用百分比不能区分 CPU 准备、绘制、同步或编码瓶颈。
独立复制真实 1080p BGRA 背景平均约 31.3 ms：当前 SkiaSharp `SKBitmap.Copy()` 经 CPU shader
绘制整图，背景每帧不可变复制使用了这条路径。调查与原始短段结果在
`.local/investigation-export-perf-20261114/report.md`，属于优化前证据。

本轮不引入硬件编码、异步回读、多帧渲染或新的用户设置，不改变预览控制节奏和 HTTP 形状。
用户工程不改；运行验收使用隔离副本。测试仅补复制语义和遥测汇总必要覆盖，复用已有场景、导出测试。

## 实现

- 产品 0.112.91。背景快照按原始像素复制，有 padding 时逐行复制有效字节，保留颜色类型、
  alpha 类型和色彩空间；快照独立分配、不可变、随帧释放，来源复用不影响已固定的帧。
  避免旧 shader 复制的额外预乘往返与半透明量化；不把可变视频位图直接标为 immutable。
- `BackgroundAcquireMs`、`BackgroundCopyMs`、`DrawMs`、`GpuSubmitMs` 补齐取帧、不可变复制、
  绘制和显式提交墙钟。既有 `ReadbackMs` 含义不变；嵌套阶段不可简单相加，提交计时不是 GPU 硬件时间。
- `ExportQueueWaitCalls/Ms` 独立统计编码队列入队调用，包括失败/取消；不增加渲染帧数或渲染 wall。
  保持有界队列、采样与限流，Hosting 校验和重编码新字段；细则见
  [性能监控](../../backend/docs/backend-performance-monitoring.md)。

## 验证记录

实现完整构建零警告、零错误。定向 Rendering 34、Export 94、Hosting telemetry drain 4 项通过。
仅新增 1 个 4-case 复制 theory、1 个阶段汇总 fact、1 个 2-case 编码队列 theory；另扩展既有环境模式
与日志转发测试。旧 Copy 变异在半透明的 2 cases 失败，恢复直接复制后 4 cases 全通过。
首轮完整构建因测试的 Assert.Throws 重载选择错误失败，改为明确 Action 后通过，不计为测试运行。
证据位于 `.local/fix-export-perf-20261114/`。

匹配发布、真实像素/性能与短段 HTTP/桌面验收待后续记录；不能用上述编译/单元结果代替。
