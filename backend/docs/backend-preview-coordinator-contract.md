# 中立预览协调器接缝

`BackendPreviewCoordinator` 是 B3 的第一条 Hosting 接缝。它位于
`MakoKaraGenerator.Backend.Hosting`，只依赖 `Rendering.Abstractions` 的
`IRenderFrameSessionFactory`／`IRenderFrameSession`，不加载 Avalonia、SukiUI 或
`TechnicalSpike.App`。桌面应用仍可继续使用自己的适配器；后端服务和未来 Worker
代理可以实现同一套会话工厂，而不把窗口或位图类型带入 Hosting。

每个 `BackendPreviewRequest` 必须携带：

- `PreviewGeneration`：非负的客户端预览代次。寻址、尺寸、透明模式、质量或换工程等输入变化应提升代次。
- `SourceRevision`：固定源的不透明修订字符串。即使快照引用暂时相同，修订变化也会使旧会话和旧帧失效。
- `RenderRequest`：不可变快照、时间和目标设置。播放头向前移动可以复用同一代次。

协调器每个实例只有一个执行中的会话请求和一个最新待处理槽。向前播放不会取消在途帧；待处理槽只保留最新时间。代次、来源修订、快照、工厂或静态渲染选项改变时取消当前工作并释放旧会话。每个提交得到单调 `RequestSequence`，事件只在以下条件成立时发布：

1. 代次与来源修订仍是当前值；
2. 快照、工厂和静态渲染选项仍匹配；
3. 对于完成帧，最新请求时间不早于实际帧时间。

因此换代次／换输入后的迟到像素和错误不会覆盖当前结果；同代次向前播放允许较早的在途帧先完成。若调用方在同一代次回退时间，应提升代次以表达 seek；协调器仍会拒绝实际倒退的迟到帧。

`Updated` 回调在内部闸门释放后执行。订阅者可以同步提交新请求而不会重新进入协调器锁；回调异常不会终止渲染工作。协调器复制 `RenderFrame` 的裸像素和诊断后立即释放底层帧，事件接收者不拥有池缓冲区或原生资源。

这一接缝目前只负责进程内排队、生命周期和旧帧门禁。它没有新增 HTTP 路由、PNG 句柄、诊断分页、Worker 进程终止或正式预览验收；这些仍属于 B3 后续批次。`BackendPreviewCoordinatorTests` 固定验证 latest 槽、显式代次／来源、旧帧拒绝、失败缓存失效和锁外事件。
