# M03 / M04 有限三轮 A/B 准备稿

本目录保存可复现的有限 A/B 与独立初始化诊断工具。实际运行结果与失败记录见
`docs/plans/script-sdk-render-m04-evidence.md`；本文件只定义步骤，不把准备完成当作运行通过。
本轮有限 A/B 得到的结论为 `approved-finite-three-round-action-ab`；原 runner 的
`finite-correctness-not-performance-acceptance` runClass 保留。它不替代 formal P3 的
两段 180s、600s lifetime、完整吞吐/新鲜度/长时资源门槛。

## 固定比较口径

- M03 为冻结的匹配 Server/Worker/native dist；M04 为完成集成与正确性验收后的另一冻结 dist。
  两者分别使用 App 内独立归档/worktree、独立 profile、loopback 端口与 session。
  归档 sourceCommit、版本、deployment 全量文件 inventory/hash，以及源到部署对应关系。
- 两侧使用同一个 `finite-action-timing.patch`；patched runner、desktop main/preload、frontend
  bundles、Electron、fixture 内容与 ordinary scheduler flags 必须逐字节一致。
  runner 的相对导入依赖 `scripts/` 位置，不能直接从本 scratch 目录启动。
- 每轮原动作数保留：pause20、seek20、input replacement20、UI resolution10、attach20、
  public resize+seek10。控制节奏仍为500ms。仅添加记录，不更改生产控制、等待条件或循环数。
- 主计时为 CDP action invocation 经 bracket 映射至 main monotonic，到当前六身份同时匹配的
  canvas-drawn + 实际 native allReferencesReleased 的最早原始完成时间戳。
  100ms采样等待、取得3帧正确性证明、记录时读取/proc与动作后旧Worker排空不作为终点。
- input replacement 在调用修改前读取 old generation，之后必须证明原 stream / 新input /
  返回的新 sourceRevision；帧本身不虚构 sourceRevision 字段。
- pure attach 从旧Worker已确认退出后、返回orchestration调用前开始。另列 detach-reattach
  全周期观察，该数值包含原流程中显式的旧PID排空等待，不对它套纯attach的优化解释。
- public resize+seek 只取新input且匹配seek后的 timelineResetId 的完成帧，避免误取seek前帧。
- 原 `action.elapsedMs` 是从先前snapshot到等待返回的正确性区间，含轮询/多帧，禁止作为延迟A/B。
  `completionToObservationMs` 只说明采样滞后，不与完成延迟相加。

三轮合并后，input replacement、pure attach、UI resolution、public resize+seek各自的
statistics.median（偶数取中间两值平均）和 nearest-rank p95满足 `candidate <= baseline * 1.10 + 5ms`。每轮值用于观察漂移，
不新增每轮硬门禁。原始时长、invocation clock uncertainty上界、所有原始样本都一并报告。
drawn/native样本缺失或身份不完整直接拒绝本轮，不用较少样本作通过结论。20次一轮、合并60次
的p95只是此有限样本的观测分位，不宣称总体尾延迟估计。

## 运行安排（获得运行指令后再执行）

先完成CPU/正确性验收与两侧准备，停止本任务所属构建/探测进程；确认采样期间没有并行构建或GPU测试。
保留用户既有App进程，并在报告中记录并发负载，不擅自关闭用户进程。

在各自隔离worktree根将同一patch应用到 `scripts/gpu-preview-p3.mjs`，确认同一patched SHA256。
每个worktree `.local/backend-current.json` 指向本侧冻结匹配dist。每次运行使用新session/profile。

```sh
git apply /mnt/Data/Program/MakoKaraGenerator-App/backend/tools/backend-gpu-worker-pool-acceptance/performance/finite-action-timing.patch
node scripts/gpu-preview-p3.mjs --finite --provider runtime --ordinary-scheduling \
  --session-name m04-ab-b1 --port 15741 --cdp-port 19241 \
  --output .local/m04-ab/b1
```

这段命令在 baseline 根运行B1；candidate根改为C1及其独立端口/session/output。
六次顺序为 **B1、C1、C2、B2、B3、C3**，同一时刻只运行一次。各次间恢复相同桌面可见状态；
不在计时运行期间重建、读取像素或截图。每次退出检查launcher exit0与全部已观察PID/startTicks消失。
若一次运行失败，保留证据并说明原因；重跑整组相应paired round，不能挑样本删尾部。

离线计划文件用已冻结的真实commit和绝对运行目录填充，不使用示例值作证据：

