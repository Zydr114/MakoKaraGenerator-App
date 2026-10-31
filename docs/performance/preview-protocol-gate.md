# 预览传输门禁与停止边界

目标是排除实际画面渲染内容，验证后端到前端完整四字节 RGBA 帧，从
1280×720 @ 24 fps 到 2560×1440 @ 30 fps 的稳定传输与交互。当前实现存在
协议兼容性阻塞，因此本轮只执行有限真实 HTTP 门禁，停止性能矩阵。
这里的“协议不足”指当前应用提供的端点、尺寸与回执合同，不是 HTTP 网络带宽上限。

## 本轮范围

`benchmarks/preview_protocol_gate.py` 通过发布包启动的真实 Server 注册独立客户端，
读取 `runtime-info` 与 `capabilities`，上传两秒静音 WAV 和一行歌词，创建有效工程。
先使用该工程精确修订创建 1280×720 预览会话，再向同一个端点、同一来源提交
2560×1440 创建请求。仅比较尺寸的正负对照，避免用未认证请求或不存在的工程获取 400。
脚本不提交 `frame-requests`，不执行合成图吞吐测试，也不改变原始兄弟仓库。

原始请求及 HTTP 响应保留在 `evidence/2026-11-01-preview-protocol-gate/`。
`result.json` 记录 UTC 时间、机器、App 提交、源快照 SHA、发布清单及其 SHA256、
实际请求计数和完成断言。注册凭证脱敏，结束时扫描所有结果文件是否含本次凭证。
脚本在结束时关闭自己创建的预览与工程，并等待工程关闭任务终态。

## 实际门禁结果

本次运行时间为北京时间 **2026-11-01 01:34:31 至 01:34:33**，对应证据内
UTC `2026-10-31T17:34:31.800979+00:00` 至 `17:34:33.619650+00:00`。
这是门禁起止时间，不作为吞吐或延迟指标。

| 真实 HTTP 操作 | 实际结果 | 证据 |
| --- | --- | --- |
| GET runtime-info | HTTP 200；1280×720；4,194,304 B | `002-runtime-info.response.json` |
| GET capabilities | HTTP 200；获取同一 Server 合同 | `003-capabilities.response.json` |
| 有效工程、精确修订创建 1280×720 preview | HTTP 201 | `007-create-valid-720p-preview.response.json` |
| 同来源创建 2560×1440 preview | HTTP 400；`invalidRequest`；`field=size`；“预览尺寸超过当前Worker公布的上限。” | `008-reject-1440p-preview.response.json` |
| 关闭自己创建的 preview / project | preview 关闭成功；project close job 到达 `succeeded` | `009` 至 `012` 响应 |

脚本退出 0，`outcome=blockedByExistingProtocol`；共 12 个 HTTP 请求，
`previewFrameRequestsIssued=0`，清理状态 `completed`。注册/访问/恢复凭证扫描通过。
达到明确停止条件后没有再发送帧请求或启动性能矩阵。

运行的是 `0.108.0`、Release、linux-x64、自包含匹配 Server/Worker。发布清单记录
App 源提交 `947f8d4835b11922bde93ba8570d50257b0831ba`、`sourceDirty=false`；
其 backend tree 对应原后端源提交 `a4783fad683bcfef3f2db4042e8963e5228830b5`，
frontend 快照来自 `c2d06fd68a051de1ab1018b9cd2b18ef2ccd8bab`。
门禁脚本与根整合 wrapper 当时尚在工作树，随后单独提交；该 App 提交号用于标识
精确被发布的后端快照，不表示包含后来提交的脚本和 wrapper。
完整部署清单、文件哈希、机器与时间见 `result.json`。

## 已确认的源合同

