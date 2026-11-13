# M13 阻断补充：Aorist 预览与导出修复

状态：修复及本次范围内验收完成，待用户验收；未启动 M14。

日期：2026-11-14。分支 `dev/script-sdk-phase-two-structure`。
本记录补充 [M13 记录](script-sdk-render-m13-evidence.md)，不覆盖历史验收，也不执行 M14。

## 1. 故障与实施顺序

原工程 `Aorist - nayuta & ARForest` 在 1080p、30 fps 下，第 1287 帧（零基索引 1286，42,866,666 µs）
第一次触发 `SourceMaterialFailureException → ImageSurfaceBudgetException`。冷启动直接跳转到该时刻也失败，
不是必须累积播放才能出现的泄漏。申请时已有 62 张表面，再申请 3 张超过原上限 64；
字节为 64,879,032 + 2,073,708，尚未达到 512 MiB。降到 720p／360p 仍撞数量上限。

源材质错误被导出层包装成 `WorkerExportRenderFailed`；UI 的约 1000 帧是进度采样值，
不是精确故障帧。预览 Worker 外层异常路径报告 `drained=false`，桌面原 fail-stop 策略退出整个应用。
调查原始证据：`.local/investigation-aorist-20261114/investigation-report.md`。

按用户授权先修复这条共享渲染／导出／桌面故障链，完成补充验收后再考虑 M14。
删除 v1 不会自动修复共享表面预算、对象存活期或桌面退出策略，混合实施会增加归因困难。

## 2. 修复范围

| 提交 | 内容 |
|---|---|
| `d33f951` | 表面数量默认 64 → 256，保留 512 MiB 字节预算、不预分配；按成员、唱态和注音归属判空；扫字合并后释放已无消费者的原始捕获及图产物 |
| `d7bc8ae` | 已知预算失败等待真实租约排空；未知完成状态隔离预览并终止所属 Worker，保留编辑器，禁用本进程后续 GPU 接入 |
| `3c2ac9e` | 导出保留源材质身份、阶段、时刻、稳定错误码与数量／字节预算；宿主对照固定工程验证；前端显示诊断 |
| `540df38` | 产品 0.112.89、合同 1.52.0；当前 CLR metadata 冻结、校验与前端合同副本同步 |
| `6b3a84a` | 故障验收分析器兼容真实 `WEB_BACKEND_STOPPED` 结构化退出日志，保留旧日志兼容 |

数量预算提供余量，不以优化后必须低于 64 为目标。有限判空保留材质可能自主产墨的条件，
整行无原样基础字必须满足完整 token 对应与独立几何分区；需要下游历史的图仍保留其产物。
未知 GPU 引用有界保留，不伪造 ACK、不把进程终止等同于 GPU 排空；重启应用后才重新开放该预览能力。
源材质诊断不虚构脚本 occurrence，不公开内部异常堆栈，失败任务不发布成片。

## 3. 构建、合同与回归

最终源码完整构建零警告、零错误，桌面构建与匹配自包含发布成功。
合同校验为 115 paths、128 methods、667 schemas、3930 cases（2839 negative）；仅补 4 个新形状夹具。
本次校验报告为 `backend/artifacts/aorist-export-20261114/contract/validation.json`；
`backend/artifacts/api-contract/validation.json` 是旧 1.51.1 报告，不作为本次证据。
保留既有历史门禁，新增回归聚焦本次判空／存活期、预算故障隔离与错误身份传递，没有引入大型新测试工程。

| 检查 | 结果及边界 |
|---|---|
| 后端全量 | 首轮 8514 通过、3 失败；3 项均为 Ruby HTTP 测试缺少指定发布物环境变量，非产品断言失败 |
| 最终 Server 定向 | 补齐同发布 Server／Worker 路径，RubyTimelineTransport、PreviewTransport、GpuStreamFailure 共 466 通过，包含首轮 3 项 |
| 最终 Worker 定向 | StaticAnalysisExportTask、GpuStreamSession 共 36 通过，覆盖最终协议校验改动 |
| 真 GPU 渲染回归 | `MAKO_GPU_SMOKE=1`，PerfFixturePackage／WipeFeather／GraphPixelCheckMode／ImageSurfaceBudget 共 120 通过 |
| 前端／桌面／launcher | 前端单元 4486、desktop 28、launcher 8 通过；前端类型检查通过 |
| 回归有效性 | 成员判空用例在原渲染文件上失败；去掉预算故障分类后 2 项失败；原导出包装返回通用码时新诊断用例失败，恢复修复后通过 |

