# 工程预设分配真实 HTTP 探针

该探针运行匹配的 self-contained Server＋Worker，覆盖 `presetApplications.apply`、`update`、`setEnabled`、`remove`，undo／redo、no-op 保 redo、库更新后的快照隔离、保存／关闭／重开，以及普通预览基线的 PNG 传输完整性。

带 preset application 的公共 `preview-sessions` 当前按既有隔离合同返回 `422 commandNotSupported`；探针把这个拒绝作为边界证据。随后通过 validation 和纯视频 export HTTP 任务验证真实 Worker 的 `static-lyrics-scene-presets-v1` 来源 profile、完整报告与媒体 SHA／大小，并用 ffprobe 核对导出帧数。

```bash
python3 tools/backend-preset-assignment-http-probe/probe.py \
  --dist artifacts/backend-preset-assignment-0850/deployment-final1 \
  --output artifacts/backend-preset-assignment-0850/http-final8
```

通过证据：167 个 HTTP 请求、991 条断言，见输出目录的 `summary.json`、`evidence-manifest.json`。该探针不宣称公共预览预设的最终外观、Server 重启恢复、完整 B3/B4、浏览器或平台矩阵验收。
