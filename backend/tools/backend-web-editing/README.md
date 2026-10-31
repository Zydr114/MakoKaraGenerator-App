# Web 后端编辑真实 HTTP 验收

使用 Python 3 标准库，继承 [`backend-web-host/smoke.py`](../backend-web-host/smoke.py) 的匹配部署启动、私有 bootstrap、默认 TTL 登记/resume、脱敏请求响应、终态 job 等待、SIGTERM 正常终止和观察到的进程身份排水。loopback 明确直连，不使用系统代理；不依赖 Avalonia 或声卡。

先用稳定发布入口生成**匹配当前源码的 Server 和完整 Worker**，然后将探针输出放在仓库忽略的 `artifacts/` 下**不存在的新目录**：

```bash
python3 tools/backend-web-host/host.py publish \
  --dist "$PWD/artifacts/web-editing/dist-s1-final1"
python3 tools/backend-web-editing/probe.py \
  --dist "$PWD/artifacts/web-editing/dist-s1-final1" \
  --output "$PWD/artifacts/web-editing/http-s1-final1"
```

编译检查：

```bash
python3 -m py_compile tools/backend-web-editing/probe.py
```

当前范围为 S1 三个新增 GET：`lyrics/branches`、`lyrics/window`、`settings`，以及这些读取依赖的既有命令/持久化链路。API 必须真实存在并成功调用；没有跳过能力或 mock 成功分支。探针检查：

- capability、真实短 WAV 上传和 Worker 准备时长，并观察实际 Worker 可执行文件身份；上传增强 LRC，检查零起点、原始正文/元素、缺失时间和时间状态。
- 精确修订和有界分页，包括末尾空页、完整页重组、重复/必填/负数/小数/溢出/越界查询；跨 client 和不存在会话的中立 404，不存在来源 404。
- 所有读取与拒绝保持工程摘要、dirty、历史、redo 和事件游标；原 `lyrics/lines` 仍给出兼容的最小投影。
- 两页之间实际改时后旧修订冲突，undo 恢复同内容后旧修订仍冲突，当前修订单调递增。
- 保存完整终态、关闭完整终态、打开完整终态后获得新 session；来源列表可以发现各来源，完整正文、稳定 ID、时间、注音、来源元数据和设置值一致。
- 自有持久化 fixture 覆盖多来源、FollowPrimary 关联及未合成的原时间、六种时间状态、完整 Unicode 注音、JSON number 比例/间距、权威行正文与 token 拼接不同、超 JS 安全整数的 Int64 字符串、有理帧率和透明底色。
- 自有大输入通过真正 HTTP 验证 413 `resourceTooLarge`、缩小窗口后完整返回、单行不截断。20000 token 加一行的 fixture 同时可能超字节预算；探针不将其宣称为隔离的项数门禁证明。

fixture **仅离线构造在探针自有的 profile 内**：由本轮 HTTP 保存且关闭终态后的模板复制，保留原始模板和旧不可变歌词，按当前 `lyricsFile` 的 SHA-256 寻址规范写入新歌词并更新工程入口；工程/schema/产品版本取自真实保存产物。fixture 身份和哈希写在 `fixtures.json`。这是读取验收输入，**不表示 HTTP 已提供新增来源或注音编辑**。所有工程及媒体只属于本轮输出；工具拒绝复用已有输出目录。

`summary.json` 只在全部断言、正常退出、进程身份消失及凭证扫描通过后写出 `passed`。输出包含脱敏 HTTP 请求/响应、完整终态响应、fixture 哈希、观察到的 Worker/进程身份及断言标签；bootstrap/access/resume 仅留内存，bootstrap 正常终止后删除。失败时保留现场并尽力走相同正常终止，不按名称杀进程、不删除用户目录。`--dist` 发布清单的源码/部署 SHA 是部署身份依据；应与本轮源码验证证据一并归档。