```json
{
  "baseline": {"sourceCommit": "M03_COMMIT", "runs": ["/BASELINE/.local/m04-ab/b1", "/BASELINE/.local/m04-ab/b2", "/BASELINE/.local/m04-ab/b3"]},
  "candidate": {"sourceCommit": "M04_COMMIT", "runs": ["/CANDIDATE/.local/m04-ab/c1", "/CANDIDATE/.local/m04-ab/c2", "/CANDIDATE/.local/m04-ab/c3"]}
}
```

```sh
python3 backend/tools/backend-gpu-worker-pool-acceptance/performance/compare-finite-ab.py \
  --plan .local/m04-ab/plan.json --output .local/m04-ab/comparison.json
```

比较器只读六份manifest/events/report并写新比较报告，不启动应用。它重新从原始事件寻找最早
六身份完成帧，拒绝重复/缺动作、无exact过程排空、缺完整事件、非finite或不同工具/fixture。
报告保存输入文件SHA256、比较器SHA256、每侧部署provenance、三轮原始记录和合并分布。

## 实际硬件初始化的独立对称诊断

时间A/B使用未增加初始化日志的归档。另从相同M03/M04源和构建选项生成各自诊断归档，
使用**完全同一份插桩工具、helper与事件schema**，在两侧应用并记录其SHA256；最终工具hash不一致
则不能比较。诊断dist的管理程序集hash不同于未插桩dist，应分别归档，禁止混入上面的时间比较。

插桩位置必须在真正成功的 `physical.CreateDevice(...)` 返回后和
`GRContext.CreateVulkan(...) ?? throw` 成功后各加一次事件。不要在factory调用点推断次数，
不要读取单侧M04已有counter便假设M03次数。诊断记录至少包含精确PID/startTicks、单调序号、
事件类型和实际device/context累计计数。失败的context构造仍应保留先前device成功记录。

在PackageRenderSession的业务open入口（M04 Bind之前、decode之前）记录business-before，
真实factory.CreateAsync成功后记录business-after；GPU Open成功前再记录stream-opened。
同时记录WarmupBinding原始preparationId/initializationId、Lease.Admission分类与原始Worker身份。
两侧使用相同真实fixture与有限动作，从原始事件按每个Worker分段，列出：

1. 启动/设备检查阶段实际device/context次数，数量完全按实测，可能存在独立availability检查进程。
2. 业务open前后device/context计数差值。M04 warm-bind核心要求为两项delta都为0，原pair标识不变。
3. stream-open阶段差值与每次business创建的新Worker身份。不得把不同PID合并成同一设备计数。
4. 非warm-hit、opening-wait、冷miss与失败回收样本单独标明，不能替换为未记录的warm-hit。

诊断只写私有归档，不在公共HTTP协议、renderer或URL暴露令牌/claim capability。
独立插桩工具源码位于 `init-diagnostic/`；构建、实际计数及诊断结果另见节点验收记录。
`apply.py`只向App `.local/`内隔离源码写同一helper与actual-site插桩，所有锚点预查通过才写入；
记录工具及before/after源码SHA256。诊断输出环境只传给单独诊断运行：

```sh
python3 backend/tools/backend-gpu-worker-pool-acceptance/performance/init-diagnostic/apply.py \
  --root .local/M03-DIAGNOSTIC-SOURCE --manifest .local/m04-init/baseline-tool.json
# 在隔离诊断源码构建匹配诊断dist；完整记录命令/版本/部署hash，运行时显式设置私有输出目录。
MAKO_GPU_INIT_DIAGNOSTIC_DIR=/ABS/APP/.local/m04-init/baseline-events \
  node scripts/gpu-preview-p3.mjs --finite --provider runtime --ordinary-scheduling \
  --session-name m04-init-old --port 15841 --cdp-port 19341 --output .local/m04-init/run-old
python3 backend/tools/backend-gpu-worker-pool-acceptance/performance/init-diagnostic/analyze.py \
  --baseline-manifest .local/m04-init/baseline-tool.json --candidate-manifest .local/m04-init/candidate-tool.json \
  --baseline-events .local/m04-init/baseline-events --candidate-events .local/m04-init/candidate-events \
  --output .local/m04-init/comparison.json
```

事件目录必须预先存在，helper写出错即诊断失败；每份JSONL按PID/startTicks命名，不携带token。
实际成功device/context事件用pairId相关联，Runtime probe与业务factory直接根据原device对象记录同pair，
因此可从两侧原始事件核对new业务选中的是业务之前真正probe过的原device/context。

## M04部署身份成本

准备边界的ConfigurationIdentity现为实际Worker目录完整闭包SHA256，并在admission/availability前后复核。
不使用manifest内容代替实际DLL/ref/native文件，不新增statx或时间戳缓存。记录真实文件数与bytes；
其成本自然进入上述动作A/B。只有实测证明它造成门槛瓶颈后才另立有证据的优化，当前不预设目录大小。
