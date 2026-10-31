# Package resource closure worker seam

本批建立 package 资源进入受管理 Worker 的最小私有接缝，服务于后续图片／视频／字体闭包扩展。它不新增公共 HTTP 路由，不接受客户端主机路径，也不修改 package export 或 B5 作者流程。

当前已实现的范围：

- Host 可以把已固定的 package 文件字节写入私有 staging 目录，生成带 package 身份、逻辑路径、role、size 和 SHA-256 的一次性 Worker grant。grant 的绝对路径只存在于 Worker 私有 IPC，公共 DTO、资源清单和 content URL 不携带它。
- Worker 以实际进程能力握手声明 `package-resource-closure-v1`，声明图片 `skia-codec-v1`、视频 `ffmpeg-video-metadata-v1`/`ffmpeg-video-frame-bgra-v1` 和字体 `skia-font-shape-v1`（provider identity 为 `skia-harfbuzz-v1`）。Worker 在读取前拒绝目录、符号链接、大小变化和 SHA 变化。
- 图片 role 使用正式 Skia codec 完整解码并报告宽高、编码格式、alpha 和有界解码字节。解码失败、预算超限及授权文件变化都有稳定错误码。
- 视频 role 只在 Worker 内受控调用 FFprobe/FFmpeg：报告有限容器/视频元数据，并读取时间 0 的一帧 BGRA 字节摘要；视频时长上限 30 分钟、首帧解码上限 16 MiB。字体 role 只从 grant 字节建立 Skia typeface，用固定文本经 HarfBuzz 整形并报告族名、字形数和 advance。两者都不接受客户端主机路径，也不发布 provider 或解码器对象。

Worker/Hosting 定向回归覆盖能力回执、真实 PNG 解码、真实 testsrc 视频元数据与首帧摘要、真实系统字体 provider/整形、变更后 grant 拒绝及 staging lease 创建／释放。该私有 seam 尚未接到连续预览、验证、导出或分析任务；视频只证明受控短帧读取，字体只证明固定文本整形，不等于完整背景媒体闭包、跨任务租约、跨平台像素等价或作者工作流已完成。下一批应在保持 grant/lease 边界的前提下接通具体调用方并补 fresh HTTP 证据。
