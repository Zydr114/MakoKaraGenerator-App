# Linux GPU 共享纹理 P0 有限验收

日期：2026-11-01（Asia/Shanghai；原始 run ID 使用 UTC 2026-10-31）。本记录支持有条件继续 P1/P2，不代表持续播放编辑器完成或 P3 性能通过。

## 实际链路与版本

App 内固定 Electron 45.2.1 / Chromium 156.0.8078.75 / Node 24.22.1 / N-API 10。安装由 `desktop/p0/setup.mjs` 执行，使用独立 `desktop/pnpm-lock.yaml`；未使用 root、系统配置修改或 `--no-sandbox`。可见 BrowserWindow 使用 sandbox、contextIsolation，renderer 不启用 Node integration。每次 profile 独立位于忽略的 `.local/gpu-preview-p0/`。

生产者是本 App 构建的真实子进程，通过私有 UNIX SOCK_SEQPACKET / SCM_RIGHTS 传输 dma-buf。main 用本 App N-API addon 非阻塞接收，校验 SO_PEERCRED、所属子 PID、epoch、slot epoch、frame sequence、完整 plane bounds；Electron 对本轮纹理 fresh import 后立即关闭接收到的 FD。每次 import 不跨帧缓存。renderer preload 调用 `getVideoFrame()`，绘入可见 canvas，关闭 VideoFrame 并显式 release。main transfer 完成也 release；只在正常消费者的 `allReferencesReleased` 后发送 exact-lease ACK。

Vulkan 生产者实际设备 AMD Radeon 880M Graphics (RADV STRIX1)，vendor 4098 / device 5390 / render node 226:128。消费者同时校验 Chromium active PCI identity 和 **真实 GPU 子 PID** 所打开 `/dev/dri/renderD128` 的设备号；不是仅比较 main 中的标称名字。`app.getGPUInfo('complete')` 本机仍返回部分默认 auxAttributes（如 gl=none），因此不据此宣称具体 GL backend；实际 GPU 子进程、DRM FD 和 `getGPUFeatureStatus` 另有记录。

生产候选采用原 Skia BottomLeft 普通 surface，经一次 `vkCmdBlitImage` Y 翻转到 BGRA export target，保留原渲染坐标语义。匹配 Skia native probe 与补丁位于 `probes/skia-gpu-target/`；其 C ABI、构建成本和查询限制由独立说明记录。这里的 `gpuCopiesPerFrame=1` 只计生产侧这一次 blit，不包括 Chromium 内部实现。

## 有限结果

原始结果、所用二进制 SHA-256、plane metadata、事件顺序及有限截图见 [evidence/p0-2026-11-01](evidence/p0-2026-11-01/)。`runs.json` 列明本机 run ID 和参数；`sha256.json` 固定归档文件。

| 场景 | 结果 | 证据目录 |
| --- | --- | --- |
| 真实 Skia direct 720p，1 帧与 12 帧三在途正确性、30 帧 finite | 通过；direct 每帧 copy counter 为 0 | `skia-direct-*` |
| 真实 Skia BottomLeft + one blit，720p 12 帧三在途正确性 | 通过；每个 frameSeq 的四角、8 级灰、透明和半透明 magenta、16-bit 图案帧号、VideoFrame timestamp 均断言 | `skia-blit-correctness12` |
| 同模式 30 帧 finite | 通过；无 getImageData、截图或逐帧像素 CPU 回读；最大 imported lease 为 3；每帧 exactly 1 生产侧 GPU blit | `skia-blit-finite30` |
| 同模式 sRGB surface，720p 12 帧正确性 | 通过；命令环境 `MAKO_P0_SKIA_COLORSPACE=srgb` | `skia-blit-srgb12` |
| 同模式 2560×1440 单帧正确性 | 通过；stride 10240、offset 0、allocation 14745600、modifier 0；方向／alpha／序号正确，截图已目视复核 | `skia-blit-1440-single` |
| 慢消费者 180ms / 隐藏窗口，各 12 帧 | 通过；固定 3 槽，无扩池；隐藏验证了消费资源路径，不宣称隐藏帧可见 | `slow12` / `hidden12` |
| 主动 detach / 关闭窗口 / 无消费者 | 合作停止通过；stop 后停止发布，所有已发布 lease 真实 final release，接收 stopped，producer exit 0 | `detach-drain` / `close-drain` / `no-consumer-drain` |
| renderer SIGKILL / GPU 子进程 SIGKILL | **预期 fail-stop**；ACK 0、未知 completion、不复用、不自动重建池，producer 断连退出，整个 Electron 实例退出 | `renderer-expected-fail-stop` / `gpu-expected-fail-stop` |

