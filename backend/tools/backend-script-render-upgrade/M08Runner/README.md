# M08 有限性能与确定性采样

本工具只采样当前构建的 SDK v2 真实包 `CursorTrail`、`VectorTrail`、`EventBurst`。
它不设置性能通过阈值，不比较 v1 像素等价，也不单独宣布 M08 验收通过。
工具不在 solution 中；只新增本目录和兄弟 `m08_matrix.py`，通过链接复用 M07 的
`EvidenceFiles.cs` 与文字结构投影，保留原 M07 工具源码。

固定输入为透明 640×360、30 fps 源配置、606 s 输出。0、180、600 s 分别开始一行，
每行 2 s，包含两个相接的 1 s token `星`／`光`，分别附 Ruby `ほし`／`ひかり`。
请求字体 `Noto Sans CJK SC`、64 px，关闭描边、阴影、发光和进入动画。固定实例、
工程绑定、工程订阅和种子 `0123456789abcdef`；真实 package revision 不被替换。

参数覆盖在 `M08Fixture.Parameters` 中明确记录：CursorTrail 固定 60/s；VectorTrail
固定 24/s、Exact、ribbon、完整 Births 可视化；EventBurst 每个真实 token 起唱 3 粒。
三者寿命均 600000 µs、散布半宽 2 px、连续包使用 Jump，其余采用包默认值。
精确 overrides、真实输入指纹与采样时刻保存在独立 `fixture.json` 资产中。

每段查询首出生的前一微秒／该时刻／后一微秒、首死亡的相邻三时刻、750000 µs、
token 交接的相邻三时刻、行终点以及最后死亡的相邻三时刻；较晚段还查开始前一微秒。
这些坐标由固定 fixture 参数手工选定，仅用于采样，不能充当从被测算法生成的 expected。
每个进程先用独立会话执行全部升序时刻，再用另一个独立会话执行固定乱序，乱序从最远
时刻和最近时刻交替取值。每个会话按同一顺序重复 1–6 轮，matrix 默认 2 轮。
生产 Vulkan 渲染另用两个独立会话重复相同查询；所有会话与进程严格串行。

主代理先串行构建工具，再在无其他构建、测试或 GPU 采样的时段运行。以下是调用说明，
不表示工具已经构建或执行。将证据目录替换为未使用路径，ICD 替换为实际驱动清单：

```bash
dotnet build backend/tools/backend-script-render-upgrade/M08Runner/M08Runner.csproj -c Release
python3 backend/tools/backend-script-render-upgrade/m08_matrix.py \
  --runner backend/tools/backend-script-render-upgrade/M08Runner/bin/Release/net10.0/M08Runner.dll \
  --backend backend --source-commit SOURCE_SHA --icd /path/to/vulkan_icd.json \
  --output /tmp/m08-finite-new --repeats 2
```

Matrix 不执行构建或安装，自动执行 3 轮、每轮 3 个独立新进程，共 9 个进程，并轮换样例
先后顺序。每个子进程的命令、PID、Linux startTicks、运行日志、负载前后值与返回码均留存；
超时只停止本次持有的子进程。单独运行一个样例的接口为：

```bash
dotnet backend/tools/backend-script-render-upgrade/M08Runner/bin/Release/net10.0/M08Runner.dll \
  backend VectorTrail /tmp/m08-single-new/vector.json 2
```

成功报告 schema 为 `mako-sdk-m08-finite-v1`；`status=completed` 只表示采样完成。
每个唯一时刻保留结构 JSON 与紧凑预乘 BGRA 资产。同进程的重复／乱序会话比较完全相等
的 SHA256；不等时保留差异资产和比较记录，继续收集其余样本。规范投影显式展开实际
`ParticleIdentity`、`ParticleOrigin.Continuous/Burst`、`ParticleHead`、全部 `ParticleSource`
种类、真实 CursorIdentity、完整 Birth、Spec、运动曲线、Alive、拖尾以及四类 overlay 图元，
不会让 JSON 基类序列化吞掉多态字段。源／最终几何与图元数组保留生产顺序。
当前三个样例使用纯色文字材质；链接的 M07 文字格式遇到其他材质会明确失败。

锚点统计具有缓存相关性，因此 `V2AnchorStatistics` 不进入语义 hash。每次 direct prepare
单独记录 UniqueTimes、TextCalls、FutureEndpoints、MemoHits、MemoMisses、完整 Births／Alive／
Trail 节点数、每实例数量、overlay 项数，以及实际 geometry provider 场景／行调用差。
这些是公开帧提供的工作量统计；生产像素会话不公开其准备帧，因此不伪造其 geometry／memo
计数，也不通过反射探测内部状态。结构与像素来自同一固定快照的不同会话。

计时与托管分配分别记录包读取／解析、编译器创建、显式编译、每个求值／渲染会话创建，
以及逐帧 PrepareAsync 或正式 RenderAsync。求值会话创建包含 Declare 与计划准备，当前
没有公开的单独 Declare 计时。正式 render 包含生产 prepare／绘制／readback；并非 GPU
纯执行时间。摘要、规范序列化、写盘与 Dispose 不在逐帧计时内，Dispose 单列。
`processAllocatedBytes` 是全进程托管分配差，可能包含后台线程，不是峰值、原生内存或 GPU
内存。首次访问、重复访问及两个查询顺序保留独立记录，matrix 计算每组中位数与 p95。

报告记录产品版本、compiler identity、包／manifest／每文件摘要、递归 DLL 摘要、真实输入
指纹、框架、OS、进程架构和明确 Vulkan 环境变量。matrix 另外记录调用方源码 SHA、实际
Git HEAD／dirty 状态、dotnet info、ICD 文件摘要和 fontconfig 匹配字体文件摘要；调用方 SHA
是标签，不能覆盖真实 dirty 状态或二进制身份。fontconfig 匹配不证明每个 Skia fallback
字形都使用同一文件。`separateBackendProbe` 在测量后创建另一设备，不冒充被测会话的
设备身份。新进程不会清空 OS、字体、驱动或 GPU 缓存。

Matrix 校验完整查询集合与每份资产摘要，核对相同样例三轮的输入／包／产品／compiler／
DLL 身份，并比较所有结构与像素观测。`summary.json` 分别保留 `status=completed`、
`determinismStatus=equal|different` 和 `performanceAcceptance=not assessed`。
退出码 0 仅表示有限确定性比较相等，1 表示差异，2 表示子进程未完成；不把耗时自动判为
性能通过。失败进程保留 pending、资产和日志，成功输出采用不覆盖发布；再次运行使用新路径。
本工具不覆盖 HTTP／Worker／UI、并发负载、共享纹理、任意包或完整作者工作流验收。
