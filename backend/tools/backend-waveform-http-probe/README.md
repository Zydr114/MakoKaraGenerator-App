# G11 波形真实 HTTP 回归

使用新的自有 profile、输出目录及匹配的 self-contained Server／完整 Worker。复用 `backend-web-host` 的发布指纹检查、私有凭证、正常 SIGTERM 排水与观察进程身份核验；不接触用户工程。

```bash
python3 tools/backend-waveform-http-probe/probe.py \
  --dist /本次匹配发布目录 \
  --output "$PWD/artifacts/waveform/http-final"
```

仅依赖 Python 标准库及本机 `/usr/bin/ffmpeg`、`/usr/bin/ffprobe`。工具自建 67 秒 stereo PCM WAV（覆盖 8192 桶窗口及不足 64 样本的尾桶）、短 MP3／FLAC，以及首轨 8kHz mono、第二轨 48kHz stereo 且标为 default 的 MOV。独立 native oracle 显式 `-map 0:a:0 -ac 1`，在源采样率解码完整 float32 PCM后自行聚合全部金字塔层；不调用产品峰值 loader 生成 oracle，也不把 PCM 注册为资产。

真实 HTTP 检查元数据的 source SHA、原 channels、mono 峰值说明、完整 sampleCount／durationUs、每一层所有窗口的 min/max little-endian float bits、尾桶、64KiB 最大响应、ETag 坐标绑定、HEAD、弱／多值／通配 If-None-Match 304、CORS expose、安全头、Range 400、严格唯一 query、认证及跨 client／Host／Origin拒绝。歌词编辑保持 waveformId；保存关闭重开后同音频完整峰值身份保持，旧／关闭会话均 404。所有 JSON 与 binary／HEAD 请求响应成对留档，二进制窗口与独立层 oracle保存为 `.bin`，JSON 记录各层与原始 PCM 的 SHA。

凭证仅存在进程内和 launcher 自有 0600 bootstrap；记录 Authorization 时脱敏，正常退出删除 bootstrap，并扫描所有证据中的 bootstrap／access／resume 值。`summary.json` 只在完整成功终态、排水和凭证扫描通过后出现；`evidence-manifest.json` 固定此前证据与部署清单 SHA。

全 Host 64MiB 只限制已保留 peak payload，不是总堆上限。低预算准入、去重、失败注册与 close／Dispose／recover 生命周期由 `BackendWaveformTests` 的可信注入回归覆盖；此 HTTP 工具不配置或伪造缓存预算，也不声称真实耗尽 64MiB、最大媒体 decode、浏览器逐样本播放对齐、性能或全平台验收。作者隔离树版本可仍为前置产品版本，只有主目录匹配发布与最终冻结才构成正式版本验收。
