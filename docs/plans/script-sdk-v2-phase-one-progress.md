# 阶段一 M07–M11 执行记录

2026-11-04 开始；分支 `dev/script-sdk-render-upgrade`，起点 `ef8207f`。
用户授权按[主计划 §7.10](script-sdk-v2-render-pipeline-plan.md#710-m06-之后的两个执行阶段)
实施阶段一，并允许多个 GPT 6.1 Sol 子代理编码。M00–M06 的既有有限证据保持原归属。

## 2026-11-05 验收口径更新

用户明确要求暂不将性能门槛作为验收标准，当前优先完成阶段一功能。此前已经记录的耗时、
失败样本与候选门槛继续保留，作为后续优化依据；不再用它们阻塞 M07–M11 功能开发和验收，
也不把暂缓门槛写成性能通过。像素正确性、确定性、预算、取消、资源寿命、错误归属、
实际作者工作流和受影响模块回归仍是必需门禁。正式性能验收恢复时另行确认范围。

## 执行与验收顺序

| 节点 | 实施切片 | 状态及关闭条件 |
| --- | --- | --- |
| M07 | 包 v3／能力与格式门禁；只读 SDK 与索引；分析器；生产纯求值分派、退场与字簇；三例及最小作者链 | 有限功能、HTTP、UI 和确定性证据已完成；耗时历史差距保留，按 2026-11-05 用户指示暂不阻塞功能验收；完整证据汇总待更新 |
| M08 | 声明式粒子／Burst、最终锚点、解析运动与晚期视觉 | 有限功能、真实锚点／预算恢复、GPU 确定性及 0.112.2 HTTP 复验已完成；完整作者链留在 M11，历史性能失败保留 |
| M09 | SVG、Warp、完整仿射 Sprite、图与组资源 | 2026-11-05 指定 Linux 输入功能验收完成；分层记录见 M09 evidence，完整 D8 和九样例仍待 |
| M10 | 不可变媒体轨、颜色／时间／空间查询、就绪换代和自适应描边 | 开始基础身份／时间／容量实现；后续 decoder、轨、任务、SDK、输入换代与导出依次接通，能力门禁仍关闭 |
| M11 | 九样例、目标复杂样式及完整作者工作流 | 待前序功能通过；更新 D8 六类证据，未映射项保持发布阻塞 |

每个独立切片由主代理集成、按路径暂存、中文提交并推送；子代理不操作共享 Git 索引。
构建／测试与性能采样串行。公共 HTTP 变化在所属节点生成合同并进行匹配发布及 fresh HTTP，
不将静态合同、单元测试或旧版本运行结果代替当前运行证据。

## M07 接口与并存约定

- 新作者命名空间为 `MakoKaraGenerator.Scripting.V2`；`IPureKaraokeEffect` 提供
  `Declare` 和 `Evaluate`，准备结果由宿主持有，实例字段不承载跨帧状态。
- 包 v3、SDK `2.0.0`、执行 profile `skia-ganesh-3.119.4-v2` 显式分派；未实现能力具名拒绝。
  底层 shader 文件 profile 与包执行 profile 分别校验，不混淆二者的身份。
- 工程 30、效果合同 2、模板库 8、脚本配置库 2 同步启用读写门禁；旧库保留且拒绝覆写。
  开发期保留 v1 包路径；旧 `simulation` 仅服务 v1，v2 不使用时间线推进。
  `subscription` 暂作固定源查询范围，不代表 v2 事件回调。
- 输出复用既有稳定目标、布局和准备帧语义；v2 不公开 v1 写入器／状态回调。
  生产纯求值必须绕过从零逐边界重放，并保留当前时刻的严格失败与取消合同。
- v2-only 会话按请求时刻直接求值；与仅使用旧文字／共享状态入口的 v1 混用时，
  v2 跳过旧 120 Hz 中间网格。seek、输出模式及目标重置仅重建 v1，保留同一固定输入
  已成功完成的 v2 `Declare`；失败或取消仍释放全部实例并从固定输入重建。
- 若 v1 覆盖 `OnEvent`（可能产生 Burst）、`EvaluateEmitters`、`AdvanceVisuals`
  或 `EvaluateVisuals`，混合会话必须在旧回放边界计算 v2，以保留最终文字几何和锚点。
  这种组合继承旧回放的时间成本，不声称 O(1) 或成本不随目标绝对时间增长。
  对应混合生命周期与退出副本隔离回归已加入，尚待主代理串行构建和运行验收。

## 性能与证据边界

旧 `110%＋5 ms` 门槛仅覆盖 M00–M06。本阶段先测可比场景及新增功能的加载、准备、
近端／3 min／长时间 seek，按实际样本提出正式矩阵与阈值供确认。
2026-11-04 用户确认本机 Radeon 880M、640×360 CJK／Ruby 小场景三轮 A/B 门槛：
查询各位置 median／p95≤v1×110%＋5 ms，加载／编译／会话准备各≤v1×125%＋50 ms，
v2 热查询准备≤5 ms、绘制读回≤10 ms；像素单通道差≤2/255、结构确定性保持。
后续节点继续回归该场景，新重场景测量后另确认；主计划其余绝对候选值尚未批准。
1x 像素容差及语义门禁保持，缩放／倾斜／Warp 的优化容差仍由 M12 测量后确认。

下文按时间保留各切片当时的状态；当前节点结论以顶部表格及最新验收段落为准，历史未通过记录不删除。

## 已交付切片（不等于 M07 关闭）

- `2017ef3`：包 v3、SDK2 及工程／库读写门禁，产品 0.111.0。Core 1380 项、
  包与模板传输 31 项通过。
- 后续包／Worker 切片：实际 Worker 广告 v1＋v2 profile；每次固定输入验证真实 profile，
  导出能力证明固定到相同进程对象；包快照／传输可保留 SDK2 补丁身份。产品 0.111.1。
  2026-11-04 全后端构建零警告／零错误；Hosting 定向 169 项通过（其中包括作者草稿
  在途切片的 6 项，后者仍未单独交付）。匹配运行发布、完整回归和正式性能仍待完成。

当前本地 SDK／渲染首批定向为 14 项、纯度与 SDK 为 54 项通过；这是开发中版本的
有限回归，尚不构成退场、索引、作者全流程或整个 M07 的验收证据。

### SDK／运行时切片（产品 0.111.2）

接通递归只读准备、纯度分析器、四类源索引与纯文字分派；三例 WordEmphasis、
RepeatEmphasis、ClusterExit 使用生产字形及 Ruby 几何。退场冻结隐藏左极限的基线及父链，
同一源行再次出现时具有独立装载身份，可与前次退出副本共存；退出副本不进入源事件、布局或播放头。
新增 Base／Cluster 仿射与 Base 外观，包材质仍明确拒绝 Base 分区；直接图／晚期输出尚待后续节点。
共享固定时刻基础几何与只读 Ruby 布局；是否产生端到端性能收益仍待实测。

本地证据目录 `.local/script-sdk-m07-20261104/`：`build-04.log` 全解决方案零警告／零错误，
`rendering-focused-03.log` 43 项、`scripting-focused-03.log` 105 项、
`core-admission-02.log` 52 项通过。移除 ref 局部变量门禁的变异使对应负例失败
（`analyzer-ref-mutant.log`：58 通过／1 失败），恢复后包含该例的 105 项通过。
首轮真实 shader 程序与包 profile 混用失败已修复；编译计数、ImmutableArray 值比较断言修正后复验，
初次失败日志保留。前端类型检查通过，但作者切片、生成合同、全量后端、HTTP／UI 和性能验收仍未关闭。

### 作者接口与前端切片（产品 0.111.3，验收中）

SDK2 保存源码只推进最新修订；编译固定到指定保留修订，成功后仍须显式采用。
配置写入同样检查编译历史，不能绕过采用门禁。草稿回执提供包／SDK 身份，前端区分最新、
编译通过、采用和在用修订，提供源码采用撤销／重做；v1 保留原有跟随行为。
工程／作者预览、验证及导出在选择 CPU 时提前报告 `PackageBackendUnsupported`；
失败切换不得更换现有 GPU 固定输入。

`build-05.log` 全后端构建零警告／零错误。首次全量 `backend-full-01.log` 共 6827 项，
6822 通过、5 失败、0 跳过：3 项 Ruby 真实 HTTP 回归缺少匹配发布；2 项新增 SDK2
工程预览回归错误地要求无媒体输入调用 runtime 绑定。原失败证据保留，测试修正及发布后分别复验。
不能把这次全量记录为全部通过；生成合同、真实 HTTP／UI 和加载／seek 证据仍待采集。

新增 SDK2 预览夹具在修正无媒体 profile 预期及嵌套数组值比较后，
`server-sdk2-03.log` 4 项通过；完整会话 wire 比较保持失败前后的全部字段约束。
`frontend-author-03.log` 作者源码／配置／组件 43 项通过，前端类型检查已通过。
其余 3 项发布依赖的 Ruby 回归留待匹配 Server／Worker 后重跑。

`dc6a2e9` 已交付作者切片。新 CLR metadata 生成合同 1.43.0／产品 0.111.3，
源码基线固定该提交，`contract-validation-02.json` 为 112 路径／125 方法／597 schemas、
3434 例（2548 反例）；前端整体同步相同合同。`frontend-build-01.log` 构建通过
（保留既有大 chunk 提示），`frontend-unit-01.log` 44 文件／3937 项通过。
`runner-build-02.log` 采样工具零警告／零错误，实际 HTTP／UI 和正式性能仍待验收。

### M07 实机验收进展

匹配发布源码 `72be32b`（0.111.3），Ruby HTTP 3项复验通过。新作者 HTTP 99请求／730断言，
三例真实 UI 共67检查／9张实际解码显示帧通过；类型及前端3937项复验通过。
三轮15进程采样中，24组可比像素逐字节一致，20组跨进程结构／像素确定性通过；
纯v2远端基础几何采样1次，重复命中0次，加载／编译／准备成本独立登记。
196项检查中ClusterExit重复准备p95=7.8293ms超过已确认的5ms，
其余195项满足；该项需定位修复并按相同场景复测，M07保持未关闭。详见[M07证据](script-sdk-render-m07-evidence.md)。

M08在已稳定SDK接口上开始四块独立编码：SDK／准入、精确发射算法、最终锚点／晚期视觉、
三样例与跨层回归。构建／测试仍由主代理串行，新增能力须执行器接通并通过测试后才开放准入。

### M08 首批集成（产品 0.112.0，验收中）

已接通固定／分段线性精确配额、独立结构化粒子身份、真实源目录、最终文字锚点、
有界事务 memo、解析运动／拖尾、自动与 Custom 晚期图元，以及三个真实 v2 样例。
`Overlay/canvas` 进入生产分派，M09 的 Paths、SVG、Warp 与图输出仍保持原门禁。

`.local/script-sdk-m08-20261104/build-03.log` 全解决方案零警告／零错误；
`core-v2-01.log` 47 项、`rendering-v2-01.log` 19 项、`export-v2-01.log` 7 项通过。
导出包含三个包各自的真实 MP4 与透明 ProRes、解码对照，以及失败时不覆盖原文件。
SDK 首轮 `scripting-v2-01.log` 为 119 通过／19 失败：18 项算法夹具使用了缺少
`sha256:` 前缀的测试资源修订，另 1 项保留了 M07 不支持 Overlay 的预期。
修正后 `scripting-build-04.log` 零警告／零错误，`scripting-v2-02.log` 138 项通过。
初次构建／测试失败记录保留，未将未执行到算法的夹具失败当作算法回归通过。

后续真实四锚点／line fallback、完整依赖断点、cluster 切换及装载／退场边界已补齐，
14 项通过。负例确实暴露 BindingCursors 空集合未拒绝及 Load 吞掉具名诊断的问题，
已取得旧实现 4 项失败、修复后通过的红绿证据。Rendering 全套 1258 项通过的范围
包含前 8 项，不含后加的 6 项；Scripting 全套 442 项通过发生在 Load 修复前。
第三批预算及中途取消恢复已通过，全部边界测试为 18/18；最终构建零警告／零错误，
同一构建串行复验 Scripting 442/442、Rendering 1268/1268、v2 导出 7/7。
合同 1.44.0 已从生产基线 `0f26db2` 重新生成／校验并同步前端；前端构建与
3447 项合同测试通过。匹配 0.112.1 发布完成；粒子三轮九进程 4896 次结构／像素比较
一致。HTTP 实测暴露预算错误 occurrence 丢失，正在修复；相同 M07 文字场景的正式
性能回归有 6 项未达，仍须定位并复测，不能按确定性通过关闭节点。
详见 [M08 证据](script-sdk-render-m08-evidence.md)，以上不构成 M08 通过。

### M09 Warp 像素切片（2026-11-05）

网格投影、Skia 源空间光栅化与最终映射已接通；CPU/Vulkan 各 21 项定向、Rendering 1400 项、
Scripting 475 项通过。源表面密度、相邻字簇顺序、预算、取消和真实 owner 已纳入。
Warp capability 仍关闭，真实包门禁 10 项已确认拒绝；详细失败与复验记录见
[M09 证据](script-sdk-render-m09-evidence.md)。SVG native 与图组能力仍在实施，M09 未关闭。

### M09 Warp 真实包准入（产品 0.112.6）

已开放 token／cluster／mark Warp；真实 v3 包 CPU 参考与 Vulkan 各 10/10，通过覆盖写入、
null 关闭、真实包修订／产物归属、累计预算，以及三级 Warp 后的播放头和粒子锚点。
CPU 生产 v2 仍拒绝。完整证据及未关闭项见 [M09 记录](script-sdk-render-m09-evidence.md)。

### M09 SVG 基础切片（产品 0.112.7）

固定 SVG 依赖及第三方声明、typed vector／atlas SDK、受控 native Picture／mip atlas、固定资源
租约和共享帧字节账已实现。解析前瞻、裁剪孔洞与作者吞预算异常均有先失败后通过的回归。
本切片不开放 SVG capability；实际渲染消费者、组合根和作者包接线继续实施。详细日志与
CPU／Vulkan 证据边界见 [M09 记录](script-sdk-render-m09-evidence.md)。

### M09 SVG 运行及作者编译接线（产品 0.112.8）

SVG 的三项 capability 已正式开放。完整闭包准入、Scene 租约、实际vector/atlas绘制、
整体tint、完整affine、GPU mip采样、预算和真实失败归属已接通；作者包文本的svg/svgResource
可按CAS保存，坏正文在编译前返回资源code/path，修复后仍须编译并显式采用。
完整构建0警告／0错误，Rendering1519、Core1471、Scripting492、RuntimeHosting153、
Backend作者41通过；显式Vulkan图元19／真实包18通过，详见[M09证据](script-sdk-render-m09-evidence.md)。
当前前端仅C#编辑；完整包文本UI、fresh HTTP与工程导出在后续切片验收，M09未关闭。


0.112.8 的匹配Server/Worker及fresh HTTP已完成：314请求／2385断言，SVG vector／atlas与
Base Warp的精确作者修订、13张透明GPU帧、保存重开后3×48帧透明ProRes严格导出均通过。
合同1.45.0已同步，前端3937项单测通过。浏览器完整包编辑与图组仍待，M09不据此关闭。

### M09 图绑定值与动态 Transform（产品 0.112.9）

G1 完整值身份、动态矩阵／输出框、原生逆采样检查及两个渲染入口已接通。
CPU／Vulkan 定向各67项、Rendering复验1544项、Scripting492项通过，完整构建0警告／0错误。
首轮重复键缺陷已修复；完整回归中一次 Roslyn 空引用异常仍单独调查，未宣称已修复。
v2 image.graph 能力仍关闭，G2 作者事务及真实包准入继续实施；详见[M09证据](script-sdk-render-m09-evidence.md)。

### M09 typed controller 图事务（产品 0.112.10）

G2 已接通 TextOutput.BindGraph 并开放 image.graph 的 Family／Line／Mark ColorOnly 路径。
真实包关门12项红例后，CPU参考／Vulkan各12项通过；Scripting501、Core包回归264、
Rendering1556通过，完整构建0警告／0错误。无DLL直接图、材质uniform、组图与owned Below仍待。

### SVG 完整包文本作者 UI

前端已接完整包文本保存、canonical SHA、按role编辑和资源诊断定位。真实浏览器首轮发现
配置页往返后的旧编辑器listener误写兄弟文件，修复后83检查／4张实际显示PNG通过；
类型检查、3944项单测和生产构建通过。后端运行明确使用已发布0.112.8／合同1.45.0，
未把当前后端开发源码冒充同一发布。精确新源schema读取与配置原子迁移仍待，M11未关闭。

### M09 无DLL直接图与材质uniform（产品0.112.11）

G3直接图独立宿主活动与material.uniform已接通。共享预算、每写验证、最终身份归约及Replace
清作用域通过真实包CPU/Vulkan各19项；Scripting509、Rendering1575、RuntimeHosting153通过，
完整构建0警告／0错误。首轮Base拒绝丢TextTarget的回归已由原断言发现并修复。Base与组消费者继续。


### M09 Base 独立包材质（产品0.112.12）

Base 两态填充／描边／uniform 已接通真实中立外观、逐部位 owner、独立捕获和主 Render，
Ruby 保留父族；后继 Family／Line 图可消费实际颜色。真实CPU／显式Vulkan 34项通过，
原像素oracle不等价与坏参数归属的红绿证据已分别保留，见[M09记录](script-sdk-render-m09-evidence.md)。
现有Rendering1575和Scripting509通过；组图、owned Below与匹配HTTP／导出仍待。

### M09 真实组图与 owned Below（产品0.112.13）

组controller／无DLL直接图以双cap和真实source父树准入；完整C/E、partial、共同alpha、父固定
Below／孩子前序兄弟、六类直接owner过滤与Base材质接通。逐输入活动层、实际pass和空组图峰值
门禁均有红绿证据。缩放透明像素误差已修复，默认64表面限制保留；Rendering1697/1697与显式
Vulkan环境组合153/153通过，详见[M09记录](script-sdk-render-m09-evidence.md)。
作者精确源metadata与配置原子采用、匹配HTTP／UI／导出以及M10/M11仍待；M09未关闭。


### 作者精确 metadata 与完整配置采用（产品0.112.14）

精确 retained source 声明接口、可选完整八字段原子采用及前端独立候选已接通；同次配置历史、
未提交原文、迟到读取和采用不确定结果的显式恢复均有回归。完整构建零警告／零错误，Hosting
1586/1586、作者HTTP传输6/6、前端3962/3962通过，失败与修正见[M09证据](script-sdk-render-m09-evidence.md)。
正式合同与匹配HTTP／浏览器／导出验收继续；M09尚未关闭，M10与M11尚未完成。

### M09 作者合同与真实链验收（产品0.112.14／合同1.46.0）

源码 `1e83e0d` 的 clean 匹配 Server／Worker 已发布；113路径／126方法／598 schemas、
3528例（2623反例）的合同生成和校验通过，前端整体同步。作者 fresh HTTP 为431请求／4183断言、
5张透明Vulkan PNG；真实浏览器为140检查／7张实际显示帧，精确源声明、完整配置采用、
schema切换、原文／编辑历史、undo／redo及显式交付故障恢复通过，所属进程排水及凭证扫描完成。
Server此前缺发布的Ruby三案匹配补跑3/3；失败历史与范围详见[M09证据](script-sdk-render-m09-evidence.md)。
图、Base材质和真实组的保存重开／导出矩阵继续验收，不由作者子集关闭M09；M10/M11未完成。

### M09 图与组真实链（产品0.112.15／合同1.46.1）

真实作者编译暴露并修复固定SceneCatalog值的纯度误拒绝，脚本519/519、纯度75/75、完整构建
零警告错误。clean源码48c1e78匹配发布的六场景HTTP为1425请求／10173断言，76张Vulkan PNG，
全部完成工程保存重开及各48帧透明导出；owned Below独立颜色控制、真实父树和Base/Ruby核对通过。
合同与实际JSON留档校验通过，失败探针及红绿证据见[M09记录](script-sdk-render-m09-evidence.md)。
固定字体、普通Sprite affine和BackgroundMedia的v3证据映射仍在收口；M09不提前关闭。

### M09 资源映射与有限关闭（2026-11-05）

当前v3固定包字体的glyph family、CJK／Ruby实际像素、CPU独立参考与Vulkan、重开一致，
普通ImageHandle裁片／完整affine／Tint的三密度独立CPU多边形参考，以及BackgroundMedia
与真实第二owner Below的独立控制、透明fallback及诊断，六项显式GPU环境用例通过。
固定font真实作者HTTP为183请求／1349断言、12张Vulkan PNG，保存重开和48帧透明导出通过；
所属进程排水及凭证扫描完成。最后相邻回归和各切片汇总见[M09记录](script-sdk-render-m09-evidence.md)。
M09仅关闭上述Linux/本机硬件/指定输入功能范围；M10/M11与完整D8仍未完成，性能门槛继续暂缓。

### M10 固定基础片（产品0.112.16）

开始接入Core来源／颜色／工具／profile／artifact不可变值、严格身份和容量校验、精确有理PTS
算术及presentation借用合同。完整solution零警告错误，35项定向通过；实际decoder、轨bytes、
任务、SDK、输入换代及导出尚待，media.features继续具名拒绝。详见[M10记录](script-sdk-render-m10-evidence.md)。