正常完成必须同时满足：每个 frameSeq 有正确 canvas draw 身份（correctness 模式还校验像素）、每个 lease 真实 final-release、全部 exact ACK、合法 finished/stopped 包、生产者已完成自身 Vulkan cleanup 并 exit 0。30 帧 one-blit 运行 main 在 ACK 时 FD 总数范围 116～118，最大在途 3；这说明本次有限运行有界，不是长期泄漏测试。主要 one-blit 与 fail-stop 运行另有 `process-drain.json`，按 PID + `/proc` starttime 确认所记录 Electron 子进程退出；不按名称停止进程。

初期 `single-correctness` 和 `rotating-correctness-12` 保留为纯 Vulkan clear 链路观察；后者当时仅三槽轮换、单帧在途，不能用它证明三在途。真正三在途由后来的 Skia 12/30 帧证据支持。

## 同步与结论边界

正常路径的一手依据是 Electron v45.2.1 `lib/browser/api/shared-texture.ts` 中 main/renderer ref tracking，以及 `shell/common/api/electron_api_shared_texture.cc` 中 `ImportedSharedTexture::SetupReleaseSyncTokenCallback()`：正常消费者传回最终 release token，经 GPU 等待后触发 callback。send Promise、renderer release-request 日志、VideoFrame.close、canvas draw 均不单独授权生产者复用。

**崩溃路径不能沿用正常完成保证。** Electron 的 dangling-frame monitor 可能仅保留 acquire token；native context support 缺失时还可能直接执行 callback。因此 consumer/GPU loss 后的 callback 也禁止 ACK。当前限制是未知状态隔离、停止流并退出整个 Electron/producer 实例。没有验证崩溃后在线恢复，没有后台重建 pool。注入测试在杀子进程前已设置 consumerLost，只证明已检测故障后的 fail-stop；未覆盖全部自发驱动 loss 与 callback 的通知竞态。若要求更强的故障恢复或 completion status，需要额外 native 支持与独立审查。

本地曾尝试 `forcefullyCrashRenderer()`，但 abort 后 30 秒未收到 renderer-gone，有限测试超时；另一个中间版本曾把崩溃 monitor callback 当正常归槽，其 `2026-10-31T18-19-02...` 本地结果已撤销，不作为本门禁证据，也未归档为通过支持。最终故障证据只采纳上述 ACK 0、unknown completion、whole-instance exit 路径。

生产完成使用 queue idle 正确性基线与 GENERAL / FOREIGN ownership 交接。CPU 等待不等于像素回读；queue idle 的吞吐代价尚未量化，不能直接作为最终连续播放同步方案。correctness 运行刻意使用 getImageData 和 capturePage，finite 运行没有；两者不可混为无回读性能测量。

canvas draw、可见 compositor 截图、GPU final release、屏幕扫描是不同事件。P0 没有显示帧率测量，没有 180/600 秒矩阵，没有 Chromium 内部 copy=0 证明，也没有完成真实工程、音频或编辑器验收。

## 重现入口

```bash
pnpm setup:p0
headers=$(bash native/gpu-preview/bootstrap-headers.sh)
VULKAN_INCLUDE_DIR="$headers" bash native/gpu-preview/build.sh
# Skia 插件须先按 probes/skia-gpu-target 的固定依赖构建
pnpm preview:p0 --mode correctness --slots 3 --frames 12 \
  --draw-plugin .local/skia-p0/libmako-skia-blit.so --gpu-copies-per-frame 1
pnpm preview:p0 --mode finite --slots 3 --frames 30 \
  --draw-plugin .local/skia-p0/libmako-skia-blit.so --gpu-copies-per-frame 1
```

`correctness` 的双 rAF 只用于可见正确性观察；finite 不附加该固定等待。故障场景按 `--scenario renderer-crash|gpu-crash` 返回失败退出码，符合 expected fail-stop，不能当成普通成功吞吐样本。
