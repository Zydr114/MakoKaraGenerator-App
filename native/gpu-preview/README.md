# Linux GPU 共享图像与原生通道

本目录提供生产用、借用同一 Vulkan 设备的共享图像 C ABI，以及独立的有界 P0 探针。
N-API addon 只在 Electron main 使用。它们不改现有 HTTP、预览采样或 500 ms 控制节奏。

## 构建

需要 C++20 编译器、系统 Vulkan loader/library、Node N-API headers。默认使用
`/usr/include/node` 和 `/usr/include/vulkan`，可由 `NODE_INCLUDE_DIR`、
`VULKAN_INCLUDE_DIR` 覆盖。本机 loader 可用但 Vulkan headers 缺失，因此提供
不需 root 的固定依赖引导（只写忽略的 `build/`）：

```sh
headers=$(bash native/gpu-preview/bootstrap-headers.sh)
VULKAN_INCLUDE_DIR="$headers" bash native/gpu-preview/build.sh
node native/gpu-preview/check-native.mjs
VULKAN_INCLUDE_DIR="$headers" bash native/gpu-preview/check-api.sh
```

引导使用 Khronos Vulkan-Headers `vulkan-sdk-1.4.341.0`，提交
`b5c8f996196ba4aa6d8f97e52b5d3b6e70f7e4e2`，压缩包 SHA-256
`7937d8ec21785736436d9ff52564db7d55992070b5ac98324472cce6e425a7ba`。
下载代理默认 `127.0.0.1:7897`，可用 `GPU_PREVIEW_PROXY` 覆盖；系统 Vulkan 库仍由
系统提供，不随探针复制。`build.sh --addon-only` 可独立编译 broker。

默认构建同时输出 `build/libmako_gpu_preview.so`、`build/gpu_preview_broker.node`、
`build/gpu-preview-producer`。生产库仅导出 `mkg_gpu_*` C 符号，可由 .NET
`DllImport("mako_gpu_preview")` 加载；无 Skia/Node/V8 链接依赖。

## 生产 C ABI 与同设备输出

固定合同见 `gpu-preview.h`，ABI 版本为 1；所有结构以 `sizeof(struct), version` 开头，
使用固定宽度整数和 Linux x64 的 64 位 Vulkan handle bits。所有异常都在 C 边界转为
状态码和有界 error 信息。调用方必须串行化每个 pool 和该 Vulkan queue 的全部
Skia/native 工作，销毁时不得有并行调用。库不创建线程，不创建第二个 Vulkan device。

`pool_create_borrowed` 借用现有 instance/physical device/device/queue/family。
调用方保证已实际启用五项 required extension，不能把“支持”伪装成“已启用”；库还会
真实查询设备、BGRA optimal BLIT_SRC、linear modifier BLIT_DST 及完整 target usage、
DMA-BUF external format 组合。固定三个槽，尺寸上限 2560×1440，每池预算由调用方
预留、按真实 allocation size 扣减，硬上限 192 MiB；本库不会扩池或跨设备回退。
Worker/Hosting 仍须把并存旧池与客户端/全局预算纳入自己的总预算。

调用顺序是：

```text
try_acquire → Reserved → blit → Ready → export → ExportedAwaitRelease
                  └ cancel ────┘                   │
                               exact consumer_released → Free
```

`try_acquire` 要求 frameSeq 单调递增，分配 slotEpoch 并返回完整 lease。
`blit` 要求 source 是同设备、BGRA8888 UNORM、optimal tiling、sampleCount=1、
TRANSFER_SRC usage，已由同一 Skia context flush/submit 到 TRANSFER_SRC_OPTIMAL 和
同一个 graphics family。source surface/image 必须保活到同步调用返回。native 不修改
source layout/family，因此 Skia 的实际 mutable state 仍一致。目标 acquire 后只执行
一次 nearest GPU blit（BottomLeft source 仅翻转 Y），release 为 GENERAL/FOREIGN，
checked queue idle 后 Ready；不映射或回读像素。颜色空间和预乘 alpha 语义由调用方保持，
本库不把缺省颜色空间自动声明为 sRGB。

