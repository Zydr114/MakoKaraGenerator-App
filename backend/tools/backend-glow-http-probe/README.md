# 静态 Glow 真实 HTTP 探针

> 合同 1.50.0（产品 0.112.85）起样式改为两态形状。本探针按 1.49.x 及更早的单态样式编写（`unsungFill`／`stroke`／`shadow`／`glow` 与旧模板属性名），只能对照当时的发布物运行，没有随 1.50.0 迁移。

本探针使用匹配发布的真实 Server/Worker、新建隔离 profile 与自有 WAV/LRC 工程。它复用 `backend-web-host/smoke.py` 和 `backend-preset-assignment-http-probe/probe.py` 的启动、认证、工程、保存重开及 PID/startTicks 排水辅助，不运行旧探针的完整测试集。输出目录必须是 `backend/artifacts/` 下尚不存在的新目录；不修改用户工程或 profile。

从 `backend/` 目录执行：

```bash
python3 tools/backend-glow-http-probe/probe.py \
  --dist "$PWD/artifacts/<matched-server-worker-publish>" \
  --output "$PWD/artifacts/<new-glow-http-evidence>"
```

前提是同一源码构建的 self-contained Linux Server/Worker 发布目录及 `deployment.json`，Python 3 标准库、`/usr/bin/ffmpeg`、宿主所需字体及 native 依赖。无需从脚本构建或发布；主任务负责合同冻结、构建与匹配发布。启动器使用随机 loopback 端口，bootstrap 文件权限 0600，凭证不写 URL；只向本次启动器发送 SIGTERM，并校验捕获的进程身份排水。`--output` 不覆盖已有证据。

范围：工程、分支、行、元素四层的 Glow 整体和四个叶字段；显式 null、restore 与继承；非法 opacity、缺失字段、叶字段 null、父子冲突、双目标原子拒绝、过期 revision、跨客户端隔离、no-op、undo/redo、保存和重新打开。每次新增负例后以原 revision 读取完整样式，验证未发生部分写入。使用真实预览 `202` 后等待对应 generation/sequence 的 `ready`，认证下载完整 PNG，校验 SHA/长度并解码 RGBA；保存基线、Glow、关闭、undo 和重开等有限 PNG。透明画面额外断言 Glow 在原零 alpha 区域产生红色扩散，不透明画面断言像素变化；关闭和重开使用严格像素对照。

`summary.json` 包含帧摘要、外扩像素数、断言和部署清单摘要；HTTP 响应证据及进程排水记录与 PNG 一同保留，所有输出扫描本次凭证。失败时保留目录，不声称通过；修复后换新目录重跑。该探针不是独立渲染算法 oracle，也不证明浏览器、跨平台、所有字体或性能；图层和边界条件须由定向渲染测试另行验证。
