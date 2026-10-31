# 结构优化正确性与日志验收证据

生产`5802ac1`，匹配发布`103ba2c`，产品0.107.0／合同1.39.0。完整范围见[验收记录](../../backend-gpu-structural-performance-optimization-acceptance-20261028.md)。本目录保留正确性与HTTP验收时快照，压缩包未改写。用户随后恢复性能测试并已完成，见[正式性能证据](../20261028-structural-optimization/README.md)。`performance-deferred.json`保留当时延期决定，恢复及完成记录见新目录`completion.json`。

- `acceptance-summary.json`：最终回归、发布审计、两组成功HTTP及首轮失败的独立计数。
- `telemetry-http-audit.json`：保存日志的结构、阶段、心跳与丢弃计数审计，带四个输入SHA；只读分析，不是额外HTTP运行。
- `evidence-index.json`：79个文件的长度与SHA，及压缩包指纹。
- `acceptance-evidence.tar.gz`：回归日志／TRX、命令与输入清单，匹配发布清单，两组成功及一组失败HTTP摘要／硬件／排水／日志及原始证据索引，有独立文件的有效变异和工具检查。原始HTTP请求响应、媒体、PNG、BGRA仍保留于本地artifacts并由HTTP清单索引，不在本精简压缩包内。

压缩包SHA-256：`2431a004887149ce81a88e1c105c5b9bf15c12518680100aacaa2d908825472c`，1,705,455 bytes；归档后逐文件解压重算SHA已通过。HTTP原probe完成凭证扫描及身份排水，归档不冒充额外凭证扫描。公平性变异与早期17/17未找到独立文件，不编造路径；最终Rendering回归包含恢复后的公平性测试。
