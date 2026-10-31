# B3 Worker IPC 最小接缝

`Backend.Worker` 是由 `Backend.Hosting.WorkerProcessProxy` 启动和回收的独立进程。当前切片验证的是进程边界、协议边界和取消生命周期；它没有宣称已经把 Runtime.Hosting 的渲染会话迁出。

Worker 使用标准输入／输出上的私有 JSON 消息。每条消息先写一个 little-endian `Int32` 长度，再写 UTF-8 JSON；长度上限为 8 MiB，opaque 输入 `payload` 上限为 4 MiB。消息包含固定的 `worker-1` 协议版本、请求 ID 和输入 `SHA-256`。Hosting 在发送和接收两端都验证长度、版本、请求 ID 与哈希，因此工程保存文件不会成为 IPC 格式。

启动参数通过 `ProcessStartInfo.ArgumentList` 传递，`UseShellExecute=false`，不拼接 shell 命令。代理先完成 `hello` 握手，再串行执行调用。取消先发送同一请求 ID 的 `cancel`，在有限宽限期内等待 Worker 回执；Worker 不收敛时代理杀死该进程树。`StopAsync` 发送 `shutdown` 并等待退出，释放时仍会回收残留子进程。

本切片的 `render` 命令只在 Worker 内延迟后回显已校验的 opaque 字节，用于覆盖协议、取消和退出回收。后续 B3 接入真实帧时，应定义独立的固定渲染输入合同，并将 `Runtime.Hosting` 会话创建和二进制帧块接在同一代理生命周期上。
