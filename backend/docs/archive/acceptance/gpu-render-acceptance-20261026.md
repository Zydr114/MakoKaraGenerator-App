# GPU 渲染实施与本机验收（2026-10-26）

状态：本机 GPU 渲染实施、审查和验收完成。工作分支为 `dev/gpu-render`；产品 `0.105.0`，公共合同 `1.38.1`、HTTP 协议 `1`。已推送开发分支，未合入 `main`。

## 实施范围

方案见[实施计划](../plans/gpu-render-plan.md)、[架构说明](../../architecture/render-skia-gpu-backend.md)与 [ADR-0239](../decisions/decisions.md#adr-0239)。由 GPT 6.1 Sol xhigh 子代理编码，主代理负责方案、源码审查和最终验收。

`MAKO_RENDER_BACKEND=cpu|vulkan` 在创建会话时选择设备，默认 CPU。Vulkan 选中后初始化失败明确报错，不回落 CPU。主画布、场景捕获、组隔离、包图、材质与图合成都经过共享的 Skia 设备；最终 flush/submit/readback 仍产生原有 premultiplied BGRA 帧，PNG 与 FFmpeg 消费边界保持。每个 Runtime 会话拥有一个设备；独立图或 pipeline 返回的自有表面持有设备租约，跨设备图像显式读回再交给目标设备。

实际 GPU 回归发现并修复了跨 context 图像传递、返回产物的设备生命周期、材质 raw 区域与 fallback 的字形栅格位置，以及已唱/未唱区域在半像素变换下重复覆盖的问题。GPU 分区固定为设备坐标的互补整数像素集合；CPU 历史 golden 与严格透明度断言保留。

## 证据与归属

本机证据统一位于 `artifacts/gpu-render-20261026-r1/`，该目录不提交原始 HTTP 会话或生成媒体。生产 GPU 修复基线为 `6dded955c578d6c280e4cccc1fb8e80ee12395fe`；最终生产源码基线为 `8fd06723945b2587b9b47b48802dc879be5b0251`，包含媒体修复 `9ec4d84`、图片导出握手修复 `521e124` 和对应测试修订。合同提交及匹配发布来源为 `ca3b4953775b6345a51eb73e2851cd7780e1cf48`；两者生产与测试输入相同，最终1494个被测输入未漂移。

| 证据 | 结果与范围 |
| --- | --- |
| `agent-impl/build-01050.log` | 渲染实现构建零警告、零错误 |
| `agent-impl/cpu-rendering-01050.trx` | Rendering 1055/1055 |
| `agent-impl/vulkan-rendering-01050.trx` | 显式 Vulkan 环境 Rendering 1055/1055；其中历史 CPU golden 显式使用 CPU，不把每条测试都称为 GPU 绘制 |
| `agent-device-clip-scratch/clip-summary.json` | 独立裁切复现与整数区域修复对照，保留原有 alpha 判据 |
| `tests/solution-build.log` | 首次完整 solution 构建零警告、零错误 |
| `review/initial-cpu-solution-results.json` | 首次完整 CPU solution 为 6102 通过、37 失败，共 6139；按下节归属保留，不改写为最终成功 |
| `tests/solution-build-final.log` | 最终统一 solution 构建零警告、零错误 |
| `review/final-cpu-results.json`、`tests/cpu-final/` | 最终完整 CPU solution 6159/6159，无失败、无跳过；Rendering 1055 已包含在其中，不重复累加 |
| `review/final-vulkan-integration-results.json`、`tests/vulkan-final/` | 显式 Vulkan 下 Runtime.Hosting 124、Backend.Rendering 275、Backend.Rendering.Skia 9、Worker 105，合计513/513。包含各层非绘制检查，不将513条全部称为实际GPU绘制 |
| `tests/vulkan-smoke-final/` | `MAKO_GPU_SMOKE=1` 真实 GPU surface、绘制/readback 与释放，1/1 |
| `contract-final/` | 从 `8fd0672` 重建 metadata 后生成/检查/验证通过；100 paths、111 methods、556 schemas、3156 examples（2374 反例）。相对初次0.105冻结仅来源变化，examples逐字节不变；500个源码指纹覆盖相对父开发线的全部30个生产差异路径。`contract/`保留早期`6dded95`归属 |
| `review/publication-audit.json`、`dist-final1/deployment.json` | 匹配 self-contained linux-x64 发布，`sourceDirty=false`；901个生产输入及1022个部署文件逐一核对 |
| `http-final1/summary.json`、`review/http-independent-audit.json` | fresh HTTP 2376请求／18405断言；52组预览完整PNG对比、8个短片／168解码帧、6组预览及3组导出控制，全部通过 |
| `contract-final/root-http-validation.json` | 2268份实际JSON响应及175份成功JSON请求匹配冻结合同；108项非JSON/未映射读取单列，0无配对产物 |

## 完整回归发现与修复归属

以下问题与 GPU 修改前的父开发线源码逐项比对，属于已有集成缺口。本批保留首次失败证据并修复，不把它们混称为 GPU 驱动差异。

- **受限媒体解码：** 自包含输入策略分支遗漏严格解码选项，损坏 FLAC 可返回成功并少解码样本。实际复现中原参数退出 0、返回 143360/144000 samples；保留协议/格式门禁并加回严格选项后正确失败。另修复显式 probe 格式被自包含分支忽略的问题。波形准备的原默认解码策略保持。
- **图片导出能力握手：** 通用导出器已公布六项来源，图片合同仍要求嵌套能力恰好四项，实际 Worker 在握手时拒绝自身能力。修复兼容旧四项和当前已知额外来源；图片任务自己的三项 `BaseProfiles` 不扩大。保留缺必需项、重复、未知来源与不支持图片基础来源的拒绝。
- **旧测试夹具：** 更新统一 runtime 输入、包与预设组合及实际音轨时长的测试依据，保留资源固定、授权、失败码与排水断言。Server 的 lazy-open 负例明确用测试 stand-in 模拟已通过的能力预检，再执行真实 Worker 启动失败；它不能作为真实握手证据，另有实际缺失部署预检拒绝测试。
- **作者编译等待：** 原测试的 2 秒轮询在完整集并发编译下会提前失败。实际证据为 2 秒时任务仍 running，6292ms 后 succeeded、调度器归零；只为真实编译增加有诊断的 8 秒等待，取消和资源排水仍保留原预算。
- **依赖边界测试：** 对齐P3/P4工坊与作者流程已引入的Server／Backend.Hosting→Runtime.Hosting依赖，只允许两个组合根和Authoring Hosting直接引用，保留GUI禁止、共享库不得依赖执行体、Contracts中立与组合根限制。

单项目重建造成的 Host/Worker `script-refs` 不一致，以及测试工具自身错误另行保留；使用匹配重建后复验，不能把这些失败记为产品缺陷。

## 真实 GPU HTTP 验收

硬件为 AMD Radeon 880M Graphics（RADV STRIX1），vendor `0x1002`、device `0x150e`，Mesa `26.2.5-arch3.1`，设备 Vulkan API `1.4.354`。本轮固定 `/usr/share/vulkan/icd.d/radeon_icd.json`；`review/vulkaninfo-final.txt` 保存独立设备查询。

`http-final1/`是一次完整成功运行，四个默认阶段全部完成：

- **预览：** static、scale、graph shader/capture、sprite、source material 各普通/透明4帧与回退seek，共50组；两组工程关闭后的独立预览恢复另作完整比对，总52组。HTTP PNG逐字节匹配同机direct GPU Runtime；独立完整PNG解码、straight BGRA长度/SHA/alpha、direct Runtime premultiplied BGRA与效果关闭反事实贡献均通过。HTTP不提供裸预乘BGRA，报告严格区分两种像素来源。
- **导出：** static、scale、graph、material各普通MP4与透明ProRes4444，共8个无音轨短片，每片21帧，共168帧。报告/媒体身份、完整解码长度/帧数/内容/alpha，以及ACL、Range、HEAD、ETag、304和诊断读取检查通过。影片没有独立Runtime逐帧像素oracle，不作等价性声明。
- **控制：** Initialize/Evaluate × cancel/deadline/close共6组实际失控Worker及其child；6个失控Worker均取得硬件采样，各组进程全部排水。另3组导出在核对当前活动job、精确PID/start ticks及硬件身份后SIGSTOP，取消与close成为cancelled，deadline成为failed／`WorkerExportTimedOut`，均无媒体产物并释放Worker。
- **硬件与排水：** 共观察89个Worker身份，其中49个取得同一身份的发布exe、白名单环境、Vulkan loader/硬件ICD映射与amdgpu render-node证据；63项帧/导出绑定均可回溯到对应硬件Worker。Server退出0，129个观察进程身份全部消失，`remaining=[]`；没有探针强制清理。5459个证据文件通过凭证扫描及完整SHA复核。

HTTP证据清单SHA-256为 `a818b4b1801ebafadf1e23a306c969da0f6850f0cae396a87d83d4860ee6c7c3`。主代理另抽查了普通graph与透明material的实际HTTP PNG；全部52组的像素判断以完整自动比对为依据。

## 使用与复现

CPU仍为默认值。在启动后端时增加环境变量即可启用本机GPU；已有预览与导出API不变。例如使用本次已验收发布：

```bash
MAKO_RENDER_BACKEND=vulkan \
VK_DRIVER_FILES=/usr/share/vulkan/icd.d/radeon_icd.json \
python3 tools/backend-web-host/host.py run \
  --dist "$PWD/artifacts/gpu-render-20261026-r1/dist-final1" \
  --profile "$PWD/artifacts/gpu-local-profile" \
  --origin http://localhost:5173 --origin http://127.0.0.1:5173
```

`VK_DRIVER_FILES`路径只适用于本机RADV；CPU路径使用`MAKO_RENDER_BACKEND=cpu`。服务按既有启动器正常关闭，不创建常驻验收服务。

重新验收须使用新目录，并按[GPU探针说明](../../../tools/backend-gpu-render-probe/README.md)先构建同源Release Oracle与匹配Server/Worker：

```bash
python3 tools/backend-gpu-render-probe/probe.py \
  --dist artifacts/gpu-render-20261026-r1/dist-final1 \
  --output artifacts/gpu-render-repeat-new
```

本批只验收当前 Linux/RADV 主机。它不承诺 CPU/GPU 位级等价、其他平台/驱动、性能提升、显存上限、长片、浏览器验收或 GPU 编码。几何/布局与媒体解码仍可在 CPU 执行；本次新增的是 GPU 光栅渲染管线。
