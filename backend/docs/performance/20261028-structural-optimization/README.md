# 0.107.0结构优化正式性能证据

用户恢复测试后，于2026-10-28 17:31:33～18:15:47（Asia/Shanghai）完成本机固定1920×1080 Final短批次。生产旧版`10bad12`、新版及工具`5802ac1`；四份隔离源码与执行体重查SHA一致。完整实现、回归、HTTP和性能边界见[验收报告§5](../../backend-gpu-structural-performance-optimization-acceptance-20261028.md#5-正式性能与监控开销)。

七标签及两组原版／trace对照全部有效：246进程／4560项目帧／246小probe，1956对旧新、608对原版／trace、432对监控开关同后端完整BGRA与诊断相同，632 trace通过。123个硬件Vulkan进程，全部Runner自然退出。此前[延期记录](../20261028-structural-optimization-acceptance/performance-deferred.json)保留历史安排，本目录`completion.json`记录恢复完成。

| Vulkan场景 | 旧中位 ms | 新中位 ms | 中位降幅 | 新主矩阵最大 ms |
| --- | ---: | ---: | ---: | ---: |
| graph | 99.384 | 34.892 | 64.89% | 51.219 |
| material | 1090.573 | 285.524 | 73.82% | 346.453 |
| combined | 1283.990 | 338.909 | 73.61% | 373.706 |

500ms只在固定五轮主矩阵稳定计量帧范围成立；material／combined首帧最大639.529／776.181ms，独立监控off的combined另有530.002ms计量帧。Vulkan particle主矩阵中位上升3.19%，CPU轻负载也有小幅回退，完整表未省略。

监控三轮的Vulkan combined summary／detailed相对off中位增加35.053／36.846ms（10.70%／11.25%），sink为本地JSONL，不含Server转发。36个开启监控进程摘要覆盖432帧；详细模式输出189条帧明细，限速丢弃27条且计数回显。不可称监控无开销或日志永不丢失。

## 文件与复核

- `compact-performance-comparison.json`：主wall、每轮中位、创建／首帧、seek／透明、托管分配／GC、trace次数／字节和监控差值。
- `evidence-index.json`、`evidence.tar.gz`：587个原始JSON／JSONL／CSV／日志、源与构建指纹、插桩补丁和环境记录；归档程序复核全部来源、矩阵、输出相等及进程退出。
- `orchestration-index.json`、`orchestration-evidence.tar.gz`：33个编排脚本／日志、恢复检查、每标签及86次约30秒间隔负载记录、主代理和Sol独立派生复核。
- `completion.json`：16条编排命令全部exit0，915个生产输入稳定，两包620个文件逐项解压重算SHA通过，有限凭证模式静态扫描无命中。

| 压缩包 | bytes | SHA-256 |
| --- | ---: | --- |
| `evidence.tar.gz` | 1,210,306 | `a22eb349230142e4affca5f5f812d92a6c16172bf5c937f9e9638bb4d38572b3` |
| `orchestration-evidence.tar.gz` | 176,842 | `3346ddd0649a41eb8b4cfd9a0513aae0bc9b59b93138cb2b5bc507717714f5a2` |

进程快照未发现已知游戏／基准负载，但不是连续独占硬件证明；普通桌面、电源动态调节和缓存均保留。trace阶段包含同步及嵌套，逻辑请求字节不是物理总线流量。凭证静态模式检查不替代此前HTTP的实际token扫描。本轮未重跑HTTP、未修改生产代码；不扩大为浏览器、跨平台、长片或GPU编码结论。
