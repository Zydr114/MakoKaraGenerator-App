# B3-C Runtime 帧适配与受保护读取

`RenderFrameV1SessionFactory` 将真实 `IRenderFrameSessionFactory` 接入 `Backend.Hosting`。它固定 `ProjectSnapshot` 引用，检查目标尺寸和 `width × height × 4` 预算，将请求令牌与帧请求令牌合并，并串行化渲染和释放。成功返回的 `RenderFrame` 由调用方拥有；取消、输出校验失败或创建失败都会释放底层帧。

`PreviewFrameHandleStore` 只接受经过 `PreviewTransportValidation`、`PreviewFrameContract` 和 PNG signature 校验的字节，按 clientId、previewId、generation、sourceRevision 和 requestSeq 隔离，保留帧数量与字节数有界。Server 只提供读取路由：ETag、单 Range、If-Range、HEAD 和 TTL 均在读取时重新检查，句柄不提供任意路径访问。

`PreviewDiagnosticsStore` 只保存绑定当前 client、预览代次和来源修订的诊断页，页大小和游标有界。当前没有预览会话创建路由，也没有把 HTTP 请求自动接到真实帧生成；跨进程 Worker 仍使用既有有界协议，真实 `Runtime.Hosting` 接入是下一步。