全量首轮并非“一次全绿”；最后两处协议防混用校验在最终完整构建与上述定向回归中复核。
原始日志集中于 `.local/fix-aorist-20261114/`，包括 `build-final.log`、`full-tests.log`、
`final-server-targeted.log`、`final-worker-targeted.log`、`gpu-render-tests.log`。

## 4. 匹配发布与真实工程验收

发布目录 `.local/backend-dist/20261113T191727523Z`，源码 `540df38e4c2334560e7148a00f0defaeb96a75ce`，
`sourceDirty=false`，产品 0.112.89。最终核对 1229 个后端源码输入、34 个 native 输入及 native 产物摘要均匹配。
后续仅修改验收脚本与记录，不改变已验收运行产物。
发布与原工程核对见 `.local/fix-aorist-20261114/final-provenance.json`。

实际桌面测试通过 1920×1080 GPU 预览的冷跳转、反向及重复跳转：42.866666、42.833333、42.859999、42.9、50、250、42.866666、0 秒。
随后从 0 播放到 253.376667 秒，累计 1907 个已完成绘制帧，无预览错误、无应用退出。
这是并行验收负载下的稳定性记录，不作为实时帧率或性能基准。
显式关闭时脚本 25 秒等待窗口超时，之后日志确认后端 exitCode=0、自有进程停止；另存复核文件确认观察到的 8 个 PID 全部消失。
证据：`aorist-fix-full/summary.json`、`process-drain-review.json`（相对上述修复证据目录）。

32 MiB 限制副本在真实桌面触发 `ImageSurfaceBudgetExceeded` 后，预览没有永久禁用，歌词编辑器仍可进入；显式关闭退出码 0。
未知 Worker 丢失的真实故障注入保留 2 个未知引用、ACK 7 → 7、imported 9 → 9；Worker 终止确认、主进程身份不变、编辑器存活、再次接入被拒绝。
显式退出后所有自有进程消失。证据分别为 `aorist-fix-budget/summary.json` 与
`.local/investigation-aorist-20261114/recovery-worker-loss-short/report.analyzed.json`。
后者基于原始日志离线重算结构化停止门禁，原报告与日志保留；首次长音频夹具未进入注入阶段，不计为故障验收成功。

32 MiB 限制副本的真实 HTTP 导出按预期失败，稳定错误码为 `ImageSurfaceBudgetExceeded`，
失败报告含真实源材质身份与预算、下载大小及 SHA-256 与报告引用一致、没有虚构 occurrence、没有成片；工程关闭与宿主停止成功，440 个证据文件凭证扫描通过。
证据：`http-budget-failure-final/summary.json`。首轮探针在已通过故障断言后误覆盖局部变量，关闭步骤报错；原失败证据保留，修正后在新目录重跑。

浏览器以独立 Chrome profile、只读工程和普通 HTTP 静态预览取得 42.866666 秒、50 秒的 1280×720 PNG，
页面没有 desktop bridge，未走共享纹理桌面入口。两帧分别为 449284、542472 字节，
PNG SHA-256 分别为 `0722a54921a72a627593ae1beb5654f6e4ff947d8a222186dfbc74de92575ed8`、
`0a3595288d9f94434c71c4efcb424c2545eaae57953e4217025179ba6f98a0fe`；目检背景与歌词正常。
关闭工程、浏览器、后端成功，网络证据无凭证 URL，公开 JSON／日志扫描零违规。
证据：`browser-preview/summary.json`、`isolated-lifecycle.json`、`acceptance-audit.json`。
首次复用桌面服务的探针因响应 envelope 取值错误未完成，保留 `shared-first` 记录；正式结果来自新隔离实例。

