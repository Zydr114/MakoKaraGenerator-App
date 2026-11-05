# M10 媒体轨实施与验收记录

2026-11-06，`dev/script-sdk-render-upgrade`。**实施中，M10未关闭，media.features能力门禁仍关闭。**
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
原259/259的`presentation-media-full-01.log`在第十七片执行时因同名被覆盖，原始日志未能恢复；
此处保留既有记录结论并明确证据缺失，不以新版本结果替代。新412/412日志已移为
`decoder-media-full-01.log`，后续日志使用decoder前缀。

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

## 第十一片：包查询声明与纯依赖闭包（产品0.112.26）

v2包新增可选媒体目录／export引用，固定SourceNormalized／CanvasPixels与literal／Duration
参数窗口，三种非Available状态显式覆盖当前export所有输出。controller作者分支与材质／图
既有旁路分别验证；strict共享结构读取仅产出不可执行catalog，唯一Definition仍检查全部
包／输出能力。media.features仅known-pending，media.features.cpu仍未知，未开放运行。

纯resolver复用完整SourceMaterialCatalog与唯一参数解析，覆盖启用实例及不可见源override，
固定包revision／export、真实owner path／region、全部typed参数和fallback归属。窗口联合
至多四种，不将batch64误用为目录或owner容量。三个分域摘要以strict UTF8／little-endian
编码完整依赖，避免反向引用input／mapping。声明格式与编码边界见[M10细化§5.1](script-sdk-v2-m10-media-notes.md#51-已实施的包查询声明与纯依赖计划011226)。

`build-query-plan-03.log`完整solution零警告／零错误；`query-plan-focused-02.log`52/52，
含22项新增query／resolver用例和30项v2准入回归。独立BinaryWriter oracle与冻结空计划／
参数向量验证九typed值、默认等价、UTF8排序、owner顺序和依赖身份；覆盖旧v1/v2、非法字段、
三状态、隐藏材质、精确修订、四窗口及65owner／目录等边界。Core全套
`query-plan-core-full-01.log`1562/1562；Scripting全套`query-plan-scripting-full-01.log`519/519。

01构建仅两处测试常量数组触发CA1861，改static readonly；02构建通过。01定向51通过／1失败，
是测试预期ImageGraphException而现有包reader按合同包装为EffectPackageException；改断言
同时核外层InvalidGraphBypass与内层原异常后通过，未改生产拒绝策略。独立静态审查无新增
阻断项，但不代替上述建测。本片不含SDK／provider／实际图统计／HTTP／导出接线，M10未关闭。


## 第十二片：取消归因与正常退出竞态（产品0.112.27）

封套在实际owned drain成功后，才把自身start／consumer linked token归一到原caller；独立
consumer token原样保留。start自身超时具名CapacityUnsupported，已确定的ownership／stall
失败不被迟到caller取消覆盖；drain失败仍优先保留源和预算。正式探针增加两个取消场景的
原caller token精确断言，probe自有pump link在其边界先归一，避免测试混淆链层。

真实首次复验发现正常exit17期间顺序读取的ActiveState仍为active，而/proc进程已消失。
保留全部unit／InvocationID／nonce／cgroup device/inode及非零PID检查，允许已捕获owner的
零PID过渡快照；零PID本身不构成terminal或drained。相邻native修复在stat后cgroup文件消失时
不发布半份ticks/member；pinned kill控制消失时只从同一FD复核events缺失，才作no-op。
其他I/O错误保留，短写具名失败；初始exec gate与最终terminal／empty／三EOF／Unref门未放宽。

红例分别是`native-caller-cancel-red-01.log`3/3、`native-start-cancel-red-01.log`2/2、
`native-exit-snapshot-red-01.log`2/2失败。native两组`native-exit-c-red-01.log`均失败；该红片
仅机械提取原kill I/O helper以运行同一测试，未改变旧行为，不能称为原始字节基线构建。
正式native以-Wall/-Wextra/-Werror构建，`native-exit-c-green-01.log`两组共69场景通过。
最终`build-native-caller-cancel-05.log`完整solution零警告／零错误，
`native-caller-cancel-processes-full-03.log`37/37；04构建／36项是新增零PID不能提前排水
用例前的中间结果。02构建曾因CA2250失败，修为ThrowIfCancellationRequested后通过。

`publish-native-exit-probe-02.log`发布成功；`native-caller-cancel-published-real-02/summary.json`
七场景全部通过，两个取消场景精确caller token、源与buffer借用、OOM、具名stall及四种错误
owner拒绝均保持。`native-caller-cancel-cleanup-02.json`核7个unit消失、含探针的9个PID均无
同startTicks残留；源码／发布DLL／native摘要与34文件凭证模式扫描另行留档，未命中。

失败历史保留：caller-cancel probe首次发布有变量同名编译错误，修名后发布；real-01第二案
因上述active／零PID过渡误报而失败，后续核该轮2个unit、含probe的3个PID全部消失。
尚有独立静态审查提出ActiveState与ControlGroup顺序读取的另一个退出窗口，后续有限修订
继续处理；本轮真实七案未覆盖全部manager交错。以上只证明Linux封套有限功能，不构成
真实媒体decoder、完整Worker、HTTP、浏览器或M10完成；性能不作本轮门槛。


## 第十三片：SDK 媒体值、阶段服务与纯度检查（产品0.112.28）

SDK新增只读输入／目录／profile／query id及Source／Canvas数值快照；Declare只能固定目录，
Text／Visual通过内部绑定入口持本次服务。旧构造签名保留，无绑定明确Input=null／空目录／
采样拒绝。sample锁与Invalidate串行，关闭清callback；前后取消与精确input/query/rect/time
验证保持。跨目录、default、非法矩形／时刻均拒绝，非Available不伪造黑色。见[M10细化§5.2](script-sdk-v2-m10-media-notes.md#52-已实施的-sdk-查询表面与阶段句柄011228)。

纯值不含provider／reader／阶段服务；MediaFeatures列入stage集合，递归数组／泛型／字段／
属性及source helper别名均检查。规则identity由sdk2-purity-5升至6。独立审查发现参数赋值、
结构化分支／pattern丢别名及static local helper误判捕获；真实编译先跑
`sdk-media-flow-red-01.log`，28项中20失败／8通过（18漏拒绝、2误拒绝）。修为结构化别名
汇合、循环有限符号集固定点、try保守汇合和准确自由变量判定；goto具名拒绝。普通明确安全
重赋值仍接受，不扩大BCL权限、不声称任意C#安全沙箱。

root另发现属性模式可把frame.Media／Source.Indexes擦成object；
`sdk-media-property-pattern-red-01.log`三项全部失败，按实际成员／子模式类型传播后通过，
安全Catalog属性模式仍接受。01构建与38项定向通过是初稿表面结果；02构建／66项是追加
上述三例之前的中间结果。最终`build-sdk-media-surface-03.log`完整solution零警告／零错误，
`sdk-media-scripting-full-01.log`588/588，含最终69项新增SDK／真实作者编译正反例；
`sdk-media-rendering-full-01.log`1703/1703；
`sdk-media-runtime-hosting-full-01.log`153/153。

实际Host finally关闭与精确owner投影、provider／factory／锚点、typed图／GPU、Job与运行
换代仍未接通；capability保持known-pending。本片无新HTTP／匹配Server Worker／浏览器／
导出或性能结论，M10/M11未完成。


## 第十四片：固定输入的同步媒体采样器（产品0.112.29）

Rendering.Abstractions新增中立binding／detached sample，Media新增固定input／exact plan
的同步CPU sampler；Source保留七矩中的alpha相关量，Canvas按canonical SAR／rotation／fit
积分并只合成一次底色。直接调用正式ReadTemporalGrid，四个window至多四个精确time槽；
Solid复用正式量化grid。非Available不读、不分配grid，也不隐藏非法query／rect／time。

所有retained grid先从与reader相同的外部runtime ledger预留；构造失败退账，query失败不
发布partial或旧cache key。Dispose关闭admission后等待同步调用排水，只退自身charge，
不关闭borrowed reader。正式validated reader来源、同ledger和外层session／ReadLease排水
仍须后续组合根保证；metadata核对不冒充这些证明。binding提供显式snapshot闭包／画布／
output复核，尚未宣称所有factory已调用。

`media-provider-focused-01.log`15/15，正式builder→codec→OpenValidated→sampler与独立
u16量化、时间权重、cell overlap、七矩合成oracle核对；覆盖Source／Canvas、still、透明
相关矩、pooled variance、fit／crop／letterbox／SAR／rotation、tiny double rect及并发时刻。
ControlledReader只用于读取次数、失败、取消、gate／预算和Dispose，不作为数值证明。

独立审查指出Dispose测试尚未证明Task已实际开始关闭；补为主动观察closing的拒绝后再核
active reader期间预算保留。`media-provider-drain-mutation-red-01.log`在故意提前退charge的
变异上1/1断言失败，恢复后全套通过；它是验收强度变异，不是已存在产品缺陷。
最终`build-media-provider-04.log`完整solution零警告／零错误；
`media-provider-media-full-01.log`350/350，`media-provider-rendering-full-01.log`1703/1703。
01构建两处测试错误为primary constructor捕获及CA2016；02因修订脚本路径错误仍留前者，
修正后03构建及定向通过，04为恢复变异后的最终构建，未抑制规则。

provider尚未接Host／图／GPU／Job，media.features保持关闭；当前HTTP合同与冻结发布不变，
无本版本fresh HTTP／浏览器／导出或性能结论，M10/M11未完成。


## 第十五片：manager 顺序快照与 OOM 历史（产品0.112.30）

已pin的cgroup在ActiveState仍active而稍后ControlGroup变空时保留原路径，继续从同一FD核
inode／events／live PID；不制造terminal或drained。不同非空group拒绝且不污染原ctx，
回填路径在返回前重读InvocationID并要求等于本次起始值；managed仍核固定owner全部令牌。
无pin启动与最终terminal／empty／EOF／Unref门不变。memory.events读失败或读到较低计数时
保持已观察last_oom单调；读取错误保留原best-effort政策，不用计数替代owned排水证明。

`native-snapshot-red-01.log`和`native-oom-history-red-01.log`分别在第一对应语义案例稳定
失败。红片只从旧正式实现机械提取同签名helper，未修正行为，不是原始字节基线构建。
正式-Wall/-Wextra/-Werror构建后，`native-snapshot-green-01.log`四组通过：原proc33／kill36，
新增snapshot58／OOM history30。snapshot测试只覆盖真实group决策helper，OOM测试通过
受控openat/pread/close运行真实counter helper；均不冒充完整D-Bus交错或实际OOM。

`build-native-snapshot-01.log`完整solution零警告／零错误，
`native-snapshot-processes-full-01.log`37/37；`publish-native-snapshot-probe-01.log`正式探针
发布成功，`native-snapshot-real-01/summary.json`真实七场景通过，含正常／非零退出、原caller
取消、borrow取消、stall、父子aggregate OOM及四种owner拒绝。源和预算释放保持。
`native-snapshot-cleanup-01.json`复核7个unit全部not-found、含探针9个PID无同startTicks残留；
`native-snapshot-provenance-01.json`固定源码与发布DLL／native摘要。

实机七场景未穷举manager属性的全部读取交错或实际unit替换；末尾InvocationID复核仍由源审
及有限实机链支持。该片不证明真实presentation decoder、Worker／HTTP或完整M10；性能暂不验收。


## 第十六片：Worker 制品写入授权与 Host 导入（产品0.112.31）

既有reservation新增互斥的Worker写入模式：单次私有grant限定四文件和Core精确ceiling，
写前per-file／combined高水位检查，缩短或失败不退admitted量。scope不能由using退出伪造
drain；可信executor实际排水后显式attest，Host才可按exact scope、文件集合／长度和正式
只读完整codec导入Prepared，继续沿原Publish／ReadLease／恢复协议。普通错误／取消后同scope
不可重导入。该片local writer只是有界文件适配器，不是已完成真实Worker IPC或native监督。

独立审查后增加失败／取消再导入及reserve129、actual63／64／65帧的正式builder／codec测试，
核实际prefix文件全部576×7累计、2／2／3边界、发布实际字节账及64→65帧采样。27项新增与
18项既有store定向合计`store-worker-stage-focused-01.log`45/45。首轮CA1001发现grant保存创建
的disposable writer，改为真实writer所有权交executor，grant仅持非owning drain信号；全部流
成功关闭后才发布该信号，独立复核其构造失败、锁序和attest边界，未抑制规则。

Hosting全套01／02均1632通过／1失败：既有作者编译source案例超过8秒，单独
`store-worker-stage-author-recheck-01.log`10/10。保存待交付源码后恢复精确父提交be133e0，
`store-worker-parent-hosting-baseline-01.log`复现相同超时（1604通过／2失败），并出现既有
音频fixture在保存终态后立即默认reject关闭导致Response空引用。源码确认编译存在进程级
串行gate；测试调整真实编译功能等待为30秒，保留2秒观察及取消／排水短门槛。音频fixture
明确ActiveJobs=wait并逐次核Accepted，遵守现有“终态可读早于Completion排水”合同，未改变
生产保存／关闭语义。父提交完整构建0/0，精确待交付源码随后已核hash还原。

03构建0/0、全套1633/1633是音频fixture修订前结果；最终
`build-store-worker-stage-04.log`完整solution零警告／零错误，
`store-worker-stage-hosting-full-04.log`1633/1633。失败历史保留，不当性能通过。
真正Worker adapter／跨Worker native监督、Job／source capture／恢复ledger和运行输入仍待；
本片不授新HTTP或包执行能力，M10/M11未完成。

## 第十七片：真实 presentation decoder 与有限 Linux 验收（产品0.112.32）

正式 `MediaPresentationDecoder` 接入固定 libav helper、实际 AVPacket opaque→AVFrame 链、
包位置／内容 hash、原生平面及完整 flush；MOV sample 表与 Matroska 真实 block 分别提供
独立时间权威。非尾帧以实际 next PTS 定区间，尾帧必须有真实正 duration，不用帧率或容器
总时长估算。完整 loaded ELF type4/type5 闭包与每 epoch 核验绑定 Tool identity；原生硬封套、
原子 managed 预算、borrow callback、真实 source hold 与 ExactSequentialSource 单次执行／
Dispose join 共同约束寿命。native 构建默认关闭，专用 probe 显式启用，不启动 Server/Worker。

集成后先修正类型推断与分析器问题，以及取消测试 FastClock 误触超时的夹具错误；失败日志
保留。五组真实红测试分别捕获 packet 位置、原 caller token、next PTS、分配失败回滚及
exact source 寿命，见 `presentation-red-01.json`；其中两组为兼容现签名的旧语义变异，
不能说成全部精确旧版本。native frame guard 旧函数及 packet guard 移除分别使 CTest 红，
恢复后 `presentation-native-ctest-01.log` 1/1。红测副本及恢复摘要均保留。

真实验收揭露并修复两项固定 decoder 适配：PNG libav 把 pHYs 的 x/y 密度写入 SAR，
仅该格式取倒数后与结构宽高比严格对账；RGB JPEG 的实际 GBRP→RGB24 组件受结构确认，
允许固定 MJPEG 初始化的 matrix=5，不把它误作实际 YCbCr 分量，仍要求独立色彩权威。
两项均进入 NormalizedPipeline 身份。PNG 先3红／2正，RGB先1红／5正，修复后解释层14/14。
没有放宽其他视频标签或 RGB 缺权威规则。

正式探针 `M10PresentationProbe` 的 caller/linked token 断言、PID I/O fail-open 及 summary
独立摘要检查已修订；在途回调抛实际 linked token，最终异常须保留原 caller token。
实际 owned kill 期间 pixels/source/budget 保持借用，释放回调并完整排水后才归零。
逐 interval 独立编码64B并累计摘要，未借产品 MFT1 编码器充当 oracle。

最终夹具为 `presentation-jpeg-fixtures-04/`，包含 PNG8/16、SAR2:1+Exif90、六帧 H264 B-frame、
VP9 Matroska DefaultDuration 与尾 BlockDuration，以及 baseline/progressive 各420/422/444/RGB/gray
十种 JPEG。原 VP8 容器／码流矩阵冲突和 H264 缺显式 tags 均如实拒绝；生成器改用明确一致
的 VP9、H264 编码参数并逐帧核标签，未用 override 绕过矛盾。

- `presentation-probe-publish-09.log` 发布成功；`presentation-jpeg-discovery-04/` 仅为实际闭包发现。
- `presentation-jpeg-verification-05/summary.json`：16正例／32完整及在途取消场景通过；
  精确PTS、像素、区间、tail、完整摘要与owned排水均核验。前四轮失败证据保留。
- `presentation-jpeg-rejection-01/summary.json`：16结构／请求负例及3闭包场景通过。闭包分别为
  过期helper hash、漏列实际libavcodec、真实首帧后有界metadata故障注入；后者不冒称物理late dlopen。
- `build-decoder-final-01.log`：完整solution零警告／零错误；`decoder-media-full-02.log`423/423，
  `decoder-processes-full-01.log`37/37。包含新增73项Media测试。
- `presentation-cleanup-01.json`：覆盖失败轮和H264诊断轮，237个精确PID/startTicks无残留，
  225个unit全部not-found；`decoder-provenance-01.json`固定源码、发布及验收摘要，42源文件凭证扫描无匹配。

JPEG canonical 全通道误差保持2e-6：native YUV/GBR/gray参考平面经独立Python颜色运算，
但codec参考与实际helper共享FFmpeg祖先，不独立证明IDCT。结构负例不证明真实复杂ICC/APNG内容，
本矩阵也不覆盖所有格式、OOM、动态源替换或性能。正确owner的排水已验证，wrong-owner token
专门场景仍归既有EnvelopeProbe。本片没有接通store/Worker/HTTP/GPU完整媒体路径，能力门禁
继续关闭，M10/M11未完成。
