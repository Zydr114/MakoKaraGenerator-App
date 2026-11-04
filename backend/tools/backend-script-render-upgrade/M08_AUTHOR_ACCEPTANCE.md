# M08 粒子作者 HTTP 有限验收

`m08_author_probe.py` 是独立工具，继承 M07 的匹配发布校验、loopback 隔离 profile、认证脱敏、完整 PNG 解码、实际 Vulkan Worker／ICD／driver／DRM 观察、凭证扫描和 PID/startTicks 排水。它不修改旧工具或仓库样例。工具写入不代表已经实跑通过；集成者在当前匹配 Server/Worker 发布后串行执行：

```bash
python3 backend/tools/backend-script-render-upgrade/m08_author_probe.py \
  --dist /absolute/path/to/matched-dist \
  --output /absolute/path/to/new-m08-http-evidence \
  --icd /usr/share/vulkan/icd.d/radeon_icd.json
```

- 逐文件核对 CursorTrail／VectorTrail／EventBurst 实际 manifest 原字节和 SHA，HTTP 上传、检查、创建草稿。配置固定种子和参数；另存真实 C# 修订，指定 sourceRevision 异步编译，等终态，再显式采用。产品版本和源码提交取实际 deployment.json，不写死。
- 使用真实 12 秒多行作者试样的 project 目标，在 1.25／6.25／10.25 秒做固定乱序与重复 seek；每帧检查完整 author source、generation、requestSeq、timeUs、后端、PNG 大小／SHA、完整 RGBA 解码和诊断页，重复时刻像素一致。该有限试样不代表三分钟／十分钟时间线验收。
- 每包实际修改 sizePx，验证成功 CAS 与陈旧 CAS 拒绝；原预览仍使用旧输入且重复帧不变，旧 PNG 在有效租约及原输入内重新下载解码不变；显式替换 input 后才切换草稿修订／generation，GPU 保持，实际像素变化。
- CursorTrail 另存预算负例修订，只将其真实源码的速率固定为 60000/s；经指定源码编译和显式采用，先取得零时刻帧，再查询一秒触发真实 50000 出生记录预算。要求失败终态、`EmissionRecordBudgetExceeded`、准确运行 occurrence／instance／包／采用源码／导出／artifact／时刻，且失败代次不发布成功或上一代帧。原仓库参数范围和样例保持。
- 成功时关闭本次预览、删除本次草稿并排空 Worker；失败时保留隔离 profile 的草稿便于定位。无论结果如何停止所属 launcher，验证观察到的进程身份已消失并扫描证据凭证。

证据入口为 `summary.json`、`deployment-reference.json`、`fixtures.json`、`author-identities.json`、`author-frames.json`、`particle-runs.json`、`particle-budget-failure.json`、`gpu-workers.json` 与 PNG／decode／请求响应文件。此工具不验收浏览器显示、桌面共享纹理、完整作者验证、保存库重开、跨平台或性能；正式导出由独立 Export 回归与后续实际工作流证据登记。
