# M10 媒体轨实施与验收记录

2026-11-05，`dev/script-sdk-render-upgrade`。**实施中，M10未关闭，media.features能力门禁仍关闭。**
本轮功能优先，性能门槛不参与验收；身份、预算、数值、像素、取消、生命周期及真实作者链仍须验证。
证据根为`.local/script-sdk-m10-20261105/`，各失败轮保留。

## 第一片：固定来源、精确时间与容量（产品0.112.16）

Core新增媒体来源／颜色／工具／profile／制品只读值与边界验证；Rendering.Abstractions新增
只读presentation frame、interval、index、callback借用合同。构造不授予信任：接受边界重算
嵌套SHA和工具／颜色绑定，不因外层重新hash放行矛盾来源；未接HTTP、Worker任务或SDK。

- 复合身份使用domain分离、little-endian固定顺序、hash原始32B和严格UTF8。工具清单以
  UTF8 byte顺序编码，每份最多1024项、单logical ID≤256B、总ID≤64KiB，分配有界snapshot
  和排序scratch前分别核数量／文本；不把路径、mtime或任意JSON作为身份。
- 原始PTS保留signed64 ticks和正约分int32 timebase，用BigInteger精确比较与数学ceil转微秒，
  每个运算中间量≤192bit，末结果checked long；不会将相邻巨大ticks经double合并。
- 固定SDR组合含RGB8/16静态图、RGB8视频、BT709 planar YUV8/10及受控JFIF BT601/sRGB
  图片解释；非方SAR正约分保留，仅quarter-turn交换canonical尺寸。颜色authority实际来源、
  解码工具加载闭包和真实pixel／PTS关联仍由后续inspector负责，当前结构校验不证明它们。
- MFT1布局单一真源：256B头、64B interval、576格×16B／帧、64帧块及32272B prefix。
  54000帧制品528390096B，spool＋candidate1056779936B；分配／落盘前按全部section准入。
  视频durationUs必须不少于frameCount；完整interval、prefix和实际bytes留给codec验证。
- decode账为所有native强制限额＋受管活跃buffer reservation≤256MiB。16M像素RGBA32F
  本身已经占满该值，加1B即拒绝；尺寸上限不代表所有更小输入均可运行。无可证明native
  封套返回CapacityUnsupported，本片只实现算术准入，不声称已强制整个Worker物理RSS。

`build-foundation-03.log`完整solution0警告／0错误；`foundation-focused-01.log`为35/35。
相邻Core全套`foundation-core-full-01.log`为1506/1506，含媒体能力仍拒绝的既有门禁。
包括9个独立规范身份向量、UTF8补充平面顺序、错绑定／非法默认值、工具文本容量、signed ceil、
最大时间乘积、颜色组合、SAR、制品精确布局与内存准入。前两轮编译失败分别是清单方法尚未
纳入身份服务接口触发CA1822，以及测试expected顺序／构造表达式丢弃的analyzer诊断；修正
实际接口／测试写法后构建通过，没有禁止或抑制分析器。这些编译失败不记作红绿业务测试。

后续顺序：七量面积kernel与MFT1流式codec；受控原生封套／同链PTS解码与SDR颜色／SAR；
任务store与租约；SDK／typed GPU查询；固定输入换代与作者链；导出等待／钉住轨；T01样例。
静态合同、匹配发布、fresh HTTP、浏览器和性能分别登记，当前没有据此宣称媒体能力已可用。

## 第二片：七原量面积内核（产品0.112.17）

`MediaMomentKernel`从真实canonical RGBA32F像素逐cell积分到32×18七量，整数有理重叠
权重明确覆盖全部源像素；独立计算Yp²／aYp／a²，不以均值平方冒充二阶量。uint16量化
使用固定half-up，非法／非预乘／隐藏透明RGB／非有限／负零先拒绝；不做隐式clamp。
单帧贡献上界`(W+31)×(H+17)`，数值scratch固定224B，原像素及destination仍由owner另计。

