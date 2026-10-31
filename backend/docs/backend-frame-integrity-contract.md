# B3-B 预览帧发布完整性合同

`PreviewTransportValidation.ValidateFrame` 和 `PreviewFrameContract.Validate` 组成真实帧进入句柄存储或 HTTP 响应前的纯验证门禁。前者校验会话、代次、来源修订、请求序号、输出设置、PNG句柄租约和诊断分页；后者进一步校验实际二进制内容的大小和 SHA-256。调用方必须提供当前预览会话、上一个已接受的请求序号和服务端公布的 `PreviewLimitsDto`。

验证顺序固定为：会话归属 → 代次与来源修订 → 请求与帧序号一致 → 输出设置 → 正数尺寸与尺寸上限 → `image/png` 内容类型 → 内容大小与保留字节上限 → 未过期租约 → 句柄 ID／URL／SHA-256 格式 → 实际内容大小与 SHA-256。失败返回稳定 `BackendErrorCode`，不产生可发布的帧句柄。诊断页另外绑定同一代次／来源和游标，超过公布页大小或游标不一致时要求重新读取。

这条合同只验证已生成的字节，不承担渲染、PNG编码、资源路径访问或 HTTP 路由。当前 Worker 仍是经过协议校验的 opaque 回显；真实 `Runtime.Hosting` 帧、句柄租约存储、诊断分页和 Server 路由接入后，必须继续使用这条验证门禁。