| 位置 | 当前行为 | 对目标的影响 |
| --- | --- | --- |
| `backend/src/MakoKaraGenerator.Backend.Server/PreviewWorkerConfiguration.cs` | 公布最大 1280×720，裸像素预算 4 MiB | 2560×1440 四字节帧为 14,745,600 B，超出合同 |
| `backend/src/MakoKaraGenerator.Backend.Server/Program.cs` | 预览 registry 同样固定 1280×720、4 MiB | 不存在通过前端解除节流即可突破的公开配置 |
| `backend/src/MakoKaraGenerator.Backend.Server/PreviewBackendService.cs` | `ValidateSize` 在创建前拒绝超限尺寸，`field=size` | 超限请求不会进入实际帧渲染 |
| `backend/src/MakoKaraGenerator.Backend.Worker/StaticRenderSession.cs`、`backend/src/MakoKaraGenerator.Backend.Contracts/WorkerProtocol.cs` | 内部结果是预乘 BGRA，Worker 消息使用 JSON，字节 payload 经 JSON 序列化 | 内部四字节像素不等于浏览器收到 raw RGBA |
| `backend/src/MakoKaraGenerator.Backend.Hosting/BackendPreviewSessionRegistry.cs`、`backend/src/MakoKaraGenerator.Backend.Rendering.Skia/SkiaBackendPreviewPngEncoder.cs` | 将 BGRA 编码为 PNG，公开帧内容类型 `image/png` | 本轮不能宣称测到了 raw RGBA 传输 |
| `frontend/src/editor/preview/preview-controller.ts` | 下载完整 blob 后创建对象 URL | 当前浏览器显示路径是图片资源 |
| `backend/src/MakoKaraGenerator.Backend.Hosting/ClientRequestReceiptStore.cs`、`ProjectCommandRetentionOptions.cs` | 默认每客户端 1024 个有效或在途回执，完成但未过期的回执仍保留；默认最大允许重试窗口是 600 s | 容量和过期按当前合同共同约束帧请求 |
| `frontend/src/client/clock.ts` | 当前 Web 默认给每个请求 60 s 重试窗口 | 按当前客户端条件，1024/60 ≈ 17.07 次/s 是窗口容量推算 |
| `frontend/src/editor/preview/PreviewPanel.tsx` | 播放请求间隔 500 ms，观察循环 250 ms | 此限制未修改；移除它仍需先解决上面的协议门禁 |

回执结论仅为当前源代码推算，本轮没有高频回执实测。若单客户端以不同 requestId
连续提交每帧一个请求，60 s 窗口内 24 fps 需要约 1440 个槽、30 fps 需要约 1800 个槽，
均高于默认 1024。空表、不含其它操作时，达到 1024 个请求约需 42.67 s / 34.13 s；
实际创建、上传、编辑也共享容量。本轮没有调整配额、缩短 TTL 或轮换客户端绕过约束。

## 原始 RGBA 需求的计算值

以下是算术需求，不是测得的吞吐；没有 PNG 压缩、JSON/Base64 或 HTTP 头开销。

| 尺寸与帧率 | 每帧 `W×H×4` | 每秒完整像素 |
| --- | ---: | ---: |
| 1280×720 @ 24 | 3,686,400 B | 88,473,600 B/s = 84.375 MiB/s |
| 2560×1440 @ 30 | 14,745,600 B | 442,368,000 B/s = 421.875 MiB/s |

## 性能矩阵状态

| 项目 | 状态与原因 |
| --- | --- |
| 720p24、720p30 稳定完整帧消费 | 未运行：有限兼容性门禁已证明总体目标被现有协议阻塞 |
| 1080p24、1080p30、1440p24、1440p30 | 未运行：超出当前 Server 1280×720 公布上限 |
| 窗口帧率、传输/解码/显示延迟及分位数 | 未测量 |
| 输入到显示、seek、播放/暂停的响应 | 未测量 |
| backpressure、stale frame 丢弃与恢复 | 未测量 |
| 持续负载内存、CPU、网络字节量 | 未测量 |
| raw RGBA 与 PNG 的实测带宽比较 | 未运行：当前公开路径没有 raw RGBA 端点 |

整合的静态 PNG / 浏览器 smoke 属于独立功能验收，不能替代本表的连续完整帧测量。
后续要恢复性能矩阵，应先提供满足目标尺寸、完整像素格式、持续请求和有界积压要求的
真实应用协议，再用可控 synthetic frame provider 排除实际渲染，并测完整浏览器消费。
本轮没有新增另一套二进制 mock 端点或以压缩 PNG 速率冒充 RGBA 结果。

## 复现

先按根 README 构建并启动 App 的匹配 Server/Worker，保持启动器运行。
下面从本地 pointer 读取带时间戳的真实发布子目录，从前端连接文件取得 run 目录，
再传入该目录下的原始 bootstrap 文件。不打印 token，不把 token 放进命令行：

```bash
python3 -B - <<'PY'
import json
import subprocess
from pathlib import Path

dist = json.loads(Path('.local/backend-current.json').read_text())['dist']
run = json.loads(Path('.local/run/backend.json').read_text())['runDirectory']
subprocess.run([
    'python3', '-B', 'benchmarks/preview_protocol_gate.py',
    '--credential-file', str(Path(run) / 'bootstrap.json'),
    '--dist', dist,
    '--output', 'docs/performance/evidence/a-new-gate-run',
], check=True)
PY
```

输出目录必须不存在，避免覆盖历史证据。预期进程退出 0，`result.json` 的
`outcome=blockedByExistingProtocol`，合法预览 HTTP 201，超限预览 HTTP 400、
`code=invalidRequest`、`field=size`。这个成功退出表示“成功复现阻塞”，不表示性能达标。