真实 HTTP 全曲导出已成功：1920×1080、30 fps、7601 帧，带音频 MP4；
下载大小与 SHA-256 均匹配任务发布的 artifact，ffprobe 逐帧计数为 7601，
音频／视频时长均为 253.366667 秒，整段 FFmpeg 解码零错误。任务运行 738.593 秒，
期间与其他验收并行，不能作为独占性能对照。工程关闭及宿主停止成功，1776 个证据文件凭证扫描通过。
证据：`http-export/summary.json`、`fresh-shutdown.json`、`aorist.mp4`。

## 5. 资源与性能对照

使用相同 Aorist 内容、1080p／30 fps／7601 个时刻和像素读回路径，对照原 0.112.88 发布 DLL（只将数量预算临时设为 256，以便跑完全曲）与当前 0.112.89 匹配发布 DLL。
当前运行使用默认预算，未启用 graphPixelChecks，也没有临时提高内存或数量上限。
测量前结束本任务其他 GPU 验收；这不是带编码器的导出耗时，也不是跨机器性能承诺。

| 指标 | 原实现，仅放宽数量预算 | 当前修复 |
|---|---:|---:|
| 全曲完成帧 | 7601 | 7601 |
| 活动表面峰值 | 126 | 107 |
| 估算像素字节峰值 | 231,789,600（221.05 MiB） | 198,211,936（189.03 MiB） |
| 渲染循环耗时 | 544.551 s | 470.294 s |
| 进程整体耗时 | 545.807 s | 472.860 s |
| 子进程 max RSS | 1,334,792 KiB | 995,004 KiB |

本次整曲仍需 107 张表面，超过旧 64 张限制，说明不能只做优化而不放宽上限。
与只提高上限的原实现相比，数量峰值降低约 15.1%，估算像素峰值降低约 14.5%，
本机本次总耗时降低约 13.4%，max RSS 降低约 25.5%；未见因提高上限导致的明显性能或内存灾难。
26 次释放后观察均为 0，结束后测量进程退出。
最终发布 DLL 的 42.866666 秒与 50 秒 RGBA SHA-256 与原发布仅放宽预算的对应帧逐字节一致；
分别为 `D2E33F2F86CF05A8F89D011A6D75B1B7A1A5E657CCDBBA7A5159D7CBE7FA23B2`、
`539ECB851B344BBF613353226402B1FCF3FD616D04A7489D496678DDF7CD1804`。
这是两个关键时刻的像素一致性，不能当作整曲逐帧像素比较；完整渲染语义另由既有 GPU 回归覆盖。
证据：`current-release-pixels.log`、`resource-comparison.json`。

RSS 包含运行时、解码器与驱动等，不等于表面预算或总显存。每 300 帧及最后一帧记录释放后的活动计数；
峰值由预算会话连续累计，采样结束时的 0 不能被描述为逐帧跟踪所有驱动分配。
证据：`baseline-256-full.log`／`baseline-256-full-summary.json`、`current-release-full.log`／`current-release-full-summary.json`。

## 6. 工程数据与适用范围

原工程 13 个内容文件摘要全部保持原值。本仓库现有工程加载门禁要求精确 `applicationVersion`；
0.112.88 原件不能直接冒充 0.112.89 工程，因此测试使用 `.local/fix-aorist-20261114/project` 隔离副本，
只更新 `project.json` 的应用版本标记（及 JSON 排版），媒体、歌词、包和效果参数不变。
导出通过正式命令恢复历史任务的 253,377,667 µs 输出时长；没有静默迁移或覆盖原件。

可在仓库根目录执行 `pnpm desktop --session-name aorist-fix-full`，打开 `aorist-fixed` 复测已验收的隔离副本。
常规 `pnpm desktop` 仍使用原 profile，原 0.112.88 工程遵循现有版本拒绝规则；本修复没有擅自改变该规则。

本次真实平台范围为当前 Linux／Vulkan／Electron，未据此声称 Windows、所有素材组合或任意复杂工程均通过。
M14 尚未开始；本记录完成后仍由用户验收本次修复与 M13 发布候选。
