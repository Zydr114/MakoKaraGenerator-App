# CPU / Vulkan 单帧性能采样工具

## App M00 显式时刻矩阵

App 仓库中 `prepare --commit HEAD` 和 `prepare-ab` 从对应 App 提交的 `backend/` 树归档，记录提交和子目录；原独立后端根目录仍使用根树。工具源码来自当前工作树并单独记录摘要及脏状态，生产源码只来自所选提交。插桩严格匹配源代码，若当前版本不匹配会失败，不能将失败计作基线。

`matrix` 模式要求显式微秒时刻、零预热及帧数等于时刻数，上限 48。其 370 秒工程在 0、180、360 秒各有四行实际歌词；可请求相同时间以观察同一会话内重复命中，也可后退以观察 seek。首次帧和会话创建分别报告，逐帧标记 first/repeat/forward/backward；它们不证明系统、驱动或编译缓存已清空。新进程使用独立空缓存目录，已生成的编译缓存是否命中需另行证明，不能把重复帧视为跨会话缓存命中。帧内耗时包含准备、绘制和最终回读，fixture 加载另计；像素哈希、完整规范诊断和设备证据仍使用原有规则。

附加场景 `ruby` 为每行首字增加注音，`multi` 使用三个相同包的独立控制器实例，`video` 在运行批次开始前用本机 FFmpeg 的 `testsrc2` 和 FFV1 生成 320×240、10fps、2 秒循环视频。所有变体共享同一文件；manifest 记录生成命令、FFmpeg 版本和文件摘要，分析及归档校验该文件。不同 FFmpeg 版本可能生成不同字节，跨批比较须核对摘要。`--transparent` 使用现有透明输出路径；请分别采集透明和背景视频，透明模式是否绘制背景以实际渲染语义为准。

`--video-width` 和 `--video-height` 只改变视频素材尺寸，默认仍为 320×240；两者须为 2..4096 的偶数，且乘积不超过 4096×2160。输出尺寸由独立的 `--width` 和 `--height` 指定。整个 run 只生成一份视频，每个变体启动前重验摘要；manifest 记录素材尺寸、帧率、时长、完整生成命令、文件字节数和 SHA-256。分析器逐帧对照同轮同后端 old/new 的完整 BGRA 摘要和诊断，摘要相同表示字节相等；不同时需要另行获取完整像素计算 D13 通道差，不能用哈希差估算误差。

M06 因子 4 验收示例（先使用匹配的旧、新提交完成 `prepare-ab` 与 `build`）：

```bash
python3 backend/tools/backend-render-performance/bench.py run --output EVIDENCE --label video-factor4 --scenarios video --variants old,new --rounds 3 --warmup 2 --frames 8 --video-width 1920 --video-height 1080 --width 480 --height 270 --mode sequential --timeout 180
python3 backend/tools/backend-render-performance/analyze.py --output EVIDENCE --label video-factor4
```

顺序请求为 30fps、视频为 10fps，因此同一视频帧会重复请求；2 个预热和 8 个测量请求跨多个源帧，但不能把每次测量都解释为一次新解码。各帧 wall time、源时刻、像素摘要及进程分配在 JSON/CSV 中；具体性能门槛须按独占采样规则对照，以上示例本身不证明缩小路径已启用或性能达标。可用独立诊断验证源和实际返回帧尺寸及每帧 stdout 字节。

M02 将 `ProjectRenderSessionFactory` 从路径构造切换为注入 `IScriptCompilationService`。当前 Runner 模板保留编译器项目引用和显式注入；`factory_layout.py` 只在隔离源码归档的工具副本中适配已识别的旧工厂：恢复路径构造调用并移除独立编译器引用。注入版直接使用相同模板。两侧都从工厂／编译服务构造以前开始计时。准备清单记录模板、各侧生成工具摘要、精确差异及工厂布局；构建前逐项复核，未知或混合签名直接失败。`instrument.py` 对旧版 `_compiler.Value` 与注入版 `compiler` 分别严格匹配并记录布局，零匹配不得忽略。

