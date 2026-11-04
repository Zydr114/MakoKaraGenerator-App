# M07 最小纯求值 SDK 与作者闭环证据

日期：2026-11-04。分支 `dev/script-sdk-render-upgrade`。
**功能、有限像素与确定性已通过；正式性能门槛待用户确认，候选检查有一项未达，M07 尚未关闭。**
后续 M08 接口已开放独立编码；本报告只使用固定 M07 二进制和未改变的 M07 样例。

## 来源与范围

包／格式门禁 `2017ef3`，Worker 身份 `6f93584`，纯文字／索引／退场 `91058e9`，
作者流程 `dc6a2e9`，合同与前端副本 `72be32b`。后端产品 0.111.3，合同 1.43.0。
包 v3／SDK 2.0.0／`skia-ganesh-3.119.4-v2`，工程 30／歌词 18／效果合同 2／模板库 8／脚本库 2。
开发期保留明确分派的 v1；CPU 拒绝实际启用的 v2 效果，未使用包和无效果 CPU 不扩大拒绝。

证据根目录为 `.local/script-sdk-m07-20261104/`，本文件只登记结果与重现入口，不提交运行产物或凭证。
SDK 实际接口见 [SDK2 参考](../style-reference/script-sdk-v2.md)，三例位于
`backend/samples/ScriptEffectsV2/{WordEmphasis,RepeatEmphasis,ClusterExit}`。

## 功能与回归

| 检查 | 固定 M07 结果与证据 |
| --- | --- |
| 全后端构建 | `build-05.log`，零警告／零错误 |
| 全后端测试首轮 | `backend-full-01.log`：14 项目、6827 测试，6822 通过／5 失败／0 跳过 |
| 失败复验 | `server-sdk2-03.log` 4/4；`server-ruby-http-02.log` 3/3。首轮 5 项失败全部已逐项复验通过；不是另一次全量全绿运行 |
| SDK／渲染定向 | `rendering-focused-03.log` 43/43；`scripting-focused-03.log` 105/105；`core-admission-02.log` 52/52 |
| 缺陷负例 | 去掉 ref 局部变量门禁的变异使对应负例失败：`analyzer-ref-mutant.log` 58 通过／1 失败，恢复后 105/105 |
| 前端 | `frontend-build-01.log` 构建通过，保留既有大 chunk 提示；修正 DOM 身份后 `frontend-typecheck-03.log` 通过，`frontend-unit-02.log` 44 文件／3937 项通过 |
| 合同 | 新 CLR metadata，`contract-validation-02.json`：112 路径／125 方法／597 schemas，3434 例含 2548 反例；随后只刷新文档指纹，无 DTO 或示例变化 |

首轮 Server 的两项新夹具错误分别涉及无媒体输入的 `package-lyrics-scene-v1` 分派、
HTTP 反序列化后嵌套数组的 record 引用比较；已核实路径并改为完整 wire 值比较，保留输入／代次／
帧／闭包不变的约束。三个 Ruby HTTP 用例缺少发布产物，补匹配 self-contained 发布后通过。
其他早期编译、shader profile、编译计数及 ImmutableArray 比较失败记录均保留。

纯文字回归覆盖真实索引、事件前后微秒、缺时间、Ruby、字簇／连字拒绝、B*A 与父链、
多个实例、同源重新装载与多退场副本共存、hide−1 冻结和退出不占布局／播放头。
冷暖／淘汰／乱序／重开、取消和预算按各定向用例验证；不据有限样例证明任意 C# 的终止或纯度。
混合 v1 状态视觉入口保留旧回放语义，不声称这种组合具有纯 v2 的时间复杂度。

## 匹配发布、HTTP 与浏览器

发布目录 `.local/backend-dist/script-sdk-m07-0.111.3-01/`，源码 `72be32b`、生产输入 clean，
Server／Worker 都为 Release/linux-x64 self-contained，SDK ABI 字节匹配。
`publish-01.log` 和部署 manifest 固定来源、文件摘要和身份。

`http-author-01/summary.json`：99 次请求、730 条断言、3 张完整解码 PNG、235 个文件凭证扫描通过。
实际 Vulkan Worker 的 ICD／driver 映射、DRM render fd、PID/startTicks 和排水由同次 probe 观察。
覆盖 A→非法 X→正确 B 的保存／编译／显式采用、编译历史、源码撤销／重做、CAS、原始包字节、
CPU 创建／验证／切换拒绝、失败保持原 GPU 输入、参数改动后旧输入继续有效及显式换输入。
`202` 均等待权威任务终态。实际命令见
[HTTP/UI 工具说明](../../backend/tools/backend-script-render-upgrade/M07_AUTHOR_ACCEPTANCE.md)。

