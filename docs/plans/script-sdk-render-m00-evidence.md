# M00 脚本与渲染基线记录

日期：2026-11-03。分支：`dev/script-sdk-render-upgrade`。
状态：CPU／HTTP／Runtime 基线已采集；桌面暂停竞态已发现，完整有限流程尚待关闭。
本记录对应[验收清单](script-sdk-v2-render-acceptance.md)，不代表 M01–M06 已验收。

## 身份与正式门槛

- 基线产品 `0.110.2`；合同 `1.42.2`，协议 `1`。工程／歌词 schema `29/18`、
  包 `2`、SDK `1.0.0` 保持；本轮不实现 SDK v2。
- Runtime 冻结生产源码为 `4666dff977cc516424fa232598fe608c55f300cd:backend`，
  归档包含 950 项生产输入、37 项样例输入。基线与插桩 Runner 的 Release 构建通过。
- HTTP／桌面使用本 App 的匹配部署 `.local/backend-dist/20261103T082755358Z`，
  其清单记录来源 `0be255d3197d52286341cd62080f352b317474b5`，与基线的生产输入逐项一致；
  部署清单 SHA-256 为 `4f5990b59df3c5fffe305e14b61d0271517173906364a8b8be62a65d4b9dbc51`。
  后续产品修改必须重新发布，不能沿用此结果。
- .NET SDK `10.0.114`、Node `26.13.0`、pnpm `11.7.0`；本机 GPU 为 Radeon 880M，
  RADV STRIX1、Mesa `26.2.5-arch3.1`、Vulkan `1.4.354`。
- 用户已确认：本轮同场景／设置三轮 A/B，中位数和 p95 均不得超过基线的 `110% + 5 ms`；
  每项优化还须证明真实编译、进程重建、等待或复制的对应成本下降。像素／正确性门禁保留。
  原计划 50／150／200 ms、1.5 s、16 ms 仅是后续阶段的候选值。

## 构建与回归

证据根：`.local/script-sdk-m00-20261103/`，原始日志／profile 不纳入 Git。

| 检查 | 本轮结果 | 证据 |
|---|---|---|
| 后端解决方案构建 | 0 警告，0 错误 | `backend-build-final.log` |
| 后端初次全量 | 14 测试项目；6507 项，6500 通过／7 失败／0 跳过 | `backend-tests.log`、`test-results/` |
| 修复后定向回归 | Rendering 27、Server 4、Export 2，全部通过 | `baseline-repairs.log`、`export-repair-final.log`、`retry-results/`、`repair-final/` |
| 前端 | typecheck 通过；44 文件、3927 项单元测试通过 | 基线日志 |
| App launcher | 7 项通过 | 基线日志 |
| 性能工具 | 21 项 Python 回归通过 | `backend/tools/backend-render-performance/test_analysis.py` |

7 项初始失败均已解释并处理：旧静态快照缺少必需 Glow 字段，测试改为拒绝旧格式并核对当前
格式往返；样式字段表从 21 项更新为实际 26 项并显式核对名字；两个 Export 测试原本依赖
已过期 AGENTS 文案，改为核查实际项目引用及命名空间边界；三个 Ruby HTTP 测试补上匹配发布路径。
修复提交 `3ab3dfb` 只修改测试，没有放宽产品格式或像素语义。全量初跑加定向修复复测，
不写成“修复后的同一次全量 6507 项通过”。

首次构建还发现 SDK SourceLink 不支持本机 Git 的相对 worktree 配置。已在 `.local` 备份
元数据，将本 App 的 worktree 指针修复为绝对路径并移除该本机扩展；没有改动其他 worktree 源码。

## 真实 HTTP 与作者流程

| 流程 | 结果 | 证据目录 |
|---|---|---|
| 通用 HTTP | 88 请求、537 断言；194 文件凭证扫描；自有进程排水通过 | `http-baseline/` |
| W8 作者流程 | 157 请求、1062 断言；377 文件凭证扫描；排水通过 | `author-baseline/` |
| seek 工具调试 | near 两次成功；106 请求、605 断言；237 文件凭证扫描；排水通过 | `seek-debug1/` |
| seek near 20 次 | 全部成功；1040 请求、5427 断言；2129 文件凭证扫描；启动器 exit 0，remaining 为空 | `seek-near20/` |