M05 去除的是 `SkiaRenderPipeline.ImageScene.Surfaces.cs` 的 `_device.FlushAndSubmit(main)`，当前插桩没有匹配该调用；`graph.submit` 匹配的是 `Effects/CompiledImageGraph.Drawing.cs` 的另一处 `_device.FlushAndSubmit(surface)`，不能因 M05 改动直接删掉或把它误记为被移除的提交。其余严格匹配仍须按合并后的实际源码验证；若缺失或数量变化，准备阶段应失败，先审查新旧布局后再加具名分支。

示例（在 App 根目录，先完成准备和构建；采样须独占设备）：

```bash
python3 backend/tools/backend-render-performance/bench.py prepare --commit HEAD --output EVIDENCE
python3 backend/tools/backend-render-performance/bench.py build --output EVIDENCE
python3 backend/tools/backend-render-performance/bench.py run --output EVIDENCE --label matrix --scenarios lyrics,ruby,multi,video --rounds 1 --mode matrix --warmup 0 --frames 7 --times-us 1000000,1000000,181000000,181000000,361000000,361000000,1000000 --timeout 1800
python3 backend/tools/backend-render-performance/analyze.py --output EVIDENCE --label matrix
python3 backend/tools/backend-render-performance/bench.py run --output EVIDENCE --label matrix-transparent --scenarios lyrics,ruby --rounds 1 --mode matrix --warmup 0 --frames 7 --times-us 1000000,1000000,181000000,181000000,361000000,361000000,1000000 --transparent --timeout 1800
python3 backend/tools/backend-render-performance/analyze.py --output EVIDENCE --label matrix-transparent
```

显式时刻有限不代表重放工作有限：v1 时间轴可能从零重放至 180 或 360 秒，首次高时间请求可能很慢。每次请求的实际耗时、失败与超时必须如实登记；该矩阵不是三分钟连续播放、HTTP、桌面或视频编码性能验收。性能阈值仍由 M00 数据及用户确认决定。

每次 Runner 以新会话启动；运行期间周期采样 Runner 进程组和可追踪后代的 PID、startTicks、父 PID、进程组及会话。超时时核对 Runner 身份后向该进程组发送 SIGKILL，等待 Runner 并复核已观察身份；manifest 将退出、PID 复用、僵尸及仍存活分别记录。身份无法确认或清理出错时先写失败记录再抛错；超时退出码为 `-999`，失败目录保留。采样不能证明瞬时进程或未观察到的脱组后代已退出；已观察到的脱组后代不会被进程组信号终止，若仍存活会明确记录。

进程观察由调度器每约 200 ms 扫描 `/proc`，可能影响采样时的 CPU 调度；同批次变体使用同一观察方式，但与使用旧工具所得耗时不直接等同。

本工具对固定工程调用生产 `ProjectRenderSessionFactory`，记录直接 Runtime 路径的短批次单帧数据。基准生产源码为 `8681bc09d59fc72c11d62810940324a6a44eefd2`（产品 0.106.0）。它不修改生产源码，不代表完整工程、HTTP、浏览器、跨平台、长片或实际导出性能。

## 来源与运行

`bench.py prepare` 从指定提交创建隔离的源码归档 `sources/baseline`，复制工具源码后，再复制出 `sources/instrumented`，仅对后者注入阶段采样。`prepare.json`、生产和工具输入清单以及插桩差异记录用于核对来源；两份源码分别构建，不能把插桩数据当作未插桩基线数据。证据目录必须是新路径，失败目录保留，后续重新准备新目录。

`bench.py build` 内部已独占 `/tmp/mkg-performance-optimization-dotnet.lock`，调用时不要再用 `flock` 包住同一把锁，否则会自锁。其他构建或测试需协调使用该锁；正式采样还必须等待全部构建、测试及其他渲染停止。

