# GPU 结构二次调研证据（2026-10-28）

对应[调研报告](../../backend-gpu-structural-performance-research-20261028.md)。固定工作分支 `dev/performance-optimization`，源码 `61ee27ef069c1d7126e68a3bfe0e6c6d8391d3bd`，产品0.106.1／合同1.39.0保持。这里记录隔离候选的研究结果，不代表生产优化已实施或正式性能验收通过。

## 归档入口

- [compact-research-summary.json](compact-research-summary.json)：微探针分组统计、三轮配对总耗时、逐轮中位数、历史 trace 重分析和有限验证计数。
- [evidence-index.json](evidence-index.json)：压缩包及209个归档文件的大小、SHA-256。
- [evidence.tar.gz](evidence.tar.gz)：原始结果、日志、实验补丁、探针和分析脚本、选定输入指纹、上游源码及许可证。
- [archive-verification.json](archive-verification.json)：解包逐项哈希检查、离线重算逐字节相同及有限凭证模式扫描记录。

压缩包 **1,552,675 bytes**，SHA-256：

```text
f452ed96ee913a30810c21c762bbf482a2dd984b95f727584414ae45adcedce4
```

## 内容与来源

| 包内路径 | 证据范围 |
| --- | --- |
| `runs/` | 基础读回微探针，CPU／Vulkan各三轮 |
| `color-runs/` | 匹配／null颜色信息控制，CPU／Vulkan各三轮 |
| `origin-runs/` | TopLeft原点微探针，Vulkan三轮；与BottomLeft的192条记录均不同 |
| `runtime-runs/` | 四场景、三种实现的初步Runtime对照；TopLeft有5/20对像素不同 |
| `followup-runs/` | 三场景的三轮4＋8配对、透明和seek；保留逐帧哈希、诊断及末帧PNG |
| `historical-traces/` | 第一批正式采样的12份trace原文件副本；属于历史输入，不计本轮新运行 |
| `ReadbackProbe/` | 微探针最终综合源码，依赖原基线程序集；不含历史三份探针二进制 |
| `*-only.patch`、`runtime-*-prepare.json` | TopLeft及颜色匹配直接读回两份独立补丁与指纹 |
| `runtime-inputs.json` | 952个选定源码／样本／工具输入的基线与候选SHA |
| `workspace-audit.json` | 生产输入不变、候选仅一文件不同、程序集与结果指纹、57个已观察PID退出检查 |
| `upstream/` | 官方SkiaSharp 3.119.4、其native固定提交源码、来源清单与许可证 |
| `analyze.py`、`assessment.json` | 可离线重算的完整统计及逐帧比较结果 |
| `run-runtime.py`、`run-followup.py` | 原研究编排命令，路径依赖原工作区；不会在离线分析时执行 |
| `audit-workspace.py`、`archive.py` | 原工作区收口及归档工具，依赖原工作区布局 |

上游native固定提交为 `7dbfc07dd33181f84e0958afb7ee805c6c769f0b`。GitHub API查询曾被匿名速率限制拒绝，之后通过官方tag目录页面确认submodule SHA；`submodule-verification.json`保存页面地址、页面SHA及提取结果，网页本身不进入归档。12份源码和两份许可证的原始地址、SHA可独立核验。

源副本和bin／obj／脚本缓存未收入压缩包。原始全量目录为 `artifacts/gpu-structural-research-20261028/`，基线Runner来自第一批 `artifacts/performance-optimization-20261027-ab-resumed/sources/new/`。基线与当前选定952输入一致，候选副本各自只修改 `src/MakoKaraGenerator.Rendering.Skia/SkiaRenderBackend.cs`；重新构造候选应从固定Git提交取源，再分别应用对应补丁，不能将两个补丁叠加。

## 离线复核

在任意新目录解压后，执行 `python3 analyze.py` 即可从包内JSON和历史trace重算 `assessment.json`，不需要.NET、GPU或访问原工作区。归档前已在独立临时目录实际执行，结果逐字节一致。`evidence-index.json`列出的所有209文件均通过解包SHA检查。

若要重新运行GPU实验，须另行固定环境、构建和执行；此归档的离线重算不代表新采样。研究脚本内的源和缓存路径须匹配实际工作区，微探针项目还使用仓库的集中包版本和构建属性。不要直接把研究补丁应用到生产分支并当作已验收优化。

## 计数与限制

- 15个微探针进程、960条读回记录、1056个合成表面。各自同图案四种方法一致；跨原点对照保留失败。
- 42个Vulkan Runtime进程、342项目帧＋42个小probe；每进程最多12项目帧。颜色匹配候选161对完整有效BGRA及完整规范化诊断一致，TopLeft另外20对中5对像素不同。
- 57个进程均正常exit0，收口时PID均不存在；42个Runtime进程有RADV库／DRM描述符及独立小probe设备信息。这不是Worker后代或整机进程排水证明。
- 三轮计量帧与原第一批正式五轮数据分列。组合候选最大506.331ms，500ms目标未通过；创建、首帧与seek不并入稳态分位数。
- 所有209个归档文件通过私钥头及常见GitHub／Slack／OpenAI／Bearer凭证模式扫描；这是有限模式扫描，不代表穷尽所有秘密格式。
- 未做候选完整非法像素／masked／跨设备／取消预算回归、生产solution验收、发布、HTTP、浏览器或跨驱动测试。