这些结果是后端真实 HTTP 的 S1 有限验收，不代替 Web 浏览器、渲染像素 oracle、S2/S3 新命令、完整 B3/B4 或性能/平台验收。

## S2 六个时间命令

独立入口保留 S1 探针的运行范围，须使用支持六个新命令的匹配 Server＋完整 Worker：

```bash
python3 tools/backend-web-editing/timing_probe.py \
  --dist "$PWD/artifacts/web-editing/dist-s2-final1" \
  --output "$PWD/artifacts/web-editing/http-s2-final1"
python3 -m py_compile tools/backend-web-editing/timing_probe.py
```

该入口继承 S1 的自有 profile、真实素材准备、合法 SHA 歌词闭包 fixture、完整投影、保存/关闭/打开终态、凭证扫描与进程身份排水。fixture 从本轮真实 HTTP 保存的模板生成，包含三个稳定元素、注音、FollowPrimary 来源、1000 元素来源和单独的长行输入；沿用模板 schema/产品版本。fixture 构造不表示新增了 HTTP 来源或注音编辑能力。

验收包括：

- `stampTokenStart` 的一帧估算区间/`estimated`，`stampTokenEnd` 相同端点的状态确认/`edited`，负播放头及超过真实音频的夹紧；仅打点响应包含 `timingResult.estimatedPlaceholder`。
- 清除、微调、整行平移及批量提交的精确微秒、状态、变更范围、实际共享历史名称；每种改变分别查询→一次 undo→一次 redo，完整投影恢复。连续同目标微调与批次分别进入独立历史。
- 零帧、零平移、重复清除、相同批次和相同打点端点的无变化请求保留修订、dirty、历史、redo 和事件游标，changeSet 为空。
- 六个 type 的完整回执重试：带符号/补零微秒及补零 expectedRevision 规范化后，旧修订重试返回原回执；改变各自派生字段、目标、截止时间或修订不能复用 requestId。
- 1～1000 上下界、完整 1000 元素成功查询/撤销/重做、重复目标/空条目/缺端点、数字/ID/形状/溢出/未知跨 type 字段与原五命令 nullable 兼容性；错误必须按预期 HTTP/code 拒绝，不把 500 或超时当作通过。
- 单独的合法长 Unicode ID fixture 验证完整默认转义 JSON 超过公开 `ProjectCommandLimits.MaxChangeSetBytes`（16KiB）时仍成功提交并保存回执：保留全部 `areas`，`branchIds/lineIds/tokenIds` 一起清空，`requiresFullRefresh=true`；重试/undo/redo 同样保持完整失效通知，不返回部分 ID 冒充完整。普通 1000 个 ASCII 目标仍必须返回全部精确 ID、`requiresFullRefresh=false`。
- 第二目标非法整批拒绝及完整状态不变；按输入次序逐个校验候选的拒绝/反向合法提交；已有行边界和元素不重叠约束不会自动扩行。领域拒绝的 `failureCode` 为共享 Application 的 PascalCase，解析/修订和旧命令拒绝省略此字段。
- 真实同时发出的两个同修订不同 requestId 只有一个成功；同 requestId 并发得到同一完整回执且只修改一次。跨 client 不可操作会话；明确编辑 FollowPrimary 来源保持关联与主来源内容。
- 长行中 batch 可以保留超过真实 2 秒音轨的合法元素时间；单元素微调及旧端点命令仍遵守服务端准备的音频上界。随后完整保存、关闭、打开并比较所有来源/歌词/注音/设置值。

`http-elapsed.json` 记录各次真实 HTTP 的本机短样本，普通调用包含客户端验证和日志开销，并发调用记录网络请求时段；不设性能门槛，不作为吞吐、延迟保证、性能或跨平台验收。`summary.json` 的成功与计数仍以所有终态、断言、正常排水及包含最终 summary 精确字节的凭证扫描为条件。此入口不代替浏览器或完整 B3/B4 验收。
