# M03 HTTP 同输入 seek 复用验收记录

日期：2026-11-03。状态：**功能实现收尾；正式发布和性能验收待补**。
范围：浏览器 HTTP 静态帧预览的固定输入 Worker 复用；GPU 持续流备用池属于 M04。

## 实现与边界

产品 `0.110.6`，合同 `1.42.4`。本机证据根为 `.local/script-sdk-m03-20261103/`。

- 复用键保留工厂／快照对象、来源修订、尺寸、质量、透明、布局和 prepared frame；
  HTTP seek 的公开代次继续递增并隔离旧帧，不再单独触发 Worker 销毁。
- 只有完整校验并消费的协作取消回执或确实未发送的请求可复用；软取消后在同一进程重开 Runtime，
  包会话沿用 M02 固定产物租约。硬超时、错误回执、作者错误和进程失败仍终止旧会话。
- 失败清理保留实际所有者和首错，允许后续重试；清理成功前不创建替代会话或释放最后输入引用。
  关闭失败保持 `closing` 并返回既有 `internalError`，新请求可重试，只有真实清理成功才返回 `closed`。
  旧输入清理持续失败时拒绝再次换代，避免积累保留租约。
- v1 时间线重放、控制器求值规则、公开 HTTP 路由／DTO 与 500 ms 控制节奏保持。
  本节点不声称长跳转重放成本已经消失，也不覆盖 SDK v2。

## 构建与回归

集成树的 Runtime.Hosting 128、Scripting 293、Worker 139、Backend.Rendering 280 项通过，
日志与 TRX 为 `bounded-final-*`。Hosting 1517 项通过；Server 503 项通过，三项 Ruby 真发布测试待匹配发布后运行。
完整 solution 在周期清理修复后重新构建，0 警告、0 错误。
`bounded-retention-red` 实际执行新增测试并失败，加入拒绝继续换代后 `bounded-retention-green` 通过。

隔离集成阶段的 retention 旧实现四项红灯与修复后相关生命周期 41 项绿灯保留在
`.local/m03-m02-integration/.local/script-sdk-m03-20261103/`；最终集成回归另行登记，
不将隔离结果冒充最终发布证据。

静态关闭首错回归先在旧实现得到排水 IOException，恢复后保留原 WorkerProtocolException 并可重试排水；
`static-cleanup-red/green` 均实际执行。周期过期清理回归同样已确认旧路径红灯与修复后绿灯：
单客户端释放失败不会阻断其他客户端，失败输入保留下一周期重试。

真实包 Worker 使用脚本已进入 marker 和取消令牌屏障验证执行中取消；静态真实 Worker 的新增测试
只覆盖调用前取消和后续同进程正确帧，静态协作 ACK 的形状／恢复由确定性协议 fixture 覆盖。
历史测试包含两代 ALC 可回收；八次 Runtime 重开 WorkingSet 的有限观察不设置任意内存通过阈值。

## 真实 HTTP 负例

同一 `cancel_probe.py` 已对匹配 M02 发布运行：脚本进入阻塞、旧控制器观察取消、恢复帧 RGBA 一致，
但 PID `746342` 换为 `746357`，在“同 Worker PID/startTicks”断言处按预期失败。
`http-cancel-m02-red/` 保留 80 HTTP 请求、491 断言、195 文件凭证扫描和所属进程排水证据。
该负例证明探针能区分旧版重建行为，不能代替 M03 正向验收或性能结论。
