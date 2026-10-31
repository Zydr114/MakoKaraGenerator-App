# G20–G24 匹配发布 HTTP 有限验收

使用 Python 3 标准库，复用 `backend-web-host/smoke.py` 的启动、内存凭证、脱敏请求、任务等待、SIGTERM 排水、进程身份与凭证扫描。启动器在启动前核完整 `deployment.json`、Server／完整 Worker 文件 SHA、自包含 Linux x64 与 script-refs；真实测量还通过 Worker 能力握手与固定输入身份门禁。

先由 `backend-web-host/host.py publish` 发布匹配的 **0.94.0** 到新目录，再运行：

```bash
python3 tools/backend-g20g24-http-probe/probe.py \
  --dist "$PWD/artifacts/web-backend/0.94.0" \
  --output "$PWD/artifacts/backend-g20g24/http-final1"
```

`--output` 必须不存在。探针复用 `host.py run` 的 `WEB_BACKEND_READY` JSON 解析，从自有 0600 文件读取启动凭证；仅向启动器 PID 发 SIGTERM。所有启动／验收路径均通过 `finally` 尝试排水。stdout 仅打印最终 `status`、`httpRequests`、`assertions`、`errorType` JSON，成功 exit0，失败 exit1；失败详情查看脱敏响应及成功断言边界。

验收链包括 capability、两行准确时间、容器创建／布局类型、工程与本句进入动画／本句局部变换、countdown／生成预览／选择应用／mark、time 与 layout 的实际 Worker PNG／SHA／稳定槽 ID／有限几何、测量不改历史、真实 Worker 附着模式切换、旧修订测量 409 与完整编排／历史不变、保存关闭重开后的完整投影／固定源／采样指纹／PNG、一轮正常 SIGTERM 与所有实际观察的进程身份消失。

公共新建当前要求音频。探针先通过 HTTP 创建短 WAV＋两行 LRC 工程并保存关闭，再只对自有关闭工程移除音频声明，通过公共 HTTP 重开；`fixture.json` 记录原／新 manifest SHA 与范围。这是离线夹具准备，不计作公共无音频新建能力。其后所有业务编辑、测量和持久化均走真实 HTTP。

证据包含脱敏原始请求／响应、PNG 文件、`fixture.json`、`deployed-shutdown.json`、`summary.json`、`evidence-manifest.json`。凭证只留内存，收尾复用共享扫描。此探针不替代独立十帧导出／像素 oracle、旧 Worker／反例／预算／取消回归，也不代表浏览器、用户、完整 B3/B4 或平台矩阵验收完成。