`export` 在暴露新 FD 前原子进入 ExportedAwaitRelease，防止消费者快速归还与
“标记已发布”之间产生竞态。调用方成功后唯一拥有这个 FD，sendmsg 完成后关闭它。
同一 lease 只能 export 一次；send WouldBlock 时可有界保留同一个 FD 重试，未知交付
必须 quarantine。Reserved/Ready 可显式 cancel；导出后 cancel 被拒绝。
只有已验证的真正消费者最终完成，才能调用 `consumer_released`，匹配所有 epoch、
frameSeq 和 slotId；陈旧/重复释放没有回收效果。quarantine 禁止新 acquire/blit/export，
仍允许确认已有 lease 的真实完成以排空。

`destroy_drained` 要求所有槽 Free，checked 自身 queue idle 成功后只销毁本地池资源，
不销毁任何 borrowed context handle。未排空或 wait 失败会保留 pool。
`abandon_local` 是独立异常出口：checked 自身 queue wait 后只释放本地对象，
`out_was_drained=0` 明确表示 outstanding lease 未证明排空。device-lost 时允许释放
本地对象，返回 OK 但 `error.vk_result=VK_ERROR_DEVICE_LOST`、drained=0；其他 wait
错误保留 pool，绝不报告正常 drain。导出 payload 的独立 FD 引用寿命见后文官方规则。

`channel_listen/accept/send/receive_control/close` 与 pool 状态完全独立，使用同一
`ipc-core` 的有界 SCM_RIGHTS 实现。监听路径父目录必须属于调用者且无 group/other
权限，接受相同 UID 并返回 peer PID；Worker 必须再校验 expected main PID 与握手。
所有调用非阻塞，最多 16 KiB/4 FD。receive_control 不接受任何 FD，恶意附属句柄会被
关闭并拒绝；它不解析 JSON 或授权 lease。正常 channel close 会关闭其 FD，并仅在路径
仍为自己创建的 socket inode 时 unlink，不删除被替换的路径。

P0 和生产共享 `gpu-core` 的外存能力查询、分配、export 与 acquire/release barrier，
以及 `ipc-core` 的 sendmsg/recvmsg；P3 synthetic provider 应继续使用生产 Skia source→
同一 pool blit→同一通道，不以 P0 另一个程序代替生产吞吐证据。

## 程序入口和实际图像

```sh
native/gpu-preview/build/gpu-preview-producer \
  --socket /private/temporary-directory/preview.sock \
  --width 1280 --height 720 --slots 3 --frames 12 \
  --release-timeout-ms 30000
```

只接受 AMD 硬件 Vulkan device，不回退到软件设备。尺寸限制 64–2560 × 64–1440，
槽位仅 1 或 3，帧数 1–600，release 超时 100–120000 ms。生产者最多等待连接
60 秒。socket 在创建时受 `umask 0077` 保护；仅接受相同 UID 的 peer，不移除
预先存在的 socket 路径。调用者应使用自己的私有临时目录并核对 peer PID。

探针实际查询 `VK_FORMAT_B8G8R8A8_UNORM`、DRM linear modifier 0、单 memory plane、
`COLOR_ATTACHMENT | SAMPLED | TRANSFER_SRC | TRANSFER_DST` 和 DMA-BUF export 组合。
图像使用 DRM modifier tiling 和 dedicated device-local allocation。实际 modifier
通过 `vkGetImageDrmFormatModifierPropertiesEXT` 核对；stride、offset、subresource size
由 `vkGetImageSubresourceLayout` 查询，allocation size 来自 Vulkan memory requirement。
`planes[].size` 表示整个导出 allocation，另有 `queriedSubresourceSize` 记录原始查询值；
不假定 tight pitch。设备 UUID、DRM major/minor 来自真实 physical-device 查询。