新 seek 工具通过真实 HTTP 导入 WordEmphasis、创建模板并应用到工程，读取完整 PNG 后解码，
检查精确来源／代次／序号／时间与重复帧摘要。初次准备因漏传 `configurationId: null` 失败，
修复后两次 seek 使用不同 PID＋startTicks，确认旧实现会重建 Worker。
两次调试耗时不能作为正式性能样本。探针测量请求发送至 ready／完整 PNG 下载，不含 UI 显示。
near 20 次均使用不同 Worker 身份；完整 PNG 中位数 `2430.181 ms`、p95 `3451.870 ms`，
ready 中位数 `2429.657 ms`、p95 `3451.287 ms`。这是一次有限 HTTP 基线批次，
不替代后续三轮交替 A/B；轮询和证据写入开销已包含。

## Runtime 有限矩阵

证据归档：`runtime-baseline-archive-fix-4/`。运行期间无产品构建或其他本次性能采样。
实际调度器另存为 `scheduler-m00/bench.py`，SHA-256
`f03d9b3b4881791b03adb58e45e647dfdfd18df0283978663addd4397b371b46`；
它与 prepare 时复制的工具快照有差异，后续 A/B 必须使用同一调度器。

| 标签 | 矩阵 | 结果 |
|---|---|---|
| `baseline-short` | 九场景 × CPU／Vulkan；1 轮、2 预热＋8 帧 | 18 进程全部成功；仅探索样本 |
| `m00-matrix` | lyrics／ruby／multi／video × 两后端；1 s、重复 1 s、181 s、361 s | 8 进程、32 帧全部成功 |
| `m00-short-three` | 上述四场景 × 两后端 ×3 轮；2 预热＋8 帧 | 24 进程、240 帧全部成功 |
| `m00-transparent-three` | 透明 lyrics／ruby × 两后端 ×3 轮；2 预热＋8 帧 | 12 进程、120 帧全部成功 |

各批次分析器通过，无超时，已观察的进程身份全部退出。短批次三轮每场景 24 个测量帧汇总：

| 场景 | CPU 中位数／p95 ms | Vulkan 中位数／p95 ms |
|---|---|---|
| lyrics | 1.368／1.498 | 3.128／6.098 |
| ruby | 1.551／1.800 | 3.083／4.264 |
| multi | 5.277／6.526 | 6.666／8.610 |
| video | 18.747／25.199 | 2.903／3.896 |

多实例从 1 s 跳至 181 s 约 6.4–6.7 s，再跳至 361 s 约 5.0–5.2 s。这是所测 v1
重放路径的长跳转成本，不能混入连续帧 p95。视频 fixture 只有 320×240，尚不能证明 M06
目标分辨率解码收益；透明路径跳过背景，不代表透明视频合成验收。帧样本相关，三轮旧版
基线也不是最终三轮新旧 A/B。编译计数、备用命中、媒体下采样与编码反压成本按相应节点补采。

## 语义冻结与后续边界

[D8 台账](script-sdk-v2-capability-ledger.md) 已覆盖十类能力、九样例及目标复杂样式的六类证据。
纯求值依赖、属性合成、Ruby／字簇、退场身份、锚点断点、媒体修订按主计划保持为后续 v2
合同，M01–M06 不提前改写。v2 memo、纯度、SVG／Warp、媒体特征轨指标仍未实现，不填造基线。

M00 桌面首次 finite 流程失败：旧代迟到完成帧增加累计数，验收脚本过早结束等待并误判 attach
复用旧代。工具修复改为要求当前公开／原生完整身份和操作后的实际完成帧；旧失败保留，
重跑结果另补，不以脚本静态检查代替桌面通过。

修复后工具的 27 项 Node 回归通过。隔离 worktree `.local/m00-desktop-baseline` 固定在
`0969972`，复制原发布制品、Web bundle 和输入记录后，`desktopBuildState` 为
`web:null/deployment:null`，没有重建产品。未变的 RobotoFlex 字体许可证因 checkout 换行差异需按
原字节保持；生产输入校验未绕过。其 `gpu-finite-r2` 实测在第二次暂停失败：最终帧已由
Worker 生产并被 native 归还，但 renderer 的 `waitingBarrier` 丢弃了它，暂停态随后没有补帧。
末端控制序号已生效，Worker executing／pending／outstanding 均为 0。探针只要求一帧当前完整
身份的 drawn＋native 完成，这不是要求暂停后持续产帧。此项作为已知产品竞态单独修复；
完整 GPU 生命周期不能记为通过。失败运行启动器 exit 0，已观察自有进程全部退出。