| 真实 UI | 检查 | 已解码显示帧 | 证据 |
| --- | ---: | ---: | --- |
| WordEmphasis | 33 | 5 | `ui-WordEmphasis-03/` |
| RepeatEmphasis | 17 | 2 | `ui-RepeatEmphasis-03/` |
| ClusterExit | 17 | 2 | `ui-ClusterExit-03/` |

三例导入真实包，经 CodeMirror 编辑实际入口、保存、编译和采用后显示固定来源的 GPU PNG。
Word 还覆盖非法源码诊断、CPU 拒绝、参数修改／撤销／重做。逐帧核对 draft/source/generation、
PNG 内容摘要、DOM Blob 摘要、`img.decode()`、RGBA 摘要、可见且未遮挡；截图已检查。
各例无未处理页面异常、无 URL 凭证、凭证扫描通过，清理各自草稿和组件，独立 Chrome 关闭；
`ui-harness-03.json` 中 Server exit 0、Vite 为本次主动停止。

UI 首轮暴露 Note 不透传身份属性，已将标记移到真实草稿根元素；第二轮暴露 Chrome Blob 的
Network 正文缓存为空，工具改在原响应暂停期间读取并原样放行。失败目录保留，没有将它们算通过。
该捕获会改变网络交付时序，仅用于正确性；Chrome helper 关闭 GPU，不代表 Chrome GPU 或桌面共享纹理验收。
Repeat／Cluster 的源码编辑为追加注释，实际语义由独立 SDK／渲染回归及下述采样验证。
完整 720 帧作者验证、保存库重开、全资源编辑和九样例导出属于 M11，未在此提前关闭。

## 三轮有限采样

工具 [M07Runner](../../backend/tools/backend-script-render-upgrade/M07Runner/README.md)，构建日志
`runner-build-02.log` 零警告／零错误。二进制按产品源码 `dc6a2e9` 构建；
`performance-02/inputs.json` 保留实际 DLL 摘要。固定 Radeon 880M／RADV、640×360 透明输出、
一个完整 CJK token 与 Ruby、近端／180.75 s／360.75 s，另覆盖退场微秒边界。
三轮以 AB／BA／AB 顺序采样，两例各 v1/v2，ClusterExit 只测 v2，共 15 个独立进程；
各进程同一会话三轮固定查询序列，未清 OS／驱动缓存。

`performance-02/summary.json`：24 组 v1/v2 像素对照最大每通道差 **0**；
20 组跨进程结构／像素对照通过；每进程内重复时刻亦强制核对相同摘要。
Repeat 的对照仅针对单 token 整句使旧 token ordinal 与新 line ordinal 对齐的有限输入。

| 指标：三轮中位数（ms） | v1 Word | v2 Word | v1 Repeat | v2 Repeat |
| --- | ---: | ---: | ---: | ---: |
| 包加载 | 83.23 | 79.44 | 80.25 | 76.96 |
| 显式编译 | 819.89 | 907.52 | 868.79 | 945.97 |
| 准备会话创建 | 128.45 | 158.56 | 120.96 | 154.92 |
| 首次 180.75 s 准备 | 1292.16 | 1.43 | 1255.39 | 9.79 |
| 首次 360.75 s 准备 | 1013.60 | 0.67 | 1005.17 | 0.77 |
| 首次 180.75 s 绘制读回 | 356.35 | 1.81 | 366.29 | 9.25 |
| 首次 360.75 s 绘制读回 | 653.64 | 1.57 | 654.28 | 1.56 |

v1 对应首次远端准备调用基础几何 21600／43134 次；v2 均为 1 次，重复命中为 0 次。
v2 首次近端准备仍约 113–115 ms，显式编译和初始准备分别增加约 77–88 ms、30–34 ms；
没有把这些成本并入 seek 收益。单独设备 probe 不是被测会话的直接设备身份证据，真实 Worker 观察见 HTTP。
本工具不采集 HTTP/UI 可见延迟或完整工程吞吐，低分辨率小场景不能外推复杂样式。

正式门槛已向用户提出，尚待确认。候选是：查询各位置中位数／p95≤v1 的110%＋5 ms；
加载／编译／会话准备各≤v1 的125%＋50 ms；重复查询准备≤5 ms、绘制读回≤10 ms。
`threshold-candidate.json` 的 196 项候选检查有 1 项未达：ClusterExit 第 8 查询位置
重复准备 p95=7.8293 ms；三轮相应记录均约7–8 ms，不能解释为仅一次偶发。
其余 195 项通过，候选失败保留，后续定位／复测与用户确认分别记录；不因此认定正式性能通过。

定位性诊断 `performance-02/diagnostic-no-tiered-cluster.json` 在同一 M07 Runner 上显式设置
`DOTNET_TieredCompilation=0`，对应第 17／26 条记录均约 0.051 ms，另第 25 条约 2.372 ms。
此结果支持进一步检查 JIT 的方向，但不足以证明唯一原因；环境不同于正式三轮矩阵，
没有替换原样本、改变产品运行配置或放宽门槛，正式性能状态不变。