主矩阵和独立阶段 trace 都通过 Runner 显式传入 `--monitoring off`。调度器会移除继承环境中的 `MAKO_PERFORMANCE*` 变量，再设置本次模式，因此桌面或 Server 的外部选择不会污染 A/B；`prepare.json` 与每个 run manifest 保存这条来源策略。监控开销需要单独运行 `run-monitoring`，不能从主矩阵推断默认 Server 日志成本：

```bash
python3 tools/backend-render-performance/bench.py run-monitoring --output EVIDENCE --label monitoring --variant new --scenarios lyrics,graph,material --rounds 1 --warmup 1 --frames 2
python3 tools/backend-render-performance/analyze.py --output EVIDENCE --label monitoring
```

该命令对每个场景、CPU/Vulkan 和 `off`／`summary`／`detailed` 各启动独立进程。`summary` 使用生产默认 `detailEvery=0`、`intervalSeconds=10`、`maxEventsPerSecond=10`；`detailed` 使用 `1/10/10`，逐帧事件仍受速率限制。结果记录这四项实际配置，短进程退出时等待尾部摘要；这属于直接 Runtime 监控开销，不包括 Server/Worker 传输或 HTTP 日志。

事件原样写入结果旁的 `.monitoring.jsonl`，分析器检查版本、阶段计数、读回字节、GC/分配计数、预期事件种类和没有 HTTP 协议字段。三种模式的有效 BGRA 与完整诊断必须逐帧匹配显式 `off`；sidecar 文件随归档收集。即使详细日志被限速，全帧像素/诊断对照仍完整保留。

```bash
python3 tools/backend-render-performance/bench.py prepare --output /path/to/new-evidence
python3 tools/backend-render-performance/bench.py build --output /path/to/new-evidence
python3 tools/backend-render-performance/bench.py run --output /path/to/new-evidence --label baseline-run
python3 tools/backend-render-performance/analyze.py --output /path/to/new-evidence --label baseline-run
```

调度器默认每个进程只创建一个项目会话，顺序请求 32 个项目帧：8 个预热帧（含单独标记的首帧）和 24 个测量帧。每进程所有项目会话的帧数硬上限为 48，包括预热帧；测量完成后另有一次独立的 4×2 设备 probe，计为 1 个 probe 帧，不包含在项目帧数内。默认 1920×1080、30fps、Final、不透明，支持透明输出。默认五轮，CPU 与 Vulkan、基线与插桩的运行顺序由调度器安排；不并发长时间渲染。

顺序模式从 1,000,000 微秒开始，按 30fps 网格向前请求。此选择让粒子有时间形成；首次请求可能包含从零开始的 120Hz 时间轴重放。`seek` 模式固定 8 个请求，覆盖直接跳到 1 秒、回退、网格时刻重复和非网格时刻重复；使用时指定 `--warmup 0 --frames 8`。

## 固定工程

全部场景使用同一歌词基础：四行中文，每行 8 个带独立时间的元素、行长 2 秒、元素长 250 毫秒，工程时长 8 秒；画布 1920×1080，字体族 `Noto Sans CJK SC`、字号 88，使用 `Project.Create` 的默认展示配置和固定种子 7。无音频、图片或视频背景。目标分辨率是渲染参数，可与固定工程画布不同；字体族依赖本机实际字体安装。

| 场景 | 固定输入 |
| --- | --- |
| `lyrics` | 纯歌词，空效果集合。 |
| `csharp` | `samples/ScriptEffects/WordEmphasis` 的 `word-emphasis` 控制器，工程绑定，包默认参数。 |
| `graph` | `samples/ScriptEffects/LineGlitch` 的 `line-glitch` 图，工程绑定，包默认参数。 |
| `material` | `samples/ScriptEffects/CustomInk` 的 `custom-ink` 材质用于未唱填充，包默认参数。 |
| `particle` | `samples/ScriptEffects/CursorTrail` 的 `cursor-trail` 控制器，工程绑定，包默认参数。 |
| `combined` | 顺序装配以上 WordEmphasis、LineGlitch、CustomInk 和 CursorTrail，未唱填充使用 CustomInk。 |

