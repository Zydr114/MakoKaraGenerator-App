# 本机 Vulkan Worker 验收探针

复用 `backend-public-package-preview-probe` 的真实 ProjectStore fixtures、HTTP helpers、direct Runtime oracle 和六组实际失控作者控制；复用 `backend-package-export-http-probe` 的导出合同、媒体/报告身份、ACL/Range/HEAD 与完整媒体解码检查。只新增本机 GPU 身份、像素检查和运行编排，不修改公共 HTTP 合同。

先准备当前源码匹配的 self-contained Server/Worker 部署，并生成 Release fixture/oracle：

```bash
flock -o /tmp/mkg-gpu-render-dotnet.lock dotnet build tools/backend-public-package-preview-probe/FixtureOracle/FixtureOracle.csproj -c Release -m:1 /p:UseSharedCompilation=false
python tools/backend-gpu-render-probe/probe.py --dist artifacts/gpu-dist --output artifacts/gpu-http-final1
```

输出目录必须不存在。默认使用该 Release `FixtureOracle.dll`，也可显式指定 `--oracle`。需要 Linux `/proc`、可访问的真实 DRM render node、Vulkan loader、硬件 ICD、`dotnet`、`flock`、`ffmpeg` 和 `ffprobe`，不需要 root。

默认固定 `/usr/share/vulkan/icd.d/radeon_icd.json`；其他硬件通过 `--icd /absolute/path/to/hardware_icd.json` 指定。工具将 `MAKO_RENDER_BACKEND=vulkan` 与单一 `VK_DRIVER_FILES` 传入 Server、Worker 和 direct Runtime；软件 ICD 明确拒绝。每个成功帧的实际存活 Worker，以及每个成功导出期间观察到的新 Worker，必须同时满足：

- PID/start ticks 前后匹配，exe 是此部署的 Worker；
- Worker 白名单环境与显式选择相符；
- 同一 Worker 映射 Vulkan loader 和指定 ICD 驱动；
- 同一 Worker 持有 `/dev/dri/renderD*` fd，sysfs 可确认内核驱动及 vendor/device。

不会保留完整进程环境，证据仅保留 `MAKO_RENDER_BACKEND`、`VK_DRIVER_FILES`。观察范围只包含本次 launcher 的后代。`gpu-worker-evidence.json` 将实际帧/导出与观察到的 Worker 身份绑定；启动能力探测、初始化失败和早期退出 Worker 可能没有完整 GPU 证据，不能据此将它们记为硬件渲染。

默认阶段：

1. `previews`：static、包 scale、graph shader/capture、sprite、source material，各普通/透明 4 帧及一次回退 seek；完整 PNG SHA/尺寸、direct GPU Runtime PNG、HTTP straight BGRA 全解码/SHA、透明 alpha，以及 direct Runtime premultiplied BGRA 长度/SHA/预乘范围。包另与禁用实例/材质的 direct Runtime 反事实比较，确认实际像素贡献。
2. `exports`：static、scale、graph、material 各普通 MP4 与透明 ProRes4444 短片；完整报告/媒体身份、独立 ffprobe 帧数与无音轨、全帧 BGRA 解码/内容/alpha/SHA，以及既有 ACL/Range/HEAD/ETag/诊断读取检查。
3. `preview-controls`：Initialize/Evaluate × cancel/deadline/close 六组真实失控作者及子进程，验证独立健康预览、恢复与原 Worker/child 身份排水。失控作者可能在创建GPU设备前进入循环；summary分别记录Vulkan配置与该失控Worker是否取得实际硬件采样，不能把配置等同于其初始化完成。
4. `export-controls`：10 秒包影片进入实际硬件 Vulkan 后 SIGSTOP，随后分别取消、20 秒 deadline、工程 close/cancelAndWait；验证终态、无媒体产物及原 Worker 排水。控制前重新取得所选Worker的实时硬件证据、精确身份和job running状态，暂停后再次核对，防止将短命握手Worker或已退出Worker的缓存采样用于当前控制。

阶段可用 `--stages previews,exports` 缩小；预览类型可用 `--kinds static,graph` 缩小，summary 会记录实际请求和完成范围。失败目录必须保留，重跑使用新目录。所有分支均尝试正常关闭 launcher；shutdown 证据要求 Server exit0、所有观察进程身份退出，凭证扫描与最终证据 SHA 清单单列登记。

此探针只验收当前开发机的有限 GPU 后端链路。HTTP 不提供裸 premultiplied BGRA，工具保留的 HTTP BGRA 来自完整 PNG 解码、为 straight alpha；裸 premultiplied readback 来自独立 direct Runtime。相同机器的 direct GPU PNG 比对不等于 CPU/GPU 一致性，影片解码不含独立 Runtime 影片像素 oracle。没有跨平台、性能、浏览器、GPU 编码或恶意脚本 OS sandbox 的结论。