SourceNormalized分数矩形按七量网格的分段常量面积积分。对每个量同时补偿累计direct
与complement，再用较近端点的等价表达式求均值，使全0／全1在double面积舍入下精确保留，
没有通过容差截断掩盖错误。grid外坏值仍拒绝；取消原样传播、partial destination不可发布。
这是相对于固定网格近似的积分，不宣称恢复任意子矩形的原始像素细节。

`build-moment-kernel-02.log`完整solution0警告／0错误，`moment-kernel-focused-01.log`为
23/23；相邻Media全套`moment-kernel-media-full-01.log`为232/232。独立BigInteger有理oracle覆盖1×1、2×1、17×13、37×23的全部量化分量；另有
相同均值的棋盘／纯灰二阶差异、透明混合、分数／极小矩形、全1端点反例、预算与取消回归。
首轮构建的可取消Solid overload未列入服务接口，触发CA1822；补完整可取消接口而非关闭
分析器后通过。尚无decoder、轨codec、任务、SDK、GPU或作者运行证据。

## 第三片：SDR 转换与正交／SAR 几何（产品0.112.18）

`MediaRasterGeometry`保持encoded像素网格，先镜像再quarter-turn精确重排；SAR正约分只改变
显示度量，720×576的16:15显示为768×576、旋转90°后576×768，无整数宽度舍入或隐式
重采样。`BackgroundPlacement`新增double显示入口，现有int入口完整方法保持原计算顺序
与最终float，未知fit的旧行为不变；新入口严格拒绝未知fit。contain／cover／original及
canonical source↔canvas映射共用新入口。

`MediaSdrFrameConverter`从明确packed/planar原生分量转换为linear-premul RGBA32F，
验证完整固定来源、位深、正stride／padding、10bit高位、输出长度与输入输出不重叠。
RGB8/16、BT709 full/limited YUV8/10 420/422/444和明确JPEG601/full/sRGB按固定样本中心、
bilinear chroma及EOTF执行；唯一规范saturate在YUV矩阵后，不掩盖非法标签或alpha。
颜色用double运算、每通道一次float写入，alpha0输出正零；缓冲所有权及native强制封套
仍由调用者控制，不把本片容量算术当真实解码器已受限。

`build-presentation-color-01.log`完整solution0警告／0错误。几何及既有placement定向
`presentation-geometry-focused-01.log`为20/20；`presentation-color-focused-01.log`为27/27。
独立8方向像素表、精确SAR／三fit、legacy矩形，以及独立Kr/Kb矩阵、样本中心tent权重和
Exp/Log EOTF oracle均通过；还覆盖奇数尺寸／padding、透明／opaque、颜色分段边界、
短plane／负stride／高位／额外plane／别名／取消和容量等号／+1。
同构建Core全套`presentation-core-full-01.log`为1520/1520，Media全套
`presentation-media-full-01.log`为259/259。
这些是受管转换功能证据，没有据此声称真实PNG/JPEG/video decoder、媒体背景呈现或GPU已接通。

## 第四片：Canvas 面积查询与最终底色合成（产品0.112.19）

`MediaCanvasMomentQuery`以double反映射当前placement的媒体交集，按完整查询面积平均七原量，
合法留白贡献透明源；随后`MediaMomentComposer`统一source-over底色，再计算预乘亮度二阶量
与方差。先统计再求方差，保留aYp和a²关联；唯一负方差归零界为13δ＋1e-12，不扩展到
颜色、网格或GPU误差。现有Source float入口精确提升，复用同一个内部double积分内核。
实际SDK的Rect2坐标为double，本片不要求作者先降为float；SDK服务仍未接线。

