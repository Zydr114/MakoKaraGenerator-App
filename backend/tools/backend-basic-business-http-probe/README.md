# 基础业务独立匹配发布 HTTP 验收

使用既有 `backend-web-host/host.py` 校验并启动匹配的 self-contained Server/Worker，随机 loopback、私有 bootstrap、真实媒体准备／分析／预览 Worker。探针只通过公开 HTTP 建立和编辑工程，不修改服务端工程文件，也不注入 fixture executor。

```bash
python3 tools/backend-basic-business-http-probe/probe.py \
  --dist "$PWD/artifacts/web-backend/0.95.0" \
  --output "$PWD/artifacts/basic-business-http-0950"
```

`--dist` 必须是既有匹配发布目录，`--output` 必须不存在。探针不执行 restore/build/publish。Linux 进程身份观察与既有 ffmpeg 完整 PNG 解码沿用原工具。

本次有限链路：

- 授权根／单层目录查询、非法查询和穿越拒绝，公开新建、保存后发现工程。
- 普通／增强 LRC 固定修订计划、损失确认、完整报告、回执重放、精确 UTF-8/BOM/换行字节 oracle、认证下载／HEAD／Range／ETag，跨 client、无凭证及旧计划拒绝。
- 真实 PCM16 WAV 的 Worker 分析及采用，替换／撤销／重做／同资源 no-op、清除及恢复、全部层级波形 min/max float32 字节 oracle、历史资源租约、改名／资源清单、保存／重开。
- 完整静态模板 ZIP 导出／CRC／上传／预检／CAS 导入／生成身份映射／重放／重新导出；除明确重新生成的模板身份外完整定义相等。
- 片段成员扩缩、拆分／合并、调度上下文、既有公开行预设引用保持、静态样式来源解释；真实 Worker 同时刻拆分前后 PNG 一致及公开预览完整解码。
- R8 失效来源、缺前奏及真实前奏的错误容器稳定错误／回执重放／历史不变；最终保存／重开保留完整歌词与编排。

每个 JSON 请求／响应保存脱敏配对；二进制保存字节与 SHA。`summary.json` 保存计数、job 终态、真实 Worker 身份、测量与产物身份；SIGTERM 后检查所有实际观察的进程身份均已消失。最后扫描 evidence/profile 中 bootstrap/access/resume 凭证，并生成 `evidence-manifest.json`。失败目录保留原失败证据，修复后使用新的 output。

这是后端公共链路验收，不代表浏览器／用户验收、任意高级模板执行或原完整性能／长片／平台矩阵完成。Core 导出器的直接双格式字节与报告对照由 `FileLyricsTests` 提供，部署探针使用独立确定输入字节 oracle。