纯 Vulkan 图案由 GPU render-pass clear 与 clear-attachment 命令生成，没有 host pixel
buffer、映射、readback、PNG 或 Base64。四象限依次为左上红、右上绿、左下蓝、右下白。
顶端高 `height/16` 的 16 个黑白块编码 frameSeq（低位在左）；中央有 8 级灰条。
底部 `x=width/4,y=3*height/4,width/4,height/8` 是预乘 RGBA `(0.5,0,0.5,0.5)`，
相邻 `x=width/2` 区域是透明黑。取四角基准时应避开顶端身份行。

## 传输和唯一所有者

addon 输出 `build/gpu_preview_broker.node`，只依赖 N-API，未使用 V8 ABI。

- `connect(path)`：非阻塞 UNIX `SOCK_SEQPACKET` 连接，核对同 UID，返回 opaque external。
  连接未就绪时抛错；上层可做有界 retry，不能在 main 阻塞等待。
- `receive(connection)`：非阻塞，暂无包返回 `null`；成功返回
  `{ metadata, fds, peerPid }`。metadata 为 JSON object，最多 16 KiB，最多 4 个 FD。
  `recvmsg(MSG_CMSG_CLOEXEC)` 设置 FD 的 close-on-exec；截断、无效附属消息、无效 JSON、
  null 或 array 均拒绝并关闭本次接收到的全部 FD。
- `send(connection, jsonString)`：发送最多 16 KiB 的控制包；失败抛错，不推断 ACK 已生效。
- `closeFd(fd)`：只关闭本 addon 接收且仍拥有的 FD；重复关闭或任意数字被拒绝。
  Electron import 同步 dup 所有 plane 后应关闭收到的原始 FD；失败路径也必须逐个关闭。
- `close(connection)`：关闭连接，可重复调用。环境 cleanup hook 关闭仍由 addon 持有的 FD；
  connection external finalizer 关闭其 socket。raw FD 不得暴露给 renderer。

FrameReady 的 `type` 是 `frame`，带 protocolVersion、producerPid/producerEpoch、
poolGeneration、slotId/slotEpoch、frameSeq、width/height、format/fourcc、modifier、
planes、allocationSize、origin、alphaMode、colorSpace、sync、gpu 和 counters。
每个 plane 使用 `fdIndex` 关联本包 SCM_RIGHTS 附件，绝不把另一个进程的 FD 数字写作句柄。
收到的 `peerPid` 必须等于本次启动的生产者 PID；Electron 还应验证 descriptor 的全部字段。

## 每轮同步与三槽流控

首次绘制从 UNDEFINED 转为 COLOR_ATTACHMENT_OPTIMAL。复用时先从
GENERAL/FOREIGN acquire 到 graphics queue；完成绘制后 release 到 GENERAL/FOREIGN，
提交并 `vkQueueWaitIdle` 后才发布 DMA-BUF。本轮 CPU 等待不是像素回读，但不是生产性能方案。
每次发布 fresh import；不跨帧缓存初次导入的 creation token。

生产者最多发布 `slots` 个未归还帧，接受乱序 release。只有本轮真正消费者完成且
Electron 的 `allReferencesReleased` 到达后，main 才能发送：

```json
{"type":"release","frameSeq":1,"slotId":0,"slotEpoch":1}
```

该三元身份必须精确匹配当前占用；重复、延迟、未知 release 导致失败并停止复用，
不会扩池。`slotEpoch` 每轮发布递增。以下操作只是主动停止新发布，仍须归还已发布帧：

```json
{"type":"stop"}
```

stop 可重复；归还全部 outstanding 后返回 `stopped` 与实际发布帧数、槽位和计数，
退出 0。自然完成同样排空后返回 `finished`，退出 0。调用者要等该终态与进程退出，
不能在最后一次 ACK 写入后立即断开。