真实定向回归先暴露旋转非方SAR显示右边缘被反映射为1.0000000000000002；新增独立
端点回归`canvas-placement-endpoints-red-01.log`为3/3失败。明确边界对应修复后，第二轮
发现未裁切源经乘除舍入仍可能宽于单位区间，`canvas-placement-endpoints-red-02.log`为
3通过／1失败。现在完整未裁切源保持原extent，正反映射的声明边缘精确对应；非边缘点
仍用原仿射式，无容差clamp，legacy int placement方法未改。像素oracle及容差未放宽。

`build-canvas-moments-04.log`完整solution0警告／0错误；最终几何／legacy定向
`canvas-placement-focused-01.log`为24/24；Media全套`canvas-media-full-01.log`为275/275，
含16项Canvas／合成独立像素oracle及原Source回归；同构建Core全套
`canvas-core-full-01.log`为1524/1524。首轮定向为32通过／1失败，上述几何修复后通过；
首轮构建因测试数组切片触发CA1832，改AsSpan后通过，未关闭分析器。
本片仍不包含GPU统计纹理、实际背景绘制、SDK、任务或HTTP验收。

## 第五片：MFT1 流式编解码与时间查询（产品0.112.20）

`MediaFeatureTrackCodec`接严格F/I串行builder、有界三section spool、流式writer及完整校验
reader；builder只留一个待配对量化grid，不留借用像素。所有数组在owner账准入后分配，
交接write source前同时计入两份活跃buffer。spool＋candidate磁盘账由调用方预留；本片
只定义接缝，尚未实现生产文件store／Job发布，writer返回身份不等于已经提交制品。

reader先校验完整hash、唯一布局、保留字段、全部raw rational与canonical区间、逐帧
stream timebase及重算prefix，再接管只读stream。重新hash不能隐藏错误布局或中途
timebase变化；最终可信endpoint允许另一约分timebase。输入外部不可变及授权lease由
运行owner保证；CanWrite=false本身不是外部文件不可变证明。

查询按64帧prefix差及两侧最多各64帧做整数累计，零窗按半开区间选帧，跨零保持首帧，
freeze尾部保持末帧，loop整圈相乘；Int128先计算窗口起点，接受long两端而不溢出。
静态图没有虚构duration。取消后可重查；reader Dispose与在途查询串行排水，失败open
不夺走caller stream，成功open才转所有权。

`build-track-codec-03.log`完整solution0警告／0错误；`track-codec-focused-02.log`为50/50，
同构建Media全套`track-codec-media-full-01.log`为325/325。独立手写wire／SHA／prefix及
逐微秒oracle覆盖65帧非均匀duration的builder→writer→reader全字节、64帧边界与尾块、
long极值、短源两百万圈有界I/O、512帧prefix路径、重新hash的坏结构与生命周期负例。
首轮48通过／2失败是video夹具只预留100000B，少于一帧spool＋candidate的147904B；
修为200000B后触达timebase／取消目标并通过，生产准入未放宽。首轮构建的两处CA1859
仅在测试变量改用已知具体类型后解决，未抑制分析器。尚无真实decoder／SDK／GPU／HTTP证明。

## 第六片：共享工作集原子账（产品0.112.21）

`MediaWorkingSetBudget`实现同一owner下的原子准入／释放和固定七类一致快照，保存live／peak
字节与reservation数；扣剩余后准入避免long加法溢出，handle分配先于记账，重复或并发
Dispose只释放一次。非法参数属于调用合同错误，合法请求超额具名CapacityExceeded；
owner仍须先排水再Dispose，GC不释放额度，本账也不是实际RSS测量或原生内存限制。

`build-working-budget-01.log`完整solution0警告／0错误；`working-budget-and-codec-01.log`
为57/57，其中7项新预算测试覆盖32并发争抢8槽、32并发释放不影响其他charge、并发
churn快照守恒、容量等号／+1／longmax、失败不改账及复用，另50项codec回归通过。
本片提供生产ledger实现，真实decoder／reader组合根仍待后续接入。

## 第七片：同步效果求值的有界轨读取（产品0.112.22）