包从其完整官方样例目录读取，包括清单和清单声明的源码、图、shader 等资源；实例采用项目订阅。输出记录包 ID、revision、效果输入指纹以及 fixture 描述的 SHA-256；后者仅覆盖输出中的描述，不是完整源码树指纹。六场景的实际效果贡献应以同设置反事实运行核对，不能只根据实例存在推断像素贡献。

反事实调度参数为 `bench.py run --counterfactual none|all|csharp|graph|material|particle`，对应保留全部效果、移除全部效果或移除指定类别。先分别用 `analyze.py --output ... --label A` 与 `--label B` 验证两个批次，再运行 `analyze.py --output ... --control-label A --counterfactual-label B`。两组必须有相同的帧时间、尺寸和设备；每组至少一帧完整像素摘要改变才记录效果有贡献。

## 计时与证据

`create.wallMs` 包括工厂和编译服务构造、创建真实会话及其资源准备；因此与旧版仅从 `CreateAsync` 开始计时的记录不可直接比较。`frames[].wallMs` 包括 `RenderAsync` 内的准备帧、Skia 绘制及 BGRA 像素读回。逐帧统计另含进程级 `GC.GetTotalAllocatedBytes` 差值和各代 GC 次数差值；这是整个进程的观察值，不是隔离线程或 native 分配量。会话释放单独计时；`RenderFrame.Dispose`、每帧哈希、诊断处理、最后一帧 PNG 编码和设备 probe 均不计入对应渲染 wall 时间。

计时外工作仍会影响后续帧的 CPU、内存和缓存状态，因此这是包含这些间隔的短批次结果，不是无干扰的连续输出吞吐量。新进程和新会话的创建、首帧包含正常 JIT 与资源首次访问；没有清空操作系统或驱动全局缓存，不能称为驱动冷启动。插桩增加测量开销，阶段数值需与未插桩基线分开解释。

每帧校验尺寸、stride、预乘 BGRA 格式、有效字节长度以及诊断状态，记录完整有效像素的 SHA-256、Runtime/图/材质诊断内容和诊断 SHA-256；随机 Runtime 诊断 epoch GUID 按首次出现顺序归一化为索引，序号及其余诊断字段保留。哈希是严格字节或诊断对照，不能替代像素容差分析。会话仍存活时采集本进程允许范围内的 Vulkan/Radeon 映射和 DRM 设备，再独立 probe 设备；probe 是同进程的另一设备创建，不证明它与项目会话是同一设备对象。

除上述 `video` 场景使用 FFmpeg 解码背景外，本工具不经过 HTTP、Worker 通信或视频编码；最后一帧 PNG 仅作为计时外的检查证据。性能结论限于记录的机器、设备、字体、样本、版本和时刻，不外推 GPU 编码、交互体验或长片连续播放性能。

## 工具检查和归档

`python3 tools/backend-render-performance/test_analysis.py` 用合成数据检查统计门禁，不渲染。`dotnet run --project tools/backend-render-performance/TraceCheck/TraceCheck.csproj -c Release` 只检查计时器的异步传播、嵌套和独占时间，不创建渲染设备。运行这些检查和构建时应暂停性能采样。

全部标签分析通过后，`archive.py --output EVIDENCE --destination NEW_DOC_DIR` 复核两份源码、工具、示例、插桩补丁、构建指纹及进程退出，输出确定性 `evidence.tar.gz` 和逐文件摘要 `evidence-index.json`。归档保留逐帧 JSON、诊断、阶段统计和环境，完整源码副本、二进制、编译缓存及代表 PNG 留在本机证据目录；归档不能代替重新分析各批次。

