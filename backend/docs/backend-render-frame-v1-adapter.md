# render-frame-v1 同进程适配器

`RenderFrameV1SessionFactory` 是 B3-C 的第一条真实渲染接缝。它位于
`Backend.Hosting`，接收任何 `IRenderFrameSessionFactory`；当前同进程组合根传入
`Runtime.Hosting.ProjectRenderSessionFactory`，后续 Worker 代理可以复用同一接口。

适配器在创建会话时固定 `ProjectSnapshot` 引用。每次 `RenderAsync` 必须引用同一快照，
并以 `RenderRequest.ResolveTarget()` 取得目标尺寸；宽度、高度和 `width × height × 4`
字节数在调用底层会话前通过 `RenderFrameV1Limits` 检查。底层返回后还会核对目标、时刻、
BGRA 8888 预乘格式和像素长度。越界、过期快照或不符合输出合同的帧都会被拒绝；若底层
已经返回帧，拒绝路径先释放它。

调用令牌、请求自带的 `RenderRequest.CancellationToken` 和底层会话共享同一有界取消链。
会话内部串行化渲染与释放，`DisposeAsync` 等待在途调用到安全点后再释放底层会话。
成功返回的 `RenderFrame` 所有权转移给调用方，调用方必须在消费后释放；适配器不会缓存
像素，也不把池缓冲区越过帧生命周期传给订阅者。

本切片只建立同进程像素接缝，不编码 PNG、不创建句柄、不改 Worker echo 协议、不提供
HTTP 路由。真实 `Runtime.Hosting` 静态快照路径由
`RenderFrameV1RuntimeIntegrationTests` 覆盖；跨进程输入与二进制传输沿用后续 B3 批次。
