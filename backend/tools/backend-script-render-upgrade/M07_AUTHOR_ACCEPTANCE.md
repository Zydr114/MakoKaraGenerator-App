# M07 作者工作流有限验收

本文件区分工具实现与实际证据。`m07_author_probe.py` 实现下述 HTTP 主链路；`frontend/scripts/author-v2-smoke.mjs` 实现下述有限真实 UI 链路。2026-11-04 已使用产品 0.111.3／发布源码 72be32b 完成有限实跑：HTTP 99 次请求／730 条断言；UI WordEmphasis 33 条、RepeatEmphasis 17 条、ClusterExit 17 条检查，共 9 张实际解码显示帧。证据位于 App `.local/script-sdk-m07-20261104/http-author-01/` 与 `ui-*-03/`；完整边界见 App `docs/plans/script-sdk-render-m07-evidence.md`。HTTP 工具复用 W8 的包/历史读取、CompletionProbe 的固定帧与 PNG 完整解码，以及 render-backend probe 的匹配发布启动、Vulkan Worker 观察和进程身份排水。旧 W8 `run()` 与 `author-smoke.mjs` 的 SDK1 自动跟随假设不能直接用作 SDK2 验收。

## HTTP 主链路

使用本仓库同一发布的 Server/Worker、全新输出目录和隔离 profile。显式传入硬件 Vulkan ICD；loopback 请求直连。包来自 `samples/ScriptEffectsV2/WordEmphasis`，保持 manifest 原字节并逐文件核对 SHA256。执行命令由集成者在匹配发布完成后串行运行：

```bash
python3 backend/tools/backend-script-render-upgrade/m07_author_probe.py \
  --dist /absolute/path/to/matched-dist \
  --output /absolute/path/to/new-m07-evidence \
  --icd /usr/share/vulkan/icd.d/radeon_icd.json
```

1. HTTP 上传、检查并创建 SDK2 草稿，核对 `packageVersion=3`、`sdkVersion=2.x`、初始来源 A。只改变参数并保存，确认 CAS 前修订、来源 A 和参数读取一致；陈旧配置写入返回 409。
2. 保存故意含编译错误的来源 X。最新来源变成 X，在用/已采用来源保持。编译结果必须绑定 X 且失败；显式采用 X、通过配置改用 X 均具名拒绝，草稿不变。
3. 保存正确来源 B；异步编译时检查接受输入的 draft/source 身份，轮询到终态并读取实际编译结果。成功编译仍保持在用 A；显式采用 B 才切换。`202` 仅作为接受证据。
4. 未编译 A 的撤销先被拒绝。精确编译历史 A 返回 `stale=true`，最新 B 和在用 B 保持；采用 A 完成源码撤销，再采用 B 完成重做。陈旧采用 CAS 不改变状态。
5. 显式 GPU 创建作者预览并完整下载/解码 PNG。记录草稿修订、在用源码、preview source、generation、requestSeq、frameId、PNG/RGBA SHA256 及诊断页。观察同一发布的实际 Worker：Vulkan 选择、固定硬件 ICD、loader/driver 映射、DRM render fd 和 PID/startTicks。此证据不代表浏览器已显示该帧。
6. CPU 创建、CPU 验证、现有 GPU 预览切到 CPU 均要求 `commandNotSupported/PackageBackendUnsupported` 和 `field=renderBackend`；失败切换保持原来源、generation 和已有帧。参数保存后，旧预览仍能输出固定旧输入；显式 PUT input 才更新草稿修订，省略后端时保留 GPU。
7. 从 B 保存完整包文本为 C，确认最新 C、在用 B、旧编译身份保持。陈旧包文本 CAS 返回 409；历史分页包含 A/X/B/C，各自编译状态不混淆。使用当前草稿 CAS 读取历史 A/B，核对原源码和完整包条目字节不变。
8. 成功时只删除本次草稿、关闭本次预览；失败时保留隔离 profile 的作者资料便于定位。无论结果如何停止本次 launcher，核对观察到的 PID/startTicks 已消失并扫描输出凭证。摘要列明发布身份、终态、帧身份、设备观察与验收边界。

该有限链路不覆盖所有 SDK2 样例/能力、GPU 正式 720 帧作者验证、保存库后重开、重启 claim、浏览器、桌面共享纹理或性能。正式作者验证可复用 W8/Selection 的 job/report 辅助，但必须等终态并核对固定 draft/source/sample/backend；当前服务每种输出 360 帧，不把单个 `202` 或 UI 的“360”当成完整验证结果。

## 有限真实 UI 链路

`frontend/scripts/author-v2-smoke.mjs` 是独立 SDK2 入口，保留旧 SDK1 smoke。`--sample` 只接受 `WordEmphasis`（默认）、`RepeatEmphasis`、`ClusterExit`；三者读取各自实际 manifest、导出、C# 文件与参数声明。它复用真实 Chrome/CDP、ARIA 查询、CodeMirror 键盘编辑与现有设备菜单；所有作者及设备修改都经过 UI，不调用 store 内部方法或额外发 HTTP 写请求。`ui-network-evidence.mjs` 只观察该页面实际发出的请求和响应，保留脱敏公共身份、C# 文本哈希和完整 PNG。Chrome 的 Blob 响应可能不保留在 Network 正文缓存，因此 PNG 在 CDP Fetch 响应阶段读取后原样放行，没有额外 HTTP 请求；该观察会影响交付时序，不能作为性能证据。