## 同设备旧 / 新 A/B

`prepare-ab` 接受两个已提交的源码，使用同一工具模板和相同样例文件，分别建立 `old`、`new` 及其插桩副本；不读取工作区中的未提交生产文件。针对已识别的旧工厂和注入式工厂签名，在隔离副本中生成对应 Runner；清单分别记录模板、生成后的工具指纹、差异和工厂布局，插桩副本使用对应的相同 Runner。准备和构建记录保留生产输入、样例、工具和二进制指纹；运行前及归档时复核。插桩是源码版本相关的严格替换，如果新源码不匹配会直接失败并保留失败目录，应检查补丁并更新插桩后另用新目录准备。

以下将 `OLD`、`NEW` 替换为固定提交，将 `EVIDENCE` 替换为新的绝对路径。正式性能采样须在其他构建、测试、发布和渲染结束后串行进行，先协调独占测量窗口。原版和插桩必须分开运行及解释；每行 run 需随后以对应 label 调用 `analyze.py`，失败不得并入统计。

```bash
python3 tools/backend-render-performance/bench.py prepare-ab --old-commit OLD --new-commit NEW --output EVIDENCE
python3 tools/backend-render-performance/bench.py build --output EVIDENCE
python3 tools/backend-render-performance/bench.py run --output EVIDENCE --label main --variants old,new --scenarios lyrics,csharp,graph,particle --rounds 5 --warmup 8 --frames 24
python3 tools/backend-render-performance/bench.py run --output EVIDENCE --label heavy --variants old,new --scenarios material,combined --rounds 5 --warmup 4 --frames 8
python3 tools/backend-render-performance/bench.py run --output EVIDENCE --label seek --variants old,new --rounds 1 --mode seek --warmup 0 --frames 8
python3 tools/backend-render-performance/bench.py run --output EVIDENCE --label transparent --variants old,new --rounds 1 --transparent --warmup 1 --frames 2
python3 tools/backend-render-performance/bench.py run --output EVIDENCE --label trace-main --variants old-instrumented,new-instrumented --scenarios lyrics,csharp,graph,particle --rounds 1 --warmup 8 --frames 24
python3 tools/backend-render-performance/bench.py run --output EVIDENCE --label trace-heavy --variants old-instrumented,new-instrumented --scenarios material,combined --rounds 1 --warmup 4 --frames 8
python3 tools/backend-render-performance/analyze.py --output EVIDENCE --label main
python3 tools/backend-render-performance/analyze.py --output EVIDENCE --label heavy
python3 tools/backend-render-performance/analyze.py --output EVIDENCE --label seek
python3 tools/backend-render-performance/analyze.py --output EVIDENCE --label transparent
python3 tools/backend-render-performance/analyze.py --output EVIDENCE --label trace-main
python3 tools/backend-render-performance/analyze.py --output EVIDENCE --label trace-heavy
python3 tools/backend-render-performance/analyze.py --output EVIDENCE --original-label main --traced-label trace-main
python3 tools/backend-render-performance/analyze.py --output EVIDENCE --original-label heavy --traced-label trace-heavy
python3 tools/backend-render-performance/archive.py --output EVIDENCE --destination NEW_ARCHIVE_DIRECTORY
```

每一对同设备同轮次比较全部帧有效 BGRA SHA-256、完整规范诊断及摘要、fixture、参数、probe 与进程 DRM 列表；不比较 CPU 和 GPU 的位级等价。probe 仍是单独创建的设备，不证明它与项目会话为同一设备对象。各轮交替旧新先后，聚合结果含计量帧 median/P95/max、按轮中位数、分配及 GC；对照仅覆盖这些 fixture 和时间序列。插桩结果另列调用次数、区间和读回请求字节，不能与原版 wall 时间合并；请求字节不等同物理总线传输量。