reader新增同步`ReadTemporalGrid`，与异步入口共用整数时间算法、backing及gate。
同步模式直接执行有界Stream.Read；内部ValueTask只在确认已完成后取结果，不等待异步
I/O或continuation。stream读取块间与gate等待均检查取消；关闭等待两种在途查询，
不会为同步作者接口另开任务、decoder或一份全轨内存。

`build-sync-track-reader-03.log`完整solution0警告／0错误；
`sync-track-reader-focused-01.log`为60/60，Media全套
`sync-track-reader-media-full-01.log`为335/335。原独立逐微秒oracle同时验证同步和异步
结果；新增禁止异步I/O的stream验证乱序／重复／long极值，31B短读取消后恢复，实际
生产budget与Dispose等待在途同步读，全部通过。前两轮构建均为CancellationToken参数
顺序的CA1068失败（第二轮修订命令路径错误，未生效），修为token末参数后通过，未抑制规则。
本片仍是受信Media读取接缝；实际SDK、缓存淘汰和作者链尚待接通。

## 第八片：固定媒体输入与映射身份（产品0.112.23）

新增不可变Mapping、纯色源和完整输入context；Pending、AvailableFile、AvailableSolid、
Unavailable、NoMedia五个封闭分支分别校验所需字段。Pending／失败绑定固定请求身份；
Available按内容复用，不带准备attempt；失败码和稳定failure身份进入摘要，受限人类诊断
不进入。透明输出只接受显式NoMedia，不持文件源；普通纯色使用canonical mapping，避免
重复合成底色。所有接收边界重算nested profile／source／mapping与最终input摘要。

实际SDK Rect2已使用double，因此尚未开放的Query统一更正为
`media-grid-area-binary64-v1`，拒绝旧f32标识，并更新独立profile／source摘要向量。
既有float Source helper精确提升到同一double算法；GPU RGBA32F仍需单独验证，未开放
第二套CPU能力。context先固定查询目录和依赖digest，其解析、运行快照及lease组合待后片。

`build-fixed-input-03.log`完整solution0警告／0错误；`fixed-input-focused-01.log`31/31，
包含16项新增输入测试及15项既有身份回归；`fixed-input-core-full-01.log`1540/1540，
`fixed-input-media-full-01.log`335/335。独立BinaryWriter字节oracle和冻结向量覆盖五分支、
double单bit、查询／依赖／映射变化、错绑定、重试、纯色、透明与反射模拟的伪造nested摘要。
首次集成命令目录错误，未修改产品且只构建旧基线（01日志）；02构建仅因测试常量数组
CA1861失败，改为static readonly后03通过，未抑制分析器。尚无本版本HTTP／GPU／作者链证明。

## 第九片：原生进程硬封套与本机实证（产品0.112.24）

Processes新增不依赖Core的预算子账、一次性native lease、四管道owned session、同PID
exec gate及Linux systemd backend。先固定MemoryMax／swap=0／OOM group，再核manager、
kernel、InvocationID、PID/startTicks及固定cgroup inode；取消只写所固定cgroup的kill。
consumer借用仍在途时先停止native，再等待callback和管道排水；无法证明排水则quarantine
保留源和预算。成功排水保留原consumer异常或取消token。实际Media runner、decoder及
生产Worker接线留后片，本片不宣称完整媒体分析可用。

`build-native-envelope-04.log`完整solution0警告／0错误；Processes全套27/27，其中22项
新封套／预算用例；Media全套335/335。移除single-use claim与active等待的变异使两项
定向回归2/2失败（`native-envelope-regressions-red-01.log`），恢复后进入上述全绿结果。
前两次构建分别发现ValueTask同步取值／取消检查和fake fixture清理规则问题，已修复，
未抑制分析器。01 focused的21/21是增加active竞态测试前的中间结果。