由集成者先串行完成匹配 Server/Worker 构建与启动，再启动连接该服务的本仓库前端。服务使用隔离 profile 和硬件 Vulkan ICD，且必须包含同一版本 Worker，避免开发目录旧 Worker 造成 `SnapshotVersionMismatch`。UI 工具不构建或启动服务，只启动并关闭本次独立 profile 的 Chrome。运行命令：

```bash
node frontend/scripts/author-v2-smoke.mjs \
  --sample WordEmphasis \
  --url http://127.0.0.1:5173 \
  --output /absolute/path/to/new-m07-ui-word

node frontend/scripts/author-v2-smoke.mjs \
  --sample RepeatEmphasis \
  --url http://127.0.0.1:5173 \
  --output /absolute/path/to/new-m07-ui-repeat

node frontend/scripts/author-v2-smoke.mjs \
  --sample ClusterExit \
  --url http://127.0.0.1:5173 \
  --output /absolute/path/to/new-m07-ui-cluster
```

以下 WordEmphasis 完整作者链及其余两例的缩小链已分别实跑通过；这是有限链路通过，不能代替本文件末尾列出的 M11 完整作者工作流：

| 样例 | 实际 C# 入口 | 实际参数 | 本次有限实跑覆盖 |
| --- | --- | --- | --- |
| WordEmphasis | `src/WordEmphasis.cs` / `WordEmphasis` | `peakScale`、`returnUs` | 下述完整作者链 |
| RepeatEmphasis | `src/RepeatEmphasis.cs` / `RepeatEmphasis` | `countScope`、`scaleStep`、`maxScale`、`darkenStep`、`minBrightness` | 导入、建立草稿、GPU A、读取真实源码、编辑保存 B、编译、显式采用、GPU B 及固定身份 |
| ClusterExit | `src/ClusterExit.cs` / `ClusterExit` | `driftPx`、`staggerFraction` | 导入、建立草稿、GPU A、读取真实源码、编辑保存 B、编译、显式采用、GPU B 及固定身份 |

RepeatEmphasis 和 ClusterExit 不重跑错误编译、CPU 拒绝或参数撤销链，也不依赖 `peakScale`。三例均检查各自 manifest 声明的参数控件、真实源码读取内容哈希、编辑后实际提交内容哈希，以及编译/采用期间参数配置保持。

WordEmphasis 完整链：

1. 按原 manifest 字节和逐文件 SHA256 打包所选 SDK2 样例，经导入 UI 创建组件和作者草稿。核对真实网络响应的 package3/SDK2、实际 package/export、初始在用来源 A 和实际参数控件。
2. 选择 CPU，确认试样创建和“开始验证”均在接受任务前返回 `commandNotSupported/PackageBackendUnsupported`、`field=renderBackend`，并在对应 UI 显示具名拒绝。选择 GPU，核对设备响应为可用 Vulkan，并等待初始 A 图片实际加载和解码。
3. 在 CodeMirror 保存含 `#error` 的 X，确认最新推进、采用/在用仍为 A；点击编译，检查实际接受输入、权威任务终态、编译结果、编译后的草稿刷新和失败诊断。点击诊断并确认编辑器标记；试样仍显示固定 A。
4. 修复并保存 B，检查 UI 提交的是对应实际源码文件和编辑后字节；成功编译后仍保持 A；只有点击“采用编译通过的修订 …”才发出 A→B 的采用请求。修订列表检查最新、编译通过、已采用、在用四种身份文案，并等待固定 B 图片。
5. 修改 `peakScale=3`，核对配置 CAS 和在用 B；点击配置撤销、重做，核对每次真实 PUT 的前修订及参数值。等待修改和重做后的对应图片。
6. 每个图片检查先锁定真实预览状态中的 draft/source/generation，再等待 DOM 的相同 PNG SHA 与时间，实际执行 `img.decode()`，只读当前 Blob URL 计算 PNG SHA256，并计算 canvas RGBA SHA256。确认图片在视口内且未被遮挡，核对 Blob 与完整网络 PNG 下载的 SHA、签名、长度、尺寸及 GPU 回显，保存截图。相同内容 SHA 证明像素内容一致，不能唯一归属某次渲染调用。
7. 成功时经 UI 关闭本次草稿、删除本次导入组件并恢复浏览器设备偏好；失败时保留本次草稿/组件 ID。始终关闭本次 Chrome、删除其独立 profile 和临时包，输出脱敏网络身份、作者身份、图片身份、未处理异常、摘要和凭证扫描。扫描发现凭证时删除受污染证据文件并将结果判为失败。

UI 的 PNG 接受证据包括真实 DOM 解码；GPU 硬件 DRM/进程观察由同次匹配发布的 HTTP probe 补充，不能仅凭 UI 的 GPU 回显声明硬件观察通过。现有 Chrome helper 带 `--disable-gpu`，因此该脚本不验收 Chrome GPU 加速或桌面共享纹理。

本 UI 工具不覆盖源码采用撤销/重做、陈旧 CAS 或保存失败后的编辑文本保持、GPU 正式 720 帧作者验证、保存库后重开、重启 claim、全部 SDK2 样例/语义、跨平台或性能。三例的源码编辑采用追加注释，不把编辑到预览链通过扩称为重复计数、全部字簇退场或像素语义验收。源码采用历史和陈旧 CAS 由上述 HTTP 有限链路覆盖；以后补齐 UI 采用历史与完整验证时，仍须等待权威终态，并核对固定 draft/source/sample/backend。UI 与 HTTP 分别给出结论，不能用 HTTP PNG 代替实际浏览器显示证据。