断连、超时、导入失败、未知 GPU 状态不授权槽位复用，探针返回 `failed`，exit 1。
Vulkan destructor 等待的是生产者自己的 queue，**不证明 Chromium 已完成消费**。
异常路径只能记录失败和未证明的 consumer lease；不能据此把 crash/quarantine 场景
标记为安全回收通过。producer 销毁自身 VkImage/内存句柄不再发起写入；消费者持有的
DMA-BUF 引用与 backing allocation 寿命由内核/驱动管理。P0 可控关闭仍优先使用
stop→全部 allReferencesReleased→ACK→stopped，避免用进程退出推断 consumer 完成。
正常终态还要求显式检查生产者 `vkDeviceWaitIdle`／插件 shutdown 成功，随后释放本地
对象，才发送 finished/stopped；device-lost 或 wait 失败不会报告正常 drain。失败析构只做
best effort 清理，并在自身 queue wait 失败时记录 `cleanup-failed`，不把它当作成功。

Vulkan 的外部内存引用规则见官方
[VkMemoryGetFdInfoKHR](https://docs.vulkan.org/refpages/latest/refpages/source/VkMemoryGetFdInfoKHR.html)：
导出的 FD 持有对应 memory payload 引用；每次导出得到的独立 FD 均由应用关闭。
[vkFreeMemory](https://docs.vulkan.org/refpages/latest/refpages/source/vkFreeMemory.html)
释放该 allocation 对 payload 的引用，其他外部引用可以继续保持 payload。
这解释 producer 本地引用释放与 consumer backing 寿命的区别，不替代本轮消费者完成证明。

## 固定 Skia 绘制插件接缝

`--draw-plugin /absolute/path/library.so` 复用同一 allocator、描述符、SCM_RIGHTS 与池状态机。
P0 ABI 见 `draw-plugin.h`：插件接收调用者拥有的 Vulkan context/image，维护正确的
GENERAL/FOREIGN acquire/release，报告本帧 GPU copy 数（0 或 1），返回成功后生产者
再次等待自身 queue idle。插件失败或 copy 数超过 1 都不发布，也不回退 CPU。
`mako_gpu_preview_shutdown_v1` 在调用者 device 存活时执行，最后才 dlclose。
描述符 `provider` 区分 `vulkan-clear` 和 `skia-plugin`，`gpuBlits` 为累计实际报告值。
此 ABI 是探针接缝，尚不是 Worker 生产 API。

## 检查范围

`check-native.mjs` 使用实际 GPU allocation 和 FD，不创建 Electron 或任何导入消费者。
因此其中关闭全部收到的 FD 后可发 native-only ACK；这不替代真实 GPU consumer release。
它覆盖单帧、CLOEXEC、double-close、三个同时 outstanding 槽位的逆序归还、stop 排空、
延迟旧身份拒绝、超时不复用、恶意 JSON/null/array、消息截断和 FD 附件截断无泄漏。
Electron 显示、校色、共享设备、消费者 GPU 完成及异常情景的证据在独立 P0 运行中记录；
本目录测试不声明呈现率、性能或端到端零 GPU copy。

`check-api.sh` 对生产 `.so` 做真实 GPU 有限检查：借用 P0 fixture 已有的设备，
用独立 optimal source GPU clear，三槽有界保留、五次 blit、原子导出、重复/旧 lease
拒绝、未排空 destroy 保留、Ready cancel、quarantine 禁止复用和 local abandon 标志。
还通过生产 channel 传递真实 DMA-BUF，验证控制 FD 拒绝并关闭、消息上限与 peer PID。
池销毁后在原 source 再次 GPU clear，确认 borrowed queue/device/source 仍可用。
此检查没有像素 mapping/readback，也没有实际 importing GPU consumer，native-only
release 仅在本测试关闭全部独立 FD 且从未导入后执行；不替代 Electron 完成信号验证。