正式可复用探针位于`backend/tools/backend-script-render-upgrade/M10EnvelopeProbe/`，
引用正式Processes源码与native target；`publish-native-envelope-probe-01.log`发布成功。
`native-envelope-published-real-01/summary.json`七场景全部通过：

- 放行前gate已完成exec且三输出静默；放行后同PID/startTicks，真实stdin/stdout/stderr/fd3。
- 非零17自然退出；caller取消；2秒pipe stall具名超时。
- 64MiB限额内父子分别触页40MiB，真实合计OOM，确认manager/kernel记录后才计CapacityExceeded。
- borrow在途时独立kill，源和buffer保留至callback返回；错误InvocationID／PID／ticks／inode
  四令牌均拒绝且原进程仍存活。

每项终态、空／已移除cgroup、EOF、Unref与源预算释放均有事件记录。正式发布探针的
7个owned unit均已消失，8个观察到的PID无同startTicks残留；见
`native-envelope-published-cleanup-01.json`。source／DLL／native摘要见
`native-envelope-provenance-01.json`；新源与有限证据43文件扫描无凭证模式命中。

真实失败保留：real-01因MSBuild在SDK完成前求值TargetDir而把工具复制到错误位置，未启动
unit；target内延迟求值修复。real-02首场景通过后暴露activating已报告PID但gate尚未exec
的竞态；等待active修复，real-03七场景通过，再移入正式工具并完成上述发布复验。
AddRef保留结果后以inactive-or-failed收集，旧失败探针的单个owned失败unit在核验身份与
无进程后精确reset；没有批量kill。以上均是Linux x64本机有限功能证据，不是decoder／
颜色PTS、Server/Worker匹配发布、HTTP、跨平台、整个Worker RSS或性能验收。

## 第十片：有界制品存储与真实 reader 寿命（产品0.112.25）

Hosting新增内部MFT1 store，按完整owner／Job／source／artifact引用授权，不因内容命中跨owner
授权。预留Core计算的spool＋candidate上界，单制品512MiB、单准备1GiB、总量2GiB、1024项；
写入、seek和扩容均受限。producer从交出scratch前持有至builder／write source排水，关闭和
清理等待在途I/O。Prepare流式写入后FlushAsync及同一writer的Flush(true)，关闭写句柄，再
完整只读codec校验。Publish要求producer／prepare／stream全部排水，在调用方Job提交门内
执行同文件系统目录rename；失败残留继续计账，不宣称目录fsync或断电事务保证。

读取先pin，再完整校验；lease关闭等待真实codec查询结束才退pin，过期条目直到最后pin释放
才删除并退磁盘账。重启只登记有界文件，必须由可信Job ledger的精确引用恢复，不能凭目录
发现宣告Available。清理中断产生的空bundle标Missing，拒绝读取但允许后续prune，不阻断
健康制品；额外文件、链接、错误身份和超额条目仍拒绝。

独立审查发现Recover在异步校验期间被Prune过期后仍返回true；真实codec暂停验证测试
`artifact-store-recovery-red-01.log`为1/1失败。锁内复查Expired后通过。另一个真实文件回归
`artifact-store-missing-bundle-red-01.log`为1/1失败，空bundle登记Missing后通过。查询寿命
测试暂停生产codec底层读，证实query／dispose／prune并发时文件和reader预算保留至读完。

`build-artifact-store-04.log`完整solution零警告／零错误；最终定向
`artifact-store-focused-02.log`20/20，Hosting全套`artifact-store-hosting-full-01.log`1606/1606。
同构建Media全套`artifact-store-media-full-01.log`335/335。
首两次构建分别修正DisposeAsync基类调用／具体类型和测试参数顺序／常量数组分析器问题，
未抑制规则。03构建与18项定向是追加两项真实缺陷回归前的中间结果。本片尚未接Job执行器、
Worker授权、恢复ledger或运行输入；内部测试不代表新版本fresh HTTP，media.features仍关闭。
